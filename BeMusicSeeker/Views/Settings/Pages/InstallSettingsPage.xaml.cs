using System;
using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts installation destination and filename settings.
/// </summary>
public partial class InstallSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the installation settings page.
    /// </summary>
    public InstallSettingsPage()
    {
        InitializeComponent();
    }

    private async void installDirectorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string path } || Window.GetWindow(this) is not SettingsWindow window) { return; }
        try { await window.HandleSelectBmsInstallDirectoryAsync(path, reportFailure: true); }
        catch (Exception failure) { await window.HandleSettingsRouteFailureAsync(failure, "BMS install directory selection"); }
    }

    private async void addBmsInstallDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Install settings page is not hosted by SettingsWindow.");
        try
        {
            settingsWindow.HandleAddBmsInstallDirectory();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "BMS install directory picker");
        }
    }
}
