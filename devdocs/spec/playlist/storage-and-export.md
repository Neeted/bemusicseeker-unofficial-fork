# プレイリストの保存と更新

## 目的と適用範囲

プレイリストの正本、外部同期の変更判定、手動編集、バックアップと復元を定めます。LR2向け・beatoraja向けの出力は別の仕様で定め、DB保存の成否と区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 正本と識別

正本は本アプリの `playlist`、`playlist_entry`、`playlist_course` です。共通の `app_schema=1` に含まれ、プレイリスト専用のスキーマ版は持ちません。LR2連携・スタンドアロンの両方で保存します。

JSON解析、内蔵推薦・難度推定、DB読込みで完成した行集合は、共通の `BMSTable.entries` setterで一度取り込みます。入力集合を防御複写し、行の親、正規化、フォルダ派生状態、読込状態とリビジョンを揃えます。所属IDは親表の現在の採番値から取得します。公開一覧は構成を変更できない読取り専用ビューとし、行の参照と編集可能性は保持します。このビューは不変スナップショットの代用にせず、独立した集合が必要な捕捉では従来どおり複写します。手動追加・削除・改名と補償復元は既存の局所操作を使い、全件の再取込み・再正規化へ変更しません。

| 表 | 主な列と意味 |
| --- | --- |
| `playlist` | `playlist_id`、`name`、`symbol`、`tag`、`page_url`、`header_url`、`data_url`、比較用の `header_sha256` と `data_sha256`、互換フォルダ名の `compat_prefix`、更新日時 `last_update` を保持する。 |
| `playlist` の表示・出力設定 | `output_dir`、`custom_folder_output_base_name`、`is_root_folder`、`ignore_folder_output`、`folder_order`、`folder_sort_key`、`folder_sort_ascending`、`is_external_sync`、`bmt_sort`、`is_bmt_output`。 |
| `playlist_entry` | 所属ID、`md5`、`sha256`、`title`、`artist`、`folder`、`level`、`url`、`url_diff`、`name_diff`、`org_md5` と、ローカル状態の `memo`、`adddate`、`is_removed`。 |
| `playlist_course` | `course_id`、所属ID、元の順序 `course_order`、正規化した `course_json`。 |

譜面の解決・参照・所持集計はMD5があればMD5を選び、ない場合だけSHA-256を選びます。MD5不一致の後で同じ行のSHA-256へ探索を広げません。手動追加では所持譜面から分かる両ハッシュを保存し、外部同期では取得元の値を保存します。手動追加の `org_md5` は同じディレクトリ内の所持BMS/BMSONのMD5集合です。

所持譜面の参照解決索引は共通所持集合の管理主体が所有し、自身の単一の集合版と既存の失効版で構築・再利用を判断します。構築前の集合版捕捉と構築後の最新性確認、取消、digest更新範囲の待機を維持します。構築済み索引へ変更事実を差分適用し、未構築時は構築を起こさず、同MD5の別配置と捕捉済みの旧索引を保持します。排他境界は[データと索引](../core/data-and-indexes.md)を参照します。

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

置換の受付拒否、取消、中断、例外は再読込み失敗です。独自の時間切れで成功を推定したり、旧表示のまま成功扱いにしたりしません。受付と継続は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従い、必要出力・公開・cleanupの実Taskを待ちます。

URL指定の複数行取込みは、取得を既存の上限内で並列化し、登録をまとめて行います。既存名と同じバッチの予約名に重複する表は改名せず省略し、完了時に件数と名前を通知します。ライブラリ参照、サマリー、URL補完は登録済み集合へ一括反映します。取得後の登録・参照・完了処理も進捗に含めます。

取得操作で受け付けるURIと、保存済み表の外部同期元は別契約です。取得操作は各遷移でHTTP(S)だけを許可します。保存済み外部同期元では相対・ローカル・ドライブ・`file:`・UNCを維持し、HTTP専用検証を流用しません。

