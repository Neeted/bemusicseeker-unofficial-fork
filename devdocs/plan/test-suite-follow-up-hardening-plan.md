# テスト整理フォローアップ hardening 計画

Status: Active

Plan date: 2026-08-26

Review evidence snapshot: `69e25e886e9e4b5978bd702972c6580e324438d3`

Implementation base: `aa5a23f81984be21528d726d133c0cf17f885d48`

## Goal

完了済みのテスト整理計画を再オープンせず、後続の repository-wide static review で見つかった必須 hardening を別単位として閉じる。

1. playlist dialog と application composition の test-local STA dispatcher owner を、assembly 共通の `TestUiDispatcherHost` へ移す。
2. worker 起点の UI 更新を worker task と対象 state transition の両方で待ち、watchdog 後の無制限同期 block をなくす。
3. portable package validator の process exit、redirected stream drain、owned process-tree cleanup、staging cleanup を bounded lifecycle owner に閉じる。

## Context

- 開始時の `git status --short` は空で、worktree は clean だった。
- review evidence snapshot から implementation base まで対象 production / test code に変更はない。
- `BmsPlaylistCustomFolderOutputTests` の local `RunOnStaDispatcherThread(Func<Task>)` は watchdog のない `Dispatcher.PushFrame` と無制限 `Thread.Join()` を所有する。
- `ApplicationCompositionTests` の local STA helper は無制限 `Thread.Join()` を所有し、worker selection test は local frame の timeout 後も incomplete worker を同期 block し得る。
- `UpdaterDeploymentBoundaryTests` の positive / negative validator process は stdout / stderr を同期的に逐次 drain した後、無制限 `WaitForExit()` を行う。staging setup は cleanup owner の外にあり、cleanup failure が primary failure を置換し得る。
- `TestUiDispatcherHost.AwaitTaskOnDispatcher` は task completion と5秒 failure watchdogを所有する canonical helper である。
- 既存 release / process test の近傍契約は process 60秒、stream drain 5秒、cleanup 5秒である。今回の validator invocation も同じ bounds を使う。

## Constraints and out of scope

- production behavior、Functional shard membership、worker topology、ClassLevel scope、180秒 budget、physical-cursor-free 方針を変えない。
- fixed wait の延長、新しい `DoNotParallelize`、worker / shard 低下、Functional 反復 gate、WPF repeat gateを追加しない。
- process 名による global kill、test 都合の public production API、service locator、broad callback host、将来用 abstractionを追加しない。
- timeout / nonzero exit / setup / orchestration failureをstream drain、process cleanup、staging cleanup failureで置換しない。primary failureがないcleanup-only failureは失敗として表面化させる。
- Unit 1A、1B、2は同一 worktreeで逐次実装する。必要なcommitはunit境界で行い、pushは行わない。
- 推奨 Unit 3〜5 は必須unitのacceptance後に別作業単位として扱い、本計画の実装scopeには含めない。

## Decision list

- Unit 1A / 1B は既存 canonical fixture と observable assertions を維持した `replace` とし、raw dispatcher / STA ownerだけを退役させる。
- Unit 1A の class-wide `DoNotParallelize` は process-global `Settings.Default` と復元ownerのため維持する。Functionalでは既存 `serial-state-a` selectorを維持する。
- Unit 1B は既存 `serial-state-a` / 1-worker ownerを維持し、新しいparallelization attributeを追加しない。
- Unit 2 は既存 `ReleaseAcceptance` testを `extend` し、対象file内の二つのvalidator invocationだけを所有するprivate narrow helperを使う。既存の別fixture private helperを横断共通化しない。
- Unit 2 の publish rootsはread-only、stagingはGUID-owned childとし、class-wide DNPは追加しない。
- Unit 2 のsetup、validator process、stream drain、owned tree cleanup、staging cleanupを同じ failure ledgerで扱い、primary/secondary precedenceを明示する。

## Unit 0: freeze と test delta

Owner: root

Writable path:

- `devdocs/plan/test-suite-follow-up-hardening-plan.md`

Observable outcome:

- clean implementation base、unitごとのowner、failure contract、verification、replan triggerを実装前に固定する。

Focused verification:

```powershell
git diff --check
```

## Unit 1A: playlist dialog dispatcher lifecycle

Owner: `implementation-worker`（single sequential owner）

Writable paths:

- `BeMusicSeeker.Tests/BmsPlaylistCustomFolderOutputTests.cs`
- 本計画書の Unit 1A implementation evidence

