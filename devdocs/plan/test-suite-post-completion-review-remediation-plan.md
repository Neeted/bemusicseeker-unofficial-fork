# テスト整理完了後レビュー P2 修正計画

Status: Unit 2 implementation complete; stability verification pending

Review base: `30d25e792ec4b58c552db7651e8d615fe54c11d1`

Plan date: 2026-08-24

## Goal

完了済みの runner lifecycle 改善に残った4件の acceptance-direct P2 を、既存の絶対 cleanup deadline、Functional 180秒 command budget、runner-owned identity、primary failure precedence を維持したまま閉じる。

1. stdout / stderr の片側だけが完了した非対称 timeout でも、完了側の UTF-8 artifact を期限内に保存する。
2. deadline 後に fault した task observation を lifecycle owner ごとに分離し、後続 command / shard へ誤帰属させない。
3. `Process.Start()` 成功後の予期しない例外も同じ bounded ownership cleanup に合流させ、live process と既存 artifact を残さない・壊さない。
4. write / dispose 待機で発生した terminal diagnostic を final snapshot より前に確定し、成功した `process-lifecycle.log` から欠落させない。

併せて、完了計画の一覧表記と canonical lifecycle fixture の Verification map を現状へ合わせる。

## Context

- `Invoke-BoundedProcessLifecycle` は片側 stream が未完了のまま deadline を消費すると、完了側の値を回収しても persistence 全体の deadline gate により `stdout.log` / `stderr.log` を両方省略する。
- late observation は process-wide static queue へ入り、最後の drain 後に enqueue された observation を次 lifecycle が回収できる。
- `Invoke-MonitoredCommand` の outer catch は、process start 後でも bounded stop を行わず dispose だけを試み、既存 log を空文字で上書きし得る。
- `process-lifecycle.log` の payload は terminal write / dispose task を待つ前に snapshot されるため、待機中の fault / timeout と terminal-operation skip が artifactへ入らない。
- `devdocs/plan/README.md` は完了済み計画を現在進行中として掲載している。
- `VerificationProcessLifecycleTests` は canonical fixture だが、`devdocs/spec/testing-strategy.md` から fixture / lane / shared resource contract を直接辿れない。

## Constraints

- cleanup deadline を延長しない。deadline 到達後に blocking wait、reader close、native residual scan、filesystem write、dispose wait を新規開始しない。
- process 名による global kill、意味の変わる fallback、PIDだけの ownership 判定を追加しない。
- primary process / orchestration failure を cleanup / persistence failure で置換しない。primary がない cleanup-only failure は成功に隠さない。
- production lifecycle を test 側へ複製しない。actual shared seam / caller を guarded deterministic probe から通す。
- runner、lane、worker topology、Functional 180秒 budget、WPF fixture の production behavior は変更しない。
- fixed sleep、timeout延長、retry、追加 `DoNotParallelize` を使わない。

## Decision list

- 3件はすべて現行仕様へ直接反する P2 であり、修正対象とする。
- lifecycle-local observation owner を正本とし、PID filter付き global queue は採用しない。
- timeout 時の durable owner diagnostic は deadline 内に確定できる `stream-drain-timeout` とする。deadline 後の exact task exception は必ず観測して unobserved fault を防ぐが、別 lifecycle の result / failure へ追加しない。
- terminal persistence のために絶対 deadline 内の reserve / cutoff を明示し、完了 stream とその時点までの diagnostics を未完了 stream から独立して保存する。deadline 後の書込み許可は採用しない。
- post-start fault injection は test-only guarded seam から actual `Invoke-MonitoredCommand` owner を通し、通常 runner から到達不能にする。
- fault injection seam は既定無効の内部 parameter / guard と deterministic signal を使い、通常 CLI routeや単なる環境変数だけでは有効化できない構造にする。
- final diagnostic flush は terminal operations とscope seal後に最後のI/Oとして一度だけ行う。cutoff前に開始したflushのfailureはowner secondary diagnosticとして返しprimaryを置換しない。cutoff後は開始せず、`terminal-diagnostic-flush` skipをresultのsecondary diagnosticに残す。sink自身が失敗した場合はartifactではなくresult/caller failure reportingを正本とし、既存artifactは空書きしない。
- 既存 canonical fixtureを `extend` し、新 fixture / lane / DNP は追加しない。

## Unit 1: Close lifecycle persistence, observation ownership, and post-start cleanup

Owner: `implementation-worker`（single sequential owner）

Writable paths:

- `scripts/verification-process-lifecycle.ps1`
- `scripts/verify-refactor.ps1`
- `scripts/test-fixtures/verification-process-lifecycle-probe.ps1`
- 必要な既存 `scripts/test-fixtures/` 内 fixture 1件まで
- `BeMusicSeeker.Tests/VerificationProcessLifecycleTests.cs`
- `devdocs/spec/testing-strategy.md`
- `devdocs/plan/README.md`
- 本計画書

### Observable outcomes

1. 各 stream は独立に完了状態と値を snapshot され、他方が timeout しても完了側の log が UTF-8 で保存される。
2. stream / terminal task observation は一意な lifecycle scope だけへ publishされる。scope seal 後の fault は観測されるが、後続 lifecycle の secondary diagnostics、artifact、failureへ入らない。
3. stdout / stderr persistence、reader / process disposalをterminal-operation cutoff前に完了またはskip確定し、scopeをsealして最後にdrainする。final diagnostic flush reserve内に確定snapshotを一度だけ書き、write / dispose待機中に生成された diagnosticもartifactまたはresultへ帰属する。
4. process start 後の例外は retained handle、PID、取得済み identity、stream taskを持つ同じ cleanup ownerへ合流する。exact owned root/descendantをboundedに停止し、元例外をprimaryとして維持する。
5. outer catch は既存 artifact を空文字で上書きしない。completed outputを保存し、未取得側とcleanup failureをcontext付きsecondary diagnosticにする。
6. `testing-strategy.md` に lifecycle fixture / ProcessIntegration lane / shared process resource / completion signalの Verification mapを記録し、完了済み旧計画をREADMEの現在進行中一覧から外す。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| normal completed outputの実artifact | `Invoke-BoundedProcessLifecycle` persistence | `VerificationProcessLifecycleTests.NormalExitPreservesCompletedStandardOutputAndError` | extend | exact-ledger probe process / `ProcessIntegration` | both stream tasks completed + log files read before probe cleanup | result JSONだけのassert |
| completed stdout + pending stderr と逆方向 | stream drain / terminal persistence | existing stream-timeout case | extend | same | completed task + guarded pending/fault gate + absolute deadline | 両stream同時timeoutだけのcoverage |
| lifecycle Aのlate faultがclean lifecycle Bへ混入しない | lifecycle observation scope | existing late-fault / fanout cases | extend | same PowerShell probe process / `ProcessIntegration` | A return → controlled fault signal → B completion | global queueのmanual drainをowner contractとするroute |
| post-start unexpected exception cleanup | `Invoke-MonitoredCommand` actual caller | existing probe-failure cleanup case | extend | exact-owned process / `ProcessIntegration` | guarded injection signal + exact ledger PID absence | outer harness cleanupだけでrunner残留を隠すcoverage |
| terminal operation中のdiagnosticとfinal flush failure | terminal operation cutoff / `process-lifecycle.log` | existing stream-timeout / cleanup diagnostic cases | extend | same | controlled write/dispose fault or cutoff signal + result/artifact read before cleanup | terminal wait前のlog payload snapshot |
| Functional shared deadlineの非resetとcutoff後primitive禁止 | `Invoke-VerificationFunctionalCleanup` | existing fanout-order / deadline cases | extend | multiple exact-ledger entries / `ProcessIntegration` | primitive event ledger with shared deadline and entry lifecycle IDs | entry単位の自己申告metadataだけのassert |

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'
```

Worker は PowerShell parse、`git diff --check`、focused Quick まで行い、Functional / Full / WPF反復はrootへhandoffする。

### Worker implementation evidence

- Implemented Unit 1 in the shared lifecycle, monitored-command caller, canonical process probe, lifecycle test fixture, testing strategy, and plan index paths listed above.
- Added deterministic asymmetric stream, lifecycle-local late-fault, actual post-start exception, terminal diagnostic / flush failure, and Functional shared-cutoff coverage to `VerificationProcessLifecycleTests` through the existing `ProcessIntegration` probe.
- PowerShell parse: pass for all three changed scripts; `git diff --check`: pass.
- Focused Quick: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'` — pass, 17/17, 65.6s; diagnostics: `artifacts/verification/tests-quick-20260824-201231`.
- Functional, Full, and WPF 30-repeat verification remain integration-owned by root and were not run by this worker.

### Replan triggers

- absolute deadline内のterminal persistence reserveでは既存 Functional cleanupを閉じられず、global cleanup budget / orchestration shapeの変更が必要になる。
- retained root handleからidentity取得前のbounded root-only stopを安全に行えない。
- actual caller fault injectionが通常 runnerから到達不能な明示guardでは構成できない。
- lifecycle-local scopeではdeadline後のfault observationとdurable diagnostic ownershipを両立できず、run-level owner追加が必要になる。

## Unit 2: Rebalance process-local presentation and settings ownership

