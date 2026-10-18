using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if(args.Length is 3 or 4){NativeHeapAnalysis.Run(args[0],args[1],int.Parse(args[2]),args.Length==4,args.Length==4&&args[3] is "windows-symbols" or "core-symbols",args.Length==4&&args[3]=="core-symbols");return;}
if(args.Length!=2)throw new ArgumentException("input.nettrace output.json [native-pid]");
if(args[0].EndsWith(".etl",StringComparison.OrdinalIgnoreCase))
{
    using var nativeLog=TraceLog.OpenOrConvert(Path.GetFullPath(args[0]));
    var counts=new Dictionary<string,int>();var examples=new Dictionary<string,List<object>>();
    foreach(var ev in nativeLog.Events)
    {
        if(!ev.ProviderName.Contains("Heap",StringComparison.OrdinalIgnoreCase))continue;
        var key=ev.ProviderName+"/"+ev.EventName;
        counts[key]=counts.GetValueOrDefault(key)+1;
        if(!examples.TryGetValue(key,out var list)){list=[];examples[key]=list;}
        if(list.Count>=2)continue;
        var frames=new List<string>();
        for(var stack=nativeLog.GetCallStackForEvent(ev);stack!=null&&frames.Count<128;stack=stack.Caller)
            frames.Add($"{stack.CodeAddress.ModuleFile?.Name}!{stack.CodeAddress.FullMethodName}@{stack.CodeAddress.Address:x}");
        list.Add(new{ev.TimeStampRelativeMSec,ev.ProcessID,Payload=ev.PayloadNames.ToDictionary(n=>n,n=>ev.PayloadByName(n)?.ToString()),Frames=frames});
    }
    File.WriteAllText(args[1],JsonSerializer.Serialize(new{nativeLog.EventsLost,nativeLog.HasCallStacks,Counts=counts,Examples=examples},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Heap event kinds={counts.Count}, lost={nativeLog.EventsLost}");return;
}
var etlx=TraceLog.CreateFromEventPipeDataFile(Path.GetFullPath(args[0]));
using var log=new TraceLog(etlx);
var allocations=new List<object>();var gcStarts=0;var withStack=0;
foreach(var ev in log.Events)
{
    if(ev is GCStartTraceData)gcStarts++;
    if(ev is not GCAllocationTickTraceData tick)continue;
    var frames=new List<string>();
    for(var stack=log.GetCallStackForEvent(ev);stack!=null&&frames.Count<128;stack=stack.Caller)
        frames.Add(stack.CodeAddress.FullMethodName);
    if(frames.Count>0)withStack++;
    allocations.Add(new{Ms=ev.TimeStampRelativeMSec,ev.ProcessID,tick.TypeName,tick.AllocationAmount64,Kind=tick.AllocationKind.ToString(),Frames=frames});
}
var result=new{Trace=Path.GetFullPath(args[0]),log.EventsLost,log.HasCallStacks,GCStarts=gcStarts,AllocationTicks=allocations.Count,AllocationTicksWithStack=withStack,Allocations=allocations,Note="Allocation ticks are sampled allocation activity, not retained objects or precise per-type bytes; no root scan and no forced GC."};
File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Ticks={allocations.Count} stacks={withStack} GC={gcStarts} lost={log.EventsLost}");
