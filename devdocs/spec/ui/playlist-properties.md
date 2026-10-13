# プレイリストのプロパティと一括編集

## 目的と適用範囲

プレイリストの設定画面、サマリーからの一括編集、BMT出力順の操作を定めます。正本の保存・失敗時の復元はプレイリスト保存仕様に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。画面操作の自動化は、Windows UI Automationの `Selection`（選択領域）と `SelectionItem`（選択項目）の操作契約に対応します。

## 仕様

### 単一プレイリストの編集

保存・一括編集・順序変更の受付とdraft現行性は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。新規作成の取消は未登録draftを閉じるだけで、正本の仮登録・削除を行いません。


所有者付きのモーダル画面を開き、「全般」「フォルダ」「カスタムフォルダ」を表示します。毎回「全般」から始め、前回のカテゴリを保存しません。共通の確定・取消ボタンはカテゴリの外側に置きます。

| カテゴリ | 編集対象 |
| --- | --- |
| 全般 | 名前、シンボル、互換用接頭辞、エントリ種別、外部同期の有効状態と3種類のURL。 |
| フォルダ | ソートキー・昇降順、拡張したフォルダ順、自動整列と手動の上下移動・自然順。 |
| カスタムフォルダ | LR2出力先、13種類のフォルダの出力設定と説明。 |

カテゴリはUI Automationの選択操作に対応します。選択を下線で示し、フォーカス・無効状態を区別します。左右・Home・Endで移動し、無効な項目は飛ばします。本文は独立したスクロール領域とし、カテゴリ変更時は先頭へ戻しますが、編集値は保持します。

フォルダ一覧は利用可能な高さに伸ばし、収まらなければその一覧内をスクロールします。説明を含む他の内容も画面の縮小時に到達可能にします。出力先は幅いっぱい、出力項目は上寄せの等幅2列とします。説明文を折り返し、横スクロールを要求しません。

カスタムフォルダはLR2用です。エントリがフォルダ単位ならレベル別の出力を無効にし、フォルダ内のソート候補からレベル順と追加日時順を除きます。外部同期の設定に応じてURLやフォルダの編集可否を切り替え、自動整列中は手動整列を無効にします。編集欄は双方向、管理主体から受け取る一覧は一方向に接続します。

画面右下の更新表示は `Update: yyyy/MM/dd` とします。通常の閉じる操作は一回の復元と完了通知を通し、閉じる要求を再発行しません。不正な入力や処理失敗がある場合は画面を閉じず、親画面と同じ調整処理で所有者を解決して通知します。保存操作のTaskは保存・復元、通知の試行、実閉鎖と再有効化まで待ちます。通知失敗はその場で診断し、保存結果へ合成したり、次の操作までTaskへ保持したりしません。実閉鎖や再有効化の失敗は画面寿命の失敗として返し、再有効化に失敗しても操作中の受付フラグは解除します。保存前の確認が失敗した場合は承認・利用者取消しに置き換えず、対象の変更を止めます。

保存サービスは一つの保存結果に確定参照、最新の保存値、未完了の必要出力と今回の実処理失敗を返します。画面はそれらを採用してから失敗を通知します。同時編集の判定は最新保存値、現行参照、項目の版に基づき、同じ保存の確定を古い編集スナップショットとの差として拒否しません。新規作成中の表は作成経路と実登録結果で判定し、旧参照が集合から外れたことを新規作成に読み替えません。

外部再取得でDB・正本が確定した場合も、その保存で必要になった旧出力先の整理とBMT出力を保持します。同じ属性を重ねてDB保存せず、後続の実出力処理が失敗しても次の明示保存へ渡します。参照に依存するフォルダ選択変換と項目変更通知は、新参照を採用してから行います。

A→BのDB保存後に出力だけ失敗した場合、同じ画面の次の明示保存は未完了のA→Bを先に完了します。さらにCへ編集していても、その成功後だけCをDBへ保存します。継続中の失敗ではDBはBのまま、画面のCを保持します。復元・取消の基準はBです。未完了は編集セッション内の一つに限り、成功した出力と公開済みスナップショットを解放します。継続に必要な保存値・参照結果記録・出力ごとのフラグを保持し、過去の失敗例外は次の失敗へ加えません。索引・現行参照の必要な公開が失敗した場合は、その未完了を残します。画面終了・再起動を跨ぐ台帳、自動再試行、旧保存全体の再実行は行いません。

### サマリーからの一括編集

選択したプレイリストを捕捉して対象を固定し、各操作のボタンで反映します。画面全体のOKでまとめて確定する方式ではありません。URLとフォルダ内の順序を一律に上書きする機能は設けません。

| 操作 | 反映規則 |
| --- | --- |
| 出力フラグ | チェックは有効、未チェックは無効、中間状態は変更なし。フォルダ単位エントリへのレベル出力は禁止。 |
| 出力先変更 | ルート出力中でも保存された出力先名を更新する。有効な出力先が変わるかどうかとは区別する。 |
| 外部同期を有効にする | `EnableExternalSync` の実際の成否を用い、不正なURLなどで有効にできない項目は飛ばして通知する。 |
| 外部情報で初期化 | 指定した名前・シンボル・互換用接頭辞・出力先を、外部同期の有効状態にかかわらず取得情報から反映する。 |

