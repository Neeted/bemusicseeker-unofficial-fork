# REF-MVP-C100 Owned Helper Aftermath

日付: 2026-07-09

## 決定

次の実装 ticket は `REF-MVP-C101: diagnostics storage row seed seam` とする。

## 背景

C99 で `BuildAndPersistInlineChartInfoForInstalledCharts` workflow の private reflection helper は coordinator + host route へ移った。C99 後に残る helper は、setup private field seeding、digest mutation window、read-only diagnostics、folder auto rename workflow、LR2 sync private workflow 群に分かれる。

主な残存 helper:

- `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification`
- `resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / `_DuplicateChartGroups` / storage row version observation
- `BeginOwnedDigestMutationWindow`
- `ResolveInstallDestinationMetadataProfileUnsafe`
- `CreateInstalledDisplayPackageForResourceOnlyMerge`
- `ApplyAutoRenamePlans`
- LR2 sync private workflow 群

## C101 Scope

- `_BMSFiles` / `_BmsonSongs` direct field set を、storage rows seed 用の internal diagnostics seam へ置き換える。
- seam は `SetStorageRowsFromInternalMutationUnsafe` / storage row version 更新の既存意味に寄せ、「通知なしに初期 storage rows を播種する」目的を明示する。
- まず対象は `OwnedChartCollectionStateTests`、`BmsLibraryFolderRenameRefreshTests`、`PlaylistSummaryAggregationTests` の `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification` とする。
- resource health / owned collection initialized / duplicate groups などの field helper は、C101 の直接スコープには含めない。storage rows seed seam によって不要になるものがあれば同時に削る。

## Reason

- `_BMSFiles` / `_BmsonSongs` private field seeding は残存 helper の中で件数が最も多く、他の workflow / diagnostics test の前提状態を作っている。
- field direct set は storage row version、owned collection invalidation、duplicate warning clear などの意味をテスト側に隠している。diagnostics seam に寄せることで、後続の private helper 削減が「どの状態を作っているか」を読みやすくなる。
- `ApplyAutoRenamePlans` や `BeginOwnedDigestMutationWindow` は単発で切りやすいが、C101 の前に storage row seed の土台を整える方が後続 ticket のレビュー容易性を上げる。

## Deferred

- `ApplyAutoRenamePlans` は C101 では触らない。folder move、batch mutation、progress、dialog、reverse lookup update を含む別 workflow として、storage row seed cleanup 後に再判断する。
- `BeginOwnedDigestMutationWindow` は C101 では standalone seam にしない。Playlist summary / resolve cache の観測 test に限定されるため、storage row seed cleanup 後に優先度を再判断する。
- `ResolveInstallDestinationMetadataProfileUnsafe` / `CreateInstalledDisplayPackageForResourceOnlyMerge` は C101 では触らない。read-only diagnostics として後続候補に残す。
- LR2 sync private workflow 群は C101 では触らない。範囲が大きいため、別 checkpoint で 1 workflow に絞ってから実装する。

## C101 Completion Criteria

- `OwnedChartCollectionStateTests` / `BmsLibraryFolderRenameRefreshTests` / `PlaylistSummaryAggregationTests` の `_BMSFiles` / `_BmsonSongs` direct field set helper が diagnostics seam 経由になっている。
- storage row version 更新と owned collection invalidation の意味が production 側 seam に閉じている。
- 既存 tests の observable behavior を変えない。
- 対象テスト、standard checks、static review が完了している。

## Next Recheck

C101 完了後、`resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / `_DuplicateChartGroups`、remaining read-only diagnostics、`ApplyAutoRenamePlans`、`BeginOwnedDigestMutationWindow` を再確認し、次に実装する 1 seam だけを選ぶ。
