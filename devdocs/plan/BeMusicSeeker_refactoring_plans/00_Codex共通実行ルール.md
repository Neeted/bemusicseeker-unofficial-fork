# Codex共通実行ルール

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [register](./PERFORMANCE_WORK_REGISTER.md)

## 1. 正本と進行

1. `PLAN_STATUS.md`のactive batchを確認する。
2. active／pending unitがあればplannerを呼ばず、記載順に実装する。
3. unitごとにproduction route、behavior／concurrency test、旧edge削除、verification、fresh review、commitを閉じる。
4. batch状態は同じcode／test commitで更新する。
5. active outcomeが未完ならunit commitでユーザー応答せず次へ進む。

planner／reviewerはsingle-flightとし、実行中rootはrepository操作を凍結する。同scopeの独立再調査、第2planner、consensus取得を行わない。

## 2. Performance-first decision

実装判断は次の順で行う。

1. safety／data integrity／deadlock freedom
2. user-visible readiness／latency
3. owner／dependency direction
4. maintainability
5. artifact compactness／差分量

sourceとlogから不要workが明確な場合、PC再起動benchmarkやnet472比較を待たず修正する。marker追加だけで既知delayを完了扱いにしない。

禁止:

- priority変更、ThreadPool min-thread増加、timeout、retry、追加`Task.Run`だけの修正
- optional workをtimer外へ移しただけで完了扱いすること
- deadlock解消済みasync boundaryをsync waitへ戻すこと
- custom loader、managed DLL relocation、service locator、broad callback host
- net472側の再instrument／再build／反復A/B

## 3. Startup milestone

### `startup_ready_ui`

初期画面のrequired data、binding、main presentationが適用された時点。

### `startup_ready_operable`

通常入力を受け付ける時点。次を待ってはならない。

- library folder treeの遅延refresh
- Dispatcher Background／idle work
- prewarm
- network
- physical consistency audit
- export
- diagnostic flush

### `startup_initialization_complete`

通常利用に必要なlocal hydrationが完了した時点。required taskの一覧をcodeとtestで固定する。

### `startup_post_initialization_maintenance_complete`

network、custom-folder physical audit、export、best-effort prewarm等の完了。UIは必要ならbackground activityを表示するが、通常操作をblockしない。

## 4. Startup implementation rules

- readiness-critical workはownerを持ち、generic `Task.Run`とlow-priority Dispatcher chainへ隠さない。
- request、worker start、model-read wait、snapshot、UI queue、UI start、apply、rescheduleを同じinteraction IDで記録する。
- logはper-row／per-fileにせず、diagnostic無効時に不要な文字列／objectを作らない。
- optional presentationが遅延してもrequired schedulerとoperabilityが進む。
- Background priorityを使う場合、そのoperationはglobal readinessをgateしない。
- ThreadPool tuningはwait graph修正の代替にしない。global `SetMinThreads`を残すならcurrent evidenceとretirement conditionを持つ。
- post-initialize GCはrequired completionと競合させず、memory threshold／idle／explicit ownerで実行する。

## 5. Concurrency rules

- lock、transaction、reservation、operation gate保持中にUI、dialog、event、別owner callbackを同期実行しない。
- background producerはimmutable fact／versionをqueueして戻る。
- UI applyはcoalesceし、stale generationをexpensive work前に棄却する。
- shutdownでqueued workを追跡し、観測不能なfire-and-forgetを作らない。

## 6. Distribution rules

- code-level startup waitを閉じる前にsingle-fileを原因扱いしない。
- `bundle-r2r`と`folder-r2r`は同じcode／R2R設定で比較する。
- folder profileが選ばれた場合、standard host managed／runtime filesをexe隣接に置く。見た目のための独自`libs` relocationを行わない。
- manual cold comparisonはEngineering Gate後の一回確認であり、active outcomeを停止させない。

## 7. Verification

各unit:

- behavior／data compatibility
- readiness dependency
- blocked optional work
- rapid reentry／shutdown
- deadlock invariant
- source-level wait／copy／queue reduction

Final Gate:

- 全5 project locked restore／Release build
- full tests／analyzer
- selected main app／updater publish
- existing-data
- update success／rollback
- estimated-install／scan／playlist／library smoke
- startup structural tests
- frozen snapshot fresh review

## 8. Git・release

rootだけがwriter／committerとなる。unrelated差分へ触れない。`git push`、tag、署名、public release、version変更を行わない。
