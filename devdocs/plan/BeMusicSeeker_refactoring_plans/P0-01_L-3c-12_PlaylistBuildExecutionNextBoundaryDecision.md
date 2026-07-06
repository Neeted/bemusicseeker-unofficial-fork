> [P0-01 active plan](./P0-01_MainWindowViewModel_リファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

# P0-01 L-3c-12 Playlist build execution next boundary decision

作成日: 2026-07-06

## 結論

次の実装 ticket は `L-3c-13: Playlist view-only apply result DTO 導入` とする。

`RebuildPlaylistSource` の execution boundary をすぐ coordinator へ移動しない。まず `ApplyPlaylistViewWithoutSourceRebuild` にも view apply result DTO を導入し、rebuilt source path と view-only path の result contract を揃える。

## 理由

L-3c-11 時点で rebuilt source path は次の形になった。

- source build result を `PlaylistSourceBuildStageResult` で受ける。
- rebuilt source view apply result を `PlaylistRebuiltSourceViewApplyResult` で受ける。
- completed log / finalize に必要な値を `PlaylistRebuildExecutionResult` 経由で読む。

一方で view-only path はまだ `ApplyPlaylistViewFromCurrentSource(...)` の `out` 引数と `finalRows` local に依存している。

coordinator 化を先に進めると、rebuilt source path と view-only path の result 形状が違うままになり、共通化すべきものと path 固有で残すものが見えにくい。先に view-only path の result DTO を導入すると、次の coordinator 化 / common view apply boundary 判断がしやすくなる。

## L-3c-13 Scope

実装すること:

- `PlaylistViewOnlyApplyResult` のような internal DTO を追加する。
- `ApplyPlaylistViewFromCurrentSource(...)` の `out` 引数を DTO 返却へ寄せる。
- `ApplyPlaylistViewWithoutSourceRebuild` の local 展開を DTO 経由にする。
- view-only path の freshness check、terminal apply、`FinalizeMainViewBuild` は root 側に残す。
- `finalRows` ownership は現状どおり扱い、挙動変更を入れない。
- source-text guard は必要最小限だけ更新する。

触らないこと:

- `RebuildPlaylistSource` execution boundary の coordinator 化。
- `ApplyPlaylistViewWithoutSourceRebuild` と rebuilt source path の共通 method 化。
- terminal apply の service 化。
- snapshot state owner 移動。
- `TryPatchPlaylistSourceChartInfoIndex` の外出し。
- XAML / code-behind / DataContext。
- persisted value、serialized name、DB schema、外部ファイル形式。

## Root に残す side effect

- request freshness checks。
- terminal apply。
- `FinalizeMainViewBuild(...)`。
- current source snapshot lock / weak reference owner。
- completed / cancelled logging。

## Expected Verification

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- Roslynator は public surface / analyzer リスクが出た場合のみ実行する。

## Follow-up

L-3c-13 完了後に、次のどちらへ進むか判断する。

- rebuilt source path と view-only path の view apply result DTO を共通化する。
- playlist build execution boundary を coordinator 化するために dependencies / root side effects を棚卸しする。
