# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-16

## Baseline

- code baseline commit: `44fd2fde`
- library ownership bridge baseline: `b6f2ef7e`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `LIB-01 Scan pipeline core ownership`

状態: in progress

- active outcome base commit: `f8a0fc2b`
- last outcome-wide verified commit: `f8a0fc2b`

目的:

startup scan の request lifecycle、列挙、file diff、parse、durable commit、progress / failure / terminal result を明示的な pipeline owner に集約する。package、catalog mutation / maintenance、LR2 freshness の production owner が未成立のまま residual bridge を増やすことはせず、scan core の結果と downstream side effect の境界を明確にしたところで閉じる。

Acceptance criteria:

- startup scan の request generation、overlap / cancellation、scan-request freshness、failure、terminal completion と、列挙、file diff、parse、durable DB commit の production 通常経路が明示的な pipeline owner を通る。
- pipeline owner は scan result / commit facts と downstream catalog mutation への handoff point までを持ち、install-destination state、共有 catalog の in-memory apply、post-scan maintenance / resource-health、LR2 trust / freshness / folder sync を自分の state として所有しない。恒久的な immutable catalog request / snapshot / receipt は `LIB-03` で成立させる。
- `b6f2ef7e` に存在する package、storage / maintenance、LR2 の residual bridge を `LIB-01` で削除するための新しい adapter、host member、coordinator factory、test-only production seam を追加しない。
- scan core の production route、request lifecycle、skip / failure contract、commit ordering を behavior test で検証し、private host の形を test contract にしない。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約を維持する。
- outcome-wide Full verification、変更範囲に対応する startup / scan / progress / cancel / failure の UI smoke check、重大な指摘なしの outcome review が完了する。

再基準化監査:

- 次の安全な作業は downstream bridge の抽出ではなく、`f8a0fc2b..HEAD` と現行 production route を対象にした上記 criteria の outcome-wide audit である。
- 無修正で criteria を満たす場合は、Codex 共通実行ルールの plan rebaseline audit 例外で `LIB-01` を `completed`、canonical catalog writer を先に成立させる `LIB-03` を `ready` にする。
- 欠陥が見つかった場合は scan core の behavior / ownership だけを同じ outcome で修正する。package、catalog、LR2 の owner を代行する seam は追加しない。

Non-goals:

- package / install-destination / library file-operation ownership と、scan host の package callback 削除（LIB-02）。
- storage rows、owned collection、catalog mutation、post-scan maintenance / resource-health ownership と、storage / mutation bridge 削除（LIB-03）。
- LR2 request / run / status、LR2 run reservation、scan-surface、trust / freshness、folder sync ownership と、scan LR2 bridge 削除（LIB-04）。
- playlist-reference maps / index / display ownership（LIB-05）。
- residual scan host、nested host / factory、facade private workflow の最終削除（LIB-06）。
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
| LIB-01 Scan pipeline core ownership | in progress |
| LIB-03 Catalog storage, mutation, maintenance and resource-health ownership | not started |
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
| Library ownership | not met | scan core の production route は pipeline owner へ移行中。catalog mutation / maintenance、package、LR2、playlist reference の production owner と、facade private state / host bridge の削除は LIB-03、LIB-02、LIB-04〜LIB-06 の対象 |
| Playlist ownership | not met | persistence / sync / output が同居する。playlist-reference lifecycle は LR2 から分離して LIB-05 で扱う |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | library owner 境界と依存順は定義済みだが、canonical catalog writer / mutation serialization と各 consumer owner への移管は未実装 |
| Quality | in progress | code baseline と UI-04 outcome-wide Full verification / review / UI smoke は完了。LIB-01 は implementation unit 検証済みの変更を含むが、再編後 criteria に対する outcome-wide Full verification / review / UI smoke は未完了 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。次の作業は `LIB-01` の再基準化監査であり、downstream owner 未成立を埋める adapter 実装ではない。
