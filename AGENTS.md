# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次だけを読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. `devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md`
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/PERFORMANCE_WORK_REGISTER.md`
4. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`

過去のOutcome、unit、commit、review記録はGit historyへ委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。
- active batchの状態更新は対応するcode／test unitと同じcommitへ含め、status-only progress commitを作らない。
- unit commitは内部checkpointである。active outcomeが未完ならユーザー応答で停止せず次unitへ進む。
- `NO_SAFE_UNIT`は無効。production data、net472再計測、実機benchmarkの不在を停止理由にしてはならない。

## 性能優先方針

- 目的は現在の.NET 10アプリを高速化することであり、差分、class数、adapter数、旧構造の維持を最小化することではない。
- 既存logとsourceから、不要なUI-thread work、広すぎる通知fan-out、同期file log、全件copy、重複invalidation、不要なpresentation hopが高い確度で確認できる場合、実機benchmarkを待たずに修正する。
- synthetic testを作れないこと、作業中に絶対時間を測れないこと、net472との厳密な比較材料がないことは、妥当な高速化を放置する理由にならない。
- instrumentation-only、manual handoff、コメント追記だけでは、既知のユーザー体感遅延を完了扱いにしない。
- C# 14／.NET 10 APIは、意味が明確でallocation、enumeration、copy、lookupを減らす場合、behavior testで意味を守れるならcomponent benchmarkがなくても採用できる。
- ただし、根拠のないparallelism、pooling、unsafe化、priority変更、cache追加は行わない。
- `PropertyChanged`をdomain／catalog event busとして使わない。subscriberは必要なtyped eventだけを購読する。
- UI threadでdiagnostic file I/O、全件materialization、無関係なsettings更新、hidden control向けの再構築を行わない。
- 同じ表示内容へ戻る場合は、安定したsource／presentation identity、version、cacheを再利用する。

## Concurrency の非交渉条件

- model lock、DB transaction、reservation、operation gateを保持したまま、別threadのUI executionを同期的に待たない。
- `PropertyChanged`、event、dialog、UI scheduler、View callback、別owner callbackをowner lock内から同期実行しない。
- background producerはversion／immutable change factをqueueして戻り、UI反映はcoalesced asynchronous drainで行う。
- timeout、retry、`TryEnter`、notification skip、追加`Task.Run`、lock recursion変更でdeadlockを隠さない。
- 性能改善のために、解消済みの非同期queueを同期waitへ戻したり、UI thread上でmodel lockを再取得したりしない。
- file／DB／playlist／selection／sortのobservable behaviorとexisting-data互換性を維持する。

## Verification

- unitごとに、変更したrouteのbehavior test、concurrency invariant、source-level work reductionを確認する。
- synthetic corpusが既に安全に存在する場合は利用する。新しい巨大なbenchmark architectureを作ることをunitの前提にしない。
- 実機性能測定は全engineering作業後にユーザーが一度行う。Codexのactive outcome、planner停止条件、`EXTERNAL_BLOCKER`にしない。
- Full Gateでは全5 project、full tests、analyzer、selected main-app／updater publish、existing-data、update success／rollback、deadlock regression、hot-path structural auditを確認する。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分へ触れない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
