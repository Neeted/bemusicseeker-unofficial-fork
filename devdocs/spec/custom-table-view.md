# CustomTableView

この文書は、現行実装の一覧表示 control `CustomTableView` と、各画面で使う初期カラム設定をまとめる。

`devdocs/plan/custom-table-view-plan.md` は移行履歴として残す。現在の挙動を確認するときはこの文書を優先する。

## 目的

`CustomTableView` は、従来の WPF `DataGrid` のセル生成コストを避けるために導入された独自描画の表 control である。現在、メインの譜面一覧とプレイリストサマリー一覧は `CustomTableView` に一本化されている。

主な利用箇所:

- `BeMusicSeeker/Views/CustomTableView.cs`
  - 独自描画、スクロール、選択、列リサイズ、列並べ替え、セル編集、右クリック要求、描画計測を担当する。
- `BeMusicSeeker/Views/CustomTableColumn.cs`
  - メイン一覧 / プレイリストサマリー一覧のカラム定義、表示 formatter、tooltip、編集可否、sort path を定義する。
- `BeMusicSeeker/ViewModels/CustomTableColumnSettings.cs`
  - メイン一覧系の列幅、表示/非表示、表示順を永続化する。
  - 旧 `dataGridColumnsSettings` は廃止し、現行設定はこの CustomTable 専用型だけを読み書きする。
- `BeMusicSeeker/ViewModels/PlaylistSummaryColumnSettings.cs`
  - プレイリストサマリー用の列幅、表示/非表示、表示順を永続化する。

## MainWindow 上の構成

`MainWindow.xaml` には 2 つの `CustomTableView` がある。

| Control | 表示条件 | ItemsSource | Column layout | Sort state |
| --- | --- | --- | --- | --- |
| `customTableView` | `IsPlaylistSummaryMode == false` | `BMSFilesView` | `ColumnsSettingsBMSFilesView` | `SortParameters.ColumnsName` / `Direction` |
| `customTablePlaylistSummary` | `IsPlaylistSummaryMode == true` | `PlaylistSummaryView` | `PlaylistSummaryColumnsSettings` | `PlaylistSummarySortParameters.ColumnsName` / `Direction` |

共通の表示値:

- `RowHeight=19`
- `HeaderHeight=22`
- メイン一覧のみ `ScoreFontFamily="{StaticResource SovjetBox}"` を渡し、CLEAR / DJ LEVEL などのスコア系表示に使う。

### メイン一覧の仮想 `IList`

`BMSFilesView` は `List<LibraryChartRow>` だけでなく、`IChartListViewMetadata` を実装した仮想 `IList` になり得る。通常ライブラリの default 表示と、その状態からの `ChartListOrder` registry 対応列 sort では、`ChartListSourceRow` と `ChartListOrder` を全件分作り、`CustomTableView` からの `Count` と index access に応じて可視行だけ `LibraryChartRow` を生成する。現行 registry は identity / install destination / ref-table 系に加えて、score 系 (`clear`, `rateDouble`, `score`, `maxcombo`, `minbp`, `rankingString`, `rankingLastupdate`, `stddevVal`, `scoreDifficulty`) と chart_info 系 (`ChartLevelSortKey`, BPM, duration, judge, feature, notes, TOTAL, density, soflan count など) を含む。`rank` は既存 sort と同じく `rateDouble` の alias として扱う。

仮想 `ChartListOrder` は列定義レジストリから Asc / Desc の order を作る。レジストリは normalized column name、alias、key kind、key selector、sort profile、prewarm 対象、main view data dependency を持ち、仮想 sort 対応列と通常 sort cache 候補、refresh dependency 判定の source of truth になる。文字列比較は `StringComparer.OrdinalIgnoreCase`、`mode` や score / chart_info 数値列は typed nullable 比較で、Desc は Asc の反転ではなく対象 key 降順 + `Title` 昇順の secondary key とする。order cache の正当性は `sourceGeneration + sortKeyGeneration + dependency generation + column + direction + rowCount` で保証する。`IdentitySortKey` は追加 generation なし、`Score` は `ScoreSnapshotVersion`、`ChartInfo` は `ChartInfoIndexVersion`、`Maintenance` は maintenance hydration generation を使う。fingerprint 再走査は行わない。`Title` / `path` / `Folder` / install destination / ref-table 表示など sort 対象値が変わる経路は、row cache の実体化状態に依存せず mutation source 側で必ず `sortKeyGeneration` を進める。score / chart_info hydrate は identity order cache を stale にしないが、対応 dependency の order cache は generation 差で再構築される。

