# P0-02 BMSLibrary ドメイン facade 化計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

## 目的

`BeMusicSeeker/Models/BMSLibrary.cs` は現在 17,866 行あり、BeMusicSeeker の中核ドメイン操作がまだ集中している。

`BMSLibrary` は public compatibility facade として残してよい。ただし、initialization、LR2 `song.db` sync、package install、maintenance、playlist reference、file operation、normal library refresh、source text / source file handling は service / coordinator へ移す。

## 現状観測

| 項目 | 観測 |
|---|---:|
| `BMSLibrary.cs` 行数 | 17,866 行。`BMSLibrary.PackageInstall.cs` 2,015 行、`BMSLibrary.OperationDialogs.cs` 164 行、LR2 sync request / run coordinator seam / input DTO boundary / scan surface DTO boundary / app-managed output scope DTO boundary / file-diff freshness DTO boundary / input row snapshot boundary / input builder helper ownership を host から分離済み |
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

## Completed Checkpoint: `REF-MVP-C15`

### LR2 song.db sync app-managed output scope DTO boundary

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncAppManagedOutputScope` を top-level internal DTO に移し、input builder follow-up の前提を作る。
- private `CreateLr2SongDbSyncAppManagedOutputScope` 入口、public property 名、playlist / DB schema / setting name は変えない。
- app-managed output scope の生成ロジック自体は今回移さない。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncAppManagedOutputScope.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`, `Lr2FolderFileDiscoveryServiceTests`

完了条件:

- `Lr2SongDbSyncAppManagedOutputScope` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- `Directories` / `FilePaths` / `PruneExcludedPaths` / `IsComplete` の property surface と null fallback が維持されている。
- 既存 reflection tests が private `CreateLr2SongDbSyncAppManagedOutputScope` 入口と public property surface 経由で通る。
- build / LR2 sync 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C16`

### LR2 song.db sync file-diff freshness DTO boundary

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncFileDiffFreshnessSnapshot` を top-level internal DTO に移し、input freshness / transient skip path 判定の nested state 依存を切る。
- file-diff freshness の capture / clear / verification ロジック、log key、DB schema / setting name は変えない。
- `CreateLr2SongDbSyncInput` 本体の builder 化は今回の完了条件に含めず、C16 完了後に再計画する。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncFileDiffFreshnessSnapshot.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`

完了条件:

- `Lr2SongDbSyncFileDiffFreshnessSnapshot` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- all count/version properties と `TransientSongRowSkipPaths` の `HashSet<string>` / `StringComparer.OrdinalIgnoreCase` / null filtering が維持されている。
- build / LR2 sync 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Completed Checkpoint: `REF-MVP-C17`

### LR2 song.db sync input row snapshot boundary

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` から `_BMSFiles` / `rwlockBMSFilesInitializedAll` / `StorageRowsVersionSnapshot` の直接扱いを外し、top-level input row snapshot DTO へ閉じ込める。
- row snapshot の chart path distinct、song row capture、owned collection / storage row version capture の順序と lock 範囲は変えない。
- `CreateLr2SongDbSyncInput` 本体の builder 化は今回の完了条件に含めず、C17 完了後に再計画する。

主対象:

- `BeMusicSeeker/Models/BMSLibrary.cs`
- 新規候補: `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncInputRowSnapshot.cs`
- LR2 sync 関連 tests: `BmsLibraryLr2SongDbSyncTests`

完了条件:

- `Lr2SongDbSyncInputRowSnapshot` が top-level internal DTO として追加されている。
- `CreateLr2SongDbSyncInput` が row snapshot DTO 経由で chart paths / song rows / version を読む。
- `GetCurrentLr2SongDbSyncScanSurface` が private `StorageRowsVersionSnapshot` ではなく row snapshot DTO から version を判定できる。
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

