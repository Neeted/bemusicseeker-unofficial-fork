# P0-03 MainWindow UI / code-behind MVVM 移行計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 目的

`BeMusicSeeker/Views/MainWindow.cs` は 10,289 行、`MainWindow.xaml` は 2,510 行ある。`MainWindowViewModel` を整理しても、code-behind と巨大 XAML が UI logic を抱えたままだと、MVVM としての見通しや将来的な `.NET 10` 移行性は十分に改善しない。

この計画では、添付画像の UI 構成に合わせて MainWindow を shell view に寄せる。

- 上部: 再生パネル
- 左サイドバー上段/下段: navigation tree
- 中央: chart / playlist / summary table workspace
- 横断: progress / status / dialog routing / drag-drop

## 現状観測

| 対象 | 規模 / 問題 |
|---|---|
| `MainWindow.cs` | 10,289 行。event handler、context menu 構築、drag & drop、URL download、external package lookup、duplicate merge、score viewer upload、external player host が同居 |
| `MainWindow.xaml` | 2,510 行。上部再生パネル、tree、table、context menu、progress UI が同居 |
| `async void` | production の `MainWindow.cs` に 74 箇所程度。WPF event entry point 以外にも処理本体が混ざりやすい |
| 最大 method | `tableContextMenuOpened` 約 566 行。context menu state calculation と UI 操作が混在 |
| WPF + WinForms | `UseWPF=True`, `UseWindowsForms=True`。player panel / WindowsFormsHost 周辺は .NET 10 移行時の注意点 |

## 現在地と P0-01 連動条件

2026-07-06 再ベースラインでは、P0-03 は P0-01 の後半 blocker を先に減らす計画として扱う。

P0-01 では `PlaybackPanelViewModel` / `OperationProgressHubViewModel` / `MainChartListViewModel` / `PlaylistWorkspaceViewModel` が導入され、一部 XAML は child VM DataContext へ先行移行済みである。これは Gate 後の例外ではなく、root pass-through 削除へ向けた段階移行として継続してよい。

即並行できるもの:

- MWUI-0B: MainWindow event handler inventory。
- `async void` と処理本体の分類。
- XAML DataContext 移行対象の棚卸し。
- playback / progress / main workspace の UserControl 化準備。
- 既存 `SourceTextTestHelper.ReadMainWindowSourceText()` の拡張。新規作成済みの場合は重複して作らない。

P0-01 と連動するもの:

- root pass-through 削除は、対応 XAML / code-behind が child VM or service を直接参照できるようになってから行う。
- UserControl 化は、child VM の契約型と DataContext が安定した領域から進める。
- `WindowsFormsHost` / player host / external process UI は .NET 10 と UI handle 依存が絡むため、P0-04 inventory 後に詳細化する。

## 目標アーキテクチャ

```text
MainWindow.xaml / MainWindow.cs           // shell view, lifecycle, view-host only
├─ Views/MainWindow/PlaybackPanelView.xaml
├─ Views/MainWindow/SidebarNavigationView.xaml
├─ Views/MainWindow/MainWorkspaceView.xaml
├─ Views/MainWindow/ProgressStatusView.xaml
├─ Views/Behaviors/CustomTableCommandAdapter.cs
├─ ViewModels/MainWindow/ChartContextMenuViewModel.cs
├─ ViewModels/MainWindow/PlaylistTreeContextMenuViewModel.cs
├─ ViewModels/MainWindow/PlaybackPanelViewModel.cs
├─ ViewModels/MainWindow/MainWorkspaceViewModel.cs
└─ Views/Dialogs/UiDialogCoordinator.cs   // existing dialog route を継続利用
```

### MainWindow code-behind に残すもの

- `InitializeComponent()` と DataContext 初期化。
- Window lifecycle: `OnClosing`, window state persistence adapter。
- WPF control / WinForms host / OS window handle を直接触る必要がある薄い adapter。
- Event-to-command bridge が未対応の WPF event entry point。
- View 固有の focus / selection / scroll / hit test の最小処理。

