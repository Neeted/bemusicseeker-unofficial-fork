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
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>
/// アプリケーション設定を編集する WPF ユーザーコントロールです。
/// </summary>
public partial class SettingDialog : UserControl, IComponentConnector
{
    public static readonly DependencyProperty PlaybackPanelProperty = DependencyProperty.Register(
        nameof(PlaybackPanel),
        typeof(PlaybackPanelViewModel),
        typeof(SettingDialog),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PlaylistWorkspaceProperty = DependencyProperty.Register(
        nameof(PlaylistWorkspace),
        typeof(PlaylistWorkspaceViewModel),
        typeof(SettingDialog),
        new PropertyMetadata(null));

    internal Binding bindingLR2CustomFolderOutputDir;

    internal Binding bindingBMSInstallDir;

    public PlaybackPanelViewModel PlaybackPanel
    {
        get => (PlaybackPanelViewModel)GetValue(PlaybackPanelProperty);
        set => SetValue(PlaybackPanelProperty, value);
    }

    public PlaylistWorkspaceViewModel PlaylistWorkspace
    {
        get => (PlaylistWorkspaceViewModel)GetValue(PlaylistWorkspaceProperty);
        set => SetValue(PlaylistWorkspaceProperty, value);
    }

    private static void ThrowIfPickerFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private static void ThrowIfWindowDialogFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private void HideThisOverlay()
    {
        if (Window.GetWindow(this) is not MainWindow mainWindow)
        {
            throw new InvalidOperationException("Setting dialog is not hosted by MainWindow.");
        }

        mainWindow.HideOverlayDialog(this);
    }

    /// <summary>
    /// 設定ダイアログを初期化し、表示時に必要な遅延更新を登録します。
    /// </summary>
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

    private void SettingDialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            var stopwatch = Stopwatch.StartNew();
            RefreshAppearanceThemeSelection(settingDialogViewModel);
            settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();
            long handlerMs = stopwatch.ElapsedMilliseconds;
            string detail =
                "handlerMs=" + handlerMs
                + " operationModeLR2DB=" + settingDialogViewModel.OperationModeLR2DB.ToString().ToLowerInvariant()
                + " schemaStatus=" + (settingDialogViewModel.Lr2PlayHistorySchemaCheckResult?.Status.ToString() ?? "Unknown");
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                (Action)(() => LogSettingsDialogPerformance(
                "settings_dialog_open",
                stopwatch,
                detail)));
        }
    }

    internal void RefreshAppearanceThemeSelection(MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
    {
        comboBoxAppearanceTheme.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
        comboBoxAppearanceTheme.SelectedValue ??= settingDialogViewModel.AppearanceTheme;
    }

    private MainWindowViewModel.SettingDialogViewModel GetSettingDialogViewModel()
    {
        return base.DataContext as MainWindowViewModel.SettingDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
    }

    private MainWindowViewModel GetMainWindowViewModel()
    {
        return Window.GetWindow(this)?.DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException("Setting dialog host view model is unavailable.");
    }

    private async void buttonOKClick(object sender, RoutedEventArgs e)
    {
        await GetSettingDialogViewModel()
            .ApplySettingsAsync()
            .LoggingAndPropagate("buttonOKClick");
    }

    private void PickRootFolderForSetting(string propertyName, string selectedPath, string title = null)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                title,
                selectedPath,
                multiselect: false,
                ensurePathExists: true,
                owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, propertyName + " folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.SetRootFolderPathFromPicker(propertyName, result.FolderPath);
        }
    }

    private void PickDirectoryForSetting(string propertyName, string selectedPath, string title = null)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                title,
                selectedPath,
                multiselect: false,
                ensurePathExists: true,
                owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, propertyName + " directory picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.SetDirectoryPathFromPicker(propertyName, result.FolderPath);
        }
    }

    private void PickFileForSetting(string propertyName, string title, string fileName, string filter, string initialDirectory)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFilePickerResult result = new UiDialogCoordinator()
            .PickFileAsync(new UiFilePickerRequest(
                title,
                fileName,
                initialDirectory,
                filter,
                defaultExtension: null,
                multiselect: false,
                ensureFileExists: true,
                ensurePathExists: true,
                owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, propertyName + " file picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.SetFilePathFromPicker(propertyName, result.FileName);
        }
    }

    private static string PathToDirectoryOrSelf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? path : directory;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FirstNonEmptyOrDirectoryOfSecond(string first, string second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }
        return string.IsNullOrWhiteSpace(second) ? string.Empty : Path.GetDirectoryName(second) ?? string.Empty;
    }

    private void browseLr2RootPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.LR2RootPath), settingDialogViewModel.LR2RootPath);
    }

    private void browseLr2SongDbPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2SongDBPath),
            "song.db を開く",
            "song.db",
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2SongDBPath));
    }

    private void browseLr2ConfigPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2ConfigXmlPath),
            "config.xml を開く",
            "config.xml",
            "|config.xm?|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2ConfigXmlPath));
    }

    private void browseBeatorajaRootPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.BeatorajaRootPath), settingDialogViewModel.BeatorajaRootPath);
    }

    private void importBeatorajaTableUrlsButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        playlistWorkspace.StartBeatorajaTableUrlImport(settingDialogViewModel.BeatorajaRootPath);
    }

    private void browseStagefilePathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.StagefilePath),
            BeMusicSeeker.Properties.Resources.Open_image,
            null,
            "Image file|*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff|BMP file (*.bmp)|*.bmp|GIF file (*.gif)|*.gif|JPEG file (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG file (*.png)|*.png|TIFF file (*.tif;*.tiff)|*.tif;*.tiff",
            PathToDirectoryOrSelf(settingDialogViewModel.StagefilePath));
    }

    private void browseUbmplayPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.uBMplayPath),
            "uBMplay.exe を開く",
            "uBMplay.exe",
            "|uBMplay.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.uBMplayPath));
    }

    private void browseBmIdxViewPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.BMIIDXViewPath),
            "BMIIDXView2015.exe を開く",
            "BMIIDXView2015.exe",
            "|BMIIDXView2015*.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.BMIIDXViewPath));
    }

    private void browseEncoderExeDirButtonClick(object sender, RoutedEventArgs e)
    {
        PickDirectoryForSetting(nameof(MainWindowViewModel.SettingDialogViewModel.EncoderExeDir), null, BeMusicSeeker.Properties.Resources.Record_setting_encoder_dir_dialog);
    }

    private void browseLr2CustomFolderOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderOutputDir),
            FirstNonEmpty(settingDialogViewModel.LR2CustomFolderOutputDir, settingDialogViewModel.LR2RootPath));
    }

    private void browseLr2CustomFolderAsRootOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderAsRootOutputDir),
            FirstNonEmptyOrDirectoryOfSecond(settingDialogViewModel.LR2CustomFolderAsRootOutputDir, settingDialogViewModel.LR2CustomFolderOutputDir));
    }

    private void addBmsInstallDirButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                selectedPath: settingDialogViewModel.AvailableBMSDirectories?.FirstOrDefault(),
                multiselect: false,
                ensurePathExists: true,
                owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "BMS install directory picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.AddBmsSearchRootPathFromPicker(nameof(settingDialogViewModel.BMSInstallDir), result.FolderPath);
        }
    }

    private void browseLr2BackupPathButtonClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.LR2BackupPath), settingDialogViewModel.LR2BackupPath);
    }

    private void resetCustomTableAppearanceDefaultsButtonClick(object sender, RoutedEventArgs e)
    {
        GetSettingDialogViewModel().ResetCustomTableAppearanceDefaults();
    }

    private static void LogSettingsDialogPerformance(string action, Stopwatch stopwatch, string detail = null)
    {
        try
        {
            stopwatch?.Stop();
            Ribbit.Logging.NLogWrapper.FileLogger?.Info(
                (action ?? "settings_dialog")
                + " elapsedMs=" + (stopwatch?.ElapsedMilliseconds ?? 0L)
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }
        catch
        {
        }
    }

    private async void resyncLr2SongDbSyncDataButtonClicked(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = GetMainWindowViewModel();
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        if (!settingDialogViewModel.CanRequestLr2SongDbSyncDataResync)
        {
            if (viewModel.IsLibraryOperationInProgress)
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            return;
        }
        if (UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_song_db_sync_data_resync,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        HideThisOverlay();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await settingDialogViewModel.RequestLr2SongDbSyncAsync();
        }
        catch (Exception ex)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
        }
    }

    private async void detailTabItemBackupButtonClicked(object sender, RoutedEventArgs e)
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null)
        {
            return;
        }
        UiSaveFilePickerResult result = await new UiDialogCoordinator().PickSaveFileAsync(new UiSaveFilePickerRequest(
            "プレイリストデータを保存",
            "BeMusicSeeker_backup.sql",
            ".sql",
            "sqlファイル(*.sql)|*.sql",
            addExtension: true,
            Window.GetWindow(this)));
        ThrowIfPickerFailed(result.Status, result.Error, "Playlist backup save picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogOperationGrid.IsEnabled = false;
            try
            {
                await playlistWorkspace.BackupPlaylistAsync(result.FileName)
                    .Logging("detailTabItemBackupButtonClicked");
            }
            finally
            {
                settingDialogOperationGrid.IsEnabled = true;
            }
        }
    }

    private async void installOrRepairLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            return;
        }
        settingDialogOperationGrid.IsEnabled = false;
        try
        {
            await settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaAsync();
        }
        finally
        {
            settingDialogOperationGrid.IsEnabled = true;
        }
    }

    private async void uninstallLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            return;
        }
        settingDialogOperationGrid.IsEnabled = false;
        try
        {
            await settingDialogViewModel.UninstallLr2PlayHistorySchemaAsync();
        }
        finally
        {
            settingDialogOperationGrid.IsEnabled = true;
        }
    }

    private async void detailTabItemRestoreButtonClicked(object sender, RoutedEventArgs e)
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null
            || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), "プレイリストをバックアップから復元します。" + Environment.NewLine + "現在のプレイリストは全て削除され置き換えられます。" + Environment.NewLine + "バックアップデータが不正な場合元に戻せなくなるかもしれません。" + Environment.NewLine + Environment.NewLine + "続行しますか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        UiFilePickerResult result = await new UiDialogCoordinator().PickFileAsync(new UiFilePickerRequest(
            "プレイリストバックアップを開く",
            "BeMusicSeeker_backup.sql",
            filter: "sqlファイル(*.sql)|*.sql",
            defaultExtension: ".sql",
            owner: Window.GetWindow(this)));
        ThrowIfPickerFailed(result.Status, result.Error, "Playlist backup restore picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogOperationGrid.IsEnabled = false;
            await playlistWorkspace.RestorePlaylistBackupAsync(result.FileName)
                .Logging("detailTabItemRestoreButtonClicked");
            await base.Dispatcher.BeginInvoke((Action)delegate
            {
                UiDialogRoute.ShowMessageBox(Application.Current.MainWindow, "アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Question, MessageBoxResult.OK);
                Application.Current.MainWindow.Close();
            }, DispatcherPriority.Normal);
        }
    }

    private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            return;
        }
        bool closeAfterSuccess = false;
        settingDialogOperationGrid.IsEnabled = false;
        try
        {
            ApplicationDataUninstallResult result = await settingDialogViewModel.UninstallApplicationDataAsync();
            closeAfterSuccess = result.ShouldCloseApplication;
            if (closeAfterSuccess)
            {
                if (Window.GetWindow(this) is not Window window)
                {
                    throw new InvalidOperationException("Setting dialog is not hosted by a window.");
                }
                window.Close();
            }
        }
        finally
        {
            if (!closeAfterSuccess)
            {
                settingDialogOperationGrid.IsEnabled = true;
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
        if (base.DataContext is not MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Add_BMSDirectory,
            settingDialogViewModel.SelectedBmsSearchRootPath,
            multiselect: true,
            ensurePathExists: true,
            Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "BMS search root folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.AddBmsSearchRootPaths(result.FolderPaths);
        }
    }

    private void buttonAddCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Playlist_output_additional,
            settingDialogViewModel.SelectedCustomFolderAdditionalOutputBaseDir ?? settingDialogViewModel.LR2CustomFolderOutputDir,
            multiselect: true,
            ensurePathExists: true,
            Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "Custom folder additional output base picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            foreach (string directory in result.FolderPaths)
            {
                settingDialogViewModel.AddCustomFolderAdditionalOutputBaseDir(directory);
            }
        }
    }

    private void buttonRemoveCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void buttonRenameCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RenameSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void buttonAddPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            ShowPlayHistoryFolderDisplayPresetEditDialog(settingDialogViewModel, null);
        }
    }

    private void buttonRemovePlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RemoveSelectedPlayHistoryFolderDisplayPreset();
        }
    }

    private void buttonEditPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            ShowPlayHistoryFolderDisplayPresetEditDialog(settingDialogViewModel, settingDialogViewModel.SelectedPlayHistoryFolderDisplayPreset);
        }
    }

    private void ShowPlayHistoryFolderDisplayPresetEditDialog(
        MainWindowViewModel.SettingDialogViewModel settingDialogViewModel,
        PlayHistoryFolderDisplayPresetEditor preset)
    {
        if (settingDialogViewModel == null)
        {
            return;
        }
        UiWindowDialogResult<object> dialogResult = new UiDialogCoordinator()
            .ShowWindowAsync(new UiWindowDialogRequest<PlayHistoryFolderDisplayPresetEditDialog, object>(
                () => new PlayHistoryFolderDisplayPresetEditDialog(
                    settingDialogViewModel,
                    settingDialogViewModel.CreatePlayHistoryFolderDisplayPresetEditSession(preset)),
                _ => null,
                Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Play history folder display preset edit dialog");
    }

    private void bmsSearchRootPathListBoxDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedDirectories(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void bmsSearchRootPathListBoxDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedDirectories(e, out List<string> directories)
            && base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
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
        if (base.DataContext is MainWindowViewModel.SettingDialogViewModel settingDialogViewModel)
        {
            await settingDialogViewModel.RunAudioDeviceTestAsync();
        }
    }
}