#### 取得失敗とSTATUS

全体同期・手動再取得では、外部表の通信・ローカル同期元の読取り・形式不正を取得境界の対象失敗として扱います。詳細はログに記録し、失敗した表の既存データを保持して、取得できた他表の確定を妨げません。これらの処理済み例外をバッチから再送出しません。全対象に失敗した場合も完了通知を全件成功にはしません。

STATUSには404、403、その他HTTPエラー、通信失敗、ヘッダー未検出、ヘッダー形式不正、データ形式不正、URL不正の分類を保持します。ローカル読取り失敗は既存のその他エラーを使用します。STATUSの詳細欄は分類・確認日時・取得元を示し、元例外の全階層を保持しません。

この扱いを、DB読込み・保存・必要な参照公開・出力・内蔵推薦の失敗へ流用しません。操作tokenによる取消しと通信の期限切れも区別します。単表のプロパティ保存は確定結果と未完了出力の既存契約を維持します。

| 入力・保証 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 取得失敗の局所化、STATUS、正常表の確定、旧表の保持、明示再取得 | `PlaylistExternalSyncOwner.ReloadPlaylistTargetCoreAsync`、`PlaylistSyncAttemptResult`、`PlaylistWorkspaceViewModel` | `PlaylistExternalFailureBoundaryTests.ExternalSync_SourceFailurePreservesStatusAndCommittedTables`。実HTTP 400/403/404、ヘッダー・データ・項目形式不正、URL欠落、ローカル読取り失敗、全対象失敗。 |
| 手動再取得のSTATUS・現在参照・DB保持 | `PlaylistWorkspaceViewModel.ResyncPlaylistTableAsync` | `BmsPlaylistExternalReloadTests.PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle`。不正JSONの分類・日時・取得元を表示状態へ残し、Workspaceから詳細を再記録せず、直前の正常更新結果を保持する。 |
| 取得境界での診断と元原因の保持 | `PlaylistExternalSyncOwner.LoadExternalTableAsync` | `PlaylistExternalFailureBoundaryTests.ExternalLoad_ReportsOriginalFailureWithoutRetainingItInStatus`。実HTTP 403・不正ヘッダーと診断先の正常・失敗で、元例外と取得元が診断先へ渡り、STATUSに元例外を持ち越さない。 |
| 包装された内部故障を通常取得失敗にしない | `PlaylistSyncAttemptResult.IsExpectedExternalLoadFailure` | `PlaylistExternalFailureBoundaryTests.SourceFailureClassification_DoesNotConsumeInternalFailures`。内部故障・OOM・混合集約を分類対象から除外する。 |

#### DB保存と画面反映の完了

DB接続・短いモデルロックを解放してからUIスケジューラーによる実置換を待ちます。DB保存だけでは再読込み成功になりません。置換前の受付拒否・取消・中断・例外は現在の公開状態へ既存のDB再整合を試み、その失敗も結果へ残します。コレクション代入後の購読通知例外は、実際に採用された新参照を維持して失敗を返します。必要なLR2/BMT出力と参照公開は保存済み事実を用いて行い、公開失敗で実施済みDBや出力を消しません。

外部再取得は対象ごとの確定参照・変更・失敗と、取消しや一括出力の操作全体の失敗を同じ結果で返します。呼出元は確定参照を採用してから失敗を通知し、二表の一方の失敗でも他方の確定結果を失いません。変更なしの対象は現行参照を返し、DB保存なしを参照採用の拒否に読み替えません。プロパティ保存内では保存サービスが必要出力を所有し、取得済み変更から一回実行します。

確定済み参照やDBは後段通知の失敗で戻しません。現行の手動再同期と任意の外部同期には、出力・取消原因と後段通知の例外を合成して返す経路が残っています。通知だけの例外を局所診断へ移す作業は[例外処理の修復計画](../../plan/exception-handling-simplification-plan.md)で扱い、必要な参照反映と通知を分けます。

