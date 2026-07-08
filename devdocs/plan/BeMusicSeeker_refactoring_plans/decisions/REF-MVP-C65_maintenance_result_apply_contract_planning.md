# REF-MVP-C65 Maintenance Result Apply Contract Planning

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C66: maintenance hydration owner attach service seam` とする。

## 理由

C45 では `ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` を直接移動しない判断にした。その後、C56-C64 で package move / repair / merge / install / estimated batch apply の workflow seam は進んだが、maintenance result apply にはまだ次の blocker が残っている。

- `BmsLibraryMaintenanceServiceTests` が private reflection で `ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` を直接呼ぶ。
- 同 test が private nested `ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` を reflection で組み立てる。
- `MainWindowContextMenuResourceTests.MaintenanceHydrationUsesOwnedStorageOwnerView` が source-text で root method body の owner view / dispatch 配置を固定している。
- `DispatchMaintenanceHydrationResult` は `ResourceMaintenanceTargetSet` / `OwnedChartCollectionMutationResult` / `ResourceHealthMutation` の nested contract に依存している。

一方、`ApplyMaintenanceHydrationResult` 前半の BMS / bmson maintenance snapshot attach と stale path selection は、`OwnedChartStorageOwnerView` と `MaintenanceTableHydrationResult` を入力にできる。ここを top-level service へ移すと、resource health mutation / dispatch の大きい contract 変更に入る前に、owner view / maintenance attach contract を独立してテストしやすくなる。

## 他候補を今選ばない理由

### `DispatchMaintenanceHydrationResult` coordinator seam

`ResourceMaintenanceTargetSet`、`OwnedChartCollectionMutationResult`、`ResourceHealthMutation`、`StorageRowsVersionSnapshot` が強く絡む。private reflection tests もこの領域を直接組み立てているため、最初に触るには横断範囲が大きい。

### private reflection tests の全面移行

reflection tests を一括で移すには、先に service / contract の着地点が必要である。C66 で owner attach service を作り、少なくとも attach 部分の直接テスト先を作ってから、残る dispatch reflection test を再評価する。

### maintenance hydration result apply 全体の coordinator 化

lock、resource health input mutation、stale DB cleanup、full owned target creation、dispatch が一体で、C45 の blocker を同時に解くことになる。今は小さい seam から進める。

## C66 で触る範囲

- `OwnedChartStorageOwnerView` と `MaintenanceTableHydrationResult` を受け取り、BMS / bmson maintenance snapshot attach、default / placeholder count、valid snapshot count、owner path count、stale path selection を担当する top-level service / helper。
- `ApplyMaintenanceHydrationResult` は write lock、resource health input mutation scope、full owned target creation、stale DB cleanup、dispatch を引き続き持つ。
- `MainWindowContextMenuResourceTests.MaintenanceHydrationUsesOwnedStorageOwnerView` は root method body 固定から service / root bridge 配置を検査する形へ更新する。
- 必要なら service 直接テストを追加し、owner attach の意味を source-text test から一部移す。

## C66 で触らない範囲

- `DispatchMaintenanceHydrationResult` の移動。
- `ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` の top-level 化。
- `OwnedChartCollectionMutationResult` / `ResourceHealthMutation` の contract 変更。
- stale maintenance row cleanup の DB schema / behavior。
- maintenance hydration queue / worker lifecycle。
- resources / settings / persisted values。

## 成功条件

- BMS / bmson maintenance snapshot attach、default / placeholder count、valid snapshot count、owner path count、stale path selection の意味が維持されている。
- `ApplyMaintenanceHydrationResult` の lock / resource health input mutation / stale cleanup / dispatch timing が維持されている。
- source-text test は root method body の loop 固定から、新しい service / bridge 配置の検査へ移っている。
- maintenance hydration / resource health 関連 targeted tests が通る。
- build / targeted tests / format / diff check / Roslynator warning / 静的レビューが完了している。
