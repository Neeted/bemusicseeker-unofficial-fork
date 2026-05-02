# 譜面ファイル読み込み重複整理計画

## 概要

初期化・リロード時の `ファイル差分確認` と、background の `譜面メタデータ解析` は、どちらも譜面ファイル本体を読む。

現状は役割が分かれているため動作としては正しいが、新規追加・更新譜面では同じファイルを複数回読む可能性がある。

- `ファイル差分確認`
  - `song` / `bmson_song` 登録に必要な軽量 metadata、resource 参照、MD5 / SHA-256 を作る。
  - BMS / bmson の parser はアプリ内一覧・検索・保守用の登録を優先する。
- `譜面メタデータ解析`
  - `chart_info` 用の詳細 metadata を作る。
  - beatoraja 相当の解析、parser version、timeout、parse failure 永続化を持つ。

この計画では parser の役割は最後まで分けたままにする。まず read / hash 計算 / bytes 受け渡しを整理し、最終的に追加・更新譜面の二重 read をほとんど消すことを目標にする。

## 現状

| 処理 | 対象 | 読み込み | 並列化 | 出力 |
| --- | --- | --- | --- | --- |
| `ApplyFileScanDiff()` BMS 追加 | 新規 `.bms/.bme/.bml/.pms` | `ChartFileContentReader.ReadSnapshot()` で bytes / MD5 / SHA-256 / 更新時刻を取得し、snapshot bytes から軽量 parse | PLINQ `AsParallel()` | `song`, `chart_digest_map`, `BMSFile` |
| `ApplyFileScanDiff()` bmson 追加/更新 | 新規・更新 `.bmson` | `ChartFileContentReader.ReadSnapshot()` で bytes / MD5 / SHA-256 / 更新時刻を取得し、snapshot bytes から JSON parse | PLINQ `AsParallel()` | `bmson_song`, `BmsonSong` |
| `ChartInfoBuildService` | `chart_info` 不足・stale・targeted 追加 | `File.ReadAllBytes`。MD5 / SHA-256 は不足時だけ bytes から計算 | reader 1本、bounded queue、worker 最大4本 | `chart_info`, `chart_digest_map`, `chart_info_parse_failure` |

Phase 1 後は、file diff 内の BMS / bmson 追加・更新 parse は原則 1 read になっている。ただし、その後 `chart_info` が必要なら `ChartInfoBuildService` がもう一度 bytes 読みするため、file diff と metadata 解析の間にはまだ二重 read が残る。

ただし次のケースでは `chart_info` 側の追加 read は避けられる。

- metadata bundle import などで current `chart_info` が既に存在する。
- current `chart_info_parse_failure` が md5 に存在する。
- full hydration / existing row lookup により current row を適用できる。

## 目標

- 追加・更新譜面の file diff 内 read を原則 1 回にする。
- file diff で読んだ bytes と digest を targeted `chart_info` backfill に渡す。
- full backfill や大量 legacy 補完では、従来通り bounded reader pipeline を使う。
- parser は統合しない。
  - 軽量登録 parser は `song` / `bmson_song` のために残す。
  - `chart_info` parser は詳細解析・timeout・parse failure 管理のために残す。
- bytes 保持は積極的に使うが、上限と fallback を必ず持つ。

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
  -> DB upsert song / bmson_song / chart_digest_map
  -> targeted chart_info request with optional snapshots
  -> ChartInfoBuildService
       if usable snapshot exists, parse chart_info from snapshot bytes
       otherwise File.ReadAllBytes fallback
