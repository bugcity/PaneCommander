using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.Views;
using PaneCommander.Infrastructure;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ShellContextMenuTests
{
    [TestMethod]
    public void NativeMenusContainShellCommandsForFilesDirectoriesMultipleAndBackground() => Sta(() =>
    {
        var root = Root();
        try
        {
            var file = Path.Combine(root,"sample.txt"); File.WriteAllText(file,"fixture"); var second=Path.Combine(root,"second.txt"); File.WriteAllText(second,"two"); var folder=Directory.CreateDirectory(Path.Combine(root,"directory")).FullName;
            var evidence=new List<object>();
            foreach(var paths in new[]{new[]{file},new[]{folder},new[]{file,second},Array.Empty<string>()})
            {
                using var menu=new ShellContextMenu(root,paths,IntPtr.Zero); var commands=menu.Commands();
                Assert.IsTrue(commands.Count>0); if(paths.Length>0)Assert.IsTrue(commands.Any(c=>c.Verb=="properties"));
                evidence.Add(new{Paths=paths.Select(Path.GetFileName),Commands=commands.Select(c=>new{c.Id,c.Text,c.Verb})});
            }
            var output=Path.Combine(Repository(),"docs/verification/shell-context");Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"native-commands.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
        }
        finally { Cleanup(root); }
    });
    [TestMethod]
    [Ignore("Unsafe interactive clipboard backup/restore: prior CLIPBRD_E_CANT_CLOSE is unresolved. Run only after a verified isolated test environment is available; not a pass.")]
    public void NativeCopyAndCutPublishWindowsFileClipboardAndRestorePriorClipboard() => Sta(() =>
    {
        var root=Root(); IntPtr previous=IntPtr.Zero; var ole=false; var captured=false;
        try
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));ole=true;
            var hr=OleGetClipboard(out previous);
            if(hr==unchecked((int)0x800401d0)) { Assert.Inconclusive("CLIPBRD_E_CANT_OPEN: 元のクリップボードを保存できないため、コピー・切り取りの実行を中止しました。未検証です。");return; }
            Marshal.ThrowExceptionForHR(hr);captured=true;
            var file=Path.Combine(root,"clipboard.txt");File.WriteAllText(file,"isolated");
            using var menu=new ShellContextMenu(root,[file],IntPtr.Zero);
            foreach(var verb in new[]{"copy","cut"})
            {
                var command=menu.Commands().Single(c=>c.Verb==verb);menu.Invoke(command.Id,0,0);
                CollectionAssert.AreEqual(new[]{file},System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray());
                var data=System.Windows.Clipboard.GetDataObject()!; Assert.IsTrue(data.GetDataPresent("Preferred DropEffect",false));
                using var stream=(MemoryStream)data.GetData("Preferred DropEffect",false)!;var bytes=stream.ToArray();Assert.AreEqual(verb=="copy"?1:2,BitConverter.ToInt32(bytes));
            }
            Assert.AreEqual("isolated",File.ReadAllText(file));
        }
        finally
        {
            try { if(captured)Marshal.ThrowExceptionForHR(OleSetClipboard(previous)); }
            finally{if(previous!=IntPtr.Zero)Marshal.Release(previous);if(ole)OleUninitialize();Cleanup(root);}
        }
    });
    [TestMethod]
    public void AuthorizedBackgroundPasteCopiesAndMovesFixtureWithoutClipboardRestoration() => Sta(() =>
    {
        if(Environment.GetEnvironmentVariable("PANECOMMANDER_ALLOW_TEST_CLIPBOARD_OVERWRITE")!="1")
        { Assert.Inconclusive("Requires explicit authorization to overwrite this session clipboard; no backup or restoration is attempted.");return; }
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        var root=Root();
        System.Windows.Interop.HwndSource? clipboardOwner=null;
        try
        {
            // Match the product's valid owner HWND; explicit 0 preserves the historical diagnostic control.
            if(Environment.GetEnvironmentVariable("PANECOMMANDER_TEST_CLIPBOARD_OWNER")!="0")
                clipboardOwner=new(new System.Windows.Interop.HwndSourceParameters("PaneCommander clipboard fixture owner"){Width=1,Height=1,PositionX=-20000,PositionY=-20000,WindowStyle=0});
            var ownerHandle=clipboardOwner?.Handle??IntPtr.Zero;
            Console.WriteLine($"Diagnostic context menu owner=0x{ownerHandle:X}");
            var source=Path.Combine(root,"source.txt");File.WriteAllText(source,"authorized paste fixture");
            var copy=Directory.CreateDirectory(Path.Combine(root,"copy")).FullName;
            var move=Directory.CreateDirectory(Path.Combine(root,"move")).FullName;
            foreach(var action in new[]{(Verb:"copy",Target:copy),(Verb:"cut",Target:move)})
            {
                void ClipboardState(string phase)
                {
                    var owner=GetClipboardOwner();GetWindowThreadProcessId(owner,out var ownerPid);
                    Console.WriteLine($"{action.Verb} {phase}: sequence={GetClipboardSequenceNumber()}, owner=0x{owner:X}, ownerPid={ownerPid}, testPid={Environment.ProcessId}, thread={Environment.CurrentManagedThreadId}");
                }
                ClipboardState("before invoke");
                var synchronousInvoke=Environment.GetEnvironmentVariable("PANECOMMANDER_TEST_CLIPBOARD_SYNC_INVOKE")=="1";
                Console.WriteLine($"Diagnostic InvokeCommand NOASYNC={synchronousInvoke}");
                using(var item=new ShellContextMenu(root,[source],ownerHandle))
                    item.Invoke(item.Commands().Single(c=>c.Verb==action.Verb).Id,0,0,waitForCompletion:synchronousInvoke);
                ClipboardState("after invoke/dispose");
                var actualPaths=System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
                Console.WriteLine("FileDrop: "+string.Join(" | ",actualPaths));
                CollectionAssert.AreEqual(new[]{source},actualPaths);
                using(var background=new ShellContextMenu(action.Target,[],ownerHandle))
                {
                    var paste=background.Commands().Single(c=>c.Verb=="paste");Assert.IsTrue(background.IsEnabled(paste.Id));
                    using var transfer=new ShellPasteData();
                    Console.WriteLine($"Shell clipboard {action.Verb}: preferred effect=0x{transfer.PreferredEffect:X8}, operation={transfer.Kind}");
                    Assert.AreEqual(action.Verb=="copy"?FileOperationKind.Copy:FileOperationKind.Move,transfer.Kind);
                    var result=ShellFileOperationService.ForIsolatedTest(root).ExecuteAsync(new(transfer.Kind,[source],action.Target),IntPtr.Zero).GetAwaiter().GetResult();
                    Assert.IsFalse(result.Aborted);ClipboardState("before completion notification");
                    var completedPath=Path.Combine(action.Target,"source.txt");
                    Console.WriteLine($"Before notification: sourceExists={File.Exists(source)}, destinationExists={File.Exists(completedPath)}, destinationHash={(File.Exists(completedPath)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(completedPath))):"absent")}");
                    try { transfer.ReportCompleted(); }
                    catch(COMException ex) { Console.WriteLine($"Completion notification HRESULT=0x{ex.HResult:X8}, format={ex.Data["ShellCompletionFormat"]}");ClipboardState("failed completion notification");throw; }
                    Assert.IsTrue(File.Exists(Path.Combine(action.Target,"source.txt")),"Shell operation completion must precede the list refresh");
                    Assert.AreEqual("authorized paste fixture",File.ReadAllText(Path.Combine(action.Target,"source.txt")));
                }
                Assert.AreEqual(action.Verb=="copy",File.Exists(source));
            }
            Assert.AreEqual("authorized paste fixture",File.ReadAllText(Path.Combine(copy,"source.txt")));
        }
        finally {try{Cleanup(root);}finally{try{clipboardOwner?.Dispose();}finally{OleUninitialize();}}}
    });
    [TestMethod]
    public void RightClickSelectionPreservesSelectedGroupAndReplacesUnselected() => Sta(() =>
    {
        Assert.IsTrue(ShellMenuBehavior.IsContextMenuKey(System.Windows.Input.Key.F10,System.Windows.Input.ModifierKeys.Shift));
        Assert.IsTrue(ShellMenuBehavior.IsContextMenuKey(System.Windows.Input.Key.Apps,System.Windows.Input.ModifierKeys.None));
        Assert.IsFalse(ShellMenuBehavior.IsContextMenuKey(System.Windows.Input.Key.F10,System.Windows.Input.ModifierKeys.None));
        Assert.IsFalse(ShellMenuBehavior.IsContextMenuKey(System.Windows.Input.Key.F10,System.Windows.Input.ModifierKeys.Control));
        var list=new System.Windows.Controls.ListView{SelectionMode=System.Windows.Controls.SelectionMode.Extended};
        var one=new FileEntry("one","C:\\one",false,1,DateTime.Now); var two=one with{Name="two",FullPath="C:\\two"}; var three=one with{Name="three",FullPath="C:\\three"};
        list.ItemsSource=new[]{one,two,three}; list.SelectedItems.Add(one);list.SelectedItems.Add(two);
        ShellMenuBehavior.SelectTarget(list,two); Assert.AreEqual(2,list.SelectedItems.Count);
        ShellMenuBehavior.SelectTarget(list,null); Assert.AreEqual(2,list.SelectedItems.Count);
        ShellMenuBehavior.SelectTarget(list,three); Assert.AreEqual(1,list.SelectedItems.Count);Assert.AreSame(three,list.SelectedItem);Assert.IsFalse(ShellMenuBehavior.Selecting);
    });
    [TestMethod]
    public void ShellSafetyAllowsExtensionsAndPreservesLinkClipboardGuards()
    {
        var root=Root();
        try
        {
            var target=Directory.CreateDirectory(Path.Combine(root,"referent")).FullName; var sentinel=Path.Combine(target,"keep.txt");File.WriteAllText(sentinel,"protected");
            var tree=Directory.CreateDirectory(Path.Combine(root,"tree")).FullName;var link=Path.Combine(tree,"junction");
            var process=Process.Start(new ProcessStartInfo("cmd.exe",$"/d /c mklink /J \"{link}\" \"{target}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;process.WaitForExit();Assert.AreEqual(0,process.ExitCode);
            foreach(var verb in new[]{"copy","cut"})
            {
                Assert.IsFalse(ShellMenuSafety.AllowedForDirectLink(verb));
                Assert.ThrowsExactly<IOException>(()=>ShellMenuSafety.CheckInvocation(verb,root,[tree],null));
                Assert.ThrowsExactly<IOException>(()=>ShellMenuSafety.CheckInvocation(verb,root,[link],null));
            }
            foreach (var verb in new[] { "compress", "7-zip", "unidentified-extension", "" })
            {
                Assert.IsTrue(ShellMenuSafety.AllowedForDirectLink(verb));
                ShellMenuSafety.CheckInvocation(verb, root, [tree], null);
                ShellMenuSafety.CheckInvocation(verb, root, [link], null);
            }
            Assert.ThrowsExactly<IOException>(()=>ShellMenuSafety.CheckInvocation("paste",target,[],[tree]));
            Assert.ThrowsExactly<IOException>(()=>ShellMenuSafety.CheckInvocation("paste",target,[],null));
            foreach(var verb in new[]{"open","properties","delete","rename"}) {Assert.IsTrue(ShellMenuSafety.AllowedForDirectLink(verb));ShellMenuSafety.CheckInvocation(verb,root,[link],null);}
            Assert.AreEqual("protected",File.ReadAllText(sentinel));Assert.IsNotNull(LinkObjectOperations.Read(link));
        }
        finally{Cleanup(root);}
    }
    private static void Sta(Action action)
    {
        Exception? failure=null; var thread=new Thread(()=>{try{action();}catch(Exception ex){failure=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}});thread.SetApartmentState(ApartmentState.STA);thread.Start(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)),"STA test timeout"); if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static string Root(){var root=Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static string Repository(){var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"PaneCommander.sln")))directory=directory.Parent;return directory?.FullName??throw new IOException("Repository not found");}
    private static void Cleanup(string root){Assert.AreEqual(Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests"),Path.GetDirectoryName(root));Assert.IsTrue(Guid.TryParse(Path.GetFileName(root),out _));LinkObjectOperations.Delete(root,default);}
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern int OleGetClipboard(out IntPtr data);
    [DllImport("ole32.dll")]private static extern int OleSetClipboard(IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
}
