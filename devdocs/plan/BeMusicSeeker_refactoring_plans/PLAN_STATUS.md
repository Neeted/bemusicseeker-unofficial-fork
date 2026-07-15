# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-16

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-04 Play history ownership`

状態: ready

目的:

play history の source query / cache / cancellation、filter、result generation、selected row の feature-local action interpretation を play history owner に集約する。main table は owner が生成した immutable result の terminal apply と selection state の接続だけを担当し、root と code-behind を composition、event forwarding、view-host 操作に限定する。

Acceptance criteria:

- source query / cache / cancellation / freshness と filter / result generation が play history owner の production 経路を通る。
- selected row の action interpretation と失敗処理が feature owner に収まり、stale result を main table へ適用しない。
- main table presentation owner は play history result の terminal apply と selection state の接続だけを担当し、play history workflow を持たない。
- root ViewModel は play history workflow を直接実装せず、明示的な composition と feature 間の最小 coordination だけを持つ。
- code-behind に残る play history 処理は event forwarding、focus / selection などの view-host 操作に限定され、旧 root relay / callback host / test-only production seam が削除される。
- feature owner は generated Settings、`Application.Current`、`DispatcherHelper.UIDispatcher`、`System.Windows.Threading.Dispatcher`、`UiDialogCoordinator` / `MessageBox` 型、`NLogWrapper`、root の nested contract を直接取得しない。必要な境界は用途別の細い port として明示する。
- query、filter、selection、cache 再利用、cancellation / freshness、失敗時の既存契約を behavior test で検証する。
- DB schema / data、setting key / serialized value、UI observable behavior、失敗契約を維持する。
- 変更範囲に対応する UI smoke check、Full 検証、重大な指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- main table presentation、regular chart、playback、playlist workspace の再編成（UI-01、UI-02、UI-03）。
- library scan / package / maintenance、settings dialog、残る shell workflow の owner 移管（LIB-*、UI-05）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。
- public modifier の変更だけを理由にした完了 outcome の再開、または過去の repository-internal call shape の復元。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Application composition and settings lifecycle boundary | completed |
| UI-02 Playback ownership | completed |
| UI-03 Playlist workspace ownership | completed |
| UI-04 Play history ownership | ready |
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

UI-01 は public surface の変更だけを理由に再開しない。旧 sort / filter compatibility surface の整理は総合計画の bounded cleanup 方針に従い、production / XAML consumer、tests、旧 wrapper を一つの unit で更新する。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner と UI-02 の playback owner は completed。playlist、play-history、設定画面と残る code-behind workflow は UI-03 / UI-04 / UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | facade private state / host bridge が残る |
| Playlist ownership | not met | persistence / sync / output が同居する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | in progress | code baseline と UI-03 outcome-wide verification / review / UI smoke は完了。次の UI-04 は ready で、outcome-wide 検証は未開始 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
