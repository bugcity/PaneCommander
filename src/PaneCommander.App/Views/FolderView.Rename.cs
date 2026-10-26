using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public partial class FolderView
{
    private HighlightedFileName? renameCandidate, renameLabel;
    private FileEntry? renameEntry;
    private TextBox? renameEditor;
    private TabState? renameTab;
    private Point renameStart;
    private DispatcherTimer? renameDelay;
    private Window? renameWindow;
    internal TextBox? RenameEditor => renameEditor;

    private void InitializeRename()
    {
        Files.PreviewMouseDown += (_, e) => PrepareRename(e.OriginalSource as DependencyObject, e.GetPosition(Files), e.ChangedButton == MouseButton.Left ? e.ClickCount : 0, Keyboard.Modifiers);
        Files.PreviewMouseLeftButtonUp += (_, e) => ReleaseRename(e.GetPosition(Files));
        Files.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) CancelRenameDrag(e.GetPosition(Files)); };
        Files.PreviewKeyDown += (_, _) => CancelRenameDelay();
        Files.SelectionChanged += async (_, _) =>
        {
            CancelRenameDelay();
            if (renameEntry is not null && !Files.SelectedItems.Contains(renameEntry)) await CommitRenameAsync(false);
        };
        Loaded += (_, _) => { renameWindow = Window.GetWindow(this); if (renameWindow is not null) renameWindow.Deactivated += RenameDeactivated; };
        Unloaded += (_, _) => { CancelRename(false); if (renameWindow is not null) renameWindow.Deactivated -= RenameDeactivated; renameWindow = null; };
    }
    private void RenameDeactivated(object? sender, EventArgs e) => CancelRename(false);
    private bool CanRename => main?.CanRenameFiles == true && tab is { IsLoading: false } && !ShellMenuBehavior.IsOpen;

    internal bool PrepareRename(DependencyObject? source, Point point, int clicks, ModifierKeys modifiers)
    {
        CancelRenameDelay();
        if (!CanRename || clicks != 1 || modifiers != ModifierKeys.None || Files.SelectedItems.Count != 1
            || FileDragBehavior.Ancestor<HighlightedFileName>(source) is not { } label
            || ItemsControl.ContainerFromElement(Files, label) is not ListViewItem { IsSelected: true }) return false;
        renameCandidate = label; renameStart = point; return true;
    }
    internal void CancelRenameDrag(Point point)
    {
        if (Math.Abs(point.X - renameStart.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(point.Y - renameStart.Y) >= SystemParameters.MinimumVerticalDragDistance) CancelRenameDelay();
    }
    internal void ReleaseRename(Point point)
    {
        CancelRenameDrag(point);
        if (renameCandidate is null) return;
        renameDelay = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        renameDelay.Tick += RenameDelayTick; renameDelay.Start();
    }
    private void RenameDelayTick(object? sender, EventArgs e) => BeginPendingRename();
    private void CancelRenameDelay()
    {
        if (renameDelay is not null) { renameDelay.Stop(); renameDelay.Tick -= RenameDelayTick; renameDelay = null; }
        renameCandidate = null;
    }
    internal bool BeginPendingRename()
    {
        var label = renameCandidate; CancelRenameDelay();
        if (!CanRename || label is not { IsLoaded: true, IsVisible: true, DataContext: FileEntry entry }
            || Window.GetWindow(this)?.IsActive != true || !Files.IsKeyboardFocusWithin
            || Files.SelectedItems.Count != 1 || !Files.SelectedItems.Contains(entry)
            || label.Parent is not Grid cell || cell.Children.OfType<TextBox>().SingleOrDefault() is not { } editor) return false;
        CancelRename(false);
        renameEntry = entry; renameTab = tab; renameLabel = label; renameEditor = editor;
        editor.Text = entry.Name; editor.ToolTip = null;
        editor.Width = Math.Max(40, Math.Min(Math.Max(label.ActualWidth + 24, 120), columns["Name"].ActualWidth - 30));
        label.Visibility = Visibility.Collapsed; editor.Visibility = Visibility.Visible;
        editor.PreviewKeyDown += RenameKeyDown; editor.LostKeyboardFocus += RenameLostFocus;
        editor.Focus();
        var length = entry.Name.Length - (entry.IsDirectory ? 0 : Path.GetExtension(entry.Name).Length);
        editor.Select(0, length > 0 ? length : entry.Name.Length); return true;
    }
    private async void RenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelRename(true); }
        else if (e.Key == Key.Enter) { e.Handled = true; await CommitRenameAsync(true); }
    }
    private async void RenameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => await CommitRenameAsync(false);
    private void CancelRename(bool focus)
    {
        CancelRenameDelay(); var target = renameTab;
        if (renameEditor is { } editor)
        {
            editor.PreviewKeyDown -= RenameKeyDown; editor.LostKeyboardFocus -= RenameLostFocus;
            editor.Visibility = Visibility.Collapsed; editor.ToolTip = null;
        }
        if (renameLabel is { } label) label.Visibility = Visibility.Visible;
        renameEditor = null; renameLabel = null; renameEntry = null; renameTab = null;
        if (focus && target is not null) PaneModel?.RequestListFocus?.Invoke(target);
    }
    internal async Task CommitRenameAsync(bool focus)
    {
        if (renameEditor is not { } editor || renameEntry is not { } entry || renameTab is not { } target) return;
        if (!CanRename || !ReferenceEquals(target, tab)) { CancelRename(false); return; }
        var name = editor.Text;
        if (name == entry.Name) { CancelRename(focus); return; }
        FileOperationRequest request;
        try { FileCreation.ValidateName(name); request = SameFolderOperation.Create(FileOperationKind.Move, entry.FullPath, name); }
        catch (ArgumentException ex)
        {
            editor.ToolTip = ex.Message; target.BeginOperationStatus(ex.Message)(ex.Message, OperationStatusKind.Attention);
            if (focus) editor.Focus(); return;
        }
        var model = main!; var pane = PaneModel; var directory = target.CurrentPath;
        CancelRename(false);
        try
        {
            var result = await model.ExecuteFileOperationAsync(request, new WindowInteropHelper(Window.GetWindow(this)).Handle, target);
            if (!result.Aborted && target.CurrentPath == directory) target.RequestSelection([Path.Combine(directory, name)]);
        }
        catch (Exception ex)
        { target.BeginOperationStatus("名前を変更できません: " + ex.Message)("名前を変更できません: " + ex.Message, OperationStatusKind.Attention); }
        finally { if (focus && ReferenceEquals(tab, target)) pane?.RequestListFocus?.Invoke(target); }
    }
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
}
