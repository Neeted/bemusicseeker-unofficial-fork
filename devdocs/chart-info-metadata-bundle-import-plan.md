# chart_info 解析済みメタデータ同梱 import 計画

## Summary

`chart_info` の解析は大規模ライブラリでは時間がかかるため、あらかじめ解析済みの `chart_info` / `chart_digest_map` をリリース物に同梱し、アプリ起動時に差分 import できるようにする。

初回実装単位では、独立した export tool と、未圧縮 SQLite sidecar DB の import までを実装する。7z 圧縮同梱、リリーススクリプトの二系統 package 化、import 済み bundle の退避、parse failure 同梱は後続 Unit に分ける。

## Goals

- 解析済み `chart_info` をアプリの `song.db` へ取り込み、通常 backfill の parse 対象を減らす。
- `chart_digest_map` も同時に補完し、譜面ファイル全量 read をできるだけ抑える。
- 既存 DB の current な `chart_info` は壊さない。
- export データは git 追跡に含めない。
- メタデータ非同梱版の配布も維持する。

## Non-Goals

- `chart_info` テーブルを削除して入れ替えることはしない。
- 初回実装では 7z 展開、release zip 二系統化、GitHub release asset 添付変更、import 済み bundle の退避は行わない。
- 初回実装では `chart_info_parse_failure` の同梱 import は行わない。
- import 直後に backfill を強制実行する UI は作らない。

## Current State

- `chart_info` は `sha256` primary key、`parser_version` と `updated_at` を持つ。
- `chart_digest_map` は `md5` primary key、`sha256` を持つ。
- 通常 backfill は `chart_info.parser_version >= CurrentChartInfoParserVersion` の行を current とみなして再解析しない。
- `chart_info_parse_failure` は `md5` primary key で失敗記録を保持する。成功 `chart_info` が作成された md5 は通常 backfill で failure row を削除する。
- 起動時は `BmsLibraryInitializationService.LoadSongTable()` が `chart_digest_map` を読み込み、`BMSFile.sha256` へ適用する。
- 起動後に `QueueDeferredChartInfoHydration(..., queueFullBackfillAfterHydration: true)` が `chart_info` を遅延 hydrate し、その後不足分 backfill を要求する。

## Bundle Format

初回実装は未圧縮 SQLite DB を対象にする。

- 配布ファイル名: `chart-info-metadata.db`
- 開発時出力先: `artifacts/chart-info-metadata/latest/chart-info-metadata.db`
- アプリ起動時探索先: `AppDomain.CurrentDomain.BaseDirectory`
- 取り込み対象 table:
  - `chart_info`
  - `chart_digest_map`
  - `chart_info_metadata_bundle` または同等の manifest table

manifest には最低限以下を保存する。

| Column | 内容 |
| --- | --- |
| `bundle_id` | export ごとの一意 ID |
| `generated_at` | UTC 生成時刻 |
| `chart_info_parser_version` | export 時点の parser version |
| `chart_info_schema_version` | export 時点の schema version |
| `chart_info_count` | export した `chart_info` 件数 |
| `chart_digest_count` | export した `chart_digest_map` 件数 |

source DB path はローカル情報なので bundle 内には必須保存しない。必要な場合も diagnostic 用の任意項目に留める。

## Unit 1: Export Tool と未圧縮 DB Import

### Export Tool

`tools/chart-info-export` を追加する。

- `tools/chart-info-compare` と同じ `net472` CLI 構成を使う。
- `libs/sqlite.net.dll` と `vendor/native/*/sqlite3.dll` を参照する。
- 既定の入力例:
  - `D:\LR2beta3\LR2files\Database\song.db`
- 出力例:
  - `artifacts/chart-info-metadata/latest/chart-info-metadata.db`

想定 CLI:

```powershell
dotnet run --project tools/chart-info-export -- `
  --source "D:\LR2beta3\LR2files\Database\song.db" `
  --out artifacts/chart-info-metadata/latest/chart-info-metadata.db
```

export 仕様:

- source DB に `chart_info` が存在しない場合はエラーにする。
- `chart_info` は `sha256` が有効で、`parser_version >= CurrentChartInfoParserVersion` の行だけ export する。
- `chart_digest_map` は source table に存在する行を export する。
- `chart_info(md5, sha256)` から `chart_digest_map` に不足分を補う。
- 出力先 DB は毎回作り直す。
- export データは `artifacts` 配下へ置き、git 追跡しない。

