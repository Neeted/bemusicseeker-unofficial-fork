> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md) / [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 目的

`MainWindowViewModel` を app shell / composition root へ寄せ、一覧・playlist・play history・settings・sidebar・progress・playback の詳細を child ViewModel / coordinator / service に移す。

行数は観測値として使う。最終的な 1,000〜1,500 行という目安は残すが、直近 ticket の成功条件は責務境界、依存方向、テスト容易性、XAML / code-behind の DataContext 境界で判断する。

## 現在地

2026-07-06 再ベースライン。

| 項目 | 現状 |
|---|---|
| `MainWindowViewModel.cs` | 24,056 行。開始時 33,379 行からは縮小したが、root workflow / facade / nested contract がまだ多い |
| `ViewModels/MainWindow/` | `PlaybackPanelViewModel`, `OperationProgressHubViewModel`, `MainChartListViewModel`, `PlaylistWorkspaceViewModel`, 各 coordinator / service を導入済み |
| P0-01 最終完了 | Gate 4 まで一気に進める前提にしない。P0-02 / P0-03 / P0-04 の並行可能範囲は総合計画で管理する |
| 直近の完了 | Ticket L-3b-3: Playlist detail worker state 抽出、コミット `0ebef79f` |
| 前回の次候補 | L-3b-4 だったが、再ベースラインにより checkpoint へ戻す |

## Now

### Active Ticket P0-01-C1: nested presentation contract top-level 化 checkpoint

目的:

- `MainWindowViewModel` の nested type に外部 service / child VM が依存している状態を減らす。
- `ViewModels/MainWindow/*.cs` が root nested type を読むためだけに `MainWindowViewModel` へ依存する流れを断つ。
- 以降の L-3b-4 / root partial split / XAML DataContext 移行をレビューしやすくする。

対象候補:

| 優先 | 型 | 方針 |
|---:|---|---|
| 1 | `viewUpdateMode` | top-level presentation contract 化。既存 numeric value / 既存意味は変えない |
| 2 | `cSortParameters` | `ChartSortDescriptor` などの snapshot contract 化を検討する |
| 3 | `ModeFilterType` | regular / playlist 両方で使う play-mode filter contract として切り出す |
| 4 | `PlaylistRequestIdentity`, `PlaylistSourceIdentity`, `PlaylistFilterType` | playlist request / view identity 境界の一部として、前 3 件の後に扱う |

制約:

- persisted setting value、serialized name、DB schema、外部ファイル形式に関わる値は変更しない。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。
- 1 ticket 内で 3 個以上の sub-ticket が必要になった場合は、実装前に checkpoint / decision record を作り直す。
- `PanelState` / player panel state / setting dialog partial はこの ticket では触らない。

完了条件:

- top-level 化する contract の範囲と名前が決まっている。
- 少なくとも最優先 contract について、`ViewModels/MainWindow/*.cs` からの root nested type 依存がなくなる、または後続 ticket に残す理由が明記される。
- source-text / private reflection test が root nested type の配置に過度に依存している場合、上位 blocker だけ direct service / child VM test へ移す。
- behavior、persisted value、public user-facing text は変えない。

標準確認:

- 変更範囲に応じた関連 test。
- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- 大きな public surface / analyzer リスクがある場合のみ Roslynator を実行する。

## Next

次候補は最大 2 件に固定する。P0-01-C1 完了時は `P0-01-C2` / `P0-01-C3` のどちらかを次の active ticket にする。

| 候補 | 内容 | 着手条件 |
|---|---|---|
| P0-01-C2 | `MainWindowViewModel.cs` の責務別 partial split checkpoint。挙動変更ではなく reviewability を上げる構造変更として、playlist / play history / settings / sidebar / shell lifecycle などの境界を明示する | C1 で nested contract 依存の大きい型が減り、分割先の型参照が安定している |
| P0-01-C3 | source-text / private reflection test blocker の上位数件を direct service / child VM test へ移す | C1 / C2 の実装を阻害する test が特定されている |

## Later

- L-3b-4: Playlist source / view snapshot state 抽出。C1 直後の次候補にはしない。C2 / C3 で分割耐性と test blocker を処理した後に戻る。
- L-3c: Playlist detail build coordinator 化。L-3b-4 と test blocker 解消後に詳細化する。
- L-4 / L-5: summary build / bulk mutation。PlaylistWorkspace の state 境界が見えた後に詳細化する。
- M / N / O / P / Q: play history、settings、sidebar、shell cleanup、playback command boundary。C1〜C3 と P0-03 early ticket の結果を見て再計画する。

## Blocked / 連動条件

- root pass-through 削除は、P0-03 の XAML / code-behind DataContext 移行後でないと無理に進めない。
- `Settings.Default` 直参照削減は P0-04 / P1-03 と owner を揃える。P0-01 では新規 service に直参照を増やさない。
- SettingDialog partial は 6,697 行あるが、設定 schema / persistence と結合しているため、P0-01-C1 では棚卸し以上に踏み込まない。
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
