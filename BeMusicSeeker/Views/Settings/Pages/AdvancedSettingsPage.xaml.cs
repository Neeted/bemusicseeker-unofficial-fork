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
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleUninstallLr2PlayHistorySchemaAsync(reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "LR2 play history schema removal");
        }
    }

    private async void applicationDataUninstallButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            await settingsWindow.HandleApplicationDataUninstallAsync(reportFailure: true);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "application data uninstall");
        }
    }
}
