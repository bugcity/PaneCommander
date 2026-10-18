"""Analyze new rendering evidence without copying historical acceptance claims."""
import csv
import json
import statistics as st
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

base = ROOT / 'docs/verification/paste-authorized-20261015'
source = base / 'render-comparison'
dest = base / 'render-analysis'
dest.mkdir(exist_ok=True)

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def distribution(values):
    values = [v for v in values if v is not None]
    return None if not values else dict(median=st.median(values), min=min(values), max=max(values), stddev=st.pstdev(values))

rows, series, checks, configurations = [], {}, [], set()
for path in sorted(source.glob('T[1-9]-[AB]-[1-3]')):
    if not (path / 'monitor-summary.json').exists():
        continue
    summary, ready, done = (read(path / name) for name in ('monitor-summary.json', 'ready.json', 'completed.json'))
    scenario, mode, repeat = path.name.split('-')
    if scenario!='T1': configurations.add(json.dumps(ready['settings'],sort_keys=True))
    with (path / 'samples.csv').open(encoding='utf-8-sig', newline='') as f:
        samples = [{k: v if k == 'Phase' else float(v) if v else None for k,v in r.items()} for r in csv.DictReader(f)]
    series[path.name] = samples
    final = next(c for c in done['checkpoints'] if c['stage'] == 'final')
    first = next(c for c in done['checkpoints'] if c['stage'] == 'baseline')
    row = dict(run=path.name, scenario=scenario, mode=mode, repeat=int(repeat), **{k:v for k,v in summary.items() if isinstance(v,(int,float,bool))})
    for label, key in [('layout','layoutReadyMs'), ('renderingEvent','renderingEventMs')]:
        for name, value in (distribution([m[key] for m in done['measurements']]) or {}).items():
            row[label + name.title() + 'Ms'] = value
    row['operationCount'] = len(done['measurements'])
    row['frameTimeouts'] = sum(m['frameTimeout'] for m in done['measurements'])
    for name, value in (distribution(done['uiThreadQueueDelayMs']) or {}).items():
        row['uiQueue'+name.title()+'Ms'] = value
    for phase in ('operations','idle'):
        s = [p for p in samples if p['Phase'] == phase]
        row[phase+'CpuAllLogical'] = 100*(s[-1]['CpuSeconds']-s[0]['CpuSeconds'])/(s[-1]['Seconds']-s[0]['Seconds'])/summary['logicalProcessors'] if len(s)>1 else None
    for phase in ('baseline','recovery'):
        s = [p for p in samples if p['Phase'] == phase]
        row[phase+'TailPrivateMB'] = st.median(p['PrivateBytes']/1e6 for p in s if p['Seconds'] >= s[-1]['Seconds']-1) if s else None
    row['residualPrivateMB'] = row['recoveryTailPrivateMB']-row['baselineTailPrivateMB'] if row['recoveryTailPrivateMB'] is not None and row['baselineTailPrivateMB'] is not None else None
    row.update(tabs=final['tabs'], entries=final['entries'], realizedRows=final['realizedRows'], managedFinalMB=final['managedBytesWithoutGc']/1e6,
               gcHeapMBAtLastGc=final['gcHeapSizeBytesAtLastGc']/1e6, gcCommittedMBAtLastGc=final['gcCommittedBytesAtLastGc']/1e6,
               gcIndex=final['gcIndex'], privateDeltaMB=(samples[-1]['PrivateBytes']-samples[0]['PrivateBytes'])/1e6)
    phases=done['phases']
    idle_duration=next((b['elapsedSeconds']-a['elapsedSeconds'] for a,b in zip(phases,phases[1:]) if a['phase']=='idle'),None)
    valid=summary['forcedGc'] is False and done['forcedGc'] is False and ready['warmupSeconds']==10 and ready['renderPreference']==('Default' if mode=='A' else 'SoftwareOnly')
    valid=valid and ready['Width']==1280 and ready['Height']==800 and ready['power']['acLineStatus']==done['power']['acLineStatus']==1
    expected_tabs=0 if scenario=='T1' else 6 if scenario in ('T3','T6') else 2
    expected_entries=0 if scenario=='T1' else 1536 if scenario in ('T3','T6') else 10256 if scenario in ('T4','T9') else 512
    valid=valid and final['tabs']==expected_tabs and final['entries']==expected_entries
    if scenario=='T5': valid=valid and idle_duration>=300
    if scenario in ('T6','T7','T8','T9'): valid=valid and row['operationCount']==100
    rows.append(row)
    checks.append(dict(run=path.name, valid=bool(valid), dpi=[ready['dpiX'],ready['dpiY']], runtime=ready['runtime'], foreground=ready['IsActive'], idleSeconds=idle_duration))

