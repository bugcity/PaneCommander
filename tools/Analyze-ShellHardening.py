from pathlib import Path
import csv, json, statistics, sys, os
root=Path(__file__).resolve().parents[1];out=root/'docs/verification/shell-context/hardening';out.mkdir(parents=True,exist_ok=True)
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
rows=[];samples=[]
for era,directory in [('before','shell-context-performance-sta'),('after','shell-hardening-after-mixed')]:
    base=root/'artifacts'/directory;env=read(base/'environment.json');assert env['ProductHashBefore']==env['ProductHashAfter']
    for run in range(1,4):
        data=read(base/f'run-{run}/samples.json');condition=read(base/f'run-{run}/conditions.json');repeat=[r for r in data if r['Phase']=='repeat'];warm=[r for r in data if r['Phase']=='warmup'][-1];end=data[-1]
        assert len(repeat)==1600 and condition['ForcedGC'] is False
        seconds=(repeat[-1]['ElapsedMs']-warm['ElapsedMs'])/1000;cpu=repeat[-1]['CpuSeconds']-warm['CpuSeconds']
        rows.append(dict(Era=era,Run=run,PrivateWarmMiB=warm['PrivateBytes']/2**20,PrivateMeanMiB=statistics.mean(r['PrivateBytes'] for r in repeat)/2**20,PrivateMaxMiB=max(r['PrivateBytes'] for r in repeat)/2**20,PrivateFinalMiB=end['PrivateBytes']/2**20,PrivateDeltaMiB=(end['PrivateBytes']-warm['PrivateBytes'])/2**20,WorkingSetMeanMiB=statistics.mean(r['WorkingSet'] for r in repeat)/2**20,WorkingSetMaxMiB=max(r['WorkingSet'] for r in repeat)/2**20,WorkingSetFinalMiB=end['WorkingSet']/2**20,HandlesWarm=warm['Handles'],HandlesMax=max(r['Handles'] for r in repeat),HandlesFinal=end['Handles'],HandlesDelta=end['Handles']-warm['Handles'],CpuSeconds=cpu,CpuMeanAllLogicalPercent=cpu/seconds/condition['Processors']*100,ElapsedSeconds=seconds,DurationMedianMs=statistics.median(r['DurationMs'] for r in repeat),DurationMaxMs=max(r['DurationMs'] for r in repeat),ManagedFinalMiB=end['ManagedBytes']/2**20,GCCommittedFinalMiB=end['GCCommitted']/2**20))
        samples.extend(dict(Era=era,Run=run,**r) for r in data)
