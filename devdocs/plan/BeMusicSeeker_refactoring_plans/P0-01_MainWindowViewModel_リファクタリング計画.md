> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 0. 目的

`BeMusicSeeker/ViewModels/MainWindowViewModel.cs` は開始時点で 33,379 行、2026-07-06 時点で 24,250 行あり、UI ルート ViewModel、チャート一覧生成、プレイリスト一覧/詳細、起動進捗、プレイ履歴、play history display target、再生制御、外部同期、beatoraja Table URL import、スコアビューア連携などがまだ 1 ファイルに多く残っている。

この計画では、添付画像の UI 構成に合わせて責務を切り分ける。

- 上部の再生パネル: `PlaybackPanelViewModel`
- 左サイドバー上段/下段のツリー: `SidebarNavigationViewModel` と用途別ツリー ViewModel
- メインの一覧画面: `MainChartListViewModel` / `PlaylistWorkspaceViewModel`
- 進捗・ステータス表示: `OperationProgressHubViewModel`
- 設定画面: `SettingDialogViewModel` の独立性強化
- ルート: `MainWindowViewModel` は構成・初期化・終了・横断調停だけを担当する Shell ViewModel に寄せる

方針は「一気に作り替えない」こと。まず partial 分割で型・バインディングを維持し、その後に子 ViewModel とサービスへ段階的に抜き出す。

## 進捗

| 日付 | 状態 | 内容 | コミット |
|---|---|---|---|
| 2026-07-05 | 完了 | SDK 10 開発環境整備 | `b112dd27` |
| 2026-07-05 | 完了 | Ticket A: partial 対応前のテスト helper | `5b43658c` |
| 2026-07-05 | 完了 | Ticket B: 先頭 helper 型の移動 | `ce7e1a66` |
| 2026-07-05 | 完了 | Ticket C: SettingDialogViewModel の nested partial 移動 | `d19295a6` |
| 2026-07-05 | 完了 | Ticket D: Playlist dialog VM の nested partial 移動 | `86d46462` |
| 2026-07-05 | 完了 | Ticket E: OperationProgressHubViewModel 導入 | `b9059645` |
| 2026-07-06 | 完了 | Ticket F: PlaybackPanelViewModel 導入（手動 smoke 確認済み） | `80b53124` |
| 2026-07-06 | 完了 | Ticket G: MainViewRefreshDecisionService 抽出 | `3cfdf1c8` |
| 2026-07-06 | 完了 | Ticket H: PlaylistRequestFactory 抽出 | `fe54e608` |
| 2026-07-06 | 完了 | Ticket I-1: MainChartListViewModel 導入前の境界確認 | `50fe8e9b` |
| 2026-07-06 | 完了 | Ticket I-2: MainChartListViewModel の state pass-through 化 | `b908f314` |
| 2026-07-06 | 完了 | Ticket I-3a: chart row swap / summary state の child 化 | `6d2518d5` |
| 2026-07-06 | 完了 | Ticket I-3b: refresh request routing の coordinator 入り口作成 | `1a0bb56a` |
| 2026-07-06 | 完了 | Ticket I-3c: virtual normal / virtual subset apply の coordinator 化 | `a60958c9` |
| 2026-07-06 | 完了 | Ticket I-3d-1: regular pipeline request / stage state 型の追加 | `e4b301b9` |
| 2026-07-06 | 完了 | Ticket I-3d-2: keyword / mode filter stage の service 化 | `b7be45e1` |
| 2026-07-06 | 完了 | Ticket I-3d-3: regular sort / cache stage の coordinator 化 | `c32cb023` |
| 2026-07-06 | 完了 | Ticket I-3d-4: regular materialized apply / log terminal の統合 | `ffcc7fd8` |
| 2026-07-06 | 完了 | Ticket I-3d-5: RefreshChartRowsView facade 化完了 | `b5427fa0` |
| 2026-07-06 | 完了 | Ticket I-4a: main chart list test dependency 棚卸し | `89db5cb6` |
| 2026-07-06 | 完了 | Ticket I-4b: source-text helper の main chart list 範囲を明確化 | `cbd6d659` |
| 2026-07-06 | 完了 | Ticket I-4c: root forwarder / reflection test の直接化 | `689518ba` |
| 2026-07-06 | 完了 | Ticket J-1: playback header DataContext 移行 | `1581fc9e` |
| 2026-07-06 | 完了 | Ticket J-2: progress status bar DataContext 移行 | `798fe933` |

次候補: Ticket J-3: DataContext 移行後の binding 棚卸し。

## 現在地サマリ

2026-07-06 時点では、P0-01 は「巨大ファイルの安全な切り出し」から「Shell 化に向けた責務移譲」へ移行中である。`MainWindowViewModel.cs` は開始時点より約 9,000 行減ったが、まだ 24,250 行あり、最終完了条件の 1,000〜1,500 行には遠い。

| 領域 | 現在の状態 | 次に必要なこと |
|---|---|---|
| Playback | `PlaybackPanelViewModel` 導入済み。root pass-through と XAML root Binding は残る。 | XAML DataContext 移行、root facade 削除、UI handle 非依存 API へ寄せる。 |
| Progress | `OperationProgressHubViewModel` 導入済み。root pass-through と一部副作用 relay は残る。 | XAML DataContext 移行、startup reducer / status presentation の service 化。 |
| Main chart list | `MainChartListViewModel`、`ChartListRefreshCoordinator`、`MainViewRefreshDecisionService` 導入済み。仮想一覧終端処理は coordinator 化済み。 | 通常一覧 pipeline、sort/filter state、column settings、keyword presentation を child/coordinator へ移す。 |
| Playlist detail / summary | `PlaylistRequestFactory` と dialog partial 移動は完了。detail build、summary build、bulk mutation は root に大きく残る。 | `PlaylistWorkspaceViewModel` と detail/summary service を導入し、root を facade にする。 |
| Play history | 読み取り、表示行構築、summary card、sort/filter が root に残る。 | `PlayHistoryWorkspaceViewModel` と presentation/read coordinator へ分ける。 |
| Settings | nested partial は別ファイル化済みだが 6,697 行あり、service 化は未着手。 | validation、snapshot/store、post-save coordinator を切り出す。 |
| Sidebar / tree | XAML と code-behind が root の tree state / handlers に強く依存。 | `SidebarNavigationViewModel` 導入、tree DataContext と command bridge を段階移行。 |
| Shell | 子 VM は増えたが root が workflow と facade と状態を兼務。 | composition root / lifecycle / dialog request / shared runtime context 以外を削る。 |

### 行数ロードマップ

行数は目的ではなく、責務境界が適切に切れているかの観測値として扱う。各ゲートで build/test/format/analyzer が通ることを必須にする。

| ゲート | 目標 | 期待される状態 |
|---|---:|---|
| Gate 1: Main chart / playlist / play history workspace 導入後 | `MainWindowViewModel.cs` 15,000 行以下 | 一覧、playlist、play history の主要 workflow が child/coordinator/service へ移る。 |
| Gate 2: settings service 化 / sidebar 導入後 | `MainWindowViewModel.cs` 8,000 行以下 | 設定保存・検証と tree/navigation の詳細が root から外れる。 |
| Gate 3: XAML DataContext 移行後 | `MainWindowViewModel.cs` 3,000 行以下 | root pass-through の大半を削除できる。XAML は子 VM を直接参照する。 |
| Gate 4: Shell 化完了 | `MainWindowViewModel.cs` 1,000〜1,500 行以下 | root は app lifecycle、子 VM composition、shared runtime context、dialog request だけを持つ。 |

### 目標アーキテクチャ達成見込み

目標アーキテクチャは達成可能。ただし、`MainWindowViewModel` 単体の行数だけを削ると、巨大な child VM へ移すだけになりやすい。以後は次の順序を守る。

1. まず root の既存 Binding / private caller を壊さず child VM or service へ責務を移す。
2. 次に source-text / reflection test を service 直接検証へ移し、root forwarder 依存を減らす。
3. XAML DataContext を child VM へ移す。
4. 最後に root pass-through と互換 forwarder を削除する。

`MainWindowRuntimeContext` を中盤で導入し、`BMSLibrary` / `BMSPlaylist` / `LR2Config` / `IBMSPlayer` / dispatcher / dialog request など横断依存を root から child VM へ直接ばらまかないようにする。

### Ticket F 手動確認結果

- `PlaybackPanelViewModel` 導入、自動確認、上部再生パネルの手動 smoke test（再生/停止/次/前/音量/ヘッダー表示）は完了。

### Ticket G 抽出範囲

- `MainViewRefreshDecisionService` が main-view data dependency 変更時の `MainViewRefreshDecision`、sort column dependency、通常ライブラリ sort-key invalidation reason、virtual normal-library mode support、regular folder stage rebuild 判定を所有する。
- `MainWindowViewModel` の既存 `ForTest` method は互換 forwarder として残す。
- `LibraryChartRowSortEngineTests` の main-view refresh decision / dependency 判定は service 直接検証へ寄せる。

### Ticket H 抽出範囲

- `PlaylistRequestFactory` が playlist request identity の正規化、生成、source invalidation reason 判定を所有する。
- `MainWindowViewModel.PlaylistRequestIdentity` の型名と root の既存 static method は互換 forwarder として残す。
- `PlaylistViewPipelineTests` の request identity / invalidation reason 判定は factory 直接検証へ寄せる。

### Ticket I-1 境界確認結果

| group | root API / usage | I-2 方針 |
|---|---|---|
| 表示行 | `ChartRowsView`。XAML main table `ItemsSource`、一部テストが直接代入。setter は旧行 dispose、null を `new List<object>()` へ正規化、`ChartRowsView` 通知を持つ。`SetChartRowsView(...)` は grid summary 更新も行う。 | `MainChartListViewModel.Rows` へ移し、root `ChartRowsView` は pass-through。旧行 dispose、null 正規化、summary 更新の順序を維持する。XAML binding は変更しない。 |
| 選択 | `SelectedIndexChartRowsView`。XAML `SelectedIndex`、playlist rebuild / play history apply 時に `-1` へ戻す。setter は通知のみ。 | `MainChartListViewModel.SelectedIndex` へ移し、root は pass-through。playlist rebuild と play history apply の `-1` reset は root orchestration 側から呼ぶ。 |
| 列設定 | `ColumnsSettingsChartRowsView` / `ChartRowsViewRowDragKind`。XAML header/menu、`MainWindow.cs` header context menu が参照。setter は `ColumnsSettingsChartRowsView` と `ChartRowsViewRowDragKind` を通知。`ChartRowsViewRowDragKind` は独立 state ではなく列設定からの read-only 派生 property。 | `MainChartListViewModel.ColumnsSettings` へ移し、root は pass-through。`RowDragKind` は child 側でも `ColumnsSettings` から計算する派生 property とし、状態重複を作らない。`ChartRowsViewRowDragKind` 通知を維持。 |
| sort 表示 | `MainTableSortParameters` は `IsPlayHistoryViewActive ? PlayHistorySortParameters : SortParameters` の合成。XAML sort column/direction が参照。 | I-2 では root に残す。`SortParameters` 移動は通常一覧と play history の合成を分ける後続 Ticket に回す。 |
| filter | `KeywordFilter` / `ModeFilter`。setter が通常 refresh、play history queue、検索警告更新を起動。 | I-2 では root に残す。MainChartListViewModel 導入後の coordinator 化で扱う。 |
| refresh orchestration | `RefreshChartRowsView` は通常/playlist/play history/tree/columns/sort/cache を横断。tests が reflection と source-text で参照。 | I-2 では root facade のまま。I-3 で段階的に coordinator へ移す。 |

