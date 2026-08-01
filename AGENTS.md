# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次だけを読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. `devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md`
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/PERFORMANCE_WORK_REGISTER.md`
4. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`

完了済みOutcome、unit、commit、review履歴はGit historyへ委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。
- batch状態は対応するcode／test unitと同じcommitで更新し、status-only progress commitを作らない。
- unit commitは内部checkpointである。active outcomeが未完ならユーザー応答で停止せず次unitへ進む。
- `NO_SAFE_UNIT`は無効。PC再起動後の実測、production data、net472再計測の不在を停止理由にしない。

## Startup readiness contract

- `startup_ready_ui`は初期画面の必要なdata／bindingが適用された時点を表す。
- `startup_ready_operable`は通常入力を受け付けられる時点を表し、非表示または遅延可能なtree、prewarm、audit、network、exportを待ってはならない。
- `startup_initialization_complete`は通常利用に必要なlocal hydrationの完了を表す。network、physical audit、export、best-effort prewarmは別のpost-initialization milestoneへ分離する。
- `DispatcherPriority.Background`、idle priority、generic `Task.Run`、任意のView refreshをglobal operabilityの必須edgeにしない。
- startup background schedulerは、optional presentation完了ではなくrequired readinessに基づいて開始する。
- readiness-critical workはrequest、worker start、model-read wait、snapshot、UI queue、UI start、applyを一つのcorrelation IDで記録する。

## 性能優先方針

- 目的は現在の.NET 10アプリを高速化することであり、差分や旧構造の維持を最小化することではない。
- sourceとlogから不要なwait、queue、copy、fan-out、rebuildが明確なら、実機benchmarkを待たず修正する。
- priority変更、ThreadPool min-thread増加、timeout、追加`Task.Run`だけで待ちを隠さない。wait graphとreadiness dependencyを直す。
- C# 14／.NET 10 APIは、意味を守りつつallocation、enumeration、copy、lookupを明確に減らすhot pathで採用できる。
- 根拠のないparallelism、pooling、unsafe化、無制限cache、custom loaderは導入しない。
- 画面遷移で成立したstable source、atomic presentation、data-only invalidation、non-blocking refresh producerを維持する。

## Concurrency の非交渉条件

- model lock、DB transaction、reservation、operation gateを保持したまま別threadのUI完了を同期waitしない。
- `PropertyChanged`、event、dialog、UI scheduler、View callback、別owner callbackをowner lock内から同期実行しない。
- background producerはversion／immutable factをqueueして戻り、UI反映はcoalesced asynchronous drainで行う。
- timeout、retry、`TryEnter`、notification skip、lock recursion変更でdeadlockを隠さない。

## Distribution

- 現行`bundle-r2r`はnative self-extract、all-content extraction、single-file compressionを使用しない。
- cold-start問題をbundle起因と断定せず、まずmanaged startup stage内の待ちを閉じる。
- `folder-r2r`は標準host layoutの性能優先fallbackとして維持する。managed DLLを独自`libs`へ移すloader／probing／deps書換えは作らない。
- 最終PC再起動後比較は全engineering作業後のユーザー手動受入れとし、Codexのactive outcomeを止めない。

## Verification

- unitごとにbehavior、concurrency invariant、readiness dependency、source-level work reductionを確認する。
- blocked／starvedなoptional folder refreshでも`startup_ready_operable`とrequired scheduler startが進む決定的testを持つ。
- Full Gateでは全5 project、full tests、analyzer、selected publish、existing-data、update success／rollback、deadlock regression、startup structural auditを確認する。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなり、unrelated差分へ触れない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
