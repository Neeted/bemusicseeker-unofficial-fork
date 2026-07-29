# PLAN_STATUS

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [リファクタリング計画](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終計画レビュー日: 2026-07-29

## Current checkpoint

- reviewed production checkpoint: `c12f6e6ce3896e6d6235630401dfdbf199ed2bfc`
- runtime evidence: `.tmp/推定先にインストールでハング_install-performance.log`
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／ownerの構造整理: **substantially complete**
- strict Refactoring Completion Gate: **reopened; not met**
- .NET 10 code／dependency／data／updater migration: **complete**
- selected distribution: **managed bundle＋ReadyToRun; retain**
- concurrency／responsiveness acceptance: **not met**
- engineering migration: **reopened only for final responsiveness closure**
- post-engineering clean-machine acceptance: **user-owned; non-blocking**

single-file extractionやReadyToRunではなく、workerがcatalog writer lockを保持したままUIへ同期通知し、UIが同lockのreaderを要求する循環待機が原因である。

## Recovery decision

- release buildではdeadlockを残さず、estimated-installを正常完了させる。
- pre-release hangの中途状態を救済するdurable journal、operation marker、recovery coordinator、専用reconciliation stateは追加しない。
- filesystem／song DB差分は、既存のstartup file diff（`ScanBmsFilesOnStartup`既定`true`）またはmanual `ReloadFileDiff`／full reinitializeで収束させる。
- startup scanを無効にしたユーザー設定は上書きしない。
- existing same-MD5 moved-file relink behaviorをtest evidenceとして維持する。

## Active outcome

- active outcome: `CONC-01 Responsiveness closure`
- active execution package: `H1-H4 minimal hardening batch`
- execution anchor: `H4 responsiveness gate`
- planner state: `not required; batch materialized`

## Active implementation batch

| Unit | State | Closure |
|---|---|---|
| `H1 NORMAL-REFRESH-DEADLOCK` | completed | version coalescing UI drain、explicit shutdown drain、held-writer／dedicated-UI-lane regression |
| `H2 ESTIMATED-INSTALL-LOCK-SCOPE` | completed | snapshot／atomic apply leaseを分離し、semantic LR2 reservation内ではstate applyだけを行い、dialog、log、event、UI refreshを全guard解放後へpublish |
| `H3 APPLICATION-WIDE-WAIT-AUDIT` | completed | library／package、playlist、shell／externalをactual wait graphで分類し、callback-under-lock、thread-affinity、unbounded external waitを3 familyで修正 |
| `H4 RESPONSIVENESS-GATE` | active | full interaction smoke、selected publish、fresh review、Gate closure |

active／pending unitがある間はunit-plannerを起動しない。各unitのstatus更新は対応code／test commitへ含める。

## Confirmed evidence

1. UI commandは`PendingPackageWorkflowOwner.ExecuteInstallAsync`からmutationをbackground executionへ渡す。
2. `PendingEstimatedInstallOwner`はsnapshot readerとatomic apply writerをboundedに取得し、file moveからmaintenanceまでのsemantic operationはestimate／LR2 reservationで直列化する。
3. 10件のfile move後、catalog、LR2、resource health、installed lookupまで完了する。
4. normal-library refresh subscriberがUI terminal applyを同期waitする。
5. UI applyはsource snapshot取得時に`rwlockBMSFiles` readerへ入る。
6. worker writer→UI completion待ち、UI→reader待ちのcycleが成立する。
7. `Settings.ScanBmsFilesOnStartup`の既定値は`true`で、startup initializationはfile diffを実行する。
8. manual `ReloadFileDiff` routeと、same-MD5 moved-fileを新pathへcommitする既存behavior testがある。
9. playlist hydration／selection／store receiptとpackage／LR2 progress通知は、owner guard解放後にpublishする。
10. playback stopとupdater decision callbackはprivate guardを保持せず外部処理を呼ぶ。
11. updater recoveryと外部playerのprocess／window待機は有限で、成立しなければvisible failureとなる。

## Scope guardrails

- timeout、retry、`TryEnter`、notification skip、追加`Task.Run`をfinal fixにしない。
- incident専用のjournal、marker、recovery DB、persistent operation state machineを作らない。
- すべてのsync wait、raw lock binding、progress／cancelを機械的に置換しない。
- selected .NET 10 profile、dependency graph、updater contractはactive outcomeで変更しない。
- forced process terminationとその自動回復はH4 Gateへ含めない。

## Exit

```text
strict Refactoring Completion Gate: met
concurrency / responsiveness acceptance: met
engineering migration: complete
active outcome: none
active implementation batch: empty
selected distribution: managed bundle + ReadyToRun
post-engineering manual acceptance: pending user action; non-blocking
```
