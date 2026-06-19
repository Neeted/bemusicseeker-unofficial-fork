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

play history schema の導入・修復は、設定保存や startup の副作用として自動実行しない。BeMusicSeeker は startup / Play History read など score DB を読むタイミング、または設定画面の導入 / 修復 / 削除操作直前に LR2 score DB を read-only で確認し、導入済み / 未導入 / 不整合 / 読み取り不可の状態だけを表示する。設定画面を開くだけでは schema check を行わない。実際に LR2 score DB へ table / trigger を追加する操作は、設定画面の明示ボタンからだけ実行する。

play history schema 状態確認や LR2 score DB 変更は、既存の重い reload ではなく score DB schema check / score refresh / play history view refresh の範囲で扱う。設定保存時の impact は次の粒度に分ける。

| 変更 | 影響 |
| --- | --- |
| LR2 score DB path 変更 | schema check、score snapshot reload、play history view invalidation |
| play history schema 状態確認 | read-only schema check、diagnostics 更新 |
| play history schema 導入 / 修復ボタン実行 | schema install / repair、diagnostics 更新、score refresh、play history view invalidation |
| play history schema 無効化 / 削除ボタン実行 | trigger uninstall または play history table uninstall、diagnostics 更新、score refresh、play history view invalidation |
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
[有効化 / 修復...]
```

状態:

- `未導入`: 統合ボタンの表示を `[プレイログを有効化...]` にして有効化する。
- `導入済み`: 統合ボタンを disabled にする。状態確認は startup / Play History read / 操作直前の read-only check で行い、設定画面表示時の自動 check と手動の `[状態確認]` ボタンは置かない。
- `一部不足 / 不整合`: 統合ボタンの表示を `[修復...]` にして有効化する。
- `DBロック / 読み取り専用 / path不明`: 操作ボタンを disabled にし、diagnostics に理由を出す。
- `LR2 linked profile ではない`: 表示しない、または disabled にする。

導入・修復ボタン押下時は、LR2 を終了してから実行すること、プレイヤー別 score DB に table / trigger を追加すること、既存 score / player table は変更しないこと、score DB backup を推奨することを警告 dialog で確認する。

導入済み schema の削除は `バックアップ > データのアンインストール` に置く。ボタンは 1 つだけとし、押下後の確認 dialog で対象の player score DB path と削除範囲を表示する。削除範囲は、今後の記録だけを止めて既存履歴 table を保持する `trigger のみ削除` と、既存履歴も削除する `table も含めて削除` を選べるようにする。

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
- active LR2 linked profile の schema status に関わらず、LAST PLAY SORT の出力設定は通常の出力種別として選択可能にし、materialization は固定 SQL projection を生成する。schema 未導入時も `bms_lr2_last_play` を参照する `.lr2folder` / `folder` row は生成し、LR2 / OpenLR2 側で利用するには schema 導入が必要であることを manual / spec に記載する。
- schema が導入済みで `bms_lr2_last_play` に row がない譜面は、SQL 上の `NULL` により最新順の末尾へ回る。これは fallback ではなく、履歴機能導入後に未観測という意味である。

```text
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
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
- [x] 実装済み範囲を `devdocs/spec/play-history.md` へ昇格し、未実装または後続 scope を現行仕様から分離する。
- [x] `docs/manual.ja.md` / `docs/manual.md` の追加章案を作る。

完了条件:

- 実装者が provider model / UI / LR2 DB trigger / schema repair / LAST PLAY SORT の境界を迷わず読める。

### Phase 1: LR2 trigger install / schema verification

成果:

