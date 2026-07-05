# P0-02 BMSLibrary ドメイン facade 化計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 目的

`BeMusicSeeker/Models/BMSLibrary.cs` は現在 21,696 行あり、BeMusicSeeker の中核ドメイン操作が集中している。`MainWindowViewModel` の整理後も `BMSLibrary` が God Object のままだと、機能追加、起動/スキャン性能改善、LR2 / beatoraja 互換、.NET 10 移行のたびに広範囲の変更が必要になる。

この計画では `BMSLibrary` を互換 API を持つ facade として残しつつ、内部を state / coordinator / service へ分離する。

## 現状観測

| 項目 | 観測 |
|---|---:|
| `BMSLibrary.cs` 行数 | 21,696 行 |
| ロック / state / version field 群 | 1,100 行目付近から大量に集中 |
| 既存 internal service 群 | `BeMusicSeeker/Models/BmsLibraryInternal/` に多数存在 |
| `Settings.Default` 直接参照 | production 内で少なくとも `BMSLibrary.cs` 25 箇所、関連 model ではさらに多い |
| 大きい workflow 例 | `CreateLr2SongDbSyncInput`, `RunLr2SongDbSync`, `_initialize`, `ApplyLibraryFileScanDiff`, `InstallPendingPackagesToEstimatedDestinations`, `InstallChartPackagesAuto`, `MergeChartDirectory` |

既に `BmsLibraryInternal` には次のような service が存在するため、完全な新規設計ではなく「facade に残った orchestration と state mutation をさらに外へ逃がす」方針にする。

- `BmsLibraryInitializationService`
- `BmsLibraryDbGateway`
- `BmsLibraryPackageInstallService`
- `BmsLibraryInstallEstimationService`
- `BmsLibraryMaintenanceService`
- `BmsLibraryPlaylistReferenceService`
- `BmsLibraryStateApplier`
- `Lr2SongDbSyncService`
- `Lr2FolderFileDbSyncService`
- `ChartInfoBuildService`
- `DirectoryResourceLookupCache`

## 目標アーキテクチャ

```text
BMSLibrary                         // public facade / compatibility API
├─ BmsLibraryRuntimeState           // owned charts, pending packages, score snapshot, versions, lock-owned data
├─ BmsLibraryStateMutationCoordinator
│  ├─ OwnedChartCollectionMutator
│  ├─ ResourceHealthIndexCoordinator
│  ├─ InstalledDirectoryIndexCoordinator
│  └─ NormalLibraryRefreshPublisher
├─ LibraryInitializationCoordinator
│  └─ BmsLibraryInitializationService
├─ Lr2SongDbSyncCoordinator
│  ├─ Lr2SongDbSyncInputBuilder
│  ├─ Lr2SongDbSyncSurfaceCache
│  └─ Lr2SongDbSyncService
├─ ChartInfoCoordinator
│  ├─ ChartInfoHydrationCoordinator
│  ├─ ChartInfoBackfillCoordinator
│  └─ ChartInfoIndexStore
├─ PackageInstallWorkspace
│  ├─ PendingInstallEstimationCoordinator
│  ├─ PackageInstallCoordinator
│  └─ PendingPackageMutationService
├─ MaintenanceWorkspace
│  ├─ MaintenanceHydrationCoordinator
│  └─ ResourceMaintenanceWorkflowCoordinator
├─ PlaylistReferenceCoordinator
├─ ScoreAndIrCoordinator
├─ LibraryFileOperationCoordinator
└─ FolderRenameCoordinator
```

### `BMSLibrary` に残すもの

- public / internal 互換 API の入口。
- constructor と依存 service の組み立て。
- 既存 event / property changed の public surface。
- 複数 service にまたがる最小限の orchestration。
- 互換性維持のための adapter / forwarder。ただし production code が使わなくなったものは後で削除する。

### `BMSLibrary` から出すもの

