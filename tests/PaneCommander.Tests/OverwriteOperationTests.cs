using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class OverwriteOperationTests
{
    [TestMethod]
    public void ConfirmationIsSuppressedOnlyForExplicitCopyOrMove()
    {
        foreach (var kind in new[] { FileOperationKind.Copy, FileOperationKind.Move })
        {
            var request = new FileOperationRequest(kind, [@"C:\source\file.txt"], @"C:\target", "file.txt");
            Assert.AreEqual(0u, ShellFileOperationService.GetOperationFlags(request) & 0x10);
            Assert.AreEqual(0x10u, ShellFileOperationService.GetOperationFlags(request with { Overwrite = true }) & 0x10);
            DropPolicy.Validate(request with { Overwrite = true });
        }
        foreach (var kind in new[] { FileOperationKind.Delete, FileOperationKind.CreateDirectory, FileOperationKind.CreateFile })
        {
            var request = new FileOperationRequest(kind, [@"C:\source\file.txt"], @"C:\target", Overwrite: true);
            Assert.AreEqual(0u, ShellFileOperationService.GetOperationFlags(request) & 0x10);
            Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(request));
        }
        var drop = DropPolicy.CreateRequest([@"C:\source\file.txt"], @"C:\target", true, false)!;
        Assert.IsFalse(drop.Overwrite, "ドラッグで上書きの同意を暗黙に与えない");
    }

    [TestMethod]
    [DataRow(FileOperationKind.Copy)]
    [DataRow(FileOperationKind.Move)]
    public async Task ExplicitOverwriteReplacesNamedFileAndMergedFolderContents(FileOperationKind kind)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var from = Directory.CreateDirectory(Path.Combine(root, "from")).FullName;
            var to = Directory.CreateDirectory(Path.Combine(root, "to")).FullName;
            var source = Path.Combine(from, "source.txt");
            var target = Path.Combine(to, "named.txt");
            await File.WriteAllTextAsync(source, "new content");
            await File.WriteAllTextAsync(target, "old content");
            var folder = Directory.CreateDirectory(Path.Combine(from, "folder")).FullName;
            var targetFolder = Directory.CreateDirectory(Path.Combine(to, "folder")).FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, "child.txt"), "new child");
            await File.WriteAllTextAsync(Path.Combine(targetFolder, "child.txt"), "old child");
            await File.WriteAllTextAsync(Path.Combine(targetFolder, "keep.txt"), "unrelated");
            // Unlike older isolated tests, this uses the production confirmation flag.
            var service = ShellFileOperationService.ForIsolatedTest(root, useConfirmationPolicy: true);
            Assert.IsFalse((await service.ExecuteAsync(new(kind, [source], to, "named.txt", Overwrite: true), IntPtr.Zero)).Aborted);
            Assert.AreEqual("new content", await File.ReadAllTextAsync(target));
            Assert.AreEqual(kind == FileOperationKind.Copy, File.Exists(source));
            Assert.IsFalse((await service.ExecuteAsync(new(kind, [folder], to, Overwrite: true), IntPtr.Zero)).Aborted);
            Assert.AreEqual("new child", await File.ReadAllTextAsync(Path.Combine(targetFolder, "child.txt")));
            Assert.AreEqual("unrelated", await File.ReadAllTextAsync(Path.Combine(targetFolder, "keep.txt")));
            Assert.AreEqual(kind == FileOperationKind.Copy, Directory.Exists(folder));

            var localSource = Path.Combine(to, "local.txt");
            await File.WriteAllTextAsync(localSource, "same-folder source");
            Assert.IsFalse((await service.ExecuteAsync(SameFolderOperation.Create(kind, localSource, "named.txt") with { Overwrite = true }, IntPtr.Zero)).Aborted);
            Assert.AreEqual("same-folder source", await File.ReadAllTextAsync(target));
        }
        finally
        {
            var parent = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests");
            if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(root), out _)) throw new InvalidOperationException("Unsafe cleanup");
            Directory.Delete(root, true);
        }
    }
}
