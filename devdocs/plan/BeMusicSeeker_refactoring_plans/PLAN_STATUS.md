# PLAN_STATUS

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

最終更新日: 2026-07-08

## Refactoring MVP State

Release Freeze: active。

`git push`、Git tag、GitHub Release、Release draft、publish / release script 実行は禁止。Refactoring MVP Gate 通過前に release / version 関連ファイルは原則触らない。

## Active Lanes

| Lane | Active ticket | 状態 | 次に読む |
|---|---|---|---|
| A: MainWindowViewModel shell 化 | `REF-MVP-A1: Playlist detail build workflow extraction` | completed checkpoint | [P0-01](./P0-01_MainWindowViewModel_リファクタリング計画.md) |
| B: MainWindow code-behind / XAML MVVM 移行 | `REF-MVP-B1: MainWindow event handler inventory and first command bridge` | completed checkpoint | [P0-03](./P0-03_MainWindow_UI_MVVM移行計画.md) |
| C: BMSLibrary domain facade 化 | `REF-MVP-C66: maintenance hydration owner attach service seam` | completed checkpoint | [P0-02](./P0-02_BMSLibrary_ドメインFacade化計画.md) |
| D: .NET 10 migration readiness | `REF-MVP-D1: .NET 10 blocker inventory in devdocs` | completed checkpoint | [P0-04](./P0-04_DotNet10_移行準備と依存関係整理計画.md) |

Codex は毎回、Refactoring MVP Gate に最も近づく slice を選ぶ。現時点の推奨順は Lane C → Lane B 後続候補 → Lane A 後続候補 → Lane D 後続候補。

## Ticket Details

### `REF-MVP-A1: Playlist detail build workflow extraction`

状態: completed checkpoint。Lane A 後続は、P0-03 の DataContext / command bridge 前提と root helper 残存状況を見て workflow 単位で再計画する。

目的:

- playlist detail build の request scheduling、source build、view apply、main view apply、finalize timing を、DTO 単位ではなく workflow 単位で root から coordinator / workspace へ移す。
- root `MainWindowViewModel` は request 発行、lifecycle、dialog / progress bridge、UI thread 境界だけを持つ。
- 挙動変更、XAML binding 変更、serialized value 変更、DB schema 変更はしない。

完了条件:

- playlist detail build workflow の主要処理が root から移動している。
- root 側に残る処理が orchestration / bridge として説明できる。
- 既存 UI 挙動が変わらない。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-B1: MainWindow event handler inventory and first command bridge`

状態: completed checkpoint。Lane B 後続は workflow 単位で再計画する。

目的:

- `MainWindow.cs` の巨大 event handler と `async void` を分類する。
- `tableContextMenuOpened` の state calculation を presentation service / command args DTO へ移す最初の slice を実装する。
- code-behind は UI 型の値を変換し、command を呼ぶだけに近づける。

完了条件:

- `MainWindow.cs` の event handler inventory が `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/` にある。
- 最初の event handler slice が ViewModel command / presentation service へ移っている。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C1: BMSLibrary source-text helper and facade split foundation`

状態: completed checkpoint。`REF-MVP-C2` も completed checkpoint。

目的:

- `SourceTextTestHelper.ReadBmsLibrarySourceText()` を追加する。
- `BMSLibrary.cs` 単体配置に依存する source-text / private reflection test を分割耐性のある形へ移す。
- その後、partial split または既存 `BmsLibraryInternal` service への workflow 移動を進められる状態にする。

完了条件:

- source-text test が `BMSLibrary.cs` 単体配置に過度に依存していない。
- `BMSLibrary` facade split の blocker が 1 つ減っている。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C2: BMSLibrary package install facade partial split`

状態: completed checkpoint。Lane C 後続は `BMSLibrary.PackageInstall.cs` に分離した facade / orchestration を見て、package install coordinator 化を進めるか、他 workflow を先に分けるかを 1 ticket だけ active 化する。

目的:

- `BMSLibrary.cs` に残っている package install facade / orchestration の連続ブロックを `BMSLibrary.PackageInstall.cs` へ挙動変更なしで分離する。
- 既存 `BmsLibraryPackageInstallService` / `BmsLibraryInstallEstimationService` への委譲関係を維持し、service extraction の reviewability を上げる。
- public API、lock 順序、dialog / file mutation / DB mutation timing、Settings / schema / serialized value は変えない。

完了条件:

- package install public/internal facade と関連 helper が dedicated partial にまとまっている。
- `BMSLibrary.cs` の package install 責務が明確に減っている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C3: ForceInstallPendingPackages coordinator seam`

状態: completed checkpoint。

目的:

- `ForceInstallPendingPackages` の lock / LR2 sync block / confirm callback / pending removal / installed package merge / logging を coordinator seam へ移す。
- root `BMSLibrary` は public API entry、dialog confirmation、`installChartPackages` delegate、state apply bridge を提供する。
- `installChartPackages` 本体、estimated install batch、auto install、pending cleanup / rename は触らない。

完了条件:

- `ForceInstallPendingPackages` public/internal API は維持されている。
- lock 順序、dialog timing、pending removal、installed add、log 文言が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C4: Pending estimated install coordinator seam`

状態: completed checkpoint。

目的:

- `InstallPendingPackagesToEstimatedDestinations` の lock / LR2 sync block / batch plan / DB row delete / pending and installed collection apply / maintenance / inline chart_info / cleanup warning / performance logging を coordinator seam へ移す。
- root `BMSLibrary` は public API entry、`installChartPackages` delegate、state apply bridge、maintenance / inline chart_info bridge を提供する。
- `installChartPackages` 本体、`InstallChartPackagesAuto`、`OverwritePendingInstalledOnlyPackagesResources`、pending cleanup / rename は触らない。

完了条件:

- `InstallPendingPackagesToEstimatedDestinations` public API、null guard、LR2 sync block、lock 順序、dialog timing、log 文言が維持されている。
- post-processing の順序、特に `dbGateway.DeleteInstallRows`、resource health delta suppression、pending/installed apply、maintenance、inline chart_info、cleanup warning が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C5: Advanced pending resource overwrite coordinator seam`

状態: completed checkpoint。

目的:

- `OverwritePendingInstalledOnlyPackagesResources` の lock / installed lookup snapshot / skip detail / estimated install callback / cleanup callback / pending removal / summary logging / deferred progress flush を coordinator seam へ移す。
- root `BMSLibrary` は public API entry、install estimation bridge、estimated install delegate、cleanup bridge、dialog / logging / pending mutation bridge を提供する。
- `InstallPendingPackagesToEstimatedDestinations` 本体、pending source cleanup、zero-note rename は触らない。

完了条件:

- `OverwritePendingInstalledOnlyPackagesResources` public API、null guard、options snapshot timing、lock 順序、deferred progress flush、summary log 文言が維持されている。
- install failure exception handling と dialog timing が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C6: Pending package source deletion coordinator seam`

状態: completed checkpoint。

目的:

- `DeletePendingPackageSources` の lock / dedup / service execution / failure dialog / pending removal / summary logging / deferred progress flush を coordinator seam へ移す。
- root `BMSLibrary` は public API entry、file mutation options、dialog / logging / pending mutation bridge を提供する。
- pending chart deletion、zero-note rename、folder/file operation 全般は触らない。

完了条件:

- `DeletePendingPackageSources` public API、null guard、lock 順序、requested/permanent log、failure warning/dialog、summary log、deferred progress flush が維持されている。
- file mutation service と options の渡し方が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C7: Pending zero-note rename coordinator seam`

状態: completed checkpoint。

目的:

- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` の snapshot fallback / lock / service execution / failure dialog / pending chart removal / summary logging / deferred progress flush を coordinator seam へ移す。
- root `BMSLibrary` は internal API entry、rename mutation bridge、dialog / logging / pending mutation bridge を提供する。
- pending source deletion、pending chart deletion、folder/file operation 全般は触らない。

完了条件:

- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` internal API、target null fallback、lock 順序、failure dialogs、pending chart removal、summary log、deferred progress flush が維持されている。
- `ProcessInvalidExtensionRename(..., removeFromLibraryOnSuccess: false)` の呼び方が維持されている。
- build / package install 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C8: Normal library refresh publisher extraction`

状態: completed checkpoint。

目的:

- normal library refresh notification の queue / version / reset barrier / batch aggregation を publisher seam へ移す。
- root `BMSLibrary` は `NormalLibraryRefreshNotificationVersion`、`GetNormalLibraryRefreshNotificationsAfter`、`RaisePropertyChanged` timing、`OwnedChartCollectionMutationResult` から publisher input を組み立てる bridge を提供する。
- LR2 sync、maintenance hydration、file operation、MainWindowViewModel 側の notification consumption は触らない。

完了条件:

- `NormalLibraryRefreshNotificationVersion` と `GetNormalLibraryRefreshNotificationsAfter` の public/internal surface が維持されている。
- reset barrier、effect aggregation、install-destination changed charts の distinct、remove-only storage row delta、clear-on-failure、property notification timing が維持されている。
- build / normal refresh 関連 tests / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C9: Playlist reference index manager seam`

状態: completed checkpoint。

目的:

- playlist reference display の index state / lock / table replace / remove / synchronize を manager seam へ移す。
- root `BMSLibrary` は既存 public/internal API、playlist reference apply workflow、pending/package/library snapshot bridge を提供する。
- playlist apply workflow 全体、MainWindowViewModel consumption、BMSTable persistence、source text tests の意図は触らない。

完了条件:

- `GetPlaylistReferenceDisplay` overloads、`AddReferenceBMSTables*`、`ReplaceReferenceBMSTable`、`RemoveReferenceBMSTables*`、`SynchronizeReferenceBMSTables` の surface と挙動が維持されている。
- root `BMSLibrary` が `playlistReferenceIndexLock` / `playlistReferenceIndex` を直接持たない。
- playlist reference 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C10: Playlist reference apply coordinator seam`

状態: completed checkpoint。

目的:

- playlist reference map build / library chart apply / pending chart apply / package chart apply / replace target logging を coordinator seam へ移す。
- root `BMSLibrary` は既存 public/internal API、playlist reference manager bridge、snapshot / lock bridge、logging bridge を提供する。
- MainWindowViewModel consumption、BMSTable persistence、playlist database 更新、chart row refresh は触らない。

完了条件:

- `AddReferenceBMSTables*`、`ReplaceReferenceBMSTable`、`RemoveReferenceBMSTables*`、`SynchronizeReferenceBMSTables` の public/internal surface と log key が維持されている。
- pending bmson entry を materialize しない既存挙動が維持されている。
- playlist reference 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C11: LR2 song.db sync request coordinator seam`