通常ライブラリの folder filter、keyword filter、mode filter は、まず全件 source row に対する現在 sort order を取得し、その index 列を `ChartListSourceRow` 上の predicate で絞り込む。filter 変更時に同じ sort column / direction の全件 order cache が有効なら、filter subset に対して `ChartListOrder.TryCreate(...)` を再実行しない。keyword filter は score / chart info field も source row から直接読むため、summary cache の filter identity には score snapshot version と chart info index version を含める。playlist detail、maintenance、warning 系列など未対応の表示条件は既存の materialized 経路を使う。

一覧ごとの現行 sort/cache contract:

| 一覧 | source row / view | sort engine | cache identity | 備考 |
| --- | --- | --- | --- | --- |
| 通常ライブラリ root / filter | `ChartListSourceRow` + `ChartListVirtualView` | `ChartListOrder` registry | source generation、sort-key generation、dependency generation、column、direction、row count | score / chart_info sort と filter 後 order 再利用に対応 |
| プレイリスト詳細 | `PlaylistDetailSourceRow` | playlist source-row sort | playlist identity、score snapshot、chart info index | 今回の通常一覧 cache とは別契約 |
| duplicate / install / playlist summary | 既存 row model | 既存 materialized sort | 各画面固有 | 次フェーズで registry/cache contract 適用対象を分解する |
| maintenance / warning 系通常一覧列 | `LibraryChartRow` fallback | 既存 materialized sort | maintenance / warning projection 依存が未整理 | 専用 dependency と invalidation 設計後に仮想化する |

`CustomTableView` は `ItemsSource` を全列挙しない。行数は `IList.Count`、描画・選択・tooltip・右クリックなどは対象 index の indexer だけを使う。旧 view の破棄処理も `IChartListViewMetadata` を優先し、仮想 view を列挙してはいけない。通常一覧の summary 表示は `GridSummaryText` を正本にし、仮想 view 作成時には folder count を同期計算しない。folder count 未計算時は曲数だけを表示し、後続の低優先度計算が current generation と一致した場合だけ `曲数 / フォルダ数` へ更新する。

`Ctrl+Shift+C` のような選択行コピーや、ユーザーが明示した全行操作は対象行の実体化を許容する。これは一覧表示の初回描画とは別の明示操作であり、仮想 view の fallback として全件 `LibraryChartRow` を常時作る経路は持たない。

## Column Layout

列 layout は `ICustomTableColumnLayout` で統一される。

| Property | 意味 |
| --- | --- |
| `Width` | 列幅。リサイズ時に更新される。 |
| `DisplayIndex` | 表示順。列ドラッグ並べ替え時に更新される。 |
| `Visibility` | 表示 / 非表示。ヘッダー右クリックメニューから切り替える。 |

`CustomTableColumnFactory` は layout から可視列だけを取り出し、`DisplayIndex`、次に `FallbackOrder` の順で表示列を作る。

列リサイズは `CustomTableView` が対象列の `Layout.Width` を直接更新する。列並べ替えは `CustomTableDataTransfer.TryReorderVisibleColumns(...)` 経由で各 `DisplayIndex` を更新する。どちらも `Settings.Default` 配下のオブジェクトを更新するため、設定保存時に永続化される。

## View Mode と設定オブジェクト

`MainWindowViewModel.loadColumnSetting(...)` は、現在の `viewUpdateMode` に応じて `ColumnsSettingsBMSFilesView` を差し替える。

