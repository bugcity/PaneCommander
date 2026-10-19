namespace PaneCommander.Core;

public sealed record FileEntry(string FullPath, string Name, bool IsDirectory, long? Size,
    DateTime? LastWriteTime, bool IsReparsePoint = false, string? MetadataError = null,FileLinkKind LinkType=FileLinkKind.None, bool IsHidden = false)
{
    public bool IsDotFile => !IsHidden && Name.StartsWith('.');
    public string Icon => IsReparsePoint ? "↗" : IsDirectory ? "▸" : "▫";
    public string Kind => IsReparsePoint ? IsDirectory ? LinkType==FileLinkKind.Junction?"[DIR] J":LinkType==FileLinkKind.SymbolicLink?"[DIR] S":"[DIR] ↗" : "リンク" : IsDirectory ? "[DIR]" : System.IO.Path.GetExtension(Name).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "ショートカット" : "ファイル";
    public string KindToolTip=>LinkType switch{FileLinkKind.Junction=>"J: ジャンクション。Enterはリンク先を開きます。コピー・移動・削除はリンク本体です。",FileLinkKind.SymbolicLink=>"S: シンボリックリンク。Enterはリンク先を開きます。コピー・移動・削除はリンク本体です。相対ターゲットは文字列を維持します。",_=>Kind};
    public string SizeText => IsDirectory ? "—" : Size is { } bytes ? FileSize.Format(bytes) : "不明";
    public string SizeToolTip => IsDirectory ? "—" : Size is { } bytes ? $"{bytes:N0} B" : "不明";
    public string ModifiedText => LastWriteTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "不明";
}

public sealed record FolderSnapshot(string Path, IReadOnlyList<FileEntry> Entries);

public interface IFileSystemService
{
    Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken);
    Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken);
}

public interface IErrorLogger
{
    void Log(string context, Exception error);
}

public interface IFolderWatchService
{
    IDisposable Watch(string path, Action changed, Action<Exception> failed);
}

public enum PaneSide { Left, Right }

public enum NavigationKind { Normal, Back, Forward, Refresh }
public enum FileLinkKind { None,SymbolicLink,Junction,OtherReparse }

public static class FileSize
{
    public static string Format(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        decimal value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
