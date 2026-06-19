using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
            if (ShouldResetSettingsOnCancel(settingDialogViewModel))
            {
                settingDialogViewModel.ResetSettings();
                SyncAppearanceThemeSelection(settingDialogViewModel);
            }
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

    private async void SettingDialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            SyncAppearanceThemeSelection(settingDialogViewModel);
            await RefreshLr2PlayHistorySchemaStatusAsync(settingDialogViewModel, force: false);
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

    private async void resyncLr2SongDbSyncDataButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!viewModel.CanRequestLr2SongDbSyncDataResync)
        {
            if (viewModel.IsLibraryOperationInProgress)
            {
                DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            return;
        }
        if (DispatcherMessageBox.Show(
            Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_song_db_sync_data_resync,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        settingDialog.Visibility = Visibility.Hidden;
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await viewModel.RequestLr2SongDbSyncAsync("setting_dialog_manual_resync", force: true);
        }
        catch (Exception ex)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
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

    private async void refreshLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            await RefreshLr2PlayHistorySchemaStatusAsync(settingDialogViewModel, force: true);
        }
    }

    private async void installLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        await InstallOrRepairLr2PlayHistorySchemaAsync(isRepair: false);
    }

    private async void repairLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        await InstallOrRepairLr2PlayHistorySchemaAsync(isRepair: true);
    }

    private async Task InstallOrRepairLr2PlayHistorySchemaAsync(bool isRepair)
    {
        if (base.DataContext is not MainWindowViewModel { settingDialog: { } settingDialogViewModel } viewModel)
        {
            return;
        }
        if (viewModel.IsLibraryOperationInProgress)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
            return;
        }

        await RefreshLr2PlayHistorySchemaStatusAsync(settingDialogViewModel, force: true);
        if ((isRepair && !settingDialogViewModel.CanRepairLr2PlayHistorySchema)
            || (!isRepair && !settingDialogViewModel.CanInstallLr2PlayHistorySchema))
        {
            return;
        }
        if (DispatcherMessageBox.Show(
            Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_play_history_schema_install_or_repair
                + Environment.NewLine
                + Environment.NewLine
                + "score DB: "
                + settingDialogViewModel.Lr2PlayHistoryScoreDbPath,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        settingDialogRootGrid.IsEnabled = false;
        try
        {
            Lr2PlayHistorySchemaCheckResult result = await Task.Run(settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaCore);
            settingDialogViewModel.ApplyLr2PlayHistorySchemaCheckResult(result);
            if (result.Status == Lr2PlayHistorySchemaStatus.Installed)
            {
                DispatcherMessageBox.Show(
                    Window.GetWindow(this),
                    BeMusicSeeker.Properties.Resources.Msg_success_lr2_play_history_schema_install_or_repair,
                    BeMusicSeeker.Properties.Resources.Success,
                    MessageBoxButton.OK,
                    MessageBoxImage.Asterisk);
                if (viewModel.HasActiveLibraryProfile)
                {
                    viewModel.ReloadScoresOnly();
                }
                return;
            }

            DispatcherMessageBox.Show(
                Window.GetWindow(this),
                result.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation);
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

    private static async Task RefreshLr2PlayHistorySchemaStatusAsync(MainWindowViewModel.SettingDialogViewModel settingDialogViewModel, bool force)
    {
        string expectedScoreDbPath = settingDialogViewModel.Lr2PlayHistoryScoreDbPath;
        bool expectedOperationMode = settingDialogViewModel.OperationModeLR2DB;
        if (!ShouldRefreshLr2PlayHistorySchemaStatus(settingDialogViewModel, force, expectedScoreDbPath, expectedOperationMode))
        {
            return;
        }
        Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
            settingDialogViewModel.CheckLr2PlayHistorySchemaCore(expectedScoreDbPath, expectedOperationMode));
        if (expectedOperationMode == settingDialogViewModel.OperationModeLR2DB
            && string.Equals(expectedScoreDbPath, settingDialogViewModel.Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
        {
            settingDialogViewModel.ApplyLr2PlayHistorySchemaCheckResult(result);
        }
    }

    internal static bool ShouldResetSettingsOnCancel(MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
    {
        return settingDialogViewModel?.HasPendingSettingChanges() == true;
    }

    internal static bool ShouldRefreshLr2PlayHistorySchemaStatus(
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel,
        bool force,
        string expectedScoreDbPath,
        bool expectedOperationMode)
    {
        return settingDialogViewModel != null
            && (force || !settingDialogViewModel.HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, expectedOperationMode));
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

    private void buttonAddBmsSearchRootPathsClicked(object sender, RoutedEventArgs e)
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
        CommonOpenFileDialogInteractionMessageAction.SetInitialDirectory(dialog, settingDialogViewModel.SelectedBmsSearchRootPath);
        if (dialog.ShowDialog(Window.GetWindow(this)) == CommonFileDialogResult.Ok)
        {
            settingDialogViewModel.AddBmsSearchRootPaths(dialog.FileNames);
        }
    }

    private void buttonAddCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            return;
        }
        using var dialog = new CommonOpenFileDialog
        {
            Title = BeMusicSeeker.Properties.Resources.Playlist_output_additional,
            IsFolderPicker = true,
            EnsurePathExists = true,
            Multiselect = true
        };
        CommonOpenFileDialogInteractionMessageAction.SetInitialDirectory(
            dialog,
            settingDialogViewModel.SelectedCustomFolderAdditionalOutputBaseDir ?? settingDialogViewModel.LR2CustomFolderOutputDir);
        if (dialog.ShowDialog(Window.GetWindow(this)) == CommonFileDialogResult.Ok)
        {
            foreach (string directory in dialog.FileNames)
            {
                settingDialogViewModel.AddCustomFolderAdditionalOutputBaseDir(directory);
            }
        }
    }

    private void buttonRemoveCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            settingDialogViewModel.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void buttonRenameCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            settingDialogViewModel.RenameSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void bmsSearchRootPathListBoxDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedDirectories(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void bmsSearchRootPathListBoxDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedDirectories(e, out List<string> directories)
            && base.DataContext is MainWindowViewModel { settingDialog: { } settingDialogViewModel })
        {
            settingDialogViewModel.AddBmsSearchRootPaths(directories);
        }
        e.Handled = true;
    }

    private static bool TryGetDroppedDirectories(DragEventArgs e, out List<string> directories)
    {
        directories = [];
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
            || e.Data.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] paths
            || paths.Length == 0)
        {
            return false;
        }
        directories = [.. paths.Where(Directory.Exists)];
        return directories.Count == paths.Length;
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
