# .NET 10 Performance Engineering Evidence

[性能計画](../plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md) / [現在地](../plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md) / [手動受入れ](../plan/BeMusicSeeker_refactoring_plans/POST_MIGRATION_MANUAL_ACCEPTANCE.md)

この文書はcurrent-only reportである。過去unitの逐次logはGit historyとignored artifactsへ委ねる。

## Evidence policy

- net472との再比較は行わない。
- production dataをCodex／CIのprerequisiteにしない。
- synthetic corpusが安全に構築できるcomponentだけを測定する。
- full WPF render、実library分布、Everything／disk／antivirusは`MANUAL-02`へhandoffする。
- raw CSV／trace／benchmark outputは`artifacts/performance/net10-engineering/`へ置き、commitしない。

## Corpus matrix

P1で次をcurrent codeに対して確定する。

| Corridor | State | Generator / fixture | Scale | Golden behavior | Command |
|---|---|---|---|---|---|
| normal library projection | planned | fixed-seed `LibraryChartRow`／index generator | small／medium／large | row、sort、filter、folder summary | pending |
| playlist summary／detail compute | planned | generated tables／entries／rows | small／medium／large | count、sort、filter、selection | pending |
| UI queue／generation | planned | fake scheduler／STA harness | burst／rapid reentry | coalescing、latest generation、bounded turn | pending |
| install destination estimation | planned | existing temp-directory helper | chart／candidate／resource matrix | selected path、confidence、warning | pending |
| managed scan／parse | planned | generated BMS／BMSON＋repository fixture | no-diff／small／large | diff、DB rows、parse output | pending |
| song-table／resource-health | feasibility | temporary SQLite／resource snapshots | pending | output／index equivalence | pending |
| full startup／WPF render／native scan | manual | final artifact＋real data | one final session | log completeness／no unexplained stall | `MANUAL-02` |

## Instrumentation schema

current .NET 10 logは次を一つのinteraction IDで相関する。

```text
input accepted
owner queued
owner started
snapshot / query / projection complete
UI queued
UI started
view applied
first useful visible
```

aggregate fields:

```text
route
interactionId
generation
stage
elapsedMs
queueWaitMs
rowCount / itemCount
cacheHit
coalescedCount
thread / lane
result / cancellation
```

per-row／per-file logは行わない。diagnostic logging無効時のallocationとI/Oをtargeted test／reviewで確認する。

## Unit results

| Unit | Commit | Corpus / classification | Before | After | Structural result | Raw artifact hash | Decision |
|---|---|---|---|---|---|---|---|
| P1 | pending | pending | — | — | — | — | pending |
| P2 | pending | pending | — | — | — | — | pending |
| P3 | pending | pending | — | — | — | — | pending |
| P4 | pending | pending | — | — | — | — | pending |
| P5 | pending | engineering Gate | — | — | — | — | pending |

Before／Afterは同じ.NET 10 path、同じmachine、same corpusで取得する。actual real-data elapsedはこの表へ推測で記入しない。

## Engineering completion

P5通過時に次を記録する。

```text
Final engineering commit:
Artifact SHA-256:
Selected profile:
Synthetic suite command:
Synthetic suite result:
Instrumentation validation:
Full test / analyzer result:
Fresh review result:
MANUAL-02 handoff: ready
```
