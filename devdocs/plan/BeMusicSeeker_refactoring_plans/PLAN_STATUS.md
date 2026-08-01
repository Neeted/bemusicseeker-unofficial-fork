# PLAN_STATUS

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [register](./PERFORMANCE_WORK_REGISTER.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [共通ルール](./00_Codex共通実行ルール.md)

最終計画レビュー日: 2026-07-31

## Current checkpoint

- reviewed HEAD: `c5ffed1379ce20b66010dd4a00f1333bd5743ba2`
- current logs:
  - `.tmp/20260731_log_.NET 10 PC起動後 初回起動`
  - `.tmp/20260731_log_.NET 10 PC起動後 2回目起動`
  - `.tmp/20260731_log_.NET 10 PC起動後 2回目起動 画面遷移問題なし`
- selected distribution: `bundle-r2r`、cold-boot decisionはprovisional
- MVVM／owner structural reorganization: complete
- .NET 10 functional／dependency／data migration: complete
- concurrency／deadlock acceptance: met、継続保護
- list-transition performance acceptance: met、継続保護
- cold-start initialization acceptance: not met
- Release Freeze: active

## Review decision

一覧画面の遷移は、ログとコードの双方で改善を説明できる。

- playlist summary cached revisit: 約32 ms
- playlist detail: 約129 ms
- full library: 約23～60 ms
- stable versioned source、atomic main-table commit、data-only invalidationが成立

一方、PC起動後初回だけ次の再現性がある。

```text
startup_ready_ui             23.225 s
startup_ready_operable       88.721 s
startup_initialization_complete 103.315 s
```

warm runではそれぞれ約24.1～24.4 s、24.4～24.8 s、38.2～39.1 sである。cold penaltyはoptional library-folder deferred refreshがoperabilityとstartup schedulerをgateするdependencyに集中する。

## Active outcome

- active outcome: `PERF-03 .NET 10 cold-start initialization closure`
- execution anchor: `S1 STARTUP-READINESS-GATE`

## Active implementation batch

| Unit | State | Closure |
|---|---|---|
| `S1 STARTUP-READINESS-GATE` | active | folder-tree completionからoperability／schedulerを分離し、exact wait markerと決定的testを追加 |
| `S2 STARTUP-TAIL-CONTRACT` | pending | required initializationとoptional maintenanceを分離 |
| `S3 STARTUP-CONTENTION-AND-OWNERSHIP` | pending | generic Task.Run、reader wait、Dispatcher queue、global ThreadPool tuningを整理 |
| `S4 COLD-BOOT-DISTRIBUTION-FALLBACK` | pending | bundle-r2r／folder-r2rの同一HEAD artifactとmanual decision path |
| `S5 FINAL-STARTUP-GATE` | pending | Full verification、publish、review、handoff |
| `HANDOFF` | pending | engineering完了、PC再起動後一回確認へhandoff |

active batchはmaterialize済みである。unit-plannerを起動しない。

## Exit state

```text
strict Refactoring Completion Gate: met
concurrency / responsiveness acceptance: met
list-transition performance acceptance: met
.NET 10 cold-start engineering: complete
engineering migration: complete
active outcome: none
active implementation batch: empty
selected distribution: bundle-r2r provisional or manual-selected folder-r2r
post-engineering cold-boot acceptance: pending user action; non-blocking
```
