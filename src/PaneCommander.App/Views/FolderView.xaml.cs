using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public partial class FolderView : UserControl
{
    private MainViewModel? main;
    private TabState? tab;
    private bool datesRefreshPending;
    private bool dragAttached;
    private readonly Dictionary<string,GridViewColumn> columns;
    public static readonly DependencyProperty ShowModifiedProperty=DependencyProperty.Register(nameof(ShowModified),typeof(bool),typeof(FolderView),new PropertyMetadata(true));
    public bool ShowModified { get=>(bool)GetValue(ShowModifiedProperty);set=>SetValue(ShowModifiedProperty,value); }
    public bool ShowFileAge { get;private set; }=true;
    public ListView FileList=>Files;
    public FolderView()
    {
        InitializeComponent();
        _ = new FileListFilter(Files, () => DataContext as TabState);
        columns=((GridView)Files.View).Columns.Zip(new[]{"Name","Kind","Size","FileAge","LastWriteTime"}).ToDictionary(p=>p.Second,p=>p.First);
        Loaded += (_, _) => { main = Window.GetWindow(this)?.DataContext as MainViewModel; if (main is not null) PropertyChangedEventManager.AddHandler(main, SettingsChanged, ""); ResizeColumns(); };
        Unloaded += (_, _) => ReleaseResources();
        Files.SizeChanged += (_, _) => ResizeColumns();
        Files.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ViewportWidthChange != 0) ResizeColumns();
        }));
        DataContextChanged += (_, _) => { if (tab is not null) PropertyChangedEventManager.RemoveHandler(tab, TabChanged, ""); tab = DataContext as TabState; if (tab is not null) PropertyChangedEventManager.AddHandler(tab, TabChanged, ""); ApplySort(); };
        Loaded += (_, _) => { if (tab is not null) { PropertyChangedEventManager.RemoveHandler(tab, TabChanged, ""); PropertyChangedEventManager.AddHandler(tab, TabChanged, ""); } ApplySort(); RefreshDatesAfterLayout(); };
        Files.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(SortClick));
        // GridViewColumnHeader handles the gripper's routed event before it reaches Files.
        Files.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler(ColumnResizeCompleted), handledEventsToo: true);
        Files.SelectionChanged+=(_,_)=>main?.NotifyFileCommands();
        Loaded+=(_,_)=>{if(!dragAttached&&main is not null&&tab is not null){FileDragBehavior.Attach(Files,()=>tab!,main);ShellMenuBehavior.Attach(Files,()=>tab?.CurrentPath,main);dragAttached=true;}};
    }
    private async void ColumnResizeCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs args)
    {
        if (args.Canceled || main is null || Files.View is not GridView) return;
        if (FileDragBehavior.Ancestor<GridViewColumnHeader>(args.OriginalSource as DependencyObject)?.Column is not { } resizedColumn) return;
        var key = columns.FirstOrDefault(pair => ReferenceEquals(pair.Value, resizedColumn)).Key;
        if (key is null) return;
        var model = main; var settings=model.Settings; var side=PaneModel?.State.Side??PaneSide.Left;
        var layout = settings.ColumnsFor(side);
        var next = layout with
        {
            NameFillsRemaining = layout.NameFillsRemaining && key != "Name",
            Columns = layout.Columns.Select(c => c.Key == key
                ? c with { Width = Math.Clamp(resizedColumn.ActualWidth, 24, 4000) } : c).ToArray()
        };
        try { await model.ApplySettingsAsync(side==PaneSide.Left ? settings with {LeftColumns=next} : settings with {RightColumns=next}); }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        { MessageBox.Show(Window.GetWindow(this), "列幅を保存できません: " + ex.Message, "表示設定", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    public void ReleaseResources()
    {
        if (main is not null) PropertyChangedEventManager.RemoveHandler(main, SettingsChanged, ""); main = null;
        if (tab is not null) PropertyChangedEventManager.RemoveHandler(tab, TabChanged, "");
    }
    private void TabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == nameof(TabState.Entries)) { ApplySort(); RefreshDatesAfterLayout(); } if (args.PropertyName == nameof(TabState.IsLoading) && tab?.IsLoading == false) RefreshDatesAfterLayout(); }
    private void RefreshDatesAfterLayout()
    {
        if (Dispatcher.HasShutdownStarted || datesRefreshPending) return;
        datesRefreshPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            datesRefreshPending = false;
            if (IsLoaded) (Window.GetWindow(this) as MainWindow)?.RefreshVisibleDates();
        });
    }
    private void SortClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not GridViewColumnHeader header || tab is null) return;
        var property = header.Content?.ToString() switch { "名前" => nameof(FileEntry.Name), "種類" => nameof(FileEntry.Kind), "サイズ" or "サイズ (B)" => nameof(FileEntry.Size), "File Age" => "FileAge", "更新日時" => nameof(FileEntry.LastWriteTime), _ => null };
        if (property is null) return;
        tab.SortDescending = tab.SortProperty == property && !tab.SortDescending; tab.SortProperty = property;
        ApplySort();
    }
    private void ApplySort()
    {
        if (tab is null || Files.ItemsSource is null || Dispatcher.HasShutdownStarted) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Files.ItemsSource);
        if (view is null) return;
        using (view.DeferRefresh()) { view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(nameof(FileEntry.IsDirectory), System.ComponentModel.ListSortDirection.Descending)); view.SortDescriptions.Add(new(tab.SortProperty == "FileAge" ? nameof(FileEntry.LastWriteTime) : tab.SortProperty, (tab.SortDescending ^ (tab.SortProperty == "FileAge")) ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending)); }
    }
    private void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == nameof(MainViewModel.Settings)) { ResizeColumns(); RefreshDatesAfterLayout(); } }
    private void ResizeColumns()
    {
        if (Files.View is not GridView view) return;
        var settings = main?.Settings ?? new AppearanceSettings();
        var layout=settings.ColumnsFor(PaneModel?.State.Side??PaneSide.Left);
        for(var i=0;i<layout.Columns.Length;i++) { var column=columns[layout.Columns[i].Key]; if(view.Columns[i]!=column) {view.Columns.Remove(column);view.Columns.Insert(i,column);} }
        double Measure(string text) => new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(ThemeManager.ResolveFont(settings.FontFamily),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),settings.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace+18;
        double Minimum(string key) => key switch {"Name"=>Measure("名前")+24,"Size"=>Math.Max(Measure("サイズ"),Measure("999.99 TB")),"FileAge"=>Math.Max(Measure("File Age"),Measure("999 y")),"LastWriteTime"=>Measure("0000-00-00 00:00:00"),_=>Measure("[DIR] ↗")};
        var scroll = FindDescendant<ScrollViewer>(Files);
        var viewport = scroll is { ViewportWidth: > 0 } ? scroll.ViewportWidth : Files.ActualWidth;
        // The viewport already excludes the actual scrollbar and outer list padding.
        // Only the row template's horizontal content padding remains to be reserved.
        var row = FindDescendant<ListViewItem>(Files);
        var fallbackPadding = Files.ItemContainerStyle?.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == PaddingProperty)?.Value is Thickness configured ? configured : new Thickness(3, 2, 3, 2);
        var padding = row?.Padding ?? fallbackPadding;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // GridViewRowPresenter reserves 2 DIP for the trailing dummy header.
        const double trailingHeader = 2;
        var available = Math.Max(0, Math.Floor((viewport - padding.Left - padding.Right - trailingHeader) * dpi) / dpi);
        var widths=AdaptiveColumns.Calculate(layout,available,Minimum);
        var ageWasShown=ShowFileAge;
        ShowModified=widths["LastWriteTime"]>0;ShowFileAge=widths["FileAge"]>0;
        foreach(var spec in layout.Columns)
            if (columns[spec.Key].Width != widths[spec.Key]) columns[spec.Key].Width=widths[spec.Key];
        if(ageWasShown!=ShowFileAge && Window.GetWindow(this) is MainWindow window)
            foreach(var cell in window.DateCells.Where(c=>FileDragBehavior.Ancestor<FolderView>(c)==this).ToArray())cell.Refresh();
    }
    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private PaneViewModel? PaneModel
    {
        get
        {
            DependencyObject? current = this;
            while (current is not null)
            {
                if (current is PaneView pane) return pane.DataContext as PaneViewModel;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 空白・列ヘッダーのダブルクリックで前の選択を起動しない。
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(Files, e.OriginalSource as DependencyObject) is not ListViewItem { DataContext: FileEntry entry }) return;
        PaneModel?.Open.Execute(entry);
        e.Handled = true;
    }
}
