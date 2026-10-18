using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class ColumnEditor : StackPanel
{
    private readonly List<Row> rows = [];
    private readonly StackPanel body = new();
    private readonly CheckBox fillName = new() { Content = "名前列に残り幅を割り当てる", Margin = new Thickness(4) };
    private sealed record Row(string Key, CheckBox Visible, TextBox Width, Grid Panel);
    public ColumnEditor(ColumnLayout layout)
    {
        Children.Add(new TextBlock { Text = "上下で順序を変更。幅はDIP。名前列は残り幅を使用します。\n狭い場合は名前を残し、種類・File Age・サイズ・更新日時の優先順で表示します。幅が戻ると復帰します。自動省略で保存値は変更しません。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        Children.Add(fillName); Children.Add(body); fillName.Click += (_, _) => UpdateNameWidth(); Fill(layout);
    }
    public void Fill(ColumnLayout layout)
    {
        rows.Clear(); body.Children.Clear();
        fillName.IsChecked = layout.NameFillsRemaining;
        foreach (var spec in layout.Columns)
        {
            var grid = new Grid { Margin = new Thickness(4) }; foreach (var w in new[] { 150d, 85, 42, 42 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            var visible = new CheckBox { Content = Label(spec.Key), IsChecked = spec.Visible, VerticalAlignment = VerticalAlignment.Center }; var width = new TextBox { Text = spec.Width.ToString("0.##", CultureInfo.InvariantCulture) };
            var up = new Button { Content = "↑" }; var down = new Button { Content = "↓" }; grid.Children.Add(visible); Grid.SetColumn(width, 1); grid.Children.Add(width); Grid.SetColumn(up, 2); grid.Children.Add(up); Grid.SetColumn(down, 3); grid.Children.Add(down);
            var row = new Row(spec.Key, visible, width, grid); rows.Add(row); body.Children.Add(grid); up.Click += (_, _) => Move(row, -1); down.Click += (_, _) => Move(row, 1);
        }
        UpdateNameWidth();
    }
    private void UpdateNameWidth() { foreach (var row in rows.Where(r => r.Key == "Name")) row.Width.IsEnabled = fillName.IsChecked != true; }
    private void Move(Row row, int delta) { var i = rows.IndexOf(row); var j = i + delta; if (j < 0 || j >= rows.Count) return; (rows[i], rows[j]) = (rows[j], rows[i]); body.Children.Clear(); foreach (var r in rows) body.Children.Add(r.Panel); }
    public ColumnLayout Read() { var result = new ColumnLayout { NameFillsRemaining = fillName.IsChecked == true, Columns = rows.Select(r => new ColumnSpec(r.Key, r.Visible.IsChecked == true, double.Parse(r.Width.Text, CultureInfo.InvariantCulture))).ToArray() }; result.Validate(); return result; }
    public static string Label(string key) => key switch { "Name" => "名前", "Kind" => "種類", "Size" => "サイズ", "FileAge" => "File Age", _ => "更新日時" };
}
