# LR2 song.db one-shot reconciliation plan

Status: Complete; final static review passed

Base revision: `9101ff7bc1717b1a9cab24614edb030dafd2ae0f`

Test Contract Packet: `B-6-D-16-LR2-ONE-SHOT-RECONCILIATION-20260904`

This document is the temporary execution record for B-6 and D-16. The durable behavior contract belongs in
`devdocs/spec/lr2-song-db-generation.md`; the design decision belongs in
`devdocs/decisions/lr2-song-db-one-shot-reconciliation.md`.

## Goal

- Reconcile LR2 `song.db` from the complete current input in one uninterrupted run.
- Never resume from a durable positional cursor. Every non-`Completed` run and every forced run starts at item zero.
- Avoid rereading a BMS file only when the immediately preceding startup/reload file-diff run already performed the
  full-sync-equivalent work and committed that path.
- Treat the whole `folder` table as an app-generated cache during full reconciliation.
- Preserve LR2 user-owned `song` columns while replacing generated columns with the current projection.
- Treat zero and negative generated timestamps as ordinary values, not missing-value sentinels.

## Context and production reachability

Canonical ingresses are:

1. `startup_initialization_complete` enrollment;
2. startup/reload file-diff completion;
3. status-bar retry;
4. Settings manual force and settings-triggered core sync;
5. coordinated application shutdown.

The first four enter the LR2 workflow owner, `BMSLibrary.QueueLr2SongDbSync`, the request coordinator and startup
background scheduler, then the LR2 sync service and SQLite writers. Their observable effects are the durable
`song` / `folder` cache, preserved user columns, durable status/progress, failure state, and number of BMS reads.
Shutdown enters through `BMSLibrary.RequestShutdown` and the internal LR2 cancellation token.

The current HEAD has already removed the user-facing LR2 cancel action and moved automatic scheduling after
`startup_initialization_complete`. Those routes are preserved. Remaining production debt is durable resume,
transient skip verification, split folder mutation, startup diagnostic/repair, shutdown writes of `Cancelled`, and
the unreachable `startup_scan_blockers` cleanup route.

## Constraints and out of scope

- Do not change the `lr2_song_db_sync_status` table/column set, the `name=default` row, sync-signature version,
  folder/song generator versions, or app-schema version.
- `processed_cursor` remains for persisted compatibility but means last committed processed count only.
- Keep legacy `Cancelled` parsing and retryability. Do not create new production `Cancelled` rows.
- Matching `Completed` plus matching signature remains an automatic no-op.
- Do not persist the file-diff receipt or restore it after consumption/failure/restart.
- Do not add input-manifest hashing, retry/replay/rollback orchestration, fallback scans, or external DB revalidation.
- Assume no out-of-process `song.db` mutation during the owned operation.
- Full sync does not add or delete `song` membership. File diff owns membership changes.
- Keep playlist/settings/catalog scoped incremental folder DB synchronization.
- Do not change BMSON-to-LR2 behavior, user-column ownership, updater/release behavior, or unrelated gateway design.

## Resolved decision list

1. `Running`, `Failed`, `Incomplete`, and legacy `Cancelled` evaluate as needed and always execute from zero.
2. Manual force also executes from zero, even when the stored row is `Completed`.
3. Progress is updated only after its corresponding durable commit and is never an input to a later run.
4. Shutdown cancellation rolls back the current transaction and records `Incomplete` / `shutdown_interrupted` when
   status persistence is safely available. A remaining `Running` row is acceptable if that write cannot complete.
   An unexpected non-shutdown `OperationCanceledException` is a failure.
5. The in-memory file-diff receipt is keyed by `OwnedChartCollectionVersion` and `BmsRowsVersion` only.
6. A receipt contains BMS paths whose full-sync-equivalent parse or safe metadata-only update, inline maintenance,
   matching LR2 row write, and SQLite chunk commit succeeded. BMSON and failed/rolled-back work are excluded.
7. The receipt is published only after the whole file-diff pipeline succeeds. It is atomically taken once by the
   immediate startup/reload follow-up and discarded on no queue, mismatch, preparation failure, sync failure,
   retry, manual/settings origin, shutdown, or disposal.
