# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-18

## Baseline

- code baseline commit: `44fd2fde`
- library ownership bridge baseline: `b6f2ef7e`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `LIB-04 LR2 synchronization ownership`

状態: in progress

active outcome base commit: `e3b743d6892734ec06701b9a0cb4bc70c654f174`
active execution package: `LIB-04 LR2 synchronization ownership`
sequence cursor: `LIB-04-U1b scan-surface / trust / LR2-folder synchronization and residual callback retirement`

目的:

LR2 request / schedule / cancellation / run reservation / publish、status、scan-surface、trust / freshness、normal-folder / LR2-folder sync と app-managed output scope を一つの owner family に集約し、`LIB-03` の catalog snapshot / mutation lease を使う production route を成立させる。

Acceptance criteria:

- LR2 input build、request identity、schedule / cancel / run / publish、status、reservation、trust / freshness、folder sync の state と behavior に明示的な owner がある。
- LR2 の通常経路は `LIB-03` の catalog snapshot / mutation lease を使い、catalog の canonical state、package state、playlist reference を直接所有しない。
- scan commit facts と LR2 status / freshness snapshot の handoff、cancellation / failure / progress / notification の ordering と failure contract を behavior test で検証する。
- scan LR2 host、scan host の LR2 callback、LR2-specific mutation-block callback、LR2 request / status host と facade-owned mutable state を同じ vertical unit で削除する。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- outcome-wide Full verification、変更範囲に対応する LR2 / folder-sync progress / failure の UI smoke check、重大な指摘なしの outcome review が完了する。

Non-goals:

- catalog storage / mutation / maintenance / resource-health ownership（LIB-03）。
- package / install-destination、installable maintenance、generic file operation（LIB-02）。
- playlist reference、playlist persistence / external-sync / output、shell closure（LIB-05、PL-01、PL-02、UI-05）。
- residual scan composition と nested host / factory の最終削除（LIB-06）。
- feature 内に残る `Settings.Default` の除去（各 feature outcome と `MIG-01`）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。
- public modifier の変更だけを理由にした completed outcome の再開、または過去の repository-internal call shape の復元。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Application composition and settings lifecycle boundary | completed |
| UI-02 Playback ownership | completed |
| UI-03 Playlist workspace ownership | completed |
| UI-04 Play history ownership | completed |
| LIB-01 Scan pipeline core ownership | completed |
| LIB-03 Catalog storage, mutation, maintenance and resource-health ownership | completed |
| LIB-02 Package, install-destination and file-operation ownership | completed |
| LIB-04 LR2 synchronization ownership | in progress |
| LIB-05 Playlist-reference ownership | not started |
| LIB-06 Library facade and scan integration closure | not started |
| PL-01 Playlist persistence and reload ownership | not started |
| PL-02 Playlist external-sync and output ownership | not started |
| UI-05 Shell closure | not started |
| MIG-01 Platform boundary closure | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は `not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。`completed` は個別 outcome の全 acceptance criteria を満たした場合だけ、`gate met` は `GATE-01` にだけ使う。internal complexity は execution package 内で分解し、責務が別 outcome に属することが production evidence で確定した場合だけ ordered backlog の prerequisite outcome を再編する。

UI-01 の完了境界は main table presentation、regular chart workflow、playlist / play-history の result-to-table production contract、および main-table subtree の binding / event adapter とする。playlist source query / cache / queue、play history の feature action、playback、playlist summary、tree / dialog workflow、`MainWindow.cs` 全体の thin-shell 化は、対応する後続 outcome が active になった段階で閉じる。UI-01 はこれらを `MainChartList` に吸収しない。

UI-01 は public surface の変更だけを理由に再開しない。旧 sort / filter compatibility surface の整理は総合計画の bounded cleanup 方針に従い、production / XAML consumer、tests、旧 wrapper を一つの unit で更新する。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner、UI-02 の playback owner、UI-03 の playlist workspace owner、UI-04 の play-history owner は completed。設定画面と残る code-behind workflow は UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | scan core、catalog rows / digest / owned collection、resource-health、catalog-owned maintenance / maintenance-table hydration、chart-info hydration / backfill、generic mutation の relocation / removal protocol、canonical receipt、broad host retirement、package / install-destination / file-operation owner は completed。LR2、playlist reference、残る facade bridge の削除は LIB-04〜LIB-06 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で扱う |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | resource-health / maintenance / chart-info owner と generic mutation の prepare / durable commit / live apply / immutable receipt publish protocol、broad host retirement、package / install-destination / file-operation owner は成立済み。LR2 / playlist / residual consumer owner の残課題は LIB-04〜LIB-06 で扱う |
| Quality | in progress | UI-04、LIB-01、LIB-03、LIB-02 の outcome-wide Full verification / review と変更範囲の exact process-path startup / normal-shutdown smoke は完了。残る outcomes と Gate evidence は未完了 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。LIB-04 の LR2 synchronization 残課題は、次の implementation unit で扱う。具体的な `EXTERNAL_BLOCKER` 以外はユーザー確認待ちにしない。
