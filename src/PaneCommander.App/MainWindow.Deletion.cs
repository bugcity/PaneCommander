using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.App;

public partial class MainWindow
{
    private bool deleting;
    private async Task DeleteSelectedAsync(string[] selected, bool recycle)
    {
        if (deleting || Model.IsFileOperationRunning || selected.Length == 0) return;
        deleting = true;
        var pane = Model.Workspace.Left.IsActive ? LeftPane : RightPane;
        var tab = Model.Workspace.ActivePane.SelectedTab;
        var path = tab.CurrentPath;
        var list = pane.CurrentFileList;
        var selectedPaths = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchor = 0;
        IInputElement? previousFocus = Keyboard.FocusedElement;
        void Capture()
        {
            if (list is null || !ReferenceEquals(list.DataContext, tab)) return;
            selectedPaths = list.SelectedItems.OfType<FileEntry>().Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var first = list.Items.OfType<FileEntry>().FirstOrDefault(e => selectedPaths.Contains(e.FullPath));
            anchor = first is null ? anchor : list.Items.IndexOf(first);
            if (list.IsKeyboardFocusWithin) previousFocus = Keyboard.FocusedElement;
        }
        Capture();
        tab.RefreshingSnapshot += Capture;
        try
        {
            await Model.ExecuteFileOperationAsync(new(FileOperationKind.Delete, selected, Recycle: recycle),
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        { MessageBox.Show(this, ex.Message, recycle ? "ごみ箱へ移動" : "完全削除", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            tab.RefreshingSnapshot -= Capture;
            try
            {
                // Run after binding, filtering and sorting have finished replacing the rows.
                if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() =>
                {
                    if (closed || list is null || !list.IsLoaded || !ReferenceEquals(pane.CurrentFileList, list)
                        || !ReferenceEquals(Model.Workspace.ActivePane.SelectedTab, tab) || tab.CurrentPath != path || tab.IsLoading) return;
                    var focus = Keyboard.FocusedElement;
                    if (!ReferenceEquals(focus, previousFocus) && !list.IsKeyboardFocusWithin && focus is not null && !ReferenceEquals(focus, this)) return;
                    var items = list.Items.OfType<FileEntry>().ToArray();
                    var remaining = items.Where(e => selectedPaths.Contains(e.FullPath)).ToArray();
                    var target = remaining.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, tab.SelectedEntry?.FullPath))
                        ?? remaining.FirstOrDefault() ?? (items.Length == 0 ? null : items[Math.Clamp(anchor, 0, items.Length - 1)]);
                    list.SelectedItems.Clear();
                    list.SetCurrentValue(Selector.SelectedItemProperty, target);
                    foreach (var item in remaining) if (!list.SelectedItems.Contains(item)) list.SelectedItems.Add(item);
                    if (target is not null)
                    {
                        list.ScrollIntoView(target); list.UpdateLayout();
                        if (list.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem row) { Keyboard.Focus(row); return; }
                    }
                    Keyboard.Focus(list);
                }, DispatcherPriority.Input);
            }
            finally { deleting = false; }
        }
    }
}
