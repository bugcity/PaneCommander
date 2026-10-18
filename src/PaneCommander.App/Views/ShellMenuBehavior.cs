using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.App.Views;

public static class ShellMenuBehavior
{
    public static bool IsOpen { get; private set; }
    public static bool Selecting { get; private set; }
    public static bool IsContextMenuKey(Key key, ModifierKeys modifiers) => key == Key.Apps && modifiers == ModifierKeys.None || key == Key.F10 && modifiers == ModifierKeys.Shift;
    public static void SelectTarget(ListView list, FileEntry? entry)
    {
        if (entry is null || list.SelectedItems.Contains(entry)) return;
        Selecting = true;
        try { list.SelectedItems.Clear(); list.SelectedItem = entry; }
        finally { Selecting = false; }
    }
    public static void Attach(ListView list, Func<string?> directory, MainViewModel main)
    {
        void Activate()
        {
            if (FileDragBehavior.Ancestor<PaneView>(list)?.DataContext is PaneViewModel pane) main.Workspace.Activate(pane.State.Side);
        }
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FileDragBehavior.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null || FileDragBehavior.Ancestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
            Activate(); var row = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListViewItem;
            SelectTarget(list, row?.Content as FileEntry); list.Focus(); e.Handled = true;
        };
        list.PreviewMouseRightButtonUp += async (_, e) =>
        {
            if (FileDragBehavior.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null || FileDragBehavior.Ancestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
            e.Handled = true; Activate(); var row = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListViewItem;
            var point = list.PointToScreen(e.GetPosition(list)); await ShowAsync(list, directory(), row is null ? [] : list.SelectedItems.OfType<FileEntry>().ToArray(), point, main);
        };
        list.PreviewKeyDown += async (_, e) =>
        {
            if (!IsContextMenuKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) return;
            e.Handled = true; Activate(); var row = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListViewItem;
            var point = (row as FrameworkElement ?? list).PointToScreen(new Point(16, row?.ActualHeight ?? 24));
            await ShowAsync(list, directory(), list.SelectedItems.OfType<FileEntry>().ToArray(), point, main);
        };
    }
    private static async Task ShowAsync(ListView list, string? directory, FileEntry[] entries, Point point, MainViewModel main)
    {
        if (IsOpen || directory is null || main.IsFileOperationRunning) return;
        var window = Window.GetWindow(list); var owner = new WindowInteropHelper(window).Handle; var source = HwndSource.FromHwnd(owner);
        var paths = entries.Select(e => e.FullPath).ToArray(); var changed = false;
        try
        {
            IsOpen = true;
            using var menu = new ShellContextMenu(directory, paths, owner);
            bool Allowed(string verb) => (!entries.Any(e => e.IsReparsePoint) || ShellMenuSafety.AllowedForDirectLink(verb)) && (verb != "rename" || paths.Length == 1);
            var restricted = entries.Any(e => e.IsReparsePoint) || paths.Length > 1;
            if (restricted) menu.Restrict(Allowed);
            IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
            {
                try { var forwarded = menu.Forward((uint)msg, w, l, out var result); if (msg == 0x117 && restricted) menu.Restrict(Allowed); handled = forwarded; return result; }
                catch (COMException) { return IntPtr.Zero; }
            }
            source.AddHook(Hook); uint command;
            try { command = menu.Track((int)point.X, (int)point.Y); }
            finally { source.RemoveHook(Hook); }
            if (command == 0) return;
            var verb = menu.Verb(command).ToLowerInvariant();
            if (!Allowed(verb)) throw new IOException("このリンクに対するShellコマンドは安全性未検証のため無効です。");
            string[]? clipboard = null;
            uint clipboardSequence = 0;
            if (verb is "paste" or "pastelink") { clipboardSequence = GetClipboardSequenceNumber(); clipboard = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : null; }
            await Task.Run(() => ShellMenuSafety.CheckInvocation(verb, directory, paths, clipboard));
            if (verb is "paste" or "pastelink" && clipboardSequence != GetClipboardSequenceNumber()) throw new IOException("検査中にクリップボードが変更されました。貼り付けをやり直してください。");
            if (verb == "paste")
            {
                using var transfer=new ShellPasteData();
                if(clipboardSequence!=GetClipboardSequenceNumber())throw new IOException("貼り付け開始前にクリップボードが変更されました。やり直してください。");
                var result=await main.ExecuteFileOperationAsync(new(transfer.Kind,clipboard!,directory),owner);
                if(!result.Aborted)
                {
                    try{transfer.ReportCompleted();}
                    catch(ExternalException ex){throw new IOException("ファイル操作は終了しましたが、コピー元へのShell転送完了通知に失敗しました。",ex);}
                }
            }
            else if (verb == "delete") { await main.ExecuteFileOperationAsync(new(FileOperationKind.Delete, paths, Recycle: true), owner); }
            else if (verb == "rename")
            {
                var dialog = new ShellRenameDialog(Path.GetFileName(paths.Single())) { Owner = window };
                if (dialog.ShowDialog() == true) await main.ExecuteFileOperationAsync(new(FileOperationKind.Move, paths, directory, dialog.NewName), owner);
            }
            else
            {
                changed = verb is not ("copy" or "cut" or "properties");
                menu.Invoke(command, (int)point.X, (int)point.Y, waitForCompletion: verb is "paste" or "pastelink");
            }
        }
        catch (Exception ex) when (ex is ExternalException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException or OperationCanceledException)
        { MessageBox.Show(window, ex.Message, "Windows Shellメニュー", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            IsOpen = false;
            if (changed) await main.RefreshAfterFileOperationAsync();
            if (list.IsLoaded && list.IsVisible && FileDragBehavior.Ancestor<PaneView>(list)?.DataContext is PaneViewModel { State.IsActive: true }) list.Focus();
        }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

internal sealed class ShellRenameDialog : Window
{
    private readonly TextBox name;
    public string NewName => name.Text;
    public ShellRenameDialog(string initial)
    {
        Title = "名前の変更"; Width = 450; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(14) }; Content = panel; name = new TextBox { Text = initial }; panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(actions);
        actions.Children.Add(new Button { Content = "キャンセル", IsCancel = true }); var ok = new Button { Content = "OK", IsDefault = true }; actions.Children.Add(ok);
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(NewName)) DialogResult = true; };
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
