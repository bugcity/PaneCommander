using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.App;

/// <summary>Event-driven monitoring; background tabs are invalidated, not enumerated.</summary>
public sealed class WorkspaceDirectoryMonitor : IDisposable
{
    private readonly MainWindow window;
    private readonly MainViewModel model;
    private readonly IFolderWatchService source;
    private readonly IErrorLogger logger;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TabState> tracked = [];
    private readonly HashSet<TabState> dirty = [];
    private volatile bool disposed;
    private bool refreshing;

    private sealed class Subscription(string path)
    {
        public string Path { get; } = path;
        public IDisposable? Handle;
        public bool Faulted;
        public int Queued;
        public Exception? Error;
    }

    public WorkspaceDirectoryMonitor(MainWindow window, MainViewModel model, IFolderWatchService source, IErrorLogger logger)
    {
        this.window = window; this.model = model; this.source = source; this.logger = logger;
        timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += Tick;
        model.PropertyChanged += ModelChanged;
        foreach (var pane in Panes)
        {
            pane.PropertyChanged += PaneChanged;
            ((INotifyCollectionChanged)pane.Tabs).CollectionChanged += TabsChanged;
        }
        Synchronize();
    }

    private IEnumerable<PaneState> Panes => [model.Workspace.Left, model.Workspace.Right];
    private IEnumerable<TabState> Visible => Panes.Select(p => p.SelectedTab);
    private static string PathKey(TabState tab) => System.IO.Path.TrimEndingDirectorySeparator(tab.CurrentPath);
    private bool IsCurrent(Subscription item) => !disposed && subscriptions.TryGetValue(item.Path, out var current) && ReferenceEquals(item, current);