## Completed Checkpoint: `REF-MVP-C18`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の root discovery と LR2 settings / builtin settings capture を top-level internal DTO 境界へ寄せる。
- C17 の row snapshot と合わせ、後続の input builder 化で必要な入力面を private root state から分離しやすくする。
- `rootsMs` / `builtinSettingsMs` の計測範囲、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- root directories / LR2 discovery directories / LR2 root path が dedicated snapshot DTO 経由で `CreateLr2SongDbSyncInput` に渡る。
- builtin custom folder settings / builtin source directories / custom folder output base / prune directories が dedicated snapshot DTO 経由で `CreateLr2SongDbSyncInput` に渡る。
- `CreateLr2SongDbSyncInput` 本体の full builder 化、scan surface helper 移動、DB schema / setting name 変更は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputRootSnapshot` と `Lr2SongDbSyncInputSettingsSnapshot` を追加し、root discovery / LR2 settings capture を dedicated DTO 経由へ移した。
- `DateTime.UtcNow` は従来どおり root discovery 前に capture し、recent 判定の cutoff が遅延しないようにした。
- 新規 DTO には意味を変える null fallback を追加していない。

## 後続候補

`REF-MVP-C18` 完了後に、次を workflow 単位で選ぶ。詳細な実装プランは、直近で選ぶ Lane C workflow の着手時に再確認する。

- LR2 song.db sync input builder / scan surface helper follow-up。
- package install coordinator。C2 では partial split までに留め、本格 coordinator 化は C2 後に詳細化する。
- maintenance coordinator。
- playlist reference follow-up coordinator。
- file operation / folder rename coordinator。

## Completed Checkpoint: `REF-MVP-C19`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の pending prepared surface 取得と scan surface 適用済み判定を top-level internal DTO 境界へ寄せる。
- 後続の input builder 化で、scan surface / prepared surface の選択済み入力を明示的に渡せる状態にする。
- prepared surface の merge 挙動、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `TakeLr2SongDbSyncPreparedDataSurface` と `preparedSurfaceAlreadyAppliedToScanSurface` 判定が dedicated selection DTO / helper 経由になる。
- `CreateLr2SongDbSyncInput` で使う active prepared surface、pending prepared surface、prepared flags、applied generation が同じ意味で残る。
- full builder 化、LR2 folder candidate builder 化、scan surface helper 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncPreparedSurfaceSelection` を追加し、pending prepared surface と active prepared surface の選択結果を dedicated DTO 経由へ移した。
- `preparedSurfaceAlreadyAppliedToScanSurface` と `preparedSurfaceAppliedScanGeneration` のログ意味は維持した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C20`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の LR2 folder candidate selection と app-managed filtering count/source を top-level internal DTO 境界へ寄せる。
- 後続の input builder 化で、LR2 folder candidates / source / app-managed counts を選択済み入力として扱える状態にする。
- candidate 生成順、prepared merge、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- scan surface / enumeration / prepared surface / app-managed scope からの `Lr2FolderFileCandidateSnapshot` 選択が dedicated selection DTO / helper 経由になる。
- `lr2FolderCandidatesSource`、`enumeratedAppManagedFiltered`、`enumeratedAppManagedExactFiles` のログ意味が維持される。
- full builder 化、directory/folderInfo/text metadata candidate builder 化、scan surface helper 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncFolderCandidateSelection` を追加し、LR2 folder candidates / source / app-managed counts を dedicated DTO 経由へ移した。
- scan surface / enumeration、app-managed complete / incomplete、prepared merge、discoveryComplete false 化、二段階 app-managed filtering の順序は維持した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C21`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の text file directory selection と `textFileDirsSource` を top-level internal DTO 境界へ寄せる。
- 後続の input builder 化で、text file directories / source を選択済み入力として扱える状態にする。
- scan surface / enumeration / prepared-only の優先順、prepared merge、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- text file directories と source が dedicated selection DTO / helper 経由になる。
- `textFileDirs` と `textFileDirsSource` のログ意味が維持される。
- directory entry / folderInfo candidate builder 化、full builder 化、scan surface helper 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncTextFileDirectorySelection` を追加し、text file directories / source を dedicated DTO 経由へ移した。
- scan surface / enumeration / prepared-only / empty の優先順と prepared merge の引数順は維持した。
- 初回 full test で `QueueLr2SongDbSync_DiscoversRootCustomFolderOutputAsRootRow` が 1 回失敗したが、同テスト単体 rerun と full test rerun は pass した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C22`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の folderInfo candidate selection と enumeration 時の text metadata candidate capture を top-level internal DTO 境界へ寄せる。
- 後続の input builder 化で、folderInfo candidates と text metadata candidates を選択済み入力として扱える状態にする。
- scan surface / enumeration の優先順、prepared merge、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- folderInfo candidates と text metadata candidates が dedicated selection DTO / helper 経由になる。
- `folderInfoCandidates` ログ値と後段 text file directory selection の入力意味が維持される。
- directory entry builder 化、full builder 化、scan surface helper 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncFolderInfoCandidateSelection` を追加し、folderInfo candidates / text metadata candidates を dedicated DTO 経由へ移した。
- scan surface / enumeration の優先順、prepared folderInfo merge の条件と引数順、後段 text file directory selection への null/非 null 入力は維持した。
- 静的レビューの指摘は新規 DTO の add 漏れ注意のみで、commit 対象に含めることで解消する。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C23`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の directory entry selection と missing count を top-level internal DTO 境界へ寄せる。
- 後続の input builder 化で、directory entries / missing count を選択済み入力として扱える状態にする。
- scan surface / grouped scan の優先順、prepared overlay、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- directory entries と missing count が dedicated selection DTO / helper 経由になる。
- `directoryEntries` と `missingDirectoryEntries` のログ意味が維持される。
- full builder 化、scan surface helper 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncDirectoryEntrySelection` を追加し、directory entries / missing count を dedicated DTO 経由へ移した。
- scan surface / grouped scan の優先順、prepared overlay、missing count の計算式は維持した。
- 初回 full test で `CreateLr2SongDbSyncInputWithoutScanSurface_ExcludesManagedOutputDirectoryFiles` が 1 回失敗したが、同テスト単体 rerun、LR2 targeted rerun、full test rerun は pass した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C24`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の最終 `Lr2SongDbSyncInput` composition を top-level internal factory 境界へ寄せる。
- C17-C23 で分けた snapshot / selection DTO を、後続 builder 化で直接渡せる composition surface にする。
- ログ項目、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `new Lr2SongDbSyncInput(...)` の組み立てが dedicated factory 経由になる。
- constructor 引数の値と順序が維持される。
- log string builder 化、scan surface helper 移動、private entry point 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputFactory` を追加し、最終 `Lr2SongDbSyncInput` constructor composition を dedicated factory 経由へ移した。
- constructor に渡す値と順序は維持した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C25`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の `lr2_song_db_sync_input_surface` log message composition を dedicated helper 境界へ寄せる。
- 入力作成本体を snapshot / selection / input composition の流れとして読みやすくする。
- ログ項目、値、順序、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `LogInstallPerformance("lr2_song_db_sync_input_surface" + ...)` の文字列組み立てが dedicated helper 経由になる。
- ログ項目名、順序、値の対応が維持される。
- scan surface helper 移動、private entry point 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `LogLr2SongDbSyncInputSurface` を追加し、`lr2_song_db_sync_input_surface` log message composition を dedicated helper 経由へ移した。
- ログ項目名、順序、値の対応は維持した。
- full test で LR2 sync 系テストが複数回単発失敗したが、該当テスト単体 rerun と最終 full test rerun は pass した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C26`

