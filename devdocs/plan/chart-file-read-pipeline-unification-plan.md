# 譜面ファイル read pipeline 統一計画

## 背景

`file diff`、`chart_info` backfill、manual maintenance rescan、LR2 `song.db` 完全生成は、いずれも大量の譜面 bytes を読む処理である。

過去の `chart-file-read-consolidation-plan.md` では、追加・更新譜面を二重 read しないために `ChartFileSnapshot` を導入し、lightweight parse、inline `chart_info`、inline maintenance を同じ bytes から作る方針を整理した。この計画は概ね完了済みである。

次の課題は、各処理が reader / worker / writer pipeline を個別に再実装しており、reader 数、hash 計算の位置、queue capacity、commit 順序、log 粒度が少しずつずれることである。今後は pipeline の vocabulary と policy を揃え、処理ごとの差分を意図的なものだけにする。

2026-06-09 の空 DB / 大量差分検証では、reader / worker の責務分離そのものは揃ってきた一方で、次の横断課題が残っていることが分かった。

- 大量 file diff で全譜面を read / parse / maintenance 評価した直後に、初回自動 LR2 full generation の `song_rows` が同じ譜面を再 read / re-parse している。
- file diff の DB commit chunk は bounded queue と専用 writer task で streaming されるようになった。次は実機ログで read / parse / inline maintenance と DB write が重なっていること、失敗時に producer 側が停止することを確認する。
- file diff inline maintenance と manual maintenance rescan は同じ性質の resource health / encoding 評価を持つため、個別最適化ではなく共通 evaluator / 共通計測として扱う必要がある。

同日の後続ログでは、2048 barrier を外したこと自体は正しい一方、post-parse を 1 件単位へ寄せたことで性能が悪化した。`post_parse_batch_count=210794`、`parser_output_wait_ms` の増大、`inline_maintenance_wall_ms` の長大化から、parser worker は並列でも single post-parse consumer が bottleneck になり、pipeline がそこで詰まっている。single consumer 内の maintenance 並列評価も 1 item では効かない。また `commit_streaming_barrier=none` でも、DB writer が 10000 件 chunk を 4-5 秒 commit している間に commit queue が詰まり、post-parse 側が `commitQueueMs` として待っている。現行の未コミット差分は機能的には破綻していないが、性能が戻り切っていない中間状態として扱い、この状態を最終 commit 境界にしない。

LR2 full generation の freshness 判定も見直す。`song.db` 完全生成が有効な run では、file diff と LR2 full generation は同じ generated song row を作るべきであり、全 generated column / digest を DB projection で再比較する厳密 verifier は高コストな drift 診断へ下げる。自動 follow-up の skip gate は、同一 scan/input generation、file diff が今回 durably commit した BMS owner path coverage、inline maintenance / chart_info coverage、moved hash relink ambiguity なしを主条件にする。

