from pathlib import Path
import csv, json, statistics

root = Path(__file__).resolve().parents[1]
out = root / 'docs/verification/intel-driver-retest-20261015'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
summary = dict(Drivers=read(out/'drivers.json'),GpuPreference=read(out/'gpu-preference.json'),
               PhysicalObservation=read(out/'physical-observation.json'),PhysicalDefaultConfirmed=True,ProductCodeChanged=False,ForcedGc=False,Trials={})
rows=[]
for mode in ['Default','SoftwareOnly']:
    folder=out/(mode+'-1'); resource=out/(mode+'-1-resource')
    ready=read(folder/'ready.json')
    observations=read(folder/'observations.json')
    segment=read(resource/'idle-segment.json')
    samples=list(csv.DictReader((resource/'samples.csv').open(encoding='utf-8-sig')))
    samples=[s for s in samples if s['Stage']=='post-ops-idle-60']
    first,last=samples[0],samples[-1]
    seconds=(float(last['ElapsedMs'])-float(first['ElapsedMs']))/1000
    cpu=100*(float(last['CpuSeconds'])-float(first['CpuSeconds']))/seconds/int(last['Processors'])
    peaks=[100*(float(b['CpuSeconds'])-float(a['CpuSeconds']))/((float(b['ElapsedMs'])-float(a['ElapsedMs']))/1000)/int(b['Processors']) for a,b in zip(samples,samples[1:])]
    metrics={k:dict(Mean=statistics.mean(float(s[k]) for s in samples),Max=max(float(s[k]) for s in samples),End=float(last[k]),Start=float(first[k])) for k in ['PrivateBytes','WorkingSet','Handles']}
    summary['Trials'][mode]=dict(Ready=ready,Idle=segment,Seconds=seconds,CpuAllLogicalMeanPercent=cpu,CpuPeakAllLogicalPercent=max(peaks),Metrics=metrics,Observations=observations)
    p=metrics['PrivateBytes'];w=metrics['WorkingSet'];h=metrics['Handles']
    rows.append(f"|{mode}|{seconds:.2f}|{p['Mean']/2**20:.2f}/{p['Max']/2**20:.2f}/{p['End']/2**20:.2f}|{w['Mean']/2**20:.2f}/{w['Max']/2**20:.2f}/{w['End']/2**20:.2f}|{h['Start']:.0f}→{h['End']:.0f}|{cpu:.5f}|{segment['InputTickUnchanged']}|")
