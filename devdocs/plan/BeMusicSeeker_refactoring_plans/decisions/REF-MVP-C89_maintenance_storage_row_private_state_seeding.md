# REF-MVP-C89 Maintenance Storage Row Private State Seeding

日付: 2026-07-08

## 決定

次の実装 ticket は `REF-MVP-C90: maintenance tests storage row setup public setter cleanup` とする。

C88 後に残る maintenance / resource health 周辺の private state seeding を確認した。`PublishResourceHealthIndexSnapshotUnsafe` private reflection は削除済みで、残る主な阻害要因は `BmsLibraryMaintenanceServiceTests` の `_BMSFiles` / `_BmsonSongs` / `resourceHealthIndexInvalidated` 直接 set と、`OwnedChartCollectionStateTests` のより広い root internal helper 群に分かれる。

次は範囲を `BmsLibraryMaintenanceServiceTests` に限定し、storage row setup を public `BMSFiles` / `BmsonSongs` setter へ寄せる。`resourceHealthIndexInvalidated` が必要なケースは、public setter の既存 invalidation side effect または既存 actual host seam で表現できるかを実装時に確認する。

## 背景

C88 後の分類:

- `BmsLibraryMaintenanceServiceTests` には `_BMSFiles` / `_BmsonSongs` private field seeding が 9 組、`resourceHealthIndexInvalidated` direct set が 2 箇所残る。
- これらは maintenance hydration、resource health rescan、warning ignore、garbled / unregistered chart list の setup であり、多くは public setters を実行してから handled notification version や snapshot を取得すれば意味を保てる見込みがある。
- `OwnedChartCollectionStateTests` には `SetLibraryFilesWithoutNotification` / `SetLibraryBmsonSongsWithoutNotification` と多数の private method / private field helper が残る。ここは installed lookup、storage mutation、file scan、owned collection state など複数 workflow が絡むため、C90 の範囲には含めない。

## 次に実装する 1 件

`REF-MVP-C90: maintenance tests storage row setup public setter cleanup`

目的:

- `BmsLibraryMaintenanceServiceTests` の `_BMSFiles` / `_BmsonSongs` private field setup を、可能な範囲で public `BMSFiles` / `BmsonSongs` setter へ置き換える。
- `resourceHealthIndexInvalidated` direct set を、public setter の invalidation side effect など private field set なしの表現へ置き換える。
- `SetPrivateField` helper の用途がなくなれば削除する。残す場合は残存理由を C90 完了記録に明記する。

完了条件:

- `BmsLibraryMaintenanceServiceTests` の maintenance / resource health setup で `_BMSFiles` / `_BmsonSongs` private field set が削減されている。
- `resourceHealthIndexInvalidated` direct set が削減されている。
- notification version、resource health invalidation、owned collection version、storage row side effect の意味が変わっていない。
- DB schema、setting name、serialized/public surface、log 文言は変更しない。
- build、targeted tests、format、diff check、Roslynator warning、静的レビュー、full test が完了している。

## 今は触らない

- `OwnedChartCollectionStateTests` の private helper 群。
- installed lookup / package install / file scan storage mutation の private method reflection。
- production code の新しい test-only reset API。

## 後続で詳細化する条件

C90 完了後に `BmsLibraryMaintenanceServiceTests` の private helper 残存を確認する。まだ `_BMSFiles` / `_BmsonSongs` / `resourceHealthIndexInvalidated` が残る場合は、実装時に分かった blocker をもとに actual host seam が必要か、public setter cleanup を続けるかを C91 で判断する。
