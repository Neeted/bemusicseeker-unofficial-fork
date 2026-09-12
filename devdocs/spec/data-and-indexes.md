# Data And Indexes

この資料は、現行実装で正本として扱うデータと索引をまとめる。

データ規模、処理速度の優先順位、差分更新の仕事量、性能受入は [performance-and-scale.md](performance-and-scale.md) を正本とする。表・索引の所有権を変更するときも、全件の cardinality と更新差分を別々に評価する。

## Catalog

所持 catalog は BMS と BMSON を含む。

- BMS
  - LR2 互換の `song` / `folder` を基礎にする。
  - DB 上の storage row は LR2 `song` row である。現行 in-memory owner は `BMSFile : LR2SongDB.song` だが、chart 共通の `ChartFile` domain/read model とは別に、LR2 `song` table への永続化型として扱う。
  - SHA-256 などの拡張情報は app 側の補助 table / map で扱う。
- BMSON
  - `bmson_song` を app 側 catalog として使う。
  - 現行 storage row は `LR2SongDBExtended.bmson_song` であり、`song` table には入れない。
  - BMS と同じ LR2 再生 capability を持つとは扱わない。
- digest / metadata
  - `chart_digest_map` は chart identity と chart_info hydration の橋渡しに使う partial cache である。
  - `chart_digest_map` は app schema repair 直後や初回 scan 前に完全である必要はない。missing SHA-256 は file diff / install / inline chart_info / chart info backfill など、譜面 bytes を読む処理で必要範囲を補完する。
  - `chart_info` と current parse failure は startup background hydration でsession indexへ適用する。currentnessは `current chart_info > current parse failure > parse candidate` の順で判定する。
  - LR2 linked / standalone は同じ actual-data hydration を使い、`lr2_song_db_sync_status` はchart-info currentnessの入力にしない。
  - inlineのBMS candidateは基本解析結果とchart-info projectionを含むgenerated rowを保存する。full backfillのsuccessまたはexisting-current reuseは、既存`song` rowのchart-info由来9列だけをupdate-onlyで投影する。duplicate MD5は一度のevaluation結果を全BMS pathへ適用する。
  - BMSON candidateは`chart_info`とsession indexを更新するが、LR2 `song` rowや`chart_digest_map`を作らない。

`ChartFile` は、この catalog storage row ではなく、BMS / bmson の Kind と storage owner を持つアプリ内 domain/read model を指す。DB 正本は BMS 用 `song` row と bmson 用 `bmson_song` row の二本立てを維持する。

identity、parse failure、session all-current snapshotの詳細は [chart-info-lifecycle.md](chart-info-lifecycle.md) を参照する。

## Startup DB Projection

install readiness の critical path では、導入先推定に必要な catalog projection だけを読む。

- path / hash / timestamp / folder / installed membership。
- chart digest / bmson catalog。
- maintenance 全件と chart_info 全件は critical path に戻さない。

詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## Resource Index

通常起動の destination resource index は file enumeration から作る。

