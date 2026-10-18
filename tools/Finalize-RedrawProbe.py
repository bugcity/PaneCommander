from pathlib import Path
import json,hashlib,runpy
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/default-redraw-20261015'
runpy.run_path(str(root/'tools/Analyze-RedrawProbe.py'),run_name='__main__')
runpy.run_path(str(root/'tools/Analyze-GpuRoute.py'),run_name='__main__')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
performance=read(out/'performance-summary.json')
product=root/'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
assert hashlib.sha256(product.read_bytes()).hexdigest().upper()=='D65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27'
for entry in read(root/'docs/verification/shell-context/no-screen-off/final-manifest.json'):
    if entry['Path'].startswith('src/') and '/bin/'not in entry['Path']:
        assert hashlib.sha256((root/entry['Path']).read_bytes()).hexdigest()==entry['SHA256']
rows=[]
for mode,data in performance.items():
    p=data['Metrics']['PrivateBytes'];w=data['Metrics']['WorkingSet'];h=data['Metrics']['Handles']
    rows.append(f"|{mode}|{data['SampledSeconds']:.2f}|{p['Mean']/2**20:.2f}/{p['Max']/2**20:.2f}/{p['End']/2**20:.2f}|{w['Mean']/2**20:.2f}/{w['Max']/2**20:.2f}/{w['End']/2**20:.2f}|{h['Start']:.0f}→{h['End']:.0f}|{data['CpuMeanAllLogicalPercent']:.5f}/{data['CpuPeakAllLogicalPercent']:.5f}/{data['CpuFinalIntervalAllLogicalPercent']:.5f}|{data['IdleSegment']['InputTickUnchanged']}|")
