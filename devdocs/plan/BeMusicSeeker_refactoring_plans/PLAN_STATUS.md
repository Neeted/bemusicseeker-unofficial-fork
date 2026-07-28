# PLAN_STATUS

[リファクタリング完了記録](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

最終計画レビュー日: 2026-07-29

## Current checkpoint

- reviewed HEAD: `01679562`
- observed worktree: clean repository snapshot
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **complete**
- strict Refactoring Completion Gate: **met**
- .NET 10 code／dependency／data／updater migration: **complete**
- previous functional Engineering Gate: **met at `01679562`**
- performance-first distribution closure: **not yet met**
- post-engineering clean-machine acceptance: **user-owned; not a Codex blocker**

現行single-file appは機能acceptanceを通過している。ただし採用判断はfile数と機能互換性を中心とし、process launchからのstartup／working set比較を持たないため、最終配布profileだけを`NET10-10`で再開する。MVVMや.NET 10 dependency corridorを再調査しない。

## Active outcome

- active outcome: `NET10-10 Performance-first distribution closure`
- active execution package: `main-app profile measurement／selection／package closure`
- execution anchor: `P1 BENCHMARK-HARNESS`
- planner state: `active batch materialized; do not invoke planner`

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `P1 BENCHMARK-HARNESS` | active | official candidate publish＋external startup／memory harness | six candidatesをfresh install／warm cacheで反復測定し、machine-readable reportを生成 |
| `P2 PROFILE-SELECTION` | pending | deterministic performance decision | decision ruleでwinnerを一つ選び、current-only acceptance evidenceへ記録 |
| `P3 PACKAGE-CLOSURE` | pending | selected profileのpublish／layout／update contract | profile、scripts、validators、tests、spec、台帳を一貫更新 |
| `P4 FINAL-GATE` | pending | final automated Engineering Gate | full tests、analyzer、publish、existing-data、update／rollback、performance rerun、fresh review |
| `HANDOFF` | pending | Engineering completion／manual handoff | statusをcompleteへ戻し、manual clean-machine／release prerequisiteへhandoff |

active／pending unitがある間はunit-plannerを起動しない。各状態遷移を対応code／config commitへ含める。

## Current evidence and constraints

- HEADは全5 projectを.NET 10へ移行済み。SDKは`10.0.302`／`latestPatch`。
- repository記録上の直近Full verificationは`3480 passed / 16 skipped / 0 failed`、Roslynator `0 diagnostics`。
- main appの現行profileはwin-x64 Self-contained single-file、`IncludeNativeLibrariesForSelfExtract=true`、trimming／ReadyToRun無効。
- managed assembliesはbundleから読み込まれるが、bundled native runtimeはfresh install／cache miss時の起動前にtemporary extractionされる。
- `startup_ready_operable elapsedMs`のStopwatchはViewModel初期化途中で開始されるため、process launch、apphost、native extractionを含むend-to-end指標ではない。
- BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`に既に整理されている。
- standard host fileを`libs`へ移すcustom loader、probing、deps rewrite、post-publish relocation、wrapperは作らない。
- updaterはsingle-fileのまま維持する。
- `.NET Desktop Runtime`未導入machine／VMは`MANUAL-01`、BASS entitlementは`RELEASE-01`として非blocking handoffする。

## Outcome exit

`P1`〜`P4`と`HANDOFF`を完了したら次へ更新する。

- engineering migration: `complete`
- active outcome: `none`
- active implementation batch: `empty`
- selected main-app layout: benchmark winner (`Properties/PublishProfiles/WinX64SelfContained.pubxml`)
- performance acceptance: `met`
- post-engineering manual acceptance: `pending user action; non-blocking`
