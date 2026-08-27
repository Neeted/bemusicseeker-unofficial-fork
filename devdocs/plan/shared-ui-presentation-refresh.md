# Shared UI presentation refresh

Status: approved for implementation
Base revision: `e65682392595e5c1af16d838f5c61ef25942107b`
Test Contract Packet: `APP-UI-SHARED-PRESENTATION-03`

## Goal

- Promote the thin MainWindow scrollbar presentation into the canonical application control system and apply it to shared UI consumers without changing scrolling semantics.
- Restore the legacy Release Notes typography and strict WPF-default right-click behavior.
- Make the shared native-window spacing role the sole outer-spacing owner, except for the Settings edge shell and overlay surfaces.
- Open Summary Bulk Edit at its existing minimum width while keeping it resizable.
- Keep the Load Playlist URI input chrome inside its overlay content area and above the footer.
- Rebuild Playlist Property around a WinUI-inspired three-item top navigation while preserving its field, draft, availability, save/reset, failure, shutdown, and cleanup behavior.

## Decisions

- Release Notes follows the current .NET 10 WPF default context-menu behavior: Copy / Select All are provided by the framework and the app does not install a custom document menu. The earlier legacy Cut / Copy / Paste compatibility proposal is explicitly retired.
- Release Notes uses `Meiryo UI` at 12 points. The independent bounded viewport, hidden toolbar, normal body, and bold version headings remain.
- The ordinary native content role is the only outer-spacing owner. Settings remains an edge-shell exception; overlays remain overlay surfaces.
- Summary Bulk Edit keeps its existing minimum width and `CanResize`; its initial width equals that minimum.
- Playlist Property has exactly General, Folder, and Custom Folder top-level categories. General is selected initially, selection is not persisted, and category changes reset the content scroll position.
- Playlist Property navigation state stays in presentation scope. No new ViewModel or settings persistence is added.
- The current Property field/info inventory, binding directions, and availability truth table are approved characterization authority only. Current control types, hierarchy, geometry, child order, and localized copy are not authority.
- After `playlist-data-and-export-flow.md` enumerates the Property contract, the current-XAML characterization exception retires and the spec becomes the durable authority.
- Implementation may be committed in bounded units without additional user approval.

## Test Contract Packet

### Shared scrollbar

- `APPUI-SB-01`: MainWindow, canonical dialogs, Settings, and representative internal TextBox/ListBox/ScrollViewer consumers use the same thin vertical and horizontal scrollbar presentation, including track, thumb, end affordances, theme states, and disabled presentation. Exact dimensions, colors, template wrappers, and resource identity are allowed to vary.
- `APPUI-SB-02`: `PART_VerticalScrollBar`, `PART_HorizontalScrollBar`, `PART_Track`, line/page commands, orientation, visibility, offset/viewport/maximum, Automation, `CanContentScroll`, and disabled behavior remain functional.
- Wrong implementations include changing only MainWindow, leaving canonical internal consumers on the old style, losing Line/Page or horizontal behavior, and changing appearance without working offsets or Automation.

### Release Notes

- `APPUI-RN-01`: one selection-enabled, read-only document viewport uses `Meiryo UI` / 12pt, normal body, bold version headings, hidden toolbar, bounded vertical scrolling, and disabled horizontal scrolling.
- `APPUI-RN-02`: the current .NET 10 WPF-default document menu remains framework-owned, exposes Copy / Select All, and is not replaced by an app-defined menu. Menu captions, popup position, and localized release text are not exact oracles.
- Approved exactness is limited to typography, heading/body weight, the framework Copy / Select All command roles, and absence of an app-defined document menu.

### Window spacing and overlay containment

- `APPUI-SPACE-01`: every ordinary native window has exactly one effective outer-spacing owner. Release Notes, LR2 schema uninstall, preset edit, and LR2 advanced paths are required base-red cases. Inner section spacing is not part of this oracle.
- `APPUI-SPACE-02`: Settings still reaches the native client edge while retaining feature-content spacing. Initial Setup and Load Playlist URI remain overlay surfaces and do not adopt the native spacing role.
- `APPUI-LOAD-01`: at the existing initial overlay size, the URI input chrome is fully contained and does not intrude into the footer under default and text/font-scale variation. The outer size, field, actions, commands, and functions are unchanged. Arbitrarily enlarging the outer overlay is an invalid fix.

