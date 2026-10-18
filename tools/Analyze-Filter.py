from pathlib import Path
import csv,hashlib,json,statistics,sys,os
root=Path(__file__).resolve().parents[1];run=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/'artifacts/filter-comparison-complete';out=root/'docs/verification/filter/performance';out.mkdir(parents=True,exist_ok=True)
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
assert read(run/'integrity.json')['Completed']
for e in read(run/'binary-manifest.json'):assert sha(Path(e['Path']))==e['SHA256'],e['Path']
if (run/'source-manifest.json').exists():
    assert read(run/'integrity.json')['SourcesUnchanged']
    for e in read(run/'source-manifest.json'):assert sha(Path(e['Path']))==e['SHA256'],e['Path']
rows=[];summaries=[];rawsamples=[]
for path in sorted(run.glob('*/monitor-summary.json')):
    era,mode,repeat=path.parent.name.split('-');summary=read(path);complete=read(path.parent/'completed.json');ready=read(path.parent/'ready.json');assert ready['filterImplemented']==(era=='after');assert not complete['forcedGc'];assert ready['dpi']==1.5
    assert all(p['folderViews']==2 and p['rows']<150 for p in complete['checkpoints'])
    points={p['phase']:p for p in complete['checkpoints']};assert points['unused']['enumerations']==points['typing']['enumerations']==points['start-clear']['enumerations'];assert points['six-tabs']['tabs']==6
    assert all(t['visible']==1 for t in complete['timings'] if t['phase']=='few')
    assert all(t['visible']==10000 for t in complete['timings'] if t['phase'] in ['unused','many','six-tabs','recovery'])
    base=points['unused']['privateBytes'];end=points['recovery']['privateBytes']
    summaries.append({'Era':era,'Mode':mode,'Repeat':int(repeat),'PrivateMaxMB':summary['privateMaxMB'],'PrivateFinalMB':summary['privateFinalMB'],'ResidualMB':(end-base)/1e6,'CpuSeconds':summary['cpuSeconds'],'Seconds':summary['durationSeconds'],'CpuMeanPercent':summary['cpuMeanAllLogical'],'RowsMax':max(p['rows'] for p in points.values()),'ManagedFinalMB':points['recovery']['managed']/1e6})
    with (path.parent/'samples.csv').open(encoding='utf-8-sig',newline='') as f: phase_samples=list(csv.DictReader(f))
    for phase in summary['phases']:
        name=phase['phase'];timings=[t['layoutMs'] for t in complete['timings'] if t['phase']==name];point=points.get(name)
        working=[float(s['WorkingSet'])/1e6 for s in phase_samples if s['Phase']==name]
        rows.append({'Era':era,'Mode':mode,'Repeat':int(repeat),'Phase':name,'PrivateMeanMB':phase['privateMeanMB'],'PrivateMaxMB':phase['privateMaxMB'],'PrivateFinalMB':phase['privateFinalMB'],'WorkingSetMeanMB':statistics.mean(working),'WorkingSetMaxMB':max(working),'CpuPercent':phase['cpuMeanAllLogical'],'CpuSeconds':phase['cpuSeconds'],'Seconds':phase['durationSeconds'],'LayoutMedianMs':statistics.median(timings) if timings else None,'LayoutMaxMs':max(timings) if timings else None,'Rows':point['rows'] if point else None,'CheckpointWorkingSetMB':point['workingSet']/1e6 if point else None,'Tabs':point['tabs'] if point else None})
    with (path.parent/'samples.csv').open(encoding='utf-8-sig',newline='') as f:
        for sample in csv.DictReader(f):rawsamples.append({'Era':era,'Mode':mode,'Repeat':repeat,**sample})
assert len(summaries)==12
def export(name,data):
    with (out/name).open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,fieldnames=list(dict.fromkeys(k for r in data for k in r)));w.writeheader();w.writerows(data)
