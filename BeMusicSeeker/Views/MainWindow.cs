using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet.EventListeners;
using NLog;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Net;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Views;

/// <summary>
/// アプリケーションのメインウィンドウを表すクラスです。
/// UIの初期化、主要なイベントハンドリング（ドラッグ＆ドロップ、ウィンドウ状態の変更、閉じる処理など）、
/// および非同期のアップデートチェッカー等のグローバルな制御を統括します。
/// </summary>
public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindow");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly UpdateCheckService updateCheckService = new(AppHttpClient.Create(5000));

    private static readonly UpdateDownloadService updateDownloadService = new();

    private readonly PlaylistExternalPackageLookupService playlistExternalPackageLookupService = PlaylistExternalPackageLookupService.CreateDefault();

    private const long DownloadAndInstallSizeLimitBytes = 536870912L;

    private const int SelectedPlaylistUrlDownloadLargeSelectionWarningThreshold = 50;

    private const int SharedDownloadPageResolverMaxBytes = 2097152;

    private static readonly MethodInfo playlistTreeBringIndexIntoViewMethod = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(System.Windows.Controls.VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic);

    private bool _isClosingOrClosed;

    private bool _shutdownPrepared;

    private bool _shutdownPreparationRunning;

    private readonly object shutdownPreparationLock = new();

    private Task<ShutdownPreparationResult> shutdownPreparationTask;

    private FrameworkElement activeOverlayDialog;

    private MainWindowViewModel subscribedViewModel;

    private async Task RunProgressUntilTaskCompletesAsync(Task task, CancellationTokenSource cancellationTokenSource, string title, string label, Action<UiProgressContext> reportProgress)
    {
        UiProgressResult progressResult = await new UiDialogCoordinator().RunWithProgressAsync(
            new UiProgressRequest(title, label, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false), this),
            context =>
            {
                while (!task.IsCompleted)
                {
                    try
                    {
                        reportProgress(context);
                    }
                    catch
                    {
                        cancellationTokenSource.Cancel();
                        WaitForTaskCompletion(task);
                        break;
                    }

                    Thread.Sleep(100);
                }

                return Task.CompletedTask;
            });
        if (progressResult.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            await task;
            return;
        }

        cancellationTokenSource.Cancel();
        try
        {
            await task;
        }
        catch
        {
        }

        throw new InvalidOperationException("Progress dialog route failed: " + progressResult.Status, progressResult.Error);
    }

    private static void WaitForTaskCompletion(Task task)
    {
        while (!task.IsCompleted)
        {
            Thread.Sleep(100);
        }
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

    private static void ThrowIfUiDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }

    internal void ShowOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            throw new ArgumentNullException(nameof(dialog));
        }
        if (activeOverlayDialog != null
            && !ReferenceEquals(activeOverlayDialog, dialog)
            && activeOverlayDialog.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException("Another overlay dialog is already visible: " + activeOverlayDialog.GetType().Name);
        }

        activeOverlayDialog = dialog;
        dialog.Visibility = Visibility.Visible;
        dialog.Focus();
    }

    internal void HideOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            return;
        }

        dialog.Visibility = Visibility.Hidden;
        if (ReferenceEquals(activeOverlayDialog, dialog))
        {
            activeOverlayDialog = null;
        }
    }

    public void ShowSettingDialogOverlay()
    {
        ShowOverlayDialog(settingDialog);
    }

    public void ShowInitialSetupLanguageDialogOverlay()
    {
        ShowOverlayDialog(initialSetupLanguageDialog);
    }

    private void showSettingDialogButtonClick(object sender, RoutedEventArgs e)
    {
        ShowSettingDialogOverlay();
    }

    private void addRootFolderMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel { settingDialog: { } settingDialogViewModel } viewModel)
        {
            return;
        }

        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                selectedPath: viewModel.BMSParentFolderList?.FirstOrDefault(),
                multiselect: false,
                ensurePathExists: true,
                owner: this))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "Main window add root folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.AddBmsSearchRootPathFromMainWindowPicker(result.FolderPath);
        }
    }

    private ContextMenu _lastOpenedContextMenu;

    // NOTE:
    // TreeView の仮想化 (Recycling) 有効時は、画面外ノードのコンテナが VisualTree から外れる。
    // そのため「VisualTree を再帰して選択状態を判定する」実装は false negative を起こす。
    // ここでは最後に確定した選択ノードの所属セクションを保持し、UIコンテナ有無に依存しない判定を行う。
    private enum TreeSelectionSection
    {
        None,
        Playlist,
        PlayHistory,
        InstallPending,
        InstallInstalled,
        FullScanCheck,
        ChartInfoParseError,
        Other
    }

    internal enum PlaylistUrlDownloadResultKind
    {
        Downloaded,
        BrowserFallback,
        BlockedBySizeLimit,
        Duplicate,
        Failed
    }

    private static readonly string[] DownloadAndInstallArchiveExtensions = [".zip", ".7z", ".rar", ".lzh"];

    private bool playlistUrlBulkDownloadRunning;

    private CancellationTokenSource playlistUrlBulkDownloadCancellation;

    private int playlistUrlBulkDownloadTotalCount;

    private int playlistUrlBulkDownloadCompletedCount;

    private string playlistUrlBulkDownloadCurrentDisplayName = string.Empty;

    private string playlistUrlBulkDownloadLabelFormat = string.Empty;

    private TreeSelectionSection _currentTreeSelectionSection = TreeSelectionSection.None;

    private readonly PropertyChangedEventListener settingsDefaultEventListnener;

    private static readonly string clearlampUri = "http://xyzzz.net/bms/clearlamp";

    private CancellationTokenSource tableContextMenuTaskTokenSource;

    private Task changeSubmenuOpenDocumentTask;

    private static readonly Regex dropBoxRegex = new("https?://(?:(?:www|dl)\\.dropbox\\.com|dl\\.dropboxusercontent\\.com)/(sh?)/([^?]*)\\.([^?]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex gdriveRegex = new("https?://drive\\.google\\.com/(file/d/|open\\?id=)([^/]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex odriveRegex = new("https?://onedrive\\.live\\.com/redir\\?(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex htmlAttributeRegex = new("(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s\"'=<>`]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Storyboard treeViewItemInstantStoryBoardPlaylistTable = new();

    private static DispatcherTimer gridBMSPlayerControlsPreviousButtonClickTimer;

    private BitmapSource _panelImage;

    // マージ後に自動選択するDuplicateGroupのHeader（曲名）をキャッシュ
    private string _pendingDuplicateGroupHeader;
    private int _duplicateGroupAutoSelectRequestVersion;
    private PropertyChangedEventHandler _duplicateGroupAutoSelectHandler;
    private MainWindowViewModel _duplicateGroupAutoSelectHandlerOwner;

    private bool startupInitialSelectionApplied;

    private PropertyChangedEventHandler _startupInitialSelectionReadyHandler;

    private HashSet<int> pendingPlaylistSummarySelectionPlaylistIds;
    private int? pendingPlaylistSummaryCurrentPlaylistId;
    private long pendingPlaylistSummarySelectionMinDataGeneration;
    private long lastPlaylistSummaryAppliedDataGeneration;

    private BitmapSource panelImage
    {
        get
        {
            if (_panelImage == null)
            {
                if (Settings.Default.UseExternalPanelImage)
                {
                    try
                    {
                        var memoryStream = new MemoryStream(LongPathFileSystem.ReadAllBytes(Settings.Default.StagefilePath));
                        _panelImage = new WriteableBitmap(BitmapFrame.Create(memoryStream));
                        memoryStream.Close();
                        return _panelImage;
                    }
                    catch
                    {
                    }
                }
                _panelImage = new BitmapImage(new Uri("pack://application:,,,/resources/default_image.jpg"));
            }
            return _panelImage;
        }
    }

    public MainWindowViewModel.PanelState NowPanelState
    {
        get
        {
            return Settings.Default.PlayerPanelState;
        }
        set
        {
            if (Settings.Default.PlayerPanelState != value)
            {
                switch (value)
                {
                    case MainWindowViewModel.PanelState.BMS_PLAYER:
                        showBMSPlayerPanel();
                        break;
                    case MainWindowViewModel.PanelState.MOVIE_PLAYER:
                        showBrowserPanel();
                        break;
                    default:
                        collapseBMSPlayerPanel();
                        collapseBrowserPanel();
                        break;
                }
                Settings.Default.PlayerPanelState = value;
            }
        }
    }

    /// <summary>
    /// <see cref="MainWindow"/> クラスの新しいインスタンスを初期化します。
    /// UIコンポーネントの構築、TreeViewのイベントハンドラ登録、
    /// 設定のプロパティ変更リスナの初期化、および非同期のアップデートチェックを開始します。
    /// </summary>
    public MainWindow()
    {
        CleanupPreviousUpdateWorkDirectory();
        InitializeComponent();
        ApplySavedTreeViewWidth();
        AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(keywordSearchWindowPreviewMouseDown), true);
        Deactivated += MainWindow_Deactivated;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PlaylistWorkspace.PlaylistSummaryViewApplied += MainWindowViewModel_PlaylistSummaryViewApplied;
            SubscribeViewModelUiInteractions(viewModel);
        }
        Closed += delegate
        {
            UnsubscribeViewModelUiInteractions();
        };
        ContentRendered += MainWindow_ContentRendered;

        // Add handler that catches already-handled TreeViewItem.Selected events to synchronize TreeView exclusivity
        gridTreePane.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(gridTreePane_TreeViewItemSelected), true);

        settingsDefaultEventListnener = new PropertyChangedEventListener(Settings.Default);
        settingsDefaultEventListnener.RegisterHandler(() => Settings.Default.UseExternalPanelImage, delegate
        {
            _panelImage = null;
        });
        settingsDefaultEventListnener.RegisterHandler(() => Settings.Default.StagefilePath, delegate
        {
            _panelImage = null;
        });
        settingsDefaultEventListnener.RegisterHandler(() => Settings.Default.PlayerPanelState, delegate
        {
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.NotifyPlayerHeaderSourceChanged();
            }
        });
        gridBMSPlayerImage.Source = panelImage;

        // Start async update check
        Task.Run(async () => await CheckForUpdatesAsync());
    }

    private void MainWindow_ContentRendered(object sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)ShowElevatedProcessWarningIfNeeded);
    }

    private void ShowElevatedProcessWarningIfNeeded()
    {
        if (ShouldSkipElevatedProcessWarning())
        {
            return;
        }

        bool isElevated;
        try
        {
            isElevated = IsCurrentProcessElevated();
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "process_elevation_check_failed");
            return;
        }

        if (!isElevated)
        {
            return;
        }

        if (ShouldSkipElevatedProcessWarning())
        {
            return;
        }

        NLogWrapper.FileLogger?.Warn("process_elevated drag_drop_limited_warning_detected=true");
        try
        {
            UiDialogRoute.ShowMessageBox(
                this,
                BeMusicSeeker.Properties.Resources.Warn_ElevatedProcessDragDropLimited,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
            NLogWrapper.FileLogger?.Warn("process_elevated drag_drop_limited_warning_shown=true");
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "process_elevated drag_drop_limited_warning_failed");
        }
    }

    private bool ShouldSkipElevatedProcessWarning()
    {
        if (_isClosingOrClosed || !IsLoaded || Visibility != Visibility.Visible)
        {
            return true;
        }

        Application application = Application.Current;
        if (application == null)
        {
            return true;
        }

        Dispatcher applicationDispatcher = application.Dispatcher;
        return applicationDispatcher == null
            || applicationDispatcher.HasShutdownStarted
            || applicationDispatcher.HasShutdownFinished
            || Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished;
    }

    private static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void SubscribeViewModelUiInteractions(MainWindowViewModel viewModel)
    {
        if (viewModel == null || ReferenceEquals(subscribedViewModel, viewModel))
        {
            return;
        }
        UnsubscribeViewModelUiInteractions();
        subscribedViewModel = viewModel;
        viewModel.InitializationExceptionRequested += MainWindowViewModel_InitializationExceptionRequested;
        viewModel.InitialSetupLanguageDialogRequested += MainWindowViewModel_InitialSetupLanguageDialogRequested;
        viewModel.InitializationSucceeded += MainWindowViewModel_InitializationSucceeded;
        viewModel.PlaybackStarting += MainWindowViewModel_PlaybackStarting;
        viewModel.PlaybackStarted += MainWindowViewModel_PlaybackStarted;
    }

    private void UnsubscribeViewModelUiInteractions()
    {
        if (subscribedViewModel == null)
        {
            return;
        }
        subscribedViewModel.InitializationExceptionRequested -= MainWindowViewModel_InitializationExceptionRequested;
        subscribedViewModel.InitialSetupLanguageDialogRequested -= MainWindowViewModel_InitialSetupLanguageDialogRequested;
        subscribedViewModel.InitializationSucceeded -= MainWindowViewModel_InitializationSucceeded;
        subscribedViewModel.PlaybackStarting -= MainWindowViewModel_PlaybackStarting;
        subscribedViewModel.PlaybackStarted -= MainWindowViewModel_PlaybackStarted;
        subscribedViewModel = null;
    }

    private void MainWindowViewModel_InitializationExceptionRequested(object sender, EventArgs e)
    {
        ShowSettingDialogOverlay();
    }

    private void MainWindowViewModel_InitialSetupLanguageDialogRequested(object sender, EventArgs e)
    {
        ShowInitialSetupLanguageDialogOverlay();
    }

    private void MainWindowViewModel_InitializationSucceeded(object sender, EventArgs e)
    {
        if (sender is MainWindowViewModel viewModel)
        {
            viewModel.SetuBMplayPanel(_panel);
        }
        gridBMSPlayerControlsRotatePanelStateButtonClicked();
    }

    private void MainWindowViewModel_PlaybackStarting(object sender, EventArgs e)
    {
        _renewBMSPlayerControlInfo();
        scrollIntoView();
    }

    private void MainWindowViewModel_PlaybackStarted(object sender, EventArgs e)
    {
        tryShowBMSPlayerPanel();
    }

    private void ApplySavedTreeViewWidth()
    {
        double normalizedWidth = Settings.NormalizeTreeViewWidth(Settings.Default.TreeViewWidth);
        Settings.Default.TreeViewWidth = normalizedWidth;
        gridColumn0.Width = new GridLength(normalizedWidth);
    }

    internal static double ResolveTreeViewWidthForSave(double actualColumnWidth, double assignedColumnWidth, double currentSettingWidth)
    {
        if (IsUsableTreeViewWidth(actualColumnWidth))
        {
            return actualColumnWidth;
        }
        if (IsUsableTreeViewWidth(assignedColumnWidth))
        {
            return assignedColumnWidth;
        }
        return Settings.NormalizeTreeViewWidth(currentSettingWidth);
    }

    private static bool IsUsableTreeViewWidth(double width)
    {
        return !double.IsNaN(width) && !double.IsInfinity(width) && width >= Settings.MinTreeViewWidth;
    }

    /// <summary>
    /// GitHub上のバージョン情報ファイルを参照し、現在実行中のアプリケーションよりも
    /// 新しいバージョンがリリースされていないか非同期でチェックします。
    /// 新しいバージョンが利用可能な場合は、ユーザーにメッセージボックスで通知します。
    /// </summary>
    /// <returns>非同期タスクを表す <see cref="Task"/> オブジェクト。</returns>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateCheckResult result = await updateCheckService.CheckAsync(CommandLineSwitches.UpdateManifestUrl);
            if (_isClosingOrClosed)
            {
                return;
            }
            if (result.IsUpdateAvailable)
            {
                MainWindowViewModel viewModel = await base.Dispatcher.InvokeAsync(() =>
                {
                    return _isClosingOrClosed ? null : base.DataContext as MainWindowViewModel;
                });
                UpdateAssetInfo selectedAsset = null;
                if (viewModel != null)
                {
                    UiWindowDialogResult<UpdateAssetInfo> dialogResult = await new UiDialogCoordinator()
                        .ShowWindowAsync(new UiWindowDialogRequest<UpdateAvailableDialog, UpdateAssetInfo>(
                            () => new UpdateAvailableDialog(result, viewModel),
                            dialog => dialog.SelectedAsset,
                            this));
                    ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Update available dialog");
                    selectedAsset = dialogResult.IsAccepted ? dialogResult.Value : null;
                }

                if (selectedAsset != null)
                {
                    await DownloadAndApplyUpdateAsync(selectedAsset);
                }
            }
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn("Failed to check for updates: " + ex.Message);
        }
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateAssetInfo selectedAsset)
    {
        bool shutdownPreparationCompleted = false;
        string packagePath = null;
        try
        {
            packagePath = await updateDownloadService.DownloadAndVerifyAsync(selectedAsset).ConfigureAwait(false);
            ProcessStartInfo updaterStartInfo = updateDownloadService.CreateUpdaterStartInfo(packagePath);
            ShutdownPreparationResult shutdownResult = await EnsureShutdownPreparedAsync("update").ConfigureAwait(false);
            shutdownPreparationCompleted = true;
            NLogWrapper.FileLogger?.Info("Update apply shutdown prepared: " + shutdownResult.ToLogFields());

            Process updaterProcess = Process.Start(updaterStartInfo);
            if (updaterProcess == null)
            {
                throw new InvalidOperationException("Updater process did not start.");
            }
            base.Dispatcher.Invoke(() =>
            {
                _shutdownPrepared = true;
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Error(ex, "Failed to apply update.");
            if (shutdownPreparationCompleted)
            {
                TryDeleteDownloadedUpdatePackage(packagePath);
                base.Dispatcher.Invoke(() =>
                {
                    _shutdownPrepared = true;
                    Application.Current.Shutdown();
                });
                return;
            }
            base.Dispatcher.Invoke(() =>
            {
                UiDialogRoute.ShowMessageBox(
                    "Failed to download or start the update.\n" + ex.Message,
                    "Update Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
    }

    private static void CleanupPreviousUpdateWorkDirectory()
    {
        try
        {
            UpdateDownloadService.CleanupPreviousWorkDirectory();
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "Failed to cleanup previous update_work directory.");
        }
    }

    /// <summary>
    /// アプリケーション起動時に、設定 (StartupSelectInstallPending) に基づいて
    /// プレイリストツリーの「インストール待ち（保留）」ノードを自動的に展開・選択します。
    /// </summary>
    public void ApplyStartupInitialSelectionRequest()
    {
        if (startupInitialSelectionApplied)
        {
            return;
        }
        startupInitialSelectionApplied = true;
        if (!Settings.Default.StartupSelectInstallPending)
        {
            return;
        }
        if (treeViewItemInstallPending != null && treeViewItemInstallPending.IsSelected)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsStartupUiInteractionBlocked)
        {
            QueueStartupInitialSelectionUntilOperable(viewModel);
            return;
        }
        ApplyStartupInitialSelectionNow();
    }

    private void QueueStartupInitialSelectionUntilOperable(MainWindowViewModel viewModel)
    {
        if (_startupInitialSelectionReadyHandler != null)
        {
            return;
        }
        _startupInitialSelectionReadyHandler = delegate (object _, PropertyChangedEventArgs args)
        {
            if (args == null || args.PropertyName != "IsStartupUiInteractionBlocked" || viewModel.IsStartupUiInteractionBlocked)
            {
                return;
            }
            viewModel.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
            ApplyStartupInitialSelectionNow();
        };
        viewModel.PropertyChanged += _startupInitialSelectionReadyHandler;
    }

    private void ApplyStartupInitialSelectionNow()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            if (treeViewItemInstall != null)
            {
                treeViewItemInstall.IsExpanded = true;
            }
            if (treeViewItemInstallPending != null)
            {
                treeViewItemInstallPending.IsExpanded = true;
                treeViewItemInstallPending.IsSelected = true;
            }
        });
    }

    /// <summary>
    /// 指定 Visual 配下に存在する特定型の子要素数を数えます。
    /// </summary>
    /// <typeparam name="T">数えたい Visual 型。</typeparam>
    /// <param name="root">探索開始要素。</param>
    /// <param name="maxCount">上限件数。</param>
    /// <returns>見つかった要素数。</returns>
    private static int CountVisualDescendants<T>(DependencyObject root, int maxCount) where T : DependencyObject
    {
        if (root == null || maxCount <= 0)
        {
            return 0;
        }
        int count = 0;
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.Count > 0 && count < maxCount)
        {
            DependencyObject current = pending.Dequeue();
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount && count < maxCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is T)
                {
                    count++;
                }
                if (child != null)
                {
                    pending.Enqueue(child);
                }
            }
        }
        return count;
    }

    private void CloseWindow(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
    }

    /// <summary>
    /// メインウィンドウに対してファイルやフォルダーがドラッグ＆ドロップされた際の完了処理イベント。
    /// ドロップされたパス一覧を取得し、譜面ファイルのインストール処理を開始します。
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (playlistUrlBulkDownloadRunning)
        {
            UiDialogRoute.ShowMessageBox(this, BeMusicSeeker.Properties.Resources.Warn_DropInstallBlockedByPlaylistUrlDownload, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            string[] pathSnapshot = [.. filePaths];
            if (pathSnapshot.Length > 0)
            {
                viewModel?.EnqueueDroppedInstallPaths(pathSnapshot);
                newlyInstalledTreeViewItem.IsExpanded = true;
            }
        }
    }
    /// <summary>
    /// メインウィンドウ内へファイルをドラッグ中（ホバー中）のイベント。
    /// ドロップされたデータがファイル(FileDrop)形式である場合のみ、カーソルエフェクトを「Copy（追加）」に変更します。
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (playlistUrlBulkDownloadRunning)
        {
            e.Effects = DragDropEffects.None;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }
    /// <summary>
    /// メインウィンドウ上でマウスの左ボタンが押し込まれた際の処理。
    /// 一覧やツリーなどの操作可能要素以外をクリックしたと判定された場合、
    /// ウィンドウ全体をドラッグ移動できるようにします (DragMove)。
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!(customTableView?.IsMouseOver ?? false) && !(customTablePlaylistSummary?.IsMouseOver ?? false) && !(treeView?.IsMouseOver ?? false))
        {
            DragMove();
        }
    }
    /// <summary>
    /// ウィンドウの表示状態（最大化、最小化、通常）が変更された際に呼び出され、
    /// ウィンドウ境界のマージンを調整します（最大化時の見切れ防止）。
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (((Window)sender).WindowState == WindowState.Maximized)
        {
            windowBorder.Margin = new Thickness(8.0);
        }
        else
        {
            windowBorder.Margin = new Thickness(0.0);
        }
    }

    /// <summary>
    /// ウィンドウの内部リソースとHWNDが初期化された直後に呼び出されます。
    /// パネル状態の整合性確認、WebBrowserコントロールの設定（サイレント化、ドロップ無効化）、
    /// および前回終了時のウィンドウ配置（最大化状態や座標）の復元を行います。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!isPanelStateValid(NowPanelState))
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked();
        }
        if (webBrowser != null)
        {
            object value = typeof(WebBrowser).GetProperty("AxIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(webBrowser, null);
            value.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, value, [true]);
            value.GetType().InvokeMember("RegisterAsDropTarget", BindingFlags.SetProperty, null, value, [false]);
        }
        try
        {
            Win32API.WINDOWPLACEMENT lpwndpl = Settings.Default.WindowPlacement;
            lpwndpl.Length = Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT));
            lpwndpl.Flags = 0;
            lpwndpl.ShowCmd = ((lpwndpl.ShowCmd == Win32API.ShowWindowCommands.ShowMinimized) ? Win32API.ShowWindowCommands.Normal : lpwndpl.ShowCmd);
            Win32API.SetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
        }
        catch
        {
        }
    }

    private static void TryDeleteDownloadedUpdatePackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }
        try
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to delete downloaded update package: " + ex.Message);
        }
    }

    private Task<ShutdownPreparationResult> EnsureShutdownPreparedAsync(string reason)
    {
        lock (shutdownPreparationLock)
        {
            shutdownPreparationTask ??= PrepareShutdownCoreAsync(reason ?? "shutdown");
            return shutdownPreparationTask;
        }
    }

    private async Task<ShutdownPreparationResult> PrepareShutdownCoreAsync(string reason)
    {
        _shutdownPreparationRunning = true;
        _isClosingOrClosed = true;
        App.MarkCoordinatedShutdownStarted(reason);
        MainWindowViewModel viewModel = null;
        if (base.Dispatcher.CheckAccess())
        {
            viewModel = base.DataContext as MainWindowViewModel;
        }
        else
        {
            await base.Dispatcher.InvokeAsync((Action)delegate
            {
                viewModel = base.DataContext as MainWindowViewModel;
            }).Task.ConfigureAwait(false);
        }
        if (viewModel != null)
        {
            ShutdownPreparationResult result = await viewModel.PrepareShutdownAsync(reason).ConfigureAwait(false);
            _shutdownPrepared = true;
            return result;
        }
        _shutdownPrepared = true;
        return new ShutdownPreparationResult(
            reason ?? "shutdown",
            0L,
            slowWaitLogged: false,
            sqliteCloseFailureCount: ShutdownOperationTracker.SqliteCloseFailureCount);
    }

    private async Task CompleteCloseAfterShutdownPreparedAsync(string reason)
    {
        try
        {
            await EnsureShutdownPreparedAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Error(ex, "Failed to prepare shutdown.");
            _shutdownPrepared = true;
        }
        await base.Dispatcher.InvokeAsync((Action)delegate
        {
            if (Application.Current != null)
            {
                Application.Current.Shutdown();
            }
            else
            {
                Close();
            }
        }).Task.ConfigureAwait(false);
    }

    /// <summary>
    /// ウィンドウが閉じられる直前に呼び出されます。
    /// 現在のUI状態（TreeViewの幅、ウィンドウの配置や最大化状態など）を
    /// ユーザー設定 (Settings.Default) に保存します。
    /// </summary>
    /// <param name="e">キャンセル可能なイベントデータ。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownPrepared)
        {
            e.Cancel = true;
            if (!_shutdownPreparationRunning)
            {
                _shutdownPreparationRunning = true;
                _isClosingOrClosed = true;
                calcelAllContextMenuTasks();
                CloseContextMenuIfOpen(_lastOpenedContextMenu);
                _ = CompleteCloseAfterShutdownPreparedAsync("window_close");
            }
            return;
        }
        _isClosingOrClosed = true;
        var viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && _startupInitialSelectionReadyHandler != null)
        {
            viewModel.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
        }
        if (viewModel != null)
        {
            viewModel.PlaylistWorkspace.PlaylistSummaryViewApplied -= MainWindowViewModel_PlaylistSummaryViewApplied;
        }
        viewModel?.SetStartupUiInteractionBlocked(false);
        calcelAllContextMenuTasks();
        CloseContextMenuIfOpen(_lastOpenedContextMenu);
        base.OnClosing(e);
        try
        {
            Settings.Default.TreeViewWidth = ResolveTreeViewWidthForSave(
                gridColumn0.ActualWidth,
                gridColumn0.Width.IsAbsolute ? gridColumn0.Width.Value : double.NaN,
                Settings.Default.TreeViewWidth);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save tree view width: " + ex.Message);
        }
        try
        {
            Win32API.WINDOWPLACEMENT lpwndpl = default;
            Win32API.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
            Settings.Default.WindowPlacement = lpwndpl;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save window placement: " + ex.Message);
        }
        try
        {
            Settings.Default.Save();
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save settings on closing: " + ex.Message);
        }
    }
    private static void CloseContextMenuIfOpen(ContextMenu contextMenu)
    {
        if (contextMenu != null && contextMenu.IsOpen)
        {
            contextMenu.IsOpen = false;
        }
    }

    private bool ShouldBlockStartupUiInteraction(string action)
    {
        if (_isClosingOrClosed)
        {
            LogStartupUiBlocked(action, "closing");
            return true;
        }
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsStartupUiInteractionBlocked)
        {
            LogStartupUiBlocked(action, "startup");
            return true;
        }
        return false;
    }

    private bool ShouldBlockChartPackageMutationInteraction(string action)
    {
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsChartPackageMutationInProgress)
        {
            installPerformanceLogger?.Info("chart_package_mutation_ui_blocked action=" + action);
            return true;
        }
        return false;
    }

    private static void LogStartupUiBlocked(string action, string reason)
    {
        installPerformanceLogger?.Info("startup_ui_blocked action=" + action + " reason=" + reason);
    }

    private void customTableView_FirstRenderCompleted(object sender, CustomTableFirstRenderCompletedEventArgs e)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        var stateLogStopwatch = Stopwatch.StartNew();
        var viewModel = base.DataContext as MainWindowViewModel;
        long sourceGenerationId = viewModel?.PlaylistSourceGenerationId ?? 0L;
        long viewGenerationId = viewModel?.PlaylistAdoptedViewGenerationId ?? 0L;
        TableFirstVisibleTiming timing = default;
        bool hasPlaylistTiming = viewModel != null && viewModel.TryCreatePlaylistOpenVisibleTiming(sourceGenerationId, viewGenerationId, out timing);
        if (!hasPlaylistTiming)
        {
            timing = new TableFirstVisibleTiming(-1, -1L, -1L, e.FirstRenderMs, -1L, e.RowCount);
        }
        long stateLogMs = stateLogStopwatch.ElapsedMilliseconds;
        var metrics = new TableFirstVisibleMetrics(
            "CustomTableView",
            "custom_onrender",
            sourceGenerationId,
            viewGenerationId,
            e.RowCount,
            e.VisibleRowCount,
            e.VisibleColumnCount,
            e.VisibleCellCount,
            e.FirstRenderMs,
            e.RenderWorkMs,
            e.TextCacheHitRate,
            stateLogMs,
            timing,
            e.IsPreparationRender);
        installPerformanceLogger.Info(TableFirstVisibleLogFormatter.Format(metrics));
        if (hasPlaylistTiming && !(e.IsPreparationRender && timing.ViewCount > 0))
        {
            viewModel.TryLogPlaylistOpenVisibleCompleted("custom_onrender", sourceGenerationId, viewGenerationId);
        }
    }

    private void customTableView_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        viewModel.MainChartList.RequestSort(e.SortMemberPath, e.Direction);
    }

    private async void customTablePlaylistSummary_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.PlaylistWorkspace.RequestPlaylistSummarySort(e.SortMemberPath, e.Direction);
        }).Logging("customTablePlaylistSummary_SortRequested");
    }

    private void customTablePlaylistSummary_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistSummaryBmtSortDropAllowed(e, out _, out _, out int visibleInsertIndex))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
            if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
            return;
        }
        customTablePlaylistSummary?.SetRowDropInsertPreview(visibleInsertIndex);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void customTablePlaylistSummary_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        try
        {
            if (!IsPlaylistSummaryBmtSortDropAllowed(e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex))
            {
                if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
                {
                    e.Handled = true;
                }
                return;
            }
            e.Handled = true;
            List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
            List<int> draggedPlaylistIds = GetPlaylistSummaryRowIds(draggedRows);
            int? currentPlaylistId = primaryDraggedRow?.PlaylistId ?? draggedRows.FirstOrDefault(row => row?.PlaylistId != null)?.PlaylistId;
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                QueuePlaylistSummarySelectionRestoreAfterApply(draggedPlaylistIds, currentPlaylistId);
                long dataRebuildGeneration = 0L;
                await Task.Run(delegate
                {
                    dataRebuildGeneration = viewModel.PlaylistSummaryBmtSort.DropRows(visibleRows, draggedRows, visibleInsertIndex);
                }).Logging("customTablePlaylistSummary_Drop");
                if (dataRebuildGeneration <= 0L)
                {
                    ClearPendingPlaylistSummarySelectionRestore();
                }
                else
                {
                    pendingPlaylistSummarySelectionMinDataGeneration = dataRebuildGeneration;
                    ApplyPendingPlaylistSummarySelectionRestore();
                }
            }
            e.Effects = DragDropEffects.Move;
        }
        catch
        {
            ClearPendingPlaylistSummarySelectionRestore();
            throw;
        }
        finally
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void customTablePlaylistSummary_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void ClearPlaylistSummaryBmtSortDropPreview()
    {
        customTablePlaylistSummary?.ClearRowDropInsertPreview();
    }

    private void QueuePlaylistSummarySelectionRestoreAfterApply(IReadOnlyCollection<int> draggedPlaylistIds, int? currentPlaylistId)
    {
        if (draggedPlaylistIds == null || draggedPlaylistIds.Count == 0)
        {
            return;
        }
        pendingPlaylistSummarySelectionPlaylistIds = new HashSet<int>(draggedPlaylistIds);
        pendingPlaylistSummaryCurrentPlaylistId = currentPlaylistId;
        pendingPlaylistSummarySelectionMinDataGeneration = long.MaxValue;
    }

    private void ClearPendingPlaylistSummarySelectionRestore()
    {
        pendingPlaylistSummarySelectionPlaylistIds = null;
        pendingPlaylistSummaryCurrentPlaylistId = null;
        pendingPlaylistSummarySelectionMinDataGeneration = 0L;
    }

    private void MainWindowViewModel_PlaylistSummaryViewApplied(object sender, PlaylistSummaryViewAppliedEventArgs e)
    {
        if (e?.DataRebuildGeneration > 0L)
        {
            lastPlaylistSummaryAppliedDataGeneration = e.DataRebuildGeneration;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyPendingPlaylistSummarySelectionRestore), DispatcherPriority.Background);
            return;
        }
        ApplyPendingPlaylistSummarySelectionRestore();
    }

    private void ApplyPendingPlaylistSummarySelectionRestore()
    {
        if (customTablePlaylistSummary == null || pendingPlaylistSummarySelectionPlaylistIds == null || pendingPlaylistSummarySelectionPlaylistIds.Count == 0)
        {
            ClearPendingPlaylistSummarySelectionRestore();
            return;
        }
        if (pendingPlaylistSummarySelectionMinDataGeneration <= 0L
            || lastPlaylistSummaryAppliedDataGeneration < pendingPlaylistSummarySelectionMinDataGeneration)
        {
            return;
        }
        HashSet<int> playlistIdSet = pendingPlaylistSummarySelectionPlaylistIds;
        int? currentPlaylistId = pendingPlaylistSummaryCurrentPlaylistId;
        customTablePlaylistSummary.SelectRowsByPredicate(
            row => row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId.HasValue
                && playlistIdSet.Contains(playlistSummaryRow.PlaylistId.Value),
            row => currentPlaylistId.HasValue
                && row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId == currentPlaylistId);
        ClearPendingPlaylistSummarySelectionRestore();
    }

    private static List<int> GetPlaylistSummaryRowIds(IEnumerable<PlaylistSummaryRow> rows)
    {
        return [.. (rows ?? [])
            .Where(row => row?.PlaylistId != null)
            .Select(row => row.PlaylistId.Value)
            .Distinct()];
    }

    private bool IsPlaylistSummaryBmtSortDropAllowed(DragEventArgs e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex)
    {
        draggedRows = [];
        primaryDraggedRow = null;
        visibleInsertIndex = -1;
        if (e?.Data == null
            || !CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows)
            || base.DataContext is not MainWindowViewModel viewModel
            || !viewModel.PlaylistWorkspace.IsPlaylistSummarySortedByBmtSortAscending
            || !CustomTableDataTransfer.TryGetSelectedRows(e.Data, out List<object> selectedRows))
        {
            return false;
        }
        draggedRows = [.. selectedRows.OfType<PlaylistSummaryRow>().Where(row => row?.TableRef != null && row.PlaylistId.HasValue)];
        if (draggedRows.Count == 0)
        {
            return false;
        }
        if (CustomTableDataTransfer.TryGetPrimaryRow(e.Data, out object primaryRow)
            && primaryRow is PlaylistSummaryRow primaryPlaylistSummaryRow
            && draggedRows.Contains(primaryPlaylistSummaryRow))
        {
            primaryDraggedRow = primaryPlaylistSummaryRow;
        }
        primaryDraggedRow ??= draggedRows.FirstOrDefault();
        visibleInsertIndex = ResolvePlaylistSummaryVisibleInsertIndex(e);
        return visibleInsertIndex >= 0;
    }

    private int ResolvePlaylistSummaryVisibleInsertIndex(DragEventArgs e)
    {
        IList rows = customTablePlaylistSummary?.ItemsSource;
        if (rows == null)
        {
            return -1;
        }
        if (rows.Count == 0)
        {
            return 0;
        }
        Point point = e.GetPosition(customTablePlaylistSummary);
        CustomTableHitTestResult hit = customTablePlaylistSummary.HitTestTable(point);
        if (hit.Kind == CustomTableHitKind.Header)
        {
            return 0;
        }
        if (hit.Kind != CustomTableHitKind.Cell)
        {
            if (customTablePlaylistSummary == null || point.X < 0d || point.X >= customTablePlaylistSummary.SurfaceWidth)
            {
                return -1;
            }
            return ResolvePlaylistSummaryVisibleInsertIndexFromPointY(point.Y, rows.Count);
        }
        bool insertAfterTarget = point.Y >= hit.CellRect.Top + (hit.CellRect.Height / 2d);
        return Math.Max(0, Math.Min(rows.Count, hit.RowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private int ResolvePlaylistSummaryVisibleInsertIndexFromPointY(double y, int rowCount)
    {
        if (customTablePlaylistSummary == null || rowCount <= 0)
        {
            return 0;
        }
        if (y < customTablePlaylistSummary.HeaderHeight)
        {
            return 0;
        }
        int firstVisibleRowIndex = customTablePlaylistSummary.FirstVisibleRowIndex;
        int drawableRowCapacity = customTablePlaylistSummary.CalculateDrawableRowCapacity();
        int lastVisibleRowExclusive = Math.Min(rowCount, firstVisibleRowIndex + drawableRowCapacity);
        double rowHeight = Math.Max(1d, customTablePlaylistSummary.RowHeight);
        int rowIndex = firstVisibleRowIndex + (int)Math.Floor((y - customTablePlaylistSummary.HeaderHeight) / rowHeight);
        if (rowIndex < firstVisibleRowIndex)
        {
            return firstVisibleRowIndex;
        }
        if (rowIndex >= lastVisibleRowExclusive)
        {
            return rowCount;
        }
        double rowTop = customTablePlaylistSummary.HeaderHeight + (rowIndex - firstVisibleRowIndex) * rowHeight;
        bool insertAfterTarget = y >= rowTop + rowHeight / 2d;
        return Math.Max(0, Math.Min(rowCount, rowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private List<PlaylistSummaryRow> GetVisiblePlaylistSummaryRowsSnapshot()
    {
        return [.. (customTablePlaylistSummary?.ItemsSource?.OfType<PlaylistSummaryRow>() ?? Enumerable.Empty<PlaylistSummaryRow>())
            .Where(row => row != null)];
    }

    private void customTableView_SelectionChanged(object sender, CustomTableSelectionChangedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: null } viewModel)
        {
            if (viewModel.IsPlaylistDetailViewActive)
            {
                NLogWrapper.FileLogger?.Info("custom_table_selection_changed selectedIndex=" + e.SelectedIndex + " selectedCount=" + (e.SelectedRows?.Count ?? 0));
            }
            if (GridRowResolver.TryGetBmsPlayerFile(e.SelectedRow, out BMSFile bmsFile))
            {
                viewModel.SetBmsPlayerHeader(bmsFile);
                _renewBMSPlayerControlInfo(bmsFile);
            }
        }
    }

    private async void customTableView_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_activate"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!GridRowResolver.TryGetBmsPlayerFile(e.Row, out BMSFile bmsFile))
        {
            return;
        }
        viewModel.SetBmsPlayerHeader(bmsFile);
        _renewBMSPlayerControlInfo(bmsFile);
        if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
        {
            NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
        }
        await Task.Run(delegate
        {
            viewModel.PlayStartBMSfile();
        }).Logging("customTableView_RowActivated");
    }

    private void customTablePlaylistSummary_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_row_activate"))
        {
            return;
        }
        if (e.Row is PlaylistSummaryRow playlistSummaryRow)
        {
            TrySelectPlaylistTreeItemFromSummary(playlistSummaryRow);
        }
    }

    private void customTableView_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (e.Row == null || !TryGetTableContextMenuResource(e.Row, "custom_table_context_menu_assign", out ContextMenu contextMenu, out _))
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_context_menu"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow)
        {
            return;
        }
        if (TryFindResource("playlistSummaryContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTableView_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_column_header_context_menu"))
        {
            return;
        }
        CustomTableColumnSettings columnSettings = (customTableView.DataContext as MainChartListViewModel)?.ColumnsSettings;
        if (TryFindResource(ResolveMainColumnHeaderContextMenuResourceKey(columnSettings)) is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    internal static string ResolveMainColumnHeaderContextMenuResourceKey(CustomTableColumnSettings columnSettings)
    {
        return columnSettings?.Kind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY
            ? "playHistoryColumnHeaderContextMenu"
            : "tableColumnHeaderContextMenu";
    }

    private void customTablePlaylistSummary_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_column_header_context_menu"))
        {
            return;
        }
        if (TryFindResource("playlistSummaryColumnHeaderContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || e.Row is not PlaylistSummaryRow playlistSummaryRow
            || playlistSummaryRow.TableRef == null
            || !IsPlaylistSummaryEditableProperty(e.EditPropertyName)
            || !CanOpenPlaylistEditDialog(viewModel)
            || !viewModel.ContainsActivePlaylistTable(playlistSummaryRow.TableRef))
        {
            e.Cancel = true;
        }
    }

    private void customTableView_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.EditPropertyName))
        {
            e.Cancel = true;
            return;
        }
        if (e.Row is PlaylistDetailRow)
        {
            if (!IsCustomTablePlaylistEditableProperty(e.EditPropertyName) || !GridRowResolver.CanEditPlaylistCell(e.Row, e.EditPropertyName))
            {
                e.Cancel = true;
                return;
            }
            viewModel.NotifyPlaylistCellEditStarted();
            return;
        }
        if (string.Equals(e.EditPropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal))
        {
            if (!GridRowResolver.TryGetFolderEditChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out _))
            {
                e.Cancel = true;
                return;
            }
            return;
        }
        if (string.Equals(e.EditPropertyName, "instl_dst", StringComparison.Ordinal))
        {
            if (!CanEditInstallDestinationInCurrentSection()
                || !GridRowResolver.TryGetChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target)
                || !target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
            {
                e.Cancel = true;
                return;
            }
            return;
        }
        e.Cancel = true;
    }

    private async void customTableView_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        bool isUrlDiff = string.Equals(e.Column?.Id, "Url2", StringComparison.Ordinal);
        bool isUrl = isUrlDiff || string.Equals(e.Column?.Id, "Url1", StringComparison.Ordinal);
        if (!isUrl)
        {
            return;
        }
        await OpenUrlFromRowAsync(e.Row, isUrlDiff, isUrlDiff ? "custom_table_open_url_diff" : "custom_table_open_url");
    }

    private async void customTablePlaylistSummary_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_action"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow playlistSummaryRow)
        {
            return;
        }
        switch (e.Column?.Id)
        {
            case "Link":
                await OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri).Logging("customTablePlaylistSummary_CellActionRequested_Link");
                break;
            case "Header":
                await OpenPlaylistSummaryUriAsync(playlistSummaryRow.HeaderUri).Logging("customTablePlaylistSummary_CellActionRequested_Header");
                break;
            case "Data":
                await OpenPlaylistSummaryUriAsync(playlistSummaryRow.DataUri).Logging("customTablePlaylistSummary_CellActionRequested_Data");
                break;
            case "IsExternalSync":
                await ApplyPlaylistSummarySyncFromCustomTableAsync(playlistSummaryRow, !(playlistSummaryRow.IsExternalSync)).Logging("customTablePlaylistSummary_CellActionRequested_Sync");
                break;
            case "IsRootFolder":
                await ApplyPlaylistSummaryRootFromCustomTableAsync(playlistSummaryRow, !(playlistSummaryRow.IsRootFolder)).Logging("customTablePlaylistSummary_CellActionRequested_Root");
                break;
            case "IsBmtOutput":
                await ApplyPlaylistSummaryBmtOutputFromCustomTableAsync(playlistSummaryRow, !(playlistSummaryRow.IsBmtOutput)).Logging("customTablePlaylistSummary_CellActionRequested_BmtOutput");
                break;
        }
    }

    private async void customTablePlaylistSummary_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!e.Commit
            || e.Row is not PlaylistSummaryRow playlistSummaryRow
            || playlistSummaryRow.TableRef == null
            || !IsPlaylistSummaryEditableProperty(e.EditPropertyName))
        {
            return;
        }

        string currentText = GetPlaylistSummaryEditableText(playlistSummaryRow, e.EditPropertyName);
        string editedText = NormalizePlaylistSummaryEditableText(e.EditPropertyName, e.Text);
        if (string.Equals(currentText, editedText, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            bool applied = await viewModel.ApplyPlaylistSummaryPropertyEditAsync(playlistSummaryRow, e.EditPropertyName, e.Text);
            if (!applied)
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_invalid_setting, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                customTablePlaylistSummary?.RefreshDisplay();
            }
        }
        catch (Exception ex)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            customTablePlaylistSummary?.RefreshDisplay();
        }
    }

    private void customTableView_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(e.EditPropertyName))
            {
                return;
            }
            if (e.Row is PlaylistDetailRow playlistRow)
            {
                if (!e.Commit || !IsCustomTablePlaylistEditableProperty(e.EditPropertyName) || !GridRowResolver.CanEditPlaylistCell(e.Row, e.EditPropertyName))
                {
                    return;
                }
                switch (e.EditPropertyName)
                {
                    case nameof(PlaylistDetailRow.Level):
                        playlistRow.Level = e.Text;
                        break;
                    case nameof(PlaylistDetailRow.Url):
                        if (!Uri.TryCreate(e.Text, UriKind.Absolute, out Uri url))
                        {
                            return;
                        }
                        playlistRow.Url = url;
                        break;
                    case nameof(PlaylistDetailRow.Url_diff):
                        if (!Uri.TryCreate(e.Text, UriKind.Absolute, out Uri urlDiff))
                        {
                            return;
                        }
                        playlistRow.Url_diff = urlDiff;
                        break;
                    case nameof(PlaylistDetailRow.comment):
                        playlistRow.comment = e.Text;
                        break;
                    case nameof(PlaylistDetailRow.memo):
                        playlistRow.memo = e.Text;
                        break;
                    default:
                        return;
                }
                viewModel.SyncPlaylistSourceRowFromEditedViewRow(playlistRow);
                Task.Run(delegate
                {
                    viewModel.CommitPlaylistRow(playlistRow);
                }).Logging("customTableView_CellEditEnded");
                return;
            }
            if (string.Equals(e.EditPropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal))
            {
                if (!GridRowResolver.TryGetFolderEditChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target))
                {
                    return;
                }
                if (!e.Commit)
                {
                    return;
                }
                if (!RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request))
                {
                    return;
                }
                string newFolder = e.Text;
                Task.Run(delegate
                {
                    try
                    {
                        viewModel.RenameChartFolder(request, newFolder);
                    }
                    finally
                    {
                        RefreshCustomTableViewDisplayAsync();
                    }
                }).Logging("customTableView_CellEditEnded");
                return;
            }
            if (string.Equals(e.EditPropertyName, "instl_dst", StringComparison.Ordinal))
            {
                if (!GridRowResolver.TryGetChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target)
                    || !target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
                {
                    return;
                }
                if (!e.Commit)
                {
                    return;
                }
                if (!CanEditInstallDestinationInCurrentSection())
                {
                    return;
                }
                if (!PendingInstallDestinationEditRequest.TryCreate(target, out PendingInstallDestinationEditRequest request))
                {
                    return;
                }
                string destinationDirectory = e.Text;
                Task.Run(delegate
                {
                    viewModel.SetPendingInstallDestination(request, destinationDirectory);
                    base.Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (_isClosingOrClosed)
                        {
                            return;
                        }
                        RefreshCustomTableViewDisplay();
                    }, DispatcherPriority.Background);
                }).Logging("customTableView_CellEditEnded");
            }
        }
        finally
        {
            if (e.Row is PlaylistDetailRow)
            {
                viewModel.NotifyPlaylistCellEditCompleted();
            }
        }
    }

    private static bool IsCustomTablePlaylistEditableProperty(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistDetailRow.Level), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url_diff), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.comment), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.memo), StringComparison.Ordinal);
    }

    private static bool IsPlaylistSummaryEditableProperty(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistSummaryRow.Name), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.FolderName), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.CompatPrefix), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.Symbol), StringComparison.Ordinal);
    }

    private static string GetPlaylistSummaryEditableText(PlaylistSummaryRow row, string propertyName)
    {
        if (row == null)
        {
            return string.Empty;
        }
        return propertyName switch
        {
            nameof(PlaylistSummaryRow.Name) => NormalizePlaylistSummaryEditableText(propertyName, row.Name),
            nameof(PlaylistSummaryRow.FolderName) => NormalizePlaylistSummaryEditableText(propertyName, row.FolderName),
            nameof(PlaylistSummaryRow.CompatPrefix) => NormalizePlaylistSummaryEditableText(propertyName, row.CompatPrefix),
            nameof(PlaylistSummaryRow.Symbol) => NormalizePlaylistSummaryEditableText(propertyName, row.Symbol),
            _ => string.Empty
        };
    }

    private static string NormalizePlaylistSummaryEditableText(string propertyName, string text)
    {
        text ??= string.Empty;
        return propertyName switch
        {
            nameof(PlaylistSummaryRow.CompatPrefix) => text.TrimStart(),
            nameof(PlaylistSummaryRow.FolderName) => BMSTable.NormalizeOutputDirectoryName(text) ?? string.Empty,
            nameof(PlaylistSummaryRow.Name) => text.Trim(),
            nameof(PlaylistSummaryRow.Symbol) => text.Trim(),
            _ => text
        };
    }

    private void RefreshCustomTableViewDisplayAsync()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            RefreshCustomTableViewDisplay();
        }, DispatcherPriority.Background);
    }

    private void RefreshCustomTableViewDisplay()
    {
        if (_isClosingOrClosed || customTableView == null)
        {
            return;
        }
        customTableView.RefreshDisplay();
    }
    private void tableInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_init_column_settings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                mainWindowViewModel.LoadColumnSetting();
            }
        }
    }

    private void playlistSummaryInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("playlist_summary_column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_init_column_settings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                mainWindowViewModel.PlaylistSummaryColumns.ResetToDefault();
            }
        }
    }

    public void scrollIntoView()
    {
        customTableView?.ScrollSelectedRowIntoView();
    }

    /// <summary>
    /// 現在 ViewModel で再生対象になっている BMS ファイルの情報で、プレイヤー UI を更新します。
    /// LivetCallMethodAction から引数なしで呼ばれる entrypoint です。
    /// </summary>
    public void _renewBMSPlayerControlInfo()
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: not null } mainWindowViewModel)
        {
            mainWindowViewModel.SetBmsPlayerHeader(mainWindowViewModel.NowPlayingBMS);
            _renewBMSPlayerControlInfo(mainWindowViewModel.NowPlayingBMS);
        }
    }

    private void ClearMainGridSelection()
    {
        customTableView?.ClearSelection();
    }

    private static object _getValueOfPropertyPath(object value, string path)
    {
        if (value == null)
        {
            return null;
        }
        Type type = value.GetType();
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            PropertyInfo property = type.GetProperty(name);
            if (property == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return null;
            }
            value = property.GetValue(value, null);
            if (value == null)
            {
                return null;
            }
            type = property.PropertyType;
        }
        return value;
    }
    private static Action<T> _getSetterOfPropertyPath<T>(object value, string path)
    {
        if (value == null)
        {
            return _ => { };
        }
        Type type = value.GetType();
        PropertyInfo propertyInfo = null;
        object firstArgument = null;
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            propertyInfo = type.GetProperty(name);
            if (propertyInfo == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return _ => { };
            }
            firstArgument = value;
            value = propertyInfo.GetValue(value, null);
            if (value == null && name != array.Last())
            {
                return _ => { };
            }
            type = propertyInfo.PropertyType;
        }
        MethodInfo setMethod = propertyInfo?.GetSetMethod();
        if (setMethod == null)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Set method for property '{propertyInfo?.Name}' not found in path '{path}'");
            return _ => { };
        }
        return Delegate.CreateDelegate(typeof(Action<T>), firstArgument, setMethod) as Action<T>;
    }
    /// <summary>
    /// 現在 ViewModel で選択されている（再生中の）BMSファイルの情報を取得し、
    /// BMSPlayerコントロールのプレビュー画像やバナーを最新状態に更新します。
    /// </summary>
    private List<object> GetSelectedGridRowsSnapshot()
    {
        try
        {
            return customTableView?.GetSelectedRowsSnapshot().Where(row => row != null).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, ChartOperationCapabilities.None);
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, capability);
    }

    private List<ChartOperationTarget> ResolveSelectedChartTargets(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capability)
    {
        return ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            GetSelectedGridRowsSnapshot(),
            sourceScope,
            capability));
    }

    private ChartOperationSourceScope GetCurrentChartOperationSourceScope()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewChartOperationSourceScope ?? ChartOperationSourceScope.Library;
    }

    private MainViewOperationSection GetCurrentMainViewOperationSection()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewOperationSection ?? MainViewOperationSection.Library;
    }

    private static bool IsPendingMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallPending;
    }

    private static bool IsInstalledMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallInstalled;
    }

    private static bool IsPlaylistMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.Playlist;
    }

    private static bool IsFullScanMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.FullScanCheck;
    }

    private static bool IsChartInfoParseErrorMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.ChartInfoParseError;
    }

    private List<ChartFile> GetSelectedBmsFormatCharts(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Select(target => target.Chart)
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private List<BMSFile> GetSelectedBmsFiles(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Where(target => ChartFileKindResolver.IsBmsChartFile(target.Chart))
            .Select(target => target.Chart.GetBmsStorageOwner())
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private List<string> GetSelectedGridHashTargets()
    {
        ChartOperationSourceScope sourceScope = GetCurrentChartOperationSourceScope();
        return [.. GetSelectedGridRowsSnapshot()
            .Select(row =>
            {
                GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target);
                return target;
            })
            .Where(target => target?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true)
            .Select(target => target.Chart?.Md5)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private List<string> GetSelectedChartInfoParseFailureMd5s()
    {
        return [.. GetSelectedChartTargets()
            .Select(target => target.Chart?.Md5)
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static bool ShouldShowChartInfoParseFailureRemovalMenu(bool isChartInfoParseErrorSection, IEnumerable<string> selectedMd5s)
    {
        return isChartInfoParseErrorSection && (selectedMd5s ?? []).Any(md5 => !string.IsNullOrWhiteSpace(md5));
    }

    internal static bool ShouldShowResourceHealthContextMenu(bool isPlaylistContext, IEnumerable<ChartOperationTarget> selectedTargets)
    {
        return ChartContextMenuStateBuilder.ShouldShowResourceHealthContextMenu(isPlaylistContext, selectedTargets);
    }

    internal static bool ShouldUsePlaylistMissingContextMenu(object row, ChartOperationSourceScope sourceScope)
    {
        return TryResolveTableContextMenuPolicy(row, sourceScope, out bool usePlaylistMissingContextMenu)
            && usePlaylistMissingContextMenu;
    }

    internal static bool TryResolveTableContextMenuPolicy(object row, ChartOperationSourceScope sourceScope, out bool usePlaylistMissingContextMenu)
    {
        usePlaylistMissingContextMenu = false;
        if (row == null || row is PlayHistoryRow)
        {
            return false;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target))
        {
            return false;
        }
        usePlaylistMissingContextMenu = target.IsPlaylistMissing;
        return true;
    }

    private List<ScoreViewerTarget> GetSelectedGridScoreViewerTargets()
    {
        return [.. GetSelectedGridRowsSnapshot().Select(TryCreateScoreViewerTarget).Where(target => target != null)];
    }

    private ScoreViewerTarget TryCreateScoreViewerTarget(object row)
    {
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.UseScoreViewer))
        {
            return null;
        }
        string hash = target.Chart.Md5;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        return new ScoreViewerTarget(hash, target.Chart.Path, target.Chart.Title);
    }

    private async Task RunScoreViewerRegistrationAsync(List<ScoreViewerTarget> targets, bool openSingleViewerOnSuccess, string logName)
    {
        if (targets == null || targets.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            ScoreViewerRegistrationPlan plan = await Task.Run(() => viewModel.PrepareScoreViewerRegistration(targets)).Logging(logName + ".preflight");
            if (plan.TargetCount == 0)
            {
                return;
            }

            bool uploadConfirmed = await ConfirmScoreViewerUploadIfNeededAsync(plan);
            ScoreViewerRegistrationResult result = await Task.Run(() => viewModel.CompleteScoreViewerRegistration(plan, uploadConfirmed)).Logging(logName + ".upload");
            await ShowScoreViewerRegistrationResultAsync(result);
            if (openSingleViewerOnSuccess && !string.IsNullOrWhiteSpace(result.LastViewUrl))
            {
                OpenScoreViewerUrl(result.LastViewUrl);
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "score_viewer_registration_failed");
            await TryShowScoreViewerMessageAsync(
                "譜面ビューアへの登録処理に失敗しました。" + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Score Viewer registration failure notification");
        }
    }

    private async Task<bool> ConfirmScoreViewerUploadIfNeededAsync(ScoreViewerRegistrationPlan plan)
    {
        if (plan == null || !plan.HasUploadCandidates)
        {
            return true;
        }
        if (plan.TargetCount == 1 && plan.UploadCandidateCount == 1 && !Settings.Default.ShowScoreViewerRegisterConfirmMsg)
        {
            return true;
        }

        string message;
        if (plan.UploadCandidateCount > 1)
        {
            message = BeMusicSeeker.Properties.Resources.Msg_register_chart
                + Environment.NewLine
                + Environment.NewLine
                + plan.UploadCandidateCount
                + " "
                + BeMusicSeeker.Properties.Resources.Num_chart;
        }
        else
        {
            ScoreViewerRegistrationItem item = plan.UploadCandidates[0];
            message = BeMusicSeeker.Properties.Resources.Msg_show_chart
                + Environment.NewLine
                + Environment.NewLine
                + (item.Target.Title ?? string.Empty)
                + Environment.NewLine
                + "MD5: "
                + item.Hash
                + Environment.NewLine
                + Environment.NewLine
                + "("
                + BeMusicSeeker.Properties.Resources.Msg_hide_message
                + ")";
        }

        UiDialogResult result = await new UiDialogCoordinator().ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.YesNo,
            MessageBoxImage.Asterisk));
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            UiDialogStatus.Failed => throw new InvalidOperationException("Score Viewer registration confirmation dialog failed: " + (result.Exception?.Message ?? result.Status.ToString()), result.Exception),
            _ => throw new InvalidOperationException("Score Viewer registration confirmation dialog was not shown: " + result.Status),
        };
    }

    private async Task ShowScoreViewerRegistrationResultAsync(ScoreViewerRegistrationResult result)
    {
        if (result == null)
        {
            return;
        }
        if (result.HasUploadedRegistration)
        {
            await ShowScoreViewerMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_success_register_chart,
                BeMusicSeeker.Properties.Resources.Information,
                MessageBoxImage.Asterisk,
                "Score Viewer registration success notification");
        }
        if (result.HasFailures)
        {
            await ShowScoreViewerMessageAsync(
                "譜面ビューアへの登録または状態確認に失敗した譜面があります。詳細はログを確認してください。",
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Exclamation,
                "Score Viewer registration partial failure notification");
        }
    }

    private static async Task ShowScoreViewerMessageAsync(string message, string caption, MessageBoxImage icon, string routeName)
    {
        UiDialogResult result = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
            message,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
        if (result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
        {
            throw new InvalidOperationException(routeName + " failed: " + (result.Exception?.Message ?? result.Status.ToString()), result.Exception);
        }
    }

    private static async Task TryShowScoreViewerMessageAsync(string message, string caption, MessageBoxImage icon, string routeName)
    {
        try
        {
            await ShowScoreViewerMessageAsync(message, caption, icon, routeName);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "score_viewer_notification_failed route=" + (routeName ?? string.Empty));
        }
    }

    private static void OpenScoreViewerUrl(string url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                Process.Start(url);
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "score_viewer_open_failed url=" + (url ?? string.Empty));
        }
    }

    private List<BMSTableEntry> GetSelectedGridPlaylistEntries()
    {
        return [.. GetSelectedGridRowsSnapshot().Select(GridRowResolver.GetPlaylistEntry).Where(entry => entry != null)];
    }

    private static ContextMenu GetOwningContextMenu(object source)
    {
        object current = source;
        while (current != null)
        {
            if (current is ContextMenu contextMenu)
            {
                return contextMenu;
            }
            if (current is MenuItem menuItem)
            {
                current = menuItem.Parent;
                continue;
            }
            if (current is FrameworkElement frameworkElement)
            {
                current = frameworkElement.Parent;
                continue;
            }
            break;
        }
        return null;
    }

    private bool TryGetContextMenuRow(object source, out ContextMenu contextMenu, out object row)
    {
        contextMenu = GetOwningContextMenu(source);
        row = null;
        if (contextMenu?.PlacementTarget is not FrameworkElement placementTarget)
        {
            return false;
        }
        if (contextMenu.Tag is CustomTableContextMenuContext customTableContext)
        {
            row = customTableContext.Row;
            return row != null;
        }
        row = placementTarget.DataContext;
        return row != null;
    }

    private bool TryGetContextMenuRow(object source, out object row)
    {
        return TryGetContextMenuRow(source, out _, out row);
    }

    private bool TryGetContextMenuChartTarget(object primarySource, object fallbackSource, out ChartOperationTarget target)
    {
        if (TryGetContextMenuRow(primarySource, out object row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        if (!ReferenceEquals(primarySource, fallbackSource)
            && TryGetContextMenuRow(fallbackSource, out row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        target = null;
        return false;
    }

    private bool TryGetTableContextMenuResource(object row, string logPrefix, out ContextMenu contextMenu, out bool usePlaylistMissingContextMenu)
    {
        contextMenu = null;
        usePlaylistMissingContextMenu = false;
        if (row == null)
        {
            return false;
        }
        string resourceKey;
        if (PlayHistoryContextMenuState.TryCreate(row, out _))
        {
            resourceKey = "playHistoryContextMenu";
            usePlaylistMissingContextMenu = false;
        }
        else if (row is PlayHistoryRow)
        {
            return false;
        }
        else if (!TryResolveTableContextMenuPolicy(row, GetCurrentChartOperationSourceScope(), out usePlaylistMissingContextMenu))
        {
            return false;
        }
        else
        {
            resourceKey = usePlaylistMissingContextMenu ? "tableContextMenuPlaylistMissing" : "tableContextMenu";
        }
        if (TryFindResource(resourceKey) is not ContextMenu foundContextMenu)
        {
            return false;
        }
        contextMenu = foundContextMenu;
        NLogWrapper.FileLogger?.Info(logPrefix + " rowType=" + row?.GetType().FullName + " missing=" + usePlaylistMissingContextMenu + " resourceKey=" + resourceKey);
        return true;
    }
    /// <summary>
    /// 指定された確定的 BMSFile インスタンス情報を用いて、
    /// BMSPlayerコントロールの画像表示（stagefile と banner）を同期します。
    /// </summary>
    /// <param name="bmsFile">更新対象となる BMS ファイル要素。</param>
    private void _renewBMSPlayerControlInfo(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return;
        }
        WriteableBitmap writeableBitmap = null;
        try
        {
            string text = ((string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.stagefile)) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.stagefile));
            if (!string.IsNullOrWhiteSpace(text) && LongPathFileSystem.FileExists(text))
            {
                var memoryStream = new MemoryStream(LongPathFileSystem.ReadAllBytes(text));
                writeableBitmap = new WriteableBitmap(BitmapFrame.Create(memoryStream));
                memoryStream.Close();
                gridBMSPlayerImage.Source = writeableBitmap;
            }
            else
            {
                gridBMSPlayerImage.Source = panelImage;
            }
        }
        catch
        {
            gridBMSPlayerImage.Source = panelImage;
        }
        try
        {
            string text2 = ((string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.banner)) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.banner));
            if (!string.IsNullOrWhiteSpace(text2) && LongPathFileSystem.FileExists(text2))
            {
                var imageBrush = new ImageBrush();
                var memoryStream2 = new MemoryStream(LongPathFileSystem.ReadAllBytes(text2));
                var imageSource = new WriteableBitmap(BitmapFrame.Create(memoryStream2));
                memoryStream2.Close();
                imageBrush.ImageSource = imageSource;
                imageBrush.Stretch = Stretch.Fill;
                gridBMSPlayerControlsBanner.Background = imageBrush;
            }
            else if (writeableBitmap == null)
            {
                gridBMSPlayerControlsBanner.Background = null;
            }
            else
            {
                gridBMSPlayerControlsBanner.Background = new ImageBrush(writeableBitmap)
                {
                    Stretch = Stretch.UniformToFill
                };
            }
        }
        catch
        {
            if (writeableBitmap == null)
            {
                gridBMSPlayerControlsBanner.Background = null;
            }
            else
            {
                gridBMSPlayerControlsBanner.Background = new ImageBrush(writeableBitmap)
                {
                    Stretch = Stretch.UniformToFill
                };
            }
        }
        gridBMSPlayerControlsBanner.BorderThickness = ((gridBMSPlayerControlsBanner.Background == null) ? new Thickness(0.0) : new Thickness(1.0, 0.0, 1.0, 0.0));
    }
    private void keywordSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            RefreshKeywordSearchSuggestions(textBox, forceHistory: false);
        }
    }

    private void keywordSearchBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox && string.IsNullOrWhiteSpace(textBox.Text))
        {
            // NOTE:
            // Popup を focus event の処理中に開くと、StaysOpen=false の外部 focus 判定で
            // 即時に閉じる環境があるため、TextBox の focus が確定してから履歴を表示します。
            Dispatcher.BeginInvoke((Action)delegate
            {
                if (textBox.IsKeyboardFocusWithin && string.IsNullOrWhiteSpace(textBox.Text))
                {
                    RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
                }
            }, DispatcherPriority.Input);
        }
    }

    private void keywordSearchBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            CommitKeywordSearchHistory(textBox);
            CloseKeywordSearchSuggestions(IsPlaylistSummaryKeywordSearchBox(textBox));
        }
    }

    private void keywordSearchWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var clickedElement = e.OriginalSource as DependencyObject;
        CloseKeywordSearchSuggestionsIfOutside(clickedElement, false);
        CloseKeywordSearchSuggestionsIfOutside(clickedElement, true);
    }

    private void MainWindow_Deactivated(object sender, EventArgs e)
    {
        CloseKeywordSearchSuggestions(false);
        CloseKeywordSearchSuggestions(true);
    }

    private void keywordSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        bool isPopupOpen = IsKeywordSearchSuggestionPopupOpen(isPlaylistSummary);
        if (e.Key == Key.Escape && isPopupOpen)
        {
            CloseKeywordSearchSuggestions(isPlaylistSummary);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Space)
        {
            RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
            FocusFirstKeywordSearchSuggestion(isPlaylistSummary);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (!isPopupOpen)
            {
                RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
            }
            NavigateKeywordSearchSuggestion(isPlaylistSummary, e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Tab) && isPopupOpen)
        {
            if (ApplySelectedKeywordSearchSuggestion(textBox))
            {
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter)
        {
            CommitKeywordSearchHistory(textBox);
        }
    }

    private void keywordSearchSuggestionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }
        var listBoxItem = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        KeywordSearchSuggestionItem suggestion = listBoxItem?.DataContext as KeywordSearchSuggestionItem ?? listBox.SelectedItem as KeywordSearchSuggestionItem;
        TextBox textBox = ReferenceEquals(listBox, PlaylistSummaryKeywordSearchSuggestionListBox) ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        if (suggestion != null && ApplyKeywordSearchSuggestion(textBox, suggestion))
        {
            e.Handled = true;
        }
    }

    private void keywordSearchSuggestionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }
        bool isPlaylistSummary = ReferenceEquals(listBox, PlaylistSummaryKeywordSearchSuggestionListBox);
        TextBox textBox = isPlaylistSummary ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (ApplySelectedKeywordSearchSuggestion(textBox))
            {
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            CloseKeywordSearchSuggestions(isPlaylistSummary);
            textBox?.Focus();
            e.Handled = true;
        }
    }

    private void RefreshKeywordSearchSuggestions(TextBox textBox, bool forceHistory)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || textBox == null)
        {
            return;
        }
        if (IsPlaylistSummaryKeywordSearchBox(textBox))
        {
            viewModel.RefreshPlaylistSummaryKeywordSearchSuggestions(textBox.Text, textBox.CaretIndex, forceHistory);
        }
        else
        {
            viewModel.RefreshKeywordSearchSuggestions(textBox.Text, textBox.CaretIndex, forceHistory);
        }
    }

    private void CommitKeywordSearchHistory(TextBox textBox)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || textBox == null)
        {
            return;
        }
        if (IsPlaylistSummaryKeywordSearchBox(textBox))
        {
            viewModel.CommitPlaylistSummaryKeywordSearchHistory(textBox.Text);
        }
        else
        {
            viewModel.CommitKeywordSearchHistory(textBox.Text);
        }
    }

    private bool ApplySelectedKeywordSearchSuggestion(TextBox textBox)
    {
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        var suggestion = listBox?.SelectedItem as KeywordSearchSuggestionItem;
        if (suggestion == null && listBox?.Items.Count > 0)
        {
            suggestion = listBox.Items[0] as KeywordSearchSuggestionItem;
        }
        return suggestion != null && ApplyKeywordSearchSuggestion(textBox, suggestion);
    }

    private bool ApplyKeywordSearchSuggestion(TextBox textBox, KeywordSearchSuggestionItem suggestion)
    {
        if (textBox == null || suggestion == null)
        {
            return false;
        }
        string appliedText = suggestion.Apply(textBox.Text, out int caretIndex);
        textBox.Text = appliedText;
        textBox.CaretIndex = Math.Max(0, Math.Min(caretIndex, textBox.Text.Length));
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        if (suggestion.Kind == KeywordSearchSuggestionKind.History)
        {
            CommitKeywordSearchHistory(textBox);
        }
        CloseKeywordSearchSuggestions(isPlaylistSummary);
        textBox.Focus();
        return true;
    }

    private void NavigateKeywordSearchSuggestion(bool isPlaylistSummary, int delta)
    {
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (listBox == null || listBox.Items.Count == 0)
        {
            return;
        }
        int selectedIndex = listBox.SelectedIndex;
        if (selectedIndex < 0)
        {
            selectedIndex = delta >= 0 ? 0 : listBox.Items.Count - 1;
        }
        else
        {
            selectedIndex += delta;
            if (selectedIndex < 0)
            {
                selectedIndex = listBox.Items.Count - 1;
            }
            else if (selectedIndex >= listBox.Items.Count)
            {
                selectedIndex = 0;
            }
        }
        listBox.SelectedIndex = selectedIndex;
        listBox.ScrollIntoView(listBox.SelectedItem);
        listBox.Focus();
    }

    private void FocusFirstKeywordSearchSuggestion(bool isPlaylistSummary)
    {
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (listBox == null || listBox.Items.Count == 0)
        {
            return;
        }
        listBox.SelectedIndex = 0;
        listBox.Focus();
    }

    private ListBox GetKeywordSearchSuggestionListBox(bool isPlaylistSummary)
    {
        return isPlaylistSummary ? PlaylistSummaryKeywordSearchSuggestionListBox : KeywordSearchSuggestionListBox;
    }

    private bool IsPlaylistSummaryKeywordSearchBox(TextBox textBox)
    {
        return ReferenceEquals(textBox, KeywordSearchBoxPlaylistSummary);
    }

    private bool IsKeywordSearchSuggestionPopupOpen(bool isPlaylistSummary)
    {
        return base.DataContext is MainWindowViewModel viewModel
            && (isPlaylistSummary ? viewModel.PlaylistWorkspace.IsPlaylistSummaryKeywordSearchSuggestionPopupOpen : viewModel.IsKeywordSearchSuggestionPopupOpen);
    }

    private void CloseKeywordSearchSuggestions(bool isPlaylistSummary)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (isPlaylistSummary)
        {
            viewModel.ClosePlaylistSummaryKeywordSearchSuggestions();
        }
        else
        {
            viewModel.CloseKeywordSearchSuggestions();
        }
    }

    private void CloseKeywordSearchSuggestionsIfOutside(DependencyObject clickedElement, bool isPlaylistSummary)
    {
        if (!IsKeywordSearchSuggestionPopupOpen(isPlaylistSummary))
        {
            return;
        }
        TextBox textBox = isPlaylistSummary ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (IsDescendantOf(clickedElement, textBox) || IsDescendantOf(clickedElement, listBox))
        {
            return;
        }
        CloseKeywordSearchSuggestions(isPlaylistSummary);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        if (child == null || ancestor == null)
        {
            return false;
        }
        DependencyObject current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            DependencyObject visualParent = current is Visual || current is Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private bool CanEditInstallDestinationInCurrentSection()
    {
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        return IsPendingMainViewSection(section) || IsFullScanMainViewSection(section);
    }

    private static T FindTemplateElement<T>(FrameworkElement source, string elementName) where T : class
    {
        FrameworkElement current = source;
        while (current != null)
        {
            if (current.FindName(elementName) is T found)
            {
                return found;
            }
            current = current.Parent as FrameworkElement;
        }
        return null;
    }
    private static T FindNamedDescendant<T>(FrameworkElement root, string elementName) where T : class
    {
        if (root == null)
        {
            return null;
        }
        if (root.FindName(elementName) is T foundByName)
        {
            return foundByName;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                T found = FindNamedDescendant<T>(child, elementName);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static T FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
            {
                return typedChild;
            }
            T descendant = FindVisualDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private async Task OpenUrlFromRowAsync(object row, bool isDiffUrl, string blockReason, MouseButtonEventArgs mouseEventArgs = null)
    {
        if (ShouldBlockStartupUiInteraction(blockReason))
        {
            if (mouseEventArgs != null)
            {
                mouseEventArgs.Handled = true;
            }
            return;
        }
        await OpenSinglePlaylistUrlAsync(row, isDiffUrl);
    }

    private async Task OpenSinglePlaylistUrlAsync(object row, bool isDiffUrl)
    {
        Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(row) : GridRowResolver.GetUrl(row);
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        await OpenSinglePlaylistUrlAsync(url);
    }

    private async Task OpenSinglePlaylistUrlAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        if (Settings.Default.ScanBmsFilesOnStartup && Settings.Default.AutoInstall)
        {
            PlaylistUrlDownloadResult downloadResult = playlistUrlBulkDownloadRunning
                ? await DownloadPlaylistUrlCandidateAsync(url)
                : await DownloadSinglePlaylistUrlCandidateWithStatusAsync(url);
            switch (downloadResult.Kind)
            {
                case PlaylistUrlDownloadResultKind.Downloaded when !string.IsNullOrWhiteSpace(downloadResult.FilePath) && LongPathFileSystem.FileExists(downloadResult.FilePath):
                    installChartPackages([downloadResult.FilePath]);
                    newlyInstalledTreeViewItem.IsExpanded = true;
                    return;
                case PlaylistUrlDownloadResultKind.BlockedBySizeLimit:
                    return;
            }
        }
        Process.Start(url.ToString());
    }

    private async Task<PlaylistUrlDownloadResult> DownloadSinglePlaylistUrlCandidateWithStatusAsync(Uri url)
    {
        var viewModel = base.DataContext as MainWindowViewModel;
        string displayName = url?.ToString() ?? string.Empty;
        viewModel?.UpdatePlaylistUrlDownloadStatus(true, 1, 0, displayName);
        try
        {
            PlaylistUrlDownloadResult result = await DownloadPlaylistUrlCandidateAsync(url);
            viewModel?.UpdatePlaylistUrlDownloadStatus(true, 1, 1, displayName);
            return result;
        }
        finally
        {
            viewModel?.UpdatePlaylistUrlDownloadStatus(false, 0, 0, string.Empty);
        }
    }

    private void playlistRootSelect(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("tree_playlist_root_select"))
        {
            return;
        }
        if (e.Source is TreeViewItem)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.SelectPlaylistSummary();
            }).Logging("playlistRootSelect");
        }
    }

    /// <summary>
    /// メインツリーやプレイリストツリー上で左クリックが行われた際の処理をハンドリングします。
    /// Node（TreeViewItem）の選択状態を判定し、クリックされた要素に応じてViewModel側に
    /// イベント（曲一覧の再生成等）を透過処理します。
    /// </summary>
    private void treeViewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView treeViewControl)
        {
            return;
        }
        // スクロールバーやExpanderトグルのクリックではフォーカス移譲や更新を行わない
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null ||
            FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) != null)
        {
            return;
        }
        treeViewControl.Focus();
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        TreeViewItem treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem == null || !treeViewItem.IsSelected)
        {
            return;
        }
        if (treeViewControl == treeViewPlaylist)
        {
            ForceRefreshPlaylistTreeSelection(treeViewItem);
        }
        else if (treeViewControl == treeView)
        {
            ForceRefreshMainTreeSelection(treeViewItem);
        }
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }
            current = GetParentObject(current);
        }
        return null;
    }

    /// <summary>
    /// VisualTree を子方向に探索し、最初に見つかった指定型の要素を返します。
    /// </summary>
    /// <typeparam name="T">検索対象の <see cref="DependencyObject"/> 型。</typeparam>
    /// <param name="parent">探索開始位置。</param>
    /// <returns>最初に見つかった要素。見つからない場合は <see langword="null"/>。</returns>
    private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is T result)
            {
                return result;
            }
            T descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private static DependencyObject GetParentObject(DependencyObject current)
    {
        if (current == null)
        {
            return null;
        }
        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }
        if (current is ContentElement)
        {
            return LogicalTreeHelper.GetParent(current);
        }
        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch
        {
            return LogicalTreeHelper.GetParent(current);
        }
    }

    private static TreeViewItem GetSelectedTreeViewItem(ItemsControl parent)
    {
        if (parent == null)
        {
            return null;
        }
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem treeViewItem)
            {
                continue;
            }
            if (treeViewItem.IsSelected)
            {
                return treeViewItem;
            }
            TreeViewItem selectedTreeViewItem = GetSelectedTreeViewItem(treeViewItem);
            if (selectedTreeViewItem != null)
            {
                return selectedTreeViewItem;
            }
        }
        return null;
    }

    /// <summary>
    /// UI仮想化などの影響で対象となる子要素(TreeViewItem)のコンテナが未生成の場合でも、
    /// ツリーの選択状態が無選択（LostFocus）で停止してしまわないように、最も近い兄弟ノードまたは
    /// ルートノードへ選択状態をフォールバック遷移させます。
    /// </summary>
    /// <param name="rootTreeViewItem">フォールバックの起点となる親（ルート）要素。</param>
    /// <param name="currentItem">現在選択されていたが削除等により遷移が必要なデータ項目。</param>
    /// <param name="logScope">ログ出力用のスコープ名。</param>
    private void SelectNextSiblingOrRoot(TreeViewItem rootTreeViewItem, object currentItem, string logScope)
    {
        if (rootTreeViewItem == null)
        {
            return;
        }
        int count = rootTreeViewItem.Items.Count;
        if (count == 0)
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        int currentIndex = rootTreeViewItem.Items.IndexOf(currentItem);
        if (currentIndex == 0 && count == 1)
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        if (currentIndex == -1)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=current_not_found root=" + rootTreeViewItem.Header);
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        int targetIndex = ((count - 1 == currentIndex) ? (currentIndex - 1) : (currentIndex + 1));
        object targetDataContext = rootTreeViewItem.Items[targetIndex];
        if (!TrySelectChildTreeViewItemByDataContext(rootTreeViewItem, targetDataContext, logScope))
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
        }
    }

    private bool TrySelectChildTreeViewItemByDataContext(TreeViewItem rootTreeViewItem, object targetDataContext, string logScope)
    {
        if (rootTreeViewItem == null || targetDataContext == null)
        {
            return false;
        }
        if (rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) is not TreeViewItem treeViewItem)
        {
            rootTreeViewItem.UpdateLayout();
            treeViewItem = rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) as TreeViewItem;
        }
        treeViewItem ??= WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(rootTreeViewItem, targetDataContext);
        if (treeViewItem == null)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=container_not_realized root=" + rootTreeViewItem.Header + " target=" + targetDataContext);
            return false;
        }
        SelectTreeViewItemWithFocus(treeViewItem);
        return true;
    }

    private static void SelectTreeViewItemWithFocus(TreeViewItem treeViewItem)
    {
        if (treeViewItem == null)
        {
            return;
        }
        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
    }

    /// <summary>
    /// プレイリストツリー用ノードから、一覧更新に使用するフィルタ種別を解決します。
    /// 特殊ノードは文字列逆変換ではなく、ノード種別を直接見て分岐します。
    /// </summary>
    /// <param name="folderNode">判定対象のプレイリストフォルダノード。</param>
    /// <returns>対応するプレイリストフィルタ種別。</returns>
    private static PlaylistDetailFilter GetPlaylistFilterType(PlaylistFolderNode folderNode)
    {
        if (folderNode == null || !folderNode.IsSpecial)
        {
            return PlaylistDetailFilter.PlaylistFilter;
        }
        return folderNode.SpecialKind switch
        {
            PlaylistFolderNodeSpecialKind.NotOwned => PlaylistDetailFilter.PlaylistNotOwnedFilterSelected,
            _ => PlaylistDetailFilter.PlaylistFilter
        };
    }

    /// <summary>
    /// プレイリストツリー選択時に ViewModel へ渡すフォルダ識別子を取得します。
    /// 通常ノードは論理フォルダ名、特殊ノードは表示名を返します。
    /// </summary>
    /// <param name="folderNode">対象ノード。</param>
    /// <returns>ViewModel 側で解釈するフォルダキー。</returns>
    private static string GetPlaylistFolderSelectionKey(PlaylistFolderNode folderNode)
    {
        if (folderNode == null)
        {
            return null;
        }
        return folderNode.IsSpecial ? folderNode.DisplayName : folderNode.FolderName;
    }

    /// <summary>
    /// プレイリストツリー項目の <c>DataContext</c> を、
    /// <see cref="PlaylistFolderNode"/> として安全に扱えるか判定します。
    /// </summary>
    /// <param name="dataContext">判定対象の DataContext。</param>
    /// <param name="folderNode">変換に成功した場合のノード。</param>
    /// <returns><see cref="PlaylistFolderNode"/> として扱える場合は <c>true</c>。</returns>
    private static bool TryGetPlaylistFolderNode(object dataContext, out PlaylistFolderNode folderNode)
    {
        folderNode = dataContext as PlaylistFolderNode;
        return folderNode != null;
    }

    private void ForceRefreshPlaylistTreeSelection(TreeViewItem selectedItem)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || selectedItem == null)
        {
            return;
        }
        if (selectedItem == treeViewItemPlaylist)
        {
            Task.Run(delegate
            {
                viewModel.SelectPlaylistSummary();
            }).Logging("ForceRefreshPlaylistTreeSelection");
            return;
        }
        BMSTable bmsTable = null;
        string folderName = null;
        PlaylistDetailFilter type = PlaylistDetailFilter.PlaylistFilter;
        if (selectedItem.DataContext is BMSTable bMSTable2)
        {
            bmsTable = bMSTable2;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(selectedItem.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            folderName = GetPlaylistFolderSelectionKey(folderNode);
            type = GetPlaylistFilterType(folderNode);
            TreeViewItem ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(selectedItem));
            while (ancestor != null && ancestor.DataContext is not BMSTable)
            {
                ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(ancestor));
            }
            bmsTable = ancestor?.DataContext as BMSTable;
        }
        if (bmsTable != null)
        {
            Task.Run(delegate
            {
                viewModel.ExecPlaylistFilter(bmsTable, folderName, type);
            }).Logging("ForceRefreshPlaylistTreeSelection");
        }
    }

    private void ForceRefreshMainTreeSelection(TreeViewItem selectedItem)
    {
        if (selectedItem == null)
        {
            return;
        }

        // 下段ツリーはノード種別ごとの Selected ハンドラを再利用して更新する
        // (ライブラリ/インストール/メンテナンスを一律にカバーする)
        selectedItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, selectedItem));
    }

    /// <summary>
    /// プレイリストツリー内の「フォルダ」上でキーボード操作が行われた際のイベントハンドラ。
    /// F2キー押下時に、該当フォルダの名称編集モード (EditableTextBlockの切り替え) を起動します。
    /// </summary>
    private void playlistTableFolderkeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && sender is TreeViewItem treeViewItem && treeViewItem.Template.FindName("PART_Header", treeViewItem) is ContentPresenter templatedParent && treeViewItem.HeaderTemplate.FindName("etbPlaylistTableFolder", templatedParent) is EditableTextBlock editableTextBlock)
        {
            if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
            {
                return;
            }
            editableTextBlock.IsInEditMode = true;
            e.Handled = true;
        }
    }

    private async void playlistTableFolderClicked(object sender, MouseButtonEventArgs e)
    {
        if (!(e.ChangedButton == MouseButton.Left) || !(sender is EditableTextBlock { TemplatedParent: ContentPresenter contentPresenter } editableTextBlock))
        {
            return;
        }
        if (contentPresenter.TemplatedParent is not TreeViewItem ownerTreeViewItem || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
        {
            return;
        }

        object originalEditableDataContext = editableTextBlock.DataContext;
        object originalHeaderDataContext = contentPresenter.DataContext;
        await Task.Delay(1000);

        // NOTE:
        // Recycling有効時は待機中にコンテナ再利用が起きるため、編集開始前に同一対象か再検証する。
        // 再利用済みなら誤った行の編集開始を避けるため何もしない。
        if (!ReferenceEquals(editableTextBlock.DataContext, originalEditableDataContext) || !ReferenceEquals(contentPresenter.DataContext, originalHeaderDataContext) || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out folderNode) && !folderNode.IsEditable)
        {
            return;
        }
        editableTextBlock.IsInEditMode = true;
    }

    private void playlistTableFolderNameChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not EditableTextBlock editableTextBlock || !editableTextBlock.IsTextChanged())
        {
            return;
        }
        if (!(editableTextBlock.TemplatedParent is ContentPresenter { TemplatedParent: TreeViewItem templatedParent }))
        {
            return;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(templatedParent);
        while (parent is not TreeViewItem && parent is not TreeView && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null || parent is TreeView)
        {
            return;
        }
        if ((parent as TreeViewItem).DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            if (!TryGetPlaylistFolderNode(templatedParent.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsEditable)
            {
                return;
            }
            string nameBefore = folderNode.FolderName;
            string nameAfter = editableTextBlock.Text;
            Task.Run(delegate
            {
                viewModel.RenameFolderBMSTable(bmsTable, nameBefore, nameAfter);
            }).Logging("playlistTableFolderNameChanged");
        }
    }

    private void playlistTableSelected(object sender, RoutedEventArgs e)
    {
        var treeViewItem3 = e.OriginalSource as TreeViewItem;
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (treeViewItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        e.Handled = true;
        PlaylistDetailFilter type = PlaylistDetailFilter.PlaylistFilter;
        string folderName;
        if (e.Source is TreeViewItem treeViewItem2)
        {
            folderName = null;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(treeViewItem3.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            folderName = GetPlaylistFolderSelectionKey(folderNode);
            type = GetPlaylistFilterType(folderNode);
        }
        e.Handled = true;
        Task.Run(delegate
        {
            viewModel.ExecPlaylistFilter(bmsTable, folderName, type);
        }).Logging("playlistTableSelected");
    }

    private void treeViewItemOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock reference)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(reference);
            while (parent is not TreeViewItem && parent is not TreeView && parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent != null && parent is TreeViewItem && ((TreeViewItem)parent).Focusable && !((TreeViewItem)parent).IsSelected)
            {
                ((TreeViewItem)parent).IsSelected = true;
            }
        }
    }

    private void mouseRightButtonDown(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem treeViewItem)
        {
            treeViewItem.IsSelected = true;
        }
    }

    private void directoryFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_directory_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.DirectoryFilter, treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private async void playHistoryPeriodSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_play_history_period_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || sender is not TreeViewItem treeRoot
            || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        PlayHistoryPeriodRequest request;
        if (treeViewItem.DataContext is PlayHistoryPeriodTreeItem periodTreeItem)
        {
            request = periodTreeItem.Request;
        }
        else if (ReferenceEquals(treeRoot, treeViewItem))
        {
            request = PlayHistoryPeriodRequest.All();
        }
        else if (treeViewItem.Tag is string tag)
        {
            request = PlayHistoryPeriodRequest.FromTag(tag);
        }
        else
        {
            treeRoot.IsSelected = true;
            treeRoot.IsExpanded = true;
            return;
        }
        if (request == null)
        {
            return;
        }
        long requestId = viewModel.BeginPlayHistoryFilterRequest(request);
        await Task.Run(delegate
        {
            viewModel.ExecPlayHistoryFilter(request, requestId);
        }).Logging("playHistoryPeriodSelect");
        treeRoot.IsExpanded = true;
    }

    private void artistFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_artist_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.ArtistFilter, treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void rootFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_root_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TreeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.FilterNone);
        }
    }

    private List<PlaylistSummaryRow> getSelectedPlaylistSummaryRows(PlaylistSummaryRow fallback = null)
    {
        List<PlaylistSummaryRow> list = [];
        if (customTablePlaylistSummary != null && customTablePlaylistSummary.IsVisible)
        {
            list = [.. customTablePlaylistSummary.GetSelectedRowsSnapshot().OfType<PlaylistSummaryRow>().Where(r => r != null)];
        }
        if ((list == null || list.Count == 0) && fallback != null)
        {
            list = [fallback];
        }
        return list ?? [];
    }

    private PlaylistSummaryRow resolvePlaylistSummaryRowFromSender(object sender)
    {
        if (TryGetContextMenuRow(sender, out object row) && row is PlaylistSummaryRow contextRow)
        {
            return contextRow;
        }
        if (sender is FrameworkElement { DataContext: PlaylistSummaryRow playlistSummaryRow })
        {
            return playlistSummaryRow;
        }
        return getSelectedPlaylistSummaryRows().FirstOrDefault();
    }

    /// <summary>
    /// プレイリストサマリー行のダブルクリック時に、対応するプレイリストを左ツリーで選択します。
    /// 既存のツリー選択イベントを再利用し、プレイリスト絞り込み表示への遷移も従来の選択経路に委ねます。
    /// </summary>
    /// <summary>
    /// プレイリストサマリー行に対応するプレイリストをプレイリストツリー上で選択します。
    /// 再読み込み後に <see cref="PlaylistSummaryRow.TableRef"/> が古い参照になっていても、既存の再選択補助ロジックで解決を試みます。
    /// </summary>
    /// <param name="playlistSummaryRow">選択元のプレイリストサマリー行。</param>
    /// <returns>プレイリストツリー項目の選択に成功した場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItemFromSummary(PlaylistSummaryRow playlistSummaryRow)
    {
        if (playlistSummaryRow?.TableRef == null)
        {
            return false;
        }
        BMSTable selectionTarget = FindReloadedPlaylistTable(playlistSummaryRow.TableRef);
        bool restoredByHeader = false;
        if (selectionTarget == null)
        {
            selectionTarget = FindPlaylistTableByName(playlistSummaryRow.Name);
            restoredByHeader = selectionTarget != null;
        }
        string playlistName = selectionTarget?.name ?? playlistSummaryRow.Name ?? string.Empty;
        string playlistId = playlistSummaryRow.PlaylistId?.ToString() ?? string.Empty;
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=target_not_found table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader);
            return false;
        }
        bool selected = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!selected)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=container_not_realized table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader + " realize_by_index_available=" + realizeByIndexAvailable);
            return false;
        }
        NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=true table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader + " usedVirtualizationFallback=" + usedVirtualizationFallback + " expanded=true");
        return true;
    }

    /// <summary>
    /// 指定されたプレイリストをプレイリストツリー上で展開・選択します。
    /// 仮想化で未生成のトップレベル項目については、インデックス指定での実体化を試みます。
    /// </summary>
    /// <param name="targetTable">選択対象のトップレベルプレイリスト。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>プレイリストツリー上で選択できた場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return false;
        }
        treeViewItemPlaylist.IsExpanded = true;
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        TreeViewItem playlistTreeViewItem = TryGetPlaylistTreeViewItem(targetTable, out usedVirtualizationFallback, out realizeByIndexAvailable);
        if (playlistTreeViewItem == null)
        {
            return false;
        }
        playlistTreeViewItem.IsExpanded = true;
        playlistTreeViewItem.UpdateLayout();
        playlistTreeViewItem.BringIntoView();
        playlistTreeViewItem.IsSelected = true;
        playlistTreeViewItem.Focus();
        return true;
    }

    /// <summary>
    /// 指定されたトップレベルプレイリストに対応する <see cref="TreeViewItem"/> を取得します。
    /// </summary>
    /// <param name="targetTable">プレイリストツリー直下の <see cref="BMSTable"/>。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>対応する <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return null;
        }
        int playlistIndex = treeViewItemPlaylist.Items.IndexOf(targetTable);
        if (playlistIndex < 0)
        {
            return null;
        }
        return TryGetPlaylistTreeViewItemByIndex(playlistIndex, out usedVirtualizationFallback, out realizeByIndexAvailable);
    }

    /// <summary>
    /// 指定インデックスのトップレベルプレイリスト項目コンテナを取得します。
    /// 画面外で未生成の場合は、仮想化回避の実体化を試みます。
    /// </summary>
    /// <param name="playlistIndex">プレイリストルート直下のインデックス。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>生成済みの <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItemByIndex(int playlistIndex, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (playlistIndex < 0)
        {
            return null;
        }
        if (treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) is TreeViewItem playlistTreeViewItem)
        {
            return playlistTreeViewItem;
        }
        usedVirtualizationFallback = TryRealizeVirtualizedPlaylistItem(playlistIndex, out realizeByIndexAvailable);
        if (!usedVirtualizationFallback)
        {
            return null;
        }
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        return treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) as TreeViewItem;
    }

    /// <summary>
    /// 仮想化で未生成のトップレベルプレイリスト項目を、インデックス指定で可視領域へ移動して実体化させます。
    /// </summary>
    /// <param name="playlistIndex">実体化したいプレイリストルート直下のインデックス。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>可視化要求を実行できた場合は <see langword="true"/>。</returns>
    private bool TryRealizeVirtualizedPlaylistItem(int playlistIndex, out bool realizeByIndexAvailable)
    {
        realizeByIndexAvailable = playlistTreeBringIndexIntoViewMethod != null;
        if (playlistIndex < 0)
        {
            return false;
        }
        VirtualizingStackPanel playlistItemsHostPanel = TryGetPlaylistItemsHostPanel();
        if (playlistItemsHostPanel == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=panel_not_found index=" + playlistIndex);
            return false;
        }
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=bring_index_method_not_found index=" + playlistIndex);
            return false;
        }
        try
        {
            // NOTE:
            // TreeView の仮想化が有効だと、画面外のトップレベル項目は ContainerFromIndex で null のままになります。
            // 公開 API にはインデックス単位で実体化を促す手段がないため、WPF 内部の BringIndexIntoView を局所的に利用します。
            playlistTreeBringIndexIntoViewMethod.Invoke(playlistItemsHostPanel, [playlistIndex]);
            treeViewPlaylist.UpdateLayout();
            treeViewItemPlaylist.UpdateLayout();
            return true;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=invoke_failed index=" + playlistIndex + " exception=" + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// プレイリストルート配下の items host となる <see cref="VirtualizingStackPanel"/> を取得します。
    /// </summary>
    /// <returns>プレイリストのトップレベル項目を管理する <see cref="VirtualizingStackPanel"/>。取得できない場合は <see langword="null"/>。</returns>
    private VirtualizingStackPanel TryGetPlaylistItemsHostPanel()
    {
        treeViewItemPlaylist.ApplyTemplate();
        treeViewItemPlaylist.UpdateLayout();
        ItemsPresenter playlistItemsPresenter = FindDescendant<ItemsPresenter>(treeViewItemPlaylist);
        if (playlistItemsPresenter == null)
        {
            return null;
        }
        playlistItemsPresenter.ApplyTemplate();
        return FindDescendant<VirtualizingStackPanel>(playlistItemsPresenter);
    }

    /// <summary>
    /// 指定されたプレイリスト名に一致するトップレベルプレイリストを検索します。
    /// </summary>
    /// <param name="playlistName">検索対象のプレイリスト名。</param>
    /// <returns>一致する <see cref="BMSTable"/>。見つからない場合は <see langword="null"/>。</returns>
    private BMSTable FindPlaylistTableByName(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return null;
        }
        return treeViewItemPlaylist.Items.OfType<BMSTable>().FirstOrDefault(playlistTable => playlistTable != null && string.Equals(playlistTable.name, playlistName, StringComparison.Ordinal));
    }

    private async void playlistSummaryLinkClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button { DataContext: PlaylistSummaryRow playlistSummaryRow }) || playlistSummaryRow.LinkUri == null)
        {
            return;
        }
        await OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri).Logging("playlistSummaryLinkClick");
    }

    private Task OpenPlaylistSummaryUriAsync(Uri uri)
    {
        if (uri == null)
        {
            return Task.CompletedTask;
        }
        return Task.Run(delegate
        {
            try
            {
                Process.Start(uri.ToString());
            }
            catch
            {
            }
        });
    }

    private async void playlistSummarySyncCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is CheckBox { DataContext: PlaylistSummaryRow playlistSummaryRow } checkBox))
        {
            return;
        }
        bool flag = checkBox.IsChecked == true;
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        string confirmationText = (!flag)
            ? "同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？"
            : "同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？";
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), confirmationText, "警告", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            checkBox.IsChecked = !flag;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, flag, null);
            }).Logging("playlistSummarySyncCheckBoxClick");
        }
        e.Handled = true;
    }

    private async Task ApplyPlaylistSummarySyncFromCustomTableAsync(PlaylistSummaryRow playlistSummaryRow, bool flag)
    {
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        string confirmationText = (!flag)
            ? "同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？"
            : "同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？";
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), confirmationText, "警告", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            customTablePlaylistSummary?.RefreshDisplay();
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, flag, null);
            });
        }
    }

    private async void playlistSummaryRootCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is CheckBox { DataContext: PlaylistSummaryRow playlistSummaryRow } checkBox))
        {
            return;
        }
        bool flag = checkBox.IsChecked == true;
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, null, flag);
            }).Logging("playlistSummaryRootCheckBoxClick");
        }
        e.Handled = true;
    }

    private async Task ApplyPlaylistSummaryRootFromCustomTableAsync(PlaylistSummaryRow playlistSummaryRow, bool flag)
    {
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, null, flag);
            });
        }
    }

    private async Task ApplyPlaylistSummaryBmtOutputFromCustomTableAsync(PlaylistSummaryRow playlistSummaryRow, bool flag)
    {
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryBmtOutput(selectedPlaylistSummaryRows, flag);
            });
        }
    }

    private async void playlistSummaryContextMenuResyncClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (base.DataContext is MainWindowViewModel viewModel && selectedPlaylistSummaryRows.Count > 0)
        {
            await viewModel.ResyncPlaylistsAsync(selectedPlaylistSummaryRows).Logging("playlistSummaryContextMenuResyncClick");
        }
    }

    private async void playlistSummaryContextMenuOpenPageClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.LinkUri != null)
        {
            await OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri).Logging("playlistSummaryContextMenuOpenPageClick");
        }
    }

    private async void playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick(object sender, RoutedEventArgs e)
    {
        List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
        if (visibleRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.PlaylistSummaryBmtSort.ApplyCurrentVisibleOrder(visibleRows);
        }).Logging("playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick");
    }

    private async void playlistSummaryContextMenuMoveToBmtSortTopClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.PlaylistSummaryBmtSort.MoveRowsToTop(selectedPlaylistSummaryRows);
        }).Logging("playlistSummaryContextMenuMoveToBmtSortTopClick");
    }

    private async void playlistSummaryContextMenuMoveToBmtSortBottomClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.PlaylistSummaryBmtSort.MoveRowsToBottom(selectedPlaylistSummaryRows);
        }).Logging("playlistSummaryContextMenuMoveToBmtSortBottomClick");
    }

    private void playlistSummaryContextMenuOpenBulkEditClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = [.. getSelectedPlaylistSummaryRows(playlistSummaryRow).Where(row => row?.TableRef != null)];
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && CanOpenPlaylistEditDialog(mainWindowViewModel))
        {
            if (!mainWindowViewModel.ContainsActivePlaylistSummaryRows(selectedPlaylistSummaryRows))
            {
                return;
            }
            mainWindowViewModel.playlistSummaryBulkEditDialog = new MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel(mainWindowViewModel, selectedPlaylistSummaryRows);
            ShowOverlayDialog(playlistSummaryBulkEditDialog);
        }
    }

    private void playlistSummaryContextMenuOpenPropertyClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.TableRef == null)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && CanOpenPlaylistEditDialog(mainWindowViewModel))
        {
            if (!mainWindowViewModel.ContainsActivePlaylistTable(playlistSummaryRow.TableRef))
            {
                return;
            }
            mainWindowViewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(mainWindowViewModel, playlistSummaryRow.TableRef);
            ShowOverlayDialog(playlistPropertyDialog);
        }
    }

    private static bool CanOpenPlaylistEditDialog(MainWindowViewModel viewModel)
    {
        return viewModel != null
            && !viewModel.IsWriteLockHeldBMSTablesInitializeMin
            && !viewModel.IsWriteLockHeldBMSTables
            && !viewModel.IsWriteLockHeldAnyBMSTable;
    }

    private async void playlistSummaryContextMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                foreach (PlaylistSummaryRow item in selectedPlaylistSummaryRows)
                {
                    if (item?.TableRef != null)
                    {
                        viewModel.RemoveBMSTable(item.TableRef);
                    }
                }
                viewModel.RebuildPlaylistSummaryView(runAsync: false);
            }).Logging("playlistSummaryContextMenuRemoveClick");
        }
    }

    private async void fullScanCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingFilter);
            }).Logging("fullScanCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void fullScanAllChartsFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_all_charts_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FullScanAllChartsFilter);
            }).Logging("fullScanAllChartsFolderSelect");
        }
    }

    private async void fullScanCheckIgnoredFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_ignored_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingIgnoredFilter);
            }).Logging("fullScanCheckIgnoredFolderSelect");
        }
    }

    private async void dupulicateFileCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_duplicate_file_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.DuplicateFilter);
            }).Logging("dupulicateFileCheckFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        object parameter;
        if (treeViewItem.DataContext is string)
        {
            parameter = treeViewItem.DataContext;
        }
        else if (treeViewItem.DataContext is DuplicateGroup)
        {
            parameter = treeViewItem.DataContext;
        }
        else
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.DuplicateFilter, parameter);
        }).Logging("dupulicateFileCheckFolderSelect");
    }

    private async void garbledCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garbled_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.GarbledFilter);
            }).Logging("garbledCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void garbleFixedFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garble_fixed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.GarbleFixedFilter);
            }).Logging("garbleFixedFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void unregisteredToDBFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_unregistered_to_db_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.UnregisteredFilter);
        }).Logging("unregisteredToDBFolderSelect");
    }

    private async void zeronoteFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_zero_note_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.ZeroNoteFilter);
        }).Logging("zeronoteFolderSelect");
    }

    private async void chartInfoParseErrorFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_chart_info_parse_error_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.ChartInfoParseErrorFilter);
        }).Logging("chartInfoParseErrorFolderSelect");
    }

    private void treeViewZeroNoteContextMenuItemRecheckClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.RecheckZeroNoteWarnings();
            }).Logging("treeViewZeroNoteContextMenuItemRecheckClick");
        }
    }

    private async void newlyInstalledFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_newly_installed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("newlyInstalledFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter, package);
            }).Logging("newlyInstalledFolderSelect");
        }
    }

    private async void pendingInstallFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_pending_install_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("pendingInstallFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter, package);
            }).Logging("pendingInstallFolderSelect");
        }
    }

    /// <summary>
    /// プレイリストツリーの「ルート（プレイリスト一覧）」に対するコンテキストメニューが開かれた際の処理。
    /// 現在の書き込みロック状態やバックグラウンド処理状況（IsWriteLockHeld等）に応じ、
    /// 新規作成やURL読み込みなどの各種メニュー項目の有効/無効 (IsEnabled) を動的に制御します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewPlaylistRootContextMenuItemCreateNewPlaylist")
            {
                menuItem = item as MenuItem;
            }
            if (item is not MenuItem)
            {
                continue;
            }
            foreach (Control item2 in (IEnumerable)((MenuItem)item).Items)
            {
                switch (item2.Name)
                {
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistURL":
                        menuItem2 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistCollection":
                        menuItem3 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadWalkureTable":
                        menuItem4 = item2 as MenuItem;
                        break;
                }
            }
        }
        if (menuItem != null)
        {
            menuItem.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable;
        }
        if (menuItem2 != null)
        {
            menuItem2.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin;
        }
        if (menuItem3 != null)
        {
            menuItem3.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsLoadingExternalCollectionBMSTables;
        }
        if (menuItem4 != null)
        {
            menuItem4.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin;
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「新しいプレイリストを作成」がクリックされた際の処理。
    /// 非同期で空のBMSTable（プレイリスト）を生成し、直後にプロパティ変更ダイアログを表示させます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem || !CanOpenPlaylistEditDialog(viewModel))
        {
            return;
        }
        try
        {
            if (!CanOpenPlaylistEditDialog(viewModel))
            {
                return;
            }
            BMSTable bMSTable = await Task.Run(() => viewModel.CreateBMSTable()).Logging("treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");
            if (bMSTable != null)
            {
                if (!viewModel.ContainsActivePlaylistTable(bMSTable))
                {
                    return;
                }
                viewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(viewModel, bMSTable, _isForNewTable: true);
                ShowOverlayDialog(playlistPropertyDialog);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「URLからプレイリストを読み込む」がクリックされた際の処理。
    /// カスタムURL入力用のダイアログ (loadPlaylistURIDialog) を画面に表示します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistURLClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { IsWriteLockHeldBMSTablesInitializeMin: false })
        {
            ShowOverlayDialog(loadPlaylistURIDialog);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「プレイリストコレクションを読み込む」がクリックされた際の処理。
    /// 指定されたコレクションURLをもとに、ViewModelへプレイリスト群の非同期登録を要求します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && menuItem.DataContext is BMSTableSimple dataContext && !(dataContext.url == null) && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            viewModel.EnqueueExternalPlaylistBMSTableImport(dataContext.url);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「Walkure/難易度表を読み込む」に関するメニュー項目（各難易度表単位）のアクション。
    /// MenuItemのTagプロパティに格納されたURLへアクセスし、プレイリスト情報を非同期で追加・登録します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel && sender is MenuItem menuItem && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            var uri = new Uri((string)menuItem.Tag);
            viewModel.EnqueueExternalPlaylistBMSTableImport(uri);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニューから「Walkureのおすすめフォルダ」関連のテーブル読み込みが選択された場合の処理。
    /// LR2IDの設定状況のチェックや、更新モード/閲覧モードに応じたユーザー確認ダイアログを挟んだ後、非同期で登録処理へ進みます。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem || viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            return;
        }
        var uri = new Uri((string)menuItem.Tag);
        if (viewModel.LR2ID == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        if (Regex.Match((string)menuItem.Tag, "mode=update").Success)
        {
            if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
            {
                return;
            }
        }
        else if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_readonly_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
        {
            return;
        }
        viewModel.EnqueueExternalPlaylistBMSTableImport(uri);
    }

    /// <summary>
    /// プレイリスト（難易度表等の直下にある上位階層）のコンテキストメニューが開かれた際の処理。
    /// 現在の選択要素 (BMSTable) の属性（外部同期するか否か、URLの有無など）や
    /// アプリ状態に応じて、メニュー各項目の有効化状態 (IsEnabled) を切り替えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel || !(contextMenu.PlacementTarget is TreeViewItem { DataContext: BMSTable dataContext }))
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem5 = null;
        MenuItem menuItem6 = null;
        MenuItem menuItem7 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "treeViewPlaylistTableContextMenuItemReload":
                    menuItem7 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenPageURI":
                    menuItem = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenClearLamp":
                    menuItem2 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOverwriteLevel":
                    menuItem3 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemCreateNewFolder":
                    menuItem4 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemRemoveTable":
                    menuItem5 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableCcontextMenuItemOpenPropertyDialog":
                    menuItem6 = item as MenuItem;
                    break;
            }
        }
        Uri uri = dataContext.Page_url ?? dataContext.Header_url;
        bool flag = uri != null && uri.IsAbsoluteUri;
        menuItem7.IsEnabled = flag;
        menuItem.IsEnabled = dataContext.Page_url != null || dataContext.GetAbsoluteHeaderUrl() != null;
        menuItem2.IsEnabled = dataContext.Page_url != null && dataContext.Page_url.Scheme != "bmseeker" && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0;
        menuItem4.IsEnabled = !dataContext.is_external_sync;
        menuItem3.IsEnabled = true;
        menuItem5.IsEnabled = true;
        menuItem6.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable;
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「最新の情報に更新（リロード）」実行時の処理。
    /// 対象プレイリスト単体の同期更新を非同期で実行し、完了後に選択状態の復元を試みます。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        await viewModel.ResyncPlaylistsAsync([table]).Logging("treeViewPlaylistTableContextMenuItemReloadClick");
        RestorePlaylistTableSelectionAfterReload(table);
    }

    private void RestorePlaylistTableSelectionAfterReload(BMSTable tableBeforeReload)
    {
        if (tableBeforeReload == null)
        {
            return;
        }
        BMSTable selectionTarget = FindReloadedPlaylistTable(tableBeforeReload);
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload skipped reason=target_not_found");
            return;
        }
        bool restored = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!restored)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=false reason=container_not_realized table=" + selectionTarget.name + " realize_by_index_available=" + realizeByIndexAvailable);
            return;
        }
        NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=true table=" + selectionTarget.name + " expanded=true usedVirtualizationFallback=" + usedVirtualizationFallback);
    }

    private BMSTable FindReloadedPlaylistTable(BMSTable tableBeforeReload)
    {
        IEnumerable<BMSTable> source = treeViewItemPlaylist.Items.OfType<BMSTable>();
        if (tableBeforeReload.playlist_id.HasValue)
        {
            BMSTable byId = source.FirstOrDefault(t => t != null && t.playlist_id.HasValue && t.playlist_id.Value == tableBeforeReload.playlist_id.Value);
            if (byId != null)
            {
                return byId;
            }
        }
        string pageUrl = tableBeforeReload.Page_url?.AbsoluteUri ?? string.Empty;
        string headerUrl = tableBeforeReload.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty;
        BMSTable byUrl = source.FirstOrDefault(delegate (BMSTable t)
        {
            if (t == null)
            {
                return false;
            }
            string text = t.Page_url?.AbsoluteUri ?? string.Empty;
            string text2 = t.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty;
            return string.Equals(text, pageUrl, StringComparison.OrdinalIgnoreCase) && string.Equals(text2, headerUrl, StringComparison.OrdinalIgnoreCase);
        });
        if (byUrl != null)
        {
            return byUrl;
        }
        return source.FirstOrDefault(t => t != null && string.Equals(t.name, tableBeforeReload.name, StringComparison.Ordinal));
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「配布ページを開く」実行時の処理。
    /// BMSTableに設定されたURL (Page_url または Header_url) を標準ブラウザ等で開きます。
    /// 特殊スキーム（Walkure難易度表等）の場合は専用のURLへ変換してブラウザ起動します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenPageURIClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel || !(sender is MenuItem { DataContext: BMSTable dataContext }))
        {
            return;
        }
        Uri uri = dataContext.Page_url ?? dataContext.GetAbsoluteHeaderUrl();
        if (uri.Scheme != "bmseeker")
        {
            if (uri != null)
            {
                Process.Start(uri.ToString());
            }
        }
        else if (uri.ToString().StartsWith("bmseeker:table.estimation"))
        {
            Process.Start("http://walkure.net/hakkyou/bms.html");
        }
        else if (uri.ToString().StartsWith("bmseeker:table.recommended") && mainWindowViewModel.LR2ID != 0)
        {
            Process.Start("http://walkure.net/hakkyou/recommended_mypage.html?playerid=" + mainWindowViewModel.LR2ID);
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「クリア状況ページを開く」実行時の処理。
    /// ユーザーのLR2IDと対象難易度表URLをパラメータにし、外部連携サイト（通常はWalkureのクリアランプページ）を表示します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenClearLampClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && sender is MenuItem { DataContext: BMSTable dataContext } && dataContext.Page_url != null && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0)
        {
            Process.Start(clearlampUri + "?lr2ID=" + Uri.EscapeDataString(mainWindowViewModel.LR2ID.ToString()) + "&table_url=" + Uri.EscapeDataString(dataContext.Page_url.ToString()));
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「フォルダを作成」実行時の処理。
    /// 選択中のプレイリスト配下に新しいサブフォルダ用BMSTable要素を非同期で追加します（自作プレイリスト用）。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemCreateNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is BMSTable bmsTable && !bmsTable.is_external_sync)
        {
            await Task.Run(delegate
            {
                viewModel.CreateNewFolderBMSTable(bmsTable);
            }).Logging("treeViewPlaylistTableContextMenuItemCreateNewFolderClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「一覧をエキスポート (JSON)」実行時の処理。
    /// 保存用ファイルダイアログを開き、指定されたパスに header.json と data.json 形式で
    /// プレイリスト情報をファイル書き出し（出力）します。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemExportTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        var dialogCoordinator = new UiDialogCoordinator();
        UiSaveFilePickerResult headerResult = await dialogCoordinator.PickSaveFileAsync(new UiSaveFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Save_header_file,
            (!string.IsNullOrWhiteSpace(bmsTable.header_url)) ? Path.GetFileName(bmsTable.Header_url.ToString()) : "header.json",
            ".json",
            BeMusicSeeker.Properties.Resources.Json_file_exts,
            addExtension: true,
            this));
        ThrowIfPickerFailed(headerResult.Status, headerResult.Error, "Header export save picker");
        if (headerResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        UiSaveFilePickerResult dataResult = await dialogCoordinator.PickSaveFileAsync(new UiSaveFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Save_data_file,
            (!string.IsNullOrWhiteSpace(bmsTable.data_url)) ? Path.GetFileName(bmsTable.Data_url.ToString()) : "data.json",
            ".json",
            BeMusicSeeker.Properties.Resources.Json_file_exts,
            addExtension: true,
            this));
        ThrowIfPickerFailed(dataResult.Status, dataResult.Error, "Data export save picker");
        if (dataResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.ExportBMSTable(bmsTable, headerResult.FileName, dataResult.FileName);
        }).Logging("treeViewPlaylistTableContextMenuItemExportTableClick");
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「ローカルBMSの難易度をこの表で上書き」実行時の処理。
    /// ユーザー確認ダイアログ表示後、このプレイリストに登録されている各楽曲のレベル情報を用いて
    /// メインDB（ローカルの全BMS情報）の同等楽曲のレベル値を書き換えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOverwriteLevelClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(bmsTable.page_url) && bmsTable.page_url.StartsWith("bmseeker:table.recommended"))
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_warning, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.ReplaceBMSFileLevelByTableEntryLevel(bmsTable);
            }).Logging("treeViewPlaylistTableContextMenuItemOverwriteLevelClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「この表を削除」実行時の処理。
    /// ユーザー確認を取った上で選択中のプレイリストをツリーから除外し、非同期でメイン側からも削除・破棄します。
    /// UI仮想化による選択ロストを防ぐためのフォールバック遷移も併せて行います。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemRemoveTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemPlaylist, bmsTable, "treeViewPlaylistTableContextMenuItemRemoveTableClick");
        await Task.Run(delegate
        {
            viewModel.RemoveBMSTable(bmsTable);
        }).Logging("treeViewPlaylistTableContextMenuItemRemoveTableClick");
        if (treeViewItemPlaylist.IsSelected && treeViewItemPlaylist.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecPlaylistFilter(null);
            }).Logging("treeViewPlaylistTableContextMenuItemRemoveTableClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「プロパティ」実行時の処理。
    /// 選択中の難易度表（BMSTable）の詳細情報や同期URLなどを確認・編集できる専用ダイアログを開きます。
    /// </summary>
    private void treeViewPlaylistTableCcontextMenuItemOpenPropertyDialogClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && sender is MenuItem menuItem && menuItem.DataContext is BMSTable table && CanOpenPlaylistEditDialog(mainWindowViewModel))
        {
            if (!mainWindowViewModel.ContainsActivePlaylistTable(table))
            {
                return;
            }
            mainWindowViewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(mainWindowViewModel, table);
            ShowOverlayDialog(playlistPropertyDialog);
        }
    }

    /// <summary>
    /// プレイリスト配下の「フォルダ（自作/自動生成）」のコンテキストメニューが開かれた際の処理。
    /// ツリーのVisualTreeを遡り、現在選択しているアイテムが外部同期中のものかを判別して、
    /// 「フォルダ名変更」や「削除」といった編集メニューの有効化状態を制御します。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null)
        {
            return;
        }
        BMSTable bMSTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bMSTable == null)
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (!(name == "treeViewPlaylistTableFolderContextMenuItemDeleteFolder"))
            {
                if (name == "treeViewPlaylistTableFolderContextMenuItemChangeFolderName")
                {
                    menuItem2 = item as MenuItem;
                }
            }
            else
            {
                menuItem = item as MenuItem;
            }
        }
        if (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode))
        {
            return;
        }
        menuItem.IsEnabled = !bMSTable.is_external_sync && folderNode.IsEditable;
        menuItem2.IsEnabled = !bMSTable.is_external_sync && folderNode.IsEditable;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となった要素から、
    /// VisualTreeを親方向へ遡及して、所属する上位の <see cref="BMSTable"/>（プレイリスト大枠）を探索して返却します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する上位の <see cref="BMSTable"/> 要素。見つからない場合は null。</returns>
    private BMSTable _getUpperBMSTableForContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem reference)
        {
            return null;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(reference);
        while ((parent is not TreeViewItem || (parent as TreeViewItem).DataContext is not BMSTable) && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null)
        {
            return null;
        }
        return (parent as TreeViewItem).DataContext as BMSTable;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となったTreeViewItem内から、
    /// VisualTreeを子方向へ探索し、フォルダ名編集などに用いられる <see cref="EditableTextBlock"/> を取得します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する <see cref="EditableTextBlock"/> コンポーネント。存在しない場合は null。</returns>
    private EditableTextBlock _getETBFromContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem treeViewItem)
        {
            return null;
        }
        return _findTypeFromVisualChildren<EditableTextBlock>([treeViewItem]);
    }

    private static Type _findTypeFromVisualChildren<Type>(IEnumerable<DependencyObject> _objs) where Type : class
    {
        IEnumerable<DependencyObject> enumerable = _objs.SelectMany(obj => _getVisualChildren(obj));
        if (enumerable.Count() == 0)
        {
            return null;
        }
        DependencyObject dependencyObject = enumerable.FirstOrDefault(c => c is Type);
        if (dependencyObject == null)
        {
            return _findTypeFromVisualChildren<Type>(enumerable);
        }
        return dependencyObject as Type;
    }

    private static IEnumerable<DependencyObject> _getVisualChildren(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            yield return VisualTreeHelper.GetChild(obj, i);
        }
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダを削除」実行時の処理。
    /// 指定されたカスタムフォルダ名を持つ仮想要素を、所属するプレイリスト (BMSTable) 内から抹消します。
    /// </summary>
    private async void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null || !TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) || folderNode.IsSpecial)
        {
            return;
        }
        BMSTable bmsTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bmsTable == null || bmsTable.is_external_sync)
        {
            return;
        }
        string folderNameDelete = folderNode.FolderName;
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await Task.Run(delegate
            {
                viewModel.RemoveFolderBMSTable(bmsTable, folderNameDelete);
            }).Logging("treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick");
        }
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダ名を変更」実行時の処理。
    /// 該当のTreeViewItem内に存在する EditableTextBlock の編集モードをアクティブ (IsInEditMode = true) にします。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuItemChangeFolderNameClick(object sender, RoutedEventArgs e)
    {
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock != null && (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) || folderNode.IsEditable))
        {
            editableTextBlock.IsInEditMode = true;
        }
    }

    /// <summary>
    /// メインツリー（ライブラリ/フォルダ等）コンテキストメニュー「エクスプローラで開く」実行時の処理。
    /// Explorer.exe を介して、選択中のローカルファイルシステム上の絶対パスを開きます。
    /// </summary>
    private void treeViewLibraryFolderContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string text = placementTarget.Header.ToString();
        if (!LongPathFileSystem.DirectoryExists(text))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(text);
    }

    /// <summary>
    /// BMS検索フォルダコンテキストメニュー「BMS検索フォルダから除外」実行時の処理。
    /// ユーザー確認ダイアログ表示後、アプリケーション設定のBMSルートフォルダー一覧から該当のパスを除外して保存します。
    /// </summary>
    private void treeViewLibraryFolderContextMenuItemUnregisterRootFolder(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is not MainWindowViewModel)
        {
            return;
        }
        MainWindowViewModel.SettingDialogViewModel viewModel = (base.DataContext as MainWindowViewModel).settingDialog;
        if (LongPathFileSystem.DirectoryExists(path) && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_unregister_root_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            viewModel.RemoveBMSDirectoryFromRootFolderAndSave(path);
        }
    }

    private void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_library_auto_rename_all"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is MainWindowViewModel viewModel && LongPathFileSystem.DirectoryExists(path) && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_folders, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                try
                {
                    viewModel.AutoRenameAllChartFolders(path);
                }
                finally
                {
                    RefreshCustomTableViewDisplayAsync();
                }
            }).Logging("treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");
        }
    }

    private void treeViewInstalledContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_installed, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveInstalledPackageRecordsAll();
            }).Logging("treeViewInstalledContextMenuClearAllClick");
        }
    }

    private void treeViewInstallPendingContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemovePendingPackagesAll();
            }).Logging("treeViewInstallPendingContextMenuClearAllClick");
        }
    }

    private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_delete_installed_only"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_delete_pending_installed_only_packages_permanently, list.Count);
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        if (list.Count == 1)
        {
            await Task.Run(delegate
            {
                viewModel.DeletePendingPackageSources(list, sendToRecycleBin: false);
            }).Logging("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
            return;
        }
        var cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        int total = list.Count;
        Task task = Task.Run(delegate
        {
            viewModel.DeletePendingPackageSources(list, sendToRecycleBin: false, cancellationTokenSource.Token, delegate
            {
                processedCount++;
            });
        }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
        await RunProgressUntilTaskCompletesAsync(
            task,
            cancellationTokenSource,
            BeMusicSeeker.Properties.Resources.Remove,
            "",
            context => context.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].path));
        await task;
    }

    private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_rename_zero_note"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartFile> list = viewModel.GetPendingBmsFormatChartFilesSnapshot();
        if (list.Count == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_charts, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_rename_pending_zero_note_to_invalid_ext, list.Count);
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        if (list.Count == 1)
        {
            await Task.Run(delegate
            {
                viewModel.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, CancellationToken.None, null);
            }).Logging("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
            return;
        }
        var cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        int total = list.Count;
        Task task = Task.Run(delegate
        {
            viewModel.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, cancellationTokenSource.Token, delegate
            {
                processedCount++;
            });
        }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
        await RunProgressUntilTaskCompletesAsync(
            task,
            cancellationTokenSource,
            BeMusicSeeker.Properties.Resources.Rename_invalid_ext,
            "",
            context => context.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].Path ?? "(null)"));
        await task;
    }

    private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_overwrite_installed_only_resources"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_overwrite_pending_installed_only_packages_resources, list.Count);
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        PendingInstalledOnlyResourceOverwriteResult pendingInstalledOnlyResourceOverwriteResult;
        if (list.Count == 1)
        {
            pendingInstalledOnlyResourceOverwriteResult = await Task.Run(delegate
            {
                return viewModel.OverwritePendingInstalledOnlyPackagesResources(list, CancellationToken.None, null);
            }).Logging("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
        }
        else
        {
            var cancellationTokenSource = new CancellationTokenSource();
            int processedCount = 0;
            int total = list.Count;
            Task<PendingInstalledOnlyResourceOverwriteResult> task = Task.Run(delegate
            {
                return viewModel.OverwritePendingInstalledOnlyPackagesResources(list, cancellationTokenSource.Token, delegate
                {
                    processedCount++;
                });
            }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
            await RunProgressUntilTaskCompletesAsync(
                task,
                cancellationTokenSource,
                BeMusicSeeker.Properties.Resources.Install_to_estimation,
                "",
                context => context.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].path ?? "(null)"));
            pendingInstalledOnlyResourceOverwriteResult = await task;
        }
        if (pendingInstalledOnlyResourceOverwriteResult != null)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), string.Format(BeMusicSeeker.Properties.Resources.Warn_overwrite_pending_installed_only_packages_summary, pendingInstalledOnlyResourceOverwriteResult.Requested, pendingInstalledOnlyResourceOverwriteResult.Processed, pendingInstalledOnlyResourceOverwriteResult.SucceededInstall, pendingInstalledOnlyResourceOverwriteResult.SucceededCleanupOnly, pendingInstalledOnlyResourceOverwriteResult.SkippedNotPending, pendingInstalledOnlyResourceOverwriteResult.SkippedMissingInstlDst, pendingInstalledOnlyResourceOverwriteResult.SkippedMultiDestination, pendingInstalledOnlyResourceOverwriteResult.SkippedNoComponentTarget, pendingInstalledOnlyResourceOverwriteResult.Failed, pendingInstalledOnlyResourceOverwriteResult.Canceled), BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
    }

    private void treeViewInstallPackageContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } }))
        {
            return;
        }
        if (LongPathFileSystem.DirectoryExists(dataContext.path))
        {
            ExplorerOpenService.OpenDirectory(dataContext.path);
            return;
        }
        if (!LongPathFileSystem.FileExists(dataContext.path))
        {
            return;
        }
        ExplorerOpenService.OpenFileAndSelect(dataContext.path);
    }

    /// <summary>
    /// インストール関連ツリーのコンテキストメニュー「このフォルダの履歴を消去」実行時の処理。
    /// 「インストール保留中」などのリストから対象のパッケージ (ChartPackage) を一つ取り除きます。
    /// </summary>
    private async void treeViewInstallPackageContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_pendings + Environment.NewLine + Environment.NewLine + GetPendingPackageClearConfirmationTarget(pkg), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemovePendingPackages([pkg]);
        }).Logging("treeViewInstallPackageContextMenuClearFolderClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuClearFolderClick");
        }
    }

    private static string GetPendingPackageClearConfirmationTarget(ChartPackage pkg)
    {
        return pkg?.DisplayTitle ?? string.Empty;
    }

    private async void treeViewInstalledFolderContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, pkg, "treeViewInstalledFolderContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemoveInstalledPackageRecords([pkg]);
        }).Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        }
    }

    private void treeViewInstallPackageContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.ClearInstallDestinationForPendingPackages([pkg]);
            }).Logging("treeViewInstallPackageContextMenuRemoveInstallDestinationClick");
        }
    }

    private async void treeViewInstallPackageContextMenuForceInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_force_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuForceInstallClick");
        await Task.Run(delegate
        {
            viewModel.ForceInstallPendingPackages([pkg]);
        }).Logging("treeViewInstallPackageContextMenuForceInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuForceInstallClick");
        }
    }

    private async void treeViewInstallPackageContextMenuManualInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_manual_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || (Settings.Default.ShowDiffBMSInstallConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK))
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuManualInstallClick");
        await Task.Run(delegate
        {
            viewModel.ManualInstallPendingPackages([pkg]);
        }).Logging("treeViewInstallPackageContextMenuManualInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuManualInstallClick");
        }
    }

    private void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.SearchInstallDestinationForPendingPackages([pkg]);
            }).Logging("treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        }
    }

    private void treeViewInstallPackageContextMenuSearchMergeDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.SearchMergeDestinationForPendingPackages([pkg]);
            }).Logging("treeViewInstallPackageContextMenuSearchMergeDestinationClick");
        }
    }

    private void treeViewDuplicateFolderContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!(sender is ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } placementTarget } contextMenu))
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>(placementTarget);
        if (treeViewItem == null)
        {
            return;
        }
        if (treeViewItem.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }
        MenuItem menuItem = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewDuplicateFolderContextMenuItemMergeInto")
            {
                menuItem = item as MenuItem;
            }
        }
        if (menuItem != null)
        {
            List<string> list = [.. duplicateGroup.Folders.Except([dataContext], StringComparer.OrdinalIgnoreCase)];
            menuItem.IsEnabled = list.Count > 0;
            if (menuItem.IsEnabled)
            {
                menuItem.ItemsSource = list;
            }
        }
    }

    private void treeViewDuplicateFolderContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } } }) || !LongPathFileSystem.DirectoryExists(dataContext))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(dataContext);
    }

    private void treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_duplicate_merge_into"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Tag: TreeViewItem tag } menuItem))
        {
            return;
        }
        string srcPath = tag.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }
        string dstPath = menuItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }
        // 親DuplicateGroupを取得
        TreeViewItem groupTreeItem = WPFUtil.FindVisualParent<TreeViewItem>(tag);
        var duplicateGroup = groupTreeItem?.DataContext as DuplicateGroup;
        ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
    }

    /// <summary>
    /// 重複フォルダのマージ処理を実行する共通メソッド。
    /// 確認ダイアログ → マージ実行 → マージ後のグループ自動選択を行う。
    /// </summary>
    private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)
    {
        if (ShouldBlockChartPackageMutationInteraction("duplicate_merge_execute"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }

        // 確認ダイアログ
        if (Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder + Environment.NewLine + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_target + ": " + srcPath + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination + ": " + dstPath,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        long operationId = Stopwatch.GetTimestamp();
        var okToTaskStopwatch = Stopwatch.StartNew();
        var totalStopwatch = Stopwatch.StartNew();
        LogDuplicateMergePerformance("duplicate_merge_ui confirmed op=" + operationId + " src=" + srcPath + " dst=" + dstPath);

        // マージ後に自動選択するグループのHeaderをキャッシュ
        _pendingDuplicateGroupHeader = null;
        if (duplicateGroup != null && viewModel.DuplicateChartGroups != null)
        {
            int folderCount = duplicateGroup.Folders.Count;
            if (folderCount == 2)
            {
                // フォルダが2つの場合: マージでグループ消滅 → 次のグループを選択
                int currentIndex = viewModel.DuplicateChartGroups.IndexOf(duplicateGroup);
                if (currentIndex >= 0 && currentIndex + 1 < viewModel.DuplicateChartGroups.Count)
                {
                    _pendingDuplicateGroupHeader = viewModel.DuplicateChartGroups[currentIndex + 1].Header;
                }
            }
            else if (folderCount >= 3)
            {
                // フォルダが3つ以上の場合: マージ後もグループが残る → 同じグループを再選択
                _pendingDuplicateGroupHeader = duplicateGroup.Header;
            }
        }

        string srcFolderName = Path.GetFileName(srcPath);
        string dstFolderName = Path.GetFileName(dstPath);

        Task.Run(delegate
        {
            LogDuplicateMergePerformance("duplicate_merge_task start op=" + operationId + " okToTaskStartMs=" + okToTaskStopwatch.ElapsedMilliseconds + " src=" + srcPath + " dst=" + dstPath);
            var taskStopwatch = Stopwatch.StartNew();
            viewModel.MergeChartDirectory(srcPath, dstPath, operationId);
            LogDuplicateMergePerformance("duplicate_merge_task viewModel_done op=" + operationId + " taskMs=" + taskStopwatch.ElapsedMilliseconds + " totalSinceOkMs=" + totalStopwatch.ElapsedMilliseconds);

            // マージ完了ログ（将来のステータスバー通知に備える）
            NLogWrapper.FileLogger?.Info(string.Format(
                BeMusicSeeker.Properties.Resources.Msg_merge_bms_completed, srcFolderName, dstFolderName));

        }).ContinueWith(t =>
        {
            LogDuplicateMergePerformance("duplicate_merge_ui continuation op=" + operationId + " faulted=" + (t.Exception != null) + " totalSinceOkMs=" + totalStopwatch.ElapsedMilliseconds);
            if (t.Exception != null)
            {
                return;
            }
            // マージ後にDuplicateChartGroupsの更新を待ってからツリーで自動選択を試みる
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateFolderMerge");
    }

    private static void LogDuplicateMergePerformance(string message)
    {
        if (CommandLineSwitches.IsInfoLoggingEnabled)
        {
            NLogWrapper.GetLogger("InstallPerformance.DuplicateMerge").Info(message);
        }
    }

    /// <summary>
    /// DuplicateChartGroups更新タイミングの競合を吸収しつつ、該当グループを自動選択する。
    /// PropertyChangedと遅延フォールバックの両方から、データ更新後のTreeView反映を待って選択を試みる。
    /// </summary>
    private void WaitForDuplicateListUpdateAndSelect(string header, MainWindowViewModel viewModel)
    {
        if (string.IsNullOrEmpty(header) || viewModel == null)
        {
            return;
        }
        if (_duplicateGroupAutoSelectHandler != null && _duplicateGroupAutoSelectHandlerOwner != null)
        {
            _duplicateGroupAutoSelectHandlerOwner.PropertyChanged -= _duplicateGroupAutoSelectHandler;
            _duplicateGroupAutoSelectHandler = null;
            _duplicateGroupAutoSelectHandlerOwner = null;
        }
        int requestVersion = Interlocked.Increment(ref _duplicateGroupAutoSelectRequestVersion);
        bool completed = false;
        PropertyChangedEventHandler handler = null;
        void completeSelection()
        {
            if (completed)
            {
                return;
            }
            completed = true;
            if (handler != null)
            {
                viewModel.PropertyChanged -= handler;
            }
            if (ReferenceEquals(_duplicateGroupAutoSelectHandler, handler))
            {
                _duplicateGroupAutoSelectHandler = null;
                _duplicateGroupAutoSelectHandlerOwner = null;
            }
        }
        async Task AttemptAutoSelectAsync(string trigger)
        {
            try
            {
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                if (viewModel.DuplicateChartGroups == null)
                {
                    NLogWrapper.FileLogger?.Info("duplicate_group_autoselect wait_for_groups trigger=" + trigger + " header=" + header + " request=" + requestVersion);
                    if (trigger == "timeout_fallback")
                    {
                        completeSelection();
                    }
                    return;
                }

                string lastReason = string.Empty;
                DispatcherPriority[] retryPriorities = [DispatcherPriority.Loaded, DispatcherPriority.Render, DispatcherPriority.ContextIdle];
                for (int retryIndex = 0; retryIndex < retryPriorities.Length; retryIndex++)
                {
                    await Dispatcher.Yield(retryPriorities[retryIndex]);
                    if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                    {
                        return;
                    }
                    if (TrySelectDuplicateGroupByHeader(header, viewModel, out lastReason))
                    {
                        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect success trigger=" + trigger + " retry=" + retryIndex + " header=" + header + " request=" + requestVersion);
                        completeSelection();
                        return;
                    }
                    if (!ShouldRetryDuplicateGroupAutoSelect(lastReason))
                    {
                        break;
                    }
                }
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect pending trigger=" + trigger + " header=" + header + " request=" + requestVersion + " reason=" + lastReason);
                if (trigger == "timeout_fallback")
                {
                    completeSelection();
                }
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect failed trigger=" + trigger + " header=" + header + " request=" + requestVersion + " message=" + ex.Message);
            }
        }
        handler = (s, e) =>
        {
            if (e.PropertyName != nameof(viewModel.DuplicateChartGroups))
            {
                return;
            }
            NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=property_changed header=" + header + " request=" + requestVersion);
            if (Dispatcher.CheckAccess())
            {
                _ = AttemptAutoSelectAsync("property_changed");
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (_isClosingOrClosed)
                    {
                        return;
                    }
                    _ = AttemptAutoSelectAsync("property_changed");
                }));
            }
        };
        viewModel.PropertyChanged += handler;
        _duplicateGroupAutoSelectHandler = handler;
        _duplicateGroupAutoSelectHandlerOwner = viewModel;
        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect queued header=" + header + " request=" + requestVersion + " mode=property_changed");
        if (viewModel.DuplicateChartGroups != null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (_isClosingOrClosed)
                {
                    return;
                }
                NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=already_ready header=" + header + " request=" + requestVersion);
                _ = AttemptAutoSelectAsync("already_ready");
            }));
        }
        Task.Run(async delegate
        {
            await Task.Delay(1500).ConfigureAwait(continueOnCapturedContext: false);
            if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
            {
                return;
            }
            await Dispatcher.InvokeAsync(async delegate
            {
                if (_isClosingOrClosed)
                {
                    return;
                }
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=timeout_fallback header=" + header + " request=" + requestVersion);
                await AttemptAutoSelectAsync("timeout_fallback");
            }, DispatcherPriority.Background);
        });
    }

    private static bool ShouldRetryDuplicateGroupAutoSelect(string failReason)
    {
        return failReason == "duplicate_tree_items_not_updated" ||
            failReason == "container_not_realized" ||
            failReason == "duplicate_items_host_not_found" ||
            failReason == "bring_index_out_of_range";
    }

    private bool TrySelectDuplicateGroupByHeader(string header, MainWindowViewModel viewModel, out string failReason)
    {
        failReason = string.Empty;
        // XAML上で Name="treeViewItemSearchDuplicated" を持つTreeViewItem
        TreeViewItem duplicateRootItem = treeViewItemSearchDuplicated;
        if (duplicateRootItem == null)
        {
            failReason = "root_not_found";
            return false;
        }

        List<DuplicateGroup> duplicatedList = viewModel.DuplicateChartGroups;
        if (duplicatedList == null)
        {
            failReason = "duplicated_list_null";
            return false;
        }

        DuplicateGroup targetGroup = null;
        for (int i = 0; i < duplicatedList.Count; i++)
        {
            if (duplicatedList[i].Header == header)
            {
                targetGroup = duplicatedList[i];
                break;
            }
        }
        if (targetGroup == null)
        {
            failReason = "group_not_found";
            return false;
        }

        // 親TreeViewItemが展開されていることを確認
        treeViewItemFullScanCheck.IsExpanded = true;
        duplicateRootItem.IsExpanded = true;
        duplicateRootItem.BringIntoView();
        duplicateRootItem.UpdateLayout();

        int targetIndex = FindDuplicateGroupTreeItemIndex(duplicateRootItem, targetGroup);
        if (targetIndex < 0)
        {
            failReason = "duplicate_tree_items_not_updated";
            return false;
        }

        TreeViewItem targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        if (targetItem == null)
        {
            if (!TryRealizeVirtualizedDuplicateGroupItem(duplicateRootItem, targetIndex, out failReason))
            {
                return false;
            }
            targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        }

        // コンテナを取得して選択
        if (targetItem != null)
        {
            targetItem.IsSelected = true;
            targetItem.IsExpanded = true;
            targetItem.BringIntoView();
            return true;
        }
        failReason = "container_not_realized";
        return false;
    }

    private static int FindDuplicateGroupTreeItemIndex(TreeViewItem duplicateRootItem, DuplicateGroup targetGroup)
    {
        ItemCollection treeItems = duplicateRootItem.Items;
        for (int i = 0; i < treeItems.Count; i++)
        {
            if (ReferenceEquals(treeItems[i], targetGroup))
            {
                return i;
            }
        }
        return -1;
    }

    private bool TryRealizeVirtualizedDuplicateGroupItem(TreeViewItem duplicateRootItem, int targetIndex, out string failReason)
    {
        failReason = string.Empty;
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            failReason = "bring_index_method_not_found";
            return false;
        }
        VirtualizingStackPanel duplicateItemsHostPanel = TryGetTreeViewItemItemsHostPanel(duplicateRootItem);
        if (duplicateItemsHostPanel == null)
        {
            failReason = "duplicate_items_host_not_found";
            return false;
        }
        try
        {
            playlistTreeBringIndexIntoViewMethod.Invoke(duplicateItemsHostPanel, [targetIndex]);
            duplicateRootItem.UpdateLayout();
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (Exception ex)
        {
            failReason = "bring_index_failed_" + ex.GetType().Name;
            return false;
        }
    }

    private static VirtualizingStackPanel TryGetTreeViewItemItemsHostPanel(TreeViewItem treeViewItem)
    {
        treeViewItem.ApplyTemplate();
        treeViewItem.UpdateLayout();
        return FindItemsHostPanelForOwner(treeViewItem, treeViewItem);
    }

    private static VirtualizingStackPanel FindItemsHostPanelForOwner(DependencyObject parent, ItemsControl owner)
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is VirtualizingStackPanel panel && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            {
                return panel;
            }
            VirtualizingStackPanel descendant = FindItemsHostPanelForOwner(child, owner);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    /// <summary>
    /// 重複フォルダのキーボードショートカットハンドラ。
    /// Ctrl+G: フォルダが2つの場合、もう一方のフォルダへマージを実行する。
    /// </summary>
    private void duplicateFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.G || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (sender is not TreeViewItem folderItem)
        {
            return;
        }

        string srcPath = folderItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }

        // 親TreeViewItemからDuplicateGroupを取得
        TreeViewItem groupItem = WPFUtil.FindVisualParent<TreeViewItem>(folderItem);
        if (groupItem?.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }

        int folderCount = duplicateGroup.Folders.Count;

        if (folderCount == 2)
        {
            // フォルダが2つの場合: 自分以外の唯一のフォルダへマージ
            string dstPath = duplicateGroup.Folders
                .FirstOrDefault(f => !f.Equals(srcPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(dstPath))
            {
                ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
            }
        }
        else if (folderCount == 1)
        {
            // フォルダが1つの場合: ハッシュ重複BMSファイルの整理
            ExecuteDuplicateHashCleanup(duplicateGroup, srcPath);
        }
        else if (folderCount >= 3)
        {
            // フォルダが3つ以上の場合: コンテキストメニューを開いてマージ先を選択
            OpenDuplicateFolderContextMenu(folderItem);
        }

        e.Handled = true;
    }

    /// <summary>
    /// フォルダが1つの重複グループで、ハッシュ重複BMSファイルを整理する。
    /// 各ハッシュグループごとに1つだけ残し、残りをごみ箱へ移動する。
    /// 保持ルール: 更新日時が最も古いものを優先、同日時ならファイル名が最も短いものを優先。
    /// </summary>
    private void ExecuteDuplicateHashCleanup(DuplicateGroup duplicateGroup, string folderPath)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var chartsInFolder = duplicateGroup.ChartFiles
            .Where(chart => !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (chartsInFolder.Count == 0)
        {
            return;
        }

        // 主キー(hash)でグループ化し、各グループで削除対象を決定
        var deletionList = new List<ChartFile>();
        foreach (var hashGroup in chartsInFolder
            .Select(chart => new { Chart = chart, LookupHash = ChartLookupKey.GetPrimaryHash(chart) })
            .Where(x => !string.IsNullOrWhiteSpace(x.LookupHash))
            .GroupBy(x => x.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            var grouped = hashGroup.Select(x => x.Chart).ToList();
            if (grouped.Count <= 1)
            {
                continue;
            }

            // 保持対象: 更新日時が最も古い → ファイル名が最も短い
            ChartFile keeper = grouped
                .OrderBy(chart =>
                {
                    try { return LongPathFileSystem.GetLastWriteTime(chart.Path, isDirectory: false); }
                    catch { return DateTime.MaxValue; }
                })
                .ThenBy(chart => Path.GetFileName(chart.Path).Length)
                .First();

            deletionList.AddRange(grouped.Where(f => f != keeper));
        }

        if (deletionList.Count == 0)
        {
            return;
        }

        // 確認ダイアログ
        if (Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, deletionList.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        // 処理後の自動選択用: フォルダ1つなのでグループは消滅 → 次のグループを自動選択
        _pendingDuplicateGroupHeader = null;
        if (viewModel.DuplicateChartGroups != null)
        {
            int currentIndex = viewModel.DuplicateChartGroups.IndexOf(duplicateGroup);
            if (currentIndex >= 0 && currentIndex + 1 < viewModel.DuplicateChartGroups.Count)
            {
                _pendingDuplicateGroupHeader = viewModel.DuplicateChartGroups[currentIndex + 1].Header;
            }
        }

        Task.Run(delegate
        {
            viewModel.RemoveLibraryCharts(deletionList);

            NLogWrapper.FileLogger?.Info(string.Format(
                "Cleaned up {0} duplicate hash BMS file(s) in folder: {1}",
                deletionList.Count, Path.GetFileName(folderPath)));

        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                return;
            }
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateHashCleanup");
    }

    /// <summary>
    /// フォルダが3つ以上の場合にコンテキストメニューをプログラムから開き、
    /// 「マージ先」サブメニューを展開した状態にする。
    /// </summary>
    private void OpenDuplicateFolderContextMenu(TreeViewItem folderItem)
    {
        ContextMenu contextMenu = folderItem.ContextMenu;
        if (contextMenu == null)
        {
            return;
        }

        contextMenu.PlacementTarget = folderItem;
        contextMenu.IsOpen = true;

        // コンテキストメニューのOpenedイベントでマージ先リストが生成されるため、
        // メニューが開いた後にサブメニューを展開する
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            foreach (Control item in (IEnumerable)contextMenu.Items)
            {
                if (item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto" && item is MenuItem mergeMenuItem)
                {
                    mergeMenuItem.IsSubmenuOpen = true;
                    break;
                }
            }
        }));
    }

    private void calcelAllContextMenuTasks()
    {
        if (changeSubmenuOpenDocumentTask?.IsCompleted == false && tableContextMenuTaskTokenSource != null)
        {
            tableContextMenuTaskTokenSource.Cancel();
            NLogWrapper.DebuggerLogger?.Trace("Cancel data grid context menu async tasks");
        }
    }

    private void initContextMenuTasks()
    {
        tableContextMenuTaskTokenSource = new CancellationTokenSource();
        changeSubmenuOpenDocumentTask = null;
    }

    private void tableContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            if (sender is ContextMenu blockedContextMenu)
            {
                CloseContextMenuIfOpen(blockedContextMenu);
            }
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        bool isPlaylistRow = GridRowResolver.IsPlaylistRow(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        MainViewOperationSection effectiveSection = mainWindowViewModel.CurrentMainViewOperationSection;
        bool isPendingSelected = IsPendingMainViewSection(effectiveSection);
        bool isInstalledSelected = IsInstalledMainViewSection(effectiveSection);
        bool isPlaylistSelected = IsPlaylistMainViewSection(effectiveSection);
        ChartOperationSourceScope sourceScope = mainWindowViewModel.CurrentMainViewChartOperationSourceScope;
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget rowTarget))
        {
            rowTarget = null;
            if (!isPlaylistRow)
            {
                NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=True but unsupported rowType=" + row?.GetType().FullName);
                return;
            }
        }
        ChartContextMenuState contextMenuState = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow,
            rowUrl,
            rowUrlDiff,
            isPendingSelected,
            isInstalledSelected,
            isPlaylistSelected,
            rowTarget,
            GetSelectedChartTargets(isPendingSelected)));
        bool isInstallListSelected = contextMenuState.IsInstallListSelected;
        bool isPlaylistContext = contextMenuState.IsPlaylistContext;
        string chartPath = contextMenuState.ChartPath;
        IReadOnlyList<ChartOperationTarget> selectedTargets = contextMenuState.SelectedTargets;
        bool isBmsonContextRow = contextMenuState.IsBmsonContextRow;
        bool hasBmsonSelection = contextMenuState.HasBmsonSelection;
        bool hasBmsSelection = contextMenuState.HasBmsSelection;
        calcelAllContextMenuTasks();
        initContextMenuTasks();
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItemFindExternalPackage = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem7 = null;
        MenuItem menuItem8 = null;
        MenuItem menuItem9 = null;
        MenuItem menuItem10 = null;
        MenuItem menuItem11 = null;
        MenuItem menuItem12 = null;
        MenuItem menuItemFullScanAllCharts = null;
        MenuItem menuItem13 = null;
        MenuItem menuItem14 = null;
        MenuItem menuItem15 = null;
        MenuItem menuItem16 = null;
        MenuItem menuItem17 = null;
        MenuItem menuItemOpenLr2Ir = null;
        MenuItem menuItemOpenMocha = null;
        MenuItem menuItemOpenMinIr = null;
        MenuItem menuItemOpenInstallDestination = null;
        MenuItem menuItemOpenDocument = null;
        MenuItem menuItem18 = null;
        MenuItem menuItemRenameInvalidExt = null;
        Separator separator = null;
        MenuItem menuItem19 = null;
        Separator separator2 = null;
        MenuItem menuItemRemoveChartInfoParseFailure = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenLR2IR":
                    menuItemOpenLr2Ir = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenMocha":
                    menuItemOpenMocha = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenMinIR":
                    menuItemOpenMinIr = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenURL":
                    menuItem = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    menuItem2 = item as MenuItem;
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    menuItemFindExternalPackage = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenExplorer":
                    menuItem3 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenInstallDestination":
                    menuItemOpenInstallDestination = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenBMSFile":
                    menuItem4 = item as MenuItem;
                    break;
                case "tableContextMenuItemRegisterScore":
                    menuItem18 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenDocument":
                    menuItemOpenDocument = item as MenuItem;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    menuItem7 = item as MenuItem;
                    break;
                case "tableContextMenuItemInstall":
                    menuItem8 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixInstall":
                    menuItem9 = item as MenuItem;
                    break;
                case "tableContextMenuItemFullScanCheck":
                    menuItem10 = item as MenuItem;
                    foreach (Control item2 in (IEnumerable)menuItem10.Items)
                    {
                        string name = item2.Name;
                        if (name == "tableContextMenuItemFullScanCheckAllCharts")
                        {
                            menuItemFullScanAllCharts = item2 as MenuItem;
                        }
                        else if (!(name == "tableContextMenuItemIgnoreFileScanCheck"))
                        {
                            if (name == "tableContextMenuItemNotIgnoreFileScanCheck")
                            {
                                menuItem12 = item2 as MenuItem;
                            }
                        }
                        else
                        {
                            menuItem11 = item2 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemMoveFile":
                    menuItem13 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteEntry":
                    menuItem14 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteFile":
                    menuItem15 = item as MenuItem;
                    foreach (Control item3 in (IEnumerable)menuItem15.Items)
                    {
                        if (item3.Name == "tableContextMenuItemRenameInvalidExt")
                        {
                            menuItemRenameInvalidExt = item3 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemAutoRenameFolder":
                    menuItem16 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixEncoding":
                    menuItem17 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForFolderview":
                    separator = item as Separator;
                    break;
                case "tableContextMenuItemConvertToAudioFile":
                    menuItem19 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForConvert":
                    separator2 = item as Separator;
                    break;
                case "tableContextMenuItemRemoveChartInfoParseFailure":
                    menuItemRemoveChartInfoParseFailure = item as MenuItem;
                    break;
            }
        }
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        bool isBulkPlaylistUrlContext = effectivePlaylistUrlRows.Count > 1;
        if (isPlaylistRow)
        {
            if (menuItem != null)
            {
                menuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                menuItem.Visibility = Visibility.Visible;
                menuItem.IsEnabled = !playlistUrlBulkDownloadRunning && (isBulkPlaylistUrlContext
                    ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: false).Count > 0
                    : rowUrl != null && rowUrl.IsAbsoluteUri);
            }
            if (menuItem2 != null)
            {
                menuItem2.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                menuItem2.Visibility = Visibility.Visible;
                menuItem2.IsEnabled = !playlistUrlBulkDownloadRunning && (isBulkPlaylistUrlContext
                    ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: true).Count > 0
                    : rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri);
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Visible;
                menuItemFindExternalPackage.IsEnabled = CanStartPlaylistExternalPackageLookup(effectivePlaylistUrlRows);
            }
        }
        else
        {
            if (menuItem != null)
            {
                menuItem.Visibility = Visibility.Collapsed;
            }
            if (menuItem2 != null)
            {
                menuItem2.Visibility = Visibility.Collapsed;
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Collapsed;
            }
        }
        if (menuItem3 != null && menuItem4 != null && menuItemOpenDocument != null && changeSubmenuOpenDocumentTask == null)
        {
            menuItemOpenDocument.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(chartPath) && LongPathFileSystem.FileExists(chartPath))
            {
                menuItem3.IsEnabled = true;
                menuItem4.IsEnabled = true;
                menuItemOpenDocument.Visibility = Visibility.Visible;
                changeSubmenuOpenDocumentTask = Task.Run(delegate
                {
                    NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenDocumentTask");
                    CancellationToken token = tableContextMenuTaskTokenSource.Token;
                    string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(chartPath);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    try
                    {
                        List<string> list2 = [.. LongPathFileSystem.EnumerateFiles(directoryNameSimple, "*.txt"), .. LongPathFileSystem.EnumerateFiles(directoryNameSimple, "*.htm?")];
                        if (list2.Count > 0)
                        {
                            base.Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (!token.IsCancellationRequested && !_isClosingOrClosed)
                                {
                                    menuItemOpenDocument.ItemsSource = list2;
                                    menuItemOpenDocument.IsEnabled = true;
                                }
                            });
                        }
                    }
                    catch
                    {
                        base.Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (!token.IsCancellationRequested && !_isClosingOrClosed)
                            {
                                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                            }
                        });
                    }
                }, tableContextMenuTaskTokenSource.Token).Logging("tableContextMenuOpened");
            }
            else
            {
                menuItem3.IsEnabled = false;
                menuItem4.IsEnabled = false;
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
            }
        }
        bool flag = false;
        bool hasScoreViewerTarget = contextMenuState.HasScoreViewerTarget;
        if (menuItem18 != null && selectedTargets.Count > 1)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Register_chart_with_viewer;
            flag = (menuItem18.IsEnabled = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UseScoreViewer) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path)));
        }
        else if (menuItem18 != null)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Open_chart_viewer;
            flag = selectedTargets.Count == 1
                && selectedTargets[0].HasCapability(ChartOperationCapabilities.UseScoreViewer)
                && !string.IsNullOrWhiteSpace(selectedTargets[0].Chart.Path)
                && LongPathFileSystem.FileExists(selectedTargets[0].Chart.Path);
            menuItem18.IsEnabled = flag;
        }
        if (menuItem18 != null)
        {
            menuItem18.Visibility = hasScoreViewerTarget ? Visibility.Visible : Visibility.Collapsed;
        }
        if (menuItem19 != null)
        {
            menuItem19.IsEnabled = flag;
        }
        if (menuItemOpenLr2Ir != null)
        {
            bool canOpenLr2Ir = contextMenuState.CanOpenLr2Ir;
            menuItemOpenLr2Ir.Visibility = canOpenLr2Ir ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenLr2Ir.IsEnabled = canOpenLr2Ir;
        }
        string repositorySha256 = GridRowResolver.GetRepositorySha256(row);
        bool canOpenRepository = !string.IsNullOrWhiteSpace(repositorySha256);
        if (menuItemOpenMocha != null)
        {
            menuItemOpenMocha.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenMocha.IsEnabled = canOpenRepository;
        }
        if (menuItemOpenMinIr != null)
        {
            menuItemOpenMinIr.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenMinIr.IsEnabled = canOpenRepository;
        }
        if (menuItem7 != null)
        {
            bool hasRankingTarget = contextMenuState.HasRankingTarget;
            menuItem7.Visibility = hasRankingTarget ? Visibility.Visible : Visibility.Collapsed;
            menuItem7.IsEnabled = mainWindowViewModel.LR2ID != 0 && hasRankingTarget;
        }
        MenuItem menuItemDeleteInstallPackages = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
        List<string> selectedChartInfoParseFailureMd5s = GetSelectedChartInfoParseFailureMd5s();
        if (menuItemRemoveChartInfoParseFailure != null)
        {
            bool canRemoveChartInfoParseFailure = ShouldShowChartInfoParseFailureRemovalMenu(IsChartInfoParseErrorMainViewSection(effectiveSection), selectedChartInfoParseFailureMd5s);
            menuItemRemoveChartInfoParseFailure.Visibility = canRemoveChartInfoParseFailure ? Visibility.Visible : Visibility.Collapsed;
            menuItemRemoveChartInfoParseFailure.IsEnabled = canRemoveChartInfoParseFailure;
        }
        bool isNotOwnedPlaylistRow = rowTarget?.IsPlaylistMissing == true;
        NLogWrapper.FileLogger?.Info("playlist_context_menu rowType=" + row?.GetType().FullName + " isPlaylistRow=" + isPlaylistRow + " isPlaylistContext=" + isPlaylistContext + " isNotOwned=" + isNotOwnedPlaylistRow + " section=" + effectiveSection + " treeSection=" + _currentTreeSelectionSection + " sourceScope=" + sourceScope + " kind=" + rowTarget?.Chart.Kind + " path=" + (chartPath ?? string.Empty));
        if (menuItemOpenInstallDestination != null)
        {
            bool canOpenInstallDestination = contextMenuState.CanOpenInstallDestination;
            menuItemOpenInstallDestination.Visibility = (canOpenInstallDestination ? Visibility.Visible : Visibility.Collapsed);
            menuItemOpenInstallDestination.IsEnabled = canOpenInstallDestination;
        }
        if (menuItem8 != null)
        {
            menuItem8.Visibility = ((!isPendingSelected || isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem8.IsEnabled = isPendingSelected && !isPlaylistContext;
        }
        if (menuItem10 != null)
        {
            bool isFullScanMenuVisible = contextMenuState.CanShowResourceHealthMenu;
            menuItem10.Visibility = ((!isFullScanMenuVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = isFullScanMenuVisible;
        }
        if (menuItem13 != null)
        {
            bool canMoveSelectedFiles = contextMenuState.CanMoveSelectedFiles;
            menuItem13.Visibility = ((!canMoveSelectedFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = canMoveSelectedFiles && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path));
        }
        if (menuItem14 != null)
        {
            menuItem14.Visibility = ((!isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = isPlaylistContext;
        }
        if (menuItem15 != null)
        {
            bool canDeleteFiles = contextMenuState.CanDeleteFiles;
            menuItem15.Visibility = ((!canDeleteFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = canDeleteFiles;
            if (menuItemRenameInvalidExt != null)
            {
                bool canRenameInvalidExt = contextMenuState.CanRenameInvalidExtension;
                menuItemRenameInvalidExt.Visibility = canRenameInvalidExt ? Visibility.Visible : Visibility.Collapsed;
                menuItemRenameInvalidExt.IsEnabled = canRenameInvalidExt;
            }
        }
        if (separator != null)
        {
            bool isFolderViewSeparatorVisible = contextMenuState.CanShowFolderViewSeparator;
            separator.Visibility = ((!isFolderViewSeparatorVisible) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = isFolderViewSeparatorVisible;
        }
        if (menuItem16 != null)
        {
            bool canAutoRenameFolders = contextMenuState.CanAutoRenameFolders;
            menuItem16.Visibility = ((!canAutoRenameFolders) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = canAutoRenameFolders;
        }
        if (menuItem17 != null)
        {
            bool canFixEncoding = contextMenuState.CanFixEncoding;
            menuItem17.Visibility = ((!canFixEncoding) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = canFixEncoding;
        }
        if (menuItem9 != null)
        {
            bool isInstalledLocationFixVisible = IsFullScanMainViewSection(effectiveSection) && !isPlaylistContext;
            menuItem9.Visibility = ((!isInstalledLocationFixVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = isInstalledLocationFixVisible && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        }
        if (menuItem11 != null)
        {
            bool isSelected = treeViewItemFullScanCheck.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem11.Visibility = ((!isSelected) ? Visibility.Collapsed : Visibility.Visible);
            menuItem11.IsEnabled = isSelected && hasResourceHealthTarget;
        }
        if (menuItem12 != null)
        {
            bool isSelected2 = treeViewItemFullScanCheckIgnored.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem12.Visibility = ((!isSelected2) ? Visibility.Collapsed : Visibility.Visible);
            menuItem12.IsEnabled = isSelected2 && hasResourceHealthTarget;
        }
        if (menuItemFullScanAllCharts != null)
        {
            bool canRescanAllCharts = menuItem10?.Visibility == Visibility.Visible
                && !mainWindowViewModel.IsMaintenanceRescanProgressActive;
            menuItemFullScanAllCharts.Visibility = Visibility.Visible;
            menuItemFullScanAllCharts.IsEnabled = canRescanAllCharts;
        }
        if (menuItem19 != null && separator2 != null)
        {
            bool canConvertToAudio = contextMenuState.CanConvertToAudio;
            Separator convertSeparator = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!canConvertToAudio) ? Visibility.Collapsed : Visibility.Visible));
            convertSeparator.Visibility = visibility;
            Separator convertSeparator2 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = canConvertToAudio && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.ConvertToAudio) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path)));
            convertSeparator2.IsEnabled = isEnabled;
        }
        if (hasBmsonSelection)
        {
            if (!hasBmsSelection)
            {
                if (menuItemOpenLr2Ir != null)
                {
                    menuItemOpenLr2Ir.Visibility = Visibility.Collapsed;
                    menuItemOpenLr2Ir.IsEnabled = false;
                }
                if (menuItem18 != null)
                {
                    menuItem18.Visibility = Visibility.Collapsed;
                    menuItem18.IsEnabled = false;
                }
                if (menuItem7 != null)
                {
                    menuItem7.Visibility = Visibility.Collapsed;
                    menuItem7.IsEnabled = false;
                }
                if (menuItem19 != null)
                {
                    menuItem19.Visibility = Visibility.Collapsed;
                    menuItem19.IsEnabled = false;
                }
                if (menuItemRenameInvalidExt != null)
                {
                    menuItemRenameInvalidExt.Visibility = Visibility.Collapsed;
                    menuItemRenameInvalidExt.IsEnabled = false;
                }
                if (menuItem17 != null)
                {
                    menuItem17.Visibility = Visibility.Collapsed;
                    menuItem17.IsEnabled = false;
                }
            }
        }
        if (menuItemDeleteInstallPackages != null)
        {
            menuItemDeleteInstallPackages.Visibility = (isInstallListSelected ? Visibility.Visible : Visibility.Collapsed);
            menuItemDeleteInstallPackages.IsEnabled = contextMenuState.CanDeleteInstallPackages;
        }
        if (isNotOwnedPlaylistRow)
        {
            if (menuItem3 != null)
            {
                menuItem3.Visibility = Visibility.Collapsed;
                menuItem3.IsEnabled = false;
            }
            if (menuItem4 != null)
            {
                menuItem4.Visibility = Visibility.Collapsed;
                menuItem4.IsEnabled = false;
            }
            if (menuItemOpenDocument != null)
            {
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                menuItemOpenDocument.IsEnabled = false;
            }
            if (menuItem18 != null)
            {
                menuItem18.Visibility = Visibility.Collapsed;
                menuItem18.IsEnabled = false;
            }
            if (menuItem7 != null)
            {
                menuItem7.Visibility = Visibility.Collapsed;
                menuItem7.IsEnabled = false;
            }
            if (menuItem8 != null)
            {
                menuItem8.Visibility = Visibility.Collapsed;
                menuItem8.IsEnabled = false;
            }
            if (menuItem9 != null)
            {
                menuItem9.Visibility = Visibility.Collapsed;
                menuItem9.IsEnabled = false;
            }
            if (menuItem10 != null)
            {
                menuItem10.Visibility = Visibility.Collapsed;
                menuItem10.IsEnabled = false;
            }
            if (menuItem13 != null)
            {
                menuItem13.Visibility = Visibility.Collapsed;
                menuItem13.IsEnabled = false;
            }
            if (menuItem15 != null)
            {
                menuItem15.Visibility = Visibility.Collapsed;
                menuItem15.IsEnabled = false;
            }
            if (menuItem16 != null)
            {
                menuItem16.Visibility = Visibility.Collapsed;
                menuItem16.IsEnabled = false;
            }
            if (menuItem17 != null)
            {
                menuItem17.Visibility = Visibility.Collapsed;
                menuItem17.IsEnabled = false;
            }
            if (menuItem19 != null)
            {
                menuItem19.Visibility = Visibility.Collapsed;
                menuItem19.IsEnabled = false;
            }
            if (separator != null)
            {
                separator.Visibility = Visibility.Collapsed;
                separator.IsEnabled = false;
            }
            if (separator2 != null)
            {
                separator2.Visibility = Visibility.Collapsed;
                separator2.IsEnabled = false;
            }
            if (menuItemDeleteInstallPackages != null)
            {
                menuItemDeleteInstallPackages.Visibility = Visibility.Collapsed;
                menuItemDeleteInstallPackages.IsEnabled = false;
            }
        }
    }

    private void tableContextMenuPlaylistMissingOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_playlist_missing_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget rowTarget);
        bool isBmsonContextRow = rowTarget?.Chart.Kind == ChartFileKind.Bmson;
        string repositorySha256 = GridRowResolver.GetRepositorySha256(row);
        bool canOpenRepository = !string.IsNullOrWhiteSpace(repositorySha256);
        bool canOpenScoreViewer = rowTarget?.HasCapability(ChartOperationCapabilities.UseScoreViewer) == true;
        bool canUpdateRanking = rowTarget?.HasCapability(ChartOperationCapabilities.UpdateRanking) == true && base.DataContext is MainWindowViewModel viewModel && viewModel.LR2ID != 0;
        bool canOpenLr2Ir = rowTarget?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true;
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        bool isBulkPlaylistUrlContext = effectivePlaylistUrlRows.Count > 1;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenLR2IR":
                    item.Visibility = canOpenLr2Ir ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenLr2Ir;
                    break;
                case "tableContextMenuItemOpenMocha":
                case "tableContextMenuItemOpenMinIR":
                    item.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenRepository;
                    break;
                case "tableContextMenuItemOpenURL":
                    if (item is MenuItem openUrlMenuItem)
                    {
                        openUrlMenuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = !playlistUrlBulkDownloadRunning && (isBulkPlaylistUrlContext
                        ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: false).Count > 0
                        : rowUrl != null && rowUrl.IsAbsoluteUri);
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    if (item is MenuItem openUrlDiffMenuItem)
                    {
                        openUrlDiffMenuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = !playlistUrlBulkDownloadRunning && (isBulkPlaylistUrlContext
                        ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: true).Count > 0
                        : rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri);
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = CanStartPlaylistExternalPackageLookup(effectivePlaylistUrlRows);
                    break;
                case "tableContextMenuItemDeleteEntry":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = entry != null;
                    break;
                case "tableContextMenuItemRegisterScore":
                    item.Visibility = canOpenScoreViewer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenScoreViewer;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    item.Visibility = canUpdateRanking ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canUpdateRanking;
                    break;
            }
        }
        NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowType=" + row?.GetType().FullName + " entryParent=" + entry?.parent?.name + " isBmson=" + isBmsonContextRow + " url=" + (rowUrl != null) + " urlDiff=" + (rowUrlDiff != null) + " canOpenLr2Ir=" + canOpenLr2Ir + " canOpenRepository=" + canOpenRepository + " canOpenScoreViewer=" + canOpenScoreViewer + " canUpdateRanking=" + canUpdateRanking);
    }

    private void playHistoryContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_play_history_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row)
            || !PlayHistoryContextMenuState.TryCreate(row, out PlayHistoryContextMenuState state))
        {
            return;
        }

        calcelAllContextMenuTasks();
        _lastOpenedContextMenu = contextMenu;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "playHistoryContextMenuItemOpenBMSIR":
                    item.Visibility = state.CanOpenBmsIr ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenBmsIr;
                    break;
                case "playHistoryContextMenuItemOpenMocha":
                case "playHistoryContextMenuItemOpenMinIR":
                    item.Visibility = state.CanOpenRepository ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenRepository;
                    break;
                case "playHistoryContextMenuSeparatorLocal":
                    item.Visibility = state.HasExternalLinkItem && state.HasLocalChartItem ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemOpenExplorer":
                    item.Visibility = state.CanOpenExplorer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenExplorer && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuItemRegisterScore":
                    item.Visibility = state.CanOpenScoreViewer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenScoreViewer && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuSeparatorHash":
                    item.Visibility = (state.HasExternalLinkItem || state.HasLocalChartItem) && state.HasHashCopyItem ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemCopyMd5":
                    item.Visibility = state.CanCopyMd5 ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanCopyMd5;
                    break;
                case "playHistoryContextMenuItemCopySha256":
                    item.Visibility = state.CanCopySha256 ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanCopySha256;
                    break;
            }
        }
    }

    private void playHistoryContextMenuItemOpenBMSIRClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row)
            || !PlayHistoryContextMenuState.TryCreate(row, out PlayHistoryContextMenuState state)
            || !state.CanOpenBmsIr)
        {
            return;
        }

        string url = GetBmsIrSongUrl(state.Md5);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Process.Start(url);
        }
        e.Handled = true;
    }

    private void playHistoryContextMenuItemCopyHashClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string copyKind }
            || !TryGetContextMenuRow(sender, out object row)
            || !PlayHistoryContextMenuState.TryCreate(row, out PlayHistoryContextMenuState state))
        {
            return;
        }

        string value = state.GetCopyValue(copyKind);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Clipboard.SetText(value);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row)
            || !PlayHistoryContextMenuState.TryCreate(row, out PlayHistoryContextMenuState state)
            || !state.CanOpenExplorer
            || !LongPathFileSystem.FileExists(state.ChartPath))
        {
            return;
        }

        ExplorerOpenService.OpenFileAndSelect(state.ChartPath);
        e.Handled = true;
    }

    private async void playHistoryContextMenuItemRegisterScoreViewerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row)
            || !PlayHistoryContextMenuState.TryCreate(row, out PlayHistoryContextMenuState state)
            || !state.CanOpenScoreViewer)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel)
        {
            return;
        }

        var targets = new List<ScoreViewerTarget> { new(state.Md5, state.ChartPath, state.ChartTitle) };
        e.Handled = true;
        await RunScoreViewerRegistrationAsync(targets, openSingleViewerOnSuccess: true, "playHistoryContextMenuItemRegisterScoreViewerClick");
    }

    private void tableContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.OpenFolder))
        {
            return;
        }
        string path = target.Chart.Path;
        if (!LongPathFileSystem.FileExists(path))
        {
            return;
        }
        ExplorerOpenService.OpenFileAndSelect(path);
    }

    private bool TryResolveInstallDestination(ChartFile chart, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (chart == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(chart.InstallDestination))
        {
            if (LongPathFileSystem.DirectoryExists(chart.InstallDestination))
            {
                installDir = chart.InstallDestination;
                return true;
            }
            reason = string.Format(BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found, chart.InstallDestination);
            return false;
        }
        string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
        if (!string.IsNullOrWhiteSpace(lookupHash)
            && base.DataContext is MainWindowViewModel mainWindowViewModel
            && mainWindowViewModel.TryGetInstalledDirectoryByHash(lookupHash, out string installDir2))
        {
            installDir = installDir2;
            return true;
        }
        reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        return false;
    }

    private bool TryResolveInstallDestination(ChartPackage pkg, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (pkg == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        foreach (var entry in pkg.ChartEntries)
        {
            if (TryResolveInstallDestination(entry?.Chart, out installDir, out reason))
            {
                return true;
            }
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        }
        return false;
    }

    private void OpenInstallDestinationInExplorer(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(installDir);
    }

    private void tableContextMenuItemOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel || !IsPendingMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets.Count == 0)
        {
            return;
        }
        if (targets.Count > 1)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
        if (!TryResolveInstallDestination(targets[0].Chart, out string installDir, out string reason))
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private void treeViewInstallPackageContextMenuOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } }))
        {
            return;
        }
        if (!TryResolveInstallDestination(dataContext, out string installDir, out string reason))
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private static string GetBmsIrSongUrl(string md5)
    {
        if (string.IsNullOrWhiteSpace(md5) || !Regex.IsMatch(md5.Trim(), "^[A-F0-9]{32}$", RegexOptions.IgnoreCase))
        {
            return null;
        }
        return "https://bms-ir.org/new/song?songmd5=" + md5.Trim() + "&view=both";
    }

    private static string GetMochaSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://mocha-repository.info/song.php?sha256=" + sha256;
    }

    private static string GetMinIrSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://www.gaftalk.com/minir/#/viewer/song/" + sha256 + "/0";
    }

    private void OpenRepositoryUrlForRow(object row, Func<string, string> urlFactory)
    {
        string sha256 = GridRowResolver.GetRepositorySha256(row);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }
        string url = urlFactory?.Invoke(sha256);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Process.Start(url);
        }
    }

    private void tableContextMenuItemOpenBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.OpenFile))
        {
            return;
        }
        string path = target.Chart.Path;
        if (!LongPathFileSystem.FileExists(path))
        {
            return;
        }
        try
        {
            Process.Start(path);
        }
        catch
        {
        }
    }

    private void tableContextMenuItemOpenLR2IRClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.UseLr2Ir))
        {
            return;
        }
        string text = GetBmsIrSongUrl(target.Chart.Md5);
        if (text != null)
        {
            Process.Start(text);
        }
    }

    private void tableContextMenuItemOpenMochaClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row))
        {
            OpenRepositoryUrlForRow(row, GetMochaSongUrl);
        }
    }

    private void tableContextMenuItemOpenMinIRClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row))
        {
            OpenRepositoryUrlForRow(row, GetMinIrSongUrl);
        }
    }

    private async void tableContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: false, "datagrid_context_menu_open_url").Logging("tableContextMenuItemOpenURLClick");
    }

    private async void tableContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: true, "datagrid_context_menu_open_url_diff").Logging("tableContextMenuItemOpenURLdiffClick");
    }

    private async void tableContextMenuItemFindExternalPackageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await FindExternalPackageFromContextMenuAsync(e.Source).Logging("tableContextMenuItemFindExternalPackageClick");
    }

    private async Task FindExternalPackageFromContextMenuAsync(object source)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_find_external_package"))
        {
            return;
        }
        if (!TryGetContextMenuRow(source, out object contextRow))
        {
            return;
        }
        await DownloadSelectedPlaylistExternalPackagesAsync(GetEffectiveContextMenuRows(contextRow));
    }

    private async Task OpenPlaylistUrlFromContextMenuAsync(object source, bool isDiffUrl, string blockReason)
    {
        if (ShouldBlockStartupUiInteraction(blockReason))
        {
            return;
        }
        if (!TryGetContextMenuRow(source, out object contextRow))
        {
            return;
        }
        List<object> rows = GetEffectiveContextMenuRows(contextRow);
        if (rows.Count <= 1)
        {
            OpenPlaylistUrlInBrowser(contextRow, isDiffUrl);
            return;
        }
        await DownloadSelectedPlaylistUrlsAsync(rows, isDiffUrl);
    }

    private static void OpenPlaylistUrlInBrowser(object row, bool isDiffUrl)
    {
        Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(row) : GridRowResolver.GetUrl(row);
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        Process.Start(url.ToString());
    }

    private List<object> GetEffectiveContextMenuRows(object contextRow)
    {
        if (contextRow == null)
        {
            return [];
        }
        List<object> selectedRows = GetSelectedGridRowsSnapshot();
        if (selectedRows.Any(row => ReferenceEquals(row, contextRow)))
        {
            return selectedRows;
        }
        return [contextRow];
    }

    private bool CanStartPlaylistExternalPackageLookup(IEnumerable<object> rows)
    {
        return !playlistUrlBulkDownloadRunning
            && base.DataContext is not MainWindowViewModel { IsDropInstallQueueActive: true }
            && PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rows).Count > 0;
    }

    private async Task DownloadSelectedPlaylistExternalPackagesAsync(IEnumerable<object> rows)
    {
        if (playlistUrlBulkDownloadRunning)
        {
            return;
        }
        List<string> targets = PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rows);
        if (targets.Count == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupNoTargets, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (base.DataContext is MainWindowViewModel { IsDropInstallQueueActive: true })
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupBlockedByInstallQueue, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string confirmationMessage = string.Format(
            BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistExternalPackageLookup,
            targets.Count);
        if (UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            confirmationMessage,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel,
            warningMessageBoxText: BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookup) == MessageBoxResult.Cancel)
        {
            return;
        }

        var downloadedPaths = new List<string>();
        var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedDownloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int noCandidateCount = 0;
        int duplicateDownloadedUrlCount = 0;
        int duplicateFailedUrlCount = 0;
        int blockedBySizeLimitCount = 0;
        int unsupportedCount = 0;
        int failedCount = 0;
        int canceledCount = 0;
        var viewModel = base.DataContext as MainWindowViewModel;
        var cancellation = new CancellationTokenSource();
        playlistUrlBulkDownloadRunning = true;
        playlistUrlBulkDownloadCancellation = cancellation;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                string targetMd5 = targets[i];
                UpdatePlaylistUrlBulkDownloadStatus(
                    viewModel,
                    true,
                    targets.Count,
                    i,
                    targetMd5,
                    canCancel: true,
                    labelFormat: BeMusicSeeker.Properties.Resources.Playlist_external_package_lookup_progress_label_format);
                PlaylistExternalPackageWorkflowResult result;
                try
                {
                    result = await playlistExternalPackageLookupService.DownloadFirstAvailablePackageAsync(
                        targetMd5,
                        (lookupResult, token) => DownloadExternalPackageLookupCandidateAsync(lookupResult, downloadedKeys, token),
                        downloadedKeys,
                        failedDownloadKeys,
                        CreatePlaylistUrlDownloadKey,
                        LogPlaylistUrlDownload,
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                switch (result.Kind)
                {
                    case PlaylistExternalPackageWorkflowResultKind.Downloaded when !string.IsNullOrWhiteSpace(result.FilePath) && LongPathFileSystem.FileExists(result.FilePath):
                        downloadedPaths.Add(result.FilePath);
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.NoCandidate:
                        noCandidateCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl:
                        duplicateDownloadedUrlCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.DuplicateFailedUrl:
                        duplicateFailedUrlCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.BlockedBySizeLimit:
                        blockedBySizeLimitCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.Unsupported:
                        unsupportedCount++;
                        break;
                    default:
                        failedCount++;
                        break;
                }
                UpdatePlaylistUrlBulkDownloadStatus(
                    viewModel,
                    true,
                    targets.Count,
                    i + 1,
                    targetMd5,
                    canCancel: !cancellation.IsCancellationRequested,
                    labelFormat: BeMusicSeeker.Properties.Resources.Playlist_external_package_lookup_progress_label_format);
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i - 1;
                    break;
                }
            }
        }
        finally
        {
            playlistUrlBulkDownloadRunning = false;
            playlistUrlBulkDownloadCancellation = null;
            UpdatePlaylistUrlBulkDownloadStatus(viewModel, false, 0, 0, string.Empty, canCancel: false);
            cancellation.Dispose();
        }
        if (downloadedPaths.Count > 0)
        {
            viewModel?.EnqueueDroppedInstallPaths(downloadedPaths);
            newlyInstalledTreeViewItem.IsExpanded = true;
        }
        string resultMessage = string.Format(
            BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistExternalPackageLookupResult,
            targets.Count,
            downloadedPaths.Count,
            noCandidateCount,
            duplicateDownloadedUrlCount,
            duplicateFailedUrlCount,
            blockedBySizeLimitCount,
            unsupportedCount,
            failedCount,
            canceledCount);
        UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            resultMessage,
            BeMusicSeeker.Properties.Resources.Information,
            MessageBoxButton.OK,
            downloadedPaths.Count > 0 ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    private async Task<PlaylistExternalPackageDownloadAttempt> DownloadExternalPackageLookupCandidateAsync(PlaylistExternalPackageLookupResult lookupResult, HashSet<string> downloadedKeys, CancellationToken cancellationToken)
    {
        if (lookupResult == null)
        {
            return PlaylistExternalPackageDownloadAttempt.Failed();
        }
        PlaylistUrlDownloadResult result = await DownloadPlaylistUrlCandidateAsync(lookupResult.DownloadUri, downloadedKeys, allowSharedPageResolution: false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LogPlaylistUrlDownload("playlist_external_package_lookup download_result provider=" + lookupResult.ProviderId + " md5=" + lookupResult.ChartMd5 + " kind=" + result.Kind + " url=" + lookupResult.DownloadUri);
        return result.Kind switch
        {
            PlaylistUrlDownloadResultKind.Downloaded => PlaylistExternalPackageDownloadAttempt.Downloaded(result.FilePath, result.DownloadKey),
            PlaylistUrlDownloadResultKind.Duplicate => PlaylistExternalPackageDownloadAttempt.DuplicateDownloadedUrl(result.DownloadKey),
            PlaylistUrlDownloadResultKind.BlockedBySizeLimit => PlaylistExternalPackageDownloadAttempt.BlockedBySizeLimit(result.DownloadKey),
            PlaylistUrlDownloadResultKind.BrowserFallback => PlaylistExternalPackageDownloadAttempt.Unsupported(result.DownloadKey),
            _ => PlaylistExternalPackageDownloadAttempt.Failed(result.DownloadKey)
        };
    }

    private async Task DownloadSelectedPlaylistUrlsAsync(IEnumerable<object> rows, bool isDiffUrl)
    {
        if (playlistUrlBulkDownloadRunning)
        {
            return;
        }
        List<Uri> targets = PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(rows, isDiffUrl);
        if (targets.Count == 0)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadNoTargets, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (base.DataContext is MainWindowViewModel { IsDropInstallQueueActive: true })
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string confirmationMessage = string.Format(
            BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistUrlDownload,
            targets.Count,
            isDiffUrl ? BeMusicSeeker.Properties.Resources.Diff_URL : BeMusicSeeker.Properties.Resources.Original_URL);
        string largeSelectionWarningMessage = targets.Count >= SelectedPlaylistUrlDownloadLargeSelectionWarningThreshold
            ? string.Format(
                BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadLargeSelection,
                SelectedPlaylistUrlDownloadLargeSelectionWarningThreshold)
            : null;
        if (largeSelectionWarningMessage != null)
        {
            LogPlaylistUrlDownload("playlist_url_download large_selection_warning count=" + targets.Count + " threshold=" + SelectedPlaylistUrlDownloadLargeSelectionWarningThreshold);
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), confirmationMessage, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel, warningMessageBoxText: largeSelectionWarningMessage) == MessageBoxResult.Cancel)
        {
            return;
        }
        var downloadedPaths = new List<string>();
        var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int browserFallbackCount = 0;
        int blockedBySizeLimitCount = 0;
        int duplicateCount = 0;
        int failedCount = 0;
        int canceledCount = 0;
        var viewModel = base.DataContext as MainWindowViewModel;
        var cancellation = new CancellationTokenSource();
        playlistUrlBulkDownloadRunning = true;
        playlistUrlBulkDownloadCancellation = cancellation;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                Uri target = targets[i];
                UpdatePlaylistUrlBulkDownloadStatus(viewModel, true, targets.Count, i, target.ToString(), canCancel: true);
                PlaylistUrlDownloadResult result;
                try
                {
                    result = await DownloadPlaylistUrlCandidateAsync(target, downloadedKeys, cancellationToken: cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                switch (result.Kind)
                {
                    case PlaylistUrlDownloadResultKind.Downloaded when !string.IsNullOrWhiteSpace(result.FilePath) && LongPathFileSystem.FileExists(result.FilePath):
                        downloadedPaths.Add(result.FilePath);
                        break;
                    case PlaylistUrlDownloadResultKind.BlockedBySizeLimit:
                        blockedBySizeLimitCount++;
                        break;
                    case PlaylistUrlDownloadResultKind.Duplicate:
                        duplicateCount++;
                        break;
                    case PlaylistUrlDownloadResultKind.Failed:
                        failedCount++;
                        break;
                    default:
                        browserFallbackCount++;
                        break;
                }
                UpdatePlaylistUrlBulkDownloadStatus(viewModel, true, targets.Count, i + 1, target.ToString(), canCancel: !cancellation.IsCancellationRequested);
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i - 1;
                    break;
                }
            }
        }
        finally
        {
            playlistUrlBulkDownloadRunning = false;
            playlistUrlBulkDownloadCancellation = null;
            UpdatePlaylistUrlBulkDownloadStatus(viewModel, false, 0, 0, string.Empty, canCancel: false);
            cancellation.Dispose();
        }
        if (downloadedPaths.Count > 0)
        {
            viewModel?.EnqueueDroppedInstallPaths(downloadedPaths);
            newlyInstalledTreeViewItem.IsExpanded = true;
        }
        string resultMessage = string.Format(
            BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistUrlDownloadResult,
            targets.Count,
            downloadedPaths.Count,
            browserFallbackCount,
            blockedBySizeLimitCount,
            duplicateCount,
            failedCount,
            canceledCount);
        UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            resultMessage,
            BeMusicSeeker.Properties.Resources.Information,
            MessageBoxButton.OK,
            downloadedPaths.Count > 0 ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    private void UpdatePlaylistUrlBulkDownloadStatus(MainWindowViewModel viewModel, bool isActive, int totalCount, int completedCount, string currentDisplayName, bool canCancel, string labelFormat = null)
    {
        if (isActive)
        {
            playlistUrlBulkDownloadTotalCount = Math.Max(0, totalCount);
            playlistUrlBulkDownloadCompletedCount = Math.Max(0, completedCount);
            playlistUrlBulkDownloadCurrentDisplayName = currentDisplayName ?? string.Empty;
            playlistUrlBulkDownloadLabelFormat = labelFormat ?? string.Empty;
        }
        else
        {
            playlistUrlBulkDownloadTotalCount = 0;
            playlistUrlBulkDownloadCompletedCount = 0;
            playlistUrlBulkDownloadCurrentDisplayName = string.Empty;
            playlistUrlBulkDownloadLabelFormat = string.Empty;
        }
        viewModel?.UpdatePlaylistUrlDownloadStatus(isActive, totalCount, completedCount, currentDisplayName, canCancel, labelFormat);
    }

    private void CancelPlaylistUrlBulkDownload()
    {
        CancellationTokenSource cancellation = playlistUrlBulkDownloadCancellation;
        if (!playlistUrlBulkDownloadRunning || cancellation == null || cancellation.IsCancellationRequested)
        {
            return;
        }
        cancellation.Cancel();
        LogPlaylistUrlDownload("playlist_url_download cancel_requested completed=" + playlistUrlBulkDownloadCompletedCount + " total=" + playlistUrlBulkDownloadTotalCount + " current=" + SanitizePlaylistUrlDownloadLogValue(playlistUrlBulkDownloadCurrentDisplayName));
        UpdatePlaylistUrlBulkDownloadStatus(
            base.DataContext as MainWindowViewModel,
            true,
            playlistUrlBulkDownloadTotalCount,
            playlistUrlBulkDownloadCompletedCount,
            playlistUrlBulkDownloadCurrentDisplayName,
            canCancel: false,
            labelFormat: playlistUrlBulkDownloadLabelFormat);
    }

    private void tableContextMenuItemOpenDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: string dataContext } && LongPathFileSystem.FileExists(dataContext))
        {
            Process.Start(dataContext);
        }
    }

    private sealed class PlaylistUrlDownloadResult
    {
        private PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind kind, string filePath = null, string downloadKey = null)
        {
            Kind = kind;
            FilePath = filePath ?? string.Empty;
            DownloadKey = downloadKey ?? string.Empty;
        }

        internal PlaylistUrlDownloadResultKind Kind { get; }

        internal string FilePath { get; }

        internal string DownloadKey { get; }

        internal static PlaylistUrlDownloadResult Downloaded(string filePath, string downloadKey)
        {
            return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Downloaded, filePath, downloadKey);
        }

        internal static PlaylistUrlDownloadResult BrowserFallback(string downloadKey = null)
        {
            return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.BrowserFallback, downloadKey: downloadKey);
        }

        internal static PlaylistUrlDownloadResult BlockedBySizeLimit(string downloadKey = null)
        {
            return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.BlockedBySizeLimit, downloadKey: downloadKey);
        }

        internal static PlaylistUrlDownloadResult Duplicate(string downloadKey)
        {
            return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Duplicate, downloadKey: downloadKey);
        }

        internal static PlaylistUrlDownloadResult Failed(string downloadKey = null)
        {
            return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Failed, downloadKey: downloadKey);
        }
    }

    private async Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlCandidateAsync(Uri uri, HashSet<string> downloadedKeys = null, bool allowSharedPageResolution = true, CancellationToken cancellationToken = default)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return PlaylistUrlDownloadResult.BrowserFallback();
        }
        cancellationToken.ThrowIfCancellationRequested();
        Uri normalizedUri = NormalizeDownloadUri(uri);
        if (IsBrowserFallbackDownloadUri(normalizedUri))
        {
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(normalizedUri));
        }
        string tempDirectory = TempDirectoryPublisher.Get();
        try
        {
            using AppHttpResponse response = await AppHttpClient.Shared.OpenReadAsync(normalizedUri, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await DownloadPlaylistUrlResponseCandidateAsync(normalizedUri, response, tempDirectory, allowSharedPageResolution, downloadedKeys, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPlaylistUrlDownload("playlist_url_download failed source=" + uri + " normalized=" + normalizedUri + " errorType=" + ex.GetType().FullName + " error=" + SanitizePlaylistUrlDownloadLogValue(ex.Message));
            return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(normalizedUri));
        }
    }

    private static Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlResponseCandidateAsync(Uri requestedUri, AppHttpResponse response, string tempDirectory, bool allowSharedPageResolution, HashSet<string> downloadedKeys = null, CancellationToken cancellationToken = default)
    {
        return DownloadPlaylistUrlResponseCandidateAsync(requestedUri, response, tempDirectory, allowSharedPageResolution ? 4 : 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), downloadedKeys, cancellationToken);
    }

    private static async Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlResponseCandidateAsync(Uri requestedUri, AppHttpResponse response, string tempDirectory, int remainingSharedPageResolutionDepth, HashSet<string> resolvedPageUris, HashSet<string> downloadedKeys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddUriWithoutFragment(resolvedPageUris, requestedUri);
        AddUriWithoutFragment(resolvedPageUris, response?.ResponseUri);
        if (response.ContentLength.HasValue)
        {
            if (response.ContentLength.Value == 0L)
            {
                return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
            }
            if (response.ContentLength.Value > DownloadAndInstallSizeLimitBytes)
            {
                LogPlaylistUrlDownload("playlist_url_download blocked_size_limit source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " contentLength=" + response.ContentLength.Value + " limitBytes=" + DownloadAndInstallSizeLimitBytes);
                return PlaylistUrlDownloadResult.BlockedBySizeLimit(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
            }
        }
        if (remainingSharedPageResolutionDepth > 0 && ShouldResolveSharedDownloadPageBeforeFileName(requestedUri, response))
        {
            Uri resolvedUri = await TryResolveSharedDownloadPageUriAsync(requestedUri, response, cancellationToken).ConfigureAwait(false);
            if (resolvedUri != null && AddUriWithoutFragment(resolvedPageUris, resolvedUri))
            {
                string resolvedDownloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(resolvedDownloadKey) && downloadedKeys != null && downloadedKeys.Contains(resolvedDownloadKey))
                {
                    LogPlaylistUrlDownload("playlist_url_download duplicate source=" + requestedUri + " resolved=" + resolvedUri + " key=" + resolvedDownloadKey);
                    return PlaylistUrlDownloadResult.Duplicate(resolvedDownloadKey);
                }
                LogPlaylistUrlDownload("playlist_url_download resolved source=" + requestedUri + " resolved=" + resolvedUri + " depth=" + remainingSharedPageResolutionDepth);
                using AppHttpResponse resolvedResponse = await AppHttpClient.Shared.OpenReadAsync(resolvedUri, cancellationToken).ConfigureAwait(false);
                return await DownloadPlaylistUrlResponseCandidateAsync(resolvedUri, resolvedResponse, tempDirectory, remainingSharedPageResolutionDepth - 1, resolvedPageUris, downloadedKeys, cancellationToken).ConfigureAwait(false);
            }
            LogPlaylistUrlDownload("playlist_url_download unresolved_shared_page source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " contentType=" + GetContentTypeLogValue(response));
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        string fileName = ResolveDownloadedArchiveFileName(requestedUri, response);
        if (IsHtmlContentType(response))
        {
            LogPlaylistUrlDownload("playlist_url_download skipped_html source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " fileName=" + fileName);
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        if (!IsDownloadAndInstallCandidateFileName(fileName))
        {
            Uri resolvedUri = remainingSharedPageResolutionDepth > 0 ? await TryResolveSharedDownloadPageUriAsync(requestedUri, response, cancellationToken).ConfigureAwait(false) : null;
            if (resolvedUri != null && AddUriWithoutFragment(resolvedPageUris, resolvedUri))
            {
                string resolvedDownloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(resolvedDownloadKey) && downloadedKeys != null && downloadedKeys.Contains(resolvedDownloadKey))
                {
                    LogPlaylistUrlDownload("playlist_url_download duplicate source=" + requestedUri + " resolved=" + resolvedUri + " key=" + resolvedDownloadKey);
                    return PlaylistUrlDownloadResult.Duplicate(resolvedDownloadKey);
                }
                LogPlaylistUrlDownload("playlist_url_download resolved source=" + requestedUri + " resolved=" + resolvedUri + " depth=" + remainingSharedPageResolutionDepth);
                using AppHttpResponse resolvedResponse = await AppHttpClient.Shared.OpenReadAsync(resolvedUri, cancellationToken).ConfigureAwait(false);
                return await DownloadPlaylistUrlResponseCandidateAsync(resolvedUri, resolvedResponse, tempDirectory, remainingSharedPageResolutionDepth - 1, resolvedPageUris, downloadedKeys, cancellationToken).ConfigureAwait(false);
            }
            LogPlaylistUrlDownload("playlist_url_download skipped_unsupported source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " fileName=" + fileName + " contentType=" + GetContentTypeLogValue(response));
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        string downloadKey = CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri);
        if (!string.IsNullOrWhiteSpace(downloadKey) && downloadedKeys != null)
        {
            if (downloadedKeys.Contains(downloadKey))
            {
                LogPlaylistUrlDownload("playlist_url_download duplicate source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " key=" + downloadKey);
                return PlaylistUrlDownloadResult.Duplicate(downloadKey);
            }
        }
        string filePath = Path.Combine(tempDirectory, fileName);
        if (!await TryCopyStreamToFileWithLimitAsync(response.ResponseStream, filePath, DownloadAndInstallSizeLimitBytes, cancellationToken).ConfigureAwait(false))
        {
            LogPlaylistUrlDownload("playlist_url_download blocked_size_limit source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " file=" + fileName + " limitBytes=" + DownloadAndInstallSizeLimitBytes);
            return PlaylistUrlDownloadResult.BlockedBySizeLimit(downloadKey);
        }
        if (!string.IsNullOrWhiteSpace(downloadKey))
        {
            downloadedKeys?.Add(downloadKey);
        }
        LogPlaylistUrlDownload("playlist_url_download downloaded source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " key=" + (downloadKey ?? string.Empty) + " file=" + fileName + " contentType=" + GetContentTypeLogValue(response));
        return PlaylistUrlDownloadResult.Downloaded(filePath, downloadKey);
    }

    internal static string CreatePlaylistUrlDownloadKeyForTest(Uri uri)
    {
        return CreatePlaylistUrlDownloadKey(uri);
    }

    private static bool ShouldResolveSharedDownloadPageBeforeFileName(Uri requestedUri, AppHttpResponse response)
    {
        Uri responseUri = response?.ResponseUri;
        if (!IsSharedDownloadPageResolutionCandidate(requestedUri) && !IsSharedDownloadPageResolutionCandidate(responseUri))
        {
            return false;
        }
        return IsHtmlContentType(response)
            || IsDownloadSourcePageUri(requestedUri)
            || IsDownloadSourcePageUri(responseUri)
            || IsKnownDownloadLandingPageResponseUri(requestedUri)
            || IsKnownDownloadLandingPageResponseUri(responseUri);
    }

    private static bool IsKnownDownloadLandingPageResponseUri(Uri uri)
    {
        return IsMediaFireLandingPageUri(uri);
    }

    private static bool IsHtmlContentType(AppHttpResponse response)
    {
        string mediaType = response?.ContentHeaders?.ContentType?.MediaType;
        return mediaType != null
            && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetContentTypeLogValue(AppHttpResponse response)
    {
        return response?.ContentHeaders?.ContentType?.ToString() ?? string.Empty;
    }

    private static void LogPlaylistUrlDownload(string message)
    {
        NLogWrapper.FileLogger?.Info(message);
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static string SanitizePlaylistUrlDownloadLogValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static string CreatePlaylistUrlDownloadKey(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return string.Empty;
        }
        Uri normalizedUri = NormalizeDownloadUri(uri);
        string googleDriveFileId = ExtractGoogleDriveFileId(normalizedUri);
        if (!string.IsNullOrWhiteSpace(googleDriveFileId) && IsGoogleDriveHost(normalizedUri))
        {
            return "gdrive:" + googleDriveFileId;
        }
        string mediaFireFileId = ExtractMediaFireFileId(normalizedUri);
        if (!string.IsNullOrWhiteSpace(mediaFireFileId))
        {
            return "mediafire:" + mediaFireFileId;
        }
        var builder = new UriBuilder(normalizedUri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ExtractMediaFireFileId(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsExactHostOrSubdomain(uri.Host, "mediafire.com"))
        {
            return null;
        }
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[1]);
        }
        if (segments.Length >= 2 && (uri.Host ?? string.Empty).StartsWith("download", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[segments.Length - 2]);
        }
        return null;
    }

    /// <summary>
    /// URL をブラウザフォールバック扱いにするかどうかをテストから確認します。
    /// </summary>
    /// <param name="uri">判定対象の URL。</param>
    /// <returns>ブラウザで開くべき URL の場合は <see langword="true"/>。</returns>
    internal static bool IsBrowserFallbackDownloadUriForTest(Uri uri)
    {
        return IsBrowserFallbackDownloadUri(uri);
    }

    /// <summary>
    /// プレイリスト URL の共有サービス向け正規化結果をテストから確認します。
    /// </summary>
    /// <param name="uri">正規化対象の URL。</param>
    /// <returns>正規化後の URL。正規化できない場合は元の URL。</returns>
    internal static Uri NormalizeDownloadUriForTest(Uri uri)
    {
        return NormalizeDownloadUri(uri);
    }

    /// <summary>
    /// 共有ページ HTML から直接ダウンロード URL を解決する処理をテストから確認します。
    /// </summary>
    /// <param name="pageUri">共有ページの URL。</param>
    /// <param name="html">共有ページの HTML。</param>
    /// <returns>解決できた直接ダウンロード URL。解決できない場合は <see langword="null"/>。</returns>
    internal static Uri ResolveSharedDownloadPageUriForTest(Uri pageUri, string html)
    {
        return ResolveSharedDownloadPageUri(pageUri, html);
    }

    private static bool IsBrowserFallbackDownloadUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return true;
        }
        if (IsSharedDownloadPageResolutionCandidate(uri))
        {
            return false;
        }
        string urlText = uri.ToString();
        return urlText.EndsWith("/", StringComparison.OrdinalIgnoreCase)
            || urlText.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || urlText.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDownloadAndInstallCandidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        if (ChartFileKindResolver.IsSupportedChartFilePath(fileName))
        {
            return true;
        }
        return DownloadAndInstallArchiveExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDownloadUrlString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }
        string text = NormalizeDropboxDownloadUrlString(input);
        text = NormalizeGoogleDriveDownloadUrlString(text);
        text = odriveRegex.Replace(text, "https://onedrive.live.com/download?$1");
        return text;
    }

    private static string NormalizeDropboxDownloadUrlString(string input)
    {
        string text = dropBoxRegex.Replace(input, "https://dl.dropboxusercontent.com/$1/$2.$3");
        if (!string.Equals(text, input, StringComparison.Ordinal))
        {
            return text;
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
        {
            return input;
        }
        string host = uri.Host ?? string.Empty;
        if (!IsExactHostOrSubdomain(host, "dropbox.com")
            || !uri.AbsolutePath.StartsWith("/scl/fi/", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }
        return SetUriQueryParameter(uri, "dl", "1").ToString();
    }

    private static string NormalizeGoogleDriveDownloadUrlString(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
        {
            return gdriveRegex.Replace(input, "https://drive.usercontent.google.com/download?id=$2&export=download");
        }
        string host = uri.Host ?? string.Empty;
        if (host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            string fileId = ExtractGoogleDriveFileId(uri);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return BuildGoogleDriveDownloadUri(fileId).ToString();
            }
        }
        if (host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase))
        {
            string fileId = GetQueryParameter(uri, "id");
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return BuildGoogleDriveDownloadUri(fileId).ToString();
            }
        }
        if (host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return SetUriQueryParameter(uri, "export", "download").ToString();
        }
        return input;
    }

    private static Uri NormalizeDownloadUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return uri;
        }
        try
        {
            return new Uri(NormalizeDownloadUrlString(uri.ToString()), UriKind.Absolute);
        }
        catch
        {
            return uri;
        }
    }

    private static Uri BuildGoogleDriveDownloadUri(string fileId)
    {
        var builder = new UriBuilder("https://drive.usercontent.google.com/download");
        builder.Query = "id=" + Uri.EscapeDataString(fileId) + "&export=download";
        return builder.Uri;
    }

    private static string ExtractGoogleDriveFileId(Uri uri)
    {
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i].Equals("file", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[i + 2]);
            }
        }
        string fileId = GetQueryParameter(uri, "id");
        return string.IsNullOrWhiteSpace(fileId) ? null : fileId;
    }

    private static async Task<Uri> TryResolveSharedDownloadPageUriAsync(Uri requestedUri, AppHttpResponse response, CancellationToken cancellationToken = default)
    {
        if (!IsSharedDownloadPageResolutionCandidate(requestedUri) && !IsSharedDownloadPageResolutionCandidate(response?.ResponseUri))
        {
            return null;
        }
        if (response?.ContentLength > SharedDownloadPageResolverMaxBytes)
        {
            return null;
        }
        string html = await ReadTextWithLimitAsync(response?.ResponseStream, response?.ContentHeaders?.ContentType?.CharSet, SharedDownloadPageResolverMaxBytes, cancellationToken).ConfigureAwait(false);
        if (html == null)
        {
            return null;
        }
        Uri resolvedUri = ResolveSharedDownloadPageUri(response.ResponseUri ?? requestedUri, html);
        return resolvedUri != null && resolvedUri.IsAbsoluteUri && !IsSameUriWithoutFragment(resolvedUri, requestedUri) ? resolvedUri : null;
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return uri != null && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExactHostOrSubdomain(string host, string rootDomain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(rootDomain))
        {
            return false;
        }
        return host.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSharedDownloadPageResolutionCandidate(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            || IsManbowDownloadPageUri(uri)
            || IsVenueBmsSearchUri(uri)
            || IsBmsSearchInfoUri(uri)
            || host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || IsExactHostOrSubdomain(host, "mediafire.com");
    }

    private static Uri ResolveSharedDownloadPageUri(Uri pageUri, string html)
    {
        if (pageUri == null || string.IsNullOrWhiteSpace(html))
        {
            return null;
        }
        if (TryResolveGoogleDriveWarningPageUri(pageUri, html, out Uri googleDriveUri))
        {
            return googleDriveUri;
        }
        if (TryResolveMediaFireDownloadUri(pageUri, html, out Uri mediaFireUri))
        {
            return mediaFireUri;
        }
        if (TryResolveManbowDownloadUri(pageUri, html, out Uri manbowUri))
        {
            return manbowUri;
        }
        if (TryResolveBmsSearchDownloadUri(pageUri, html, out Uri bmsSearchUri))
        {
            return bmsSearchUri;
        }
        return null;
    }

    private static bool TryResolveGoogleDriveWarningPageUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsGoogleDriveHost(pageUri))
        {
            return false;
        }
        Match formMatch = Regex.Match(html, "<form\\b(?=[^>]*\\bid\\s*=\\s*[\"']download-form[\"'])[^>]*>(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!formMatch.Success)
        {
            return false;
        }
        Dictionary<string, string> formAttributes = ParseHtmlAttributes(formMatch.Value);
        if (formAttributes.TryGetValue("method", out string method) && !string.Equals(method, "get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!formAttributes.TryGetValue("action", out string action) || string.IsNullOrWhiteSpace(action))
        {
            action = pageUri.ToString();
        }
        if (!Uri.TryCreate(pageUri, action, out Uri actionUri))
        {
            return false;
        }
        if (!IsHttpOrHttps(actionUri) || !IsGoogleDriveHost(actionUri))
        {
            return false;
        }
        var queryParameters = ParseQueryParameters(actionUri.Query);
        foreach (Match inputMatch in Regex.Matches(formMatch.Groups["body"].Value, "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string inputTag = inputMatch.Value;
            Dictionary<string, string> inputAttributes = ParseHtmlAttributes(inputTag);
            if (!inputAttributes.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            string type = inputAttributes.TryGetValue("type", out string inputType) ? inputType : string.Empty;
            if (type.Equals("submit", StringComparison.OrdinalIgnoreCase)
                || type.Equals("button", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(inputTag, "\\sdisabled(?:\\s|=|>|/)", RegexOptions.IgnoreCase))
            {
                continue;
            }
            SetQueryParameter(queryParameters, name, inputAttributes.TryGetValue("value", out string value) ? value : string.Empty);
        }
        resolvedUri = BuildUriWithQuery(actionUri, queryParameters);
        return true;
    }

    private static bool TryResolveMediaFireDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (pageUri == null || !IsHttpOrHttps(pageUri) || !IsExactHostOrSubdomain(pageUri.Host, "mediafire.com"))
        {
            return false;
        }
        foreach (Match anchorMatch in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(anchorMatch.Value);
            if (!attributes.TryGetValue("href", out string href) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }
            attributes.TryGetValue("id", out string id);
            attributes.TryGetValue("class", out string className);
            if (!string.Equals(id, "downloadButton", StringComparison.OrdinalIgnoreCase)
                && !(className?.IndexOf("popsok", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                continue;
            }
            if (Uri.TryCreate(pageUri, WebUtility.HtmlDecode(href), out Uri candidate)
                && IsHttpOrHttps(candidate)
                && IsExactHostOrSubdomain(candidate.Host, "mediafire.com"))
            {
                resolvedUri = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveManbowDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsManbowDownloadPageUri(pageUri))
        {
            return false;
        }
        Match match = Regex.Match(html, "(?:Down\\s*Load|Download)Address.*?<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }
        Dictionary<string, string> attributes = ParseHtmlAttributes(match.Value);
        return attributes.TryGetValue("href", out string href) && TryCreateResolvableDownloadUri(pageUri, href, allowDownloadSourcePageUri: true, out resolvedUri);
    }

    private static bool TryResolveBmsSearchDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsVenueBmsSearchUri(pageUri) && !IsBmsSearchInfoUri(pageUri))
        {
            return false;
        }
        if (IsVenueBmsSearchUri(pageUri))
        {
            if (TryResolveSerializedVenueCoreDownloadUri(pageUri, html, out resolvedUri))
            {
                return true;
            }
            return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri: false, CollectSerializedVenueNonCoreDownloadKeys(pageUri, html), out resolvedUri);
        }
        if (TryResolveSerializedDownloadUri(pageUri, html, out resolvedUri))
        {
            return true;
        }
        return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri: false, out resolvedUri);
    }

    private static bool TryResolveAnchorDownloadUri(Uri pageUri, string html, bool allowDownloadSourcePageUri, out Uri resolvedUri)
    {
        return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri, null, out resolvedUri);
    }

    private static bool TryResolveAnchorDownloadUri(Uri pageUri, string html, bool allowDownloadSourcePageUri, HashSet<string> excludedDownloadKeys, out Uri resolvedUri)
    {
        resolvedUri = null;
        foreach (Match anchorMatch in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(anchorMatch.Value);
            if (attributes.TryGetValue("href", out string href) && TryCreateResolvableDownloadUri(pageUri, href, allowDownloadSourcePageUri, out resolvedUri))
            {
                string downloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(downloadKey) && excludedDownloadKeys?.Contains(downloadKey) == true)
                {
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveSerializedVenueCoreDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        foreach (SerializedVenueDownloadCandidate candidate in EnumerateSerializedVenueDownloadCandidates(html))
        {
            if (!candidate.IsCore)
            {
                continue;
            }
            if (TryCreateResolvableDownloadUri(pageUri, candidate.Url, allowDownloadSourcePageUri: false, out resolvedUri))
            {
                return true;
            }
        }
        return false;
    }

    private static HashSet<string> CollectSerializedVenueNonCoreDownloadKeys(Uri pageUri, string html)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SerializedVenueDownloadCandidate candidate in EnumerateSerializedVenueDownloadCandidates(html))
        {
            if (candidate.IsCore)
            {
                continue;
            }
            if (TryCreateResolvableDownloadUri(pageUri, candidate.Url, allowDownloadSourcePageUri: false, out Uri resolvedUri))
            {
                string downloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(downloadKey))
                {
                    keys.Add(downloadKey);
                }
            }
        }
        return keys;
    }

    private sealed class SerializedVenueDownloadCandidate
    {
        internal SerializedVenueDownloadCandidate(string url, bool isCore)
        {
            Url = url;
            IsCore = isCore;
        }

        internal string Url { get; }

        internal bool IsCore { get; }
    }

    private static List<SerializedVenueDownloadCandidate> EnumerateSerializedVenueDownloadCandidates(string html)
    {
        var candidates = new List<SerializedVenueDownloadCandidate>();
        string text = DecodeEmbeddedJsonText(html);
        foreach (Match objectMatch in Regex.Matches(text, "\\{(?<body>[^{}]{0,2048}\"downloadURL\"[^{}]{0,2048})\\}", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string body = objectMatch.Groups["body"].Value;
            Match urlMatch = Regex.Match(body, "\"downloadURL\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase);
            if (!urlMatch.Success)
            {
                continue;
            }
            bool isCore = Regex.IsMatch(body, "\"type\"\\s*:\\s*\"CORE\"", RegexOptions.IgnoreCase);
            candidates.Add(new SerializedVenueDownloadCandidate(urlMatch.Groups["url"].Value, isCore));
        }
        return candidates;
    }

    private static bool TryResolveSerializedDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        string text = DecodeEmbeddedJsonText(html);
        foreach (Match match in Regex.Matches(text, "\"downloadURL\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase))
        {
            if (TryCreateResolvableDownloadUri(pageUri, match.Groups["url"].Value, allowDownloadSourcePageUri: false, out resolvedUri))
            {
                return true;
            }
        }
        foreach (Match blockMatch in Regex.Matches(text, "\"downloads\"\\s*:\\s*\\[(?<body>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            foreach (Match urlMatch in Regex.Matches(blockMatch.Groups["body"].Value, "\"url\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase))
            {
                if (TryCreateResolvableDownloadUri(pageUri, urlMatch.Groups["url"].Value, allowDownloadSourcePageUri: false, out resolvedUri))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsGoogleDriveHost(Uri uri)
    {
        if (uri == null)
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManbowDownloadPageUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("manbow.nothing.sh", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.IndexOf("event.cgi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsVenueBmsSearchUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("venue.bmssearch.net", StringComparison.OrdinalIgnoreCase)
            && IsVenueBmsSearchDetailPath(uri.AbsolutePath);
    }

    private static bool IsBmsSearchInfoUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("bmssearch.net", StringComparison.OrdinalIgnoreCase)
            && (uri.AbsolutePath.Equals("/bmses", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/bmses/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVenueBmsSearchDetailPath(string absolutePath)
    {
        string[] segments = (absolutePath ?? string.Empty).Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && int.TryParse(segments[segments.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryCreateResolvableDownloadUri(Uri pageUri, string href, bool allowDownloadSourcePageUri, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (pageUri == null || string.IsNullOrWhiteSpace(href))
        {
            return false;
        }
        string decodedHref = WebUtility.HtmlDecode(href.Trim());
        if (!Uri.TryCreate(pageUri, decodedHref, out Uri candidate) || !IsHttpOrHttps(candidate))
        {
            return false;
        }
        Uri normalizedCandidate = NormalizeDownloadUri(candidate);
        if (!IsResolvableDownloadUri(normalizedCandidate, allowDownloadSourcePageUri))
        {
            return false;
        }
        resolvedUri = normalizedCandidate;
        return true;
    }

    private static bool IsResolvableDownloadUri(Uri uri, bool allowDownloadSourcePageUri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        if (IsDownloadAndInstallCandidateFileName(Path.GetFileName(uri.AbsolutePath)))
        {
            return true;
        }
        string host = uri.Host ?? string.Empty;
        if (host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (IsKnownDownloadLandingPageUri(uri))
        {
            return true;
        }
        return allowDownloadSourcePageUri && IsDownloadSourcePageUri(uri);
    }

    private static bool IsKnownDownloadLandingPageUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || IsExactHostOrSubdomain(host, "mediafire.com");
    }

    private static bool IsMediaFireLandingPageUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri) || !IsExactHostOrSubdomain(uri.Host, "mediafire.com"))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        return host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("mediafire.com", StringComparison.OrdinalIgnoreCase)
            || (segments.Length >= 1 && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDownloadSourcePageUri(Uri uri)
    {
        return IsManbowDownloadPageUri(uri)
            || IsVenueBmsSearchUri(uri)
            || IsBmsSearchInfoUri(uri);
    }

    private static string DecodeEmbeddedJsonText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string decoded = WebUtility.HtmlDecode(text);
        decoded = Regex.Replace(decoded, "\\\\u(?<hex>[0-9A-Fa-f]{4})", match =>
        {
            int value = int.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (value >= 0xD800 && value <= 0xDFFF)
            {
                return match.Value;
            }
            return char.ConvertFromUtf32(value);
        });
        return decoded.Replace("\\\"", "\"").Replace("\\/", "/");
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return attributes;
        }
        foreach (Match match in htmlAttributeRegex.Matches(tag))
        {
            string name = match.Groups["name"].Value;
            string value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                attributes[name] = WebUtility.HtmlDecode(value ?? string.Empty);
            }
        }
        return attributes;
    }

    private static async Task<string> ReadTextWithLimitAsync(Stream source, string charSet, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (source == null || maxBytes <= 0)
        {
            return null;
        }
        byte[] buffer = new byte[8192];
        using var memoryStream = new MemoryStream();
        int count;
        while ((count = await source.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)memoryStream.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await memoryStream.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            if (memoryStream.Length > maxBytes)
            {
                return null;
            }
        }
        try
        {
            Encoding encoding = string.IsNullOrWhiteSpace(charSet) ? Encoding.UTF8 : Encoding.GetEncoding(charSet.Trim('"'));
            return encoding.GetString(memoryStream.ToArray());
        }
        catch
        {
            return Encoding.UTF8.GetString(memoryStream.ToArray());
        }
    }

    private static string GetQueryParameter(Uri uri, string name)
    {
        if (uri == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        foreach (KeyValuePair<string, string> parameter in ParseQueryParameters(uri.Query))
        {
            if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }
        return null;
    }

    private static Uri SetUriQueryParameter(Uri uri, string name, string value)
    {
        var parameters = ParseQueryParameters(uri?.Query);
        SetQueryParameter(parameters, name, value);
        return BuildUriWithQuery(uri, parameters);
    }

    private static List<KeyValuePair<string, string>> ParseQueryParameters(string query)
    {
        var parameters = new List<KeyValuePair<string, string>>();
        string trimmedQuery = (query ?? string.Empty).TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return parameters;
        }
        foreach (string part in trimmedQuery.Split('&'))
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }
            int separatorIndex = part.IndexOf('=');
            string key = separatorIndex >= 0 ? part.Substring(0, separatorIndex) : part;
            string value = separatorIndex >= 0 ? part.Substring(separatorIndex + 1) : string.Empty;
            parameters.Add(new KeyValuePair<string, string>(Uri.UnescapeDataString(key.Replace("+", " ")), Uri.UnescapeDataString(value.Replace("+", " "))));
        }
        return parameters;
    }

    private static void SetQueryParameter(List<KeyValuePair<string, string>> parameters, string name, string value)
    {
        if (parameters == null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        parameters.RemoveAll(parameter => string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase));
        parameters.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
    }

    private static Uri BuildUriWithQuery(Uri uri, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        if (uri == null)
        {
            return null;
        }
        var builder = new UriBuilder(uri);
        builder.Query = string.Join("&", (parameters ?? []).Select(parameter => Uri.EscapeDataString(parameter.Key ?? string.Empty) + "=" + Uri.EscapeDataString(parameter.Value ?? string.Empty)));
        return builder.Uri;
    }

    private static bool IsSameUriWithoutFragment(Uri first, Uri second)
    {
        if (first == null || second == null)
        {
            return false;
        }
        var firstBuilder = new UriBuilder(first) { Fragment = string.Empty };
        var secondBuilder = new UriBuilder(second) { Fragment = string.Empty };
        return string.Equals(firstBuilder.Uri.ToString(), secondBuilder.Uri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddUriWithoutFragment(HashSet<string> uriSet, Uri uri)
    {
        if (uriSet == null || uri == null)
        {
            return false;
        }
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return uriSet.Add(builder.Uri.ToString());
    }

    private static async Task<bool> TryCopyStreamToFileWithLimitAsync(Stream source, string destinationPath, long maxBytes, CancellationToken cancellationToken = default)
    {
        const int bufferSize = 81920;
        byte[] array = new byte[bufferSize];
        long totalBytes = 0L;
        bool completed = false;
        try
        {
            using FileStream fileStream = LongPathFileSystem.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            int count;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = await source.ReadAsync(array, 0, array.Length, cancellationToken).ConfigureAwait(false);
                if (count <= 0)
                {
                    break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += count;
                if (totalBytes > maxBytes)
                {
                    return false;
                }
                await fileStream.WriteAsync(array, 0, count, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            return true;
        }
        finally
        {
            if (totalBytes > maxBytes || (!completed && cancellationToken.IsCancellationRequested))
            {
                try
                {
                    if (LongPathFileSystem.FileExists(destinationPath))
                    {
                        LongPathFileSystem.DeleteFile(destinationPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static string ResolveDownloadedArchiveFileName(Uri requestedUri, AppHttpResponse response)
    {
        if (TryResolveRawContentDispositionFileName(response?.ContentHeaders, out string rawFileName))
        {
            return rawFileName;
        }
        ContentDispositionHeaderValue contentDisposition = response?.ContentHeaders?.ContentDisposition;
        string fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return NormalizeDownloadedFileName(fileName);
        }
        if (response?.Headers?.Location != null)
        {
            return GetFileNameFromUri(response.Headers.Location);
        }
        if (Path.GetFileName(requestedUri.ToString()).Contains('?') || Path.GetFileName(requestedUri.ToString()).Contains('='))
        {
            return GetFileNameFromUri(response.ResponseUri);
        }
        return GetFileNameFromUri(requestedUri);
    }

    internal static string ResolveContentDispositionFileNameForTest(string contentDisposition)
    {
        return TryResolveContentDispositionFileName(contentDisposition, out string fileName) ? fileName : null;
    }

    private static bool TryResolveRawContentDispositionFileName(HttpContentHeaders headers, out string fileName)
    {
        fileName = null;
        if (headers == null || !headers.TryGetValues("Content-Disposition", out IEnumerable<string> contentDispositionValues))
        {
            return false;
        }
        foreach (string contentDisposition in contentDispositionValues)
        {
            if (TryResolveContentDispositionFileName(contentDisposition, out fileName))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveContentDispositionFileName(string contentDisposition, out string fileName)
    {
        fileName = null;
        if (string.IsNullOrWhiteSpace(contentDisposition))
        {
            return false;
        }
        if (TryGetContentDispositionParameter(contentDisposition, "filename*", out string fileNameStar)
            && TryDecodeRfc5987Value(fileNameStar, out string decodedFileNameStar))
        {
            fileName = NormalizeDownloadedFileName(decodedFileNameStar);
            return !string.IsNullOrWhiteSpace(fileName);
        }
        if (TryGetContentDispositionParameter(contentDisposition, "filename", out string rawFileName))
        {
            fileName = NormalizeDownloadedFileName(RepairPossiblyMojibakeFileName(rawFileName));
            return !string.IsNullOrWhiteSpace(fileName);
        }
        return false;
    }

    private static bool TryGetContentDispositionParameter(string contentDisposition, string parameterName, out string value)
    {
        value = null;
        foreach (Match match in Regex.Matches(contentDisposition, "(?:^|;)\\s*(?<name>[^=;\\s]+)\\s*=\\s*(?:\"(?<quoted>(?:\\\\.|[^\"])*)\"|(?<bare>[^;]*))", RegexOptions.IgnoreCase))
        {
            if (!match.Groups["name"].Value.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
            value = value.Replace("\\\"", "\"").Trim();
            return true;
        }
        return false;
    }

    private static bool TryDecodeRfc5987Value(string value, out string decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        Match match = Regex.Match(value.Trim(), "^(?<charset>[^']*)'(?<language>[^']*)'(?<encoded>.*)$");
        if (!match.Success)
        {
            return false;
        }
        try
        {
            Encoding encoding = Encoding.GetEncoding(match.Groups["charset"].Value);
            decoded = DecodePercentEncodedBytes(match.Groups["encoded"].Value, encoding);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DecodePercentEncodedBytes(string value, Encoding encoding)
    {
        var bytes = new List<byte>();
        var builder = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && byte.TryParse(value.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedByte))
            {
                bytes.Add(parsedByte);
                i += 2;
                continue;
            }
            if (bytes.Count > 0)
            {
                builder.Append(encoding.GetString(bytes.ToArray()));
                bytes.Clear();
            }
            builder.Append(value[i]);
        }
        if (bytes.Count > 0)
        {
            builder.Append(encoding.GetString(bytes.ToArray()));
        }
        return builder.ToString();
    }

    private static string RepairPossiblyMojibakeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !LooksLikeLatin1Mojibake(fileName))
        {
            return fileName;
        }
        try
        {
            string repaired = Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-1").GetBytes(fileName));
            return ContainsJapaneseText(repaired) ? repaired : fileName;
        }
        catch
        {
            return fileName;
        }
    }

    private static bool LooksLikeLatin1Mojibake(string text)
    {
        return text.IndexOf('ã') >= 0
            || text.IndexOf('ä') >= 0
            || text.IndexOf('å') >= 0
            || text.IndexOf('æ') >= 0
            || text.IndexOf('ç') >= 0
            || text.IndexOf('è') >= 0
            || text.IndexOf('é') >= 0;
    }

    private static bool ContainsJapaneseText(string text)
    {
        return !string.IsNullOrEmpty(text) && text.Any(ch =>
            (ch >= '\u3040' && ch <= '\u30FF')
            || (ch >= '\u3400' && ch <= '\u9FFF'));
    }

    private static string NormalizeDownloadedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }
        string normalized = fileName.Trim().Trim('"');
        normalized = normalized.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = Path.GetFileName(normalized);
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '_');
        }
        return normalized;
    }

    private static string GetFileNameFromUri(Uri uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }
        return NormalizeDownloadedFileName(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)));
    }

    /// <summary>
    /// 取得された複数のBMSファイル（またはアーカイブのパス）の一覧をもとに、
    /// ViewModelのインストールモジュールを非同期で呼び出し、アプリケーションのデータベースやフォルダへ導入します。
    /// 導入件数が複数の場合は進捗表示付きのポップアップダイアログ (ProgressDialog) を表示します。
    /// </summary>
    /// <param name="filePaths">インストール対象の一連のファイルシステムパス群。</param>
    private async void installChartPackages(IEnumerable<string> filePaths)
    {
        var viewModel = base.DataContext as MainWindowViewModel;
        int progIdx = 0;
        int failNum = 0;
        List<string> installs = [.. filePaths];
        int total = installs.Count;
        if (total == 0)
        {
            return;
        }
        var cancelTokenSource = new CancellationTokenSource();
        Task task = Task.Run(delegate
        {
            viewModel.InstallChartPackages(installs, cancelTokenSource.Token, delegate (bool s)
            {
                progIdx++;
                if (!s)
                {
                    failNum++;
                }
            });
        }, cancelTokenSource.Token).Logging("MainWindow.installChartPackages");
        if (total == 1)
        {
            await task;
            return;
        }
        await RunProgressUntilTaskCompletesAsync(
            task,
            cancelTokenSource,
            BeMusicSeeker.Properties.Resources.Install,
            "",
            context => context.ReportWithCancellationCheck(100 * progIdx / total, "[{0}/{1}] {2}", Math.Min(progIdx + 1, total), total, installs[Math.Min(progIdx, total - 1)]));
    }

    private void cancelDropInstallQueueClick(object sender, RoutedEventArgs e)
    {
        if (playlistUrlBulkDownloadRunning)
        {
            CancelPlaylistUrlBulkDownload();
            return;
        }
        (base.DataContext as MainWindowViewModel)?.CancelDroppedInstallQueue();
    }

    private void cancelMaintenanceRescanClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CancelMaintenanceRescan();
    }

    private void retryLr2SongDbSyncClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.RetryLr2SongDbSync();
    }

    private void cancelLr2SongDbSyncClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CancelLr2SongDbSync();
    }

    private void cleanupLr2SongDbSyncStartupScanBlockersClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CleanupLr2SongDbSyncStartupScanBlockersAndRetry();
    }

    private void tableContextMenuItemUpdateRankingDataClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<string> hashes = GetSelectedGridHashTargets();
        if (hashes != null && hashes.Count != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.GetLR2IRCacheHashes(hashes);
            }).Logging("tableContextMenuItemUpdateRankingDataClick");
            e.Handled = true;
        }
    }

    private async void tableContextMenuItemRegisterBMSFileToScoreViwer(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ScoreViewerTarget> targets = GetSelectedGridScoreViewerTargets();
        if (targets.Count == 0)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        await RunScoreViewerRegistrationAsync(targets, openSingleViewerOnSuccess: targets.Count == 1, "tableContextMenuItemRegisterBMSFileToScoreViwer");
    }

    private void tableContextMenuItemForceFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
        if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
        {
            Task.Run(delegate
            {
                viewModel.ForceResourceHealthCheckCharts(request);
            }).Logging("tableContextMenuItemForceFileScanCheckSelectedCharts");
            e.Handled = true;
        }
    }

    private void tableContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        bool isInstalledLocationRepair = IsFullScanMainViewSection(GetCurrentMainViewOperationSection());
        ChartOperationCapabilities capability = isInstalledLocationRepair
            ? ChartOperationCapabilities.RepairInstalledLocation
            : ChartOperationCapabilities.UpdateInstallDestination;
        RepairInstalledLocationRequest repairRequest = null;
        if (isInstalledLocationRepair)
        {
            List<ChartOperationTarget> targets = GetSelectedChartTargets(capability);
            RepairInstalledLocationRequest.TryCreate(targets, out repairRequest);
        }
        PendingInstallDestinationClearRequest pendingInstallRequest = null;
        if (!isInstalledLocationRepair)
        {
            List<ChartOperationTarget> pendingTargets = GetSelectedChartTargets(capability, isPendingSection: true);
            PendingInstallDestinationClearRequest.TryCreate(pendingTargets, out pendingInstallRequest);
        }
        if ((repairRequest?.HasTargets == true) || (pendingInstallRequest?.HasTargets == true))
        {
            e.Handled = true;
            repairRequest?.MaterializeRepairEntries();
            pendingInstallRequest?.MaterializeLooseEntries();
            Task.Run(delegate
            {
                if (isInstalledLocationRepair)
                {
                    viewModel.ClearInstallDestinationForCharts(repairRequest);
                }
                else
                {
                    viewModel.ClearPendingInstallDestination(pendingInstallRequest);
                }
            }).Logging("tableContextMenuRemoveInstallDestinationClick");
        }
    }

    private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_search_correct_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            request.MaterializeRepairEntries();
            Task.Run(delegate
            {
                viewModel.SearchCorrectInstallationDirectoryCharts(request);
            }).Logging("tableContextMenuSearchCorrectInstallationDirectoryChartsClick");
            e.Handled = true;
        }
    }

    private void tableContextMenuFixInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_fix_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (!RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        if (!request.HasInstallDestination)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        else if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.FixInstallationDirectoryCharts(request);
            }).Logging("tableContextMenuFixInstallationDirectoryClick");
        }
    }

    private void tableContextMenuItemDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        List<BMSTableEntry> list2 = GetSelectedGridPlaylistEntries();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (list2.Count <= 0)
        {
            return;
        }
        foreach (IGrouping<BMSTable, BMSTableEntry> enGrp in from en in list2
                                                             group en by en.parent)
        {
            Task.Run(delegate
            {
                viewModel.DeleteBMSTableEntries(enGrp.AsEnumerable(), enGrp.Key);
            }).Logging("tableContextMenuItemDeleteEntryClick");
        }
    }

    private void tableContextMenuItemForceFileScanCheckAllCharts(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_full_scan_all_charts"))
        {
            e.Handled = true;
            return;
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_rescan_all_charts_confirm,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        viewModel?.StartRescanAllOwnedChartMaintenance();
        e.Handled = true;
    }

    private void tableContextMenuItemRemoveChartInfoParseFailureClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _) || !IsChartInfoParseErrorMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<string> md5s = GetSelectedChartInfoParseFailureMd5s();
        if (base.DataContext is not MainWindowViewModel viewModel || md5s.Count == 0)
        {
            return;
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_chart_info_parse_failure_record, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        Task.Run(delegate
        {
            viewModel.RemoveChartInfoParseFailuresByMd5(md5s);
        }).Logging("tableContextMenuItemRemoveChartInfoParseFailureClick");
    }

    private void tableContextMenuItemAutoRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_auto_rename_folder"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request))
        {
            Task.Run(delegate
            {
                try
                {
                    viewModel.AutoRenameChartFolders(request);
                }
                finally
                {
                    RefreshCustomTableViewDisplayAsync();
                }
            }).Logging("tableContextMenuItemAutoRenameFolderClick");
        }
    }

    private void tableContextMenuItemRenameBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_rename_invalid_extension"))
        {
            e.Handled = true;
            return;
        }
        List<ChartFile> charts = GetSelectedBmsFormatCharts(ChartOperationCapabilities.RenameInvalidExtension);
        var viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = IsPendingMainViewSection(GetCurrentMainViewOperationSection());
        if (charts.Count <= 0 || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        Task.Run(delegate
        {
            List<ChartFile> list = [.. charts.Where(chart => (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
            List<ChartFile> list2 = [.. charts.Where(chart => (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
            if (list.Count > 0)
            {
                if (isPendingSelected)
                {
                    viewModel.RenamePendingBmsFormatChartFileExtensions(list, ".bmx");
                }
                else
                {
                    viewModel.RenameBMSFilesExtensions(list, ".bmx");
                }
            }
            if (list2.Count > 0)
            {
                if (isPendingSelected)
                {
                    viewModel.RenamePendingBmsFormatChartFileExtensions(list2, ".pmx");
                }
                else
                {
                    viewModel.RenameBMSFilesExtensions(list2, ".pmx");
                }
            }
        }).Logging("tableContextMenuItemRenameBMSFileClick");
    }

    private void tableContextMenuItemRemoveBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_remove_chart"))
        {
            e.Handled = true;
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        List<ChartOperationTarget> selectedTargets = GetSelectedChartTargets(isPendingSelected);
        TryGetContextMenuChartTarget(sender, e.Source, out ChartOperationTarget contextTarget);
        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(selectedTargets, contextTarget, section);
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("delete_chart_request section=" + section
                + " contextScope=" + (resolution.ContextScope?.ToString() ?? "None")
                + " selected=" + resolution.SelectedInputCount
                + " fallback=" + resolution.UsedContextFallback
                + " route=" + resolution.Route.ToString().ToLowerInvariant()
                + " targetCount=" + resolution.Targets.Count
                + " droppedMixedScope=" + resolution.MixedScopeDroppedCount);
        }
        if (base.DataContext is not MainWindowViewModel viewModel || resolution.Targets.Count == 0)
        {
            return;
        }
        bool deleteContainingPackageFoldersWhenNoBms = false;
        if (resolution.Route == ChartDeleteRoute.Pending)
        {
            if (!ShowPendingDeleteConfirmDialog(out deleteContainingPackageFoldersWhenNoBms))
            {
                return;
            }
        }
        else if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            if (installPerformanceLoggingEnabled)
            {
                installPerformanceLogger.Info("delete_chart_confirm route=" + resolution.Route.ToString().ToLowerInvariant() + " accepted=False");
            }
            return;
        }
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("delete_chart_confirm route=" + resolution.Route.ToString().ToLowerInvariant() + " accepted=True");
        }
        List<string> approvedWholeFolderDeletePaths = null;
        if (resolution.Route == ChartDeleteRoute.Library)
        {
            approvedWholeFolderDeletePaths = [];
            foreach (string folderPath in viewModel.GetLibraryWholeFolderDeleteConfirmationPaths(resolution.Targets))
            {
                bool approved = UiDialogRoute.ShowMessageBox(
                    Window.GetWindow(this),
                    string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                    BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes) == MessageBoxResult.Yes;
                if (installPerformanceLoggingEnabled)
                {
                    installPerformanceLogger.Info("delete_chart_folder_confirm path=" + folderPath + " accepted=" + approved);
                }
                if (approved)
                {
                    approvedWholeFolderDeletePaths.Add(folderPath);
                }
            }
        }
        Task.Run(delegate
        {
            if (resolution.Route == ChartDeleteRoute.Pending)
            {
                viewModel.RemovePendingCharts(resolution.Targets, sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: deleteContainingPackageFoldersWhenNoBms);
            }
            else
            {
                viewModel.RemoveLibraryCharts(resolution.Targets, approvedWholeFolderDeletePaths);
            }
        }).Logging("tableContextMenuItemRemoveBMSFileClick");
    }

    private bool ShowPendingDeleteConfirmDialog(out bool deleteContainingPackageFoldersWhenNoBms)
    {
        UiWindowDialogResult<bool> dialogResult = new UiDialogCoordinator()
            .ShowWindowAsync(new UiWindowDialogRequest<PendingDeleteConfirmDialog, bool>(
                () => new PendingDeleteConfirmDialog(),
                dialog => dialog.DeleteFolderWhenNoBmsChecked,
                this))
            .GetAwaiter()
            .GetResult();
        ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Pending delete confirmation dialog");
        deleteContainingPackageFoldersWhenNoBms = dialogResult.Value;
        return dialogResult.IsAccepted;
    }

    private async void tableContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_move_chart"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = [.. GetSelectedChartTargets().Where(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart?.Path))];
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && menuItem.DataContext is string dstDir && targets.Count > 0 && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_other_root, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            if (!ChartLibraryMoveRequest.TryCreate(targets, dstDir, out ChartLibraryMoveRequest request))
            {
                return;
            }
            await Task.Run(delegate
            {
                viewModel.MoveLibraryCharts(request);
            }).Logging("tableContextMenuItemMoveFileClick");
        }
    }

    private void fixEncodingSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && TryGetContextMenuRow(e.Source, out _))
        {
            List<BMSFile> list = GetSelectedBmsFiles(ChartOperationCapabilities.RunBmsEncodingFix);
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).FixEncodingBMSFiles(list, menuItem.Tag.ToString());
                e.Handled = true;
            }
        }
    }

    private void ignoreFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                viewModel.SetChartResourceWarningsIgnored(request);
                e.Handled = true;
            }
        }
    }

    private void notIgnoredFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                viewModel.SetChartResourceWarningsIgnored(request, unset: true);
                e.Handled = true;
            }
        }
    }

    private async void forceInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ForceInstallSelectedPendingChartsAsync(e);
    }

    private async Task ForceInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_force_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateForceInstall(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        ClearMainGridSelection();
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "forceInstallSelectedPendingCharts");
        }
        await Task.Run(delegate
        {
            viewModel.InstallPendingCharts(request);
        }).Logging("forceInstallSelectedPendingCharts");
    }

    private static string GetManualInstallConfirmationMessage()
    {
        return Settings.Default.DeletePendingPackageSourceAfterInstall
            ? BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source
            : BeMusicSeeker.Properties.Resources.Msg_manual_installation;
    }

    private async void manualInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ManualInstallSelectedPendingChartsAsync(e);
    }

    private async Task ManualInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_manual_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateManualInstall(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (Settings.Default.ShowDiffBMSInstallConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK)
        {
            return;
        }
        ClearMainGridSelection();
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "manualInstallSelectedPendingCharts");
        }
        await Task.Run(delegate
        {
            viewModel.InstallPendingCharts(request);
        }).Logging("manualInstallSelectedPendingCharts");
    }

    private async void searchInstallDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchInstallDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchInstallDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.SearchPendingInstallDestination(request);
        }).Logging("searchInstallDestinationSelectedPendingCharts");
    }

    private async void tableContextMenuItemDeleteInstallPackagesClick(object sender, RoutedEventArgs e)
    {
        await DeleteInstallPackageRecordsFromContextMenuAsync(e);
    }

    private async Task DeleteInstallPackageRecordsFromContextMenuAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_delete_install_package_records"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        bool isInstalledSelected = IsInstalledMainViewSection(section);
        if (!isPendingSelected && !isInstalledSelected)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!TryCreateDeleteInstallPackageRecordsRequest(isPendingSelected, isInstalledSelected, out DeleteInstallPackageRecordsRequest request))
        {
            return;
        }
        string confirmationMessage = request.IsPending ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed;
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), confirmationMessage, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        NLogWrapper.FileLogger?.Info("table_delete_install_packages requested section=" + section + " treeSection=" + _currentTreeSelectionSection + " selectedRows=" + request.SelectedRowCount);
        e.Handled = true;
        ClearMainGridSelection();
        if (request.IsPending)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
            await Task.Run(delegate
            {
                viewModel.DeleteInstallPackageRecords(request);
            }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
            if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
            {
                await Task.Run(delegate
                {
                    viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
                }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
            }
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
        await Task.Run(delegate
        {
            viewModel.DeleteInstallPackageRecords(request);
        }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
        }
    }

    private bool TryCreateDeleteInstallPackageRecordsRequest(bool isPendingSelected, bool isInstalledSelected, out DeleteInstallPackageRecordsRequest request)
    {
        request = null;
        if (isPendingSelected)
        {
            List<ChartOperationTarget> selectedPendingTargets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
            if (selectedPendingTargets.Count == 0)
            {
                return false;
            }

            request = DeleteInstallPackageRecordsRequest.CreatePending(selectedPendingTargets);
            return true;
        }
        if (isInstalledSelected)
        {
            List<ChartOperationTarget> selectedInstalledTargets = GetSelectedChartTargets(ChartOperationCapabilities.None);
            if (selectedInstalledTargets.Count == 0)
            {
                return false;
            }

            request = DeleteInstallPackageRecordsRequest.CreateInstalled(selectedInstalledTargets);
            return true;
        }

        return false;
    }

    private async void searchMergeDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchMergeDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchMergeDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0 || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.SearchPendingInstallDestination(request);
        }).Logging("searchMergeDestinationSelectedPendingCharts");
    }

    private bool ConfirmMergeDestinationSearch()
    {
        return UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) == MessageBoxResult.OK;
    }

    private async void tableContextMenuItemConvertToAudioFileClick(object sender, RoutedEventArgs e)
    {
        BMSFile[] bmsFiles = [.. GetSelectedBmsFiles(ChartOperationCapabilities.ConvertToAudio).Where(f => LongPathFileSystem.FileExists(f.path))];
        if (bmsFiles.Length == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        var cancelTokenSource = new CancellationTokenSource();
        int progIdx = 0;
        int failNum = 0;
        UiFolderPickerResult folderResult = await new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Save_to,
            owner: this));
        ThrowIfPickerFailed(folderResult.Status, folderResult.Error, "Audio conversion output folder picker");
        if (folderResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        string saveDir = folderResult.FolderPath;
        Task task = Task.Run(delegate
        {
            viewModel.ConvertBMSToAudioFiles(bmsFiles, saveDir, cancelTokenSource.Token, delegate (bool s)
            {
                progIdx++;
                if (!s)
                {
                    failNum++;
                }
            });
        }, cancelTokenSource.Token).Logging("tableContextMenuItemConvertToAudioFileClick");
        await RunProgressUntilTaskCompletesAsync(
            task,
            cancelTokenSource,
            BeMusicSeeker.Properties.Resources.Converting,
            viewModel.settingDialog.EncoderNames[(int)Settings.Default.Encoder] + " - " + BeMusicSeeker.Properties.Resources.Sampling_rate + ":" + viewModel.settingDialog.PlayerSampleRateNames[Settings.Default.EncoderSampleRate] + " " + BeMusicSeeker.Properties.Resources.Sampling_format + ":" + viewModel.settingDialog.PlayerFormatNames[Settings.Default.EncoderFormat],
            context => context.ReportWithCancellationCheck(100 * (progIdx + 1) / (bmsFiles.Length + 1), "[{0}/{1}] {2}", Math.Min(progIdx + 1, bmsFiles.Length), bmsFiles.Length, bmsFiles[Math.Min(progIdx, bmsFiles.Length - 1)].path));
        UiDialogRoute.ShowMessageBox(Window.GetWindow(this), ((!cancelTokenSource.IsCancellationRequested) ? BeMusicSeeker.Properties.Resources.Msg_conversion_completed : BeMusicSeeker.Properties.Resources.Msg_conversion_stopped) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (progIdx - failNum) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + (bmsFiles.Length - progIdx + failNum), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, cancelTokenSource.IsCancellationRequested ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk, MessageBoxResult.OK);
    }

    private void playlistTableDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
        treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows)
            || !MainWindowViewModel.ArePlaylistDropCandidateRows(selectedRows))
        {
            return;
        }
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (viewModel == null || treeViewItem.DataContext is not BMSTable table || table.is_external_sync)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 == null)
        {
            return;
        }
        treeViewItem2.Background = Brushes.Transparent;
        string folderName;
        if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode))
        {
            folderName = null;
        }
        else
        {
            if (folderNode.IsSpecial)
            {
                return;
            }
            folderName = folderNode.FolderName;
        }
        e.Effects = DragDropEffects.Copy;
        Task.Run(delegate
        {
            viewModel.AddChartRowsToFolderBMSTable(selectedRows, table, folderName);
        }).Logging("playlistTableDrop");
    }

    private void playlistTableDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!MainWindowViewModel.ArePlaylistDropCandidateRows(selectedRows)
            || sender is not TreeViewItem { DataContext: BMSTable { is_external_sync: false } })
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 != null && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void playlistTableDragEnter(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!MainWindowViewModel.ArePlaylistDropCandidateRows(selectedRows))
        {
            return;
        }
        if (sender is not TreeViewItem tviTable)
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem == null || tviTable.DataContext is not BMSTable bMSTable)
        {
            return;
        }
        if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
        {
            e.Effects = DragDropEffects.Copy;
            treeViewItem.Background = SystemColors.HighlightBrush;
        }
        if (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out _))
        {
            var booleanAnimationUsingKeyFrames = new BooleanAnimationUsingKeyFrames();
            Storyboard.SetTargetProperty(booleanAnimationUsingKeyFrames, new PropertyPath(TreeViewItem.IsExpandedProperty));
            Storyboard.SetTarget(booleanAnimationUsingKeyFrames, tviTable);
            booleanAnimationUsingKeyFrames.KeyFrames.Add(new DiscreteBooleanKeyFrame(!tviTable.IsExpanded, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.0))));
            booleanAnimationUsingKeyFrames.FillBehavior = FillBehavior.Stop;
            booleanAnimationUsingKeyFrames.Completed += delegate
            {
                tviTable.IsExpanded = !tviTable.IsExpanded;
            };
            treeViewItemInstantStoryBoardPlaylistTable.Children.Add(booleanAnimationUsingKeyFrames);
            treeViewItemInstantStoryBoardPlaylistTable.Begin(this, isControllable: true);
        }
    }

    private static bool IsPlaylistDropCandidateDrag(IDataObject dataObject, out List<object> selectedRows)
    {
        selectedRows = null;
        return CustomTableDataTransfer.HasRowDragKind(dataObject, CustomTableRowDragKind.PlaylistDropCandidateRows)
            && CustomTableDataTransfer.TryGetSelectedRows(dataObject, out selectedRows);
    }

    private void playlistTableDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 != null && treeViewItem.DataContext is BMSTable bMSTable)
        {
            if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
            {
                treeViewItem2.Background = Brushes.Transparent;
            }
            if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out _))
            {
                treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
                treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
            }
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「次の曲へ (Next)」ボタンがクリックされた際のイベントハンドラ。
    /// リスト内で現在選択されている曲の次の曲を非同期で再生開始します。
    /// </summary>
    private async void gridBMSPlayerControlsNextButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.PlayNextBMSfile();
            }).Logging("gridBMSPlayerControlsNextButtonClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「前の曲へ (Previous)」ボタンがクリックされた際のイベントハンドラ。
    /// ダブルクリック時は前の曲へ移動し、シングルクリック時は現在の曲を最初から再生し直します。
    /// </summary>
    private async void gridBMSPlayerControlsPreviousButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        gridBMSPlayerControlsPreviousButtonClickTimer ??= new DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), DispatcherPriority.Background, gridBMSPlayerControlsPreviousButtonSingleClicked, Dispatcher.CurrentDispatcher);
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            await Task.Run(delegate
            {
                viewModel.PlayPreviousBMSfile();
            }).Logging("gridBMSPlayerControlsPreviousButtonClicked");
        }
        else
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Start();
        }
    }

    private async void gridBMSPlayerControlsPreviousButtonSingleClicked(object sender, EventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            await Task.Run(delegate
            {
                viewModel.RestartPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsPreviousButtonSingleClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「再生 (Play)」ボタンがクリックされた際のイベントハンドラ。
    /// 現在の再生状態が停止・一時停止であれば再生を再開または開始します。
    /// </summary>
    private async void gridBMSPlayerControlsPlayStartButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
            {
                NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
            }
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("gridBMSPlayerControlsPlayStartButtonClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「停止 (Stop)」ボタンがクリックされた際のイベントハンドラ。
    /// 実行中の再生プロセスを終了し、再生状態をクリアします。
    /// </summary>
    private async void gridBMSPlayerControlsPlayStopButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.PlayEndBMSFile(closeProcess: true);
            }).Logging("gridBMSPlayerControlsPlayStopButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastForwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastForwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        var viewModel = base.DataContext as MainWindowViewModel;
        await Task.Run(delegate
        {
            viewModel.FastForwardPlayingBMSfileEnd();
        }).Logging("gridBMSPlayerControlsFastForwardButtonReleased");
    }

    private async void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastForwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsShowInfoButtonClicked(object sender, RoutedEventArgs e)
    {
        if (Settings.Default.UsePlayeruBMplay)
        {
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                await Task.Run(delegate
                {
                    viewModel.uBMplayShowInfo();
                }).Logging("gridBMSPlayerControlsShowInfoButtonClicked");
            }
        }
        else
        {
            gridPlayngBmsInfo.Visibility = ((gridPlayngBmsInfo.Visibility != Visibility.Hidden) ? Visibility.Hidden : Visibility.Visible);
        }
    }

    private async void gridBMSPlayerControlsShowEffectButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayShowEffect();
            }).Logging("gridBMSPlayerControlsShowEffectButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsChangePlaysideButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayChangePlayside();
            }).Logging("gridBMSPlayerControlsChangePlaysideButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsIncreaseHighSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.uBMplayIncreaseHighSpeed();
            }).Logging("gridBMSPlayerControlsIncreaseHighSpeedButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsDecreaseHighSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.uBMplayDecreaseHighSpeed();
            }).Logging("gridBMSPlayerControlsDecreaseHighSpeedButtonClicked");
        }
    }

    private void showBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Visible;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (NowPanelState == MainWindowViewModel.PanelState.BMS_PLAYER)
        {
            showBMSPlayerPanel();
        }
        IntPtr handle;
        try
        {
            handle = new WindowInteropHelper(this).Handle;
        }
        catch
        {
            return;
        }
        if (!(handle == Win32API.GetForegroundWindow()))
        {
            return;
        }
        base.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)async delegate
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            for (int i = 1; i <= 10; i++)
            {
                if (_isClosingOrClosed)
                {
                    break;
                }
                NLogWrapper.DebuggerLogger?.Trace("try to set focus on custom table");
                IntPtr currentHandle;
                try
                {
                    currentHandle = new WindowInteropHelper(this).Handle;
                }
                catch
                {
                    break;
                }
                if (!(currentHandle == Win32API.GetForegroundWindow()))
                {
                    break;
                }
                Keyboard.Focus(customTableView);
                await Task.Delay(100);
            }
        });
    }

    private void showBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (webBrowser != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(webBrowser, UIElement.VisibilityProperty).ParentMultiBinding;
            webBrowser.Visibility = Visibility.Visible;
            webBrowser.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (NowPanelState == MainWindowViewModel.PanelState.MOVIE_PLAYER)
        {
            showBrowserPanel();
        }
    }

    private void collapseBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Collapsed;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    private void collapseBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (webBrowser != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(webBrowser, UIElement.VisibilityProperty).ParentMultiBinding;
            webBrowser.Visibility = Visibility.Collapsed;
            webBrowser.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    private void dialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement overlayDialog && (bool)e.NewValue)
        {
            activeOverlayDialog = overlayDialog;
        }
        if (!(bool)e.NewValue && (bool)e.OldValue)
        {
            if (ReferenceEquals(activeOverlayDialog, sender))
            {
                activeOverlayDialog = null;
            }
            switch (NowPanelState)
            {
                case MainWindowViewModel.PanelState.BMS_PLAYER:
                    showBMSPlayerPanel();
                    break;
                case MainWindowViewModel.PanelState.MOVIE_PLAYER:
                    showBrowserPanel();
                    break;
            }
            if (sender is PlaylistPropertyDialog)
            {
                BindingOperations.GetMultiBindingExpression(gridBMSPlayerControlsFolderPath, TextBlock.TextProperty).UpdateTarget();
            }
        }
    }

    public void gridBMSPlayerControlsRotatePanelStateButtonClicked()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            gridBMSPlayerControlsRotatePanelStateButtonClicked(null, null);
        }, DispatcherPriority.ContextIdle);
    }

    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        MainWindowViewModel.PanelState panelState = NowPanelState;
        do
        {
            panelState = (panelState.HasFlag(MainWindowViewModel.PanelState.BMS_PLAYER) ? ((panelState & ~MainWindowViewModel.PanelState.BMS_PLAYER) | MainWindowViewModel.PanelState.MOVIE_PLAYER) : ((!panelState.HasFlag(MainWindowViewModel.PanelState.MOVIE_PLAYER)) ? (panelState | MainWindowViewModel.PanelState.BMS_PLAYER) : (panelState & ~MainWindowViewModel.PanelState.MOVIE_PLAYER)));
        }
        while (!isPanelStateValid(panelState));
        NowPanelState = panelState;
    }

    private void gridBMSPlayerControlsRotatePanelStateButtonClicked2(object sender = null, RoutedEventArgs e = null)
    {
        NowPanelState ^= MainWindowViewModel.PanelState.TITLE_SMALL;
    }

    private bool isPanelStateValid(MainWindowViewModel.PanelState state)
    {
        if (state.HasFlag(MainWindowViewModel.PanelState.BMS_PLAYER))
        {
            if (windowsFormsHost == null || !windowsFormsHost.IsEnabled)
            {
                return false;
            }
            if (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && Settings.Default.UsePlayeruBMplay)
            {
                return false;
            }
            if (!Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && Settings.Default.UsePlayeruBMplay)
            {
                return true;
            }
            if (Settings.Default.UsePlayerLR2body)
            {
                return false;
            }
            if (Settings.Default.UsePlayerBMIIDXView)
            {
                return true;
            }
            return false;
        }
        if (state.HasFlag(MainWindowViewModel.PanelState.MOVIE_PLAYER) && (webBrowser == null || !webBrowser.IsEnabled || ((MainWindowViewModel)base.DataContext).BrowserHtml == null))
        {
            return false;
        }
        return true;
    }

    private void windowsFormsHostIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!isPanelStateValid(NowPanelState))
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked();
        }
    }

    private static void forbidNavigating(object s, NavigatingCancelEventArgs e)
    {
        e.Cancel = true;
    }

    private void webBrowserLoadCompleted(object sender, NavigationEventArgs e)
    {
        webBrowser.Navigating += forbidNavigating;
        NowPanelState = MainWindowViewModel.PanelState.MOVIE_PLAYER;
    }

    private TreeViewItem _lastSelectedTreeViewItem;
    private bool _isCrossTreeDeselecting = false;

    private void gridTreePane_TreeViewItemSelected(object sender, RoutedEventArgs e)
    {
        TreeViewItem tvi = e.OriginalSource as TreeViewItem ?? e.Source as TreeViewItem;
        if (tvi != null && tvi.IsSelected)
        {
            if (_lastSelectedTreeViewItem != null && _lastSelectedTreeViewItem != tvi)
            {
                _isCrossTreeDeselecting = true;
                _lastSelectedTreeViewItem.IsSelected = false;
                _isCrossTreeDeselecting = false;
            }
            _lastSelectedTreeViewItem = tvi;
            TreeSelectionSection previousSection = _currentTreeSelectionSection;
            _currentTreeSelectionSection = ResolveTreeSelectionSection(tvi);
            if (previousSection != _currentTreeSelectionSection)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("tree_selection_section_changed section=" + _currentTreeSelectionSection + " header=" + tvi.Header);
            }
        }
    }

    private void treeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView treeView)
        {
            e.Handled = true;
            if (e.NewValue == null && !_isCrossTreeDeselecting)
            {
                _currentTreeSelectionSection = TreeSelectionSection.None;
            }
            var viewModel = base.DataContext as MainWindowViewModel;
            bool isManualInteraction = treeView.IsKeyboardFocusWithin || treeView.IsMouseOver;
            if (!_isCrossTreeDeselecting && e.NewValue == null && e.OldValue != null && e.OldValue is BMSTable
                && (viewModel?.IsPlaylistUpdating ?? false) && !isManualInteraction)
            {
                if (!treeView.SelectTreeViewItemSearchedByDataContext(e.OldValue))
                {
                    var table = (BMSTable)e.OldValue;
                    bool restoredByHeader = treeView.SelectTreeViewItemSearchedByHeader(table.name);
                    NLogWrapper.FileLogger?.Info("playlist_selection_restore fallback_by_header=" + restoredByHeader + " table=" + table.name);
                }
            }
        }
    }

    private TreeSelectionSection ResolveTreeSelectionSection(TreeViewItem selectedTreeViewItem)
    {
        if (selectedTreeViewItem == null)
        {
            return TreeSelectionSection.None;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemInstallPending))
        {
            return TreeSelectionSection.InstallPending;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, newlyInstalledTreeViewItem))
        {
            return TreeSelectionSection.InstallInstalled;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlaylist))
        {
            return TreeSelectionSection.Playlist;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlayHistory))
        {
            return TreeSelectionSection.PlayHistory;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemFullScanCheck))
        {
            return TreeSelectionSection.FullScanCheck;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemChartInfoParseError))
        {
            return TreeSelectionSection.ChartInfoParseError;
        }
        return TreeSelectionSection.Other;
    }

    private static bool IsSameOrDescendantOf(TreeViewItem targetTreeViewItem, TreeViewItem ancestorTreeViewItem)
    {
        if (targetTreeViewItem == null || ancestorTreeViewItem == null)
        {
            return false;
        }
        DependencyObject current = targetTreeViewItem;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestorTreeViewItem))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void sliderPlayerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var slider = (Slider)sender;
        Point position = e.GetPosition(slider);
        double value = slider.Maximum * Math.Max(0.0, Math.Min(1.0, (position.X - 5.0) / (slider.ActualWidth - 10.0)));
        slider.Value = value;
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PLAY))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseMove");
        }
    }

    private async void sliderPlayerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseLeftButtonUp");
        }
    }

    private async void sliderPlayerMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseLeave");
        }
    }



}
