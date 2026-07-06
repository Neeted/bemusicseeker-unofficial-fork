# PLAN_STATUS

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

最終更新日: 2026-07-06

## Active Ticket

L-3c-10-checkpoint: 次の playlist build execution 境界決定

目的:

- L-3c-9 で導入した `PlaylistRebuiltSourceViewApplyResult` / `ApplyPlaylistViewFromRebuiltSource` を前提に、次に動かす境界を 1 ticket に絞る。
- source build + rebuilt source view apply を execution boundary 化するか、view-only apply path へ横展開するかを decision record に残す。
- freshness check、snapshot replace、terminal apply、finalize logging を root に残した判断を後続計画に反映する。

次に読む:

- [P0-01 MainWindowViewModel リファクタリング計画](./P0-01_MainWindowViewModel_リファクタリング計画.md)
- [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)

## Next 2 Tickets

| 順 | 候補 | 条件 |
|---:|---|---|
| 1 | L-3c-11: L-3c-10-checkpoint の decision に従う実装 ticket | L-3c-10-checkpoint で実装単位が確定した後 |
| 2 | P0-01-C3: source-text / private reflection test blocker 上位数件の direct service / child VM test 化 | L-3c-10-checkpoint で blocker が特定された場合 |

## Blocked / Waiting

| 項目 | 状態 |
|---|---|
| root pass-through 削除 | P0-03 の XAML / code-behind DataContext 移行後に進める |
| SettingDialog service 化 | settings schema / persistence owner を P0-04 / P1-03 と揃えてから詳細化する |
| `net10.0-windows` 本移行 | P0-04 Phase 0 inventory と UI / dependency 境界整理後に dry-run plan を作る |

## P0 Status

| 計画 | 現在地 | 並行可否 |
|---|---|---|
| P0-01 | L-3c-9 完了。active は L-3c-10-checkpoint | WIP は原則 1 ticket |
| P0-02 | BMSLibrary Phase 0 は未着手 | source-text helper / reflection inventory / dependency inventory は即並行可 |
| P0-03 | MainWindow UI Phase 0 は未着手。一部 DataContext 移行は P0-01 で先行済み | event handler inventory / `async void` 分類は即並行可 |
| P0-04 | SDK 10 + `net472` baseline。TFM は未変更 | TFM 変更なしの棚卸しは即並行可 |

## Latest Completed Ticket

L-3c-9: `PlaylistRebuiltSourceViewApplyResult` と `ApplyPlaylistViewFromRebuiltSource` を追加し、rebuilt source から view rows を作る stage の rows / metrics / completed log を root-local boundary にまとめた。

実行した確認:

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~ChartInfoMetadataTests.BackfillChartInfos_TimeoutStillPersistsDigest"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- `dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path <VS2022 MSBuild> --properties Configuration=Release --severity-level warning --verbosity minimal`

## 次回 Codex が最初に読むべきファイル

1. [00_Codex共通実行ルール.md](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS.md](./PLAN_STATUS.md)
3. [P0-01_MainWindowViewModel_リファクタリング計画.md](./P0-01_MainWindowViewModel_リファクタリング計画.md)
4. [99_調査メモ_現状メトリクス.md](./99_調査メモ_現状メトリクス.md)
