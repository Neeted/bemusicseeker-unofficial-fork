# テスト整理完了後レビュー P2 修正計画

Status: Unit 4b staged fanout replan implementation complete; stability verification pending

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
- `BmsLibraryStateApplierTests` のfixture分割はUnit 2の初期案では行わない。Unit 2後もdeadline failureが残ったため、remaining owner rebalancingをUnit 3として再計画する。

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

## Unit 3: Rebalance remaining owner fixtures

Owner: `implementation-worker`

Writable paths:

- `BeMusicSeeker.Tests/BmsLibraryStateApplierTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryPackageLifecycleTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryCatalogRelocationTests.cs`
- `devdocs/spec/library-mutation-boundary.md`
- `devdocs/spec/testing-strategy.md`
- 本計画書

### Context and decision

Unit 2後のFunctional retryは `7ccca8eaa590cb8869b3eb272f0589c32bd60ff9` (`7ccca8ea`) で実行し、artifactは `artifacts/verification/tests-functional-20260824-213728`。shared process deadlineへ到達したため `remaining`、`playlist-update`、`presentation-workspace` はTRXを生成せず停止した。一方、`settings-presentation-classwide` は142/142、`settings-state-classwide` は119/119で完了した。tracked file fingerprintは不変で、testhost / dotnet residualは0だった。

remainingの26-case `BmsLibraryStateApplierTests` は、ownerごとのClassLevel schedulingを得るため次の3 fixtureへ分ける。既存の12-worker `remaining` process、ClassLevel scope、除外filter、10秒cleanup reserve、timeout、logical test setは変更しない。

- `BmsLibraryStateApplierTests`: library initialization progress と `ApplyLibraryMutationDelta(...)` の13 case。
- `BmsLibraryPackageLifecycleTests`: pending package collection publication と durable pending-package delta の7 case。
- `BmsLibraryCatalogRelocationTests`: catalog relocationのstorage-row、path、LR2 compatibilityの6 case。

