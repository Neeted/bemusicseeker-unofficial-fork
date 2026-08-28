# Playlist property page layout refresh

Status: page-chrome/order amendment implemented and focused verification complete; integrated Functional and fresh static review pending
Packet: `APP-UI-PLAYLIST-PROPERTY-LAYOUT-04`
Base: `e9502773`

## Goal

Rebuild the three `PlaylistPropertyDialog` page bodies from the user-approved
Concept A without using the former fixed-size arrangement as a layout source.
The result follows the Settings window's unboxed section, heading, description,
field, spacing, scrolling, and resize grammar while retaining every existing
edit, availability, navigation, timestamp, and lifecycle behavior.

## Decisions

- Initial size is `640 x 720`; minimum size remains `560 x 420` and the window
  remains user-resizable.
- At Japanese culture, the default font, and 96 DPI, General has no vertical
  page overflow at the initial size. Other cultures, text scaling, and a
  user-shrunk window may scroll.
- One native content owner supplies outer spacing. Navigation and footer stay
  fixed; exactly one page-body viewport owns vertical page scrolling and page
  horizontal scrolling remains disabled.
- General uses a full-width name, a roomy two-column symbol/prefix row, a
  separate entry-unit section, and full-width Page/Header/Data URI editors.
- Folder puts sorting before a dominant independently scrolling order list;
  the list grows when the window height grows.
- Custom Folder puts Output Folder first and Output Destination second in
  natural top-origin flow. Output editors stretch; all thirteen flags use two
  wrapping columns and explanatory text remains in the section hierarchy.
- Concise localized descriptions may be added. Tests verify resource parity,
  binding, containment, and wrapping rather than exact translated prose.
- Manual screenshots are deferred until the user approves the uncommitted UI.
- This work stops before commit.

## Test Contract Packet

Authority is the user's approved Concept A and sizing decision, plus the
existing `playlist-data-and-export-flow.md` presentation contract only for
inventory, navigation, availability, date format, and lifecycle. Current XAML
geometry, runtime output, existing expected values, and current translations
are not oracle authority.

| Contract ID | Observable contract | Allowed variation | Wrong implementation to detect |
| --- | --- | --- | --- |
| `APP04-SHELL` | Exact initial/minimum dimensions and `CanResize`; fixed navigation/footer; one vertical body viewport; zero page horizontal overflow; initial General vertical overflow is zero in the decided environment; navigation resets offsets | margins, templates, localized labels, scrollbar chrome; overflow in other cultures/scales/smaller sizes | 520-height initial window, footer/nav inside scrolling body, horizontal overflow, preserved stale offset |
| `APP04-GENERAL` | Full-width name; roomy two-column symbol/prefix; separate entry unit; external sync before full-width URI fields; editors are contained, at least canonical 32 DIP high, and grow when the window widens | exact ratios, gaps, field widths, panel/control types | fixed 40-DIP symbol, capped name/URI editors, compressed entry selector, horizontal page scroll |
| `APP04-FOLDER` | Sorting precedes the folder order region; the list owns independent scrolling, remains reachable with actions, and gains height after a taller resize; existing gates remain | exact list height/delta, action orientation, scrollbar visibility when content fits | fixed-height list, page expands to every item, list precedes sorting, clipped actions |
| `APP04-CUSTOM` | Output Folder precedes Output Destination in natural top-origin flow; destination editors stretch and do not move when only height grows; explanation follows its heading and precedes flags; thirteen tooltip-bearing TwoWay flags occupy two wrapping columns without page horizontal overflow at minimum width | exact flag order/column assignment, localized copy, exact wrap height, panel types | destination first, bottom-pinned destination/note, fixed 900-DIP content, no-wrap/clipped captions, missing flag/tooltip |
| `APP04-COMPAT` | Existing three-category navigation, drafts, bindings, availability, exact `Update: yyyy/MM/dd`, save/cancel/reset/failure/retry/shutdown/cleanup remain | implementation types and localized captions | dropped field/gate/tooltip, changed binding direction/date/lifecycle |

### Coverage ledger

