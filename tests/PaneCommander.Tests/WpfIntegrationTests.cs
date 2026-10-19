using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed partial class WpfIntegrationTests
{
    [TestMethod]
    public void WindowBindingsAndRoutedInputUseLogicalPaneAndPreserveTabSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var bindingErrors = new BindingErrorListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml")
            });
            MainWindow? window = null;
            try
            {
                var service = new FakeFileSystem();
                using var model = new MainViewModel(service, new FakeLogger());
                // WPFの実テンプレートを展開する。画面外・タスクバー非表示で実入力を奪わない。
                window = new MainWindow
                {
                    DataContext = model, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
                };
                window.Show();
                Pump();
                Assert.AreEqual("#FF111820", window.Background.ToString(), "MainWindowにWindowの基底スタイルを適用");
                Assert.AreEqual(14d, window.FontSize);
                Assert.AreEqual(1, application.Windows.Count, "テストで製品AppのOnStartupを実行して余分なウィンドウを作らない");
                var panes = Descendants<PaneView>(window).ToArray();
                Assert.AreEqual(2, panes.Length);
                var left = panes.Single(p => ((PaneViewModel)p.DataContext).State.Side == PaneSide.Left);
                var right = panes.Single(p => ((PaneViewModel)p.DataContext).State.Side == PaneSide.Right);
                var leftList = Descendants<ListView>(left).Single();
                Assert.AreEqual(1, leftList.Items.Count);
                var titleBar = (Border)window.FindName("AppTitleBar");
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.IsNotNull(chrome);
                Assert.AreEqual(40d, chrome.CaptionHeight);
                foreach (var label in new[] { "お気に入り", "更新", "設定" })
                {
                    var titleButton = Descendants<Button>(titleBar).Single(b => Equals(b.Content, label));
                    Assert.IsTrue(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome((UIElement)VisualTreeHelper.GetParent(titleButton)));
                    Assert.IsTrue(titleButton.Focusable);
                }
                CompleteWithDispatcher(FileIconProvider.GetAsync((FileEntry)leftList.Items[0])); Pump();
                Assert.IsNotNull(Descendants<ShellFileIcon>(leftList).First().Source);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m2-layout.json"), System.Text.Json.JsonSerializer.Serialize(new { windowWidth = window.ActualWidth, windowHeight = window.ActualHeight, listWidth = leftList.ActualWidth, listHeight = leftList.ActualHeight, listTop = leftList.TransformToAncestor((Visual)window.Content).Transform(new Point()).Y, fontSize = window.FontSize }));
                Assert.AreEqual(model.Workspace.Left.IsActive ? 1 : 0, Descendants<TextBlock>(window).Count(t => t.Text == "ACTIVE" && t.Visibility == Visibility.Visible));
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                bitmap.Render(background);
                bitmap.Render((Visual)window.Content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "m2-wpf.png"))) encoder.Save(stream);

                leftList.SelectedIndex = 0;
                var originalLeftTab = model.Workspace.Left.SelectedTab;
                var selected = originalLeftTab.SelectedEntry;
                Assert.IsNotNull(selected);
                Descendants<DateCell>(left).First().Text = "stale-tab";
                model.Left.AddTab.Execute(null);
                Pump();
                Assert.AreEqual(2, model.Workspace.Left.Tabs.Count);
                Descendants<TabControl>(left).Single().SelectedItem = originalLeftTab;
                Pump();
                Assert.AreSame(selected, originalLeftTab.SelectedEntry, "WPFタブ切替で選択状態を失わない");
                Assert.AreSame(selected, Descendants<ListView>(left).Single().SelectedItem);
                Assert.IsFalse(Descendants<DateCell>(left).Any(c => c.Text == "stale-tab"), "タブ切替で再計算");

                right.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Pump();
                Assert.AreEqual(PaneSide.Right, model.Workspace.ActiveSide);
                Assert.AreEqual(model.Workspace.Right.IsActive ? 1 : 0, Descendants<TextBlock>(right).Count(t => t.Text == "ACTIVE" && t.Visibility == Visibility.Visible));
                Assert.AreEqual(model.Workspace.Left.IsActive ? 1 : 0, Descendants<TextBlock>(left).Count(t => t.Text == "ACTIVE" && t.Visibility == Visibility.Visible));
                Assert.AreEqual(selected, originalLeftTab.SelectedEntry);

                // ツールバーにフォーカスが移っても論理側を保持する。
                var refresh = Descendants<Button>(window).First(b => b.Command == model.RefreshActive);
                refresh.Focus();
                Assert.AreEqual(PaneSide.Right, model.Workspace.ActiveSide);
                model.Right.State.SelectedTab.InputPath = Path.Combine(Path.GetTempPath(), "right-only");
                model.Right.Navigate.Execute(null);
                Pump();
                var rightPath = model.Right.State.SelectedTab.CurrentPath;
                Assert.AreEqual(originalLeftTab.CurrentPath, model.Left.State.SelectedTab.CurrentPath);

                var source = PresentationSource.FromVisual(window)!;
                RaiseKey(refresh, source, Key.Tab);
                Pump();
                Assert.AreEqual(PaneSide.Right,model.Workspace.ActiveSide,"ツールバーのTabは通常のフォーカス移動");
                RaiseKey(Descendants<ListView>(right).Single(),source,Key.Tab);Pump();
                Assert.AreEqual(PaneSide.Left, model.Workspace.ActiveSide);
                Assert.AreEqual(rightPath, model.Right.State.SelectedTab.CurrentPath);

                Assert.AreEqual(0, Descendants<TextBox>(window).Count(t => t.IsVisible), "常設Path入力を廃止");
                var readsBefore = service.ReadCount;
                RaiseKey(refresh, source, Key.F2);
                Assert.AreEqual(readsBefore, service.ReadCount, "F2は未実装のため更新しない");
                var pathModel = new PathDialogViewModel(model.Left.State.SelectedTab);
                pathModel.Path = Path.Combine(Path.GetTempPath(), "アドレス 入力");
                Assert.IsTrue(pathModel.SubmitAsync().GetAwaiter().GetResult());
                Pump();
                Assert.AreEqual(pathModel.Path, model.Left.State.SelectedTab.CurrentPath);
                Assert.AreEqual(rightPath, model.Right.State.SelectedTab.CurrentPath);
                // 実際の所有ダイアログをボタンの共通コマンドから開く。
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var dialog = application.Windows.OfType<PathDialog>().Single();
                    var editor = Descendants<TextBox>(dialog).Single();
                    Assert.AreEqual(model.Left.State.SelectedTab.CurrentPath, editor.Text);
                    Capture(dialog, "m2-path.png");
                    Assert.IsTrue(Descendants<Button>(dialog).Any(b => b.IsDefault));
                    Assert.IsTrue(Descendants<Button>(dialog).Any(b => b.IsCancel));
                    Descendants<Button>(dialog).Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                model.Left.GoToPath.Execute(null);
                Pump();
                Assert.AreEqual(10, model.Functions.Count);
                Assert.IsTrue(model.Functions.Take(9).All(f => !f.Command.CanExecute(null)));
                Assert.IsTrue(model.Functions[9].Command.CanExecute(null));
                Assert.IsTrue(Descendants<ListView>(window).All(l => ScrollViewer.GetHorizontalScrollBarVisibility(l) == ScrollBarVisibility.Auto));
                Assert.IsTrue(leftList.ActualHeight > 550, "一覧領域を拡大");
                var originalWeight = model.Settings.ColumnsFor(PaneSide.Left).Columns[2].Width;
                ((GridView)Descendants<ListView>(left).Single().View).Columns[2].Width *= 1.3;
                leftList.UpdateLayout();
                var sizeHeader = Descendants<GridViewColumnHeader>(leftList).Single(h => Equals(h.Column?.Header, "サイズ"));
                Descendants<System.Windows.Controls.Primitives.Thumb>(sizeHeader).Single().RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(40, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                Pump();
                Assert.AreNotEqual(originalWeight,model.Settings.LeftColumns!.Columns.Single(c=>c.Key=="Size").Width,"列幅を所有する側の設定へ保存");Assert.IsNull(model.Settings.RightColumns,"反対側の設定を変更しない");
                var resizedGrid = (GridView)leftList.View;
                var nameColumn = resizedGrid.Columns.Single(c => Equals(c.Header, "名前"));
                var nameHeader = Descendants<GridViewColumnHeader>(leftList).Single(h => h.Column == nameColumn);
                var nameGripper = Descendants<System.Windows.Controls.Primitives.Thumb>(nameHeader).Single();
                var untouchedColumns = model.Settings.LeftColumns!.Columns.Where(c => c.Key != "Name").ToArray();
                var actualViewport = Descendants<ScrollViewer>(leftList).First().ViewportWidth;
                var requestedNameWidth = actualViewport - 8 - 100;
                nameColumn.Width = requestedNameWidth; leftList.UpdateLayout();
                nameGripper.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(200, 0, false)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent }); Pump();
                Assert.IsFalse(model.Settings.LeftColumns!.NameFillsRemaining);
                Assert.AreEqual(requestedNameWidth, nameColumn.Width, .1, "名前列の手動拡大を他列より優先する");
                Assert.AreEqual(requestedNameWidth, model.Settings.LeftColumns.Columns.Single(c => c.Key == "Name").Width, .1);
                foreach (var label in new[] { "更新日時", "サイズ", "File Age" })
                    Assert.AreEqual(0d, resizedGrid.Columns.Single(c => Equals(c.Header, label)).Width, label);
                Assert.IsTrue(resizedGrid.Columns.Single(c => Equals(c.Header, "種類")).Width > 0);
                Assert.IsTrue(resizedGrid.Columns.Sum(c => c.Width) <= actualViewport - 8 + .1);
                Assert.IsTrue(model.Settings.LeftColumns.Columns.All(c => c.Visible));
                CollectionAssert.AreEqual(untouchedColumns, model.Settings.LeftColumns.Columns.Where(c => c.Key != "Name").ToArray(), "操作していない列の保存幅は変えない");
                nameColumn.Width = 180; leftList.UpdateLayout();
                nameGripper.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(-200, 0, false)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent }); Pump();
                Assert.IsTrue(resizedGrid.Columns.Single(c => Equals(c.Header, "File Age")).Width > 0, "名前列を狭めると省略列が戻る");
                Assert.IsTrue(resizedGrid.Columns.Single(c => Equals(c.Header, "サイズ")).Width > 0);
                Assert.IsNull(model.Settings.RightColumns);
                model.ApplySettingsAsync(AppearanceSettings.Light() with { FontSize = 16, ShowKind = false }).GetAwaiter().GetResult();
                Pump();
                Assert.AreEqual(16d, window.FontSize);
                Assert.AreEqual(0d, ((GridView)Descendants<ListView>(left).Single().View).Columns[1].Width);
                model.ApplySettingsAsync(model.Settings with { ShowFileAge = false }).GetAwaiter().GetResult(); Pump();
                Assert.IsTrue(Descendants<DateCell>(left).All(c => c.Visibility == Visibility.Collapsed));
                model.ApplySettingsAsync(model.Settings with { ShowFileAge = true }).GetAwaiter().GetResult(); Pump();
                Assert.IsTrue(Descendants<DateCell>(left).All(c => c.Visibility == Visibility.Visible));
                Assert.IsTrue(AbsoluteDates(left).All(c => c.Visibility == Visibility.Visible));
                Assert.AreEqual(5, ((GridView)Descendants<ListView>(left).Single().View).Columns.Count);
                model.ApplySettingsAsync(model.Settings with { ShowModified = false }).GetAwaiter().GetResult(); Pump();
                Assert.IsTrue(Descendants<DateCell>(left).All(c => c.Visibility == Visibility.Visible));
                Assert.IsTrue(AbsoluteDates(left).All(c => c.Visibility == Visibility.Collapsed));
                model.ApplySettingsAsync(model.Settings with { ShowModified = true }).GetAwaiter().GetResult(); Pump();
                Capture(window, "m2-light.png");
                Assert.AreEqual(pathModel.Path, model.Left.State.SelectedTab.CurrentPath);
                Dispatcher.CurrentDispatcher.BeginInvoke(() => { var settings = application.Windows.OfType<SettingsDialog>().Single(); Capture(settings, "m2-settings.png"); settings.Close(); });
                model.OpenSettings.Execute(null);
                Pump();
                window.Width = 850; window.Height = 480; Pump();
                var narrowList = Descendants<ListView>(left).Single();
                // 2026-10-17: low-priority columns are hidden instead of forcing horizontal overflow.
                var compactColumns=((GridView)narrowList.View).Columns;
                if (compactColumns.Single(c=>Equals(c.Header,"File Age")).Width == 0)
                    Assert.AreEqual(0d,compactColumns.Single(c=>Equals(c.Header,"サイズ")).Width,"File Ageより先にサイズを省略");
                if (compactColumns.Single(c=>Equals(c.Header,"サイズ")).Width == 0)
                    Assert.AreEqual(0d,compactColumns.Single(c=>Equals(c.Header,"更新日時")).Width,"サイズより先に更新日時を省略");
                Assert.IsTrue(compactColumns.Single(c=>Equals(c.Header,"名前")).Width>0,"名前は残す");
                Assert.IsTrue(compactColumns.Sum(c=>c.Width)<=narrowList.ActualWidth,"列は利用幅内に収まる");
                Capture(window, "m2-compact.png");
                window.WindowState = WindowState.Minimized; Pump();
                Assert.IsFalse(typeof(MainWindow).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Any(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType)), "日時常駐タイマーなし");
                window.WindowState = WindowState.Normal; window.Width = 1280; window.Height = 800; Pump();
                Assert.IsTrue(((GridView)narrowList.View).Columns.Single(c=>Equals(c.Header,"サイズ")).Width>=92,"拡大後は設定されたサイズ列が復帰");
                var ageCell = Descendants<DateCell>(left).First();
                Assert.AreEqual(Visibility.Visible,ageCell.Visibility,"File Ageセルも省略状態から復帰");
                var absoluteText = AbsoluteDates(left).First().Text;
                ageCell.Text = "stale";
                window.WindowState = WindowState.Minimized; Pump(); window.WindowState = WindowState.Normal; Pump();
                Assert.IsFalse(Descendants<DateCell>(left).Any(c => c.Text == "stale"), "再表示時に更新");
                model.ApplySettingsAsync(model.Settings with { ColorFileAge = false }).GetAwaiter().GetResult(); Pump();
                Assert.AreEqual(absoluteText, AbsoluteDates(left).First().Text);
                using (var ageModel = new MainViewModel(new AgeFixture(), new FakeLogger()))
                {
                    var ageWindow = new MainWindow { DataContext = ageModel, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                    try
                    {
                        ageWindow.Show(); Pump(); Capture(ageWindow, "file-age-dark.png");
                        var ageCells = Descendants<DateCell>(ageWindow).ToArray();
                        Assert.IsTrue(ageCells.Select(c => c.Background.ToString()).Distinct().Count() >= 8);
                        Assert.IsTrue(ageCells.All(c => c.TextAlignment == TextAlignment.Right));
                        Assert.IsTrue(ageCells.All(c => c.Foreground.ToString() == "#FF000000"));
                        Assert.IsTrue(ageCells.Any(c => c.Text == "1 y"));
                        ageCells[0].Text = "stale-navigation";
                        ageModel.Left.State.SelectedTab.NavigateAsync("age-fixture").GetAwaiter().GetResult(); Pump();
                        Assert.IsFalse(Descendants<DateCell>(ageWindow).Any(c => c.Text == "stale-navigation"), "同一メタデータでの再表示も更新");
                        ageModel.ApplySettingsAsync(AppearanceSettings.Light()).GetAwaiter().GetResult(); Pump(); Capture(ageWindow, "file-age-light.png");
                        ageModel.ApplySettingsAsync(ageModel.Settings with { FileAgeSaturation = 0, FileAgeLightness = .5 }).GetAwaiter().GetResult(); Pump();
                        Assert.IsTrue(Descendants<DateCell>(ageWindow).Where(c => c.Entry?.LastWriteTime is not null).All(c => c.Background.ToString() == "#FF808080"), "彩度・明度が背景に反映");
                        Capture(ageWindow, "file-age-grayscale.png");
                        ageModel.ApplySettingsAsync(ageModel.Settings with { ColorFileAge = false }).GetAwaiter().GetResult(); Pump();
                        Assert.IsTrue(Descendants<DateCell>(ageWindow).All(c => c.Background is null || c.Background.ToString() == "#00FFFFFF"), "色無効では背景を消去");
                        Capture(ageWindow, "file-age-color-off.png");
                        ageModel.ApplySettingsAsync(ageModel.Settings with { ColorFileAge = true, FileAgeLightness = 0 }).GetAwaiter().GetResult(); Pump();
                        Assert.IsTrue(Descendants<DateCell>(ageWindow).All(c => c.Background.ToString() == "#FF000000" && c.Foreground.ToString() == "#FFFFFFFF"), "暗い背景では白文字");
                    }
                    finally { ageWindow.Close(); }
                }
                if (Environment.GetEnvironmentVariable("PANECOMMANDER_DESIGN_DIRECTORY") is { Length: > 0 } designDirectory)
                {
                    using var directoryModel = new MainViewModel(new PaneCommander.Infrastructure.FileSystemService(), new FakeLogger());
                    var directoryWindow = new MainWindow { DataContext = directoryModel, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                    try
                    {
                        CompleteWithDispatcher(directoryModel.AddFavoriteAsync("表示サンプル", designDirectory));
                        directoryWindow.Show(); Pump();
                        CompleteWithDispatcher(directoryModel.Left.State.SelectedTab.NavigateAsync(designDirectory));
                        var firstDirectory = directoryModel.Left.State.SelectedTab.Entries.First(e => e.IsDirectory);
                        CompleteWithDispatcher(directoryModel.Right.State.SelectedTab.NavigateAsync(firstDirectory.FullPath));
                        Pump();
                        Assert.AreEqual(Path.GetFullPath(designDirectory), directoryModel.Left.State.SelectedTab.CurrentPath);
                        Assert.IsTrue(directoryModel.Left.State.SelectedTab.Entries.Any(e => e.IsDirectory));
                        Capture(directoryWindow, "directory-design-dark.png");
                        directoryModel.ApplySettingsAsync(AppearanceSettings.Light()).GetAwaiter().GetResult(); Pump();
                        Capture(directoryWindow, "directory-design-light.png");
                    }
                    finally { directoryWindow.Close(); }
                }
                Assert.ThrowsException<InvalidOperationException>(() => RenderingConfiguration.ApplyBeforeWindowCreation(RenderingPreference.Default), "Window生成後の描画変更を拒否");
                var expectedRender = Environment.GetEnvironmentVariable("PANECOMMANDER_TEST_RENDER_MODE") ?? "SoftwareOnly";
                Assert.AreEqual(expectedRender, RenderOptions.ProcessRenderMode.ToString());
                var m3Root = Path.Combine(Path.GetTempPath(), "PaneCommander M3 UI");
                var m3Session = new SessionDocument
                {
                    Left = new() { Tabs = [new(Path.Combine(m3Root, "left0")), new(Path.Combine(m3Root, "left1")), new(Path.Combine(m3Root, "left2"))], SelectedIndex = 2 },
                    Right = new() { Tabs = [new(Path.Combine(m3Root, "right0")), new(Path.Combine(m3Root, "right1")), new(Path.Combine(m3Root, "right2"))], SelectedIndex = 1 },
                    ActiveSide = PaneSide.Right, Layout = new() { LeftPaneFraction = .6, SidebarWidth = 220 }
                };
                var favorite = new FavoriteFolder(Guid.NewGuid(), "資料 日本語", Path.Combine(m3Root, "favorite"));
                var m3Service = new FakeFileSystem();
                var m3Profile = Path.Combine(Path.GetTempPath(), "PaneCommander-m3-wpf-" + Guid.NewGuid().ToString("N"));
                var m3SettingsStore = new PaneCommander.Infrastructure.JsonSettingsStore(Path.Combine(m3Profile, "settings.json"));
                var m3SessionStore = new PaneCommander.Infrastructure.JsonSessionStore(Path.Combine(m3Profile, "session.json"));
                using (var m3Model = new MainViewModel(m3Service, new FakeLogger(), settingsStore: m3SettingsStore, settings: new() { RenderingMode = Enum.Parse<RenderingPreference>(expectedRender) },
                    savedFavorites: new() { Items = [favorite] }, session: m3Session, sessionStore: m3SessionStore))
                {
                    var m3Window = new MainWindow { DataContext = m3Model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                    try
                    {
                        m3Window.Show(); Pump();
                        Assert.AreEqual(2, m3Service.ReadCount, "復元時に選択中の2タブだけ読込");
                        Assert.AreEqual(2, Descendants<FolderView>(m3Window).Count(), "6タブでも左右2ビューを共有");
                        Assert.AreEqual(PaneSide.Right, m3Model.Workspace.ActiveSide);
                        var favoriteList = (ListBox)m3Window.FindName("FavoritesList");
                        Assert.AreEqual(1, favoriteList.Items.Count);
                        var favoriteButton = Descendants<Button>(favoriteList).Single(b => b.Command == m3Model.OpenFavorite);
                        Assert.AreSame(favorite, favoriteButton.CommandParameter);
                        favoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        favoriteButton.Command.Execute(favoriteButton.CommandParameter); Pump();
                        Assert.AreEqual(favorite.Path, m3Model.Workspace.Right.SelectedTab.CurrentPath);
                        Assert.AreEqual(m3Session.Left.Tabs[2].Path, m3Model.Workspace.Left.SelectedTab.CurrentPath);
                        Assert.AreSame(favorite, m3Model.SelectedFavorite, "クリック対象を編集対象にもする");
                        Capture(m3Window, "m3-favorites.png");
                        for (var i = 0; i < 30; i++) { m3Model.Workspace.Left.SelectedTab = m3Model.Workspace.Left.Tabs[i % 3]; Pump(); }
                        Assert.AreEqual(2, Descendants<FolderView>(m3Window).Count());
                        CompleteWithDispatcher(m3Model.ApplySettingsAsync(m3Model.Settings with { ShowFavorites = false })); Pump();
                        Assert.AreEqual(Visibility.Collapsed, ((Border)m3Window.FindName("FavoritesSidebar")).Visibility);
                        CompleteWithDispatcher(m3Model.ApplySettingsAsync(m3Model.Settings with { ShowFavorites = true })); Pump();
                        Assert.AreEqual(220d, m3Window.CaptureLayout().SidebarWidth, 1);
                        Assert.AreEqual(.6, m3Window.CaptureLayout().LeftPaneFraction, .01);
                        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                        {
                            var settingsDialog = application.Windows.OfType<SettingsDialog>().Single();
                            Assert.IsTrue(Descendants<TextBlock>(settingsDialog).Any(t => t.Text.Contains("再起動が必要")));
                            var combo = Descendants<ComboBox>(settingsDialog).Single(c=>c.Items.OfType<ComboBoxItem>().Any(i=>i.Tag is RenderingPreference));
                            Assert.AreEqual(2, combo.Items.Count);
                            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(c => Equals(c.Tag, RenderingPreference.Default));
                            Capture(settingsDialog, "m3-render-settings.png");
                            Descendants<Button>(settingsDialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                        var actualBeforeSave = RenderOptions.ProcessRenderMode;
                        m3Model.OpenSettings.Execute(null); Pump();
                        Assert.AreEqual(RenderingPreference.Default, m3Model.Settings.RenderingMode);
                        Assert.AreEqual(RenderingPreference.Default, m3SettingsStore.LoadAsync().GetAwaiter().GetResult().Settings.RenderingMode, "設定画面から実JSONへ保存");
                        Assert.AreEqual(actualBeforeSave, RenderOptions.ProcessRenderMode, "設定保存は稼働中の描画方式を変えない");
                        m3Window.Width = 850; m3Window.Height = 480; Pump();
                        foreach(var list in Descendants<ListView>(m3Window))
                        {
                            var compact=((GridView)list.View).Columns;
                            var ageWidth=compact.Single(c=>Equals(c.Header,"File Age")).Width;
                            Assert.IsTrue(ageWidth==0 || ageWidth>=78,"File Ageは省略または可読幅で表示");
                            if(ageWidth==0)Assert.IsTrue(compact.Where(c=>Equals(c.Header,"サイズ")||Equals(c.Header,"更新日時")).All(c=>c.Width==0),"File Ageより低優先の列を先に省略");
                            Assert.IsTrue(compact.Sum(c=>c.Width)<=list.ActualWidth,"復元した設定でも列は利用幅内");
                        }
                        Capture(m3Window, "m3-compact.png");
                    }
                    finally { m3Window.Close(); CompleteWithDispatcher(m3Window.SessionSaveTask); Pump(); }
                    Assert.IsFalse(m3Window.IsVisible, "非同期保存の完了後に正常終了");
                    var writtenSession = m3SessionStore.LoadAsync().GetAwaiter().GetResult().Value!;
                    Assert.AreEqual(3, writtenSession.Left.Tabs.Length); Assert.AreEqual(PaneSide.Right, writtenSession.ActiveSide);
                    Assert.AreEqual(850d, writtenSession.Layout.Width); Assert.AreEqual(480d, writtenSession.Layout.Height);
                }
                Directory.Delete(m3Profile, true);
                VerifyReviewUi(application);
                VerifyColumnViewportFit();
                VerifyTabSortRestoration();
                VerifyDeleteFocus();
                VerifyActivePaneTarget();
                VerifyExternalDragFocusRecovery();
                VerifyFilterUi(application);
                VerifyKeyboardAndHiddenUi();
                VerifyFunctionAndClipboardKeys();
                VerifyNavigationFocus();
                VerifyTabReordering();
                VerifyDirectoryMonitoring();
                VerifyShellActionInputs();
                VerifyDropTargets();
                Assert.AreEqual("", bindingErrors.Errors, "WPF binding errors");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                application.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(45)), "WPF integration test timeout");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<TextBlock> AbsoluteDates(DependencyObject parent) => Descendants<TextBlock>(parent).Where(c => Equals(c.Tag, "AbsoluteModified"));

    private static void CompleteWithDispatcher(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(1); }
        Assert.IsTrue(task.IsCompleted, "確認用ディレクトリの読込待ち");
        task.GetAwaiter().GetResult();
    }

    private static void RaiseKey(UIElement target, PresentationSource source, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(background); bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public string Errors { get; private set; } = "";
        public override void Write(string? message) => Errors += message;
        public override void WriteLine(string? message) => Errors += message + Environment.NewLine;
    }

    private sealed class AgeFixture : IFileSystemService
    {
        private readonly FileEntry[] entries = new[] { .1, .5, 1, 3, 12, 24, 48, 168, 720, 8760, 43800 }
            .Select((hours, index) => new FileEntry("age-" + index, $"{index + 1:00} sample.txt", false, 1024, DateTime.Now.AddHours(-hours))).ToArray();
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path, entries));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}
