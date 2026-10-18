# 詳細一覧への一本化とWindows Shell Open/Edit

2026-10-17ユーザー確定仕様。requirements.mdの旧階層表示要求、旧OP-12未確定、旧DD-03候補より優先する。過去の仕様原文や測定・GUI証跡はその時点の履歴として保存する。

## 表示と保存

製品は詳細一覧のみ。MillerView/MillerViewModel、親/子プレビュー、表示方式切替、専用イベント・状態・専用試験を削除する。FolderViewとTabState/PaneStateの共有機能は維持する。

左右2ペイン、独立タブ、パス、親/戻る/進むと稼働中の履歴、お気に入り、フィルター、複数選択、列設定、File Age、ソート、ファイル操作、DnDを維持する。履歴の再起動永続化は従来どおり対象外であり、新たに追加しない。

保存形式はVersion=1を継続。旧session.jsonの各タブにある`ColumnsMode`はSystem.Text.Jsonの未知フィールドとして無視する。TabSnapshotから当該プロパティを除き、次回正常保存ではPath/SortProperty/SortDescendingだけを出力する。タブ順・選択index・ActiveSide・Layoutは保持する。settings.jsonの左右列設定とfavorites.jsonは独立して保持する。未知の旧項目があってもそれだけを理由に初期化しない。破損JSON・無効Pathの既存失敗通知は維持する。

## Open

両入力はPaneViewModel.Open → TabState.OpenAsyncを使用する。Enterは選択済みのフォーカス行、行にフォーカスがない/未選択の場合は主選択1件。選択なしでは何もしない。複数選択でも一括起動しない。左ダブルクリックは実際にクリックした行を明示的に渡す。空白・ヘッダー・右ボタンでは起動しない。

ディレクトリはTabState.NavigateAsyncでアプリ内へ移動。ファイルは既存FileSystemService.OpenFileAsyncでローカルパス/リンク検査・存在検査後、ProcessStartInfo(FileName=対象Path, UseShellExecute=true)を使用する。Verbを固定せずShellの既定動作に従い、Arguments/ArgumentListは空。EXE/PS1/BAT/CMDを含め拡張子分岐は設けず、PowerShell直接呼出し/ポリシー変更もしない。Shell起動失敗は既存のタブ状態欄とログへ通知し、UIへ未処理例外を伝播しない。

既存のディレクトリリンクは安全なローカル参照先へアプリ内移動。ファイルリンク・ショートカットは従来のローカル検査とShellの責務を維持する。リンクのコピー/移動/削除/外部DnDの制限は緩和しない。

## F4 Edit

MainViewModelは実際の選択Path配列が1件で、現在の一覧内の通常ファイルに一致する場合のみ有効にする。対象なし・複数・ディレクトリ・再解析ポイント・一覧にない古いPathは無効。実行時も再判定する。

IShellEditServiceの実装は既存ShellContextMenuをSTAで短期間だけ使用する。ファイルの実属性/ローカルPathを再検査し、既存ShellMenuSafety.CheckInvocation("edit")を通す。Commandsからcanonical verb `edit`の有効な項目を選び、同じIContextMenu.InvokeCommand経路で実行する。パスからコマンド文字列や引数を組み立てないため、空白・日本語・記号を含むPathもShellのPIDLで渡す。Windows Shellの正式な[InvokeCommand](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-icontextmenu-invokecommand)と[canonical verb](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-implement-the-icontextmenu-interface)に基づく。

Edit項目なし/無効は利用不可としてメイン状態欄へ通知。例外も通知・ログへ収め、Openへ代替しない。外部アプリを強制的に背面へ戻したり、編集完了/保存完了を推測したりしない。独自エディター、外部エディターパス、拡張子別設定、ファイル本文の変更は追加しない。

## DnDと範囲

FileDragBehavior/DropPolicyの既存宛先決定を維持し、受信ペインの選択タブの現在ディレクトリへコピー/移動する。行がEXE/ファイル/ディレクトリでも宛先を変更しない。Shell Open/Editは明示入力だけの別経路。DD-03は対象UI廃止により要求対象外。F1/F2/F9は未計画、キーバインド編集は初版非必須のまま。

実装・GUI/自動証跡と未達事項は[今回報告](../verification/detail-shell-20261017/REPORT.md)。Default P0とメモリ調査の保留を維持する。
