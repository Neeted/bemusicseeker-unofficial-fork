# 譜面ファイル read pipeline 統一計画

## 背景

`file diff`、`chart_info` backfill、manual maintenance rescan、LR2 `song.db` 完全生成は、いずれも大量の譜面 bytes を読む処理である。

過去の `chart-file-read-consolidation-plan.md` では、追加・更新譜面を二重 read しないために `ChartFileSnapshot` を導入し、lightweight parse、inline `chart_info`、inline maintenance を同じ bytes から作る方針を整理した。この計画は概ね完了済みである。

次の課題は、各処理が reader / worker / writer pipeline を個別に再実装しており、reader 数、hash 計算の位置、queue capacity、commit 順序、log 粒度が少しずつずれることである。今後は pipeline の vocabulary と policy を揃え、処理ごとの差分を意図的なものだけにする。

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
- bytes は bounded queue 内だけで保持し、長期 model / result / DB へ保持しない。
- current skip は file read 前に可能なら前倒しする。
  - 既に sha256 / parser version が分かっている target は、read しないで skip できるかを先に判定する。
  - read 後にしか分からない md5 / sha256 は worker stage の hash 計算後に判定する。
- `devdocs/spec/chart-file-read-pipeline.md` を現行仕様の正本にし、この plan は実装順と判断履歴を置く。

## 現状整理

| 処理 | 現状 reader | 現状 hash 計算 | worker / writer | 揃える方向 |
| --- | --- | --- | --- | --- |
| `ApplyFileScanDiff()` | policy で 1 / 2 本 | parser worker の `CreateSnapshot(ReadBuffer)` 内 | parser workers、post-parse worker、commit collector | 完了。reader は bytes / metadata だけを読み、hash / parse は worker 側 |
| `ChartInfoBuildService` full backfill | policy で 1 / 2 本 | worker の `ParseQueuedItem()` 内 | chart_info workers、result collector、commit writer | 完了。既存の test injection を保つため reader は `readAllBytes` delegate を使う |
| manual maintenance rescan | 1 本 | `ChartFileContentReader.ReadSnapshot()` 内 | evaluator workers、single DB writer | reader を少数並列化し、hash は evaluator 側へ寄せる |
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

- Status: 完了。
- 空 DB / 大量差分で最も影響が大きいため、LR2 基準実装の次に扱う。
- reader を policy に従って 1 / 2 本にできるようにする。
- worker で digest 計算と lightweight parse を行う。
- post-parse worker は current `chart_info` 判定、inline parse、inline maintenance row 作成に集中する。
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

- reader は `ReadBuffer`、evaluator が digest / snapshot / resource health / encoding / bmson refs を処理する。
- `warning-model.md` の「reader / parallel evaluator / single DB writer」方針は維持する。
- hash が不要な maintenance path があれば、snapshot 作成を最小化できるか確認する。ただし初期実装では互換性優先で snapshot を作ってよい。
- changed row だけ DB upsert する現行方針は維持する。

### Phase 6: 旧 path-only / ad hoc pipeline の整理

- 新規大量処理では `ReadBuffer` / worker digest / bounded queue を使うことを rule 化する。
- `ChartInfoParser.Parse(path)`、`BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)` は互換 API として残す。
- 新規コードで path-only API を使う場合は「二重 read にならないか」を review checklist に入れる。
- spec の pipeline matrix を更新し、処理追加時の判断先を `chart-file-read-pipeline.md` に一本化する。

## テスト計画

- `ChartFileContentReader` の buffer / snapshot equivalence test。
- LR2 full generation sync の reader degree log / resume / ordered commit regression。
- file diff の BMS / bmson 追加更新で read count が増えないこと。
- chart_info backfill の current skip / parse failure skip / commit chunk regression。
- maintenance rescan の changed-only upsert / bmson refs reuse regression。
- 可能なら fake reader で reader degree と queue backpressure を確認する unit test を追加する。

## 実機確認

同じ対象 set で reader 1 / 2 を比較する。

| 対象 | 見る log |
| --- | --- |
| LR2 full generation manual resync | `lr2_full_generation_sync pipeline_start/done`, read / digest / parse / commit |
| 空 DB 初回相当 file diff | `song_tbl_file_check_breakdown`, `parse_read_bytes_estimate`, read / digest / parse / inline maintenance |
| chart_info full backfill | `chart_info_backfill start/done`, fileReadBytes, read / parse |
| manual maintenance full rescan | `maintenance_rescan_chunk`, `maintenance_update checked` |

reader 2 が常に速いとは限らない。特に HDD / network share / antivirus 影響が大きい環境では悪化し得るため、policy は実測後に調整する。

## 判断メモ

`ReadAllBytes` 自体を低レベルに最適化するより先に、pipeline の責務分離を優先する。現状の `ReadSnapshot()` は bytes read と digest を同じ method に閉じ込めているため、reader stage の仕事量が処理ごとにぶれやすい。まずは bytes-only reader と worker digest に分けることで、reader degree の意味を「並列 read 数」に近づける。

この整理後も、`ChartFileSnapshot` は便利な一時値として残す。重要なのは snapshot を作る場所であり、reader が snapshot 完成まで抱え込む構造を大量処理から減らすことである。
