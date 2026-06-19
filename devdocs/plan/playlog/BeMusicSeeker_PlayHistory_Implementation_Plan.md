# BeMusicSeeker Play History Implementation Plan

## 目的

`BeMusicSeeker_LR2_PlayHistory_Trigger_Proposal.md` で固めた LR2 / OpenLR2 の play history trigger 方針を、BeMusicSeeker 側の実装作業へ落とすための計画資料とする。

初期実装は LR2 provider を対象にする。ここでいう LR2 provider は LR2 / OpenLR2 のプレイヤー別 score DB を入力にする provider である。LR2 実装後に beatoraja provider を追加する前提で、表示用 model、UI、列設定、検索・summary の境界は最初から provider 非依存にする。

この資料は BeMusicSeeker repository 側の実装計画として `devdocs/plan/playlog/BeMusicSeeker_PlayHistory_Implementation_Plan.md` に置く。LR2 score DB に追加する table / trigger の仕様は、同じ directory の `BeMusicSeeker_LR2_PlayHistory_Trigger_Proposal.md` を正本として参照する。実装が安定したら、確定した仕様を `devdocs/spec/play-history.md` と `docs/manual.ja.md` / `docs/manual.md` へ昇格・反映する。

## 参照する既存仕様

- BeMusicSeeker の開発資料は、現行仕様を `devdocs/spec/`、作業計画を `devdocs/plan/` に置く運用である。
  - `D:\work\BeMusicSeeker-decomp\devdocs\README.md:7`
  - `D:\work\BeMusicSeeker-decomp\devdocs\README.md:11`
- UI は左 sidebar の tree で表示対象を選び、右側の一覧で内容を出す構成である。
  - `D:\work\BeMusicSeeker-decomp\docs\manual.ja.md:306`
  - `D:\work\BeMusicSeeker-decomp\docs\manual.ja.md:310`
- `CustomTableView` は main chart list と playlist summary で使われる独自描画 table で、仮想 `IList` を受けられる。
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\custom-table-view.md:9`
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\custom-table-view.md:40`
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\custom-table-view.md:60`
- 新しい column set を追加する場合は、settings、column factory、header context menu、sort / tooltip / formatter を揃える必要がある。
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\custom-table-view.md:548`
- playlist と custom folder は、`playlist` / `playlist_entry` / `playlist_course` を正本にし、LR2 custom folder は LR2 linked profile の出力である。
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\playlist-data-and-export-flow.md:17`
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\playlist-data-and-export-flow.md:120`
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\playlist-data-and-export-flow.md:169`
- `.lr2folder` の `#COMMAND` は LR2 / OpenLR2 が `song LEFT JOIN score` の WHERE 句断片として評価する。
  - `D:\work\BeMusicSeeker-decomp\devdocs\spec\playlist-data-and-export-flow.md:153`
- beatoraja 連携は既に `config_sys.json`、`playerpath`、選択 player の `score.db` を読む設定を持つ。
  - `D:\work\BeMusicSeeker-decomp\docs\manual.ja.md:152`
  - `D:\work\BeMusicSeeker-decomp\docs\manual.ja.md:156`

## 現行実装の接続点

### UI / Table

- `BeMusicSeeker/Views/MainWindow.xaml`
  - main table `customTableView` は `ChartRowsView`、`ColumnsSettingsChartRowsView`、`SortParameters` を受ける。
  - playlist summary table は別 control として `PlaylistSummaryView` を受ける。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\MainWindow.xaml:1886`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\MainWindow.xaml:1903`
- `BeMusicSeeker/Views/CustomTableColumn.cs`
  - main table の column 定義を `CustomTableColumnFactory.CreateAllMainColumns(...)` で組み立てる。
  - 既存 score 系列は `CLEAR`, `DJ LEVEL`, `RATE`, `SCORE`, `COMBO`, `BP`。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableColumn.cs:270`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableColumn.cs:377`
- `BeMusicSeeker/ViewModels/CustomTableColumnSettings.cs`
  - `ViewKind` は現状 `STANDARD`, `PLAYLIST`, `FULLSCAN`, `DUPLICATE`, `ENCODING`, `INSTALL`, `ZERO_NOTE`, `CHART_INFO_PARSE_ERROR`, `UNREGISTERED`。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\CustomTableColumnSettings.cs:69`
- `BeMusicSeeker/Views/CustomTableView.cs`
  - `ItemsSource`、`ColumnsSettings`、sort request、row context、header context、column rebuild を持つ。
  - play history view は既存 `CustomTableView` に新しい row source / column set を渡す構成で始める。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableView.cs:70`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableView.cs:82`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableView.cs:366`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\CustomTableView.cs:619`
- `BeMusicSeeker/Views/MainWindow.cs`
  - main table sort request と playlist summary sort request を別 event として受けている。
  - play history は Chart row の sort engine に混ぜず、play history 専用 sort engine を通す。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\MainWindow.cs:644`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\MainWindow.cs:660`
- 既存 row source / sort engine
  - chart list は `ChartListVirtualView` と `LibraryChartRowSortEngine`。
  - playlist detail は `PlaylistDetailVirtualView` と `PlaylistDetailSortEngine`。
  - play history は同じ virtual view pattern を使うが、Chart row として扱わない。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\ChartListVirtualView.cs:8`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\PlaylistDetailVirtualView.cs:8`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\LibraryChartRowSortEngine.cs:76`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\PlaylistDetailSortEngine.cs:16`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
  - `loadColumnSetting(...)` は `viewUpdateMode` から settings object を選んで `ColumnsSettingsChartRowsView` へ割り当てる。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\MainWindowViewModel.cs:18770`

### Score / Provider

- `BeMusicSeeker/Models/BMSLibrary.cs`
  - `ScoresByHash` を持ち、score snapshot を build する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:249`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:4436`
