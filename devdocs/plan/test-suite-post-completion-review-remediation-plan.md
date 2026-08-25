# テスト整理完了後レビュー P2 修正計画

Status: Complete (`07e174ea` correction snapshot reviewed with no blocking findings; historical record)

Review base: `30d25e792ec4b58c552db7651e8d615fe54c11d1`

Plan date: 2026-08-24

> **Archive note:** この文書は完了時 snapshot の実行計画と検証証跡である。現行の lane / timeout / retry は `../spec/testing-strategy.md`、test implementation は `../spec/test-authoring-contract.md`、agent workflow は `../spec/codex-agent-workflow.md` を正とする。以下の Unit、反復回数、当時の gate は履歴として読む。

## Goal

完了済みの runner lifecycle 改善に残った4件の acceptance-direct P2 を、既存の絶対 cleanup deadline、Functional test execution全体の180秒 budget、runner-owned identity、primary failure precedence を維持したまま閉じる。

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
- implementation routeでは fixed sleep、timeout延長、無制限 retry、追加 `DoNotParallelize` を使わない。Functional acceptance の同一条件一回 retry は current policy correction に定めた timeout 時だけの例外とする。

## Decision list

- 4件はすべて現行仕様へ直接反する P2 であり、修正対象とする。
- lifecycle-local observation owner を正本とし、PID filter付き global queue は採用しない。
- timeout 時の durable owner diagnostic は deadline 内に確定できる `stream-drain-timeout` とする。deadline 後の exact task exception は必ず観測して unobserved fault を防ぐが、別 lifecycle の result / failure へ追加しない。
- terminal persistence のために絶対 deadline 内の reserve / cutoff を明示し、完了 stream とその時点までの diagnostics を未完了 stream から独立して保存する。deadline 後の書込み許可は採用しない。
- post-start fault injection は test-only guarded seam から actual `Invoke-MonitoredCommand` owner を通し、通常 runner から到達不能にする。
- fault injection seam は既定無効の内部 parameter / guard と deterministic signal を使い、通常 CLI routeや単なる環境変数だけでは有効化できない構造にする。
- final diagnostic flush は terminal operations とscope seal後に最後のI/Oとして一度だけ行う。cutoff前に開始したflushのfailureはowner secondary diagnosticとして返しprimaryを置換しない。cutoff後は開始せず、`terminal-diagnostic-flush` skipをresultのsecondary diagnosticに残す。sink自身が失敗した場合はartifactではなくresult/caller failure reportingを正本とし、既存artifactは空書きしない。
- 既存 canonical fixtureを `extend` し、新 fixture / lane / DNP は追加しない。
- Functional の最終 acceptance は一回を原則とし、180秒 timeout 時だけ同じ条件で一回 retry する。二回目の budget 内成功は再発 / 決定的 artifact がない場合に限り一過性 machine load として記録し、それ以外は原因調査へ進む。timeout 以外の deterministic failure は retry しない。
- 180秒は portable testhost 開始直前から全 Functional testhost の実際の `ExitTime` までだけを測定し、startup / restore / build / preflight / postflight / artifact / fingerprint / environment / whitespace を含めない。Functional 3回 gate と WPF repeat gate は現行条件ではない。
- tests / fixtures の physical OS cursor 操作・観測は禁止し、key / routed event、explicit hit、deterministic fake / typed action seam を使う。production のcursor実装は変更せず、退役したtest routeだけを本correctionの対象とする。

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

## Unit 4c: LR2-only early and regular-chart ownership

`1db6ae18` のFunctionalはsource変更直後runとexact retryの両方でdeadlineへ到達した（`tests-functional-20260825-003425` / `003803`）。retryではsettingsを含む大半が完了したが、LR2が通常34秒から116秒へ膨張してfanout全期間と重なった。LR2単独Quick `tests-quick-20260825-005600` は103/103、test 25.9秒であり、fixtureではなくsettings + LR2 + pre-wave同時実行によるcontentionと確定した。

1. early setをLR2 exact 1 routeへ戻し、settings edit/windowはpre-wave後の通常fanoutでexact 1回起動する。foreground exact 7、BMS 2 grouping、15 retained shard / 14 fanout descriptorは維持し、actual fanout launchは14とする。historical LR2-only evidenceではLR2はpre-wave終了2.4秒後、fanout testhost開始20秒以上前に完了している。
2. `RegularChartListOwnerTests` 76 methodを既存remaining process / 12 workers / `ClassLevel`内の5 owner fixtureへexact移動し、old classを退役する。method body、assertion、GUID resource、task/event completion、failure watchdogを維持する。
   - `RegularChartNavigationTests`: methods 1-7。
   - `RegularChartNormalLibraryRefreshTests`: methods 8-10 + 14-19。
   - `RegularChartFolderRenameTests`: methods 11-13。
   - `RegularChartViewBuildAndOrderingTests`: methods 20-45。
   - `RegularChartCommitAndLifecycleTests`: methods 46-76。
3. helper/test doubleはnarrow supportへ一度だけ抽出し、mutable shared state、production seam、DNP、新processを追加しない。単一classのprior span約33.3秒を最大owner約12.6秒へ下げる。

Runner validatorはearly exact LR2、fanout launch exact 14、settings2 routeのfanout exact-once、same object identity、no relaunch、remaining/cross-route uniquenessを検証する。180/170/10 budget、pre-wave I/O isolation、logical test setは不変とする。

### Test delta and verification

| Behavior / failure contract | Candidate existing coverage | Decision | Shared resource / lane / completion signal | Retired test / route |
| --- | --- | --- | --- | --- |
| LR2 early ownership、settings post-pre-wave fanout、account-once / no relaunch | `VerificationRunnerContractTests` + existing lifecycle early-entry probe | `extend`; actual plan counts and exact route membership | same launch objects、common raw ownership / cleanup ledger、global deadline | three-entry early set and settings fanout exclusion |
| navigation and summary transitions | old `RegularChartListOwnerTests` methods 1-7 | `replace`; exact bodies / assertions | existing `remaining`, 12 workers, `ClassLevel`; synchronous presentation / refresh events | old methods 1-7 in monolithic class |
| normal-library refresh ownership and failure | old methods 8-10, 14-19 | `replace`; exact bodies / assertions | same remaining host; existing task/event receipts, catalog gate, failure watchdog | old methods 8-10, 14-19 |
| folder rename mutation / shutdown drain | old methods 11-13 | `replace`; exact bodies / assertions | same remaining host; existing mutation completion / shutdown receipt | old methods 11-13 |
| view build, ordering, virtual cache / prewarm | old methods 20-45 | `replace`; exact bodies / assertions | same remaining host; request leases, cancellation, publication signals | old methods 20-45 |
| commit, nested retirement, presentation notification, disposal lifecycle | old methods 46-76 | `replace`; exact bodies / assertions | same remaining host; terminal receipts, event ordering, failure watchdog | old methods 46-76 |

The five new fixtures are not added to a named route; the existing catch-all `remaining` route discovers them after exact class exclusion. No DNP, new process, broad base, mutable static state, or production seam is introduced.

Focused Quickはnew5 fixture 76件、runner contract、LR2 103件、settings 142件、library 243件、playlist grouped 47件を実行する。最終snapshotでFunctional 3回、WPF30、Full1回を実行する。

Replan triggerは、LR2がpre-wave終了後も長時間running、old/new Regular classの重複・欠落、fanout testhost launch delay 25秒超、deadline headroom 15秒未満、timeout、tracked mutation、residual process/HWNDのいずれかとする。

### Unit 4c implementation evidence

- `New-FunctionalShardPlan` now retains 15 launch shard objects, 14 fanout descriptors, 14 fanout launch objects, and one early LR2 descriptor. Settings edit/window objects remain in the same validated fanout ledger and start once after the pre-wave; generic raw ownership, state inspection, account-once, deadline, diagnostics, and cleanup paths are unchanged.
- The old `RegularChartListOwnerTests` class is retired. Methods 1-76 were moved without method-body or assertion changes into the five owner fixtures listed above. Existing helper/test doubles are in one narrow `RegularChartListOwnerTestSupport` file; no production route, DNP, or process was added.
- Static body identity comparison currently passes 76/76. Focused Quick, PowerShell parse, whitespace, and final plan counts are recorded in the Verification log after execution.

