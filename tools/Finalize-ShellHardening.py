from pathlib import Path
import runpy,sys
if (Path(__file__).resolve().parents[1]/'docs/verification/shell-context/gui-resume/resume-status.json').exists():
    runpy.run_path(str(Path(__file__).with_name('Finalize-ShellGuiResume.py')),run_name='__main__')
    sys.exit(0)
import hashlib,json,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1];out=root/'docs/verification/shell-context/hardening';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest().upper()
tests={}
for mode in ['SoftwareOnly','Default']:
    document=ET.parse(out/'complete'/mode/'links-regression.trx');counter=document.find('.//t:Counters',ns).attrib
    assert int(counter['total'])==70 and int(counter['passed'])==67 and int(counter['failed'])==0,counter
    tests[mode]=dict(Total=70,Passed=67,Failed=0,Skipped=3,SkippedTests=[r.attrib['testName'] for r in document.findall('.//t:UnitTestResult',ns) if r.attrib['outcome']!='Passed'])
desktop=read(root/'artifacts/shell-desktop-environment/run-environment/desktop-environment.json')
gui=dict(DesktopEnvironment=desktop,NotepadActivation='failed to activate captured window',Recovery='Unique returned Notepad window refreshed; second activation failed identically',ActualMouseKeysVerified=False,PopupVerified=False,ScreenshotVerified=False,ToolSessionIdentity='Not exposed by GUI tool; diagnostic process is console session 1',Clipboard='Read-only OpenClipboard failed with Win32 5; unit test CLIPBRD_E_CANT_OPEN',OsSettingsChanged=False,ExtensionSettingsChanged=False,UserUnlockRequested=True)
(out/'gui-environment.json').write_text(json.dumps(gui,ensure_ascii=False,indent=2),encoding='utf-8')
summary=dict(Tests=tests,FeatureImplementation='Partial acceptance; link recycle and collision UI gaps remain',GuiCompatibility='Pending locked desktop',FileSafety='Normal recycle receipt, junction safe refusal, staged-copy cancellation and existing link engine tested; symlinks/GUI unverified',Resources='Handle cycle resolved for observed nvui version; Private Bytes unmet',Regression='Passed with 3 explicit skips each mode',OverallAcceptance='Pending',ForcedGC=False)
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'docs/verification/shell-context/verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
cause='''# 背景Shellメニュー リソース寿命の原因調査

2026-10-14。**Eventハンドル増加のトリガーは特定して対策済み。Private Bytesの確保元は未特定、全体受入は保留。**

## 再現と所有関係

増えるハンドルの種類はEvent、名前は `\\Sessions\\1\\BaseNamedObjects\\HomePageEvent`。各ハンドルの所有プロセスは専用診断プロセスです。同じ名前のEventへの追加参照であり、400個の別Eventオブジェクトを生成したという意味ではありません。

自プロセスの有効ハンドル一覧を取得し、NtQueryObjectで種類・名前を検査しました。20回ウォームアップ後、100回の反復で次を観測しました。診断ではハンドル追跡を自プロセスだけに設定し、終了時に解除。グローバルのVerifier／レジストリ設定は変更していません。これらは計測用APIで、製品には組み込みません。

|条件|HomePageEventハンドル before→after|説明|
|---|---:|---|
|メニューなし|0→0|同じ自己サンプル処理|
|COM取得のみ|0→0|CreateViewObject取得・解放、Queryなし|
|CMF_DEFAULTONLY|0→0|既定verbだけの対照。全拡張排除の保証ではない|
|最小の生COM背景メニュー|20→120|RCWなし、HMENU/PIDL/COMを明示解放|
|改修前の製品背景メニュー|20→120|RCW経由|
|nvui DLLを1参照保持|20→20|Query回数を変えず、IAT変更もなし|
|nvui DLLのロード・解放のみ|20→120|Shell、PIDL、HMENU、RCWを使わない対照|

最後の対照で `nvui.dll` のロード／アンロード経路だけでも同じEvent参照が増えることを確認しました。観測したファイルは `C:/Program Files/NVIDIA Corporation/NVIDIA App/NvCpl/nvui.dll`、FileVersion `8.17.15.5527`、ProductVersion `11.0.9.251`、CompanyName `NVIDIA Corporation`。DLL読込の相関だけで原因としたものではありません。

ただし、DLL内のどの関数・ソース行が開いたか、Eventオブジェクトの最初の作成サービスまでは特定していません。ハンドル追跡の作成／解放履歴は取得できましたが、スタックは空でした。IAT観測はDLLを保持する介入で現象が停止し、CreateEvent／OpenEventの呼出しスタックを捕捉できていません。これらを作成元スタックの証拠として扱いません。OneDriveを原因とする証拠もありません。

## 対策

`ShellExtensionLifetime` は、背景メニューが実際にロードした当該CompanyName・FileVersionのDLLだけに、NativeLibraryによる1参照を取得します。プロセス終了時に解放します。上限は1モジュール。COMオブジェクトやメニューは保持せず、他者のEventハンドルを直接閉じません。NVIDIAのメニューを無効化せず、Shell APIの呼出し回数も変更しません。初回ロードのメモリ保持という代償がありますが、反復ロードの未解放参照を止めるための限定対策です。NVIDIA側の実装を修正したものではなく、他バージョンへ無条件適用もしません。

同じ1,600回×3プロセスでハンドル増分中央値は＋392→−7。背景単独400回は＋397→−3でした。一方、Private Bytes増分中央値は76.66→76.16 MiBで、継続増加は残っています。ハンドル対策だけでメモリ条件を合格にしていません。

## 所有リソースの監査

|対象|所有・解放と確認範囲|
|---|---|
|IShellFolder|desktop取得参照はBind後finallyでRelease。対象folderはメニューのusing終了時にRelease|
|CreateViewObjectのIContextMenu|選択項目のGetUIObjectOfと同じスコープ所有。終了・キャンセル・例外でRelease|
|IContextMenu2/3|同じRCWの別インターフェース。別々にFinalReleaseして二重解放しない|
|HMENU|CreatePopupMenuで所有。DestroyMenuが子メニューも破棄。GetSubMenuの借用値は個別破棄しない|
|PIDL|SHParseDisplayNameの所有値はCoTaskMemFree。ILFindLastIDはその中の借用値|
|OLE|STAで成功したOleInitializeを同じスレッドでOleUninitialize。外側WPF/OLE初期化を壊さない|
|例外時解放|Disposeをfinallyの階層へ変更し、ある解放が失敗しても残りの解放を試みる|
|HwndSourceHook|Trackの直前だけ追加し、finallyで解除。常駐フックにしない|
|メッセージ|WM_INITMENUPOPUP/WM_DRAWITEM/WM_MEASUREITEM/WM_MENUCHARを転送。実ポップアップ互換性は未検証|

最小の生COM条件でも増えたため、今回のEvent増加をRCW二重保持と断定しません。HMENUはUSERリソースであり、Process.HandleCountだけで破棄を証明した扱いにもしていません。

## 残る調査

Private Bytesはメニューなし対照＋6.52 MiBに対し、混合条件では約76 MiB増えます。内訳は未特定です。次は対象プロセスに限定したヒープ割当スタック（WPR/WPAまたはWinDbg）と、実HWND・実TrackPopupMenu経路を比較する必要があります。現在の環境にはWPRはありますがデバッガーは確認できず、OS設定を変更するヒープトレースは実施していません。原因不明のまま追加DLLを保持したり、GCや呼出し抑制で値を隠す修正はしていません。

原ログ：`artifacts/shell-lifetime-pin-control`、`artifacts/shell-native-module-cycle`。集計：`docs/verification/shell-context/hardening/cause-controls.csv`。改修前のソース・製品・報告書は `artifacts/shell-hardening-before` に保全しました。

初期の無効番号走査はSTATUS_INVALID_HANDLEで診断プロセスが終了したため除外。`shell-lifetime-open-event` はビルド失敗後に旧診断バイナリが起動されたため、原因・性能の判断から除外しています。その他の空スタックの記録はハンドル種類の補助にとどめ、スタック成功や実GUI成功に数えません。

API資料：[NtQueryObject](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryobject)、[ハンドル追跡](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/-htrace)、[診断構造体の参照元](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntpsapi.h)。
'''
gdi=[]
for folder in sorted((root/'artifacts/shell-hardening-gdi-diagnosis').glob('run-*')):
    data=read(folder/'diagnosis.json')
    rows=data['Rows']
    gdi.append(dict(Mode=folder.name,GdiBefore=rows[0]['GdiObjects'],GdiAfter=rows[-1]['GdiObjects'],UserBefore=rows[0]['UserObjects'],UserAfter=rows[-1]['UserObjects']))
