# v3 safety audit A-1 / A-2 remediation plan

Updated: 2026-09-03

## Goal

Close the two release-blocking findings in `BeMusicSeeker_v3.0.0.0_safety_audit_ja.md` without changing the post-durable rollback boundary or adding retry, replay, or persistent recovery state.

## Decisions

1. A-1 uses one package-install-owned immutable source-to-actual-destination map. The generic file/DB boundary and `ChartPackage` source-path semantics are not widened.
2. A-1 is implemented, reviewed, and committed before A-2. The units share package-install paths and are not parallel writers.
3. A-2 represents an authoritative post-durable finalization failure separately from the durable-commit fact and cleanup outcome. It never compensates or rolls back the durable filesystem/DB result.
4. A throwing canonical or LR2 finalizer is a non-success command outcome. Non-throwing LR2 non-convergence remains the deferred D-16 behavior.
5. A dual finalization and cleanup failure preserves the finalization failure as the primary non-success outcome and also preserves cleanup failure details and recovery paths.
6. Existing feature failure reporting is reused unless it cannot distinguish the durable partial outcome. No success milestone, registration, or selection is published after a finalization failure; a non-success refresh needed to converge already-durable state is allowed if it is not presented as success.

## Unit A-1: exact collision-resolved install destination

- Observable outcome: the actual destination selected during immutable preflight is the sole path used by the promoted file, receipt, detached DB projection, storage owner, live package entry, installed registration, and duplicate-merge catalog delta. The pre-existing collision file and DB row remain unchanged.
- Production reachability: file drop, pending normal install, pending force install, and duplicate merge all converge on `BmsLibraryPackageInstallService.MovePackageFilesWithReceipt`.
- Writable scope: package-install service and its package-local immutable mapping/result mechanics; `BMSLibrary.PackageInstall.cs` and merge owner only where the exact projection is consumed; canonical package/duplicate fixtures; mutation/install/duplicate specs; this plan status.
- Retired route: destination basename/root recomputation after preflight completion. Legacy bool-route collision tests are not the oracle and need not be removed.
- Invariants: D-09 through D-11, existing file collision suffix policy, case-preserving chosen path value, no overwrite of the old path row, no retry/rollback/persistent state, no new UI text.
- Test Contract Packet: `A1-EXACT-INSTALL-DESTINATION-20260903` (`A1-MAP-COHERENCE`, `A1-COLLISION-PRESERVE`, `A1-FORMAT-SHAPES`, `A1-INGRESS-PARITY`, `A1-MERGE-COHERENCE`). Authority is the user-approved audit finding, the public collision behavior in `docs/manual.ja.md`, D-09 through D-11, and the DB path primary-key contract. Current implementation/output and existing expectations are not oracle authority.
- Coverage: extend `BmsLibraryPackageInstallServiceTests` and `BmsLibraryDuplicateServiceTests`; use per-test GUID filesystem/SQLite resources and synchronous receipt plus persisted/live state as completion. Cover single BMS, single BMSON, mixed directory, existing collision, reachable same-plan reservation, the three install ingress families, and merge without a full cross-product.
- Red/head evidence: the same canonical receipt-path tests must fail on base because the actual suffixed file differs from DB/live projection, then pass on head. If same-plan reservation is not reachable from a supported ingress, stop rather than manufacture a private/fake-only state.
- Quick filters: `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibraryPackageInstallServiceTests` and `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibraryDuplicateServiceTests`.
- Review: frozen static review checks all packet contracts, absence of path recomputation, existing-row preservation, and no A-2 semantics mixed into this unit.
- Replan: an exact map cannot be completed during preflight; merge requires a different relocation contract; supported same-plan collision is unreachable; generic boundary semantics must change.
- Coverage decision: the same-plan reservation axis was not added because no supported production ingress was established for it. No private, reflection, duplicate-entry, or fake-only route was introduced to manufacture that state. Existing-file collisions cover the reachable suffix path; a future supported ingress must freeze its own reachability and oracle before adding reservation behavior.

## Unit A-2: durable finalization failure visibility

