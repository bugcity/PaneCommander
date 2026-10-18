using System.Windows;
using System.Windows.Controls;

namespace PaneCommander.App.Views;

public static class FileSelection
{
    public static void Toggle(ListView list, DependencyObject? focusedElement)
    {
        var row = focusedElement is null ? null : ItemsControl.ContainerFromElement(list, focusedElement) as ListViewItem;
        var item = row?.Content ?? list.SelectedItem;
        if (item is null || !list.Items.Contains(item)) return;
        if (list.SelectedItems.Contains(item)) list.SelectedItems.Remove(item);
        else list.SelectedItems.Add(item);
        // Keep the focused row even when it becomes unselected, so a second Insert reverses it.
    }
}