Read-only references:

- `BeMusicSeeker.Tests/TestUiScheduler.cs`
- `devdocs/spec/testing-strategy.md`
- `devdocs/spec/test-authoring-contract.md`

Observable outcome:

- `PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot` は共有 dispatcher 上で core task を開始し、同dispatcher上から識別可能なoperation name付き `AwaitTaskOnDispatcher` で待つ。
- local `RunOnStaDispatcherThread(Func<Task>)`、raw `Dispatcher.PushFrame`、無制限 `Thread.Join()`、専用importを削除する。
- dialog、`Settings.Default`、`DispatcherHelper.UIDispatcher`、temporary directoryの既存restore / cleanup順序とobservable assertionsを維持する。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal / watchdog | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| open時custom-folder settings snapshotでproperty dialog save後処理を行う | `PlaylistWorkspaceViewModel` / `PlaylistPropertyDialogViewModel` | `BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot` | replace | process-global `Settings.Default`、`DispatcherHelper.UIDispatcher`、shared WPF host / `serial-state-a`; class DNP維持 | core async `Task` / canonical 5秒 watchdog | local STA thread、frame、unbounded join |

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot'
```

Replan triggers:

- shared dispatcherでは既存 settings / dialog cleanupやobservable assertionを維持できない。
- `AwaitTaskOnDispatcher` をdispatcher外から呼ぶ必要が生じる。
- 既存class DNPまたはFunctional selectorの変更が必要になる。

### Implementation evidence

- Replaced the canonical playlist dialog snapshot test's local STA thread, raw dispatcher frame, and unbounded thread join with `TestUiDispatcherHost.Invoke` and `AwaitTaskOnDispatcher`. The existing core task remains the completion signal, with the shared helper's five-second watchdog and an identifiable operation name.
- Removed the test-only STA helper and its dedicated exception-dispatch import; retained `System.Threading` for the fixture's existing `Interlocked` assertions. The core observable assertions, dialog disposal, four `Settings.Default` restores, `DispatcherHelper.UIDispatcher` restore, temporary-directory cleanup, class-wide `DoNotParallelize`, and `serial-state-a` ownership remain unchanged.
- Focused Quick passed on the retry after retaining the fixture's shared `System.Threading` import used by existing `Interlocked` assertions: 1/1 test passed, runner elapsed 35.2s (test elapsed 4.8529s), artifact `artifacts/verification/tests-quick-20260826-000747/functional/results.trx`. The initial attempt stopped at compile with those pre-existing `Interlocked` references; no test execution occurred.
- Targeted changed-file scans found no local STA helper, raw `Dispatcher.PushFrame`, unbounded `Join`, or physical cursor primitive; `git diff --check` passed.

## Unit 1B: application composition dispatcher and worker lifecycle

Owner: `implementation-worker`（Unit 1A完了後のsingle sequential owner）

Writable paths:

- `BeMusicSeeker.Tests/ApplicationCompositionTests.cs`
- 本計画書の Unit 1B implementation evidence

Read-only references:

- `BeMusicSeeker.Tests/TestUiScheduler.cs`
- `BeMusicSeeker.Tests/MainWindowPlayHistoryWpfTests.cs`
- `devdocs/spec/testing-strategy.md`

Observable outcome:

- 対象2 testを `TestUiDispatcherHost.Invoke` へ移し、local STA helperを削除する。
- worker selection testは `RunContinuationsAsynchronously` 付き `TaskCompletionSource` で対象 `PropertyChanged` を表し、worker taskとproperty observation taskを識別可能なoperation name付きで待つ。
- event handlerは `finally` で解除し、worker threadとUI threadの相違、UI thread上の通知、requested state、event count 1回の既存assertionを維持する。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal / watchdog | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| shell arbiter経由のplaylist summary refresh | `ApplicationComposition` / `PlaylistWorkspaceViewModel` | `ApplicationCompositionTests.MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter` | replace | shared WPF host / `serial-state-a` 1-worker | synchronous observable state | local STA wrapper |
| worker起点selectionをUI dispatcherへ適用 | `PlaylistWorkspaceViewModel.RequestSummarySelection` | `ApplicationCompositionTests.PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher` | replace | shared WPF host / `serial-state-a` 1-worker | worker `Task` + `PropertyChanged` TCS / canonical 5秒 watchdog | local frame、timer、STA helper、unbounded join |

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.ApplicationCompositionTests.MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter|FullyQualifiedName=BeMusicSeeker.Tests.ApplicationCompositionTests.PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher'
```

