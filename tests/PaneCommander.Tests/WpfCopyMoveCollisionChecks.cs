using System.Reflection;
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
    private static void VerifyCopyMoveCollisionFlow()
    {
        const string source = @"C:\collision-flow\left";
        const string destination = @"C:\collision-flow\right";
        var files = new CollisionFlowFiles(destination);
        var operations = new CollisionFlowOperations();
        using var model = new MainViewModel(files, new FakeLogger(), fileOperations: operations);
        model.Workspace.Left.Restore(new PaneSnapshot { Tabs = [new(source)] });
        model.Workspace.Right.Restore(new PaneSnapshot { Tabs = [new(destination)] });
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            Task Run(FileOperationKind kind, params string[] names)
            {
                model.GetSelectedPaths = () => names.Select(name => System.IO.Path.Combine(source, name)).ToArray();
                return (Task)typeof(MainWindow).GetMethod("RequestFileOperationCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [kind, null])!;
            }
            foreach (var kind in new[] { FileOperationKind.Copy, FileOperationKind.Move })
            {
                var before = operations.Requests.Count;
                CompleteWithDispatcher(Run(kind, "fresh.txt")); Pump();
                Assert.AreEqual(before + 1, operations.Requests.Count, "同名がなければ入力なしで実行する");
                Assert.AreEqual("fresh.txt", operations.Requests[^1].NewName);
                Assert.IsFalse(window.OwnedWindows.OfType<FileOperationDialog>().Any());
                foreach (var chosenName in new string?[] { "collision.txt", "occupied.txt", "renamed.txt", null })
                {
                    before = operations.Requests.Count;
                    var dialogs = 0;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single(); dialogs++;
                        var editors = Descendants<TextBox>(dialog).ToArray();
                        var name = editors.Single(editor => !editor.IsReadOnly);
                        Assert.AreEqual("collision.txt", name.Text);
                        Assert.AreEqual("collision", name.SelectedText);
                        Assert.IsTrue(name.IsKeyboardFocused);
                        Assert.IsFalse(Descendants<CheckBox>(dialog).Any());
                        if (chosenName is null) { dialog.Close(); return; }
                        name.Text = chosenName;
                        if (kind == FileOperationKind.Copy && chosenName == "occupied.txt") Capture(dialog, "copy-collision-dialog.png");
                        Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    CompleteWithDispatcher(Run(kind, "collision.txt")); Pump();
                    Assert.AreEqual(1, dialogs);
                    Assert.AreEqual(before + (chosenName is null ? 0 : 1), operations.Requests.Count);
                    if (chosenName is not null)
                    {
                        Assert.AreEqual(chosenName, operations.Requests[^1].NewName);
                        Assert.IsTrue(operations.Requests[^1].Overwrite, "変更後の名前が存在しても追加確認を要求しない");
                    }
                }
                before = operations.Requests.Count;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                    Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                CompleteWithDispatcher(Run(kind, "fresh.txt", "collision.txt")); Pump();
                Assert.AreEqual(before + 2, operations.Requests.Count, "混在選択では同名項目だけ入力する");
                before = operations.Requests.Count;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.OwnedWindows.OfType<FileOperationDialog>().Single().Close());
                CompleteWithDispatcher(Run(kind, "fresh.txt", "collision.txt")); Pump();
                Assert.AreEqual(before, operations.Requests.Count, "後の入力でキャンセルした場合も先に処理しない");
            }
            Assert.IsTrue(files.TargetReads > 0);
            var pane = (PaneView)window.FindName("LeftPane");
            Assert.IsTrue(pane.CurrentFileList!.IsKeyboardFocusWithin);
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class CollisionFlowFiles(string destination) : IFileSystemService
    {
        public int TargetReads;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token)
        {
            var target = path == destination;
            if (target) TargetReads++;
            var names = target ? new[] { "collision.txt", "occupied.txt" } : new[] { "fresh.txt", "collision.txt" };
            return Task.FromResult(new FolderSnapshot(path, names.Select(name => new FileEntry(System.IO.Path.Combine(path, name), name, false, 1, null, IsHidden: target)).ToArray()));
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class CollisionFlowOperations : IFileOperationService
    {
        public List<FileOperationRequest> Requests { get; } = [];
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default)
        { Requests.Add(request); return Task.FromResult(new FileOperationResult(false)); }
    }
}
