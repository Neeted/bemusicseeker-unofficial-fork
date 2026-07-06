# PLAN_STATUS

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

最終更新日: 2026-07-06

## Active Ticket

L-3c-2: Playlist detail worker queue / cancellation 境界整理

目的:

- `PlaylistDetailBuildState` の request queue / coalescing / cancellation / worker-running state 操作を、root から coordinator 境界へ寄せる。
- root は request 作成、open timing、logging、Task 起動、shutdown lifecycle の入口を担当し、queue mutation の詳細を薄くする。
- L-3c-1 で作った `PlaylistDetailBuildDecisionService` は維持し、source rebuild / view apply / chart-info patch の実行本体はまだ root に残す。

次に読む:

- [P0-01 MainWindowViewModel リファクタリング計画](./P0-01_MainWindowViewModel_リファクタリング計画.md)
- [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)

## Next 2 Tickets

| 順 | 候補 | 条件 |
|---:|---|---|
| 1 | L-3c-3: Playlist detail source rebuild / view apply 実行境界の整理 | L-3c-2 完了後に詳細な実装プランを検討する |
| 2 | P0-01-C3: source-text / private reflection test blocker 上位数件の direct service / child VM test 化 | L-3c-2 の実装を阻害する test が特定された場合 |

## Blocked / Waiting

| 項目 | 状態 |
|---|---|
| root pass-through 削除 | P0-03 の XAML / code-behind DataContext 移行後に進める |
| SettingDialog service 化 | settings schema / persistence owner を P0-04 / P1-03 と揃えてから詳細化する |
| `net10.0-windows` 本移行 | P0-04 Phase 0 inventory と UI / dependency 境界整理後に dry-run plan を作る |

## P0 Status

| 計画 | 現在地 | 並行可否 |
|---|---|---|
| P0-01 | L-3c-1 完了。active は L-3c-2 | WIP は原則 1 ticket |
| P0-02 | BMSLibrary Phase 0 は未着手 | source-text helper / reflection inventory / dependency inventory は即並行可 |
| P0-03 | MainWindow UI Phase 0 は未着手。一部 DataContext 移行は P0-01 で先行済み | event handler inventory / `async void` 分類は即並行可 |
| P0-04 | SDK 10 + `net472` baseline。TFM は未変更 | TFM 変更なしの棚卸しは即並行可 |

## Latest Completed Ticket

L-3c-1: `PlaylistDetailBuildDecisionService` を追加し、source rebuild / chart-info patch / view-only apply の判定を root-free service として抽出した。

実行した確認:

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildDecision"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- `dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path <VS2022 MSBuild> --properties Configuration=Release --severity-level warning --verbosity minimal`

## 次回 Codex が最初に読むべきファイル

1. [00_Codex共通実行ルール.md](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS.md](./PLAN_STATUS.md)
3. [P0-01_MainWindowViewModel_リファクタリング計画.md](./P0-01_MainWindowViewModel_リファクタリング計画.md)
4. [99_調査メモ_現状メトリクス.md](./99_調査メモ_現状メトリクス.md)
