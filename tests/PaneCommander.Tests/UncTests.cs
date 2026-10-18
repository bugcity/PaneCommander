using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class UncTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(@"\\server\share", @"\\server\share")]
    [DataRow(@"\\server\share\folder\..\日本語 空白", @"\\server\share\日本語 空白")]
    [DataRow("//server/share/folder", @"\\server\share\folder")]
    [DataRow(@"\\192.0.2.1\share\folder", @"\\192.0.2.1\share\folder")]
    public void UncSyntaxIsAcceptedWithoutContactingServer(string input, string expected)
        => Assert.AreEqual(expected, FileSystemService.NormalizePath(input));

    [TestMethod]
    [DataRow(@"\\server")]
    [DataRow(@"\\server\")]
    [DataRow(@"\\server\\folder")]
    [DataRow(@"\\server\..\folder")]
    public void ServerWithoutShareIsRejectedBeforeNetworkAccess(string path)
        => Assert.ThrowsExactly<ArgumentException>(() => FileSystemService.NormalizePath(path));

    [TestMethod]
    [DataRow(@"\\?\UNC\server\share")]
    [DataRow(@"\\.\C:\folder")]
    [DataRow("//?/C:/folder")]
    [DataRow(@"\??\C:\folder")]
    public void DeviceNamespaceRemainsRejected(string path)
        => Assert.ThrowsExactly<NotSupportedException>(() => FileSystemService.NormalizePath(path));

    [TestMethod]
    public async Task UncHistoryShareRootAndSessionRestorePreserveLogicalPath()
    {
        var files = new FakeFileSystem();
        using var pane = new PaneState(PaneSide.Left, @"C:\fixture", files, new FakeLogger());
        var tab = pane.SelectedTab;
        await tab.NavigateAsync(@"\\server\share\folder");
        await tab.UpAsync(); Assert.AreEqual(@"\\server\share", tab.CurrentPath.TrimEnd('\\'));
        var reads = files.ReadCount; await tab.UpAsync(); Assert.AreEqual(reads, files.ReadCount);
        await tab.BackAsync(); Assert.AreEqual(@"\\server\share\folder", tab.CurrentPath);
        await tab.ForwardAsync(); Assert.AreEqual(@"\\server\share", tab.CurrentPath.TrimEnd('\\'));
        var snapshot = JsonSerializer.Deserialize<PaneSnapshot>(JsonSerializer.Serialize(pane.Capture()))!;
        pane.Restore(snapshot); Assert.AreEqual(@"\\server\share", pane.SelectedTab.CurrentPath.TrimEnd('\\'));
        new FavoritesDocument { Items = [new(Guid.NewGuid(), "共有", @"\\server\share\folder")] }.Validate();
    }

    [TestMethod]
    public void UncDragAndFileOperationsRespectShareBoundaries()
    {
        Assert.AreEqual(DropIntent.Move, DropPolicy.Intent([@"\\server\share\a.txt"], @"\\server\share\folder", false, false));
        Assert.AreEqual(DropIntent.Copy, DropPolicy.Intent([@"\\server\share\a.txt"], @"\\server\other", false, false));
        Assert.AreEqual(DropIntent.Copy, DropPolicy.Intent([@"C:\fixture\a.txt"], @"\\server\share", false, false));
        Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(new(FileOperationKind.Copy, [@"\\server\share\"], @"C:\fixture")));
        Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(new(FileOperationKind.Move, [@"\\server\share\folder"], @"\\server\share\folder\child")));
        DropPolicy.Validate(SameFolderOperation.Create(FileOperationKind.Move, @"\\server\share\a.txt", "renamed.txt"));
    }

    [TestMethod]
    public async Task UncRecycleIsRefusedBeforeContactingServer()
    {
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => new ShellFileOperationService().ExecuteAsync(
            new(FileOperationKind.Delete, [@"\\server\share\fixture.txt"], Recycle: true), IntPtr.Zero));
        StringAssert.Contains(error.Message, "完全削除には自動で切り替えません");
    }

    [TestMethod]
    public async Task AuthorizedUncFixtureSupportsEnumerationAndNativeOperations()
    {
        var authorized = Environment.GetEnvironmentVariable("PANECOMMANDER_UNC_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(authorized)) { Assert.Inconclusive("UNC実機試験には明示承認されたPANECOMMANDER_UNC_TEST_ROOTが必要です。"); return; }
        var parent = Path.Combine(FileSystemService.NormalizePath(authorized), "PaneCommanderReviewTests");
        Assert.IsTrue(parent.StartsWith(@"\\", StringComparison.Ordinal));
        var id = Guid.NewGuid().ToString(); var root = Path.Combine(parent, id);
        Assert.IsFalse(Directory.Exists(root)); Directory.CreateDirectory(root);
        var localParent = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests");
        var localRoot = Path.Combine(localParent, Guid.NewGuid().ToString()); Directory.CreateDirectory(localRoot);
        try
        {
            var file = Path.Combine(root, "日本語 空白.txt"); await File.WriteAllTextAsync(file, "PaneCommander UNC fixture");
            ProcessStartInfo? opened = null;
            var files = new FileSystemService(info => opened = info);
            var snapshot = await files.ReadFolderAsync(root, default);
            Assert.AreEqual(root, snapshot.Path); Assert.AreEqual(1, snapshot.Entries.Count);
            Assert.AreEqual("日本語 空白.txt", snapshot.Entries[0].Name);
            await files.OpenFileAsync(snapshot.Entries[0], default); Assert.AreEqual(file, opened!.FileName);
            Assert.AreEqual("PaneCommander UNC fixture", await TextPreviewReader.ReadAsync(file));
            using var tab = new TabState(root, files, new FakeLogger());
            await tab.RefreshAsync(); Assert.IsTrue(tab.NavigationSucceeded);
            await tab.NavigateAsync(Path.Combine(root, "not-created")); Assert.IsFalse(tab.NavigationSucceeded);
            Assert.AreEqual(root, tab.CurrentPath, "到達不能な場所への移動失敗で元の一覧を失わない");

            var operations = ShellFileOperationService.ForIsolatedTest(root);
            var created = Path.Combine(root, "created");
            Assert.IsFalse((await operations.ExecuteAsync(new(FileOperationKind.CreateDirectory, [], root, @"created\中間/leaf"), IntPtr.Zero)).Aborted);
            Assert.IsTrue(Directory.Exists(created));
            Assert.IsTrue(Directory.Exists(Path.Combine(created, "中間", "leaf")));
            Assert.IsFalse((await operations.ExecuteAsync(new(FileOperationKind.CreateFile, [], created, "空ファイル.txt"), IntPtr.Zero)).Aborted);
            Assert.AreEqual(0L, new FileInfo(Path.Combine(created, "空ファイル.txt")).Length);
            Assert.IsFalse((await operations.ExecuteAsync(new(FileOperationKind.Copy, [file], created), IntPtr.Zero)).Aborted);
            var copy = Path.Combine(created, Path.GetFileName(file)); Assert.AreEqual(await File.ReadAllTextAsync(file), await File.ReadAllTextAsync(copy));
            Assert.IsFalse((await operations.ExecuteAsync(SameFolderOperation.Create(FileOperationKind.Move, copy, "renamed.txt"), IntPtr.Zero)).Aborted);
            var renamed = Path.Combine(created, "renamed.txt"); Assert.IsTrue(File.Exists(renamed)); Assert.IsFalse(File.Exists(copy));
            await Assert.ThrowsExactlyAsync<IOException>(() => operations.ExecuteAsync(new(FileOperationKind.Delete, [renamed], Recycle: true), IntPtr.Zero));
            Assert.IsTrue(File.Exists(renamed), "ごみ箱拒否後も元ファイルを維持");
            Assert.IsFalse((await operations.ExecuteAsync(new(FileOperationKind.Delete, [renamed], Recycle: false), IntPtr.Zero)).Aborted);
            Assert.IsFalse(File.Exists(renamed)); Assert.IsTrue(File.Exists(file));
            var transfer = ShellFileOperationService.ForIsolatedTransferTest(localRoot, root);
            var localFile = Path.Combine(localRoot, "local-transfer.txt"); await File.WriteAllTextAsync(localFile, "local to UNC and back");
            Assert.IsFalse((await transfer.ExecuteAsync(new(FileOperationKind.Copy, [localFile], created), IntPtr.Zero)).Aborted);
            var uncFile = Path.Combine(created, "local-transfer.txt"); Assert.AreEqual(await File.ReadAllTextAsync(localFile), await File.ReadAllTextAsync(uncFile));
            Assert.IsFalse((await transfer.ExecuteAsync(new(FileOperationKind.Move, [uncFile], localRoot, "returned.txt"), IntPtr.Zero)).Aborted);
            Assert.IsFalse(File.Exists(uncFile)); Assert.AreEqual("local to UNC and back", await File.ReadAllTextAsync(Path.Combine(localRoot, "returned.txt")));
            Assert.IsFalse((await transfer.ExecuteAsync(new(FileOperationKind.Move, [localFile], created), IntPtr.Zero)).Aborted);
            Assert.IsFalse(File.Exists(localFile)); Assert.IsTrue(File.Exists(uncFile));
            Assert.IsFalse((await transfer.ExecuteAsync(new(FileOperationKind.Copy, [uncFile], localRoot, "copied-back.txt"), IntPtr.Zero)).Aborted);
            Assert.AreEqual("local to UNC and back", await File.ReadAllTextAsync(Path.Combine(localRoot, "copied-back.txt")));
            TestContext.WriteLine("UNC enumeration/Open dispatch/preview/navigation/create/copy/rename/permanent-delete/recycle-refusal and local/UNC bidirectional copy/move confirmed in owned GUID fixture: " + root);
        }
        finally
        {
            var checkedRoot = Path.GetFullPath(root);
            Assert.AreEqual(parent, Path.GetDirectoryName(checkedRoot)); Assert.AreEqual(id, Path.GetFileName(checkedRoot));
            Directory.Delete(checkedRoot, true);
            var checkedLocal = Path.GetFullPath(localRoot);
            Assert.AreEqual(localParent, Path.GetDirectoryName(checkedLocal)); Assert.IsTrue(Guid.TryParse(Path.GetFileName(checkedLocal), out _));
            Directory.Delete(checkedLocal, true);
        }
    }
}