- `BeMusicSeeker/Models/BmsLibraryInternal/ScoreTableLoadResult.cs`
  - `ActiveScoreSource` は `None`, `Lr2`, `Beatoraja`。
  - beatoraja scores は `BeatorajaScoresBySha256` として別保持される。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ScoreTableLoadResult.cs:7`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ScoreTableLoadResult.cs:15`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
  - score 読み込みは beatoraja enabled の場合 beatoraja score DB を優先し、それ以外で LR2 score DB を読む。
  - 現状 catch で例外を握っているが、play history 導入時は診断表示・ログを持つ。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryInitializationService.cs:4771`
- `BeMusicSeeker/Models/BmsLibraryInternal/BeatorajaScoreDbLoader.cs`
  - 現状は `score.db` の `score` table から `mode = 0` の best score を sha256 key で読む。
  - play history では後続フェーズで `scoredatalog.db` / `scorelog.db` を別 loader として読む。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BeatorajaScoreDbLoader.cs:12`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BeatorajaScoreDbLoader.cs:17`
- `BeMusicSeeker/Models/BeatorajaConfigService.cs`
  - beatoraja root から `playerpath`、player id、`score.db` path を解決する。
  - play history では同じ player directory から `scorelog.db` / `scoredatalog.db` も解決する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BeatorajaConfigService.cs:17`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BeatorajaConfigService.cs:36`

### Hash / Playlist resolution

- `BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs`
  - `playlist_entry` は MD5 / SHA256 を持ち、playlist 由来の folder label 解決に使える。
  - `chart_digest_map` は LR2 MD5 と SHA256 の対応に使える。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\LR2\LR2SongDBExtended.cs:248`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\LR2\LR2SongDBExtended.cs:378`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistEntryLookupKey.cs`
  - lookup key は MD5 優先で構成される。
  - LR2 play history row の chart 解決もこの優先順に合わせる。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\PlaylistEntryLookupKey.cs:24`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistLibraryResolveIndexSnapshot.cs`
  - playlist entry と library chart の resolve index を作る。
  - play history projection はここへ直接状態を足すのではなく、read model 側で snapshot を参照して解決する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\PlaylistLibraryResolveIndexSnapshot.cs:97`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\PlaylistLibraryResolveIndexSnapshot.cs:109`
- `BeMusicSeeker/Models/BMSLibrary.cs`
  - `ResolveChartInfo` と `chart_info` index は表示名・難易度表 label の補助に使える。
  - play history から `chart_info` を更新しない。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:9787`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:10000`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:18586`

### LR2 custom folder output

- `BeMusicSeeker/Models/LR2/CustomFolderSortTypeExt.cs`
  - `SCORE`, `MISS`, `PLAYCOUNT`, `ADDDATE` など playlist 内の譜面並び順 sort type の表示名・column 名変換を持つ。
  - `LAST PLAY SORT` は folder sort key ではなく出力フォルダ種別なので、初期実装では `CustomFolderSortType` / `CustomFolderSortTypeExt` へ追加しない。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\LR2\CustomFolderSortTypeExt.cs:7`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\LR2\CustomFolderSortTypeExt.cs:81`
- `BeMusicSeeker/Models/BMSPlaylist.cs`
  - playlist から LR2 custom folder の `#COMMAND` と `folder` table projection を生成する。
  - LAST PLAY SORT は `.bmt` ではなく LR2 custom folder 出力側だけに追加する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSPlaylist.cs:2975`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSPlaylist.cs:3162`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSPlaylist.cs:5556`

### Settings / View mode

- `BeMusicSeeker/Properties/Settings.cs`
  - standard / playlist / summary など view ごとの column settings default を持つ。
  - `PLAY_HISTORY` は既存 view 設定と namespace を分けて追加する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Properties\Settings.cs:487`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Properties\Settings.cs:529`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Properties\Settings.cs:1542`
- `BeMusicSeeker/Properties/PortableSettingsProvider.cs`
  - portable settings 対象へ play history column settings / display target set を追加する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Properties\PortableSettingsProvider.cs:27`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
  - `viewUpdateMode` に応じて表示 view と column settings を切り替える。
  - play history view は chart list / playlist detail とは独立した mode にする。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\MainWindowViewModel.cs:5293`

### Row 操作 / Context Menu

- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
  - row から `ChartFile`、MD5、SHA256、repository SHA256、操作 capability を解決する。
  - play history row は Chart row ではないため、ChartFile 起点の操作へ無理に載せない。
  - 解決済み chart row は repository SHA256 / MD5 を使う。未解決 row は raw hash copy だけを許可する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\GridRowResolver.cs:211`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\GridRowResolver.cs:223`
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\GridRowResolver.cs:274`
- `BeMusicSeeker/Views/MainWindow.xaml`
  - table context menu には BMS-IR / Mocha / MinIR / URL / Explorer / install / maintenance などが並ぶ。
  - play history row では chart 操作を広く出さないよう、row kind 判定と PlayHistory 専用 context menu branch を追加する。
  - `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Views\MainWindow.xaml:147`

## 実装方針

### Profile boundary

初期の LR2 provider は LR2 linked profile 専用とする。stand-alone mode では LR2 score DB が正本として存在しないため、LR2 trigger install、LR2 play history read、LR2 LAST PLAY SORT custom folder は有効化しない。

beatoraja provider は LR2 linked profile には依存しないが、初期実装には含めない。既存 beatoraja 設定 (`BeatorajaRootPath`, `BeatorajaPlayerId`) を使う後続 provider として追加する。

Play history provider は既存 score display source (`ActiveScoreSource`) と別境界にする。現行の score hydration は beatoraja score DB 有効時に LR2 score DB 読み込みを skip するが、LR2 play history schema check / install / read は LR2 linked profile の score DB path がある限り実行可能にする。実装時に `ActiveScoreSource == Lr2` を LR2 play history provider の有効条件にしない。

### Existing workflow boundary

play history の導入・読み取り・表示は、次の既存 workflow を起動しない。

- BMS file diff / full scan
- playlist reload / external sync
- playlist `last_update` 更新
- `.bmt` 再出力
- LR2IR ranking refresh
- chart_info / maintenance の再解析

LR2 custom folder の LAST PLAY SORT は `.lr2folder` と LR2 `folder` row の projection 変更であるため、Phase 4 の出力設定変更時だけ custom folder materialization 対象になる。LR2 で曲をプレイして `bms_lr2_last_play` が更新された後は、`.lr2folder` 本文を再生成しなくても、LR2 / OpenLR2 側の SQL 評価時に最新の `bms_lr2_last_play` が参照される。

### LR2 schema contract

LR2 score DB に導入する schema / trigger SQL の正本は `BeMusicSeeker_LR2_PlayHistory_Trigger_Proposal.md` の Table 定義、`bms_lr2_play_pending`、Trigger 定義とする。実装時に plan 側の要約だけから SQL を再構成しない。

Phase 1 で扱う object は次の一式である。

- tables: `bms_lr2_last_play`, `bms_lr2_play_history`, `bms_lr2_play_pending`
- indexes: `idx_bms_lr2_play_history_hash_time`, `idx_bms_lr2_play_history_time`
- triggers: `bms_lr2_score_history_after_insert`, `bms_lr2_score_history_after_update_playcount`, `bms_lr2_player_history_after_update`, `bms_lr2_player_history_cleanup_stale_pending`

schema check は `sqlite_master` と table columns を read-only connection で確認し、`Installed` / `NotInstalled` / `Repairable` / `ManualRepairRequired` / `Unreadable` / `SkippedProfile` の診断へ分類する。

- `NotInstalled`: `bms_lr2_*` object が存在しない。install で全 object を作る。
- `Repairable`: table columns は要求を満たすが、index / trigger が欠落、または trigger SQL が現行定義と一致しない。repair は trigger を drop / recreate し、欠落 index を作る。履歴 table は drop しない。
- `ManualRepairRequired`: required table の required column が欠落している、型・not-null 前提が壊れている、または table 名が衝突している。履歴を失う可能性があるため自動 repair しない。
- `Unreadable`: path 不明、lock、read-only open 失敗、SQLite error など。成功扱いにしない。
- `SkippedProfile`: LR2 linked profile ではない。

install / repair はユーザーの明示操作でだけ write connection を開く。`CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` は使ってよいが、既存 table の破壊的再作成、履歴 row の削除、既存 score row からの backfill は行わない。

### Settings impact

play history schema の導入・修復は、設定保存や startup の副作用として自動実行しない。BeMusicSeeker は startup / 設定画面表示時に LR2 score DB を read-only で確認し、導入済み / 未導入 / 不整合 / 読み取り不可の状態だけを表示する。実際に LR2 score DB へ table / trigger を追加する操作は、設定画面の明示ボタンからだけ実行する。

play history schema 状態確認や LR2 score DB 変更は、既存の重い reload ではなく score DB schema check / score refresh / play history view refresh の範囲で扱う。設定保存時の impact は次の粒度に分ける。

| 変更 | 影響 |
| --- | --- |
| LR2 score DB path 変更 | schema check、score snapshot reload、play history view invalidation |
| play history schema 状態確認 | read-only schema check、diagnostics 更新 |
| play history schema 導入 / 修復ボタン実行 | schema install / repair、diagnostics 更新、score refresh、play history view invalidation |
| custom folder LAST PLAY SORT 出力設定 | custom folder projection / materialization |
| 表示対象セット変更 | play history view rebuild のみ |
| beatoraja player 変更 | Phase 7 以降、beatoraja provider view invalidation |

startup / reload 中に score DB が読めない場合は、失敗を隠して成功扱いにしない。UI には play history diagnostics として出し、既存 chart library の初期化自体は可能な範囲で継続する。

診断 result は実装初期から provider / stage / severity / code / message / source path を持つ軽量 model として扱う。Phase 6 は診断 UI と manual の充実フェーズであり、Phase 1-5 の失敗を握りつぶすための後回し枠ではない。

### Schema install UI

設定画面の `一般 > 動作モード > LR2と連携する` に、LR2 play history schema の状態表示と明示操作ボタンを置く。`LR2 song.db データを再同期` は song DB / playlist / folder projection の操作であり、play history schema install はプレイヤー別 score DB の操作であるため、同じ行に単純に並べず、短い label で区切る。

推奨表示:

```text
プレイログ: 未導入
[プレイログを有効化...]
```

状態:

- `未導入`: `[プレイログを有効化...]` を表示する。
- `導入済み`: `[再確認]` を表示するか、操作ボタンを disabled にする。
- `一部不足 / 不整合`: `[修復...]` を表示する。
- `DBロック / 読み取り専用 / path不明`: 操作ボタンを disabled にし、diagnostics に理由を出す。
- `LR2 linked profile ではない`: 表示しない、または disabled にする。

導入・修復ボタン押下時は、LR2 を終了してから実行すること、プレイヤー別 score DB に table / trigger を追加すること、既存 score / player table は変更しないこと、score DB backup を推奨することを警告 dialog で確認する。

初期実装では、導入済み schema を削除する UI は作らない。削除はメンテナンス系の後続 scope とし、通常設定画面には置かない。

### Provider model

`PlayHistoryRow` は provider 非依存の表示 row とする。

```text
PlayHistoryRow
  Provider: Lr2 / Beatoraja
  SourceProfile
  SourceKey
  ChartKey
  PlayedAt
  PeriodKey
  HashKind
  ActualResult
  BestDelta
  FolderLabels
  Raw
