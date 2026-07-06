> [P0-01 active plan](./P0-01_MainWindowViewModel_リファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

# P0-01 L-3c-3 Playlist detail build execution boundary decision

作成日: 2026-07-06

## 結論

次の実装 ticket は `L-3c-4: Playlist detail terminal apply boundary 抽出` とする。

`RebuildPlaylistSource` / `ApplyPlaylistViewWithoutSourceRebuild` / `TryPatchPlaylistSourceChartInfoIndex` を一気に coordinator へ移動しない。まず、`RebuildPlaylistSource` と `ApplyPlaylistViewWithoutSourceRebuild` に重複している「view rows を main view へ反映する終端処理」を root 内の小さな boundary method にまとめる。

## 理由

playlist detail build execution は次の責務が同じ method 群に混在している。

| 領域 | 現状 | 今回の判断 |
|---|---|---|
| source materialize | `BuildPlaylistSourceRows` が `tables`, `files`, library index, score probe, cancellation stage を直接使う | まだ root に残す |
| chart-info patch | `TryPatchPlaylistSourceChartInfoIndex` が `files.ResolveChartInfo` と source row mutation を行う | まだ root に残す |
| view materialize | `ApplyPlaylistVirtualViewFromSource` / `PlaylistDetailPresentationService` は既に root-free に近い | すぐ大きく動かさない |
| snapshot replace | `ReplacePlaylistSourceRows` / `ReplacePlaylistViewRows` が `playlistViewState` の weak reference / generation を更新する | state owner をさらに整理するまで root に残す |
| terminal apply | `SelectedIndexChartRowsView`, `RaiseMainTableSwapPreparing`, `loadColumnSetting`, `SetChartRowsView`, `TryMarkPlaylistOpenBuildCompleted`, `FinalizeMainViewBuild` が重複 | 次 ticket で root-local boundary method にまとめる |

`terminal apply` は UI side effect なので、今すぐ service へ出すより root 内で境界名を与える方が安全である。これにより、後続で coordinator が返す result DTO と root が実行する terminal apply の関係を読みやすくできる。

## L-3c-4 Scope

実装すること:

- `ApplyPlaylistDetailViewRowsToMainView` のような root-local method を追加する。
- `RebuildPlaylistSource` と `ApplyPlaylistViewWithoutSourceRebuild` の重複 terminal apply sequence をその method へ寄せる。
- method input は、`PlaylistBuildRequest`, `IList finalRows`, `viewCount`, `sortProfile`, stage timings, folder/source counts, column setting mode など、既存 sequence の値に限定する。
- behavior、logging text、public user-facing text は変えない。

触らないこと:

- `BuildPlaylistSourceRows` の外出し。
- `TryPatchPlaylistSourceChartInfoIndex` の外出し。
- `ReplacePlaylistSourceRows` / `ReplacePlaylistViewRows` の state owner 移動。
- `PlaylistDetailBuildState.BuildGate` の owner 移動。
- XAML / code-behind / DataContext。

## Expected Verification

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- `dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path <VS2022 MSBuild> --properties Configuration=Release --severity-level warning --verbosity minimal`

## Follow-up

L-3c-4 完了後に、次のどちらを実装するか判断する。

- source build execution result DTO を導入し、source materialize と terminal apply をさらに分離する。
- source-text / private reflection test が邪魔になる場合は `P0-01-C3` に戻って blocker test を direct service test へ移す。
