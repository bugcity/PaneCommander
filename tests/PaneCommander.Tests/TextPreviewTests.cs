using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class TextPreviewTests
{
    private string directory = null!;
    [TestInitialize] public void Setup() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaneCommander-preview-" + Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public async Task UtfTextPreviewPreservesContentsAndDoesNotModifyFile()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            var path = Path.Combine(directory, "日本語.txt");
            const string text = "日本語の読み取り専用 preview\r\nline2\tend";
            await File.WriteAllTextAsync(path, text, encoding);
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            Assert.AreEqual(text, await TextPreviewReader.ReadAsync(path));
            CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
    }
    [TestMethod]
    public async Task BinaryAndInvalidEncodingAreRejectedWithoutChangingSource()
    {
        foreach (var bytes in new byte[][] { [0, 1, 2, 3], [0xFF, 0xA5, 0x80], [0xFF, 0xFE, 0x41] })
        {
            var path = Path.Combine(directory, "unsupported.bin"); await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => TextPreviewReader.ReadAsync(path));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
    }
    [TestMethod]
    public async Task EmptyAndExactLimitAreSupportedButOversizeIsRejected()
    {
        var path = Path.Combine(directory, "limit.txt"); await File.WriteAllTextAsync(path, "");
        Assert.AreEqual("", await TextPreviewReader.ReadAsync(path));
        await File.WriteAllTextAsync(path, new string('a', TextPreviewReader.MaximumBytes));
        Assert.AreEqual(TextPreviewReader.MaximumBytes, (await TextPreviewReader.ReadAsync(path)).Length);
        await File.AppendAllTextAsync(path, "b");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => TextPreviewReader.ReadAsync(path));
    }
    [TestMethod]
    public async Task MissingAndCancelledReadsDoNotCreateAFile()
    {
        var path = Path.Combine(directory, "missing.txt");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => TextPreviewReader.ReadAsync(path));
        Assert.IsFalse(File.Exists(path));
        await File.WriteAllTextAsync(path, "test"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TextPreviewReader.ReadAsync(path, cancel.Token));
        Assert.AreEqual("test", await File.ReadAllTextAsync(path));
    }
    [TestMethod]
    public async Task PreviewCommandUsesActiveSingleFileAndNeverDispatchesFileOperation()
    {
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger()); await model.Workspace.InitializeAsync();
        string? opened = null; model.RequestPreview = path => { opened = path; return Task.CompletedTask; };
        var file = new PaneCommander.Core.FileEntry(Path.Combine(directory, "file.txt"), "file.txt", false, 5, DateTime.Now);
        model.Workspace.ActivePane.SelectedTab.SelectedEntry = file;
        model.GetSelectedPaths = () => [file.FullPath];
        Assert.IsTrue(model.Functions[2].Implemented); Assert.IsTrue(model.Functions[2].Command.CanExecute(null));
        model.Functions[2].Command.Execute(null); Assert.AreEqual(file.FullPath, opened);
        model.GetSelectedPaths = () => [file.FullPath, Path.Combine(directory, "second.txt")]; Assert.IsFalse(model.Functions[2].Command.CanExecute(null));
        model.GetSelectedPaths = () => []; Assert.IsFalse(model.Functions[2].Command.CanExecute(null));
        model.GetSelectedPaths = () => [directory]; model.Workspace.ActivePane.SelectedTab.SelectedEntry = file with { IsDirectory = true };
        Assert.IsFalse(model.Functions[2].Command.CanExecute(null));
        model.Workspace.Toggle(); Assert.IsFalse(model.Functions[2].Command.CanExecute(null));
    }
}
