using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.App;

public partial class MainWindow
{
    internal sealed record DragFocusState(PaneView Pane, ListView List, TabState Tab, string Path,
        string[] SelectedPaths, string? PrimaryPath, int Index, IInputElement? PreviousFocus)
    {
        public bool Ready { get; set; }
    }
    private DragFocusState? dragFocus;

    internal DragFocusState? BeginFileDragFocus(ListView list, TabState tab)
    {
        CancelFileDragFocus();
        if (FileDragBehavior.Ancestor<PaneView>(list) is not { DataContext: PaneViewModel pane } view
            || !ReferenceEquals(pane.State.SelectedTab, tab)) return null;
        if (!list.IsKeyboardFocusWithin) list.Focus(); // Deferred multi-selection mouse-down can leave focus outside the source list.
        var selected = list.SelectedItems.OfType<FileEntry>().ToArray();
        var primary = tab.SelectedEntry;
        dragFocus = new(view, list, tab, tab.CurrentPath, selected.Select(e => e.FullPath).ToArray(), primary?.FullPath,
            Math.Max(0, list.Items.IndexOf(primary ?? selected.FirstOrDefault())), Keyboard.FocusedElement);
        tab.PropertyChanged += DragFocusTabChanged;
        return dragFocus;
    }

    internal void CompleteFileDragFocus(DragFocusState? state)
    {
        if (state is null || !ReferenceEquals(dragFocus, state)) return;
        state.Ready = true;
        QueueFileDragFocus();
    }

    private void QueueFileDragFocus()
    {
        if (!IsActive || dragFocus is not { Ready: true } state || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsActive && ReferenceEquals(dragFocus, state)) RecoverFileDragFocus();
        });
    }

    // Called only after activation/layout in production. Tests can exercise it without activating an OS window.
    internal void RecoverFileDragFocus()
    {
        if (dragFocus is not { Ready: true } state) return;
        if (closed || !state.List.IsLoaded || !ReferenceEquals(state.Pane.CurrentFileList, state.List)
            || !ReferenceEquals(Model.Workspace.ActivePane.SelectedTab, state.Tab) || state.Tab.CurrentPath != state.Path)
        { CancelFileDragFocus(); return; }
        if (state.Tab.IsLoading || Model.IsFileOperationRunning) return;
        var focus = Keyboard.FocusedElement;
        if (focus is not null && !ReferenceEquals(focus, this) && !ReferenceEquals(focus, state.PreviousFocus) && !state.List.IsKeyboardFocusWithin)
        { CancelFileDragFocus(); return; }
        CancelFileDragFocus();
        var list = state.List;
        var items = list.Items.OfType<FileEntry>().ToArray();
        var saved = state.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var survivors = items.Where(e => saved.Contains(e.FullPath)).ToArray();
        var target = survivors.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.FullPath, state.PrimaryPath))
            ?? survivors.FirstOrDefault() ?? (items.Length == 0 ? null : items[Math.Clamp(state.Index, 0, items.Length - 1)]);
        list.SelectedItems.Clear();
        list.SetCurrentValue(Selector.SelectedItemProperty, target);
        foreach (var entry in survivors) if (!list.SelectedItems.Contains(entry)) list.SelectedItems.Add(entry);
        if (target is not null)
        {
            list.ScrollIntoView(target); list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem row) { Keyboard.Focus(row); return; }
        }
        Keyboard.Focus(list);
    }

    private void CancelFileDragFocus()
    {
        if (dragFocus is { } state) state.Tab.PropertyChanged -= DragFocusTabChanged;
        dragFocus = null;
    }
    private void DragFocusTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabState.IsLoading) or nameof(TabState.CurrentPath)) QueueFileDragFocus();
    }
    private void FileDragRecovery_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (HasAncestor<PaneView>(source) || HasAncestor<ButtonBase>(source) || HasAncestor<TextBoxBase>(source)
            || HasAncestor<PasswordBox>(source) || HasAncestor<ListBox>(source) || HasAncestor<Thumb>(source)) CancelFileDragFocus();
    }
}