summary=dict(Performance=performance,PhysicalProductFailureConfirmed=True,
             PhysicalProductObservation=read(root/'docs/verification/gui-investigation-20261015/physical-display-observation.json'),
             PhysicalMinimumFailureConfirmed=True,NoCapturePhysicalObservation=read(out/'no-capture-physical-observation.json'),
             GuiAcquisitionRequiredForFailure=False,GpuRouteEvidence=read(out/'gpu-route-1/gpu-route-summary.json'),
             LogicalVisualBitmapsIdentical=True,ResponsibleComponent='Unresolved after UI Visual generation; screen rendering/presentation path remains',
             Etw='Start denied by Windows 0x80070005; no valid trace',
             WaitingFor='Permission for temporary per-executable NVIDIA preference on minimal reproduction only',
             ProductCodeChanged=False,WorkaroundApplied=False,ClipboardTesting='Deferred at user request')
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
report='''# Default白画面：初期描画・部分更新の対照試験

2026-10-15（日本時間）。**製品に加え、GUIキャプチャ・UIA未実行の最小WPFでも実画面の初期白表示とHover領域だけの描画をユーザーが確認。GUI取得を現象発生の必須条件から除外。最小再現のGPU割当てとDWMのGPU使用はLUID照合でIntel側と確認した。Default描画品質は未達、責任コンポーネントは未確定、製品修正・修正後受入には至っていない。**

## 今回分かったこと

製品の一覧・Shell・テーマを持たない最小WPFで、同じReleaseバイナリ・800×640の同じ画面をDefault/SoftwareOnly独立プロセスで表示。同じ文字、色付き領域、Counter、単発の背景色変更、VisualのBitmap出力を使用した。.NET 10.0.12、DPI 1.5、RenderTier 2、DwmIsCompositionEnabled=trueは一致。方式はウィンドウ生成前に適用。RenderTierやDLL読込だけで実際のGPU経路を断定しない。

|観測点|Default-1|SoftwareOnly-1|
|---|---|---|
|初期Root.OnRender|1回実行|1回実行|
|初期キャプチャ|WPF本体は白い|文字・色・ボタンを表示|
|通常ボタンのMouseEnter|アプリ側イベントを記録|アプリ側イベントを記録|
|Counterのクリック|Counter=1。UIA/ログで受付確認、キャプチャは白い|Counter=1、画面にも反映|
|背景色を単発変更|Root.OnRenderが2回へ増加。キャプチャは白い|Root.OnRenderが2回へ増加。背景変更が画面にも反映|
|VisualのRenderTargetBitmap出力|文字・色・ボタンを正常に生成|文字・色・ボタンを正常に生成|
|操作後60秒無入力|白いキャプチャを維持|表示を維持|

両方式の診断BitmapのSHA-256は **F216EA8975D7C2621E224724F0A04097E62F80C469370F45F9189A59B1F3CC8D** で完全一致。したがって、同じVisualをソフトウェアでラスタライズした内容は同じであり、「内容が生成されていない」「ルートのOnRenderが一度も実行されない」だけでは説明できない。今回の対照では、問題の範囲は生成済みVisualを画面へ反映する経路に寄る。

ただしOnRenderはUI側の描画内容の生成であり、GPU命令実行・Present・DWM合成完了を証明しない。ContentRenderedやDispatcher.ContextIdleも物理描画完了の測定点ではない。診断BitmapはWPFのソフトウェア描画による出力で、実画面のスクリーンショットではない。Bitmap正常をDefault実画面正常へ読み替えない。後述のno-capture-defaultについてもユーザーが実画面の白表示・Hover部分の描画を確認済み。

![Defaultの初期キャプチャ](docs/verification/default-redraw-20261015/Default-1-initial-0.jpg)
![SoftwareOnlyの初期キャプチャ](docs/verification/default-redraw-20261015/SoftwareOnly-1-initial-0.jpg)
![Defaultの診断Bitmap（実画面ではない）](docs/verification/default-redraw-20261015/Default-1/logical-visual-bitmap.png)

## 候補の評価

- 製品固有の一覧／Shellコード：最小WPFでも白いキャプチャが再現するため必須条件ではない。製品固有の修正を加える根拠はない。
- UI側の初期生成／通常の無効化：OnRender初回実行、通常の背景DP変更で再実行、Counter更新、両Bitmap一致を確認。GPU側の無効領域処理や画面反映の欠落までは除外できない。
- WPFハードウェア描画／Direct3D／Intelドライバー／DWM：未分離。前回のDefault製品にはd3d9、d3d9on12、d3d12、Intelユーザーモードドライバーが読み込まれ、CIMではIntel側に2560×1440/165Hz出力がある。ただし読込は故障箇所の特定ではない。NVIDIA Shell拡張の寿命問題と同一原因にしない。
- GUI取得ツール：キャプチャ結果だけが白いという説明に加え、対象ウィンドウへのGUI取得介入が発生に必須という説明も、no-capture-defaultの実機観測により除外。ツールが全く影響しないという一般的保証まで主張するものではない。

最後の仮説に対し、同じバイナリの新規Defaultプロセス（PID8120、タイトルPaneCommander capture probe Default）を、GUIキャプチャ・UIA取得を一度も呼ばずに起動した。no-capture-default/launch.jsonを保存。アプリ内部のOnRender等の記録と外部Get-Process採取だけを実施。ユーザーから「GUIキャプチャ・UI Automationを実行していないにもかかわらず、初期画面は白く表示」「カーソルをボタンなどへ移動するとその部分だけ描画」と回答を受領。no-capture-physical-observation.jsonへ原文・条件を保存し、確認待ちを解消した。最小WPFでの実描画不良は取得ツール接続前に成立している。

## ドライバーと実際に観測したGPU割当て

GPU設定を変更せず、所有するPID8120と同セッションのDWM PID2560のGPUメモリ／エンジンカウンターを5回採取。DXGI EnumAdapters1/GetDesc1で各アダプターのLUIDを取得し、カウンターのLUIDと照合した。GUI取得・UIA・GPU割当て変更は行っていない。

|GPU|現在のドライバー|DXGI LUID|今回の対象プロセスのGPUメモリ|
|---|---|---|---|
|Intel UHD Graphics|31.0.101.5445（INF日付2024-04-20）|00000000:0000E948|5回とも観測。共有GPUメモリ平均148.48 MiB、専用0|
|NVIDIA GeForce RTX 4060 Laptop GPU|32.0.16.1742（INF日付2026-09-30）|00000000:0000ECE7|対象PIDのインスタンスは今回未観測|

DWMもIntel LUID上で共有GPUメモリ使用を確認。Microsoft Basic Render DriverのDWMメモリインスタンスは0であり、それをDWMのソフトウェア描画の証拠にはしない。GPUエンジンの利用率はアイドル採取時0で、過去のフレームをどのエンジンが実行したかは未測定。GPU共有メモリをPrivate Bytesに単純加算せず、DWMの値とも合算して物理総量としない。

最小再現にはwpfgfx_cor3.dll、d3d9.dll、d3d9on12.dll、d3d12.dll、Intelのigd12umd64.dll／igd12um64xel.dll（31.0.101.5445）等がロードされている。**WPF→D3D9→D3D9On12→D3D12→Intel UMDという経路が、モジュールとIntel GPU割当ての両方に整合する。これはAPI呼出し全体のトレースによる確定ではなく、証拠に基づく推定。** PresentとDWM合成の各段階は未分離。最小プローブでNVIDIA UMDやnvui.dllは観測されず、従来のNVIDIA Shell拡張のロードが白画面の必須条件ではない。NVIDIAのシステムドライバー全体が無関係とまでは断定しない。

原ログはgpu-route-1/gpu-route.json、対応表はgpu-route-summary.json。Read-GpuRoute.ps1で同条件の読み取りを再現できる。

次の対照案は最小再現EXE（tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe）だけを、一時的に高パフォーマンス（NVIDIA）へ指定し、新規Defaultプロセスで物理表示・初期／Hover描画・GPU LUIDを比較すること。現時点の当該EXEは個別GPU指定なし（gpu-preference-before.json）。試験後は個別指定を解除してこの状態へ戻す。設定がNVIDIAでもWPFが実際にIntelを使い続ける可能性があるため、再起動後のLUIDが変わらなければNVIDIA対照成立とは扱わない。表示出力やDWMがIntelに残る可能性もあり、これだけでDWMの責任を完全分離することはできない。ユーザーの実施前確認を得るまでGPU指定・OS設定は変更しない。

## 詳細トレースの制約

ETWプロバイダーとWPRプロファイルを読み取り列挙し、Direct3D12、DXGI、DxgKrnl、DWM等の存在を確認。WPFの名前ではlogmanに見つからなかったため、公式WPFのmanifestのGUID E13B77A8-14B6-11DE-8069-001B212B5009、Graphics/Perf/Generalに限定して一時セッション開始を試みた。Windowsからアクセス拒否（0x80070005）で開始できず、有効なWPF ETWは得られていない。これは自動承認レビューの拒否ではなく、実行先OSの権限による失敗。権限変更、UAC操作、他セッション停止、ログの恒久有効化は行っていない。

次の詳細診断は、管理可能な環境でWPFの描画キュー／PresentとGPU/DWMイベントを対応付けること。既存の「Systemログに該当なし」を正常の証拠にしない。トレースが得られないままドライバーやDWMを原因と断定しない。

## リソースと応答の観測

先の2条件は同じ順序でCounter→背景色変更→Bitmap保存、その後同じ30秒ウォームアップ・60秒観測。外部PowerShellで約500msごとに取得し、この区間はGUI入力・UIA・キャプチャ取得を止めた。LastInputInfoも区間前後で一致。既存のキャプチャ接続の寿命による影響は未分離。初期起動から操作までの待ち時間は揃っておらず、各1プロセスの探索的試験であり、厳密な製品性能比較ではない。強制GCなし。

|条件|採取秒|Private平均/最大/終了 MiB|WS平均/最大/終了 MiB|ハンドル開始→終了|CPU全24論理平均/瞬間最大/最終区間 %|入力tick不変|
|---|---:|---|---|---|---:|---|
'''+ '\n'.join(rows)+'''

no-capture-defaultは初期画面の参照値で、Counter／背景変更／Bitmap保存を行っていない。前2条件と同じ操作状態ではなく、メモリ差をそのまま取得ツールの使用量と断定しない。全条件とも最小WPF診断であり、2ペイン・6タブ・1万件の製品性能を示さない。3反復、長期安定性、修正後の数値は未測定。

![操作後の60秒観測](docs/verification/default-redraw-20261015/post-ops-idle.png)

observations.jsonにはイベント処理後からDispatcher.ContextIdleまでの差を集計可能な時刻を保存し、performance-summary.jsonにUiDispatchMeasurementsを記録した。この値はマウス入力送信からの遅延でも物理描画完了時間でもない。UIAと画像は同じ瞬間の原子的取得ではなく、クリック直後のUIA Counter=0と画像Counter=1などの過渡差があったため、処理後ログと再取得で確定した。物理描画応答時間は未測定。

## 変更と受入

変更は診断用CaptureProbe.csの有限イベント記録・一回の通常背景変更・診断Bitmap保存、外部の区間記録／集計スクリプトだけ。描画タイマー、CompositionTarget.Renderingの常駐購読、強制再描画ループ、製品のSoftwareOnly固定は追加していない。製品ソースと製品DLLは前回と同じ。診断ツールのReleaseビルドは警告0・エラー0（artifacts/redraw-probe-build.log）。製品修正がないため、前回の両方式71合格・0失敗・3スキップを履歴として維持し、新たな全回帰合格とは報告しない。

Default品質は未達、原因特定は未完了、修正未実施、修正後の実画面確認・性能影響は未検証。SoftwareOnlyは今回の最小画面の取得と操作を確認した範囲で正常。製品全体のGUI受入、性能受入、リソース寿命の受入は別々に保留。クリップボード試験は引き続きユーザー指示で保留。

前回の実機観測、GPU・ドライバー情報、クリップボード保留判断はGUI_REMAINING_ISSUES_REPORT.mdとその証跡を継承する。以前の「キャプチャ経路か実描画か未特定」は当時の記録であり、現在の判定は「製品の実描画不良を確認済み」。

参考：[WPF graphics tiers（RenderTargetBitmapのソフトウェア描画）](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers)、[公式WPF ETW manifest](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/Shared/Tracing/wpf-etw.man)、[Microsoft D3D9On12](https://github.com/microsoft/D3D9On12)、[DXGIアダプターのLUID](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc)、[GPUカウンターの計測元](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)。
'''
(root/'DEFAULT_REDRAW_INVESTIGATION_REPORT.md').write_text(report,encoding='utf-8')
notice='Default再描画の最新対照試験：[DEFAULT_REDRAW_INVESTIGATION_REPORT.md](DEFAULT_REDRAW_INVESTIGATION_REPORT.md)。GUI取得未実行の最小WPFでも実機白表示・Hover領域だけの描画を確認済み。GPUカウンターとDXGI LUIDはIntel側の割当てを示す。画面反映経路の故障箇所は未特定。GPU指定変更は未実施。\n\n'
for name in ['GUI_REMAINING_ISSUES_REPORT.md','SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SCREEN_OFF_DISABLED_GUI_REPORT.md','IMPLEMENTATION_STATUS.md']:
    p=root/name;text=p.read_text(encoding='utf-8-sig')
    if text.startswith('Default再描画の最新対照試験：') and not text.startswith(notice):text=text.split('\n\n',1)[1]
    if not text.startswith(notice):p.write_text(notice+text,encoding='utf-8')
(root/'docs/verification/shell-context/verification-summary.json').write_text(json.dumps(dict(LatestReport='DEFAULT_REDRAW_INVESTIGATION_REPORT.md',Current=summary,OtherIssues='docs/verification/gui-investigation-20261015/verification-summary.json'),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'ProductCodeChanged':False,'RootCause':'Unresolved','BitmapMatch':True,'WaitingFor':summary['WaitingFor']}))
if (out/'nvidia-preference-state/gpu-setting-restored.json').exists():
    runpy.run_path(str(root/'tools/Finalize-GpuPreferenceTrial.py'),run_name='__main__')