- BeMusicSeeker の設定画面から、ユーザーの明示操作で LR2 score DB に play history table / trigger を導入できる。
- SQLite 3.6.7 互換 SQL だけを使う。
- 導入済み / 未導入 / 壊れた schema を診断できる。
- LR2 linked profile 以外では install を実行しない。
- startup / Play History read / 設定画面の明示操作直前は read-only の schema check だけを行い、自動導入しない。設定画面表示だけでは schema check を行わない。

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
- [x] 設定画面の `LR2と連携する` 配下に play history schema 状態表示と状態別文言を持つ統合 `[プレイログを有効化...]` / `[修復...]` ボタンを追加する。
- [x] 導入・修復ボタン押下時に warning dialog を表示し、OK の場合だけ score DB へ書き込む。
- [x] warning dialog には、LR2 終了、score DB への table / trigger 追加、既存 score / player table 非変更、backup 推奨を明記する。
- [x] trigger install を設定保存時または startup score hydration 前に自動実行しない。
- [x] trigger install 成功後も `bms_lr2_last_play` / `bms_lr2_play_history` は空開始であることを UI / log 上で自然に扱う。
- [x] LR2 play history schema check / install は `ActiveScoreSource` ではなく LR2 linked profile の score DB path を見る。
- [x] `application.log` または `install-performance.log` に `play_history_schema_*` を出す。
- [x] LR2 linked profile でない場合は `skipped_profile` として診断する。
- [x] schema install は file scan / playlist reload / `.bmt` 出力を要求しない。
- [x] `バックアップ > データのアンインストール` に LR2 play history schema の無効化 / 削除ボタンを追加し、確認 dialog で trigger のみ削除 / table も含めて削除を選べるようにする。

テスト:

- [x] temp SQLite score DB に schema / trigger を作れる。
- [x] 2 回実行して冪等。
- [x] `bms_lr2_play_pending`、2 index、4 trigger の作成漏れを検出できる。
- [x] trigger SQL 不一致は repairable になり、repair で trigger だけ再作成される。
- [x] required table の column 不足は `ManualRepairRequired` になり、table が drop されない。
- [x] startup / Play History read / 設定画面の明示操作直前の schema check が read-only で完了し、table / trigger を作らない。設定画面表示だけでは schema check を行わない。
- [x] warning dialog で cancel した場合、DB が変更されない。
- [x] trigger のみ削除では履歴 table / index を保持し `Repairable` になること、table も含めた削除では app-owned play history object が消えて `NotInstalled` になることをテストする。
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
- [x] 日別 / 月別 / 年別 summary 用 aggregate を作る。
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
- [x] `GridRowResolver` と PlayHistory 専用 context menu policy に PlayHistory row 用の hash-only operation target を追加し、未解決 row は raw hash copy のみにする。
- [x] `GridSummaryText` または専用 summary binding で期間 digest を出す。
- [x] Phase 3 では表示対象 dropdown を作らず、全履歴を対象にする。`FOLDER` は解決できた playlist / folder label の表示だけに使い、絞り込みは Phase 5 に送る。
- [x] play history view rebuild は score DB read と projection に限定し、library catalog 再構築を要求しない。
- [x] `bms_lr2_play_history.played_at` から年 / 月 / 日 archive node を動的生成し、一覧 reader の default limit に巻き込まれない軽量 period index read を使う。

初期可視 columns:

| Order | Column | Header | 備考 |
| ---: | --- | --- | --- |
| 1 | PlayedAt | `DATE` | `yyyy/MM/dd HH:mm:ss` |
| 2 | FolderLabels | `FOLDER` | Phase 3 は解決できた playlist / folder label を列挙。対象 playlist / 表示対象セット由来の絞り込みは Phase 5 |
| 3 | Title | `TITLE` | Chart 解決できない場合は raw title なし |
| 4 | BestClear | `CLEAR` | BestDelta。更新時だけ old -> new、初回は NO PLAY -> value |
| 5 | BestDjLevel | `BEST DJ` | BestDelta。best EX score が変化した row だけ表示 |
| 6 | BestRate | `BEST RATE` | BestDelta。best EX score が変化した row だけ表示 |
| 7 | BestBp | `BP` | BestDelta。初回 BP は `BP 10` ではなく `10` のように値だけ表示する |
| 8 | BestCombo | `COMBO` | BestDelta。更新時 old -> new |
| 9 | Kind | `TYPE` | score / bp / clear / combo は複合表示可。`play` は他の種別が無い場合だけ表示 |
| 10 | OpHistory | `OP HISTORY` | 新規 bit を option 名で表示。ASSIST など消えた bit も `off` として遷移表示 |

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
- [x] 年 / 月 / 日 node の epoch range が期待通りになる。
- [x] `未確定 / 診断` node では LR2 `finalized = 0` の未確定 row と projection / read diagnostics が見える。確定済み row は一覧対象にしない。beatoraja provider では通常履歴を未確定 row として代替表示しない。
- [x] play history row は playlist tree drop candidate にならない。
- [x] play history row 右 click では通常 chart 操作 context menu を開かない。
- [x] play history 専用 context menu で、解決済み row の repository / hash 系と、未解決 row の raw hash copy だけを出す。
- [x] play history row activation は chart 再生 / explorer / playlist edit を起動しない。