I-2 の最初の実装単位:

1. `MainChartListViewModel` を追加し、`Rows`、`SelectedIndex`、`ColumnsSettings` を所有させる。`RowDragKind` は `ColumnsSettings` から計算する read-only 派生 property とする。
2. `MainWindowViewModel` に `public MainChartListViewModel MainChartList { get; }` を追加する。
3. 既存 root property 名（`ChartRowsView`、`SelectedIndexChartRowsView`、`ColumnsSettingsChartRowsView`、`ChartRowsViewRowDragKind`）は pass-through と `PropertyChanged` relay で維持する。
4. XAML は変更しない。
5. `SortParameters`、`MainTableSortParameters`、`KeywordFilter`、`ModeFilter`、`RefreshChartRowsView` は I-2 では移さない。

I-2 確認対象:

- `ChartListVirtualViewTests`
- `MainColumnSettingModeTests`
- `CustomTableColumnSettingsTests`
- `PlayHistoryReadModelTests` の `MainTableSortParameters` 周辺
- `MainWindowContextMenuResourceTests` の binding/source-text 検査

### Ticket I-3 分割方針

`RefreshChartRowsView` は通常ライブラリ、仮想通常一覧、仮想サブセット、playlist 詳細、play history、tree selection、column settings、sort/cache、ログ更新を横断しているため、1 サイクルで coordinator へ移すと検証範囲が大きくなりすぎる。I-3 は以下の小単位で進める。

#### Ticket I-3a: chart row swap / summary state の child 化

1. `MainChartListViewModel` に `SummaryText` と rows swap helper を追加する。
2. root の `GridSummaryText` は pass-through と property relay を維持する。
3. root の `SetChartRowsView(...)` は child helper 呼び出しに寄せ、通常一覧 / playlist 詳細の summary 更新責務を child に移す。
4. play history / playlist summary は既存 root workflow から `GridSummaryText` に書き続け、表示名と通知は維持する。

確認対象:

- `ChartListVirtualViewTests`
- `PlaylistViewPipelineTests`
- `PlayHistoryReadModelTests`
- `MainWindowContextMenuResourceTests`

完了条件:

- rows swap と通常 summary 計算が `MainChartListViewModel` で完結する。
- root の `ChartRowsView` / `GridSummaryText` Binding path は変わらない。

#### Ticket I-3b: refresh request routing の coordinator 入り口作成

1. `ChartListRefreshCoordinator` を追加し、`RefreshChartRowsView` 冒頭の requested mode / current tree mode / play history stale 判定 / playlist route 判定を表す request context を作る。
2. 副作用のある `SetTreeViewFilterSelection`、`UpdateKeywordSearchPresentation`、`RegisterPlaylistSourceBuildRequest` は root callback として残し、意味の変わる fallback は入れない。
3. root は既存 private `RefreshChartRowsView(viewUpdateMode, object)` の呼び出し形状を維持する。

#### Ticket I-3c: virtual normal / virtual subset apply の coordinator 化

1. `TryApplyVirtualDefaultNormalLibraryView` と `TryApplyVirtualChartSubsetLibraryView` の重複している view swap / column setting / build timestamp / log metric 終端を coordinator 経由に寄せる。
2. source rows / order / cache の所有はまだ root に残し、移動範囲を終端処理に限定する。

#### Ticket I-3d: regular library pipeline の coordinator 化

`Ticket I-3d` は 1 実装サイクルとしては大きすぎるため、以下へ分割する。

##### Ticket I-3d-1: regular pipeline request / stage state 型の追加

1. 通常一覧の materialized fallback に必要な入力を `RegularChartListRefreshRequest` / `RegularChartListStageState` として定義する。
2. `ChartRowsFolderView`、`ChartRowsKeywordFilterView`、`ChartRowsModeFilterView` の read/write 契約を棚卸しし、root 所有のまま context 経由にする。
3. `KeywordFilter` / `ModeFilter` / `SortParameters` / tree mode / virtual fallback failure の入力関係を明文化する。
4. まだ filtering / sort 実装は移さず、型と forwarder だけで build/test できる状態にする。

確認対象:

- `ChartListVirtualViewTests`
- `LibraryChartRowSortEngineTests`
- `MainWindowContextMenuResourceTests`

完了条件:

- 通常一覧 pipeline の入力・出力・mutable cache が構造化される。
- `RefreshChartRowsView` の既存呼び出し形状は変わらない。

##### Ticket I-3d-2: keyword / mode filter stage の service 化

1. `ChartRowsFolderView` から keyword filter、mode filter を適用する pure 部分を `RegularChartListFilterService` へ移す。
2. `CreateModeFilterValueSet` と `GridKeywordSearchQuery.Parse(...)` の扱いを service 側へ寄せる。
3. root は stage input/output を受け取り、既存 field の更新だけを担当する。

確認対象:

- `GridKeywordSearchQueryTests`
- `ChartListVirtualViewTests`
- `LibraryChartRowSortEngineTests`

完了条件:

- keyword / mode filtering が root を new しなくても検証できる。
- filter stage の件数 logging が変わらない。

##### Ticket I-3d-3: regular sort / cache stage の coordinator 化

1. materialized 通常一覧の sort request を `RegularChartListSortRequest` として分離する。
2. `LibraryChartRowSortEngine.SortForMainView`、folder sort snapshot、normal library sort cache の選択を coordinator へ移す。
3. cache store / generation lock は root に残す場合でも、callback で注入して drift を避ける。
4. `CreateNormalLibrarySortCacheKey` / `TryGetNormalLibrarySortCache` / `StoreNormalLibrarySortCache` の所有者を後続で `ChartListSortCache` に移す前提を文書化する。

確認対象:

- `LibraryChartRowSortEngineTests`
- `ChartListVirtualViewTests`
- full `dotnet test`（sort/cache は共有影響が大きいため）

完了条件:

- materialized sort/cache 選択が root から独立し始める。
- `RefreshChartRowsView` 本体に残る通常一覧 sort 分岐が薄くなる。

##### Ticket I-3d-4: regular materialized apply / log terminal の統合

1. materialized 通常一覧の `RaiseMainTableSwapPreparing`、`ApplyMainColumnSettingForViewUpdate`、`SetChartRowsView`、build timestamp、`LogMainViewBuild` の終端を `ChartListRefreshCoordinator` へ寄せる。
2. I-3c の virtual terminal helper と共通化できる箇所を再利用する。
3. playlist detail と play history の logging と混ざらないよう、通常一覧専用 result 型を使う。

確認対象:

- `ChartListVirtualViewTests`
- `PlaylistViewPipelineTests`
- `PlayHistoryReadModelTests`

完了条件:

- `RefreshChartRowsView` の通常一覧 terminal apply が coordinator 経由になる。
- virtual / materialized の terminal apply の順序が揃う。

##### Ticket I-3d-5: RefreshChartRowsView facade 化完了

1. `RefreshChartRowsView` を request normalization、route selection、各 coordinator 呼び出しだけに縮小する。
2. play history と playlist はまだ root workflow でもよいが、main chart list 通常/仮想 workflow は child/coordinator 側へ寄せる。
3. source-text / reflection tests を必要に応じて coordinator/service 直接検証へ移す。
4. 手動一覧 smoke test を実施する。

確認対象:

- `ChartListVirtualViewTests`
- `LibraryChartRowSortEngineTests`
- `PlaylistViewPipelineTests`
- `PlayHistoryReadModelTests`
- `MainWindowContextMenuResourceTests`
- 手動 smoke: 通常一覧、仮想通常一覧、duplicate/missing/installed/pending subset、sort、keyword、mode filter

完了条件:

- `RefreshChartRowsView` は facade として読める大きさになる。
- main chart list の sort/filter/virtualization/cache の体感挙動が変わらない。

### Ticket E progress property 契約

| group | root property | normalize / side effect | relay |
|---|---|---|---|
| Install pipeline | `IsInstallPipelineStatusActive`, `InstallPipelineLabel`, `InstallPipelineSubLabel`, `InstallPipelineValue`, `InstallPipelineMaximum`, `InstallPipelineCanCancel` | label は null を空文字、maximum は 1 以上 | 同名 `PropertyChanged` |
| Maintenance rescan | `IsMaintenanceRescanProgressActive`, `MaintenanceRescanLabel`, `MaintenanceRescanSubLabel`, `MaintenanceRescanValue`, `MaintenanceRescanMaximum`, `MaintenanceRescanCanCancel` | label は null を空文字、maximum は 1.0 以上 | 同名 `PropertyChanged` |
| Folder auto rename | `IsFolderAutoRenameProgressActive`, `FolderAutoRenameProgressLabel`, `FolderAutoRenameProgressSubLabel`, `FolderAutoRenameProgressValue`, `FolderAutoRenameProgressMaximum` | label は null を空文字、maximum は 1.0 以上 | 同名 `PropertyChanged` |
| Playlist sync | `IsPlaylistSyncProgressActive`, `PlaylistSyncProgressLabel`, `PlaylistSyncProgressSubLabel`, `PlaylistSyncProgressValue`, `PlaylistSyncProgressMaximum` | label は null を空文字 | 同名 `PropertyChanged` |
| Startup progress | `IsStartupProgressActive`, `StartupProgressLabel`, `StartupProgressSubLabel`, `StartupProgressValue`, `StartupProgressMaximum` | label は null を空文字。`IsStartupProgressActive` 変更時に `IsLibraryOperationInProgress` 通知、LR2 song DB sync resync availability 更新、LR2 song DB sync status presentation 再計算を維持 | 同名 `PropertyChanged` と `IsLibraryOperationInProgress` |
| LR2 song DB sync status | `IsLr2SongDbSyncStatusActive`, `Lr2SongDbSyncStatusLabel`, `Lr2SongDbSyncStatusSubLabel`, `Lr2SongDbSyncStatusToolTip`, `Lr2SongDbSyncStatusProgressValue`, `Lr2SongDbSyncStatusProgressMaximum`, `IsLr2SongDbSyncStatusProgressVisible`, `IsLr2SongDbSyncRetryVisible`, `IsLr2SongDbSyncCancelVisible`, `IsLr2SongDbSyncCleanupVisible` | text は null を空文字。retry/cancel/cleanup は root の public setter を維持 | 同名 `PropertyChanged` |

## 1. 現状観測メモ

### ファイル規模

行数は空行を含む総行数で、PowerShell の `(Get-Content <path>).Count` による。

| 対象 | 行数/範囲 | 備考 |
|---|---:|---|
| `MainWindowViewModel.cs` 全体 | 24,250 行（開始時 33,379 行） | nested dialog VM、先頭 helper、進捗/再生/一覧の一部は分離済み。root 本体には workflow と互換 facade がまだ多い |
| `MainWindowViewModel.SettingDialogViewModel.cs` | 6,697 行 | 設定値の一時保持、検証、保存後処理、LR2/Beatoraja/プレイ履歴/エンコード/プレイヤー設定を保持 |
| `MainWindowViewModel.PlaylistPropertyDialogViewModel.cs` | 960 行 | プレイリストプロパティ編集 |
| `MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel.cs` | 463 行 | プレイリストサマリ一括編集 |
| `ChartListRefreshCoordinator.cs` | 407 行 | main chart list refresh の coordinator。virtual terminal apply は移動済み |
| `OperationProgressHubViewModel.cs` | 431 行 | 進捗/ステータス表示 state の子 VM |
| `MainViewRefreshDecisionService.cs` | 370 行 | main-view refresh 判定と sort invalidation 判定 |
| `MainChartListViewModel.cs` | 250 行 | main chart list state の受け皿。まだ pass-through 中心 |
| `PlaybackPanelViewModel.cs` | 154 行 | 上部再生パネル state の子 VM |
| `MainWindow.xaml` | 2,510 行 | ルート VM への Binding が多い |
| `MainWindow.cs` | 10,289 行 | UI 操作、ダイアログ、ドラッグ&ドロップ、右クリックなどが残る |