| Contract | Owner / fixture | Decision | Shared resource / completion | Retired assumption |
| --- | --- | --- | --- | --- |
| `APP04-SHELL` | `PlaylistPropertyDialog` / `PlaylistSummaryBulkEditTests` | extend and replace only obsolete geometry setup | existing `TestUiDispatcherHost` and presentation scope; `ContentRendered`, layout update, dispatcher drain | do not require Custom Folder to overflow naturally at a fixed 520 height; induce overflow for scroll-reset coverage |
| `APP04-GENERAL` | same | extend | same WPF host and signals | fixed-width geometry is not retained |
| `APP04-FOLDER` | same | extend with synthetic folder rows | fixture-owned collection; same host/signals | none |
| `APP04-CUSTOM` | same | extend; retain inventory/tooltips test | test-local long caption; same host/signals | none |
| `APP04-COMPAT` | existing property presentation and workflow fixtures | retain | existing owners/tasks/events | no duplicate outer-spacing test |

No new `DoNotParallelize`, foreground activation, cursor access, raw dispatcher
pump, fixed sleep, exact translated-copy assertion, XAML/source assertion, or
duplicate native outer-spacing assertion is permitted.

### Negative controls

The implementation handoff must demonstrate at least one restored targeted
negative control for each changed family:

- Shell: wrong initial height or page horizontal overflow.
- General: fixed narrow symbol or capped full-width editor.
- Folder: fixed-height order list.
- Custom: destination-first section order, bottom-pinned destination, or
  no-wrap wide flag content.

## Unit and ownership

One implementation unit owns the visual contract end-to-end because the XAML,
localized descriptions, presentation tests, and feature spec must agree.

Writable paths:

- `BeMusicSeeker/Views/PlaylistPropertyDialog.xaml`
- `BeMusicSeeker/Views/Settings/SettingsControls.xaml` for the shared
  constrained-content behavior described below
- the resource parity set under `BeMusicSeeker/Properties` and `lang/` only if
  description keys are added
- `BeMusicSeeker.Tests/PlaylistSummaryBulkEditTests.cs`
- `BeMusicSeeker.Tests/SettingsForegroundInteractionTests.cs` for the existing
  allowlisted shared Settings-section host
- `devdocs/spec/playlist-data-and-export-flow.md`
- `devdocs/spec/appearance-theme.md` only if the initial-size contract belongs
  in the shared native-window specification
- this plan for verification evidence

`DialogPresentationTests.cs`, ViewModel/code-behind, shared Settings controls,
manual screenshots, persistence, and workflow owners are read-only unless a
replan trigger is returned.

The static review amendment below explicitly expands the original shared
Settings-controls restriction; other shared controls remain read-only.

## Static review amendment 1

The first frozen-snapshot review rejected the implementation for three related
reasons: the Folder page fixed its `Height` to the body viewport and therefore
could not create a reachable outer extent at minimum height; the dialog copied
the shared `SettingsSection` template to obtain stretch; and General/Folder
minimum-size routes were not materialized by tests.

The root-approved amendment adds these contracts without changing the original
Contract IDs:

| Contract ID | Observable contract | Allowed variation | Wrong implementation |
| --- | --- | --- | --- |
| `APP04-SECTION-CONSTRAINED-STRETCH` | Shared `SettingsSection` owns heading, optional description, and content. In a finite-height host, natural heading/description leave the remaining height to content. In an auto-sized Settings page, the section retains natural desired height without blank inflation. Playlist Property has no local copy of that template. | concrete template elements/rows, coordinates, text, content type | StackPanel/Auto content that cannot fill, a local template copy, or auto-sized height inflation |
| `APP04-MINIMUM-VIEWPORT-REACHABILITY` | General and Folder at width 560 have no page horizontal overflow or clipped required controls. Folder at 560x420 with a deterministic long description and excess items creates reachable outer vertical extent through which move actions remain reachable, while its list retains independent positive scroll extent. At roomy heights the list still grows with the window. | exact extents/deltas/action orientation/margins; ordinary short text need not overflow | exact page Height clamp, loss of roomy-height fill, width over 560, or expanding all items into the page |

Implementation direction:

- Remove the dialog-local stretching template.
- Make the shared base `SettingsSection` template allocate remaining finite
  height to content while preserving natural auto-sized behavior.
- Use viewport-relative `MinHeight`, never exact `Height`, for the Folder page.
- Extend the existing allowlisted Settings foreground method with constrained
  and auto-sized section probes; do not add a new foreground method/FQN.
