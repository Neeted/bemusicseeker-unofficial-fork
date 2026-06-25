using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public partial class PlaylistSummaryBulkEditDialog : UserControl
{
    public PlaylistSummaryBulkEditDialog()
    {
        InitializeComponent();
    }

    private void CloseDialog(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            mainWindowViewModel.playlistSummaryBulkEditDialog = null;
        }
        playlistSummaryBulkEditDialog.Visibility = Visibility.Hidden;
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

    private async Task RunApplyAsync(Action<MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel> apply, Action<MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply, string logName)
    {
        if (base.DataContext is not MainWindowViewModel { playlistSummaryBulkEditDialog: { } bulkEditDialogViewModel })
        {
            return;
        }
        IsEnabled = false;
        try
        {
            await Task.Run(() => apply(bulkEditDialogViewModel)).Logging(logName);
            afterApply?.Invoke(bulkEditDialogViewModel);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task RunApplyAsync(Func<MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel, Task> apply, Action<MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel> afterApply, string logName)
    {
        if (base.DataContext is not MainWindowViewModel { playlistSummaryBulkEditDialog: { } bulkEditDialogViewModel })
        {
            return;
        }
        IsEnabled = false;
        try
        {
            await Task.Run(async () => await apply(bulkEditDialogViewModel).ConfigureAwait(false)).Logging(logName);
            afterApply?.Invoke(bulkEditDialogViewModel);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private bool ConfirmExternalSyncBulkApply()
    {
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel
            || mainWindowViewModel.playlistSummaryBulkEditDialog is not { } bulkEditDialogViewModel
            || bulkEditDialogViewModel.ExternalSyncOption?.Value is not bool enabled)
        {
            return false;
        }

        string message = enabled
            ? "同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？"
            : "同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？";
        return DispatcherMessageBox.Show(
            Window.GetWindow(this),
            message,
            "警告",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }
}
