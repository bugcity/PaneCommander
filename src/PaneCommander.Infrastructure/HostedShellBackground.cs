using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PaneCommander.Infrastructure;

/// <summary>Owns a transient native Shell host only for one background menu.</summary>
[SupportedOSPlatform("windows")]
internal sealed class HostedShellBackground : IDisposable
{
    private IntPtr browser,view,host;
    private bool initialized;
    internal object CreateContext(string directory,IntPtr owner)
    {
        IntPtr pidl=IntPtr.Zero,context=IntPtr.Zero;
        try
        {
            host=CreateWindowEx(0,"STATIC","PaneCommander Shell background host",0,0,0,1,1,owner,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(host==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            var cls=new Guid("71f96385-ddd6-48d3-a0c1-ae06e8b055fb");var iid=new Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in cls,IntPtr.Zero,1,in iid,out browser));
            var rect=new Rect{Right=1,Bottom=1};var settings=new FolderSettings{ViewMode=4,Flags=0x10080000}; // no persisted view state, no refresh enumeration
            Marshal.ThrowExceptionForHR(Method<Initialize>(browser,3)(browser,host,ref rect,ref settings));initialized=true;
            Marshal.ThrowExceptionForHR(Method<Options>(browser,11)(browser,0x1|0x8|0x40|0x80)); // navigate once, no travel log/border/persisted view state
            Marshal.ThrowExceptionForHR(SHParseDisplayName(directory,IntPtr.Zero,out pidl,0,out _));
            Marshal.ThrowExceptionForHR(Method<Browse>(browser,13)(browser,pidl,0));
            var watch=Stopwatch.StartNew();iid=new("000214e3-0000-0000-C000-000000000046");int hr;
            do
            {
                // A bounded STA message pump while the native view navigates, not an idle timer.
                var budget=Stopwatch.StartNew();
                while(budget.ElapsedMilliseconds<10&&PeekMessage(out var msg,IntPtr.Zero,0,0,1))
                {
                    if(msg.Id==0x12){PostQuitMessage((int)msg.W.ToUInt64());throw new OperationCanceledException("Shellホストの初期化中に終了が要求されました。");}
                    TranslateMessage(ref msg);DispatchMessage(ref msg);
                }
                hr=Method<GetView>(browser,17)(browser,ref iid,out view);
                if(hr>=0&&view!=IntPtr.Zero)break;
                if(view!=IntPtr.Zero){Marshal.Release(view);view=IntPtr.Zero;}
                Thread.Sleep(1);
            }while(watch.Elapsed.TotalSeconds<5);
            if(view==IntPtr.Zero)throw new TimeoutException("Windows Shellの背景メニュー準備が完了しませんでした。");
            Marshal.ThrowExceptionForHR(hr);
            iid=new("000214e4-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<GetItem>(view,15)(view,0,ref iid,out context));
            return Marshal.GetObjectForIUnknown(context);
        }
        finally{if(context!=IntPtr.Zero)Marshal.Release(context);if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);}
    }
    public void Dispose()
    {
        try{if(view!=IntPtr.Zero){var value=view;view=IntPtr.Zero;Marshal.Release(value);}}
        finally
        {
            try{if(browser!=IntPtr.Zero){var value=browser;browser=IntPtr.Zero;try{if(initialized)Method<Destroy>(value,4)(value);}finally{Marshal.Release(value);}}}
            finally{if(host!=IntPtr.Zero){DestroyWindow(host);host=IntPtr.Zero;}}
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
    [DllImport("ole32.dll")]private static extern int CoCreateInstance(in Guid cls,IntPtr outer,uint context,in Guid iid,out IntPtr obj);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint mask,out uint attributes);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message msg);
    [DllImport("user32.dll")]private static extern void PostQuitMessage(int code);
}