- Extend Playlist Property tests with real minimum-width and minimum-height
  rendered routes and reachability assertions.

Required restored negative controls include the shared section's non-stretching
composition and the Folder exact-height clamp. Focused verification must include
the existing Settings foreground method and Playlist Property presentation
fixture before a fresh static review.

## Page chrome and Custom order amendment

The approved final composition adds two presentation contracts:

| Contract ID | Observable contract | Allowed variation | Wrong implementation |
| --- | --- | --- | --- |
| `APP04-PAGE-CHROME` | Fixed top navigation, one unframed selected-page body, and fixed footer. General, Folder, and Custom retain major SettingsSection hierarchy without a visible page-wide border or rounded background surface. | internal hosts, spacing, page background, individual control chrome, concrete template | the former rounded canonical content surface, a hidden-border workaround with visible rounded background, or flattened major sections |
| `APP04-CUSTOM-ORDER` | Output Folder with heading, description, and thirteen flags precedes Output Destination with output base, folder name, and root option. Both remain in natural top-origin flow, and Destination top stays stable when only height grows. | exact flag order/columns, localized copy, layout rounding | destination first, bottom docking, or a star spacer that moves Destination as height grows |

Coverage extends the existing real-window Playlist Property shell and Custom
tests. The existing canonical top-navigation test verifies the shared unframed
role while preserving padding, stretch alignment, focusability, selection,
keyboard, and Automation semantics. No source/template-type assertion or new
foreground/parallel lane is introduced.

## Verification

- Focused Quick: `PlaylistSummaryBulkEditTests`, plus
  `LocalizationResourceParityTests` if resources change.
- Adjacent Quick: `DialogPresentationTests` and the existing actual MainWindow
  playlist-property modal route.
- Final integrated Functional once on the reviewed snapshot.
- Static review after implementation and focused verification, before any user
  handoff.

## Replan triggers

- General cannot avoid initial overflow at 720 without hiding fields, shrinking
  canonical controls, or removing meaningful descriptions.
- A supported initial/minimum-width route clips or needs page horizontal scroll.
- The design requires changing shared Settings controls, ViewModel/code-behind,
  binding/lifecycle/availability behavior, or outer-spacing ownership.
- Folder-list growth cannot be observed through the existing safe WPF host.

## Evidence

- Semantic base red: focused Concept A Quick compiled and ran 3 tests with 3
  intended failures in
  `artifacts/verification/tests-quick-20260828-080109/functional/results.trx`:
  the old initial height was 520 instead of 720, the Folder list did not grow
  after a taller resize, and Custom lacked its Output Types semantic role.
- Head Concept A plus localization parity: 9/9 passed in
  `artifacts/verification/tests-quick-20260828-081713/functional/results.trx`.
  General fits without vertical overflow at 720 while preserving the standard
  28-DIP interval between its major Settings sections; the redundant identity
  intro description was omitted instead of compressing section rhythm.
- Restored negative controls all failed their intended observable:
  - Shell wrong initial height: 1/1 failed in
    `artifacts/verification/tests-quick-20260828-081745/functional/results.trx`.
  - General fixed 40-DIP symbol editor: 1/1 failed in
    `artifacts/verification/tests-quick-20260828-081908/functional/results.trx`.
  - Folder fixed 130-DIP order list: 1/1 failed in
    `artifacts/verification/tests-quick-20260828-082029/functional/results.trx`.
  - Custom non-wrapping long flag caption: 1/1 failed in
    `artifacts/verification/tests-quick-20260828-082150/functional/results.trx`.
  Every mutant was removed before head verification.
- Focused head Quick for the full `PlaylistSummaryBulkEditTests` fixture plus
  localization parity: 23/23 passed in
  `artifacts/verification/tests-quick-20260828-082323/functional/results.trx`.
- Adjacent Quick for all `DialogPresentationTests` and the actual MainWindow
  playlist modal lifetime fixture: 25/25 passed in
  `artifacts/verification/tests-quick-20260828-082446/functional/results.trx`.
- The former natural-overflow assumption in the navigation reset test was
  replaced by test-local induced overflow; inventory, availability, date,
  navigation, lifecycle, and canonical-dialog coverage remain in place.