## Unit 4d: close the final ClassLevel owner tails without adding processes

`3fcf07fa` の Functional は source rebuild run と exact retry の両方で process deadline へ到達した（`tests-functional-20260825-012115` / `012440`）。両runとも未完了shardのstderrは空で、stdoutはcutoffまで進行し、retryでは完了件数が前進した。したがって単一testのhangではなく、pre-wave後14 testhostの起動競合と、巨大な `ClassLevel` fixture / catch-all queue tailの組合せである。

retryでは `feature-process-global-state` の非DNP 11 class 393件が完了し、残る138件はexact 5 DNP classだった。`remaining` ではRegularChart 5 fixture 76件、`PlaylistSummaryAggregationTests` 18件、`StartupLibraryConstructionOwnerTests` 6件が両runとも未開始だった。`OwnedChartCollectionStateTests` 105件は初回でもdeadline約2秒前の完了、`ChartInfoMetadataTests` 141件はfocused artifactで約54.6秒の単一class spanである。playlist 2 routeはisolated payloadが各10秒未満であり、追加process分割はstartup contentionを悪化させるため対象外とする。

### Design and dependency order

1. **Chart owner route**: `OwnedChartCollectionStateTests` 105件を projection/index、reference-index mutation、lookup/membership、library mutation delta、installed/overlay lookup、refresh/file-scan、inline digest のowner fixtureへ `replace` 分割する。`PlaylistSummaryAggregationTests` 18件を count/presentation、owned-hash build、mutation/warm、resolve-index のowner fixtureへ `replace` 分割する。Unit 4cのRegularChart 5 fixture 76件は `remaining` から既存 `owned-chart-collection` processへ移す。process数は増やさず、routeはexact 6 workers / `ClassLevel` とする。
2. **Library owner route**: `ChartInfoMetadataTests` 141件を schema/export/import、parser behavior、backfill/storage、inline/hydration、install/failure/retry のowner fixtureへ `replace` 分割する。`BmsLibraryInitializationServiceTests` 108件を load、install、file-scan、LR2/normal-folder、inline-chart-info のowner fixtureへ `replace` 分割する。`StartupLibraryConstructionOwnerTests` 6件は profile success、failure contract、MainWindow typed route のowner fixtureへ分割して `remaining` から既存 `library-chart-classwide` processへ移す。process数は増やさず、routeはexact 6 workers / `ClassLevel` とする。
3. **Feature route integration**: `feature-process-global-state` は実在するclass-wide DNP owner 5 class（installed-only resource overwrite、library file scan pipeline、LR2 play-history schema UI、MainWindow external shell、play-history read model）だけをexact 1 worker / `ClassLevel` で保持する。非DNP 11 classは、上記で100件の重いownerを除いた `remaining` / 12 workers / `ClassLevel` へ戻す。

Unit 4d-BのFunctional exact retryでは、5分割したChartInfo fixtureのうちinline/hydrationとinstall/failure/retryが、同じ10秒watchdogでbackground completionを待ったまま再現性をもって失敗した。各caseのsong DB / file rootはGUID所有で、2 fixture単独45件、async ChartInfo 3 fixtureの6-worker route 63件、14 class route単独249件はいずれも成功したため、DB/path衝突ではなくfanout負荷下でprocess ThreadPool completionを同時に待つ5-way ClassLevel splitが誤ったownership premiseだった。ChartInfoの5 source groupは単一partial `ChartInfoMetadataOwnerTests` fixtureへregroupし、library / startup ownerの並列性とrouteの6 workers / `ClassLevel`を維持する。

### Unit 4d-A chart owner implementation evidence

- `OwnedChartCollectionStateTests` 105件は projection/index 9、reference-index mutation 32、lookup/membership 20、library mutation delta 12、installed/overlay lookup 19、refresh/file-scan 5、inline digest 8 の7 fixtureへ、`PlaylistSummaryAggregationTests` 18件は count/presentation 9、owned-hash build 2、mutation/warm 4、resolve-index 3 の4 fixtureへ、method body/assertion/categoryを保持して `replace` 移動した。source body identity ledgerは Owned 105/105、Playlist 18/18で missing/extra/mismatch 0。旧2 monolith FQNは退役した。
- Unit 4cのRegularChart 5 fixture 76件を同一 `owned-chart-collection` routeへ移し、routeは新11 fixtureを含むexact 16 class、6 workers / `ClassLevel` とした。実際のrunner validatorで15 launch shards、14 fanout descriptors / launches、1 LR2 early entry、remaining exclusion、cross-route uniqueness、旧FQN不在、worker/scopeを確認し、process数・LR2/prewave/180/170/10・DNPは変更していない。
- `pwsh -NoProfile -File .\\scripts\\verify-refactor.ps1 -Mode Quick -TestFilter '<owned chart + playlist summary + RegularChart five + VerificationRunnerContractTests>'` は `tests-quick-20260825-020100` で203/203 pass、exit 0、filtered build/test 23.2s、residual test process 0、tracked fingerprint unchanged。PowerShell parse、Release build、`git diff --check`もpass。

### Unit 4d-C feature route integration evidence

- `feature-process-global-state` は `InstalledOnlyResourceOverwriteValidationTests`、`LibraryFileScanPipelineOwnerTests`、`Lr2PlayHistorySchemaUiTests`、`MainWindowExternalShellTests`、`PlayHistoryReadModelTests` のexact 5 classだけを1 worker / `ClassLevel`で保持する。`AudioContractsTests`、`AudioDeviceTestWorkflowOwnerTests`、`BmsLibraryInstallEstimationServiceTests`、`CatalogMutationOwnerTests`、`ChartListVirtualViewTests`、`InstallDestinationStateOwnerTests`、`Lr2PlayHistorySchemaServiceTests`、`MainWindowViewModelStartupProgressTests`、`PlaylistOperationNotificationOwnerTests`、`PlaylistUrlAcquisitionOwnershipTests`、`PlaylistUrlCompletionTests` のexact 11 classは専用routeとassigned / remaining exclusionから除外し、既存remainingで一度だけdiscoverする。
- runner validatorは実際のplan objectでexact 5/11 membership、1 worker / `ClassLevel`、remaining exclusion、cross-route uniquenessを確認し、15 launch shard / 14 fanout descriptor / 14 fanout launch / 1 LR2 early topologyを維持する。`VerificationRunnerContractTests` は実テスト型の `[TestClass]` / class-wide `[DoNotParallelize]` metadataをreflectionで確認する。
- focused Quickはfeatureのretained 5 + returned 11 fixtureと `VerificationRunnerContractTests` を対象にする。既存のprocess数、pre-wave、180/170/10 budget、logical set、completion / cleanup / watchdog contractは変更しない。

Unit 4d-A、4d-B、4d-Cの順で実装した。各fixture unit後にrunner exact membership、remaining exclusion、cross-route uniqueness、旧/new FQN absence、Verification mapを統合した。shared runner、validator、specは同時編集しない。14 fanout process、LR2-only early、pre-wave、logical test set、180秒command / 170秒process deadline / 10秒cleanup reserve、既存category、method/class DNP、GUID DB/root/file、dispatcher、task/event completion、failure watchdogを維持する。新process、新DNP、fixed wait、timeout延長、worker低下、production seamは追加しない。

### Writable ownership and test delta

| Unit / behavior | Writable paths | Decision / retired route | Shared resource / lane / completion signal |
| --- | --- | --- | --- |
| chart collection / summary / regular chart owners | `OwnedChartCollectionStateTests.cs`、`PlaylistSummaryAggregationTests.cs`、Unit 4cの5 `RegularChart*Tests.cs`、owner別replacementとnarrow support、runner / validator、`testing-strategy.md`、chart / playlist Verification map、本計画 | `replace`; 105 + 18件のmonolithを退役し、Regular 76件をremainingから退役 | existing owned-chart process、6 workers / `ClassLevel`; GUID DB/root、immutable snapshots、task/event receipts、cleanup / watchdog |
| chart metadata / library initialization / startup owners | `ChartInfoMetadataTests.cs`、`BmsLibraryInitializationServiceTests.cs`、`StartupLibraryConstructionOwnerTests.cs`、owner別replacementとnarrow support、runner / validator、`testing-strategy.md`、chart-info / library / startup Verification map、本計画 | `replace`; ChartInfo 5-way fixture splitを単一partial ownerへregroupし、108 + 6件のlibrary/startup owner splitは維持 | existing library process、6 workers / `ClassLevel`; existing category、GUID song DB/files、dispatcher/task/event receipts、cleanup / watchdog |
| actual feature-global safety boundary | runner / validator、`testing-strategy.md`、該当feature Verification map、本計画 | `replace` route membership; nonDNP 11 classの専用1-worker直列化を退役 | exact 5 DNP classだけをexisting feature process / 1 workerへ保持; nonDNP 11 classはremainingへ一度だけdiscover |