`SortUpdated`、keyword 更新、mode 更新、`TreeViewFilterNotChanged` は列セットを直接表す mode ではないため、現在選択中の tree mode へ解決してから列設定を適用する。同じ解決済み mode の列設定がすでに適用済みで、対象 settings と playlist summary settings が存在する場合は、`loadColumnSetting` を再実行しない。特に `SortUpdated` は表示 mode を変えない操作なので、列設定再適用による `columnSettingMs` を発生させない。ログ上の互換 metric `columnMs` は `prepareSwapMs + columnSettingMs + setViewMs` の区間として扱う。

| 画面 / filter | `viewUpdateMode` | 設定オブジェクト | `CustomTableColumnSettings.ViewKind` |
| --- | --- | --- | --- |
| 通常ライブラリ / フォルダ | `FolderFilterSelected` | `Settings.Default.StandardCustomTableColumnSettings` | `STANDARD` |
| LR2非対応パス | `UnregisteredFilterSelected` | `Settings.Default.UnregisteredCustomTableColumnSettings` | `UNREGISTERED` |
| プレイリスト詳細 / 未所持フィルタ | `PlaylistFilterSelected`, `PlaylistNotOwnedFilterSelected` | `Settings.Default.PlaylistCustomTableColumnSettings` | `PLAYLIST` |
| ゼロノート | `ZeroNoteFilterSelected` | `Settings.Default.ZeroNoteCustomTableColumnSettings` | `ZERO_NOTE` |
| 譜面メタデータ解析失敗 | `ChartInfoParseErrorFilterSelected` | `Settings.Default.ChartInfoParseErrorCustomTableColumnSettings` | `CHART_INFO_PARSE_ERROR` |
| ファイル不足 / full scan / 新規導入済み | `FileMissingFilterSelected`, `FileMissingIgnoredFilterSelected`, `FullScanAllChartsFilterSelected`, `NewlyInstalledFolderSelected` | `Settings.Default.FullScanCustomTableColumnSettings` | `FULLSCAN` |
| 重複ファイル | `DuplicateFilterSelected` | `Settings.Default.DuplicateCustomTableColumnSettings` | `DUPLICATE` |
| 文字化け / 修正済み | `GarbledFilterSelected`, `GarbleFixedFilterSelected` | `Settings.Default.EncodingCustomTableColumnSettings` | `ENCODING` |
| インストール保留 | `PendingInstallFolderSelected` | `Settings.Default.InstallCustomTableColumnSettings` | `INSTALL` |
| プレイリストサマリー | `IsPlaylistSummaryMode == true` | `Settings.Default.PlaylistSummaryColumnsSettings` | 専用型 |

プレイリストサマリーの設定は、メイン一覧系とは別に常に `EnsureCompatibility()` され、`PlaylistSummaryColumnsSettings` へ割り当てられる。

## 初期カラム設定

以下は、新しい設定オブジェクトを作成したときの初期可視カラムである。幅は `CustomTableColumnSettings` または `PlaylistSummaryColumnSettings` の既定値。ユーザーが列幅、表示順、表示/非表示を変更した後は、保存済み設定が優先される。

### 通常ライブラリ

`STANDARD`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | Title | `TITLE` | 200 |
| 3 | Artist | `ARTIST` | 100 |
| 4 | Genre | `GENRE` | 100 |
| 5 | Mode | `KEYS` | 50 |
| 6 | Folder | `FOLDER` | 140 |
| 7 | Path | `PATH` | 250 |
| 8 | Clear | `CLEAR` | 90 |
| 9 | Rank | `DJ LEVEL` | 60 |
| 10 | Rate | `RATE` | 60 |
| 11 | Bp | `BP` | 40 |
| 12 | Level | `LEVEL` | 50 |
| 13 | ChartDifficulty | `DIFFICULTY` | 80 |
| 14 | ChartJudge | `JUDGE` | 70 |
| 15 | ChartJudgePercent | `JUDGE%` | 60 |
| 16 | Notes | `NOTES` | 40 |
| 17 | ChartLongNotes | `LONG` | 40 |
| 18 | ChartScratchNotes | `SCRATCH` | 40 |
| 19 | ChartMainBpm | `MAINBPM` | 40 |
| 20 | ChartMinBpm | `MINBPM` | 40 |
| 21 | ChartMaxBpm | `MAXBPM` | 40 |
| 22 | ChartSoflan | `SOFLAN` | 40 |
| 23 | ChartTotal | `TOTAL` | 40 |
| 24 | ChartTotalPerNote | `T/N` | 40 |
| 25 | ChartDuration | `DURATION` | 60 |
| 26 | ChartFeature | `FEATURE` | 60 |
| 27 | ChartDensity | `DENSITY` | 40 |
| 28 | ChartPeakDensity | `PEAK` | 40 |
| 29 | ChartEndDensity | `END` | 40 |
| 30 | PlaylistSymbols | `PLAYLIST` | 70 |

