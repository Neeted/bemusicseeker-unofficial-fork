using System;
using System.Threading.Tasks;
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

    private SettingsDialogViewModel GetSettings() => DataContext as SettingsDialogViewModel
        ?? throw new InvalidOperationException("Settings view model is unavailable.");

    private SettingsWindow GetSettingsWindow()
    {
        return Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("Right-click settings page is not hosted by SettingsWindow.");
    }

    private void addWebActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().AddWebAction();

    private async void deleteWebActionButtonClicked(object sender, RoutedEventArgs e) => await CommitConfirmedRecordAsync(true, "delete");

    private void moveWebActionUpButtonClicked(object sender, RoutedEventArgs e) => GetSettings().CommitRightClickRecord(true, "up");

    private void moveWebActionDownButtonClicked(object sender, RoutedEventArgs e) => GetSettings().CommitRightClickRecord(true, "down");

    private void addProgramActionButtonClicked(object sender, RoutedEventArgs e) => GetEditor().AddProgramAction();

    private async void deleteProgramActionButtonClicked(object sender, RoutedEventArgs e) => await CommitConfirmedRecordAsync(false, "delete");

    private void moveProgramActionUpButtonClicked(object sender, RoutedEventArgs e) => GetSettings().CommitRightClickRecord(false, "up");

    private void moveProgramActionDownButtonClicked(object sender, RoutedEventArgs e) => GetSettings().CommitRightClickRecord(false, "down");

    private async void restoreDefaultsButtonClicked(object sender, RoutedEventArgs e) => await CommitConfirmedRecordAsync(true, "defaults");

    /// <summary>確認を伴う削除・既定値への復帰を実行し、確認の表示失敗などを設定画面の失敗の経路へ渡します。</summary>
    private async Task CommitConfirmedRecordAsync(bool web, string operation)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            GetSettings().CommitRightClickRecord(web, operation);
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "right-click record " + operation);
        }
    }

    private async void browseProgramExecutableButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseRightClickProgramExecutable();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "right-click program executable picker");
        }
    }
}