### Bulk sizing

- `APPUI-BULK-01`: initial `Width == MinWidth`, the positive minimum is unchanged from base, `ResizeMode.CanResize` remains, and the window can grow and shrink back to the minimum. The durable test does not fix the literal minimum value.

### Playlist Property navigation and compatibility

- `APPUI-PROP-01`: three peer categories are visible in a top navigation area with one selection, General initially selected, separate navigation/header/content roles, selected content only, and a bottom selection indicator. WPF control/template type and indicator dimensions are allowed to vary.
- `APPUI-PROP-02`: Left/Right/Home/End update selection and focus; Selection/SelectionItem Automation remains single-select; category changes reset content scroll; reopening selects General and does not restore or persist a previous category.
- `APPUI-PROP-03`: the following inventory and semantics remain:
  - General: name, symbol, compatible prefix, entry type/list, external sync, Page/Header/Data URLs, update date.
  - Folder: sort key/list, ascending/descending, extended folder order, automatic sorting, move up/down and natural-sort actions.
  - Custom Folder: output base, output directory, root flag, and AllSongs/User/Level/Alphabet/Clear/DJLevel/CategoryAll/Other/Random/BpmSort/BpSort/PlayCountSort/LastPlaySort outputs, plus their semantic help/tooltips.
  - Editable values remain bidirectional draft bindings; lists/info remain owner-to-view.
  - External sync, `OperationModeLR2DB`, automatic-sort, and Level/File-entry availability gates retain their current approved truth table.
- `APPUI-PROP-04`: category switching preserves drafts and does not save, reset, recreate, or persist navigation state. Existing save/cancel/reset, failure/retry, owner-shutdown waiting, DataContext/session cleanup, and active-dialog cleanup remain unchanged.

## Test coverage ledger

| Contract | Production owner | Coverage | Decision | Shared resource / lane | Completion signal | Retired coverage |
| --- | --- | --- | --- | --- | --- | --- |
| `APPUI-SB-01/02` | canonical/Simple control resources | `SettingsForegroundInteractionTests`, `MainWindowChartPresentationWpfTests`, `MainWindowContextMenuResourceTests` | replace source/numeric assertions and extend materialized behavior | existing WPF host; existing `serial-state-a` foreground method and `serial-state-b` MainWindow route | materialization plus routed command/offset/Automation transition | Simple scrollbar source-copy tests after semantic replacement |
| `APPUI-RN-01/02` | `ReleaseNotesWindow` | `DialogPresentationTests` | extend | existing DNP WPF fixture | `ContentRendered`, deterministic explicit-hit/menu-open signal | ReleaseNotes source fragment in Settings test when replaced |
| `APPUI-SPACE-01/02` | canonical dialog surface and native roots | `DialogPresentationTests`, existing Settings edge test | extend/replace helper | existing WPF fixtures | rendered owner chain/client edge | style-root-only spacing assertion |
| `APPUI-LOAD-01` | `LoadPlaylistURIDialog` | `LoadPlaylistURIDialogTests` | extend | `serial-state-b`, existing presentation scope | `ContentRendered` and rendered containment | none |
| `APPUI-BULK-01` | `PlaylistSummaryBulkEditDialog` | `PlaylistSummaryBulkEditTests` | extend | existing WPF host | constructed/displayed sizing transition | none |
| `APPUI-PROP-01/03` | Property view/navigation/content | `PlaylistSummaryBulkEditTests` | replace/extend semantic cases | existing WPF host | rendered selection/content/binding state | no TabControl/template/source contract; fold narrow flag/date cases into semantic coverage where appropriate |
| `APPUI-PROP-02` | canonical top-nav primitive and Property integration | existing Settings foreground method, `PlaylistSummaryBulkEditTests`, `MainWindowPlaylistWorkspaceWpfTests` | extend | existing allowlisted foreground method; no new foreground FQN | routed key/Automation/scroll transition and modal reopen signal | none |
| `APPUI-PROP-04` | Property/MainWindow/workspace owners | existing MainWindow/workflow/output fixtures | extend draft/reopen only; preserve lifecycle tests | existing routes | existing save/reset/terminal cleanup tasks/events | none |