    private void Synchronize()
    {
        if (disposed) return;
        var tabs = Panes.SelectMany(p => p.Tabs).ToHashSet();
        foreach (var removed in tracked.Except(tabs).ToArray())
        { removed.PropertyChanged -= TabChanged; tracked.Remove(removed); dirty.Remove(removed); }
        foreach (var added in tabs.Except(tracked)) { added.PropertyChanged += TabChanged; tracked.Add(added); }
        var paths = tabs.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in subscriptions.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray())
        { subscriptions[removed].Handle?.Dispose(); subscriptions.Remove(removed); }
        foreach (var path in paths)
        {
            if (subscriptions.ContainsKey(path)) continue;
            var item = new Subscription(path); subscriptions.Add(path, item);
            _ = OpenAsync(item);
        }
        foreach (var tab in tabs)
            tab.WatchWarning = subscriptions[PathKey(tab)].Faulted ? "自動更新停止 · Ctrl+Rで再接続を試せます" : "";
        Schedule();
    }

    private async Task OpenAsync(Subscription item)
    {
        IDisposable? handle = null;
        try
        {
            handle = await Task.Run(() => source.Watch(item.Path, () => Signal(item, null), error => Signal(item, error))).ConfigureAwait(false);
            if (window.Dispatcher.HasShutdownStarted) return;
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(item) || item.Faulted) return;
                item.Handle = handle; handle = null;
                // Close the gap between the last read and enabling notifications.
                MarkDirty(item);
            });
        }
        catch (Exception error)
        {
            if (!window.Dispatcher.HasShutdownStarted)
                await window.Dispatcher.InvokeAsync(() => { if (IsCurrent(item)) Fault(item, error); });
        }
        finally { handle?.Dispose(); }
    }

    private void Signal(Subscription item, Exception? error)
    {
        if (disposed || window.Dispatcher.HasShutdownStarted) return;
        if (error is not null) Interlocked.CompareExchange(ref item.Error, error, null);
        if (Interlocked.Exchange(ref item.Queued, 1) != 0) return;
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref item.Queued, 0);
            if (!IsCurrent(item) || item.Faulted) return;
            var failure = Interlocked.Exchange(ref item.Error, null);
            if (failure is not null) Fault(item, failure);
            MarkDirty(item);
        });
    }

    private void Fault(Subscription item, Exception error)
    {
        if (item.Faulted) return;
        item.Faulted = true; item.Handle?.Dispose(); item.Handle = null;
        foreach (var tab in tracked.Where(t => StringComparer.OrdinalIgnoreCase.Equals(PathKey(t), item.Path)))
            tab.WatchWarning = "自動更新停止 · Ctrl+Rで再接続を試せます";
        logger.Log("フォルダー変更監視", error);
    }
    private void MarkDirty(Subscription item)
    {
        foreach (var tab in tracked.Where(t => StringComparer.OrdinalIgnoreCase.Equals(PathKey(t), item.Path))) dirty.Add(tab);
        Schedule();
    }
    private void Schedule()
    {
        if (!disposed && !refreshing && !timer.IsEnabled && !model.IsFileOperationRunning && Visible.Any(t => dirty.Contains(t) && !t.IsLoading)) timer.Start();
    }
    private async void Tick(object? sender, EventArgs args)
    {
        timer.Stop();
        if (disposed || refreshing || model.IsFileOperationRunning) return;
        refreshing = true;
        try
        {
            foreach (var tab in Visible.Distinct().ToArray())
            {
                if (disposed || model.IsFileOperationRunning) break;
                if (tab.IsLoading || !dirty.Remove(tab)) continue;
                await RefreshVisibleAsync(tab);
            }
        }
        catch (Exception error) { logger.Log("フォルダー自動更新", error); }
        finally { refreshing = false; Schedule(); }
    }

    private async Task RefreshVisibleAsync(TabState tab)
    {
        var pane = ReferenceEquals(model.Workspace.Left.SelectedTab, tab) ? window.FindName("LeftPane") as PaneView : window.FindName("RightPane") as PaneView;
        var list = pane?.CurrentFileList;
        var path = tab.CurrentPath;
        HashSet<string> selected = [];
        string? primary = null, focusPath = null, anchor = null;
        IInputElement? oldFocus = null;
        var hadFocus = false;
        ScrollViewer? scroll = null;
        double offset = 0, horizontal = 0;
        void Capture()
        {
            // Capture at replacement, so input made while the directory was being read wins.
            selected = list?.SelectedItems.OfType<FileEntry>().Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            primary = tab.SelectedEntry?.FullPath; oldFocus = Keyboard.FocusedElement;
            hadFocus = list?.IsKeyboardFocusWithin == true;
            focusPath = FileDragBehavior.Ancestor<ListViewItem>(oldFocus as DependencyObject)?.DataContext is FileEntry entry ? entry.FullPath : null;
            scroll = list is null ? null : FindScroll(list);
            offset = scroll?.VerticalOffset ?? 0; horizontal = scroll?.HorizontalOffset ?? 0;
            anchor = list is not null && (int)offset < list.Items.Count && list.Items[(int)offset] is FileEntry top ? top.FullPath : null;
        }
        tab.RefreshingSnapshot += Capture;
        try { await tab.RefreshAsync(); }
        finally { tab.RefreshingSnapshot -= Capture; }
        if (disposed || list is null || !list.IsLoaded || !ReferenceEquals(list.DataContext, tab) || tab.CurrentPath != path || tab.IsLoading || !tab.NavigationSucceeded) return;
        list.UpdateLayout();
        var items = list.Items.OfType<FileEntry>().ToArray();
        if (hadFocus && (ReferenceEquals(Keyboard.FocusedElement, oldFocus) || list.IsKeyboardFocusWithin || Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, window)))
        {
            var focused = items.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, focusPath));
            if (focused is not null)
            {
                list.ScrollIntoView(focused); list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(focused) is ListViewItem row) Keyboard.Focus(row);
            }
            else list.Focus();
        }
        list.SelectedItems.Clear();
        list.SelectedItem = items.FirstOrDefault(e => selected.Contains(e.FullPath) && StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, primary));
        foreach (var item in items.Where(e => selected.Contains(e.FullPath))) if (!list.SelectedItems.Contains(item)) list.SelectedItems.Add(item);
        if (anchor is not null && Array.FindIndex(items, e => StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, anchor)) is var index && index >= 0)
            offset = index + offset - Math.Floor(offset);
        var snapshot = tab.Entries;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (disposed || !list.IsLoaded || !ReferenceEquals(list.DataContext, tab) || !ReferenceEquals(tab.Entries, snapshot)) return;
            scroll?.ScrollToVerticalOffset(offset); scroll?.ScrollToHorizontalOffset(horizontal);
        });
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs args) => Synchronize();
    private void PaneChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(PaneState.SelectedTab)) Schedule(); }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(MainViewModel.IsFileOperationRunning)) Schedule(); }
    private void TabChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TabState.CurrentPath)) Synchronize();
        else if (args.PropertyName == nameof(TabState.IsLoading))
        {
            if (!refreshing && sender is TabState { IsLoading: false, NavigationSucceeded: true } tab
                && subscriptions.TryGetValue(PathKey(tab), out var item) && item.Faulted)
            {
                subscriptions.Remove(item.Path);
                Synchronize();
            }
            Schedule();
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); timer.Tick -= Tick;
        model.PropertyChanged -= ModelChanged;
        foreach (var pane in Panes)
        {
            pane.PropertyChanged -= PaneChanged;
            ((INotifyCollectionChanged)pane.Tabs).CollectionChanged -= TabsChanged;
        }
        foreach (var tab in tracked) tab.PropertyChanged -= TabChanged;
        foreach (var item in subscriptions.Values) item.Handle?.Dispose();
        subscriptions.Clear(); tracked.Clear(); dirty.Clear();
    }
}
