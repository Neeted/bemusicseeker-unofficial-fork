# プレイリストのプロパティと一括編集

## 目的と適用範囲

プレイリストの設定画面、サマリーからの一括編集、BMT出力順の操作を定めます。正本の保存・失敗時の復元はプレイリスト保存仕様に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。画面操作の自動化は、Windows UI Automationの `Selection`（選択領域）と `SelectionItem`（選択項目）の操作契約に対応します。

## 仕様

### 単一プレイリストの編集

所有者付きのモーダル画面を開き、「全般」「フォルダ」「カスタムフォルダ」を表示します。毎回「全般」から始め、前回のカテゴリを保存しません。共通の確定・取消ボタンはカテゴリの外側に置きます。

| カテゴリ | 編集対象 |
| --- | --- |
| 全般 | 名前、シンボル、互換用接頭辞、エントリ種別、外部同期の有効状態と3種類のURL。 |
| フォルダ | ソートキー・昇降順、拡張したフォルダ順、自動整列と手動の上下移動・自然順。 |
| カスタムフォルダ | LR2出力先、13種類のフォルダの出力設定と説明。 |

カテゴリはUI Automationの選択操作に対応します。選択を下線で示し、フォーカス・無効状態を区別します。左右・Home・Endで移動し、無効な項目は飛ばします。本文は独立したスクロール領域とし、カテゴリ変更時は先頭へ戻しますが、編集値は保持します。

フォルダ一覧は利用可能な高さに伸ばし、収まらなければその一覧内をスクロールします。説明を含む他の内容も画面の縮小時に到達可能にします。出力先は幅いっぱい、出力項目は上寄せの等幅2列とします。説明文を折り返し、横スクロールを要求しません。

カスタムフォルダはLR2用です。エントリがフォルダ単位ならレベル別の出力を無効にし、フォルダ内のソート候補からレベル順と追加日時順を除きます。外部同期の設定に応じてURLやフォルダの編集可否を切り替え、自動整列中は手動整列を無効にします。編集欄は双方向、管理主体から受け取る一覧は一方向に接続します。

画面右下の更新表示は `Update: yyyy/MM/dd` とします。通常の閉じる操作は一回の復元と完了通知を通し、閉じる要求を再発行しません。不正な入力や処理失敗がある場合は画面を閉じず、共通のダイアログ境界で通知します。

### サマリーからの一括編集

選択したプレイリストを捕捉して対象を固定し、各操作のボタンで反映します。画面全体のOKでまとめて確定する方式ではありません。URLとフォルダ内の順序を一律に上書きする機能は設けません。

| 操作 | 反映規則 |
| --- | --- |
| 出力フラグ | チェックは有効、未チェックは無効、中間状態は変更なし。フォルダ単位エントリへのレベル出力は禁止。 |
| 出力先変更 | ルート出力中でも保存された出力先名を更新する。有効な出力先が変わるかどうかとは区別する。 |
| 外部同期を有効にする | `EnableExternalSync` の実際の成否を用い、不正なURLなどで有効にできない項目は飛ばして通知する。 |
| 外部情報で初期化 | 指定した名前・シンボル・互換用接頭辞・出力先を、外部同期の有効状態にかかわらず取得情報から反映する。 |

外部情報の取得は上限付きの並列処理で先に行い、読めない対象は変更せず飛ばします。初期化では既存の接頭辞を保存する規則を使わず、選択された項目を外部値から作ります。出力先がNULLなら名前から作り、Shift_JISで扱えない文字とファイル名の不正文字を除きます。接頭辞の衝突、LR2出力先の空値・重複は対象ごとに除外します。

エントリの投影が変わる複数対象は、一回のDBトランザクションで保存します。出力先が変わる場合は移転元の不要な出力も処理し、同じ出力先の内容変更もまとめて出力します。取得、DB反映、出力、画面更新を一つの進捗範囲とし、最後にボタンを再び使用可能にします。BMT内容に影響する操作だけが、末尾で一回BMT出力を予約します。LR2専用設定だけの変更では予約しません。

### BMT出力順の操作

サマリーのドラッグによる並べ替えはBMT出力順の昇順表示でのみ受け付けます。絞込みで隠れた項目の相対順を維持します。先頭・末尾への移動、現在の表示順の採用は、選択対象と現在の並びを明示して適用し、保存とBMT出力の必要性を判定します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 単一画面の編集、取消、保存後の更新 | [`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | [`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs) |
| 選択集合、三状態フラグ、外部初期化、出力の集約 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) |
| 表示と更新日時、件数 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryCountAndPresentationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryCountAndPresentationTests.cs) |
| カテゴリ操作と所有者付き画面 | [プロパティ画面](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.xaml) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) |

## 関連資料

[保存と正本](../playlist/storage-and-export.md)、[LR2出力](../playlist/lr2-custom-folders.md)、[BMT出力](../playlist/bmt-export.md)、[共通外観](appearance.md)。
