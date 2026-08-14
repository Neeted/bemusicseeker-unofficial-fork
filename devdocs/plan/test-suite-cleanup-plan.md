# Test suite cleanup execution plan

> Temporary, non-normative execution record. Current contracts remain in `devdocs/spec`; this file does not redefine product or test behavior.

## Fixed decisions

- Preserve observable behavior and failure contracts. Do not add meaning-changing fallbacks or hide failures to make tests pass.
- Remove `DoNotParallelize` only after the fixture or method is shown to use fresh owners/fakes, GUID-scoped files, or otherwise test-local state.
- Keep explicit serialization around real process-wide WPF/native/settings/logging/resource state until that state has an owned isolation boundary. Every retained attribute records the exact shared resource.
- Replace fixed waits for normal completion with an existing deterministic signal, barrier, or idle contract. Delays used deliberately to create contention are not completion waits.
- Replace source-text and private-reflection assertions with compiled contracts or explicit test seams by the owning slice; do not widen production APIs merely for speculative test convenience.
- The canonical verification route must keep bounded execution, immutable tracked files, useful timeout/failure artifacts, and opt-in fixture semantics.
- Each slice is independently reviewable and records its Quick/Functional/static-review evidence below.

## Baseline inventory

- `DoNotParallelize`: 76 attributes total: 41 class-level and 35 method-level attributes across 47 files.
- `SourceTextTestHelper`: helper plus consumers span 20 files and 417 references. The 19 consumer files contain 156 methods and 416 calls.
- Private reflection, original Unit 8 target: 286 planner-core references across 8 files. A broader `Invoke`/`GetValue`/`SetValue` search reports 466 references; that broader count is an inventory signal, not a claim that every occurrence is invalid.
- Runner contract gaps:
  - no `VerificationRunnerContractTests` coverage;
  - Full reconstructs Functional instead of invoking its canonical route;
  - Full has no overall deadline;
  - `accept-net10-update` republishes the current build and chooses the distribution zip by latest modification time;
  - `UpdaterPackageSync` creates `ProcessStartInfo` without `CreateNoWindow`.
- Latest Functional baseline, three consecutive runs: 3,962 total, 3,951 passed, 11 opt-in skipped, 0 failed; approximately 143 s / 134 s / 134 s.

## Slice checklist and evidence

`commit` remains blank until the corresponding reviewed slice is committed. Evidence is filled only after the corresponding command or review completes.

| Slice | Scope | Status | Commit | Quick | Functional | Review | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1A | owner-local/GUID resource | complete | `0d2731b9` | 232/232 passed, 50.8 s | 3 consecutive runs passed: 149.7 / 144.4 / 137.8 s | clean after two focused corrections | Audited 12 owner/resource fixtures; removed 5 attributes and retained 7 documented process-wide boundaries. |
| 1B | fresh settings/composition | complete | `6c8d64d6` | 519/519 passed, plus correction 4/4 and 140/140 | 3 consecutive runs passed: 139.9 / 131.3 / 136.4 s; corrected snapshot 134.5 s | clean after corrective replan | Replaced shared settings/resource state with fresh settings sessions, explicit option providers, and scoped thread culture in the 16-fixture cohort; retained method-level serialization only for the intentional distinct-`Settings.Default` sentinel test. |
| 1C | large fixture/method-level DNP/runner topology | complete | `1c8f2cce` | corrective combined Quick 916/916 passed, 13 opt-in skipped | 3 consecutive runs passed: 152.8 / 143.3 / 151.2 s | clean after exact-membership correction | Audited method boundaries, retained 3 class safety boundaries, and replaced the broad class-wide shard with exact foreground-window and process-global-lifecycle shards. |
| 1D | deterministic signal cohort | in progress | — | Unit 1: final correction 1/1 and fixture 11/11 passed; Unit 2 final cohort: 391/391 passed | Unit 1: 3,962 total in 149.3 s; Unit 2 final snapshot: 3,962 total in 151.0 s | Units 1-2 clean | Unit 1 replaces fixed/polling completion waits in three owner fixtures; Unit 2 covers play-history refresh/sort and shell-shutdown waits. |
| 1E | remaining wait audit | pending | — | pending | pending | pending | — |
| 2A | compiled owner contracts | pending | — | pending | pending | pending | — |
| 2B | WPF shell behavior + `SourceTextTestHelper` retirement | pending | — | pending | pending | pending | — |
| 3A | settings private-reflection retirement | pending | — | pending | pending | pending | — |
| 3B | playlist private-reflection retirement | pending | — | pending | pending | pending | — |
| 3C | pending-install private-reflection retirement | pending | — | pending | pending | pending | — |
| 3D | catalog/LR2 private-reflection retirement | pending | — | pending | pending | pending | — |
| 4 | runner failure contract + immutable/bounded Full + plan retirement | pending | — | pending | pending | pending | Delete this temporary plan after its durable contracts/evidence are integrated into the normative spec or history. |

