using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyOperationNameDefaults()
    {
        var operations = new ReviewUiOperations();
        using var model = new MainViewModel(new OperationNameFiles(), new FakeLogger(), fileOperations: operations);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var pane = (PaneView)window.FindName("LeftPane"); var tab = model.Workspace.Left.SelectedTab;
            tab.ShowDotFiles = true; Pump();
            foreach (var (fileName, selectedName, function, sameFolder) in new[]
            {
                ("report.v1.txt", "report.v1", 5, false),
                ("folder.with.dot", "folder.with.dot", 6, false),
                (".gitignore", ".gitignore", 6, false),
                ("README", "README", 5, false),
                ("report.v1.txt", "report.v1", 5, true),
                ("folder.with.dot", "folder.with.dot", 6, true)
            })
            {
                var list = pane.CurrentFileList!;
                list.SelectedItems.Clear(); list.SelectedItem = list.Items.Cast<FileEntry>().Single(entry => entry.Name == fileName);
                pane.FocusList(); Pump();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                    Assert.IsFalse(Descendants<CheckBox>(dialog).Any(), "実行の前にチェック操作を要求しない");
                    Assert.IsFalse(dialog.Overwrite, "実行ボタンを押す前は未確定");
                    Assert.AreEqual(function == 5 ? "コピー（同名は上書き）" : "移動（同名は上書き）", Descendants<Button>(dialog).Single(button => button.IsDefault).Content);
                    var editors = Descendants<TextBox>(dialog).ToArray();
                    var name = editors.Single(editor => editor.Text == fileName);
                    Assert.IsTrue(name.IsKeyboardFocused, "元の名前を入力欄へ表示してフォーカスする");
                    Assert.AreEqual(selectedName, name.SelectedText);
                    Assert.AreEqual(0, name.SelectionStart);
                    var destination = editors.Single(editor => editor != name);
                    Assert.IsTrue(destination.IsReadOnly, "競合時は名前だけ入力する");
                    if (!sameFolder) Assert.AreEqual(model.Workspace.Right.SelectedTab.CurrentPath, destination.Text);
                    name.SelectedText = "更新";
                    if (fileName == "report.v1.txt") Assert.AreEqual("更新.txt", name.Text, "打ち替えても拡張子を維持");
                    Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (sameFolder)
                {
                    var task = (Task)typeof(MainWindow).GetMethod("SameFolderOperationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, [function == 5 ? FileOperationKind.Copy : FileOperationKind.Move])!;
                    CompleteWithDispatcher(task);
                }
                else model.Functions[function - 1].Command.Execute(null);
                Pump();
                Assert.AreEqual(fileName == "report.v1.txt" ? "更新.txt" : "更新", operations.Last!.NewName);
                Assert.AreEqual(function == 5 ? FileOperationKind.Copy : FileOperationKind.Move, operations.Last.Kind);
                Assert.IsTrue(operations.Last.Overwrite, "実行ボタンだけで上書きまで確定する");
                Assert.IsTrue(pane.CurrentFileList!.IsKeyboardFocusWithin);
            }
            var multiple = pane.CurrentFileList!; multiple.SelectedItems.Clear();
            multiple.SelectedItems.Add(multiple.Items[0]); multiple.SelectedItems.Add(multiple.Items[1]);
            for (var index = 0; index < 2; index++) Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                Assert.AreEqual(2, Descendants<TextBox>(dialog).Count(), "競合した項目ごとに元の名前を入力する");
                Assert.IsNotNull(dialog.NewName);
                var name = Descendants<TextBox>(dialog).Single(editor => !editor.IsReadOnly);
                name.Text += "-copy";
                Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            model.Functions[4].Command.Execute(null); Pump();
            Assert.IsTrue(operations.Last!.Overwrite);
            Assert.AreEqual(1, operations.Last.Sources.Length);
            foreach (var kind in new[] { FileOperationKind.CreateFile, FileOperationKind.CreateDirectory })
            {
                var dialog = new FileOperationDialog(kind, 0, "new-item") { Owner = window };
                dialog.Show(); Pump();
                Assert.AreEqual("OK", Descendants<Button>(dialog).Single(button => button.IsDefault).Content);
                Assert.IsFalse(dialog.Overwrite, "新規作成に上書きを適用しない");
                dialog.Close();
            }
        }
        finally { window.Close(); Pump(); }
        VerifyCopyMoveCollisionFlow();
    }
    private sealed class OperationNameFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            new[] { "report.v1.txt", "folder.with.dot", ".gitignore", "README" }
                .Select(name => new FileEntry(Path.Combine(path, name), name, name == "folder.with.dot", 1, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
