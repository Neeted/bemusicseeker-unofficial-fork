#nullable enable
using System;
using System.Linq;
using System.Windows;
using BeMusicSeeker.ViewModels;
using AppResources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>統合候補一覧の優先統合先を追加・削除するウィンドウです。OKで確定し、キャンセルでは登録内容を変えません。</summary>
public partial class DuplicatePreferredDestinationDialog : ThemedWindow
{
    private readonly DuplicatePreferredDestinationDialogViewModel viewModel;
    private readonly IUiDialogService dialogs;

    /// <summary>一回の入力とフォルダ選択の窓口を接続します。</summary>
    internal DuplicatePreferredDestinationDialog(DuplicatePreferredDestinationDialogViewModel viewModel, IUiDialogService dialogs)
    {
        this.viewModel = viewModel;
        this.dialogs = dialogs;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void AcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private async void AddClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UiFolderPickerResult result = await dialogs.PickFolderAsync(new UiFolderPickerRequest(
                AppResources.Duplicate_merge_preferred_destination_title, null, false, this));
            if (result?.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser) { return; }
            if (result?.Status != UiDialogStatus.Accepted)
            {
                throw new InvalidOperationException("Preferred destination folder picker failed: " + result?.Status, result?.Error);
            }
            if (DataContext == null) { return; }
            foreach (string folder in result.FolderPaths) { viewModel.Add(folder); }
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
            if (DataContext == null) { return; }
            try
            {
                UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
                    AppResources.Msg_error_unexpected, AppResources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, owner: this));
                if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                {
                    TryLogFailure(result?.Exception);
                }
            }
            catch (Exception notificationFailure) { TryLogFailure(notificationFailure); }
        }
    }

    private void RemoveClick(object sender, RoutedEventArgs e)
        => viewModel.Remove(destinationList.SelectedItems.OfType<string>());

    private static void TryLogFailure(Exception? exception)
    {
        try { Ribbit.Logging.NLogWrapper.FileLogger?.Error(exception, "Preferred destination picker failed."); }
        catch { /* 診断失敗で入力値や元の失敗を変更しません。 */ }
    }

    /// <summary>閉鎖後にフォルダ選択が完了しても入力へ反映しないよう、画面接続を解放します。</summary>
    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        base.OnClosed(e);
    }
}
