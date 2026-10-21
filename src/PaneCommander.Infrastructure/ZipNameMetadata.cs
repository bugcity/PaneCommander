using System.Buffers.Binary;
using System.Text;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed record ZipNameRecord(byte[] RawName, string? UnicodeName, uint Crc, long Size, uint Attributes)
{
    public string Decode(Encoding encoding) => UnicodeName ?? encoding.GetString(RawName);
}
public sealed class ZipNameMetadata
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static readonly Encoding Cp932;
    static ZipNameMetadata() { System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); Cp932 = System.Text.Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
    public IReadOnlyList<ZipNameRecord> Entries { get; }
    public ZipNameEncoding? Encoding { get; }
    public bool NeedsChoice { get; }
    private ZipNameMetadata(List<ZipNameRecord> entries)
    {
        Entries = entries;
        var utf8 = true; var cp932 = true; var differs = false;
        foreach (var entry in entries.Where(e => e.UnicodeName is null))
        {
            string? a = null, b = null;
            try { a = Utf8.GetString(entry.RawName); } catch (DecoderFallbackException) { utf8 = false; }
            try { b = Cp932.GetString(entry.RawName); } catch (DecoderFallbackException) { cp932 = false; }
            if (a is not null && b is not null && a != b) differs = true;
        }
        if (!utf8 && !cp932) throw new InvalidDataException("ZIP全体に適用できるUTF-8/CP932の復号方法がありません。");
        NeedsChoice = utf8 && cp932 && differs;
        Encoding = NeedsChoice ? null : utf8 ? ZipNameEncoding.Utf8 : ZipNameEncoding.Cp932;
    }
    public string[] Names(ZipNameEncoding encoding) => Entries.Select(e => e.Decode(encoding == ZipNameEncoding.Utf8 ? Utf8 : Cp932)).ToArray();
    public ArchiveNamePreview Preview(string archive) => new(archive, Entries.Select(e => new ArchiveNamePair(e.Decode(Utf8), e.Decode(Cp932))).ToArray());
    public static ZipNameMetadata Read(Stream stream, CancellationToken token = default)
    {
        if (!stream.CanSeek || stream.Length < 22) throw new InvalidDataException("ZIP終端がありません。");
        var tail = new byte[(int)Math.Min(stream.Length, 65557)]; stream.Position = stream.Length - tail.Length; stream.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10)) throw new InvalidDataException("分割ZIPまたは不正なZIP終端です。");
        long count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
        var endPosition = stream.Length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = ReadAt(stream, endPosition - 20, 20);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1) throw new InvalidDataException("分割ZIP64は非対応です。");
            var zip64 = ReadAt(stream, L64(locator, 8), 56);
            if (U32(zip64, 0) != 0x06064b50 || L64(zip64, 4) < 44 || U32(zip64, 16) != 0 || U32(zip64, 20) != 0 || L64(zip64, 24) != L64(zip64, 32)) throw new InvalidDataException("ZIP64終端が不正です。");
            count = L64(zip64, 32); size = L64(zip64, 40); offset = L64(zip64, 48);
        }
        if (count < 0 || count > int.MaxValue || offset < 0 || size < 0 || offset > endPosition - size || count > size / 46) throw new InvalidDataException("ZIP中央ディレクトリの範囲が不正です。");
        var entries = new List<ZipNameRecord>(); stream.Position = offset;
        for (long i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var header = new byte[46]; stream.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50 || (U16(header, 8) & 0x41) != 0 || U16(header, 34) != 0) throw new InvalidDataException("暗号化・分割または不正なZIPは展開できません。");
            var raw = new byte[U16(header, 28)]; stream.ReadExactly(raw);
            var extra = new byte[U16(header, 30)]; stream.ReadExactly(extra);
            stream.Seek(U16(header, 32), SeekOrigin.Current);
            string? unicode = (U16(header, 8) & 0x800) != 0 ? Utf8.GetString(raw) : null;
            long unpacked = U32(header, 24);
            for (var p = 0; p + 4 <= extra.Length;)
            {
                var id = U16(extra, p); var length = U16(extra, p + 2); p += 4;
                if (p + length > extra.Length) throw new InvalidDataException("ZIP拡張フィールドの範囲が不正です。");
                if (id == 1 && unpacked == uint.MaxValue) { if (length < 8) throw new InvalidDataException("ZIP64サイズがありません。"); unpacked = L64(extra, p); }
                if (id == 0x7075 && length >= 5 && extra[p] == 1 && U32(extra, p + 1) == Crc32.Compute(raw))
                {
                    try
                    {
                        var decoded = Utf8.GetString(extra, p + 5, length - 5);
                        if (unicode is not null && unicode != decoded) throw new InvalidDataException("ZIPの明示的Unicode名が矛盾しています。");
                        unicode = decoded;
                    }
                    catch (DecoderFallbackException) { /* Invalid Unicode extra is not authoritative. */ }
                }
                p += length;
            }
            if (unpacked < 0 || stream.Position > offset + size) throw new InvalidDataException("ZIPのサイズ情報が不正です。");
            entries.Add(new(raw, unicode, U32(header, 16), unpacked, U32(header, 38)));
        }
        return new(entries);
    }
    private static byte[] ReadAt(Stream stream, long position, int count)
    {
        if (position < 0 || position > stream.Length - count) throw new InvalidDataException("ZIPの位置情報が不正です。");
        stream.Position = position; var buffer = new byte[count]; stream.ReadExactly(buffer); return buffer;
    }
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static long L64(byte[] data, int offset) => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)));
}
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i => { var c = (uint)i; for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    internal static uint Update(uint crc, ReadOnlySpan<byte> data) { foreach (var b in data) crc = Table[(crc ^ b) & 255] ^ (crc >> 8); return crc; }
    internal static uint Compute(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);
}