if len(rows)!=54 or not all(c['valid'] for c in checks):
    raise SystemExit(f'Dataset incomplete/invalid: {len(rows)}/54')
if len(configurations)!=1:
    raise SystemExit('Display settings differ between trials')
integrity=read(source/'integrity.json')
if not integrity['FrozenBinariesUnchanged']:
    raise SystemExit('Binary integrity failed')
order=read(source/'run-order.json')
expected=[(f'T{n}',m,r) for n in range(1,10) for r in range(1,4) for m in ('A','B')]
if [(r['Scenario'],r['Mode'],r['Repeat']) for r in order]!=expected:
    raise SystemExit('Order mismatch')
if len({tuple(c['dpi']) for c in checks})!=1 or len({c['runtime'] for c in checks})!=1:
    raise SystemExit('DPI/runtime mismatch')
fields=list(dict.fromkeys(k for row in rows for k in row))
with (dest/'runs.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=fields);writer.writeheader();writer.writerows(rows)
aggregates=[]
metrics=['privateMeanMB','privateMaxMB','privateFinalMB','workingSetMeanMB','workingSetMaxMB','handlesDelta','cpuMeanAllLogical','cpuPeakSampleAllLogical','operationsCpuAllLogical','idleCpuAllLogical','layoutMedianMs','layoutMaxMs','uiQueueMaxMs','residualPrivateMB','gpuSharedMaxMB','gpuDedicatedMaxMB']
for n in range(1,10):
    for mode in ('A','B'):
        group=[r for r in rows if r['scenario']==f'T{n}' and r['mode']==mode]
        for metric in metrics:
            s=distribution([r.get(metric) for r in group])
            if s: aggregates.append(dict(scenario=f'T{n}',mode=mode,metric=metric,**s))
with (dest/'aggregate.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=['scenario','mode','metric','median','min','max','stddev']);w.writeheader();w.writerows(aggregates)
(dest/'validation.json').write_text(json.dumps(dict(checks=checks,integrity=integrity,forcedGc=False),indent=2),encoding='utf-8')

for metric, ylabel, name in [('PrivateBytes','Private Bytes (MB)','memory'),('CpuPercentAllLogical','CPU (% all logical CPUs)','cpu')]:
    fig,axes=plt.subplots(3,3,figsize=(14,10))
    for n,ax in enumerate(axes.flat,1):
        for mode,color in [('A','#1f77b4'),('B','#ff7f0e')]:
            for repeat in range(1,4):
                s=series[f'T{n}-{mode}-{repeat}']
                ax.plot([r['Seconds'] for r in s],[r[metric]/1e6 if metric=='PrivateBytes' else r[metric] for r in s],color=color,alpha=.55,label=('Default' if mode=='A' else 'SoftwareOnly') if repeat==1 else None)
        ax.set_title(f'T{n}');ax.set_xlabel('Seconds');ax.set_ylabel(ylabel);ax.grid(alpha=.2)
    axes[0,0].legend();fig.tight_layout();fig.savefig(dest/f'{name}-timeseries.png',dpi=150);plt.close(fig)

def stat(scenario,mode,metric):
    a=next((r for r in aggregates if r['scenario']==scenario and r['mode']==mode and r['metric']==metric),None)
    return '未測定' if a is None else f"{a['median']:.4f} [{a['min']:.4f}–{a['max']:.4f}] σ={a['stddev']:.4f}"
def median(scenario,mode,metric):
    return next(r['median'] for r in aggregates if r['scenario']==scenario and r['mode']==mode and r['metric']==metric)

lines=['# Paste改修後の描画方式比較（専用ドライバー）','',
       '2026-10-15。T1〜T9 × Default/SoftwareOnly × 3反復、54独立プロセス。10秒ウォームアップ、T5は300秒アイドル、強制GCなし。A/Bを交互に実行。環境・ハッシュ・実行順・生データは ../render-comparison/、全統計は runs.csv / aggregate.csv / validation.json。メモリは十進MB、CPUは全論理CPU数で正規化。', '',
       '実際のWPF MainWindowを使う専用ドライバーであり、製品EXEや実マウス入力、Shellポップアップの測定ではない。モデルにファイル操作サービスは接続しない。GUI自動操作・キャプチャ・ビルドを観測区間に重ねていない。前面状態はvalidationに記録し、外部アプリ・OS全体の負荷まで厳密固定してはいない。', '',
       'layoutはDispatcherのレイアウト／ApplicationIdle到達、Rendering通知はCompositionTarget.Renderingまでの時間で、物理画面へのpresent完了を示さない。キャプチャ時間ではない。GC Heap / Committedは自然GC最終時点の値で、現在の生存ヒープを強制GCして求めた値ではない。', '',
       '|試験|方式|Private平均 MB|Private最大 MB|Private終了 MB|WS平均 MB|CPU平均 %|ハンドル増分|', '|---|---|---|---|---|---|---|---|']
for n in range(1,10):
    for mode in ('A','B'):
        lines.append('|'+f'T{n}|'+('Default' if mode=='A' else 'SoftwareOnly')+'|'+'|'.join(stat(f'T{n}',mode,m) for m in ['privateMeanMB','privateMaxMB','privateFinalMB','workingSetMeanMB','cpuMeanAllLogical','handlesDelta'])+'|')
lines += ['', '各セルは各回統計の3回中央値 [最小–最大] と3回間の標準偏差。個々のピーク・CPU秒・GPU・GC・実体行数・残留はruns.csvで別記。', '', '## 操作応答とCPU', '', '|試験|方式|layout中央値 ms|layout最大 ms|操作中CPU %|UIキュー最大 ms|操作後残留 MB|','|---|---|---|---|---|---|---|']
for n in range(6,10):
    for mode in ('A','B'):
        lines.append('|'+f'T{n}|'+('Default' if mode=='A' else 'SoftwareOnly')+'|'+'|'.join(stat(f'T{n}',mode,m) for m in ['layoutMedianMs','layoutMaxMs','operationsCpuAllLogical','uiQueueMaxMs','residualPrivateMB'])+'|')
lines += ['', '残留は回復末尾1秒のPrivate中央値−操作前末尾1秒。短い回復待ちでキャッシュ・未回収領域も残り、残留だけでリークや所有者を断定しない。GPUカウンターの割当量はruns.csvに記録するが、Private差をそのままGPUドライバー使用量とはしない。', '',
          '![Memory](memory-timeseries.png)', '', '![CPU](cpu-timeseries.png)', '', '## 結論', '',
          f"2→6タブのPrivate平均の中央値差はDefault {median('T3','A','privateMeanMB')-median('T2','A','privateMeanMB'):+.2f} MB、SoftwareOnly {median('T3','B','privateMeanMB')-median('T2','B','privateMeanMB'):+.2f} MB。6タブのSoftwareOnlyによる削減は {median('T3','A','privateMeanMB')-median('T3','B','privateMeanMB'):.2f} MB。", '',
          '300秒アイドルCPU：Default '+stat('T5','A','idleCpuAllLogical')+' %、SoftwareOnly '+stat('T5','B','idleCpuAllLogical')+' %。', '',
          'タブ切替layout中央値：Default '+stat('T6','A','layoutMedianMs')+' ms、SoftwareOnly '+stat('T6','B','layoutMedianMs')+' ms。フォルダー移動はDefault '+stat('T7','A','layoutMedianMs')+' ms、SoftwareOnly '+stat('T7','B','layoutMedianMs')+' ms。', '',
          '結論：判断保留。これらの数値だけでは実表示品質・操作の滑らかさ・製品Shellホストの反復寿命・長期安定性を合格にできない。両方式の実GUI操作証跡はPASTE_GUI_ACCEPTANCE_REPORT.mdに別記。製品の既定描画方式は変更しない。']
lines += ['', '## 暫定メモリ目標との照合', '',
          '従来の測定方針に合わせPrivate Bytes最大、十進MBで照合する。専用ドライバーなので製品全体の受入合格には転用しない。T1の空画面を製品起動150MB目標の試験にしない。T9は1万件の操作条件として300MBで照合。Working Setはruns.csvへ別記。', '',
          '|条件|方式|全3回の最大 MB|目標 MB|条件内の結果|','|---|---|---|---|---|']
for n in (2,3,4,6,7,8,9):
    limit=300 if n in (4,9) else 200
    for mode in ('A','B'):
        peak=max(r['privateMaxMB'] for r in rows if r['scenario']==f'T{n}' and r['mode']==mode)
        lines.append(f"|T{n}|{'Default' if mode=='A' else 'SoftwareOnly'}|{peak:.2f}|{limit}|{'未達' if peak>limit else 'この条件内で目標以下'}|")
lines += ['', '結論：判断保留']
(dest/'REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print('Validated 54 trials; fresh report and charts written:',dest)
