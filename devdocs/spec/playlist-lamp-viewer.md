# Playlist Lamp Viewer

The playlist lamp viewer is a local, modeless presentation of one playlist's
current lamp aggregation. The playlist tree and the playlist-summary row each
expose a localized `Open lamp viewer` command immediately below `Open page`.
The command is a typed local route: it never calls the browser sink and does
not reuse the retired fixed external URL route.

## Presentation and layout

Every command invocation creates a fresh `ThemedWindow`. The viewer is
temporarily owned by the main window while its initial `CenterOwner` placement
is resolved. The manager shows it modelessly, makes one initial activation
attempt, and immediately clears the WPF `Owner`; later main-window interaction
therefore is not constrained by the viewer's z-order. Multiple instances for
the same playlist are allowed. Dialogs raised by the viewer remain owned by the
main window. The top of the window contains one ordered statistics collection.
Its score-independent cards are total, owned, missing, ownership rate, active
score source, and playlist last update. When score data is available, played,
unplayed, play rate, average EX score rate, and whole-playlist clear rate are
inserted before the final playlist last-update card. When score data is not
available, those score-dependent cards are omitted.

The header also contains a calendar-only `As of` selector and a `Latest` command.
`Latest` (the default) uses the current score snapshot. When an active score
provider has history, the calendar permits every local date from the oldest
provider-wide qualifying history row through today; an empty history disables
the date selector while leaving `Latest` available. The text box portion of the
date picker is read-only; dates are selected through its calendar, and the date
is not persisted or shared with another window.

Selecting local date `D` rolls back the current score snapshot at the local
midnight immediately after `D`. History rows at or after that cutoff are
processed newest first; equal timestamps use the provider source id descending.
LR2 considers finalized and unfinalized rows and restores `old_clear` plus
`old_op_history`, and `old_exscore` plus `old_totalnotes`. beatoraja considers
only mode `0` rows and restores `oldclear` plus `oldscore` using current notes.
If a processed future row explicitly has no old score, the chart is NP. A hash
with no processed future row retains its current score exactly.

Historical mode changes only score-dependent semantics: played/unplayed,
play rate, arithmetic average EX rate, clear rate, and the clear/rank/DJ graphs.
Current playlist membership, folder order, ownership, missing state, and last
update remain current facts. Segment clicks retain the current typed category
and scope route; the historical date and cutoff are not sent to navigation.

An unavailable, malformed, unreadable, or out-of-range historical snapshot is
score-degraded rather than a terminal playlist failure. The viewer remains
live with score-independent cards, and no current-score or all-NP fallback is
shown. Choosing `Latest` or a valid date performs recovery. Query and source
generations suppress stale results when date changes race with refresh or
cancellation.

The graph area is split into two equal, aligned halves: clear lamp on the left
and DJ rank on the right. Each half has its own heading, visible legend, and
bounded 100%-stacked bar. Below the global bars, one shared vertical
`ScrollViewer`/virtualized item collection presents normal folders in the
playlist's folder order. Every folder row has aligned clear and rank halves,
each with `folder label | 6-DIP spacer | bar | 6-DIP spacer | chart count`.
Empty normal folders remain
visible with a zero count; no synthetic `[NO SONG]` row is rendered.

The bars use a retained weighted WPF panel. Positive segments are standard
`Button` controls weighted by count, and the final visible segment receives the
remaining pixels so rounding cannot overflow its host. A zero-count segment has
no positive width and is not materialized as a button. Category colors are
distinct and supplied through dynamic theme resources. A selected segment also
changes border, text weight, and automation status; selection is not conveyed
by color alone.

Graph hosts and unselected segment buttons are borderless. A selected positive
segment has a visible non-color border and heavier text while its siblings stay
unselected; it remains a standard keyboard/UI Automation `Button`. The folder
label column is measured from the rendered label typeface over the current live
normal-folder rows, shared by the clear and rank halves, and capped at 170 DIPs.
The count column is a separate shared live column measured from every localized
numeric `CountText` in both halves, capped at the rendered localized `N0(9999)`
width. Both columns recompute after folder rows refresh. Labels beyond the cap
use ellipsis and retain their complete tooltip. Folder rows have no separator
and use a compact pitch that keeps adjacent bars non-overlapping. The default
window width is 1290 DIPs and presents the complete clear legend from `MAX`
through `NP` on one row; a narrower resize may wrap it.