共通helperは `BmsLibraryStateApplierTestSupport` にまとめ、GUID付きtemporary song DB、filesystem cleanup、callback、scheduler、cancellation / completion signalを既存 semanticsのまま維持する。新しいnamed shard/process、worker増加、DNP、fallback、completion推測用のsleepは追加しない。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal | Retired test / route |
| --- | --- | --- | --- | --- | --- | --- |
| library initialization progress と library mutation の永続化・package pruning・BMSON owner contract | `BMSLibrary` / `PackageStateMutationApplier.ApplyLibraryMutationDelta(...)` | 旧 `BmsLibraryStateApplierTests` のinit 1 + mutation 12 | `replace` fixture container; exact 13 method bodiesを保持 | existing `remaining` process, 12 workers, `ClassLevel`; per-test GUID DB/root | existing scheduler queue, callback counters, synchronous receipt and cleanup | 旧26-case single class route → `BmsLibraryStateApplierTests` 13 cases |
| pending package publication、durable deletion、subscriber failure / cancellation | `PackageLifecycleOwner` / `PackageStateMutationApplier.ApplyPendingPackageMutationDelta(...)` | 旧 fixtureのpackage/pending 7 | `replace` fixture container; method bodies unchanged | same `remaining` process and ClassLevel; no new shared state | existing `ManualResetEventSlim`, accepted-operation `Task` and exception callback | old methods → `BmsLibraryPackageLifecycleTests` 7 cases |
| catalog relocation storage rows、path replacement、LR2 facts and failure rollback | `CatalogMutationOwner.ApplyCatalogMutation(...)` + state applier package projection | 旧 fixtureのcatalog relocation 6 | `replace` fixture container; method bodies unchanged | same `remaining` process and ClassLevel; GUID filesystem/database | existing catalog receipt and callback failure facts | old methods → `BmsLibraryCatalogRelocationTests` 6 cases |

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryStateApplierTests|FullyQualifiedName~BmsLibraryPackageLifecycleTests|FullyQualifiedName~BmsLibraryCatalogRelocationTests'
```

Workerは上記fixture filterのQuick、PowerShell parse、`git diff --check`を実行する。Functional / Full、および同じ条件での最終Functional 3回はrootの統合検証とする。

### Replan triggers

- 3 fixtureを同じremaining processで起動してもClassLevel schedulingがfixture間で行われず、splitによるcritical path短縮がない。
- 既存method bodyの移動だけではcompile / test semanticsを維持できず、production fallback、runner topology、worker数、timeout、DNPまたは所有path外の変更が必要になる。
- GUID resource cleanup、completion / cancellation signal、failure precedenceのいずれかを変更しないとfixtureを分離できない。

## Unit 4: critical-tail owner topology

### Context and decision

Unit 3後の `4097e3a3` では `remaining` が1869/1869でprocess deadlineの約2秒前に完了し、旧58.1秒の `BmsLibraryStateApplierTests` は3 fixtureの最大10.4秒まで短縮された。一方、旧 `playlist-update`、`presentation-workspace`、`settings-presentation-classwide` がdeadline時点で残った。3 routeの過去成功時のtailはそれぞれ約57.3秒、53.5秒、60.6秒であるため、1 routeだけの局所修正では別routeがcriticalになる。次の3 ownership変更を同じreviewable unitで閉じ、既存の180秒command budget、170秒process deadline、10秒cleanup reserve、pre-wave順序、remaining 12 workers、logical test setを維持する。

1. Settingsはexact 7 foreground methodを新しい `SettingsForegroundInteractionTests` へ集約する。foreground fixtureとnon-foreground `SettingDialogEditCompletionTests` を1-workerの `settings-edit-foreground-classwide` で実行し、non-foreground `SettingsWindowPresentationTests` は別の1-worker `settings-window-nonactivating-classwide` で実行する。foregroundを取得するprocessはexact 1つとし、複数foreground process案は退役する。
2. 旧95-case `BmsPlaylistUpdateTests` は external reload、persistence lifecycle、custom-folder output、migration / registration の4 owner fixtureへ分割し、各fixtureを別の1-worker / `ClassLevel` processへ割り当てる。各classは既存のclass-wide DNP、process-local `Settings.Default`、GUID temporary DB/files/outputを維持する。`Settings.Default.Save()` または共有portable config書込みが見つかった場合は並列化しない。
3. 旧147-case `PlaylistWorkspaceViewModelTests` は external source、action workflow、detail refresh、presentation state、persistence command の5 owner fixtureへ分割する。既存 `presentation-workspace` testhost内で `LibraryFolderTreeViewModelTests` とともに3-worker / `ClassLevel` で実行し、`PlaybackPanelViewModelTests` のclass-wide DNP exclusive phaseは維持する。新しいprocessは追加しない。

期待critical tailは約60.6秒から39-43秒で、17秒以上のheadroomを得る。production code、fallback、timeout、固定wait、追加DNP、pre-wave overlapは変更しない。

### Ownership and handoff

- Worker A owns `SettingDialogEditCompletionTests.cs`、`SettingsWindowPresentationTests.cs`、旧 `BmsPlaylistUpdateTests.cs` と新しいsettings / BMS playlist fixture/support files。既存method body、assertion、completion signal、cleanupを一度だけ移動し、共通support変更が必要なら停止してrootへ返す。
- Worker B owns `PlaylistWorkspaceViewModelTests.cs` と新しいworkspace fixture/support files。同じ巨大fileをWorker Aは編集しない。
- 両worker完了後、integration workerが `scripts/verify-refactor.ps1`、`VerificationRunnerContractTests.cs`、`testing-strategy.md`、feature Verification map、本計画を所有する。実launch objectのexact membership、worker/scope、remaining exclusion、cross-route uniqueness、foreground exact allowlistを検証する。

### Test delta

| Behavior / failure contract | Coverage | Decision | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- |
| settings foreground / nonactivating presentation | 旧2 fixtureの全case + exact 7 foreground methods | `replace` fixture containers; `SettingsForegroundInteractionTests` + non-FG `SettingDialogEditCompletionTests` in `settings-edit-foreground-classwide`, non-FG `SettingsWindowPresentationTests` in `settings-window-nonactivating-classwide` | two 1-worker `ClassLevel` testhosts; process-local Application/dispatcher/resources/settings | existing dispatcher host、window scope、task/event completion、HWND cleanup | `settings-presentation-classwide` single host |
| playlist reload / persistence / output / migration | 旧 `BmsPlaylistUpdateTests` 95 methods | `replace` with `BmsPlaylistExternalReloadTests`、`BmsPlaylistPersistenceLifecycleTests`、`BmsPlaylistCustomFolderOutputTests`、`BmsPlaylistMigrationAndRegistrationTests` in two grouped routes | two 1-worker `ClassLevel` processes; existing DNP; process-local settings; GUID DB/files | existing task/event/DB commit and visible-publication receipts | four singleton playlist routes / `playlist-update` single-class route |
| playlist workspace owner boundaries | 旧 `PlaylistWorkspaceViewModelTests` 147 methods | `replace` with `PlaylistWorkspaceExternalSourceTests`、`PlaylistWorkspaceActionWorkflowTests`、`PlaylistWorkspaceDetailRefreshTests`、`PlaylistWorkspacePresentationStateTests`、`PlaylistWorkspacePersistenceCommandTests` | existing `presentation-workspace`, 3 workers/ClassLevel; Playback DNP exclusive | existing dispatcher host、TCS/event/barrier、GUID files | monolithic workspace class / 2-worker assumption |
| actual launch topology | `VerificationRunnerContractTests` + Functional | `extend` actual-consumed plan validator | no metadata-only route | launch validator + exact TRX/host exit | old settings/playlist/workspace allowlists |

### Focused verification

- Worker A: new settings fixture FQNs and four BMS playlist fixture FQNs.
- Worker B: five workspace fixture FQNs plus `LibraryFolderTreeViewModelTests` and `PlaybackPanelViewModelTests`.
- Integration: `VerificationRunnerContractTests`、PowerShell parse、`git diff --check`。Focused command: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests'`。
- Final snapshot: WPF focused 30回とFunctional 3回を1回目から再実行し、Fullを1回実行する。

