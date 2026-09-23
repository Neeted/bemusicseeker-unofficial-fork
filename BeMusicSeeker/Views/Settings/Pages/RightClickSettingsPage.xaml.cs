using System;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>Hosts the dialog-local right-click web and program action editor.</summary>
public partial class RightClickSettingsPage : UserControl
{
    /// <summary>Initializes the right-click action settings page.</summary>
    public RightClickSettingsPage()
    {
        InitializeComponent();
    }

    private RightClickActionSettingsEditor GetEditor()
    {
        return (DataContext as SettingsDialogViewModel)?.RightClickActionSettingsEditor
            ?? throw new InvalidOperationException("Right-click settings editor is unavailable.");
    }

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Right-click settings page is not hosted by SettingsWindow.");
    }

    private void addWebActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().AddWebAction();

    private void deleteWebActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().DeleteSelectedWebAction();

    private void moveWebActionUpButtonClicked(object sender, RoutedEventArgs e) => GetEditor().MoveSelectedWebActionUp();

    private void moveWebActionDownButtonClicked(object sender, RoutedEventArgs e) => GetEditor().MoveSelectedWebActionDown();

    private void addProgramActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().AddProgramAction();

    private void deleteProgramActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().DeleteSelectedProgramAction();

    private void moveProgramActionUpButtonClicked(object sender, RoutedEventArgs e) => GetEditor().MoveSelectedProgramActionUp();

    private void moveProgramActionDownButtonClicked(object sender, RoutedEventArgs e) => GetEditor().MoveSelectedProgramActionDown();

    private void restoreDefaultsButtonClicked(object sender, RoutedEventArgs e) => GetEditor().RestoreDefaults();

    private void browseProgramExecutableButtonClicked(object sender, RoutedEventArgs e)
    {
        GetSettingsWindow().HandleBrowseRightClickProgramExecutable();
    }
}