### LR2非対応パス

`UNREGISTERED`

LR2非対応パス画面は、通常ライブラリよりも警告内容の確認を優先するため、`Warning` を初期表示に含める。

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | Warning | `WARNING` | 200 |
| 3 | Title | `TITLE` | 200 |
| 4 | Artist | `ARTIST` | 100 |
| 5 | Genre | `GENRE` | 100 |
| 6 | Mode | `KEYS` | 50 |
| 7 | Folder | `FOLDER` | 140 |
| 8 | Path | `PATH` | 250 |
| 9 | PlaylistSymbols | `PLAYLIST` | 70 |

### プレイリスト詳細

`PLAYLIST`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | Folder | `FOLDER` | 80 |
| 3 | Title | `TITLE` | 200 |
| 4 | Artist | `ARTIST` | 100 |
| 5 | Url1 | `URL1` | 40 |
| 6 | Url2 | `URL2` | 40 |
| 7 | Comment | `COMMENT` | 200 |
| 8 | Clear | `CLEAR` | 90 |
| 9 | Rank | `DJ LEVEL` | 60 |
| 10 | Rate | `RATE` | 60 |
| 11 | Bp | `BP` | 40 |
| 12 | ChartJudge | `JUDGE` | 70 |
| 13 | ChartJudgePercent | `JUDGE%` | 60 |
| 14 | Notes | `NOTES` | 40 |
| 15 | ChartLongNotes | `LONG` | 40 |
| 16 | ChartScratchNotes | `SCRATCH` | 40 |
| 17 | ChartMainBpm | `MAINBPM` | 40 |
| 18 | ChartMinBpm | `MINBPM` | 40 |
| 19 | ChartMaxBpm | `MAXBPM` | 40 |
| 20 | ChartSoflan | `SOFLAN` | 40 |
| 21 | ChartTotal | `TOTAL` | 40 |
| 22 | ChartTotalPerNote | `T/N` | 40 |
| 23 | ChartDuration | `DURATION` | 60 |
| 24 | ChartFeature | `FEATURE` | 60 |
| 25 | ChartDensity | `DENSITY` | 40 |
| 26 | ChartPeakDensity | `PEAK` | 40 |
| 27 | ChartEndDensity | `END` | 40 |
| 28 | PlaylistSymbols | `PLAYLIST` | 70 |

プレイリスト詳細では `EntryLevel`, `Url1`, `Url2`, `Comment`, `Memo` など、プレイリスト編集に関係する列がヘッダー右クリックメニューから表示可能になる。

### ゼロノート

`ZERO_NOTE`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | Title | `TITLE` | 200 |
| 3 | Artist | `ARTIST` | 100 |
| 4 | Mode | `KEYS` | 50 |
| 5 | Warning | `WARNING` | 200 |
| 6 | Notes | `NOTES` | 40 |
| 7 | PlaylistSymbols | `PLAYLIST` | 70 |
| 8 | Folder | `FOLDER` | 140 |
| 9 | Path | `PATH` | 250 |
| 10 | Hash | `MD5 HASH` | 240 |

### 譜面メタデータ解析失敗

