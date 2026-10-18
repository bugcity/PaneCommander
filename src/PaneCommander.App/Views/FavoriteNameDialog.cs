using System.Windows;
using System.Windows.Controls;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class FavoriteNameDialog : Window
{
    private readonly TextBox editor;
    public string FavoriteName => editor.Text.Trim();
    public FavoriteNameDialog(string initial)
    {
        Title = "お気に入りの表示名"; Width = 430; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "表示名（フォルダ名は変更しません）", Margin = new Thickness(0, 0, 0, 6) });
        editor = new TextBox { Text = initial, MaxLength = 120 }; panel.Children.Add(editor);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true }; buttons.Children.Add(cancel);
        var ok = new Button { Content = "OK", IsDefault = true }; buttons.Children.Add(ok);
        ok.Click += (_, _) => { if (FavoritesDocument.ValidName(FavoriteName)) DialogResult = true; else error.Text = "1〜120文字の表示名を入力してください。"; };
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { editor.Focus(); editor.SelectAll(); };
    }
}
