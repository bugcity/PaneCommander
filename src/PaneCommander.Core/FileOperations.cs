namespace PaneCommander.Core;

public enum FileOperationKind { Copy, Move, Delete, CreateDirectory, CreateFile }
public sealed record FileOperationRequest(FileOperationKind Kind, string[] Sources, string? Destination = null, string? NewName = null, bool Recycle = true, bool Overwrite = false);
public sealed record FileOperationResult(bool Aborted);
public interface IFileOperationService { Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default); }
public enum DropIntent { None, Copy, Move }
public static class DropPolicy
{
    // DnD does not prompt for a new name or silently generate one in the source folder.
    public static FileOperationRequest? CreateRequest(string[] sources, string destination, bool control, bool shift, bool alt = false)
    {
        var intent = Intent(sources, destination, control, shift, alt);
        if (intent == DropIntent.None) return null;
        var request = new FileOperationRequest(intent == DropIntent.Copy ? FileOperationKind.Copy : FileOperationKind.Move, sources.ToArray(), destination);
        if (!Path.IsPathFullyQualified(destination) || sources.Any(source => !Path.IsPathFullyQualified(source))) throw new ArgumentException("送り元と宛先は絶対パスで指定してください。");
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (sources.Any(source => string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), fullDestination, StringComparison.OrdinalIgnoreCase))) return null;
        Validate(request);
        return request;
    }
    public static DropIntent Intent(IReadOnlyList<string> sources, string destination, bool control, bool shift, bool alt = false)
    {
        if (sources.Count == 0 || alt || control && shift) return DropIntent.None;
        if (control) return DropIntent.Copy; if (shift) return DropIntent.Move;
        return sources.All(s => string.Equals(Path.GetPathRoot(s), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase)) ? DropIntent.Move : DropIntent.Copy;
    }
    public static void Validate(FileOperationRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || request.Sources is null || request.Sources.Length > 32768) throw new ArgumentException("操作の形式が不正です。");
        if (request.Overwrite && request.Kind is not (FileOperationKind.Copy or FileOperationKind.Move)) throw new ArgumentException("上書きの指定はコピー・移動のみで使用できます。");
        if (request.Kind != FileOperationKind.CreateDirectory && request.NewName is not null && (string.IsNullOrWhiteSpace(request.NewName) || request.NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.NewName is "." or "..")) throw new ArgumentException("新しい名前が不正です。");
        if (request.Kind != FileOperationKind.Delete && (request.Destination is null || !Path.IsPathFullyQualified(request.Destination))) throw new ArgumentException("送り先は絶対パスで指定してください。");
        if (request.Kind == FileOperationKind.CreateDirectory) DirectoryCreation.ParseRelativePath(request.NewName);
        else if (request.Kind == FileOperationKind.CreateFile) FileCreation.ValidateName(request.NewName);
        else if (request.Sources.Length == 0) throw new ArgumentException("操作対象を選択してください。");
        foreach (var source in request.Sources) if (!Path.IsPathFullyQualified(source) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) == Path.GetPathRoot(Path.GetFullPath(source))) throw new ArgumentException("ドライブ直下そのものは操作対象にできません。");
        if (request.Kind != FileOperationKind.CreateDirectory && request.Sources.Length > 1 && request.NewName is not null) throw new ArgumentException("複数項目の名前を一括指定することはできません。");
        if (request.Kind is FileOperationKind.Copy or FileOperationKind.Move)
        {
            if (request.Destination is null || !Path.IsPathFullyQualified(request.Destination)) throw new ArgumentException("送り先は絶対パスで指定してください。");
            var dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination));
            var sameFolderCount = request.Sources.Count(s => string.Equals(Path.GetDirectoryName(Path.GetFullPath(s)), dest, StringComparison.OrdinalIgnoreCase));
            if (request.Kind == FileOperationKind.Copy && sameFolderCount > 0 && sameFolderCount != request.Sources.Length) throw new ArgumentException("同じフォルダを含む異なる送り元のコピーは、分けて実行してください。");
            foreach (var source in request.Sources)
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                if (string.Equals(Path.GetDirectoryName(full), dest, StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Kind == FileOperationKind.Move && request.NewName is null) throw new ArgumentException("同じフォルダへの移動は実行しません。");
                    if (request.NewName is not null && string.Equals(Path.GetFileName(full), request.NewName, request.Kind == FileOperationKind.Move ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("元と同じ名前です。");
                }
                if (string.Equals(full, dest, StringComparison.OrdinalIgnoreCase) || dest.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("対象自身や配下のフォルダへは送り込めません。");
            }
        }
    }
}

public static class DropCompletion
{
    public static bool RequiresSourceDeletion(int returnedEffect, int? performedEffect, int? logicalEffect) => returnedEffect == 2 && performedEffect == 2 && (logicalEffect is null or 2);
}