Each positive segment exposes a localized label and count in the visible legend.
When a segment is wide enough to contain it, the bar also shows the complete
localized percentage; a narrow segment intentionally has no in-bar text, so it
cannot show a clipped fragment or ellipsis. Every positive segment retains its
complete localized label, count, and semantically nonzero localized percentage
in its tooltip and Automation name. Ordinary values use the normal localized
precision; values below that precision use a localized lower-bound representation
instead of being rounded to zero. Exact precision and copy are not part of this
contract. Enter, Space, and UI Automation Invoke use the same typed
segment intent.

## Categories and sources

The beatoraja clear order is `MAX`, `PERFECT`, `FC`, `EXHARD`, `HARD`,
`NORMAL`, `EASY`, `ASSIST`, `FAILED`, `NP`. The rank order is `AAA`, `AA`,
`A`, `B`, `C`, `D`, `E`, `F`, `NP`. Score rank `MAX` is folded into `AAA`.
`ASSIST` includes `INVALID` and `L_ASSIST`. `NP` means no play/no score.
Rank `F` includes played `F` and played `INVALID`-rank outcomes, while
`NO_PLAY`/`NO_SONG` rows are excluded from rank `F` and selected only by the
rank `NP` clear semantics.
There is no `NS` viewer category; missing entries remain part of the total
denominator and are represented by the core's score-independent ownership
cards. The special `[NO SONG]` folder remains a navigation guard and is
included in the NP search semantics, not as a rendered row.

With an LR2 source, `MAX` and `EXHARD` are omitted from every viewer surface:
cards, legends, global bars, folder bars, tooltips, and automation values.

## States, lifecycle, and failure

The session publishes `Loading`, `Ready`, `Empty`, `Deleted`, and `Failed`.
The manager subscribes before starting and waits through `Loading` for the
first non-loading result before showing a window. Initial `Deleted` and
`Failed` results show one owner-scoped localized dialog and do not show a
window. A dialog failure is propagated. After a window is shown, a live
`Deleted` result silently closes only that viewer; a live `Failed` result shows
one localized dialog and then closes only that viewer. Repeated terminal
signals and manual `Close` dispose the source, session, and DataContext exactly
once.

Main-window shutdown cancels pending opens and closes/disposes all tracked
viewers. No later continuation may show a window or dialog after shutdown.
There is no geometry/selection persistence and no cross-window cache.

After a positive typed segment request has been applied to the playlist tree
and detail selection, the main shell performs one best-effort restore (when
minimized), activation, and focus terminal. A failed activation or focus
attempt does not roll back the applied navigation. Requests whose tree
selection does not complete do not invoke that terminal.

For a normal `Ready`/`Empty` result, score-independent cards remain visible
even when score data is unavailable or failed. In that degraded case the
clear/rank graphs, score-dependent cards, and segment invocation are absent or
disabled. The failure remains distinct from `Empty` and `NP`; no routine
status/retry control is used.

## Statistics and denominators

The aggregation uses active real entries only. Global graph denominators and
whole-playlist rates use `total`; folder graph denominators use each folder's
real-entry count. The rendered card order is `total`, `owned`, `missing`,
ownership rate, active score source, `played`, `unplayed`, play rate,
arithmetic average EX score rate, whole-playlist clear rate, and playlist last
update. The five score-dependent cards are omitted together when score data is
unavailable. Whole-playlist clear rate is `(ASSIST or better) / total`, where
`FAILED` and `NP` are not clear categories.

An empty denominator displays localized `Unavailable`, never `0%`. Score
dependent values are unavailable when the score source is absent or failed.

## Segment navigation

