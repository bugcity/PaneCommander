using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ResourceLifecycleTests
{
    [TestMethod]
    public async Task SwitchingTabsCancelsUnneededReadAndResumesUnloadedTab()
    {
        using var pane = new PaneState(PaneSide.Left, "home", new CancellableFileSystem(), new FakeLogger());
        var original = pane.SelectedTab;
        var read = original.RefreshAsync();
        var other = pane.AddTab();
        await read;
        Assert.IsFalse(original.IsLoading);
        StringAssert.Contains(original.Status, "キャンセル");
        pane.SelectedTab = original;
        Assert.IsTrue(original.IsLoading);
        original.CancelLoading();
        other.CancelLoading();
    }

    [TestMethod]
    public void LargeInactiveSnapshotIsReleasedButPathHistoryAndSelectionRecover()
    {
        using var pane = new PaneState(PaneSide.Left, "home", new SizedFileSystem(3_000), new FakeLogger());
        var original = pane.SelectedTab;
        original.RefreshAsync().GetAwaiter().GetResult();
        original.NavigateAsync("child").GetAwaiter().GetResult();
        original.SelectedEntry = original.Entries[42];
        var selectedPath = original.SelectedEntry.FullPath;
        pane.AddTab();
        Assert.AreEqual(0, original.Entries.Count);
        Assert.IsNull(original.SelectedEntry);
        Assert.AreEqual("child", original.CurrentPath);
        Assert.IsTrue(original.CanGoBack);
        pane.SelectedTab = original;
        Assert.AreEqual(3_000, original.Entries.Count);
        Assert.AreEqual(selectedPath, original.SelectedEntry?.FullPath);
        original.BackAsync().GetAwaiter().GetResult();
        Assert.AreEqual("home", original.CurrentPath);
    }

    [TestMethod]
    public void AggregateInactiveCacheIsBoundedAndClosedTabsReleaseEntries()
    {
        using var pane = new PaneState(PaneSide.Left, "home", new SizedFileSystem(1_500), new FakeLogger());
        pane.SelectedTab.RefreshAsync().GetAwaiter().GetResult();
        for (var i = 0; i < 20; i++) pane.AddTab();
        Assert.IsTrue(pane.Tabs.Where(t => t != pane.SelectedTab).Sum(t => t.Entries.Count) <= PaneState.MaxInactiveEntriesPerPane);
        var closed = pane.SelectedTab;
        closed.SelectedEntry = closed.Entries[0];
        Assert.IsTrue(pane.CloseTab(closed));
        Assert.AreEqual(0, closed.Entries.Count);
        Assert.IsNull(closed.SelectedEntry);
    }

    [TestMethod]
    public async Task CancelledProviderThatIgnoresTokenCannotPopulateInactiveTab()
    {
        var service = new ControlledFileSystem();
        using var pane = new PaneState(PaneSide.Left, "home", service, new FakeLogger());
        var original = pane.SelectedTab;
        var read = original.NavigateAsync("slow");
        pane.AddTab();
        service.Complete("slow");
        await read;
        Assert.AreEqual("home", original.CurrentPath);
        Assert.AreEqual(0, original.Entries.Count);
        service.Complete("home");
    }
}

internal sealed class SizedFileSystem(int count) : IFileSystemService
{
    public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken) => Task.FromResult(
        new FolderSnapshot(path, Enumerable.Range(0, count).Select(i =>
            new FileEntry(Path.Combine(path, $"item-{i}.txt"), $"item-{i}.txt", false, 0, null)).ToArray()));
    public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
}