- LR2 song.db sync input 構築、予約、進捗、キャンセル、status mapping。
- chart_info hydration / backfill / lazy display index。
- pending install estimate queue と install estimation evaluation。
- package install / force install / pending package mutation。
- maintenance hydration / resource health index / warning mutation。
- playlist reference index の同期と表示解決。
- folder merge / rename / auto rename / delete / unregister mutation。
- normal library refresh notification の publish / coalescing。

## 実装ルール

1. 最初の ticket は partial 分割に限定し、動作を変えない。
2. lock 順序コメントは失わない。現在の順序は `BMSLibrary` の安全性に関わる。
3. field を state object へ移すときは、同じ ticket で関連 method をすべて移す。field だけ移して root から大量参照する中途半端な形を長く残さない。
4. `ReaderWriterLockSlimWrapper` の取得順序を変える変更は、別 ticket に分けて理由コメントと test を追加する。
5. service 抽出では `BMSLibrary` の public API を即変更しない。先に facade から委譲する。
6. `Settings.Default` 直参照を新規 service に持ち込まない。必要な値は `BmsLibraryOptionsSnapshot` または新しい options snapshot で渡す。
7. DB schema / setting name / serialized field は migration plan なしに変えない。

## Phase 0: 安全網と分割耐性の整備

### Ticket BMSLIB-0A: source-text test helper を BMSLibrary 分割に対応させる