各canonical monolithはmethod body、assertion、category、completion/failure semanticsをexactに移動し、旧FQNを削除する。各unitのfocused Quickはnew owner fixture全件と `VerificationRunnerContractTests` を含める。実装workerはmethod ledger、旧→新fixture対応、shared resource、正常completion signal、failure watchdog、anti-pattern scanをhandoffする。

### Replan triggers

- exact method ledgerに欠落・重複・body/assertion/category変更が生じる、またはper-testで隔離されないmutable static/settings/resource ownershipが見つかる。
- process launch数が14から変わる、old/new FQN absence、remaining exclusion、cross-route uniqueness、exact 5/11 feature membershipをvalidatorで閉じられない。
- route単体のowner spanが35秒を超える、fanout startup taxが50秒超のまま15秒headroomを阻む、playlistなど別routeが新たなcritical tailになる。
- focused / Functionalでdeterministic failure、timeout、tracked mutation、residual process/HWND、DNP交差failureが生じる。

最終snapshotではFunctionalを最初から3回連続実行し、各回のportable開始から全Functional testhost完了までのtest executionが180秒以内、tracked fingerprint不変、残留test process 0を必須とする。timeout / failure invocation は execution deadline + 10秒の一つの failure-cleanup cutoff 内で owned process を収束させる。途中修正後は成功回数をリセットする。

## Unit 4e: KISS Functional topology and global-budget watchdog policy

`7875a908` の最終安定性確認中、ユーザー判断により受入条件を再整理した。Functionalでは個別test / fixtureの一時的な遅延をfailureとせず、portable testhostの開始から全Functional testhostの完了までのテスト実行全体が180秒以内に収まることをdeadlock / progress watchdogの正本とする。script startup、restore、build、preflight、artifact保存、fingerprint、環境復元などテスト実行前後の処理はこの180秒へ含めない。短い局所watchdogは、cleanup、外部process / UI、negative lock assertion、timeout自体がobservable contractである場合だけ残す。通常完了はdeterministicなTask / event / state transitionをtimeoutなしで`await`し、test coordinatorが別thread / ThreadPool workの結果を`.Wait`、`.Result`、`WaitOne`、`SpinUntil`で同期blockしない。

静的監査ではbounded waitが487箇所 / 56 files、うち405箇所（83%）が5秒だった。全件の機械的削除や一律延長は行わず、今回false positiveを出したStartupとChartInfoのnormal-completion ownerだけを修正する。process / stream cleanup、PID + creation identity、descendant-only cleanup、primary failure precedence、partial launch raw ownership、external process / dispatcher / lockの局所boundは維持する。

現行15 launch / 14 fanoutは12 logical CPUに最大約40 MSTest workersを重ね、`tests-functional-20260825-030537` ではtesthost開始lag 20.5-48.4秒、LR2 103件が通常約26-33秒から115秒へ膨張した。correctness上別processが必要なのはportable `user.config`を書き換える1 fixtureとvirgin processを要求するBass collectible fixtureである。その他のnamed route、LR2 early、method-level pre-wave、15/14/14/1 object accountingは性能topologyとして退役する。

### Unit 4e-A: four-host bounded fanout

portable exclusive fixture `PlayerPanelStateSettingsCompatibilityTests` を1 worker / `ClassLevel`で単独完了後、次の4 processを待機phaseなしで起動し、Functional test execution開始時刻（portable開始直前）からの一つの execution deadline と、その +10秒の failure-cleanup cutoffへ合流させる。

| Host | Workers / scope | Exact ownership |
| --- | --- | --- |
| `bass-collectible` | 1 / `ClassLevel` | `BassCollectibleLoadContextTests` |
| `serial-state-a` | 1 / `ClassLevel` | foreground/settings/playlist settings/native loggingの下記23 class |
| `serial-state-b` | 1 / `ClassLevel` | LR2/compiled nonactivating WPF/class-wide DNPの下記20 class |
| `remaining` | `Environment.ProcessorCount` / `ClassLevel` | Functional対象からportable、Bass、A、Bを除いた全classをexact 1回 |

`serial-state-a` exact 23:

`SettingsForegroundInteractionTests`、`SettingDialogEditCompletionTests`、`SettingsWindowPresentationTests`、`ApplicationCompositionTests`、`ApplicationSettingsLifecycleTests`、`ApplicationUiSchedulerBoundaryTests`、`BeatorajaBmtOptionsSnapshotTests`、`BmsLibraryOptionsSnapshotTests`、`CustomFolderOutputSettingsSnapshotTests`、`MainWindowViewSettingsBoundaryTests`、`PlayerSettingsGatewayTests`、`PlaylistUrlCompletionOptionsSnapshotTests`、`ResourceIconContractTests`、`SettingDialogCustomFolderOutputBaseTests`、`SettingDialogOpenCommandTests`、`ShellShutdownWorkflowOwnerTests`、`StartupSettingsSnapshotTests`、`BmsPlaylistExternalReloadTests`、`BmsPlaylistCustomFolderOutputTests`、`BmsPlaylistPersistenceLifecycleTests`、`BmsPlaylistMigrationAndRegistrationTests`、`BassNativeRuntimeTests`、`NLogWrapperTests`。

`serial-state-b` exact 20:

`BmsLibraryLr2SongDbSyncTests`、`LoadPlaylistURIDialogTests`、`MainWindowChartPresentationWpfTests`、`MainWindowPackageMaintenanceWpfTests`、`MainWindowPlaybackWpfTests`、`MainWindowPlayHistoryWpfTests`、`MainWindowPlaylistWorkspaceWpfTests`、`MainWindowProgressStatusBarWpfTests`、`MainWindowSelectedChartContextMenuWpfTests`、`MainWindowTreePresentationWpfTests`、`MainWindowViewHostTests`、`SettingsWindowCompiledBehaviorTests`、`UiDialogCoordinatorWpfTests`、`PlaybackPanelViewModelTests`、`InstalledOnlyResourceOverwriteValidationTests`、`LibraryFileScanPipelineOwnerTests`、`Lr2PlayHistorySchemaUiTests`、`MainWindowExternalShellTests`、`PlayHistoryReadModelTests`、`ApplicationStartupCompositionOwnerTests`。

A/Bは、process-global settings / Application / resources / foreground、native / logging、LR2 state、class-wide DNPの安全境界を同一host内で直列化する。残りのDB / file / chart / library / presentation ownerはGUID resourceと既存method-level DNPを維持してremainingへ戻す。現在の validator は portable、Bass、A、B の exact 45 class を remaining から除外し、それ以外の Functional logical test を一度だけ discover / launch する実際の plan を検証する。

foreground exact 7 methodはすべて`SettingsForegroundInteractionTests`にある現行FQNへ修正し、Aだけがclass全体を所有する。旧`SettingDialogEditCompletionTests`をownerとする3 FQNは退役する。

退役対象はLR2 early / pre-waveの宣言・phase、`EarlyShards` / `FanoutShards` / `FanoutLaunchShards`の二重表現、state/account-once helper、15/14/14/1大量allowlist、`functional-early-entry` probe/testである。validatorはhost名/selectorの一意性とdisjointness、portable/Bass isolation、A/B exact membership、remaining exclusion exact 45、foreground exact owner、logical onceだけへ縮小する。Functional test argumentsからper-testhost `--blame-hang*` と未使用shard timeoutを外し、`--blame-crash`、global deadline、last-observed stdout/stderr、TRX、bounded cleanupを維持する。

