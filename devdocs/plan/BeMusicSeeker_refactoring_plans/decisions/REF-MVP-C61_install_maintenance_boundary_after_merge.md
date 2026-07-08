# REF-MVP-C61 Install / Maintenance Boundary After Merge Seam

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C62: installChartPackages coordinator seam` とする。

## 理由

C56 で package move adapter、C58 で repair workflow、C60 で merge workflow が coordinator seam になった。package install lane で root `BMSLibrary` に残る大きな orchestration は `installChartPackages` である。

`installChartPackages` は `BmsLibraryPackageInstallService.InstallPackages` の core 判定を呼ぶ facade だが、root 側に次の orchestration を持っている。

- LR2 sync mutation block の fail-fast。
- null package filtering。
- `PackageInstallExecutionResult` から `ChartStorageTargetSet` を作る callback。
- LR2 song.db / bmson DB upsert。
- maintenance immediate apply または deferred list への積み増し。
- score update。
- estimated batch context への added targets 反映。
- normal install state apply と reverse lookup add / warmup。
- `MoveChartPackageFiles` callback contract。
- installed package registration または deferred list への積み増し。
- install performance log。
- inline chart_info build。

C60 までで `MoveChartPackageFiles`、repair、merge の外側 workflow が固定されたため、`installChartPackages` を coordinator / host 境界へ寄せると package install lane の root facade 化がさらに進む。

## 他候補を今選ばない理由

### maintenance result apply contract prep

C45 の blocker が残る。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は owner view、resource health full rebuild dispatch、maintenance DB cleanup、private reflection / source-text tests が重なる。`installChartPackages` の state apply / maintenance / reverse lookup seam を先に整理した後に詳細化する。

### merge seam 追加整理

C60 で `MergeChartDirectory` の root orchestration は coordinator seam へ移った。追加整理は C62 を妨げる blocker ではないため、今は選ばない。

## C62 で触らない範囲

- `BmsLibraryPackageInstallService.InstallPackages` core behavior。
- `PackageInstallExecutionResult` の意味。
- `MoveChartPackageFiles` seam contract。
- pending estimated install / force install の caller contract。
- maintenance hydration result apply、resource health dispatch。
- DB schema / setting name / resources / persisted value。

## 成功条件

- `installChartPackages` signature、戻り値、deferred maintenance / deferred installed packages / estimated batch context の意味が維持されている。
- DB upsert、maintenance apply、score update、state apply、reverse lookup add、installed package registration、inline chart_info build の順序と意味が維持されている。
- `MoveChartPackageFiles` callback へ渡す `showMessageBoxOnInstallFail`、`deleteAllContents`、`hashSnapshot`、`excludedComponentPaths` の意味が維持されている。
- package install / pending estimated install / force install / merge / repair 関連 tests が通る。
- build / targeted tests / format / diff check / Roslynator warning / 静的レビューが完了している。