### App Import

起動時に exe と同階層の `chart-info-metadata.db` を検出した場合、`song.db` へ差分 import する。

差し込み位置は `BMSLibrary.Initialize()` の `initializationService.RunInitialize(...)` より前にする。これにより `LoadSongTable()` の `chart_digest_map` 読み込みに間に合い、起動時から `BMSFile.sha256` の補完に使える。

import service の候補:

- `ChartInfoMetadataImportService`
- `BmsLibraryDbGateway.ImportChartInfoMetadataBundle(...)`

import 条件:

- `chart_info`
  - source row の `parser_version >= CurrentChartInfoParserVersion` のみ対象。
  - destination に同じ `sha256` がない場合は insert。
  - destination の `parser_version < CurrentChartInfoParserVersion` の場合は replace。
  - destination が current 以上の場合は触らない。
- `chart_digest_map`
  - destination に同じ `md5` がない場合だけ insert。
  - local DB の既存 md5 -> sha256 対応を優先する。
- `chart_info_parse_failure`
  - import された current `chart_info` の md5 に対応する failure row は削除する。
  - 初回 Unit では failure row 自体の import はしない。

import は `ATTACH DATABASE` を使い、SQLite 側で差分投入する。大量行を C# object に materialize しない。

### Import History

大容量 bundle を毎回差分 scan しないため、app DB に import 履歴 table を追加する。

候補 table: `chart_info_import_history`

| Column | 内容 |
| --- | --- |
| `bundle_id` | bundle manifest の ID |
| `bundle_sha256` | sidecar DB ファイルの SHA-256 |
| `parser_version` | import 対象 parser version |
| `chart_info_imported_count` | insert / replace した chart_info 件数 |
| `chart_digest_imported_count` | insert した chart_digest_map 件数 |
| `failure_cleared_count` | 削除した failure row 件数 |
| `imported_at` | UTC import 完了時刻 |

同じ `bundle_sha256` かつ同じ parser version の完了済み履歴がある場合は import を skip する。

### Logging

`install-performance.log` に以下を出す。

- `chart_info_metadata_import skipped reason=missing_bundle`
- `chart_info_metadata_import skipped reason=already_imported bundleSha256=...`
- `chart_info_metadata_import start path=... bundleSha256=...`
- `chart_info_metadata_import done chartInfoImported=... digestImported=... failureCleared=... elapsedMs=...`
- `chart_info_metadata_import failed message=...`

## Unit 2: 7z 圧縮 Bundle 対応

未圧縮 DB import が安定してから対応する。

- 配布ファイル名: `chart-info-metadata.7z`
- archive 中身:
  - `chart-info-metadata.db`
  - 任意で `manifest.json`
- 起動時は `.db` を優先し、なければ `.7z` を探す。
- `SevenZipExtractor.dll` と bundled native 7z を利用する。
- 展開先は一時ディレクトリにし、import 後に削除する。
- import history の `bundle_sha256` は `.7z` ファイルの SHA-256 を使う。

## Unit 3: Release Script

`scripts/publish.ps1` と `scripts/release.ps1` を二系統 package に対応させる。

- 通常版:
  - `bemusicseeker-unofficial-fork-vX.X.X.X.zip`
- メタデータ同梱版:
  - `bemusicseeker-unofficial-fork-vX.X.X.X-with-metadata.zip`

`publish.ps1` の拡張候補:

- `-IncludeMetadata`
- `-MetadataSource artifacts/chart-info-metadata/latest/chart-info-metadata.7z`
- `-MetadataPackageSuffix "-with-metadata"`

`release.ps1` は `dist/bemusicseeker-unofficial-fork-${tag}*.zip` を release asset に添付できるようにする。

## Unit 4: Import 済み Bundle の退避

metadata bundle を exe と同階層に置きっぱなしにすると、起動のたびに `chart-info-metadata.db` / `.7z` の SHA-256 計算が走る。`.7z` は import history 済みなら展開されないが、archive hash 計算だけでも大容量ファイルでは無視できない。

削除でも実運用上は困りにくいが、`song.db` を再構築したい場合や、同じ配布 metadata を再利用したい場合に備えて、削除ではなく退避を基本方針にする。

