# テスト整理後 Blocking Findings 修正計画

Status: Complete (historical record)

Review base: `7835539a21091b47c67dc13866ad4fcdae759ccc`

Plan date: 2026-08-23

> **Archive note:** この文書は完了時 snapshot の実行計画と検証証跡である。現行の lane / timeout / retry は `../spec/testing-strategy.md`、test implementation は `../spec/test-authoring-contract.md`、agent workflow は `../spec/codex-agent-workflow.md` を正とする。以下の Unit、反復回数、当時の gate は履歴として読む。

## 当時の Codex 実行指示

`AGENTS.md` と `devdocs/spec/codex-agent-workflow.md` に従って Unit 0 から実行する。ルートが設計、計画、統合、最終検証を所有し、実装は bounded unit ごとに `implementation-worker` へ渡す。

- review base は blocking findings が確認された production / test snapshot である。開始時の implementation file が review base と異なる場合は、該当差分だけを再監査し、finding が既に解消済みか、line / symbol / test delta を更新すべきか判断してから編集する。
- Unit 0 の `plan-clarifier` は不明点、test coverage、並列境界の点検だけを行う。repository で解けない observable semantics または replan trigger がない限り、計画を作り直したりユーザー質問を増やしたりしない。
- Unit 1 と Unit 2 は同一 worktree では逐次実行する。隔離 worktree、独立した output root、独立した verification がそろう場合だけ並列化する。

## Goal

テスト整理後レビューで確認された次の2件を、Functional test execution 全体の180秒 budget と既存の failure contract を維持したまま解消する。

1. `scripts/verify-refactor.ps1` が process timeout / failure 後に redirected stdout / stderr の EOF を無期限に待ち、runner 自身が cleanup reserve を超えて停止し得る。
2. 4つの compiled WPF fixture が、watchdog なしのローカル `Dispatcher.PushFrame` helper で shutdown task を待ち、testhost 全体を runner timeout まで停止させ得る。

完了時には、同じ種類の無期限待機を再導入しにくい実装、テスト、レビュー境界も同じ変更で閉じる。

## Context / repository evidence

### Runner

- `scripts/verify-refactor.ps1:627-649` の `Invoke-MonitoredCommand` は、process 終了後に `ReadToEndAsync()` task へ無期限の `GetResult()` を行う。
- `scripts/verify-refactor.ps1:1557-1564` の Functional shard 回収も、同じ無期限待機を行う。
- Functional には10秒の cleanup reserve と7秒の process cleanup deadline があるが、stream drain はその境界を消費せずに待てる。
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs:175-239` は、別 process を読む側では5秒の stream timeout を既に採用している。ただし、runner 本体の実行経路は検証していない。
- cleanup で確認、停止する対象は、runner が起動した PID lineage だけとする。マシン上の無関係な `dotnet` / `testhost` / `vstest` を process 名だけで kill してはならない。

### WPF cleanup

次の fixture にローカル `AwaitOnDispatcher` と watchdog なしの `Dispatcher.PushFrame` がある。

- `BeMusicSeeker.Tests/MainWindowPackageMaintenanceWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowProgressStatusBarWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowViewHostTests.cs`

共通の `TestUiDispatcherHost.AwaitTaskOnDispatcher(Task, string)` は `BeMusicSeeker.Tests/TestUiScheduler.cs:197-269` にあり、5秒の failure watchdog、task fault 伝播、diagnostic operation name を既に持つ。

## Constraints

- production behavior、test assertions、Functional shard membership、worker 数、180秒 budget を変更しない。
- timeout 値の延長、Functional の並列度低下、追加の `DoNotParallelize` で症状を隠さない。
- primary failure の優先順位を維持する。process timeout、nonzero exit、launch / orchestration failure がある場合、stream drain や cleanup failure で置き換えず secondary diagnostics として残す。
- process が成功しても、stream drain または runner-owned process cleanup が境界内に完了しなければ成功扱いにせず、infrastructure cleanup failure とする。
- incomplete な `Task<string>` に対して `.Result` / `GetAwaiter().GetResult()` を呼ばない。取得できた completed stream だけを保存し、未完了 stream は明示的な diagnostic を残す。
- runner lifecycle test は、production helper または production execution seam そのものを通す。テスト側に同等ロジックをコピーしたり、自己申告 metadata だけを検証したりしない。
- WPF fixture 側で新しい dispatcher pump を再実装しない。共通 helper の契約が不足する場合だけ `TestUiDispatcherHost` を拡張する。
- 無関係な P2 整理、raw `HwndSource` 全件移行、runner operation plan の全面再設計、既存 source-artifact test の一括移行は、この計画の対象外とする。

## Decision list

次の semantics は確定済みであり、worker が再判断しない。

1. **Stream drain timeout**: process timeout とは独立した bounded cleanup failure である。Functional では残っている10秒 cleanup reserve 内、通常 monitored command では明示した最大5秒以内で完了させる。
2. **Output preservation**: timeout 時も completed な stdout / stderr は UTF-8 log へ保存する。未完了側には `stream-drain-timeout` と対象 stream 名、process PID、経過時間を diagnostic へ残す。
3. **Failure precedence**: primary process / orchestration failure を throw する前に cleanup diagnostics を保存する。cleanup だけが失敗した成功 process では cleanup failure を primary にする。
4. **Process ownership**: cleanup と残留確認は、runner が起動した root PID と追跡できた descendant PID だけを対象にする。global process-name scan による kill は禁止する。
5. **WPF wait**: affected fixture は `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使い、operation ごとに識別可能な名前を渡す。ローカル `AwaitOnDispatcher` は削除する。
6. **Final stability gate**: 最終 snapshot で Functional を3回連続実行する。途中で failure を修正した場合、それ以前の pass は数えず1回目からやり直す。

