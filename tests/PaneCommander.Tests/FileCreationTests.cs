using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FileCreationTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow(@"..\outside.txt")]
    [DataRow(@"child\file.txt")]
    [DataRow(@"C:\outside.txt")]
    [DataRow("CON.txt")]
    [DataRow("file.")]
    [DataRow("file.txt/")]
    public void InvalidNamesCannotCreateOutsideCurrentFolder(string name)
        => Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(new(FileOperationKind.CreateFile, [], @"C:\fixture", name)));

    [TestMethod]
    public async Task CreatesAnEmptyFileAndNeverOverwritesExistingItems()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests");
        var root = Path.Combine(parent, Guid.NewGuid().ToString()); Directory.CreateDirectory(root);
        try
        {
            var service = ShellFileOperationService.ForIsolatedTest(root);
            var name = "新規 ファイル.txt"; var path = Path.Combine(root, name);
            Assert.IsFalse((await service.ExecuteAsync(new(FileOperationKind.CreateFile, [], root, name), IntPtr.Zero)).Aborted);
            Assert.IsTrue(File.Exists(path)); Assert.AreEqual(0L, new FileInfo(path).Length);
            await File.WriteAllTextAsync(path, "must survive");
            await Assert.ThrowsExactlyAsync<IOException>(() => service.ExecuteAsync(new(FileOperationKind.CreateFile, [], root, name), IntPtr.Zero));
            Assert.AreEqual("must survive", await File.ReadAllTextAsync(path));
            Directory.CreateDirectory(Path.Combine(root, "existing-folder"));
            Exception? folderCollision = null;
            try { await service.ExecuteAsync(new(FileOperationKind.CreateFile, [], root, "existing-folder"), IntPtr.Zero); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { folderCollision = error; }
            Assert.IsNotNull(folderCollision); Assert.IsTrue(Directory.Exists(Path.Combine(root, "existing-folder")));
        }
        finally
        {
            var checkedRoot = Path.GetFullPath(root);
            Assert.AreEqual(parent, Path.GetDirectoryName(checkedRoot)); Assert.IsTrue(Guid.TryParse(Path.GetFileName(checkedRoot), out _));
            Directory.Delete(checkedRoot, true);
        }
    }
}