状態: completed checkpoint。

目的:

- C25 で切った `lr2_song_db_sync_input_surface` log helper を top-level internal formatter へ移す。
- `BMSLibrary` root から長いログ文字列 composition を外し、入力作成本体を orchestration に近づける。
- ログ項目、値、順序、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `lr2_song_db_sync_input_surface` log message が `BmsLibraryInternal` の dedicated formatter で組み立てられる。
- `BMSLibrary` は formatter の戻り値を `LogInstallPerformance` へ渡すだけになる。
- ログ項目名、順序、値の対応が維持される。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputSurfaceLogFormatter` を追加し、`lr2_song_db_sync_input_surface` log message composition を top-level internal formatter へ移した。
- `BMSLibrary` は formatter の戻り値を `LogInstallPerformance` へ渡すだけになった。
- ログ項目名、順序、値の対応は維持した。
- DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C27`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の scan surface / miss reason / reuse 判定を dedicated selection DTO 境界へ寄せる。
- `GetCurrentLr2SongDbSyncScanSurface` の lock 下 snapshot 読み取りと root state access は root に残し、後続の input builder 化で参照しやすい選択済み入力にする。
- scan surface の有効性判定、miss reason、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- scan surface と miss reason が `Lr2SongDbSyncScanSurfaceSelection` 経由で扱われる。
- reused scan surface / reused LR2 folder surface / generation の意味が維持される。
- `GetCurrentLr2SongDbSyncScanSurface` 本体の top-level service 化、directory target selection 移動、private entry point 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncScanSurfaceSelection` を追加し、scan surface / miss reason / reused LR2 folder surface / generation を dedicated DTO 経由へ移した。
- `GetCurrentLr2SongDbSyncScanSurface` の lock 下 snapshot 読み取りと root state access は root helper に残した。
- scan surface の有効性判定、miss reason、ログ項目、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C28`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` の normal directory metadata targets / LR2 folder parent directory targets / merged directory entry targets を dedicated selection DTO 境界へ寄せる。
- 後続の input builder 化で、directory target 群を選択済み入力として扱える状態にする。
- target の優先順、dedupe / comparer、ログ項目、`directoryTargetsMs` の計測意味、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- normal directory metadata targets / LR2 folder parent directory targets / merged directory entry targets が selection DTO / helper 経由になる。
- `directoryTargetsMs` は既存通り normal directory metadata target 作成部分だけを測る。
- directory entry enumeration、folder info candidate selection、private entry point 移動は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncDirectoryTargetSelection` を追加し、normal directory metadata targets / LR2 folder parent directory targets / merged directory entry targets を dedicated DTO 経由へ移した。
- normal directory metadata target 作成は既存と同じ `directoryTargetsMs` 範囲に残し、LR2 folder parent target と merged target の comparer / dedupe / sort semantics は既存 helper をそのまま使って維持した。
- directory entry enumeration、folder info candidate selection、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C29`

状態: completed checkpoint。

目的:

- C18-C28 で切った selection DTO のうち scan surface selection / directory target selection を log formatter 入力として直接扱う。
- `CreateLr2SongDbSyncInput` に残る log 専用 local alias を減らし、snapshot / selection / candidate / factory の流れを読みやすくする。
- ログ項目名、ログ値、ログ順序、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が scan surface selection / directory target selection を直接受け取る。
- `CreateLr2SongDbSyncInput` の log 専用 alias が減っている。
- input builder 化、private entry point 移動、service 化は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が scan surface selection / directory target selection を直接受け取る形にした。
- `CreateLr2SongDbSyncInput` から log 専用の `reusedLr2FolderSurface` / LR2 folder parent target alias などを減らした。
- ログ項目名、ログ値、ログ順序、selection 順序、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。
- 初回 LR2 sync 関連 tests で `ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots` が 1 回失敗したが、同テスト単体 rerun と LR2 targeted rerun は pass した。最終 full test は pass した。

## Completed Checkpoint: `REF-MVP-C30`

状態: completed checkpoint。

目的:

- `Lr2SongDbSyncInputFactory.Create` が scan surface selection / directory target selection を直接受け取る形にする。
- C18-C29 で切った selection DTO を log と final input composition の両方で同じ境界として扱う。
- constructor に渡す値、selection 順序、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `Lr2SongDbSyncInputFactory.Create` の引数から standalone `directoryMetadataTargets` と `scanSurfaceGeneration` が消え、selection DTO 経由になる。
- `CreateLr2SongDbSyncInput` の factory 呼び出しが selection DTO ベースになっている。
- input builder 化、private entry point 移動、service 化は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputFactory.Create` が scan surface selection / directory target selection を直接受け取る形にした。
- constructor に渡す directory metadata target copy と scan surface generation は、既存と同じ値を selection DTO 経由で渡す形にした。
- constructor 値、selection 順序、ログ項目、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C31`

状態: completed checkpoint。

目的:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` に渡している Stopwatch 群を dedicated timing DTO 経由にする。
- C18-C30 で切った snapshot / selection DTO と同じく、ログ計測値も named contract として扱える状態にする。
- ログ項目名、ログ値、ログ順序、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `Lr2SongDbSyncInputSurfaceTimings` が追加され、Stopwatch 群が DTO 経由で formatter に渡る。
- `rowSnapshotMs` など既存ログ timing 値の対応が維持される。
- input builder 化、private entry point 移動、service 化は今回の完了条件に含めない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputSurfaceTimings` を追加し、Stopwatch 群を dedicated timing DTO 経由で formatter に渡す形にした。
- `directoryEntriesMs`、`rowSnapshotMs`、`rootsMs`、`builtinSettingsMs`、`scanSurfaceLookupMs`、`directoryTargetsMs`、`lr2FolderCandidatesMs`、`folderInfoCandidatesMs`、`textFileDirsMs`、`totalMs` のログ項目名・順序・値対応は維持した。
- ログ項目、selection 順序、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C32`

