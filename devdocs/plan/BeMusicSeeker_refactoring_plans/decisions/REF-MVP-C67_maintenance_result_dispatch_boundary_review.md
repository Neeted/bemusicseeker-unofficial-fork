# REF-MVP-C67 Maintenance Result Dispatch Boundary Review

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C68: resource maintenance target contract extraction` とする。

`DispatchMaintenanceHydrationResult` 本体や resource health mutation dispatch を先に移動するのではなく、まず `ResourceMaintenanceTargetSet` と `StorageRowsVersionSnapshot` を `BMSLibrary` private nested type から top-level internal contract へ移す。

## 背景

C66 で `ApplyMaintenanceHydrationResult` の owner view への maintenance snapshot attach / stale path selection は `MaintenanceHydrationOwnerAttachService` へ移った。一方、maintenance hydration 後半にはまだ次の結合が残っている。

- `DispatchMaintenanceHydrationResult` は `ResourceMaintenanceTargetSet`、`OwnedChartCollectionMutationResult`、`ResourceHealthMutation`、`StorageRowsVersionSnapshot` を同時に扱う。
- `ResourceMaintenanceTargetSet` と `StorageRowsVersionSnapshot` は `BMSLibrary` private nested type のため、外部 service / test から素直に扱えない。
- `BmsLibraryMaintenanceServiceTests` は private reflection で `DispatchMaintenanceHydrationResult` を呼び、さらに nested type を reflection で組み立てている。
- source-text tests も dispatch / target construction の配置を監視している。

この状態で dispatch coordinator を直接切り出すと、production code の移動、nested contract の移動、private reflection test の移行、resource health mutation contract の再設計が同時に発生する。

## 次に実装する 1 件

`REF-MVP-C68: resource maintenance target contract extraction`

目的:

- `StorageRowsVersionSnapshot` を top-level internal contract へ移す。
- `ResourceMaintenanceTargetSet` を top-level internal contract へ移す。
- `BMSLibrary` 内の利用箇所は同じ意味のまま新 contract を参照する。
- `BmsLibraryMaintenanceServiceTests` の nested type reflection helper を、可能な範囲で direct construction / static factory call へ移す。

完了条件:

- `BMSLibrary` private nested type としての `StorageRowsVersionSnapshot` / `ResourceMaintenanceTargetSet` が残っていない。
- storage row version と resource maintenance target の意味、default sentinel、full-owned / subset の判定、version carrying が変わっていない。
- `DispatchMaintenanceHydrationResult` 自体の配置と dispatch behavior は変えない。
- reflection test は少なくとも nested type lookup からは離れている。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `DispatchMaintenanceHydrationResult` の top-level service / coordinator 化。
- `OwnedChartCollectionMutationResult` / `ResourceHealthMutation` の top-level contract 化。
- maintenance DB cleanup の順序。
- resource health index dispatch / invalidation の意味。
- settings、schema、serialized value、persisted value。

## 後続で詳細化する条件

C68 完了後に、次のどちらを進めるかを checkpoint で判断する。

- `DispatchMaintenanceHydrationResult` の mutation build / dispatch boundary を service seam へ移す。
- 先に `OwnedChartCollectionMutationResult` / `ResourceHealthMutation` 周辺の contract を整理し、remaining private reflection tests を減らす。
