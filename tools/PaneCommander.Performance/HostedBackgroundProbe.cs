using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PaneCommander.Performance;

// Diagnostic only: a short-lived Windows ExplorerBrowser hosts the Shell view.
internal static class HostedBackgroundProbe
{
    internal static int Run(string directory,string output)
    {
        Directory.CreateDirectory(output);var result=new Dictionary<string,object?>();
        IntPtr browser=IntPtr.Zero,view=IntPtr.Zero,context=IntPtr.Zero,pidl=IntPtr.Zero,host=IntPtr.Zero,menu=IntPtr.Zero;
        bool ole=false,initialized=false;
        try
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));ole=true;
            host=CreateWindowEx(0,"STATIC","PaneCommander diagnostic Shell host",0,0,0,256,256,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(host==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            var cls=new Guid("71f96385-ddd6-48d3-a0c1-ae06e8b055fb");var iid=new Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in cls,IntPtr.Zero,1,in iid,out browser));
            var rect=new Rect{Right=256,Bottom=256};var settings=new FolderSettings{ViewMode=4,Flags=0x10000000};
            Marshal.ThrowExceptionForHR(Method<Initialize>(browser,3)(browser,host,ref rect,ref settings));initialized=true;
            Marshal.ThrowExceptionForHR(Method<Options>(browser,11)(browser,0x4|0x8|0x10));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetFullPath(directory),IntPtr.Zero,out pidl,0,out _));
            Marshal.ThrowExceptionForHR(Method<Browse>(browser,13)(browser,pidl,0));
            var watch=Stopwatch.StartNew();iid=new("000214e3-0000-0000-C000-000000000046");int hr;
            do
            {
                while(PeekMessage(out var msg,IntPtr.Zero,0,0,1)){TranslateMessage(ref msg);DispatchMessage(ref msg);}
                hr=Method<GetView>(browser,17)(browser,ref iid,out view);
                if(hr>=0&&view!=IntPtr.Zero)break;
                if(view!=IntPtr.Zero){Marshal.Release(view);view=IntPtr.Zero;}
                Thread.Sleep(10);
            }while(watch.Elapsed.TotalSeconds<10);
            Marshal.ThrowExceptionForHR(hr);if(view==IntPtr.Zero)throw new IOException("Shell view unavailable");
            iid=new("000214e4-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<GetItem>(view,15)(view,0,ref iid,out context));
            menu=CreatePopupMenu();if(menu==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            Marshal.ThrowExceptionForHR(Method<Query>(context,3)(context,menu,0,1,0x7fff,0x10));
            var commands=new List<object>();
            for(int i=0;i<GetMenuItemCount(menu);i++)
            {
                var id=GetMenuItemID(menu,i);if(id==0||id>0x7fff)continue;
                var text=new StringBuilder(512);GetMenuString(menu,(uint)i,text,512,0x400);
                var buffer=Marshal.AllocHGlobal(2048);
                try{Marshal.WriteInt16(buffer,0);var r=Method<GetString>(context,5)(context,(UIntPtr)(id-1),4,IntPtr.Zero,buffer,1024);commands.Add(new{Id=id,Text=text.ToString(),Verb=r>=0?Marshal.PtrToStringUni(buffer):null,State=GetMenuState(menu,id,0)});}
                finally{Marshal.FreeHGlobal(buffer);}
            }
            result["Commands"]=commands;result["NavigationReadyMs"]=watch.Elapsed.TotalMilliseconds;
            result["Success"]=true;return 0;
        }
        catch(Exception ex){result["Error"]=ex.ToString();result["Success"]=false;return 1;}
        finally
        {
            if(menu!=IntPtr.Zero)DestroyMenu(menu);if(context!=IntPtr.Zero)Marshal.Release(context);if(view!=IntPtr.Zero)Marshal.Release(view);
            if(browser!=IntPtr.Zero){try{if(initialized)Method<Destroy>(browser,4)(browser);}finally{Marshal.Release(browser);}}
            if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);if(host!=IntPtr.Zero)DestroyWindow(host);if(ole)OleUninitialize();
            result["ClipboardWritten"]=false;result["CommandInvoked"]=false;result["ProductCodeChanged"]=false;result["Route"]="IExplorerBrowser.Initialize/BrowseToIDList/GetCurrentView → IShellView.GetItemObject(SVGIO_BACKGROUND)";
            File.WriteAllText(Path.Combine(output,"hosted-background.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    private static T Method<T>(IntPtr p,int slot)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct FolderSettings{public uint ViewMode,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Hwnd;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Initialize(IntPtr self,IntPtr parent,ref Rect r,ref FolderSettings s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Destroy(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Options(IntPtr self,uint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Browse(IntPtr self,IntPtr pidl,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetView(IntPtr self,ref Guid iid,out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetItem(IntPtr self,uint flags,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Query(IntPtr self,IntPtr menu,uint index,uint first,uint last,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetString(IntPtr self,UIntPtr id,uint flags,IntPtr reserved,IntPtr text,uint max);
    [DllImport("ole32.dll")]private static extern int CoCreateInstance(in Guid cls,IntPtr outer,uint context,in Guid iid,out IntPtr obj);
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint mask,out uint attributes);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message msg);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]private static extern uint GetMenuItemID(IntPtr menu,int i);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetMenuString(IntPtr menu,uint id,StringBuilder text,int max,uint flags);
    [DllImport("user32.dll")]private static extern uint GetMenuState(IntPtr menu,uint id,uint flags);
}
