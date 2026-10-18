using PaneCommander.Core;
using System.Collections.ObjectModel;

namespace PaneCommander.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private AppearanceSettings settings;
    private readonly ISettingsStore? settingsStore;
    private readonly IFavoritesStore? favoritesStore;
    private readonly ISessionStore? sessionStore;
    private readonly IErrorLogger logger;
    private readonly ObservableCollection<FavoriteFolder> favorites = [];
    private FavoriteFolder? selectedFavorite;
    private bool savingFavorites;
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private Task favoritesIdle = Task.CompletedTask;
    private string startupWarning = "";
    private TabState? titleLeft, titleRight;
    private readonly IFileOperationService? fileOperations;
    private readonly IShellEditService? shellEdit;
    private bool editing;
    private bool operating; private Task operationsIdle=Task.CompletedTask;private string operationStatus="";
    public MainViewModel(IFileSystemService fileSystem, IErrorLogger logger, ISettingsStore? settingsStore = null, AppearanceSettings? settings = null,
        IFavoritesStore? favoritesStore = null, FavoritesDocument? savedFavorites = null, ISessionStore? sessionStore = null, SessionDocument? session = null,IFileOperationService? fileOperations=null, IShellEditService? shellEdit=null)
    {
        this.settingsStore = settingsStore;
        this.fileOperations=fileOperations;
        this.shellEdit=shellEdit;
        DeleteCommandEnabled=fileOperations is not null;
        this.logger = logger;
        this.favoritesStore = favoritesStore; this.sessionStore = sessionStore;
        this.settings = settings ?? new();
        StartupRenderingMode = this.settings.RenderingMode;
        FileSystem = fileSystem;
        Workspace = new WorkspaceState(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fileSystem, logger);
        if (session is not null) Workspace.Restore(session);
        RestoredLayout = session?.Layout;
        savedFavorites?.Validate();
        foreach (var item in (savedFavorites?.Items ?? []).DistinctBy(f => NormalizeFavoritePath(f.Path),StringComparer.OrdinalIgnoreCase)) favorites.Add(item);
        Favorites = new ReadOnlyObservableCollection<FavoriteFolder>(favorites);
        Left = new(Workspace.Left, logger);
        Right = new(Workspace.Right, logger);
        Workspace.PropertyChanged += WorkspaceChanged;
        Workspace.Left.PropertyChanged += PaneChanged; Workspace.Right.PropertyChanged += PaneChanged; TrackTitles();
        RefreshActive = new(_ => Workspace.ActivePane.SelectedTab.RefreshAsync(), logger);
        OpenSettings = new(_ => RequestSettings?.Invoke() ?? Task.CompletedTask, logger);
        OpenFavorite = new(async value =>
        {
            if (value is not FavoriteFolder favorite) return;
            var pane = Active; var tab = pane.State.SelectedTab;
            await tab.NavigateAsync(favorite.Path);
            if (ReferenceEquals(pane.State.SelectedTab, tab) && !tab.IsLoading) pane.RequestListFocus?.Invoke(tab);
        }, logger);
        AddFavorite = new(async _ =>
        {
            var tab = Workspace.ActivePane.SelectedTab;
            var name = RequestFavoriteName is null ? tab.Title : await RequestFavoriteName(tab.Title);
            if (name is not null) await AddFavoriteAsync(name, tab.CurrentPath);
        }, logger, _ => !savingFavorites && favorites.Count < 256);
        RenameFavorite = new(async _ =>
        {
            if (SelectedFavorite is not { } favorite || RequestFavoriteName is null) return;
            var name = await RequestFavoriteName(favorite.Name);
            if (name is not null) await RenameFavoriteAsync(favorite, name);
        }, logger, _ => !savingFavorites && SelectedFavorite is not null);
        RemoveFavorite = new(_ => SelectedFavorite is { } favorite ? RemoveFavoriteAsync(favorite) : Task.CompletedTask, logger, _ => !savingFavorites && SelectedFavorite is not null);
        MoveFavoriteUp = new(_ => SelectedFavorite is { } favorite ? MoveFavoriteAsync(favorite, -1) : Task.CompletedTask, logger, _ => !savingFavorites && SelectedFavorite is { } f && favorites.IndexOf(f) > 0);
        MoveFavoriteDown = new(_ => SelectedFavorite is { } favorite ? MoveFavoriteAsync(favorite, 1) : Task.CompletedTask, logger, _ => !savingFavorites && SelectedFavorite is { } f && favorites.IndexOf(f) < favorites.Count - 1);
        ToggleFavorites = new(_ => ApplySettingsAsync(Settings with { ShowFavorites = !Settings.ShowFavorites }), logger);
        var names = new[] { "ヘルプ", "ユーザー", "表示", "編集", "コピー", "移動", "作成", "削除", "メニュー", "終了" };
        Functions = Enumerable.Range(1, 10).Select(number => new FunctionAction(number, names[number - 1],
            number is 1 or 2 or 9 ? "未計画" : number == 10 ? "M2" : "M4",
            new AsyncCommand(async _ =>
            {
                if(number==10) RequestClose?.Invoke();
                else if(number==3 && RequestPreview is not null) await RequestPreview(GetSelectedPaths!().Single());
                else if(number==4) await EditSelectedAsync();
                else if(RequestFileOperation is not null) await RequestFileOperation(number switch{5=>FileOperationKind.Copy,6=>FileOperationKind.Move,7=>FileOperationKind.CreateDirectory,_=>FileOperationKind.Delete});
            }, logger, _ => number==10 || number==3 && !operating && RequestPreview is not null && GetSelectedPaths?.Invoke().Length==1 && Workspace.ActivePane.SelectedTab.SelectedEntry is {IsDirectory:false}
                || number==4 && CanEditSelected
                || fileOperations is not null && RequestFileOperation is not null && !operating && (number==7 || (number is 5 or 6 || number==8&&DeleteCommandEnabled) && (GetSelectedPaths?.Invoke().Length??0)>0)),number is 3 or 10 || number==4 && shellEdit is not null || fileOperations is not null&&number is 5 or 6 or 7 or 8)).ToArray();
    }
    public Func<IntPtr>? GetShellOwner { get; set; }
    private FileEntry? SingleEditTarget()
    {
        var paths = GetSelectedPaths?.Invoke();
        return paths is { Length: 1 } ? Workspace.ActivePane.SelectedTab.Entries.FirstOrDefault(e =>
            StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, paths[0]) && !e.IsDirectory && !e.IsReparsePoint) : null;
    }
    public bool CanEditSelected => shellEdit is not null && !operating && !editing && SingleEditTarget() is not null;
    public async Task EditSelectedAsync()
    {
        if (!CanEditSelected || SingleEditTarget() is not { } entry) return;
        editing = true; NotifyFileCommands();
        try { await shellEdit!.EditAsync(entry, GetShellOwner?.Invoke() ?? IntPtr.Zero); OperationStatus = "Windows Shellの編集を実行しました。"; }
        catch (Exception ex) { OperationStatus = "編集できません: " + ex.Message; logger.Log("Shell編集", ex); }
        finally { editing = false; NotifyFileCommands(); }
    }
    public IFileSystemService FileSystem { get; }
    public bool DeleteCommandEnabled {get;set;}
    public bool IsFileOperationRunning=>operating;
    public string OperationStatus {get=>operationStatus;private set=>Set(ref operationStatus,value);}
    public Func<string[]>? GetSelectedPaths {get;set;}
    public Func<FileOperationKind,Task>? RequestFileOperation {get;set;}
    public Func<string,Task>? RequestPreview {get;set;}
    public void NotifyFileCommands(){foreach(var f in Functions)f.Command.NotifyCanExecuteChanged();}
    public async Task<FileOperationResult> ExecuteFileOperationAsync(FileOperationRequest request,IntPtr owner)
        => await ExecuteFileOperationBatchAsync([request], owner);
    public async Task<FileOperationResult> ExecuteFileOperationBatchAsync(IReadOnlyList<FileOperationRequest> requests,IntPtr owner)
    {
        if(fileOperations is null)throw new InvalidOperationException("ファイル操作サービスがありません。");
        if(operating)throw new InvalidOperationException("別のファイル操作が実行中です。");
        foreach(var request in requests) DropPolicy.Validate(request);
        var idle=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);operationsIdle=idle.Task;
        operating=true;Raise(nameof(IsFileOperationRunning));NotifyFileCommands();OperationStatus="Windowsでファイル操作中…";
        var completed=0;
        try
        {
            foreach(var request in requests)
            {
                var result=await fileOperations.ExecuteAsync(request,owner);
                if(result.Aborted){OperationStatus=$"{completed}/{requests.Count}操作完了。残りは中止または一部未処理です。現在の一覧を確認してください。";return result;}
                completed++;
            }
            OperationStatus="Windowsの操作が終了しました。一覧を更新しました。";return new(false);
        }
        catch{OperationStatus=$"{completed}/{requests.Count}操作完了。残りの操作中にエラーが発生しました。現在の一覧を確認してください。";throw;}
        finally
        {
            await RefreshAfterFileOperationAsync();
            operating=false;Raise(nameof(IsFileOperationRunning));NotifyFileCommands();idle.SetResult();
        }
    }
    public async Task RefreshAfterFileOperationAsync()
    {
        foreach(var pane in new[]{Workspace.Left,Workspace.Right})foreach(var tab in pane.Tabs.Where(t=>t!=pane.SelectedTab))tab.ReleaseSnapshot();
        await Task.WhenAll(Workspace.Left.SelectedTab.RefreshAsync(),Workspace.Right.SelectedTab.RefreshAsync());
    }
    public string WindowTitle => Settings.TitleDisplay == TitleDisplay.FullPath ? Workspace.ActivePane.SelectedTab.CurrentPath : Workspace.ActivePane.SelectedTab.Title;
    private void WorkspaceChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e) { if(e.PropertyName==nameof(WorkspaceState.ActiveSide)) {Raise(nameof(WindowTitle));NotifyFileCommands();} }
    private void PaneChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e) { if(e.PropertyName==nameof(PaneState.SelectedTab)) { TrackTitles(); Raise(nameof(WindowTitle));NotifyFileCommands(); } }
    private void TitleChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e) { if(e.PropertyName==nameof(TabState.CurrentPath)) Raise(nameof(WindowTitle)); }
    private void TrackTitles()
    {
        if(titleLeft is not null) titleLeft.PropertyChanged-=TitleChanged; if(titleRight is not null) titleRight.PropertyChanged-=TitleChanged;
        titleLeft=Workspace.Left.SelectedTab; titleRight=Workspace.Right.SelectedTab;
        titleLeft.PropertyChanged+=TitleChanged; titleRight.PropertyChanged+=TitleChanged;
    }
    public RenderingPreference StartupRenderingMode { get; }
    public bool RenderingRestartRequired => Settings.RenderingMode != StartupRenderingMode;
    public WindowLayout? RestoredLayout { get; }
    public string StartupWarning { get => startupWarning; set => Set(ref startupWarning, value); }
    public bool HasSessionStore => sessionStore is not null;
    public ReadOnlyObservableCollection<FavoriteFolder> Favorites { get; }
    public FavoriteFolder? SelectedFavorite { get => selectedFavorite; set { if (Set(ref selectedFavorite, value)) NotifyFavoriteCommands(); } }
    public AppearanceSettings Settings { get => settings; private set => Set(ref settings, value); }
    public WorkspaceState Workspace { get; }
    public PaneViewModel Left { get; }
    public PaneViewModel Right { get; }
    public PaneViewModel Active => Workspace.ActiveSide == PaneSide.Left ? Left : Right;
    public AsyncCommand RefreshActive { get; }
    public AsyncCommand OpenSettings { get; }
    public AsyncCommand OpenFavorite { get; }
    public AsyncCommand AddFavorite { get; }
    public AsyncCommand RenameFavorite { get; }
    public AsyncCommand RemoveFavorite { get; }
    public AsyncCommand MoveFavoriteUp { get; }
    public AsyncCommand MoveFavoriteDown { get; }
    public AsyncCommand ToggleFavorites { get; }
    public Func<string, Task<string?>>? RequestFavoriteName { get; set; }
    public IReadOnlyList<FunctionAction> Functions { get; }
    public Func<Task>? RequestSettings { get; set; }
    public Action? RequestClose { get; set; }
    public async Task ApplySettingsAsync(AppearanceSettings value)
    {
        var errors = value.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("、", errors));
        await settingsGate.WaitAsync();
        try
        {
            if (settingsStore is not null) await settingsStore.SaveAsync(value);
            await OnUiAsync(() => { Settings = value; Raise(nameof(RenderingRestartRequired)); Raise(nameof(WindowTitle)); });
        }
        finally { settingsGate.Release(); }
    }
    public async Task SaveSessionAsync(WindowLayout layout)
    {
        await favoritesIdle;
        await operationsIdle;
        await settingsGate.WaitAsync();
        try
        {
            if (sessionStore is not null)
            {
                SessionDocument? snapshot = null;
                await OnUiAsync(() => snapshot = Workspace.Capture(layout));
                await sessionStore.SaveAsync(snapshot!);
            }
        }
        catch (Exception ex) { logger.Log("作業状態保存", ex); throw; }
        finally { settingsGate.Release(); }
    }
    public Task AddFavoriteAsync(string name, string path) => ChangeFavoritesAsync(items =>
    {
        var normalized=NormalizeFavoritePath(path);
        if(items.Any(f=>StringComparer.OrdinalIgnoreCase.Equals(NormalizeFavoritePath(f.Path),normalized))) return;
        items.Add(new(Guid.NewGuid(),name.Trim(),path));
    });
    private static string NormalizeFavoritePath(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public Task RemoveFavoriteAsync(FavoriteFolder value) => ChangeFavoritesAsync(items => items.RemoveAll(f => f.Id == value.Id));
    public Task RenameFavoriteAsync(FavoriteFolder value, string name) => ChangeFavoritesAsync(items =>
    { var index = items.FindIndex(f => f.Id == value.Id); if (index >= 0) items[index] = value with { Name = name.Trim() }; });
    public Task MoveFavoriteAsync(FavoriteFolder value, int delta) => ChangeFavoritesAsync(items =>
    {
        var index = items.FindIndex(f => f.Id == value.Id); var next = index + delta;
        if (index >= 0 && next >= 0 && next < items.Count) (items[index], items[next]) = (items[next], items[index]);
    });
    private async Task ChangeFavoritesAsync(Action<List<FavoriteFolder>> change)
    {
        if (savingFavorites) throw new InvalidOperationException("お気に入りを保存中です。");
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        favoritesIdle = idle.Task;
        savingFavorites = true; NotifyFavoriteCommands();
        try
        {
            var next = favorites.ToList(); change(next);
            var value = new FavoritesDocument { Items = next.ToArray() }; value.Validate();
            if (favoritesStore is not null) await favoritesStore.SaveAsync(value);
            await OnUiAsync(() =>
            {
                var selectedId = SelectedFavorite?.Id;
                favorites.Clear(); foreach (var item in next) favorites.Add(item);
                SelectedFavorite = next.FirstOrDefault(f => f.Id == selectedId) ?? next.LastOrDefault();
            });
        }
        finally { await OnUiAsync(() => { savingFavorites = false; NotifyFavoriteCommands(); }); idle.SetResult(); }
    }
    private static Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action).Task;
        action(); return Task.CompletedTask;
    }
    private void NotifyFavoriteCommands()
    {
        AddFavorite?.NotifyCanExecuteChanged(); RemoveFavorite?.NotifyCanExecuteChanged(); RenameFavorite?.NotifyCanExecuteChanged();
        MoveFavoriteUp?.NotifyCanExecuteChanged(); MoveFavoriteDown?.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        Left.RequestPath = Right.RequestPath = null;
        Left.RequestListFocus = Right.RequestListFocus = null;
        Left.RequestColumns=Right.RequestColumns=null;GetSelectedPaths=null;RequestFileOperation=null;RequestPreview=null;
        RequestSettings = null; RequestClose = null;
        RequestFavoriteName = null;
        Workspace.PropertyChanged-=WorkspaceChanged; Workspace.Left.PropertyChanged-=PaneChanged; Workspace.Right.PropertyChanged-=PaneChanged;
        if(titleLeft is not null) titleLeft.PropertyChanged-=TitleChanged; if(titleRight is not null) titleRight.PropertyChanged-=TitleChanged;
        Workspace.Dispose();
    }
}

public sealed record FunctionAction(int Number, string Name, string Milestone, AsyncCommand Command,bool Implemented=false)
{
    public string Label => $"F{Number} {Name}";
    public string Description => Number == 10 ? "アプリケーションを終了" : Implemented ? Number==6?"移動・名前変更":Number==8?"ごみ箱へ移動（完全削除はShift+Deleteで確認）":Number==7?"ディレクトリ作成":Name : Number == 6 ? "移動・名前変更（未実装）" : $"{Name}（未実装）";
}