完了条件:

- LR2 履歴を一覧・sort・期間選択・summary 表示できる。
- 一覧ラベル / 検索欄の下に PlayHistory 専用 summary row を出す。判定数、プレイ数、演奏時間、score / BP / combo / clear 更新、ASSIST / EASY / NORMAL / HARD / FC の clear 内訳をカード風領域として並べる。EXH は LR2 provider では出さず、beatoraja provider のときだけ出す。
- PlayHistory 専用 context menu は、MD5 解決済み row で BMS-IR、repository SHA-256 解決済み row で Mocha / MinIR、未解決 row で raw hash copy を出す。

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

- [x] `LR2SongDBExtended.playlist.CustomFolderType` に `LastPlaySortFolder` を追加する。
- [x] `AllFolders` / `ignore_folder_output` の bit を割り当てる。保存済み `ignore_folder_output` は raw mask として扱い、旧全 OFF 相当値を新 `AllFolders` へ拡張しない。
- [x] `CustomFolderSortType` / `CustomFolderSortTypeExt` には追加しない。
- [x] `BMSPlaylist` の custom folder definition builder と `Lr2ManagedCustomFolderOutputLayout` の relative path / count 計算へ `LastPlaySortFolder` を追加する。
- [x] playlist property / bulk edit / output bit へ追加する。
- [x] LR2 play history schema status に関わらず、LastPlaySortFolder の UI は通常の出力種別として操作できるようにする。
- [x] 保存済み LastPlaySortFolder bit が ON の場合、LR2 play history schema status に関わらず固定 SQL projection を生成する。schema 未導入時も `bms_lr2_last_play` への fallback や出力抑止は行わない。
- [x] `.lr2folder` projection に `ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC` を追加する。
- [x] `folder` table sync projection にも同じ command を入れる。
- [x] `bms_lr2_last_play` が無い場合は score / playcount / playlist 更新日時へ意味を変えて fallback しない。schema 未導入時も固定 command を生成し、LR2 / OpenLR2 側で利用するには schema 導入を必要とする。
- [x] `playlist_custom_folder_output_status` の fingerprint に last play sort output bit を含める。
- [x] `docs/manual.ja.md` / `docs/manual.md` / spec に `LAST PLAY SORT` を追記する。
- [x] LR2 で play 後に `.lr2folder` 再出力不要で並びが変わることを仕様として明記する。

テスト:

- [x] `.lr2folder` に LAST PLAY SORT command が出る。
- [x] output bit OFF の場合は出ない。
- [x] schema 未導入で output bit ON の場合も materialization は成功し、LAST PLAY SORT command を生成する。
- [x] LAST PLAY SORT 固有の stale file / stale row cleanup が既存 sort folder と同じように動く。
- [x] folder table sync に LAST PLAY SORT command が入る。
- [x] schema 導入済みで未観測譜面に row が無い場合は、`NULL` sort として末尾へ回る command になる。

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