### MainWindow code-behind から出すもの

- context menu item の有効/無効/表示判定。
- URL 解決、Google Drive warning page 解決、download 実行。
- duplicate folder merge / cleanup の domain orchestration。
- score viewer registration / upload confirmation flow。
- playlist table drag/drop の workflow。
- install pending tree の mutation call。
- keyword search suggestion の UI 状態算出。
- user-facing dialog 表示判断。

## 実装ルール

1. P0-01 の Gate 完了だけを待たず、child VM / coordinator 境界が安定した小領域は先行して DataContext 移行してよい。大規模 XAML 分割は対象領域の child VM contract が安定してから行う。
2. 先に pure helper / service を抽出し、次に event-command bridge、最後に XAML split を行う。
3. WPF `Control` / `RoutedEventArgs` / `DragEventArgs` を ViewModel に渡さない。
4. `ICommand` 化できるものは ViewModel command へ寄せる。
5. `async void` は WPF event handler 入口だけに残し、処理本体は `Task` method に逃がす。
6. user-facing text を増やす場合は resource / lang JSON の parity を守る。
7. `MenuItem` / `ContextMenu` は .NET 10 の WPF+WinForms ambiguity を避けるため、必要に応じて `System.Windows.Controls.MenuItem` などに明示する。

## Phase 0: 分割前の安全網

### Ticket MWUI-0A: MainWindow source-text test helper を作る

2026-07-06 時点で helper が既に存在する場合、この ticket は「既存 helper の対象範囲確認と不足分の拡張」として扱う。

