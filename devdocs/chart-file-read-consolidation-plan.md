# 譜面ファイル読み込み重複整理計画

## 概要

初期化・リロード時の `ファイル差分確認` と、background の `譜面メタデータ解析` は、どちらも譜面ファイル本体を読む。

Phase 1-4 で、`ApplyFileScanDiff()` 内の BMS / bmson 追加・更新 parse は `ChartFileSnapshot` による single-read と bounded parallelism になった。さらに、追加・更新譜面と package install 譜面は同じ snapshot bytes または最終配置後 snapshot から inline `chart_info` を作るようになった。

現在の方針は、追加・更新譜面については `ApplyFileScanDiff()` の snapshot bytes を使って lightweight parse と chart_info parse を同じ処理単位内で連続実行すること。これにより、空DBに数万譜面を追加する初回起動でも「数万件分の bytes を長時間保持して後続 backfill に渡す」のではなく、各譜面の snapshot が生きている間に chart_info まで処理する。

既存 DB に多数の `song` / `bmson_song` があり、現在のアプリの `chart_info` が未整備なケースは、引き続き full / legacy backfill の役割とする。

この計画では parser の役割は最後まで分けたままにする。

- lightweight parser: `song` / `bmson_song` 登録、一覧・検索・保守用 metadata を作る。
- chart_info parser: beatoraja 相当の詳細 metadata、parser version、timeout、parse failure 永続化を担う。

## 現状

| 処理 | 対象 | 読み込み | 並列化 | 出力 |
| --- | --- | --- | --- | --- |
| `ApplyFileScanDiff()` BMS 追加 | 新規 `.bms/.bme/.bml/.pms` | `ChartFileContentReader.ReadSnapshot()` で bytes / MD5 / SHA-256 / 更新時刻を取得し、snapshot bytes から lightweight parse と inline chart_info parse | bounded PLINQ | `song`, `chart_digest_map`, `chart_info`, `chart_info_parse_failure`, `BMSFile` |
| `ApplyFileScanDiff()` bmson 追加/更新 | 新規・更新 `.bmson` | `ChartFileContentReader.ReadSnapshot()` で bytes / MD5 / SHA-256 / 更新時刻を取得し、snapshot bytes から JSON parse と inline chart_info parse | bounded PLINQ | `bmson_song`, `chart_info`, `chart_info_parse_failure`, `BmsonSong` |
| package install inline | インストール等で追加された譜面 | 最終配置後 path を `ChartFileSnapshot` として 1 read し、inline chart_info parse | bounded PLINQ | `chart_info`, `chart_digest_map`, `chart_info_parse_failure` |
| `ChartInfoBuildService` full request | 既存DB補完、stale parser version 補完 | `File.ReadAllBytes` | reader 1本、bounded queue、worker 最大4本 | `chart_info`, `chart_digest_map`, `chart_info_parse_failure` |

Phase 4 後は、追加・更新譜面を dedicated added backfill へ回す経路は削除済み。新規追加直後の二重 read は、通常の file diff / package install 経路では発生しない。

ただし次のケースでは chart_info 側の追加 read は避けられる。

- metadata bundle import などで current `chart_info` が既に存在する。
- current `chart_info_parse_failure` が md5 に存在する。
- full hydration / existing row lookup により current row を適用できる。

## 目標

- 追加・更新譜面は file diff の snapshot bytes から lightweight metadata と chart_info metadata の両方を作る。
- 追加・更新譜面を後続 added backfill に回す必要をなくした状態を維持する。
- full / legacy backfill は「既にDBにある譜面の chart_info 補完」専用に近づける。
- 大量追加時でも snapshot bytes を長時間保持しない。
- parser は統合しない。

## 最終形

```text
File scan
  -> changed path diff
  -> ChartFileContentReader
       read bytes once
       compute MD5/SHA-256 from bytes
       build ChartFileSnapshot
  -> lightweight parser
       BMSFile.CreateBMSFileFromSnapshot(...)
       BmsonSongParser.ParseSnapshot(...)
  -> chart_info parser
       ChartInfoParser.ParseBytesDetailed(snapshot.Bytes, snapshot.Path, snapshot.Md5, snapshot.Sha256, ...)
       or skip if current chart_info / parse failure already covers the snapshot
  -> DB transaction
       upsert song / bmson_song
       upsert chart_digest_map
       upsert chart_info or chart_info_parse_failure
  -> memory apply
       BMSFile.ChartInfo / bmson_song.ChartInfo
       chart_info index delta
```

`ChartFileSnapshot` は各譜面の処理中だけ使う一時データであり、DB や長期 model へ保持しない。full backfill は従来通り path から read する。

## Snapshot 利用ポリシー

以前の計画では追加・更新譜面の snapshot bytes を result に保持し、後続 targeted backfill へ渡す想定だった。新方針では長期保持を行わない。

