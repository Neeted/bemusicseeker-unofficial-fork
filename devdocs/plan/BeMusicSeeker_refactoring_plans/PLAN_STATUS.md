# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-10

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-01 Main chart list ownership`

状態: in progress

目的:

main chart list の request、route、source / query、filter / sort、cache / cancellation、view generation、atomic terminal apply、selection、summary / metrics を一つの feature owner に移し、root ViewModel と code-behind を shell / UI adapter に近づける。

Acceptance criteria:

- regular chart、playlist detail、playlist summary、play history など、main table に表示される各 mode の ownership と切替境界が説明できる。
- `MainWindow.xaml` の main chart table subtree が `MainChartList` の state / command を直接 bind する。
- `ChartRowsView`、selected index、column settings、row drag kind、summary などの root chart-list state / method / binding relay と PropertyChanged 再中継が削除される。
- refresh request から terminal apply までの通常経路が feature owner を通り、chart-list 用 root callback host / private workflow が残っていない。
- `MainWindow.cs` は CustomTable / WPF event data を request / command へ変換する薄い adapter に限定される。
- existing behavior、selection / disposal timing、virtual view generation、cancellation / freshness、settings persistence を維持する behavior test がある。
- root から責務が減り、単に state holder、DTO、host interface を追加しただけではない。
- build / test / format / analyzer / diff check と、重大指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- CustomTable 全体の置換。
- persisted column setting の schema / key / value 変更。
- play history、playlist workspace 全体の feature migration。UI-01 では main table へ渡す contract までを扱う。
- `.NET 10` TFM 変更。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main chart list ownership | in progress |
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
| UI ownership | not met | `MainWindowViewModel.cs` 23,413 行、`MainWindow.cs` 10,327 行、code-behind `async void` 約74件 |
| Library ownership | not met | top-level `BMSLibrary*.cs` family 20,157 行、facade private state / host bridge が残る |
| Playlist ownership | not met | top-level `BMSPlaylist*.cs` family 10,273 行、persistence / sync / output が同居 |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | baseline met | code baseline 時点の build / test / format / analyzer は完了。各新規差分で再検証する |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。

## Next action

refresh / terminal apply の state transition を `MainChartList` owner へ移し、root の `SetChartRowsView` / summary wrapper と `ChartListRefreshCoordinator.Apply*` callback host を削除する。docs-only checkpoint は作らない。
