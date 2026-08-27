# App-wide UI canonicalization plan

Status: complete (`APP-UI-CANON-01-REVIEW-FIX-01`)

Base revision: `b53e792d7d64da9a05af22180d0553de12415a28`

Test Contract Packet: `APP-UI-CANON-01` (approved)

## Goal

Promote the design language first established by Settings into an application-level canonical control system, then adopt it in Settings and every app-rendered custom dialog without changing feature state, persistence, dialog ownership, or operation semantics.

## Decisions

- Canonical control styles and templates are application-level explicit resources. This unit does not implicitly restyle the whole `MainWindow`.
- Settings-only generic control templates are retired. Settings-specific presentation controls and compatibility style keys may remain as aliases over the canonical resources.
- Required custom-dialog scope is SettingsWindow, ReleaseNotesWindow, Lr2AdvancedPathsDialog, UpdateAvailableDialog, PendingDeleteConfirmDialog, PlayHistoryFolderDisplayPresetEditDialog, Lr2PlayHistorySchemaUninstallDialog, ProgressDialog, ThemedMessageBox, InitialSetupLanguageDialog, LoadPlaylistURIDialog, PlaylistPropertyDialog, and PlaylistSummaryBulkEditDialog.
- OS file/folder pickers and EmergencyDialog remain outside the custom-dialog design system.
- ComboBox popup width exactly follows the displayed ComboBox width when opened and after resizing/reopening.
- PlayerLatency keeps its current value semantics: initial `0.0`, updated only by an applicable successful device test.
- MessageBox button copy is not changed in this work.
- Implementation-unit commits are authorized by the user. Commits must contain only the completed unit paths.

## Approved test contract

### `UI-DS-001A` — canonical resource ownership

- Settings and custom dialogs that adopt the same semantic role resolve to the same application-level explicit canonical style/template.
- Key names, file names, BasedOn mechanics, margins, and radii are allowed to vary.
- Wrong variants that must fail: copying Settings templates into each dialog, keeping the canonical owner Settings-local, or sharing only brushes while retaining unrelated templates.
- Coverage: new `DialogPresentationTests`; replace the obsolete Settings-only closed-system assertions.

### `UI-DS-001B` — bounded adoption

- Introducing canonical resources must not implicitly apply the dialog control system to unadopted MainWindow controls.
- Wrong variant: application-level implicit type styles that restyle MainWindow globally.
- Coverage: `DialogPresentationTests` plus the relevant Settings control-system replacement test.

### `UI-CBX-001` — exact popup width

- For editable and non-editable Settings ComboBoxes, the opened popup presentation root width equals the ComboBox ActualWidth in DIPs.
- After closing, resizing, and reopening, the popup follows the new width.
- PART_Popup, editable text/caret, full-surface hit testing, keyboard selection, and Automation Expand/Collapse remain functional.
- Wrong variants: item-sized, fixed-width, MinWidth-only, cached-first-width, or editable-only implementations.
- Coverage: extend the existing exact-foreground `SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes` method without adding another foreground allow-list entry.

### `UI-SET-001A` — one status glyph owner

- A nonblank semantic Settings status presents exactly one visual glyph for its meaning.
- Automation Name is the message, ItemStatus is the semantic status, and the decorative glyph is not a separate Automation element.
- Wrong variants: prefixing the message with the banner glyph, removing all glyphs, or including the glyph in Automation Name.
- Coverage: extend `SettingsWindowPresentationTests`; do not add exact localized-copy assertions.

### `UI-SET-001B` — LR2 feature grouping

- LR2 song.db resync and play-history schema status/install-repair have distinct nearest semantic group owners with distinct nonblank headings.
- Existing click routes, availability, Settings close behavior for manual resync, and schema operation gate remain unchanged.
- Coverage: extend `SettingsWindowCompiledBehaviorTests`; retain existing owner/workflow tests.

### `UI-SET-001C` — audio measurement grouping

- The localized latency field and test playback/latency action share one nearest semantic group that is neither the advanced-control group nor the volume group.
- ja-JP renders the label `レイテンシ`.
- PlayerLatency value semantics remain unchanged.
- Coverage: extend `SettingsWindowCompiledBehaviorTests`; retain `SettingDialogEditCompletionTests` audio value coverage unchanged.

### `UI-SET-001D` — independent play-history preset section

- The preset editor is not a descendant of the MD5 URL mapping section and is a descendant of its own localized feature-heading section.
- Coverage: extend `SettingsWindowCompiledBehaviorTests`.

### `UI-SET-001E` — localization parity

- New semantic heading/label resources exist, are strings, and are nonblank in the default resources and all six JSON languages.
- UI uses resource binding rather than fixed copy.
- Coverage: extend `LocalizationResourceParityTests` without copying all translations into test code.

### `UI-DLG-001A` — custom-dialog visual grammar

