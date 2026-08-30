# Keyword search assistance

Status: Current implementation contract

Packet: `KSA-2026-01`

This specification describes the reusable keyword-search editor and the non-WPF
owners that provide its immutable presentation. The approved plan and packet are
the authority for this behavior; parser/matcher behavior is outside this feature.

## Scope and ownership

There are two search scopes:

- Normal search is shared by ChartList, PlaylistDetail, and PlayHistory.
- PlaylistSummary has an independent saved-query scope and catalog context.

`KeywordSearchAssistanceOwner` owns the focused text/caret snapshot, active
context, immutable dynamic catalog snapshot, suppression state, and immutable
`KeywordSearchPresentationState`. It calculates candidates synchronously from
the injected snapshot and the saved-query owner. It does not read a database or
hold a model lock during a keystroke or UI callback.

`KeywordSearchSavedQueryOwner` owns Favorites and History for one scope. Its
settings adapters are explicit dependencies. A mutation calls the appropriate
adapter setter synchronously before publishing new in-memory state or the
presentation notification. A setter exception returns a failed result and leaves
adapter-visible data, collections, ordering, projection, and success
notifications unchanged. There is no per-action `Settings.Save`.

`KeywordSearchEditor` is a reusable WPF terminal owner. It owns Popup visibility,
row materialization, flattened selection, per-section scrolling, keyboard/IME
handling, focus transitions, and row action buttons. MainWindow only composes one
normal and one summary editor and keeps the existing clear/help/warning/filter
affordances. Popup `IsOpen` is not two-way bound to a ViewModel.

## Presentation lifecycle

While focused, every text change, caret change, context change, or catalog
snapshot change recomputes presentation immediately. The catalog revision is
carried through each candidate identity. The outer chart-list filter binding
continues to use its existing 500 ms delay; that delay is not used for assistance.

Empty or whitespace-only text presents non-empty sections in this order:

1. Favorites
2. History projected for the current context
3. Fields

Empty sections collapse. A named-field query incompatible with the current
context is hidden from projection, never deleted. Visual Favorite/History
deduplication is projection-only; a pinned query keeps its backing History entry.
Unpinning reveals that History entry only if it remains in the current scope.

For non-empty input, field-prefix positions present Fields only. Finite or
dynamic value positions present Values only. Arbitrary values, regular
expressions, and no-match positions close the surface. PlaylistSummary presents
Fields only and includes `output:`; it has no Values section.

The editor closes on blur or window deactivation. Escape suppresses only the
unchanged text/caret/context/catalog-revision snapshot. Any of those inputs
resets suppression. Ctrl+Space forces a fresh presentation. Up/Down moves through
the flattened visible rows while preserving TextBox focus. Enter/Tab applies the
selected row, or the first row if none is selected. An IME composition/commit
Enter is never treated as candidate application.

## UI-fix packet KSA-2026-01-UIFIX

`KSA-FOCUS-02` makes the focused search TextBox the authority for an ordinary
routed mouse click outside that editor and its popup-owned subtree. The window
preview route clears WPF keyboard focus even when assistance is already closed;
the existing TextBox `LostKeyboardFocus` handler remains the single owner-blur
and history-commit lifecycle. Candidate and action interactions inside the
popup remain owned by the editor. This applies independently to normal and
PlaylistSummary search.

`KSA-CLEAR-02` keeps the window preview-to-clear-affordance route synchronous:
it clears the corresponding query, keeps or restores that editor's TextBox
keyboard focus, and immediately presents the appropriate empty-input sections.
The normal and PlaylistSummary scopes remain independent, and the outer
500 ms filter delay is unchanged. No timer or delayed refocus is part of this
route.

`KSA-VISUAL-02` is a manual/static acceptance contract. Favorite and history
action glyphs use semantic theme foreground resources and remain readable in
both themes. A row owns one declarative hover/selection surface spanning its
label and actions; action buttons do not render separate tiles. Assistance is
anchored to the reusable editor surface with the existing semantic popup
top-gap convention, visibly below and separate from the input chrome.
Automated verification does not assert ARGB/brush identity, pixels, sizes,
screenshots, hover rendering, XAML source text, or exact assistance popup
placement targets.