`CHART_INFO_PARSE_ERROR`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | PlaylistSymbols | `PLAYLIST` | 70 |
| 3 | WavHealth | `WAV` | 40 |
| 4 | BgaHealth | `BGA` | 40 |
| 5 | MovieHealth | `MOVIE` | 40 |
| 6 | Warning | `WARNING` | 200 |
| 7 | Title | `TITLE` | 200 |
| 8 | Artist | `ARTIST` | 100 |
| 9 | Mode | `KEYS` | 50 |
| 10 | Folder | `FOLDER` | 140 |
| 11 | Path | `PATH` | 250 |
| 12 | Hash | `MD5 HASH` | 240 |

### Full scan / 新規導入済み / ファイル不足

`FULLSCAN`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | PlaylistSymbols | `PLAYLIST` | 70 |
| 3 | WavHealth | `WAV` | 40 |
| 4 | BgaHealth | `BGA` | 40 |
| 5 | MovieHealth | `MOVIE` | 40 |
| 6 | Warning | `WARNING` | 200 |
| 7 | InstallDst | `INSTL DST` | 250 |
| 8 | InstallDstTitle | `INSTALL DST TITLE` | 200 |
| 9 | Title | `TITLE` | 200 |
| 10 | InstallDstArtist | `INSTALL DST ARTIST` | 100 |
| 11 | Artist | `ARTIST` | 100 |
| 12 | Mode | `KEYS` | 50 |
| 13 | Folder | `FOLDER` | 140 |
| 14 | Path | `PATH` | 250 |
| 15 | Hash | `MD5 HASH` | 240 |

`InstallDstTitle` / `InstallDstArtist` のヘッダー文字列はリソース `Header_InstallDstTitle` / `Header_InstallDstArtist` 由来である。

### インストール保留

`INSTALL`

初期カラムは `FULLSCAN` と同じ `ApplyInstallAndFullScanDefaults()` を使う。

### 重複ファイル

`DUPLICATE`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | PlaylistSymbols | `PLAYLIST` | 70 |
| 3 | WavHealth | `WAV` | 40 |
| 4 | BgaHealth | `BGA` | 40 |
| 5 | MovieHealth | `MOVIE` | 40 |
| 6 | Warning | `WARNING` | 200 |
| 7 | Hash | `MD5 HASH` | 240 |
| 8 | Title | `TITLE` | 200 |
| 9 | Artist | `ARTIST` | 100 |
| 10 | Mode | `KEYS` | 50 |
| 11 | Path | `PATH` | 250 |
| 12 | Folder | `FOLDER` | 140 |

### 文字化け / 修正済み

`ENCODING`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | Status | `♬` | 18 |
| 2 | CharcterEncoding | `ENCODING` | 130 |
| 3 | Title | `TITLE` | 200 |
| 4 | Artist | `ARTIST` | 100 |
| 5 | Genre | `GENRE` | 100 |
| 6 | Mode | `KEYS` | 50 |
| 7 | Folder | `FOLDER` | 140 |
| 8 | Path | `PATH` | 250 |

### プレイリストサマリー

`PlaylistSummaryColumnSettings`

| Order | Column | Header | Width |
| ---: | --- | --- | ---: |
| 1 | PlaylistId | `ID` | 60 |
| 2 | Name | `NAME` | 220 |
| 3 | Symbol | `SYMBOL` | 70 |
| 4 | LastUpdate | `LAST UPDATE` | 145 |
| 5 | TotalCharts | `TOTAL` | 80 |
| 6 | OwnedCharts | `OWNED` | 80 |
| 7 | MissingCharts | `MISSING` | 80 |
| 8 | OwnedRatio | `OWNED %` | 80 |
| 9 | Link | `LINK` | 70 |
| 10 | IsExternalSync | `SYNC` | 70 |
| 11 | Status | `STATUS` | 90 |
| 12 | IsRootFolder | `ROOT` | 70 |

`Status` のヘッダーは `Resources.Playlist_summary_status_header` 由来である。

## 非表示で保持される主なカラム

