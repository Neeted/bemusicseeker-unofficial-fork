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

| Corridor | State | Generator / fixture | Scale | Golden behavior | Command |
|---|---|---|---|---|---|
| normal library projection | `SYNTHETIC_MEASURABLE` | fixed-seed rows／immutable index、既存owner helper | 1,000／25,000／200,000 rows | row、sort、filter、folder summary | `P2` |
| playlist summary／detail compute | `SYNTHETIC_MEASURABLE` | generated tables／entries／rows、既存aggregation helper | small／medium／large | count、sort、filter、selection | `P2` |
| UI queue／generation | `SYNTHETIC_MEASURABLE` | fake scheduler／dedicated STA harness | burst／rapid reentry | coalescing、latest generation、bounded turn | `P2` |
| install destination estimation | `SYNTHETIC_MEASURABLE` | existing temp-directory helper | 71／100／399 resources | selected path、confidence、warning | `P4` |
| managed scan／parse | `SYNTHETIC_MEASURABLE` | generated BMS／BMSON＋repository fixture | no-diff／120／160 charts | diff、DB rows、parse output | `P4` |
| song-table materialization | `SYNTHETIC_MEASURABLE` | temporary SQLiteと既存load owner | small／medium／large rows | loaded rows、index publication | `P3` |
| resource-health component | `SYNTHETIC_MEASURABLE` | immutable synthetic resource snapshotと既存owner | small／medium／large targets | warning／ignored／index output | `P3` |
| post-initialize GC | `OBSERVABILITY_REQUIRED` | aggregate startup marker | one aggregate event | pause／retained-memory component fields | `P3` |
| full startup／WPF render／Everything／disk | `MANUAL_REAL_DATA` | final artifact＋real data | one final session | log completeness／no unexplained stall | `MANUAL-02` |

共通generatorはseed `0xBEE501`から再構築し、一時workspaceを実行後に削除する。
corpus contractは次で確認する。

```powershell
pwsh -NoProfile -File .\scripts\benchmark-net10-performance.ps1 -Corpus contract
```

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
| P1 | same unit commit | fixed-seed corpus contract＋全corridor分類 | — | deterministic fingerprint／disabled-path formatter 0 calls | interaction／generation schema、aggregate marker、manual boundaryを固定 | ignored command receipt | accepted |
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
