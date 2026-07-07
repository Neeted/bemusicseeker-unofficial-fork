# P0-02 BMSLibrary ドメイン facade 化計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

## 目的

`BeMusicSeeker/Models/BMSLibrary.cs` は現在 18,301 行あり、BeMusicSeeker の中核ドメイン操作がまだ集中している。

`BMSLibrary` は public compatibility facade として残してよい。ただし、initialization、LR2 `song.db` sync、package install、maintenance、playlist reference、file operation、normal library refresh、source text / source file handling は service / coordinator へ移す。

## 現状観測

| 項目 | 観測 |
|---|---:|
| `BMSLibrary.cs` 行数 | 18,301 行。`BMSLibrary.PackageInstall.cs` 2,015 行、`BMSLibrary.OperationDialogs.cs` 164 行、LR2 sync request / run coordinator seam / input DTO boundary / scan surface DTO boundary と host へ分離済み |
| 既存 internal service 群 | `BeMusicSeeker/Models/BmsLibraryInternal/` に多数存在 |
| `Settings.Default` 直接参照 | production 内で少なくとも `BMSLibrary.cs` 25 箇所、関連 model ではさらに多い |
| 大きい workflow 例 | `CreateLr2SongDbSyncInput`, `RunLr2SongDbSync`, `_initialize`, `ApplyLibraryFileScanDiff`, `InstallPendingPackagesToEstimatedDestinations`, `InstallChartPackagesAuto`, `MergeChartDirectory` |

## Completed Checkpoint: `REF-MVP-C1`

### BMSLibrary source-text helper and facade split foundation

目的:

- `SourceTextTestHelper.ReadBmsLibrarySourceText()` を追加する。
- `BMSLibrary.cs` 単体配置に依存する source-text / private reflection test を分割耐性のある形へ移す。
- その後、partial split または既存 `BmsLibraryInternal` service への workflow 移動を進められる状態にする。

主対象:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`
- `BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs`
- `BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs`
- `BeMusicSeeker.Tests/DialogRouteConsolidationTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs`
- `BeMusicSeeker/Models/BMSLibrary.cs`

subtasks:

1. 完了: `ReadBmsLibrarySourceText()` を追加し、少なくとも次を連結して読む。
   - `BeMusicSeeker/Models/BMSLibrary.cs`
   - `BeMusicSeeker/Models/BMSLibrary*.cs`
   - `BeMusicSeeker/Models/BmsLibrary/**/*.cs`
   - 必要な test では `BeMusicSeeker/Models/BmsLibraryInternal/**/*.cs`
2. 完了: `File.ReadAllText` で `BMSLibrary.cs` 単体を読む test を helper 経由に置き換える。
3. 完了: private reflection / source-text test の主要 blocker を `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/REF-MVP-C1_bmslibrary_test_blockers.md` に記録する。
4. 完了: `BMSLibrary` を `partial` 化し、operation dialog scope / message / service と scope 制御メソッドを `BMSLibrary.OperationDialogs.cs` へ挙動変更なしで分割する。

完了条件:

- source-text test が `BMSLibrary.cs` 単体配置に過度に依存していない。
- `BMSLibrary` facade split の blocker が 1 つ以上減っている。
- 以後の partial split / service extraction が可能になっている。
- 最初の partial split 後も `BmsLibraryMutationBoundaryTests` の source-text architecture check が logical BMSLibrary source set を読んで通る。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C2`

### BMSLibrary package install facade partial split

目的:

- `BMSLibrary.cs` の package install facade / orchestration を `BMSLibrary.PackageInstall.cs` へ挙動変更なしで分離する。
- 既存 `BmsLibraryPackageInstallService` / `BmsLibraryInstallEstimationService` への委譲関係を維持し、次の service extraction / coordinator 化の reviewability を上げる。
- public API、lock 順序、dialog / file mutation / DB mutation timing、Settings / schema / serialized value は変えない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規: `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryInstallEstimationServiceTests`, `BmsLibraryDialogRoutingTests`, `BmsLibraryMutationBoundaryTests`

subtasks:

1. 完了: `InstallChartPackagesAuto` から pending package cleanup helpers までの連続ブロックを dedicated partial へ移した。
2. 完了: 分割後の `BMSLibrary.cs` / `BMSLibrary.PackageInstall.cs` 行数と残る責務を記録した。
3. 完了: package install 関連 tests と標準確認を実行した。

