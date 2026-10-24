using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts general, LR2, and beatoraja settings for the shared settings edit session.
/// </summary>
public partial class GeneralSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the general settings page.
    /// </summary>
    public GeneralSettingsPage()
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
            ?? throw new InvalidOperationException("General settings page is not hosted by SettingsWindow.");
    }

    private async void operationModeRadioButtonClick(object sender, RoutedEventArgs e)
    {
        bool requestedOperationMode = sender switch
        {
            _ when ReferenceEquals(sender, radioButtonUseLR2) => true,
            _ when ReferenceEquals(sender, radioButtonNotUseLR2) => false,
            _ => throw new InvalidOperationException("Unexpected operation mode selection source.")
        };
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleOperationModeSelectionAsync(requestedOperationMode);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "operation mode selection");
        }
    }

    private async void resyncLr2SongDbSyncDataButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleLr2SongDbSyncDataResyncAsync();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 manual resync");
        }
    }

    private async void installOrRepairLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleInstallOrRepairLr2PlayHistorySchemaAsync();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 play history schema installation");
        }
    }

    private async void buttonAddBmsSearchRootPathsClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleAddBmsSearchRootPathsAsync();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "BMS search root folder picker");
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

    private async void browseLr2SongDbPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleBrowseLr2SongDbPathAsync();
        }
        catch (Exception ex)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(ex, "LR2 song database picker");
        }
    }

    private async void browseLr2ConfigPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleBrowseLr2ConfigPathAsync();
        }
        catch (Exception ex)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(ex, "LR2 configuration picker");
        }
    }

    private async void browseBeatorajaRootPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleBrowseBeatorajaRootPathAsync();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "beatoraja root folder picker");
        }
    }

    private async void importBeatorajaTableUrlsButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleImportBeatorajaTableUrls();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "beatoraja table URL import");
        }
    }

    private async void buttonRemoveBmsSearchRootClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow window = GetSettingsWindow();
        try { await window.HandleRemoveBmsSearchRootPathAsync(); }
        catch (Exception failure) { await window.HandleSettingsRouteFailureAsync(failure, "BMS search root removal"); }
    }

    private void bmsSearchRootPathListBoxDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedDirectories(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void bmsSearchRootPathListBoxDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedDirectories(e, out List<string> directories))
        {
            try { await GetSettingsWindow().HandleDroppedBmsSearchRootPathsAsync(directories); }
            catch (Exception failure) { await GetSettingsWindow().HandleSettingsRouteFailureAsync(failure, "BMS search root drop"); }
        }

        e.Handled = true;
    }

    private static bool TryGetDroppedDirectories(DragEventArgs e, out List<string> directories)
    {
        directories = [];
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
            || e.Data.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] paths
            || paths.Length == 0)
        {
            return false;
        }

        directories = [.. paths.Where(Directory.Exists)];
        return directories.Count == paths.Length;
    }
}
