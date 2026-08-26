# External Chart Launch

この資料は、譜面の右クリックから web link と external program を解決・起動するための現行 contract である。Settings UI は draft/commit を所有し、runtime unit は persisted definition、strict parse、single-chart resolution、menu terminal、process/browser gateway の境界を定める。

## Persisted Settings

`Settings.RightClickActionsJson` は `webActions` と `programActions` を一つにまとめた user-scoped JSON aggregate である。配列順がメニュー順であり、schema version は持たない。missing property（null の設定値）は次の五つの enabled built-in web action にだけ置き換える。

| stable ID | default URL template | default chart kind |
| --- | --- | --- |
| `bms-ir` | `https://bms-ir.org/new/song?songmd5={md5}&view=both` | `BmsOnly` |
| `mocha` | `https://mocha-repository.info/song.php?sha256={sha256}` | `All` |
| `minir` | `https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0` | `All` |
| `rianir` | `https://rianir.link/ranking?sha256={sha256}` | `All` |
| `stellaverse-ir` | `https://ir.stellabms.xyz/charts/{md5}` | `All` |

An explicit empty setting is retained as an empty aggregate and never reseeds defaults. Invalid JSON, unknown or duplicate members, duplicate IDs, invalid enum values, and invalid action values return a typed parse failure. They do not throw during startup and do not silently fall back to defaults. The caller must surface a settings error and may offer an explicit reset to defaults; until then no action is exposed.

The Settings dialog exposes this aggregate as a dialog-local draft under a dedicated right-click category. Opening the category only parses a snapshot; editing a field, reordering an action, or choosing an executable does not mutate `Settings.RightClickActionsJson`. Save validates and serializes the complete aggregate once, then lets the existing settings edit session persist it. Cancel and native close discard the child draft. When the persisted value is invalid, the page shows a localized settings error and exposes only an explicit “restore defaults” recovery; Save remains unavailable until that recovery (or another valid draft) succeeds. Raw parser details remain internal diagnostics. The executable picker accepts `*.exe` and all files, and fills a blank program name from the accepted basename without overwriting a user name.

Built-in IDs may have a null or nonblank display-name override. A custom web action requires a nonblank name. Web templates must be absolute `http` or `https` URLs and contain at least one case-sensitive `{md5}` or `{sha256}` placeholder. Unknown or unbalanced placeholders are rejected. `md5` and `sha256` values are accepted only as exact-length hexadecimal strings and are substituted in lowercase; a resolved action is omitted when any placeholder it requires is unavailable.

Program definitions require a stable ID, nonblank name, absolute executable path, enabled flag, and an argument template containing `{filePath}`. Unknown or unbalanced placeholders are rejected. The template is tokenized with Windows double-quote/backslash rules; single quotes are literal characters. Tokenization happens before placeholder substitution, and the resulting exact tokens are passed as `ProcessStartInfo.ArgumentList` entries. Executable existence is deliberately checked at click time, not while parsing or resolving.

## Single Chart Context

The resolver receives one immutable `RightClickActionResolutionInput` containing optional MD5, optional SHA-256, optional local absolute chart path, and chart kind (`BmsOnly`, `BmsonOnly`, or `All`). It preserves enabled action order. Web actions are filtered by chart kind and required digest capability. Program actions are considered only when the selected chart has a local absolute path; they are not inferred from a URL, a play-history hash, or a relative path. Resolver output performs no filesystem existence check.

Every standard-library context menu, missing-chart context menu, and play-history context menu first selects exactly one chart context row and resolves it through this contract. A multi-selection or a mixed-kind aggregate must not be passed to the resolver. Rows without the required digest expose no corresponding web action; a play-history row may still expose hash-only web actions when its raw MD5/SHA-256 is valid. No hash-only or missing-chart row exposes a program action. A play-history row resolved to a local chart also exposes the existing associated-open command, with the Program submenu inserted immediately after it.

## Runtime Launch Contract

The UI owner owns menu construction, localization, and click error presentation. The current runtime integration keeps the following boundaries:

- web links use the existing browser / shell gateway and are opened only after a resolved URL has passed the typed capability checks;
- programs use `UseShellExecute=false`, one `ArgumentList` entry per resolved token, and a working directory equal to the executable's parent directory;
- programs are launched without waiting for process completion; process ownership and shutdown do not become a chart-row lock or UI synchronous wait;
- a missing executable produces an explicit click failure and does not hide the settings or pretend that launch succeeded;
- invalid persisted settings produce no actions plus a localized settings error/reset affordance, while a valid explicit empty aggregate produces no actions without an error;
- parser and gateway diagnostics remain available to internal logging, but the UI terminal maps typed failure kinds to localized messages and does not display raw exception or parser text.

The old hard-coded web routes are no longer a runtime source. The legacy owner methods remain only as compatibility entry points for existing command/test seams and resolve the same stable IDs through the shared resolver. The pure resolver must not call `Process`, shell APIs, browser APIs, `File.Exists`, or UI/dialog services.

At menu-open time the MainWindow owner resolves the current settings and inserts the enabled web actions in persisted order. A local owned chart gets a `Program actions` submenu immediately after the existing associated-open command; each child carries the stable action ID and action kind rather than a captured row or URL. Play-history rows that resolve to a local chart use the same associated-open terminal and receive the Program submenu directly after it; hash-only play-history rows expose Web actions only. Missing playlist entries never get that submenu. On click, the exact context row is recovered from the context menu, the current settings are read again, and the action is re-resolved before it is sent to a gateway. A stale action, changed settings, invalid settings, missing chart, missing executable, or gateway failure produces no launch and a localized terminal error.

The program gateway checks the absolute executable and chart paths at click time, constructs `ProcessStartInfo` with `UseShellExecute=false`, the executable parent as `WorkingDirectory`, and one resolved token per `ArgumentList` entry, then starts once without waiting or retaining the process. It does not redirect streams, use raw `Arguments`, or fall back to associated-open. Web failures use the existing typed external-shell request boundary.

## Verification Map

| Behavior / failure contract | Owner fixture | Decision |
| --- | --- | --- |
| missing defaults, exact IDs/URLs/order/enabled/kinds, explicit empty, round-trip, unknown/duplicate/invalid settings, name rules | `RightClickActionSettingsStoreTests` | extend/new pure aggregate fixture; no filesystem or process |
| Windows tokenization, quoted/embedded placeholders, empty token, unicode/spaces, backslash/trailing slash, missing/unknown/unbalanced input | `ExternalProgramArgumentTemplateTests` | new pure parser fixture; exact token assertions |
| lowercase hash substitution, BMS/BMSON/All capability, enabled/order, program local-path gate and deferred executable existence | `RightClickActionSettingsStoreTests` | extend resolver cases; deterministic in-memory input |
| Settings metadata and app.config typed default parity | `ApplicationSettingsMetadataTests` | extend existing compatibility fixture |
| right-click editor parse/draft/dirty/validation/commit semantics | `RightClickActionSettingsEditorTests` | new pure editor fixture; no settings mutation before explicit commit |
| settings navigation/page bindings and executable picker route | `SettingsWindowCompiledBehaviorTests`, `SettingsWindowPresentationTests` | extend existing compiled/presentation fixtures; shared WPF dispatcher only |
| right-click resource keys and six-language parity | `LocalizationResourceParityTests` | extend existing resource parity fixture |
| program process-start contract and typed missing/start failures | `ExternalProgramLaunchGatewayTests` | new pure gateway fixture with a fake file-exists function and starter; no real process/filesystem |
| menu-open resolution, stable-ID re-resolution, web/program placement, PlayHistory associated/program order, exact-row and typed failure terminal | `SelectedChartExternalActionWorkflowOwnerTests`, `MainWindowSelectedChartContextMenuWpfTests`, `MainWindowPlayHistoryWpfTests`, `MainWindowContextMenuResourceTests` | extend shared owner and existing WPF context-menu fixtures; assert localized messages without raw diagnostics; no real process or fixed wait |

The pure tests run in the normal Quick lane with no WPF, reflection seam, real process, external executable, fixed wait, or shared resource. The context-menu coverage uses the existing WPF dispatcher/host lane and verifies materialization and terminal behavior without opening a browser or process. Settings editor behavior remains owned by the settings unit; the final repository Functional lane remains the root integration responsibility.
