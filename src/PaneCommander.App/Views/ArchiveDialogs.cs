using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class ArchiveCompressionDialog : Window
{
    private readonly TextBox name = new();
    private readonly ComboBox format = new(), level = new();
    public string ArchiveName => name.Text.Trim();
    public ArchiveFormat Format => (ArchiveFormat)((ComboBoxItem)format.SelectedItem).Tag;
    public ArchiveLevel Level => (ArchiveLevel)((ComboBoxItem)level.SelectedItem).Tag;
    public ArchiveCompressionDialog(string initial)
    {
        Title = "圧縮"; Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var root = new StackPanel { Margin = new Thickness(16) }; Content = root;
        root.Children.Add(new TextBlock { Text = "アーカイブ名（形式に合う拡張子を付けて保存します）", TextWrapping = TextWrapping.Wrap });
        name.Text = initial; root.Children.Add(name);
        root.Children.Add(new TextBlock { Text = "形式" }); root.Children.Add(format);
        foreach (var value in new[] { ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.TarGZip }) format.Items.Add(new ComboBoxItem { Tag = value, Content = ArchiveFormats.Extension(value)[1..] });
        root.Children.Add(new TextBlock { Text = "圧縮レベル" }); root.Children.Add(level);
        foreach (var (value, text) in new[] { (ArchiveLevel.None, "無圧縮"), (ArchiveLevel.Fast, "高速"), (ArchiveLevel.Normal, "標準"), (ArchiveLevel.Maximum, "最大") }) level.Items.Add(new ComboBoxItem { Tag = value, Content = text });
        level.SelectedIndex = 2;
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedItem is null) return;
            level.IsEnabled = Format != ArchiveFormat.Tar;
            foreach (ComboBoxItem item in level.Items) item.IsEnabled = ArchiveFormats.SupportsLevel(Format, (ArchiveLevel)item.Tag);
            if (!ArchiveFormats.SupportsLevel(Format, Level)) level.SelectedIndex = 2;
        };
        format.SelectedIndex = 0;
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }; root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true }); var ok = new Button { Content = "圧縮", IsDefault = true }; buttons.Children.Add(ok);
        ok.Click += (_, _) =>
        {
            try { FileCreation.ValidateName(ArchiveName); DialogResult = true; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
public sealed class ArchiveEncodingDialog : Window
{
    public ZipNameEncoding? SelectedEncoding { get; private set; }
    public ArchiveEncodingDialog(ArchiveNamePreview preview)
    {
        Title = "ZIPファイル名の文字コード"; Width = 900; Height = 540; MinWidth = 600; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var title = new TextBlock { Text = $"{System.IO.Path.GetFileName(preview.Archive)}\nUTF-8とCP932の両方で復号できます。フォルダー階層と名前を確認して選んでください。キャンセルするとこのアーカイブだけスキップします。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        foreach (var (value, text) in new[] { (ZipNameEncoding.Utf8, "UTF-8で展開"), (ZipNameEncoding.Cp932, "CP932で展開") })
        {
            var button = new Button { Content = text }; button.Click += (_, _) => { SelectedEncoding = value; DialogResult = true; }; buttons.Children.Add(button);
        }
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true });
        var list = new ListView { ItemsSource = preview.Names, Background = (System.Windows.Media.Brush)FindResource("CanvasBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true);
        list.View = new GridView { Columns = { new GridViewColumn { Header = "UTF-8（相対パス）", Width = 405, DisplayMemberBinding = new Binding(nameof(ArchiveNamePair.Utf8)) }, new GridViewColumn { Header = "CP932（相対パス）", Width = 405, DisplayMemberBinding = new Binding(nameof(ArchiveNamePair.Cp932)) } } };
        root.Children.Add(list);
    }
}
public sealed class ArchiveConflictDialog : Window
{
    public ArchiveConflictChoice Choice { get; private set; } = new(ArchiveConflictAction.Cancel);
    public ArchiveConflictDialog(ArchiveConflict conflict)
    {
        Title = "圧縮・展開の出力競合"; Width = 650; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var root = new StackPanel { Margin = new Thickness(16) }; Content = root;
        root.Children.Add(new TextBlock { Text = $"{conflict.Archive}\n出力先: {conflict.Path}\n" + (conflict.TypeMismatch ? "ファイルとフォルダーの種別が衝突します。上書きは既存の対象を置き換えます。" : "既存ファイル、または同じバッチの出力先と競合します。"), TextWrapping = TextWrapping.Wrap });
        var all = new CheckBox { Content = "以降の競合にも同じ選択を適用", Margin = new Thickness(0, 10, 0, 10) }; root.Children.Add(all);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(buttons);
        foreach (var (action, text) in new[] { (ArchiveConflictAction.Overwrite, "上書き"), (ArchiveConflictAction.Skip, "スキップ"), (ArchiveConflictAction.Rename, "名前変更（連番）"), (ArchiveConflictAction.Cancel, "全体をキャンセル") })
        {
            var button = new Button { Content = text, IsCancel = action == ArchiveConflictAction.Cancel };
            button.Click += (_, _) => { Choice = new(action, all.IsChecked == true); DialogResult = action != ArchiveConflictAction.Cancel; }; buttons.Children.Add(button);
        }
    }
}
internal sealed class ArchiveWindowInteraction(Window owner) : IArchiveInteraction
{
    public async Task<ZipNameEncoding?> ChooseEncodingAsync(ArchiveNamePreview preview, CancellationToken token) => await owner.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested(); var dialog = new ArchiveEncodingDialog(preview) { Owner = owner };
        using var cancel = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        dialog.ShowDialog(); token.ThrowIfCancellationRequested(); return dialog.SelectedEncoding;
    });
    public async Task<ArchiveConflictChoice> ResolveConflictAsync(ArchiveConflict conflict, CancellationToken token) => await owner.Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested(); var dialog = new ArchiveConflictDialog(conflict) { Owner = owner };
        using var cancel = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        dialog.ShowDialog(); token.ThrowIfCancellationRequested(); return dialog.Choice;
    });
}
