using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

// Diagnostic-only native interfaces; never used by the product. No global verifier settings.
internal static class ShellLifetimeDiagnosis
{
    internal static int Run(string output,string mode)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var root=Path.Combine(Path.GetTempPath(),"PaneCommanderShellLifetime",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        var enable=Marshal.AllocHGlobal(8);Marshal.WriteInt32(enable,0);Marshal.WriteInt32(enable,4,65536);
        var traceStatus=NtSetInformationProcess(new(-1),32,enable,8);Marshal.FreeHGlobal(enable);
        try
        {
            for(var i=0;i<20;i++){Once(root,mode);Pump();}
            using var eventProbe=mode=="product-pin"?new EventImportProbe(false):mode=="product-probe"?new EventImportProbe():null;
            var before=Handles();using var process=Process.GetCurrentProcess();process.Refresh();var startPrivate=process.PrivateMemorySize64;
            var rows=new List<object>();var watch=Stopwatch.StartNew();var cpuStart=process.TotalProcessorTime;
            for(var i=0;i<100;i++){Once(root,mode);Pump();process.Refresh();rows.Add(new{Iteration=i,ElapsedMs=watch.Elapsed.TotalMilliseconds,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,Handles=process.HandleCount,GdiObjects=GetGuiResources(new(-1),0),UserObjects=GetGuiResources(new(-1),1),CpuSeconds=(process.TotalProcessorTime-cpuStart).TotalSeconds,ManagedBytes=GC.GetTotalMemory(false),GCCommitted=GC.GetGCMemoryInfo().TotalCommittedBytes});}
            var after=Handles();var newHandles=after.Where(h=>!before.ContainsKey(h.Key)).ToArray();
            File.WriteAllText(Path.Combine(output,"diagnosis.json"),JsonSerializer.Serialize(new{Mode=mode,TraceEnableStatus=$"0x{traceStatus:X8}",Warmup=20,Iterations=100,ForcedGC=false,ProcessId=Environment.ProcessId,Before=before.Values,After=after.Values,NewHandles=newHandles.Select(h=>new{h.Value.Handle,h.Value.Type,h.Value.Name,Traces=Traces(h.Key)}),PinnedModules=eventProbe?.ModulePaths,EventImports=eventProbe?.Imports,EventCalls=eventProbe?.Calls,PrivateBefore=startPrivate,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        finally
        {
            if(traceStatus>=0)NtSetInformationProcess(new(-1),32,IntPtr.Zero,0);
            OleUninitialize();Dispatcher.CurrentDispatcher.InvokeShutdown();
            if(Path.GetDirectoryName(root)!=Path.Combine(Path.GetTempPath(),"PaneCommanderShellLifetime")||!Guid.TryParse(Path.GetFileName(root),out _))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);
        }
    }
    private static void Once(string root,string mode)
    {
        if(mode=="baseline")return;
        if(mode=="module-cycle")
        {
            var path=Environment.GetEnvironmentVariable("PANECOMMANDER_DIAGNOSTIC_MODULE")??throw new ArgumentException("Diagnostic module path missing");
            if(!Path.IsPathFullyQualified(path)||!Path.GetFileName(path).Equals("nvui.dll",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Expected observed nvui.dll path");
            var module=NativeLibrary.Load(path);NativeLibrary.Free(module);return;
        }
        if(mode is "product" or "product-pin" or "product-probe"){using var productMenu=new ShellContextMenu(root,[],IntPtr.Zero);return;}
        IntPtr pidl=IntPtr.Zero,desktop=IntPtr.Zero,folder=IntPtr.Zero,context=IntPtr.Zero,menu=IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(root,IntPtr.Zero,out pidl,0,out _));Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktop));
            var iid=new Guid("000214e6-0000-0000-C000-000000000046");Marshal.ThrowExceptionForHR(Method<Bind>(desktop,5)(desktop,pidl,IntPtr.Zero,ref iid,out folder));
            iid=new("000214e4-0000-0000-C000-000000000046");Marshal.ThrowExceptionForHR(Method<View>(folder,8)(folder,IntPtr.Zero,ref iid,out context));
            if(mode=="acquire-only")return;
            menu=CreatePopupMenu();if(menu==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            Marshal.ThrowExceptionForHR(Method<Query>(context,3)(context,menu,0,1,0x7fff,mode=="default-verb"?1u:0x10u));
        }
        finally{if(menu!=IntPtr.Zero)DestroyMenu(menu);if(context!=IntPtr.Zero)Marshal.Release(context);if(folder!=IntPtr.Zero)Marshal.Release(folder);if(desktop!=IntPtr.Zero)Marshal.Release(desktop);if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);}
    }
    private static T Method<T>(IntPtr value,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),slot*IntPtr.Size));
    private sealed record HandleInfo(long Handle,string Type,string Name);
    private static Dictionary<long,HandleInfo> Handles()
    {
        // Self only. TYPE queries cannot block on a file's name; name queries restricted to registry/events.
        var result=new Dictionary<long,HandleInfo>();var buffer=Marshal.AllocHGlobal(65536);var snapshot=Marshal.AllocHGlobal(1024*1024);
        try
        {
            var status=NtQueryInformationProcess(new(-1),51,snapshot,1024*1024,out _);if(status<0)throw new IOException($"Handle snapshot NTSTATUS 0x{status:X8}");
            var count=Marshal.ReadInt64(snapshot);if(count<0||count>20000)throw new IOException("Handle snapshot invalid");
            for(var i=0;i<count;i++) {var h=Marshal.ReadInt64(snapshot,16+i*40);if(NtQueryObject(new(h),2,buffer,65536,out _) < 0)continue;var type=Unicode(buffer);var name="";if(type is "Event" or "Key" or "Mutant" or "Semaphore")if(NtQueryObject(new(h),1,buffer,65536,out _)>=0)name=Unicode(buffer);result[h]=new(h,type,name);}
        }
        finally{Marshal.FreeHGlobal(snapshot);Marshal.FreeHGlobal(buffer);}return result;
    }
    private static string Unicode(IntPtr value){var length=(ushort)Marshal.ReadInt16(value);var pointer=Marshal.ReadIntPtr(value,IntPtr.Size==8?8:4);return pointer==IntPtr.Zero?"":Marshal.PtrToStringUni(pointer,length/2)??"";}
    private static object Traces(long handle)
    {
        var buffer=Marshal.AllocHGlobal(65536);
        try
        {
            Marshal.WriteIntPtr(buffer,new(handle));var status=NtQueryInformationProcess(new(-1),32,buffer,65536,out _);if(status<0)return new{Status=$"0x{status:X8}"};
            var count=Marshal.ReadInt32(buffer,8);if(count<0||count>400)throw new IOException("Invalid trace count");using var process=Process.GetCurrentProcess();var modules=process.Modules.Cast<ProcessModule>().ToArray();
            var traces=new List<object>();for(var i=0;i<count;i++) {var entry=buffer+16+i*160;var stack=new List<string>();for(var j=0;j<16;j++){var address=Marshal.ReadInt64(entry,32+j*8);if(address==0)continue;var module=modules.FirstOrDefault(m=>address>=m.BaseAddress.ToInt64()&&address<m.BaseAddress.ToInt64()+m.ModuleMemorySize);stack.Add(module is null?$"0x{address:X}":$"{module.ModuleName}+0x{address-module.BaseAddress.ToInt64():X}");}traces.Add(new{Type=Marshal.ReadInt32(entry,24),ThreadId=Marshal.ReadInt64(entry,16),Stack=stack});}return new{Status=$"0x{status:X8}",Traces=traces};
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Bind(IntPtr self,IntPtr pidl,IntPtr bind,ref Guid iid,out IntPtr folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int View(IntPtr self,IntPtr hwnd,ref Guid iid,out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Query(IntPtr self,IntPtr menu,uint index,uint first,uint last,uint flags);
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ntdll.dll")]private static extern int NtSetInformationProcess(IntPtr process,int info,IntPtr value,int size);
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(IntPtr process,int info,IntPtr value,int size,out int returned);
    [DllImport("ntdll.dll")]private static extern int NtQueryObject(IntPtr handle,int info,IntPtr value,int size,out int returned);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint mask,out uint attributes);
    [DllImport("shell32.dll")]private static extern int SHGetDesktopFolder(out IntPtr folder);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern uint GetGuiResources(IntPtr process,uint flags);
}
