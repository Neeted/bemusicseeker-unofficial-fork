# REF-MVP-C55 Package Move / Merge Boundary After Rename

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C56: MoveChartPackageFiles adapter boundary` とする。

## 理由

C54 で file operation の rename / delete seam は進んだ。次に package install / merge / repair の重い workflow へ入る前に、3 つの呼び出し元が共有する package move adapter の意味を固定する。

`MoveChartPackageFiles` の core file move は既に `BmsLibraryPackageInstallService.MovePackageFiles` にある。root 側に残っているのは次の adapter 責務である。

- current options snapshot。
- auto folder naming callback。
- displayed exception message callback。
- file mutation service / options。
- scoped operation dialog service。
- install performance log callback。
- `showMessageBoxOnInstallFail`、`deleteAllContents`、`existingHashes`、`excludedComponentPaths` の引き渡し。

この adapter は `installChartPackages`、`MergeChartDirectory`、`FixInstallationDirectoryCharts` から共有されている。C56 で package move adapter を explicit seam にしておくと、後続で merge / repair / install を切るときに、package move の意味を再定義せずに済む。

## 他候補を今選ばない理由

### `MergeChartDirectory`

C47 / C49 / C51 / C53 の判断を維持する。source unregister、reverse lookup remove/add、package move、destination scan、DB upsert、maintenance、owned state apply が一体で、1 ticket としてはまだ横断しすぎる。

### `FixInstallationDirectoryCharts`

範囲は `MergeChartDirectory` より小さいが、package move、duplicate confirmation、duplicate source removal、`RemoveLibraryCharts`、maintenance が絡む。C56 で package move adapter を固定した後に再評価する。

### `installChartPackages` follow-up

`InstallPackages` callback 以降が DB、maintenance、score、state apply、inline chart_info、installed package registration をまたぐため、C55 の次に直接入るには重い。

### maintenance result apply contract prep

C45 の blocker が残る。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は owner view、resource health full rebuild dispatch、maintenance DB cleanup、private reflection / source-text tests が重なるため、まだ直接移動しない。

## C56 で触らない範囲

- `MergeChartDirectory` 本体移動。
- `FixInstallationDirectoryCharts` 本体移動。
- `installChartPackages` callback 契約変更。
- `BmsLibraryPackageInstallService.MovePackageFiles` core behavior。
- maintenance hydration result apply、resource health dispatch。
- DB schema / setting name / resources / persisted value。

## 成功条件

- `MoveChartPackageFiles` の parameters と戻り値、`showMessageBoxOnInstallFail`、`deleteAllContents`、`existingHashes`、`excludedComponentPaths` の意味が維持されている。
- auto naming、smart overwrite、folder cleanup、安全 cleanup 判定、dialog/log callback の意味が維持されている。
- `installChartPackages`、`MergeChartDirectory`、`FixInstallationDirectoryCharts` の呼び出し順序と周辺 state apply / DB / maintenance 処理が変わっていない。
- build / package move・merge・repair 関連 tests / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。
