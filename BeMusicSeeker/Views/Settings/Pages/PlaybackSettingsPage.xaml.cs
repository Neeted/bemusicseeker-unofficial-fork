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

    private async void browseUbmplayPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseUbmplayPath();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "uBMplay executable picker");
        }
    }

    private async void browseBmIdxViewPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseBmIdxViewPath();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "BMIIDXView executable picker");
        }
    }

    private async void browseLr2RootPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleBrowseLr2RootPathAsync();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 root folder picker");
        }
    }
}