状態: completed checkpoint。

目的:

- `QueueLr2SongDbSync`、`TryRunLr2SongDbSyncDataPreparation`、`CleanupLr2SongDbSyncStartupScanBlockerFolderRows`、`PublishLr2SongDbSyncExternalStageProgress` の実行要求・準備・状態公開 orchestration を coordinator seam へ移す。
- `BMSLibrary` には既存 public/internal API、options / DB / status / scheduler / input creation bridge を残す。
- `CreateLr2SongDbSyncInput`、`RunLr2SongDbSync` 本体、file diff scan surface、chart_info hydration / resolver、DB schema / setting name は触らない。

完了条件:

- 対象 API の surface と log key が維持されている。
- prepare reservation、mutation block、shutdown skip、startup scheduler、status publish の順序が維持されている。
- LR2 sync 関連 tests / source-text 代表 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C12: LR2 song.db sync run coordinator seam`

状態: completed checkpoint。

目的:

- `RunLr2SongDbSync` の preflight / service request construction / result logging / completion-failure handling を coordinator seam へ移す。
- `BMSLibrary` には cancellation token / input construction / chart_info resolver / projection / status mutation / startup task reporting の host bridge を残す。
- `CreateLr2SongDbSyncInput` 本体、scan surface、file diff freshness、chart_info hydration internals、DB schema / setting name は触らない。

完了条件:

- `lr2_song_db_sync` の log key、preflight stage、service request fields、completion / incomplete / cancelled / failed の status transition が維持されている。
- `CreateLr2SongDbSyncInput` と scan surface helper は今回移さず、後続 ticket で詳細化する。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C13: LR2 song.db sync input DTO boundary`

状態: completed checkpoint。

目的:

- `BMSLibrary.Lr2SongDbSyncInput` nested type 依存をやめ、LR2 sync coordinator / host が top-level internal DTO を扱える境界にする。
- `CreateLr2SongDbSyncInput` の private reflection 入口、property 名、service request fields、DB schema / setting name は変えない。
- `CreateLr2SongDbSyncInput` 本体と scan surface helper の移動は今回の完了条件に含めず、C13 完了後に builder / scan surface follow-up として再計画する。

完了条件:

- `Lr2SongDbSyncInput` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- `ILr2SongDbSyncRequestHost` / `Lr2SongDbSyncRequestCoordinator` が `BMSLibrary.Lr2SongDbSyncInput` に依存していない。
- 既存 reflection tests が private `CreateLr2SongDbSyncInput` 入口と public property surface 経由で通る。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C14: LR2 song.db sync scan surface DTO boundary`

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncScanSurfaceSnapshot` を top-level internal DTO に移し、input builder / scan surface follow-up の前提を作る。
- scan surface の取得、適用、世代判定、prepared surface merge、DB schema / setting name は変えない。
- `Lr2SongDbSyncFileDiffFreshnessSnapshot` や app-managed output scope の移動は今回の完了条件に含めず、C14 完了後に必要なら再計画する。

完了条件:

- `Lr2SongDbSyncScanSurfaceSnapshot` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- scan surface generation / root directories / directory entries / LR2 folder candidate fields の property surface と null fallback が維持されている。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C15: LR2 song.db sync app-managed output scope DTO boundary`

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncAppManagedOutputScope` を top-level internal DTO に移し、input builder follow-up の前提を作る。
- private `CreateLr2SongDbSyncAppManagedOutputScope` 入口、public property 名、playlist / DB schema / setting name は変えない。
- app-managed output scope の生成ロジック自体は今回移さない。

完了条件:

- `Lr2SongDbSyncAppManagedOutputScope` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- `Directories` / `FilePaths` / `PruneExcludedPaths` / `IsComplete` の property surface と null fallback が維持されている。
- 既存 reflection tests が private `CreateLr2SongDbSyncAppManagedOutputScope` 入口と public property surface 経由で通る。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C16: LR2 song.db sync file-diff freshness DTO boundary`

状態: completed checkpoint。

目的:

- `BMSLibrary` nested `Lr2SongDbSyncFileDiffFreshnessSnapshot` を top-level internal DTO に移し、input freshness / transient skip path 判定の nested state 依存を切る。
- file-diff freshness の capture / clear / verification ロジック、log key、DB schema / setting name は変えない。
- `CreateLr2SongDbSyncInput` 本体の builder 化は今回の完了条件に含めず、C16 完了後に再計画する。

完了条件:

- `Lr2SongDbSyncFileDiffFreshnessSnapshot` が `BMSLibrary` nested type ではなく top-level internal DTO になっている。
- all count/version properties と `TransientSongRowSkipPaths` の `HashSet<string>` / `StringComparer.OrdinalIgnoreCase` / null filtering が維持されている。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C17: LR2 song.db sync input row snapshot boundary`

状態: completed checkpoint。

目的:

- `CreateLr2SongDbSyncInput` から `_BMSFiles` / `rwlockBMSFilesInitializedAll` / `StorageRowsVersionSnapshot` の直接扱いを外し、top-level input row snapshot DTO へ閉じ込める。
- row snapshot の chart path distinct、song row capture、owned collection / storage row version capture の順序と lock 範囲は変えない。
- `CreateLr2SongDbSyncInput` 本体の builder 化は今回の完了条件に含めず、C17 完了後に再計画する。

完了条件:

- `Lr2SongDbSyncInputRowSnapshot` が top-level internal DTO として追加されている。
- `CreateLr2SongDbSyncInput` が row snapshot DTO 経由で chart paths / song rows / version を読む。
- `GetCurrentLr2SongDbSyncScanSurface` が private `StorageRowsVersionSnapshot` ではなく row snapshot DTO から version を判定できる。
- LR2 sync 関連 tests / build / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-D1: .NET 10 blocker inventory in devdocs`

状態: completed checkpoint。後続は [REF-MVP-D1 inventory](./inventory/REF-MVP-D1_dotnet10_blockers.md) を見て、Settings boundary、native load layout、WPF / WinForms boundary のいずれか 1 件だけを active ticket 化する。

目的:

- TFM は変更しない。
- `Settings.Default`、`System.Configuration`、`app.config`、HintPath DLL、native DLL、WPF + WinForms、P/Invoke、external process host の blocker inventory を `.tmp` ではなく `devdocs` 管理下に置く。
- .NET 10 本移行ではなく、Refactoring MVP Gate のための blocker 可視化に限定する。

完了条件:

- blocker inventory が git 管理下にある。
- 各 blocker について、該当箇所、影響、refactor 前にできる対処、.NET 10 移行時に検討する対処が書かれている。
- TFM 変更や release 作業をしていない。
- docs のリンクが壊れていない。
- `git diff --check` が通る。

## Guardrail

| 対象 | 現状目安 | Guardrail | 次の extraction 候補 |
|---|---:|---:|---|
| `MainWindowViewModel.cs` | 23,413 行 | 8,000 行以下 | playlist detail build workflow、play history、playback、settings save、library refresh |
| `MainWindow.cs` | 10,289 行 | 5,000 行以下 | `tableContextMenuOpened`、async void 本体、drag/drop、URL download |
| `BMSLibrary.cs` | 16,992 行 | 12,000 行以下 | folder/file operation follow-up、maintenance result apply contract prep、package install follow-up |
| `BMSPlaylist.cs` | P1 対象 | 6,000 行以下 | P0/P1 境界で再計画 |

Guardrail 超過は現時点では既知。残す理由は「MVP active lanes の extraction 前であるため」。次の extraction 候補は上表を正本とする。

## Blocked / Waiting

| 項目 | 状態 |
|---|---|
| release / version 作業 | Refactoring MVP Gate 通過まで freeze |
| `net10.0-windows` 本移行 | Lane D inventory と Lane A/B/C の責務分離後に dry-run plan を作る |
| 旧 `L-3c-17` | `REF-MVP-A1` の subtask に格下げ。独立 ticket としては扱わない |
| `.tmp` inventories | 継続判断・blocker inventory は `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/` へ移す |

## Latest Completed Work

`REF-MVP-C26` として C25 で切った `lr2_song_db_sync_input_surface` log formatter を `BmsLibraryInternal/Lr2SongDbSyncInputSurfaceLogFormatter.cs` の top-level internal formatter へ移した。`BMSLibrary.cs` は 18,375 行、追加 formatter は 89 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C27` として `CreateLr2SongDbSyncInput` の scan surface / miss reason / reused LR2 folder surface / generation を `BmsLibraryInternal/Lr2SongDbSyncScanSurfaceSelection.cs` の dedicated selection DTO 経由へ移した。`BMSLibrary.cs` は 18,386 行、追加 DTO は 16 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C28` として `CreateLr2SongDbSyncInput` の normal directory metadata targets / LR2 folder parent directory targets / merged directory entry targets を `BmsLibraryInternal/Lr2SongDbSyncDirectoryTargetSelection.cs` の dedicated selection DTO 経由へ移した。`directoryTargetsMs` は normal directory metadata target 作成部分の計測として維持した。`BMSLibrary.cs` は 18,416 行、追加 DTO は 15 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C29` として `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が scan surface selection / directory target selection を直接受け取る形にした。`CreateLr2SongDbSyncInput` の log 専用 alias は減り、ログ項目名・順序・値は維持した。`BMSLibrary.cs` は 18,402 行、formatter は 90 行。build、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。初回 LR2 sync 関連 tests で `ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots` が 1 回失敗したが、同テスト単体 rerun と LR2 targeted rerun は pass した。

`REF-MVP-C30` として `Lr2SongDbSyncInputFactory.Create` が scan surface selection / directory target selection を直接受け取る形にした。constructor に渡す directory metadata target copy と scan surface generation は維持した。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C31` として `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` に渡していた Stopwatch 群を `BmsLibraryInternal/Lr2SongDbSyncInputSurfaceTimings.cs` の dedicated timing DTO 経由にした。ログ timing 項目名・順序・値は維持した。`BMSLibrary.cs` は 18,386 行、formatter は 80 行、timing DTO は 36 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C32` として C18-C31 後の `CreateLr2SongDbSyncInput` composition surface をレビューし、decision record [REF-MVP-C32 LR2 Sync Input Composition Surface Decision](./decisions/REF-MVP-C32_lr2_sync_input_composition_surface.md) を追加した。現時点では root orchestration として許容し、full service / builder extraction の前に `REF-MVP-C33: LR2 sync input prepared surface selection boundary` を 1 ticket だけ挟む。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C33` として `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` と related selection helpers が prepared surface selection を直接受け取る形にした。`CreateLr2SongDbSyncInput` から prepared surface log/helper 専用 alias を減らし、prepared surface consumption timing とログ値は維持した。`BMSLibrary.cs` は 18,371 行、formatter は 78 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C34` として C33 後の extraction readiness をレビューし、decision record [REF-MVP-C34 LR2 Sync Input Builder Extraction Decision](./decisions/REF-MVP-C34_lr2_sync_input_builder_extraction.md) を追加した。次は service extraction ではなく `REF-MVP-C35: LR2 sync input builder extraction` とし、root-state / concurrency 境界は BMSLibrary に残す。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C35` として `BmsLibraryInternal/Lr2SongDbSyncInputBuilder.cs` を追加し、LR2 folder candidates 以降の selection / log / final input factory composition を builder へ移した。`CreateLr2SongDbSyncInput` は root-state snapshot 採取と builder 呼び出しに近づいた。`BMSLibrary.cs` は 18,281 行、builder は 141 行。初回 post-review build は並列 testhost file lock で失敗したが、再実行 build は pass。LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C36` として C35 後の builder delegate surface をレビューし、decision record [REF-MVP-C36 LR2 Sync Input Builder Helper Ownership Decision](./decisions/REF-MVP-C36_lr2_sync_input_builder_helper_ownership.md) を追加した。`Lr2SongDbSyncInputBuilder` が folder info / directory entry / text file directory selection を直接担当し、selection helper が使う input surface merge / overlay helper を `BmsLibraryInternal/Lr2SongDbSyncInputSurfaceHelper.cs` へ移した。`CreateLr2SongDbSyncInput` から builder に渡す delegate は folder candidate / directory target / log の 3 件に減った。`BMSLibrary.cs` は 17,866 行、builder は 238 行、helper は 326 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。LR2 targeted / full test の初回で既知の `ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots` 単発失敗があったが、単体 rerun と最終 rerun は pass した。

`REF-MVP-C37` として C36 後の残り builder delegate surface をレビューし、decision record [REF-MVP-C37 LR2 Sync Input Builder Boundary Review](./decisions/REF-MVP-C37_lr2_sync_input_builder_boundary_review.md) を追加した。次は directory target selection ではなく `REF-MVP-C38: LR2 sync input folder candidate selection builder ownership` に進む判断にした。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C38` として folder candidate selection を `Lr2SongDbSyncInputBuilder` へ移し、`CreateLr2SongDbSyncFolderCandidateSelection` delegate を builder constructor から外した。`LogEverythingScan` は low-level scan log action として渡し、`lr2FolderCandidatesSource` 文字列、prepared merge、app-managed filtering、不完全 scope 時の incomplete candidate、`lr2FolderCandidatesMs` 計測範囲は維持した。`BMSLibrary.cs` は 17,746 行、builder は 363 行。初回 build は並列 testhost file lock で失敗したが、再実行 build は pass。LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C39` として C38 後に残る directory target delegate をレビューし、decision record [REF-MVP-C39 LR2 Sync Input Directory Target Boundary Review](./decisions/REF-MVP-C39_lr2_sync_input_directory_target_boundary_review.md) を追加した。次は `REF-MVP-C40: LR2 sync input directory target selection builder ownership` に進む判断にした。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C40` として directory target selection を `Lr2SongDbSyncInputBuilder` へ移し、builder constructor から最後の selection delegate を外した。`CreateLr2FolderPhysicalParentDirectoryMetadataTargets` と private helper は `BmsLibraryInternal/Lr2FolderPhysicalParentDirectoryTargetHelper.cs` の dedicated helper へ移し、`PrepareLr2FolderParentDirectoryEntrySurface` は `BMSLibrary` 側 workflow として残した。`BMSLibrary.cs` は 17,594 行、builder は 380 行、helper は 136 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C41` として C40 後の `CreateLr2SongDbSyncInput` と builder boundary をレビューし、decision record [REF-MVP-C41 LR2 Sync Input Builder Aftermath Review](./decisions/REF-MVP-C41_lr2_sync_input_builder_aftermath_review.md) を追加した。次は `REF-MVP-C42: LR2 sync input normal directory metadata target builder ownership` に進む判断にした。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C42` として normal directory metadata target selection を `Lr2SongDbSyncInputBuilder` へ移し、`CreateLr2SongDbSyncInput` から `directoryMetadataTargets` local と helper を外した。`directoryTargetsMs` は builder 内で同じ normal directory metadata target 作成時間を測り、`lr2FolderCandidatesMs` には directory target 作成時間が混入しないよう `lr2FolderCandidatesStopwatch` を一時停止している。`BMSLibrary.cs` は 17,577 行、builder は 394 行。build、LR2 sync 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C43` として C42 後の `CreateLr2SongDbSyncInput` と `Lr2SongDbSyncInputBuilder` boundary をレビューし、decision record [REF-MVP-C43 LR2 Sync Input Builder Lane Closure](./decisions/REF-MVP-C43_lr2_input_builder_lane_closure.md) を追加した。明確な builder ownership cleanup は残っていないため LR2 sync input builder lane は閉じ、次は `REF-MVP-C44: maintenance hydration coordinator seam` に進む判断にした。production code は変更していない。diff check と静的レビューは完了。

`REF-MVP-C44` として maintenance hydration の queue / worker lifecycle を `BmsLibraryInternal/MaintenanceHydrationCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.MaintenanceHydrationHost.cs` で observable state、startup task reporting、shutdown skip、scheduler bridge、maintenance table load、result apply を提供する host になっている。`ApplyMaintenanceHydrationResult` 内部、resource health index mutation、maintenance DB schema、warning projection、`installable_maintenance_deferred` は触っていない。`BMSLibrary.cs` は 17,424 行、coordinator は 175 行、host は 128 行。build、maintenance hydration / initialization 関連 tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C45` として [maintenance hydration result apply boundary](./decisions/REF-MVP-C45_maintenance_hydration_result_apply_boundary.md) をレビューした。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は root lock、owner view、resource health nested mutation、maintenance DB cleanup、private reflection / source-text tests が重なるため、現時点では移動しない。production code は変更していない。次は `installable_maintenance_deferred` の queue / worker lifecycle を coordinator seam へ移す。