状態: completed checkpoint。

目的:

- C18-C31 後の `CreateLr2SongDbSyncInput` が root orchestration として妥当かをレビューする。
- 次に service / builder extraction へ進むか、もう 1 件だけ DTO wiring / helper placement を切るかを decision record 化する。
- production code は変更しない。

完了条件:

- decision record が `decisions/` に追加され、次にやる 1 件が明確になっている。
- まだ root に残すべき state / concurrency 境界が明記されている。
- P0-02 と `PLAN_STATUS.md` が decision と矛盾していない。
- diff check / 静的レビューが完了している。

実装結果:

- [REF-MVP-C32 LR2 Sync Input Composition Surface Decision](./decisions/REF-MVP-C32_lr2_sync_input_composition_surface.md) を追加した。
- `CreateLr2SongDbSyncInput` は現時点では root orchestration として許容し、full service / builder extraction の前に `REF-MVP-C33: LR2 sync input prepared surface selection boundary` を 1 ticket だけ挟む判断にした。
- root に残す state / concurrency 境界として、scan surface snapshot state access、row snapshot reader lock、root/settings compatibility boundary、prepared surface consumption timing を明記した。
- production code は変更していない。

## Completed Checkpoint: `REF-MVP-C33`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` で展開している prepared surface alias を減らし、prepared surface selection DTO を helper / formatter へ直接渡す。
- C32 decision に従い、full service / builder extraction の前に prepared surface boundary を安定させる。
- prepared surface consumption timing、ログ項目名、ログ値、selection 順序、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が prepared surface selection を直接受け取る。
- folder candidate / folder info / directory entry / text file directory selection helpers が、可能な範囲で prepared surface selection 経由になる。
- `CreateLr2SongDbSyncInput` に残る prepared surface local alias が減っている。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が prepared surface selection を直接受け取る形にした。
- folder candidate / folder info / directory entry / text file directory selection helpers も prepared surface selection 経由に寄せ、`CreateLr2SongDbSyncInput` から prepared surface log/helper 専用 alias を減らした。
- prepared surface consumption timing、ログ項目名、ログ値、selection 順序、DB schema、setting name、private reflection entry point、serialized/public surface は変更していない。

