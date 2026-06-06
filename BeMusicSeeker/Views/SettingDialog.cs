using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace BeMusicSeeker.Views;

public partial class SettingDialog : UserControl, IComponentConnector
{
    internal Binding bindingLR2CustomFolderOutputDir;

    internal Binding bindingBMSInstallDir;

    public SettingDialog()
    {
        InitializeComponent();
        IsVisibleChanged += SettingDialogIsVisibleChanged;
        var entryAssembly = Assembly.GetEntryAssembly();
        string text = entryAssembly?.GetName().Version?.ToString() ?? string.Empty;
        string text2 = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        textBlockVerNum.Text = string.IsNullOrWhiteSpace(text2) ? text : text2;
        textBlockBuildNum.Text = "Build: " + text;
    }

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel } mainWindowViewModel)
        {
            MainWindowViewModel.SettingDialogViewModel.RestartMode restartMode = settingDialogViewModel.IsNeedRestartForSaveOrCancel();
            settingDialogViewModel.ResetSettings();
            SyncAppearanceThemeSelection(settingDialogViewModel);
            settingDialog.Visibility = Visibility.Hidden;
            if (restartMode.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.All))
            {
                mainWindowViewModel.Initialize();
            }
            else if (restartMode.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.FolderOnly))
            {
                mainWindowViewModel.ReloadFileDiff();
            }
            else if (restartMode.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.ScoreOnly))
            {
                mainWindowViewModel.ReloadScoresOnly();
            }
        }
    }

    private void SettingDialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            SyncAppearanceThemeSelection(settingDialogViewModel);
        }
    }

    private void SyncAppearanceThemeSelection(MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
    {
        comboBoxAppearanceTheme.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
        comboBoxAppearanceTheme.SelectedValue ??= settingDialogViewModel.AppearanceTheme;
    }

    private async void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel } viewModel))
        {
            return;
        }
        settingDialogRootGrid.IsEnabled = false;
        try
        {
            if (viewModel.IsLibraryOperationInProgress)
            {
                DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
                settingDialogViewModel.ResetSettings();
                SyncAppearanceThemeSelection(settingDialogViewModel);
                return;
            }
            if (settingDialogViewModel.CheckValidation(out string errMsg))
            {
                bool shouldInitializeAfterSave = !viewModel.HasActiveLibraryProfile;
                MainWindowViewModel.SettingDialogViewModel.RestartMode needRestart = shouldInitializeAfterSave
                    ? MainWindowViewModel.SettingDialogViewModel.RestartMode.None
                    : settingDialogViewModel.IsNeedRestartForSaved();
                if (shouldInitializeAfterSave)
                {
                    await settingDialogViewModel.SaveSettingsForInitialInitialize();
                    settingDialog.Visibility = Visibility.Hidden;
                    if (((App)Application.Current).firstStartup)
                    {
                        DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_initsetting_completed, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
                    }
                    viewModel.Initialize();
                }
                else
                {
                    await settingDialogViewModel.SaveSettings();
                    if (needRestart.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.All))
                    {
                        viewModel.Initialize();
                    }
                    else if (needRestart.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.ScoreOnly)
                        && needRestart.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.FolderOnly))
                    {
                        viewModel.Initialize();
                    }
                    else if (needRestart.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.ScoreOnly))
                    {
                        viewModel.ReloadScoresOnly();
                    }
                    else if (needRestart.HasFlag(MainWindowViewModel.SettingDialogViewModel.RestartMode.FolderOnly))
                    {
                        viewModel.ReloadFileDiff();
                    }
                    settingDialog.Visibility = Visibility.Hidden;
                }
            }
            else
            {
                DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_invalid_setting + Environment.NewLine + Environment.NewLine + errMsg, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
            }
        }
        catch (Exception ex)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            settingDialogRootGrid.IsEnabled = true;
        }
    }

    private async void resyncLr2FullGenerationDataButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (viewModel.IsLibraryOperationInProgress)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
            return;
        }
        if (DispatcherMessageBox.Show(
            Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_full_generation_data_resync,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        settingDialog.Visibility = Visibility.Hidden;
        Control control = sender as Control;
        if (control != null)
        {
            control.IsEnabled = false;
        }
        try
        {
            await viewModel.RequestLr2FullGenerationDataSyncAsync("setting_dialog_manual_resync", force: true);
        }
        catch (Exception ex)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            if (control != null)
            {
                control.ClearValue(UIElement.IsEnabledProperty);
            }
        }
    }

    private async void detailTabItemBackupButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || viewModel.BMSTables == null)
        {
            return;
        }
        var fileDialog = new SaveFileDialog
        {
            Title = "プレイリストデータを保存",
            FileName = "BeMusicSeeker_backup.sql",
            DefaultExt = ".sql",
            AddExtension = true,
            Filter = "sqlファイル(*.sql)|*.sql"
        };
        if (fileDialog.ShowDialog() == true)
        {
            settingDialogRootGrid.IsEnabled = false;
            await Task.Run(delegate
            {
                viewModel.BackupBMSTables(fileDialog.FileName);
            }).Logging("detailTabItemBackupButtonClicked");
            settingDialogRootGrid.IsEnabled = true;
        }
    }

    private async void detailTabItemRestoreButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || viewModel.BMSTables == null || DispatcherMessageBox.Show(Window.GetWindow(this), "プレイリストをバックアップから復元します。" + Environment.NewLine + "現在のプレイリストは全て削除され置き換えられます。" + Environment.NewLine + "バックアップデータが不正な場合元に戻せなくなるかもしれません。" + Environment.NewLine + Environment.NewLine + "続行しますか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        var fileDialog = new OpenFileDialog
        {
            Title = "プレイリストバックアップを開く",
            FileName = "BeMusicSeeker_backup.sql",
            DefaultExt = ".sql",
            Filter = "sqlファイル(*.sql)|*.sql"
        };
        if (fileDialog.ShowDialog() == true)
        {
            settingDialogRootGrid.IsEnabled = false;
            await Task.Run(delegate
            {
                viewModel.RestoreBMSTables(fileDialog.FileName);
            }).Logging("detailTabItemRestoreButtonClicked");
            await base.Dispatcher.BeginInvoke((Action)delegate
            {
                DispatcherMessageBox.Show(Application.Current.MainWindow, "アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Question, MessageBoxResult.OK);
                Application.Current.MainWindow.Close();
            }, DispatcherPriority.Normal);
        }
    }

    private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || viewModel.BMSTables == null)
        {
            return;
        }
        if (viewModel.IsLibraryOperationInProgress)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
            return;
        }
        if (DispatcherMessageBox.Show(Window.GetWindow(this), "BeMusicSeekerのデータをLR2データベースから削除します。" + Environment.NewLine + "続行した場合この操作を取り消しすることは出来ません。" + Environment.NewLine + "必要に応じて事前にバックアップを取得してください。" + Environment.NewLine + Environment.NewLine + "続行しますか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        bool closeAfterSuccess = false;
        settingDialogRootGrid.IsEnabled = false;
        try
        {
            await Task.Run(delegate
            {
                viewModel.UninstallAllData();
            }).Logging("detailTabItemUninstallButtonClicked");
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_success_uninstall, BeMusicSeeker.Properties.Resources.Success, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
            await base.Dispatcher.BeginInvoke((Action)delegate
            {
                DispatcherMessageBox.Show(Application.Current.MainWindow, "アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Question, MessageBoxResult.OK);
                Application.Current.MainWindow.Close();
            }, DispatcherPriority.Normal);
            closeAfterSuccess = true;
        }
        catch (Exception ex)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_failed_uninstall + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        finally
        {
            if (!closeAfterSuccess)
            {
                settingDialogRootGrid.IsEnabled = true;
            }
        }
    }

    private void hyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(e.Uri.ToString());
        e.Handled = true;
    }

    private void comboBoxEncoderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.Items.Count > comboBox.SelectedIndex && comboBox.SelectedValue != comboBox.Items?[comboBox.SelectedIndex])
        {
            comboBox.SelectedItem = comboBox.Items[comboBox.SelectedIndex];
            comboBox.SelectedValue = comboBox.Items[comboBox.SelectedIndex];
        }
    }

    private void buttonAddStandaloneBmsRootPathsClicked(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel }))
        {
            return;
        }
        using var dialog = new CommonOpenFileDialog
        {
            Title = BeMusicSeeker.Properties.Resources.Add_BMSDirectory,
            IsFolderPicker = true,
            EnsurePathExists = true,
            Multiselect = true
        };
        CommonOpenFileDialogInteractionMessageAction.SetInitialDirectory(dialog, settingDialogViewModel.SelectedStandaloneBmsRootPath);
        if (dialog.ShowDialog(Window.GetWindow(this)) == CommonFileDialogResult.Ok)
        {
            settingDialogViewModel.AddStandaloneBmsRootPaths(dialog.FileNames);
        }
    }

    private async void buttonPlayerTestClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel && sender is Button button)
        {
            settingDialog.IsEnabled = false;
            await Task.Run(delegate
            {
                viewModel.settingDialog?.AudioPlayerInitTest();
            });
            settingDialog.IsEnabled = true;
        }
    }
}
