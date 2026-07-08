# REF-MVP-C83 Resource Health Full Rebuild Stale Integration

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C84: resource health full rebuild coordinator seam` とする。

C82 で `DispatchResourceHealthIndexMutation` の branch orchestration は direct test 可能になった。しかし `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` はまだ、root `RebuildResourceHealthIndexSnapshotLocked` が stale full-owned target で新 snapshot を publish せず current snapshot を返す integration を見ている。

次は `OwnedChartCollectionMutationResult` 境界へ戻るのではなく、resource health full rebuild workflow を coordinator + host seam へ移す。これにより、stale full-owned target、fresh full-owned target、unspecified target fallback、non-versioned specified target fallback の publish / stale result を private reflection なしで検証できる。

## 背景

C82 後の状態:

- resource health mutation dispatch の invalidate / defer / delta / full rebuild / stale full rebuild branch は `ResourceHealthIndexMutationDispatcher` へ移った。
- `ResourceHealthFullOwnedTargetFreshness` により full-owned version freshness の比較は direct test 済み。
- 残る stale full target private reflection test は、maintenance hydration entry point から root full rebuild stale handling を間接確認している。

`OwnedChartCollectionMutationResult` はまだ複数 workflow の notification surface を抱えるため、C84 で触ると scope が広がりすぎる。先に full rebuild の stale publish suppression を切り出す。

## 次に実装する 1 件

`REF-MVP-C84: resource health full rebuild coordinator seam`

目的:

- `RebuildResourceHealthIndexSnapshotLocked(reason, fullOwnedTargetSet, out staleFullOwnedTarget)` の target resolution、snapshot build、stale full-owned target handling、publish result decision、build / stale log orchestration を top-level coordinator へ移す。
- root `BMSLibrary` は current target creation、snapshot build、resource health index lock 内 publish / invalidate / current snapshot取得、log を提供する host bridge へ寄せる。
- direct unit test で stale full-owned target が current snapshot を返し、stale flag を立て、publish success / build success として扱われないことを確認する。
- C84 の範囲で可能なら `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` を削除する。意味を保てない場合は、残す理由を C84 完了記録に明記する。

完了条件:

- full rebuild stale publish suppression の branch orchestration が root private method から coordinator へ移っている。
- stale full-owned target branch の direct test が private reflection なしで存在する。
- lock ordering、snapshot build timing、stale / build log 文言、DB schema、setting name、serialized/public surface は変更していない。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` の top-level contract 化。
- `DispatchOwnedChartCollectionMutation` の publish notification ordering。
- maintenance DB cleanup の順序。
- resource health delta apply internals。

## 後続で詳細化する条件

C84 完了後に stale full target private reflection test を削除できたかを確認する。まだ残る場合は、残った root-only assertion が maintenance hydration dispatch entry の問題か、`OwnedChartCollectionMutationResult` adapter の問題かを C85 で判断する。