```

`ActualResult` は今回プレイとして確定できる値、`BestDelta` は best row の old -> new 変化とする。LR2 trigger 由来では BP / clear / option が best 更新情報になり得る。beatoraja `scoredatalog.db` 由来では単曲 play の actual result として扱える値が増える。

`0` と不明は分ける。取れない値は `null`、UI 表示は空欄。

LR2 provider の表示値は次の境界を守る。

- `ActualResult`: `finalized = 1` の `player` 差分から復元できる `PlayExScore`, `Judges`, `PlaytimeDelta` を入れる。LR2 では実 BP、実 clear、実 option は復元できないため入れない。
- `BestDelta`: `score` row の old/new から `BestExScore`, `BestRate`, `BestDjLevel`, `BestBp`, `BestClear`, `BestCombo`, `OpBest`, `OpHistoryNewBits` を入れる。値が変化していない列は、今回プレイの実値のように表示しない。
- `PLAY EXSCORE` / `JUDGES` のように `PLAY` が付く列だけを今回プレイの実値として扱う。`BEST` が付く列は best row の変化であり、best 更新しなかったプレイでは空欄にする。

### Storage boundary

LR2 provider:

- LR2 player score DB に `bms_lr2_*` table / trigger を追加する。
- BeMusicSeeker は trigger install / schema check / read を担当する。
- `bms_lr2_last_play` と `bms_lr2_play_history` は導入後から空で開始する。
- `bms_lr2_play_pending` は `score` trigger と `player` trigger の一時結合用 table であり、表示用履歴としては読まない。
- BeMusicSeeker app-owned DB に LR2 履歴の正本を複製しない。読み取り cache は初期実装では持たない。

beatoraja provider:

- DB へ trigger は追加しない。
- `score.db`, `scorelog.db`, `scoredatalog.db` を read-only input とする。
- LR2 provider 実装後に adapter を追加する。

### View / UI boundary

- 左 tree に `プレイログ` root を追加する。
- playlist tree からの直接動線は追加しない。
- Phase 3 の初期表示対象は `すべて` 固定にする。playlist / 表示対象セット dropdown は Phase 5 で追加する。
- `プレイログ` root には `すべて`、`今日`、`昨日`、`最近 7 日`、`最近 30 日`、`年別 > 月 > 日`、`未確定 / 診断` node を置く。
- 期間 node を選ぶと、右 table は選択期間全体の `PlayHistoryRow` を出す。
- table 上部 summary は、全期間 / 固定期間 / 年 / 月 / 日の選択 node に応じて期間 digest を出す。
- 共有画像生成やカード型 digest は初期 scope 外。

### LAST PLAY SORT

- LR2 provider 初期実装で `LAST PLAY SORT` を custom folder 出力へ追加する。
- 初期は最新順のみ。
- `bms_lr2_last_play` を見る。
- 既存 `score` row から backfill しない。
- SQL は素朴な correlated subquery を基本にする。
- active LR2 linked profile の schema status が `Installed` ではない場合、LAST PLAY SORT の出力設定は UI で選択不可にし、materialization は diagnostics 付きで失敗させる。存在しない `bms_lr2_last_play` を参照する `.lr2folder` / `folder` row は新規生成しない。
- schema が導入済みで `bms_lr2_last_play` に row がない譜面は、SQL 上の `NULL` により最新順の末尾へ回る。これは fallback ではなく、履歴機能導入後に未観測という意味である。

```text
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

### 検索

Play history view 用の keyword search context を追加する。既存 chart list の `GridKeywordSearchQuery` をそのまま chart row として使い回すのではなく、対象 row type を分ける。

初期 field:

- `date:`
- `year:`
- `month:`
- `provider:`
- `playlist:`
- `folder:`
- `title:`
- `artist:`
- `clear:`
- `djlevel:` / `rank:`
- `rate:`
- `score:` / `exscore:`
- `bp:`
- `combo:`
- `option:`
- `kind:`
- `finalized:`
- `hash:` / `sha256:`

## 実装フェーズ

### Phase 0: 資料・仕様固定

成果:

- LR2 trigger proposal と本実装 plan の整合。
- BeMusicSeeker repository 内での置き場所を `devdocs/plan/playlog/` に固定。
- マニュアル更新範囲の洗い出し。

作業:

- [x] `devdocs/plan/playlog/BeMusicSeeker_LR2_PlayHistory_Trigger_Proposal.md` を LR2 score DB trigger 仕様資料として維持する。
- [x] 本資料を BeMusicSeeker 実装計画として `devdocs/plan/playlog/BeMusicSeeker_PlayHistory_Implementation_Plan.md` に維持する。
- [ ] 実装後に `devdocs/spec/play-history.md` へ昇格する項目を決める。
- [x] `docs/manual.ja.md` / `docs/manual.md` の追加章案を作る。

完了条件:

- 実装者が provider model / UI / LR2 DB trigger / schema repair / LAST PLAY SORT の境界を迷わず読める。

### Phase 1: LR2 trigger install / schema verification

成果:

- BeMusicSeeker の設定画面から、ユーザーの明示操作で LR2 score DB に play history table / trigger を導入できる。
- SQLite 3.6.7 互換 SQL だけを使う。
- 導入済み / 未導入 / 壊れた schema を診断できる。
- LR2 linked profile 以外では install を実行しない。
- startup / 設定画面表示時は read-only の schema check だけを行い、自動導入しない。

主な対象コード:

- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryProfile.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/Properties/Settings.cs`
- `BeMusicSeeker/Views/SettingDialog.xaml`

作業:

- [x] `Lr2PlayHistorySchemaService` を追加する。
- [x] `BeMusicSeeker_LR2_PlayHistory_Trigger_Proposal.md` の SQL を install SQL の正本としてコードへ持ち込み、SQL text snapshot test で固定する。
- [x] `bms_lr2_last_play` / `bms_lr2_play_history` / `bms_lr2_play_pending`、2 index、4 trigger を install 対象として列挙する。
- [x] read-only schema check path を作り、`Installed` / `NotInstalled` / `Repairable` / `ManualRepairRequired` / `Unreadable` / `SkippedProfile` を返す。
- [x] ユーザー明示操作用の install / repair path を作る。
- [x] install path で `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` / `CREATE TRIGGER IF NOT EXISTS` を実行する。
- [x] repair path では missing index を作成し、trigger 欠落・trigger SQL 不一致だけを drop / recreate する。
- [x] required table の required column 不足や互換不能な table 衝突は `ManualRepairRequired` とし、table drop / truncate / rename は行わない。
- [x] score DB path がない / read-only / lock / SQLite error の診断結果を返す。
- [x] 設定画面の `LR2と連携する` 配下に play history schema 状態表示と `[プレイログを有効化...]` / `[修復...]` ボタンを追加する。
- [x] 導入・修復ボタン押下時に warning dialog を表示し、OK の場合だけ score DB へ書き込む。
- [x] warning dialog には、LR2 終了、score DB への table / trigger 追加、既存 score / player table 非変更、backup 推奨を明記する。
- [x] trigger install を設定保存時または startup score hydration 前に自動実行しない。
- [x] trigger install 成功後も `bms_lr2_last_play` / `bms_lr2_play_history` は空開始であることを UI / log 上で自然に扱う。
- [x] LR2 play history schema check / install は `ActiveScoreSource` ではなく LR2 linked profile の score DB path を見る。
- [x] `application.log` または `install-performance.log` に `play_history_schema_*` を出す。
- [x] LR2 linked profile でない場合は `skipped_profile` として診断する。
- [x] schema install は file scan / playlist reload / `.bmt` 出力を要求しない。
- [x] 導入済み schema を削除する UI は初期 scope 外にする。

テスト:

- [x] temp SQLite score DB に schema / trigger を作れる。
- [x] 2 回実行して冪等。
- [x] `bms_lr2_play_pending`、2 index、4 trigger の作成漏れを検出できる。
- [x] trigger SQL 不一致は repairable になり、repair で trigger だけ再作成される。
- [x] required table の column 不足は `ManualRepairRequired` になり、table が drop されない。
- [x] startup / 設定画面表示の schema check が read-only で完了し、table / trigger を作らない。
- [x] warning dialog で cancel した場合、DB が変更されない。
- [x] SQLite 3.6.7 非互換構文が入っていないことを SQL text snapshot で固定。
- [x] read-only DB で失敗が診断扱いになり、成功扱いにならない。
- [x] stand-alone mode では install が skip される。
- [x] beatoraja score DB を表示 score source にしていても、LR2 linked profile の score DB path があれば schema check は実行される。

完了条件:

- LR2 を変更せず、ユーザーの明示操作で score DB へ履歴 trigger を導入できる。
- 失敗時にユーザーが何が起きたか分かる。

### Phase 2: LR2 play history read model

成果:

- `bms_lr2_play_history` / `bms_lr2_last_play` を `PlayHistoryRow` へ投影できる。
- Chart / playlist / chart_info と照合し、表示用 title / artist / folder labels / sha256 を解決できる。
- 読み取りは play history view の入力であり、playlist 正本や playlist `last_update` を変更しない。

主な対象コード:

- `BeMusicSeeker/Models/BmsLibraryInternal/*`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistLibraryResolveIndexSnapshot.cs`
- `BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistEntryLookupKey.cs`
- `BeMusicSeeker/ViewModels/PlayHistoryRow.cs`
- `BeMusicSeeker/ViewModels/PlayHistoryVirtualView.cs`
- `BeMusicSeeker/ViewModels/PlayHistorySortEngine.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`

作業:

- [x] `PlayHistoryProvider` / `PlayHistorySourceProfile` / `PlayHistoryRow` を追加する。
- [x] `Lr2PlayHistoryReader` を追加し、期間・provider・playlist 対象で query できるようにする。
- [x] `Lr2PlayHistoryReader` は score snapshot の `ActiveScoreSource` からではなく、LR2 linked profile の score DB path から read-only で開く。
- [x] MD5 -> Chart / playlist_entry / chart_info / SHA256 mapping を解決する projection service を作る。
- [x] `playlist_entry` と `chart_digest_map` を使い、MD5 優先で chart / SHA256 を解決する。
- [x] 通常 chart list への `LAST PLAY` column 追加は初期 scope 外に固定する。後続で追加する場合の対象コードは `ChartListSourceRow` / `LibraryChartRow` / `PlaylistDetailSourceRow` / `PlaylistDetailRow` と各 sort engine。
- [x] `HashKind = chart / course / unknown` を解決する。
- [x] `finalized = 0` は通常一覧から除外し、診断 filter で見えるようにする。
- [x] option / clear / rank / rate / DJ LEVEL / BP / combo / EX score の display formatter を作る。
- [x] LR2 provider では `ActualResult` と `BestDelta` を別 projection にし、best 更新値を今回プレイ実値として表示しない。
- [ ] 日別 / 月別 / 年別 summary 用 aggregate を作る。
- [x] score DB lock / missing table / malformed row の診断 result を作る。
- [x] 読み取り失敗時も playlist reload や full scan へ fallback しない。

テスト:

- [x] score update row から score / BP / clear / combo の best delta が出る。
- [x] player 差分がある row だけ summary の playtime / judge count に反映される。
- [x] Chart 解決できない hash でも raw hash row として落ちない。
- [x] `finalized = 0` は通常 query に出ない。
- [x] `NO PLAY -> value` 表示、BP 初回表示、option 表示条件を固定。
- [x] best 更新しなかった LR2 row では `BEST RATE` / `BEST DJ` / `BEST EXSCORE` が今回結果として表示されない。
- [x] `finalized = 1` の row では `PLAY EXSCORE` / `JUDGES` が player 差分から出る。

完了条件:

- UI なしでも、unit test で `PlayHistoryRow` と summary が作れる。

### Phase 3: Play log tree / period selection / table integration

成果:

- 左 sidebar に `プレイログ` root を追加する。
- `すべて` / `今日` / `昨日` / `最近 7 日` / `最近 30 日` / 年 / 月 / 日 node を選ぶと、右 table に該当期間の履歴が出る。
- `未確定 / 診断` node で `finalized = 0` や projection 異常を確認できる。
- table 上部 summary が選択期間に追従する。

主な対象コード:

- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/Views/CustomTableView.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
- `BeMusicSeeker/ViewModels/PlayHistoryVirtualView.cs`
- `BeMusicSeeker/ViewModels/PlayHistorySortEngine.cs`
- `BeMusicSeeker/ViewModels/CustomTableColumnSettings.cs`
- `BeMusicSeeker/Views/CustomTableColumn.cs`
- `BeMusicSeeker/Properties/Settings.cs`
- `BeMusicSeeker/Properties/PortableSettingsProvider.cs`

作業:

- [x] `viewUpdateMode.PlayHistorySelected` を追加する。
- [x] `MainViewOperationSection` / chart operation scope / row activation / context menu の view mode 判定に play history を追加する。
- [x] `CustomTableColumnSettings.ViewKind.PLAY_HISTORY` を追加する。
- [x] `Settings.Default.PlayHistoryCustomTableColumnSettings` を追加する。
- [x] portable settings provider に play history settings を追加する。
- [x] `loadColumnSetting(...)` に play history settings を追加する。
- [x] `PlayHistoryColumns` を `CustomTableColumnFactory` に追加する。
- [x] Header context menu に play history columns を追加する。
- [x] `PlayHistoryRowsView` を `customTableView.ItemsSource` へ出せるようにする。
- [x] `CustomTableView` の row context / header context / sort request で play history row を識別する。
- [x] PlayHistory view 中は playlist 編集用 column / DnD / cell edit を無効にする。`customTableView` の固定 `RowDragKind="PlaylistDropCandidateRows"` に依存せず、`ChartRowsViewRowDragKind` binding で PlayHistory row を playlist drop candidate にしない。
- [ ] `GridRowResolver` に PlayHistory row 用の hash-only operation target を追加し、未解決 row は raw hash copy のみにする。
- [x] `GridSummaryText` または専用 summary binding で期間 digest を出す。
- [x] Phase 3 では表示対象 dropdown を作らず、全履歴を対象にする。`FOLDER` は解決できた playlist / folder label の表示だけに使い、絞り込みは Phase 5 に送る。
- [x] play history view rebuild は score DB read と projection に限定し、library catalog 再構築を要求しない。

初期可視 columns:

| Order | Column | Header | 備考 |
| ---: | --- | --- | --- |
| 1 | PlayedAt | `DATE` | `yyyy/MM/dd HH:mm:ss` |
| 2 | FolderLabels | `FOLDER` | Phase 3 は解決できた playlist / folder label を列挙。対象 playlist / 表示対象セット由来の絞り込みは Phase 5 |
| 3 | Title | `TITLE` | Chart 解決できない場合は raw title なし |
| 4 | BestClear | `CLEAR` | BestDelta。更新時だけ old -> new、初回は NO PLAY -> value |
| 5 | BestDjLevel | `BEST DJ` | BestDelta。best EX score が変化した row だけ表示 |
| 6 | BestRate | `BEST RATE` | BestDelta。best EX score が変化した row だけ表示 |
| 7 | BestBp | `BP` | BestDelta。初回 BP は悪化表示しない |
| 8 | BestCombo | `COMBO` | BestDelta。更新時 old -> new |
| 9 | Kind | `TYPE` | score / BP / clear / combo / play only |
| 10 | OpHistory | `OP HISTORY` | 新規 bit だけセル表示、詳細は tooltip |

切替 columns:

- `BEST EXSCORE`
- `PLAY EXSCORE`
- `JUDGES`
- `OPTION`
- `SHA256`
- `PROVIDER`
- `SOURCE`
- `RAW HASH`
- `FINALIZED`

テスト:

- [x] `PLAY_HISTORY` settings が null から作成される。
- [x] 既存 view の column settings が壊れない。
- [x] `すべて` / `今日` / `昨日` / `最近 7 日` / `最近 30 日` node の epoch range が期待通りになる。
- [ ] 年 / 月 / 日 node の epoch range が期待通りになる。
- [x] `未確定 / 診断` node では `finalized = 0` と projection diagnostics が見える。
- [x] play history row は playlist tree drop candidate にならない。
- [x] play history row 右 click では通常 chart 操作 context menu を開かない。
- [ ] play history 専用 context menu で、解決済み row の repository / hash 系と、未解決 row の raw hash copy だけを出す。
- [x] play history row activation は chart 再生 / explorer / playlist edit を起動しない。

完了条件:

- LR2 履歴を一覧・sort・期間選択・summary 表示できる。

### Phase 4: LAST PLAY SORT custom folder output

成果:

- LR2 custom folder 出力に `LAST PLAY SORT` を追加する。
- 初期は最新順のみ。

主な対象コード:

- `BeMusicSeeker/Models/BMSPlaylist.cs`
- `BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2ManagedCustomFolderOutputLayout.cs`
- `BeMusicSeeker/Views/PlaylistPropertyDialog.xaml`
- `BeMusicSeeker.Tests/BmsPlaylistUpdateTests.cs`
- `D:\work\BeMusicSeeker-decomp\devdocs\spec\playlist-data-and-export-flow.md`

作業:

- [ ] `LR2SongDBExtended.playlist.CustomFolderType` に `LastPlaySortFolder` を追加する。
- [ ] `LegacyAllFolders` / `AllFolders` / `NormalizeCustomFolderOutputMask` / `ignore_folder_output` 既存値の互換を崩さない bit を割り当てる。
- [ ] `CustomFolderSortType` / `CustomFolderSortTypeExt` には追加しない。
- [ ] `BMSPlaylist` の custom folder definition builder と `Lr2ManagedCustomFolderOutputLayout` の relative path / count 計算へ `LastPlaySortFolder` を追加する。
- [ ] playlist property / bulk edit / output bit へ追加する。
- [ ] LR2 play history schema status が `Installed` でない profile では、LastPlaySortFolder の UI を disabled にし、保存済み bit が ON の場合も materialization 前に diagnostics 付きで停止する。
- [ ] `.lr2folder` projection に `ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC` を追加する。
- [ ] `folder` table sync projection にも同じ command を入れる。
- [ ] `bms_lr2_last_play` が無い場合は score / playcount / playlist 更新日時へ意味を変えて fallback しない。未導入として UI / diagnostics に出し、存在しない table を参照する command を生成しない。
- [ ] `playlist_custom_folder_output_status` の fingerprint に last play sort output bit を含める。
- [ ] `docs/manual.ja.md` / `docs/manual.md` / spec に `LAST PLAY SORT` を追記する。
- [ ] LR2 で play 後に `.lr2folder` 再出力不要で並びが変わることを仕様として明記する。

テスト:

- [ ] `.lr2folder` に LAST PLAY SORT command が出る。
- [ ] output bit OFF の場合は出ない。
- [ ] schema 未導入で output bit ON の場合は diagnostics 付きで materialization が失敗し、LAST PLAY SORT command を生成しない。
- [ ] managed cleanup / folder table sync が既存 sort folder と同じように動く。
- [ ] schema 導入済みで未観測譜面に row が無い場合は、`NULL` sort として末尾へ回る command になる。

完了条件:

- LR2 / OpenLR2 上で、履歴機能導入後の last play 最新順 custom folder を使える。

### Phase 5: Search / filter / display target set

成果:

- Play history view で keyword search と対象 playlist filter を使える。
- 表示対象セットを user settings に JSON として保存できる。初期実装では app-owned DB table を追加しない。

主な対象コード:

- `BeMusicSeeker/ViewModels/GridKeywordSearchQuery.cs`
- `BeMusicSeeker/ViewModels/GridKeywordSearchCompletion.cs`
- `BeMusicSeeker/ViewModels/KeywordSearchHistoryStore.cs`
- `BeMusicSeeker/Properties/Settings.cs`
- `BeMusicSeeker/Properties/PortableSettingsProvider.cs`
- `BeMusicSeeker/Views/MainWindow.xaml`

作業:

- [ ] `GridKeywordSearchContext.PlayHistory` を追加する。
- [ ] PlayHistoryRow 用 matcher を実装する。
- [ ] field completion に play history fields を追加する。
- [ ] `プレイログ` 右上 dropdown の対象を `すべて` / playlist / 表示対象セットにする。
- [ ] 表示対象セットは `Settings.Default.PlayHistoryDisplayTargetSetsJson` のような settings へ保存し、portable settings 対象に含める。
- [ ] 表示対象セットは playlist identity / folder label の参照集合として扱い、playlist 正本や `playlist.last_update` は変更しない。
- [ ] FOLDER column は対象 playlist 選択時は playlist folder、それ以外は表示対象セット内の `org_symbol + level` を列挙する。

テスト:

- [ ] `date:` / `year:` / `month:` / `kind:` / `clear:` / `playlist:` / `folder:` / `finalized:` が期待通り絞り込む。
- [ ] field completion と unknown field diagnostics が play history fields と一致する。
- [ ] 表示対象セットの変更で FOLDER 表示と row filter が変わる。
- [ ] 既存 chart list / playlist detail の keyword search が壊れない。

完了条件:

- 期間だけでなく、更新種別・表・folder・譜面名で履歴を探せる。

### Phase 6: Diagnostics / maintenance / manual

成果:

- Phase 1-5 で出している trigger install / read / projection の診断を、設定画面・メンテナンス・manual から確認できる。
- ユーザーマニュアルに最小限の説明を追加する。

作業:

- [ ] 設定画面またはメンテナンスに play history status を表示する。
- [ ] `finalized = 0` や異常値を診断表示に出す。
- [ ] `LR2 score DB に履歴 trigger を導入する` 操作の注意文を追加する。
- [ ] backup 対象に score DB を含める注意を更新する。
- [x] `docs/manual.ja.md` に `プレイログ` 章、`docs/manual.md` に対応する英語章を追加する。
- [ ] `devdocs/spec/play-history.md` を作り、実装済み仕様を正本化する。
- [ ] log event を `play_history_schema_*`, `play_history_read_*`, `play_history_projection_*`, `play_history_view_*` のように段階別に出す。
  - 現状: `play_history_schema_*` と main view build / diagnostics log は出力済み。read / projection / view の event 名整理は後続で行う。

テスト:

- [ ] schema missing / trigger missing / locked / read-only の表示確認。
- [ ] manual 画像更新は UI mock ではなく実画面 screenshot で行う。日英 manual の参照画像・説明が同じ実装状態を指すようにする。

完了条件:

- ユーザーが導入・未記録・異常値の意味を理解できる。

### Phase 7: beatoraja provider

成果:

- LR2 と同じ `PlayHistoryRow` 画面に beatoraja の単曲 play history を表示できる。

主な対象コード:

- `BeMusicSeeker/Models/BeatorajaConfigService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BeatorajaScoreDbLoader.cs`
- 新規 `BeatorajaPlayHistoryReader`
- `BeMusicSeeker/ViewModels/PlayHistoryRow.cs`

作業:

- [ ] player directory から `scoredatalog.db` / `scorelog.db` / `score.db` path を解決する。
- [ ] `scoredatalog.db` を主入力にして actual result を作る。
- [ ] `scorelog.db` を `sha256 + mode + date` で対応させ、best delta を補う。
- [ ] LAST PLAY SORT は `MAX(scoredatalog.date)` を主入力にする。`scoredatalog.db` が読めない場合は beatoraja provider の last play を未提供にする。
- [ ] clear / option は raw と projection を分ける。
- [ ] course / grade aggregate row は通常 Chart 履歴に混ぜず、診断または後続 scope にする。

テスト:

- [ ] `scoredatalog.db` の single play row が `PlayHistoryRow` になる。
- [ ] `scorelog.db` が対応できない場合は `BestDelta` 空欄。
- [ ] playtime は row では空欄、期間 summary は provider 固有 aggregate から扱う。
- [ ] LR2 provider と同じ columns / search / summary が動く。

完了条件:

- score source を beatoraja にしても、プレイログ画面が同じ UI で動く。

## 進捗管理表

| Phase | 状態 | 完了条件 |
| --- | --- | --- |
| Phase 0 | 進行中 | 実装計画と trigger proposal は配置済み。`devdocs/spec/play-history.md` 昇格は未実施 |
| Phase 1 | 完了 | LR2 score DB へ trigger / table を導入できる |
| Phase 2 | 完了 | LR2 履歴を `PlayHistoryRow` と summary に投影できる |
| Phase 3 | 進行中 | Play log tree と table UI は静的期間で動作。DnD / cell edit / activation / 通常 context menu guard は完了。年 / 月 / 日 node と PlayHistory 専用 context menu が残る |
| Phase 4 | 未着手 | LAST PLAY SORT custom folder が出力される |
| Phase 5 | 未着手 | keyword search と表示対象 filter が動く |
| Phase 6 | 進行中 | 最小 manual と summary 診断は追加済み。maintenance/spec/log contract 整理が残る |
| Phase 7 | 未着手 | beatoraja provider が同じ UI に載る |

## 作業記録

- 2026-06-19: 実装計画を BeMusicSeeker repository 前提に整理し、manual は `docs/manual.ja.md` と英語版 `docs/manual.md` を同時更新する方針を明記した。
- 2026-06-19: Phase 1 として LR2 play history schema service、設定画面の状態表示 / install / repair、schema tests を追加した。
- 2026-06-19: Phase 2 として LR2 play history reader、`PlayHistoryRow` projection、summary、sort、column settings foundation を追加した。
- 2026-06-19: Phase 3 初期実装として `プレイログ` tree、静的期間 selection、main table 表示、summary 診断、in-memory sort、対象 row だけを読む projection index、stale request guard を追加した。
- 2026-06-19: `docs/manual.ja.md` / `docs/manual.md` に `プレイログ` の最小説明を追加した。後続で設定画面、LAST PLAY SORT、troubleshooting、画像を拡充する。
- 2026-06-19: Phase 3 初期実装の検証として `PlayHistoryReadModelTests|MainWindowContextMenuResourceTests|LocalizationResourceParityTests|MainColumnSettingModeTests` と `dotnet build BeMusicSeeker-decomp.sln` を実行し、成功を確認した。
- 2026-06-19: Phase 3 操作 guard として PlayHistory view の row drag kind を `GenericSelectedRows` へ切り替え、playlist drop candidate / cell edit / row activation / 通常 chart context menu に流れないことを静的テストで固定した。

## 実装時の注意

- 初期 LR2 provider は LR2 linked profile 限定。stand-alone mode へ LR2 score DB 連携を広げない。
- LR2 score DB への trigger は SQLite 3.6.7 互換を必須にする。
- LR2 score DB への table / trigger 導入・修復は、ユーザーの明示操作でだけ行う。startup、設定保存、score hydration の副作用で自動実行しない。
- startup / 設定画面表示時の schema check は read-only にする。
- 既存 score row から last play を backfill しない。
- `playlist.last_update` と play history の日時を混ぜない。
- play history 読み取りで playlist reload / external sync / `.bmt` 再出力 / full scan を起動しない。
- `PlayHistoryRow` は Chart row ではない。Chart 操作 context menu を広く出さない。
- `PLAYTIME` と打鍵数は期間 summary の材料であり、曲単位 row の通常 column にしない。
- LR2 provider の `option` は best score 更新時以外は今回 play の option として扱わない。
- beatoraja provider 追加時に LR2 の値へ丸めて保存しない。raw と projection を分ける。
- 失敗を握りつぶさない。既存 score 読み込みに catch がある箇所も、play history では診断へ出す。

## マニュアル更新案

`docs/manual.ja.md` と英語版 `docs/manual.md` には、実装後に同じ内容をそれぞれの言語で追加する。片方だけ更新して完了扱いにしない。

- `はじめに` の追加機能に `LR2 / beatoraja プレイログ表示` を追加。
- `設定画面 > LR2と連携する` に、`プレイログ` 状態表示、`プレイログを有効化...` / `修復...` ボタン、警告 dialog の意味を追加。
- `設定画面 > LR2と連携する` に、score DB へ履歴 table / trigger を追加すること、ユーザー操作でのみ導入すること、バックアップ推奨、導入後から記録開始することを追加。
- `画面構成` に `プレイログ` tree を追加。
- 新章 `プレイログ` を追加し、期間 tree、summary、table columns、LAST PLAY SORT、診断表示を説明する。
- `バックアップ・アンインストール` に、履歴 table は LR2 score DB に入るため score DB backup が重要であることを追加。
- `ログとトラブルシューティング` に schema install 失敗、score DB locked、`finalized = 0` の意味を追加。

## 後続で spec 化する項目

実装後、BeMusicSeeker 側 `devdocs/spec/play-history.md` に移す項目:

- provider model
- LR2 trigger install timing
- `PlayHistoryRow` field semantics
- period summary semantics
- play history column defaults
- LAST PLAY SORT output command
- beatoraja provider read model
- diagnostics / logging contract
