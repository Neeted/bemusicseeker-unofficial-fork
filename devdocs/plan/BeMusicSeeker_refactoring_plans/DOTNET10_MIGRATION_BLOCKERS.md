# .NET 10 Engineering Blockers

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## Current decision

現行checkpointに、Codexのコード／自動検証を停止させる`EXTERNAL_BLOCKER`はない。

`.NET Desktop Runtime`未導入machine／VMの確認は`MANUAL-01`、BASS.NETの公開配布権限確認は`RELEASE-01`として手動checklistへ分離した。どちらもactive outcome、planner停止条件、Engineering Gateではない。

## Remaining engineering work

| ID | State | Work | Owner / exit |
|---|---|---|---|
| `SDK-01` | resolved | SDK `10.0.302`／`latestPatch`でSelf-contained artifactを再生成し、全5 projectのlocked restore／Release／acceptanceを再検証済み | `NET10-09 F1` |
| `LAYOUT-01` | resolved | official single-file profile、bounded path修正、隣接native owner、package／startup／existing-data／update success／rollbackを検証し`ADOPTED` | `NET10-09 F2` |
| `GATE-01` | resolved | selected layoutでlocked restore、full tests、publish、existing-data、update／rollback、fresh reviewを完了 | `NET10-09 F3` |

これらのengineering unitはすべて解消済みであり、現在のblockerではない。以後は手動受入れ／release prerequisiteだけを追跡する。

## Post-engineering / release follow-up

| ID | Classification | Owner | Engineeringへの影響 |
|---|---|---|---|
| `MANUAL-01` | post-engineering manual acceptance | user | なし。Codex完了後にruntime未導入clean x64 Windows／VMで実施 |
| `RELEASE-01` | release prerequisite | user／release owner | なし。BASS.NET source／licensee／registration／redistribution証跡を公開前に確認 |
| `RELEASE-02` | release operation | user／release owner | なし。署名、tag、push、public publishは明示指示後 |

## Resolved corridor summary

configuration、dispatcher、path／process、native interop、managed dependency、SQLite、archive／audio、Self-contained publish、updater transaction、existing-data、old-to-new update／rollbackのmigration blockerは解消済みである。詳細はGit historyとbehavior／acceptance testsを正本とし、この台帳へ履歴を追記しない。
