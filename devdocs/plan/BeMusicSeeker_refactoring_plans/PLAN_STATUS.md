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
- execution anchor: `P2 PROFILE-SELECTION`
- planner state: `active batch materialized; do not invoke planner`

## Active implementation batch

| Unit | State | Closure family | Exit |
|---|---|---|---|
| `P1 BENCHMARK-HARNESS` | completed | official candidate publish＋practical startup／memory comparison | 一candidateの最小smoke後、six candidatesをwarm-up 1回＋warm／fresh各3回で比較し、必要な上位候補だけ少数追加測定 |
| `P2 PROFILE-SELECTION` | active | practical performance and layout decision | 実用差を優先し、同等時はnative extraction、file数、ReadyToRunの一般特性で選びcurrent-only evidenceへ記録 |
| `P3 PACKAGE-CLOSURE` | pending | selected profileのpublish／layout／update contract | profile、scripts、validators、tests、spec、台帳を一貫更新 |
| `P4 FINAL-GATE` | pending | final automated Engineering Gate | full tests、analyzer、publish、existing-data、update／rollback、selected profile startup smoke、fresh review |
| `HANDOFF` | pending | Engineering completion／manual handoff | statusをcompleteへ戻し、manual clean-machine／release prerequisiteへhandoff |

active／pending unitがある間はunit-plannerを起動しない。各状態遷移を対応code／config commitへ含める。

## Current evidence and constraints

- HEADは全5 projectを.NET 10へ移行済み。SDKは`10.0.302`／`latestPatch`。
- P1 frozen worktreeのFull verificationは`3489 passed / 16 skipped / 0 failed`、Roslynator `0 diagnostics`。Self-contained publish、existing-data、update success／rollbackも通過。
- main appの現行profileはwin-x64 Self-contained single-file、`IncludeNativeLibrariesForSelfExtract=true`、trimming／ReadyToRun無効。
- practical comparisonは42起動、failure 0。`folder-r2r`と`bundle-r2r`は通常起動中央値`3.30 s`／`3.28 s`、fresh中央値`4.65 s`／`4.67 s`で実用上同等。`bundle-r2r`は24 files、folder候補は505 filesであり、追加測定なしで`bundle-r2r`を選択候補とした。旧`folder-il`固定fallbackは採用しない。
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
