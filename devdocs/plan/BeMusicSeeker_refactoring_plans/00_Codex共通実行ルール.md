# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [作業register](./PERFORMANCE_WORK_REGISTER.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

## 1. 正本と実行

1. `PLAN_STATUS.md`を読む。
2. performance planとregisterを読む。
3. active implementation batchに未完unitがあればplannerを起動せず、記載順に進める。

過去のOutcome、unit、commit、review logはGit historyへ委ねる。

planner／reviewerはsingle-flightで使い、実行中はrootのrepository操作を凍結する。active batchが空、batch完了後もexit未達、または具体的evidenceでbatch前提が崩れた場合だけplannerを起動する。

unit commitは内部checkpointである。active outcomeが未完ならユーザー応答で停止しない。status-only progress commitを作らず、batch状態は対応するcode／test commitで進める。

## 2. 目的

目的は、現在の.NET 10アプリのユーザー体感性能を改善することである。

次は目的ではない。

- 差分量を小さくすること。
- 旧adapter、relay、PropertyChanged経路を残すこと。
- net472との厳密な対照実験。
- instrumentationを増やすだけで完了扱いにすること。
- synthetic testで再現できる部分だけを最適化し、既知の実画面遅延を放置すること。

大きめの変更でも、hot pathから不要なwork、fan-out、copy、queue、invalidationを除去し、behaviorとconcurrencyをtestできるなら採用する。

## 3. Evidence hierarchy

### 3.1 `DIRECT_FIX`

logとsourceから、次が明確な場合は実機benchmarkを待たず修正する。

- UI表示の`PropertyChanged`が無関係なsettings／domain更新へ伝播する。
- UI thread上の同期file logging。
- UI thread上の全件copy／materialization。
- 同一dataのcollection identity交換。
- data変更でcolumn layoutまでinvalidateする。
- 同一apply内の重複cache invalidation／subscription rebuild。
- hidden／inactive control向けのrebuild。
- 不要なDispatcher／Task.Run／owner relay。
- source clearとsource applyを別々のvisible transactionにする。
- production consumerのないbinding state／notification。

### 3.2 `LIKELY_OPTIMIZATION`

一般的なcost modelとcurrent sourceから高速化が強く見込まれ、behavior／concurrency testで安全性を確認できる変更は、作業中に絶対時間を測定できなくても実装する。

例:

- stable source／presentation identityの再利用。
- typed eventへの置換とsubscriber範囲の限定。
- UI外でのsummary text／projection作成。
- keyed／versioned snapshotによる一括apply。
- cache invalidationのdimension分離。
- stale generationの早期中断。
- 重複enumeration、normalization、hash、lookupの統合。
- 起動必須でないmaintenance／warmupのidle移動。
- pre-sized collection、span、indexed loopなど明確なallocation削減。

### 3.3 `MEASURED_OPTIMIZATION`

既存fixtureまたは小さいfixed-seed corpusで安全に測れる場合はcomponent evidenceも取る。ただし、新しい巨大なbenchmark infrastructureを最適化の前提にしない。

### 3.4 最終実機確認

実データでの起動、playlist summary／detail、library一覧、導入先推定、scanは全engineering作業後にユーザーが一度確認する。Codexは結果を待たず、active outcomeや`EXTERNAL_BLOCKER`にしない。

既存のnet472 logは症状と優先度の参考にだけ使用し、再build／再計測／marker追加を行わない。

## 4. Hot-path設計規則

- `PropertyChanged`はpresentation propertyの変更通知であり、playlist table／catalog／domain変更eventとして代用しない。
- settings dialogや非active featureはtyped version／dirty flagを受け、必要になった時点でsnapshotを読む。
- table表示はrows、column schema、selection、modeを可能な限り一つのtyped presentation commitで適用する。
- source identityを毎回交換せず、versioned read-only sourceまたは一回のresetで更新する。
- row data変更とcolumn layout変更を別のinvalidation dimensionとして扱う。
- activeでないcontrolのItemsSource／Columns／selection restoreをhot pathで更新しない。
- performance markerはtimestampをproducer側で採取し、buffered writerへ渡す。UI threadでfile flushを待たない。
- diagnostic logging無効時はmessage文字列やfields objectを作らない。
- cacheはowner、version、invalidation、size／lifetimeを持つ。
- `Task.Run`、Dispatcher hop、immutable DTO copyは、それ自体をarchitecture上の正しさとみなさない。必要な境界だけ残す。

## 5. Concurrency／behavior invariants

- model lock、transaction、reservation、operation gateを保持したまま別laneのUI完了を同期waitしない。
- owner外callback、event、dialog、UI schedulerをglobal mutation lock内から同期実行しない。
- producerはversion／immutable factをqueueして戻る。UI drainはcoalesceし、一つのDispatcher turnの仕事量をboundedにする。
- deadlock修正をperformanceのために巻き戻さない。
- notificationを黙って捨てたり、stale dataを表示したり、priority変更だけで遅延を隠したりしない。
- sort、filter、selection、scroll、editing、playlist／library state、file／DB互換性を維持する。
- incident専用journal／recovery architectureを追加しない。filesystem／song DB差分は既存file diffを収束経路とする。

## 6. Unit contract

各unitは次を同じcycleで閉じる。

1. end-to-end routeと現在の無駄を特定。
2. `DIRECT_FIX`／`LIKELY_OPTIMIZATION`／`MEASURED_OPTIMIZATION`を分類。
3. 不要なwork、notification、copy、hop、invalidation、unused stateを削除。
4. behavior／concurrency／data testを更新。
5. deterministicに確認できるwork countを固定。
6. synthetic corpusが既に安全にある場合だけcomponent run。
7. fresh static review、指摘修正、再検証。
8. `PLAN_STATUS.md`とregisterをcurrent stateへ更新。
9. commit後、active outcomeが未完なら次unitへ進む。

production benchmarkやnet472比較の欠如だけでunitを延期しない。instrumentation-onlyで既知の遅延を閉じない。

## 7. Verification

### Docs／agent only

UTF-8、LF、末尾改行、TOML／Markdown構文、relative link、table、whitespace、`git diff --check`を確認する。production差分がなければbuild／test／reviewは不要。

### Performance unit

- affected projectのRelease buildとtargeted behavior tests。
- deadlock／thread-affinity regression。
- notification、source replacement、invalidation、materialization、queue countのdeterministic test。
- existing synthetic corpusがある場合のcomponent evidence。
- frozen snapshotのfresh review。

### Final Gate

- 全5 projectのlocked restore、Release build、full tests、analyzer／warnings。
- selected main-app／updater Self-contained publish。
- deadlock regression、estimated-install、scan／parse golden behavior。
- existing-data、update success／rollback。
- hot-path structural audit。
- final .NET 10 real-data logを読める低負荷instrumentation contract。
- fresh outcome review。

real-data measurement、net472 run、runtime未導入machineはGateに含めない。

## 8. Release Freeze

rootだけがwrite、stage、commitする。unrelatedな差分へ触れない。

`git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで行わない。
