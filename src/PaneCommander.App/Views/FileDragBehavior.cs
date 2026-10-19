using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.App.Views;

public sealed record FavoriteTabPayload(string Name, string Path);
public static class FileDragBehavior
{
    public const string FavoriteFormat = "PaneCommander.TabFavorite";
    public const string InternalFilesFormat = "PaneCommander.LinkSafeFiles";
    public static string[]? ReadPaths(IDataObject data) => data.GetDataPresent(InternalFilesFormat) ? data.GetData(InternalFilesFormat) as string[] : data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
    public static string[] CaptureSources(ListView list, FileEntry? anchor) => anchor is null ? [] : list.SelectedItems.Contains(anchor)
        ? list.SelectedItems.OfType<FileEntry>().Select(f => f.FullPath).ToArray() : [anchor.FullPath];
    public static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    { while (node is not null) { if (node is T result) return result; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); } return null; }
    public static void Attach(ListView list, Func<TabState> tab, MainViewModel main)
    {
        Point? start = null; ListViewItem? deferred = null; ModifierKeys clickModifiers = ModifierKeys.None;
        FileEntry? anchor = null; string[] dragSources = [];
        IDataObject? cachedData = null; string? cachedDestination = null; DragDropKeyStates cachedKeys = 0; DragDropEffects cachedAllowed = 0, cachedEffect = DragDropEffects.None;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            deferred = null; var row = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListViewItem; start = row is null ? null : e.GetPosition(list); clickModifiers = Keyboard.Modifiers;
            anchor = row?.Content as FileEntry; dragSources = CaptureSources(list, anchor);
            if (row is { IsSelected: true } && (list.SelectedItems.Count > 1 || clickModifiers.HasFlag(ModifierKeys.Control)) && !clickModifiers.HasFlag(ModifierKeys.Shift)) { deferred = row; e.Handled = true; }
        };
        // Capture the completed Ctrl/Shift selection after the row's mouse-down handling.
        list.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && start is not null) dragSources = CaptureSources(list, anchor);
        }), true);
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (start is not null && deferred?.DataContext is FileEntry entry) { if (clickModifiers.HasFlag(ModifierKeys.Control)) list.SelectedItems.Remove(entry); else { list.SelectedItems.Clear(); list.SelectedItems.Add(entry); } deferred.Focus(); e.Handled = true; }
            start = null; deferred = null; anchor = null; dragSources = [];
        };
        list.PreviewMouseMove += async (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed || main.IsFileOperationRunning || ShellMenuBehavior.IsOpen) return;
            var now = e.GetPosition(list); if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null; deferred = null; var paths = dragSources; dragSources = []; if (paths.Length == 0) return;
            var owner = Window.GetWindow(list) as MainWindow;
            MainWindow.DragFocusState? focusRecovery = null;
            try
            {
                var internalOnly = await Task.Run(() => ShellFileOperationService.RequiresInternalDrag(paths));
                if (Mouse.LeftButton != MouseButtonState.Pressed) { DropDiagnostics.Write("gesture-ended-before-preflight", sources: paths); return; }
                var data = new DataObject(); data.SetData(InternalFilesFormat, paths);
                // Explorer's CF_HDROP handler may dereference junctions. Do not export link trees to it.
                if (!internalOnly) { var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files); }
                DropDiagnostics.Write("drag-start", sources: paths);
                focusRecovery = owner?.BeginFileDragFocus(list, tab());
                var returned = DragDrop.DoDragDrop(list, data, DragDropEffects.Copy | DragDropEffects.Move);
                DropDiagnostics.Write("drag-return", sources: paths, result: returned.ToString());
                // DoDragDrop's return alone is not a reliable permission to delete source data.
                if (DropCompletion.RequiresSourceDeletion((int)returned, ReadEffect(data, "Performed DropEffect"), ReadEffect(data, "Logical Performed DropEffect")))
                {
                    var remaining = paths.Where(ExistsWithoutFollowing).ToArray();
                    if (remaining.Length > 0) await main.ExecuteFileOperationAsync(new(FileOperationKind.Delete, remaining, Recycle: false), new WindowInteropHelper(Window.GetWindow(list)).Handle);
                }
            }
            catch (Exception ex) { ShowError(ex); }
            finally
            {
                try { await main.RefreshAfterFileOperationAsync(); }
                finally { owner?.CompleteFileDragFocus(focusRecovery); }
            }
        };
        list.AllowDrop = true;
        list.PreviewDragEnter += (_, e) => { e.Effects = Effect(e); e.Handled = true; };
        list.PreviewDragOver += (_, e) => { e.Effects = Effect(e); e.Handled = true; };
        list.DragLeave += (_, _) => { cachedData = null; cachedDestination = null; };
        list.PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = DragDropEffects.None; cachedData = null;
            try
            {
                // Resolve once at Drop, independently of the DragOver cache and the hit row.
                var destination = Destination();
                var request = Request(e, destination);
                if (request is null) { DropDiagnostics.Write("drop-rejected", destination, ReadPaths(e.Data)); return; }
                DropDiagnostics.Write("drop-accepted", request.Destination, request.Sources, request.Kind.ToString());
                var intended = request.Kind == FileOperationKind.Copy ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Data.SetData("Performed DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
                e.Data.SetData("Logical Performed DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
                var task = main.ExecuteFileOperationAsync(request, new WindowInteropHelper(Window.GetWindow(list)).Handle);
                // OLE Drop must not return a successful move before its asynchronous I/O completes.
                // A nested Dispatcher frame keeps the UI responsive without a polling timer.
                if (!task.IsCompleted) { var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => list.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); }
                var result = task.GetAwaiter().GetResult();
                DropDiagnostics.Write("drop-completed", request.Destination, request.Sources, result.Aborted ? "Aborted" : "Completed");
                e.Data.SetData("Performed DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(intended == DragDropEffects.Move ? 0 : 1)));
                e.Data.SetData("Logical Performed DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(result.Aborted ? 0 : (int)intended)));
                e.Effects = result.Aborted ? DragDropEffects.None : intended;
            }
            catch (Exception ex) { DropDiagnostics.Write("drop-failed", result: ex.GetType().Name + ": " + ex.Message); ShowError(ex); }
        };
        DragDropEffects Effect(DragEventArgs e)
        {
            try
            {
                if (main.IsFileOperationRunning || ShellMenuBehavior.IsOpen) { cachedData = null; return DragDropEffects.None; }
                var destination = Destination();
                if (ReferenceEquals(cachedData, e.Data) && cachedDestination == destination && cachedKeys == e.KeyStates && cachedAllowed == e.AllowedEffects && !main.IsFileOperationRunning) return cachedEffect;
                cachedData = e.Data; cachedDestination = destination; cachedKeys = e.KeyStates; cachedAllowed = e.AllowedEffects; cachedEffect = DragDropEffects.None;
                var request = Request(e, destination);
                return cachedEffect = request is null ? DragDropEffects.None : request.Kind == FileOperationKind.Copy ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException) { return DragDropEffects.None; }
        }
        string Destination() => Ancestor<PaneView>(list)?.DataContext is PaneViewModel pane ? pane.State.SelectedTab.CurrentPath : tab().CurrentPath;
        FileOperationRequest? Request(DragEventArgs e, string destination)
        {
            if (main.IsFileOperationRunning || ShellMenuBehavior.IsOpen || e.Data.GetDataPresent(FavoriteFormat) || ReadPaths(e.Data) is not string[] paths || paths.Length > 32768) return null;
            var request = DropPolicy.CreateRequest(paths, destination, e.KeyStates.HasFlag(DragDropKeyStates.ControlKey), e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey), e.KeyStates.HasFlag(DragDropKeyStates.AltKey));
            if (request is null || !e.AllowedEffects.HasFlag(request.Kind == FileOperationKind.Copy ? DragDropEffects.Copy : DragDropEffects.Move)) return null;
            return request;
        }
        void ShowError(Exception ex) { if (Window.GetWindow(list) is { } owner && owner.IsVisible) MessageBox.Show(owner, ex.Message, "ファイル操作", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public static int? ReadEffect(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format, false)) return null; var value = data.GetData(format, false);
        if (value is int number) return number; if (value is byte[] bytes && bytes.Length >= 4) return BitConverter.ToInt32(bytes, 0);
        if (value is System.IO.Stream stream && stream.CanRead && stream.CanSeek) { var position = stream.Position; try { stream.Position = 0; var buffer = new byte[4]; return stream.Read(buffer, 0, 4) == 4 ? BitConverter.ToInt32(buffer, 0) : null; } finally { stream.Position = position; } }
        return null;
    }
    private static bool ExistsWithoutFollowing(string path) { try { _ = System.IO.File.GetAttributes(path); return true; } catch (System.IO.FileNotFoundException) { return false; } catch (System.IO.DirectoryNotFoundException) { return false; } }
}
