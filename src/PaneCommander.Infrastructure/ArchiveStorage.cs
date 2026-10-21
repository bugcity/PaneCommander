using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaneCommander.Infrastructure;

public static class ArchiveTemporaryPaths
{
    private static readonly ConcurrentDictionary<string, byte> paths = new(StringComparer.OrdinalIgnoreCase);
    public static bool Contains(string path) => paths.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    internal static void Add(string path) => paths.TryAdd(path, 0);
    internal static void Remove(string path) => paths.TryRemove(path, out _);
}
internal static class ArchivePaths
{
    internal static string Relative(string name)
    {
        name = name.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        name = name.TrimEnd('/');
        if (string.IsNullOrEmpty(name) || name.StartsWith('/') || Path.IsPathRooted(name)) throw new InvalidDataException("絶対パスまたは空のアーカイブ名です。");
        foreach (var part in name.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new InvalidDataException($"安全でないアーカイブ内パス: {name}");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3])))) throw new InvalidDataException($"予約名は展開できません: {name}");
        }
        return name;
    }
    internal static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("展開先の外への書き込みを拒否しました。");
        return path;
    }
    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static void NoLinks(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(part)))
            if (Attributes(part) is { } attributes && attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"再解析ポイント経由のアーカイブ操作はできません: {part}");
    }
}
internal sealed record ArchiveStamp(uint Volume, ulong Id, long Length, long Modified, FileAttributes Attributes);
internal sealed class ArchiveLease : IDisposable
{
    internal SafeFileHandle Handle { get; }
    internal string PathName { get; private set; }
    internal ArchiveLease(string path, bool delete = false, bool metadata = false, bool data = false, bool shareDelete = false)
    {
        PathName = path;
        Handle = CreateFile(path, 0x80u | (delete ? 0x10000u : 0) | (metadata ? 0x100u : 0) | (data ? 0x80000000u : 0), shareDelete ? 5u : 1u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (Handle.IsInvalid) { Handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), $"対象を保護できません: {path}"); }
        try { if (Stamp.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"リンク経由の操作を拒否しました: {path}"); }
        catch { Handle.Dispose(); throw; }
    }
    internal ArchiveStamp Stamp
    {
        get
        {
            if (!GetFileInformationByHandle(Handle, out var info)) throw new Win32Exception();
            return new(info.Volume, ((ulong)info.IdHigh << 32) | info.IdLow, checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)), ((long)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime, (FileAttributes)info.Attributes);
        }
    }
    internal void Rename(string destination)
    {
        var bytes = Encoding.Unicode.GetBytes(destination + '\0'); var rootOffset = IntPtr.Size == 8 ? 8 : 4; var nameOffset = rootOffset + IntPtr.Size + 4;
        var size = nameOffset + bytes.Length; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size); Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero); Marshal.WriteInt32(buffer, rootOffset + IntPtr.Size, bytes.Length - 2); Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            if (!SetFileInformationByHandle(Handle, 3, buffer, (uint)size)) { var code = Marshal.GetLastWin32Error(); throw new Win32Exception(code, $"反映先が変更されたか使用中です ({code}): {destination}"); }
            PathName = destination;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal void Metadata(DateTime? modified, FileAttributes? attributes)
    {
        var value = new BasicInfo { Write = modified?.ToUniversalTime().ToFileTimeUtc() ?? 0, Attributes = attributes is { } a ? (uint)(a & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory | FileAttributes.Archive)) : 0 };
        if (attributes is not null && value.Attributes == 0) value.Attributes = 0x80;
        if (!SetBasic(Handle, 0, ref value, (uint)Marshal.SizeOf<BasicInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal void Delete()
    {
        var value = 1;
        if (!SetDelete(Handle, 4, ref value, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"一時データを削除できません: {PathName}");
    }
    public void Dispose() => Handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long Creation, Access, Write, Change; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr data, uint length);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] private static extern bool SetBasic(SafeFileHandle handle, int kind, ref BasicInfo data, uint length);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] private static extern bool SetDelete(SafeFileHandle handle, int kind, ref int data, uint length);
}
internal sealed class ArchiveDirectoryGuard : IDisposable
{
    private readonly Dictionary<string, ArchiveLease> held = new(StringComparer.OrdinalIgnoreCase);
    private readonly string writableRoot;
    private readonly bool writable;
    internal ArchiveDirectoryGuard(string root, bool writable = true)
    {
        this.writable = writable;
        writableRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        try { Hold(writableRoot); } catch { Dispose(); throw; }
    }
    internal void Release(string path) { if (held.Remove(path, out var lease)) lease.Dispose(); }
    internal void ReleaseTree(string path)
    {
        foreach (var key in held.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderByDescending(key => key.Length).ToArray()) Release(key);
    }
    internal ArchiveLease Hold(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (held.TryGetValue(path, out var existing)) return existing;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase)) Hold(parent);
        var lease = new ArchiveLease(path, metadata: writable && (path.Equals(writableRoot, StringComparison.OrdinalIgnoreCase) || path.StartsWith(writableRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        if (!lease.Stamp.Attributes.HasFlag(FileAttributes.Directory)) { lease.Dispose(); throw new IOException($"フォルダーではありません: {path}"); }
        held.Add(path, lease); return lease;
    }
    public void Dispose() { foreach (var value in held.Values.Reverse()) value.Dispose(); held.Clear(); }
}
internal static class ArchiveSpace
{
    internal static long Available(string path)
    {
        if (!GetDiskFreeSpaceEx(path, out var available, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "実際の出力ボリュームの空き容量を取得できません。");
        return checked((long)available);
    }
    internal static void Require(string path, long reserve, long bytes, Func<string, long>? available = null)
    {
        if (reserve < 0 || bytes < 0 || (available ?? Available)(path) - reserve < bytes) throw new ArchiveSpaceException("空き容量のしきい値を維持できないため処理を中止しました。");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
public sealed class ArchiveSpaceException(string message) : IOException(message);
internal sealed class ArchiveTransferStream(Stream inner, CancellationToken token, Action<int>? read = null, Action<int>? write = null, bool leaveOpen = true) : Stream
{
    public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); var n = inner.Read(buffer, offset, count); read?.Invoke(n); return n; }
    public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); var n = inner.Read(buffer); read?.Invoke(n); return n; }
    public override void Write(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); write?.Invoke(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { token.ThrowIfCancellationRequested(); write?.Invoke(buffer.Length); inner.Write(buffer); }
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => inner.SetLength(value);
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
}
