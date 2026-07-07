# REF-MVP-C1 BMSLibrary test blockers

[PLAN_STATUS](../PLAN_STATUS.md) / [P0-02](../P0-02_BMSLibrary_ドメインFacade化計画.md)

最終更新日: 2026-07-07

## 現在の結論

`BMSLibrary.cs` 単体配置に依存していた source-text tests は `SourceTextTestHelper.ReadBmsLibrarySourceText()` 経由へ移した。これにより、`BMSLibrary.cs` の partial split や `BmsLibraryInternal` service への移動で source-text architecture check が壊れにくくなった。

## 解消した blocker

- `BmsLibraryMutationBoundaryTests.MainWindowViewModel_UsesChartPackageMutationBoundary` は `BMSLibrary.cs` 単体ではなく logical BMSLibrary source set を読む。
- `MainWindowContextMenuResourceTests` 内の BMSLibrary source-text assertions は logical BMSLibrary source set を読む。
- `ReadBmsLibrarySourceText()` は `BeMusicSeeker/Models/BMSLibrary.cs`、top-level `BMSLibrary*.cs`、`Models/BmsLibrary/`、`Models/BmsLibraryInternal/` を deterministic order で連結する。

## 残存 blocker

- `BMSLibrary` private reflection tests が多数残る。代表例は `BmsLibraryLr2SongDbSyncTests`、`BmsLibraryFolderRenameRefreshTests`、`BmsLibraryMaintenanceServiceTests`、`OwnedChartCollectionStateTests`。
- private method / field 名に直接依存しているため、service extraction 時は対応する service / state object の public or internal test seam へ移す必要がある。
- `MainWindowContextMenuResourceTests` には production source-text check がまだ多い。今回の対象は `BMSLibrary.cs` 単体配置依存の解消に限定し、UI / resource / XAML の個別 file read は対象外。

## 次に減らす候補

1. `BmsLibraryMutationBoundaryTests` の source-text assertions を、可能な範囲で `BMSLibrary` facade と internal service の境界テストへ分割する。
2. `BmsLibraryLr2SongDbSyncTests` の private reflection helper を、LR2 sync coordinator extraction 時に service 直接テストへ移す。
3. `BmsLibraryFolderRenameRefreshTests` / `OwnedChartCollectionStateTests` の private field access は、folder/file operation coordinator または owned collection state の test seam へ移す。
