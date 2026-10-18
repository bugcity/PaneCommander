using System.Text;

namespace PaneCommander.Infrastructure;

public static class TextPreviewReader
{
    public const int MaximumBytes = 1024 * 1024;

    public static async Task<string> ReadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes) throw new NotSupportedException("プレビューは1 MiB以下のテキストに対応します。");
        // Also bound reads if a file grows after opening it.
        var bytes = new byte[MaximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumBytes) throw new NotSupportedException("プレビューは1 MiB以下のテキストに対応します。");
        token.ThrowIfCancellationRequested();
        return Decode(bytes.AsSpan(0, count));
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        string text;
        try { text = encoding.GetString(bytes[skip..]); }
        catch (DecoderFallbackException ex) { throw new NotSupportedException("UTF-8、またはBOM付きUTF-16/UTF-32のテキストのみ表示できます。", ex); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f')))
            throw new NotSupportedException("バイナリまたは未対応の制御文字を含むため、テキストとして表示できません。");
        return text;
    }
}
