# テスト整理フォローアップ hardening 計画

Status: Complete

Plan date: 2026-08-26

Review evidence snapshot: `69e25e886e9e4b5978bd702972c6580e324438d3`

Implementation base: `aa5a23f81984be21528d726d133c0cf17f885d48`

## Goal

完了済みのテスト整理計画を再オープンせず、後続の repository-wide static review で見つかった必須 hardening を別単位として閉じる。

1. playlist dialog と application composition の test-local STA dispatcher owner を、assembly 共通の `TestUiDispatcherHost` へ移す。
2. worker 起点の UI 更新を worker task と対象 state transition の両方で待ち、watchdog 後の無制限同期 block をなくす。
3. portable package validator の process exit、redirected stream drain、childless root cleanup、staging cleanup を bounded lifecycle owner に閉じる。

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
- Unit 2 のsetup、validator process、stream drain、root-only cleanup、staging cleanupを同じ failure ledgerで扱い、primary/secondary precedenceを明示する。

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

- Replaced both application-composition tests' local STA dispatcher wrapper with `TestUiDispatcherHost.Invoke`, removing the test-owned dispatcher thread and its unbounded join. The playlist summary refresh test keeps its synchronous state assertions on the shared dispatcher.
- Replaced the worker-selection test's raw `DispatcherFrame` / `DispatcherTimer` wait with a `TaskCompletionSource<bool>` using `RunContinuationsAsynchronously` as the `IsPlaylistSummaryMode` `PropertyChanged` terminal signal. The handler records the notification thread and exact count, and is removed in `finally`. The worker task is awaited first, followed by the property-observation task, through `TestUiDispatcherHost.AwaitTaskOnDispatcher` with method-qualified operation names so worker faults surface before notification watchdog failures.
- Retained summary mode, requested mode, worker/UI thread separation, UI-thread notification, and exactly-once notification assertions; retained shared WPF host, `serial-state-a` one-worker ownership, and existing DNP policy. Removed only the now-unused exception-dispatch import.
- Focused Quick passed: both tests passed, runner elapsed 32.8s, test elapsed 2.4576s, artifact `artifacts/verification/tests-quick-20260826-001302/functional/results.trx`.
- Changed-file anti-pattern scans found no local STA helper, raw `Dispatcher.PushFrame`, `DispatcherTimer`, unbounded `Join`, or physical cursor primitive; `git diff --check` passed.

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

- positive / negative validator processはstart直後にstdout / stderrの `ReadToEndAsync()` を同時開始し、process exit、stream drain、childless validator root cleanupをboundedにする。root exit直後に `ExitCode` を取得し、nonzero exitをstream drainより先にprimaryとして確定する。
- stdout / stderrは同一のabsolute 5秒 stream cutoffを共有し、cleanup後の観測はそのdeadlineまでのremainingだけを使う。late stream faultは明示的にobserveし、nonzero exit時のstream timeout / faultはsecondary evidenceにする。exit zero時だけstream drain failureをprimaryにする。
- timeout時はretained process handleとcaptured PID / StartTime identityでvalidator rootだけを停止し、root residualを同一の5秒 cleanup deadline内に確認する。validator commandはdot-sourceとin-process PowerShell/.NET filesystem操作に限定したchildless contractであり、root already exitedは成功とする。
- process timeout、start / setup failure、nonzero exit、exit-zero stream drain failureをprimaryとし、process / staging cleanup failureはsecondary evidenceにする。primaryがないcleanup-only failureはfailureとする。
- GUID staging directoryはsetup途中のfailureでもcleanupされ、positive / forbidden-path assertionsを維持する。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal / watchdog | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| self-contained publish outputとforbidden path policyをactual validatorで検証 | `scripts/portable-package-layout.ps1` | `UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput` | extend | readonly publish roots、GUID staging / `ReleaseAcceptance`; DNPなし | root exit + captured exit code、stdout/stderr tasks、root residual / process 60秒、single absolute stream 5秒、cleanup 5秒 | two sequential sync `ReadToEnd` + unbounded `WaitForExit` + tree/taskkill fallback routes |

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput'
```

publish rootsが無い環境でinconclusive / unavailableの場合は理由を記録し、最終 `Full` の `ReleaseAcceptance` で実行経路を閉じる。

Replan triggers:

- validator commandが外部 child processを起動するよう変更され、childless root contractではowned descendantを限定できなくなる。
- 60 / 5 / 5秒の既存boundsでvalidator contractを閉じられず、release runner budget変更が必要になる。
- primary exceptionを保ったままroot-only cleanupとstaging cleanupの両方をboundedにできない。
- helperを複数の異なるprocess lifecycleへ一般化する必要が生じる。

### Implementation evidence

- Replaced the two synchronous `ReadToEnd` / unbounded `WaitForExit` calls with one file-local async `RunValidatorProcess` owner. It starts both redirected reads immediately after successful start and retaining the root handle, then captures PID / StartTime as optional metadata; it captures `ExitCode` immediately after root exit, applies a 60-second process bound and one absolute 5-second stream cutoff, and keeps the 5-second cleanup bound on a root-only `Kill(entireProcessTree: false)` plus remaining-budget residual confirmation. The helper is intentionally limited to the validator's childless dot-sourced PowerShell/.NET filesystem contract and has no descendant discovery, taskkill, name lookup, or global process operation.
- Nonzero exit is established as primary before stream drain. For exit zero, stream timeout/fault becomes primary; after a primary is established, cleanup, remaining stream observation, handle disposal, and GUID-child staging deletion are secondary diagnostics, while cleanup-only failure still fails. Completed output and incomplete/faulted stream state retain stream name, PID, elapsed, bound, and cleanup result; late stream faults are explicitly observed. Secondary `Exception.Data` attachment is best-effort and the captured primary is always rethrown through `ExceptionDispatchInfo` without `Console.Error` or a replacement wrapper.
- GUID staging creation and copies remain inside the existing cleanup owner, and deletion remains limited to that GUID child. The existing positive validation and complete forbidden-path assertions are unchanged. Coverage remains an `extend` of the canonical `ReleaseAcceptance` fixture with no new fixture, DNP, shared root, lane, runner, or spec change; process/task completion replaces the retired synchronous and tree-kill routes.
- Static review of frozen snapshot `75316b4a` identified four P2 issues: root-only success without a childless invariant, nonzero exit replaced by stream failure, two independent stream bounds, and `Console.Error` / diagnostic attachment able to replace the primary. This remediation records the childless validator contract, ExitCode-first precedence, a single absolute stream deadline, retained PID/StartTime residual confirmation, and guarded secondary diagnostics. Focused roots-supplied Quick after this remediation is recorded below; root retains Full / ReleaseAcceptance integration ownership.
- Focused roots-supplied Quick after review remediation: `$env:BMS_SCD_APP_PUBLISH_ROOT=(Resolve-Path .\artifacts\publish\app).Path; $env:BMS_SCD_UPDATER_PUBLISH_ROOT=(Resolve-Path .\artifacts\publish\updater).Path; pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput'`. Result: 1/1 passed; test elapsed 7.2097s, runner elapsed 25s; artifact `artifacts/verification/tests-quick-20260826-014315/functional/results.trx`. No timeout or retry; Full / ReleaseAcceptance integration remains root-owned.
- Fresh review of snapshot `b88d9bfb` found that a successful `Process.Start()` could still skip root cleanup when `Process.Id` threw, and could refuse `Kill(false)` when `StartTime` threw. The fix records start success in an independent `processStarted` state immediately, starts both redirected readers from the retained handle, preserves PID/StartTime capture failures as primary setup evidence, and always invokes retained-handle root-only cleanup. `CleanupOwnedProcess` and `ObserveRootProcess` now treat metadata as optional identity diagnostics while retaining `HasExited`, `Kill(entireProcessTree: false)`, bounded `WaitForExitAsync`, and residual confirmation; the childless, 60/5/5-second, precedence, EDI/secondary, and no-global/name/tree-kill contracts remain unchanged.
- Roots-supplied exact filtered Quick after this fix (filter `FullyQualifiedName=BeMusicSeeker.Tests.UpdaterDeploymentBoundaryTests.PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput`): 1/1 passed; test elapsed 9.0085s, filtered build/test elapsed 41s; artifact `artifacts/verification/tests-quick-20260826-015925/functional/results.trx`. No timeout or retry; Full / ReleaseAcceptance integration remains root-owned.

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

