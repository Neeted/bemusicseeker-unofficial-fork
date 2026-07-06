> [P0-01 active plan](./P0-01_MainWindowViewModel_リファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

# P0-01 L-3c-5 Source build result decision

作成日: 2026-07-06

## 結論

次の実装 ticket は `L-3c-6: Playlist source build result DTO 導入` とする。

`BuildPlaylistSourceRows` の処理本体、`TryPatchPlaylistSourceChartInfoIndex`、`ReplacePlaylistSourceRows`、terminal apply を一気に coordinator へ移動しない。まず `BuildPlaylistSourceRows` の戻り値と `out` 引数を `PlaylistSourceBuildResult` のような DTO に束ね、`RebuildPlaylistSource` の source build stage を「結果を受け取って UI / state side effect へ渡す」形へ近づける。

## 理由

L-3c-4 で view rows の terminal apply は `ApplyPlaylistDetailViewRowsToMainView` にまとまった。一方で `RebuildPlaylistSource` には、source materialize の結果、timing、score probe metrics、後続ログ材料が `out` 引数とローカル変数として散らばっている。

この状態で coordinator 化を進めると、次のどちらかになりやすい。

- root の mutable state / UI side effect まで service へ渡す。
- coordinator の引数や戻り値が巨大になり、責務境界が読みにくくなる。

先に source build result DTO を入れると、挙動を変えずに worker logic と root side effect の境界を名前で表せる。

## L-3c-6 Scope

実装すること:

- `BeMusicSeeker/ViewModels/MainWindow/PlaylistSourceBuildResult.cs` を追加する。
- DTO は source build stage の結果だけを保持する。
  - `List<PlaylistDetailSourceRow> SourceRows`
  - `int SourceCount` / `FolderCount`
  - `int ScoreUpdateTargetCount`
  - `long EntryResolveMs`
  - `long ScoreProbeMs`
  - `long SourceMaterializeMs`
  - `PlaylistScoreProbeMetrics ScoreProbeMetrics`
- `BuildPlaylistSourceRows` を、`out` 引数の束ではなく DTO を返す形へ変更する。
- `RebuildPlaylistSource` のログ文言、timing 値、cancellation stage、dispose safety を維持する。
- `PlaylistViewPipelineTests` の source-text guard を、DTO 導入後の意図に合わせて最小限更新する。

触らないこと:

- `BuildPlaylistSourceRows` の処理本体の外出し。
- `TryPatchPlaylistSourceChartInfoIndex` の外出し。
- `ReplacePlaylistSourceRows` / `ReplacePlaylistViewRows` の state owner 移動。
- `ApplyPlaylistDetailViewRowsToMainView` の service 化。
- `BuildGate` / worker queue ownership。
- XAML / code-behind / DataContext。
- persisted value、serialized name、DB schema、外部ファイル形式。

## Root に残す side effect

- `tables?.EnsurePlaylistEntriesLoaded(...)`
- `files?.GetScoreSnapshotForDiagnostics()` / `files?.ResolveChartInfo(...)`
- `LogPlaylistWorker(...)` / `LogPlaylistSourceBuild(...)` / `LogPlaylistViewApply(...)`
- `ReplacePlaylistSourceRows(...)`
- `ApplyPlaylistDetailViewRowsToMainView(...)`
- `FinalizeMainViewBuild(...)`
- cancellation freshness check and `BuildGate` release

## Expected Verification

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- Roslynator は public surface / analyzer リスクが出た場合のみ実行する。

## Follow-up

L-3c-6 完了後に、次のどちらへ進むか判断する。

- `L-3c-7`: source build result DTO を使って `RebuildPlaylistSource` の source build stage を root-local boundary method にまとめる。
- `P0-01-C3`: source-text / private reflection test が次の抽出を妨げる場合だけ、上位 blocker を direct service / child VM test へ移す。
