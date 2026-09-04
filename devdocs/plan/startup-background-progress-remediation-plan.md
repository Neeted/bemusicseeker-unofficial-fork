# Startup background progress remediation plan

Status: In progress

Base revision: `1805354e0a2a5184e79d98a1644c7c38aa15b445`

Approved Test Contract Packet: `STARTUP-BG-PROGRESS-20260905`

## Goal

起動時の required initialization と、その後に継続する scheduler-managed post-initialization work の
表示境界を整理する。LR2 自動同期の受理済み preparation を retryable として表示せず、custom-folder
repair、`.lr2folder` file diff、LR2 folder-table reconciliation の長時間区間へ bounded progress を
接続する。pure cache である virtual-order prewarm は durable/output/LR2 work の後に実行する。

## Decisions and constraints

- `StartupReadyOperable` で通常 UI を解禁する現行境界と required startup gauge の分母は変更しない。
- required gauge の完了後は、別の nonblocking background-initialization presentation を使う。
- background presentation は post scheduling 開始から、既存の
  `startup_post_initialization_maintenance_complete` 条件（post scheduling closed、scheduler fully idle、
  warmup completed）まで継続する。
- playlist または LR2 の dedicated progress が active の間は generic presentation を抑制し、二重表示しない。
- scheduler 外の ranking/XML refresh と遅延 presentation flush は今回の terminal に含めない。
- unknown total は determinate percentage にせず indeterminate とする。
- progress publication は best-effort とし、durable commit、whole-table atomicity、failure、cancellation を
  変更しない。
- version/generation token、persistent state、retry/replay/rollback/fallback を追加しない。
- future LR2 procedural orchestration は本 unit では実装しない。

## Reviewable units

| Unit | Observable outcome | Main ownership | Contract | Verification | Status |
| --- | --- | --- | --- | --- | --- |
| U1 LR2 preparation status | accepted automatic preparation は直ちに non-retryable Running/preparing となり、retry button を露出しない | LR2 request coordinator/status publication | `SBG-01` | focused LR2 Quick + static review | Complete |
| U2 prewarm ordering | virtual-order prewarm は既登録 output work と enrollment が追加する LR2 work の後に開始し、LR2 no-op でも完了する | startup background scheduler/warmup | `SBG-02` | scheduler Quick + static review | Complete |
| U3 custom-folder repair progress | startup repair は bounded table progress を公開し、zero target/terminal で非表示へ戻る | playlist repair/progress hub | `SBG-03` | playlist + hub Quick + static review | Pending |
| U4 folder progress | `.lr2folder` file diff と full LR2 folder reconciliation が bounded intermediate progress を公開する | library scan/LR2 folder reconciliation | `SBG-04A`, `SBG-04B` | LR2/library Quick + static review | Pending |
| U5 background presentation | required gauge 後も scheduler-managed post work が連続して表示され、composite terminal だけで消える | startup lifecycle/progress hub/WPF | `SBG-05` | startup/hub/WPF Quick + Functional + static review | Pending |

Units are sequential because U3-U5 share progress presentation and resource/spec paths. Each unit is reviewed and
committed before the next unit begins. Final integration runs one Functional verification on the final snapshot.

## Approved test contract

### `SBG-01` — automatic LR2 preparation status

- Production ingress: successful startup completion -> post-startup LR2 enrollment -> library queue/preparation ->
  runtime status -> progress hub -> status bar.
- Required outcome: accepted preparation immediately publishes active `Running/preparing` semantics and is not
  retryable. Idle `Needed` and terminal `Failed`/`Incomplete`/`Cancelled` remain retryable. A retry during accepted
  work does not enqueue duplicate work.
- Allowed variation: stage identifier/copy, UI dispatch turn, internal preparation mechanics.
- Wrong implementations: publish `Needed` until preparation completes; allow retry while Running; queue duplicate work.
- Evidence: replace the inverse expectation in
  `QueueLr2SongDbSync_RunsPrepareBeforeMarkingSyncRunning`; observe preparation entry, status timeline, retry
  visibility and queue cardinality through existing owner seams.

### `SBG-02` — virtual-order prewarm ordering

- Production ingress: startup completion -> LR2 enrollment and warmup scheduling -> startup background scheduler ->
  optional dynamically enrolled LR2 work -> prewarm.
- Required outcome: prewarm begins only after already-enrolled output/durable work and dynamically enrolled LR2 work
  terminate. LR2 no-op does not create an unsatisfied dependency or block composite completion.
- Allowed variation: predecessor ordering, priority number, prewarm stage count.
- Wrong implementations: a merely lower numeric priority that still precedes dynamically queued LR2; dependency on a
  non-existent no-op LR2 task.
- Evidence: signal/barrier task ledger for dynamic-LR2 and LR2-no-op cases; no wall-clock success inference.

### `SBG-03` — startup custom-folder repair progress

- Production ingress: startup -> playlist entries hydration -> scheduled repair -> playlist output owner -> playlist
  progress -> hub/status bar.
- Required outcome: a multi-target run publishes active progress with a fixed run total, bounded monotonic processed
  values and at least one strict intermediate value; terminal and zero-target runs leave presentation inactive.
  Observer failure cannot change files, LR2 rows or repair outcome.
- Allowed variation: callback frequency/order, current-table copy, coalescing, indeterminate target discovery.
- Wrong implementations: start/end only, decreasing/overrun values, active leak on zero target, observer exception
  escaping into the repair.
- Evidence: extend the production hydration/repair lifecycle fixture with multi-target, zero-target and throwing-observer
  cases; use the existing hub mapping seam.

### `SBG-04A` — startup `.lr2folder` file-diff progress

- Production ingress: startup/reload file diff -> library scan pipeline -> LR2 folder-file sync -> library initialization
  progress -> startup status bar.
