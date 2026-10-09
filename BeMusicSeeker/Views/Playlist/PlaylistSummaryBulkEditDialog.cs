using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

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

    /// <summary>現在反映中なら実操作・必要通知・再有効化までのTaskを返します。開始前と終端後は完了済みTaskです。通知単独の失敗も反映中に取得したTaskへ伝えます。</summary>
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
        if (!ConfirmExternalSyncBulkApply())
        {
            return;
        }
        RunApplyAsync(
            viewModel => viewModel.ApplyExternalSyncAsync(),
            viewModel => viewModel.ResetExternalSyncOption(),
            "PlaylistSummaryBulkEditDialog.ApplyExternalSync").ObserveFault("PlaylistSummaryBulkEditDialog.ApplyExternalSync");
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

    private Task RunApplyAsync(Func<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel, Task> apply, Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply, string logName)
    {
        if (DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel
            || Interlocked.CompareExchange(ref applyInProgress, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        IsEnabled = false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        applyTask = completion.Task;
        _ = RunApplyCoreAsync(bulkEditDialogViewModel, apply, afterApply, logName, completion);
        return completion.Task;
    }

    private async Task RunApplyCoreAsync(
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel,
        Func<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel, Task> apply,
        Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply,
        string logName,
        TaskCompletionSource<bool> completion)
    {
        Exception failure = null;
        try
        {
            try
            {
                await Task.Run(async () => await apply(bulkEditDialogViewModel).ConfigureAwait(false)).LoggingAndPropagate(logName);
                afterApply?.Invoke(bulkEditDialogViewModel);
            }
            catch (Exception ex) { await ShowOperationFailureAsync(ex); }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                IsEnabled = true;
                Interlocked.Exchange(ref applyInProgress, 0);
                applyTask = Task.CompletedTask;
            }
            catch (Exception cleanupFailure)
            {
                if (failure == null) { failure = cleanupFailure; }
                else { failure.Data["PlaylistBulkEditCleanupFailure"] = cleanupFailure; }
            }
            if (failure is OperationCanceledException cancelled) { completion.TrySetCanceled(cancelled.CancellationToken); }
            else if (failure != null) { completion.TrySetException(failure); }
            else { completion.TrySetResult(true); }
        }
    }

    private async Task ShowOperationFailureAsync(Exception exception)
    {
        if (ownerShutdownCloseRequested) { return; }
        UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            owner: this));
        UiDialogRoute.ThrowIfNotShown(notification, "Playlist bulk edit failure notification");
    }

    private bool ConfirmExternalSyncBulkApply()
    {
        if (base.DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel
            || bulkEditDialogViewModel.ExternalSyncOption?.Value is not bool enabled)
        {
            return false;
        }

        string message = enabled
            ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
            : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied;
        return UiDialogRoute.ShowMessageBox(
            this,
            message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }
}
