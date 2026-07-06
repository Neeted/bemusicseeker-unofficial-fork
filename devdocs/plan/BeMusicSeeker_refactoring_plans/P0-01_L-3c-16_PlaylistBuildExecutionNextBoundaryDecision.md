# P0-01 L-3c-16 Playlist build execution 次境界 decision

作成日: 2026-07-06

> 2026-07-07 superseded: この decision で次 ticket とした `L-3c-17` は、Refactoring MVP 計画更新により独立 ticket ではなく `REF-MVP-A1: Playlist detail build workflow extraction` の subtask に格下げした。現在の active lane は [PLAN_STATUS](./PLAN_STATUS.md) を正本とする。

## 決定

次の実装 ticket は `L-3c-17: Playlist main view apply result DTO 導入` とする。

`RebuildPlaylistSource` 全体の coordinator 化へはまだ進まない。先に `ApplyPlaylistDetailViewRowsToMainView` の `out long columnStageMs` / `out long callbackStageMs` を internal result DTO に束ね、source build、view apply、main view apply の各 stage が値を返す形を揃える。

## 理由

- L-3c-15 で view apply result は共通 contract になったが、terminal apply はまだ `out` 引数で timing metrics を root に返している。
- `ApplyPlaylistDetailViewRowsToMainView` は rebuilt source path と view-only path の両方から呼ばれるため、ここを揃えると後続 coordinator 化の surface が単純になる。
- UI side effect の実行位置、`FinalizeMainViewBuild` の呼び出し位置、main table swap の順序は動かさずに境界だけ整えられる。
- `callbackStageMs` は現状 `0L` のまま返す。意味を補完したり新しい callback 計測を足したりしない。

## 動かす範囲

- `PlaylistMainViewApplyResult` のような internal DTO を追加する。
- `ApplyPlaylistDetailViewRowsToMainView` の戻り値を DTO にし、`out` 引数を削除する。
- rebuilt source path / view-only path の `FinalizeMainViewBuild` は DTO の `ColumnStageMs` / `CallbackStageMs` を使う。
- source-text guard は main view apply result contract を検証する形へ更新する。

## 動かさない範囲

- `ReplacePlaylistViewRows`、`SetChartRowsView`、`TryMarkPlaylistOpenBuildCompleted` の順序。
- `RaiseMainTableSwapPreparing` と `loadColumnSetting` の実行位置。
- `FinalizeMainViewBuild` の呼び出し位置や引数の意味。
- BuildGate、cancellation / freshness check、source materialize、chart-info patch mutation。
- XAML Binding / code-behind event handler。
- persisted setting value、serialized name、DB schema、外部ファイル形式。

## ownership / side effect の扱い

- rebuilt source path の `finalRows` cleanup ownership は維持する。terminal apply が成功した後だけ root が `finalRows = null` にする流れを変えない。
- view-only path には新しい cleanup ownership を追加しない。
- DTO は timing metrics の返却だけに使い、UI side effect の実行を遅延・再実行可能にしない。

## 必要な確認

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- public surface / analyzer リスクが出た場合のみ Roslynator を実行する。
