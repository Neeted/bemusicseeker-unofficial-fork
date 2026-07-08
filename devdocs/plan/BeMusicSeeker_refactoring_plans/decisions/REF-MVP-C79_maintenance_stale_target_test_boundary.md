# REF-MVP-C79 Maintenance Stale-Target Test Boundary

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C80: resource health full-owned target freshness seam` とする。

`DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` は C79 では削除しない。この test は stale full-owned target が root resource health dispatch で publish されず、既存 snapshot が維持されることを確認している。現時点で direct coordinator / plan test だけへ置き換えると、root の current version 判定、invalidated flag、publish 抑止の意味が抜ける。

一方で、stale 判定の中核である full-owned target version freshness は、root private method と nullable contract のまま残っている。次は `ResourceMaintenanceTargetSet` と現在の version snapshot を入力にする top-level internal helper へ切り出し、direct unit test を追加する。これにより、private reflection test が担う意味を「version 判定」と「root dispatch integration」に分けられる。

## 背景

C78 後の状態:

- `ResourceHealthIndexDispatchResult` は top-level internal contract になった。
- `DispatchMaintenanceHydrationResult` は coordinator bridge になっている。
- `OwnedChartCollectionMutationResult` は root private contract のまま残っている。
- stale full target private reflection test は、root dispatch outcome の integration test として残っている。

`OwnedChartCollectionMutationResult` は publish notification、resource health mutation、refresh effects、reverse lookup mutation など複数 workflow を含むため、stale test 移行の前提として丸ごと top-level 化するには大きい。先に full-owned target freshness 判定だけを分離する。

## 次に実装する 1 件

`REF-MVP-C80: resource health full-owned target freshness seam`

目的:

- full-owned target が current storage rows / owned collection / resource health input version と一致しているかの判定を `BmsLibraryInternal` の top-level helper へ移す。
- `BMSLibrary` の private method は root state を snapshot して helper へ渡すだけに近づける。
- direct unit test で fresh / stale / no full-owned version / unstable input version を private reflection なしで確認する。

完了条件:

- full-owned target freshness の比較ロジックが root private method に残っていない。
- `ResourceMaintenanceTargetSet` の persisted value、serialized name、DB schema、public API は変更していない。
- stale full target private reflection test は root dispatch integration として残してよい。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` の top-level contract 化。
- stale full target private reflection test の削除。
- `DispatchOwnedChartCollectionMutation` の実行順。
- resource health index publish / invalidation の lock ordering。

## 後続で詳細化する条件

C80 完了後に、stale full target private reflection test を direct freshness tests + narrower root integration test へ分けられるか再評価する。まだ難しい場合は、`OwnedChartCollectionMutationResult` の contract 境界整理を次候補として検討する。