## Completed Checkpoint: `REF-MVP-C34`

状態: completed checkpoint。

目的:

- C33 後の `CreateLr2SongDbSyncInput` を再評価し、service / builder extraction に進むかを決める。
- root に残す stateful helper と concurrency 境界を明記する。
- production code は変更しない。

完了条件:

- decision record が `decisions/` に追加され、次にやる 1 件が明確になっている。
- P0-02 と `PLAN_STATUS.md` が decision と矛盾していない。
- diff check / 静的レビューが完了している。

実装結果:

- [REF-MVP-C34 LR2 Sync Input Builder Extraction Decision](./decisions/REF-MVP-C34_lr2_sync_input_builder_extraction.md) を追加した。
- Lane C は service extraction ではなく `REF-MVP-C35: LR2 sync input builder extraction` に進む判断にした。
- root に残す state / concurrency 境界として、sync 実行状態、host adapter snapshot / reservation、scan / prepared / file-diff mutable cache、row snapshot reader lock、freshness 判定、chart_info hydration / UI warning dispatch を明記した。
- production code は変更していない。

## Completed Checkpoint: `REF-MVP-C35`

状態: completed checkpoint。

目的:

- `Lr2SongDbSyncInputBuilder` を `BmsLibraryInternal` に追加し、候補選択以降の input composition を builder に移す。
- BMSLibrary root は row/root/settings/scan/directory metadata/prepared/app-managed scope snapshot 採取と compatibility/concurrency boundary に集中させる。
- timing、prepared surface consumption timing、ログ項目、DB schema、setting name、private `CreateLr2SongDbSyncInput` entry point は変えない。

