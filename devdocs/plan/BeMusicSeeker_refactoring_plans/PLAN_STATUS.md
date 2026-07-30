# PLAN_STATUS

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [作業register](./PERFORMANCE_WORK_REGISTER.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終計画レビュー日: 2026-07-31

## Current checkpoint

- reviewed HEAD: `72445a5029ba12356ac50340e4a399f7292f349f`
- current log evidence: `.tmp/20260731_net472_log`、`.tmp/20260731_.NET 10_log`
- selected distribution: managed bundle＋ReadyToRun
- MVVM／owner structural reorganization: complete
- .NET 10 functional／dependency／data migration: complete
- known deadlock closure: met; invariant protected
- .NET 10 user-visible performance closure: **not met**
- Release Freeze: active

## Review decision

previous `PERF-01` produced useful component improvements but closed the engineering Gate while the real-data logs still show approximately 0.9～1.2 second UI-apply gaps.

The remaining problem is not primarily summary／detail compute. F1 closed the generic settings fan-out、synchronous performance marker write、and unused binding state. F2 stabilized the playlist-summary source、made same-version presentation a no-op、and separated data reset from column-layout invalidation. F3 now commits rows、schema、selection、operation context、and visible playlist mode as one terminal transition、then retires the old detail source after ownership transfer. Data-only table updates preserve column layout and avoid duplicate cell-cache invalidation.

F4 removed the forced full GC immediately before startup catalog allocation、coalesced initialization progress into one latest-snapshot UI commit、and bypassed PLINQ when install estimation is configured sequentially. Existing scan hash canonicalization and indexed BMSON continuation parsing remain covered by synthetic／golden tests.

F5 completed the application-wide grouped audit. Catalog derived state now performs a lightweight version check before detached row materialization、drop-install active progress has one latest-status UI operation while terminal ordering remains explicit、and playlist aggregate lifecycle uses typed table／hydration facts instead of a generic property bus.

The remaining work is the final engineering verification、selected publish／data-update acceptance、fresh outcome review、and user handoff.

## Active outcome

- active outcome: `PERF-02 .NET 10 user-visible performance acceleration`
- active execution package: `F1-F6 Performance-first closure`
- execution anchor: `F6 FINAL-PERFORMANCE-GATE`
- planner state: not required; batch materialized

## Active implementation batch

| Unit | State | Scope |
|---|---|---|
| `F1 FANOUT-AND-DIAGNOSTICS` | completed | typed playlist event、lazy settings refresh、buffered performance log、unused hot-path state退役 |
| `F2 PLAYLIST-SUMMARY-APPLY` | completed | stable source、single presentation apply、summary table invalidation削減 |
| `F3 MAIN-LIST-TRANSITION` | completed | detail／summary／library atomic mode transition、CustomTableView fast path |
| `F4 STARTUP-ESTIMATION-SCAN-PARSE` | completed | forced GC、progress fan-out、sequential estimation overheadを除去しsingle-pass scan／parser contractを維持 |
| `F5 APPLICATION-WIDE-PERF-AUDIT` | completed | catalog version-first、drop progress coalescing、typed playlist lifecycle |
| `F6 FINAL-PERFORMANCE-GATE` | active | full verification、publish、review、manual handoff |
| `HANDOFF` | pending | final artifactの一回実データ確認へhandoff |

## Execution rules

- active／pending unitがある間はunit-plannerを起動しない。
- production dataやnet472再計測がないことを理由にunitを延期しない。
- `DIRECT_FIX`と`LIKELY_OPTIMIZATION`はbehavior／concurrency testを用意して実装する。
- instrumentation-onlyでは既知UI遅延を閉じない。
- unit commitは内部checkpointであり、`F6`完了までユーザー応答で停止しない。
- status更新は対応するcode／test commitへ含める。
- unrelated worktree差分へ触れない。

## Exit

`F6`が通過したら次へ更新する。

```text
strict Refactoring Completion Gate: met
concurrency / responsiveness acceptance: met
.NET 10 user-visible performance engineering: complete
engineering migration: complete
active outcome: none
active implementation batch: empty
selected distribution: managed bundle + ReadyToRun
post-engineering real-data performance acceptance: pending user action; non-blocking
```
