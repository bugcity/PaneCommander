using System.Windows;
using System.Windows.Controls;

namespace PaneCommander.App.Views;

public sealed class ColorEditor : UserControl
{
    public TextBox Code { get; } = new();
    private readonly Button swatch = new() { Width = 36, ToolTip = "色を選択" };
    public event EventHandler? Changed;
    public string ColorCode { get => Code.Text; set => Code.Text = value; }
    public ColorEditor()
    {
        var row = new DockPanel(); DockPanel.SetDock(swatch, Dock.Right); row.Children.Add(swatch); row.Children.Add(Code); Content = row;
        Code.TextChanged += (_, _) => { swatch.Background = System.Text.RegularExpressions.Regex.IsMatch(Code.Text, "^#[0-9a-fA-F]{6}$") ? ThemeManager.Brush(Code.Text) : System.Windows.Media.Brushes.Transparent; Changed?.Invoke(this, EventArgs.Empty); };
        swatch.Click += (_, _) => { var picker = new ColorPicker(Code.Text) { Owner = Window.GetWindow(this) }; if (picker.ShowDialog() == true) Code.Text = picker.SelectedColor; };
    }
}
public sealed class ColorPicker : Window
{
    private readonly Slider[] channels = [new(), new(), new()]; private readonly Border sample = new() { Height = 56, Margin = new Thickness(6) };
    public string SelectedColor => $"#{(int)channels[0].Value:X2}{(int)channels[1].Value:X2}{(int)channels[2].Value:X2}";
    public ColorPicker(string hex)
    {
        Title = "色の選択"; Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource(typeof(Window)); var root = new StackPanel { Margin = new Thickness(14) }; Content = root; root.Children.Add(sample);
        for (var i = 0; i < 3; i++) { var row = new DockPanel(); row.Children.Add(new TextBlock { Text = new[] { "R", "G", "B" }[i], Width = 26 }); var s = channels[i]; s.Minimum = 0; s.Maximum = 255; s.TickFrequency = 1; s.IsSnapToTickEnabled = true; s.ValueChanged += (_, _) => sample.Background = ThemeManager.Brush(SelectedColor); row.Children.Add(s); root.Children.Add(row); }
        try { for (var i = 0; i < 3; i++) channels[i].Value = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16); } catch (Exception ex) when (ex is ArgumentException or FormatException) { foreach (var s in channels) s.Value = 128; }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; var cancel = new Button { Content = "キャンセル", IsCancel = true }; var ok = new Button { Content = "OK", IsDefault = true }; ok.Click += (_, _) => DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(ok); root.Children.Add(buttons);
    }
}
