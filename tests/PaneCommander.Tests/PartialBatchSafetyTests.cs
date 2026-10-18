using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class PartialBatchSafetyTests
{
    [TestMethod]
    public Task CopyAbortAfterFirstCompletionPreservesPendingSourcesAndExistingTargets() => Check(FileOperationKind.Copy,false);
    [TestMethod]
    public Task MoveAbortAfterFirstCompletionPreservesPendingSourcesAndExistingTargets() => Check(FileOperationKind.Move,false);
    [TestMethod]
    public Task MoveFailureAfterFirstCompletionPreservesPendingSourcesAndExistingTargets() => Check(FileOperationKind.Move,true);

    private static async Task Check(FileOperationKind kind,bool fail)
    {
        var parent=Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests");
        var root=Directory.CreateDirectory(Path.Combine(parent,Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var left=Directory.CreateDirectory(Path.Combine(root,"left")).FullName;
            var right=Directory.CreateDirectory(Path.Combine(root,"right")).FullName;
            var sources=Enumerable.Range(1,3).Select(i=>Path.Combine(left,$"{i}.txt")).ToArray();
            foreach(var path in sources)await File.WriteAllTextAsync(path,"source "+Path.GetFileName(path));
            var hashes=sources.Select(Hash).ToArray();
            var existing=Path.Combine(right,"2.txt");await File.WriteAllTextAsync(existing,"existing destination");var guard=Hash(existing);
            var operations=new StopAtSecond(ShellFileOperationService.ForIsolatedTest(root),fail);
            using var model=new MainViewModel(new FileSystemService(),new FakeLogger(),fileOperations:operations);
            await model.Workspace.Left.SelectedTab.NavigateAsync(left);await model.Workspace.Right.SelectedTab.NavigateAsync(right);
            var requests=sources.Select(p=>new FileOperationRequest(kind,[p],right)).ToArray();
            if(fail)await Assert.ThrowsExactlyAsync<IOException>(()=>model.ExecuteFileOperationBatchAsync(requests,IntPtr.Zero));
            else Assert.IsTrue((await model.ExecuteFileOperationBatchAsync(requests,IntPtr.Zero)).Aborted);
            Assert.AreEqual(2,operations.Calls,"第三操作を実行しない");
            Assert.AreEqual(hashes[0],Hash(Path.Combine(right,"1.txt")),"完了済み操作を巻き戻さない");
            Assert.AreEqual(kind==FileOperationKind.Copy,File.Exists(sources[0]));
            if(kind==FileOperationKind.Copy)Assert.AreEqual(hashes[0],Hash(sources[0]));
            Assert.AreEqual(hashes[1],Hash(sources[1]));Assert.AreEqual(hashes[2],Hash(sources[2]));
            Assert.AreEqual(guard,Hash(existing));Assert.IsFalse(File.Exists(Path.Combine(right,"3.txt")));
            CollectionAssert.AreEquivalent(Directory.GetFiles(left),model.Workspace.Left.SelectedTab.Entries.Select(e=>e.FullPath).ToArray());
            CollectionAssert.AreEquivalent(Directory.GetFiles(right),model.Workspace.Right.SelectedTab.Entries.Select(e=>e.FullPath).ToArray());
            StringAssert.Contains(model.OperationStatus,"1/3");Assert.IsFalse(model.IsFileOperationRunning);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{kind,fail,Completed=1,UnprocessedSourcesPreserved=true,ExistingTargetPreserved=true,ListsMatch=true,GuiInput=false,Boundary="test adapter returns abort/error before second native operation; first operation uses actual Shell service"}));
        }
        finally
        {
            Assert.AreEqual(parent,Path.GetDirectoryName(root));Assert.IsTrue(Guid.TryParse(Path.GetFileName(root),out _));
            Directory.Delete(root,true);
        }
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class StopAtSecond(IFileOperationService inner,bool fail):IFileOperationService
    {
        public int Calls;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request,IntPtr owner,CancellationToken token=default)
        {
            if(++Calls!=2)return inner.ExecuteAsync(request,owner,token);
            return fail?Task.FromException<FileOperationResult>(new IOException("Controlled failure before second operation")):Task.FromResult(new FileOperationResult(true));
        }
    }
}
