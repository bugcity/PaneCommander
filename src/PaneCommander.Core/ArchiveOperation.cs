namespace PaneCommander.Core;

public enum ArchiveFormat { Zip, SevenZip, Tar, TarGZip, TarXZ, TarZstd }
public enum ArchiveLevel { None, Fast, Normal, Maximum }
public enum ZipNameEncoding { Utf8, Cp932 }
public enum ArchiveConflictAction { Overwrite, Skip, Rename, Cancel }
public sealed record ArchiveCompressionRequest(string[] Sources, string Directory, string Name, ArchiveFormat Format, ArchiveLevel Level, long ReserveBytes = 1_073_741_824);
public sealed record ArchiveProgress(string Phase, string FileName, long CompletedBytes, long TotalBytes, int CompletedFiles, int TotalFiles)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(100d * CompletedBytes / TotalBytes, 0, 100) : TotalFiles > 0 ? 100d * CompletedFiles / TotalFiles : 0;
    public string DisplayText => $"{Phase}: {FileName} ({CompletedFiles:N0}/{TotalFiles:N0})";
    public bool IsIndeterminate => TotalBytes == 0 && TotalFiles == 0;
}
public sealed record ArchiveNamePreview(string Archive, IReadOnlyList<ArchiveNamePair> Names);
public sealed record ArchiveNamePair(string Utf8, string Cp932);
public sealed record ArchiveConflict(string Archive, string Path, bool IsDirectory, bool TypeMismatch);
public sealed record ArchiveConflictChoice(ArchiveConflictAction Action, bool ApplyToAll = false);
public sealed record ArchiveBatchResult(int Succeeded, int Failed, int Skipped, bool Cancelled, IReadOnlyList<string> Outputs, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public interface IArchiveInteraction
{
    Task<ZipNameEncoding?> ChooseEncodingAsync(ArchiveNamePreview preview, CancellationToken token);
    Task<ArchiveConflictChoice> ResolveConflictAsync(ArchiveConflict conflict, CancellationToken token);
}
public interface IArchiveService
{
    Task<ArchiveBatchResult> CompressAsync(ArchiveCompressionRequest request, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token);
    Task<ArchiveBatchResult> ExtractAsync(string[] archives, string directory, long reserveBytes, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token);
}
public static class ArchiveFormats
{
    private static readonly (string Extension, ArchiveFormat Format)[] formats = [(".tar.gz", ArchiveFormat.TarGZip), (".tar.xz", ArchiveFormat.TarXZ), (".tar.zst", ArchiveFormat.TarZstd), (".zip", ArchiveFormat.Zip), (".7z", ArchiveFormat.SevenZip), (".tar", ArchiveFormat.Tar)];
    public static ArchiveFormat? Detect(string name) => formats.Where(p => name.EndsWith(p.Extension, StringComparison.OrdinalIgnoreCase)).Select(p => (ArchiveFormat?)p.Format).FirstOrDefault();
    public static string Extension(ArchiveFormat format) => formats.Single(p => p.Format == format).Extension;
    public static string Stem(string name) => Detect(name) is { } format ? name[..^Extension(format).Length] : Path.GetFileNameWithoutExtension(name);
    public static bool CanCreate(ArchiveFormat format) => format is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Tar or ArchiveFormat.TarGZip;
    public static bool SupportsLevel(ArchiveFormat format, ArchiveLevel level) => CanCreate(format) && Enum.IsDefined(level) && (format != ArchiveFormat.SevenZip || level != ArchiveLevel.None);
}