export('phase-runs.csv',rows);export('process-runs.csv',summaries);export('samples.csv',rawsamples)
def group(era,mode,phase):return [r for r in rows if r['Era']==era and r['Mode']==mode and r['Phase']==phase]
def median(data,key):return statistics.median(r[key] for r in data if r[key] is not None)
comparisons=[]
for mode in ['A','B']:
    for phase in ['unused','few','many','typing','start-clear','six-tabs','recovery']:
        before=group('before',mode,phase);after=group('after',mode,phase);assert len(before)==len(after)==3
        comparison={'Mode':mode,'Phase':phase}
        for key in ['PrivateMeanMB','PrivateMaxMB','WorkingSetMeanMB','WorkingSetMaxMB','CheckpointWorkingSetMB','CpuPercent','CpuSeconds','Seconds','LayoutMedianMs','Rows']:
            a=median(before,key);b=median(after,key);comparison[key+'Before']=a;comparison[key+'After']=b;comparison[key+'Delta']=b-a
            for era,data in [('Before',before),('After',after)]:
                values=[r[key] for r in data if r[key] is not None];comparison[key+era+'Max']=max(values);comparison[key+era+'Spread']=max(values)-min(values)
        comparison['NumericWorsening']=any(comparison[k+'Delta']>0 for k in ['PrivateMeanMB','WorkingSetMeanMB','CpuPercent','CpuSeconds'])
        comparisons.append(comparison)
export('comparison.csv',comparisons)
lines=['# インクリメンタルフィルターの性能比較','', '変更前後×Default(A)／SoftwareOnly(B)×各3回、計12の独立プロセス。1プロセス内で未使用・少数一致・多数一致・連続入力削除100要求・開始解除100要求・6タブ・解除後を同じ順序で実施した。条件間のキャッシュ履歴は共有されるため、各条件を単独起動した値ではない。ウォームアップ5秒、各条件の静止観測3秒、250ms採取、強制GCなし。10,000件の合成一覧を各フォルダに与える専用ドライバーであり、実ディレクトリ列挙・OS実入力を含む製品利用性能ではない。','', '変更前にはフィルターが存在しないため、同一ドライバーが旧画面のCollectionViewにOrdinalIgnoreCaseの部分一致条件を注入した。変更後は製品のFilterTextへ同じ文字列を設定する。したがって旧製品に存在したフィルターの回帰比較ではなく、同等の絞り込みに強調表示と入力基盤を追加した比較である。旧側は同値の条件でもFilter設定によるRefresh（未使用と開始解除の初回）を含むため、差の原因を強調描画だけに限定できない。','', '少数一致は `item-09999`（1件）、多数一致は `item`（10,000件）。連続入力は i→it→ite→item→ite→it→i→空、開始解除はitem→空を計100要求。開始解除の初回は直前条件と同じなので、製品側の実変更は99回（旧注入側は100回Refresh）である。6タブは左右各3つを読み込んで訪問。描画方式とビルド以外は同じドライバー、入力、1280×800 DIP、DPI150%、.NET・PCを使用した。','', 'CPUは24論理CPU全体を100%とする。各欄は3回の中央値。Privateはフェーズ観測平均、WSもフェーズ観測平均。終了チェックポイントは別欄で保持。CPU累計は当該フェーズで採取できた最初と最後のサンプル間。入力完了はDispatcher.ApplicationIdle到達であり、画面への提示完了ではない。','', '| 条件 | 方式 | Private前→後 MB | WS前→後 MB | CPU前→後 % | CPU累計前→後 秒 | 数値悪化 |','|---|---|---:|---:|---:|---:|---|']
for c in comparisons:
    def pair(k,d=2):return f"{c[k+'Before']:.{d}f} → {c[k+'After']:.{d}f}"
    lines.append(f"| {c['Phase']} | {'Default' if c['Mode']=='A' else 'SoftwareOnly'} | {pair('PrivateMeanMB')} | {pair('WorkingSetMeanMB')} | {pair('CpuPercent',4)} | {pair('CpuSeconds',3)} | {'未達' if c['NumericWorsening'] else 'この測定ではなし'} |")
