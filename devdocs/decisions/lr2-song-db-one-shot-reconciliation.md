# LR2 song.db one-shot reconciliation

Status: Accepted
Date: 2026-09-04

## Context

LR2 `song.db` synchronization previously mixed durable positional resume, file-diff skip
verification, split folder mutations, startup diagnostics/repair, and full-sync song
membership pruning. Those routes made a completed status depend on incomplete or
heuristic observations and allowed a later run to apply a cursor or stale folder
assumption to different input.

The supported ingress is the startup/reload workflow, status retry, settings/manual
force, file-diff completion, and coordinated application shutdown. The resulting
database rows and durable status are user-visible and must converge without hiding
incomplete discovery or parse failures.

## Decision

Full LR2 reconciliation is one fresh, uninterrupted projection of the current input.
Every non-`Completed` run and every forced run starts at item zero; the persisted
`processed_cursor` remains a compatibility/progress field and is never a resume
instruction. Progress is published only after the corresponding durable commit.

The file-diff pipeline may publish an in-memory committed-path receipt guarded by
`BmsRowsVersion` as a transitional narrow check. `OwnedChartCollectionVersion` is not a
receipt-validity dependency. A receipt contains only paths for which the
full-sync-equivalent work, inline maintenance, matching LR2 row write, and SQLite commit
succeeded. The immediate startup/reload follow-up may take a matching receipt exactly
once. Eligible paths skip the BMS reader and database currentness verification. The
receipt is not persisted, reused, reverified, or restored after failure, retry,
manual/settings execution, shutdown, disposal, or a BMS-row version mismatch.

The captured scan surface is guarded by its positive generation, BMS/BMSON storage-row
versions, BMS roots, and LR2 folder discovery roots. It deliberately does not use
`OwnedChartCollectionVersion` for selection or current-surface validation. The
runtime-wide input-currentness query still uses `OwnedChartCollectionVersion`, so an
input captured before an owned-collection mutation remains stale/non-successful even
when the narrow scan surface remains reusable.

Full folder preparation builds the complete normal-directory, `folderinfo.txt`,
discovered/application-managed `.lr2folder`, built-in custom-folder, and required
parent/root projection before database mutation. Source priority is built-in custom
folder > `.lr2folder` > `folderinfo.txt` > normal directory. Equal projections
deduplicate; unequal same-tier projections fail deterministically. A complete
projection is applied by one whole-table transaction. Incomplete discovery, metadata,
parse, or preflight failure performs zero folder mutation.

Full mode reparses `.lr2folder` content even when mtime matches and treats zero and
negative Unix-second dates as ordinary generated values. Full preparation for playlist
and built-in surfaces performs physical materialization/verification only. Existing
playlist/settings/catalog scoped incremental folder DB synchronization remains the
owner of those incremental mutations.

Full reconciliation updates generated song columns from the current projection while
preserving LR2 user columns such as `favorite`, `adddate`, and `tag`. It does not add,
delete, or stale-prune `song` membership; file-diff continues to own membership and
its app-managed maintenance/chart-digest cleanup.

Startup blocker diagnostics, missing/unknown-root inference, date-sentinel checks,
repair, cleanup/retry UI, durable resume/verifier routes, and the LR2 user cancel
route are retired. `Running`, `Failed`, `Incomplete`, and legacy `Cancelled` values
remain retryable from zero; legacy `Cancelled` remains parseable/displayable. New
production runs do not write `Cancelled`. Shutdown cancellation rolls back the active
transaction and records `Incomplete` with `shutdown_interrupted` when status
persistence is safely available; unexpected non-shutdown cancellation is a failure.

The `lr2_song_db_sync_status` schema, `name=default` row, status signature and
generator/app-schema declarations remain unchanged for compatibility.

## Consequences

- A complete folder table is deterministic and atomic, and incomplete input cannot
  silently prune rows.
- The only cross-run optimization is the immediate, one-shot in-memory receipt with its
  transitional BMS-row guard; the captured surface additionally keeps generation, row,
  and root guards. There is no replay, recovery cursor, manifest, or database
  revalidation machinery.
- File-diff remains the single owner of song membership and scoped incremental folder
  synchronization, reducing overlap between startup and user actions.
- A failed or incomplete run is visible through existing status/retry behavior and
  must be rerun from zero.
- Existing user-owned song values and persisted status/schema compatibility are
  preserved.

## Verification

The implementation is covered by the approved packet
`B-6-D-16-LR2-ONE-SHOT-RECONCILIATION-20260904`, including `RCP-01..04`,
`FDR-01..05`, `SON-01..03`, `RTR-01`, `UI-01`, `SHD-01`, and `SCH-01`.
Focused Quick results and the integrated verification ledger are recorded in the
temporary execution plan.
