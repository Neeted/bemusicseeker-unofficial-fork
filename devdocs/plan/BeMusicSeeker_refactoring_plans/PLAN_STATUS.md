# PLAN_STATUS

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

最終更新日: 2026-07-07

## Refactoring MVP State

Release Freeze: active。

`git push`、Git tag、GitHub Release、Release draft、publish / release script 実行は禁止。Refactoring MVP Gate 通過前に release / version 関連ファイルは原則触らない。

## Active Lanes

| Lane | Active ticket | 状態 | 次に読む |
|---|---|---|---|
| A: MainWindowViewModel shell 化 | `REF-MVP-A1: Playlist detail build workflow extraction` | completed checkpoint | [P0-01](./P0-01_MainWindowViewModel_リファクタリング計画.md) |
| B: MainWindow code-behind / XAML MVVM 移行 | `REF-MVP-B1: MainWindow event handler inventory and first command bridge` | completed checkpoint | [P0-03](./P0-03_MainWindow_UI_MVVM移行計画.md) |
| C: BMSLibrary domain facade 化 | `REF-MVP-C8: Normal library refresh publisher extraction` | completed checkpoint | [P0-02](./P0-02_BMSLibrary_ドメインFacade化計画.md) |
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
| `BMSLibrary.cs` | 19,546 行 | 12,000 行以下 | package install follow-up、LR2 sync、maintenance、folder/file operation |
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

`REF-MVP-C8` として normal library refresh notification の queue / version / reset barrier / batch aggregation / clear を `NormalLibraryRefreshPublisher` へ移した。`BMSLibrary.cs` は 19,436 行、`NormalLibraryRefreshPublisher.cs` は 176 行。build、normal refresh 関連 tests、source-text 代表 tests、full test、format、diff check、Roslynator 対象確認、静的レビューは完了。

次にやる 1 件: Lane C / B / A / D の後続候補から、Refactoring MVP Gate に最も近い workflow を 1 件だけ active ticket 化してから実装する。候補は各 P0 計画書の「後続候補」を正本とする。

## 次回 Codex が最初に読むべきファイル

1. [00_Codex共通実行ルール.md](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS.md](./PLAN_STATUS.md)
3. 推奨最初の実装: [P0-02_BMSLibrary_ドメインFacade化計画.md](./P0-02_BMSLibrary_ドメインFacade化計画.md)
4. [99_調査メモ_現状メトリクス.md](./99_調査メモ_現状メトリクス.md)