### Policy

- import 成功、または `already_imported` 確認後に、元 bundle を `imported_metadata/` へ移動する。
- import 失敗時は移動しない。ユーザーがファイルを直して再起動できるよう、exe 同階層に残す。
- `missing_bundle` は従来通り no-op。
- 退避後の次回起動では exe 同階層に bundle がないため、SHA-256 計算自体が発生しない。
- `song.db` 再構築などで再 import したい場合は、ユーザーが `imported_metadata/` から exe 同階層へ戻す。

### Target Files

- `chart-info-metadata.db`
- `chart-info-metadata.7z`

探索順は Unit 2 と同じく `.db` 優先、`.db` がなければ `.7z` とする。退避対象も実際に import / skip 判定に使ったファイルだけにする。

### Destination

退避先:

```text
<exe directory>/imported_metadata/
```

通常の移動先:

- `imported_metadata/chart-info-metadata.db`
- `imported_metadata/chart-info-metadata.7z`

同名ファイルが存在する場合は衝突を避ける。

候補:

- `chart-info-metadata.<sha256-prefix>.db`
- `chart-info-metadata.<sha256-prefix>.7z`
- それでも衝突する場合は連番 suffix を付ける。

`sha256-prefix` は 12 文字程度で十分だが、実装上は後から調整可能にする。

### Logging

`install-performance.log` に以下を追加する。

- `chart_info_metadata_bundle_archive moved bundleType=... source=... destination=...`
- `chart_info_metadata_bundle_archive skipped reason=... source=...`
- `chart_info_metadata_bundle_archive failed source=... message=...`

退避失敗は起動失敗にしない。import 自体は完了済みなので、ログに残して続行する。

### Test Plan

- `.db` import 成功後、`imported_metadata/` に移動されること。
- `.7z` import 成功後、`imported_metadata/` に移動されること。
- `.7z` が `already_imported` の場合、展開せずに退避されること。
- import 失敗時は移動されないこと。
- 同名退避先がある場合、衝突しないファイル名へ移動されること。
- 退避後の次回起動では `missing_bundle` になり、SHA-256 計算や展開が走らないこと。
- 退避失敗時も起動処理が継続すること。

### Notes

初回 import のためには SHA-256 計算が必要なので、配置直後の1回分のコストは残る。Unit 4 の目的は、import 済み bundle を置きっぱなしにした場合の毎回起動コストをなくすこと。

## Unit 5: Future Improvements

- `chart_info_parse_failure` の export / import。
- bundle manifest の署名または checksum file 分離。
- UI からの手動 import / reimport。
- import 済み bundle の状態表示と、退避済み bundle を戻す導線。
- parser version 更新時の stale bundle warning。
- 差分 bundle 作成。

## Test Plan

### Export Tool

- `chart_info` と `chart_digest_map` を持つ source DB から sidecar DB が生成されること。
- `parser_version` が古い `chart_info` は export されないこと。
- `chart_info(md5, sha256)` から `chart_digest_map` 不足分が補完されること。
- source DB に `chart_info` がない場合はエラーになること。

### Import

- bundle がない場合は no-op で起動できること。
- missing `chart_info` だけ import されること。
- stale `chart_info` は current row で置き換わること。
- current 以上の local row は置き換えないこと。
- missing `chart_digest_map` だけ insert されること。
- imported md5 の `chart_info_parse_failure` が削除されること。
- import history により同一 bundle の2回目 import が skip されること。
- import 後の `LoadSongTable()` で `chart_digest_map` が `BMSFile.sha256` に反映されること。
- import 後の hydration/backfill で imported `chart_info` が current として扱われ、再解析対象にならないこと。

### Regression

- `ChartInfoMetadataTests`
- `BmsLibraryInitializationServiceTests`
- `dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release`
- `dotnet build BeMusicSeeker-decomp.sln /p:Configuration=Release`

## Assumptions

- 同梱メタデータはアプリ独自 `chart_info` schema に従う。
- `sha256` が chart_info の正本 key、`md5` は LR2 song / digest / failure 連携用の補助 key とする。
- `chart_digest_map` は local DB を優先し、bundle は不足補完に使う。
- 初回実装では未圧縮 DB import までを完了ラインにする。
