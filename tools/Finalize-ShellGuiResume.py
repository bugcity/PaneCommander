from pathlib import Path
import runpy,sys
if (Path(__file__).resolve().parents[1]/'docs/verification/shell-context/no-screen-off/status.json').exists():
    runpy.run_path(str(Path(__file__).with_name('Finalize-NoScreenOff.py')),run_name='__main__')
    sys.exit(0)
import csv,hashlib,json,statistics,xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/shell-context/gui-resume'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tests={}
for mode in ['SoftwareOnly','Default']:
    doc=ET.parse(out/'regression'/mode/'links-regression.trx')
    counts=doc.find('.//t:Counters',ns).attrib
    tests[mode]={'Counters':counts,'NonPassed':[dict(Name=r.attrib['testName'],Outcome=r.attrib['outcome'],Message=''.join(r.find('t:Output/t:ErrorInfo/t:Message',ns).itertext()) if r.find('t:Output/t:ErrorInfo/t:Message',ns) is not None else '') for r in doc.findall('.//t:UnitTestResult',ns) if r.attrib['outcome']!='Passed']}
    assert int(counts['total'])==74
initial=read(root/'artifacts/shell-gui-resume-environment-20261014/run-environment/desktop-environment.json')
final=read(root/'artifacts/shell-gui-resume-environment-after-default/run-environment/desktop-environment.json')
assert initial['WTSSessionFlags']==1 and final['WTSSessionFlags']==0
fixture=root/'artifacts/shell-gui-resume/SoftwareOnly-3bc860a5d9af4b0ba75e67017104403f'
fixture_record=read(fixture/'verification.json')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
protected_hash=sha(fixture/'protected-target/sentinel.txt')
assert protected_hash==fixture_record['TargetHash']
assert sha(fixture/'left/copy-f5.txt')==sha(fixture/'right/copy-f5.txt')
assert not(fixture/'left/move-f6.txt').exists()and(fixture/'right/move-f6.txt').read_text()=='PaneCommander isolated GUI fixture: move-f6.txt'
assert not(fixture/'left/rename.txt').exists()and(fixture/'left/renamed.txt').read_text()=='PaneCommander isolated GUI fixture: rename.txt'
assert not(fixture/'left/drag.txt').exists()and(fixture/'right/drag.txt').read_text()=='PaneCommander isolated GUI fixture: drag.txt'
assert not(fixture/'left/permanent.txt').exists()
receipts=read(out/'software-recycle-receipts.json')['Receipts']
assert {Path(r['Original']).name for r in receipts}=={'trash-f8.txt','trash-delete.txt'}
for receipt in receipts:
    name=Path(receipt['Original']).name
    assert receipt['Exists']and receipt['OriginalAbsent']
    assert receipt['Hash']==hashlib.sha256(('PaneCommander isolated GUI fixture: '+name).encode()).hexdigest().upper()