完了条件:

- package install public/internal facade と関連 helper が dedicated partial にまとまっている。
- `BMSLibrary.cs` の package install 責務が明確に減っている。
- `SourceTextTestHelper.ReadBmsLibrarySourceText()` 経由の architecture tests が partial split 後も通る。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C3`

### ForceInstallPendingPackages coordinator seam

状態: completed checkpoint。

目的:

- `ForceInstallPendingPackages` の lock / LR2 sync block / confirm callback / pending removal / installed package merge / logging を coordinator seam へ移す。
- `installChartPackages` 本体は移さず delegate 境界として残す。
- `InstallPendingPackagesToEstimatedDestinations`、`InstallChartPackagesAuto`、pending cleanup / rename は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/ForceInstallPendingPackagesCoordinator.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryDialogRoutingTests`, `BmsLibraryMutationBoundaryTests`

subtasks:

1. coordinator / host contract を追加し、`ForceInstallPendingPackages` 本体を移す。
2. root `BMSLibrary` には public/internal API entry と host bridge を残す。
3. package install 関連 tests と標準確認を実行する。

完了条件:

- `ForceInstallPendingPackages` public/internal API は維持されている。
- lock 順序、LR2 sync block、dialog timing、pending removal、installed add、log 文言が維持されている。
- `installChartPackages` は今回移さず delegate 境界に留める。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C4`

### Pending estimated install coordinator seam

状態: completed checkpoint。

目的:

- `InstallPendingPackagesToEstimatedDestinations` の lock / LR2 sync block / batch plan / DB row delete / pending and installed collection apply / maintenance / inline chart_info / cleanup warning / performance logging を coordinator seam へ移す。
- `installChartPackages` 本体は移さず delegate 境界として残す。
- `InstallChartPackagesAuto`、`OverwritePendingInstalledOnlyPackagesResources`、pending cleanup / rename は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PendingEstimatedInstallCoordinator.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.PendingEstimatedInstallHost.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryDialogRoutingTests`, `BmsLibraryMutationBoundaryTests`

subtasks:

1. coordinator / host contract を追加し、`InstallPendingPackagesToEstimatedDestinations` 本体を移す。
2. root `BMSLibrary` には public API entry と host bridge を残す。
3. package install 関連 tests と標準確認を実行する。

完了条件:

- `InstallPendingPackagesToEstimatedDestinations` public API、null guard、LR2 sync block、lock 順序、dialog timing、log 文言が維持されている。
- post-processing の順序、特に `dbGateway.DeleteInstallRows`、resource health delta suppression、pending/installed apply、maintenance、inline chart_info、cleanup warning が維持されている。
- `installChartPackages` は今回移さず delegate 境界に留める。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C5`

### Advanced pending resource overwrite coordinator seam

状態: completed checkpoint。

目的:

- `OverwritePendingInstalledOnlyPackagesResources` の lock / installed lookup snapshot / skip detail / estimated install callback / cleanup callback / pending removal / summary logging / deferred progress flush を coordinator seam へ移す。
- `InstallPendingPackagesToEstimatedDestinations` 本体は移さず delegate 境界として残す。
- pending source cleanup、zero-note rename は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PendingResourceOverwriteCoordinator.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.PendingResourceOverwriteHost.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryMutationBoundaryTests`, source-text architecture tests

subtasks:

1. coordinator / host contract を追加し、`OverwritePendingInstalledOnlyPackagesResources` 本体を移す。
2. root `BMSLibrary` には public API entry と host bridge を残す。
3. package install 関連 tests と標準確認を実行する。

完了条件:

- `OverwritePendingInstalledOnlyPackagesResources` public API、null guard、options snapshot timing、lock 順序、deferred progress flush、summary log 文言が維持されている。
- install failure exception handling と dialog timing が維持されている。
- `InstallPendingPackagesToEstimatedDestinations` は今回移さず delegate 境界に留める。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C6`

### Pending package source deletion coordinator seam

状態: completed checkpoint。

目的:

- `DeletePendingPackageSources` の lock / dedup / service execution / failure dialog / pending removal / summary logging / deferred progress flush を coordinator seam へ移す。
- pending chart deletion、zero-note rename、folder/file operation 全般は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PendingPackageSourceDeletionCoordinator.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.PendingPackageSourceDeletionHost.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryMutationBoundaryTests`

