# プレイリストの保存と更新

## 目的と適用範囲

プレイリストの正本、外部同期の変更判定、手動編集、バックアップと復元を定めます。LR2向け・beatoraja向けの出力は別の仕様で定め、DB保存の成否と区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 正本と識別

正本は本アプリの `playlist`、`playlist_entry`、`playlist_course` です。共通の `app_schema=1` に含まれ、プレイリスト専用のスキーマ版は持ちません。LR2連携・スタンドアロンの両方で保存します。

| 表 | 主な列と意味 |
| --- | --- |
| `playlist` | `playlist_id`、`name`、`symbol`、`tag`、`page_url`、`header_url`、`data_url`、比較用の `header_sha256` と `data_sha256`、互換フォルダ名の `compat_prefix`、更新日時 `last_update` を保持する。 |
| `playlist` の表示・出力設定 | `output_dir`、`custom_folder_output_base_name`、`is_root_folder`、`ignore_folder_output`、`folder_order`、`folder_sort_key`、`folder_sort_ascending`、`is_external_sync`、`bmt_sort`、`is_bmt_output`。 |
| `playlist_entry` | 所属ID、`md5`、`sha256`、`title`、`artist`、`folder`、`level`、`url`、`url_diff`、`name_diff`、`org_md5` と、ローカル状態の `memo`、`adddate`、`is_removed`。 |
| `playlist_course` | `course_id`、所属ID、元の順序 `course_order`、正規化した `course_json`。 |

譜面の解決・参照・所持集計はMD5があればMD5を選び、ない場合だけSHA-256を選びます。MD5不一致の後で同じ行のSHA-256へ探索を広げません。手動追加では所持譜面から分かる両ハッシュを保存し、外部同期では取得元の値を保存します。手動追加の `org_md5` は同じディレクトリ内の所持BMS/BMSONのMD5集合です。

コースはヘッダー由来であり、入れ子配列もコースオブジェクトへ平坦化します。別のコースハッシュは保存せず、ヘッダーハッシュで更新を判定します。DB内部のコース改ざん検出を目的としません。

### 互換フォルダ名

新規取得と明示的な外部データ初期化では、ヘッダーに `compat_prefix` があれば空文字・CP932範囲外の文字も含めその値を保存します。なければ `tag`、次に `symbol` を候補とし、選んだ値をCP932で厳密に表せる場合だけ採用します。表せなければ文字を削ったり次候補へ切り替えたりせず `LEVEL ` とします。候補がない場合も同じです。レベル値が数字か記号かでは決めません。

明示の接頭辞がないヘッダーの `folder_order` は、読込み済みのフォルダ名と一致する値をそのまま保持し、それ以外を互換レベル名として接頭辞付きへ変換します。保存時は接頭辞をヘッダーへ明記します。通常の外部同期では、取得元の明示値よりDBの保存済み接頭辞を優先し、勝手に推定し直しません。

接頭辞の変更は各フォルダ名の先頭にある旧接頭辞だけを除き、新接頭辞を付けます。旧接頭辞がなければ元の名前全体へ付けます。既に新接頭辞で始まる名前も特別扱いしません。変換途中でなく最終名の重複を検証し、重複なら既存構成を保持して失敗とします。

この変更で譜面のフォルダ名が変わる場合は同じ保存単位で保存し、詳細表示の版を進めます。ツリーとFOLDER列、選択中のフォルダキーを同じ対応で更新します。接頭辞だけの変更では外部表を再取得しません。

### 既存DBの正規化

不足する `bmt_sort`、`is_bmt_output`、`custom_folder_output_base_name` は列の追加で対応し、専用の表再構築を行いません。出力先名のNULLは既定の通常出力先、BMT出力可否のNULLはtrueとして正規化します。`bmt_sort` は既存の有効値を優先し、名前・IDで同順位を解消して1始まりの一意な順序へ揃えます。

通常の外部更新ではこの3設定を保持します。新規表は最大の順序+1、BMT出力true、通常出力先名NULLです。出力除外ビットの既存値は維持し、新しく増えた種類まで除外するよう拡張しません。

### 外部取得と公開の境界