### Replan triggers

- method移動にshared mutable state、portable `user.config` write、cross-fixture cleanup dependency、意味の変わるhelper抽出が必要になる。
- nonactivating settings hostにforeground methodまたはunprepared activation routeが残る。
- Functionalのいずれかでprocess deadlineまで15秒未満、timeout、deterministic failure、残留process/HWND、tracked file変更が生じる。
- 追加processのSQLite / I/O contentionで別routeがcriticalになる。この場合もtimeout延長やworker削減で隠さずownershipを再調査する。

## Unit 4b: staged settings and bounded fanout

### Trigger evidence

`018b894b` の最初のFunctionalは `artifacts/verification/tests-functional-20260824-232820` でdeadlineへ到達した。16 fanout processを0.36秒で起動し、実行中LR2を含む17 shard / worker ceiling 33となった直後、LR2の通常0.45秒のcaseが52秒、通常0.05秒前後のcaseが16-17秒へ膨張した。fanoutは3519件中593件しか完了せず、`remaining` は約97秒でtest-run headerにも到達しなかった。これはfixture固有tailではなくprocess / CPU / I/O contentionであるため、同じ17-process topologyは再試行しない。

また、`settings-window-nonactivating-classwide` の `SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow` はproduction `ThemedMessageBox.ShowDialog()` で未準備のnested modalをactivateする。現在のexact 6 foreground allowlistでは別processのkeyboard/focus testと競合するため、このmethodをforeground ownerへ移し、nonactivating routeの契約を実態へ合わせる。

### Design

1. `SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow` のbehavior body、assertion、completion、cleanupを `SettingsForegroundInteractionTests` へ移し、foreground / activating allowlistをexact 7 methodにする。旧FQNは退役し、`SettingsWindowPresentationTests` にはactivating routeを残さない。
2. BMS playlist 4 fixtureは次の2 processへbalanced groupingする。各processは1 worker / `ClassLevel`、各classのDNP、process-local settings、GUID DB/filesを維持する。
   - `playlist-external-custom-folder`: `BmsPlaylistExternalReloadTests` + `BmsPlaylistCustomFolderOutputTests`（isolated span合計約11.2秒）。
   - `playlist-persistence-migration`: `BmsPlaylistPersistenceLifecycleTests` + `BmsPlaylistMigrationAndRegistrationTests`（同約8.7秒）。