完了条件:

- `Lr2SongDbSyncInputBuilder` が追加され、LR2 folder candidates 以降の selection / log / final input factory composition を担当する。
- `CreateLr2SongDbSyncInput` は root-state snapshot 採取と builder 呼び出しに近づいている。
- `Queue/Run`、status、cancel、DB write、chart_info projection、private reflection entry point は変更していない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputBuilder` を追加し、LR2 folder candidates 以降の selection / log / final input factory composition を builder へ移した。
- `CreateLr2SongDbSyncInput` は row/root/settings/scan/directory metadata/prepared/app-managed scope snapshot 採取と builder 呼び出しに近づいた。
- `lr2FolderCandidatesMs` は builder 生成後に開始し、app-managed scope 作成と folder candidate selection を含む既存計測範囲を維持した。
- `Queue/Run`、status、cancel、DB write、chart_info projection、private reflection entry point、DB schema、setting name、serialized/public surface は変更していない。
- 初回 post-review build は並列 testhost file lock で失敗したが、再実行 build は pass した。

## Completed Checkpoint: `REF-MVP-C36`

状態: completed checkpoint。

目的:

- C35 後の `Lr2SongDbSyncInputBuilder` delegate surface をレビューし、builder が直接所有できる selection helper を `BMSLibrary` から外す。
- folder info / directory entry / text file directory selection と、その selection が使う input surface merge / overlay helper を `BmsLibraryInternal` 側へ移す。
- `CreateLr2SongDbSyncInput` に残す root state / concurrency 境界を変えない。

完了条件:

- decision record が `decisions/` に追加され、C36 の範囲と触らない範囲が明確になっている。
- `Lr2SongDbSyncInputBuilder` が folder info / directory entry / text file directory selection を直接担当している。
- `BMSLibrary.CreateLr2SongDbSyncInput` から builder に渡す delegate が減っている。
- private `CreateLr2SongDbSyncInput` entry point、log item、timing、DB schema、setting name、serialized/public surface は変更していない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- [REF-MVP-C36 LR2 Sync Input Builder Helper Ownership Decision](./decisions/REF-MVP-C36_lr2_sync_input_builder_helper_ownership.md) を追加した。
- `Lr2SongDbSyncInputBuilder` が folder info / directory entry / text file directory selection を直接担当する形にした。
- selection helper が使う input surface merge / overlay / normalization helper を `BmsLibraryInternal/Lr2SongDbSyncInputSurfaceHelper.cs` へ移した。
- `CreateLr2SongDbSyncInput` から builder に渡す delegate は folder candidate / directory target / log の 3 件に減った。
- folder candidate selection、directory target selection、row/root/settings/scan/prepared/app-managed scope snapshot 採取、scan / prepared / file-diff mutable cache、private reflection entry point、DB schema、setting name、serialized/public surface は変更していない。
- LR2 targeted / full test の初回で既知の `ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots` 単発失敗があったが、単体 rerun と最終 rerun は pass した。

次にやる 1 件:

- `REF-MVP-C37: LR2 sync input builder boundary review` として、C36 後の残り delegate surface と root 残存責務を再確認する。folder candidate / directory target selection の境界を 1 ticket だけ整理できるか、または Lane C の別 workflow へ移るかを decision record 化する。production code 変更は、次の実装単位が明確に決まるまで行わない。

## Completed Checkpoint: `REF-MVP-C37`

状態: completed checkpoint。

目的:

- C36 後に `Lr2SongDbSyncInputBuilder` に残っている folder candidate / directory target delegate surface を再評価する。
- 次に LR2 sync input builder 周りを続けるか、Lane C の別 workflow へ移るかを 1 件に絞る。
- production code は変更しない。

完了条件:

- decision record が `decisions/` に追加され、次にやる 1 件が明確になっている。
- root に残すべき state / concurrency / DB 境界が明記されている。
- P0-02 と `PLAN_STATUS.md` が decision と矛盾していない。
- diff check / 静的レビューが完了している。

実装結果:

- [REF-MVP-C37 LR2 Sync Input Builder Boundary Review](./decisions/REF-MVP-C37_lr2_sync_input_builder_boundary_review.md) を追加した。
- 次は `REF-MVP-C38: LR2 sync input folder candidate selection builder ownership` に進む判断にした。
- `CreateLr2SongDbSyncAppManagedOutputScope`、row/root/settings/scan/prepared snapshot 採取、directory target selection、queue / run / status / cancel、DB write、chart_info、DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point は C38 で触らない範囲として明記した。
- production code は変更していない。

次にやる 1 件:

- `REF-MVP-C38: LR2 sync input folder candidate selection builder ownership` として、folder candidate selection を builder / internal helper 側へ移し、`CreateLr2SongDbSyncFolderCandidateSelection` delegate を builder constructor から外す。`lr2FolderCandidatesSource` の文字列、prepared merge、app-managed filtering、不完全 scope 時の incomplete candidate、`lr2FolderCandidatesMs` 計測範囲は維持する。

## Completed Checkpoint: `REF-MVP-C38`

状態: completed checkpoint。

目的:

- folder candidate selection を `Lr2SongDbSyncInputBuilder` 側へ移し、`CreateLr2SongDbSyncFolderCandidateSelection` delegate を builder constructor から外す。
- `LogEverythingScan` 相当は low-level scan log action として渡し、scan log 文言とエラー露出を維持する。
- root に残す row/root/settings/scan/prepared/app-managed scope 採取と directory target selection は変えない。

完了条件:

- `Lr2SongDbSyncInputBuilder` が folder candidate selection を直接担当している。
- `BMSLibrary.CreateLr2SongDbSyncInput` から builder に渡す delegate から `CreateLr2SongDbSyncFolderCandidateSelection` が消えている。
- `lr2FolderCandidatesSource` の文字列、prepared merge、app-managed filtering、不完全 scope 時の incomplete candidate、`lr2FolderCandidatesMs` 計測範囲が維持されている。
- app-managed output scope 採取、directory target selection、DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point は変更していない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputBuilder` が folder candidate selection を直接担当する形にした。
- `CreateLr2SongDbSyncFolderCandidateSelection` delegate を builder constructor から外し、`LogEverythingScan` は low-level scan log action として渡した。
- `CreateLr2FolderDiscoveryDirectoriesForEnumeration` の root 側未使用 wrapper を削除した。
- `BMSLibrary.cs` は 17,746 行、builder は 363 行。
- 初回 build は並列 testhost file lock で失敗したが、再実行 build は pass した。
- LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

