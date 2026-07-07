> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md) / [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 目的

`MainWindowViewModel` を application shell / composition / lifecycle / dialog request bridge / progress bridge / UI thread 境界へ寄せる。

playlist、main chart list、play history、playback、settings save、library refresh、package install の詳細 workflow は child ViewModel / coordinator / service へ移す。

## 現在地

2026-07-07。

| 項目 | 現状 |
|---|---|
| `MainWindowViewModel.cs` | 23,413 行。playlist build の helper / DTO 抽出は進み、view-only / source rebuild workflow 本体は coordinator へ移ったが、source build stage helper は root に残っている |
| `MainWindowViewModel.PlaylistState.cs` | 473 行。playlist identity / source snapshot / view snapshot state の受け皿 |
| `PlaylistSourceBuildResult.cs` | 249 行。source build / view apply / main view apply / build completion result contract |
| `PlaylistDetailBuildDecisionService.cs` | 210 行。source rebuild / chart-info patch / view-only apply 判定を担当 |
| `PlaylistDetailBuildQueueCoordinator.cs` | 264 行。request queue / coalescing / cancellation / worker state mutation を担当 |
| `PlaylistDetailBuildWorkflowCoordinator.cs` | 300 行。decision result に基づく patch / rebuild / view-only workflow、completion creation、finalize bridge を担当 |
| `MainWindowViewModel.PlaylistDetailBuildWorkflowHost.cs` | 154 行。root private method、BuildGate、source replace、finalize wrapper を workflow coordinator へ明示的に bridge |
| 旧 active | `L-3c-17: Playlist main view apply result DTO 導入` は独立 ticket から外し、`REF-MVP-A1` の subtask に格下げ |
| active lane | `REF-MVP-A1: Playlist detail build workflow extraction` |

## Active Ticket: `REF-MVP-A1`

### Playlist detail build workflow extraction

目的:

- playlist detail build の request scheduling、source build、view apply、main view apply、finalize timing を workflow 単位で root から coordinator / workspace へ移す。
- root `MainWindowViewModel` は request 発行、lifecycle、dialog / progress bridge、UI thread 境界だけを持つ。
- 挙動変更、XAML binding 変更、serialized value 変更、DB schema 変更はしない。

主対象:

- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.PlaylistState.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistDetailBuildDecisionService.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistDetailBuildQueueCoordinator.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistSourceBuildResult.cs`
- 新規候補: `PlaylistDetailBuildWorkspace`

subtasks:

1. 完了: `PlaylistDetailBuildWorkflowCoordinator` を追加し、`TryBuildPlaylistViewAndApply` の decision / dispatch を root から移した。
2. 完了: `ApplyPlaylistDetailViewRowsToMainView` の timing result DTO 化として `PlaylistMainViewApplyResult` を追加し、rebuild / view-only 両経路が戻り値 contract を読む形へ揃えた。
3. 完了: `PlaylistDetailBuildCompletionResult` と `FinalizePlaylistDetailBuild` を追加し、rebuild / view-only 両経路が同じ finalize contract を通る形へ揃えた。
4. 完了: completion contract の finalize 呼び出しを `PlaylistDetailBuildWorkflowCoordinator` / host bridge 経由へ寄せた。
5. 完了: rebuild / view-only の completion creation を `PlaylistDetailBuildWorkflowCoordinator` へ寄せた。
6. 完了: view-only apply workflow 本体を `PlaylistDetailBuildWorkflowCoordinator` へ移した。
7. 完了: BuildGate / cancellation / freshness check / cleanup ownership の順序を変えずに、source rebuild workflow 本体を root から `PlaylistDetailBuildWorkflowCoordinator` へ移した。
8. root に残すものを request 発行、lifecycle、UI thread bridge、dialog / progress bridge として説明できる状態にする。

完了条件:

- playlist detail build workflow の主要処理が root から移動している。
- root 側に残る処理が orchestration / bridge として説明できる。
- `MainWindowViewModel.cs` の playlist workflow 責務が明確に減っている。
- 既存 UI 挙動が変わらない。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Constraints

- release freeze を破らない。
- XAML Binding / code-behind event handler の参照先を無計画に変えない。
- persisted setting value、serialized name、DB schema、外部ファイル形式に触れない。
- BuildGate、cancellation / freshness check、cleanup ownership、terminal apply の UI side effects は順序を維持する。
- 新規 coordinator に `Window`、`Control`、`MessageBox`、`Settings.Default`、`NLog`、無制御な `Dispatcher` 直参照を増やさない。
- DTO、result object、helper 導入は subtask として扱い、独立した成果にしない。

## Guardrail

`MainWindowViewModel.cs` は最終 8,000 行以下を目標にする。行数は観測値であり、成否は責務境界、依存方向、テスト容易性、UI / DataContext 境界で判断する。

Guardrail 超過時に残せる責務:

- application shell
- composition
- lifecycle
- dialog request bridge
- progress bridge
- UI thread 境界
- child ViewModel / coordinator への委譲

## P0-03 連動条件

root pass-through 削除は、P0-03 / Lane B の XAML / code-behind DataContext 移行後でないと無理に進めない。MainWindow code-behind の event handler / `async void` は `REF-MVP-B1` で先行して扱う。

## 完了履歴の扱い

旧 `L-*` の詳細履歴は [P0-01 完了履歴](./P0-01_MainWindowViewModel_完了履歴.md) と各 decision file に残す。今後の active plan では `REF-MVP-A1` の workflow extraction を正本とし、DTO / checkpoint 単位の ticket へ戻さない。
