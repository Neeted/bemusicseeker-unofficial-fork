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

カタログ変更後の詳細は有効エントリーを保持し、最後の一致配置が消えれば未所持へ、別配置が残れば既存の代表選択で再解決します。表示の更新だけでエントリーのDB値や出力ファイルを変更しません。サマリーの所持・未所持集計も更新しますが、表の所属・総エントリー数の算定規則・同期STATUSはカタログ変更だけでは変えません。詳細とサマリーの採用・再利用は[一覧表示](../ui/table-view.md)に従います。

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

全体同期・手動再取得・起動後の同期では、未採用の外部表の通信・ローカル同期元の読取り・解析失敗を取得境界の対象失敗として扱います。原因の許可リストを続行条件にせず、未分類の通常失敗も既存のその他エラーとして残します。詳細はログに記録し、失敗した表の既存データを保持して、取得できた他表の確定を妨げません。処理済みの取得失敗を、親が操作全体の例外へ再集約・再送出しません。全対象に失敗した場合も完了通知を全件成功にはしません。

表ヘッダー・データのJSON形式失敗だけを対応する解析例外へ変換し、内部故障・資源不足を入力不良へ包装しません。任意の日付・順序・ハッシュ・URL・親ハッシュ項目で従来許容していた不正値の省略は、その入力変換だけを囲む境界に残します。行集合・フォルダ状態の必要反映や購読先の故障は解析失敗へ変換しません。受理入力、日付表現、順序、ハッシュと後続JSONの判定は `BMSTableLoadTests`、`BmsPlaylistExternalLoadTests` で維持します。

STATUSには404、403、その他HTTPエラー、通信失敗、ヘッダー未検出、ヘッダー形式不正、データ形式不正、URL不正の分類を保持します。ローカル読取り失敗は既存のその他エラーを使用します。STATUSの詳細欄は分類・確認日時・取得元を示し、元例外の全階層を保持しません。

この扱いを、DB読込み・保存・必要な参照公開・出力・内蔵推薦の失敗へ流用しません。操作トークンによる取消しと通信の期限切れも区別します。単表のプロパティ保存では、再取得に失敗しても属性保存と必要出力を進め、保存済みの編集値を採用してから同期未完了を通知し、ダイアログを開いたままにします。一般の同期操作で処理済み取得失敗を再送出しないことを、この未完了結果を消す理由にしません。元例外と取得元URLの診断は取得境界で行い、通常取得失敗を再取得対象境界でも重複記録しません。ワークスペースの診断コールバックが失敗しても実処理の失敗一覧へ加えません。

| 入力・保証 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 取得失敗の局所化、STATUS、正常表の確定、旧表の保持、明示再取得 | `PlaylistExternalSyncOwner.ReloadPlaylistTargetCoreAsync`、`PlaylistSyncAttemptResult`、`PlaylistWorkspaceViewModel` | `PlaylistExternalFailureBoundaryTests.ExternalSync_SourceFailurePreservesStatusAndCommittedTables`。実HTTP 400/403/404、ヘッダー・データ・項目形式不正、URL欠落、ローカル読取り失敗、未分類の取得失敗、全対象失敗。原因分類は表示のために使い、停止範囲を決める許可リストとして固定しない。 |
| 手動再取得のSTATUS・現在参照・DB保持 | `PlaylistWorkspaceViewModel.ResyncPlaylistTableAsync` | `BmsPlaylistExternalReloadTests.PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle`。不正JSONの分類・日時・取得元を表示状態へ残し、Workspaceから詳細を再記録せず、直前の正常更新結果を保持する。 |
| 取得境界での診断と元原因の保持 | `PlaylistExternalSyncOwner.LoadExternalTableAsync` | `PlaylistExternalFailureBoundaryTests.ExternalLoad_ReportsOriginalFailureWithoutRetainingItInStatus`。実HTTP 403・不正ヘッダーと診断先の正常・失敗で、元例外と取得元が診断先へ渡り、STATUSに元例外を持ち越さない。 |
| 起動の対象失敗と保存失敗の区別 | `PlaylistWorkspaceViewModel.QueueExternalPlaylistSync`、`PlaylistExternalSyncOwner.PlaylistReloadOperationResult` | `ExternalSync_SourceFailurePreservesStatusAndCommittedTables` の起動条件は予約された実Taskの終端、旧表・正常表、全件成功でない完了通知を確認する。`PlaylistExternalFailureBoundaryTests.ExternalReload_DatabaseSaveFailureRemainsUnhandledAndPreservesCommittedTable` は実SQLiteの保存失敗を取得失敗として除外せず、旧正本・受付と次要求を保持する。 |
| 単表属性保存の保存済み・同期未完了 | `PlaylistPropertySaveService`、`PlaylistPropertyDialogViewModel` | `BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_ExternalSourceFailureAdoptsSavedValuesAndCompletesRequiredOutput` は未分類の外部取得失敗でも属性DB保存と実BMT出力、通知時の保存値採用、Reset基準、次保存を確認する。表示STATUSの分類と例外を持ち越さない契約は `PlaylistSyncAttemptResultTests.CreateReportedExternalFailure_PreservesFailureClassificationWithoutExceptionTransport` が分担する。 |

