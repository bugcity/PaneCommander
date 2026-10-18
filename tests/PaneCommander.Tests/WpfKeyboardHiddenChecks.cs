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
    private static void VerifyKeyboardAndHiddenUi()
    {
        var files = new HiddenUiFiles();
        var operations = new ReviewUiOperations();
        using var model = new MainViewModel(files, new FakeLogger(), fileOperations: operations);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var left = (PaneView)window.FindName("LeftPane"); var right = (PaneView)window.FindName("RightPane");
            var tab = model.Workspace.Left.SelectedTab; var list = left.CurrentFileList!;
            var options = (System.Windows.Controls.Primitives.Popup)left.FindName("DisplayOptionsPopup");
            Assert.IsFalse(options.IsOpen);
            ((Button)left.FindName("DisplayOptionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.IsTrue(options.IsOpen);
            var toggle = (CheckBox)left.FindName("ShowHiddenFiles");
            var dotToggle = (CheckBox)left.FindName("ShowDotFiles");
            Assert.IsFalse(toggle.IsChecked); Assert.AreEqual(1, list.Items.Count);
            Assert.IsFalse(dotToggle.IsChecked);
            var initialReads = files.Reads;
            dotToggle.IsChecked = false; Pump(); Assert.AreEqual(1, list.Items.Count);
            toggle.IsChecked = true; Pump(); Assert.AreEqual(3, list.Items.Count);
            dotToggle.IsChecked = true; Pump(); Assert.AreEqual(4, list.Items.Count);
            toggle.IsChecked = false; Pump(); Assert.AreEqual(2, list.Items.Count);
            Assert.AreEqual(initialReads, files.Reads);
            var visible = list.Items.Cast<FileEntry>().First(); list.SelectedItem = visible;
            toggle.IsChecked = true; Pump(); Assert.AreEqual(4, list.Items.Count);
            Assert.AreSame(visible, list.SelectedItem); Assert.AreEqual(1, right.CurrentFileList!.Items.Count);
            var reads = files.Reads; tab.FilterText = "hidden"; Pump(); Assert.AreEqual(2, list.Items.Count);
            list.SelectAll(); toggle.IsChecked = false; Pump(); Assert.AreEqual(0, list.Items.Count);
            Assert.AreEqual(0, list.SelectedItems.Count); Assert.IsNull(tab.SelectedEntry);
            Assert.AreEqual(0, model.GetSelectedPaths!().Length, "非表示にした隠し項目をファイル操作に渡さない");
            tab.ClearFilter(); Pump(); Assert.AreEqual(2, list.Items.Count); Assert.AreEqual(reads, files.Reads);
            model.Left.AddTab.Execute(null); Pump(); Assert.IsFalse(model.Workspace.Left.SelectedTab.ShowHiddenFiles);
            model.Workspace.Left.SelectedTab = tab; Pump(); Assert.IsFalse(toggle.IsChecked);
            model.Left.ToggleHiddenFiles.Execute(null); Pump(); Assert.IsTrue(toggle.IsChecked); Assert.AreEqual(4, left.CurrentFileList!.Items.Count);
            model.Left.ToggleHiddenFiles.Execute(null); Pump(); Assert.IsFalse(toggle.IsChecked);
            RaiseKey(dotToggle, PresentationSource.FromVisual(dotToggle)!, Key.Escape); Pump();
            Assert.IsFalse(options.IsOpen);
            list = left.CurrentFileList!; list.SelectedIndex = 0; left.FocusList(); Pump();
            var focus = Keyboard.FocusedElement;
            foreach (var button in Descendants<Button>(window).Where(b => b.DataContext is FunctionAction))
            { Assert.IsFalse(button.Focusable); Assert.IsFalse(button.IsTabStop); Assert.IsFalse(button.Focus()); }
            Assert.AreSame(focus, Keyboard.FocusedElement, "下部ボタンへフォーカスを奪われない");
            var source = PresentationSource.FromVisual(window)!;
            foreach (var useKeyboard in new[] { true, false })
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                    dialog.Close(); // キャンセル後のフォーカス復帰を検証。実ファイル操作なし。
                });
                if (useKeyboard) RaiseKey(list, source, Key.F7);
                else Descendants<Button>(window).Single(b => Equals(b.Content, "F7 作成")).Command.Execute(null);
                Pump();
                Assert.IsTrue(list.IsKeyboardFocusWithin, "Fキー/下部ボタンの操作・キャンセル後も一覧で続けられる");
                var focusedRow = (UIElement)Keyboard.FocusedElement;
                RaiseKey(focusedRow, source, Key.Insert); Pump();
                Assert.AreEqual(0, list.SelectedItems.Count);
                RaiseKey(focusedRow, source, Key.Insert); Pump(); Assert.AreEqual(1, list.SelectedItems.Count);
            }
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var dialog = window.OwnedWindows.OfType<FileOperationDialog>().Single();
                var editor = Descendants<TextBox>(dialog).Single();
                var ok = Descendants<Button>(dialog).Single(b => b.IsDefault);
                editor.Text = @"..\outside"; ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(dialog.IsVisible, "不正な階層は作成を開始せず入力画面に留まる");
                editor.Text = @"親\子\孫"; ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RaiseKey(list, source, Key.F7); Pump();
            Assert.AreEqual(@"親\子\孫", operations.Last!.NewName);
            Assert.AreEqual(FileOperationKind.CreateDirectory, operations.Last.Kind);
            Assert.IsTrue(left.CurrentFileList!.IsKeyboardFocusWithin);
            RaiseKey(left.CurrentFileList!, source, Key.Tab); Pump();
            Assert.AreEqual(PaneSide.Right, model.Workspace.ActiveSide);
            Assert.IsTrue(right.CurrentFileList!.IsKeyboardFocusWithin);
            Capture(window, "keyboard-hidden-toolbar.png");
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class HiddenUiFiles : IFileSystemService
    {
        public int Reads;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new FolderSnapshot(path, new[]
            {
                new FileEntry(Path.Combine(path,"visible.txt"),"visible.txt",false,1,null),
                new FileEntry(Path.Combine(path,".visible"),".visible",false,1,null),
                new FileEntry(Path.Combine(path,"hidden.txt"),"hidden.txt",false,1,null,IsHidden:true),
                new FileEntry(Path.Combine(path,"hidden-folder"),"hidden-folder",true,null,null,IsHidden:true)
            }));
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
