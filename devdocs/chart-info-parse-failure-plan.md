# chart_info 解析失敗永続化 実装計画

## Summary

`chart_info_backfill` で毎回同じ譜面の解析に失敗し続ける問題を解消するため、解析失敗を DB に永続化し、現行 parser version で失敗済みの譜面は次回以降の backfill 対象から除外する。

あわせて、メンテナンスツリーに `解析エラー` 画面を追加し、現在ライブラリに存在する失敗譜面を一覧表示する。WARNING 列には解析エラー内容を表示し、行ハイライト対象にする。ユーザーが必要に応じて失敗記録を削除できる右クリックメニューも追加する。

## Background

- `song` / `bmson_song` に登録できる譜面でも、`chart_info` 登録時の beatoraja 相当パースでは失敗することがある。
- 現状の `chart_info` は成功した解析結果だけを保存するテーブルで、失敗状態は永続化していない。
- そのため、起動時や backfill 実行時に同じ失敗譜面を毎回解析し、ログに `chart_info_backfill parse_failed` が繰り返し出る。
- `chart_info` 本体に失敗 sentinel row を入れると、hydrate / 表示 / 検索 index に偽のメタデータが混ざるリスクがあるため、失敗は別テーブルに保存する。

## Goals

- 解析失敗した譜面を DB に永続化し、同じ parser version では再解析しない。
- parser version が上がった場合は、過去の失敗記録を stale とみなし再解析できるようにする。
- 解析失敗譜面をメンテナンスツリーの `解析エラー` 画面で確認できるようにする。
- WARNING 列にエラー内容を表示し、行ハイライトする。
- 右クリック操作で失敗記録を削除し、次回以降の再解析対象へ戻せるようにする。
- BMS と bmson の両方を対象にする。

## Non Goals

- `chart_info` テーブルに失敗行を混ぜない。
- `song` / `bmson_song` / `maintenance` の登録可否は変更しない。
- ファイル read 失敗は初期実装では永続化しない。ロック、権限、一時欠落などの一過性要因があり得るため。
- parser の許容範囲そのものは変更しない。

## Implementation Units

### Unit 1: DB 永続化と backfill skip

`chart_info_parse_failure` テーブルを追加する。

推奨 schema:

```sql
CREATE TABLE IF NOT EXISTS chart_info_parse_failure (
    md5 TEXT PRIMARY KEY,
    sha256 TEXT,
    path TEXT,
    parser_version INTEGER NOT NULL,
    failure_kind TEXT NOT NULL,
    exception_type TEXT,
    message TEXT,
    parse_timeout_ms INTEGER,
    updated_at TEXT NOT NULL
);
```

index:

```sql
CREATE INDEX IF NOT EXISTS idx_chart_info_parse_failure_sha256
    ON chart_info_parse_failure(sha256);

CREATE INDEX IF NOT EXISTS idx_chart_info_parse_failure_parser_version
    ON chart_info_parse_failure(parser_version);
```

設計方針:

- 主キーは `md5` とする。譜面 path が変わっても同一内容なら再解析を避けるため。
- `sha256` は補助情報として保存し、検索・診断・将来拡張用の index を張る。
- `parser_version` が `CurrentChartInfoParserVersion` と一致する失敗記録だけを skip 対象にする。
- timeout の場合は `parse_timeout_ms` も保存する。将来 timeout 設定を変更した場合の再解析判定に使えるようにする。
- message は巨大化を避けるため、保存時または表示時に適度に truncate する。

変更箇所:

- `LR2SongDBExtended`
  - `chart_info_parse_failure` モデルを追加する。
- `BmsLibraryDbGateway`
  - schema repair に失敗テーブル作成を追加する。
  - 失敗記録の load / upsert / delete API を追加する。
  - `UpsertChartInfoBackfillChunk` または同等の commit path で、成功 row / digest row と同じ chunk transaction に失敗記録も含める。
- `ChartInfoBuildService`
  - backfill 開始時に現行失敗記録を読み込む。
  - target 構築時、同一 md5 の現行 parser version 失敗記録があれば parse queue に入れない。
  - parse 成功時、同一 md5 の失敗記録を削除する。
  - parse 失敗時、失敗記録を upsert する。
  - read 失敗は現行通りログと failure count のみとし、失敗記録には入れない。
- `ChartInfoBackfillResult`
  - `FailureSkippedCount` を追加する。
  - 完了ログに skip 件数を出す。

### Unit 2: WARNING とメンテナンス `解析エラー` 画面

構造化 WARNING に chart_info 解析失敗用 kind を追加する。

追加案:

- `ChartWarningKind.ChartInfoParseFailure`
- `ChartWarningCategory.ChartMetadata`
- digest label: `メタデータ解析エラー`
- message: 永続化された `message` / `exception_type` から生成
- `HighlightRow = true`
- `ShowInDigest = true`
- `ShowInTooltip = true`

表示方針:

- BMS は通常の `BMSFile` に structured warning を付ける。
- bmson は通常の `LibraryChartRow.FromBmsonSong()` では `BMSFile` を持たないため、解析エラー画面では `PendingChartEntry.CreateFromBmsonSong()` 相当の warning 付き shim row として表示する。
- 失敗記録は DB に残してよいが、解析エラー画面には現在ライブラリに存在する `song` / `bmson_song` と join できるものだけを表示する。

メンテナンスツリー:

- `viewUpdateMode.ChartInfoParseErrorFilterSelected = 40` を追加する。
- `MaintenanceFilterType.ChartInfoParseErrorFilter = 40` を追加する。
- `ExecMaintenanceFilter()` の数値対応を維持する。
- `MainWindow.xaml` のメンテナンスツリーに `解析エラー` node を追加する。
- `MainWindow.cs` に selected handler を追加する。
- `MainWindowViewModel.makeBMSFilesView()` に filter case を追加する。
- `BMSLibrary` / `MainWindowViewModel` に `BMSFilesChartInfoParseFailed` などの公開口を追加する。

初期カラム:

`dataGridColumnsSettings.viewType.CHART_INFO_PARSE_ERROR` を追加し、初期表示順を以下にする。

1. `Status`
2. `PlaylistSymbols`
3. `WavHealth`
4. `BgaHealth`
5. `MovieHealth`
6. `Warning`
7. `Title`
8. `Artist`
9. `Mode`
10. `Folder`
11. `Path`
12. `Hash`

`Settings` には専用の `ChartInfoParseErrorColumnsSettings` を追加する。`FullScanColumnsSettings` の流用は避け、解析エラー画面の用途に合わせた既定列を独立させる。

### Unit 3: 右クリックで失敗記録を除去

解析エラー画面の選択行に対して、DB 上の失敗記録を削除する右クリックメニューを追加する。

表示名案:

- ja-JP: `メタデータ解析失敗記録を除去`
- en-US: `Remove metadata parse failure record`

確認文言案:

```text
メタデータ解析の失敗記録を除去します。次回起動時に再解析の対象になります。高負荷時のタイムアウト以外の理由で失敗した譜面は再度失敗する可能性が高いので、特別な理由がない限り除去しないでください。
```

処理方針:

- 選択行から md5 を収集する。
- OK / Cancel 付き確認ダイアログを表示する。
- OK の場合だけ `chart_info_parse_failure` から md5 を削除する。
- `song` / `bmson_song` / `maintenance` / `chart_info` は変更しない。
- 現在 `解析エラー` 画面を表示中なら filter を再構築する。
- 対象行の structured warning も消えるように再 materialize する。

## Parser Version Policy

- `parser_version == CurrentChartInfoParserVersion` の失敗記録は skip 対象。
- `parser_version < CurrentChartInfoParserVersion` の失敗記録は stale として再解析対象。
- 再解析が成功した場合は失敗記録を削除する。
- 再解析が失敗した場合は、現行 parser version の失敗記録として上書きする。

これにより、parser 修正や許容範囲改善後に自動で再試行できる。

## Duplicate MD5 Policy

- ライブラリ内に md5 重複譜面が存在する場合、失敗記録は md5 単位で共有される。
- 同一 md5 の複数 path が存在する場合、一覧には現在ライブラリ上で見つかる行をすべて出す。
- DB の `path` は代表 path / 最終失敗 path として扱い、主キーとしては使わない。

## Tests

### DB / schema

- `EnsureChartInfoBackfillSchema()` が `chart_info_parse_failure` と index を作成する。
- schema repair 後に既存 `chart_info` テーブルが壊れない。
- `chart_info_schema` または専用 schema version が期待値になる。

### Backfill

- parse failure 時に digest と失敗記録が保存される。
- 同一 parser version の2回目 backfill では、失敗記録により parse が skip される。
- parser version が古い失敗記録は再解析される。
- 再解析成功時に失敗記録が削除される。
- timeout は失敗記録として保存される。
- read failure は失敗記録として保存されない。
- BMS と bmson の両方で skip が効く。
- md5 重複譜面で失敗記録が重複作成されない。

### WARNING / UI

- `ChartInfoParseFailure` warning が digest / tooltip / highlight に反映される。
- 解析エラー画面で BMS の失敗譜面が表示される。
- 解析エラー画面で bmson の失敗譜面が warning 付き shim row として表示される。
- 現在ライブラリに存在しない stale failure record は解析エラー画面に出ない。
- 初期カラムが `status, playlist, wav, bga, movie, warning, title, artist, keys, folder, path, md5 hash` になる。

### Context Menu

- 解析エラー画面で選択行に失敗記録除去メニューが表示される。
- 通常ライブラリ画面では不要な除去メニューが表示されない。
- Cancel 時は DB を変更しない。
- OK 時は該当 md5 の失敗記録だけ削除する。
- 削除後に解析エラー画面から対象行が消える。
- 次回 backfill で削除済み md5 が再解析対象になる。

### Regression

- `dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release`
- `dotnet build BeMusicSeeker-decomp.sln /p:Configuration=Release`

## Risks

- `chart_info` 本体に失敗情報を混ぜると成功 metadata と誤認されるため、必ず別テーブルにする。
- `viewUpdateMode` と `MaintenanceFilterType` の数値対応がずれると誤った filter が開く。
- bmson は通常 row が `BMSFile` を持たないため、WARNING 表示には shim row が必要。
- 永続化 message が長すぎると tooltip と DB が肥大化する。
- md5 主キーにより同一 md5 の全 path がまとめて skip される。内容同一なので基本的には望ましいが、一覧表示では複数 path を materialize する必要がある。

## Future Considerations

- timeout だけを一括再解析するメニュー。
- parser version 変更時の stale failure 件数表示。
- 解析失敗理由ごとの filter / grouping。
- 失敗記録の export / diagnostics。
- path が存在しない stale failure record の cleanup UI。
