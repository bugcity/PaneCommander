using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PaneCommander.Infrastructure;

/// <summary>A short-lived, STA native Shell menu. No COM objects survive its using scope.</summary>
[SupportedOSPlatform("windows")]
public sealed class ShellContextMenu : IDisposable
{
    public const uint CompressCommand = 0x8001, ExtractCommand = 0x8002;
    public void AddArchiveCommands(bool compress, bool extract)
    {
        EnsureThread();
        if (!AppendMenu(menu, 0x800, 0, null) || !AppendMenu(menu, compress ? 0u : 1u, CompressCommand, "圧縮（PaneCommander）") || !AppendMenu(menu, extract ? 0u : 1u, ExtractCommand, "展開（PaneCommander）")) throw new System.ComponentModel.Win32Exception();
    }
    private object? folderObject, menuObject;
    private HostedShellBackground? background;
    private readonly List<IntPtr> pidls = [];
    private IntPtr menu;
    private bool oleInitialized;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly IntPtr owner;
    private IContextMenu Context => (IContextMenu)menuObject!;
    public ShellContextMenu(string directory, string[] paths, IntPtr owner)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("ShellメニューはSTAスレッドで開いてください。");
        this.owner = owner;
        try
        {
            Check(OleInitialize(IntPtr.Zero)); oleInitialized = true;
            FileSystemService.ValidatePath(directory);
            Check(SHParseDisplayName(directory, IntPtr.Zero, out var folderPidl, 0, out _)); pidls.Add(folderPidl);
            Check(SHGetDesktopFolder(out var desktop));
            try { var iid = typeof(IShellFolder).GUID; desktop.BindToObject(folderPidl, IntPtr.Zero, ref iid, out folderObject); }
            finally { Marshal.ReleaseComObject(desktop); }
            var folder = (IShellFolder)folderObject!; var contextId = typeof(IContextMenu).GUID;
            if (paths.Length == 0) { background=new HostedShellBackground();menuObject=background.CreateContext(directory,owner); }
            else
            {
                var children = new IntPtr[paths.Length];
                for (var i = 0; i < paths.Length; i++)
                {
                    if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(paths[i]))!), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("選択対象は同じディレクトリ内である必要があります。");
                    Check(SHParseDisplayName(paths[i], IntPtr.Zero, out var absolute, 0, out _)); pidls.Add(absolute); children[i] = ILFindLastID(absolute);
                }
                folder.GetUIObjectOf(owner, (uint)children.Length, children, ref contextId, IntPtr.Zero, out menuObject);
            }
            menu = CreatePopupMenu(); if (menu == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Check(Context.QueryContextMenu(menu, 0, 1, 0x7fff, 0x10)); // CMF_CANRENAME
            if(paths.Length==0)ShellExtensionLifetime.AfterBackgroundQuery();
        }
        catch { Dispose(); throw; }
    }
    public string Verb(uint command)
    {
        var buffer = Marshal.AllocHGlobal(2048);
        try { Marshal.WriteInt16(buffer, 0); return Context.GetCommandString((UIntPtr)(command - 1), 4, IntPtr.Zero, buffer, 1024) >= 0 ? Marshal.PtrToStringUni(buffer) ?? "" : ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public IReadOnlyList<(uint Id, string Text, string Verb)> Commands()
    {
        var result = new List<(uint, string, string)>();
        void Walk(IntPtr current)
        {
            for (var i = 0; i < GetMenuItemCount(current); i++)
            {
                var child = GetSubMenu(current, i); if (child != IntPtr.Zero) Walk(child);
                var id = GetMenuItemID(current, i); if (id == 0 || id > 0x7fff) continue;
                var text = new StringBuilder(512); GetMenuString(current, (uint)i, text, text.Capacity, 0x400); result.Add((id, text.ToString(), Verb(id)));
            }
        }
        Walk(menu); return result;
    }
    public void Restrict(Func<string, bool> allowed)
    {
        void Walk(IntPtr current) { for (var i = 0; i < GetMenuItemCount(current); i++) { var child = GetSubMenu(current, i); if (child != IntPtr.Zero) Walk(child); var id = GetMenuItemID(current, i); if (id > 0 && id <= 0x7fff && !allowed(Verb(id))) EnableMenuItem(current, (uint)i, 0x403); } }
        Walk(menu);
    }
    public uint Track(int x, int y) { EnsureThread(); return TrackPopupMenuEx(menu, 0x102, x, y, owner, IntPtr.Zero); }
    public bool IsEnabled(uint command) { EnsureThread(); return (GetMenuState(menu,command,0)&3)==0; }
    public bool Forward(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message is not (0x117 or 0x2b or 0x2c or 0x120)) return false;
        if (menuObject is IContextMenu3 third && third.HandleMenuMsg2(message, wParam, lParam, out result) == 0) return true;
        return menuObject is IContextMenu2 second && second.HandleMenuMsg(message, wParam, lParam) == 0;
    }
    public void Invoke(uint command, int x, int y, bool waitForCompletion = false)
    {
        EnsureThread(); var offset = (IntPtr)(command - 1);
        var info = new InvokeInfo { Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x20004000u | (waitForCompletion ? 0x100u : 0), Owner = owner, Verb = offset, VerbW = offset, Show = 1, Point = new NativePoint { X = x, Y = y } }; // CMIC_MASK_NOASYNC for native Paste
        Check(Context.InvokeCommand(ref info));
    }
    private void EnsureThread() { ObjectDisposedException.ThrowIf(menuObject is null, this); if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Shell COMオブジェクトのスレッドが変わりました。"); }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    public void Dispose()
    {
        try { if (menu != IntPtr.Zero) { DestroyMenu(menu); menu = IntPtr.Zero; } }
        finally
        {
            try { if (menuObject is not null) { var value=menuObject;menuObject=null;Marshal.ReleaseComObject(value); } }
            finally
            {
                try { try { background?.Dispose(); } finally { background=null;if (folderObject is not null) {var value=folderObject;folderObject=null;Marshal.ReleaseComObject(value);} } }
                finally { try { foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);pidls.Clear(); } finally {if (oleInitialized) {OleUninitialize();oleInitialized=false;}} }
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct InvokeInfo { public int Size; public uint Mask; public IntPtr Owner, Verb, Parameters, Directory; public int Show; public uint HotKey; public IntPtr Icon, Title, VerbW, ParametersW, DirectoryW, TitleW; public NativePoint Point; }
    [ComImport, Guid("000214e4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu { [PreserveSig] int QueryContextMenu(IntPtr menu,uint index,uint first,uint last,uint flags); [PreserveSig] int InvokeCommand(ref InvokeInfo info); [PreserveSig] int GetCommandString(UIntPtr id,uint flags,IntPtr reserved,IntPtr text,uint max); }
    [ComImport, Guid("000214f4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 { [PreserveSig] int QueryContextMenu(IntPtr m,uint i,uint f,uint l,uint flags); [PreserveSig] int InvokeCommand(ref InvokeInfo info); [PreserveSig] int GetCommandString(UIntPtr id,uint flags,IntPtr r,IntPtr s,uint max); [PreserveSig] int HandleMenuMsg(uint message,IntPtr w,IntPtr l); }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3 { [PreserveSig] int QueryContextMenu(IntPtr m,uint i,uint f,uint l,uint flags); [PreserveSig] int InvokeCommand(ref InvokeInfo info); [PreserveSig] int GetCommandString(UIntPtr id,uint flags,IntPtr r,IntPtr s,uint max); [PreserveSig] int HandleMenuMsg(uint message,IntPtr w,IntPtr l); [PreserveSig] int HandleMenuMsg2(uint message,IntPtr w,IntPtr l,out IntPtr result); }
    [ComImport, Guid("000214e6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd,IntPtr bind,[MarshalAs(UnmanagedType.LPWStr)]string name,ref uint eaten,out IntPtr pidl,ref uint attributes);
        void EnumObjects(IntPtr hwnd,uint flags,out IntPtr list);
        void BindToObject(IntPtr pidl,IntPtr bind,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);
        void BindToStorage(IntPtr pidl,IntPtr bind,ref Guid iid,out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr flags,IntPtr first,IntPtr second);
        void CreateViewObject(IntPtr hwnd,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);
        void GetAttributesOf(uint count,IntPtr children,ref uint flags);
        void GetUIObjectOf(IntPtr hwnd,uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)]IntPtr[] children,ref Guid iid,IntPtr reserved,[MarshalAs(UnmanagedType.Interface)]out object result);
        void GetDisplayNameOf(IntPtr pidl,uint flags,IntPtr name);
        void SetNameOf(IntPtr hwnd,IntPtr pidl,[MarshalAs(UnmanagedType.LPWStr)]string name,uint flags,out IntPtr result);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint mask,out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr hwnd,IntPtr parameters);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu,int index);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu,uint id,StringBuilder text,int max,uint flags);
    [DllImport("user32.dll")] private static extern uint EnableMenuItem(IntPtr menu,uint id,uint flags);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu,uint id,uint flags);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
