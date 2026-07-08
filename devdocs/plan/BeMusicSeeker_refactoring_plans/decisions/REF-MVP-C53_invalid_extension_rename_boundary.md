# REF-MVP-C53 Invalid Extension Rename Boundary

[P0-02 へ戻る](../P0-02_BMSLibrary_ドメインFacade化計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

## Decision

次の実装 ticket は `REF-MVP-C54: invalid extension rename coordinator seam` とする。

対象は normal library 側の `RenameBMSFilesExtensions` と manual pending 側の `RenamePendingBmsFormatChartFileExtensions`。既に coordinator 化済みの `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` は回帰確認対象に留める。

## 理由

C52 後の file operation 候補のうち、invalid extension rename は core 処理が既に service 側へ寄っている。

- normal library 側は `BmsLibraryLibraryFileOperationsService.RenameLibraryFileExtensions` が rename / duplicate delete / mutation delta を返す。
- manual pending 側は `BmsLibraryPackageInstallService.RenamePendingBmsFormatChartFileExtensions` が rename / duplicate delete / pending remove target / failures を返す。
- root `BMSLibrary` には LR2 sync block、lock、service call、failure dialog、state apply / pending removal、summary log が残っている。

normal 側だけ、または pending 側だけを切ると rename 境界が非対称に残る。C54 では両者を同じ `InvalidExtensionRenameCoordinator` に寄せ、root は host bridge へ近づける。

## 他候補を今選ばない理由

### `MoveChartPackageFiles`

core は既に `BmsLibraryPackageInstallService.MovePackageFiles` にある。残りは options / dialog / log / path factory adapter で、install、merge、fix-installation から共有される。単独で切ると package install command 境界が中途半端になる。

### `MergeChartDirectory`

C47 / C49 / C51 の判断を維持する。source unregister、reverse lookup remove/add、`MoveChartPackageFiles`、destination scan、DB upsert、maintenance、owned state apply が一体で、1 ticket としては横断しすぎる。

### package install follow-up

`ForceInstallPendingPackages`、estimated install、pending resource overwrite、pending cleanup は coordinator seam 済み。残る `installChartPackages` は DB / maintenance / inline chart_info / package registration をまたぐため、rename 境界より重い。

### pending zero-note rename

`RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` は `PendingZeroNoteRenameCoordinator` と host partial 済み。C54 では同じ invalid extension rename の shared low-level processing を壊さない回帰確認対象に留める。

## C54 で触らない範囲

- `MoveChartPackageFiles`。
- `MergeChartDirectory`。
- `installChartPackages` / package install batch。
- `PendingZeroNoteRenameCoordinator` の deferred progress behavior。
- maintenance result apply、resource health mutation / dispatch、maintenance DB schema、warning projection。
- persisted value / setting name / DB schema / serialized value。

## 成功条件

- normal rename の LR2 sync block、lock order、`unregister` 時の remove / path change、duplicate delete、failure dialog、`ApplyLibraryMutationDelta`、`invalid_ext_rename summary scope=normal` が維持されている。
- manual pending rename の null guard、lock order、BMS-format 限定、bmson 除外、failure dialog、`RemovePendingChartsFromPendingPackagesAndInstallRows`、`invalid_ext_rename summary scope=pending` が維持されている。
- `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` の coordinator / deferred progress behavior が変わっていない。
- build / rename 関連 tests / format / diff check / Roslynator 対象確認 / 静的レビューが完了している。
