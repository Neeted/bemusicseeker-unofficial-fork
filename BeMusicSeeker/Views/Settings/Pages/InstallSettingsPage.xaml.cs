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

    private void addBmsInstallDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Install settings page is not hosted by SettingsWindow.");
        settingsWindow.HandleAddBmsInstallDirectory();
    }
}
