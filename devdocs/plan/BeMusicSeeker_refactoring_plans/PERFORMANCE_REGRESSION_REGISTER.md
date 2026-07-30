# .NET 10 Performance Improvement Register

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

current-only inventory。net472／.NET 10の一回ログはpriorityを決めたhistorical symptom evidenceであり、今後のGate baselineではない。

| ID | State | Evidence class | Scope | Current finding | Owner / exit |
|---|---|---|---|---|---|
| `PRF-001` | active | `STRUCTURAL_PERF_DEFECT` + `SYNTHETIC_MEASURABLE` | full library list | UI laneでbackground summary用ordered rowsを全件materializeするrouteがある | `P2`: copy 0、output一致、synthetic large corpus改善 |
| `PRF-002` | active | `OBSERVABILITY_REQUIRED` + partial synthetic | playlist summary | current interaction／generation markerを追加済み | `P2`: duplicate／stale applyのdeterministic closure、実時間はMANUAL-02 |
| `PRF-003` | active | `OBSERVABILITY_REQUIRED` + partial synthetic | playlist detail transition | selectionからfirst-visibleまでcurrent request versionで相関済み | `P2`: 不要turnの除去、actual renderはMANUAL-02 |
| `PRF-004` | active | `SYNTHETIC_MEASURABLE` | resource-health | immutable synthetic snapshotを既存ownerへ入力可能。aggregate marker追加済み | `P3`: output／allocation component evidence |
| `PRF-005` | active | `SYNTHETIC_MEASURABLE` | song-table materialization | temporary SQLiteを既存load ownerへ接続可能。aggregate marker追加済み | `P3`: query／managed publication component evidence |
| `PRF-006` | active | `OBSERVABILITY_REQUIRED` | post-init forced GC | pauseとretained-memory benefitの関係が不明 | `P3`: component evidence。full startup判断はMANUAL-02 |
| `PRF-007` | split | synthetic managed + `MANUAL_REAL_DATA` native | scan／construction | managed decode／diff／parseはfixture化可能。Everything／diskは実環境依存 | `P4`: managed component改善、nativeはmarker／manual |
| `PRF-008` | active | `SYNTHETIC_MEASURABLE` | install destination estimation | existing testsに71／100／399 resource生成がありcorpus化可能 | `P4`: fixed-seed scale、index／allocation／parallel crossover |
| `PRF-009` | active | `SYNTHETIC_MEASURABLE` | BMS／BMSON／chart-info parse | generated chartとrepository fixtureがありgolden corpus化可能 | `P4`: allocation hot pathだけ改善 |
| `PRF-010` | protected review | `SYNTHETIC_MEASURABLE` | normal refresh async drain | deadlock解消済み。one-turn work、coalescing、obsolete applyを確認 | `P2`: synchronous waitへ戻さずbounded test |
| `PRF-011` | protected | `ALLOWED_COST` | distribution | managed bundle＋ReadyToRunは選定済み | profile evidenceなしでは再開しない |
| `PRF-012` | manual | `MANUAL_REAL_DATA` | final real-data interactions | production-like dataはdevelopment環境にない | `MANUAL-02`: 全engineering完了後にfinal artifactで一度確認 |

## Closure rule

- `STRUCTURAL_PERF_DEFECT`はdeterministic invariantとbehavior testで閉じる。
- `SYNTHETIC_MEASURABLE`はfixed corpusの変更前後evidenceとgolden outputで閉じる。
- `OBSERVABILITY_REQUIRED`はcurrent .NET 10 marker、低負荷性、manual operation coverageで閉じる。
- `MANUAL_REAL_DATA`はCodexのactive blockerにせず、`POST_MIGRATION_MANUAL_ACCEPTANCE.md`へhandoffする。
- net472を再計測しないことを理由にactive itemを残さない。
