# .NET 10 Performance Engineering Evidence

[性能計画](../plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md) / [現在地](../plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md) / [register](../plan/BeMusicSeeker_refactoring_plans/PERFORMANCE_WORK_REGISTER.md)

この文書はcurrent-only reportである。逐次unit履歴はGit historyへ委ねる。

## Reviewed snapshot

```text
HEAD: c5ffed1379ce20b66010dd4a00f1333bd5743ba2
logs:
  .tmp/20260731_log_.NET 10 PC起動後 初回起動
  .tmp/20260731_log_.NET 10 PC起動後 2回目起動
  .tmp/20260731_log_.NET 10 PC起動後 2回目起動 画面遷移問題なし
```

## List transition decision

一覧画面のユーザー体感問題は解消済みと判断する。

| Route | Current log evidence |
|---|---:|
| playlist summary 初回 | input→first useful visible 約594 ms。compute 480 ms、UI apply約3 ms、first render 109 ms |
| playlist summary 同一cache再訪 | input→first useful visible 約32 ms |
| playlist detail 114 rows | input→first useful visible 約129 ms |
| full library 211,867 rows | input→first useful visible 約60 ms、再訪約23 ms |

コード上の説明:

- `PlaylistSummaryVersionedCollection`がstable source identityを保持し、同一versionの再訪でcollection replacementを行わない。
- `CommitMainTablePresentationWithoutNotification`と`PublishMainTablePresentation`がrows、mode、selectionを一つのterminal transactionで公開する。
- `CustomTableView`はdata-only source changeでcolumn layout snapshotを破棄しない。
- settings fan-outはtyped playlist table／catalog eventへ分離され、presentation propertyから無関係なsettings更新を起こさない。
- performance markerはbounded asynchronous writerを使う。
- normal-library refresh producerはUI完了を同期waitしない。

これらは今後のstartup修正で保護する。

## Reproducible cold-start issue

| Run | `startup_ready_ui` | `startup_ready_operable` | UI→operable gap | `startup_initialization_complete` | operable→complete |
|---|---:|---:|---:|---:|---:|
| PC起動後 初回 | 23,225 ms | 88,721 ms | 65,496 ms | 103,315 ms | 14,594 ms |
| 2回目 | 24,434 ms | 24,779 ms | 345 ms | 39,101 ms | 14,322 ms |
| 2回目・画面遷移確認 | 24,140 ms | 24,418 ms | 278 ms | 38,176 ms | 13,758 ms |

song-table load、Everything／BMS scan、`startup_ready_ui`までの主要phaseはcold／warmで同程度である。約65秒のcold penaltyは、ほぼ全て`startup_ready_ui`後、`startup_ready_operable`前に存在する。

cold runでは次の順序である。

```text
startup_ready_ui                    23.225 s
post_initialize_gc                 +30 s付近、515 ms
library folder deferred apply      88.5 s付近
parent_folder_cache rebuildMs      119 ms
startup_ready_operable             88.721 s
startup background scheduler start 88.721 s
startup_initialization_complete    103.315 s
```

`parent_folder_cache rebuildMs=119`はcache algorithm部分だけで、requestからworker開始、reader-lock wait、path snapshot、Dispatcher queue waitを含まない。

## Cause assessment

### Confirmed

`MainWindowViewModel.TryLogStartupReadyOperable`は、通常入力のunblockと`StartupBackgroundTaskSchedulerOwner.Start()`を同時に行う。startup UI flushにlibrary-folder refreshが含まれる場合、この呼出しは`LibraryFolderTreeViewModel.DeferredRefreshCompleted`まで延期される。

そのrefreshは次の二段queueである。

```text
Task.Run
  → BMSLibrary.BuildBMSParentFolderListCacheSnapshot
  → UI scheduler at Background priority
  → DeferredRefreshCompleted
  → startup_ready_operable / scheduler_start
```

したがって、optional folder-tree presentationが遅れると、アプリ操作可能化と全startup background workが同じ時間だけ遅れる。これが100秒化の直接原因である。

### Highly likely contributor

UI applyは`UiSchedulePriority.Background`であり、cold runではrequest後約65秒間completionがない。exact split markerがないため、ThreadPool queue、`rwlockBMSFiles` reader wait、path snapshot、Dispatcher Background queueのどこが支配したかは未確定である。ただし、いずれであってもoptional low-priority refreshをglobal readiness gateにした設計は不適切である。

### Not the primary cause

現行publishはmanaged bundle＋ReadyToRunだが、native self-extract、all-content extraction、single-file compressionを無効にしている。cold penaltyはprocess起動前や`startup_ready_ui`前ではなくmanaged startup route内の65秒gapなので、配布形式だけでは説明できない。

## Current decision

`PERF-03 .NET 10 cold-start initialization closure`を開始する。

1. operabilityとstartup schedulerをoptional folder-tree completionから分離する。
2. required initializationとpost-initialization maintenanceを分離する。
3. folder refreshのworker／lock／Dispatcher waitを可視化し、generic `Task.Run`／global min-thread tuningを整理する。
4. code fix後にbundle-r2rとfolder-r2rをPC再起動後一回だけ比較できるhandoffを用意する。
