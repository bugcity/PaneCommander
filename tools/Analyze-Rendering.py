"""Analyze independent, alternating rendering trials. No target-process interaction."""
from pathlib import Path
import argparse
import csv
import hashlib
import json
import math
import re
import shutil
import statistics
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'artifacts/render-analysis-deps'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser()
parser.add_argument('--input', default='artifacts/render-comparison-controlled')
parser.add_argument('--output', default='docs/verification/render-comparison')
parser.add_argument('--partial', action='store_true')
args = parser.parse_args()
source = ROOT / args.input
dest = ROOT / args.output
dest.mkdir(parents=True, exist_ok=True)
charts = dest / 'charts'
charts.mkdir(exist_ok=True)
MB = 1e6
COLORS = {'A': '#1769aa', 'B': '#e56b17'}
LABELS = {'A': 'Default', 'B': 'SoftwareOnly'}
NAMES = {'T1': '空のWPF画面', 'T2': '実画面・2タブ', 'T3': '実画面・6タブ',
         'T4': '10,000件表示', 'T5': '300秒アイドル・2タブ', 'T6': '6タブ切替100回',
         'T7': 'フォルダ移動100回', 'T8': 'タブ作成・閉鎖100回', 'T9': 'スクロール50回＋リサイズ50回'}

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def stats(values):
    x = [float(v) for v in values if v is not None and math.isfinite(float(v))]
    if not x:
        return {'median': None, 'min': None, 'max': None, 'stddev': None, 'p95': None}
    return {'median': statistics.median(x), 'min': min(x), 'max': max(x),
            'stddev': statistics.pstdev(x), 'p95': float(np.percentile(x, 95))}

def cell(values, digits=2):
    s = stats(values)
    if s['median'] is None:
        return '未測定'
    return f"{s['median']:.{digits}f} [{s['min']:.{digits}f}–{s['max']:.{digits}f}]"

def table(headers, rows):
    return '\n'.join(['| ' + ' | '.join(headers) + ' |', '| ' + ' | '.join(['---'] * len(headers)) + ' |']
                     + ['| ' + ' | '.join(str(v) for v in row) + ' |' for row in rows])

