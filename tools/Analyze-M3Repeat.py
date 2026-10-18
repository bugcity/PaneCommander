"""Report the separate WPF-driver repetition checks; never mix with product EXE runs."""
from pathlib import Path
import argparse, csv, hashlib, json, math, shutil, statistics, sys

ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(); p.add_argument('--input',default='artifacts/m3-repeat-verified'); args=p.parse_args()
source=ROOT/args.input; dest=ROOT/'docs/verification/m3/repeat'; dest.mkdir(parents=True,exist_ok=True)
def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def median(xs): return statistics.median(xs)
def percentile(xs,q):
    xs=sorted(xs); pos=(len(xs)-1)*q; lo=math.floor(pos); hi=math.ceil(pos)
    return xs[lo]+(xs[hi]-xs[lo])*(pos-lo)
def csv_out(path,rows):
    with path.open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,list(dict.fromkeys(k for r in rows for k in r))); w.writeheader(); w.writerows(rows)
rows=[]; timeseries={}
for scenario in ['T6','T8']:
    for mode in ['A','B']:
        for repeat in range(1,4):
            folder=source/f'{scenario}-{mode}-{repeat}'
            ready,done,summary=[load(folder/n) for n in ['ready.json','completed.json','monitor-summary.json']]
            assert ready['renderPreference']==('Default' if mode=='A' else 'SoftwareOnly')
            assert ready['warmupSeconds']==10 and not ready['offscreen'] and not ready['interactiveDriver']
            assert not done['forcedGc'] and not summary['forcedGc'] and len(done['measurements'])==100
            assert not any(m['frameTimeout'] for m in done['measurements'])
            assert all(c['tabs']==(6 if scenario=='T6' else 2) for c in done['checkpoints'])
            assert all(0<c['realizedRows']<100 for c in done['checkpoints'])
            with (folder/'samples.csv').open(encoding='utf-8-sig') as f:
                samples=[{k:(v if k=='Phase' else float(v) if v else None) for k,v in r.items()} for r in csv.DictReader(f)]
            timeseries[(scenario,mode,repeat)]=samples
            phase={v['phase']:v for v in summary['phases']}
            baseline=[s for s in samples if s['Phase']=='baseline']; recovery=[s for s in samples if s['Phase']=='recovery']
            base_tail=[s['PrivateBytes']/1e6 for s in baseline if s['Seconds']>=baseline[-1]['Seconds']-1]
            recovery_tail=[s['PrivateBytes']/1e6 for s in recovery if s['Seconds']>=recovery[-1]['Seconds']-1]
            last5=[s for s in recovery if s['Seconds']>=recovery[-1]['Seconds']-5]
            layouts=[m['layoutReadyMs'] for m in done['measurements']]
            frames=[m['renderingEventMs'] for m in done['measurements']]
            gc=done['checkpoints'][-1]
            row={'scenario':scenario,'mode':ready['renderPreference'],'repeat':repeat,
                 'privateMaxMB':summary['privateMaxMB'],'privateFinalMB':summary['privateFinalMB'],
                 'baselineTailMedianMB':median(base_tail),'recoveryTailMedianMB':median(recovery_tail),
                 'residualMB':median(recovery_tail)-median(base_tail),
                 'recoveryLast5sRangeMB':(max(s['PrivateBytes'] for s in last5)-min(s['PrivateBytes'] for s in last5))/1e6,
                 'operationCpuAllLogical':phase['operations']['cpuMeanAllLogical'],
                 'operationCpuSeconds':phase['operations']['cpuSeconds'],
                 'cpuPeakSampleAllLogical':summary['cpuPeakSampleAllLogical'],
                 'cpuSeconds':summary['cpuSeconds'],'layoutMedianMs':median(layouts),'layoutP95Ms':percentile(layouts,.95),
                 'layoutMaxMs':max(layouts),'renderEventMedianMs':median(frames),
                 'uiQueueMaxMs':max(done['uiThreadQueueDelayMs']),
                 'gpuSharedMaxMB':summary['gpuSharedMaxMB'],
                 'gcHeapAtLastGcMB':gc['gcHeapSizeBytesAtLastGc']/1e6 if gc['gcIndex'] else None,
                 'gcCommittedAtLastGcMB':gc['gcCommittedBytesAtLastGc']/1e6 if gc['gcIndex'] else None,
                 'managedWithoutGcMB':gc['managedBytesWithoutGc']/1e6,'realizedRows':gc['realizedRows'],'tabs':gc['tabs']}
            rows.append(row); shutil.copytree(folder,dest/'raw'/folder.name,dirs_exist_ok=True)
