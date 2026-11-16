using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

/// <summary>
/// play history FOLDER 表示プリセットの名前と対象 playlist を編集するダイアログです。
/// 設定ダイアログ本体を狭くしないため、playlist の複数選択はこの別ウィンドウに分離します。
/// </summary>
public partial class PlayHistoryFolderDisplayPresetEditDialog : ThemedWindow
{
    private readonly SettingsDialogViewModel settingDialogViewModel;
    private readonly IUiDialogService dialogService;

    /// <summary>
    /// 編集ダイアログを初期化します。
    /// </summary>
    /// <param name="settingDialogViewModel">設定ダイアログ ViewModel。</param>
    /// <param name="session">編集セッション。</param>
    /// <param name="dialogService">検証エラーの通知に使う、設定画面と同じ窓口。null は許されません。</param>
    internal PlayHistoryFolderDisplayPresetEditDialog(
        SettingsDialogViewModel settingDialogViewModel,
        PlayHistoryFolderDisplayPresetEditSession session,
        IUiDialogService dialogService)
    {
        this.settingDialogViewModel = settingDialogViewModel;
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        InitializeComponent();
        DataContext = session;
    }

    /// <summary>
    /// 保存して閉じます。検証エラーの通知は窓口の完了を待ち、表示の失敗は任意の通知として記録するだけにして、
    /// 保存の成否や閉じる動作へ読み替えません。
    /// </summary>
    private async void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayHistoryFolderDisplayPresetEditSession session)
        {
            DialogResult = false;
            return;
        }
        if (!settingDialogViewModel.TryApplyPlayHistoryFolderDisplayPresetEditSession(session, out string errMsg))
        {
            await NotifyValidationErrorAsync(errMsg);
            return;
        }
        DialogResult = true;
    }

    private async Task NotifyValidationErrorAsync(string message)
    {
        try
        {
            UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
                message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK,
                owner: this));
            if (notification is not { IsShown: true })
            {
                LogNotificationFailure(notification?.Exception
                    ?? new InvalidOperationException("Play history preset validation notification was not shown (" + (notification?.Status.ToString() ?? "no result") + ")."));
            }
        }
        catch (Exception exception)
        {
            LogNotificationFailure(exception);
        }
    }

    private static void LogNotificationFailure(Exception exception)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, "play history preset validation notification"); }
        catch { /* 診断先の失敗を元の操作結果へ合成しません。 */ }
    }
}
