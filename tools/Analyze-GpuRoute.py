from pathlib import Path
import json,re,statistics,sys
root=Path(__file__).resolve().parents[1]
folder=root/(sys.argv[1] if len(sys.argv)>1 else 'docs/verification/default-redraw-20261015/gpu-route-1')
data=json.loads((folder/'gpu-route.json').read_text(encoding='utf-8-sig'))
adapters={a['LuidPrefix'].lower():a for a in data['DxgiAdapters']}
groups={}
for sample in data['Samples']:
    for entry in sample.get('GpuProcessMemory',[]):
        match=re.match(r'pid_(\d+)_(luid_0x[0-9a-f]+_0x[0-9a-f]+)',entry['Name'],re.I)
        assert match and match[2].lower()in adapters
        key=(int(match[1]),match[2].lower())
        groups.setdefault(key,[]).append(entry)
summary=[]
for (pid,luid),values in groups.items():
    summary.append(dict(ProcessId=pid,Adapter=adapters[luid]['Description'],Luid=luid,Samples=len(values),
                        DedicatedMeanBytes=statistics.mean(int(v['DedicatedUsage'])for v in values),
                        SharedMeanBytes=statistics.mean(int(v['SharedUsage'])for v in values),
                        SharedMaxBytes=max(int(v['SharedUsage'])for v in values),
                        CommittedMeanBytes=statistics.mean(int(v['TotalCommitted'])for v in values)))
result=dict(DriverVersions=data['DisplayDrivers'],GpuAllocations=summary,TargetProcessId=data['ProcessId'],
            ObservedTargetAdapters=[s['Adapter']for s in summary if s['ProcessId']==data['ProcessId']],
            AcquisitionRequiredForFailure=False,CompletePerFrameCallTraceAvailable=False,
            Interpretation='Adapter identity established for GPU allocations; API call chain remains an inference from modules and allocation evidence. Idle engine samples do not prove which engine executed prior frames.')
(folder/'gpu-route-summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
