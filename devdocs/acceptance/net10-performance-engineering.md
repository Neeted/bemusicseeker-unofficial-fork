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
| song-table materialization／publication | `SYNTHETIC_MEASURABLE` + `MANUAL_REAL_DATA` | existing temporary SQLite golden tests＋owned row-reference publication corpus | small／medium／large rows | loaded columns、row identity／order、publication copy count | `P3`。実DB query絶対時間は`MANUAL-02` |
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
terminal apply started / applied
UI queued / started / applied（dispatcher queueが実在するrouteだけ）
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
| P2 | same unit commit | fixed-seed normal-library list＋existing playlist queue／generation fixtures | source-reference materializations 1,000／25,000／200,000、allocated bytes 20,536／598,328／4,517,880 | materializations 0、owned index snapshot込みallocated bytes 7,496／173,264／1,122,728、folder output一致 | immutable source＋owned read-only index snapshot、1,024-row cancellation boundary、guard外shutdown drain。playlistのstale／dedupe／latest-generation／selection behaviorを維持 | `8417F007771BA673BD61E6CE7A46B0284BEFC76BF48FE76A68C8A660A85F2377` | accepted。playlist detailはbackground terminal applyを明示し、actual WPF first-visibleは`MANUAL-02` |
| P3 | same unit commit | fixed-seed BMS／BMSON song-table publication＋immutable resource-health snapshots | song copy 2、各route allocated 16,112／400,112／3,200,112。resource-health allocated 9,859,400／244,531,336／1,961,633,696 | song copy 1、各route allocated 8,056／200,056／1,600,056。resource-health allocated 413,400／10,089,752／82,279,704 | BMS／BMSON全row identity／order、warning kind／message、ignored membership、case-insensitive duplicate first-wins一致。GC request outstanding数、startup schedulerのqueued／running／backlog、before-after memory markerを全terminal outcomeへ追加し削除判断をmanualへ分離 | `C0D46191FECF76F2AB24DDB8185F3A6BFED0493DDE43E5D7F2D8AAACB7046C63` | accepted。full startup／GC benefitは`MANUAL-02` |
| P4 | same unit commit | fixed-scale install candidates＋managed resource indexes＋pathological equal-Y BMSON notes | candidate views 16／128／1,024 builds、managed index allocation 65,776／1,511,824／13,215,568 bytes、BMSON continuation probes 499,500／7,998,000／127,992,000 | candidate views 8／64／512 builds、managed index allocation 12,184／300,184／2,400,184 bytes、BMSON continuation probe upper bound 1,999／7,999／31,999 | candidate resource viewをgate／final evaluationで共有、canonical managed scanのsorted distinct配列を再sortせず所有copyしuntrusted／merged入力はcanonical化、BMSON continuationを単調indexで走査。selected destination、hash arrays、parser golden behavior一致 | estimation `8A342DA80CE5BC8475E4955C57EE5E2E5E2D682C00060576A7B1207B7E31880C`／scan `2FA487DD1136D4BDED1DC55D57CA69E884906AC996CB736E24FA91754DA10AB5`／parser `2D48E6536EFB87DB4E47AB038878F3B01FEC1143AF3289AD0B367D85BD65422D` | accepted。native Everything／diskと実data全体性能は`MANUAL-02` |
| P5 | same unit commit | engineering Gate | P1～P4 current synthetic evidence、selected publish、existing-data／update acceptance | 13 synthetic tests passed、3,547 full tests passed／16 skipped、Roslynator 0 diagnostics、selected publish／existing-data／baseline-to-current update／fault rollback passed | update acceptanceをretired net472 buildからcommitted .NET 10 baselineへ変更。instrumentation contract 6 testsでdisabled formatter 0 callsとcorrelation schemaを再確認 | synthetic all `D514A1F353FB775A2A12327D5DAEE0F68127B730ECB1DA965FFF2941939E1C5F`／existing-data `A994D80BCD82E42922CCFDF4F1EAC57AAFAD4B8C190AE06191E9D5BF1FD6E265`／update `C86318016C3677114AFD399336B3AB215EE16CB930FAF2F990E198B0216A0F36` | accepted。実data elapsedは`MANUAL-02` |

Before／Afterは同じ.NET 10 path、同じmachine、same corpusで取得する。actual real-data elapsedはこの表へ推測で記入しない。

## Engineering completion

```text
Final engineering commit: same closure commit
Artifact SHA-256: final selected publish / receipt hashes are generated by the verified commands and are not committed
Selected profile: WinX64SelfContained (managed bundle + ReadyToRun); updater WinX64SelfContainedSingleFile
Synthetic suite command: pwsh -NoProfile -File .\scripts\benchmark-net10-performance.ps1 -Corpus all -Configuration Release -Scale small,medium,large
Synthetic suite result: 13 passed; TRX SHA-256 D514A1F353FB775A2A12327D5DAEE0F68127B730ECB1DA965FFF2941939E1C5F
Instrumentation validation: contract small preflight 6 passed; disabled formatter 0 calls and correlation schema protected
Full test / analyzer result: 3,547 passed / 16 skipped; Roslynator 0 diagnostics; selected publish, existing-data, update success / rollback passed
Fresh review result: no major findings after reviewer fixes
MANUAL-02 handoff: ready
```
