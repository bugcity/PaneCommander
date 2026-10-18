using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

// Diagnostic child process only. Never switches the interactive desktop or writes WinSta0's clipboard.
internal static class ClipboardIsolation
{
    private static string expectedStation = "";
    private static IntPtr owner;
    internal static int Run(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var result=new Dictionary<string,object?> { ["InteractiveClipboardWritten"]=false,["InteractiveDesktopSwitched"]=false };var root=Path.Combine(output,"fixture");Directory.CreateDirectory(root);
        IntPtr station=IntPtr.Zero,desktop=IntPtr.Zero;bool ole=false;
        try
        {
            result["InitialStation"]=Name(GetProcessWindowStation());
            result["InitialClipboardSequenceReadOnly"]=GetClipboardSequenceNumber();
            // Named stations require administrator membership. Ask Windows for a fresh automatic name.
            // CWF_CREATE_ONLY refuses an existing station, so no prior clipboard can be reused.
            station=CreateWindowStation(null,1,0x37f,IntPtr.Zero);CheckHandle(station);
            expectedStation=Name(station);
            if(expectedStation.Equals("WinSta0",StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected interactive station");
            Check(SetProcessWindowStation(station));
            desktop=CreateDesktop("Default",IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);CheckHandle(desktop);
            Check(SetThreadDesktop(desktop));Guard();
            result["Station"]=Name(GetProcessWindowStation());result["Desktop"]=Name(GetThreadDesktop(GetCurrentThreadId()));
            result["InteractiveClipboardWritten"]=false;result["InteractiveDesktopSwitched"]=false;
            result["IsolationBoundary"]="New noninteractive window station, not merely a desktop";
            owner=CreateWindowEx(0,"STATIC","PaneCommander isolated clipboard owner",0,0,0,1,1,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);CheckHandle(owner);
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));ole=true;
            var file=Path.Combine(root,"clipboard.txt");File.WriteAllText(file,"isolated clipboard fixture");
            var known=new Dictionary<uint,byte[]> { [13]=Encoding.Unicode.GetBytes("PaneCommander synthetic original\0"),
                [RegisterClipboardFormat("PaneCommander.Isolation.Binary")]=[0,1,127,128,255,0,42] };
            if(known.ContainsKey(0))throw new IOException("Cannot register isolated format");
            Seed(known);
            IntPtr previous=IntPtr.Zero;
            var get=OleGetClipboard(out previous);result["PreviousProxyGetHr"]=$"0x{get:X8}";
            if(get>=0)
            {
                try
                {
                    Copy(file,"copy",root);
                    Guard();var restore=OleSetClipboard(previous);result["PreviousProxyRestoreHr"]=$"0x{restore:X8}";
                    result["PreviousProxyRestoredKnownBytes"]=restore>=0&&Matches(known);
                }
                finally {if(previous!=IntPtr.Zero)Marshal.Release(previous);}
            }
            var cases=new List<object>();
            foreach(var verb in new[]{"copy","cut"})
            {
                Seed(known);var snapshot=known.Keys.ToDictionary(k=>k,ReadBytes);
                Copy(file,verb,root);
                var files=ReadDropFiles();var effect=ReadBytes(RegisterClipboardFormat("Preferred DropEffect"));
                var valid=files.SequenceEqual(new[]{file})&&effect.Length>=4&&BitConverter.ToUInt32(effect)==(verb=="copy"?1u:2u);
                result["BackgroundWith"+verb]=Background(root);
                Seed(snapshot);var restored=Matches(snapshot);
                cases.Add(new{Verb=verb,FileDropValid=valid,KnownFormatsRestored=restored,Formats=snapshot.Keys,FileUnchanged=File.ReadAllText(file)=="isolated clipboard fixture"});
                if(!valid||!restored)throw new IOException("Isolated file clipboard or synthetic-format round trip failed");
            }
            result["Cases"]=cases;result["KnownFormatRoundTripPassed"]=true;
            result["ArbitraryUserFormatsRestorable"]=false;result["RealGuiVerified"]=false;
            result["Success"]=true;return 0;
        }
        catch(Exception ex){result["Success"]=false;result["Error"]=ex.ToString();return 1;}
        finally
        {
            if(ole)OleUninitialize();if(owner!=IntPtr.Zero)DestroyWindow(owner);
            // Remain on the isolated station until process exit; Shell/OLE may own hidden windows.
            // Owned station/desktop handles and remaining extension resources end with this child process.
            result["Lifetime"]="Owned child process exits on isolated station; no return to interactive clipboard";
            File.WriteAllText(Path.Combine(output,"clipboard-isolation.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    private static void Copy(string file,string verb,string directory)
    {
        Guard();using var menu=new ShellContextMenu(directory,[file],owner);
        var command=menu.Commands().Single(c=>c.Verb==verb);menu.Invoke(command.Id,0,0);
    }
    internal static int ReadOnlyBackground(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var root=Path.Combine(output,"empty-fixture");Directory.CreateDirectory(root);
        var result=new Dictionary<string,object?> { ["ClipboardWritten"]=false,["GuiDisplayed"]=false,["Station"]=Name(GetProcessWindowStation()),
            ["FileDropFormatAdvertised"]=IsClipboardFormatAvailable(15),["ClipboardSequenceBefore"]=GetClipboardSequenceNumber() };
        try{result["Menus"]=Background(root,false);result["Success"]=true;return 0;}
        catch(Exception ex){result["Error"]=ex.ToString();result["Success"]=false;return 1;}
        finally{result["ClipboardSequenceAfter"]=GetClipboardSequenceNumber();File.WriteAllText(Path.Combine(output,"background-readonly.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}
    }
    private static object Background(string directory,bool requireIsolation=true)
    {
        if(requireIsolation)Guard();using var direct=new ShellContextMenu(directory,[],owner);
        var result=new Dictionary<string,object?> { ["DirectProduct"]=direct.Commands().Select(c=>new{c.Id,c.Text,c.Verb}).ToArray() };
        // Compare documented IShellView/GetItemObject(SVGIO_BACKGROUND) without constructing a host site.
        var rcw=typeof(ShellContextMenu).GetField("folderObject",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(direct)!;
        IntPtr unknown=Marshal.GetIUnknownForObject(rcw),folder=IntPtr.Zero;
        try
        {
            var iid=new Guid("000214e6-0000-0000-C000-000000000046");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,in iid,out folder));
            result["QueriesUseFreshObjects"]=true;
            IntPtr absolute=IntPtr.Zero,defaultContext=IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(directory,IntPtr.Zero,out absolute,0,out _));
                var definition=new DefaultContext{Owner=owner,FolderPidl=absolute,Folder=folder};
                var contextIid=new Guid("000214e4-0000-0000-C000-000000000046");
                var hr=SHCreateDefaultContextMenu(ref definition,ref contextIid,out defaultContext);
                result["DefaultContextHr"]=$"0x{hr:X8}";
                if(hr>=0)result["DefaultContext"]=QueryCommands(defaultContext,0x10);
            }
            finally{if(defaultContext!=IntPtr.Zero)Marshal.Release(defaultContext);if(absolute!=IntPtr.Zero)Marshal.FreeCoTaskMem(absolute);}
            foreach(var flags in new uint[]{0,0x10,0x100})
            {
                result[$"DirectFlags{flags:X}"]=FreshContext(folder,flags,false);
                result[$"ShellViewFlags{flags:X}"]=FreshContext(folder,flags,true);
            }
            return result;
        }
        finally{if(folder!=IntPtr.Zero)Marshal.Release(folder);Marshal.Release(unknown);}
    }
    private static object FreshContext(IntPtr folder,uint flags,bool throughView)
    {
        IntPtr view=IntPtr.Zero,context=IntPtr.Zero;var result=new Dictionary<string,object?>();
        try
        {
            var iid=new Guid(throughView?"000214e3-0000-0000-C000-000000000046":"000214e4-0000-0000-C000-000000000046");
            var hr=Method<CreateView>(folder,8)(folder,owner,ref iid,out var created);result["CreateHr"]=$"0x{hr:X8}";
            if(throughView)view=created;else context=created;
            if(hr<0)return result;
            if(throughView){iid=new("000214e4-0000-0000-C000-000000000046");hr=Method<GetItem>(view,15)(view,0,ref iid,out context);result["GetBackgroundObjectHr"]=$"0x{hr:X8}";if(hr<0)return result;}
            result["Query"]=QueryCommands(context,flags);return result;
        }
        finally{if(context!=IntPtr.Zero)Marshal.Release(context);if(view!=IntPtr.Zero)Marshal.Release(view);}
    }
    private static object QueryCommands(IntPtr context,uint flags)
    {
        var menu=CreatePopupMenu();CheckHandle(menu);
        try
        {
            var hr=Method<Query>(context,3)(context,menu,0,1,0x7fff,flags);var commands=new List<object>();
            for(var i=0;i<GetMenuItemCount(menu);i++)
            {
                var id=GetMenuItemID(menu,i);if(id==0||id>0x7fff)continue;
                var text=new StringBuilder(512);GetMenuString(menu,(uint)i,text,512,0x400);
                var buffer=Marshal.AllocHGlobal(2048);
                try{Marshal.WriteInt16(buffer,0);var verbHr=Method<GetString>(context,5)(context,(UIntPtr)(id-1),4,IntPtr.Zero,buffer,1024);commands.Add(new{Id=id,Text=text.ToString(),Verb=verbHr>=0?Marshal.PtrToStringUni(buffer):null,State=GetMenuState(menu,id,0)});}
                finally{Marshal.FreeHGlobal(buffer);}
            }
            return new{QueryHr=$"0x{hr:X8}",Commands=commands};
        }
        finally{DestroyMenu(menu);}
    }
    private static T Method<T>(IntPtr p,int slot)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));
    [StructLayout(LayoutKind.Sequential)]private struct DefaultContext{public IntPtr Owner,Callback,FolderPidl,Folder;public uint Count;public IntPtr Children,Association;public uint Keys;public IntPtr KeyArray;}
    [DllImport("shell32.dll")]private static extern int SHCreateDefaultContextMenu(ref DefaultContext context,ref Guid iid,out IntPtr result);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint mask,out uint attributes);
    private static void Guard(){if(expectedStation.Length==0||Name(GetProcessWindowStation())!=expectedStation||expectedStation=="WinSta0")throw new IOException("Clipboard isolation not verified; refusing write");}
    private static string Name(IntPtr p){var s=new StringBuilder(256);Check(GetUserObjectInformation(p,2,s,512,out _));return s.ToString();}
    private static bool Matches(Dictionary<uint,byte[]> data)=>data.All(p=>{var actual=ReadBytes(p.Key);return actual.Length>=p.Value.Length&&actual.Take(p.Value.Length).SequenceEqual(p.Value)&&actual.Skip(p.Value.Length).All(b=>b==0);});
    private static void Seed(Dictionary<uint,byte[]> data)
    {
        Guard();Check(OpenClipboard(owner));
        try
        {
            Check(EmptyClipboard());
            foreach(var pair in data)
            {
                var memory=GlobalAlloc(0x42,(UIntPtr)pair.Value.Length);CheckHandle(memory);
                try{var pointer=GlobalLock(memory);CheckHandle(pointer);try{Marshal.Copy(pair.Value,0,pointer,pair.Value.Length);}finally{GlobalUnlock(memory);}CheckHandle(SetClipboardData(pair.Key,memory));memory=IntPtr.Zero;}
                finally{if(memory!=IntPtr.Zero)GlobalFree(memory);}
            }
        }
        finally{Check(CloseClipboard());}
    }
    private static byte[] ReadBytes(uint format)
    {
        Guard();Check(OpenClipboard(owner));
        try{var memory=GetClipboardData(format);CheckHandle(memory);var size=checked((int)GlobalSize(memory));var data=new byte[size];var pointer=GlobalLock(memory);CheckHandle(pointer);try{Marshal.Copy(pointer,data,0,size);return data;}finally{GlobalUnlock(memory);}}
        finally{Check(CloseClipboard());}
    }
    private static string[] ReadDropFiles()
    {
        Guard();Check(OpenClipboard(owner));
        try{var h=GetClipboardData(15);CheckHandle(h);var count=DragQueryFile(h,uint.MaxValue,null,0);return Enumerable.Range(0,checked((int)count)).Select(i=>{var s=new StringBuilder(32768);DragQueryFile(h,(uint)i,s,s.Capacity);return s.ToString();}).ToArray();}
        finally{Check(CloseClipboard());}
    }
    private static void Check(bool value){if(!value)throw new Win32Exception(Marshal.GetLastWin32Error());}
    private static void CheckHandle(IntPtr value){if(value==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int CreateView(IntPtr self,IntPtr hwnd,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetItem(IntPtr self,uint flags,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Query(IntPtr self,IntPtr menu,uint index,uint first,uint last,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetString(IntPtr self,UIntPtr id,uint flags,IntPtr reserved,IntPtr text,uint max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowStation(string? name,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder info,int size,out int returned);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EmptyClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetClipboardData(uint format,IntPtr memory);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern uint DragQueryFile(IntPtr drop,uint index,StringBuilder? text,int max);
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern int OleGetClipboard(out IntPtr data);
    [DllImport("ole32.dll")]private static extern int OleSetClipboard(IntPtr data);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]private static extern uint GetMenuItemID(IntPtr menu,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetMenuString(IntPtr menu,uint id,StringBuilder text,int max,uint flags);
    [DllImport("user32.dll")]private static extern uint GetMenuState(IntPtr menu,uint id,uint flags);
}
