"""Analyze frozen review trials; never start/build the product or force GC."""
from pathlib import Path
import csv, hashlib, json, os, statistics, sys
ROOT = Path(__file__).resolve().parents[1]
RUN = Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT / 'artifacts/review-performance-final'
OUT = ROOT / 'docs/verification/review/performance'
OUT.mkdir(parents=True, exist_ok=True)
def read(p): return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
def export(name, rows):
    keys = list(dict.fromkeys(k for r in rows for k in r))
    with (OUT/name).open('w', encoding='utf-8-sig', newline='') as f:
        w=csv.DictWriter(f,keys); w.writeheader(); w.writerows(rows)
def median(values): return statistics.median(values)
def p95(v): return sorted(v)[max(0, int(len(v)*.95+.999999)-1)] if v else 0
def trip(v, digits=2): return f'{median(v):.{digits}f} / {max(v):.{digits}f} / {max(v)-min(v):.{digits}f}'
assert read(RUN/'completed.json')['Completed']
checks=[]
for manifest in RUN.rglob('binary-manifest.json'):
    for entry in read(manifest):
        assert sha(entry['Path']) == entry['SHA256'], entry['Path']
    checks.append(str(manifest.relative_to(ROOT)))
for entry in read(RUN/'after-product/source-manifest-during.json'):
    assert sha(ROOT/entry['Path'])==entry['SHA256'], f"Source changed during trials: {entry['Path']}"
oldEnvironment=read(ROOT/'artifacts/m3-product-verified/environment.json')
newEnvironment=read(RUN/'after-product/environment.json')
for key in ['OS','SDK','CPU','RAM','GPU','PowerPlan','WarmupSeconds','ForcedGc']:
    assert oldEnvironment[key]==newEnvironment[key], f'Environment differs: {key}'
old=read(ROOT/'artifacts/m3-product-verified/binary-manifest.json')
baseline=ROOT/'artifacts/review-baseline/app'
for e in old:
    if '/src/PaneCommander.App/bin/' in e['Path'].replace('\\','/'):
        assert sha(baseline/Path(e['Path']).name)==e['SHA256']
products=[]
for era, folder in [('before',ROOT/'artifacts/m3-product-verified'),('after',RUN/'after-product'),('before-handles',RUN/'before-handles')]:
    for summary in sorted(folder.glob('*/summary.json')):
        s=read(summary); ready=read(summary.parent/'ready.json'); closing=read(summary.parent/'closing.json')
        scenario,mode,rep=summary.parent.name.rsplit('-',2)
        # Render preference is applied before Window; actual GPU participation is not implied.
        assert s['savedTabStateMatchesReady'] and s['profileFilesUnchangedDuringObservation']
        assert ready['modeBeforeWindow']==ready['effectiveRenderMode']==mode
        assert ready['runtime']=='.NET 10.0.12' and ready['dpiX']==ready['dpiY']==1.5
        assert ready['folderViews']==2
        expected=6 if scenario.startswith('six-') else 2
        assert ready['tabs']==expected
        assert ready['realizedRows'] < 150
        row={'Era':era,'Scenario':scenario,'Mode':mode,'Repeat':int(rep),**{k:v for k,v in s.items() if isinstance(v,(int,float,bool)) or v is None},
             'Rows':ready['realizedRows'],'FolderViews':ready['folderViews'], 'LoadedTabs':ready['loadedTabs'],
             'ManagedReadyMB':ready['managedBytesWithoutGc']/1e6,'HeapReadyMB':ready['gcHeapAtLastGc']/1e6,
             'CommittedReadyMB':ready['gcCommittedAtLastGc']/1e6,'ManagedFinalMB':closing['managedBytesWithoutGc']/1e6,
             'HeapFinalMB':closing['gcHeapAtLastGc']/1e6,'CommittedFinalMB':closing['gcCommittedAtLastGc']/1e6,
             'Raw':str(summary.parent.relative_to(ROOT))}
        products.append(row)
