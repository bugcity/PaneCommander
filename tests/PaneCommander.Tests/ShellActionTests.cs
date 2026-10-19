using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ShellActionTests
{
    [TestMethod]
    public async Task OldHierarchySessionLoadsAndResavesWithoutModeOrLosingOtherState()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-detail-migration-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.json");
            var settingsPath=Path.Combine(root,"settings.json"); var favoritesPath=Path.Combine(root,"favorites.json");
            var legacySettings=new AppearanceSettings { ShowFileAge=false,ShowModified=false,FileAgeWidth=25,FontFamily="Meiryo" };
            await new JsonSettingsStore(settingsPath).SaveAsync(legacySettings);
            await new JsonFavoritesStore(favoritesPath).SaveAsync(new() { Items=[new(Guid.NewGuid(),"kept favorite",root)] });
            var settingsBefore=await File.ReadAllTextAsync(settingsPath); var favoritesBefore=await File.ReadAllTextAsync(favoritesPath);
            var saved = new SessionDocument
            {
                Left = new() { Tabs = [new(root), new(Path.Combine(root, "child"), "Size", true)], SelectedIndex = 1 },
                Right = new() { Tabs = [new(root)] }, ActiveSide = PaneSide.Right,
                Layout = new() { LeftPaneFraction = .35 }
            };
            var json = JsonSerializer.Serialize(saved).Replace("\"SortProperty\":", "\"ColumnsMode\":true,\"SortProperty\":");
            await File.WriteAllTextAsync(path, json);
            var store = new JsonSessionStore(path);
            var loaded = await store.LoadAsync(); Assert.IsNull(loaded.Error); Assert.IsNotNull(loaded.Value);
            // Arrays use reference equality in records; compare the persisted shape instead.
            using var workspace = new WorkspaceState(root, new ActionFiles(), new FakeLogger());
            workspace.Restore(loaded.Value!); await workspace.InitializeAsync();
            Assert.AreEqual(saved.Left.Tabs[1].Path, workspace.Left.SelectedTab.CurrentPath);
            Assert.AreEqual("Size", workspace.Left.SelectedTab.SortProperty); Assert.IsTrue(workspace.Left.SelectedTab.SortDescending);
            Assert.AreEqual(PaneSide.Right, workspace.ActiveSide); Assert.AreEqual(2, workspace.Left.Tabs.Count);
            await workspace.Left.SelectedTab.NavigateAsync(root); await workspace.Left.SelectedTab.BackAsync();
            Assert.AreEqual(saved.Left.Tabs[1].Path, workspace.Left.SelectedTab.CurrentPath);
            await workspace.Left.SelectedTab.ForwardAsync(); Assert.AreEqual(root, workspace.Left.SelectedTab.CurrentPath);
            await store.SaveAsync(loaded.Value!);
            var rewritten = await File.ReadAllTextAsync(path);
            Assert.IsFalse(rewritten.Contains("ColumnsMode", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(JsonSerializer.Serialize(saved), rewritten);
            Assert.AreEqual(settingsBefore,await File.ReadAllTextAsync(settingsPath)); Assert.AreEqual(favoritesBefore,await File.ReadAllTextAsync(favoritesPath));
            var settings=(await new JsonSettingsStore(settingsPath).LoadAsync()).Settings;
            Assert.IsFalse(settings.ColumnsFor(PaneSide.Left).Columns.Single(c=>c.Key=="FileAge").Visible);
            Assert.IsFalse(settings.ColumnsFor(PaneSide.Right).Columns.Single(c=>c.Key=="LastWriteTime").Visible);
            Assert.AreEqual("Meiryo",settings.FontFamily);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task AllExtensionsUseShellDefaultWithoutArgumentsOrInterpreter()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-shell-open-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var invocations = new List<ProcessStartInfo>(); var service = new FileSystemService(invocations.Add);
            var parentDirectory = Environment.CurrentDirectory;
            foreach (var extension in new[] { ".txt", ".exe", ".ps1", ".bat", ".cmd", ".unknown" })
            {
                var path = Path.Combine(root, "日本語 & 空白" + extension); await File.WriteAllTextAsync(path, "inert fixture");
                await service.OpenFileAsync(new(path, Path.GetFileName(path), false, 13, null), default);
                var info = invocations.Last(); Assert.AreEqual(path, info.FileName); Assert.IsTrue(info.UseShellExecute);
                Assert.AreEqual(root, info.WorkingDirectory);
                Assert.AreEqual(parentDirectory, Environment.CurrentDirectory, "親プロセスのカレントディレクトリを変更しない");
                Assert.AreEqual("", info.Verb); Assert.AreEqual("", info.Arguments); Assert.AreEqual(0, info.ArgumentList.Count);
            }
            Assert.AreEqual(6, invocations.Count);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task OpenNavigatesDirectoriesAndSafelyHandlesNoTargetAndFailure()
    {
        var files = new ActionFiles(); using var tab = new TabState(@"C:\fixture", files, new FakeLogger());
        await tab.OpenAsync(null); Assert.AreEqual(0, files.Opened.Count);
        await tab.OpenAsync(new(@"C:\fixture\child", "child", true, null, null));
        Assert.AreEqual(@"C:\fixture\child", tab.CurrentPath); Assert.AreEqual(0, files.Opened.Count);
        files.Fail = true; await tab.OpenAsync(new(@"C:\fixture\failed.exe", "failed.exe", false, 0, null));
        StringAssert.Contains(tab.Status, "開けません"); Assert.AreEqual(@"C:\fixture\child", tab.CurrentPath);
    }

    [TestMethod]
    public async Task EditRequiresExactlyOneOrdinarySelectedFileAndUsesExactPathAndOwner()
    {
        var files = new ActionFiles(); var edit = new RecordingEdit();
        using var model = new MainViewModel(files, new FakeLogger(), shellEdit: edit);
        await model.Workspace.InitializeAsync(); var entries = model.Workspace.ActivePane.SelectedTab.Entries;
        string[] selected = []; model.GetSelectedPaths = () => selected; model.GetShellOwner = () => (IntPtr)123;
        foreach (var paths in new[] { Array.Empty<string>(), new[] { entries[0].FullPath }, new[] { entries[1].FullPath, entries[2].FullPath }, new[] { entries[3].FullPath }, new[] { @"C:\stale.txt" } })
        { selected = paths; Assert.IsFalse(model.Functions[3].Command.CanExecute(null)); await model.EditSelectedAsync(); }
        Assert.AreEqual(0, edit.Calls);
        selected = [entries[1].FullPath]; Assert.IsTrue(model.Functions[3].Command.CanExecute(null)); await model.EditSelectedAsync();
        Assert.AreEqual(entries[1], edit.Last); Assert.AreEqual((IntPtr)123, edit.Owner); Assert.AreEqual(1, edit.Calls);
        Assert.AreEqual(0, files.Opened.Count, "Edit must never fall back to Open");
    }

    [TestMethod]
    public async Task EditUnavailableOrFailedIsReportedWithoutOpenFallback()
    {
        var files = new ActionFiles(); var edit = new RecordingEdit { Failure = new NotSupportedException("Edit未登録") };
        using var model = new MainViewModel(files, new FakeLogger(), shellEdit: edit); await model.Workspace.InitializeAsync();
        model.GetSelectedPaths = () => [model.Workspace.ActivePane.SelectedTab.Entries[1].FullPath];
        await model.EditSelectedAsync(); StringAssert.Contains(model.Workspace.ActivePane.SelectedTab.Status, "Edit未登録");
        edit.Failure = new IOException("Shell起動失敗"); await model.EditSelectedAsync(); StringAssert.Contains(model.Workspace.ActivePane.SelectedTab.Status, "Shell起動失敗");
        Assert.AreEqual(0, files.Opened.Count); Assert.IsTrue(model.CanEditSelected);
    }

    [TestMethod]
    public async Task RealShellOpenStartsOnlyTheHarmlessProbeWithExactExecutablePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-shell-probe-日本語 & 空白-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            foreach(var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"ShellProbe"))) File.Copy(file,Path.Combine(root,Path.GetFileName(file)));
            var path = Path.Combine(root,"日本語 & 空白.exe");
            File.Copy(Path.Combine(root,"PaneCommander.ShellActionProbe.exe"),path);
            var parentDirectory = Environment.CurrentDirectory;
            await new FileSystemService().OpenFileAsync(new(path,Path.GetFileName(path),false,null,null),default);
            var receipt=Path.ChangeExtension(path,".launch.json");
            var deadline=DateTime.UtcNow.AddSeconds(8);
            while(!File.Exists(receipt) && DateTime.UtcNow<deadline) await Task.Delay(20);
            Assert.IsTrue(File.Exists(receipt),"Actual child process receipt, not just an internal call");
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.AreEqual(path,document.RootElement.GetProperty("Executable").GetString());
            Assert.AreEqual(root,document.RootElement.GetProperty("WorkingDirectory").GetString(), "実際の子プロセスのカレントディレクトリ");
            Assert.AreEqual(parentDirectory,Environment.CurrentDirectory);
            Assert.AreEqual(0,document.RootElement.GetProperty("Arguments").GetArrayLength());
            try
            {
                using var process=Process.GetProcessById(document.RootElement.GetProperty("Pid").GetInt32());
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8)); await process.WaitForExitAsync(timeout.Token);
            }
            catch(ArgumentException) { /* The receipt process already exited. */ }
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RealShellMissingEditNotifiesWithoutLaunchingOpen()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"PaneCommander-shell-noedit-"+Guid.NewGuid()); Directory.CreateDirectory(root);
            try
            {
                var path=Path.Combine(root,"日本語 & 空白.panecommander-noedit-"+Guid.NewGuid().ToString("N")); File.WriteAllText(path,"inert");
                using var owner=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Shell edit test owner") { Width=1,Height=1,WindowStyle=0 });
                using(var menu=new ShellContextMenu(root,[path],owner.Handle)) Assert.IsFalse(menu.Commands().Any(c=>c.Verb=="edit" && menu.IsEnabled(c.Id)));
                Assert.ThrowsException<NotSupportedException>(()=>new ShellEditService().EditAsync(new(path,Path.GetFileName(path),false,5,null),owner.Handle));
                Assert.AreEqual("inert",File.ReadAllText(path));
            }
            catch(Exception ex){failure=ex;}
            finally {Cleanup(root);}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public void RealShellEditRejectsDirectoryAndLinkBeforeInvocation()
    {
        var service = new ShellEditService();
        Assert.ThrowsException<NotSupportedException>(() => service.EditAsync(new(@"C:\fixture", "fixture", true, null, null), IntPtr.Zero));
        Assert.ThrowsException<NotSupportedException>(() => service.EditAsync(new(@"C:\fixture\link", "link", false, null, null, true), IntPtr.Zero));
    }

    private static void Cleanup(string root)
    {
        var resolved=Path.GetFullPath(root);
        if(!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(resolved),Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            || !Path.GetFileName(resolved).StartsWith("PaneCommander-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup");
        Directory.Delete(resolved,true);
    }

    internal sealed class ActionFiles : IFileSystemService
    {
        public readonly List<FileEntry> Opened = [];
        public bool Fail;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            [new(Path.Combine(path,"child"),"child",true,null,null), new(Path.Combine(path,"日本語 & 空白.txt"),"日本語 & 空白.txt",false,1,null),
             new(Path.Combine(path,"sample.ps1"),"sample.ps1",false,1,null), new(Path.Combine(path,"link.txt"),"link.txt",false,1,null,true)]));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) { Opened.Add(entry); return Fail ? Task.FromException(new IOException("test launch failure")) : Task.CompletedTask; }
    }
    internal sealed class RecordingEdit : IShellEditService
    {
        public int Calls; public FileEntry? Last; public IntPtr Owner; public Exception? Failure;
        public Task EditAsync(FileEntry entry, IntPtr owner) { Calls++; Last=entry; Owner=owner; return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
    }
}
