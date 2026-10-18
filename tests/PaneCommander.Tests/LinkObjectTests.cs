using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;
using PaneCommander.App.ViewModels;

namespace PaneCommander.Tests;

[TestClass]
public sealed class LinkObjectTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task ActualCrossVolumeJunctionMoveKeepsReferent()
    {
        var root = Root(); string? remote = null;
        try
        {
            var sourceDrive = Path.GetPathRoot(root);
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.Equals(sourceDrive, StringComparison.OrdinalIgnoreCase) && d.DriveFormat == "NTFS");
            if (drive is null) { Assert.Inconclusive("別のローカルNTFSボリュームがありません。"); return; }
            remote = Path.Combine(drive.RootDirectory.FullName, "PaneCommanderReviewTests", Path.GetFileName(root));
            try { Directory.CreateDirectory(remote); }
            catch (UnauthorizedAccessException ex) { remote = null; Assert.Inconclusive("別ボリュームの隔離一時領域を作成できません: " + ex.Message); return; }
            var destinationAlias = Path.Combine(root, "other-volume"); Junction(destinationAlias, remote);
            var target = Dir(root, "referent"); var sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "protected"); var hash = Hash(sentinel);
            var source = Path.Combine(root, "junction"); Junction(source, target); var descriptor = LinkObjectOperations.Read(source);
            var service = ShellFileOperationService.ForIsolatedTest(root);
            await service.ExecuteAsync(new(FileOperationKind.Move, [source], destinationAlias), IntPtr.Zero);
            AssertAbsent(source); var moved = Path.Combine(remote, "junction"); CollectionAssert.AreEqual(descriptor.Data, LinkObjectOperations.Read(moved).Data); Assert.AreEqual(hash, Hash(sentinel));
            var tree = Dir(root, "tree"); File.WriteAllText(Path.Combine(tree, "ordinary.txt"), "ordinary"); Junction(Path.Combine(tree, "nested-link"), target);
            await service.ExecuteAsync(new(FileOperationKind.Move, [tree], destinationAlias), IntPtr.Zero);
            AssertAbsent(tree); Assert.AreEqual("ordinary", File.ReadAllText(Path.Combine(remote, "tree", "ordinary.txt"))); CollectionAssert.AreEqual(descriptor.Data, LinkObjectOperations.Read(Path.Combine(remote, "tree", "nested-link")).Data); Assert.AreEqual(hash, Hash(sentinel));
            Evidence("actual-cross-volume", new { SourceVolume = sourceDrive, DestinationVolume = drive.Name, LinkAndTreeMoved = true, RawLinkDataPreserved = true, ReferentHashBefore = hash, ReferentHashAfter = Hash(sentinel) });
        }
        finally
        {
            if (remote is not null)
            {
                var expectedParent = Path.Combine(Path.GetPathRoot(remote)!, "PaneCommanderReviewTests");
                if (!Path.GetDirectoryName(remote)!.Equals(expectedParent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(remote) != Path.GetFileName(root)) throw new InvalidOperationException("Unsafe remote cleanup");
                LinkObjectOperations.Delete(remote, CancellationToken.None);
            }
            Cleanup(root);
        }
    }
    [TestMethod]
    public void CrossVolumeFallbackAlgorithmVerifiesCloneBeforeDeletingSource()
    {
        var root = Root();
        try
        {
            var target = Dir(root, "referent"); var sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "keep");
            var source = Path.Combine(root, "link"); Junction(source, target); var descriptor = LinkObjectOperations.Read(source);
            var destination = Path.Combine(root, "copy-delete-result");
            LinkObjectOperations.MoveAcrossVolumes(source, destination, CancellationToken.None);
            AssertAbsent(source); CollectionAssert.AreEqual(descriptor.Data, LinkObjectOperations.Read(destination).Data);
            source = Path.Combine(root, "second-link"); Junction(source, target);
            Assert.ThrowsExactly<IOException>(() => LinkObjectOperations.MoveAcrossVolumes(source, destination, CancellationToken.None));
            SameLink(source, destination); Assert.AreEqual("keep", File.ReadAllText(sentinel));
            Evidence("cross-volume-algorithm", new { CopyVerifiedBeforeSourceDelete = true, FailurePreservesSource = true, ReferentHash = Hash(sentinel), ActualDifferentVolumes = false });
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public async Task F5F6AndExplicitPermanentDeleteOperateSelectedJunctionThroughSharedService()
    {
        var root = Root();
        try
        {
            var from = Dir(root, "left"); var to = Dir(root, "right"); var target = Dir(root, "referent"); var sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "keep");
            var link = Path.Combine(from, "junction"); Junction(link, target); var selected = new[] { link };
            using var model = new MainViewModel(new FileSystemService(), new FakeLogger(), fileOperations: ShellFileOperationService.ForIsolatedTest(root));
            await model.Workspace.Left.SelectedTab.NavigateAsync(from); await model.Workspace.Right.SelectedTab.NavigateAsync(to); model.DeleteCommandEnabled = true;
            model.GetSelectedPaths = () => selected;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.RequestFileOperation = async kind =>
            {
                try { await model.ExecuteFileOperationAsync(new(kind, selected, kind == FileOperationKind.Delete ? null : model.Workspace.OppositePane.SelectedTab.CurrentPath, kind == FileOperationKind.Move ? "renamed" : null, Recycle: false), IntPtr.Zero); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
            };
            foreach (var key in new[] { 5, 6 })
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); Assert.IsTrue(model.Functions[key - 1].Command.CanExecute(null)); model.Functions[key - 1].Command.Execute(null); await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (key == 5) SameLink(link, Path.Combine(to, "junction"));
                if (key == 6) { AssertAbsent(link); selected = [Path.Combine(to, "renamed")]; SameLink(Path.Combine(to, "junction"), selected[0]); model.Workspace.Activate(PaneSide.Right); }
                if (key == 8) AssertAbsent(selected[0]);
                Assert.AreEqual("keep", File.ReadAllText(sentinel));
            }
            await Assert.ThrowsExactlyAsync<IOException>(()=>model.ExecuteFileOperationAsync(new(FileOperationKind.Delete,selected,Recycle:true),IntPtr.Zero));
            Assert.IsNotNull(LinkObjectOperations.Read(selected[0]));
            await model.ExecuteFileOperationAsync(new(FileOperationKind.Delete,selected,Recycle:false),IntPtr.Zero);AssertAbsent(selected[0]);Assert.AreEqual("keep",File.ReadAllText(sentinel));
            Evidence("function-commands", new { F5 = true, F6 = true, F8RecycleSafelyRejected=true, ExplicitPermanentDelete = true, ReferentHash = Hash(sentinel), NativeKeyboardInput = false, IsolatedConfirmationBypass = true });
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public void UnknownTagsAreRejectedAndSymlinkCreationErrorsAreExplicit()
    {
        var raw = new byte[16]; BitConverter.GetBytes(0x80001234u).CopyTo(raw, 0);
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => LinkObjectOperations.Decode(raw, raw.Length, true, "unknown")).Message, "80001234");
        var root = Root();
        try
        {
            var target = Path.Combine(root, "target.txt"); File.WriteAllText(target, "protected");
            var text = "target.txt"; var data = new byte[20 + Encoding.Unicode.GetByteCount(text)];
            BitConverter.GetBytes(0xA000000Cu).CopyTo(data, 0); BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4); BitConverter.GetBytes((ushort)(data.Length - 20)).CopyTo(data, 10); BitConverter.GetBytes(1u).CopyTo(data, 16); Encoding.Unicode.GetBytes(text).CopyTo(data, 20);
            var link = LinkObjectOperations.Decode(data, data.Length, false, "test"); Assert.AreEqual(text, link.Target);
            var destination = Path.Combine(root, "new-link");
            try { LinkObjectOperations.Clone(link, destination); Assert.AreEqual(text, LinkObjectOperations.Read(destination).Target); }
            catch (IOException ex) when (ex.InnerException is Win32Exception error)
            {
                Assert.IsTrue(error.NativeErrorCode is 1314 or 5, ex.ToString()); StringAssert.Contains(ex.Message, "Win32 " + error.NativeErrorCode); AssertAbsent(destination);
                Evidence("symlink-creation-restriction", new { error.NativeErrorCode, ex.Message, SourceReferentHash = Hash(target), DestinationAbsent = true });
            }
            Assert.AreEqual("protected", File.ReadAllText(target));
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public async Task JunctionTreesBrokenAndCyclesPreserveObjectsAndReferentData()
    {
        var root = Root();
        try
        {
            var target = Dir(root, "referent"); var sentinel = Path.Combine(target, "sentinel.bin");
            File.WriteAllBytes(sentinel, Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray());
            var before = Hash(sentinel); var stamp = File.GetLastWriteTimeUtc(sentinel);
            var source = Dir(root, "source"); var to = Dir(root, "destination");
            var link = Path.Combine(source, "junction"); Junction(link, target);
            var service = ShellFileOperationService.ForIsolatedTest(root);
            await service.ExecuteAsync(new(FileOperationKind.Copy, [link], to), IntPtr.Zero);
            var copied = Path.Combine(to, "junction"); SameLink(link, copied);
            CollectionAssert.AreEqual(LinkObjectOperations.Read(link).Data, LinkObjectOperations.Read(copied).Data, "リンクの再解析データそのものを複製する");
            var moved = Path.Combine(to, "moved"); await service.ExecuteAsync(new(FileOperationKind.Move, [link], to, "moved"), IntPtr.Zero);
            AssertAbsent(link); SameLink(copied, moved);
            await service.ExecuteAsync(new(FileOperationKind.Delete, [moved], Recycle: false), IntPtr.Zero); AssertAbsent(moved);
            var tree = Dir(source, "tree"); File.WriteAllText(Path.Combine(tree, "ordinary.txt"), "ordinary");
            var nested = Dir(tree, "nested"); Junction(Path.Combine(nested, "link"), target);
            Junction(Path.Combine(tree, "broken"), Path.Combine(root, "missing"));
            Junction(Path.Combine(tree, "cycle"), tree);
            await service.ExecuteAsync(new(FileOperationKind.Copy, [tree], to), IntPtr.Zero);
            var treeCopy = Path.Combine(to, "tree");
            foreach (var name in new[] { "nested/link", "broken", "cycle" }) SameLink(Path.Combine(tree, name), Path.Combine(treeCopy, name));
            Assert.AreEqual("ordinary", File.ReadAllText(Path.Combine(treeCopy, "ordinary.txt")));
            Assert.AreEqual(4, Directory.EnumerateFileSystemEntries(treeCopy).Count(), "リンク先を実ディレクトリとして展開しない");
            await service.ExecuteAsync(new(FileOperationKind.Delete, [tree, treeCopy, copied], Recycle: false), IntPtr.Zero);
            AssertAbsent(tree); AssertAbsent(treeCopy); AssertAbsent(copied);
            Assert.AreEqual(before, Hash(sentinel)); Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(sentinel));
            Evidence("junction", new { ReferentHashBefore = before, ReferentHashAfter = Hash(sentinel), LastWriteUnchanged = true, RawReparseDataPreserved = true, NestedBrokenCycleAndMultipleDelete = true });
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public async Task DestinationFailureLeavesLinkAndReferentUntouched()
    {
        var root = Root();
        try
        {
            var target = Dir(root, "target"); var sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "protected");
            var source = Path.Combine(root, "link"); Junction(source, target);
            var to = Dir(root, "to"); var collision = Path.Combine(to, "link"); Junction(collision, target);
            var before = LinkObjectOperations.Read(source).Data; var service = ShellFileOperationService.ForIsolatedTest(root);
            foreach (var operation in new[] { FileOperationKind.Copy, FileOperationKind.Move })
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => service.ExecuteAsync(new(operation, [source], to), IntPtr.Zero));
                CollectionAssert.AreEqual(before, LinkObjectOperations.Read(source).Data); SameLink(source, collision);
                Assert.AreEqual("protected", File.ReadAllText(sentinel));
            }
            Evidence("destination-failure", new { SourceLinkPreserved = true, DestinationLinkPreserved = true, ReferentHash = Hash(sentinel), NoFallback = true });
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public async Task MultipleSelectionAndInternalDragPolicyUseObjectOperations()
    {
        var root = Root();
        try
        {
            var target = Dir(root, "target"); File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            var from = Dir(root, "from"); var to = Dir(root, "to");
            var a = Path.Combine(from, "a"); var b = Path.Combine(from, "b"); Junction(a, target); Junction(b, target);
            File.WriteAllText(Path.Combine(from, "ordinary.txt"), "ordinary"); var paths = new[] { a, b, Path.Combine(from, "ordinary.txt") };
            Assert.IsTrue(ShellFileOperationService.RequiresInternalDrag(paths));
            var intent = DropPolicy.Intent(paths, to, true, false); Assert.AreEqual(DropIntent.Copy, intent);
            var service = ShellFileOperationService.ForIsolatedTest(root);
            await service.ExecuteAsync(new(FileOperationKind.Copy, paths, to), IntPtr.Zero);
            SameLink(a, Path.Combine(to, "a")); SameLink(b, Path.Combine(to, "b")); Assert.AreEqual("ordinary", File.ReadAllText(Path.Combine(to, "ordinary.txt")));
            await service.ExecuteAsync(new(FileOperationKind.Copy, [a], from), IntPtr.Zero); SameLink(a, Path.Combine(from, "a - コピー (1)"));
            await service.ExecuteAsync(new(FileOperationKind.Delete, paths, Recycle: false), IntPtr.Zero); Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        }
        finally { Cleanup(root); }
    }
    [TestMethod]
    public async Task DirectoryAndFileSymlinksKeepRelativeTargetsIncludingBrokenLinks()
    {
        var root = Root();
        try
        {
            var source = Dir(root, "source"); var to = Dir(root, "to"); var target = Dir(root, "target"); var file = Path.Combine(target, "keep.txt"); File.WriteAllText(file, "protected");
            try { File.CreateSymbolicLink(Path.Combine(source, "file"), @"..\target\keep.txt"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Evidence("symlink-unavailable", new { Exception = ex.ToString(), ex.HResult }); Assert.Inconclusive(ex.ToString()); return; }
            Directory.CreateSymbolicLink(Path.Combine(source, "directory"), @"..\target");
            File.CreateSymbolicLink(Path.Combine(source, "broken-file"), @"..\absent-file");
            Directory.CreateSymbolicLink(Path.Combine(source, "broken-directory"), @"..\absent-dir");
            Directory.CreateSymbolicLink(Path.Combine(source, "cycle"), ".");
            var service = ShellFileOperationService.ForIsolatedTest(root);
            foreach (var path in Directory.EnumerateFileSystemEntries(source).ToArray())
            {
                var name = Path.GetFileName(path); var original = LinkObjectOperations.Read(path);
                await service.ExecuteAsync(new(FileOperationKind.Copy, [path], to), IntPtr.Zero); var copy = Path.Combine(to, name); SameLink(path, copy);
                Assert.AreEqual(original.Target, LinkObjectOperations.Read(copy).Target); Assert.AreEqual(1u, BitConverter.ToUInt32(original.Data, 16));
                await service.ExecuteAsync(new(FileOperationKind.Move, [path], to, name + "-moved"), IntPtr.Zero);
                await service.ExecuteAsync(new(FileOperationKind.Delete, [copy, Path.Combine(to, name + "-moved")], Recycle: false), IntPtr.Zero);
            }
            Assert.AreEqual("protected", File.ReadAllText(file)); Evidence("symlink", new { RelativeTextPreserved = true, DirectoryFileBrokenCycle = true, ReferentHash = Hash(file) });
        }
        finally { Cleanup(root); }
    }
    private void Evidence(string name, object value) { var path = Path.Combine(AppContext.BaseDirectory, "link-" + name + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); TestContext.AddResultFile(path); }
    private static void SameLink(string source, string destination) { var a = LinkObjectOperations.Read(source); var b = LinkObjectOperations.Read(destination); Assert.AreEqual(a.Tag, b.Tag); Assert.AreEqual(a.Directory, b.Directory); Assert.AreEqual(a.Target, b.Target); }
    private static void AssertAbsent(string path) { try { _ = File.GetAttributes(path); Assert.Fail("Object remains: " + path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Root() => Dir(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests"), Guid.NewGuid().ToString("N"));
    private static string Dir(string root, string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static void Cleanup(string root)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(root), out _)) throw new InvalidOperationException("Unsafe cleanup");
        LinkObjectOperations.Delete(root, CancellationToken.None);
    }
    private static void Junction(string path, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", path, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; Assert.IsTrue(process.WaitForExit(5000)); Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }
}
