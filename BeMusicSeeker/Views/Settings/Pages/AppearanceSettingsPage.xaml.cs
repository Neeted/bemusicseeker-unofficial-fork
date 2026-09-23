using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts theme and table appearance settings for the shared settings edit session.
/// </summary>
public partial class AppearanceSettingsPage : UserControl
{
    /// <summary>
    /// Initializes the appearance settings page.
    /// </summary>
    public AppearanceSettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Re-evaluates the theme selector after application theme resources have changed.
    /// </summary>
    internal void RefreshThemeSelection()
    {
        radioButtonLightTheme.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        radioButtonDarkTheme.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private SettingsDialogViewModel GetViewModel()
    {
        return DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Appearance settings page is not hosted by SettingsWindow.");
    }

    private void resetCustomTableAppearanceDefaultsButtonClick(object sender, RoutedEventArgs e)
    {
        GetViewModel().ResetCustomTableAppearanceDefaults();
    }

    private void browseStagefilePathButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseStagefilePath();
    }
}
