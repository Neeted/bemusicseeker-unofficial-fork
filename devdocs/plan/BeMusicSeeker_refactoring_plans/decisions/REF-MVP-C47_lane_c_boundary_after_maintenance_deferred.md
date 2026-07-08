# REF-MVP-C47 Lane C Boundary After Maintenance Deferred Seams

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C48: RenameChartFolder single-folder move coordinator seam` とする。

## 候補評価

### Maintenance Result Apply

C45 の判断を維持する。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は root lock、owner view、resource health nested mutation、maintenance DB cleanup、private reflection / source-text tests が重なるため、まだ直接移動しない。

詳細化は resource health mutation top-level contract、owner view / maintenance attach result contract、または blocker tests の service / contract 直接テスト化後に行う。

### MergeChartDirectory

`MergeChartDirectory` は初手にしない。

理由:

- prepare、source unregister、reverse lookup、`MoveChartPackageFiles`、destination scan、DB upsert、maintenance、owned state apply が 1 workflow に入っている。
- folder/file operation だけでなく package install、maintenance、resource cache を横断する。
- 先に単一フォルダ move seam を作った方が host contract の形を小さく検証できる。

### Package Install Follow-up

`installChartPackages` 本体は次にしない。

理由:

- C3-C7 の coordinator 群から delegate boundary として使われている。
- DB upsert、maintenance、score、state apply、inline chart_info が絡む。
- package install follow-up は C48 以降に folder/file seam の結果を見て再評価する。

## 次の実装単位

`RenameChartFolder` から単一フォルダ move workflow を coordinator seam へ移す。

対象候補:

- `RenameChartFolder`
- `MoveLibraryChartFolderInternal`
- `TryMoveLibraryChartFolder`

root `BMSLibrary` は host として次を提供する。

- LR2 sync mutation block。
- root folder rename guard / dialog。
- lock boundary。
- destination path calculation。
- file move execution via existing `BmsLibraryLibraryFileOperationsService` / `IFileMutationService`。
- reverse lookup mutation logging / warmup queue。
- `BuildFolderMoveDelta` input snapshot。
- `ApplyLibraryMutationDelta`。
- duplicate cache invalidation.

## C48 で触らない範囲

- `MergeChartDirectory`。
- `MoveLibraryRootFolder` の複数 folder move semantics。
- `MoveChartPackageFiles`。
- `installChartPackages` / package install batch。
- `FixInstallationDirectoryCharts`。
- `RemoveLibraryCharts`。
- `ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult`。
- resource health mutation / dispatch、maintenance DB schema、warning projection。
- persisted value / setting name / DB schema / serialized value。

## 成功条件

- `RenameChartFolder` の public surface、null guard、root folder warning、lock order、destination path、dialog timing が維持されている。
- successful move path で reverse lookup update、warmup queue、folder move delta apply、duplicate cache invalidation が維持されている。
- no-op / destination exists / move failure の挙動と dialogs が維持されている。
- build / folder-file operation 関連 tests / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。
