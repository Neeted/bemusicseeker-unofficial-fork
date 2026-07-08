# REF-MVP-C81 Stale Full Target Reflection Reduction

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C82: resource health mutation dispatcher seam` とする。

`DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` が確認している主な意味は、maintenance hydration 固有処理ではなく、resource health mutation dispatch の full rebuild branch が stale full-owned target を publish 成功として扱わず、`FullRebuilt` / `IndexMs` を立てないことである。

C80 で full-owned target freshness 判定は direct test 可能になったため、次は `DispatchResourceHealthIndexMutation` の orchestration を top-level dispatcher + host seam へ移す。これにより、stale full rebuild result、defer、invalidate、delta fallback、full rebuild success の dispatch result を private reflection なしで検証できる。

## 背景

C80 後の状態:

- full-owned target freshness 比較は `ResourceHealthFullOwnedTargetFreshness` へ移った。
- `DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` は root private reflection のまま残っている。
- `DispatchResourceHealthIndexMutation` は root に残り、`ResourceHealthIndexDispatchResult` を返すが、delta / defer / invalidate / full rebuild / stale full target branch の orchestration をまとめて持っている。

`OwnedChartCollectionMutationResult` は publish notification、warning presentation、maintenance presentation、reverse lookup mutation など複数 workflow を抱えるため、C82 で丸ごと top-level 化しない。resource health mutation dispatch workflow だけを切り出す。

## 次に実装する 1 件

`REF-MVP-C82: resource health mutation dispatcher seam`

目的:

- `DispatchResourceHealthIndexMutation` の branch orchestration を `BmsLibraryInternal` の top-level dispatcher へ移す。
- root `BMSLibrary` は published snapshot、invalidate、delta apply、full rebuild、deferred log を提供する host bridge へ寄せる。
- direct unit test で stale full rebuild result が `FullRebuilt=false` / `IndexMs=0` のまま current snapshot を返すことを確認する。
- C82 の範囲で可能なら、`DispatchMaintenanceHydrationResult_StaleFullTargetInvalidatesInsteadOfPublishing` を direct dispatcher / freshness tests へ置き換えて削除する。意味を保てない場合は、残す理由を C82 完了記録に明記する。

完了条件:

- `DispatchResourceHealthIndexMutation` の branch orchestration が root private method から top-level dispatcher へ移っている。
- stale full target branch の direct test が private reflection なしで存在する。
- `OwnedChartCollectionMutationResult` の top-level 化、publish notification ordering、DB schema、setting name、serialized/public surface は変更していない。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` の top-level contract 化。
- `DispatchOwnedChartCollectionMutation` の publish notification ordering。
- `RebuildResourceHealthIndexSnapshotLocked` 内部の lock ordering / snapshot build timing。
- maintenance DB cleanup の順序。

## 後続で詳細化する条件

C82 完了後に、stale full target private reflection test を削除できたかを確認する。残る場合は、残った root-only assertion を `OwnedChartCollectionMutationResult` contract 境界で扱うか、narrower integration hook を用意するかを C83 で判断する。
