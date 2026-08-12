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

    private void browseLr2CustomFolderOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseLr2CustomFolderOutputDirectory();
    }

    private void browseLr2CustomFolderAsRootOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseLr2CustomFolderAsRootOutputDirectory();
    }

    private void buttonAddCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleAddCustomFolderAdditionalOutputBase();
    }

    private void buttonRemoveCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        GetViewModel().RemoveSelectedCustomFolderAdditionalOutputBaseDir();
    }

    private void buttonRenameCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        GetViewModel().RenameSelectedCustomFolderAdditionalOutputBaseDir();
    }

    private void buttonAddPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleAddPlayHistoryFolderDisplayPreset();
    }

    private void buttonRemovePlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        GetViewModel().RemoveSelectedPlayHistoryFolderDisplayPreset();
    }

    private void buttonEditPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleEditPlayHistoryFolderDisplayPreset();
    }
}
