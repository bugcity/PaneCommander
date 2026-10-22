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
    private const int FileCacheLimit = 128;
    private static readonly Dictionary<(string Path, long? Size, DateTime? Modified), Task<ImageSource>> fileCache = new();
    private static readonly Queue<(string Path, long? Size, DateTime? Modified)> fileOrder = new();
    private static readonly SemaphoreSlim fileGate = new(1);

    public static Task<ImageSource> GetAsync(FileEntry entry)
    {
        var extension = entry.IsDirectory ? "" : System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
        if (!entry.IsDirectory && !entry.IsReparsePoint && extension is ".exe" or ".ico")
        {
            // Executables and icon files have individual resources. Do not share by extension.
            // Use the listing's metadata for invalidation without doing filesystem I/O on the UI thread.
            lock (gate)
            {
                var key = (entry.FullPath.ToUpperInvariant(), entry.Size, entry.LastWriteTime);
                if (fileCache.TryGetValue(key, out var existing)) return existing;
                if (fileCache.Count >= FileCacheLimit) fileCache.Remove(fileOrder.Dequeue());
                var task = Task.Run(() => LoadFileAsync(entry.FullPath, extension));
                fileCache.Add(key, task); fileOrder.Enqueue(key); return task;
            }
        }
        // Ordinary types and links use metadata only, without following the entry path.
        return GetTypeAsync(extension, entry.IsDirectory);
    }

    private static Task<ImageSource> GetTypeAsync(string extension, bool directory)
    {
        lock (gate)
        {
            var key = (extension, directory);
            if (cache.TryGetValue(key, out var cached)) return cached;
            if (cache.Count >= CacheLimit - 2) key = ("", directory);
            if (!cache.TryGetValue(key, out cached))
                cache[key] = cached = Task.Run(() => LoadAsync(key.Item1, key.Item2));
            return cached;
        }
    }

    private static async Task<ImageSource> LoadFileAsync(string path, string extension)
    {
        ImageSource? image = null;
        await fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            path = System.IO.Path.GetFullPath(path);
            var safe = true;
            for (var part = path; !string.IsNullOrEmpty(part); part = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(part)))
                if (System.IO.File.GetAttributes(part).HasFlag(System.IO.FileAttributes.ReparsePoint)) { safe = false; break; }
            if (safe)
            {
                // Read icon resources directly; never launch the executable or resolve a shortcut.
                var handle = IntPtr.Zero;
                try
                {
                    var count = ExtractIconEx(path, 0, IntPtr.Zero, out handle, 1);
                    if (count is not (0 or uint.MaxValue) && handle != IntPtr.Zero) image = FromHandle(handle);
                }
                finally { if (handle != IntPtr.Zero) _ = DestroyIcon(handle); }
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        { /* Missing, inaccessible or resource-less files keep their normal type icon. */ }
        finally { fileGate.Release(); }
        return image ?? await GetTypeAsync(extension, false).ConfigureAwait(false);
    }

    private static BitmapSource FromHandle(IntPtr handle)
    {
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bitmap.Freeze(); return bitmap;
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
                    return FromHandle(info.Icon);
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW", SetLastError = true)]
    private static extern uint ExtractIconEx(string path, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
