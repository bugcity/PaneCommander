using System.Reflection;
using System.Text.RegularExpressions;

namespace PaneCommander.Core;

public sealed record ColorScheme(string Name, IReadOnlyDictionary<string, string> Colors, double Saturation = .45, double Lightness = .65);
public static class ColorSchemes
{
    public static IReadOnlyList<PropertyInfo> ColorProperties { get; } = typeof(AppearanceSettings).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name is not ("FontFamily" or "ColorSchemeName")).ToArray();
    public static IReadOnlyList<ColorScheme> Presets { get; } = [Capture(new(), "Ocean Dark"), Capture(new AppearanceSettings
    {
        Background="#191B1E", InactiveBackground="#24272B", ActiveBackground="#2A2D32", ActiveHeader="#3D454B", InactiveHeader="#30353B",
        ActiveBorder="#A6B9C1", InactiveBorder="#555F68", ActiveSelection="#455A64", InactiveSelection="#3C4854",
        TitleBackground="#24272B", ActiveTabBackground="#4B5B64", ActiveTabBorder="#A6B9C1", InactiveTabBackground="#3B4653", InactiveTabBorder="#7D8C9E", TabBackground="#272B30",
        ScrollTrack="#24272B", ScrollThumb="#5B656D", ScrollHover="#87949D"
    }, "Graphite"), Capture(AppearanceSettings.Light(), "Light")];
    public static ColorScheme Capture(AppearanceSettings settings, string name) => new(name, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(ColorProperties.ToDictionary(p => p.Name, p => (string)p.GetValue(settings)!)), settings.FileAgeSaturation, settings.FileAgeLightness);
    public static AppearanceSettings Apply(AppearanceSettings current, ColorScheme scheme)
    {
        scheme = Upgrade(scheme);
        Validate([scheme], true); var copy = current with { ColorSchemeName = scheme.Name, FileAgeSaturation = scheme.Saturation, FileAgeLightness = scheme.Lightness };
        foreach (var p in ColorProperties) p.SetValue(copy, scheme.Colors[p.Name]);
        return copy;
    }
    public static ColorScheme Upgrade(ColorScheme scheme)
    {
        if (scheme.Colors is null || scheme.Colors.ContainsKey(nameof(AppearanceSettings.FilterMatchForeground))) return scheme;
        var colors = new Dictionary<string,string>(scheme.Colors);
        var background = colors.GetValueOrDefault(nameof(AppearanceSettings.Background), "#111820");
        colors[nameof(AppearanceSettings.FilterMatchForeground)] = AppearanceSettings.DefaultFilterMatchColor(background);
        return scheme with { Colors = colors };
    }
    public static void Validate(ColorScheme[]? schemes, bool allowPresetNames = false)
    {
        if (schemes is null) return;
        schemes = schemes.Select(s => s is null ? s! : Upgrade(s)).ToArray();
        if (!allowPresetNames && schemes.Any(s => s is not null && Presets.Any(p => p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("カスタム配色にプリセット名は使用できません。");
        if (schemes.Length > 32 || schemes.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80 || s.Colors is null || s.Colors.Count != ColorProperties.Count
            || ColorProperties.Any(p => !s.Colors.TryGetValue(p.Name, out var c) || c is null || !Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$"))
            || !double.IsFinite(s.Saturation) || !double.IsFinite(s.Lightness) || s.Saturation is < 0 or > 1 || s.Lightness is < 0 or > 1)
            || schemes.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != schemes.Length)
            throw new ArgumentException("カスタム配色の形式・色・名前が不正です（最大32件）。");
    }
}
