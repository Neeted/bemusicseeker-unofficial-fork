# REF-MVP-C91 Owned Chart Collection Private Helper Boundary

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C92: library mutation delta apply workflow seam` とする。

C90 後に `OwnedChartCollectionStateTests` に残る private helper を分類した。残存 helper は、単なる test setup ではなく、owned collection snapshot、installed lookup snapshot、library mutation delta apply、installed storage target apply、file scan mutation apply、install destination overlay、inline chart info など複数 workflow にまたがる。

次は snapshot / lookup の観測 helperを先に薄くするのではなく、最も多くの integration test が依存している `ApplyLibraryMutationDelta` entry workflow を top-level coordinator + host seam へ移す。これにより root-only private invocation を減らし、resource health input mutation、storage row unregister、state apply、owned collection apply、fallback invalidation、dispatch handoff の順序を direct test 可能にする。

## 背景

C91 時点の主な分類:

- `InvokeApplyLibraryMutationDelta` は unregister、path change、parent folder / duplicate cache、install destination overlay、installed lookup、resource health delta など複数の owned mutation tests から呼ばれている。
- `InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot` / `InvokeCreateInstalledChartLookupSnapshot` / `InvokeCreateInstalledChartKeySnapshotExcludingCharts` は assertion 用の read helper として残る。これらは C92 では観測面として維持する。
- `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification` は mutation event / normal refresh notification をテストするために initial setup の通知を抑えている。public setter へ単純置換すると、テスト対象の notification count / version が変わる可能性がある。
- `ApplyInstalledChartStorageTargets`、`ApplyLibraryFileScanStorageMutation`、`BuildAndPersistInlineChartInfoForInstalledCharts` はそれぞれ別 workflow なので C92 には含めない。

## 次に実装する 1 件

`REF-MVP-C92: library mutation delta apply workflow seam`

目的:

- `ApplyLibraryMutationDelta` / `ApplyLibraryMutationDeltaWithPerformanceContext` の orchestration を top-level coordinator + host seam へ移す。
- root `BMSLibrary` は resource health input mutation、mutation result build、owned collection notification、storage row unregister、state apply、owned collection apply、normal folder sync、fallback invalidation、dispatch handoff、performance log を提供する bridge に寄せる。
- `OwnedChartCollectionStateTests` の `InvokeApplyLibraryMutationDelta` を actual host / coordinator 経由の private reflection なしに移す。

完了条件:

- `ApplyLibraryMutationDelta` の主要 workflow が root private method だけに閉じていない。
- resource health input mutation、`SuppressResourceHealthIndexInvalidation`、storage row unregister、state apply、owned collection apply、failure fallback、normal refresh clear、dispatch ordering が維持されている。
- `InvokeApplyLibraryMutationDelta` helper が削除されている。削除しない場合は、残る理由を C92 完了記録に明記する。
- DB schema、setting name、serialized/public surface、log 文言は変更しない。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `CreateOwnedChartInfoFullBackfillTargetSnapshot` / installed lookup snapshot の read helper。
- `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification` の setup helper。
- `ApplyInstalledChartStorageTargets`。
- `ApplyLibraryFileScanStorageMutation`。
- inline chart info build workflow。

## 後続で詳細化する条件

C92 完了後に `OwnedChartCollectionStateTests` の private helper を再確認する。`InvokeApplyLibraryMutationDelta` が削除できた場合は、次に read-only snapshot helperを internal query seam へ寄せるか、`ApplyInstalledChartStorageTargets` / file scan mutation のどちらを切るかを C93 で判断する。
