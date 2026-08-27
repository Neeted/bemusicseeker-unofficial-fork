using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    private bool allowClose;

    private bool ownerShutdownCloseRequested;

    private bool closed;

    /// <summary>Initializes an unbound bulk-edit window for XAML tooling.</summary>
    public PlaylistSummaryBulkEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>Initializes a bulk-edit window for one workspace-owned operation session.</summary>
    /// <param name="viewModel">The bulk-edit session displayed by the window.</param>
    internal PlaylistSummaryBulkEditDialog(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>Gets whether the owner forced this window closed for application shutdown.</summary>
    internal bool IsOwnerShutdownClose => ownerShutdownCloseRequested;

    /// <summary>Gets the apply task that must settle before the workspace drops the session.</summary>
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

    private async void ApplyCustomFolderOutput(object sender, RoutedEventArgs e)
    {
        await RunApplyAsync(
            viewModel => viewModel.ApplyCustomFolderOutputTypes(),
            viewModel => viewModel.ReloadCustomFolderOutputStates(),
            "PlaylistSummaryBulkEditDialog.ApplyCustomFolderOutput");
    }

    private async void ApplyRootFolder(object sender, RoutedEventArgs e)
    {
        await RunApplyAsync(
            viewModel => viewModel.ApplyRootFolder(),
            viewModel => viewModel.ResetRootFolderOption(),
            "PlaylistSummaryBulkEditDialog.ApplyRootFolder");
    }

    private async void ApplyExternalSync(object sender, RoutedEventArgs e)
    {
        if (!ConfirmExternalSyncBulkApply())
        {
            return;
        }
        await RunApplyAsync(
            viewModel => viewModel.ApplyExternalSync(),
            viewModel => viewModel.ResetExternalSyncOption(),
            "PlaylistSummaryBulkEditDialog.ApplyExternalSync");
    }

    private async void ApplyBmtOutput(object sender, RoutedEventArgs e)
    {
        await RunApplyAsync(
            viewModel => viewModel.ApplyBmtOutput(),
            viewModel => viewModel.ResetBmtOutputOption(),
            "PlaylistSummaryBulkEditDialog.ApplyBmtOutput");
    }

    private async void ApplyOutputBase(object sender, RoutedEventArgs e)
    {
        await RunApplyAsync(
            viewModel => viewModel.ApplyOutputBase(),
            viewModel => viewModel.ResetOutputBaseOption(),
            "PlaylistSummaryBulkEditDialog.ApplyOutputBase");
    }

    private async void ApplyExternalPropertyInitialization(object sender, RoutedEventArgs e)
    {
        await RunApplyAsync(
            viewModel => viewModel.ApplyExternalPropertyInitializationAsync(),
            viewModel => viewModel.ResetExternalPropertyInitializationOptions(),
            "PlaylistSummaryBulkEditDialog.ApplyExternalPropertyInitialization");
    }

    private Task RunApplyAsync(Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> apply, Action<PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply, string logName)
    {
        return RunApplyAsync(
            async viewModel =>
            {
                apply(viewModel);
                await Task.CompletedTask;
            },
            afterApply,
            logName);
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
        try
        {
            await Task.Run(async () => await apply(bulkEditDialogViewModel).ConfigureAwait(false)).Logging(logName);
            afterApply?.Invoke(bulkEditDialogViewModel);
        }
        catch (Exception ex)
        {
            ShowOperationFailure(ex);
        }
        finally
        {
            IsEnabled = true;
            Interlocked.Exchange(ref applyInProgress, 0);
            applyTask = Task.CompletedTask;
            completion.TrySetResult(true);
        }
    }

    private void ShowOperationFailure(Exception exception)
    {
        UiDialogRoute.ShowMessageBox(
            this,
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand);
    }

    private bool ConfirmExternalSyncBulkApply()
    {
        if (base.DataContext is not PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel bulkEditDialogViewModel
            || bulkEditDialogViewModel.ExternalSyncOption?.Value is not bool enabled)
        {
            return false;
        }

        string message = enabled
            ? "同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？"
            : "同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？";
        return UiDialogRoute.ShowMessageBox(
            this,
            message,
            "警告",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }
}
