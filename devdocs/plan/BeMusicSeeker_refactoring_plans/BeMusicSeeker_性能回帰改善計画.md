# BeMusicSeeker .NET 10 性能改善計画

[現在地](./PLAN_STATUS.md) / [性能register](./PERFORMANCE_REGRESSION_REGISTER.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [historical symptom note](../../acceptance/net472-net10-interaction-baseline.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

## Outcome

`PERF-01 .NET 10 performance engineering closure`

目的は、MVVM ownership、data compatibility、deadlock safetyを維持したまま、現在の.NET 10アプリに残る不要なcopy、queue、allocation、index rebuild、GC pauseを減らすことである。net472との厳密な対照実験は行わない。

最優先は次である。

1. playlist summary、playlist detail、通常ライブラリ一覧の遷移。
2. startup／initialization内のsong-table、resource-health、GC。
3. install destination estimation。
4. library construction、managed file diff、BMS／BMSON／chart-info parsing。

## Evidence policy

- 既存のnet472／.NET 10 logは、秒単位遅延の候補を発見したhistorical symptom evidenceとして保持する。
- 今後はnet472へmarkerを追加せず、再build／再計測もしない。
- Codexの性能evidenceは、production dataを必要としないsynthetic corpus、deterministic structural counter、current .NET 10 instrumentationから構成する。
- full WPF render、実library分布、Everything／disk／antivirus、実運用cacheに依存する性能は`MANUAL_REAL_DATA`へ分類し、全engineering作業後にユーザーが確認する。
- synthetic結果から実データの絶対時間を断定しない。algorithmic work、allocation、queue、scale behaviorを改善したことを示す。

## Current findings

既存logとcurrent sourceから、次の優先候補がある。

- 通常ライブラリでは、UI lane上でbackground summary用のordered rowsを約21万件materializeするrouteがある。
- playlist summaryはcompute後のUI queue、ItemsSource apply、first renderが現行markerの外にある。
- playlist detailはowner request以前のmode transition／preparation renderにblind intervalがある。
- resource-health、song-table materialization、post-init forced GCはstage別のallocation／retention evidenceが不足している。
- install estimation、managed scan、parserにはrepository-owned fixtureと生成helperがあり、production dataなしのcomponent corpusを作れる見込みがある。

これらは修正候補であり、net472比thresholdではない。

## Synthetic corpus feasibility

| Corridor | Classification | Repository evidence / intended corpus | Limit |
|---|---|---|---|
| normal library projection／summary | `SYNTHETIC_MEASURABLE` | `RegularChartListOwnerTests.cs`のrow／apply helperを基に、in-memory `LibraryChartRow`、sort index、folder／resource stateをfixed seedで生成 | actual WPF render timeは`MANUAL_REAL_DATA` |
| playlist summary／detail compute | `SYNTHETIC_MEASURABLE` | `PlaylistSummaryAggregationTests.cs`等を基に`BMSTable`、entries、summary rows、small／medium／large tableを生成 | GPU／layout／real column configurationはmanual |
| UI queue／generation／drain | `SYNTHETIC_MEASURABLE` | dedicated STA／fake schedulerとexplicit barrierでqueue count、coalescing、first applyを検証 | absolute user-visible latencyはmanual |
| install destination estimation | `SYNTHETIC_MEASURABLE` | `BmsLibraryInstallEstimationServiceTests.cs`が71／100／399 resource規模をtemp directoryで生成済み | real package distributionはmanual |
| BMS／BMSON parse | `SYNTHETIC_MEASURABLE` | `BmsLibraryInitializationServiceTests.cs`／`BmsonSongParserTests.cs`のgenerated BMS／BMSONとrepository fixtures | unknown real chart distributionはmanual |
| managed file diff／DB apply | `SYNTHETIC_MEASURABLE` | `BmsLibraryInitializationServiceTests.cs`が120／160 chart corpusを生成済み | Everything native enumerationはmanual |
| song-table／resource-health component | `FEASIBILITY_REQUIRED` | temporary SQLite／synthetic chart-resource snapshotでownerを分離できる場合のみ測定 | ownerを安全に分離できなければinstrumentation-only |
| full startup／first render／Everything／disk | `MANUAL_REAL_DATA` | final artifactの.NET 10 logで確認 | Codex Gateへ入れない |

corpus feasibilityはP1でcurrent codeに対して確定する。`FEASIBILITY_REQUIRED`が成立しない場合、test-only seamやproduction-like mock architectureを増やさず`OBSERVABILITY_REQUIRED`へ移す。

## Active implementation batch

### `P1 OBSERVABILITY-AND-CORPUS`

1. current .NET 10だけにinteraction correlationを追加する。

```text
input accepted
owner queued / started
snapshot / query / projection
UI queued / started / applied
first useful visible
```

2. startup、resource-health、song-table、install estimation、scan、parserはaggregate stage eventを追加する。
3. logはdiagnostic switch／levelで有効化し、disabled時にmessage文字列やper-item objectを作らない。
4. [current evidence](../../acceptance/net10-performance-engineering.md)へcorpus matrix、generator、seed、規模、commandをmaterializeする。
5. synthetic corpusを作れないrouteは`MANUAL_REAL_DATA`へ移し、今後の実機sessionに必要なmarkerだけを残す。
6. net472 code、log、scriptには触れない。

Exit:

- critical routeの.NET 10 log schemaが一貫し、build値とend-to-end markerを混同しない。
- 各corridorが`SYNTHETIC_MEASURABLE`、`OBSERVABILITY_REQUIRED`、`MANUAL_REAL_DATA`のいずれかへ確定する。
- production dataを要求するtest／scriptを追加していない。

### `P2 LIST-TRANSITION-CRITICAL-PATH`

- 通常ライブラリのUI-thread ordered-row materializationを廃止し、immutable source＋index snapshotをbackground summaryへ渡す。
- 同じ全件copyを別threadへ移すだけの修正にせず、copy自体を不要にする。
- fixed-seed list corpusでrow count、sort／filter、folder summary、allocation、materialization countを検証する。
- playlist summaryはqueue／apply／first-visible markerを追加し、同一versionの重複presentation、obsolete generation、不要なcollection replacementがsynthetic evidenceで確認された場合だけ除去する。
- playlist detailはselection→request、mode swap、preparation applyを計測し、不要なWPF turn／旧view再適用だけを修正する。
- normal refresh drainはproducerのnon-blocking contractを維持し、一turnのwork量とreschedule countをdeterministic testで固定する。

Exit:

- UI lane上のsummary用全件copyが0である。
- output、sort、filter、selection、scroll、rapid reentry、deadlock regressionが通る。
- synthetic large corpusで対象hotspotのelapsedまたはallocationが変更前より改善し、別stageへworkを隠していない。
- actual first-renderの合否はMANUAL-02へhandoffできるlogを持つ。

### `P3 STARTUP-INDEX-GC-COMPONENTS`

- full startupの実データbenchmarkは行わない。
- song-tableとresource-healthはtemporary SQLite／synthetic snapshotsでownerを分離できる場合だけcomponent corpusを作る。
- key normalization、target enumeration、warning projection、dictionary／set allocation、publicationを別stageにする。
- capacity、identity key reuse、single-pass index、zero-warning allocation削減はsynthetic evidenceがある場合だけ採用する。
- post-init `GC.Collect()`は、synthetic componentとcurrent .NET 10 telemetryでpause、retained bytes、直後のqueue backlogを観測する。full startup条件を再現できない場合は、推測で削除せずMANUAL-02のmarker対象にする。
- startup background jobのdependency、重複warmup、同一index rebuildはsource／structural evidenceで閉じる。

Exit:

- corpusが成立したcomponentに不要なmaterialization／allocation regressionがない。
- corpusが成立しないstageは低負荷instrumentationとmanual classificationを持つ。
- full startupの改善をdevelopment dataだけで断定していない。

### `P4 ESTIMATION-SCAN-PARSE-COMPONENTS`

#### Install destination estimation

- existing temp-directory helperを使い、chart数、candidate directory数、audio／visual resource数、hash hit率を固定seedで生成する。
- snapshot、index build、candidate enumeration、score、warning、result sortを分ける。
- versioned index reuse、重複enumeration、per-candidate allocation、parallel crossoverを同じ.NET 10 componentで比較する。
- progress／cancellationはper-item UI dispatchを発生させない。

#### Managed scan／construction

- generated BMS／BMSON corpusとpre-enumerated path snapshotでdecode、diff、parse、DB applyを測る。
- Everything native query、actual disk／antivirus性能は測定対象外とし、current logのstage boundaryだけ用意する。

#### Parser

- repository fixtureとgenerated small／large／pathological chartをgolden outputへ固定する。
- measured allocation topに限り、span、indexed loop、pre-sized collection等を適用する。
- encoding、culture、case、conditional／random command、diagnostic内容を維持する。

Exit:

- synthetic corpusが成立するcomponentで変更前よりelapsedまたはallocationが改善し、golden behaviorが一致する。
- production dataやnet472 runを要求していない。
- native／external stageを改善済みと誤認していない。

### `P5 ENGINEERING-PERFORMANCE-GATE`

- 全5 projectのlocked restore、Release build、full tests、Roslynator／warnings。
- deadlock regression、estimated-install normal completion、startup／scan／parse golden behavior。
- selected main-app／updater Self-contained publish、existing-data、update success／rollback。
- synthetic performance suiteとcurrent-only report。
- instrumentation disabled pathの低負荷性、log schema、manual operation coverage。
- frozen snapshotのfresh outcome review、重大指摘修正後の再検証／fresh review。

P5に次を含めない。

- net472 build／logging／repeat run。
- production DB／playlist／chart treeを使うbenchmark。
- actual WPF first-renderの統計Gate。
- Everything／disk／antivirusのperformance Gate。
- userの実データsession結果待ち。

### `HANDOFF`

P5通過後、Codexのengineering performance readinessをcompleteとし、[MANUAL-02](./POST_MIGRATION_MANUAL_ACCEPTANCE.md#manual-02-real-data-performance-acceptance)へhandoffする。ユーザーはfinal artifactで一度の実データsessionを行い、残る秒単位stallがあれば新しいfollow-up outcomeを開始する。

## Engineering acceptance

| Scope | Required |
|---|---|
| correctness／data／deadlock | regression 0 |
| known full-list copy | UI lane materialization 0 |
| synthetic component | fixed corpusで変更前より対象hotspotのelapsedまたはallocationが改善し、output一致 |
| queue／generation | duplicate／obsolete applyとone-turn workがdeterministic testでbounded |
| instrumentation | current .NET 10 interactionがinputからfirst-visibleまで相関可能。disabled時にper-item overheadなし |
| unsupported real-data route | `MANUAL_REAL_DATA`分類、marker、manual scriptあり |
| net472 | 新しい作業・Gateなし |

wall-clock thresholdを全componentへ一律適用しない。各unitは変更前に対象metricとsuccess conditionを固定し、短いtimer noiseではなくallocation、operation count、scale curveも併用する。

## Exit

```text
strict Refactoring Completion Gate: met
concurrency / responsiveness acceptance: met
.NET 10 engineering performance readiness: met
engineering migration: complete
active outcome: none
active implementation batch: empty
post-engineering real-data performance acceptance: pending user action; non-blocking
```
