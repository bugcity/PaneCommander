using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaneCommander.Core;

namespace PaneCommander.App;

public static class NativeTitleBar
{
    public static void BeginCaptionDrag(Window window, Point screenPoint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Use native caption behavior, including dragging a maximized window to restore it.
        var position = new IntPtr(unchecked((int)(((uint)(ushort)(int)screenPoint.Y << 16) | (ushort)(int)screenPoint.X)));
        ReleaseCapture();
        SendMessage(handle, 0x00A1, new IntPtr(2), position); // WM_NCLBUTTONDOWN / HTCAPTION
    }
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