- Every in-scope custom dialog explicitly adopts the canonical surface/content/action/control roles.
- Affirmative/default, quiet cancel/close, and explicitly destructive actions share the corresponding canonical state model.
- Native-window versus overlay form, feature-specific layout, size, and content may vary.
- Wrong variants: leaving one scoped dialog on a legacy/local template, copying the Settings dictionary, sharing only brushes, or including EmergencyDialog.
- Coverage: new data-driven `DialogPresentationTests` using `TestUiDispatcherHost` and `TestWindowPresentationScope` only when presentation is required.

### `UI-DLG-001B` — route/result/lifetime preservation

- Owner, dispatcher, modal-versus-overlay route, result/default result, close/cancel, operation gate, ViewModel, persistence, and lifetime contracts remain unchanged.
- ProgressDialog busy-close behavior and ThemedMessageBox close-without-selection behavior remain unchanged.
- Coverage: retain `UiDialogCoordinatorWpfTests`, `ThemedMessageBoxTests`, `NativeWindowThemeContractTests`, and feature-specific dialog tests without weakening their expected semantics.

## Implementation units

### Unit 1 — canonical resources and ComboBox

Observable outcome:

- Application-level explicit canonical controls exist.
- Settings generic controls resolve through that canonical owner.
- Unadopted MainWindow controls are not implicitly restyled.
- Settings ComboBox popups exactly track displayed width.

Owned paths:

- `BeMusicSeeker/App.xaml`
- new canonical resource dictionaries under `BeMusicSeeker/Themes/`
- `BeMusicSeeker/Views/Settings/SettingsControls.xaml`
- `devdocs/spec/appearance-theme.md`
- `BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs`
- `BeMusicSeeker.Tests/SettingsForegroundInteractionTests.cs`
- new `BeMusicSeeker.Tests/DialogPresentationTests.cs` only for the canonical resource ownership/bounded-adoption foundation
- this plan

Verification:

- Base-fail/head-pass or targeted negative controls for `UI-DS-001A`, `UI-DS-001B`, `UI-CBX-001`.
- Quick filters for Settings presentation/foreground and DialogPresentationTests.

Replan if canonical resources require application-level implicit type styles, standard WPF parts cannot be retained, or another project cannot resolve the application resources without changing observable ownership.

### Unit 2 — Settings semantics and localization

Observable outcome:

- Status glyph ownership, LR2 grouping, audio measurement grouping, and independent preset section match `UI-SET-001A` through `UI-SET-001E`.

Owned paths:

- `BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml`
- `BeMusicSeeker/Views/Settings/Pages/AudioSettingsPage.xaml`
- `BeMusicSeeker/Views/Settings/Pages/PlaylistSettingsPage.xaml`
- `BeMusicSeeker/Properties/Resources.resx`
- `BeMusicSeeker/Properties/Resources.cs`
- `lang/*.json`
- `devdocs/spec/settings-change-impact-and-startup-operations.md`
- `devdocs/spec/audio-runtime-phase1.md` only if the presentation contract needs an explicit placement note
- `BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs`
- `BeMusicSeeker.Tests/SettingsWindowCompiledBehaviorTests.cs`
- `BeMusicSeeker.Tests/LocalizationResourceParityTests.cs`

Verification:

- Base-fail/head-pass or targeted negative controls for `UI-SET-001A` through `UI-SET-001E`.
- Retain existing LR2 resync, schema, preset draft, and audio value tests unchanged unless fixture mechanics require an authority-preserving adjustment.

Replan if layout changes require ViewModel/persistence semantics, PlayerLatency needs a measured/unmeasured state, or resource parity cannot be satisfied with meaningful translations.

### Unit 3 — custom-dialog adoption

Observable outcome:

- Every in-scope custom dialog uses the canonical visual roles while `UI-DLG-001B` remains unchanged.

Owned paths:

- in-scope dialog/window/overlay XAML and code-behind under `BeMusicSeeker/Views/`
- `Parago/Windows/ProgressDialog.xaml` and presentation-only code if required
- `BeMusicSeeker/Views/ThemedMessageBox.cs` and a concrete XAML view if selected
- `BeMusicSeeker.Tests/DialogPresentationTests.cs`
- dialog-specific tests only when fixture mechanics require an authority-preserving adjustment
- `devdocs/spec/appearance-theme.md` dialog-adoption completion record

Verification:

- Head-pass and legacy-style targeted negative control for `UI-DLG-001A`.
- Existing route/result/lifetime fixtures remain green for `UI-DLG-001B`.

Replan if visual adoption requires converting overlays to windows, changing coordinator routes/results, inferring danger semantics from copy/icon, or altering progress/dialog lifetime.

### Unit 4 — integration and review

