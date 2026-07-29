# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [性能register](./PERFORMANCE_REGRESSION_REGISTER.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md)

## 1. 正本

1. `PLAN_STATUS.md`を読む。
2. active outcomeの計画とregisterを読む。
3. active implementation batchに未完unitがあればplannerを起動せず、記載順に進める。

過去のunit、commit、review logはGit historyへ委ねる。

## 2. Orchestration

- planner／reviewerはsingle-flightで使い、実行中はrootのrepository操作を凍結する。
- active batchが空、batch完了後もexit未達、または具体的evidenceでbatch前提が崩れた場合だけplannerを起動する。
- rootはplanner scopeを独立再調査しない。指定route、symbol、fixtureだけをboundedに確認する。
- `NO_SAFE_UNIT`は無効。複雑性はinteraction、owner、wait graph、synthetic measurement corridorへ分解する。
- unit commitは内部checkpointであり、active outcomeが未完なら応答境界にしない。

## 3. Performance evidence hierarchy

### 3.1 Historical symptom evidence

`.tmp/net472_log`と`.tmp/.NET 10_log`は、一覧遷移、resource-health、GC、scan等の候補を見つけた一回ログである。今後は次を行わない。

- net472側への新しいmarker追加。
- net472 build／operationの再実行。
- net472とcurrentの厳密なp50／p90 A/B Gate。
- .NET 10 log schemaをnet472に合わせるための後退。

既存logはpriorityとsource correlationにだけ使う。

### 3.2 Synthetic engineering evidence

Codexが性能測定を行ってよいのは、repository内でproduction dataなしに再構築できるcorridorだけである。

synthetic corpusは次を持つ。

- fixed seedまたは固定fixture。
- small／medium／largeの規模parameter。
- field／resource／path分布の明示。
- golden behavior／output。
- temporary pathとcleanup。
- raw resultを再現するcommand。

user DB、user playlist、user chart tree、private package archiveのコピーを要求しない。corpusを作れないrouteは`MANUAL_REAL_DATA`として扱い、実測を待たずinstrumentationとhandoffでengineering unitを閉じる。

### 3.3 Current .NET 10 instrumentation

重要interactionは現在の.NET 10 codeだけに、一つのcorrelation IDで次を記録する。

```text
input / selection accepted
→ owner request queued / started
→ snapshot / query / projection
→ UI presentation queued / started
→ view / ItemsSource applied
→ first useful visible
```

startup、scan、install estimation、parserはstage名、item count、cache hit、allocation candidateをaggregateで記録する。per-row／per-file loggingを行わず、disabled時に不要なmessage allocationを発生させない。

### 3.4 Post-engineering real-data evidence

実データを使う起動、一覧画面、導入先推定、scanの計測は、全code／test／publish作業完了後にユーザーが[MANUAL-02](./POST_MIGRATION_MANUAL_ACCEPTANCE.md#manual-02-real-data-performance-acceptance)として一度行う。Codexのactive outcome、planner停止条件、`EXTERNAL_BLOCKER`へ入れない。

## 4. Unit contract

performance unitは次を閉じる。

1. production routeとbehavior／concurrency invariant。
2. corpus feasibilityの判定。
3. `SYNTHETIC_MEASURABLE`なら同じ.NET 10 pathの変更前evidence。
4. implementationと不要surfaceの退役。
5. behavior test、structural counter、変更後synthetic evidence。
6. `MANUAL_REAL_DATA`ならcurrent .NET 10 instrumentationとmanual script coverage。
7. fresh review、指摘修正、再検証。
8. `PLAN_STATUS.md`とcurrent-only register更新。

短時間のwall-clock thresholdを通常unit testへ埋め込まない。通常testではmaterialization count、queue count、generation、cache invalidation、output equality等のdeterministic invariantを固定する。

## 5. Optimization order

1. 現行.NET 10のblind intervalを低負荷instrumentationで可視化する。
2. sourceで確認できる不要なUI-thread全件copy、重複projection、stale work、unbounded drainを除去する。
3. synthetic corpusを作れるcomponentのalgorithm、index、allocation、parallelismを改善する。
4. C# 14／.NET 10 APIはsynthetic evidenceが勝つhot pathだけに適用する。
5. full correctness、deadlock、publishを閉じ、実データ測定をMANUAL-02へhandoffする。

priority変更、UI全面disable、notification drop、timing markerの移動だけで改善扱いしない。

## 6. Concurrency invariants

- lock、transaction、reservation、operation gateを保持したまま別laneのUI完了を同期waitしない。
- owner外callback、event、dialog、UI schedulerをglobal mutation lock内から同期実行しない。
- producerはversion／immutable factをqueueして戻る。UI drainはcoalesceし、一つのdispatcher turnの仕事量をboundedにする。
- deadlock修正をperformanceのために巻き戻さない。shutdown／test drainは全guard解放後だけawaitする。
- incident専用journal／recovery architectureを追加しない。filesystem／song DB差分は既存file diffを収束経路とする。

## 7. C# 14／.NET 10 policy

- 言語機能やAPIの採用自体をunit objectiveにしない。
- parser、path、hash、tokenization、large aggregationでsynthetic allocation profileが支配的な場合だけ、span、indexed loop、pre-sized collection、read-mostly index等を候補にする。
- overload、encoding、culture、case、legacy parse behaviorのgolden testを先に置く。
- LINQは原則禁止ではない。hot pathで多重列挙、closure、iterator、全件materializationが確認された箇所だけ置換する。
- pooling／unsafe／parallelismはlifetime、cancellation、memory上限を説明でき、component evidenceで勝つ場合だけ採用する。

## 8. Verification

### Docs／agent only

UTF-8、LF、末尾改行、TOML／Markdown構文、relative link、table、whitespace、`git diff --check`を確認する。production差分がなければbuild／test／reviewは不要。

### Performance unit

- affected projectsのlocked restore、Release build、targeted behavior tests。
- synthetic corpusが成立する場合のbefore／after component run。
- deterministic materialization／queue／generation／allocation evidence。
- deadlock／heartbeat regression。
- frozen snapshotのfresh review。

### Final Engineering Gate

- full tests、analyzer／warnings。
- selected main-app／updater Self-contained publish。
- synthetic performance suiteとcurrent .NET 10 instrumentation contract。
- estimated-install、scan／parse golden behavior、deadlock regression。
- existing-data、update success／rollback。
- fresh outcome review。

real-data interaction benchmark、net472 run、runtime未導入machineはGateに含めない。

## 9. Manual acceptance／Release Freeze

実データ性能確認、`.NET Desktop Runtime`未導入machine／VM、署名、公開、BASS.NET entitlementは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。Codexは結果を待たない。

rootだけがwrite、stage、commitする。`git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで行わない。
