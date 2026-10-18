"""Analyze production-EXE M3 measurements without touching the target process."""
from pathlib import Path
import argparse
import csv
import hashlib
import json
import shutil
import statistics
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--input',default='artifacts/m3-product-verified')
parser.add_argument('--output',default='docs/verification/m3/product')
args=parser.parse_args()
SOURCE = ROOT / args.input
DEST = ROOT / args.output
DEST.mkdir(parents=True, exist_ok=True)
SCENARIOS = ['two', 'six-restored', 'six-visited', 'large', 'idle']
NAMES = dict(zip(SCENARIOS, ['2タブ', '6タブ・復元直後', '6タブ・全タブ表示後', '1万件表示', '300秒アイドル']))
MODES = ['Default', 'SoftwareOnly']
COLORS = {'Default': '#1769aa', 'SoftwareOnly': '#e56b17'}

def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def write_csv(path, rows):
    with path.open('w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, list(dict.fromkeys(k for r in rows for k in r)))
        writer.writeheader(); writer.writerows(rows)

def stats(values):
    values = [float(v) for v in values if v is not None]
    return {'median': statistics.median(values), 'min': min(values), 'max': max(values), 'stddev': statistics.pstdev(values)} if values else dict.fromkeys(['median','min','max','stddev'])

rows, runs, checks = [], {}, []
for scenario in SCENARIOS:
    for mode in MODES:
        for repeat in range(1, 4):
            directory = SOURCE / f'{scenario}-{mode}-{repeat}'
            if not (directory / 'summary.json').exists():
                continue
            summary, ready, closing = (load(directory / name) for name in ['summary.json','ready.json','closing.json'])
            with (directory / 'samples.csv').open(encoding='utf-8-sig') as f:
                samples = [{k: float(v) if v else None for k,v in r.items()} for r in csv.DictReader(f)]
            key = (scenario, mode, repeat)
            runs[key] = {'summary': summary, 'ready': ready, 'closing': closing, 'samples': samples}
            row = {'run': directory.name, 'scenario': scenario, 'mode': mode, 'repeat': repeat,
                   **{k:v for k,v in summary.items() if isinstance(v,(float,int,bool))},
                   'tabs': ready['tabs'], 'loadedTabs': ready['loadedTabs'], 'folderViews': ready['folderViews'], 'realizedRows': ready['realizedRows']}
            row['privateMedianMB'] = statistics.median(s['PrivateBytes']/1e6 for s in samples)
            row['privateStddevMB'] = statistics.pstdev(s['PrivateBytes']/1e6 for s in samples)
            row['cpuMedianAllLogical'] = statistics.median(s['CpuPercentAllLogical'] for s in samples[1:])
            row['cpuStddevAllLogical'] = statistics.pstdev(s['CpuPercentAllLogical'] for s in samples[1:])
            row['cpuMeanOneCore'] = summary['cpuMeanAllLogical'] * summary['logicalProcessors']
            row['gcHeapFinalMBAtLastGc'] = closing['gcHeapAtLastGc']/1e6 if closing['gcIndex'] else None
            row['gcCommittedFinalMBAtLastGc'] = closing['gcCommittedAtLastGc']/1e6 if closing['gcIndex'] else None
            row['gcIndexFinal'] = closing['gcIndex']
            row['managedFinalWithoutGcMB'] = closing['managedBytesWithoutGc']/1e6
            steady = [s for s in samples if 1 <= s['Seconds'] <= samples[-1]['Seconds'] - 1]
            row['steadyCpuAllLogical'] = 100*(steady[-1]['CpuSeconds']-steady[0]['CpuSeconds'])/(steady[-1]['Seconds']-steady[0]['Seconds'])/summary['logicalProcessors'] if len(steady)>1 else None
            row['sampleIntervalMedianMs'] = statistics.median((b['Seconds']-a['Seconds'])*1000 for a,b in zip(samples,samples[1:]))
            rows.append(row)
            session = ready['session']; left = session['Left']; right = session['Right']
            selected = [ready['statuses'][left['SelectedIndex']], ready['statuses'][len(left['Tabs'])+right['SelectedIndex']]]
            expected_tabs = 6 if scenario.startswith('six') else 2
            valid = (ready['modeBeforeWindow'] == ready['effectiveRenderMode'] == mode and ready['settings']['RenderingMode'] == mode
                and summary['savedTabStateMatchesReady'] and summary['profileFilesUnchangedDuringObservation']
                and ready['forcedGc'] is False and closing['forcedGc'] is False and summary['forcedGc'] is False
                and ready['productionExecutable'] and ready['warmupSeconds'] == 10 and summary['displayAndSystemRequested']
                and ready['tabs'] == expected_tabs and ready['loadedTabs'] == (6 if scenario == 'six-visited' else 2)
                and left['SelectedIndex'] == right['SelectedIndex'] == session['ActiveSide'] == 0
                and abs(session['Layout']['LeftPaneFraction']-.5)<1e-6 and session['Layout']['SidebarWidth']==180
                and ready['folderViews'] == 2 and 0 < ready['realizedRows'] < 100
                and all(s['NavigationSucceeded'] for s in selected) and [s['count'] for s in selected] == ([10000,256] if scenario=='large' else [256,256])
                and ready['dpiX'] == ready['dpiY'] == 1.5 and ready['Width'] == 1280 and ready['Height'] == 800
                and summary['observedSeconds'] >= (300 if scenario=='idle' else 10))
            checks.append({'run':directory.name, 'conditionsValid':bool(valid)})

if len(rows) != 30 or not all(c['conditionsValid'] for c in checks):
    raise SystemExit(f'Incomplete/invalid cohort: {len(rows)}/30; failed={ [c for c in checks if not c["conditionsValid"]] }')
order = load(SOURCE / 'run-order.json')
assert [(r['Scenario'],r['Mode'],r['Repeat']) for r in order] == [(s,m,r) for s in SCENARIOS for r in range(1,4) for m in MODES]
assert load(SOURCE / 'integrity.json')['FrozenBinariesUnchanged']
assert load(SOURCE / 'environment.json')['PowerPlan'] == load(SOURCE / 'integrity.json')['PowerPlan']
assert load(SOURCE / 'environment.json')['GPU'] == load(SOURCE / 'environment-after.json')['GPU']
signatures = {(r['ready']['runtime'],r['ready']['dpiX'],r['ready']['dpiY']) for r in runs.values()}
assert len(signatures) == 1
appearance_signatures = {json.dumps({k:v for k,v in r['ready']['settings'].items() if k!='RenderingMode'},sort_keys=True) for r in runs.values()}
assert len(appearance_signatures) == 1
assert len({json.dumps(r['ready']['favorites'],sort_keys=True) for r in runs.values()})==1
for entry in load(SOURCE / 'source-manifest-during.json'):
    assert hashlib.sha256((ROOT/entry['Path']).read_bytes()).hexdigest().upper() == entry['SHA256']
write_csv(DEST/'runs.csv', rows)
aggregate = []
for scenario in SCENARIOS:
    for mode in MODES:
        group = [r for r in rows if r['scenario']==scenario and r['mode']==mode]
        for metric in [k for k,v in rows[0].items() if isinstance(v,(int,float)) and not isinstance(v,bool) and k!='repeat']:
            aggregate.append({'scenario':scenario,'mode':mode,'metric':metric,**stats([r.get(metric) for r in group])})
write_csv(DEST/'aggregate.csv',aggregate)
(DEST/'validation.json').write_text(json.dumps({'complete':True,'runs':30,'alternatingOrder':True,'settingsIdenticalExceptRendering':True,'sourceUnchanged':True,'checks':checks},indent=2),encoding='utf-8')

def values(scenario, mode, metric):
    return [r.get(metric) for r in rows if r['scenario']==scenario and r['mode']==mode]

def med(scenario, mode, metric):
    return stats(values(scenario,mode,metric))['median']

def cell(scenario, mode, metric, digits=2):
    s = stats(values(scenario,mode,metric))
    return '未測定' if s['median'] is None else f"{s['median']:.{digits}f} [{s['min']:.{digits}f}–{s['max']:.{digits}f}]"

def table(headers, body):
    return '\n'.join(['| '+' | '.join(headers)+' |','| '+' | '.join(['---']*len(headers))+' |']+['| '+' | '.join(str(v) for v in r)+' |' for r in body])

sys.path.insert(0,str(ROOT/'artifacts/render-analysis-deps'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
charts = DEST/'charts'; charts.mkdir(exist_ok=True)

def save(fig,name):
    fig.savefig(charts/f'{name}.png',dpi=150,bbox_inches='tight')
    fig.savefig(charts/f'{name}.svg',bbox_inches='tight'); plt.close(fig)

def bars(scenarios,metric,ylabel,name,title):
    fig,ax=plt.subplots(figsize=(10,4)); x=np.arange(len(scenarios))
    for i,mode in enumerate(MODES):
        summary=[stats(values(s,mode,metric)) for s in scenarios]
        y=np.array([s['median'] for s in summary]); low=np.array([s['min'] for s in summary]); high=np.array([s['max'] for s in summary])
        ax.bar(x+(i-.5)*.36,y,.36,color=COLORS[mode],label=mode,yerr=[y-low,high-y],capsize=3)
    ax.set_xticks(x,scenarios); ax.set_ylabel(ylabel); ax.set_title(title); ax.legend(); ax.grid(axis='y',alpha=.2)
    save(fig,name)

bars(SCENARIOS,'privateMeanMB','Private Bytes (decimal MB)','private-mean','Production EXE: median of run means; whiskers = 3-run range')
bars(SCENARIOS,'privateMaxMB','Private Bytes (decimal MB)','private-max','Production EXE: median of run maxima; whiskers = 3-run range')
bars(['idle'],'cpuMeanAllLogical','CPU % of 24 logical processors','idle-cpu','300-second idle observation; includes diagnostic handoff at start')
bars(SCENARIOS,'gpuSharedMaxMB','Reported GPU shared usage (decimal MB)','gpu-shared','Do not add GPU shared usage to Private Bytes')
fig,axes=plt.subplots(2,3,figsize=(15,8))
for ax,scenario in zip(axes.flat,SCENARIOS):
    for mode in MODES:
        for repeat in range(1,4):
            samples=runs[(scenario,mode,repeat)]['samples']
            ax.plot([s['Seconds'] for s in samples],[s['PrivateBytes']/1e6 for s in samples],color=COLORS[mode],linestyle=['-','--',':'][repeat-1],label=f'{mode}-{repeat}')
    ax.set_title(scenario); ax.set_xlabel('Seconds after warmup'); ax.set_ylabel('Private Bytes (MB)'); ax.grid(alpha=.2)
axes.flat[0].legend(fontsize=7); axes.flat[-1].set_visible(False)
fig.suptitle('M3 production EXE memory time series; independent process per repetition'); fig.tight_layout(); save(fig,'memory-timeseries')

for name in ['environment.json','environment-after.json','binary-manifest.json','source-manifest-during.json','integrity.json','run-order.json']:
    if (SOURCE/name).exists(): shutil.copy2(SOURCE/name,DEST/name)
for key in runs:
    directory=SOURCE/f'{key[0]}-{key[1]}-{key[2]}'
    target=DEST/'raw'/directory.name
    shutil.copytree(directory,target,dirs_exist_ok=True)
validation_root=ROOT/'artifacts/m3-validation'
for mode,filename in [('software','software-verified.trx'),('hardware','hardware-verified.trx')]:
    source=validation_root/mode/filename
    tree=ET.parse(source)
    counters=tree.find('.//{*}Counters')
    assert counters is not None and counters.get('failed')=='0' and counters.get('passed')=='42'
    shutil.copy2(source,ROOT/'docs/verification/m3'/mode/filename)
for filename in ['build-software.txt','build-hardware.txt','build-monitor.txt','test-software.txt','test-hardware.txt']:
    shutil.copy2(validation_root/filename,ROOT/'docs/verification/m3'/filename)
shutil.copytree(ROOT/'artifacts/m3-restart-verified',ROOT/'docs/verification/m3/restart',dirs_exist_ok=True)

def compare(metric,digits=2):
    return table(['条件','Default','SoftwareOnly'],[[NAMES[s],cell(s,'Default',metric,digits),cell(s,'SoftwareOnly',metric,digits)] for s in SCENARIOS])

environment=load(SOURCE/'environment.json')
saving=med('six-visited','Default','privateMeanMB')-med('six-visited','SoftwareOnly','privateMeanMB')
root_link=(ROOT/'docs/verification/m3/product').as_posix()
report=f'''# M3 製品版の性能測定（2026-10-13）

実際のPaneCommander.App.exeを30独立プロセスで測定した。5条件×2方式×3回、各条件でDefault→SoftwareOnlyを交互に実行。ユーザーの最新指示により製品の既定値はSoftwareOnlyへ変更済み。実GUI品質と8時間以上の安定性は未検証で、受入判定は保留する。

## 条件

- OS: {environment['OS']}、.NET: {next(iter(signatures))[0]}、SDK: {environment['SDK']}。
- CPU: {environment['CPU'][0]['Name']}、24論理CPU。RAM {environment['RAM']/1e9:.2f}GB。
- GPU: {' / '.join(g['Name']+' driver '+g['DriverVersion'] for g in environment['GPU'])}。2560×1440・165Hz、DPI150%、Window1280×800 DIP。
- 電源プラン: {environment['PowerPlan']}。外部モニターで試験中だけ自動消灯・スリープ抑止要求を保持し、終了時解除。電源プラン自体は変更しない。各試行のAC接続状態は連続記録していない。
- 同じReleaseバイナリ・同じローカルディレクトリ・同じ表示設定・同じお気に入り2件。描画方式以外の設定は同一。試験中のバイナリハッシュ不変を検査。
- 起動時のメモリは外部から約100ms間隔、ウォームアップ後は約250ms間隔で採取。ウォームアップ10秒、通常観測10秒、アイドル観測300秒。強制GCなし。MBは10進。
- 通常フォルダは1ディレクトリ＋255空ファイル、左右各256項目。大量条件は左10,000空ファイル、右256項目。6タブは左右各3枚。全タブ表示後は、診断コードから各タブを一度選択して元に戻してからウォームアップした。
- 実製品の起動・保存・復元・終了経路を使用。独立プロファイルと明示的な診断起動オプションを利用し、ready JSONと終了イベント待機の小さな診断オーバーヘッドを含む。通常起動では診断待機を実行しない。
- ネイティブなマウス／キー操作は行っていない。最前面の固定、他アプリ負荷、GPU温度・クロック・消費電力は制御／計測していない。構成値DefaultはGPUを許可する指定であり、全フレームのハードウェア描画を保証しない。

以下は3回の中央値 [最小–最大]。母標準偏差と各回の値は[集計CSV]({root_link}/aggregate.csv)、[各回CSV]({root_link}/runs.csv)に保存。

## メモリ

Private Bytes平均（MB）:

{compare('privateMeanMB')}

Private Bytes最大（MB）:

{compare('privateMaxMB')}

Private Bytes終了時（MB、Windowを閉じる前の最終サンプル）:

{compare('privateFinalMB')}

Working Set平均（MB）:

{compare('workingSetMeanMB')}

Working Set最大（MB）:

{compare('workingSetMaxMB')}

![製品版メモリ比較]({root_link}/charts/private-mean.png)

6タブ全表示後のSoftwareOnlyによるPrivate Bytes平均の削減量は **{saving:.2f}MB**（{100*saving/med('six-visited','Default','privateMeanMB'):.1f}%）。2→6タブの平均増分は、復元直後ではDefault **{med('six-restored','Default','privateMeanMB')-med('two','Default','privateMeanMB'):.2f}MB**／SoftwareOnly **{med('six-restored','SoftwareOnly','privateMeanMB')-med('two','SoftwareOnly','privateMeanMB'):.2f}MB**、全タブ表示後ではDefault **{med('six-visited','Default','privateMeanMB')-med('two','Default','privateMeanMB'):.2f}MB**／SoftwareOnly **{med('six-visited','SoftwareOnly','privateMeanMB')-med('two','SoftwareOnly','privateMeanMB'):.2f}MB**。独立したプロセス群の中央値差であり、確保元を直接特定した値ではない。

6タブ復元直後の読込済みタブは2、全タブ表示後は6。両方とも接続中のVisual TreeにあるFolderViewは2で、非選択タブの状態と描画ホストを分離している。今回の個数検査は接続中のTreeであり、M3の全ヒープや切り離された描画オブジェクトの生存を網羅した検査ではない。

![時系列]({root_link}/charts/memory-timeseries.png)

暫定目標との照合（3回の最大サンプル。CPUやWorking Setとは別のPrivate Bytes判定）:

{table(['条件','目標MB','Default 最大/結果','SoftwareOnly 最大/結果'],[[label,limit,*[f"{max(values(s,mode,metric)):.2f} / {'達成' if max(values(s,mode,metric))<=limit else '未達'}" for mode in MODES]] for label,s,metric,limit in [('起動〜初期表示・ウォームアップ','two','startupPrivateMaxMB',150),('6タブ復元直後','six-restored','privateMaxMB',200),('6タブ全表示後','six-visited','privateMaxMB',200),('1万件','large','privateMaxMB',300)]])}

起動値はプロセス開始からreadyまでの約100msサンプル最大で、真の瞬間ピークの保証ではない。全タブ訪問条件の起動区間には診断操作も含むため、150MB判定には2タブ条件だけを用いた。新規で設定ファイルがない製品起動の検証も別に実施し、SoftwareOnlyの既定値を確認した。

## CPU・アイドル書込み

全観測区間のCPU平均（24論理CPU全体を100%）:

{compare('cpuMeanAllLogical',4)}

300秒アイドルの平均はDefault **{cell('idle','Default','cpuMeanAllLogical',4)}%**、SoftwareOnly **{cell('idle','SoftwareOnly','cpuMeanAllLogical',4)}%**。先頭・末尾各1秒を除いた補助的な定常区間はDefault **{cell('idle','Default','steadyCpuAllLogical',4)}%**、SoftwareOnly **{cell('idle','SoftwareOnly','steadyCpuAllLogical',4)}%**。全300秒の値と区別する。

![アイドルCPU]({root_link}/charts/idle-cpu.png)

CPU平均はプロセスCPU時間差÷経過時間÷論理CPU数。ピークは約250ms区間平均の最大であり、真の瞬間負荷ではない。起動後の診断ハンドオフのCPUも観測に含まれる。プロセス終了時の保存・終了処理は平均の範囲外。DWMやモニター自身を含むPC全体のCPUは対象PIDの値と別で、今回は測定していない。

全30回で、観測中にsettings/session/favoritesの更新時刻が変化しないことと、終了後にセッションが正常保存されることを確認した。通常起動のセッション保存は正常終了時だけ。強制終了やOSシャットダウンでの保存は保証しない。

## GPU・GC

GPU共有メモリの報告値最大（MB）:

{compare('gpuSharedMaxMB')}

専用GPUメモリ最大（MB）:

{compare('gpuDedicatedMaxMB')}

![GPU共有メモリ]({root_link}/charts/gpu-shared.png)

PDHの対象PIDインスタンスを集計した値。GPU使用量とPrivate Bytesを足し合わせず、その差をドライバー占有量とは断定しない。静止画面の観測が主体なので、GPU Engineの0%だけでソフトウェア描画経路と判定しない。実描画経路のフレーム別割合・ネイティブ確保スタック・表示完了時刻は未測定。

GC Heap/Committedは最後の自然GC時点の値。GC index=0は未測定として扱う。managed falseはGCを要求せず取得した概算で、回収可能な未回収オブジェクトも含む。

{table(['run','Private 平均/最大/終了MB','CPU 平均/区間ピーク%','CPU秒 差/累計','GC Heap/Committed MB','managed false MB'],[[r['run'],f"{r['privateMeanMB']:.2f}/{r['privateMaxMB']:.2f}/{r['privateFinalMB']:.2f}",f"{r['cpuMeanAllLogical']:.4f}/{r['cpuPeakSampleAllLogical']:.2f}",f"{r['cpuSeconds']:.3f}/{r['cpuTotalIncludingWarmupSeconds']:.3f}",'未測定' if r['gcIndexFinal']==0 else f"{r['gcHeapFinalMBAtLastGc']:.2f}/{r['gcCommittedFinalMBAtLastGc']:.2f}",f"{r['managedFinalWithoutGcMB']:.2f}"] for r in rows])}

## 検証範囲と再現

実GUIの表示品質・実キー／マウス操作・ちらつき・滑らかさ、複数画面／異DPI、8時間以上の安定性は未検証。自動生成PNGはRenderTargetBitmapによるレイアウト確認用で、ハードウェア出力の実スクリーンショットではない。短い観測から長時間の増加なしを合格扱いしない。

Releaseビルドは両方式とも警告0・エラー0。42件の自動テストを方式別の独立テストプロセスで実施し、各42/42合格。製品EXEによる再起動切替、状態復元、無効パス、破損JSON、新規プロファイルの確認は[実装報告]({(ROOT/'M3_IMPLEMENTATION_REPORT.md').as_posix()})を参照。

再現: `dotnet build PaneCommander.sln -c Release`、`dotnet build tools/PaneCommander.RenderMonitor/PaneCommander.RenderMonitor.csproj -c Release`の後、`./tools/Measure-M3Product.ps1 -Output artifacts/m3-product-new`。全試行の完了まで再ビルドしない。終了後`python tools/Analyze-M3Product.py --input artifacts/m3-product-new`。解析依存はtools/requirements-render-analysis.txtを参照し、製品依存には加えない。新しい出力先を使い、既存データと混ぜない。

[環境]({root_link}/environment.json)、[実行順]({root_link}/run-order.json)、[整合性]({root_link}/integrity.json)、[条件検査]({root_link}/validation.json)、[生ログ]({root_link}/raw)。予備試験と終了処理／最小幅修正前の測定は集計に含めていない。タブ切替と作成・閉鎖の操作反復は[専用ドライバーの別報告]({(ROOT/'M3_RESOURCE_REPEAT_REPORT.md').as_posix()})へ分離する。

SoftwareOnlyの省メモリ効果は実製品でも確認できた。実装上の既定値はユーザー指示どおりSoftwareOnly、Defaultへの切替手段を維持。受入判定は人によるGUI確認と残る非機能検証まで保留する。
'''
(ROOT/'M3_PERFORMANCE_REPORT.md').write_text(report,encoding='utf-8')
print(f'M3 product report generated: {len(rows)} trials.')
