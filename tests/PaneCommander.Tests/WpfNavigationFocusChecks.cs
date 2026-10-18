using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
    private static void VerifyNavigationFocus()
    {
        var files = new NavigationFocusFiles();
        var favorite = new FavoriteFolder(Guid.NewGuid(), "destination", @"C:\focus-fixture\favorite");
        var empty = new FavoriteFolder(Guid.NewGuid(), "empty", @"C:\focus-fixture\empty");
        using var model = new MainViewModel(files, new FakeLogger(), savedFavorites: new FavoritesDocument { Items = [favorite, empty] });
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var left = (PaneView)window.FindName("LeftPane"); var right = (PaneView)window.FindName("RightPane");
            var tab = model.Workspace.Left.SelectedTab;
            CompleteWithDispatcher(tab.NavigateAsync(NavigationFocusFiles.Child)); Pump();
            left.CurrentFileList!.SelectedIndex = 0; left.FocusList(); Pump();
            RaiseKey((UIElement)Keyboard.FocusedElement, PresentationSource.FromVisual(window)!, Key.Back); Pump();
            Assert.AreEqual(NavigationFocusFiles.Parent, tab.CurrentPath);
            Assert.AreEqual(NavigationFocusFiles.Child, tab.SelectedEntry!.FullPath);
            Assert.IsTrue(Keyboard.FocusedElement is ListViewItem { IsSelected: true } row && ReferenceEquals(row.DataContext, tab.SelectedEntry));
            Assert.IsTrue(Descendants<ScrollViewer>(left.CurrentFileList!).First().VerticalOffset > 0, "仮想化された画面外の戻り先を表示する");
            CompleteWithDispatcher(tab.NavigateAsync(NavigationFocusFiles.Child)); tab.FilterText = "folder-0000"; Pump();
            model.Left.Up.Execute(null); Pump();
            Assert.AreEqual(1, left.CurrentFileList!.Items.Count);
            Assert.AreEqual("folder-0000", tab.SelectedEntry!.Name, "戻り先の子がフィルターで隠れる場合は表示中の先頭行を使う");
            Assert.IsTrue(left.CurrentFileList.IsKeyboardFocusWithin);
            tab.ClearFilter(); Pump();

            var leftPath = tab.CurrentPath;
            model.Workspace.Activate(PaneSide.Right);
            var favoriteList = (ListBox)window.FindName("FavoritesList"); favoriteList.Focus();
            var favoriteButton = Descendants<Button>(window).Single(b => b.DataContext is FavoriteFolder f && f.Id == favorite.Id);
            ((IInvokeProvider)new ButtonAutomationPeer(favoriteButton).GetPattern(PatternInterface.Invoke)).Invoke(); Pump();
            Assert.AreEqual(favorite.Path, model.Workspace.Right.SelectedTab.CurrentPath);
            Assert.AreEqual(leftPath, tab.CurrentPath);
            Assert.IsTrue(right.CurrentFileList!.IsKeyboardFocusWithin);
            Assert.IsTrue(Keyboard.FocusedElement is ListViewItem { IsSelected: true });
            Assert.AreEqual(favorite, model.SelectedFavorite);

            favoriteList.SelectedItem = empty; favoriteList.Focus();
            favoriteList.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent }); Pump();
            Assert.AreEqual(empty.Path, model.Workspace.Right.SelectedTab.CurrentPath);
            Assert.AreEqual(0, right.CurrentFileList!.Items.Count);
            Assert.IsTrue(right.CurrentFileList.IsKeyboardFocusWithin, "空のお気に入り先でも一覧へフォーカスを移す");

            // A command target remains selected while visual focus follows the keyboard.
            CompleteWithDispatcher(tab.NavigateAsync(NavigationFocusFiles.Child));
            model.Workspace.Activate(PaneSide.Left);
            left.CurrentFileList!.SelectedIndex = 0; left.FocusList(); Pump();
            var selection = left.CurrentFileList.SelectedItem;
            var selectedRow = (ListViewItem)left.CurrentFileList.ItemContainerGenerator.ContainerFromItem(selection);
            var paneBorder = (Border)left.Content;
            void CheckPane(bool focused)
            {
                Assert.AreEqual(ThemeManager.Brush(focused ? model.Settings.ActiveBackground : model.Settings.InactiveBackground).ToString(), paneBorder.Background.ToString());
                Assert.AreEqual(focused ? Visibility.Visible : Visibility.Hidden, Descendants<TextBlock>(left).Single(t => t.Text == "ACTIVE").Visibility);
            }
            CheckPane(window.IsActive);
            Assert.AreEqual(ThemeManager.Brush(window.IsActive ? model.Settings.ActiveSelection : model.Settings.InactiveSelection).ToString(), selectedRow.Background.ToString());
            foreach (var content in new[] { "お気に入り", "更新", "設定" })
            {
                var button = Descendants<Button>(window).Single(b => Equals(b.Content, content));
                button.Focus(); Pump();
                Assert.IsTrue(button.IsKeyboardFocused);
                CheckPane(false);
                Assert.AreEqual(ThemeManager.Brush(model.Settings.InactiveSelection).ToString(), selectedRow.Background.ToString());
                Assert.AreSame(selection, left.CurrentFileList.SelectedItem);
                Assert.AreEqual(PaneSide.Left, model.Workspace.ActiveSide);
            }
            left.BeginFilter(); Pump();
            CheckPane(window.IsActive);
            Assert.AreEqual(ThemeManager.Brush(model.Settings.InactiveSelection).ToString(), selectedRow.Background.ToString(), "フィルター入力中は一覧の選択を非フォーカス色にする");
            left.FocusList(); Pump();
            CheckPane(window.IsActive);
            Assert.AreEqual(ThemeManager.Brush(window.IsActive ? model.Settings.ActiveSelection : model.Settings.InactiveSelection).ToString(), selectedRow.Background.ToString());
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class NavigationFocusFiles : IFileSystemService
    {
        public const string Parent = @"C:\focus-fixture\parent";
        public const string Child = Parent + @"\zzzz-child";
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token)
        {
            IReadOnlyList<FileEntry> entries = path == Parent
                ? Enumerable.Range(0, 1200).Select(i => new FileEntry(Path.Combine(path, $"folder-{i:D4}"), $"folder-{i:D4}", true, null, null))
                    .Append(new FileEntry(Child, "zzzz-child", true, null, null)).ToArray()
                : path.EndsWith("empty", StringComparison.Ordinal) ? [] : [new FileEntry(Path.Combine(path, "sample.txt"), "sample.txt", false, 1, null)];
            return Task.FromResult(new FolderSnapshot(path, entries));
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