## Slice 1A audit

- Parallelization candidates: `AudioDeviceTestWorkflowOwnerTests`, `CatalogMutationOwnerTests`, `InstallDestinationStateOwnerTests`, `Lr2PlayHistorySchemaServiceTests`, and `PlaylistOperationNotificationOwnerTests`. Their owners/fakes are fresh per test and filesystem/database resources are GUID-scoped where present.
- Retained serialization:
  - `ApplicationUiSchedulerBoundaryTests`: process-wide `TestUiDispatcherHost` WPF dispatcher and its thread affinity.
  - `LibraryFileScanPipelineOwnerTests`: process-wide localization resource state changed through `TestResourceInitializer`.
  - `Lr2PlayHistorySchemaUiTests`: WPF dispatcher state and process-wide `Settings.Default`.
  - `PlayHistoryReadModelTests`: shared WPF dispatcher host and process-wide settings-backed test ports.
  - `ResourceIconContractTests`: shared WPF dispatcher, process-wide `ResourceManager`, and GDI icon handles.
  - `ShellShutdownWorkflowOwnerTests`: process-wide `Settings.Default` and shared dispatcher-owned shutdown composition state.
  - `InstalledOnlyResourceOverwriteValidationTests`: production options snapshot created from process-wide `Settings.Default`, plus localization globals (`App.AvailableCultures` and `Resources.Culture`) changed by `TestResourceInitializer`.
- Wait audit: no normal-completion fixed wait was replaced. The only fixed delay in this slice is the intentional 2 ms subscriber delay that creates queue contention for the coalescing contract; completion uses bounded idle/event conditions.

## Validation and review evidence

- Slice 1A targeted Quick: passed, 232/232 tests, 0 failed, 50.8 s command time (12.7373 s test time), `tests-quick-20260815-024413`.
- Slice 1A Functional after the parallelization change: three consecutive runs passed with 3,962 total tests and unchanged tracked-tree fingerprint `DF58E4A98050FA772F271C011F18567FB92FB84219082ADBEF63B30DBC7D0FD1`:
  - `tests-functional-20260815-024942`: 149.7 s;
  - `tests-functional-20260815-025217`: 144.4 s;
  - `tests-functional-20260815-025446`: 137.8 s.
- No `testhost`, `vstest`, updater, or repository test process remained after the three runs.
- `git diff --check`: passed.
- Static review: the first review requested the mandatory post-change Functional triplet. The first fresh review confirmed that evidence and identified an incomplete retained-resource comment for `InstalledOnlyResourceOverwriteValidationTests`; the comment and this audit now name both settings and localization globals. The second fresh review reported no blocking findings.

### Slice 1B

- Exact 16-fixture Quick: 519/519 passed in 20.5 s, `tests-quick-20260815-032158`.
- Explicit-global compatibility checks: the Settings window appearance and playlist default-output migration tests each passed with an explicit `Settings.Default` factory argument.
- Three consecutive Functional runs passed with 3,962 total tests and unchanged tracked-tree fingerprint `0E066EAFD817B1EC60B0E29E168286A18F980BB26F844CBE8104BBD9F878401C`:
  - `tests-functional-20260815-033114`: 139.9 s;
  - `tests-functional-20260815-033341`: 131.3 s;
  - `tests-functional-20260815-033557`: 136.4 s.
