# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-10

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-01 Main table presentation and regular chart ownership`

状態: in progress

目的:

main table の presentation state / terminal apply と regular chart mode の workflow ownership を閉じ、root ViewModel と code-behind を shell / UI adapter に近づける。playlist workspace と play history は各 feature workflow owner を維持し、UI-01 では immutable presentation result を table owner へ渡す production contract までを扱う。

Acceptance criteria:

- main table presentation owner、regular chart workflow owner、playlist / play-history workflow owner、shell / composition の境界が実コードで説明できる。
- `MainWindow.xaml` の main chart table subtree が `MainChartList` の state / command を直接 bind する。
- `ChartRowsView`、selected index、column settings、row drag kind、summary などの root chart-list state / method / binding relay と PropertyChanged 再中継が削除される。
- regular chart の refresh request、query / filter / sort、cache / cancellation / freshness、presentation result 生成が regular workflow owner を通る。
- playlist detail / summary と play history は各 feature owner が result を生成し、main table presentation owner が atomic terminal apply、selection、dispose、通知を行う。MainChartList が各 feature の query / cache / cancellation を再所有しない。
- chart-list 用 root callback host / private workflow / terminal transition partial が削除されるか、shell に残せる coordination だけであることが説明できる。
- `MainWindow.cs` は CustomTable / WPF event data を request / command へ変換する薄い adapter に限定される。
- existing behavior、selection / disposal timing、virtual view generation、cancellation / freshness、settings persistence を維持する behavior test がある。
- 移管済み child owner に `Application.Current`、`DispatcherHelper.UIDispatcher`、`MainWindowViewModel` nested contract、`*ForTest` production method、root relay が残っていない。
- root から責務が減り、単に state holder、DTO、host interface を追加しただけではない。
- build / test / format / analyzer / diff check と、重大指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- CustomTable 全体の置換。
- persisted column setting の schema / key / value 変更。
- play history、playlist workspace 全体の feature migration。UI-01 はそれらの workflow を MainChartList へ移さず、main table へ渡す result contract と terminal apply までを扱う。
- `.NET 10` TFM 変更。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | in progress |
| APP-01 Composition and configuration ownership | not started |
| UI-02 Playback ownership | not started |
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

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | root relay、terminal transition / workflow host、code-behind workflow が残る |
| Library ownership | not met | facade private state / host bridge が残る |
| Playlist ownership | not met | persistence / sync / output が同居する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | baseline met | code baseline 時点の build / test / format / analyzer は完了。各新規差分で再検証する |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
