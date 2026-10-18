using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

// A state object belongs to a visible list; it never subscribes to all workspace tabs.
internal sealed class FileListFilter
{
    private readonly ListView list;
    private readonly Func<TabState?> current;
    private TabState? tab;
    private ICollectionView? view;
    private string query = "";
    private bool showHidden;
    private bool showDot;
    private long revision;
    public FileListFilter(ListView list, Func<TabState?> current)
    {
        this.list = list; this.current = current;
        var descriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView));
        EventHandler sourceChanged = (_,_) => Apply();
        list.Loaded += (_,_) => { descriptor.AddValueChanged(list,sourceChanged); Bind(); }; list.DataContextChanged += (_,_) => { if (list.IsLoaded) Bind(); };
        list.Unloaded += (_,_) => { descriptor.RemoveValueChanged(list,sourceChanged); Release(); };
        list.IsVisibleChanged += (_,_) => { if (list.IsVisible) Bind(); else Release(); };
    }
    private void Release() { if (tab is not null) PropertyChangedEventManager.RemoveHandler(tab, Changed, ""); tab = null; view = null; revision++; }
    private void Bind()
    {
        Release(); if (!list.IsVisible) return;
        tab = current(); if (tab is null) return;
        PropertyChangedEventManager.AddHandler(tab,Changed,""); Apply();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(TabState.FilterText) or nameof(TabState.Entries) or nameof(TabState.ShowHiddenFiles) or nameof(TabState.ShowDotFiles)) Apply(); }
    private void Apply()
    {
        if (tab is null || list.ItemsSource is null) return;
        var next = CollectionViewSource.GetDefaultView(list.ItemsSource); if (next is null) return;
        if (ReferenceEquals(view,next) && query == tab.FilterText && showHidden == tab.ShowHiddenFiles && showDot == tab.ShowDotFiles) return;
        view = next; query = tab.FilterText; showHidden = tab.ShowHiddenFiles; showDot = tab.ShowDotFiles;
        var selected = list.SelectedItems.Cast<object>().ToArray();
        var scroll = Descendants<ScrollViewer>(list).FirstOrDefault(); var offset = scroll?.VerticalOffset ?? 0;
        var text = query;
        var includeHidden = showHidden;
        var includeDot = showDot;
        view.Filter = text.Length == 0 && includeHidden && includeDot ? null : value => value is FileEntry entry
            && (includeHidden || !entry.IsHidden) && (includeDot || !entry.IsDotFile) && FileNameFilter.Matches(entry.Name,text);
        foreach (var value in selected) if (view.Contains(value) && !list.SelectedItems.Contains(value)) list.SelectedItems.Add(value);
        var version = ++revision;
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (version == revision && list.IsVisible) scroll?.ScrollToVerticalOffset(offset); });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    { for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i); if(child is T result)yield return result; foreach(var nested in Descendants<T>(child))yield return nested; } }
}
