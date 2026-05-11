# Workflows

この資料は主要操作の現行フローをまとめる。startup の詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を正本にする。

## Startup

入口: `MainWindowViewModel.Initialize()`

概略:

1. bmson migration preflight / startup migration / final preflight。
2. metadata bundle import。
3. catalog DB load。
4. score DB load と LR2IR player score XML prefetch。
5. file enumeration と native canonical resource index build。
6. file diff apply。
7. pending package restore。
8. install readiness publish。
9. UI refresh / operable。
10. startup background scheduler。

install readiness は playlist、score/ranking、chart_info、maintenance hydration を待たない。これらは background task として扱う。

## ReloadFileDiff

入口: library reload 操作。

目的:

- DB を読み直さず、現在の memory catalog と file scan result の差分を反映する。
- file enumeration / file diff / playlist reference apply を行う。

通常起動と違い、起動済み memory catalog / resource index を正本にできる。

## ReloadTables

入口: playlist/table reload 操作。

目的:

- playlist table header と playlist entries を読み直す。
- external playlist sync を再スケジュールする。
- score DB load、score snapshot rebuild、ranking refresh は行わない。

library の full file scan とは別操作である。

## FullReinitialize

入口: library 初期化再実行操作。

目的:

- 外部 DB 編集や状態修復を想定し、startup 相当の catalog load / file enumeration / file diff を再実行する。
- post-startup operation なので background scheduler は runnable に保つ。

## Install Estimation

導入先推定は [install-estimation-current-logic.md](install-estimation-current-logic.md) を正本にする。

概要:

- pending package の source surface と destination `DirectoryResourceLookupCache` を使う。
- candidate directory は destination resource index から得る。
- final evaluation は audio / image / movie の chart-relative resource key で行う。
- resource index がない場合は `resource_index_unavailable` として推定不可にする。

## Install / Merge / Reinstall Correction

- install / merge 後は catalog、song DB、resource index、maintenance、chart_info の更新境界を明確に保つ。
- folder operation 後の resource index cleanup は `DirectoryResourceLookupCache.Keys` を正本にする。
- reinstall correction は candidate-only の health 評価を使い、source/bundled resource を混ぜない。

## UI Update Suppression

大量更新中は UI refresh を抑制し、最後に必要な channel だけ flush する。

代表例:

- library main view
- library folder tree
- install tree
- playlist tree
- duplicate tree

startup / reload / install の progress 表示は [startup-reload-progress.md](startup-reload-progress.md) を参照する。