8. Eligible receipt paths are skipped without a BMS reader call and without a DB currentness query.
9. Full reconciliation composes normal directory, `folderinfo.txt`, discovered `.lr2folder`, app-managed playlist
   `.lr2folder`, built-in custom folder, and required parent/root rows before any DB mutation.
10. Folder priority is built-in > any `.lr2folder` > `folderinfo.txt` > normal directory. Equal same-tier projections
    deduplicate; unequal same-tier projections fail before DB write.
11. Full mode reparses `.lr2folder` content even when mtime matches. Incremental routes may retain scoped optimizations.
12. Full preparation for playlist and built-in surfaces performs physical materialization/verification only. Ordinary
    playlist/settings incremental actions continue their direct scoped DB synchronization.
13. After a complete projection, full reconciliation reads existing folder rows once and applies one transactional
    whole-table delete/upsert plan. Incomplete input or preflight failure causes zero folder mutation.
14. Startup blocker diagnostics, missing/unknown-root inference, date sentinel logic, repair, and the unreachable
    cleanup action are removed. Successful reconciliation is gated by complete preparation, folder apply, song
    generated-column update, source-current check, and status commit.

## Done when

- Matching `Completed` automatic requests execute no reader or writer.
- Every other/forced run starts with the first input regardless of persisted cursor/stage/total.
- A two-BMS empty-DB startup reads each path once across file diff plus its immediate full sync.
- Receipt version mismatch or any later/manual/retry/settings run performs normal reads.
- Receipt paths are never persisted, reverified, or reused.
- Full folder output equals the independently expected fresh projection; all unexpected rows are deleted.
- Folder projection conflicts and incomplete input fail before any folder mutation.
- Epoch and pre-epoch song/folder mtimes are preserved as zero/negative generated values.
- Full song reconciliation preserves `favorite`, `adddate`, and `tag`, and does not alter path membership.
- A failed chunk is retried from zero and converges idempotently.
- Legacy resume, verifier, diagnostic/repair, cleanup, and LR2 cancel production routes are absent; status retry and
  unrelated cancellation routes remain.
- Focused Quick, integrated Quick, Functional, Full, semantic old-name audit, and static review pass.

## Approved Test Contract Packet

The independent oracle was frozen before implementation from the user-approved one-shot plan and decisions. Current
implementation bodies, runtime output, existing test expected values, translations, and snapshots are not authority.

| Contract ID | Required observable outcome | Primary evidence |
| --- | --- | --- |
| `STS-01` | Matching `Completed` is an automatic no-op and consumes/discards any receipt. | Production-shaped queue/status test. |
| `STS-02` | All non-completed states and force start at item zero. | Nonzero cursor plus stale first item, base-fail/head-pass. |
| `STS-03` | Persisted cursor is commit-backed progress only. | Commit barrier/status observation and resume mutant. |
| `RCP-01` | Receipt includes only full-equivalent, committed paths from a wholly successful file-diff pipeline. | Success, rollback, maintenance and late-failure producer cases. |
| `RCP-02` | Only immediate startup/reload follow-up can take the matching receipt once. | Receipt owner plus origin/version/discard matrix. |
| `RCP-03` | Eligible paths have no reader call and no DB verification query. | Production-shaped read/query ledger; empty DB two-path case. |
| `RCP-04` | Receipt is in-memory only and absent after disposal/new owner/later run. | New-owner/manual execution and schema no-diff gate. |
| `FDR-01` | Full projection contains every source and required parent/root with independently expected values. | New whole-folder service fixture. |
| `FDR-02` | Priority, equal dedupe and unequal same-tier conflict are deterministic and order-independent. | Reversed-input and conflict tests. |
| `FDR-03` | Complete preflight precedes one whole-table transaction; incomplete/failure means zero mutation. | Late preparation fault and writer rollback tests. |
| `FDR-04` | Full mode reparses same-mtime content, accepts zero/negative dates, and deletes unexpected rows. | Base-fail/head-pass timestamp/content fixtures. |
| `FDR-05` | Full preparation has no DB prewrite; incremental playlist/settings DB sync remains. | Writer-call ledger plus existing incremental state tests. |
| `SON-01` | All generated columns converge while `favorite`/`adddate`/`tag` and zero/negative dates are preserved. | Independent synthetic row projection and durable query. |
| `SON-02` | Full sync does not add/delete song membership. | File-diff integration plus full-stage path-set equality. |
| `SON-03` | Failure cannot complete and retry starts at zero/idempotently converges. | Nth-chunk fault and stale-prefix test. |
| `RTR-01` | Diagnostic/repair/sentinel/cleanup routes are retired. | Behavior replacement plus narrow one-time legacy-absence audit. |
| `UI-01` | LR2 retry and legacy Cancelled display remain; LR2 cancel/cleanup actions are absent. | Mapper/hub/WPF behavior. |
| `SHD-01` | Only shutdown interrupts; rollback/status semantics hold; unrelated cancellation fails. | Transaction barrier plus RequestShutdown/status event. |
| `SCH-01` | Status schema/default row/signature/generator/app-schema compatibility is unchanged. | Existing-DB behavior and base/head declaration diff. |

