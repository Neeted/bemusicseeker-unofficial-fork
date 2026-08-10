using System;
using System.Collections.Generic;
using System.ComponentModel;
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
/// 設定編集セッションを MainWindow owner の modal Window として表示します。
/// </summary>
public partial class SettingsWindow : Window, IComponentConnector
{
    public static readonly DependencyProperty PlaybackPanelProperty = DependencyProperty.Register(
        nameof(PlaybackPanel),
        typeof(PlaybackPanelViewModel),
        typeof(SettingsWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PlaylistWorkspaceProperty = DependencyProperty.Register(
        nameof(PlaylistWorkspace),
        typeof(PlaylistWorkspaceViewModel),
        typeof(SettingsWindow),
        new PropertyMetadata(null));

    internal Binding bindingLR2CustomFolderOutputDir;

    internal Binding bindingBMSInstallDir;

    private SettingsWindowCloseReason closeReason;

    private bool presentationActivated;

    private bool presentationCloseObserved;

    private bool userCancellationQueued;

    private bool viewOperationInProgress;

    /// <summary>
    /// Gets the reason selected for the current window close operation.
    /// </summary>
    internal SettingsWindowCloseReason CloseReason => closeReason;

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

    /// <summary>
    /// 設定ウィンドウを初期化します。表示状態は Window lifecycle override で ViewModel へ通知します。
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        var entryAssembly = Assembly.GetEntryAssembly();
        string text = entryAssembly?.GetName().Version?.ToString() ?? string.Empty;
        string text2 = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        textBlockVerNum.Text = string.IsNullOrWhiteSpace(text2) ? text : text2;
        textBlockBuildNum.Text = "Build: " + text;
    }

    /// <summary>
    /// coordinator による modal 表示が content rendering まで到達した時点で、表示中だけ必要な presentation を有効化します。
    /// </summary>
    /// <param name="e">content rendering event data。</param>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (presentationActivated)
        {
            return;
        }

        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        presentationActivated = true;
        settingDialogViewModel.SetPresentationActive(true);
        var stopwatch = Stopwatch.StartNew();
        RefreshAppearanceThemeSelection(settingDialogViewModel);
        settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();
        long handlerMs = stopwatch.ElapsedMilliseconds;
        string detail =
            "handlerMs=" + handlerMs
            + " operationModeLR2DB=" + settingDialogViewModel.OperationModeLR2DB.ToString().ToLowerInvariant()
            + " schemaStatus=" + (settingDialogViewModel.Lr2PlayHistorySchemaStatusSnapshot?.Status.ToString() ?? "Unknown");
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            (Action)(() => LogSettingsDialogPerformance(
                "settings_dialog_open",
                stopwatch,
                detail)));
    }

    /// <summary>
    /// user close と programmatic close を分離し、Cancel 不可の編集状態を title bar や Alt+F4 で迂回させません。
    /// </summary>
    /// <param name="e">cancelable close event data。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (closeReason != SettingsWindowCloseReason.None)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        QueueUserCancellation();
        base.OnClosing(e);
    }

    /// <summary>
    /// modal lifetime の終了を ViewModel へ通知します。close reason に関係なく必ず presentation を解除します。
    /// </summary>
    /// <param name="e">closed event data。</param>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (presentationActivated && base.DataContext is SettingsDialogViewModel settingDialogViewModel)
            {
                presentationActivated = false;
                settingDialogViewModel.SetPresentationActive(false);
            }
        }
        finally
        {
            try
            {
                base.OnClosed(e);
            }
            finally
            {
                // Each presentation gets a fresh Window while the ViewModel is shared by the shell.
                // A closed Window must not retain a live binding graph into that shared edit session.
                DataContext = null;
            }
        }
    }

    /// <summary>
    /// ViewModel の Apply、Cancel、または通常 presentation close request を rollback なしで完了します。
    /// </summary>
    internal void CloseFromPresentation()
    {
        presentationCloseObserved = true;
        if (closeReason == SettingsWindowCloseReason.None)
        {
            closeReason = GetSettingDialogViewModel().IsEditCompletionInProgress
                ? SettingsWindowCloseReason.Apply
                : SettingsWindowCloseReason.Presentation;
        }
        Close();
    }

    /// <summary>
    /// owner shell の terminal shutdown に伴う close を Cancel guard と rollback の対象外にします。
    /// </summary>
    internal void CloseForOwnerShutdown()
    {
        closeReason = SettingsWindowCloseReason.OwnerShutdown;
        Close();
    }

    private void CloseForManualResync()
    {
        closeReason = SettingsWindowCloseReason.ManualResync;
        Close();
    }

    private void QueueUserCancellation()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        if (!settingDialogViewModel.IsEditCancellationEnabled || viewOperationInProgress || userCancellationQueued)
        {
            return;
        }

        userCancellationQueued = true;
        closeReason = SettingsWindowCloseReason.Cancel;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
        {
            userCancellationQueued = false;
            presentationCloseObserved = false;
            settingDialogViewModel.CancelCommand.Execute();
            if (!presentationCloseObserved)
            {
                closeReason = SettingsWindowCloseReason.None;
            }
        }));
    }

    private void SettingsWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        e.Handled = true;
        QueueUserCancellation();
    }

    private void BeginViewOperation()
    {
        if (viewOperationInProgress)
        {
            throw new InvalidOperationException("A settings window operation is already in progress.");
        }

        viewOperationInProgress = true;
        settingDialogOperationGrid.IsEnabled = false;
    }

    private void EndViewOperation()
    {
        settingDialogOperationGrid.IsEnabled = true;
        viewOperationInProgress = false;
    }

    /// <summary>
    /// Window が所有する非同期 operation と native close guard を同じ lifetime へ接続します。
    /// </summary>
    /// <param name="operation">設定 Window が表示中のまま完了を待つ operation。</param>
    /// <returns>operation の完了を表す Task。</returns>
    internal async Task RunViewOperationAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        BeginViewOperation();
        try
        {
            await operation();
        }
        finally
        {
            EndViewOperation();
        }
    }

    /// <summary>
    /// Runs the settings apply operation without authorizing a close until the ViewModel requests presentation completion.
    /// </summary>
    /// <param name="operation">The ViewModel-owned apply operation.</param>
    /// <returns>A task that completes after the apply operation and its presentation callback finish.</returns>
    internal async Task RunApplyOperationAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        closeReason = SettingsWindowCloseReason.None;
        presentationCloseObserved = false;
        try
        {
            await RunViewOperationAsync(operation);
        }
        finally
        {
            if (!presentationCloseObserved)
            {
                closeReason = SettingsWindowCloseReason.None;
            }
        }
    }

    private async Task<TResult> RunViewOperationAsync<TResult>(Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        BeginViewOperation();
        try
        {
            return await operation();
        }
        finally
        {
            EndViewOperation();
        }
    }

    internal void RefreshAppearanceThemeSelection(SettingsDialogViewModel settingDialogViewModel)
    {
        comboBoxAppearanceTheme.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
        comboBoxAppearanceTheme.SelectedValue ??= settingDialogViewModel.AppearanceTheme;
    }

    private SettingsDialogViewModel GetSettingDialogViewModel()
    {
        return base.DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
    }

    private void operationModeRadioButtonClick(object sender, RoutedEventArgs e)
    {
        bool requestedOperationMode = sender switch
        {
            _ when ReferenceEquals(sender, radioButtonUseLR2) => true,
            _ when ReferenceEquals(sender, radioButtonNotUseLR2) => false,
            _ => throw new InvalidOperationException("Unexpected operation mode selection source.")
        };
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        if (settingDialogViewModel.OperationModeLR2DB != requestedOperationMode)
        {
            settingDialogViewModel.OperationModeLR2DB = requestedOperationMode;
        }
    }

    private async void buttonOKClick(object sender, RoutedEventArgs e)
    {
        await RunApplyOperationAsync(() => GetSettingDialogViewModel()
            .ApplySettingsAsync()
            .LoggingAndPropagate("buttonOKClick"));
    }

    private void buttonCancelClick(object sender, RoutedEventArgs e)
    {
        closeReason = SettingsWindowCloseReason.Cancel;
        presentationCloseObserved = false;
        GetSettingDialogViewModel().CancelCommand.Execute();
        if (!presentationCloseObserved)
        {
            closeReason = SettingsWindowCloseReason.None;
        }
    }

    private void PickRootFolderForSetting(string propertyName, string selectedPath, string title = null)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                title,
                selectedPath,
                multiselect: false,
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
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                title,
                selectedPath,
                multiselect: false,
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
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
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
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.LR2RootPath), settingDialogViewModel.LR2RootPath);
    }

    private void browseLr2SongDbPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2SongDBPath),
            "song.db を開く",
            "song.db",
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2SongDBPath));
    }

    private void browseLr2ConfigPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2ConfigXmlPath),
            "config.xml を開く",
            "config.xml",
            "|config.xm?|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2ConfigXmlPath));
    }

    private void browseBeatorajaRootPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.BeatorajaRootPath), settingDialogViewModel.BeatorajaRootPath);
    }

    private void importBeatorajaTableUrlsButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        playlistWorkspace.StartBeatorajaTableUrlImport(settingDialogViewModel.BeatorajaRootPath);
    }

    private void browseStagefilePathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.StagefilePath),
            BeMusicSeeker.Properties.Resources.Open_image,
            null,
            "Image file|*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff|BMP file (*.bmp)|*.bmp|GIF file (*.gif)|*.gif|JPEG file (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG file (*.png)|*.png|TIFF file (*.tif;*.tiff)|*.tif;*.tiff",
            PathToDirectoryOrSelf(settingDialogViewModel.StagefilePath));
    }

    private void browseUbmplayPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.uBMplayPath),
            "uBMplay.exe を開く",
            "uBMplay.exe",
            "|uBMplay.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.uBMplayPath));
    }

    private void browseBmIdxViewPathButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.BMIIDXViewPath),
            "BMIIDXView2015.exe を開く",
            "BMIIDXView2015.exe",
            "|BMIIDXView2015*.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.BMIIDXViewPath));
    }

    private void browseEncoderExeDirButtonClick(object sender, RoutedEventArgs e)
    {
        PickDirectoryForSetting(nameof(SettingsDialogViewModel.EncoderExeDir), null, BeMusicSeeker.Properties.Resources.Record_setting_encoder_dir_dialog);
    }

    private void browseLr2CustomFolderOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderOutputDir),
            FirstNonEmpty(settingDialogViewModel.LR2CustomFolderOutputDir, settingDialogViewModel.LR2RootPath));
    }

    private void browseLr2CustomFolderAsRootOutputDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderAsRootOutputDir),
            FirstNonEmptyOrDirectoryOfSecond(settingDialogViewModel.LR2CustomFolderAsRootOutputDir, settingDialogViewModel.LR2CustomFolderOutputDir));
    }

    private void addBmsInstallDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                selectedPath: settingDialogViewModel.AvailableBMSDirectories?.FirstOrDefault(),
                multiselect: false,
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
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
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
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        if (!settingDialogViewModel.CanRequestLr2SongDbSyncDataResync)
        {
            if (settingDialogViewModel.IsLr2SongDbSyncDataResyncBlockedByLibraryOperation)
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
        Window owner = Owner ?? throw new InvalidOperationException("Settings window owner is unavailable.");
        CloseForManualResync();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await settingDialogViewModel.RequestLr2SongDbSyncAsync();
        }
        catch (Exception ex)
        {
            UiDialogRoute.ShowMessageBox(owner, BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
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
            await RunViewOperationAsync(() => playlistWorkspace.BackupPlaylistAsync(result.FileName)
                .Logging("detailTabItemBackupButtonClicked"));
        }
    }

    private async void installOrRepairLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        await RunViewOperationAsync(settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaAsync);
    }

    private async void uninstallLr2PlayHistorySchemaButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        await RunViewOperationAsync(settingDialogViewModel.UninstallLr2PlayHistorySchemaAsync);
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
            await RunViewOperationAsync(() => playlistWorkspace.RestorePlaylistBackupAsync(result.FileName)
                .Logging("detailTabItemRestoreButtonClicked"));
            await base.Dispatcher.BeginInvoke((Action)delegate
            {
                UiDialogRoute.ShowMessageBox(Application.Current.MainWindow, "アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Question, MessageBoxResult.OK);
                Application.Current.MainWindow.Close();
            }, DispatcherPriority.Normal);
        }
    }

    private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        ApplicationDataUninstallResult result = await RunViewOperationAsync(
            settingDialogViewModel.UninstallApplicationDataAsync);
        if (result.ShouldCloseApplication)
        {
            MainWindow owner = Owner as MainWindow
                ?? throw new InvalidOperationException("Settings window is not owned by MainWindow.");
            closeReason = SettingsWindowCloseReason.OwnerShutdown;
            Close();
            owner.Close();
        }
    }

    private void hyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        GetSettingDialogViewModel().ExternalShellGateway.Open(ExternalShellRequest.OpenUrl(e.Uri.ToString()));
        e.Handled = true;
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

    private void buttonAddBmsSearchRootPathsClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Add_BMSDirectory,
            settingDialogViewModel.SelectedBmsSearchRootPath,
            multiselect: true,
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
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Playlist_output_additional,
            settingDialogViewModel.SelectedCustomFolderAdditionalOutputBaseDir ?? settingDialogViewModel.LR2CustomFolderOutputDir,
            multiselect: true,
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
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void buttonRenameCustomFolderAdditionalOutputBaseClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RenameSelectedCustomFolderAdditionalOutputBaseDir();
        }
    }

    private void buttonAddPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            ShowPlayHistoryFolderDisplayPresetEditDialog(settingDialogViewModel, null);
        }
    }

    private void buttonRemovePlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            settingDialogViewModel.RemoveSelectedPlayHistoryFolderDisplayPreset();
        }
    }

    private void buttonEditPlayHistoryFolderDisplayPresetClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            ShowPlayHistoryFolderDisplayPresetEditDialog(settingDialogViewModel, settingDialogViewModel.SelectedPlayHistoryFolderDisplayPreset);
        }
    }

    private void ShowPlayHistoryFolderDisplayPresetEditDialog(
        SettingsDialogViewModel settingDialogViewModel,
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
            && base.DataContext is SettingsDialogViewModel settingDialogViewModel)
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
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            await settingDialogViewModel.RunAudioDeviceTestAsync();
        }
    }
}

/// <summary>
/// SettingsWindow の close が user cancellation、編集完了、別 workflow、shell shutdown のどこから要求されたかを表します。
/// </summary>
internal enum SettingsWindowCloseReason
{
    /// <summary>No close has been requested.</summary>
    None,

    /// <summary>Settings were applied successfully.</summary>
    Apply,

    /// <summary>The user completed the Cancel rollback route.</summary>
    Cancel,

    /// <summary>The ViewModel requested a close outside a view-originated completion handler.</summary>
    Presentation,

    /// <summary>The window closed before starting manual LR2 resynchronization.</summary>
    ManualResync,

    /// <summary>The owner shell is performing terminal shutdown.</summary>
    OwnerShutdown
}
