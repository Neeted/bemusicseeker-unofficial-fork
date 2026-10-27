# プレイリストの保存と更新

## 目的と適用範囲

プレイリストの正本、外部同期の変更判定、手動編集、バックアップと復元を定めます。LR2向け・beatoraja向けの出力は別の仕様で定め、DB保存の成否と区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 正本と識別

永続化の正本は本アプリの `playlist`、`playlist_entry`、`playlist_course` です。アプリ内の現在値・所属・明示順序は `BMSPlaylist` / `PlaylistAggregatePersistenceOwner` / `BMSTable` が所有し、DB確定後に同じentry参照へ採用します。共通の `app_schema=1` に含まれ、プレイリスト専用のスキーマ版は持ちません。LR2連携・スタンドアロンの両方で保存します。

起動時のentry読込みは [型付きraw読取り](../core/performance-and-scale.md#全件処理と局所処理) で読取り専用の最終集合を生成します。REALのlevelはdoubleのbit、追加日は1tickを保ちます。旧schemaの `adddate TEXT` は通常writerのticks保存もTEXTになるため、固定TEXT ticksを引き続き受理します。非ticksの日付文字列を地域推測しません。所属IDがNULL・範囲外・REAL・不適合の行は除外し、削除履歴は保持します。levelの不適合はnull、真偽値の不適合はfalse、追加日の不適合は従来のconstructor日時を保持し、保存値を読込み時に書き換えません。

親MD5の保存生値は必要時まで遅延保持し、公開の `Org_md5` と `org_md5`、複製・再読込み用clone、JSON、BMTは一つの遅延解決を通して同じ親列と入力順を読みます。MD5あり・SHA-256のみ、親0・1・複数で既存の解析・正規化・空/nullの意味と外部形式を保ち、JSONの `org_md5s` は全親列、`org_md5` は先頭親または空文字です。読取りのために元DBを補正せず、起動時に全件を展開しません。BMTの出力と旧生成物の通常再生成は[BMT](bmt-export.md#形式とハッシュ)に従います。

ヘッダー一括保存の`requireCurrentTarget=true`は、要求した非空対象に一件でも退役参照があれば、全件失効を含めてDB変更前に失敗させます。入力空は何もしません。現行参照集合は保存要求内で一度構築し、対象ごとの全件再走査を行いません。`false`では現行参照だけを保存する従来の契約を維持します。編集受付での全対象検証は[プロパティと一括編集](../ui/playlist-properties.md)に従います。

JSON解析、内蔵推薦・難度推定、DB読込みで完成した行集合は、共通の `BMSTable.entries` setterで一度取り込みます。行の親、正規化、フォルダ派生状態、読込状態とentry版を揃えます。公開所属列は構造共有した読取り専用列で、未変更entryの参照と明示順序を保持します。entry自体はownerが更新するため、不変入力にはreader内で `CaptureEntryReadSnapshot` / `CaptureEntriesReadSnapshot` を使い、必要値とentry版を同時捕捉します。少数の追加・削除は対象だけを候補化し、全行のclone・退避・再取込みを行いません。所属・解決キーの依存は表示内容のentry版と分け、memo・URL・levelだけの更新では所持数を再走査しません。

| 表 | 主な列と意味 |
| --- | --- |
| `playlist` | `playlist_id`、`name`、`symbol`、`tag`、`page_url`、`header_url`、`data_url`、比較用の `header_sha256` と `data_sha256`、互換フォルダ名の `compat_prefix`、更新日時 `last_update` を保持する。 |
| `playlist` の表示・出力設定 | `output_dir`、`custom_folder_output_base_name`、`is_root_folder`、`ignore_folder_output`、`folder_order`、`folder_sort_key`、`folder_sort_ascending`、`is_external_sync`、`bmt_sort`、`is_bmt_output`。 |
| `playlist_entry` | 所属ID、`md5`、`sha256`、`title`、`artist`、`folder`、`level`、`url`、`url_diff`、`name_diff`、`org_md5` と、ローカル状態の `memo`、`adddate`、`is_removed`。 |
| `playlist_course` | `course_id`、所属ID、元の順序 `course_order`、正規化した `course_json`。 |

entryの所持譜面の解決・所持集計と参照索引への登録は、MD5があればMD5を選び、ない場合だけSHA-256を選びます。所持候補のMD5不一致の後で同じentryのSHA-256へ探索を広げません。手動追加では所持譜面から分かる両ハッシュを保存し、外部同期では取得元の値を保存します。手動追加の `org_md5` は同じディレクトリ内の所持BMS/BMSONのMD5集合です。

表内の所属キーは表と宛先フォルダごとに、空白だけでないMD5、SHA-256、`lr2_bmsid`、titleの順で最初の値を選び、すべて空なら空キーを使います。既存のhash setter・readerの正規化とOrdinal比較を維持します。MD5が同じならSHA-256やtitleが異なっても同じ所属キーであり、フォルダが異なれば別所属です。所持譜面の解決規則と混同しません。

所持譜面の参照解決索引はカタログownerが所有し、候補の識別・hash・配置・候補順に依存します。表示用のfull譜面値は保持せず、候補解決と選択したtokenのcurrent・CatalogRevisionを共通の読取り区間で捕捉します。無関係な内容変更だけで候補索引を再構築せず、取消、digest更新範囲の待機を維持します。構築済み索引へ変更事実を差分適用し、未構築時は構築を起こさず、同MD5の別配置と捕捉済みの旧索引を保持します。排他境界は[データと索引](../core/data-and-indexes.md)を参照します。

カタログ変更後の詳細は有効エントリーを保持し、最後の一致配置が消えれば未所持へ、別配置が残れば既存の代表選択で再解決します。表示の更新だけでエントリーのDB値や出力ファイルを変更しません。サマリーの所持・未所持集計も更新しますが、表の所属・総エントリー数の算定規則・同期STATUSはカタログ変更だけでは変えません。詳細とサマリーの採用・再利用は[一覧表示](../ui/table-view.md)に従います。

コースはヘッダー由来であり、入れ子配列もコースオブジェクトへ平坦化します。別のコースハッシュは保存せず、ヘッダーハッシュで更新を判定します。DB内部のコース改ざん検出を目的としません。

LR2の管理出力先を変える保存は、[出力配置条件](lr2-custom-folders.md#出力先と事前検査)を副作用前に満たす必要があります。保存済み包含配置への明示出力・移行・削除も同じ条件で拒否し、正本の保存や出力先の自動修正を行いません。配置を変えないエントリの追加・削除は既存のDB先行確定を維持します。付随するLR2出力が包含配置で拒否された場合は、編集済みのDB・モデルを保持し、確定後の出力失敗として通知します。

### プロパティのURL

Page・Header・Dataはnullによる明示解除を受理します。Pageの非null指定は有効な絶対URIを要求し、Header・Dataは有効な相対URIも受理します。同期OFFではURLの未設定やHeader・Dataの片方だけの指定も許容します。不正な非null入力を未設定や旧値へ置き換えて保存しません。画面での空欄・変換失敗と訂正・取消は[プロパティ画面](../ui/playlist-properties.md#単一プレイリストの編集)に従います。

外部同期ONでは、Pageが `bmseeker` の既存例外を除き、Header・Dataがともに設定され、PageまたはHeaderが絶対URIであることを要求します。Pageがnullでも絶対Headerを基点に相対Dataを使えます。HTTP(S)だけを要求する取得操作の検査を、この保存条件へ流用しません。

### 互換フォルダ名

新規取得と明示的な外部データ初期化では、ヘッダーに `compat_prefix` があれば空文字・CP932範囲外の文字も含めその値を保存します。なければ `tag`、次に `symbol` を候補とし、選んだ値をCP932で厳密に表せる場合だけ採用します。表せなければ文字を削ったり次候補へ切り替えたりせず `LEVEL ` とします。候補がない場合も同じです。レベル値が数字か記号かでは決めません。

明示の接頭辞がないヘッダーの `folder_order` は、読込み済みのフォルダ名と一致する値をそのまま保持し、それ以外を互換レベル名として接頭辞付きへ変換します。保存時は接頭辞をヘッダーへ明記します。通常の外部同期では、取得元の明示値よりDBの保存済み接頭辞を優先し、勝手に推定し直しません。

接頭辞の変更は各フォルダ名の先頭にある旧接頭辞だけを除き、新接頭辞を付けます。旧接頭辞がなければ元の名前全体へ付けます。既に新接頭辞で始まる名前も特別扱いしません。変換途中でなく最終名の重複を検証し、重複なら既存構成を保持して失敗とします。

この変更で譜面のフォルダ名が変わる場合は同じ保存単位で保存し、詳細表示の版を進めます。ツリーとFOLDER列、選択中のフォルダキーを同じ対応で更新します。接頭辞だけの変更では外部表を再取得しません。

### 既存DBの正規化

不足する `bmt_sort`、`is_bmt_output`、`custom_folder_output_base_name` は列の追加で対応し、専用の表再構築を行いません。出力先名のNULLは既定の通常出力先、BMT出力可否のNULLはtrueとして正規化します。`bmt_sort` は既存の有効値を優先し、名前・IDで同順位を解消して1始まりの一意な順序へ揃えます。

通常の外部更新ではこの3設定を保持します。新規表は最大の順序+1、BMT出力true、通常出力先名NULLです。出力除外ビットの既存値は維持し、新しく増えた種類まで除外するよう拡張しません。

### 外部取得と公開の境界

HTTP転送後の相対参照は[外部表の転送と相対参照](downloads.md#外部表の転送と相対参照)に従います。HTML入力の元 `Page_url`、直接header JSON入力の元 `Header_url` は次回の同期元として保持し、一時転送先へ置換しません。HTMLのraw相対headerを元pageで解決すると実header要求先と異なる場合だけ、既存の `Header_url` に実header要求先を保存します。

完成する表の `GetAbsoluteDataUrl()` が実最終data URIと異なる場合だけ、既存の `Data_url` にその実URIを保存してからentryを読み込みます。同じ参照なら通常のraw相対表現を保持します。返却表、登録保存、DB再読込みと公開data基準でentry本体・差分の意味を揃え、最終URIの永続fieldや本文hashへの混入を加えません。参照同値は `Uri.Equals` で判定し、HTTPのpath/queryの有意な大小文字を保ち、fragmentだけの差を変更にしません。保存済み同期元のfile・ドライブ・UNCの解決意味も維持します。

HTMLはアプリのHTTP管理主体が取得した本文だけを解析し、パーサーに再取得や外部DTD取得をさせません。ページ、ヘッダーJSON、データJSONは各一回取得します。相対ヘッダーURLはページ、相対データURLはヘッダーを基準に解決します。

取得と解析はUIスレッド外で行います。集合の書込みロックは重複確認・採番・出力先算出など短い状態変更に限定し、DB保存はその外で行います。確定後の表示集合への追加・置換はUIスケジューラーへ渡し、その処理の `Completion` を待って成功とします。UI完了を待つ間は表・集合・UIのロックを保持しません。

置換の受付拒否、取消、中断、例外は再読込み失敗です。独自の時間切れで成功を推定したり、旧表示のまま成功扱いにしたりしません。受付と継続は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従い、必要出力・公開・cleanupの実Taskを待ちます。

URL指定の複数行取込みは、取得を既存の上限内で並列化し、登録をまとめて行います。既存名と同じバッチの予約名に重複する表は改名せず省略し、完了時に件数と名前を通知します。ライブラリ参照、サマリー、URL補完は登録済み集合へ一括反映します。取得後の登録・参照・完了処理も進捗に含めます。

取得操作で受け付けるURIと、保存済み表の外部同期元は別契約です。取得操作は各遷移でHTTP(S)だけを許可します。保存済み外部同期元では相対・ローカル・ドライブ・`file:`・UNCを維持し、HTTP専用検証を流用しません。

#### 取得失敗とSTATUS

全体同期・手動再取得・起動後の同期では、未採用の外部表の通信・ローカル同期元の読取り・解析失敗を取得境界の対象失敗として扱います。原因の許可リストを続行条件にせず、未分類の通常失敗も既存のその他エラーとして残します。詳細はログに記録し、失敗した表の既存データを保持して、取得できた他表の確定を妨げません。処理済みの取得失敗を、親が操作全体の例外へ再集約・再送出しません。全対象に失敗した場合も完了通知を全件成功にはしません。

表ヘッダー・データのJSON形式失敗だけを対応する解析例外へ変換し、内部故障・資源不足を入力不良へ包装しません。任意の順序・ハッシュ・URL・親ハッシュ項目で従来許容していた不正値の省略は、その入力変換だけを囲む境界に残します。行集合・フォルダ状態の必要反映や購読先の故障は解析失敗へ変換しません。受理入力、日付表現、順序、ハッシュと後続JSONの判定は `BMSTableLoadTests`、`BmsPlaylistExternalLoadTests` で維持します。

採用する `last_update` と `adddate` の固定文法・空値・保存前停止は[JSON日時の規則](#json出力sqlバックアップと復元)に従います。通常の新規登録は読み取った時刻とtickまでDBへ保持し、再同期では既存項目のローカル追加日保持を維持します。既存DBの誤った年を推測して補正しません。

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

外部再取得は対象ごとの確定参照・変更・失敗と、取消しや一括出力の操作全体の失敗を同じ結果で返します。対象の保存と必須参照反映が終わるたびに、最終結果にも含める同じ対象結果を通知します。手動再取得・任意の外部同期・表一覧再読込みに続く外部同期は、他表の完了を待たずに対象の現在参照・更新日時・STATUSをサマリーへ反映し、選択中の詳細参照も新しい表へ追従させます。通常の取得失敗では旧参照・DB・更新日時を保ち、STATUSだけを更新します。重い詳細再構築は全件の終端で一度要求します。

対象確定の通知は派生出力の完了を意味しません。全件の取得・保存、必要なLR2/BMT出力と通知sessionの終端まで同じPと受付を保持し、Busyや操作全体の完了を個別反映で解放しません。BMT等の後続失敗は対象の成功結果を書き換えず、操作全体の失敗として残します。モデルの任意結果callbackは観測者の例外を局所診断しますが、Workspaceの必須採用失敗は操作内に並列安全に集め、全件終了後に元の対象・出力失敗と合わせて返します。終端では個別に採用した参照・STATUS・診断を再採用しません。

呼出元は確定参照を採用してから失敗を通知し、二表の一方の失敗でも他方の確定結果を失いません。変更なしの対象は現行参照を返し、DB保存なしを参照採用の拒否に読み替えません。プロパティ保存内では保存サービスが必要出力を所有し、取得済み変更から一回実行します。

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
| 本文hashが同じで、解決済みdata参照だけが変化 | ヘッダーとコース。空entry・全絶対entryでも保存し、URI差だけを譜面行保存の原因にしない。 | 更新しない。 |
| 譜面行の内容比較だけで差分を検出 | 一致行のローカル状態を引き継いで譜面行を保存する。 | 更新しない。 |

いずれもBMT再出力の対象です。内容比較はハッシュ未初期化や古い状態でも譜面差分を反映するためのもので、更新日時の独立した根拠にはしません。外部表の初回登録は取得元の日時があればそれを使い、なければ登録時刻です。空のローカル表は作成時刻を初期値とし、通常保存でDBへ反映します。

解決済みdata参照が同じなら、raw相対/絶対の表現差や無関係なpage/headerの差だけで保存・BMT対象を増やしません。URIだけの保存も `Updated=false`、`StatePersisted=true` の新しい確定表として既存receiptと新旧参照差で公開し、利用者の同期元、ID、手動名・記号、出力設定、hash、memo・adddateを保持します。相対entryの解決先が変わる場合の通常URL値の差は、既存の行内容比較で必要な行保存へ反映します。BMTも実data参照を入力指紋へ含め、変更回は生成bytesが同じでも一度の投影・書込みを行い得ます。その後の同じ完全入力・実ファイル状態での投影/書込み省略と、旧指紋の初回更新は[BMTの管理台帳](bmt-export.md#管理台帳と未変更判定)に従います。

リコメンドは再読込み時に三方針の自動名へ戻し、難度推定表と通常外部表は手動名を保持します。名称差だけでもDB保存と画面公開を行います。実力更新通知は、成功反映前後の取得元名から解釈できる★数値と設定で別に判定し、ハッシュ初期化・更新検知・日時の有無で制限しません。詳細は[内蔵リコメンド](local-recommendations.md#uriui取消と保存)を参照します。

フォルダ作成・改名・譜面追加・削除など構成の手動編集では編集時刻へ更新します。名前、記号、URL、出力先、整列・順序、接頭辞だけの保存では更新しません。接頭辞に伴う表示名の変換も譜面所属の変更ではありません。

### 手動編集と失敗

セル編集は[一覧の共通基準](../ui/table-view.md#行のドラッグと編集)に従い、必要表示と編集cleanupまで受付を保持します。既存の単一配送と実適用Taskを使い、receiptごとの完了管理や構築候補の後継への完了引継ぎは設けません。現在表示の編集列は正規化・補完後の確定値へ局所反映し、後からの参照投影で旧値へ戻しません。参照待ちの元投影を採用するときは、その対象の現在entry値を照合して確定編集属性を保ちます。全receiptの完全投影・後発構築の採用をセル保存の完了条件にしません。

選択中の通常フォルダを改名した場合は、確定した名前の対応へ選択を移します。接頭辞変更でも同じ対応を使い、別フォルダと特殊NO SONGノードの選択を保ちます。フォルダ削除では内容の移動先の空名フォルダを選び、そのノードが残らない場合は表本体を選びます。更新後に利用者が新しく明示選択した項目には、旧構成の改名対応を再適用しません。ツリー・workspace・詳細集合を一致させ、構造置換の親選択を明示選択と混同しません。

フォルダノード・通常名の並び・明示順序は確定した不変入力として公開します。必要な構成変更は既存writer内で完成させてから公開し、DB確定前の候補で現在の入力を変更しません。getter・選択受付・要求の複製で再構築や列複製をせず、MEMO・URL・level等の構成に依存しない編集では同じ入力を再利用します。必要通知はguard解放後に行い、捕捉済みの旧入力は後の構成変更でも書き換えません。

フォルダ作成・改名・削除、譜面削除、ドロップを一つの編集として扱います。対象folderの行と移動先で衝突する候補だけを準備し、DB確定まで同じ表・entry参照、所属、フォルダ順、更新日時、entry版を変更しません。少数folder変更のために全entryを退避・再取込み・DB全置換しません。全表の交換や無条件の再読込み、永続的な取消履歴・再試行は使いません。複数表の削除は受理した有限の対象を一回のDB保存へまとめます。DB確定後の出力・公開失敗で成功済み事実を戻しません。プロパティ保存では必要な索引・現行参照の公開と任意通知を区別し、進捗・処理後通知の失敗を保存失敗や次回の未完了出力へ持ち越しません。必要出力の継続は保存値と未完了の事実だけを保持し、過去の例外を次の明示保存へ再合成しません。

永続化成功後だけ確定差分の表示反映、成功通知、`afterApply` を進めます。サマリーの一括変更も同様で、DB前失敗を成功扱いにして索引・検索候補・整列・表示更新やダイアログ終了を進めません。DB前には候補をliveへ適用せず、DB後の必要出力・通知失敗では確定値を維持して同じ結果を配送します。モデルの準備途中ではプロパティ通知を公開しません。

受付条件・draftの現行性確認は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。新規draftは保存まで正本外です。購読先の実行前に集合・表・DBのロックを解放し、必要な出力・公開は同じ処理のTaskとして直接待ちます。

新規draftの初回保存は登録前に旧出力配置がないことを捕捉し、DBへ一度登録したIDで必要なLR2初回出力・ROOT検索ルートのディスク保存・BMT・表示を完了します。空名から求めた共通基点を旧出力先として移行しません。出力や検索ルート保存の失敗後は登録済みIDと未完了部分を保持し、同じ画面の明示再保存で捕捉済み配置を使って継続します。既存配置からの移行は旧先整理を引き続き行います。

共有操作結果記録を使うフォルダ作成・改名、譜面削除、サマリー一括変更、外部プロパティ初期化、手動再取得、任意の外部同期、JSON出力、設定の非同期処理とバックアップでは、操作後の任意表示だけを局所診断します。表示先や診断先の故障を保存・出力の結果へ加えず、例外履歴や次の保存の原因として保持しません。結果記録取得とセッションの実寿命、必要な参照・索引・確認と進捗の所有状態は維持し、表示先の故障とは区別します。受理済みTaskと各入口が所有するセッション・操作権限・受付の既存解放順を保ち、通知の実試行と必要な後片付けを終端まで待ちます。`MainWindow`は結果記録の各要素だけを表示し、空の結果記録では表示を試みません。空イベントの購読処理故障と、実際の通知内容の表示故障は分けて検証します。

再同期完了後の古い詳細行からの保存は拒否し、最新行を選んで再操作するよう通知します。同じID・hash等への旧編集の引継ぎは行わず、再同期後のlevel・URL・保存値と表示を保ちます。最新行からの明示編集は通常どおり保存できます。

詳細セルの保存は正本の現行項目に限ります。ローカル表では本体・差分の有効URLをDB原値へ材料化し、更新日時を表ヘッダーと一致させます。外部同期表の補完URLは派生値のまま保持します。所持譜面によるhash補完を含めて同じ項目を一意に保存し、bmsonの両hash・Org_md5を保ちます。level変更に必要なLR2出力も同じ受理操作で反映します。

ローカル表のURL1/URL2は、空文字または空白だけの入力で未設定へ解除できます。当該欄のランタイム補完も解除し、保存直後や別属性の保存で旧補完を復活させません。他方のURLは有効な補完を材料化し、補完がなければ保存値を保持します。非空の無効な絶対URIは画面受付・保存候補の双方で拒否します。解除後に明示した別のURL補完は通常どおり適用でき、補完を恒久的に禁止する状態は保存しません。外部同期表のURL編集制限と、memo保存時に補完を材料化しない扱いは維持します。

DB確定後のLR2/BMT出力失敗で、保存済みの表や表示を巻き戻しません。対象と元の原因を警告・エラーへ残し、現在の参照と表示は確定した内容へ揃えます。BMT生成・削除・manifest・Table URL反映の失敗も元操作の部分失敗として返します。

詳細セルはDB確定から必要なLR2/BMT出力と結果記録のセッション終端までを一つの確定後境界として扱います。設定捕捉・準備・書込みの失敗、終了によるBMT取消、結果記録取得やセッションの実寿命の失敗は、確定後の失敗分類で元例外・取消を保持します。先行のDB・出力失敗を二次結果記録・セッション失敗で置換せず、二次原因は局所診断します。任意の結果記録表示とその診断だけが失敗した場合は、保存成功を失敗へ変えず、実出力失敗や取消へ合成しません。draftは生存する元表・同じentry参照・属性名・入力値として渡します。writer内の現在値から候補を作り、DB確定までlive entry・header・版を変更しません。DB確定後に必要値・更新日時・entry版・不変の `PlaylistEntryCommitReceipt` を同じwriter内で揃え、配送キューへ登録します。DB前失敗では旧正本を保持し、DB後の出力・結果記録・セッション失敗でも登録済み差分を取り消しません。短いwriter保護の解放後に既存経路で差分を配送し、現在表示へ必要な確定値を反映します。表示コピー同士の手動同期と正本のrollbackを行いません。表示の実終端前に受理Task・受付を解放せず、編集セッションの後片付けを必ず行います。終了では受付閉鎖を維持し、それ以外の表示失敗後は次の明示編集を新しい保存値として受け付けます。

`PlaylistEntryCommitReceipt` は表、元版・確定版、対象entryの不変な旧新値・所属・順序を持ちます。フォルダ明示順の変更も同じ結果に不変値で含めます。表全体の置換は、旧表の退役と新表の再捕捉で扱います。URL補完・解除も同じentry版と差分を発行し、DBと永続化世代を変えません。単一属性保存はwriter内の最新値を基にし、URL補完後に古い表示行からmemoを保存しても非編集URLを戻しません。ローカル明示保存時だけ有効URLを材料化します。

ローカル表の `ENTRY LEVEL` は表示・F2初期値・コピーにInvariantの往復精度表現を使います。確定は `NumberStyles.Float` 相当の数値全体（固定dot、小数・符号・指数・前後空白）と有限値だけを受理し、空文字・空白だけはnull、取消は旧値を保持します。装飾、部分数値、カンマ小数・桁区切り、NaN・Infinity・overflowによる非有限値は保存前に拒否し、固定形式を既存通知receiptから表示します。モデル確定入口も同じ判定を使い、確定表示へ未保存文字列を残しません。外部JSONの `level` は別文法で、JSON numberは直接値を取得し、文字列は従来の一数値抽出だけをInvariantで読みます。org_level優先・明示null・元folder/装飾labelを保持します。所持行の保存Levelがnullの場合に譜面Levelを代替表示する既存規則も維持します。

表のproperty保存と外部属性の一括初期化も、編集するheader属性を明示した不変の`PlaylistHeaderEdit`から`CommitPlaylistHeaderEdits`を通して同じ候補・確定経路へ接続します。prefixと明示フォルダ順、対象entryのFolderをwriter内の現行値から準備し、DB確定前にはliveへ反映しません。DB後に同じentry・header・順序・版・receiptを確定し、解放後に通知します。古い全属性copyで非編集値を戻す処理や、liveを先に書き換えて全表snapshotへ戻す処理は置きません。接頭辞だけの保存では更新日時を保ち、外部データを再取得しません。

少数追加・削除は旧新キーに影響するDB候補だけを同じtransaction内で取得・差替えます。DBのNULLとliveの既存正規化、対象外の重複候補・削除済み、空フォルダのダミーを保持し、未変更行の参照や順序を作り直しません。複数表のbatchは一つのtransactionで確定し、二表目の保存が失敗しても全表の旧所属・日時・版とDBを保持します。

明示追加・移動・コピーでは、宛先の同じ所属キーについて削除履歴と生存重複を区別します。削除履歴は今回入力で置き換え、生存所属を一件成立させます。生存重複先がある場合はその既存値を優先し、同じ所属キーの削除履歴を併存させません。履歴の削除は入力のtitle・SHA-256ではなく、履歴自身の保存値を使います。同hash別フォルダ・別hashの無関係な履歴は保持し、フォルダ改名・統合の既存の衝突選択は変更しません。

所持一覧から同じ所属へ再追加すると、通常の新規追加の既定MEMOと今回の追加時刻を使い、履歴のMEMO・追加日時を復帰しません。現行プレイリスト詳細からの移動・コピーでは、現在の元行のMEMO・コメント・永続URL1/2・`name_diff`・level・追加日時を保持します。同フォルダへの詳細再ドロップでは所属・値・追加日時を変更しません。移動元の削除と宛先の履歴置換・所属成立は既存の一transactionで確定し、一回の局所変更配送へ渡します。DB前失敗では両側のDB・現在参照・値・日時・版・通知を保持し、DB後の必要出力・通知失敗では確定済み事実を戻しません。受理TaskとPは必要出力・配送・cleanupの終端まで保持します。

候補取得は同じ正規化式の非一意索引を使います。hashのNULL・空・Unicode空白だけの値は同じblankキーとし、非blank値は前後を削らず、ASCIIの大小文字だけを同一視します。従来のsetterが受理する最終LF付きhashもLFを保持した別キーです。titleのNULLと空は既存のentry identityが同値とし、通常readerがNULLを空へ読み込む場合も、raw startupがNULLを保持する場合も同じ対象を保存・削除します。状態列`is_removed`のNULLと0は同じ生存候補とし、1の削除済み候補とは分けます。startup・activeOnly reader・局所候補SQL・候補索引・LR2カスタムフォルダ生成SQLの生存判定を揃え、既存同名索引も更新します。全表保存を伴わない再出力でもNULL行を含め、LEVEL・ADDDATEの並び順に保存値を使います。保存行のNULL一括書換えやUNIQUE条件の変更は行いません。同表・同folder・NULL titleでhashだけ異なる背景16/128行を使い、正規保存時の候補SELECTの実 `SQLITE_STMTSTATUS_VM_STEP` が背景件数に比例しないことを確認します。返却行数・変更行数と `FULLSCAN_STEP` だけでは、索引の広い範囲を読む退行を検出できません。

### 参照プレイリストの索引と表示

`BmsLibraryPlaylistReferenceOwner`は選択された参照表から派生する既存索引と`indexRevision`を所有します。table/keyの件数により多重配置を数え、同じ表の一件を削除しても残るentryの参照を保持します。entry確定の旧新keyだけを反映し、name/symbol変更はその表が参加するbucketだけを更新します。一件変更のための全表捕捉・全参照keyの表示再生成は行いません。初回構築と実際の参照集合全置換では、その全体入力を扱います。

表示を構成する表順はBMT SORT昇順、同順位では正本表リスト順です。0・負値も数値順に扱い、欠損だけを末尾へ置きます。entryの編集やhydrationで表を末尾へ移しません。BMT SORTや正本表順が変わる場合も、関与するbucketだけを並べ直します。表示検索はtrimしたMD5/SHA-256を大小文字を区別せず照合し、MD5 bucketが存在すれば表示文字列が空でもそれを返します。MD5 bucketがない場合だけSHA-256へ進みます。この表示検索と、entry登録・所持候補解決のMD5優先規則を混同しません。

参照確定結果は既存版の前後と、変更keyから不変表示値への差分です。bucket削除はnull、存在する空表示はnonnullとして区別します。同じ参照owner排他内で索引・版・結果と既存publisher登録を揃え、解放後に配送を起動します。entry結果の第二キュー消費者は置かず、元の操作が返された同じreceipt群を全guard解放後に参照ownerへ渡します。参照派生状態を必要出力より先に反映し、DB後失敗でも確定値と元原因を保持して出力・配送の終端を回収します。画面への受渡しと処理済み位置は[一覧仕様](../ui/table-view.md)を参照します。

### フォルダ単位のドロップ

入力順を保ったディレクトリ単位の計画を作り、計画中は表示用の表を変更しません。既存と先に計画したフォルダ・譜面を作業集合に持ち、`GetPlaylistFolderOrgMd5sForCharts` のパッケージMD5集合とフォルダ内MD5の重なりで分類します。削除済み譜面しかないフォルダは候補から外しますが、その履歴は保持します。

複数候補は実一覧と同じフォルダ順・自然順で選びます。名前の衝突は既存の接尾辞規則で避け、同じタイトルだけ、または空の親MD5集合だけでは統合しません。同じ入力順なら一括投入と分割投入で所属と重複除去後の集合が一致します。任意の入力順の同一視や既存手動分類の移行は行いません。

通常譜面、プレイリスト詳細、解決済みの `PlayHistoryRow.ResolvedChart` を入力にできます。未解決の履歴行を含む選択は部分追加しません。

所持詳細の同表ルートへの再分類と別表コピーでは、分類用譜面と元entryの対応を保持し、通常フォルダへのドロップと同じentry複製を保存候補にします。採用するentryのメモ・コメント・永続URL1/2・`name_diff`・編集済みlevel・追加日時を保持し、表示用のランタイムURL補完を永続値へ昇格させません。移動先の同一フォルダに生存重複entryがある場合は既存entryの値を優先します。未所持詳細も元entryを複製し、ライブラリ行・解決済み履歴行は所持譜面から新規生成します。


### JSON出力・SQLバックアップと復元

JSON出力では専用ウィンドウでヘッダーとデータの保存先を同時に指定します。未入力、または正規化後のパスが大文字小文字を無視して同一なら理由を表示し、出力ボタンを無効にします。`.` / `..` の表記差も同一判定へ含め、存在しない先でも同じ条件を使います。同名でも別ディレクトリなら受理します。画面と保存入口は同じ判定を使い、保存入口では正本の遅延読込み・通知セッション・ファイル生成に先立って再検査します。取消・拒否ではDB・正本・既存ファイルを変更しません。自動改名、保存先の自動置換、外部リンク差替えまで含むファイル同一性の保証は設けません。

必要な正本入力を読取り境界内で捕捉し、`Data_url` が空の場合だけ、出力ヘッダーから選択した実データへ解決できるURI参照を出力用値へ反映します。同じドライブ・共有ルートではエスケープ済み相対参照を使い、別ディレクトリの同名ファイルや、両保存先の空白・日本語・`#`・文字列としての `%23` も区別します。異なるドライブ・共有ルートでは明示的な `file:` 絶対URIを出力します。この参照成立は別環境へ公開した場合の可搬性を保証しません。既存の非空 `Data_url` は絶対・相対とも優先し、選択したローカルデータへ置換・再基準化しません。正本の `Data_url`・`Header_url`・`Page_url` と同期元参照の意味は変更せず、シリアライズ中も正本を一時変更しません。

新規JSONの `output_date` は `yyyy-MM-dd`、`last_update` と `adddate` は `yyyy-MM-ddTHH:mm:ss.fffffff` で出力します。Gregorian・Invariantの壁時計で時刻と1tickを保持し、新たなoffsetやKindは付与しません。

入力は `yyyy/M/d`、`yyyy/M/d H:m:s`、明示offset付き旧形 `yyyy/M/d H:m:szzz` と、ISO日 `yyyy-MM-dd`・T区切り日時（時分秒必須、小数秒0～7桁、offsetなし又はZ/±HH:mm）の全体一致です。offsetなしはUnspecifiedの壁時計、ありはDateTimeOffsetのLocalDateTimeとして、月日桁数にかかわらず同じ意味で読みます。年末尾・2桁年・装飾抽出・時分秒の省略・7桁超の推測丸めは受理しません。

実採用の `last_update` と行の `adddate` だけを検査します。空・null・省略は既定値を保持し、入力 `output_date` と識別子/title等の既存採用条件を満たさない行は無検査です。不適合な非空日時が一つあれば、正常行を含む一表の取込・再読込を保存前に止め、旧DB・モデルを保ちます。独立した他表の成功は保持します。header/data解析の分類を維持し、欄名・固定形式・元データ訂正後の再実行を既存結果・通知へ届けます。手動再取得も実receiptへ通知し、STATUSへ元例外や詳細を恒久保持しません。

各JSONは保存先と同じディレクトリの一時ファイルへUTF-8・BOMなしで書き、`StreamWriter` のバッファ排出と終了、基底ストリームの終了を済ませた後、既存先は置換、新規先は移動で公開します。書込み・バッファ排出・公開の失敗では旧内容を保持し、初回失敗では不完全な本番ファイルを作りません。`Flush(true)` による電源断時の永続性は保証しません。一時ファイルの削除失敗は主原因を置き換えず、残存パスとともに診断へ残します。

選択したヘッダーを先に書き、その正常完了後にデータを書きます。書込み失敗では元原因、書込み段階（ヘッダーまたはデータ）、選択した保存先を既存の診断先へ渡すことを試みます。診断自体の失敗を分離し、一件のエラー結果記録で処理を終えます。データ失敗では新しいヘッダーと旧データ（初回はデータ不在）を保持し、部分失敗を通知します。ヘッダー失敗では後続データへ着手しません。二ファイルを単一トランザクションにはしません。補償削除や自動再試行を行わず、表示や診断の故障でもこの部分状態と`Data_url`を変えません。この処理済み書込み失敗のTask正常終端は、停止と通知の実試行が終わったことを示し、両ファイルの出力成功を意味しません。選択画面、必要な項目読込み、シリアライズ、結果記録取得・セッションの実故障を書込み失敗の`catch`へ含めません。次の明示出力は新しい二つの保存先へ通常どおり出力し、以前の書込み・表示・診断例外を持ち越しません。

バックアップは整合読取りを使い、選択先と同じディレクトリの一時ファイルへUTF-8・BOMなしで書き、閉じた後に置換または移動で公開します。先に既存先を消したり、直接上書きへ切り替えたりしません。後片付け失敗も診断に残し、成功通知は公開後だけです。書込み・公開の失敗はワークスペースで保存先と元原因を診断し、一回の失敗結果記録へ渡します。結果記録の表示失敗は局所診断で終え、公開済みバイト列や元の保存結果を変えず、表示のために失敗を再送出しません。

SQLリテラルはSQLiteの保存値と型から生成します。数値風のTEXT、空文字、空白、引用符、日本語はTEXTのまま、NULLは空文字と区別して往復します。埋込みNULやバックアップ区切りに使う垂直TABを含むTEXTは、引用時の切詰めや区切りとの衝突を避け、全byteから同じTEXTを復元するSQL式へ変換します。INTEGERとREALも値・型を保ち、文化圏の数値解釈へ依存しません。旧形式のBMT設定を補正する場合も、bmt_sortとis_bmt_outputの二列だけを更新して他列の保存値を保ちます。復元途中のSQL失敗では旧三表を保ち、songや未知の利用者表は変更しません。従来の有効なSQLバックアップも引き続き読み込みます。

復元はファイル読込み、DB復元、表示一覧への反映を分けます。DBのMonitor、トランザクション、確定後のヘッダー読込み、接続解放は同じワーカーの同期範囲で完結させます。UIへの一覧反映を予約する前にその範囲を抜け、予約の受付だけでなく完了を待ちます。譜面行は引き続き遅延読込みです。

SQL復元では、既存のカスタムフォルダ出力済み状態を三表の復元と同じトランザクションで無効化します。同じプレイリストID・ハッシュ・更新日時でも復元前の項目と同じとは限らないため、復元後の必須準備で新しい正本から `.lr2folder` とLR2の `folder` 行を作り直します。復元確定前の失敗では出力済み状態も保持します。`BmsPlaylistPersistenceLifecycleTests.PlaylistRestore_RebuildsCurrentOutputAfterPrefixOnlyChange` は、接頭辞だけを変更して出力した表を復元し、生成ファイルの検索条件が復元済み項目から実際に楽曲を返すことを確認します。

復元の受付分類は[競合ポリシー](../core/operation-concurrency-policy.md#設定全体操作終了)に従います。読込み・復元確定前の失敗は旧DBと一覧を保持します。確定後のヘッダー読込みやUI反映の失敗は復元済みDBを保持し、元原因を診断して終了不可の結果と失敗結果記録を返します。UIの途中適用は補償しません。必須読込み・出力修復も同じ受理操作のTaskとして実終端を待ち、表示用の独立した版・対象識別は維持します。

復元後に開始した必須のプレイリスト準備は、出力先同期等が失敗した場合も同じ元例外で終端します。復元処理と準備を待つ外部取込みの両方へ失敗を返し、待機を残しません。準備開始前の失敗では既存の準備状態を変えません。失敗時に成功表示、追加出力、設定画面終了、アプリ終了の許可を出しません。ワークスペースは必要公開・出力の完了までL/Pを保持し、最終結果通知より先に解放します。`Busy`・受付拒否ではバックアップを読まず、復元完了の結果を返しません。下位の失敗結果記録があれば同じ事実を再通知せず、通知失敗も元復元結果へ合成しません。実復元と必要公開・出力が成功した場合だけ終了を許可し、任意の完了・終了案内の失敗はその許可を取り消しません。必要出力後の任意URL補完の予約・診断失敗は局所で終え、成功済み復元や必須準備完了状態を失敗へ戻しません。

SQL読取りは `SQLITE_ROW` と `SQLITE_DONE` 以外を失敗とし、途中行をダンプ・復元・表示へ公開しません。読取り失敗を解放失敗より主原因にします。同期 `LoadPlaylistDump` は移行等のDB専用入口に限り、設定画面は非同期復元を使います。

## 実装とテストの対応

フォルダ改名・別フォルダの更新・NO SONG・内容あり削除から空名フォルダへの移動・空フォルダ削除から表本体への移動は [`PlaylistWorkspaceActionWorkflowTests.PlaylistFolderMutation_PreservesOrRemapsSelectionAndDetail`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) が実保存、選択requestと詳細hashを確認します。同クラスの `PlaylistPropertySave_PrefixReceiptUpdatesExistingDetailAndLaterRequests` は通常のprefix追従と後続編集、`PlaylistPropertySave_NewExplicitSelectionDoesNotApplyOldPrefixMap` は確定後の同キー明示選択を守る境界を担います。実ツリーの選択同期は [一覧仕様](../ui/table-view.md#実装とテストの対応) のWPF代表へ分担します。

[`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs) は構成・明示順の変更不能性と旧公開入力の不変を確認します。[`PlaylistEntryCommitTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistEntryCommitTests.cs) はprefix・改名確定後の公開値、DB前失敗の旧構成維持、MEMO・実行時URL補完での構成再利用を既存16/128件の仕事量・保存保証と合わせて確認します。保存済み構成の通知時点は上記property保存と [`PlaylistCatalogViewChangeTests.WorkspaceRenameFolder_UsesReceiptAndRefreshesOnlyRemappedSelection`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) が公開値とreadonly DBで確認し、実Binding終端はWPF代表へ分担します。

`PlaylistUrlCompletionTests.DetailEdit_LocalAttributesPersistThroughFreshPlaylistAndPresentation` は通常ローカル表のLevel・Url・Url_diff・comment・memoを1属性ずつ実保存Taskの終端まで待ち、現在entry、編集row、同期source、新表示row、read-only DB、新playlistと表示rowまで独立期待を照合する。新playlistは実repositoryからヘッダーを読み、実hydration ownerで対象表を読み込む。起動時の全初期化はこの保存読戻しの保証対象に含めない。未編集属性・識別子と別entryを保つ。列割当の全5属性は `CustomTableColumnFactoryTests`、F2/Enterの上位端末は `MainWindowChartPresentationWpfTests`、実効URL確定と必要LR2出力・確定後失敗は既存の詳細編集系列へ分担する。

名称差のヘッダー保存と日時維持は `PlaylistReloadMergeTests`、実DB再読込み・公開・同一入力の不要保存は `BmsPlaylistExternalReloadTests.LocalRecommendations_KeepUserStateAndAvoidUnchangedWritesThenReachLr2AndBmt` で確認します。推薦以外の手動名保持、既知ハッシュ差の既存保存範囲・日時更新も維持します。通知と三入口の表示確認は[内蔵リコメンドの対応表](local-recommendations.md#実装とテストの対応)を参照します。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| プロパティURLのnull解除、相対参照と外部同期の必須条件 | [`PlaylistPropertySaveService`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_UrlAcceptancePreservesClearRelativeSourcesAndSyncRequirements`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) は同期OFFの解除・片側指定・相対Header/Data、同期ONの必須URL・絶対基点・bmseeker例外、相対Page拒否のDB不変と有効なPageなし同期設定の保存を担う。実画面からの空欄解除・変換失敗・候補保持・訂正再保存・取消は[プロパティ画面の対応表](../ui/playlist-properties.md#実装とテストの対応)へ分担する。 |
| data転送後の参照と登録・DB再読込み | `PlaylistExternalSyncOwner.LoadExternalTableAsync` / `RegistrateExternalTableAsync` | [`BmsPlaylistExternalLoadTests.RegistrateExternalTableAsync_RedirectedDataPreservesReferencesAfterDatabaseReload`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs) は実HTTP data転送後の相対本体・差分、元headerと公開data基準を、取得表・登録済み集合・別readonly DB・新readerで確認する。転送hopごとの要求集合・回数も照合する。 |
| 解決済みdata参照だけの保存判断と同値抑止 | `PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState`、`PlaylistReloadPersistenceDecision.EffectiveDataUriChanged` | [`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) の `MergeReloadedBMSTableState_EffectiveDataUriChangeSavesHeaderWithoutDateOrEntries` は空entry・全絶対entryとHTTP path/queryのcase差で、header/state/BMT対象、entry非保存と日時保持を確認する。`MergeReloadedBMSTableState_EquivalentDataUriDoesNotPersist` は同じ実参照の相対/絶対・scheme/host・fragment・file/UNC、`MergeReloadedBMSTableState_UnrelatedSourceContextDoesNotPersist` は無関係page/header差と文脈なしの手動・内蔵表を分担する。 |
| 同本文で実data基準だけ変わる通常reloadの確定参照公開 | `PlaylistExternalSyncOwner.ReloadPlaylistTargetsAsync`、`PlaylistAggregatePersistenceOwner.TryApplyReloadedTableAsync` | [`BmsPlaylistExternalReloadTests.ReloadPlaylistTargetsAsync_RedirectedDataContextPersistsWithoutUpdatedFlag`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) は全絶対entryの実転送と通常登録・reloadを通し、UpdatedとStatePersisted、新旧表とreceipt・現行集合の一致、公開data基準、元同期元、hash・日時・ローカル属性と別DB・新readerを確認する。参照公開・詳細選択・プロパティ保存は既存receiptと新旧参照差/StatePersistedの接続を静的確認し、画面の条件網羅を重ねない。 |
| 保存生値の遅延親MD5・公開読取り・clone・JSONの同値 | [`BMSTableEntry`](../../../BeMusicSeeker/Models/Playlist/BMSTableEntry.cs) | [`BmtTableExportServiceTests.HydratedParentMd5_FirstPublicReadCloneAndJsonPreserveSavedParents`](../../../BeMusicSeeker.Tests/Playlist/BmtTableExportServiceTests.cs) がMD5あり／SHA256のみ×親0・1・複数を通常保存し、各入口ごとに別の新接続・起動hydrateを使って、一覧・保存文字列・Duplicate・再読込みsnapshot・JSONの初回読取りを固定親列と比較する。別readonly接続の生保存行で対象hash・親MD5と無関係行の不変を確認する。受理文法・null等の条件は既存 `BMSTableLoadTests` / `PlaylistUrlCompletionTests`、実gzip接続は[BMTの対応表](bmt-export.md#実装とテストの対応)へ分担する。 |
| ヘッダー一括保存の全件/一部失効拒否、空入力、非厳格互換 | `BMSPlaylist.CommitBMSTableHeadersToDB` | [`BmsPlaylistPersistenceLifecycleTests.CommitHeaders_CurrentTargetRequirementRejectsAllAndPartialRetirementButAllowsEmpty`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)。実SQLiteで失効時に生存対象だけを部分保存せず、`false`では現行対象のみ保存することを確認する。保存境界で現行参照集合を一度構築して照合する点は静的確認で分担する。 |
| 外部属性一括適用のDB後出力故障と確定表示の終端 | [`PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs) | [`PlaylistSummaryBulkEditTests.BulkExternalProperties_BmtFailureRefreshesCommittedPresentationAndPreservesPrimary`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) は実BMT台帳の読取り故障で、確定済みDB・live・参照・概要コピー・詳細とkeyword候補を保ち、guard解放後の通知終端とPの保持・解放を確認する。二次通知故障でも元の出力原因を返す。DB前失敗の成功通知抑止は同クラスの既存検証が担う。 |
| 起動entryの型保持、旧TEXT ticks通常保存と行の採否 | [`PlaylistPersistenceRepository.LoadStartupPlaylistEntries`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistPersistenceRepository.cs) | [`BmsPlaylistMigrationAndRegistrationTests.LoadStartupPlaylistEntries_UsesPlaylistIdProjectionAndKeepsRemovedRows`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) は旧TEXT schemaへ通常 `ReplaceEntry` で保存し、実typeofと新接続の最終entryでen-US/de-DE/th-THのdouble bit・1tick・原文・NULL・削除履歴・ReadOnlyを確認する。ID範囲外/REAL/不適合の除外と無効値の既定値、保存値不変も代表で接続確認する。型変換条件とstep/callback失敗は `SQLiteProviderRuntimeTests`、SQLバックアップの型保持は `PlaylistSchemaMigrationTests` へ分担する。 |
| NULL生存行のLR2再出力と保存値による順序 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) のカスタムフォルダ生成SQL | [`BmsPlaylistCustomFolderOutputTests.ReOutputCustomFolder_RawNullLiveEntriesRemainIncludedAndUsePersistedSortValues`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) はraw NULL/0/1を全保存なしで再出力し、実際の`#COMMAND`で生存行の抽出・削除済み除外・LEVEL/ADDDATEのFile/Folder順とraw NULL保持を確認する。 |
| NULL状態の局所保存とreader整合、prefix/headerの同一確定 | [`PlaylistPersistenceRepository`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistPersistenceRepository.cs)、[`PlaylistHeaderEdit`](../../../BeMusicSeeker/Models/Playlist/PlaylistHeaderEdit.cs)、[`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`PlaylistEntryCommitTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistEntryCommitTests.cs) はraw状態NULLから保存/削除後の新reader、対象外行・旧同名索引の更新とSQL仕事量を確認する。property/bulkからの配送・次のsort/filter/edit終端とDB前後の失敗は [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が担い、変換条件は [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs)、Standaloneの実保存は [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) が分担する。 |
| 参照bucket多重度、空表示の存在、BMT SORT、確定範囲と局所更新 | [`BmsLibraryPlaylistReferenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistReferenceOwner.cs)、[`PlaylistReferenceIndex`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistReferenceIndex.cs) | [`BmsLibraryPlaylistReferenceServiceTests`](../../../BeMusicSeeker.Tests/Playlist/BmsLibraryPlaylistReferenceServiceTests.cs) は0→1→2→1→0、MD5空表示とSHA fallback、表順・0/負値/欠損、旧snapshot、reset/旧clearをまたぐ連続範囲、背景16/128件の実処理を確認する。操作開始から表示公開までのSQL・参照・投影の局所性は [`PlaylistCatalogViewChangeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) の実workspace追加/削除系列が担う。実登録のDB後取消・BMT出力とP終端は [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、外部再取得のDB・旧表退役・参照結果は [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) が分担する。 |
| スキーマ・既存設定・コース | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、[`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) |
| 完成集合の共通取込み・公開構成保護・入力集合の共有排除 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs)の標準可変interface経由の変更試行、空・非空入力の後続変更、行参照・順序・所属・フォルダ状態・リビジョン維持。実登録と新主体のhydrationは内蔵表群と外部JSON登録群で分担する。 |
| SQLバックアップの値・型・所属と旧世代保全 | [`LR2SongDBExtended`](../../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs)、[`PlaylistPersistenceRepository`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistPersistenceRepository.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs)で三表の実往復、二組の文化圏、途中SQL失敗、非対象保全。ファイル公開・通知と復元の受付・UI寿命は既存workspace群が分担する。 |
| 外部取得・変更判定・表示の完了 | [`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | [`BmsPlaylistExternalLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs)、[`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) |
| DB失敗後の同一オブジェクト保持と次の正常編集 | [`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) |
| 実ドロップ・分類・編集値・参照・通知 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) のroot-drop系列は一括/分割の入力順とフォルダ順、パッケージMD5分類・接尾辞・削除履歴除外を担う。`PlaylistRootFolderDrop_PreservesEditedEntryValuesAndExistingDuplicates` は保存済み詳細の同表再分類・別表コピー・未所持詳細と重複先優先を、reader内の正本snapshotと新readonly DB接続で確認する。入力種別と履歴の解決可否は既存 `PlaylistViewPipelineTests`、実ドロップの受付・必要出力・通知終端は同workspace系列が分担する。 |
| JSON保存先の判定・専用画面・副作用前の拒否 | [`PlaylistJsonExportDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistJsonExportDialogViewModel.cs)、[`PlaylistJsonExportDialog`](../../../BeMusicSeeker/Views/Dialogs/PlaylistJsonExportDialog.xaml)、[`PlaylistWorkspaceViewModel.PlaylistExport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs) | [`PlaylistJsonExportDialogViewModelTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistJsonExportDialogViewModelTests.cs) が未入力・正規化同一・別ディレクトリを未作成と既存先で確認する。[`PlaylistJsonExportDialogTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistJsonExportDialogTests.cs) は入力Binding、説明、ボタンの有効化、picker接続と確定を代表操作で確認する。[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) はAcceptedに混入した無効先と取消による実ファイル・DB・正本の非変更を担う。 |
| JSON出力のdata参照・非空参照優先・正本保持 | [`PlaylistWorkspaceViewModel.PlaylistExport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、`BMSTable.HeaderToJson` | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) の `PlaylistWorkspaceExportPlaylistTableAsync_WritesJsonAndPreservesDataUrl` は同ディレクトリ・別ディレクトリ同名・両保存先の予約文字について、実出力参照をヘッダーの絶対file URIから解決し、選択dataの完全パスと実内容・主要項目・空receiptを確認する。`PlaylistWorkspaceExportPlaylistTableAsync_DifferentDriveWritesAbsoluteFileReference` はケース所有ドライブで明示file:文字列・絶対URI・実内容への到達を確認し、Task終端・所有確認・別名解除後に物理領域を回収する。`PlaylistWorkspaceExportPlaylistTableAsync_PreservesExistingDataReference` は非空remote・相対参照の優先と既存先置換を担い、各ケースでシリアライズ中と終端後の正本URLを保持する。保存先条件と実画面接続は前行、失敗・拒否・取消は既存ケースへ分担する。 |
| JSON出力の旧本文保全・順序・部分状態と通知・診断の故障分離 | [`PlaylistWorkspaceViewModel.PlaylistExport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、[`AtomicFileWriter`](../../../BeMusicSeeker/Models/Utils/FileOperations/AtomicFileWriter.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) は実ファイルを削除共有なしで開いて置換を失敗させ、新ヘッダー＋旧データ、旧ヘッダー＋未着手データ、初回失敗の不完全ファイル不在を確認する。元原因・段階・保存先、部分失敗通知、表示・診断故障後の終端と次の明示出力、正常JSONの主要項目とシリアライズ中の正本URL保持を担う。細かな一時書込み・公開・削除失敗と主原因・残存パスの保持は [`AtomicFileWriterTests`](../../../BeMusicSeeker.Tests/FileOperations/AtomicFileWriterTests.cs) が分担する。診断窓口への引渡しは永続ログ成功の保証ではない。 |
| 所持参照解決索引の所有・版・差分と旧捕捉値 | [`CatalogOwnedCollectionOwner.Playlist`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.Playlist.cs) | [`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryResolveIndexTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryOwnedHashTests.cs) |
| バックアップの公開結果と任意通知、復元の終了許可 | [`PlaylistWorkspaceViewModel.PlaylistBackup`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs)、[`PlaylistWorkspaceViewModel.PlaylistRestore`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs) | [`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) の `PlaylistWorkspaceBackupPlaylistAsync_PublishesSuccessWarningAndFailureReceipts` は既存内容の保護・公開済み内容と結果通知を確認する。`PlaylistWorkspaceRestorePlaylistBackupAsync_AwaitsUiCompletionAndRejectsCompetingWrites` は必須UIの終端と通知前のP解放、任意URL補完予約の成功・失敗を分担する。`PlaylistWorkspaceRestorePlaylistBackupAsync_UiApplyFailureKeepsCommittedDatabaseAndOldCollection` は確定後失敗と終了拒否、`PlaylistWorkspaceRestorePlaylistBackupAsync_RollsBackInvalidDumpAndReleasesLock` は不正SQLの巻戻しと次の明示保存を確認する。`PlaylistWorkspaceRestorePlaylistBackupAsync_ReportsReadFailureWithoutAuthorizingExit` は入力読取り失敗後のDB・モデル・公開一覧の保持、終了不可と通知前のL/P解放を確認する。実ボタン・画面閉鎖は[設定画面の対応表](../runtime/settings.md#実装とテストの対応)が分担する。 |
| 復元の排他・DB待機・UI完了・準備失敗 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspacePersistenceCommandTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) |
| 復元から新正本・表示・必須出力と次保存への正常接続 | 同上、`PlaylistWorkspaceViewModel`、`PlaylistPropertySaveService` | 同workspace系列の `PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess` は実SQL復元後の新正本・ツリー受信・summary・entry hydration、必要な実LR2/BMT出力を新readerの初期化前に観測する。新正本の次編集保存を新readonly接続で読戻し、別表・song・利用者表と管理外ファイルを保つ。SQL値・型の条件表は既存schema往復系列へ分担する。 |
| 見出し・項目の型と不正なJSONの失敗 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BMSTableLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BMSTableLoadTests.cs) |
| JSON日時の固定文法・tick・失敗境界 | [`PlaylistJsonDate`](../../../BeMusicSeeker/Models/Playlist/PlaylistJsonDate.cs)、`BMSTable`、`BMSTableEntry`、`PlaylistWorkspaceViewModel.Reload` | `BMSTableLoadTests` の固定旧/ISO・異文化・offset・空値/実不採用行・拒否条件と `DataToJson_CrossCultureRoundTripPreservesAddDate`、独立wire goldenで形式と値を照合する。`PlaylistWorkspaceExternalSourceTests.TryStartPlaylistImport_NewExternalTablePreservesFourDigitAddDateInDatabase` は実取込の保存前拒否・訂正後再実行とDBの1tick保持、`PlaylistExternalFailureBoundaryTests.ExternalSync_SourceFailurePreservesStatusAndCommittedTables` は正常行と不正行の混在、旧DB・他表の成功、通常同期/手動再取得の実通知と訂正後成功を担う。Table URLのBMT復元禁止は[専用仕様](table-url-import.md#実装とテストの対応)へ分担する。 |
| 現行詳細編集のURL・日時・ハッシュ一意性と補完・BMSON・レベルと確定前後の失敗 | [`PlaylistWorkspaceViewModel.DetailEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、[`PlaylistAggregatePersistenceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs) | [`PlaylistUrlCompletionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) はURLの材料化・非材料化とDB原値、[`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) は表・項目削除後の拒否を確認する。[`BmsPlaylistCustomFolderOutputTests.DetailEdit_ReoutputsLr2FolderRowsForLevelProjection`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) は設定取得窓口の優先、DB・LR2/BMT出力、確定後の出力・準備失敗と終了取消、確定前失敗の旧値保持と同じ入力の再保存を分担する。 |
| 詳細編集の空の結果記録と実LR2警告の配送、受付と次操作 | [`PlaylistWorkspaceViewModel.DetailTerminal`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailTerminal.cs) | [`BmsPlaylistCustomFolderOutputTests.DetailEdit_ReoutputsLr2FolderRowsForLevelProjection`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) は成功・DB確定前失敗・BMT取消での空の結果記録の購読故障と、LR2出力失敗による実警告の表示・診断故障を区別する。警告の件数・種別・見出し・表名・出力先、配送中のDB確定・編集状態・PとTaskの保持を確認し、終端後も元原因とDB・正本・入力を保つ。編集後片付けと次の明示レベル変更のDB・LR2反映も確認する。 |
| 削除履歴宛先への明示再追加・移動・コピー | `BMSTable.PrepareEntryMutation`、`PlaylistWorkspaceViewModel.AddRowsToFolder` | [`PlaylistEntryCommitTests.CommitLocalChanges_AddToRemovedDestinationReplacesHistoryAtomically`](../../../BeMusicSeeker.Tests/Playlist/PlaylistEntryCommitTests.cs) は履歴と入力のtitle・SHA-256・MEMOを変え、完全な所属集合と新DB読取りで履歴置換・無関係履歴保持を確認する。宛先INSERTの実trigger拒否では元所属・履歴・DB・現在値・日時・版・未配送を保持する。 [`PlaylistWorkspacePersistenceCommandTests.PlaylistDrop_RemovedDestinationUsesCurrentInputAndCompletesRequiredOutputs`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) は実再同期で履歴化した宛先への所持再追加・同表移動・別表コピーについて、入力元ごとのMEMOと追加日時・編集値、新readonly接続、参照表示、実songへ適用したLR2の利用者フォルダ/ALL COMMAND、実gzip BMTの生存所属一件、同folder再dropを確認する。生存重複先優先とroot分類は既存root-drop系列、Busy・確定後失敗・Pの終端は既存Admission系列、局所仕事量・複数表原子性・改名統合は既存EntryCommit系列が分担する。 |
| entryのdraft・確定版・旧捕捉値・局所SQLと複数表原子性 | `PlaylistAggregatePersistenceOwner`、`PlaylistPersistenceRepository` | [`PlaylistEntryCommitTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistEntryCommitTests.cs) は実SQLiteの失敗・NULL/重複キーと背景16/128行で実捕捉/所属変更/SQL対象を確認する。表示・出力終端は既存詳細編集系列、正規URL補完/解除の不変差分は `PlaylistUrlCompletionTests` が分担する。 |
| ENTRYの固定Float全体入力と精度保持・拒否 | [`BMSTableEntry.ApplyEditDraft`](../../../BeMusicSeeker/Models/Playlist/BMSTableEntry.cs)、`PlaylistDetailSourceRow`、`PlaylistAggregatePersistenceOwner.CommitEntry` | `PlaylistEntryCommitTests.CommitDraft_LevelUsesSavedNumberAndKeepsOtherValues` は異文化の往復精度・Float全体・空null・拒否条件を実ownerと新DBで確認し、非編集値・日時・版・索引・receiptを保つ。実F2/Enter/Escape・拒否通知・次の正常保存は[一覧仕様](../ui/table-view.md#実装とテストの対応)、複数表示経路・再読込みは `PlaylistUrlCompletionTests.DetailEdit_LocalAttributesPersistThroughFreshPlaylistAndPresentation`、必要出力・失敗・受付は既存詳細編集系列へ分担する。外部levelの別文法は `BMSTableLoadTests.LoadDataJson_ExternalLevelKeepsItsOwnGrammarAndOriginalFolder`、同じsource hashの旧誤読値の明示reload訂正は `BmsPlaylistExternalReloadTests.ReloadPlaylistTargetsAsync_SameSourceHashRepairsOldRegionalLevel` が担う。 |
| URL1/2の空欄解除、有効置換、非編集欄保持と明示補完の再適用 | `BMSTableEntry.TryParseEditedUrl`・`ApplyEditDraft`、`PlaylistWorkspaceViewModel.CompleteDetailEdit` | [`PlaylistUrlCompletionTests.DetailEdit_LocalUrlClearOrReplacePreservesOtherUrl`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) は両欄の空文字・空白・有効URIと他方の補完有無について、実保存・別属性保存後のDB・正本・実効表示を照合する。`DetailDraft_NormalUrlCompletionThenMemoCommitUsesLatestInput` は正規補完配送と解除後の別候補適用・再保存を確認する。非空無効URIは同系列の `DetailEdit_InvalidUriPreservesSavedEntryAndReleasesEditing` が画面受付を、`PlaylistEntryCommitTests.CommitDraft_InvalidUrlPreservesCurrentValues` が画面を経由しない保存入口の拒否と旧補完・保存値・版の保持を分担する。実セル配送・Escapeは[一覧仕様](../ui/table-view.md#実装とテストの対応)が担当する。 |
| 項目の題名の永続化 | [`LR2SongDB`](../../../BeMusicSeeker/Models/LR2/LR2SongDB.cs) | [`Lr2PlaylistEntryPersistenceTests`](../../../BeMusicSeeker.Tests/Playlist/Lr2PlaylistEntryPersistenceTests.cs) |
| 外部再取得の出力失敗・部分成功・実置換後の通知失敗と次操作 | `PlaylistExternalSyncOwner`、`PlaylistAggregatePersistenceOwner` | [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の`ReloadPlaylistTargetsAsync_OutputFailureReturnsConfirmedReferencesAndSeparateTargetFailures`、`ReloadPlaylistTargetsAsync_CollectionNotificationFailureRetainsActuallyAdoptedReference`。変更判定・日時・ローカル値は既存再取得・mergeテストと分担する。 |
| 再取得対象の逐次サマリー採用と全体終端 | `PlaylistExternalSyncOwner`、`PlaylistWorkspaceViewModel.Reload`、`PlaylistWorkspaceViewModel.ExternalPlaylistSync` | [`BmsPlaylistExternalReloadTests.PlaylistWorkspaceReload_PublishesEachSummaryBeforeOtherTargetsComplete`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) は手動・任意同期で、Bの実HTTP本文待ち中にAの新参照・日時・成功STATUSとCの旧状態・失敗STATUSを実Dispatcher上のsummaryから確認する。全件終了までのP・Task・詳細再構築の保持、終端一回の詳細要求、次操作と個別採用の二重計上防止を確認し、必須summary要求購読先の例外も終端へ保持する。 |
| 手動単表・任意複数表の元BMT失敗と参照採用後の必要詳細公開失敗の保持 | [`PlaylistWorkspaceViewModel.Reload`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Reload.cs)、[`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | [`BmsPlaylistExternalReloadTests.PlaylistWorkspaceResync_PostAdoptionPresentationFailureRetainsOutputFailureAndConfirmedState`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)の二行で、実台帳書込み失敗と必要な`PlaylistDetailReloadRefreshRequested`の実失敗、公開中P/Task保持、進捗終端と次要求を確認する。任意の操作結果記録表示失敗はこの二次原因の保証へ含めない。 |
| 進捗表示故障時の対象成否・保存結果・後続購読先への終端 | [`PlaylistWorkspaceViewModel.SyncProgress`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs)、[`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | [`PlaylistExternalFailureBoundaryTests.ExternalSync_SourceFailurePreservesStatusAndCommittedTables`](../../../BeMusicSeeker.Tests/Playlist/PlaylistExternalFailureBoundaryTests.cs) は実JSON・SQLiteで、開始・進捗・参照反映済み・完了の表示先が失敗しても、旧表保持、別表確定、正確な失敗状態、独立した購読先への終端、受付解放と次の全件成功を確認する。 |
| 任意外部同期の結果表示失敗と実同期結果・完了事実の分離 | [`PlaylistWorkspaceViewModel.ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs)、[`PlaylistWorkspaceViewModel.Mutations`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs) | [`BmsPlaylistExternalReloadTests.ExternalPlaylistSync_ReceiptDisplayFailurePreservesCommittedResultAndNextRequest`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) は、実推薦の実力変化による情報通知の内容・種別・見出しと、BMT成功・台帳保存失敗の両条件で表示・診断故障を確認する。必要出力の警告と終端の情報通知を区別し、配送中の実DB・現在の表と詳細参照・ライブラリ索引、PとTaskの保持、完了の未公開を確認する。初回の有効1行・削除済み1行、次の明示変更の有効1行・削除済み2行を表とDBの完全な集合で照合し、有効行だけを索引とBMTへ反映する。台帳保存失敗でも先行生成済みBMTと旧台帳原本を保持する。通知故障を同期結果へ加えず、必要出力の元原因・進捗終端を保つ。実力不変で空の結果記録なら表示しない。削除履歴の一般分岐は [`PlaylistReloadMergeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) が分担する。 |
| 実通知のメイン画面接続・表示内容・所有ウィンドウ | [`MainWindow.PresentPlaylistOperationNotifications`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`PlaylistWorkspaceViewModel.ReportBmtOutputFailures`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `RecommendationReload_ThreeEntriesPublishAutomaticNamesAndPresentOnlyNumericSkillChanges` は実推薦の情報通知と所有画面を確認する。`PlaylistPropertyDialog_ExternalReplacementBmtFailureKeepsOwnedDialogAndResavesSameId` はBMT警告の表示成功・失敗で、表示試行、見出し、出力先・台帳パス・元原因と所有画面を確認する。後続のエラー通知、元Taskの終端、画面・DB保持と同IDの再保存を保つ。警告表示の故障診断はBMT管理主体が担い、再読込み診断へ重複計上しない。 |
| 操作tokenの全体取消し、別対象の確定参照・receipt保持と全開始Task終端 | `PlaylistExternalSyncOwner` | [`BmsPlaylistExternalReloadTests.ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)で実JSONの置換確定と実HTTP本文待機を分け、scheduler完了までP・進捗を保持し、取消対象のDB非変更と次要求を確認する。参照採用後のsort通知失敗を重ねる行では、取消原因と二次失敗も保持する。[`PlaylistRecommendedTableOwnerTests.ReloadPlaylistTargetsAsync_LocalReadFailurePreservesDataAndExitsUpdating`](../../../BeMusicSeeker.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs)で通常対象失敗との分類とDB・memo・通知非変更を分担する。UI scheduler由来の中断は既存ケースを維持する。 |
| 新規draftの初回登録と出力失敗後の同ID再保存、同時編集の三境界 | `PlaylistPropertySaveService`、`PlaylistPropertyEditSession` | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)の`CreatePlaylistPropertyDialog_OutputFailureResavesRegisteredDraftWithoutDuplicateId`と`PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges`。属性・参照・本番項目追加の版を観測する。 |
| LR2初回出力の通常・追加・ROOT、出力/XML保存失敗後の同ID継続と基点保全 | `PlaylistPropertySaveService`、`BMSPlaylist.ReOutputCustomFolderWithSettings` | [`PlaylistWorkspaceActionWorkflowTests.CreatePlaylistPropertyDialog_FirstLr2OutputPreservesBasesAndResavesSameId`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)は実draft、実ファイル・folder行・再読込みXML・BMT台帳・現行表を確認する。DB前/BMT失敗は前行、既存移行の旧先整理は`BmsPlaylistMigrationAndRegistrationTests`が分担する。compiled名前Binding・実保存ボタンと閉鎖は[`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyNewDraft_CompiledNameAndSaveCompleteFirstLr2Output`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)が担う。 |
| 借用した読取りの解放、参照先の確定と操作の受付 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) |

設定画面のBMT出力はBMT組の明示確定から全表再更新し、通常・ルート出力先はフォルダ選択と既存確認から、追加先は追加・削除・改名から必要な変更だけ確定して既存移行を実行します。両者は[設定変更](../runtime/settings.md)の局所保存単位に従い、他セクションの候補や通常項目保存を出力の開始条件にしません。

## 関連資料

[LR2出力](lr2-custom-folders.md)、[BMT出力](bmt-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[データと索引](../core/data-and-indexes.md)を参照します。

詳細セルの旧行拒否と再操作は [`MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が、実再同期、小DB、実MainWindowの通知購読と所有windowを接続して確認します。無効URIの編集は [`PlaylistUrlCompletionTests.DetailEdit_InvalidUriPreservesSavedEntryAndReleasesEditing`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) が、実編集Taskの終端後の編集解放、行・正本・read-only DBの元URLと未編集属性の保持を確認します。取消・通常編集は既存 `PlaylistViewPipelineTests`、一般の永続化・表示参照更新は `BmsPlaylistPersistenceLifecycleTests` が分担します。
