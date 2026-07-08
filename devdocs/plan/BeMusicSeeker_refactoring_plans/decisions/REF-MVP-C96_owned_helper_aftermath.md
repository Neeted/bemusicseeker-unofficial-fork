# REF-MVP-C96 Owned Helper Aftermath

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C97: library file scan storage mutation seam` とする。

## 背景

C95 で `ApplyInstalledChartStorageTargets` workflow の private reflection helper は coordinator + host route へ移った。C95 後も `OwnedChartCollectionStateTests` には file scan storage mutation、inline chart info、digest mutation window、setup private field seeding などが残っている。

主な残存 helper:

- `InvokeApplyLibraryFileScanStorageMutation`
- `InvokeBuildAndPersistInlineChartInfoForInstalledCharts`
- `BeginOwnedDigestMutationWindow`
- `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification`
- `resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / storage rows version field observation
- install destination metadata profile / display package / owned chart ref index の remaining read-only helper

## C97 Scope

- `ApplyLibraryFileScanStorageMutation` の workflow を top-level coordinator + host seam へ移す。
- `rwlockBMSFiles` writer guard、removed payload detection、resource health input mutation、storage row replacement、owned collection replacement、library resource index / directory lookup cache update、failure fallback、dispatch reason を維持する。
- `OwnedChartCollectionStateTests.InvokeApplyLibraryFileScanStorageMutation` を private reflection なしの coordinator route へ置き換える。
- `SongTableFileCheckResult`、storage rows、resource health、normal refresh notification の既存 contract は変更しない。

## C97 Completion Criteria

- `ApplyLibraryFileScanStorageMutation` private reflection helper が削減されている。
- `ApplyLibraryFileScanStorageMutation_*` tests が private method invocation ではなく actual coordinator/host route を通る。
- Failure fallback の cache invalidation / owned collection invalidation / resource health invalidation / normal refresh clearing の順序が維持されている。
- 対象テスト、standard checks、static review が完了している。

## Deferred

- `BuildAndPersistInlineChartInfoForInstalledCharts` は C97 では触らない。直叩きは主に 2 件で、digest window / inline build / installed lookup update が絡むため C97 後に再判断する。
- `BeginOwnedDigestMutationWindow` は playlist summary / resolve cache 観測と絡むため C97 では触らない。
- setup private field seeding は C97 では触らない。workflow seam を先に切らないと test utility 公開になりやすい。
- remaining read-only helper は C97 では触らない。今回の主眼は mutation workflow の entry point を削ることであり、read-only helper は file scan workflow の完了条件を直接妨げていない。
- `ApplyAutoRenamePlans` は C97 では触らない。folder rename progress / batch mutation / deferred progress reporter が絡む別 workflow なので、file scan workflow とは分けて再計画する。

## Next Recheck

C97 完了後、`OwnedChartCollectionStateTests` / `PlaylistSummaryAggregationTests` / `BmsLibraryFolderRenameRefreshTests` の private helper を再確認する。次は inline chart info workflow seam、digest mutation window seam、または setup private field seeding cleanup のいずれか 1 件だけを選ぶ。