### MainWindowViewModel 内の大きなメソッド例

行数は計画開始時または直近抽出前の観測値を含む。各 ticket の実装後に、残存 method と行数を再計測して更新する。

| メソッド | おおよその行数 | 主要責務 |
|---|---:|---|
| `Initialize` | 632 行 | 起動初期化の統括 |
| `RefreshChartRowsView` | 331 行 | メイン一覧の表示更新統括 |
| `ApplyPlayHistoryView` | 325 行 | プレイ履歴ビュー構築 |
| `ApplyPlaylistSummaryExternalPropertyInitializationAsync` | 235 行 | プレイリストサマリ外部プロパティ初期化 |
| `RunVirtualNormalLibraryOrderPrewarm` | 225 行 | 仮想一覧のソート事前計算 |
| `loadColumnSetting` | 172 行 | 一覧カラム設定切替 |
| `PlayStartBmsFile` | 148 行 | 再生開始処理 |
| `StartDeferredExternalPlaylistSync` | 116 行 | 外部プレイリスト同期遅延実行 |
| `RecomputeStartupProgressPresentation` | 105 行 | 起動進捗表示再計算 |

### 移動時の注意点

このリポジトリには、ソース文字列を直接検査するテストがある。特に次は partial 分割だけでも壊れやすい。

- `Lr2PlayHistorySchemaUiTests.cs`: `MainWindowViewModel.cs` を `File.ReadAllText` して特定文字列を確認している
- `PlayHistoryReadModelTests.cs`: `MainWindowViewModel.ApplyPlayHistoryDisplayTargetRows` を reflection で参照している
- `MainWindowContextMenuResourceTests.cs`: `MainWindowViewModel.cs` や `MainWindow.cs` の文字列検査がある
- `BmsLibraryMutationBoundaryTests.cs`: `MainWindowViewModel.cs` / `MainWindow.cs` の境界検査がある
- `DialogRouteConsolidationTests.cs`: dialog route の source-text 検査で `MainWindowViewModel.cs` / `MainWindow.cs` を読む
- `PlaylistConcurrencyArchitectureTests.cs`: playlist 更新境界の source-text 検査で `MainWindowViewModel.cs` を読む
- `ExplorerOpenServiceTests.cs`: `MainWindow.cs` の explorer open 経路を source-text 検査する
- `SettingDialogCustomFolderOutputBaseTests.cs`: `MainWindowViewModel.SettingDialogViewModel` の private メソッド/フィールドを reflection で参照している

そのため、最初の分割フェーズでは「型名・ネスト関係・メンバー名」を維持し、必要なテストは `MainWindowViewModel*.cs`、`ViewModels/MainWindow/**/*.cs`、`MainWindow*.cs`、`Views/MainWindow/**/*.cs` を連結して読む helper へ置き換える。

## 2. 目標アーキテクチャ

```text
MainWindowViewModel  // Shell / composition root
├─ PlaybackPanelViewModel
│  └─ PlaybackService / IPlaybackHostAdapter
├─ SidebarNavigationViewModel
│  ├─ LibraryTreeViewModel
│  ├─ PlaylistTreeViewModel
│  ├─ InstallTreeViewModel
│  ├─ MaintenanceTreeViewModel
│  └─ PlayHistoryArchiveTreeViewModel
├─ MainChartListViewModel
│  ├─ ChartListRefreshCoordinator
│  ├─ ChartListSourceBuilder
│  ├─ ChartListSortCache
│  └─ KeywordSearchPresentationService
├─ PlaylistWorkspaceViewModel
│  ├─ PlaylistDetailViewModel
│  ├─ PlaylistSummaryViewModel
│  ├─ PlaylistOpenCoordinator
│  └─ PlaylistExternalSyncCoordinator
├─ OperationProgressHubViewModel
│  ├─ StartupProgressViewModel
│  ├─ InstallPipelineStatusViewModel
│  ├─ PlaylistSyncProgressViewModel
│  ├─ MaintenanceRescanProgressViewModel
│  └─ Lr2SongDbSyncStatusViewModel
└─ SettingDialogViewModel
   ├─ AppSettingsStore / AppSettingsSnapshot
   ├─ SettingsValidationService
   └─ SettingsPostSaveCoordinator
```

### MainWindowViewModel に残すもの

- `BMSLibrary` / `BMSPlaylist` / `LR2Config` / `IBMSPlayer` のライフサイクル統括。将来的には `MainWindowRuntimeContext` に集約する。
- 起動、再読み込み、終了、シャットダウン調停。
- 子 ViewModel の生成と依存関係注入。
- View へ依頼する必要があるイベント。例: 初期設定ダイアログ表示、初期化失敗通知、テーブル描画更新要求。
- 横断的な UI refresh 抑制/再開。ただし最終的には `UiRefreshCoordinator` へ寄せる。

### MainWindowViewModel から出すもの

- 各画面領域のプロパティ群。
- 行生成、ソート、フィルタリング、キャッシュキー生成。
- 設定検証・設定保存後処理。
- 再生開始/停止/次曲/前曲の詳細。
- プレイリスト詳細/サマリの構築。
- プレイ履歴読み取り・集計・表示行構築。
- 進捗ラベルやボタン表示状態の算出。
- 外部 URL / スコアビューア / BMT 出力などの個別機能処理。

## 3. 実装ルール

1. **最初は挙動を変えない。** partial 分割と型移動は、できる限り機械的な移動だけにする。
2. **XAML Binding はすぐ変えない。** 子 ViewModel 導入直後は、既存の root-level プロパティを pass-through として残す。
3. **ネスト型の公開形状を維持する。** 既存コードは `MainWindowViewModel.SettingDialogViewModel` などを参照しているため、初期段階では nested partial class のまま別ファイルへ移す。
4. **partial 間の field initializer 順序に依存しない。** まずフィールドは元の順序を保つ 1 ファイルに残すか、`State` ファイルに順序維持で移す。初期化順序が意味を持つ可能性があるため、フィールドを無計画に複数 partial へ分散しない。
5. **UI 型を新しい ViewModel へ持ち込まない。** 例外的に現状 `SetuBMplayPanel(Panel panel)` は root facade に残し、子 VM には `IPlaybackHostAdapter` のような抽象化を渡す。
6. **`async void` を増やさない。** View イベントの入口だけに限定し、内部実装は `Task` を返す。
7. **`Settings.Default` 直参照を新規に増やさない。** 設定系抽出時は P1-03 の `AppSettingsStore` / `AppSettingsSnapshot` 経由へ寄せる。
8. **テスト用 public/internal forwarder はすぐ消さない。** 既存テストを一括で壊さず、移行後に整理する。

## 4. Phase 0: 安全網整備

### 0-1. 作業用メモと計測を追加

追加候補:

- `.tmp/plans/mainwindow-viewmodel-refactor.md`
- `.tmp/mainwindow-viewmodel-metrics.ps1`

`.tmp/` は git 管理外の一時領域として使う。ticket をまたいで残す必要がある判断、棚卸し、設計メモは `devdocs/` 側へ移し、`.tmp/` のメモを恒久的な引き継ぎ資料にしない。

`metrics.ps1` は、最低限次を出せばよい。

```powershell
(Get-Content .\BeMusicSeeker\ViewModels\MainWindowViewModel.cs).Count
rg "^\s*(public|internal|private|protected).*\(" .\BeMusicSeeker\ViewModels\MainWindowViewModel.cs
```

### 0-2. ソース文字列テストを partial 対応にする

`BeMusicSeeker.Tests` に helper を追加する。

追加候補:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`

責務:

- repository root の探索を共通化する。
- source-text test 用に deterministic order で複数ファイルを連結して返す。
- `MainWindowViewModel.cs` 単体ではなく、以下を連結して返す。
  - `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
  - `BeMusicSeeker/ViewModels/MainWindowViewModel*.cs`
  - `BeMusicSeeker/ViewModels/MainWindow/**/*.cs`
- `MainWindow.cs` 単体ではなく、以下を連結して返す。
  - `BeMusicSeeker/Views/MainWindow.cs`
  - `BeMusicSeeker/Views/MainWindow*.cs`
  - `BeMusicSeeker/Views/MainWindow/**/*.cs`
- 既存の `File.ReadAllText(... MainWindowViewModel.cs)` と `File.ReadAllText(... MainWindow.cs)` を helper 呼び出しへ置換する。

着手時の棚卸し:

```powershell
rg "File\.ReadAllText.*MainWindowViewModel\.cs|File\.ReadAllText.*MainWindow\.cs" .\BeMusicSeeker.Tests -g "*.cs"
```

対象候補:

- `BmsLibraryMutationBoundaryTests.cs`
- `DialogRouteConsolidationTests.cs`
- `Lr2PlayHistorySchemaUiTests.cs`
- `MainWindowContextMenuResourceTests.cs`
- `PlaylistConcurrencyArchitectureTests.cs`
- `ExplorerOpenServiceTests.cs`

受け入れ条件:

- ソース文字列テストの意図は維持する。
- `MainWindowViewModel` / `MainWindow` のファイル分割だけで source-text test が落ちない。
- 可能な箇所は文字列検査から reflection/動作検査へ置き換える。ただし Phase 0 では最小変更を優先する。

### 0-3. 標準確認コマンド

PowerShell で実行する。

```powershell
dotnet restore .\BeMusicSeeker.sln
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
dotnet roslynator analyze .\BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal
```

大きな分割中は、まず次の関連テストを個別に回す。

```powershell
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~MainWindowViewModelStartupProgressTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistViewPipelineTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~ChartListVirtualViewTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistSummaryAggregationTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~SettingDialogCustomFolderOutputBaseTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~Lr2PlayHistorySchemaUiTests"
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~MainWindowContextMenuResourceTests"
```

## 5. Phase 1: 機械的な partial 分割

目的は、依存関係を変えずにファイルを切ること。ここでは設計改善を欲張らない。

### 1-1. ルートを partial 化する

変更:

```csharp
public partial class MainWindowViewModel : ViewModel
```

ネスト型も、別ファイルへ移す対象は partial にする。

```csharp
public partial class SettingDialogViewModel : ViewModel
public partial class PlaylistSummaryBulkEditDialogViewModel : ViewModel
public partial class PlaylistPropertyDialogViewModel : ViewModel
```

### 1-2. 先頭の補助型を別ファイルへ移す

移動候補:

- `PackageChartSourceSnapshot`
- `BmsonLibraryRowCacheSyncResult`
- `NormalLibrarySortCacheKey`
- `VirtualChartSubsetSortCacheKey`
- `MainViewSummaryCacheKey`
- `VirtualNormalLibrarySortDescriptor`
- `MainViewDataDependency`
- `MainViewRefreshAction`
- `MainViewRefreshDecision`

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/MainViewRefreshTypes.cs
BeMusicSeeker/ViewModels/MainWindow/NormalLibrarySortCacheTypes.cs
BeMusicSeeker/ViewModels/MainWindow/PackageChartSourceSnapshot.cs
```

注意:

- namespace は `BeMusicSeeker.ViewModels;` のままにする。
- アクセス修飾子は変えない。
- XML コメントが不足している internal/public 型は、移動時に最小限追加する。

### 1-3. ネストされたダイアログ VM を nested partial のまま別ファイルへ移す

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.SettingDialogViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.PlaylistPropertyDialogViewModel.cs
```

