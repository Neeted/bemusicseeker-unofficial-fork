# REF-MVP-C45 Maintenance Hydration Result Apply Boundary

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は C45 では移動しない。

次の実装 ticket は `REF-MVP-C46: installable maintenance deferred coordinator seam` とする。

## 理由

`ApplyMaintenanceHydrationResult` は単なる worker 結果反映ではなく、次の root 境界を同時に扱っている。

- `rwlockBMSFiles` writer guard 内で `OwnedChartStorageOwnerView` を作成し、BMS / bmson の maintenance snapshot を attach する。
- `BeginResourceHealthInputMutation()` の範囲で resource health input version を更新する。
- stale maintenance row を `rwlockSongDBMaintenance` writer guard と `dbGateway.DeleteMaintenanceRows` で cleanup する。
- cleanup failure 時に `ForceInvalidateResourceHealthIndex("maintenance_hydration_cleanup_failed")` を呼ぶ。
- `ResourceMaintenanceTargetSet` / `OwnedChartCollectionMutationResult` / `ResourceHealthMutation` など、`BMSLibrary` nested type に強く依存する full owned resource health rebuild を dispatch する。

ここを coordinator / service へ直接移すと、resource health mutation contract、owner view contract、maintenance DB cleanup contract、private reflection tests を同時に再設計する必要がある。現在の Refactoring MVP の 1 ticket としては大きすぎる。

## Test Blockers

代表的な blocker:

- `BmsLibraryMaintenanceServiceTests` が private reflection で `ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` を直接呼ぶ。
- 同 test が private nested `ResourceMaintenanceTargetSet` / `StorageRowsVersionSnapshot` を reflection で組み立てる。
- `MainWindowContextMenuResourceTests.MaintenanceHydrationUsesOwnedStorageOwnerView` が source-text で `ApplyMaintenanceHydrationResult` の owner view / resource health dispatch / installable count の配置を確認している。

これらは削除ではなく、後続で service / contract 直接テストへ移す対象とする。

## 次に進む理由

`QueueDeferredInstallableMaintenance` / `CompleteInstallableMaintenanceForShutdown` / deferred worker 本体は C44 の `maintenance_hydration` queue / worker lifecycle と似た形をしている。

C46 では worker lifecycle を coordinator seam へ移し、root `BMSLibrary` は host として次を提供する。

- shutdown skip / scheduler bridge。
- requested / completed version と running flag の状態更新。
- installable maintenance snapshot count。
- `setModeAndCommitToDB` 実行。
- `setInstallableMaintenanceInfo("installable_maintenance_deferred")` 実行。
- write-lock-held flags の reset。
- install performance log と startup memory checkpoint。

## C46 で触らない範囲

- `setInstallableMaintenanceInfo` の内部。
- resource health index mutation / dispatch。
- maintenance DB schema。
- warning projection。
- BMS / bmson maintenance snapshot attach。
- serialized value / setting name / public API。

## 後続で詳細化する条件

`ApplyMaintenanceHydrationResult` を分割する詳細計画は、次のいずれかが完了した後に作る。

- resource health mutation の top-level contract 化。
- owner view / maintenance attach result の dedicated contract 化。
- private reflection / source-text tests のうち、result apply を固定している上位数件の service / contract 直接テストへの移行。
