#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;
using AppResources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>二つのJSON保存先を同時に確認し、有効な組合せだけを確定する画面です。</summary>
public partial class PlaylistJsonExportDialog : ThemedWindow
{
    private readonly PlaylistJsonExportDialogViewModel viewModel;
    private readonly IUiDialogService dialogs;
    private readonly string headerFileName;
    private readonly string dataFileName;

    /// <summary>一回の入力とpickerの既定ファイル名・所有者を接続します。</summary>
    internal PlaylistJsonExportDialog(PlaylistJsonExportDialogViewModel viewModel, IUiDialogService dialogs,
        string headerFileName, string dataFileName)
    {
        this.viewModel = viewModel;
        this.dialogs = dialogs;
        this.headerFileName = headerFileName;
        this.dataFileName = dataFileName;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void AcceptExport(object sender, RoutedEventArgs e)
    {
        if (viewModel.CanExport) { DialogResult = true; }
    }

    private async void BrowseHeader(object sender, RoutedEventArgs e) => await BrowseAsync(true);
    private async void BrowseData(object sender, RoutedEventArgs e) => await BrowseAsync(false);

    private async Task BrowseAsync(bool header)
    {
        try
        {
            string current = header ? viewModel.HeaderPath : viewModel.DataPath;
            UiSaveFilePickerResult result = await dialogs.PickSaveFileAsync(new UiSaveFilePickerRequest(
                header ? AppResources.Save_header_file : AppResources.Save_data_file,
                string.IsNullOrWhiteSpace(current) ? (header ? headerFileName : dataFileName) : current,
                ".json", AppResources.Json_file_exts, owner: this));
            if (result?.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser) { return; }
            if (result?.Status != UiDialogStatus.Accepted)
            {
                throw new InvalidOperationException("Playlist JSON save picker failed: " + result?.Status, result?.Error);
            }
            if (DataContext == null) { return; }
            if (header) { viewModel.HeaderPath = result.FileName; }
            else { viewModel.DataPath = result.FileName; }
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

    private static void TryLogFailure(Exception? exception)
    {
        try { Ribbit.Logging.NLogWrapper.FileLogger?.Error(exception, "Playlist JSON export picker failed."); }
        catch { /* 診断失敗で入力値や元の失敗を変更しません。 */ }
    }

    /// <summary>閉鎖後にpickerが完了しても入力へ反映しないよう、画面接続を解放します。</summary>
    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        base.OnClosed(e);
    }
}