ファイル形状:

```csharp
namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    public partial class SettingDialogViewModel : ViewModel
    {
        // 既存中身を移動
    }
}
```

受け入れ条件:

- `MainWindowViewModel.SettingDialogViewModel` という型名が維持される。
- `SettingDialog.cs`、`PlayHistoryFolderDisplayPresetEditDialog.cs`、既存テストの型参照を壊さない。
- `SettingDialogCustomFolderOutputBaseTests` の reflection 対象名を維持する。

### 1-4. MainWindowViewModel 本体を責務別 partial に切る

初期の分割候補:

| ファイル | 移動する主な範囲/責務 |
|---|---|
| `MainWindowViewModel.State.cs` | フィールド、イベント、プロパティのうち初期化順序が絡むもの。順序を保持する。 |
| `MainWindowViewModel.Logging.cs` | `LogUiSuppression`、`LogPlaylist...` 系の static logging helper |
| `MainWindowViewModel.PlaylistPipeline.cs` | プレイリスト open/reload/source build、`PlaylistRequestIdentity`、`PlaylistBuildRequest`、coalescing |
| `MainWindowViewModel.ChartList.cs` | `RefreshChartRowsView`、通常ライブラリ/仮想サブセット/カラム設定/ソートキャッシュ |
| `MainWindowViewModel.PlayHistory.cs` | `ApplyPlayHistoryView`、プレイ履歴読み取り、期間ツリー、サマリカード |
| `MainWindowViewModel.Playback.cs` | 再生開始/停止/次/前、再生パネル表示、プレイヤー状態 |
| `MainWindowViewModel.Progress.cs` | 起動進捗、インストール進捗、メンテナンス/フォルダリネーム/Playlist sync/LR2 song DB sync 表示 |
| `MainWindowViewModel.SettingsFacade.cs` | `settingDialog` 生成、設定保存後に root 側へ反映する薄い接続部 |
| `MainWindowViewModel.PlaylistSummary.cs` | プレイリストサマリ表示、集計、外部プロパティ初期化、一括編集適用 |
| `MainWindowViewModel.InstallMaintenance.cs` | インストール、再スキャン、ヘルスチェック、重複、pending install 操作 |
| `MainWindowViewModel.ExternalPlaylist.cs` | 外部プレイリスト import/sync、URL 補完 |
| `MainWindowViewModel.ScoreViewer.cs` | スコアビューア登録/URL 生成/状態確認 |
| `MainWindowViewModel.AudioExport.cs` | `ConvertBMSToAudioFiles` |
| `MainWindowViewModel.Initialization.cs` | `Initialize`、`ReloadTables`、`ReloadScoresOnly`、`ReloadFileDiff`、`ReinitializeLibrary`、終了処理 |

作業単位:

1. 1 ファイルずつ移動する。
2. 移動後に `dotnet build` を通す。
3. 大きな範囲を動かしたら関連テストを個別実行する。
4. partial 分割中はロジック変更禁止。変数名改善やコメント追加は、対象メソッドを移した直後の最小範囲に留める。

受け入れ条件:

- 型名、メンバー名、Binding path は不変。
- `MainWindowViewModel.cs` は shell 宣言とごく少量の core だけになる。
- ただし root field の初期化順序が不明なものは無理に分散しない。
- 全体テストが通る。

## 6. Phase 2: 子 ViewModel を導入し、root は pass-through にする

目的は「画面領域ごとに状態を所有する ViewModel」を作ること。XAML はすぐに変更せず、まず root の既存プロパティを子 VM へ委譲する。

### 2-1. 共通の所有コンテキストを追加

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/MainWindowRuntimeContext.cs
```

初期形:

```csharp
internal sealed class MainWindowRuntimeContext
{
    internal MainWindowRuntimeContext(
        BMSLibrary files,
        BMSPlaylist tables,
        LR2Config lr2Config,
        IBMSPlayer bmsPlayer,
        Dispatcher uiDispatcher,
        ResourceService resources)
    {
        Files = files;
        Tables = tables;
        Lr2Config = lr2Config;
        BmsPlayer = bmsPlayer;
        UiDispatcher = uiDispatcher;
        Resources = resources;
    }

    internal BMSLibrary Files { get; }
    internal BMSPlaylist Tables { get; }
    internal LR2Config Lr2Config { get; }
    internal IBMSPlayer BmsPlayer { get; }
    internal Dispatcher UiDispatcher { get; }
    internal ResourceService Resources { get; }
}
```

context は基本的に constructor injection + get-only とし、差し替えが必要な lifecycle 変更は shell-owned method/event で明示する。settable な共有箱にすると子 VM が root proxy 化しやすいため、避ける。

移行の初期段階では `MainWindowViewModel owner` を一時 adapter として渡してもよい。ただし ticket 内で owner 経由で触る member を列挙し、完了時に owner 依存が増えていないことを確認する。各子 VM の public API は owner 前提にしない。

### 2-2. OperationProgressHubViewModel から始める

最初の抽出対象に適している理由:

- UI 表示用の bool/string/double が多く、ドメイン変更が少ない。
- `MainWindow.xaml` の進捗 binding 群をひとまとまりにできる。
- 抽出後の回帰が見つけやすい。

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/OperationProgressSnapshot.cs
```

移すプロパティ群:

- `IsStartupProgressActive`
- `StartupProgressLabel`
- `StartupProgressSubLabel`
- `StartupProgressValue`
- `StartupProgressMaximum`
- `IsInstallPipelineStatusActive`
- `InstallPipelineLabel`
- `InstallPipelineSubLabel`
- `InstallPipelineValue`
- `InstallPipelineMaximum`
- `InstallPipelineCanCancel`
- `IsMaintenanceRescanProgressActive`
- `MaintenanceRescanLabel`
- `MaintenanceRescanSubLabel`
- `MaintenanceRescanValue`
- `MaintenanceRescanMaximum`
- `MaintenanceRescanCanCancel`
- `IsFolderAutoRenameProgressActive`
- `FolderAutoRenameProgressLabel`
- `FolderAutoRenameProgressSubLabel`
- `FolderAutoRenameProgressValue`
- `FolderAutoRenameProgressMaximum`
- `IsPlaylistSyncProgressActive`
- `PlaylistSyncProgressLabel`
- `PlaylistSyncProgressSubLabel`
- `PlaylistSyncProgressValue`
- `PlaylistSyncProgressMaximum`
- `IsLr2SongDbSyncStatusActive`
- `Lr2SongDbSyncStatusLabel`
- `Lr2SongDbSyncStatusSubLabel`
- `Lr2SongDbSyncStatusToolTip`
- `Lr2SongDbSyncStatusProgressValue`
- `Lr2SongDbSyncStatusProgressMaximum`
- `IsLr2SongDbSyncStatusProgressVisible`
- `IsLr2SongDbSyncRetryVisible`
- `IsLr2SongDbSyncCancelVisible`
- `IsLr2SongDbSyncCleanupVisible`

Root 側は以下のような pass-through を残す。

```csharp
public bool IsStartupProgressActive
{
    get => ProgressHub.IsStartupProgressActive;
    private set => ProgressHub.IsStartupProgressActive = value;
}
```

移動前に progress property ごとの notification / side-effect contract 表を作る。特に `IsStartupProgressActive` は旧 property 名の通知だけでなく、`IsLibraryOperationInProgress` の通知、LR2 song DB sync availability 更新、LR2 song DB sync status presentation 再計算を維持する。

子 VM の `PropertyChanged` を root が購読し、既存 Binding 名で `RaisePropertyChanged` を転送する。root 側の setter 可視性は既存と同等に保ち、root 内部からの代入は pass-through setter または child VM の明示的な update method へ逃がす。

受け入れ条件:

- XAML 変更なしで既存 UI が動く。
- `MainWindowViewModelStartupProgressTests` が通る。
- root の進捗 field が削減される。
- progress property の副作用契約が維持される。

### 2-3. PlaybackPanelViewModel を追加

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/IPlaybackHostAdapter.cs
BeMusicSeeker/ViewModels/MainWindow/BmsPlaybackService.cs
```

移す候補:

- `PlayerHeaderTitle`
- `PlayerHeaderArtist`
- `PlayerHeaderSubtitle`
- `PlayerPanelState`
- `NowPlayingBMS`
- `CurrentlyPlayingTime`
- `CurrentlyPlayingDuration`
- `PlayerVolume`
- `RepeatPlayMode`
- `FolderSkipPlayMode`
- `SinglePlayMode`
- `PlayStartBmsFile`
- `PlayEndBMSFile`
- `PlayNextBMSfile`
- `PlayPreviousBMSfile`
- `SetuBMplayPanel` のうち UI handle 非依存部分

注意:

- `Panel` や `Handle` は ViewModel に直接渡さない。root facade または `IPlaybackHostAdapter` に閉じ込める。
- `IBMSPlayer` の lifecycle は当面 root が保持し、子 VM へ参照を渡す。

受け入れ条件:

- 上部再生パネルの表示と操作が変わらない。
- 再生中行、次/前、停止、音量、リピート/単曲/フォルダスキップの既存挙動を維持する。
- `MainWindow.xaml` はまだ root Binding のままでよい。

### 2-4. KeywordSearchPresentation を抽出

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/KeywordSearchPanelViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/KeywordSearchPresentationService.cs
```

移す候補:

- `KeywordFilter`
- `PlaylistSummaryKeywordFilter`
- `KeywordSearchWarningText`
- `PlaylistSummaryKeywordSearchWarningText`
- `IsKeywordSearchHelpOpen`
- `IsPlaylistSummaryKeywordSearchHelpOpen`
- `KeywordSearchSuggestions`
- `PlaylistSummaryKeywordSearchSuggestions`
- `KeywordSearchSuggestionHeaderText`
- `PlaylistSummaryKeywordSearchSuggestionHeaderText`
- `BuildKeywordSearchWarningText`
- `BuildKeywordSearchHelpText`
- `BuildKeywordSearchSuggestionHeaderText`
- `BuildKeywordSearchHistorySuggestions`

受け入れ条件:

- 通常一覧とプレイリストサマリの検索 UI が変わらない。
- `GridKeywordSearchQueryTests`、`KeywordSearchPresentationTests` が通る。