3. exclusive portable-settings lane完了後、LR2、`settings-edit-foreground-classwide`、`settings-window-nonactivating-classwide` をexact 1回ずつ起動してentryを直ちに記録し、その後にmethod-level pre-waveを実行する。pre-wave fixtureはHWNDを作らず、settings / culture / resources / DB / filesystemはprocess-localまたはGUID ownershipである。settings routeはfanoutから除外し、同じprocess deadline、result、diagnostics、cleanup setへjoinする。
4. pre-wave後のearly entry状態遷移を次に固定する。
   - completed / exit 0: resultを一度だけaccountし、再起動しない。
   - running: 同一entry / process identityをcommon monitorへ渡し、再起動しない。
   - nonzero / canceled / invalid: fanoutを開始せずprimary failureとして失敗し、記録済み全entryをbounded cleanupする。
   - process start後entry記録前、またはN件起動後N+1件目のlaunch exception: raw processをexact identityで回収し、記録済みentryとともにcleanupする。primary / secondary precedenceを維持する。
5. actual shard object identity、early membership、fanout exclusion、no relaunch、unique accounting、partial-launch cleanupをbehavior validatorへ追加する。最終構成は15 launch shards / 14 fanout descriptorsで、4 BMS fixtureはexact 1 route、foregroundはexact 7 methodsとする。

180秒command、170秒process deadline、10秒cleanup reserve、remaining 12 workers、presentation 3 workers、logical test set、固定waitなし、新規DNPなし、production behavior不変を維持する。

### Test delta and verification

| Contract | Coverage | Decision | Completion / failure signal | Retired route |
| --- | --- | --- | --- | --- |
| nested activating settings modal | moved ManualResync method + exact foreground allowlist | `replace`; exact 6 → 7 | existing owned window/modal receipt and cleanup | old SettingsWindow FQN / false nonactivating ownership |
| balanced playlist process ownership | four existing owner fixtures | `replace`; four singleton process routes → two exact grouped routes | class completion / process exit / GUID cleanup | four-process topology |
| early settings/LR2 lifecycle | actual launch plan and lifecycle probe | `extend` | process identity、exit/stream tasks、primitive ledger、shared deadline | LR2-only early ownership / settings fanout relaunch path |

Focused Quickはsettings 3 fixture、4 BMS fixture、`VerificationRunnerContractTests`、`VerificationProcessLifecycleTests` を実行する。最終snapshotでFunctional 3回、WPF focused 30回、Full 1回を最初から実行する。

### Unit 4b implementation evidence

- SettingsWindowのManualResync behavior body、assertion、completion、window cleanupを `SettingsForegroundInteractionTests` へ移動し、foreground allowlistをexact 7 methodへ更新した。旧 `SettingsWindowPresentationTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow` FQNとnonactivating routeのactivating ownershipは退役。
- BMS playlist descriptorを `playlist-external-custom-folder`（external reload + custom-folder output）と `playlist-persistence-migration`（persistence lifecycle + migration / registration）の2つへgroup化した。4 fixtureのclass-wide DNP、process-local settings、GUID-owned DB/files、logical test setは維持。
- `New-FunctionalShardPlan` は15 launch shards / 14 retained fanout descriptors / 12 actual fanout launchesを返し、LR2とsettingsの3 early descriptorを同じobject ledgerへ渡す。exclusive lane後に3 entryをexact 1回起動・即時記録し、pre-wave後のcompleted/running/nonzero/canceled/invalid stateを共通accounting・deadline・diagnostics・cleanupへ接続する。raw process ownershipはentry構築例外にも保持し、primary failureをcleanup failureで置換しない。
- Validator coverageは実際のrunner plan objectとobject identityを対象にextendし、exact grouped membership、remaining exclusion、cross-route uniqueness、early membership、settings fanout exclusion、foreground exact 7を確認する。PowerShell parse、`git diff --check`、focused Quickの結果は下記のVerification logへ追記する。

### Replan triggers

- settings nonactivating routeに別のnested activating HWNDが見つかる。
- early process start後のraw identityをentry記録前exceptionからexact cleanupできない。
- grouped Quickでcross-class settings/static/resource leakageが出る。
- Functionalでstartup delay、LR2のfanout同期膨張、15秒未満のdeadline headroom、timeout、残留process/HWND、tracked mutationが一度でも出る。

## Unit 5: final stability gates and review