- Worker static review completed two focused correction rounds and reported no remaining blocking findings. Final integrated review is pending.
- The integrated review then found a process-global `DispatcherHelper.UIDispatcher` race and two retained-global tests with mismatched factory settings. A corrective unit-planner pass removed both static dispatcher mutations and made the two remaining global-settings consumers explicit.
- Corrective Quick runs passed: 4/4 focused tests (`tests-quick-20260815-035246`) and 140/140 ApplicationComposition/BmsPlaylistUpdate tests (`tests-quick-20260815-035319`).
- Corrected-snapshot Functional passed 3,962 tests in 134.5 s with unchanged fingerprint `F7C627C6B75F8B893EE55B385B3CD8ECD8B38351A401DABD4AF6AD4E75210027`, `tests-functional-20260815-035353`.
- The final fresh review of the replanned correction reported no blocking findings.

### Slice 1C

- Attribute audit result: remove 3 class-level attributes and 8 unjustified method-level attributes, add 1 method-level attribute for the shared main/workspace column settings mutation, and retain 3 class-level plus 27 existing method-level attributes. The 12-class cohort contains 3 class-level and 28 method-level `DoNotParallelize` attributes; repository totals are class 17, method 29, total 46.
- Retained method resources: initialization environment variables (3), chart-info process/global database and environment contracts (8), `TempDirectoryPublisher.RemoveAll` archive expansion cleanup (4), pending-package settings mutation (4), external playlist default/settings and registration state (5), recommended-table settings mutation (1), and regular-chart-list `StandardCustomTableColumnSettings` / `PlaylistSummaryColumnsSettings` mutation (3).
- Runner topology target: retire `feature-remaining-classwide-dnp`; use exact 1-worker / `ClassLevel` shards `foreground-window-interaction` (2 classes) and `process-global-lifecycle` (3 classes). Former broad-shard members without a justified resource boundary fall through the remaining shard.
- The initial exact combined 12-class Quick detected a same-testhost `Settings.Default` collision: 929 total, 898 passed, 18 failed, 13 skipped in 94.7 s, `tests-quick-20260815-040840`. All failures were confined to `BmsLibraryLr2SongDbSyncTests` and `BmsPlaylistUpdateTests`; isolated runs passed LR2 sync 103/103 (`tests-quick-20260815-041047`) and playlist update 108/108 (`tests-quick-20260815-041121`). The correction retains class DNP for those fixtures and `PlaybackPanelViewModelTests`, whose process-global settings mutations require the same arbitrary-filter safety boundary. This failure is detection evidence, not an accepted limitation.
- Corrective exact combined 12-class Quick passed: 929 total, 916 passed, 13 skipped, 0 failed in 106.9 s, `tests-quick-20260815-042841`; tracked fingerprint remained `93D2B920367E2A5D5A284F31796FEA0B44D0AB39C3A3E1A80BCCBB53E503DB06`.
- PowerShell parser reported 0 errors. The runner's own topology assertion passed with exact 2-class foreground and 3-class lifecycle membership, each using 1 worker / `ClassLevel`; generic overlap and exclusion validation remained active.
- A high-load Functional attempt exposed a separate audio workflow test race: its runtime-side 5-second release expiry could win against a delayed test continuation. Corrective commits `69f743a9` and `60ab7fc4` replaced the blocking start observation and then made release a test-owned task completed from `finally`; focused 1/1 and fixture 22/22 Quick runs passed and fresh static review was clean.
- Three consecutive Functional runs then passed with 3,962 total tests (3,951 passed and 11 opt-in skipped), unchanged tracked fingerprint `48AC58E408A2AC301A21E228675AC4289CD8FFF78A5BDF755DC4D94CF0AEBF14`, and no residual repository test process:
  - `tests-functional-20260815-050122`: 152.8 s;
  - `tests-functional-20260815-050404`: 143.3 s;
  - `tests-functional-20260815-050637`: 151.2 s.
