# .NET 10 Performance Improvement Register

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

current-only inventory。net472／.NET 10の一回ログはpriorityを決めたhistorical symptom evidenceであり、今後のGate baselineではない。

| ID | State | Evidence class | Scope | Current finding | Owner / exit |
|---|---|---|---|---|---|
| `PRF-001` | closed | `STRUCTURAL_PERF_DEFECT` + `SYNTHETIC_MEASURABLE` | full library list | UI laneのsummary用source-reference materializationを0にし、immutable source＋read-only index snapshotをworkerが直接走査する | `P2`: output一致、1,000／25,000／200,000 rowsでallocation減少 |
| `PRF-002` | protected | `OBSERVABILITY_REQUIRED` + partial synthetic | playlist summary | current interaction／generation markerとsupersede／hidden cancellation、gate drainのdeterministic testsで保護済み。追加のduplicate apply defect evidenceなし | actual renderは`MANUAL-02` |
| `PRF-003` | protected | `OBSERVABILITY_REQUIRED` + partial synthetic | playlist detail transition | selection、background terminal apply、first-visibleをcurrent request versionで相関し、stale terminal、queue dedupe、latest intent、selection reentryをdeterministic testsで保護済み。dispatcher queueが実在しないbackground stageをUI queueとは記録しない | actual renderは`MANUAL-02` |
| `PRF-004` | closed | `SYNTHETIC_MEASURABLE` | resource-health | immutable snapshotからwarning／ignoredをmutable objectへ戻さずsingle-pass projectionし、large allocationを1,961,633,696→82,279,704 bytesへ削減 | warning kind／message、ignored membership、case-insensitive duplicate first-wins／version behavior一致 |
| `PRF-005` | closed | `SYNTHETIC_MEASURABLE` + `MANUAL_REAL_DATA` | song-table materialization／publication | canonical publication前のrow-reference copyを2→1、large allocationを3,200,112→1,600,056 bytesへ削減。DB query絶対時間は実data依存 | existing temporary DB golden tests＋publication corpus。full startupは`MANUAL-02` |
| `PRF-006` | manual | `OBSERVABILITY_REQUIRED` | post-init forced GC | synthetic componentだけでは削除判断不能。generation、GC request outstanding数、startup schedulerのqueued／running／backlog、直前／直後managed bytes、collection delta、pauseをcurrent markerへ追加 | 削除を推測せず`MANUAL-02` |
| `PRF-007` | protected | synthetic managed + `MANUAL_REAL_DATA` native | scan／construction | managed scanのsorted distinct hash arraysを再sortせずowner copyし、large allocationを13,215,568→2,400,184 bytesへ削減。Everything／diskは実環境依存 | array content／ownership behaviorをtestで保護。nativeはmarker／`MANUAL-02` |
| `PRF-008` | closed | `SYNTHETIC_MEASURABLE` | install destination estimation | audio gateとfinal evaluationでcandidate viewを共有し、8／64／512 candidatesのview buildを16／128／1,024から8／64／512へ削減 | selected path、candidate count、既存estimation behavior一致 |
| `PRF-009` | closed | `SYNTHETIC_MEASURABLE` | BMS／BMSON／chart-info parse | BMSON continuationの後続note探索を反復LINQ走査から単調indexへ変更し、16,000 equal-Y notesのprobe上限を127,992,000から31,999へ削減 | varied-Y golden outputと既存parser behavior suite一致 |
| `PRF-010` | protected | `SYNTHETIC_MEASURABLE` | normal refresh async drain | deadlock解消済み。summary workerはbounded cancellation、obsolete apply防止、shutdown時のguard外drainを行う | synchronous waitへ戻さずdeterministic testsで保護 |
| `PRF-011` | protected | `ALLOWED_COST` | distribution | managed bundle＋ReadyToRunは選定済み | profile evidenceなしでは再開しない |
| `PRF-012` | manual | `MANUAL_REAL_DATA` | final real-data interactions | production-like dataはdevelopment環境にない | `MANUAL-02`: 全engineering完了後にfinal artifactで一度確認 |

## Closure rule

- `STRUCTURAL_PERF_DEFECT`はdeterministic invariantとbehavior testで閉じる。
- `SYNTHETIC_MEASURABLE`はfixed corpusの変更前後evidenceとgolden outputで閉じる。
- `OBSERVABILITY_REQUIRED`はcurrent .NET 10 marker、低負荷性、manual operation coverageで閉じる。
- `MANUAL_REAL_DATA`はCodexのactive blockerにせず、`POST_MIGRATION_MANUAL_ACCEPTANCE.md`へhandoffする。
- net472を再計測しないことを理由にactive itemを残さない。