## UI-fix packet KSA-2026-01-TOOLTIP-WIDTH

`KSA-TOOLTIP-03` keeps saved-row presentation attached to the current item
when a virtualized Favorites or History surface is recycled or reassigned.
The non-action row hover surface exposes the complete current `Query`, and the
apply control's `AutomationProperties.Name` follows the current
`DisplayText`. Fields, Values, and PlaylistSummary Fields do not expose a
saved-query tooltip. Favorite and History action buttons retain their own
localized, non-empty `ToolTip` and accessibility name; that action-owned value
takes precedence over the row query tooltip.

`KSA-WIDTH-03` keeps the popup's outer content width equal, within layout
rounding, to the editor surface `ActualWidth` captured before opening. A long
saved query remains intact in its underlying label text while the rendered
label is constrained to one non-wrapping line with standard character
ellipsis. The same width rule applies independently to the Fields-only
PlaylistSummary popup. Width is a layout relationship, not a fixed-pixel
oracle.

## Editing and row actions

Field application replaces only the active field prefix, preserves a leading
negation and the rest of the query, and inserts `field:` without a following
space. Value application replaces only the active value fragment, preserves the
suffix and quoting/escaping rules, normalizes the immediate following separator
to exactly one ASCII space, and places the caret after that separator. Saved rows
replace the whole query and commit it to History.

Candidate identity contains the exact text, caret, context, dynamic catalog
revision, and replacement span. Apply rejects a mismatch or recomputes through
the current presentation; it never applies an old span to new text.

Favorite rows expose a trailing remove button. History rows expose trailing pin
and delete buttons. Each is a real keyboard-activatable Button with localized
ToolTip and AutomationProperties.Name. An action does not apply the row query,
change the active filter, or steal input focus. Favorites are unlimited and
newest-pin-first. History remains Base64-line compatible, recent-first, capped at
20, case-insensitively normalized/deduplicated, and tolerant of corrupt lines.
History deletion removes only the exact normalized target in the active scope.

Favorites and History render at most five rows before their own vertical scroll;
Fields and Values render at most ten rows before their own vertical scroll. A
section below its cap sizes to content, and each section owns a virtualized item
viewport so unlimited Favorites do not require all backing rows to be
materialized. There is no shared outer completion scroll viewer.

For a saved row with actions, `Shift+Tab` from the focused TextBox moves to the
first trailing action in the selected row: Favorite remove, or History pin then
delete. If no saved row is selected, the first visible saved row is used only
when it has an action. While an action has focus, `Tab` and `Shift+Tab` cycle
the actions in that row and then return to the TextBox without closing the
popup. `Enter` and `Space` use normal Button activation. `Escape` returns to
the TextBox and suppresses the unchanged snapshot. Mouse action buttons remain
focus-preserving and do not apply the row query. An active IME composition is
never committed as a candidate by a normal Enter key.

## Canonical candidate values

The following insertion values are authoritative. Display labels may vary by
presentation, but the inserted set is set-equal to these values.

| Field/context | Values |
| --- | --- |
| Normal `clear` | `nosong`, `NP`, `F`, `AE`, `LAE`, `EC`, `NC`, `HC`, `EXH`, `FC`, `PF`, `MAX` |
| PlayHistory `clear`, `oldclear`, `newclear` | Normal `clear` values plus `defined`, `undefined` |
| `rank`, `djlevel`, `dj` | `F`, `E`, `D`, `C`, `B`, `A`, `AA`, `AAA`, `MAX`, `defined`, `undefined` |
| `difficulty` | `beginner`, `normal`, `hyper`, `another`, `insane`, `defined`, `undefined` |
| `judge` | `veryhard`, `hard`, `normal`, `easy`, `veryeasy`, `defined`, `undefined` |
| `judge%`, `judgepct` | `defined`, `undefined` |
| `feature` | `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll`, `defined`, `undefined` |
| Chart-info fields (`level`, BPM, duration/length, notes, long/ln, scratch, total, tn/t/n, density, peak/peakdensity, end/enddensity, soflan) | `defined`, `undefined` |
| Nullable score fields (`rate`, `score`, `combo`, `bp`) | `defined`, `undefined` |
| PlayHistory `finalized` | `true`, `false` |
| PlayHistory `month` | `1` through `12` |
| PlayHistory `type`, `kind` | `score`, `bp`, `clear`, `combo`, `play` |

