using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace PaneCommander.Core;

public sealed record AppearanceSettings
{
    public RenderingPreference RenderingMode { get; init; } = RenderingPreference.SoftwareOnly;
    public bool ShowFavorites { get; init; } = true;
    public bool RestoreSession { get; init; } = true;
    public TitleDisplay TitleDisplay { get; init; } = TitleDisplay.DirectoryName;
    public ColumnLayout? LeftColumns { get; init; }
    public ColumnLayout? RightColumns { get; init; }
    public string ColorSchemeName { get; init; } = "Ocean Dark";
    public ColorScheme[]? CustomSchemes { get; init; }
    public string TitleBackground { get; init; } = "#19252F";
    public string TitleForeground { get; init; } = "#E5ECF3";
    public string ActiveTabBackground { get; init; } = "#28555B";
    public string ActiveTabBorder { get; init; } = "#72BCB4";
    public string InactiveTabBackground { get; init; } = "#354657";
    public string InactiveTabBorder { get; init; } = "#73869A";
    public string TabBackground { get; init; } = "#1C2732";
    public string ScrollTrack { get; init; } = "#17212B";
    public string ScrollThumb { get; init; } = "#4B6070";
    public string ScrollHover { get; init; } = "#748F9D";
    public string AgeRed { get; init; } = "#B77878";
    public string AgeYellow { get; init; } = "#B2A677";
    public string AgeGreen { get; init; } = "#7DA38C";
    public string AgeCyan { get; init; } = "#7CA4B0";
    public string AgeBlue { get; init; } = "#838FBA";
    public string FontFamily { get; init; } = "Yu Gothic UI";
    public double FontSize { get; init; } = 14;
    public double RowHeight { get; init; } = 30;
    public double RowSpacing { get; init; } = 1;
    public string Background { get; init; } = "#111820";
    public string Foreground { get; init; } = "#E5ECF3";
    public string FilterMatchForeground { get; init; } = "#FFE08A";
    public static string DefaultFilterMatchColor(string background)
    {
        var light = background is { Length: 7 } && int.TryParse(background[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb) && .2126 * ((rgb >> 16) & 255) + .7152 * ((rgb >> 8) & 255) + .0722 * (rgb & 255) > 128;
        return light ? "#7A2D00" : "#FFE08A";
    }
    public string InactiveBackground { get; init; } = "#19222D";
    public string ActiveBackground { get; init; } = "#1C2A37";
    public string ActiveHeader { get; init; } = "#184C50";
    public string InactiveHeader { get; init; } = "#253344";
    public string ActiveBorder { get; init; } = "#66DDD0";
    public string InactiveBorder { get; init; } = "#415266";
    public double BorderWidth { get; init; } = 3;
    public string ActiveSelection { get; init; } = "#21666C";
    public string InactiveSelection { get; init; } = "#425169";
    public string MutedForeground { get; init; } = "#A7B6C6";
    public bool ColorFileAge { get; init; } = true;
    public double FileAgeSaturation { get; init; } = .45;
    public double FileAgeLightness { get; init; } = .65;
    public bool ShowFileAge { get; init; } = true;
    public double FileAgeWidth { get; init; } = 12;
    public bool ShowKind { get; init; } = true;
    public bool ShowSize { get; init; } = true;
    public bool ShowModified { get; init; } = true;
    public double NameWidth { get; init; } = 40;
    public double KindWidth { get; init; } = 10;
    public double SizeWidth { get; init; } = 12;
    public double ModifiedWidth { get; init; } = 32;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(RenderingMode)) errors.Add("RenderingMode: SoftwareOnly または Default を指定");
        if (!Enum.IsDefined(TitleDisplay)) errors.Add("タイトル表示方式が不正です。");
        if (string.IsNullOrWhiteSpace(ColorSchemeName) || ColorSchemeName.Length > 80) errors.Add("カラースキーマ名は1〜80文字。");
        try { LeftColumns?.Validate(); RightColumns?.Validate(); ColorSchemes.Validate(CustomSchemes); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        foreach (var property in typeof(AppearanceSettings).GetProperties())
        {
            if (property.PropertyType == typeof(string))
            {
                var value = (string?)property.GetValue(this);
                if (property.Name == nameof(ColorSchemeName)) continue;
                if (property.Name == nameof(FontFamily))
                { if (string.IsNullOrWhiteSpace(value) || value.Length > 120) errors.Add("FontFamily: フォント名は1〜120文字"); }
                else if (value is null || !Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$")) errors.Add($"{property.Name}: 色は #RRGGBB 形式");
            }
            else if (property.PropertyType == typeof(double) && !double.IsFinite((double)property.GetValue(this)!))
                errors.Add($"{property.Name}: 有限の数値を指定");
        }
        void Range(string name, double value, double min, double max) { if (value < min || value > max) errors.Add($"{name}: {min}〜{max}"); }
        Range(nameof(FontSize), FontSize, 10, 32); Range(nameof(RowHeight), RowHeight, 24, 80);
        Range(nameof(RowSpacing), RowSpacing, 0, 12); Range(nameof(BorderWidth), BorderWidth, 2, 8);
        if (RowHeight < FontSize + 6) errors.Add("RowHeight: 文字サイズ＋6以上を指定");
        Range(nameof(FileAgeSaturation), FileAgeSaturation, 0, 1);
        Range(nameof(FileAgeLightness), FileAgeLightness, 0, 1);
        foreach (var pair in new[] { (nameof(FileAgeWidth), FileAgeWidth), (nameof(NameWidth), NameWidth), (nameof(KindWidth), KindWidth), (nameof(SizeWidth), SizeWidth), (nameof(ModifiedWidth), ModifiedWidth) })
            Range(pair.Item1, pair.Item2, 1, 1000);
        return errors;
    }

    public static AppearanceSettings Light() => new()
    {
        Background = "#F4F7FA",
        Foreground = "#152B40",
        FilterMatchForeground = "#7A2D00",
        InactiveBackground = "#EBF0F5",
        ActiveBackground = "#FFFFFF",
        ActiveHeader = "#C8EEE8",
        InactiveHeader = "#DCE4ED",
        ActiveBorder = "#087C73",
        InactiveBorder = "#8C9EAE",
        ActiveSelection = "#9EDAD2",
        InactiveSelection = "#CFD9E5",
        MutedForeground = "#536579",
        ColorSchemeName = "Light",
        TitleBackground = "#E1E8EE",
        TitleForeground = "#152B40",
        ActiveTabBackground = "#B9DED7",
        ActiveTabBorder = "#087C73",
        InactiveTabBackground = "#CCD7E3",
        InactiveTabBorder = "#657C91",
        TabBackground = "#EBF0F5",
        ScrollTrack = "#EBF0F5",
        ScrollThumb = "#9BAFBF",
        ScrollHover = "#657F94"
    };

    public ColumnLayout ColumnsFor(PaneSide side) => (side == PaneSide.Left ? LeftColumns : RightColumns) ?? new ColumnLayout
    {
        Columns = [new("Name", true, NameWidth * 4), new("Kind", ShowKind, Math.Max(64, KindWidth * 4)), new("Size", ShowSize, Math.Max(92, SizeWidth * 4)),
            new("FileAge", ShowFileAge, Math.Max(78, FileAgeWidth * 4)), new("LastWriteTime", ShowModified, Math.Max(150, ModifiedWidth * 4))]
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<RenderingPreference>))]
public enum RenderingPreference { SoftwareOnly, Default }

[JsonConverter(typeof(JsonStringEnumConverter<TitleDisplay>))]
public enum TitleDisplay { DirectoryName, FullPath }

public sealed record SettingsLoadResult(AppearanceSettings Settings, string? Error = null);
public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken = default);
}