### Unit 4e-B: known false-watchdog completion owners

1. `MainWindowViewModelStartupProgressTests.StartupPostInitialization_StaleCallbackCannotOpenNewGenerationBarrier` は`ManualResetEventSlim.Wait(5s)`を`RunContinuationsAsynchronously`付きTCSとplain `await`へ置換し、通常完了の`fullyIdle.WaitAsync(5s)`もplain `await`にする。lock/read responsivenessのnegative boundは維持する。
2. ChartInfoのnormal-completion `SpinUntil` / 10秒poll 13箇所を、RequestedVersion / CompletedVersion / Runningの`PropertyChanged`とpredicate再確認を所有するtest-private async signalへ置換する。temporary song DB helperをasync delegate対応し、該当8 testsをasync化する。cleanupとnegative lock boundは維持する。
3. ChartInfo 5 source groupは再び5 owner fixtureへ分け、ThreadPoolを同期blockする前提への対策だった単一partial `ChartInfoMetadataOwnerTests` regroupを退役する。production seam、new process / DNP、既定timeout helperは追加しない。

### Test delta and verification

| Contract | Coverage decision | Shared resource / completion | Retired route |
| --- | --- | --- | --- |
| four-host Functional logical once / bounded cleanup | `VerificationRunnerContractTests`とlifecycle probeを`replace/extend` | actual launch plan、absolute deadline、raw PID ownership、stream tasks | LR2 early、pre-wave、15/14/14/1、per-host hang watchdog |
| startup stale generation barrier | existing methodを`replace` | async work-start TCS + fully-idle Task; global Functional budget | 5秒normal-completion wait |
| ChartInfo hydration/backfill/install completion | existing8 tests / 13 pollsを`replace` | PropertyChanged + version/running predicate、GUID DB/root、finally unsubscribe/cleanup | SpinUntil/10秒poll、single partial regroup |
| test authoring policy | Tests AGENTS、test-authoring contract、testing strategyを更新 | normal completion plain await; B/D bounds only | convenience 5-second default policy |

Focused verificationはrunner/lifecycle、Startup fixture、ChartInfo 5 fixture、foreground/settings/WPF/nativeの対象filterを実行する。before/after logical setは現行buildでdiscoverしたtest identityを比較し、TRX件数だけに依存しない。runner / fixture placement変更の最終snapshotでFunctionalを3回連続し、各回のportable開始から全Functional testhost完了までのtest executionを180秒以内、tracked fingerprint不変、残留test process / foreground HWND 0とする。以前のcanonical 155秒 / 15秒headroom条件は退役する。Full 1回とfresh static reviewを続ける。WPF focused 30回はテスト整理中の一時的な競合検出gateとして既に十分実施済みであり、以後の修正では反復しない。

Replan triggerは、logical testの欠落/重複、portable/Bass/foreground/process-global stateの交差、async signalの例外/late completion喪失、Functional failure/180秒超過、残留process/HWND、tracked mutationである。failure時は局所watchdog追加、timeout延長、named shardの継ぎ足しをせず、resource ownershipかcompletion signalを調査する。

### Unit 4e-B implementation evidence

- `AwaitChartInfoHydrationAsync` / `AwaitChartInfoBackfillAsync` now subscribe to the BMSLibrary `PropertyChanged` event, recheck the requested/completed version and running predicate after subscription, complete a `RunContinuationsAsynchronously` TCS, and always unsubscribe in `finally`. All former ChartInfo normal-completion poll sites, including the direct owner poll, no longer use `SpinWait` / 10-second polling; lock and other negative bounds remain unchanged.
- The temporary song DB helper has an async-delegate overload whose root cleanup runs after the awaited delegate in `finally`. Eight hydration/backfill/install tests are async MSTest methods; task faults and late state notifications remain observable, and GUID-owned DB/filesystem cleanup is still awaited before deletion.
- The five source groups are distinct `TestClass` fixtures (`ChartInfoMetadataSchemaExportImportTests`, `ChartInfoParserBehaviorTests`, `ChartInfoBackfillStorageTests`, `ChartInfoInlineHydrationTests`, `ChartInfoInstallFailureRetryTests`). The partial `ChartInfoMetadataOwnerTests` type and its runner absence selector are retired; KISS Functional keeps all five on `remaining` discovery with no named selector. The 134-method / 141-case ledger is unchanged, including all `DataRow` cases.
- Focused Quick for the five fixtures plus `VerificationRunnerContractTests` passed 134 tests with 11 expected opt-in skips (145 discovered) in `tests-quick-20260825-044047`; tracked fingerprint and residual process checks were clean. Functional / Full / WPF repeat and final static review remain Unit 5 responsibilities.

### Unit 4e-C: one canonical Functional deadline policy

Unit 4e-A / 4e-B の実装後、Functional が 170 秒で test host を止め、10 秒を正常完了前の reserve として扱う設計を退役させる。portable testhost の起動直前に `FunctionalTimeoutSeconds` の executable deadline policy object を一度だけ作り、portable から全 fanout testhost の process exit までを同じ execution deadline で判定する。restore、build、built-output validation、stream / artifact 回収、repository whitespace は test execution budget の対象外とする。failure 時だけ、その execution deadline + 10 秒の一つの cleanup cutoff を portable / fanout の lifecycleへ渡す。cleanup window は失敗 invocation の owned PID / descendant / stream / artifact cleanup 専用で、execution deadline後に成功へ昇格させない。

`Get-RemainingBudgetSeconds` は cleanup window を差し引かず、test-execution start probe で zero elapsed が configured execution budget 全量を返す。host topology は `portable-settings` の後に `bass-collectible`、`serial-state-a`、`serial-state-b`、`remaining` を開始する 5-host / `1/1/1/1/ProcessorCount` / `ClassLevel` のまま変更しない。170 秒、pre-completion reserve、host ごとの deadline reset、named watchdog、worker reduction は追加しない。

### Unit 4e-C evidence and replan

- `tests-functional-20260825-044755`: canonical 173.7 秒で execution deadline failure、remaining host stopped、2638 results、tracked unchanged / residual 0。
- `tests-functional-20260825-045115`: canonical 174.5 秒で同じ execution deadline failure、remaining host stopped、3255 results、tracked unchanged / residual 0。
- 2回とも単一 host hang の evidence はなく、testhost start / execution window が subset progression を消費した結果であり、同じ process lifecycle artifact は PID ownership / cleanup を維持している。これは execution deadline を緩める根拠ではなく、Functional test-execution budget が進捗 failure の正本であることを確認する evidence として記録する。
- Test delta は既存 `VerificationRunnerContractTests` の `extend`。guarded actual runner seam を portable testhost 起動境界から呼び、execution delta = budget、failure-cleanup delta = budget + 10、restore / build / postflight の budget外、retired 170 / pre-reserve field absence、現行 5-host plan を検証する。`VerificationProcessLifecycleTests` と probe は既存 shared cutoff coverage で充足し、fixture / lane / DNP は追加しない。
- `verification-runner-contract.ps1` の CanonicalFunctional metadata は `ExecutionDeadline = portable-test-start+FunctionalTimeoutSeconds` と `FailureCleanupDeadline = execution-deadline+10-seconds` を記述し、`CanonicalFunctional_PropagatesTimeoutAndCallerOwnedDiagnostics` で確認する。ただし metadata は standalone proof ではなく、同じ Quick の guarded actual-policy test が executable behavior の正本である。