export('product-runs.csv',products)
repeats=[]
routing=[]
for era,folder in [('before',RUN/'before-repeat'),('before',RUN/'before-scroll'),('before',RUN/'before-navigation'),('after',RUN/'after-repeat')]:
    for summary in sorted(folder.glob('*/monitor-summary.json')):
        s=read(summary); comp=read(summary.parent/'completed.json'); ready=read(summary.parent/'ready.json'); phases={p['phase']:p for p in s['phases']}
        assert ready['renderPreference']==('Default' if s['mode']=='A' else 'SoftwareOnly')
        routing.append({'Era':era,'Scenario':s['scenario'],'Mode':ready['renderPreference'],'Tier':ready['renderCapabilityTier'],
                        'GraphicsModules':';'.join(x['ModuleName'] for x in ready['modules'] if x['ModuleName'].lower().startswith(('d3d','dxgi','wpfgfx')) or 'warp' in x['ModuleName'].lower()),
                        'GpuDedicatedMaxMB':s['gpuDedicatedMaxMB'],'GpuSharedMaxMB':s['gpuSharedMaxMB'],'GpuEnginePeakPercent':s['gpuEnginePeakPercent']})
        assert not s['forcedGc'] and not comp['forcedGc']
        m=comp['measurements']; assert len(m)==100
        assert comp['uiThreadQueueDelayMs'], 'UI queue probe did not collect data'
        base=phases['baseline']; recovery=phases['recovery']; active=phases['operations']
        r={'Era':era,'Scenario':s['scenario'],'Mode':'Default' if s['mode']=='A' else 'SoftwareOnly','Repeat':int(summary.parent.name.rsplit('-',1)[1]),
           'PrivateMaxMB':s['privateMaxMB'],'PrivateFinalMB':s['privateFinalMB'],
           'ResidualMB':recovery['privateFinalMB']-base['privateFinalMB'],
           'WorkingSetMeanMB':s['workingSetMeanMB'],'WorkingSetMaxMB':s['workingSetMaxMB'],
           'CpuSeconds':s['cpuSeconds'],'OperationCpuPercent':active['cpuMeanAllLogical'],'CpuPeakPercent':s['cpuPeakSampleAllLogical'],
           'OperationCpuSeconds':active['cpuSeconds'],'OperationSeconds':active['durationSeconds'],
           'LayoutMedianMs':median([x['layoutReadyMs'] for x in m]),'LayoutP95Ms':p95([x['layoutReadyMs'] for x in m]),'LayoutMaxMs':max(x['layoutReadyMs'] for x in m),
           'RenderEventMedianMs':median([x['renderingEventMs'] for x in m if x['renderingEventMs'] is not None]) if any(x['renderingEventMs'] is not None for x in m) else None,
           'FrameTimeouts':sum(x['frameTimeout'] for x in m), 'UiQueueP95Ms':p95(comp['uiThreadQueueDelayMs']),
           'HandlesFinal':s.get('handlesFinal'),'HandlesMax':s.get('handlesMax'),
           'HandlesResidual':(recovery['handlesFinal']-base['handlesFinal']) if 'handlesFinal' in base else None,
           'ManagedFinalMB':comp['checkpoints'][-1]['managedBytesWithoutGc']/1e6,
           'HeapFinalMB':comp['checkpoints'][-1]['gcHeapSizeBytesAtLastGc']/1e6,
           'CommittedFinalMB':comp['checkpoints'][-1]['gcCommittedBytesAtLastGc']/1e6,
           'RowsFinal':comp['checkpoints'][-1]['realizedRows'],'Raw':str(summary.parent.relative_to(ROOT))}
        repeats.append(r)
export('repeat-runs.csv',repeats)
export('render-routing.csv',routing)
assert len([r for r in products if r['Era']=='after'])==30
assert len([r for r in repeats if r['Era']=='after'])==42
assert len([r for r in products if r['Era']=='before-handles'])==10
assert len([r for r in repeats if r['Era']=='before'])==24

