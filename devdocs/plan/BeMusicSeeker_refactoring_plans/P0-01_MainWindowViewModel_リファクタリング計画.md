> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md) / [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 目的

`MainWindowViewModel` を app shell / composition root へ寄せ、一覧・playlist・play history・settings・sidebar・progress・playback の詳細を child ViewModel / coordinator / service に移す。

行数は観測値として使う。最終的な 1,000〜1,500 行という目安は残すが、直近 ticket の成功条件は責務境界、依存方向、テスト容易性、XAML / code-behind の DataContext 境界で判断する。

## 現在地

2026-07-06。

| 項目 | 現状 |
|---|---|
| `MainWindowViewModel.cs` | 23,538 行。playlist detail build decision は service 化済み |
| `MainWindowViewModel.PlaylistState.cs` | 485 行。playlist identity / source snapshot / view snapshot state の受け皿 |
| `PlaylistDetailBuildDecisionService.cs` | 210 行。source rebuild / chart-info patch / view-only apply 判定を担当 |
| 直近の完了 | L-3c-1: Playlist detail build decision service 抽出 |
| 次の active ticket | L-3c-2: Playlist detail worker queue / cancellation 境界整理 |

## Now

### Active Ticket L-3c-2: Playlist detail worker queue / cancellation 境界整理

目的:

- `PlaylistDetailBuildState` の request queue / coalescing / cancellation / worker-running state 操作を、root から coordinator 境界へ寄せる。
- root は request 作成、open timing、logging、Task 起動、shutdown lifecycle の入口を担当し、queue mutation の詳細を薄くする。
- L-3c-1 で作った `PlaylistDetailBuildDecisionService` は維持し、source rebuild / view apply / chart-info patch の実行本体はまだ root に残す。

制約:

- 挙動変更を入れない。
- XAML Binding / code-behind event handler の参照先を変えない。
- persisted setting value、serialized name、DB schema、外部ファイル形式に触れない。
- terminal apply callback、main table swap、timing / logging side effects は root に残す。
- BuildGate、source materialize、chart-info patch mutation、view apply UI side effects は今回の ticket では動かさない。
- 1 ticket 内で 3 個以上の sub-ticket が必要になった場合は、実装前に checkpoint / decision record を作り直す。

完了条件:

- request queue / coalescing / dequeue / worker stop / shutdown cancellation の state mutation が専用 coordinator/helper へまとまっている。
- root の `RegisterPlaylistSourceBuildRequest` / `ProcessPendingPlaylistBuildRequests` は、queue 操作の詳細ではなく coordinator API 呼び出し中心になっている。
- coordinator/helper へ移した範囲を root なしで直接検証できる test がある。
- `SourceTextTestHelper.ReadMainWindowViewModelSourceText()` と playlist source-text tests が分割後も意図を検証できる。
- behavior、persisted value、public user-facing text は変えない。

標準確認:

- 変更範囲に応じた関連 test。
- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- 大きな public surface / analyzer リスクがある場合のみ Roslynator を実行する。

## Next

次候補は最大 2 件に固定する。L-3c-2 が大きすぎると判明した場合は checkpoint を作り、次候補のどちらかを active ticket に戻す。

| 候補 | 内容 | 着手条件 |
|---|---|---|
| L-3c-3 | Playlist detail source rebuild / view apply 実行境界の整理 | L-3c-2 完了後に詳細な実装プランを検討する |
| P0-01-C3 | source-text / private reflection test blocker の上位数件を direct service / child VM test へ移す | L-3c-2 の実装を阻害する test が特定された場合 |

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
