using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class StateTests
{
    [TestMethod]
    public async Task MovingTabAcrossPanesKeepsIdentityHistorySettingsAndSelection()
    {
        var files = new FakeFileSystem();
        using var workspace = new WorkspaceState(Home, files, new FakeLogger());
        await workspace.InitializeAsync();
        var tab = workspace.Left.SelectedTab;
        await tab.NavigateAsync(Path.Combine(Home, "child"));
        tab.ShowHiddenFiles = true; tab.ShowDotFiles = true;
        tab.SortProperty = nameof(FileEntry.Size); tab.SortDescending = true; tab.FilterText = "sample";
        var selection = tab.SelectedEntry = tab.Entries[0];
        workspace.Left.AddTab();
        workspace.Left.SelectedTab = tab;
        var reads = files.ReadCount;
        Assert.IsTrue(workspace.MoveTabToPane(tab, PaneSide.Right, 0));
        Assert.AreSame(tab, workspace.Right.SelectedTab);
        Assert.AreSame(tab, workspace.Right.Tabs[0]);
        Assert.IsFalse(workspace.Left.Tabs.Contains(tab));
        Assert.AreSame(selection, tab.SelectedEntry);
        Assert.AreEqual(reads, files.ReadCount, "移動したタブは再列挙しない");
        Assert.IsTrue(tab.ShowHiddenFiles && tab.ShowDotFiles && tab.SortDescending && tab.CanGoBack);
        Assert.AreEqual(nameof(FileEntry.Size), tab.SortProperty);
        Assert.AreEqual("sample", tab.FilterText);
        Assert.AreEqual(PaneSide.Right, workspace.ActiveSide);
        var snapshot = workspace.Capture(new WindowLayout());
        Assert.AreEqual(tab.CurrentPath, snapshot.Right.Tabs[0].Path);
        Assert.AreEqual(0, snapshot.Right.SelectedIndex);
        await tab.BackAsync();
        Assert.AreEqual(Home, tab.CurrentPath);
        Assert.IsTrue(workspace.MoveTabToPane(tab, PaneSide.Left));
        Assert.AreSame(tab, workspace.Left.SelectedTab);
    }

    [TestMethod]
    public async Task MovingLastTabLeavesUsableReplacementAndInvalidTransfersDoNothing()
    {
        using var workspace = Create();
        await workspace.InitializeAsync();
        var tab = workspace.Left.SelectedTab;
        Assert.IsFalse(workspace.MoveTabToPane(tab, PaneSide.Left));
        Assert.IsFalse(workspace.MoveTabToPane(tab, PaneSide.Right, -1));
        Assert.IsFalse(workspace.MoveTabToPane(tab, PaneSide.Right, 2));
        using var foreign = Create();
        Assert.IsFalse(workspace.MoveTabToPane(foreign.Left.SelectedTab, PaneSide.Right));
        Assert.AreEqual(1, workspace.Left.Tabs.Count);
        Assert.IsTrue(workspace.MoveTabToPane(tab, PaneSide.Right));
        Assert.AreEqual(1, workspace.Left.Tabs.Count);
        Assert.AreNotSame(tab, workspace.Left.SelectedTab);
        Assert.AreEqual(tab.CurrentPath, workspace.Left.SelectedTab.CurrentPath);
        Assert.IsTrue(workspace.Left.SelectedTab.NavigationSucceeded);
        Assert.AreEqual(2, workspace.Right.Tabs.Count);
        Assert.IsTrue(workspace.Left.CloseTab(tab) == false, "移動元はタブを破棄できない");
        await tab.RefreshAsync();
        Assert.IsTrue(tab.NavigationSucceeded);
    }

    [TestMethod]
    public async Task MovingLoadingTabDoesNotCancelItsNavigation()
    {
        using var workspace = new WorkspaceState(Home, new CancellableFileSystem(), new FakeLogger());
        var tab = workspace.Left.SelectedTab;
        var loading = tab.RefreshAsync();
        Assert.IsTrue(workspace.MoveTabToPane(tab, PaneSide.Right));
        await Task.Delay(30);
        Assert.IsFalse(loading.IsCompleted, "所有ペインの変更だけで読み込みをキャンセルしない");
        tab.CancelLoading();
        await loading;
        workspace.Left.SelectedTab.CancelLoading();
    }

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "PaneCommanderHome");
    private static WorkspaceState Create() => new(Home, new FakeFileSystem(), new FakeLogger());

    [TestMethod]
    public void ActiveSideIsExclusiveAndStableWithoutExplicitActivation()
    {
        using var workspace = Create();
        Assert.IsTrue(workspace.Left.IsActive);
        Assert.IsFalse(workspace.Right.IsActive);
        for (var i = 0; i < 100; i++)
        {
            workspace.Toggle();
            Assert.AreNotEqual(workspace.Left.IsActive, workspace.Right.IsActive);
            Assert.AreEqual(workspace.ActivePane, workspace.ActiveSide == PaneSide.Left ? workspace.Left : workspace.Right);
            Assert.AreNotEqual(workspace.ActivePane, workspace.OppositePane);
        }
        workspace.Activate(PaneSide.Right);
        workspace.Left.AddTab();
        workspace.Right.AddTab();
        Assert.AreEqual(PaneSide.Right, workspace.ActiveSide, "タブ状態へのアクセスや変更は入力フォーカスではない");
    }

    [TestMethod]
    public async Task TabsAndNavigationAreIndependentAcrossPanes()
    {
        using var workspace = Create();
        await workspace.InitializeAsync();
        var originalLeft = workspace.Left.SelectedTab;
        originalLeft.SelectedEntry = originalLeft.Entries[0];
        var right = workspace.Right.AddTab();
        await right.NavigateAsync(Path.Combine(Home, "right"));
        Assert.AreEqual(1, workspace.Left.Tabs.Count);
        Assert.AreEqual(2, workspace.Right.Tabs.Count);
        Assert.AreSame(originalLeft, workspace.Left.SelectedTab);
        Assert.AreEqual(Home, originalLeft.CurrentPath);
        Assert.IsNotNull(originalLeft.SelectedEntry);
        Assert.IsFalse(originalLeft.CanGoBack);
        workspace.Right.SelectedTab = workspace.Right.Tabs[0];
        Assert.AreEqual(Home, workspace.Right.SelectedTab.CurrentPath);
        workspace.Right.SelectedTab = right;
        Assert.AreEqual(Path.Combine(Home, "right"), right.CurrentPath);
    }

    [TestMethod]
    public async Task NewTabInheritsPathAndLastTabCannotClose()
    {
        using var workspace = Create();
        var pane = workspace.Left;
        await pane.SelectedTab.NavigateAsync(Path.Combine(Home, "日本語 空白"));
        var original = pane.SelectedTab;
        var added = pane.AddTab();
        Assert.AreEqual(original.CurrentPath, added.CurrentPath);
        Assert.IsFalse(added.CanGoBack);
        Assert.IsTrue(pane.CloseTab(added));
        Assert.AreSame(original, pane.SelectedTab);
        Assert.IsFalse(pane.CloseTab(original));
        Assert.AreEqual(1, pane.Tabs.Count);
    }

    [TestMethod]
    public void ClosingBackgroundTabDoesNotChangeSelectionAndForeignSelectionIsIgnored()
    {
        using var workspace = Create();
        var original = workspace.Left.SelectedTab;
        var current = workspace.Left.AddTab();
        workspace.Left.SelectedTab = workspace.Right.SelectedTab;
        Assert.AreSame(current, workspace.Left.SelectedTab);
        Assert.IsTrue(workspace.Left.CloseTab(original));
        Assert.AreSame(current, workspace.Left.SelectedTab);
        Assert.IsFalse(workspace.Left.CloseTab(workspace.Right.SelectedTab));
    }

    [TestMethod]
    public async Task HistoryBranchesAndRefreshDoesNotAddHistory()
    {
        using var workspace = Create();
        var tab = workspace.Left.SelectedTab;
        await tab.RefreshAsync();
        var childA = Path.Combine(Home, "a");
        var childB = Path.Combine(Home, "b");
        var childC = Path.Combine(Home, "c");
        await tab.NavigateAsync(childA);
        await tab.NavigateAsync(childB);
        await tab.BackAsync();
        Assert.AreEqual(childA, tab.CurrentPath);
        Assert.IsTrue(tab.CanGoForward);
        await tab.RefreshAsync();
        Assert.IsTrue(tab.CanGoForward);
        await tab.ForwardAsync();
        Assert.AreEqual(childB, tab.CurrentPath);
        await tab.BackAsync();
        await tab.NavigateAsync(childC);
        Assert.IsFalse(tab.CanGoForward);
        await tab.BackAsync();
        Assert.AreEqual(childA, tab.CurrentPath);
        await tab.BackAsync();
        Assert.AreEqual(Home, tab.CurrentPath);
        Assert.IsFalse(tab.CanGoBack);
    }

    [TestMethod]
    public async Task FailedNavigationPreservesListingSelectionAndHistory()
    {
        var logger = new FakeLogger();
        using var tab = new TabState(Home, new FakeFileSystem(), logger);
        await tab.RefreshAsync();
        tab.SelectedEntry = tab.Entries[0];
        var previousEntries = tab.Entries;
        var previousSelection = tab.SelectedEntry;
        await tab.NavigateAsync("FAIL");
        Assert.AreEqual(Home, tab.CurrentPath);
        Assert.AreSame(previousEntries, tab.Entries);
        Assert.AreSame(previousSelection, tab.SelectedEntry);
        Assert.IsFalse(tab.CanGoBack);
        StringAssert.Contains(tab.Status, "FAIL");
        Assert.AreEqual(1, logger.Errors.Count);
        Assert.IsFalse(tab.IsLoading);
    }

    [TestMethod]
    public async Task LateCompletionCannotReplaceNewerFolder()
    {
        var service = new ControlledFileSystem();
        using var tab = new TabState(Home, service, new FakeLogger());
        var first = tab.NavigateAsync("first");
        var second = tab.NavigateAsync("second");
        service.Complete("second");
        await second;
        service.Complete("first");
        await first;
        Assert.AreEqual("second", tab.CurrentPath);
        Assert.IsFalse(tab.CanGoBack, "古い要求は履歴にも残さない");
        Assert.IsFalse(tab.IsLoading);
    }

    [TestMethod]
    public async Task LateErrorDoesNotOverwriteCurrentStatus()
    {
        var service = new ControlledFileSystem();
        var logger = new FakeLogger();
        using var tab = new TabState(Home, service, logger);
        var first = tab.NavigateAsync("first");
        var second = tab.NavigateAsync("second");
        service.Complete("second");
        await second;
        service.Fail("first");
        await first;
        Assert.AreEqual("0 件", tab.Status);
        Assert.AreEqual(0, logger.Errors.Count);
    }

    [TestMethod]
    public async Task ClosedTabIgnoresPendingResult()
    {
        var service = new ControlledFileSystem();
        using var tab = new TabState(Home, service, new FakeLogger());
        var load = tab.NavigateAsync("later");
        tab.Dispose();
        service.Complete("later");
        await load;
        Assert.AreEqual(Home, tab.CurrentPath);
    }

    [TestMethod]
    public async Task CancelledNavigationPreservesFolderAndIsNotSuccess()
    {
        var service = new CancellableFileSystem();
        using var tab = new TabState(Home, service, new FakeLogger());
        var load = tab.NavigateAsync("slow");
        Assert.IsTrue(tab.IsLoading);
        tab.CancelLoading();
        await load;
        Assert.AreEqual(Home, tab.CurrentPath);
        Assert.IsFalse(tab.IsLoading);
        StringAssert.Contains(tab.Status, "キャンセル");
    }

    [TestMethod]
    public async Task RefreshKeepsSelectionByPathAndTabSwitchKeepsSelection()
    {
        using var workspace = Create();
        var tab = workspace.Left.SelectedTab;
        await tab.RefreshAsync();
        tab.SelectedEntry = tab.Entries[0];
        await tab.RefreshAsync();
        Assert.IsNotNull(tab.SelectedEntry);
        Assert.AreEqual(tab.Entries[0].FullPath, tab.SelectedEntry.FullPath);
        var second = workspace.Left.AddTab();
        var third = workspace.Left.AddTab();
        var opposite = workspace.Right.SelectedTab;
        workspace.Left.SelectAdjacentTab(-1);
        Assert.AreSame(second, workspace.Left.SelectedTab);
        workspace.Left.SelectAdjacentTab(-1);
        Assert.AreSame(tab, workspace.Left.SelectedTab);
        Assert.IsNotNull(tab.SelectedEntry);
        workspace.Left.SelectAdjacentTab(-1);
        Assert.AreSame(third, workspace.Left.SelectedTab, "先頭から前へは末尾へ循環");
        workspace.Left.SelectAdjacentTab(1);
        Assert.AreSame(tab, workspace.Left.SelectedTab, "末尾から次へは先頭へ循環");
        Assert.IsNotNull(tab.SelectedEntry);
        Assert.AreSame(opposite, workspace.Right.SelectedTab);
        workspace.Right.SelectAdjacentTab(1);
        Assert.AreSame(opposite, workspace.Right.SelectedTab, "1タブの場合はそのまま");
    }

    [TestMethod]
    public async Task ReorderingTabsPreservesSelectionHistoryAndCapturedOrder()
    {
        using var workspace = Create();
        var pane = workspace.Left;
        var first = pane.SelectedTab;
        await first.NavigateAsync(Path.Combine(Home, "first"));
        first.SelectedEntry = first.Entries[0];
        var second = pane.AddTab();
        await second.NavigateAsync(Path.Combine(Home, "second"));
        var third = pane.AddTab();
        await third.NavigateAsync(Path.Combine(Home, "third"));
        var selectedEntry = third.Entries[0]; third.SelectedEntry = selectedEntry;
        var history = third.CanGoBack;
        Assert.IsTrue(pane.MoveSelectedTab(-1));
        CollectionAssert.AreEqual(new[] { first, third, second }, pane.Tabs.ToArray());
        Assert.AreSame(third, pane.SelectedTab);
        Assert.AreSame(selectedEntry, third.SelectedEntry);
        Assert.AreEqual(history, third.CanGoBack);
        Assert.IsTrue(pane.MoveSelectedTab(-1));
        Assert.IsFalse(pane.MoveSelectedTab(-1), "端では並べ替えを循環させない");
        Assert.IsTrue(pane.MoveTab(first, 2));
        CollectionAssert.AreEqual(new[] { third, second, first }, pane.Tabs.ToArray());
        Assert.AreSame(third, pane.SelectedTab);
        Assert.IsFalse(pane.MoveTab(workspace.Right.SelectedTab, 0));
        Assert.IsFalse(pane.MoveTab(third, 3));
        var snapshot = pane.Capture();
        using var restored = new PaneState(PaneSide.Left, Home, new FakeFileSystem(), new FakeLogger());
        restored.Restore(snapshot);
        CollectionAssert.AreEqual(pane.Tabs.Select(t => t.CurrentPath).ToArray(), restored.Tabs.Select(t => t.CurrentPath).ToArray());
        Assert.AreEqual(third.CurrentPath, restored.SelectedTab.CurrentPath);
        Assert.IsTrue(restored.MoveTab(restored.SelectedTab, 2));
        Assert.IsFalse(restored.MoveSelectedTab(1));
        Assert.AreEqual(1, workspace.Right.Tabs.Count);
    }
    [TestMethod]
    public async Task UpUsesParentAndStopsAtRoot()
    {
        using var workspace = Create();
        var tab = workspace.Left.SelectedTab;
        await tab.NavigateAsync(Path.Combine(Home, "child"));
        await tab.UpAsync();
        Assert.AreEqual(Home, tab.CurrentPath);
        var root = Path.GetPathRoot(Home)!;
        await tab.NavigateAsync(root);
        await tab.UpAsync();
        Assert.AreEqual(root, tab.CurrentPath);
    }

    [TestMethod]
    public async Task DirectoryLinkNavigatesAndFileOpenFailureIsReported()
    {
        var logger = new FakeLogger();
        using var tab = new TabState(Home, new FakeFileSystem(), logger);
        tab.SelectedEntry = new("link", "link", true, null, null, true);
        await tab.OpenSelectedAsync();
        Assert.AreEqual("link",tab.CurrentPath,"リンクもフォルダ移動サービスへ委譲する");
        tab.SelectedEntry = new("file", "file", false, 10, null);
        await tab.OpenSelectedAsync();
        StringAssert.Contains(tab.Status, "開けません");
        Assert.AreEqual(1, logger.Errors.Count);
    }
}

internal sealed class FakeLogger : IErrorLogger
{
    public List<Exception> Errors { get; } = [];
    public void Log(string context, Exception error) => Errors.Add(error);
}

internal sealed class FakeFileSystem : IFileSystemService
{
    public int ReadCount { get; private set; }
    public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken)
    {
        ReadCount++;
        if (path == "FAIL") throw new UnauthorizedAccessException("テストアクセス拒否");
        return Task.FromResult(new FolderSnapshot(path,
            [new FileEntry(Path.Combine(path, "sample.txt"), "sample.txt", false, 42, DateTime.Now)]));
    }
    public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => throw new IOException("テスト起動失敗");
}

internal sealed class ControlledFileSystem : IFileSystemService
{
    private readonly Dictionary<string, TaskCompletionSource<FolderSnapshot>> requests = [];
    public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken)
    {
        requests[path] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return requests[path].Task;
    }
    public void Complete(string path) => requests[path].SetResult(new FolderSnapshot(path, []));
    public void Fail(string path) => requests[path].SetException(new IOException("古い失敗"));
    public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CancellableFileSystem : IFileSystemService
{
    public async Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new(path, []);
    }
    public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
}
