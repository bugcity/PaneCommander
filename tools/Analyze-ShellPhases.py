"""Analyse segmented diagnostics; never translate Private Bytes into leak ownership."""
import csv
import json
import statistics
import sys
from pathlib import Path

root=Path(__file__).resolve().parents[1]
base=root/'docs/verification/shell-lifetime-20261016'
source=base/(sys.argv[1] if len(sys.argv)>1 else 'exploratory')
out=source/'analysis';out.mkdir(exist_ok=True)
rows=[]
def slope(x,y):
    mx,my=statistics.mean(x),statistics.mean(y)
    d=sum((v-mx)**2 for v in x)
    return sum((a-mx)*(b-my) for a,b in zip(x,y))/d if d else 0
for folder in sorted(source.iterdir()):
    if not folder.is_dir() or not (folder/'conditions.json').exists():continue
    c=json.loads((folder/'conditions.json').read_text(encoding='utf-8-sig'))
    with (folder/'samples.csv').open(encoding='utf-8-sig',newline='') as f:
        samples=[{k:(v if k=='Phase' else float(v)) for k,v in r.items()} for r in csv.DictReader(f)]
    for phase in dict.fromkeys(r['Phase'] for r in samples):
        group=[r for r in samples if r['Phase']==phase];a,b=group[0],group[-1]
        if phase.startswith('block-'):assert len(group)==c['CountPerBlock']+1
        seconds=(b['ElapsedMs']-a['ElapsedMs'])/1000
        rows.append(dict(run=folder.name,kind=c['Kind'],mode=c['Mode'],logging=c['Logging'],phase=phase,
            samples=len(group),seconds=seconds,privateDeltaMB=(b['PrivateBytes']-a['PrivateBytes'])/1e6,
            privateMeanMB=statistics.mean(r['PrivateBytes'] for r in group)/1e6,
            privateMaxMB=max(r['PrivateBytes'] for r in group)/1e6,privateEndMB=b['PrivateBytes']/1e6,
            privateSlopeBytesPerOp=slope([r['Iteration'] for r in group],[r['PrivateBytes'] for r in group]) if phase.startswith('block-') else None,
            privateSlopeMBPerSecond=slope([r['ElapsedMs']/1000 for r in group],[r['PrivateBytes']/1e6 for r in group]),
            workingSetDeltaMB=(b['WorkingSet']-a['WorkingSet'])/1e6,
            handlesDelta=b['Handles']-a['Handles'],gdiDelta=b['GdiObjects']-a['GdiObjects'],userDelta=b['UserObjects']-a['UserObjects'],
            managedDeltaMB=(b['ManagedBytes']-a['ManagedBytes'])/1e6,
            allocatedDeltaMB=(b['TotalAllocated']-a['TotalAllocated'])/1e6,
            gen0Delta=b['Gen0']-a['Gen0'],gen1Delta=b['Gen1']-a['Gen1'],gen2Delta=b['Gen2']-a['Gen2'],
            gcCommittedEndMB=b['GCCommitted']/1e6,gcHeapEndMB=b['GCHeap']/1e6,
            cpuAllLogical=(b['CpuSeconds']-a['CpuSeconds'])/seconds/c['LogicalProcessors']*100 if seconds else 0))