`CustomTableColumnSettings` は全 view type で同じ layout object 群を持つ。初期可視でないカラムの多くは、ヘッダー右クリックメニューから表示できる。

ただし `EntryLevel`, `Url1`, `Url2`, `Comment`, `Memo` などのプレイリスト編集用カラムは、プレイリスト詳細表示時だけメニュー項目を表示する。通常ライブラリ表示では、playlist 専用の編集 surface を出さない。

主な非表示カラム:

- `EntryLevel` (`ENTRY LEVEL`, 80)
- `Tag` (`TAG`, 50)
- `Memo` (`MEMO`, 200)
- `Sha256` (`SHA256 HASH`, 480)
- `Ranking` (`RANKING`, 95)
- `RankingLastupdate` (`RANK UPDATE`, 95)
- `Score` (`SCORE`, 40)
- `Combo` (`COMBO`, 40)
- `TScore` (`T-SCORE`, 40)
- `ScoreDifficulty` (`ΔMAX`, 40)

## 列幅制約

`CustomTableColumn` の既定制約は `MinWidth=40`, `MaxWidth=unbounded`, `CanResize=true`, `CanReorder=true` である。以下の列だけ個別指定がある。

### メイン一覧

| Column | Header | MinWidth | MaxWidth | CanResize | CanReorder | 備考 |
| --- | --- | ---: | ---: | --- | --- | --- |
| Status | `♬` | 18 | 18 | false | false | 固定 icon 列 |
| Mode | `KEYS` | 50 | 50 | false | true | 幅固定 |
| Url1 | `URL1` | 40 | 40 | false | true | download icon 列 |
| Url2 | `URL2` | 40 | 40 | false | true | download icon 列 |
| Hash | `MD5 HASH` | 40 | 240 | true | true | 最大幅のみ制限 |
| Sha256 | `SHA256 HASH` | 40 | 480 | true | true | 最大幅のみ制限 |
| WavHealth | `WAV` | 40 | 50 | true | true | resource health |
| BgaHealth | `BGA` | 40 | 50 | true | true | resource health |
| MovieHealth | `MOVIE` | 40 | 50 | true | true | resource health |
| CharcterEncoding | `ENCODING` | 40 | 130 | true | true | encoding 表示 |

上記以外のメイン一覧カラムは既定制約を使う。`Ranking` も既定制約であり、初期幅は 95 だが `MinWidth=40`, `MaxWidth=unbounded` でリサイズ可能である。`Rate` と `ChartDuration` は初期幅を 60 にしているが、個別の最小幅 / 最大幅は指定していない。

### プレイリストサマリー

プレイリストサマリー列はすべて既定制約 (`MinWidth=40`, `MaxWidth=unbounded`, `CanResize=true`, `CanReorder=true`) を使う。

## Tooltip

`CustomTableColumn.GetTooltip(...)` は、列に `TooltipSelector` がある場合に意味付き tooltip 文字列を返す。意味付き tooltip は、セル本文が省略されていない場合でも常時表示する。

意味付き tooltip がない通常テキスト列では、`AutoTrimTooltip=true` かつ描画時に省略 `...` が発生する幅の場合だけ、セル全文を tooltip 表示する。省略判定は描画と同じ text style / score font / DPI / culture を使う。`Status`, icon/action/checkbox 系や、短いスコア表示列など tooltip が不要な列は `AutoTrimTooltip=false` にする。

### メイン一覧

| Column | Header | Tooltip |
| --- | --- | --- |
| Status | `♬` | `status` flag に応じた説明。`PLAY`, `LOADING`, `PAUSE`, `FORWARD`, `BACKWARD`, `SEARCHING`, `SCORE_UNSENT` で表示し、`NONE` では表示しない。 |
| Url1 | `URL1` | プレイリスト詳細 row の `UrlToolTipText`。 |
| Url2 | `URL2` | プレイリスト詳細 row の `UrlDiffToolTipText`。 |
| Warning | `WARNING` | `WarningTooltipText`。セル本文は短い `WarningDigestText` を使い、詳細は tooltip に出す。 |
| Comment | `COMMENT` | `comment` と同じ文字列。 |
| Memo | `MEMO` | `memo` と同じ文字列。 |
| PlaylistSymbols | `PLAYLIST` | `RefTablesNames`。セル本文は `RefTablesSymbols`。 |

