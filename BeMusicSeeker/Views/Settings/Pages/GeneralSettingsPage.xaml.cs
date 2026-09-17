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

    private void operationModeRadioButtonClick(object sender, RoutedEventArgs e)
    {
        bool requestedOperationMode = sender switch
        {
            _ when ReferenceEquals(sender, radioButtonUseLR2) => true,
            _ when ReferenceEquals(sender, radioButtonNotUseLR2) => false,
            _ => throw new InvalidOperationException("Unexpected operation mode selection source.")
        };

        SettingsDialogViewModel viewModel = GetViewModel();
        if (viewModel.OperationModeLR2DB != requestedOperationMode)
        {
            viewModel.OperationModeLR2DB = requestedOperationMode;
        }
    }

    private async void resyncLr2SongDbSyncDataButtonClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandleLr2SongDbSyncDataResyncAsync();
    }

    private async void installOrRepairLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandleInstallOrRepairLr2PlayHistorySchemaAsync();
    }

    private async void buttonAddBmsSearchRootPathsClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandleAddBmsSearchRootPathsAsync();
    }

    private void browseLr2RootPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseLr2RootPath();
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

    private void browseBeatorajaRootPathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseBeatorajaRootPath();
    }

    private void importBeatorajaTableUrlsButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleImportBeatorajaTableUrls();
    }

    private void bmsSearchRootPathListBoxDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedDirectories(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void bmsSearchRootPathListBoxDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedDirectories(e, out List<string> directories))
        {
            GetViewModel().AddBmsSearchRootPaths(directories);
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
