# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-17

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
- active execution package: `LIB-03-GM Generic catalog mutation closure`
- sequence cursor: `LIB-03-GM-A Catalog relocation corridor`
- cursor policy: unit commit は応答境界にせず、同じ commit で現在 cursor だけを次の未完 unit へ進めて実装を継続する

目的:

session BMS / bmson catalog rows、owned chart collection、catalog 固有の index / digest / version と、runtime catalog mutation の canonical writer、write serialization、maintenance-table hydration、chart-info hydration / backfill、post-scan maintenance / resource-health を bounded owner family に集約する。scan、package、file operation、LR2 は catalog state を直接変更せず、immutable な request / snapshot / receipt / lease / event facts で接続する。

Acceptance criteria:

- catalog rows、owned collection、digest / owned-hash、parent-folder / duplicate / resource / directory / chart-info index、version、mutation ordering / failure atomicity、maintenance-table hydration、chart-info hydration / backfill、catalog-owned maintenance / resource-health の state と behavior に新 owner がある。
- non-scan runtime catalog writer と write serialization は一つの production owner 境界にあり、scan の通常 route は durable commit 後の immutable committed-row / maintenance facts をその owner に渡す。package、file operation、LR2 は catalog state の直接書き込みを行わず、catalog mutation request / receipt / lease を使う。
- immutable な catalog snapshot、catalog mutation request / receipt、catalog-changed notification は consumer 固有の install-destination、LR2 freshness、playlist cache / status を含めず、各 consumer owner が自分の state を更新できる事実だけを持つ。
- production の旧 writer、旧 storage / mutation / maintenance host、不要な forwarding / callback path を、担当する vertical unit で削除する。baseline residual bridge は `b6f2ef7e` から追加・拡張せず、`LIB-03` が担当する category を減らす場合だけ一時的に残す。
- resource-health の index snapshot、input / index version、invalidation / suppression、full / delta / defer / failure behavior と全 production route を一つの owner 境界へ移し、`BMSLibrary` の resource-health private state と full-rebuild / mutation-dispatch / input-mutation host を削除する。
- catalog-owned maintenance / maintenance-table hydration の queue state、load、attach、stale-row detection、maintenance evaluation / durable write request、resource-health request、progress / failure を一つの owner family へ移し、hydration queue state / lock、apply / dispatch host と package-install / fix-install / pending estimated-install の facade maintenance forwarding を削除する。catalog mutation owner を non-scan runtime song / bmson / maintenance-table durable write / delete の command owner とし、maintenance owner は DB gateway を直接書かない。`LIB-01` の scan durable commit は immutable facts を返す別境界として維持する。installable maintenance の queue / mode / package target / progress は `LIB-02` に残すが、maintenance request は immutable snapshot で同じ maintenance owner へ渡す。
- chart-info hydration / backfill の queue / currentness / progress state、DB load / materialize / attach、candidate / parse / digest / chunk-write workflow と、startup metadata import / file-scan / LR2 committed-row / package inline-build / parse-failure delete の全 writer route を catalog owner family へ移し、`BMSLibrary` の queue / lock / private writer workflow、direct DB writer route、broad inline-build host を削除する。LR2 completion / trust は `LIB-04` まで immutable input の residual bridge とする。
- generic mutation production route は `LIB-03-GM-A`〜`D` の依存順で、relocation、removal、canonical receipt / intrinsic projection、direct composition / broad-host retirement を閉じる。catalog mutation owner は prepare → durable commit → canonical live apply / version → guard release → canonical receipt / event publish を所有し、旧 state applier の catalog row / path / folder / maintenance writer と mutation-delta broad host を削除する。package state apply と LR2 mutation block / normal-folder sync は canonical receipt 後の baseline composition として `LIB-02` / `LIB-04` まで残してよいが、新しい catalog callback host へ移し替えない。既存の owned-collection version / PropertyChanged timing は behavior test で維持し、public event を catalog guard 内の cross-owner callback にしない。
- behavior test で mutation ordering、version / receipt、failure atomicity、hydration、maintenance / resource-health の observable behavior を検証し、private class / method 配置を test contract にしない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- outcome-wide Full verification、変更範囲に対応する startup / catalog mutation / progress / failure の UI smoke check、重大な指摘なしの outcome review が完了する。

Non-goals:

- package / install-destination / library file-operation ownership（LIB-02）。
- LR2 request / run / status、LR2 run reservation、scan-surface、trust / freshness、folder sync、LR2-specific mutation-block ownership（LIB-04）。
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

許可する状態は `not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。`completed` は個別 outcome の全 acceptance criteria を満たした場合だけ、`gate met` は `GATE-01` にだけ使う。internal complexity は execution package 内で分解し、責務が別 outcome に属することが production evidence で確定した場合だけ ordered backlog の prerequisite outcome を再編する。

UI-01 の完了境界は main table presentation、regular chart workflow、playlist / play-history の result-to-table production contract、および main-table subtree の binding / event adapter とする。playlist source query / cache / queue、play history の feature action、playback、playlist summary、tree / dialog workflow、`MainWindow.cs` 全体の thin-shell 化は、対応する後続 outcome が active になった段階で閉じる。UI-01 はこれらを `MainChartList` に吸収しない。

UI-01 は public surface の変更だけを理由に再開しない。旧 sort / filter compatibility surface の整理は総合計画の bounded cleanup 方針に従い、production / XAML consumer、tests、旧 wrapper を一つの unit で更新する。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner、UI-02 の playback owner、UI-03 の playlist workspace owner、UI-04 の play-history owner は completed。設定画面と残る code-behind workflow は UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | scan core、catalog rows / digest / owned collection、resource-health、catalog-owned maintenance / maintenance-table hydration、chart-info hydration / backfill の owner 境界は成立済み。generic mutation の relocation / removal protocol、canonical receipt、broad host retirement と、package、LR2、playlist reference、残る facade bridge の削除は LIB-03-GM、LIB-02、LIB-04〜LIB-06 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で扱う |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | resource-health / maintenance / chart-info owner は成立済み。generic mutation の prepare / durable commit / live apply / receipt publish protocol、broad host retirement と各 consumer owner への移管は未完了 |
| Quality | in progress | code baseline と UI-04 outcome-wide Full verification / review / UI smoke、LIB-01 の再編後 criteria に対する outcome-wide Full verification / review / UI smoke は完了。LIB-03 は outcome-wide 検証待ち |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。複数 caller、broad host、lock ordering、DB / live atomicity、package / LR2 / resource-health residual は `LIB-03-GM` の内部分解条件であり、`EXTERNAL_BLOCKER` ではない。planner は sequence cursor から実装可能な順序列を返し、root は planner / reviewer 実行中の独立調査を行わない。
