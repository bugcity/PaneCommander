using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PaneCommander.Performance;

internal static class DesktopEnvironment
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var devices=new List<object>();
        for(uint i=0;i<16;i++){var device=new Device{Size=Marshal.SizeOf<Device>()};if(!EnumDisplayDevices(null,i,ref device,0))break;devices.Add(new{device.Name,device.Description,device.Flags});}
        var input=OpenInputDesktop(0,false,1);var error=input==IntPtr.Zero?Marshal.GetLastWin32Error():0;var inputName=input==IntPtr.Zero?"":Name(input);if(input!=IntPtr.Zero)CloseDesktop(input);
        IntPtr info=IntPtr.Zero;int? flags=null;int? state=null;var wts=WTSQuerySessionInformation(IntPtr.Zero,Process.GetCurrentProcess().SessionId,25,out info,out var bytes);var wtsError=wts?0:Marshal.GetLastWin32Error();
        if(wts){try{if(bytes>=20&&Marshal.ReadInt32(info)==1){state=Marshal.ReadInt32(info,12);flags=Marshal.ReadInt32(info,16);}}finally{WTSFreeMemory(info);}}
        var clipboard=OpenClipboard(IntPtr.Zero);var clipboardError=clipboard?0:Marshal.GetLastWin32Error();if(clipboard)CloseClipboard();
        var result=new{ProcessId=Environment.ProcessId,ProcessSession=Process.GetCurrentProcess().SessionId,ActiveConsoleSession=WTSGetActiveConsoleSessionId(),WindowStation=Name(GetProcessWindowStation()),ThreadDesktop=Name(GetThreadDesktop(GetCurrentThreadId())),InputDesktop=inputName,InputDesktopError=error,WTSSessionState=state,WTSSessionFlags=flags,WTSError=wtsError,SessionFlagMeaning="Windows 11: 0 locked, 1 unlocked; no settings changed",MonitorCount=GetSystemMetrics(80),ScreenWidth=GetSystemMetrics(0),ScreenHeight=GetSystemMetrics(1),Devices=devices,ClipboardReadOnlyOpen=clipboard,ClipboardError=clipboardError};
        File.WriteAllText(Path.Combine(output,"desktop-environment.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
    private static string Name(IntPtr handle){var text=new StringBuilder(256);return GetUserObjectInformation(handle,2,text,512,out _)?text.ToString():$"error:{Marshal.GetLastWin32Error()}";}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Device {public int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Key;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool EnumDisplayDevices(string? device,uint index,ref Device output,uint flags);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")]private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder info,int size,out int returned);
    [DllImport("kernel32.dll")]private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)]private static extern bool WTSQuerySessionInformation(IntPtr server,int session,int info,out IntPtr buffer,out int size);
    [DllImport("wtsapi32.dll")]private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
}
