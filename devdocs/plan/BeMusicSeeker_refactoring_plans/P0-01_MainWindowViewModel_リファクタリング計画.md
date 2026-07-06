> [総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md) / [完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

# P0-01 MainWindowViewModel リファクタリング計画

## 目的

`MainWindowViewModel` を app shell / composition root へ寄せ、一覧・playlist・play history・settings・sidebar・progress・playback の詳細を child ViewModel / coordinator / service に移す。

行数は観測値として使う。最終的な 1,000〜1,500 行という目安は残すが、直近 ticket の成功条件は責務境界、依存方向、テスト容易性、XAML / code-behind の DataContext 境界で判断する。

## 現在地

2026-07-06。

| 項目 | 現状 |
|---|---|
| `MainWindowViewModel.cs` | 24,056 行から C1 で `MainViewUpdateMode` を top-level 化。C2 で責務別 partial split へ進む |
| `ViewModels/MainWindow/` | `MainViewUpdateMode` は root nested type ではなく top-level contract を参照する |
| 直近の完了 | P0-01-C1: `viewUpdateMode` を `MainViewUpdateMode` top-level contract へ移動 |
| 次の active ticket | P0-01-C2: `MainWindowViewModel.cs` の責務別 partial split checkpoint |

## Now

### Active Ticket P0-01-C2: `MainWindowViewModel.cs` の責務別 partial split checkpoint

目的:

- `MainWindowViewModel.cs` に残る大きな責務を、挙動変更なしの partial split で見える形にする。
- playlist / play history / settings / sidebar / shell lifecycle などの境界を、後続の service / child VM 抽出前にレビューしやすくする。
- 行数削減だけを目的にせず、責務境界と依存方向で分割単位を決める。

対象候補:

| 優先 | 領域 | 方針 |
|---:|---|---|
| 1 | playlist workflow | L-3b-4 に進む前に、playlist source / view / build / terminal apply 周辺を分割単位として見えるようにする |
| 2 | play history workflow | P0-01 後半の候補として、playlist と混ざらない単位へ分ける |
| 3 | shell lifecycle / startup / shutdown | composition root と lifecycle の残存責務を見えるようにする |
| 4 | settings / sidebar | service 化はまだ行わず、分割候補と blocker を明確にする |

制約:

- 挙動変更を入れない。
- XAML Binding / code-behind event handler の参照先を変えない。
- persisted setting value、serialized name、DB schema、外部ファイル形式に触れない。
- 1 ticket 内で 3 個以上の sub-ticket が必要になった場合は、実装前に checkpoint / decision record を作り直す。
- SettingDialog service 化、root pass-through 削除、playlist coordinator 化はこの ticket では行わない。

完了条件:

- `MainWindowViewModel.cs` から責務別 partial へ移す範囲が 1 つ以上完了している。
- 移動先 partial の責務名が明確で、後続の service / child VM 抽出候補を説明できる。
- `SourceTextTestHelper.ReadMainWindowViewModelSourceText()` が分割後も対象を読める。
- behavior、persisted value、public user-facing text は変えない。

標準確認:

- 変更範囲に応じた関連 test。
- `dotnet build .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet test .\BeMusicSeeker.sln /p:Configuration=Release`
- `dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal`
- 大きな public surface / analyzer リスクがある場合のみ Roslynator を実行する。

## Next

次候補は最大 2 件に固定する。P0-01-C2 完了時は `P0-01-C3` / `L-3b-4` のどちらかを次の active ticket にする。

| 候補 | 内容 | 着手条件 |
|---|---|---|
| P0-01-C3 | source-text / private reflection test blocker の上位数件を direct service / child VM test へ移す | C2 の実装を阻害する test が特定されている |
| L-3b-4 | Playlist source / view snapshot state 抽出 | C2 / C3 で分割耐性と test blocker を処理した後 |

## Later

- L-3c: Playlist detail build coordinator 化。L-3b-4 と test blocker 解消後に詳細化する。
- L-4 / L-5: summary build / bulk mutation。PlaylistWorkspace の state 境界が見えた後に詳細化する。
- M / N / O / P / Q: play history、settings、sidebar、shell cleanup、playback command boundary。C2 / C3 と P0-03 early ticket の結果を見て再計画する。

## Blocked / 連動条件

- root pass-through 削除は、P0-03 の XAML / code-behind DataContext 移行後でないと無理に進めない。
- `Settings.Default` 直参照削減は P0-04 / P1-03 と owner を揃える。P0-01 では新規 service に直参照を増やさない。
- SettingDialog partial は 6,697 行あるが、設定 schema / persistence と結合しているため、P0-01-C2 では責務別 partial split 以上に踏み込まない。
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