lines+=['', '正の差がある項目は数値悪化として未達と記録した。測定ばらつき・有限サンプル・必須描画の追加コストを区別できる証拠は不足しており、増加分をすべて漏れ／不要な処理と断定しない。最大値・3回の最大−最小・各回の値・入力応答時間はCSVに記録。ハードウェア側のPrivate差をGPUドライバー確保量とは断定しない。','', '| 方式 | 解除後−未使用のPrivate残留 MB：前（中央値／最大） | 後（中央値／最大） | 後の管理メモリ終了値 MB（中央値） |','|---|---:|---:|---:|']
for mode in ['A','B']:
    a=[r for r in summaries if r['Era']=='before' and r['Mode']==mode];b=[r for r in summaries if r['Era']=='after' and r['Mode']==mode]
    lines.append(f"| {'Default' if mode=='A' else 'SoftwareOnly'} | {median(a,'ResidualMB'):.2f} / {max(r['ResidualMB'] for r in a):.2f} | {median(b,'ResidualMB'):.2f} / {max(r['ResidualMB'] for r in b):.2f} | {median(b,'ManagedFinalMB'):.2f} |")
lines+=['', '全チェックポイントでFolderViewは2個、ListViewItemは150個未満。連続入力・解除でファイルサービスのRead回数が増えないことをドライバー内部で検査した。GC.GetTotalMemory(false)を終了時に採取したが、強制GCはしていない。描画リソースの全回収や長時間の安定性は、この反復回数だけでは証明していない。','', '[条件・最大・ばらつきCSV](docs/verification/filter/performance/comparison.csv)、[各回・各フェーズCSV](docs/verification/filter/performance/phase-runs.csv)、[全時系列CSV](docs/verification/filter/performance/samples.csv)。生ログ・固定ビルド・SHA256・環境情報・各プロセスのready/completedは `artifacts/filter-comparison-complete`。初回の不完全な測定は `artifacts/filter-comparison/EXCLUDED.json` により除外。','', '再現：Releaseビルド後 `pwsh -NoProfile -File tools/Measure-Filter.ps1 -Output <新しい出力先>`。旧製品には `artifacts/filter-before/app` を使用する。分析は `tools/Analyze-Filter.py`。','', '**性能受入：未達項目あり。実GUI確認と長時間安定性は未検証。** 機能の自動試験合格と分けて判定する。','']
(root/'FILTER_PERFORMANCE_REPORT.md').write_text('\n'.join(lines),encoding='utf-8')
(out/'verification.json').write_text(json.dumps({'Trials':12,'FrozenBinariesUnchanged':True,'ForcedGc':False,'AllRowsUnder150':True,'FolderViews':2,'NoEnumerationDuringFilter':True,'NumericWorseningConditions':sum(c['NumericWorsening'] for c in comparisons),'NativeGuiVerified':False},indent=2),encoding='utf-8')
os.environ['MPLCONFIGDIR']=str(root/'artifacts/filter-plot-cache');sys.path.insert(0,str(root/'artifacts/render-analysis-deps'));import matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as plt
fig,axes=plt.subplots(2,2,figsize=(12,7))
for i,mode in enumerate(['A','B']):
    for era in ['before','after']:
        selected=[r for r in rawsamples if r['Era']==era and r['Mode']==mode and r['Repeat']=='1']
        axes[0,i].plot([float(r['Seconds']) for r in selected],[float(r['PrivateBytes'])/1e6 for r in selected],label=era)
        axes[1,i].plot([float(r['Seconds']) for r in selected],[float(r['CpuPercentAllLogical']) for r in selected],label=era)
    axes[0,i].set_title('Default' if mode=='A' else 'SoftwareOnly');axes[0,i].set_ylabel('Private Bytes MB');axes[1,i].set_ylabel('CPU % (24 logical)');axes[1,i].set_xlabel('seconds');axes[0,i].legend();axes[1,i].legend()
fig.tight_layout();fig.savefig(out/'timeseries.png',dpi=140);plt.close(fig)
print(json.dumps({'Trials':12,'WorsenedConditions':sum(c['NumericWorsening'] for c in comparisons),'RowsMax':max(r['RowsMax'] for r in summaries)}))
