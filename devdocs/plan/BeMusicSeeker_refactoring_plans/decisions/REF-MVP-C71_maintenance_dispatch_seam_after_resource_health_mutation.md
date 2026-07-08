# REF-MVP-C71 Maintenance Dispatch Seam After Resource Health Mutation

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C72: maintenance hydration resource health mutation planner seam` とする。

`DispatchMaintenanceHydrationResult` 本体や `OwnedChartCollectionMutationResult` をまだ直接移動しない。C70 で resource health mutation contract は top-level 化されたが、`OwnedChartCollectionMutationResult` は file scan、digest、installed lookup、install destination runtime state、normal refresh notification、resource health dispatch result まで含む大きな private contract のままである。

## 背景

C70 後の状態:

- `ResourceHealthIndexMutation` と `ResourceHealthIndexUpdateMode` は top-level internal contract になった。
- maintenance workflow 用の mutation 判断は `ResourceHealthIndexMutationPlanner.BuildMaintenanceMutation` へ移った。
- `DispatchMaintenanceHydrationResult` はまだ root にあり、`OwnedChartCollectionMutationResult` を作って `DispatchOwnedChartCollectionMutation` に渡す。
- `BuildMaintenanceHydrationMutationResult` は root の private method として、warning / maintenance presentation refresh と resource health full rebuild mutation を同時に組み立てている。

`OwnedChartCollectionMutationResult` を今 top-level 化すると、maintenance hydration だけでなく owned collection storage mutation、installed lookup mutation、install destination mutation、normal library refresh notification まで巻き込む。C72 ではそこまで広げず、maintenance hydration が必要とする resource health mutation 構築だけを planner 側へ移す。

## 次に実装する 1 件

`REF-MVP-C72: maintenance hydration resource health mutation planner seam`

目的:

- `ResourceHealthIndexMutationPlanner` に maintenance hydration full rebuild 用の builder を追加する。
- `BuildMaintenanceHydrationMutationResult` は warning / maintenance presentation refresh の owned collection mutation result を作り、resource health mutation は planner から受け取る形にする。
- `MainWindowContextMenuResourceTests.MaintenanceHydrationUsesOwnedStorageOwnerView` は root method body の inline mutation 固定から planner seam 検査へ寄せる。

完了条件:

- `BuildMaintenanceHydrationMutationResult` から `ResourceHealthMutation.RebuildFull = true` / `FullOwnedTargetSet = fullOwnedTargets` の inline assignment が消え、planner 経由になる。
- maintenance hydration の warning presentation / maintenance presentation / resource health full rebuild の意味が変わっていない。
- `DispatchMaintenanceHydrationResult` 本体、`OwnedChartCollectionMutationResult`、`ResourceHealthIndexDispatchResult` は root に残す。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `DispatchMaintenanceHydrationResult` の top-level service / coordinator 化。
- `OwnedChartCollectionMutationResult` の top-level contract 化。
- `ResourceHealthIndexDispatchResult` の top-level contract 化。
- `DispatchOwnedChartCollectionMutation` の実行順。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C72 完了後に、次のどちらを進めるかを checkpoint で判断する。

- `DispatchMaintenanceHydrationResult` を root bridge としてどこまで薄くできるかを見直す。
- 先に `OwnedChartCollectionMutationResult` / `ResourceHealthIndexDispatchResult` の contract 境界を整理する。
