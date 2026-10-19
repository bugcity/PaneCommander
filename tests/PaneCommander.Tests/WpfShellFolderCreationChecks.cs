using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyShellFolderCreation()
    {
        var files = new ShellFolderFiles();
        var operations = new ShellFolderOperations(files);
        using var model = new MainViewModel(files, new FakeLogger(), fileOperations: operations);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            model.Workspace.Activate(PaneSide.Right);
            var pane = (PaneView)window.FindName("RightPane");
            pane.FocusList(); Pump();
            var directory = model.Workspace.Right.SelectedTab.CurrentPath;
            var expectedName = "名前を入力したフォルダー";
            foreach (var cancel in new[] { true, false })
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                    var input = Descendants<TextBox>(dialog).Single();
                    Assert.IsTrue(input.IsKeyboardFocused, "Shell経由でも名前入力欄に実フォーカスを置く");
                    Assert.AreEqual(input.Text, input.SelectedText, "初期名をそのまま打ち替えられる");
                    if (cancel) { dialog.Close(); return; }
                    var ok = Descendants<Button>(dialog).Single(b => b.IsDefault);
                    input.Text = @"..\outside"; ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(dialog.IsVisible, "無効な名前では作成せず入力を継続");
                    Assert.AreEqual(0, operations.Count);
                    input.Text = expectedName;
                    Capture(dialog, "shell-new-folder-name-entry.png");
                    ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                CompleteWithDispatcher(window.CreateDirectoryFromShellAsync(directory)); Pump();
                Assert.AreEqual(cancel ? 0 : 1, operations.Count, "キャンセル時は作成せず、確定時に一度だけ作成");
                Assert.IsTrue(pane.CurrentFileList!.IsKeyboardFocusWithin);
            }
            Assert.AreEqual(FileOperationKind.CreateDirectory, operations.Last!.Kind);
            Assert.AreEqual(directory, operations.Last.Destination);
            Assert.AreEqual(expectedName, operations.Last.NewName);
            Assert.AreEqual(0, operations.Last.Sources.Length);
            Assert.AreEqual(expectedName, ((FileEntry)pane.CurrentFileList!.SelectedItem).Name);
            Assert.IsTrue(Keyboard.FocusedElement is ListViewItem { IsSelected: true });
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class ShellFolderFiles : IFileSystemService
    {
        public string? CreatedPath;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            CreatedPath is { } created && Path.GetDirectoryName(created) == path
                ? [new(created, Path.GetFileName(created), true, null, null)] : []));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ShellFolderOperations(ShellFolderFiles files) : IFileOperationService
    {
        public int Count;
        public FileOperationRequest? Last;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default)
        {
            Count++; Last = request; files.CreatedPath = Path.Combine(request.Destination!, request.NewName!);
            return Task.FromResult(new FileOperationResult(false));
        }
    }
}