- Amendment semantic reds:
  - The former shared `SettingsSection` StackPanel composition failed the
    finite-height content-allocation probe in
    `artifacts/verification/tests-quick-20260828-083712/functional/results.trx`.
  - The former exact Folder page height clamp failed to create reachable outer
    extent for minimum-size long content in
    `artifacts/verification/tests-quick-20260828-083752/functional/results.trx`.
- The shared section template is now the sole heading / optional-description /
  content owner. Its finite host stretches content into the remaining height,
  while the auto-sized probe retains natural desired height. Playlist Property
  no longer carries a local copy of the template.
- Folder now uses viewport-relative minimum page height and maximum list height,
  allowing long content to extend the outer viewport while preserving the
  list's independent scrolling and roomy-height growth.
- Amendment focused Quick for the full `PlaylistSummaryBulkEditTests`, the
  exact existing Settings foreground method, and localization parity: 24/24
  passed in
  `artifacts/verification/tests-quick-20260828-084157/functional/results.trx`.
- Amendment adjacent Quick for all `DialogPresentationTests` and the actual
  MainWindow playlist modal lifetime fixture: 25/25 passed in
  `artifacts/verification/tests-quick-20260828-084223/functional/results.trx`.
- Page-chrome/order semantic base red compiled and ran the existing shell,
  Custom, and shared canonical-navigation routes with 3/3 intended failures in
  `artifacts/verification/tests-quick-20260828-102931/functional/results.trx`:
  all selected pages still used the rounded shared content surface, Custom
  still put Destination first, and the shared canonical fixture still observed
  the framed content role.
- Focused page-chrome/order head Quick passed 3/3 in
  `artifacts/verification/tests-quick-20260828-103054/functional/results.trx`.
- Restored page-chrome/order negative controls failed their intended
  observables and were removed before head verification:
  - Restoring the rounded shared top-navigation content frame failed the
    rendered selected-page chrome oracle in
    `artifacts/verification/tests-quick-20260828-103231/functional/results.trx`.
  - Bottom-docking Output Destination failed the height-stable natural-flow
    oracle in
    `artifacts/verification/tests-quick-20260828-103359/functional/results.trx`.
- Focused head Quick for the full `PlaylistSummaryBulkEditTests` fixture and
  shared canonical top-navigation test passed 18/18 in
  `artifacts/verification/tests-quick-20260828-103626/functional/results.trx`.
- The exact existing Settings foreground route, with its obsolete template-name
  assertion replaced by the same rendered unframed/padding/stretch contract,
  passed 1/1 in
  `artifacts/verification/tests-quick-20260828-103852/functional/results.trx`.
- Adjacent Quick for all `DialogPresentationTests` and the actual MainWindow
  playlist modal lifetime fixture passed 25/25 in
  `artifacts/verification/tests-quick-20260828-103929/functional/results.trx`.
- The obsolete Custom Destination-before assertion and method name were retired;
  the replacement verifies Output Folder first, Destination field order, the
  thirteen wrapping flags, editor stretch/containment, and Destination's stable
  top position under height-only resize. Existing selection, keyboard,
  Automation, focusability, padding, binding, availability, date, navigation,
  and lifecycle coverage remains in place.
- Static-review acceptance amendments keep internal template variation allowed:
  the Settings foreground route now rejects only a content-wide enclosing
  border with a visible border, or a rounded surface with visible background /
  border, while preserving the existing padding, stretch, and focusability
  assertions. The selected-page route also verifies that visible major
  `SettingsSection` owners partition General identity from synchronization,
  Folder sorting from ordering, and Custom Output Folder flags from Output
  Destination controls.
- The tightened page-chrome and section-partition route plus the exact Settings
  foreground route passed 2/2 in
  `artifacts/verification/tests-quick-20260828-104630/functional/results.trx`.
- A restored General hierarchy-flattening mutant replaced the identity
  `SettingsSection` owner with a plain headered content host. It failed the
  intended semantic owner assertion in
  `artifacts/verification/tests-quick-20260828-104720/functional/results.trx`
  and was restored before head verification.
- Final focused Quick for the full `PlaylistSummaryBulkEditTests` fixture, the
  canonical top-navigation route, and the exact Settings foreground method
  passed 19/19 in
  `artifacts/verification/tests-quick-20260828-104831/functional/results.trx`.
- The developer flow specification now matches the accepted Custom layout:
  Output Folder first, Output Destination second, both in natural top-origin
  flow with height-stable Destination placement.
