from pathlib import Path
from datetime import datetime
import csv,json,hashlib,statistics,sys
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/default-redraw-20261015'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
stamp=lambda value:datetime.fromisoformat(value.replace('Z','+00:00'))
summary={}
for label in ['Default-1','SoftwareOnly-1','no-capture-default']:
    folder=out/label;resource=out/(label+'-resource')
    if not (resource/'idle-segment.json').exists():continue
    segment=read(resource/'idle-segment.json')
    with (resource/'samples.csv').open(encoding='utf-8-sig',newline='')as stream:
        samples=[r for r in csv.DictReader(stream)if r['Stage']=='post-ops-idle-60' and stamp(segment['IdleStartUtc'])<=stamp(r['Timestamp'])<=stamp(segment['IdleEndUtc'])]
    assert len(samples)>2
    first,last=samples[0],samples[-1];seconds=(float(last['ElapsedMs'])-float(first['ElapsedMs']))/1000
    cpu=float(last['CpuSeconds'])-float(first['CpuSeconds'])
    cpu_intervals=[100*(float(b['CpuSeconds'])-float(a['CpuSeconds']))/((float(b['ElapsedMs'])-float(a['ElapsedMs']))/1000)/int(b['Processors'])for a,b in zip(samples,samples[1:])]
    metrics={key:dict(Start=float(first[key]),End=float(last[key]),Delta=float(last[key])-float(first[key]),Mean=statistics.mean(float(r[key])for r in samples),Max=max(float(r[key])for r in samples)) for key in ['PrivateBytes','WorkingSet','Handles']}
    ready=read(folder/'ready.json');observations=read(folder/'observations.json')
    response=[]
    for snapshot in observations['Snapshots']:
        if not snapshot['Stage'].endswith('-ContextIdle'):continue
        kind=snapshot['Stage'].removesuffix('-ContextIdle')
        events=[e for e in observations['Events']if e['Kind']==kind and e['ElapsedMs']<=snapshot['ElapsedMs']]
        if events:response.append(dict(Stage=kind,HandlerToContextIdleMs=snapshot['ElapsedMs']-events[-1]['ElapsedMs'],PhysicalPresentationMeasured=False))
    image=folder/'logical-visual-bitmap.png'
    summary[label]=dict(Ready=ready,IdleSegment=segment,Count=len(samples),SampledSeconds=seconds,CpuSeconds=cpu,CpuMeanAllLogicalPercent=100*cpu/seconds/int(last['Processors']),CpuPeakAllLogicalPercent=max(cpu_intervals),CpuFinalIntervalAllLogicalPercent=cpu_intervals[-1],Metrics=metrics,
        Snapshots=observations['Snapshots'],UiDispatchMeasurements=response,LogicalBitmapSHA256=hashlib.sha256(image.read_bytes()).hexdigest()if image.exists()else None,
        Scope='Initial no-capture reference; no counter/background/bitmap operations'if label=='no-capture-default'else 'Captured minimal WPF; after counter/background/bitmap operations')
a,b=summary['Default-1'],summary['SoftwareOnly-1']
for key in ['Runtime','DpiX','DpiY','Width','Height','DwmComposition']:assert a['Ready'][key]==b['Ready'][key]
assert a['LogicalBitmapSHA256']==b['LogicalBitmapSHA256']
(out/'performance-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,axes=plt.subplots(3,1,figsize=(10,8),sharex=True)
for label in ['Default-1','SoftwareOnly-1']:
    with(out/(label+'-resource')/'samples.csv').open(encoding='utf-8-sig',newline='')as stream:rows=[r for r in csv.DictReader(stream)if r['Stage']=='post-ops-idle-60']
    base=float(rows[0]['ElapsedMs']);times=[(float(r['ElapsedMs'])-base)/1000 for r in rows]
    axes[0].plot(times,[float(r['PrivateBytes'])/2**20 for r in rows],label=label)
    axes[1].plot(times,[float(r['Handles'])for r in rows],label=label)
    cpu=[100*(float(b['CpuSeconds'])-float(a['CpuSeconds']))/((float(b['ElapsedMs'])-float(a['ElapsedMs']))/1000)/int(b['Processors'])for a,b in zip(rows,rows[1:])]
    axes[2].plot(times[1:],cpu,label=label)
axes[0].set_ylabel('Private Bytes MiB');axes[1].set_ylabel('Handles');axes[2].set_ylabel('CPU % / all logical');axes[2].set_xlabel('Seconds in post-operation idle')
for axis in axes:axis.grid(alpha=.25);axis.legend()
fig.suptitle('Diagnostic minimal WPF, 30 s post-operation warmup + 60 s observation; one pair')
fig.tight_layout();fig.savefig(out/'post-ops-idle.png',dpi=120);plt.close(fig)
print(json.dumps({k:dict(Seconds=v['SampledSeconds'],Cpu=v['CpuMeanAllLogicalPercent'],PrivateMeanMiB=v['Metrics']['PrivateBytes']['Mean']/2**20,InputUnchanged=v['IdleSegment']['InputTickUnchanged'])for k,v in summary.items()}))
