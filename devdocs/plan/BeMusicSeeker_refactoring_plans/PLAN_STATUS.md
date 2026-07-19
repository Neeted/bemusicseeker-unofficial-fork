# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-18

## Baseline

- code baseline commit: `44fd2fde`
- library ownership bridge baseline: `b6f2ef7e`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `PL-01 Playlist persistence and reload ownership`

状態: in progress

active outcome base commit: `eef386ce`
active execution package: `PL-01 Playlist persistence and reload ownership`
sequence cursor: `PL01-U4 External snapshot diff, reload decision, durable/live apply`

目的:

playlist の DB transaction、hydration、diff、reload decision、catalog-change による playlist aggregate / summary invalidation を repository / workflow owner へ移し、application-facing facade から persistence lifecycle を分離する。

Acceptance criteria:

- playlist persistence の durable transaction、hydration、diff、reload decision、aggregate / summary invalidation の state と behavior が新 owner に集約され、production route がその owner を直接使う。
- `BMSPlaylist` と application-facing facade から persistence writer、hydration queue、reload decision、summary cache の private forwarding / callback host を削除する。
- `LIB-03` の versioned owned-hash snapshot / catalog mutation receipt を immutable input として受け、DB schema / data、failure、cancellation、notification、UI observable behavior を維持する。
- repository / workflow owner の behavior test、outcome-wide Full verification、必要な UI smoke、重大指摘なしの outcome review を完了する。

Non-goals:

- playlist external sync、custom-folder / BMT / recommended-table output（PL-02）。
- shell の残存 root pass-through / code-behind workflow（UI-05）。
- feature 内に残る `Settings.Default` の除去（各 feature outcome と `MIG-01`）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。

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
| LIB-04 LR2 synchronization ownership | completed |
| LIB-05 Playlist-reference ownership | completed |
| LIB-06 Library facade and scan integration closure | completed |
| PL-01 Playlist persistence and reload ownership | in progress |
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
| Library ownership | not met | scan core、catalog rows / digest / owned collection、resource-health、catalog-owned maintenance / maintenance-table hydration、chart-info hydration / backfill、generic mutation の relocation / removal protocol、canonical receipt、broad host retirement、package / install-destination / file-operation owner、LR2 synchronization owner、playlist-reference owner、LIB-06 facade / scan integration closure は completed。playlist persistence / shell closure は PL-01、PL-02、UI-05 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で completed |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | resource-health / maintenance / chart-info owner と generic mutation の prepare / durable commit / live apply / immutable receipt publish protocol、broad host retirement、package / install-destination / file-operation owner、LR2 synchronization owner、playlist-reference owner、LIB-06 facade / scan integration closure は成立済み。残る playlist / shell consumer owner の課題は PL-01、PL-02、UI-05 で扱う |
| Quality | in progress | LIB-06 の outcome-wide Full verification、static review、変更範囲の x64 Release process-path startup / normal-shutdown smoke は完了。既知の無関係な既存テスト失敗と、残る outcomes / Gate evidence は未完了 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。PL-01 の persistence / reload ownership を次の implementation unit で扱う。具体的な `EXTERNAL_BLOCKER` 以外はユーザー確認待ちにしない。