`REF-MVP-C46` として `installable_maintenance_deferred` の queue / worker lifecycle を `BmsLibraryInternal/InstallableMaintenanceDeferredCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.InstallableMaintenanceHost.cs` で request state、snapshot 作成、`setModeAndCommitToDB`、`setInstallableMaintenanceInfo("installable_maintenance_deferred")`、write-lock flag reset、logging を提供する host になっている。`setInstallableMaintenanceInfo` 内部、resource health index mutation、maintenance DB schema、warning projection、`ApplyMaintenanceHydrationResult` は触っていない。`BMSLibrary.cs` は 17,215 行、coordinator は 298 行、host は 161 行。build、targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。初回 full test で `QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean` が 1 回失敗したが、同テスト単体 rerun と full test rerun は pass した。

`REF-MVP-C47` として [Lane C boundary after maintenance deferred seams](./decisions/REF-MVP-C47_lane_c_boundary_after_maintenance_deferred.md) をレビューした。maintenance result apply は C45 の blocker が残るためまだ移動しない。`MergeChartDirectory` と `installChartPackages` は横断範囲が大きいため初手にしない。次は `RenameChartFolder` の単一フォルダ move workflow を coordinator seam へ移す。

`REF-MVP-C48` として `RenameChartFolder` / 単一 folder move workflow を `BmsLibraryInternal/LibraryFolderMoveCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.LibraryFolderMoveHost.cs` で LR2 sync block、root folder guard、dialog、lock boundary、file move / reverse lookup、folder move delta build、delta apply を提供する host になっている。`MoveLibraryRootFolder` は plan 生成と lock semantics を維持し、各 plan の単一 move apply だけ同じ coordinator helper を呼ぶ形にした。`MergeChartDirectory`、`MoveChartPackageFiles`、package install、maintenance result apply、resource health mutation は触っていない。`BMSLibrary.cs` は 17,133 行、coordinator は 138 行、host は 112 行。build、folder/dialog/source-text targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。初回 full test で `CreateLr2SongDbSyncInputWithoutScanSurface_KeepsPhysicalLr2FoldersWhenNoManagedPlaylistScopeExists` が 1 回失敗したが、同テスト単体 rerun と full test rerun は pass した。

