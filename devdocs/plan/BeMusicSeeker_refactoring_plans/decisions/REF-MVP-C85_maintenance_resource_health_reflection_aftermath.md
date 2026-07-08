# REF-MVP-C85 Maintenance Resource Health Reflection Aftermath

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C86: maintenance hydration apply workflow seam` とする。

C84 で stale full-owned resource health rebuild は coordinator + actual `BMSLibrary.ResourceHealthIndexFullRebuildHost` 経由で direct test 可能になり、stale full target 用の maintenance private reflection test は削除できた。C85 では、その後に残る maintenance / resource health 周辺の private reflection と `OwnedChartCollectionMutationResult` 境界を再確認した。

次は test helper だけを置き換えるのではなく、`ApplyMaintenanceHydrationResult` の owner attach、resource health input mutation、full-owned target capture、stale maintenance row cleanup、hydration dispatch handoff を、小さな top-level coordinator / host 境界にする。これにより maintenance hydration の root-only private invocation を減らし、resource health input mutation と cleanup / dispatch timing を direct test できる足場を作る。

## 背景

C84 後に残る代表的な依存:

- `BmsLibraryMaintenanceServiceTests.SetCurrentResourceHealthIndexSnapshot` は `PublishResourceHealthIndexSnapshotUnsafe` を private reflection で呼んでいる。これは C84 で internal host ができたため置き換え可能だが、主に test seam cleanup であり production root の責務はあまり減らない。
- `BmsLibraryMaintenanceServiceTests.InvokeApplyMaintenanceHydrationResult` は `ApplyMaintenanceHydrationResult` を private reflection で呼んでいる。この経路は owner attach、resource health input mutation、cleanup DB write、hydration dispatch を含み、maintenance / resource health 境界として最も意味が大きい。
- `BmsLibraryMaintenanceServiceTests.InvokeDispatchOwnedChartDigestChanges` は `DispatchOwnedChartDigestChanges` を private reflection で呼び、warning presentation と digest producer が混在しても `NormalLibraryRefreshNotificationBatch` の warning effect が維持されることを見ている。これは次点候補として残す。
- `OwnedChartCollectionMutationResult` は storage mutation、digest changes、installed lookup、resource health mutation、normal refresh notification、loggable counters を同時に抱えており、いきなり top-level contract 化すると大きすぎる。

## 次に実装する 1 件

`REF-MVP-C86: maintenance hydration apply workflow seam`

目的:

- `ApplyMaintenanceHydrationResult` の owner attach、resource health input mutation、full-owned target capture、stale maintenance row cleanup、dispatch handoff を、root private method から top-level coordinator / host 境界へ移す。
- root `BMSLibrary` は owned storage owner view、resource health mutation scope、DB cleanup、full-owned target creation、dispatch handoff を提供する bridge に寄せる。
- `ApplyMaintenanceHydrationResult_PublishesMaintenanceRefreshThroughOwnedDispatcher` を private reflection なしの direct test または public behavior test へ移す。

完了条件:

- `ApplyMaintenanceHydrationResult` の主要 workflow が root private method だけに閉じていない。
- writer lock、`BeginResourceHealthInputMutation`、full-owned target capture、stale maintenance row cleanup、`ViewRefreshQueued`、dispatch handoff の順序が維持されている。
- 可能なら `InvokeApplyMaintenanceHydrationResult` を削除する。削除しない場合は、残る理由と次の seam を C86 完了記録に明記する。
- DB schema、setting name、serialized/public surface、log 文言、notification effect は変更しない。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionMutationResult` 全体の top-level contract 化。
- `DispatchOwnedChartDigestChanges` / `DispatchOwnedPotentialDigestChanges` の digest dispatch seam。C86 後の次点候補として残す。
- `DispatchOwnedChartCollectionMutation` の publish ordering、resource health dispatch ordering、normal refresh ordering。
- `SetCurrentResourceHealthIndexSnapshot` reflection helper の一括削除。C86 中に自然に置き換えられる場合だけ対応し、無理に混ぜない。

## 後続で詳細化する条件

C86 完了後に、残る private reflection helper を再確認する。`InvokeApplyMaintenanceHydrationResult` が削除できた場合は、次に `DispatchOwnedChartDigestChanges` の digest dispatch seam を切るか、snapshot publish test seam cleanup を短い follow-up にするかを判断する。