外部情報の取得は上限付きの並列処理で先に行い、読めない対象は変更せず飛ばします。初期化では既存の接頭辞を保存する規則を使わず、選択された項目を外部値から作ります。出力先がNULLなら名前から作り、Shift_JISで扱えない文字とファイル名の不正文字を除きます。接頭辞の衝突、LR2出力先の空値・重複は対象ごとに除外します。

エントリの投影が変わる複数対象は、一回のDBトランザクションで保存します。出力先が変わる場合は移転元の不要な出力も処理し、同じ出力先の内容変更もまとめて出力します。取得、DB反映、出力、画面更新を一つの進捗範囲とし、最後にボタンを再び使用可能にします。BMT内容に影響する操作は、有限の変更表をまとめて一回の出力Taskを直接待ちます。LR2専用設定だけの変更ではBMTを再生成しません。

各反映ボタンは保存と必要出力の実Taskを所有し、受付拒否・保存・出力の失敗を画面へ通知します。実終端より前にdraftをリセットしたり、反映完了としてボタンを再有効化したりしません。失敗時は編集入力を保持し、成功時だけ反映後のdraftへ更新します。保存前の失敗は、その保存対象の正本を変更前へ戻し、同じdraftを再反映して保存できます。保存済み対象は後段の出力や別の保存対象の失敗で戻しません。

### BMT出力順の操作

サマリーのドラッグによる並べ替えはBMT出力順の昇順表示でのみ受け付けます。絞込みで隠れた項目の相対順を維持します。先頭・末尾への移動、現在の表示順の採用は、選択対象と現在の並びを明示して適用し、保存とBMT出力の必要性を判定します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 単一画面の編集、取消、保存後の更新 | [`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | [`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs) |
| 実folder編集controlから保存・LR2出力順への接続 | `PlaylistPropertyDialog`、`PlaylistPropertyDialogViewModel`、`PlaylistPropertySaveService` | [`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyFolderControls_SaveDraftOrderToDatabaseAndRealLr2Output`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) は実Up・Down・NaturalSortのdraftを独立順と比較する。共通Save後は新readonly DBの手動保存列、またはheaderと実entryから再構成した自然順、実LR2採番順・folder行を確認し、新propertyのAutoオンと上下移動無効化も観測する。未保存正本・未編集属性・別表の値と出力を保ち、所有モーダル・保存Task・native閉鎖を待つ。出力種別の条件表は既存custom-folder系列へ分担する。 |
| 保存後の未完了出力先行、Cの保持、最新保存基準と外部取得・接頭辞非再実行 | `PlaylistPropertySaveService`、`PlaylistPropertyDialogViewModel` | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)の`PlaylistPropertyDialog_*`。実DB・FS・config・BMTとP/Task終端を下位から確認する。 |
| 外部再取得確定後の追加DB失敗とA→B継続、参照採用後の接頭辞選択・詳細更新 | `PlaylistPropertySaveService`、`PlaylistWorkspaceViewModel.PropertyEditing` | [`BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_ExternalReloadCommitRetainsOutputMigrationAfterLaterDatabaseFailure`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)で実置換後の移行DB故障と、元JSONを外した明示再保存を確認する。[`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_ExternalReplacementRemapsSelectedFolderAndRefreshesDetail`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)で実workspaceの通知時の新参照・選択・ツリー・実詳細Folder列・DBを確認する。 |
| 同じ所有画面の実保存ボタン、外部置換後BMT失敗の通知・維持・再保存・`Close` | [`PlaylistPropertyDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.cs)、[`PlaylistWorkspaceViewModel.PropertyEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PropertyEditing.cs) | [`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyDialog_ExternalReplacementBmtFailureKeepsOwnedDialogAndResavesSameId`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)。実データ連結、通知の所有画面、通知二次失敗後も終端するTaskと画面維持、次の明示保存成功後の旧失敗非混入、同IDの保存行と実出力を確認する。 |
| 選択集合、三状態フラグ、外部初期化、出力の集約 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) |
| 表示と更新日時、件数 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryCountAndPresentationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryCountAndPresentationTests.cs) |
| カテゴリ操作と所有者付き画面 | [プロパティ画面](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.xaml) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) |
| 一括変更の実Task所有、Busy・DB失敗・BMT出力待ちと出力失敗 | [`PlaylistSummaryBulkEditDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistSummaryBulkEditDialog.cs) | [`MainWindowPlaylistWorkspaceWpfTests.MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)のcompiled反映入口・入力保持・終端後の再有効化 |
| 一括変更の保存前復元と同じ入力の再保存 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryBulkEditTests.BulkHeaderSaveFailure_RestoresCurrentTableAndSameDraftCanSave`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs)の5入口・実DB失敗・同一正本保持と再保存 |

## 関連資料

[保存と正本](../playlist/storage-and-export.md)、[LR2出力](../playlist/lr2-custom-folders.md)、[BMT出力](../playlist/bmt-export.md)、[共通外観](appearance.md)。