### 2-5. MainChartListViewModel を追加

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/MainChartListViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/ChartListRefreshCoordinator.cs
BeMusicSeeker/ViewModels/MainWindow/NormalLibraryChartListSource.cs
BeMusicSeeker/ViewModels/MainWindow/VirtualChartSubsetSource.cs
BeMusicSeeker/ViewModels/MainWindow/MainViewRefreshDecisionService.cs
```

移す候補:

- `ChartRowsView`
- `SelectedIndexChartRowsView`
- `ChartRowsViewRowDragKind`
- `MainTableSortParameters`
- `ColumnsSettingsChartRowsView`
- `ColumnSettingsVisibilityForPlaylist`
- `ModeFilter`
- `GridHeaderText`
- `GridSummaryText`
- `RefreshChartRowsView`
- `TryApplyVirtualDefaultNormalLibraryView`
- `TryApplyVirtualChartSubsetLibraryView`
- `ApplyVirtualNormalLibraryFilters`
- `CreateNormalLibrarySortCacheKey`
- `CreateVirtualChartSubsetSortCacheKey`
- `BuildMainViewRefreshDecision`
- `GetMainViewSortColumnDependency`
- `loadColumnSetting`

段階:

1. `MainViewRefreshDecisionService` のような pure service だけ先に抜く。
2. `ChartRowsView` の所有を `MainChartListViewModel` に移す。
3. root は pass-through と `RefreshChartRowsView(...)` facade を維持する。
4. XAML Binding は Phase 4 まで変更しない。

受け入れ条件:

- `ChartListVirtualViewTests`
- `LibraryChartRowSortEngineTests`
- `MainColumnSettingModeTests`
- `CustomTableColumnSettingsTests`
- 一覧の sort/filter/virtualization の体感挙動が変わらない。

### 2-6. PlaylistWorkspaceViewModel を追加

追加候補:

```text
BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistOpenCoordinator.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistDetailPanelViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistSummaryPanelViewModel.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistSummaryPresentationService.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistExternalSyncCoordinator.cs
```

移す候補:

- `BMSTables`
- `IsPlaylistSummaryMode`
- `PlaylistSummaryView`
- `PlaylistSummaryColumnsSettings`
- `PlaylistSummarySortParameters`
- `PlaylistSummaryOwnedFilter`
- `PlaylistSummaryViewApplied`
- `PlaylistRequestIdentity`
- `PlaylistPresentationIdentity`
- `PlaylistBuildRequest`
- `RegisterPlaylistSourceBuildRequest`
- `ProcessPendingPlaylistBuildRequests`
- `BuildPlaylistSourceRows`
- `RebuildPlaylistSource`
- `ApplyPlaylistSourceRows`
- `BuildPlaylistSummaryRows`
- `BuildPlaylistSummaryPresentationRows`
- `ApplyPlaylistSummary...` 系
- `StartDeferredExternalPlaylistSync`

受け入れ条件:

- `PlaylistViewPipelineTests`
- `PlaylistSummaryAggregationTests`
- `PlaylistSummaryBulkEditTests`
- `BmsPlaylistUpdateTests` の MainWindowViewModel 関連ケース
- プレイリストツリー選択、フォルダ選択、未所持フィルタ、サマリ表示、一括編集、外部同期が変わらない。

### 2-7. SettingDialogViewModel を段階的に独立させる

現時点では `MainWindowViewModel.SettingDialogViewModel` として残す。中身をサービスへ出す。

追加候補:

```text
BeMusicSeeker/Properties/AppSettingsSnapshot.cs
BeMusicSeeker/Properties/AppSettingsStore.cs
BeMusicSeeker/ViewModels/Settings/SettingsValidationService.cs
BeMusicSeeker/ViewModels/Settings/SettingsPostSaveCoordinator.cs
BeMusicSeeker/ViewModels/Settings/CustomFolderOutputBaseSettingsService.cs
BeMusicSeeker/ViewModels/Settings/PlayerSettingsService.cs
BeMusicSeeker/ViewModels/Settings/PlayHistoryDisplayPresetSettingsService.cs
```

settings の読み取り snapshot / store 名は P1-03 と合わせて `AppSettingsSnapshot` / `AppSettingsStore` に寄せる。dialog 固有の edit session や validation service は `ViewModels/Settings/` 側に置いてよい。

最初に抜く候補:

- `backupSavedSettingsCore`
- `HasSettingValueChanges`
- `BuildSettingsPostSaveImpact`
- `CheckCurrentRequiredSettingsForSave`
- `CheckValidation`
- `ApplyCustomFolderAdditionalOutputBaseRegistrationChanges`
- `necessaryStepsAfterSaved`
- `SaveSettingsCore`
- `ResetSettings`

受け入れ条件:

- `SettingDialogCustomFolderOutputBaseTests`
- `Lr2PlayHistorySchemaUiTests`
- `LocalizationResourceParityTests`
- 設定保存、キャンセル、リセット、初回設定、LR2/Beatoraja 切替、再起動判定が変わらない。

## 7. Phase 3: pure service / reducer 抽出

子 VM 導入後、複雑なロジックをさらにサービスへ出す。

### 3-1. MainViewRefreshDecisionService

抽出対象:

- `BuildMainViewRefreshDecision`
- `GetMainViewSortColumnDependency`
- `IsMainViewScoreSortColumn`
- `IsMainViewChartInfoSortColumn`
- `IsMainViewMaintenanceSortColumn`
- `IsMainViewWarningSortColumn`
- `IsMainViewDisplayRefreshEnough`

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/MainViewRefreshDecisionService.cs
```

既存の `MainWindowViewModel.BuildMainViewRefreshDecisionForTest` は当面 forwarder として残す。

### 3-2. StartupProgressReducer

抽出対象:

- `StartupProgressState`
- `StartupProgressTestResult`
- `ReduceStartupProgressForTest`
- `GetInitialExpectedStartupProgressPhases`
- `RecomputeStartupProgressPresentation` の表示算出部分

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/StartupProgressReducer.cs
BeMusicSeeker/ViewModels/MainWindow/StartupProgressPresentation.cs
```

受け入れ条件:

- `MainWindowViewModelStartupProgressTests` が root ではなく reducer を直接検証できるようになる。

### 3-3. PlaylistRequestFactory / PlaylistSourceInvalidationService

抽出対象:

- `NormalizePlaylistFolderName`
- `NormalizePlaylistKeywordFilter`
- `CreatePlaylistRequestIdentity`
- `DeterminePlaylistSourceInvalidationReason`
- `ResolvePlaylistColumnSettingMode`
- `ResolveMainColumnSettingMode`
- `ShouldUsePlaylistBuildCoalescingWindow`

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/PlaylistRequestFactory.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistSourceInvalidationService.cs
```

### 3-4. PlaylistSummaryPresentationService

抽出対象:

