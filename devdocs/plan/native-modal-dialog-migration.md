# Native window and playlist modal migration plan

Status: active

Base revision: `d7be48f5`

Test Contract Packet: `APP-UI-NATIVE-MODAL-02` (approved)

## Goal

Restore single-owner native window framing and content-fit sizing, then migrate Playlist Property and Playlist Summary Bulk Edit from MainWindow overlays to resizable, MainWindow-owned modal windows without changing their editing, persistence, or failure semantics.

## Decisions

- `ThemedWindow` owns an independent window's outer client surface and native border. Rounded `DialogSurfaceStyle` remains an overlay-panel role.
- Native content may own internal spacing but must not wrap the whole window in an additional rounded/bordered surface.
- Explicit initial-size exceptions are exactly SettingsWindow, PlaylistPropertyDialog, and PlaylistSummaryBulkEditDialog. The three remain user-resizable and have positive minimum sizes.
- Other independent custom windows use content-driven or bounded-scroll initial sizing so wrapped/localized content remains reachable.
- Playlist Property visible update text remains exactly `Update: yyyy/MM/dd` with no time.
- Playlist Property and Summary Bulk Edit are fresh `ThemedWindow` instances shown modally with MainWindow as owner. MainWindow embedded instances and overlay routes are retired.
- Property Save closes only after `Completed`. Cancel, Esc, and native close share reset/rollback and close only after `Completed`; Busy, validation failure, and faults keep the window open.
- Bulk Edit keeps six immediate-apply groups and stays open after each successful apply. Ordinary close is suppressed while an apply is running.
- Playback visibility restoration, Property folder-path refresh, active dialog/session cleanup, provisional playlist rollback, and forced owner-shutdown close remain explicit lifetime responsibilities.

## Approved test contract

- `NMS-01`: native windows have one outer surface; retained overlays keep canonical overlay/dialog-surface roles.
- `NMS-02`: non-exception native windows contain or make all semantic content/actions reachable at initial presentation. LR2 schema uninstall is the required base-fail regression.
- `NMS-03`: Settings, Property, and Bulk have explicit finite initial and minimum dimensions, `ResizeMode.CanResize`, owner-centered/taskbar-hidden presentation, and usable minimum-size layouts. Exact pixels are allowed to vary.
- `MOD-01`: Property and Bulk are fresh MainWindow-owned modal windows; MainWindow is disabled and child dialogs resolve to the active modal.
- `PROP-01`: Property retains the approved field inventory and availability conditions, exactly three top navigation sections, and no GroupBox framing.
- `PROP-DATE-01`: a `2024-01-02 03:04:05` value renders exactly `Update: 2024/01/02`.
- `PROP-02`: Save/reset completion gates every close route and suppresses duplicate pending operations.
- `PROP-03`: provisional playlist rollback and active dialog/session/DataContext cleanup occur exactly once on every terminal or presentation-failure path.
- `BULK-01`: all six immediate-apply groups remain and successful apply does not close the window.
- `BULK-02`: Close/Esc/X discard unapplied drafts, are suppressed during apply, and cleanup after the operation reaches a terminal state.
- `MOD-02`: modal lifetime preserves playback restoration, Property folder-path refresh, forced owner shutdown, and exactly-once cleanup.

The only exact-copy exception is `PROP-DATE-01`. Screenshots, current geometry, child order, padding/radius values, current translations, XAML source text, and cross-load CLR identity are not oracle authority.

## Unit 1 — native surface and sizing correction

Observable outcome:

- Settings shell/sidebar reaches the native client edge while feature-owned inner spacing remains.
- Native windows no longer draw an inner rounded outer frame.
- LR2 schema uninstall, Release Notes, and Play History Preset Edit use content-fit or bounded-scroll sizing and expose all terminal content/actions.
- Retained MainWindow overlays keep rounded canonical surfaces.

Writable ownership:

- canonical dialog styles and affected native-window XAML/code
- `appearance-theme.md`
- relevant presentation tests (`DialogPresentationTests`, `WpfTestApplicationHostTests`, Settings presentation coverage)

Coverage:

- replace the all-dialog outer-surface assumption with native/overlay branches
- extend rendered containment/reachability for LR2 uninstall and the remaining non-exception native windows
- extend Settings client-edge behavior without exact pixel assertions
- base-fail for the native rounded wrapper and LR2 fixed-height clipping; head-pass plus targeted negative controls

Verification:

- focused Quick for dialog, Settings, WPF host, theme, LR2 schema UI, and preset/release presentation fixtures
- `git diff --check`

Implementation evidence:

- Base red (`artifacts/verification/tests-quick-20260827-170459/functional`): 17 focused presentation cases ran; 10 intended semantic failures were recorded (9 native rounded-surface failures for NMS-01 and 1 LR2 lower-content containment failure for NMS-02). The remaining 7 cases passed; compile/setup failures were not counted as red evidence.
- The implementation assigns `NativeWindowContentStyle` as a padding-only native content role, retains `DialogContentStyle` for MainWindow overlays, makes Settings shell edge ownership explicit, and changes Release Notes, Play History Preset Edit, and LR2 uninstall to content-fit or bounded-scroll presentation.
- Focused head-pass (`artifacts/verification/tests-quick-20260827-171047/functional`): 87/87 cases passed for the dialog, WPF host, and Settings presentation scope, including rendered native containment and Settings client-edge assertions.
- Targeted negative controls passed their intended failure checks and were fully reverted: native rounded-wrapper restore failed 1/13 NMS-01 cases (`artifacts/verification/tests-quick-20260827-171624/functional`); LR2 fixed `Height=320` / `MinHeight=300` failed 1/3 NMS-02 cases (`artifacts/verification/tests-quick-20260827-171727/functional`); Release Notes no-scroll (`VerticalScrollBarVisibility=Disabled`) failed 1/3 bounded-scroll cases (`artifacts/verification/tests-quick-20260827-171841/functional`).
- Final focused Quick (`artifacts/verification/tests-quick-20260827-172239/functional`): 98/98 passed across `DialogPresentationTests`, `WpfTestApplicationHostTests`, `SettingsWindowPresentationTests`, `Lr2PlayHistorySchemaUiTests`, and `NativeWindowThemeContractTests`.

## Unit 2 — playlist modal windows and date compatibility

Observable outcome:

- Property and Bulk are independent resizable modal windows owned by MainWindow.
- Property uses horizontal three-section navigation and Settings-like heading/content; all existing bindings and operation conditions remain.
- Bulk retains six immediate-apply sections and safe close gating.
- embedded overlay instances/routes are removed and shell lifetime cleanup is explicit.
- historical visible date formatting is restored.

Writable ownership:

- both dialog XAML/code-behind and only necessary ViewModel/lifetime seams
- MainWindow XAML/code and dialog composition/owner helpers as required
- affected playlist/dialog tests
- `appearance-theme.md`, `dialog-route-inventory.md`, `playlist-data-and-export-flow.md`, and user documentation where the route/layout changed

Coverage:

- actual-window semantic binding and layout tests for all Property fields and six Bulk operations
- actual MainWindow modal owner/disable/child-owner/fresh-instance behavior
- save/reset/apply completion-controlled lifetime tests and presentation-failure rollback
- playback/folder refresh and forced shutdown cleanup
- visible date regression using the exact compatibility exception
- retire embedded overlay hosts, type branches, close forwarding, and overlay-only test mechanics

Verification:

- focused Quick for playlist property/bulk, MainWindow workspace, coordinator/modal, dialog presentation, native window theme, and localization parity if resources change
- `git diff --check`

Implementation evidence:

- Property and Summary Bulk Edit now construct fresh `ThemedWindow` instances through `UiDialogCoordinator.ShowWindowAsync`; the MainWindow retains only the Initial Setup and Load Playlist URI overlay hosts.
- Property field availability and the three General/Folder/Custom Folder navigation sections remain rendered through the existing bindings, while both dialogs use `SettingsSection` content and bounded scrolling at explicit resizable minimum sizes.
- The rendered date regression first failed against raw `DateTime` output (`1/2/2024 3:04:05 AM`), then passed after moving the compatibility format onto the rendered `TextBlock`: `Update: 2024/01/02`. A raw-date targeted mutant failed the same actual-window assertion and was reverted.
- Focused Quick (`artifacts/verification/tests-quick-20260827-175735/functional`): 28/28 passed for dialog presentation and playlist property/bulk fixtures. Combined focused Quick (`artifacts/verification/tests-quick-20260827-182541/functional`): 53/53 passed across dialog presentation, playlist property/bulk, WPF host, MainWindow playlist workspace, `UiDialogCoordinator`, and native-window theme fixtures. The WPF host semantic-brush case was updated from the retired property overlay surface to the native content role.
- The first cache-cold focused run reached 28/28 test-body passes but the runner rejected a stale untracked hang-dump XML during whitespace verification; the diagnostic process and generated `BeMusicSeeker.Tests/TestResults` tree were removed, and the clean rerun above passed the runner fingerprint/whitespace checks.

## Review amendment evidence (`APP-UI-NATIVE-MODAL-02`)

- MainWindow owner shutdown now defers the terminal application request until active Property/Bulk operations reach terminal completion, owner-forced close has returned, DataContext detachment has completed, and the route cleanup task has completed. Repeated shutdown requests remain idempotent, while ordinary close gating and operation failure visibility are preserved.
- Property and Summary Bulk Edit use the native content padding owner without an additional root margin. The retained overlay contract remains limited to `InitialSetupLanguageDialog` and `LoadPlaylistURIDialog`.
- Actual MainWindow modal lifetime coverage was added for owned/disabled fresh dialogs, Property reset and reopen cleanup, Bulk successful apply, and shutdown ordering. The focused two-test head pass is recorded at `artifacts/verification/tests-quick-20260827-200024/functional` (2/2).
- The targeted wrong-order mutant that requested terminal shutdown before awaiting the active Bulk apply failed the shutdown ordering oracle at `artifacts/verification/tests-quick-20260827-195942/functional`; the mutant was reverted before the head pass.
- Final amendment focused Quick (`artifacts/verification/tests-quick-20260827-200333/functional`): 116/116 passed across MainWindow playlist workspace, Playlist Summary Bulk Edit, dialog presentation, WPF host, `UiDialogCoordinator`, and Settings presentation fixtures.
- Fresh-review test-safety fixes removed foreground activation and child-ordinal lookup from the actual MainWindow route coverage, made dispatcher observation terminally cancellable, and use the coordinator's pre-`ShowDialog` modal-scope seam to prepare owned child windows as non-activating. The corrected two-test scope passed 2/2 (`artifacts/verification/native-modal-pre-show-2test/results.trx`), the neighboring MainWindow scope passed 13/13 (`artifacts/verification/tests-quick-20260827-211038/functional`), and the focused aggregate passed 116/116 (`artifacts/verification/tests-quick-20260827-211121/functional`).

## Integration and acceptance

- Combined focused Quick passed 125/125 on the first integrated snapshot (`artifacts/verification/tests-quick-20260827-183229/functional`). The shutdown-handshake amendment then passed its final 116/116 focused scope (`artifacts/verification/tests-quick-20260827-200333/functional`).
- Exact repository-executable inspection confirmed Settings client-edge framing, complete LR2 uninstall content, resizable owned Property/Bulk modal windows, Property horizontal navigation and `Update: yyyy/MM/dd` display, and all six scroll-reachable Bulk sections. Inspection was cancelled without persisting changes and the repository process was closed.
- Final post-review Functional verification passed with 2,657 successful tests, 0 failures, and 8 skipped out of 2,665 tests in the canonical aggregate (`artifacts/verification/tests-functional-20260827-211332`).
- The final committed snapshot is frozen for fresh static review against `APP-UI-NATIVE-MODAL-02` and the review-amendment findings.

## Replan triggers

- a new public dialog API or persistence ownership move outside MainWindow is required
- the existing WPF host cannot deterministically present/close nested modal windows
- save/reset/apply completion cannot be controlled without copying production logic
- any proposal changes the exact date format, field/group inventory, close-on-failure behavior, provisional rollback, persistence meaning, or operation-owned state lifetime
- another fixed-size exception or a fallback dialog route becomes necessary