`missing_expected_folder` は通常 folder 欠落ではなく、LR2 built-in `.lr2folder` 親ディレクトリを normal folder 期待値へ混ぜたことが原因と見る。`LR2files\CustomFolder\INSANE01\` / `INSANE02\` のような built-in custom folder 親は `.lr2folder` file row の分類・同期対象であり、物理 normal folder row として期待しない。修正は推測で特殊名を除外するのではなく、LR2 folder parent/category directory expected set と normal folder expected set を分離し、built-in source classification が root custom output classification に負けないようにする。

このため、この計画は狭義の read pipeline 統一だけでなく、大量譜面処理の reader / worker / writer / evaluator / follow-up の横断整理を扱う受け皿として継続する。

## 基本方針

- 「bytes 読込」と「hash 計算」は分ける。
  - reader stage は path から bytes と file metadata を取得するだけに寄せる。
  - MD5 / SHA-256 は worker stage で同じ bytes から計算する。
  - hash のために同じ譜面ファイルを再 read しない。
- reader は 1 本固定ではなく、少数並列にできる。
  - LR2 `song.db` 完全生成の current implementation に合わせ、十分な CPU と対象件数がある場合は reader 2 本を許容する。
  - まずは `maxReaderDegree=2` を上限にし、実機計測で有効性を確認する。
  - HDD / network share / 低 CPU 環境で過剰な read 並列を避けるため、policy は中央集約する。
- writer は原則 single writer にする。
  - SQLite write、ordered commit、durable cursor、runtime state mutation は reader / worker と分離する。
  - LR2 full generation のように input order が resume contract になる処理は ordered writer を維持する。
- progress は stage ごとの意味を分ける。
  - reader progress は bytes 読込済み件数で、診断・activity 表示用に限定する。
  - worker progress は hash / parse / evaluate 済み件数で、診断 log の主材料にする。
  - file diff UI の `ProcessedCount` は post-parse prepared progress、つまり DB writer へ渡せる staging data を作り終えた件数として扱う。
  - writer progress は DB commit / runtime apply / durable cursor 更新済み件数で、完了判定や resume contract に使う。
  - DB commit 完了件数は 10000 件単位になりやすいため file diff UI の通常進捗には含めず、log / cursor / summary では writer progress と混同しない。
- bytes は bounded queue 内だけで保持し、長期 model / result / DB へ保持しない。
- current skip は file read 前に可能なら前倒しする。
  - 既に sha256 / parser version が分かっている target は、read しないで skip できるかを先に判定する。
  - read 後にしか分からない md5 / sha256 は worker stage の hash 計算後に判定する。
- 大量 file diff の成果物は、同じ起動サイクルの自動 follow-up で再 read しない。
  - 初回自動 LR2 full generation は、manual resync とは別に、直前 file diff が生成した row / digest / maintenance / compatibility 鮮度を検証して `song_rows` 再処理を skip または縮小できるようにする。
  - skip 判定は「空 DB 専用」ではなく、大量差分で file diff が対象行を十分に fresh にした場合の一般化として扱う。
- DB writer は single writer を維持しつつ、可能な処理では worker と重ねる。
  - chunk は transaction 範囲であり、現行のように final flush だけに使うと tail latency を減らせない。
  - streaming writer 化する場合も、SQLite write、runtime apply、failure propagation、cancel / rollback の契約を明確にする。
- maintenance 評価は file diff inline と manual rescan で共通化する。
  - `maintenanceMs` は DB table write ではなく、resource health / encoding / bmson refs / row construction の評価時間として扱う。
  - file diff と manual rescan の log 粒度を揃え、同じ改善が両方に効く構造にする。
- `devdocs/spec/chart-file-read-pipeline.md` を現行仕様の正本にし、この plan は実装順と判断履歴を置く。

## 現状整理

| 処理 | 現状 reader | 現状 hash 計算 | worker / writer | 揃える方向 |
| --- | --- | --- | --- | --- |
| `ApplyFileScanDiff()` | policy で 1 / 2 本 | parser worker の `CreateSnapshot(ReadBuffer)` 内 | parser workers、post-parse worker、commit collector | 完了。reader は bytes / metadata だけを読み、hash / parse は worker 側 |
| `ChartInfoBuildService` full backfill | policy で 1 / 2 本 | worker の `ParseQueuedItem()` 内 | chart_info workers、result collector、commit writer | 完了。既存の test injection を保つため reader は `readAllBytes` delegate を使う |
| manual maintenance rescan | policy で 1 / 2 本 | evaluator の `CreateSnapshot(ReadBuffer)` 内 | evaluator workers、single DB writer | 完了。reader は bytes / metadata だけを読み、hash は evaluator 側 |
| LR2 full generation `song_rows` | policy で 1 / 2 本 | worker の `CreateSnapshot(ReadBuffer)` 内 | parse/enrich workers、ordered single writer | 完了。reader 2 本許容を維持し、hash は worker 側 |
| package install inline | bounded workers | `ChartFileContentReader.ReadSnapshot()` 内 | install 後 inline chart_info | 大量処理ではないため優先度低。helper 移行の影響範囲として追従する |

## 目標形

```text
target enumeration
  -> optional pre-read skip
  -> bounded reader tasks
       path -> ChartFileReadBuffer
       bytes, full path, last write time
  -> bounded worker tasks
       compute MD5 / SHA-256 from bytes
       build ChartFileSnapshot or operation-specific item
       lightweight parse / chart_info parse / maintenance evaluation
  -> single collector or writer
       unordered collect, ordered commit, or durable cursor commit
