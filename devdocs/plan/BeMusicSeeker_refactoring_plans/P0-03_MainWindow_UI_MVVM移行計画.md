# P0-03 MainWindow UI / code-behind MVVM 移行計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

## 目的

`BeMusicSeeker/Views/MainWindow.cs` は 10,289 行、`MainWindow.xaml` は 2,510 行ある。`MainWindowViewModel` を整理しても、code-behind と巨大 XAML が UI logic を抱えたままだと MVVM としての責務分離は完了しない。

`MainWindow.cs` は WPF lifecycle / view-host / WPF event entry point / UI 型 adapter / command invocation bridge に限定する。処理本体を持つ `async void` と巨大 event handler は presentation service / command request DTO / ViewModel command へ移す。

## 現状観測

| 対象 | 規模 / 問題 |
|---|---|
| `MainWindow.cs` | 10,289 行。event handler、context menu 構築、drag & drop、URL download、external package lookup、duplicate merge、score viewer upload、external player host が同居 |
| `MainWindow.xaml` | 2,510 行。上部再生パネル、tree、table、context menu、progress UI が同居 |
| `async void` | production の `MainWindow.cs` に 74 箇所程度 |
| 最大 method | `tableContextMenuOpened` 約 566 行。context menu state calculation と UI 操作が混在 |
| WPF + WinForms | `UseWPF=True`, `UseWindowsForms=True`。player panel / WindowsFormsHost 周辺は .NET 10 移行時の注意点 |

## Active Ticket: `REF-MVP-B1`

### MainWindow event handler inventory and first command bridge

目的:

- `MainWindow.cs` の巨大 event handler と `async void` を分類する。
- `tableContextMenuOpened` の state calculation を presentation service / command args DTO へ移す最初の slice を実装する。
- code-behind は UI 型の値を変換し、command / presentation service を呼ぶだけに近づける。

主対象:

- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`
- `BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs`
- 新規候補: `BeMusicSeeker/ViewModels/MainWindow/ChartContextMenuStateBuilder.cs`
- 新規候補: `BeMusicSeeker/ViewModels/MainWindow/ChartContextMenuRequest.cs`
- inventory: `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/REF-MVP-B1_mainwindow_event_handlers.md`

subtasks:

1. `MainWindow.xaml` の `Click=`, `Opened=`, `Drop=`, `Preview*`, `SelectionChanged=` と対応する `MainWindow.cs` handler を inventory 化する。
2. `async void` を WPF event entry point / 処理本体あり / command bridge 候補へ分類する。
3. `tableContextMenuOpened` から、UI 型に依存しない state calculation を `ChartContextMenuStateBuilder` などへ移す。
4. code-behind は UI control から必要な値を抽出し、request DTO を作って command / presentation service を呼ぶ形へ寄せる。

完了条件:

- `MainWindow.cs` の event handler inventory が devdocs にある。
- 最初の event handler slice が ViewModel command / presentation service へ移っている。
- code-behind が UI 型の値を変換し、command を呼ぶだけに近づいている。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

## Target Architecture

```text
MainWindow.xaml / MainWindow.cs           // shell view, lifecycle, view-host, command bridge
├─ Views/MainWindow/PlaybackPanelView.xaml
├─ Views/MainWindow/SidebarNavigationView.xaml
├─ Views/MainWindow/MainWorkspaceView.xaml
├─ Views/MainWindow/ProgressStatusView.xaml
├─ Views/Behaviors/CustomTableCommandAdapter.cs
├─ ViewModels/MainWindow/ChartContextMenuViewModel.cs
├─ ViewModels/MainWindow/PlaylistTreeContextMenuViewModel.cs
├─ ViewModels/MainWindow/PlaybackPanelViewModel.cs
├─ ViewModels/MainWindow/MainWorkspaceViewModel.cs
└─ Views/Dialogs/UiDialogCoordinator.cs
```

MainWindow code-behind に残すもの:

- `InitializeComponent()` と DataContext 初期化。
- Window lifecycle。
- WPF control / WinForms host / OS window handle を直接触る必要がある薄い adapter。
- Event-to-command bridge が未対応の WPF event entry point。
- View 固有の focus / selection / scroll / hit test の最小処理。

MainWindow code-behind から出すもの:

- context menu item の有効/無効/表示判定。
- URL 解決、Google Drive warning page 解決、download 実行。
- duplicate folder merge / cleanup の domain orchestration。
- score viewer registration / upload confirmation flow。
- playlist table drag/drop の workflow。
- install pending tree の mutation call。
- keyword search suggestion の UI 状態算出。
- user-facing dialog 表示判断。

## Constraints

- release freeze を破らない。
- WPF `Control` / `RoutedEventArgs` / `DragEventArgs` を ViewModel に渡さない。
- `ICommand` 化できるものは ViewModel command へ寄せる。
- `async void` は WPF event handler 入口だけに残し、処理本体は `Task` method / service / command に逃がす。
- user-facing text を増やす場合は resource / lang JSON の parity を守る。
- `MenuItem` / `ContextMenu` は WPF + WinForms ambiguity を避けるため、必要に応じて `System.Windows.Controls.MenuItem` などに明示する。

## Guardrail

`MainWindow.cs` は最終 5,000 行以下を目標にする。超過する場合は `PLAN_STATUS.md` に、残す責務、残す理由、次の extraction 候補、サブエージェントレビュー結果を記録する。

## 後続候補

`REF-MVP-B1` 完了後に、次を workflow / responsibility boundary 単位で選ぶ。

- chart table context menu command bridge の残り。
- duplicate group interaction service。
- external URL / Google Drive helper service。
- score viewer interaction service。
- CustomTable command adapter。
- playback / progress / sidebar / main workspace の UserControl 化。
