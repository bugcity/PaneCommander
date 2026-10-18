"""Record evidence from the completed screen-off-disabled GUI session; no UI input."""
from pathlib import Path
import hashlib,json
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/shell-context/no-screen-off'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
fixtures={'Default':'artifacts/shell-gui-resume/Default-8afbd299241b4a67af6e0d11e83ce7b9',
          'SoftwareOnly':'artifacts/shell-gui-resume/SoftwareOnly-e80699e6e3734a15a30b8efffba074dd'}
actions=read(out/'actions.json')
labels={a['label'] for a in actions}
counts={}
for kind in ['file','directory','background']:
    for i in range(1,6):
        assert f'software-{kind}-open-{i}' in labels
        assert f'software-{kind}-close-{i}' in labels
    counts[kind]=5
fixture=root/fixtures['SoftwareOnly']
permanent=fixture/'left/permanent.txt'
sentinel=fixture/'protected-target/sentinel.txt'
assert permanent.read_text()=='PaneCommander isolated GUI fixture: permanent.txt'
expected=read(fixture/'verification.json')['TargetHash']
actual=hashlib.sha256(sentinel.read_bytes()).hexdigest().upper()
assert actual==expected
proof={'CancelledPermanentDeleteFileExists':True,'CancelledFileSHA256':hashlib.sha256(permanent.read_bytes()).hexdigest(),
       'SentinelExpectedSHA256':expected,'SentinelActualSHA256':actual,'SentinelUnchanged':True,
       'Scope':'Cancellation only; this does not prove unexecuted link operations'}
(out/'filesystem-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
final=read(root/'artifacts/shell-gui-no-screen-off-final-environment/run-environment/desktop-environment.json')
assert final['WTSSessionFlags']==1 and final['ProcessSession']==final['ActiveConsoleSession']==1
runtime=read(out/'SoftwareOnly-resource/runtime-settings.json')
idle_ms=(runtime['LastInputInfo']['CurrentTick32']-runtime['LastInputInfo']['Tick32'])%(2**32)
for path in fixtures.values():assert read(root/path/'shutdown.json')['OwnedProcessStopped']
text=f'''AC画面消灯は0秒（なし）を読み取りで確認。DCは180秒のままであり、両方を「なし」とは報告しない。開始時・Default待機後・SoftwareOnly長時間待機後・全操作終了時のホスト診断はいずれもWTS SessionFlags=1（ロック解除）、セッション1のWinSta0/Default、有効画面1台2560×1440。自動ロック設定を無効化した事実だけから解除を推測していない。OS設定はユーザーが変更し、エージェントは変更していない。

メモ帳は既存の試験文書を一覧から再取得してアクティブ化・画面取得に成功。今回は新たな文字入力や保存はしていない。PaneCommander両方式もアクティブ化には成功し、前回の `failed to activate captured window` は今回再現していない。

SoftwareOnlyでは通常の製品画面、[DIR]、File Age背景色、アクティブ枠をキャプチャで確認。長時間待機後にも正常画面を取得し、実入力を再開できた。13:47:51 UTCのGetLastInputInfoから無入力期間は{idle_ms/60000:.2f}分、同時期のWTS診断は解除状態。これはその時点まで入力がなかったことと解除状態の観測であり、全期間の連続WTS監視ではない。リソース採取器は起動後30分で終了し、無入力部分は約28.14分だけ記録。残りの時間のメモリ・CPUを推定しない。

DefaultはタイトルバーとネイティブShellメニューを取得できる一方、WPF本体が白いキャプチャになった。新しいウィンドウ一覧から選び直して再試行しても同じ。ロック解除状態でも再現するため、前回のロックエラーとは別の未解決事項。実ディスプレイ上の描画異常かキャプチャ経路の問題かは未特定で、Windows／ドライバー／WPFのいずれかへ断定しない。白い領域への見えないマウス操作を避け、DefaultではShift+F10による背景メニュー1回とEsc終了の確認までに限定した。両方式の表示品質受入は保留。

修正済みビルドのSoftwareOnlyで、permanent.txt選択→Shift+Delete→対象名付き確認→「いいえ」を実操作。警告ダイアログを出さず中止ステータスへ戻り、元ファイルの内容を保持。キャンセル修正の実GUI再検証はこの経路で合格。番兵ハッシュも不変。ただし、これを未実施のリンク削除の証明にはしない。

SoftwareOnlyの実ポップアップ開閉はfile 5回、directory 5回、background 5回。右クリック、Shift+F10、Appsキーで表示し、各回Esc後の画面を確認、一覧へフォーカスが戻った。フォルダー選択と右側の背景対象でアクティブ枠が切り替わることも確認。外部拡張は表示までで実行未検証。100/500/1600回の受入用反復は未実施。この15回を専用STAドライバーの反復や長期安定性の合格へ転用しない。

各方式の先頭360秒（最後のサンプルとの間隔により実際は約359.8秒）を比較窓として切り出し、全待機区間と最後60秒は別集計。ウォームアップはDefault 82.76秒、SoftwareOnlyは明示warmup 97.79秒（それ以前にunmarked 12.29秒）で一致していない。各1プロセスであり、厳密な交互3反復の描画方式比較ではない。先頭360秒CPUはDefault 1.14847%、SoftwareOnly 0.44374%（24論理CPU全体に対する値）。この区間の0%近傍達成は未達として扱う。SoftwareOnlyの約28分全体は0.09459%、末尾約60秒は0%だが、それだけで恒常的な0%近傍や長期安定性を証明しない。

メニュー終了後の新しい無入力採取は54.38秒。60秒には達しておらず、60秒試験完了と報告しない。この区間のCPUは4.08042%で未達。操作中のUIA・キャプチャに由来する継続的な影響と製品の処理は未分離。製品のネイティブShell経路の漏れや常駐タイマーと断定せず、次はキャプチャ接続を終了した対照測定で切り分ける。

全操作終了時にも解除状態を確認し、専用プロファイルの試験用PaneCommander両プロセスを既存診断イベントで正常終了した。ユーザー文書、通常プロファイル、Shell拡張、OS設定を変更していない。製品コードはこの再試験では変更していない。'''
status=dict(Fixtures=fixtures,ResultText=text,MenuOpenCloseCounts={'SoftwareOnly':counts,'Default':{'background':1}},
            NoInputElapsedAtSoftwareHostCheckMs=idle_ms,FinalSessionUnlocked=True,
            Acceptance={'Function':'Partial: evidenced menu paths and delete cancellation passed',
                        'GuiCompatibility':'Pending: Default white capture; clipboard/Paste outstanding',
                        'Safety':'Cancellation and sentinel intact; unexecuted link/clipboard paths unverified',
                        'Performance':'Not accepted: idle CPU and controlled long repetition outstanding',
                        'Regression':'Previous SoftwareOnly clipboard failure preserved; no product change this phase'})
(out/'status.json').write_text(json.dumps(status,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'MenuCounts':counts,'NoInputMinutes':idle_ms/60000,'FinalUnlocked':True}))
