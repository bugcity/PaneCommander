from pathlib import Path
from datetime import datetime
import csv,json,hashlib,statistics
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/shell-context/no-screen-off'
read=lambda path:json.loads(path.read_text(encoding='utf-8-sig'))
summary={}
for mode in ['Default','SoftwareOnly','SoftwareOnly-functional']:
    folder=out/(mode+'-resource')
    if not(folder/'samples.csv').exists():continue
    with(folder/'samples.csv').open(encoding='utf-8-sig',newline='')as stream:rows=list(csv.DictReader(stream))
    by_stage={}
    groups={stage:[r for r in rows if r['Stage']==stage] for stage in sorted({r['Stage']for r in rows})}
    idle=groups.pop('idle-360',[])
    if idle:
        base=float(idle[0]['ElapsedMs']);end=float(idle[-1]['ElapsedMs'])
        groups['idle-first-360']=[r for r in idle if float(r['ElapsedMs'])<=base+360000]
        groups['idle-full-observation']=idle
        groups['idle-final-60']=[r for r in idle if float(r['ElapsedMs'])>=end-60000]
    for stage,samples in groups.items():
        if len(samples)<2:continue
        first,last=samples[0],samples[-1]
        seconds=(float(last['ElapsedMs'])-float(first['ElapsedMs']))/1000
        cpu=float(last['CpuSeconds'])-float(first['CpuSeconds'])
        metrics={key:dict(Start=float(first[key]),End=float(last[key]),Delta=float(last[key])-float(first[key]),Mean=statistics.mean(float(r[key])for r in samples),Max=max(float(r[key])for r in samples)) for key in ['PrivateBytes','WorkingSet','Handles']}
        by_stage[stage]=dict(Count=len(samples),StartUtc=first['Timestamp'],EndUtc=last['Timestamp'],Seconds=seconds,CpuSeconds=cpu,CpuMeanAllLogicalPercent=100*cpu/seconds/int(last['Processors']),Metrics=metrics)
    summary[mode]=by_stage
(out/'performance-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps({m:{stage:{'Seconds':s['Seconds'],'CpuPercent':s['CpuMeanAllLogicalPercent'],'PrivateDeltaMiB':s['Metrics']['PrivateBytes']['Delta']/2**20,'HandlesDelta':s['Metrics']['Handles']['Delta']}for stage,s in v.items()}for m,v in summary.items()}))
