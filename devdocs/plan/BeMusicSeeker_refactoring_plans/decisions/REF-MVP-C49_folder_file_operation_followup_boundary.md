# REF-MVP-C49 Folder/File Operation Follow-up Boundary

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C50: MoveLibraryRootFolder coordinator seam` とする。

## 理由

C48 で `LibraryFolderMoveCoordinator` と `ILibraryFolderMoveHost` が追加され、単一 folder move apply は coordinator 経由になった。

`MoveLibraryRootFolder` はまだ root に次を持っている。

- null guard。
- LR2 sync mutation block。
- initialized / pending / BMS writer lock。
- destination root exists guard と dialog。
- target chart snapshot filtering。
- root folder move plan build。
- drive root warning。
- plan ごとの単一 folder move apply。

このうち単一 move apply は C48 coordinator を既に呼んでいる。C50 では `MoveLibraryRootFolder` 全体を同じ coordinator seam に寄せ、root `BMSLibrary` は host として destination guard、plan build、drive root warning、lock boundary を提供する。

## 他候補を今選ばない理由

### `RemoveLibraryCharts`

`RemoveLibraryCharts` は service 側に `DeleteLibraryCharts` があり足場はあるが、whole-folder delete confirmation、file delete / recycle bin、reverse lookup cleanup、pending install destination clear、dialog、mutation delta applyが絡む。C50 で folder move coordinator の形をもう一段固めてから再評価する。

### `RenameBMSFilesExtensions`

比較的小さいが、invalid extension rename、duplicate delete、pending rename、normal library rename の境界が複数に分かれている。単一 folder move lane を閉じてから着手する方が reviewability が高い。

### `MergeChartDirectory`

C47 の判断を維持する。prepare、source unregister、reverse lookup、`MoveChartPackageFiles`、destination scan、DB upsert、maintenance、owned state apply が横断するため、まだ初手にしない。

## C50 で触らない範囲

- `RenameChartFolder` の behavior 変更。
- `MergeChartDirectory`。
- `MoveChartPackageFiles`。
- `installChartPackages` / package install batch。
- `RemoveLibraryCharts`。
- `RenameBMSFilesExtensions` / pending invalid extension rename。
- maintenance result apply、resource health mutation / dispatch、maintenance DB schema、warning projection。
- persisted value / setting name / DB schema / serialized value。

## 成功条件

- `MoveLibraryRootFolder` の internal surface、null guard、LR2 sync block、lock order、destination root guard、drive root warning が維持されている。
- `BuildRootFolderMovePlans` と plan ごとの single-folder move apply が維持されている。
- C48 の `RenameChartFolder` behavior が変わらない。
- build / folder move 関連 tests / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。