`playlist`, `ref`, and `table` use the injected immutable installed-playlist
snapshot, case-insensitive prefix matching, deduplication, sorting, and the
existing quote/escape behavior. `undef` and `null` remain parser aliases, but
candidate insertion uses canonical `undefined`. Normal `clear` does not include
`defined`/`undefined`; PlayHistory clear does. Canonical `nosong` is inserted
even when a display label such as `NO SONG` is used.

## Resources and compatibility

Every new user-facing header, action ToolTip, or AutomationProperties.Name has a
resource entry in `Resources.resx`, a generated accessor, and all six language
JSON files. The guides in `docs/keyword-search-syntax-guide.md` and
`docs/keyword-search-syntax-guide.ja.md` describe the same `output:` and editor
interactions. Existing parser aliases, matcher results, normal/summary scope
separation, history serialization, toolbar geometry, clear/help/warning bindings,
and the 500 ms filter debounce remain compatible.

## Verification map

| Contract | Verification |
| --- | --- |
| `KSA-REFRESH-01`, `KSA-EMPTY-01`, `KSA-CONTEXT-01`, `KSA-EDIT-01`, `KSA-REV-01` | `KeywordSearchPresentationTests`, `GridKeywordSearchQueryTests`, `ChartListFilterViewModelTests`, `PlaylistWorkspacePresentationStateTests` |
| `KSA-FAV-01`, `KSA-HIST-01`, `KSA-PERSIST-01` | `KeywordSearchSavedQueryStoreTests`, `ApplicationCompositionTests` |
| `KSA-INPUT-01`, `KSA-VIEW-01`, `KSA-FAV-01`, `KSA-HIST-01`, `KSA-COMPAT-01` | `MainWindowChartPresentationWpfTests` using `TestUiDispatcherHost` and `TestWindowPresentationScope` with routed-event and dispatcher/layout completion |
| `KSA-FOCUS-02`, `KSA-CLEAR-02` | `MainWindowChartPresentationWpfTests` covering normal and PlaylistSummary scopes, including outside click with assistance already closed and the actual window preview/clear routes |
| `KSA-VISUAL-02` | Manual/static review of semantic resources, row-owned declarative visuals, and popup separation; no pixel or screenshot test |
| `KSA-TOOLTIP-03`, `KSA-WIDTH-03` | `MainWindowChartPresentationWpfTests` using `TestUiDispatcherHost` and `TestWindowPresentationScope`: virtualized saved-row realization/rebinding, localized action ownership, candidate tooltip absence, and pre-open surface-width relation with no `MaterializePopup` |
| `KSA-LOC-01` | `LocalizationResourceParityTests` plus localized WPF ToolTip/Automation checks |

Focused verification uses:

```text
FullyQualifiedName~GridKeywordSearchQueryTests|FullyQualifiedName~KeywordSearchSavedQueryStoreTests|FullyQualifiedName~KeywordSearchPresentationTests|FullyQualifiedName~ChartListFilterViewModelTests|FullyQualifiedName~PlaylistWorkspacePresentationStateTests|FullyQualifiedName~ApplicationCompositionTests|FullyQualifiedName~MainWindowChartPresentationWpfTests|FullyQualifiedName~LocalizationResourceParityTests
```

The WPF fixture remains serial-state-b and uses no foreground activation,
physical cursor, fixed sleep, raw HWND helper, source-text assertion, broad
snapshot, exact translated-copy assertion, or new private reflection seam.
