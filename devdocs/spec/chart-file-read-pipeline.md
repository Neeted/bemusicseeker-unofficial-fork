# 現行の譜面ファイル読み込みパイプライン

この資料は、`2026-05-09` 時点の譜面ファイル読み込み、軽量 metadata、maintenance、`chart_info` 生成の正本仕様をまとめる。実装中の初期化軽量化では、この資料の責務分離に合わせて差分ファイル由来の処理と DB 由来の background 補完を分ける。

目的は、追加・更新譜面で同じファイルを軽量 parser と `chart_info` parser が別々に読む状態を避け、どの入口を使うべきかを明確にすること。

## 基本方針

- 新規コードで譜面 bytes と digest が必要な場合は `ChartFileSnapshot` を使う。
- 大量処理では reader が `ChartFileContentReader.ReadBuffer(path)` で bytes と file metadata だけを読み、worker が `ChartFileContentReader.CreateSnapshot(buffer)` で digest 付き snapshot を作る。
- path だけを持つ小さな処理や互換 API では `ChartFileContentReader.ReadSnapshot(path)` を使ってよい。
- snapshot には `Path`, `Bytes`, `Length`, `LastWriteTimeUtc`, `Md5`, `Sha256` が入る。
- `ReadSnapshot(path)` は互換入口として `ReadBuffer(path)` + `CreateSnapshot(buffer)` を行う。同じ bytes から digest を作り、hash のために同じ譜面を再 read しない方針は維持する。
- snapshot bytes は処理中だけ保持し、DB や長期 model へ保存しない。
- `ChartFileSnapshot` は bytes 読み取り結果であり、`ChartFile` domain/read model とは別責務である。`ChartFile` は storage row でも snapshot でもない。
- lightweight parser と `chart_info` parser は統合しない。同じ bytes を使うが、役割は分ける。
- 新規・更新ファイル由来の補助情報は、snapshot が生きている間に作る。
- DB に既に存在する owner 由来の補助情報だけを background hydration/backfill へ回す。
- BMS の `WAVfiles` / `BGAfiles` は譜面が要求するリソース参照集合であり、空DB初回起動では memory peak の大きな要因になり得る。新規 file diff 由来では、これらを長期 model field として保持せず、chunk 内で maintenance row へ畳み込んだら破棄する。

## Progress Semantics

大量 read pipeline の進捗は、どの stage で数えたかを混同しない。

| 種類 | 意味 | 用途 |
| --- | --- | --- |
| reader progress | bytes / file metadata を read した件数 | 診断 log、activity 表示 |
| worker progress | digest / parse / evaluate が終わった件数 | 診断 log |
| post-parse prepared progress | parse 後処理が終わり、DB writer へ渡せる staging data ができた件数 | file diff UI の逐次 progress |
| writer progress | DB commit、runtime apply、durable cursor 更新が終わった件数 | 完了判定、resume contract、summary log |

UI が単一の `ProcessedCount` しか持たない file diff では、parse 完了ではなく post-parse prepared progress を表示する。これは「1 譜面について DB 投入用 staging data を作り終え、snapshot bytes を破棄できる状態」を表す。DB commit 完了は 10000 件単位の transaction 境界になりやすいため、通常の file diff UI 進捗には含めない。ただし、LR2 full generation の `processed_cursor` のような durable cursor は writer progress でなければならない。writer progress より前に cursor を進めると、cancel / crash 後に未 commit row を処理済みとして skip する危険がある。

file diff、chart_info backfill、manual maintenance rescan、LR2 `song_rows` は、いずれも stage の意味を混同しない。file diff は post-parse prepared progress を小刻みに報告し、chunk commit が重い場合でも UI 上の処理済み数を commit 完了件数に縛らない。正確な commit 完了件数は別の log / cursor / result count で確認する。

## 正規 Entry Point

| 用途 | 正規 entry point | 備考 |
| --- | --- | --- |
| BMS 軽量 parse | `BMSFile.CreateBMSFileFromSnapshot(snapshot, codepageName)` | `song` 登録、一覧 metadata、resource list 用 |
| bmson 軽量 parse | `BmsonSongParser.ParseSnapshot(snapshot)` | `bmson_song` 登録、一覧 metadata、resource list 用 |
| chart_info parse | `ChartInfoParser.ParseBytesDetailed(snapshot.Bytes, snapshot.Path, snapshot.Md5, snapshot.Sha256, ..., timeout)` | 診断、timeout、parse failure 保存に必要な情報を返す |
| BMS encoding reload | `BMSFile.ReloadBMSMetadataWithEncodingDetection(file, snapshot, detectionResult)` | snapshot 由来の bytes で raw metadata だけを再デコードする |

`BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)`、`ChartInfoParser.Parse(path)` は互換 API として残す。既に path しか持っていない保守処理や pending 生成では使ってよいが、新しい single-read 経路では snapshot / bytes entry point を優先する。

## File Diff

初期化、`FullReinitialize`、軽量 `ReloadFileDiff` で追加・更新譜面を検出した場合は、`ApplyFileScanDiff()` の中で次の順に処理する。

```text
changed path
  -> bounded reader tasks
       ChartFileContentReader.ReadBuffer(path)
  -> parser workers
       ChartFileContentReader.CreateSnapshot(buffer)
  -> lightweight parse
       BMSFile.CreateBMSFileFromSnapshot(...)
       BmsonSongParser.ParseSnapshot(...)
  -> inline chart_info
       current chart_info があれば parse skip
       current parse failure があれば parse skip
       なければ ChartInfoParser.ParseBytesDetailed(...)
  -> same transaction
       song / bmson_song
       chart_digest_map
       generated chart_info / chart_info_parse_failure
       generated maintenance row when available
       resource refs are folded into maintenance row and released
  -> memory apply
       model ChartInfo
       session chart_info index for generated rows
```

file diff の reader は `ChartFileReadPipelinePolicy` に従い、十分な CPU と複数 target がある場合は 2 本まで並列化する。reader は bytes と file metadata だけを bounded queue へ流し、MD5 / SHA-256 計算と snapshot 作成は parser worker 側で行う。file diff の progress target は lightweight parse 対象数で、BMS 追加件数と bmson 追加・更新件数の合算。ただし progress の processed count は parser 完了ではなく、post-parse が DB writer へ渡せる staging data を作った時点で進める。`chart_info` parse failure は `song` / `bmson_song` 登録を止めない。

file diff の `InlineChartInfoBatchSize` 既定値 2048 は current `chart_info` lookup / inline build helper の内部粒度であり、post-parse barrier や DB commit 単位ではない。bytes/read buffer は reader / parsed queue の件数上限で backpressure し、post-parse は snapshot を受け取った worker が 1 譜面ずつ流して、lightweight parse 後の bytes と resource refs を長く滞留させない。post-parse の並列性は micro-batch ではなく parser と同数の post-parse worker で確保し、snapshot bytes は maintenance row / chart_info staging へ畳み込んだら破棄する。

post-parse は parser と同じく worker stage として並列化されている。並列 post-parse worker は `SongTableFileCheckResult`、`FileDiffParsePipelineResult`、runtime model、commit context を直接 mutate せず、item-local な immutable result / commit staging chunk を返す。single collector は sequence 順にその結果を集約し、counter、moved hash relink tracking、runtime apply list、inline `chart_info` publish list、commit queue 投入を担当する。この分離により、chart_info apply や maintenance 評価は並列に進めつつ、DB commit と runtime state mutation の ordering / 一貫性は collector / writer 側へ閉じ込める。2 件以上の差分では schema current な read-only connection から current parser version の `chart_info` row だけを snapshot として読み、schema が current でない場合は post-parse worker に空 snapshot を渡して fallback DB lookup を抑止する。これにより、同じ file diff 実行内の先行 commit を current row として観測する timing 依存を避ける。DB commit は別途 `DbCommitChunkSize` 既定 10000 件で transaction 範囲を切る。

current `chart_info` row が存在する場合、inline parser は詳細 parse を skip できる。この row は対象 model に適用してよいが、file diff の成果物として全件蓄積しない。session chart_info index の全量更新は `chart_info_hydration` が担当し、`file_diff_inline` で publish するのは新規生成または更新した row に限定する。current row lookup は schema が current と確認できる場合 read-only connection を使い、producer 側が writable `song.db` process lock を取りに行かない。

