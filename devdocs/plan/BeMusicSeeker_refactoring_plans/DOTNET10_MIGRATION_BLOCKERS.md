# Engineering Blockers

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [性能register](./PERFORMANCE_REGRESSION_REGISTER.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## Current decision

.NET 10 retarget、dependency、data、updater、distribution profile、MVVM／concurrency safetyは機能面で完了している。current .NET 10のknown performance workを`PERF-01`として閉じる。

production-like dataがdevelopment環境にないこと、net472を再計測しないことはblockerではない。synthetic corpusを作れないrouteはinstrumentationと`MANUAL-02` handoffでEngineering Gateを通過できる。

## Active engineering corridor

| ID | State | Work | Exit |
|---|---|---|---|
| `PERF-01` | active | current .NET 10 performance engineering | known structural work、synthetic component、instrumentationを閉じる |
| `PERF-02` | active | list transition critical path | UI full-copy 0、queue／generation bounded、manual marker ready |
| `PERF-03` | active | startup／index／GC component | feasible synthetic component改善、unsupported stageはmanual classification |
| `PERF-04` | active | estimation／managed scan／parser | fixed-seed corpus、golden behavior、allocation／elapsed改善 |
| `GATE-04` | pending | engineering performance closure | P5、fresh review、`engineering migration: complete` |

## Completed corridors

TFM／SDK、managed dependencies、SQLite、archive／audio、native interop、existing-data、updater success／rollback、managed bundle＋ReadyToRun selection、estimated-install deadlock closureはcompleted。PERF-01に必要な範囲以外で再計画しない。

## Post-engineering / release follow-up

| ID | Classification | Owner | Engineeringへの影響 |
|---|---|---|---|
| `MANUAL-01` | post-engineering manual acceptance | user | なし。runtime未導入clean x64 Windows／VMで実施 |
| `MANUAL-02` | post-engineering real-data performance acceptance | user | なし。final .NET 10 artifactで一度実施。net472比較なし |
| `RELEASE-01` | release prerequisite | user／release owner | なし。BASS.NET provenance／redistribution証跡を公開前に確認 |
| `RELEASE-02` | release operation | user／release owner | なし。署名、tag、push、public publishは明示指示後 |
