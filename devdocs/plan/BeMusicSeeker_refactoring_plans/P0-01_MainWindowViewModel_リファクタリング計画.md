> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md) / [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 目的

`MainWindowViewModel` を app shell / composition root へ寄せ、一覧・playlist・play history・settings・sidebar・progress・playback の詳細を child ViewModel / coordinator / service に移す。

行数は観測値として使う。最終的な 1,000〜1,500 行という目安は残すが、直近 ticket の成功条件は責務境界、依存方向、テスト容易性、XAML / code-behind の DataContext 境界で判断する。

## 現在地

2026-07-06。

| 項目 | 現状 |
|---|---|
| `MainWindowViewModel.cs` | 23,443 行。playlist view apply result DTO 共通化済み |
| `MainWindowViewModel.PlaylistState.cs` | 473 行。playlist identity / source snapshot / view snapshot state の受け皿 |
| `PlaylistSourceBuildResult.cs` | 195 行。source build / view apply / rebuild execution result contract |
| `PlaylistDetailBuildDecisionService.cs` | 210 行。source rebuild / chart-info patch / view-only apply 判定を担当 |
| `PlaylistDetailBuildQueueCoordinator.cs` | 264 行。request queue / coalescing / cancellation / worker state mutation を担当 |
| 直近の完了 | L-3c-16-checkpoint: playlist build execution boundary の次候補決定 |
| 次の active ticket | L-3c-17: Playlist main view apply result DTO 導入 |

## Now

### Active Ticket L-3c-17: Playlist main view apply result DTO 導入

目的:

- [L-3c-16 decision](./P0-01_L-3c-16_PlaylistBuildExecutionNextBoundaryDecision.md) に従い、`ApplyPlaylistDetailViewRowsToMainView` の `out` timing metrics を internal result DTO に束ねる。
- source build、view apply、main view apply の各 stage が値を返す形を揃え、後続 coordinator 化の surface を単純にする。
- UI side effect の順序と `FinalizeMainViewBuild` の呼び出し位置は変えない。

制約:

- 挙動変更を入れない。
- XAML Binding / code-behind event handler の参照先を変えない。
- persisted setting value、serialized name、DB schema、外部ファイル形式に触れない。
- terminal apply callback、main table swap、timing / logging side effects は root に残す。
- 挙動変更を入れない。
- BuildGate、source materialize の処理本体、chart-info patch mutation、view apply UI side effects は動かさない。
- `PlaylistSourceBuildResult` の public / serialized contract 化はしない。internal worker contract のまま扱う。
- rebuilt source path の `finalRows` cleanup ownership と view-only path の ownership 差分は変えない。
- 1 ticket 内で 3 個以上の sub-ticket が必要になった場合は、実装前に checkpoint / decision record を作り直す。

完了条件:

- `PlaylistMainViewApplyResult` のような internal DTO が追加され、`ApplyPlaylistDetailViewRowsToMainView` の `out` 引数がなくなっている。
- rebuilt source / view-only path の `FinalizeMainViewBuild` が DTO の timing metrics を使っている。
- `ReplacePlaylistViewRows`、`SetChartRowsView`、`TryMarkPlaylistOpenBuildCompleted` の順序が変わっていない。
- behavior、persisted value、public user-facing text は変えない。

標準確認:

- 変更範囲に応じた関連 test。
- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- 大きな public surface / analyzer リスクがある場合のみ Roslynator を実行する。

## Next

次候補は最大 2 件に固定する。L-3c-17 完了後に次の実装単位を判断する。

| 候補 | 内容 | 着手条件 |
|---|---|---|
| L-3c-18-checkpoint | main view apply result 導入後、playlist build execution boundary の次候補を決める | L-3c-17 完了後 |
| P0-01-C3 | source-text / private reflection test blocker の上位数件を direct service / child VM test へ移す | L-3c-17 で blocker が特定された場合 |

## Later

- L-4 / L-5: summary build / bulk mutation。PlaylistWorkspace の state 境界が見えた後に詳細化する。
- M / N / O / P / Q: play history、settings、sidebar、shell cleanup、playback command boundary。L-3c と P0-03 early ticket の結果を見て再計画する。

## Blocked / 連動条件

- root pass-through 削除は、P0-03 の XAML / code-behind DataContext 移行後でないと無理に進めない。
- `Settings.Default` 直参照削減は P0-04 / P1-03 と owner を揃える。P0-01 では新規 service に直参照を増やさない。
- SettingDialog partial は 6,697 行あるが、設定 schema / persistence と結合しているため、P0-01 の playlist ticket では触らない。
- MainWindow code-behind の event handler / `async void` は P0-03 の early inventory と連動して扱う。

## P0-01 最小完了条件

P0-01 は Gate 4 完了まで他 P0 を止める gate ではない。P0-01 側の最小完了は次を満たす状態とする。

- root nested presentation contract 依存が縮小し、child VM / service が root type を契約型置き場として使わない。
- main chart list と playlist の主要 workflow は child VM / coordinator / service へ移り、root は shell / composition / lifecycle / dialog request に寄る。
- XAML / code-behind / tests が root pass-through だけに依存し続けないための P0-03 連動条件が明確になっている。
- 残る root の行数超過分を責務として説明できる。

## 更新ルール

ticket 完了時に更新するのは次だけにする。

- 実装結果
- 実行したテスト
- 未解決の blocker
- 次にやる 1 件

長い調査ログや完了済み ticket の詳細は、この active plan ではなく [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md) または [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md) に移す。