def group(rows, **values): return [r for r in rows if all(r[k]==v for k,v in values.items())]
def values(rows,key): return [r[key] for r in rows if r.get(key) is not None]
def med(rows,key): return median(values(rows,key))
lines=['# 統合レビュー：改修前後の性能比較','',
    '2026-10-14。製品版と専用ドライバーを別集計。SoftwareOnly既定を維持し、GUIの最終受入は保留。MBは1,000,000 bytes。', '',
    '## 条件と証跡','',
    '同一PC・Windows・.NET・GPUドライバー・2560×1440/165Hz・DPI 150%・バランス電源。1280×800 DIP、通常256項目／大量10,000項目、ウォームアップ10秒、250ms採取。強制GCなし。A(Default)→B(SoftwareOnly)交互、各3回、独立プロセス。CPUは24論理CPU全体を100%として表示。外部モニターの負荷は製品CPUに含めないがシステム競合はあり得る。','',
    '改修前製品の3回比較にはM3の既存測定30回を再利用し、保全した製品ディレクトリの全DLL/EXE/JSONハッシュ一致を検査した。環境情報も同条件。ただし製品の前後測定日は別日で、完全な同時対照ではない。追加したハンドル数の改修前測定は今回各1回・10秒であり、過去の300秒アイドルと混ぜない。改修後は製品30回（300秒アイドル各方式3回）、専用ドライバー42回。改修前のタブ切替・フォルダ移動・開閉・スクロールは同日に新規各6回、計24回を測定。タブ切替・開閉の再測定は改修後の反復に続けて行い、M3の過去レイアウト時間と直接比較して原因を断定しない。','',
    '製品は公開EXEの起動・設定復元を含む。オプトイン診断の待機イベントを含むが通常起動にはタイマーを追加しない。専用ドライバーは同じMainWindow/ViewModel/FileSystemを直接生成し、App起動処理・セッション保存・設定ストア・ファイル操作サービスを経由しない。ファイルコピー等の処理性能や実GUIのマウス／キーボード操作試験ではない。配色・列・フォントの変更もあるため、全体差を単一部品の効果とは断定しない。','',
    '診断のstartupDurationSecondsは10秒のウォームアップを含むready到達時間であり、最初の画面が使えるまでの実表示時間ではない。起動Privateピークはready前100ms間隔で別採取している。','',
    '[全製品測定CSV](docs/verification/review/performance/product-runs.csv)、[反復CSV](docs/verification/review/performance/repeat-runs.csv)。生CSV・ready/closing/completed JSON・環境・ハッシュ・順序は `artifacts/review-performance-final`、旧製品は `artifacts/m3-product-verified`、旧反復は `docs/verification/m3/repeat/raw`。','',
    '`artifacts/review-performance/SUPERSEDED.json`で途中版と指定した改修後測定は集計から除外。完了済みの改修前測定だけをハッシュ照合して再利用し、最終修正版の改修後測定は別ディレクトリで取り直した。','',
    '観測中にsettings.json／session.json／favorites.jsonの更新時刻が変わらないことを確認。全プロセスのファイルI/OのETW測定は未実施であり、不要な読込みを含む完全なI/O不存在の証明ではない。','',
    '## 製品EXE','',
    '各欄は3回の「中央値 / 最大 / 最大−最小」。Private平均・WS平均は各回の観測平均を集計。CPU平均も各回の時間差分から算出。最大CPUは250ms区間であり瞬間ピークではない。','',
    '| 条件 | 方式 | 前 Private平均 MB | 後 Private平均 MB | 後 Private最大 MB | 後 WS平均 MB | 後 CPU平均 % |','|---|---|---:|---:|---:|---:|---:|']
for scenario in ['two','six-restored','six-visited','large','idle']:
    for mode in ['Default','SoftwareOnly']:
        b=group(products,Era='before',Scenario=scenario,Mode=mode); a=group(products,Era='after',Scenario=scenario,Mode=mode)
        lines.append(f"| {scenario} | {mode} | {trip(values(b,'privateMeanMB'))} | {trip(values(a,'privateMeanMB'))} | {trip(values(a,'privateMaxMB'))} | {trip(values(a,'workingSetMeanMB'))} | {trip(values(a,'cpuMeanAllLogical'),4)} |")
lines+=['','Privateの終了値・開始からの残留・起動ピーク・WS最大・CPU累計／区間ピーク・GC Heap／Committed・GPU専用／共有は全測定CSVに個別記録。GC値は直近GC時のスナップショットであり、現在の管理データ量と同一ではない。PrivateとWSは加算しない。GPUカウンターがnullなら未測定、0なら取得した0である。','',
        '| 方式 | 後の起動Private最大 MB（3回中最大、two） | 2→6訪問済のPrivate平均差 MB | 6訪問済：前→後 MB | 300秒CPU平均 % |','|---|---:|---:|---:|---:|']