軽量 `ReloadFileDiff` では、現在の in-memory `BMSFiles` / `BmsonSongs` と scan result だけを比較する。DB 再読込、metadata bundle import、full `chart_info` hydration/backfill、installable maintenance deferred は行わない。DB 外部編集や互換修復まで拾う場合は `FullReinitialize` を使う。

削除 commit は row-by-row の `song.hash` lookup / orphan check ではなく、削除対象 path を temp table に入れて集合 SQL で処理する。BMS は削除前に対象 MD5 を temp table へ退避し、`song` と `maintenance` を削除した後、残存 `song.hash` owner が無い MD5 だけ `chart_digest_map` から消す。bmson は `bmson_song` と `maintenance` を同じ chunk transaction で削除する。メモリ側の `NextFiles` 構築では削除 path を `HashSet` 化し、ルート削除やルート近傍 rename のような大量削除でも `currentFiles * deletedPaths` の線形探索に戻さない。

`song_tbl_file_check_breakdown` の `inline_chart_info_index_published_count` は、file diff から runtime index delta へ流した row 数を表す。metadata bundle current skip が大半のケースでは、この値は `inline_chart_info_current_skipped_count` ではなく `inline_chart_info_success_count` 近辺になる。

`song_tbl_file_check_breakdown` の `inline_maintenance_*` は、file diff chunk 内で作った `maintenance` row の対象数、成功/失敗、BMS/bmson 内訳、cache hit / `File.Exists` fallback を表す。chunk commit log の `maintenance=` は、その chunk で `maintenance` table へ保存した row 数を表す。

`song_tbl_file_check_breakdown` の `inline_encoding_*` は、BMS の encoding 判定と非 Shift_JIS 確定時の metadata reload を表す。`inline_encoding_detect_count` は判定対象数、`inline_encoding_fast_ascii_count` は bytes 由来の fast ASCII 判定、`inline_encoding_shift_jis_count` / `inline_encoding_ks_c_5601_count` / `inline_encoding_utf8_count` などは判定結果、`inline_encoding_reload_count` / `inline_encoding_reload_wall_ms` は raw metadata reload の件数と wall clock を表す。

file diff DB commit chunk は transaction 範囲を分けるための単位である。post-parse が作った staging chunk は bounded input queue へ流し、commit aggregator が `DbCommitChunkSize` 単位の immutable DB chunk へ集約し、別の DB writer queue へ渡す。DB writer は chunk commit だけを担当する。post-parse の完了時点で snapshot bytes は不要になるため、file diff UI 進捗は post-parse worker が 1 譜面分の staging data を作った時点で進め、DB commit 完了は待たない。DB が長期的な bottleneck の場合は bounded queue で backpressure するが、commit 中も aggregator は input queue を消費し続けられる。writer task は chunk commit ごとに writable `song.db` connection を開き、commit 後に閉じる。`song_tbl_file_check_breakdown` は post-parse -> aggregator 側の `commit_queue_wait_ms` と、aggregator -> DB writer 側の `commit_writer_queue_wait_ms` を分けて出す。chunk log は `applyMs`、`schemaMs`、`bmsUpsertMs`、`maintenanceUpsertMs`、`chartInfoMs`、`sqliteCommitMs` などを出し、summary には `db_commit_*_ms` として累積する。BMS `song` / `chart_digest_map` の commit は row-by-row upsert ではなく `Lr2SongDbWriter.UpsertGeneratedSongs(...)` の bulk path を使う。moved hash relink は、削除候補の user song columns を file diff 開始時に snapshot し、一意な destination が確定した場合だけ DB commit 後に `song_tbl_file_check user_column_restore_*` で復元する。

file diff inline maintenance は、pipeline 共通の `ResourceHealthLookupContext` を使う。resource health の cache 解決結果は `(directory, resource kind, relative path hash)` 単位で共有され、同一 directory にある多数の BMS が同じ WAV/BGA/movie key を参照するケースで、重複した hash-set lookup を抑える。各 chart の `cacheHit` / `File.Exists` fallback counter は item-local scope で数え、並列評価中の他 chart の counter と混ざらない。`song_tbl_file_check_breakdown` の `inline_maintenance_shared_resource_cache_entries` は、この共有 cache に載った resource key 数を表す。

