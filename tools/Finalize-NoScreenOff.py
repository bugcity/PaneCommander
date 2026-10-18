from pathlib import Path
import json,hashlib,runpy,sys,csv
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/shell-context/no-screen-off'
if (root/'docs/verification/gui-investigation-20261015/verification-summary.json').exists():
    runpy.run_path(str(root/'tools/Finalize-GuiInvestigation.py'),run_name='__main__')
    sys.exit(0)
runpy.run_path(str(root/'tools/Analyze-NoScreenOff.py'),run_name='__main__')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
performance=read(out/'performance-summary.json')
status=read(out/'status.json')
for mode,fixture in status['Fixtures'].items():
    ready=read(root/fixture/'diagnostics/ready.json')
    assert ready['modeBeforeWindow']==mode and ready['effectiveRenderMode']==mode
product=root/'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
assert hashlib.sha256(product.read_bytes()).hexdigest().upper()=='D65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27'
rows=[]
for mode in ['Default','SoftwareOnly','SoftwareOnly-functional']:
    for stage,data in performance.get(mode,{}).items():
        if stage=='unmarked':continue
        p=data['Metrics']['PrivateBytes'];w=data['Metrics']['WorkingSet'];h=data['Metrics']['Handles']
        rows.append(f"|{mode}|{stage}|{data['Seconds']:.2f}|{p['Mean']/2**20:.2f}/{p['Max']/2**20:.2f}/{p['End']/2**20:.2f}|{w['Mean']/2**20:.2f}/{w['Max']/2**20:.2f}|{h['Delta']:+.0f}|{data['CpuMeanAllLogicalPercent']:.5f}|")
report='''# 画面消灯「なし」変更後の実GUI再試験

2026-10-14。ユーザーがAC画面消灯を「なし」に変更した後の確認。製品コード・OS設定はエージェント側では変更していない。既存の独立プロファイル／専用テストデータで実製品EXEを再起動して測定した。強制GCなし、消灯抑止・スリープ抑止要求なし、自動入力で無入力待機を偽装していない。

## 結果

'''+status['ResultText']+'''

## 実測

|描画方式|測定区間|秒|Private平均/最大/終了 MiB|WS平均/最大 MiB|ハンドル増分|CPU全論理平均 %|
|---|---|---:|---|---|---:|---:|
'''+ '\n'.join(rows)+'''

各idle区間はUI入力・UIAツリー取得・画面キャプチャを止め、外部PowerShellでプロセス情報だけを500ms間隔で取得。既存のキャプチャ接続の寿命による影響は未分離。GUIメニュー区間は、実画面に基づく1操作ごとの観測・入力を含むため、UIA／キャプチャの影響を分離できない。ネイティブSTA取得ドライバーの値ではない。各条件1プロセスの探索的測定で、3回反復や長期安定性の証拠にはしない。idle-first-360だけが共通長の窓で、idle-full-observationは方式間で長さが異なる。表の区間は重複するため合算しない。

DefaultはWPF標準の設定でハードウェア描画を許可するが、全描画がGPUで行われたことは保証しない。実GPU割当／ドライバーの確保量はこの測定では未測定。

## 残課題

- 前回の背景Paste不在とクリップボード復元失敗は未解決。GUIクリップボード操作を再開していない。前回の自動回帰SoftwareOnly 74件中71合格・1失敗・2スキップ、Default 71合格・0失敗・3スキップを今回成功へ書き換えない。
- 今回製品変更はなく、新たな全回帰実行はしていない。稼働DLLハッシュと前回検証ビルドの一致を確認する。
- 100/500/1600回の反復開閉、外部Explorer相互操作、実シンボリックリンク、動的拡張実行など、証跡のない項目は未検証を維持。
- 画面消灯を「なし」にした条件で再ロックしなかったことは、この条件・観測時間での結果。前回のロック主体を一意に証明するものではない。

証跡：docs/verification/shell-context/no-screen-off の操作別JSON/JPG、actions.json、各resource/samples.csv、idle-start/end.json、performance-summary.json、status.json。起動プロファイル・ready.jsonの実適用方式はstatus.jsonのFixturesに記録。前回調査はSESSION_LOCK_DIAGNOSIS_REPORT.md。
'''
(root/'SCREEN_OFF_DISABLED_GUI_REPORT.md').write_text(report,encoding='utf-8')
summary=dict(Status=status,Performance=performance,ForcedGC=False,OsSettingsChangedByAgent=False,ProductCodeChanged=False,OverallAcceptance='Pending; only evidenced checks passed')
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'docs/verification/shell-context/verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
notice='最新：ユーザーによるAC画面消灯「なし」への変更後の検証は [SCREEN_OFF_DISABLED_GUI_REPORT.md](SCREEN_OFF_DISABLED_GUI_REPORT.md)。既存の未解決・未検証項目を合格へ変更しない。以下は過去時点の報告として保持。\n\n'
for name in ['SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_GUI_RESUME_REPORT.md','IMPLEMENTATION_STATUS.md']:
    path=root/name
    text=path.read_text(encoding='utf-8-sig')
    if not text.startswith(notice):path.write_text(notice+text,encoding='utf-8')