内蔵難度推定表・リコメンドの入力と計算は [専用仕様](local-recommendations.md)に従い、生成結果を同じ登録・再読込み統合へ渡します。

### 更新検知と更新日時

ヘッダーハッシュは `compat_prefix` を除き、明示接頭辞付きの `folder_order` を比較用に正規化して計算します。`tag`、`course`、`level_order`、`symbol` 等は含みます。データハッシュは取得したデータJSON全体が対象です。

| 変化 | DB保存 | `last_update` |
| --- | --- | --- |
| 既知のヘッダーハッシュから別値へ変化 | ヘッダーとコース。譜面行は保存し直さない。 | 更新する。 |
| ヘッダーハッシュの初期化、または一致する旧形式からの変換 | ヘッダーとコース。 | 更新しない。 |
| 既知のデータハッシュから別値へ変化 | ヘッダー・コース・譜面行。 | 更新する。 |
| データハッシュの初期化 | ヘッダー・コース・譜面行。 | 更新しない。 |
| 表示名 `name` または取得元名 `org_name` だけが変化 | ヘッダーとコース。譜面行は保存し直さない。 | 更新しない。 |
| 譜面行の内容比較だけで差分を検出 | 一致行のローカル状態を引き継いで譜面行を保存する。 | 更新しない。 |

いずれもBMT再出力の対象です。内容比較はハッシュ未初期化や古い状態でも譜面差分を反映するためのもので、更新日時の独立した根拠にはしません。外部表の初回登録は取得元の日時があればそれを使い、なければ登録時刻です。空のローカル表は作成時刻を初期値とし、通常保存でDBへ反映します。

