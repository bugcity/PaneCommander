using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FileClipboardTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyAndCutPublishSelectedPathsWithoutExecutingFileOperations(bool cut)
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new[] { Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt") };
            foreach (var path in paths) await File.WriteAllTextAsync(path, "test");
            var clipboard = new MemoryClipboard();
            await FileClipboard.ExecuteAsync(cut ? FileClipboardOperation.Cut : FileClipboardOperation.Copy, root, paths, (IntPtr)123,
                (_, _) => throw new AssertFailedException("コピー・切り取り時点でファイルを操作しない"), clipboard);
            CollectionAssert.AreEqual(paths, clipboard.Paths);
            Assert.AreEqual(cut ? "cut" : "copy", clipboard.Verb);
            Assert.AreEqual((IntPtr)123, clipboard.Owner);
            Assert.IsTrue(paths.All(File.Exists));
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task PasteUsesTransferIntentAndReportsOnlyCompletedOperations(bool move, bool abort)
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-paste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(source, "test");
            var destination = Path.Combine(root, "destination"); Directory.CreateDirectory(destination);
            var clipboard = new MemoryClipboard { Paths = [source], Kind = move ? FileOperationKind.Move : FileOperationKind.Copy };
            FileOperationRequest? request = null;
            await FileClipboard.ExecuteAsync(FileClipboardOperation.Paste, destination, [], (IntPtr)123,
                (value, owner) => { request = value; Assert.AreEqual((IntPtr)123, owner); return Task.FromResult(new FileOperationResult(abort)); }, clipboard);
            Assert.IsNotNull(request);
            Assert.AreEqual(clipboard.Kind, request.Kind); Assert.AreEqual(destination, request.Destination);
            CollectionAssert.AreEqual(new[] { source }, request.Sources);
            Assert.AreEqual(!abort, clipboard.Reported); Assert.IsTrue(clipboard.Disposed);
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedClipboardStopsBeforeFileOperations(bool changeAtOpen)
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-clipboard-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "a.txt"); await File.WriteAllTextAsync(source, "test");
            var clipboard = new MemoryClipboard { Paths = [source], ChangeOnRead = !changeAtOpen, ChangeOnOpen = changeAtOpen };
            await Assert.ThrowsExactlyAsync<IOException>(() => FileClipboard.ExecuteAsync(FileClipboardOperation.Paste, root, [], IntPtr.Zero,
                (_, _) => throw new AssertFailedException("変更後のクリップボードで操作しない"), clipboard));
            Assert.IsFalse(clipboard.Reported);
            Assert.AreEqual(changeAtOpen, clipboard.Disposed);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NonFileClipboardDoesNotPerformAnOperation()
    {
        var clipboard = new MemoryClipboard();
        await FileClipboard.ExecuteAsync(FileClipboardOperation.Paste, Path.GetTempPath(), [], IntPtr.Zero,
            (_, _) => throw new AssertFailedException("テキストや画像をファイルとして貼り付けない"), clipboard);
        Assert.IsFalse(clipboard.Reported); Assert.IsFalse(clipboard.Disposed);
    }

    private sealed class MemoryClipboard : IFileClipboardBackend, IFileClipboardTransfer
    {
        public uint Sequence { get; private set; } = 1;
        public string[] Paths = [];
        public string? Verb;
        public IntPtr Owner;
        public FileOperationKind Kind { get; set; } = FileOperationKind.Copy;
        public bool Reported, Disposed, ChangeOnRead, ChangeOnOpen;
        public string[] ReadPaths() { if (ChangeOnRead) Sequence++; return Paths; }
        public void SetFiles(string directory, string[] paths, IntPtr owner, string verb) { Paths = paths; Owner = owner; Verb = verb; }
        public IFileClipboardTransfer OpenTransfer() { if (ChangeOnOpen) Sequence++; return this; }
        public void ReportCompleted() => Reported = true;
        public void Dispose() => Disposed = true;
    }
}
