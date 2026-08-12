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
        await GetSettingsWindow().HandlePlaylistBackupAsync();
    }

    private async void detailTabItemRestoreButtonClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandlePlaylistRestoreAsync();
    }

    private void browseLr2BackupPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseLr2BackupPath();
    }
}
