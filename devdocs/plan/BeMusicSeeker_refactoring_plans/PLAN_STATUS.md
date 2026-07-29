# PLAN_STATUS

[リファクタリング完了記録](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

最終計画レビュー日: 2026-07-29

## Current checkpoint

- reviewed scope: `cfedc5fd..760d47da` plus frozen `P4/HANDOFF` worktree
- resulting tree: final Engineering closure
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／owner整理: **complete**
- strict Refactoring Completion Gate: **met**
- .NET 10 code／dependency／data／updater migration: **complete**
- engineering migration: **complete**
- performance-first distribution closure: **met**
- performance acceptance: **met**
- selected main-app layout: **managed bundle＋ReadyToRun**
- post-engineering clean-machine acceptance: **user-owned; not a Codex blocker**

main appは外部startup／working set比較により、native self-extractなしのmanaged bundle＋ReadyToRunへ確定した。CodexのEngineering Gateは完了しており、残るclean-machine／release prerequisiteは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoff済み。

## Active outcome

- active outcome: `none`
- active execution package: `none`
- execution anchor: `none`
- planner state: `not required`

## Active implementation batch

empty

## Current evidence and constraints

- HEADは全5 projectを.NET 10へ移行済み。SDKは`10.0.302`／`latestPatch`。
- main appのselected profileは`Properties/PublishProfiles/WinX64SelfContained.pubxml`。win-x64 Self-contained managed bundle、native self-extract無効、ReadyToRun有効、trimming／compression／Composite R2R無効。
- practical comparisonは42起動、failure 0。`folder-r2r`と`bundle-r2r`は通常起動中央値`3.30 s`／`3.28 s`、fresh中央値`4.65 s`／`4.67 s`で実用上同等。`bundle-r2r`は24 files、folder候補は505 filesであり、追加測定なしで`bundle-r2r`を選択候補とした。旧`folder-il`固定fallbackは採用しない。
- current-only selection evidenceは`devdocs/acceptance/net10-distribution-performance.md`。benchmark harnessの最小smokeを再実行し、selected profileはFull publish／startup acceptanceで整合を確認済み。
- final Full verificationは`3493 passed / 16 skipped / 0 failed`、Roslynator `0 diagnostics`。全5 projectのlocked restore／Release build、selected app／updater publish、existing-data、old-to-new update success／fault rollbackを通過。
- `dotnet test`の最終command responseは164秒。deployment validatorの禁止path membershipとdirectory／descendant behaviorを単一process内で検証し、180秒閾値を維持した。
- managed assembliesはbundleから読み込む。SDK／SQLite／WPF native runtimeの6 DLLはexe隣接とし、temporary extractionを使用しない。application-owned BASS／7z／Everythingはowner directoryを維持する。
- `startup_ready_operable elapsedMs`のStopwatchはViewModel初期化途中で開始されるため、process launch、apphost、native extractionを含むend-to-end指標ではない。
- BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`に既に整理されている。
- standard host fileを`libs`へ移すcustom loader、probing、deps rewrite、post-publish relocation、wrapperは作らない。
- updaterはsingle-fileのまま維持する。
- `.NET Desktop Runtime`未導入machine／VMは`MANUAL-01`、BASS entitlementは`RELEASE-01`として非blocking handoffする。
