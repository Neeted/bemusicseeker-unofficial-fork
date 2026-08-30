# Keyword search assistance redesign

Status: complete
Packet: `KSA-2026-01`
Base: `8ce21222`

## Goal

検索欄の入力支援を、現在の単一候補一覧から、入力・caret・画面 context に継続追従する editor-owned UI へ再構成する。
空入力では Favorites、History、Fields を表示し、入力中は Fields または有限な Values のみを表示する。
Favorites と History の永続化、行 action、keyboard/IME、section ごとの独立 scroll を、既存 parser と 500 ms の一覧 filtering から分離して実装する。

## Context and constraints

- 対象は通常検索（ChartList / PlaylistDetail / PlayHistory の共有 scope）と PlaylistSummary 検索（独立 scope）の二つである。
- 現在の履歴 header、単一 ListBox、ViewModel-owned `Popup.IsOpen`、新規 focus 時だけ履歴を強制する route は新仕様の authority ではなく、退役対象である。
- parser / matcher の受理範囲と match 結果、および一覧 filtering の 500 ms debounce は維持する。
- 既存履歴の最大 20 件、Base64 行形式、case-insensitive dedupe、recent-first、破損行だけの skip、通常/summary の分離を維持する。
- WPF の focus、selection、scroll、routed input、hit-test は reusable search editor が所有する。MainWindow/root ViewModel へ popup terminal state を持たせない。
- 新しい表示文字列、ToolTip、Automation name は resx、generated accessor、六つの言語 JSON を同時に更新する。
- 旧 UI を固定する brittle test は維持せず、`KSA-2026-01` の observable behavior test へ置換する。
- preview image は合意形成用であり、pixel/layout oracle にはしない。
- commit、push、version update、release は行わない。

## Decision list

1. Favorites は session を越えて永続化し、件数上限・自動 eviction を設けない。normal と PlaylistSummary は別 scope とする。
2. normal Favorites/History は ChartList、PlaylistDetail、PlayHistory で共有する。現在の context で互換性のない named field を含む保存 query は表示から除外するが削除しない。
3. Favorites と History の identity は normalized case-insensitive とする。同一 query は Favorites にだけ表示し、backing History は保持する。unpin 後は backing History が残る場合だけ再表示する。
4. 空または whitespace の focused input は、空でない Favorites、空でない projected History、Fields の順で表示する。空 section は header ごと collapse する。
5. 非 whitespace 入力では applicable な tier だけを表示する。field-entry position は Fields、有限/dynamic field value position は Values、任意値・regex・候補なしは popup close とする。
6. field 選択は active prefix だけを `field:` に置換して space を追加しない。value 選択は active value だけを置換し、直後を正確に一つの半角 space に正規化し、次の Fields 入力へ移る。
7. stale candidate は text、caret、context、dynamic snapshot revision のいずれかが異なれば拒否または再計算し、古い span へ適用しない。
8. Up/Down は TextBox focus を維持して section 横断の visible rows を移動する。Enter/Tab は適用、Escape は同じ snapshot だけ抑止、Ctrl+Space は再表示する。IME composition Enter は候補適用に使わない。
9. Favorite 行は末尾に remove、History 行は末尾に favorite、delete の real Button を持つ。row action は query/filter を変えず、row apply を発火せず、input focus を奪わない。
10. Favorites/History は最大 5 行、Fields/Values は最大 10 行まで content-size 表示し、それぞれ独立して縦 scroll する。
11. pin は newest-first、Favorite 選択は順序を変えない。history delete は normalized target だけを削除する。最後の saved row を削除しても focused empty state の Fields は残る。
12. settings adapter setter は mutation publication より先に同期更新する。setter が throw した場合は operation を success にせず、以前の collection/order/projection を維持し、fallback しない。action ごとの disk `Save` は行わない。
13. PlaylistSummary は Fields only とし、parser-valid な `output:` も field catalog と JA/EN guide に含める。Values は初期導入しない。

## Canonical Values contract

順序と説明用 display text は実装 variation とするが、挿入値の集合は次を正とする。