for mode in ['Default','SoftwareOnly']:
    a2=group(products,Era='after',Scenario='two',Mode=mode); a6=group(products,Era='after',Scenario='six-visited',Mode=mode); b6=group(products,Era='before',Scenario='six-visited',Mode=mode); idle=group(products,Era='after',Scenario='idle',Mode=mode)
    lines.append(f"| {mode} | {max(values(a2,'startupPrivateMaxMB')):.2f} | {med(a6,'privateMeanMB')-med(a2,'privateMeanMB'):.2f} | {med(b6,'privateMeanMB'):.2f} → {med(a6,'privateMeanMB'):.2f} | {med(idle,'cpuMeanAllLogical'):.4f} |")
lines+=['','同日に取り直した改修前（n=1）のPrivate平均も、旧3回の集計とは別に示す。','', '| 条件 | 方式 | 同日改修前 n=1 MB | 改修後 n=3中央値 MB |','|---|---|---:|---:|']
for scenario in ['two','six-restored','six-visited','large']:
    for mode in ['Default','SoftwareOnly']:
        b=group(products,Era='before-handles',Scenario=scenario,Mode=mode); a=group(products,Era='after',Scenario=scenario,Mode=mode)
        lines.append(f"| {scenario} | {mode} | {med(b,'privateMeanMB'):.2f} | {med(a,'privateMeanMB'):.2f} |")
lines+=['','## ハンドル数','',
        '改修前は今回1回、改修後は3回の中央値。終了値と最大値を別記。短時間でありリーク不存在の証明ではない。','',
        '| 条件 | 方式 | 前 終了 / 最大 (n=1) | 後 終了中央値 / 最大 (n=3) | 後 増減中央値 |','|---|---|---:|---:|---:|']
for scenario in ['two','six-restored','six-visited','large','idle']:
    for mode in ['Default','SoftwareOnly']:
        b=group(products,Era='before-handles',Scenario=scenario,Mode=mode); a=group(products,Era='after',Scenario=scenario,Mode=mode)
        lines.append(f"| {scenario} | {mode} | {med(b,'handlesFinal'):.0f} / {max(values(b,'handlesMax')):.0f} | {med(a,'handlesFinal'):.0f} / {max(values(a,'handlesMax')):.0f} | {med(a,'handlesDelta'):.0f} |")
lines+=['','idleの前は10秒、後は300秒であり、ハンドルの時間依存差を直接同条件比較とはしない。全製品起動で一覧Visualは2個、実体化行数は150未満。6タブの全データを保持しても非表示タブ用一覧を追加しない。','',
        '## 管理メモリとGPUカウンター','',
        '改修後3回の中央値。管理データ量はGCを起こさず取得。Heap／Committedは直近GC時点。GPU専用／共有は各回の最大値を集計。所有者の帰属を示す値ではなく、Privateとの重複もあり得る。','',
        '| 条件 | 方式 | 管理データ ready MB | GC Heap ready MB | GC Committed ready MB | GPU専用 最大 MB | GPU共有 最大 MB |','|---|---|---:|---:|---:|---:|---:|']
for scenario in ['two','six-restored','six-visited','large','idle']:
    for mode in ['Default','SoftwareOnly']:
        a=group(products,Era='after',Scenario=scenario,Mode=mode)
        def show(key): return f'{med(a,key):.2f}' if values(a,key) else '未測定'
        lines.append(f"| {scenario} | {mode} | {show('ManagedReadyMB')} | {show('HeapReadyMB')} | {show('CommittedReadyMB')} | {show('gpuDedicatedMaxMB')} | {show('gpuSharedMaxMB')} |")