```

`ChartFileSnapshot` は引き続き「bytes + mtime + digest を持つ一時 snapshot」として残す。ただし作成入口を次のように整理する。

| API | 役割 |
| --- | --- |
| `ChartFileContentReader.ReadBuffer(path)` | bytes と file metadata だけを読む新規入口 |
| `ChartFileSnapshot.FromBuffer(buffer)` | worker 側で digest を計算し snapshot を作る |
| `ChartFileContentReader.ReadSnapshot(path)` | 互換入口。内部で `ReadBuffer` + `FromBuffer` を行う |

この分離により、既存 API を急に壊さず、pipeline 化済みの大量処理だけ段階的に `ReadBuffer` へ移行できる。

## 共通 policy

`ChartFileReadPipelinePolicy` のような小さな policy helper を用意し、少なくとも次を中央集約する。

| 項目 | 初期方針 |
| --- | --- |
| reader degree | `targetCount <= 1` なら 1。`processorCount >= 6` かつ大量 target なら 2。それ以外は 1 |
| max reader degree | 2 |
| worker degree | CPU work の内容ごとに既存上限を尊重しつつ、`Environment.ProcessorCount - 1` を基本にする |
| read queue capacity | `workerDegree * max(2, readerDegree * 2)` を初期値にし、処理ごとの chunk size と memory risk で調整 |
| computed queue capacity | writer chunk size または worker 数に比例させる |
| logging | `readerDegree`, `workerDegree`, `readQueueCapacity`, `computedQueueCapacity`, `readMs`, `digestMs`, `parseMs`, `readerOutputWaitMs`, queue high watermark を可能な範囲で出す |
| progress | file diff UI は post-parse prepared progress、その他の evaluator 型 UI は worker progress、完了判定 / durable cursor は writer progress を使う。reader progress は診断値として扱う |

実機確認では、reader 1 / 2 の差を同じ target set で比べる。SSD / NVMe では read 並列化が効く可能性があるが、小さい譜面ファイル大量 read では open / metadata / antivirus / OS cache / hash CPU の影響が混ざるため、上限は保守的に 2 から始める。

## 実装フェーズ

### Phase 0: docs と計測観点の固定

- `devdocs/spec/chart-file-read-pipeline.md` の LR2 `song_rows` 記述を現行実装に合わせる。
- この計画に沿って、各 pipeline の current / target state を明文化する。
- 既存 log で reader / worker / queue / read / parse / commit のどこが見えるかを確認する。

### Phase 1: bytes-only read primitive の導入

- Status: 完了。
- `ChartFileReadBuffer` などの一時型を追加する。
  - `Path`
  - `Bytes`
  - `LastWriteTimeUtc`
  - `Length`
- `ChartFileContentReader.ReadBuffer(path)` を追加する。
- `ChartFileSnapshot.FromBuffer(buffer)` または同等 helper を追加する。
- 既存 `ReadSnapshot(path)` は互換 wrapper として維持する。
- unit test で `ReadSnapshot(path)` と `ReadBuffer(path) -> snapshot` の hash / mtime / parse 結果が一致することを確認する。

### Phase 2: LR2 full generation `song_rows` を基準実装にする

- Status: 完了。
- 現在の readerDegree 1 / 2 policy は維持する。
- reader task は `ReadBuffer(path)` だけを行う。
- worker task が digest 計算、snapshot 作成、encoding detection、BMS lightweight parse、`chart_info` apply、LR2 compatibility facts build を行う。
- ordered writer と durable cursor contract は変更しない。
- log は `readMs` と `digestMs` / `parseMs` を分けて出す。
- reader 1 / 2 の実機比較を行い、reader 2 が有効な条件を確認する。

### Phase 3: file diff pipeline を同じ vocabulary に寄せる

- Status: 完了。reader / parser の責務分離に加え、post-parse も parser と同数の worker stage へ分離した。worker は batch-local `FileDiffPostParseResult` と commit staging chunk だけを作り、single collector が sequence 順に result counters、runtime apply list、moved hash relink tracking、commit queue 投入を集約する。
- 空 DB / 大量差分で最も影響が大きいため、LR2 基準実装の次に扱う。
- reader を policy に従って 1 / 2 本にできるようにする。
- worker で digest 計算と lightweight parse を行う。
- post-parse worker は current `chart_info` 判定、inline parse、inline maintenance row 作成に集中する。
- 2026-06-10 追記: `InlineChartInfoBatchSize=2048` を post-parse barrier として使わない。file reader が貯める bounded buffer と DB commit chunk 以外は 1 譜面ずつ流し、post-parse の並列性は micro-batch ではなく parser と同数の post-parse worker で確保する。snapshot bytes と resource refs は maintenance row / chart_info staging へ畳み込んだら破棄する。
- 完了: 旧 `FlushFileDiffParsedBatch()` の shared state mutation を、item-local `FileDiffPostParseResult` / commit staging chunk の生成と、single collector による ordered aggregation へ分解した。parallel post-parse workers は `SongTableFileCheckResult`、runtime list、commit context を直接触らない。
- 2 件以上の差分では schema current な read-only connection から current parser version の `chart_info` row を一括 snapshot として読み、per-item DB lookup を避ける。DB commit は引き続き `DbCommitChunkSize` の transaction 単位として独立させる。
- 完了: current snapshot を張れない複数件差分では、同じ実行内の先行 commit を current row として観測しないよう、post-parse worker に空 snapshot を渡して fallback DB lookup を抑止する。1 件差分だけは従来どおり対象 row lookup を許容する。
- 差分少数では reader 1 本のままになるようにし、startup 差分 0 の hot path を重くしない。
- `song_tbl_file_check_breakdown` には reader degree と digest time を追加する。

### Phase 4: chart_info full backfill を policy 化する

- Status: 完了。既存テストの reader injection surface は保持し、reader degree と queue capacity を共通 policy へ寄せた。
- hash は worker 側なので、責務分離は比較的近い。
- `readAllBytes` delegate は維持し、reader task が bytes だけを読む。
- worker で digest 計算と `ChartInfoParser.ParseBytesDetailed()` を行う。
- existing row / parse failure による pre-read skip は維持する。
- reader 1 / 2 の実機比較を行い、policy 条件を必要に応じて調整する。

### Phase 5: manual maintenance rescan を policy 化する

- Status: 完了。
- reader は `ReadBuffer`、evaluator が digest / snapshot / resource health / encoding / bmson refs を処理する。
- `warning-model.md` の「reader / parallel evaluator / single DB writer」方針は維持する。
- hash が不要な maintenance path があれば、snapshot 作成を最小化できるか確認する。ただし初期実装では互換性優先で snapshot を作ってよい。
- changed row だけ DB upsert する現行方針は維持する。

### Phase 6: 旧 path-only / ad hoc pipeline の整理

- Status: 継続監査。
- 新規大量処理では `ReadBuffer` / worker digest / bounded queue を使うことを rule 化する。
- `ChartInfoParser.Parse(path)`、`BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)` は互換 API として残す。
- 新規コードで path-only API を使う場合は「二重 read にならないか」を review checklist に入れる。
- spec の pipeline matrix を更新し、処理追加時の判断先を `chart-file-read-pipeline.md` に一本化する。

### Phase 7: progress reporting の統一

- Status: 完了。
- file diff は lightweight parse 後の post-parse が DB 投入用 staging data を作り終えた件数を UI に出し、DB commit の完了は breakdown log と完了処理で追う。
- chart_info full backfill は parse / skip / read-failure result を worker progress として UI に出す。DB commit writer が後続であることは log 側で維持する。
- manual maintenance rescan は evaluator 完了時点で UI progress を進め、chunk flush / DB upsert 完了件数は completed count として扱う。
- LR2 `song_rows` は durable cursor を writer progress のまま維持し、stage progress だけ worker progress で逐次表示する。resume 判定は引き続き commit 済み cursor だけを見る。
- 既存 UI が単一の `ProcessedCount` しか持たない箇所では、file diff は post-parse prepared progress、その他の evaluator 型処理は worker progress を流す。永続状態や完了判定に使う変数名・log は writer progress と分ける。

残る確認候補:

- `BackfillChartDigests()` / `RepairChartDigestMapConsistency()` は旧来の全件 path-based SHA-256 補完実装を持つが、現行起動では呼び出さない。復活させる場合は統一 pipeline へ寄せ、復活予定がなければ obsolete 化または削除する。
- 非 forceUpdate の bmson maintenance missing resource refs 補完は、必要時に `BmsonSongParser.Parse(path)` へ落ちる。通常の明示 full rescan は snapshot pipeline 済みだが、古い DB で missing refs が大量にある場合は次の統一候補にする。

### Phase 8: 大量 file diff 後の自動 LR2 full generation 再読込回避

- Status: 完了。直前 file diff の鮮度 snapshot は、完全生成有効時の自動 follow-up で DB projection strict verifier を走らせず、file diff が今回 durably commit した path coverage だけを `song_rows` skip gate にする。coverage が成立しない場合は安全側で従来の `song_rows` pipeline を実行する。
- 目的は、手動 full generation resync の重い再検証を変えることではなく、同じ起動サイクル内の大量 file diff 直後に自動実行される初回 LR2 full generation が、直前に read / parse 済みの譜面を全件再 read する状態を避けること。
- 対象は「空 DB 初回」専用ではなく、大量差分で file diff が多数の `song` / `bmson_song` / `chart_digest_map` / `maintenance` / `chart_info` を fresh にしたケース全般とする。
- 前提を固定する。
  - LR2 `song.db` 完全生成設定が有効な run では、file diff と LR2 full generation は同じ generated song row を作る。`subtitle` / `subartist` などの差分が出る場合は、skip 判定を厳しくする理由ではなく、row generator / encoding canonicalization の drift として直す。
  - path が DB に存在するだけでは不十分。今回の file diff が durably commit した BMS owner path であることを coverage として持つ。
  - manual resync、force、signature mismatch、coverage 不足、file diff 失敗、moved hash relink ambiguity は従来どおり `song_rows` pipeline を実行する。
- 実装候補:
  - 完了: `ApplyFileScanDiff()` の結果から、大量差分の generated row 鮮度を表す runtime snapshot を保持する。現時点では scan surface generation、BMS/BMSON owner version、BMS target coverage、inline maintenance coverage を照合する。
  - 完了: runtime snapshot の `TransientSongRowSkipPaths` が全 current BMS owner path を覆う場合は、自動 follow-up の `song_rows` stage を丸ごと skip する。空 DB 初回では、file diff が全 BMS を追加 commit するため、この条件を満たす想定にする。
  - 完了: 自動 follow-up では DB projection strict verifier を使わない。coverage gate が成立しない場合は `file_diff_transient_coverage_incomplete` として skip せず、通常の `song_rows` pipeline に任せる。
  - 完了: file diff と LR2 full generation の BMS row 生成は `Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(...)` を共有し、LR2 full generation 専用の別 parser 経路を持たない。
  - 保留: coverage が部分的な通常大量差分では、今回 commit 済み path を自動 follow-up の `song_rows` 対象から一時的に除外し、残りだけを処理する縮小実行を検討する。これは durable resume 対象にはせず、次回起動では通常検証へ戻す。
  - 万一途中終了した場合、次回起動では前回 skip した row も含めて通常どおり再検証してよい。初回自動 full generation は一度だけの best-effort follow-up と扱い、途中再開のために skip target list を永続化しない。
  - skip できない場合は従来どおり `song_rows` pipeline を実行する。manual resync / force resync は安全側で従来動作を維持する。
- log 方針:
  - `lr2_full_generation_sync song_rows_skip` または同等の log に、reason、coveredRows、targetRows、fileDiffGeneration、signature、coverageMs を出す。
  - skip しなかった場合も、`song_rows_skip reason=...` でなぜ再読込が必要だったかを残す。
- 完了条件:
  - 大量 file diff 直後の初回自動 LR2 full generation で、file diff の durable path coverage が成立した譜面は再 read / re-parse されない。
  - 完了: manual full generation resync、途中失敗 resume、signature mismatch、force 実行は従来の安全な full pipeline を維持する。
  - 完了: `startup_progress` / `Lr2FullGenerationStatusService` の durable cursor が、未 commit の処理を完了扱いにしない。
- 追加診断:
  - 完了: `lr2_full_generation_sync startup_scan_diagnostics_detail` を追加し、`dateMissingSongRows` / `missingExpectedFolderRows` / `missingExpectedLr2FolderRows` が残った場合に、最大 10 件の path を出す。
  - 次回ログ確認: `song_rows_skip action=skip reason=file_diff_new_insert_projection_current` で `pipeline_start stage=song_rows` が出ないこと、または `song_rows_skip action=run reason=file_diff_transient_coverage_incomplete` で通常 pipeline に戻ることを見る。

### Phase 9: file diff DB commit chunk の streaming writer 化

- Status: 完了。post-parse -> commit aggregator の input queue と、commit aggregator -> DB writer の write queue を分離した。`FileDiffStreamingCommitContext` は post-parse staging item を `DbCommitChunkSize` 単位の immutable DB chunk へ集約し、別 writer task が chunk commit だけを担当する。DB writer が `CommitChunk()` 中でも aggregator は input queue を消費できるため、短い DB commit pause がそのまま post-parse progress 停止へ伝搬しにくい。
- writer task が開いた writable `song.db` connection は chunk commit ごとに閉じる。次 batch の `chart_info` lookup が read-only open できず writable fallback しても、writer が queue 待ち中に process lock を保持し続けない。
- 目的は、DB writer を single writer のまま維持しつつ、post-parse が chunk を作った時点で DB commit を進め、終端の `db_commit_ms` tail と chunk 保持メモリを減らすこと。
- 実装候補:
  - 完了: moved hash relink は backup / restore 方式へ切り替え、`commit_streaming_barrier=none` の通常 path で streaming writer を使えるようにした。
  - 知見: 旧実装では `FileScanDiffCommitChunk` を即 commit すると、moved hash relink が後から destination chunk へ delete / user column preservation を追加する契約を壊していた。backup / restore 方式へ切り替えることで、relink は chunk mutate ではなく final small write として扱う。
  - 採用: file diff 開始時に削除候補 BMS の user song columns (`favorite`, `adddate`, `tag` など) を path keyed snapshot として backup し、streaming commit 完了後に「同一 MD5 の新規 destination が一意に存在する」場合だけ後段 transaction で restore する。この場合、streaming chunk を後から mutate する必要がなくなり、moved hash relink は final small write として扱える。
  - restore 案の注意点: DB write が追加で 1 回増える。restore までの短時間は新 row の user columns が空になるため、runtime catalog / UI 公開は restore 後に行う。source / destination が 1 対 1 でない ambiguous case は現行同様 restore しない。restore 対象列は小さいが、`tag` の異常値や重複 MD5 の扱いは既存の ambiguous guard を維持する。
  - 完了: moved hash relink は destination `BMSFile` へ user columns を反映し、DB commit 後に同じ commit context で `song` row の user columns を restore する。delete / add / maintenance / chart_info の commit chunk は post-parse 後に immutable とし、streaming writer 化の前提を作った。
  - 知見: 直接 streaming writer を試すと、writer が writable `song.db` connection / process lock を保持したまま queue 待ちし、次 batch の `ProcessInlineBmsChartInfo()` が `LoadChartInfosBySha256()` で同じ lock を取りに行く deadlock が起き得る。producer 側 lookup を read-only-first にし、writer は bounded queue 消費中だけ connection を使う。
  - 完了: current schema の `LoadChartInfosBySha256()` / `LoadChartInfosByMd5()` は read-only connection で lookup し、schema 未整備や read-only open 不可の場合だけ従来どおり writable + schema ensure へ fallback する。
  - 完了: `commitQueue` を bounded input queue にし、`FileDiffStreamingCommitContext` が aggregator と DB writer queue を内包する。
  - 完了: post-parse staging item は writer context 側で `DbCommitChunkSize` 単位に集約し、1 譜面単位の post-parse と DB transaction size を分離する。
  - 完了: writer 失敗時は `PipelineExceptionSignal` で post-parse 側の commit queue 投入を停止し、bounded queue 待ちで固まらないようにした。
  - 完了: aggregator は post-parse から staging chunk を受け取り続け、10000 mutation 程度の immutable DB chunk を組む。DB writer は write queue の chunk を commit するだけにし、commit 中も aggregator が input queue を消費できるようにした。
  - 完了: input queue / write queue は bounded にし、DB が長期的に遅い場合は backpressure するが、短い commit pause で reader / parser / post-parse 全体が停止しないことを目標にする。
  - delete / date-only / moved hash relink / inline chart_info publish の順序制約を現行動作から洗い出し、streaming 化してよい chunk と final barrier が必要な chunk を分ける。
  - runtime catalog swap は従来どおり file diff 全体の成功後に行う。DB chunk commit が先行しても、in-memory owner replacement と UI notification は途中公開しない。
  - inline `chart_info` index publish は chunk commit 後に限定する。ただし大量 current-skip row を publish しない既存方針は維持する。
- log 方針:
  - `commit_queue_capacity` を 0 ではなく実際の bounded capacity として出す。
  - 完了: `commit_writer_queue_capacity` と `commit_writer_queue_wait_ms` を追加し、post-parse -> aggregator 側の `commit_queue_wait_ms` と、aggregator -> DB writer 側の待ちを分ける。
  - `db_commit_chunk_start/done` が `song_tbl_file_check_batch_slow` と時間的に重なることを確認できるようにする。
  - writer wait / post-parse wait / commit wait を分離して、DB writer が詰まり始めた場合に分かるようにする。
- 完了条件:
  - 大量差分時に DB commit chunk が post-parse 完了後だけでなく処理中から進む。
  - DB commit chunk 中も post-parse が次の staging chunk を投入でき、`commitQueueMs` 相当の待ちが commit duration に張り付かない。
  - file diff の成功 / 失敗時の DB 一貫性、runtime apply、chart_info index publish が現行と同等である。
  - `db_commit_ms` 自体がゼロにならなくても、critical path tail と memory peak が下がる。

### Phase 10: maintenance evaluator の共通化と resource health / encoding 軽量化

- Status: 実装中。file diff batch slow log と manual maintenance rescan chunk log の語彙を寄せたうえで、file diff inline maintenance は同一 `ResourceHealthLookupContext` を batch / pipeline 内で共有する。さらに `(directory, resource kind, relative path hash)` の resource existence 判定を共有 cache 化し、同一 directory に多数の譜面がある初回 scan で重複した hash lookup を減らす。
- 対象は file diff inline maintenance だけではなく、manual `RescanAllOwnedChartMaintenance()` / selected maintenance rescan も含める。
- 現行ログでは `maintenanceMs` が重く見えるが、これは DB `maintenance` table write ではなく、resource health、encoding 判定、bmson refs refresh、maintenance row construction を含む evaluator 時間である。DB write は file diff では `db_commit_ms`、manual rescan では `maintenance_rescan_chunk commitMs` として別に見る。
- まず計測を揃える。
  - 完了: file diff の batch slow log に `bmsMaintenanceMs`, `bmsonMaintenanceMs`, `healthMs`, `encodingMs`, `cacheHit`, `fileExistsFallback` を追加する。
  - 完了: manual rescan の `maintenance_rescan_chunk` に `healthMs`, `encodingMs`, `bmsonRefreshMs`, `cacheHit`, `fileExistsFallback` を追加する。
  - 完了: manual snapshot pipeline は item-local `ResourceHealthLookupContext` で評価し、親 context へ counter を合算する。これにより chunk log の cache / fallback は累積値の二重加算ではなく、file diff batch log と同じ粒度の評価結果になる。
  - 完了: file diff inline maintenance の BMS / bmson evaluator は pipeline 共通の resource existence cache を共有し、item result の `cacheHit` / `fileExistsFallback` は item-local counter scope から返す。
  - 完了: `song_tbl_file_check_breakdown` に `inline_maintenance_shared_resource_cache_entries` を追加し、共有 cache がどの程度形成されたかを次回起動ログで確認できるようにする。
  - 継続: manual rescan の `maintenance_rescan_chunk` と file diff の `song_tbl_file_check_batch_slow` で、read / digest / compute / health / encoding / commit の語彙をさらに揃える。
  - encoding slow item、resource ref count が極端に大きい chart、resource lookup cache hit count の増え方を path 付きで追跡できるようにする。
- 実装候補:
  - file diff inline maintenance と manual rescan が同じ `MaintenanceEvaluationResult` / evaluator helper を通るように整理する。
  - 完了: `ResourceHealthLookupContext` の cache を処理単位で共有し、同一 directory / 同一 resource key の cache 解決結果を再利用する。
  - BMS の難易度差分に多い「同一 directory かつ類似 WAV/BGA 参照集合」を、resource ref signature でまとめて health 判定を再利用する。
  - encoding 判定は bytes 由来の現行方針を維持するが、BOM / fast ASCII / strict decode / metadata reload のどこが重いかを分け、非 Shift_JIS 確定時だけ raw metadata reload する方針を保つ。
  - bmson missing refs 補完で path-only parse へ落ちる経路が大量発生する古い DB ケースも、snapshot pipeline または共通 evaluator へ寄せる。
- 完了条件:
  - file diff と manual maintenance rescan の evaluator semantics と log が比較可能になる。
  - 大量差分で `inline_maintenance_wall_ms`、manual rescan で `computeMs` / `healthMs` / `encodingMs` の支配項が特定でき、同じ改善が両方に効く。
  - resource health の結果が変わらないことを、既存 warning / maintenance tests と追加 regression で確認する。

### Phase 11: LR2 built-in folder parent directory の期待値分離

- Status: 完了。`missing_expected_folder` は、LR2 built-in `.lr2folder` 親ディレクトリを normal folder row の期待値へ混ぜた診断ノイズとして扱う。`INSANE01` / `INSANE02` などの built-in custom folder parent は、`.lr2folder` file row / parent category row の surface であり、通常の BMS normal folder row と同じ期待値ではない。
- 前提:
  - `Lr2BuiltinCustomFolderSettings` が含める `LR2files\CustomFolder` 配下は、LR2 built-in custom folder source として扱う。
  - `.lr2folder` parent/category directory は LR2 folder sync 側の生成・保持対象であり、BMS chart directory 由来の normal folder sync 対象ではない。
  - root custom output が LR2 built-in custom folder root と重なる設定では、built-in source classification を優先するか、設定 validation で重複を明示的に扱う。
- 実装:
  - 完了: `CreateExpectedLr2FolderParentDirectoryRowPaths()` 由来の expected paths を normal folder expected set へ union しない。LR2 folder parent expected set と normal folder expected set を別々に持つ。
  - 完了: `missingExpectedFolderRows` は chart directory / normal folder sync 由来だけを見る。`.lr2folder` parent 側の欠落は `missingExpectedLr2FolderRows` として扱う。
  - 完了: `Lr2FolderFileSourceClassifier` は built-in `LR2files\CustomFolder` source を root custom output より先に分類する。絶対 path になった built-in parent directory が `missing_expected_folder` へ落ちる状態を避ける。
  - 完了: `RemoveKnownRelativeLr2FolderDirectories()` のような後段文字列除外を削除し、expected set の分類で normal / LR2 folder を分ける。
- 完了条件:
  - built-in custom folder parent directory が normal folder の `missing_expected_folder` として残らない。
  - 実際に LR2 folder file row が欠けている場合は、`missingExpectedLr2FolderRows` または LR2 folder sync log に出る。
  - root custom output と built-in custom folder root が重なる設定で、分類結果が安定し、特殊名の ad hoc exclude を増やさない。

## テスト計画

- `ChartFileContentReader` の buffer / snapshot equivalence test。
- LR2 full generation sync の reader degree log / resume / ordered commit regression。
- file diff の BMS / bmson 追加更新で read count が増えないこと。
- chart_info backfill の current skip / parse failure skip / commit chunk regression。
- maintenance rescan の changed-only upsert / bmson refs reuse regression。
- Phase 8:
  - 大量 file diff 後の自動 LR2 full generation が、durable path coverage の成立した `song_rows` を skip すること。
  - coverage gate が成立しない場合は DB projection verifier へ逃げず、通常 `song_rows` pipeline を実行すること。
  - signature mismatch / force / manual resync / failed resume では従来 pipeline へ落ちること。
  - skip 後の `Lr2FullGenerationStatusService` cursor / completed status が未 commit row を含まないこと。
- Phase 9:
  - streaming writer で DB chunk が post-parse 中に commit され、失敗時に reader / parser / post-parse が停止すること。
  - delete、add/update、maintenance、chart_info、parse failure、moved hash relink の commit 結果が現行と一致すること。
- Phase 10:
  - file diff inline maintenance と manual rescan が同じ evaluator helper で同じ maintenance row / warning を作ること。
  - resource health cache / encoding slow log の追加で結果が変わらないこと。
- Phase 11:
  - built-in `LR2files\CustomFolder\INSANE01\` / `INSANE02\` parent が normal folder missing diagnostic に入らないこと。
  - 通常 `.lr2folder` file row 欠落は LR2 folder diagnostic 側に出ること。
  - root custom output と built-in custom folder root が重なる設定でも built-in source classification が安定すること。
- 可能なら fake reader で reader degree と queue backpressure を確認する unit test を追加する。

## 実機確認

同じ対象 set で reader 1 / 2 を比較する。

| 対象 | 見る log |
| --- | --- |
| LR2 full generation manual resync | `lr2_full_generation_sync pipeline_start/done`, read / digest / parse / commit |
| 空 DB 初回相当 file diff | `song_tbl_file_check_breakdown`, `parse_read_bytes_estimate`, read / digest / parse / inline maintenance |
| chart_info full backfill | `chart_info_backfill start/done`, fileReadBytes, read / parse |
| manual maintenance full rescan | `maintenance_rescan_chunk`, `maintenance_update checked` |
| 大量 file diff 後の自動 LR2 full generation | `lr2_full_generation_sync song_rows_skip`, `pipeline_start stage=song_rows` が出ない / 対象縮小されること |
| file diff streaming commit | `song_tbl_file_check_batch_slow` と `song_tbl_file_check db_commit_chunk_start/done` の時間的重なり、`commitQueueWaitMs` |
| maintenance evaluator 軽量化 | `inline_maintenance_wall_ms`, `inline_health_wall_ms`, `inline_encoding_wall_ms`, `maintenance_rescan_chunk computeMs/commitMs` |

reader 2 が常に速いとは限らない。特に HDD / network share / antivirus 影響が大きい環境では悪化し得るため、policy は実測後に調整する。

## 判断メモ

`ReadAllBytes` 自体を低レベルに最適化するより先に、pipeline の責務分離を優先する。現状の `ReadSnapshot()` は bytes read と digest を同じ method に閉じ込めているため、reader stage の仕事量が処理ごとにぶれやすい。まずは bytes-only reader と worker digest に分けることで、reader degree の意味を「並列 read 数」に近づける。

この整理後も、`ChartFileSnapshot` は便利な一時値として残す。重要なのは snapshot を作る場所であり、reader が snapshot 完成まで抱え込む構造を大量処理から減らすことである。
