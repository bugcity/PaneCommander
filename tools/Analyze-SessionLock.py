from pathlib import Path
from datetime import datetime,timezone,timedelta
import json,re
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/session-lock/20261014'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
events=read(out/'events.json')
power=(out/'power-settings.txt').read_text(encoding='utf-8-sig')
values={}
for alias in ['CONSOLELOCK','LIDACTION','STANDBYIDLE','VIDEOIDLE','UNATTENDSLEEP','VIDEOCONLOCK']:
    match=re.search(r'GUID エイリアス: '+alias+r'\s*\n(.*?)(?=\n\s*\n|\Z)',power,re.S)
    assert match,alias
    numbers=re.findall(r'現在の (AC|DC) 電源設定のインデックス:\s*(0x[0-9a-fA-F]+)',match[1])
    values[alias]={key:int(value,16)for key,value in numbers}
timeline=[]
for event in events:
    if event['Provider']=='Microsoft-Windows-Kernel-Power'and event['Id']in [506,507,566]:
        stamp=datetime.fromisoformat(event['Time'].replace('Z','+00:00')).astimezone(timezone(timedelta(hours=9)))
        timeline.append(dict(JST=stamp.isoformat(),Id=event['Id'],RecordId=event['RecordId'],Message=event['Message'],Data=event['Data']))