lines+=['','WPFの基礎コストの以前の空画面試験は [描画比較報告](RENDERING_COMPARISON_REPORT.md)に保管した。今回の総Private値をすべてWPF固定費とみなさず、2→6タブ差・復元のみ／訪問済差・管理量・GPUカウンターを分ける。WPF／Direct3D／GPUドライバーの具体的なネイティブ割当所有者は今回も未特定。','',
        '[描画経路の補助情報](docs/verification/review/performance/render-routing.csv)にWPF Tier・実際のProcessRenderMode・ロードされたグラフィックDLL・GPUカウンターを記録。Defaultはハードウェア描画の許可であり全描画のGPU実行保証ではない。SoftwareOnlyでもデスクトップ合成等のGPU共有メモリはゼロとは限らない。DLLのロードやTierだけでは各描画処理の実行先を確定できず、GPUView/ETWによる処理単位の追跡は未実施。','',
        '## 反復と応答','',
        'T6=タブ切替100、T7=フォルダ移動100、T8=タブ作成・閉鎖100、T9=スクロール50＋リサイズ50、R1=配色適用100要求（初回は同一配色、実際の変更99回）、R2=3書体を順番に100回変更、R3=設定画面生成・表示・閉鎖100。残留は回復10秒後Private−操作前Private。各欄は3回の中央値、最大値とばらつきはCSVおよび次表。','',
        '| 試験 | 方式 | 前→後 残留 MB | 後 Privateピーク MB（全回最大） | 前→後 操作CPU % | 前→後 layout中央値 ms | 後 UIキューp95 ms | 後 ハンドル残留 |','|---|---|---:|---:|---:|---:|---:|---:|']
for scenario in ['T6','T7','T8','T9','R1','R2','R3']:
    for mode in ['Default','SoftwareOnly']:
        b=group(repeats,Era='before',Scenario=scenario,Mode=mode); a=group(repeats,Era='after',Scenario=scenario,Mode=mode)
        def arrow(key): return (f"{med(b,key):.2f} → " if b else '前なし → ')+f"{med(a,key):.2f}"
        lines.append(f"| {scenario} | {mode} | {arrow('ResidualMB')} | {max(values(a,'PrivateMaxMB')):.2f} | {arrow('OperationCpuPercent')} | {arrow('LayoutMedianMs')} | {med(a,'UiQueueP95Ms'):.2f} | {med(a,'HandlesResidual'):.0f} |")
lines+=['','| 試験 | 方式 | 後 layout中央値 ms（中央値 / 最大 / 幅） | 後 layout最大 ms（全回最大） | 後 Private残留 MB（中央値 / 最大 / 幅） |','|---|---|---:|---:|---:|']
for scenario in ['T6','T7','T8','T9','R1','R2','R3']:
    for mode in ['Default','SoftwareOnly']:
        a=group(repeats,Era='after',Scenario=scenario,Mode=mode)
        lines.append(f"| {scenario} | {mode} | {trip(values(a,'LayoutMedianMs'))} | {max(values(a,'LayoutMaxMs')):.2f} | {trip(values(a,'ResidualMB'))} |")
lines+=['','完了点は状態変更／非同期列挙完了後のDispatcher.ApplicationIdle（レイアウト・Binding）。CompositionTarget.Rendering通知も別記録しているが実画面へのpresent完了ではない。実表示完了時間、実入力の応答性、文字・ちらつき・滑らかさは未測定。R1〜R3は部品別に独立プロセスで測定し、配色・フォント・設定画面のコストを混同しない。短い操作区間のCPUは採取境界の影響を受ける。','',
        '## 暫定目標','', '| 方式 | 起動150MB | 通常200MB（2/6タブの最大） | 10,000件300MB | アイドルCPU | 長時間継続増加なし |','|---|---|---|---|---|---|']
for mode in ['Default','SoftwareOnly']:
    a=group(products,Era='after',Mode=mode)
    startup=max(r['startupPrivateMaxMB'] for r in a if r['Scenario']=='two'); normal=max(r['privateMaxMB'] for r in a if r['Scenario'] in ['two','six-restored','six-visited']); large=max(r['privateMaxMB'] for r in a if r['Scenario']=='large')
    lines.append(f"| {mode} | {'範囲内' if startup<=150 else '未達'} {startup:.2f}MB | {'範囲内' if normal<=200 else '未達'} {normal:.2f}MB | {'範囲内' if large<=300 else '未達'} {large:.2f}MB | 数値を上表参照（厳密閾値未指定） | 未検証 |")
