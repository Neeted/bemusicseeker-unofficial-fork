# .NET 10 Engineering Blockers

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## Current decision

現行checkpointに`EXTERNAL_BLOCKER`はない。MVVM整理と.NET 10機能移行は完了しているが、main-app distribution profileは性能evidence不足のため`NET10-10`で再確定する。

## Active engineering work

| ID | State | Work | Owner / exit |
|---|---|---|---|
| `PERF-01` | active | folder／managed bundle／native self-extractとReadyToRun有無を、fresh install／warm cacheの外部startup、working set、機能受入れで比較 | `NET10-10 P1/P2` |
| `LAYOUT-02` | pending | 性能winnerをpublish、validator、update contract、specへ一貫適用。custom relocationなし | `NET10-10 P3` |
| `GATE-02` | pending | selected profileでfull verification、publish、existing-data、update／rollback、performance rerun、fresh review | `NET10-10 P4` |

`PERF-01`はsingle-fileを失敗扱いするための作業ではない。current profileを含む全candidateを同条件で測り、決定規則で一つを選ぶ。

## Post-engineering / release follow-up

| ID | Classification | Owner | Engineeringへの影響 |
|---|---|---|---|
| `MANUAL-01` | post-engineering manual acceptance | user | なし。runtime未導入clean x64 Windows／VMで実施 |
| `RELEASE-01` | release prerequisite | user／release owner | なし。BASS.NET provenance／redistribution証跡を公開前に確認 |
| `RELEASE-02` | release operation | user／release owner | なし。署名、tag、push、public publishは明示指示後 |

configuration、DB、managed dependency、SQLite、archive／audio、native interop、updater transaction、existing-data、old-to-new update／rollbackの旧blockerは解消済みであり、この台帳へ履歴を再掲しない。