- `CalculatePlaylistSummaryCounts`
- `BuildPlaylistSummaryPresentationRows`
- `BuildPlaylistSummaryDataRefreshDecision`
- `FilterPlaylistSummaryExternalPropertyInitializationOutputConflicts`
- `ApplyPlaylistSummaryCustomFolderOutputTypes` の pure 部分

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/PlaylistSummaryPresentationService.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistSummaryMutationService.cs
```

### 3-5. PlayHistoryPresentationService

抽出対象:

- プレイ履歴読み取り結果から `PlayHistoryRow` を構築する処理
- 期間ツリー生成
- サマリカード生成
- keyword/filter/sort 適用

配置候補:

```text
BeMusicSeeker/ViewModels/MainWindow/PlayHistoryPresentationService.cs
BeMusicSeeker/ViewModels/MainWindow/PlayHistoryPeriodTreeService.cs
```

### 3-6. Settings services

抽出対象:

- settings snapshot 作成/復元
- validation
- custom folder output base の採用/競合検出
- LR2 / Beatoraja / プレイヤー / エンコーダー設定の保存後処理

最終目標:

`SettingDialogViewModel` は UI 入力状態と command だけを持ち、検証・保存差分判定・保存後副作用は service に委譲する。

## 8. Phase 4: XAML Binding を子 ViewModel へ移行

ここで初めて View 側の DataContext を分割する。

### 4-1. 再生パネル

`MainWindow.xaml` の上部パネル root に DataContext を設定する。

```xml
<Grid DataContext="{Binding PlaybackPanel}">
```

変更:

- `{Binding PlayerHeaderTitle}` → `{Binding PlayerHeaderTitle}` のまま、DataContext を子 VM にする。
- root を参照する必要がある command は、必要に応じて `RelativeSource AncestorType=Window` か root command relay を使う。

受け入れ条件:

- 上部パネルの Binding path が root から消える。
- code-behind が root の playback property を直接触る箇所を減らす。

### 4-2. 進捗/ステータス表示

進捗表示領域に DataContext を設定する。

```xml
<Grid DataContext="{Binding ProgressHub}">
```

変更:

- Startup / Install / Playlist sync / Maintenance / Folder rename / Lr2SongDbSync の Binding を `ProgressHub` 配下に移す。
- root pass-through はこの段階では残してよい。

### 4-3. メイン一覧

メイン一覧領域に DataContext を設定する。

```xml
<Grid DataContext="{Binding MainChartList}">
```

変更:

- `ChartRowsView`
- `SelectedIndexChartRowsView`
- `MainTableSortParameters`
- `ColumnsSettingsChartRowsView`
- `ModeFilter`
- keyword search 関連
- grid header/summary

注意:

- ContextMenu は visual tree 外になるため、`PlacementTarget.DataContext` または BindingProxy を使う。
- 既存の `DataContext.IsWriteLockHeld...` のような root 参照は、どの component が持つべきかを明確化してから移す。

### 4-4. 左サイドバー

上段/下段ツリーに DataContext を分ける。

候補:

```xml
<TreeView DataContext="{Binding Sidebar.PlaylistTree}" ItemsSource="{Binding RootNodes}" />
<TreeView DataContext="{Binding Sidebar.LibraryTree}" ItemsSource="{Binding RootNodes}" />
```

最初は ItemsSource だけ移し、選択/右クリック/ドラッグ&ドロップは code-behind から段階的に command 化する。

### 4-5. プレイリストサマリ/詳細

プレイリスト詳細とサマリ表示領域を `PlaylistWorkspace` に寄せる。

```xml
<Grid DataContext="{Binding PlaylistWorkspace}">
```

受け入れ条件:

- root から playlist detail/summary 用 Binding が大幅に減る。
- `MainWindow.cs` の `mainWindowViewModel.playlistPropertyDialog = ...` のような直接生成を `PlaylistWorkspace.ShowPropertyDialogCommand` へ寄せ始める。

## 9. Phase 5: root facade を削除し、MainWindowViewModel を Shell 化

XAML と code-behind の参照が子 VM へ移ったら、root の pass-through を削除する。

### 削除対象

- 再生パネル用 root property/method
- 進捗表示用 root property/method
- chart list 用 root property/method
- playlist detail/summary 用 root property/method
- setting dialog 内の pure service へ移った static/internal helper

### 残す対象

- `InitializeAsync` / `ShutdownAsync` 相当の shell orchestration
- 子 VM プロパティ
- shared context の lifecycle
- app-level event
- View に依頼する dialog request event

### 行数目標

| 対象 | 目標 |
|---|---:|
| `MainWindowViewModel` shell | 1,000〜1,500 行以下 |
| 各子 ViewModel | 原則 300〜800 行、最大でも 1,200 行程度 |
| pure service | 原則 500 行以下 |
| 1 メソッド | 原則 80 行以下。超える場合は理由コメントを残す |

## 10. Codex 向け実装チケット

### Ticket A: partial 対応前のテスト helper

1. `BeMusicSeeker.Tests/SourceTextTestHelper.cs` を追加。
2. 次で `MainWindowViewModel.cs` / `MainWindow.cs` 直読み test を棚卸しする。
   ```powershell
   rg "File\.ReadAllText.*MainWindowViewModel\.cs|File\.ReadAllText.*MainWindow\.cs" .\BeMusicSeeker.Tests -g "*.cs"
   ```
3. `ReadMainWindowViewModelSourceText()` と `ReadMainWindowSourceText()` 経由へ変更する。
4. 少なくとも `BmsLibraryMutationBoundaryTests`、`DialogRouteConsolidationTests`、`Lr2PlayHistorySchemaUiTests`、`MainWindowContextMenuResourceTests`、`PlaylistConcurrencyArchitectureTests`、`ExplorerOpenServiceTests` の該当ケースを確認する。
5. 全体 build を実行。

完了条件:

- まだ production code を分割していない状態でテストが通る。
- 後続の `MainWindowViewModel` / `MainWindow` partial 分割だけでは source-text test が落ちない。

### Ticket B: 先頭 helper 型の移動

1. `MainWindowViewModel.cs` 先頭の helper 型を `ViewModels/MainWindow/*.cs` へ移動。
2. namespace と access modifier を維持。
3. `LibraryChartRowSortEngineTests`、`PlaylistViewPipelineTests` を実行。

完了条件:

- `MainWindowViewModel.cs` から 400 行程度減る。
- 型名解決が変わらない。

### Ticket C: SettingDialogViewModel の nested partial 移動

1. `MainWindowViewModel` と `SettingDialogViewModel` を partial 化。
2. `MainWindowViewModel.SettingDialogViewModel.cs` を作成し、既存中身を移す。
3. `SettingDialog.cs` と tests の型参照が変わらないことを確認。
4. `SettingDialogCustomFolderOutputBaseTests`、`Lr2PlayHistorySchemaUiTests` を実行。

完了条件:

- `MainWindowViewModel.SettingDialogViewModel` の型名が維持される。
- 設定画面関連テストが通る。

### Ticket D: Playlist dialog VM の nested partial 移動

1. `PlaylistSummaryBulkEditDialogViewModel` を別ファイルへ移動。
2. `PlaylistPropertyDialogViewModel` を別ファイルへ移動。
3. 関連 option/patch 型を、参照形状を壊さない範囲で近接ファイルへ移す。
4. `PlaylistSummaryBulkEditTests`、`BmsPlaylistUpdateTests` の関連ケースを実行。

完了条件:

- `MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel` と `MainWindowViewModel.PlaylistPropertyDialogViewModel` の型名が維持される。

### Ticket E: OperationProgressHubViewModel 導入

1. progress property ごとの notification / side-effect contract 表を作る。
2. `OperationProgressHubViewModel` を追加。
3. root に `public OperationProgressHubViewModel ProgressHub { get; }` を追加。
4. root の既存進捗 property は `ProgressHub` への pass-through にする。
5. root が `ProgressHub.PropertyChanged` を受けて旧 property 名と関連 property 名を raise する。
6. XAML は変更しない。
7. `MainWindowViewModelStartupProgressTests` を実行。

完了条件:

- 進捗系 state が root から削減される。
- Binding path は不変。
- `IsStartupProgressActive` などの既存 setter が持っていた副作用が維持される。

### Ticket F: PlaybackPanelViewModel 導入

1. `PlaybackPanelViewModel` を追加。
2. 再生パネル用 property を child に移し、root pass-through を残す。
3. `IBMSPlayer` 操作は最初 root 経由でもよいが、子 VM の公開 API は UI 型に依存させない。
4. 上部パネルを手動 smoke test する。

完了条件:

- 再生状態の表示更新が child VM で完結し始める。

### Ticket G: MainViewRefreshDecisionService 抽出

1. pure method を service へ移す。
2. root の `ForTest` method は service 呼び出しの forwarder にする。
3. `LibraryChartRowSortEngineTests` を service 直接検証へ寄せる。

完了条件:

- メイン一覧更新判断の pure ロジックが root から独立する。
- Ticket I の MainChartListViewModel 導入前提ができる。

### Ticket H: PlaylistRequestFactory 抽出

1. `NormalizePlaylistFolderName`、`NormalizePlaylistKeywordFilter`、`CreatePlaylistRequestIdentity` を service/factory へ移す。
2. root forwarder を残す。
3. `PlaylistViewPipelineTests` を更新。

完了条件:

- プレイリスト request identity のテストが root なしで書ける。

### Ticket I-1: MainChartListViewModel 導入前の境界確認

1. Ticket G が完了済みであることを確認する。未完了なら先に Ticket G を実施する。
2. `ChartRowsView`、`SelectedIndexChartRowsView`、sort/filter/summary property の setter 副作用を棚卸しする。
3. XAML / code-behind / tests から root-level main chart API を参照している箇所を分類する。
4. Ticket I-2 で最初に child 所有へ移す state group を決める。

完了条件:

- Ticket I-2 の実装範囲と維持すべき通知・副作用が明確になっている。

### Ticket I-2: MainChartListViewModel の state pass-through 化

1. `MainChartListViewModel` を追加。
2. `ChartRowsView`、`SelectedIndexChartRowsView`、sort/filter/summary state のうち副作用が小さいものから child に移す。
3. root pass-through を残し、Binding path は変えない。
4. `ChartListVirtualViewTests`、`MainColumnSettingModeTests`、`CustomTableColumnSettingsTests` を実行する。

完了条件:

- メイン一覧 state の所有者が child VM に移り始める。
- XAML / code-behind から見える root-level API は維持される。

### Ticket I-3: RefreshChartRowsView facade/coordinator 化

1. `RefreshChartRowsView` の orchestration を `MainChartListViewModel` / `ChartListRefreshCoordinator` 側へ段階的に移す。
2. root は facade として既存呼び出し形状を維持する。
3. 通常ライブラリ、仮想サブセット、プレイ履歴、プレイリスト選択の各 source identity / cache invalidation を分けて確認する。
4. `ChartListVirtualViewTests`、`LibraryChartRowSortEngineTests`、`PlaylistViewPipelineTests` と手動一覧 smoke test を実行する。

完了条件:

- `RefreshChartRowsView` の主要 workflow が child/coordinator に移る。
- sort/filter/virtualization/cache の体感挙動が変わらない。

### Ticket I-4: MainChartList 関連 test 移行

`Ticket I-4` は test 移行の範囲が広いため、以下へ分割する。

#### Ticket I-4a: main chart list test dependency 棚卸し

1. `MainChartList` / `RefreshChartRowsView` / `ChartListRefreshCoordinator` / `RegularChartList*` に関係する source-text / reflection / root forwarder test を一覧化する。
2. 各 test を「coordinator/service 直接検証へ移せる」「root integration として残す」「後続 XAML/DataContext 移行まで保留」に分類する。
3. 分類結果をこの計画書の I-4 表へ残す。

確認対象:

- `rg` による test 棚卸し
- 変更が資料のみなら `dotnet format whitespace --verify-no-changes`

完了条件:

- I-4 の移行対象と保留対象が前提知識なしに読める。

#### Ticket I-4b: source-text helper の main chart list 範囲を明確化

1. Main chart list 関連の source-text test で、`MainWindowViewModel` 全体や広すぎる `ExtractBetween` に依存している箇所を method/helper 単位の検証へ狭める。
2. 既に抽出済みの `ChartListRefreshCoordinator` / `RegularChartList*` で直接検証できるものはそちらへ寄せる。
3. root facade の dispatch 契約だけは root method body で確認する。

確認対象:

- `RegularChartListRefreshTypesTests`
- `MainWindowContextMenuResourceTests`
- `ChartListVirtualViewTests`

完了条件:

- source-text test が facade 本体と helper/coordinator 本体を取り違えない。

#### Ticket I-4c: root forwarder / reflection test の直接化

1. `MainWindowViewModel` の互換 forwarder 経由で child VM / coordinator を確認している test を、可能なものから直接 test へ移す。
2. root integration として残す必要がある test は理由を I-4 表へ残す。
3. production API 削除候補は、XAML / code-behind 参照が外れた後の Ticket P/Q へ送る。

完了条件:

- メイン一覧の主要テストが root VM の巨大 class 形状に依存しない。

#### I-4 test dependency inventory

| test / file | 現在の依存 | 分類 | 移行方針 |
|---|---|---|---|
| `RegularChartListRefreshTypesTests` | `MainWindowViewModel` source-text | coordinator/service 直接検証 + root facade dispatch | root は dispatch 契約のみ。regular pipeline 接続は `ApplyMainLibraryChartListView` / `RegularChartList*` へ範囲を狭める |
| `MainWindowContextMenuResourceTests.DuplicateFilterViewUsesChartFileParameters` | `MainWindowViewModel` source-text | helper 範囲明確化 | duplicate/subset の実体は `ApplyMainLibraryChartListView` / virtual helper の method body を確認する |
| `ChartListVirtualViewTests.VirtualChartSubsetUnsupportedSort_ResetsToDefaultVirtualSort` | private `RefreshChartRowsView` reflection + root state setup | root integration として残す | virtual subset route、sort reset、bound rows までを横断するため、coordinator 直接化は追加 test で補完しつつ当面維持 |
| `ChartListVirtualViewTests.SummaryFormatter_OmitsUnknownFolderCount` | root static forwarder | child VM / formatter 直接検証 | `MainChartListViewModel` または summary formatter へ寄せ、root wrapper は削除候補にする |
| `ChartListVirtualViewTests.MainChartList_*` | root forwarder behavior | child VM 直接検証 + root relay smoke | `MainChartListViewModel` 直接 test を厚くし、root は relay 互換だけ確認する |
| `ChartListVirtualViewTests.VirtualNormalLibraryRequestModes_*` | root forwarder behavior | coordinator / decision service 直接検証 | support matrix は `ChartListRefreshCoordinator` または小さな decision service へ寄せる |
| `ChartListVirtualViewTests.VirtualChartSubsetRequestModes_*` | root forwarder behavior | coordinator / decision service 直接検証 | subset request mode の support matrix は root wrapper から外す |
| `ChartListVirtualViewTests.DuplicateVirtualSourceRows_PreserveBmsonStorageRowsWithoutCompatibilityFiles` | root forwarder behavior | 保留 | duplicate virtual source-row builder 抽出後に直接化する |
| `BmsLibraryMutationBoundaryTests.MainWindowViewModel_UsesChartPackageMutationBoundary` | `MainWindowViewModel` source-text | main chart list 対象外寄り | chart package mutation 境界のため I-4 では保留。後続 Shell 化監査で再分類 |
| `RegularChartListFilterServiceTests` / `RegularChartListSortCoordinatorTests` | direct service/coordinator | 移行不要 | 既に root class 形状に依存しない |
| `ChartListRefreshCoordinator_Apply*` / `Create*Metrics*` tests | direct coordinator | 整理候補 | 必要なら `ChartListRefreshCoordinatorTests` へファイル分離する |

### Ticket J: XAML の playback/progress DataContext 移行

`Ticket J` は XAML binding の blast radius が大きいため、以下へ分割する。

#### Ticket J-1: playback header DataContext 移行

1. 上部再生パネルの header 表示領域だけを `PlaybackPanel` DataContext へ移す。
2. `NowPlayingBMS`、`CurrentlyPlayingTime`、player host / command handler など root 所有の binding / event handler は移さない。
3. root pass-through はまだ削除しない。

確認対象:

- `dotnet build` で XAML compile
- `PlaylistViewPipelineTests`
- `MainWindowContextMenuResourceTests` の playback/header 周辺 source-text checks

完了条件:

- `PlayerHeaderTitle` / `PlayerHeaderSubtitle` / `PlayerHeaderArtist` の XAML binding が `PlaybackPanel` を DataContext にして解決される。

#### Ticket J-2: progress status bar DataContext 移行

1. status bar の進捗表示領域を `ProgressHub` DataContext へ移す。
2. retry/cancel/cleanup click handler は root/code-behind のまま維持する。
3. root pass-through はまだ削除しない。

確認対象:

- `dotnet build` で XAML compile
- `Lr2SongDbSyncStatusServiceTests`
- `MainWindowContextMenuResourceTests` の progress/status bar source-text checks

完了条件:

- startup / install / LR2 song DB sync status の表示 binding が `ProgressHub` を DataContext にして解決される。

#### Ticket J-3: DataContext 移行後の binding 棚卸し

1. playback/progress 領域に残った root binding と code-behind event handler を一覧化する。
2. root pass-through 削除候補を Ticket P/Q へ送る。
3. 手動 smoke test 項目を更新する。

完了条件:

- もっとも独立性の高い 2 領域が root Binding から外れる。
- runtime binding error が増えていないことを debug output または手動確認で確認する。

### Ticket K: MainWindowRuntimeContext 導入

1. `MainWindowRuntimeContext` を追加し、子 VM / coordinator が必要とする横断依存を明示する。
2. 初期段階では `BMSLibrary`、`BMSPlaylist`、`LR2Config`、`IBMSPlayer`、dispatcher、dialog request、logging callback をすべて移さず、read-only provider / callback interface から始める。
3. root から child VM へ model 参照を直接ばらまいている箇所を棚卸しする。
4. runtime context は意味の変わる fallback を持たず、未初期化なら明示的に失敗させる。

確認対象:

- `MainWindowViewModelStartupProgressTests`
- `ChartListVirtualViewTests`
- `PlaylistViewPipelineTests`
- `PlayHistoryReadModelTests`

完了条件:

- child VM / service へ渡す横断依存の置き場ができる。
- root の constructor / initialize が composition root として読めるようになる。

### Ticket L: PlaylistWorkspaceViewModel 導入

#### Ticket L-1: detail / summary state contract 棚卸し

1. playlist detail と playlist summary の root property、field、command、dialog 生成、progress 表示を分類する。
2. `PlaylistViewState`、`PlaylistBuildRequest`、`PlaylistSummaryView`、`PlaylistSummaryColumnsSettings`、bulk edit / property dialog の所有境界を決める。
3. XAML / code-behind / tests からの root API 参照を分類する。

確認対象:

- `PlaylistViewPipelineTests`
- `PlaylistSummaryAggregationTests`
- `PlaylistSummaryBulkEditTests`
- `MainWindowContextMenuResourceTests`

完了条件:

- `PlaylistWorkspaceViewModel` が持つ state と root facade に残す API が明確になる。

#### Ticket L-2: PlaylistWorkspaceViewModel state pass-through

1. `PlaylistWorkspaceViewModel` を追加する。
2. playlist detail rows、summary rows、summary mode、summary columns、summary sort/filter/owned filter、dialog state のうち副作用が小さいものから child に移す。
3. root の既存 property は pass-through と property relay を維持する。
4. XAML はまだ変更しない。

確認対象:

- `PlaylistViewPipelineTests`
- `PlaylistSummaryAggregationTests`
- `PlaylistSummaryBulkEditTests`
- `BmsPlaylistUpdateTests`

完了条件:

- playlist detail / summary の表示 state が root から child VM へ移り始める。
- root Binding path は維持される。

#### Ticket L-3: Playlist detail build coordinator 化

1. `RegisterPlaylistSourceBuildRequest`、`CreatePlaylistBuildRequest`、`ApplyPlaylistViewRequest`、`RebuildPlaylistSource`、`ApplyPlaylistViewWithoutSourceRebuild` の境界を `PlaylistOpenCoordinator` / `PlaylistDetailViewModel` へ移す。
2. `PlaylistRequestFactory` を coordinator から直接使う。
3. source identity / presentation identity / chart info patch の stale 判定を root なしで検証できるようにする。

確認対象:

- `PlaylistViewPipelineTests`
- `PlaylistReloadMergeTests`
- `BmsPlaylistUpdateTests`
- full `dotnet test`

完了条件:

- playlist detail build workflow が root facade から独立する。
- root は playlist tree 選択と coordinator 呼び出しに寄る。

#### Ticket L-4: Playlist summary presentation service 化

1. `BuildPlaylistSummaryPresentationRows`、`ApplyPlaylistSummaryFilters`、`CalculatePlaylistSummaryCounts`、owned filter、sort を `PlaylistSummaryPresentationService` へ移す。
2. `PlaylistSummaryDataRefreshDecision` と rows cache の責務を `PlaylistSummaryViewModel` / service へ寄せる。
3. summary table count cache は callback または専用 cache 型に分ける。

確認対象:

- `PlaylistSummaryAggregationTests`
- `PlaylistSummaryBulkEditTests`
- `PlaylistViewPipelineTests`

完了条件:

- playlist summary の pure presentation が root を new せず検証できる。
- summary rebuild / presentation refresh の stale guard が維持される。

#### Ticket L-5: Playlist mutation / external sync の分割方針

playlist 更新系は副作用と確認範囲が広いため、1 ticket では実装しない。次の sub-ticket に分け、各 ticket で `BMSPlaylist` / `BMSLibrary` mutation を runtime context 経由へ寄せる。

##### Ticket L-5a: playlist summary bulk mutation service 化

1. summary bulk edit の入力、対象 row selection、patch 生成、commit 結果を structured request/result にする。
2. dialog VM は edit state、mutation service は patch 適用と結果分類を担当する。
3. progress 表示が必要な場合は `ProgressHub` / `PlaylistWorkspace` のどちらが所有するかを明記する。

確認対象:

- `PlaylistSummaryBulkEditTests`
- `PlaylistSummaryAggregationTests`
- `BmsPlaylistUpdateTests`

完了条件:

- summary bulk mutation が root workflow から外れる。
- bulk edit の成功/失敗/部分更新結果が service 単体で検証できる。

##### Ticket L-5b: BMT sort / playlist order coordinator 化

1. BMT sort と playlist order 更新の入力、対象 playlist、sort key、更新結果を coordinator request/result にする。
2. playlist detail / summary の stale guard と reload 判定を coordinator から返す。

確認対象:

- `BmsPlaylistUpdateTests`
- `PlaylistViewPipelineTests`

完了条件:

- BMT sort と playlist order 更新が root から独立する。

##### Ticket L-5c: custom folder output coordinator 化

1. custom folder output の plan 作成、materialize、repair、post-refresh を coordinator に分ける。
2. file system mutation と playlist/header mutation の境界を明示する。
3. 失敗時は意味の変わる fallback を入れず、structured error として root / dialog へ返す。

確認対象:

- `BmsPlaylistUpdateTests`
- `SettingDialogCustomFolderOutputBaseTests`
- full `dotnet test`（file system mutation 周辺の影響確認）

完了条件:

- custom folder output workflow が root から外れる。
- file system mutation の失敗経路が隠蔽されない。

##### Ticket L-5d: external playlist sync coordinator 化

1. external playlist sync の request、download/parse/update、progress、cancellation、stale guard を coordinator に分ける。
2. `ProgressHub` と `PlaylistWorkspace` の progress 所有境界を明記する。
3. sync 完了後の playlist detail / summary refresh request を structured result として返す。

確認対象:

- `PlaylistReloadMergeTests`
- `PlaylistViewPipelineTests`
- full `dotnet test`

完了条件:

- external sync workflow が root facade から独立する。
- cancellation / stale request の扱いが coordinator 単体で追える。

##### Ticket L-5e: beatoraja table URL import coordinator 化

1. beatoraja Table URL import の URL 正規化、取得、playlist mutation、UI 表示結果を coordinator request/result にする。
2. user-facing text を触る場合は resource / lang JSON parity を同じ ticket で更新する。

確認対象:

- `BmsPlaylistUpdateTests`
- `LocalizationResourceParityTests`
- 手動 smoke: beatoraja Table URL import

完了条件:

- beatoraja Table URL import が root から外れる。
- playlist 更新と UI 通知の境界が明確になる。

### Ticket M: PlayHistoryWorkspaceViewModel 導入

#### Ticket M-1: play history state / provider contract 棚卸し

1. LR2 / beatoraja provider、period request、keyword queued refresh、display target、summary card、sort parameter の所有者を分類する。
2. `MainTableSortParameters` が通常一覧 sort と play history sort を合成している現状を分離する方針を決める。
3. reflection / source-text tests を棚卸しする。

確認対象:

- `PlayHistoryReadModelTests`
- `Lr2PlayHistorySchemaUiTests`
- `MainWindowContextMenuResourceTests`

完了条件:

- `PlayHistoryWorkspaceViewModel` が持つ state と root facade に残す API が明確になる。

#### Ticket M-2: PlayHistoryPresentationService 抽出

1. read result から `PlayHistoryRow`、summary card、diagnostic summary、grid summary text を作る pure 部分を service へ移す。
2. keyword / mode / sort / display target 適用を service へ寄せる。
3. LR2 / beatoraja DB read 自体はまだ root または provider に残してよい。

確認対象:

- `PlayHistoryReadModelTests`
- `GridKeywordSearchQueryTests`

完了条件:

- play history 表示行構築が root なしで検証できる。

#### Ticket M-3: ApplyPlayHistoryView coordinator 化

1. `ApplyPlayHistoryView` を request validation、read provider、presentation apply、logging の小さな段階に分ける。
2. stale request / cancellation / schema status の扱いを `PlayHistoryViewCoordinator` へ移す。
3. root は facade と tree selection / operation section の relay に寄せる。

確認対象:

- `PlayHistoryReadModelTests`
- `Lr2PlayHistorySchemaUiTests`
- full `dotnet test`

完了条件:

- `ApplyPlayHistoryView` が root の巨大 workflow でなくなる。
- play history sort/filter と main chart list sort/filter の state が分離される。

### Ticket N: Settings service 化

#### Ticket N-1: AppSettingsSnapshot / AppSettingsStore 導入

1. P1-03 と整合する `AppSettingsSnapshot` / `AppSettingsStore` を追加する。
2. `SettingDialogViewModel` の backup / restore / diff 判定を store 経由に寄せる。
3. `Settings.Default` 直参照を増やさず、変更箇所を一覧化する。

確認対象:

- `SettingDialogCustomFolderOutputBaseTests`
- `Lr2PlayHistorySchemaUiTests`

完了条件:

- settings backup / restore / diff が service 化される。

#### Ticket N-2: SettingsValidationService 抽出

1. `CheckCurrentRequiredSettingsForSave`、`CheckValidation`、path / player / LR2 / beatoraja validation を service へ移す。
2. UI 表示に必要な validation result を structured result として返す。
3. UI 文言追加が必要なら resource 更新と parity test を同じ ticket に含める。

確認対象:

- `SettingDialogCustomFolderOutputBaseTests`
- `LocalizationResourceParityTests`

完了条件:

- 設定検証が root nested VM の巨大 method に依存しない。

#### Ticket N-3: SettingsPostSaveCoordinator 抽出

1. `necessaryStepsAfterSaved`、保存後の library reload / LR2 DB sync / play history schema / player 設定反映を coordinator へ移す。
2. `MainWindowRuntimeContext` 経由で横断副作用を呼び、意味の変わる fallback は入れない。
3. 保存後に必要な UI refresh / restart 判定を structured result 化する。

確認対象:

- `SettingDialogCustomFolderOutputBaseTests`
- `MainWindowViewModelAppSchemaRepairTests`
- `Lr2PlayHistorySchemaUiTests`

完了条件:

- `MainWindowViewModel.SettingDialogViewModel.cs` が UI edit state 中心になり、6,000 行規模から段階的に縮小する。

### Ticket O: SidebarNavigationViewModel 導入

#### Ticket O-1: tree state / event boundary 棚卸し

1. library tree、playlist tree、install tree、maintenance tree、play history archive tree の ItemsSource / selected item / context menu / drag-drop 参照を分類する。
2. code-behind handlers が root のどの method/property を呼ぶか一覧化する。
3. `MainWindow.cs` から root 内部 state へ直接アクセスしている箇所を分類する。

確認対象:

- `MainWindowContextMenuResourceTests`
- `BmsLibraryMutationBoundaryTests`
- `DialogRouteConsolidationTests`

完了条件:

- sidebar の child VM 分割順が決まる。

#### Ticket O-2: SidebarNavigationViewModel state pass-through

1. `SidebarNavigationViewModel` を追加し、tree root nodes / expanded state / selected request を child に移す。
2. root pass-through と event relay を維持し、XAML はまだ root Binding のままにする。
3. tree selection は command 化の前に facade method として残す。

確認対象:

- `MainWindowContextMenuResourceTests`
- `BmsLibraryMutationBoundaryTests`
- `PlaylistViewPipelineTests`

完了条件:

- left sidebar state の所有者が root から child VM へ移り始める。

#### Ticket O-3: tree command bridge / DataContext 移行

1. tree 領域の DataContext を `Sidebar` 配下へ移す。
2. code-behind event は root 直呼びではなく command bridge / request object を経由する。
3. ContextMenu の `PlacementTarget.DataContext` / root command 参照を明示する。

確認対象:

- `MainWindowContextMenuResourceTests`
- `dotnet build` の XAML compile
- 手動 smoke: library / playlist / maintenance / install / play history tree selection と context menu

完了条件:

- left sidebar の主要 Binding が root から外れる。

### Ticket P: XAML DataContext 移行の完了

#### Ticket P-1: MainChartList DataContext 移行

1. main table / keyword filter / mode filter / grid header / summary の DataContext を `MainChartList` または明示した child へ移す。
2. `MainTableSortParameters` は play history と通常一覧の合成を解消してから移す。
3. ContextMenu と `StaticResource vm` 経由の column settings 参照は BindingProxy / placement target 経由へ移す。

確認対象:

- `dotnet build`
- `ChartListVirtualViewTests`
- `MainWindowContextMenuResourceTests`
- 手動 smoke: sort / keyword / mode / column menu / row activation

完了条件:

- main chart list の主要 Binding が root から外れる。

#### Ticket P-2: PlaylistWorkspace DataContext 移行

1. playlist detail / summary table、summary filter、bulk edit entry point の DataContext を `PlaylistWorkspace` へ移す。
2. code-behind の playlist summary handlers を command bridge へ寄せる。
3. dialog open は root 直 new ではなく request / command 経由にする。

確認対象:

- `dotnet build`
- `PlaylistSummaryAggregationTests`
- `PlaylistSummaryBulkEditTests`
- `MainWindowContextMenuResourceTests`
- 手動 smoke: summary filter / sort / bulk edit / property dialog

完了条件:

- playlist detail / summary の主要 Binding が root から外れる。

#### Ticket P-3: PlayHistory DataContext 移行

1. play history summary card / filter / table state を `PlayHistoryWorkspace` へ移す。
2. main table と共有している Binding は `MainChartList` か `PlayHistoryWorkspace` のどちらが所有するか明示する。
3. root の `MainTableSortParameters` 合成 forwarder を削除候補にする。

確認対象:

- `dotnet build`
- `PlayHistoryReadModelTests`
- `Lr2PlayHistorySchemaUiTests`
- 手動 smoke: play history period selection / sort / keyword / display target

完了条件:

- play history の主要 Binding が root から外れる。

#### Ticket P-4: root pass-through 削除

1. XAML / code-behind / tests が child VM を直接参照できるようになった property から root pass-through を削除する。
2. 削除前に `rg` で production 参照と test 参照を分類する。
3. test-only forwarder は service 直接検証へ置き換える。

確認対象:

- full `dotnet test`
- `dotnet build`
- `dotnet format whitespace`
- `dotnet roslynator analyze`

完了条件:

- root Binding は shell-level property に限定される。
- `MainWindowViewModel.cs` が Gate 3 の 3,000 行以下に近づく。

### Ticket Q: Shell 化仕上げ

#### Ticket Q-1: root field / nested type 整理

1. `MainWindowViewModel.cs` に残る nested type / field を owner 別に分類する。
2. child VM / service / runtime context に移ったものは削除または移動する。
3. field initializer 順序依存があるものは constructor 初期化へ明示移行する。

確認対象:

- full `dotnet test`
- source-text tests

完了条件:

- root に残る field は lifecycle / composition / shared context に限られる。

#### Ticket Q-2: dialog request / command boundary 整理

1. `MainWindow.cs` から ViewModel 内部型を直接 new している箇所を request / command 経由にする。
2. View 固有の `Window`, `Control`, `Panel`, `ContextMenu`, `TreeViewItem`, `DragEventArgs` は View / adapter に閉じる。
3. `async void` は WPF event entry point のみに残す。

確認対象:

- `DialogRouteConsolidationTests`
- `ExplorerOpenServiceTests`
- `MainWindowContextMenuResourceTests`
- 手動 smoke: dialog open / close / context menu actions

完了条件:

- `MainWindowViewModel` は dialog 実体ではなく dialog request を発行する。
- `MainWindow.cs` と root VM の相互依存が薄くなる。

#### Ticket Q-3: final shell audit

1. `MainWindowViewModel.cs` の行数、public/root Binding、child VM property、service forwarder を一覧化する。
2. 1,000〜1,500 行を超える場合は、残る責務ごとに追加 ticket を切る。
3. 手動 smoke test を全項目実施する。
4. P0-01 の完了条件に対するチェックリストをこの計画書へ追記する。

確認対象:

- 標準確認手順すべて
- 手動 smoke test 全項目
- source-text / reflection test の残存理由確認

完了条件:

- `MainWindowViewModel` が Shell ViewModel として 1,000〜1,500 行以下、または超過分が shell 責務として説明可能。
- root Binding が shell-level に限定される。
- P0-02 / P0-03 へ制約なく本格移行できる。なお、P0-01 Gate 2 完了後は P0-02 / P0-03 の限定的な並行着手を再判断してよい。

## 11. テスト方針

### 自動テスト

分割/抽出対象ごとの優先テスト:

| 対象 | テスト |
|---|---|
| 起動進捗 | `MainWindowViewModelStartupProgressTests` |
| App schema repair | `MainWindowViewModelAppSchemaRepairTests` |
| メイン一覧/仮想化 | `ChartListVirtualViewTests`, `LibraryChartRowSortEngineTests`, `MainColumnSettingModeTests` |
| カラム設定 | `CustomTableColumnSettingsTests`, `MainColumnSettingModeTests` |
| プレイリスト詳細 | `PlaylistViewPipelineTests`, `PlaylistReloadMergeTests`, `BmsPlaylistUpdateTests` |
| プレイリストサマリ | `PlaylistSummaryAggregationTests`, `PlaylistSummaryBulkEditTests` |
| プレイ履歴 | `PlayHistoryReadModelTests`, `Lr2PlayHistorySchemaUiTests` |
| 設定画面 | `SettingDialogCustomFolderOutputBaseTests`, `Lr2PlayHistorySchemaUiTests` |
| 多言語 | `LocalizationResourceParityTests` |
| UI ソース検査 | `MainWindowContextMenuResourceTests` |
| install/drop/progress | `DropInstallQueueProcessorTests`, `PendingInstallEstimateQueueProcessorTests`, `BmsLibraryInstallEstimationServiceTests` |

### 手動 smoke test

各 Phase 終了時に最低限確認する。

1. アプリ起動。初期化進捗が止まらず、メイン一覧が表示される。
2. 左上ツリーでライブラリ/プレイリストを選択し、メイン一覧が切り替わる。
3. 左下ツリーでメンテナンス/インストール/プレイログ系を選択する。
4. メイン一覧の sort、keyword filter、mode filter を操作する。
5. プレイリストサマリ表示、一括編集、プロパティ編集を開く。
6. 設定画面を開き、保存せず閉じる/保存する/リセットする。
7. 再生パネルで再生、停止、次、前、音量変更を試す。
8. インストール推定/再スキャン/フォルダリネームなど進捗表示が出る操作を 1 つ実行する。
9. 終了時に shutdown 例外が出ない。

## 12. リスクと対策

| リスク | 対策 |
|---|---|
| partial 分割で source text test が壊れる | Phase 0 でソース連結 helper を入れる |
| nested type を top-level 化して参照が壊れる | 初期は nested partial のまま移す |
| partial field initializer 順序が変わる | field は順序維持ファイルに残す。依存がある場合は constructor 初期化へ明示移行する |
| XAML Binding の DataContext 切替で ContextMenu が壊れる | ContextMenu は `PlacementTarget.DataContext` / BindingProxy を明示的に使う |
| `PropertyChanged` の通知漏れ | child VM property changed を root が旧名で relay するテストを追加し、setter 副作用 contract を Ticket E で棚卸しする |
| Dispatcher thread 問題 | ObservableCollection 更新は既存同様 UI dispatcher に限定し、service は snapshot を返す |
| 既存 private reflection テストが壊れる | 移動初期は member name を変えない。rename は behavior test 化後に行う |
| 性能劣化 | `InstallPerformance.MainWindowViewModel` など既存ログ形式を維持し、一覧 build/prewarm の elapsed を比較する |
| 一括抽出でレビュー不能になる | 1 ticket = 1 責務。build/test できる状態を保つ |

## 13. 完了定義

### Gate 完了

P0-01 は一度に完了判定しない。次の Gate を順に満たした状態で、次の ticket 群へ進む。

| Gate | 完了条件 |
|---|---|
| Gate 1 | main chart / playlist / play history の主要 workflow が child VM / coordinator / service へ移り、`MainWindowViewModel.cs` が 15,000 行以下になる。 |
| Gate 2 | settings validation/save と sidebar tree/navigation の詳細が root から外れ、`MainWindowViewModel.cs` が 8,000 行以下になる。 |
| Gate 3 | XAML / code-behind / tests が child VM or service を直接参照できるようになり、root pass-through の大半を削除できる。`MainWindowViewModel.cs` は 3,000 行以下を目安にする。 |
| Gate 4 | root は lifecycle、composition、shared runtime context、dialog request、shell-level command に限定され、`MainWindowViewModel.cs` は 1,000〜1,500 行以下、または超過分が shell 責務として説明可能になる。 |

各 Gate の共通条件:

- source text test は分割後のファイル構成に追従済み。
- root forwarder 依存のテストは、抽出済み service / child VM 直接検証へ移行済み、または残す理由が計画書に明記済み。
- 標準確認手順が通る。通らない場合は既存失敗 / 新規失敗を分類し、次 ticket へ持ち越す理由を明記する。
- 手動 smoke が必要な ticket では、確認項目と結果をこの計画書の進捗へ追記する。

### 最終完了

- `MainWindowViewModel` は Shell ViewModel として 1,000〜1,500 行以下、または超過分が shell 責務として説明可能。
- 上部再生パネル、左ツリー、メイン一覧、進捗、設定、プレイリストはそれぞれ所有 ViewModel を持つ。
- root Binding は shell-level に限定される。
- pure logic は service/reducer へ移り、root を new しなくても単体テストできる。
- `MainWindow.cs` から ViewModel 内部型への直接 new が減り、dialog request / command 経由になる。
- 既存機能の挙動差分なし。
