# BeMusicSeeker .NET 10 ユーザー体感性能改善計画

[現在地](./PLAN_STATUS.md) / [作業register](./PERFORMANCE_WORK_REGISTER.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [historical log note](../../acceptance/net472-net10-interaction-baseline.md)

## Outcome

`PERF-02 .NET 10 user-visible performance acceleration`

MVVM ownership、existing-data互換性、解消済みのdeadlock safetyを維持しながら、現在の.NET 10アプリで残っている一覧画面の秒単位遅延と、起動・導入先推定・scan／parseの不要workを除去する。

このOutcomeでは、差分の小ささよりhot pathの単純さを優先する。production benchmarkを作業中に取得できないことは、構造上妥当な高速化を延期する理由にしない。

## Review decision

reviewed HEAD:

```text
72445a5029ba12356ac50340e4a399f7292f349f
```

最近の作業には有効な改善が含まれる。

- normal-library refreshのproducer-side synchronous UI waitは退役しており、既知deadlockは再発していない。
- background summary用のUI-thread全件ordered-row copyは削除された。
- playlist detailのowner request後の処理は、今回の.NET 10 logで約97～175 msであり、net472 sampleより速い。
- startup readyは今回のsampleで.NET 10側が短く、startup全体は最優先問題ではない。
- install estimation、scan、parser、resource indexにはsyntheticなallocation／operation削減が入っている。

一方、性能Outcomeを完了扱いにした判断は早かった。synthetic componentの改善と実画面の体感改善が一致していない。

## Current log findings

比較は厳密なbenchmarkではなく、修正優先度を決める症状evidenceとして使用する。

| Route | Current observation | Decision |
|---|---|---|
| playlist summary初回 | compute約323 msに対し、inputからfirst visibleまで約1.18 s | compute後のUI applyを直接改善する |
| playlist summary再訪 | compute約15 msでもfirst visibleまで約0.90 s | cache computeではなくsource replacement／binding／notificationが支配的 |
| playlist detail | requestからvisibleまで約97～175 ms | 高速なrouteを保護し、全面rewriteしない |
| playlist detail→full library | rows／columnの計測値はほぼ0～1 msだがfirst visibleまで約1.17 s | related presentation、old source clear、binding fan-outのblind intervalを除去する |
| startup | current sampleはnet472 sampleより短い | 最優先にせず、明確な不要workだけ改善する |

## Confirmed structural defects

### 1. Settings dialogの広すぎるplaylist購読

`ISettingsDialogWorkspacePort.SubscribePlaylistTableChanges`は、実際には`PlaylistWorkspaceViewModel.PropertyChanged`全体を購読している。

そのため、playlist summary rows、summary text、column visibility、detail mode、binding mode等のpresentation変更が、settings dialogのdirectory property通知とpreset dirty処理へfan-outする。

これは`DIRECT_FIX`である。

恒久形:

- playlist table／catalogの実変更専用typed eventまたはversionを作る。
- settings dialogはそのeventだけを購読する。
- dialogが非表示ならdirty/versionだけを更新し、directory snapshotは表示時に読む。
- summary／detail／libraryのpresentation property変更ではsettings notificationを発火しない。

### 2. Playlist summaryのreplace-all UI apply

summary applyは毎回UI threadで次を実行する。

```text
new ObservableCollection<PlaylistSummaryRow>(rows)
total chart Sum
PlaylistSummaryView identity replacement
PropertyChanged
selection restore
CustomTableView full invalidation
```

再訪時にcomputeが15 msでも約0.9 sかかるため、summary source identity、binding、selection、table invalidationを見直す。

恒久形:

- stableなversioned summary sourceをownerが保持する。
- background側でsummary textとpresentation snapshotを完成させる。
- UIは一つのtyped presentation commitを適用する。
- 内容とversionが同一ならsource交換、column rebuild、selection restoreを行わない。
- replace-all時も一回のdata resetと必要なrow／selection invalidationだけにする。

### 3. CustomTableViewの過剰invalidation

`OnItemsSourceChanged`はdata source変更だけでもcolumn layout snapshotを破棄し、cell cache、selection、scroll、row subscription、redrawを一括更新する。

恒久形:

- row data、column schema、text metric、selection、scrollを別dimensionとしてinvalidateする。
- data-only swapではcolumn layoutを維持する。
-同じapply内でcell cacheを二重破棄しない。
- visible row subscriptionを差分更新する。
- presentation snapshotを一回applyし、rowsとcolumnsのPropertyChanged順序へ依存しない。

### 4. Main-list mode transitionのpresentation fan-out

通常libraryへのcommit後に、playlist workspaceがcolumn visibility、summary columns、detail active、async binding stateを個別PropertyChangedする。

さらにdetail source clearを新source applyより前に公開している。

恒久形:

- main tableのrows、column schema、modeを一つのtyped presentation transactionで適用する。
- old detail sourceのretireはnew sourceのownership transfer後に行う。
- production consumerのない`UseAsyncChartRowsViewBinding`等のstate／notificationを退役する。
- 非active summary controlやsettings dialogへhot-path notificationを伝播させない。

### 5. Performance loggingの同期file I/O

current performance markerはUI apply pathからNLogの`FileTarget`へ同期的に書き込まれる。

恒久形:

- timestampとsmall value payloadは呼出laneで取得する。
- performance eventはbounded async queue／bufferへenqueueする。
- writerはUI外でbatch flushする。
- queue overflowはdropped countをaggregate記録し、UIをblockしない。
- fatal／errorの通常log semanticsは変更しない。
- diagnostic無効時はmessage文字列を作らない。

## Ordered implementation batch

### F1 — `FANOUT-AND-DIAGNOSTICS`

分類: `DIRECT_FIX`

1. generic `PropertyChanged` playlist-table subscriptionをtyped catalog/table eventへ置換する。
2. settings dialogはlazy dirty/version方式へ変更する。
3. summary／detail／libraryのpresentation changeがsettings notificationへ流れないtestを追加する。
4. performance markerをnon-blocking buffered writerへ移す。
5. `UseAsyncChartRowsViewBinding`等、production consumerのないhot-path stateを退役する。
6. current logのblind intervalを、related-presentation、source apply、selection restore、table invalidationへ分割する。

exit:

- presentation property一件につきsettings handlerが走らない。
- UI threadがperformance file writeを待たない。
- deadlock invariantとlog correlationを維持する。

### F2 — `PLAYLIST-SUMMARY-APPLY`

分類: `DIRECT_FIX`＋`LIKELY_OPTIMIZATION`

1. stable versioned summary source／presentation snapshotを導入する。
2. summary total、text、row snapshotをbackground buildで完成させる。
3. cached revisitで同一source／column schemaを再利用する。
4. source変更、text変更、selection restoreを一つのterminal applyへまとめる。
5. inactive／stale generationのapplyをcollection生成前に棄却する。
6. summary tableのrow subscription、cache invalidation、render requestを一回へ抑える。
7. sort、filter、selection、context menu、editing、reload cleanupを維持する。

exit:

- cached revisitでnew `ObservableCollection`とfull binding source replacementを行わない。
-同一versionではtable invalidationとselection restoreが0回。
- changed versionでは一回のdata resetで表示を更新する。

### F3 — `MAIN-LIST-TRANSITION`

分類: `DIRECT_FIX`＋`LIKELY_OPTIMIZATION`

1. detail→library、summary→library、library→detailを一つのmain-table presentation commitへ統合する。
2. new rows／columns／modeを先にcommitし、old detail sourceのdispose／logはownership transfer後へ移す。
3. row dataだけの変更でcolumn schemaを再構築しない。
4. hidden summary tableと無関係なsettings／playlist propertiesを更新しない。
5. CustomTableViewへdata-only／schema-change／selection-onlyのfast pathを追加する。
6. `ItemsSource`変更時の重複cell cache invalidation、column layout invalidation、row subscription rebuildを削減する。
7. fastなplaylist detail build routeとvirtual library sourceを維持する。

exit:

- detail→libraryのhot pathに一般PropertyChanged fan-outがない。
- source clearとnew source applyが別のvisible transitionにならない。
- data-only source applyでcolumn layout rebuild countが0。
- first renderを要求するDispatcher turnがboundedである。

### F4 — `STARTUP-ESTIMATION-SCAN-PARSE`

分類: `LIKELY_OPTIMIZATION`＋既存の`MEASURED_OPTIMIZATION`

一覧以外の重要経路について、proof不足を理由に既知の不要workを残さない。

対象:

- startup／initializationのduplicate warmup、index rebuild、forced GC、非必須maintenance。
- install destination estimationのsnapshot、candidate enumeration、normalization、hash／resource lookup、progress。
- library construction／managed file diffのpath decode、row mapping、DB batch、index publication。
- BMS／BMSON／chart-info parserのsubstring、multiple enumeration、collection growth、dictionary lookup。

方針:

- startupでfirst useful windowに不要なworkはidle／post-visibleへ移し、cancellationとshutdownを持たせる。
-同じcatalog versionのindex／hash／normalized keyはowner-scoped cacheとして再利用する。
-小規模入力をparallel化せず、大規模入力だけbounded parallelismを使う。
- progressはitemごとにUIへ送らずcoalesceする。
- parserはgolden behaviorを守れる箇所でspan、indexed loop、pre-sized collection、single-pass処理を採用する。
- forced GCは必須根拠がなければcritical startup pathから外す。memory-pressureまたはidle ownerへ限定できる。
-実環境I/Oを再現できなくても、重複work／allocation／enumerationがsource上明確なら修正する。

### F5 — `APPLICATION-WIDE-PERF-AUDIT`

分類: `DIRECT_FIX`／`LIKELY_OPTIMIZATION`

一覧画面で見つかった同型問題を全featureへ横展開する。

探索対象:

- feature ViewModelの一般`PropertyChanged`をdomain eventとして購読するroute。
- UI thread上のsync log、file／DB、全件copy、large LINQ materialization。
-一操作で複数回発火するItemsSource／Columns／selection／visibility通知。
- hidden control向けapply。
- commit→publish→relay→Viewの重複Dispatcher hop。
- version／cacheがあるのに毎回再構築するindex／projection。
- defensive snapshotの多重copy。
- stale generation棄却が遅いroute。
- rapid reentryでqueueが蓄積するroute。

method一件ごとのunitは作らず、同じowner／invalidation contract／test scopeでまとめて修正する。

### F6 — `FINAL-PERFORMANCE-GATE`

1. 全5 projectのlocked restore、Release build、full tests、analyzer。
2. deadlock、rapid reentry、shutdown、estimated-install正常完了。
3. playlist summary／detail／libraryのstructural performance tests。
4. startup／estimation／scan／parseのgolden behaviorと既存synthetic suite。
5. selected main-app／updater Self-contained publish。
6. existing-data、update success、rollback。
7. performance logging disabled pathとbounded writer。
8. frozen snapshotのfresh outcome review、重大指摘修正後の再検証。

Gateは次を要求する。

- 既知の秒単位UI gapに対応するhot-path defectが未処理で残っていない。
- generic PropertyChanged domain busがない。
- UI thread synchronous performance file I/Oがない。
- summary revisitで無条件collection replacementがない。
- data-only table applyでcolumn layout rebuildを行わない。
- detail→libraryが一つのpresentation transactionである。
-解消済みdeadlockを復活させていない。
-最終実機確認に必要なinteraction markerが低負荷で残っている。

実データでの絶対時間は全engineering作業後の手動受入れで一度確認する。結果待ちをCodex工程へ含めない。