次にやる 1 件:

- `REF-MVP-C39: LR2 sync input directory target boundary review` として、残る directory target delegate を移すか、Lane C の別 workflow へ移るかを判断する。production code 変更は、次の実装単位が明確に決まるまで行わない。

## Completed Checkpoint: `REF-MVP-C39`

状態: completed checkpoint。

目的:

- C38 後に builder constructor に残っている directory target delegate を再評価する。
- directory target selection を builder / helper 側へ移すか、Lane C の別 workflow へ移るかを 1 件に絞る。
- production code は変更しない。

完了条件:

- decision record が `decisions/` に追加され、次にやる 1 件が明確になっている。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` の共有方針が明記されている。
- root に残すべき state / concurrency / DB 境界が明記されている。
- P0-02 と `PLAN_STATUS.md` が decision と矛盾していない。
- diff check / 静的レビューが完了している。

実装結果:

- [REF-MVP-C39 LR2 Sync Input Directory Target Boundary Review](./decisions/REF-MVP-C39_lr2_sync_input_directory_target_boundary_review.md) を追加した。
- 次は `REF-MVP-C40: LR2 sync input directory target selection builder ownership` に進む判断にした。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` とその private helper は、既存 surface helper ではなく dedicated internal helper へ移す方針にした。
- `PrepareLr2FolderParentDirectoryEntrySurface` は C40 では workflow 移動せず、`BMSLibrary` 側に残して移した helper を呼ぶだけにする。
- production code は変更していない。