if not rows:raise RuntimeError('No complete runs yet')
with (out/'phases.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
confirmation=source.name=='query-confirmation'
lines=['# 同一プロセスのShell操作区間', '',
    '探索的責務切り分け。初期10秒、ウォームアップ20回、100回操作＋10秒アイドルを3区間。Default指定、GUI Popupなし。製品と同じShellContextMenuを呼ぶ専用EXE。host-onlyだけは製品HostedShellBackgroundの診断用コピーでGetItemObject直前に戻る（provenance参照）。表示されたWPF製品の描画比較ではない。', '',
    'streamはGetProcessMemoryInfo/GetProcessTimes/GetProcessHandleCountとCSV逐次出力、全サンプルをプロセス内保持しない。legacyは同じ計測に加えて従来Process.Refresh・サンプルList保持を追加した対照で、旧ドライバー完全同一ではない。全条件共通の出力・カウンター取得・bounded WeakReferenceリング100件等の計測負荷は残る。アイドル用一回タイマーは診断プロセスだけに存在し、製品には追加しない。', '',
    '|条件|区間|Private増分 MB|Private傾き KB/操作|管理領域増分 MB|累積管理割当増分 MB|Gen0/1/2回数|ハンドル差|CPU %|',
    '|---|---|---:|---:|---:|---:|---|---:|---:|']
for r in rows:
    if not r['phase'].startswith(('block-','idle-1','idle-2','idle-3')):continue
    s='—' if r['privateSlopeBytesPerOp'] is None else f"{r['privateSlopeBytesPerOp']/1000:.3f}"
    lines.append(f"|{r['run']}|{r['phase']}|{r['privateDeltaMB']:.3f}|{s}|{r['managedDeltaMB']:.3f}|{r['allocatedDeltaMB']:.3f}|{r['gen0Delta']:.0f}/{r['gen1Delta']:.0f}/{r['gen2Delta']:.0f}|{r['handlesDelta']:.0f}|{r['cpuAllLogical']:.4f}|")
lines += ['', '傾きは区間内の最小二乗回帰。Privateの増分から管理領域を差し引いてネイティブ所有量を算出しない。GCHeap/Committedは最後の自然GC情報で、Gen回数0なら0を未使用量とは扱わない。弱参照の生存は自然GCが未発生なら強参照保持の証拠ではない。減少しないことだけでリークとせず、3区間の再利用と自然GCの有無を照合する。', '']
if confirmation:
    lines += ['同一診断ビルドでfile-context / fileを各3独立プロセス。第2反復は順序を逆転。製品Infrastructureは同じ。context対照はQueryContextMenuを省略する診断コピー（context-control-provenance.json）であり、製品の機能を無効にしていない。3反復が完了した組だけ中央値 [最小–最大] を集計する。', '',
              '|条件|区間|Private増分 MB 中央値 [範囲]|傾き KB/操作 中央値 [範囲]|','|---|---|---:|---:|']
    for kind in ('file-context','file'):
        for phase in ('block-1','block-2','block-3'):
            group=[r for r in rows if r['kind']==kind and r['phase']==phase]
            if len(group)!=3:continue
            def interval(key, divisor=1):
                v=[r[key]/divisor for r in group];return f'{statistics.median(v):.3f} [{min(v):.3f}–{max(v):.3f}]'
            lines.append(f"|{kind}|{phase}|{interval('privateDeltaMB')}|{interval('privateSlopeBytesPerOp',1000)}|")
    lines += ['', 'この確認はネイティブ取得経路の責務を絞るもので、可視WPFのDefault/SoftwareOnly比較・実Popup受入・長期安定性の合格ではない。']
else:
    lines += ['未完了の条件はこの表に含めない。1条件1プロセスの探索段階で、3独立反復・長期安定・実GUI・描画方式比較の合格ではない。']
(out/'REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,axes=plt.subplots(3,1,figsize=(11,9),sharex=True)
for folder in sorted(source.iterdir()):
    if not folder.is_dir() or not (folder/'conditions.json').exists():continue
    with (folder/'samples.csv').open(encoding='utf-8-sig',newline='') as f: data=list(csv.DictReader(f))
    x=[float(r['ElapsedMs'])/1000 for r in data]
    for ax,key in zip(axes,['PrivateBytes','ManagedBytes','TotalAllocated']):ax.plot(x,[float(r[key])/1e6 for r in data],label=folder.name)
for ax,label in zip(axes,['Private Bytes (MB)','Managed estimate (MB)','Cumulative managed allocated (MB)']):ax.set_ylabel(label);ax.grid(alpha=.2)
axes[0].legend(fontsize=8,loc='upper left',bbox_to_anchor=(1.01,1));axes[2].set_xlabel('Elapsed seconds (different operation rates)')
fig.tight_layout(rect=(0,0,.80,1));fig.savefig(out/'timeseries.png',dpi=140);plt.close(fig)
print(out/'REPORT.md')
