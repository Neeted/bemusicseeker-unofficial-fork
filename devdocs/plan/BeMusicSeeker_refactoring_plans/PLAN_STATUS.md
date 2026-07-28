# PLAN_STATUS

[リファクタリング完了記録](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

最終計画レビュー日: 2026-07-29

## Current checkpoint

- reviewed HEAD: `891fe226`
- observed worktree: F2 implementation diff under review
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **complete**
- strict Refactoring Completion Gate: **met**
- .NET 10 implementation through `NET10-08`: **complete**
- .NET 10 Engineering Gate: **not yet met; F3 final verification remains**
- post-engineering clean-machine acceptance: **user-owned; not a Codex blocker**

現行sourceは全5 projectを.NET 10へ移行し、managed／native dependency、SQLite、single-file app、single-file updater、existing-data、old-to-new update／rollbackの自動routeを持つ。repository記録上の直近Full verificationは`3480 passed / 16 skipped / 0 failed`、Roslynator `0 diagnostics`である。

## Active outcome

- active outcome: `NET10-09 Final engineering closure`
- active execution package: `.NET 10 servicing／distribution／automated gate`
- execution anchor: `F3 Automated Engineering Gate`
- planner state: `active batch materialized; do not invoke planner`

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `F1 SDK-SERVICING` | completed | SDK／runtime servicing baseline | `global.json`を10.0.302／latestPatchへ更新し、全5 projectと現行SCD artifactをlocked再検証 |
| `F2 LAYOUT-DECISION` | completed | official single-file bounded evaluation | `ADOPTED`: managed assemblies／runtimeは公式bundle、application content／native owner directoryは隣接配置。custom loader／probing／relocationなし |
| `F3 ENGINEERING-GATE` | pending | selected distributionの最終自動Gate | full tests、analyzer、publish、layout、startup、existing-data、update／rollback、fresh review |
| `HANDOFF` | pending | Engineering completion／manual handoff | statusをcompleteへ更新し、manual clean-machine／BASS entitlementを別checklistへ渡す |

active／pending unitがある間はunit-plannerを起動しない。F1／F2の状態遷移は各対応code/config commitへ含め、次はF3の最終Engineering Gateを行う。

## Current evidence and constraints

- `global.json`はSDK `10.0.302`、`rollForward: latestPatch`。F1のlocked restore／Release／publish／acceptanceはこのSDKで完了した。
- main app／updater profileはwin-x64 Self-contained single-file、untrimmed、ReadyToRun無効。application contentは`lang`、`test.mp3`、`BeMusicSeeker.dll.config`、native owner directoryを隣接配置する。
- `scripts/portable-package-layout.ps1`はsingle-file bundleを標準契約とし、managed DLL／deps／runtimeconfigのexe隣接を要求しない。
- `ApplicationPathSnapshot`、audio encoder／writer、BASS runtimeのpath利用は`Environment.ProcessPath`／`AppContext.BaseDirectory`へ統一し、`Assembly.Location`へfallbackしない。
- managed DLLを`libs`へ移す独自loader／probing／deps rewrite／post-publish relocationは採用しない。
- candidate single-file publish、startup、existing-data、package layout、old-to-new update success／fault rollback acceptanceはF2で完了した。
- `.NET Desktop Runtime`未導入machine／VMは`POST_MIGRATION_MANUAL_ACCEPTANCE.md`の`MANUAL-01`でEngineering完了後にユーザーが実施する。
- BASS.NET source／licensee／registration／redistribution evidenceは`RELEASE-01`で公開前に確認する。

## Outcome exit

`F1`〜`HANDOFF`を完了したら次へ更新する。

- engineering migration: `complete`
- active outcome: `none`
- active implementation batch: `empty`
- selected main-app layout: `single-file ADOPTED`
- post-engineering manual acceptance: `pending user action; non-blocking`