次にやる 1 件:

- `REF-MVP-C40: LR2 sync input directory target selection builder ownership` として、directory target selection を builder / internal helper 側へ移し、builder constructor から最後の selection delegate を外す。app-managed output scope 採取、row/root/settings/scan/prepared snapshot 採取、`PrepareLr2FolderParentDirectoryEntrySurface` workflow、DB schema、setting name、private reflection entry point は触らない。

## Completed Checkpoint: `REF-MVP-C40`

状態: completed checkpoint。

目的:

- directory target selection を `Lr2SongDbSyncInputBuilder` へ移し、builder constructor から最後の selection delegate を外す。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` とその private helper を dedicated internal helper へ移す。
- `PrepareLr2FolderParentDirectoryEntrySurface` は `BMSLibrary` 側 workflow として残し、移した helper を呼ぶだけにする。

完了条件:

- `Lr2SongDbSyncInputBuilder` が directory target selection を直接担当している。
- builder constructor から `CreateLr2SongDbSyncDirectoryTargetSelection` delegate が消えている。
- physical parent directory target helper 群が `BmsLibraryInternal` の dedicated helper に移っている。
- app-managed output scope 採取、row/root/settings/scan/prepared snapshot 採取、`PrepareLr2FolderParentDirectoryEntrySurface` workflow、DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point は変更していない。
- build / LR2 sync 関連 tests / full test / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。

実装結果:

- `Lr2SongDbSyncInputBuilder` が directory target selection を直接担当する形にした。
- builder constructor から最後の selection delegate を外した。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` と private helper を `BmsLibraryInternal/Lr2FolderPhysicalParentDirectoryTargetHelper.cs` へ移した。
- `PrepareLr2FolderParentDirectoryEntrySurface` は `BMSLibrary` 側 workflow として残し、移した helper を呼ぶ形にした。
- `BMSLibrary.cs` は 17,594 行、builder は 380 行、helper は 136 行。
- build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

次にやる 1 件:

- `REF-MVP-C41: LR2 sync input builder aftermath review` として、C40 後の `CreateLr2SongDbSyncInput` と builder boundary を再評価し、LR2 sync input builder lane を閉じて Lane C の別 workflow へ移るかを判断する。production code 変更は、次の実装単位が明確に決まるまで行わない。

## Completed Checkpoint: `REF-MVP-C41`

状態: completed checkpoint。

目的:

- C40 後の `CreateLr2SongDbSyncInput` と builder boundary を再評価する。
- LR2 sync input builder lane を閉じるか、もう 1 件だけ続けるかを 1 件に絞る。
- production code は変更しない。

完了条件:

- decision record が `decisions/` に追加され、次にやる 1 件が明確になっている。
- `CreateLr2SongDbSyncInput` に残すべき root-state / concurrency / mutable cache / DB 境界が明記されている。
- P0-02 と `PLAN_STATUS.md` が decision と矛盾していない。
- diff check / 静的レビューが完了している。

実装結果:

- [REF-MVP-C41 LR2 Sync Input Builder Aftermath Review](./decisions/REF-MVP-C41_lr2_sync_input_builder_aftermath_review.md) を追加した。
- 次は `REF-MVP-C42: LR2 sync input normal directory metadata target builder ownership` に進む判断にした。
- `CreateLr2SongDbSyncDirectoryMetadataTargets` は root mutable state / DB boundary を直接読まない target selection と判断した。
- row/root/settings/scan/prepared/app-managed scope 採取は `BMSLibrary` に残す root-state / concurrency / mutable cache / DB-read warning boundary として明記した。
- production code は変更していない。

次にやる 1 件:

- `REF-MVP-C42: LR2 sync input normal directory metadata target builder ownership` として、normal directory metadata target selection を builder ownership へ移し、`directoryTargetsMs` の計測範囲と log values を維持する。row/root/settings/scan/prepared/app-managed scope 採取、DB schema、setting name、private reflection entry point は触らない。
