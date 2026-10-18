using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

// Separate diagnostic EXE: no forced GC, clipboard access, menu invocation or global tracing.
internal static class ShellPhaseMeasurement
{
    internal static int Run(string kind,string output,int blocks,int count,int idleSeconds,string logging,string mode)
    {
        if(blocks is <1 or >10 || count is <1 or >1600 || idleSeconds is <1 or >300 || logging is not ("stream" or "legacy"))throw new ArgumentException("Invalid diagnostic conditions");
        RenderOptions.ProcessRenderMode=mode switch {"Default"=>RenderMode.Default,"SoftwareOnly"=>RenderMode.SoftwareOnly,_=>throw new ArgumentException("mode")};
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var root=Path.Combine(Path.GetTempPath(),"PaneCommanderShellPhases",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var file=Path.Combine(root,"sample.txt");File.WriteAllText(file,"isolated fixture");
        var second=Path.Combine(root,"second.txt");File.WriteAllText(second,"isolated second");
        var directory=Directory.CreateDirectory(Path.Combine(root,"directory")).FullName;
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        using var process=Process.GetCurrentProcess();
        using var csv=new StreamWriter(Path.Combine(output,"samples.csv"));
        csv.WriteLine("Phase,Iteration,ElapsedMs,PrivateBytes,WorkingSet,Handles,CpuSeconds,ManagedBytes,TotalAllocated,GCHeap,GCCommitted,Fragmented,FinalizationPending,Gen0,Gen1,Gen2,GdiObjects,UserObjects");
        var retained=new List<object>();var weak=new WeakReference?[100];var total=0;var watch=Stopwatch.StartNew();
        void Sample(string phase,int index)
        {
            var mem=new Counters{Size=(uint)Marshal.SizeOf<Counters>()};
            if(!GetProcessMemoryInfo(new(-1),ref mem,mem.Size)||!GetProcessHandleCount(new(-1),out var handles)||!GetProcessTimes(new(-1),out _,out _,out var kernel,out var user))throw new System.ComponentModel.Win32Exception();
            var pb=(long)mem.PrivateUsage;var ws=(long)mem.WorkingSet;
            var cpu=(kernel+user)/1e7;var managed=GC.GetTotalMemory(false);var gc=GC.GetGCMemoryInfo();
            if(logging=="legacy")
            {
                process.Refresh();pb=process.PrivateMemorySize64;ws=process.WorkingSet64;handles=(uint)process.HandleCount;cpu=process.TotalProcessorTime.TotalSeconds;
                retained.Add(new{Phase=phase,Iteration=index,ElapsedMs=watch.Elapsed.TotalMilliseconds,PrivateBytes=pb,WorkingSet=ws,Handles=handles,CpuSeconds=cpu,ManagedBytes=managed,GCCommitted=gc.TotalCommittedBytes});
            }
            csv.WriteLine($"{phase},{index},{watch.Elapsed.TotalMilliseconds},{pb},{ws},{handles},{cpu},{managed},{GC.GetTotalAllocatedBytes(false)},{gc.HeapSizeBytes},{gc.TotalCommittedBytes},{gc.FragmentedBytes},{gc.FinalizationPendingCount},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{GetGuiResources(new(-1),0)},{GetGuiResources(new(-1),1)}");csv.Flush();
            if(ShellPhaseEvents.Log.IsEnabled())ShellPhaseEvents.Log.Marker(phase,index,watch.Elapsed.TotalMilliseconds);
        }
        void Idle(string phase)
        {
            Sample(phase,0);
            for(var i=1;i<=idleSeconds;i++)
            {
                var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
                using var timer=new Timer(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),null,1000,Timeout.Infinite);
                Dispatcher.PushFrame(frame);Sample(phase,i);
            }
        }
        void Once()
        {
            if(kind=="baseline")Thread.Sleep(50);
            else if(kind=="host-only"){using var host=new DiagnosticHostedShellBackground();host.CreateContext(root,IntPtr.Zero,false);}
            else if(kind is "file-context" or "background-context")
            {using var menu=new DiagnosticShellContextMenu(root,kind=="file-context"?[file]:[],IntPtr.Zero);weak[total%weak.Length]=new WeakReference(menu);}
            else
            {
                using var menu=new ShellContextMenu(root,kind switch {"file"=>[file],"directory"=>[directory],"multiple"=>[file,second],"background"=>[],_=>throw new ArgumentException("kind")},IntPtr.Zero);
                if(kind=="multiple")menu.Restrict(verb=>verb!="rename");
                weak[total%weak.Length]=new WeakReference(menu);
            }
            var pump=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>pump.Continue=false);Dispatcher.PushFrame(pump);total++;
        }
        try
        {
            Sample("initial",0);Idle("idle-initial");
            for(var i=0;i<20;i++){Once();Sample("warmup",i);}
            for(var block=1;block<=blocks;block++)
            {
                Sample($"block-{block}",-1);
                for(var i=0;i<count;i++){Once();Sample($"block-{block}",i);}
                Idle($"idle-{block}");
            }
            File.WriteAllText(Path.Combine(output,"conditions.json"),JsonSerializer.Serialize(new{Kind=kind,Mode=mode,Logging=logging,Blocks=blocks,CountPerBlock=count,IdleSeconds=idleSeconds,Warmup=20,ForcedGC=false,Popup=false,ProcessId=Environment.ProcessId,LogicalProcessors=Environment.ProcessorCount,Runtime=Environment.Version.ToString(),LegacyRetainedSamples=retained.Count,WeakRingSize=weak.Length,WeakAlive=weak.Count(w=>w?.IsAlive==true),Modules=process.Modules.Cast<ProcessModule>().Select(m=>m.FileName).ToArray()},new JsonSerializerOptions{WriteIndented=true}));
            GC.KeepAlive(retained);return 0;
        }
        finally
        {
            OleUninitialize();Dispatcher.CurrentDispatcher.InvokeShutdown();
            if(Path.GetDirectoryName(root)!=Path.Combine(Path.GetTempPath(),"PaneCommanderShellPhases")||!Guid.TryParse(Path.GetFileName(root),out _))throw new IOException("Unsafe cleanup");
            Directory.Delete(root,true);
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct Counters {public uint Size,PageFaultCount;public nuint PeakWorkingSet,WorkingSet,QuotaPeakPaged,QuotaPaged,QuotaPeakNonPaged,QuotaNonPaged,Pagefile,PeakPagefile,PrivateUsage;}
    [DllImport("psapi.dll",SetLastError=true)]private static extern bool GetProcessMemoryInfo(IntPtr process,ref Counters counters,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessHandleCount(IntPtr process,out uint count);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(IntPtr process,out ulong creation,out ulong exit,out ulong kernel,out ulong user);
    [DllImport("user32.dll")]private static extern uint GetGuiResources(IntPtr process,uint flags);
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
}

[EventSource(Name="PaneCommander-ShellPhases",Guid="9f5333a6-8647-4300-8c79-70b37385a149")]
internal sealed class ShellPhaseEvents : EventSource
{
    internal static readonly ShellPhaseEvents Log=new();
    [Event(1,Level=EventLevel.Informational)]
    public void Marker(string phase,int index,double elapsedMs)=>WriteEvent(1,phase,index,elapsedMs);
}