HTMLはアプリのHTTP管理主体が取得した本文だけを解析し、パーサーに再取得や外部DTD取得をさせません。ページ、ヘッダーJSON、データJSONは各一回取得します。相対ヘッダーURLはページ、相対データURLはヘッダーを基準に解決します。

取得と解析はUIスレッド外で行います。集合の書込みロックは重複確認・採番・出力先算出など短い状態変更に限定し、DB保存はその外で行います。確定後の表示集合への追加・置換はUIスケジューラーへ渡し、その処理の `Completion` を待って成功とします。UI完了を待つ間は表・集合・UIのロックを保持しません。

置換の受付拒否、取消、中断、例外は再読込み失敗です。独自の時間切れで成功を推定したり、旧表示のまま成功扱いにしたりしません。再読込み予約は成功・失敗のどちらでも `finally` で解放します。

URL指定の複数行取込みは、取得を既存の上限内で並列化し、登録をまとめて行います。既存名と同じバッチの予約名に重複する表は改名せず省略し、完了時に件数と名前を通知します。ライブラリ参照、サマリー、URL補完は登録済み集合へ一括反映します。取得後の登録・参照・完了処理も進捗に含めます。

取得操作で受け付けるURIと、保存済み表の外部同期元は別契約です。取得操作は各遷移でHTTP(S)だけを許可します。保存済み外部同期元では相対・ローカル・ドライブ・`file:`・UNCを維持し、HTTP専用検証を流用しません。

### 更新検知と更新日時

ヘッダーハッシュは `compat_prefix` を除き、明示接頭辞付きの `folder_order` を比較用に正規化して計算します。`tag`、`course`、`level_order`、`symbol` 等は含みます。データハッシュは取得したデータJSON全体が対象です。

| 変化 | DB保存 | `last_update` |
| --- | --- | --- |
| 既知のヘッダーハッシュから別値へ変化 | ヘッダーとコース。譜面行は保存し直さない。 | 更新する。 |
| ヘッダーハッシュの初期化、または一致する旧形式からの変換 | ヘッダーとコース。 | 更新しない。 |
| 既知のデータハッシュから別値へ変化 | ヘッダー・コース・譜面行。 | 更新する。 |
| データハッシュの初期化 | ヘッダー・コース・譜面行。 | 更新しない。 |
| 譜面行の内容比較だけで差分を検出 | 一致行のローカル状態を引き継いで譜面行を保存する。 | 更新しない。 |

いずれもBMT再出力の対象です。内容比較はハッシュ未初期化や古い状態でも譜面差分を反映するためのもので、更新日時の独立した根拠にはしません。外部表の初回登録は取得元の日時があればそれを使い、なければ登録時刻です。空のローカル表は作成時刻を初期値とし、通常保存でDBへ反映します。

フォルダ作成・改名・譜面追加・削除など構成の手動編集では編集時刻へ更新します。名前、記号、URL、出力先、整列・順序、接頭辞だけの保存では更新しません。接頭辞に伴う表示名の変換も譜面所属の変更ではありません。

### 手動編集と失敗

フォルダ作成・改名・削除、譜面削除、ドロップを一つの編集として扱います。DB保存前の失敗では、同じ表・譜面オブジェクトを維持しながら集合、編集可能な値、フォルダ順、更新日時、譜面の版を開始時へ戻します。全表の交換や無条件の再読込み、永続的な取消履歴・再試行は使いません。複数表の削除は表ごとの既存保存単位を維持し、成功済みの別表を戻しません。

永続化成功後だけ詳細表示の同期、成功通知、`afterApply` を進めます。サマリーの一括変更も同様で、失敗後の索引・検索候補・整列・表示更新やダイアログ終了を成功経路で実行しません。モデルの途中失敗でプロパティ通知を公開せず、復元後の現在状態を通常の通知処理へ渡します。

必要な通知と後片付けが終わるまで編集の論理的な受付を保持し、その間の新規編集は待たずに使用中として拒否します。既に受理された遅延同期だけは、既存の集約キュー内で編集終端を待てます。購読先の実行前に集合・表・DBのロックを解放します。

