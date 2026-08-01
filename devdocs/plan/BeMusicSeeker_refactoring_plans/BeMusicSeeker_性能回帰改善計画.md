# BeMusicSeeker .NET 10 性能改善計画

[current evidence](../../acceptance/net10-performance-engineering.md) / [distribution](../../acceptance/net10-distribution-performance.md) / [現在地](./PLAN_STATUS.md) / [register](./PERFORMANCE_WORK_REGISTER.md)

## 目的

.NET 10版のMVVM／concurrency／data compatibilityを維持しつつ、PC起動後初回だけ`startup_initialization_complete`が約100秒になる問題を解消する。

画面遷移は、stable summary source、atomic main-table presentation、data-only invalidationによって実用水準へ改善済みである。これらを再設計せず保護する。

## 現在の診断

cold／warmの主要scanやDB loadは同程度である。cold-only penalty約65秒は次へ集中する。

```text
startup_ready_ui
  → deferred library-folder preparation
  → Dispatcher Background apply
  → DeferredRefreshCompleted
  → startup_ready_operable
  → startup background scheduler start
```

`startup_ready_operable`が遅れるため、schedulerにqueue済みのhydration／maintenanceも65秒後まで開始されず、最終的に`startup_initialization_complete`が約103秒になる。

exact waitの内訳は現行logだけでは確定できない。候補はThreadPool queue、library reader-lock wait、211k path snapshot、Dispatcher Background queueである。しかし、optional tree refreshをglobal readiness gateにしたdependency自体は、内訳に関係なく修正対象である。

## Target architecture

```text
required data / initial main presentation
  ↓
startup_ready_ui
  ↓
startup_ready_operable + required background scheduler start
  ├─ required local hydration
  │    ↓
  │  startup_initialization_complete
  └─ optional presentation / audit / network / export / prewarm
       ↓
     startup_post_initialization_maintenance_complete
```

library folder tree:

```text
versioned refresh request
  → owned preparation lane
  → short model snapshot
  → coalesced UI apply
```

folder treeが遅延しても通常操作とrequired hydrationは進む。表示時に未準備ならloading stateまたはlast valid snapshotを使い、同期buildでUIを止めない。

## Ordered implementation batch

### S1 — `STARTUP-READINESS-GATE`

分類: `READINESS_DEFECT`＋`DIRECT_FIX`

1. folder refreshへ同一interaction IDのmarkerを追加する。
   - request accepted
   - worker queued／started
   - model reader wait start／end
   - path snapshot complete
   - cache build complete
   - UI queued／started／applied
   - stale／reschedule reason
2. `startup_ready_operable`と`StartupBackgroundTaskSchedulerOwner.Start()`を`DeferredRefreshCompleted`から分離する。
3. required UI maskが適用された同じstartup transitionでoperabilityを確定し、folder refreshは独立して継続する。
4. `LibraryFolderTreeDeferredRefreshCompleted`はtree-specific readinessだけを通知する。
5. `Background` operationまたはreader guardを意図的にblockしても、operability、scheduler start、required tasksが進む決定的testを追加する。
6. folder treeのlatest-version coalescing、eventual apply、shutdown abortを維持する。

禁止:

- priorityをNormalへ上げるだけでglobal dependencyを残す
- timeoutでoperable扱いにする
- refreshを捨てる
- UI threadで同期cache buildする

### S2 — `STARTUP-TAIL-CONTRACT`

分類: `DIRECT_FIX`＋`LIKELY_OPTIMIZATION`

1. scheduler taskを`required initialization`と`post-initialization maintenance`へ分類する。
2. `startup_initialization_complete`はrequired local hydrationだけを待つ。
3. 次は原則post-initializationへ移す。
   - external／network catalog
   - playlist URL completion
   - custom-folder physical consistency audit
   - beatoraja export
   - best-effort prewarm
4. `playlist_custom_folder_output_repair`はeventual repairを維持するが、`pendingCount=0`でも行う335,550-entry physical auditでrequired completionを止めない。
5. featureをaudit完了前に開いた場合のlazy ensure／updating state／explicit refreshを定義する。
6. post-initialize GCはrequired tasksと競合しないidle／memory-pressure ownerへ移し、startup開始からの固定delayだけで発火させない。
7. required task failureとoptional task failureをUI／logで区別する。

### S3 — `STARTUP-CONTENTION-AND-OWNERSHIP`

分類: `DIRECT_FIX`＋`OBSERVABILITY_SUPPORT`

1. parent-folder preparationをgeneric `Task.Run`から明示startup／folder-refresh owner laneへ移す。
2. model reader-lock waitとpath snapshotを短縮し、同じcatalog versionのsnapshotを再利用できる場合はowner-scoped version cacheを使う。
3. `Task.Run`→Background Dispatcherという二段queueを必要最小限にする。
4. `ThreadPool.SetMinThreads(200, 200)`をcurrent routeで再評価する。
   - readiness dependency修正後にqueue waitがなければ削除する。
   - queue waitが残る場合も、global magic numberではなくowned lane／bounded concurrencyを優先する。
5. startup markersへThreadPool thread count／pending work、reader wait、Dispatcher queue waitをaggregateで記録する。
6. startup progress animationやbindingがBackground operationを飢餓させても、required readinessへ影響しないことを確認する。

### S4 — `COLD-BOOT-DISTRIBUTION-FALLBACK`

分類: `POST_ENGINEERING_MANUAL`を支えるengineering task

1. 同じHEADから`bundle-r2r`と`folder-r2r`を再現できるpublish command／validatorを維持する。
2. app code、R2R、native assets、settings／data layoutを同一にする。
3. folder profileはstandard host layoutとし、custom loader／managed relocationを導入しない。
4. process start、managed startup start、`startup_ready_ui`、`startup_ready_operable`、`startup_initialization_complete`を両artifactで記録できるようにする。
5. code fix完了後のPC再起動比較を`POST_MIGRATION_MANUAL_ACCEPTANCE.md`へhandoffする。
6. manual resultがfolder選択条件を満たした場合に、profile、publish script、validator、update manifestを一括変更できる単一follow-upを定義する。

S4はmanual result待ちでCodexを停止しない。defaultは引き続き`bundle-r2r`とする。

### S5 — `FINAL-STARTUP-GATE`

1. 全5 projectのlocked restore、Release build、full tests、analyzer。
2. blocked folder-refreshでもoperabilityとrequired schedulerが進むtest。
3. folder tree eventual apply、rapid reentry、shutdown。
4. playlist summary／detail／libraryの成立済みperformance contract。
5. estimated-install、scan／parse golden behavior、deadlock regression。
6. selected main app／updater Self-contained publish。
7. existing-data、update success、rollback。
8. frozen snapshot fresh review、重大指摘修正後の再検証。

Engineering exit:

- optional folder tree／Background workがglobal readinessをgateしない。
- cold-only 65秒gapを生むdependencyがsourceから消えている。
- `startup_initialization_complete`のrequired task契約が明示されている。
- optional maintenanceはeventual completionとfailure visibilityを持つ。
-画面遷移改善とdeadlock修正を維持する。
- cold-boot distribution比較に必要な最終artifactとmarkerが揃っている。
