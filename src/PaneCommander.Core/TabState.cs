namespace PaneCommander.Core;

/// <summary>UI非依存。成功時のみ履歴を確定し、後発のナビゲーションを優先する。</summary>
public sealed class TabState : ObservableObject, IDisposable
{
    private readonly IFileSystemService fileSystem;
    private readonly IErrorLogger logger;
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private int requestVersion;
    private CancellationTokenSource? pending;
    private string currentPath;
    private string inputPath;
    private string status = "読み込み待ち";
    private string operationMessage = "";
    private int operationVersion;
    private readonly TimeProvider statusClock;
    private OperationStatusKind operationKind;
    private TimeSpan operationRemaining;
    private long? operationShownAt;
    private bool statusPresented;
    private string watchWarning = "";
    public string WatchWarning
    {
        get => watchWarning;
        set { if (Set(ref watchWarning, value)) Raise(nameof(Status)); }
    }
    private bool isLoading;
    private ArchiveProgress? archiveProgress;
    public ArchiveProgress? ArchiveProgress { get => archiveProgress; set => Set(ref archiveProgress, value); }
    public Action? CancelArchive { get; set; }
    private string[] requestedSelectionPaths = [];
    public string[] RequestedSelectionPaths { get => requestedSelectionPaths; private set => Set(ref requestedSelectionPaths, value); }
    public void RequestSelection(IEnumerable<string> paths) => RequestedSelectionPaths = paths.ToArray();
    public void CompleteSelectionRequest() => RequestedSelectionPaths = [];
    private IReadOnlyList<FileEntry> entries = [];
    private FileEntry? selectedEntry;
    private bool disposed;
    private bool hasSnapshot;
    private string filterText = "";
    private bool isFilterOpen;
    private bool showHiddenFiles;
    public bool ShowHiddenFiles { get => showHiddenFiles; set => Set(ref showHiddenFiles, value); }
    private bool showDotFiles;
    public bool ShowDotFiles { get => showDotFiles; set => Set(ref showDotFiles, value); }
    public string FilterText { get => filterText; set => Set(ref filterText, value ?? ""); }
    public bool IsFilterOpen { get => isFilterOpen; set => Set(ref isFilterOpen, value); }
    public void ClearFilter() { FilterText = ""; IsFilterOpen = false; }
    private string? suspendedSelectionPath;
    public bool NavigationSucceeded { get; private set; }
    public string SortProperty { get; set; } = nameof(FileEntry.Name);
    public bool SortDescending { get; set; }

    public TabState(string path, IFileSystemService fileSystem, IErrorLogger logger, TimeProvider? statusClock = null)
    {
        currentPath = inputPath = path;
        this.fileSystem = fileSystem;
        this.logger = logger;
        this.statusClock = statusClock ?? TimeProvider.System;
    }

