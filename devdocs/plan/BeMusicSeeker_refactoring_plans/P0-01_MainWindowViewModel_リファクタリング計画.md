> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 0. 目的

`BeMusicSeeker/ViewModels/MainWindowViewModel.cs` は現在 33,379 行あり、UI ルート ViewModel、設定画面 ViewModel、プレイリスト編集ダイアログ、チャート一覧生成、プレイリスト一覧/詳細、起動進捗、インストール進捗、プレイ履歴、play history display target、再生制御、外部同期、beatoraja Table URL import、スコアビューア連携などが 1 ファイルに集約されている。

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
| 2026-07-06 | 完了 | Ticket G: MainViewRefreshDecisionService 抽出 | このコミット |

### Ticket F 手動確認結果

- `PlaybackPanelViewModel` 導入、自動確認、上部再生パネルの手動 smoke test（再生/停止/次/前/音量/ヘッダー表示）は完了。

### Ticket G 抽出範囲

- `MainViewRefreshDecisionService` が main-view data dependency 変更時の `MainViewRefreshDecision`、sort column dependency、通常ライブラリ sort-key invalidation reason、virtual normal-library mode support、regular folder stage rebuild 判定を所有する。
- `MainWindowViewModel` の既存 `ForTest` method は互換 forwarder として残す。
- `LibraryChartRowSortEngineTests` の main-view refresh decision / dependency 判定は service 直接検証へ寄せる。

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

| 対象 | 行数/範囲 | 備考 |
|---|---:|---|
| `MainWindowViewModel.cs` 全体 | 33,379 行 | ルート ViewModel と多数の補助型が同居 |
| `MainWindowViewModel` 本体 | 約 32,864 行 | 516 行目付近から末尾まで |
| ネストされた `SettingDialogViewModel` | 約 6,516 行 | 設定値の一時保持、検証、保存後処理、LR2/Beatoraja/プレイ履歴/エンコード/プレイヤー設定を保持 |
| `PlaylistSummaryBulkEditDialogViewModel` | 約 395 行 | プレイリストサマリ一括編集 |
| `PlaylistPropertyDialogViewModel` | 約 908 行 | プレイリストプロパティ編集 |
| `MainWindow.xaml` | 約 2,510 行 | ルート VM への Binding が多い |
| `MainWindow.cs` | 約 10,289 行 | UI 操作、ダイアログ、ドラッグ&ドロップ、右クリックなどが残る |

### MainWindowViewModel 内の大きなメソッド例

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

1. Main chart list 関連の source-text / reflection test を棚卸しする。
2. root forwarder 経由の test を、抽出した service / child VM を直接検証する形へ寄せる。
3. 互換 forwarder は production code から不要になった段階で削除候補にする。

完了条件:

- メイン一覧の主要テストが root VM の巨大 class 形状に依存しない。

### Ticket J: XAML の playback/progress DataContext 移行

1. 上部再生パネルの DataContext を `PlaybackPanel` にする。
2. 進捗表示領域の DataContext を `ProgressHub` にする。
3. root pass-through はまだ削除しない。
4. `dotnet build` で XAML compile を確認。
5. 対象領域の binding path 棚卸しを行い、`ContextMenu` / `StaticResource vm` / code-behind からの root 参照を分類する。
6. 手動 smoke test では起動進捗、install 進捗、LR2 song DB sync status、再生パネル操作を確認する。

完了条件:

- もっとも独立性の高い 2 領域が root Binding から外れる。
- runtime binding error が増えていないことを debug output または手動確認で確認する。

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

### 中間完了

- `MainWindowViewModel.cs` 単体が 2,000 行以下になる。
- ただし partial 全体の総行数はまだ大きくてもよい。
- 設定画面 VM とプレイリストダイアログ VM は別ファイル化済み。
- source text test は partial 対応済み。
- 全体 build/test が通る。

### 最終完了

- `MainWindowViewModel` は Shell ViewModel として 1,000〜1,500 行以下。
- 上部再生パネル、左ツリー、メイン一覧、進捗、設定、プレイリストはそれぞれ所有 ViewModel を持つ。
- root Binding は shell-level に限定される。
- pure logic は service/reducer へ移り、root を new しなくても単体テストできる。
- `MainWindow.cs` から ViewModel 内部型への直接 new が減り、dialog request / command 経由になる。
- 既存機能の挙動差分なし。
