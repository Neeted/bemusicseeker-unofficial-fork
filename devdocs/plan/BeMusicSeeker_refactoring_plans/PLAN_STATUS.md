# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-12

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-02 Playback ownership`

状態: ready

目的:

playback の state、command、progress、player lifecycle の ownership を child View / ViewModel と player adapter に集約し、root と code-behind を composition と view-host 操作に限定する。既存の player 選択、再生・停止・一時停止、進捗表示、失敗契約、UI observable behavior は維持し、後続 outcome が playback の結果だけを利用できる境界を閉じる。

Acceptance criteria:

- playback state、command、progress、player lifecycle が child View / ViewModel と player adapter の production 経路を通る。
- root ViewModel は playback workflow を直接実装せず、明示的な composition を通じて child owner と adapter を構築する。
- code-behind に残る playback 処理は view-host 操作に限定され、旧 root callback / workflow host / binding relay と test-only production seam が削除される。
- 内部 player、外部 player、再生状態遷移、停止・一時停止、progress 更新、失敗時の既存契約を behavior test で検証する。
- 移管済み child owner から `Application.Current`、`DispatcherHelper.UIDispatcher`、root の nested contract を直接取得しない。
- 変更範囲に対応する UI smoke check、Full 検証、重大な指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- playlist workspace、play history、library、playlist persistence / sync / output の owner 移管（UI-03、UI-04、LIB-*、PL-*）。
- settings dialog、library refresh、package operation など playback 以外の `MainWindow.cs` workflow の thin-shell 化（UI-05）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Composition and configuration ownership | completed |
| UI-02 Playback ownership | ready |
| UI-03 Playlist workspace ownership | not started |
| UI-04 Play history ownership | not started |
| LIB-01 Initialization and scan ownership | not started |
| LIB-02 Package and file-operation ownership | not started |
| LIB-03 Maintenance and resource-health ownership | not started |
| LIB-04 LR2 sync and playlist-reference ownership | not started |
| PL-01 Playlist persistence and reload ownership | not started |
| PL-02 Playlist external-sync and output ownership | not started |
| UI-05 Shell closure | not started |
| MIG-01 Platform boundary closure | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は `not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。`completed` は個別 outcome の全 acceptance criteria を満たした場合だけ、`gate met` は `GATE-01` にだけ使う。途中段階を完了状態として記録しない。

UI-01 の完了境界は main table presentation、regular chart workflow、playlist / play-history の result-to-table production contract、および main-table subtree の binding / event adapter とする。playlist source query / cache / queue、play history の feature action、playback、playlist summary、tree / dialog workflow、`MainWindow.cs` 全体の thin-shell 化は、対応する後続 outcome が active になった段階で閉じる。UI-01 はこれらを `MainChartList` に吸収しない。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner は completed。playback、playlist、play-history、設定画面と code-behind workflow は後続 UI-02 / UI-03 / UI-04 / UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | facade private state / host bridge が残る |
| Playlist ownership | not met | persistence / sync / output が同居する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | baseline met | code baseline 時点の build / test / format / analyzer は完了。各新規差分で再検証する |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
