using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class DropFileSystemTests
{
    [TestMethod]
    public async Task ExplicitSameFolderCopyAndRenamePreserveContentsAndUnrelatedFile()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"PaneCommanderReviewTests",Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source=Path.Combine(root,"source.txt");var guard=Path.Combine(root,"guard.txt");
            await File.WriteAllTextAsync(source,"same folder contents");await File.WriteAllTextAsync(guard,"guard");
            var service=ShellFileOperationService.ForIsolatedTest(root);
            Assert.IsFalse((await service.ExecuteAsync(SameFolderOperation.Create(FileOperationKind.Copy,source,"copy.txt"),IntPtr.Zero)).Aborted);
            Assert.AreEqual("same folder contents",await File.ReadAllTextAsync(source));
            Assert.IsFalse((await service.ExecuteAsync(SameFolderOperation.Create(FileOperationKind.Move,Path.Combine(root,"copy.txt"),"renamed.txt"),IntPtr.Zero)).Aborted);
            Assert.IsFalse(File.Exists(Path.Combine(root,"copy.txt")));
            Assert.AreEqual("same folder contents",await File.ReadAllTextAsync(Path.Combine(root,"renamed.txt")));
            Assert.AreEqual("guard",await File.ReadAllTextAsync(guard));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task FailedMovePreservesSourceAndLockedExistingDestination()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
            var source = Path.Combine(left, "collision.txt");
            var destination = Path.Combine(right, "collision.txt");
            await File.WriteAllTextAsync(source, "source must survive failure");
            await File.WriteAllTextAsync(destination, "existing target must survive failure");
            var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
            var destinationHash = SHA256.HashData(File.ReadAllBytes(destination));
            var service = ShellFileOperationService.ForIsolatedTest(root);
            using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    var result = await service.ExecuteAsync(new(FileOperationKind.Move, [source], right), IntPtr.Zero);
                    Assert.IsTrue(result.Aborted, "書き込み不能な既存先への移動を成功扱いしない");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            }
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));
            CollectionAssert.AreEqual(destinationHash, SHA256.HashData(File.ReadAllBytes(destination)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DropRequestsCopyMoveAndFailWithoutChangingUnrelatedData()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
            var moved = Directory.CreateDirectory(Path.Combine(root, "moved")).FullName;
            var child = Directory.CreateDirectory(Path.Combine(right, "subdirectory")).FullName;
            var sentinel = Path.Combine(child, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "protected child");
            var sources = new[] { Path.Combine(left, "payload.txt"), Path.Combine(left, "second.txt") };
            foreach (var source in sources) await File.WriteAllTextAsync(source, "payload: " + Path.GetFileName(source));
            var hashes = sources.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
            var service = ShellFileOperationService.ForIsolatedTest(root);
            var copy = DropPolicy.CreateRequest(sources, right, true, false)!;
            Assert.IsFalse((await service.ExecuteAsync(copy, IntPtr.Zero)).Aborted);
            for (var i = 0; i < sources.Length; i++)
            {
                CollectionAssert.AreEqual(hashes[i], SHA256.HashData(File.ReadAllBytes(sources[i])));
                CollectionAssert.AreEqual(hashes[i], SHA256.HashData(File.ReadAllBytes(Path.Combine(right, Path.GetFileName(sources[i])))));
                Assert.IsFalse(File.Exists(Path.Combine(child, Path.GetFileName(sources[i]))));
            }
            Assert.IsFalse((await service.ExecuteAsync(DropPolicy.CreateRequest(sources, moved, false, true)!, IntPtr.Zero)).Aborted);
            Assert.IsTrue(sources.All(p => !File.Exists(p)));
            var survivor = Path.Combine(moved, "payload.txt");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExecuteAsync(DropPolicy.CreateRequest([survivor], left, true, false)!, IntPtr.Zero, cancel.Token));
            Assert.IsFalse(File.Exists(Path.Combine(left, "payload.txt")));
            try
            {
                var result = await service.ExecuteAsync(DropPolicy.CreateRequest([survivor], Path.Combine(root, "missing"), false, true)!, IntPtr.Zero);
                Assert.IsTrue(result.Aborted, "不存在の宛先を成功扱いしない");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            CollectionAssert.AreEqual(hashes[0], SHA256.HashData(File.ReadAllBytes(survivor)));
            Assert.AreEqual("protected child", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(root, true); }
    }
}