(out/'gui-resource-diagnosis.json').write_text(json.dumps(gdi,indent=2),encoding='utf-8')
cause+='\n追加の100回反復診断では最小API／製品ともGDIオブジェクト64→64、USERオブジェクト8→8でした。個数の継続増加は観測しませんでしたが、これだけではPrivate Bytesの確保元は特定できません。原ログ：`artifacts/shell-hardening-gdi-diagnosis`。\n'
(root/'SHELL_CONTEXT_CAUSE_REPORT.md').write_text(cause,encoding='utf-8')
implementation='''# Windows Shellコンテキストメニュー 実装・受入状況

2026-10-14 最新の性能改善・削除仕様の整合性確認を反映。**総合受入は保留。ハンドル増加は対策済みですが、メモリ増加と実GUI確認が残っています。**

## 今回の結果

- NVIDIA `nvui.dll 8.17.15.5527` のロード・解放で `HomePageEvent` ハンドルが増える経路を、Shellを使わない対照でも再現。対象版だけ1モジュール参照をプロセス終了まで保持する対策を実装。
- 混合1,600回×3プロセスのハンドル増分中央値＋392→−7、背景400回＋397→−3。
- Private Bytes増分中央値76.66→76.16 MiB。継続増加が残り、性能条件は未達。
- Releaseビルド警告0・エラー0。Default／SoftwareOnlyそれぞれ70件中67件合格・失敗0・3件スキップ。
- Windowsは有効ディスプレイ1台、2560×1440を認識。WTS APIはセッション1をロック状態と報告。メモ帳も再取得後を含め `failed to activate captured window`。実ポップアップ表示・入力・コマンド実行のGUI証跡は未取得。

[原因調査](SHELL_CONTEXT_CAUSE_REPORT.md)、[性能比較](SHELL_CONTEXT_PERFORMANCE_REPORT.md)、`docs/verification/shell-context/hardening/`のCSV・JSON・TRXを参照。改修前の正本は `artifacts/shell-hardening-before/source/` に保全しています。

## 現在の機能と互換性境界

Windowsから取得する従来形式のネイティブメニューです。IShellFolder/GetUIObjectOf/CreateViewObject、PIDL、IContextMenu/2/3、TrackPopupMenuEx、Unicode InvokeCommandを使用し、WPF ContextMenuで模倣していません。詳細一覧と階層3列に接続し、選択済み行の複数選択を保持、未選択行では置換、空白では背景を対象にします。Shift+F10とAppsに接続し、メニュー中の独自キー・DnD・ファイル操作の競合を抑止します。

Windows 11のExplorer新形式UI、IShellView/IFolderViewのサイトは未実装です。動的新規作成などサイト依存コマンド、外部拡張の実表示・実行は未検証です。QueryContextMenuの項目取得成功を、表示・入力・コマンド実行成功の代用にしません。実画面スクリーンショットは取得できていません。

## 最新の削除仕様

以前の「F8／右クリックは完全削除」は今回のユーザー指示で置き換わります。

|操作|現在の接続|
|---|---|
|F8・Delete|ごみ箱へ移動を要求|
|Shift+Delete|確認後に完全削除を要求|
|右クリックdelete|共通基盤へ、ごみ箱を要求|
|Shift+F8|完全削除へは割り当てない|

ボタン説明と設定画面も更新。Deleteは実際にフォーカスのある一覧の選択項目を捕捉し、階層プレビューで別の項目を誤削除しないようにしました。TextBox編集中はファイル削除のキーを横取りしません。

通常のローカル固定NTFSのごみ箱操作はFOFX_RECYCLEONDELETEを使用。恒久削除への変更時はFOF_WANTNUKEWARNINGを維持します。ネットワーク・未検証ボリュームは安全に失敗し、黙って完全削除にしません。リンクを含む項目のごみ箱操作は引き続き未実装／安全性未検証のため、操作前に拒否します。完全削除へフォールバックしません。

新しい自動テストでは、実際のIFileOperationのPostDeleteItemからごみ箱のIShellItemを受領し、その物理Pathと内容を確認しました。元ファイルの消失だけでごみ箱成功と判断していません。ごみ箱のテスト項目と$Iメタデータは、内容と元Pathを照合して後始末。これは専用一時データによるAPI試験で、実キーや確認ダイアログのGUI試験ではありません。

仕様資料：[SetOperationFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)、[PostDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem)。

## リンク・コピー・更新

Shell経由のリンクコピー・切り取り・貼り付け・未知の拡張コマンドの制限を維持します。実行直前の検査で通常ディレクトリ配下のリンクも拒否。リンクターゲットの内容をコピーする代替処理は実装しません。相対ターゲットを保持するF5/F6の共通基盤は既存回帰で検証しました。

リンク保護付きコピー基盤の通常ファイル部分は、一意な送り先側ステージングファイルにコピーしてから、既存ファイルを置換しないMoveFileExで確定します。中断・失敗時は自分の一時ファイルだけ削除を試み、後始末も失敗した場合はPathと原因を報告します。ネイティブの進捗コールバックでキャンセルを検査し、途中キャンセルの自動試験で残骸が消えることを確認。既存ファイル・完成済みファイルは削除しません。

競合UIには残課題があります。通常対象のWindows Shellの衝突UIは維持しますが、実GUIで上書き／スキップ／名前変更保存／以降すべて適用を確認できていません。リンクを含む共通基盤は同名衝突を安全に拒否する従来の制限が残り、3択・以降すべて適用は未対応です。Shell通常コピー・貼り付けの中断後始末も実GUI未検証です。全コピー経路を今回のステージング試験だけで合格にしません。

FileSystemWatcher・常時ポーリングは追加していません。操作の一括処理終了後にまとめて更新します。以前の「次のWindow.Activatedで追加再列挙」は削除し、アクティブ化ではFile Ageの表示だけ更新します。外部Shell処理が非同期で完了する場合、完了後の自動反映は保証せず、ユーザーのCtrl+Rが必要になる制約を記録します。

## 実GUI環境

診断プロセスはActiveConsoleと同じセッション1、WinSta0/Default。有効画面1、2560×1440。物理画面がダミーHDMI由来かどうかは未確認。WTSINFOEXのSessionFlags=0はWindows 11ではロック状態です。InputDesktopの名前を取得できたこととは別に扱います。GUIツール自身のセッションIDは公開されていないため、接続先の完全な同一性も未確定です。

メモ帳のアクティブ化を試し、対象を再取得して一度復旧を試しましたが同じエラー。ロックを確認した後はGUI入力を停止し、解除を依頼しています。OSの自動ロック・認証・電源・ディスプレイ設定や、インストール済み拡張の設定は変更していません。

読み取り専用のOpenClipboardもWin32 5で失敗し、実コピー／切り取りのテストはCLIPBRD_E_CANT_OPENで3件目のスキップ。元のクリップボードを保存できないため、変更前に中止しました。Explorer相互操作、実GUIリンク操作、外部拡張の実行は未検証です。

環境原ログ：`artifacts/shell-desktop-environment/run-environment/desktop-environment.json`。API定義：[WTSINFOEX_LEVEL1_W](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w)。

## 独立した受入判定

|区分|判定|
|---|---|
|機能実装|基本接続・通常ごみ箱・キー・制限は実装／自動検証済み。リンクごみ箱とリンク衝突UIは未対応|
|実GUI互換性|ロック状態・アクティブ化失敗のため確認待ち。表示・入力・実行を未検証|
|ファイル操作の安全性|通常ごみ箱の実在、ジャンクションの安全拒否、コピー中断、既存共通基盤は自動検証。実シンボリックリンク2件・GUI・外部Shell操作は未検証|
|リソース寿命と性能|対象版のEventハンドル増加は観測範囲で改善。Private Bytes増加は未達。実ポップアップ・長期・実GUI両描画方式の性能は未測定|
|既存機能の回帰|両方式とも70件中67件合格、失敗0、3件スキップ。未検証の実操作へ結果を拡張しない|

再現：Releaseビルド後 `pwsh -NoProfile -File tools/Verify-LinkOperations.ps1 -Output docs/verification/shell-context/recheck`。性能は性能報告の別出力先の手順で実施。GUIは `docs/verification/shell-context/GUI_CHECKLIST.md` に従い、ロック解除後に再開します。
'''
(root/'SHELL_CONTEXT_IMPLEMENTATION_REPORT.md').write_text(implementation,encoding='utf-8')
files=[]
for directory in ['src','tests','tools']:
    files.extend(p for p in(root/directory).rglob('*') if p.is_file() and p.suffix in ['.cs','.csproj','.xaml','.ps1','.py'] and not any(part in ['bin','obj','TestResults'] for part in p.relative_to(root).parts))
files.extend(p for p in(root/'src/PaneCommander.App/bin/Release/net10.0-windows').iterdir() if p.is_file() and p.suffix in ['.exe','.dll','.json'])
manifest=[dict(Path=str(p.relative_to(root)),SHA256=sha(p))for p in files]
(out/'final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False))
