using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyTabSortRestoration()
    {
        using var model = new MainViewModel(new TabSortFiles(), new FakeLogger());
        var window = new MainWindow { DataContext = model, Width = 1800, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var pane = model.Workspace.Left;
            var left = (PaneView)window.FindName("LeftPane");
            var tabs = (TabControl)left.FindName("Tabs");
            var first = pane.SelectedTab;
            void SizeClick()
            {
                Descendants<GridViewColumnHeader>(left).Single(h => Equals(h.Content, "サイズ"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            }
            void Check(TabState tab, bool descending)
            {
                Assert.AreSame(tab, pane.SelectedTab);
                Assert.AreEqual(nameof(FileEntry.Size), tab.SortProperty);
                Assert.AreEqual(descending, tab.SortDescending);
                var list = left.CurrentFileList!;
                Assert.AreSame(tab.Entries, list.ItemsSource);
                var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
                Assert.AreEqual(2, view.SortDescriptions.Count, "新しいItemsSourceにも並べ替えを適用");
                Assert.AreEqual(descending ? ListSortDirection.Descending : ListSortDirection.Ascending, view.SortDescriptions[1].Direction);
                var values = list.Items.Cast<FileEntry>().Where(e => !e.IsDirectory).Select(e => e.Size!.Value).ToArray();
                CollectionAssert.AreEqual(descending ? new long[] { 30, 20, 10 } : new long[] { 10, 20, 30 }, values);
                Assert.IsTrue(((FileEntry)list.Items[0]).IsDirectory, "フォルダーを先頭に維持");
            }
            SizeClick(); SizeClick(); Check(first, true);
            var second = pane.AddTab(); Pump(); SizeClick(); Check(second, false);
            for (var i = 0; i < 3; i++)
            {
                // Tab header selection and keyboard tab-switch logic must agree.
                tabs.SelectedItem = first; Pump(); Check(first, true);
                pane.SelectAdjacentTab(1); Pump(); Check(second, false);
            }
            first.ReleaseSnapshot(); // Reloading an evicted background snapshot must also restore the sort.
            tabs.SelectedItem = first; Pump(); Check(first, true);
            CompleteWithDispatcher(first.RefreshAsync()); Pump(); Check(first, true);
            tabs.SelectedItem = second; Pump(); Check(second, false);
            CompleteWithDispatcher(second.RefreshAsync()); Pump(); Check(second, false);
            Capture(window, "tab-sort-restored.png");
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class TabSortFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            [new(Path.Combine(path, "b.txt"), "b.txt", false, 20, null),
             new(Path.Combine(path, "a.txt"), "a.txt", false, 30, null),
             new(Path.Combine(path, "folder"), "folder", true, null, null),
             new(Path.Combine(path, "c.txt"), "c.txt", false, 10, null)]));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
