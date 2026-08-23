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
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views.Settings.Pages;

namespace BeMusicSeeker.Views;

/// <summary>
/// Executes the shell-owned terminal application-exit route for a settings window.
/// </summary>
/// <param name="settingsWindow">The settings window whose owner-bound close must happen first.</param>
internal delegate void SettingsWindowApplicationExitTerminal(SettingsWindow settingsWindow);

/// <summary>
/// 設定編集セッションを MainWindow owner の modal Window として表示します。
/// </summary>
public partial class SettingsWindow : ThemedWindow, IComponentConnector
{
    public static readonly DependencyProperty PlaybackPanelProperty = DependencyProperty.Register(
        nameof(PlaybackPanel),
        typeof(PlaybackPanelViewModel),
        typeof(SettingsWindow),
        new PropertyMetadata(null, playbackPanelChanged));

    public static readonly DependencyProperty PlaylistWorkspaceProperty = DependencyProperty.Register(
        nameof(PlaylistWorkspace),
        typeof(PlaylistWorkspaceViewModel),
        typeof(SettingsWindow),
        new PropertyMetadata(null));

    private SettingsWindowCloseReason closeReason;

    private bool presentationActivated;

    private bool presentationCloseObserved;

    private bool userCancellationQueued;

    private bool viewOperationInProgress;

    private readonly IUiDialogService dialogService;

    private readonly SettingsWindowApplicationExitTerminal applicationExitTerminal;

    private readonly UserControl[] categoryPages;

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
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
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

    private static void ThrowIfDialogFailed(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted
            or UiDialogStatus.Rejected
            or UiDialogStatus.CancelledByUser
            or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }

    /// <summary>
    /// 設定ウィンドウを初期化します。表示状態は Window lifecycle override で ViewModel へ通知します。
    /// </summary>
    public SettingsWindow()
        : this(
            new DwmNativeWindowTitleBarGateway(),
            new AppNativeWindowTitleBarThemeSource(),
            new UiDialogCoordinator(),
            ThrowApplicationExitTerminalUnavailable)
    {
    }

    /// <summary>Initializes the settings window with an injectable dialog boundary.</summary>
    internal SettingsWindow(IUiDialogService dialogService)
        : this(
            new DwmNativeWindowTitleBarGateway(),
            new AppNativeWindowTitleBarThemeSource(),
            dialogService,
            ThrowApplicationExitTerminalUnavailable)
    {
    }

    /// <summary>Initializes the settings window with an application-exit terminal supplied by the shell.</summary>
    /// <param name="applicationExitTerminal">The shell-owned terminal for a completed application-data uninstall.</param>
    internal SettingsWindow(SettingsWindowApplicationExitTerminal applicationExitTerminal)
        : this(
            new DwmNativeWindowTitleBarGateway(),
            new AppNativeWindowTitleBarThemeSource(),
            new UiDialogCoordinator(),
            applicationExitTerminal)
    {
    }

    /// <summary>Initializes the settings window with injectable dialogs and a shell-owned application-exit terminal.</summary>
    /// <param name="dialogService">The dialog boundary used by settings routes.</param>
    /// <param name="applicationExitTerminal">The shell-owned terminal for a completed application-data uninstall.</param>
    internal SettingsWindow(
        IUiDialogService dialogService,
        SettingsWindowApplicationExitTerminal applicationExitTerminal)
        : this(
            new DwmNativeWindowTitleBarGateway(),
            new AppNativeWindowTitleBarThemeSource(),
            dialogService,
            applicationExitTerminal)
    {
    }