- ChartList / PlaylistDetail `clear`: `nosong, NP, F, AE, LAE, EC, NC, HC, EXH, FC, PF, MAX`。ここでは `defined/undefined` を出さない。
- `rank/djlevel/dj`: `F, E, D, C, B, A, AA, AAA, MAX, defined, undefined`。
- `difficulty`: `beginner, normal, hyper, another, insane, defined, undefined`。
- `judge`: `veryhard, hard, normal, easy, veryeasy, defined, undefined`。
- `judge%/judgepct`: `defined, undefined`。
- `feature`: `ln, mine, random, lnmode, cn, hcn, stop, scroll, defined, undefined`。
- chart-info fields `level, mainbpm, maxbpm, minbpm, duration/length, notes, long/ln, scratch, total, tn/t/n, density, peak/peakdensity, end/enddensity, soflan`: `defined, undefined`。
- nullable score fields `rate, score, combo, bp`: `defined, undefined`。
- PlayHistory `clear/oldclear/newclear`: main clear values に `defined, undefined` を加える。
- PlayHistory `finalized`: `true, false`。`month`: canonical `1..12`。`type/kind`: `score, bp, clear, combo, play`。
- `playlist/ref/table`: injected immutable installed-playlist snapshot を case-insensitive prefix match、dedupe、sort、既存 quote/escape で候補化する。key stroke ごとに DB/lock-backed catalog を読み直さない。
- `undef/null` は parser alias として受理を維持するが、候補挿入は canonical `undefined` のみとする。
- `NO SONG` の説明表示は許容するが、canonical insertion は `nosong` とする。

## Test Contract Packet

Authority は original user requirements と承認済み decision list だけである。current implementation、runtime output、existing expected/snapshot、translations、repository prose、XAML/source、preview image を新 expected の根拠にしない。API/type/resource key、Favorites の新 serialization、failure の exception/result 表現は、次の observable contract を保つ限り variation とする。

| Contract ID | Observable outcome | Representative wrong implementation |
| --- | --- | --- |
| `KSA-REFRESH-01` | focus 中は text/caret/context/snapshot revision に即時追従し、clear で empty sections を復元し、blur で閉じる | focus 時または debounced filter 時だけ refresh |
| `KSA-EMPTY-01` | Favorites/History/Fields の順、空 collapse、scope/context projection、summary `output:` | incompatible query を削除、summary scope を共有 |
| `KSA-CONTEXT-01` | field position は Fields only、finite/dynamic value position は Values only、arbitrary/regex/no match は閉じる | Fields と Values を同時表示、title/regex を補完 |
| `KSA-CATALOG-01` | Canonical Values contract と set-equal | main clear に `defined`、`undef` や `01` を挿入 |
| `KSA-EDIT-01` | active fragment、negation、suffix、escape を保持し、value 後は正確に一つの ASCII space と Fields transition | whole-text 置換、double space、suffix loss |
| `KSA-REV-01` | text/caret/context/snapshot revision mismatch で stale edit を拒否または正しく再計算 | same-length text や caret 変更を無視 |
| `KSA-INPUT-01` | editor-owned lifecycle、focus-preserving navigation、snapshot Escape、Ctrl+Space、button isolation、IME safety | VM TwoWay open、ListBox focus、button bubble、IME Enter apply |
| `KSA-VIEW-01` | section cap `5/5/10/10`、独立 scroll、cap 未満は content-size、action が unclipped | outer single scroller、常時 max height、button clip |
| `KSA-FAV-01` | unlimited persistent Favorites、scope/order/identity、projection-only dedupe、non-query row actions | history cap を流用、pin で history 削除、selection reorder |
| `KSA-HIST-01` | 既存互換性、normalized single delete、pin で backing record 保持、selection commit | corrupt row で全 load failure、wrong-scope delete |
| `KSA-PERSIST-01` | synchronous adapter-first atomic mutation、setter failure は non-success/no publication、per-action disk Save なし | publish-first、swallow、per-action Save |
| `KSA-LOC-01` | 六言語 parity、localized ToolTip/Automation、semantic `output:`、JA/EN docs/spec | XAML literal、language omission、docs-only output fix |
| `KSA-COMPAT-01` | parser/matcher unchanged、assistance immediate、filter debounce exact 500 ms | candidate convenience のため parser/debounce を変更 |

## Coverage ledger