Allowed variation includes internal type names, collection types, chunk sizes, ordering where not part of source
priority, logging, conflict exception wording, and transaction/savepoint implementation. It does not include weakening
the receipt lifecycle, folder atomicity/ownership, song user-column ownership, zero-start rule, or compatibility gate.

Authorized exactness is limited to persisted `name=default`, the existing status column set, legacy `Cancelled`,
`shutdown_interrupted`, exact selected paths, and the one-time absence audit for retired production routes. There is
no authority for localized-copy snapshots, broad source snapshots, private call-order assertions, or current-output
goldens.

## Coverage ledger

| Contract IDs | Fixture decision | Shared resource / completion | Retired coverage |
| --- | --- | --- | --- |
| `STS-*`, `SCH-01` | Extend/replace `Lr2SongDbSyncStatusServiceTests`, `Lr2SongDbSyncServiceTests`, `BmsLibraryLr2SongDbSyncTests`, signature tests. | Per-test DB; existing BMSLibrary DNP lane; direct return/status event. | Resume DTO/helper and resume-start assertions. |
| `RCP-01` | Extend `LibraryFileScanPipelineOwnerTests`. | Captured surface; pipeline commit/terminal callback. | `NewlyInsertedBmsPaths` as skip authority. |
| `RCP-02`,`RCP-04` | New focused receipt fixture; extend workflow/BMSLibrary fixtures. | Pure parallel owner test plus existing DNP integration. | Reason-prefix/broad freshness ownership. |
| `RCP-03` | Extend/replace BMSLibrary/service/pipeline fixtures. | File-diff terminal -> queued run status; finite watchdog only on failure. | DB skip verifier helper/tests. |
| `FDR-01..04` | New `Lr2FolderTableReconciliationServiceTests`; extend projection/writer tests. | Unique temp DB/files; synchronous result and injected failure. | Split-full prune and preserve-existing full behavior. |
| `FDR-05` | Extend playlist, BMSLibrary and scoped folder sync fixtures. | Existing playlist DNP lane; preparation return/persisted row. | Full-preparation prewrite expectation only. |
| `SON-*` | Extend writer/service/BMSLibrary fixtures. | Temp DB/files and deterministic chunk barrier. | Full stale-prune, resume and verifier expectations. |
| `RTR-01`,`UI-01` | Replace service, architecture, mapper, workflow, hub and WPF cleanup cases. | Existing WPF host/DNP; typed terminal/action ledger. | Startup diagnostic/repair/cleanup cases. |
| `SHD-01` | Replace production-shaped preflight cancel; extend rollback case. | Transaction barrier, RequestShutdown, run/status event. | New durable Cancelled-write assertions. |

No new fixed sleeps, raw dispatcher loops, broad reflection/source helpers, global process resources, or additional
`DoNotParallelize` boundaries are authorized.

## Sequential implementation units

### Unit 1: fresh-start status and shutdown semantics

- Outcome: retire durable resume while preserving schema/progress/Completed compatibility; make shutdown internal and
  fail unexpected cancellation.
- Primary writable paths: status service/ports/request coordinator/sync service, BMSLibrary LR2 owner, directly
  associated status/service/BMSLibrary tests.