- Run affected filtered Quick tests.
- Run one final Functional lane according to `testing-strategy.md`.
- Inspect light/dark, ja-JP and a long-label locale, Settings minimum size, ComboBox open/resize/reopen, and representative modal/overlay dialogs from the repository executable.
- Close the application and UI-control session without leaving a process behind.
- Freeze the worktree and run a fresh read-only `repo-static-review` with this packet, base/head, commits, verification, and negative-control evidence.
- Address blocking findings, reverify the affected scope, and use a fresh reviewer for any fix snapshot.

## Coverage safety

- WPF tests use `TestUiDispatcherHost` and `TestWindowPresentationScope`; no fixed wait, raw dispatcher pump, physical cursor, or unexplained new `DoNotParallelize`.
- Exact foreground method count is not increased for ComboBox coverage.
- Completion is tied to Loaded/ContentRendered, Popup.Opened/IsOpen, dispatcher DataBind completion, or the existing typed task/result seam.
- Source/XAML text is not the oracle unless an application resource key itself is the public adoption seam; broad snapshots and exact translation-copy assertions are not added.

## Completion record

- Review-fix amendment: `APP-UI-CANON-01-REVIEW-FIX-01` (focused implementation unit complete; Unit 4 integration/review remains root-owned).

### Approved review-fix Test Contract Packet

The packet authority is the root-approved review-fix request. The following
contracts freeze the observable outcome, allowed variation, and negative
control before implementation details are considered:

| Contract ID | Required outcome | Allowed variation | Plausible wrong implementation |
| --- | --- | --- | --- |
| `UI-DLG-001A-PROGRESS-FIT` | `ProgressDialog(showSubLabel:true, showCancelButton:true)` lays out body, sub-label, progress, and enabled/hit-testable cancel inside the client/content bounds; rendered height satisfies its minimum. | Content text, wrapping, and non-semantic row mechanics may vary; no fixed height is required. | Retain the legacy fixed height so a long sub-label clips or overlaps the progress row. |
| `UI-DS-001A-HOST` / `UI-DLG-001A-RUNTIME` | The 13 required production dialog/window surfaces are constructed through production constructors/factories and apply the canonical role plus effective canonical template on the same WPF host. | Native-window versus overlay form, feature layout, dimensions, role aliases, and visual-tree implementation may vary. | Keep a legacy/local template, copy Settings templates, or inspect a disconnected fixture instead of the production visual tree. |
| `UI-DS-001B-SENTINEL` | A host-level sentinel style remains isolated from the dialog subtree while canonical roles are applied to dialog controls. | Sentinel content and test wrapper mechanics may vary. | Let dialog resource adoption leak into the outer host or rely on an application-wide implicit type style. |
| `UI-DLG-001A-MSGBOX-SEAM` | The presentation test uses the same narrow construction builder as `ShowWithStatus`; the close/result route remains unchanged. | The internal method name and callback mechanics may vary. | Reconstruct a test-only message box or maintain a second builder that can drift from production. |

The runtime test intentionally does not use cross-load dictionary identity,
source text, geometry snapshots, child order, copied source, or broad snapshot
assertions as an oracle. The Progress test is the only content-fit geometry
assertion because bounds containment is its explicit observable contract.

### Coverage and evidence

- `DialogPresentationTests.CustomDialogScope_UsesAppliedCanonicalRolesAndKeepsOuterSentinelIsolated` is a `replace` of the former source/XML oracle and covers exactly the 13 surfaces listed in the Decisions section. Its production fixture uses `TestUiDispatcherHost`; completion is `ContentRendered` through the existing `TestWindowPresentationScope` signal.
- `DialogPresentationTests.ProgressDialog_WithSubLabelAndCancel_RendersContentInsideItsClientBounds` is `new` coverage for `UI-DLG-001A-PROGRESS-FIT`. `SettingsWindowPresentationTests.SettingsWindow_UsesApplicationCanonicalControlSystemWithExplicitSettingsAdoption` and its source/XAML-only helpers are retired; the remaining foreground, sentinel, interaction, and measured-popup tests stay in place.
- Focused Dialog-only Quick: `artifacts/verification/tests-quick-20260827-141027/functional/results.trx` — 14/14 passed (13 data rows plus the Progress fit test).
- Required bounded Quick: `artifacts/verification/tests-quick-20260827-141108/functional/results.trx` — 109/109 passed for `DialogPresentationTests|SettingsWindowPresentationTests|SettingsForegroundInteractionTests|WpfTestApplicationHostTests|UiDialogCoordinatorWpfTests|ThemedMessageBoxTests|NativeWindowThemeContractTests`.
- Targeted negative control for the legacy fixed-height Progress implementation: `artifacts/verification/tests-quick-20260827-135609/functional/results.trx` — 1/1 intended failure from rendered sub-label overflow; the mutation was reverted.
- Targeted negative control after removing the scoped canonical content role: `artifacts/verification/tests-quick-20260827-135708/functional/results.trx` — 12/13 passed and the intended Progress data row failed with the semantic locator assertion; the mutation was reverted.
- No deviation from the approved packet. Functional and fresh static review are the root-owned Unit 4 follow-up.