LR2 `song.db` 完全生成が有効な大量 file diff 直後の自動 LR2 full generation では、file diff と LR2 full generation が同じ generated song row contract を共有する前提にする。直前 file diff が同一 scan/input generation で全 current BMS owner path を durably commit し、inline maintenance / chart_info coverage と moved hash relink ambiguity が問題ない場合は、`song_rows` stage を coverage-based に skip できる。全 column / digest を DB projection で再比較する strict verifier は drift 診断として残してよいが、完全生成有効時の自動 follow-up を止める必須 gate にはしない。skip target は永続化せず、途中終了した場合は次回起動で通常どおり再検証してよい。

### Resource Ref Lifetime

`BMSFile.CreateBMSFileFromSnapshot()` は BMS metadata と同時に `WAVfiles` / `BGAfiles` を構築する。これは health 判定に必要だが、BMSFile 正本へ長期保持すると大量追加時に heap を大きく押し上げる。

正本方針:

- file diff 由来の新規/更新 BMS では、`WAVfiles` / `BGAfiles` を chunk 内の一時入力として扱う。
- native bridge scan から作った canonical resource index と照合し、`maintenance` row を作る。Everything API / service が使えない場合は managed scan から同じ semantics の resource index を作る。resource reference は拡張子を落とした chart-relative resource key として扱い、`foo.wav` は `foo`、`sound/foo.wav` は `sound/foo` になる。resource index は audio / image / movie のカテゴリ別 surface を正本とし、未分類 all-resource surface は保持しない。
- `maintenance` row 作成後は、BMSFile に残る `WAVfiles` / `BGAfiles` / 派生 hash/list cache を破棄する。
- DB 由来の既存 BMS で refs がない場合だけ、background maintenance が path read fallback で補完してよい。
- bmson は `ParseSnapshot()` 済みの fresh resource refs を同じ chunk 内で使い、再パースを避ける。

この方針では、追加ファイル由来の resource health は `installable_maintenance_deferred` へ押し出さない。deferred は DB 由来の missing/stale maintenance 補完、force update、file diff で扱えなかった例外的対象に寄せる。

### Encoding / Raw Metadata

BMS の一覧用 metadata は軽量 parser がまず Shift_JIS 系の既定挙動で読む。maintenance 作成時に `SetEncodingInfoFromSnapshotDetailed()` で bytes 由来の encoding 判定を行い、ASCII fast path、Shift_JIS、KS_C_5601、UTF-8、unknown などを分類する。

非 Shift_JIS が確定し、かつ `?` / unknown ではない場合だけ、同じ snapshot bytes を使って `title` / `subtitle` / `artist` / `subartist` / `genre` の raw metadata を再適用する。ここでは `#SUBTITLE` を title へ、`#SUBARTIST` を artist へ合成する setter 挙動に戻さない。manual/public 側の `ReloadBMSFileWithEncoding(...)` も同じ raw metadata 適用方針に揃える。LR2 full generation と file diff は同じ metadata canonicalization を使う必要があり、uncertain encoding の扱い差で `subtitle` / `subartist` だけがずれる場合は projection skip の blocker ではなく generator drift として修正する。

`maintenance.encoding` は UI metadata 補正と maintenance 表示のための情報であり、`chart_info` parser の decode 方針を変えない。`chart_info` は inline / full backfill とも beatoraja 互換の既定 decode を使い、maintenance の encoding 補正とは別の責務として扱う。

## Package Install

package install は、保留で読んだ bytes を長期保持しない。pending discovery では path-based な `BMSFile.CreateBMSFileFromFile(...)` / `BmsonSongParser.Parse(path)` 由来の model を使う。インストール後は最終配置 path を対象に inline `chart_info` を作り、maintenance は batch 末尾の affected chart 更新で再計算する。
保留中に付いた `ResourceHealth` warning は導入前配置の一時評価なので、導入成功時に package entry の pending warning state から消す。導入後の `ResourceHealth` warning 表示は、通常ライブラリと同じく `maintenanceInfo` / resource health index の projection に任せる。

```text
package install / move
  -> clear pending ResourceHealth package-entry warnings
  -> song / bmson_song registration
  -> affected chart maintenance update
  -> BMS-only zero-note / score update
  -> library state apply
  -> final path chart_info read / parse
  -> DB apply + session chart_info index apply
```

