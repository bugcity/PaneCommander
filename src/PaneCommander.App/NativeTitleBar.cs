using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaneCommander.Core;

namespace PaneCommander.App;

public static class NativeTitleBar
{
    private sealed class Applied { public string? Background; public string? Foreground; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Applied> colors = new();
    public static void Apply(Window window, AppearanceSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var applied = colors.GetValue(window, _ => new Applied()); if (applied.Background == settings.TitleBackground && applied.Foreground == settings.TitleForeground) return;
        if (Set(35, settings.TitleBackground) == 0 && Set(36, settings.TitleForeground) == 0) { applied.Background = settings.TitleBackground; applied.Foreground = settings.TitleForeground; }
        int Set(int attribute, string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); var color = (uint)(c.R | (c.G << 8) | (c.B << 16)); return DwmSetWindowAttribute(handle, attribute, ref color, 4); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
