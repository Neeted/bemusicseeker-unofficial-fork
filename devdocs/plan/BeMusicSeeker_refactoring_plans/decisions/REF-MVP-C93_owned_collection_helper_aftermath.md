# REF-MVP-C93 Owned Collection Helper Aftermath

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C94: owned collection read-only snapshot seam` とする。

## 背景

C92 で `ApplyLibraryMutationDelta` workflow の private reflection helper は coordinator + host route へ移った。一方で、`OwnedChartCollectionStateTests` と `BmsLibraryFolderRenameRefreshTests` には owned collection / installed lookup の read-only snapshot helper が残っている。

主な残存 helper:

- `InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot`
- `InvokeCreateInstalledChartLookupSnapshot`
- `InvokeCreateInstalledChartKeySnapshotExcludingCharts`
- `InvokeCreateInstallDestinationOverlayChartRefSnapshot`
- `InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay`
- `IsInstalledChartLookupIndexInitialized`
- `IsInstalledPrimaryHashLookupInitialized`

これらは mutation を直接起こす helper ではなく、既存 workflow の結果を観測する helper である。次の mutation seam を切る前に観測口を private reflection から外すと、後続の `ApplyInstalledChartStorageTargets` / file scan / inline chart info workflow のレビューが読みやすくなる。

## C94 Scope

- `BMSLibrary` に production からも意味がある internal read-only diagnostics / snapshot facade を追加する。
- Facade は private nested mutation result を公開しない。
- Snapshot の生成順序、cache build、lock 順序は既存 private method を bridge して維持する。
- `OwnedChartCollectionStateTests` と `BmsLibraryFolderRenameRefreshTests` の read-only snapshot / initialized flag helper を facade 経由へ置き換える。
- persisted value、serialized name、DB schema、UI/XAML は変更しない。

## C94 Completion Criteria

- `CreateOwnedChartInfoFullBackfillTargetSnapshot` / installed lookup snapshot / primary hash snapshot / install destination overlay snapshot に対する private reflection helper が削減されている。
- `installedChartLookupIndexInitialized` / `installedPrimaryHashLookupInitialized` private field reflection が削減されている。
- Read-only facade が test-only な setter や state mutation API になっていない。
- 対象テストが通り、standard checks と static review が完了している。

## Deferred

- `_BMSFiles` / `_BmsonSongs` / `resourceHealthIndexInvalidated` private field seeding は C94 では触らない。setup notification 抑制と version/cache invalidation の意図が絡むため、別 checkpoint で public setter / setup seam / actual workflow のどれに寄せるかを判断する。
- `ApplyInstalledChartStorageTargets` は C94 後の有力な mutation workflow seam 候補とする。LR2 normal folder sync block と failure fallback を含むため、read-only observation helper の整理後に implementation ticket 化する。
- `ApplyLibraryFileScanStorageMutation`、`BuildAndPersistInlineChartInfoForInstalledCharts`、`ApplyAutoRenamePlans`、`BeginOwnedDigestMutationWindow` はさらに副作用が広いため、C94 の対象外とする。

## Next Recheck

C94 完了後、`OwnedChartCollectionStateTests` / `BmsLibraryFolderRenameRefreshTests` / `BmsLibraryLr2SongDbSyncTests` / `PlaylistSummaryAggregationTests` の private helper を再確認する。次は `ApplyInstalledChartStorageTargets` workflow seam を第一候補にするが、C94 の結果で observation helper が十分に減っていない場合は追加の read-only cleanup を優先する。
