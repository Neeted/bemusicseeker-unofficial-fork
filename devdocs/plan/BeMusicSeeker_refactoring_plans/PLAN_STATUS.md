# PLAN_STATUS

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [リファクタリング計画](./BeMusicSeekerリファクタリング計画.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

最終計画レビュー日: 2026-07-30

## Current checkpoint

- reviewed production scope: `5d51990e`＋H4 frozen worktree
- H4 verification evidence: Full verification、selected publish、existing-data、update success／rollback、3,523 tests passed／16 skipped、format、Roslynator 0 diagnostics
- H4 repository publish UI smoke: startup operable、Stella一覧表示、normal shutdown
- H4 fresh outcome review: 重大指摘なし
- runtime evidence: `.tmp/推定先にインストールでハング_install-performance.log`
- Release Freeze: active
- `git push`／tag／署名／public release: ユーザーの明示指示まで禁止

## Completion decision

- MVVM／ownerの構造整理: **complete**
- strict Refactoring Completion Gate: **met**
- .NET 10 code／dependency／data／updater migration: **complete**
- selected distribution: **managed bundle＋ReadyToRun; retain**
- concurrency／responsiveness acceptance: **met**
- engineering migration: **complete**
- post-engineering clean-machine acceptance: **user-owned; non-blocking**

single-file extractionやReadyToRunではなく、workerがcatalog writer lockを保持したままUIへ同期通知し、UIが同lockのreaderを要求する循環待機が原因である。

## Recovery decision

- release buildではdeadlockを残さず、estimated-installを正常完了させる。
- pre-release hangの中途状態を救済するdurable journal、operation marker、recovery coordinator、専用reconciliation stateは追加しない。
- filesystem／song DB差分は、既存のstartup file diff（`ScanBmsFilesOnStartup`既定`true`）またはmanual `ReloadFileDiff`／full reinitializeで収束させる。
- startup scanを無効にしたユーザー設定は上書きしない。
- existing same-MD5 moved-file relink behaviorをtest evidenceとして維持する。

## Active outcome

- active outcome: none
- active execution package: none
- execution anchor: none
- planner state: not required

## Active implementation batch

empty

## Confirmed evidence

1. UI commandは`PendingPackageWorkflowOwner.ExecuteInstallAsync`からmutationをbackground executionへ渡す。
2. estimated-install aggregate transitionは`BMSLibrary`が所有し、snapshot readerとatomic apply writerをboundedに取得する。pending／installed collection mutationは`PackageLifecycleOwner`が一括適用する。
3. 10件のfile move後、catalog、LR2、resource health、installed lookupまで完了する。
4. normal-library refreshはlatest versionをsingle pending UI drainへqueueし、producerはUI terminal applyを待たない。
5. LR2 progress propertyはproperty-name集合をsingle pending operationへcoalesceし、1 UI turnで1 snapshotだけpublishする。
6. manual downloadとstartup／deferred ranking refreshは、network、cache列挙、XML parse、offline lookup準備をmodel guard外で実行する。score-source generation一致時だけparse済みcache promotion／score applyをbounded guard内でcommitし、score通知とdialogはguard外のUI laneで行う。parse失敗したstaging fileは既存cacheを置換しない。
7. optionalな外部テーブル一覧HTTPはcore startup continuationと`rwlockBMSFilesInitializedAll`から分離し、`startup_ready_operable`後のcancel可能なbackground requestとして実行する。catalogとloading stateはgeneration一致時だけUI laneへpublishし、shutdownはactive requestをcancelする。
8. `Settings.ScanBmsFilesOnStartup`の既定値は`true`で、startup initializationはfile diffを実行する。
9. manual `ReloadFileDiff` routeと、same-MD5 moved-fileを新pathへcommitする既存behavior testがある。
10. playlist hydration／selection／store receiptとpackage／LR2 progress通知は、owner guard解放後にpublishする。
11. playback stopとupdater decision callbackはprivate guardを保持せず外部処理を呼ぶ。
12. updater recoveryと外部playerのprocess／window待機は有限で、成立しなければvisible failureとなる。

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
