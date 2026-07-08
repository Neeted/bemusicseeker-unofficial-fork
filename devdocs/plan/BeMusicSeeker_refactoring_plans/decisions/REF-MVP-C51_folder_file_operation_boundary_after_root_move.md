# REF-MVP-C51 Folder/File Operation Boundary After Root Move

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C52: RemoveLibraryCharts coordinator seam` とする。

## 理由

C48-C50 で folder move workflow は `LibraryFolderMoveCoordinator` に寄った。次に同じ file operation 領域で進めるなら、`RemoveLibraryCharts` が最も切り出し効果と実装単位の釣り合いがよい。

`RemoveLibraryCharts` は root `BMSLibrary` に次を持っている。

- LR2 sync mutation block。
- approved whole-folder delete path set の作成。
- initialized / pending / BMS writer lock。
- whole-folder delete confirmation dialog。
- `DeleteLibraryCharts` service 呼び出し。
- `delete_library_result` performance log。
- reverse lookup mutation log / warmup queue。
- `LibraryMutationDelta` apply。
- delete failure dialogs。

実削除、canonical chart 解決、resource index mutation、pending install destination clear、`LibraryMutationDelta` 生成は既に `BmsLibraryLibraryFileOperationsService.DeleteLibraryCharts` に寄っている。C52 では root を host bridge に寄せ、削除 workflow の orchestration を dedicated coordinator に移す。

## 他候補を今選ばない理由

### `MergeChartDirectory`

C47 / C49 の判断を維持する。DB user columns 保持、source unregister、reverse lookup remove/add、`MoveChartPackageFiles`、destination scan、DB upsert、maintenance、owned state apply が 1 workflow に絡むため、まだ初手にしない。

### `MoveChartPackageFiles`

install、merge、fix-installation から共有され、core は既に `BmsLibraryPackageInstallService.MovePackageFiles` にある。残りは options / dialog / log / path factory adapter の性格が強く、切るなら package install 全体の command 境界を再検討してからにする。

### `RenameBMSFilesExtensions`

通常 library rename と pending rename が対になっている。片方だけ coordinator 化すると rename 境界が非対称に残るため、`RemoveLibraryCharts` 後に normal / pending rename の整理単位を再評価する。

### `LibraryFolderMoveCoordinator` 追加整理

現状の `ILibraryFolderMoveHost` は folder move に閉じている。削除や rename extension を同じ coordinator に足すと host が肥大化するため、C52 は dedicated `LibraryChartRemovalCoordinator` として進める。

## C52 で触らない範囲

- `MergeChartDirectory`。
- `MoveChartPackageFiles`。
- `RenameBMSFilesExtensions` / pending invalid extension rename。
- `LibraryFolderMoveCoordinator` の責務拡張。
- package install batch。
- maintenance result apply、resource health mutation / dispatch、maintenance DB schema、warning projection。
- persisted value / setting name / DB schema / serialized value。

## 成功条件

- `RemoveLibraryCharts` の internal surface、LR2 sync block、lock order が維持されている。
- `approvedWholeFolderDeletePaths == null` は確認 dialog、空配列は whole-folder delete を承認しない既存挙動を維持している。
- `delete_library_result` log、`LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", ...)`、`ApplyLibraryMutationDelta(...)`、delete failure dialogs の順序と文言が維持されている。
- build / deletion 関連 tests / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。
