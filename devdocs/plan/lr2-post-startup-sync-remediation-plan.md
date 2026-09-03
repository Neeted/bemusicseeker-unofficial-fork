# LR2 post-startup sync remediation plan

## Decisions

- Automatic LR2 `song.db` synchronization belongs after successful normal `Startup` completion, not in the startup progress phase set.
- `startup_initialization_complete` is the production completion boundary. The pending first-startup completion dialog is shown synchronously; the automatic LR2 queue is created only after that call returns (dismissal), and no queue is created for failed or aborted startup.
- One in-memory scheduling guard limits automatic queueing to once per successful Startup generation. Existing durable `Completed` + signature gating, scheduler generation checks, shutdown behavior, manual resync, and coalescing remain authoritative.
- LR2 `Running`, progress, `Incomplete`, and `Failed` states remain in the dedicated `OperationProgressHub` status. They do not alter startup expected/completed counts, gauge, or failure. Startup visual linger may temporarily hide the dedicated status; the latest nonterminal status is shown after linger clears.
- Unit A does not alter user cancellation. Unit B owns cancellation-specific behavior and tests.

## Packet and coverage ledger

Packet ID: `LR2-POSTSTARTUP-2026-09`

| Contract | Authority / expected observable | Unit A coverage |
| --- | --- | --- |
| ORDER-1 | User requirement: completion, including pending dialog dismissal, precedes one automatic LR2 queue; failed/aborted startup does not queue | `MainWindowViewModelStartupProgressTests.StartupProgress_BackgroundTasksWaitForRequiredSchedulingClosure` proves idle-before-close cannot complete and close+idle can complete; `StartupBackgroundTaskSchedulerOwnerTests` proves LR2 requests use the post lane. Existing dialog/UI-coordinator tests own synchronous dismissal, while durable/full acceptance owns the real startup/dialog/LR2 terminal route. No local test asserts private call order. |
| PRESENT-1 | User requirement: LR2 status is dedicated; startup accounting stays unchanged; latest status appears after visual linger | `OperationProgressHubViewModelTests.Lr2SongDbSyncPresentation_UsesRuntimeStatusAndSuppression` |
| FAILURE-1 | User requirement: incomplete/failure remains dedicated and retryable; successful startup stays successful | `OperationProgressHubViewModelTests.Lr2SongDbSyncIncompleteStatus_RemainsDedicatedAndRetryableWithoutStartupAccounting` |
| PROGRESS-1 | Preserve coalescing/latest-state publication | Existing runtime-status publication coverage retained; no publication contract rewrite |
| SHUTDOWN-1 | Internal shutdown cancellation and required-task drain remain available after removing startup-phase coupling | Existing scheduler shutdown classification/drain tests and LR2 model cancellation coverage remain unchanged |

## Unit A status and evidence (2026-09-03)

Implemented the production route and retired LR2 startup-phase coupling. Focused Quick was red before production edits for the intended PRESENT-1 mismatch (old LR2 phase tracking was removed from the fixture while the old suppression expectation remained): 47 passed, 1 failed in `OperationProgressHubViewModelTests`, artifact `artifacts/verification/tests-quick-20260903-142124/functional/results.trx`. Per the amended ORDER-1 packet, the temporary boundary helper/fixture and its negative control are not retained: they did not execute the canonical `MainWindowViewModel` once state and would have made a private call-order assertion. Required-close and post-classification negative controls remain represented by the owner/scheduler tests. The final focused Quick covering all Unit A fixtures passed 87/87, artifact `artifacts/verification/tests-quick-20260903-155826/functional/results.trx` (43.9s filtered build/test; existing compiler warnings only). The run covers required-scheduling-close ordering and LR2 post-lane/shutdown classification.

## ORDER-1 evidence amendment

The local automated evidence is intentionally decomposed across existing production-owned seams. No new full startup/LR2 integration fixture, fake startup library, temporary DB/configuration, constructor port, generic harness, source/reflection test, or placement mutant is added. The workflow-owner test covers the required-scheduling-close boundary, scheduler tests cover post-lane classification, existing dialog/UI-coordinator tests cover synchronous dismissal, and durable/full acceptance covers the real application ingress and terminal LR2 behavior. The canonical `MainWindowViewModel` sequence and existing once guard remain a production review point rather than a test-owned copy of that state.

## Unit B pending

Unit B owns user-cancel behavior and its cancel-specific tests. It must preserve the post-startup ordering and dedicated LR2 status established by Unit A.
