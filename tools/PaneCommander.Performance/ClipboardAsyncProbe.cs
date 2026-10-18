using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;
internal static class ClipboardAsyncProbe
{
    internal static int Run(string destination,string output)
    {
        Directory.CreateDirectory(output);var result=new Dictionary<string,object?>();object? data=null;IAsync? async=null;bool prior=false,changed=false;
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            var paths=System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
            var allowed=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/shell-gui-resume"))+Path.DirectorySeparatorChar;
            if(paths.Length==0||!Path.GetFullPath(destination).StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||paths.Any(p=>!Path.GetFullPath(p).StartsWith(allowed,StringComparison.OrdinalIgnoreCase)))throw new IOException("Dedicated fixture paths required");
            Directory.CreateDirectory(destination);ShellMenuSafety.CheckInvocation("paste",destination,[],paths);
            Marshal.ThrowExceptionForHR(OleGetClipboard(out data));async=data as IAsync;result["SupportsAsync"]=async!=null;
            if(async!=null){Marshal.ThrowExceptionForHR(async.GetAsyncMode(out prior));result["PriorAsyncMode"]=prior;Marshal.ThrowExceptionForHR(async.SetAsyncMode(false));changed=true;}
            using(var menu=new ShellContextMenu(destination,[],IntPtr.Zero))
            {
                var paste=menu.Commands().Single(c=>c.Verb=="paste");result["Enabled"]=menu.IsEnabled(paste.Id);
                var clock=Stopwatch.StartNew();menu.Invoke(paste.Id,0,0);result["InvokeMilliseconds"]=clock.Elapsed.TotalMilliseconds;
                result["FilesExistAtInvokeReturn"]=paths.Select(p=>new{Source=p,Destination=Path.Combine(destination,Path.GetFileName(p)),Exists=File.Exists(Path.Combine(destination,Path.GetFileName(p)))}).ToArray();
            }
            result["Success"]=true;return 0;
        }
        catch(Exception e){result["Success"]=false;result["Error"]=e.ToString();return 1;}
        finally
        {
            try{if(changed&&async!=null){var hr=async.SetAsyncMode(prior);result["AsyncModeRestoreHr"]=$"0x{hr:X8}";}}
            finally{if(data!=null)Marshal.ReleaseComObject(data);OleUninitialize();result["ClipboardContentsRestored"]=false;File.WriteAllText(Path.Combine(output,"clipboard-async.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}
        }
    }
    [ComImport,Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IAsync
    {
        [PreserveSig]int SetAsyncMode([MarshalAs(UnmanagedType.Bool)]bool value);
        [PreserveSig]int GetAsyncMode([MarshalAs(UnmanagedType.Bool)]out bool value);
        [PreserveSig]int StartOperation(IntPtr bind);
        [PreserveSig]int InOperation([MarshalAs(UnmanagedType.Bool)]out bool value);
        [PreserveSig]int EndOperation(int hr,IntPtr bind,uint effect);
    }
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr p);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)]out object data);
}