## Test delta table

| Behavior / failure contract | Existing coverage | Action | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- |
| process が終了し、stdout / stderr も閉じた通常経路 | runner 本体の直接 contract なし | production lifecycle seam を通る contract test を追加 | `ProcessIntegration` | process exit + 両 stream task 完了 | 無期限 `GetResult()` |
| child が redirected handle を保持し、root process だけが終了する経路 | なし | inherited-handle probe で bounded drain と diagnostic を検証 | `ProcessIntegration`、runner-owned PID lineage | stream deadline または child cleanup 完了 | EOF 待ちによる runner hang |
| process timeout / nonzero exit と stream cleanup failure が併発 | 部分的な process timeout contract のみ | primary failure 保持と secondary cleanup diagnostic を検証 | `ProcessIntegration` | primary error + diagnostic artifact | cleanup failure による primary 上書き |
| Functional shard cleanup 後に runner-owned process が残らない | 文書上の要求のみ | tracked PID lineage の bounded residual check を実装、検証 | Functional runner | tracked PID 不在 | top-level process だけの確認 |
| MainWindow shutdown task の dispatcher 待機 | 4 fixture に unbounded local helper | 共通 watchdog helper へ置換 | `compiled-wpf-classwide` / 1 worker | task completion または5秒 watchdog failure | 4つの local `AwaitOnDispatcher` |
| dispatcher wait の fault / watchdog diagnostic | 共通 helper 実装のみ | helper を変更する場合だけ focused contract test を追加 | shared WPF host | fault 伝播、operation name 入り timeout | fixture ごとの pump 実装 |

## Unit 0: Baseline freeze and plan clarification

Owner: root agent

Writes: この plan の Progress / Verification Log だけ

1. `git status --short` と `git rev-parse HEAD` を記録し、review base 以外の未コミット差分を識別する。
2. `plan-clarifier` にこの文書、review findings、上記 test delta を一度渡す。
3. repository から解けない observable semantics が新たに見つからない限り、ユーザー質問は増やさない。
4. Unit 1 / 2 の path ownership と、runner-owned descendant を識別する Windows API / process seam を確定する。

Replan triggers:

- runner-owned descendant を無関係な process と区別できず、global process scan が必要になる。
- actual runner seam を通す deterministic inherited-handle probe を作れず、production code の大規模分割が必要になる。
- 共通 WPF helper が affected task の dispatcher / shutdown contract を満たさない。

## Unit 1: Bound runner process and stream lifecycle

Owner: `implementation-worker`

Default execution: Unit 2 の前後どちらかで逐次実行する。隔離 worktree があり、generated output と verification を共有しない場合だけ Unit 2 と並列化してよい。

Writable paths:

- `scripts/verify-refactor.ps1`
- 必要なら新しい `scripts/verification-process-lifecycle.ps1`
- 必要なら新しい `scripts/test-fixtures/*`
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs`、または責務を分けた新しい runner lifecycle test file
- runner lifecycle contract を変えた場合の `devdocs/spec/testing-strategy.md`

Read-only references:

- `scripts/verification-runner-contract.ps1`
- `BeMusicSeeker.Tests/DistributionArtifactContractTests.cs`
- `devdocs/spec/test-authoring-contract.md`

### Required implementation

1. stdout / stderr task を一つの shared bounded-drain seam で回収し、`Invoke-MonitoredCommand` と Functional shard cleanup の両方から使用する。
2. helper は deadline / remaining cleanup budget、stream tasks、PID、diagnostics directory を受け、completed output と cleanup diagnostics を構造化して返す。
3. 両 task が deadline 内に完了した場合だけ結果を同期取得する。faulted task は例外内容を cleanup diagnostic にする。
4. deadline を超えた場合は completed 側だけを保存し、未完了側を明記する。redirected reader を閉じるか cancellation / continuation で未完了 task の最終例外を観測し、放置した task が後から primary failure を置き換えないようにする。process object の dispose も同じ deadline 内で行う。
5. Functional では process cleanup、fallback helper cleanup、stream drain、tracked descendant 確認を同じ10秒 cleanup reserve 内で配分する。個別に10秒ずつ追加して global budget を破らない。
6. runner-owned root / descendant PID を追跡し、cleanup deadline 後に残っていれば PID と command identity を diagnostic へ残して failure にする。無関係な process は停止しない。
7. primary failure、cleanup failure、diagnostic write failure の優先順位を一箇所で明示し、既存の primary failure 文言と diagnostics path を保つ。

### Required tests

production seam を直接通す deterministic probe で、少なくとも次を確認する。

- normal exit: stdout / stderr が完全に保存され、cleanup failure がない。
- root exit + descendant inherited handle 保持: runner / test probe が bounded time で戻り、`stream-drain-timeout` または tracked descendant cleanup の diagnostic を持つ。
- timeout / nonzero exit + stream cleanup failure: primary failure 種別が維持され、secondary diagnostic が失われない。
- cleanup 完了後: probe が起動した root / descendant PID が残らない。

テストのために production lifecycle 処理を複製してはならない。private function を直接呼べない場合の推奨は、lifecycle helper だけを別 script へ抽出し、runner と probe の双方が source する形である。test-only bypass を追加する場合は、明示 env guard がない通常 runner から到達不能にする。

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'
```

`VerificationRunnerContractTests` と新しい lifecycle test は `ProcessIntegration` のまま明示 filter 付き Quick で実行する。通常 Functional では除外されることを runner contract でも確認する。

### Handoff

