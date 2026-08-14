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
| 1A | owner-local/GUID resource | complete | pending | 232/232 passed, 50.8 s | 3 consecutive runs passed: 149.7 / 144.4 / 137.8 s | clean after two focused corrections | Audited 12 owner/resource fixtures; removed 5 attributes and retained 7 documented process-wide boundaries. |
| 1B | fresh settings/composition | pending | — | pending | pending | pending | — |
| 1C | large fixture/method-level DNP/runner topology | pending | — | pending | pending | pending | — |
| 1D | deterministic signal cohort | pending | — | pending | pending | pending | — |
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

## Retirement rule

Slice 4 must delete this file. Before deletion, any still-relevant runner contract or test strategy decision must be incorporated once into the appropriate `devdocs/spec` document; transient status, command output, and slice bookkeeping are not normative and must not be copied forward.