- Observable outcome: an authoritative finalizer failure after a durable filesystem/DB commit is a typed durable non-success propagated through batch/command results. Package, folder move/rename, auto-rename, and merge do not publish ordinary success, registration, selection, or success dialog for that operation.
- Production reachability: the listed commands use `FileDbMutationExecutor`, whose current receipt captures the exception but reports `Completed`; their consumers primarily branch on `DurableCommit`.
- Writable scope: file/DB receipt and batch types; package, folder, auto-rename, and merge result/consumer paths proven necessary; canonical primitive/feature/workflow fixtures; relevant specs and localization only if existing feature errors are insufficient; this plan status.
- Retired route: `Completed` with a non-null authoritative post-commit failure and durable-only success branching.
- Invariants: durable filesystem/DB state remains authoritative, no compensation/retry, cleanup outcome and recovery paths remain orthogonal, finalization failure details are retained, non-throwing LR2 incompleteness remains D-16.
- Test Contract Packet: `A2-DURABLE-FINALIZATION-FAILURE-20260903` (`A2-PRIM-THROW`, `A2-PRIM-COMMIT`, `A2-DUAL`, `A2-BATCH-STOP`, `A2-PACKAGE-RESULT`, `A2-PACKAGE-WORKFLOW`, `A2-FOLDER-COMMAND`, `A2-FOLDER-WORKFLOW`, `A2-AUTO-COMMAND`, `A2-AUTO-WORKFLOW`, `A2-MERGE-COMMAND`, `A2-MERGE-WORKFLOW`, `A2-CLASSIFICATION`, `A2-A1-COHERENCE`). Authority is the user-approved audit finding, D-08 through D-11 and D-16, and the public command rule that failure is not ordinary success. Current implementation/output, the old `Completed` expectation, private call order, and localized copy are not oracle authority.
- Coverage candidates: replace the primitive Completed-on-finalizer-failure assertion in `ResilientFileMutationServiceTests`; extend existing package, folder rename/move, auto-rename, duplicate merge, and workflow completion fixtures. Use GUID resources and task/event/result terminal signals; no fixed waits or new parallel lane.
- Red/head evidence: canonical fault injection must first show durable receipt plus erroneous normal success, then the same tests pass with typed non-success. A dual finalization/cleanup negative control must retain both dimensions.
- Coverage boundary: the primitive fixture owns the full durability/finalization/cleanup matrix. Feature fixtures each cover one distinct producer or result/publication boundary and must not duplicate the filesystem matrix. A production-shaped zero-file cleanup case is included only if its finalizer fault is reachable through an existing canonical seam.
- Failure contract: `DurableCommit` remains true; the exact terminal state is `DurableFinalizationFailed` and the batch fact is `HasDurableFinalizationFailure`. The failed item is excluded from registration, maintenance, score, state, after-apply, selection, success dialog/milestone/normal success notification, and later batch mutation. Cleanup-only remains `CompletedWithCleanupFailure`; dual failure retains the finalization exception as primary and also exposes cleanup failure and recovery paths.
- Classification control: a non-throwing LR2 incomplete status and post-lease dialog, notification, progress, or subscriber failure are not A-2 finalization failures. A throwing finalizer, including an LR2 finalizer that propagates an exception, is a typed non-success.
- Review: frozen static review checks every reachable consumer, dual-failure precedence, preserved durability, and absence of rollback/retry/persistent state.
- Replan: LR2 exception authority cannot be reconciled with D-16; a legacy ingress cannot expose non-success without a compatibility decision; cleanup facts cannot be retained; convergence would require automatic retry/rollback.

## Integration and commit policy

- Each unit gets focused Quick verification and a frozen static review before its own commit.
- After both commits, run the final Functional lane once. Run Full because these findings gate the v3 release and the prior Full predates both code units.
- A review fix gets a focused Quick rerun and a fresh static review. A changed integration premise triggers the relevant integrated lane again.
- Do not push, tag, publish, or change the version.

## Execution record

| Date | Unit | Result | Evidence |
| --- | --- | --- | --- |
| 2026-09-03 | A-1 | Implemented and reviewed | Base regression exposed actual-destination versus DB/live mismatch. A post-implementation integration regression showed that the DB finalizer could mutate a live chart path before the live package finalizer performed its source lookup; the final implementation snapshots source identity before that boundary. Exact set `tests-quick-20260903-195030` passed 6/6; related fixtures `tests-quick-20260903-193932` passed 143 with 2 approved cross-volume skips; fresh static review found no blocking issue. Same-plan reservation was excluded because no supported production ingress was established. |
| 2026-09-03 | A-2 | Implemented and reviewed | Primitive, package, folder, auto-rename, and duplicate-merge fault injection now preserve durable commit while publishing typed non-success, stop later batch items, and suppress ordinary success consumers. Combined focused fixtures `tests-quick-20260903-223046` passed 269 with 6 approved cross-volume skips. Review fixes preserved the typed auto-rename failure projection and asserted dual-fault recovery paths; `tests-quick-20260903-230643` passed 35 with 4 approved skips, and the fresh static review found no blocking issue. Functional and Full integration remain pending after this unit commit. |
