# アプリ全体のダイアログ経路整理計画

## Summary

この資料は、BeMusicSeeker のダイアログ表示経路をアプリ全体で整理するための実装計画である。

現状は `DispatcherMessageBox.Show`、Livet `InteractionMessage` / `ConfirmationMessage`、
`CommonOpenFileDialogInteractionMessageAction`、`ProgressDialog.Execute`、各 View の `ShowDialog()` 直呼び、
`System.Windows.MessageBox.Show` 直呼びが混在している。個別不具合に対して最小差分の修正を重ねた結果、
ある経路では表示されるが別経路では裏に回る、表示されない、応答待ちのまま止まる、という回帰を繰り返しやすい構造になっている。

この計画では、中間段階ごとのリリース可能性を優先しない。同じ系統のダイアログは横断的に一括置換し、
一時的に差分が大きくなっても、最終的に owner、dispatcher、modal state、戻り値、失敗時挙動が一貫する設計へ収束させる。

## Progress

この表を実装単位ごとの消し込み表として使う。各 unit 完了時に、実装内容、検証、レビュー結果、次 unit への計画修正をここへ反映する。

| Unit | Status | Progress Notes |
| --- | --- | --- |
| Unit 0: Inventory Freeze | Completed | `devdocs/spec/dialog-route-inventory.md` を追加。legacy route source file を inventory と一致させる architecture guard を追加。 |
| Unit 1: UiDialogCoordinator Skeleton | Completed | request / result / owner resolver / async message route skeleton を追加。`ThemedMessageBox` は close と button selection を区別できる表示部品に拡張。 |
| Unit 2: Message / Confirmation Route Replacement | Completed | Legacy entrypoint `DispatcherMessageBox` と Livet `Themed*InteractionMessageAction` を coordinator-backed に置換済み。譜面ビューア登録確認、設定系、playlist sync、app schema、temporary install playback の decision confirmation は coordinator route へ置換済み。残る OK notification の Livet route は Unit 3 の model boundary / operation result 整理で扱う。 |
| Unit 3: Model Dialog Boundary Replacement | Completed | `BmsLibraryDialogService` は `DispatcherMessageBox` 依存を外し、coordinator-backed legacy adapter bridge に集約済み。`Backup.SaveBackups` は warning / failure result 化し、UI await 後に coordinator route で flush する形へ移行済み。`BMSPlaylist` の recommended table / custom folder notification は `OperationNotificationScope` へ移し、UI operation 境界で coordinator route flush する形へ移行済み。`uBMplay` の起動失敗は Model 内 dialog ではなく caller 例外へ移行済み。`FastDirectoryEnumerator` のアクセス不可 directory skip は UI dialog 直呼びを廃止し、skip 事実を file logger に残す形へ移行済み。`TaskEx` は task fault 分類ログだけを担う utility に整理し、continuation 内 dialog と network report consent prompt を廃止済み。 |
| Unit 4: Progress Dialog Replacement | Completed | `MainWindow.cs` の progress 操作は `UiDialogCoordinator.RunWithProgressAsync` へ移行済み。`ProgressDialog.Current` static state は廃止済み。`ProgressDialog` 表示中は coordinator-managed active modal として owner resolver の最優先に登録し、progress 中に発生する message / confirmation は progress dialog owner へ寄る形に整理済み。 |
| Unit 5: File Picker Route Replacement | In progress | `UiFilePickerRequest` / `UiFolderPickerRequest` / `UiSaveFilePickerRequest` と typed result を実装済み。`MainWindow.cs`、`SettingDialog.cs`、`LoadPlaylistURIDialog.cs` の direct open / save / folder picker は coordinator route へ移行済み。`CommonOpenFileDialogInteractionMessageAction` は coordinator-backed bridge 化済み。残る作業は XAML action route の撤去可否と overlay owner / DialogHost 連携を Unit 6 境界に合わせて整理すること。 |
| Unit 6: Overlay DialogHost | Pending | MainWindow overlay 表示を coordinator managed host に集約する。 |
| Unit 7: DispatcherMessageBox Retirement | Pending | 通常 route から legacy adapter / standard MessageBox fallback を削除する。 |
| Unit 8: Score Viewer Registration Rework | Pending | 譜面ビューア登録確認を preflight confirmation と background upload に分離する。 |