DB確定後のLR2/BMT出力失敗で、保存済みの表や表示を巻き戻しません。対象と元の原因を警告・エラーへ残し、現在の参照と表示は確定した内容へ揃えます。非同期BMT出力には独立した既存の完了処理があり、元編集の受付を保持し続けません。

### フォルダ単位のドロップ

入力順を保ったディレクトリ単位の計画を作り、計画中は表示用の表を変更しません。既存と先に計画したフォルダ・譜面を作業集合に持ち、`GetPlaylistFolderOrgMd5sForCharts` のパッケージMD5集合とフォルダ内MD5の重なりで分類します。削除済み譜面しかないフォルダは候補から外しますが、その履歴は保持します。

複数候補は実一覧と同じフォルダ順・自然順で選びます。名前の衝突は既存の接尾辞規則で避け、同じタイトルだけ、または空の親MD5集合だけでは統合しません。同じ入力順なら一括投入と分割投入で所属と重複除去後の集合が一致します。任意の入力順の同一視や既存手動分類の移行は行いません。

通常譜面、プレイリスト詳細、解決済みの `PlayHistoryRow.ResolvedChart` を入力にできます。未解決の履歴行を含む選択は部分追加しません。

### SQLバックアップと復元

バックアップは選択先と同じディレクトリの一時ファイルへUTF-8・BOMなしで書き、閉じた後に置換または移動で公開します。先に既存先を消したり、直接上書きへ切り替えたりしません。後片付け失敗も診断に残し、成功通知は公開後だけです。

復元はファイル読込み、DB復元、表示一覧への反映を分けます。DBのMonitor、トランザクション、確定後のヘッダー読込み、接続解放は同じワーカーの同期範囲で完結させます。UIへの一覧反映を予約する前にその範囲を抜け、予約の受付だけでなく完了を待ちます。譜面行は引き続き遅延読込みです。

復元予約は登録・一覧再読込み・個別再読込み・読込み結果公開の予約と競合する場合、DB変更前に拒否します。復元中は保存・削除・登録・再読込み・読込み結果公開を拒否し、UI反映の成功または失敗まで保持します。通常の再読込みの許可条件は変えず、購読通知そのものの全寿命まで公開予約を延長しません。

読込み・復元確定前の失敗は旧DBと一覧を保持します。確定後のヘッダー読込みやUI反映の失敗は復元済みDBを保持し、エラーを返します。UIの途中適用は補償しません。確定後の永続化世代と開始時の集合を適用へ渡し、自身の確定で古くなった世代を理由に誤って拒否しません。

復元後に開始した必須のプレイリスト準備は、出力先同期等が失敗した場合も同じ元例外で終端します。復元処理と準備を待つ外部取込みの両方へ失敗を返し、待機を残しません。準備開始前の失敗では既存の準備状態を変えません。失敗時に成功表示、追加出力、設定画面終了、アプリ終了の許可を出しません。

SQL読取りは `SQLITE_ROW` と `SQLITE_DONE` 以外を失敗とし、途中行をダンプ・復元・表示へ公開しません。読取り失敗を解放失敗より主原因にします。同期 `LoadPlaylistDump` は移行等のDB専用入口に限り、設定画面は非同期復元を使います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| スキーマ・既存設定・コース | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、[`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) |
| 外部取得・変更判定・表示の完了 | [`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | [`BmsPlaylistExternalLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs)、[`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) |
| DB失敗後の同一オブジェクト保持と次の正常編集 | [`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) |
| 実ドロップ・分類・参照・通知 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 復元の排他・DB待機・UI完了・準備失敗 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 見出し・項目の型と不正なJSONの失敗 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs) |
| 項目の題名の永続化 | [`LR2SongDB`](../../../BeMusicSeeker/Models/LR2/LR2SongDB.cs) | [`Lr2PlaylistEntryPersistenceTests`](../../../BeMusicSeeker.Tests/Playlist/Lr2PlaylistEntryPersistenceTests.cs) |
| 借用した読取りの解放、参照先の確定と操作の受付 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) |

## 関連資料

[LR2出力](lr2-custom-folders.md)、[BMT出力](bmt-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[データと索引](../core/data-and-indexes.md)を参照します。
