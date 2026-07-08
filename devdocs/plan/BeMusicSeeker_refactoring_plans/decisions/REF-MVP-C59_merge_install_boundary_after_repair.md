# REF-MVP-C59 Merge / Install Boundary After Repair Seam

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C60: MergeChartDirectory coordinator seam` とする。

## 理由

C56 で package move adapter、C58 で repair workflow が coordinator seam になった。次に残る package move 呼び出し元のうち、MVP Gate に最も近づくのは `MergeChartDirectory` である。

`MergeChartDirectory` はまだ root `BMSLibrary` に次の orchestration をまとめて持っている。

- LR2 sync mutation block。
- initialized-min reader / pending writer / BMS writer lock。
- detailed performance logging。
- source chart refs / install destination overlay snapshot。
- `PrepareMergeDirectory` service call と installed hash snapshot callback。
- source BMS / bmson user column snapshot。
- source unregister delta apply。
- reverse lookup remove / add。
- package move through `MoveChartPackageFiles` seam。
- destination directory scan。
- reference delta apply。
- moved target DB upsert。
- destination + moved target maintenance apply。
- final owned chart state apply。
- failure dialog / installed directory index invalidation。

これらは横断的だが、C56 / C58 後は core file move と repair workflow が固定されているため、次は root に残る merge orchestration を coordinator / host 境界へ移すのが妥当である。

## Source-text test 方針

`MainWindowContextMenuResourceTests.DuplicateMergeMaintenanceDefersResourceHealthIndexRebuildAndLogsDuplicateSearchStages` は現在 `internal void MergeChartDirectory(string src, string dst, long operationId)` の method body を直接検査している。

`SourceTextTestHelper.ReadBmsLibrarySourceText()` は `BeMusicSeeker/Models/BmsLibraryInternal` も連結して読むため、C60 では root method body に固執せず、`LibraryMergeDirectoryCoordinator` 側の coordinator method body または BMSLibrary source set 全体を検査する形へ移す。source-text test を削除するのではなく、検査対象を新しい責務配置へ合わせる。

## 他候補を今選ばない理由

### `installChartPackages` follow-up

`installChartPackages` は `PackageInstallExecutionResult` の callbacks、DB upsert、maintenance、score update、state apply、reverse lookup update、inline chart_info、installed package registration、estimated batch context をまたぐ。`MergeChartDirectory` 後に、package install coordinator へ切る前提が見えた段階で詳細化する。

### maintenance result apply contract prep

C45 の blocker が残る。`ApplyMaintenanceHydrationResult` / `DispatchMaintenanceHydrationResult` は owner view、resource health full rebuild dispatch、maintenance DB cleanup、private reflection / source-text tests が重なる。`MergeChartDirectory` と `installChartPackages` の state apply / maintenance seam がもう少し整理された後に詳細化する。

### repair seam 追加整理

C58 で repair workflow の root orchestration は coordinator seam へ移った。追加整理は C60 の merge seam を妨げる blocker ではないため、今は選ばない。

## C60 で触らない範囲

- `BmsLibraryLibraryFileOperationsService.PrepareMergeDirectory` core behavior。
- `BmsLibraryPackageInstallService.MovePackageFiles` core behavior。
- `MoveChartPackageFiles` seam contract。
- `installChartPackages` callback contract。
- maintenance hydration result apply、resource health dispatch。
- DB schema / setting name / resources / persisted value。

## 成功条件

- `MergeChartDirectory` public/internal surface、null guard、LR2 sync block、lock order、operationId logging が維持されている。
- source unregister、reverse lookup remove/add、package move、destination scan、reference delta apply、DB upsert、maintenance apply、final state apply の順序と意味が維持されている。
- BMS / bmson merge、duplicate skip、LR2 user columns、resource-health defer の既存 tests が通る。
- source-text test は新しい coordinator 配置を検査しており、root method body 固定に戻っていない。
- build / merge・package move・repair 関連 tests / format / diff check / Roslynator warning / 静的レビューが完了している。
