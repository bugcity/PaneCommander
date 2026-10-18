using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FileSystemTests
{
    private string root = null!;
    private readonly FileSystemService service = new();
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "PaneCommander.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }
    [TestCleanup]
    public void Cleanup()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PaneCommander.Tests")) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid cleanup path");
        Directory.Delete(resolved, true);
    }

    [TestMethod]
    public async Task EnumeratesRealUnicodePathsAndMetadataWithDirectoriesFirst()
    {
        Directory.CreateDirectory(Path.Combine(root, "日本語 空白 📂"));
        var path = Path.Combine(root, "ファイル 空白.txt");
        await File.WriteAllTextAsync(path, "hello");
        File.SetLastWriteTime(path, new DateTime(2025, 1, 2, 3, 4, 6));
        var result = await service.ReadFolderAsync(root, CancellationToken.None);
        Assert.AreEqual(root, result.Path);
        Assert.AreEqual(2, result.Entries.Count);
        Assert.IsTrue(result.Entries[0].IsDirectory);
        Assert.AreEqual("日本語 空白 📂", result.Entries[0].Name);
        Assert.AreEqual(path, result.Entries[1].FullPath);
        Assert.AreEqual(5L, result.Entries[1].Size);
        Assert.AreEqual(new DateTime(2025, 1, 2, 3, 4, 6), result.Entries[1].LastWriteTime);
    }

    [TestMethod]
    public async Task EmptyFolderIsValidAndMissingFolderFails()
    {
        Assert.AreEqual(0, (await service.ReadFolderAsync(root, CancellationToken.None)).Entries.Count);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.ReadFolderAsync(Path.Combine(root, "missing"), CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidAndDevicePathsAreRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadFolderAsync("", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadFolderAsync("relative", CancellationToken.None));
        Assert.AreEqual(@"\\server\share", FileSystemService.NormalizePath(@"\\server\share"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.ReadFolderAsync(@"\\?\C:\", CancellationToken.None));
    }

    [TestMethod]
    public async Task PreCancelledEnumerationStops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.ReadFolderAsync(root, cancellation.Token));
    }

    [TestMethod]
    public async Task LargeRealFolderHasCompleteListing()
    {
        for (var i = 0; i < 1200; i++) await File.WriteAllTextAsync(Path.Combine(root, $"item-{i:D4}.txt"), "");
        var result = await service.ReadFolderAsync(root, CancellationToken.None);
        Assert.AreEqual(1200, result.Entries.Count);
        Assert.AreEqual("item-0000.txt", result.Entries[0].Name);
        Assert.AreEqual("item-1199.txt", result.Entries[^1].Name);
    }

    [TestMethod]
    public void DiagnosticLogDoesNotIncludePrivateTextAndLogFailureIsNonfatal()
    {
        var logs = Path.Combine(root, "logs");
        new ErrorLogger(logs).Log("フォルダ列挙", new IOException("PRIVATE-FILE-CONTENT C:\\private\\name"));
        var text = File.ReadAllText(Directory.GetFiles(logs).Single());
        StringAssert.Contains(text, "IOException");
        Assert.IsFalse(text.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private", StringComparison.Ordinal));
        var occupied = Path.Combine(root, "occupied");
        File.WriteAllText(occupied, "");
        new ErrorLogger(occupied).Log("test", new IOException());
    }
}