上記以外のメイン一覧カラムは、現行実装では tooltip selector を持たない。ただし以下を除く通常テキスト列は、省略時にセル全文を tooltip 表示する。

- `Status`
- `Mode`
- `Clear`
- `Rank`
- `Rate`
- `ChartDifficulty`
- `ChartJudge`
- `WavHealth`
- `BgaHealth`
- `MovieHealth`

### プレイリストサマリー

| Column | Header | Tooltip |
| --- | --- | --- |
| Link | `LINK` | `LinkUri`。URL1 / URL2 と同様に、開く先の URL を表示する。 |
| Status | `STATUS` | `StatusDetail`。 |

上記以外のプレイリストサマリー列は、現行実装では tooltip selector を持たない。ただし以下を除く通常テキスト列は、省略時にセル全文を tooltip 表示する。

- `PlaylistId`
- `Symbol`
- `IsExternalSync`
- `IsRootFolder`

## カラム定義の意味

`CustomTableColumn` は以下を持つ。

| Field | 意味 |
| --- | --- |
| `Id` | 内部列 ID。列並べ替え、編集対象判定、cell hit test で使う。 |
| `Header` | 表示ヘッダー。固定文字列または resource 由来。 |
| `Layout` | `Width`, `DisplayIndex`, `Visibility` の永続化先。 |
| `FallbackOrder` | `DisplayIndex` が未設定の場合の安定順序。 |
| `SortMemberPath` | sort 要求で `SortParameters.ColumnsName` に設定する値。`null` の列は sort 不可。 |
| `Alignment` | cell text alignment。 |
| `TextSelector` | row object から表示文字列を作る。 |
| `ForegroundSelector` / `BackgroundSelector` | スコア色、warning 色、未定義メタデータ背景などを返す。 |
| `TooltipSelector` | tooltip 表示文字列を返す。 |
| `AutoTrimTooltip` | 意味付き tooltip がない通常テキスト列で、省略時にセル全文 tooltip を表示するか。 |
| `CheckedSelector` | checkbox cell 用。 |
| `CellKind` | `Text`, `DownloadIcon`, `StatusIcon`, `ActionText`, `CheckBox`。 |
| `EditPropertyName` | inline edit で更新する row property 名。未設定なら編集不可。 |
| `EditSuggestionsSelector` | `INSTL DST` などの候補 popup 用。 |

## 操作仕様

### 選択

`CustomTableSelectionModel` が選択状態を保持する。`SelectedIndex` は双方向 binding で view model と同期する。

マウス操作:

- 通常クリック: 単一選択。
- `Ctrl`: 選択 toggle。
- `Shift`: 範囲選択。
- drag: 行 drag 操作の開始候補になる。
- 右クリック: クリック行を選択対象へ含め、row context menu を要求する。
- 表内の空白およびスクロールバー操作: 選択状態と current cell を変更しない。

キーボード操作:

- `Up` / `Down`: current row 移動。
- `Enter`: current row の activate。
- `F2` またはテキスト入力: 編集可能 cell なら inline edit 開始。
- `Ctrl+A`: 全行選択。
- `Ctrl+C`: current cell のコピー用文字列を clipboard へコピー。
- `Ctrl+Shift+C`: 選択行を TSV として clipboard へコピー。
- Context menu key: row context menu を要求する。

コピー用文字列は `CustomTableColumn.GetEditText(...)` を使う。通常列は表示文字列と同じだが、`Url1` / `Url2` とプレイリストサマリー `Link` は画面上の icon / `Open` ではなく、開く URL 文字列をコピーする。

### ソート

ヘッダー click 時に `SortRequested` を発火する。`SortMemberPath` がない列、または status 列は sort 対象外。