Invoking a positive folder segment creates a typed
`PlaylistLampSegmentInvocationRequest`. The workspace re-resolves the active
playlist by stable `playlist_id`, verifies the same normal folder, replaces the
keyword filter, and publishes one folder-plus-filter intent. The main window
applies that snapshot atomically and navigates to the corresponding playlist
detail row set. Existing unowned entries are included in scored category
filters. The typed intents are serialized with aliases supported by the
chart-list parser (`pf`, `fc`, `exh`, `hc`, `nc`, `ec`, `ae`, `lae`, and `f`
for clear categories; `aaa`, `aa`, `a` through `f` for rank categories).
`ASSIST` maps to `INVALID | L_ASSIST`; `AAA` maps to `AAA | MAX`; both clear
NP and rank NP semantically include `NO_PLAY` and `NO_SONG`. Exact query
spelling or alternative ordering is not a contract; membership through the
real parser and detail presentation pipeline is. Stale/deleted playlist or
folder requests are no-ops.

Invoking a positive global (top) segment carries a distinct typed `Overall`
scope. The workspace re-resolves the same stable playlist, activates the table
root rather than an arbitrary folder, replaces the keyword filter, and publishes
one atomic root-plus-filter intent. The resulting detail rows are the union of
the active normal folders in their current table state; the special `[NO SONG]`
folder is excluded. Overall NP clear and rank segments include normal-folder
`NO_PLAY` and `NO_SONG` outcomes, while never selecting the special folder.
Global zero/degraded/deleted/failed/stale segments remain non-actionable, and
folder-scoped routes retain their existing folder guard.

## Verification map

