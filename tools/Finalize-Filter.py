from pathlib import Path
import csv,hashlib,json,statistics,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1];out=root/'docs/verification/filter';run=root/'artifacts/filter-comparison-complete'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
tests=[]
for mode in ['SoftwareOnly','Default']:
    tree=ET.parse(out/'complete'/mode/'links-regression.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};counts=tree.find('.//t:Counters',ns).attrib
    assert counts['total']=='62' and counts['passed']=='60' and counts['failed']=='0'
    skipped=[r.attrib['testName'] for r in tree.findall('.//t:UnitTestResult',ns) if r.attrib['outcome']!='Passed'];assert len(skipped)==2
    tests.append({'Mode':mode,'Total':62,'Passed':60,'Failed':0,'SkippedTests':skipped})
for e in json.loads((run/'source-manifest.json').read_text(encoding='utf-8-sig')):
    source=Path(e['Path']);assert sha(source)==e['SHA256'];target=run/'source'/source.relative_to(root);target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(source.read_bytes())
for name in ['Directory.Build.props','global.json','PaneCommander.sln']:(run/'source'/name).write_bytes((root/name).read_bytes())
with (out/'performance/comparison.csv').open(encoding='utf-8-sig',newline='') as f:comparison=list(csv.DictReader(f))
with (out/'performance/process-runs.csv').open(encoding='utf-8-sig',newline='') as f:runs=list(csv.DictReader(f))
report=root/'FILTER_PERFORMANCE_REPORT.md';text=report.read_text(encoding='utf-8').split('\n## 応答と管理メモリ')[0]
lines=['','## 応答と管理メモリ','', '| 条件 | 方式 | レイアウト到達中央値：前→後 ms |','|---|---|---:|']
for r in comparison:
    if r['Phase'] in ['typing','start-clear']:lines.append(f"| {r['Phase']} | {'Default' if r['Mode']=='A' else 'SoftwareOnly'} | {float(r['LayoutMedianMsBefore']):.2f} → {float(r['LayoutMedianMsAfter']):.2f} |")
lines+=['','各回100要求の中央値を、3回で再度中央値にした値。実画面の描画完了・OS入力応答ではない。','', '| 方式 | GC.GetTotalMemory(false)終了値：前→後 MB（3回中央値） |','|---|---:|']
for mode in ['A','B']:
    before=statistics.median(float(r['ManagedFinalMB']) for r in runs if r['Mode']==mode and r['Era']=='before');after=statistics.median(float(r['ManagedFinalMB']) for r in runs if r['Mode']==mode and r['Era']=='after')
    lines.append(f"| {'Default' if mode=='A' else 'SoftwareOnly'} | {before:.2f} → {after:.2f} |")
lines+=['', '管理メモリの現在値は、解放可能だが未回収のオブジェクトも含む。Private Bytesとの差からネイティブ領域の所有者やリークを断定しない。強制GCでそろえず、観測された値を報告する。','', '![Private BytesとCPUの時系列](docs/verification/filter/performance/timeseries.png)','', '図は各方式の1回目。操作時間が前後で異なるため、同じ横軸時刻が同じ操作位置とは限らない。フェーズ別の比較にはCSVと表を使う。','', '環境はenvironment.jsonとprotocol.jsonに記録。i7-13700HX、24論理CPU、RAM約34.1GB、Intel UHD／RTX4060 Laptop、2560×1440・165Hz、バランス電源。全試行でDPI150%。OS/.NET詳細、GPUドライバー版、ソース・バイナリのSHA256は生ログに含む。','', '本報告は非表示行のRun解放と不正配色JSON処理を反映した最終版だけを集計している。それ以前の比較値・途中のコメントは最終結果として扱わない。','']
report.write_text(text+'\n'.join(lines),encoding='utf-8')
summary={'Tests':tests,'ReleaseWarnings':0,'ReleaseErrors':0,'MeasuredSourcesMatchCurrent':True,'BenchmarkTrials':12,'ForcedGc':False,'NativeGuiVerified':False,'PerformanceAcceptance':'Unmet: numeric regressions recorded','OverallAcceptance':'Pending'}
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(summary,ensure_ascii=False))