summary=dict(Power=values,Timeline=timeline,Runtime=read(out/'runtime-settings.json'),Conclusion='Display idle/Modern Standby transition is the leading explanation; initiating lock caller is unproven',SettingsChanged=False,SecurityLockEventsFound=False,AuditPolicyQuery='Insufficient privilege 0x522; audit enablement is unknown')
(out/'diagnosis-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
report='''# Windows再ロックの原因調査

2026-10-14（日本時間）。設定・イベントログの読み取り専用調査。製品コード、OSのロック／電源／ディスプレイ／監査設定は変更していない。

**有力なのは無操作時の画面消灯／Modern Standbyとサインイン要求の経路。画面オフへの移行原因はログでIdle Timeoutと確認できたが、ロックを要求した主体までは未特定。**

## 時系列

|日本時間|確認した事実|
|---|---|
|20:02:09|前回GUI診断はWTS SessionFlags=1、ロック解除状態|
|20:05〜20:24|メモ帳・SoftwareOnlyの実マウス／キー操作に成功|
|20:29:30|専用Defaultプロセスを起動|
|20:34:33.209|Kernel-Power 506（RecordId12179）：理由Idle TimeoutでModern Standbyへ移行。LidOpenState=false、ExternalMonitorConnectedState=false。隣接566はPowerStateAc=true|
|20:34:33.498〜.501|Winlogon 811/812：Sens/TermSrvへ通知Event=4。通知値の意味はこの報告では推測で確定しない|
|21:04:02.725|Kernel-Power 507（RecordId12187）：Input KeyboardでModern Standbyのセッションが終了。SleepEntered=false、SleepDurationInUs=0、ScreenOffDurationInUs約1769.52秒|
|21:04:22|GUI再診断はWTS SessionFlags=0、ロック状態。有効画面は1台2560×1440。アクティブ化は再取得・一度再試行後も失敗|
|21:04:54.917〜.918|566はMessageにSessionUnlockを記録、同時刻の506はIdle Timeoutで画面オフ／Modern Standbyへの移行を記録。これはWTSの21:04:22時点とは別の時刻。現在の解除を保証する証拠にはしない|

507のSleepEntered=falseを踏まえると、約29分のModern Standbyセッションを「PCが29分間完全にスリープしていた」とは解釈できない。画面オフ区間とSleep区間は別である。[MicrosoftのModern Standby状態定義](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-states)。

## 採取時の実効設定と候補

|項目|AC|DC|読み取り結果の意味|
|---|---:|---:|---|
|VIDEOIDLE：ディスプレイ消灯|300秒|180秒|ACでも5分で画面を消灯する設定が残る|
|STANDBYIDLE：スリープ|0秒|180秒|ACの通常スリープはなし。画面消灯とは別|
|CONSOLELOCK：復帰時パスワード|1|1|両方とも有効|
|LIDACTION：ふたを閉じた操作|0|2|ACは何もしない、DCは休止状態|
|UNATTENDSLEEP：無人スリープ|120秒|120秒|存在するが、今回このタイマーが発火した直接証拠はない|
|VIDEOCONLOCK：ロック後消灯|30秒|30秒|ロック後の消灯設定。これ自体をロックを起こす原因とは扱わない|

InactivityTimeoutSecs=0は現在の「対話型ログオン：コンピューターの非アクティブ状態の制限」が無効であることと整合する。これは画面消灯や復帰時パスワード要求の設定を同時に無効にする値ではない。[Microsoftの非アクティブ制限](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/interactive-logon-machine-inactivity-limit)、[画面とスリープのタイマー](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/display--sleep--and-hibernate-idle-timers)。

powercfg /aではS0低電力アイドルに対応し、S3非対応。20:34のイベントはAC電源・閉じたふた・外部モニターなしを明記し、理由はLidやPowerButtonではなくIdle Timeout。したがって、ふたを閉じた瞬間の直接スリープより、無操作タイマーの経路を優先して調べる根拠がある。

21:27のSystemParametersInfo読み取りではScreenSaverActive=0、Secure=0、Timeout=0、Running=0。HKCUレジストリのScreenSaveActive=1と一致していないため、レジストリ値だけでスクリーンセーバーが今回の原因とは断定しない。BlockSendInputResets=0で、採取時にはSendInputによるアイドルリセットを禁止する設定は検出しなかった。これらは採取時の状態で、20:34時点の設定履歴を復元するものではない。[SystemParametersInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)。

## 特定できた範囲と限界

- 画面オフ／Modern Standbyに入った理由Idle Timeoutはイベントに明記されている。
- その時刻にWinlogon通知もあり、後のWTSではロック中だった。電源管理経路は有力だが、画面消灯がロックを起こしたのか、先行する別のロック要求に伴って消灯したのかは、4800などの直接監査イベントなしに確定しない。
- Securityの4800/4801/4802/4803/4778/4779は対象時刻で該当なし。auditpolの読み取りは権限不足0x522で失敗したため、監査が無効なのかも未確定。ログがないことを「ロックしていない」証拠にはしない。
- TerminalServicesの対象ログでは、GUI停止を説明する同時刻の切断イベントは取得していない。接続切断の不存在を完全に証明したとは扱わない。
- LockWorkStationを直接呼ぶスケジュールタスクは今回の検索では検出しなかった。全てのロック要求経路を調査したわけではない。
- PaneCommander製品ソースにはLockWorkStation／SetSuspendStateの呼出しは検出されなかった。従来の描画性能モニターには一時的な消灯・スリープ抑止があるが、GUI再開試験では使用していない。Default描画がWindowsロックを要求したという証拠はない。
- 現在の設定は21:24〜21:27に採取したもの。20:34の値が完全に同一だったことは未証明。

## 次の確認

まずユーザーがAC電源時の「画面をオフにする：5分」が意図した設定か確認するのが妥当。GUI検証中だけ画面消灯を防ぐ対照試験を行い、同じアイドル時間で再ロックの有無を比較すると原因を絞り込める。設定変更や一時的な電源要求は今回実施していない。パスワード要求を解除する必要があるとはまだ判断しない。

直接のロック主体まで必要なら、管理権限で4800監査の現在状態を読み取り、必要な追跡方法を検討する。監査有効化や再現のためのロックはユーザーの承認なしに行わない。ダミーHDMIは画面認識の対照には使えるが、ロックを防ぐ保証はなく、今回のログだけで必須とはしない。

原ログ・再現スクリプト：docs/verification/session-lock/20261014、tools/Diagnose-SessionLock.ps1、tools/Read-LockRuntimeSettings.ps1、tools/Analyze-SessionLock.py。ロック原因調査として製品の変更やGUI入力は行っていない。
'''
# Verify quoted event identity instead of relying on a manually transcribed record id.
first=next(e for e in timeline if e['Id']==506 and e['JST'].startswith('2026-10-14T20:34:33'))
report=report.replace('RecordId12179','RecordId'+str(first['RecordId']))
(root/'SESSION_LOCK_DIAGNOSIS_REPORT.md').write_text(report,encoding='utf-8')
print(json.dumps({'Power':values,'FirstIdleEvent':first,'Conclusion':summary['Conclusion']},ensure_ascii=False))