| Contract | Observable coverage |
| --- | --- |
| PLV-01 | Compiled tree/summary menu entries are below Open page, create a local modeless window, and do not invoke browser routing. |
| PLV-02 | Core-backed folder rows preserve normal order, zero-count folders, and omit synthetic `[NO SONG]`. |
| PLV-03 | Rendered beatoraja clear categories use the canonical order and labels. |
| PLV-04 | Rendered LR2 surfaces omit MAX/EXHARD; rank MAX is represented by AAA. |
| PLV-05 | Legends, width-aware visible percentages, localized tooltip/Automation detail with semantically nonzero positive percentages, and zero-width behavior. |
| PLV-06 | Typed folder/category navigation, mappings, stale no-op, and one final atomic publication. |
| PLV-07 | ResultChanged delivery is dispatcher-safe and selected state is per window. |
| PLV-08 | Loading/Ready/Empty/Deleted/Failed plus score-unavailable/failed degradation; no routine status/retry or failure-to-NP masking. |
| PLV-09 | Multiple windows, exact-once lifecycle cleanup, initial gate, terminal dialogs, and main-window shutdown. |
| PLV-10 | ThemedWindow, standard Button keyboard/Invoke path, width-aware labels contained by bounded weighted geometry, automation state, and non-color selection. |
| PLV-11 | All cards, totals, rates, averages, and empty-denominator rules. |
| PLV-12 | RESX/generated accessors and all six language JSON key/value/placeholder parity. |
| PLV-13 | This specification, the Japanese/English manuals, release-note sections, and the spec index. |
| PLV-14 | No chart package, settings, persistence, or cross-window cache; legacy external route remains retired. |
| PLV-OVR-01 | Positive global clear and rank segments expose Button/Invoke actions, preserve per-window selection, and keep the viewer open. |
| PLV-OVR-02 | A global request uses the typed Overall scope, re-resolves the playlist, selects the table root, replaces the keyword, and commits one final union-of-normal-folders detail result. |
| PLV-OVR-03 | Overall NP includes normal-folder NO_PLAY/NO_SONG outcomes and excludes the special `[NO SONG]` folder. |
| PLV-OVR-04 | Zero, degraded, deleted, failed, and stale global segments do not navigate or fall back; folder-scoped navigation remains unchanged. |
| PLV-OVR-05 | Multiple viewers for one playlist keep independent selected segments and remain independently usable after one global invocation. |
| PLV-FG-01 | First-presentable Ready/Empty viewers use temporary `CenterOwner` ownership through modeless `Show` and one initial activation attempt, then release WPF `Owner`; visibility, tracking, multiple instances, and lifecycle remain intact. |
| PLV-FG-02 | A successful Overall request selects the table root and then performs exactly one main-shell restore/activation/focus terminal. |
| PLV-FG-03 | A successful normal-folder request selects the target folder child and then performs exactly one main-shell restore/activation/focus terminal. |
| PLV-FG-04 | A minimized shell is restored before activation and focus; false activation/focus results are best-effort and do not roll back navigation. |
| PLV-FG-05 | Viewer dialogs remain owned by the main window throughout initial and live failure presentation. |
| D3-NAV-CLR | Every positive clear intent reaches the supported chart-list grammar and the real parser/detail pipeline returns exact category membership, including ASSIST (`INVALID` + `L_ASSIST`) and NP (`NO_PLAY` + `NO_SONG`). |
| D3-NAV-RNK | Rank intents return exact AA through F membership; AAA folds `AAA` + `MAX`, and NP uses clear no-play/no-score semantics without played INVALID-rank assist rows. |
| D3-NAV-SCOPE | Folder requests retain their normal-folder scope; Overall requests publish the table root and return the union of current normal folders while excluding special `[NO SONG]`. |
| D3-WPF-LEGEND | A rendered default viewer presents the canonical clear legend from MAX through NP on one row. |
| D3-WPF-LABEL | Rendered folder labels use one live measured, capped 170-DIP column for both halves, with ellipsis and complete tooltip for over-cap names. |
| D3-WPF-SELECT | Rendered hosts and unselected buttons are borderless; invoking a positive segment leaves only that selection visibly bordered with heavier text and preserves Button/UIA behavior. |
| D3-WPF-ROWS | Rendered folder rows have no separator and adjacent bars remain non-overlapping with compact vertical pitch. |
| D4-CONTRAST | Rendered legend and positive-segment text use opaque black or white selected from the actual resolved solid background by WCAG sRGB relative luminance, including after selection. |
| D4-COUNT-TEXT | Folder counts render the current-culture N0 number only, without a unit suffix. |
| D4-FOLDER-GEOMETRY | Clear and rank folder halves use shared live label and count columns with explicit 6-DIP spacers; labels cap at 170 DIPs, counts cap at localized N0(9999), widths recompute after row refresh, and bars align across rows and halves. |
| D4-STAT-ORDER | One ordered statistics collection renders score-available and degraded card sequences in their required semantic order, with playlist last update always last. |
| D4-DEFAULT-WIDTH | The viewer defaults to 1290 DIPs, and the score-available cards and clear legend fit one rendered row at that width. |
| HIST-01 | Latest is the default, each window owns its nullable date query, and no historical selection is persisted or shared. |
| HIST-02 | Calendar-only date selection uses the exact active provider-wide range (oldest qualifying local date through today); LR2 includes all finalization states, beatoraja uses mode 0, and empty history disables only historical selection. |
| HIST-03 | The rollback cutoff is the selected local date plus one day at local midnight; equality is included and source rows are newest-first with descending source id ties. |
| HIST-04 | LR2 rollback restores the required clear/option-history and EX/notes fields and preserves explicit old-score absence as NP. |
| HIST-05 | Only the active provider is read; provider failure never falls back to the opposite provider, and beatoraja rollback uses mode-0 oldclear/oldscore plus current notes. |
| HIST-06 | No future row preserves current score; explicit old-score absence is NP and remains distinct from current score retention. |
| HIST-07 | Historical aggregation changes only score-dependent cards, rates, and graphs while current playlist membership/ownership/folder/last-update facts remain unchanged. |
| HIST-08 | Missing, unreadable, malformed, and out-of-range historical data is a nonterminal score-degraded result; Latest and valid dates recover. |
| HIST-09 | Query/source generations suppress stale publication even when cancellation is ignored. |
| HIST-10 | Historical graph segments emit the same typed category/scope navigation request without cutoff or membership parameters. |
| HIST-11 | Historical reads open provider databases read-only and never create, repair, or alter schema/data. |
| HIST-12 | Header controls, resource-backed labels, six-language parity, manuals, and this specification document the historical contract. |
