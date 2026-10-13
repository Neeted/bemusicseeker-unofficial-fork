using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

/// <summary>Provides the six immediate-apply playlist summary edits in an owner-modal window.</summary>
public partial class PlaylistSummaryBulkEditDialog : ThemedWindow
{
    private int applyInProgress;

    private Task applyTask = Task.CompletedTask;

    private readonly IUiDialogService dialogService;

    private bool allowClose;

    private bool ownerShutdownCloseRequested;

    private bool closed;

    /// <summary>Initializes an unbound bulk-edit window for XAML tooling.</summary>
    public PlaylistSummaryBulkEditDialog()
    {
        dialogService = new UiDialogCoordinator();
        InitializeComponent();
    }

    /// <summary>所有workspaceの一括変更と同じcoordinatorで、必要な失敗通知まで待つ画面を作ります。</summary>
    /// <param name="viewModel">画面が所有する一括変更session。</param>
    /// <param name="dialogService">親画面と同じ所有者解決・表示窓口。省略時は既定coordinatorを使います。</param>
    internal PlaylistSummaryBulkEditDialog(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel viewModel,
        IUiDialogService dialogService = null)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.dialogService = dialogService ?? this.dialogService;
    }

    /// <summary>Gets whether the owner forced this window closed for application shutdown.</summary>
    internal bool IsOwnerShutdownClose => ownerShutdownCloseRequested;

    /// <summary>確認・実反映・任意通知・再有効化までのTaskを返します。確認失敗では反映せず、通知単独の失敗は局所診断します。</summary>
    internal Task WaitForApplyCompletionAsync() => applyTask;

    /// <summary>
    /// Closes this window for owner shutdown without discarding an apply operation in flight.
    /// Repeated calls are intentionally idempotent.
    /// </summary>
    internal void CloseForOwnerShutdown()
    {
        ownerShutdownCloseRequested = true;
        allowClose = true;
        if (!closed)
        {
            Close();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        base.OnClosed(e);
    }

    private void CloseDialog(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }

    private void DialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (ownerShutdownCloseRequested || Volatile.Read(ref applyInProgress) != 0)
        {
            return;
        }

        allowClose = true;
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            // Presentation fixtures may use Show() to inspect the native window.
            Close();
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (allowClose || ownerShutdownCloseRequested)
        {
            base.OnClosing(e);
            return;
        }

        if (DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)
        {
            // An unbound tooling/presentation instance has no draft to discard.
            allowClose = true;
            base.OnClosing(e);
            return;
        }

        // Apply owns the operation state.  Keep the native window open until it settles;
        // the MainWindow route then drops the draft exactly once.
        if (Volatile.Read(ref applyInProgress) != 0)
        {
            e.Cancel = true;
            return;
        }

        // The title-bar X is an ordinary draft-discard close and has no asynchronous work.
        allowClose = true;
        base.OnClosing(e);
    }

    private void ApplyCustomFolderOutput(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyCustomFolderOutputTypesAsync(),
            viewModel => viewModel.ReloadCustomFolderOutputStates(),
            "PlaylistSummaryBulkEditDialog.ApplyCustomFolderOutput").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyCustomFolderOutput");
    }

    private void ApplyRootFolder(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyRootFolderAsync(),
            viewModel => viewModel.ResetRootFolderOption(),
            "PlaylistSummaryBulkEditDialog.ApplyRootFolder").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyRootFolder");
    }

    private void ApplyExternalSync(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyExternalSyncAsync(),
            viewModel => viewModel.ResetExternalSyncOption(),
            "PlaylistSummaryBulkEditDialog.ApplyExternalSync",
            ConfirmExternalSyncBulkApplyAsync).ObserveFault("PlaylistSummaryBulkEditDialog.ApplyExternalSync");
    }

    private void ApplyBmtOutput(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyBmtOutputAsync(),
            viewModel => viewModel.ResetBmtOutputOption(),
            "PlaylistSummaryBulkEditDialog.ApplyBmtOutput").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyBmtOutput");
    }

    private void ApplyOutputBase(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyOutputBaseAsync(),
            viewModel => viewModel.ResetOutputBaseOption(),
            "PlaylistSummaryBulkEditDialog.ApplyOutputBase").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyOutputBase");
    }

    private void ApplyExternalPropertyInitialization(object sender, RoutedEventArgs e)
    {
        RunApplyAsync(
            viewModel => viewModel.ApplyExternalPropertyInitializationAsync(),
            viewModel => viewModel.ResetExternalPropertyInitializationOptions(),
            "PlaylistSummaryBulkEditDialog.ApplyExternalPropertyInitialization").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyExternalPropertyInitialization");
    }

    private Task RunApplyAsync(Func<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel, Task> apply, Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply, string logName, Func<Task<bool>> confirm = null)
    {
        if (DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel
            || Interlocked.CompareExchange(ref applyInProgress, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        applyTask = completion.Task;
        _ = RunApplyCoreAsync(bulkEditDialogViewModel, apply, afterApply, logName, completion, confirm);
        return completion.Task;
    }

    private async Task RunApplyCoreAsync(
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel,
        Func<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel, Task> apply,
        Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply,
        string logName,
        TaskCompletionSource<bool> completion,
        Func<Task<bool>> confirm)
    {
        Exception failure = null;
        try
        {
            try
            {
                // UIの状態変更より先に、呼出元が待機できる操作Taskを公開しておきます。
                IsEnabled = false;
                if (confirm != null && !await confirm())
                {
                    return;
                }
                await Task.Run(async () => await apply(bulkEditDialogViewModel).ConfigureAwait(false));
                afterApply?.Invoke(bulkEditDialogViewModel);
            }
            catch (Exception ex)
            {
                TryLogFailure(ex, logName);
                await ShowOperationFailureAsync(ex);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                IsEnabled = true;
            }
            catch (Exception cleanupFailure)
            {
                if (failure == null) { failure = cleanupFailure; }
                else { TryLogFailure(cleanupFailure, logName + " restore_enabled_failed"); }
            }
            finally
            {
                // 再有効化の購読者が失敗しても、操作受付とその待機寿命を取り残しません。
                Interlocked.Exchange(ref applyInProgress, 0);
                applyTask = Task.CompletedTask;
            }
            if (failure != null) { completion.TrySetException(failure); }
            else { completion.TrySetResult(true); }
        }
    }

    private async Task ShowOperationFailureAsync(Exception exception)
    {
        if (ownerShutdownCloseRequested) { return; }
        try
        {
            UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                owner: this));
            UiDialogRoute.ThrowIfNotShown(notification, "Playlist bulk edit failure notification");
        }
        catch (Exception notificationFailure)
        {
            TryLogFailure(notificationFailure, "Playlist bulk edit failure notification");
        }
    }

    private static void TryLogFailure(Exception exception, string routeName)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, routeName); }
        catch { /* 診断先の失敗を反映結果へ加えません。 */ }
    }

    private async Task<bool> ConfirmExternalSyncBulkApplyAsync()
    {
        if (base.DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel
            || bulkEditDialogViewModel.ExternalSyncOption?.Value is not bool enabled)
        {
            return false;
        }

        string message = enabled
            ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
            : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied;
        UiDialogResult confirmation = await dialogService.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel,
            owner: this));
        UiDialogRoute.ThrowIfNotShown(confirmation, "Playlist bulk external sync confirmation");
        return confirmation.Status == UiDialogStatus.Accepted
            || confirmation.Status == UiDialogStatus.ClosedByUser
                && confirmation.MessageBoxResult == MessageBoxResult.OK;
    }
}
