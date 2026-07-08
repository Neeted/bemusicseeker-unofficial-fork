# REF-MVP-C75 Maintenance Dispatch Reflection Boundary

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C76: maintenance hydration dispatch coordinator seam` とする。

`DispatchMaintenanceHydrationResult` private reflection test は C75 では直接削除しない。現在の `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` は、stale full-owned resource health target が publish されず invalidate 扱いになることを確認している。この挙動は `DispatchOwnedChartCollectionMutation`、`RebuildResourceHealthIndexSnapshotLocked`、resource health input / storage / owned collection version の root state に依存しており、C75 時点で direct plan test へ置き換えると同じ意味を保てない。

## 背景

C74 後の状態:

- maintenance hydration 固有の presentation flags と resource health mutation construction は `MaintenanceHydrationDispatchPlan` / `ResourceHealthIndexMutationPlanner` へ移った。
- root `BuildMaintenanceHydrationMutationResult` は plan を `OwnedChartCollectionMutationResult` に変換する adapter になった。
- root `DispatchMaintenanceHydrationResult` はまだ plan build、owned collection dispatch、`MaintenanceTableHydrationResult.ResourceHealthIndexMs` update をまとめて持っている。
- `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` はまだ root private contract であり、今 top-level 化すると maintenance hydration 以外の workflow も巻き込む。

## 次に実装する 1 件

`REF-MVP-C76: maintenance hydration dispatch coordinator seam`

目的:

- `MaintenanceHydrationDispatchCoordinator` のような top-level internal coordinator を追加する。
- coordinator は `ResourceMaintenanceTargetSet` から `MaintenanceHydrationDispatchPlan` を作り、host へ dispatch を依頼し、`MaintenanceTableHydrationResult.ResourceHealthIndexMs` を更新する。
- root `BMSLibrary` は host bridge として `MaintenanceHydrationDispatchPlan` を private `OwnedChartCollectionMutationResult` に変換して `DispatchOwnedChartCollectionMutation` を呼ぶ。
- coordinator の direct unit test を追加し、plan creation と `ResourceHealthIndexMs` update を private reflection なしで確認する。

完了条件:

- `DispatchMaintenanceHydrationResult` は coordinator 呼び出しの薄い bridge になっている。
- root private `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` はまだ root に残す。
- stale full target の既存 private reflection test は、同じ意味を保つため C76 では残してよい。
- coordinator direct tests が追加され、少なくとも plan dispatch と index ms propagation を reflection なしで検証している。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` の top-level contract 化。
- `ResourceHealthIndexDispatchResult` の top-level contract 化。
- stale full target の root integration test 削除。
- `DispatchOwnedChartCollectionMutation` の実行順。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C76 完了後に、次のどちらを進めるかを checkpoint で判断する。

- `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` を direct seam / integration test へ移せるか再評価する。
- 先に `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の contract 境界を整理する。