変更候補:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`
- `BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs`
- `BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs`
- `BeMusicSeeker.Tests/DialogRouteConsolidationTests.cs`

作業:

1. `SourceTextTestHelper.ReadBmsLibrarySourceText()` を追加する。
2. `BMSLibrary.cs` 単体を読む test を helper 経由に置き換える。
3. helper は次を連結する。
   - `BeMusicSeeker/Models/BMSLibrary.cs`
   - `BeMusicSeeker/Models/BMSLibrary*.cs`
   - `BeMusicSeeker/Models/BmsLibrary/**/*.cs`
4. 既存 intent が `BMSLibrary.cs` 単体の配置確認でなく、設計境界確認であることを test 名またはコメントで明確化する。

受け入れ条件:

- `BMSLibrary` partial 分割だけで source-text test が落ちない。
- 既存の禁止事項検査は維持される。

関連 test:

```powershell
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~BmsLibraryMutationBoundaryTests|FullyQualifiedName~DialogRouteConsolidationTests|FullyQualifiedName~MainWindowContextMenuResourceTests"
```

### Ticket BMSLIB-0B: private reflection tests の棚卸し

対象例:

- `ChartInfoMetadataTests.cs`
- `BmsLibraryLr2SongDbSyncTests.cs`
- `BmsLibraryZeroNoteRefreshTests.cs`
- `BmsLibraryMutationBoundaryTests.cs`

作業:

1. `typeof(BMSLibrary).GetMethod(..., BindingFlags.NonPublic)` / `GetField(..., BindingFlags.NonPublic)` を一覧化する。
2. 各 reflection 参照を次に分類する。
   - 既存 private のまま一時維持する。
   - 抽出予定 service の internal API test へ置き換える。
   - production API として昇格する。
   - 不要な implementation detail test として削除候補。
3. `.tmp/refactor/bmslibrary-reflection-test-inventory.md` に記録する。

受け入れ条件:

- 後続 ticket で method 移動しても test 修正方針が分かる。
- private API をむやみに public にしない。

## Phase 1: partial 分割で責務の塊を可視化する

### Ticket BMSLIB-1A: `BMSLibrary` を partial 化する

変更:

```csharp
public partial class BMSLibrary : NotificationObject
```

移動先候補:

```text
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.Core.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.State.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.Initialization.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.Lr2SongDbSync.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.ChartInfo.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.InstallEstimation.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.PackageInstall.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.Maintenance.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.PlaylistReference.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.ScoreAndIr.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.FileOperations.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.NormalLibraryRefresh.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.FolderRename.cs
BeMusicSeeker/Models/BmsLibrary/BMSLibrary.NestedTypes.cs
```

namespace は `BeMusicSeeker.Models` のまま維持する。

受け入れ条件:

- 動作差分なし。
- public API 差分なし。
- `BMSLibrary.cs` に constructor / public facade / compatibility comment が残る。
- file 移動後も standard checks が通る。

### Ticket BMSLIB-1B: nested helper type を領域別に移す

移動候補:

- pending install estimate 系: `PendingInstallEstimateEvaluation*`, `InstallEstimationExecutionPolicy`
- LR2 song.db sync 系: `Lr2SongDbSync*`
- chart_info 系: `ChartInfo*`
- mutation 系: `OwnedChartCollectionMutationResult`, `LibraryMutationDelta`, `InstalledChartLookupMutation`
- resource health 系: `ResourceHealth*`
- folder rename 系: `FolderAutoRenamePlan`, `AutoRenameBatchMetrics`

受け入れ条件:

- nested type の visibility と名前を維持する。
- reflection / source-text tests の意図が維持される。
- 新しい file の冒頭に「partial 分割のみ。挙動変更なし」とコメントを残す。

## Phase 2: state と mutation の境界を切る

### Ticket BMSLIB-2A: `NormalLibraryRefreshPublisher` を抽出する

対象 method 例:

- `PublishNormalLibraryRefreshNotification`
- `PublishNormalLibraryRefreshResetNotification`
- `PublishExternalReplacementNormalLibraryRefreshNotification`
- `DistinctChartsByNotificationKey`
- `CreateNormalLibraryRefreshNotificationKey`
- `ClearNormalLibraryRefreshNotification`
- `RaiseNormalLibraryRefreshNotificationVersionChanged`
- `GetNormalLibraryRefreshNotificationsAfter`

新規候補:

- `BeMusicSeeker/Models/BmsLibraryInternal/NormalLibraryRefreshPublisher.cs`
- `BeMusicSeeker.Tests/NormalLibraryRefreshPublisherTests.cs`

作業:

1. notification list / latest version / lock を `NormalLibraryRefreshPublisher` に閉じ込める。
2. `BMSLibrary` は publisher の snapshot を公開するだけにする。
3. reset / coalescing / effects merge の pure test を追加する。

受け入れ条件:

- `BMSLibrary` の normal library refresh field が publisher 1 個に集約される。
- 既存 `NormalLibraryRefreshNotificationBatch` の挙動が変わらない。

関連 test:

```powershell
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~ChartListVirtualViewTests|FullyQualifiedName~LibraryChartRowSortEngineTests|FullyQualifiedName~BmsLibraryMutationBoundaryTests"
```

### Ticket BMSLIB-2B: `LibraryMutationDeltaApplier` を抽出する

対象 method 例:

- `ApplyLibraryMutationDelta`
- `ApplyLibraryMutationDeltaWithPerformanceContext`
- `ApplyLibraryUnregisterStorageRowsUnsafe`
- `CreateRemovedBmsStorageRowDelta`
- `CreateRemovedBmsonStorageRowDelta`
- `CreateLibraryChartRefreshEffects`
- `HasLibraryMutationDeltaChanges`
- `AppendLibraryMutationDelta`

新規候補:

- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryMutationDeltaApplier.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryMutationContext.cs`

作業:

1. mutation 入力を `LibraryMutationDelta` と `LibraryMutationContext` にまとめる。
2. `BMSLibrary` の lock 取得は facade 側に残す。applier は lock が取得済みである前提を XML コメントで明記する。
3. storage rows version / owned collection invalidation / refresh notification publish を一箇所にまとめる。

受け入れ条件:

- library mutation の副作用順序が test で確認される。
- `BmsLibraryMutationBoundaryTests` が通る。
- lock 取得順序がコメントで保持される。

### Ticket BMSLIB-2C: owned chart / installed lookup / resource health state を state store へ寄せる

新規候補:

```text
BmsLibraryOwnedChartStateStore
BmsLibraryInstalledLookupStateStore
BmsLibraryResourceHealthStateStore
```

作業:

1. まず store を作って既存 field を移す。
2. mutation method は同じ ticket では移しすぎない。field owner を store に変え、既存挙動を維持する。
3. 次 ticket で method を store/coordinator へ移す。

受け入れ条件:

- field の direct mutation 箇所が grep で追える。
- state store に XML コメントで lock owner / thread safety を明記する。