#### DB保存と画面反映の完了

DB接続・短いモデルロックを解放してからUIスケジューラーによる実置換を待ちます。DB保存だけでは再読込み成功になりません。置換前の受付拒否・取消・中断・例外は現在の公開状態へ既存のDB再整合を試み、その失敗も結果へ残します。コレクション代入後の購読通知例外は、実際に採用された新参照を維持して失敗を返します。必要なLR2/BMT出力と参照公開は保存済み事実を用いて行い、公開失敗で実施済みDBや出力を消しません。

外部再取得は対象ごとの確定参照・変更・失敗と、取消しや一括出力の操作全体の失敗を同じ結果で返します。呼出元は確定参照を採用してから失敗を通知し、二表の一方の失敗でも他方の確定結果を失いません。変更なしの対象は現行参照を返し、DB保存なしを参照採用の拒否に読み替えません。プロパティ保存内では保存サービスが必要出力を所有し、取得済み変更から一回実行します。

確定済み参照やDBは後段処理の失敗で戻しません。手動再同期と任意の外部同期の操作結果記録表示は、共通の境界で元原因と操作経路を局所診断し、出力・取消結果や完了事実の成否へ合成しません。結果記録取得・セッション寿命、必要な参照・索引・詳細公開が失敗した場合は、元の出力・取消原因とその実失敗を返します。進捗表示と実行周の開始・参照反映・完了を表示先へ知らせるイベントは任意通知とし、購読先ごとに失敗を診断して残りの購読先への通知を続けます。進捗の所有状態と実際の参照反映は、この任意通知から分離して維持します。`PlaylistDetailReloadRefreshRequested` は次の操作対象の詳細反映を要求するため、任意の結果記録表示とは区別します。結果記録配送の実終端まで受理TaskとPを保持し、表示失敗を理由に同期を自動再実行しません。次の明示要求は新しい取得内容を通常どおり保存・公開・出力します。

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

フォルダ作成・改名・削除、譜面削除、ドロップを一つの編集として扱います。DB保存前の失敗では、同じ表・譜面オブジェクトを維持しながら集合、編集可能な値、フォルダ順、更新日時、譜面の版を開始時へ戻します。全表の交換や無条件の再読込み、永続的な取消履歴・再試行は使いません。複数表の削除は受理した有限の対象を一回のDB保存へまとめます。DB確定後の出力・公開失敗で成功済み事実を戻しません。プロパティ保存では必要な索引・現行参照の公開と任意通知を区別し、進捗・処理後通知の失敗を保存失敗や次回の未完了出力へ持ち越しません。必要出力の継続は保存値と未完了の事実だけを保持し、過去の例外を次の明示保存へ再合成しません。

永続化成功後だけ詳細表示の同期、成功通知、`afterApply` を進めます。サマリーの一括変更も同様で、失敗後の索引・検索候補・整列・表示更新やダイアログ終了を成功経路で実行しません。モデルの途中失敗でプロパティ通知を公開せず、復元後の現在状態を通常の通知処理へ渡します。

受付条件・draftの現行性確認は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。新規draftは保存まで正本外です。購読先の実行前に集合・表・DBのロックを解放し、必要な出力・公開は同じ処理のTaskとして直接待ちます。