- 正本:
  - runtime owner は `LibraryResourceIndexOwner` とする。
  - owner が current `LibraryResourceIndex`、対応する `DirectoryResourceLookupCache`、runtime generation を一体で所有する。
  - file-scan replacement、move / rename、whole-folder delete、install、merge は owner の replace / mutation command を通す。長寿命 owner / service は cache instance を保持しない。
  - snapshot は index / directory cache / generation の対応を atomic に捕捉し、公開後の directory/resource mapping は後続 mutation で変化しない。変更 command は current cache を copy-on-write し、新 index/cache/generation を一括 publish する。変更 receipt は mutation result と変更後 snapshot を返し、実変更時だけ generation を進める。`Replace` に渡した完成済み index は owner へ ownership transfer され、呼出側は publish 後に直接変更しない。
  - directory entry は immutable `Entry` と dictionary root を共有し、unpublished clone の初回entry変更時だけrootをdetachする。入力配列は ownership transfer が明示された native canonical build を除いて複製し、呼出側 alias を保持しない。`Entry` の内部 backing 配列も assembly consumer へ公開しない。
  - resource reverse lookup は所有権移転された不変の初期baseと、keyごとの最新値を持つ構造共有の差分mapで構成する。forkでbaseを列挙・全コピーせず、初回mutationにも全件変換を持ち込まない。差分の更新は変更keyへのtree経路とcandidate配列だけを置換し、前世代への参照chainを持たない。lookupは差分とbaseの二層で、世代数分を辿らない。ownerのpublish前とlazy結果の格納完了時に計算済み変更nodeをfreezeし、次操作へそのfreeze処理を持ち越さない。
  - 最終candidate列が順序も含めて同一のbucketは逆引きを書き換えない。directory entryの変更（空resource、SelfOwnedのみ等）は別に判断する。SelfOwnedのみの変更でも、従来互換のremove-then-addで候補順序が変わるbucketは書き換える（例: `[A, B]` のAを更新すると `[B, A]`）。末尾候補の更新等で最終列が同じなら書かない。カテゴリの参照集合が同じことだけでは無書込を保証しない。候補順序、大小文字比較、hash=0、full/lazy、未cachedと空候補の区別は維持する。`updatedHashes` は従来のremove/add遷移の集計であり、正味のbucket書込回数とは異なる。
  - 初期baseの置換前payloadはindexの寿命内で保持し、差分は同じkeyの履歴ではなく最新値だけを持つ。明示scan/replacementや既存の全逆引きinvalidateでbaseも置き換わる。自動compact、操作後への必須更新の遅延、世代台帳は追加しない。差分が長期に増えた場合の処理速度は別途の未測定事項とする。
  - whole-folder deleteは物理削除が成功した `DeletedFolderPaths` を一つのowner commandへ渡し、全成功分を一度だけpublishする。失敗・未実行のフォルダは取り除かず、重複/親子のentryを二重計上しない。入力列挙/反映失敗は旧snapshotを維持するが、先行FS削除が取り消されたとは扱わない。
  - installはpackage単位の確定・公開境界を維持し、後続packageの処理から先行成功分のresource候補を参照可能にする。途中の `ManualRecoveryRequired` では公開済みの成功prefixを保持し、その失敗packageと未実行suffixの候補を追加しない。これは失敗packageのFS残存物が取り消されたという保証ではなく、回復済み失敗の後続継続可否も既存のbatch契約に従う。
  - merge の source subtree removal と destination scan addition は単一 owner command で unpublished clone に適用し、combined receipt として1回だけ publish する。途中で入力列挙または mutation が失敗した場合は例外を伝播し、旧 snapshot / generation を維持する。remove と add の最終 mapping が更新前と同一なら no-op とし、snapshot identity と generation を維持する。
- key semantics:
  - chart-relative resource key。
  - `foo.wav` は `foo`。
  - `sound/foo.wav` は `sound/foo`。
  - basename-only matching は使わない。
- category:
  - audio
  - image
  - movie
- reverse lookup:
  - resource-key -> candidate chart directory。
  - install readiness 前に完成している。
  - pending package batch 側へ lazy build を持ち越さない。

pending install destination の background 推定は resource generation、owned collection version、installed-directory lookup generation、digest mutation generation を currentness stamp として保持する。installed lookup の snapshot / generation 公開、digest mutation window の begin / end、推定結果の currentness 検証 / entry 適用は同じ狭い同期境界を通す。準備済み installed-directory 解決、installed / missing partition、source-derived state は準備 stamp が処理開始時の composite stamp と一致する場合だけ一体で再利用し、不一致なら current partition / context を同じ read boundary で再構築する。評価結果も stamp が current で digest mutation window が閉じている場合だけ適用する。準備または評価の後に入力が変化した場合は current snapshot で同じ段階を最大1回再評価し、retry の installed / missing partition、pending membership、resource snapshot、installed lookup、currentness stamp は同じ read boundary で一括捕捉する。再評価中にも変化した場合は destination / warning を書き込まず明示的に skip する。generation は runtime-only であり DB schema や install row へ保存しない。background batch が terminal success、stale skip、exception、cancellation のいずれで終了しても、dispatch 時に `SEARCHING` を立てた元 entry 集合を必ず解除する。

native bridge path では `EBridge_ScanChartAndResources` の packed result から直接 resource index を作る。`ChartScanResult` は chart paths / chart directories の carrier として使い、resource dictionaries は通常起動 main path では materialize しない。

Everything unavailable 時の managed fallback scan とテスト用 merge path では、`ChartScanResult` が category 別 resource dictionary を持つ。

### 規模を伴う cache / snapshot の改修

