from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET,sys,runpy
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/gui-investigation-20261015'
if (root/'docs/verification/default-redraw-20261015/verification-summary.json').exists():
    runpy.run_path(str(root/'tools/Finalize-RedrawProbe.py'),run_name='__main__')
    sys.exit(0)
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
comparisons=[]
for trial in range(2,5):
    data=read(out/f'background-readonly/run-{trial}/background-readonly.json')
    assert data['Success'] and not data['ClipboardWritten'] and data['Menus']['QueriesUseFreshObjects']
    assert data['ClipboardSequenceBefore']==data['ClipboardSequenceAfter']
    for flag in ['0','10','100']:
        for route in ['Direct','ShellView']:
            query=data['Menus'][route+'Flags'+flag]['Query']
            paste=[c for c in query['Commands'] if c.get('Verb')=='paste']
            comparisons.append(dict(Trial=trial,Route=route,Flags='0x'+flag,PasteCount=len(paste),PasteStates=[c['State']for c in paste],FileDropAdvertised=data['FileDropFormatAdvertised']))
            assert len(paste)==(1 if route=='ShellView' else 0)
for trial in [1,2]:
    attempt=read(out/f'clipboard-isolation/run-{trial}/clipboard-isolation.json')
    assert not attempt['Success']
regression={}
for mode in ['Default','SoftwareOnly']:
    xml=ET.parse(out/f'regression/{mode}/regression.trx')
    counters=xml.find('.//{*}Counters');assert counters is not None
    regression[mode]=dict(counters.attrib)
    skipped=sum(r.attrib.get('outcome')=='NotExecuted' for r in xml.findall('.//{*}UnitTestResult'))
    regression[mode]['VerifiedSkippedResults']=skipped
    assert counters.attrib['passed']=='71' and counters.attrib['failed']=='0' and skipped==3
for mode in ['minimal-default','minimal-software']:
    ready=read(out/mode/'ready.json');assert ready['RenderTier']==2 and ready['DpiX']==ready['DpiY']==1.5
product=root/'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
product_hash=hashlib.sha256(product.read_bytes()).hexdigest().upper()
assert product_hash=='D65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27'
baseline=read(root/'docs/verification/shell-context/no-screen-off/final-manifest.json')
for item in baseline:
    if item['Path'].startswith('src/') and '/bin/'not in item['Path']:
        assert hashlib.sha256((root/item['Path']).read_bytes()).hexdigest()==item['SHA256']
