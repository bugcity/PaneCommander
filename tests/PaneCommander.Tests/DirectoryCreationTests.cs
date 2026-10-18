using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class DirectoryCreationTests
{
    [TestMethod]
    public void RelativeHierarchyAcceptsBothSeparatorsAndDoesNotChangeRenameRules()
    {
        CollectionAssert.AreEqual(new[] { "親", "子 フォルダー", "孫" }, DirectoryCreation.ParseRelativePath(@"親\子 フォルダー/孫\"));
        DropPolicy.Validate(new(FileOperationKind.CreateDirectory, [], @"C:\fixture", @"親\子\孫"));
        Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(new(FileOperationKind.Move, [@"C:\fixture\file.txt"], @"C:\fixture", @"親\子")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(@"..\outside")]
    [DataRow(@"parent\..\outside")]
    [DataRow(@"parent\.\child")]
    [DataRow(@"C:\outside")]
    [DataRow(@"\\server\share\outside")]
    [DataRow(@"parent\\child")]
    [DataRow(@"parent\bad:name")]
    [DataRow(@"parent\trailing.")]
    [DataRow(@"parent\CON.txt")]
    public void InvalidHierarchyIsRejectedBeforeCreation(string input)
        => Assert.ThrowsExactly<ArgumentException>(() => DirectoryCreation.ParseRelativePath(input));

    [TestMethod]
    public async Task CreatesMissingLevelsReusesExistingFoldersAndPreservesFileConflicts()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests");
        var root = Path.Combine(parent, Guid.NewGuid().ToString()); Directory.CreateDirectory(root);
        try
        {
            var service = ShellFileOperationService.ForIsolatedTest(root);
            var existing = Path.Combine(root, "existing"); Directory.CreateDirectory(existing);
            var sentinel = Path.Combine(existing, "keep.txt"); await File.WriteAllTextAsync(sentinel, "keep");
            foreach (var name in new[] { @"親\子 フォルダー/孫", @"existing\child\leaf", @"existing\child\leaf", "single" })
                Assert.IsFalse((await service.ExecuteAsync(new(FileOperationKind.CreateDirectory, [], root, name), IntPtr.Zero)).Aborted);
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "親", "子 フォルダー", "孫")));
            Assert.IsTrue(Directory.Exists(Path.Combine(existing, "child", "leaf")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "single")));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
            await Assert.ThrowsExactlyAsync<IOException>(() => service.ExecuteAsync(new(FileOperationKind.CreateDirectory, [], root, @"existing\keep.txt\child"), IntPtr.Zero));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            var checkedRoot = Path.GetFullPath(root);
            Assert.AreEqual(parent, Path.GetDirectoryName(checkedRoot)); Assert.IsTrue(Guid.TryParse(Path.GetFileName(checkedRoot), out _));
            Directory.Delete(checkedRoot, true);
        }
    }
}
