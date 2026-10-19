using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyExternalDragFocusRecovery()
    {
        var files = new ExternalDragFiles();
        using var model = new MainViewModel(files, new FakeLogger());
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        Window? receiver = null;
        try
        {
            window.Show(); Pump();
            var pane = (PaneView)window.FindName("LeftPane"); var list = pane.CurrentFileList!; var tab = model.Workspace.Left.SelectedTab;
            void Select(params int[] indices)
            {
                list.SelectedItems.Clear(); foreach (var index in indices) list.SelectedItems.Add(list.Items[index]);
                pane.FocusList(); Pump();
            }
            void LoseFocus()
            {
                receiver = new Window { Owner = window, Content = new TextBox(), ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 200, Height = 100 };
                receiver.Show(); ((TextBox)receiver.Content).Focus(); Pump();
                FocusManager.SetFocusedElement(window, null);
                Assert.IsFalse(window.IsActive);
                Assert.IsFalse(list.IsKeyboardFocusWithin);
            }
            void ReturnToSource()
            {
                receiver?.Close(); receiver = null;
                window.Activate(); Pump(); // Match activation's queued recovery after native focus restoration.
                window.RecoverFileDragFocus(); Pump();
            }
            Select(1, 2);
            Descendants<Button>(window).Single(b => Equals(b.Content, "更新")).Focus(); Pump();
            var state = window.BeginFileDragFocus(list, tab); Assert.IsNotNull(state);
            Assert.IsTrue(list.IsKeyboardFocusWithin, "複数選択の遅延クリックでもドラッグ元へフォーカスを合わせる");
            LoseFocus(); CompleteWithDispatcher(model.RefreshAfterFileOperationAsync()); Pump();
            window.CompleteFileDragFocus(state); Pump();
            Assert.IsFalse(list.IsKeyboardFocusWithin, "外部アプリでのドロップ直後にフォーカスを奪わない");
            ReturnToSource();
            Assert.IsTrue(list.IsKeyboardFocusWithin, $"再アクティブ化後の一覧フォーカス: active={window.IsActive}, focus={Keyboard.FocusedElement?.GetType().Name}");
            CollectionAssert.AreEquivalent(new[] { "b.txt", "c.txt" }, list.SelectedItems.Cast<FileEntry>().Select(e => e.Name).ToArray());
            Assert.IsTrue(Keyboard.FocusedElement is ListViewItem { IsSelected: true });

            Select(1); state = window.BeginFileDragFocus(list, tab); LoseFocus();
            files.Names.Remove("b.txt"); CompleteWithDispatcher(model.RefreshAfterFileOperationAsync()); Pump();
            window.CompleteFileDragFocus(state); ReturnToSource();
            Assert.AreEqual("c.txt", ((FileEntry)list.SelectedItem).Name, "外部移動で元が消えたら近くの行へ復帰");
            Assert.IsTrue(list.IsKeyboardFocusWithin);

            state = window.BeginFileDragFocus(list, tab); LoseFocus();
            CompleteWithDispatcher(model.RefreshAfterFileOperationAsync()); window.CompleteFileDragFocus(state);
            var settings = Descendants<Button>(window).Single(b => Equals(b.Content, "設定"));
            receiver?.Close(); receiver = null;
            settings.Focus(); Pump(); window.RecoverFileDragFocus(); Pump();
            Assert.IsTrue(settings.IsKeyboardFocused, "明示的なツールバーのフォーカスを奪わない");

            Select(0); state = window.BeginFileDragFocus(list, tab); LoseFocus();
            window.CompleteFileDragFocus(state);
            var next = model.Workspace.Left.AddTab(); Pump();
            ReturnToSource(); Assert.AreSame(next, model.Workspace.Left.SelectedTab);
            model.Workspace.Left.SelectedTab = tab; Pump(); list = pane.CurrentFileList!;
            Select(0); state = window.BeginFileDragFocus(list, tab); LoseFocus();
            files.Names.Clear(); CompleteWithDispatcher(model.RefreshAfterFileOperationAsync());
            window.CompleteFileDragFocus(state); ReturnToSource();
            Assert.AreSame(list, Keyboard.FocusedElement); Assert.AreEqual(0, list.Items.Count);
        }
        finally { receiver?.Close(); window.Close(); Pump(); }
    }

    private sealed class ExternalDragFiles : IFileSystemService
    {
        public readonly List<string> Names = ["a.txt", "b.txt", "c.txt", "d.txt"];
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            Names.Select(name => new FileEntry(Path.Combine(path, name), name, false, 1, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