- production process lifecycle seam と caller 一覧
- primary / cleanup failure matrix
- probe が使用した root / descendant PID と最終残留確認
- focused command、elapsed、artifact path
- test delta の `extend / replace / new` 結果

## Unit 2: Replace unbounded WPF fixture pumps

Owner: `implementation-worker`

Writable paths:

- `BeMusicSeeker.Tests/MainWindowPackageMaintenanceWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowProgressStatusBarWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowViewHostTests.cs`
- 共通 helper の契約変更が必要な場合だけ `BeMusicSeeker.Tests/TestUiScheduler.cs`
- 共通 helper test が必要な場合だけ `BeMusicSeeker.Tests/WpfTestApplicationHostTests.cs`

### Required implementation

1. 各 `AwaitOnDispatcher` call を `TestUiDispatcherHost.AwaitTaskOnDispatcher` へ置換する。
2. operation name は fixture / operation を識別できる固定文字列にする。複数 task を待つ `MainWindowViewHostTests` は別名を使う。
3. 4つの private helper を削除し、不要になった `DispatcherFrame` / `DispatcherPriority` / dispatcher parameter / using を整理する。
4. task fault、cancel、shutdown request の既存 semantics を変更しない。
5. 共通 helper を変更せずに済むなら変更しない。不足がある場合は既定5秒 watchdog を維持し、テストだけ短い watchdog を注入できる internal seam を検討する。通常完了に delay を追加しない。

### Required checks

```powershell
rg -n 'private static void AwaitOnDispatcher|Dispatcher\.PushFrame' `
  .\BeMusicSeeker.Tests\MainWindowPackageMaintenanceWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowProgressStatusBarWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowTreePresentationWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowViewHostTests.cs
```

上記は match 0 を受入条件とする。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~MainWindowPackageMaintenanceWpfTests|FullyQualifiedName~MainWindowProgressStatusBarWpfTests|FullyQualifiedName~MainWindowTreePresentationWpfTests|FullyQualifiedName~MainWindowViewHostTests|FullyQualifiedName~WpfTestApplicationHostTests'
```

既知の低頻度停止を確認するため、統合後に同じ filter を30回反復する。各回で timeout、残留 test process、tracked file 変更がないことを記録する。1回でも failure があれば成功回数を積み増さず root cause を修正し、反復を1回目からやり直す。

### Handoff

- 削除した4 helper と replacement call の対応
- operation name 一覧
- affected fixture の lane / shared resource / `DoNotParallelize` が不変であること
- focused / repeated verification 結果

## Unit 3: Integration, final verification, and fresh review

Owner: root agent

Writes: mechanical integration conflict と plan log だけ

1. Unit 1 / 2 の handoff、path ownership、test delta を確認する。
2. `git diff --check`、PowerShell parse、C# build を行う。
3. Unit 1 / 2 の focused Quick / ProcessIntegration を統合 snapshot で一度実行する。
4. WPF focused filter を30回反復する。failure 修正後は1回目から数え直す。
5. runner 変更のため、同じ最終 snapshot、同じ command、同じ budget で Functional を3回連続実行する。各回について elapsed、run root、tracked fingerprint、runner-owned residual PID を記録する。途中修正後は3回をやり直す。
6. Full を1回実行する。Full failure を単なる Functional pass で代替しない。
7. 実装 thread を閉じ、snapshot を凍結して `repo-static-review` を呼ぶ。review 中に root は repository 操作を行わない。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Reviewer へ渡す重点:

- incomplete stream task への unbounded wait が全 caller から除去されたか
- process cleanup / stream drain / residual check が global cleanup reserve を超えないか
- primary failure が cleanup failure で上書きされないか
- actual runner seam をテストしているか。metadata が metadata 自身をテストする構造になっていないか
- runner-owned process だけを停止し、無関係な process を巻き込まないか
- 4 fixture の local dispatcher pump が完全に削除され、共通 watchdog へ接続されたか
- helper の local 再実装、固定 sleep、追加 DNP、worker 低下で問題を隠していないか

## Unit 1R: Replanned root fanout and bounded cleanup closure

Owner: `implementation-worker` (single sequential owner)

This unit supersedes the review-fix route that called lineage discovery before each Functional root termination request. The same P1 remained after two correction reviews, so further local additions to that route are prohibited.

Writable paths:

- `scripts/verification-process-lifecycle.ps1`
- `scripts/verify-refactor.ps1`
- `scripts/test-fixtures/verification-process-lifecycle-probe.ps1`
- `scripts/test-fixtures/verification-process-lifecycle-child.ps1`
- `BeMusicSeeker.Tests/VerificationProcessLifecycleTests.cs`
- lifecycle contract changes in `devdocs/spec/testing-strategy.md`

### Phase A: O(1) root-stop fanout before lineage work

Observable outcome: every Functional shard root receives a root-only termination request before any process-table snapshot, descendant discovery, wait, stream drain, residual check, persistence, or disposal begins for any shard.

1. Use the retained launch-time `Process` handle and launch identity. Immediately before the request, validate the retained handle's `StartTime` against the launch identity.
2. The first pass may call only the root-only primitive and `Kill(false)` (or an equivalent bounded root-only OS request). It must not enumerate or terminate descendants, scan the process table, wait, drain streams, write files, or dispose the process.
3. Only after the first pass has visited every Functional root may the second pass take bounded Toolhelp PID/PPID snapshots, query creation time for attributable candidates, collect descendants, drain streams, persist output, dispose processes, and check residual ownership.
4. The second pass uses the one existing absolute Functional cleanup deadline. It does not reset a per-entry deadline. No process-table or creation-time query is started after that deadline.
5. If the deadline prevents residual confirmation, report cleanup failure and uncertainty; do not silently claim cleanup success and do not use a process-name fallback.

