# REF-MVP-C73 Maintenance Dispatch Bridge After Hydration Planner

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C74: maintenance hydration dispatch plan seam` とする。

`OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の top-level contract 化はまだ行わない。C72 後も `DispatchMaintenanceHydrationResult` は root で `OwnedChartCollectionMutationResult` を dispatch しており、`BmsLibraryMaintenanceServiceTests` は private reflection でこの method を直接呼んでいる。ただし、`OwnedChartCollectionMutationResult` は maintenance hydration 以外の workflow も多数抱えているため、今 top-level 化すると差分が大きすぎる。

## 背景

C72 後の状態:

- maintenance hydration full rebuild 用 resource health mutation は `ResourceHealthIndexMutationPlanner.BuildMaintenanceHydrationFullRebuildMutation` へ移った。
- `BuildMaintenanceHydrationMutationResult` は root に残り、warning presentation / maintenance presentation refresh と resource health mutation を `OwnedChartCollectionMutationResult` へ詰めている。
- `DispatchMaintenanceHydrationResult` は root に残り、dispatch 後に `MaintenanceTableHydrationResult.ResourceHealthIndexMs` を更新している。
- `OwnedChartCollectionMutationResult` は file scan、digest、installed lookup、install destination runtime state、normal refresh notification、resource health dispatch result まで含む大きな private contract である。

このため C74 では `OwnedChartCollectionMutationResult` を直接動かさず、maintenance hydration 固有の dispatch plan を top-level internal contract として切り出す。

## 次に実装する 1 件

`REF-MVP-C74: maintenance hydration dispatch plan seam`

目的:

- `MaintenanceHydrationDispatchPlan` のような top-level internal contract を追加する。
- warning presentation refresh、maintenance presentation refresh、resource health mutation を plan に閉じ込める。
- `ResourceHealthIndexMutationPlanner` または dedicated planner に maintenance hydration dispatch plan builder を追加する。
- root `BuildMaintenanceHydrationMutationResult` は plan を `OwnedChartCollectionMutationResult` に変換する adapter にする。

完了条件:

- maintenance hydration 固有の presentation flags と resource health mutation construction が top-level plan / planner に移っている。
- `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` は root private contract のまま残している。
- `DispatchMaintenanceHydrationResult` の dispatch order と `ResourceHealthIndexMs` update の意味が変わっていない。
- source-text tests は root inline construction ではなく plan / adapter 境界を検査している。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `DispatchMaintenanceHydrationResult` の top-level service / coordinator 化。
- `OwnedChartCollectionMutationResult` の top-level contract 化。
- `ResourceHealthIndexDispatchResult` の top-level contract 化。
- `DispatchOwnedChartCollectionMutation` の実行順。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C74 完了後に、次のどちらを進めるかを checkpoint で判断する。

- `DispatchMaintenanceHydrationResult` の private reflection test を direct plan / adapter test へ寄せ、root bridge をさらに薄くする。
- 先に `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の contract 境界を整理する。