- [x] `GridKeywordSearchContext.PlayHistory` を追加する。
- [x] PlayHistoryRow 用 matcher を実装する。
- [x] field completion に play history fields を追加する。
- [x] `プレイログ` 右上 dropdown の対象を `すべて` / 表示プリセット / `FOLDER: 表示プリセット` / playlist にする。
- [x] 表示対象セットは `Settings.Default.PlayHistoryDisplayTargetSetsJson` のような settings へ保存し、portable settings 対象に含める。
- [x] 表示対象セットは playlist identity / folder label の参照集合として扱い、playlist 正本や `playlist.last_update` は変更しない。
- [x] 設定ダイアログ Playlist tab に `プレイログ FOLDER 表示プリセット` 編集 UI を追加する。プリセット名と対象 playlist の複数選択を編集でき、JSON は user.config に保存する。
- [x] FOLDER column は `すべて` では投影時の playlist symbol、対象 playlist 選択時は playlist folder、表示プリセットでは一致する難易度表 entry の `org_symbol + level` を列挙する。`FOLDER: 表示プリセット` では row filter は行わず、preset に一致しない row の FOLDER は空欄にする。`すべて` は表示補正のために playlist entries を同期ロードまたは全件走査しない。

テスト:

- [x] `date:` / `year:` / `month:` / `kind:` / `clear:` / `playlist:` / `folder:` / `finalized:` が期待通り絞り込む。
- [x] field completion と unknown field diagnostics が play history fields と一致する。
- [x] 表示対象セットの変更で FOLDER 表示と row filter が変わる。`FOLDER: 表示プリセット` では FOLDER 表示だけが変わり、期間 tree / keyword search の対象行は維持する。
- [x] 既存 chart list / playlist detail の keyword search が壊れない。

完了条件:

- 期間だけでなく、更新種別・表・folder・譜面名で履歴を探せる。

### Phase 6: Diagnostics / maintenance / manual

成果:

- Phase 1-5 で出している trigger install / read / projection の診断を、設定画面・メンテナンス・manual から確認できる。
- ユーザーマニュアルに最小限の説明を追加する。

作業:

- [x] 設定画面またはメンテナンスに play history status を表示する。
  - 実装済み: `設定 > LR2と連携する` 配下の `Lr2PlayHistorySchemaStatusText` / detail tooltip / 導入・修復の統合ボタン、`バックアップ > データのアンインストール` 配下の無効化・削除ボタン。
- [x] `finalized = 0` や異常値を診断表示に出す。
  - 実装済み: `未確定 / 診断` node と summary diagnostics。projection/read diagnostics は summary と log に出す。
- [x] `LR2 score DB に履歴 trigger を導入する` 操作の注意文を追加する。
- [x] backup 対象に score DB を含める注意を更新する。
- [x] `docs/manual.ja.md` に `プレイログ` 章、`docs/manual.md` に対応する英語章を追加する。
- [x] `devdocs/spec/play-history.md` を作り、実装済み仕様を正本化する。
- [x] log event を `play_history_schema_*`, `play_history_read_*`, `play_history_projection_*`, `play_history_view_*` のように段階別に出す。
  - 実装済み: schema 操作は `application.log` の `play_history_schema_*`、view build は `install-performance.log` の read (`play_history_read_done`, `play_history_read_period_index_*`)、projection (`play_history_projection_*`)、view (`play_history_view_*`) で段階別に追跡できる。

テスト:

- [x] schema missing / trigger missing / unreadable / manual repair required の表示確認。
  - 自動テスト済み: `Lr2PlayHistorySchemaUiTests`, `Lr2PlayHistorySchemaServiceTests`, `PlayHistoryReadModelTests`。
- [ ] locked / read-only の実環境表示確認。
  - SQLite / Windows file lock 状態に依存するため、自動テストではなく実画面または手動検証として残す。
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

- [x] player directory から `scoredatalog.db` / `scorelog.db` / `score.db` path を解決する。
- [x] `scoredatalog.db` を主入力にして actual result を作る。
- [x] `scorelog.db` を `sha256 + mode + date` で対応させ、best delta を補う。
- [x] archive / last play index は `MAX(scoredatalog.date)` を主入力にする。`scoredatalog.db` が読めない場合は beatoraja provider の last play を未提供にする。LR2 LAST PLAY SORT custom folder への beatoraja 統合は今回の scope 外。
- [x] clear / option は raw と projection を分ける。
- [x] course / grade aggregate row は通常 Chart 履歴に混ぜず、診断または後続 scope にする。

