# REF-MVP-C102 Owned Helper Aftermath

日付: 2026-07-09

## 決定

次の実装 ticket は `REF-MVP-C103: auto rename batch workflow seam` とする。

## 背景

C101 で `_BMSFiles` / `_BmsonSongs` direct field seeding は diagnostics seam 経由になった。C101 後に残る helper は、folder auto rename batch workflow、digest mutation window、read-only diagnostics、remaining private field observation、LR2 sync private workflow 群に分かれる。

主な残存 helper:

- `ApplyAutoRenamePlans`
- `BeginOwnedDigestMutationWindow`
- `ResolveInstallDestinationMetadataProfileUnsafe`
- `CreateInstalledDisplayPackageForResourceOnlyMerge`
- install estimation metadata cache count / install destination runtime state count / owned chart ref index initialized diagnostics
- `resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / `_DuplicateChartGroups`
- LR2 sync private workflow 群

## C103 Scope

- `ApplyAutoRenamePlans` の batch workflow を top-level coordinator + host seam へ移す。
- file move、plan ごとの失敗継続、batch mutation aggregation、reverse lookup update、normal refresh notification、progress reporting、dialog / log の既存挙動を維持する。
- `BmsLibraryFolderRenameRefreshTests.InvokeApplyAutoRenamePlans` を private reflection なしの coordinator route へ置き換える。
- `AutoRenameChartFolders` / `AutoRenameAllChartFolders` の observable surface は変更しない。

## Reason

- `ApplyAutoRenamePlans` は単なる diagnostics ではなく、file move と library mutation を束ねる実 workflow である。
- 対象 private reflection は 1 件だが、workflow 境界として切る価値が高く、既存の coordinator + host pattern に乗せやすい。
- `BeginOwnedDigestMutationWindow` は小さいが、単独 seam 化すると test-only window control になりやすい。
- read-only diagnostics / private field observation は C103 後に、workflow seam が減った状態でまとめて再分類した方が散らばりにくい。

## Deferred

- `BeginOwnedDigestMutationWindow` は C103 では standalone seam にしない。Playlist summary / resolve cache の観測 test に限定されるため、auto rename workflow 完了後に再判断する。
- `ResolveInstallDestinationMetadataProfileUnsafe` / `CreateInstalledDisplayPackageForResourceOnlyMerge` と count / initialized diagnostics は C103 では触らない。
- `resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / `_DuplicateChartGroups` は C103 では触らない。個別 diagnostics API を増やす前に、remaining diagnostics snapshot としてまとめられるか再判断する。
- LR2 sync private workflow 群は C103 では触らない。別 checkpoint で 1 workflow に絞ってから実装する。

## C103 Completion Criteria

- `InvokeApplyAutoRenamePlans` が private reflection を使わない。
- batch rename の部分成功、failed plan skip、file move、reverse lookup update、library mutation apply、progress reporting、normal refresh notification の既存挙動を維持している。
- 対象テスト、standard checks、static review が完了している。

## Next Recheck

C103 完了後、`BeginOwnedDigestMutationWindow`、remaining read-only diagnostics、remaining private field observation、LR2 sync private workflow 群を再確認し、次に実装する 1 seam だけを選ぶ。
