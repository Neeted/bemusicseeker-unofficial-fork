# 一覧の列と既定値

## 目的と適用範囲

`CustomTableView` の列の保存値、初期表示、幅、ツールチップ、編集可能な項目を定めます。描画・操作・仮想化は[一覧表示](table-view.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 列設定の選択と保存

列設定は `ICustomTableColumnLayout` の `Width`、`DisplayIndex`、`Visibility` で表します。列は可視のものを `DisplayIndex`、同値なら `FallbackOrder` の順で作ります。幅変更と可視列の並べ替えは設定オブジェクトへ反映し、設定保存時に永続化します。

通常・フォルダ表示は `STANDARD`、LR2互換性警告は `UNREGISTERED`、プレイリスト詳細と未所持表示は `PLAYLIST`、ゼロノートは `ZERO_NOTE`、解析失敗は `CHART_INFO_PARSE_ERROR`、リソース不足・全件確認・新規導入済みは `FULLSCAN`、重複は `DUPLICATE`、文字化けと修正済みは `ENCODING`、導入保留は `INSTALL`、履歴は `PLAY_HISTORY` を使います。プレイリスト一覧は独立した `PlaylistSummaryColumnSettings` を使います。

並べ替えや検索条件の変更は表示先の変更ではありません。現在のツリー選択へ解決してから設定を選び、同じ表示先の設定が適用済みなら再適用しません。プレイリスト一覧の設定は独立して互換性を補完します。

既定値は設定オブジェクトがないときだけ作成します。利用者が保存した幅・順序・表示状態を既定値で上書きしません。廃止された `dataGridColumnsSettings` を現行設定へ読み替えません。既知の廃止キー `*ColumnsSettings` と `BmsonColumnSettingsMigrationVersion` は従来版の設定コピーと保存時に除去しますが、未知の設定を一括削除しません。

### 初期表示

以下は、新しい設定オブジェクトを作成したときの初期可視列です。幅は `CustomTableColumnSettings` または `PlaylistSummaryColumnSettings` の既定値です。ユーザーが列幅、表示順、表示/非表示を変更した後は、保存済み設定が優先されます。

#### 通常ライブラリ

`STANDARD`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `Title` | `TITLE` | 200 |
| 3 | `Artist` | `ARTIST` | 100 |
| 4 | `Genre` | `GENRE` | 100 |
| 5 | `Mode` | `KEYS` | 50 |
| 6 | `Folder` | `FOLDER` | 140 |
| 7 | `Path` | `PATH` | 250 |
| 8 | `Clear` | `CLEAR` | 90 |
| 9 | `Rank` | `DJ LEVEL` | 60 |
| 10 | `Rate` | `RATE` | 60 |
| 11 | `Bp` | `BP` | 40 |
| 12 | `Level` | `LEVEL` | 50 |
| 13 | `ChartDifficulty` | `DIFFICULTY` | 80 |
| 14 | `ChartJudge` | `JUDGE` | 70 |
| 15 | `ChartJudgePercent` | `JUDGE%` | 60 |
| 16 | `Notes` | `NOTES` | 40 |
| 17 | `ChartLongNotes` | `LONG` | 40 |
| 18 | `ChartScratchNotes` | `SCRATCH` | 40 |
| 19 | `ChartMainBpm` | `MAINBPM` | 40 |
| 20 | `ChartMinBpm` | `MINBPM` | 40 |
| 21 | `ChartMaxBpm` | `MAXBPM` | 40 |
| 22 | `ChartSoflan` | `SOFLAN` | 40 |
| 23 | `ChartTotal` | `TOTAL` | 40 |
| 24 | `ChartTotalPerNote` | `T/N` | 40 |
| 25 | `ChartDuration` | `DURATION` | 60 |
| 26 | `ChartFeature` | `FEATURE` | 60 |
| 27 | `ChartDensity` | `DENSITY` | 40 |
| 28 | `ChartPeakDensity` | `PEAK` | 40 |
| 29 | `ChartEndDensity` | `END` | 40 |
| 30 | `PlaylistSymbols` | `PLAYLIST` | 70 |

#### LR2互換性警告

`UNREGISTERED`

LR2互換性警告画面は LR2連携モード・単独動作モード の両方で表示します。通常ライブラリよりも警告内容の確認を優先するため、`Warning` を初期表示に含めます。

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `Warning` | `WARNING` | 200 |
| 3 | `Title` | `TITLE` | 200 |
| 4 | `Artist` | `ARTIST` | 100 |
| 5 | `Genre` | `GENRE` | 100 |
| 6 | `Mode` | `KEYS` | 50 |
| 7 | `Folder` | `FOLDER` | 140 |
| 8 | `Path` | `PATH` | 250 |
| 9 | `PlaylistSymbols` | `PLAYLIST` | 70 |
| 10 | `CharcterEncoding` | `ENCODING` | 130 |

#### プレイリスト詳細

`PLAYLIST`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `Folder` | `FOLDER` | 80 |
| 3 | `Title` | `TITLE` | 200 |
| 4 | `Artist` | `ARTIST` | 100 |
| 5 | `Url1` | `URL1` | 40 |
| 6 | `Url2` | `URL2` | 40 |
| 7 | `Comment` | `COMMENT` | 200 |
| 8 | `Clear` | `CLEAR` | 90 |
| 9 | `Rank` | `DJ LEVEL` | 60 |
| 10 | `Rate` | `RATE` | 60 |
| 11 | `Bp` | `BP` | 40 |
| 12 | `ChartJudge` | `JUDGE` | 70 |
| 13 | `ChartJudgePercent` | `JUDGE%` | 60 |
| 14 | `Notes` | `NOTES` | 40 |
| 15 | `ChartLongNotes` | `LONG` | 40 |
| 16 | `ChartScratchNotes` | `SCRATCH` | 40 |
| 17 | `ChartMainBpm` | `MAINBPM` | 40 |
| 18 | `ChartMinBpm` | `MINBPM` | 40 |
| 19 | `ChartMaxBpm` | `MAXBPM` | 40 |
| 20 | `ChartSoflan` | `SOFLAN` | 40 |
| 21 | `ChartTotal` | `TOTAL` | 40 |
| 22 | `ChartTotalPerNote` | `T/N` | 40 |
| 23 | `ChartDuration` | `DURATION` | 60 |
| 24 | `ChartFeature` | `FEATURE` | 60 |
| 25 | `ChartDensity` | `DENSITY` | 40 |
| 26 | `ChartPeakDensity` | `PEAK` | 40 |
| 27 | `ChartEndDensity` | `END` | 40 |
| 28 | `PlaylistSymbols` | `PLAYLIST` | 70 |

プレイリスト詳細では `EntryLevel`、`Url1`、`Url2`、`Comment`、`Memo` など、プレイリスト編集に関係する列がヘッダー右クリックメニューから表示できます。

#### ゼロノート

`ZERO_NOTE`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `Title` | `TITLE` | 200 |
| 3 | `Artist` | `ARTIST` | 100 |
| 4 | `Mode` | `KEYS` | 50 |
| 5 | `Warning` | `WARNING` | 200 |
| 6 | `Notes` | `NOTES` | 40 |
| 7 | `PlaylistSymbols` | `PLAYLIST` | 70 |
| 8 | `Folder` | `FOLDER` | 140 |
| 9 | `Path` | `PATH` | 250 |
| 10 | `Hash` | `MD5 HASH` | 240 |

#### 譜面メタデータ解析失敗

`CHART_INFO_PARSE_ERROR`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `PlaylistSymbols` | `PLAYLIST` | 70 |
| 3 | `WavHealth` | `WAV` | 40 |
| 4 | `BgaHealth` | `BGA` | 40 |
| 5 | `MovieHealth` | `MOVIE` | 40 |
| 6 | `Warning` | `WARNING` | 200 |
| 7 | `Title` | `TITLE` | 200 |
| 8 | `Artist` | `ARTIST` | 100 |
| 9 | `Mode` | `KEYS` | 50 |
| 10 | `Folder` | `FOLDER` | 140 |
| 11 | `Path` | `PATH` | 250 |
| 12 | `Hash` | `MD5 HASH` | 240 |

#### 全件確認・新規導入済み・ファイル不足

`FULLSCAN`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `PlaylistSymbols` | `PLAYLIST` | 70 |
| 3 | `WavHealth` | `WAV` | 40 |
| 4 | `BgaHealth` | `BGA` | 40 |
| 5 | `MovieHealth` | `MOVIE` | 40 |
| 6 | `Warning` | `WARNING` | 200 |
| 7 | `InstallDst` | `INSTL DST` | 250 |
| 8 | `InstallDstTitle` | `INSTALL DST TITLE` | 200 |
| 9 | `Title` | `TITLE` | 200 |
| 10 | `InstallDstArtist` | `INSTALL DST ARTIST` | 100 |
| 11 | `Artist` | `ARTIST` | 100 |
| 12 | `Mode` | `KEYS` | 50 |
| 13 | `Folder` | `FOLDER` | 140 |
| 14 | `Path` | `PATH` | 250 |
| 15 | `Hash` | `MD5 HASH` | 240 |

`InstallDstTitle` / `InstallDstArtist` のヘッダー文字列はリソース `Header_InstallDstTitle` / `Header_InstallDstArtist` に定義されています。

#### インストール保留

`INSTALL`

初期列は `FULLSCAN` と同じ `ApplyInstallAndFullScanDefaults()` を使います。

#### 重複ファイル

`DUPLICATE`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `PlaylistSymbols` | `PLAYLIST` | 70 |
| 3 | `WavHealth` | `WAV` | 40 |
| 4 | `BgaHealth` | `BGA` | 40 |
| 5 | `MovieHealth` | `MOVIE` | 40 |
| 6 | `Warning` | `WARNING` | 200 |
| 7 | `Hash` | `MD5 HASH` | 240 |
| 8 | `Title` | `TITLE` | 200 |
| 9 | `Artist` | `ARTIST` | 100 |
| 10 | `Mode` | `KEYS` | 50 |
| 11 | `Path` | `PATH` | 250 |
| 12 | `Folder` | `FOLDER` | 140 |

#### 文字化け / 修正済み

`ENCODING`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `Status` | `♬` | 18 |
| 2 | `CharcterEncoding` | `ENCODING` | 130 |
| 3 | `Title` | `TITLE` | 200 |
| 4 | `Artist` | `ARTIST` | 100 |
| 5 | `Genre` | `GENRE` | 100 |
| 6 | `Mode` | `KEYS` | 50 |
| 7 | `Folder` | `FOLDER` | 140 |
| 8 | `Path` | `PATH` | 250 |

#### プレイリストサマリー

`PlaylistSummaryColumnSettings`

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `PlaylistId` | `ID` | 60 |
| 2 | `OutputBase` | `OUTPUT` | 100 |
| 3 | `Name` | `NAME` | 220 |
| 4 | `CompatPrefix` | `PREFIX` | 80 |
| 5 | `Symbol` | `SYMBOL` | 70 |
| 6 | `LastUpdate` | `LAST UPDATE` | 145 |
| 7 | `TotalCharts` | `TOTAL` | 80 |
| 8 | `OwnedCharts` | `OWNED` | 80 |
| 9 | `MissingCharts` | `MISSING` | 80 |
| 10 | `OwnedRatio` | `OWNED %` | 80 |
| 11 | `Link` | `LINK` | 70 |
| 12 | `IsExternalSync` | `SYNC` | 70 |
| 13 | `Status` | `STATUS` | 90 |
| 14 | `IsRootFolder` | `ROOT` | 70 |
| 15 | `BmtSort` | `BMT SORT` | 80 |
| 16 | `IsBmtOutput` | `BMT OUTPUT` | 95 |

`FolderName` (`FOLDER NAME`, 160)、`Header` (`HEADER`, 70)、`Data` (`DATA`, 70) は初期非表示で保持します。`FolderName` は `Name` の直後、`Header` / `Data` は `Link` の直後の表示順を持ちます。`Status` のヘッダーは `Resources.Playlist_summary_status_header` に定義されています。

`FolderName` はプレイリストの保存値 `output_dir` が未設定の場合でも、プロパティダイアログと同じくプレイリスト名由来の実効フォルダ名を表示します。この場合は、メイン一覧の未定義メタデータと同じ `UndefinedCellBackground` をセル背景に使う。

#### プレイログ

`PLAY_HISTORY`

プレイログ一覧は通常の譜面一覧と同じ `customTableView` を使うが、行型は `PlayHistoryRow` であり、プレイ時点・更新内容・プレイ時スコアを確認するための列を初期表示します。既定の `BEST RATE` 表示は単位 `%` を付けず、`66.67 -> 83.33` のように数値だけで表示します。

| 順序 | 列の識別子 | 見出し | 幅 |
| ---: | --- | --- | ---: |
| 1 | `PlayHistoryPlayedAt` | `DATE` | 130 |
| 2 | `PlayHistoryFolderLabels` | `FOLDER` | 90 |
| 3 | `Title` | `TITLE` | 200 |
| 4 | `PlayHistoryBestClear` | `CLEAR` | 120 |
| 5 | `PlayHistoryBestDjLevel` | `BEST DJ` | 90 |
| 6 | `PlayHistoryBestRate` | `BEST RATE` | 90 |
| 7 | `PlayHistoryBestExscore` | `BEST EXSCORE` | 90 |
| 8 | `PlayHistoryBestBp` | `BP` | 90 |
| 9 | `PlayHistoryBestCombo` | `COMBO` | 90 |
| 10 | `PlayHistoryKind` | `TYPE` | 70 |
| 11 | `PlayHistoryOption` | `OPTION` | 120 |
| 12 | `PlayHistoryOpHistory` | `OP HISTORY` | 90 |
| 13 | `PlayHistoryPlayExscore` | `PLAY EXSCORE` | 100 |
| 14 | `PlayHistoryJudges` | `JUDGES` | 180 |

`ARTIST`, `SHA256`, `RAW HASH`, `FINALIZED` などは、ヘッダーの右クリックメニューから表示できる補助列として保持します。`PROVIDER` / `SOURCE` 系は内部判定用の列設定として保持しますが、利用者向けの通常メニューには出しません。

### 初期非表示の列

全てのメイン一覧の設定は共通の列設定群を持ちます。主な初期非表示列は `EntryLevel`（80）、`Tag`（50）、`Memo`（200）、`Sha256`（480）、`Ranking`（95）、`RankingLastupdate`（95）、`Score`（40）、`Combo`（40）、`TScore`（40）、`ScoreDifficulty`（40）です。括弧内は幅です。

`EntryLevel`、`Url1`、`Url2`、`Comment`、`Memo` 等のプレイリスト編集用の列は、プレイリスト詳細に限って列選択メニューへ表示します。通常ライブラリに編集機能だけを露出させません。

### 幅の制約

`CustomTableColumn` の既定制約は `MinWidth=40`, `MaxWidth=unbounded`, `CanResize=true`, `CanReorder=true` です。以下の列だけ個別指定があります。

#### メイン一覧

| 列 | 見出し | MinWidth | MaxWidth | CanResize | CanReorder | 備考 |
| --- | --- | ---: | ---: | --- | --- | --- |
| Status | `♬` | 18 | 18 | false | false | 固定アイコン列 |
| Mode | `KEYS` | 50 | 50 | false | true | 幅固定 |
| Url1 | `URL1` | 40 | 40 | false | true | 取得用アイコン列 |
| Url2 | `URL2` | 40 | 40 | false | true | 取得用アイコン列 |
| Hash | `MD5 HASH` | 40 | 240 | true | true | 最大幅のみ制限 |
| Sha256 | `SHA256 HASH` | 40 | 480 | true | true | 最大幅のみ制限 |
| WavHealth | `WAV` | 40 | 50 | true | true | リソース健全性 |
| BgaHealth | `BGA` | 40 | 50 | true | true | リソース健全性 |
| MovieHealth | `MOVIE` | 40 | 50 | true | true | リソース健全性 |
| CharcterEncoding | `ENCODING` | 40 | 130 | true | true | 文字コード表示 |

上記以外のメイン一覧列は既定制約を使います。`Ranking` も既定制約であり、初期幅は 95 だが `MinWidth=40`, `MaxWidth=unbounded` でリサイズ可能です。`Rate` と `ChartDuration` は初期幅を 60 にしているが、個別の最小幅 / 最大幅は指定していません。

#### プレイリスト一覧

プレイリストサマリー列はすべて既定制約 (`MinWidth=40`, `MaxWidth=unbounded`, `CanResize=true`, `CanReorder=true`) を使います。

### ツールチップ

`TooltipSelector` が返す意味付きの説明は、文字列が省略されていなくても表示します。その他の通常テキスト列では、`AutoTrimTooltip=true` で実際に省略される幅の場合だけ全文を表示します。省略判定は描画と同じ書体、スコア用書体、DPI、言語設定を使います。

| 対象 | 説明 |
| --- | --- |
| メイン一覧の `Status` | `PLAY`、`LOADING`、`PAUSE`、`FORWARD`、`BACKWARD`、`SEARCHING`、`SCORE_UNSENT` の説明。`NONE` は出さない |
| `Url1` / `Url2` | `UrlToolTipText` / `UrlDiffToolTipText` |
| `Warning` | 詳細な `WarningTooltipText`。セルは短い `WarningDigestText` |
| `Comment` / `Memo` | それぞれの文字列 |
| `PlaylistSymbols` | セルの記号に対応する `RefTablesNames` |
| プレイリスト一覧の `Link` / `Header` / `Data` | 開く先の `LinkUri` / `HeaderUri` / `DataUri` |
| プレイリスト一覧の `Status` | `StatusDetail` |
| 履歴の `FOLDER` | `PlaylistNames`。他の履歴テキスト列は省略時に全文を表示 |

意味付きの説明がないメイン一覧の通常テキスト列は原則として省略時に全文を表示します。ただし `Status`、`Mode`、`Clear`、`Rank`、`Rate`、`ChartDifficulty`、`ChartJudge`、`WavHealth`、`BgaHealth`、`MovieHealth` は対象外です。プレイリスト一覧では `PlaylistId`、`OutputBase`、`CompatPrefix`、`Symbol`、`IsExternalSync`、`IsRootFolder`、`BmtSort`、`IsBmtOutput` を対象外とします。

### 列定義と変更時の整合

`CustomTableColumn` は識別子、見出し、設定、並べ替え項目、文字列・色・説明・チェック状態の取得方法、セルの種類、編集先と候補取得方法を定義します。セルの種類は `Text`、`DownloadIcon`、`StatusIcon`、`ActionText`、`CheckBox` です。`SortMemberPath` がなければ並べ替え不可、`EditPropertyName` がなければ編集不可です。

列を追加・変更するときは、設定プロパティ、生成処理、列選択メニュー、並べ替え・説明・表示形式、編集・操作、保存値の互換性補完を同じ変更で揃えます。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 既定順序、幅、表示範囲と保存値の保持 | [`CustomTableColumnSettings`](../../../BeMusicSeeker/ViewModels/CustomTableColumnSettings.cs)、[`PlaylistSummaryColumnSettings`](../../../BeMusicSeeker/ViewModels/PlaylistSummaryColumnSettings.cs) | [`CustomTableColumnSettingsTests`](../../../BeMusicSeeker.Tests/CustomTableColumnSettingsTests.cs) |
| 列の定義、意味付きの説明、表示と編集の値 | [`CustomTableColumnFactory`](../../../BeMusicSeeker/Views/CustomTableColumn.cs) | [`CustomTableColumnFactoryTests`](../../../BeMusicSeeker.Tests/CustomTableColumnFactoryTests.cs) |
| 省略判定と描画の書体・文字列の一致 | [`CustomTableTextLayoutCache`](../../../BeMusicSeeker/Views/CustomTableTextLayoutCache.cs) | [`CustomTableTextLayoutCacheTests`](../../../BeMusicSeeker.Tests/CustomTableTextLayoutCacheTests.cs) |

## 関連資料

[一覧表示](table-view.md)、[外観](appearance.md)、[設定](../runtime/settings.md)を参照します。
