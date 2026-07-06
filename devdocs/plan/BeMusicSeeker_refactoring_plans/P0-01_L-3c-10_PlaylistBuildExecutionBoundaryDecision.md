> [P0-01 active plan](./P0-01_MainWindowViewModel_リファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

# P0-01 L-3c-10 Playlist build execution boundary decision

作成日: 2026-07-06

## 結論

次の実装 ticket は `L-3c-11: RebuildPlaylistSource execution result boundary 抽出` とする。

`ApplyPlaylistViewWithoutSourceRebuild` への横展開や coordinator 化はまだ行わない。まず `RebuildPlaylistSource` 内で、source build stage と rebuilt source view apply stage の結果をまとめる root-local execution result を導入し、UI / state side effect に渡す直前までの worker result を 1 つの contract として読める形にする。

## 理由

L-3c-6 から L-3c-9 で、rebuilt source path は次の境界を持った。

- `PlaylistSourceBuildResult`
- `PlaylistSourceBuildStageResult`
- `PlaylistRebuiltSourceViewApplyResult`
- `BuildPlaylistSourceForRequest(...)`
- `ApplyPlaylistViewFromRebuiltSource(...)`

ただし `RebuildPlaylistSource` 本体では、source build result と view apply result から多数のローカル変数を取り出している。このまま coordinator 化すると root に残すべき freshness check / ownership transfer / UI side effect と、worker result の読み取りが混ざったままになる。

次に root-local execution result を導入すると、次段で coordinator 化する場合も、root に残す side effect を判断しやすくなる。

## L-3c-11 Scope

実装すること:

- `PlaylistRebuildExecutionResult` のような internal DTO を追加する。
- DTO は source build stage result と rebuilt source view apply result を束ね、completed log / finalize に必要な読み取り値を提供する。
- `RebuildPlaylistSource` 内の source build result / view apply result からのローカル変数展開を減らす。
- request freshness checks は root 側で維持する。
- `sourceRows` / `finalRows` ownership は root の `finally` が見える形を維持する。
- snapshot replace、terminal apply、`FinalizeMainViewBuild`、completed / cancelled source build logging は root 側に残す。
- source-text guard は必要最小限だけ更新する。

触らないこと:

- `BuildPlaylistSourceForRequest` / `ApplyPlaylistViewFromRebuiltSource` の coordinator 化。
- `ApplyPlaylistViewWithoutSourceRebuild` の共通化。
- `TryPatchPlaylistSourceChartInfoIndex` の外出し。
- snapshot state owner 移動。
- terminal apply の service 化。
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

L-3c-11 完了後に、次のどちらへ進むか判断する。

- root-local execution boundary を coordinator へ移せるか、dependencies / side effects を棚卸しする。
- view-only apply path に view apply result DTO を横展開し、rebuilt source path との共通化候補を整理する。