共有操作結果記録を使うフォルダ作成・改名、譜面削除、サマリー一括変更、外部プロパティ初期化、手動再取得、任意の外部同期、JSON出力、設定の非同期処理とバックアップでは、操作後の任意表示だけを局所診断します。表示先や診断先の故障を保存・出力の結果へ加えず、例外履歴や次の保存の原因として保持しません。結果記録取得とセッションの実寿命、必要な参照・索引・確認と進捗の所有状態は維持し、表示先の故障とは区別します。受理済みTaskと各入口が所有するセッション・操作権限・受付の既存解放順を保ち、通知の実試行と必要な後片付けを終端まで待ちます。`MainWindow`は結果記録の各要素だけを表示し、空の結果記録では表示を試みません。空イベントの購読処理故障と、実際の通知内容の表示故障は分けて検証します。

再同期完了後の古い詳細行からの保存は拒否し、最新行を選んで再操作するよう通知します。同じID・hash等への旧編集の引継ぎは行わず、再同期後のlevel・URL・保存値と表示を保ちます。最新行からの明示編集は通常どおり保存できます。

詳細セルの保存は正本の現行項目に限ります。ローカル表では本体・差分の有効URLをDB原値へ材料化し、更新日時を表ヘッダーと一致させます。外部同期表の補完URLは派生値のまま保持します。所持譜面によるhash補完を含めて同じ項目を一意に保存し、bmsonの両hash・Org_md5を保ちます。level変更に必要なLR2出力も同じ受理操作で反映します。

DB確定後のLR2/BMT出力失敗で、保存済みの表や表示を巻き戻しません。対象と元の原因を警告・エラーへ残し、現在の参照と表示は確定した内容へ揃えます。BMT生成・削除・manifest・Table URL反映の失敗も元操作の部分失敗として返します。

詳細セルはDB確定から必要なLR2/BMT出力と結果記録のセッション終端までを一つの確定後境界として扱います。設定捕捉・準備・書込みの失敗、終了によるBMT取消、結果記録取得やセッションの実寿命の失敗は、確定後の失敗分類で元例外・取消を保持します。先行のDB・出力失敗を二次結果記録・セッション失敗で置換せず、二次原因は局所診断します。任意の結果記録表示とその診断だけが失敗した場合は、保存成功を失敗へ変えず、実出力失敗や取消へ合成しません。DB、正本の項目、詳細一覧の入力スナップショットを確定した値へ揃えます。DB未確定時だけ正本と編集した一属性の表示値を戻し、旧入力スナップショットとの整合と同じ入力の再保存を保ちます。表示の実終端前に受理Task・受付を解放せず、編集セッションの後片付けを必ず行います。終了では受付閉鎖を維持し、それ以外の表示失敗後は次の明示編集を新しい保存値として受け付けます。

### フォルダ単位のドロップ

入力順を保ったディレクトリ単位の計画を作り、計画中は表示用の表を変更しません。既存と先に計画したフォルダ・譜面を作業集合に持ち、`GetPlaylistFolderOrgMd5sForCharts` のパッケージMD5集合とフォルダ内MD5の重なりで分類します。削除済み譜面しかないフォルダは候補から外しますが、その履歴は保持します。

複数候補は実一覧と同じフォルダ順・自然順で選びます。名前の衝突は既存の接尾辞規則で避け、同じタイトルだけ、または空の親MD5集合だけでは統合しません。同じ入力順なら一括投入と分割投入で所属と重複除去後の集合が一致します。任意の入力順の同一視や既存手動分類の移行は行いません。

通常譜面、プレイリスト詳細、解決済みの `PlayHistoryRow.ResolvedChart` を入力にできます。未解決の履歴行を含む選択は部分追加しません。

### JSON出力・SQLバックアップと復元

JSON出力は必要な正本入力を読取り境界内で捕捉し、保存先に応じたData_urlは出力用値だけへ反映します。シリアライズ中も正本Data_urlを一時変更しません。

選択したヘッダーを先に書き、その正常完了後にデータを書きます。書込み失敗では元原因、書込み段階（ヘッダーまたはデータ）、選択した保存先を既存の診断先へ渡すことを試みます。診断自体の失敗を分離し、一件のエラー結果記録で処理を終えます。データ失敗では既に書いたヘッダーを保持し、ヘッダー失敗では後続データを書きません。補償削除や自動再試行を行わず、表示や診断の故障でもこの部分状態と`Data_url`を変えません。この処理済み書込み失敗のTask正常終端は、停止と通知の実試行が終わったことを示し、両ファイルの出力成功を意味しません。選択画面、必要な項目読込み、シリアライズ、結果記録取得・セッションの実故障を書込み失敗の`catch`へ含めません。次の明示出力は新しい二つの保存先へ通常どおり出力し、以前の書込み・表示・診断例外を持ち越しません。