Focused verificationはPowerShell parse、guarded actual deadline-policy / plan probe、`git diff --check`、および次の Quick filterで行う。Functional / Fullはrootの統合責任とし、このunitでは実行しない。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'
```

Replan triggerは、execution / cleanup deadlineが別 phaseへ resetされる、successが180秒超過後に通る、failure cleanupが +10秒 cutoffを超える、restore/build/portable/fanoutで absolute deadlineの伝播が失われる、5-host topologyやlogical onceが変わる、tracked mutationまたはresidual processが発生する、もしくは同一条件の2回目の deterministic timeout / failure evidenceが出る場合とする。

### Unit 4e-D: persistent nonactivating HWND style correction

#### Trigger evidence

`b19a8c29` の Functional `tests-functional-20260825-053138` は `serial-state-a` で 392 件中 1 件が失敗した。失敗は `SettingsWindowPresentationTests.SettingsWindow_AboutIdentityPresentationUpdatesVersionAndBuildWithoutRedundantStatus` の `Non-activating test HWND ... became the foreground window` で、canonical elapsed は 125.8 秒、residual は 0 だった。同じ host は直前に foreground interaction の 12 invocation をすべて実行しており、直前の foreground window が閉じた時点で、`ShowActivated=false`、offscreen placement、`SWP_NOACTIVATE` だけでは共有 dispatcher の test HWND が選択され得ることが確認された。

#### Correction and observable outcome

`TestWindowPresentationScope` の既存 HWND 作成境界で、`NonActivating` window の native extended style を読み、既存 bits を保持したまま `WS_EX_NOACTIVATE` を追加する。`GetWindowLongPtr` / `SetWindowLongPtr` の error state と readback を検証し、native API failure、既存 style の欠落、style mismatch は明示的に失敗させる。popup は既存 `Opened` observation で同じ policy を適用する。`VerifyPresentationPolicy` は offscreen / non-foreground に加えて native `WS_EX_NOACTIVATE` を確認する。`ShowActivated=false`、`ShowInTaskbar=false`、offscreen placement、`SWP_NOACTIVATE`、現行 5-host `1/1/1/1/ProcessorCount` topology、`ForegroundInteraction` の exact 7 methodは変更しない。

#### Test delta and verification

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| foreground close後も non-activating owner / child / popup HWND が選択されない | `TestWindowPresentationScope` HWND creation / native style readback / `VerifyPresentationPolicy` | existing `WpfTestApplicationHostTests.RunWindowTest_NonActivatingPresentationStaysOffscreenAndCleansWindowAndPopupHwnds` を extend; failed `SettingsWindowPresentationTests` methodは behavior coverage として維持 | extend; new fixtureなし | process-local WPF `Application` / STA dispatcher / `serial-state-a`; standard filtered Quick | existing `ContentRendered` / owned presentation signal、popup `Opened`、deterministic cleanup | transient `ShowActivated` / `SWP_NOACTIVATE` flagsだけに依存する route、stale named settings host map |

Focused verification:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~WpfTestApplicationHostTests|FullyQualifiedName=BeMusicSeeker.Tests.SettingsWindowPresentationTests.SettingsWindow_AboutIdentityPresentationUpdatesVersionAndBuildWithoutRedundantStatus|FullyQualifiedName~SettingsForegroundInteractionTests'
```

結果は 18/18 pass、diagnostics `artifacts/verification/tests-quick-20260825-055313`、testhost / vstest / verification residual 0、`git diff --check` pass。Functional / Full / repeat30 は Unit 5 の root 統合責任であり、final stability は未完了のままとする。

### Unit 4e-E: BmsLibrary logical-prefix partition replan

#### Trigger evidence

`d9d33b55` の Functional evidenceでは `055855` が142.6秒で完了した一方、`055532` と `060135` は `remaining` の execution deadlineへ到達した。成功した `remaining` のwallは112.1秒で、method-DNP tailは3.3秒に留まったため、単一hostのmethod tailや新しいwatchdogが主因ではない。portable完了後の通常parallel phase全体で、BmsLibraryを含むcatch-all `remaining` の進捗が広く減速する証拠であり、同じ180秒 execution deadlineと+10秒 failure-cleanup contractを維持したまま、論理的なprefix partitionを再計画する。

#### Replan and bounded implementation

既存catch-allのbase filter `R`（Functional category exclusionとportable / BASS / serial A / serial Bのexact 45 class exclusion）を、一つの selector constant `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary`から生成したpositive / negative predicateへ分割する。`remaining-bms-library` は `R & positive`、`remaining` は `R & negative` とし、両descriptorは同じ `R`、`ProcessorCount` workers、`ClassLevel` scope、45 class exclusionを共有する。exact 21-class allowlistをexecution sourceへ追加せず、logical prefixで自動routeする。validatorはactual plan array上で6 hostの名前 / 順序、portable first、fanout 5 host、shared base、common selectorのopposite polarity、disjoint union = `R`、workers / scope、logical onceを起動前に証明する。

portable hostはexclusive firstのまま、成功後にBass、serial A、serial B、`remaining-bms-library`、remainingを同じvalidated plan arrayから待機phaseなしで開始する。fanoutのworkersは `1/1/1/ProcessorCount/ProcessorCount`（portableを含む全体は `1/1/1/1/ProcessorCount/ProcessorCount`）で、12 logical processorsではportable完了後の最大同時worker数は約27となる。one canonical 180-second execution deadline、failure cleanup deadline = execution + 10 seconds、foreground exact 7 methods、45 exclusions、logical once、既存process lifecycle / cleanupは変更しない。worker reduction、pre-wave、per-host accounting、new watchdog、arbitrary fixture shard、new lane / DNP / timeoutは追加しない。

Test deltaは既存 `VerificationRunnerContractTests.FunctionalShardPlan_UsesExecutablePlanForExactHostOwnership` の `extend`。actual runner planを通して、6 host names/order、二つの空class logical-prefix descriptor、shared `R`、positive / negative selector、45 exclusions、ProcessorCount / ClassLevel、filter compositionを検証する。新しいfixture、process seam、completion signal、lane、DNPは追加しない。

Focused PowerShell parse / guarded actual plan probeと `git diff --check` はpassした。`VerificationRunnerContractTests` filtered Quickは5/5 pass、diagnostics `artifacts/verification/tests-quick-20260825-073449`、tracked fingerprint不変だった。Functional / Full / final stabilityはUnit 5のroot統合責任であり、ここではまだpassを主張しない。

Replan triggerは、actual planが6 host / 5 fanout / logical-prefix complementを証明できない、BmsLibraryのexact allowlistや別selector constantが再導入される、portable後の同一plan fanoutが崩れる、180秒/+10秒 deadlineまたはcleanup ownershipが変わる、tracked mutation / residual processが出る、または同一条件の2回目のdeterministic timeout / failure evidenceが出る場合とする。

## Unit 5: final acceptance under current policy and review

Review correction と current policy / cursor correction の統合後、最終 behavior snapshot で次を実行する。過去のWPF 30回の競合検出は `62f27b51` までに十分完了しているため、evidenceを保持したまま再実行しない。Functional は原則一回とし、180秒 timeout時だけ同じ条件で一回 retryする。

1. PowerShell parse、`git diff --check`、Release build、runner hash。
2. lifecycle focused Quick。
3. WPF focused repeat gate は退役済みとし、既存 `62f27b51` evidenceを一時的な競合検出gateの完了記録として保持するだけで、今後は実行しない。
4. Functionalを一回。portable testhost開始直前から全Functional testhostの実際の`ExitTime`までが180秒以内、tracked fingerprint不変、残留test process 0であることを確認する。180秒 timeout時はcleanup / artifact確認後、同じ command・filter・budget・snapshot・条件で一回だけretryし、二回目の結果に応じて一過性 machine load の記録または原因調査を行う。
5. Fullを一回。今回のdocs / test policy correctionだけを理由にはしないが、先行 runner 変更 `b32df9d6` の最終 acceptance を同じ final snapshot で閉じるため実行する。
6. implementation threadを閉じ、snapshotを凍結してfresh `repo-static-review`を呼ぶ。

Reviewer は asymmetric persistence、scope seal後のfault、actual post-start caller exception、artifact非破壊、primary/secondary precedence、deadline後のprimitive開始、test自己検証、physical OS cursor route の不在と key / routed event / explicit hit / deterministic seam の利用を重点確認する。

### Post-completion review correction resolution