リコメンドは再読込み時に三方針の自動名へ戻し、難度推定表と通常外部表は手動名を保持します。名称差だけでもDB保存と画面公開を行います。実力更新通知は、成功反映前後の取得元名から解釈できる★数値と設定で別に判定し、ハッシュ初期化・更新検知・日時の有無で制限しません。詳細は[内蔵リコメンド](local-recommendations.md#uriui取消と保存)を参照します。

フォルダ作成・改名・譜面追加・削除など構成の手動編集では編集時刻へ更新します。名前、記号、URL、出力先、整列・順序、接頭辞だけの保存では更新しません。接頭辞に伴う表示名の変換も譜面所属の変更ではありません。

### 手動編集と失敗

フォルダ作成・改名・削除、譜面削除、ドロップを一つの編集として扱います。DB保存前の失敗では、同じ表・譜面オブジェクトを維持しながら集合、編集可能な値、フォルダ順、更新日時、譜面の版を開始時へ戻します。全表の交換や無条件の再読込み、永続的な取消履歴・再試行は使いません。複数表の削除は受理した有限の対象を一回のDB保存へまとめます。DB確定後の出力・公開失敗で成功済み事実を戻しません。

永続化成功後だけ詳細表示の同期、成功通知、`afterApply` を進めます。サマリーの一括変更も同様で、失敗後の索引・検索候補・整列・表示更新やダイアログ終了を成功経路で実行しません。モデルの途中失敗でプロパティ通知を公開せず、復元後の現在状態を通常の通知処理へ渡します。

受付条件・draftの現行性確認は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。新規draftは保存まで正本外です。購読先の実行前に集合・表・DBのロックを解放し、必要な出力・公開は同じ処理のTaskとして直接待ちます。

再同期完了後の古い詳細行からの保存は拒否し、最新行を選んで再操作するよう通知します。同じID・hash等への旧編集の引継ぎは行わず、再同期後のlevel・URL・保存値と表示を保ちます。最新行からの明示編集は通常どおり保存できます。

詳細セルの保存は正本の現行項目に限ります。ローカル表では本体・差分の有効URLをDB原値へ材料化し、更新日時を表ヘッダーと一致させます。外部同期表の補完URLは派生値のまま保持します。所持譜面によるhash補完を含めて同じ項目を一意に保存し、bmsonの両hash・Org_md5を保ちます。level変更に必要なLR2出力も同じ受理操作で反映します。

DB確定後のLR2/BMT出力失敗で、保存済みの表や表示を巻き戻しません。対象と元の原因を警告・エラーへ残し、現在の参照と表示は確定した内容へ揃えます。BMT生成・削除・manifest・Table URL反映の失敗も元操作の部分失敗として返します。

詳細セルはDB確定からLR2/BMT出力と通知終端までを一つの確定後境界として扱います。設定捕捉・準備・書込みの失敗、shutdownによるBMT取消、必要通知の失敗でも、確定後の失敗分類で元例外・取消を保持します。通知が二次的に失敗した場合は元の失敗を置換せず、その例外も保持します。DB、正本の項目、詳細一覧の入力値を確定した値へ揃えます。DB未確定時だけ正本と編集した一属性の表示値を戻し、旧入力snapshotとの整合と同じ入力の再保存を保ちます。

### フォルダ単位のドロップ

入力順を保ったディレクトリ単位の計画を作り、計画中は表示用の表を変更しません。既存と先に計画したフォルダ・譜面を作業集合に持ち、`GetPlaylistFolderOrgMd5sForCharts` のパッケージMD5集合とフォルダ内MD5の重なりで分類します。削除済み譜面しかないフォルダは候補から外しますが、その履歴は保持します。

複数候補は実一覧と同じフォルダ順・自然順で選びます。名前の衝突は既存の接尾辞規則で避け、同じタイトルだけ、または空の親MD5集合だけでは統合しません。同じ入力順なら一括投入と分割投入で所属と重複除去後の集合が一致します。任意の入力順の同一視や既存手動分類の移行は行いません。

通常譜面、プレイリスト詳細、解決済みの `PlayHistoryRow.ResolvedChart` を入力にできます。未解決の履歴行を含む選択は部分追加しません。

### JSON出力・SQLバックアップと復元

JSON出力は必要な正本入力を読取り境界内で捕捉し、保存先に応じたData_urlは出力用値だけへ反映します。シリアライズ中も正本Data_urlを一時変更しません。


バックアップは整合読取りを使い、選択先と同じディレクトリの一時ファイルへUTF-8・BOMなしで書き、閉じた後に置換または移動で公開します。先に既存先を消したり、直接上書きへ切り替えたりしません。後片付け失敗も診断に残し、成功通知は公開後だけです。

SQLリテラルはSQLiteの保存値と型から生成します。数値風のTEXT、空文字、空白、引用符、日本語はTEXTのまま、NULLは空文字と区別して往復します。埋込みNULやバックアップ区切りに使う垂直TABを含むTEXTは、引用時の切詰めや区切りとの衝突を避け、全byteから同じTEXTを復元するSQL式へ変換します。INTEGERとREALも値・型を保ち、文化圏の数値解釈へ依存しません。旧形式のBMT設定を補正する場合も、bmt_sortとis_bmt_outputの二列だけを更新して他列の保存値を保ちます。復元途中のSQL失敗では旧三表を保ち、songや未知の利用者表は変更しません。従来の有効なSQLバックアップも引き続き読み込みます。

復元はファイル読込み、DB復元、表示一覧への反映を分けます。DBのMonitor、トランザクション、確定後のヘッダー読込み、接続解放は同じワーカーの同期範囲で完結させます。UIへの一覧反映を予約する前にその範囲を抜け、予約の受付だけでなく完了を待ちます。譜面行は引き続き遅延読込みです。

復元の受付分類は[競合ポリシー](../core/operation-concurrency-policy.md#設定全体操作終了)に従います。読込み・復元確定前の失敗は旧DBと一覧を保持します。確定後のヘッダー読込みやUI反映の失敗は復元済みDBを保持し、元エラーを返します。UIの途中適用は補償しません。必須hydration・出力修復も同じ受理操作のTaskとして実終端を待ち、表示用の独立した版・対象識別は維持します。

復元後に開始した必須のプレイリスト準備は、出力先同期等が失敗した場合も同じ元例外で終端します。復元処理と準備を待つ外部取込みの両方へ失敗を返し、待機を残しません。準備開始前の失敗では既存の準備状態を変えません。失敗時に成功表示、追加出力、設定画面終了、アプリ終了の許可を出しません。

SQL読取りは `SQLITE_ROW` と `SQLITE_DONE` 以外を失敗とし、途中行をダンプ・復元・表示へ公開しません。読取り失敗を解放失敗より主原因にします。同期 `LoadPlaylistDump` は移行等のDB専用入口に限り、設定画面は非同期復元を使います。

## 実装とテストの対応

`PlaylistUrlCompletionTests.DetailEdit_LocalAttributesPersistThroughFreshPlaylistAndPresentation` は通常ローカル表のLevel・Url・Url_diff・comment・memoを1属性ずつ実保存Taskの終端まで待ち、現在entry、編集row、同期source、新表示row、read-only DB、新playlistと表示rowまで独立期待を照合する。新playlistは実repositoryからヘッダーを読み、実hydration ownerで対象表を読み込む。起動時の全初期化はこの保存読戻しの保証対象に含めない。未編集属性・識別子と別entryを保つ。列割当の全5属性は `CustomTableColumnFactoryTests`、F2/Enterの上位端末は `MainWindowChartPresentationWpfTests`、実効URL確定と必要LR2出力・確定後失敗は既存の詳細編集系列へ分担する。

名称差のヘッダー保存と日時維持は `PlaylistReloadMergeTests`、実DB再読込み・公開・同一入力の不要保存は `BmsPlaylistExternalReloadTests.LocalRecommendations_KeepUserStateAndAvoidUnchangedWritesThenReachLr2AndBmt` で確認します。推薦以外の手動名保持、既知ハッシュ差の既存保存範囲・日時更新も維持します。通知と三入口の表示確認は[内蔵リコメンドの対応表](local-recommendations.md#実装とテストの対応)を参照します。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| スキーマ・既存設定・コース | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、[`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) |
| 完成集合の共通取込み・公開構成保護・入力集合の共有排除 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs)の標準可変interface経由の変更試行、空・非空入力の後続変更、行参照・順序・所属・フォルダ状態・リビジョン維持。実登録と新主体のhydrationは内蔵表群と外部JSON登録群で分担する。 |
| SQLバックアップの値・型・所属と旧世代保全 | [`LR2SongDBExtended`](../../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs)、[`PlaylistPersistenceRepository`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistPersistenceRepository.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs)で三表の実往復、二組の文化圏、途中SQL失敗、非対象保全。ファイル公開・通知と復元の受付・UI寿命は既存workspace群が分担する。 |
| 外部取得・変更判定・表示の完了 | [`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | [`BmsPlaylistExternalLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs)、[`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) |
| DB失敗後の同一オブジェクト保持と次の正常編集 | [`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) |
| 実ドロップ・分類・参照・通知 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 所持参照解決索引の所有・版・差分と旧捕捉値 | [`CatalogOwnedCollectionOwner.Playlist`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.Playlist.cs) | [`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryResolveIndexTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryOwnedHashTests.cs) |
| 復元の排他・DB待機・UI完了・準備失敗 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 復元から新正本・表示・必須出力と次保存への正常接続 | 同上、`PlaylistWorkspaceViewModel`、`PlaylistPropertySaveService` | 同workspace系列の `PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess` は実SQL復元後の新正本・ツリー受信・summary・entry hydration、必要な実LR2/BMT出力を新readerの初期化前に観測する。新正本の次編集保存を新readonly接続で読戻し、別表・song・利用者表と管理外ファイルを保つ。SQL値・型の条件表は既存schema往復系列へ分担する。 |
| 見出し・項目の型と不正なJSONの失敗 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs) |
| 現行詳細編集のURL材料化・非材料化、日時、hash一意性・補完、bmson、削除済対象拒否、level出力 | [`PlaylistWorkspaceViewModel.DetailEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、[`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`PlaylistUrlCompletionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs)の実詳細保存とDB原値、[`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)の表・項目削除後拒否、[`BmsPlaylistCustomFolderOutputTests.DetailEdit_ReoutputsLr2FolderRowsForLevelProjection`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)のprovider優先・実DB/ファイル・確定後のLR2/BMT失敗・shutdown取消・通知失敗時のDB/正本/入力保持、DB未確定失敗時の旧値保持と二次通知失敗の識別 |
| 項目の題名の永続化 | [`LR2SongDB`](../../../BeMusicSeeker/Models/LR2/LR2SongDB.cs) | [`Lr2PlaylistEntryPersistenceTests`](../../../BeMusicSeeker.Tests/Playlist/Lr2PlaylistEntryPersistenceTests.cs) |
| 外部再取得の出力失敗・部分成功・実置換後の通知失敗と次操作 | `PlaylistExternalSyncOwner`、`PlaylistAggregatePersistenceOwner` | [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の`ReloadPlaylistTargetsAsync_OutputFailureReturnsConfirmedReferencesAndSeparateTargetFailures`、`ReloadPlaylistTargetsAsync_CollectionNotificationFailureRetainsActuallyAdoptedReference`。変更判定・日時・ローカル値は既存再取得・mergeテストと分担する。 |
| 手動単表・任意複数表の元BMT失敗と参照採用後の表示失敗の保持 | `PlaylistWorkspaceViewModel.Reload`、`PlaylistWorkspaceViewModel.ExternalPlaylistSync` | [`BmsPlaylistExternalReloadTests.PlaylistWorkspaceResync_PostAdoptionPresentationFailureRetainsOutputFailureAndConfirmedState`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の二行で、実manifest書込み失敗と後段通知失敗、通知中P/Task保持、進捗終端と次要求を確認する。 |
| 操作tokenの全体取消し、別対象の確定参照・receipt保持と全開始Task終端 | `PlaylistExternalSyncOwner` | [`BmsPlaylistExternalReloadTests.ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)で実JSONの置換確定と実HTTP本文待機を分け、scheduler完了までP・進捗を保持し、取消対象のDB非変更と次要求を確認する。参照採用後のsort通知失敗を重ねる行では、取消原因と二次失敗も保持する。[`PlaylistRecommendedTableOwnerTests.ReloadPlaylistTargetsAsync_LocalReadFailurePreservesDataAndExitsUpdating`](../../../BeMusicSeeker.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs)で通常対象失敗との分類とDB・memo・通知非変更を分担する。UI scheduler由来の中断は既存ケースを維持する。 |
| 新規draftの初回登録と出力失敗後の同ID再保存、同時編集の三境界 | `PlaylistPropertySaveService`、`PlaylistPropertyEditSession` | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)の`CreatePlaylistPropertyDialog_OutputFailureResavesRegisteredDraftWithoutDuplicateId`と`PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges`。属性・参照・本番項目追加の版を観測する。 |
| 借用した読取りの解放、参照先の確定と操作の受付 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) |

## 関連資料

[LR2出力](lr2-custom-folders.md)、[BMT出力](bmt-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[データと索引](../core/data-and-indexes.md)を参照します。

詳細セルの旧行拒否と再操作は [`MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が、実再同期、小DB、実MainWindowの通知購読と所有windowを接続して確認します。無効URIの編集は [`PlaylistUrlCompletionTests.DetailEdit_InvalidUriPreservesSavedEntryAndReleasesEditing`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) が、実編集Taskの終端後の編集解放、行・正本・read-only DBの元URLと未編集属性の保持を確認します。取消・通常編集は既存 `PlaylistViewPipelineTests`、一般の永続化・表示参照更新は `BmsPlaylistPersistenceLifecycleTests` が分担します。
