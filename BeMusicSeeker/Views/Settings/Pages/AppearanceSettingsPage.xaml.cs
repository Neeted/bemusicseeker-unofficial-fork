using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        appearanceThemeComboBox.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.UpdateTarget();
    }

    private void appearanceFontComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 保存に失敗すると、Bindingは選択欄の値だけを確定値へ戻し、選択の変更中のため表示中の候補は戻さない。
        // 変更の完了後に、保存されなかった候補が表示に残っていれば、確定値の候補を選び直す。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
        {
            if (DataContext is SettingsDialogViewModel viewModel
                && appearanceFontComboBox.SelectedItem is SettingsDialogViewModel.AppearanceFontOption selected
                && !string.Equals(selected.Key, viewModel.AppearanceFontFamily, StringComparison.Ordinal))
            {
                appearanceFontComboBox.SelectedItem = viewModel.AppearanceFontOptions
                    .FirstOrDefault(option => string.Equals(option.Key, viewModel.AppearanceFontFamily, StringComparison.Ordinal));
            }
        });
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

    private async void browseStagefilePathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = GetSettingsWindow();
        try
        {
            settingsWindow.HandleBrowseStagefilePath();
        }
        catch (Exception exception)
        {
            await settingsWindow.HandleSettingsRouteFailureAsync(exception, "stage image file picker");
        }
    }
}
