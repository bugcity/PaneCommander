"""Record the authorized, rolled-back GPU preference experiment without product changes."""
from pathlib import Path
import csv, json, statistics

root = Path(__file__).resolve().parents[1]
out = root / 'docs/verification/default-redraw-20261015'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
state = out / 'nvidia-preference-state'
trial = out / 'nvidia-preference-default'
before = read(state / 'gpu-setting-backup.json')
after = read(state / 'gpu-setting-restored.json')
assert before['UserAuthorized'] and not before['Before']['ValueExisted']
assert after['Restored'] and not after['ValueExisted']
route = read(out / 'gpu-route-nvidia-1/gpu-route-summary.json')
restored_route = read(out / 'gpu-route-nvidia-after-restore/gpu-route-summary.json')
assert route['TargetProcessId'] == restored_route['TargetProcessId'] == 18084
physical_path = trial / 'physical-observation.json'
physical = read(physical_path) if physical_path.exists() else None
physical_status = 'PID18084との対応を照合し、実画面の白表示継続を確認。今回のHover挙動は未確認。' if physical else '新プロセスの実画面は確認待ち。'
samples_path = trial / 'idle-reference/samples.csv'
metrics = None
if samples_path.exists() and (trial / 'idle-reference/conditions.json').exists():
    rows = list(csv.DictReader(samples_path.open(encoding='utf-8-sig')))
    seconds = float(rows[-1]['ElapsedSeconds']) - float(rows[0]['ElapsedSeconds'])
    metrics = {'SampledSeconds': seconds, 'CpuAllLogicalMeanPercent':
               100 * (float(rows[-1]['CpuSeconds']) - float(rows[0]['CpuSeconds'])) /
               seconds / int(rows[0]['Processors']), 'LogicalProcessors': int(rows[0]['Processors'])}
    for key in ['PrivateBytes', 'WorkingSet', 'Handles']:
        values = [int(r[key]) for r in rows]
        metrics[key] = dict(Mean=statistics.mean(values), Max=max(values), End=values[-1], Start=values[0])
result = dict(UserAuthorized=True, ProductCodeChanged=False, PreferenceRestored=True,
              SettingBackup=before, SettingRestored=after, RouteDuringPreference=route,
              RouteAfterRestore=restored_route, PhysicalObservation=physical,
              PhysicalDisplayStatus='Awaiting user observation' if physical is None else physical,
              ReferenceMetrics=metrics, PerFrameExecutionTraceAvailable=False,
              Interpretation='NVIDIA allocation and UMD loaded; Intel allocation and Intel DWM remain. Not an NVIDIA-only path or proof of fault ownership.')
(out / 'gpu-preference-trial-summary.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')

report_path = root / 'DEFAULT_REDRAW_INVESTIGATION_REPORT.md'
report = report_path.read_text(encoding='utf-8')
report = report.split('\n## 承認後の一時NVIDIA指定試験')[0]
report = '\n'.join(line for line in report.split('\n') if not line.startswith('最新の限定試験：'))
latest = ('\n最新の限定試験：承認された診断EXEだけの一時NVIDIA指定を実施し、元の個別指定なしへ復元済み。'
          '新規PID18084ではNVIDIAとIntel両側の割当てを確認。' + physical_status +
          '製品修正や原因確定は行っていない。\n')
if latest.strip() not in report:
    report = report.replace('\n## 今回分かったこと', latest + '\n## 今回分かったこと', 1)
report = report.replace('ユーザーの実施前確認を得るまでGPU指定・OS設定は変更しない。',
                        'この提案はその後ユーザーから承認され、以下の限定試験を実施して設定を復元した。')