## Verification Log

| Unit | Check | Result | Notes |
| --- | --- | --- | --- |
| Unit 0/1 | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 0/1 | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter DialogRouteConsolidationTests /p:Configuration=Release --no-restore` | Passed | inventory guard and result status tests passed. |
| Unit 0/1 | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 0/1 | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 0/1 | `dotnet test BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Blocked by unrelated tests | Dialog tests passed, but unrelated rendering pixel tests `CustomTableTextLayoutCacheTests.GetOrCreate_AppliesTextRunForegrounds` and `CustomTablePhase6CacheTests.CustomTableView_RendersCellTextRunsAndPreservesRunsForSelection` fail in this environment. Earlier full run also showed `AppSchemaPreflightServiceTests.Inspect_PathOverload_DoesNotWaitForLr2SongDbExtendedMonitorLock` once; it passed when rerun individually. |
| Unit 0/1 | sub-agent static review | Passed | Initial High findings were fixed. Final review returned重大な指摘なし. |
| Unit 2 entrypoints | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 2 entrypoints | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 94 tests passed. |
| Unit 2 entrypoints | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 2 entrypoints | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 2 entrypoints | sub-agent static review | Passed | Initial High finding for `ClosedByUser` / `YesNo` close mapping was fixed. Final review returned重大な指摘なし. |
| Unit 2 score viewer | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 2 score viewer | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 95 tests passed. |
| Unit 2 score viewer | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 2 score viewer | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 2 score viewer | sub-agent static review | Passed | Initial High finding for dialog failures being caught as upload failures was fixed. Final review returned重大な指摘なし. |
| Unit 2 settings decisions | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 2 settings decisions | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 95 tests passed. |
| Unit 2 settings decisions | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 2 settings decisions | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 2 settings decisions | sub-agent static review | Passed | Final review returned重大な指摘なし. |
| Unit 2 remaining decisions | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 2 remaining decisions | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests\|PlaylistSummaryBulkEditTests\|MainWindowViewModelAppSchemaRepairTests" /p:Configuration=Release --no-restore` | Passed | 112 tests passed. |
| Unit 2 remaining decisions | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 2 remaining decisions | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 2 remaining decisions | sub-agent static review | Passed | Final review returned重大な指摘なし. |
| Unit 3 entry boundary | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 entry boundary | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|BmsLibraryDialogRoutingTests\|BmsLibraryMutationBoundaryTests" /p:Configuration=Release --no-restore` | Passed | 16 tests passed. |
| Unit 3 entry boundary | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 entry boundary | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 entry boundary | sub-agent static review | Passed | Initial Medium guard finding was fixed. Final review returned重大な指摘なし. |
| Unit 3 backup result | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 backup result | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "BackupTests\|DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 99 tests passed. |
| Unit 3 backup result | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 backup result | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 backup result | sub-agent static review | Passed | Initial Medium findings for Task fault leakage and warning loss were fixed. Final review returned重大な指摘なし. |
| Unit 3 BMSPlaylist notifications | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 BMSPlaylist notifications | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|BmsPlaylistUpdateTests\|CustomFolderOutputBaseRegistryTests\|ExternalPlaylistImportQueueTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 173 tests passed. |
| Unit 3 BMSPlaylist notifications | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 BMSPlaylist notifications | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 BMSPlaylist notifications | sub-agent static review | Passed | Initial High/Medium findings for missing playlist-property scopes, import queue flush placement, and output-directory invalid notification were fixed. Final review returned重大な指摘なし. |
| Unit 3 uBMplay startup failure | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 uBMplay startup failure | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 97 tests passed. |
| Unit 3 uBMplay startup failure | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 uBMplay startup failure | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 uBMplay startup failure | sub-agent static review | Passed | Initial High findings for success-on-startup-failure paths were fixed. Final review returned重大な指摘なし. |
| Unit 3 FastDirectoryEnumerator skips | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 FastDirectoryEnumerator skips | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests" /p:Configuration=Release --no-restore` | Passed | 6 tests passed. |
| Unit 3 FastDirectoryEnumerator skips | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 FastDirectoryEnumerator skips | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 FastDirectoryEnumerator skips | sub-agent static review | Passed | Final review returned重大な指摘なし. |
| Unit 3 TaskEx fault logging | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 3 TaskEx fault logging | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests" /p:Configuration=Release --no-restore` | Passed | 6 tests passed. |
| Unit 3 TaskEx fault logging | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 3 TaskEx fault logging | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 3 TaskEx fault logging | sub-agent static review | Passed | Final review returned重大な指摘なし. |
| Unit 4 progress route entry | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 4 progress route entry | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests" /p:Configuration=Release --no-restore` | Passed | 7 tests passed. |
| Unit 4 progress route entry | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 4 progress route entry | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 4 progress route entry | sub-agent static review | Passed | Initial High finding for missing task observation and Low inventory note were fixed. Final review returned重大な指摘なし. |
| Unit 4 active modal owner | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 4 active modal owner | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests" /p:Configuration=Release --no-restore` | Passed | 7 tests passed. |
| Unit 4 active modal owner | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 4 active modal owner | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 4 active modal owner | sub-agent static review | Passed | Initial High finding for pre-load modal stack removal and Low guard weakness were fixed. Final review returned重大な指摘なし. |
| Unit 5 picker route entry | `dotnet build BeMusicSeeker.sln /p:Configuration=Release --no-restore` | Passed | 0 warnings, 0 errors. |
| Unit 5 picker route entry | `dotnet test BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj --filter "DialogRouteConsolidationTests\|MainWindowContextMenuResourceTests" /p:Configuration=Release --no-restore` | Passed | 99 tests passed. |
| Unit 5 picker route entry | `dotnet format whitespace BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal` | Passed | no whitespace changes required. |
| Unit 5 picker route entry | `dotnet roslynator analyze BeMusicSeeker.sln --properties Configuration=Release --severity-level warning --verbosity minimal` | Passed | 0 diagnostics. |
| Unit 5 picker route entry | sub-agent static review | Passed | Initial High finding for interaction-action display failures being treated as cancel was fixed. Final review had Low inventory freshness note, now reflected. |