- Review P1の「Functional 180秒はscript開始からpostflightまでのcommand全体を含むべき」という指摘は、元の要件と異なるため採用しない。ただしrepository側もその解釈を一時的に仕様化していたため、`f5e884bf` で portable `dotnet test` 起動直前から全Functional testhost終了までだけを測る実装・executable contract・仕様へ補正した。restore、build、runsettings / artifact準備、stream / artifact回収、fingerprint、環境復元、whitespace確認は180秒に含めない。
- Review P2の4つのVerification mapが退役済みnamed shardを正本としていた指摘は妥当だった。`76b0ccfa` で現行6-host plan、exact serial selector、shared `R`のBmsLibrary positive / negative partitionへ統一した。
- WPF 30回反復は `62f27b51` の一時的な競合検出gateで完了済みとし、ユーザー判断により今回も今後も再実行しない。通常のWPF coverageはcanonical Functionalの一回のinvocationに含める。
- 最初のFull `tests-full-20260825-093908` は、12-worker ProcessIntegrationでstream-timeout probeが固定2秒root budgetを先に消費し、本来のstream-drain contractではなくprocess timeout routeへ入って失敗した。shared path / PID競合ではなくprocess-heavy並列時のfixture開始条件だったため、worker / parallelismを減らさず、`a8f1f6a4` でroot exitを30秒の外部process containment内に同期してから4秒stream-cleanup deadlineを開始した。production lifecycleのtimeout時 `ExitCode = null` contractは変更していない。
- `e638d132` のfresh static reviewで、deadline直前に終了したhostをpoll観測時刻でfalse timeoutにし得る指摘は妥当だった。`b32df9d6` でretained process handleの実際の`ExitTime`を正本にし、deadline直前 / 直後の実process境界probeを追加した。
- 同reviewのraw-process failure contract不足も妥当だった。`b32df9d6` でportable nonzero、portable post-start exception、partial fanout post-start exceptionをactual canonical callerから発生させ、fanout抑止、primary precedence、raw PID ledger、exact cleanup residual 0を検証した。
- `scripts/AGENTS.md`が旧command-wide 180秒契約を残していた指摘も妥当で、`8712f94c`でtest-execution-only契約へ同期した。さらに`319cbccd`でFunctional 3回gateを原則1回・180秒timeout時だけ同一条件1回retryへ退役し、tests / fixturesのphysical OS cursor操作・観測を全廃・禁止した。

## Progress

