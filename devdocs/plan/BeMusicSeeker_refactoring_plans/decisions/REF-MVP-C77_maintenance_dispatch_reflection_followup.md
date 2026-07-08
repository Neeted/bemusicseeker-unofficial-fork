# REF-MVP-C77 Maintenance Dispatch Reflection Follow-up

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C78: resource health dispatch result contract extraction` とする。

`DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` は C77 では削除しない。この test は stale full-owned target が root resource health dispatch で publish されないことを確認しており、direct coordinator / plan test だけでは同じ意味を担保できない。

一方で、`ResourceHealthIndexDispatchResult` は resource health dispatch outcome を表す小さな private nested contract で、maintenance hydration の `ResourceHealthIndexMs` propagation と stale full-target handling の境界に直接関係している。`OwnedChartCollectionMutationResult` 全体を top-level 化する前に、この result contract を先に切り出す。

## 背景

C76 後の状態:

- `DispatchMaintenanceHydrationResult` は coordinator 呼び出しの薄い bridge になった。
- coordinator direct tests は plan dispatch と `ResourceHealthIndexMs` propagation を private reflection なしで確認している。
- stale full target の root private reflection test は残っている。
- `ResourceHealthIndexDispatchResult` は root private nested class のままで、`OwnedChartCollectionMutationResult.ResourceHealthDispatchResult` と resource health dispatch implementation に閉じている。

`OwnedChartCollectionMutationResult` はまだ大きすぎるため、C78 では `ResourceHealthIndexDispatchResult` だけを top-level internal contract にする。

## 次に実装する 1 件

`REF-MVP-C78: resource health dispatch result contract extraction`

目的:

- `ResourceHealthIndexDispatchResult` を `BmsLibraryInternal` の top-level internal contract へ移す。
- root `DispatchResourceHealthIndexMutation` と `OwnedChartCollectionMutationResult.ResourceHealthDispatchResult` は新 contract を使う。
- tests から必要なら direct に result contract を扱えるようにする。

完了条件:

- `BMSLibrary` private nested `ResourceHealthIndexDispatchResult` が残っていない。
- `Snapshot`、`DeltaApplied`、`Deferred`、`FullRebuilt`、`IndexMs` の意味が変わっていない。
- `OwnedChartCollectionMutationResult` は root private contract のまま残す。
- `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` は同じ意味を保つため残してよい。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` の top-level contract 化。
- stale full target private reflection test の削除。
- `DispatchOwnedChartCollectionMutation` の実行順。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C78 完了後に、stale full target private reflection test を direct seam / integration test へ移せるか再評価する。まだ難しい場合は、`OwnedChartCollectionMutationResult` の contract 境界整理を先に検討する。