counter=read(out/'minimal-default-counter.json')['accessibility']['focused_element']
assert 'Counter: 1' in counter
summary=dict(BackgroundComparison=comparisons,Regression=regression,ProductHash=product_hash,
             ProductSourceUnchanged=True,InteractiveClipboardWritten=False,
             PhysicalObservation=read(out/'physical-display-observation.json'),
             ClipboardDeferral=read(out/'clipboard-deferral.json'),
             WaitingFor=[],
             Acceptance={'GuiQuality':'Failed in Default physical display: white client area, hovered regions drawn; responsible component unknown',
                         'ShellCompatibility':'Partial: interface-dependent Paste presence verified; enable/execute unverified',
                         'ClipboardSafety':'Deferred by user: no test environment now; prior restoration failure unresolved',
                         'FileOperations':'71 regression tests pass in each mode; current GUI paths not all tested',
                         'ResourceLifetime':'Not remeasured; previous unmet result preserved',
                         'CpuMemory':'Fair comparison not started; previous exploratory values not promoted',
                         'Regression':'71 pass, 0 fail, 3 skipped each mode; unsafe clipboard test explicitly disabled'})
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'docs/verification/shell-context/verification-summary.json').write_text(json.dumps(dict(LatestReport='GUI_REMAINING_ISSUES_REPORT.md',Current=summary,PreviousEvidence='docs/verification/shell-context/no-screen-off'),ensure_ascii=False,indent=2),encoding='utf-8')
table='\n'.join(f"|{r['Trial']}|{r['Route']}|{r['Flags']}|{r['PasteCount']}|{r['PasteStates']}|"for r in comparisons)
report='''# 実GUI残課題の原因切り分け（2026-10-15）

現状：**ユーザーがDefaultの実画面でも白く、マウスを重ねた部分だけ描画されることを確認。Default描画品質は未達、原因切り分けを継続する。クリップボード試験はユーザー指示で保留。全体受入は保留。** 製品ソース・デザイン・描画設定の既定値・リンク安全制限は変更していない。以前のソースマニフェストとsrc配下を照合し、製品DLLもD65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27で一致。

## A. Default白画面

開始時・最終時のホスト診断はセッション1、WinSta0/Default、WTS SessionFlags=1（解除）、有効画面1台2560×1440。原ログ：artifacts/gui-investigation-20261015-environment と同名末尾-finalのrun-environment/desktop-environment.json。power-video-idle.txtでAC消灯なし（0秒）・DC180秒も読み取り再確認した。全期間を連続監視したという意味ではない。

既存Release製品を専用プロファイルDefault-418d9a4a2c5a42649e7b4bdccf433f4aで起動。ready.jsonはmodeBeforeWindow/effectiveRenderModeともDefault。WGC経路でWPF本体は白く、タイトルバーとShift+F10のネイティブ背景メニューは取得できる。Tabでタイトルleft→right、UIAの一覧フォーカスも右側へ変わる。Esc後の復帰を確認し、最終的に左側へ戻した。UIA取得・入力反応を描画正常の証拠にしない。

製品固有の一覧／Shell／テーマを含まない最小WPF診断を追加。同じ800×480、同じ色付き領域とカウンターボタンを独立プロセスで比較した。Defaultでは白いキャプチャを再現、Tab→SpaceでUIAのCounter: 0→1を確認。SoftwareOnlyでは赤・青・文字をキャプチャでき、実マウスでCounter: 1へ更新することを画面で確認。両方.NET 10.0.12、DPI 1.5×1.5、RenderTier=2、ウィンドウ生成前に指定方式を適用。RenderTierはハードウェア能力の値であり、SoftwareOnlyでも2になり得る。実際の全描画がGPUかは未測定。

この対照から、PaneCommanderのファイル一覧やShell実装は白いキャプチャを起こす必須条件ではないと分かる。その後、ユーザーが「実画面も白い」「ボタンなどへカーソルを移動するとそこだけ描画される」と報告した。**製品の実画面にも描画不良があることを確認し、キャプチャ経路だけの問題という仮説を除外する。** 原文と観測範囲はphysical-display-observation.json。最小WPFの実画面まで人が確認したわけではない。

Hoverによる局所的な更新で描画されることは、初期／全面描画と局所的な再描画・画面反映の差を調べる手掛かりになる。ただし、描画命令の生成、バックバッファ、無効領域処理、DWMへの呈示のどの段階かは未特定。WPF／Windows／GPUドライバーの責任を断定しない。常時Invalidateやタイマーによる全面再描画で隠す製品変更は行わない。製品固有の修正根拠はまだない。

追加の読み取り診断：Intel UHD Graphics 31.0.101.5445、NVIDIA RTX 4060 Laptop 32.0.16.1742。現在の2560×1440/165Hz出力はCIMではIntel側。Default製品PID22420にはd3d9.dll、d3d9on12.dll、d3d12.dll、igd12umd64.dll等が読み込まれていた。Intel／D3D9On12側の経路を調べる手掛かりだが、読込だけで全描画の経路・GPU割当・故障箇所を確定しない。nvui.dll 8.17.15.5527も読み込まれているが、Shell拡張の寿命対策とは別問題として扱う。過去12時間のSystem/Displayは該当イベントなし、DWM/DxgKrnlは指定Systemログとの非対応／Operationalログ未検出であり、詳細な描画エラー履歴は得られていない。エラーがない証明にはしない。ログ有効化やドライバー更新はしていない。原ログ：rendering-environment/rendering-environment.json。

今回使用できるsky公開APIにはキャプチャ方式の切替引数がなく、別経路のキャプチャは未実施。PrintScreen等で通常のクリップボードを書き換える検証はしていない。最小WPFのスクリーンショットも実ディスプレイの観測の代用にはしない。実画面の判定はユーザーによる製品画面の観測に基づく。

![最小Default](docs/verification/gui-investigation-20261015/minimal-default-0.jpg)
![最小SoftwareOnly](docs/verification/gui-investigation-20261015/minimal-software-0.jpg)

## B. 背景Pasteの取得経路

クリップボードを書き換えず、専用の空ディレクトリで比較。今回のFileDropFormatAdvertised=false、シーケンス番号は各回40→40。この状態は前回の有効なCF_HDROPを持つ状態と同一ではなく、有効化・貼り付け実行の証拠にはならない。

直接CreateViewObject(IContextMenu)とCreateViewObject(IShellView)→GetItemObject(SVGIO_BACKGROUND=0,IContextMenu)を比較した。フラグ0／CMF_CANRENAME=0x10／CMF_EXTENDEDVERBS=0x100、3独立プロセス。条件ごとに新しいCOMオブジェクト・HMENUを生成し、1回だけQuery、各COM参照とHMENUをfinallyで解放。Shell拡張は無効化していない。

|独立プロセス|経路|Queryフラグ|Paste項目数|Paste状態|
|---:|---|---|---:|---|
'''+table+'''

DirectではPasteなし、ShellViewではpaste verbの項目あり、状態1（無効／グレー）。取得インターフェースがメニュー構成を変えることを確認した。フラグ変更だけではDirectのPaste不在は変わらなかった。**Paste項目不在について取得経路の影響を特定したが、適切なホストサイト・IDropTarget・IShellBrowserなどを含む有効化と実行条件は未検証。** ビューのCreateViewWindowやサイトはまだ構築していないため、そのまま製品へ置換しない。独自WPFメニューでPasteを模倣する変更もしていない。

最初の探索run-1は同じIContextMenuへ複数回Queryしており、ShellViewでIDが累積し2回目以降のverb対応が不確かになった。比較ハーネスを修正し、run-2～4だけを上表の根拠とする。これは製品の不具合として扱わない。各raw JSONのQueryHr/GetBackgroundObjectHrを保持する。

## C. クリップボード保護

旧方式はOleGetClipboardのIDataObject参照を保持した後にコピー／切り取りし、同じ参照をOleSetClipboardで戻す。独立した全形式・全媒体のスナップショットを作っていない。OLEは遅延レンダリングや元アプリへのRPCを利用する場合があり、参照保持だけで任意の内容の保存を保証できない。前回の0x800401D4はOleSetClipboard内のCloseClipboard失敗を表すが、**失敗時の所有者・競合プロセス・遅延レンダリングの呼び出し経路は記録がなく、直接原因は未特定。** STAであるだけでは保証にならない。単なる再試行や全形式保存可能という仮定で保護スクリプトを復活させない。

Windowsのクリップボードはウィンドウステーションに属するため、デスクトップを増やすだけの方法は採用しなかった。診断用の子プロセスで新規非対話型ステーションを作り、その領域内で合成Unicode／バイナリと専用ファイルのcopy/cutを検証するハーネスを作成。名前付き作成はWin32 5（アクセス拒否）、自動名+CWF_CREATE_ONLYは183（既に存在）で失敗。いずれもCreateWindowStationで停止し、SetProcessWindowStation/OLE取得/EmptyClipboard/OleSetClipboardに到達していない。既存ステーションへの切替・内容破棄を許すフォールバックはしていない。ハーネスの後段自体は未実行であり、安全な保存・復元を達成したとは報告しない。

命名には管理者条件があるという公開仕様と失敗は整合するが、権限・DACLの変更やUAC操作はしていない。Windows SandboxのWin32_OptionalFeature.InstallStateは2（無効）を読み取ったのみで、有効化していない。ユーザーへ専用検証ユーザー／VM等の環境判断を依頼した。非対話型のAPI試験が成功した場合にも、対話型GUI・Explorer相互操作は別検証が必要。

その後ユーザーが「今は用意せず、クリップボード試験を保留する」と回答した。clipboard-deferral.jsonに保存。環境の再確認を繰り返さず、copy/cut/paste・復元・Explorer相互クリップボード試験は再開しない。描画の読み取り診断は独立して続ける。

Protect-ShellGuiClipboard.ps1は無効のまま。通常セッションで危険な復元処理を実行していた回帰テストもIgnore理由を明記して停止した。これによって復元失敗が修正された、あるいは元のユーザーデータを今回復元したとは扱わない。

## D. 修正・回帰と受入

製品修正はなし。追加は診断ツールClipboardIsolation.cs／CaptureProbe.csとCLI入口、旧クリップボード試験の安全停止。Releaseビルドは警告0・エラー0、release-build.logに保存。

|描画方式|合計|合格|失敗|スキップ|
|---|---:|---:|---:|---:|
|Default|74|71|0|3|
|SoftwareOnly|74|71|0|3|

スキップは実シンボリックリンク権限不足2件と明示停止したクリップボード1件。前回SoftwareOnlyの71/1/2をこの71/0/3で「不具合解消」と読み替えない。個々のテスト名・結果はregressionのTRX参照。既存のTab／Enter／フィルター／表示／共通ファイル操作／セッション等の自動検証を、今回未実施の実GUI全経路へ転用しない。

|評価|現状|
|---|---|
|GUI描画品質|Default未達：ユーザーが実画面の白表示・Hover部分だけの描画を確認。原因と修正は未確定|
|Shellメニュー互換性|一部検証：取得経路によるPaste項目の差。通常copy後の有効化・実行は未検証|
|クリップボード安全性|未解決：新規隔離確保前に安全停止、元データの任意形式保存・復元未達|
|ファイル操作の正しさ|既存自動回帰71件合格。今回実GUIの全操作を再検証してはいない|
|リソース寿命|今回再測定なし。以前のPrivate Bytes増加の未達を維持|
|CPU・メモリ性能|同条件3反復は未実施。前回の探索値を昇格させない|
|回帰|両方式71合格・0失敗・3スキップ。全項目合格ではない|

今回の指示では優先3課題を確認してから公平比較・GUI反復へ進む。Defaultの実描画不良の原因切り分けと、ユーザー指示で保留したクリップボード試験が残るため、100/500/1600回のGUI反復や性能比較を先行しない。回数削減を性能改善として報告していない。強制GC、常駐監視、FileSystemWatcher、無断の拡張停止、OS設定変更、通常クリップボードへの書き込みは行っていない。

## 再現と次の判断

1. Default実画面は白いと人が確認済み。次は最小WPFでも実画面の同症状が出るか、局所更新と全面更新の差、GPU／DWM診断を切り分ける。確認用製品は専用プロファイル・PID22420、タイトルleftで残している（継続稼働はユーザー観測後に再確認が必要）。最小診断2プロセスは正常終了済み。ユーザー文書のメモ帳は変更していない。
2. クリップボード試験は「今は用意せず保留する」というユーザー判断を受領済み。再開指示と安全な環境が整うまで未検証を維持する。
3. 隔離成立後に合成データの旧方式／独立実体化方式、CF_HDROP/Preferred DropEffect、背景Paste有効化・実行、Explorer相互操作を検証。リンク安全制限は維持。
4. 優先課題の結果を踏まえ、同じディレクトリ／ビルド／DPI／ウォームアップ／測定時間のDefault/SoftwareOnly各3独立プロセスと実GUI段階反復へ進む。

診断の再現は、Releaseビルド後に未使用の出力先で専用ツールを実行：

```powershell
tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe background-readonly <new-output>
tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe capture-probe Default <new-output>
tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe capture-probe SoftwareOnly <new-output>
```

clipboard-isolationモードは作成経路で停止した診断であり、未検証の後段を含む。安全性達成済みの保護手順として配布・再有効化しない。公平な性能測定中にはビルド／他試験を並行しない。

参照：[Window Stations](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)、[CreateWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)、[OleGetClipboard](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-olegetclipboard)、[OleSetClipboard](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-olesetclipboard)、[IShellView.GetItemObject](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellview-getitemobject)。
'''
(root/'GUI_REMAINING_ISSUES_REPORT.md').write_text(report,encoding='utf-8')
notice='最新の残課題調査（2026-10-15）：[GUI_REMAINING_ISSUES_REPORT.md](GUI_REMAINING_ISSUES_REPORT.md)。ユーザーがDefault実画面の白表示とHover部分だけの描画を確認。描画の原因切り分けを継続。クリップボード試験はユーザー指示で保留。以下は過去の報告として保持。\n\n'
for name in ['SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SHELL_CONTEXT_GUI_RESUME_REPORT.md','SCREEN_OFF_DISABLED_GUI_REPORT.md','IMPLEMENTATION_STATUS.md']:
    p=root/name;text=p.read_text(encoding='utf-8-sig')
    if text.startswith('最新の残課題調査（2026-10-15）：') and not text.startswith(notice):text=text.split('\n\n',1)[1]
    if not text.startswith(notice):p.write_text(notice+text,encoding='utf-8')
files=[]
for directory in ['src','tests','tools']:
    files.extend(p for p in(root/directory).rglob('*')if p.is_file()and p.suffix in ['.cs','.csproj','.xaml','.ps1','.py']and not any(s in ['bin','obj','TestResults']for s in p.relative_to(root).parts))
manifest=[dict(Path=p.relative_to(root).as_posix(),SHA256=hashlib.sha256(p.read_bytes()).hexdigest())for p in files]
(out/'final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'Regression':regression,'ProductSourceUnchanged':True,'Acceptance':summary['Acceptance']},ensure_ascii=False))
