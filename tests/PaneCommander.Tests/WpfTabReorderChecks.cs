using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyTabReordering()
    {
        var files = new FakeFileSystem();
        using var model = new MainViewModel(files, new FakeLogger());
        model.Workspace.Left.Restore(new PaneSnapshot
        {
            Tabs = [new(@"C:\tabs\first"), new(@"C:\tabs\second"), new(@"C:\tabs\third")], SelectedIndex = 1
        });
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var pane = model.Workspace.Left;
            var left = (PaneView)window.FindName("LeftPane");
            var tabs = (TabControl)left.FindName("Tabs");
            var selected = pane.SelectedTab;
            var entry = selected.Entries[0]; selected.SelectedEntry = entry; Pump();
            var reads = files.ReadCount;
            Assert.IsTrue(pane.MoveSelectedTab(-1)); Pump();
            Assert.AreSame(selected, tabs.SelectedItem);
            Assert.AreSame(selected, pane.Tabs[0]);
            Assert.AreSame(entry, left.CurrentFileList!.SelectedItem);
            Assert.IsTrue(pane.MoveSelectedTab(1)); Pump();
            Assert.AreSame(selected, tabs.SelectedItem);
            Assert.AreSame(selected, pane.Tabs[1]);
            Assert.AreSame(entry, left.CurrentFileList!.SelectedItem);
            Assert.AreEqual(reads, files.ReadCount, "並べ替えで再列挙しない");
            Assert.AreEqual(1, model.Workspace.Right.Tabs.Count);
            CollectionAssert.AreEqual(pane.Tabs.ToArray(), tabs.Items.Cast<TabState>().ToArray());
            Assert.IsTrue(tabs.AllowDrop);

            var right = (PaneView)window.FindName("RightPane");
            selected.ShowHiddenFiles = true;
            selected.SortProperty = nameof(FileEntry.Size);
            selected.SortDescending = true;
            var history = selected.CanGoBack;
            Assert.IsTrue(right.MoveTabHere(selected, 0)); Pump();
            Assert.AreSame(selected, model.Workspace.Right.SelectedTab);
            Assert.AreSame(selected, ((TabControl)right.FindName("Tabs")).SelectedItem);
            Assert.AreSame(entry, right.CurrentFileList!.SelectedItem);
            Assert.IsTrue(right.CurrentFileList.IsKeyboardFocusWithin, "移動先の一覧へフォーカスを移す");
            Assert.IsTrue(model.Workspace.Right.IsActive);
            Assert.AreEqual(history, selected.CanGoBack);
            Assert.IsTrue(selected.ShowHiddenFiles && selected.SortDescending);
            Assert.AreEqual(nameof(FileEntry.Size), selected.SortProperty);
            Assert.IsFalse(pane.Tabs.Contains(selected));
            Assert.IsTrue(left.MoveTabHere(selected)); Pump();
            Assert.AreSame(selected, pane.SelectedTab);
            Assert.AreSame(entry, left.CurrentFileList!.SelectedItem);
            Assert.IsTrue(left.CurrentFileList.IsKeyboardFocusWithin);
            Assert.IsFalse(left.MoveTabHere(selected), "同じ側への移動は無効");
            Assert.AreEqual(1, model.Workspace.Right.Tabs.Count);

            // The header menu also moves inactive tabs without changing their identity.
            var inactive = pane.Tabs.First(t => !ReferenceEquals(t, selected));
            var inactiveHeader = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(inactive);
            var menu = Descendants<Grid>(inactiveHeader).Single(g => g.ContextMenu is not null).ContextMenu!;
            menu.PlacementTarget = Descendants<Grid>(inactiveHeader).Single(g => g.ContextMenu is not null);
            menu.IsOpen = true; Pump();
            var move = (MenuItem)menu.Items[0];
            Assert.AreSame(inactive, move.DataContext);
            move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false; Pump();
            Assert.AreSame(inactive, model.Workspace.Right.SelectedTab);
            Assert.AreSame(selected, pane.SelectedTab);
            Assert.IsTrue(right.CurrentFileList!.IsKeyboardFocusWithin);

            // TabPanel stretches headers independently on each row. The close button
            // must follow the stretched right edge, including after row rearrangement.
            pane.Restore(new PaneSnapshot
            {
                Tabs = [new(@"C:\tabs\a"), new(@"C:\tabs\medium-name"),
                    new(@"C:\tabs\a-very-long-folder-name-for-ellipsis"), new(@"C:\tabs\b"),
                    new(@"C:\tabs\another-folder"), new(@"C:\tabs\last"),
                    new(@"C:\tabs\extra-folder"), new(@"C:\tabs\end")], SelectedIndex = 0
            });
            foreach (var width in new[] { 1000d, 1400d })
            {
                window.Width = width; Pump(); window.UpdateLayout();
                foreach (var index in new[] { 0, pane.Tabs.Count - 1 })
                {
                    tabs.SelectedItem = pane.Tabs[index]; Pump(); window.UpdateLayout();
                    var headers = Enumerable.Range(0, tabs.Items.Count)
                        .Select(i => (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
                    Assert.IsTrue(headers.Select(h => Math.Round(h.TranslatePoint(new Point(), tabs).Y)).Distinct().Count() >= 2,
                        "複数行のタブで検証する");
                    foreach (var header in headers)
                    {
                        var close = Descendants<Button>(header).Single(b => Equals(b.Content, "×"));
                        var closeRight = close.TranslatePoint(new Point(close.ActualWidth, 0), header).X;
                        Assert.AreEqual(12d, header.ActualWidth - closeRight, 1d,
                            $"閉じるボタンをタブ右端に固定: {((TabState)header.DataContext).Title}");
                        Assert.AreSame(header.DataContext, close.CommandParameter);
                    }
                }
            }
            Capture(window, "tab-close-multiple-rows.png");
        }
        finally { window.Close(); Pump(); }
        VerifyTransferredMultiSelection();
    }

    private static void VerifyTransferredMultiSelection()
    {
        using var model = new MainViewModel(new SizedFileSystem(5), new FakeLogger());
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var left = (PaneView)window.FindName("LeftPane");
            var right = (PaneView)window.FindName("RightPane");
            var list = left.CurrentFileList!;
            list.SelectedItems.Add(list.Items[1]); list.SelectedItems.Add(list.Items[3]);
            var paths = list.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray();
            var tab = model.Workspace.Left.SelectedTab;
            Assert.IsTrue(right.MoveTabHere(tab)); Pump();
            CollectionAssert.AreEquivalent(paths, right.CurrentFileList!.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray());
            Assert.IsTrue(right.CurrentFileList.IsKeyboardFocusWithin);
            Assert.AreEqual(1, model.Workspace.Left.Tabs.Count);
            Assert.IsTrue(left.MoveTabHere(tab)); Pump();
            CollectionAssert.AreEquivalent(paths, left.CurrentFileList!.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray());
            Assert.IsTrue(left.CurrentFileList.IsKeyboardFocusWithin);
        }
        finally { window.Close(); Pump(); }
    }
}