### Phase B: one cleanup transition and deadline-closed terminal operations

Observable outcome: every ordinary monitored command, including normal success with completed streams, enters cleanup exactly once with `cleanupDeadline = min(now + 5 seconds, phaseDeadline)` before persistence or disposal. Functional keeps its existing shared absolute cleanup deadline.

1. Once the applicable deadline is reached, do not initiate reader `Close`, a residual snapshot, blocking I/O, a blocking wait, or a disposal wait. Already-started asynchronous operations may only be observed without blocking.
2. Output persistence, reader closure, and process disposal must start before and complete within the applicable deadline, or add a contextual cleanup diagnostic containing operation, PID, and elapsed time.
3. Preserve primary failure precedence. Cleanup failures remain secondary unless the process path itself otherwise succeeded.
4. The deterministic probe writes a sidecar ownership ledger immediately after each root or child launch, with PID and creation identity. The C# harness uses that exact ledger in `finally` for bounded identity-validated cleanup, independent of the production result JSON. It must not copy descendant discovery or use name/global scans.

### Required behavior tests

- Functional fanout order: all root-only requests are observed before the first lineage snapshot or descendant operation.
- Ordinary normal success: cleanup transition occurs once before persistence/disposal and produces no cleanup failure.
- Stream deadline: after expiry, no synchronous reader close or residual scan starts; late task faults are still observed diagnostically.
- Probe failure cleanup: exact ledger PIDs are absent after the outer harness cleanup even if production result JSON is missing.
- Existing normal, inherited-handle, timeout/nonzero, primary-precedence, contextual diagnostic, and residual-PID cases remain passing through the production seam.

Replan triggers:

- retained-handle `Kill(false)` cannot provide a bounded root-only request;
- supported Windows/runtime behavior cannot provide reliable post-exit Toolhelp PPID attribution;
- the required sidecar cannot be made independent of the production result contract without duplicating production lineage logic.

## Unit 2R: Stabilize play-history compiled-WPF startup sequencing

Owner: `implementation-worker`

Writable path: `BeMusicSeeker.Tests/MainWindowPlayHistoryWpfTests.cs`

Observable outcome: `PlayHistoryMainTable_ShowsDedicatedSummaryCardsAndDiagnostics` no longer races `BeginRequest` against the first visual-host startup/tree-selection deactivation.

