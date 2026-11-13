using System;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts playlist backup, restore, and LR2 scheduled backup settings.
/// </summary>
public partial class BackupSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the backup settings page.
    /// </summary>
    public BackupSettingsPage()
    {
        InitializeComponent();
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Backup settings page is not hosted by SettingsWindow.");
    }

    private async void detailTabItemBackupButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandlePlaylistBackupAsync(reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "playlist backup");
        }
    }

    private async void detailTabItemRestoreButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandlePlaylistRestoreAsync(reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "playlist restore");
        }
    }

    private async void browseLr2BackupPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleBrowseLr2BackupPathAsync(reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 backup folder picker");
        }
    }

    /// <summary>
    /// クリック・キーボード・UIオートメーションの切替のどれでも通る表示状態の変化を、LR2バックアップの有効化の要求として扱います。
    /// 束縛の更新で確定値・候補へ揃えた変化は要求と候補が一致するため処理せず、保存結果の反映から再入しません。
    /// </summary>
    private async void lr2BackupEnabledCheckBoxToggled(object sender, RoutedEventArgs e)
    {
        var checkBox = (CheckBox)sender;
        bool requested = checkBox.IsChecked == true;
        if (checkBox.DataContext is not SettingsDialogViewModel settings || settings.IsLR2BackupEnabled == requested) { return; }
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleLr2BackupEnabledChangedAsync(requested, reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 backup enable");
        }
    }
}