バックアップは整合読取りを使い、選択先と同じディレクトリの一時ファイルへUTF-8・BOMなしで書き、閉じた後に置換または移動で公開します。先に既存先を消したり、直接上書きへ切り替えたりしません。後片付け失敗も診断に残し、成功通知は公開後だけです。書込み・公開の失敗はワークスペースで保存先と元原因を診断し、一回の失敗結果記録へ渡します。結果記録の表示失敗は局所診断で終え、公開済みバイト列や元の保存結果を変えず、表示のために失敗を再送出しません。

SQLリテラルはSQLiteの保存値と型から生成します。数値風のTEXT、空文字、空白、引用符、日本語はTEXTのまま、NULLは空文字と区別して往復します。埋込みNULやバックアップ区切りに使う垂直TABを含むTEXTは、引用時の切詰めや区切りとの衝突を避け、全byteから同じTEXTを復元するSQL式へ変換します。INTEGERとREALも値・型を保ち、文化圏の数値解釈へ依存しません。旧形式のBMT設定を補正する場合も、bmt_sortとis_bmt_outputの二列だけを更新して他列の保存値を保ちます。復元途中のSQL失敗では旧三表を保ち、songや未知の利用者表は変更しません。従来の有効なSQLバックアップも引き続き読み込みます。

復元はファイル読込み、DB復元、表示一覧への反映を分けます。DBのMonitor、トランザクション、確定後のヘッダー読込み、接続解放は同じワーカーの同期範囲で完結させます。UIへの一覧反映を予約する前にその範囲を抜け、予約の受付だけでなく完了を待ちます。譜面行は引き続き遅延読込みです。

