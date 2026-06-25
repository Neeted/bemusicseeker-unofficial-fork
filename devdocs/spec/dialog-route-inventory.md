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

These counts are refreshed as implementation units complete. They should monotonically move toward the coordinator route.

| Pattern | Count | Route |
| --- | ---: | --- |
| `DispatcherMessageBox.Show` | 16 | legacy message / confirmation |
| `internal static class DispatcherMessageBox` | 1 | legacy adapter entry point |
| `internal static class UiDialogLegacyAdapter` | 1 | legacy adapter bridge |
| `UiDialogLegacyAdapter.ShowMessageBox` | 2 | legacy adapter bridge |
| `System.Windows.MessageBox.Show` | 4 | emergency route candidate |
| `MessageBox.Show(` | 20 | legacy + emergency aggregate |
| `new ConfirmationMessage` | 40 | Livet confirmation |
| `InteractionMessageAction<FrameworkElement>` | 2 | Livet action component |
| `RaiseInteractionMessageOnUiThread` | 57 | Livet interaction dispatch |
| `CommonOpenFileDialog` | 3 | common picker |
| `OpenFileDialog` | 3 | open picker |
| `SaveFileDialog` | 1 | save picker |
| `FolderBrowserDialog` | 0 | legacy folder picker |
| `.ShowDialog(` | 5 | window / picker modal |
| `ProgressDialog.Execute` | 1 | progress operation |
| `ProgressDialog.Current` | 0 | progress operation static state |

Current progress notes:

- Unit 4 moved direct `ProgressDialog.Execute` call sites in `MainWindow.cs` to `UiDialogCoordinator.RunWithProgressAsync`.
- Unit 4 removed `ProgressDialog.Current` from production code. Remaining `ProgressDialog.Execute` calls are inside the coordinator / display component bridge.
- Unit 5 moved direct code-behind open / save / folder pickers to `UiDialogCoordinator` and made `CommonOpenFileDialogInteractionMessageAction` a coordinator-backed bridge.
- Unit 6 first overlay pass moved MainWindow-embedded overlay open / close visibility changes to `ShowOverlayDialog` / `HideOverlayDialog` and removed the `InitialSetupLanguageDialog` parent panel walk. Route counts are unchanged because this pass does not remove legacy message / picker route classes yet.
- Unit 6 picker bridge pass removed `CommonOpenFileDialogInteractionMessageAction` and the remaining XAML picker action bridge. Setting pickers now use explicit view click handlers and the coordinator directly.
- Unit 6 modal window pass moved direct `ShowDialog()` calls for update, pending delete, LR2 schema uninstall, and play-history preset edit windows to `UiDialogCoordinator.ShowWindowAsync`. Remaining `.ShowDialog(` calls are display component boundaries inside the coordinator / message box.
- Unit 7 view code-behind pass moved `DispatcherMessageBox.Show` calls in `BeMusicSeeker/Views/**/*.cs` to `UiDialogRoute.ShowMessageBox`, a coordinator-backed synchronous helper that does not hide display failures.

## Legacy Source Files

Architecture tests require every production file that still uses a legacy dialog route to be listed here.
When a unit removes the last legacy call from a file, remove the file from this list.
When a new legacy route is added, the test should fail unless the route is intentionally documented here, which should be rare during this consolidation.

| File | Primary routes | Migration unit |
| --- | --- | --- |
| `BeMusicSeeker/App.cs` | emergency `System.Windows.MessageBox.Show` | Unit 7 |
| `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDialogService.cs` | coordinator-backed legacy adapter bridge | Unit 3 |
| `BeMusicSeeker/Models/Utils/DispatcherMessageBox.cs` | coordinator-backed legacy adapter | Unit 7 |
| `BeMusicSeeker/ViewModels/MainWindowViewModel.cs` | Livet confirmation / interaction dispatch | Unit 2, Unit 8 |
| `BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs` | ViewModel `DispatcherMessageBox` | Unit 2 |
| `BeMusicSeeker/Views/ThemedDialogInteractionMessageActions.cs` | Livet dialog action | Unit 2 |
| `BeMusicSeeker/Views/ThemedMessageBox.cs` | message box display component | Unit 1, Unit 7 |
| `BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs` | progress / picker display component bridge | Unit 4, Unit 5 |
| `BeMusicSeeker/Views/Dialogs/UiFilePickerUtilities.cs` | picker display component utility | Unit 5 |
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
- `MainWindow.cs` initially mixed owner-aware message boxes, ownerless picker calls, direct window modals, and all `ProgressDialog.Execute` calls. Unit 4 moved the progress call sites to `UiDialogCoordinator.RunWithProgressAsync`; Unit 5 and Unit 6 still need picker and overlay cleanup.
- `BMSLibrary.cs` uses `ShowOperationDialog` heavily, even though it does not appear in the source-file allow list above because the direct legacy API is behind model service methods. Unit 3 must treat it as a separate operation-notification inventory.
- `SettingDialog.cs` contains owner-aware notifications and ownerless `OpenFileDialog` / `SaveFileDialog` calls while an overlay dialog is active. It should move with picker and overlay units, not as one-off message fixes.
- `DispatcherMessageBox.cs` and `ThemedDialogInteractionMessageActions.cs` are transitional entry points only. They should not gain new call sites.