1. Create and attach the visual host before activating play history.
2. Observe the actual startup deactivation/visibility transition through the existing dispatcher/test-host synchronization surface; do not add fixed sleep, timeout extension, DNP, or retry.
3. Call `BeginRequest` only after that signal, then observe the summary bar `Visible` transition before asserting the remaining bindings.
4. Preserve production behavior and the existing cleanup/dispatcher host contract.

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName=BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests.PlayHistoryMainTable_ShowsDedicatedSummaryCardsAndDiagnostics'
```

## Done when

- `Invoke-MonitoredCommand` と Functional shard 回収に unbounded stream `GetResult()` が残っていない。
- stream drain と runner-owned process cleanup は bounded で、diagnostic と failure precedence がテストされている。
- affected 4 fixture に private `AwaitOnDispatcher` と直接 `Dispatcher.PushFrame` が残っていない。
- focused WPF filter 30回、Functional 3回連続、Full 1回が同一の最終 snapshot で成功している。
- Functional 各回が180秒以内、tracked files 不変、runner-owned residual process なしである。
- fresh static review で P0 / P1 と acceptance へ直接反する P2 がない。
- verification evidence に HEAD、worktree diff hash、runner hash、各 run root / elapsed / result が記録されている。

## Progress

| Unit | Status | Notes |
| --- | --- | --- |
| Unit 0: Baseline freeze and clarification | Complete | HEAD `9dc805563bc9098531d7a0a06dff029b3b5e7c7d`, clean worktree. Review base から対象 files に差分なし。追加の observable-semantics question なし。Unit 1 / 2 は逐次実行。 |
| Unit 1: Runner process and stream lifecycle | Complete | Shared bounded lifecycle seamをrunnerの2 callerへ接続。Toolhelp32 PID lineage、bounded stream drain、primary/cleanup precedence、actual-seam ProcessIntegration probeを追加。再発したCIM/`WaitForExit()` blockerはresolverで除去。 |
| Unit 2: WPF dispatcher cleanup | Complete | 4 fixtureのlocal helper/pumpを既存`TestUiDispatcherHost.AwaitTaskOnDispatcher`へ置換。共通helper、lane、worker、DNPは不変。 |
| Unit 1R: Replanned lifecycle closure | Complete after second remediation | `98c4cbaaa7eed4f4c25e1453ad2eef245f134858`。native Capture deadline/partial state、cleanup task wait gate、shared `Invoke-VerificationFunctionalCleanup`、全residual exact assertionを追加。resolverでprobeの`pwsh`/`conhost` identity mismatchをheadless `wscript` fixtureへ修正。focused 10/10。 |
| Unit 2R: Play-history WPF startup sequencing | Complete | `28e90e79`。host attachment → startup deactivation/Collapsed signal → BeginRequest → Visible signalへfixture順序を固定。focused 1/1、class 3/3。 |
| Unit 3: Integration and final review | Complete | Unit 2R後の同一snapshot `28260a09fd0ce7e503828b0f1d74cb7cea23f64e` で lifecycle/WPF Quick、WPF 30回、Functional 3回、Full 1回を完了。`68f31eb78f1ef1ea4a00c190c1b9117d4766f08e` の fresh static review は blocking finding なし。 |

## Verification log

| Snapshot | Command / filter | Result | Elapsed | Artifact / residual-process evidence |
| --- | --- | --- | ---: | --- |
| `9dc805563bc9098531d7a0a06dff029b3b5e7c7d` | Unit 0 baseline / plan clarification | Pass | n/a | clean worktree; both findings reproduced by targeted static inspection; no user decision required |
| Unit 1 pre-resolver | filtered Quick: `VerificationRunnerContractTests|VerificationProcessLifecycleTests` | Fail (5/6) | 17.7s test | `tests-quick-20260823-220916`; recurring stream watchdog, no residual process; resolver threshold met |
| Unit 1 current worktree | same filtered Quick after resolver fix | Pass (6/6) | 7.9s test / 16.4s phase | `tests-quick-20260823-222602/functional`; tracked fingerprint unchanged; direct probe owned PIDs removed |
| Unit 2 current worktree | filtered Quick: four affected WPF fixtures + `WpfTestApplicationHostTests` | Pass (28/28) | 35.3s | `tests-quick-20260823-223123/functional`; anti-pattern scan 0; no residual test process |
| `f1c7860ccb34c1e2e528f23a7e75a23fc7d1934e` | PowerShell parse / `git diff --check` / Release build | Pass (0 errors) | 21.8s build | runner SHA-256 `8A53D15B3550DC314B4CEF4D8837CE4DB3472F2060EFB36CE6B5A6163EE22D22`; existing build warnings only |
| same | integrated runner lifecycle Quick | Pass (6/6) | 8.0s test / 16.8s phase | `tests-quick-20260823-223413/functional`; fingerprint unchanged; no cleanup diagnostic |
| same | integrated WPF Quick | Pass (28/28) | 10.0s test / 18.3s phase | `tests-quick-20260823-223444/functional`; fingerprint unchanged; no cleanup diagnostic |
| same | WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 25.5-27.1s / run | run roots: `tests-quick-20260823-223539`, `tests-quick-20260823-223606`, `tests-quick-20260823-223632`, `tests-quick-20260823-223658`, `tests-quick-20260823-223725`, `tests-quick-20260823-223751`, `tests-quick-20260823-223817`, `tests-quick-20260823-223843`, `tests-quick-20260823-223910`, `tests-quick-20260823-223936`, `tests-quick-20260823-224003`, `tests-quick-20260823-224030`, `tests-quick-20260823-224057`, `tests-quick-20260823-224123`, `tests-quick-20260823-224149`, `tests-quick-20260823-224216`, `tests-quick-20260823-224243`, `tests-quick-20260823-224309`, `tests-quick-20260823-224335`, `tests-quick-20260823-224402`, `tests-quick-20260823-224429`, `tests-quick-20260823-224456`, `tests-quick-20260823-224523`, `tests-quick-20260823-224549`, `tests-quick-20260823-224615`, `tests-quick-20260823-224642`, `tests-quick-20260823-224708`, `tests-quick-20260823-224734`, `tests-quick-20260823-224801`, `tests-quick-20260823-224828`; all fingerprints unchanged; no cleanup diagnostic |
| same | Functional 1/3 | Pass | 160.1s command / 145.0s phase | `tests-functional-20260823-224913`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | Functional 2/3 | Pass | 158.8s command / 144.0s phase | `tests-functional-20260823-225152`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | Functional 3/3 | Pass | 157.1s command / 142.4s phase | `tests-functional-20260823-225431`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | Full | Pass | about 524s total; canonical Functional 156.2s / 180s | `tests-full-20260823-225723`; publish, existing-data, update, ProcessIntegration, ReleaseAcceptance, format, analyzer passed; fingerprint unchanged; residual `testhost` / `vstest.console` count 0 |
| `59c3c8b9f21de71c1e8a841c33e3305eee895cfe` | fresh static review | Blocking | n/a | 2 P1: cleanup reserve二重控除、kill前identity再照合不足。2 acceptance-direct P2: monitored cleanup 10s、stream diagnostic context不足。 |
| review-fix worktree | lifecycle Quick after reserve/identity/diagnostic fixes and probe EOF-seam correction | Pass (6/6) | 8.5s test / 34.1s phase | `tests-quick-20260823-233441/functional`; direct probe 2.4s、owned PID residual 0。旧stability runは変更前snapshotのため最終gateには数えない。 |
| `24e119069a0be27d7b23161286f1c849ea2d10a7` | final PowerShell parse / `git diff --check` / Release build | Pass (0 errors) | 21.7s build | runner SHA-256 `CE329370C37C42F4AA193A651347F080C9A22D0006039A18BD5655B99F161248`; existing build warnings only |
| same | final integrated lifecycle Quick | Pass (6/6) | 7.9s test / 16.7s phase | `tests-quick-20260823-233741/functional`; fingerprint unchanged; no cleanup diagnostic |
| same | final integrated WPF Quick | Pass (28/28) | 10.3s test / 18.9s phase | `tests-quick-20260823-233815/functional`; fingerprint unchanged; no cleanup diagnostic |
| same | final WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 25.7-28.6s / run | run roots: `tests-quick-20260823-233854`, `tests-quick-20260823-233921`, `tests-quick-20260823-233948`, `tests-quick-20260823-234015`, `tests-quick-20260823-234042`, `tests-quick-20260823-234109`, `tests-quick-20260823-234135`, `tests-quick-20260823-234201`, `tests-quick-20260823-234228`, `tests-quick-20260823-234255`, `tests-quick-20260823-234324`, `tests-quick-20260823-234349`, `tests-quick-20260823-234416`, `tests-quick-20260823-234443`, `tests-quick-20260823-234510`, `tests-quick-20260823-234536`, `tests-quick-20260823-234603`, `tests-quick-20260823-234630`, `tests-quick-20260823-234657`, `tests-quick-20260823-234723`, `tests-quick-20260823-234750`, `tests-quick-20260823-234817`, `tests-quick-20260823-234844`, `tests-quick-20260823-234912`, `tests-quick-20260823-234939`, `tests-quick-20260823-235006`, `tests-quick-20260823-235033`, `tests-quick-20260823-235059`, `tests-quick-20260823-235126`, `tests-quick-20260823-235153`; all fingerprints unchanged; no cleanup diagnostic |
| same | final Functional 1/3 | Pass | 157.3s command / 142.0s phase | `tests-functional-20260823-235232`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final Functional 2/3 | Pass | 156.1s command / 140.9s phase | `tests-functional-20260823-235509`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final Functional 3/3 | Pass | 157.4s command / 142.3s phase | `tests-functional-20260823-235745`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final Full | Pass | about 517.3s total; canonical Functional 153.9s / 180s | `tests-full-20260824-000034`; publish, existing-data, update, ProcessIntegration, ReleaseAcceptance, format, analyzer passed; fingerprint unchanged; residual `testhost` / `vstest.console` count 0 |
| `69050a8f01c6f63dab76e4778a9654fa1ef00874` | first fix fresh static review | Blocking | n/a | 1 P1: cleanup deadline逐次消費前の全shard停止fanout不足。3 acceptance-direct P2: monitored cleanup起点、diagnostic assertion、bounded output/dispose。 |
| second-review-fix worktree | lifecycle Quick after fanout / transition deadline / bounded persistence / deterministic stream-timeout fixes | Pass (7/7) | 13.6s test / 22.2s runner | `tests-quick-20260824-004624`; stream-timeout contextual diagnostics 2件、direct probe全routeのowned PID residual 0。直前stability runは変更前snapshotのため最終gateには数えない。 |
| `0ce535d3e35f8fa0c1ac282484d54e64f0310974` | final-candidate PowerShell parse / `git diff --check` / Release build | Pass (0 errors) | 21.7s build | runner SHA-256 `16A836953F2494CCB5F52C242D05683F98381ED0C4A33EA581C9B0B86F16634B`; existing build warnings only |
| same | final-candidate integrated lifecycle Quick | Pass (7/7) | 13.6s test / 22.5s phase | `tests-quick-20260824-004850/functional`; fingerprint unchanged; deterministic stream-timeout case included |
| same | final-candidate integrated WPF Quick | Pass (28/28) | 10.4s test / 18.9s phase | `tests-quick-20260824-004928/functional`; fingerprint unchanged; no cleanup diagnostic |
| same | final-candidate WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 26.3-27.8s / run | run roots: `tests-quick-20260824-005005`, `tests-quick-20260824-005031`, `tests-quick-20260824-005058`, `tests-quick-20260824-005125`, `tests-quick-20260824-005153`, `tests-quick-20260824-005219`, `tests-quick-20260824-005246`, `tests-quick-20260824-005313`, `tests-quick-20260824-005340`, `tests-quick-20260824-005407`, `tests-quick-20260824-005434`, `tests-quick-20260824-005501`, `tests-quick-20260824-005529`, `tests-quick-20260824-005555`, `tests-quick-20260824-005622`, `tests-quick-20260824-005648`, `tests-quick-20260824-005716`, `tests-quick-20260824-005742`, `tests-quick-20260824-005809`, `tests-quick-20260824-005837`, `tests-quick-20260824-005904`, `tests-quick-20260824-005930`, `tests-quick-20260824-005957`, `tests-quick-20260824-010025`, `tests-quick-20260824-010051`, `tests-quick-20260824-010118`, `tests-quick-20260824-010145`, `tests-quick-20260824-010213`, `tests-quick-20260824-010240`, `tests-quick-20260824-010307`; all fingerprints unchanged; no cleanup diagnostic |
| same | final-candidate Functional 1/3 | Pass | 158.7s command / 143.0s phase | `tests-functional-20260824-010348`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final-candidate Functional 2/3 | Pass | 161.3s command / 146.0s phase | `tests-functional-20260824-010626`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final-candidate Functional 3/3 | Pass | 159.0s command / 143.1s phase | `tests-functional-20260824-010908`; within 180s; fingerprint unchanged; no cleanup diagnostic |
| same | final-candidate Full | Pass | about 560.8s total; canonical Functional 169.6s / 180s | `tests-full-20260824-011153`; publish, existing-data, update, ProcessIntegration 37 pass + 2 intentional skip, ReleaseAcceptance, format, analyzer passed; fingerprint unchanged; residual `testhost` / `vstest.console` count 0 |
| `3f53752c96b4d4315f33077b279adde212b828ef` | second correction fresh static review | Blocking; replan required | n/a | Persistent P1: per-entry lineage scan can consume shared deadline before later root stop requests. Acceptance-direct P2: normal-success cleanup transition, post-deadline close/scan, and independent probe-child cleanup. Unit 1R supersedes the prior correction route. |
| `c3403326e7be9d521adcb109afdb84a4fafc5782` | Unit 1R PowerShell parse / `git diff --check` / lifecycle focused Quick | Pass (9/9) | focused phase completed within configured Quick budget | `tests-quick-20260824-020406`; no timeout or retry; exact-ledger owned PID residual 0; Functional/WPF/Full intentionally deferred to Unit 3 |
| `965dc8a098287f1d77ee90cd9548b89e128ca64a` | final PowerShell parse / `git diff --check` / Release build | Pass (0 errors) | 23.1s build | runner SHA-256 `9317E665A694599385215E37E92F9189B4EB14CE487F7754A387B234A25D996E`; existing build warnings only; clean fingerprint |
| same | final integrated lifecycle Quick | Pass (9/9) | 18.6s test / about 34s command | `tests-quick-20260824-020734`; fingerprint unchanged; exact-ledger owned PID residual 0 |
| same | final integrated WPF Quick | Pass (28/28) | 10.8s test / about 30s command | `tests-quick-20260824-020816`; fingerprint unchanged; no cleanup diagnostic |
| same | final WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 26.7-28.3s / run | run roots: `tests-quick-20260824-020904`, `tests-quick-20260824-020931`, `tests-quick-20260824-020958`, `tests-quick-20260824-021025`, `tests-quick-20260824-021053`, `tests-quick-20260824-021120`, `tests-quick-20260824-021147`, `tests-quick-20260824-021214`, `tests-quick-20260824-021242`, `tests-quick-20260824-021309`, `tests-quick-20260824-021336`, `tests-quick-20260824-021402`, `tests-quick-20260824-021430`, `tests-quick-20260824-021457`, `tests-quick-20260824-021525`, `tests-quick-20260824-021552`, `tests-quick-20260824-021620`, `tests-quick-20260824-021648`, `tests-quick-20260824-021715`, `tests-quick-20260824-021743`, `tests-quick-20260824-021810`, `tests-quick-20260824-021837`, `tests-quick-20260824-021905`, `tests-quick-20260824-021932`, `tests-quick-20260824-022000`, `tests-quick-20260824-022027`, `tests-quick-20260824-022055`, `tests-quick-20260824-022123`, `tests-quick-20260824-022151`, `tests-quick-20260824-022218`; all fingerprints unchanged; residual `testhost` / `vstest.console` count 0 |
| same | final Functional 1/3 | Pass | 165.7s command | `tests-functional-20260824-022304`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Functional 2/3 | Pass | 160.7s command | `tests-functional-20260824-022549`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Functional 3/3 | Pass | 161.8s command | `tests-functional-20260824-022829`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Full | Pass | canonical Functional 159.3s / 180s | `tests-full-20260824-023118`; current/baseline publish, existing-data, update, ProcessIntegration 39 pass + 2 intentional skip, ReleaseAcceptance 2/2, format, analyzer passed; 0 diagnostics; fingerprint unchanged |
| `b6d76206a6f58d52bbd8c964d2e34b945c0c6a51` | Unit 1R fresh static review | Blocking | n/a | 3 P1: descendant second scan/open after deadline, later-entry `New-Item` after shared deadline, unbounded post-lifecycle diagnostic filesystem reads that can replace primary failure. 1 acceptance-direct P2: order/deadline tests observe reported metadata instead of actual primitive boundaries; late fault and nonempty exact residual PID cases missing. |
| `16d256519b5f24cf63cf78c4d169a90b61448bfe` | review remediation parse / `git diff --check` / lifecycle focused Quick | Pass (10/10) | 49.6s focused test | `tests-quick-20260824-031735/functional`; actual primitive observer, deterministic late fault, nonempty residual PID and exact-ledger cleanup included; no replan trigger |
| `2c7f0c8522eb238de5858c13cf846d7f9a0f8ded` | post-review final parse / `git diff --check` / Release build | Pass (0 errors) | 23.3s build | runner SHA-256 `2C4C2E265CC0CFF71A0AB22759941D23B8913EBE1ED98F2B5FE5C5F01AE7867E`; existing build warnings only; clean fingerprint |
| same | post-review lifecycle Quick | Pass (10/10) | 23.2s test / 32.6s phase | `tests-quick-20260824-032020`; actual primitive, late fault, nonempty residual and exact-ledger cases included; fingerprint unchanged |
| same | post-review WPF Quick | Pass (28/28) | 10.6s test / 19.5s phase | `tests-quick-20260824-032106`; fingerprint unchanged; no cleanup diagnostic |
| same | post-review WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 26.3-30.8s / run | run roots: `tests-quick-20260824-032145`, `tests-quick-20260824-032214`, `tests-quick-20260824-032241`, `tests-quick-20260824-032310`, `tests-quick-20260824-032338`, `tests-quick-20260824-032406`, `tests-quick-20260824-032433`, `tests-quick-20260824-032501`, `tests-quick-20260824-032528`, `tests-quick-20260824-032557`, `tests-quick-20260824-032628`, `tests-quick-20260824-032658`, `tests-quick-20260824-032727`, `tests-quick-20260824-032754`, `tests-quick-20260824-032823`, `tests-quick-20260824-032850`, `tests-quick-20260824-032918`, `tests-quick-20260824-032946`, `tests-quick-20260824-033015`, `tests-quick-20260824-033043`, `tests-quick-20260824-033111`, `tests-quick-20260824-033140`, `tests-quick-20260824-033211`, `tests-quick-20260824-033240`, `tests-quick-20260824-033309`, `tests-quick-20260824-033336`, `tests-quick-20260824-033402`, `tests-quick-20260824-033429`, `tests-quick-20260824-033456`, `tests-quick-20260824-033523`; all fingerprints unchanged; residual `testhost` / `vstest.console` count 0 |
| same | post-review Functional 1/3 | Pass | 163.6s command | `tests-functional-20260824-033608`; within 180s; fingerprint unchanged; residual test process 0 |
| same | post-review Functional 2/3 | Pass | 168.5s command | `tests-functional-20260824-033851`; within 180s; fingerprint unchanged; residual test process 0 |
| same | post-review Functional 3/3 | Pass | 171.9s command | `tests-functional-20260824-034139`; within 180s; fingerprint unchanged; residual test process 0 |
| same | post-review Full | Pass | canonical Functional 164.0s / 180s | `tests-full-20260824-034438`; current/baseline publish, existing-data, update, ProcessIntegration 40 pass + 2 intentional skip, ReleaseAcceptance 2/2, format, analyzer passed; 0 diagnostics; fingerprint unchanged |
| `dc0ff1705bcf795d47779aaf2990418f32596eec` | post-remediation fresh static review | Blocking | n/a | 1 P1: native process-table Capture loop starts creation-time queries after shared deadline. 3 acceptance-direct P2: cleanup task wait直前gate不足、probeがactual Functional caller orchestrationを通らない、nonempty residual assertionがunrelated PID混入を許す。前回directory creation / recursive diagnostic P1は解消確認。 |
| `98c4cbaaa7eed4f4c25e1453ad2eef245f134858` | second remediation parse / `git diff --check` / lifecycle focused Quick | Pass (10/10) | 25.8s focused test | `tests-quick-20260824-042521/functional`; first two attempts exposed deterministic unledgered `conhost` blocker (`041100`, `041400`), resolver identified OS-created child and aligned probe identity universe without filtering production lineage; sleeper/testhost/vstest residual 0 |
| `bf23285bb95ce1a9fe0f91361328e6abd08b9da9` | observer timing correction / lifecycle focused Quick | Pass (10/10) | 25.6s test | `tests-quick-20260824-043403`; prior `042930` focused run exposed observer-induced check/use race; production-captured wait-start timestamp fixed it; fingerprint unchanged |
| same | final-candidate WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 26.1-29.4s / run | `tests-quick-20260824-043531` through `tests-quick-20260824-044857`; all fingerprints unchanged; residual test process 0 |
| same | final-candidate Functional 1/3, 2/3, 3/3 | Pass | 164.7s / 163.6s / 165.7s | `tests-functional-20260824-044941`, `tests-functional-20260824-045225`, `tests-functional-20260824-045508`; all within 180s; fingerprints unchanged; residual 0 |
| same | final-candidate Full | Fail: compiled-WPF assertion | canonical Functional 145.9s / 180s before failure surfaced | `tests-full-20260824-045759`; `MainWindowPlayHistoryWpfTests.PlayHistoryMainTable_ShowsDedicatedSummaryCardsAndDiagnostics` expected Visible, observed Collapsed. Non-timeout recurring fixture race (3/106 prior artifacts), no crash/cleanup/residual process; retry prohibited until Unit 2R correction. |
| `28e90e79` | Unit 2R focused exact method / full class Quick | Pass (1/1; 3/3) | within Quick budget | `tests-quick-20260824-050932`, `tests-quick-20260824-051021`; existing dispatcher/visibility signals only; no timeout or residual process |
| `28260a09fd0ce7e503828b0f1d74cb7cea23f64e` | final PowerShell parse / `git diff --check` / Release build | Pass (0 errors) | 24.1s build | runner SHA-256 `9259F4B7E606BA8566CF277BF3B512B82EA10C628D1CA89CFB7BA42106E303BC`; existing build warnings only; tracked fingerprint `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` |
| same | final integrated lifecycle Quick | Pass (10/10) | 26.8s test / 37.5s phase | `tests-quick-20260824-051235`; exact-ledger cases included; fingerprint unchanged; residual test process 0 |
| same | final combined WPF Quick including Unit 2R | Pass (31/31) | within Quick budget | `tests-quick-20260824-051328`; original focused fixtures, shared host, and play-history fixture included; residual test process 0 |
| same | final WPF focused filter, 30 consecutive runs | Pass (30/30 runs) | 26.4-32.7s / run | run roots: `tests-quick-20260824-051513`, `tests-quick-20260824-051542`, `tests-quick-20260824-051612`, `tests-quick-20260824-051640`, `tests-quick-20260824-051708`, `tests-quick-20260824-051736`, `tests-quick-20260824-051805`, `tests-quick-20260824-051835`, `tests-quick-20260824-051904`, `tests-quick-20260824-051933`, `tests-quick-20260824-052004`, `tests-quick-20260824-052035`, `tests-quick-20260824-052105`, `tests-quick-20260824-052135`, `tests-quick-20260824-052203`, `tests-quick-20260824-052233`, `tests-quick-20260824-052304`, `tests-quick-20260824-052334`, `tests-quick-20260824-052405`, `tests-quick-20260824-052437`, `tests-quick-20260824-052509`, `tests-quick-20260824-052538`, `tests-quick-20260824-052604`, `tests-quick-20260824-052632`, `tests-quick-20260824-052659`, `tests-quick-20260824-052726`, `tests-quick-20260824-052753`, `tests-quick-20260824-052820`, `tests-quick-20260824-052847`, `tests-quick-20260824-052914`; all tracked fingerprints unchanged; residual test process 0 |
| same | final Functional 1/3 | Pass | 166.2s command | `tests-functional-20260824-052952`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Functional 2/3 | Pass | 161.6s command | `tests-functional-20260824-053238`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Functional 3/3 | Pass | 163.5s command | `tests-functional-20260824-053520`; within 180s; fingerprint unchanged; residual test process 0 |
| same | final Full | Pass | 532.2s total; canonical Functional 165.0s / 180s | `tests-full-20260824-053810`; current/baseline publish, existing-data, update, ProcessIntegration 40 pass + 2 intentional skip, ReleaseAcceptance 2/2, format, analyzer passed; 0 diagnostics; fingerprint unchanged; residual test process 0 |
| `68f31eb78f1ef1ea4a00c190c1b9117d4766f08e` | final fresh static review | Pass: no blocking findings | n/a | P0/P1、acceptance-direct P2、pre-existing/out-of-scope、recommendation はすべて0件。native deadline gate、cleanup wait gate、shared Functional caller seam、exact residual ledger、observer timing、play-history WPF startup sequencingを確認。 |