- Contract IDs: `STS-01..03`, `SHD-01`, `SCH-01`.
- Semantic changes are API refactors, not text replacement. Persisted schema identifiers remain contract names.

### Unit 2: one-shot committed-path receipt

- Outcome: publish/take/discard the two-version in-memory receipt and skip eligible immediate paths without read/query.
- Primary writable paths: file-scan parse/commit/pipeline owners and result types, LR2 owner/coordinator/request/service,
  receipt type, focused pipeline/receipt/workflow/BMSLibrary/service tests.
- Contract IDs: `RCP-01..04`.
- Depends on Unit 1 so retry/force/origin behavior has one meaning.

### Unit 3: whole-folder reconciliation and song ownership

- Outcome: preflight a complete folder projection, apply it atomically once, preserve scoped incremental routes, update
  generated song columns without membership changes, and accept zero/negative dates.
- Primary writable paths: LR2 input/preparation owners, folder projection/generator/services/writer, sync service/song
  writer, playlist preparation seam, focused folder/song/playlist/BMSLibrary tests.
- Contract IDs: `FDR-01..05`, `SON-01..03`.
- Depends on Unit 2 because folder preparation and receipt consumption share the full-run request boundary.

### Unit 4: route retirement and durable documentation

- Outcome: delete startup diagnostic/repair and unreachable cleanup production/tests, update current spec and add ADR.
- Primary writable paths: coordinator/service DTOs, workflow/status mapper/hub/WPF only where cleanup remnants exist,
  architecture/UI tests, this plan, feature spec, ADR, and diagnostic memo retirement note.
- Contract IDs: `RTR-01`, `UI-01`, plus schema/route absence checks from `SCH-01`.
- Depends on Units 1-3 so no temporary compatibility wrapper remains.

All implementation units use one writable worker because they overlap the LR2 owner, sync service, status contract and
shared fixtures. The root owns integration verification and commits are not authorized by this request.

## Verification and review

- Each unit: filtered `Quick` for the affected canonical fixtures and `git diff --check`.
- After integration: combined filtered `Quick`, one `Functional`, and `Full` because this changes startup, durable DB,
  shutdown and release-candidate behavior.
- Semantic audit: old symbol/casing search; classify persisted schema/legacy parsing hits separately from stale API hits.
- Compatibility diff: no changes to status schema declaration, signature/generator/app-schema version declarations.
- Static review: frozen final snapshot against every Contract ID, reachability/impact evidence, receipt lifecycle,
  folder atomicity, user-column ownership, incremental route preservation, shutdown semantics and test-oracle independence.

## Verification ledger

Unit 4 implementation verification was run on the final working-tree snapshot. The initial focused run exposed
10 deterministic fixture mismatches caused by the Unit 3 full-sync ownership change (empty song membership and the
retired split folder result); those fixtures were corrected without changing packet semantics. The targeted rerun
and route/UI lanes then passed.

| Artifact | Command / scope | Result |
| --- | --- | --- |
| `artifacts/verification/tests-quick-20260904-033413/functional/results.trx` | LR2 service/folder/receipt/status/workflow/architecture/writer and file-scan pipeline focused Quick | 102 passed, 10 deterministic fixture failures; superseded by targeted correction |
| `artifacts/verification/tests-quick-20260904-033707/functional/results.trx` | Corrected receipt, chart-info, shutdown, and folder-result regression subset | 7 passed, 3 remaining deterministic fixture failures |
| `artifacts/verification/tests-quick-20260904-033912/functional/results.trx` | Corrected MD5 fallback, shutdown retry, and folder timestamp subset | 3 passed |
| `artifacts/verification/tests-quick-20260904-034019/functional/results.trx` | Workflow/status/hub/WPF, architecture, pipeline, settings, initialization, and retained scoped incremental routes | 116 passed |
| `artifacts/verification/tests-quick-20260904-034055/functional/results.trx` | `LocalizationResourceParityTests` after resource-key retirement | 7 passed |
| `artifacts/verification/tests-quick-20260904-034208/functional/results.trx` | Combined LR2 service/folder/receipt/status/workflow/architecture/writer, file-scan pipeline, hub/WPF, and localization filtered Quick | 150 passed |

