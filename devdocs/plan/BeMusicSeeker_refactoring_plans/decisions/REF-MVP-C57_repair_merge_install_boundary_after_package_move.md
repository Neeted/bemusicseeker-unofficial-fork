# REF-MVP-C57 Repair / Merge / Install Boundary After Package Move

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C58: FixInstallationDirectoryCharts coordinator seam` とする。

## 理由

C56 で `MoveChartPackageFiles` adapter が explicit package move seam になった。次は、その package move seam を直接使う repair workflow を coordinator へ寄せる。

`FixInstallationDirectoryCharts` の core 判定は既に `BmsLibraryLibraryFileOperationsService.FixInstallationDirectory` に寄っている。root `BMSLibrary` に残っているのは次の orchestration である。

- LR2 sync mutation block。
- initialized-all reader / BMS writer lock。
- approved duplicate removal path set の作成。
- target chart filtering。
- installed hash snapshot 作成。
- package move callback。
- duplicate removal confirmation。
- `LibraryMutationDelta` apply。
- duplicate source removal via `RemoveLibraryCharts(... approvedWholeFolderDeletePaths: [])`。
- maintenance target normalization / apply。

`MergeChartDirectory` より小さく、`installChartPackages` より callback contract が局所的で、C56 の package move adapter 境界をそのまま使える。

## 他候補を今選ばない理由

### `MergeChartDirectory`

C47 / C49 / C51 / C53 / C55 の判断を維持する。source unregister、reverse lookup remove/add、package move、destination scan、DB upsert、maintenance、owned state apply、LR2 user columns 保持が一体で、1 ticket としてはまだ横断しすぎる。

### `installChartPackages` follow-up

`InstallPackages` callback 以降が DB、maintenance、score、state apply、inline chart_info、installed package registration、estimated batch context をまたぐため、C58 より重い。

### maintenance result apply contract prep

C45 の blocker が残る。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は owner view、resource health full rebuild dispatch、maintenance DB cleanup、private reflection / source-text tests が重なるため、まだ直接移動しない。

## C58 で触らない範囲

- `MergeChartDirectory` 本体移動。
- `installChartPackages` callback 契約変更。
- `BmsLibraryPackageInstallService.MovePackageFiles` core behavior。
- `BmsLibraryLibraryFileOperationsService.FixInstallationDirectory` core behavior。
- `LibraryFixInstallationResult` の意味。
- maintenance hydration result apply、resource health dispatch。
- DB schema / setting name / resources / persisted value。

## 成功条件

- `FixInstallationDirectoryCharts` の public/internal surface、null guard、LR2 sync block、lock order が維持されている。
- installed hash snapshot、package move callback、duplicate confirmation、delta apply、duplicate removal、maintenance apply の順序と意味が維持されている。
- `MoveChartPackageFiles` seam と `RemoveLibraryCharts` seam の契約を変えていない。
- BMS / bmson repair、overlay clear、LR2 user columns、duplicate removal の既存 tests が通る。
- build / repair・package move・merge 関連 tests / format / diff check / Roslynator warning 確認 / 静的レビューが完了している。
