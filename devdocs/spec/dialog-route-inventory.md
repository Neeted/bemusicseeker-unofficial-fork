# Dialog Route Inventory

This document freezes the legacy dialog routes that existed when the app-wide dialog consolidation started.
It is intentionally used as both a migration checklist and an architecture-test allow list.

## Scan Date

- Date: 2026-06-26
- Source roots: `BeMusicSeeker/**/*.cs`, `BeMusicSeeker/**/*.xaml`
- Command shape:

```powershell
rg -n "DispatcherMessageBox\.Show|internal static class DispatcherMessageBox|internal static class UiDialogLegacyAdapter|UiDialogLegacyAdapter\.ShowMessageBox|System\.Windows\.MessageBox\.Show|MessageBox\.Show\(|new ConfirmationMessage|InteractionMessageAction<FrameworkElement>|RaiseInteractionMessageOnUiThread|CommonOpenFileDialog|OpenFileDialog|SaveFileDialog|FolderBrowserDialog|\.ShowDialog\(|ProgressDialog\.Execute|ProgressDialog\.Current" BeMusicSeeker -g "*.cs" -g "*.xaml"
```

## Route Counts

These counts are a starting snapshot, not a target. They should monotonically move toward the coordinator route as implementation units are completed.

| Pattern | Count | Route |
| --- | ---: | --- |
| `DispatcherMessageBox.Show` | 111 | legacy message / confirmation |
| `internal static class DispatcherMessageBox` | 1 | legacy adapter entry point |
| `internal static class UiDialogLegacyAdapter` | 1 | legacy adapter bridge |
| `UiDialogLegacyAdapter.ShowMessageBox` | 2 | legacy adapter bridge |
| `System.Windows.MessageBox.Show` | 4 | emergency route candidate |
| `MessageBox.Show(` | 121 | legacy + emergency aggregate |
| `new ConfirmationMessage` | 59 | Livet confirmation |
| `InteractionMessageAction<FrameworkElement>` | 3 | Livet action component |
| `RaiseInteractionMessageOnUiThread` | 76 | Livet interaction dispatch |
| `CommonOpenFileDialog` | 37 | common picker |
| `OpenFileDialog` | 41 | open picker |
| `SaveFileDialog` | 4 | save picker |
| `FolderBrowserDialog` | 0 | legacy folder picker |
| `.ShowDialog(` | 14 | window / picker modal |
| `ProgressDialog.Execute` | 5 | progress operation |
| `ProgressDialog.Current` | 5 | progress operation static state |

## Legacy Source Files

Architecture tests require every production file that still uses a legacy dialog route to be listed here.
When a unit removes the last legacy call from a file, remove the file from this list.
When a new legacy route is added, the test should fail unless the route is intentionally documented here, which should be rare during this consolidation.

| File | Primary routes | Migration unit |
| --- | --- | --- |
| `BeMusicSeeker/App.cs` | emergency `System.Windows.MessageBox.Show` | Unit 7 |
| `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDialogService.cs` | coordinator-backed legacy adapter bridge | Unit 3 |
| `BeMusicSeeker/Models/Utils/DispatcherMessageBox.cs` | coordinator-backed legacy adapter | Unit 7 |
| `BeMusicSeeker/Models/Utils/TaskEx.cs` | utility `DispatcherMessageBox` | Unit 3 |
| `BeMusicSeeker/ViewModels/MainWindowViewModel.cs` | Livet confirmation / interaction dispatch | Unit 2, Unit 8 |
| `BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs` | ViewModel `DispatcherMessageBox` | Unit 2 |
| `BeMusicSeeker/Views/CommonOpenFileDialogInteractionMessageAction.cs` | common picker action | Unit 5 |
| `BeMusicSeeker/Views/LoadPlaylistURIDialog.cs` | overlay message / open picker | Unit 5, Unit 6 |
| `BeMusicSeeker/Views/MainWindow.cs` | message / confirmation / picker / progress / modal | Unit 2, Unit 4, Unit 5, Unit 6, Unit 8 |
| `BeMusicSeeker/Views/MainWindow.xaml` | Livet trigger wiring / picker actions | Unit 2, Unit 5, Unit 6 |
| `BeMusicSeeker/Views/PlayHistoryFolderDisplayPresetEditDialog.cs` | modal window result | Unit 6 |
| `BeMusicSeeker/Views/PlaylistPropertyDialog.cs` | overlay dialog result | Unit 6 |
| `BeMusicSeeker/Views/PlaylistSummaryBulkEditDialog.cs` | overlay dialog result | Unit 6 |
| `BeMusicSeeker/Views/SettingDialog.cs` | message / picker / modal | Unit 2, Unit 5, Unit 6 |
| `BeMusicSeeker/Views/SettingDialog.xaml` | Livet picker actions | Unit 5 |
| `BeMusicSeeker/Views/ThemedDialogInteractionMessageActions.cs` | Livet dialog action | Unit 2 |
| `BeMusicSeeker/Views/ThemedMessageBox.cs` | message box display component | Unit 1, Unit 7 |
| `BeMusicSeeker/Views/Dialogs/UiDialogLegacyAdapter.cs` | coordinator-backed legacy adapter bridge | Unit 7 |

## Route Classification Axes

Each call site should be classified before replacement.

| Axis | Values |
| --- | --- |
| Intent | notification, confirmation, picker, progress, overlay, emergency |
| User interaction | non-interactive notification, interactive decision, file-system selection, long-running operation |
| Owner source | explicit owner, associated object owner, active window, main window fallback, no owner |
| Thread boundary | UI thread, dispatcher invoke, background worker, progress worker |
| Failure representation | explicit result, `bool?`, `MessageBoxResult`, `ConfirmationMessage.Response`, hidden fallback |
| Migration target | `ShowMessageAsync`, `ConfirmAsync`, picker request, `RunWithProgressAsync`, `DialogHost`, `EmergencyDialog` |

## Initial Hotspots

- `MainWindowViewModel.cs` contains most `ConfirmationMessage` and `RaiseInteractionMessageOnUiThread` calls. This is the main target for Unit 2 and Unit 8.
- `MainWindow.cs` mixes owner-aware message boxes, ownerless picker calls, direct window modals, and all `ProgressDialog.Execute` calls. This is the main target for Unit 4 and Unit 5.
- `BMSLibrary.cs` uses `ShowOperationDialog` heavily, even though it does not appear in the source-file allow list above because the direct legacy API is behind model service methods. Unit 3 must treat it as a separate operation-notification inventory.
- `SettingDialog.cs` contains owner-aware notifications and ownerless `OpenFileDialog` / `SaveFileDialog` calls while an overlay dialog is active. It should move with picker and overlay units, not as one-off message fixes.
- `DispatcherMessageBox.cs` and `ThemedDialogInteractionMessageActions.cs` are transitional entry points only. They should not gain new call sites.