The repository test project build passed with 0 errors. JSON localization parsing and `git diff --check` passed.
The narrow legacy-route audit is clean except for the two authorized one-time absence assertions in
`Lr2SynchronizationArchitectureTests`. Status schema/default/signature/generator/app-schema declarations were not
changed.

Root integration verification added the following evidence:

| Artifact | Command / scope | Result |
| --- | --- | --- |
| `artifacts/verification/tests-quick-20260904-040939/functional/results.trx` | Combined LR2 integration Quick after correcting root/custom-folder base classification and song-membership fixture setup | 743 passed |
| `artifacts/verification/tests-functional-20260904-041056` | Canonical Functional | 2,800 passed, 8 existing skips; 178.4 seconds |
| `artifacts/verification/tests-quick-20260904-042330/functional/results.trx` | Formatting-only correction in `MainWindowProgressStatusBarWpfTests` | 5 passed |
| `artifacts/verification/tests-full-20260904-042403` | Full: restore, build, canonical Functional, tool smoke, distribution, existing-data/update/v2.1.6 first-hop/release acceptance, format, analyzer | Passed; Functional 2,800 passed with 8 existing skips in 169.8 seconds; analyzer 0 diagnostics |

The first Full attempt reached every acceptance lane but reported two whitespace diagnostics in
`MainWindowProgressStatusBarWpfTests`. After removing those two extra spaces, the focused Quick and complete Full
rerun passed. The final Full runner reported an unchanged tracked-worktree fingerprint.

The first frozen static review found two acceptance-blocking gaps: a same-MD5 date-only file-diff update could be
treated as a full-equivalent receipt path without regenerating all song columns, and shutdown was not observed
inside the folder/song transactions before commit. The correction excludes date-only updates from receipt
eligibility, checks the shutdown boundary before each full-sync commit so rollback occurs, and reports the new
whole-folder reconciliation metrics instead of the retired split-result counters. The production-shaped stale-row
and transaction-barrier regression tests passed 3/3 in
`artifacts/verification/tests-quick-20260904-052317/functional/results.trx`. The corrected snapshot then passed
Functional with 2,802 tests passed and 8 existing skips in 179.4 seconds at
`artifacts/verification/tests-functional-20260904-052355`. A fresh static review remains pending on this corrected
snapshot.

The fresh review confirmed both prior fixes, then found that playlist full preparation still committed the scoped
folder synchronization before the complete full-reconciliation preflight. The full-preparation path now performs
physical materialization and verification only, while the ordinary incremental route still requires and invokes its
scoped DB synchronization callback. Preparation-only DB invariance, a later full-preflight failure, and the retained
incremental write route passed 3/3 in
`artifacts/verification/tests-quick-20260904-055548/functional/results.trx`. Final Functional and review remain
pending at that point.

The first Functional after that correction exposed one deterministic stale fixture,
`Lr2SongDbSyncCustomFolderPreparation_RemovesExtraLr2FolderInManagedOutputDirectory`, which still required the
retired preparation-time DB prune. It was replaced with a durable row-preservation assertion while keeping physical
extra-file cleanup coverage. The correction and prior three cases passed 4/4 in
`artifacts/verification/tests-quick-20260904-060134/functional/results.trx`. The final corrected snapshot then passed
Functional with 2,802 tests passed and 8 existing skips in 170.5 seconds at
`artifacts/verification/tests-functional-20260904-060222`. The final fresh static review confirmed the
full-preparation P1 was resolved and reported no P0, P1, acceptance-blocking P2, out-of-scope issue, theoretical
issue, or recommendation.

## Replan triggers

Stop and return to the root if implementation evidence requires any of the following:

- changing status schema/signature/generator/app-schema versions or rerunning matching `Completed`;
- persisting/reusing/reverifying the receipt or changing its identity/origin/eligibility;
- resuming from cursor, partially applying folders before complete preflight, or adding fallback/retry/replay machinery;
- changing folder priority, preserving foreign folder rows, or making incremental playlist/settings source-only;
- allowing full sync to add/delete song membership or change preserved user columns;
- inventing a private/fake-only production route to make a test executable;
- creating an ownership conflict outside the paths and contracts above.
