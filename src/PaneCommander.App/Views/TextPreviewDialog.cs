using System.Windows;
using System.Windows.Controls;

namespace PaneCommander.App.Views;

public sealed class TextPreviewDialog : Window
{
    public TextPreviewDialog(string path, string text)
    {
        Title = "表示 — " + System.IO.Path.GetFileName(path);
        Width = 850; Height = 600; MinWidth = 400; MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource(typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(10) }; Content = panel;
        var close = new Button { Content = "閉じる", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var label = new TextBlock { Text = path, ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true, AcceptsTab = true };
        System.Windows.Automation.AutomationProperties.SetName(content, "読み取り専用プレビュー");
        panel.Children.Add(content);
        Loaded += (_, _) => content.Focus();
    }
}
