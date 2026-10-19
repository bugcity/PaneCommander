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
    private static void VerifyMouseSelection()
    {
        using var model = new MainViewModel(new MouseSelectionFiles(), new FakeLogger());
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var pane = (PaneView)window.FindName("LeftPane"); var list = pane.CurrentFileList!;
            var behavior = FileMarqueeSelection.Get(list);
            Point At(int index) { list.UpdateLayout(); var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(index); return row.TranslatePoint(new Point(list.ActualWidth - 30, row.ActualHeight / 2), list); }
            int[] Selected() => list.SelectedItems.Cast<object>().Select(list.Items.IndexOf).Order().ToArray();
            var label = Descendants<HighlightedFileName>(list).First();
            Assert.IsFalse(behavior.Begin(At(1), ModifierKeys.None, label), "名前からはファイルDnDを開始できる");
            Assert.IsFalse(behavior.Begin(At(1), ModifierKeys.None, Descendants<GridViewColumnHeader>(list).First()), "列見出しの操作を奪わない");
            Assert.IsTrue(behavior.Begin(At(1), ModifierKeys.None, list));
            behavior.Update(At(4));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, Selected());
            behavior.Update(At(2)); CollectionAssert.AreEqual(new[] { 1, 2 }, Selected(), "矩形を縮めると選択も縮む");
            behavior.Finish(); Pump();
            Assert.IsNull(FileMarqueeSelection.Active(list)); Assert.IsFalse(list.IsMouseCaptured);
            CollectionAssert.AreEqual(new[] { 1, 2 }, Selected());
            CollectionAssert.AreEquivalent(new[] { ((FileEntry)list.Items[1]).FullPath, ((FileEntry)list.Items[2]).FullPath },
                FileDragBehavior.CaptureSources(list, (FileEntry)list.Items[1]), "複数選択からのDnDは全選択を渡す");

            Assert.IsTrue(behavior.Begin(At(2), ModifierKeys.Control, list)); behavior.Update(At(4));
            CollectionAssert.AreEqual(new[] { 1, 3, 4 }, Selected(), "Ctrlは矩形内を反転"); behavior.Finish();
            Assert.IsTrue(behavior.Begin(At(5), ModifierKeys.Shift, list)); behavior.Update(At(6));
            CollectionAssert.AreEqual(new[] { 1, 3, 4, 5, 6 }, Selected(), "Shiftは既存選択へ追加");
            behavior.Finish(cancel: true); CollectionAssert.AreEqual(new[] { 1, 3, 4 }, Selected(), "Escで開始前の選択へ復帰");

            Assert.IsTrue(behavior.Begin(At(1), ModifierKeys.None, list)); behavior.Update(At(4));
            Capture(window, "mouse-selection-rectangle.png");
            // Recycle visible containers while keeping the original selection anchor.
            list.ScrollIntoView(list.Items[100]); Pump(); list.UpdateLayout();
            var visible = Descendants<ListViewItem>(list).Where(r => r.IsVisible).ToArray();
            var endRow = visible.First(r => list.ItemContainerGenerator.IndexFromContainer(r) >= 95);
            var endIndex = list.ItemContainerGenerator.IndexFromContainer(endRow);
            behavior.Update(endRow.TranslatePoint(new Point(list.ActualWidth - 30, endRow.ActualHeight / 2), list));
            Assert.IsTrue(list.SelectedItems.Contains(list.Items[50]), "スクロールで画面外になった中間行も選択");
            Assert.IsTrue(list.SelectedItems.Contains(list.Items[endIndex])); behavior.Finish();
            Assert.AreEqual(endIndex, list.SelectedItems.Count);

            // A new filtered view invalidates a live gesture instead of reusing its indices.
            Assert.IsTrue(behavior.Begin(endRow.TranslatePoint(new Point(100, endRow.ActualHeight / 2), list), ModifierKeys.None, list));
            model.Workspace.Left.SelectedTab.FilterText = "item000"; Pump();
            Assert.IsNull(FileMarqueeSelection.Active(list)); Assert.IsFalse(list.IsMouseCaptured);
            Assert.AreEqual(10, list.Items.Count);
        }
        finally { window.Close(); Pump(); }
    }
    private sealed class MouseSelectionFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            Enumerable.Range(0, 300).Select(i => new FileEntry(Path.Combine(path, $"item{i:0000}.txt"), $"item{i:0000}.txt", false, i, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => throw new AssertFailedException("範囲選択でファイルを開かない");
    }
}