## Goals

- 通常運用中のダイアログ表示経路を `IUiDialogService` / `UiDialogCoordinator` に集約する。
- owner 解決を一箇所へ閉じ込め、`MainWindow` 固定ではなく現在の active modal / overlay を優先する。
- background worker / `Task.Run` から同期的に UI dialog を出す経路を原則廃止する。
- 確認が必要な処理は処理開始前の preflight で解決し、処理中は UI 応答待ちを発生させない。
- 警告・完了通知は処理結果として収集し、UI 側で処理完了後に flush する。
- `ConfirmationMessage.Response == null` を「ユーザーキャンセル」と見なすような曖昧な失敗をなくす。
- owner なし fallback や標準 `MessageBox.Show` fallback で失敗を隠さない。
- file picker / folder picker / save dialog / progress dialog も同じ coordinator の管理対象にする。
- 起動前・未処理例外など、通常 UI coordinator を使えない経路だけを明示的な emergency route として残す。

## Non Goals

- 各フェーズを常にリリース可能な小差分に保つこと。
- 既存 API の呼び出し箇所を最小限の変更だけで温存すること。
- `ThemedMessageBox` の見た目だけを差し替えて問題を解決した扱いにすること。
- Model 層から任意のタイミングで UI dialog を出せる現状仕様を維持すること。
- dialog 表示失敗時にデフォルト OK / Yes を返して処理継続を優先すること。

## Current State

2026-06 時点の静的調査では、主な利用規模は次の通り。

| 経路 | 概数 | 備考 |
| --- | ---: | --- |
| `DispatcherMessageBox.Show` | 111 | View では owner 付きが多いが、Model / ViewModel / utility では owner なしが多い。 |
| `new ConfirmationMessage` | 59 | `MainWindow.xaml` の Livet trigger に依存する。 |
| `RaiseInteractionMessageOnUiThread` | 76 | 同期 `Dispatcher.Invoke` で `Messenger.Raise` する。表示成否は戻らない。 |
| `CommonOpenFileDialog` | 37 | Interaction action と code-behind 直呼びが混在する。 |
| `ShowDialog()` 直呼び | 15 | owner 指定あり / なしが混在する。 |
| `ProgressDialog.Execute` | 5 | owner はあるが、worker 内 dialog と競合しやすい。 |

