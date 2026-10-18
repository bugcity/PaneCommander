using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class M3Tests
{
    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "PaneCommander M3 日本語", name);
    private static SessionDocument Session() => new()
    {
        Left = new() { Tabs = [new(Folder("left0")), new(Folder("left1"), "Size", true), new(Folder("left2"))], SelectedIndex = 1 },
        Right = new() { Tabs = [new(Folder("right0")), new(Folder("right1"))], SelectedIndex = 1 }, ActiveSide = PaneSide.Right,
        Layout = new() { Left = -100, Top = 80, Width = 1400, Height = 900, LeftPaneFraction = .6, SidebarWidth = 210, Maximized = true }
    };

    [TestMethod]
    public async Task RenderingDefaultLegacyJsonAndBothPreferencesRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new JsonSettingsStore(path);
            Assert.AreEqual(RenderingPreference.SoftwareOnly, (await store.LoadAsync()).Settings.RenderingMode);
            await File.WriteAllTextAsync(path, "{\"FontSize\":16}");
            Assert.AreEqual(RenderingPreference.SoftwareOnly, (await store.LoadAsync()).Settings.RenderingMode, "既存JSONも新しい既定値を使用");
            foreach (var mode in Enum.GetValues<RenderingPreference>())
            {
                await store.SaveAsync(new() { RenderingMode = mode, ShowFavorites = false });
                var loaded = await store.LoadAsync(); Assert.IsNull(loaded.Error); Assert.AreEqual(mode, loaded.Settings.RenderingMode);
                Assert.IsFalse(loaded.Settings.ShowFavorites);
                StringAssert.Contains(await File.ReadAllTextAsync(path), mode.ToString());
            }
            await File.WriteAllTextAsync(path, "{\"RenderingMode\":\"unknown\"}");
            Assert.IsNotNull((await store.LoadAsync()).Error);
            await File.WriteAllTextAsync(path, "{\"RenderingMode\":123}");
            Assert.IsNotNull((await store.LoadAsync()).Error);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task RenderingChangeIsPendingAndNewViewModelUsesSavedPreference()
    {
        var store = new MemorySettingsStore();
        using var model = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), store);
        Assert.AreEqual(RenderingPreference.SoftwareOnly, model.StartupRenderingMode);
        var actualBefore = System.Windows.Media.RenderOptions.ProcessRenderMode;
        await model.ApplySettingsAsync(model.Settings with { RenderingMode = RenderingPreference.Default });
        Assert.AreEqual(actualBefore, System.Windows.Media.RenderOptions.ProcessRenderMode, "保存時に稼働中の描画方式を変更しない");
        Assert.IsTrue(model.RenderingRestartRequired);
        using var restarted = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), store, (await store.LoadAsync()).Settings);
        Assert.AreEqual(RenderingPreference.Default, restarted.StartupRenderingMode); Assert.IsFalse(restarted.RenderingRestartRequired);
        await model.ApplySettingsAsync(model.Settings with { RenderingMode = RenderingPreference.SoftwareOnly });
        Assert.IsFalse(model.RenderingRestartRequired);
    }
    [TestMethod]
    public async Task SessionRestoresOrderSelectionModesAndLoadsOnlyVisibleTabs()
    {
        var service = new TrackingFileSystem(); using var workspace = new WorkspaceState(Folder("home"), service, new FakeLogger());
        var saved = Session(); workspace.Restore(saved);
        Assert.AreEqual(0, service.Reads.Count);
        await workspace.InitializeAsync();
        Assert.AreEqual(2, service.Reads.Count); Assert.AreEqual(PaneSide.Right, workspace.ActiveSide);
        Assert.AreEqual(saved.Left.Tabs[1].Path, workspace.Left.SelectedTab.CurrentPath);
        Assert.AreEqual("Size", workspace.Left.SelectedTab.SortProperty);
        Assert.AreEqual(0, workspace.Left.Tabs[0].Entries.Count);
        var again = workspace.Capture(saved.Layout);
        CollectionAssert.AreEqual(saved.Left.Tabs, again.Left.Tabs); CollectionAssert.AreEqual(saved.Right.Tabs, again.Right.Tabs);
        Assert.AreEqual(saved.Layout, again.Layout);
        workspace.Left.SelectedTab = workspace.Left.Tabs[0];
        await workspace.Left.SelectedTab.EnsureLoadedAsync();
        Assert.AreEqual(3, service.Reads.Count);
    }
    [TestMethod]
    public async Task MissingAndDeniedRestorePathsRemainRecoverable()
    {
        var service = new TrackingFileSystem { Missing = Folder("left1"), Denied = Folder("right1") };
        using var workspace = new WorkspaceState(Folder("home"), service, new FakeLogger()); workspace.Restore(Session());
        await workspace.InitializeAsync();
        Assert.AreEqual(service.Missing, workspace.Left.SelectedTab.CurrentPath); Assert.AreEqual(service.Denied, workspace.Right.SelectedTab.CurrentPath);
        Assert.IsFalse(workspace.Left.SelectedTab.NavigationSucceeded); Assert.IsFalse(workspace.Right.SelectedTab.NavigationSucceeded);
        StringAssert.Contains(workspace.Left.SelectedTab.Status, "移動できません"); StringAssert.Contains(workspace.Right.SelectedTab.Status, "移動できません");
        await workspace.Right.SelectedTab.NavigateAsync(Folder("fixed")); Assert.IsTrue(workspace.Right.SelectedTab.NavigationSucceeded);
        Assert.AreEqual(service.Missing, workspace.Left.SelectedTab.CurrentPath);
    }
    [TestMethod]
    public async Task FavoritesPersistNamesOrderAndOpenOnlyActiveTab()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonFavoritesStore(Path.Combine(directory, "favorites.json"));
            using var model = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), favoritesStore: store);
            await model.Workspace.InitializeAsync(); var left = model.Workspace.Left.SelectedTab.CurrentPath;
            await model.AddFavoriteAsync("資料 日本語", Folder("one")); await model.AddFavoriteAsync("two", Folder("two"));
            var first = model.Favorites[0]; await model.RenameFavoriteAsync(first, "新しい表示名");
            await model.MoveFavoriteAsync(model.Favorites[1], -1);
            model.Workspace.Activate(PaneSide.Right); model.OpenFavorite.Execute(model.Favorites[0]);
            Assert.AreEqual(Folder("two"), model.Workspace.Right.SelectedTab.CurrentPath); Assert.AreEqual(left, model.Workspace.Left.SelectedTab.CurrentPath);
            var loaded = await store.LoadAsync(); Assert.IsNull(loaded.Error); Assert.IsNotNull(loaded.Value);
            Assert.AreEqual("two", loaded.Value.Items[0].Name); Assert.AreEqual("新しい表示名", loaded.Value.Items[1].Name);
            await model.RemoveFavoriteAsync(model.Favorites[0]); Assert.AreEqual(1, (await store.LoadAsync()).Value!.Items.Length);
            Assert.AreEqual(Folder("one"), model.Favorites[0].Path, "名前変更はパスを変更しない");
            Assert.IsTrue(File.Exists(Path.Combine(directory, "favorites.json.bak")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task FailedFavoriteSaveKeepsCurrentCollectionAndSelection()
    {
        var favorite = new FavoriteFolder(Guid.NewGuid(), "keep", Folder("keep"));
        using var model = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), favoritesStore: new FailingFavoritesStore(), savedFavorites: new() { Items = [favorite] });
        model.SelectedFavorite = favorite;
        await Assert.ThrowsExceptionAsync<IOException>(() => model.RemoveFavoriteAsync(favorite));
        Assert.AreEqual(1, model.Favorites.Count); Assert.AreSame(favorite, model.SelectedFavorite);
        Assert.IsTrue(model.RemoveFavorite.CanExecute(null));
    }
    [TestMethod]
    public async Task SessionJsonRoundTripCorruptionBoundsAndCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session.json"); var store = new JsonSessionStore(path); var saved = Session();
            Assert.IsNull((await store.LoadAsync()).Value);
            await store.SaveAsync(saved); var loaded = await store.LoadAsync(); Assert.IsNull(loaded.Error);
            Assert.AreEqual(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(loaded.Value));
            using var token = new CancellationTokenSource(); token.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => store.SaveAsync(saved with { ActiveSide = PaneSide.Left }, token.Token));
            Assert.AreEqual(PaneSide.Right, (await store.LoadAsync()).Value!.ActiveSide);
            foreach (var invalid in new[] { "{", "{\"Version\":99}", "{\"Left\":null}", JsonSerializer.Serialize(saved with { Layout = saved.Layout with { Width = 10 } }), JsonSerializer.Serialize(saved with { Left = saved.Left with { SelectedIndex = 99 } }) })
            { await File.WriteAllTextAsync(path, invalid); var bad = await store.LoadAsync(); Assert.IsNull(bad.Value); Assert.IsNotNull(bad.Error); }
            await File.WriteAllBytesAsync(path, new byte[1_048_577]); Assert.IsNotNull((await store.LoadAsync()).Error);
            await store.SaveAsync(saved); Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task FavoritesRejectNullDuplicateIdsAndExcessiveFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "favorites.json"); var store = new JsonFavoritesStore(path);
            var item = new FavoriteFolder(Guid.NewGuid(), "a", Folder("a"));
            foreach (var json in new[] { "{\"Items\":null}", "{\"Items\":[null]}", JsonSerializer.Serialize(new FavoritesDocument { Items = [item, item] }), "{\"Version\":2}" })
            { await File.WriteAllTextAsync(path, json); Assert.IsNotNull((await store.LoadAsync()).Error); }
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.SaveAsync(new() { Items = [item with { Path = "relative" }] }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task SessionSaveWaitsForPendingFavoriteWithoutBlockingCaller()
    {
        var favorites = new DelayedFavoritesStore(); var sessions = new RecordingSessionStore();
        using var model = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), favoritesStore: favorites, sessionStore: sessions);
        var add = model.AddFavoriteAsync("pending", Folder("pending"));
        Assert.IsFalse(add.IsCompleted);
        var save = model.SaveSessionAsync(new()); Assert.IsFalse(save.IsCompleted); Assert.AreEqual(0, sessions.Saves);
        favorites.Complete.SetResult(); await Task.WhenAll(add, save);
        Assert.AreEqual(1, sessions.Saves); Assert.AreEqual(1, model.Favorites.Count);
    }
    [TestMethod]
    public async Task SessionSaveFailureAllowsRetryAndKeepsWorkspace()
    {
        var sessions = new RecordingSessionStore { Fail = true };
        using var model = new MainViewModel(new TrackingFileSystem(), new FakeLogger(), sessionStore: sessions, session: Session());
        await Assert.ThrowsExceptionAsync<IOException>(() => model.SaveSessionAsync(Session().Layout));
        Assert.AreEqual(3, model.Workspace.Left.Tabs.Count); Assert.AreEqual(PaneSide.Right, model.Workspace.ActiveSide);
        sessions.Fail = false; await model.SaveSessionAsync(Session().Layout); Assert.AreEqual(1, sessions.Saves);
    }
    private sealed class DelayedFavoritesStore : IFavoritesStore
    {
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StateLoadResult<FavoritesDocument>> LoadAsync(CancellationToken token = default) => Task.FromResult(new StateLoadResult<FavoritesDocument>(null));
        public Task SaveAsync(FavoritesDocument value, CancellationToken token = default) => Complete.Task;
    }
    private sealed class RecordingSessionStore : ISessionStore
    {
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public Task<StateLoadResult<SessionDocument>> LoadAsync(CancellationToken token = default) => Task.FromResult(new StateLoadResult<SessionDocument>(null));
        public Task SaveAsync(SessionDocument value, CancellationToken token = default)
        { if (Fail) throw new IOException("test"); Saves++; return Task.CompletedTask; }
    }
    private sealed class TrackingFileSystem : IFileSystemService
    {
        public List<string> Reads { get; } = [];
        public string? Missing { get; init; }
        public string? Denied { get; init; }
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token)
        {
            Reads.Add(path);
            if (path == Missing) throw new DirectoryNotFoundException("missing");
            if (path == Denied) throw new UnauthorizedAccessException("denied");
            return Task.FromResult(new FolderSnapshot(path, [new(path + "/child", "child", true, null, DateTime.Now)]));
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class MemorySettingsStore : ISettingsStore
    {
        private AppearanceSettings value = new();
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(value));
        public Task SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken = default) { value = settings; return Task.CompletedTask; }
    }
    private sealed class FailingFavoritesStore : IFavoritesStore
    {
        public Task<StateLoadResult<FavoritesDocument>> LoadAsync(CancellationToken token = default) => Task.FromResult(new StateLoadResult<FavoritesDocument>(null));
        public Task SaveAsync(FavoritesDocument value, CancellationToken token = default) => Task.FromException(new IOException("test failure"));
    }
}