Owner: `implementation-worker`（Unit 1 ownerとは別turn、single sequential owner）

Writable paths:

- `scripts/verify-refactor.ps1`
- 必要なら `scripts/verification-runner-contract.ps1`
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs`
- `devdocs/spec/testing-strategy.md`
- 本計画書

### Context and decision

直近15件の完了Functionalでも最終shardのprocess-deadline headroomは0.3-14.3秒で、critical shardはremaining 8回、settings 5回、playlist 1回、library 1回と交代していた。`5e7ccaed` の2回のfailureも未完了shardが入れ替わり、lifecycle cleanup前の通常workloadでdeadlineへ到達した。

- `settings-presentation-classwide` の16 fixtureを、foreground interactionを持つ2 fixtureとprocess-local stateを持つ14 fixtureへ分離する。両方1-worker / `ClassLevel` とし、portable settings lane後に他named shardと同時起動する。
- `presentation-workspace` は同じ3 fixture membershipと`ClassLevel`を維持して2-workerへ変更する。class-wide `DoNotParallelize` の `PlaybackPanelViewModelTests` は他classと重ならず、`PlaylistWorkspaceViewModelTests` と `LibraryFolderTreeViewModelTests` だけが並行可能になる。
- `playlist-update` のpre-wave overlapは、27件中25件で理論critical path短縮がなく、pre-waveの明示I/O isolationを崩すため採用しない。
- `BmsLibraryStateApplierTests` のfixture分割は今回行わない。Unit 2後もdeadline failureが残る場合だけ、remaining owner rebalancingとして再計画する。

`settings-state-classwide` のexact 14 fixture:

- `ApplicationCompositionTests`
- `ApplicationSettingsLifecycleTests`
- `ApplicationUiSchedulerBoundaryTests`
- `BeatorajaBmtOptionsSnapshotTests`
- `BmsLibraryOptionsSnapshotTests`
- `CustomFolderOutputSettingsSnapshotTests`
- `MainWindowViewSettingsBoundaryTests`
- `PlayerSettingsGatewayTests`
- `PlaylistUrlCompletionOptionsSnapshotTests`
- `ResourceIconContractTests`
- `SettingDialogCustomFolderOutputBaseTests`
- `SettingDialogOpenCommandTests`
- `ShellShutdownWorkflowOwnerTests`
- `StartupSettingsSnapshotTests`

2つのsettings testhost間で `Application`、dispatcher、resources、generated resource culture、`Settings.Default` static instanceはprocess-localである。対象16 fixtureに実portable `Settings.Save` / `Reload` / `Reset` はなく、filesystem書込みはGUID-owned temp rootを使う。`SettingDialogCustomFolderOutputBaseTests` の`config.Save()`はGUID付きtemporary LR2 configだけを対象にする。foreground routeは2 fixtureの既存6 methodだけで、14 fixture側はforeground windowを持たない。このisolationが崩れる差分を見つけた場合は2 host同時起動を実装せずreplanする。

### Observable outcomes

1. `settings-presentation-classwide` は `SettingDialogEditCompletionTests` と `SettingsWindowPresentationTests` のexact 2 fixtureだけを1-worker / `ClassLevel`で実行する。
2. 新しい `settings-state-classwide` は残るexact 14 fixtureだけを1-worker / `ClassLevel`で実行する。
3. `presentation-workspace` は既存exact 3 fixtureを2-worker / `ClassLevel`で実行し、`PlaybackPanelViewModelTests` のDNP safetyを維持する。
4. runnerが実際に起動へ使う shard object / allowlistを正本として、exact membership、worker、scope、remaining exclusion、cross-route uniqueness、重複起動なしをlaunch前に検証し、検証済みの同じobjectをlaunchへ渡す。descriptor-only / 自己申告metadataだけをgreenにするrouteは追加しない。
5. Functionalのlogical test set、process deadline、10秒cleanup reserve、remaining worker数、pre-wave順序、WPF foreground allowlistは変更しない。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| settings foreground/state process ownership | actual Functional shard descriptors and `Assert-FunctionalShardConfiguration` | existing `VerificationRunnerContractTests` + actual Functional | extend | two process-local WPF/settings testhosts | launch-time exact validator + exact TRX completion + host exit/HWND cleanup | 16 fixtureを1 testhostへ直列化する旧allowlist |
| presentation workspace parallel ownership | same | existing `VerificationRunnerContractTests` + actual Functional | extend | one testhost, 2 workers, ClassLevel; Playback class DNP | launch-time validator + exact TRX completion; Playback intervalは他2 classと非重複 | generic 1-worker assumption |

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests'
```

WorkerはPowerShell parse、`git diff --check`、focused Quickまで行う。Functionalはrootが同一最終snapshotで3回連続実行する。