d=group(products,Era='after',Scenario='six-visited',Mode='Default'); s=group(products,Era='after',Scenario='six-visited',Mode='SoftwareOnly')
saved=med(d,'privateMeanMB')-med(s,'privateMeanMB')
td=group(repeats,Era='after',Scenario='T6',Mode='Default'); ts=group(repeats,Era='after',Scenario='T6',Mode='SoftwareOnly')
lines+=['','上の通常条件とは別に、配色・フォント・設定画面・タブ反復のピークは前表へ記載した。そこで200MBを超える場合は通常目標を常に満たすとは判定しない。8時間試験は未実施。','',
        '## 再現','', '```powershell', 'dotnet restore PaneCommander.sln','dotnet restore tools/PaneCommander.RenderMonitor/PaneCommander.RenderMonitor.csproj','dotnet build PaneCommander.sln -c Release --no-restore -m:1','dotnet build tools/PaneCommander.RenderMonitor/PaneCommander.RenderMonitor.csproj -c Release --no-restore -m:1','pwsh -NoProfile -File tools/Verify-ReviewRestart.ps1 -Output artifacts/review-restart-new','pwsh -NoProfile -File tools/Measure-Review.ps1 -Output artifacts/review-performance-final-new','python tools/Analyze-Review.py artifacts/review-performance-final-new','```','',
        '新しい出力先を使う。保全したM3バイナリと旧ログが必要。測定中のビルド・別性能試験・強制GCをしない。集計ツールは引数省略時に標準出力先を読む。グラフ生成だけにmatplotlibを使い、製品依存には追加していない。通常製品プロファイルを変更しない。','',
        '## 結論','',f'描画方式の最終判断は保留。6タブ訪問済のPrivate平均中央値はDefault {med(d,"privateMeanMB"):.2f}MB、SoftwareOnly {med(s,"privateMeanMB"):.2f}MBで、削減は{saved:.2f}MB（{saved/med(d,"privateMeanMB")*100:.1f}%）。CPU・レイアウト応答の数値は上表のとおりで、メモリだけを理由に受入完了とはしない。ユーザー指定のSoftwareOnly既定とDefaultへの復帰を維持し、実GUI品質と操作性の確認後に判断する。']
lines.append(f'タブ切替中CPUはDefault {med(td,"OperationCpuPercent"):.4f}% → SoftwareOnly {med(ts,"OperationCpuPercent"):.4f}%、レイアウト完了中央値は{med(td,"LayoutMedianMs"):.2f}ms → {med(ts,"LayoutMedianMs"):.2f}ms。表示完了時間と滑らかさは未測定。')
(ROOT/'REVIEW_PERFORMANCE_REPORT.md').write_text(('\n'.join(lines)+'\n').replace('artifacts/review-performance-final`',RUN.relative_to(ROOT).as_posix()+'`'),encoding='utf-8')
oldSources={e['Path'].replace('\\','/'):e['SHA256'] for e in read(ROOT/'artifacts/review-baseline/source-manifest.json')}
changed=[]
for directory in ['src','tests']:
    for p in (ROOT/directory).rglob('*'):
        if p.is_file() and not any(part in ['bin','obj'] for part in p.parts):
            key=p.relative_to(ROOT).as_posix(); h=sha(p)
            if oldSources.get(key)!=h: changed.append({'Path':key,'Change':'modified' if key in oldSources else 'added','SHA256':h})
export('changed-files.csv',changed)
(OUT/'validation.json').write_text(json.dumps({'FrozenManifests':checks,'MeasuredSourcesUnchanged':True,'EnvironmentMatchesM3':True,'BaselineAllProductBinariesMatchM3':True,'AfterProductRuns':30,'AfterDriverRuns':42,'BeforeFreshHandleRuns':10,'BeforeDriverRuns':24,'ForcedGc':False,'GuiVerified':False,'ChangedFiles':len(changed)},indent=2),encoding='utf-8')