- `git diff --check`: passed.
- Corrective static review: the first review found that the spec incorrectly described LR2 settings cleanup as original-value restoration; the spec now distinguishes its fixed fixture-baseline reset from the playlist/playback fixtures' original-value restoration. The integrated review then found that exact-membership validation reused the shard construction arrays as its expected values. The preflight now compares against independent literal allowlists; parser and preflight checks passed, and a fresh review reported no blocking findings.

### Slice 1D

- Unit 1 commit `9707487c` scope: `BmsLibraryFolderRenameRefreshTests`, `PlaylistRecommendedTableOwnerTests`, and `StartupBackgroundTaskSchedulerOwnerTests`.
- Folder rename completion now observes the exact `BMSFile.Folder` and `BMSFile.path` notifications; synchronous encoding persistence and reference-display updates are asserted without grace-period sleeps.
- Concurrent recommended-table loading now issues eight synchronous callers on dedicated long-running tasks, then waits for their production-call receipts and the first HTTP request before releasing the fake response. Cleanup always releases both gates, boundedly observes every caller task, and disposes synchronization primitives only after all callers complete.
- Startup scheduler completion and summary checks now consume generation/revision notifications and recheck scheduler state without polling. Dependency ordering, concurrency thresholds, and reset accounting use task-owned entry barriers and exact snapshots. Lane and total concurrency are independently proven by queued/running snapshots before any gate release, and cleanup always releases the held workers. The 250 ms negative contention watchdog remains because it proves new-generation required work stays blocked while the prior garbage-collection gate is deliberately held.
- Unit 1 review correction Quick passed 2/2 focused tests in 25.0 s command time (1.6486 s test time), `tests-quick-20260815-054530`. The final three-fixture Quick passed 73/73 tests in 17.2 s command time (8.6699 s test time), `tests-quick-20260815-054601`; tracked fingerprint remained `7565840152115905435E516DC87925E1BCA10333604AEFAD7F78973E12718A75`.
- A subsequent high-load Functional invalidated the ThreadPool-based caller barrier by timing out before all eight callers issued the production call. The dedicated-thread correction passed its focused Quick 1/1 in 22.8 s, `tests-quick-20260815-055305`, and the complete fixture 11/11 in 11.0 s, `tests-quick-20260815-055333`; tracked fingerprint remained `B86016E098DB111916CBE3B673E2764F60A7636A7E7A97C95D726453E928B73E`.
- The pre-correction Unit 1 Functional completed 3,962 total tests (3,951 passed and 11 opt-in skipped) in 154.5 s, `tests-functional-20260815-053318`. After the dedicated-thread correction, Functional completed the same 3,962 total tests in 149.3 s, `tests-functional-20260815-055435`; tracked fingerprint remained `BD272CC9E2C763BC3E896C58F9AE886D4AF82013505ADB2DA34668309AA20C9A` and no repository test process remained.
- Unit 1 fresh static review reported no blocking findings after the scheduler-threshold, all-caller issuance, and failure-cleanup corrections.

#### Unit 2 (complete, `86eec92f`)

