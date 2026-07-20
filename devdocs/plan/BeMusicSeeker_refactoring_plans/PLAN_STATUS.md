# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-21

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `UI-05 Shell closure`

状態: in progress

active outcome base commit: `6d170cb9`
active execution package: `UI-05 Shell closure`
sequence cursor: `UI05 planner required`

目的:

settings dialog、library refresh、package operation、残存 feature の root pass-through と code-behind workflow を、明示的な shell composition と feature owner へ移し、UI Gate に残る責務を閉じる。

Acceptance criteria:

- settings dialog、normal library refresh、package operation、残存 feature workflow が production の通常経路で owner / shell composition に直接接続され、旧 root pass-through、code-behind workflow、callback host、test-only seam が同じ unit で削除される。
- UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、外部同期の cancellation / progress を維持する。
- owner handoff、live apply、immutable receipt / completion publish の順序と shutdown / scheduler rejection / stale generation の失敗契約を維持する。
- 各 execution unit の behavior test、UI-05 outcome-wide Full verification、Release executable UI smoke、重大指摘なしの static / outcome review を完了する。

Non-goals:

- feature 内に残る `Settings.Default` の除去（各 feature outcome と `MIG-01`）。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。
- feature owner 自体の責務再設計（各 feature outcome）。

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
| PL-01 Playlist persistence and reload ownership | completed |
| PL-02 Playlist external-sync and output ownership | completed |
| UI-05 Shell closure | in progress |
| MIG-01 Platform boundary closure | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は `not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。`completed` は個別 outcome の全 acceptance criteria を満たした場合だけ、`gate met` は `GATE-01` にだけ使う。internal complexity は execution package 内で分解し、責務が別 outcome に属することが production evidence で確定した場合だけ ordered backlog の prerequisite outcome を再編する。

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | feature ownership outcomes は completed。settings dialog と残る shell / code-behind workflow を UI-05 で閉じ、Gate audit で確認する |
| Library ownership | not met | library / playlist owner outcomes は completed。残る shell composition を UI-05 で閉じ、Gate audit で owner boundary と旧 route 不在を確認する |
| Playlist ownership | not met | persistence / reload と external-sync / output の owner outcomes は completed。残る shell consumer route を UI-05 で閉じ、Gate audit で確認する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | domain / playlist owner outcomes は completed。UI-05 の shell closure と MIG-01 の platform boundary closure 後に Gate audit で確認する |
| Quality | in progress | committed implementation units は unit-level verification / review 済み。UI-05 outcome-wide verification / review と最終 Gate evidence は未完了 |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。
