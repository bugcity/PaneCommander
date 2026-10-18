from pathlib import Path
import csv, hashlib, json, os, statistics, sys

root=Path(__file__).resolve().parents[1]
if (root/'artifacts/shell-hardening-after-mixed').exists():
    import runpy
    runpy.run_path(str(root/'tools/Analyze-ShellHardening.py'),run_name='__main__')
    sys.exit(0)
run=root/'artifacts/shell-context-performance-sta'
out=root/'docs/verification/shell-context'
out.mkdir(parents=True,exist_ok=True)
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
env=read(run/'environment.json')
assert env['ProductHashBefore']==env['ProductHashAfter']
results=[]; all_rows=[]
for number in range(1,4):
    rows=read(run/f'run-{number}/samples.json'); conditions=read(run/f'run-{number}/conditions.json')
    assert conditions['ForcedGC'] is False and conditions['Iterations']==1620
    repeat=[r for r in rows if r['Phase']=='repeat']; assert len(repeat)==1600
    warm=[r for r in rows if r['Phase']=='warmup'][-1]; end=rows[-1]
    duration=(repeat[-1]['ElapsedMs']-warm['ElapsedMs'])/1000
    cpu=repeat[-1]['CpuSeconds']-warm['CpuSeconds']
    result=dict(Run=number,PrivateWarmMiB=warm['PrivateBytes']/2**20,PrivateFinalMiB=end['PrivateBytes']/2**20,PrivateDeltaMiB=(end['PrivateBytes']-warm['PrivateBytes'])/2**20,PrivateMaxMiB=max(r['PrivateBytes'] for r in repeat)/2**20,WorkingSetMeanMiB=statistics.mean(r['WorkingSet'] for r in repeat)/2**20,WorkingSetMaxMiB=max(r['WorkingSet'] for r in repeat)/2**20,HandlesWarm=warm['Handles'],HandlesFinal=end['Handles'],HandlesDelta=end['Handles']-warm['Handles'],DurationMedianMs=statistics.median(r['DurationMs'] for r in repeat),DurationMaxMs=max(r['DurationMs'] for r in repeat),CpuSeconds=cpu,CpuMeanAllLogicalPercent=100*cpu/duration/conditions['Processors'],IdleAfterCpuSeconds=end['CpuSeconds']-rows[-2]['CpuSeconds'],ManagedFinalMiB=end['ManagedBytes']/2**20,GCCommittedFinalMiB=end['GCCommitted']/2**20)
    results.append(result);all_rows.extend(dict(Run=number,**r) for r in rows)
for name,data in [('performance-runs.csv',results),('performance-samples.csv',all_rows)]:
    with (out/name).open('w',encoding='utf-8-sig',newline='') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