- Scope: replace play-history sort and refresh-queue polling with owner receipts, and replace shell-shutdown task polling with task-based completion plus the existing startup-update idle/terminal receipts.
- Production contract: `PlayHistoryWorkflowOwner` exposes one shared, generation-scoped refresh-queue idle task whose state is linearized with queued revisions and active counts under `PresentationState.SyncRoot`; continuations complete outside that lock.
- Targeted Quick passed 110/110 tests in 30.7 s command time (4.9018 s test time), `tests-quick-20260815-061327`; tracked fingerprint remained `5C36930A2C23C835F9DD3D01069CA1D33F28D9C7139C4921A7DB25D90E77A684` and no repository test process remained. A proposed dispatch-failure callback receipt was removed after a 109/110 diagnostic run showed that the callback is intentionally downstream of the held drain; the owner `Started` / `Running` state is the deterministic entry contract for that failure path, and the focused correction passed 2/2 tests (`tests-quick-20260815-061104`).
- The initial Unit 2 Functional (`tests-functional-20260815-061437`) failed only `MainWindowContextMenuResourceTests.PlayHistoryView_KeywordFilterUpdatedReusesProjectedState`, whose source-text assertions required the former `Interlocked.CompareExchange` implementation literals. The correction deletes that single brittle method instead of teaching it new implementation text. Its behavioral coverage now maps to the compiled `PlayHistoryReadModelTests.WorkflowOwner_ExecuteViewKeywordFilterUpdatedReusesCurrentProjectionWithoutReadingSource` entry/no-read contract, `ChartListVirtualViewTests.PlayHistoryWorkflowOwner_BuildPresentationOnlyAppliesLatestKeywordRevision` for the direct latest-revision build, `ChartListVirtualViewTests.PlayHistoryWorkflowOwner_ApplySortedRowsBuildsPresentationAndCommitsTerminalState` for terminal success, and the existing refresh coalescing/activity and callback-completion tests.
- The three exact entry/build/terminal correction tests passed 3/3 in 16.0 s command time (1.6383 s test time), `tests-quick-20260815-064941`; tracked fingerprint remained `CF481BEC31BF417EF0A97F89DA8BAD07D1483513F69609956102A11ECE0756B2`.
- Correction verification passed: mapped typed tests 4/4 (`tests-quick-20260815-062056`), the remaining `MainWindowContextMenuResourceTests` fixture 140/140 (`tests-quick-20260815-062116`), and the combined four-fixture Unit 2 cohort 358/358 in 16.7 s command time (`tests-quick-20260815-062138`). All three retained tracked fingerprint `1E4999E633A9A07B071526C153DC00EFEC5D04BD58B98A2EDE27B9B627CEEE22`.
- After adding the compiled entry/no-read coverage, the combined four-fixture Unit 2 cohort passed 359/359 in 16.9 s command time (8.2094 s test time), `tests-quick-20260815-065019`; tracked fingerprint remained `E378B7D3C6C5C99F921233D6DA9E70B5D93039D908AFFB8A24CDB4341188C37B`.
- Expanding the cohort to `ApplicationCompositionTests` exposed one failure in `PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher` (`tests-functional-20260815-065111`): the worker request could run before a valid playlist-summary cache generation existed, so its dispatcher-frame timer became an accidental completion timeout. The correction seeds an empty cache for the current rebuild generation, runs the request on a dedicated `LongRunning` worker, terminates the frame only from the typed `IsPlaylistSummaryMode` notification, and retains the five-second timer only as a deadlock watchdog. The exact test passed 1/1 (`tests-quick-20260815-065732`), the `ApplicationCompositionTests` fixture passed 32/32 (`tests-quick-20260815-065753`), and the expanded five-fixture Unit 2 cohort passed 391/391 in 16.4 s command time (7.8335 s test time), `tests-quick-20260815-065812`; all retained tracked fingerprint `68CF56418E89032C6CE0433504C13AEE7B89559D6352118172F1530BF586FE41`.
- Final Unit 2 Functional completed 3,962 total tests (3,951 passed and 11 opt-in skipped) in 151.0 s, `tests-functional-20260815-065905`; tracked fingerprint remained `4A7D3FAE01560A2B16474CF0F933BE8C2009A24848BADECDD58E210EF7ADCDA9` and no repository test process remained. Fresh static review reported no blocking findings after the typed entry-contract and summary-terminal corrections.

## Retirement rule

Slice 4 must delete this file. Before deletion, any still-relevant runner contract or test strategy decision must be incorporated once into the appropriate `devdocs/spec` document; transient status, command output, and slice bookkeeping are not normative and must not be copied forward.
