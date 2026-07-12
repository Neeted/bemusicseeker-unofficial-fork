# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-12

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate 前は禁止。Gate 後もユーザーの明示指示まで禁止

## Active outcome

### `APP-01 Composition and configuration ownership`

状態: in progress

目的:

startup composition と settings load / edit / save / upgrade の ownership を明示し、feature owner が global settings と root の構築状態を直接取得しない境界を閉じる。既存の setting key、serialized value、upgrade timing、UI observable behavior は維持し、後続 outcome が用途別 snapshot / adapter を受け取れる状態にする。

Acceptance criteria:

- startup の child ViewModel / service / adapter composition が一つの明示的な owner を通り、root は composition と lifecycle に限定される。
- settings の load、editable な edit session、save、upgrade / migration が用途別 contract または adapter を通り、feature owner が `Settings.Default` を直接取得しない。
- settings snapshot は workflow が必要とする値だけを immutable に保持し、巨大な settings facade や interface-per-setting を追加しない。
- 既存の setting key、serialized value、upgrade timing、保存先、失敗契約を変更しない behavior test がある。
- startup、settings dialog の open / edit / save / 再表示、reload、shutdown の composition behavior test がある。
- 後続の UI / library / playlist owner を新しい composition boundary から構築でき、root の global singleton / platform dependency の直接取得が増えていない。
- build / test / format / analyzer / diff check と、重大な指摘なしのサブエージェント静的レビューが完了する。

Non-goals:

- feature workflow の owner 移管（UI-02、UI-03、UI-04、LIB-*、PL-*）。
- `MainWindow.cs` 全体の薄型化。main-table 以外の code-behind workflow は UI-02 / UI-03 / UI-04 / UI-05 で扱う。
- `.NET 10` TFM 変更、NuGet の一括更新、native dependency の置換。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Composition and configuration ownership | in progress |
| UI-02 Playback ownership | not started |
| UI-03 Playlist workspace ownership | not started |
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

## Gate scorecard

| Gate area | State | Current evidence |
|---|---|---|
| UI ownership | not met | UI-01 の main table / regular owner は completed。playback、playlist、play-history、設定画面と code-behind workflow は後続 UI-02 / UI-03 / UI-04 / UI-05 の対象。settings の composition / adapter 境界は APP-01 で扱う |
| Library ownership | not met | facade private state / host bridge が残る |
| Playlist ownership | not met | persistence / sync / output が同居する |
| Configuration ownership | not met | `Settings.Default` が ViewModel / domain / XAML / tests に広く残る |
| Platform boundary | not met | HintPath DLL、native layout、P/Invoke、external process、WPF / WinForms が混在 |
| Migration readiness | not met | architecture refactor と `.NET 10` 固有作業がまだ分離し切れていない |
| Quality | baseline met | code baseline 時点の build / test / format / analyzer は完了。各新規差分で再検証する |

数値は状態の正本ではない。Gate audit 時は実ソースから再計測し、partial / host file への移動で達成扱いにしない。

## Active outcome blockers

なし。

## APP-01 implementation progress

- 完了した縦切り: `ApplicationComposition` を XAML の `vm` resource に接続し、`BmsLibraryOptionsSnapshot` を immutable な用途別 snapshot として構築する provider を `MainWindowViewModel` から `BMSLibrary` production constructor へ渡す経路を追加した。
- `BMSLibrary` 本体と package-move host に残っていた設定・追加 custom output directory の直接取得を provider 経由へ移し、provider は operation ごとに評価する。public constructor の既存 fallback、設定キー、serialized value、保存タイミングは維持する。
- この縦切りの検証: Full build、全テスト（2523 passed / 13 skipped）、Roslynator 0 diagnostics、format / diff check、サブエージェント静的レビューで P0/P1 なし。
- 完了した縦切り: `StartupSettingsSnapshot` を `ApplicationComposition` から `MainWindowViewModel.Initialize` へ接続し、schema preflight/repair、LR2/standalone library profile、LR2 score DB path、standalone search roots、player 選択と LR2body path を同一 snapshot から構成する。`Initialize` ごとの provider 評価は一回に限定し、LR2 config は snapshot の path から再生成する。既存の validation、設定キー、保存タイミング、player 選択順序は維持する。
- この縦切りの検証: Full build、全テスト（2524 passed / 13 skipped）、Roslynator 0 diagnostics、format / diff check、サブエージェント静的レビューで P0/P1 なし。
- APP-01 の残作業: startup 後半の backup / `SkipInitPlaylistLoad` / `TableListURL` / deferred playlist sync、post-load custom-root repair、`BMSPlaylist` と `SettingDialogViewModel` の global settings 取得、App の upgrade / migration / theme / culture lifecycle、settings dialog の open/edit/save/reload/shutdown behavior test。これらを一つの巨大 facade にせず、production 経路へ接続する用途別縦切りで継続する。
