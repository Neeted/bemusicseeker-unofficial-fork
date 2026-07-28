# PLAN_STATUS

[リファクタリング完了記録](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

最終計画レビュー日: 2026-07-29

## Current checkpoint

- reviewed HEAD: `07426644d6ed723d54832e28581dad039601283a`
- observed worktree: clean
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **complete**
- strict Refactoring Completion Gate: **met**
- .NET 10 implementation through `NET10-08`: **complete**
- .NET 10 Engineering Gate: **not yet met; finite final batch remains**
- post-engineering clean-machine acceptance: **user-owned; not a Codex blocker**

現行sourceは全5 projectを.NET 10へ移行し、managed／native dependency、SQLite、folder Self-contained app、single-file updater、existing-data、old-to-new update／rollbackの自動routeを持つ。repository記録上の直近Full verificationは`3480 passed / 16 skipped / 0 failed`、Roslynator `0 diagnostics`である。

## Active outcome

- active outcome: `NET10-09 Final engineering closure`
- active execution package: `.NET 10 servicing／distribution／automated gate`
- execution anchor: `F1 SDK servicing baseline`
- planner state: `active batch materialized; do not invoke planner`

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `F1 SDK-SERVICING` | active | SDK／runtime servicing baseline | `global.json`を10.0.302／latestPatchへ更新し、全5 projectと現行SCD artifactをlocked再検証 |
| `F2 LAYOUT-DECISION` | pending | official single-file bounded evaluation | `ADOPTED`またはfolder SCD `NOT_ADOPTED`を自動evidenceで確定。custom loader／probing／relocationなし |
| `F3 ENGINEERING-GATE` | pending | selected distributionの最終自動Gate | full tests、analyzer、publish、layout、startup、existing-data、update／rollback、fresh review |
| `HANDOFF` | pending | Engineering completion／manual handoff | statusをcompleteへ更新し、manual clean-machine／BASS entitlementを別checklistへ渡す |

active／pending unitがある間はunit-plannerを起動しない。各unitの状態遷移は対応code commitへ含める。

## Current evidence and constraints

- `global.json`は現在SDK `10.0.301`、`rollForward: latestFeature`。公式latest servicing baselineは計画更新時点で`10.0.302`。
- main app profileはwin-x64 folder Self-contained、untrimmed、non-single-file。updaterはwin-x64 Self-contained single-file。
- `scripts/portable-package-layout.ps1`と`ManagedDependencyOutputPolicyTests`はmanaged DLLを標準host layoutとしてexe隣接に置くことを明示している。
- `ApplicationPathSnapshot`、audio encoder／writer、BASS runtime周辺に`Assembly.Location`依存が残り、single-file候補ではboundedなpath修正が必要である。
- managed DLLを`libs`へ移す独自loader／probing／deps rewrite／post-publish relocationは採用しない。
- automated existing-data acceptanceとupdate success／rollback acceptanceは実装済みである。
- `.NET Desktop Runtime`未導入machine／VMは`POST_MIGRATION_MANUAL_ACCEPTANCE.md`の`MANUAL-01`でEngineering完了後にユーザーが実施する。
- BASS.NET source／licensee／registration／redistribution evidenceは`RELEASE-01`で公開前に確認する。

## Outcome exit

`F1`〜`HANDOFF`を完了したら次へ更新する。

- engineering migration: `complete`
- active outcome: `none`
- active implementation batch: `empty`
- selected main-app layout: `single-file ADOPTED`または`folder SCD NOT_ADOPTED`
- post-engineering manual acceptance: `pending user action; non-blocking`
