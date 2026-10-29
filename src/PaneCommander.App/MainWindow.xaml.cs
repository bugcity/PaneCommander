using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;
using System.ComponentModel;

namespace PaneCommander.App;

public partial class MainWindow : Window
{
    internal HashSet<DateCell> DateCells { get; } = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public Task SessionSaveTask { get; private set; } = Task.CompletedTask;
    private double sidebarWidth = 180;
    private bool sidebarWasVisible;
    private bool savingSession;
    private bool closeApproved;
    private bool closed;
    private WindowState lastNonMinimizedState = WindowState.Normal;
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is MainViewModel { RestoredLayout: { } layout }) RestoreLayout(layout); };
        Closing += SaveBeforeClose;
        SizeChanged += (_, _) => UpdateSidebarLimit();
        PreviewMouseDown += FileDragRecovery_MouseDown;
        Closed += (_, _) => CancelFileDragFocus();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAppearance();
        Model.Left.RequestListFocus = tab => LeftPane.FocusList(tab, selectFirst: true);
        Model.Right.RequestListFocus = tab => RightPane.FocusList(tab, selectFirst: true);
        Model.Left.RequestPath = Model.Right.RequestPath = target => { new PathDialog(target) { Owner = this }.ShowDialog(); return Task.CompletedTask; };
        Model.Left.RequestColumns=Model.Right.RequestColumns=side=>{new SettingsDialog(Model,side){Owner=this}.ShowDialog();return Task.CompletedTask;};
        Model.GetSelectedPaths=()=>((Model.Workspace.Left.IsActive?LeftPane:RightPane).CurrentFileList?.SelectedItems.OfType<FileEntry>().Select(f=>f.FullPath).ToArray())??[];
        Model.GetShellOwner=()=>new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Model.RequestFileOperation=RequestFileOperationAsync;Model.RequestPreview=RequestPreviewAsync;
        Model.RequestSameFolderOperation=SameFolderOperationAsync;Model.RequestCreateFile=CreateFileAsync;
        Model.RequestContextMenu=()=>ShellMenuBehavior.ShowSelectionAsync((Model.Workspace.Left.IsActive?LeftPane:RightPane).CurrentFileList!,Model.Workspace.ActivePane.SelectedTab.CurrentPath,Model);
        Model.RequestArchive = RequestArchiveAsync;
        Model.NotifyFileCommands();
        Closed+=(_,_)=>{Model.Left.RequestListFocus=Model.Right.RequestListFocus=null;Model.Left.RequestColumns=Model.Right.RequestColumns=null;Model.GetSelectedPaths=null;Model.GetShellOwner=null;Model.RequestFileOperation=null;Model.RequestPreview=null;};
        Model.RequestClose = Close;
        Model.RequestSettings = () => { new SettingsDialog(Model) { Owner = this }.ShowDialog(); return Task.CompletedTask; };
        Model.RequestFavoriteName = initial => { var dialog = new FavoriteNameDialog(initial) { Owner = this }; return Task.FromResult(dialog.ShowDialog() == true ? dialog.FavoriteName : null); };
        Model.PropertyChanged += ModelChanged;
        Activated += WindowActivity;
        Activated += FunctionKeysActivated;
        Deactivated += FunctionKeysDeactivated;
        Closed += (_, _) => { Activated -= FunctionKeysActivated; Deactivated -= FunctionKeysDeactivated; Model.SetFunctionModifiers(ModifierKeys.None); Model.RequestSameFolderOperation=null; Model.RequestCreateFile=null; Model.RequestContextMenu=null; Model.RequestArchive=null; ClipboardOperation=null; };
        StateChanged += WindowActivity;
        Closed += (_, _) => { closed = true; Model.PropertyChanged -= ModelChanged; Activated -= WindowActivity; StateChanged -= WindowActivity; LeftPane.ReleaseResources(); RightPane.ReleaseResources(); DateCells.Clear(); Model.Left.RequestPath = Model.Right.RequestPath = null; Model.RequestSettings = null; Model.RequestClose = null; Model.RequestFavoriteName = null; };
        await Model.Workspace.InitializeAsync();
        if (!closed) (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
        ready.TrySetResult();
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Settings)) ApplyAppearance(); if (e.PropertyName == nameof(MainViewModel.IsFileOperationRunning) && !Model.IsFileOperationRunning) QueueFileDragFocus(); }
    internal void RefreshVisibleDates() { foreach (var cell in DateCells.Where(c => c.IsVisible).ToArray()) cell.Refresh(); }
    private void WindowActivity(object? sender, EventArgs e) { if (WindowState != WindowState.Minimized) { lastNonMinimizedState = WindowState; RefreshVisibleDates(); QueueFileDragFocus(); } }
    private void ApplyAppearance() { ThemeManager.Apply(Model.Settings); ApplySidebar(); foreach (var cell in DateCells.ToArray()) cell.Refresh(); }
    private void ApplySidebar()
    {
        var show = Model.Settings.ShowFavorites;
        if (sidebarWasVisible && !show && SidebarColumn.ActualWidth >= 120) sidebarWidth = SidebarColumn.ActualWidth;
        SidebarColumn.MinWidth = show ? 120 : 0;
        SidebarColumn.MaxWidth = show ? 360 : 0;
        SidebarColumn.Width = new GridLength(show ? sidebarWidth : 0);
        SidebarSplitterColumn.Width = new GridLength(show ? 6 : 0);
        FavoritesSidebar.Visibility = SidebarSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        sidebarWasVisible = show;
        UpdateSidebarLimit();
    }
    private void UpdateSidebarLimit()
    {
        if (sidebarWasVisible && PaneGrid.Parent is FrameworkElement body && body.ActualWidth > 0)
            SidebarColumn.MaxWidth = Math.Clamp(body.ActualWidth - 574, 120, 360);
    }
    private void Sidebar_DragCompleted(object sender, DragCompletedEventArgs e) { if (SidebarColumn.ActualWidth >= 120) sidebarWidth = SidebarColumn.ActualWidth; }
    private void Favorite_Click(object sender, RoutedEventArgs e) { Model.SelectedFavorite = (FavoriteFolder)((Button)sender).DataContext; }
    private void Favorites_DragOver(object sender,DragEventArgs e){e.Effects=e.Data.GetDataPresent(FileDragBehavior.FavoriteFormat,false)&&e.Data.GetData(FileDragBehavior.FavoriteFormat,false) is FavoriteTabPayload?DragDropEffects.Link:DragDropEffects.None;e.Handled=true;}
    private async void Favorites_Drop(object sender,DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;
        if(!e.Data.GetDataPresent(FileDragBehavior.FavoriteFormat,false)||e.Data.GetData(FileDragBehavior.FavoriteFormat,false) is not FavoriteTabPayload tab)return;
        try{await Model.AddFavoriteAsync(tab.Name,tab.Path);e.Effects=DragDropEffects.Link;}
        catch(Exception ex)when(ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException){MessageBox.Show(this,ex.Message,"お気に入り登録",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Favorites_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Model.SelectedFavorite is { } favorite) { Model.OpenFavorite.Execute(favorite); e.Handled = true; } }
    private Task RequestFileOperationAsync(FileOperationKind kind)
    {
        if (ShellMenuBehavior.IsOpen) return Task.CompletedTask;
        return RequestFileOperationCoreAsync(kind);
    }
    internal Task CreateDirectoryFromShellAsync(string directory) => RequestFileOperationCoreAsync(FileOperationKind.CreateDirectory, directory);
    private async Task RequestFileOperationCoreAsync(FileOperationKind kind, string? creationDirectory = null)
    {
        try
        {
            var sources=Model.GetSelectedPaths?.Invoke()??[];
            if(kind==FileOperationKind.Delete){await DeleteSelectedAsync(sources,true);return;}
            var creationTab = Model.Workspace.ActivePane.SelectedTab;
            var originalEntry = sources.Length == 1 ? creationTab.Entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.FullPath, sources[0])) : null;
            var dialog=new FileOperationDialog(kind,sources.Length,kind==FileOperationKind.CreateDirectory?"新しいフォルダ":kind==FileOperationKind.CreateFile?"新しいファイル.txt":Model.Workspace.OppositePane.SelectedTab.CurrentPath,sources.Length==1?System.IO.Path.GetDirectoryName(sources[0]):null,
                originalName: sources.Length == 1 ? System.IO.Path.GetFileName(sources[0]) : null, originalIsDirectory: originalEntry?.IsDirectory == true){Owner=this};
            if(dialog.ShowDialog()!=true)return;
            var request=kind is FileOperationKind.CreateDirectory or FileOperationKind.CreateFile?new FileOperationRequest(kind,[],creationDirectory ?? creationTab.CurrentPath,dialog.Destination):new FileOperationRequest(kind,sources,dialog.Destination,dialog.NewName);
            var result = await Model.ExecuteFileOperationAsync(request,new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (kind is FileOperationKind.CreateFile or FileOperationKind.CreateDirectory && !result.Aborted && creationTab.CurrentPath == request.Destination)
            {
                var created = System.IO.Path.Combine(request.Destination!, request.NewName!);
                creationTab.SelectedEntry = creationTab.Entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.FullPath, created));
            }
        }
        finally
        {
            if (!closed) (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
        }
    }
    private bool previewOpen;
    private bool sameFolderDialogOpen;
    private async Task SameFolderOperationAsync(FileOperationKind kind)
    {
        if(sameFolderDialogOpen || Model.IsFileOperationRunning || ShellMenuBehavior.IsOpen)return;
        var sources=Model.GetSelectedPaths?.Invoke()??[];
        if(sources.Length==0 || kind==FileOperationKind.Move && sources.Length!=1)return;
        sameFolderDialogOpen=true;
        try
        {
            var requests=new List<FileOperationRequest>();
            foreach(var source in sources)
            {
                var folder=System.IO.Path.GetDirectoryName(source)!;
                var originalEntry = Model.Workspace.ActivePane.SelectedTab.Entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.FullPath, source));
                var dialog=new FileOperationDialog(kind,1,folder,folder,System.IO.Path.GetFileName(source),originalIsDirectory:originalEntry?.IsDirectory==true){Owner=this};
                if(dialog.ShowDialog()!=true)return;
                requests.Add(SameFolderOperation.Create(kind,source,dialog.NewName!));
            }
            if(requests.Select(r=>System.IO.Path.Combine(r.Destination!,r.NewName!)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=requests.Count)
                throw new ArgumentException("コピー先の名前が重複しています。");
            await Model.ExecuteFileOperationBatchAsync(requests,new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {MessageBox.Show(this,ex.Message,"同じフォルダーでの操作",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally
        {
            sameFolderDialogOpen=false;
            if(!closed)(Model.Workspace.Left.IsActive?LeftPane:RightPane).FocusList();
        }
    }
    private async Task RequestPreviewAsync(string path)
    {
        if (previewOpen || ShellMenuBehavior.IsOpen) return;
        previewOpen = true;
        try
        {
            var text = await PaneCommander.Infrastructure.TextPreviewReader.ReadAsync(path);
            if (!closed) new TextPreviewDialog(path, text) { Owner = this }.ShowDialog();
        }
        finally
        {
            previewOpen = false;
            if (!closed) (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
        }
    }
    private void RestoreLayout(WindowLayout layout)
    {
        layout.Validate();
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(layout.Left, layout.Top, layout.Width, layout.Height);
        var intersection = Rect.Intersect(area, rect);
        if (intersection.IsEmpty || intersection.Width < 100 || intersection.Height < 80)
        {
            var work = SystemParameters.WorkArea;
            rect = new Rect(work.Left + 20, work.Top + 20, Math.Max(MinWidth, Math.Min(layout.Width, work.Width - 40)), Math.Max(MinHeight, Math.Min(layout.Height, work.Height - 40)));
        }
        rect.Width = Math.Max(MinWidth, Math.Min(rect.Width, area.Width));
        rect.Height = Math.Max(MinHeight, Math.Min(rect.Height, area.Height));
        rect.X = Math.Clamp(rect.X, area.Left - rect.Width + 100, area.Right - 100);
        rect.Y = Math.Clamp(rect.Y, area.Top, Math.Max(area.Top, area.Bottom - 80));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left; Top = rect.Top; Width = rect.Width; Height = rect.Height;
        LeftPaneColumn.Width = new GridLength(layout.LeftPaneFraction, GridUnitType.Star);
        RightPaneColumn.Width = new GridLength(1 - layout.LeftPaneFraction, GridUnitType.Star);
        sidebarWidth = layout.SidebarWidth;
        if (layout.Maximized) { WindowState = WindowState.Maximized; lastNonMinimizedState = WindowState.Maximized; }
    }
    public WindowLayout CaptureLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var total = LeftPaneColumn.ActualWidth + RightPaneColumn.ActualWidth;
        return new WindowLayout { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized || WindowState == WindowState.Minimized && lastNonMinimizedState == WindowState.Maximized, LeftPaneFraction = total > 0 ? LeftPaneColumn.ActualWidth / total : .5,
            SidebarWidth = sidebarWasVisible ? Math.Clamp(SidebarColumn.ActualWidth, 120, 360) : sidebarWidth };
    }
    private async void SaveBeforeClose(object? sender, CancelEventArgs e)
    {
        if (ShellMenuBehavior.IsOpen) { e.Cancel = true; return; }
        if (closeApproved || DataContext is not MainViewModel model || !model.HasSessionStore && !model.IsArchiveOperationRunning) return;
        if (model.IsArchiveOperationRunning) model.CancelActiveArchive();
        e.Cancel = true;
        if (savingSession) return;
        savingSession = true;
        IsEnabled = false;
        try
        {
            SessionSaveTask = model.HasSessionStore ? model.SaveSessionAsync(CaptureLayout()) : model.WaitForOperationsAsync();
            await SessionSaveTask;
            // Always leave the original Closing callback before requesting Close again.
            // This also handles a store whose Task completed synchronously.
            _ = Dispatcher.InvokeAsync(() => { savingSession = false; IsEnabled = true; closeApproved = true; Close(); });
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                savingSession = false; IsEnabled = true;
                MessageBox.Show(this, "作業状態を保存できないため終了を中止しました。保存先を確認して再度終了してください。\n" + ex.Message, "作業状態", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }
    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var view = (PaneView)sender;
        if (view.DataContext is not PaneViewModel pane) return;
        Model.Workspace.Activate(pane.State.Side);
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton == MouseButton.Left && FileDragBehavior.Ancestor<ListView>(source) is { } list
            && ItemsControl.ContainerFromElement(list, source) is null
            && !HasAncestor<GridViewColumnHeader>(source) && !HasAncestor<ScrollBar>(source))
            list.Focus();
        else if (e.ChangedButton == MouseButton.Left && !HasAncestor<ListView>(source)
            && !HasAncestor<ButtonBase>(source) && !HasAncestor<TextBoxBase>(source) && !HasAncestor<PasswordBox>(source)
            && !HasAncestor<TabItem>(source) && !HasAncestor<Thumb>(source) && !HasAncestor<ScrollBar>(source))
            view.FocusList(pane.State.SelectedTab, selectFirst: true);
    }
    private void Pane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((PaneView)sender).DataContext is PaneViewModel pane) Model.Workspace.Activate(pane.State.Side);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        UpdateFunctionModifiers(Keyboard.Modifiers);
        if (ShellMenuBehavior.IsOpen) return;
        // TextBox内部のScrollViewer等にも適用。文字編集のキーを横取りしない。
        if (HasAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) || HasAncestor<PasswordBox>(e.OriginalSource as DependencyObject)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (FileDragBehavior.Ancestor<ListView>(e.OriginalSource as DependencyObject) is { } selectingList
            && FileMarqueeSelection.Active(selectingList) is { } selection)
        {
            if (key == Key.Escape) selection.Finish(cancel: true);
            e.Handled = true;
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if (HandleFileClipboardKey(key, modifiers, e.OriginalSource as DependencyObject)) { e.Handled=true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key is Key.Left or Key.Right)
        {
            (key == Key.Left ? LeftPane : RightPane).MoveTabHere(Model.Workspace.ActivePane.SelectedTab);
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.Left or Key.Right)
        {
            Model.Workspace.ActivePane.MoveSelectedTab(key == Key.Left ? -1 : 1);
            (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Shift && key is Key.Left or Key.Right)
        {
            Model.Workspace.ActivePane.SelectAdjacentTab(key == Key.Left ? -1 : 1);
            (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
            e.Handled = true;
            return;
        }
        if (key == Key.Delete && modifiers is ModifierKeys.None or ModifierKeys.Shift && HasAncestor<ListView>(e.OriginalSource as DependencyObject) && HasAncestor<FolderView>(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            var selected=FileDragBehavior.Ancestor<ListView>(e.OriginalSource as DependencyObject)?.SelectedItems.OfType<FileEntry>().Select(f=>f.FullPath).ToArray()??[];
            if (!Model.IsFileOperationRunning && selected.Length>0) _ = DeleteSelectedAsync(selected,modifiers!=ModifierKeys.Shift);
            return;
        }
        if (modifiers == ModifierKeys.None && HasAncestor<ListView>(e.OriginalSource as DependencyObject) && HasAncestor<FolderView>(e.OriginalSource as DependencyObject))
        {
            var pane = Model.Workspace.Left.IsActive ? LeftPane : RightPane;
            if (key == Key.Insert && FileDragBehavior.Ancestor<ListView>(e.OriginalSource as DependencyObject) is { } list && ReferenceEquals(list, pane.CurrentFileList))
            { FileSelection.Toggle(list, e.OriginalSource as DependencyObject); e.Handled = true; return; }
            if (key is Key.OemQuestion or Key.Divide) { pane.BeginFilter(); e.Handled = true; return; }
            if (key == Key.Escape && (Model.Workspace.ActivePane.SelectedTab.FilterText.Length > 0 || Model.Workspace.ActivePane.SelectedTab.IsFilterOpen)) { Model.Workspace.ActivePane.SelectedTab.ClearFilter(); e.Handled = true; return; }
            if (key == Key.Escape && Model.Workspace.ActivePane.SelectedTab.DismissOperationStatus()) { e.Handled = true; return; }
        }
        AsyncCommand? command = null;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift && HasAncestor<FolderView>(e.OriginalSource as DependencyObject))
        {
            Model.Workspace.Toggle();
            (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList();
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control)
            command = key switch { Key.T => Model.Active.AddTab, Key.W => Model.Active.CloseTab, Key.R => Model.RefreshActive, Key.D => Model.AddFavorite, Key.H => Model.Active.ToggleHiddenFiles, _ => null };
        else if (key >= Key.F1 && key <= Key.F10 && modifiers is ModifierKeys.None or ModifierKeys.Shift) command = Model.Functions[(int)key - (int)Key.F1].Command;
        else if (HasAncestor<FolderView>(e.OriginalSource as DependencyObject) && HasAncestor<ListView>(e.OriginalSource as DependencyObject))
        {
            if (modifiers == ModifierKeys.Alt) command = key switch { Key.Left => Model.Active.Back, Key.Right => Model.Active.Forward, _ => null };
            else if (modifiers == ModifierKeys.None) command = key switch { Key.Enter => Model.Active.Open, Key.Back => Model.Active.Up, _ => null };
        }
        if (command is null) return;
        e.Handled = true;
        var target = key == Key.Enter && HasAncestor<FolderView>(e.OriginalSource as DependencyObject)
            && FileDragBehavior.Ancestor<ListViewItem>(e.OriginalSource as DependencyObject) is { IsSelected: true, DataContext: FileEntry entry }
            ? entry : null;
        command.Execute(target);
    }
    private static bool HasAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private bool createFileDialogOpen;
    private async Task CreateFileAsync()
    {
        if (createFileDialogOpen || ShellMenuBehavior.IsOpen) return;
        createFileDialogOpen = true;
        try { await RequestFileOperationAsync(FileOperationKind.CreateFile); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        { MessageBox.Show(this, ex.Message, "ファイルの新規作成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            createFileDialogOpen = false;
            if (!closed) (Model.Workspace.Left.IsActive ? LeftPane : RightPane).FocusList(selectFirst: true);
        }
    }
}