- Required outcome: multi-item input publishes a fixed total and bounded monotonic strict intermediate before terminal.
  Existing DB commit, prune, failure and cancellation semantics remain unchanged; observer failure is isolated.
- Allowed variation: bounded batch size, path display, stage identifier/copy, duplicate-value coalescing.
- Wrong implementations: scan start/end only, first `N/N` after all work, decreasing/overrun progress, observer failure
  changing the folder DB result.
- Evidence: extend the captured-surface reload/startup owner case; direct service tests are supporting evidence only.

### `SBG-04B` — LR2 folder-table reconciliation progress

- Production ingress: automatic LR2 work -> coordinator -> song-db service -> folder-table reconciliation -> runtime
  status -> hub.
- Required outcome: multi-item reconciliation publishes stage total and bounded monotonic strict intermediate before
  terminal. Whole-table atomic apply, durable cursor meaning, cancellation and failure terminal semantics are unchanged.
- Allowed variation: normal/`.lr2folder` stage split, bounded callback frequency, current-path display.
- Wrong implementations: stage boundary only; treating progress as durable partial commit; partial rows after cancel;
  reporter exception failing sync.
- Evidence: production queue/status observation plus focused service emission coverage and retained atomic rollback tests.

### `SBG-05` — nonblocking background-initialization presentation

- Production ingress: startup schedule-start -> separate background presentation -> progress hub -> compiled status bar;
  terminal is post scheduling closed + scheduler fully idle + warmup completed.
- Required outcome: required startup gauge and UI unblock remain unchanged. Generic indeterminate presentation covers
  queued/running post work after required completion. Dedicated playlist/LR2 progress replaces generic presentation;
  generic returns if the composite terminal is not reached. Only the composite terminal clears it. Dedicated LR2
  failure/retry remains visible and scheduler-external work does not extend lifetime.
- Allowed variation: localized copy, animation/layout, generic task detail.
- Wrong implementations: clear at required completion or transient idle; clear before warmup; show generic and dedicated
  simultaneously; hide LR2 failure; block UI; wait for scheduler-external work.
- Evidence: startup lifecycle, hub precedence and compiled WPF representative-state tests. Do not make an exact binding
  inventory or translated copy the oracle.

## Coverage and safety

- Candidate fixtures: `BmsLibraryLr2SongDbSyncTests`, `StartupBackgroundTaskSchedulerOwnerTests`,
  `BmsPlaylistPersistenceLifecycleTests`, `OperationProgressHubViewModelTests`,
  `MainWindowViewModelStartupProgressTests`, `MainWindowProgressStatusBarWpfTests`, and focused LR2 service/folder tests.
- Shared resources remain in their existing Settings/WPF/LR2 lanes; no new `DoNotParallelize` is added.
- Completion uses TCS/barrier/property events/scheduler idle/warmup receipts. Fixed sleeps and timeout increases are not
  success conditions.
- Localized copy, source text, exact stage strings, log text, snapshots and priority numeric values are not assertion
  authority.
- Replan if any unit requires changed retryable states, fewer predecessors before prewarm, scheduler-external completion,
  non-monotonic progress, weakened atomic/failure/cancellation semantics, new persistent state/token, or recovery machinery.

## Final acceptance

1. Each unit's focused Quick filter passes on its committed snapshot.
2. Each unit receives a frozen static review; blocking findings are fixed and freshly reviewed before commit.
3. The final snapshot passes `scripts/verify-refactor.ps1 -Mode Functional` once.
4. `git diff --check`, resource parity, and tracked-file integrity pass; no unrelated user change is modified.

## Verification ledger

- U1 `SBG-01`: replacement regression
  `QueueLr2SongDbSync_PublishesRunningStatusBeforePreparingWithoutQueuingDuplicateWork` failed on the production-fix
  baseline because preparation still observed `Needed`, then passed after publication was moved to the accepted
  preparation boundary. The first static review found that an exception after early publication could strand
  `Running`; `QueueLr2SongDbSync_PreparationFailurePublishesRetryableTerminalStatus` now verifies exception propagation,
  retryable `Failed` publication, and that no work was scheduled. A fresh review then found a release-before-terminal
  race; the preparation reservation now covers prepared-surface apply or failure cleanup/publication, and an inline
  competing request at failure publication verifies that stale failure cannot overwrite newer work. The next review
  found the matching success-side release-before-running gap, so U1 ownership was replanned: the synchronization owner
  now atomically transfers the existing preparation lease into running ownership before the lease is disposed. An
  inline competing request at the transfer boundary verifies that only the accepted work is scheduled. Focused Quick
  artifact after the ownership correction: `artifacts/verification/tests-quick-20260905-022426`.
- U2 `SBG-02`: the dynamic-LR2 and LR2-no-op scheduler tests both failed on the U1 baseline because the previously
  enrolled priority-18 prewarm ran before LR2 enrollment. The implementation now enrolls the one-shot prewarm only
  after post scheduling is closed and the scheduler is fully idle; the prewarm task also has the scheduler's lowest
  priority so no already-queued post work can follow it. Dynamic LR2 work therefore terminates first, while an LR2
  no-op leaves no unsatisfied dependency. The first static review found that the scheduler-only tests did not protect
  the MainWindow idle-callback wiring. The production-route regression now holds either a dynamically enrolled LR2
  task or a no-op enrollment active, proves warmup is absent before first full idle, and then verifies one reservation
  plus final composite completion through the actual callback. The fresh review found that an early-scheduling mutant
  at required completion could coexist with the correct idle callback without being observed. A second route test now
  reaches actual required completion while post work is active and proves that the warmup reservation count remains
  zero until first full idle. Consolidated focused Quick artifact after those corrections:
  `artifacts/verification/tests-quick-20260905-025655`.