| 項目 | 方針 |
| --- | --- |
| retention scope | 追加・更新譜面 1 件の parse 処理中のみ |
| total cap | 原則不要。処理中の bounded parallelism が上限になる |
| per file cap | Phase 3 では設けない。異常に大きい file への対策が必要なら別途追加 |
| full backfill | snapshot handoff なし。従来の reader pipeline を維持 |
| install package 追加 | 最終配置後 path を snapshot で read し、inline chart_info 化する |

大量追加時のメモリ使用量は、おおむね `file diff parser degree` × `同時処理中 snapshot bytes` に収まる。数万譜面をすべて保持する設計にはしない。

## Phase 0: 現状計測と安全網

### 目的

実装前に、実際の重複 read の規模を見積もれるようにする。

### 完了済み内容

- `ApplyFileScanDiff()` の result / log に次を追加した。
  - `BmsAddedTargetCount`
  - `BmsonUpsertTargetCount`
  - `BmsParseMs`
  - `BmsonParseMs`
  - `ParseReadBytesEstimate`
- `ChartInfoBuildService` の result / log に次を追加した。
  - `Mode`
  - `FileReadCount`
  - `FileReadBytes`
  - `CurrentRowSkippedCount`
  - `parseFailureSkipped` alias

### 完了条件

- 起動ログから file diff と chart_info の対象数・read 量を比較できる。

## Phase 1: file diff の single-read 化

### 目的

`ApplyFileScanDiff()` 内で BMS / bmson を読む回数を減らす。

### 完了済み内容

- `ChartFileSnapshot` / `ChartFileContentReader` を追加した。
- `BMSFile.CreateBMSFileFromSnapshot(...)` を追加した。
- `BmsonSongParser.ParseSnapshot(...)` を追加した。
- `ApplyFileScanDiff()` の追加 BMS / 追加更新 bmson は snapshot 版を使う。
- `ParseReadBytesEstimate` は single-read の実態に合わせて対象 file length の合算にした。

### 完了条件

- file diff 内で BMS / bmson の hash 用 stream read が消える。
- `song` / `bmson_song` / `chart_digest_map` の出力は現状と一致する。

## Phase 2: file diff 並列化方針の統一

### 目的

file diff の PLINQ 無制限並列をやめ、chart_info と近い bounded 方針へ寄せる。

### 完了済み内容

- `ApplyFileScanDiff()` の parser 並列数を内部設定化した。
  - default: `min(4, max(1, Environment.ProcessorCount - 1))`
  - test override 可能
- BMS / bmson は同じ degree を使う。
- `SongTableFileCheckResult` と `song_tbl_file_check_breakdown` log に `file_diff_parser_degree` を出す。

### 完了条件

- file diff の CPU / disk 負荷が chart_info と同程度に制御される。
- 進捗表示と result count は変わらない。

## Phase 3: file diff 内 chart_info inline 解析

### 目的

追加・更新譜面を後続 added backfill に回さず、`ApplyFileScanDiff()` 内で同じ snapshot bytes から chart_info まで生成する。

### 完了済み内容

- `ChartInfoBuildService.BuildInlineChartInfo(...)` を追加し、単一 snapshot から `chart_info` / parse failure を作れるようにした。
- `ApplyFileScanDiff()` の BMS / bmson parse worker 内で、lightweight parse 成功後に同じ snapshot bytes から inline chart_info を作る。
- current `chart_info` が snapshot sha256 で current の場合は chart_info parse を skip し、その row を model に適用する。
- current `chart_info_parse_failure` が snapshot md5 で current の場合は chart_info parse を skip する。
- chart_info parse 成功時は、生成 row を `BMSFile.ChartInfo` / `bmson_song.ChartInfo` に適用し、DB commit buffer に入れる。
- chart_info parse 失敗時は、既存 backfill と同じ形式で `chart_info_parse_failure` row を保存する。`song` / `bmson_song` 登録は失敗させない。
- file diff の DB transaction で、`song` / `bmson_song` / `chart_digest_map` と一緒に chart_info / parse_failure を upsert する。
- log に inline chart_info summary を追加した。
  - `inline_chart_info_target_count`
  - `inline_chart_info_success_count`
  - `inline_chart_info_current_skipped_count`
  - `inline_chart_info_failure_skipped_count`
  - `inline_chart_info_parse_failed_count`
  - `inline_chart_info_failure_persisted_count`
  - `inline_chart_info_failure_cleared_count`
  - `inline_chart_info_parse_ms`
  - `inline_chart_info_batch_size`

### 注意点

- lightweight parse が失敗した譜面は従来通り追加対象にならないため、inline chart_info も行わない。
- chart_info parse failure は lightweight parse 成功後の追加登録を止めない。
- chart_info parser は beatoraja 互換 decode を使う。maintenance encoding hint は使わない。
- snapshot bytes は file diff batch 内だけで保持し、後続 backfill へ渡さない。既定 batch size は 512。
- full backfill の reader pipeline は変更しない。

### 完了条件

- 新規・更新譜面は file diff の 1 read で lightweight metadata と chart_info metadata の両方を得られる。
- inline chart_info 成功 row が DB と memory index に反映される。
- inline chart_info parse failure が DB に永続化され、次回以降の full backfill で skip される。

