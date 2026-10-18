using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyReviewUi(Application application)
    {
        var profile = Path.Combine(Path.GetTempPath(), "PaneCommander-review-wpf-" + Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(Path.Combine(profile, "settings.json")); var operations = new ReviewUiOperations();
        using var model = new MainViewModel(new ReviewUiFiles(), new FakeLogger(), settingsStore: store, fileOperations: operations);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump(); CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\review\left")); CompleteWithDispatcher(model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\review\right")); Pump();
            Assert.AreEqual("left", window.Title);
            var panes = Descendants<PaneView>(window).ToArray(); var left = panes.Single(p => ((PaneViewModel)p.DataContext).State.Side == PaneSide.Left); var right = panes.Single(p => p != left);
            var leftList = left.CurrentFileList!; var rightList = right.CurrentFileList!;
            var defaultScroll = Descendants<ScrollViewer>(rightList).First(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "review-layout-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { rightList.ActualWidth, defaultScroll.ViewportWidth, defaultScroll.ExtentWidth, Horizontal = defaultScroll.ComputedHorizontalScrollBarVisibility.ToString() })); Assert.AreEqual(Visibility.Collapsed, defaultScroll.ComputedHorizontalScrollBarVisibility, "既定の通常幅で不要な水平スクロールを出さない");
            Assert.AreEqual(SelectionMode.Extended, leftList.SelectionMode); Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(leftList)); Assert.IsTrue(leftList.AllowDrop);
            leftList.SelectedIndex = 2; rightList.SelectedIndex = 3; var selected = leftList.SelectedItem;
            var insertRow = (ListViewItem)leftList.ItemContainerGenerator.ContainerFromItem(selected);
            var oppositeSelection = rightList.SelectedItem;
            RaiseKey(insertRow, PresentationSource.FromVisual(window)!, System.Windows.Input.Key.Insert); Pump();
            Assert.IsFalse(leftList.SelectedItems.Contains(selected), "Insertで対象行だけ解除");
            RaiseKey(insertRow, PresentationSource.FromVisual(window)!, System.Windows.Input.Key.Insert); Pump();
            Assert.IsTrue(leftList.SelectedItems.Contains(selected), "解除したフォーカス行も再選択できる");
            Assert.AreSame(oppositeSelection, rightList.SelectedItem, "反対側の選択を変えない");
            rightList.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent }); Pump();
            Assert.AreEqual(PaneSide.Right, model.Workspace.ActiveSide);
            Assert.AreSame(rightList, System.Windows.Input.FocusManager.GetFocusedElement(window), "空白クリックで一覧へフォーカスを合わせる");
            Assert.AreSame(oppositeSelection, rightList.SelectedItem, "空白クリックで選択を不必要に変更しない");
            model.Workspace.Activate(PaneSide.Left);
            var edit = new TextBox { Text = "abc" }; var dialog = new Window { Content = edit, Owner = window, ShowActivated = false };
            dialog.Show(); RaiseKey(edit, PresentationSource.FromVisual(dialog)!, System.Windows.Input.Key.Insert); dialog.Close();
            Assert.IsTrue(leftList.SelectedItems.Contains(selected), "別ダイアログのInsertを横取りしない");
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var copyDialog=application.Windows.OfType<FileOperationDialog>().Single();
                copyDialog.DialogResult=false;
            });
            var sameFolderTask=(Task)typeof(MainWindow).GetMethod("SameFolderOperationAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[FileOperationKind.Copy])!;
            CompleteWithDispatcher(sameFolderTask);Pump();
            var afterCopyCancel=System.Windows.Input.FocusManager.GetFocusedElement(window) as DependencyObject;
            Assert.IsTrue(ReferenceEquals(afterCopyCancel,leftList)||afterCopyCancel is not null&&leftList.IsAncestorOf(afterCopyCancel),"同一フォルダー操作のキャンセル後に一覧へフォーカスを戻す");
            var preview = new TextPreviewDialog(@"C:\fixture\sample.txt", "読み取り専用\nPreview") { Owner = window, ShowActivated = false };
            preview.Show(); Pump(); Assert.IsTrue(Descendants<TextBox>(preview).Single().IsReadOnly); Assert.AreEqual("読み取り専用\nPreview", Descendants<TextBox>(preview).Single().Text); preview.Close();
            model.Left.AddTab.Execute(null); Pump();
            var tabs = (TabControl)left.FindName("Tabs");
            var active = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(model.Workspace.Left.SelectedTab);
            var body = (Border)active.Template.FindName("TabBody", active);
            Assert.AreEqual(ThemeManager.Brush(window.IsActive && left.IsKeyboardFocusWithin ? model.Settings.ActiveTabBackground : model.Settings.InactiveTabBackground).ToString(), body.Background.ToString());
            var selectedTab = tabs.SelectedItem; model.Workspace.Toggle(); Pump(); Assert.AreSame(selectedTab, tabs.SelectedItem);
            Assert.AreEqual(ThemeManager.Brush(window.IsActive && left.IsKeyboardFocusWithin ? model.Settings.ActiveTabBackground : model.Settings.InactiveTabBackground).ToString(), body.Background.ToString()); Assert.AreEqual("right", window.Title);
            var unselected = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(model.Workspace.Left.Tabs[0]); var otherBody = (Border)unselected.Template.FindName("TabBody", unselected);
            Assert.AreEqual(ThemeManager.Brush(model.Settings.TabBackground).ToString(), otherBody.Background.ToString());
            model.Workspace.Left.SelectedTab = model.Workspace.Left.Tabs[0]; model.Workspace.Activate(PaneSide.Left); Pump(); Assert.AreSame(selected, left.CurrentFileList!.SelectedItem);
            var layout = new ColumnLayout { NameFillsRemaining = false, Columns = [new("Size", true, 130), new("Name", true, 250), new("Kind", false, 64), new("FileAge", true, 90), new("LastWriteTime", true, 180)] };
            CompleteWithDispatcher(model.ApplySettingsAsync(model.Settings with { LeftColumns = layout, TitleDisplay = TitleDisplay.FullPath })); Pump(); Assert.AreEqual(@"C:\review\left", window.Title);
            var grid = (GridView)left.CurrentFileList!.View; Assert.AreEqual("サイズ", grid.Columns[0].Header); Assert.AreEqual(130d, grid.Columns[0].Width); Assert.AreEqual(250d, grid.Columns[1].Width); Assert.AreEqual(0d, grid.Columns[2].Width);
            Assert.AreEqual("名前", ((GridView)right.CurrentFileList!.View).Columns[0].Header); Assert.IsNull(model.Settings.RightColumns);
            var sizeHeader = Descendants<GridViewColumnHeader>(left).Single(h => Equals(h.Content, "サイズ")); sizeHeader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Assert.AreEqual("Size", model.Workspace.Left.SelectedTab.SortProperty);
            var sizes = left.CurrentFileList.Items.OfType<FileEntry>().Where(f => !f.IsDirectory).Select(f => f.Size!.Value).ToArray(); CollectionAssert.AreEqual(sizes.OrderBy(s => s).ToArray(), sizes);
            var oldColor = ColorSchemes.Presets[1].Colors["Background"];
            var picker = new ColorPicker("#112233") { Owner = window, ShowActivated = false };
            Dispatcher.CurrentDispatcher.BeginInvoke(() => { var sliders = Descendants<Slider>(picker).ToArray(); sliders[0].Value = 170; Assert.AreEqual("#AA2233", picker.SelectedColor); Capture(picker, "review-color-picker.png"); picker.DialogResult = false; });
            Assert.AreEqual(false, picker.ShowDialog()); Assert.AreEqual("#112233", new ColorPicker("#112233").SelectedColor); Pump();
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var dialog = application.Windows.OfType<SettingsDialog>().Single(); var categories = Descendants<TabControl>(dialog).First(); Assert.AreEqual(6, categories.Items.Count); categories.SelectedIndex = 2; Pump();
                var colors = (Dictionary<PropertyInfo, ColorEditor>)typeof(SettingsDialog).GetField("colorEditors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                var background = colors.Single(kv => kv.Key.Name == "Background").Value;
                var original = model.Settings; background.ColorCode = ""; Descendants<Button>(dialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.IsTrue(dialog.IsVisible); Assert.AreSame(original, model.Settings, "不正な色を保存・適用しない");
                background.ColorCode = "#232A31";
                var name = (TextBox)typeof(SettingsDialog).GetField("schemeName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!; name.Text = "Review custom"; Descendants<Button>(dialog).Single(b => Equals(b.Content, "カスタム保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Capture(dialog, "review-settings-colors.png");
                categories.SelectedIndex = 3; Pump(); var font = Descendants<ComboBox>(dialog).Single(c => c.IsEditable); font.Text = Fonts.SystemFontFamilies.First().Source; Capture(dialog, "review-settings-font.png");
                Descendants<Button>(dialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            model.OpenSettings.Execute(null); Pump(); Assert.AreEqual("#232A31", model.Settings.Background); Assert.AreEqual("Review custom", model.Settings.ColorSchemeName); Assert.AreEqual(oldColor, ColorSchemes.Presets[1].Colors["Background"]);
            var saved = store.LoadAsync().GetAwaiter().GetResult().Settings; Assert.AreEqual("#232A31", saved.CustomSchemes!.Single(s => s.Name == "Review custom").Colors["Background"]); Assert.IsFalse(saved.LeftColumns!.NameFillsRemaining);
            var fontName = model.Settings.FontFamily;
            foreach (var preset in ColorSchemes.Presets) { CompleteWithDispatcher(model.ApplySettingsAsync(ColorSchemes.Apply(model.Settings, preset))); Pump(); Assert.AreEqual(fontName, model.Settings.FontFamily); Capture(window, "review-" + preset.Name.Replace(' ', '-') + ".png"); }
            var brush = application.Resources["CanvasBrush"]; var fontResource = application.Resources["AppFont"]; ThemeManager.Apply(model.Settings); Assert.AreSame(brush, application.Resources["CanvasBrush"]); Assert.AreSame(fontResource, application.Resources["AppFont"]);
            var countBefore = model.Settings.CustomSchemes!.Length;
            Dispatcher.CurrentDispatcher.BeginInvoke(() => { var dialog = application.Windows.OfType<SettingsDialog>().Single(); var categories = Descendants<TabControl>(dialog).First(); categories.SelectedIndex = 2; Pump(); var colors = (Dictionary<PropertyInfo, ColorEditor>)typeof(SettingsDialog).GetField("colorEditors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!; colors.First().Value.ColorCode = "#123456"; dialog.DialogResult = false; });
            var beforeCancel = model.Settings; model.OpenSettings.Execute(null); Pump(); Assert.AreSame(beforeCancel, model.Settings, "キャンセルで配色を適用しない"); Assert.AreEqual(countBefore, model.Settings.CustomSchemes!.Length);
            leftList = left.CurrentFileList!; leftList.SelectedItems.Clear(); leftList.SelectedItems.Add(leftList.Items[1]); leftList.SelectedItems.Add(leftList.Items[2]); model.NotifyFileCommands(); Assert.IsTrue(model.Functions[4].Command.CanExecute(null));
            Dispatcher.CurrentDispatcher.BeginInvoke(() => { var dialog = application.Windows.OfType<FileOperationDialog>().Single(); Assert.AreEqual(@"C:\review\right", dialog.Destination); Descendants<Button>(dialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            model.Functions[4].Command.Execute(null); Pump(); Assert.AreEqual(FileOperationKind.Copy, operations.Last!.Kind); Assert.AreEqual(2, operations.Last.Sources.Length);
            left.CurrentFileList!.SelectedIndex = 1; model.NotifyFileCommands(); model.Functions[7].Command.Execute(null); Pump(); Assert.AreEqual(FileOperationKind.Delete, operations.Last.Kind); Assert.IsTrue(operations.Last.Recycle, "最新仕様: F8はごみ箱。模擬サービスで実データを削除しない");
            RaiseKey(left.CurrentFileList!,PresentationSource.FromVisual(window)!,System.Windows.Input.Key.Delete);Pump();Assert.IsTrue(operations.Last.Recycle,"Deleteもごみ箱へ接続する");
            Assert.AreEqual(2, Descendants<FolderView>(window).Count()); Assert.IsTrue(Descendants<ListViewItem>(window).Count() < 100, "1200件でも仮想化を維持");
            Capture(window, "review-final-layout.png");
            // A selection in the detailed list must refresh the actual button,
            // not just pass CanExecute when the keyboard explicitly queries it.
            model.Workspace.Left.SelectedTab.SelectedEntry=null;
            var hierarchy=left.CurrentFileList!;
            var copyButton=Descendants<Button>(window).Single(b=>Equals(b.Content,"F5 コピー"));
            hierarchy.SelectedItems.Clear();Pump();Assert.IsFalse(copyButton.IsEnabled);
            hierarchy.SelectedIndex=1;Pump();Assert.IsTrue(copyButton.IsEnabled,"詳細一覧の選択でF5ボタンを有効にする");
            hierarchy.SelectedItems.Clear();Pump();Assert.IsFalse(copyButton.IsEnabled,"詳細一覧の選択解除でF5ボタンを無効にする");
            var effects=new DataObject();var effectStream=new MemoryStream(BitConverter.GetBytes(2));effectStream.Position=3;effects.SetData("Performed DropEffect",effectStream);
            Assert.AreEqual(2,FileDragBehavior.ReadEffect(effects,"Performed DropEffect"));Assert.AreEqual(3L,effectStream.Position);Assert.IsNull(FileDragBehavior.ReadEffect(effects,"missing"));
            var internalFiles=new DataObject();var linkPaths=new[]{@"C:\isolated\junction"};internalFiles.SetData(FileDragBehavior.InternalFilesFormat,linkPaths);
            Assert.IsFalse(internalFiles.GetDataPresent(DataFormats.FileDrop),"リンクをExplorerのShellコピーへ渡さない");CollectionAssert.AreEqual(linkPaths,FileDragBehavior.ReadPaths(internalFiles)!);
        }
        finally { window.Close(); var safe = Path.GetFullPath(Path.GetTempPath()) + "PaneCommander-review-wpf-"; if (Path.GetFullPath(profile).StartsWith(safe, StringComparison.OrdinalIgnoreCase) && Directory.Exists(profile)) Directory.Delete(profile, true); }
    }
    private sealed class ReviewUiFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path, Enumerable.Range(0, 1200).Select(i => new FileEntry(Path.Combine(path, $"item-{i:D4}"), $"item-{i:D4}", i == 0, i == 0 ? null : new[] { 560L, 65536L, 4456448L }[i % 3], DateTime.Now.AddHours(-i))).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ReviewUiOperations : IFileOperationService
    { public FileOperationRequest? Last; public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default) { Last = request; return Task.FromResult(new FileOperationResult(false)); } }
}
