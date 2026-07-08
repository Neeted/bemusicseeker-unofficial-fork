# REF-MVP-C63 Package Install Lane Closure Review

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C64: estimated install batch apply coordinator seam` とする。

## 理由

C56 で package move adapter、C58 で repair workflow、C60 で merge workflow、C62 で normal install package workflow が coordinator seam になった。package install lane の root 残存責務として次に局所的なのは `ApplyEstimatedInstallBatchLibraryState` である。

`ApplyEstimatedInstallBatchLibraryState` は、pending estimated install の batch 後処理として root に次の orchestration を残している。

- null context guard。
- `context.AddedCharts` から `ChartStorageTargetSet` を作り、`ApplyInstalledChartStorageTargets(..., "install_package_batch")` を呼ぶ。
- affected directories の distinct filtering。
- destination directory scan。
- reverse lookup add。
- `LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", ...)`。
- incomplete scan warning。
- reverse lookup mutation result の返却。

C62 で normal install 側の state apply / reverse lookup add は host seam に寄ったため、estimated batch apply も dedicated coordinator seam に寄せると package install lane の残りが整理される。

## 他候補を今選ばない理由

### maintenance result apply contract prep

C45 の blocker がまだ残る。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は owner view、resource health full rebuild dispatch、maintenance DB cleanup、private reflection / source-text tests が重なる。C64 後に、resource health mutation / owner view / tests のどこから契約化するかを改めて詳細化する。

### package install seam 追加整理

C62 で `installChartPackages` の main orchestration は coordinator seam へ移った。追加整理は `ApplyEstimatedInstallBatchLibraryState` より優先度が低く、C64 を妨げる blocker ではない。

## C64 で触らない範囲

- `installChartPackages` signature / caller contract。
- `PackageInstallCoordinator` の normal install behavior。
- `PendingEstimatedInstallCoordinator` の batch plan / cleanup / pending package apply。
- estimated install maintenance / inline chart_info apply。
- maintenance hydration result apply、resource health dispatch。
- DB schema / setting name / resources / persisted value。

## 成功条件

- `ApplyEstimatedInstallBatchLibraryState` の null guard、state apply、affected directories filtering、scan failure warning、reverse lookup add / warmup、戻り値の意味が維持されている。
- `PendingEstimatedInstallHost.ApplyEstimatedInstallBatchLibraryState` の suppress resource health invalidation wrapper が維持されている。
- `EstimatedInstallPostProcessing_IsBatchedAndUsesResourceHealthDelta` などの source-text tests が新しい coordinator 配置に合っている。
- package install / pending estimated install / context menu source-text tests が通る。
- build / targeted tests / format / diff check / Roslynator warning / 静的レビューが完了している。
