using System;
using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts audio recording and encoder settings.
/// </summary>
public partial class RecordingSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the recording settings page.
    /// </summary>
    public RecordingSettingsPage()
    {
        InitializeComponent();
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Recording settings page is not hosted by SettingsWindow.");
    }

    private void comboBoxEncoderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox
            && comboBox.SelectedIndex >= 0
            && comboBox.Items.Count > comboBox.SelectedIndex
            && comboBox.SelectedValue != comboBox.Items[comboBox.SelectedIndex])
        {
            comboBox.SelectedItem = comboBox.Items[comboBox.SelectedIndex];
            comboBox.SelectedValue = comboBox.Items[comboBox.SelectedIndex];
        }
    }

    private async void browseEncoderExeDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseEncoderExecutableDirectory();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "encoder directory picker");
        }
    }
}