summary['DefaultGpuRoute']=read(out/'gpu-route-Default-1/gpu-route-summary.json')
if (out/'product-Default-verification.json').exists():
    summary['ProductDefault']=read(out/'product-Default-verification.json')
    summary['ProductStartup']=read(out/'product-Default/diagnostics/ready.json')
    assert summary['ProductStartup']['modeBeforeWindow']==summary['ProductStartup']['effectiveRenderMode']=='Default'
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
report='''# Intelドライバー更新後の再試験

2026-10-15。Intel UHD Graphicsは31.0.101.5445から32.0.101.7092へ更新。NVIDIAは32.0.16.1742で変わらず。ドライバー更新はユーザーが実施した。今回CodexによるOS/GPU設定変更、製品コード変更、再ビルドはない。

## 描画結果

新規Default最小WPF PID23252（唯一の対象ウィンドウID131968）で、初回キャプチャから文字・赤青の領域・ボタンを全面表示。ユーザーからも「実画面でも正常に表示されている」と回答を受領。Counterクリックで1へ更新、通常の背景色変更も全面に反映。更新前に実画面でも発生していた白表示は、更新後の今回の最小再現試験では再現しなかった。

個別GPU指定なし。DXGI LUIDとGPUカウンター5回の照合で対象のIntel側割当てを確認（共有約36.16 MiB）。NVIDIA側の対象インスタンスは今回未観測。DWMもIntel側の割当てを持つ。RenderMode.Default、RenderTier2、.NET10.0.12、DPI1.5、800×640。モジュール・カウンターは経路の手掛かりであり、Present全体のトレースではない。

読み込まれたIntel UMDも32.0.101.7092で、igdumdim64.dll、igd9trinity64.dll等を確認。更新前に存在したd3d9on12.dll/d3d12.dll/igd12umd64.dllは、今回の初期採取一覧にはない。d3d9.dllは両方に存在する。ロードされた実装の構成も変わっており、旧経路のまま正常化したとは断定しない。DLL不在だけで全フレームの経路や障害箇所を確定するものではない。

SoftwareOnlyも同じReleaseバイナリ・同じ画面で初期表示、Counterクリック、通常背景変更の反映をキャプチャで確認。SoftwareOnlyの新たな人による実画面確認は行っていない。

![Default更新後初期表示](docs/verification/intel-driver-retest-20261015/Default-1-initial.jpg)
![Default更新後背景変更](docs/verification/intel-driver-retest-20261015/Default-1-background.jpg)
![SoftwareOnly更新後初期表示](docs/verification/intel-driver-retest-20261015/SoftwareOnly-1-initial.jpg)

## リソース参考値

両方式を順に独立プロセスで実施し、Counterと背景変更の後に同じ30秒ウォームアップ＋60秒観測。既存の外部サンプラー約500ms間隔、CPUは24論理CPU全体で正規化。強制GCなし。起動から操作までの時間・人による確認時間は揃っておらず、各1回の探索的結果。長期安定性や正式な3反復性能比較とは扱わない。以前の試験にはBitmap生成も含まれており、更新前後のメモリ差をドライバー更新だけに帰属させない。

|方式|採取秒|Private平均/最大/終了 MiB|WS平均/最大/終了 MiB|ハンドル開始→終了|CPU平均 %|無入力tick一致|
|---|---:|---|---|---|---:|---|
'''+ '\n'.join(rows)+'''

応答はCounterと背景の反映を確認した定性評価。Dispatcher.ContextIdleは物理描画完了ではなく、物理描画応答時間は未測定。

## 判定と残課題

最小WPFのDefault白表示：今回の試験では解消をキャプチャと実機回答の両方で確認。更新前後の結果はIntel描画経路との関連を強く支持するが、ドライバー内部の具体的欠陥、Direct3D/DWMとの境界の故障所有者は未特定。OSセッション再起動等の影響を分離した旧ドライバーへの戻し試験は行っていない。

製品全体のGUI受入・長期性能・6タブ/1万件・Shell反復はこの最小試験だけで合格にしない。クリップボードはユーザー指示で保留のまま。既定描画方式の変更はしていない。

生データと再現：docs/verification/intel-driver-retest-20261015/。既存capture-probe Default/SoftwareOnly、Read-GpuRoute.ps1、Measure-ShellGuiProcess.ps1、Mark-RedrawIdle.ps1を使用。両プロセスは同じ二つの操作を行い、30秒＋60秒区間を採取した。製品コード変更がないため全自動回帰は再実行せず、過去の結果を新たな合格として扱わない。
'''
if 'ProductDefault' in summary:
    report+='''
## PaneCommander本体の追加確認

同じ既存Release製品DLL（SHA256 D65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27）を、専用のテストフォルダー・プロファイルで起動。通常ユーザー設定・ファイルは使用していない。PID6168、ウィンドウID853388、起動前適用方式・実効方式はいずれもDefaultとready.jsonで確認。初期画面で両ペイン・一覧・File Age背景色等が全面表示され、テストフォルダーへのダブルクリック移動後も正常に描画された。これはキャプチャと実入力による確認で、製品画面の新たな人による確認は未実施。全機能・Shell・6タブ・1万件・長期性能の受入に拡大しない。テスト用製品プロセスは表示確認用に残している。

![製品Default初期表示](docs/verification/intel-driver-retest-20261015/product-Default-initial.jpg)
![製品Defaultフォルダー移動後](docs/verification/intel-driver-retest-20261015/product-Default-navigation.jpg)
'''
(root/'INTEL_DRIVER_RETEST_REPORT.md').write_text(report,encoding='utf-8')
notice='Intelドライバー更新後の最新結果：[INTEL_DRIVER_RETEST_REPORT.md](INTEL_DRIVER_RETEST_REPORT.md)。32.0.101.7092で新規最小WPF Defaultは全面描画し、ユーザーも実画面正常を確認。以前の白表示は今回再現せず。製品全体の受入・故障所有者の確定は別途。\n\n'
for name in ['DEFAULT_REDRAW_INVESTIGATION_REPORT.md','GUI_REMAINING_ISSUES_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SCREEN_OFF_DISABLED_GUI_REPORT.md','IMPLEMENTATION_STATUS.md']:
    p=root/name;text=p.read_text(encoding='utf-8-sig')
    if text.startswith('Intelドライバー更新後の最新結果：'):text=text.split('\n\n',1)[1]
    p.write_text(notice+text,encoding='utf-8')
checklist=root/'docs/verification/shell-context/GUI_CHECKLIST.md'
text=checklist.read_text(encoding='utf-8')
prefix='Intel更新後：32.0.101.7092の新規最小WPF Defaultはキャプチャとユーザー実機回答で正常。製品版Defaultも初期表示と1回のフォルダー移動をキャプチャ・実入力で確認。全GUIチェックリストの合格ではない。詳細は[更新後再試験](../../../INTEL_DRIVER_RETEST_REPORT.md)。\n\n'
if prefix not in text:checklist.write_text(prefix+text,encoding='utf-8')
shell_summary=root/'docs/verification/shell-context/verification-summary.json'
combined=read(shell_summary);combined['LatestReport']='INTEL_DRIVER_RETEST_REPORT.md';combined['IntelDriverRetest']=summary
shell_summary.write_text(json.dumps(combined,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({m:{'PrivateMeanMiB':v['Metrics']['PrivateBytes']['Mean']/2**20,'CpuPercent':v['CpuAllLogicalMeanPercent']} for m,v in summary['Trials'].items()}))