report += '''
## 承認後の一時NVIDIA指定試験

ユーザーの「一時的に指定して対照試験を行う」という承認に基づき、診断用PaneCommander.Performance.exeだけにGpuPreference=2を一時設定。旧PID8120を通常終了し、同じReleaseバイナリ・Default・800×640・DPI1.5・.NET10.0.12の新規PID18084を起動した。製品EXE、グローバルGPU設定、電源・ディスプレイ・ドライバーは変更していない。

取得前の値を保存し、try/finallyで元の「個別指定なし」へ復元した。設定は2026-10-15 07:00:52〜07:01:05 JSTの約13秒間だけ適用。設定キーも元々存在せず、復元時に他値・サブキーがないことを確認して空キーだけ除去。gpu-setting-backup/applied/restored.jsonを保存した。

|対象PID18084のGPU割当て（各5回）|専用 MiB|共有 MiB|LUID|
|---|---:|---:|---|
|NVIDIA GeForce RTX 4060 Laptop|8.60|1.76|00000000:0000ECE7|
|Intel UHD Graphics|0|64.83|00000000:0000E948|

NVIDIAのnvldumdx.dll、nvd3dumx.dll（32.0.16.1742）等を新たに確認。設定なしの旧プロセスではIntel側のみ観測されたため、指定によってGPUリソース経路が変化した証拠になる。一方でIntelの割当て・UMDも残り、DWMもIntel側に残る。**NVIDIAだけで描画・合成しているとは判定しない。** NVIDIA側の各フレーム実行・Presentのトレースは未取得。nvspcap64.dllも読み込まれており、単純なGPU一要因の完全分離でもない。

復元後も同じPIDを再採取し、両GPUの割当て値が5回とも維持されていることを確認。起動時指定の対照用プロセスは残し、実画面の初期表示・Hover部分描画についてユーザー確認を依頼した。このプロセスには確認依頼までキャプチャ・UIA取得・自動マウス操作を行っていない。実機回答前に白画面の改善／再現を断定しない。

原データ：docs/verification/default-redraw-20261015/gpu-route-nvidia-1/、gpu-route-nvidia-after-restore/、nvidia-preference-state/。復元後の同一プロセスでの外部リソース参考採取はnvidia-preference-default/idle-reference/。ユーザーの画面確認を許容したため無入力を保証せず、起動からの時間も前回と揃っていない。正式な性能比較・長期安定性・製品受入には使用しない。強制GC、描画タイマー、強制再描画、製品修正はなし。
'''
if metrics:
    report += (f"\n参考採取 {metrics['SampledSeconds']:.2f}秒：Private Bytes平均/最大/終了 "
               f"{metrics['PrivateBytes']['Mean']/2**20:.2f}/{metrics['PrivateBytes']['Max']/2**20:.2f}/{metrics['PrivateBytes']['End']/2**20:.2f} MiB、"
               f"Working Set平均/最大/終了 {metrics['WorkingSet']['Mean']/2**20:.2f}/{metrics['WorkingSet']['Max']/2**20:.2f}/{metrics['WorkingSet']['End']/2**20:.2f} MiB、"
               f"ハンドル {metrics['Handles']['Start']}→{metrics['Handles']['End']}、CPU全{metrics['LogicalProcessors']}論理平均 {metrics['CpuAllLogicalMeanPercent']:.5f}%。\n")
if physical:
    report += '\n実機回答：' + json.dumps(physical, ensure_ascii=False) + '\n'
    report = report.replace('実機回答前に白画面の改善／再現を断定しない。',
                            'その後の実機回答は内部白表示。現在唯一の可視プローブHWND 0xDF058C→PID18084、作成時刻・コマンドライン・launch/readyのPID一致を照合し、NVIDIA指定で起動した両GPU割当てプロセスでも白表示継続と記録した。観測瞬間そのもののHWNDログはないという限界を併記する。Hover挙動は今回未報告。NVIDIA単独描画の結果や故障所有者の特定とは扱わない。')
report_path.write_text(report, encoding='utf-8')
summary_path = out / 'verification-summary.json'
summary = read(summary_path)
summary['GpuPreferenceTrial'] = result
summary['WaitingFor'] = 'Physical observation of PID18084; detailed frame trace remains unavailable' if physical is None else 'Further component isolation; detailed frame trace remains unavailable'
summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
notice = ('Default再描画の最新対照試験：[DEFAULT_REDRAW_INVESTIGATION_REPORT.md](DEFAULT_REDRAW_INVESTIGATION_REPORT.md)。'
          'GUI取得なしの最小WPFでも実機白表示・Hover領域だけの描画を確認済み。承認済みの診断EXE一時NVIDIA指定でNVIDIAとIntel両側の割当てを確認。個別GPU指定は復元済み。' + physical_status + '原因未確定。\n\n')
for name in ['GUI_REMAINING_ISSUES_REPORT.md', 'SHELL_CONTEXT_IMPLEMENTATION_REPORT.md', 'SHELL_CONTEXT_CAUSE_REPORT.md', 'SHELL_CONTEXT_PERFORMANCE_REPORT.md', 'SCREEN_OFF_DISABLED_GUI_REPORT.md', 'IMPLEMENTATION_STATUS.md']:
    p = root / name
    text = p.read_text(encoding='utf-8-sig')
    if text.startswith('Default再描画の最新対照試験：'):
        text = text.split('\n\n', 1)[1]
    p.write_text(notice + text, encoding='utf-8')
shell_summary = root / 'docs/verification/shell-context/verification-summary.json'
combined = read(shell_summary)
combined['Current'] = summary
shell_summary.write_text(json.dumps(combined, ensure_ascii=False, indent=2), encoding='utf-8')
checklist = root / 'docs/verification/shell-context/GUI_CHECKLIST.md'
text = checklist.read_text(encoding='utf-8-sig')
lines = text.splitlines()
lines[1] = '最新：承認済みの最小WPF限定・一時NVIDIA指定を実施し復元済み。NVIDIAとIntelのGPU割当てを確認、DWMはIntel。' + physical_status + '既存のno-capture-default実機白表示・Hover部分描画は確認済み。原因特定・修正・受入は未完了。'
text = '\n'.join(lines).replace('キャプチャ未接続の新規Defaultの物理表示を確認待ち。', '旧no-capture-defaultの物理白表示はユーザー確認済み。')
checklist.write_text(text + '\n', encoding='utf-8')
print(json.dumps(dict(PreferenceRestored=True, ObservedAdapters=route['ObservedTargetAdapters'], PhysicalObservation=physical, ReferenceMetrics=metrics), ensure_ascii=False))