Replan triggers:

- worker faultをproperty notification timeoutより先に観測できない。
- event解除またはexact-once assertionを維持できない。
- shared WPF host、Functional selector、DNP policyの変更が必要になる。

### Implementation evidence

- Pending.

## Unit 2: portable validator process lifecycle

Owner: `implementation-worker`（Unit 1B完了後のsingle sequential owner）

Writable paths:

- `BeMusicSeeker.Tests/UpdaterDeploymentBoundaryTests.cs`
- 本計画書の Unit 2 implementation evidence

Read-only references:

- `BeMusicSeeker.Tests/DistributionArtifactContractTests.cs`
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs`
- `devdocs/spec/testing-strategy.md`

Observable outcome:

- positive / negative validator processはstart直後にstdout / stderrの `ReadToEndAsync()` を同時開始し、process exit、stream drain、owned process-tree cleanupをそれぞれboundedにする。
- timeout時は開始したroot PIDとowned descendantsだけを停止する。completed streamはdiagnosticに残し、未完了streamはstream名、PID、elapsed、cleanup resultを明示する。
- process timeout、start / setup failure、nonzero exit、stream drain failureをprimaryとし、process / staging cleanup failureはsecondary evidenceにする。primaryがないcleanup-only failureはfailureとする。
- GUID staging directoryはsetup途中のfailureでもcleanupされ、positive / forbidden-path assertionsを維持する。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal / watchdog | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| self-contained publish outputとforbidden path policyをactual validatorで検証 | `scripts/portable-package-layout.ps1` | `UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput` | extend | readonly publish roots、GUID staging / `ReleaseAcceptance`; DNPなし | process exit + stdout/stderr tasks / process 60秒、stream 5秒、cleanup 5秒 | two sequential sync `ReadToEnd` + unbounded `WaitForExit` routes |

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput'
```

publish rootsが無い環境でinconclusive / unavailableの場合は理由を記録し、最終 `Full` の `ReleaseAcceptance` で実行経路を閉じる。

Replan triggers:

- root processからowned descendantを限定できず、process名killまたは広いmachine state操作が必要になる。
- 60 / 5 / 5秒の既存boundsでvalidator contractを閉じられず、release runner budget変更が必要になる。
- primary exceptionを保ったままprocess-tree cleanupとstaging cleanupの両方をboundedにできない。
- helperを複数の異なるprocess lifecycleへ一般化する必要が生じる。

### Implementation evidence

- Pending.

## Integration, review, and acceptance

Unitごとに exact filtered Quick、変更fileのanti-pattern scan、`git diff --check`、test coverage / safety handoffを行う。Unit 1A → Unit 1B → Unit 2 の順で統合し、最終snapshotでは次を実行する。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
rg -n 'Dispatcher\.PushFrame|\.Join\(\s*\)|WaitForExit\(\s*\)|Standard(Output|Error)\.ReadToEnd\(\)' BeMusicSeeker.Tests -g '*.cs'
rg -n 'GetCursorPos|SetCursorPos|Mouse\.GetPosition' BeMusicSeeker.Tests -g '*.cs'
rg -n '\[Microsoft\.VisualStudio\.TestTools\.UnitTesting\.Ignore|\[Ignore' BeMusicSeeker.Tests -g '*.cs'
git diff --check
```

- Functionalは最終snapshotで原則1回。180秒timeoutの場合だけ同一command / filter / budget / snapshot / environmentで一度だけretryする。
- FullはUnit 2のReleaseAcceptance変更を閉じる必須laneとする。
- scanは既存例外を分類し、新規または無理由のunbounded waitがないことを確認する。physical cursor primitiveはゼロ件を維持する。
- 実装と標準検証後、implementation agentをすべて完了させてworktreeを凍結し、fresh `repo-static-review` にintent、acceptance、base/head、worktree diff、検証結果を渡す。
- blocking finding修正後は影響範囲のQuickと必要なintegration laneを再実行し、fresh reviewerへfix deltaとprevious findingを渡す。

## Required handoff

完了時は unitごとのobservable outcome、modified paths、retired helper / route とreplacement、coverage ledger、shared resource / lane / DNP、completion signal / watchdog、process ownership / cleanup / stream precedence、exact commands / artifacts / elapsed、not-run items、deferred recommended / optional unitを記録する。
