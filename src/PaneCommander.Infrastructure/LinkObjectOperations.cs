using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

// No recursion through reparse points, including dangling and cyclic links.
internal static class LinkObjectOperations
{
    internal sealed record Link(uint Tag, bool Directory, byte[] Data, string Target);
    internal static Link Read(string path)
    {
        using var handle = Open(path, 0);
        var buffer = new byte[16384];
        if (!DeviceIoControl(handle, 0x900A8, null, 0, buffer, buffer.Length, out var count, IntPtr.Zero)) Fail("リンク情報の取得", path);
        return Decode(buffer, count, File.GetAttributes(path).HasFlag(FileAttributes.Directory), path);
    }
    internal static Link Decode(byte[] buffer, int count, bool directory, string path)
    {
        if (count < 16) throw new IOException("再解析データが不正です: " + path);
        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag is not (0xA0000003 or 0xA000000C)) throw new IOException($"未対応の再解析ポイント (0x{tag:X8}): {path}");
        var start = tag == 0xA000000C ? 20 : 16;
        var offset = BitConverter.ToUInt16(buffer, 8); var length = BitConverter.ToUInt16(buffer, 10);
        if (start + offset + length > count) throw new IOException("再解析データの範囲が不正です: " + path);
        return new(tag, directory, buffer[..count], Encoding.Unicode.GetString(buffer, start + offset, length));
    }
    internal static bool ContainsLink(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) { _ = Read(path); return true; }
        if (!attributes.HasFlag(FileAttributes.Directory)) return false;
        // Inspect the entire tree, so an unknown tag fails before mutation even after a known link.
        var found = false;
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) found |= ContainsLink(child, token);
        return found;
    }
    internal static void Copy(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureAbsent(destination);
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) { Clone(Read(source), destination); return; }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            // A source replaced by a symlink between inspection and copy must not be dereferenced.
            CopyFileStaged(source,destination,token);
            return;
        }
        if (!CreateDirectory(destination, IntPtr.Zero)) Fail("ディレクトリ作成", destination);
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
        {
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("操作中にディレクトリがリンクへ変更されました: " + source);
            Copy(child, Path.Combine(destination, Path.GetFileName(child)), token);
        }
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }
    private static void CopyFileStaged(string source,string destination,CancellationToken token)
    {
        var staging=Path.Combine(Path.GetDirectoryName(destination)!,".panecommander-copy-"+Guid.NewGuid().ToString("N")+".tmp");
        var mayOwnPartial=false;
        try
        {
            var cancel=false;CopyProgress progress=(_,_,_,_,_,_,_,_,_)=>token.IsCancellationRequested?1u:0u;
            var copied=CopyFileEx(source,staging,Marshal.GetFunctionPointerForDelegate(progress),IntPtr.Zero,ref cancel,0x801);
            var error=Marshal.GetLastWin32Error();GC.KeepAlive(progress);
            mayOwnPartial=copied||error is not (80 or 183); // CREATE_NEW collision never belongs to us.
            if(!copied){token.ThrowIfCancellationRequested();throw Error("ファイルコピー",source+" → "+destination,error);}
            token.ThrowIfCancellationRequested();
            if(!MoveFileEx(staging,destination,0))Fail("コピー先の確定",destination); // never replaces an existing file.
            mayOwnPartial=false;
        }
        catch(Exception failure)
        {
            if(mayOwnPartial)
            {
                try
                {
                    var attrs=File.GetAttributes(staging);
                    if(attrs.HasFlag(FileAttributes.ReadOnly)&&!attrs.HasFlag(FileAttributes.ReparsePoint))File.SetAttributes(staging,attrs&~FileAttributes.ReadOnly);
                    Delete(staging,default);
                }
                catch(FileNotFoundException) { }
                catch(DirectoryNotFoundException) { }
                catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException)
                {throw new IOException(failure.Message+"\n不完全なコピーの後始末にも失敗しました: "+staging+"\n"+cleanup.Message,failure);}
            }
            throw;
        }
    }
    internal static void Delete(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) { _ = Read(path); RemoveLeaf(path, attributes.HasFlag(FileAttributes.Directory)); return; }
        if (!attributes.HasFlag(FileAttributes.Directory)) { RemoveLeaf(path, false); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("操作中にディレクトリがリンクへ変更されました: " + path);
            Delete(child, token);
        }
        RemoveLeaf(path, true); // Never use recursive Directory.Delete on a link-containing tree.
    }
    internal static void Move(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureAbsent(destination);
        if (MoveFileEx(source, destination, 0)) return; // Rename the object; COPY_ALLOWED deliberately absent.
        var error = Marshal.GetLastWin32Error();
        if (error != 17) throw Error("移動", source + " → " + destination, error);
        MoveAcrossVolumes(source, destination, token);
    }
    internal static void MoveAcrossVolumes(string source, string destination, CancellationToken token)
    {
        Copy(source, destination, token); // Failure leaves the source intact; no referent-content fallback.
        Verify(source, destination, token);
        Delete(source, token);
    }
    internal static void Verify(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(source); var other = File.GetAttributes(destination);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (other & (FileAttributes.Directory | FileAttributes.ReparsePoint))) throw new IOException("複製後のオブジェクト種類が一致しません。");
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var a = Read(source); var b = Read(destination);
            if (a.Tag != b.Tag || a.Directory != b.Directory || a.Target != b.Target || (a.Tag == 0xA000000C && BitConverter.ToUInt32(a.Data, 16) != BitConverter.ToUInt32(b.Data, 16))) throw new IOException("複製後のリンク種別・ターゲットが一致しません。");
        }
        else if (attributes.HasFlag(FileAttributes.Directory)) foreach (var child in Directory.EnumerateFileSystemEntries(source)) Verify(child, Path.Combine(destination, Path.GetFileName(child)), token);
        else if (new FileInfo(source).Length != new FileInfo(destination).Length) throw new IOException("複製後のファイルサイズが一致しません。");
    }
    internal static void Clone(Link link, string destination)
    {
        if (link.Tag is not (0xA0000003 or 0xA000000C)) throw new IOException($"未対応の再解析ポイント (0x{link.Tag:X8})");
        if (link.Tag == 0xA000000C)
        {
            var target = link.Target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + link.Target[8..] : link.Target.StartsWith(@"\??\", StringComparison.Ordinal) ? link.Target[4..] : link.Target;
            if (!CreateSymbolicLink(destination, target, (link.Directory ? 1u : 0u) | 2u)) Fail("シンボリックリンクの複製（権限・開発者モード・送り先FSを確認してください）", destination);
        }
        else
        {
            // CREATE_NEW semantics: never attach reparse data to an existing directory.
            if (!CreateDirectory(destination, IntPtr.Zero)) Fail("ジャンクション用ディレクトリ作成", destination);
            try
            {
                using var handle = Open(destination, 0x40000000);
                if (!DeviceIoControl(handle, 0x900A4, link.Data, link.Data.Length, null, 0, out _, IntPtr.Zero)) Fail("ジャンクションの複製", destination);
            }
            catch { RemoveDirectory(destination); throw; }
        }
        var copied = Read(destination);
        if (copied.Tag != link.Tag || copied.Directory != link.Directory || copied.Target != link.Target) throw new IOException("複製後のリンク検証に失敗しました: " + destination);
    }
    internal static void EnsureAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("送り先に同名のオブジェクトがあります。リンクを含む操作では別の名前を指定してください: " + path);
    }
    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw Error("再解析ポイントを開く", path, error); }
        return handle;
    }
    private static void RemoveLeaf(string path, bool directory) { if (!(directory ? RemoveDirectory(path) : DeleteFile(path))) Fail("オブジェクト削除", path); }
    private static IOException Error(string action, string path, int error) => new($"{action}: {path}\nWin32 {error}: {new Win32Exception(error).Message}", new Win32Exception(error));
    private static void Fail(string action, string path) => throw Error(action, path, Marshal.GetLastWin32Error());
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, byte[]? output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLink(string path, string target, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveDirectory(string path);
    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteFile(string path);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CopyFileEx(string source, string destination, IntPtr progress, IntPtr data, [MarshalAs(UnmanagedType.Bool)] ref bool cancel, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CopyProgress(long total,long copied,long streamTotal,long streamCopied,uint stream,uint reason,IntPtr source,IntPtr destination,IntPtr data);
}
