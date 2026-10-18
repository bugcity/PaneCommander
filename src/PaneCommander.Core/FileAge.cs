using System.Globalization;

namespace PaneCommander.Core;

public sealed record AgePresentation(string Text, string Color);

public static class FileAge
{
    // Approximation, not One Commander's unpublished formula. No age buckets.
    public static double Hue(double hours)
    {
        var position = Math.Log(1 + Math.Max(0, hours));
        return 240 * (1 - Math.Exp(-position / 4));
    }

    public static AgePresentation Present(DateTime? modified, DateTime now, AppearanceSettings settings)
    {
        if (modified is null) return new("—", settings.Foreground);
        var elapsed = now.ToUniversalTime() - modified.Value.ToUniversalTime();
        var age = elapsed.Duration();
        var (number, unit) = age.TotalMinutes < 60 ? (Math.Floor(age.TotalMinutes), "m")
            : age.TotalHours < 24 ? (Math.Floor(age.TotalHours), "h")
            : age.TotalDays < 30 ? (Math.Floor(age.TotalDays), "d")
            : age.TotalDays < 365 ? (Math.Floor(age.TotalDays / 30), "M")
            : (Math.Floor(age.TotalDays / 365), "y");
        var text = (elapsed < TimeSpan.Zero ? "+" : "") + number.ToString(CultureInfo.InvariantCulture) + " " + unit;
        return new(text, settings.ColorFileAge ? PaletteColor(Hue(elapsed.TotalHours), settings) : settings.Foreground);
    }

    private static string PaletteColor(double hue, AppearanceSettings settings)
    {
        string[] anchors = [settings.AgeRed, settings.AgeYellow, settings.AgeGreen, settings.AgeCyan, settings.AgeBlue];
        double[] positions = [0, 60, 120, 180, 240];
        var i = 0; while (i < 3 && hue > positions[i + 1]) i++;
        var t = Math.Clamp((hue - positions[i]) / (positions[i + 1] - positions[i]), 0, 1);
        double Channel(string hex, int start) => Convert.ToInt32(hex.Substring(start, 2), 16) / 255d;
        double Mix(int start) => Channel(anchors[i], start) * (1 - t) + Channel(anchors[i + 1], start) * t;
        var r = Mix(1); var g = Mix(3); var b = Mix(5); var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2; var d = max - min;
        var s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        var h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        s = Math.Clamp(s * settings.FileAgeSaturation / .45, 0, 1);
        l = settings.FileAgeSaturation == 0 ? settings.FileAgeLightness : settings.FileAgeLightness <= .65 ? l * settings.FileAgeLightness / .65 : l + (1 - l) * (settings.FileAgeLightness - .65) / .35;
        return Color(h, s, l);
    }

    private static string Color(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = hue / 60; var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h < 1 ? (chroma, x, 0d) : h < 2 ? (x, chroma, 0d)
            : h < 3 ? (0d, chroma, x) : h < 4 ? (0d, x, chroma) : h < 5 ? (x, 0d, chroma) : (chroma, 0d, x);
        var m = lightness - chroma / 2;
        int Channel(double value) => (int)Math.Round((value + m) * 255);
        return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
    }
}
