# REF-MVP-B1 MainWindow event handler inventory

[PLAN_STATUS](../PLAN_STATUS.md) / [P0-03](../P0-03_MainWindow_UI_MVVM移行計画.md)

最終更新日: 2026-07-07

## 現在の結論

`MainWindow.cs` は WPF event entry point と処理本体がまだ混在している。最初の実装 slice は `tableContextMenuOpened` から UI 型に依存しない state calculation を `ChartContextMenuRequest` / `ChartContextMenuStateBuilder` へ移し、後続の command bridge 化に必要な状態境界を作る。

## XAML event handler 分類

| 分類 | 代表 handler | 次の扱い |
|---|---|---|
| chart table context menu | `tableContextMenuOpened`, `tableContextMenuPlaylistMissingOpened`, `tableContextMenuItem*Click`, `fixEncodingSelectedBMS`, `forceInstallSelectedPendingCharts`, `manualInstallSelectedPendingCharts` | state calculation と mutation request を presentation service / command DTO へ移す |
| play history context menu | `playHistoryContextMenuOpened`, `playHistoryContextMenuItemOpenBMSIRClick`, `playHistoryContextMenuItemRegisterScoreViewerClick`, `playHistoryContextMenuItemCopyHashClick` | chart table と同じ policy DTO へ寄せられるか後続で判断 |
| playlist tree / summary context menu | `treeViewPlaylistRootContextMenuOpend`, `treeViewPlaylistTableContextMenuOpend`, `playlistSummaryContextMenu*` | playlist workspace / dialog coordinator 側へ移す候補 |
| install package tree context menu | `treeViewInstallPendingContextMenu*`, `treeViewInstallPackageContextMenu*`, `treeViewInstalledContextMenu*` | package mutation command と progress boundary を先に整理する |
| table / CustomTable events | `SortRequested`, `RowActivated`, `RowContextMenuRequested`, `HeaderContextMenuRequested`, `CellActionRequested`, `CellEditBeginning`, `CellEditEnded`, `SelectionChanged` | CustomTable command adapter または workspace VM command へ移す候補 |
| drag/drop | `Window_Drop`, `customTablePlaylistSummary_Drop`, `playlistTableDrop`, `playlistTableDragOver`, `playlistTableDragEnter`, `playlistTableDragLeave` | WPF `DragEventArgs` は view adapter に閉じ込め、request DTO へ変換する |
| tree selection | `playlistRootSelect`, `playlistTableSelected`, `directoryFolderSelect`, `rootFolderSelect`, `fullScan*FolderSelect`, `playHistoryPeriodSelect` | navigation / main workspace state へ移す |
| keyword search | `keywordSearchBoxTextChanged`, `keywordSearchBoxPreviewKeyDown`, `keywordSearchSuggestionPreview*`, focus events | search suggestion view model / behavior へ移す |
| playback controls | `gridBMSPlayerControls*`, `sliderPlayer*` | playback panel UserControl / command bridge へ移す |
| window lifecycle / host | `MainWindow_ContentRendered`, `Window_Drop`, `Window_DragOver`, `Window_StateChanged`, `windowsFormsHostIsEnabledChanged`, `webBrowserLoadCompleted` | `MainWindow.cs` に残してよい。ただし本体処理は薄く保つ |

## async void hotspot

| handler | 観測 | 優先度 |
|---|---|---|
| `tableContextMenuItemDeleteInstallPackagesClick` | pending / installed 分岐、選択解決、VM mutation、tree 選択更新が混在 | 高 |
| `treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick` | package mutation と UI 更新が混在 | 高 |
| `treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick` | package mutation と progress gating が混在 | 高 |
| `treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick` | package mutation と confirmation flow が混在 | 高 |
| `customTablePlaylistSummary_Drop` | WPF drop event と playlist reorder workflow が混在 | 中 |
| `playHistoryPeriodSelect` | tree event と play history view request が混在 | 中 |
| `treeViewPlaylistTableContextMenuItemExportTableClick` | dialog / export workflow が混在 | 中 |
| `tableContextMenuItemConvertToAudioFileClick` | context target resolution と external process flow が混在 | 中 |

## tableContextMenuOpened extraction checkpoints

1. 完了: row / section / selected chart targets から作る UI 非依存 state を `ChartContextMenuRequest` / `ChartContextMenuStateBuilder` へ移す。
2. 完了: score viewer / ranking / resource health / install destination の boolean policy を state 側へ追加する。
3. 進行中: click handler 側の selected target resolution を `ChartOperationTargetSelectionRequest` / `ChartOperationTargetSelectionResolver` へ移す。
4. 進行中: selected target resolution を使う click handler 本体を command request DTO / `Task` method へ移す。`tableContextMenuItemDeleteInstallPackagesClick` は `DeleteInstallPackageRecordsRequest` で pending / installed と targets の境界を作り、mutation 分岐を `MainWindowViewModel.DeleteInstallPackageRecords` へ移した。`forceInstallSelectedPendingCharts` / `manualInstallSelectedPendingCharts` は `PendingInstallPackageOperationRequest` を介して `MainWindowViewModel.InstallPendingCharts` へ渡す形にした。`searchInstallDestinationSelectedPendingCharts` / `searchMergeDestinationSelectedPendingCharts` は `PendingInstallDestinationSearchRequest` を介して `MainWindowViewModel.SearchPendingInstallDestination` へ渡す形にした。
   - `tableContextMenuRemoveInstallDestinationClick` の pending install destination clear 側は `PendingInstallDestinationClearRequest` を介して `MainWindowViewModel.ClearPendingInstallDestination` へ渡す形にした。full-scan repair 側は `IRepairInstalledLocationTargetSnapshot` のまま残す。
   - pending install destination cell edit 側は `PendingInstallDestinationEditRequest` を介して `MainWindowViewModel.SetPendingInstallDestination` へ渡す形にした。既存 `PendingInstallDestinationEditTargetSnapshot` API は直接テストと既存互換のため残す。
   - repair installed location search / fix 側は `RepairInstalledLocationRequest` を介して `MainWindowViewModel.SearchCorrectInstallationDirectoryCharts` / `FixInstallationDirectoryCharts` へ渡す形にした。clear 側は `IRepairInstalledLocationTargetSnapshot` のまま残す。
5. 後続: `MenuItem` / `ContextMenu` 更新は UserControl / behavior / command bridge の方針が決まるまで view adapter に残す。

## 注意

- `ContextMenu`, `MenuItem`, `RoutedEventArgs`, `DragEventArgs` を ViewModel / presentation service に渡さない。
- `MainWindowViewModel.MainViewOperationSection` はまだ nested type なので、root pass-through 削除や DataContext 移行と同時に top-level contract 化を検討する。
- source-text test はまだ多い。実装のたびに「削除」ではなく service / child VM 直接テストへの移行候補を見つける。
