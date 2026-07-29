# BeMusicSeeker 応答性・並行処理ハードニング計画

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

## 完了済みの安全性境界

元のestimated-install hangは、workerがcatalog writerを保持したままUI completionを待ち、UIが同catalog readerを要求する循環待機だった。

現在は次へ変更済みである。

- normal-library refreshはlatest versionをsingle pending UI drainへqueueする。
- producerはUI terminal applyを待たず戻る。
- UI drainは連続versionをcoalesceする。
- estimated-installのUI／dialog／notificationはmodel guard解放後にpublishする。
- app-wide wait inventoryで同型のactive P0／P1を閉じた。
- held writerとdedicated UI laneを用いたdeterministic regressionを維持する。

## Performance follow-up

[PERF-01](./BeMusicSeeker_性能回帰改善計画.md)は安全化を巻き戻さず、不要なcopy、重複apply、queue backlog、one-turn workを減らす。

- queue／generation／drainはfake scheduler／STA harnessでproduction dataなしに検証する。
- actual UI latencyはcurrent .NET 10 markerを用意し、全engineering完了後の`MANUAL-02`へ渡す。
- synchronous UI wait、callback-under-lock、notification skip、priority変更だけの修正へ戻さない。
- pre-release incident専用のjournal、recovery coordinator、persistent operation stateを追加しない。
- filesystem／song DB差分は既存startup／manual file diff contractで収束させる。

## Reopen condition

次のいずれかが見つかった場合だけconcurrency Outcomeを再開する。

- 再現可能なwait cycle。
- model guard中のUI／dialog／別owner callback。
- UI drain failureが観測不能、またはshutdown後apply。
- performance修正がdeterministic deadlock regressionを壊した。

単に`Wait`、`GetResult`、raw lock countが存在することや、実データ計測が未実施であることだけでは再開しない。
