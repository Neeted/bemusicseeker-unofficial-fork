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

## Integration and acceptance

- Run the combined focused Quick scope after both units.
- Launch the exact repository executable and inspect Settings edge framing, LR2 uninstall containment, both playlist modal windows, minimum-size scrolling, modal owner disablement, and date display. Cancel without persisting inspection changes and close the process.
- Run one final `verify-refactor.ps1 -Mode Functional` on the integrated snapshot.
- Freeze the worktree and request a fresh static review against this packet.

## Replan triggers

- a new public dialog API or persistence ownership move outside MainWindow is required
- the existing WPF host cannot deterministically present/close nested modal windows
- save/reset/apply completion cannot be controlled without copying production logic
- any proposal changes the exact date format, field/group inventory, close-on-failure behavior, provisional rollback, persistence meaning, or operation-owned state lifetime
- another fixed-size exception or a fallback dialog route becomes necessary

