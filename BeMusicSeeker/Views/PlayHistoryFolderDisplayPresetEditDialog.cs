using System.Windows;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>
/// play history FOLDER 表示プリセットの名前と対象 playlist を編集するダイアログです。
/// 設定ダイアログ本体を狭くしないため、playlist の複数選択はこの別ウィンドウに分離します。
/// </summary>
public partial class PlayHistoryFolderDisplayPresetEditDialog : Window
{
    private readonly MainWindowViewModel.SettingDialogViewModel settingDialogViewModel;

    /// <summary>
    /// 編集ダイアログを初期化します。
    /// </summary>
    /// <param name="settingDialogViewModel">設定ダイアログ ViewModel。</param>
    /// <param name="session">編集セッション。</param>
    public PlayHistoryFolderDisplayPresetEditDialog(
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel,
        PlayHistoryFolderDisplayPresetEditSession session)
    {
        this.settingDialogViewModel = settingDialogViewModel;
        InitializeComponent();
        DataContext = session;
    }

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayHistoryFolderDisplayPresetEditSession session)
        {
            DialogResult = false;
            return;
        }
        if (!settingDialogViewModel.TryApplyPlayHistoryFolderDisplayPresetEditSession(session, out string errMsg))
        {
            UiDialogRoute.ShowMessageBox(this, errMsg, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        DialogResult = true;
    }
}