変更候補:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`
- `DialogRouteConsolidationTests.cs`
- `MainWindowContextMenuResourceTests.cs`
- `PlaylistSummaryBulkEditTests.cs`
- `BmsLibraryMutationBoundaryTests.cs`

作業:

1. `ReadMainWindowSourceText()` を追加する。
2. `MainWindow.cs` 単体を読む test を helper に変更する。
3. helper は次を連結する。
   - `BeMusicSeeker/Views/MainWindow.cs`
   - `BeMusicSeeker/Views/MainWindow*.cs`
   - `BeMusicSeeker/Views/MainWindow/**/*.cs`
   - 必要に応じて `Views/Behaviors/**/*.cs`

受け入れ条件:

- `MainWindow.cs` を partial 分割 / helper 抽出しても設計境界 test が落ちない。

### Ticket MWUI-0B: event handler inventory を作る

作業:

1. `MainWindow.xaml` の `Click=`, `Opened=`, `Drop=`, `Preview*`, `SelectionChanged=` を一覧化する。
2. 対応する `MainWindow.cs` method の分類を `.tmp/refactor/mainwindow-event-handler-inventory.md` に書く。
3. 分類は次のいずれか。
   - View-only: code-behind に残す。
   - Command candidate: ViewModel command へ移す。
   - Behavior candidate: attached behavior / adapter へ移す。
   - Service candidate: pure service / presentation service へ移す。

受け入れ条件:

- 後続 ticket の対象 method が明確になる。
- `async void` のうち処理本体を持つものが洗い出される。

## Phase 1: pure helper / presentation service 抽出

### Ticket MWUI-1A: external URL / Google Drive helper を service 化する

対象 method 例:

- `TryResolveGoogleDriveWarningPageUri`
- URL open / diff URL download 関連 helper
- `DownloadSelectedPlaylistUrlsAsync`

新規候補:

```text
BeMusicSeeker/ViewModels/MainWindow/ExternalNavigationRequest.cs
BeMusicSeeker/ViewModels/MainWindow/ExternalNavigationService.cs
BeMusicSeeker/ViewModels/MainWindow/PlaylistUrlDownloadPresentationService.cs
```

作業:

1. HTML / URI 解析は pure static または injectable service に移す。
2. browser / shell open は `IExternalProcessLauncher` 経由にする。
3. download UI progress は ViewModel command から呼ぶ形へ寄せる。

受け入れ条件:

- URL 解析が `MainWindow.cs` から消える。
- Google Drive warning page 解析に単体 test がある。

### Ticket MWUI-1B: score viewer interaction を service 化する

対象 method 例:

- `ConfirmScoreViewerUploadIfNeededAsync`
- `tableContextMenuItemRegisterBMSFileToScoreViwer`
- score viewer registration plan / confirmation flow

新規候補:

```text
ScoreViewerInteractionService
ScoreViewerDialogRequest
ScoreViewerRegistrationCommand
```

受け入れ条件:

- confirmation の表示判断と upload 実行が code-behind に残らない。
- View は dialog coordinator を呼ぶだけになる。

### Ticket MWUI-1C: duplicate group 操作を interaction service へ移す

対象 method 例:

- `WaitForDuplicateListUpdateAndSelect`
- `TrySelectDuplicateGroupByHeader`
- `ExecuteDuplicateFolderMerge`
- `ExecuteDuplicateHashCleanup`
- duplicate folder keyboard / context menu 系

新規候補:

```text
DuplicateGroupInteractionService
DuplicateGroupSelectionRequest
DuplicateFolderMergeCommand
```

受け入れ条件:

- UI selection / focus だけ code-behind に残る。
- domain mutation は ViewModel command / service 経由になる。

## Phase 2: CustomTable event-command bridge

### Ticket MWUI-2A: `CustomTableCommandAdapter` behavior を追加する

目的:

`CustomTableView` の custom event を直接 code-behind で処理するのではなく、ViewModel command へ bridge できるようにする。

新規候補:

```text
BeMusicSeeker/Views/Behaviors/CustomTableCommandAdapter.cs
BeMusicSeeker/ViewModels/MainWindow/CustomTableCommandArgs.cs
```

対応 event 候補:

- `SortRequested`
- `SelectionChanged`
- `RowActivated`
- `RowContextMenuRequested`
- `HeaderContextMenuRequested`
- `CellEditBeginning`
- `CellActionRequested`
- `CellEditEnded`
- `FirstRenderCompleted`

作業:

1. まず `SortRequested` と `RowActivated` の 2 つだけ command 化する。
2. event args を ViewModel に渡す前に、UI 型を含まない DTO へ変換する。
3. code-behind handler は command adapter に置き換える。
4. 既存 handler は互換のため一時的に残してもよいが、production XAML から参照されなくなったら削除する。

受け入れ条件:

- `MainWindow.cs` の table handler 数が減る。
- ViewModel に `CustomTableView` 型が流入しない。
- `CustomTableView` 自体の描画性能に影響しない。

### Ticket MWUI-2B: chart table context menu state を ViewModel 化する

対象:

- `tableContextMenuOpened` 約 566 行
- `tableContextMenuItem*Click` 系
- `DownloadSelectedPlaylistExternalPackagesAsync`
- `DownloadSelectedPlaylistUrlsAsync`

新規候補:

```text
ChartContextMenuViewModel
ChartContextMenuStateBuilder
ChartContextMenuCommandRouter
```

作業:

1. `tableContextMenuOpened` 内の判定を `ChartContextMenuState` DTO に抽出する。
2. XAML の `MenuItem` は state に Binding する。
3. click handler は command に置換する。
4. 動的 submenu は `ItemsSource` に寄せる。
5. external package lookup は既存の `PlaylistExternalPackageLookupService` を維持し、UI 側は選択行・進捗・通知の bridge に寄せる。

受け入れ条件:

- `tableContextMenuOpened` が削除される、または state builder 呼び出しだけになる。
- menu state calculation の単体 test がある。
- external package lookup の既存挙動を `PlaylistExternalPackageLookupServiceTests` で確認できる。
- `MainWindowContextMenuResourceTests` が通る。

### Ticket MWUI-2C: playlist summary / playlist tree context menu を ViewModel 化する

対象:

- playlist root/table/folder context menu open handler
- playlist summary table row context menu
- load / export / bulk edit / property edit commands

新規候補:

```text
PlaylistTreeContextMenuViewModel
PlaylistSummaryContextMenuViewModel
PlaylistTreeCommandRouter
```

受け入れ条件:

- tree selection の UI-specific 処理以外が code-behind から消える。
- playlist command が `PlaylistWorkspaceViewModel` または子 VM に集約される。

## Phase 3: XAML を UI 領域ごとに分割する

### Ticket MWUI-3A: playback panel を UserControl 化する

新規候補:

```text
BeMusicSeeker/Views/MainWindow/PlaybackPanelView.xaml
BeMusicSeeker/Views/MainWindow/PlaybackPanelView.xaml.cs
```

DataContext:

- P0-01 で作る `PlaybackPanelViewModel`

作業:

1. 上部再生パネルの XAML を UserControl へ移す。
2. player host / WindowsFormsHost が必要な箇所は view adapter に閉じる。
3. root XAML には `<mw:PlaybackPanelView DataContext="{Binding PlaybackPanel}" />` の形で配置する。

受け入れ条件:

- 再生パネルの binding が root VM direct でなくなる。
- root XAML の該当行数が減る。

### Ticket MWUI-3B: progress / status overlay を UserControl 化する

DataContext:

- `OperationProgressHubViewModel`

作業:

1. startup / install / playlist sync / maintenance / LR2 sync / folder auto rename progress UI をまとめる。
2. `Cancel` command は各 progress VM へ委譲する。

受け入れ条件:

- progress 表示の XAML が root から独立する。
- progress 状態の binding が root VM pass-through から子 VM へ移る。

### Ticket MWUI-3C: sidebar navigation を UserControl 化する

DataContext:

- `SidebarNavigationViewModel`

作業:

1. playlist tree と library / install / maintenance / play history tree を分割する。
2. tree selected item の event は command adapter / behavior へ寄せる。
3. tree context menu は context menu VM へ移行する。

受け入れ条件:

- `MainWindow.xaml` の TreeView 定義が root から消える。
- `MainWindow.cs` の tree event handler が大幅に減る。

### Ticket MWUI-3D: main workspace を UserControl 化する

DataContext:

- `MainWorkspaceViewModel`

作業:

1. chart table と playlist summary table の表示切替を workspace VM に寄せる。
2. keyword search UI を chart / playlist summary それぞれの VM に寄せる。
3. custom table command adapter を適用する。

受け入れ条件:

- `MainWindow.xaml` の main table / keyword search 定義が root から独立する。
- `MainWindow.cs` の table event handler が command adapter に置き換わる。

## Phase 4: code-behind を薄くする

### Ticket MWUI-4A: `async void` を event entry point だけに制限する

作業:

1. `async void` handler の処理本体を `Task` method に移す。
2. handler では `await ExecuteXxxAsync().Logging(...)` のように入口だけにする。
3. 例外表示は dialog coordinator / logging 経由に統一する。

受け入れ条件:

- `MainWindow.cs` の `async void` は WPF event signature 以外に残らない。
- async 処理本体は test 可能な service / command に移る。

### Ticket MWUI-4B: `MainWindow.cs` の目標行数を 2,000 行以下へ落とす

作業:

1. 残っている method を分類し、View-only 以外を移す。
2. lifecycle / host / view adapter 以外を code-behind から削除する。
3. partial code-behind 分割で満足せず、責務を service / behavior へ移す。

受け入れ条件:

- code-behind は UI host と event bridge だと説明できる。
- `MainWindowContextMenuResourceTests` / `DialogRouteConsolidationTests` が分割後の構造を前提に更新されている。

## 完了目標

- `MainWindow.cs` は Window lifecycle / View adapter / command bridge 中心になる。
- `MainWindow.xaml` は UI 領域ごとの UserControl へ分割される。
- context menu と drag/drop のロジックは ViewModel / presentation service に移る。
- WPF+WinForms 混在箇所が局所化され、.NET 10 移行時の影響範囲が狭くなる。
