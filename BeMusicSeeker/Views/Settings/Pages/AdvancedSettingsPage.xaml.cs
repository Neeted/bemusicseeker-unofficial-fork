using System;
using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts advanced confirmation, startup, integration, and installation settings.
/// </summary>
public partial class AdvancedSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the advanced settings page.
    /// </summary>
    public AdvancedSettingsPage()
    {
        InitializeComponent();
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Advanced settings page is not hosted by SettingsWindow.");
    }

    private async void uninstallLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandleUninstallLr2PlayHistorySchemaAsync();
    }

    private async void applicationDataUninstallButtonClicked(object sender, RoutedEventArgs e)
    {
        await GetSettingsWindow().HandleApplicationDataUninstallAsync();
    }
}