### Completion evidence

- Final implementation snapshot: `26bf7a19`. Unit commits are `3f04f9c5` (Unit 1A), `e5324371` (Unit 1B), `75316b4a` / `b88d9bfb` / `26bf7a19` (Unit 2 and review fixes). No push was performed.
- On the final snapshot, the first standalone Functional attempt reached the 180-second bound only in `remaining`; the other five hosts exited zero and the tracked-tree fingerprint was unchanged (`artifacts/verification/tests-functional-20260826-020354`). The single permitted exact retry passed all six hosts with no failures in 177.7 seconds (`remaining`: 2,603 passed / 8 skipped; `remaining-bms-library`: 652 passed / 3 skipped; the other four shards all passed), artifact `artifacts/verification/tests-functional-20260826-020740`.
- `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full` passed with exit code zero, artifact `artifacts/verification/tests-full-20260826-021109`. Tool restore, locked restore, Release/x64 build, all six internal Functional shards, tool smoke, current and baseline distribution publish, existing-data acceptance, update acceptance, format verification, and analyzer all passed. `ProcessIntegration` passed 54 with 2 skipped in 82.504 seconds; `ReleaseAcceptance` passed 2/2 in 15.955 seconds, including `PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput`; Roslynator reported 0 diagnostics. The tracked-tree fingerprint remained unchanged.
- Frozen-snapshot static review first found four Unit 2 failure-contract issues in `75316b4a`; fresh review of `b88d9bfb` found one post-start metadata cleanup issue. After the corresponding fixes and focused Quick runs, fresh static review of `26bf7a19` reported no blocking finding, pre-existing/out-of-scope finding, or recommendation.
- Changed-file scans found no retired local STA helper, raw dispatcher frame, unbounded join / process wait, synchronous redirected-stream drain, physical cursor primitive, global/name/tree process kill, or new parallelization suppression. Repository-wide physical cursor scan returned zero. The repository-wide unbounded-wait scan returned only pre-existing candidates outside Units 1A/1B/2; the ignore scan returned the one pre-existing `ChartInfoParserBehaviorTests` manual-smoke ignore.
- Recommended Units 3–5 remain deferred as separate follow-up work: classify and harden the remaining pre-existing wait candidates, consider bounded dispatcher-host shutdown, and replace or reclassify the ignored ChartInfo parser smoke test. Optional repeat-gate/topology work remains out of scope.

## Required handoff

完了時は unitごとのobservable outcome、modified paths、retired helper / route とreplacement、coverage ledger、shared resource / lane / DNP、completion signal / watchdog、process ownership / cleanup / stream precedence、exact commands / artifacts / elapsed、not-run items、deferred recommended / optional unitを記録する。
