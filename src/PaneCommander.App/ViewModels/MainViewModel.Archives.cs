using PaneCommander.Core;

namespace PaneCommander.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IArchiveService? archives;
    private CancellationTokenSource? archiveCancellation;
    public Func<bool, Task>? RequestArchive { get; set; }
    public bool IsArchiveOperationRunning => archiveCancellation is not null;
    public void CancelActiveArchive() => archiveCancellation?.Cancel();
    public Task WaitForOperationsAsync() => operationsIdle;
    public bool CanExtractArchives => archives is not null && !operating && !editing && !Workspace.ActivePane.SelectedTab.IsLoading
        && (GetSelectedPaths?.Invoke() ?? []).Any(path => ArchiveFormats.Detect(path) is not null && Workspace.ActivePane.SelectedTab.Entries.Any(e => e.FullPath == path && !e.IsDirectory && !e.IsReparsePoint));
    public bool CanCompressArchives => archives is not null && !operating && !editing && !Workspace.ActivePane.SelectedTab.IsLoading && (GetSelectedPaths?.Invoke().Length ?? 0) > 0;
    public async Task<ArchiveBatchResult> ExecuteArchiveAsync(TabState target, Func<IArchiveService, IProgress<ArchiveProgress>, CancellationToken, Task<ArchiveBatchResult>> operation)
    {
        if (archives is null || operating || editing) throw new InvalidOperationException("アーカイブ操作を開始できません。");
        using var cancellation = new CancellationTokenSource(); archiveCancellation = cancellation;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); operationsIdle = idle.Task;
        var report = target.BeginOperationStatus("アーカイブを解析中…");
        try
        {
            operating = true; Raise(nameof(IsFileOperationRunning)); Raise(nameof(IsArchiveOperationRunning)); NotifyFileCommands();
            target.CancelArchive = cancellation.Cancel;
            target.ArchiveProgress = new("事前解析", "", 0, 0, 0, 0);
            var progress = new Progress<ArchiveProgress>(value => { if (ReferenceEquals(archiveCancellation, cancellation)) target.ArchiveProgress = value; });
            var result = await operation(archives, progress, cancellation.Token);
            await RefreshAfterFileOperationAsync();
            var summary = ArchiveSummary(result);
            report(summary, result.Failed > 0 || result.Skipped > 0 || result.Cancelled || result.Warnings.Count > 0 ? OperationStatusKind.Attention : OperationStatusKind.Success);
            return result;
        }
        catch (Exception ex) { report("アーカイブ操作に失敗しました: " + ex.Message, OperationStatusKind.Attention); logger.Log("圧縮・展開", ex); throw; }
        finally
        {
            target.CancelArchive = null; target.ArchiveProgress = null; archiveCancellation = null;
            operating = false;
            try { Raise(nameof(IsFileOperationRunning)); Raise(nameof(IsArchiveOperationRunning)); NotifyFileCommands(); }
            finally { idle.TrySetResult(); }
        }
    }
    public static string ArchiveSummary(ArchiveBatchResult result) => $"成功 {result.Succeeded} / 失敗 {result.Failed} / スキップ {result.Skipped}" + (result.Cancelled ? " · 中止" : "")
        + (result.Errors.Count == 0 ? "" : "\n" + string.Join("\n", result.Errors)) + (result.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", result.Warnings));
}
