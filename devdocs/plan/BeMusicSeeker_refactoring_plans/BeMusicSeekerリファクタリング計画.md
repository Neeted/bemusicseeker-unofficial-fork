# BeMusicSeeker リファクタリング計画

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

## 目的

目的は計画項目や行数目標の達成ではなく、WPFアプリをMVVMとして整理し、owner、dependency direction、testability、応答性、.NET 10移行可能性を改善することである。

## 現在の判定

- shell、library、playlist、package、maintenance、LR2、configuration、platform boundaryのowner整理は完了している。
- WPF固有のfocus、selection、scroll、hit-test、virtualization、typed presentationのterminal applyはView／view-hostに残す。
- broad facade port、non-event `async void`、元のestimated-install deadlockは解消済みである。
- 構造整理を全面的に再開せず、現在は[PERF-01](./BeMusicSeeker_性能回帰改善計画.md)でcurrent .NET 10の不要なcopy、queue、allocation、index rebuildを閉じる。

## 維持する境界

- stateとbehaviorを同じownerに置く。
- facadeのprivate state、lock、mutable collection、private operationを列挙するhost／adapterを追加しない。
- UIはimmutable request／snapshot／receiptを受け、terminalなWPF applyだけを行う。
- model lock内からUI、dialog、event、別owner callbackを同期実行しない。
- performance改善のためにfeature workflowをroot shellへ戻したり、global service locatorや共有mutable cacheを導入したりしない。
- production data不在を理由にowner境界を崩したtest seamを追加しない。

## Completion Gate

strict Refactoring Completion Gateは構造／機能面で通過済みである。release engineeringでは次を追加確認する。

1. current .NET 10のcritical routeに既知の不要な全件copy／重複apply／unbounded drainがない。
2. synthetic corpusを構築できるcomponentで変更前よりelapsedまたはallocationが改善する。
3. synthetic化できない実データ性能は低負荷instrumentationと`MANUAL-02`へhandoffされる。
4. deadlock regression、data compatibility、update／rollbackを維持する。

net472との厳密な再比較はCompletion Gateに含めない。
