using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

internal static class ShellMenuMeasurement
{
    public static int Run(string output, string kind = "mixed", int? iterationCount = null)
    {
        if(iterationCount is <21 or >20000)throw new ArgumentOutOfRangeException(nameof(iterationCount));
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var root=Path.Combine(Path.GetTempPath(),"PaneCommanderShellMenuMeasure",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            var file=Path.Combine(root,"sample.txt");File.WriteAllText(file,"test");var second=Path.Combine(root,"second.txt");File.WriteAllText(second,"test");var directory=Directory.CreateDirectory(Path.Combine(root,"directory")).FullName;
            var rows=new List<object>();using var process=Process.GetCurrentProcess();var watch=Stopwatch.StartNew();
            void Sample(string phase,int iteration,double duration)
            {
                process.Refresh();rows.Add(new{Phase=phase,Iteration=iteration,ElapsedMs=watch.Elapsed.TotalMilliseconds,DurationMs=duration,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,Handles=process.HandleCount,CpuSeconds=process.TotalProcessorTime.TotalSeconds,ManagedBytes=GC.GetTotalMemory(false),GCCommitted=GC.GetGCMemoryInfo().TotalCommittedBytes});
            }
            Sample("before",0,0);Idle(2000);Sample("idle-before",0,0);
            var beforeModules=process.Modules.Cast<ProcessModule>().Select(m=>m.FileName).ToArray();
            var iterations=iterationCount??(kind is "mixed" or "baseline" or "validate-only"?1620:420);
            for(var i=0;i<iterations;i++)
            {
                var begin=watch.Elapsed.TotalMilliseconds;
                if(kind is "baseline" or "validate-only")
                { if(kind=="validate-only")FileSystemService.ValidateLocalPath(root);Thread.Sleep(50); }
                else
                {
                    var type=kind=="mixed"?(i%4):kind switch{"file"=>0,"directory"=>1,"multiple"=>2,"background" or "post-menu-idle"=>3,_=>throw new ArgumentException("Unknown kind")};
                    using(var menu=new ShellContextMenu(root,type switch{0=>new[]{file},1=>new[]{directory},2=>new[]{file,second},_=>Array.Empty<string>()},IntPtr.Zero))
                    { if(type==2)menu.Restrict(verb=>verb!="rename"); }
                }
                Pump();
                Sample(i<20?"warmup":"repeat",i,watch.Elapsed.TotalMilliseconds-begin);
            }
            if(kind=="post-menu-idle")IdleQuiet(60000);else Idle(2000);Sample("idle-after",iterations,0);
            File.WriteAllText(Path.Combine(output,"samples.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"conditions.json"),JsonSerializer.Serialize(new{Driver=kind is "baseline" or "validate-only"?"No Shell menus; same sampling/STA pump; 50ms per iteration":"Native QueryContextMenu/Restrict(multiple only)/Dispose; TrackPopupMenu and GUI input NOT measured",Kind=kind,IdleAfterSeconds=kind=="post-menu-idle"?60:2,ForcedGC=false,Iterations=iterations,WarmupIterations=20,Environment.OSVersion,Runtime=Environment.Version.ToString(),Processors=Environment.ProcessorCount,BeforeModules=beforeModules,AfterModules=process.Modules.Cast<ProcessModule>().Select(m=>m.FileName).ToArray()},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        finally
        {
            OleUninitialize();Dispatcher.CurrentDispatcher.InvokeShutdown();
            if(Path.GetDirectoryName(root)!=Path.Combine(Path.GetTempPath(),"PaneCommanderShellMenuMeasure")||!Guid.TryParse(Path.GetFileName(root),out _))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);
        }
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Idle(int milliseconds){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Pump();Thread.Sleep(10);}}
    private static void IdleQuiet(int milliseconds){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;using var timer=new Timer(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),null,milliseconds,Timeout.Infinite);Dispatcher.PushFrame(frame);}
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
}