order=load(source/'run-order.json')
assert load(source/'integrity.json')['FrozenBinariesUnchanged']
assert load(source/'environment.json')['PowerPlan']==load(source/'integrity.json')['PowerPlan']
assert load(source/'environment.json')['GPU']==load(source/'integrity.json')['GPU']
product_manifest=load(ROOT/'docs/verification/m3/product/binary-manifest.json')
repeat_manifest=load(source/'binary-manifest.json')
for name in ['PaneCommander.App.dll','PaneCommander.Core.dll','PaneCommander.Infrastructure.dll']:
    expected={v['SHA256'] for v in product_manifest if Path(v['Path']).name==name}
    actual={v['SHA256'] for v in repeat_manifest if Path(v['Path']).name==name}
    assert len(expected)==1 and actual==expected, f'Different product assemblies: {name}'
assert [(r['Scenario'],r['Mode'],r['Repeat']) for r in order]==[(s,m,r) for s in ['T6','T8'] for r in range(1,4) for m in ['A','B']]
shutil.copy2(source/'run-order.json',dest/'run-order.json'); csv_out(dest/'runs.csv',rows)
for name in ['binary-manifest.json','environment.json','integrity.json']: shutil.copy2(source/name,dest/name)
aggregate=[]
for scenario in ['T6','T8']:
    for mode in ['Default','SoftwareOnly']:
        group=[r for r in rows if r['scenario']==scenario and r['mode']==mode]
        for metric in rows[0]:
            if metric in ['scenario','mode','repeat']: continue
            xs=[r[metric] for r in group if r[metric] is not None]
            aggregate.append(dict(scenario=scenario,mode=mode,metric=metric,median=median(xs) if xs else None,
                                  min=min(xs) if xs else None,max=max(xs) if xs else None,stddev=statistics.pstdev(xs) if xs else None))