summary={k:dict(Median=statistics.median(r[k] for r in results),Max=max(r[k] for r in results),Min=min(r[k] for r in results),Spread=max(r[k] for r in results)-min(r[k] for r in results)) for k in results[0] if k!='Run'}
summary.update(ActualPopupMeasured=False,ForcedGC=False,PerformanceAcceptance='Unmet in driver; actual popup unverified',Environment=env)
(out/'performance-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
os.environ['MPLCONFIGDIR']=str(root/'artifacts/shell-plot-cache');sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
figure,axes=plt.subplots(2,1,figsize=(10,7),sharex=True)
for number in range(1,4):
    rows=[r for r in all_rows if r['Run']==number and r['Phase']=='repeat']
    axes[0].plot([r['Iteration']-20 for r in rows],[r['PrivateBytes']/2**20 for r in rows],label=f'Run {number}')
    axes[1].plot([r['Iteration']-20 for r in rows],[r['Handles'] for r in rows],label=f'Run {number}')
axes[0].set_ylabel('Private Bytes (MiB)');axes[1].set_ylabel('Process handles');axes[1].set_xlabel('Query / dispose repetitions after warmup')
axes[0].set_title('Native Shell query/dispose driver — NOT popup open/close')
for axis in axes: axis.grid(alpha=.3);axis.legend()
figure.tight_layout();figure.savefig(out/'query-lifecycle.png',dpi=140);plt.close(figure)
lines=['# Shellメニュー取得・解放の性能測定','', '2026-10-14。実GUIのポップアップ開閉ではなく、専用STAドライバーの測定。性能受入は未達／実GUIは未検証。','', '|回|ウォームアップ後 Private MiB|終了 Private MiB|増分 MiB|ハンドル増分|取得・解放 中央値 ms|最大 ms|CPU累計 秒|CPU平均 全論理CPU %|','|---|---:|---:|---:|---:|---:|---:|---:|---:|']
for r in results: lines.append(f"|{r['Run']}|{r['PrivateWarmMiB']:.2f}|{r['PrivateFinalMiB']:.2f}|{r['PrivateDeltaMiB']:.2f}|{r['HandlesDelta']}|{r['DurationMedianMs']:.2f}|{r['DurationMaxMs']:.2f}|{r['CpuSeconds']:.2f}|{r['CpuMeanAllLogicalPercent']:.3f}|")
lines+=['','各独立プロセスで20回ウォームアップ＋1,600回。ファイル・ディレクトリ・複数選択・背景を交互に取得。QueryContextMenu→必要時Restrict→Dispose→Dispatcher.ApplicationIdleまで処理。TrackPopupMenu、メニューへの入力、コマンド実行は含まない。描画完了時間・タブ応答時間ではない。OLEはドライバー実行中保持し、強制GCなし。前後のアイドル観測は各2秒に過ぎず、300秒のアイドルCPU試験を代替しない。','',f"増分の3回中央値: Private {summary['PrivateDeltaMiB']['Median']:.2f} MiB（範囲 {summary['PrivateDeltaMiB']['Min']:.2f}–{summary['PrivateDeltaMiB']['Max']:.2f}）、ハンドル {summary['HandlesDelta']['Median']:.0f}。増加は観測終了時にも安定していない。終了時の管理ヒープ中央値 {summary['ManagedFinalMiB']['Median']:.2f} MiB、GC Committed {summary['GCCommittedFinalMiB']['Median']:.2f} MiB。自然GC後もPrivate/ハンドルの増加があるが、増分をShell拡張・Windows・アプリ固有のどれに帰属するかは未特定。",'', 'NVIDIA `nvui.dll` とOneDrive `FileSyncShell64.dll` が読み込まれた。DLL読込は所有量の証明ではない。拡張なし環境との比較・ヒープ／ハンドルの所有者追跡は未実施。','', '最初の `artifacts/shell-context-performance` は全コマンドの追加列挙、STAポンプなし、320回の別条件。保存しているが、この1,600回と同列比較しない。','', '測定時バイナリと対象ソースは `artifacts/shell-context-performance-sta/measured-binaries` と `measured-source` に保存。現在の製品には、その後キー判定・エラー捕捉・クリップボード検査・階層更新・競合ガードを追加したため、製品版全体の性能値とは扱わない。ネイティブメニュー実装 `ShellContextMenu.cs` は測定時と同一。','', '![Private Bytesとハンドル推移](docs/verification/shell-context/query-lifecycle.png)','', '再現: Releaseビルド後 `pwsh -NoProfile -File tools/Measure-ShellMenu.ps1 -Output artifacts/shell-context-performance-new`。別出力先を指定し、測定中のビルド／他の性能試験を避ける。`tools/Analyze-ShellMenu.py` は今回の保存済みSTAログを検証・集計する。']
(root/'SHELL_CONTEXT_PERFORMANCE_REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
isolation=root/'artifacts/shell-context-type-isolation'
if (isolation/'environment.json').exists():
    diagnostic=[]
    for kind in ['file','directory','multiple','background']:
        folder=isolation/f'run-{kind}';rows=read(folder/'samples.json');conditions=read(folder/'conditions.json');warm=[r for r in rows if r['Phase']=='warmup'][-1];end=rows[-1]
        assert conditions['Iterations']==420 and conditions['ForcedGC'] is False
        diagnostic.append(dict(Kind=kind,PrivateDeltaMiB=(end['PrivateBytes']-warm['PrivateBytes'])/2**20,HandlesDelta=end['Handles']-warm['Handles'],NvidiaLoaded=any(p.lower().endswith('nvui.dll') for p in conditions['AfterModules']),OneDriveLoaded=any(p.lower().endswith('filesyncshell64.dll') for p in conditions['AfterModules'])))
    (out/'type-isolation.json').write_text(json.dumps(diagnostic,indent=2),encoding='utf-8')
    extra=['','## 種類別の追加切り分け','', '各種類を独立した1プロセス、20回ウォームアップ＋400回で測定。探索的な1回の測定であり、3回反復の混合条件とは区別する。','', '|種類|Private増分 MiB|ハンドル増分|NVIDIA DLL|OneDrive DLL|','|---|---:|---:|---|---|']
    for row in diagnostic:extra.append(f"|{row['Kind']}|{row['PrivateDeltaMiB']:.2f}|{row['HandlesDelta']}|{row['NvidiaLoaded']}|{row['OneDriveLoaded']}|")
    extra+=['','ハンドルの継続増加は背景メニューの取得経路に集中する。NVIDIA DLLの読込も背景でのみ観測した。ただしWindows標準の背景処理と拡張を分離した比較ではないため、NVIDIAが原因とは断定しない。次に確認すべき対象は背景メニューの生成・破棄、拡張の寿命、実TrackPopupMenuのメッセージ循環との差である。常駐アプリへ導入する性能条件は未達のままとする。']
    with (root/'SHELL_CONTEXT_PERFORMANCE_REPORT.md').open('a',encoding='utf-8') as stream:stream.write('\n'.join(extra)+'\n')
print(json.dumps({k:v for k,v in summary.items() if k in ['PrivateDeltaMiB','HandlesDelta','DurationMedianMs','CpuSeconds']},ensure_ascii=False))
