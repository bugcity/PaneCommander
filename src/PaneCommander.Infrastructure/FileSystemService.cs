using System.Diagnostics;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class FileSystemService : IFileSystemService
{
    private readonly Action<ProcessStartInfo> launch;
    public FileSystemService(Action<ProcessStartInfo>? launch = null)
    {
        this.launch = launch ?? (info => { using var process = Process.Start(info); });
    }
    public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var fullPath = ValidatePath(path, cancellationToken);
        var directory = new DirectoryInfo(fullPath);
        var entries = new List<FileEntry>();
        // IgnoreInaccessible=false: 一部失敗を全件成功に見せない。
        foreach (var item in directory.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ArchiveTemporaryPaths.Contains(item.FullName)) continue;
            var attributes = item.Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            DateTime? modified = null;
            long? size = null;
            string? metadataError = null;
            try
            {
                modified = item.LastWriteTime;
                if (!isDirectory) size = ((FileInfo)item).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                metadataError = ex.Message;
            }
            entries.Add(new FileEntry(item.FullName, item.Name, isDirectory, size, modified,
                attributes.HasFlag(FileAttributes.ReparsePoint), metadataError,attributes.HasFlag(FileAttributes.ReparsePoint)?LinkMetadata.Kind(item.FullName):FileLinkKind.None,
                attributes.HasFlag(FileAttributes.Hidden)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        entries.Sort((a, b) => a.IsDirectory != b.IsDirectory ? a.IsDirectory ? -1 : 1
            : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        cancellationToken.ThrowIfCancellationRequested();
        return new FolderSnapshot(directory.FullName, entries);
    }, cancellationToken);

    public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidatePath(entry.FullPath, cancellationToken);
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルが見つかりません。", path);
        launch(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
    }, cancellationToken);

    // Pure syntax validation; it does not contact the server or change credentials.
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("フォルダの絶対パスを入力してください。");
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new NotSupportedException("デバイスパスは非対応です。ドライブの絶対パスまたはUNC共有パスを指定してください。");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(@"ドライブの絶対パスまたはUNC共有パス（\\server\share）を入力してください。");
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\');
            if (parts.Length < 2 || parts.Take(2).Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException(@"UNCパスにはサーバー名と共有名が必要です（\\server\share）。");
        }
        var fullPath = Path.GetFullPath(path);
        return fullPath;
    }

    public static string ValidatePath(string path, CancellationToken token = default)
    {
        var fullPath = NormalizePath(path);
        ResolveTarget(fullPath, token);
        return fullPath;
    }

    // Kept for existing diagnostic tools; validation now supports UNC and mapped drives.
    public static string ValidateLocalPath(string path) => ValidatePath(path);
    public static string ResolveLocalTarget(string path) => ResolveTarget(path);

    // Resolve one reparse component at a time. Preserve the logical path for Back/Up.
    public static string ResolveTarget(string path, CancellationToken token = default)
    {
        var resolved = NormalizePath(path); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var hop = 0; hop < 64; hop++)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(resolved)) throw new IOException("循環リンクを検出しました。");
            var root = Path.GetPathRoot(resolved)!;
            var parts = resolved[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root; var replaced = false;
            for (var i = 0; i < parts.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                current = Path.Combine(current, parts[i]);
                var attributes = File.GetAttributes(current); // Keep genuine access-denied errors.
                if (!attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(current) : new FileInfo(current);
                var target = info.ResolveLinkTarget(false) ?? throw new IOException("この再解析ポイントのリンク先を解決できません。");
                resolved = NormalizePath(Path.Combine(new[] { target.FullName }.Concat(parts.Skip(i + 1)).ToArray()));
                replaced = true; break;
            }
            if (!replaced) return resolved;
        }
        throw new IOException("リンクの参照回数が上限64を超えました（循環または過度に深いリンク）。");
    }
}
