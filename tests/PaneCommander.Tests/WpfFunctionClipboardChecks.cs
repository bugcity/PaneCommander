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
    private static void VerifyFunctionAndClipboardKeys()
    {
        using var model = new MainViewModel(new ReviewUiFiles(), new FakeLogger(), fileOperations: new ReviewUiOperations());
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var left = (PaneView)window.FindName("LeftPane"); var list = left.CurrentFileList!;
            var buttons = Descendants<Button>(window).Where(b => b.DataContext is FunctionAction).ToArray();
            list.SelectedItems.Clear(); Pump();
            foreach (var n in new[] { 3, 4, 5, 6, 8 }) Assert.IsFalse(buttons[n - 1].IsEnabled, "未選択では対象操作を無効化");
            Assert.IsTrue(buttons[6].IsEnabled);
            list.SelectedIndex = 0; Pump(); Assert.IsFalse(buttons[2].IsEnabled, "フォルダーはプレビュー不可");
            list.SelectedIndex = 1; Pump(); Assert.IsTrue(buttons[2].IsEnabled); Assert.IsTrue(buttons[4].IsEnabled);
            list.SelectedItems.Add(list.Items[2]); Pump(); Assert.IsFalse(buttons[2].IsEnabled);
            model.SetFunctionModifiers(ModifierKeys.Shift); Pump();
            Assert.AreEqual("⇧F5 別名コピー", buttons[4].Content);
            Assert.IsTrue(buttons[4].IsEnabled); Assert.IsFalse(buttons[5].IsEnabled, "複数項目は名前変更不可");
            Assert.IsTrue(buttons[6].IsEnabled); Assert.IsTrue(buttons[9].IsEnabled);
            foreach (var n in new[] { 1, 2, 3, 4, 8, 9 }) Assert.IsFalse(buttons[n - 1].IsEnabled);
            list.SelectedItems.Remove(list.Items[2]); Pump(); Assert.IsTrue(buttons[5].IsEnabled);
            FileOperationKind? sameFolder = null; var creations = 0;
            model.RequestSameFolderOperation = kind => { sameFolder = kind; return Task.CompletedTask; };
            model.RequestCreateFile = () => { creations++; return Task.CompletedTask; };
            buttons[4].Command.Execute(null); Assert.AreEqual(FileOperationKind.Copy, sameFolder);
            buttons[5].Command.Execute(null); Assert.AreEqual(FileOperationKind.Move, sameFolder);
            buttons[6].Command.Execute(null); Assert.AreEqual(1, creations);
            Capture(window, "function-shift.png");
            typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
            Pump(); Assert.AreEqual("F5 コピー", buttons[4].Content);
            model.SetFunctionModifiers(ModifierKeys.Control); Assert.IsTrue(buttons.All(b => !b.IsEnabled));
            model.SetFunctionModifiers(ModifierKeys.None); Pump();
            Capture(window, "function-normal.png");

            var calls = new List<(FileClipboardOperation Kind, string Directory, string[] Paths)>();
            window.ClipboardOperation = (kind, directory, paths, _) => { calls.Add((kind, directory, paths)); return Task.CompletedTask; };
            var selection = list.SelectedItems.Cast<FileEntry>().Select(f => f.FullPath).ToArray();
            foreach (var key in new[] { Key.C, Key.X, Key.V }) Assert.IsTrue(window.HandleFileClipboardKey(key, ModifierKeys.Control, list));
            CollectionAssert.AreEqual(new[] { FileClipboardOperation.Copy, FileClipboardOperation.Cut, FileClipboardOperation.Paste }, calls.Select(c => c.Kind).ToArray());
            foreach (var call in calls) { Assert.AreEqual(model.Workspace.Left.SelectedTab.CurrentPath, call.Directory); CollectionAssert.AreEqual(selection, call.Paths); }
            Assert.IsFalse(window.HandleFileClipboardKey(Key.V, ModifierKeys.Control, new TextBox()), "文字編集を横取りしない");
            Assert.IsFalse(window.HandleFileClipboardKey(Key.C, ModifierKeys.Control, (Button)left.FindName("DisplayOptionsButton")));
            Assert.IsFalse(window.HandleFileClipboardKey(Key.C, ModifierKeys.Control | ModifierKeys.Shift, list));
            list.SelectedItems.Clear(); window.HandleFileClipboardKey(Key.C, ModifierKeys.Control, list); Assert.AreEqual(3, calls.Count);
            Assert.IsTrue(window.HandleFileClipboardKey(Key.A, ModifierKeys.Control, list)); Assert.AreEqual(list.Items.Count, list.SelectedItems.Count);
            Assert.IsFalse(buttons[2].IsEnabled); Assert.IsTrue(buttons[4].IsEnabled);
            foreach (var button in buttons) { Assert.IsFalse(button.Focusable); Assert.IsFalse(button.IsTabStop); }
        }
        finally { window.Close(); Pump(); }
    }
}