## Evidence rules

- Use base-red/head-pass for current scrollbar mismatch, native double-spacing, Bulk width mismatch, Load URI containment, Release Notes font regression, and legacy Property navigation mismatch when the public/materialized seam can reproduce them.
- Use targeted mutants for already-green scrolling mechanics, WPF-default menu ownership, Settings/overlay exceptions, top-navigation keyboard/Automation/scroll reset, Property binding/availability/draft retention, and any base-inexpressible new primitive.
- The Release Notes menu test may use keyboard/context-menu public seams, but it must not move or inspect the physical cursor, mutate the clipboard, use private reflection, or assert popup coordinates.
- Do not add pixel, screenshot, current translation, child-order, source-text, private-reflection, broad snapshot, or current-output assertions.
- The one-time Bulk base/head differential may record the base minimum, but durable assertions remain relational.
- The one-time Load overlay base/head differential may record the outer requested size, but durable assertions verify containment and unchanged function rather than literal geometry.

## Implementation units

### Unit A — canonical ScrollBar and top-navigation primitives

Observable outcome:

- Canonical controls and MainWindow share one scrollbar visual primitive while keeping their existing ScrollViewer/control templates and behavior.
- A keyed horizontal top-navigation primitive provides single selection, bottom indicator, focus states, keyboard/Automation behavior, and a separate content host contract for Unit C.

Writable ownership:

- `BeMusicSeeker/Themes/CanonicalControls.xaml`
- `Simple Styles.xaml`
- `BeMusicSeeker/App.xaml` only if resource order requires it
- scrollbar/top-navigation presentation tests listed above
- `devdocs/spec/appearance-theme.md`

Verification:

- focused Quick for Settings foreground/materialization, MainWindow chart presentation, and context-menu resource replacement
- base-red or targeted mutants for canonical mismatch, Line/Page/horizontal/disabled behavior, and top-nav focus/Automation
- `git diff --check`

Unit A evidence (working-tree implementation):

- `APPUI-SB-01/02`: the canonical vertical/horizontal templates are consumed by `App.Canonical.ScrollBarStyle` and the `SimpleScrollBar` compatibility/implicit route. The materialized ScrollViewer, Settings, and CustomTableView fixtures cover `PART_*`, `PART_Track`, orientation, visibility, offset/viewport/maximum, line/page commands, Automation, `CanContentScroll`, theme switching, and disabled presentation.
- `APPUI-PROP-01/02` primitive seam: the keyed top-navigation fixture covers three categories, General initial selection, separate content host, bottom indicator, focus border, single-selection/SelectionItem Automation, Left/Right/Home/End keyboard movement, and disabled presentation. Property consumer adoption remains Unit C.
- Focused head-pass artifacts: Settings `tests-quick-20260828-014242`, Chart `tests-quick-20260828-015107`, canonical ScrollViewer `tests-quick-20260828-015326`, and top navigation `tests-quick-20260828-015559`.
- The Chart WPF host explicitly merges the compiled app dictionaries in application order (`Simple Styles.xaml`, then `CanonicalDialogStyles.xaml`) so the test exercises the same compatibility route as the application.

### Unit B — native/layout/Release Notes repairs

Observable outcome:

- normal native windows have one outer-spacing owner; Settings and overlays keep their exceptions
- Release Notes restores legacy typography and strict WPF-default menu behavior
- Load Playlist URI contains its input above the footer without changing outer size/functions
- Summary Bulk Edit opens at its existing minimum width and remains resizable

Writable ownership:

- the four native dialog XAML roots identified by `APPUI-SPACE-01`
- `ReleaseNotesWindow.xaml`
- `LoadPlaylistURIDialog.xaml`
- `PlaylistSummaryBulkEditDialog.xaml`
- `DialogPresentationTests.cs`, `LoadPlaylistURIDialogTests.cs`, and the Bulk sizing cases in `PlaylistSummaryBulkEditTests.cs`
- `appearance-theme.md` and `settings-change-impact-and-startup-operations.md`

