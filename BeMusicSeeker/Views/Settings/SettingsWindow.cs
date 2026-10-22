using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings.Pages;

namespace BeMusicSeeker.Views;

/// <summary>
/// 設定画面の処理が完了した後、shellが所有する終了経路を実行します。
/// </summary>
/// <param name="settingsWindow">所有者の終了より先に閉じる設定画面。</param>
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

    private TaskCompletionSource viewOperationCompletion;

    private readonly IUiDialogService dialogService;

    private readonly SettingsWindowApplicationExitTerminal applicationExitTerminal;

    private readonly UserControl[] categoryPages;

    private long? presentationRequestedTimestamp;

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

    /// <summary>復元・アプリデータ削除の完了後にshellの終了端末を呼ぶ設定画面を初期化します。</summary>
    /// <param name="applicationExitTerminal">復元またはアプリデータ削除が完了した後のshell終了経路。</param>
    internal SettingsWindow(SettingsWindowApplicationExitTerminal applicationExitTerminal)
        : this(
            new DwmNativeWindowTitleBarGateway(),
            new AppNativeWindowTitleBarThemeSource(),
            new UiDialogCoordinator(),
            applicationExitTerminal)
    {
    }

    /// <summary>注入されたdialogと、復元・アプリデータ削除後のshell終了端末を使って設定画面を初期化します。</summary>
    /// <param name="dialogService">設定操作のpicker・確認・結果通知を実行する境界。</param>
    /// <param name="applicationExitTerminal">復元またはアプリデータ削除が完了した後のshell終了経路。</param>
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

    /// <summary>構成から渡された表示・dialog・終了境界で設定画面を初期化します。</summary>
    /// <param name="titleBarGateway">標準タイトルバー属性を適用する境界。</param>
    /// <param name="titleBarThemeSource">標準タイトルバーのsemantic paletteを供給する境界。</param>
    /// <param name="dialogService">設定操作のpicker・確認・結果通知を実行する境界。</param>
    /// <param name="applicationExitTerminal">復元またはアプリデータ削除が完了した後のshell終了経路。</param>
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
        var construction = Stopwatch.StartNew();
        InitializeComponent();
        DataContextChanged += SettingsDataContextChanged;
        AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler(settingsSliderDragCompleted));
        categoryPages = new UserControl[11];
        settingsPageContent.Content = GetOrCreateCategoryPage(0);
        LogSettingsDialogPerformance("settings_window_construct", construction);
    }

    /// <summary>表示portが受け取った今回の表示要求時刻を、最初の描画の計測へ接続します。</summary>
    /// <param name="timestamp">Stopwatch.GetTimestampで捕捉した表示要求時刻。</param>
    internal void SetPresentationRequestedTimestamp(long timestamp)
        => presentationRequestedTimestamp = timestamp;

    private UserControl GetOrCreateCategoryPage(int index)
    {
        if (categoryPages[index] is UserControl existing)
        {
            return existing;
        }

        var construction = Stopwatch.StartNew();
        LogSettingsDialogPerformance("settings_page_construct_start", null, "category=" + index);
        try
        {
            UserControl page = index switch
            {
                0 => new GeneralSettingsPage(),
                1 => new AppearanceSettingsPage(),
                2 => new PlaybackSettingsPage(),
                3 => new AudioSettingsPage { PlaybackPanel = PlaybackPanel },
                4 => new RecordingSettingsPage(),
                5 => new PlaylistSettingsPage(),
                6 => new InstallSettingsPage(),
                7 => new BackupSettingsPage(),
                8 => new AdvancedSettingsPage(),
                9 => new RightClickSettingsPage(),
                10 => new AboutSettingsPage(),
                _ => throw new ArgumentOutOfRangeException(nameof(index))
            };
            // 非表示の間も同じ編集元を保ち、再接続による未確定入力の上書きを防ぎます。
            page.SetBinding(DataContextProperty, new Binding(nameof(DataContext))
            {
                Source = this,
                Mode = BindingMode.OneWay
            });
            categoryPages[index] = page;
            return page;
        }
        finally
        {
            LogSettingsDialogPerformance("settings_page_construct_end", construction, "category=" + index);
        }
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
        long firstRenderTimestamp = Stopwatch.GetTimestamp();
        base.OnContentRendered(e);
        if (presentationActivated || !IsVisible)
        {
            return;
        }

        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        presentationActivated = true;
        settingDialogViewModel.SetPresentationActive(true);
        if (presentationRequestedTimestamp is long requestedTimestamp)
        {
            LogSettingsDialogPerformance(
                "settings_dialog_first_render",
                null,
                "requestToFirstRenderMs=" + Stopwatch.GetElapsedTime(requestedTimestamp, firstRenderTimestamp).TotalMilliseconds);
        }
        var stopwatch = Stopwatch.StartNew();
        settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();
        LogSettingsDialogPerformance("settings_dialog_schema_presentation", stopwatch);
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
            PlaybackPanel?.CompleteVolumeAdjustment();
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
                // 非表示のページも含めて、共有編集セッションとWindowへの接続を閉鎖時に解放します。
                DataContext = null;
                foreach (UserControl page in categoryPages.OfType<UserControl>())
                {
                    BindingOperations.ClearBinding(page, DataContextProperty);
                    page.DataContext = null;
                }
            }
        }
    }

    /// <summary>
    /// 初期化完了または通常表示担当の終了要求で画面を閉じます。保存済み項目は復元しません。
    /// </summary>
    internal void CloseFromPresentation()
    {
        presentationCloseObserved = true;
        if (closeReason == SettingsWindowCloseReason.None)
        {
            closeReason = GetSettingDialogViewModel().IsEditCompletionInProgress
                ? SettingsWindowCloseReason.InitializationCompleted
                : SettingsWindowCloseReason.Presentation;
        }
        Close();
    }

    /// <summary>
    /// shell終了では表示を失効させ、通常閉鎖のguardを通さず閉じます。受理Taskは既存の終了担当が別に待ちます。
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
        closeReason = SettingsWindowCloseReason.UserClose;
        Dispatcher.InvokeAsync((Func<Task>)(() =>
        {
            userCancellationQueued = false;
            return CompleteUserCancellationAsync(settingDialogViewModel);
        }), DispatcherPriority.Normal).Task.Unwrap().ObserveFault("settings user cancellation");
    }

    private async Task CompleteUserCancellationAsync(SettingsDialogViewModel settingDialogViewModel)
    {
        presentationCloseObserved = false;
        try
        {
            settingDialogViewModel.CloseCommand.Execute();
        }
        catch (Exception exception)
        {
            // 失敗でCloseの許可を残さず、開始済みの取消処理が終わった後で一回だけ通知します。
            closeReason = SettingsWindowCloseReason.None;
            await HandleSettingsRouteFailureAsync(exception, "settings cancellation");
        }
        finally
        {
            if (!presentationCloseObserved)
            {
                closeReason = SettingsWindowCloseReason.None;
            }
        }
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
        viewOperationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndViewOperation()
    {
        viewOperationInProgress = false;
        viewOperationCompletion?.TrySetResult();
    }

    /// <summary>所有者終了が、設定画面から開始した受理済み処理と通知の実終端を回収します。</summary>
    internal Task WaitForOperationCompletionAsync() => viewOperationCompletion?.Task ?? Task.CompletedTask;

    /// <summary>
    /// 非同期処理の完了と利用者の閉じる操作の抑止を接続します。
    /// 保存成功による表示終了後も処理を追跡し、破棄済み画面の操作受付は再開しません。
    /// </summary>
    /// <param name="operation">画面が閉じられた場合も完了まで追跡する処理。</param>
    /// <returns>処理と画面側の後片付けの完了を表すタスク。</returns>
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
    /// 確定済み環境の初期化と必須公開を追跡し、成功した表示終了要求でのみ閉じます。
    /// </summary>
    /// <param name="operation">ViewModel が所有する初期化・必須公開処理。</param>
    /// <returns>設定の反映と画面側の後片付けの完了を表すタスク。</returns>
    internal async Task RunInitializationOperationAsync(Func<Task> operation)
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
    /// 作成済み外観ページの既存Bindingを更新し、未訪問ページは生成しません。
    /// </summary>
    /// <param name="settingDialogViewModel">接続されたページが共有する設定編集セッション。</param>
    internal void RefreshAppearanceThemeSelection(SettingsDialogViewModel settingDialogViewModel)
    {
        if (categoryPages[1] is AppearanceSettingsPage appearancePage
            && appearancePage.IsLoaded && ReferenceEquals(appearancePage.DataContext, settingDialogViewModel))
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

        settingsPageContent.Content = GetOrCreateCategoryPage(settingsNavigation.SelectedIndex);
        settingsPageScrollViewer.ScrollToVerticalOffset(0d);
    }

    private async void buttonOKClick(object sender, RoutedEventArgs e)
    {
        if (viewOperationInProgress || !GetSettingDialogViewModel().CanStartEnvironment) { return; }
        try
        {
            await RunInitializationOperationAsync(() => GetSettingDialogViewModel().StartConfirmedEnvironmentAsync());
        }
        catch (Exception exception)
        {
            await HandleSettingsRouteFailureAsync(exception, "settings apply");
        }
    }

    private async void buttonCancelClick(object sender, RoutedEventArgs e)
    {
        closeReason = SettingsWindowCloseReason.UserClose;
        await CompleteUserCancellationAsync(GetSettingDialogViewModel());
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

    /// <summary>Owns the executable picker route for the right-click program-action page.</summary>
    internal void HandleBrowseRightClickProgramExecutable()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        RightClickProgramActionEditorRow selectedAction = settingDialogViewModel
            .RightClickActionSettingsEditor
            .SelectedProgramAction;
        if (selectedAction == null)
        {
            return;
        }

        UiFilePickerResult result = dialogService
            .PickFileAsync(new UiFilePickerRequest(
                BeMusicSeeker.Properties.Resources.RightClick_executable_picker_title,
                string.IsNullOrWhiteSpace(selectedAction.ExecutablePath)
                    ? string.Empty
                    : Path.GetFileName(selectedAction.ExecutablePath),
                PathToDirectoryOrSelf(selectedAction.ExecutablePath),
                BeMusicSeeker.Properties.Resources.RightClick_executable_filter,
                defaultExtension: null,
                multiselect: false,
                ensureFileExists: true,
                ensurePathExists: true,
                owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "right-click program executable picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            selectedAction.SetExecutablePathFromPicker(result.FileName);
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

    /// <summary>実radio入力のモード反映を同じWindowの操作Taskで追跡します。</summary>
    internal Task HandleOperationModeSelectionAsync(bool value)
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsEnvironmentSelectionEnabled) { return Task.CompletedTask; }
        return RunViewOperationAsync(() => settings.SelectOperationModeAsync(value));
    }

    /// <summary>LR2 root選択の標準組を解決し、その明示入力だけを自動反映します。</summary>
    internal async Task HandleBrowseLr2RootPathAsync()
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsEnvironmentSelectionEnabled) { return; }
        await RunViewOperationAsync(async () =>
        {
            UiFolderPickerResult result = await dialogService.PickFolderAsync(new UiFolderPickerRequest(null, settings.LR2RootPath, false, this));
            ThrowIfPickerFailed(result.Status, result.Error, "LR2 root folder picker");
            if (result.Status != UiDialogStatus.Accepted) { return; }
            await settings.SelectLr2RootAsync(result.FolderPath);
        });
    }

    /// <summary>LR2 の song.db を個別指定する前に注意を確認し、受理された場合だけファイル選択を開きます。</summary>
    internal Task HandleBrowseLr2SongDbPathAsync()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        return HandleBrowseLr2IndividualFilePathAsync(
            settingDialogViewModel,
            nameof(settingDialogViewModel.LR2SongDBPath),
            string.Format(BeMusicSeeker.Properties.Resources.Open_file_title_format, "song.db"),
            "song.db",
            "song.db (*.db)|*.db|" + BeMusicSeeker.Properties.Resources.All_file_exts,
            settingDialogViewModel.LR2SongDBPath,
            "LR2 song database picker");
    }

    /// <summary>LR2 の config.xml / config.xmh を個別指定する前に注意を確認し、受理された場合だけファイル選択を開きます。</summary>
    internal Task HandleBrowseLr2ConfigPathAsync()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        return HandleBrowseLr2IndividualFilePathAsync(
            settingDialogViewModel,
            nameof(settingDialogViewModel.LR2ConfigXmlPath),
            string.Format(BeMusicSeeker.Properties.Resources.Open_file_title_format, "config.xml"),
            "config.xml",
            "|config.xm?|" + BeMusicSeeker.Properties.Resources.All_file_exts,
            settingDialogViewModel.LR2ConfigXmlPath,
            "LR2 configuration picker");
    }

    private async Task HandleBrowseLr2IndividualFilePathAsync(
        SettingsDialogViewModel settingDialogViewModel,
        string propertyName,
        string title,
        string fileName,
        string filter,
        string currentPath,
        string routeName)
    {
        UiDialogResult confirmation = await dialogService.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Settings_lr2_individual_path_warning,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            owner: this));
        ThrowIfDialogFailed(confirmation, routeName + " confirmation");
        if (!confirmation.IsAccepted)
        {
            return;
        }

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
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.SetFilePathFromPicker(propertyName, result.FileName);
        }
    }

    /// <summary>未処理の設定操作失敗を元原因・経路付きで診断し、所有画面への結果通知を一回だけ試みます。通知・診断先の失敗は局所で終えます。</summary>
    internal async Task HandleSettingsRouteFailureAsync(Exception exception, string routeName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string effectiveRouteName = string.IsNullOrWhiteSpace(routeName) ? "settings dialog route" : routeName;
        TryLogSettingsRouteFailure(exception, effectiveRouteName);
        await ShowSettingsMessageBestEffortAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK,
            owner: this), effectiveRouteName + " failure notification");
    }

    private async Task ShowSettingsMessageBestEffortAsync(UiMessageRequest request, string routeName)
    {
        try
        {
            UiDialogResult notification = await dialogService.ShowMessageAsync(request);
            if (notification == null || notification.Status is not (UiDialogStatus.Accepted
                or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
            {
                TryLogSettingsRouteFailure(notification?.Exception,
                    routeName + " was not shown: " + (notification?.Status.ToString() ?? "no result"));
            }
        }
        catch (Exception exception)
        {
            TryLogSettingsRouteFailure(exception, routeName);
        }
    }

    private static void TryLogSettingsRouteFailure(Exception exception, string routeName)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindow)).Error(exception, routeName + " failed");
        }
        catch
        {
            // 診断の失敗を再通知せず、元の操作結果と画面の終端を保ちます。
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

    /// <summary>beatorajaのroot選択と成立した局所組の必要処理を同じWindowで待ちます。</summary>
    internal async Task HandleBrowseBeatorajaRootPathAsync()
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsBeatorajaSelectionEnabled) { return; }
        await RunViewOperationAsync(async () =>
        {
            UiFolderPickerResult result = await dialogService.PickFolderAsync(new UiFolderPickerRequest(null, settings.BeatorajaRootPath, false, this));
            ThrowIfPickerFailed(result.Status, result.Error, "beatoraja root folder picker");
            if (result.Status != UiDialogStatus.Accepted) { return; }
            string previous = settings.BeatorajaRootPath;
            settings.SetRootFolderPathFromPicker(nameof(settings.BeatorajaRootPath), result.FolderPath);
            if (string.Equals(previous, settings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.FolderPath, settings.BeatorajaRootPath, StringComparison.Ordinal)
                || !settings.IsScoreSelectionValid) { return; }
            await settings.CommitScoreSettingsAsync();
        });
    }

    /// <summary>Starts the workspace-owned beatoraja table import requested by the General page.</summary>
    internal void HandleImportBeatorajaTableUrls()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        playlistWorkspace.StartBeatorajaTableUrlImport(settingDialogViewModel.ConfirmedBeatorajaRootPath);
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
            string.Format(BeMusicSeeker.Properties.Resources.Open_file_title_format, "uBMplay.exe"),
            "uBMplay.exe",
            "|uBMplay.exe|" + BeMusicSeeker.Properties.Resources.All_file_exts,
            PathToDirectoryOrSelf(settingDialogViewModel.uBMplayPath));
    }

    /// <summary>Owns the BMIIDXView executable picker route for the Playback page.</summary>
    internal void HandleBrowseBmIdxViewPath()
    {
        SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();
        PickFileForSetting(
            nameof(settingDialogViewModel.BMIIDXViewPath),
            string.Format(BeMusicSeeker.Properties.Resources.Open_file_title_format, "BMIIDXView2015.exe"),
            "BMIIDXView2015.exe",
            "|BMIIDXView2015*.exe|" + BeMusicSeeker.Properties.Resources.All_file_exts,
            PathToDirectoryOrSelf(settingDialogViewModel.BMIIDXViewPath));
    }

    /// <summary>Owns the encoder directory picker route for the Recording page.</summary>
    internal void HandleBrowseEncoderExecutableDirectory()
    {
        PickDirectoryForSetting(nameof(SettingsDialogViewModel.EncoderExeDir), null, BeMusicSeeker.Properties.Resources.Record_setting_encoder_dir_dialog);
    }

    /// <summary>通常出力先の実選択と既存確認・保存・移行の終端を所有します。</summary>
    internal Task HandleBrowseLr2CustomFolderOutputDirectory()
        => SelectOutputDirectoryAsync(nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir), "normal");

    /// <summary>ROOT出力先の実選択と既存確認・保存・移行の終端を所有します。</summary>
    internal Task HandleBrowseLr2CustomFolderAsRootOutputDirectory()
        => SelectOutputDirectoryAsync(nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir), "root");

    private async Task SelectOutputDirectoryAsync(string property, string section)
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsEditCompletionEnabled) { return; }
        await RunViewOperationAsync(async () =>
        {
            string initial = section == "normal" ? FirstNonEmpty(settings.LR2CustomFolderOutputDir, settings.LR2RootPath)
                : FirstNonEmptyOrDirectoryOfSecond(settings.LR2CustomFolderAsRootOutputDir, settings.LR2CustomFolderOutputDir);
            UiFolderPickerResult result = await dialogService.PickFolderAsync(new UiFolderPickerRequest(null, initial, false, this));
            ThrowIfPickerFailed(result.Status, result.Error, property + " folder picker");
            if (result.Status != UiDialogStatus.Accepted) { return; }
            string previous = section == "normal" ? settings.LR2CustomFolderOutputDir : settings.LR2CustomFolderAsRootOutputDir;
            settings.SetRootFolderPathFromPicker(property, result.FolderPath);
            string candidate = section == "normal" ? settings.LR2CustomFolderOutputDir : settings.LR2CustomFolderAsRootOutputDir;
            // 無効選択で保持した以前の入力や同値選択から、失敗済み要求を暗黙に再開しません。
            if (string.Equals(previous, candidate, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate, result.FolderPath, StringComparison.Ordinal)) { return; }
            await settings.CommitOutputSettingsAsync(section);
        });
    }

    /// <summary>Owns the install-directory picker route for the Install page.</summary>
    internal void HandleAddBmsInstallDirectory()
    {
        settingsNavigation.SelectedIndex = 0;

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
            TryLogSettingsRouteFailure(ex, "LR2 manual resync");
            await ShowSettingsMessageBestEffortAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK,
                owner: owner), "LR2 manual resync failure notification");
        }
    }

    /// <summary>画面の操作gate内で実バックアップと一回の結果通知を待ちます。処理済みの失敗を表示のために再送出しません。</summary>
    /// <returns>書込み・後片付け・通知、またはpicker取消の終端を表すTask。</returns>
    internal async Task HandlePlaylistBackupAsync()
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null)
        {
            return;
        }
        UiSaveFilePickerResult result = await dialogService.PickSaveFileAsync(new UiSaveFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Save_PlaylistBackup,
            "BeMusicSeeker_backup.sql",
            ".sql",
            BeMusicSeeker.Properties.Resources.Sql_file_exts,
            addExtension: true,
            Window.GetWindow(this)));
        ThrowIfPickerFailed(result.Status, result.Error, "Playlist backup save picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            await RunViewOperationAsync(() => playlistWorkspace.BackupPlaylistAsync(result.FileName));
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

    /// <summary>実復元と必要公開・出力が完了した場合だけshellの終了へ渡します。拒否・失敗では閉じず、任意通知の失敗は終了を妨げません。</summary>
    /// <returns>操作gate、復元、通知、必要な閉鎖の終端までを追跡するTask。</returns>
    internal async Task HandlePlaylistRestoreAsync()
    {
        PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspace
            ?? throw new InvalidOperationException("Playlist workspace is unavailable.");
        if (playlistWorkspace.PlaylistTreeTables == null)
        {
            return;
        }
        UiDialogResult confirmation = await dialogService.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Confirm_RestorePlaylistBackup,
            BeMusicSeeker.Properties.Resources.Confirm,
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
            BeMusicSeeker.Properties.Resources.Open_PlaylistBackup,
            "BeMusicSeeker_backup.sql",
            filter: BeMusicSeeker.Properties.Resources.Sql_file_exts,
            defaultExtension: ".sql",
            owner: Window.GetWindow(this)));
        ThrowIfPickerFailed(result.Status, result.Error, "Playlist backup restore picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            await RunViewOperationAsync(async () =>
            {
                if (!await playlistWorkspace.RestorePlaylistBackupAsync(result.FileName))
                {
                    return;
                }
                await ShowSettingsMessageBestEffortAsync(new UiMessageRequest(
                    BeMusicSeeker.Properties.Resources.Msg_ApplicationWillExit,
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxButton.OK,
                    MessageBoxImage.Question,
                    MessageBoxResult.OK,
                    owner: Owner ?? this), "playlist restore shutdown notification");
                // 必要公開・出力が成功した場合だけshellへ渡します。任意の案内失敗では終了を止めません。
                applicationExitTerminal(this);
            });
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

    /// <summary>追加出力先の実選択と必要な登録変更だけを確定し、終端を所有します。</summary>
    internal async Task HandleAddCustomFolderAdditionalOutputBase()
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsEditCompletionEnabled) { return; }
        await RunViewOperationAsync(async () =>
        {
            UiFolderPickerResult result = await dialogService.PickFolderAsync(new UiFolderPickerRequest(
                BeMusicSeeker.Properties.Resources.Playlist_output_additional,
                settings.SelectedCustomFolderAdditionalOutputBaseDir ?? settings.LR2CustomFolderOutputDir, true, this));
            ThrowIfPickerFailed(result.Status, result.Error, "Custom folder additional output base picker");
            if (result.Status != UiDialogStatus.Accepted) { return; }
            string[] previous = settings.CustomFolderAdditionalOutputBaseDirList.ToArray();
            foreach (string directory in result.FolderPaths) { settings.AddCustomFolderAdditionalOutputBaseDir(directory); }
            if (previous.SequenceEqual(settings.CustomFolderAdditionalOutputBaseDirList, StringComparer.OrdinalIgnoreCase)) { return; }
            await settings.CommitOutputSettingsAsync("additional");
        });
    }

    /// <summary>追加先の削除・改名操作とその登録変更の実Taskを追跡します。</summary>
    internal async Task ConfirmAdditionalOutputOperationAsync(bool rename)
    {
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        if (viewOperationInProgress || !settings.IsEditCompletionEnabled) { return; }
        await RunViewOperationAsync(async () =>
        {
            string[] previous = settings.CustomFolderAdditionalOutputBaseDirList.ToArray();
            if (rename) { settings.RenameSelectedCustomFolderAdditionalOutputBaseDir(); }
            else { settings.RemoveSelectedCustomFolderAdditionalOutputBaseDir(); }
            if (previous.SequenceEqual(settings.CustomFolderAdditionalOutputBaseDirList, StringComparer.OrdinalIgnoreCase)) { return; }
            await settings.CommitOutputSettingsAsync("additional");
        });
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

    /// <summary>確定済み環境の初期化と必須公開が完了しました。</summary>
    InitializationCompleted,

    /// <summary>利用者が保存済み値を保って閉じました。</summary>
    UserClose,

    /// <summary>The ViewModel requested a close outside a view-originated completion handler.</summary>
    Presentation,

    /// <summary>The window closed before starting manual LR2 resynchronization.</summary>
    ManualResync,

    /// <summary>The owner shell is performing terminal shutdown.</summary>
    OwnerShutdown
}