Unit 4bをcommit後、同一最終snapshotで次を実行する。途中でfailureを修正した場合は、該当stability gateを1回目から数え直す。

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
| Unit 3: remaining owner fixture rebalancing | Complete; integration stability pending | `7ccca8ea` / `tests-functional-20260824-213728` のprocess deadline failureを受け、26 caseをlibrary init/mutation 13、package lifecycle/pending 7、catalog relocation 6へ分割。既存remaining process、12-worker ClassLevel、GUID resource、completion signalは維持。focused Quick 26/26 pass、Functional/Full/WPF30はroot担当。 |
| Unit 4: critical-tail owner topology | Implementation and focused verification complete; stability pending | `4097e3a3` でremainingは完走したがplaylist / presentation / settingsがdeadlineへ残ったため、4つのBMS playlist owner shard、7-class / 3-worker workspace shard、foreground / nonactivating / stateの3 settings shardへ再編した。実際のlaunch plan objectをvalidatorへ渡し、exact membership、worker/scope、remaining exclusion、cross-route uniqueness、旧FQN不在、foreground exact allowlist、fanout object identityを検証する。180秒command、170秒process deadline、10秒cleanup reserve、pre-wave順、remaining 12 workers、DNP、logical test setは維持。settings 142/142、workspace 208/208、BMS playlist + runner contract 99/99 pass。Functional / Full / static reviewはUnit 5で実施する。 |
| Unit 4b: staged settings and bounded fanout | Implementation complete; stability verification pending | `018b894b` の17-shard contention failureとnested activating modalの誤分類を受け、foreground exact 7、playlist 2 grouped process、LR2 + settings early ownership、partial-launch cleanupへ再計画。実際のlaunch object validator、early state/accounting、raw process ownership cleanupを実装。Focused Quick / Functional / WPF30 / Fullの最終安定性確認はUnit 5で実施する。 |
| Unit 5: final stability gates and review | Pending | Unit 4b後snapshotでWPF 30回、Functional 3回、Full 1回を最初から実行する。 |

## Verification log

