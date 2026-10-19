using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaneCommander.App.Views;

public partial class PaneView : UserControl
{
    public PaneView()
    {
        InitializeComponent();
        AttachTabReordering();
    }
    public ListView? CurrentFileList=>FindList(this);
    private void DisplayOptions_Click(object sender, RoutedEventArgs e) => DisplayOptionsPopup.IsOpen = true;
    private void DisplayOptions_Opened(object? sender, EventArgs e) => ShowHiddenFiles.Focus();
    private void DisplayOptions_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Escape or System.Windows.Input.Key.Enter)) return;
        DisplayOptionsPopup.IsOpen = false;
        FocusList();
        e.Handled = true;
    }
    public void BeginFilter()
    {
        if (DataContext is not PaneCommander.App.ViewModels.PaneViewModel pane) return;
        if (Window.GetWindow(this)?.DataContext is PaneCommander.App.ViewModels.MainViewModel main) main.Workspace.Activate(pane.State.Side);
        pane.State.SelectedTab.IsFilterOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { FilterBox.Focus(); FilterBox.SelectAll(); });
    }
    private void Filter_Click(object sender,RoutedEventArgs e) => BeginFilter();
    private void Filter_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not PaneCommander.App.ViewModels.PaneViewModel pane) return;
        if (e.Key == System.Windows.Input.Key.Escape) { pane.State.SelectedTab.ClearFilter(); FocusList(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter) { FocusList(); e.Handled = true; }
    }
    public void FocusList(PaneCommander.Core.TabState? expectedTab = null, bool selectFirst = false) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        if (expectedTab is not null && (DataContext is not PaneCommander.App.ViewModels.PaneViewModel pane
            || !pane.State.IsActive || !ReferenceEquals(pane.State.SelectedTab, expectedTab) || expectedTab.IsLoading)) return;
        if (FindList(this) is not { } list) return;
        var selected = list.SelectedItem;
        if (selected is null && selectFirst && list.Items.Count > 0)
        {
            selected = list.Items[0];
            list.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, selected);
        }
        if (selected is not null)
        {
            list.ScrollIntoView(selected);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem row)
            { System.Windows.Input.Keyboard.Focus(row); return; }
        }
        System.Windows.Input.Keyboard.Focus(list);
    });
    public void ReleaseResources() => Release(this);
    private static void Release(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FolderView folder) folder.ReleaseResources(); else Release(child);
        }
    }
    private static ListView? FindList(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListView list && list.IsVisible && list.Name == "Files") return list;
            if (FindList(child) is { } found) return found;
        }
        return null;
    }
}
