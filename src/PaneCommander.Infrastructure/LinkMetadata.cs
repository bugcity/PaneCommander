using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

internal static class LinkMetadata
{
    public static FileLinkKind Kind(string path)
    {
        if (!OperatingSystem.IsWindows()) return FileLinkKind.OtherReparse;
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8)) return FileLinkKind.OtherReparse;
        return info.Tag switch { 0xA0000003 => FileLinkKind.Junction, 0xA000000C => FileLinkKind.SymbolicLink, _ => FileLinkKind.OtherReparse };
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
}
