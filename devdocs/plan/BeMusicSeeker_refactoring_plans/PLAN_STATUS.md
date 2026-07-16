# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-16

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `LIB-01 Initialization and scan ownership`

状態: ready

目的:

startup scan、file diff、parse、commit、post-scan maintenance の state / behavior を明示的な pipeline owner に集約し、application-facing facade を request / lifecycle bridge に限定する。

Acceptance criteria:

- startup scan、file diff、parse、commit、post-scan maintenance の production 通常経路が、明示的な pipeline owner を通る。
- scan の request identity、cancellation / freshness、失敗処理、完了通知が同じ owner に収まり、root は composition と最小限の lifecycle coordination に限定される。
- application-facing facade は必要な request / lifecycle bridge だけを持ち、private workflow、callback host、test-only production seam を保持しない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- startup、差分検出、parse、commit、post-scan maintenance の behavior test と、変更範囲に対応する UI smoke check、Full 検証、重大な指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- package / file-operation ownership、maintenance / resource-health、LR2 sync / playlist-reference ownership（LIB-02、LIB-03、LIB-04）。
- playlist persistence / external-sync / output、shell closure（PL-01、PL-02、UI-05）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。
- public modifier の変更だけを理由にした完了 outcome の再開、または過去の repository-internal call shape の復元。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Application composition and settings lifecycle boundary | completed |
| UI-02 Playback ownership | completed |
| UI-03 Playlist workspace ownership | completed |
| UI-04 Play history ownership | completed |
| LIB-01 Initialization and scan ownership | ready |
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
| UI ownership | not met | UI-01 の main table / regular owner、UI-02 の playback owner、UI-03 の playlist workspace owner、UI-04 の play-history owner は completed。設定画面と残る code-behind workflow は UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | facade private state / host bridge が残る |
| Playlist ownership | not met | persistence / sync / output が同居する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | in progress | code baseline と UI-04 outcome-wide Full verification / review / UI smoke は完了。LIB-01 は ready で、次の outcome-wide 検証は未開始 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
