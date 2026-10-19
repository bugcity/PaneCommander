using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using PaneCommander.App.Views;

namespace PaneCommander.App;

public partial class MainWindow
{
    private bool clipboardBusy;
    // Kept injectable so keyboard integration tests never replace the user's clipboard.
    internal Func<FileClipboardOperation, string, string[], IntPtr, Task>? ClipboardOperation { get; set; }

    private void UpdateFunctionModifiers(ModifierKeys modifiers) => Model.SetFunctionModifiers(modifiers);
    private void FunctionKeysActivated(object? sender, EventArgs e) => UpdateFunctionModifiers(Keyboard.Modifiers);
    private void FunctionKeysDeactivated(object? sender, EventArgs e) => UpdateFunctionModifiers(ModifierKeys.None);

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key is Key.LeftShift or Key.RightShift && !Keyboard.IsKeyDown(e.Key == Key.LeftShift ? Key.RightShift : Key.LeftShift)) modifiers &= ~ModifierKeys.Shift;
        UpdateFunctionModifiers(modifiers);
    }

    internal bool HandleFileClipboardKey(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (ShellMenuBehavior.IsOpen || modifiers != ModifierKeys.Control
            || HasAncestor<TextBoxBase>(source) || HasAncestor<PasswordBox>(source)
            || !HasAncestor<FolderView>(source) || FileDragBehavior.Ancestor<ListView>(source) is not { } list)
            return false;
        if (!ReferenceEquals(list, (Model.Workspace.Left.IsActive ? LeftPane : RightPane).CurrentFileList)) return false;
        if (key == Key.A) { list.SelectAll(); return true; }
        if (key is not (Key.C or Key.X or Key.V)) return false;
        if (!clipboardBusy && !Model.IsFileOperationRunning && !Model.Workspace.ActivePane.SelectedTab.IsLoading)
            _ = RunClipboardAsync(key);
        return true;
    }

    private async Task RunClipboardAsync(Key key)
    {
        clipboardBusy = true;
        var tab = Model.Workspace.ActivePane.SelectedTab;
        var paths = Model.GetSelectedPaths?.Invoke() ?? [];
        try
        {
            if (key != Key.V && paths.Length == 0) return;
            var operation = key == Key.C ? FileClipboardOperation.Copy : key == Key.X ? FileClipboardOperation.Cut : FileClipboardOperation.Paste;
            var owner = new WindowInteropHelper(this).Handle;
            if (ClipboardOperation is { } run) await run(operation, tab.CurrentPath, paths, owner);
            else await FileClipboard.ExecuteAsync(operation, tab.CurrentPath, paths, owner, Model.ExecuteFileOperationAsync);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { MessageBox.Show(this, ex.Message, "ファイルのコピー・貼り付け", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { clipboardBusy = false; }
    }
}
