> [P0-01 active plan](./P0-01_MainWindowViewModel_リファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

# P0-01 L-3c-8 Playlist build execution next boundary decision

作成日: 2026-07-06

## 結論

次の実装 ticket は `L-3c-9: Playlist rebuilt source view apply result DTO 導入` とする。

`BuildPlaylistSourceForRequest` をすぐ coordinator へ移動しない。次は、`RebuildPlaylistSource` 内の source build 後から terminal apply 前までにある「rebuilt source から view rows を作る stage」の結果を `PlaylistRebuiltSourceViewApplyResult` のような DTO に束ねる。

## 理由

L-3c-6 / L-3c-7 で source build stage は次の形になった。

- source rows / timing / score metrics は `PlaylistSourceBuildResult` にまとまった。
- library index diagnostics と folder timing は `PlaylistSourceBuildStageResult` にまとまった。
- freshness check は request 競合検出タイミング維持のため root 側に残した。

一方で `RebuildPlaylistSource` には、source build 後も view apply の結果がローカル変数として散らばっている。

- `finalRows`
- `viewCount`
- `sortProfile`
- `keywordCount`
- `modeCount`
- `keywordStageMs`
- `modeStageMs`
- `sortStageMs`
- `viewMaterializeMs`

この状態で coordinator 化へ進むと、source build result と view apply result の両方を一度に coordinator contract にする必要がある。まず view apply result を束ねると、次の coordinator 化 / root-local boundary 抽出で入出力を読みやすくできる。

## L-3c-9 Scope

実装すること:

- `BeMusicSeeker/ViewModels/MainWindow/PlaylistSourceBuildResult.cs` または近い場所に、rebuilt source view apply stage の結果 DTO を追加する。
- DTO は rebuilt source から作った view rows と stage metrics だけを保持する。
  - `IList FinalRows`
  - `int ViewCount`
  - `string SortProfile`
  - `int KeywordCount`
  - `int ModeCount`
  - `long KeywordStageMs`
  - `long ModeStageMs`
  - `long SortStageMs`
  - `long ViewMaterializeMs`
- `RebuildPlaylistSource` の `ApplyPlaylistVirtualViewFromSource(...)` 呼び出しと直後の completed log を、小さな root-local method へ寄せる。
- freshness check after apply、snapshot replace、terminal apply、finalize logging は root 側に残す。
- `finalRows` ownership と `finally` の `DisposePlaylistViewRows(finalRows)` safety を維持する。
- `PlaylistViewPipelineTests` の source-text guard を最小限更新する。

触らないこと:

- `BuildPlaylistSourceForRequest` の coordinator 化。
- `TryPatchPlaylistSourceChartInfoIndex` の外出し。
- `ApplyPlaylistViewFromCurrentSource` / view-only apply path の移動。
- `ReplacePlaylistSourceRows` / `ReplacePlaylistViewRows` の state owner 移動。
- `ApplyPlaylistDetailViewRowsToMainView` の service 化。
- XAML / code-behind / DataContext。
- persisted value、serialized name、DB schema、外部ファイル形式。

## Root に残す side effect

- BuildGate lifecycle。
- request freshness checks。
- `sourceRows` / `finalRows` ownership transfer and discard logging。
- `ReplacePlaylistSourceRows(...)`
- `ApplyPlaylistDetailViewRowsToMainView(...)`
- `FinalizeMainViewBuild(...)`
- completed / cancelled source build logging。

## Expected Verification

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- Roslynator は public surface / analyzer リスクが出た場合のみ実行する。

## Follow-up

L-3c-9 完了後に、次のどちらへ進むか判断する。

- source build + rebuilt source view apply をひとまとまりの root-local execution boundary にする。
- chart-info patch / view-only apply path との重複を見て、先に view apply result DTO を共通化する。
