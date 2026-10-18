using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Symbols;

internal static class NativeHeapAnalysis
{
    private sealed record Allocation(ulong Size,int Stack,double Born,bool JitInStack);
    private sealed class Totals {public ulong Allocated,Freed,Destroyed;public long Allocations,Frees,DestroyedAllocations;}
    internal static void Run(string input,string output,int pid,bool symbols=false,bool cachedWindowsOnly=false,bool includeCoreClr=false)
    {
        using var log=TraceLog.OpenOrConvert(Path.GetFullPath(input));
        var heaps=new Dictionary<ulong,Dictionary<ulong,Allocation>>();
        var totals=new Dictionary<int,Totals>();var examples=new Dictionary<int,string[]>();
        var snapshotStacks=new HashSet<int>();var committed=new Dictionary<ulong,ulong>();
        var jitStacks=new Dictionary<int,bool>();ulong jitLive=0;
        ulong live=0,allocated=0,freed=0,destroyed=0;long allocCount=0,freeCount=0,unknownFree=0,duplicate=0,reallocCount=0,heapDestroy=0;
        var samples=new List<object>();var snapshots=new List<object>();double lastSample=-1,lastSnapshot=-1;
        long reallocAlreadyAccounted=0;
        var markers=new List<object>();var freePairs=new Dictionary<(int Alloc,int Free),(ulong Bytes,long Count)>();
        long freeEvents=0,freeEventsWithStack=0,allocationEventsWithStack=0;
        Totals T(int stack){if(!totals.TryGetValue(stack,out var value)){value=new();totals.Add(stack,value);}return value;}
        void Remove(ulong heap,ulong address,bool destroy=false,int freeStack=-1)
        {
            if(!heaps.TryGetValue(heap,out var map)||!map.Remove(address,out var old)){if(!destroy)unknownFree++;return;}
            live-=old.Size;if(old.JitInStack)jitLive-=old.Size;var t=T(old.Stack);
            if(destroy){destroyed+=old.Size;t.Destroyed+=old.Size;t.DestroyedAllocations++;}
            else{freed+=old.Size;t.Freed+=old.Size;t.Frees++;freeCount++;}
            if(!destroy && freeStack>=0){var key=(old.Stack,freeStack);var previous=freePairs.GetValueOrDefault(key);freePairs[key]=(previous.Bytes+old.Size,previous.Count+1);}
        }
        void Add(ulong heap,ulong address,ulong size,int stack,double time)
        {
            if(!heaps.TryGetValue(heap,out var map)){map=[];heaps.Add(heap,map);}
            if(map.TryGetValue(address,out var old)){duplicate++;live-=old.Size;if(old.JitInStack)jitLive-=old.Size;map.Remove(address);}
            if(!jitStacks.TryGetValue(stack,out var jit))
            {
                for(var frame=log.CallStacks[(CallStackIndex)stack];frame!=null;frame=frame.Caller)
                    if(frame.CodeAddress.ModuleFile?.Name.Equals("clrjit",StringComparison.OrdinalIgnoreCase)==true){jit=true;break;}
                jitStacks[stack]=jit;
            }
            map.Add(address,new(size,stack,time,jit));live+=size;if(jit)jitLive+=size;allocated+=size;allocCount++;var t=T(stack);t.Allocated+=size;t.Allocations++;
        }
        void Snapshot(double time)
        {
            var outstanding=heaps.Values.SelectMany(h=>h.Values).GroupBy(a=>a.Stack)
                .Select(g=>new{Stack=g.Key,Bytes=g.Aggregate(0UL,(sum,a)=>sum+a.Size),Count=g.Count(),OldestMs=g.Min(a=>a.Born)})
                .OrderByDescending(g=>g.Bytes).Take(30).ToArray();
            snapshots.Add(new{Ms=time,LiveBytes=live,ClrJitStackOutstandingBytes=jitLive,Top=outstanding});
            foreach(var item in outstanding)snapshotStacks.Add(item.Stack);
        }
        foreach(var ev in log.Events)
        {
            if(ev.ProcessID!=pid)continue;
            if(ev.ProviderName=="PaneCommander-ShellPhases" && ev.EventName=="Marker")
            {
                var phase=ev.PayloadByName("phase")?.ToString();var index=Convert.ToInt32(ev.PayloadByName("index"));
                markers.Add(new{Ms=ev.TimeStampRelativeMSec,Phase=phase,Index=index,DiagnosticElapsedMs=Convert.ToDouble(ev.PayloadByName("elapsedMs")),LiveBytes=live,ClrJitStackOutstandingBytes=jitLive,AllocatedBytes=allocated,FreedBytes=freed,HeapDestroyedBytes=destroyed,LastReportedHeapCommittedBytes=committed.Values.Aggregate(0UL,(sum,v)=>sum+v)});
                if(index is -1 or 0 or 24 or 49 || phase is "idle-1" or "idle-2")Snapshot(ev.TimeStampRelativeMSec);
                continue;
            }
            if(ev.ProviderName!="HeapTraceProvider")continue;
            var name=ev.EventName;
            if(!name.StartsWith("HeapTrace/",StringComparison.Ordinal))continue;
            ulong V(string key)=>Convert.ToUInt64(ev.PayloadByName(key));
            var heap=V("HeapHandle");var stack=(int)log.GetCallStackIndexForEvent(ev);var ms=ev.TimeStampRelativeMSec;
            if(name=="HeapTrace/Alloc"){if(stack>=0)allocationEventsWithStack++;Add(heap,V("AllocAddress"),V("AllocSize"),stack,ms);}
            else if(name=="HeapTrace/Free"){freeEvents++;if(stack>=0)freeEventsWithStack++;Remove(heap,V("FreeAddress"),freeStack:stack);}
            else if(name=="HeapTrace/ReAlloc")
            {
                reallocCount++;
                var address=V("NewAllocAddress");
                var oldAddress=V("OldAllocAddress");var size=V("NewAllocSize");
                // Some ReAlloc events follow the corresponding Alloc/Free events.
                // Count those physical allocations once instead of overwriting the live entry.
                if(address!=0)
                {
                    var map=heaps.GetValueOrDefault(heap);
                    if(map is not null && map.TryGetValue(address,out var current) && current.Size==size &&
                       (oldAddress!=address && !map.ContainsKey(oldAddress) || oldAddress==address && size!=V("OldAllocSize")))
                        reallocAlreadyAccounted++;
                    else{Remove(heap,oldAddress);Add(heap,address,size,stack,ms);}
                }
            }
            else if(name=="HeapTrace/Destroy")
            {
                heapDestroy++;
                if(heaps.TryGetValue(heap,out var map)){foreach(var address in map.Keys.ToArray())Remove(heap,address,true);heaps.Remove(heap);}
                committed.Remove(heap);
            }
            else if(name is "HeapTrace/Expand" or "HeapTrace/Contract")committed[heap]=V("CommittedSpace");
            if(ms-lastSample>=250)
            {
                samples.Add(new{Ms=ms,LiveBytes=live,ClrJitStackOutstandingBytes=jitLive,LastReportedHeapCommittedBytes=committed.Values.Aggregate(0UL,(sum,v)=>sum+v),AllocatedBytes=allocated,FreedBytes=freed,HeapDestroyedBytes=destroyed,AllocationCount=allocCount,FreeCount=freeCount});lastSample=ms;
            }
            if(ms-lastSnapshot>=2500){Snapshot(ms);lastSnapshot=ms;}
        }
        Snapshot(lastSample);
        var relevant=totals.OrderByDescending(k=>k.Value.Allocated).Take(30).Select(k=>k.Key)
            .Concat(heaps.Values.SelectMany(h=>h.Values).GroupBy(a=>a.Stack).OrderByDescending(g=>g.Aggregate(0UL,(sum,a)=>sum+a.Size)).Take(30).Select(g=>g.Key)).Concat(snapshotStacks).Distinct();
        relevant=relevant.Concat(freePairs.OrderByDescending(k=>k.Value.Bytes).Take(30).SelectMany(k=>new[]{k.Key.Alloc,k.Key.Free})).Distinct().ToArray();
        if(symbols)
        {
            var cache=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"symbols");Directory.CreateDirectory(cache);
            using var symbolLog=new StreamWriter(output+".symbols.log");
            using var reader=new SymbolReader(symbolLog,$"srv*{cache}*https://msdl.microsoft.com/download/symbols");
            var modules=new HashSet<TraceModuleFile>();
            foreach(var index in relevant)for(var stack=log.CallStacks[(CallStackIndex)index];stack!=null;stack=stack.Caller)
                if(stack.CodeAddress.ModuleFile is { } module && module.Name.ToLowerInvariant() is "coreclr" or "ntdll" or "ucrtbase" or "shell32" or "windows.storage" or "icu")modules.Add(module);
            foreach(var module in modules)
                if(!cachedWindowsOnly || module.Name.ToLowerInvariant() is "ntdll" or "windows.storage" or "shell32" || includeCoreClr && module.Name.Equals("coreclr",StringComparison.OrdinalIgnoreCase))
                    log.CodeAddresses.LookupSymbolsForModule(reader,module);
        }
        foreach(var index in relevant)
        {
            var frames=new List<string>();
            for(var stack=log.CallStacks[(CallStackIndex)index];stack!=null&&frames.Count<128;stack=stack.Caller)
                frames.Add($"{stack.CodeAddress.ModuleFile?.Name}!{stack.CodeAddress.FullMethodName}@{stack.CodeAddress.Address:x}");
            examples[index]=frames.ToArray();
        }
        var liveGroups=heaps.Values.SelectMany(h=>h.Values).GroupBy(a=>a.Stack).ToDictionary(g=>g.Key,g=>new{Bytes=g.Aggregate(0UL,(sum,a)=>sum+a.Size),Count=g.Count()});
        if(allocated!=freed+destroyed+live)throw new InvalidDataException("Heap allocation ledger does not balance.");
        File.WriteAllText(output,JsonSerializer.Serialize(new{TargetPid=pid,log.EventsLost,LedgerBalanced=true,FreeEvents=freeEvents,FreeEventsWithStack=freeEventsWithStack,AllocationEventsWithStack=allocationEventsWithStack,AllocatedBytes=allocated,FreedBytes=freed,HeapDestroyedBytes=destroyed,LiveBytesAtEnd=live,AllocationCount=allocCount,MatchedFreeCount=freeCount,UnknownFreeCount=unknownFree,DuplicateAddressCount=duplicate,ReallocationCount=reallocCount,ReallocAlreadyAccounted=reallocAlreadyAccounted,HeapDestroyCount=heapDestroy,Samples=samples,Snapshots=snapshots,Markers=markers,FreePairs=freePairs.OrderByDescending(k=>k.Value.Bytes).Take(100).Select(k=>new{AllocationStack=k.Key.Alloc,FreeStack=k.Key.Free,k.Value.Bytes,k.Value.Count}),StackTotals=totals.Select(k=>new{Stack=k.Key,k.Value.Allocated,k.Value.Freed,k.Value.Destroyed,k.Value.Allocations,k.Value.Frees,OutstandingBytes=liveGroups.TryGetValue(k.Key,out var g)?g.Bytes:0,OutstandingCount=liveGroups.TryGetValue(k.Key,out g)?g.Count:0}).OrderByDescending(g=>g.OutstandingBytes),StackExamples=examples,Note="Tracked requested heap allocations only; not committed Private Bytes. Heap destruction frees tracked blocks. No retaining-reference/COM root scan. Unmatched frees and duplicate addresses require interpretation."},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        Console.WriteLine($"pid={pid} allocations={allocCount} matchedFrees={freeCount} live={live} unknownFrees={unknownFree} duplicates={duplicate} lost={log.EventsLost}");
    }
}