このため、旧来の added chart_info backfill は使わない。install inline の summary は `chart_info_inline_install ...` として `install-performance.log` に出る。`song` / `bmson_song` registration は install execution result の post-processing callback で BMS / bmson を同じタイミングに揃える。inline `chart_info` 適用後にも storage row は再 upsert され得るが、これは chart_info / parse failure / runtime index 更新を伴う冪等な最終適用として扱う。

package install は起動時 file diff と完全には同じではない。起動時 file diff は追加/更新 chart のその時点の snapshot を起点に lightweight parse、maintenance、inline `chart_info` を一貫処理する。一方 package install は pending discovery 時の model、移動後の destination file からの `chart_info` read、batch 末尾の maintenance 再計算に分かれる。discovery から install までに source file が変わった場合は、起動時 file diff より鮮度差が生じやすい。

## Manual Rescan / Encoding Fix

行右クリックの `ファイルスキャン > 再スキャン` と `全譜面を再スキャン` は、resource health / encoding / bmson resource reference を再計算する明示的な重い操作である。この経路は file diff と同じく、reader が `ChartFileContentReader.ReadBuffer(path)` で bytes と file metadata だけを bounded queue へ流し、parallel evaluator が `CreateSnapshot(buffer)` で digest 付き snapshot を作ってから snapshot bytes を消費し、single DB writer が changed row だけを chunk commit する。全件 bytes は保持せず、reader / evaluator / writer の queue capacity でメモリを制限する。reader は `ChartFileReadPipelinePolicy` に従い、十分な CPU と複数 target がある場合は 2 本まで並列化できる。resource health / encoding / bmson resource refs の evaluator は file diff inline maintenance と共有し、BMS / bmson を同じ target list と progress で扱う。

manual rescan は `chart_info` を作らない。既存 `chart_info` の不足や parser version 差分は `chart_info_hydration` / `chart_info_backfill` が担当する。manual encoding fix は snapshot bytes から metadata を読み直し、適用対象は file diff と同じ raw `title` / `subtitle` / `artist` / `subartist` / `genre` に限定する。再計算した `maintenance` row が既存 row と同一の場合、DB upsert は行わない。

## LR2 Full Generation Song Rows

LR2 `song.db` 完全生成の `song_rows` stage も、譜面 bytes を扱う大量処理として bounded pipeline を使う。

```text
current owned BMS song rows
  -> bounded reader tasks (1 or 2)
       ChartFileContentReader.ReadBuffer(path)
  -> parallel workers
       ChartFileContentReader.CreateSnapshot(buffer)
       Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(...)
       recoverable failure は existing row copy fallback
  -> ordered single writer
       current chart_info apply
       LR2 compatibility fact build
       song / maintenance targeted upsert
       durable cursor update after chunk commit
```

writer は worker 完了順ではなく input index 順の contiguous chunk だけを commit する。`processed_cursor` は
「この index より前の `song_rows` target は transaction commit 済み」という durable resume contract であり、
out-of-order commit で進めない。chunk size は transaction 範囲であり、snapshot bytes の保持上限は reader /
computed queue capacity と各譜面ファイルサイズに依存する。queue は件数上限で bytes を bounded にするための
実用的な backpressure であり、巨大な個別譜面ファイルの byte[] size そのものを固定上限にするものではない。

現行実装では対象が複数あり十分な CPU がある場合、`song_rows` reader は 2 本まで並列化される。reader は
bytes-only producer として動き、worker が MD5 / SHA-256 計算、snapshot 作成、parse / enrich を担当する。BMS row の生成は file diff と同じ `Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(...)` を通し、LR2 full generation 専用の別 parser 経路を持たない。
pipeline log では `readMs` と `digestMs` / `parseMs` を分けて確認できる。

`song_rows` stage は `chart_info` full backfill 自体を再実装しない。current parser version の
`chart_info` は `ChartInfoBuildService` 側で先に補完し、`song_rows` writer は chunk 内の対象 hash に対する
current row を lookup して `song` numeric columns に反映する。LR2 compatibility facts は resource health /
encoding row を置換せず、maintenance の LR2 列だけを targeted update する。