Verification:

- focused Quick for dialog presentation, Settings edge, Load URI, Bulk sizing, and WPF host coverage
- required base-red/head-pass and menu/exception/scale mutants
- `git diff --check`

Unit B evidence:

- Base semantic red `tests-quick-20260828-022021`: 23/30 passed; the seven intended failures covered the four duplicate native margins, Release Notes typography, Bulk initial/minimum width mismatch, and large-font Load URI containment.
- Head combined Quick `tests-quick-20260828-030944`: 31/31 passed; Settings edge `tests-quick-20260828-030901`: 1/1 passed.
- Targeted mutants for a custom Release Notes menu, Georgia typography, disabled selection, duplicate Release Notes margin, native/overlay misclassification, fixed Load URI input/outer heights, and fixed/non-resizable Bulk sizing each failed the intended contract and were restored.

### Unit C — Playlist Property redesign

Dependency: Unit A must be integrated first.

Observable outcome:

- the legacy tab presentation is retired and replaced by the approved three-item top navigation
- all approved fields, binding directions, availability gates, drafts, modal lifecycle, failure, shutdown, and cleanup semantics remain

Writable ownership:

- `PlaylistPropertyDialog.xaml`
- minimal presentation-only code-behind when required for selection/focus/scroll behavior
- Property cases in `PlaylistSummaryBulkEditTests.cs`
- draft/reopen additions in `MainWindowPlaylistWorkspaceWpfTests.cs` only when required
- `devdocs/spec/playlist-data-and-export-flow.md`

Verification:

- focused Quick for Property/Bulk semantic presentation, canonical navigation interaction, and actual modal reopen/lifecycle
- legacy navigation base-red plus keyboard/Automation/inventory/gate/draft targeted mutants
- `git diff --check`

Unit C evidence:

- Base semantic red `tests-quick-20260828-032655`: the legacy property layout failed the three intended top-navigation integration contracts.
- Head combined Quick `tests-quick-20260828-040027`: 24/24 passed across property presentation, actual MainWindow reopen/lifecycle, and generic dialog role validation.
- Targeted mutants for multiple selection, missing selection indicator, missing scroll reset, broken availability gating, and the wrong ordinary-ListBox role each failed the intended contract and were restored.

Static-review amendment evidence:

- The first fresh review found no production P0/P1, but rejected five acceptance-direct test-contract issues: non-allowlisted foreground interaction, durable literal Load URI geometry, concrete Property navigation control types, scrollbar style identity, and incomplete binding-direction coverage.
- The amendment removes those implementation constraints while retaining observable behavior and adds effective TwoWay/OneWay binding-direction checks. A temporary OneWay `is_external_sync` mutant failed the intended oracle and was restored (`tests-quick-20260828-043448`).
- Amendment head Quick `tests-quick-20260828-043649`: 65/65 passed; `git diff --check` passed.
- Second fresh-review amendment replaces Property navigation ListBox/Selector/page-name/template assumptions with public Selection/SelectionItem Automation, single-selection, selected-content, scroll-reset, and draft/reopen observations. ScrollViewer/ScrollBar checks retain materialized behavior and transitions without template/resource identity gates. A temporary wrong-initial-category mutant failed the semantic navigation oracle and was restored (`tests-quick-20260828-051042`); restored head Quick `tests-quick-20260828-051324`: 104/104 passed.

## Dependency, parallelism, and review

- Unit A runs first.
- After Unit A is committed, Unit B and Unit C may run in parallel only with the ownership above: Unit B owns `DialogPresentationTests`; Unit C must keep Property coverage in the playlist/MainWindow fixtures. Shared spec or fixture edits require root integration rather than concurrent edits.
- Each worker receives only its Contract IDs and may alter fixture mechanics but not expected semantics.
- Replan if canonical adoption requires application-level implicit resources that break standalone dictionary closure, if Property needs ViewModel/persisted navigation state, or if any fix needs changing the approved current-framework Release Notes menu ownership, outer overlay size, Bulk minimum, field inventory, availability, or modal lifecycle.
- After integration, run combined focused Quick, exact repository-executable UI inspection, one Functional verification, and a fresh read-only static review against this packet.
