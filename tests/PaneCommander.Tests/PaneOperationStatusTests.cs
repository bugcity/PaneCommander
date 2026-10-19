using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class PaneOperationStatusTests
{
    [TestMethod]
    public async Task SuccessExpiresAfterFivePresentedSecondsAndRefreshDoesNotExtendIt()
    {
        var clock = new StatusTestClock();
        using var tab = new TabState(@"C:\fixture", new StatusFiles(), new FakeLogger(), clock);
        await tab.RefreshAsync(); tab.WatchWarning = "監視警告";
        var report = tab.BeginOperationStatus("処理中");
        Assert.IsFalse(tab.DismissOperationStatus());
        tab.SetStatusPresented(true); report("完了", OperationStatusKind.Success);
        clock.Advance(2); await tab.RefreshAsync();
        Assert.AreEqual(TimeSpan.FromSeconds(3), tab.OperationStatusTimeRemaining);
        tab.SetStatusPresented(false); clock.Advance(300); tab.ExpireOperationStatus();
        StringAssert.Contains(tab.Status, "完了");
        Assert.AreEqual(TimeSpan.FromSeconds(3), tab.OperationStatusTimeRemaining);
        tab.SetStatusPresented(true); clock.Advance(2.9); tab.ExpireOperationStatus();
        Assert.AreEqual(OperationStatusKind.Success, tab.OperationKind);
        clock.Advance(.1); tab.ExpireOperationStatus();
        Assert.AreEqual(OperationStatusKind.None, tab.OperationKind);
        Assert.IsFalse(tab.CanDismissOperationStatus); StringAssert.StartsWith(tab.Status, "1 件");
        StringAssert.Contains(tab.Status, "属性を取得できません"); StringAssert.Contains(tab.Status, "監視警告");
    }

    [TestMethod]
    public async Task AttentionHasNoDeadlineAndExplicitAcknowledgementKeepsBaseStateAndWarnings()
    {
        var clock = new StatusTestClock();
        using var tab = new TabState(@"C:\fixture", new StatusFiles(), new FakeLogger(), clock);
        await tab.RefreshAsync(); tab.WatchWarning = "監視警告"; tab.SetStatusPresented(true);
        var report = tab.BeginOperationStatus("処理中"); report("中止・一部完了", OperationStatusKind.Attention);
        clock.Advance(3600); tab.ExpireOperationStatus(); await tab.RefreshAsync();
        Assert.IsNull(tab.OperationStatusTimeRemaining); StringAssert.Contains(tab.Status, "中止・一部完了");
        Assert.IsTrue(tab.DismissOperationStatus()); Assert.IsFalse(tab.DismissOperationStatus());
        StringAssert.StartsWith(tab.Status, "1 件"); StringAssert.Contains(tab.Status, "監視警告");
        report("確認後の古い完了", OperationStatusKind.Success); Assert.IsFalse(tab.Status.Contains("古い完了"));
    }

    [TestMethod]
    public void OldDeadlineCannotClearANewerOperationAndHiddenCompletionStartsAtFirstPresentation()
    {
        var clock = new StatusTestClock();
        using var tab = new TabState(@"C:\fixture", new StatusFiles(), new FakeLogger(), clock);
        var old = tab.BeginOperationStatus("旧処理"); old("旧完了", OperationStatusKind.Success);
        clock.Advance(60); tab.ExpireOperationStatus();
        Assert.AreEqual(TimeSpan.FromSeconds(5), tab.OperationStatusTimeRemaining);
        tab.SetStatusPresented(true); clock.Advance(4);
        var current = tab.BeginOperationStatus("新処理"); current("新完了", OperationStatusKind.Success);
        clock.Advance(1); tab.ExpireOperationStatus();
        Assert.AreEqual(TimeSpan.FromSeconds(4), tab.OperationStatusTimeRemaining);
        old("遅い旧エラー", OperationStatusKind.Attention); StringAssert.Contains(tab.Status, "新完了");
    }

    private sealed class StatusTestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(double seconds) => timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
    [TestMethod]
    public async Task CompletionStaysWithStartingTabWhenActivePaneAndTabChange()
    {
        var operations = new PendingOperation();
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger(), fileOperations: operations);
        await model.Workspace.InitializeAsync();
        var original = model.Workspace.Left.SelectedTab;
        var request = new FileOperationRequest(FileOperationKind.CreateDirectory, [], original.CurrentPath, "created");
        var pending = model.ExecuteFileOperationAsync(request, IntPtr.Zero);
        StringAssert.Contains(original.Status, "ファイル操作中");
        var otherTab = model.Workspace.Left.AddTab();
        model.Workspace.Activate(PaneSide.Right);
        operations.Completion.SetResult(new(false)); await pending;
        StringAssert.Contains(original.Status, "完了しました");
        Assert.IsFalse(otherTab.Status.Contains("完了しました"));
        Assert.IsFalse(model.Workspace.Right.SelectedTab.Status.Contains("完了しました"));
        model.Workspace.Left.SelectedTab = original;
        await original.EnsureLoadedAsync();
        StringAssert.Contains(original.Status, "完了しました", "自動更新・再取得では結果を消さない");
        await original.NavigateAsync(@"C:\different");
        Assert.IsFalse(original.Status.Contains("完了しました"));
    }

    [TestMethod]
    public async Task NavigationBeforeCompletionPreventsStaleResultEvenAfterReturningToSamePath()
    {
        var operations = new PendingOperation();
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger(), fileOperations: operations);
        await model.Workspace.InitializeAsync(); var tab = model.Workspace.Left.SelectedTab; var original = tab.CurrentPath;
        var pending = model.ExecuteFileOperationAsync(new(FileOperationKind.CreateDirectory, [], original, "created"), IntPtr.Zero);
        await tab.NavigateAsync(@"C:\different"); await tab.NavigateAsync(original);
        operations.Completion.SetResult(new(false)); await pending;
        Assert.IsFalse(tab.Status.Contains("操作中")); Assert.IsFalse(tab.Status.Contains("完了しました"));
        Assert.IsFalse(model.IsFileOperationRunning);
    }

    [TestMethod]
    public async Task DropCanReportToDestinationTabAndAbortIsRetainedThere()
    {
        var operations = new PendingOperation();
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger(), fileOperations: operations);
        await model.Workspace.InitializeAsync(); var target = model.Workspace.Right.SelectedTab;
        var pending = model.ExecuteFileOperationAsync(new(FileOperationKind.CreateDirectory, [], target.CurrentPath, "created"), IntPtr.Zero, target);
        StringAssert.Contains(target.Status, "操作中");
        Assert.IsFalse(model.Workspace.Left.SelectedTab.Status.Contains("操作中"));
        operations.Completion.SetResult(new(true)); await pending;
        StringAssert.Contains(target.Status, "0/1"); StringAssert.Contains(target.Status, "中止");
    }

    [TestMethod]
    public async Task NotificationsPreserveCountsWarningsAndInvalidateOlderOperation()
    {
        using var tab = new TabState(@"C:\fixture", new StatusFiles(), new FakeLogger());
        await tab.RefreshAsync(); tab.WatchWarning = "監視警告";
        var old = tab.BeginOperationStatus("古い操作"); var current = tab.BeginOperationStatus("新しい操作");
        old("古い完了", OperationStatusKind.Success); Assert.IsFalse(tab.Status.Contains("古い"));
        current("操作完了", OperationStatusKind.Success); await tab.RefreshAsync(); await tab.RefreshAsync();
        StringAssert.StartsWith(tab.Status, "1 件");
        StringAssert.Contains(tab.Status, "属性を取得できません");
        Assert.AreEqual(1, tab.Status.Split("操作完了").Length - 1);
        Assert.AreEqual(1, tab.Status.Split("監視警告").Length - 1);
        tab.Dispose(); current("終了後の通知", OperationStatusKind.Success); Assert.IsFalse(tab.Status.Contains("終了後"));
    }
    private sealed class PendingOperation : IFileOperationService
    {
        public readonly TaskCompletionSource<FileOperationResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default) => Completion.Task;
    }
    private sealed class StatusFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            [new(Path.Combine(path, "file.txt"), "file.txt", false, 1, null, MetadataError: "fixture error")]));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
