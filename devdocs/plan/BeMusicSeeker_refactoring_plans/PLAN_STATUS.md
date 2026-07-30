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

The remaining problem is not primarily summary／detail compute. Current source shows high-confidence structural work on the UI critical path:

- generic playlist workspace `PropertyChanged` is used as a settings/catalog change bus.
- summary revisit replaces the entire collection and binding source.
- data-only source changes invalidate column layout.
- detail→library publishes old source clear and multiple related presentation properties around the new source apply.
- performance marker writes use synchronous file targets.

These are engineering defects, not a final manual-measurement-only concern. The performance Outcome is reopened.

## Active outcome

- active outcome: `PERF-02 .NET 10 user-visible performance acceleration`
- active execution package: `F1-F6 Performance-first closure`
- execution anchor: `F1 FANOUT-AND-DIAGNOSTICS`
- planner state: not required; batch materialized

## Active implementation batch

| Unit | State | Scope |
|---|---|---|
| `F1 FANOUT-AND-DIAGNOSTICS` | active | typed playlist event、lazy settings refresh、buffered performance log、unused hot-path state退役 |
| `F2 PLAYLIST-SUMMARY-APPLY` | pending | stable source、single presentation apply、summary table invalidation削減 |
| `F3 MAIN-LIST-TRANSITION` | pending | detail／summary／library atomic mode transition、CustomTableView fast path |
| `F4 STARTUP-ESTIMATION-SCAN-PARSE` | pending | likely optimizationsを実測待ちにせず実装 |
| `F5 APPLICATION-WIDE-PERF-AUDIT` | pending | generic event bus、copy、queue、invalidationの横断grouped fix |
| `F6 FINAL-PERFORMANCE-GATE` | pending | full verification、publish、review、manual handoff |
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
