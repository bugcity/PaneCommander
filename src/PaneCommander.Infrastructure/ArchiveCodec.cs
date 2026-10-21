using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using PaneCommander.Core;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Xz;
using SharpCompress.Providers;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;
using ZipArchive = System.IO.Compression.ZipArchive;

namespace PaneCommander.Infrastructure;

internal sealed record ArchiveItem(string Name, bool Directory, long Size, DateTime? Modified, FileAttributes Attributes, string? Source = null, bool Recorded = true);
internal sealed class ArchiveCodec
{
    private const int BufferSize = 65536;
    internal static List<ArchiveItem> Inspect(string path, ArchiveFormat format, ZipNameMetadata? zip, string[]? names, CancellationToken token)
    {
        var entries = new List<ArchiveItem>();
        Walk(path, format, zip, names, (entry, _) => entries.Add(entry), null, token);
        return entries;
    }
    internal static void Extract(string path, ArchiveFormat format, ZipNameMetadata? zip, string[]? names, string stage, Action<ArchiveItem, int> transferred, Action<int> space, CancellationToken token)
    {
        using var guard = new ArchiveDirectoryGuard(stage);
        void EnsureDirectory(string directory)
        {
            if (directory.Equals(stage, StringComparison.OrdinalIgnoreCase)) return;
            EnsureDirectory(Path.GetDirectoryName(directory)!);
            if (ArchivePaths.Attributes(directory) is null) ArchiveWorkspace.CreateNewDirectory(directory);
            guard.Hold(directory);
        }
        Walk(path, format, zip, names, (entry, copy) =>
        {
            var destination = ArchivePaths.Under(stage, entry.Name);
            if (entry.Directory) EnsureDirectory(destination);
            else
            {
                EnsureDirectory(Path.GetDirectoryName(destination)!);
                copy!(destination);
                if (new FileInfo(destination).Length != entry.Size) throw new InvalidDataException($"展開サイズが一致しません: {entry.Name}");
            }
            transferred(entry, 0);
        }, (entry, count) => { space(count); transferred(entry, count); }, token);
    }
    private static void Walk(string path, ArchiveFormat format, ZipNameMetadata? zip, string[]? names, Action<ArchiveItem, Action<string>?> visit, Action<ArchiveItem, int>? transfer, CancellationToken token)
    {
        using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var source = new ArchiveTransferStream(raw, token);
        if (format == ArchiveFormat.Zip)
        {
            if (zip is null || names is null) throw new InvalidOperationException("ZIP名を決定してください。");
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, true, Encoding.Latin1);
            if (archive.Entries.Count != zip.Entries.Count) throw new InvalidDataException("ZIPエントリ一覧が一致しません。");
            for (var i = 0; i < names.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.Entries[i]; var metadata = zip.Entries[i];
                RejectLink(metadata.Attributes);
                var directory = names[i].EndsWith('/') || names[i].EndsWith('\\') || (metadata.Attributes & 0x10) != 0 || ((metadata.Attributes >> 16) & 0xf000) == 0x4000;
                var item = new ArchiveItem(ArchivePaths.Relative(names[i]), directory, metadata.Size, entry.LastWriteTime.UtcDateTime, (FileAttributes)(metadata.Attributes & 0x16));
                visit(item, destination =>
                {
                    using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    var buffer = new byte[BufferSize]; uint crc = uint.MaxValue; long bytes = 0; int n;
                    while ((n = input.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested(); bytes = checked(bytes + n);
                        if (bytes > item.Size) throw new InvalidDataException("ZIPの申告サイズを超えました。");
                        transfer?.Invoke(item, n); crc = Crc32.Update(crc, buffer.AsSpan(0, n)); output.Write(buffer, 0, n);
                    }
                    if (~crc != metadata.Crc || bytes != item.Size) throw new InvalidDataException($"ZIPのCRC/サイズが一致しません: {item.Name}");
                });
            }
        }
        else if (format == ArchiveFormat.SevenZip)
        {
            ArchiveItem? current = null; long last = 0;
            var options = new ReaderOptions { LeaveStreamOpen = true, BufferSize = BufferSize, Progress = new InlineProgress<ProgressReport>(p =>
            {
                token.ThrowIfCancellationRequested();
                if (current is not null) { var delta = p.BytesTransferred - last; last = p.BytesTransferred; if (delta > 0) transfer?.Invoke(current, checked((int)delta)); }
            }) };
            using var archive = ArchiveFactory.OpenArchive(source, options);
            if (archive.Type != ArchiveType.SevenZip || archive.Volumes.Count() != 1) throw new InvalidDataException("単一の7zアーカイブではありません。");
            var list = archive.Entries.ToArray();
            foreach (var entry in list) ValidateSevenZip(entry);
            if (transfer is null)
            {
                foreach (var entry in list) { token.ThrowIfCancellationRequested(); visit(SevenZipItem(entry), null); }
            }
            else
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    token.ThrowIfCancellationRequested(); ValidateSevenZip(reader.Entry);
                    current = SevenZipItem(reader.Entry); last = 0;
                    visit(current, destination => reader.WriteEntryToFile(destination, new ExtractionOptions { Overwrite = false, CheckCrc = true, BufferSize = BufferSize, PreserveFileTime = false, PreserveAttributes = false }));
                }
            }
        }
        else
        {
            using var decoded = DecodeTar(source, format);
            using var reader = new TarReader(decoded, leaveOpen: true);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw new InvalidDataException($"リンクまたは特殊TARエントリは展開できません: {entry.Name}");
                if (entry.Name is "." or "./") continue;
                var attributes = entry is PaxTarEntry pax && pax.ExtendedAttributes.TryGetValue("PaneCommander.WindowsAttributes", out var attr) && int.TryParse(attr, out var parsed) ? (FileAttributes)(parsed & 6) : 0;
                var item = new ArchiveItem(ArchivePaths.Relative(entry.Name), entry.EntryType == TarEntryType.Directory, entry.Length, entry.ModificationTime.UtcDateTime, attributes);
                visit(item, destination =>
                {
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    if (entry.DataStream is { } data) Copy(data, output, n => transfer?.Invoke(item, n), token);
                });
            }
            // Consume the compression footer as well, so wrapper checksum failures precede commit.
            Copy(decoded, Stream.Null, _ => { }, token);
        }
    }
    private static Stream DecodeTar(Stream source, ArchiveFormat format) => format switch
    {
        ArchiveFormat.Tar => new ArchiveTransferStream(source, CancellationToken.None),
        ArchiveFormat.TarGZip => new GZipStream(source, CompressionMode.Decompress, true),
        ArchiveFormat.TarXZ => new XZStream(source),
        ArchiveFormat.TarZstd => CompressionProviderRegistry.Default.GetProvider(CompressionType.ZStandard)!.CreateDecompressStream(source),
        _ => throw new NotSupportedException("未対応形式です。")
    };
    private static ArchiveItem SevenZipItem(IEntry entry) => new(ArchivePaths.Relative(entry.Key ?? ""), entry.IsDirectory, entry.Size, entry.LastModifiedTime, (FileAttributes)((entry.Attrib ?? 0) & 0x16));
    private static void ValidateSevenZip(IEntry entry)
    {
        // SharpCompress's SevenZipFilePart.Index is a file index, not a volume index.
        // The archive's actual volume count is checked when opening it.
        if (entry.IsEncrypted || entry.IsSplitAfter || !string.IsNullOrEmpty(entry.LinkTarget)) throw new InvalidDataException("暗号化・分割・リンクを含む7zは展開できません。");
        RejectLink(unchecked((uint)(entry.Attrib ?? 0)));
        if (entry is SharpCompress.Common.SevenZip.SevenZipEntry seven && seven.ExtendedAttrib is { } extended) RejectLink(unchecked((uint)extended) << 16);
        if (entry.Size < 0) throw new InvalidDataException("負の展開サイズです。");
    }
    private static void RejectLink(uint attributes)
    {
        var unix = (attributes >> 16) & 0xf000;
        if ((attributes & 0x400) != 0 || unix is not (0 or 0x4000 or 0x8000)) throw new InvalidDataException("シンボリックリンクまたは特殊ファイルを含むアーカイブです。");
    }
    internal static void Create(string destination, ArchiveFormat format, ArchiveLevel level, IReadOnlyList<ArchiveItem> entries, Action<ArchiveItem, int> progress, Action<int> space, CancellationToken token)
    {
        if (!ArchiveFormats.SupportsLevel(format, level)) throw new NotSupportedException("選択形式ではこの圧縮レベルを使用できません。");
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize);
        using var output = new ArchiveTransferStream(file, token, write: space);
        var compression = level switch { ArchiveLevel.None => CompressionLevel.NoCompression, ArchiveLevel.Fast => CompressionLevel.Fastest, ArchiveLevel.Maximum => CompressionLevel.SmallestSize, _ => CompressionLevel.Optimal };
        if (format == ArchiveFormat.Zip)
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, true, new UTF8Encoding(false));
            foreach (var item in entries)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(item.Name + (item.Directory ? "/" : ""), compression);
                entry.ExternalAttributes = (int)item.Attributes;
                if (item.Modified is { } utc)
                {
                    var date = utc.ToLocalTime();
                    entry.LastWriteTime = new DateTimeOffset(date < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : date > new DateTime(2107, 12, 31) ? new DateTime(2107, 12, 31) : date);
                }
                if (!item.Directory) { using var input = Source(item); using var data = entry.Open(); Copy(input, data, n => progress(item, n), token); }
                progress(item, 0);
            }
        }
        else if (format == ArchiveFormat.SevenZip)
        {
            var dictionary = level switch { ArchiveLevel.Fast => 1 << 20, ArchiveLevel.Maximum => 8 << 20, _ => 4 << 20 };
            using var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2) { LzmaProperties = new LzmaEncoderProperties(false, dictionary, level == ArchiveLevel.Fast ? 16 : level == ArchiveLevel.Maximum ? 64 : 32), LeaveStreamOpen = true, CompressHeader = true, BufferSize = BufferSize });
            foreach (var item in entries)
            {
                token.ThrowIfCancellationRequested();
                if (item.Directory) writer.WriteDirectory(item.Name, item.Modified);
                else { using var input = Source(item); using var metered = new ArchiveTransferStream(input, token, read: n => progress(item, n)); writer.Write(item.Name, metered, item.Modified); }
                progress(item, 0);
            }
        }
        else
        {
            using var wrapped = format == ArchiveFormat.TarGZip ? (Stream)new GZipStream(output, compression, true) : new ArchiveTransferStream(output, token);
            using var writer = new TarWriter(wrapped, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var item in entries)
            {
                token.ThrowIfCancellationRequested();
                var entry = new PaxTarEntry(item.Directory ? TarEntryType.Directory : TarEntryType.RegularFile, item.Name,
                    new Dictionary<string, string> { ["PaneCommander.WindowsAttributes"] = ((int)(item.Attributes & (FileAttributes.Hidden | FileAttributes.System))).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                if (item.Modified is { } modified) entry.ModificationTime = new DateTimeOffset(modified.ToUniversalTime());
                if (item.Directory) writer.WriteEntry(entry);
                else { using var input = Source(item); using var metered = new ArchiveTransferStream(input, token, read: n => progress(item, n)); entry.DataStream = metered; writer.WriteEntry(entry); }
                progress(item, 0);
            }
        }
    }
    private static Stream Source(ArchiveItem item)
    {
        ArchivePaths.NoLinks(item.Source!);
        var lease = new ArchiveLease(item.Source!, data: true);
        try { return new FileStream(lease.Handle, FileAccess.Read, BufferSize); }
        catch { lease.Dispose(); throw; }
    }
    private static void Copy(Stream source, Stream destination, Action<int> progress, CancellationToken token)
    {
        var buffer = new byte[BufferSize]; int n;
        while ((n = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); progress(n); destination.Write(buffer, 0, n); }
    }
}
internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
