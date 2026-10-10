using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Livet.EventListeners;
using NLog;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Supplies the shell lifecycle state required before a library reload can run.
/// </summary>
internal interface IMainWindowInitializationStatePort
{
    /// <summary>
    /// Gets whether the shell has completed its library initialization boundary.
    /// </summary>
    bool IsInitializationCompleted { get; }
}

/// <summary>
/// BeMusicSeeker のメイン画面を制御する ViewModel です。
/// ライブラリ（BMSファイル群）やプレイリストの管理、各ビュー状態の維持、内蔵および外部BMSプレイヤー機能の連携のほか、
/// UI (MainWindow) とのデータバインディングやルーティングを担います。
/// </summary>
public partial class MainWindowViewModel : ViewModel,
    ISettingsDialogStatePort,
    IStartupLibraryApplicationPort,
    IMainWindowInitializationStatePort
{
    /// <summary>
    /// Gets status-bar progress presentation state owned by the composed progress hub.
    /// </summary>
    public OperationProgressHubViewModel ProgressHub { get; }

    internal ChartMutationActivityOwner ChartMutationActivity { get; private set; }

    /// <summary>
    /// Gets the composed package-install workflow that owns dropped and playlist-url ingress.
    /// </summary>
    internal PackageInstallWorkflowOwner PackageInstallWorkflow { get; private set; }

    /// <summary>
    /// Gets the composed all-owned maintenance rescan workflow.
    /// </summary>
    internal MaintenanceRescanWorkflowOwner MaintenanceRescanWorkflow { get; private set; }

    /// <summary>
    /// Gets the composed folder auto-rename workflow.
    /// </summary>
    internal FolderAutoRenameWorkflowOwner FolderAutoRenameWorkflow { get; private set; }

    internal ScoreViewerRegistrationWorkflowOwner ScoreViewerRegistration { get; private set; }

    internal ZeroNoteMaintenanceWorkflowOwner ZeroNoteMaintenance { get; private set; }

    internal PackageCatalogWorkflowOwner PackageCatalog { get; private set; }

    internal DuplicateMaintenanceWorkflowOwner DuplicateMaintenanceWorkflow { get; private set; }

    internal SelectedChartMutationWorkflowOwner SelectedChartMutations { get; private set; }

    internal SelectedChartExternalActionWorkflowOwner SelectedChartExternalActions { get; private set; }

    internal SelectedChartResourceHealthWorkflowOwner SelectedChartResourceHealth { get; private set; }

    internal ChartInfoParseFailureRemovalWorkflowOwner ChartInfoParseFailureRemoval { get; private set; }

    internal SelectedChartAudioConversionWorkflowOwner SelectedChartAudioConversion { get; private set; }

    internal Lr2SongDbSyncWorkflowOwner Lr2SongDbSyncWorkflow { get; private set; }

    /// <summary>
    /// Gets the file-diff reload owner that sequences library reload before LR2 song DB synchronization.
    /// </summary>
    internal FileDiffReloadWorkflowOwner FileDiffReloadWorkflow { get; private set; }

    internal RankingCacheDownloadWorkflowOwner RankingCacheDownloadWorkflow { get; private set; }

    internal PendingPackageWorkflowOwner PendingPackages { get; private set; }

    /// <summary>
    /// Gets the playlist-table reload owner that sequences table storage reload and external sync queueing.
    /// </summary>
    internal PlaylistTablesReloadWorkflowOwner PlaylistTablesReloadWorkflow { get; private set; }

    /// <summary>
    /// Gets the score-only reload owner for the library score-storage operation.
    /// </summary>

    /// <summary>
    /// Gets the one-shot startup update workflow owned by application composition.
    /// </summary>
    internal StartupUpdateWorkflowOwner StartupUpdateWorkflow { get; private set; }

    internal ElevatedProcessWarningWorkflowOwner ElevatedProcessWarningWorkflow { get; private set; }

    internal ShellActivationWorkflowOwner ShellActivationWorkflow { get; private set; }

    internal ShellShutdownWorkflowOwner ShellShutdownWorkflow { get; private set; }

    /// <summary>
    /// Gets playback adapter state and telemetry while chart-row traversal remains on the shell ViewModel.
    /// </summary>
    public PlaybackPanelViewModel PlaybackPanel { get; }

    /// <summary>
    /// Gets main chart-table binding state while refresh orchestration remains in the shell ViewModel.
    /// </summary>
    public MainChartListViewModel MainChartList { get; }

    /// <summary>
    /// Gets settings owned by the MainWindow view-host boundary.
    /// </summary>
    public IMainWindowViewSettingsStore ViewSettings { get; }

    /// <summary>
    /// Gets playlist detail/summary presentation state while shell workflows remain in the root ViewModel.
    /// </summary>
    public PlaylistWorkspaceViewModel PlaylistWorkspace { get; }

    /// <summary>
    /// Gets the chart-list filter input state owned by the chart-list feature.
    /// </summary>
    public ChartListFilterViewModel ChartFilters { get; }

    /// <summary>
    /// Gets the library-folder tree presentation owner.
    /// </summary>
    public LibraryFolderTreeViewModel LibraryFolderTree { get; }

    /// <summary>
    /// Gets the installed and pending package tree presentation owner.
    /// </summary>
    public InstallTreeViewModel InstallTree { get; }

    /// <summary>
    /// Gets the maintenance tree presentation owner.
    /// </summary>
    public MaintenanceTreeViewModel MaintenanceTree { get; }

    internal RegularChartListOwner RegularChartList => regularChartListOwner;

    public PlayHistoryWorkflowOwner PlayHistory { get; }

    /// <summary>
    /// 起動・リロード進捗の対象 operation 種別です。
    /// </summary>
    private enum UiRefreshChannel
    {
        None = 0,
        LibraryMainView = 1,
        LibraryFolderTree = 2,
        InstallTree = 4,
        PlaylistTree = 8,
        DuplicateTree = 16
    }

    private const UiRefreshChannel StartupBasicPresentationChannels =
        UiRefreshChannel.LibraryFolderTree
        | UiRefreshChannel.PlaylistTree;



    private bool initializationCompleted;

    public bool IsInitializationCompleted => initializationCompleted;

    private bool hasActiveLibraryProfile;

    public bool HasActiveLibraryProfile => hasActiveLibraryProfile;



    private BMSLibrary files;

    private BMSPlaylist tables;

    private event EventHandler libraryOperationAvailabilityChanged;

    private event Action<Lr2PlayHistorySchemaStatusSnapshot> lr2PlayHistorySchemaStatusChanged;

    Task<StartupInitializationResult> ISettingsDialogStatePort.InitializeLibraryAsync(LibraryFileMutationCapability capability,
        LeapYearFolderRepairApproval leapYearRepairApproval, Action<LeapYearFolderRepairNotification> repairNotificationObserver)
        => InitializeLibraryAsync(isNormalStartup: false, capability, leapYearRepairApproval, repairNotificationObserver);

    Task<LeapYearFolderRepairApproval> ISettingsDialogStatePort.PrepareLibraryInitializationAsync()
    {
        if (!SettingDialog.CheckValidation(out _)) { return Task.FromResult<LeapYearFolderRepairApproval>(null); }
        StartupSettingsSnapshot settings = GetStartupSettingsSnapshot();
        return startupLibraryInitializationWorkflowOwner.PrepareLeapYearFolderRepairAsync(
            settings.OperationModeLR2DB ? settings.LR2SongDBPath : applicationComposition.ApplicationPathSnapshot.StandaloneSongDbPath,
            FileDbMutationDialogs, () => IsRequiredInitializationShutdownRequested);
    }

    Task ISettingsDialogStatePort.PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification notification)
        => IsRequiredInitializationShutdownRequested ? Task.CompletedTask
            : startupLibraryInitializationWorkflowOwner.PresentLeapYearFolderRepairAsync(notification, FileDbMutationDialogs, () => IsRequiredInitializationShutdownRequested);

    Task<StartupInitializationResult> ISettingsDialogStatePort.ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
        => ReloadScoresOnlyUnderAdmissionAsync(capability);

    Task<StartupInitializationResult> ISettingsDialogStatePort.ReloadFileDiffAsync(LibraryFileMutationCapability capability)
        => ReloadFileDiffUnderAdmissionAsync(capability);

    Task ISettingsDialogStatePort.PresentLibraryDirectoryWarningAsync(LibraryDirectoryPreflightException failure)
        => PresentLibraryDirectoryWarningAsync(failure, LibraryDirectoryWarningPhase.Late, "Settings reload directory preflight warning");

    event EventHandler ISettingsDialogStatePort.LibraryOperationAvailabilityChanged
    {
        add => libraryOperationAvailabilityChanged += value;
        remove => libraryOperationAvailabilityChanged -= value;
    }

    event Action<Lr2PlayHistorySchemaStatusSnapshot> ISettingsDialogStatePort.Lr2PlayHistorySchemaStatusChanged
    {
        add => lr2PlayHistorySchemaStatusChanged += value;
        remove => lr2PlayHistorySchemaStatusChanged -= value;
    }

    private readonly ApplicationComposition applicationComposition;

    /// <summary>Provides the shared optional mutation-report dialog boundary to view terminals.</summary>
    internal IUiDialogService FileDbMutationDialogs => applicationComposition.FileDbMutationDialogs;

    private readonly PlayHistoryRuntimeEventReporter playHistoryRuntimeEventReporter;

    private readonly StartupLibraryConstructionOwner startupLibraryConstructionOwner;

    private readonly StartupLibraryInitializationWorkflowOwner startupLibraryInitializationWorkflowOwner;

    private readonly IStartupLibraryInitializationFailurePresenter startupLibraryInitializationFailurePresenter;

    private readonly IUiScheduler uiScheduler;

    private readonly IApplicationLifetimePort applicationLifetime;

    private readonly IMainWindowInitializationStatePort initializationStatePort;

    internal IExternalShellGateway ExternalShellGateway => applicationComposition.ExternalShellGateway;

    private void DispatchUiAction(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        if (action == null)
        {
            return;
        }

        if (uiScheduler.CanExecuteInline)
        {
            action();
            return;
        }

        uiScheduler.Schedule(action, priority);
    }

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    private StartupSettingsSnapshot GetStartupSettingsSnapshot()
    {
        return startupSettingsProvider()
            ?? throw new InvalidOperationException("Startup settings provider returned null.");
    }

    private async Task PresentLibraryDirectoryWarningAsync(
        LibraryDirectoryPreflightException failure,
        LibraryDirectoryWarningPhase phase,
        string routeName)
    {
        if (failure == null)
        {
            throw new ArgumentNullException(nameof(failure));
        }

        try
        {
            string message = LibraryDirectoryWarningFormatter.Format(failure, phase);
            UiDialogResult result = await FileDbMutationDialogs.ShowMessageAsync(new UiMessageRequest(
                message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK));
            UiDialogRoute.ThrowIfNotShown(result, routeName);
        }
        catch (Exception presentationException)
        {
            NLogWrapper.FileLogger?.Warn(
                presentationException,
                routeName + " failed for directory preflight failure: " + failure.Message);
        }
    }

    private StartupProgressVersionSnapshot CaptureStartupProgressVersionSnapshot()
    {
        return new StartupProgressVersionSnapshot
        {
            ScoreHydrationCompletedVersion = files?.ScoreHydrationCompletedVersion ?? 0,
            ScoreHydrationRequestedVersion = files?.ScoreHydrationRequestedVersion ?? 0,
            ChartInfoHydrationCompletedVersion = files?.ChartInfoHydrationCompletedVersion ?? 0,
            PlaylistEntriesHydrationCompletedVersion = tables?.PlaylistEntriesHydrationCompletedVersion ?? 0,
            LibraryDatabaseLoadCompletedVersion = files?.LibraryDatabaseLoadCompletedVersion ?? 0,
            LibraryFileEnumerationCompletedVersion = files?.LibraryFileEnumerationCompletedVersion ?? 0,
            LibraryFileDiffCompletedVersion = files?.LibraryFileDiffCompletedVersion ?? 0
        };
    }

    private void PrepareStartupProgressOperation(
        StartupProgressOperationKind operationKind,
        long operationToken)
    {
        ProgressHub?.ResetStartupBackgroundInitializationPresentation();
        files?.BeginLibraryInitializationProgressOperation(operationToken);
        lock (startupBackgroundTaskProgressSynchronization)
        {
            startupPostInitializationWarmupOwner?.Reset("startup_operation_reset");
            startupBackgroundTaskScheduler.Reset(
                false, operationToken);
            ProgressHub?.BeginBackgroundProgressGeneration(startupBackgroundTaskScheduler.CurrentGeneration);
            lock (startupInitializationCompletionLock)
            {
                startupInitializationCompleteStopwatch = operationKind == StartupProgressOperationKind.Startup
                    ? Stopwatch.StartNew()
                    : null;
                startupInitializationCompleteLogged = false;
                startupPostInitializationCompletionTracking = operationKind is StartupProgressOperationKind.Startup or StartupProgressOperationKind.FullReinitialize;
                startupPostInitializationCompletionLogged = false;
                startupPostInitializationWarmupScheduled = false;
                startupPostInitializationWarmupCompleted = false;
                startupCompletionContinuationToken = 0L;
            }
        }
    }

    private void DispatchStartupProgressPresentation(Action action)
    {
        DispatchUiAction(action);
    }

    private bool IsStartupCompletionTokenCurrent(long expectedOperationToken)
    {
        if (expectedOperationToken == 0L
            || startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(expectedOperationToken))
        {
            return true;
        }
        if (startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken() != 0L)
        {
            return false;
        }
        lock (startupInitializationCompletionLock)
        {
            return startupCompletionContinuationToken == expectedOperationToken;
        }
    }

    private void MarkNonStartupBackgroundSchedulingComplete()
    {
        startupBackgroundTaskScheduler.MarkRequiredInitializationSchedulingComplete();
        startupBackgroundTaskScheduler.MarkPostInitializationSchedulingComplete();
    }

    internal static bool IsCurrentStartupPostInitializationCallback(
        long operationToken,
        long schedulerGeneration,
        Func<long, bool> isOperationTokenCurrent,
        Func<long, bool> isSchedulerGenerationCurrent)
    {
        return isOperationTokenCurrent(operationToken)
            && isSchedulerGenerationCurrent(schedulerGeneration);
    }

    private void TryLogStartupInitializationComplete(long expectedOperationToken = 0L)
    {
        if (expectedOperationToken != 0L
            && !IsStartupCompletionTokenCurrent(expectedOperationToken))
        {
            return;
        }
        StartupProgressOperationKind currentOperationKind = startupProgressWorkflowOwner.CurrentOperationKind;
        bool startupCompletionContinuation = currentOperationKind == StartupProgressOperationKind.None
            && expectedOperationToken != 0L
            && IsStartupCompletionTokenCurrent(expectedOperationToken);
        if (currentOperationKind != StartupProgressOperationKind.Startup
            && !startupCompletionContinuation)
        {
            return;
        }
        bool completionAlreadyLogged;
        lock (startupInitializationCompletionLock)
        {
            completionAlreadyLogged = startupInitializationCompleteLogged;
        }
        if (completionAlreadyLogged)
        {
            return;
        }

        long elapsedMs;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                if (expectedOperationToken != 0L
                    && !IsStartupCompletionTokenCurrent(expectedOperationToken))
                {
                    return;
                }
                if (startupInitializationCompleteLogged || startupInitializationCompleteStopwatch == null)
                {
                    return;
                }
                if (!startupProgressWorkflowOwner.IsStartupInitializationRequiredProgressComplete(expectedOperationToken))
                {
                    return;
                }
                startupInitializationCompleteLogged = true;
                elapsedMs = startupInitializationCompleteStopwatch.ElapsedMilliseconds;
            }
        }
        if (expectedOperationToken != 0L
            && !IsStartupCompletionTokenCurrent(expectedOperationToken))
        {
            return;
        }
        LogUiSuppression("startup_initialization_complete elapsedMs=" + elapsedMs);
        LogUiSuppression(startupBackgroundTaskScheduler.BuildSummaryLog(elapsedMs));
    }

    private void TryLogStartupPostInitializationComplete()
    {
        if (ShellShutdownWorkflow?.IsShutdownRequested == true
            || !startupBackgroundTaskScheduler.IsStarted
            || !startupBackgroundTaskScheduler.IsPostInitializationSchedulingComplete)
        {
            return;
        }
        long operationToken;
        long schedulerGeneration;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                operationToken = startupCompletionContinuationToken;
                schedulerGeneration = startupBackgroundTaskScheduler.CurrentGeneration;
            }
        }
        if (!startupBackgroundTaskScheduler.IsFullyIdle)
        {
            return;
        }
        bool shouldLog;
        lock (startupInitializationCompletionLock)
        {
            shouldLog = startupPostInitializationCompletionTracking
                && startupPostInitializationWarmupScheduled
                && startupPostInitializationWarmupCompleted
                && !startupPostInitializationCompletionLogged;
            if (shouldLog)
            {
                startupPostInitializationCompletionLogged = true;
            }
        }
        if (!shouldLog)
        {
            return;
        }
        // worker の複合終端は待たせず、表示だけを通常の UI 排出へ渡します。
        // 行を消すときにも送出時の操作を検査し、新操作の親行を旧終端で消しません。
        DispatchStartupProgressPresentation(() =>
        {
            if (!IsCurrentStartupPostInitializationCallback(
                    operationToken,
                    schedulerGeneration,
                    IsStartupCompletionTokenCurrent,
                    startupBackgroundTaskScheduler.IsCurrentGeneration))
            {
                return;
            }
            ProgressHub?.CompleteStartupBackgroundInitializationPresentation();
        });
        LogUiSuppression("startup_post_initialization_maintenance_complete");
        if (Net10PerformanceLog.IsEnabled && startupPerformanceInteraction.InteractionId > 0L)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "post_initialization_maintenance_complete",
                "kind=post_initialization");
        }
    }

    private void CompleteStartupPostInitializationWarmup(
        StartupPostInitializationWarmupCompletion completion)
    {
        long operationToken = completion.Request.OperationToken;
        long schedulerGeneration = completion.Request.SchedulerGeneration;
        if (!IsCurrentStartupPostInitializationCallback(
                operationToken,
                schedulerGeneration,
                IsStartupCompletionTokenCurrent,
                startupBackgroundTaskScheduler.IsCurrentGeneration))
        {
            return;
        }
        lock (startupInitializationCompletionLock)
        {
            if (!IsCurrentStartupPostInitializationCallback(
                    operationToken,
                    schedulerGeneration,
                    IsStartupCompletionTokenCurrent,
                    startupBackgroundTaskScheduler.IsCurrentGeneration))
            {
                return;
            }
            startupPostInitializationWarmupCompleted = true;
        }
        LogMainViewBuild("post_startup_warmup terminal reason=" + completion.Request.Reason
            + " operationToken=" + operationToken
            + " schedulerGeneration=" + schedulerGeneration
            + " kind=" + completion.Kind
            + " failedStage=" + completion.FailedStage);
        TryLogStartupPostInitializationComplete();
    }

    private LR2Config lr2config;

    private PropertyChangedEventListener listenerForBMSLibrary;

    private readonly object lockThis = new();

    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;





    private UiRefreshChannel playlistPresentationRefreshApplyInFlight = UiRefreshChannel.None;

    private readonly object lockUiSuppression = new();

    private readonly object startupInitializationCompletionLock = new();

    private readonly object startupBackgroundTaskProgressSynchronization = new();

    private readonly StartupBackgroundTaskSchedulerOwner startupBackgroundTaskScheduler;

    private readonly StartupPostInitializationWarmupOwner startupPostInitializationWarmupOwner;

    private Stopwatch startupInitializationCompleteStopwatch;

    private bool startupInitializationCompleteLogged;

    private bool startupPostInitializationCompletionTracking;

    private bool startupPostInitializationCompletionLogged;

    private bool startupPostInitializationWarmupScheduled;

    private bool startupPostInitializationWarmupCompleted;



    private long startupCompletionContinuationToken;

    private Stopwatch startupReadyInstallStopwatch;

    private Stopwatch startupReadyOperableStopwatch;

    private PerformanceInteraction startupPerformanceInteraction;

    private bool startupReadyDataLogged;

    private bool startupReadyUiLogged;

    private bool startupReadyDataReached;

    private bool startupReadyUiReached;

    private bool startupReadyOperableReached;

    private string _WindowTitle = "BeMusicSeeker Unofficial Fork - ";

    private PlayHistoryWorkflowOwner playHistoryWorkflowOwner => PlayHistory;

    private readonly object playHistoryViewRequestLock = new();

    private readonly RegularChartListOwner regularChartListOwner;

    private readonly StartupProgressWorkflowOwner startupProgressWorkflowOwner;

    private MainViewUpdateMode treeViewFilterTypeSelected;

    private object treeViewFilterParameterSelected;

    public SettingsDialogViewModel SettingDialog { get; private set; }

    private static void LogUiSuppression(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogUiSuppressionWarning(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Warn(message);
        }
    }

    private static void LogInitStage(string stage, string scope)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("init_stage " + stage + " scope=" + scope);
        }
    }

    private static void LogMainViewBuild(string message)
    {
        if (installPerformanceLoggingEnabled && Net10PerformanceLog.IsEnabled)
        {
            string normalized = message ?? string.Empty;
            int separator = normalized.IndexOf(' ');
            string stage = separator > 0 ? normalized[..separator] : "main_view_diagnostic";
            string fields = separator > 0 ? normalized[(separator + 1)..] : normalized;
            var interaction = PerformanceInteraction.Start("main_view");
            Net10PerformanceLog.Write(interaction, stage, fields);
        }
    }

    private static void LogMainViewBuildWarning(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Warn(message);
        }
    }

    private static void ReportPlayHistoryRuntimeEvent(PlayHistoryRuntimeEvent runtimeEvent)
    {
        if (runtimeEvent.Exception != null)
        {
            NLogWrapper.FileLogger?.Warn(runtimeEvent.Exception, runtimeEvent.Message);
        }
        else if (runtimeEvent.IsWarning)
        {
            LogMainViewBuildWarning(runtimeEvent.Message);
        }
        else
        {
            LogMainViewBuild(runtimeEvent.Message);
        }
    }

    /// <summary>
    /// プレイリスト source build の診断ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>

    /// <summary>
    /// プレイリスト source からの view 適用ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistViewApply(string message)
    {
        LogMainViewBuild("playlist_view_apply " + message);
    }

    /// <summary>
    /// playlist source/view の所有権と世代遷移を診断ログへ出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistRetention(string message)
    {
        LogMainViewBuild(message);
    }

    private static void LogPlaylistSummaryBulkWarning(string message)
    {
        NLogWrapper.FileLogger?.Warn(message);
    }

    private static void LogExternalPlaylistImportWarning(Exception exception, string message)
    {
        NLogWrapper.FileLogger?.Warn(exception, message);
    }

    private static void LogExternalPlaylistImportInfo(string message)
    {
        NLogWrapper.FileLogger?.Info(message);
    }

    private static void LogBeatorajaTableUrlImportWarning(Exception exception, string message)
    {
        NLogWrapper.FileLogger?.Warn(exception, message);
    }

    private static void LogBeatorajaTableUrlImportInfo(string message)
    {
        NLogWrapper.FileLogger?.Info(message);
    }

    /// <summary>
    /// playlist build request / worker の制御ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistWorker(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist reload operation と cleanup の診断ログを出力します。
    /// </summary>
    private static void LogPlaylistReload(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist request の keyword filter を同値判定向けに正規化します。
    /// </summary>
    internal static string NormalizePlaylistKeywordFilter(string keywordFilter)
    {
        return PlaylistRequestFactory.NormalizeKeywordFilter(keywordFilter);
    }

    /// <summary>
    /// request identity に source rebuild 前の quiet window を適用するかを返します。
    /// </summary>
    private static bool ShouldUsePlaylistBuildCoalescingWindow(MainViewUpdateMode mode, MainViewUpdateMode requestedMode)
    {
        return IsPlaylistViewMode(mode) || requestedMode == MainViewUpdateMode.TreeViewFilterNotChanged;
    }

    /// <summary>
    /// playlist 系表示モードかどうかを判定します。
    /// </summary>
    /// <param name="mode">判定対象モード。</param>
    /// <returns>playlist 系であれば <see langword="true"/>。</returns>
    private static bool IsPlaylistViewMode(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.PlaylistFilterSelected || mode == MainViewUpdateMode.PlaylistNotOwnedFilterSelected;
    }

    /// <summary>
    /// 現在の更新要求がプレイリスト詳細ビューの再描画経路を使うべきかを判定します。
    /// </summary>
    /// <param name="mode">今回の更新モード。</param>
    /// <param name="currentTreeMode">現在選択中の tree モード。</param>
    /// <returns>プレイリスト詳細ビューの再描画経路を使う場合は <see langword="true"/>。</returns>
    private static bool IsPlaylistTreeActive(MainViewUpdateMode mode, MainViewUpdateMode currentTreeMode)
    {
        return ChartListRefreshCoordinator.IsPlaylistTreeActive(mode, currentTreeMode);
    }

    /// <summary>
    /// playlist 詳細表示の activation state を更新します。
    /// </summary>
    /// <param name="playlistDetailActive">playlist 詳細表示中かどうか。</param>
    private void UpdatePlaylistDetailActivation(bool playlistDetailActive)
    {
        if (!playlistDetailActive)
        {
            return;
        }
        if (!PlaylistWorkspace.IsPlaylistDetailViewActive)
        {
            PlaylistWorkspace.InitializePlaylistDetailSort(regularChartListOwner.CaptureSortParameters());
        }
        PlaylistWorkspace.InitializePlaylistDetailFilter(ChartFilters.CaptureSnapshot());
    }

    /// <summary>
    /// 現在の playlist open readiness snapshot を返します。
    /// </summary>
    private PlaylistOpenReadinessSnapshot CapturePlaylistOpenReadinessSnapshot()
    {
        bool playlistRefRunning = !PlaylistWorkspace.PlaylistReferenceApplyWorkflow.IsIdle;
        int playlistRefLastCompletedVersion = PlaylistWorkspace.PlaylistReferenceApplyWorkflow.LastCompletedVersion;
        BMSLibrary.ScoreRuntimeState scoreState = default;
        if (files != null)
        {
            scoreState = files.GetScoreRuntimeStateForDiagnostics();
        }
        PlaylistLibraryIndexReadinessSnapshot libraryIndexSnapshot = PlaylistWorkspace.CapturePlaylistLibraryIndexReadinessSnapshot();
        return new PlaylistOpenReadinessSnapshot
        {
            StartupReadyDataReached = startupReadyDataReached,
            StartupReadyUiReached = startupReadyUiReached,
            StartupReadyOperableReached = startupReadyOperableReached,
            PlaylistRefDeferredRunning = playlistRefRunning,
            PlaylistRefDeferredLastCompletedVersion = playlistRefLastCompletedVersion,
            PlaylistLibraryIndexState = libraryIndexSnapshot.State,
            PlaylistLibraryIndexBuildMs = libraryIndexSnapshot.BuildElapsedMs,
            ScoreSnapshotReady = scoreState.SnapshotReady,
            ScoreSnapshotVersion = scoreState.SnapshotVersion,
            ScoreHydrationRunning = scoreState.HydrationRunning,
            ScoreHydrationCompletedVersion = scoreState.HydrationCompletedVersion,
            RankingRefreshRunning = scoreState.RankingRefreshRunning,
            RankingRefreshCompletedVersion = scoreState.RankingRefreshCompletedVersion
        };
    }

    private async Task WaitForPlaylistReloadCleanupDispatcherIdleAsync()
    {
        if (!uiScheduler.IsAvailable)
        {
            return;
        }
        await uiScheduler.InvokeAsync(delegate
        {
        }, UiSchedulePriority.ContextIdle).ConfigureAwait(false);
        await uiScheduler.InvokeAsync(delegate
        {
        }, UiSchedulePriority.ApplicationIdle).ConfigureAwait(false);
    }

    private static void CollectPlaylistReloadCleanupGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private ChartListSortParameters CaptureActiveMainViewSortParameters()
    {
        return IsPlaylistDetailWorkflowActive
            ? PlaylistWorkspace.CapturePlaylistDetailSortParameters()
            : regularChartListOwner.CaptureSortParameters();
    }

    private bool IsPlaylistDetailWorkflowActive =>
        PlaylistWorkspace.IsPlaylistDetailViewActive && !PlaylistWorkspace.IsPlaylistSummaryMode;

    private static bool IsSameReferenceSequence<T>(List<T> left, List<T> right) where T : class
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
            {
                return false;
            }
        }
        return true;
    }

    private void BeginUiUpdateSuppression(UiRefreshChannel mask)
    {
        if (mask == UiRefreshChannel.None)
        {
            return;
        }
        int suppressDepth;
        UiRefreshChannel suppressedMask;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth == 0)
            {
                pendingUiRefreshMask = UiRefreshChannel.None;
            }
            suppressUiUpdateDepth++;
            suppressedUiRefreshMask |= mask;
            suppressDepth = suppressUiUpdateDepth;
            suppressedMask = suppressedUiRefreshMask;
        }
        LogUiSuppression("ui_suppress begin depth=" + suppressDepth + " mask=" + mask + " suppressed=" + suppressedMask);
    }


    private bool TrySuppress(UiRefreshChannel channel)
    {
        if (channel == UiRefreshChannel.None)
        {
            return false;
        }
        bool suppressed = false;
        bool pendingChanged = false;
        int suppressDepth = 0;
        UiRefreshChannel pendingMask = UiRefreshChannel.None;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth > 0 && (suppressedUiRefreshMask & channel) != 0)
            {
                suppressed = true;
                UiRefreshChannel uiRefreshChannel = pendingUiRefreshMask;
                pendingUiRefreshMask |= channel;
                pendingChanged = uiRefreshChannel != pendingUiRefreshMask;
                suppressDepth = suppressUiUpdateDepth;
                pendingMask = pendingUiRefreshMask;
            }
        }
        if (pendingChanged)
        {
            LogUiSuppression("ui_suppress pending depth=" + suppressDepth + " channel=" + channel + " pending=" + pendingMask);
        }
        return suppressed;
    }

    private bool IsPlaylistSummaryPresentationRefreshDeferred(out bool applyReservation)
        => TryReservePlaylistSummaryRefresh(out applyReservation);

    private bool IsPlaylistSummaryDataRefreshDeferred(out bool applyReservation)
        => TryReservePlaylistSummaryRefresh(out applyReservation);

    /// <summary>日常の抑止と同時表示適用だけを調停します。起動完了ログや別maskを待ちません。</summary>
    private bool TryReservePlaylistSummaryRefresh(out bool applyReservation)
    {
        lock (lockUiSuppression)
        {
            UiRefreshChannel mask = UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView;
            bool deferred = suppressUiUpdateDepth > 0 || (playlistPresentationRefreshApplyInFlight & mask) != 0;
            applyReservation = !deferred;
            if (applyReservation) { playlistPresentationRefreshApplyInFlight |= mask; }
            if (suppressUiUpdateDepth > 0) { pendingUiRefreshMask |= suppressedUiRefreshMask & mask; }
            return deferred;
        }
    }

    private void CompletePlaylistPresentationRefreshApply(UiRefreshChannel channel)
    {
        bool reservationCleared = false;
        try
        {
            while (true)
            {
                lock (startupBackgroundTaskProgressSynchronization)
                {
                    lock (startupInitializationCompletionLock)
                    {
                        lock (lockUiSuppression)
                        {
                            bool deferred = IsPlaylistSummaryRefreshDeferredNowUnsafe();
                            bool pending = PlaylistWorkspace.HasDeferredPlaylistSummaryRefresh();
                            if (deferred || !pending)
                            {
                                playlistPresentationRefreshApplyInFlight &= ~channel;
                                reservationCleared = true;
                                return;
                            }
                            // Keep the arbitration locks while draining.  A suppression
                            // or startup transition on another thread must not enter the
                            // gap between the decision and the actual deferred apply.
                            PlaylistWorkspace.DrainDeferredPlaylistSummaryRefresh(
                                dataRefreshRequired: false,
                                rebuildAsync: true);
                        }
                    }
                }
            }
        }
        finally
        {
            if (!reservationCleared)
            {
                lock (startupBackgroundTaskProgressSynchronization)
                {
                    lock (startupInitializationCompletionLock)
                    {
                        lock (lockUiSuppression)
                        {
                            playlistPresentationRefreshApplyInFlight &= ~channel;
                        }
                    }
                }
            }
        }
    }

    private void ApplyPlaylistSummaryDataRefreshAtomically(
        PlaylistWorkspaceViewModel workspace,
        bool rebuildAsync,
        out bool applyReservation)
    {
        applyReservation = false;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                lock (lockUiSuppression)
                {
                    bool deferred = IsPlaylistSummaryDataRefreshDeferred(out applyReservation);
                    workspace.ApplyPlaylistSummaryDataRefresh(deferred, rebuildAsync);
                }
            }
        }
    }

    private void ExecutePlaylistSummaryPresentationRefreshWithArbitration(
        PlaylistWorkspaceViewModel workspace,
        out bool applyReservation)
    {
        applyReservation = false;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                lock (lockUiSuppression)
                {
                    bool deferred = IsPlaylistSummaryPresentationRefreshDeferred(out applyReservation);
                    workspace.ApplyPlaylistSummaryPresentationRefresh(deferred);
                }
            }
        }
    }


    // 呼出元は表示適用の既存調停lockを保持します。
    private bool IsPlaylistSummaryRefreshDeferredNowUnsafe() => suppressUiUpdateDepth > 0;

    private bool IsPlaylistTreePresentationDeferred(string reason)
    {
        if (TrySuppress(UiRefreshChannel.PlaylistTree))
        {
            return true;
        }
        return TrySuppress(UiRefreshChannel.PlaylistTree);
    }

    private void PlaylistWorkspacePlaylistPresentationRefreshRequested(
        object sender,
        PlaylistPresentationRefreshRequestedEventArgs request)
    {
        if (sender is not PlaylistWorkspaceViewModel workspace)
        {
            throw new InvalidOperationException(
                "Playlist presentation refresh request sender is not the composed workspace.");
        }
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        switch (request.Kind)
        {
            case PlaylistPresentationRefreshKind.SummaryData:
                bool dataApplyReservation = false;
                try
                {
                    ApplyPlaylistSummaryDataRefreshAtomically(
                        workspace,
                        request.RebuildAsync,
                        out dataApplyReservation);
                }
                finally
                {
                    if (dataApplyReservation)
                    {
                        CompletePlaylistPresentationRefreshApply(UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView);
                    }
                }
                return;
            case PlaylistPresentationRefreshKind.SummaryPresentation:
                bool presentationApplyReservation = false;
                try
                {
                    ExecutePlaylistSummaryPresentationRefreshWithArbitration(
                        workspace,
                        out presentationApplyReservation);
                }
                finally
                {
                    if (presentationApplyReservation)
                    {
                        CompletePlaylistPresentationRefreshApply(UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView);
                    }
                }
                return;
            case PlaylistPresentationRefreshKind.Tree:
                workspace.ApplyPlaylistTreePresentationRefresh(
                    request.Reason,
                    IsPlaylistTreePresentationDeferred(request.Reason));
                return;
            case PlaylistPresentationRefreshKind.HydrationCompleted:
                if (!workspace.TryBeginPlaylistHydrationNotification(
                    request.HydrationSourceStore,
                    request.HydrationSourceTables,
                    request.HydrationNotificationGeneration,
                    request.HydrationCompletionReceipt))
                {
                    return;
                }
                if (!workspace.PublishPlaylistEntriesHydrationCompleted(
                    request.HydrationVersion,
                    request.HydrationSourceStore,
                    request.HydrationSourceTables,
                    request.HydrationNotificationGeneration,
                    request.HydrationCompletionReceipt))
                {
                    return;
                }
                bool hydrationPresentationApplied = workspace.ExecuteCurrentPlaylistHydrationNotification(
                    request.HydrationSourceStore,
                    request.HydrationSourceTables,
                    request.HydrationNotificationGeneration,
                    request.HydrationCompletionReceipt,
                    () =>
                    {
                        bool hydrationDeferred = IsPlaylistTreePresentationDeferred(request.Reason);
                        workspace.ApplyPlaylistEntriesHydrationCompleted(
                            request.HydrationVersion,
                            hydrationDeferred,
                            request.HydrationSourceStore,
                            request.HydrationSourceTables,
                            request.HydrationNotificationGeneration,
                            request.HydrationCompletionReceipt);
                    });
                if (!hydrationPresentationApplied)
                {
                    return;
                }
                return;
            default:
                throw new InvalidOperationException(
                    "Unknown playlist presentation refresh request kind: " + request.Kind);
        }
    }

    private void EndUiUpdateSuppression(bool scheduleFlush = true)
    {
        long operationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
        UiRefreshChannel uiRefreshChannel = UiRefreshChannel.None;
        bool playlistSummaryRefreshPending;
        int suppressDepth = 0;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth <= 0)
            {
                suppressUiUpdateDepth = 0;
                suppressedUiRefreshMask = UiRefreshChannel.None;
                pendingUiRefreshMask = UiRefreshChannel.None;
                return;
            }
            suppressUiUpdateDepth--;
            suppressDepth = suppressUiUpdateDepth;
            if (suppressUiUpdateDepth == 0)
            {
                uiRefreshChannel = pendingUiRefreshMask;
                pendingUiRefreshMask = UiRefreshChannel.None;
                suppressedUiRefreshMask = UiRefreshChannel.None;
            }
        }
        playlistSummaryRefreshPending = suppressDepth == 0
            && PlaylistWorkspace.HasDeferredPlaylistSummaryRefresh();
        LogUiSuppression("ui_suppress end depth=" + suppressDepth + " flush=" + uiRefreshChannel
            + " playlistSummaryRefreshPending=" + playlistSummaryRefreshPending);
        // 必須初期化は抑止のcleanupだけをここで終え、親受付解放後の実InvokeAsyncで画面へ反映する。
        if (!scheduleFlush) { return; }
        if (uiRefreshChannel == UiRefreshChannel.None && !playlistSummaryRefreshPending)
        {
            startupReadyInstallStopwatch = null;
            startupReadyOperableStopwatch = null;
            startupReadyDataLogged = false;
            startupReadyUiLogged = false;
        }
        if (uiRefreshChannel == UiRefreshChannel.None && !playlistSummaryRefreshPending)
        {
            return;
        }
        Action flush = delegate
        {
            if (!startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken))
            {
                LogUiSuppression("ui_suppress flush_skipped_stale token=" + operationToken + " mask=" + uiRefreshChannel
                    + " playlistSummaryRefreshPending=" + playlistSummaryRefreshPending);
                return;
            }
            if (uiRefreshChannel != UiRefreshChannel.None)
            {
                FlushPendingUiRefresh(uiRefreshChannel, operationToken);
            }
            else if (playlistSummaryRefreshPending)
            {
                PlaylistWorkspace.DrainDeferredPlaylistSummaryRefresh(
                    dataRefreshRequired: false,
                    rebuildAsync: true);
            }
        };
        if (!uiScheduler.IsAvailable && uiScheduler.CanExecuteInline)
        {
            flush();
        }
        else
        {
            uiScheduler.Schedule(flush);
        }
    }

    private void TryLogStartupReadyData(long operationToken)
    {
        if (startupReadyInstallStopwatch == null || startupReadyDataLogged)
        {
            return;
        }
        if (!startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_data elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        if (Net10PerformanceLog.IsEnabled && startupPerformanceInteraction.InteractionId > 0L)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "snapshot_query_projection",
                "checkpoint=data_ready elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        }
        startupReadyDataLogged = true;
    }

    private void TryLogStartupReadyUi(UiRefreshChannel mask)
    {
        TryLogStartupReadyUi(mask, startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyUi(UiRefreshChannel mask, long operationToken)
    {
        if (!IsStartupReadyUiMaskSatisfied(mask))
        {
            return;
        }
        if (startupReadyInstallStopwatch == null || startupReadyUiLogged)
        {
            return;
        }
        if (!startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        bool flag = (mask & UiRefreshChannel.PlaylistTree) != 0;
        LogUiSuppression("startup_ready_ui elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds + " playlistRefreshed=" + flag.ToString().ToLowerInvariant());
        if (Net10PerformanceLog.IsEnabled && startupPerformanceInteraction.InteractionId > 0L)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "ui_applied",
                "checkpoint=ready_ui elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds
                + " playlistRefreshed=" + flag.ToString().ToLowerInvariant());
        }
        startupReadyUiLogged = true;
    }

    private static bool IsStartupReadyUiMaskSatisfied(UiRefreshChannel mask)
    {
        return (mask & UiRefreshChannel.InstallTree) != 0;
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask)
    {
        TryLogStartupReadyInstall(mask, startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask, long operationToken)
    {
        if (!IsStartupReadyInstallMaskSatisfied(mask))
        {
            return;
        }
        if (startupReadyInstallStopwatch == null)
        {
            return;
        }
        if (!startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_install elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        startupReadyInstallStopwatch = null;
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
    }

    private static bool IsStartupReadyInstallMaskSatisfied(UiRefreshChannel mask)
    {
        return (mask & UiRefreshChannel.InstallTree) != 0;
    }

    private void TryLogStartupReadyOperable(long operationToken)
    {
        if (startupReadyOperableStopwatch == null || !initializationCompleted)
        {
            return;
        }
        if (!startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_operable elapsedMs=" + startupReadyOperableStopwatch.ElapsedMilliseconds);
        if (Net10PerformanceLog.IsEnabled && startupPerformanceInteraction.InteractionId > 0L)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "first_useful_visible",
                "checkpoint=startup_ready_operable elapsedMs=" + startupReadyOperableStopwatch.ElapsedMilliseconds);
        }
        startupReadyOperableStopwatch = null;
    }

    private Task RefreshLibraryMainViewForCurrentFilter()
    {
        if (RegularChartListOwner.IsMaintenanceNavigationMode(treeViewFilterTypeSelected))
        {
            regularChartListOwner.NavigateMaintenance(
                treeViewFilterTypeSelected,
                treeViewFilterParameterSelected,
                "refresh_current_filter");
            return Task.CompletedTask;
        }
        return RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
    }

    private void RefreshLibraryMainViewForDataDependency(MainViewDataDependency dependency, string reason)
    {
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        ChartListSortParameters sortParameters = CaptureActiveMainViewSortParameters();
        MainViewRefreshDecision decision = MainViewRefreshDecisionService.Build(
            treeViewFilterTypeSelected,
            regularChartListOwner.HasTreeFilter,
            filters.KeywordFilter,
            filters.ModeFilter,
            sortParameters?.ColumnsName,
            PlaylistWorkspace.IsPlaylistDetailViewActive,
            dependency,
            reason);
        LogMainViewBuild("main_view_refresh_decision reason=" + decision.Reason
            + " dependency=" + decision.Dependency
            + " sortDependency=" + decision.SortDependency
            + " action=" + decision.Action
            + " detail=" + decision.Detail
            + " mode=" + treeViewFilterTypeSelected
            + " sortColumn=" + (sortParameters?.ColumnsName ?? "(default_title)")
            + " keywordEmpty=" + string.IsNullOrWhiteSpace(filters.KeywordFilter).ToString().ToLowerInvariant()
            + " modeFilter=" + filters.ModeFilter
            + " folderFilterApplied=" + regularChartListOwner.HasTreeFilter.ToString().ToLowerInvariant()
            + " isPlaylistDetailView=" + PlaylistWorkspace.IsPlaylistDetailViewActive.ToString().ToLowerInvariant());
        if (!decision.ShouldRefresh)
        {
            if (decision.ShouldRefreshDisplay)
            {
                if (!TryRefreshMainViewDisplayForDataDependency(dependency))
                {
                    if (TrySuppress(UiRefreshChannel.LibraryMainView))
                    {
                        return;
                    }
                    RefreshLibraryMainViewForCurrentFilter();
                }
            }
            return;
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForCurrentFilter();
    }

    private bool TryRefreshMainViewDisplayForDataDependency(MainViewDataDependency dependency)
    {
        if (dependency == MainViewDataDependency.Warning
            || dependency == MainViewDataDependency.Maintenance)
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return true;
            }
        }
        if (MainChartList.Rows is not ChartListVirtualView virtualView)
        {
            return false;
        }
        if (dependency == MainViewDataDependency.Warning
            || dependency == MainViewDataDependency.Maintenance)
        {
            regularChartListOwner.PrepareResourceHealthIndexForView(
                files,
                treeViewFilterTypeSelected,
                dependency == MainViewDataDependency.Maintenance
                    ? "normal_library_maintenance_display"
                    : "normal_library_warning_display");
        }
        virtualView.ForEachRealizedRow(row => row.RefreshDisplayForDataDependency(dependency));
        MainChartList.RequestDisplayRefresh();
        return true;
    }

    private void RefreshChartInfoDependentViews()
    {
        MainChartList.RowProjection.CaptureVersions(files);
        MainViewDataDependency libraryDependency = MainViewDataDependency.ChartInfo;
        regularChartListOwner.ResetDerivedCaches();
        if (TrySuppress(UiRefreshChannel.PlaylistTree))
        {
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "chart_info_dependent_views");
        }
        else
        {
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "chart_info_dependent_views");
            PlaylistWorkspace.RequestPlaylistDetailReloadRefresh();
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForDataDependency(libraryDependency, "chart_info_dependent_views");
    }

    private void LibraryFolderTreeCacheRefreshRequested(
        object sender,
        LibraryFolderTreeRefreshRequestedEventArgs e)
    {
        if (TrySuppress(UiRefreshChannel.LibraryFolderTree))
        {
            return;
        }
        long activeOperationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
        bool startupOperationActive = startupProgressWorkflowOwner.IsOperationActive
            && startupProgressWorkflowOwner.CurrentOperationKind == StartupProgressOperationKind.Startup;
        PerformanceInteraction interaction = e.Interaction.InteractionId > 0L
            ? e.Interaction
            : startupOperationActive
                ? startupPerformanceInteraction
                : default;
        LibraryFolderTree.ScheduleDeferredRefresh(
            activeOperationToken,
            interaction);
    }

    private void LibraryFolderTreeDeferredRefreshCompleted(
        object sender,
        LibraryFolderTreeRefreshCompletedEventArgs e)
    {
        LogUiSuppression("library_folder_tree_ready operationToken=" + e.OperationToken);
    }

    private void InstallTreePresentationChanged(
        object sender,
        InstallTreePresentationChangedEventArgs e)
    {
        InstallTreePresentationSection sections = e?.Sections ?? InstallTreePresentationSection.None;
        if ((sections & InstallTreePresentationSection.Installed) != 0
            && treeViewFilterTypeSelected == MainViewUpdateMode.NewlyInstalledFolderSelected
            && !TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
        }
        if ((sections & InstallTreePresentationSection.Pending) != 0
            && treeViewFilterTypeSelected == MainViewUpdateMode.PendingInstallFolderSelected
            && !TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        InstallTree.ApplyPresentation(sections);
    }

    private void MaintenanceTreeDuplicatePresentationChanged(
        object sender,
        MaintenanceTreeDuplicatePresentationChangedEventArgs e)
    {
        RefreshDuplicatePresentationAfterGroupsChanged(e?.Reason);
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask)
    {
        FlushPendingUiRefresh(mask, startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask, long operationToken)
    {
        FlushPendingUiRefresh(mask, operationToken, logReadiness: true);
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask, long operationToken, bool logReadiness)
        => FlushRequiredUiRefreshAsync(mask, operationToken, logReadiness).ObserveFault("Library presentation refresh");

    /// <summary>今回のライブラリ、保留、表一覧と選択中詳細の実表示を直接待ちます。独立readerやwarmupのidleを追加しません。</summary>
    private async Task FlushRequiredUiRefreshAsync(UiRefreshChannel mask, long operationToken, bool logReadiness)
    {
        LogUiSuppression("ui_suppress flush mask=" + mask);
        UiRefreshChannel requestedMask = mask;
        var stopwatchTotal = Stopwatch.StartNew();
        long num = 0L;
        long num2 = 0L;
        long num3 = 0L;
        long num4 = 0L;
        long num5 = 0L;
        bool flag = false;
        if ((mask & UiRefreshChannel.InstallTree) != 0)
        {
            var stopwatch = Stopwatch.StartNew();
            InstallTree.ApplyPresentation(
                InstallTreePresentationSection.Installed | InstallTreePresentationSection.Pending);
            stopwatch.Stop();
            num = stopwatch.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.PlaylistTree) != 0)
        {
            var stopwatch2 = Stopwatch.StartNew();
            PlayHistory.RefreshDisplayTargetCatalog();
            PlaylistWorkspace.RefreshPlaylistTreePresentation();
            UpdateChartKeywordSearchContext();
            stopwatch2.Stop();
            num2 = stopwatch2.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.LibraryFolderTree) != 0)
        {
            flag = true;
        }
        if ((mask & UiRefreshChannel.DuplicateTree) != 0)
        {
            var stopwatch4 = Stopwatch.StartNew();
            MaintenanceTree.ApplyDuplicateGroupsPresentation();
            stopwatch4.Stop();
            num4 = stopwatch4.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.LibraryMainView) != 0)
        {
            var stopwatch5 = Stopwatch.StartNew();
            await RefreshLibraryMainViewForCurrentFilter();
            stopwatch5.Stop();
            num5 = stopwatch5.ElapsedMilliseconds;
        }
        if (num > 1000)
        {
            LogUiSuppressionWarning("ui_stall_install_tree elapsedMs=" + num + " mask=" + mask);
        }
        if (num2 > 1000)
        {
            LogUiSuppressionWarning("ui_stall_playlist_tree elapsedMs=" + num2 + " mask=" + mask);
        }
        if (num5 > 1000)
        {
            LogUiSuppressionWarning("ui_stall_main_view elapsedMs=" + num5 + " filter=" + treeViewFilterTypeSelected + " mask=" + mask);
        }
        stopwatchTotal.Stop();
        LogUiSuppression("ui_suppress flush_install_tree_ms=" + num + " flush_playlist_tree_ms=" + num2 + " flush_library_folder_tree_ms=" + num3 + " flush_duplicate_tree_ms=" + num4 + " flush_library_main_view_ms=" + num5 + " flush_total_ms=" + stopwatchTotal.ElapsedMilliseconds + " deferred_library_folder_tree=" + flag + " requested_mask=" + requestedMask + " flushed_mask=" + mask);
        bool playlistSummaryDataRefreshRequired = (mask & (UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView)) != 0;
        PlaylistWorkspace.DrainDeferredPlaylistSummaryRefresh(
            playlistSummaryDataRefreshRequired,
            rebuildAsync: true);
        if (logReadiness)
        {
            TryLogStartupReadyUi(mask, operationToken);
            TryLogStartupReadyInstall(mask, operationToken);
        }
        if (flag)
        {
            LibraryFolderTree.ScheduleDeferredRefresh(
                operationToken,
                startupProgressWorkflowOwner.IsOperationActive
                    && startupProgressWorkflowOwner.CurrentOperationKind == StartupProgressOperationKind.Startup
                    ? startupPerformanceInteraction
                    : default);
        }
        if (logReadiness)
        {
            TryLogStartupReadyOperable(operationToken);
        }
    }

    private void PackageCatalogMutationPhasePublished(
        object sender,
        PackageCatalogMutationPhaseEventArgs e)
    {
        switch (e.Phase)
        {
            case PackageCatalogMutationPhase.RefreshSuppressionStarted:
                BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
                break;
            case PackageCatalogMutationPhase.RefreshSuppressionEnded:
                EndUiUpdateSuppression();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e.Phase), e.Phase, "Unsupported package catalog mutation phase.");
        }
    }

    private void DuplicateMaintenanceWorkflowChanged(
        object sender,
        DuplicateMaintenanceWorkflowChangedEventArgs e)
    {
        switch (e)
        {
            case DuplicateMaintenanceRefreshSuppressionChangedEventArgs suppressionChanged:
                if (suppressionChanged.IsSuppressed)
                {
                    BeginUiUpdateSuppression(
                        UiRefreshChannel.LibraryMainView
                        | UiRefreshChannel.LibraryFolderTree
                        | UiRefreshChannel.InstallTree
                        | UiRefreshChannel.DuplicateTree);
                }
                else
                {
                    EndUiUpdateSuppression();
                }
                break;
            case DuplicateMaintenanceRefreshPriorityWindowChangedEventArgs priorityChanged:
                if (priorityChanged.IsActive)
                {
                    PlaylistWorkspace.BeginDuplicateRefreshPriorityWindow(priorityChanged.Reason);
                }
                else
                {
                    ReleaseDuplicateRefreshPriorityWindowAfterUiRefresh(
                        priorityChanged.Reason + "_ui_refresh_done");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(e),
                    e,
                    "Unsupported duplicate-maintenance workflow change.");
        }
    }

    private void SelectedChartMutationWorkflowChanged(
        object sender,
        SelectedChartMutationWorkflowChangedEventArgs e)
    {
        switch (e)
        {
            case SelectedChartMutationRefreshSuppressionChangedEventArgs suppressionChanged:
                if (!suppressionChanged.IsSuppressed)
                {
                    EndUiUpdateSuppression();
                    break;
                }
                UiRefreshChannel refreshMask = suppressionChanged.Scope switch
                {
                    SelectedChartMutationRefreshScope.Pending =>
                        UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree,
                    SelectedChartMutationRefreshScope.Library =>
                        UiRefreshChannel.LibraryMainView
                        | UiRefreshChannel.LibraryFolderTree
                        | UiRefreshChannel.InstallTree
                        | UiRefreshChannel.DuplicateTree,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(suppressionChanged.Scope),
                        suppressionChanged.Scope,
                        "Unsupported selected chart mutation refresh scope.")
                };
                BeginUiUpdateSuppression(refreshMask);
                break;
            case SelectedChartMutationAppliedEventArgs mutationApplied:
                if (mutationApplied.LibraryPathChanged)
                {
                    regularChartListOwner.QueueLatestNormalLibraryRefreshNotification("library_charts_changed");
                    InvalidateNormalLibrarySortKeysAfterPathMutation(
                        hasBmsPathMutation: true,
                        hasBmsonPathMutation: true);
                }
                if (mutationApplied.EncodingChanged)
                {
                    InvalidateNormalLibraryIdentitySortKeys(NormalLibraryBmsTitleChangedReason);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(e),
                    e,
                    "Unsupported selected chart mutation workflow change.");
        }
    }

    private void PendingPackageWorkflowChanged(
        object sender,
        PendingPackageWorkflowChangedEventArgs e)
    {
        switch (e)
        {
            case PendingPackageRefreshSuppressionChangedEventArgs suppressionChanged:
                if (!suppressionChanged.IsSuppressed)
                {
                    EndUiUpdateSuppression();
                    break;
                }
                UiRefreshChannel refreshMask = suppressionChanged.Scope switch
                {
                    PendingPackageRefreshScope.DestinationState =>
                        UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree,
                    PendingPackageRefreshScope.PackageMutation =>
                        UiRefreshChannel.LibraryMainView
                        | UiRefreshChannel.InstallTree
                        | UiRefreshChannel.LibraryFolderTree
                        | UiRefreshChannel.DuplicateTree,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(suppressionChanged.Scope),
                        suppressionChanged.Scope,
                        "Unsupported install-destination refresh scope.")
                };
                BeginUiUpdateSuppression(refreshMask);
                break;
            case PendingPackageMutationAppliedEventArgs mutationApplied:
                if (!TryDispatchPackageInstallUi(() => ApplyPendingPackageMutation(mutationApplied)))
                {
                    ReportPackageInstallWorkflowNotificationFailure(
                        new InvalidOperationException("Pending-package workflow UI publication was rejected."));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e, "Unsupported pending-package workflow change.");
        }
    }

    private void ApplyPendingPackageMutation(PendingPackageMutationAppliedEventArgs mutationApplied)
    {
        MainChartList.RowProjection.UpdateTransientStates(
            mutationApplied.ChangedCharts,
            forceInstallDestinationProjection: true);
        if (mutationApplied.InstallDestinationStateChanged)
        {
            InvalidateNormalLibrarySortDependency(
                MainViewDataDependency.InstallDestination,
                NormalLibraryInstallDestinationChangedReason);
        }
        if (mutationApplied.InstallDestinationStateChanged || mutationApplied.IdentitySortKeyChanged)
        {
            RefreshLibraryMainViewForDataDependency(
                mutationApplied.IdentitySortKeyChanged
                    ? MainViewDataDependency.IdentitySortKey
                    : MainViewDataDependency.InstallDestination,
                NormalLibraryInstallDestinationChangedReason);
        }
        else if (mutationApplied.DisplayStateChanged)
        {
            MainChartList.RequestDisplayRefresh();
        }
    }

    public string WindowTitle
    {
        get
        {
            return _WindowTitle;
        }
        set
        {
            if (!(_WindowTitle == value))
            {
                _WindowTitle = value;
                RaisePropertyChanged("WindowTitle");
            }
        }
    }

    private void RefreshNormalLibraryAfterSourceChanged(string reason)
    {
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree))
        {
            return;
        }
        if (RegularChartListOwner.IsMaintenanceNavigationMode(treeViewFilterTypeSelected))
        {
            MainViewUpdateMode mode = treeViewFilterTypeSelected;
            if (mode != MainViewUpdateMode.DuplicateFilterSelected)
            {
                regularChartListOwner.NavigateMaintenance(
                    mode,
                    reason: reason);
            }
            return;
        }
        RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
    }

    private void IncrementNormalLibrarySourceGenerationForBmsonSync(
        BmsonLibraryRowCacheSyncResult result,
        string reasonPrefix)
    {
        ConsumeNormalLibrarySourceChangeForBmsonSync(result, reasonPrefix, out _);
    }

    private bool ConsumeNormalLibrarySourceChangeForBmsonSync(
        BmsonLibraryRowCacheSyncResult result,
        string reasonPrefix,
        out bool sourceGenerationChanged)
    {
        sourceGenerationChanged = false;
        if (!result.SourceChanged)
        {
            return false;
        }

        if (regularChartListOwner.TryInvalidateSourceForOwnedCollectionVersion(
            files?.OwnedCollectionVersion ?? 0,
            out int cacheCount))
        {
            sourceGenerationChanged = true;
            LogNormalLibrarySortCacheInvalidation(
                "source",
                ResolveBmsonSourceGenerationReason(result, reasonPrefix),
                cacheCount);
            return true;
        }

        regularChartListOwner.InvalidateVirtualSourceRows();
        return true;
    }

    private void InvalidateNormalLibraryIdentitySortKeys(string reason)
    {
        bool clearSourceRows = ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChange(reason);
        int cacheCount = regularChartListOwner.InvalidateIdentitySortKeys(clearSourceRows);
        LogNormalLibrarySortCacheInvalidation("sort_key", reason, cacheCount);
    }

    private void InvalidateNormalLibraryReferenceTableSortKeys()
    {
        NotifyBmsonPlaylistReferenceDisplayChanged();
        InvalidateNormalLibrarySortDependency(
            MainViewDataDependency.ReferenceTables,
            NormalLibraryReferenceTablesChangedReason);
    }

    private void InvalidateNormalLibrarySortDependency(MainViewDataDependency dependency, string reason)
    {
        int removedCount = regularChartListOwner.InvalidateSortCacheByDependency(dependency, out int cacheCount);
        LogNormalLibrarySortCacheInvalidation("dependency", reason, cacheCount, dependency, removedCount);
    }

    private void InvalidateNormalLibrarySortKeysForBmsonSync(BmsonLibraryRowCacheSyncResult result, string reason = null)
    {
        if (!result.SortKeyChanged)
        {
            return;
        }

        InvalidateNormalLibraryIdentitySortKeys(result.SourceIdentityChanged
            ? NormalLibraryBmsonSourceIdentityChangedReason
            : (string.IsNullOrWhiteSpace(reason) ? NormalLibraryBmsonSortKeyChangedReason : reason));
    }

    private const string NormalLibraryBmsPathChangedReason = "bms_path_changed";

    private const string NormalLibraryBmsTitleChangedReason = "bms_title_changed";

    private const string NormalLibraryBmsonPathChangedReason = "bmson_path_changed";

    private const string NormalLibraryBmsonSortKeyChangedReason = "bmson_sort_key_changed";

    private const string NormalLibraryChartInfoDigestBackfilledReason = "chart_info_digest_backfilled";

    private const string NormalLibraryInstallDestinationChangedReason = "install_destination_changed";

    private const string NormalLibraryReferenceTablesChangedReason = "ref_tables_changed";

    private const string NormalLibraryMaintenanceChangedReason = "maintenance_changed";

    private const string NormalLibraryWarningChangedReason = "warning_changed";

    private static IReadOnlyList<string> GetNormalLibraryPathSortKeyInvalidationReasons(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        return MainViewRefreshDecisionService.GetPathSortKeyInvalidationReasons(hasBmsPathMutation, hasBmsonPathMutation);
    }

    private const string NormalLibraryBmsonSourceIdentityChangedReason = "bmson_source_identity_changed";

    private void InvalidateNormalLibrarySortKeysAfterPathMutation(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        IReadOnlyList<string> reasons = GetNormalLibraryPathSortKeyInvalidationReasons(hasBmsPathMutation, hasBmsonPathMutation);
        foreach (string reason in reasons)
        {
            if (string.Equals(reason, NormalLibraryBmsPathChangedReason, StringComparison.Ordinal))
            {
                InvalidateNormalLibraryIdentitySortKeys(reason);
            }
            else if (string.Equals(reason, NormalLibraryBmsonPathChangedReason, StringComparison.Ordinal))
            {
            }
        }
    }


    private void NotifyBmsonPlaylistReferenceDisplayChanged()
    {
        Action notify = delegate
        {
            if (MainChartList.Rows is ChartListVirtualView virtualView)
            {
                virtualView.ForEachRealizedRow(RaiseBmsonPlaylistReferenceDisplayChanged);
            }
            else
            {
                foreach (LibraryChartRow row in (MainChartList.Rows ?? new List<object>()).OfType<LibraryChartRow>())
                {
                    RaiseBmsonPlaylistReferenceDisplayChanged(row);
                }
            }
            foreach (LibraryChartRow row in regularChartListOwner.SnapshotRows())
            {
                RaiseBmsonPlaylistReferenceDisplayChanged(row);
            }
        };
        DispatchUiAction(notify);
    }

    private static void RaiseBmsonPlaylistReferenceDisplayChanged(LibraryChartRow row)
    {
        row?.RaisePlaylistReferenceDisplayChanged();
    }


    private static bool ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChange(string reason)
    {
        return MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange(reason);
    }

    private void LogNormalLibrarySortCacheInvalidation(string reason, string detail, int cacheCountBefore)
    {
        LogMainViewBuild("normal_library_sort_cache_invalidate reason=" + (reason ?? string.Empty)
            + " detail=" + (detail ?? string.Empty)
            + " cacheCountBefore=" + cacheCountBefore
            + " sourceGeneration=" + regularChartListOwner.SourceGeneration
            + " sortKeyGeneration=" + regularChartListOwner.SortKeyGeneration);
    }

    private void LogNormalLibrarySortCacheInvalidation(string reason, string detail, int cacheCountBefore, MainViewDataDependency dependency, int removedCount)
    {
        LogMainViewBuild("normal_library_sort_cache_invalidate reason=" + (reason ?? string.Empty)
            + " detail=" + (detail ?? string.Empty)
            + " dependency=" + dependency
            + " cacheCountBefore=" + cacheCountBefore
            + " removed=" + removedCount
            + " sourceGeneration=" + regularChartListOwner.SourceGeneration
            + " sortKeyGeneration=" + regularChartListOwner.SortKeyGeneration
            + " warningGeneration=" + regularChartListOwner.WarningGeneration
            + " installDestinationGeneration=" + regularChartListOwner.InstallDestinationGeneration
            + " maintenanceGeneration=" + regularChartListOwner.MaintenanceGeneration
            + " referenceTablesGeneration=" + regularChartListOwner.ReferenceTablesGeneration);
    }

    private void ScheduleStartupPostInitializationWarmup(string reason, long operationToken)
    {
        long schedulerGeneration;
        lock (startupInitializationCompletionLock)
        {
            if (!startupPostInitializationCompletionTracking
                || startupPostInitializationWarmupScheduled)
            {
                return;
            }
            startupPostInitializationWarmupScheduled = true;
            schedulerGeneration = startupBackgroundTaskScheduler.CurrentGeneration;
        }
        startupPostInitializationWarmupOwner.Schedule(
            new StartupPostInitializationWarmupRequest(
                reason,
                operationToken,
                schedulerGeneration));
    }

    private static void RunStartupPostInitializationWarmupStage(
        RegularChartListPrewarmLease lease,
        string reason,
        CancellationToken cancellationToken,
        Action<BMSLibrary, string> warmup)
    {
        cancellationToken.ThrowIfCancellationRequested();
        warmup(lease.Library, "post_startup_" + (reason ?? string.Empty));
        cancellationToken.ThrowIfCancellationRequested();
    }


    /// <summary>
    /// Creates chart playback-stop targets from package entries without materializing bmson compatibility adapters.
    /// </summary>
    /// <param name="packages">Packages whose entries may overlap the currently playing chart directory.</param>
    /// <returns>Charts that should be considered before package mutation.</returns>
    internal static List<ChartFile> CreatePackagePlaybackTargetSnapshot(IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null)];
    }

    private void SyncMainChartListSortPresentation()
    {
        bool isPlayHistory = MainChartList.CurrentOperationContext.OperationSection == MainViewOperationSection.PlayHistory;
        ChartListSortParameters sortParameters = isPlayHistory
            ? playHistoryWorkflowOwner.CaptureSortParameters(out _)
            : CaptureActiveMainViewSortParameters();
        MainChartList.SetSortPresentation(
            CreateMainChartListSortPresentation(sortParameters),
            isPlayHistory ? MainChartListSortTarget.PlayHistory : MainChartListSortTarget.Regular);
    }

    private static MainChartListSortPresentation CreateMainChartListSortPresentation(ChartListSortParameters sortParameters)
    {
        return sortParameters == null
            ? null
            : new MainChartListSortPresentation(sortParameters.ColumnsName, sortParameters.Direction);
    }

    /// <summary>
    /// 最新の一覧更新要求を識別するIDを返します。
    /// MainWindow 側の描画遅延計測ログを main_view_build と突き合わせるために使用します。
    /// </summary>
    public long LastMainViewBuildRequestId => MainChartList.LastCompletion.RequestId;

    /// <summary>
    /// 最新の main_view_build 完了時刻 (Stopwatch タイムスタンプ) を返します。
    /// </summary>
    public long LastMainViewBuildEndTimestamp => MainChartList.LastCompletion.EndTimestamp;

    /// <summary>
    /// 最新の main_view_build を実行したスレッドIDを返します。
    /// </summary>
    public int LastMainViewBuildThreadId => MainChartList.LastCompletion.ThreadId;

    /// <summary>
    /// 最新の main_view_build 実行時モードを int 値で返します。
    /// </summary>
    public int LastMainViewBuildMode => (int)MainChartList.LastCompletion.Mode;

    /// <summary>実必須UIの成功公開前と変更作業中だけ操作を抑止します。完了表示・通知・任意通信の寿命は含めません。</summary>
    public bool IsLibraryOperationInProgress
    {
        get
        {
            long operationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
            bool completionPublished;
            lock (startupInitializationCompletionLock)
            {
                completionPublished = operationToken != 0L && startupCompletionContinuationToken == operationToken;
            }
            return startupProgressWorkflowOwner.IsStartupUiInteractionBlocked
                || (startupProgressWorkflowOwner.IsOperationActive
                    && !completionPublished
                    && (!startupProgressWorkflowOwner.IsFailed || !startupProgressWorkflowOwner.IsRetryableFailure))
                || ChartMutationActivity.IsActive;
        }
    }

    private void ChartMutationActivityChanged(object sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(IsLibraryOperationInProgress));
        RaiseLibraryOperationAvailabilityChanged();
    }

    private void RaiseLibraryOperationAvailabilityChanged()
        => libraryOperationAvailabilityChanged?.Invoke(this, EventArgs.Empty);

    private void ChartFiltersModeFilterChanged(object sender, EventArgs e)
    {
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        if (filters.ModeFilter == ChartModeFilter.None)
        {
            return;
        }

        if (PlaylistWorkspace.TryRequestPlaylistDetailFilter(MainViewUpdateMode.ModeFilterUpdated, filters))
        {
            return;
        }
        RefreshChartRowsView(MainViewUpdateMode.ModeFilterUpdated);
    }

    private void ChartFiltersKeywordFilterChanged(object sender, EventArgs e)
    {
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        if (PlaylistWorkspace.TryRequestPlaylistDetailFilter(MainViewUpdateMode.KeywordFilterUpdated, filters))
        {
            return;
        }
        bool isPlayHistorySelected;
        lock (playHistoryViewRequestLock)
        {
            isPlayHistorySelected = treeViewFilterTypeSelected == MainViewUpdateMode.PlayHistorySelected;
        }
        if (isPlayHistorySelected)
        {
            playHistoryWorkflowOwner.UpdateKeywordIdentity(
                NormalizePlaylistKeywordFilter(filters.KeywordFilter),
                advanceRevision: true);
        }
        if (isPlayHistorySelected)
        {
            playHistoryWorkflowOwner.QueueKeywordFilterRefresh(
                NormalizePlaylistKeywordFilter(filters.KeywordFilter),
                advanceRevision: false);
        }
        else
        {
            RefreshChartRowsView(MainViewUpdateMode.KeywordFilterUpdated);
        }
    }

    private void SchedulePlayHistoryDisplayTargetCatalogRefresh(Action refresh)
    {
        if (!uiScheduler.IsAvailable)
        {
            if (uiScheduler.CanExecuteInline)
            {
                Task.Run(refresh).ObserveFault("QueuePlayHistoryDisplayTargetsRefresh");
            }
            else
            {
                PlayHistory.RejectDisplayTargetCatalogRefreshScheduling();
            }
            return;
        }
        IUiScheduledOperation operation = uiScheduler.Schedule(refresh, UiSchedulePriority.Background);
        if (!operation.IsAccepted)
        {
            PlayHistory.RejectDisplayTargetCatalogRefreshScheduling();
            throw new InvalidOperationException("The UI scheduler rejected the play-history display-target refresh.");
        }
        _ = operation.Completion.ContinueWith(
            completedOperation =>
            {
                if (completedOperation.IsFaulted)
                {
                    _ = completedOperation.Exception;
                }
                if (operation.IsAborted)
                {
                    PlayHistory.RejectDisplayTargetCatalogRefreshScheduling();
                }
            },
            TaskScheduler.Default);
    }

    private GridKeywordSearchContext GetCurrentChartKeywordSearchContext()
    {
        if (treeViewFilterTypeSelected == MainViewUpdateMode.PlayHistorySelected)
        {
            return GridKeywordSearchContext.PlayHistory;
        }
        return IsPlaylistViewMode(treeViewFilterTypeSelected)
            ? GridKeywordSearchContext.PlaylistDetail
            : GridKeywordSearchContext.ChartList;
    }

    private void UpdateChartKeywordSearchContext()
    {
        GridKeywordSearchContext context = GetCurrentChartKeywordSearchContext();
        IReadOnlyList<string> playlistNameCandidates = context == GridKeywordSearchContext.PlaylistSummary
            ? []
            : PlaylistWorkspace.GetPlaylistKeywordValueCandidates();
        ChartFilters.UpdateKeywordSearchContext(context, playlistNameCandidates);
    }

    public bool IS_WIN8OR10 => Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012);

    /// <summary>
    /// Initializes the shell ViewModel with its explicit application and startup-library boundaries.
    /// </summary>
    /// <param name="composition">The composition for the shell's other feature services.</param>
    /// <param name="startupLibraryFactory">The factory used for ordered startup library construction.</param>
    /// <param name="fileDiffReloadWorkflow">An optional typed reload owner for consumer-boundary tests; production composition creates the owner from the shell's actual reload delegate.</param>
    /// <param name="initializationStatePort">An optional lifecycle-state port for consumer-boundary tests; production composition uses this ViewModel's own state.</param>
    /// <param name="startupLibraryInitializationFailurePresenter">An optional failure-presentation boundary; production composition uses the shell dialog route.</param>
    internal MainWindowViewModel(
        ApplicationComposition composition,
        IStartupLibraryFactory startupLibraryFactory,
        FileDiffReloadWorkflowOwner fileDiffReloadWorkflow = null,
        IMainWindowInitializationStatePort initializationStatePort = null,
        IStartupLibraryInitializationFailurePresenter startupLibraryInitializationFailurePresenter = null)
    {
        if (composition == null)
        {
            throw new ArgumentNullException(nameof(composition));
        }
        if (startupLibraryFactory == null)
        {
            throw new ArgumentNullException(nameof(startupLibraryFactory));
        }
        applicationComposition = composition;
        chartFileOperations = composition.OperationAdmission;
        playHistoryRuntimeEventReporter = new PlayHistoryRuntimeEventReporter(ReportPlayHistoryRuntimeEvent);
        startupLibraryConstructionOwner = new StartupLibraryConstructionOwner(startupLibraryFactory);
        startupLibraryInitializationWorkflowOwner = new StartupLibraryInitializationWorkflowOwner(_semaphore);
        this.startupLibraryInitializationFailurePresenter = startupLibraryInitializationFailurePresenter
            ?? new DefaultStartupLibraryInitializationFailurePresenter();
        uiScheduler = composition.UiScheduler;
        applicationLifetime = composition.ApplicationLifetime;
        this.initializationStatePort = initializationStatePort ?? this;
        startupSettingsProvider = composition.StartupSettingsProvider;
        ViewSettings = composition.MainWindowViewSettingsStore;
        startupBackgroundTaskScheduler = new StartupBackgroundTaskSchedulerOwner(
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            LogUiSuppression,
            LogUiSuppressionWarning,
            LogShutdown,
            FormatTextForLog,
            (generation, revision) =>
            {
                TryLogStartupPostInitializationComplete();
            },
            startupBackgroundTaskProgressSynchronization);
        startupProgressWorkflowOwner = new StartupProgressWorkflowOwner(
            CaptureStartupProgressVersionSnapshot,
            PrepareStartupProgressOperation,
            DispatchStartupProgressPresentation,
            LogUiSuppression,
            TryLogStartupInitializationComplete,
            startupBackgroundTaskProgressSynchronization);
        treeViewFilterTypeSelected = GetStartupSettingsSnapshot().StartupSelectInstallPending
            ? MainViewUpdateMode.PendingInstallFolderSelected
            : MainViewUpdateMode.FolderFilterSelected;
        customFolderOutputSettingsProvider = composition.CustomFolderOutputSettingsProvider;
        playHistoryDisplaySettingsStore = composition.PlayHistoryDisplaySettingsStore;
        MainChartList = composition.CreateMainChartListViewModel(
            DispatchMainChartListPresentationAction,
            LogMainViewBuild);
        MainChartList.SetOperationContext(treeViewFilterTypeSelected);
        PlaylistWorkspace = composition.CreatePlaylistWorkspaceViewModel(
            DispatchMainChartListAction,
            MainChartList,
            () => tables,
            () => PlaylistWorkspace.PlaylistTreeTables,
            LogPlaylistViewApply,
            LogPlaylistRetention,
            paths =>
            {
                if (PackageInstallWorkflow == null)
                {
                    throw new InvalidOperationException("Package install workflow is not composed.");
                }
                return PackageInstallWorkflow.Enqueue(paths);
            },
            uri => ExternalShellGateway.Open(ExternalShellRequest.OpenUrl(uri.ToString())),
            LogExternalPlaylistImportWarning,
            LogExternalPlaylistImportInfo,
            LogBeatorajaTableUrlImportWarning,
            LogBeatorajaTableUrlImportInfo,
            () => files,
            () => lr2config,
            LogPlaylistSummaryBulkWarning,
            new ObservableCollection<BMSTable>(),
            (reason, work) => startupBackgroundTaskScheduler.Queue("playlist_library_index_prewarm", reason, null, work),
            () => startupReadyOperableReached,
            () => treeViewFilterTypeSelected,
            WaitForPlaylistReloadCleanupDispatcherIdleAsync,
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            CollectPlaylistReloadCleanupGarbage,
            LogPlaylistReload,
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message),
            (reason, work) => startupBackgroundTaskScheduler.Queue(
                "external_playlist_sync",
                reason,
                null,
                work,
                shutdownReason => { }),
            (reason, work) => startupBackgroundTaskScheduler.Queue(
                "playlist_ref_apply",
                reason,
                null,
                work,
                shutdownReason => PlaylistWorkspace.PlaylistReferenceApplyWorkflow.DiscardForShutdown(shutdownReason)),
            ApplyMainChartListPresentationActionAsync,
            () => uiScheduler.CanExecuteInline,
            () => PackageInstallWorkflow.WaitForIdleAsync());
        PlaylistWorkspace.ProgressRequestFactory = startupBackgroundTaskScheduler.CaptureProgressRequest;
        PlaylistWorkspace.RequestProgressReporter = startupBackgroundTaskScheduler.ReportRequestProgress;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.ProgressRequestFactory = startupBackgroundTaskScheduler.CaptureProgressRequest;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.RequestProgressReporter = startupBackgroundTaskScheduler.ReportRequestProgress;
        PlaylistWorkspace.ConfigureCatalogNotificationQueue(QueueMainChartListAction);
        PlaylistTablesReloadWorkflow = new PlaylistTablesReloadWorkflowOwner(
            request => tables.ReloadTablesAsync(exportBeatorajaBmt: false, request.Capability),
            request => PlaylistWorkspace.RunExternalPlaylistSyncAsync(
                request.Reason, request.FromReloadTables, request.PublishReferenceReceipt,
                request.OperationToken, request.Capability));
        PlaylistWorkspace.TreeSelectionActivated += PlaylistWorkspaceTreeSelectionActivated;
        PlaylistWorkspace.PlaylistLampNavigationRequested += PlaylistWorkspacePlaylistLampNavigationRequested;
        PlaylistWorkspace.PlaylistPresentationRefreshRequested += PlaylistWorkspacePlaylistPresentationRefreshRequested;
        PlaylistWorkspace.PlaylistDetailScoreSnapshotRefreshRequested += PlaylistWorkspacePlaylistDetailScoreSnapshotRefreshRequested;
        PlaylistWorkspace.PlaylistReferenceSortInvalidationRequested += PlaylistWorkspacePlaylistReferenceSortInvalidationRequested;
        PlaylistWorkspace.PlaylistDetailReloadRefreshRequested += PlaylistWorkspacePlaylistDetailReloadRefreshRequested;
        PlaylistWorkspace.PlaylistTablesPresentationChanged += PlaylistWorkspacePlaylistTablesPresentationChanged;
        PlaylistWorkspace.PlaylistKeywordValueCandidatesChanged += PlaylistWorkspacePlaylistKeywordValueCandidatesChanged;
        PlaylistWorkspace.PlaylistEntriesHydrationRequested += PlaylistWorkspacePlaylistEntriesHydrationRequested;
        PlaylistWorkspace.PlaylistEntriesHydrationCompleted += PlaylistWorkspacePlaylistEntriesHydrationCompleted;
        PlaylistWorkspace.PlaylistExternalSyncQueued += PlaylistWorkspacePlaylistExternalSyncQueued;
        PlaylistWorkspace.PlaylistExternalSyncCompleted += PlaylistWorkspacePlaylistExternalSyncCompleted;
        PlaylistWorkspace.PlaylistExternalSyncReferenceApplied += PlaylistWorkspacePlaylistExternalSyncReferenceApplied;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queued += PlaylistReferenceApplyWorkflowQueued;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Completed += PlaylistReferenceApplyWorkflowCompleted;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.PresentationRequested += PlaylistReferenceApplyWorkflowPresentationRequested;
        PlaylistWorkspace.PlaylistReferenceApplyWorkflow.ReferenceApplied += PlaylistReferenceApplyWorkflowReferenceApplied;
        MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
            MainChartList,
            PlaylistWorkspace,
            applicationComposition.CreateDefaultBmsPlayer,
            chartFileOperations,
            LogMainViewBuild,
            DispatchMainChartListAction,
             LogMainViewBuildWarning,
             TryDispatchPackageInstallUi,
             () => files,
             new UiDialogCoordinator(),
            startupProgressWorkflowOwner,
            ReportPackageInstallWorkflowNotificationFailure,
            (library, progress, cancellationToken, capability) => library.RescanAllOwnedChartMaintenance(progress, cancellationToken, capability),
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportMaintenanceRescanWorkflowNotificationFailure,
            ReportMaintenanceRescanWorkflowFailure,
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportFolderAutoRenameWorkflowNotificationFailure,
            ReportFolderAutoRenameWorkflowFailure,
            FileDbMutationDialogs,
            zeroNoteLibraryProvider: () => files,
            packageCatalogLibraryProvider: () => files,
            duplicateMaintenanceDialogService: new UiDialogCoordinator(),
            showDuplicateFileCheckConfirmProvider: () => GetStartupSettingsSnapshot().ShowDuplicateFileCheckConfirmMsg,
            duplicateMaintenanceLibraryProvider: () => files,
            selectedChartMutationDialogService: new UiDialogCoordinator(),
            selectedChartMutationLibraryProvider: () => files,
            selectedChartResourceHealthDialogService: new UiDialogCoordinator(),
            selectedChartResourceHealthLibraryProvider: () => files,
            maintenanceRescanDialogService: new UiDialogCoordinator(),
            chartInfoParseFailureRemovalDialogService: new UiDialogCoordinator(),
            chartInfoParseFailureRemovalLibraryProvider: () => files,
            selectedChartAudioConversionDialogService: new UiDialogCoordinator(),
            lr2SongDbSyncWorkflow: new Lr2SongDbSyncWorkflowOwner(
                new BmsLr2SongDbSyncWorkflowRuntime(
                    () => files,
                    () => tables,
                    () => GetStartupSettingsSnapshot().OperationModeLR2DB)),
            rankingCacheDownloadWorkflow: new RankingCacheDownloadWorkflowOwner(
                new BmsRankingCacheDownloadRuntime(() => files),
                new UiDialogCoordinator()),
            libraryFolderTreeLog: LogUiSuppression,
            libraryFolderTreeLogWarning: LogUiSuppressionWarning);
        ProgressHub = childComposition.ProgressHub;
        startupBackgroundTaskScheduler.ProgressChanged += status =>
            DispatchStartupProgressPresentation(() => ProgressHub.UpdateBackgroundTaskProgress(status));
        ChartMutationActivity = childComposition.ChartMutationActivity;
        ChartMutationActivity.ActivityChanged += ChartMutationActivityChanged;
        PlaybackPanel = childComposition.PlaybackPanel;
        MainChartList.RowProjection.SetPlaybackStatusProvider(PlaybackPanel.GetPlaybackStatus);
        PlaybackPanel.PlaybackStatusChanged += () => DispatchUiAction(MainChartList.RequestDisplayRefresh);
        ChartFilters = childComposition.ChartFilters;
        LibraryFolderTree = childComposition.LibraryFolderTree;
        LibraryFolderTree.ConfigureDeferredRefreshScheduler(
            (reason, work) => startupBackgroundTaskScheduler.Queue(
                "library_folder_tree_refresh",
                reason,
                null,
                work));
        LibraryFolderTree.CacheRefreshRequested += LibraryFolderTreeCacheRefreshRequested;
        LibraryFolderTree.DeferredRefreshCompleted += LibraryFolderTreeDeferredRefreshCompleted;
        InstallTree = childComposition.InstallTree;
        InstallTree.PresentationChanged += InstallTreePresentationChanged;
        MaintenanceTree = childComposition.MaintenanceTree;
        MaintenanceTree.DuplicatePresentationChanged += MaintenanceTreeDuplicatePresentationChanged;
        ChartFilters.ModeFilterChanged += ChartFiltersModeFilterChanged;
        ChartFilters.KeywordFilterChanged += ChartFiltersKeywordFilterChanged;
        UpdateChartKeywordSearchContext();
        PlayHistory = childComposition.PlayHistory;
        PackageInstallWorkflow = childComposition.PackageInstallWorkflow;
        PackageInstallWorkflow.CompletionPublished += PackageInstallWorkflowCompletionPublished;
        PackageInstallWorkflow.FailurePublished += PackageInstallWorkflowFailurePublished;
        PackageInstallWorkflow.RefreshSuppressionChanged += PackageInstallWorkflowRefreshSuppressionChanged;
        MaintenanceRescanWorkflow = childComposition.MaintenanceRescanWorkflow;
        MaintenanceRescanWorkflow.CompletionPublished += MaintenanceRescanWorkflowCompletionPublished;
        FolderAutoRenameWorkflow = childComposition.FolderAutoRenameWorkflow;
        FolderAutoRenameWorkflow.CompletionPublished += FolderAutoRenameWorkflowCompletionPublished;
        FolderAutoRenameWorkflow.FailurePublished += FolderAutoRenameWorkflowFailurePublished;
        FolderAutoRenameWorkflow.RefreshSuppressionChanged += FolderAutoRenameWorkflowRefreshSuppressionChanged;
        StartupUpdateWorkflow = childComposition.StartupUpdateWorkflow;
        ElevatedProcessWarningWorkflow = childComposition.ElevatedProcessWarningWorkflow;
        ShellActivationWorkflow = new ShellActivationWorkflowOwner(
            StartupUpdateWorkflow,
            ElevatedProcessWarningWorkflow,
            InitializeAsync);
        ScoreViewerRegistration = childComposition.ScoreViewerRegistrationWorkflow;
        ZeroNoteMaintenance = childComposition.ZeroNoteMaintenanceWorkflow;
        PackageCatalog = childComposition.PackageCatalogWorkflow;
        PackageCatalog.MutationPhasePublished += PackageCatalogMutationPhasePublished;
        DuplicateMaintenanceWorkflow = childComposition.DuplicateMaintenanceWorkflow;
        DuplicateMaintenanceWorkflow.WorkflowChanged += DuplicateMaintenanceWorkflowChanged;
        SelectedChartMutations = childComposition.SelectedChartMutations;
        SelectedChartMutations.WorkflowChanged += SelectedChartMutationWorkflowChanged;
        SelectedChartExternalActions = childComposition.SelectedChartExternalActions;
        SelectedChartResourceHealth = childComposition.SelectedChartResourceHealth;
        SelectedChartResourceHealth.RescanCompleted += SelectedChartResourceHealthRescanCompleted;
        ChartInfoParseFailureRemoval = childComposition.ChartInfoParseFailureRemoval;
        SelectedChartAudioConversion = childComposition.SelectedChartAudioConversion;
        Lr2SongDbSyncWorkflow = childComposition.Lr2SongDbSyncWorkflow;
        FileDiffReloadWorkflow = fileDiffReloadWorkflow ?? new FileDiffReloadWorkflowOwner(
            request => Task.Run(delegate
            {
                LogInitStage("file_diff_reload_call", "ReloadFileDiff");
                return files.ReloadFileDiff(request.Capability, request.WarningObserver);
            }),
            Lr2SongDbSyncWorkflow,
            _ => tables.ApplyRequiredLibraryReferencesAsync());
        RankingCacheDownloadWorkflow = childComposition.RankingCacheDownloadWorkflow;
        PendingPackages = childComposition.PendingPackageWorkflow;
        PendingPackages.WorkflowChanged += PendingPackageWorkflowChanged;
        PlayHistory.ConfigureDisplayTargetPersistence(identity => playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = identity);
        PlayHistory.ConfigureDisplayTargetCatalogRefresh(
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot,
            SchedulePlayHistoryDisplayTargetCatalogRefresh);
        PlayHistory.PeriodRequestActivated += PlayHistoryPeriodRequestActivated;
        PlayHistory.ConfigureViewRefreshScheduler(
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            request => RefreshChartRowsView(MainViewUpdateMode.KeywordFilterUpdated, request));
        PlayHistory.ConfigureDetailSourceRetirement(() =>
        {
            PlaylistSourceRetirementRequest retirement =
                PlaylistWorkspace.PrepareDetailSourceRetirementWithoutPublishing();
            PlaylistWorkspace.PublishDetailSourceRetirement(retirement);
            return retirement;
        });
        PlayHistory.ConfigureViewExecution(new PlayHistoryViewExecutionDependencies(
            () => files,
            () => tables,
            ResolvePlayHistoryReadSourceContext,
            InvokeMainChartListPresentationAction,
            (request, progress) => ReportPlayHistoryReadWorkflowProgress(
                request.PeriodRequest,
                request.RequestId,
                progress),
            () =>
            {
                lock (playHistoryViewRequestLock)
                {
                    return treeViewFilterTypeSelected;
                }
            },
            MainChartList,
            PlaylistWorkspace));
        PlayHistory.DisplayTargetRefreshRequested += (_, _) => PlayHistory.QueueDisplayTargetRefresh(
            PlayHistory.SelectedDisplayTarget?.Identity ?? string.Empty,
            advanceRevision: false);
        PlayHistory.SummaryFilterRefreshRequested += (_, _) => PlayHistory.QueueKeywordFilterRefresh(
            NormalizePlaylistKeywordFilter(ChartFilters.KeywordFilter));
        startupProgressWorkflowOwner.PropertyChanged += StartupProgressWorkflowOwnerPropertyChanged;
        PlaylistWorkspace.PlaylistDetailSortChanged += PlaylistWorkspacePlaylistDetailSortChanged;
        PlaylistWorkspace.PlaylistDetailFilterChanged += PlaylistWorkspacePlaylistDetailFilterChanged;
        regularChartListOwner = childComposition.RegularChartListOwner;
        startupPostInitializationWarmupOwner = new StartupPostInitializationWarmupOwner(
            () =>
            {
                BMSLibrary library = files;
                return library != null
                    && regularChartListOwner.TryBeginVirtualOrderPrewarm(
                        library,
                        out RegularChartListPrewarmLease lease)
                    ? lease
                    : null;
            },
            lease => regularChartListOwner.CancelVirtualOrderPrewarm(lease),
            (lease, reason, cancellationToken) => RunStartupPostInitializationWarmupStage(
                lease,
                reason,
                cancellationToken,
                (library, stageReason) => library.WarmOwnedRealPathDirectoryView(stageReason)),
            (lease, reason, cancellationToken) => RunStartupPostInitializationWarmupStage(
                lease,
                reason,
                cancellationToken,
                (library, stageReason) => library.WarmInstallDestinationOverlaySnapshot(stageReason)),
            (lease, reason, cancellationToken) => RunStartupPostInitializationWarmupStage(
                lease,
                reason,
                cancellationToken,
                (library, stageReason) => library.WarmInstalledPrimaryHashLookup(stageReason)),
            (lease, reason, cancellationToken) => RunStartupPostInitializationWarmupStage(
                lease,
                reason,
                cancellationToken,
                (library, stageReason) => library.WarmOwnedChartHashIndexSnapshot(stageReason)),
            (lease, reason) =>
            {
                IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors =
                    RegularChartListOwner.CreateDefaultVirtualOrderPrewarmDescriptors();
                bool includeBmsonRows = ShouldIncludeBmsonLibraryRowsInMainView(
                    MainViewUpdateMode.FolderFilterSelected,
                    MainViewUpdateMode.FolderFilterSelected);
                regularChartListOwner.RunVirtualOrderPrewarm(
                    lease,
                    lease.Library,
                    includeBmsonRows,
                    descriptors,
                    reason);
            },
            (name, expectedGeneration, ownerSequence) => startupBackgroundTaskScheduler.Reserve(
                name,
                expectedGeneration,
                ownerSequence),
            (reservation, reason, work, discard) => startupBackgroundTaskScheduler.QueueReserved(
                reservation,
                reason,
                work,
                discard),
            startupBackgroundTaskScheduler.CancelQueued,
            CompleteStartupPostInitializationWarmup,
            (exception, stage) => LogMainViewBuild(
                "post_startup_warmup failure stage=" + stage
                + " exception=" + exception.GetType().Name
                + " message=" + FormatTextForLog(exception.Message)));
        regularChartListOwner.NormalLibraryRefreshApplied += RegularChartListOwnerNormalLibraryRefreshApplied;
        regularChartListOwner.RefreshSuppressionChanged += RegularChartListOwnerRefreshSuppressionChanged;
        regularChartListOwner.TreeNavigationPresentationRequested += RegularChartListOwnerTreeNavigationPresentationRequested;
        regularChartListOwner.MaintenanceNavigationPresentationRequested += RegularChartListOwnerMaintenanceNavigationPresentationRequested;
        regularChartListOwner.InstallNavigationPresentationRequested += RegularChartListOwnerInstallNavigationPresentationRequested;
        MainChartList.SortRequested += MainChartListSortRequested;
        MainChartList.OperationContextChanged += (_, _) =>
        {
            SyncMainChartListSortPresentation();
            UpdateChartKeywordSearchContext();
        };
        MainChartList.CellEditBeginningRequested += MainChartListCellEditBeginningRequested;
        MainChartList.CellEditStarted += MainChartListCellEditStarted;
        MainChartList.CellEditEndedRequested += MainChartListCellEditEndedRequested;
        regularChartListOwner.SortChanged += RegularChartListOwnerSortChanged;
        regularChartListOwner.SortRefreshRequested += ChartListOwnerSortRefreshRequested;

        ShellShutdownWorkflow = new ShellShutdownWorkflowOwner(
            StartupUpdateWorkflow,
            ElevatedProcessWarningWorkflow,
            startupBackgroundTaskScheduler,
            regularChartListOwner,
            PlaylistWorkspace,
            playHistoryWorkflowOwner,
            PackageInstallWorkflow,
            MaintenanceRescanWorkflow,
            FolderAutoRenameWorkflow,
            PlaybackPanel,
            applicationComposition.SettingsEditSession,
            applicationComposition.OperationAdmission,
            applicationComposition.PlaylistOperationAdmission,
            _semaphore,
            startupProgressWorkflowOwner,
            applicationLifetime.MarkCoordinatedShutdownStarted,
            applicationLifetime.RequestShutdown,
            Net10PerformanceLog.StopAsync,
            DispatchShellShutdownActionAsync,
            LogShutdown,
            LogShutdownWarning,
            FormatTextForLog,
            applicationComposition.ReportTerminalSettingsSaveFailure,
            applicationLifetime.RestartApplicationAsync,
            applicationComposition.RestartFailureDialogs,
            applicationComposition.ReportRestartFailure);
        ProgressHub.AttachPlaylistProgressSources(
            PlaylistWorkspace,
            DispatchMainChartListAction,
            () => ShellShutdownWorkflow?.IsClosingOrClosed == true);
        PlayHistory.SortChanged += PlayHistorySortChanged;
        PlayHistory.SortRefreshRequested += ChartListOwnerSortRefreshRequested;
        PlayHistory.RestoreDisplayTargetIdentity(playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity);
        PlayHistory.RefreshDisplayTargetSetsFromSettings(
            playHistoryDisplaySettingsStore.DisplayTargetSetsJson,
            queueRefreshWhenSelectionChanges: false);
        SettingDialog = applicationComposition.CreateSettingDialogViewModel(
            statePort: this,
            workspacePort: PlaylistWorkspace,
            customFolderOutputPort: PlaylistWorkspace,
            playHistoryPort: PlayHistory,
            searchRootRuntimePort: LibraryFolderTree,
            playerFactoryPort: applicationComposition,
            playbackRuntimePort: PlaybackPanel,
            lr2SongDbSyncWorkflow: Lr2SongDbSyncWorkflow,
            requestOperationModeRestart: ShellShutdownWorkflow.RequestOperationModeRestartAsync);
    }

    private void MainChartListSortRequested(object sender, MainChartListSortRequestedEventArgs request)
    {
        if (request.Target == MainChartListSortTarget.PlayHistory)
        {
            PlayHistory.QueueSort(request);
        }
        else if (!PlaylistWorkspace.TryRequestPlaylistDetailSort(request.ColumnName, request.Direction))
        {
            regularChartListOwner.QueueSort(request);
        }
    }

    private void MainChartListCellEditBeginningRequested(object sender, MainChartListCellEditBeginningEventArgs request)
    {
        MainChartListCellEditContext context = request.Context;
        if (string.IsNullOrWhiteSpace(context.PropertyName))
        {
            return;
        }
        if (context.Row is PlaylistDetailRow)
        {
            request.Accepted = PlaylistWorkspace.CanBeginDetailEdit(context);
            return;
        }
        request.Accepted = regularChartListOwner.CanBeginCellEdit(context);
    }

    private void MainChartListCellEditStarted(object sender, MainChartListCellEditContext context)
    {
        if (context.Row is PlaylistDetailRow)
        {
            PlaylistWorkspace.BeginDetailEdit(context);
        }
    }

    private void MainChartListCellEditEndedRequested(object sender, MainChartListCellEditEndedEventArgs request)
    {
        MainChartListCellEditContext context = request.Context;
        if (context.Row is PlaylistDetailRow)
        {
            PlaylistWorkspace.CompleteDetailEdit(request).ObserveFault("playlistDetailCellEditCommit");
            return;
        }
        regularChartListOwner.CompleteCellEdit(request);
    }

    private void RegularChartListOwnerNormalLibraryRefreshApplied(
        object sender,
        NormalLibraryRefreshAppliedEventArgs request)
    {
        if (request?.NotificationBatch?.HasRefreshNotification != true)
        {
            return;
        }

        if (request.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged))
        {
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(request.Reason);
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView)
            || TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        MainViewRefreshAction action = regularChartListOwner.ApplyNotificationPresentation(
            files, request.NotificationBatch, treeViewFilterTypeSelected,
            treeViewFilterParameterSelected as ChartPackage, ChartFilters.CaptureSnapshot(), PlaylistWorkspace.IsPlaylistDetailViewActive);
        if (action == MainViewRefreshAction.Refresh)
        {
            if (request.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged))
            {
                RefreshNormalLibraryAfterSourceChanged(request.Reason);
            }
            else
            {
                RefreshLibraryMainViewForCurrentFilter();
            }
        }
    }

    private void RegularChartListOwnerTreeNavigationPresentationRequested(
        object sender,
        RegularChartTreeNavigationPresentationRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.KeywordPresentationRefreshRequired)
        {
            UpdateChartKeywordSearchContext();
        }
        RefreshChartRowsView(request.RefreshMode);
    }

    private void RegularChartListOwnerMaintenanceNavigationPresentationRequested(
        object sender,
        RegularChartMaintenanceNavigationPresentationRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.KeywordPresentationRefreshRequired)
        {
            UpdateChartKeywordSearchContext();
        }
        if (request.RefreshRequested)
        {
            RefreshChartRowsView(request.Mode, request.Parameter);
        }
    }

    private void RegularChartListOwnerInstallNavigationPresentationRequested(
        object sender,
        RegularChartInstallNavigationPresentationRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.KeywordPresentationRefreshRequired)
        {
            UpdateChartKeywordSearchContext();
        }
        RefreshChartRowsView(request.Mode, request.Parameter);
    }

    private void PlaylistWorkspacePlaylistDetailScoreSnapshotRefreshRequested(
        object sender,
        PlaylistDetailScoreSnapshotRefreshRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        InvokeMainChartListPresentationAction(
            () =>
            {
                if (!PlaylistWorkspace.IsPlaylistDetailViewActive
                    || PlaylistWorkspace.IsPlaylistSummaryMode)
                {
                    return;
                }
                if (!request.RefreshRequired)
                {
                    LogPlaylistWorker("playlist_score_snapshot_refresh_deferred scoreSnapshotVersion="
                        + request.ScoreSnapshotVersion
                        + " lastBuiltScoreSnapshotVersion="
                        + request.LastBuiltVersion
                        + " reason=editing");
                    return;
                }
                LogPlaylistWorker("playlist_score_snapshot_refresh_requested scoreSnapshotVersion="
                    + request.ScoreSnapshotVersion
                    + " lastBuiltScoreSnapshotVersion="
                    + request.LastBuiltVersion
                    + " deferredByEdit="
                    + request.DeferredByEdit.ToString().ToLowerInvariant());
                if (!TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
                }
            });
    }

    private void PlaylistWorkspaceTreeSelectionActivated(
        object sender,
        PlaylistTreeSelectionActivatedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        if (request.IsSummary)
        {
            regularChartListOwner.PrepareForMainViewRefresh();
            SetTreeViewFilterSelection(MainViewUpdateMode.PlaylistFilterSelected, null);
            PlayHistory.ClearSummaryPresentation();
            if (request.SummaryModeChanged)
            {
                UpdateChartKeywordSearchContext();
            }
            return;
        }
        PlaylistDetailSelection selection = request.Detail;
        if (request.SummaryModeChanged)
        {
            UpdateChartKeywordSearchContext();
        }
        RefreshChartRowsView(
            selection.Filter == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
                ? MainViewUpdateMode.PlaylistNotOwnedFilterSelected
                : MainViewUpdateMode.PlaylistFilterSelected,
            selection);
    }

    private void PlaylistWorkspacePlaylistDetailReloadRefreshRequested(object sender, EventArgs e)
    {
        InvokeMainChartListPresentationAction(
            () =>
            {
                if (PlaylistWorkspace.ShouldRefreshPlaylistDetailAfterReload(treeViewFilterTypeSelected))
                {
                    RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
                }
            });
    }

    private void RegularChartListOwnerSortChanged(object sender, MainChartListSortRequestedEventArgs request)
    {
        if (MainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.PlayHistory && !IsPlaylistDetailWorkflowActive)
        {
            SyncMainChartListSortPresentation();
        }
    }

    private void PlayHistorySortChanged(object sender, MainChartListSortRequestedEventArgs request)
    {
        if (MainChartList.CurrentOperationContext.OperationSection == MainViewOperationSection.PlayHistory)
        {
            SyncMainChartListSortPresentation();
        }
    }

    private void PlayHistoryPeriodRequestActivated(
        object sender,
        PlayHistoryViewRequestActivatedEventArgs eventArgs)
    {
        PlayHistoryViewRequest request = eventArgs?.Request;
        if (request == null)
        {
            return;
        }

        PlaylistWorkspace.RequestPlaylistSummaryMode(enabled: false);
        PlaylistWorkspace.ClearPlaylistDetailSelection();
        lock (playHistoryViewRequestLock)
        {
            treeViewFilterTypeSelected = MainViewUpdateMode.PlayHistorySelected;
            treeViewFilterParameterSelected = request;
        }

        Task.Run(() => RefreshChartRowsView(MainViewUpdateMode.PlayHistorySelected, request))
            .ObserveFault("playHistoryPeriodSelect");
    }

    private void ChartListOwnerSortRefreshRequested(object sender, MainChartListSortRequestedEventArgs request)
    {
        bool isCurrent = request.Target == MainChartListSortTarget.PlayHistory
            ? PlayHistory.IsCurrentSortRequest(request)
            : regularChartListOwner.IsCurrentSortRequest(request);
        if (isCurrent
            && (request.Target == MainChartListSortTarget.PlayHistory
                ? MainChartList.CurrentOperationContext.OperationSection == MainViewOperationSection.PlayHistory
                : MainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.PlayHistory && !IsPlaylistDetailWorkflowActive))
        {
            RefreshChartRowsView(MainViewUpdateMode.SortUpdated, expectedSortTarget: request.Target);
        }
    }

    private void DispatchMainChartListAction(Action action)
    {
        DispatchUiAction(action);
    }

    private void QueueMainChartListAction(Action action)
    {
        if (action == null)
        {
            return;
        }

        IUiScheduledOperation operation = uiScheduler.Schedule(action);
        if (operation == null || !operation.IsAccepted)
        {
            throw new InvalidOperationException(
                "Playlist catalog notification could not be queued."
                + (string.IsNullOrWhiteSpace(operation?.RejectionReason)
                    ? string.Empty
                    : " reason=" + operation.RejectionReason));
        }
    }

    private Task DispatchShellShutdownActionAsync(Func<Task> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (uiScheduler.CanExecuteInline)
        {
            return action();
        }
        if (!uiScheduler.IsAvailable)
        {
            return Task.FromException(new InvalidOperationException("The UI dispatcher is shutting down."));
        }
        return uiScheduler.InvokeAsync(action, UiSchedulePriority.Normal);
    }

    private void DispatchMainChartListPresentationAction(Action action)
    {
        InvokeMainChartListPresentationAction(action);
    }

    private Task ApplyMainChartListPresentationActionAsync(Action action)
    {
        if (!InvokeMainChartListPresentationAction(action))
        {
            return Task.FromException(
                new InvalidOperationException("The UI dispatcher is shutting down."));
        }
        return Task.CompletedTask;
    }

    private bool InvokeMainChartListPresentationAction(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (uiScheduler.CanExecuteInline)
        {
            action();
            return true;
        }
        if (!uiScheduler.IsAvailable)
        {
            return false;
        }

        try
        {
            uiScheduler.Invoke(action, UiSchedulePriority.Normal);
            return true;
        }
        catch (InvalidOperationException) when (!uiScheduler.IsAvailable)
        {
            return false;
        }
        catch (OperationCanceledException) when (!uiScheduler.IsAvailable)
        {
            return false;
        }
    }

    private void PlaylistWorkspacePlaylistDetailSortChanged(
        object sender,
        MainChartListSortRequestedEventArgs request)
    {
        if (!IsPlaylistDetailWorkflowActive
            || !PlaylistWorkspace.IsCurrentPlaylistDetailSortRequest(request))
        {
            return;
        }

        MainChartList.SetSortPresentation(
            CreateMainChartListSortPresentation(PlaylistWorkspace.CapturePlaylistDetailSortParameters()),
            MainChartListSortTarget.Regular);
        RefreshChartRowsView(MainViewUpdateMode.SortUpdated, expectedSortTarget: MainChartListSortTarget.Regular);
    }

    private void PlaylistWorkspacePlaylistDetailFilterChanged(
        object sender,
        PlaylistDetailFilterChangedEventArgs request)
    {
        if (!IsPlaylistDetailWorkflowActive
            || !PlaylistWorkspace.IsCurrentPlaylistDetailFilterRequest(request))
        {
            return;
        }

        RefreshChartRowsView(request.UpdateMode);
    }


    private void StartupProgressWorkflowOwnerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e?.PropertyName == nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
            || e.PropertyName == nameof(StartupProgressWorkflowOwner.IsOperationActive)
            || e.PropertyName == nameof(StartupProgressWorkflowOwner.IsFailed)
            || e.PropertyName == nameof(StartupProgressWorkflowOwner.IsRetryableFailure))
        {
            RaisePropertyChanged(nameof(IsLibraryOperationInProgress));
            RaiseLibraryOperationAvailabilityChanged();
        }
    }

    private void UpdateLr2SongDbSyncRuntimeStatus(Lr2SongDbSyncStatusSnapshot snapshot)
    {
        ProgressHub.UpdateLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.Now, files?.Lr2Synchronization.ProgressRequest));
    }

    private static void LogShutdown(string message)
    {
        string line = "shutdown " + (message ?? string.Empty);
        NLogWrapper.FileLogger?.Info(line);
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(line);
        }
    }

    private static void LogShutdownWarning(string message)
    {
        string line = "shutdown " + (message ?? string.Empty);
        NLogWrapper.FileLogger?.Warn(line);
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Warn(line);
        }
    }

    private static string FormatTextForLog(string value)
    {
        return (value ?? string.Empty).Replace(Environment.NewLine, " | ");
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 対象解決前にPを非待機取得し、DB一覧・必須項目読込み・外部同期・出力と参照公開の実終端を同じ権限で待ちます。
    /// 競合要求はBusyとして未開始で終了し、予約・再実行しません。
    /// </summary>
    internal async Task ReloadTablesAsync()
    {
        if (!initializationCompleted || IsRequiredInitializationShutdownRequested) { return; }
        if (!tables.TryEnterPlaylistMutation(out IDisposable admission))
        {
            if (!IsRequiredInitializationShutdownRequested) { await FileDbMutationDialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, BeMusicSeeker.Properties.Resources.Warning)); }
            return;
        }
        StartupInitializationResult result;
        using (admission)
        using (LibraryFileMutationCapability authority = tables.CreatePlaylistMutationCapability(admission))
        {
            await _semaphore.WaitAsync();
            long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree);
            try
            {
                await PlaylistTablesReloadWorkflow.ReloadAsync(new(operationToken, authority)).LoggingAndPropagate("ReloadTables");
                await tables.ApplyRequiredLibraryReferencesAsync();
                result = new(StartupInitializationOutcome.Succeeded, GetStartupSettingsSnapshot(), operationToken,
                    OperationKind: StartupProgressOperationKind.ReloadTables);
            }
            catch (Exception failure) { result = CreateLimitedInitializationFailure(operationToken, StartupProgressOperationKind.ReloadTables, failure); }
            finally { EndUiUpdateSuppression(scheduleFlush: false); _semaphore.Release(); }
        }
        await CompleteLimitedInitializationAsync(result);
    }

    /// <summary>スコアのみの明示要求をLで受け付け、受付外で必要表示を完了します。ファイル・表・hostを再初期化しません。</summary>
    internal Task ReloadScoresOnlyAsync()
        => RunLibraryInputOperationAsync(ReloadScoresOnlyUnderAdmissionAsync, "ReloadScoresOnly");

    /// <summary>保存スコアと投影を同じL権限で終え、表示と順位後続に必要な結果を局所で返します。</summary>
    private async Task<StartupInitializationResult> ReloadScoresOnlyUnderAdmissionAsync(LibraryFileMutationCapability capability)
    {
        if (!initializationStatePort.IsInitializationCompleted) { return null; }
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView);
        try
        {
            playHistoryWorkflowOwner.InvalidateReadCache("score_reload");
            LibraryFileInitializationResult result = await Task.Run(() => files.InitializeScoresOnlyUnderAdmission(capability,
                new(startupBackgroundTaskScheduler.CurrentGeneration, operationToken, "score_only", operationToken)));
            PublishLatestLr2PlayHistorySchemaStatusSnapshotFromLibrary();
            return new(StartupInitializationOutcome.Succeeded, GetStartupSettingsSnapshot(), operationToken,
                RequiredResult: new(result, null), OperationKind: StartupProgressOperationKind.ScoreOnly);
        }
        catch (Exception failure) { return CreateLimitedInitializationFailure(operationToken, StartupProgressOperationKind.ScoreOnly, failure); }
        finally { EndUiUpdateSuppression(scheduleFlush: false); _semaphore.Release(); }
    }

    /// <summary>差分再読込みの停止・変更・必要参照までL/Pを所有し、受付外で表示と失敗通知を終えます。</summary>
    internal Task ReloadFileDiffAsync()
        => RunLibraryInputOperationAsync(ReloadFileDiffUnderAdmissionAsync, "ReloadFileDiff", requiresPlaylist: true);

    /// <summary>有限の入力変更を非待機で受け付け、局所結果を親解放後の同じ必須UI終端へ渡します。</summary>
    private async Task RunLibraryInputOperationAsync(Func<LibraryFileMutationCapability, Task<StartupInitializationResult>> operation, string routeName, bool requiresPlaylist = false)
    {
        if (!initializationStatePort.IsInitializationCompleted || IsRequiredInitializationShutdownRequested) { return; }
        if (!chartFileOperations.TryEnter(out IDisposable lease))
        {
            if (!IsRequiredInitializationShutdownRequested) { await FileDbMutationDialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, BeMusicSeeker.Properties.Resources.Warning)); }
            return;
        }
        IDisposable playlistLease = null;
        if (requiresPlaylist && !applicationComposition.PlaylistOperationAdmission.TryEnter(out playlistLease))
        {
            lease.Dispose();
            if (!IsRequiredInitializationShutdownRequested) { await FileDbMutationDialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, BeMusicSeeker.Properties.Resources.Warning)); }
            return;
        }
        StartupInitializationResult result;
        using (lease)
        using (playlistLease)
        using (LibraryFileMutationCapability playlistCapability = (playlistLease as LibraryFileMutationLease)?.CreateMutationCapability())
        using (LibraryFileMutationCapability original = chartFileOperations.CreateMutationCapability(lease))
        using (LibraryFileMutationCapability capability = original.WithPlaylistCapability(playlistCapability))
        {
            result = await operation(capability);
        }
        await CompleteLimitedInitializationAsync(result, routeName);
    }

    /// <summary>親解放済みの限定操作を完了し、実失敗を既存警告のあと元例外として返します。</summary>
    private async Task CompleteLimitedInitializationAsync(StartupInitializationResult result, string routeName = "ReloadTables")
    {
        if (result == null) { return; }
        result = await CompleteRequiredInitializationAfterAdmissionAsync(result);
        if (result.Failure is LibraryDirectoryPreflightException directoryFailure)
        {
            await PresentLibraryDirectoryWarningAsync(directoryFailure, LibraryDirectoryWarningPhase.Late, routeName + " directory preflight warning");
        }
        if (result.Failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Failure).Throw(); }
    }

    private StartupInitializationResult CreateLimitedInitializationFailure(long operationToken, StartupProgressOperationKind kind, Exception failure)
        => new(StartupInitializationOutcome.SettingsRequired, GetStartupSettingsSnapshot(), operationToken, Failure: failure, OperationKind: kind);

    /// <summary>同じL/Pで差分と必要LR2・保存済み表参照を終え、実表示前の結果を返します。</summary>
    private async Task<StartupInitializationResult> ReloadFileDiffUnderAdmissionAsync(LibraryFileMutationCapability capability)
    {
        if (!initializationStatePort.IsInitializationCompleted) { return null; }
        await PlaybackPanel.StopPlaybackForMutationAsync();
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
        BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
        var scanWarnings = new ConcurrentDictionary<LibraryScanWarningKind, LibraryScanWarning>();
        IReadOnlyList<UiMessageRequest> CaptureNotifications() => Array.AsReadOnly(scanWarnings.Values.OrderBy(warning => warning.Kind)
            .Select(StartupLibraryInitializationWorkflowOwner.CreateScanWarningNotification).ToArray());
        try
        {
            FileDiffReloadWorkflowResult result = await FileDiffReloadWorkflow.ReloadAsync(new("ReloadFileDiff", operationToken, capability,
                new(startupBackgroundTaskScheduler.CurrentGeneration, operationToken, "reload_file_diff", operationToken), warning => scanWarnings.TryAdd(warning.Kind, warning))).LoggingAndPropagate("ReloadFileDiff");
            return new(StartupInitializationOutcome.Succeeded, GetStartupSettingsSnapshot(), operationToken,
                RequiredResult: new(result.FileResult, null), OperationKind: StartupProgressOperationKind.ReloadFileDiff, BackupNotifications: CaptureNotifications());
        }
        catch (Exception failure) { return CreateLimitedInitializationFailure(operationToken, StartupProgressOperationKind.ReloadFileDiff, failure) with { BackupNotifications = CaptureNotifications() }; }
        finally { EndUiUpdateSuppression(scheduleFlush: false); _semaphore.Release(); }
    }

    /// <summary>全再初期化の必須処理を同じL/Pで終え、解放後の共通UI・通知・後続へ合流します。</summary>
    internal async Task ReinitializeLibraryAsync()
    {
        if (IsRequiredInitializationShutdownRequested || !initializationCompleted) { return; }
        LeapYearFolderRepairNotification notification = null;
        LeapYearFolderRepairApproval approval = await startupLibraryInitializationWorkflowOwner.PrepareLeapYearFolderRepairAsync(
            files.InitializationSongDbPath, FileDbMutationDialogs, () => IsRequiredInitializationShutdownRequested);
        await RunLibraryInputOperationAsync(async capability =>
        {
            StartupInitializationResult result = await ReinitializeLibraryUnderAdmissionAsync(capability, approval, value => notification = value);
            return result with { RepairNotification = notification };
        }, "FullReinitialize", requiresPlaylist: true);
    }

    /// <summary>既存組の保存データ・全差分・出力と必要LR2を親受付内で終え、選択した段階の変更不能な結果を返します。</summary>
    private async Task<StartupInitializationResult> ReinitializeLibraryUnderAdmissionAsync(LibraryFileMutationCapability capability, LeapYearFolderRepairApproval approval,
        Action<LeapYearFolderRepairNotification> repairNotificationObserver)
    {
        await PlaybackPanel.StopPlaybackForMutationAsync();
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.FullReinitialize);
        startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(true);
        var scanWarnings = new ConcurrentDictionary<LibraryScanWarningKind, LibraryScanWarning>();
        IReadOnlyList<UiMessageRequest> CaptureNotifications() => Array.AsReadOnly(scanWarnings.Values.OrderBy(warning => warning.Kind)
            .Select(StartupLibraryInitializationWorkflowOwner.CreateScanWarningNotification).ToArray());
        try
        {
            playHistoryWorkflowOwner.InvalidateReadCache("full_reinitialize");
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree
                | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
            StartupRequiredInitializationResult required = await startupLibraryInitializationWorkflowOwner.ReinitializeAsync(files, tables, Lr2SongDbSyncWorkflow, capability,
                new(startupBackgroundTaskScheduler.CurrentGeneration, operationToken, "reinitialize", operationToken), approval, repairNotificationObserver, warning => scanWarnings.TryAdd(warning.Kind, warning));
            if (required.Lr2Failure != null) { UpdateLr2SongDbSyncRuntimeStatus(files.GetLr2SongDbSyncStatusSnapshot()); }
            startupReadyDataReached = true;
            return new(StartupInitializationOutcome.Succeeded, GetStartupSettingsSnapshot(), operationToken,
                RequiredResult: required, OperationKind: StartupProgressOperationKind.FullReinitialize, BackupNotifications: CaptureNotifications());
        }
        catch (Exception failure) { return CreateLimitedInitializationFailure(operationToken, StartupProgressOperationKind.FullReinitialize, failure) with { BackupNotifications = CaptureNotifications() }; }
        finally
        {
            EndUiUpdateSuppression(scheduleFlush: false);
            _semaphore.Release();
            startupProgressWorkflowOwner.MarkStartupProgressFailureCleanupComplete(operationToken);
        }
    }

    private LR2Config CreateLR2PlayerConfig(StartupSettingsSnapshot startupSettings)
    {
        if (startupSettings.OperationModeLR2DB && lr2config != null)
        {
            return lr2config;
        }
        return new LR2Config(startupSettings.LR2ConfigXmlPath);
    }

    /// <summary>
    /// 起動・受理済み再構築の共通受付を取得または借用してライブラリを画面の各管理主体へ接続し、
    /// 一時導入試聴の参照交換にも同ownerの生存権限を渡します。
    /// </summary>
    /// <param name="library">今回構築した非nullのライブラリ。</param>
    /// <param name="capability">外側の受理済み操作の権限。通常起動ではnullで非待機取得します。</param>
    void IStartupLibraryApplicationPort.AttachStartupLibrary(BMSLibrary library, LibraryFileMutationCapability capability)
    {
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        IDisposable operationGate = capability == null ? null : chartFileOperations.Borrow(capability);
        if (operationGate == null && !chartFileOperations.TryEnter(out operationGate))
        {
            throw new InvalidOperationException("A chart-file operation is already active during startup attachment.");
        }
        using (operationGate)
        {
            using LibraryFileMutationCapability attachmentCapability = chartFileOperations.CreateMutationCapability(operationGate);
            files = library;
            ShellShutdownWorkflow.AttachLibrary(files);
            PackageInstallWorkflow.AttachLibrary(files);
            MaintenanceRescanWorkflow.AttachLibrary(files);
            FolderAutoRenameWorkflow.AttachLibrary(files);
            regularChartListOwner.AttachNormalLibraryRefreshSource(files);
            PlaybackPanel.AttachLibrary(files, attachmentCapability);
        }
    }

    void IStartupLibraryApplicationPort.AttachStartupServices(StartupLibraryServices services)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        files = services.Library;
        tables = services.Playlist;
        files.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => startupBackgroundTaskScheduler.Queue(name, reason, dependency, work);
        files.StartupBackgroundTaskReporter = startupBackgroundTaskScheduler.Report;
        files.StartupExecutionProgressReporterFactory = startupBackgroundTaskScheduler.CaptureExecutionProgressReporter;
        files.StartupProgressRequestFactory = startupBackgroundTaskScheduler.CaptureProgressRequest;
        files.StartupRequestProgressReporter = startupBackgroundTaskScheduler.ReportRequestProgress;
        files.AttachStartupRequestProgressSources();
        files.StartupBackgroundWorkSnapshotProvider = startupBackgroundTaskScheduler.CaptureWorkSnapshot;
        tables.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => startupBackgroundTaskScheduler.Queue(name, reason, dependency, work);
        tables.ProgressRequestFactory = startupBackgroundTaskScheduler.CaptureProgressRequest;
        tables.RequestProgressReporter = startupBackgroundTaskScheduler.ReportRequestProgress;
        tables.BmtOutput.ProgressRequestFactory = startupBackgroundTaskScheduler.CaptureProgressRequest;
        tables.BmtOutput.RequestProgressReporter = startupBackgroundTaskScheduler.ReportRequestProgress;
        tables.BmtOutput.ExportProgressReporter = PlaylistWorkspace.ReportPlaylistSyncProgress;
        tables.BmtOutput.FailureReporter = PlaylistWorkspace.ReportBmtOutputFailures;
        tables.CustomFolderOutputRepairProgressReporter = PlaylistWorkspace.ReportPlaylistSyncProgress;
        ShellShutdownWorkflow.AttachPlaylist(tables);
        PlaylistWorkspace.RefreshPlaylistTreeTables(tables, files);
        PlaylistWorkspace.SetDetailDataSource(
            applicationComposition.CreatePlaylistDetailDataSource(files, tables, MainChartList));
    }

    /// <summary>
    /// 起動処理を直列化し、設定画面への案内は排他とUI抑止の解放後に行います。
    /// </summary>
    /// <returns>必須処理とその終端通知に成功し、この操作の完了を公開した場合だけ true。</returns>
    internal async Task<bool> InitializeAsync()
    {
        StartupInitializationResult result = await InitializeLibraryAsync(isNormalStartup: true);
        return result.Outcome == StartupInitializationOutcome.Succeeded && result.CompletionPublished && result.Failure == null;
    }

    /// <summary>親L/P解放後の必須UIで実hostを接続する画面境界です。成功property通知を接続開始の根拠にしません。</summary>
    internal event Action RequiredStartupUiApplying;

    /// <summary>親L/P解放後に必須UI・hostを直接待ち、同じ操作の成功公開・解禁・任意登録閉鎖と開始を順に終えます。</summary>
    private async Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result, bool isNormalStartup = false)
    {
        try
        {
            if (result == null) { return null; }
            if (result.Failure != null)
            {
                CompleteStartupFailureAfterAdmission(result.OperationToken, result.Failure);
                if (IsStartupCompletionTokenCurrent(result.OperationToken) && !IsRequiredInitializationShutdownRequested)
                {
                    MarkNonStartupBackgroundSchedulingComplete();
                    startupBackgroundTaskScheduler.Start();
                    try
                    {
                        await startupLibraryInitializationWorkflowOwner.PresentLeapYearFolderRepairAsync(result.RepairNotification, FileDbMutationDialogs,
                        () => IsRequiredInitializationShutdownRequested, () => IsStartupCompletionTokenCurrent(result.OperationToken));
                        Exception warningFailure = await startupLibraryInitializationWorkflowOwner.PresentCompletionNotificationsAsync(null, false, false,
                            FileDbMutationDialogs, () => IsRequiredInitializationShutdownRequested, () => IsStartupCompletionTokenCurrent(result.OperationToken), result.BackupNotifications);
                        if (warningFailure != null) { ReportStartupLibraryInitializationFailurePresentationFailure(result.Failure, warningFailure); }
                    }
                    catch (Exception secondaryNotificationFailure) { ReportStartupLibraryInitializationFailurePresentationFailure(result.Failure, secondaryNotificationFailure); }
                }
                return result;
            }
            if (result.Outcome != StartupInitializationOutcome.Succeeded || result.CompletionPublished)
            {
                if (result.Outcome == StartupInitializationOutcome.SettingsRequired && IsStartupCompletionTokenCurrent(result.OperationToken)
                    && !IsRequiredInitializationShutdownRequested)
                {
                    startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
                    if (result.ValidationFailure != null)
                    {
                        NLogWrapper.FileLogger?.Warn("startup_setting_validation_failed " + result.ValidationFailure.Replace(Environment.NewLine, " | "));
                        if (isNormalStartup && applicationLifetime.IsFirstStartup) { SettingDialog?.RequestInitialSetupLanguageDialog(); }
                        else
                        {
                            UiDialogResult warning = await FileDbMutationDialogs.ShowMessageAsync(new UiMessageRequest(
                                BeMusicSeeker.Properties.Resources.Msg_init_settings_check, BeMusicSeeker.Properties.Resources.Warning,
                                MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK));
                            if (warning?.Status == UiDialogStatus.AppClosing && IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
                            UiDialogRoute.ThrowIfNotShown(warning, "Startup settings validation notification");
                        }
                    }
                }
                return result;
            }
            ArgumentNullException.ThrowIfNull(result.Settings);
            if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
            long operationToken = result.OperationToken;
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            await uiScheduler.InvokeAsync(() =>
            {
                if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
                if (!IsStartupCompletionTokenCurrent(operationToken)) { return Task.CompletedTask; }
                UiRefreshChannel channels = result.OperationKind switch
                {
                    StartupProgressOperationKind.ScoreOnly => UiRefreshChannel.LibraryMainView,
                    StartupProgressOperationKind.ReloadTables => UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree,
                    _ => UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree
                };
                return FlushRequiredUiRefreshAsync(channels, operationToken, logReadiness: true);
            });
            if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            if (result.OperationKind == StartupProgressOperationKind.Startup)
            {
                await uiScheduler.InvokeAsync(() =>
                {
                    if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
                    if (IsStartupCompletionTokenCurrent(operationToken)) { RequiredStartupUiApplying?.Invoke(); }
                    return Task.CompletedTask;
                });
                if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
                if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            }
            startupReadyUiReached = true;
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyUi, operationToken);
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            startupReadyOperableReached = true;
            bool firstStartup = result.OperationKind == StartupProgressOperationKind.Startup && applicationLifetime.IsFirstStartup;
            if (firstStartup) { applicationLifetime.CompleteFirstStartup(); }
            initializationCompleted = true;
            hasActiveLibraryProfile = true;
            RaisePropertyChanged(nameof(IsInitializationCompleted));
            RaisePropertyChanged(nameof(HasActiveLibraryProfile));
            RaiseLibraryOperationAvailabilityChanged();
            result = result with { CompletionPublished = true };
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            lock (startupInitializationCompletionLock) { startupCompletionContinuationToken = operationToken; }
            startupProgressWorkflowOwner.CompleteRequiredInitialization(operationToken);
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            if (IsRequiredInitializationShutdownRequested) { return result; }
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            TryLogStartupReadyOperable(operationToken);
            Exception notificationFailure = await startupLibraryInitializationWorkflowOwner.PresentCompletionNotificationsAsync(
                result.RepairNotification, result.ShowRootWarning, firstStartup, FileDbMutationDialogs,
                () => IsRequiredInitializationShutdownRequested, () => IsStartupCompletionTokenCurrent(operationToken), result.BackupNotifications, result.RequiredResult?.Lr2Failure);
            if (notificationFailure != null)
            {
                result = result with { Failure = notificationFailure };
                CompleteStartupFailureAfterAdmission(operationToken, notificationFailure);
            }
            if (IsRequiredInitializationShutdownRequested)
            {
                if (result.Failure != null) { return result; }
                throw new OperationCanceledException();
            }
            if (!IsStartupCompletionTokenCurrent(operationToken)) { return result; }
            if (result.OperationKind is StartupProgressOperationKind.Startup or StartupProgressOperationKind.FullReinitialize)
            {
                ProgressHub.BeginStartupBackgroundInitializationPresentation(operationToken, startupBackgroundTaskScheduler.CurrentGeneration);
            }
            files.ScheduleInitializationFollowUp(result.RequiredResult?.FileResult.FollowUp, result.RequiredResult?.ChartInfo);
            tables.SchedulePlaylistUrlCompletionRefresh("initialization_completed");
            if (result.OperationKind != StartupProgressOperationKind.ScoreOnly)
            {
                PlaylistWorkspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");
            }
            if (result.OperationKind is StartupProgressOperationKind.Startup or StartupProgressOperationKind.FullReinitialize)
            {
                ScheduleStartupPostInitializationWarmup("required_ui_completed", operationToken);
            }
            if (result.OperationKind == StartupProgressOperationKind.Startup)
            {
                startupBackgroundTaskScheduler.Queue("external_table_catalog", "Initialize", null,
                    () => PlaylistWorkspace.LoadExternalTableCollectionAsync(result.Settings.TableListURL, BMSPlaylist.GetBMSTableInfoAsync),
                    _ => PlaylistWorkspace.CancelExternalTableCollectionLoadForShutdown());
                if (!result.Settings.SkipInitPlaylistLoad) { PlaylistWorkspace.QueueExternalPlaylistSync("Initialize", false, true, 0); }
            }
            startupBackgroundTaskScheduler.MarkRequiredInitializationSchedulingComplete();
            startupBackgroundTaskScheduler.MarkPostInitializationSchedulingComplete();
            startupBackgroundTaskScheduler.Start();
            return result;
        }
        catch (OperationCanceledException) when (IsRequiredInitializationShutdownRequested && result.Failure == null)
        {
            return result with { Outcome = StartupInitializationOutcome.ShutdownRequested };
        }
        catch (Exception failure)
        {
            Exception primaryFailure = result.Failure ?? failure;
            if (result.Failure != null) { ReportStartupLibraryInitializationFailurePresentationFailure(primaryFailure, failure); }
            CompleteStartupFailureAfterAdmission(result.OperationToken, primaryFailure);
            return result with { Outcome = result.CompletionPublished ? result.Outcome : StartupInitializationOutcome.SettingsRequired, Failure = primaryFailure };
        }
    }

    /// <summary>親解放後に元の失敗とcleanupを既存進捗へ確定します。通知から再入した新tokenは変更しません。</summary>
    private void CompleteStartupFailureAfterAdmission(long operationToken, Exception failure)
    {
        if (!IsStartupCompletionTokenCurrent(operationToken)) { return; }
        if (!startupProgressWorkflowOwner.IsFailed)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(failure is LibraryDirectoryPreflightException
                ? BeMusicSeeker.Properties.Resources.LibraryDirectoryPreflightProgressFailure : failure.Message);
        }
        if (!startupProgressWorkflowOwner.IsRetryableFailure)
        {
            startupProgressWorkflowOwner.MarkStartupProgressFailureCleanupComplete(operationToken);
        }
    }

    Task<StartupInitializationResult> ISettingsDialogStatePort.CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
        => CompleteRequiredInitializationAfterAdmissionAsync(result);

    /// <summary>設定の実完了公開と保存通知後の継続に、既存の操作tokenと画面終了の判定を接続します。</summary>
    bool ISettingsDialogStatePort.IsInitializationCompletionCurrent(long operationToken)
        => !IsRequiredInitializationShutdownRequested && IsStartupCompletionTokenCurrent(operationToken);

    /// <summary>構成済みモデルまたは画面寿命の終了を、起動成功・解禁の直前に確認します。</summary>
    private bool IsRequiredInitializationShutdownRequested => ShellShutdownWorkflow?.IsShutdownRequested == true || files?.IsShutdownRequested == true
        || chartFileOperations.IsAdmissionClosed || applicationComposition.PlaylistOperationAdmission.IsAdmissionClosed;

    private async Task<StartupInitializationResult> InitializeLibraryAsync(bool isNormalStartup, LibraryFileMutationCapability capability = null,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null)
    {
        StartupLibraryInitializationGateLease initializationGate =
            await startupLibraryInitializationWorkflowOwner.AcquireGateAsync();
        Exception initializationFailure = null;
        Exception constructionFailure = null;
        LibraryDirectoryPreflightException directoryFailure = null;
        StartupSettingsSnapshot initializedSettings = null;
        LeapYearFolderRepairNotification repairNotification = null;
        LibraryDirectoryWarningPhase directoryWarningPhase =
            LibraryDirectoryWarningPhase.Early;
        StartupInitializationOutcome outcome;
        StartupInitializationResult initializationResult = null;
        var scanWarnings = new ConcurrentDictionary<LibraryScanWarningKind, LibraryScanWarning>();
        try
        {
            using (initializationGate)
            {
                initializationResult = await InitializeCoreAsync(
                    phase => directoryWarningPhase = phase,
                    settings => initializedSettings = settings, capability, exception => constructionFailure = exception, leapYearRepairApproval,
                    notification => { repairNotification = notification; repairNotificationObserver?.Invoke(notification); },
                    warning => scanWarnings.TryAdd(warning.Kind, warning));
                outcome = initializationResult.Outcome;
            }
        }
        catch (LibraryDirectoryPreflightException failure)
        {
            initializationFailure = failure;
            outcome = StartupInitializationOutcome.SettingsRequired;
        }
        catch (OperationCanceledException) when (IsRequiredInitializationShutdownRequested)
        {
            outcome = StartupInitializationOutcome.ShutdownRequested;
        }
        catch (Exception failure)
        {
            initializationFailure = failure;
            outcome = StartupInitializationOutcome.SettingsRequired;
        }
        initializationResult ??= new(outcome, initializedSettings, startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
        initializationResult = initializationResult with { Failure = constructionFailure ?? initializationFailure ?? initializationResult.Failure };
        initializationResult = initializationResult with
        {
            RepairNotification = repairNotification,
            BackupNotifications = Array.AsReadOnly((initializationResult.BackupNotifications ?? [])
                .Concat(scanWarnings.Values.OrderBy(warning => warning.Kind).Select(StartupLibraryInitializationWorkflowOwner.CreateScanWarningNotification)).ToArray()),
            ShowRootWarning = isNormalStartup && initializedSettings?.OperationModeLR2DB == true && string.IsNullOrWhiteSpace(initializedSettings.LR2RootPath)
        };
        if (capability == null && outcome != StartupInitializationOutcome.ShutdownRequested)
        {
            initializationResult = await CompleteRequiredInitializationAfterAdmissionAsync(initializationResult, isNormalStartup);
            outcome = initializationResult.Outcome;
        }
        initializationFailure = initializationResult.Failure;
        if (initializationFailure is LibraryDirectoryPreflightException directoryException)
        {
            directoryFailure = directoryException;
            initializationFailure = null;
        }

        if (IsRequiredInitializationShutdownRequested && outcome == StartupInitializationOutcome.Succeeded && !initializationResult.CompletionPublished)
        {
            outcome = StartupInitializationOutcome.ShutdownRequested;
        }
        if (outcome == StartupInitializationOutcome.ShutdownRequested)
        {
            return initializationResult with { Outcome = outcome };
        }
        if (capability != null) { return initializationResult with { Outcome = outcome }; }
        if (constructionFailure != null)
        {
            try
            {
                if (!IsRequiredInitializationShutdownRequested)
                {
                    UiDialogResult result = await FileDbMutationDialogs.ShowMessageAsync(new UiMessageRequest(
                        BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + constructionFailure,
                        BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                    UiDialogRoute.ThrowIfNotShown(result, "Startup library construction failure notification");
                }
            }
            catch (Exception presentationFailure) { ReportStartupLibraryInitializationFailurePresentationFailure(constructionFailure, presentationFailure); }
            NLogWrapper.GetLogger(typeof(MainWindowViewModel)).Error(constructionFailure,
                (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty) + " - " + Environment.NewLine + constructionFailure, null);
        }

        if (initializationFailure != null && constructionFailure == null)
        {
            PresentStartupLibraryInitializationFailure(initializationFailure);
        }

        if (directoryFailure != null)
        {
            // 設定の必須継続では外側leaseのcleanup・解放後に通知し、通知からの再試行を妨げません。
            await PresentLibraryDirectoryWarningAsync(
                directoryFailure,
                directoryWarningPhase,
                "Startup directory preflight warning");
        }

        if (isNormalStartup && outcome == StartupInitializationOutcome.SettingsRequired && !IsRequiredInitializationShutdownRequested
            && !(initializationResult.ValidationFailure != null && applicationLifetime.IsFirstStartup))
        {
            SettingDialog?.RequestOpen();
        }
        return initializationResult with { Outcome = outcome };
    }

    private async Task<StartupInitializationResult> InitializeCoreAsync(
        Action<LibraryDirectoryWarningPhase> recordDirectoryWarningPhase,
        Action<StartupSettingsSnapshot> recordStartupSettings, LibraryFileMutationCapability capability,
        Action<Exception> recordLibraryConstructionFailure, LeapYearFolderRepairApproval leapYearRepairApproval,
        Action<LeapYearFolderRepairNotification> repairNotificationObserver, Action<LibraryScanWarning> warningObserver)
    {
        if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
        if (capability == null && (!chartFileOperations.CanEnter || !applicationComposition.PlaylistOperationAdmission.CanEnter))
        {
            if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
            throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
        }
        startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(true);
        LogInitStage("start", "Initialize");
        initializationCompleted = false;
        startupReadyOperableReached = false;
        RaisePropertyChanged(nameof(IsInitializationCompleted));
        if (hasActiveLibraryProfile)
        {
            hasActiveLibraryProfile = false;
            RaisePropertyChanged(nameof(HasActiveLibraryProfile));
            RaiseLibraryOperationAvailabilityChanged();
        }
        _ = string.Empty;
        string text = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty;
        WindowTitle = "BeMusicSeeker Unofficial Fork - " + text;
        StartupSettingsSnapshot startupSettings = null;
        LR2Config startupLr2Config;
        AppSchemaPreflightResult schemaApproval = null;
        CustomFolderOutputSettingsSnapshot startupCustomFolderSettings = null;
        long operationToken = 0L;
        try
        {
            startupSettings = GetStartupSettingsSnapshot();
            recordStartupSettings?.Invoke(startupSettings);
            if (startupSettings.OperationModeLR2DB)
            {
                startupCustomFolderSettings = customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null during startup.");
            }
            (bool configValid, LR2Config config) = await StartupLibraryConstructionOwner.PrepareDirectoriesAsync(
                startupSettings,
                startupCustomFolderSettings);
            startupLr2Config = config;
            string startupValidationErrorMessage = BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath;
            if (!configValid || !SettingDialog.CheckValidation(out startupValidationErrorMessage))
            {
                return new(StartupInitializationOutcome.SettingsRequired, startupSettings, ValidationFailure: startupValidationErrorMessage ?? string.Empty);
            }
        }
        catch (LibraryDirectoryPreflightException failure)
        {
            recordDirectoryWarningPhase?.Invoke(LibraryDirectoryWarningPhase.Early);
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(
                StartupProgressOperationKind.Startup);
            return new(StartupInitializationOutcome.SettingsRequired, startupSettings, operationToken, Failure: failure);
        }
        catch (OperationCanceledException) when (IsRequiredInitializationShutdownRequested) { throw; }
        catch (Exception ex)
        {
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            recordLibraryConstructionFailure(ex);
            return new(StartupInitializationOutcome.SettingsRequired, OperationToken: operationToken);
        }
        recordDirectoryWarningPhase?.Invoke(LibraryDirectoryWarningPhase.Late);
        try
        {
            if (startupSettings.OperationModeLR2DB)
            {
                schemaApproval = await startupLibraryInitializationWorkflowOwner.PrepareSchemaAsync(startupSettings, FileDbMutationDialogs,
                    () => IsRequiredInitializationShutdownRequested, applicationLifetime.RequestShutdown);
                if (schemaApproval == null) { return new(StartupInitializationOutcome.ShutdownRequested); }
            }
        }
        catch (OperationCanceledException) when (IsRequiredInitializationShutdownRequested) { throw; }
        catch (Exception ex)
        {
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            recordLibraryConstructionFailure(ex);
            return new(StartupInitializationOutcome.SettingsRequired, OperationToken: operationToken);
        }
        if (capability == null)
        {
            leapYearRepairApproval = await startupLibraryInitializationWorkflowOwner.PrepareLeapYearFolderRepairAsync(
                startupSettings.OperationModeLR2DB ? startupSettings.LR2SongDBPath : applicationComposition.ApplicationPathSnapshot.StandaloneSongDbPath,
                FileDbMutationDialogs, () => IsRequiredInitializationShutdownRequested);
        }
        ArgumentNullException.ThrowIfNull(leapYearRepairApproval);
        using StartupRequiredOperationLease requiredOperation = startupLibraryInitializationWorkflowOwner.AcquireRequiredOperation(
            chartFileOperations, applicationComposition.PlaylistOperationAdmission, capability);
        capability = requiredOperation.Capability;
        try
        {
            playHistoryWorkflowOwner.InvalidateReadCache("initialize");
            if (schemaApproval != null) { await startupLibraryInitializationWorkflowOwner.ApplyPreparedSchemaAsync(startupSettings, schemaApproval); }
            StartupLibraryConstructionOwner.RepairCustomFolderSearchRoots(startupSettings, startupLr2Config);
            LibraryProfile libraryProfile = StartupLibraryConstructionOwner.CreateProfile(startupSettings, startupLr2Config, applicationComposition.ApplicationPathSnapshot);
            lr2config = startupLr2Config;
            StartupLibraryServices libraryServices = startupLibraryConstructionOwner.CreateAndApply(
                libraryProfile,
                this, capability, files, tables);
            files = libraryServices.Library;
            tables = libraryServices.Playlist;
            LibraryFolderTree.AttachLibrary(files);
            InstallTree.AttachLibrary(files);
            MaintenanceTree.AttachLibrary(files);
            IBMSPlayer configuredBmsPlayer = applicationComposition.CreateBmsPlayer(
                startupSettings,
                () => CreateLR2PlayerConfig(startupSettings));
            if (configuredBmsPlayer != null)
            {
                await PlaybackPanel.ReplacePlayerAsync(configuredBmsPlayer, deferWindowHostAttachment: true);
            }
            if (IsRequiredInitializationShutdownRequested) { throw new OperationCanceledException(); }
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(
                StartupProgressOperationKind.Startup);
        }
        catch (LibraryDirectoryPreflightException failure)
        {
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            return new(StartupInitializationOutcome.SettingsRequired, startupSettings, operationToken, Failure: failure);
        }
        catch (OperationCanceledException) when (IsRequiredInitializationShutdownRequested) { throw; }
        catch (Exception ex)
        {
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            // 構成失敗も親受付と開始済み処理の解放後に通知する。通知先からの明示再試行を妨げない。
            recordLibraryConstructionFailure(ex);
            return new(StartupInitializationOutcome.SettingsRequired, startupSettings, operationToken);
        }
        regularChartListOwner.InitializeColumnPresentation(treeViewFilterTypeSelected);
        listenerForBMSLibrary?.Dispose();
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSLibrary.RegisterHandler(() => files.OwnedCollectionVersion, delegate
        {
            PlaylistWorkspace.InvalidatePlaylistLibraryIndexSnapshot(
                "owned_collection_changed",
                startupReadyOperableReached,
                treeViewFilterTypeSelected);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressVersion, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLibraryInitializationStatuses(
                files.GetLibraryInitializationProgressSnapshots());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryDatabaseLoadCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressLibraryDatabaseLoad(files.LibraryDatabaseLoadCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileEnumerationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressLibraryFileEnumeration(files.LibraryFileEnumerationCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileDiffCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressLibraryFileDiff(files.LibraryFileDiffCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressScoreHydrationRequested(files.ScoreHydrationRequestedVersion, files.ScoreHydrationProgressRequest);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressScoreHydration(files.ScoreHydrationCompletedVersion);
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "score_hydration_completed");
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                    "score_hydration_completed");
                return;
            }
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "score_hydration_completed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreSnapshotVersion, delegate
        {
            MainChartList.RowProjection.CaptureVersions(files);
            if (!PlaylistWorkspace.IsPlaylistDetailViewActive)
            {
                return;
            }
            PlaylistWorkspace.RequestPlaylistDetailScoreSnapshotRefresh(files.ScoreSnapshotVersion);
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "score_snapshot_changed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshCompletedVersion, delegate
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "ranking_refresh_completed");
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                    "ranking_refresh_completed");
                return;
            }
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "ranking_refresh_completed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCompletedVersion, delegate
        {
            if ((files?.ChartInfoBackfillDigestBackfilledCount ?? 0) > 0)
            {
                InvalidateNormalLibraryIdentitySortKeys(NormalLibraryChartInfoDigestBackfilledReason);
            }
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressChartInfoHydrationRequested(files.ChartInfoHydrationRequestedVersion, files.ChartInfoHydrationProgressRequest);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressChartInfoHydration(files.ChartInfoHydrationCompletedVersion);
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationProgressSnapshot, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationProgressSnapshot);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStatusVersion, delegate
        {
            UpdateLr2SongDbSyncRuntimeStatus(files.GetLr2SongDbSyncStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallEstimationProgressVersion, delegate
        {
            UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        });
        UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        IReadOnlyList<UiMessageRequest> backupNotifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(startupSettings);
        Thread.Yield();
        startupReadyInstallStopwatch = Stopwatch.StartNew();
        startupReadyOperableStopwatch = Stopwatch.StartNew();
        startupPerformanceInteraction = PerformanceInteraction.Start(
            "startup",
            operationToken);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "input_accepted",
                "operationToken=" + operationToken);
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "owner_queued",
                "operationToken=" + operationToken);
        }
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
        startupReadyDataReached = false;
        startupReadyUiReached = false;
        startupReadyOperableReached = false;
        StartupRequiredInitializationResult requiredResult = await InitializeStartupLibraryFilesAsync(
            () => startupLibraryInitializationWorkflowOwner.InitializeRequiredAsync(
                files, tables, Lr2SongDbSyncWorkflow, startupPerformanceInteraction, capability,
                new(startupBackgroundTaskScheduler.CurrentGeneration, operationToken, "required_initialization", operationToken),
                startupCustomFolderSettings, leapYearRepairApproval, repairNotificationObserver, warningObserver));
        if (IsRequiredInitializationShutdownRequested) { return new(StartupInitializationOutcome.ShutdownRequested); }
        return new(StartupInitializationOutcome.Succeeded, startupSettings, operationToken, RequiredResult: requiredResult, BackupNotifications: backupNotifications);
    }

    /// <summary>必須管理主体の完了中はUI更新を抑制し、確定後の画面接続を適用します。失敗通知は親受付解放後の呼出元が担当します。</summary>
    /// <param name="initializeRequired">必須起動の実終端までを待つ管理主体の操作。</param>
    /// <returns>LR2単独失敗を既存の状態表示へ接続した必須処理の結果。終了取消とローカル失敗は例外で伝播します。</returns>
    internal async Task<StartupRequiredInitializationResult> InitializeStartupLibraryFilesAsync(
        Func<Task<StartupRequiredInitializationResult>> initializeRequired)
    {
        ArgumentNullException.ThrowIfNull(initializeRequired);
        BeginUiUpdateSuppression(
            UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree
                | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
        try
        {
            StartupRequiredInitializationResult result = await initializeRequired();
            if (result.Lr2Failure != null) { UpdateLr2SongDbSyncRuntimeStatus(files.GetLr2SongDbSyncStatusSnapshot()); }
            PublishLatestLr2PlayHistorySchemaStatusSnapshotFromLibrary();
            LogInitStage("files_initialize_done", "Initialize");
            long operationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
            startupReadyDataReached = true;
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData, operationToken);
            TryLogStartupReadyData(operationToken);
            return result;
        }
        finally
        {
            EndUiUpdateSuppression(scheduleFlush: false);
            LogInitStage("ui_suppress_end_called", "Initialize");
        }
    }

    /// <summary>開始済みTaskと親受付の解放後、元のローカル失敗を既存の通知経路へ渡します。</summary>
    private void PresentStartupLibraryInitializationFailure(Exception ex)
    {
        try
        {
            // 起動の実Taskは回収し元失敗を保持しますが、終了開始後に新しい失敗画面を開きません。
            if (!IsRequiredInitializationShutdownRequested)
            {
                startupLibraryInitializationFailurePresenter.Present(
                    new StartupLibraryInitializationFailurePresentation(ex));
            }
        }
        catch (Exception presentationException)
        {
            ReportStartupLibraryInitializationFailurePresentationFailure(
                ex,
                presentationException);
        }
        Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
        string version = Assembly.GetEntryAssembly().GetName().Version.ToString();
        currentClassLogger.Error(ex, version + " - " + Environment.NewLine + ex.ToString(), null);
    }

    private static void ReportStartupLibraryInitializationFailurePresentationFailure(
        Exception initializationException,
        Exception presentationException)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(
                presentationException,
                "startup_library_initialization_failure_presentation_failed primary_failure="
                    + initializationException);
        }
        catch
        {
            // Diagnostic reporting is secondary to preserving the original initialization failure policy.
        }
    }

    private void PlaylistWorkspacePlaylistTablesPresentationChanged(object sender, EventArgs e)
    {
        UpdateChartKeywordSearchContext();
        PlayHistory.QueueDisplayTargetCatalogRefresh();
    }

    private void PlaylistWorkspacePlaylistKeywordValueCandidatesChanged(object sender, EventArgs e)
    {
        UpdateChartKeywordSearchContext();
    }

    private void PlaylistWorkspacePlaylistEntriesHydrationRequested(
        object sender,
        PlaylistEntriesHydrationVersionChangedEventArgs e)
    {
        if (sender is not PlaylistWorkspaceViewModel workspace)
        {
            throw new InvalidOperationException(
                "Playlist hydration request sender is not the composed workspace.");
        }
        if (!workspace.TryBeginPlaylistHydrationNotification(
            e?.SourceStore,
            e?.SourceTables,
            e?.Generation ?? 0L,
            e?.CompletionReceipt))
        {
            return;
        }
        workspace.ExecuteCurrentPlaylistHydrationNotification(
            e?.SourceStore,
            e?.SourceTables,
            e?.Generation ?? 0L,
            e?.CompletionReceipt,
            () =>
            {
                startupProgressWorkflowOwner.TrackStartupProgressPlaylistEntriesHydrationRequested(
                    e?.Version ?? 0,
                    e?.Request?.OperationToken ?? 0, e?.Request);
            });
    }

    private void PlaylistWorkspacePlaylistEntriesHydrationCompleted(
        object sender,
        PlaylistEntriesHydrationVersionChangedEventArgs e)
    {
        if (sender is not PlaylistWorkspaceViewModel workspace)
        {
            throw new InvalidOperationException(
                "Playlist hydration completion sender is not the composed workspace.");
        }
        void ApplyHydrationLifecycle()
        {
            int version = e?.Version ?? 0;
            long operationToken = e?.Request?.OperationToken ?? 0;
            startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistEntriesHydration(version, operationToken);
            startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistReferenceFromHydration(version, operationToken);
            if (PlayHistory.SelectedDisplayTarget.UsesProjection)
            {
                playHistoryWorkflowOwner.QueueDisplayTargetRefresh(
                    PlayHistory.SelectedDisplayTarget?.Identity ?? string.Empty);
            }
        }
        if (e?.CompletionReceipt != null)
        {
            workspace.ExecuteCurrentPlaylistHydrationNotification(
                e.SourceStore,
                e.SourceTables,
                e.Generation,
                e.CompletionReceipt,
                ApplyHydrationLifecycle);
            return;
        }
        ApplyHydrationLifecycle();
    }

    private void PlaylistWorkspacePlaylistExternalSyncQueued(
        object sender,
        PlaylistExternalSyncRequestEventArgs request)
    {
        if (request == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        startupProgressWorkflowOwner.TrackStartupProgressExternalSyncRequest(request.Reason, request.Version, request.OperationToken, request.ProgressRequest);
        if (request.PublishesReferenceReceipt)
        {
            startupProgressWorkflowOwner.TrackStartupProgressPlaylistReferenceRequest(
                "DeferredExternalSync:" + request.Reason,
                request.Version,
                request.OperationToken, request.ProgressRequest);
        }
    }

    private void PlaylistWorkspacePlaylistExternalSyncCompleted(
        object sender,
        PlaylistExternalSyncCompletionEventArgs completion)
    {
        if (completion == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(completion.OperationToken))
        {
            return;
        }
        if (completion.WasSkipped && completion.PublishesReferenceReceipt)
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistReference(completion.Version, completion.OperationToken);
        }
        startupProgressWorkflowOwner.TryCompleteStartupProgressExternalSync(completion.Version, completion.OperationToken);
    }

    private void PlaylistWorkspacePlaylistExternalSyncReferenceApplied(
        object sender,
        PlaylistExternalSyncReferenceAppliedEventArgs request)
    {
        if (request == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistReference(request.Version, request.OperationToken);
    }

    private void PlaylistReferenceApplyWorkflowQueued(
        object sender,
        PlaylistReferenceApplyQueuedEventArgs request)
    {
        if (request == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        startupProgressWorkflowOwner.TrackStartupProgressPlaylistReferenceRequest(request.Reason, request.Version, request.OperationToken, request.ProgressRequest);
        startupProgressWorkflowOwner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(
            request.Version,
            "playlist_ref_deferred:" + request.Reason,
            request.OperationToken, request.ProgressRequest);
    }

    private void PlaylistReferenceApplyWorkflowCompleted(
        object sender,
        PlaylistReferenceApplyCompletedEventArgs completion)
    {
        if (completion == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(completion.OperationToken))
        {
            return;
        }
        startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistEntriesHydration(completion.Version, completion.OperationToken);
        startupProgressWorkflowOwner.TryCompleteStartupProgressPlaylistReference(completion.Version, completion.OperationToken);
    }

    private void PlaylistReferenceApplyWorkflowPresentationRequested(
        object sender,
        PlaylistReferenceApplyPresentationRequestedEventArgs request)
    {
        if (request == null || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree))
        {
            return;
        }
        RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
    }

    private void PlaylistReferenceApplyWorkflowReferenceApplied(
        object sender,
        PlaylistReferenceAppliedEventArgs receipt)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

    /// <summary>
    /// 指定された更新モードとパラメータに基づいて、メインの chart row 表示用コレクションを生成・更新します。
    /// ツリーでのフォルダ選択、プレイリストや難易度表の適用、Missingファイル等の保守フィルタ、およびキーワードやキーモードでの絞り込み等を行います。<br/>
    /// このメソッドの実行には、規模に応じて時間がかかるため内部でタイマー計測し遅延を制御・ロギングする機構が含まれています。
    /// </summary>
    /// <param name="mode">更新の契機（どのフィルタや要素が変更されたかを示す更新モード）。</param>
    /// <param name="parameter">選択されたプレイリスト（BMSTable）やフォルダ名などの追加パラメータ、無い場合は null。</param>
    private Task RefreshChartRowsView(
        MainViewUpdateMode mode,
        object parameter = null,
        MainChartListSortTarget? expectedSortTarget = null)
    {
        if (expectedSortTarget.HasValue
            && expectedSortTarget.Value != (MainChartList.CurrentOperationContext.OperationSection == MainViewOperationSection.PlayHistory
                ? MainChartListSortTarget.PlayHistory
                : MainChartListSortTarget.Regular))
        {
            return Task.CompletedTask;
        }
        regularChartListOwner.PrepareForMainViewRefresh();
        var viewBuildStopwatch = Stopwatch.StartNew();
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        MainViewUpdateMode requestedMode = mode;
        MainViewOperationSection previousOperationSection = MainChartList.CurrentOperationContext.OperationSection;
        MainViewUpdateMode activeTreeViewFilterMode = MainViewUpdateMode.TreeViewFilterNotChanged;
        object activeTreeViewFilterParameter = null;
        bool hasActiveTreeViewFilterSnapshot = false;
        if (mode == MainViewUpdateMode.TreeViewFilterNotChanged)
        {
            GetTreeViewFilterSelection(out activeTreeViewFilterMode, out activeTreeViewFilterParameter);
            mode = activeTreeViewFilterMode;
            parameter = activeTreeViewFilterParameter;
            hasActiveTreeViewFilterSnapshot = true;
        }
        else if (mode == MainViewUpdateMode.PlayHistorySelected)
        {
            var playHistoryRequest = parameter as PlayHistoryViewRequest;
            if (playHistoryRequest == null || !playHistoryWorkflowOwner.IsCurrentRequest(playHistoryRequest.RequestId))
            {
                playHistoryRuntimeEventReporter.ReportStaleViewRequest(
                    mode,
                    requestedMode,
                    parameter,
                    playHistoryRequest?.PeriodRequest ?? parameter as PlayHistoryPeriodRequest,
                    playHistoryRequest?.RequestId ?? 0L,
                    playHistoryWorkflowOwner.CurrentRequestId,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return Task.CompletedTask;
            }
        }
        else if (mode == MainViewUpdateMode.KeywordFilterUpdated && parameter is PlayHistoryViewRequest playHistoryKeywordRequest)
        {
            if (!playHistoryWorkflowOwner.IsCurrentRequest(playHistoryKeywordRequest.RequestId))
            {
                playHistoryRuntimeEventReporter.ReportStaleViewRequest(
                    mode,
                    requestedMode,
                    parameter,
                    playHistoryKeywordRequest.PeriodRequest,
                    playHistoryKeywordRequest.RequestId,
                    playHistoryWorkflowOwner.CurrentRequestId,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return Task.CompletedTask;
            }
            if (files == null)
            {
                return Task.CompletedTask;
            }
            LogPlayHistoryViewExecution(PlayHistory.ExecuteView(
                new PlayHistoryViewExecutionRequest(
                    mode,
                    requestedMode,
                    parameter,
                    viewBuildStopwatch,
                    playHistoryKeywordRequest,
                    filters.KeywordFilter,
                    PlayHistory.SelectedDisplayTarget)));
            return Task.CompletedTask;
        }
        else if (mode < MainViewUpdateMode.KeywordFilterUpdated)
        {
            if (mode == MainViewUpdateMode.DuplicateFilterSelected)
            {
                parameter = NormalizeDuplicateViewParameter(parameter);
            }
            SetTreeViewFilterSelection(mode, parameter);
            activeTreeViewFilterMode = mode;
            activeTreeViewFilterParameter = IsPlaylistViewMode(mode) ? null : parameter;
            hasActiveTreeViewFilterSnapshot = true;
            UpdateChartKeywordSearchContext();
        }
        if (previousOperationSection != MainChartList.CurrentOperationContext.OperationSection)
        {
            SyncMainChartListSortPresentation();
        }
        if (!hasActiveTreeViewFilterSnapshot)
        {
            GetTreeViewFilterSelection(out activeTreeViewFilterMode, out activeTreeViewFilterParameter);
        }
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(
            mode,
            requestedMode,
            activeTreeViewFilterMode,
            files != null);
        if (route.Kind == ChartListRefreshRouteKind.MissingFiles)
        {
            return Task.CompletedTask;
        }
        if (route.Kind == ChartListRefreshRouteKind.ApplyPlayHistoryView)
        {
            PlayHistoryViewRequest activeRequest = parameter as PlayHistoryViewRequest
                ?? playHistoryWorkflowOwner.SnapshotActiveRequest();
            if (activeRequest == null || !playHistoryWorkflowOwner.IsCurrentRequest(activeRequest.RequestId))
            {
                playHistoryRuntimeEventReporter.ReportStaleViewRequest(
                    route.Mode,
                    route.RequestedMode,
                    parameter,
                    activeRequest?.PeriodRequest ?? parameter as PlayHistoryPeriodRequest,
                    activeRequest?.RequestId ?? 0L,
                    playHistoryWorkflowOwner.CurrentRequestId,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return Task.CompletedTask;
            }
            LogPlayHistoryViewExecution(PlayHistory.ExecuteView(
                new PlayHistoryViewExecutionRequest(
                    route.Mode,
                    route.RequestedMode,
                    parameter,
                    viewBuildStopwatch,
                    activeRequest,
                    filters.KeywordFilter,
                    PlayHistory.SelectedDisplayTarget)));
            return Task.CompletedTask;
        }
        if (route.Kind == ChartListRefreshRouteKind.RegisterPlaylistSourceBuild)
        {
            UpdatePlaylistDetailActivation(route.IsPlaylistTreeActive);
            return PlaylistWorkspace.RequestDetailRefreshTask(
                route.Mode, route.RequestedMode, activeTreeViewFilterMode,
                ShouldUsePlaylistBuildCoalescingWindow(route.Mode, route.RequestedMode), CapturePlaylistOpenReadinessSnapshot());
        }
        RegularChartListEntryResult regularResult = regularChartListOwner.ApplyMainLibraryView(
            route,
            files,
            parameter,
            activeTreeViewFilterParameter,
            PlaylistWorkspace.IsPlaylistSummaryModeRequested,
            viewBuildStopwatch,
            filters);
        if (regularResult.WasCommitted && regularResult.SortWasReset)
        {
            if (MainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.PlayHistory)
            {
                SyncMainChartListSortPresentation();
            }
        }
        return Task.CompletedTask;
    }

    private void LogPlayHistoryViewExecution(PlayHistoryViewExecutionResult execution)
    {
        playHistoryRuntimeEventReporter.ReportViewExecution(
            execution,
            PlayHistory.SelectedDisplayTarget,
            playHistoryWorkflowOwner.CurrentRequestId);
    }

    private void ReportPlayHistoryReadWorkflowProgress(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        PlayHistoryReadWorkflowProgress progress)
    {
        if (progress?.Stage == PlayHistoryReadWorkflowProgressStage.ReadCompleted
            && progress.Lr2SchemaStatusSnapshot != null)
        {
            PublishLr2PlayHistorySchemaStatusSnapshot(progress.Lr2SchemaStatusSnapshot);
        }
        playHistoryRuntimeEventReporter.ReportReadWorkflowProgress(periodRequest, requestId, progress);
    }

    private void PublishLr2PlayHistorySchemaStatusSnapshot(Lr2PlayHistorySchemaStatusSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (uiScheduler.CanExecuteInline)
        {
            lr2PlayHistorySchemaStatusChanged?.Invoke(snapshot);
            return;
        }
        if (!uiScheduler.IsAvailable)
        {
            return;
        }
        uiScheduler.Schedule(
            () => lr2PlayHistorySchemaStatusChanged?.Invoke(snapshot),
            UiSchedulePriority.Background);
    }

    private void PublishLatestLr2PlayHistorySchemaStatusSnapshotFromLibrary()
    {
        var stopwatch = Stopwatch.StartNew();
        Lr2PlayHistorySchemaStatusSnapshot snapshot = files?.GetLr2PlayHistorySchemaStatusSnapshot()
            ?? Lr2PlayHistorySchemaStatusSnapshot.Reset;
        PublishLr2PlayHistorySchemaStatusSnapshot(snapshot);
        LogSettingsPerformance(
            "settings_schema_status_publish",
            stopwatch,
            "result=" + (snapshot.IsReset ? "reset" : "published")
            + " status=" + snapshot.Status
            + " path=" + snapshot.ScoreDbPath);
    }

    private static void LogSettingsPerformance(string action, Stopwatch stopwatch, string detail = null)
    {
        try
        {
            stopwatch?.Stop();
            NLogWrapper.FileLogger?.Info(
                (action ?? "settings")
                + " elapsedMs=" + (stopwatch?.ElapsedMilliseconds ?? 0L)
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }
        catch
        {
        }
    }

    private void GetTreeViewFilterSelection(out MainViewUpdateMode mode, out object parameter)
    {
        lock (playHistoryViewRequestLock)
        {
            mode = treeViewFilterTypeSelected;
            parameter = treeViewFilterParameterSelected;
        }
    }

    private void SetTreeViewFilterSelection(MainViewUpdateMode mode, object parameter)
    {
        if (mode == MainViewUpdateMode.PlayHistorySelected)
        {
            throw new InvalidOperationException("Play-history selection must be activated through PlayHistory.ActivatePeriod.");
        }
        if (!IsPlaylistViewMode(mode))
        {
            PlaylistWorkspace.ClearPlaylistDetailSelection();
        }
        playHistoryWorkflowOwner.Deactivate(() =>
        {
            lock (playHistoryViewRequestLock)
            {
                treeViewFilterTypeSelected = mode;
                treeViewFilterParameterSelected = IsPlaylistViewMode(mode) ? null : parameter;
            }
        });
        playHistoryWorkflowOwner.ClearSummaryFilters();
    }

    private string ResolveMainViewLr2PlayHistoryScoreDbPath()
    {
        StartupSettingsSnapshot settings = GetStartupSettingsSnapshot();
        if (!settings.OperationModeLR2DB)
        {
            return null;
        }
        if (lr2config == null
            && !string.IsNullOrWhiteSpace(settings.LR2ConfigXmlPath)
            && File.Exists(settings.LR2ConfigXmlPath))
        {
            lr2config = new LR2Config(settings.LR2ConfigXmlPath);
        }
        return Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(settings.LR2RootPath, () => lr2config?.GetPlayerId());
    }

    private string ResolveMainViewBeatorajaPlayHistoryScoreDbPath()
    {
        StartupSettingsSnapshot settings = GetStartupSettingsSnapshot();
        if (BeatorajaConfigService.IsBeatorajaRootPathValid(settings.BeatorajaRootPath))
        {
            return BeatorajaConfigService.GetScoreDbPath(settings.BeatorajaRootPath, settings.BeatorajaPlayerId);
        }
        return settings.BeatorajaScoreDbPath;
    }

    private BeatorajaPlayHistoryScoreContext ResolveBeatorajaPlayHistoryScoreContext()
    {
        BeMusicSeeker.Models.BMSLibrary.ScoreSnapshot scoreSnapshot = files?.GetScoreSnapshotForDiagnostics();
        if (scoreSnapshot?.ActiveScoreSource != ActiveScoreSource.Beatoraja)
        {
            return BeatorajaPlayHistoryScoreContext.Empty;
        }

        return new BeatorajaPlayHistoryScoreContext(scoreSnapshot.Version, scoreSnapshot.ScoresBySha256);
    }

    private PlayHistoryReadSourceContext ResolvePlayHistoryReadSourceContext()
    {
        StartupSettingsSnapshot settings = GetStartupSettingsSnapshot();
        string beatorajaScoreDbPath = settings.UseBeatorajaScoreDb
            ? ResolveMainViewBeatorajaPlayHistoryScoreDbPath()
            : string.Empty;
        ActiveScoreSource activeScoreSource = settings.UseBeatorajaScoreDb
            ? files?.GetActiveScoreSourceForDiagnostics() ?? ActiveScoreSource.None
            : ActiveScoreSource.None;
        bool beatorajaScoreDbFileExists = settings.UseBeatorajaScoreDb
            && !string.IsNullOrWhiteSpace(beatorajaScoreDbPath)
            && File.Exists(beatorajaScoreDbPath);
        BeatorajaPlayHistoryScoreContext beatorajaScoreContext =
            settings.UseBeatorajaScoreDb
            && activeScoreSource == ActiveScoreSource.Beatoraja
            && beatorajaScoreDbFileExists
                ? ResolveBeatorajaPlayHistoryScoreContext()
                : BeatorajaPlayHistoryScoreContext.Empty;
        return PlayHistoryReadSourceSelector.Select(
            new PlayHistoryReadSourceSelectionInput(
                ResolveMainViewLr2PlayHistoryScoreDbPath(),
                settings.OperationModeLR2DB,
                settings.UseBeatorajaScoreDb,
                beatorajaScoreDbPath,
                activeScoreSource,
                beatorajaScoreDbFileExists,
                beatorajaScoreContext));
    }

    /// <summary>
    /// Captures the exact active provider context used by playlist lamp historical reads.
    /// Unlike the play-history view selector this boundary never falls back to another provider.
    /// </summary>
    internal PlaylistLampHistoricalScoreSourceContext ResolvePlaylistLampHistoricalScoreSourceContext()
    {
        StartupSettingsSnapshot settings = GetStartupSettingsSnapshot();
        ActiveScoreSource activeScoreSource = files?.GetActiveScoreSourceForDiagnostics() ?? ActiveScoreSource.None;
        if (activeScoreSource == ActiveScoreSource.Lr2 && settings.OperationModeLR2DB)
        {
            return PlaylistLampHistoricalScoreSourceContext.Lr2(
                ResolveMainViewLr2PlayHistoryScoreDbPath(),
                isLr2LinkedProfile: true);
        }
        if (activeScoreSource == ActiveScoreSource.Beatoraja && settings.UseBeatorajaScoreDb)
        {
            BMSLibrary.ScoreSnapshot scoreSnapshot = files?.GetScoreSnapshotForDiagnostics();
            var notesBySha256 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, BMSScore> pair in scoreSnapshot?.ScoresBySha256 ?? new Dictionary<string, BMSScore>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value?.totalnotes > 0)
                {
                    notesBySha256[pair.Key] = pair.Value.totalnotes;
                }
            }
            return PlaylistLampHistoricalScoreSourceContext.Beatoraja(
                ResolveMainViewBeatorajaPlayHistoryScoreDbPath(),
                notesBySha256);
        }
        return null;
    }

    private static PlayHistoryDiagnostic CreatePlayHistoryDiagnostic(
        PlayHistoryDiagnosticSeverity severity,
        string stage,
        string code,
        string message,
        string sourcePath)
    {
        return CreatePlayHistoryDiagnostic(PlayHistoryProvider.Lr2, severity, stage, code, message, sourcePath);
    }

    private static PlayHistoryDiagnostic CreatePlayHistoryDiagnostic(
        PlayHistoryProvider provider,
        PlayHistoryDiagnosticSeverity severity,
        string stage,
        string code,
        string message,
        string sourcePath)
    {
        return new PlayHistoryDiagnostic
        {
            Provider = provider,
            Stage = stage ?? string.Empty,
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }

    private static bool ShouldIncludeBmsonLibraryRowsInMainView(MainViewUpdateMode mode, MainViewUpdateMode currentTreeMode)
    {
        return ChartListRefreshCoordinator.ShouldIncludeBmsonLibraryRows(mode, currentTreeMode);
    }

    private static object NormalizeDuplicateViewParameter(object parameter)
    {
        if (parameter == null || parameter is DuplicateViewContext)
        {
            return parameter;
        }
        if (parameter is DuplicateGroup duplicateGroup)
        {
            return DuplicateViewContext.ForGroup(duplicateGroup.Header);
        }
        if (parameter is string folderPath)
        {
            return DuplicateViewContext.ForFolder(folderPath);
        }
        return null;
    }

    private static string ResolveBmsonSourceGenerationReason(BmsonLibraryRowCacheSyncResult result, string reasonPrefix)
    {
        string prefix = string.IsNullOrWhiteSpace(reasonPrefix) ? "bmson" : reasonPrefix;
        if (result.MembershipChanged)
        {
            return prefix + "_membership_changed";
        }
        if (result.SourceIdentityChanged)
        {
            return NormalLibraryBmsonSourceIdentityChangedReason;
        }
        return prefix + "_source_reference_changed";
    }

    private void MaintenanceRescanWorkflowCompletionPublished(MaintenanceRescanCompletionReceipt receipt)
    {
        RefreshResourceHealthViewsAfterMaintenanceChanged(
            "maintenance_hydration_completed",
            "maintenance_changed",
            invalidateSortDependency: false);
    }

    private void SelectedChartResourceHealthRescanCompleted(object sender, EventArgs e)
    {
        RefreshResourceHealthViewsAfterMaintenanceChanged(
            "maintenance_hydration_completed",
            "maintenance_changed",
            invalidateSortDependency: false);
    }

    private static void ReportMaintenanceRescanWorkflowNotificationFailure(Exception exception)
    {
        NLogWrapper.FileLogger?.Error(exception, "maintenance_rescan_workflow_notification_failed");
    }

    private static void ReportMaintenanceRescanWorkflowFailure(Exception exception)
    {
        NLogWrapper.FileLogger?.Error(exception, "maintenance_rescan failed scope=all_owned");
    }

    private async void FolderAutoRenameWorkflowCompletionPublished(FolderAutoRenameCompletionReceipt receipt)
    {
        if (receipt?.RefreshRequired == true)
        {
            FileDbMutationReport.NotifyBestEffort(() =>
            {
                regularChartListOwner.QueueLatestNormalLibraryRefreshNotification("library_charts_changed");
                InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
            });
        }
        await FileDbMutationReport.ShowAsync(FileDbMutationDialogs,
            BeMusicSeeker.Properties.Resources.FileDbMutationReport_Rename, receipt?.SessionReceipt);
    }

    private async void FolderAutoRenameWorkflowFailurePublished(FolderAutoRenameFailure failure)
    {
        await FileDbMutationReport.ShowAsync(FileDbMutationDialogs,
            BeMusicSeeker.Properties.Resources.FileDbMutationReport_Rename,
            failure?.MutationResult?.SessionReceipt, failure?.Exception);
    }

    private static void ReportFolderAutoRenameWorkflowNotificationFailure(Exception exception)
    {
        NLogWrapper.FileLogger?.Error(exception, "folder_auto_rename_workflow_notification_failed");
    }

    private static void ReportFolderAutoRenameWorkflowFailure(Exception exception)
    {
        NLogWrapper.FileLogger?.Error(exception, "folder_auto_rename failed");
    }

    private void FolderAutoRenameWorkflowRefreshSuppressionChanged(
        object sender,
        FolderAutoRenameRefreshSuppressionChangedEventArgs e)
    {
        if (e?.IsSuppressed == true)
        {
            BeginUiUpdateSuppression(
                UiRefreshChannel.LibraryMainView
                | UiRefreshChannel.LibraryFolderTree
                | UiRefreshChannel.InstallTree
                | UiRefreshChannel.DuplicateTree);
        }
        else
        {
            EndUiUpdateSuppression();
        }
    }

    private void RegularChartListOwnerRefreshSuppressionChanged(
        object sender,
        RegularChartMutationRefreshSuppressionChangedEventArgs e)
    {
        if (e?.IsSuppressed == true)
        {
            BeginUiUpdateSuppression(
                UiRefreshChannel.LibraryMainView
                | UiRefreshChannel.LibraryFolderTree
                | UiRefreshChannel.InstallTree
                | UiRefreshChannel.DuplicateTree);
        }
        else
        {
            EndUiUpdateSuppression();
        }
    }

    private void RefreshResourceHealthViewsAfterMaintenanceChanged()
    {
        RefreshResourceHealthViewsAfterMaintenanceChanged("maintenance_hydration_completed", "maintenance_changed");
    }

    private void RefreshResourceHealthViewsAfterMaintenanceChanged(string reason)
    {
        RefreshResourceHealthViewsAfterMaintenanceChanged(reason, reason);
    }

    private void RefreshResourceHealthViewsAfterMaintenanceChanged(
        string filterReason,
        string dependencyReason,
        bool invalidateSortDependency = true)
    {
        if (invalidateSortDependency)
        {
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.Maintenance, NormalLibraryMaintenanceChangedReason);
        }
        Action refresh = delegate
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (treeViewFilterTypeSelected == MainViewUpdateMode.FileMissingFilterSelected
                || treeViewFilterTypeSelected == MainViewUpdateMode.FileMissingIgnoredFilterSelected
                || treeViewFilterTypeSelected == MainViewUpdateMode.FullScanAllChartsFilterSelected)
            {
                RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Maintenance, dependencyReason);
        };
        DispatchUiAction(refresh);
    }

    private void RefreshNormalLibraryAfterWarningChanged(string reason)
    {
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Warning, reason);
    }

    private void RefreshDuplicatePresentationAfterGroupsChanged(string reason)
    {
        string refreshReason = string.IsNullOrWhiteSpace(reason) ? "bms_files_duplicated_changed" : reason;
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.Warning, NormalLibraryWarningChangedReason);
        if (!TrySuppress(UiRefreshChannel.DuplicateTree)
            && !TrySuppress(UiRefreshChannel.DuplicateTree))
        {
            MaintenanceTree.ApplyDuplicateGroupsPresentation();
        }
        if (treeViewFilterTypeSelected == MainViewUpdateMode.DuplicateFilterSelected)
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (MaintenanceTree.DuplicateChartGroups == null)
            {
                regularChartListOwner.EnsureDuplicateChartGroupsReady(refreshReason);
                return;
            }
            RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
        }
        else
        {
            RefreshNormalLibraryAfterWarningChanged(refreshReason);
        }
    }

    private void RefreshNormalLibraryAfterInstallDestinationChanged(string reason)
    {
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.InstallDestination, reason);
    }

    private bool TryDispatchPackageInstallUi(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (uiScheduler.CanExecuteInline)
        {
            action();
            return true;
        }
        if (!uiScheduler.IsAvailable)
        {
            return false;
        }
        IUiScheduledOperation operation = uiScheduler.Schedule(
            action,
            UiSchedulePriority.Normal);
        if (!operation.IsAccepted)
        {
            return false;
        }
        _ = operation.Completion.ContinueWith(
            task => ReportPackageInstallWorkflowNotificationFailure(
                task.Exception?.GetBaseException()
                    ?? new OperationCanceledException(
                        "Package-install UI publication was canceled.")),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private async void PackageInstallWorkflowCompletionPublished(PackageInstallCompletionReceipt receipt)
    {
        try
        {
            if (receipt?.Packages.Count > 0)
            {
                FileDbMutationReport.NotifyBestEffort(() => PlaylistWorkspace.AttachInstalledPackageReferences(receipt.Packages));
            }
            await FileDbMutationReport.ShowAsync(FileDbMutationDialogs,
                BeMusicSeeker.Properties.Resources.Install, receipt?.SessionReceipt);
        }
        catch (Exception exception) { ReportPackageInstallWorkflowNotificationFailure(exception); }
    }

    private async void PackageInstallWorkflowFailurePublished(PackageInstallFailure failure)
    {
        try
        {
            if (failure?.Exception == null) { return; }
            if (failure.CommandResult?.SessionReceipt != null)
            {
                if (failure.CommandResult.RegisteredPackages.Count > 0)
                {
                    FileDbMutationReport.NotifyBestEffort(() => PlaylistWorkspace.AttachInstalledPackageReferences(failure.CommandResult.RegisteredPackages));
                }
                await FileDbMutationReport.ShowAsync(FileDbMutationDialogs,
                    BeMusicSeeker.Properties.Resources.Install, failure.CommandResult.SessionReceipt, failure.Exception);
                return;
            }
            UiDialogResult result = await FileDbMutationDialogs.ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_failed_installation + Environment.NewLine + failure.Exception.Message,
                BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
            UiDialogRoute.ThrowIfNotShown(result, "Package install failure notification");
        }
        catch (Exception exception)
        {
            // 非同期receiverの通知失敗はPublish側の同期catchへ戻らないため、この境界で報告します。
            ReportPackageInstallWorkflowNotificationFailure(exception);
        }
    }

    private void PackageInstallWorkflowRefreshSuppressionChanged(
        object sender,
        PackageInstallRefreshSuppressionChangedEventArgs e)
    {
        if (e?.IsSuppressed == true)
        {
            BeginUiUpdateSuppression(
                UiRefreshChannel.LibraryMainView
                | UiRefreshChannel.InstallTree
                | UiRefreshChannel.LibraryFolderTree
                | UiRefreshChannel.DuplicateTree);
        }
        else
        {
            EndUiUpdateSuppression();
        }
    }

    private static void ReportPackageInstallWorkflowNotificationFailure(Exception exception)
    {
        NLogWrapper.FileLogger?.Error(exception, "package_install_workflow_notification_failed");
    }



    private void UpdateInstallEstimationProgressStatus(InstallEstimationProgressSnapshot snapshot)
    {
        DispatchMainChartListAction(() => ProgressHub.UpdateInstallEstimationProgress(snapshot));
    }

    private void ReleaseDuplicateRefreshPriorityWindowAfterUiRefresh(string reason)
    {
        if (!uiScheduler.IsAvailable && uiScheduler.CanExecuteInline)
        {
            PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
            return;
        }

        try
        {
            IUiScheduledOperation operation = uiScheduler.Schedule(delegate
            {
                PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
            }, UiSchedulePriority.ApplicationIdle);
            if (!operation.IsAccepted)
            {
                PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
            }
        }
        catch
        {
            PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
        }
    }

    private static bool ShowUiConfirmation(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        MessageBoxButton button,
        string routeName = "UI confirmation dialog",
        MessageBoxResult defaultResult = MessageBoxResult.None,
        string warningMessageBoxText = null)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ConfirmAsync(new UiConfirmationRequest(
                messageBoxText,
                caption,
                button,
                icon,
                defaultResult,
                warningMessageBoxText: warningMessageBoxText))
            .GetAwaiter()
            .GetResult();
        return ToUiConfirmationDecision(result, routeName);
    }

    private static void ShowUiMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName = "UI message dialog",
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, MessageBoxButton.OK, icon, defaultResult))
            .GetAwaiter()
            .GetResult();
        ThrowIfUiDialogNotShown(result, routeName);
    }

    private sealed class DefaultStartupLibraryInitializationFailurePresenter
        : IStartupLibraryInitializationFailurePresenter
    {
        public void Present(StartupLibraryInitializationFailurePresentation presentation)
        {
            if (presentation == null)
            {
                throw new ArgumentNullException(nameof(presentation));
            }
            ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected
                    + Environment.NewLine
                    + presentation.Exception,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Startup library initialization failure notification");
        }
    }

    private static bool ToUiConfirmationDecision(UiDialogResult result, string routeName)
    {
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            UiDialogStatus.Failed => throw CreateUiDialogDisplayException(routeName, result),
            _ => throw CreateUiDialogDisplayException(routeName, result),
        };
    }

    private static void ThrowIfUiDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        if (result.Status == UiDialogStatus.Failed)
        {
            throw CreateUiDialogDisplayException(routeName, result);
        }

        throw CreateUiDialogDisplayException(routeName, result);
    }

    private static UiDialogDisplayException CreateUiDialogDisplayException(string routeName, UiDialogResult result)
    {
        string message = result.Status == UiDialogStatus.Failed
            ? routeName + " failed."
            : routeName + " was not shown: " + result.Status;
        return new UiDialogDisplayException(message, result.Exception);
    }

    private sealed class UiDialogDisplayException : InvalidOperationException
    {
        internal UiDialogDisplayException()
        {
        }

        internal UiDialogDisplayException(string message)
            : base(message)
        {
        }

        internal UiDialogDisplayException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

}
