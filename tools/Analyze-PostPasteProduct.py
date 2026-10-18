"""Summarize the actual product EXE comparison separately from the WPF driver."""
import csv
import json
import statistics as st
from pathlib import Path

root=Path(__file__).resolve().parents[1]
base=root/'docs/verification/paste-authorized-20261015/product-comparison'
read=lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
rows=[];settings=set();geometry=set()
for scenario in ('two-tabs','six-tabs','large'):
    for repeat in range(1,4):
        for mode in ('Default','SoftwareOnly'):
            path=base/f'{scenario}-{mode}-{repeat}'
            s,r=read(path/'summary.json'),read(path/'ready.json')
            assert s['productionExecutable'] and s['forcedGc'] is False and s['savedTabStateMatchesReady'] and s['profileFilesUnchangedDuringObservation']
            assert r['modeBeforeWindow']==r['effectiveRenderMode']==mode
            assert r['tabs']==(6 if scenario=='six-tabs' else 2) and r['folderViews']==2
            assert [i['count'] for i in r['statuses']]==([10000,256] if scenario=='large' else [256]*(6 if scenario=='six-tabs' else 2))
            assert all(i['NavigationSucceeded'] for i in r['statuses']) and r['warmupSeconds']==10
            config=dict(r['settings']);config.pop('RenderingMode');settings.add(json.dumps(config,sort_keys=True))
            geometry.add((r['dpiX'],r['dpiY'],r['Width'],r['Height']))
            rows.append(dict(run=path.name,scenario=scenario,repeat=repeat,**{k:v for k,v in s.items() if isinstance(v,(int,float,str,bool)) or v is None},
                             folderViews=r['folderViews'],realizedRows=r['realizedRows'],tabs=r['tabs'],loadedTabs=r['loadedTabs'],managedReadyMB=r['managedBytesWithoutGc']/1e6,
                             gcHeapMBAtLastGc=r['gcHeapAtLastGc']/1e6,gcCommittedMBAtLastGc=r['gcCommittedAtLastGc']/1e6))
assert len(rows)==18 and len(settings)==1 and len(geometry)==1 and read(base/'integrity.json')['FrozenBinariesUnchanged']
with (base/'runs.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(dict.fromkeys(k for r in rows for k in r)));w.writeheader();w.writerows(rows)
aggregates=[]
metrics=['startupPrivateMaxMB','privateMeanMB','privateMaxMB','privateFinalMB','workingSetMeanMB','workingSetMaxMB','cpuMeanAllLogical','cpuPeakSampleAllLogical','handlesDelta','gpuSharedMaxMB','gpuDedicatedMaxMB']
for scenario in ('two-tabs','six-tabs','large'):
    for mode in ('Default','SoftwareOnly'):
        group=[r for r in rows if r['scenario']==scenario and r['mode']==mode]
        for metric in metrics:
            values=[r[metric] for r in group if r[metric] is not None]
            if values: aggregates.append(dict(scenario=scenario,mode=mode,metric=metric,median=st.median(values),min=min(values),max=max(values),stddev=st.pstdev(values)))
with (base/'aggregate.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=['scenario','mode','metric','median','min','max','stddev']);w.writeheader();w.writerows(aggregates)
def value(scenario,mode,metric): return next(a for a in aggregates if a['scenario']==scenario and a['mode']==mode and a['metric']==metric)
def cell(scenario,mode,metric):
    a=value(scenario,mode,metric);return f"{a['median']:.4f} [{a['min']:.4f}–{a['max']:.4f}] σ={a['stddev']:.4f}"
lines=['# Paste改修後の製品EXEメモリ比較','',
       '同じRelease製品EXE、独立プロファイル、同じ256件のフォルダー（大量表示は左10000件・右256件）、1280×800 DIP、同一DPI。2/6タブ・大量表示それぞれDefault→SoftwareOnlyを交互に3反復、18独立プロセス。既存のopt-in StartupDiagnosticsで全タブを一度表示した後10秒ウォームアップ、外部モニターで10秒観測。強制GC・GUI入力・キャプチャなし。設定・セッションは試験専用。製品の通常起動に診断処理を追加していない。', '',
       '起動最大はプロセス作成〜ready（ウォームアップを含む）のサンプル最大。StartupDiagnosticsのJSON作成・列挙診断・停止イベント等の計測用処理を含み、無計装の起動そのものではない。10秒観測のCPUを製品の300秒アイドルや長期安定性の合格にしない。専用WPFドライバーは起動経路・操作サービス・アクティブ状態が異なり、差を特定コンポーネントの所有量とは断定しない。', '',
       'メモリは十進MB、CPUは全24論理CPUで正規化。表は各回統計の3回中央値 [最小–最大] と標準偏差。生CSV・ready・summary・conditions・binary-manifest・integrityを各出力ディレクトリに保存。', '',
       '|条件|方式|起動Private最大 MB|Private平均 MB|Private最大 MB|Private終了 MB|WS平均 MB|CPU平均 %|','|---|---|---|---|---|---|---|---|']
for scenario in ('two-tabs','six-tabs','large'):
    for mode in ('Default','SoftwareOnly'):
        lines.append('|'+scenario+'|'+mode+'|'+'|'.join(cell(scenario,mode,m) for m in ['startupPrivateMaxMB','privateMeanMB','privateMaxMB','privateFinalMB','workingSetMeanMB','cpuMeanAllLogical'])+'|')
lines += ['', '## タブと描画オブジェクト', '',
          '全18回で可視ツリーのfolderViews=2。6タブ条件もloadedTabs=6だがfolderViewsは2で、表示中の行だけが実体化されている（詳細はruns.csv）。これは可視ツリーの計数であり、全てのネイティブ／GPU割当の解放や、非表示の切り離された全オブジェクトの不存在を単独で証明しない。', '',
          '2→6タブのPrivate平均の中央値差：Default '+f"{value('six-tabs','Default','privateMeanMB')['median']-value('two-tabs','Default','privateMeanMB')['median']:+.2f} MB、SoftwareOnly {value('six-tabs','SoftwareOnly','privateMeanMB')['median']-value('two-tabs','SoftwareOnly','privateMeanMB')['median']:+.2f} MB。", '',
          '6タブのSoftwareOnlyによるPrivate平均削減：'+f"{value('six-tabs','Default','privateMeanMB')['median']-value('six-tabs','SoftwareOnly','privateMeanMB')['median']:.2f} MB。", '',
          '## 暫定目標', '', '|方式|全条件の起動Private最大 MB|150MB目標（計測条件内）|','|---|---|---|']
for mode in ('Default','SoftwareOnly'):
    peak=max(r['startupPrivateMaxMB'] for r in rows if r['mode']==mode)
    lines.append(f"|{mode}|{peak:.2f}|{'未達' if peak>150 else 'この条件内で目標以下'}|")
lines += ['', '通常表示200MB・大量表示300MBは各回のprivateMaxMBで照合できるが、本測定は操作やShellメニューを含まず、日常利用全般の合格ではない。専用ドライバーではDefaultの切替・移動・タブ作成で200MB超過があり、別報告へ記録した。', '',
          'Shell背景ホストを開閉していないため、その追加コストをこの表から評価しない。クリップボード復元、Shell拡張全般、実Popup寿命は別の受入項目。', '', '結論：判断保留']
(base/'REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print('Validated 18 actual-product trials:',base)
