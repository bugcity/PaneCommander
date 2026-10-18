from pathlib import Path
import json,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/post-driver-acceptance-20261015'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
regression={}
for mode in ['Default','SoftwareOnly']:
    p=out/'regression'/mode/'links-regression.trx'
    tree=ET.parse(p);ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counts=tree.find('.//t:Counters',ns).attrib
    skipped=[r.attrib['testName'] for r in tree.findall('.//t:UnitTestResult',ns) if r.attrib['outcome']=='NotExecuted']
    regression[mode]=dict(Counters=counts,Skipped=skipped,Evidence=str(p.relative_to(root)))
summary=dict(Drivers=read(out/'drivers.json'),ProductHash=read(out/'product-hash.json'),
             Regression=regression,ProductCodeChanged=False,OsSettingsChanged=False,
             ClipboardWritten=False,ClipboardIsolationEstablished=False,
             UserDecision='専用セッション／VMを用意する',WaitingFor='Dedicated environment connection and clipboard-sharing isolation evidence',
             DefaultWhiteScreen='Not reproduced in observed baseline GUI actions; long-term stability unverified',
             NativeBackgroundPaste='Still absent in GUI; valid clipboard activation/execution deferred',
             PerformanceComparison='New fair three-repeat product comparison not performed',PopupStress='100/500/1600 not performed',
             GuiEvidence='actions.json and numbered JPEG/JSON',Scope='Baseline GUI input/capture, not comprehensive human acceptance')
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
report='''# Intel更新後 GUI受入の再開・専用環境待ち

2026-10-15。基準はIntel 32.0.101.7092、NVIDIA 32.0.16.1742、.NET10.0.12、既存Release製品DLL D65929F046FEADD7DBA70E3D146D5A86CF4E0EB46EA1DC83C79560DD398FFA27。白画面の深掘りは終了し、製品への回避処理は追加しない。

## 1. 描画回帰

Defaultは前回の専用製品PID6168を継続使用。新規起動の証跡はINTEL_DRIVER_RETEST_REPORT.mdの製品初期表示・フォルダー移動を継承し、今回の操作を新規起動と偽らない。今回、両ペイン、設定表示とEscキャンセル、151件の専用ファイル一覧スクロール、最大化・通常サイズ復元、最小化・復元、タブ追加と元タブへの切替、背景Shellメニューの表示・Esc終了を実入力とキャプチャで確認。最小化はIsIconic=trueの読み取り証跡あり。背景メニュー後のUIAフォーカスは右一覧Filesへ復帰。観測した画面で白表示は再発しなかった。

SoftwareOnlyは専用プロファイルで新規PID16180を起動し、左右一覧、設定表示・キャンセル、背景メニュー表示・Esc終了と右一覧へのフォーカス復帰を確認。ready.jsonのウィンドウ生成前方式・実効方式はSoftwareOnly。試験プロセスは通常終了。ユーザー自身の実画面回答は前回の最小Defaultについてのみで、今回の全GUI項目について人による受入完了とはしない。

スクロールは専用フォルダーへ追加した150ファイル＋既存のinside.txt。元のユーザーファイルは使用していない。タブ切替のスクロール位置保持など、描画以外の状態をこの確認だけで合格にしない。全サイズでの連続リサイズや長期安定性は未検証。

証跡：docs/verification/post-driver-acceptance-20261015/actions.jsonと01〜18のJPEG/JSON。Default初期起動の証跡はdocs/verification/intel-driver-retest-20261015/product-Default/。旧白画面調査は更新前の履歴として保持する。

## 2. 背景Paste

両方式の実ネイティブ背景メニューにPasteがないことを確認（12、17の画像）。通常クリップボードを変更していないため、この再確認は有効なFileDropをコピーした条件の試験ではない。

ソースShellContextMenu.csの背景経路はIShellFolder.CreateViewObject(IContextMenu)、QueryContextMenuはCMF_CANRENAME。既存background-readonly対照では、Direct経路はpaste verbなし、IShellView→GetItemObject(SVGIO_BACKGROUND)経路は無効のpaste項目あり。フラグ0/0x10/0x100・新しいCOM/HMENU・3独立プロセスで差を確認済み。この取得経路の差は再利用する根拠だが、Shellサイトやホストを含む有効化・実行の成立条件は未確定。名前だけのPaste項目や独自WPFメニューを追加せず、現時点で製品を置換しない。

## 3. クリップボード安全境界と停止点

現在query sessionでは通常ユーザーのconsoleセッション1のみがActive。別専用環境の接続先は未提供。既存の隔離作成はWin32 5/183で停止した証跡があり、安全な隔離成功の証跡はない。今回は失敗した経路を再試行したり、権限・OS設定を変更したりしていない。

ユーザーは今回「専用セッション／VMを用意する」と回答。環境種類・接続先・作業先を質問済み。準備済みとは解釈せず、クリップボード共有・リダイレクトが通常環境と分離されている証跡を確認するまでコピー／切り取り／Paste／復元／Explorer相互試験は安全上の理由で停止する。専用プロファイル・一時フォルダーだけではクリップボードの隔離にならない。

旧方式はOleGetClipboardのIDataObjectを保持し、同じ参照をOleSetClipboardで戻す方式。全形式の独立保存ではなく、以前のCLIPBRD_E_CANT_CLOSEの競合所有者・遅延レンダリング呼出し経路も未記録で、直接原因は未特定。Protect-ShellGuiClipboard.ps1は無効を維持。通常クリップボードを取得・変更・復元する試験は今回行っていない。

隔離成立後は、合成データのOLE取得／設定／所有寿命を確認し、通常ファイル・ディレクトリ・複数選択のcopy/cut/paste、Explorer双方向、失敗・キャンセルの実データを検証する。リンク制限は維持する。任意の全クリップボード形式の完全保存・復元を仮定しない。

## 4. 自動回帰

既存Releaseビルド、Verify-LinkOperations.ps1 --no-buildで両方式を今回実行。各74件中71合格・0失敗・3スキップ。TRXと出力はregression/Default、regression/SoftwareOnly。スキップ2件は実シンボリックリンクの作成権限不足、1件は以前から明示停止されている通常クリップボードのバックアップ／復元テスト。今回Ignoreやテストコードは変更していない。クリップボード不具合解消・全項目合格とは扱わない。

## 5. 独立した受入状態

|対象|今回の判定|
|---|---|
|ドライバー更新後の描画|観測した基本GUI操作は正常、白表示再発なし。人による全画面受入・長期は未検証|
|背景Paste互換性|未達。取得経路による差は既存証跡で確認、有効化・実行は隔離待ち|
|クリップボード検証の安全性|安全な隔離未成立。書き込み試験を停止し専用環境待ち|
|Shell機能の実GUI受入|背景表示・Esc・フォーカス復帰のみ今回追加。全コマンド、動的サブメニュー、Explorer等は未検証|
|ファイル操作の安全性|既存自動回帰の範囲を維持。今回の全キー・削除・競合・DnD等の実GUI試験は未実施|
|公平性能比較|今回未実施。前回各1回の参考値を3反復比較へ昇格させない|
|実Popup反復100/500/1600|今回未実施。基準メニュー各1回をストレス試験と扱わない|
|自動回帰|両方式71合格・0失敗・3スキップ|

## 次の実施順

1. ユーザーが用意する専用環境の接続先と、通常クリップボードとの分離を確認。
2. 隔離環境で合成クリップボードとShellView取得経路の有効化・実行を検証。根拠のある最小変更だけを実装し、同条件で再確認。
3. 未検証のShell／ファイル操作を専用データで確認。安全な削除のGUI確認は実行直前の確認ルールに従う。Shift+F6未実装やリンク競合／ごみ箱制限は機能不足として保持。
4. 同ビルド・データ・ウィンドウ条件で両方式を原則3反復、実GUIと専用ドライバーを分けて計測。Popup100回を起点に傾向を見て500/1600へ進む。

専用環境が未提供のため、今回の最新指示STEP3の停止点を維持する。OS／GPU設定、製品コード、デザイン、通常ユーザープロファイルは変更していない。
'''
(root/'POST_DRIVER_GUI_ACCEPTANCE_REPORT.md').write_text(report,encoding='utf-8')
notice='更新後GUI受入の最新状況：[POST_DRIVER_GUI_ACCEPTANCE_REPORT.md](POST_DRIVER_GUI_ACCEPTANCE_REPORT.md)。Defaultの基本GUI操作で白表示再発なし。両方式の回帰71合格・0失敗・3スキップ。Paste実行・クリップボード試験はユーザーが用意する専用環境の接続／隔離確認待ち。公平性能比較・大規模Popup反復は未実施。以下の旧記録を最新受入と混同しない。\n\n'
for name in ['INTEL_DRIVER_RETEST_REPORT.md','DEFAULT_REDRAW_INVESTIGATION_REPORT.md','SCREEN_OFF_DISABLED_GUI_REPORT.md','SHELL_CONTEXT_GUI_RESUME_REPORT.md','SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','GUI_REMAINING_ISSUES_REPORT.md','IMPLEMENTATION_STATUS.md']:
    p=root/name;text=p.read_text(encoding='utf-8-sig')
    if text.startswith('更新後GUI受入の最新状況：'):text=text.split('\n\n',1)[1]
    p.write_text(notice+text,encoding='utf-8')
checklist=root/'docs/verification/shell-context/GUI_CHECKLIST.md'
text=checklist.read_text(encoding='utf-8')
text=text.replace('既存のクリップボードは保存・復元する。','通常ユーザーのクリップボードを保存・復元できると仮定しない。書き込み試験は専用環境との分離を証明した場合に限る。')
prefix='更新後の最新：Default設定・スクロール・最大化/復元・最小化/復元・タブ切替・背景Shell表示/Esc、SoftwareOnly主要画面を確認。両方式の自動回帰71/0/3。専用セッション／VMの接続先とクリップボード隔離確認待ち。詳細は[受入再開報告](../../../POST_DRIVER_GUI_ACCEPTANCE_REPORT.md)。\n\n'
if not text.startswith(prefix):checklist.write_text(prefix+text,encoding='utf-8')
p=root/'docs/verification/shell-context/verification-summary.json';combined=read(p)
combined['LatestReport']='POST_DRIVER_GUI_ACCEPTANCE_REPORT.md';combined['PostDriverAcceptance']=summary
p.write_text(json.dumps(combined,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(regression,ensure_ascii=False))