csv_out(dest/'aggregate.csv',aggregate)
sys.path.insert(0,str(ROOT/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,axes=plt.subplots(2,2,figsize=(13,8))
for i,scenario in enumerate(['T6','T8']):
    for mode in ['A','B']:
        for repeat in range(1,4):
            data=timeseries[(scenario,mode,repeat)]; label=f'{"Default" if mode=="A" else "SoftwareOnly"}-{repeat}'
            color='#1769aa' if mode=='A' else '#e56b17'
            axes[i,0].plot([s['Seconds'] for s in data],[s['PrivateBytes']/1e6 for s in data],color=color,linestyle=['-','--',':'][repeat-1],label=label)
            axes[i,1].plot([s['Seconds'] for s in data],[s['CpuPercentAllLogical'] for s in data],color=color,linestyle=['-','--',':'][repeat-1])
    for j in [0,1]: axes[i,j].set_title(scenario+(' memory' if j==0 else ' CPU')); axes[i,j].set_xlabel('Seconds after warmup'); axes[i,j].grid(alpha=.2)
    axes[i,0].set_ylabel('Private Bytes (decimal MB)'); axes[i,1].set_ylabel('CPU % of 24 logical processors')
axes[0,0].legend(fontsize=7); fig.tight_layout(); fig.savefig(dest/'timeseries.png',dpi=150); fig.savefig(dest/'timeseries.svg'); plt.close(fig)
def cell(scenario,mode,metric,digits=2):
    r=next(r for r in aggregate if r['scenario']==scenario and r['mode']==mode and r['metric']==metric)
    return '未測定' if r['median'] is None else f"{r['median']:.{digits}f} [{r['min']:.{digits}f}–{r['max']:.{digits}f}]"
def table(metric,digits=2):
    return '| 条件 | Default | SoftwareOnly |\n| --- | --- | --- |\n'+'\n'.join(f'| {s} | {cell(s,"Default",metric,digits)} | {cell(s,"SoftwareOnly",metric,digits)} |' for s in ['T6','T8'])
report=f'''# M3 操作反復試験（専用WPFドライバー）

製品EXEの30回測定とは分離し、同じReleaseの画面・ViewModel・FileSystemServiceを専用ドライバーで検査した。T6は6タブで100回切替、T8はタブ作成・読込・閉鎖を100回反復。各方式3回、A/B交互の12独立プロセス。強制GCなし、ウォームアップ10秒、操作前5秒、回復10秒。お気に入りは空、App.OnStartupと終了時JSON保存は経由しない。実GUIのキー・マウス操作ではない。

数値は各回の指標の中央値 [最小–最大]。全回値と標準偏差は[CSV](docs/verification/m3/repeat/aggregate.csv)。CPUは24論理CPU全体に対する百分率。

## 操作中CPU（%）

{table('operationCpuAllLogical',4)}

## 操作完了点と応答時間

layoutReadyMsはモデルの操作完了後、Dispatcher.ApplicationIdleまでの時間。WPFレイアウト／バインディング処理の測定で、画面へのpresent完了ではない。renderingEventMsはCompositionTarget.Rendering通知と継続処理までで、実表示完了時間ではない。UIキュー遅延は100ms間隔の試験用Input優先度コールバックであり、実入力の遅延ではない。いずれの試験用待機・計測も通常起動には追加されない。

各回のlayoutReady中央値（ms）：

{table('layoutMedianMs')}

各回のlayoutReady p95（ms）：

{table('layoutP95Ms')}

UIキュー遅延の各回最大（ms）：

{table('uiQueueMaxMs')}

## メモリと残留量

Private Bytesの各回最大（MB、1MB=1,000,000bytes）：

{table('privateMaxMB')}

残留量＝回復区間末尾1秒のPrivate Bytes中央値−操作前区間末尾1秒の中央値（MB）。自然GC、WPF／ドライバーの保持を含み、漏れ量そのものではない。

{table('residualMB')}

回復区間末尾5秒の変動幅（MB）：

{table('recoveryLast5sRangeMB')}

全12回で100操作を完了し、Rendering通知待ちタイムアウトは0。終了時のタブ数はT6が6、T8が2。可視行数は100未満を維持。短い回復区間だけでは継続増加なし・8時間安定性を合格と判定できない。Private Bytesの差からネイティブ確保の所有者を断定しない。GC Heap／Committedは最終自然GC時点であり、生存オブジェクト量の確定値ではない。

![メモリ・CPU推移](docs/verification/m3/repeat/timeseries.png)

## 再現

Releaseビルド後、先に同じビルドの製品版測定と`Analyze-M3Product.py`による集計を完了する。続いて`./tools/Compare-Rendering.ps1 -Output artifacts/m3-repeat-new -Scenarios T6,T8 -Repetitions 3`、`python tools/Analyze-M3Repeat.py --input artifacts/m3-repeat-new`。解析時に製品版とのApp/Core/Infrastructure DLLハッシュ一致を検査する。同時に他の性能測定を実行しない。生ログは[証跡](docs/verification/m3/repeat/raw)に保存。

実装・製品版測定は[M3実装報告](M3_IMPLEMENTATION_REPORT.md)と[M3性能報告](M3_PERFORMANCE_REPORT.md)。実GUIの表示品質・操作性と長時間安定性は確認待ち。
'''
(ROOT/'M3_RESOURCE_REPEAT_REPORT.md').write_text(report,encoding='utf-8')
(dest/'validation.json').write_text(json.dumps({'complete':True,'runs':12,'operationsPerRun':100,'forcedGc':False,'actualGuiInput':False},indent=2),encoding='utf-8')
print('Verified 12 separate-driver runs; wrote M3_RESOURCE_REPEAT_REPORT.md')
