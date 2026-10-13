using System;
using System.Windows;
using System.Windows.Controls;

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
            await settingsWindow.HandlePlaylistBackupAsync();
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
            await settingsWindow.HandlePlaylistRestoreAsync();
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
            settingsWindow.HandleBrowseLr2BackupPath();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 backup folder picker");
        }
    }
}