上記の旧世代不変・atomic publish は維持するが、全件コピーを追加する根拠にはしない。改修時は [性能要件 section 3](performance-and-scale.md#3-規模を踏まえた設計要件) に従い、no-op 前の root コピー、空カテゴリの detach、package / folder ごとの全 root 複製を確認する。consumer が必要とする範囲の不変 facts、世代の再利用、既存の安全な境界内でのバッチ化を検討する。これらの性能条件が既存の全経路で達成済みという意味ではない。

### Verification map: resource-index mutation

`DirectoryResourceLookupCacheTests` は小規模のentry/3カテゴリ/SelfOwned、full/lazy/空候補、候補順序、旧snapshotと独立membership factsを確認する。SelfOwnedのみの変更は単一候補に加え、複数候補の先頭/末尾更新を区別し、順序変更時の実書込と最終列が同じ場合の無書込を確認する。`ResourceReverseLookupMapTests` は列挙禁止のowned read-only baseを実際の格納部品へ渡し、初期受取/fork/実変更がbase全件の列挙・コピーへ戻らないこと、双方向の世代分離を確認する。`LibraryResourceIndexOwnerTests` は成功subtreeの一括公開・entryコピー回数・例外時非公開を確認する。`OwnedChartCollectionLibraryMutationTests` と `BmsLibraryPackageInstallServiceTests` は実library command、一時DB/ファイル、既存FS fakeを通して部分削除失敗およびリソース同梱の推定先/強制導入を確認する。導入成功ケースは2 package目のsource copy直前にsnapshotと3カテゴリの候補列を捕捉し、先行公開と中間snapshotの不変性を区別して確認する。既存の推定先/強制 `ManualRecoveryRequired` ケースにはpackageごとに異なるresource keyを与え、成功prefixだけの候補保持、失敗分/未実行分の非混入、旧snapshot不変を確認する。

このcoverageはFunctionalの振る舞いテストであり、wall-clockの閾値・本番規模fixture・外部Everythingは要求しない。`LibraryResourceIndexTestSupport` のreflectionは既存ownerのsetupとsnapshot観測に限定し、private workflowを呼ばない。対応する診断APIができた場合にこの例外を退役する。instance-localのentryコピー/bucket書込observerは実処理直後のテスト観測専用で、通常運用は未設定とする。実行有無は[実装記録](../plan/install-delete-resource-index-p0.md)に分離し、追加テストの存在をpassや速度保証とは扱わない。

## Resource Ownership

resource ownership は chart-directory keyed に再集約する。

- aggregate ownership
  - ancestor chart directory から見える resource を含む。
- self-only ownership
  - 最も近い chart directory が所有する resource だけを含む。

health / install estimation / maintenance は category 別 chart-relative key を使う。旧 all-resource union や basename-only fallback は正本にしない。

## Maintenance And Resource Health

`BMSFile.maintenanceInfo` は常に valid snapshot とは限らない。

- valid snapshot:
  - DB 由来の `DbHydrated`
  - file diff / install / manual rescan 由来の `Calculated`
- placeholder:
  - 起動直後や未 hydration owner の暫定値。
  - resource health index の正本として扱わない。

通常起動では全譜面の resource file existence を再検証しない。persisted maintenance snapshot を hydration し、必要な missing/stale target だけ deferred maintenance で補完する。

## Playlist And Score Data

- playlist header は startup early phase で読む。
- playlist entries は startup background task `playlist_entries_hydration` で読む。
- score DB load は startup early phase で行い、LR2ID 確定後に LR2IR player score XML prefetch を開始する。
- player score XML は要求開始から本文受信完了まで単一の30秒予算とし、ヘッダー受信後に期限を再開始しない。終了要求は本文待機にも伝播する。
- 同じ player / score DB の prefetch は失敗結果も後続の ranking refresh へ渡し、失敗後の即時再取得はしない。通信失敗・期限超過・終了キャンセルでは既存 `ir_score`、digest metadata、live score を保持する。正常に取得した空スコアと取得失敗を区別する。
- 期限超過・キャンセル・取得不能は IR 結果の型と通常ログで区別し、startup ranking refresh の background status に取得失敗を反映する。新しい modal dialog や自動 retry は追加しない。
- ranking refresh / score hydration は install readiness blocker ではない。

LR2 ranking 系は 2 table に分かれる。

- `ir_score`
  - LR2IR player score XML 由来。
  - 未送信検出と `UNSENT SONGS` に使う。
  - normalized digest は LR2IR XML の score 実体を対象にし、hash 側更新時刻として揺れる `lastupdate` は無視する。
- `ir_data`
  - LR2IR local ranking cache XML 由来。
  - ranking 表示と offline score ranking estimation に使う。
  - 起動時の ranking cache refresh は `UpdateLr2IrRankingCacheOnStartup=true` の場合だけ行う。false の場合、起動時には local ranking cache XML の scan / reload / `ir_data` upsert を行わない。
  - XML reload は hash cache file の mtime / tail `lastupdate` で判定する。
  - startup refresh、manual download、`LR2IRCache` wrapper は同じ ranking cache XML parser を使う。
  - refresh path は full ranking list materialize を避け、valid `<score>` rows を 1 pass summary parse する。`id`、`clear`、`notes`、`combo`、`pg`、`gr`、`minbp` は 0 以上の整数だけを valid とし、不正 row は集計対象から外す。
  - `EstimateOfflineScoreRanking=true` の場合、offline score ranking estimation は必要時だけ同じ parser の compact rank calculator を on-demand load する。startup refresh で reload 済みの hash はその lookup を再利用する。
  - 初回構築では対象 LR2ID の既存 row が DB 上も 0 件であることを transaction 内で確認し、dedupe 済み rows を bulk insert する。incremental 更新は従来通り `(hash, lr2id)` 単位の delete + insert upsert を使う。
  - schema 互換のため unique 制約は持たない。index は既存 `ir_data_idx(lr2id)` に加え、非 unique `ir_data_idx_lr2id_hash(lr2id, hash)` を持つ。

### Verification map: IR 取得

`AppHttpClientTests` は固有 loopback socket のヘッダー・本文 phase を制御し、本文までの単一期限と応答待機中の外部キャンセルを検証する。`BmsLibraryIrServiceTests` は固有 DB で失敗 prefetch の再取得禁止、既存データ保持、成功 prefetch と digest 互換を検証する。`BmsLibraryIrStartupTests` は captured options と IR client、存在しない Everything bridge の composition から `InitializeStartup` / `RequestShutdown` を通し、live score 保持、失敗 status、通信と ranking の drain を検証する。いずれも Functional（IR fixture は BmsLibrary shard）に属し、正常完了は request Task / client signal / ranking state transition、timeout は HTTP 契約または cleanup watchdog に限定する。

## DB Access

startup hydration の read phase は read-only connection を使う。

DB / index 改修では、query 件数だけでなく実際の読込・materialize row 数を代表規模で確認する。局所 path / hash の要求を全 table の読込と再索引化へ広げず、必要な projection と既存 lookup / 集合更新を使う。初回 full load と操作ごとの増分処理は別に測る。具体的な規模・受入方法は [性能要件](performance-and-scale.md) に従う。

- read-only loader は schema ensure / repair を行わない。
- write が必要な cleanup / backfill / metadata update / file diff commit は write-capable transaction path に分ける。
- `app_schema_version(name='app_schema')` は startup app schema repair で収束させる。

DB commit failure を再試行の no-op で成功へ変換せず、失敗後の追加 rollback / 診断で primary failure を置き換えない。共通 transaction 境界の現行契約は [file-db-consistency.md の SQLite transaction 失敗伝播](file-db-consistency.md#31-sqlite-transaction-の失敗伝播) を正本とする。

chart-info storage writeは`CatalogMutationOwner`が所有する。immutableな`CatalogChartInfoStorageWriteRequest`にinline用BMS/BMSON storage rows、full-backfill用narrow song projections、chart-info factsを束ね、同じcatalog transactionで保存する。full backfillは`Lr2SongDbWriter.UpdateChartInfoSongProjections()`でpathとMD5が一致する既存rowの`level`、`difficulty`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes`だけを更新し、missing rowをINSERTしない。基本列とuser管理列はUPDATE句へ含めない。commit成功後だけcanonical owner、digest-derived index、session index、warning/digest publicationを進め、失敗時はどれも部分更新しない。storage rowを伴わないparse-failure明示削除にはfacts-only writeを残す。

`song` table は LR2 互換の lookup index を前提にする。LR2 が作成する `song.db` と同様に `hashidx(song.hash)` と `parentidx(song.parent)` を ensure し、アプリ側で使う `song_idx_folder(song.folder)` も維持する。スタンドアローン DB 作成時だけでなく、通常の DB schema ensure でも不足 index を補う。

app schema preflight は、警告が必要な修復と警告不要の初回準備を分ける。

- warning target:
  - 既存 `playlist_entry` に `sha256` column / index を追加する。
  - 既存 `playlist_entry_idx_uniq` を `sha256` 込みへ作り直す。
  - 既存 app-owned schema がある状態で `app_schema` version row を記録または更新する。
  - version row が無い状態で既存 `chart_digest_map` / `bmson_song` があり、app-owned schema/data を現行化する。
- no-warning startup preparation:
  - LR2 `song.db` に playlist tables が無く、初回連携用に追加する。
  - `chart_digest_map` / `bmson_song` / `app_schema_version` が無く、初回連携用に追加して `app_schema = 1` を記録する。

`EnsureAppOwnedSchema()` は playlist / bmson / chart_info / IR / lookup index を現行 schema へ揃え、`app_schema = 1` を記録する。既存 `chart_digest_map` の row を保持しながら schema 正規化と current version stamp が必要な場合は `RepairAppOwnedSchema()` を使う。どちらも `song` table 全件から実ファイルを読んで `chart_digest_map` を全量補完しない。metadata bundle manifest の `chart_info_schema_version` は import/export 互換値であり、DB 内 `app_schema` version とは別物である。

設定ダイアログの「BeMusicSeeker関連データをLR2データベースから削除」は、LR2 native tables (`song`, `folder`, `score` など) は保持し、`LR2SongDBExtended.BeMusicSeekerOwnedTableNames` に列挙した app-owned tables だけを drop する。app-owned table の AUTOINCREMENT 由来 `sqlite_sequence` row と、LR2 native table 上に作る app-owned index (`song_idx_folder`) も削除する。初期化・reload・background 更新中は実行不可とし、実行中は設定ダイアログ操作を無効化する。成功後はアプリを終了するため、uninstall は通常運用中の差分更新ではなく終了前の破壊的な単独操作として扱う。新しい app-owned table や native table 上の app-owned index を追加する場合は、この一覧と uninstall regression test も更新する。

## Consistency Updates

file diff / install / merge / delete / move 後は、必要な範囲で次を同期する。

- memory catalog
- LR2 song DB / app extension tables
- `bmson_song`
- `chart_digest_map`
- inline / full `chart_info` facts と対象 storage projection
- inline or deferred maintenance snapshot
- `LibraryResourceIndex`
- `DirectoryResourceLookupCache`
- playlist references when affected

増分更新では、旧 union cache ではなく `LibraryResourceIndexOwner` が所有する current `DirectoryResourceLookupCache` の directory key set を正本にする。filesystem mutation は resource owner の lock 外で完了させ、成功後に current index へ command を適用する。file-scan replacement は owner 自体を差し替えず、owner の `Replace` で current generation を更新する。

chart-info storageでは、inline用BMS generated rowsまたはfull-backfill用update-only projectionと`chart_digest_map` / `chart_info` / parse-failure factsを同じtransactionに入れる。full backfillは既存songの基本列を再生成せず、missing rowもmaterializeしない。BMSONは`bmson_song`と`chart_info`を正本にし、LR2 `song`へ互換rowをmaterializeしない。metadata cacheとしてownerのない`chart_info` rowを保持する契約も維持し、backfillを理由に全件削除しない。

file diff の大量削除は、削除対象 path / hash を一時 table に集め、`song`、`maintenance`、`bmson_song`、`chart_digest_map` を集合 SQL で更新する。`chart_digest_map` は削除対象 MD5 のうち、削除後の `song.hash` に残存 owner が無いものだけを削除する。これにより、ルート削除やルート近傍 rename のような大量 delete でも 1 row ごとの orphan check に戻さない。

局所 catalog mutation の PathCleanup も同じ契約を使う。`BmsLibraryDbGateway` は確定済みの exact cleanup key と削除 owner の現在 path を path temp set に集約し、必要な削除前 `song.hash` と owner hash を hash temp set に集約してから、`song`、`bmson_song`、`maintenance`、`chart_digest_map` を同一 transaction 内で更新する。存続する relocation owner の destination は exact key で保護し、maintenance-only path は対応する `song` が無くても削除する。digest は削除後の残存 `song.hash` を再確認し、残存 owner がある候補を保持する。BMSON の cleanup は `bmson_song` と `maintenance` に限定し、LR2 `song` や `chart_digest_map` の owner を作らない。PathCleanup は対象集合だけを扱い、catalog 表の全行 materialize と hash ごとの孤児確認を行わない。BMSの削除前hash収集はpath temp setを外側に固定してsong.path主キーへ限定し、hash条件でhashidx全体を走査しない。この契約の実接続・処理量・query plan は `CatalogMutationOwnerTests.ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership` で確認する。実接続のPROFILEではFULLSCAN_STEPに加えてVM_STEPを記録し、背景行の増加で全statementのVM仕事量が増えないことを確認する。query planは空のtemp表を再構成した補助診断として扱う。