# Plot only after measurement completes; avoid analysis CPU overlapping trials.
sys.path.insert(0,str(ROOT/'artifacts/render-analysis-deps'))
os.environ['MPLCONFIGDIR']=str(ROOT/'artifacts/review-analysis-cache')
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,ax=plt.subplots(2,1,figsize=(11,8),constrained_layout=True)
scenarios=['two','six-restored','six-visited','large','idle']
for era,style in [('before','--'),('after','-')]:
    for mode,color in [('Default','#bd7859'),('SoftwareOnly','#4186ad')]:
        sets=[group(products,Era=era,Scenario=x,Mode=mode) for x in scenarios]
        for axis,key in zip(ax,['privateMeanMB','cpuMeanAllLogical']):
            axis.plot(scenarios,[med(g,key) for g in sets],style,marker='o',color=color,label=f'{era} {mode}')
ax[0].set_ylabel('Private Bytes mean (MB)'); ax[1].set_ylabel('CPU (% of 24 logical CPUs)')
for axis in ax: axis.grid(alpha=.25); axis.legend(fontsize=8)
fig.savefig(OUT/'comparison.png',dpi=140);plt.close(fig)
fig,ax=plt.subplots(2,1,figsize=(11,7),constrained_layout=True)
for mode,color in [('Default','#bd7859'),('SoftwareOnly','#4186ad')]:
    for rep in range(1,4):
        path=RUN/'after-product'/f'idle-{mode}-{rep}'/'samples.csv'
        with path.open(encoding='utf-8-sig') as f: data=list(csv.DictReader(f))
        x=[float(r['Seconds']) for r in data]
        ax[0].plot(x,[float(r['PrivateBytes'])/1e6 for r in data],color=color,alpha=.65,label=f'{mode} #{rep}')
        ax[1].plot(x,[float(r['CpuPercentAllLogical']) for r in data],color=color,alpha=.4)
ax[0].set_ylabel('Private Bytes (MB)');ax[0].legend(fontsize=8,ncol=3)
ax[1].set_ylabel('250ms CPU (% of 24 logical CPUs)');ax[1].set_xlabel('Observation seconds after warmup')
for axis in ax: axis.grid(alpha=.25)
fig.savefig(OUT/'idle-timeseries.png',dpi=140);plt.close(fig)
fig,axes=plt.subplots(4,2,figsize=(12,14),constrained_layout=True)
axes.flat[-1].set_visible(False)
for axis,scenario in zip(axes.flat,['T6','T7','T8','T9','R1','R2','R3']):
    for mode,code,color in [('Default','A','#bd7859'),('SoftwareOnly','B','#4186ad')]:
        path=RUN/'after-repeat'/f'{scenario}-{code}-1'/'samples.csv'
        with path.open(encoding='utf-8-sig') as f: data=list(csv.DictReader(f))
        axis.plot([float(r['Seconds']) for r in data],[float(r['PrivateBytes'])/1e6 for r in data],label=mode,color=color)
    axis.set_title(f'{scenario} (representative repetition 1)');axis.set_xlabel('Seconds after warmup');axis.set_ylabel('Private Bytes (MB)');axis.grid(alpha=.25);axis.legend(fontsize=8)
fig.savefig(OUT/'timeseries.png',dpi=140);plt.close(fig)
fig,ax=plt.subplots(figsize=(11,4),constrained_layout=True)
for mode,color in [('Default','#bd7859'),('SoftwareOnly','#4186ad')]:
    ax.plot(['T6','T7','T8','T9','R1','R2','R3'],[med(group(repeats,Era='after',Scenario=x,Mode=mode),'LayoutMedianMs') for x in ['T6','T7','T8','T9','R1','R2','R3']],marker='o',label=mode,color=color)
ax.set_ylabel('ApplicationIdle completion median (ms)');ax.grid(alpha=.25);ax.legend();fig.savefig(OUT/'response.png',dpi=140);plt.close(fig)
with (ROOT/'REVIEW_PERFORMANCE_REPORT.md').open('a',encoding='utf-8') as f:
    f.write('\n[比較グラフ](docs/verification/review/performance/comparison.png)、[300秒アイドル時系列](docs/verification/review/performance/idle-timeseries.png)、[操作時系列例](docs/verification/review/performance/timeseries.png)、[レイアウト応答比較](docs/verification/review/performance/response.png)。\n')
print(json.dumps({'products':len(products),'driverRuns':len(repeats),'changedFiles':len(changed)},ensure_ascii=False))