初回自動 LR2 full generation は、直前の file diff が大量の譜面を read / parse している場合、`song_rows`
stage を独立 pipeline として再実行する前に file diff の durable coverage を見る。LR2 `song.db` 完全生成が有効な run では、file diff と LR2 full generation が完全に同じ generated song row を作ることを契約にし、同一 scan/input generation、全 current BMS owner path の durable commit、inline maintenance / chart_info coverage、moved hash relink ambiguity なしを満たす場合は `song_rows` stage を skip する。DB projection による全 generated column / digest 比較は高コストな drift 診断に下げ、manual resync、force、signature mismatch、coverage 不足では従来どおり read pipeline を実行する。途中終了した場合に備え、coverage skip target は永続化しない。

## Full Backfill

full backfill は、既存 DB 補完用の background 処理として残す。

主な対象:

- 旧バージョンや外部操作で作られた DB に `chart_info` がない譜面。
- parser version が古い `chart_info`。
- metadata bundle で補完されなかった譜面。

full backfill は path から bytes を read する reader pipeline を維持する。file diff / package install で inline 済みの譜面は、current `chart_info` により file read 前に skip される。

`ChartInfoBuildService` の full backfill は `ChartFileReadPipelinePolicy` に従い、十分な CPU と複数 target がある場合は reader を 2 本まで並列化できる。reader は `readAllBytes` delegate で bytes だけを取得し、MD5 / SHA-256 計算、current parse failure 判定、`ChartInfoParser.ParseBytesDetailed(...)` は worker 側で行う。`chart_info_backfill start/done` log には `workerCount`、`readerCount`、`queueCapacity`、`fileReadCount`、`fileReadBytes`、`readMs`、`parseMs` が出る。

full backfill は新規ファイル追加の後処理ではない。新規・更新ファイルの lightweight parse、chart_info、可能な範囲の maintenance は file diff / install の処理単位で完了させる。

## Skip 判定

| 判定 | Key | 動作 |
| --- | --- | --- |
| current chart_info | `sha256` + current parser version | parse せず既存 row を model に適用 |
| current parse failure | `md5` + current parser version + timeout 条件 | parse せず skip |
| parse success | `md5`, `sha256` | `chart_info` upsert、同 md5 の parse failure を削除 |
| parse failure / timeout | `md5` | `chart_info_parse_failure` upsert。軽量登録は維持 |

## Log

主に見る log:

| Log | 意味 |
| --- | --- |
| `song_tbl_file_check_breakdown` | file diff の対象数、single-read 推定量、inline chart_info count / ms |
| `song_tbl_file_check_breakdown inline_encoding_*` | file diff chunk 内の encoding 判定と raw metadata reload の count / ms |
| `chart_info_inline_install` | package install 後 inline chart_info の summary |
| `chart_info_backfill start/done` | full backfill の summary |
| `chart_info_backfill parse_failed` | shared parser failure log。inline / full の両方で使われることがある |

`chart_info_backfill parse_failed` は名前に backfill を含むが、内部 helper を共有しているため inline 解析の失敗でも出る。詳細な経路は周辺の `song_tbl_file_check_breakdown` や `chart_info_inline_install` summary で判断する。

## 実装上の注意

- snapshot bytes を result や model に保持しない。
- 新規追加譜面は後続 full backfill に回さず、追加処理中に inline `chart_info` まで進める。
- full backfill は「既にライブラリにある譜面の補完」用と考える。
- path-only API を新しい大量処理で使う場合は、二重 read にならないか確認する。
- parser の挙動差を避けるため、inline と full backfill は `ChartInfoParser.ParseBytesDetailed(...)` を共通入口にする。
- current skip した既存 row を、file diff result や commit callback に全件載せない。これは bounded queue / chunk commit を無効化する大きなメモリ要因になる。
- chunk commit 後は、DB 保存用 staging、inline `chart_info` staging、parse failure staging を速やかに破棄する。
- file diff 由来の `WAVfiles` / `BGAfiles` は、maintenance row 作成後に速やかに破棄する。これを `installable_maintenance_deferred` まで保持すると、大量追加時の memory peak を作る。
- 大量初期化では、file diff / chart_info / maintenance の phase 境界で一時参照を切り、memory checkpoint log で推移を観測する。現行では初期化処理側から明示 GC / LOH compact は行わない。
- commit chunk size はメモリ保持上限ではなく transaction 範囲の調整値として扱う。snapshot bytes と current skip row を chunk 外へ持ち越さない前提で、file diff / chart_info backfill の既定は 10000 件とする。