## Phase 4: added chart_info backfill の縮小・除去

### 目的

追加・更新譜面を後続 added backfill へ回す経路を削除し、追加譜面の二重 read をなくした。旧 Phase 5 の install package inline 化もこの Phase に含めた。

### 完了済み内容

- inline chart_info 処理を `ChartInfoInlineBuildService` として file diff / install package の両方から使える helper に整理した。
- 通常 package install と推定先 install は、最終配置後 path を `ChartFileSnapshot` で 1 read し、同じ bytes で chart_info を作る。
- inline 成功 row は DB と memory index へ反映し、parse failure は `chart_info_parse_failure` に保存する。
- `QueueChartInfoBackfillForAddedCharts(...)`、`ChartInfoBackfillRequestKind.AddedCharts`、`BackfillChartInfosForTargets(...)` を削除した。
- startup / reload 後の full backfill は、既存DB補完専用として維持する。
- full backfill の target build では、inline chart_info 済みの新規譜面は current row により file read 前 skip される。

### 注意点

- 「アプリ外で作られた既存DBに chart_info がない」ケースは full backfill で処理する。
- full backfill は引き続き background 処理でよい。
- UI の `ChartInfoBackfillRunning` / progress 表示は full backfill 用に維持する。
- package install の inline chart_info parse failure は install 成功を取り消さない。

### 完了条件

- file diff で追加・更新された譜面が added backfill で再 read されない。
- install package で追加された譜面も、追加直後に chart_info まで作られる。
- install package added backfill が不要になる。

## Phase 5: parser bytes entry point と旧 API 整理

### 目的

inline 経路と backfill 経路の parser 呼び出しを整理し、single-read 化後の現行仕様を明確にする。

### 完了済み内容

- `ChartInfoParser.ParseBytesDetailed(...)` を正規 entry point としてコードコメントと資料に明記した。
- `ChartInfoBuildService` と inline helper は同じ `BuildInlineChartInfo(...)` / commit helper を経由する。
- `CreateBMSFileFromFile()` / `BmsonSongParser.Parse(path)` は互換 API として残し、新規 single-read 経路では snapshot / bytes API を優先する方針を明記した。
- `devdocs/current-chart-file-read-pipeline.md` に現行仕様を分離した。

### 完了条件

- 新規コードは snapshot / bytes entry point を使う。
- path-only API は互換 wrapper として位置づけられる。
- 実装後の read pipeline が資料化される。

## Test Plan

### Phase 3

- BMS / bmson 追加で lightweight parse と chart_info parse が同じ snapshot bytes から行われる。
- fake file reader により、inline chart_info で追加 read が発生しないことを確認する。
- current chart_info row がある場合は inline chart_info parse を skip し、row が model に適用される。
- current parse failure がある場合は inline chart_info parse を skip し、追加登録は継続する。
- inline chart_info parse failure は `chart_info_parse_failure` に保存され、`song` / `bmson_song` は登録される。
- `song_tbl_file_check_breakdown` に inline chart_info counters が出る。

### Phase 4

- file diff 追加譜面が added backfill queue に積まれない。
- 空DB初回相当で、file diff 後の full backfill が inline 済み row を current skip する。
- 既存DB補完では full backfill が従来通り missing chart_info を解析する。
- package install 追加譜面で chart_info が追加直後に作られる。
- package install 後の added backfill が不要になる。
- bmson / BMS 混在 package で DB 登録、maintenance、chart_info が揃う。

### Phase 5

- inline helper / parser bytes entry point / 現行仕様資料を整理済み。

### Regression

- `BmsLibraryInitializationServiceTests`
- `ChartInfoMetadataTests`
- `BmsLibraryPackageInstallServiceTests`
- `BmsLibraryMaintenanceServiceTests`
- `BmsLibraryZeroNoteRefreshTests`
- `dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release`
- `dotnet build BeMusicSeeker-decomp.sln /p:Configuration=Release`

## リスクと判断

| リスク | 対応 |
| --- | --- |
| file diff が重くなる | 追加・更新譜面では最終的に chart_info も必要なため、同じ read 中に行う。進捗と log で inline chart_info 時間を見える化する |
| chart_info parse failure で追加登録が止まる | failure は永続化するが、`song` / `bmson_song` 登録は継続する |
| full backfill と inline 経路で結果がずれる | ChartInfoBuildService の parse / failure / commit helper を共有する |
| 空DB初回で full backfill が再解析する | inline chart_info row を DB と memory index に反映し、full backfill では current row skip させる |
| install package 経路だけ二重 read が残る | Phase 4 で inline 化し、added backfill を削除済み |

## 実装順の推奨

Phase 3 では file diff の追加・更新譜面だけ inline chart_info 化した。これで空DB初回起動やライブラリリロード時の大量追加に対する二重 read を大きく減らせる。

Phase 4 で package install 経路も inline chart_info 化し、added backfill queue を削除した。

Phase 5 で API / docs 整理を行い、現行仕様を `current-chart-file-read-pipeline.md` に分離した。