`REF-MVP-C49` として [folder/file operation follow-up boundary](./decisions/REF-MVP-C49_folder_file_operation_followup_boundary.md) をレビューした。C48 の coordinator seam が `MoveLibraryRootFolder` の plan ごとの単一 move apply で既に使われているため、次は `MoveLibraryRootFolder` 全体を同じ coordinator seam へ寄せる。`RemoveLibraryCharts`、`RenameBMSFilesExtensions`、`MergeChartDirectory` は C50 後に再評価する。

`REF-MVP-C50` として `MoveLibraryRootFolder` 全体を `LibraryFolderMoveCoordinator` へ移した。root `BMSLibrary` は `BMSLibrary.LibraryFolderMoveHost.cs` で non-null chart list 作成、root-folder move plan build、destination root / drive root dialog、lock boundary、single-folder move apply host を提供する形になった。`RenameChartFolder`、`MergeChartDirectory`、package install、maintenance result apply、resource health mutation は触っていない。`BMSLibrary.cs` は 17,093 行、coordinator は 199 行、host は 133 行。build、folder/dialog/source-text targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。初回 full test で `QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean` が 1 回失敗したが、同テスト単体 rerun と full test rerun は pass した。

`REF-MVP-C51` として [folder/file operation boundary after root folder move](./decisions/REF-MVP-C51_folder_file_operation_boundary_after_root_move.md) をレビューした。次は `RemoveLibraryCharts` の LR2 sync block、lock boundary、confirmation、service call、log、reverse lookup warmup、delta apply、failure dialog を dedicated coordinator seam へ移す。`MergeChartDirectory`、`MoveChartPackageFiles`、`RenameBMSFilesExtensions` は C52 後に再評価する。production code は変更していない。