| Contract | Fixture / decision | Lane and completion | Retired assumption |
| --- | --- | --- | --- |
| `KSA-REFRESH-01`, `KSA-INPUT-01`, `KSA-VIEW-01` | `MainWindowChartPresentationWpfTests`: replace/extend | existing `TestUiDispatcherHost` / presentation scope、`serial-state-b`、routed event + dispatcher/layout completion、fixed sleep なし | focus-to-ListBox、VM TwoWay popup、single ListBox/global height |
| `KSA-EMPTY-01` | `KeywordSearchPresentationTests`, `ChartListFilterViewModelTests`, `PlaylistWorkspacePresentationStateTests`: replace/extend | synchronous Functional `remaining` | single-list/header/forceHistory expectations |
| `KSA-CONTEXT-01`, `KSA-CATALOG-01`, `KSA-EDIT-01`, `KSA-REV-01` | `GridKeywordSearchQueryTests`: extend | synchronous Functional `remaining` | playlist-only value special case、revision-less executable apply route |
| `KSA-FAV-01`, `KSA-HIST-01`, `KSA-PERSIST-01` | new `KeywordSearchSavedQueryStoreTests`; presentation/composition extend | store tests `remaining`; `ApplicationCompositionTests` は `serial-state-a`; synchronous counting/throwing adapter | parser fixture に埋め込まれた store test は同等 coverage 後に移動、history-only wiring |
| `KSA-LOC-01` | `LocalizationResourceParityTests`, WPF semantic controls | parity `remaining`; WPF `serial-state-b` | single header/copy-oriented test、docs prose assertion は作らない |
| `KSA-COMPAT-01` | existing parser tests retain; WPF search binding semantic assertion replace/extend | synchronous + compiled WPF binding metadata | popup ownership assertionだけ退役し、toolbar geometry/clear/help/warning は維持 |

No new ProcessIntegration, LargeFixture, foreground-input allowlist, physical cursor, fixed sleep, raw dispatcher/HWND helper, private reflection, source-text assertion, broad snapshot, exact translated-copy assertion, or docs-prose test is approved.

## Units, dependencies, and ownership

### Unit 0 — contract and plan

Owner: root. This file and `KSA-2026-01` freeze decisions, oracle, coverage, ownership, and replan triggers before production/test implementation.

### Unit 1 — assistance core, catalog, saved-query owner, composition

Depends on Unit 0. One `implementation-worker` owns this unit. It must first establish base-fail or targeted negative-control evidence where the packet requires changed semantics, then implement the approved contract. It may keep temporary compatibility adapters needed by the unchanged View route; Unit 2 removes those after switching the editor.

Writable paths:

- `BeMusicSeeker/ViewModels/GridKeywordSearchQuery.cs`
- `BeMusicSeeker/ViewModels/GridKeywordSearchCompletion.cs`
- `BeMusicSeeker/ViewModels/KeywordSearchHistoryStore.cs`
- `BeMusicSeeker/ViewModels/KeywordSearchHistorySettingsStore.cs`
- `BeMusicSeeker/ViewModels/KeywordSearchPresentationText.cs`
- new narrowly scoped `BeMusicSeeker/ViewModels/KeywordSearch*.cs` core/store files
- `BeMusicSeeker/ViewModels/ApplicationComposition.cs`
- `BeMusicSeeker/ViewModels/MainWindow/ChartListFilterViewModel.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.KeywordSearch.cs`
- `BeMusicSeeker/Properties/Settings.cs`
- `app.config`
- `BeMusicSeeker.Tests/GridKeywordSearchQueryTests.cs`
- new `BeMusicSeeker.Tests/KeywordSearchSavedQueryStoreTests.cs`
- `BeMusicSeeker.Tests/KeywordSearchPresentationTests.cs`
- `BeMusicSeeker.Tests/ChartListFilterViewModelTests.cs`
- `BeMusicSeeker.Tests/PlaylistWorkspacePresentationStateTests.cs`
- `BeMusicSeeker.Tests/ApplicationCompositionTests.cs`

Unit 1 may update other composition call sites only when compiler errors prove they are direct API consumers; it must report each added path. `Views/MainWindow.xaml`, `Views/MainWindow.cs`, localization files, user docs, feature spec, and WPF tests remain read-only.

Handoff must include changed paths, implemented Contract IDs, base-red/negative-control artifacts, focused Quick result, old tests/routes and replacements, shared-resource safety, API adapters left for Unit 2, and remaining risks.

### Unit 2 — reusable WPF editor, localization, docs, old route retirement

Depends on the completed Unit 1 API. The same or a fresh single `implementation-worker` owns this unit after Unit 1 handoff; no concurrent writer is allowed.

Writable paths:

- new `BeMusicSeeker/Views/KeywordSearchEditor.xaml`
- new `BeMusicSeeker/Views/KeywordSearchEditor.xaml.cs`
- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/Properties/Resources.resx`
- `BeMusicSeeker/Properties/Resources.cs`
- `lang/en-US.json`, `lang/fr-FR.json`, `lang/ja-JP.json`, `lang/ko-KR.json`, `lang/zh-CN.json`, `lang/zh-TW.json`
- `BeMusicSeeker.Tests/MainWindowChartPresentationWpfTests.cs`
- `BeMusicSeeker.Tests/LocalizationResourceParityTests.cs` only if parity mechanics, not expected copy, require adjustment
- `docs/keyword-search-syntax-guide.md`
- `docs/keyword-search-syntax-guide.ja.md`
- new `devdocs/spec/keyword-search-assistance.md`
- this plan for worker evidence only after root hands it off

After the WPF route compiles and tests, Unit 2 may make deletion-only cleanup of obsolete popup/header/forceHistory compatibility members in the Unit 1 ViewModel/presentation files. Any new core semantics or API redesign returns to root for replan. It must preserve existing toolbar geometry, clear/help/warning bindings, parser behavior, and 500 ms filter binding.

Handoff must include all Contract IDs exercised through the compiled editor, section cap/scroll and accessibility evidence, restored negative controls, focused Quick result, localization parity, removed old routes/resources/tests, and any UI behaviors that could not be deterministically observed.

## Verification

Focused Quick filter:

```text
FullyQualifiedName~GridKeywordSearchQueryTests|FullyQualifiedName~KeywordSearchSavedQueryStoreTests|FullyQualifiedName~KeywordSearchPresentationTests|FullyQualifiedName~ChartListFilterViewModelTests|FullyQualifiedName~PlaylistWorkspacePresentationStateTests|FullyQualifiedName~ApplicationCompositionTests|FullyQualifiedName~MainWindowChartPresentationWpfTests|FullyQualifiedName~LocalizationResourceParityTests
```

Each worker runs the subset it owns through `scripts/verify-refactor.ps1 -Mode Quick`. Root runs the full focused filter on the integrated snapshot, then exactly one normal `-Mode Functional` acceptance run. `MainWindowPlayHistoryWpfTests` remains unchanged collateral coverage in Functional. Root also audits `git diff --check`, settings/resource parity, unexpected paths, and retained parser/filter contracts.

After implementation workers are closed, root freezes the snapshot and delegates a static review with the full packet, base/head, diff, negative-control evidence, and verification results. Root performs no repository read/write/build/test while the reviewer is active. P0/P1 and acceptance-direct P2 findings are fixed and reverified; recommendations remain scoped.

## Replan triggers

- A candidate catalog requires changing parser/matcher semantics rather than sharing an explicit vocabulary authority.
- Additive Favorites persistence requires migration, renaming, or reinterpretation of existing history settings.
- The reusable editor cannot preserve toolbar geometry or must move feature state into MainWindow/root ViewModel.
- Dynamic playlist/ref/table completion requires hot-path DB access, a lock held across UI work, or a mutable broad host.
- Deterministic WPF verification would require foreground activation, physical cursor, fixed waits, a new runner allowlist, or test-only public production API.
- The settings adapter cannot provide atomic setter-before-publication behavior without changing the approved failure contract.
- Any worker believes an authority-backed canonical set, scope, edit rule, focus/IME behavior, persistence rule, or allowed variation must change to make tests pass.

## Evidence

- Independent Test Contract Packet `KSA-2026-01` completed before implementation. Phase A used only approved user authority; Phase B inspected repository seams solely for coverage placement and test safety.
- Plan clarification resolved that Unit 0 → Unit 1 → Unit 2 is the only safe order and that one writer is the maximum useful concurrency.
- Unit 1 implemented the immutable assistance/catalog/apply core and the scoped saved-query owner. Its focused Quick passed 134/134 in `artifacts/verification/tests-quick-20260830-144455/functional/results.trx`. A targeted wrong normal-clear catalog failed the canonical-set oracle and was restored before head verification.
- Unit 2 replaced the MainWindow-owned single ListBox/popup route with `KeywordSearchEditor`. The old route failed `SearchEditorRouteOwnsPopupStateInsteadOfViewModel` in `artifacts/verification/tests-quick-20260830-150023/functional/results.trx`; the initial integrated KSA focused Quick passed 157/157 in `artifacts/verification/tests-quick-20260830-155019/functional/results.trx`.
- Integration audit rejected XAML comments and namescope aliases that existed only to satisfy old source-marker tests. The same unit then removed those fallbacks, the unused ViewModel popup/header/`forceHistory` state machine, duplicate history mirrors, and their brittle assertions. The replacement tests use the actual editors and immutable section state. Targeted cleanup Quick passed 131/131 in `artifacts/verification/tests-quick-20260830-160740/functional/results.trx`.
- Final KSA focused Quick passed 205/205 in `artifacts/verification/tests-quick-20260830-160932/functional/results.trx`. The obsolete-route audit found no old popup/list/header/`forceHistory`/alias/resource token in production, tests, user docs, or current specs. `git diff --check` was clean and no WPF temporary project or residual test process remained.
- The pre-review integrated Functional run completed successfully in 164.3 seconds at `artifacts/verification/tests-functional-20260830-161255`. The remaining lane reported 2760 passed and 8 skipped; every host exited successfully and the runner confirmed that the tracked-worktree fingerprint remained unchanged.
- The first frozen static review found quoted-value replacement and suffix-transition defects, inaccessible saved-row actions, missing virtualization/content-size and active-IME evidence, an implicit Favorites fallback, retained obsolete completion production code, incomplete catalog/output coverage, and missing history setter-failure atomicity coverage. Remediation preserved `KSA-2026-01`: the core/parser-facing edit span, explicit settings dependency, exact catalogs and output matching, persistence failure tests, retired route deletion, keyboard focus cycle, independent virtualized section viewports, and routed active-IME negative control were implemented without weakening the packet.
- Resolver verification passed the targeted saved-row keyboard test 1/1 at `artifacts/verification/tests-quick-20260830-184402/functional/results.trx` and the full WPF fixture 20/20 at `artifacts/verification/tests-quick-20260830-184529/functional/results.trx`. The final full KSA focused Quick passed 218/218 at `artifacts/verification/tests-quick-20260830-184753/functional/results.trx`; `git diff --check` was clean, no obsolete production route or fallback remained, and no WPF temporary project or residual test process remained.
- The post-remediation final Functional run completed successfully in 153.7 seconds at `artifacts/verification/tests-functional-20260830-185026`. The remaining lane reported 2768 passed and 8 skipped; every host exited successfully and the runner confirmed that the tracked-worktree fingerprint remained unchanged.
- The first fresh review confirmed eight of the eleven original findings were fully resolved and found three remaining acceptance violations: handled non-empty IME commit could leave the composition guard active, `Shift+Tab` from a selected Field skipped the first saved-row action fallback, and assistance separator detection did not share the parser's `char.IsWhiteSpace` rule. The remediation added the reliable handled-event IME completion/lifecycle reset, actionable-row fallback, parser-equivalent unquoted whitespace detection, and independent routed/core regression tests. Its base-red run failed the intended three behaviors at `artifacts/verification/tests-quick-20260830-190721/functional/results.trx`.
- The final explicit eight-fixture KSA Quick passed 170/170 at `artifacts/verification/tests-quick-20260830-191653/functional/results.trx`; the runner confirmed the tracked-worktree fingerprint was unchanged. The post-second-review Functional run completed successfully in 180.6 seconds at `artifacts/verification/tests-functional-20260830-191739`, exceeding the 180-second reporting target by 0.6 seconds but remaining within the 300-second hard budget. The remaining lane reported 2769 passed and 8 skipped, every host exited successfully, and the tracked-worktree fingerprint remained unchanged.
- The second fresh review found one remaining acceptance-direct P2: assistance treated a backslash outside quotes as escaping the next character, while the parser only escapes inside quotes. All assistance lexical scanners now use the parser-equivalent quote/backslash state, and an independent unmatched quoted-token negative control failed before the fix at `artifacts/verification/tests-quick-20260830-192946/functional/results.trx`. The final eight-fixture KSA Quick passed 171/171 at `artifacts/verification/tests-quick-20260830-193252`.
- The first final-snapshot Functional attempt at `artifacts/verification/tests-functional-20260830-193426` had one non-timeout failure in the unchanged `SettingsForegroundInteractionTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes`: selection moved correctly but its independent keyboard-focus assertion failed. The same test had passed in the preceding 18 same-day artifacts, passed its focused Quick 1/1 at `artifacts/verification/tests-quick-20260830-194535/functional/results.trx`, and did not touch keyword-search code. After investigation, the same final snapshot and canonical fanout completed successfully in 168.3 seconds at `artifacts/verification/tests-functional-20260830-194626`; the remaining lane reported 2770 passed and 8 skipped, every host exited successfully, and the tracked-worktree fingerprint remained unchanged. Both failure and recovery evidence are retained rather than hiding the transient foreground-focus interference.
- The final fresh static review inspected the lexical remediation and its directly affected invariants without changing or executing the repository. It confirmed parser-equivalent outside/inside backslash handling, quote and whitespace state, token/replacement spans, authority-backed negative control, and preservation of the previously closed findings. It reported no P0/P1, no acceptance-direct P2, no pre-existing/out-of-scope blocker, and no recommendation.
