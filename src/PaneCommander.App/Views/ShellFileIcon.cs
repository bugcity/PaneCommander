using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class ShellFileIcon : Image
{
    private int revision;
    public ShellFileIcon()
    {
        Width = Height = 16;
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { revision++; Source = null; };
        DataContextChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    private async void Refresh()
    {
        var request = ++revision;
        Source = null;
        if (DataContext is not FileEntry entry) return;
        var icon = await FileIconProvider.GetAsync(entry);
        if (request == revision && IsLoaded) Source = icon;
    }
}

public static class FileIconProvider
{
    private const int CacheLimit = 256;
    private static readonly object gate = new();
    private static readonly Dictionary<(string Extension, bool Directory), Task<ImageSource>> cache = new();
    private static readonly SemaphoreSlim shellGate = new(1);

    public static Task<ImageSource> GetAsync(FileEntry entry)
    {
        // Use only type metadata: never access a file, executable, UNC share or link target.
        var extension = entry.IsDirectory ? "" : System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
        lock (gate)
        {
            var key = (extension, entry.IsDirectory);
            if (cache.TryGetValue(key, out var cached)) return cached;
            if (cache.Count >= CacheLimit - 2) key = ("", entry.IsDirectory);
            if (!cache.TryGetValue(key, out cached))
                cache[key] = cached = Task.Run(() => LoadAsync(key.Item1, key.Item2));
            return cached;
        }
    }

    private static async Task<ImageSource> LoadAsync(string extension, bool directory)
    {
        await shellGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 0);
            if (initialized < 0) return Fallback(directory);
            try
            {
                const uint iconSmallUsingAttributes = 0x100 | 0x1 | 0x10;
                _ = SHGetFileInfo(directory ? "folder" : "file" + extension, directory ? 0x10u : 0x80u,
                    out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), iconSmallUsingAttributes);
                if (info.Icon == IntPtr.Zero) return Fallback(directory);
                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                finally { _ = DestroyIcon(info.Icon); }
            }
            finally { CoUninitialize(); }
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        { return Fallback(directory); }
        finally { shellGate.Release(); }
    }

    private static ImageSource Fallback(bool directory)
    {
        var geometry = Geometry.Parse(directory ? "M1,4 L6,4 8,6 15,6 15,14 1,14 Z" : "M3,1 L10,1 14,5 14,15 3,15 Z M10,1 L10,5 14,5");
        var image = new DrawingImage(new GeometryDrawing(directory ? Brushes.Goldenrod : Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), geometry));
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
