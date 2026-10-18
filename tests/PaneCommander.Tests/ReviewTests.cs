using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ReviewTests
{
    [TestMethod]
    public void FileSizesUseBinaryUnitsAndKeepExactBytes()
    {
        foreach (var (bytes, text) in new[] { (560L, "560 B"), (65536L, "64 KB"), (1048576L, "1 MB"), (4456448L, "4.25 MB"), (1073741824L, "1 GB"), (1099511627776L, "1 TB") })
        { var e = new FileEntry("x", "x", false, bytes, null); Assert.AreEqual(text, e.SizeText); Assert.AreEqual(bytes, e.Size); StringAssert.Contains(e.SizeToolTip, "B"); }
        Assert.AreEqual("—", new FileEntry("x", "x", true, null, null).SizeText); Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FileSize.Format(-1));
    }
    [TestMethod]
    public async Task TitleTracksActivePaneSelectedTabPathAndRoot()
    {
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger());
        await model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\left\Documents"); Assert.AreEqual("Documents", model.WindowTitle);
        await model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\right\Downloads"); Assert.AreEqual("Documents", model.WindowTitle);
        model.Workspace.Toggle(); Assert.AreEqual("Downloads", model.WindowTitle);
        var old = model.Workspace.Right.SelectedTab; var next = model.Workspace.Right.AddTab(); await next.NavigateAsync(@"C:\Work"); Assert.AreEqual("Work", model.WindowTitle);
        await old.NavigateAsync(@"C:\ignored"); Assert.AreEqual("Work", model.WindowTitle);
        await model.ApplySettingsAsync(model.Settings with { TitleDisplay = TitleDisplay.FullPath }); Assert.AreEqual(@"C:\Work", model.WindowTitle);
        await model.ApplySettingsAsync(model.Settings with { TitleDisplay = TitleDisplay.DirectoryName }); await next.NavigateAsync(@"C:\"); Assert.AreEqual(@"C:\", model.WindowTitle);
    }
    [TestMethod]
    public async Task PerPaneColumnsAndCustomPaletteRoundTripWithoutChangingSort()
    {
        var root = CreateRoot(); try
        {
            var layout = new ColumnLayout { Columns = [new("Size", true, 134), new("Name", true, 200), new("Kind", false, 64), new("LastWriteTime", true, 180), new("FileAge", true, 82)] };
            var value = new AppearanceSettings { LeftColumns = layout, TitleDisplay = TitleDisplay.FullPath, RestoreSession = false, CustomSchemes = [ColorSchemes.Capture(new AppearanceSettings { AgeRed = "#AA6677" }, "私の配色")] };
            var store = new JsonSettingsStore(Path.Combine(root, "settings.json")); await store.SaveAsync(value); var read = (await store.LoadAsync()).Settings;
            CollectionAssert.AreEqual(layout.Columns, read.LeftColumns!.Columns); Assert.IsNull(read.RightColumns); Assert.AreEqual("#AA6677", read.CustomSchemes![0].Colors["AgeRed"]); Assert.IsFalse(read.RestoreSession);
            using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger(), settings: read); Assert.AreEqual("Name", model.Workspace.Left.SelectedTab.SortProperty);
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"ShowSize\":false,\"NameWidth\":75}"); var legacy = (await store.LoadAsync()).Settings;
            Assert.IsFalse(legacy.ColumnsFor(PaneSide.Left).Columns.Single(c => c.Key == "Size").Visible); Assert.AreEqual(300d, legacy.ColumnsFor(PaneSide.Right).Columns.Single(c => c.Key == "Name").Width);
        }
        finally { RemoveRoot(root); }
    }
    [TestMethod]
    public void ColorPresetsOnlyChangeColorsAndAgeGradientUsesEditableAnchors()
    {
        var original = new AppearanceSettings { FontSize = 20, RowHeight = 32, RenderingMode = RenderingPreference.Default, LeftColumns = new() };
        var changed = ColorSchemes.Apply(original, ColorSchemes.Presets.Single(s => s.Name == "Graphite")); Assert.AreEqual(20d, changed.FontSize); Assert.AreEqual(32d, changed.RowHeight); Assert.AreSame(original.LeftColumns, changed.LeftColumns); Assert.AreEqual(RenderingPreference.Default, changed.RenderingMode);
        var now = DateTime.Now; Assert.AreEqual(original.AgeRed, FileAge.Present(now, now, original).Color);
        Assert.AreEqual("#A46677", FileAge.Present(now, now, original with { AgeRed = "#A46677" }).Color);
        Assert.AreNotEqual(ColorSchemes.Presets[0].Colors["Background"], changed.Background);
        Assert.IsTrue((original with { LeftColumns = new() { Columns = [new("Name")] } }).Validate().Count > 0);
        Assert.IsTrue((original with { AgeBlue = "invalid" }).Validate().Count > 0);
    }
    [TestMethod]
    public async Task FavoritePathsAreCaseAndSeparatorInsensitive()
    {
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger()); await model.AddFavoriteAsync("first", @"C:\Work\日本語"); await model.AddFavoriteAsync("duplicate", @"c:\work\日本語\"); Assert.AreEqual(1, model.Favorites.Count); Assert.AreEqual("first", model.Favorites[0].Name);
    }
    [TestMethod]
    public void DropIntentAndInvalidDestinationsDoNotLoseFiles()
    {
        Assert.AreEqual(DropIntent.Move, DropPolicy.Intent([@"C:\a\x"], @"C:\b", false, false)); Assert.AreEqual(DropIntent.Copy, DropPolicy.Intent([@"C:\a\x"], @"D:\b", false, false));
        Assert.AreEqual(DropIntent.Copy, DropPolicy.Intent([@"C:\a\x"], @"C:\b", true, false)); Assert.AreEqual(DropIntent.Move, DropPolicy.Intent([@"C:\a\x"], @"D:\b", false, true)); Assert.AreEqual(DropIntent.None, DropPolicy.Intent([@"C:\a\x"], @"C:\b", true, true));
        foreach (var dest in new[] { @"C:\a", @"C:\a\x", @"C:\a\x\child" }) Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.Validate(new(FileOperationKind.Move, [@"C:\a\x"], dest)));
        DropPolicy.Validate(new(FileOperationKind.Move, [@"C:\a\x"], @"C:\a", "renamed"));
        DropPolicy.Validate(new(FileOperationKind.Copy, [@"C:\a\x"], @"C:\a"));
        Assert.IsFalse(DropCompletion.RequiresSourceDeletion(2, null, null)); Assert.IsFalse(DropCompletion.RequiresSourceDeletion(2, 0, 2)); Assert.IsFalse(DropCompletion.RequiresSourceDeletion(2, 2, 1)); Assert.IsTrue(DropCompletion.RequiresSourceDeletion(2, 2, 2));
    }
    [TestMethod]
    public async Task RealJunctionNavigationKeepsLogicalHistoryAndDetectsCycles()
    {
        var root = CreateRoot(); try
        {
            var target = Path.Combine(root, "target"); Directory.CreateDirectory(Path.Combine(target, "child")); await File.WriteAllTextAsync(Path.Combine(target, "hello.txt"), "hello"); var link = Path.Combine(root, "alias"); Junction(link, target);
            var fs = new FileSystemService(); var snapshot = await fs.ReadFolderAsync(link, CancellationToken.None); Assert.AreEqual(link, snapshot.Path); Assert.AreEqual(2, snapshot.Entries.Count);
            using var tab = new TabState(root, fs, new FakeLogger()); await tab.RefreshAsync(); tab.SelectedEntry = tab.Entries.Single(e => e.Name == "alias"); Assert.AreEqual(FileLinkKind.Junction, tab.SelectedEntry.LinkType); await tab.OpenSelectedAsync(); Assert.AreEqual(link, tab.CurrentPath); await tab.NavigateAsync(Path.Combine(link, "child")); await tab.UpAsync(); Assert.AreEqual(link, tab.CurrentPath); await tab.BackAsync(); Assert.AreEqual(Path.Combine(link, "child"), tab.CurrentPath);
            var broken = Path.Combine(root, "broken"); Junction(broken, Path.Combine(root, "absent")); await Assert.ThrowsAsync<IOException>(() => fs.ReadFolderAsync(broken, CancellationToken.None));
            var a = Path.Combine(root, "cycle-a"); var b = Path.Combine(root, "cycle-b"); Junction(a, b); Junction(b, a); await Assert.ThrowsAsync<IOException>(() => fs.ReadFolderAsync(a, CancellationToken.None));
            foreach (var item in new[] { link, broken, a, b }) Directory.Delete(item);
            Assert.IsTrue(File.Exists(Path.Combine(target, "hello.txt")), "リンクの片付けで実体を削除しない");
        }
        finally { RemoveRoot(root); }
    }
    [TestMethod]
    public async Task FileOperationsWaitBeforeSessionSaveAndRecoverAfterErrors()
    {
        var files = new FakeFileSystem(); var operations = new DelayedOperations(); var session = new SavedSession(); using var model = new MainViewModel(files, new FakeLogger(), sessionStore: session, fileOperations: operations);
        await model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\left"); await model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\right"); model.GetSelectedPaths = () => [@"C:\left\file"]; model.RequestFileOperation = _ => Task.CompletedTask;
        var run = model.ExecuteFileOperationAsync(new(FileOperationKind.Copy, [@"C:\left\file"], @"C:\right"), IntPtr.Zero); Assert.IsTrue(model.IsFileOperationRunning); Assert.IsFalse(model.Functions[4].Command.CanExecute(null));
        var save = model.SaveSessionAsync(new()); Assert.IsFalse(save.IsCompleted); operations.Completion.SetResult(new(false)); await run; await save; Assert.IsNotNull(session.Value); Assert.IsFalse(model.IsFileOperationRunning); Assert.IsTrue(model.Functions[4].Command.CanExecute(null));
        operations.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously); run = model.ExecuteFileOperationAsync(new(FileOperationKind.Copy, [@"C:\left\file"], @"C:\right"), IntPtr.Zero); operations.Completion.SetException(new IOException("injected I/O failure")); await Assert.ThrowsExactlyAsync<IOException>(() => run); Assert.IsFalse(model.IsFileOperationRunning); Assert.AreEqual(@"C:\left", model.Workspace.Left.SelectedTab.CurrentPath); Assert.IsTrue(model.Functions[4].Command.CanExecute(null));
    }
    [TestMethod]
    public async Task SymbolicLinksResolveAndMoveWithoutDeletingTheirTargets()
    {
        var root = CreateRoot(); try
        {
            var target = Path.Combine(root, "target.txt"); await File.WriteAllTextAsync(target, "keep target"); var link = Path.Combine(root, "source-link.txt");
            try { File.CreateSymbolicLink(link, target); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Assert.Inconclusive("シンボリックリンク作成失敗: " + ex.ToString()); return; }
            Assert.AreEqual(target, FileSystemService.ResolveLocalTarget(link)); var fs = new FileSystemService(); var entry = (await fs.ReadFolderAsync(root, CancellationToken.None)).Entries.Single(e => e.FullPath == link); Assert.AreEqual(FileLinkKind.SymbolicLink, entry.LinkType);
            var to = Path.Combine(root, "to"); Directory.CreateDirectory(to); var shell = ShellFileOperationService.ForIsolatedTest(root);
            Assert.IsFalse((await shell.ExecuteAsync(new(FileOperationKind.Copy, [link], to), IntPtr.Zero)).Aborted); var copied = Path.Combine(to, "source-link.txt"); Assert.AreEqual("keep target", await File.ReadAllTextAsync(copied));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "review-symlink-copy-observation.json"), System.Text.Json.JsonSerializer.Serialize(new { CopiedIsSymbolicLink = File.GetAttributes(copied).HasFlag(FileAttributes.ReparsePoint), TargetContentsPreserved = File.Exists(target) }));
            await shell.ExecuteAsync(new(FileOperationKind.Move, [link], to, "moved-link.txt"), IntPtr.Zero); Assert.IsTrue(File.GetAttributes(Path.Combine(to, "moved-link.txt")).HasFlag(FileAttributes.ReparsePoint)); Assert.AreEqual("keep target", await File.ReadAllTextAsync(target));
            await shell.ExecuteAsync(new(FileOperationKind.Delete, [Path.Combine(to, "moved-link.txt")], Recycle: false), IntPtr.Zero); Assert.AreEqual("keep target", await File.ReadAllTextAsync(target));
        }
        finally { RemoveRoot(root); }
    }
    private sealed class DelayedOperations : IFileOperationService { public TaskCompletionSource<FileOperationResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously); public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default) => Completion.Task; }
    private sealed class SavedSession : ISessionStore { public SessionDocument? Value; public Task<StateLoadResult<SessionDocument>> LoadAsync(CancellationToken token = default) => Task.FromResult(new StateLoadResult<SessionDocument>(Value)); public Task SaveAsync(SessionDocument value, CancellationToken token = default) { Value = value; return Task.CompletedTask; } }
    [TestMethod]
    public async Task ShellCopyMoveRenameAndCreateUseRealFiles()
    {
        var root = CreateRoot(); try
        {
            var from = Path.Combine(root, "from"); var to = Path.Combine(root, "to"); Directory.CreateDirectory(from); Directory.CreateDirectory(to);
            var source = Path.Combine(from, "日本語.txt"); await File.WriteAllTextAsync(source, "unchanged contents"); var shell = ShellFileOperationService.ForIsolatedTest(root);
            Assert.IsFalse((await shell.ExecuteAsync(new(FileOperationKind.Copy, [source], to), IntPtr.Zero)).Aborted); Assert.IsTrue(File.Exists(source)); Assert.AreEqual("unchanged contents", await File.ReadAllTextAsync(Path.Combine(to, "日本語.txt")));
            await shell.ExecuteAsync(new(FileOperationKind.Copy, [source], from), IntPtr.Zero); Assert.AreEqual(2, Directory.GetFiles(from).Length, "同じフォルダのコピーは別名を生成し元を保持");
            Assert.IsFalse((await shell.ExecuteAsync(new(FileOperationKind.Move, [source], to, "renamed.txt"), IntPtr.Zero)).Aborted); Assert.IsFalse(File.Exists(source)); Assert.AreEqual("unchanged contents", await File.ReadAllTextAsync(Path.Combine(to, "renamed.txt")));
            await shell.ExecuteAsync(new(FileOperationKind.Move, [Path.Combine(to, "renamed.txt")], to, "again.txt"), IntPtr.Zero); Assert.IsTrue(File.Exists(Path.Combine(to, "again.txt")));
            await shell.ExecuteAsync(new(FileOperationKind.CreateDirectory, [], to, "new directory"), IntPtr.Zero); Assert.IsTrue(Directory.Exists(Path.Combine(to, "new directory")));
            var target = Path.Combine(root, "real target"); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "must survive"); var link = Path.Combine(from, "directory link"); Junction(link, target);
            var copied = await shell.ExecuteAsync(new(FileOperationKind.Copy, [link], to), IntPtr.Zero); var copyPath = Path.Combine(to, "directory link");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "review-link-copy-observation.json"), System.Text.Json.JsonSerializer.Serialize(new { JunctionCopyAborted = copied.Aborted, DestinationExists = Directory.Exists(copyPath), DestinationIsReparse = Directory.Exists(copyPath) && File.GetAttributes(copyPath).HasFlag(FileAttributes.ReparsePoint), TargetContentsPreserved = File.Exists(Path.Combine(target, "keep.txt")) }));
            Assert.IsTrue(File.Exists(Path.Combine(target, "keep.txt")));
            Assert.AreEqual(FileLinkKind.Junction, LinkMetadata.Kind(copyPath)); Assert.AreEqual(LinkObjectOperations.Read(link).Target, LinkObjectOperations.Read(copyPath).Target);
            Directory.CreateDirectory(Path.Combine(target,"child"));
            await Assert.ThrowsExactlyAsync<IOException>(()=>shell.ExecuteAsync(new(FileOperationKind.Copy,[target],Path.Combine(link,"child")),IntPtr.Zero));
            await shell.ExecuteAsync(new(FileOperationKind.Move,[link],to,"moved junction"),IntPtr.Zero);var movedLink=Path.Combine(to,"moved junction");Assert.IsTrue(File.GetAttributes(movedLink).HasFlag(FileAttributes.ReparsePoint));Assert.IsFalse(Directory.Exists(link));
            await shell.ExecuteAsync(new(FileOperationKind.Delete, [movedLink], Recycle: false), IntPtr.Zero); Assert.IsFalse(Directory.Exists(movedLink)); Assert.AreEqual("must survive", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")), "完全削除はジャンクション本体だけを削除");
            await shell.ExecuteAsync(new(FileOperationKind.Delete, [Path.Combine(to, "again.txt")], Recycle: false), IntPtr.Zero); Assert.IsFalse(File.Exists(Path.Combine(to, "again.txt")));
        }
        finally { RemoveRoot(root); }
    }
    private static string CreateRoot() { var root = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static void RemoveRoot(string root) { var safe = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests")) + Path.DirectorySeparatorChar; if (!Path.GetFullPath(root).StartsWith(safe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup"); LinkObjectOperations.Delete(root, CancellationToken.None); }
    private static void Junction(string path, string target) { var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in new[] { "/c", "mklink", "/J", path, target }) info.ArgumentList.Add(arg); using var process = Process.Start(info)!; Assert.IsTrue(process.WaitForExit(5000)); Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd()); }
}