import sys
sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,axes=plt.subplots(3,1,figsize=(11,8),sharex=True)
for mode in ['Default','SoftwareOnly']:
    folder=out/(mode+'-resource')
    if not(folder/'samples.csv').exists():continue
    with(folder/'samples.csv').open(encoding='utf-8-sig',newline='')as stream:samples=[r for r in csv.DictReader(stream)if r['Stage']=='idle-360']
    if len(samples)<2:continue
    base=float(samples[0]['ElapsedMs'])
    samples=[r for r in samples if float(r['ElapsedMs'])<=base+360000]
    times=[(float(r['ElapsedMs'])-base)/1000 for r in samples]
    axes[0].plot(times,[float(r['PrivateBytes'])/2**20 for r in samples],label=mode)
    axes[1].plot(times,[int(r['Handles'])for r in samples],label=mode)
    cpu=[100*(float(b['CpuSeconds'])-float(a['CpuSeconds']))/((float(b['ElapsedMs'])-float(a['ElapsedMs']))/1000)/int(b['Processors'])for a,b in zip(samples,samples[1:])]
    axes[2].plot(times[1:],cpu,label=mode)
axes[0].set_ylabel('Private Bytes MiB');axes[1].set_ylabel('Handles');axes[2].set_ylabel('CPU % (24 logical)');axes[2].set_xlabel('Seconds in no-input interval')
for axis in axes:axis.grid(alpha=.25);axis.legend()
fig.suptitle('Screen-off disabled: actual product no-input observations, one process per mode')
fig.tight_layout();fig.savefig(out/'idle-comparison.png',dpi=130);plt.close(fig)
with(root/'SCREEN_OFF_DISABLED_GUI_REPORT.md').open('a',encoding='utf-8')as stream:
    stream.write('\n![待機中の比較](docs/verification/shell-context/no-screen-off/idle-comparison.png)\n')
files=[]
for directory in ['src','tests','tools']:
    files.extend(p for p in(root/directory).rglob('*')if p.is_file()and p.suffix in ['.cs','.csproj','.xaml','.ps1','.py']and not any(s in ['bin','obj','TestResults']for s in p.relative_to(root).parts))
files.extend(p for p in(root/'src/PaneCommander.App/bin/Release/net10.0-windows').iterdir()if p.is_file()and p.suffix in ['.exe','.dll','.json'])
manifest=[dict(Path=p.relative_to(root).as_posix(),SHA256=hashlib.sha256(p.read_bytes()).hexdigest())for p in files]
(out/'final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
(root/'docs/verification/shell-context/final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'Modes':list(performance),'Acceptance':summary['OverallAcceptance']}))