### Replan triggers

- 対象14 fixtureに実portable-settings保存、固定共有path書込み、またはforeground routeが見つかる。
- `PlaybackPanelViewModelTests` がMSTest DNPでも他2 classと実際に重なる。
- Functionalでactivation、HWND cleanup、culture/theme、user.config、shared settings failureが1回でも発生する。
- Functionalが1回でもprocess deadlineへ再到達する。この場合はtimeout延長やworker追加を継ぎ足さず、remaining owner fixture分割を別unitとして再計画する。

## Integration and final review

Unit 1をcommit後、同一最終snapshotで次を実行する。途中でfailureを修正した場合は、該当stability gateを1回目から数え直す。

1. PowerShell parse、`git diff --check`、Release build、runner hash。
2. lifecycle focused Quick。
3. 旧計画指定のWPF focused filterを30回。各回でtimeout、tracked file変更、残留test processがないことを確認する。
4. Functionalを3回連続。各command 180秒以内、tracked fingerprint不変、残留test process 0。
5. Fullを1回。
6. implementation threadを閉じ、snapshotを凍結してfresh `repo-static-review`を呼ぶ。

Reviewer は asymmetric persistence、scope seal後のfault、actual post-start caller exception、artifact非破壊、primary/secondary precedence、deadline後のprimitive開始、test自己検証を重点確認する。

## Progress

| Unit | Status | Notes |
| --- | --- | --- |
| Unit 1: lifecycle P2 closure | Complete | `5e7ccaed`。focused Quick 17/17、PowerShell parse、`git diff --check`、Release build成功。 |
| Unit 2: Functional headroom replan | Implementation complete; stability verification pending | base HEAD `128519856d57b304bc21930843b1aecfc10eeaa4` から、settings 16 fixtureをforeground 2/state 14の2 testhostへ分離し、presentation workspaceを2-worker/ClassLevel化。実 launch plan objectを同一 validatorへ渡し、exact membership、worker/scope、remaining exclusion、cross-route uniqueness、fanout object identityを検証する。PowerShell parse / `git diff --check` pass。focused Quick `VerificationRunnerContractTests` 4/4 pass、33.5s command、diagnostics `artifacts/verification/tests-quick-20260824-213148`。Functional/Full/WPF30はroot担当。playlist overlapとremaining fixture分割は採用しない。 |
| Unit 3: final stability gates and review | Pending | Unit 2後snapshotでWPF 30回、Functional 3回、Full 1回を最初から実行する。 |

## Verification log

| Snapshot | Command / filter | Result | Elapsed | Artifact / evidence |
| --- | --- | --- | ---: | --- |
| `5e7ccaed` | lifecycle focused Quick | Pass (17/17) | 74.2s command / 51.5s test | `tests-quick-20260824-201559`; fingerprint unchanged; residual test process 0 |
| same | WPF focused filter, 30 consecutive runs | Pass (30/30) | 31.6-33.9s / run | `tests-quick-20260824-201725` through `tests-quick-20260824-203307`; residual test process 0 |
| same | Functional first run | Fail: process deadline | 176.5s command; canonical 174.6s | `tests-functional-20260824-203352`; remaining / playlist-update / settings-presentation-classwide stopped; fingerprint unchanged; residual 0 |
| same | Functional exact retry | Fail: process deadline | 176.2s command; canonical 174.0s | `tests-functional-20260824-203720`; remaining / playlist-update / presentation-workspace stopped; fingerprint unchanged; residual 0; repeated-timeout investigation threshold met |
| Unit 2 snapshot | PowerShell parse + `git diff --check` | Pass | <1s | runner script parse clean; whitespace clean |
| Unit 2 snapshot | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests'` | Pass (4/4) | 33.5s command / 6.3s test | `tests-quick-20260824-213148`; actual launch plan validator and contract tests passed; no residual testhost process |

## Done when

- 4件のacceptance-direct P2がbehavior testとdurable specを伴って解消される。
- completed stream artifactとlifecycle diagnosticが他streamのtimeoutに失われない。
- late faultが別lifecycleへ混入せず、元ownerのtimeout diagnosticとfault observation contractが明確である。
- post-start exception後のexact-owned PID residualが0で、既存artifactとprimary failureが維持される。
- terminal operation中に確定したdiagnosticが成功したfinal logに含まれ、final flush失敗時はresult/callerのsecondary failureとして観測される。
- WPF 30回、Functional 3回、Full 1回が同一最終snapshotで成功する。
- fresh static reviewでP0/P1とacceptanceへ直接反するP2がない。
- verification evidence、review result、最終commitを本計画へ記録し、StatusをCompleteにする。
