# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-13

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-03 Playlist workspace ownership`

状態: in progress

- active outcome base commit: `0ecd2c32`
- last outcome-wide verified commit: `0ecd2c32`

目的:

playlist tree、source query / cache / cancellation、detail / summary result generation、feature-local interaction、drag-drop、reload / edit workflow の ownership を playlist workspace owner に集約する。main table は workspace result の terminal apply だけを担当し、root と code-behind を composition、event forwarding、view-host 操作に限定する。playlist persistence / external sync / output は後続 outcome の責務として混在させない。

Acceptance criteria:

- playlist tree、detail、summary の state と interaction が `PlaylistWorkspaceViewModel` と用途別 adapter / service の production 経路を通る。
- source query / cache / cancellation / freshness と detail / summary result generation を workspace owner が持ち、stale result を main table または summary view へ適用しない。
- main table presentation ownerは workspace から受け取った immutable result の terminal apply だけを担当し、playlist query / filter / sort / reload / edit workflow を持たない。
- root ViewModel は workspace workflow を直接実装せず、明示的な composition と feature 間の最小 coordination だけを持つ。
- `PlaylistWorkspaceViewModel` に global-backed convenience constructor、optional provider fallback、暗黙の current-settings providerを production seam として残さない。
- `IPlaylistPropertySaveInteraction` のような広い root callback host を残さず、workspace ownership または用途別の細い shell / domain port へ整理する。
- code-behind に残る playlist 処理は event forwarding、drag visual、focus / selection などの view-host 操作に限定され、旧 root callback / workflow host / binding relay と test-only production seam が削除される。
- `PlaylistFilterType` を canonical feature type へ統合し、旧 repository-internal call shape のための compatibility type / wrapper を残さない。
- generated Settings / `ISettingsEditSession`、`Application.Current`、`DispatcherHelper.UIDispatcher`、`System.Windows.Threading.Dispatcher`、`UiDialogCoordinator` / `MessageBox` 型、`NLogWrapper`、root の nested contract を feature ownerから直接取得しない。
- tree selection、detail / summary切替、filter / sort、cache再利用、cancellation / freshness、reload / edit、drag-drop と失敗時の既存契約を behavior test で検証する。
- DB schema / data、playlist file形式、setting key / serialized value、UI observable behavior、失敗契約を維持する。
- 変更範囲に対応する UI smoke check、Full 検証、重大な指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- playlist persistence / reload基盤そのもの、external sync / output の domain owner 移管（PL-01、PL-02）。workspace からこれらを呼ぶ interaction は UI-03 の対象。
- play history、library scan / package / maintenance、settings dialog、playback 以外の shell workflow の owner 移管（UI-04、LIB-*、UI-05）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。
- public modifier の変更だけを理由にした完了 outcome の再開、または過去の repository-internal call shape の復元。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Application composition and settings lifecycle boundary | completed |
| UI-02 Playback ownership | completed |
| UI-03 Playlist workspace ownership | in progress |
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
| Quality | in progress | code baseline は検証済み。UI-03 は `0ecd2c32` まで outcome-wide 検証済みで、現行 head の outcome-wide Full verification / review 待ち |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
