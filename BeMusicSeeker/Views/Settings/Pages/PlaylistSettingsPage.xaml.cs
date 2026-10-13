using System;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts playlist output and play-history presentation settings.
/// </summary>
public partial class PlaylistSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the playlist settings page.
    /// </summary>
    public PlaylistSettingsPage()
    {
        InitializeComponent();
    }

    private SettingsDialogViewModel GetViewModel()
    {
        return DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Playlist settings page is not hosted by SettingsWindow.");
    }

    private async void browseLr2CustomFolderOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseLr2CustomFolderOutputDirectory();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 custom folder output picker");
        }
    }

    private async void browseLr2CustomFolderAsRootOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseLr2CustomFolderAsRootOutputDirectory();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 custom root folder output picker");
        }
    }

    private async void buttonAddCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleAddCustomFolderAdditionalOutputBase();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "additional custom folder output picker");
        }
    }

    private async void buttonRemoveCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            GetViewModel().RemoveSelectedCustomFolderAdditionalOutputBaseDir();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "additional custom folder output removal");
        }
    }

    private async void buttonRenameCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            GetViewModel().RenameSelectedCustomFolderAdditionalOutputBaseDir();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "additional custom folder output rename");
        }
    }

    private async void buttonAddPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleAddPlayHistoryFolderDisplayPreset();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "play history display preset dialog");
        }
    }

    private void buttonRemovePlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        GetViewModel().RemoveSelectedPlayHistoryFolderDisplayPreset();
    }

    private async void buttonEditPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleEditPlayHistoryFolderDisplayPreset();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "play history display preset dialog");
        }
    }
}