proof=dict(ProtectedTargetHash=protected_hash,ProtectedTargetUnchanged=True,CopyHashesEqual=True,MoveSourceAbsent=True,MoveContentVerified=True,RenameVerified=True,DragEffect='Move',DragSourceAbsent=True,PermanentSourceAbsent=True,RecycleReceipts=receipts)
(out/'filesystem-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
with (out/'software-resource/samples.csv').open(encoding='utf-8-sig',newline='')as stream:
    samples=list(csv.DictReader(stream))
start,end=samples[0],samples[-1]
duration=(float(end['ElapsedMs'])-float(start['ElapsedMs']))/1000
cpu=float(end['CpuSeconds'])-float(start['CpuSeconds'])
memory={key:dict(Mean=statistics.mean(float(r[key])for r in samples),Max=max(float(r[key])for r in samples),Start=float(start[key]),End=float(end[key]),Delta=float(end[key])-float(start[key])) for key in ['PrivateBytes','WorkingSet','Handles']}
perf=dict(Samples=len(samples),DurationSeconds=duration,CpuSeconds=cpu,CpuMeanAllLogicalPercent=100*cpu/duration/int(end['Processors']),Metrics=memory,Scenario='Mixed functional GUI checks and observation; not controlled repetition benchmark',Product='SoftwareOnly before cancellation fix',ForcedGC=False,DefaultMeasured=False,ControlledIdleMeasured=False,Controlled100_500_1600=False)
(out/'performance-summary.json').write_text(json.dumps(perf,indent=2),encoding='utf-8')
import sys
sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
fig,axes=plt.subplots(3,1,figsize=(11,8),sharex=True)
times=[(float(r['ElapsedMs'])-float(start['ElapsedMs']))/1000 for r in samples]
axes[0].plot(times,[float(r['PrivateBytes'])/2**20 for r in samples],label='Private Bytes')
axes[0].plot(times,[float(r['WorkingSet'])/2**20 for r in samples],label='Working Set')
axes[0].set_ylabel('MiB');axes[0].legend()
axes[1].plot(times,[int(r['Handles'])for r in samples]);axes[1].set_ylabel('Handles')
cpu_series=[100*(float(b['CpuSeconds'])-float(a['CpuSeconds']))/((float(b['ElapsedMs'])-float(a['ElapsedMs']))/1000)/int(b['Processors']) for a,b in zip(samples,samples[1:])]
axes[2].plot(times[1:],cpu_series);axes[2].set_ylabel('CPU % (24 logical)');axes[2].set_xlabel('Seconds since sampling began')
for axis in axes:axis.grid(alpha=.25)
fig.suptitle('SoftwareOnly actual GUI functional checks: mixed observation, NOT a controlled benchmark')
fig.tight_layout();fig.savefig(out/'software-mixed-resources.png',dpi=130);plt.close(fig)
status=read(out/'resume-status.json')
summary=dict(Status=status,Tests=tests,EnvironmentBefore=initial,EnvironmentAfter=final,Performance=perf,OverallAcceptance='Pending; SoftwareOnly regression has one explicit failure; GUI retest blocked')
for path in [out/'verification-summary.json',root/'docs/verification/shell-context/verification-summary.json']:
    path.write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
actions=read(out/'actions.json')
menu_labels=['software-file-context','software-shift-f10','software-apps-menu','software-file-menu-properties-start','software-multiple-context','software-unselected-directory-context','software-copy-context','software-background-menu','software-rename-context']
assert all(label in [r['label']for r in actions]for label in menu_labels)
report=f'''# Windowsロック解除後の実GUI検証

2026-10-14。**実GUI操作はSoftwareOnlyで再開できたが、途中でWindowsが再びロック状態になったため停止。全体受入は保留。**

## 環境と前回エラー

開始時：WTS SessionFlags=1（解除）、ActiveConsoleと診断プロセスはセッション1、WinSta0/Default、有効画面1台2560×1440、Intel UHD Graphics。ダミーHDMIはユーザー申告で未接続。メモ帳の初回起動は「targetable windowなし」だったが、一覧再取得で「タイトルなし」を一意に選択でき、アクティブ化・空の編集面クリック・実キー `a` の表示まで確認した。前回のアクティブ化エラーはこの時点では解消。

SoftwareOnlyのPaneCommanderもアクティブ化・画面取得・実マウスとキー操作が成功。UIA要素取得だけでは判定していない。診断のmodeBeforeWindow/effectiveRenderModeもSoftwareOnlyを確認。

修正後のDefaultプロセスは起動し、両設定がDefaultであることをready.jsonで確認した。ただしアクティブ化は `failed to activate captured window`。対象を一覧から再取得して一度再試行しても同じエラー。再診断はSessionFlags=0（ロック）、画面は引き続き1台2560×1440。自動ロック無効化と現在の解除状態は分けて扱う。再ロックの契機は未特定。GUI入力を停止し、手動解除を依頼した。OS設定や拡張の変更、認証の回避は行っていない。

computer-useスキルの参照先guidance.mdは「If the Windows desktop is locked, stop immediately and ask the user to unlock the desktop.」と定める。現在は解除待ち。有効画面が認識されているため、今回の証拠だけではダミーHDMIを必須と判断しない。

## 実操作の判定（SoftwareOnly、修正前ビルド）

|項目|判定と証拠|
|---|---|
|通常ファイル・フォルダー・背景の右クリック|実表示を確認。画像と操作後のUIAを各ラベルのPNG/JSONへ保存|
|複数選択を右クリック|alpha/bravoの2行強調を維持したメニューを確認|
|未選択項目を右クリック|folderだけへ選択対象とアクティブ枠が切り替わることを確認|
|Shift+F10・Apps|実キーでメニュー表示を確認|
|Esc・フォーカス復帰|フォルダーメニュー消失と、その後Tabで右ペインへ切り替わる実画面を確認。一部の直後画像にメニューの残像があり、全Esc画像を独立の成功証拠とはしない|
|プロパティ|alpha.txtのShellプロパティ、場所・種類・サイズ45Bを確認。Esc後の一覧復帰を確認|
|開く|Windowsの.txtアプリ選択画面までは表示。『一度だけ』の入力後に内容表示は確認できず、完了未検証。関連付け設定は変更していない|
|Shellコピー|alpha.txtのコピーをマウスで実行。読み取り専用検査でFileDropとPreferred DropEffect、専用パス1件を確認|
|背景貼り付け|失敗／互換性未達：有効なFileDropがあるが背景メニューにPasteがない。処理完了を確認できない。CreateViewObjectによる取得経路と実表示を再現済み。提供側のsite/IShellViewとの関係は未特定、推測で製品を変更しない|
|切り取り・Explorer相互操作|未検証。クリップボード復元失敗を検出したため追加操作を停止|
|Shell名前変更|rename.txt→renamed.txtを実入力して確定。旧名の消失・新名の内容・一覧更新を確認|
|F5|copy-f5.txtを既定の反対側へコピー。左右のSHA256一致を確認|
|F6|move-f6.txtを反対側へ移動。元は不存在、先の内容は元のマーカーと一致|
|Shift+F6|現行キー定義に割当なし。今回追加しない。実操作の成功証拠はない|
|F8/Delete|trash-f8.txtとtrash-delete.txtの一覧からの消失を確認。専用パスに一致するごみ箱$Iメタデータと$R実体・ハッシュを別途読み取り専用で確認|
|Shift+Delete|permanent.txtの対象名・完全削除確認を表示。『いいえ』で元ファイル維持、再試行の『はい』で削除を確認|
|キャンセル時の表示|不具合：『いいえ』で0x80270000の警告を表示。後述の修正後GUI再試験はロックで未検証|
|右クリック削除|今回の実GUIでは未検証。F8/Deleteの成功を転用しない|
|DnD|drag.txtを左→右へドラッグし移動を確認（コピーではない）。元消失・先実在。別効果・外部DnDは未検証|
|Shell拡張|OneDrive、Git、Visual Studio等の項目表示を確認。外部拡張実行と動的サブメニューは未検証。ターミナル・セキュリティ機能は操作していない|
|リンク安全性|専用ジャンクションを作成し一覧で[DIR] J表示。番兵のハッシュ不変を確認。実GUIリンク操作・実シンボリックリンクは未検証、制限を解除していない|

QueryContextMenuの取得・ポップアップ表示・入力受付・コマンド完了は別判定。表示しか確認していない項目を完了扱いしない。

## クリップボード復元の問題

事前にOleGetClipboardで元のCOMデータを保持し、内容はログに出さなかった。試験後の復元処理が **CLIPBRD_E_CANT_CLOSE (0x800401D4)** で失敗し、終了時に参照を解放した。元の内容を復元できたことは確認できない。保護方法の検証が不足していた点は今回の検証手順の問題。復元成功とは扱わず、追加GUIクリップボード試験を停止した。Protect-ShellGuiClipboard.ps1は再利用による上書きを防ぐため、冒頭で停止するよう無効化した。取得したCOM参照は既に失われ、今回の証跡から元の内容を再構成することはできない。

SoftwareOnlyの既存クリップボード自動テストも復元行55で同じHRESULTの失敗。Defaultの再試験は事前取得がCLIPBRD_E_CANT_OPENとなりスキップし、その後の環境診断で再ロックを確認した。前回の63/66や今回のShellコピー成功を、復元の保証に転用しない。

## 発見した製品不具合と修正

通常ファイルのShift+Delete確認を『いいえ』で中止するとPerformOperationsがCOMException 0x80270000を返すケースを再現。以前はGetAnyOperationsAbortedへ到達せずエラー表示になった。

ShellFileOperationService.CompleteShellExecutionでPerformOperationsのCOM失敗後にも中止フラグを取得し、**今回観測したキャンセルHRESULTと中止=trueの両方を満たすときだけ** Aborted結果へ変換する。その他のHRESULTや中止=falseの失敗は隠さない。成功HRESULTでも中止フラグを確認する。参照：[GetAnyOperationsAborted](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-getanyoperationsaborted)。

4件の追加単体テストでこの境界を検証。Releaseビルドは警告0・エラー0。修正後の実GUI中止確認はDefaultの再ロックで未検証。背景Paste不在には推測の修正を加えていない。

## 修正後の自動回帰

|描画方式|合計|合格|失敗|スキップ|
|---|---:|---:|---:|---:|
|SoftwareOnly|74|71|1|2|
|Default|74|71|0|3|

SoftwareOnlyの失敗は既存クリップボード試験の復元HRESULT。両方式とも実シンボリックリンク2件は作成権限不足のスキップ。Defaultはさらにクリップボード事前取得不可をスキップ。**全回帰合格とは報告しない。** 以前の70件中67件合格は変更前の履歴として保持。

## 実GUIリソース観測

SoftwareOnly製品PID2428の実操作中を500ms間隔で読み取り専用サンプル。{len(samples)}点、{duration:.2f}秒。Private Bytes平均 {memory['PrivateBytes']['Mean']/2**20:.2f} MiB／最大 {memory['PrivateBytes']['Max']/2**20:.2f}／終了 {memory['PrivateBytes']['End']/2**20:.2f}、開始→終了増分 {memory['PrivateBytes']['Delta']/2**20:+.2f} MiB。WS平均 {memory['WorkingSet']['Mean']/2**20:.2f}／最大 {memory['WorkingSet']['Max']/2**20:.2f}／終了 {memory['WorkingSet']['End']/2**20:.2f} MiB。ハンドル開始 {memory['Handles']['Start']:.0f}→終了 {memory['Handles']['End']:.0f}、最大 {memory['Handles']['Max']:.0f}。CPU累計差 {cpu:.4f}秒、全24論理CPU平均 {perf['CpuMeanAllLogicalPercent']:.4f}%。強制GCなし。

これはGUIの機能試験・UIA取得・画面キャプチャ・操作待ちが混在する観測であり、分離されたメニュー反復性能試験ではない。描画方式比較へ転用しない。GUIから確認したメニュー表示は9回（file系7、directory1、background1、複数選択をfile系に含む）。100/500/1600回の制御された開閉、種類別性能、開閉後の制御アイドルは再ロックまでに未実施。Default実測は未測定。専用STAドライバーの1600回と混同しない。**リソース安定性の合否は保留。**

サンプラーは最初の非昇格実行では対象プロセスを取得できず0点で終了したため除外し、所有プロセスを取得できる実行のCSVだけを使用した。操作ラベル用stage列はunmarkedであり、種類別CPU値を事後に作らない。

![実GUIの混合観測](docs/verification/shell-context/gui-resume/software-mixed-resources.png)

## 証跡と再開事項

- このフォルダーgui-resumeのactions.json、操作別PNG/JSON、regressionのTRX、software-resource/samples.csv、performance-summary.json。
- 原環境ログ：artifacts/shell-gui-resume-environment-20261014 と shell-gui-resume-environment-after-default。
- 専用データ：artifacts/shell-gui-resume/SoftwareOnly-3bc860a5d9af4b0ba75e67017104403f、Default-aef0dd8fbe53435e80ad51c5dfe6acaf。ユーザーファイルは操作していない。
- 今回起動した空のメモ帳へテスト文字 `a` を入力した。ファイル保存操作や既存文書の編集はしていない。
- Defaultの専用プロセスは既存の診断終了イベントで正常終了。GUI入力・強制終了・OS変更による後始末はしていない。メモ帳のテスト文書は残している。
- filesystem-proof.jsonに内容ハッシュと専用ごみ箱実体を照合した結果を保存。DnDの操作ラベルはdrag-copyだが、実結果は移動であり、コピー成功の証拠には使用しない。
- 次に必要な環境操作：ユーザーがWindowsを手動解除し、解除状態のデスクトップを維持する。設定を無断変更せず、その状態を再診断してから再開する。
- 再開後はキャンセル修正の実操作、背景Pasteの提供経路、損失なく復元できるクリップボード保護、未検証のリンク・削除・Explorer・動的拡張、両描画方式の反復／アイドル測定を実施する。

機能実装・GUI互換性・安全性・性能・回帰のいずれも今回全項目合格には至っていない。
'''
(root/'SHELL_CONTEXT_GUI_RESUME_REPORT.md').write_text(report,encoding='utf-8')
notice='2026-10-14 GUI再開の最新結果は [SHELL_CONTEXT_GUI_RESUME_REPORT.md](SHELL_CONTEXT_GUI_RESUME_REPORT.md)。開始時のロック解除・SoftwareOnlyの実操作成功後、再びロックを検出して停止。キャンセル表示を修正。修正後回帰はSoftwareOnly 74件中71合格・1失敗・2スキップ、Default 71合格・0失敗・3スキップ。クリップボード復元は確認できず、背景Pasteは未達。以下は前回時点の報告として保持。\n\n'
for name in ['SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','README.md','IMPLEMENTATION_STATUS.md']:
    path=root/name
    text=path.read_text(encoding='utf-8-sig')
    if not text.startswith(notice):path.write_text(notice+text,encoding='utf-8')
files=[]
for directory in ['src','tests','tools']:
    files.extend(p for p in(root/directory).rglob('*')if p.is_file()and p.suffix in ['.cs','.csproj','.xaml','.ps1','.py']and not any(s in ['bin','obj','TestResults']for s in p.relative_to(root).parts))
files.extend(p for p in(root/'src/PaneCommander.App/bin/Release/net10.0-windows').iterdir()if p.is_file()and p.suffix in ['.exe','.dll','.json'])
manifest=[dict(Path=p.relative_to(root).as_posix(),SHA256=hashlib.sha256(p.read_bytes()).hexdigest())for p in files]
(out/'final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
(root/'docs/verification/shell-context/final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'Tests':{mode:value['Counters']for mode,value in tests.items()},'Gui':'SoftwareOnly partial; renewed lock stops Default','FilesystemProof':True,'Overall':'Pending'},ensure_ascii=False))