    /// <summary>
    /// テスト可能な title-bar 境界を使って設定ウィンドウを初期化します。
    /// </summary>
    /// <param name="titleBarGateway">標準 Window chrome へ任意のテーマ属性を適用する境界。</param>
    /// <param name="titleBarThemeSource">現在の semantic palette と変更通知を供給する境界。</param>
    internal SettingsWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource)
        : this(
            titleBarGateway,
            titleBarThemeSource,
            new UiDialogCoordinator(),
            ThrowApplicationExitTerminalUnavailable)
    {
    }

    /// <summary>Initializes the settings window with testable title-bar and dialog boundaries.</summary>
    internal SettingsWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource,
        IUiDialogService dialogService)
        : this(
            titleBarGateway,
            titleBarThemeSource,
            dialogService,
            ThrowApplicationExitTerminalUnavailable)
    {
    }

    /// <summary>Initializes the settings window with all view and shell boundaries supplied by composition.</summary>
    /// <param name="titleBarGateway">The boundary that applies native title-bar attributes.</param>
    /// <param name="titleBarThemeSource">The semantic palette source for native title-bar attributes.</param>
    /// <param name="dialogService">The dialog boundary used by settings routes.</param>
    /// <param name="applicationExitTerminal">The shell-owned terminal for a completed application-data uninstall.</param>
    internal SettingsWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource,
        IUiDialogService dialogService,
        SettingsWindowApplicationExitTerminal applicationExitTerminal)
        : base(titleBarGateway, titleBarThemeSource)
    {
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.applicationExitTerminal = applicationExitTerminal
            ?? throw new ArgumentNullException(nameof(applicationExitTerminal));
        InitializeComponent();
        categoryPages =
        [
            new GeneralSettingsPage(),
            new AppearanceSettingsPage(),
            new PlaybackSettingsPage(),
            new AudioSettingsPage { PlaybackPanel = PlaybackPanel },
            new RecordingSettingsPage(),
            new PlaylistSettingsPage(),
            new InstallSettingsPage(),
            new BackupSettingsPage(),
            new AdvancedSettingsPage(),
            new AboutSettingsPage()
        ];
        settingsPageContent.Content = categoryPages[0];
    }

    private static void ThrowApplicationExitTerminalUnavailable(SettingsWindow settingsWindow)
    {
        throw new InvalidOperationException(
            "The settings window application-exit terminal is unavailable for this presentation.");
    }

    private static void playbackPanelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is SettingsWindow window
            && window.categoryPages?.ElementAtOrDefault(3) is AudioSettingsPage audioPage)
        {
            audioPage.PlaybackPanel = (PlaybackPanelViewModel)e.NewValue;
        }
    }

    /// <summary>
    /// coordinator による modal 表示が content rendering まで到達した時点で、表示中だけ必要な presentation を有効化します。
    /// </summary>
    /// <param name="e">content rendering event data。</param>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (presentationActivated || !IsVisible)
        {
            return;
        }

        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        presentationActivated = true;
        settingDialogViewModel.SetPresentationActive(true);
        var stopwatch = Stopwatch.StartNew();
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

    /// <summary>
    /// Refreshes the connected Appearance selector through its existing binding without creating a local value.
    /// </summary>
    /// <param name="settingDialogViewModel">The shared settings edit session expected on the connected page.</param>
    internal void RefreshAppearanceThemeSelection(SettingsDialogViewModel settingDialogViewModel)
    {
        AppearanceSettingsPage appearancePage = (AppearanceSettingsPage)categoryPages[1];
        if (appearancePage.IsLoaded && ReferenceEquals(appearancePage.DataContext, settingDialogViewModel))
        {
            appearancePage.RefreshThemeSelection();
        }
    }

    private SettingsDialogViewModel GetSettingDialogViewModel()
    {
        return base.DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
    }

    private void settingsNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (categoryPages == null
            || settingsNavigation.SelectedIndex < 0
            || settingsNavigation.SelectedIndex >= categoryPages.Length)
        {
            return;
        }

        settingsPageContent.Content = categoryPages[settingsNavigation.SelectedIndex];
        settingsPageScrollViewer.ScrollToVerticalOffset(0d);
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
        UiFolderPickerResult result = dialogService
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
        UiFolderPickerResult result = dialogService
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
        UiFilePickerResult result = dialogService
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

    /// <summary>Owns the LR2 root picker route for category pages.</summary>
    internal void HandleBrowseLr2RootPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.LR2RootPath), settingDialogViewModel.LR2RootPath);
    }

    /// <summary>Owns the LR2 song database picker route for category pages.</summary>
    internal void HandleBrowseLr2SongDbPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2SongDBPath),
            "song.db を開く",
            "song.db",
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2SongDBPath));
    }

    /// <summary>Picks a song database path for the LR2 advanced dialog without mutating the parent settings draft.</summary>
    internal Task<string> PickLr2AdvancedSongDbPathAsync(string currentPath) => PickLr2AdvancedFilePathAsync(
        "song.db を開く",
        "song.db",
        "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
        currentPath,
        "LR2 advanced song database picker");

    /// <summary>Owns the LR2 configuration picker route for category pages.</summary>
    internal void HandleBrowseLr2ConfigPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.LR2ConfigXmlPath),
            "config.xml を開く",
            "config.xml",
            "|config.xm?|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.LR2ConfigXmlPath));
    }

    /// <summary>Picks a configuration path for the LR2 advanced dialog without mutating the parent settings draft.</summary>
    internal Task<string> PickLr2AdvancedConfigPathAsync(string currentPath) => PickLr2AdvancedFilePathAsync(
        "config.xml を開く",
        "config.xml",
        "|config.xm?|すべてのファイル(*.*)|*.*",
        currentPath,
        "LR2 advanced configuration picker");

    private async Task<string> PickLr2AdvancedFilePathAsync(
        string title,
        string fileName,
        string filter,
        string currentPath,
        string routeName)
    {
        UiFilePickerResult result = await dialogService.PickFileAsync(new UiFilePickerRequest(
            title,
            fileName,
            PathToDirectoryOrSelf(currentPath),
            filter,
            defaultExtension: null,
            multiselect: false,
            ensureFileExists: true,
            ensurePathExists: true,
            owner: this));
        ThrowIfPickerFailed(result.Status, result.Error, routeName);
        return result.Status == UiDialogStatus.Accepted ? result.FileName : null;
    }

    /// <summary>Shows the owned advanced LR2 path editor over the current settings draft.</summary>
    /// <returns>A task that completes when the owned LR2 path editor closes.</returns>
    internal async Task HandleEditCustomLr2PathsAsync()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiWindowDialogResult<object> result = await dialogService.ShowWindowAsync(
            new UiWindowDialogRequest<Lr2AdvancedPathsDialog, object>(
                () => new Lr2AdvancedPathsDialog(settingDialogViewModel),
                _ => null,
                Window.GetWindow(this)));
        ThrowIfWindowDialogFailed(result.Status, result.Error, "LR2 advanced paths dialog");
    }

    /// <summary>Logs a settings presentation route failure and attempts one owner-bound localized notification.</summary>
    internal async Task HandleSettingsRouteFailureAsync(Exception exception, string routeName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string effectiveRouteName = string.IsNullOrWhiteSpace(routeName) ? "settings dialog route" : routeName;
        Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindow)).Error(exception, effectiveRouteName + " failed");
        try
        {
            UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK,
                owner: this));
            if (notification.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
            {
                Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindow)).Error(
                    notification.Exception,
                    effectiveRouteName + " failure notification was not shown: " + notification.Status);
            }
        }
        catch (Exception notificationException)
        {
            Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindow)).Error(
                notificationException,
                effectiveRouteName + " failure notification failed");
        }
    }

    /// <summary>Shows release notes through the injected modal dialog boundary without changing the settings draft.</summary>
    /// <returns>A task that completes when the owned release-notes window closes.</returns>
    internal async Task HandleShowReleaseNotesAsync()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiWindowDialogResult<object> result = await dialogService.ShowWindowAsync(
            new UiWindowDialogRequest<ReleaseNotesWindow, object>(
                () => new ReleaseNotesWindow { DataContext = settingDialogViewModel },
                _ => null,
                this));
        ThrowIfWindowDialogFailed(result.Status, result.Error, "Release notes dialog");
    }

    /// <summary>Owns the beatoraja root picker route for the General page.</summary>
    internal void HandleBrowseBeatorajaRootPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.BeatorajaRootPath), settingDialogViewModel.BeatorajaRootPath);
    }

    /// <summary>Starts the workspace-owned beatoraja table import requested by the General page.</summary>
    internal void HandleImportBeatorajaTableUrls()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        playlistWorkspace.StartBeatorajaTableUrlImport(settingDialogViewModel.BeatorajaRootPath);
    }

    /// <summary>Owns the stage image picker route for the Appearance page.</summary>
    internal void HandleBrowseStagefilePath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.StagefilePath),
            BeMusicSeeker.Properties.Resources.Open_image,
            null,
            "Image file|*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff|BMP file (*.bmp)|*.bmp|GIF file (*.gif)|*.gif|JPEG file (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG file (*.png)|*.png|TIFF file (*.tif;*.tiff)|*.tif;*.tiff",
            PathToDirectoryOrSelf(settingDialogViewModel.StagefilePath));
    }

    /// <summary>Owns the uBMplay executable picker route for the Playback page.</summary>
    internal void HandleBrowseUbmplayPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.uBMplayPath),
            "uBMplay.exe を開く",
            "uBMplay.exe",
            "|uBMplay.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.uBMplayPath));
    }

    /// <summary>Owns the BMIIDXView executable picker route for the Playback page.</summary>
    internal void HandleBrowseBmIdxViewPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.BMIIDXViewPath),
            "BMIIDXView2015.exe を開く",
            "BMIIDXView2015.exe",
            "|BMIIDXView2015*.exe|すべてのファイル(*.*)|*.*",
            PathToDirectoryOrSelf(settingDialogViewModel.BMIIDXViewPath));
    }

    /// <summary>Owns the encoder directory picker route for the Recording page.</summary>
    internal void HandleBrowseEncoderExecutableDirectory()
    {
        PickDirectoryForSetting(nameof(SettingsDialogViewModel.EncoderExeDir), null, BeMusicSeeker.Properties.Resources.Record_setting_encoder_dir_dialog);
    }

    /// <summary>Owns the standard custom-folder output picker route.</summary>
    internal void HandleBrowseLr2CustomFolderOutputDirectory()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderOutputDir),
            FirstNonEmpty(settingDialogViewModel.LR2CustomFolderOutputDir, settingDialogViewModel.LR2RootPath));
    }

    /// <summary>Owns the root custom-folder output picker route.</summary>
    internal void HandleBrowseLr2CustomFolderAsRootOutputDirectory()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(
            nameof(settingDialogViewModel.LR2CustomFolderAsRootOutputDir),
            FirstNonEmptyOrDirectoryOfSecond(settingDialogViewModel.LR2CustomFolderAsRootOutputDir, settingDialogViewModel.LR2CustomFolderOutputDir));
    }

    /// <summary>Owns the install-directory picker route for the Install page.</summary>
    internal void HandleAddBmsInstallDirectory()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        UiFolderPickerResult result = dialogService
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

    /// <summary>Owns the LR2 backup path picker route for the Backup page.</summary>
    internal void HandleBrowseLr2BackupPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickRootFolderForSetting(nameof(settingDialogViewModel.LR2BackupPath), settingDialogViewModel.LR2BackupPath);
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

    /// <summary>Closes with the manual-resync reason and runs the ViewModel-owned LR2 resync.</summary>
    /// <returns>A task that completes when the resync request completes.</returns>
    internal async Task HandleLr2SongDbSyncDataResyncAsync()
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

    /// <summary>Runs playlist backup inside the Window operation gate.</summary>
    /// <returns>A task that completes when backup finishes or the picker is cancelled.</returns>
    internal async Task HandlePlaylistBackupAsync()
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null)
        {
            return;
        }
        UiSaveFilePickerResult result = await dialogService.PickSaveFileAsync(new UiSaveFilePickerRequest(
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

    /// <summary>Runs LR2 play-history schema installation inside the Window operation gate.</summary>
    /// <returns>A task that completes when the schema operation finishes.</returns>
    internal async Task HandleInstallOrRepairLr2PlayHistorySchemaAsync()
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        await RunViewOperationAsync(settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaAsync);
    }

    /// <summary>Runs LR2 play-history schema removal inside the Window operation gate.</summary>
    /// <returns>A task that completes when the schema operation finishes.</returns>
    internal async Task HandleUninstallLr2PlayHistorySchemaAsync()
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        await RunViewOperationAsync(settingDialogViewModel.UninstallLr2PlayHistorySchemaAsync);
    }

    /// <summary>Runs playlist restore inside the Window operation gate and preserves terminal shutdown ordering.</summary>
    /// <returns>A task that completes after restore and any requested shutdown dispatch.</returns>
    internal async Task HandlePlaylistRestoreAsync()
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null)
        {
            return;
        }
        UiDialogResult confirmation = await dialogService.ConfirmAsync(new UiConfirmationRequest(
            "プレイリストをバックアップから復元します。" + Environment.NewLine + "現在のプレイリストは全て削除され置き換えられます。" + Environment.NewLine + "バックアップデータが不正な場合元に戻せなくなるかもしれません。" + Environment.NewLine + Environment.NewLine + "続行しますか？",
            "確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel,
            owner: Window.GetWindow(this)));
        ThrowIfDialogFailed(confirmation, "Playlist backup restore confirmation");
        if (!confirmation.IsAccepted)
        {
            return;
        }
        UiFilePickerResult result = await dialogService.PickFileAsync(new UiFilePickerRequest(
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
            Func<Task> closeAfterRestore = async () =>
            {
                UiDialogResult shutdownNotification = await dialogService.ShowMessageAsync(new UiMessageRequest(
                    "アプリケーションを終了します。",
                    "確認",
                    MessageBoxButton.OK,
                    MessageBoxImage.Question,
                    MessageBoxResult.OK,
                    owner: Application.Current.MainWindow));
                ThrowIfDialogFailed(shutdownNotification, "Playlist backup restore shutdown notification");
                Application.Current.MainWindow.Close();
            };
            await base.Dispatcher.InvokeAsync(closeAfterRestore, DispatcherPriority.Normal).Task.Unwrap();
        }
    }

    /// <summary>Runs application-data uninstall inside the Window operation gate.</summary>
    /// <returns>A task that completes after uninstall and any requested owner shutdown.</returns>
    internal async Task HandleApplicationDataUninstallAsync()
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        ApplicationDataUninstallResult result = await RunViewOperationAsync(
            settingDialogViewModel.UninstallApplicationDataAsync);
        if (result.ShouldCloseApplication)
        {
            applicationExitTerminal(this);
        }
    }

    /// <summary>Owns the multi-select BMS root picker route for the General page.</summary>
    /// <returns>A task that completes after the picker result is applied.</returns>
    internal async Task HandleAddBmsSearchRootPathsAsync()
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = await dialogService.PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Add_BMSDirectory,
            settingDialogViewModel.SelectedBmsSearchRootPath,
            multiselect: true,
            Window.GetWindow(this)));
        if (result == null)
        {
            throw new InvalidOperationException("BMS search root folder picker returned no result.");
        }
        ThrowIfPickerFailed(result.Status, result.Error, "BMS search root folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.AddBmsSearchRootPaths(result.FolderPaths);
        }
    }

    /// <summary>Owns the additional custom-folder output picker route.</summary>
    internal void HandleAddCustomFolderAdditionalOutputBase()
    {
        if (base.DataContext is not SettingsDialogViewModel settingDialogViewModel)
        {
            return;
        }
        UiFolderPickerResult result = dialogService.PickFolderAsync(new UiFolderPickerRequest(
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

    /// <summary>Opens a new play-history display preset editor owned by this Window.</summary>
    internal void HandleAddPlayHistoryFolderDisplayPreset()
    {
        if (base.DataContext is SettingsDialogViewModel settingDialogViewModel)
        {
            ShowPlayHistoryFolderDisplayPresetEditDialog(settingDialogViewModel, null);
        }
    }

    /// <summary>Opens the selected play-history display preset editor owned by this Window.</summary>
    internal void HandleEditPlayHistoryFolderDisplayPreset()
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
        UiWindowDialogResult<object> dialogResult = dialogService
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
