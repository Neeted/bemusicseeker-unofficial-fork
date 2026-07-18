# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-18

## Baseline

- code baseline commit: `44fd2fde`
- library ownership bridge baseline: `b6f2ef7e`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `LIB-06 Library facade and scan integration closure`

状態: in progress

active outcome base commit: 2a75ca5c21ca8885a0b9228756f7a115625f9203

active execution package: LIB-06 Library facade and scan integration closure

sequence cursor: LIB-06-U8 Scan inline chart-info warning event ownership and callback-port retirement

目的:

成立済みの catalog、package、LR2、playlist-reference owner を application-facing facade / aggregate entry へ直接 compose し、残る scan composition、nested host / factory、facade forwarding、test-only production seam を削除して Library Gate を閉じる。

Acceptance criteria:

- established owner の command / query / event を production composition から直接接続し、facade に workflow private state、state mirror、broad callback host を残さない。
- residual scan progress / dialog / diagnostics / DB gateway port、storage / mutation factory、nested host / forwarding、test-only production seam を用途別の narrow boundary へ整理し、同じ vertical unit で旧 route を削除する。
- scan / catalog / package / LR2 / playlist-reference の既存 owner boundary、ordering、failure、cancellation、notification、UI observable behavior を behavior test で維持する。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- outcome-wide Full verification、変更範囲に対応する Library composition / scan の UI smoke check、重大な指摘なしの outcome review を完了する。

Non-goals:

- catalog storage / mutation / maintenance / resource-health ownership（LIB-03）。
- package / install-destination、installable maintenance、generic file operation（LIB-02）。
- LR2 request / run / status、trust / freshness、normal-folder sync（LIB-04）。
- playlist-reference index / apply ownership（LIB-05）。
- playlist persistence / external-sync / output、shell closure（PL-01、PL-02、UI-05）。
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
| LIB-04 LR2 synchronization ownership | completed |
| LIB-05 Playlist-reference ownership | completed |
| LIB-06 Library facade and scan integration closure | in progress |
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
| Library ownership | not met | scan core、catalog rows / digest / owned collection、resource-health、catalog-owned maintenance / maintenance-table hydration、chart-info hydration / backfill、generic mutation の relocation / removal protocol、canonical receipt、broad host retirement、package / install-destination / file-operation owner、LR2 synchronization owner、playlist-reference owner は completed。残る facade bridge の削除は LIB-06 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で completed |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | resource-health / maintenance / chart-info owner と generic mutation の prepare / durable commit / live apply / immutable receipt publish protocol、broad host retirement、package / install-destination / file-operation owner、LR2 synchronization owner、playlist-reference owner は成立済み。残る facade / playlist / shell consumer owner の課題は LIB-06、PL-01、PL-02、UI-05 で扱う |
| Quality | in progress | UI-04、LIB-01、LIB-03、LIB-02、LIB-04、LIB-05 の outcome-wide Full verification / review と変更範囲の exact process-path startup / normal-shutdown smoke は完了。残る outcomes と Gate evidence は未完了 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。LIB-06 の facade / scan integration 残課題は、次の implementation unit で扱う。具体的な `EXTERNAL_BLOCKER` 以外はユーザー確認待ちにしない。
