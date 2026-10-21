using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class SettingsDialog : Window
{
    private readonly MainViewModel model;
    private readonly Dictionary<PropertyInfo, Control> editors = [];
    private readonly Dictionary<PropertyInfo, ColorEditor> colorEditors = [];
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };
    private readonly Border preview = new() { Margin = new Thickness(5), Padding = new Thickness(6) };
    private readonly ComboBox schemas = new() { MinWidth = 160 }; private readonly TextBox schemeName = new() { Width = 130, ToolTip = "カスタム配色名" };
    private readonly ColumnEditor leftColumns, rightColumns;
    private ColorScheme[] custom = []; private bool filling;
    public SettingsDialog(MainViewModel model, PaneSide? columnsSide = null)
    {
        this.model = model; Title = "設定"; Width = 780; Height = 760; MinWidth = 620; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        leftColumns = new(model.Settings.ColumnsFor(PaneSide.Left)); rightColumns = new(model.Settings.ColumnsFor(PaneSide.Right));
        var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        AddButton(actions, "標準", () => Fill(new())); AddButton(actions, "ライト", () => TryAction(() => Fill(ColorSchemes.Apply(Read(), ColorSchemes.Presets.Single(s => s.Name == "Light"))))); AddButton(actions, "プレビュー", UpdatePreview);
        actions.Children.Add(new Button { Content = "キャンセル", IsCancel = true }); var apply = new Button { Content = "保存・適用", IsDefault = true }; actions.Children.Add(apply);
        apply.Click += async (_, _) => { apply.IsEnabled = false; try { await model.ApplySettingsAsync(Read()); DialogResult = true; } catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or System.IO.IOException or UnauthorizedAccessException) { error.Text = ex.Message; } finally { apply.IsEnabled = true; } };
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error); DockPanel.SetDock(preview, Dock.Bottom); root.Children.Add(preview);
        var tabs = new TabControl(); root.Children.Add(tabs); var groups = new Dictionary<string, StackPanel>();
        foreach (var name in new[] { "一般", "表示・レイアウト", "カラー", "フォント", "ファイル操作", "キーボード" }) { var panel = new StackPanel { Margin = new Thickness(8) }; groups[name] = panel; tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }); }
        groups["一般"].Children.Add(new TextBlock { Text = "描画方式の変更はアプリの再起動が必要です。\n現在: " + (model.StartupRenderingMode == RenderingPreference.SoftwareOnly ? "ソフトウェア描画" : "ハードウェア描画"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) });
        var schemeRow = new WrapPanel(); schemeRow.Children.Add(schemas); schemeRow.Children.Add(schemeName);
        AddButton(schemeRow, "カスタム保存", () => TryAction(() => { var name = schemeName.Text.Trim(); if (ColorSchemes.Presets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("プリセット名は変更できません。"); var schema = ColorSchemes.Capture(Read(), name); var next = custom.Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Append(schema).ToArray(); ColorSchemes.Validate(next); custom = next; filling = true; try { ReloadSchemas(name); } finally { filling = false; } UpdatePreview(); }));
        AddButton(schemeRow, "初期配色", () => TryAction(() => { var preset = ColorSchemes.Presets.FirstOrDefault(s => s.Name == schemas.SelectedItem as string) ?? ColorSchemes.Presets[0]; Fill(ColorSchemes.Apply(Read(), preset)); })); groups["カラー"].Children.Add(schemeRow);
        schemas.SelectionChanged += (_, _) => { if (filling || schemas.SelectedItem is not string name) return; var scheme = ColorSchemes.Presets.Concat(custom).FirstOrDefault(s => s.Name == name); if (scheme is not null) TryAction(() => Fill(ColorSchemes.Apply(Read(), scheme))); };
        var colorTabs = new TabControl(); var colorGroups = new Dictionary<string, StackPanel>(); foreach (var label in new[] { "基本", "ペイン・タブ", "File Age", "スクロールバー" }) { var panel = new StackPanel(); colorGroups[label] = panel; colorTabs.Items.Add(new TabItem { Header = label, Content = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); }
        groups["カラー"].Children.Add(colorTabs);
        foreach (var property in typeof(AppearanceSettings).GetProperties())
        {
            if (property.Name is "LeftColumns" or "RightColumns" or "CustomSchemes" or "ColorSchemeName" or "NameWidth" or "KindWidth" or "SizeWidth" or "FileAgeWidth" or "ModifiedWidth" or "ShowKind" or "ShowSize" or "ShowFileAge" or "ShowModified") continue;
            var row = new Grid { Margin = new Thickness(4, 3, 4, 3) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(new TextBlock { Text = Label(property.Name), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            FrameworkElement element;
            if (ColorSchemes.ColorProperties.Any(p => p.Name == property.Name)) { var editor = new ColorEditor(); colorEditors[property] = editor; element = editor; editor.Changed += (_, _) => { if (!filling) UpdatePreview(); }; }
            else
            {
                Control editor;
                if (property.PropertyType.IsEnum) { var combo = new ComboBox(); foreach (var value in Enum.GetValues(property.PropertyType)) { combo.Items.Add(new ComboBoxItem { Tag = value, Content = value switch { RenderingPreference.SoftwareOnly => "ソフトウェア描画（既定）", RenderingPreference.Default => "ハードウェア描画", TitleDisplay.DirectoryName => "ディレクトリ名のみ（既定）", TitleDisplay.FullPath => "フルパス", _ => value.ToString() } }); } editor = combo; combo.SelectionChanged += (_, _) => { if (!filling) UpdatePreview(); }; }
                else if (property.Name == "FontFamily")
                {
                    var combo = new ComboBox { IsEditable = true, IsTextSearchEnabled = false }; var names = Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values.Append(f.Source)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray(); combo.ItemsSource = names; var filtering = false;
                    combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (filling || filtering) return; filtering = true; try { var text = combo.Text; combo.ItemsSource = names.Where(n => n.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(150).ToArray(); } finally { filtering = false; } UpdatePreview(); })); editor = combo;
                }
                else if (property.PropertyType == typeof(bool)) { var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center }; check.Click += (_, _) => { if (!filling) UpdatePreview(); }; editor = check; }
                else { var box = new TextBox(); box.TextChanged += (_, _) => { if (!filling) UpdatePreview(); }; editor = box; }
                editors[property] = editor; element = editor;
            }
            Grid.SetColumn(element, 1); row.Children.Add(element); if (Category(property.Name) == "カラー") colorGroups[ColorCategory(property.Name)].Children.Add(row); else groups[Category(property.Name)].Children.Add(row);
        }
        var paneTabs = new TabControl(); paneTabs.Items.Add(new TabItem { Header = "左側の列", Content = leftColumns }); paneTabs.Items.Add(new TabItem { Header = "右側の列", Content = rightColumns }); groups["表示・レイアウト"].Children.Insert(0, paneTabs);
        groups["ファイル操作"].Children.Add(new TextBlock { Text = "F5: コピー　F6: 移動・名前変更　F7: ディレクトリ作成　Shift+F7: 空ファイルの新規作成\nShift+F5: 同じフォルダーに名前指定コピー（複数は各項目に指定）\nShift+F6: 単一項目の名前変更。複数選択では無効。\nF8・Delete・右クリックの削除: ごみ箱へ移動。\nShift+Delete: 確認後に完全削除。Shift+F8とは別です。\nリンクを含む項目のごみ箱操作は安全性未検証のため中止し、完全削除には切り替えません。\nコピー・移動・削除はリンク本体を扱います。相対ターゲットは文字列を維持するため、送り先では参照先が変わる場合があります。リンクを含む項目のExplorerへのドラッグは安全のため無効です。\n移動・削除はリンク本体を扱い、リンク先を削除しません。\n未実装の動作設定はここに表示しません。", TextWrapping = TextWrapping.Wrap });
        groups["キーボード"].Children.Add(new TextBlock { Text = "一覧内のTab / Shift+Tab: 左右切替\nInsert: フォーカス行の選択を反転\nF3: 読み取り専用テキスト表示（1 MiB以下、UTF-8またはBOM付きUTF-16/32）\nCtrl+T: タブ追加　Ctrl+W: タブを閉じる\nShift+左右: タブ切替　Ctrl+Shift+左右: タブを並べ替え（端で停止）\nタブ見出しのドラッグ: 同じペイン内で並べ替え\nCtrl+R: 更新　Ctrl+D: お気に入り登録\nShift+F5: 同じフォルダーに名前指定コピー　Shift+F6: 単一項目の名前変更\nShift+F7: 現在のフォルダーに空ファイルを新規作成（上書きしない）\nEnter/ダブルクリック: フォルダーへ移動・ファイルをShell既定Openで開く\nF4: 単一の通常ファイルをWindows ShellのEditで編集（未登録時は通知、Open代替なし）\nF2: 展開　Shift+F2: 圧縮（右クリックからも実行可）\nF1/F9は未計画。キー割当の編集は未実装。", TextWrapping = TextWrapping.Wrap });
        Fill(model.Settings); if (columnsSide is not null) { tabs.SelectedIndex = 1; paneTabs.SelectedIndex = columnsSide == PaneSide.Left ? 0 : 1; }
    }
    private void TryAction(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { error.Text = ex.Message; } }
    private void ReloadSchemas(string name) { schemas.ItemsSource = ColorSchemes.Presets.Select(s => s.Name).Concat(custom.Select(s => s.Name)).Append(name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); schemas.SelectedItem = name; schemeName.Text = name; }
    private void Fill(AppearanceSettings value)
    {
        filling = true;
        try { custom = value.CustomSchemes ?? []; foreach (var (p, c) in editors) { var v = p.GetValue(value); if (c is CheckBox check) check.IsChecked = (bool)v!; else if (c is ComboBox combo) { if (p.Name == "FontFamily") combo.Text = (string)v!; else combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, v)); } else ((TextBox)c).Text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; } foreach (var (p, c) in colorEditors) c.ColorCode = (string)p.GetValue(value)!; leftColumns.Fill(value.ColumnsFor(PaneSide.Left)); rightColumns.Fill(value.ColumnsFor(PaneSide.Right)); ReloadSchemas(value.ColorSchemeName); }
        finally { filling = false; }
        UpdatePreview();
    }
    private AppearanceSettings Read()
    {
        var value = model.Settings with { LeftColumns = leftColumns.Read(), RightColumns = rightColumns.Read(), CustomSchemes = custom.Length == 0 ? null : custom, ColorSchemeName = schemas.SelectedItem as string ?? model.Settings.ColorSchemeName };
        foreach (var (p, c) in editors) { object parsed = c is CheckBox check ? check.IsChecked == true : c is ComboBox combo ? p.Name == "FontFamily" ? combo.Text : ((ComboBoxItem?)combo.SelectedItem)?.Tag ?? throw new FormatException("選択してください。") : p.PropertyType == typeof(double) ? double.Parse(((TextBox)c).Text, CultureInfo.InvariantCulture) : ((TextBox)c).Text; p.SetValue(value, parsed); }
        foreach (var (p, c) in colorEditors) p.SetValue(value, c.ColorCode);
        if (ColorSchemes.Presets.Concat(custom).FirstOrDefault(s => s.Name == value.ColorSchemeName) is { } preset && (ColorSchemes.ColorProperties.Any(p => (string?)p.GetValue(value) != preset.Colors[p.Name]) || value.FileAgeSaturation != preset.Saturation || value.FileAgeLightness != preset.Lightness)) value = value with { ColorSchemeName = "カスタム（未登録）" };
        var errors = value.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("、", errors)); return value;
    }
    private void UpdatePreview()
    {
        if (filling) return;
        try { var value = Read(); preview.Background = ThemeManager.Brush(value.ActiveBackground); preview.BorderBrush = ThemeManager.Brush(value.ActiveBorder); preview.BorderThickness = new Thickness(value.BorderWidth); var sample = new StackPanel(); sample.SetValue(TextElement.FontFamilyProperty, ThemeManager.ResolveFont(value.FontFamily)); sample.SetValue(TextElement.FontSizeProperty, value.FontSize); sample.Children.Add(new TextBlock { Text = "C:\\Work   ACTIVE　▸ [DIR]　example.txt　4.25 MB", Foreground = ThemeManager.Brush(value.Foreground), MinHeight = value.RowHeight }); var ages = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var hours in new[] { .1, 3, 48, 720, 8760, 43800 }) { var age = FileAge.Present(DateTime.Now.AddHours(-hours), DateTime.Now, value); ages.Children.Add(new TextBlock { Text = age.Text, Width = 64, TextAlignment = TextAlignment.Right, Padding = new Thickness(3), Margin = new Thickness(2), Background = value.ColorFileAge ? ThemeManager.Brush(age.Color) : Brushes.Transparent, Foreground = ThemeManager.Brush(value.ColorFileAge ? ThemeManager.ContrastTextColor(age.Color) : value.Foreground) }); } sample.Children.Add(ages); preview.Child = sample; error.Text = ""; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { error.Text = ex.Message; }
    }
    private static void AddButton(Panel parent, string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => action(); parent.Children.Add(b); }
    private static string Category(string name) => name == "ArchiveReserveGiB" ? "ファイル操作" : name is "RenderingMode" or "RestoreSession" or "TitleDisplay" ? "一般" : name.StartsWith("Font", StringComparison.Ordinal) ? "フォント" : ColorSchemes.ColorProperties.Any(p => p.Name == name) || name.StartsWith("FileAgeS", StringComparison.Ordinal) || name.StartsWith("FileAgeL", StringComparison.Ordinal) || name == "ColorFileAge" ? "カラー" : "表示・レイアウト";
    private static string ColorCategory(string name) => name.StartsWith("Age", StringComparison.Ordinal) || name.Contains("FileAge", StringComparison.Ordinal) ? "File Age" : name.StartsWith("Scroll", StringComparison.Ordinal) ? "スクロールバー" : name.StartsWith("Active", StringComparison.Ordinal) || name.StartsWith("Inactive", StringComparison.Ordinal) || name.StartsWith("Tab", StringComparison.Ordinal) ? "ペイン・タブ" : "基本";
    private static string Label(string name) => ColorLabels.TryGetValue(name, out var label) ? label : name switch { "ArchiveReserveGiB" => "圧縮・展開で確保する空き容量（GiB）", "RenderingMode" => "描画方式（再起動後に反映）", "RestoreSession" => "起動時に作業状態を復元", "TitleDisplay" => "タイトルバーの表示形式", "FontFamily" => "フォント名で検索・選択", "FontSize" => "文字サイズ", "ShowFavorites" => "お気に入りサイドバーの表示", "RowHeight" => "行の高さ", "RowSpacing" => "行の間隔", "BorderWidth" => "ペイン枠の太さ", "ColorFileAge" => "File Ageの背景色を有効", "FileAgeSaturation" => "File Age 彩度（0〜1）", "FileAgeLightness" => "File Age 明度（0〜1）", _ => name };
    private static readonly IReadOnlyDictionary<string, string> ColorLabels = new Dictionary<string, string> { ["Background"] = "画面・メニュー背景", ["Foreground"] = "一覧・画面の文字", ["FilterMatchForeground"] = "フィルター一致文字", ["MutedForeground"] = "補助文字", ["TitleBackground"] = "タイトルバー背景", ["TitleForeground"] = "タイトルバー文字", ["ActiveBackground"] = "アクティブペイン背景", ["InactiveBackground"] = "非アクティブペイン背景", ["ActiveHeader"] = "アクティブヘッダー", ["InactiveHeader"] = "非アクティブヘッダー", ["ActiveBorder"] = "アクティブペイン枠", ["InactiveBorder"] = "通常の枠線", ["ActiveSelection"] = "アクティブ側の選択行", ["InactiveSelection"] = "非アクティブ側の選択行", ["ActiveTabBackground"] = "アクティブ側の選択タブ", ["ActiveTabBorder"] = "アクティブ選択タブ枠", ["InactiveTabBackground"] = "非アクティブ側の選択タブ", ["InactiveTabBorder"] = "非アクティブ選択タブ枠", ["TabBackground"] = "非選択タブ", ["ScrollTrack"] = "スクロールバーのトラック", ["ScrollThumb"] = "スクロールバーのつまみ", ["ScrollHover"] = "つまみのホバー", ["AgeRed"] = "File Age 赤系の補間色", ["AgeYellow"] = "File Age 黄系の補間色", ["AgeGreen"] = "File Age 緑系の補間色", ["AgeCyan"] = "File Age 水色の補間色", ["AgeBlue"] = "File Age 青系の補間色" };
}