`REF-MVP-C52` として `RemoveLibraryCharts` の orchestration を `BmsLibraryInternal/LibraryChartRemovalCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.LibraryChartRemovalHost.cs` で LR2 sync block、lock boundary、`DeleteLibraryCharts` service call、whole-folder confirmation、log、reverse lookup warmup、delta apply、failure dialogs を提供する host になっている。`DeleteLibraryCharts` service、`MergeChartDirectory`、`MoveChartPackageFiles`、`RenameBMSFilesExtensions`、package install、maintenance result apply、resource health mutation は触っていない。`BMSLibrary.cs` は 17,048 行、coordinator は 69 行、host は 81 行。build、deletion targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C53` として [invalid extension rename boundary](./decisions/REF-MVP-C53_invalid_extension_rename_boundary.md) をレビューした。次は normal library 側の `RenameBMSFilesExtensions` と manual pending 側の `RenamePendingBmsFormatChartFileExtensions` を同じ invalid extension rename coordinator seam へ移す。`RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` は coordinator 済みのため回帰確認対象に留める。production code は変更していない。

`REF-MVP-C54` として `RenameBMSFilesExtensions` と `RenamePendingBmsFormatChartFileExtensions` の orchestration を `BmsLibraryInternal/InvalidExtensionRenameCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.InvalidExtensionRenameHost.cs` で LR2 sync block、normal/pending lock boundary、service calls、failure dialogs、normal delta apply、pending chart removal、summary logs を提供する host になっている。`MoveChartPackageFiles`、`MergeChartDirectory`、`installChartPackages`、pending zero-note rename behavior、maintenance result apply、resource health mutation は触っていない。`BMSLibrary.cs` は 16,992 行、coordinator は 78 行、host は 108 行。build、rename targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C55` として [package move / merge boundary after rename](./decisions/REF-MVP-C55_package_move_merge_boundary_after_rename.md) をレビューした。次は `MoveChartPackageFiles` adapter を explicit package move seam にする。`MergeChartDirectory`、`FixInstallationDirectoryCharts`、`installChartPackages` follow-up、maintenance result apply contract prep は C56 後に再評価する。production code は変更していない。

`REF-MVP-C56` として `MoveChartPackageFiles` adapter を `BmsLibraryInternal/PackageMoveCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.PackageMoveHost.cs` で current options snapshot、auto folder naming、displayed exception message、file mutation service/options、dialog service、performance log callback を提供する host になっている。`BmsLibraryPackageInstallService.MovePackageFiles` core behavior、`MergeChartDirectory`、`FixInstallationDirectoryCharts`、`installChartPackages` callback 契約は触っていない。`BMSLibrary.PackageInstall.cs` は 1,628 行、coordinator は 54 行、host は 37 行。build、package move / merge / repair targeted tests、format、diff check、Roslynator 対象確認、静的レビュー、full test は完了。

`REF-MVP-C57` として [repair / merge / install boundary after package move](./decisions/REF-MVP-C57_repair_merge_install_boundary_after_package_move.md) をレビューした。次は C56 の package move seam を直接使う `FixInstallationDirectoryCharts` を coordinator seam へ移す。`MergeChartDirectory`、`installChartPackages` follow-up、maintenance result apply contract prep は C58 後に再評価する。production code は変更していない。

`REF-MVP-C58` として `FixInstallationDirectoryCharts` の orchestration を `BmsLibraryInternal/LibraryFixInstallationCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.FixInstallationHost.cs` で LR2 sync block、lock boundary、installed hash snapshot、package move callback、duplicate confirmation、delta apply、duplicate source removal、maintenance apply を提供する host になっている。`BmsLibraryLibraryFileOperationsService.FixInstallationDirectory` core behavior、`MoveChartPackageFiles` seam、`RemoveLibraryCharts` seam、`MergeChartDirectory`、`installChartPackages` callback 契約、maintenance result apply は触っていない。`BMSLibrary.cs` は 16,950 行、coordinator は 75 行、host は 85 行。build、repair / package install / duplicate / context menu targeted tests、format、diff check、Roslynator warning、静的レビュー、full test は完了。

`REF-MVP-C59` として [merge / install boundary after repair seam](./decisions/REF-MVP-C59_merge_install_boundary_after_repair.md) をレビューした。次は `MergeChartDirectory` の LR2 sync block、lock boundary、prepare service call、source unregister、reverse lookup remove/add、package move、destination scan、reference delta apply、DB upsert、maintenance apply、final state apply を coordinator seam へ移す。`installChartPackages` follow-up と maintenance result apply contract prep は C60 後に再評価する。production code は変更していない。