| Unit | Status | Notes |
| --- | --- | --- |
| Unit 1: lifecycle P2 closure | Complete | `5e7ccaed`。focused Quick 17/17、PowerShell parse、`git diff --check`、Release build成功。 |
| Unit 2: Functional headroom replan | Retired by Unit 4e | base HEAD `128519856d57b304bc21930843b1aecfc10eeaa4` から、settings 16 fixtureをforeground 2/state 14の2 testhostへ分離し、presentation workspaceを2-worker/ClassLevel化。実 launch plan objectを同一 validatorへ渡し、exact membership、worker/scope、remaining exclusion、cross-route uniqueness、fanout object identityを検証する。PowerShell parse / `git diff --check` pass。focused Quick `VerificationRunnerContractTests` 4/4 pass、33.5s command、diagnostics `artifacts/verification/tests-quick-20260824-213148`。Functional/Full/WPF30はroot担当。playlist overlapとremaining fixture分割は採用しない。 |
| Unit 3: remaining owner fixture rebalancing | Retired by Unit 4e | `7ccca8ea` / `tests-functional-20260824-213728` のprocess deadline failureを受け、26 caseをlibrary init/mutation 13、package lifecycle/pending 7、catalog relocation 6へ分割。既存remaining process、12-worker ClassLevel、GUID resource、completion signalは維持。focused Quick 26/26 pass、Functional/Full/WPF30はroot担当。 |
| Unit 4: critical-tail owner topology | Retired by Unit 4e | `4097e3a3` でremainingは完走したがplaylist / presentation / settingsがdeadlineへ残ったため、4つのBMS playlist owner shard、7-class / 3-worker workspace shard、foreground / nonactivating / stateの3 settings shardへ再編した。実際のlaunch plan objectをvalidatorへ渡し、exact membership、worker/scope、remaining exclusion、cross-route uniqueness、旧FQN不在、foreground exact allowlist、fanout object identityを検証する。180秒command、170秒process deadline、10秒cleanup reserve、pre-wave順、remaining 12 workers、DNP、logical test setは維持。settings 142/142、workspace 208/208、BMS playlist + runner contract 99/99 pass。Functional / Full / static reviewはUnit 5で実施する。 |
| Unit 4b: staged settings and bounded fanout | Retired by Unit 4e | `018b894b` の17-shard contention failureとnested activating modalの誤分類を受け、foreground exact 7、playlist 2 grouped process、LR2 + settings early ownership、partial-launch cleanupへ再計画。実際のlaunch object validator、early state/accounting、raw process ownership cleanupを実装。Focused Quick / Functional / WPF30 / Fullの最終安定性確認はUnit 5で実施する。 |
| Unit 4c: LR2-only early and regular-chart ownership | Retired by Unit 4e | repeat timeoutとLR2 isolated 25.9s evidenceを受け、settingsをfanoutへ戻し、RegularChart 76件をremaining内5 ownerへ分割した。actual planは15/14/14/1、LR2-only early、settings post-pre-wave fanout exact-once、old class退役、narrow supportを維持する。fixture/runner focused Quick、parse、diff check、76/76 body identityを完了。 |
| Unit 4d: final Functional tail ownership | Retired by Unit 4e | Functional fanoutでChartInfo splitのThreadPool completion ownership premiseが破綻したため、5 source groupを単一partial `ChartInfoMetadataOwnerTests`へregroup。library/startup owner split、exact 6-worker ClassLevel route、15/14/14/1 topology、logical test set、watchdogを維持する。Functional / Full / WPF30 / static reviewはUnit 5で実施する。 |
| Unit 4e: KISS Functional / watchdog policy | Complete | 個別testの短時間予算と15-shard性能topologyを退役し、normal-completion plain awaitとtest execution全体の単一deadlineへ統合した。初期5-host / 4-fanoutはUnit 4e-Eで退役し、最終構成はportable完了後の6-host / 5-fanout `1/1/1/ProcessorCount/ProcessorCount`。180秒はrestore/build/preflight/postflightを除くportable開始から全Functional testhost完了までだけを測る。 |
| Unit 4e-E: BmsLibrary logical-prefix partition | Complete | `d9d33b55` の remaining timeout evidenceを受け、shared base `R`を一つの `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary` selectorのpositive / negative predicateへ分割。portable first後の6-host / 5-fanout `1/1/1/ProcessorCount/ProcessorCount` topology、45 exclusions、one canonical deadline、logical onceをactual plan validatorへ閉じた。parse / guarded plan probe / diff checkと runner contract Quick 5/5を完了。過去のFunctional 3回連続 evidenceは履歴として保持し、現行policyでは反復しない。 |
| Unit 5: current-policy acceptance and review | Complete | `62f27b51` のWPF 30回と過去のFunctional 3回は履歴として保持し再実行しなかった。`319cbccd`でcursor-free WPF Quick、runner / lifecycle Quick、Functionalを実行し、初回180秒timeout後の同一条件retryは164.3秒で成功したため両結果を一過性machine loadとして記録した。初回fresh reviewの2件のacceptance-direct P2を`489b38af`で修正し、成功時elapsedを最大retained process `ExitTime`、timeout時elapsedをexecution deadlineから算出する境界テストを追加した。最終snapshotのFunctionalは初回166.0秒で成功し、Fullも成功した。fresh correction reviewはblocking findingなし。 |

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
| `1db6ae18` | Functional after deterministic pre-wave fix | Fail: shared process deadline | 176.4s command / 174.4s canonical | `tests-functional-20260825-003425`; source rebuild 25.7s; pre-wave pass; residual0 |
| same | exact Functional retry | Fail: repeated process deadline | 175.9s command / 174.0s canonical | `tests-functional-20260825-003803`; remaining / library-chart / playlist-external-custom incomplete; LR2 span116s; tracked unchanged; residual0; Unit4c trigger met |
| same | LR2 isolated Quick | Pass (103/103) | 50.0s command / 25.9s test | `tests-quick-20260825-005600`; fingerprint unchanged; residual0; topology contention confirmed |
| Unit 4c integration worktree | PowerShell parse + direct `New-FunctionalShardPlan` / `Assert-FunctionalShardConfiguration` / `Assert-FunctionalOrchestrationConfiguration` probe + `git diff --check` | Pass | <1s | actual plan 15 shards / 14 fanout descriptors / 14 fanout launches / 1 early (`lr2-songdb-sync`); settings are post-pre-wave fanout; clean parse and whitespace |
| same | static method-body identity comparison | Pass (76/76) | <1s | old `RegularChartListOwnerTests` methods matched by name/body to five fixtures; no missing/extra/mismatch |
| same | regular five-fixture + `VerificationRunnerContractTests` focused Quick | Pass (80/80) | 38.8s command | first identical run hit only deterministic EOF whitespace guard (`tests-quick-20260825-011045`); formatting-only cleanup and exact rerun `tests-quick-20260825-011201` passed; fingerprint unchanged; residual0 |
| same | LR2/settings/library/BMS playlist grouped focused Quick | Pass (593 total: 582 passed, 11 skipped) | 95.1s command / 1.4m test | `tests-quick-20260825-011301`; expected opt-in compatibility skips; fingerprint unchanged; residual0 |
| `3fcf07fa` | Functional first run after Unit 4c | Fail: shared process deadline | 175.4s canonical / 139.9s test phase | `tests-functional-20260825-012115`; source rebuild 26.7s; LR2 and several routes completed、remaining / library / playlist / presentation / settings / WPF / feature stopped; tracked unchanged; residual test process0 |
| same | exact Functional retry | Fail: repeated process deadline | 175.0s canonical / 157.7s test phase | `tests-functional-20260825-012440`; build 8.9s; remaining / library / owned-chart / playlist 2 route / feature stopped、他route完了; tracked unchanged; residual test process0; Unit4d trigger met |
| Unit 4d-A snapshot | chart-owner focused Quick (seven OwnedChartCollection, four PlaylistSummary, five RegularChart fixtures, `VerificationRunnerContractTests`) | Pass (203/203) | 23.2s filtered build/test; 30.2s command including restore | `tests-quick-20260825-020100`; actual plan validator and launch-contract tests passed; PowerShell parse, Release build (0 errors), `git diff --check`; tracked fingerprint unchanged; residual test process 0 |
| Unit 4d-B snapshot | library/chart/startup owner fixture focused Quick + `VerificationRunnerContractTests` | Pass (259 total: 248 passed, 11 expected opt-in skips) | 27.2s command / 17.6s test | `tests-quick-20260825-022345`; actual plan validator: 15 launch / 14 fanout / 14 fanout launches / 1 LR2 early, library exact 14 classes / 6 ClassLevel workers; PowerShell parse, Release build (0 errors), `git diff --check`, old-FQN and body-ledger checks pass; tracked fingerprint unchanged; residual test process 0 |
| Unit 4d-C snapshot | feature retained 5 + returned 11 fixtures + `VerificationRunnerContractTests` focused Quick | Pass (536/536) | 41.7s command / 14.7s test | `tests-quick-20260825-023613`; actual plan validator: feature exact 5, returned exact 11, 15 / 14 / 14 / 1 topology, 1 worker / `ClassLevel`, remaining exclusion and cross-route uniqueness; reflection DNP metadata checks passed; `git diff --check`; tracked fingerprint unchanged; residual test process 0 |
| `759c40a2` resolver investigation | affected two ChartInfo fixture Quick | Pass (45/45) | 25.9s command / 6.2s test | `tests-quick-20260825-025038`; isolated split fixtures complete normally |
| same | async ChartInfo three-fixture direct 6-worker / `ClassLevel` route | Pass (63/63) | 5.9s test | `issue-resolver-chartinfo-three-owner`; no build/restore, same route runsettings |
| same | exact 14-class library-chart direct 6-worker / `ClassLevel` route | Pass (248 + 1 expected skip) | 20.4s test | `issue-resolver-library-chart-route`; supplied Functional fanout retry remains the deterministic failure evidence |
| regrouped worktree | `ChartInfoMetadataOwnerTests` + `VerificationRunnerContractTests` focused Quick | Pass (134 + 11 expected skips) | 46.2s command / 13.1s test | `tests-quick-20260825-025650`; build pass, exact 10-class route and 15 / 14 / 14 / 1 topology contract pass, tracked fingerprint unchanged |
| same | exact regrouped 10-class library-chart direct 6-worker / `ClassLevel` route | Pass (248 + 1 expected skip) | 17s test | `issue-resolver-library-chart-regrouped`; no build/restore, unchanged route runsettings |
| `7875a908` | exact foreground navigation diagnostic Quick | Pass (1/1) | 31.3s filtered build/test | `tests-quick-20260825-030451`; preceding Functionalの単発keyboard-focus lossはselection/page遷移後のfocus theftと判定; tracked unchanged; residual0 |
| same | Functional after ChartInfo regroup | Fail: remaining false watchdog / global oversubscription | 169.6s canonical / 154.1s test phase | `tests-functional-20260825-030537`; LR2 103/103だが115sへ膨張、Startup progress work-startの5s sync wait failure、tracked unchanged、residual0; Unit4e trigger |
| Unit 4e-A snapshot | PowerShell parse + direct `New-FunctionalShardPlan` / `Assert-FunctionalShardConfiguration` probe + `git diff --check` | Pass | <1s | actual executable plan: 5 hosts (`portable-settings`, `bass-collectible`, `serial-state-a`, `serial-state-b`, `remaining`), workers `1/1/1/1/12`, all `ClassLevel`, assigned 45, remaining exclusion 45, foreground 7; tracked files unchanged |
| Unit 4e-A snapshot | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'` | Pass (18/18) | 58.6s command / 48.8s test | `tests-quick-20260825-042849`; build succeeded after order/exclusion validator strengthening, actual plan contract and lifecycle probes passed, fingerprint unchanged, residual test process 0 |
| Unit 4e-C evidence | Functional first repeated failure | Fail: canonical execution deadline | 173.7s canonical | `tests-functional-20260825-044755`; remaining stopped, 2638 results, tracked unchanged, residual 0; no single-host hang evidence, build/start window consumed subset progression |
| same | Functional exact retry | Fail: canonical execution deadline | 174.5s canonical | `tests-functional-20260825-045115`; remaining stopped, 3255 results, tracked unchanged, residual 0; repeated evidence confirms the canonical budget is the progress-failure contract |
| Unit 4e-C snapshot | PowerShell parse + guarded fixed-start deadline policy / 5-host plan probe + `git diff --check` | Pass | <3s | executable policy returned execution delta = 180s, failure-cleanup delta = 190s, zero-elapsed remaining = 180s; plan remained 5 hosts / `1/1/1/1/12` / `ClassLevel`; tracked files unchanged |
| Unit 4e-C snapshot | `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'` | Pass (19/19) | 57.8s command / 48.1s test | `tests-quick-20260825-052312`; deadline policy and existing lifecycle / plan contracts passed; tracked fingerprint unchanged; residual test process 0 |
| Unit 4e-C metadata follow-up | Same focused Quick filter after canonical deadline metadata/assertion update | Pass (19/19) | `tests-quick-20260825-052704` test TRX: 49.0s | Canonical metadata now reports execution deadline = canonical start + `FunctionalTimeoutSeconds` and failure cleanup = execution + 10s; guarded executable policy and lifecycle / plan contracts passed; tracked fingerprint unchanged; residual test process 0 |
| `62f27b51` final implementation snapshot | modified-fixture focused Quick | Pass (328/328) | 41.7s command / 32.1s test | `tests-quick-20260825-073308`; normal-completion waits, playlist replacement, startup presentation, cancellation assertion all passed; fingerprint unchanged; residual 0 |
| same | runner/lifecycle focused Quick | Pass (19/19) | 68.1s command / 50.0s test | `tests-quick-20260825-080545`; runner SHA-256 `6A10D5EDEF466AA4A8B33CC4EC025E37D1632D40017C81F55A33951E766BD6EF`; PowerShell parse, Release build, `git diff --check` passed; fingerprint unchanged; residual 0 |
| same | WPF focused filter, 30 consecutive runs | Pass (30/30 runs, each 28/28) | 27.3-29.3s / run | `tests-quick-20260825-075025` through `tests-quick-20260825-080450`; all fingerprints unchanged; residual test process 0 |
| same | Functional first run | Fail: canonical execution deadline; exact retry required | 184.3s including failure cleanup / 150.5s test phase | `tests-functional-20260825-073617`; only `remaining` unfinished; other fanout hosts including `remaining-bms-library` completed; fingerprint unchanged; residual 0 |
| same | Functional exact retry, consecutive pass 1/3 | Pass | 175.7s canonical / 158.6s test phase | `tests-functional-20260825-073939`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 2/3 | Pass | 162.3s canonical / 145.4s test phase | `tests-functional-20260825-074253`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 3/3 | Pass | 177.7s canonical / 160.9s test phase | `tests-functional-20260825-074549`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Full | Pass | 626.5s total; canonical Functional 178.1s / 180s | `tests-full-20260825-080702`; Functional logical set, publish, existing-data, update, ProcessIntegration 49 pass + 2 skip, ReleaseAcceptance 2/2, format, analyzer 0 diagnostics passed; fingerprint unchanged; residual `testhost` / `vstest.console` 0 |
| `f5e884bf` integration worktree | runner / lifecycle focused Quick | Pass (20/20) | 61.3s command / 51.2s test | `tests-quick-20260825-092209`; guarded actual runner proves restore/build before deadline, portable raw-process start at deadline boundary, five-host fanout after portable exit, stopwatch stop before cleanup/postflight; fingerprint unchanged; residual 0 |
| `72dfb829` | Functional first run after test-only boundary correction | Fail: test execution deadline; exact retry required | 180.1s test execution | `tests-functional-20260825-092535`; only `remaining` unfinished; restore 8.4s and build 25.1s were outside deadline; fingerprint unchanged; residual 0 |
| same | Functional exact retry, consecutive pass 1/3 | Pass | 172.6s test execution | `tests-functional-20260825-092926`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 2/3 | Pass | 163.3s test execution | `tests-functional-20260825-093255`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 3/3 | Pass | 155.8s test execution | `tests-functional-20260825-093607`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Full before ProcessIntegration fixture correction | Fail after Functional/publish/update pass | 149.3s Functional test execution | `tests-full-20260825-093908`; ProcessIntegration `StreamDrainTimeoutDiagnosticIncludesContextAndOwnedCleanupCompletes` observed `ExitCode = null` after the process-heavy fanout consumed its fixed root budget; fingerprint unchanged; residual 0 |
| `a8f1f6a4` | ProcessIntegration focused Quick after deterministic fixture correction | Pass (50 + 2 expected skips) | 70.3s command / 58.9s test | `tests-quick-20260825-095730`; 12 workers / `ClassLevel`; stream-timeout probe 9s; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 1/3 on final snapshot | Pass | 162.1s test execution | `tests-functional-20260825-095904`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 2/3 on final snapshot | Pass | 155.8s test execution | `tests-functional-20260825-100230`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Functional consecutive pass 3/3 on final snapshot | Pass | 155.5s test execution | `tests-functional-20260825-100532`; all six hosts completed; fingerprint unchanged; residual 0 |
| same | Full | Pass | 155.3s Functional test execution | `tests-full-20260825-100834`; publish, existing-data, update, ProcessIntegration 50 pass + 2 skip, ReleaseAcceptance 2/2, format, analyzer 0 diagnostics passed; fingerprint unchanged; residual test / `wscript` process 0 |
| `b32df9d6` | runner / lifecycle focused Quick after retained-`ExitTime` and raw failure-contract correction | Pass (24/24) | 58.0s test | `tests-quick-20260825-105221`; actual exit-time boundary、portable nonzero / post-start、partial fanout cleanup passed; fingerprint unchanged; residual test process 0 |
| `319cbccd` | cursor-free WPF fixture focused Quick | Pass (14/14) | 22.2s phase / 8.2s test | `tests-quick-20260825-113700`; keyboard current-cell activation and deterministic explicit-hit event dispatch passed; fingerprint unchanged; physical cursor primitive audit 0 |
| same | runner / lifecycle focused Quick | Pass (24/24) | 85.6s phase / 71.4s test | `tests-quick-20260825-113754`; retained `ExitTime` boundary、raw process failure / cleanup、deadline / lifecycle contracts passed; fingerprint unchanged; residual test process 0 |
| same | Functional initial invocation under current policy | Fail: 180s test-execution timeout; one exact retry permitted | 180.0s test execution | `tests-functional-20260825-114002`; only `remaining` unfinished、other five hosts exit 0、tracked fingerprint unchanged、owned residual process 0、stdout progress retained |
| same | Functional exact retry | Pass; initial timeout classified as transient machine load | 164.3s test execution | `tests-functional-20260825-114413`; all six hosts completed、same command / budget / snapshot / conditions、no repeated symptom or deterministic failure evidence、fingerprint unchanged、residual 0 |
| same | Full | Pass | embedded Functional 162.9s test execution | `tests-full-20260825-114736`; publish、existing-data、update、ProcessIntegration 54 pass + 2 skip、ReleaseAcceptance 2/2、format、analyzer 0 diagnostics passed; fingerprint unchanged; residual test process 0 |
| `489b38af` correction worktree | runner / lifecycle focused Quick | Pass (24/24) | 96.8s command | `tests-quick-20260825-120940`; retained process `ExitTime` success elapsed 2.0s、timeout deadline elapsed 10.0s、timeout classification、process cleanup contracts passed; PowerShell parse and `git diff --check` passed; Functional / Full / WPF repeat were not run by the worker |
| `489b38af` | Functional current-policy acceptance | Pass on first invocation; no retry | 166.0s test execution | `tests-functional-20260825-122226`; all six hosts completed inside the shared 180s execution deadline; restore/build/postflight excluded; tracked fingerprint unchanged |
| same | Full | Pass | embedded Functional 166.4s test execution | `tests-full-20260825-122600`; publish、existing-data、update、ProcessIntegration 54 pass + 2 skip、ReleaseAcceptance 2/2、format、analyzer 0 diagnostics passed; tracked fingerprint unchanged; WPF repeat not run |