同じ列を再 click すると昇順 / 降順を切り替える。sort glyph は `SortColumnName` と `SortDirection` が一致する列のヘッダー上部中央に描画される。

### 列操作

- ヘッダー境界 drag: `Width` を変更する。
- ヘッダー drag: visible columns の `DisplayIndex` を更新する。
- ヘッダー右クリック: main / summary それぞれの列表示 context menu を開く。

`Status` 列は固定アイコン列であり、リサイズ不可、並べ替え不可である。

### セル編集

編集可能な列は `EditPropertyName` を持つ。現在の主な編集対象:

- `EntryLevel`
- `Folder`
- `Url1`
- `Url2`
- `Comment`
- `Memo`
- `InstallDst`

`Url1` / `Url2` は download icon cell であり、通常 click は URL action として処理される。編集は repeat click、`F2`、またはテキスト入力などの編集開始操作から入り、編集 overlay では URL 文字列を表示する。`InstallDst` は `InstallDestinationSuggestions` から候補 list を出す。

編集確定時は `CellEditEnded` を発火し、`MainWindow` 側で対象 row property または関連 DB / playlist 更新へつなぐ。

### セルアクション

`DownloadIcon`, `ActionText`, `CheckBox` などの action cell は `CellActionRequested` を発火する。

代表例:

- `Url1` / `Url2`: download URL を開く/編集する。
- プレイリストサマリー `Link`: playlist link を開く。
- プレイリストサマリー `SYNC`: 外部同期の ON/OFF。
- プレイリストサマリー `ROOT`: root folder 出力の ON/OFF。

## 描画と性能計測

`CustomTableView` は `CustomTableSurface.OnRender(...)` でヘッダーと可視行だけを描画する。

内部に `ScrollBar` を持ち、縦横スクロールバーの表示有無は row count、可視行数、列全体幅から決まる。スクロールバーの厚みは 15px。

描画時は `CustomTableCellValueCache` を使い、row / column / generation 単位で表示値、tooltip、foreground、background、checked state を cache する。

ログ:

- `custom_table_render`
  - redraw reason、row count、visible row/column/cell count、render work time、text cache hit rate を出す。
  - すべての描画で必ず出るわけではなく、初回 reason、slow render、または常時ログ対象 reason の場合に間引いて出す。
- `table_first_visible controlType=CustomTableView`
  - メイン譜面一覧 `customTableView` の初回可視描画 timing を出す。
  - `requestToVisibleRenderMs`, `firstRenderMs`, `renderWorkMs`, `textCacheHitRate` などを含む。

## テーマ

`CustomTableView` は XAML template ではなくコード描画なので、`DynamicResource` 参照だけでは描画色が更新されない。`CustomTablePalette` が theme resource を Brush/Pen へ変換し、theme change 時に palette / cache を更新する。

標準 `ScrollBar` は `Simple Styles.xaml` の `ScrollBar.*` resource を使うため、CustomTableView 内蔵スクロールバーもアプリテーマに追従する。

詳細は [appearance-theme.md](appearance-theme.md) を参照。

## 互換と注意点

- 旧 `dataGridColumnsSettings` 系の user setting は読み替えない。新しい `*CustomTableColumnSettings` が null の場合は、現行の初期カラム設定で新規作成する。
- 旧 `*ColumnsSettings` と `BmsonColumnSettingsMigrationVersion` は、従来版 `user.config` のコピー時と設定保存時に既知の廃止キーとして削除される。未知の user setting は将来互換のため一括削除しない。
- 初期カラムは新規設定作成時だけ適用される。既存ユーザーの列幅、表示順、表示/非表示は保存済み設定が優先される。
- 新しいカラムを追加する場合は、以下を揃える。
  - `CustomTableColumnSettings` または `PlaylistSummaryColumnSettings` の layout property。
  - `CustomTableColumnFactory` の column 定義。
  - ヘッダー右クリック menu の表示切り替え項目。
  - 必要なら sort path、tooltip、formatter、編集/アクション処理。
  - `SettingsLoadedEventHandler` での null 補完と compatibility 初期化。
