from pathlib import Path
import csv,hashlib,json,shutil,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/link-objects'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
tests=[]
for mode in ['SoftwareOnly','Default']:
    folder=out/mode;tree=ET.parse(folder/'links-regression.trx'); ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counts=tree.find('.//t:Counters',ns).attrib
    assert counts['failed']=='0' and counts['passed']=='58' and counts['total']=='60', counts
    results=tree.findall('.//t:UnitTestResult',ns)
    assert next(r for r in results if r.attrib['testName']=='ActualCrossVolumeJunctionMoveKeepsReferent').attrib['outcome']=='Passed'
    skipped=[r.attrib['testName'] for r in results if r.attrib['outcome']!='Passed']
    tests.append({'Mode':mode,'Passed':58,'Skipped':len(skipped),'Total':60,'SkippedTests':skipped,'ActualCrossVolumePassed':True})
    for name in ['junction','function-commands','destination-failure','cross-volume-algorithm','actual-cross-volume','symlink-creation-restriction','symlink-unavailable']:
        candidates=list(folder.rglob('link-'+name+'.json'))
        assert candidates,name
        latest=max(candidates,key=lambda p:p.stat().st_mtime_ns)
        target=out/'evidence'/mode/latest.name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(latest,target)
    evidence=read(out/'evidence'/mode/'link-actual-cross-volume.json')
    assert evidence['ReferentHashBefore']==evidence['ReferentHashAfter'] and evidence['SourceVolume']!=evidence['DestinationVolume']
    assert read(out/'evidence'/mode/'link-symlink-creation-restriction.json')['NativeErrorCode']==1314
smoke=root/'artifacts/link-final/smoke'
assert read(smoke/'integrity.json')['FrozenBinariesUnchanged']
for e in read(smoke/'binary-manifest.json'):assert sha(Path(e['Path']))==e['SHA256'],e['Path']
rows=[]
for p in sorted(smoke.glob('*/summary.json')):
    value=read(p);scenario=p.parent.name.rsplit('-',2)[0];ready=read(p.parent/'ready.json')
    assert value['savedTabStateMatchesReady'] and value['profileFilesUnchangedDuringObservation']
    rows.append({'Scenario':scenario,'Mode':value['mode'],'PrivateMeanMB':value['privateMeanMB'],'PrivateMaxMB':value['privateMaxMB'],'WorkingSetMeanMB':value['workingSetMeanMB'],'CpuMeanAllLogical':value['cpuMeanAllLogical'],'Seconds':value['observedSeconds'],'Repetitions':1,'FolderViews':ready['folderViews'],'Rows':ready['realizedRows']})
with (out/'product-smoke.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
restart=read(root/'artifacts/link-final/restart/verification.json')+read(root/'artifacts/link-final/restart/m3-regression/verification.json')
assert len(restart)==8 and all(x['Passed'] for x in restart)
summary={'Tests':tests,'RestartCasesPassed':8,'CurrentProductHashesMatchSmoke':True,'ActualCrossVolumeJunctionAndTreeMove':True,'NativeGuiVerified':False,'RealSymlinkOperationsVerified':False,'SymlinkError':1314,'Smoke':rows,'FullPerformanceAfterLinkPolicy':False}
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
lines=['','## 最終版の短時間回帰測定','', '設定・復元の実製品再起動8ケースが合格。製品バイナリの測定後ハッシュ一致を確認。以下は各方式・条件1回、ウォームアップ10秒＋観測10秒の回帰プローブであり、3回反復・300秒アイドルの本測定を置き換えない。ファイル操作中の負荷測定ではない。CPUは24論理CPU全体を100%として表示。MBは1,000,000 bytes。','', '| 条件 | 方式 | Private平均 MB | Private最大 MB | WS平均 MB | CPU平均 % |','|---|---|---:|---:|---:|---:|']
for r in rows:lines.append(f"| {r['Scenario']} | {r['Mode']} | {r['PrivateMeanMB']:.2f} | {r['PrivateMaxMB']:.2f} | {r['WorkingSetMeanMB']:.2f} | {r['CpuMeanAllLogical']:.4f} |")
lines+=['', '通常利用200MB目標に対し、今回の6訪問済みタブのPrivate BytesはDefaultで未達、SoftwareOnlyで目標内。ただしWSでは別の値になり、両指標を混同しない。各1回の短時間値だけで性能の優劣・長時間安定・表示品質の受入を確定しない。描画既定値の変更は行っていない。','', '[集計JSON](docs/verification/link-objects/verification-summary.json)、[短時間測定CSV](docs/verification/link-objects/product-smoke.csv)、[リンク先保持の証跡](docs/verification/link-objects/evidence)。','']
report=root/'LINK_OBJECT_OPERATIONS_REPORT.md';text=report.read_text(encoding='utf-8');text=text.split('\n## 最終版の短時間回帰測定')[0];report.write_text(text+'\n'.join(lines),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False))