## Static review log

- Fresh review of `e638d132..0e350b30` found two acceptance-direct P2 findings: successful Functional elapsed used the poll-observation stopwatch instead of the retained process `ExitTime`, and `testing-strategy.md` still described the retired WPF repeat gate as current policy. No P0 / P1, pre-existing / out-of-scope finding, or recommendation was reported.
- `489b38af` fixes both findings. Successful elapsed is the maximum retained host `ExitTime` minus the portable-boundary `StartUtc`; timeout elapsed is the absolute execution deadline minus that same `StartUtc`; apparent success fails closed when a required retained `ExitTime` is unavailable. The canonical boundary probe deterministically verifies poll-after-deadline success at 2.0s and timeout at the 10.0s probe deadline. The spec now states that the WPF repeat gate is retired and is not run.
- Fresh correction review of `0e350b30..07e174ea` confirmed both prior findings are closed. It reported no P0 / P1、acceptance-direct P2、pre-existing / out-of-scope finding、or recommendation. The reviewer checked the supplied Quick / Functional / Full artifacts without re-running tests.

## Done when

- 4件のacceptance-direct P2がbehavior testとdurable specを伴って解消される。
- completed stream artifactとlifecycle diagnosticが他streamのtimeoutに失われない。
- late faultが別lifecycleへ混入せず、元ownerのtimeout diagnosticとfault observation contractが明確である。
- post-start exception後のexact-owned PID residualが0で、既存artifactとprimary failureが維持される。
- terminal operation中に確定したdiagnosticが成功したfinal logに含まれ、final flush失敗時はresult/callerのsecondary failureとして観測される。
- WPF 30回は `62f27b51` で一時的な競合検出gateとして完了済みとし、以後は反復しない。current policyの最終snapshotでFunctional一回が成功する（180秒 timeout時のみ同じ条件で一回 retryし、結果に応じて一過性 machine load の記録または原因調査を行う）。Functionalの180秒はportable testhost開始直前から全Functional testhostの実際の`ExitTime`までだけを対象とし、Fullは先行 runner 変更 `b32df9d6` の最終 acceptanceとして一回実行する。
- tests / fixtures に physical OS cursor の操作・観測がなく、key / routed event、explicit hit、deterministic fake / typed action seamで外部 user / OS状態への依存を避けている。production のcursor実装は変更せず、退役したtest routeは履歴として保持する。
- fresh static reviewでP0/P1とacceptanceへ直接反するP2がない。
- verification evidence、review result、最終commitを本計画へ記録し、StatusをCompleteにする。