復元の受付分類は[競合ポリシー](../core/operation-concurrency-policy.md#設定全体操作終了)に従います。読込み・復元確定前の失敗は旧DBと一覧を保持します。確定後のヘッダー読込みやUI反映の失敗は復元済みDBを保持し、元原因を診断して終了不可の結果と失敗結果記録を返します。UIの途中適用は補償しません。必須読込み・出力修復も同じ受理操作のTaskとして実終端を待ち、表示用の独立した版・対象識別は維持します。

復元後に開始した必須のプレイリスト準備は、出力先同期等が失敗した場合も同じ元例外で終端します。復元処理と準備を待つ外部取込みの両方へ失敗を返し、待機を残しません。準備開始前の失敗では既存の準備状態を変えません。失敗時に成功表示、追加出力、設定画面終了、アプリ終了の許可を出しません。ワークスペースは必要公開・出力の完了までL/Pを保持し、最終結果通知より先に解放します。`Busy`・受付拒否ではバックアップを読まず、復元完了の結果を返しません。下位の失敗結果記録があれば同じ事実を再通知せず、通知失敗も元復元結果へ合成しません。実復元と必要公開・出力が成功した場合だけ終了を許可し、任意の完了・終了案内の失敗はその許可を取り消しません。必要出力後の任意URL補完の予約・診断失敗は局所で終え、成功済み復元や必須準備完了状態を失敗へ戻しません。

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
| JSON出力の順序・部分状態と書込み・通知・診断の故障分離 | [`PlaylistWorkspaceViewModel.PlaylistExport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) の `PlaylistWorkspaceExportPlaylistTableAsync_PublishesFileFailureAndPreservesHeaderFirstOrder` は、表示・診断の成功と失敗の組合せで、データ書込み失敗後のヘッダー保持、ヘッダー書込み失敗後のデータ未出力を確認する。元原因・段階・保存先と表示失敗を別に診断し、一件のエラー結果記録、処理終端と次の明示出力、正本の `Data_url` 保持を確認する。診断窓口への引渡しは永続ログ成功の保証ではない。正常JSONと保存先選択は同クラスの既存ケースが分担する。 |
| 所持参照解決索引の所有・版・差分と旧捕捉値 | [`CatalogOwnedCollectionOwner.Playlist`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.Playlist.cs) | [`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryResolveIndexTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryOwnedHashTests.cs) |
| バックアップの公開結果と任意通知、復元の終了許可 | [`PlaylistWorkspaceViewModel.PlaylistBackup`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs)、[`PlaylistWorkspaceViewModel.PlaylistRestore`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) の `PlaylistWorkspaceBackupPlaylistAsync_PublishesSuccessWarningAndFailureReceipts` は既存内容の保護・公開済み内容と結果通知を確認する。`PlaylistWorkspaceRestorePlaylistBackupAsync_AwaitsUiCompletionAndRejectsCompetingWrites` は必須UIの終端と通知前のP解放、任意URL補完予約の成功・失敗を分担する。`PlaylistWorkspaceRestorePlaylistBackupAsync_UiApplyFailureKeepsCommittedDatabaseAndOldCollection` は確定後失敗と終了拒否、`PlaylistWorkspaceRestorePlaylistBackupAsync_RollsBackInvalidDumpAndReleasesLock` は不正SQLの巻戻しと次の明示保存を確認する。`PlaylistWorkspaceRestorePlaylistBackupAsync_ReportsReadFailureWithoutAuthorizingExit` は入力読取り失敗後のDB・モデル・公開一覧の保持、終了不可と通知前のL/P解放を確認する。実ボタン・画面閉鎖は[設定画面の対応表](../runtime/settings.md#実装とテストの対応)が分担する。 |
| 復元の排他・DB待機・UI完了・準備失敗 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 復元から新正本・表示・必須出力と次保存への正常接続 | 同上、`PlaylistWorkspaceViewModel`、`PlaylistPropertySaveService` | 同workspace系列の `PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess` は実SQL復元後の新正本・ツリー受信・summary・entry hydration、必要な実LR2/BMT出力を新readerの初期化前に観測する。新正本の次編集保存を新readonly接続で読戻し、別表・song・利用者表と管理外ファイルを保つ。SQL値・型の条件表は既存schema往復系列へ分担する。 |
| 見出し・項目の型と不正なJSONの失敗 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs) |
| 現行詳細編集のURL・日時・ハッシュ一意性と補完・BMSON・レベルと確定前後の失敗 | [`PlaylistWorkspaceViewModel.DetailEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、[`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`PlaylistUrlCompletionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) はURLの材料化・非材料化とDB原値、[`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) は表・項目削除後の拒否を確認する。[`BmsPlaylistCustomFolderOutputTests.DetailEdit_ReoutputsLr2FolderRowsForLevelProjection`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) は設定取得窓口の優先、DB・LR2/BMT出力、確定後の出力・準備失敗と終了取消、確定前失敗の旧値保持と同じ入力の再保存を分担する。 |
| 詳細編集の空の結果記録と実LR2警告の配送、受付と次操作 | [`PlaylistWorkspaceViewModel.DetailTerminal`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailTerminal.cs) | [`BmsPlaylistCustomFolderOutputTests.DetailEdit_ReoutputsLr2FolderRowsForLevelProjection`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) は成功・DB確定前失敗・BMT取消での空の結果記録の購読故障と、LR2出力失敗による実警告の表示・診断故障を区別する。警告の件数・種別・見出し・表名・出力先、配送中のDB確定・編集状態・PとTaskの保持を確認し、終端後も元原因とDB・正本・入力を保つ。編集後片付けと次の明示レベル変更のDB・LR2反映も確認する。 |
| 項目の題名の永続化 | [`LR2SongDB`](../../../BeMusicSeeker/Models/LR2/LR2SongDB.cs) | [`Lr2PlaylistEntryPersistenceTests`](../../../BeMusicSeeker.Tests/Playlist/Lr2PlaylistEntryPersistenceTests.cs) |
| 外部再取得の出力失敗・部分成功・実置換後の通知失敗と次操作 | `PlaylistExternalSyncOwner`、`PlaylistAggregatePersistenceOwner` | [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の`ReloadPlaylistTargetsAsync_OutputFailureReturnsConfirmedReferencesAndSeparateTargetFailures`、`ReloadPlaylistTargetsAsync_CollectionNotificationFailureRetainsActuallyAdoptedReference`。変更判定・日時・ローカル値は既存再取得・mergeテストと分担する。 |
| 手動単表・任意複数表の元BMT失敗と参照採用後の必要詳細公開失敗の保持 | [`PlaylistWorkspaceViewModel.Reload`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Reload.cs)、[`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | [`BmsPlaylistExternalReloadTests.PlaylistWorkspaceResync_PostAdoptionPresentationFailureRetainsOutputFailureAndConfirmedState`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の二行で、実台帳書込み失敗と必要な`PlaylistDetailReloadRefreshRequested`の実失敗、公開中P/Task保持、進捗終端と次要求を確認する。任意の操作結果記録表示失敗はこの二次原因の保証へ含めない。 |
| 進捗表示故障時の対象成否・保存結果・後続購読先への終端 | [`PlaylistWorkspaceViewModel.SyncProgress`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs)、[`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | [`PlaylistExternalFailureBoundaryTests.ExternalSync_SourceFailurePreservesStatusAndCommittedTables`](../../../BeMusicSeeker.Tests/Playlist/PlaylistExternalFailureBoundaryTests.cs) は実JSON・SQLiteで、開始・進捗・参照反映済み・完了の表示先が失敗しても、旧表保持、別表確定、正確な失敗状態、独立した購読先への終端、受付解放と次の全件成功を確認する。 |
| 任意外部同期の結果表示失敗と実同期結果・完了事実の分離 | [`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs)、[`PlaylistWorkspaceViewModel.Mutations`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs) | [`BmsPlaylistExternalReloadTests.ExternalPlaylistSync_ReceiptDisplayFailurePreservesCommittedResultAndNextRequest`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) は、実推薦の実力変化による情報通知の内容・種別・見出しと、BMT成功・台帳保存失敗の両条件で表示・診断故障を確認する。必要出力の警告と終端の情報通知を区別し、配送中の実DB・現在の表と詳細参照・ライブラリ索引、PとTaskの保持、完了の未公開を確認する。初回の有効1行・削除済み1行、次の明示変更の有効1行・削除済み2行を表とDBの完全な集合で照合し、有効行だけを索引とBMTへ反映する。台帳保存失敗でも先行生成済みBMTと旧台帳原本を保持する。通知故障を同期結果へ加えず、必要出力の元原因・進捗終端を保つ。実力不変で空の結果記録なら表示しない。削除履歴の一般分岐は [`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) が分担する。 |
| 実通知のメイン画面接続・表示内容・所有ウィンドウ | [`MainWindow.PresentPlaylistOperationNotifications`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`PlaylistWorkspaceViewModel.ReportBmtOutputFailures`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `RecommendationReload_ThreeEntriesPublishAutomaticNamesAndPresentOnlyNumericSkillChanges` は実推薦の情報通知と所有画面を確認する。`PlaylistPropertyDialog_ExternalReplacementBmtFailureKeepsOwnedDialogAndResavesSameId` はBMT警告の表示成功・失敗で、表示試行、見出し、出力先・台帳パス・元原因と所有画面を確認する。後続のエラー通知、元Taskの終端、画面・DB保持と同IDの再保存を保つ。警告表示の故障診断はBMT管理主体が担い、再読込み診断へ重複計上しない。 |
| 操作tokenの全体取消し、別対象の確定参照・receipt保持と全開始Task終端 | `PlaylistExternalSyncOwner` | [`BmsPlaylistExternalReloadTests.ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)で実JSONの置換確定と実HTTP本文待機を分け、scheduler完了までP・進捗を保持し、取消対象のDB非変更と次要求を確認する。参照採用後のsort通知失敗を重ねる行では、取消原因と二次失敗も保持する。[`PlaylistRecommendedTableOwnerTests.ReloadPlaylistTargetsAsync_LocalReadFailurePreservesDataAndExitsUpdating`](../../../BeMusicSeeker.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs)で通常対象失敗との分類とDB・memo・通知非変更を分担する。UI scheduler由来の中断は既存ケースを維持する。 |
| 新規draftの初回登録と出力失敗後の同ID再保存、同時編集の三境界 | `PlaylistPropertySaveService`、`PlaylistPropertyEditSession` | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)の`CreatePlaylistPropertyDialog_OutputFailureResavesRegisteredDraftWithoutDuplicateId`と`PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges`。属性・参照・本番項目追加の版を観測する。 |
| 借用した読取りの解放、参照先の確定と操作の受付 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) |

## 関連資料

[LR2出力](lr2-custom-folders.md)、[BMT出力](bmt-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[データと索引](../core/data-and-indexes.md)を参照します。

詳細セルの旧行拒否と再操作は [`MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が、実再同期、小DB、実MainWindowの通知購読と所有windowを接続して確認します。無効URIの編集は [`PlaylistUrlCompletionTests.DetailEdit_InvalidUriPreservesSavedEntryAndReleasesEditing`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) が、実編集Taskの終端後の編集解放、行・正本・read-only DBの元URLと未編集属性の保持を確認します。取消・通常編集は既存 `PlaylistViewPipelineTests`、一般の永続化・表示参照更新は `BmsPlaylistPersistenceLifecycleTests` が分担します。
