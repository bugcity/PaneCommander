using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ShellHardeningTests
{
    [TestMethod]
    public async Task RecycleProducesAnActualRecycleBinItemAndDoesNotDeletePermanently()
    {
        var root=Root();var marker=Guid.NewGuid().ToString("N");var source=Path.Combine(root,marker+".txt");string? recycled=null;
        try
        {
            File.WriteAllText(source,marker);
            var service=ShellFileOperationService.ForIsolatedTest(root,path=>recycled=path);
            var result=await service.ExecuteAsync(new(FileOperationKind.Delete,[source],Recycle:true),IntPtr.Zero);
            Assert.IsFalse(result.Aborted);Assert.IsFalse(File.Exists(source));Assert.IsNotNull(recycled,"PostDeleteItem must return the item in the Recycle Bin");
            Assert.IsTrue(Path.GetFullPath(recycled).StartsWith(Path.GetPathRoot(source)+"$Recycle.Bin\\",StringComparison.OrdinalIgnoreCase));Assert.AreEqual(marker,File.ReadAllText(recycled));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{Original=source,RecycleReceipt=recycled,OriginalAbsent=true,ReceiptExists=true,ContentVerified=true,GuiInput=false}));
        }
        finally
        {
            if(recycled is not null && File.Exists(recycled))
            {
                // Delete only this test's own receipt and its matching original-path metadata.
                Assert.AreEqual(marker,File.ReadAllText(recycled));Assert.IsTrue(Path.GetFileName(recycled).StartsWith("$R",StringComparison.Ordinal));
                var metadata=Path.Combine(Path.GetDirectoryName(recycled)!,"$I"+Path.GetFileName(recycled)[2..]);
                byte[] bytes;
                using(var stream=new FileStream(metadata,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {using var snapshot=new MemoryStream();stream.CopyTo(snapshot);bytes=snapshot.ToArray();}
                Assert.IsTrue(System.Text.Encoding.Unicode.GetString(bytes).Contains(source,StringComparison.OrdinalIgnoreCase));
                File.Delete(recycled);File.Delete(metadata);
            }
            Cleanup(root);
        }
    }
    [TestMethod]
    public async Task RecycleOfAJunctionTreeFailsSafelyInsteadOfFallingBackToPermanentDelete()
    {
        var root=Root();try
        {
            var target=Directory.CreateDirectory(Path.Combine(root,"referent")).FullName;File.WriteAllText(Path.Combine(target,"keep.txt"),"protected");
            var tree=Directory.CreateDirectory(Path.Combine(root,"tree")).FullName;var link=Path.Combine(tree,"junction");Junction(link,target);
            var service=ShellFileOperationService.ForIsolatedTest(root);
            await Assert.ThrowsExactlyAsync<IOException>(()=>service.ExecuteAsync(new(FileOperationKind.Delete,[tree],Recycle:true),IntPtr.Zero));
            Assert.IsNotNull(LinkObjectOperations.Read(link));Assert.AreEqual("protected",File.ReadAllText(Path.Combine(target,"keep.txt")));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{Link=link,RecycleRejected=true,LinkStillExists=true,ReferentContent="protected",PermanentFallback=false}));
        }
        finally{Cleanup(root);}
    }
    [TestMethod]
    public void CancelledStagedCopyPreservesExistingDataAndCleansItsOwnPartialFile()
    {
        var root=Root();try
        {
            var source=Path.Combine(root,"source.bin");using(var file=File.Create(source))file.SetLength(128*1024*1024);
            var destination=Path.Combine(root,"destination.bin");using var cancellation=new CancellationTokenSource();
            var copy=Task.Run(()=>LinkObjectOperations.Copy(source,destination,cancellation.Token));
            var watch=Stopwatch.StartNew();while(!copy.IsCompleted&&watch.Elapsed<TimeSpan.FromSeconds(10))
            {if(Directory.EnumerateFiles(root,".panecommander-copy-*.tmp").Any()){cancellation.Cancel();break;}Thread.Sleep(1);}
            try{copy.GetAwaiter().GetResult();Assert.Inconclusive("コピーが中断前に完了したため、途中キャンセルは未検証です。");}
            catch(OperationCanceledException){Assert.IsFalse(File.Exists(destination));Assert.AreEqual(0,Directory.EnumerateFiles(root,".panecommander-copy-*.tmp").Count());}
            File.WriteAllText(destination,"existing");Assert.ThrowsExactly<IOException>(()=>LinkObjectOperations.Copy(source,destination,default));Assert.AreEqual("existing",File.ReadAllText(destination));
        }
        finally{Cleanup(root);}
    }
    [TestMethod]
    public void RepeatedBackgroundQueryDisposalDoesNotAccumulateOneHandlePerMenu()
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var root=Root();try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));try
                {
                    for(var i=0;i<20;i++){using var menu=new ShellContextMenu(root,[],IntPtr.Zero);}
                    using var process=Process.GetCurrentProcess();process.Refresh();var start=process.HandleCount;
                    for(var i=0;i<120;i++){using var menu=new ShellContextMenu(root,[],IntPtr.Zero);menu.Dispose();}
                    process.Refresh();Assert.IsTrue(process.HandleCount-start<30,$"120 calls retained {process.HandleCount-start} handles");
                }
                finally{OleUninitialize();}
            }
            catch(Exception error){failure=error;}
            finally{Cleanup(root);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)));if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static string Root(){var root=Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static void Cleanup(string root){Assert.AreEqual(Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests"),Path.GetDirectoryName(root));Assert.IsTrue(Guid.TryParse(Path.GetFileName(root),out _));LinkObjectOperations.Delete(root,default);}
    private static void Junction(string link,string target){using var process=Process.Start(new ProcessStartInfo("cmd.exe",$"/d /c mklink /J \"{link}\" \"{target}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;process.WaitForExit();Assert.AreEqual(0,process.ExitCode);}
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
}
