# 現行の譜面ファイル読み込みパイプライン

この資料は、`2026-05-03` 時点の譜面ファイル読み込みと `chart_info` 生成の正本仕様をまとめる。実装中の初期化軽量化では、この資料の責務分離に合わせて差分ファイル由来の処理と DB 由来の background 補完を分ける。

目的は、追加・更新譜面で同じファイルを軽量 parser と `chart_info` parser が別々に読む状態を避け、どの入口を使うべきかを明確にすること。

## 基本方針

- 新規コードで譜面 bytes が必要な場合は `ChartFileSnapshot` を使う。
- `ChartFileSnapshot` は `ChartFileContentReader.ReadSnapshot(path)` で作る。
- snapshot には `Path`, `Bytes`, `Length`, `LastWriteTimeUtc`, `Md5`, `Sha256` が入る。
- snapshot bytes は処理中だけ保持し、DB や長期 model へ保存しない。
- lightweight parser と `chart_info` parser は統合しない。同じ bytes を使うが、役割は分ける。
- 新規・更新ファイル由来の補助情報は、snapshot が生きている間に作る。
- DB に既に存在する owner 由来の補助情報だけを background hydration/backfill へ回す。

## 正規 Entry Point

| 用途 | 正規 entry point | 備考 |
| --- | --- | --- |
| BMS 軽量 parse | `BMSFile.CreateBMSFileFromSnapshot(snapshot, codepageName)` | `song` 登録、一覧 metadata、resource list 用 |
| bmson 軽量 parse | `BmsonSongParser.ParseSnapshot(snapshot)` | `bmson_song` 登録、一覧 metadata、resource list 用 |
| chart_info parse | `ChartInfoParser.ParseBytesDetailed(snapshot.Bytes, snapshot.Path, snapshot.Md5, snapshot.Sha256, ..., timeout)` | 診断、timeout、parse failure 保存に必要な情報を返す |

`BMSFile.CreateBMSFileFromFile(...)`、`BmsonSongParser.Parse(path)`、`ChartInfoParser.Parse(path)` は互換 API として残す。既に path しか持っていない保守処理や pending 生成では使ってよいが、新しい single-read 経路では snapshot / bytes entry point を優先する。

## File Diff

初期化やライブラリリロードで追加・更新譜面を検出した場合は、`ApplyFileScanDiff()` の中で次の順に処理する。

```text
changed path
  -> ChartFileContentReader.ReadSnapshot(path)
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
  -> memory apply
       model ChartInfo
       session chart_info index for generated rows
```

file diff の progress target は lightweight parse 対象数で、BMS 追加件数と bmson 追加・更新件数の合算。`chart_info` parse failure は `song` / `bmson_song` 登録を止めない。

current `chart_info` row が存在する場合、inline parser は詳細 parse を skip できる。この row は対象 model に適用してよいが、file diff の成果物として全件蓄積しない。session chart_info index の全量更新は `chart_info_hydration` が担当し、`file_diff_inline` で publish するのは新規生成または更新した row に限定する。

## Package Install

package install は、保留で読んだ bytes を長期保持しない。インストール後の最終配置 path を対象に snapshot を 1 回 read し、inline `chart_info` を作る。

```text
package install / move
  -> song / bmson_song registration
  -> final path snapshot read
  -> inline chart_info
  -> DB apply + session chart_info index apply
```

このため、旧来の added chart_info backfill は使わない。install inline の summary は `chart_info_inline_install ...` として `install-performance.log` に出る。

## Full Backfill

full backfill は、既存 DB 補完用の background 処理として残す。

主な対象:

- 旧バージョンや外部操作で作られた DB に `chart_info` がない譜面。
- parser version が古い `chart_info`。
- metadata bundle で補完されなかった譜面。

full backfill は path から bytes を read する reader pipeline を維持する。file diff / package install で inline 済みの譜面は、current `chart_info` により file read 前に skip される。

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