## Phase 3: 大きい workflow coordinator を抽出する

### Ticket BMSLIB-3A: pending install estimation coordinator

対象 method 例:

- `QueuePendingInstallEstimateBatch`
- `ProcessPendingInstallEstimateBatch`
- `PreparePendingInstallEstimateEvaluationRequests`
- `ProcessPendingInstallEstimateEvaluationPipeline`
- `EvaluatePendingInstallEstimateRequest`
- `ApplyPendingInstallEstimateEvaluationResult`
- `PrepareBackgroundPendingEstimatePackagesUnsafe`
- `BuildPendingEstimateSourceBatchSnapshotUnsafe`
- `SearchEstimatedInstallationDirectory*`

新規候補:

- `PendingInstallEstimationCoordinator`
- `PendingInstallEstimationState`
- `PendingInstallEstimationOptions`

受け入れ条件:

- `BMSLibrary` は queue / cancel / snapshot API を facade する。
- estimation progress snapshot の更新が coordinator 内に閉じる。
- `BmsLibraryInstallEstimationServiceTests`, `PendingInstallEstimateQueueProcessorTests`, `BmsLibraryPendingPackageRegroupTests` が通る。

### Ticket BMSLIB-3B: LR2 song.db sync coordinator

対象 method 例:

- `QueueLr2SongDbSync`
- `TryRunLr2SongDbSyncDataPreparation`
- `RunLr2SongDbSync`
- `CreateLr2SongDbSyncInput`
- `CaptureLr2SongDbSyncScanSurface`
- `CaptureLr2SongDbSyncFileDiffFreshnessSnapshot`
- `ApplyLr2SongDbSyncPreparedDataSurface`
- `PublishLr2SongDbSyncStatus`
- `CancelLr2SongDbSync`

新規候補:

```text
Lr2SongDbSyncCoordinator
Lr2SongDbSyncInputBuilder
Lr2SongDbSyncSurfaceStore
Lr2SongDbSyncStatusPublisher
```

作業:

1. status publisher を先に抽出する。
2. input builder を抽出し、pure-ish test を増やす。
3. queue / cancellation / mutation block を coordinator へ移す。
4. `BMSLibrary` には facade method と compatibility diagnostics を残す。

受け入れ条件:

- LR2 sync cancellation / failure / completed status の既存 test が通る。
- `BmsLibraryLr2SongDbSyncTests` が通る。
- `.NET 10` 移行時に DB / file system / UI dialog 依存を追いやすくなる。

### Ticket BMSLIB-3C: chart_info hydration / backfill coordinator

対象 method 例:

- `QueueDeferredChartInfoHydration`
- `ProcessDeferredChartInfoHydrationRequests`
- `HydrateChartInfos`
- `TryCreateAllCurrentChartInfoHydrationResultFromCompletedLr2SongDbSync`
- `QueueChartInfoBackfill`
- `ProcessChartInfoBackfillRequests`
- `RunChartDigestBackfill`

新規候補:

```text
ChartInfoHydrationCoordinator
ChartInfoBackfillCoordinator
ChartInfoIndexStore
```

受け入れ条件:

- lazy display index load と full hydration の違いが型で分かる。
- `ChartInfoMetadataTests`, `ChartInfoProductionCompareTests`, `ChartInfoBuildServiceTests` 相当が通る。

### Ticket BMSLIB-3D: package install coordinator

対象 method 例:

- `InstallChartPackagesAuto`
- `installChartPackages`
- `InstallPendingPackagesToEstimatedDestinations`
- `ForceInstallPendingPackages`
- `RemovePendingPackages*`
- `OverwritePendingInstalledOnlyPackagesResources`
- `DeletePendingPackageSources`
- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions`

新規候補:

```text
PackageInstallCoordinator
PendingPackageMutationService
InstalledOnlyResourceOverwriteCoordinator
```

受け入れ条件:

- `BMSLibrary` は install API の入口のみ。
- file mutation と DB mutation の境界が明確。
- `BmsLibraryPackageInstallServiceTests`, `DropInstallQueueProcessorTests`, `BmsLibraryInstallEstimationServiceTests` が通る。

### Ticket BMSLIB-3E: maintenance coordinator

対象 method 例:

- `setMaintenanceInfo*`
- `ApplyMaintenanceHydrationResult`
- `ProcessDeferredMaintenanceHydrationRequests`
- `QueueDeferredInstallableMaintenance`
- `RescanResourceHealthCharts`
- `RescanAllOwnedChartMaintenance`
- `SetChartResourceWarningsIgnored`
- `SetBMSFilesEncoding`
- `RecheckZeroNoteWarnings`

新規候補:

```text
MaintenanceHydrationCoordinator
ResourceMaintenanceWorkflowCoordinator
EncodingMaintenanceCoordinator
```

受け入れ条件:

- maintenance workflow progress が `BMSLibrary` root state から外れる。
- `BmsLibraryMaintenanceServiceTests`, `BMSFileSnapshotTests`, `BmsLibraryZeroNoteRefreshTests` が通る。

### Ticket BMSLIB-3F: playlist reference coordinator

対象 method 例:

- `GetPlaylistReferenceDisplay`
- `SynchronizePlaylistReferenceIndex`
- `AddReferenceBMSTables*`
- `ReplaceReferenceBMSTable`
- `SynchronizeReferenceBMSTables`
- `RemoveReferenceBMSTables*`
- `GetPlaylistOrgMd5sForChart`
- `GetPlaylistFolderOrgMd5sForCharts`

新規候補:

```text
PlaylistReferenceCoordinator
PlaylistReferenceDisplayResolver
```

受け入れ条件:

- `BMSLibraryPlaylistReferenceService` と root class の役割が整理される。
- `BmsLibraryPlaylistReferenceServiceTests`, `PlaylistSummaryAggregationTests`, `PlaylistViewPipelineTests` が通る。

### Ticket BMSLIB-3G: folder merge / rename / deletion coordinator

対象 method 例:

- `MergeChartDirectory`
- `FixInstallationDirectoryCharts`
- `AutoRenameChartFolders`
- `AutoRenameAllChartFolders`
- `RenameChartFolder`
- `MoveLibraryRootFolder`
- `RenameBMSFilesExtensions`
- `RemoveLibraryCharts`
- `RemovePendingCharts`

新規候補:

```text
LibraryFolderMutationCoordinator
ChartFolderRenameService
LibraryChartRemovalCoordinator
```

受け入れ条件:

- file system mutation と library state mutation の順序が test で守られる。
- `BmsLibraryFolderRenameRefreshTests`, `LR2SongDBExtendedUninstallTests`, `ExplorerOpenServiceTests` 相当が通る。

## Phase 4: facade を薄くする

### Ticket BMSLIB-4A: public API forwarder を整理する

作業:

1. production code が使う API と tests だけが使う API を一覧化する。
2. tests だけが使う private reflection target は service test へ移す。
3. `BMSLibrary` の public API は compatibility と domain facade として説明できるものに絞る。

受け入れ条件:

- root class の責務が README / XML コメントで説明できる。
- `BMSLibrary.cs` は facade と composition に近づき、workflow 本体が残っていない。

### Ticket BMSLIB-4B: .NET 10 移行向け依存を狭める

作業:

1. `Settings.Default` 直接参照を options snapshot 経由へ寄せる。
2. `SQLite` 型を service 境界の内側に閉じる。
3. file system mutation は `IFileMutationService` / adapter を通す。
4. dialog 表示は `IBmsLibraryDialogService` 経由に統一する。

受け入れ条件:

- domain service が WPF 型に依存しない。
- migration blocker report に残る依存箇所が明確になる。

## 完了目標

- `BMSLibrary` root file は facade / state composition / public surface 中心になる。
- LR2 song.db sync、package install、maintenance、playlist reference、folder operation の各 workflow が単独で test 可能になる。
- lock 順序と mutation boundary が型とコメントで追える。
- `.NET 10` 移行時に、UI / config / DB / native / file system の各依存を個別に扱える。
