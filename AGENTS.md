# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次を読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. active outcomeに対応する計画
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`
4. active register

`PLAN_STATUS.md`は現在地と現行evidenceだけを持つ。過去のunit、commit、review logはGit historyへ委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。planner後は指定routeのbounded feasibility checkだけを行う。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner、behavior corridor、synthetic measurement corridorへ分解する。
- active batchの状態更新は対応code／test unitと同じcommitに含め、status-only progress commitを作らない。
- unit commitは内部checkpointである。active outcomeが未完ならユーザー応答で停止せず次unitへ進む。

## Performance evidence

- 目的は現在の.NET 10アプリの性能改善である。net472 buildの再計測、logging変更、厳密なA/B Gateを行わない。
- `.tmp/net472_log`と`.tmp/.NET 10_log`は遅延候補を見つけたhistorical symptom evidenceであり、今後の合否基準にしない。
- Codexが性能測定を行ってよいのは、repository内で固定seedから再構築でき、production dataを必要としないsynthetic corpusがあるcorridorだけである。
- synthetic corpusを安全に作れないrouteは、無理にproduction-like dataを捏造しない。低負荷な.NET 10 instrumentation、behavior／structural test、`MANUAL_REAL_DATA` handoffで閉じる。
- 実データを使う起動・一覧遷移・導入先推定・scanの計測は全engineering作業後にユーザーが一度行う。Codexのactive outcome、planner停止条件、`EXTERNAL_BLOCKER`へ入れない。
- synthetic benchmarkは同じ.NET 10 code pathの変更前後を同じmachine、fixture、configurationで比較する。wall-clockだけでなくallocation、materialization count、queue count、algorithmic scaleを記録する。
- normal test suiteへ不安定な短時間thresholdを入れない。timing-sensitive benchmarkは明示commandで実行し、deterministic invariantは通常testで固定する。
- .NET 10 performance logはroute ID／generation IDを持ち、input accepted、owner start、terminal apply、実在するdispatcher queueのUI apply、first visibleを同じinteractionとして追跡する。background terminal stageをUI queueとは記録しない。per-row／per-file logを追加しない。

## Concurrency の非交渉条件

- model lock、DB transaction、reservation、operation gateを保持したまま、別threadのUI executionを同期的に待たない。
- `PropertyChanged`、event、dialog、UI scheduler、View callback、別owner callbackをowner lock内から同期実行しない。
- background producerはversion／immutable change factをqueueして戻り、UI反映はcoalesced asynchronous drainで行う。
- timeout、retry、`TryEnter`、notification skip、追加`Task.Run`、lock recursion変更でdeadlockを隠さない。
- 性能改善のために、解消済みの非同期queueを同期waitへ戻したり、UI thread上でmodel lockを再取得する設計へ戻したりしない。

## Verification

- performance unitは、behavior test、deadlock regression、synthetic corpusが成立する場合のbefore／after component evidence、fresh static reviewを同じunitで閉じる。
- corpusが`MANUAL_REAL_DATA`の場合は、instrumentation contract、低負荷性、log schema、manual operation scriptを検証してunitを閉じる。実データ結果を待たない。
- Full Gateでは全5 project、selected main-app／updater publish、existing-data、update success／rollback、synthetic performance suite、instrumentation schemaを確認する。
- `.NET Desktop Runtime`未導入machine／VM、実データ性能確認、署名、公開release、proprietary license証跡はpost-engineeringのユーザー作業である。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分へ触れない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
