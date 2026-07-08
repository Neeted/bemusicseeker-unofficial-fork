# REF-MVP-C98 Owned Helper Aftermath

日付: 2026-07-09

## 決定

次の実装 ticket は `REF-MVP-C99: inline chart info workflow seam` とする。

## 背景

C97 で `ApplyLibraryFileScanStorageMutation` workflow の private reflection helper は coordinator + host route へ移った。C97 後に残る helper は、inline chart info、digest mutation window、setup private field seeding、read-only display / metadata helper、folder auto rename workflow に分かれる。

主な残存 helper:

- `InvokeBuildAndPersistInlineChartInfoForInstalledCharts`
- `BeginOwnedDigestMutationWindow`
- `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification`
- `resourceHealthIndexInvalidated` / `ownedChartCollectionInitialized` / owned collection field observation
- `ResolveInstallDestinationMetadataProfileUnsafe`
- `CreateInstalledDisplayPackageForResourceOnlyMerge`
- `ApplyAutoRenamePlans`

## C99 Scope

- `BuildAndPersistInlineChartInfoForInstalledCharts` の workflow を top-level coordinator + host seam へ移す。
- target chart normalization、storage target split、inline build service invocation、BMS / bmson storage row upsert、chart info backfill chunk persistence、chart info index upsert、warning presentation dispatch、digest dispatch / failure fallback を維持する。
- `OwnedChartCollectionStateTests.InvokeBuildAndPersistInlineChartInfoForInstalledCharts` を private reflection なしの coordinator route へ置き換える。
- persisted value、LR2 song DB schema、chart info row schema、digest semantics は変更しない。

## Reason

- private method 直叩きは `OwnedChartCollectionStateTests` の 2 件に集中しており、次の seam として scope を切りやすい。
- inline chart info は C95 / C97 で抽出した storage mutation 系 workflow と同じく、root private state への bridge を host に閉じ込める効果が大きい。
- `BeginOwnedDigestMutationWindow` を先に単独公開すると、test-only window control になりやすい。inline chart info workflow の中で必要な digest window を扱い、残る direct use は別 checkpoint で再判断する。

## Deferred

- `BeginOwnedDigestMutationWindow` は C99 では standalone seam にしない。Playlist summary / resolve cache の観測 test と backfill workflow が絡むため、inline chart info 完了後に再分類する。
- setup private field seeding は C99 では触らない。workflow seam を先に減らし、残った setup helper が test fixture として正当化できるかを後続 checkpoint で判断する。
- `ResolveInstallDestinationMetadataProfileUnsafe` / `CreateInstalledDisplayPackageForResourceOnlyMerge` は C99 では触らない。read-only / display composition であり、inline chart info mutation workflow の完了条件を直接妨げていない。
- `ApplyAutoRenamePlans` は C99 では触らない。folder rename progress、batch mutation、deferred progress reporter を含む別 workflow として後続で再計画する。

## C99 Completion Criteria

- `InvokeBuildAndPersistInlineChartInfoForInstalledCharts` が private reflection を使わない。
- inline chart info の success path が installed lookup / playlist summary / resource health / normal refresh notification に与える既存効果を維持している。
- failure path が potential digest dispatch を維持している。
- 対象テスト、standard checks、static review が完了している。

## Next Recheck

C99 完了後、`BeginOwnedDigestMutationWindow`、setup private field seeding、remaining read-only helper、`ApplyAutoRenamePlans` を再確認し、次に実装する 1 seam だけを選ぶ。
