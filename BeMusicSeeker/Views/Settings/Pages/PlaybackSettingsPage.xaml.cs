using System;
using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts player selection and external playback settings.
/// </summary>
public partial class PlaybackSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the playback settings page.
    /// </summary>
    public PlaybackSettingsPage()
    {
        InitializeComponent();
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Playback settings page is not hosted by SettingsWindow.");
    }

    private void browseUbmplayPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseUbmplayPath();
    }

    private void browseBmIdxViewPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseBmIdxViewPath();
    }

    private void browseLr2RootPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseLr2RootPath();
    }
}