主な実装箇所:

- `BeMusicSeeker/Models/Utils/DispatcherMessageBox.cs`
- `BeMusicSeeker/Views/ThemedMessageBox.cs`
- `BeMusicSeeker/Views/ThemedDialogInteractionMessageActions.cs`
- `BeMusicSeeker/Views/CommonOpenFileDialogInteractionMessageAction.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/Views/SettingDialog.cs`
- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDialogService.cs`
- `Parago/Windows/ProgressDialog.cs`

## Dialog Route Inventory

整理対象を次の route に分類する。

| Route | 代表例 | 現状の問題 | 最終方針 |
| --- | --- | --- | --- |
| Message box | `DispatcherMessageBox.Show` | owner なし fallback、Model 直呼び、標準 `MessageBox.Show` fallback | `UiDialogCoordinator.ShowMessageAsync` へ集約 |
| Confirmation | `ConfirmationMessage`, `MessageBoxButton.YesNo`, `OKCancel` | trigger 未接続や表示失敗が cancel と区別できない | `UiDialogCoordinator.ConfirmAsync` へ集約し表示失敗を明示 |
| Interaction overlay | `InitialSetupLanguageDialog`, `SettingDialog`, `LoadPlaylistURIDialog` | 個別 `Visibility` と `ZIndex` に依存し排他制御がない | `DialogHost` / `OverlayDialogCoordinator` で単一管理 |
| File / folder picker | `CommonOpenFileDialog`, `OpenFileDialog`, `SaveFileDialog` | owner なし `ShowDialog()` と action 経由が混在 | `PickFileAsync` / `PickFolderAsync` / `PickSaveFileAsync` に集約 |
| Progress dialog | `ProgressDialog.Execute` | static `ProgressDialog.Current`、worker 内 dialog と modal 衝突 | `RunWithProgressAsync` と operation context に置換 |
| Model operation notification | `IBmsLibraryDialogService`, `OperationDialogScope` | Model から UI 表示できる。flush が worker / modal 中に起こり得る | UI に結果を返して完了後に表示 |
| Emergency dialog | `App.cs` の多重起動 / 未処理例外 | 通常 coordinator を使えない | `EmergencyDialog` として分離し例外扱いで温存 |

## Problems

### Owner 解決が統一されていない

`DispatcherMessageBox.Show(owner, ...)` は owner があればそれを使うが、owner なしでは `Application.Current.MainWindow` に寄る。
Livet の `ThemedConfirmationDialogInteractionMessageAction` は `Window.GetWindow(AssociatedObject)` を使うため、通常は `MainWindow` owner になる。

しかし、設定ダイアログ、進捗ダイアログ、独自 overlay が前面にある状態では、`MainWindow` owner の dialog は現在の操作対象と一致しない。
この mismatch が、裏に回る、入力できない、処理が応答待ちに見える、という症状を生む。

### ProgressDialog と worker 内 dialog が競合する

`ProgressDialog.Execute(this, ...)` は `MainWindow` owner の modal を表示しながら worker を走らせる。
worker 内で `RaiseInteractionMessageOnUiThread` や `DispatcherMessageBox.Show` が呼ばれると、別の modal dialog を UI thread に同期表示しようとする。

このとき owner が ProgressDialog ではなく MainWindow になると、確認 dialog が ProgressDialog の裏に回り、worker は確認応答待ち、ProgressDialog は worker 終了待ちになる。

### `RaiseInteractionMessageOnUiThread` が表示成否を表現しない

`RaiseInteractionMessageOnUiThread` は dispatcher shutdown 中には skip するが、呼び出し側には「表示されなかった」という結果を返さない。
`ConfirmationMessage.Response` が `null` の場合、未表示、trigger 未接続、MessageKey typo、ユーザーが閉じた、の区別ができない。

### catch-all fallback が失敗を隠す

`DispatcherMessageBox` は広い `catch` で標準 `MessageBox.Show` に落ちる。
これは theme / owner / dispatcher / default result の前提を変え、元の失敗原因を隠す。

「失敗させない」ための fallback が、実際には「別挙動で表示されたり表示されなかったりする」原因になっている。

### Model / utility から UI 表示できる

`BMSLibrary`、`BMSPlaylist`、`FastDirectoryEnumerator`、`TaskEx`、`uBMplay` などから直接または `IBmsLibraryDialogService` 経由で dialog が表示される。

Model が現在の UI modal state を知らないまま表示要求を投げるため、owner と処理境界の整合性を保てない。

### Overlay dialog が個別制御されている

`InitialSetupLanguageDialog`、`SettingDialog`、`LoadPlaylistURIDialog`、`PlaylistPropertyDialog`、`PlaylistSummaryBulkEditDialog` は MainWindow 上に重ねる UserControl だが、共通の排他・focus・戻り値管理がない。

`InitialSetupLanguageDialog.xaml.cs` のように親 Panel の children を探索して別 dialog を操作する実装もあり、配置変更や再利用に弱い。

## Target Design

### UiDialogCoordinator

通常 UI の dialog 表示は `UiDialogCoordinator` に集約する。

責務:

- UI dispatcher への marshal。
- 現在有効な owner の解決。
- active modal stack の管理。
- dialog request の直列化。
- progress operation 中の dialog 禁止 / queue。
- closing / shutdown 中の明示的な失敗。
- 表示成功、ユーザー応答、表示失敗の区別。
- dialog request / result の logging。

想定 API:

```csharp
internal interface IUiDialogService
{
    Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default);
    Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default);
    Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default);
    Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default);
    Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default);
    Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default);
}
```

同期 API が必要な legacy call site には一時的な adapter を用意してよいが、最終形では UI thread を長時間塞ぐ同期 dialog を標準 route にしない。

### Owner Resolver

owner 解決は次の優先順位にする。

1. 現在の coordinator managed active modal window。
2. 現在の coordinator managed overlay host。
3. `Application.Current.Windows` の active window。
4. `Application.Current.MainWindow`。
5. 通常 route では失敗。emergency route だけ owner なし許可。

owner なしで標準 `MessageBox.Show` へ静かに落とす fallback は通常 route から廃止する。

### Dialog Result

confirmation の結果は `bool?` だけにしない。

必要な区別:

- `Accepted`
- `Rejected`
- `CancelledByUser`
- `ClosedByUser`
- `NotShown`
- `AppClosing`
- `OwnerUnavailable`
- `DispatcherUnavailable`
- `Failed`

`NotShown` / `OwnerUnavailable` / `Failed` をユーザーキャンセルと同一視しない。
処理の意味が変わる default fallback は入れない。

### DialogHost

MainWindow 上の overlay dialog は `DialogHost` に集約する。

対象:

- initial setup language dialog
- setting dialog
- load playlist URI dialog
- playlist property dialog
- playlist summary bulk edit dialog
- update available dialog など、window / overlay どちらに寄せるか再評価するもの

方針:

- 表示中 dialog は coordinator が一元管理する。
- 同時表示は禁止または明示 stack として扱う。
- focus restore、escape / cancel、closing 中の close、結果返却を共通化する。
- `MessageKey` 文字列と `Visibility` 直接操作を減らす。
- `Parent.Children` 探索を廃止する。

### Progress Operation

`ProgressDialog.Current` static を使う設計から、operation context を渡す設計へ移行する。

方針:

- progress operation 内で interactive confirmation を出さない。
- 必要な確認は開始前 preflight で解決する。
- OK warning / error summary は operation result として蓄積し、progress close 後に coordinator で表示する。
- どうしても operation 中に通知が必要なものは progress UI 内の status / log に出し、modal dialog にしない。

### Model Boundary

Model 層は UI を直接表示しない。

許可するもの:

- `IBmsLibraryDialogService` のような抽象へ「表示要求」を返す transitional adapter。
- 最終的には `OperationNotification` / `OperationPromptRequest` / `OperationResult` として ViewModel に返す。

禁止するもの:

- Model / utility から `DispatcherMessageBox.Show` を直接呼ぶ。
- Model mutation 中に UI 応答待ちする。
- `MessageBoxButton.YesNo` のような interactive prompt を mutation 境界内で発生させる。

`OperationDialogScope` は、最終的には「OK notification の蓄積」ではなく「operation result の message collection」へ寄せる。
flush は UI 側で、progress close 後または mutation boundary 終了後に行う。

### Emergency Route

起動前、多重起動、未処理例外、dispatcher 崩壊、shutdown 中の致命的通知は通常 coordinator から分離する。

方針:

- `EmergencyDialog.Show(...)` のような明示 API に限定する。
- theme / owner / async / coordinator の保証は持たない。
- 通常処理から emergency route を呼ばない。
- emergency route の存在を、通常 route の fallback として使わない。

## Implementation Units

この整理は中間リリース可能性を前提にしない。各 unit は、同じ系統の call site を横断的に置換する。

### Unit 0: Inventory Freeze

- `rg` ベースで dialog call site の一覧を生成する。
- 各 call site に route、owner 有無、UI thread 前提、戻り値有無、interactive / notification / picker / progress を付与する。
- 生成結果を `devdocs/spec` または同 plan の appendix に保存する。
- 以後の置換では inventory を消し込み表として使う。

### Unit 1: UiDialogCoordinator Skeleton

- `IUiDialogService` と request / result 型を追加する。
- owner resolver を実装する。
- closing / shutdown / owner unavailable を result として表現する。
- `ThemedMessageBox` は表示部品として残し、coordinator から呼ぶ。
- `DispatcherMessageBox` は coordinator adapter へ段階移行するが、新規 call site では使わない。

### Unit 2: Message / Confirmation Route Replacement

- `RaiseInteractionMessageOnUiThread(new ConfirmationMessage(...))` の interactive confirmation を横断置換する。
- `DispatcherMessageBox.Show(... OKCancel / YesNo ...)` の interactive confirmation を横断置換する。
- `ConfirmationMessage.Response == null` 判定を廃止し、`UiDialogResult` の明示 status で分岐する。
- `MessageKey = "ConfirmationDialog"` 依存を減らす。
- 置換対象の設定確認、譜面ビューア登録確認、schema repair 確認、playlist property 確認などを同じ route に寄せる。

### Unit 3: Model Dialog Boundary Replacement

- `BmsLibraryDialogService` の既定実装を coordinator backed adapter に置き換える。
- `BMSLibrary` の interactive prompt は mutation 前 preflight に移す。
- mutation 中の OK warning / error は operation result collection として返す。
- `OperationDialogScope.Flush()` を UI 側 await 後の flush に変更する。
- `TaskEx`、`FastDirectoryEnumerator`、`BMSPlaylist`、`uBMplay` など utility / model 直呼びを廃止する。

### Unit 4: Progress Dialog Replacement

- `ProgressDialog.Execute` call site を `RunWithProgressAsync` へ置換する。
- `ProgressDialog.Current` static 依存をなくす。
- progress worker から dialog を出す経路を inventory で検出し、preflight / post-result に移す。
- progress 中の cancellation、failure summary、warning summary を result 型で扱う。

### Unit 5: File Picker Route Replacement

- `CommonOpenFileDialogInteractionMessageAction` 経由と code-behind 直呼びを coordinator に寄せる。
- owner なし `ShowDialog()` を全廃する。
- `OpenFileDialog` / `SaveFileDialog` / `CommonOpenFileDialog` の初期 directory、filter、multi-select、default extension を request 型に統一する。
- setting dialog、playlist URI import、backup / restore、audio conversion destination、export path を同じ picker route へ移す。

### Unit 6: Overlay DialogHost

- `MainWindow.xaml` の overlay UserControl 表示を `DialogHost` に集約する。
- `InitialSetupLanguageDialog`、`SettingDialog`、`LoadPlaylistURIDialog`、`PlaylistPropertyDialog`、`PlaylistSummaryBulkEditDialog` の表示/非表示を coordinator managed request にする。
- `InitialSetupLanguageDialog.xaml.cs` の親 panel 探索を廃止する。
- overlay 表示中の message box owner が overlay / active modal を指すようにする。

### Unit 7: DispatcherMessageBox Retirement

- `DispatcherMessageBox` は legacy adapter として残すか、通常 route から完全削除するかを決定する。
- 残す場合も catch-all fallback を廃止し、owner なし通常呼び出しを warning / failure にする。
- `System.Windows.MessageBox.Show` 直呼びは `App.cs` の emergency route 以外から削除する。
- 新規利用を防ぐ architecture test を追加する。

### Unit 8: Score Viewer Registration Rework

- 譜面ビューア登録確認を coordinator route に移す。
- status API / upload は confirmation 後の background operation にし、確認 dialog を background worker 内で出さない。
- `Path` なし hash-only row は「登録」ではなく「viewer URL open」として UI 表示文言を分ける。
- status API 失敗、upload 失敗、未登録、登録済み、hash-only を結果型で区別し、失敗を catch-only ログで隠さない。

## Verification Policy

この計画では、中間 unit ごとにリリースできる状態を保つことは目的ではない。
ただし、各 unit 完了時には次の観点で検証する。

- dialog inventory の消し込みが進んでいること。
- owner なし通常 dialog が残っていないこと。
- interactive confirmation が background worker / progress operation 中に発生しないこと。
- `ConfirmationMessage.Response == null` に依存する分岐が残っていないこと。
- `DispatcherMessageBox` catch-all fallback に依存する route が残っていないこと。
- picker の `ShowDialog()` は必ず owner を持つこと。
- overlay dialog 同時表示のルールが test で固定されていること。

推奨テスト:

- static architecture tests:
  - `DispatcherMessageBox.Show(` の新規利用禁止または許可リスト化。
  - `System.Windows.MessageBox.Show` は `App.cs` / emergency route だけ許可。
  - `new ConfirmationMessage` は coordinator 内または削除済み。
  - `ShowDialog()` owner なし呼び出し禁止。
  - `ProgressDialog.Current` 参照禁止。
- unit tests:
  - owner resolver の優先順位。
  - progress operation 中の dialog queue / rejection。
  - `NotShown` / `OwnerUnavailable` / `AppClosing` が cancel と区別されること。
  - operation result message collection の flush 順序。
- UI smoke tests:
  - 設定 dialog 表示中の warning / confirmation。
  - progress dialog 表示中に warning が発生する operation。
  - 初回起動 validation failure。
  - 譜面ビューア未登録 upload confirmation。
  - file picker owner / foreground behavior。

## Acceptance Criteria

- 通常 UI の message / confirmation / picker / progress / overlay が `UiDialogCoordinator` 経由に統一されている。
- owner 解決が coordinator に集約され、call site が `Application.Current.MainWindow` を直接前提にしない。
- background worker から同期的に confirmation dialog を出す経路がない。
- progress operation 中の通知は preflight / post-result / progress status のいずれかに分類される。
- Model / utility から直接 UI dialog を表示する経路がない。
- `DispatcherMessageBox` の標準 `MessageBox.Show` fallback が通常 route で使われない。
- `ConfirmationMessage.Response == null` がユーザーキャンセルとして扱われない。
- owner なし picker / owner なし `ShowDialog()` が残っていない。
- emergency route が通常 route と分離されている。
- architecture test が、新しい分散 dialog route の追加を検出できる。

## Risks

- 差分が大きく、既存の ViewModel / View / Model 境界を広く触る。
- 既存の Livet `Messenger` 前提のテストや XAML trigger test が大幅に変わる。
- progress operation と mutation boundary の責務整理により、処理結果型の設計変更が必要になる。
- overlay dialog host 化は XAML の重なり、focus、keyboard、binding lifetime に影響する。
- emergency route と通常 route の分離により、起動直後 / 終了中の表示方針を明確に決める必要がある。

この計画では、これらのリスクを小差分で隠さず、全体整合性を優先してまとめて解消する。

## Handoff Notes

- 既存の `ThemedMessageBox` は「表示部品」としては使える。問題は部品ではなく route / owner / modal state 管理である。
- `DispatcherMessageBox` を直接強化するだけでは、Livet route、picker route、progress route、overlay route は揃わない。
- 最初に coordinator と inventory を作り、その後は同じ route の call site を横断的に置換する。
- `IBmsLibraryDialogService` は Model 側の抽象として一時的に残せるが、最終的には operation result を UI に返す形へ寄せる。
- score viewer 登録の問題は、この整理の Unit 8 として扱う。個別修正を先に入れる場合でも、最終的には coordinator route に統合する。