```

`ChartFileSnapshot` は file diff の結果に紐付く一時データであり、DB には保存しない。

## Bytes 保持ポリシー

大量追加時でも二重 read を避けるため、追加・更新譜面の bytes はできるだけ保持する。

推奨初期値:

| 項目 | 値 | 意図 |
| --- | ---: | --- |
| 64bit process total cap | 1024 MiB | 平均的な譜面なら 1 万譜面程度を保持しやすい |
| 32bit process total cap | 256 MiB | address space 圧迫を避ける |
| per file cap | 16 MiB | 異常に大きい譜面で cache を占有しない |
| retention scope | targeted added / updated charts only | full backfill では保持しない |

cap 超過時は bytes を保持せず、`chart_info` 側で従来通り read fallback する。処理結果は変えず、log で `retainedBytes`, `droppedBytes`, `droppedCount`, `reason=cache_limit` を見られるようにする。

## Phase 0: 現状計測と安全網

### 目的

実装前に、実際の重複 read の規模と bytes 保持の効果を見積もれるようにする。

### 変更案

- `ApplyFileScanDiff()` の result / log に次を追加する。
  - `BmsAddedTargetCount`
  - `BmsonUpsertTargetCount`
  - `BmsParseMs`
  - `BmsonParseMs`
  - `ParseReadBytesEstimate`
- `ParseReadBytesEstimate` は Phase 0 時点では旧実装の概算として `FileInfo.Length * 3` を合算していた。Phase 1 後は file diff 内の single-read 化に合わせ、BMS / bmson target file の `FileInfo.Length` 合算へ更新する。length 取得に失敗した file は 0 扱いにし、差分確認自体は失敗させない。
- `song_tbl_file_check_breakdown` に `bms_added_target_count`, `bmson_upsert_target_count`, `bms_parse_ms`, `bmson_parse_ms`, `parse_read_bytes_estimate` を出す。
- `ChartInfoBuildService` の result / log に次を追加する。
  - `Mode`
  - `FileReadCount`
  - `FileReadBytes`
  - `CurrentRowSkippedCount`
- `chart_info_backfill total=...` と `chart_info_backfill done ...` に `mode`, `fileReadCount`, `fileReadBytes`, `currentRowSkipped`, `parseFailureSkipped` を出す。
- `chart_info_backfill start ...` に `currentRowSkipped`, `parseFailureSkipped` を出す。
- `parseFailureSkipped` は既存 `failureSkipped` の読みやすい alias として追加し、既存 field は残す。
- `devdocs/current-startup-reload-progress.md` には進捗仕様だけを残し、本資料に read pipeline の整理を集約する。

### 完了条件

- 起動ログから file diff と chart_info の対象数・read 量を比較できる。
- 挙動変更なし。

## Phase 1: file diff の single-read 化

### 目的

`ApplyFileScanDiff()` 内で BMS / bmson を読む回数を減らす。

### 変更案

- internal 型を追加する。
  - `ChartFileSnapshot`
    - `Path`
    - `Bytes`
    - `Length`
    - `LastWriteTimeUtc`
    - `Md5`
    - `Sha256`
  - `ChartFileContentReader`
    - `ReadSnapshot(path)`
    - bytes から MD5 / SHA-256 を計算
- BMS 用 parser を追加する。
  - `BMSFile.CreateBMSFileFromSnapshot(ChartFileSnapshot snapshot, string codepageName = "shift_jis")`
  - 既存 `CreateBMSFileFromFile()` は互換用に残し、直接呼び出し時の read / exception behavior は大きく変えない。
- bmson 用 parser を追加する。
  - `BmsonSongParser.ParseSnapshot(ChartFileSnapshot snapshot)`
  - 既存 `Parse(path)` は互換用に残す。
- `ApplyFileScanDiff()` の追加 BMS / 追加更新 bmson は snapshot 版を使う。
- `ParseReadBytesEstimate` は single-read の実態に合わせて対象 file length の合算にする。

### 注意点

- BMS の encoding は現状と同じ `shift_jis` default を維持する。
- bmson は UTF-8 decode を snapshot bytes から行う。
- parser 失敗時も progress processed は進める。
- MD5 / SHA-256 は bytes から一度だけ作る。

### 完了条件

- file diff 内で BMS / bmson の hash 用 stream read が消える。
- `song` / `bmson_song` / `chart_digest_map` の出力は現状と一致する。
- `CreateBMSFileFromFile()` / `BmsonSongParser.Parse(path)` は互換 API として残り、file diff 経路だけ snapshot API を使う。

## Phase 2: file diff 並列化方針の統一

### 目的

file diff の PLINQ 無制限並列をやめ、chart_info と近い bounded 方針へ寄せる。

### 変更案

- `ApplyFileScanDiff()` の parser 並列数を内部設定化する。
  - default: `min(4, max(1, Environment.ProcessorCount - 1))`
  - test override 可能にする。
- BMS / bmson を別々の PLINQ で無制限に回すのではなく、同じ degree を使う。
- file diff progress は既存通り BMS + bmson 合算で出す。

### 注意点

- HDD / network drive では並列 read が強すぎると悪化するため、まず chart_info と同じ保守的な値に寄せる。
- Everything scan / fallback enumeration とは別の制御にする。

### 完了条件

- file diff の CPU / disk 負荷が chart_info と同程度に制御される。
- 進捗表示と result count は変わらない。

## Phase 3: snapshot retention を result に載せる

### 目的

file diff で読んだ bytes を、後続 targeted `chart_info` backfill に渡せる形で保持する。

### 変更案

- `ApplyFileScanDiffResult` に optional snapshot collection を追加する。
  - key は sha256 優先、md5、path の順で引けるようにする。
  - BMS と bmson の両方を扱う。
- retention policy を実装する。
  - 64bit total cap 1024 MiB
  - 32bit total cap 256 MiB
  - per file cap 16 MiB
- cap 超過時は snapshot metadata は残しても bytes は破棄する。
- result log に retention summary を出す。

### 注意点

- snapshot は `Initialize()` 内の一時データとして扱い、長期保持しない。
- `BMSFiles` / `BmsonSongs` model に bytes を直接持たせない。
- full backfill 用に全ライブラリ bytes を cache しない。

### 完了条件

- added / updated charts の bytes が cap 内で result から参照できる。
- cap 超過時も従来通り動作する。

## Phase 4: targeted chart_info backfill への bytes handoff

### 目的

追加・更新直後の `chart_info` 解析で、file diff が読んだ bytes を再利用する。

### 変更案

- `QueueChartInfoBackfillForAddedCharts(...)` に optional snapshot provider を渡せるようにする。
- `ChartInfoBackfillRequest` に optional snapshot map を持たせる。
- `ChartInfoBuildTarget` に optional snapshot reference を持たせる。
- `ChartInfoBuildService` の reader loop を変更する。
  - target に usable snapshot bytes がある場合は `File.ReadAllBytes` しない。
  - usable 判定:
    - sha256 が一致する、または
    - path + length + lastWriteTimeUtc が一致し、必要なら bytes から sha256 を確認する。
  - 判定できない場合は従来 read fallback。
- log に `snapshotBytesUsedCount`, `snapshotBytesUsedBytes`, `fileReadFallbackCount` を出す。

### 注意点

- current `chart_info` / parse failure で skip できる場合は、snapshot bytes も使わず skip する。
- snapshot bytes を使うのは targeted request のみ。
- full request では従来の bounded reader pipeline を維持する。

### 完了条件

- 新規追加・更新譜面で `chart_info` が必要な場合、cap 内なら追加の file read が発生しない。
- metadata bundle などで skip される場合も余計な bytes 消費はしない。

## Phase 5: chart_info parser 入力の整理

### 目的

`ChartInfoParser` が file path と bytes の両方から自然に呼べる形にし、snapshot 利用と通常 read fallback の差を小さくする。

### 変更案

- `ChartInfoParser.ParseBytesDetailed(...)` を正規 entry point として整理する。
- `ChartInfoBuildService` は常に `ChartInfoQueuedItem.Bytes` を worker に渡す。
  - bytes の由来だけが snapshot / file read で異なる。
- parse failure log には従来通り path / md5 / sha256 / parser version を出す。

### 注意点

- parser の実装統合は行わない。
- bmson JSON parse の共有もしない。
  - file diff と chart_info は同じ bytes を使うが、JSON document はそれぞれ parse する。

### 完了条件

- chart_info 側の parser 呼び出し経路が snapshot / file read fallback で共通化される。
- timeout / parse failure 永続化の仕様が変わらない。

## Phase 6: 旧 API とテスト helper の整理

### 目的

single-read / handoff 完了後、互換 API とテスト helper を整理する。

### 変更案

- `CreateBMSFileFromFile()` / `BmsonSongParser.Parse(path)` は残すが、内部では snapshot reader を使う。
- test helper は snapshot builder を使う。
- read count を検証する fake reader を追加する。
- docs を更新する。
  - `current-startup-reload-progress.md`
  - 必要なら `current-chart-file-read-pipeline.md` を作成し、実装後の現行仕様を分離する。

### 完了条件

- 新規コードは snapshot / bytes entry point を使う。
- path-only API は互換 wrapper になる。

## Test Plan

### Phase 1

- BMS snapshot parse が既存 `CreateBMSFileFromFile()` と同じ metadata / resource / md5 / sha256 を返す。
- bmson snapshot parse が既存 `Parse(path)` と同じ metadata / resource / md5 / sha256 / updated_at を返す。
- parse failure でも processed count が進む。

### Phase 2

- file diff parser degree override が効く。
- BMS / bmson 合算 progress が単調に進む。
- 並列数を 1 にしても結果が変わらない。

### Phase 3

- cap 内では snapshot bytes が保持される。
- per file cap 超過では bytes が保持されない。
- total cap 超過では一部 bytes が破棄され、summary log が出る。
- 32bit / 64bit 判定の cap が期待通りになる。

### Phase 4

- targeted chart_info backfill が snapshot bytes を使い、fake file reader が呼ばれない。
- snapshot sha256 不一致では file read fallback する。
- current chart_info row がある場合は snapshot bytes も file read も使わない。
- current parse failure がある場合も read しない。
- full backfill は snapshot を使わず従来 path を維持する。

### Phase 5

- timeout / parse failure 永続化が snapshot bytes 経由でも従来通り動く。
- BMS / bmson の chart_info row が file read fallback と同じ内容になる。

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
| 大量追加時のメモリ増加 | 64bit 1024 MiB / 32bit 256 MiB / per file 16 MiB cap を設ける |
| ファイルが diff 後に変更される | sha256、length、lastWriteTimeUtc で usable 判定し、怪しければ read fallback |
| chart_info parser の厳密さにより軽量登録と結果がずれる | parser は統合せず、役割を分ける |
| full backfill で全ファイル bytes を持ってしまう | snapshot handoff は targeted added / updated charts 限定にする |
| PLINQ 並列変更で速度が落ちる | degree override と log を用意し、必要なら調整できるようにする |

## 実装順の推奨

最初の実装単位は Phase 1-2 が良い。これだけで file diff 内の read 回数と並列負荷が整理され、挙動変更も比較的小さい。

次に Phase 3-4 をまとめて進めると、目的である targeted `chart_info` への bytes handoff が完成する。

Phase 5-6 は安定後の整理として扱う。
