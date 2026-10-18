"""Summarize native acquisition and actual popup trials without conflating them."""
import csv
import json
import statistics as st
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / 'docs/verification/paste-authorized-20261015'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
out = base / 'shell-analysis'
out.mkdir(exist_ok=True)
rows = []
for kind in ('baseline', 'file', 'directory', 'multiple', 'background'):
    for repeat in range(1, 4):
        p = base / 'shell-acquire' / f'{kind}-{repeat}'
        data, conditions = read(p/'samples.json'), read(p/'conditions.json')
        measured = [r for r in data if r['Phase'] == 'repeat']
        assert len(measured) == 400 and conditions['ForcedGC'] is False
        a, b = measured[0], measured[-1]
        elapsed = (b['ElapsedMs']-a['ElapsedMs'])/1000
        rows.append(dict(kind=kind, repeat=repeat,
            privateDeltaMB=(b['PrivateBytes']-a['PrivateBytes'])/1e6,
            privateMaxMB=max(r['PrivateBytes'] for r in measured)/1e6,
            privateEndMB=b['PrivateBytes']/1e6,
            workingSetDeltaMB=(b['WorkingSet']-a['WorkingSet'])/1e6,
            handlesDelta=b['Handles']-a['Handles'], handlesEnd=b['Handles'],
            managedDeltaMB=(b['ManagedBytes']-a['ManagedBytes'])/1e6,
            cpuPercent=(b['CpuSeconds']-a['CpuSeconds'])/elapsed/conditions['Processors']*100,
            durationMedianMs=st.median(r['DurationMs'] for r in measured),
            durationMaxMs=max(r['DurationMs'] for r in measured)))
with (out/'acquire-runs.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
lines=['# Shellリソース再測定', '',
    '取得・解放試験：各種類3独立プロセス、20回ウォームアップ後400回。強制GCなし。Popup表示・GUI入力なし。背景は新IExplorerBrowserホスト。CPUは24論理CPUで正規化。増分は測定先頭（iteration20）から末尾（419）まで。単位は十進MB。', '',
    '|種類|Private増分中央値 [最小–最大] MB|ハンドル増分中央値 [最小–最大]|CPU中央値 %|取得解放時間中央値 ms|',
    '|---|---:|---:|---:|---:|']
for kind in ('baseline','file','directory','multiple','background'):
    group=[r for r in rows if r['kind']==kind]
    def interval(key):
        v=[r[key] for r in group];return f'{st.median(v):.3f} [{min(v):.3f}–{max(v):.3f}]'
    lines.append(f"|{kind}|{interval('privateDeltaMB')}|{interval('handlesDelta')}|{st.median(r['cpuPercent'] for r in group):.4f}|{st.median(r['durationMedianMs'] for r in group):.3f}|")
lines += ['', 'Private Bytesの増加をGPUや外部Shell拡張の所有量と断定しない。診断ドライバー自身のサンプル保持、管理ヒープの自然GC時期、Shellキャッシュ等を含む。GCCommitted=0は自然GC情報未取得の可能性があり、実使用量ゼロを意味しない。短期400回のハンドル安定を長期安定性の合格にしない。旧測定とはソース・環境・経路が異なるため直接の修正効果量とはしない。', '', '## 実GUI背景Popup', '',
    '取得・解放だけの試験とは別。Shift+F10とEscを実入力し、各入力直後にアクセシビリティ取得、各100ms待機。外部の読み取り専用GetGUIThreadInfoでメニュー所有HWNDとPopupモードを確認。GUI取得・バッチ間待機を含み、実描画完了時間ではない。DefaultとSoftwareOnlyは異なる日時・待機時間の探索的測定であり、公平なCPU比較には使わない。', '']
for mode, folder in [('Default','popup-default-background'),('SoftwareOnly','popup-software-background')]:
    p=base/folder
    if not (p/'conditions.json').exists():
        lines.append(f'- {mode}：未完了。');continue
    conditions=read(p/'conditions.json')
    with (p/'samples.csv').open(encoding='utf-8-sig',newline='') as f: samples=list(csv.DictReader(f))
    trial=[s for s in samples if s['Stage']=='background-100']
    events=[json.loads(s) for s in (p/'popup-events.jsonl').read_text().splitlines()]
    entering=sum(e['Entering'] and e['Stage']=='background-100' for e in events)
    exiting=sum(not e['Entering'] and e['Stage']=='background-100' for e in events)
    if trial:
        a,b=trial[0],trial[-1]
        lines.append(f"- {mode}：測定Popup enter/exit={entering}/{exiting}、ウォームアップ含む開始{conditions['PopupStarts']}回、照会失敗{conditions['GuiThreadQueryFailures']}。測定区間Private増分{(int(b['PrivateBytes'])-int(a['PrivateBytes']))/1e6:.3f} MB、ハンドル差{int(b['Handles'])-int(a['Handles'])}。終了時Private {int(samples[-1]['PrivateBytes'])/1e6:.3f} MB、ハンドル{samples[-1]['Handles']}。")
lines += ['', '500/1600回、ファイル・ディレクトリ別Popup反復は未実施。実GUI計装後のCPU負荷は無計装アイドルと一致せず、取得ツールの影響との切り分け待ち。継続増加の原因未確定につきリソース寿命・性能の受入は保留。']
(out/'REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
sys.path.insert(0, str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig, axes = plt.subplots(2, 1, figsize=(10, 7), sharex=True)
for kind in ('baseline','file','directory','multiple','background'):
    data=read(base/'shell-acquire'/f'{kind}-1'/'samples.json')
    data=[r for r in data if r['Phase']=='repeat']
    axes[0].plot([r['Iteration']-20 for r in data],[r['PrivateBytes']/1e6 for r in data],label=kind)
    axes[1].plot([r['Iteration']-20 for r in data],[r['Handles'] for r in data],label=kind)
axes[0].set_ylabel('Private Bytes (decimal MB)');axes[0].legend()
axes[1].set_ylabel('Process handles');axes[1].set_xlabel('Measured acquisition index (run 1 only)')
fig.suptitle('Native acquisition/disposal, no popup, no forced GC')
fig.tight_layout();fig.savefig(out/'native-timeseries.png',dpi=140);plt.close(fig)
print(out/'REPORT.md')