subtasks:

1. coordinator / host contract を追加し、`DeletePendingPackageSources` 本体を移す。
2. root `BMSLibrary` には public API entry と host bridge を残す。
3. package install 関連 tests と標準確認を実行する。

完了条件:

- `DeletePendingPackageSources` public API、null guard、lock 順序、requested/permanent log、failure warning/dialog、summary log、deferred progress flush が維持されている。
- file mutation service と options の渡し方が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C7`

### Pending zero-note rename coordinator seam

状態: completed checkpoint。

目的:

- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` の snapshot fallback / lock / service execution / failure dialog / pending chart removal / summary logging / deferred progress flush を coordinator seam へ移す。
- pending source deletion、pending chart deletion、folder/file operation 全般は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PendingZeroNoteRenameCoordinator.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.PendingZeroNoteRenameHost.cs`
- package install 関連 tests: `BmsLibraryPackageInstallServiceTests`, `BmsLibraryMutationBoundaryTests`

subtasks:

1. coordinator / host contract を追加し、`RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` 本体を移す。
2. root `BMSLibrary` には internal API entry と host bridge を残す。
3. package install 関連 tests と標準確認を実行する。

完了条件:

- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` internal API、target null fallback、lock 順序、failure dialogs、pending chart removal、summary log、deferred progress flush が維持されている。
- `ProcessInvalidExtensionRename(..., removeFromLibraryOnSuccess: false)` の呼び方が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C8`

### Normal library refresh publisher extraction

状態: completed checkpoint。

目的:

- normal library refresh notification の queue / version / reset barrier / batch aggregation を publisher seam へ移す。
- `BMSLibrary` には public/internal surface と `OwnedChartCollectionMutationResult` から publisher input を組み立てる bridge を残す。
- LR2 sync、maintenance hydration、file operation、MainWindowViewModel 側の notification consumption は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/NormalLibraryRefreshPublisher.cs`
- normal refresh 関連 tests: `OwnedChartCollectionStateTests`, `BmsLibraryFolderRenameRefreshTests`, `BmsLibraryMaintenanceServiceTests`, `BmsLibraryZeroNoteRefreshTests`, `ChartInfoMetadataTests`

subtasks:

1. publisher を追加し、queue / version / batch aggregation / clear を移す。
2. root `BMSLibrary` の `NormalLibraryRefreshNotificationVersion` / `GetNormalLibraryRefreshNotificationsAfter` / publish / clear bridge を publisher 委譲にする。
3. normal refresh 関連 tests と標準確認を実行する。

完了条件:

- `NormalLibraryRefreshNotificationVersion` と `GetNormalLibraryRefreshNotificationsAfter` の public/internal surface が維持されている。
- reset barrier、effect aggregation、install-destination changed charts の distinct、remove-only storage row delta、clear-on-failure、property notification timing が維持されている。
- build / normal refresh 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C9`

### Playlist reference index manager seam

状態: completed checkpoint。

目的:

- playlist reference display の index state / lock / table replace / remove / synchronize を manager seam へ移す。
- `BMSLibrary` には既存 public/internal API と playlist reference apply workflow、pending/package/library snapshot bridge を残す。
- MainWindowViewModel consumption、BMSTable persistence、playlist apply workflow 全体は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistReferenceManager.cs`
- playlist reference 関連 tests: `BmsLibraryFolderRenameRefreshTests`, `BmsLibraryPlaylistReferenceServiceTests`, `MainWindowContextMenuResourceTests`, `PlaylistConcurrencyArchitectureTests`

subtasks:

1. manager を追加し、`playlistReferenceIndexLock` / `playlistReferenceIndex` と find / replace / remove / synchronize を移す。
2. root `BMSLibrary` の `GetPlaylistReferenceDisplay` overloads と index mutation helpers を manager 委譲にする。
3. playlist reference 関連 tests と標準確認を実行する。

完了条件:

- `GetPlaylistReferenceDisplay` overloads、`AddReferenceBMSTables*`、`ReplaceReferenceBMSTable`、`RemoveReferenceBMSTables*`、`SynchronizeReferenceBMSTables` の surface と挙動が維持されている。
- root `BMSLibrary` が `playlistReferenceIndexLock` / `playlistReferenceIndex` を直接持たない。
- playlist reference 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C10`

### Playlist reference apply coordinator seam

状態: completed checkpoint。

目的:

- playlist reference map build / library chart apply / pending chart apply / package chart apply / replace target logging を coordinator seam へ移す。
- `BMSLibrary` には既存 public/internal API、playlist reference manager bridge、snapshot / lock bridge、logging bridge を残す。
- MainWindowViewModel consumption、BMSTable persistence、playlist database 更新、chart row refresh は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.PlaylistReferenceHost.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistReferenceApplyCoordinator.cs`
- playlist reference 関連 tests: `BmsLibraryFolderRenameRefreshTests`, `BmsLibraryPlaylistReferenceServiceTests`, `MainWindowContextMenuResourceTests`, `PlaylistConcurrencyArchitectureTests`

subtasks:

1. coordinator / host contract を追加し、`AddReferenceBMSTables*` / `ReplaceReferenceBMSTable` / remove / synchronize orchestration を移す。
2. root `BMSLibrary` の public/internal API entry は維持し、snapshot / lock / logging / index bridge は host 経由にする。
3. playlist reference 関連 tests と標準確認を実行する。

完了条件:

- `AddReferenceBMSTables*`、`ReplaceReferenceBMSTable`、`RemoveReferenceBMSTables*`、`SynchronizeReferenceBMSTables` の public/internal surface と log key が維持されている。
- pending bmson entry を materialize しない既存挙動が維持されている。
- playlist reference 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C11`

### LR2 song.db sync request coordinator seam

状態: completed checkpoint。

目的:

- `QueueLr2SongDbSync`、`TryRunLr2SongDbSyncDataPreparation`、`CleanupLr2SongDbSyncStartupScanBlockerFolderRows`、`PublishLr2SongDbSyncExternalStageProgress` の実行要求・準備・状態公開 orchestration を `BmsLibraryInternal` coordinator へ移す。
- `BMSLibrary` には既存 public/internal API、options / DB / status / scheduler / input creation bridge を残す。
- `CreateLr2SongDbSyncInput`、`RunLr2SongDbSync` 本体、file diff scan surface、chart_info hydration / resolver、DB schema / setting name は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BMSLibrary.Lr2SongDbSyncRequestHost.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncRequestCoordinator.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`, `Lr2SongDbSyncStatusServiceTests`, `MainWindowContextMenuResourceTests`

subtasks:

1. coordinator / host contract を追加し、queue / preparation / cleanup / external progress orchestration を移す。
2. root `BMSLibrary` の API entry は維持し、status publish・request begin/complete・scheduler・input creation は host bridge 経由にする。
3. LR2 sync 関連 tests と標準確認を実行する。

完了条件:

- 対象 API の surface と log key が維持されている。
- prepare reservation、mutation block、shutdown skip、startup scheduler、status publish の順序が維持されている。
- `CreateLr2SongDbSyncInput` と `RunLr2SongDbSync` 本体は今回移さず、後続 ticket で詳細化する。
- LR2 sync 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C12`

### LR2 song.db sync run coordinator seam

状態: completed checkpoint。

目的:

- `RunLr2SongDbSync` の preflight / service request construction / result logging / completion-failure handling を `Lr2SongDbSyncRequestCoordinator` へ移す。
- `BMSLibrary` には cancellation token / input construction / chart_info resolver / projection / status mutation / startup task reporting の host bridge を残す。
- `CreateLr2SongDbSyncInput` 本体、scan surface、file diff freshness、chart_info hydration internals、DB schema / setting name は今回触らない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BMSLibrary.Lr2SongDbSyncRequestHost.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncRequestCoordinator.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`, `Lr2SongDbSyncStatusServiceTests`, `MainWindowContextMenuResourceTests`

subtasks:

1. `RunLr2SongDbSync` 本体を coordinator へ移し、root method は entry / compatibility forwarder にする。
2. coordinator が必要とする root state / callbacks を host contract へ追加する。
3. LR2 sync 関連 tests と標準確認を実行する。

完了条件:

- `lr2_song_db_sync` の log key、preflight stage、service request fields、completion / incomplete / cancelled / failed の status transition が維持されている。
- `CreateLr2SongDbSyncInput` と scan surface helper は今回移さず、後続 ticket で詳細化する。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C13`

### LR2 song.db sync input DTO boundary

状態: completed checkpoint。

目的:

- `BMSLibrary.Lr2SongDbSyncInput` nested type 依存をやめ、LR2 sync coordinator / host が top-level internal DTO を扱える境界にする。
- private `CreateLr2SongDbSyncInput` 入口、DTO property 名、service request fields、DB schema / setting name は変えない。
- `CreateLr2SongDbSyncInput` 本体と scan surface helper の移動は今回の完了条件に含めず、C13 完了後に builder / scan surface follow-up として再計画する。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BMSLibrary.Lr2SongDbSyncRequestHost.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncRequestCoordinator.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncInput.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`, `Lr2SongDbSyncStatusServiceTests`, `Lr2SongDbSyncStatusMapperTests`

完了条件:

- `Lr2SongDbSyncInput` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- `ILr2SongDbSyncRequestHost` / `Lr2SongDbSyncRequestCoordinator` が `BMSLibrary.Lr2SongDbSyncInput` に依存していない。
- 既存 reflection tests が private `CreateLr2SongDbSyncInput` 入口と public property surface 経由で通る。
- build / LR2 sync 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C14`

### LR2 song.db sync scan surface DTO boundary

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncScanSurfaceSnapshot` を top-level internal DTO に移し、input builder / scan surface follow-up の前提を作る。
- scan surface の取得、適用、世代判定、prepared surface merge、DB schema / setting name は変えない。
- `Lr2SongDbSyncFileDiffFreshnessSnapshot` や app-managed output scope の移動は今回の完了条件に含めず、C14 完了後に必要なら再計画する。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncScanSurfaceSnapshot.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`, `Lr2SongDbSyncStatusServiceTests`, `Lr2SongDbSyncStatusMapperTests`

完了条件:

- `Lr2SongDbSyncScanSurfaceSnapshot` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- scan surface generation / root directories / directory entries / LR2 folder candidate fields の property surface と null fallback が維持されている。
- build / LR2 sync 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Target Architecture

```text
BMSLibrary                         // public facade / compatibility API
├─ BmsLibraryRuntimeState
├─ LibraryInitializationCoordinator
├─ Lr2SongDbSyncCoordinator
├─ PackageInstallWorkspace
├─ MaintenanceWorkspace
├─ PlaylistReferenceCoordinator
├─ LibraryFileOperationCoordinator
└─ NormalLibraryRefreshPublisher
```

`BMSLibrary` に残すもの:

- public / internal 互換 API の入口。
- constructor と依存 service の組み立て。
- 既存 event / property changed の public surface。
- 複数 service にまたがる最小限の orchestration。
- 互換性維持のための adapter / forwarder。

`BMSLibrary` から出すもの:

- LR2 song.db sync input 構築、予約、進捗、キャンセル、status mapping。
- chart_info hydration / backfill / lazy display index。
- pending install estimate queue と install estimation evaluation。
- package install / force install / pending package mutation。
- maintenance hydration / resource health index / warning mutation。
- playlist reference index の同期と表示解決。
- folder merge / rename / auto rename / delete / unregister mutation。
- normal library refresh notification の publish / coalescing。

## Constraints

- release freeze を破らない。
- `BMSLibrary` の public API を即変更しない。先に facade から委譲する。
- lock 順序コメントを失わない。
- `ReaderWriterLockSlimWrapper` の取得順序を変える変更は、別 slice に分けて理由コメントと test を追加する。
- 新規 service に `Settings.Default` 直参照を持ち込まない。必要な値は options snapshot / gateway で渡す。
- DB schema / setting name / serialized field は migration plan なしに変えない。
- `.tmp` にだけ blocker inventory を残さない。

## Guardrail

`BMSLibrary.cs` は最終 12,000 行以下を目標にする。超過する場合は `PLAN_STATUS.md` に、残す責務、残す理由、次の extraction 候補、サブエージェントレビュー結果を記録する。

## 後続候補

`REF-MVP-C14` 完了後に、次を workflow 単位で選ぶ。詳細な実装プランは、直近で選ぶ Lane C workflow の着手時に再確認する。

- LR2 song.db sync input builder / scan surface helper follow-up。
- package install coordinator。C2 では partial split までに留め、本格 coordinator 化は C2 後に詳細化する。
- maintenance coordinator。
- playlist reference follow-up coordinator。
- file operation / folder rename coordinator。