テスト:

- [x] `scoredatalog.db` の single play row が `PlayHistoryRow` になる。
- [x] `scorelog.db` が対応できない場合は `BestDelta` 空欄。
- [x] playtime は row では空欄。期間 summary の provider 固有 aggregate 加算は後続 scope とする。
- [x] LR2 provider と同じ columns / search / summary 経路へ載る。
- [x] beatoraja provider は score 表示 source が実際に beatoraja の場合だけ選択する。設定値や DB path だけで provider を切り替えない。
- [x] `scoredatalog.option` は `1P + 2P * 10 + DP * 100` として RANDOM / MIRROR / FLIP / BATTLE AS などへ decode する。`random` / `seed` は通常表示には混ぜない。
- [x] `scorelog.oldminbp = int.MaxValue` は未プレイ sentinel として null に正規化し、`2147483647 -> value` と表示しない。

完了条件:

- score source を beatoraja にしても、プレイログ画面が同じ UI で動く。

## 進捗管理表

| Phase | 状態 | 完了条件 |
| --- | --- | --- |
| Phase 0 | 完了 | 実装計画 / trigger proposal / `devdocs/spec/play-history.md` が配置済み |
| Phase 1 | 完了 | LR2 score DB へ trigger / table を導入できる |
| Phase 2 | 完了 | LR2 履歴を `PlayHistoryRow` と summary に投影できる |
| Phase 3 | 完了 | Play log tree と table UI は固定期間 + 年 / 月 / 日 archive node で動作。専用 summary row、DnD / cell edit / activation / 通常 context menu guard、PlayHistory 専用 BMS-IR / repository / hash context menu まで完了 |
| Phase 4 | 完了 | LAST PLAY SORT custom folder が schema status に関わらず出力され、cleanup / NULL sort command / schema 未導入時の固定 SQL 出力の検証まで完了 |
| Phase 5 | 完了 | keyword search と `すべて` / 表示プリセット / playlist dropdown filter が動作し、settings JSON 保存形式と設定 UI まで追加済み |
| Phase 6 | 進行中 | status 表示、summary 診断、現行仕様 spec、log contract は追加済み。locked / read-only 実環境確認と実画面 screenshot が残る |
| Phase 7 | 完了 | beatoraja provider が同じ UI に載り、挙動 / 性能 / テスト充足レビューで P0 / P1 指摘なしを確認済み |

## 作業記録