def export(name,data):
    with(out/name).open('w',encoding='utf-8-sig',newline='') as file:
        writer=csv.DictWriter(file,fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
export('comparison-runs.csv',rows);export('comparison-samples.csv',samples)
summary={}
for era in ['before','after']:
    subset=[r for r in rows if r['Era']==era];summary[era]={key:dict(Median=statistics.median(r[key] for r in subset),Min=min(r[key] for r in subset),Max=max(r[key] for r in subset),Spread=max(r[key] for r in subset)-min(r[key] for r in subset)) for key in rows[0] if key not in ['Era','Run']}
types=[]
for era,directory in [('before','shell-context-type-isolation'),('after','shell-hardening-after-types')]:
    for kind in ['file','directory','multiple','background']:
        data=read(root/'artifacts'/directory/f'run-{kind}/samples.json');warm=[r for r in data if r['Phase']=='warmup'][-1];end=data[-1];assert len([r for r in data if r['Phase']=='repeat'])==400
        types.append(dict(Era=era,Kind=kind,PrivateDeltaMiB=(end['PrivateBytes']-warm['PrivateBytes'])/2**20,HandlesDelta=end['Handles']-warm['Handles']))
export('type-comparison.csv',types)
baseline=read(root/'artifacts/shell-hardening-baseline/run-baseline/samples.json');warm=[r for r in baseline if r['Phase']=='warmup'][-1];end=baseline[-1]
summary['baseline']=dict(PrivateDeltaMiB=(end['PrivateBytes']-warm['PrivateBytes'])/2**20,HandlesDelta=end['Handles']-warm['Handles'],Seconds=end['ElapsedMs']/1000,Repetitions=1,MenuCalls=0)
idle=read(root/'artifacts/shell-hardening-idle/run-post-menu-idle/samples.json');condition=read(root/'artifacts/shell-hardening-idle/run-post-menu-idle/conditions.json');seconds=(idle[-1]['ElapsedMs']-idle[-2]['ElapsedMs'])/1000;cpu=idle[-1]['CpuSeconds']-idle[-2]['CpuSeconds']
summary['idle']=dict(Seconds=seconds,CpuSeconds=cpu,CpuMeanAllLogicalPercent=100*cpu/seconds/condition['Processors'],PrivateDeltaMiB=(idle[-1]['PrivateBytes']-idle[-2]['PrivateBytes'])/2**20,HandlesDelta=idle[-1]['Handles']-idle[-2]['Handles'],Repetitions=1,PopupMeasured=False)
causes=[]
for mode in ['baseline','acquire-only','default-verb','minimal','product','product-pin','product-probe']:
    data=read(root/f'artifacts/shell-lifetime-pin-control/run-{mode}/diagnosis.json');causes.append(dict(Mode=mode,Iterations=100,HandlesDelta=len(data['After'])-len(data['Before']),HomePageBefore=sum('HomePageEvent' in h['Name'] for h in data['Before']),HomePageAfter=sum('HomePageEvent' in h['Name'] for h in data['After'])))
data=read(root/'artifacts/shell-native-module-cycle/run-module-cycle/diagnosis.json');causes.append(dict(Mode='module-cycle',Iterations=100,HandlesDelta=len(data['After'])-len(data['Before']),HomePageBefore=sum('HomePageEvent' in h['Name'] for h in data['Before']),HomePageAfter=sum('HomePageEvent' in h['Name'] for h in data['After'])))
export('cause-controls.csv',causes)
summary.update(HandleAcceptance='Observed repetitive growth resolved for nvui 8.17.15.5527',MemoryAcceptance='Unmet; allocation owner unresolved',GuiAcceptance='Pending locked session',ActualPopupMeasured=False,ForcedGC=False)
(out/'performance-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
os.environ['MPLCONFIGDIR']=str(root/'artifacts/shell-hardening-plot-cache');sys.path.insert(0,str(root/'artifacts/render-analysis-deps'));import matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as plt
figure,axes=plt.subplots(2,1,figsize=(10,7),sharex=True)
for era,color in [('before','tab:orange'),('after','tab:blue')]:
    points=[r for r in samples if r['Era']==era and r['Run']==1 and r['Phase']=='repeat'];x=[r['Iteration']-20 for r in points]
    axes[0].plot(x,[r['PrivateBytes']/2**20 for r in points],color=color,label=era+' run 1');axes[1].plot(x,[r['Handles'] for r in points],color=color,label=era+' run 1')
axes[0].set_title('Shell query/dispose driver comparison — NOT actual popup');axes[0].set_ylabel('Private Bytes (MiB)');axes[1].set_ylabel('Process handles');axes[1].set_xlabel('Repetitions after warmup')
for axis in axes:axis.legend();axis.grid(alpha=.3)
figure.tight_layout();figure.savefig(out/'before-after.png',dpi=140);plt.close(figure)
lines=['# Shellコンテキストメニュー 性能再測定','', '2026-10-14。ハンドル増加は対象版NVIDIA DLLの寿命対策で停止。Private Bytesの継続増加は未解決のため、性能全体は未達。実GUIのポップアップ開閉は未測定。','', '|条件|回|Private増分 MiB|Private平均/最大/終了 MiB|WS平均/最大/終了 MiB|ハンドル増分|CPU累計 秒|全論理CPU平均 %|','|---|---|---:|---|---|---:|---:|---:|']
for r in rows:lines.append(f"|{r['Era']}|{r['Run']}|{r['PrivateDeltaMiB']:.2f}|{r['PrivateMeanMiB']:.2f}/{r['PrivateMaxMiB']:.2f}/{r['PrivateFinalMiB']:.2f}|{r['WorkingSetMeanMiB']:.2f}/{r['WorkingSetMaxMiB']:.2f}/{r['WorkingSetFinalMiB']:.2f}|{r['HandlesDelta']}|{r['CpuSeconds']:.2f}|{r['CpuMeanAllLogicalPercent']:.3f}|")
lines+=['',f"3回中央値：Private増分 {summary['before']['PrivateDeltaMiB']['Median']:.2f} → {summary['after']['PrivateDeltaMiB']['Median']:.2f} MiB。ハンドル増分 {summary['before']['HandlesDelta']['Median']:.0f} → {summary['after']['HandlesDelta']['Median']:.0f}。減少分をメモリ問題の解消とは扱わない。中央値・最小・最大・ばらつきは performance-summary.json、全試行と時系列は比較CSVに保存。",'', '|種類（各1プロセス、400回）|Private増分 before→after MiB|ハンドル増分 before→after|','|---|---:|---:|']
for kind in ['file','directory','multiple','background']:
    before=next(r for r in types if r['Era']=='before' and r['Kind']==kind);after=next(r for r in types if r['Era']=='after' and r['Kind']==kind);lines.append(f"|{kind}|{before['PrivateDeltaMiB']:.2f} → {after['PrivateDeltaMiB']:.2f}|{before['HandlesDelta']} → {after['HandlesDelta']}|")
b=summary['baseline'];i=summary['idle']
lines+=['',f"メニューなし対照：同じ自己サンプル・STAポンプを1,600回、各回50ms待機。1プロセス、{b['Seconds']:.2f}秒、Private＋{b['PrivateDeltaMiB']:.2f} MiB、ハンドル {b['HandlesDelta']:+d}。呼出しありの増加はこれだけでは説明できないが、差をそのまま特定モジュールの確保量と断定しない。",'',f"背景400回後の静かなアイドル観測1回：{i['Seconds']:.2f}秒、CPU {i['CpuSeconds']:.4f}秒、全論理CPU平均 {i['CpuMeanAllLogicalPercent']:.5f}%、Private＋{i['PrivateDeltaMiB']:.3f} MiB、ハンドル {i['HandlesDelta']:+d}。この待機は専用ドライバーの一度だけの終了タイマーで、製品にタイマーを追加していない。長期安定性やWPF製品全体のアイドル性能は証明しない。",'', '条件：Windows 11 Pro build 26300、.NET 10、同じPC・Release構成・ローカル一時フォルダー・同じ2ファイルと1ディレクトリ。混合条件は20回ウォームアップ＋1,600回、3独立プロセス。file/directory/multiple/backgroundの順を反復。各条件でQueryContextMenuと解放を継続し、呼出し回数を減らしていない。強制GCなし。取得・解放後にDispatcher.ApplicationIdleまでポンプする。記録時点は各操作後のプロセス情報取得であり、画面描画完了ではない。HWND=0での取得専用試験で、TrackPopupMenuEx／実マウス／実キー／GPU使用量は未測定。電源条件の詳細は旧ログにも取得がなく、完全な環境同一性の保証はしない。','', '修正前：artifacts/shell-context-performance-sta と shell-context-type-isolation。修正後：shell-hardening-after-mixed と shell-hardening-after-types。対照：shell-hardening-baseline。アイドル：shell-hardening-idle。Query経路のShellContextMenu.csとShellExtensionLifetime.csは修正後コホート中に変更していない。後から加えたテスト用ごみ箱受領通知・設定説明はこの測定で実行していない。実製品GUIの性能値として転用しない。','', '![修正前後の代表1回](docs/verification/shell-context/hardening/before-after.png)','', '再現：Releaseビルド後に `pwsh -NoProfile -File tools/Measure-ShellMenu.ps1 -Output artifacts/shell-hardening-new`。種類別は `-IsolateTypes`、メニューなしは `-Scenarios baseline`、60秒アイドルは `-Scenarios post-menu-idle`。各回で未使用の出力先を指定し、測定中にビルド・他の性能試験を並行しない。今回の集計は `tools/Analyze-ShellHardening.py`。','', '判定：ハンドル寿命は対象版・観測範囲で改善。Private Bytesは未達。実ポップアップと両描画方式の実GUI性能比較は未測定。全体受入は保留。']
(root/'SHELL_CONTEXT_PERFORMANCE_REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(json.dumps({'PrivateMedianBefore':summary['before']['PrivateDeltaMiB']['Median'],'PrivateMedianAfter':summary['after']['PrivateDeltaMiB']['Median'],'HandleMedianBefore':summary['before']['HandlesDelta']['Median'],'HandleMedianAfter':summary['after']['HandlesDelta']['Median'],'Idle':summary['idle']},ensure_ascii=False))
