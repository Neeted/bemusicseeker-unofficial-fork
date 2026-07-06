# P0-01 L-3c-14 Playlist build execution 次境界 decision

作成日: 2026-07-06

## 決定

次の実装 ticket は `L-3c-15: Playlist view apply result DTO 共通化` とする。

`RebuildPlaylistSource` の coordinator 化へはまだ進まない。先に rebuilt source path と view-only path が返す view apply 結果を同じ internal contract に揃え、root が扱う「view rows / filter counts / sort profile / timing metrics」の形を一本化する。

## 理由

- `PlaylistRebuiltSourceViewApplyResult` と `PlaylistViewOnlyApplyResult` はほぼ同じ責務を持つが、型が分かれているため後続の execution boundary が二重に見える。
- BuildGate、freshness check、terminal apply、main table swap、`FinalizeMainViewBuild` はまだ root に残す方が安全。
- DTO 共通化は UI side effect や persisted value に触れず、後続 coordinator 化で切り出す入力と出力を読みやすくする。
- L-3c-4 以降の terminal apply boundary を壊さず、source build と view apply の境界だけを整えられる。

## 動かす範囲

- `PlaylistRebuiltSourceViewApplyResult` と `PlaylistViewOnlyApplyResult` を、共通の `PlaylistViewApplyResult` へ寄せる。
- `ApplyPlaylistViewFromRebuiltSource` と `ApplyPlaylistViewFromCurrentSource` の戻り値を同じ型にする。
- `PlaylistRebuildExecutionResult` は共通 result を受け取る。
- source-text guard は「view apply result が共通 contract になっている」ことを検証する形へ更新する。

## 動かさない範囲

- `RebuildPlaylistSource` 全体の coordinator 化。
- `BuildPlaylistSourceForRequest` / source materialize 本体。
- BuildGate、cancellation / freshness check、terminal apply callback、main table swap。
- `FinalizeMainViewBuild` の呼び出し位置。
- XAML Binding / code-behind event handler。
- persisted setting value、serialized name、DB schema、外部ファイル形式。

## ownership / logging の扱い

- rebuilt source path は、root の `finally` が未適用 `finalRows` を dispose できるように、引き続き `ref IList finalRows` で ownership を root に見せる。
- view-only path は従来どおり root 側の cleanup ownership を追加しない。今回の共通化では挙動を変えない。
- `sourceCount` は view-only path の finalize metrics に必要なので、共通 result に含める。rebuilt source path では source build result と同じ値を渡す。
- started / completed log の内容と順序は維持する。

## 必要な確認

- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~PlaylistDetailBuildState_SourceText"`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- Roslynator は public surface / analyzer リスクが出た場合に実行する。