def write_csv(path, rows):
    if not rows:
        return
    fields = list(dict.fromkeys(k for r in rows for k in r))
    with path.open('w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)

def numeric_samples(path):
    with path.open(encoding='utf-8-sig', newline='') as f:
        return [{k: v if k == 'Phase' else (float(v) if v else None) for k, v in r.items()}
                for r in csv.DictReader(f)]

def phase_cpu(samples, phase, logical):
    group = [s for s in samples if s['Phase'] == phase]
    if len(group) < 2:
        return None
    return 100 * (group[-1]['CpuSeconds'] - group[0]['CpuSeconds']) / (group[-1]['Seconds'] - group[0]['Seconds']) / logical

def tail_private(samples, phase, seconds=1):
    group = [s for s in samples if s['Phase'] == phase]
    if not group:
        return None
    return statistics.median(s['PrivateBytes'] / MB for s in group if s['Seconds'] >= group[-1]['Seconds'] - seconds)

rows, operations, runs, checks = [], [], {}, []
for path in sorted(source.glob('T[1-9]-[AB]-[1-3]')):
    if not (path / 'monitor-summary.json').exists():
        continue
    summary = read(path / 'monitor-summary.json')
    ready = read(path / 'ready.json')
    done = read(path / 'completed.json')
    samples = numeric_samples(path / 'samples.csv')
    scenario, mode, repeat = path.name.split('-')
    repeat = int(repeat)
    key = (scenario, mode, repeat)
    runs[key] = {'summary': summary, 'ready': ready, 'done': done, 'samples': samples}
    final = next(c for c in done['checkpoints'] if c['stage'] == 'final')
    baseline = next(c for c in done['checkpoints'] if c['stage'] == 'baseline')
    row = {'run': path.name, 'scenario': scenario, 'mode': mode, 'repeat': repeat}
    for name in ['durationSeconds', 'privateMeanMB', 'privateMaxMB', 'privateFinalMB', 'workingSetMeanMB',
                 'workingSetMaxMB', 'cpuSeconds', 'cpuTotalIncludingWarmupSeconds', 'cpuMeanAllLogical',
                 'cpuPeakSampleAllLogical', 'gpuDedicatedMaxMB', 'gpuSharedMaxMB', 'gpuEnginePeakPercent']:
        row[name] = summary[name]
    row['privateSampleMedianMB'] = statistics.median(s['PrivateBytes'] / MB for s in samples)
    row['privateSampleStddevMB'] = statistics.pstdev(s['PrivateBytes'] / MB for s in samples)
    row['workingSetSampleMedianMB'] = statistics.median(s['WorkingSet'] / MB for s in samples)
    row['workingSetSampleStddevMB'] = statistics.pstdev(s['WorkingSet'] / MB for s in samples)
    row['cpuSampleMedianAllLogical'] = statistics.median(s['CpuPercentAllLogical'] for s in samples[1:])
    row['cpuSampleStddevAllLogical'] = statistics.pstdev(s['CpuPercentAllLogical'] for s in samples[1:])
    row['gpuSharedAvailableFraction'] = sum(s['GpuSharedBytes'] is not None for s in samples) / len(samples)
    row['gpuDedicatedAvailableFraction'] = sum(s['GpuDedicatedBytes'] is not None for s in samples) / len(samples)
    row['sampleIntervalMedianMs'] = statistics.median((b['Seconds'] - a['Seconds']) * 1000 for a, b in zip(samples, samples[1:]))
    row['idleCpuAllLogical'] = phase_cpu(samples, 'idle', summary['logicalProcessors'])
    idle_samples = [s for s in samples if s['Phase'] == 'idle']
    steady = [s for s in idle_samples if s['Seconds'] >= 1 and s['Seconds'] <= idle_samples[-1]['Seconds'] - 1] if idle_samples else []
    row['idleSteadyCpuAllLogical'] = phase_cpu(steady, 'idle', summary['logicalProcessors'])
    row['operationCpuAllLogical'] = phase_cpu(samples, 'operations', summary['logicalProcessors'])
    row['gcHeapFinalMBAtLastGc'] = final['gcHeapSizeBytesAtLastGc'] / MB if final['gcIndex'] else None
    row['gcCommittedFinalMBAtLastGc'] = final['gcCommittedBytesAtLastGc'] / MB if final['gcIndex'] else None
    row['gcIndexFinal'] = final['gcIndex']
    row['managedFinalMBWithoutGc'] = final['managedBytesWithoutGc'] / MB
    row['tabsFinal'], row['entriesFinal'], row['realizedRowsFinal'] = final['tabs'], final['entries'], final['realizedRows']
    row['gcCollectionsDelta'] = '/'.join(str(b - a) for a, b in zip(baseline['gcCollections'], final['gcCollections']))
    row['frameTimeouts'] = sum(m['frameTimeout'] for m in done['measurements'])
    row['uiQueueSampleCount'] = len(done['uiThreadQueueDelayMs'])
    row['operationCount'] = len(done['measurements'])
    row['foregroundAtReady'] = ready['IsActive']
    for prefix, values in [('layout', [m['layoutReadyMs'] for m in done['measurements']]),
                           ('renderingEvent', [m['renderingEventMs'] for m in done['measurements']]),
                           ('uiQueue', done['uiThreadQueueDelayMs'])]:
        for stat, value in stats(values).items():
            row[f'{prefix}{stat.capitalize()}Ms'] = value
    before = tail_private(samples, 'baseline')
    after = tail_private(samples, 'recovery')
    row['residualPrivateMB'] = after - before if after is not None and before is not None else None
    recovery = [s for s in samples if s['Phase'] == 'recovery']
    tail = [s for s in recovery if s['Seconds'] >= recovery[-1]['Seconds'] - 5] if recovery else []
    row['recoveryLast5sRangeMB'] = (max(s['PrivateBytes'] for s in tail) - min(s['PrivateBytes'] for s in tail)) / MB if tail else None
    row['recoveryLast5sSlopeMBPerSecond'] = float(np.polyfit([s['Seconds'] for s in tail], [s['PrivateBytes'] / MB for s in tail], 1)[0]) if len(tail) > 1 else None
    idle = [s for s in samples if s['Phase'] == 'idle']
    row['idlePrivateDeltaMB'] = (idle[-1]['PrivateBytes'] - idle[0]['PrivateBytes']) / MB if idle else None
    row['idlePrivateRangeMB'] = (max(s['PrivateBytes'] for s in idle) - min(s['PrivateBytes'] for s in idle)) / MB if idle else None
    phases = done['phases']
    row['idleDriverDurationSeconds'] = next((b['elapsedSeconds'] - a['elapsedSeconds'] for a, b in zip(phases, phases[1:]) if a['phase'] == 'idle'), None)
    rows.append(row)
    for m in done['measurements']:
        operations.append({'run': path.name, 'scenario': scenario, 'mode': mode, 'repeat': repeat, **m})
    expected_tabs = 6 if scenario in ['T3', 'T6'] else 0 if scenario == 'T1' else 2
    expected_entries = 0 if scenario == 'T1' else 1536 if scenario in ['T3', 'T6'] else 10256 if scenario in ['T4', 'T9'] else 512
    valid = (summary['forcedGc'] is False and done['forcedGc'] is False and summary['displayAndSystemRequested']
             and summary['logicalProcessors'] == 24
             and ready['warmupSeconds'] == 10 and ready['renderPreference'] == ('Default' if mode == 'A' else 'SoftwareOnly')
             and ready['dpiX'] == 1.5 and ready['dpiY'] == 1.5 and ready['Width'] == 1280 and ready['Height'] == 800
             and ready['power']['acLineStatus'] == done['power']['acLineStatus'] == 1 and final['tabs'] == expected_tabs
             and final['entries'] == expected_entries)
    if scenario == 'T5':
        valid = valid and row['idleDriverDurationSeconds'] >= 300
    if scenario in ['T6', 'T7', 'T8', 'T9']:
        valid = valid and row['operationCount'] == 100
    if scenario == 'T9':
        valid = valid and sum(m['operation'] == 'scroll' for m in done['measurements']) == 50 and sum(m['operation'] == 'resize' for m in done['measurements']) == 50
    checks.append({'run': path.name, 'conditionsValid': bool(valid), 'frameTimeouts': row['frameTimeouts']})

complete = len(rows) == 54 and all(c['conditionsValid'] for c in checks)
if not args.partial and not complete:
    raise SystemExit(f'Incomplete or invalid controlled dataset: {len(rows)}/54. {checks}')
settings = {json.dumps(r['ready']['settings'], sort_keys=True) for k, r in runs.items() if k[0] != 'T1'}
if len(settings) > 1:
    raise SystemExit('Settings differ between trials')
order = read(source / 'run-order.json')
expected_order = [(f'T{n}', m, r) for n in range(1, 10) for r in range(1, 4) for m in ['A', 'B']]
actual_order = [(r['Scenario'], r['Mode'], r['Repeat']) for r in order]
if not args.partial and actual_order != expected_order:
    raise SystemExit('Trial order is not A1,B1,A2,B2,A3,B3 per scenario')
if complete and not args.partial:
    integrity = read(source / 'final-integrity.json')
    if not all(integrity[k] for k in ['ProductAndTestsUnchanged', 'FrozenBinariesUnchanged', 'PowerPlanUnchanged']):
        raise SystemExit('Source/build/power integrity check failed')
    env_at_start = read(source / 'environment.json')
    if sorted(env_at_start['GPU'], key=lambda g: g['Name']) != sorted(integrity['GPU'], key=lambda g: g['Name']):
        raise SystemExit('GPU/driver/display configuration changed')
    signatures = {(r['ready']['runtime'], r['ready']['os'], r['ready']['screenWidthDip'], r['ready']['screenHeightDip']) for r in runs.values()}
    if len(signatures) != 1:
        raise SystemExit('Runtime/OS/resolution differs between trials')
write_csv(dest / 'runs.csv', rows)
write_csv(dest / 'operations.csv', operations)
aggregate = []
metric_names = list(dict.fromkeys(k for row in rows for k, v in row.items()
    if isinstance(v, (int, float)) and not isinstance(v, bool) and k != 'repeat'))
for scenario in NAMES:
    for mode in ['A', 'B']:
        group = [r for r in rows if r['scenario'] == scenario and r['mode'] == mode]
        if not group:
            continue
        for metric in metric_names:
            aggregate.append({'scenario': scenario, 'mode': mode, 'metric': metric, 'repetitions': len(group),
                              **stats([r.get(metric) for r in group])})
write_csv(dest / 'aggregate.csv', aggregate)
(dest / 'validation.json').write_text(json.dumps({'complete': complete, 'runs': len(rows), 'alternatingOrder': actual_order == expected_order,
    'settingsIdentical': len(settings) == 1, 'checks': checks}, indent=2), encoding='utf-8')

def group(scenario, mode):
    return [r for r in rows if r['scenario'] == scenario and r['mode'] == mode]

def values(scenario, mode, metric):
    return [r.get(metric) for r in group(scenario, mode)]

def median(scenario, mode, metric):
    return stats(values(scenario, mode, metric))['median']

def save(fig, name):
    fig.savefig(charts / f'{name}.png', dpi=150, bbox_inches='tight')
    fig.savefig(charts / f'{name}.svg', bbox_inches='tight')
    plt.close(fig)

def bars(scenarios, metric, ylabel, name, title):
    fig, ax = plt.subplots(figsize=(10, 4))
    x = np.arange(len(scenarios))
    for i, mode in enumerate(['A', 'B']):
        s = [stats(values(n, mode, metric)) for n in scenarios]
        y = np.array([v['median'] if v['median'] is not None else np.nan for v in s])
        low = np.array([v['min'] if v['min'] is not None else np.nan for v in s])
        high = np.array([v['max'] if v['max'] is not None else np.nan for v in s])
        ax.bar(x + (i - .5) * .36, y, .36, label=LABELS[mode], color=COLORS[mode], yerr=[y-low, high-y], capsize=3)
    ax.set_xticks(x, scenarios)
    ax.set_ylabel(ylabel)
    ax.set_title(title)
    ax.legend()
    ax.grid(axis='y', alpha=.2)
    save(fig, name)

available = [s for s in NAMES if group(s, 'A') and group(s, 'B')]
bars(available, 'privateMaxMB', 'Private Bytes (decimal MB)', 'private-max', 'Median of run maxima; whiskers = range of 3 repetitions')
bars(available, 'gpuSharedMaxMB', 'GPU shared reported usage (decimal MB)', 'gpu-shared', 'Per-process GPU shared memory; do not add to Private Bytes')
active = [s for s in ['T6', 'T7', 'T8', 'T9'] if s in available]
if active:
    bars(active, 'operationCpuAllLogical', 'CPU % of 24 logical processors', 'operation-cpu', 'Operation-phase CPU (excludes warmup and recovery)')
    bars(active, 'layoutMedianMs', 'Milliseconds', 'layout-response', 'Median layout-ready duration; NOT display-present completion')
    bars(active, 'renderingEventMedianMs', 'Milliseconds from operation start', 'rendering-event', 'Return from next Rendering wait; NOT display-present completion')
    bars(active, 'residualPrivateMB', 'Private Bytes residual (decimal MB)', 'residual-memory', 'Recovery last 1s median minus baseline last 1s median; no forced GC')
if 'T5' in available:
    bars(['T5'], 'idleCpuAllLogical', 'CPU % of 24 logical processors', 'idle-cpu', '300-second idle phase CPU; weighted by elapsed time')
fig, axes = plt.subplots(3, 3, figsize=(15, 10))
for ax, scenario in zip(axes.flat, NAMES):
    for mode in ['A', 'B']:
        for repeat in range(1, 4):
            run = runs.get((scenario, mode, repeat))
            if run:
                s = run['samples']
                ax.plot([v['Seconds'] for v in s], [v['PrivateBytes']/MB for v in s], color=COLORS[mode],
                        linestyle=['-', '--', ':'][repeat-1], linewidth=1, label=f'{mode}-{repeat}')
    ax.set_title(scenario)
    ax.set_xlabel('Seconds after warmup')
    ax.set_ylabel('Private Bytes (MB)')
    ax.grid(alpha=.2)
axes.flat[0].legend(fontsize=7, ncol=2)
fig.suptitle('Private Bytes time series: Default (blue), SoftwareOnly (orange); each trial is a new process')
fig.tight_layout()
save(fig, 'memory-timeseries')

if args.partial or not complete:
    print(f'Partial analysis: {len(rows)}/54 trials. Report not finalized.')
    raise SystemExit(0)

env = read(source / 'environment.json')
for name in ['environment.json', 'run-order.json', 'release-build.txt', 'monitor-build.txt',
             'binary-manifest-during.json', 'final-integrity.json', 'test-results.txt']:
    if (source / name).exists():
        shutil.copy2(source / name, dest / name)
for path in source.glob('T[1-9]-[AB]-[1-3]'):
    target = dest / 'raw' / path.name
    target.mkdir(parents=True, exist_ok=True)
    for file in path.iterdir():
        if file.is_file():
            shutil.copy2(file, target / file.name)

def comparison(metric, scenarios=tuple(NAMES), digits=2):
    return table(['試験', 'A Default', 'B SoftwareOnly'], [[f'{s} {NAMES[s]}', cell(values(s, 'A', metric), digits), cell(values(s, 'B', metric), digits)] for s in scenarios])

increase_a = median('T3', 'A', 'privateMeanMB') - median('T2', 'A', 'privateMeanMB')
increase_b = median('T3', 'B', 'privateMeanMB') - median('T2', 'B', 'privateMeanMB')
saving = median('T3', 'A', 'privateMeanMB') - median('T3', 'B', 'privateMeanMB')
quality_path = dest / 'GUI_QUALITY.md'
quality = quality_path.read_text(encoding='utf-8-sig') if quality_path.exists() else '実GUIのA/B操作・スクリーンショット比較は未実施。静止画だけではちらつき・滑らかさを判定できない。人による確認待ち。'
foreground = {mode: sum(r['foregroundAtReady'] for r in rows if r['mode'] == mode) for mode in ['A', 'B']}
module_rows = []
for scenario in ['T1', 'T3', 'T9']:
    for mode in ['A', 'B']:
        modules = sorted({m['ModuleName'] for repeat in range(1,4) for m in runs[(scenario,mode,repeat)]['done']['modules']
                          if any(t in m['ModuleName'].lower() for t in ['d3d', 'dxgi', 'igd', 'nvwg', 'wpfgfx'])})
        module_rows.append([scenario, mode, ', '.join(modules)])
perrun_rows = [[r['run'], f"{r['privateMeanMB']:.2f}/{r['privateMaxMB']:.2f}/{r['privateFinalMB']:.2f}",
                f"{r['workingSetMeanMB']:.2f}/{r['workingSetMaxMB']:.2f}", f"{r['cpuMeanAllLogical']:.4f}/{r['cpuPeakSampleAllLogical']:.2f}",
                f"{r['cpuSeconds']:.3f}/{r['cpuTotalIncludingWarmupSeconds']:.3f}",
                f"{r['privateSampleMedianMB']:.2f}/{r['privateSampleStddevMB']:.3f}"] for r in rows]
timing_rows = []
for scenario in ['T6','T7','T8','T9']:
    for mode in ['A','B']:
        timing_rows.append([scenario, mode, cell(values(scenario, mode, 'layoutMedianMs')), cell(values(scenario, mode, 'layoutP95Ms')),
                           cell(values(scenario, mode, 'layoutMaxMs')), cell(values(scenario, mode, 'renderingEventMedianMs')),
                           cell(values(scenario, mode, 'renderingEventMaxMs')), cell(values(scenario, mode, 'uiQueueP95Ms')),
                           cell(values(scenario, mode, 'uiQueueMaxMs')), sum(r['frameTimeouts'] for r in group(scenario, mode))])
t9_rows = []
for mode in ['A','B']:
    for operation in ['scroll', 'resize']:
        per_repeat = [stats([r['layoutReadyMs'] for r in operations if r['scenario']=='T9' and r['mode']==mode and r['repeat']==repeat and r['operation']==operation]) for repeat in range(1,4)]
        t9_rows.append([mode, operation, cell([s['median'] for s in per_repeat]), cell([s['p95'] for s in per_repeat]), cell([s['max'] for s in per_repeat])])
residual_rows = []
for scenario in ['T6','T7','T8','T9']:
    for mode in ['A','B']:
        residual_rows.append([scenario, mode, cell(values(scenario,mode,'residualPrivateMB')), cell(values(scenario,mode,'recoveryLast5sRangeMB')),
                              cell(values(scenario,mode,'recoveryLast5sSlopeMBPerSecond'), 4)])
gcs = [[r['run'], '未測定' if r['gcHeapFinalMBAtLastGc'] is None else f"{r['gcHeapFinalMBAtLastGc']:.2f}",
        '未測定' if r['gcCommittedFinalMBAtLastGc'] is None else f"{r['gcCommittedFinalMBAtLastGc']:.2f}",
        f"{r['managedFinalMBWithoutGc']:.2f}",r['gcIndexFinal'],r['gcCollectionsDelta'],r['tabsFinal'],r['entriesFinal'],r['realizedRowsFinal']] for r in rows]
environment_table = table(['項目','条件'],[
    ['日時',env['Date']], ['OS',env['OS']], ['.NET',runs[('T2','A',1)]['ready']['runtime'] + '; SDK '+env['SDK']],
    ['CPU', env['CPU'][0]['Name']+'; 16 cores / 24 logical'], ['RAM',f"{env['RAM']/1e9:.2f} GB"],
    ['GPU', '; '.join(g['Name']+' / driver '+g['DriverVersion'] for g in env['GPU'])],
    ['画面','2560×1440 / 165Hz / DPI 150%; Window 1280×800 DIP (1920×1200 pixels), T9は1000×650 DIPとも交互'],
    ['電源',env['PowerPlan']+'; AC接続、試験中のみ消灯・自動スリープ抑止要求'],
    ['Driver SHA256',env['DriverHash']], ['Monitor SHA256',env['MonitorHash']],
    ['条件','Release; warmup 10秒; A/B交互; 各3回; 新規プロセス; 強制GCなし'],
    ['UI','同じ製品MainWindow・設定・既定詳細表示。製品src変更0。測定器は別プロセス。'],
    ['ファイル','同一fixture normal: child 1ディレクトリ＋255空ファイル、child:256空ファイル、large:10,000空ファイル'],
])
report = f'''# PaneCommander 描画方式比較試験（2026-10-13）

本試験は専用ドライバーによる54プロセス（9シナリオ×2方式×3回）。製品の機能・デザイン・既定の描画方式は変更していない。メモリ差は大きいが、実表示完了時間・人による動的な表示品質確認が不足しており、採用判断は保留する。

## 測定条件・環境

{environment_table}

Aは`Default`、Bは`SoftwareOnly`をApplication/Window生成前に設定。設定は試験プロセス限り。同一の凍結Releaseバイナリを全54回で使用した。T1〜T4はウォームアップ後10秒観測、T5は300秒、T6〜T9は5秒ベースライン→100操作→10秒回復待機。T6は左右各3タブを交互に選択、T7はnormalとchildを交互に移動、T8は左右交互の作成→読込→閉鎖を100組、T9は10,000件の左ペインをスクロール50回、ウィンドウサイズ変更50回。

前試行`artifacts/render-comparison`は、ディスプレイ消灯条件の統一前だったため集計から除外。pilotも除外。今回の根拠は`render-comparison-controlled`のみ。電源プラン自体は変更せず、外部モニターのスレッドで一時的な[SetThreadExecutionState](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)要求を保持し、終了時に解除。ユーザー操作によるロックやスリープを防ぐ設定ではない。

ウィンドウは画面内に表示。トップ最前面固定・前面維持・他アプリの描画負荷抑制は実施していない。ready時IsActiveはA {foreground['A']}/27、B {foreground['B']}/27であり、前面・遮蔽・他アプリ負荷が厳密に一定だったとの主張はしない。全回のDPI、解像度、設定、AC接続はログで検査。同一ディレクトリを使用したが、File Ageは時計の進行に伴い変化する。GPU温度・クロック・実電力は未測定。3反復は同一PC内のばらつきであり、他PCへの一般化や統計的有意差を示すものではない。

## 測定点と読み方

- MBは10進（1 MB = 1,000,000 bytes）。メモリ値はウォームアップ後から終了記録まで、ウィンドウを閉じる前の観測。終了時はOSプロセス終了後の0ではない。外部から約250ms間隔で観測し、実間隔はruns.csvに記録。平均はサンプルの算術平均。
- CPUは24論理CPU合計を100%とした値。raw CSVには1コア100%表記も保存。CPU累計はプロセスCPU時間であり、複数スレッドの合計。平均はCPU時間差÷経過時間÷24。ピークは約250ms区間平均の最大で、真の瞬間ピークではない。初回サンプルCPU率0はピーク計算から除外。
- `layoutReadyMs`はモデル操作開始→NavigateAsync/IsLoading終了→Dispatcher.ApplicationIdle到達。バインド・レイアウト処理を含むプロキシで、画面に現れた時刻ではない。
- `renderingEventMs`は同じ操作開始→次のCompositionTarget.Renderingを待機してUI側の呼出し元へ戻った時刻。コールバック内で刻時していないため、イベントそのものの通知時刻ではなく、awaitの継続処理の遅延も含む。[イベント通知はレイアウト後、描画前](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget)だが、この計測値はGPU処理完了・DWM present・画面表示完了のいずれも証明しない。各操作後の通知待ちと100ms間隔のUIキュー観測は試験専用で、アイドルには常設しない。起動初期表示・10,000件の最初のロード時間は今回の応答統計外。
- UI遅延は別スレッドからDispatcher.Inputへ投入したコールバックの待ち時間。OSの入力受付から画面応答までの遅延ではない。ドライバー呼出しは実マウス／キー入力と区別する。
- GC Heap/Committedは`GC.GetGCMemoryInfo()`の**最後に実行された自然GC時点**。[MicrosoftのGC API説明](https://devblogs.microsoft.com/dotnet/the-updated-getgcmemoryinfo-api-in-net-5-0-and-how-it-can-help-you/)のとおり、現在時刻の厳密な使用量ではない。GC index=0の場合は未測定扱い。`GC.GetTotalMemory(false)`は回収可能な未回収オブジェクトも含み、リーク量とは解釈しない。
- GPUカウンターはPDH GPU Process Memoryの対象PIDインスタンス合計（Dedicated/Shared）、GPU Engineは対象PIDの最大エンジン値。OSが対象PIDに対して報告したGPUメモリ使用量であり、Private Bytesと足し合わせない。[Microsoftの解説](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)ではプロセス間で共有するGPUメモリも各PIDに計上されるため、PID間の単純合計にも重複がある。Private Bytesの差をGPUドライバー占有量とは断定しない。

以下の比較表は**3回の中央値 [最小–最大]**。3回の標準偏差は[aggregate.csv](docs/verification/render-comparison/aggregate.csv)、各回の全項目は[runs.csv](docs/verification/render-comparison/runs.csv)、全操作時間は[operations.csv](docs/verification/render-comparison/operations.csv)。目標未達・未測定項目は合格にしていない。

## メモリ

Private Bytes平均（MB）:

{comparison('privateMeanMB')}

Private Bytes最大（MB）:

{comparison('privateMaxMB')}

Private Bytes終了時（MB）:

{comparison('privateFinalMB')}

Working Set平均／最大は下表の各回一覧とruns.csvに保存。

![Private Bytes最大](docs/verification/render-comparison/charts/private-max.png)

![メモリ時系列](docs/verification/render-comparison/charts/memory-timeseries.png)

2→6タブのPrivate Bytes平均の中央値差はA **{increase_a:.2f} MB**、B **{increase_b:.2f} MB**。これは独立したT2/T3プロセス群間の差で、同一プロセス内の増分や確保元の直接測定ではない。6タブでのBによる削減は **{saving:.2f} MB**（{100*saving/median('T3','A','privateMeanMB'):.1f}%）。非表示タブのVisual Treeが増えた証拠ではなく、[前回の寿命調査](VISUAL_RESOURCE_LIFECYCLE_REPORT.md)では左右2ビューの共有と古い行の回収を確認済み。

WPFの基本消費との比較（Private Bytes平均の3回中央値、独立群間の差）:

{table(['指標','A MB','B MB'],[
    ['空のWPF',f"{median('T1','A','privateMeanMB'):.2f}",f"{median('T1','B','privateMeanMB'):.2f}"],
    ['実画面2タブ − 空WPF',f"{median('T2','A','privateMeanMB')-median('T1','A','privateMeanMB'):.2f}",f"{median('T2','B','privateMeanMB')-median('T1','B','privateMeanMB'):.2f}"],
    ['実画面6タブ − 空WPF',f"{median('T3','A','privateMeanMB')-median('T1','A','privateMeanMB'):.2f}",f"{median('T3','B','privateMeanMB')-median('T1','B','privateMeanMB'):.2f}"],
])}

空WPFはアプリのリソース辞書・VMを作らない対照で、ウィンドウ寸法は同一。同じドライバーを使用するため、参照先のPaneCommander.App/Core/Infrastructureアセンブリもロードされる。純粋なWPFランタイムだけの絶対値ではなく、同じ試験ホストで空のWindowを表示した基準である。差にはアプリのデータ・コントロール・WPF/CLRの追加ロード・描画割当が混在する。基本消費との切り分けの対照であり、すべてを製品の独自オブジェクト量とは解釈しない。

暫定メモリ目標との照合（各方式・3回の最大Private Bytes）:

{table(['試験','目標MB','A最大/結果','B最大/結果'], [[s,300 if s in ['T4','T9'] else 200, *[f"{max(values(s,m,'privateMaxMB')):.2f} / {'達成' if max(values(s,m,'privateMaxMB')) <= (300 if s in ['T4','T9'] else 200) else '未達'}" for m in ['A','B']]] for s in NAMES if s != 'T1'])}

T1は空のWPF基準。製品の起動直後150MB目標は、本試験のウォームアップ済み画面からは判定しない（未測定）。Working SetやGPU分を含めた別指標では数値が異なるため、上記はPrivate Bytesに限定した照合。

## CPU

全観測区間のCPU平均（%）:

{comparison('cpuMeanAllLogical', digits=4)}

操作区間のみのCPU平均（%）:

{comparison('operationCpuAllLogical', ['T6','T7','T8','T9'], 4)}

B−Aの操作区間CPU増分（パーセントポイント、3回中央値の差）: T6 **{median('T6','B','operationCpuAllLogical')-median('T6','A','operationCpuAllLogical'):+.4f}**、T7 **{median('T7','B','operationCpuAllLogical')-median('T7','A','operationCpuAllLogical'):+.4f}**、T8 **{median('T8','B','operationCpuAllLogical')-median('T8','A','operationCpuAllLogical'):+.4f}**、T9 **{median('T9','B','operationCpuAllLogical')-median('T9','A','operationCpuAllLogical'):+.4f}**。操作中のUIキュー観測・Renderingイベント待機を含む専用ドライバーの値であり、実マウス操作のCPU値とは区別する。

T5アイドル区間CPU平均（%）: A **{cell(values('T5','A','idleCpuAllLogical'),4)}**、B **{cell(values('T5','B','idleCpuAllLogical'),4)}**。ドライバーのidle→end時刻差は全6回300秒以上であり、外部の最初／最後のサンプル間隔と混同しない。終了時JSON記録のCPUはアイドル区間平均から除外。開始直後の基準チェックポイント処理は先頭サンプル区間に含まれ得る。補助値として先頭・末尾各1秒を除いた定常区間はA **{cell(values('T5','A','idleSteadyCpuAllLogical'),4)}%**、B **{cell(values('T5','B','idleSteadyCpuAllLogical'),4)}%**。後者は300秒全体の平均とは区別する。

![操作中CPU](docs/verification/render-comparison/charts/operation-cpu.png)

![アイドルCPU](docs/verification/render-comparison/charts/idle-cpu.png)

## 操作応答

各回100操作。単位ms、表は各回の統計の3反復中央値 [範囲]。最大欄の範囲上限が全300操作中の最大。

{table(['試験','方式','layout中央値','layout p95','layout最大','Rendering通知中央値','Rendering通知最大','UIキューp95','UIキュー最大','通知timeout計'],timing_rows)}

layout中央値のB−A差はタブ切替 **{median('T6','B','layoutMedianMs')-median('T6','A','layoutMedianMs'):+.2f} ms**、フォルダ移動 **{median('T7','B','layoutMedianMs')-median('T7','A','layoutMedianMs'):+.2f} ms**。この差から実際の画面応答や滑らかさの優劣を直接判定しない。

T9の内訳（layoutのみ、実表示完了ではない）:

{table(['方式','操作','中央値','p95','最大'],t9_rows)}

![レイアウト応答](docs/verification/render-comparison/charts/layout-response.png)

![描画前通知](docs/verification/render-comparison/charts/rendering-event.png)

## 操作後の残留・安定性

残留量は回復区間末尾1秒のPrivate Bytes中央値−操作前末尾1秒の中央値。操作直後のピークや強制GC後の生存ヒープ量ではない。短い回復待機で未回収オブジェクトや再利用キャッシュも残るため、残留だけでリークと判定しない。

{table(['試験','方式','残留MB','回復末尾5秒の幅MB','同区間傾きMB/秒'],residual_rows)}

100回のフォルダ移動後の残留量中央値はA **{median('T7','A','residualPrivateMB'):.2f} MB**、B **{median('T7','B','residualPrivateMB'):.2f} MB**。スクロール／リサイズ後はA **{median('T9','A','residualPrivateMB'):.2f} MB**、B **{median('T9','B','residualPrivateMB'):.2f} MB**で、10秒待機しても基準値には戻っていない。一方、回復末尾5秒の幅は大部分が約0.08MB以下で、短い観測では横ばいまたは減少。ただしT8 B2は約2.17MBの変動があり、すべて完全一定だったとは扱わない。保持キャッシュ・未回収メモリ・ネイティブ資源の所有者の内訳は、この差だけでは分からない。

![操作後残留](docs/verification/render-comparison/charts/residual-memory.png)

T5の300秒中のPrivate Bytes増減: A **{cell(values('T5','A','idlePrivateDeltaMB'))} MB**、B **{cell(values('T5','B','idlePrivateDeltaMB'))} MB**。同区間の最大−最小: A **{cell(values('T5','A','idlePrivateRangeMB'))} MB**、B **{cell(values('T5','B','idlePrivateRangeMB'))} MB**。300秒および100操作の観測だけでは、長時間利用の継続的増加なしを証明できない。

## GPU・実描画経路

GPU Shared最大（MB）:

{comparison('gpuSharedMaxMB')}

GPU Dedicated最大（MB）:

{comparison('gpuDedicatedMaxMB')}

GPU Engine区間ピーク（%）:

{comparison('gpuEnginePeakPercent')}

![GPU共有割当](docs/verification/render-comparison/charts/gpu-shared.png)

ロード済み描画関連モジュール（各条件3回の和集合）:

{table(['試験','方式','モジュール'],module_rows)}

全回RenderCapability Tier=2はハードウェア能力の情報であり、当該フレームの経路の証明ではない。[ProcessRenderModeは設定上の優先指定](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.processrendermode)で、Defaultで全描画がGPUになる保証はない。GPU割当、操作中GPU Engineカウンター、ロードモジュールは経路の補助証拠。BでもGPU共有割当が観測されるが、その確保元は特定していない。SoftwareOnlyはOS全体のGPU処理禁止を意味しない。WPF/MIL→Direct3D→ドライバーの個々のネイティブ確保スタック、GPUアダプター別内訳、フレームごとのHW/SW混在割合は未測定。ETW/PresentMon等によるpresent完了・アダプター別イベント採取は行っていない。DWMを含むシステム全体のCPU消費は対象PIDのCPU消費と別であり、今回のCPU表には含めない。

A/B双方にD3D9/D3D12・Intelドライバーモジュールがロードされており、DLLの存在だけでは方式を区別できない。操作中のGPU EngineピークはT6でA **{cell(values('T6','A','gpuEnginePeakPercent'))}%**、B **{cell(values('T6','B','gpuEnginePeakPercent'))}%**。Default群で対象PIDのGPU活動を観測したことは確認できるが、どの描画命令がHW/SWだったかまでは確定しない。

## 実GUIと表示品質

{quality}

## 各回の結果

CPU平均は全観測区間、ピークは約250ms区間平均。CPU秒は観測区間差／起動・ウォームアップ含む累計。Private中央値/σは各回内のサンプル統計で、3回間のσとは別。

{table(['run','Private平均/最大/終了MB','WS平均/最大MB','CPU平均/ピーク%','CPU秒 差/累計','Private中央値/σ MB'],perrun_rows)}

GCの最終チェックポイント（自然GCの最終時点、Gen0/1/2増分）:

{table(['run','GC Heap MB','GC Committed MB','managed false MB','GC index','GC増分','タブ','保持項目','実体行'],gcs)}

## 再現手順・証跡

1. 同じPC・AC電源・バランスプラン・2560×1440/150%/165Hzで、他の重い処理を避ける。製品の表示設定ファイルはドライバーで読まず同一既定設定を使用。
2. `dotnet build PaneCommander.sln -c Release`、`dotnet build tools/PaneCommander.Performance/PaneCommander.Performance.csproj -c Release`、`dotnet build tools/PaneCommander.RenderMonitor/PaneCommander.RenderMonitor.csproj -c Release`。Releaseログは証跡ディレクトリに保存。ビルド後は54回完了まで再ビルドしない。
3. 未作成の出力先を指定し、PowerShellで `./tools/Start-RenderingComparison.ps1 -Output artifacts/render-comparison-new`。環境・ビルドのハッシュを保存し、Compare-Renderingを実行、終了後に整合性検査を行う再現用ラッパー。今回の54回は同じ内容の環境採取とCompare-Renderingの直接実行で測定した。fixtureは同じ作業ディレクトリに再利用される。T5だけで約31分、全体は40分以上。PCを操作せず、ウィンドウの前面状態も揃えるのが望ましい。
4. Pythonで`tools/Analyze-Rendering.py --input artifacts/render-comparison-new`。numpy/matplotlibは解析環境のみに必要で製品依存なし。今回の解析用依存はartifacts/render-analysis-depsに隔離。
5. 実GUIは別プロセスの`render GUI <fixture> <output> A`またはBで起動、ready後に操作し、出力先にfinish.txtを作成して終了。数値試験と重ねない。今回の実操作手順はGUI_QUALITY.mdを参照。

[条件検査](docs/verification/render-comparison/validation.json)、[実行順](docs/verification/render-comparison/run-order.json)、[環境](docs/verification/render-comparison/environment.json)、[生ログ54組](docs/verification/render-comparison/raw)、[全数値](docs/verification/render-comparison/runs.csv)、[3回間の中央値・最大・σ](docs/verification/render-comparison/aggregate.csv)。ソースはtools/PaneCommander.Performance/RenderComparison.cs、tools/PaneCommander.RenderMonitor/Program.cs、tools/Compare-Rendering.ps1、tools/Analyze-Rendering.py。製品の描画設定を変更するコードは追加していない。

Releaseビルドは警告0・エラー0。終了後の既存テストは**32/32合格、スキップ0**（[ログ](docs/verification/render-comparison/test-results.txt)）。[終了後の整合性検査](docs/verification/render-comparison/final-integrity.json)で承認済み製品・テスト40ファイルと使用バイナリ／構成14ファイルのハッシュ一致、電源プラン不変を確認。GUI試行の許可タイムアウトは数値試験54回の失敗ではなく、別群の未測定として扱う。

## 結論

6タブのPrivate Bytes平均はA **{median('T3','A','privateMeanMB'):.2f} MB**、B **{median('T3','B','privateMeanMB'):.2f} MB**、差 **{saving:.2f} MB**。タブ切替の操作区間CPUはA **{median('T6','A','operationCpuAllLogical'):.4f}%**、B **{median('T6','B','operationCpuAllLogical'):.4f}%**。切替layout中央値はA **{median('T6','A','layoutMedianMs'):.2f} ms**、B **{median('T6','B','layoutMedianMs'):.2f} ms**。フォルダ移動layout中央値はA **{median('T7','A','layoutMedianMs'):.2f} ms**、B **{median('T7','B','layoutMedianMs'):.2f} ms**。スクロール・リサイズ複合操作のCPUはA **{median('T9','A','operationCpuAllLogical'):.4f}%**、B **{median('T9','B','operationCpuAllLogical'):.4f}%**。CPUと応答のばらつき・最大値は上記表を参照。

T9のlayout中央値はA **{median('T9','A','layoutMedianMs'):.2f} ms**、B **{median('T9','B','layoutMedianMs'):.2f} ms**。全3回の最大はA **{max(values('T9','A','layoutMaxMs')):.2f} ms**、B **{max(values('T9','B','layoutMaxMs')):.2f} ms**、UIキュー最大はA **{max(values('T9','A','uiQueueMaxMs')):.2f} ms**、B **{max(values('T9','B','uiQueueMaxMs')):.2f} ms**であり、Bがすべての操作で速いとはいえない。どちらにも一時的な長い待ちが観測され、滑らかさの実GUI確認が必要。

メモリ削減は確認できたが、実表示完了時間、人による文字・色・ちらつき・滑らかさ確認、前面・他アプリ負荷の厳密統一、長時間の安定性は不足。メモリ差だけで採用を決めず、製品の既定描画方式は維持する。

結論：判断保留
'''
report = re.sub(r'\]\(([^)\n]+)\)', lambda m: m.group(0) if m.group(1).startswith(('http:', 'https:', 'C:/', '/'))
    else '](' + (ROOT / m.group(1)).as_posix() + ')', report)
(ROOT / 'RENDERING_COMPARISON_REPORT.md').write_text(report, encoding='utf-8')
print(f'Final analysis: {len(rows)} trials; report and artifacts written to {dest}')