`REF-MVP-C60` として `MergeChartDirectory` の orchestration を `BmsLibraryInternal/LibraryMergeDirectoryCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.MergeDirectoryHost.cs` で LR2 sync block、lock boundary、prepare service call、source unregister delta apply、reverse lookup remove/add、package move、failure dialog、DB upsert、maintenance apply、final state apply を提供する host になっている。`PrepareMergeDirectory` core behavior、`MoveChartPackageFiles` seam、`installChartPackages` callback 契約、maintenance result apply は触っていない。source-text test は root method body 固定から coordinator / host 検査へ更新した。`BMSLibrary.cs` は 16,724 行、coordinator は 265 行、host は 174 行。build、merge / package install / repair / context menu targeted tests、format、diff check、Roslynator warning、静的レビュー、full test は完了。

`REF-MVP-C61` として [install / maintenance boundary after merge seam](./decisions/REF-MVP-C61_install_maintenance_boundary_after_merge.md) をレビューした。次は `installChartPackages` の LR2 sync block、package filtering、InstallPackages callbacks、DB upsert、maintenance apply/defer、score update、state apply、reverse lookup add、installed package registration、performance log、inline chart_info build を coordinator seam へ移す。maintenance result apply contract prep は C62 後に再評価する。production code は変更していない。

`REF-MVP-C62` として `installChartPackages` の orchestration を `BmsLibraryInternal/PackageInstallCoordinator.cs` へ移した。root `BMSLibrary` は `BMSLibrary.PackageInstallHost.cs` で LR2 sync block、`InstallPackages` service call、package move callback、DB upsert、maintenance apply/defer、score update、estimated batch targets、state apply、reverse lookup add、installed package registration、performance log、inline chart_info build を提供する host になっている。`BmsLibraryPackageInstallService.InstallPackages` core behavior、`MoveChartPackageFiles` seam contract、pending estimated install / force install caller contract、maintenance result apply は触っていない。`BMSLibrary.PackageInstall.cs` は 1,534 行、coordinator は 145 行、host は 136 行。build、package install / pending / merge / repair / context menu targeted tests、format、diff check、Roslynator warning、静的レビュー、full test は完了。初回 full test で既知の `ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots` 単発失敗があったが、単体 rerun と最終 full test rerun は pass した。

`REF-MVP-C63` として [package install lane closure review](./decisions/REF-MVP-C63_package_install_lane_closure_review.md) をレビューした。次は `ApplyEstimatedInstallBatchLibraryState` の null guard、state apply、affected directories filtering、destination scan、reverse lookup add / warmup、scan failure warning、reverse lookup mutation result return を coordinator seam へ移す。maintenance result apply contract prep は C64 後に再評価する。production code は変更していない。

`REF-MVP-C64` として `ApplyEstimatedInstallBatchLibraryState` の orchestration を `PackageInstallCoordinator.ApplyEstimatedInstallBatchLibraryState` へ移した。root `BMSLibrary` は `BMSLibrary.PackageInstallHost.cs` で estimated batch state apply、directory scan、reverse lookup add、scan failure warning、warmup log を提供する host になっている。normal install behavior、pending estimated install batch plan / cleanup / maintenance inline apply、maintenance result apply は触っていない。source-text test は coordinator method を検査する形へ更新した。`BMSLibrary.PackageInstall.cs` は 1,511 行、coordinator は 179 行、host は 166 行。build、estimated install / package install / context menu targeted tests、format、diff check、Roslynator warning、静的レビュー、full test は完了。

`REF-MVP-C65` として [maintenance result apply contract planning](./decisions/REF-MVP-C65_maintenance_result_apply_contract_planning.md) をレビューした。次は `ApplyMaintenanceHydrationResult` 全体ではなく、owner view への BMS / bmson maintenance snapshot attach、default / placeholder count、valid snapshot count、owner path count、stale path selection を top-level service / helper へ移す。`DispatchMaintenanceHydrationResult`、`ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` top-level 化、resource health mutation contract 変更は C66 後に再評価する。production code は変更していない。

`REF-MVP-C66` として owner view への maintenance snapshot attach / stale path selection を `BmsLibraryInternal/MaintenanceHydrationOwnerAttachService.cs` へ移した。root `ApplyMaintenanceHydrationResult` は write lock、resource health input mutation scope、full owned target creation、stale DB cleanup、dispatch timing を維持し、attach service を呼ぶ形になっている。`DispatchMaintenanceHydrationResult`、`ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` top-level 化、resource health mutation contract は触っていない。source-text test は service / root bridge 配置を検査する形へ更新した。`BMSLibrary.cs` は 16,658 行、service は 97 行。build、maintenance hydration / resource health / context menu targeted tests、format、diff check、Roslynator warning、静的レビュー、full test は完了。

現在の active ticket: なし。`REF-MVP-C66` completed checkpoint。

次にやる 1 件: `REF-MVP-C67: maintenance result dispatch boundary review` として、C66 後に残る `DispatchMaintenanceHydrationResult`、`ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot`、resource health mutation contract、reflection tests のどれを次に進めるかを 1 件に絞る。production code 変更は、次の実装単位が明確に決まるまで行わない。

## 次回 Codex が最初に読むべきファイル

1. [00_Codex共通実行ルール.md](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS.md](./PLAN_STATUS.md)
3. 推奨最初の実装: [P0-02_BMSLibrary_ドメインFacade化計画.md](./P0-02_BMSLibrary_ドメインFacade化計画.md)
4. [99_調査メモ_現状メトリクス.md](./99_調査メモ_現状メトリクス.md)