    public string CurrentPath { get => currentPath; private set => Set(ref currentPath, value); }
    public string InputPath { get => inputPath; set => Set(ref inputPath, value); }
    public string Title => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(CurrentPath),System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetPathRoot(CurrentPath)??""),StringComparison.OrdinalIgnoreCase) ? CurrentPath : System.IO.Path.GetFileName(CurrentPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : CurrentPath;
    public string Status { get => status + (operationMessage.Length == 0 ? "" : " · " + operationMessage) + (watchWarning.Length == 0 ? "" : " · " + watchWarning); private set => Set(ref status, value); }
    /// <summary>操作開始時のタブに通知を固定し、後の移動・操作で古い通知を無効化する。</summary>
    public OperationStatusKind OperationKind => operationKind;
    public bool CanDismissOperationStatus => operationKind is OperationStatusKind.Success or OperationStatusKind.Attention;
    public TimeSpan? OperationStatusTimeRemaining => operationKind == OperationStatusKind.Success
        ? TimeSpan.FromTicks(Math.Max(0, (operationRemaining - (operationShownAt is { } started ? statusClock.GetElapsedTime(started) : TimeSpan.Zero)).Ticks)) : null;
    public Action<string, OperationStatusKind> BeginOperationStatus(string message)
    {
        var version = ++operationVersion;
        void Report(string value, OperationStatusKind kind)
        {
            if (disposed || version != operationVersion) return;
            SetOperationStatus(value, kind);
        }
        Report(message, OperationStatusKind.Progress);
        return Report;
    }
    private void ClearOperationStatus()
    {
        operationVersion++;
        SetOperationStatus("", OperationStatusKind.None);
    }
    private void SetOperationStatus(string message, OperationStatusKind kind)
    {
        operationMessage = message; operationKind = kind;
        operationRemaining = kind == OperationStatusKind.Success ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        operationShownAt = statusPresented && kind == OperationStatusKind.Success ? statusClock.GetTimestamp() : null;
        Raise(nameof(Status)); Raise(nameof(OperationKind)); Raise(nameof(CanDismissOperationStatus)); Raise(nameof(OperationStatusTimeRemaining));
    }
    public bool DismissOperationStatus()
    {
        if (!CanDismissOperationStatus) return false;
        ClearOperationStatus(); return true;
    }
    public void SetStatusPresented(bool presented)
    {
        if (disposed || statusPresented == presented) return;
        if (operationShownAt is not null) operationRemaining = OperationStatusTimeRemaining ?? TimeSpan.Zero;
        statusPresented = presented; operationShownAt = null;
        if (operationKind != OperationStatusKind.Success) return;
        if (operationRemaining <= TimeSpan.Zero) { ClearOperationStatus(); return; }
        if (presented) operationShownAt = statusClock.GetTimestamp();
    }
    public void ExpireOperationStatus()
    {
        if (statusPresented && OperationStatusTimeRemaining is { } remaining && remaining <= TimeSpan.Zero) ClearOperationStatus();
    }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public IReadOnlyList<FileEntry> Entries { get => entries; private set => Set(ref entries, value); }
    public FileEntry? SelectedEntry { get => selectedEntry; set => Set(ref selectedEntry, value); }
    public bool CanGoBack => historyIndex > 0;
    public bool CanGoForward => historyIndex >= 0 && historyIndex < history.Count - 1;
    public event Action? RefreshingSnapshot;

    public async Task NavigateAsync(string path, NavigationKind kind = NavigationKind.Normal, string? selectPath = null)
    {
        if (disposed) return;
        // 履歴操作中の連打で同じ履歴を二重に確定させない。
        if (IsLoading && kind is NavigationKind.Back or NavigationKind.Forward) return;
        if (kind != NavigationKind.Refresh) { ClearOperationStatus(); CompleteSelectionRequest(); }
        pending?.Cancel();
        var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var version = ++requestVersion;
        IsLoading = true;
        NavigationSucceeded = false;
        Status = $"読み込み中: {path}";
        try
        {
            var folder = await fileSystem.ReadFolderAsync(path, cancellation.Token);
            if (disposed || version != requestVersion) return;
            cancellation.Token.ThrowIfCancellationRequested();
            var previousSelection = SelectedEntry?.FullPath ?? suspendedSelectionPath;
            if (kind == NavigationKind.Back) historyIndex--;
            else if (kind == NavigationKind.Forward) historyIndex++;
            else if (kind == NavigationKind.Normal || historyIndex < 0)
            {
                if (historyIndex < 0 || !StringComparer.OrdinalIgnoreCase.Equals(history[historyIndex], folder.Path))
                {
                    history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                    history.Add(folder.Path);
                    historyIndex = history.Count - 1;
                }
            }
            CurrentPath = folder.Path;
            InputPath = folder.Path;
            if (kind == NavigationKind.Refresh) RefreshingSnapshot?.Invoke();
            Entries = folder.Entries;
            hasSnapshot = true;
            NavigationSucceeded = true;
            SelectedEntry = kind == NavigationKind.Refresh
                ? Entries.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, previousSelection))
                : selectPath is null ? null : Entries.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(e.FullPath), System.IO.Path.TrimEndingDirectorySeparator(selectPath)));
            suspendedSelectionPath = null;
            Status = $"{Entries.Count:N0} 件" + (Entries.Any(e => e.MetadataError is not null) ? " · 一部の属性を取得できません（名前にマウスを重ねて確認）" : "");
            Raise(nameof(Title));
            Raise(nameof(CanGoBack));
            Raise(nameof(CanGoForward));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (version == requestVersion && !disposed) Status = "読み込みをキャンセルしました";
        }
        catch (Exception ex)
        {
            if (version != requestVersion || disposed) return;
            Status = $"移動できません: {path} — {ex.Message}";
            logger.Log("フォルダ列挙", ex);
        }
        finally
        {
            if (version == requestVersion) { IsLoading = false; pending = null; }
            cancellation.Dispose();
        }
    }

    public Task BackAsync() => CanGoBack ? NavigateAsync(history[historyIndex - 1], NavigationKind.Back) : Task.CompletedTask;
    public Task ForwardAsync() => CanGoForward ? NavigateAsync(history[historyIndex + 1], NavigationKind.Forward) : Task.CompletedTask;
    public Task RefreshAsync() => NavigateAsync(CurrentPath, NavigationKind.Refresh);
    public Task EnsureLoadedAsync() => hasSnapshot || (IsLoading && pending?.IsCancellationRequested != true)
        ? Task.CompletedTask : RefreshAsync();

    /// <summary>状態と選択パスを維持したまま表示用一覧だけを手放す。</summary>
    public void ReleaseSnapshot()
    {
        CancelLoading();
        suspendedSelectionPath = SelectedEntry?.FullPath ?? suspendedSelectionPath;
        SelectedEntry = null;
        Entries = Array.Empty<FileEntry>();
        hasSnapshot = false;
        Status = "一覧キャッシュ解放済み · タブ切替時に再取得します";
    }
    public Task UpAsync()
    {
        var parent = System.IO.Path.GetDirectoryName(CurrentPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? Task.CompletedTask : NavigateAsync(parent, selectPath: CurrentPath);
    }
    public void CancelLoading() => pending?.Cancel();

    public Task OpenSelectedAsync() => OpenAsync(SelectedEntry);

    public async Task OpenAsync(FileEntry? entry)
    {
        if (entry is null) return;
        if (entry.IsDirectory) { await NavigateAsync(entry.FullPath); return; }
        var report = BeginOperationStatus($"既定アプリを起動中: {entry.Name}");
        try { await fileSystem.OpenFileAsync(entry, CancellationToken.None); report($"既定アプリで開きました: {entry.Name}", OperationStatusKind.Success); }
        catch (Exception ex) { report($"開けません: {ex.Message}", OperationStatusKind.Attention); logger.Log("ファイル起動", ex); }
    }

    public void Dispose()
    {
        disposed = true;
        statusPresented = false; ClearOperationStatus();
        pending?.Cancel();
        SelectedEntry = null;
        suspendedSelectionPath = null;
        Entries = Array.Empty<FileEntry>();
    }
}