- 2026-06-19: 実装計画を BeMusicSeeker repository 前提に整理し、manual は `docs/manual.ja.md` と英語版 `docs/manual.md` を同時更新する方針を明記した。
- 2026-06-19: Phase 1 として LR2 play history schema service、設定画面の状態表示 / install / repair、schema tests を追加した。
- 2026-06-19: Phase 2 として LR2 play history reader、`PlayHistoryRow` projection、summary、sort、column settings foundation を追加した。
- 2026-06-19: Phase 3 初期実装として `プレイログ` tree、静的期間 selection、main table 表示、summary 診断、in-memory sort、対象 row だけを読む projection index、stale request guard を追加した。
- 2026-06-19: `docs/manual.ja.md` / `docs/manual.md` に `プレイログ` の最小説明を追加した。後続で設定画面、LAST PLAY SORT、troubleshooting、画像を拡充する。
- 2026-06-19: Phase 3 初期実装の検証として `PlayHistoryReadModelTests|MainWindowContextMenuResourceTests|LocalizationResourceParityTests|MainColumnSettingModeTests` と `dotnet build BeMusicSeeker-decomp.sln` を実行し、成功を確認した。
- 2026-06-19: Phase 3 操作 guard として PlayHistory view の row drag kind を `GenericSelectedRows` へ切り替え、playlist drop candidate / cell edit / row activation / 通常 chart context menu に流れないことを静的テストで固定した。
- 2026-06-19: Phase 3 archive node として `bms_lr2_play_history.played_at` の軽量 period index reader、年 / 月 / 日 `PlayHistoryPeriodRequest`、`PlayHistoryArchivePeriodTree` binding を追加し、`docs/manual.ja.md` / `docs/manual.md` に `日付別` / `By Date` を追記した。
- 2026-06-19: Phase 3 PlayHistory 専用 context menu として、解決済み row は Mocha / MinIR と MD5 / repository SHA256 copy、未解決 row は raw hash copy だけを出す hash-only policy を追加した。
- 2026-06-19: Phase 4 初期実装として `LastPlaySortFolder` bit、playlist property / bulk edit の出力種別、`.lr2folder` / LR2 `folder` row の LAST PLAY SORT projection、manual/spec 追記を追加した。当初検討した schema 未導入時の materialization guard と UI disabled は採用せず、schema status に関わらず固定 SQL projection を出力する方針に整理した。
- 2026-06-19: Phase 4 追加検証として、LastPlaySortFolder を OFF にした再出力で `LAST PLAY SORT` の生成済み file / stale file / stale LR2 `folder` row が prune されることをテストで固定した。
- 2026-06-19: Phase 4 UI 方針を見直し、play history schema status が `Installed` ではない場合でも playlist property / bulk edit の LastPlaySortFolder checkbox は enabled のままにし、bulk patch から LastPlaySortFolder 変更を除外しない tests に更新した。
- 2026-06-19: Phase 4 NULL sort 検証として、LAST PLAY SORT command を `last_play_at IS NULL ASC, last_play_at DESC` に明示化し、未観測譜面が末尾へ回る ORDER BY を `.lr2folder` / LR2 `folder` row の tests で固定した。
- 2026-06-19: Phase 0 / Phase 6 spec 整理として `devdocs/spec/play-history.md` を追加し、LR2 schema / read model / projection / period UI / diagnostics / LAST PLAY SORT の現行仕様と、当時未実装だった Phase 5 / Phase 7 の scope を分離した。
- 2026-06-19: Phase 6 manual 整理として、`docs/manual.ja.md` / `docs/manual.md` に LR2 play-log schema 有効化 / 修復が player `score.db` へ table / index / trigger を追加・修復することと、backup 対象に score DB を含める注意を追記した。
- 2026-06-19: Phase 6 log contract として、PlayHistory view build に `play_history_read_done` / `play_history_read_period_index_*` / `play_history_projection_*` / `play_history_view_*` の段階別 event を追加し、専用 event 名・主要 field・projection fallback の理由を静的テストで固定した。
- 2026-06-19: Phase 6 進捗整理として、設定画面の play history schema status 表示、`未確定 / 診断` node、schema missing / trigger missing / unreadable / manual repair required の自動テストが実装済みであることを計画表へ反映した。locked / read-only の実環境確認と実画面 screenshot は手動検証として残す。
- 2026-06-19: Phase 5 keyword search として `GridKeywordSearchContext.PlayHistory`、`PlayHistoryRow` matcher、PlayHistory 用 field completion/help、PlayHistory view の projection 後 keyword filter を追加した。この時点では表示対象セット / dropdown filter は後続作業として残っていた。
- 2026-06-19: Phase 5 display target として、PlayHistory 右上に `すべて` / settings JSON の表示プリセット / playlist dropdown を追加し、target filter を projection 後・keyword filter 前に適用する read model を追加した。playlist 選択時の FOLDER は playlist entry folder、表示プリセット選択時は `org_symbol + level` 表示とし、対象外 row は除外する。`Settings.Default.PlayHistoryDisplayTargetSetsJson` は portable settings と同じ provider 管理対象とし、設定ダイアログ Playlist tab で追加 / 編集 / 削除する。
- 2026-06-19: Phase 5 display target のサブエージェントレビューを挙動 / 性能 / テスト充足の観点で実施し、PlaylistTree flush 時の target 再構築、playlist entries hydration 完了時の不要 refresh 抑止、MD5 優先 lookup、空 folder 表示、stale target filter cancellation、settings / XAML / refresh 経路の静的テストを追加した。再レビューで P0 / P1 指摘なしを確認した。
- 2026-06-19: Phase 7 初期実装として、beatoraja player directory の `scoredatalog.db` / `scorelog.db` path 解決、`BeatorajaPlayHistoryReader`、`BeatorajaPlayHistoryRecord`、`PlayHistoryRow.ProjectBeatorajaRows`、MainWindow の provider 切り替えを追加した。`scoredatalog.mode = 0` の単曲 row を actual result とし、`scorelog` は `sha256 + mode + date` が一致した場合のみ best delta を補う。`scorelog.db` 不在時は best delta を空欄にし、playtime は row に混ぜない。
- 2026-06-19: Phase 7 サブエージェントレビューを挙動 / 性能 / テスト充足の観点で実施し、resolved MD5 を使う playlist / display target 解決、beatoraja projection diagnostic provider、scorelog cancellation、scorelog read 範囲、provider 判定の snapshot build 回避、summary / search / display target / scorelog mismatch tests、manual / spec の単一 provider 記述を追加・修正した。再レビューで P0 / P1 指摘なしを確認した。
- 2026-06-19: UI follow-up として、PlayHistory 専用 summary row を一覧ラベル / 検索欄の下へ追加し、判定数 / プレイ数 / 演奏時間 / score / BP / combo / clear 更新 / clear 内訳をカード風に表示するようにした。通常の `GridSummaryText` は PlayHistory では診断・補助情報へ寄せる。
- 2026-06-19: 表示 follow-up として、CLEAR / BEST DJ に通常一覧の色解決を適用し、BEST DJ / BEST RATE / BEST EXSCORE / BP / COMBO / CLEAR を遷移表示に統一した。初回 BP は値だけ、TYPE は複合表示、OP HISTORY は LR2 option history bit 名表示と ASSIST off などの遷移表示に変更した。
- 2026-06-19: beatoraja follow-up として、provider 選択を `ActiveScoreSource.Beatoraja` に連動させ、`scoredatalog.option` を beatoraja 実装に基づき decode し、`scorelog.oldminbp = int.MaxValue` を未プレイ扱いにした。
- 2026-06-19: UX follow-up として、PlayHistory の PROVIDER / SOURCE をユーザー表示列と列メニューから外し、右クリックに BMS-IR を追加し、日付別 tree は同じ archive tree を毎回差し替えないようにして選択が root へ戻る問題を修正した。
- 2026-06-19: 上記 follow-up の検証として、`PlayHistoryReadModelTests|MainWindowContextMenuResourceTests|CustomTableColumnFactoryTests` を実行し、成功を確認した。実装後レビューを挙動 / 性能 / テスト充足のサブエージェントへ依頼し、diagnostics 表示消失、beatoraja BP sentinel / summary row / BEST DJ・RATE column のテスト不足、summary card binding のテスト不足を修正した。再レビューで重大指摘なしを確認する。
- 2026-06-19: 追加 follow-up として、設定ダイアログを開いてキャンセルするだけの経路で schema check と設定復元の重い処理を繰り返さないようにし、CLEAR / BEST DJ の遷移をセル内で更新元・矢印・更新先別に色分けした。CLEAR は PlayHistory 用の短縮形に変更し、LR2 summary では EXH 内訳を非表示にした。
- 2026-06-19: 追加 follow-up として、日付別 tree のパンくず表示、PlayHistory tree の配置、playlist tree の編集時テーマ色、`未確定 / 診断` node の LR2 `finalized = 0` 絞り込みを修正した。`すべて` 選択時の FOLDER は entry-level 補正を行わず、複数 playlist の `symbol + level` 表示が必要な場合は表示プリセットを選ぶ設計にした。
- 2026-06-19: 追加 follow-up として、PlayHistory summary row が playlist summary 画面に残る問題、summary card label のリソース化、PlayHistory tree static node のテーマ追従、CustomTableView の選択行 text-run 色維持、設定ダイアログの変更なし OK / Cancel 軽量化、PlayHistory FOLDER 表示プリセット設定 UI を追加した。
- 2026-06-19: 追加レビュー指摘への対応として、表示プリセット参照は `playlist.id` が保存されている場合に name / symbol へフォールバックしない挙動を固定し、設定ダイアログの表示プリセット draft / validation / dropdown 順序、変更なし OK の早期 close、`EnsureSchema` が既存 custom folder 出力設定を書き換えない DB 回帰テストを追加した。
- 2026-06-19: 再レビュー指摘への対応として、表示プリセットの保存用 JSON と変更検知用 draft JSON を分離し、空名 / 重複名 / playlist 未選択の invalid draft を「変更なし」と誤判定しないようにした。設定ダイアログ表示用 playlist 候補は settings backup / cancel では全件再構築せず dirty 化し、選択判定は `playlist.id` / name / symbol の集合 index で行う。保存 helper / cancel 復元 / summary row clear / CustomTable current cell text-run 色保持の tests も追加した。
- 2026-06-19: UI follow-up として、設定ダイアログ Playlist tab の `プレイログ FOLDER 表示プリセット` はプリセット名一覧だけを表示し、追加 / 編集ボタンから別ウィンドウでプリセット名と対象 playlist を選択する設計へ変更した。対象 playlist 候補の全件構築は別ウィンドウを開く時だけ行い、Cancel では draft を変更しない。
- 2026-06-19: テストレビュー指摘への対応として、別ウィンドウ化した FOLDER 表示プリセット編集の OK / Cancel 配線、未適用 edit session が draft を汚さないこと、検証失敗時に既存 preset が変わらないこと、設定ダイアログ OK 保存経路が preset persist を呼ぶことをテストで固定した。
- 2026-06-19: 再レビュー指摘への対応として、beatoraja provider の `未確定 / 診断` では通常履歴を未確定 row として代替表示しないよう `FinalizationFilter.UnfinalizedOnly` を空 rows として扱う実装にした。CustomTable text run の実描画色、SettingDialog cancel / schema check 判定、beatoraja diagnostics を追加した。さらに `UnfinalizedOnly` でも壊れた `scoredatalog.db` は `Unreadable` diagnostic として検出する。
- 2026-06-19: 表示対象 follow-up として、PlayHistory 右上 dropdown の表示プリセットに `FOLDER: preset` モードを追加した。このモードは期間 tree で得た行を落とさず、FOLDER だけを preset の `org_symbol + level` で投影し、preset 外 row の FOLDER は空欄にする。keyword search は従来通り display target 適用後に実行する。
- 2026-06-19: 設定画面 follow-up として、LR2 play history schema の手動 `[状態確認]` ボタンを撤去し、導入 / 修復を状態別文言の統合ボタンに整理した。`バックアップ > データのアンインストール` へ LR2 play history schema の無効化 / 削除ボタンを追加し、確認 dialog で trigger のみ削除（既存履歴保持）と table も含めた削除（履歴削除）を選べるようにした。

## 実装時の注意

- 初期 LR2 provider は LR2 linked profile 限定。stand-alone mode へ LR2 score DB 連携を広げない。
- LR2 score DB への trigger は SQLite 3.6.7 互換を必須にする。
- LR2 score DB への table / trigger 導入・修復は、ユーザーの明示操作でだけ行う。startup、設定保存、score hydration の副作用で自動実行しない。
- startup / Play History read / 設定画面の明示操作直前の schema check は read-only にする。設定画面表示だけでは schema check を行わない。
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
- `設定画面 > LR2と連携する` に、`プレイログ` 状態表示、状態別文言を持つ導入 / 修復の統合ボタン、警告 dialog の意味を追加。
- `設定画面 > LR2と連携する` に、score DB へ履歴 table / trigger を追加すること、ユーザー操作でのみ導入すること、バックアップ推奨、導入後から記録開始することを追加。
- `画面構成` に `プレイログ` tree を追加。
- 新章 `プレイログ` を追加し、期間 tree、summary、table columns、LAST PLAY SORT、診断表示を説明する。
- `バックアップ・アンインストール` に、履歴 table は LR2 score DB に入るため score DB backup が重要であることと、LR2 play history schema の trigger のみ削除 / table も含めた削除の違いを追加。
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
