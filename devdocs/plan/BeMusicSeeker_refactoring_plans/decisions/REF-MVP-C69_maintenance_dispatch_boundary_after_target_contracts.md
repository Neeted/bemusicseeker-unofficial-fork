# REF-MVP-C69 Maintenance Dispatch Boundary After Target Contracts

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C70: resource health mutation contract extraction` とする。

`DispatchMaintenanceHydrationResult` 本体をまだ直接移動しない。C68 で `ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` は top-level contract になったが、maintenance / resource health dispatch 周辺にはまだ `ResourceHealthIndexUpdateMode`、`ResourceHealthIndexMutation`、`BuildMaintenanceResourceHealthIndexMutation` が `BMSLibrary` private nested / private method として残っている。

## 背景

C68 後の状態:

- `ResourceMaintenanceTargetSet` と `StorageRowsVersionSnapshot` は direct contract として扱える。
- `DispatchMaintenanceHydrationResult` はまだ `OwnedChartCollectionMutationResult` を組み立て、`DispatchOwnedChartCollectionMutation` へ渡す root method である。
- `BuildMaintenanceHydrationMutationResult` は `OwnedChartCollectionMutationResult` と `ResourceHealthMutation` に依存する。
- `BmsLibraryMaintenanceServiceTests` は `BuildMaintenanceResourceHealthIndexMutation` と `ResourceHealthIndexUpdateMode` を private reflection で呼び、戻り値も object reflection で検証している。

このまま `DispatchMaintenanceHydrationResult` を移動すると、dispatch orchestration、owned collection mutation result、resource health mutation、private reflection tests の整理が同時に発生する。

## 次に実装する 1 件

`REF-MVP-C70: resource health mutation contract extraction`

目的:

- `ResourceHealthIndexUpdateMode` を `BmsLibraryInternal` の top-level internal enum に移す。
- `ResourceHealthIndexMutation` を `BmsLibraryInternal` の top-level internal contract に移す。
- `BuildMaintenanceResourceHealthIndexMutation` の判断を top-level internal planner / builder へ移す。
- `BmsLibraryMaintenanceServiceTests` の `BuildMaintenanceResourceHealthIndexMutation` / `ResourceHealthIndexUpdateMode` private reflection を direct planner test へ寄せる。

完了条件:

- `BMSLibrary` private nested `ResourceHealthIndexUpdateMode` / `ResourceHealthIndexMutation` が残っていない。
- maintenance resource health mutation の `Defer`、`DeltaOnUpdates`、`FullOnUpdates`、`InvalidateIfDeltaFails`、full-owned target carrying の意味が変わっていない。
- `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` / `DispatchMaintenanceHydrationResult` 本体はまだ root に残す。
- `BmsLibraryMaintenanceServiceTests` の mutation builder coverage が private method / nested enum reflection から離れている。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `DispatchMaintenanceHydrationResult` の top-level service / coordinator 化。
- `BuildMaintenanceHydrationMutationResult` の移動。
- `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の top-level contract 化。
- resource health index publish / invalidation / full rebuild / delta apply の実行順。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C70 完了後に、次のどちらを進めるかを checkpoint で判断する。

- `BuildMaintenanceHydrationMutationResult` / `DispatchMaintenanceHydrationResult` を small service seam へ移す。
- 先に `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の contract 境界を整理する。
