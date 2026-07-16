# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-16

## Baseline

- code baseline commit: `44fd2fde`
- library ownership bridge baseline: `b6f2ef7e`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `LIB-03 Catalog storage, mutation, maintenance and resource-health ownership`

状態: in progress

- active outcome base commit: `d040b210`
- last outcome-wide verified commit: `d040b210`

目的:

session BMS / bmson catalog rows、owned chart collection、catalog 固有の index / digest / version と、runtime catalog mutation の canonical writer、write serialization、hydration、post-scan maintenance / resource-health を bounded owner family に集約する。scan、package、file operation、LR2 は catalog state を直接変更せず、immutable な request / snapshot / receipt / lease / event facts で接続する。

Acceptance criteria:

- catalog rows、owned collection、digest / owned-hash、parent-folder / duplicate / resource / directory index、version、mutation ordering / failure atomicity、hydration、maintenance / resource-health の state と behavior に新 owner がある。
- canonical writer と write serialization は一つの production owner 境界にあり、scan の通常 mutation route がその writer を使う。package、file operation、LR2 は catalog state の直接書き込みを行わず、catalog mutation request / receipt / lease を使う。
- immutable な catalog snapshot、catalog mutation request / receipt、catalog-changed notification は consumer 固有の install-destination、LR2 freshness、playlist cache / status を含めず、各 consumer owner が自分の state を更新できる事実だけを持つ。
- production の旧 writer、旧 storage / mutation / maintenance host、不要な forwarding / callback path を、担当する vertical unit で削除する。baseline residual bridge は `b6f2ef7e` から追加・拡張せず、`LIB-03` が担当する category を減らす場合だけ一時的に残す。
- behavior test で mutation ordering、version / receipt、failure atomicity、hydration、maintenance / resource-health の observable behavior を検証し、private class / method 配置を test contract にしない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- outcome-wide Full verification、変更範囲に対応する startup / catalog mutation / progress / failure の UI smoke check、重大な指摘なしの outcome review が完了する。

Non-goals:

- package / install-destination / library file-operation ownership（LIB-02）。
- LR2 request / run / status、LR2 run reservation、scan-surface、trust / freshness、folder sync ownership（LIB-04）。
- playlist-reference maps / index / display ownership（LIB-05）。
- residual scan host、nested host / factory、facade private workflow の最終削除（LIB-06）。
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
| LIB-03 Catalog storage, mutation, maintenance and resource-health ownership | in progress |
| LIB-02 Package, install-destination and file-operation ownership | not started |
| LIB-04 LR2 synchronization ownership | not started |
| LIB-05 Playlist-reference ownership | not started |
| LIB-06 Library facade and scan integration closure | not started |
| PL-01 Playlist persistence and reload ownership | not started |
| PL-02 Playlist external-sync and output ownership | not started |
| UI-05 Shell closure | not started |
| MIG-01 Platform boundary closure | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は `not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。`completed` は個別 outcome の全 acceptance criteria を満たした場合だけ、`gate met` は `GATE-01` にだけ使う。internal owner dependency は `blocked` にせず、ordered backlog の prerequisite outcome で解消する。

UI-01 の完了境界は main table presentation、regular chart workflow、playlist / play-history の result-to-table production contract、および main-table subtree の binding / event adapter とする。playlist source query / cache / queue、play history の feature action、playback、playlist summary、tree / dialog workflow、`MainWindow.cs` 全体の thin-shell 化は、対応する後続 outcome が active になった段階で閉じる。UI-01 はこれらを `MainChartList` に吸収しない。

UI-01 は public surface の変更だけを理由に再開しない。旧 sort / filter compatibility surface の整理は総合計画の bounded cleanup 方針に従い、production / XAML consumer、tests、旧 wrapper を一つの unit で更新する。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner、UI-02 の playback owner、UI-03 の playlist workspace owner、UI-04 の play-history owner は completed。設定画面と残る code-behind workflow は UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | scan core の production route は pipeline owner へ移行済み。catalog mutation / maintenance、package、LR2、playlist reference の production owner と、facade private state / host bridge の削除は LIB-03、LIB-02、LIB-04〜LIB-06 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で扱う |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | library owner 境界と依存順は定義済みだが、canonical catalog writer / mutation serialization と各 consumer owner への移管は未実装 |
| Quality | in progress | code baseline と UI-04 outcome-wide Full verification / review / UI smoke、LIB-01 の再編後 criteria に対する outcome-wide Full verification / review / UI smoke は完了。LIB-03 は outcome-wide 検証待ち |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。次の作業は `LIB-03` の canonical catalog writer / mutation serialization を成立させる vertical unit であり、未成立の downstream owner を埋める adapter 実装ではない。