| Snapshot | Command / filter | Result | Elapsed | Artifact / evidence |
| --- | --- | --- | ---: | --- |
| `5e7ccaed` | lifecycle focused Quick | Pass (17/17) | 74.2s command / 51.5s test | `tests-quick-20260824-201559`; fingerprint unchanged; residual test process 0 |
| same | WPF focused filter, 30 consecutive runs | Pass (30/30) | 31.6-33.9s / run | `tests-quick-20260824-201725` through `tests-quick-20260824-203307`; residual test process 0 |
| same | Functional first run | Fail: process deadline | 176.5s command; canonical 174.6s | `tests-functional-20260824-203352`; remaining / playlist-update / settings-presentation-classwide stopped; fingerprint unchanged; residual 0 |
| same | Functional exact retry | Fail: process deadline | 176.2s command; canonical 174.0s | `tests-functional-20260824-203720`; remaining / playlist-update / presentation-workspace stopped; fingerprint unchanged; residual 0; repeated-timeout investigation threshold met |
| Unit 2 snapshot | PowerShell parse + `git diff --check` | Pass | <1s | runner script parse clean; whitespace clean |
| Unit 2 snapshot | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests'` | Pass (4/4) | 33.5s command / 6.3s test | `tests-quick-20260824-213148`; actual launch plan validator and contract tests passed; no residual testhost process |
| `7ccca8ea` / Unit 2 retry | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional` | Fail: shared process deadline | 176.5s command | `tests-functional-20260824-213728`; settings-presentation-classwide 142/142 and settings-state-classwide 119/119 completed; remaining / playlist-update / presentation-workspace stopped without TRX; tracked fingerprint unchanged; residual testhost / dotnet process 0 |
| Unit 3 snapshot | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryStateApplierTests|FullyQualifiedName~BmsLibraryPackageLifecycleTests|FullyQualifiedName~BmsLibraryCatalogRelocationTests'` | Pass (26/26) | 29.3s command / 2.8s test | `tests-quick-20260824-215349`; build succeeded; 12 workers / `ClassLevel` observed; tracked fingerprint unchanged; no residual test process |
| `4097e3a3` | Release build + lifecycle/runner focused Quick | Pass (18/18) | 22.4s build / 70.7s Quick | `tests-quick-20260824-215800`; 0 errors; runner `F434C6C3...15DFF0`; lifecycle `87D0E075...A28997C`; tracked unchanged; residual 0 |
| same | WPF focused filter, 30 consecutive runs | Pass (30/30) | 31.3-34.6s / run | `tests-quick-20260824-215928` through `tests-quick-20260824-221507`; all residual 0; tracked unchanged |
| same | Functional first run | Fail: shared process deadline | 173.4s command / 171.8s canonical / 155.4s test phase | `tests-functional-20260824-221554`; remaining 1869/1869 completed; playlist-update 87/95、presentation 205/208、settings-presentation 136/142 at cutoff; tracked unchanged; residual 0; Unit 4 replan trigger met |
| Unit 4 integration worktree | PowerShell parse + direct `New-FunctionalShardPlan` / `Assert-FunctionalShardConfiguration` probe + `git diff --check` | Pass | <1s parse/check; 3.2s plan probe | 17 launch shards / 16 fanout objects; exact final fixture names, worker/scope, remaining exclusions, old-FQN absence、foreground allowlist、fanout object identity passed; tracked files unchanged |
| same | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests'` | Blocked by deterministic compile errors in worker-owned fixture | 25.3s command; no testhost residual | `tests-quick-20260824-230949`; `SettingsForegroundInteractionTests.cs(751,752,770,773)` cannot resolve `TestSettingsDialogPlayerFactoryPort` / `TestSettingsDialogPlaybackRuntimePort`; fixture files are outside Unit 4 integration ownership and were not edited |
| Unit 4 fixed worktree | settings three-fixture focused Quick | Pass (142/142) | 60.1s build/test | `tests-quick-20260824-231857`; narrow foreground-owned test doubles fixed the compile errors; tests passed; no residual test process |
| same | workspace five-fixture + LibraryFolderTree + Playback focused Quick | Pass (208/208) | 20.8s test | `tests-quick-20260824-232355`; first identical test run also passed but whitespace guard found EOF formatting, then formatting-only fix and exact rerun passed; residual 0 |
| same | four BMS playlist fixture + `VerificationRunnerContractTests` focused Quick | Pass (99/99) | 47.6s command / 26.7s test | `tests-quick-20260824-232556`; runner exit 0 and tracked fingerprint unchanged; residual 0. Root summary wrapper alone returned failure after success because its dirty-status array comparison produced `System.Object[]`; test/runner evidence is green |
| `018b894b` | Functional first run | Fail: 17-shard contention / shared process deadline | 180.0s command / 178.1s canonical / 144.8s test phase | `tests-functional-20260824-232820`; 593/3519 fanout results; LR2 case latency 100-300x after fanout; most shards no TRX; tracked unchanged; residual 0 after outer check; Unit 4b replan trigger met |
| Unit 4b snapshot | PowerShell parse + direct shard-plan validator + `functional-early-entry` lifecycle probe + `git diff --check` | Pass | 5.9s probe; parse/check clean | 15 launch shards / 14 retained fanout descriptors / 12 actual fanout launches; 3 early entries started once, exit0/running accounted once, nonzero blocked fanout, raw record promoted once, exact cleanup residual 0 |
| Unit 4b snapshot | requested focused Quick: settings, four BMS playlist fixtures, `VerificationRunnerContractTests`, `VerificationProcessLifecycleTests` | Pass (256/256) | 146.6s command / 138.4s filtered build/test | `tests-quick-20260825-001640`; tracked fingerprint unchanged; residual test process 0; includes `functional-early-entry` behavior coverage |

## Done when

- 4件のacceptance-direct P2がbehavior testとdurable specを伴って解消される。
- completed stream artifactとlifecycle diagnosticが他streamのtimeoutに失われない。
- late faultが別lifecycleへ混入せず、元ownerのtimeout diagnosticとfault observation contractが明確である。
- post-start exception後のexact-owned PID residualが0で、既存artifactとprimary failureが維持される。
- terminal operation中に確定したdiagnosticが成功したfinal logに含まれ、final flush失敗時はresult/callerのsecondary failureとして観測される。
- WPF 30回、Functional 3回、Full 1回が同一最終snapshotで成功する。
- fresh static reviewでP0/P1とacceptanceへ直接反するP2がない。
- verification evidence、review result、最終commitを本計画へ記録し、StatusをCompleteにする。
