using System.Windows;
using System.Windows.Controls;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class FileOperationDialog : Window
{
    private readonly TextBox path = new();
    private readonly TextBox name = new();
    public string Destination => path.Text.Trim();
    public string? NewName => string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
    public bool Overwrite { get; private set; }
    public FileOperationDialog(FileOperationKind kind, int count, string initial, string? originalFolder = null, string? requiredOriginalName = null, string? originalName = null, bool originalIsDirectory = false, bool collisionOnly = false)
    {
        var editsName = count == 1 && kind is FileOperationKind.Copy or FileOperationKind.Move;
        name.Text = requiredOriginalName ?? originalName ?? "";
        void SelectName()
        {
            var extension = originalIsDirectory ? "" : System.IO.Path.GetExtension(name.Text);
            var length = name.Text.Length - extension.Length;
            name.Select(0, length > 0 ? length : name.Text.Length);
        }
        name.GotKeyboardFocus += (_, _) => SelectName();
        Title = kind == FileOperationKind.Copy ? "ファイルコピー" : kind == FileOperationKind.Move ? "移動・名前変更" : kind == FileOperationKind.CreateFile ? "ファイルの新規作成" : "ディレクトリ作成"; Width = 650; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var root = new StackPanel { Margin = new Thickness(16) }; Content = root; root.Children.Add(new TextBlock { Text = collisionOnly ? "送り先に同じ名前の項目があります。送り先フォルダー：" : kind == FileOperationKind.CreateDirectory ? "新しいフォルダー（複数階層可）" : kind == FileOperationKind.CreateFile ? "新しいファイル名（拡張子を含む）" : $"選択した{count}項目の送り先フォルダ", TextWrapping = TextWrapping.Wrap }); path.Text = initial; path.IsReadOnly = collisionOnly; root.Children.Add(path);
        if (kind == FileOperationKind.CreateFile) root.Children.Add(new TextBlock { Text = "現在のフォルダーに空のファイルを作成します。同名の項目は上書きしません。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (kind == FileOperationKind.CreateDirectory) root.Children.Add(new TextBlock { Text = @"例：親\子\孫（現在のフォルダーからの相対パス）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.OrangeRed };
        root.Children.Add(validation);
        if (editsName) { root.Children.Add(new TextBlock { Text = "新しい名前（そのままなら同じ名前・空欄でも変更しません）" }); root.Children.Add(name); if (!collisionOnly) { var same = new Button { Content = "元のフォルダで名前を変更", HorizontalAlignment = HorizontalAlignment.Left }; same.Click += (_, _) => { path.Text = originalFolder ?? initial; name.Focus(); SelectName(); }; root.Children.Add(same); } }
        if (kind == FileOperationKind.Copy) root.Children.Add(new TextBlock { Text = "リンク自体を複製します。相対ターゲットはそのまま保持するため、送り先では参照先が変わる場合があります。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        if(requiredOriginalName is not null){path.IsReadOnly=true;name.Text=requiredOriginalName;root.Children.Add(new TextBlock{Text="新しい名前を明示指定してください。同じ名前・空欄では実行しません。",TextWrapping=TextWrapping.Wrap});}
        if (kind is FileOperationKind.Copy or FileOperationKind.Move) root.Children.Add(new TextBlock { Text = "同名のファイルは上書きします。フォルダー内の同名ファイルも対象です。追加の確認は表示しません。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(new Button { Content = "キャンセル", IsCancel = true }); var ok = new Button { Content = kind == FileOperationKind.Copy ? "コピー（同名は上書き）" : kind == FileOperationKind.Move ? "移動（同名は上書き）" : "OK", IsDefault = true }; ok.Click += (_, _) =>
        {
            if(string.IsNullOrWhiteSpace(Destination))return;
            if (kind == FileOperationKind.CreateDirectory)
            {
                try { DirectoryCreation.ParseRelativePath(Destination); }
                catch (ArgumentException error) { validation.Text = error.Message; return; }
            }
            if (kind == FileOperationKind.CreateFile)
            {
                try { FileCreation.ValidateName(Destination); }
                catch (ArgumentException error) { validation.Text = error.Message; return; }
            }
            if(requiredOriginalName is not null && !SameFolderOperation.IsValidName(requiredOriginalName,NewName,kind))return;
            Overwrite = kind is FileOperationKind.Copy or FileOperationKind.Move;
            DialogResult = true;
        }; actions.Children.Add(ok); root.Children.Add(actions); Loaded += (_, _) =>
        {
            if (editsName && name.Text.Length > 0) { name.Focus(); SelectName(); }
            else { path.Focus(); path.SelectAll(); }
        };
    }
}
