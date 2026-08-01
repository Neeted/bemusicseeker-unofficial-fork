using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using NLog;
using Ribbit.BMS;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// BeMusicSeeker のメイン画面を制御する ViewModel です。
/// ライブラリ（BMSファイル群）やプレイリストの管理、各ビュー状態の維持、内蔵および外部BMSプレイヤー機能の連携のほか、
/// UI (MainWindow) とのデータバインディングやルーティングを担います。
/// </summary>
public partial class MainWindowViewModel : ViewModel,
    ISettingsDialogStatePort
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

    internal RankingCacheDownloadWorkflowOwner RankingCacheDownloadWorkflow { get; private set; }

    internal PendingPackageWorkflowOwner PendingPackages { get; private set; }

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

    private const UiRefreshChannel StartupDeferredPresentationChannels =
        UiRefreshChannel.LibraryMainView
        | UiRefreshChannel.LibraryFolderTree
        | UiRefreshChannel.PlaylistTree
        | UiRefreshChannel.DuplicateTree;

    private const UiRefreshChannel StartupBasicPresentationChannels =
        UiRefreshChannel.LibraryFolderTree
        | UiRefreshChannel.PlaylistTree;

    private const string StartupUiSuppressFlushReason = "startup_ui_suppress_flush";


    private bool initializationCompleted;

    public bool IsInitializationCompleted => initializationCompleted;

    private bool hasActiveLibraryProfile;

    public bool HasActiveLibraryProfile => hasActiveLibraryProfile;

    private bool bmsonMigrationApprovedForSession;

    private bool initialSetupCompletionMessagePending;

    private BMSLibrary files;

    private BMSPlaylist tables;

    private event EventHandler libraryOperationAvailabilityChanged;

    private event Action<Lr2PlayHistorySchemaStatusSnapshot> lr2PlayHistorySchemaStatusChanged;

    Task<bool> ISettingsDialogStatePort.InitializeLibraryAsync()
        => InitializeAsync();

    Task ISettingsDialogStatePort.ReloadScoresOnlyAsync()
        => ReloadScoresOnlyAsync();

    Task ISettingsDialogStatePort.ReloadFileDiffAsync()
        => ReloadFileDiffAsync();

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

    private readonly IUiScheduler uiScheduler;

    private readonly IApplicationLifetimePort applicationLifetime;

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

    private StartupProgressVersionSnapshot CaptureStartupProgressVersionSnapshot()
    {
        return new StartupProgressVersionSnapshot
        {
            ScoreHydrationCompletedVersion = files?.ScoreHydrationCompletedVersion ?? 0,
            ScoreHydrationRequestedVersion = files?.ScoreHydrationRequestedVersion ?? 0,
            RankingRefreshCompletedVersion = files?.RankingRefreshCompletedVersion ?? 0,
            RankingRefreshRequestedVersion = files?.RankingRefreshRequestedVersion ?? 0,
            MaintenanceHydrationRequestedVersion = files?.MaintenanceHydrationRequestedVersion ?? 0,
            InstallableMaintenanceDeferredRequestedVersion = files?.InstallableMaintenanceDeferredRequestedVersion ?? 0,
            ChartDigestBackfillCompletedVersion = files?.ChartDigestBackfillCompletedVersion ?? 0,
            ChartInfoBackfillCompletedVersion = files?.ChartInfoBackfillCompletedVersion ?? 0,
            ChartInfoHydrationCompletedVersion = files?.ChartInfoHydrationCompletedVersion ?? 0,
            ChartInfoBackfillRequestedVersion = files?.ChartInfoBackfillRequestedVersion ?? 0,
            Lr2SongDbSyncCompletedVersion = files?.Lr2SongDbSyncCompletedVersion ?? 0,
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
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (lockUiSuppression)
            {
                deferredStartupPresentationMask = UiRefreshChannel.None;
                deferredStartupPresentationOperationToken = operationToken;
                deferredStartupPresentationInFlightMask = UiRefreshChannel.None;
                deferredStartupPresentationInFlightOperationToken = 0L;
            }
            startupBackgroundTaskScheduler.Reset(
                operationKind != StartupProgressOperationKind.Startup && startupReadyOperableReached);
            lock (startupInitializationCompletionLock)
            {
                startupInitializationCompleteStopwatch = operationKind == StartupProgressOperationKind.Startup
                    ? Stopwatch.StartNew()
                    : null;
                startupInitializationCompleteLogged = false;
                startupPostInitializationCompletionTracking = operationKind == StartupProgressOperationKind.Startup;
                startupPostInitializationCompletionLogged = false;
                startupPostInitializationWarmupOperationToken = 0L;
                startupPostInitializationWarmupSchedulerGeneration = 0L;
                startupPostInitializationWarmupScheduled = false;
                startupPostInitializationWarmupsPending = 0;
                startupPostInitializationVirtualWarmupScheduled = false;
                startupPostInitializationLr2Enrolled = false;
                startupInitializationCompleteRetryQueued = false;
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

    private bool IsStartupRequiredBackgroundTaskEnrollmentComplete()
    {
        lock (startupInitializationCompletionLock)
        {
            return !startupPostInitializationCompletionTracking
                || startupPostInitializationLr2Enrolled;
        }
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
            ShowInitialSetupCompletionMessageIfPending(expectedOperationToken, requireBackgroundTasksIdle: true);
            return;
        }
        bool completionAlreadyLogged;
        lock (startupInitializationCompletionLock)
        {
            completionAlreadyLogged = startupInitializationCompleteLogged;
        }
        if (completionAlreadyLogged)
        {
            ShowInitialSetupCompletionMessageIfPending(expectedOperationToken, requireBackgroundTasksIdle: true);
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
                startupCompletionContinuationToken = expectedOperationToken;
                if (startupInitializationCompleteLogged || startupInitializationCompleteStopwatch == null)
                {
                    return;
                }
                if ((startupPostInitializationCompletionTracking && !startupPostInitializationLr2Enrolled)
                    || !startupProgressWorkflowOwner.IsStartupInitializationRequiredProgressComplete(expectedOperationToken)
                    || !startupBackgroundTaskScheduler.IsStarted
                    || !startupBackgroundTaskScheduler.IsIdle)
                {
                    QueueStartupInitializationCompleteRetryUnsafe(expectedOperationToken);
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
        SchedulePostStartupBestEffortWarmups("startup_initialization_complete", expectedOperationToken);
        QueueDeferredStartupPresentationFlushAfterInitialization(expectedOperationToken);
    }

    private void TryLogStartupPostInitializationComplete()
    {
        if (ShellShutdownWorkflow?.IsShutdownRequested == true
            || !startupBackgroundTaskScheduler.IsStarted
            || !startupBackgroundTaskScheduler.IsPostInitializationSchedulingComplete
            || !startupBackgroundTaskScheduler.IsFullyIdle)
        {
            return;
        }
        bool shouldLog;
        lock (startupInitializationCompletionLock)
        {
            shouldLog = startupPostInitializationCompletionTracking
                && startupInitializationCompleteLogged
                && startupPostInitializationWarmupScheduled
                && startupPostInitializationVirtualWarmupScheduled
                && startupPostInitializationWarmupsPending == 0
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
        LogUiSuppression("startup_post_initialization_maintenance_complete");
        if (Net10PerformanceLog.IsEnabled && startupPerformanceInteraction.InteractionId > 0L)
        {
            Net10PerformanceLog.Write(
                startupPerformanceInteraction,
                "post_initialization_maintenance_complete",
                "kind=post_initialization");
        }
    }

    private void CompleteStartupPostInitializationWarmup(long operationToken, long schedulerGeneration)
    {
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
            if (startupPostInitializationWarmupsPending > 0)
            {
                startupPostInitializationWarmupsPending--;
            }
        }
        TryLogStartupPostInitializationComplete();
    }

    private bool QueueDeferredStartupPresentationFlushAfterInitialization(long expectedOperationToken = 0L)
    {
        if (expectedOperationToken != 0L
            && !IsStartupCompletionTokenCurrent(expectedOperationToken))
        {
            return false;
        }
        if (!TryTakeDeferredStartupPresentationMask(expectedOperationToken, out UiRefreshChannel mask))
        {
            return false;
        }
        Action flush = delegate
        {
            if (expectedOperationToken != 0L
                && !IsStartupCompletionTokenCurrent(expectedOperationToken))
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            LogUiSuppression("startup_presentation_flush start mask=" + mask);
            long flushOperationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken() == expectedOperationToken
                ? expectedOperationToken
                : 0L;
            FlushPendingUiRefresh(mask, flushOperationToken, allowStartupPresentationDefer: false, logReadiness: false);
            CompleteDeferredStartupPresentationFlush(expectedOperationToken);
            stopwatch.Stop();
            LogUiSuppression("startup_presentation_flush done elapsedMs=" + stopwatch.ElapsedMilliseconds + " mask=" + mask);
            SchedulePostStartupBestEffortWarmups("startup_presentation_flush_done", expectedOperationToken);
        };
        DispatchUiAction(flush);
        return true;
    }

    private void ShowInitialSetupCompletionMessageIfPending(
        long expectedOperationToken = 0L,
        bool requireBackgroundTasksIdle = false)
    {
        if (expectedOperationToken != 0L
            && !IsStartupCompletionTokenCurrent(expectedOperationToken))
        {
            return;
        }
        if (!initialSetupCompletionMessagePending)
        {
            return;
        }
        if (requireBackgroundTasksIdle
            && (!startupBackgroundTaskScheduler.IsStarted || !startupBackgroundTaskScheduler.IsIdle))
        {
            lock (startupInitializationCompletionLock)
            {
                QueueStartupInitializationCompleteRetryUnsafe(expectedOperationToken);
            }
            return;
        }
        Action showMessage = delegate
        {
            if (expectedOperationToken != 0L
                && !IsStartupCompletionTokenCurrent(expectedOperationToken))
            {
                return;
            }
            if (requireBackgroundTasksIdle
                && (!startupBackgroundTaskScheduler.IsStarted || !startupBackgroundTaskScheduler.IsIdle))
            {
                lock (startupInitializationCompletionLock)
                {
                    QueueStartupInitializationCompleteRetryUnsafe(expectedOperationToken);
                }
                return;
            }
            if (!initialSetupCompletionMessagePending)
            {
                return;
            }
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, MessageBoxImage.Asterisk, "Initial setup completion notification");
            initialSetupCompletionMessagePending = false;
        };
        DispatchUiAction(showMessage);
    }

    private void QueueStartupInitializationCompleteRetryUnsafe(long expectedOperationToken = 0L)
    {
        if (startupInitializationCompleteRetryQueued)
        {
            return;
        }
        startupInitializationCompleteRetryQueued = true;
        Task.Run(async delegate
        {
            await Task.Delay(250).ConfigureAwait(false);
            lock (startupInitializationCompletionLock)
            {
                startupInitializationCompleteRetryQueued = false;
            }
            TryLogStartupInitializationComplete(expectedOperationToken);
        });
    }

    private LR2Config lr2config;

    private PropertyChangedEventListener listenerForBMSLibrary;

    private readonly object lockThis = new();

    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ChartFileOperationSynchronizer chartFileOperations = new();

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel deferredStartupPresentationMask = UiRefreshChannel.None;

    private long deferredStartupPresentationOperationToken;

    private UiRefreshChannel deferredStartupPresentationInFlightMask = UiRefreshChannel.None;

    private long deferredStartupPresentationInFlightOperationToken;

    private UiRefreshChannel playlistPresentationRefreshApplyInFlight = UiRefreshChannel.None;

    private readonly object lockUiSuppression = new();

    private readonly object startupInitializationCompletionLock = new();

    private readonly object startupBackgroundTaskProgressSynchronization = new();

    private readonly StartupBackgroundTaskSchedulerOwner startupBackgroundTaskScheduler;

    private Stopwatch startupInitializationCompleteStopwatch;

    private bool startupInitializationCompleteLogged;

    private bool startupPostInitializationCompletionTracking;

    private bool startupPostInitializationCompletionLogged;

    private long startupPostInitializationWarmupOperationToken;

    private long startupPostInitializationWarmupSchedulerGeneration;

    private bool startupPostInitializationWarmupScheduled;

    private int startupPostInitializationWarmupsPending;

    private bool startupPostInitializationVirtualWarmupScheduled;

    private bool startupPostInitializationLr2Enrolled;

    private bool startupInitializationCompleteRetryQueued;

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

    private long lastMainViewBuildRequestId;

    private long lastMainViewBuildEndTimestamp;

    private int lastMainViewBuildThreadId;

    private int lastMainViewBuildMode;

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
            PerformanceInteraction interaction = PerformanceInteraction.Start("main_view");
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

    private static void LogPlayHistoryEvent(string eventName, string message)
    {
        LogMainViewBuild((eventName ?? "play_history_event") + " " + (message ?? string.Empty));
    }

    /// <summary>
    /// プレイリスト source build の診断ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistSourceBuild(string message)
    {
        LogMainViewBuild("playlist_source_build " + message);
    }

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
            MaintenanceHydrationRunning = files?.MaintenanceHydrationRunning ?? false,
            MaintenanceHydrationLastCompletedVersion = files?.MaintenanceHydrationCompletedVersion ?? 0,
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

    private void RequestUiRefresh(UiRefreshChannel channel)
    {
        TrySuppress(channel);
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

    private bool TryDeferStartupPresentationRefresh(UiRefreshChannel channel, string reason)
    {
        if (channel == UiRefreshChannel.None)
        {
            return false;
        }
        long operationToken;
        UiRefreshChannel deferredChannel = channel & StartupDeferredPresentationChannels;
        if (deferredChannel == UiRefreshChannel.None)
        {
            return false;
        }
        if (!TryAddDeferredStartupPresentationMask(deferredChannel, out operationToken, out UiRefreshChannel pendingMask))
        {
            return false;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return true;
    }

    private UiRefreshChannel DeferStartupPresentationChannels(UiRefreshChannel mask, long operationToken, string reason)
    {
        UiRefreshChannel deferredChannel = GetStartupPresentationDeferredChannels(mask, reason, CanShowStartupBasicLibraryMainView(treeViewFilterTypeSelected));
        if (deferredChannel == UiRefreshChannel.None
            || !TryAddDeferredStartupPresentationMask(deferredChannel, operationToken, out UiRefreshChannel pendingMask))
        {
            return mask;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return mask & ~deferredChannel;
    }

    private static bool CanShowStartupBasicLibraryMainView(MainViewUpdateMode currentTreeMode)
    {
        return currentTreeMode == MainViewUpdateMode.FolderFilterSelected
            || currentTreeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected;
    }

    private bool TryAddDeferredStartupPresentationMask(
        UiRefreshChannel deferredChannel,
        out long operationToken,
        out UiRefreshChannel pendingMask)
    {
        lock (startupBackgroundTaskProgressSynchronization)
        {
            operationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
            return TryAddDeferredStartupPresentationMask(deferredChannel, operationToken, out pendingMask);
        }
    }

    private bool TryAddDeferredStartupPresentationMask(
        UiRefreshChannel deferredChannel,
        long operationToken,
        out UiRefreshChannel pendingMask)
    {
        pendingMask = UiRefreshChannel.None;
        if (deferredChannel == UiRefreshChannel.None)
        {
            return false;
        }
        lock (startupBackgroundTaskProgressSynchronization)
        {
            if (operationToken == 0L
                || !startupProgressWorkflowOwner.IsStartupProgressOperationTokenCurrent(operationToken)
                || !startupProgressWorkflowOwner.IsOperationActive
                || startupProgressWorkflowOwner.CurrentOperationKind != StartupProgressOperationKind.Startup)
            {
                return false;
            }
            lock (startupInitializationCompletionLock)
            {
                if (startupInitializationCompleteLogged)
                {
                    return false;
                }
                lock (lockUiSuppression)
                {
                    NormalizeDeferredStartupPresentationMaskUnsafe(operationToken);
                    deferredStartupPresentationOperationToken = operationToken;
                    deferredStartupPresentationMask |= deferredChannel;
                    pendingMask = deferredStartupPresentationMask;
                    return true;
                }
            }
        }
    }

    private bool TryTakeDeferredStartupPresentationMask(
        long expectedOperationToken,
        out UiRefreshChannel mask)
    {
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (lockUiSuppression)
            {
                if (expectedOperationToken != 0L
                    && deferredStartupPresentationOperationToken != expectedOperationToken)
                {
                    mask = UiRefreshChannel.None;
                    return false;
                }
                mask = deferredStartupPresentationMask;
                deferredStartupPresentationMask = UiRefreshChannel.None;
                deferredStartupPresentationOperationToken = 0L;
                if (mask != UiRefreshChannel.None)
                {
                    deferredStartupPresentationInFlightMask = mask;
                    deferredStartupPresentationInFlightOperationToken = expectedOperationToken;
                }
                return mask != UiRefreshChannel.None;
            }
        }
    }

    private void CompleteDeferredStartupPresentationFlush(long operationToken)
    {
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                lock (lockUiSuppression)
                {
                    if (deferredStartupPresentationInFlightOperationToken == operationToken)
                    {
                        deferredStartupPresentationInFlightMask = UiRefreshChannel.None;
                        deferredStartupPresentationInFlightOperationToken = 0L;

                        // Clearing the in-flight marker and draining work that arrived during
                        // the flush must be one arbitration interval.  Otherwise a request
                        // reentrant with the final drain can observe the old marker, queue
                        // itself, and remain stranded after the marker is cleared.
                        while (!IsPlaylistSummaryRefreshDeferredNowUnsafe()
                            && PlaylistWorkspace.HasDeferredPlaylistSummaryRefresh())
                        {
                            PlaylistWorkspace.DrainDeferredPlaylistSummaryRefresh(
                                dataRefreshRequired: false,
                                rebuildAsync: true);
                        }
                    }
                }
            }
        }
    }

    private static UiRefreshChannel GetStartupPresentationDeferredChannels(UiRefreshChannel mask, string reason, bool includeBasicLibraryMainView)
    {
        return (UiRefreshChannel)StartupPresentationPolicy.GetDeferredPresentationChannels(
            (int)mask,
            string.Equals(reason, StartupUiSuppressFlushReason, StringComparison.Ordinal),
            includeBasicLibraryMainView);
    }

    private bool IsPlaylistSummaryPresentationRefreshDeferred(out bool applyReservation)
    {
        applyReservation = false;
        bool deferred;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            bool startupCompletionLogged;
            lock (startupInitializationCompletionLock)
            {
                startupCompletionLogged = startupInitializationCompleteLogged;
            }
            lock (lockUiSuppression)
            {
                long activeOperationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
                long operationToken = activeOperationToken != 0L
                    ? activeOperationToken
                    : startupCompletionContinuationToken;
                NormalizeDeferredStartupPresentationMaskUnsafe(operationToken);
                bool startupOperationActive = !startupCompletionLogged
                    && operationToken != 0L
                    && startupProgressWorkflowOwner.IsOperationActive
                    && startupProgressWorkflowOwner.CurrentOperationKind == StartupProgressOperationKind.Startup;
                UiRefreshChannel summaryMask = UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView;
                bool startupPresentationFlushInFlight = operationToken != 0L
                    && deferredStartupPresentationInFlightOperationToken == operationToken
                    && (deferredStartupPresentationInFlightMask & summaryMask) != UiRefreshChannel.None;
                if (startupOperationActive)
                {
                    deferredStartupPresentationOperationToken = operationToken;
                    deferredStartupPresentationMask |= summaryMask;
                }
                bool startupPresentationDeferred = operationToken != 0L
                    && (startupOperationActive
                        || startupPresentationFlushInFlight
                        || (deferredStartupPresentationOperationToken == operationToken
                            && (deferredStartupPresentationMask & summaryMask) != UiRefreshChannel.None));
                deferred = suppressUiUpdateDepth > 0 || startupPresentationDeferred;
                if (!deferred
                    && (playlistPresentationRefreshApplyInFlight & summaryMask) != UiRefreshChannel.None)
                {
                    deferred = true;
                }
                if (!deferred)
                {
                    playlistPresentationRefreshApplyInFlight |= summaryMask;
                    applyReservation = true;
                }
            }
        }
        return deferred;
    }

    private bool IsPlaylistSummaryDataRefreshDeferred(out bool applyReservation)
    {
        applyReservation = false;
        bool deferred;
        lock (startupBackgroundTaskProgressSynchronization)
        {
            bool startupCompletionLogged;
            lock (startupInitializationCompletionLock)
            {
                startupCompletionLogged = startupInitializationCompleteLogged;
            }
            lock (lockUiSuppression)
            {
                long activeOperationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
                long operationToken = activeOperationToken != 0L
                    ? activeOperationToken
                    : startupCompletionContinuationToken;
                NormalizeDeferredStartupPresentationMaskUnsafe(operationToken);
                bool startupOperationActive = !startupCompletionLogged
                    && operationToken != 0L
                    && startupProgressWorkflowOwner.IsOperationActive
                    && startupProgressWorkflowOwner.CurrentOperationKind == StartupProgressOperationKind.Startup;
                UiRefreshChannel summaryMask = UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView;
                bool startupPresentationFlushInFlight = operationToken != 0L
                    && deferredStartupPresentationInFlightOperationToken == operationToken
                    && (deferredStartupPresentationInFlightMask & summaryMask) != UiRefreshChannel.None;
                if (startupOperationActive)
                {
                    deferredStartupPresentationOperationToken = operationToken;
                    deferredStartupPresentationMask |= summaryMask;
                }
                deferred = suppressUiUpdateDepth > 0
                    || (operationToken != 0L
                        && (startupOperationActive
                            || startupPresentationFlushInFlight
                            || (deferredStartupPresentationOperationToken == operationToken
                        && (deferredStartupPresentationMask & summaryMask) != UiRefreshChannel.None)));
                if (!deferred
                    && (playlistPresentationRefreshApplyInFlight & summaryMask) != UiRefreshChannel.None)
                {
                    deferred = true;
                }
                if (!deferred)
                {
                    playlistPresentationRefreshApplyInFlight |= summaryMask;
                    applyReservation = true;
                }
                if (suppressUiUpdateDepth > 0)
                {
                    pendingUiRefreshMask |= suppressedUiRefreshMask & summaryMask;
                }
            }
        }
        return deferred;
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

    private bool IsPlaylistSummaryRefreshDeferredNow()
    {
        lock (startupBackgroundTaskProgressSynchronization)
        {
            lock (startupInitializationCompletionLock)
            {
                lock (lockUiSuppression)
                {
                    return IsPlaylistSummaryRefreshDeferredNowUnsafe();
                }
            }
        }
    }

    // The caller holds startupBackgroundTaskProgressSynchronization,
    // startupInitializationCompletionLock, and lockUiSuppression.
    private bool IsPlaylistSummaryRefreshDeferredNowUnsafe()
    {
        long activeOperationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
        long operationToken = activeOperationToken != 0L
            ? activeOperationToken
            : startupCompletionContinuationToken;
        NormalizeDeferredStartupPresentationMaskUnsafe(operationToken);
        bool startupOperationActive = !startupInitializationCompleteLogged
            && operationToken != 0L
            && startupProgressWorkflowOwner.IsOperationActive
            && startupProgressWorkflowOwner.CurrentOperationKind == StartupProgressOperationKind.Startup;
        UiRefreshChannel summaryMask = UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView;
        bool startupPresentationFlushInFlight = operationToken != 0L
            && deferredStartupPresentationInFlightOperationToken == operationToken
            && (deferredStartupPresentationInFlightMask & summaryMask) != UiRefreshChannel.None;
        bool startupPresentationDeferred = operationToken != 0L
            && (startupOperationActive
                || startupPresentationFlushInFlight
                || (deferredStartupPresentationOperationToken == operationToken
                    && (deferredStartupPresentationMask & summaryMask) != UiRefreshChannel.None));
        return suppressUiUpdateDepth > 0 || startupPresentationDeferred;
    }

    private bool IsPlaylistTreePresentationDeferred(string reason)
    {
        if (TrySuppress(UiRefreshChannel.PlaylistTree))
        {
            return true;
        }
        return TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, reason);
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

    private void NormalizeDeferredStartupPresentationMaskUnsafe(long operationToken)
    {
        if (deferredStartupPresentationMask != UiRefreshChannel.None
            && deferredStartupPresentationOperationToken != operationToken)
        {
            deferredStartupPresentationMask = UiRefreshChannel.None;
            deferredStartupPresentationOperationToken = 0L;
        }
    }

    private void EndUiUpdateSuppression()
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

    private void TryLogStartupReadyData()
    {
        TryLogStartupReadyData(startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
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
        startupReadyDataReached = true;
        startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData, operationToken);
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
        startupReadyUiReached = true;
        startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyUi, operationToken);
    }

    private static bool IsStartupReadyUiMaskSatisfied(UiRefreshChannel mask)
    {
        return StartupPresentationPolicy.IsReadyUiMaskSatisfied(
            installTree: (mask & UiRefreshChannel.InstallTree) != 0,
            libraryMainView: (mask & UiRefreshChannel.LibraryMainView) != 0,
            playlistTree: (mask & UiRefreshChannel.PlaylistTree) != 0);
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

    private void TryLogStartupReadyOperable()
    {
        TryLogStartupReadyOperable(startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyOperable(long operationToken)
    {
        if (startupReadyOperableStopwatch == null || !startupReadyUiReached)
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
        startupReadyOperableReached = true;
        startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
        startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, operationToken);
        startupBackgroundTaskScheduler.Start();
        startupProgressWorkflowOwner.TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);
    }

    private void RefreshLibraryMainViewForCurrentFilter()
    {
        if (RegularChartListOwner.IsMaintenanceNavigationMode(treeViewFilterTypeSelected))
        {
            regularChartListOwner.NavigateMaintenance(
                treeViewFilterTypeSelected,
                treeViewFilterParameterSelected,
                "refresh_current_filter");
        }
        else
        {
            RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
        }
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
                    if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, reason))
                    {
                        return;
                    }
                    RefreshLibraryMainViewForCurrentFilter();
                }
            }
            return;
        }
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, reason))
        {
            return;
        }
        RefreshLibraryMainViewForCurrentFilter();
    }

    private bool TryRefreshMainViewDisplayForDataDependency(MainViewDataDependency dependency)
    {
        if (MainChartList.Rows is not ChartListVirtualView virtualView)
        {
            return false;
        }
        virtualView.ForEachRealizedRow(row => row.RefreshDisplayForDataDependency(dependency));
        MainChartList.RequestDisplayRefresh();
        return true;
    }

    private void RefreshChartInfoDependentViews()
    {
        MainChartList.RowProjection.CaptureVersions(files);
        BmsonLibraryRowCacheSyncResult bmsonSyncResult = regularChartListOwner.SyncBmsonRows(files);
        MainViewDataDependency libraryDependency = bmsonSyncResult.SourceChanged
            ? MainViewDataDependency.SourceMembership
            : MainViewDataDependency.ChartInfo;
        if (bmsonSyncResult.SortKeyChanged)
        {
            InvalidateNormalLibrarySortKeysForBmsonSync(bmsonSyncResult);
        }
        if (bmsonSyncResult.SourceChanged)
        {
            IncrementNormalLibrarySourceGenerationForBmsonSync(bmsonSyncResult, "bmson");
        }
        regularChartListOwner.ResetDerivedCaches();
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "chart_info_dependent_views"))
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
        if (StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
                e.Origin == LibraryFolderTreeRefreshRequestOrigin.DeferredContinuation,
                e.OperationToken,
                activeOperationToken,
                startupOperationActive)
            && TryDeferStartupPresentationRefresh(
                UiRefreshChannel.LibraryFolderTree,
                "parent_folder_cache_changed"))
        {
            return;
        }
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
        FlushPendingUiRefresh(mask, operationToken, allowStartupPresentationDefer: true, logReadiness: true);
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask, long operationToken, bool allowStartupPresentationDefer, bool logReadiness)
    {
        LogUiSuppression("ui_suppress flush mask=" + mask);
        UiRefreshChannel requestedMask = mask;
        if (allowStartupPresentationDefer)
        {
            mask = DeferStartupPresentationChannels(mask, operationToken, "startup_ui_suppress_flush");
        }
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
            RefreshLibraryMainViewForCurrentFilter();
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
                if (mutationApplied.ChangedCharts.Count > 0)
                {
                    MainChartList.RowProjection.UpdateTransientStates(
                        mutationApplied.ChangedCharts,
                        forceInstallDestinationProjection: true);
                }
                if (mutationApplied.InstallDestinationStateChanged)
                {
                    InvalidateNormalLibrarySortDependency(
                        MainViewDataDependency.InstallDestination,
                        NormalLibraryInstallDestinationChangedReason);
                }
                if (mutationApplied.IdentitySortKeyChanged)
                {
                    RefreshLibraryMainViewForDataDependency(
                        MainViewDataDependency.IdentitySortKey,
                        NormalLibraryInstallDestinationChangedReason);
                }
                if (mutationApplied.DisplayStateChanged)
                {
                    MainChartList.RequestDisplayRefresh();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e, "Unsupported pending-package workflow change.");
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

    private void RefreshNormalLibraryForNotificationPresentationEffects(NormalLibraryRefreshNotificationBatch notificationBatch)
    {
        if (notificationBatch?.HasRefreshNotification != true)
        {
            return;
        }
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged))
        {
            RefreshNormalLibraryAfterInstallDestinationChanged("normal_library_install_destination_changed");
        }
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged))
        {
            RefreshNormalLibraryAfterWarningChanged("normal_library_warning_changed");
        }
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
        {
            RefreshResourceHealthViewsAfterMaintenanceChanged(
                "normal_library_maintenance_changed",
                "normal_library_maintenance_changed",
                invalidateSortDependency: false);
        }
    }

    private void RefreshNormalLibraryAfterSourceChanged(string reason)
    {
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                reason);
            return;
        }
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree, reason))
        {
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                reason);
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
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                reason);
            return;
        }
        RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
        PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
            reason);
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
            files?.OwnedChartCollectionVersion ?? 0,
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
                SyncBmsonLibraryRowCacheWithoutRebuild(reason);
            }
        }
    }

    private void ClearNormalLibrarySortCache()
    {
        int cacheCount = regularChartListOwner.CacheCount;
        regularChartListOwner.ClearSortCache();
        LogNormalLibrarySortCacheInvalidation("clear", "explicit", cacheCount);
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

    private void ClearVirtualNormalLibrarySourceRows()
    {
        regularChartListOwner.InvalidateVirtualSourceRows();
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

    private void SchedulePostStartupBestEffortWarmups(string reason, long operationToken)
    {
        Action warmupCompleted = null;
        Action lr2WarmupCompleted = null;
        bool scheduleLr2Warmup = false;
        bool scheduleVirtualWarmup = false;
        long schedulerGeneration = 0L;
        lock (startupInitializationCompletionLock)
        {
            if (startupPostInitializationCompletionTracking)
            {
                if (startupPostInitializationWarmupScheduled
                    && startupPostInitializationWarmupOperationToken == operationToken)
                {
                    if (startupInitializationCompleteLogged
                        && !startupPostInitializationVirtualWarmupScheduled)
                    {
                        schedulerGeneration = startupPostInitializationWarmupSchedulerGeneration;
                        warmupCompleted = () => CompleteStartupPostInitializationWarmup(operationToken, schedulerGeneration);
                        startupPostInitializationVirtualWarmupScheduled = true;
                        startupPostInitializationWarmupsPending++;
                        scheduleVirtualWarmup = true;
                    }
                }
                else
                {
                    startupPostInitializationWarmupOperationToken = operationToken;
                    startupPostInitializationWarmupScheduled = true;
                    startupPostInitializationWarmupsPending = 1;
                    startupPostInitializationVirtualWarmupScheduled = false;
                    schedulerGeneration = startupBackgroundTaskScheduler.CurrentGeneration;
                    startupPostInitializationWarmupSchedulerGeneration = schedulerGeneration;
                    warmupCompleted = () => CompleteStartupPostInitializationWarmup(operationToken, schedulerGeneration);
                    scheduleLr2Warmup = true;
                }
            }
            else
            {
                scheduleLr2Warmup = true;
                scheduleVirtualWarmup = true;
            }
        }
        if (warmupCompleted != null)
        {
            lr2WarmupCompleted = delegate
            {
                if (!IsCurrentStartupPostInitializationCallback(
                        operationToken,
                        schedulerGeneration,
                        IsStartupCompletionTokenCurrent,
                        startupBackgroundTaskScheduler.IsCurrentGeneration)
                    || !startupBackgroundTaskScheduler.MarkRequiredInitializationSchedulingComplete(schedulerGeneration))
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
                    startupPostInitializationLr2Enrolled = true;
                }
                warmupCompleted();
                startupProgressWorkflowOwner.TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);
                TryLogStartupInitializationComplete(operationToken);
            };
        }
        if (scheduleLr2Warmup)
        {
            Func<Action, Task> startupScheduler = null;
            if (lr2WarmupCompleted != null)
            {
                startupScheduler = action =>
                {
                    bool accepted = startupBackgroundTaskScheduler.Queue(
                        "lr2_song_db_sync_enrollment",
                        reason,
                        null,
                        () =>
                        {
                            action();
                            return Task.CompletedTask;
                        },
                        _ => lr2WarmupCompleted());
                    if (!accepted)
                    {
                        lr2WarmupCompleted();
                    }
                    return Task.CompletedTask;
                };
            }
            Lr2SongDbSyncWorkflow.SchedulePostStartupSync(
                reason,
                lr2WarmupCompleted,
                startupScheduler);
        }
        if (scheduleVirtualWarmup)
        {
            ScheduleVirtualNormalLibraryOrderPrewarm(reason, warmupCompleted);
        }
        TryLogStartupPostInitializationComplete();
    }

    private void RunPostStartupOwnedAdjacentIndexWarmup(int runId, string reason, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogMainViewBuild("post_startup_warmup start reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " stage=owned_adjacent_index");
            BMSLibrary library = files;
            if (library == null)
            {
                stopwatch.Stop();
                LogMainViewBuild("post_startup_warmup skipped reason=" + (reason ?? string.Empty)
                    + " runId=" + runId
                    + " stage=owned_adjacent_index"
                    + " skipReason=no_library"
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }

            BMSLibrary.OwnedAdjacentIndexWarmupResult realPathResult = library.WarmOwnedRealPathDirectoryView("post_startup_" + (reason ?? string.Empty));
            cancellationToken.ThrowIfCancellationRequested();
            BMSLibrary.OwnedAdjacentIndexWarmupResult installDestinationOverlayResult = library.WarmInstallDestinationOverlaySnapshot("post_startup_" + (reason ?? string.Empty));
            cancellationToken.ThrowIfCancellationRequested();
            BMSLibrary.InstalledPrimaryHashWarmupResult primaryHashResult = library.WarmInstalledPrimaryHashLookup("post_startup_" + (reason ?? string.Empty));
            cancellationToken.ThrowIfCancellationRequested();
            OwnedHashIndexWarmupResult playlistSummaryResult = library.WarmOwnedChartHashIndexSnapshot("post_startup_" + (reason ?? string.Empty));
            stopwatch.Stop();
            LogMainViewBuild("post_startup_warmup done reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " stage=owned_adjacent_index"
                + " installedPrimaryStatus=" + (primaryHashResult?.Status ?? "(null)")
                + " installedPrimaryHashes=" + (primaryHashResult?.PrimaryHashCount ?? 0)
                + " installedPrimaryFiles=" + (primaryHashResult?.BmsCount ?? 0)
                + " installedPrimaryBmson=" + (primaryHashResult?.BmsonCount ?? 0)
                + " installedPrimaryFullDirectoryLookupInitialized=" + (primaryHashResult?.FullDirectoryLookupInitialized ?? false)
                + " realPathStatus=" + (realPathResult?.Status ?? "(null)")
                + " realPathChartRefs=" + (realPathResult?.ChartRefCount ?? 0)
                + " realPathDirectDirs=" + (realPathResult?.DirectDirectoryCount ?? 0)
                + " realPathSubtreeDirs=" + (realPathResult?.SubtreeDirectoryCount ?? 0)
                + " realPathOwnedCollectionVersion=" + (realPathResult?.OwnedCollectionVersion ?? 0)
                + " installDestinationOverlayStatus=" + (installDestinationOverlayResult?.Status ?? "(null)")
                + " installDestinationOverlayChartRefs=" + (installDestinationOverlayResult?.ChartRefCount ?? 0)
                + " installDestinationOverlayDirs=" + (installDestinationOverlayResult?.DirectoryCount ?? 0)
                + " playlistSummaryStatus=" + (playlistSummaryResult?.Status ?? "(null)")
                + " playlistSummaryMd5Hashes=" + (playlistSummaryResult?.Md5Count ?? 0)
                + " playlistSummarySha256Hashes=" + (playlistSummaryResult?.Sha256Count ?? 0)
                + " playlistSummarySnapshotVersion=" + (playlistSummaryResult?.SnapshotVersion ?? 0)
                + " playlistSummaryInvalidationVersion=" + (playlistSummaryResult?.InvalidationVersion ?? 0)
                + " playlistSummaryOwnedCollectionVersion=" + (playlistSummaryResult?.OwnedCollectionVersion ?? 0)
                + " playlistSummaryBmsRowsVersion=" + (playlistSummaryResult?.BmsRowsVersion ?? 0)
                + " playlistSummaryBmsonRowsVersion=" + (playlistSummaryResult?.BmsonRowsVersion ?? 0)
                + " playlistSummaryStaleRetries=" + (playlistSummaryResult?.StaleRetryCount ?? 0)
                + " installedPrimaryWarmupMs=" + (primaryHashResult?.ElapsedMs ?? 0L)
                + " realPathWarmupMs=" + (realPathResult?.ElapsedMs ?? 0L)
                + " installDestinationOverlayWarmupMs=" + (installDestinationOverlayResult?.ElapsedMs ?? 0L)
                + " playlistSummaryWarmupMs=" + (playlistSummaryResult?.ElapsedMs ?? 0L)
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            LogMainViewBuild("post_startup_warmup cancelled reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " stage=owned_adjacent_index"
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogMainViewBuild("post_startup_warmup failed reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " stage=owned_adjacent_index"
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + FormatTextForLog(ex.Message));
        }
    }

    private void ScheduleVirtualNormalLibraryOrderPrewarm(string reason, Action completed = null)
    {
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = RegularChartListOwner.CreateDefaultVirtualOrderPrewarmDescriptors();
        int degree = RegularChartListOwner.ResolveVirtualOrderPrewarmDegree(descriptors.Count);
        BMSLibrary prewarmLibrary = files;
        if (!regularChartListOwner.TryBeginVirtualOrderPrewarm(prewarmLibrary, out RegularChartListPrewarmLease lease))
        {
            LogMainViewBuild("virtual_order_prewarm queued reason=" + (reason ?? string.Empty)
                + " descriptorCount=" + descriptors.Count
                + " degree=" + degree
                + " priority1=" + CountPrewarmDescriptorsByPriority(descriptors, 1)
                + " priority2=" + CountPrewarmDescriptorsByPriority(descriptors, 2)
                + " priority3=" + CountPrewarmDescriptorsByPriority(descriptors, 3)
                + " waitFor=owned_adjacent_index"
                + " skipped=already_running_or_stopped");
            completed?.Invoke();
            return;
        }
        LogMainViewBuild("virtual_order_prewarm queued reason=" + (reason ?? string.Empty)
            + " runId=" + lease.RunId
            + " descriptorCount=" + descriptors.Count
            + " degree=" + degree
            + " priority1=" + CountPrewarmDescriptorsByPriority(descriptors, 1)
            + " priority2=" + CountPrewarmDescriptorsByPriority(descriptors, 2)
            + " priority3=" + CountPrewarmDescriptorsByPriority(descriptors, 3)
            + " waitFor=owned_adjacent_index");
        bool queued = startupBackgroundTaskScheduler.Queue(
            "playlist_virtual_order_prewarm",
            reason,
            null,
            () =>
            {
                try
                {
                    using (lease)
                    {
                        RunPostStartupOwnedAdjacentIndexWarmup(lease.RunId, reason, lease.Token);
                        bool includeBmsonRows = ShouldIncludeBmsonLibraryRowsInMainView(
                            MainViewUpdateMode.FolderFilterSelected,
                            MainViewUpdateMode.FolderFilterSelected);
                        regularChartListOwner.RunVirtualOrderPrewarm(
                            lease,
                            prewarmLibrary,
                            includeBmsonRows,
                            descriptors,
                            reason);
                    }
                }
                finally
                {
                    completed?.Invoke();
                }
                return Task.CompletedTask;
            },
            _ =>
            {
                lease.Dispose();
                completed?.Invoke();
            });
        if (!queued)
        {
            lease.Dispose();
            completed?.Invoke();
        }
    }

    private static int CountPrewarmDescriptorsByPriority(IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors, int priority)
    {
        return descriptors?.Count(descriptor => descriptor.PrewarmPriority == priority) ?? 0;
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
    public long LastMainViewBuildRequestId
    {
        get
        {
            MainChartListCompletion regular = MainChartList.LastCompletion;
            return regular.EndTimestamp >= Interlocked.Read(ref lastMainViewBuildEndTimestamp)
                ? regular.RequestId
                : Interlocked.Read(ref lastMainViewBuildRequestId);
        }
    }

    /// <summary>
    /// 最新の main_view_build 完了時刻 (Stopwatch タイムスタンプ) を返します。
    /// </summary>
    public long LastMainViewBuildEndTimestamp => Math.Max(Interlocked.Read(ref lastMainViewBuildEndTimestamp), MainChartList.LastCompletion.EndTimestamp);

    /// <summary>
    /// 最新の main_view_build を実行したスレッドIDを返します。
    /// </summary>
    public int LastMainViewBuildThreadId
    {
        get
        {
            MainChartListCompletion regular = MainChartList.LastCompletion;
            return regular.EndTimestamp >= Interlocked.Read(ref lastMainViewBuildEndTimestamp)
                ? regular.ThreadId
                : Volatile.Read(ref lastMainViewBuildThreadId);
        }
    }

    /// <summary>
    /// 最新の main_view_build 実行時モードを int 値で返します。
    /// </summary>
    public int LastMainViewBuildMode
    {
        get
        {
            MainChartListCompletion regular = MainChartList.LastCompletion;
            return regular.EndTimestamp >= Interlocked.Read(ref lastMainViewBuildEndTimestamp)
                ? (int)regular.Mode
                : Volatile.Read(ref lastMainViewBuildMode);
        }
    }

    public bool IsLibraryOperationInProgress
    {
        get
        {
            return startupProgressWorkflowOwner.IsStartupUiInteractionBlocked
                || (startupProgressWorkflowOwner.IsOperationActive
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
                Task.Run(refresh).Logging("QueuePlayHistoryDisplayTargetsRefresh");
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

    internal MainWindowViewModel(ApplicationComposition composition)
    {
        if (composition == null)
        {
            throw new ArgumentNullException(nameof(composition));
        }
        applicationComposition = composition;
        uiScheduler = composition.UiScheduler;
        applicationLifetime = composition.ApplicationLifetime;
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
                if (IsStartupRequiredBackgroundTaskEnrollmentComplete())
                {
                    startupProgressWorkflowOwner?.TryCompleteStartupBackgroundTasksPhaseIfIdle(
                        schedulerGeneration: generation,
                        schedulerRevision: revision);
                }
                TryLogStartupPostInitializationComplete();
            },
            startupBackgroundTaskProgressSynchronization);
        startupProgressWorkflowOwner = new StartupProgressWorkflowOwner(
            CaptureStartupProgressVersionSnapshot,
            PrepareStartupProgressOperation,
            DispatchStartupProgressPresentation,
            LogUiSuppression,
            TryLogStartupInitializationComplete,
            () => startupBackgroundTaskScheduler.IsStarted && startupBackgroundTaskScheduler.IsIdle,
            (generation, revision) => startupBackgroundTaskScheduler.IsCurrentIdleSnapshot(generation, revision),
            startupBackgroundTaskProgressSynchronization,
            IsStartupRequiredBackgroundTaskEnrollmentComplete);
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
            () => PackageInstallWorkflow?.IsActive == true,
            paths =>
            {
                if (PackageInstallWorkflow == null)
                {
                    throw new InvalidOperationException("Package install workflow is not composed.");
                }
                PackageInstallWorkflow.Enqueue(paths);
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
                "playlist_entries_hydration",
                work,
                shutdownReason => PlaylistWorkspace.DiscardDeferredExternalPlaylistSyncForShutdown(shutdownReason)),
            (reason, work) => startupBackgroundTaskScheduler.Queue(
                "playlist_ref_apply",
                reason,
                null,
                work,
                shutdownReason => PlaylistWorkspace.PlaylistReferenceApplyWorkflow.DiscardForShutdown(shutdownReason)),
            ApplyMainChartListPresentationActionAsync,
            () => uiScheduler.CanExecuteInline);
        PlaylistWorkspace.ConfigureCatalogNotificationQueue(QueueMainChartListAction);
        PlaylistWorkspace.TreeSelectionActivated += PlaylistWorkspaceTreeSelectionActivated;
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
            (library, progress, cancellationToken) => library.RescanAllOwnedChartMaintenance(progress, cancellationToken),
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportMaintenanceRescanWorkflowNotificationFailure,
            ReportMaintenanceRescanWorkflowFailure,
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportFolderAutoRenameWorkflowNotificationFailure,
            ReportFolderAutoRenameWorkflowFailure,
            new UiDialogCoordinator(),
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
                    () => GetStartupSettingsSnapshot().OperationModeLR2DB),
                new UiDialogCoordinator()),
            rankingCacheDownloadWorkflow: new RankingCacheDownloadWorkflowOwner(
                new BmsRankingCacheDownloadRuntime(() => files),
                new UiDialogCoordinator()),
            libraryFolderTreeLog: LogUiSuppression,
            libraryFolderTreeLogWarning: LogUiSuppressionWarning);
        ProgressHub = childComposition.ProgressHub;
        ChartMutationActivity = childComposition.ChartMutationActivity;
        ChartMutationActivity.ActivityChanged += ChartMutationActivityChanged;
        PlaybackPanel = childComposition.PlaybackPanel;
        ChartFilters = childComposition.ChartFilters;
        LibraryFolderTree = childComposition.LibraryFolderTree;
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
            _semaphore,
            startupProgressWorkflowOwner,
            applicationLifetime.MarkCoordinatedShutdownStarted,
            Net10PerformanceLog.StopAsync,
            DispatchShellShutdownActionAsync,
            LogShutdown,
            LogShutdownWarning,
            FormatTextForLog);
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
            lr2SongDbSyncWorkflow: Lr2SongDbSyncWorkflow);
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
            PlaylistWorkspace.CompleteDetailEdit(request);
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
            RefreshNormalLibraryAfterSourceChanged(request.Reason);
        }
        else
        {
            RefreshNormalLibraryForNotificationPresentationEffects(request.NotificationBatch);
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
            .Logging("playHistoryPeriodSelect");
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
            Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.Now));
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
    /// データベース側からプレイリスト情報 (BMSTable) を再読み込みし、コレクションを更新します。<br/>
    /// バックグラウンドで初期化を行い、更新完了後に外部同期などを再スケジュールします。
    /// </summary>
    internal async Task ReloadTablesAsync()
    {
        if (!initializationCompleted)
        {
            return;
        }
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);
        bool scheduleDeferredExternalSync = false;
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
            await Task.Run(delegate
            {
                tables.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            }).LoggingAndPropagate("ReloadTables");
            scheduleDeferredExternalSync = true;
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, operationToken);
            try
            {
                if (scheduleDeferredExternalSync)
                {
                    PlaylistWorkspace.QueueExternalPlaylistSync(
                        "ReloadTables",
                        fromReloadTables: true,
                        publishReferenceReceipt: true,
                        operationToken: operationToken);
                }
            }
            finally
            {
                MarkNonStartupBackgroundSchedulingComplete();
                _semaphore.Release();
            }
        }
        startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(
            "ReloadTables:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistReferenceApplied);
    }

    /// <summary>
    /// score source / score.db 設定変更を、playlist/table reload を伴わずに反映します。
    /// </summary>
    internal async Task ReloadScoresOnlyAsync()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadScoresOnly");
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView);
            playHistoryWorkflowOwner.InvalidateReadCache("score_reload");
            LogInitStage("score_reload_task_start", "ReloadScoresOnly");
            await Task.Run(delegate
            {
                LogInitStage("score_reload_call", "ReloadScoresOnly");
                files.InitializeScoresOnly(null);
            }).LoggingAndPropagate("ReloadScoresOnly");
            PublishLatestLr2PlayHistorySchemaStatusSnapshotFromLibrary();
            LogInitStage("score_reload_done", "ReloadScoresOnly");
            RefreshLibraryMainViewForCurrentFilter();
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "score_only_reload");
            startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(
                "ReloadScoresOnly:scheduled",
                operationToken,
                StartupProgressPhase.ScoreHydrationDone,
                StartupProgressPhase.RankingRefreshDone);
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            MarkNonStartupBackgroundSchedulingComplete();
            EndUiUpdateSuppression();
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, operationToken);
            LogInitStage("ui_suppress_end_called", "ReloadScoresOnly");
            _semaphore.Release();
            startupProgressWorkflowOwner.MarkStartupProgressFailureCleanupComplete(operationToken);
        }
    }

    internal async Task ReloadFileDiffAsync()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadFileDiff");
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            LogInitStage("file_diff_reload_task_start", "ReloadFileDiff");
            await Task.Run(delegate
            {
                LogInitStage("file_diff_reload_call", "ReloadFileDiff");
                files.ReloadFileDiff();
                Lr2SongDbSyncWorkflow.QueueAfterReloadFileDiff("ReloadFileDiff");
            }).LoggingAndPropagate("ReloadFileDiff");
            LogInitStage("file_diff_reload_done", "ReloadFileDiff");
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                LibraryFolderTree.ScheduleDeferredRefresh(operationToken);
            }
            PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue("ReloadFileDiff", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "ReloadFileDiff");
            startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(
                "ReloadFileDiff:scheduled",
                operationToken,
                StartupProgressPhase.PlaylistReferenceApplied,
                StartupProgressPhase.PlaylistEntriesHydrationDone);
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            MarkNonStartupBackgroundSchedulingComplete();
            EndUiUpdateSuppression();
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, operationToken);
            LogInitStage("ui_suppress_end_called", "ReloadFileDiff");
            _semaphore.Release();
            startupProgressWorkflowOwner.MarkStartupProgressFailureCleanupComplete(operationToken);
        }
    }

    internal async Task ReinitializeLibraryAsync()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "FullReinitialize");
        bool scheduleDeferredPlaylistRef = false;
        await _semaphore.WaitAsync();
        long operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.FullReinitialize);
        try
        {
            playHistoryWorkflowOwner.InvalidateReadCache("full_reinitialize");
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            LogInitStage("files_initialize_task_start", "FullReinitialize");
            await Task.Run(delegate
            {
                LogInitStage("files_initialize_call", "FullReinitialize");
                files.Reinitialize();
            }).LoggingAndPropagate("FullReinitialize");
            LogInitStage("files_initialize_done", "FullReinitialize");
            scheduleDeferredPlaylistRef = true;
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                LibraryFolderTree.ScheduleDeferredRefresh(operationToken);
            }
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            startupProgressWorkflowOwner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, operationToken);
            LogInitStage("ui_suppress_end_called", "FullReinitialize");
            try
            {
                if (scheduleDeferredPlaylistRef)
                {
                    PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue("FullReinitialize", operationToken);
                    LogInitStage("deferred_playlist_ref_queued", "FullReinitialize");
                }
            }
            finally
            {
                MarkNonStartupBackgroundSchedulingComplete();
                _semaphore.Release();
            }
        }
        startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(
            "FullReinitialize:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone);
    }

    internal static string BuildAppSchemaRepairWarningMessage(AppSchemaPreflightResult preflightResult)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        return BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningMessage;
    }

    internal static bool ApplyAppSchemaRepairPreflightForStartup(AppSchemaPreflightResult preflightResult, ref bool approvedForSession, Func<string, bool?> confirmWarning, Action applyStartupRepair, Action shutdown)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        if (applyStartupRepair == null)
        {
            throw new ArgumentNullException(nameof(applyStartupRepair));
        }
        if (preflightResult.WarnRequired && !approvedForSession)
        {
            if (confirmWarning == null)
            {
                throw new ArgumentNullException(nameof(confirmWarning));
            }
            if (confirmWarning(BuildAppSchemaRepairWarningMessage(preflightResult)) != true)
            {
                shutdown?.Invoke();
                return false;
            }
            approvedForSession = true;
        }
        applyStartupRepair();
        return true;
    }

    private async Task<bool> EnsureAppSchemaRepairApprovedForStartupAsync(StartupSettingsSnapshot startupSettings)
    {
        LogInitStage("app_schema_preflight_inspect_start", "Initialize");
        var appSchemaPreflightService = new AppSchemaPreflightService();
        AppSchemaPreflightResult preflightResult = appSchemaPreflightService.Inspect(startupSettings.LR2SongDBPath);
        LogInitStage("app_schema_preflight_inspect_done", "Initialize");
        if (preflightResult.WarnRequired && !bmsonMigrationApprovedForSession)
        {
            LogInitStage("app_schema_preflight_prompt_show", "Initialize");
            bool approved = ShowUiConfirmation(BuildAppSchemaRepairWarningMessage(preflightResult), BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningTitle, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "App schema repair startup confirmation");
            LogInitStage("app_schema_preflight_prompt_close", "Initialize");
            if (!approved)
            {
                applicationLifetime.RequestShutdown();
                return false;
            }
            bmsonMigrationApprovedForSession = true;
        }
        await Task.Run(delegate
        {
            ApplyAppSchemaRepairForStartupOrThrow(appSchemaPreflightService, preflightResult, startupSettings.LR2SongDBPath);
        }).Logging("AppSchemaStartupRepair");
        return true;
    }

    private void ApplyAppSchemaRepairForStartupOrThrow(
        AppSchemaPreflightService appSchemaPreflightService,
        AppSchemaPreflightResult preflightResult,
        string songDbPath)
    {
        var stopwatch = Stopwatch.StartNew();
        LogInitStage("app_schema_repair_start", "Initialize");
        var gateway = new BmsLibraryDbGateway(songDbPath);
        long schemaStartMs = stopwatch.ElapsedMilliseconds;
        if (preflightResult.WarnRequired || preflightResult.NeedsAppSchemaVersionRepair || preflightResult.RepairRequired)
        {
            LogInitStage("app_schema_repair_apply_start", "Initialize");
            gateway.RepairAppOwnedSchema();
            LogInitStage("app_schema_repair_apply_done elapsedMs=" + (stopwatch.ElapsedMilliseconds - schemaStartMs), "Initialize");
        }
        else
        {
            LogInitStage("app_schema_ensure_start", "Initialize");
            gateway.EnsureAppOwnedSchema();
            LogInitStage("app_schema_ensure_done elapsedMs=" + (stopwatch.ElapsedMilliseconds - schemaStartMs), "Initialize");
        }
        LogInitStage("app_schema_preflight_final_reinspect_start", "Initialize");
        AppSchemaPreflightResult finalResult = appSchemaPreflightService.Inspect(songDbPath);
        LogInitStage("app_schema_preflight_final_reinspect_done", "Initialize");
        if (finalResult.NeedsAppSchemaVersionRepair
            || finalResult.RepairRequired)
        {
            throw new InvalidOperationException("app schema repair did not converge.");
        }
        LogInitStage("app_schema_repair_done elapsedMs=" + stopwatch.ElapsedMilliseconds, "Initialize");
    }

    /// <summary>
    /// アプリケーション初期起動時に実行される、メイン初期化ルーチンです。非同期で呼び出されます。<br/>
    /// 設定の妥当性チェック、BMSデータベース (LR2SongDB形式など) との接続、BMSプレイヤーインスタンスの生成、
    /// およびコレクション更新をフックする各種イベントリスナーの登録を順次行います。
    /// </summary>
    private LibraryProfile CreateLibraryProfileForStartup(StartupSettingsSnapshot startupSettings)
    {
        if (startupSettings.OperationModeLR2DB)
        {
            lr2config = new LR2Config(startupSettings.LR2ConfigXmlPath);
            EnsureLR2DatabaseAutoReloadManualOnlyForStartup(startupSettings);
            string scoreDbPath = Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(startupSettings.LR2RootPath, lr2config.GetPlayerId);
            return new LibraryProfile(
                operationModeLR2DB: true,
                songDbPath: startupSettings.LR2SongDBPath,
                searchRoots: [],
                lr2ConfigProvider: () => lr2config,
                lr2ScoreDbPath: scoreDbPath,
                canWriteLr2Config: true,
                canOutputLr2Folders: true,
                canUseLr2Backup: true,
                canUseLr2IrScore: true);
        }

        lr2config = null;
        StandaloneLibraryDatabaseEnsureResult standaloneSongDb = StandaloneLibraryDatabase.EnsurePortableSongDb();
        return new LibraryProfile(
            operationModeLR2DB: false,
            songDbPath: standaloneSongDb.SongDbPath,
            searchRoots: startupSettings.StandaloneBmsRootPaths,
            lr2ConfigProvider: null,
            lr2ScoreDbPath: null,
            canWriteLr2Config: false,
            canOutputLr2Folders: false,
            canUseLr2Backup: false,
            canUseLr2IrScore: false,
            startupRequiredFileScanReason: standaloneSongDb.RequiresInitialLibraryBuild ? standaloneSongDb.InitialLibraryBuildReason : null);
    }

    private void RepairCustomFolderOutputSearchRootsBeforeStartupValidation(StartupSettingsSnapshot startupSettings)
    {
        if (!startupSettings.OperationModeLR2DB
            || string.IsNullOrWhiteSpace(startupSettings.LR2ConfigXmlPath)
            || !File.Exists(startupSettings.LR2ConfigXmlPath))
        {
            return;
        }

        lr2config = new LR2Config(startupSettings.LR2ConfigXmlPath);
        CustomFolderOutputBaseSearchRootSyncResult result =
            CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                lr2config,
                startupSettings.LR2CustomFolderOutputBaseDir,
                startupSettings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (result.Changed)
        {
            lr2config.Save();
            LogInitStage("custom_folder_output_search_root_repair added=" + result.AddedCount, "Initialize");
        }

    }

    private void RepairRootCustomFolderOutputSearchRootsAfterStartupPlaylistLoad(
        CustomFolderOutputSettingsSnapshot startupCustomFolderSettings)
    {
        if (startupCustomFolderSettings?.OperationModeLR2DB != true)
        {
            return;
        }

        if (PlaylistWorkspace.RepairRootCustomFolderOutputSearchRootsAfterStartup(startupCustomFolderSettings))
        {
            LogInitStage("custom_folder_root_output_search_root_repair", "Initialize");
        }
    }

    private void EnsureLR2DatabaseAutoReloadManualOnlyForStartup(StartupSettingsSnapshot startupSettings)
    {
        if (!startupSettings.OperationModeLR2DB || lr2config == null)
        {
            return;
        }
        if (lr2config.EnsureDatabaseAutoReloadManualOnly())
        {
            lr2config.Save();
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

    internal async Task<bool> InitializeAsync()
    {
        await _semaphore.WaitAsync();
        startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(true);
        LogInitStage("start", "Initialize");
        initializationCompleted = false;
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
        StartupSettingsSnapshot startupSettings;
        CustomFolderOutputSettingsSnapshot startupCustomFolderSettings = null;
        try
        {
            startupSettings = GetStartupSettingsSnapshot();
            if (startupSettings.OperationModeLR2DB)
            {
                startupCustomFolderSettings = customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null during startup.");
            }
            RepairCustomFolderOutputSearchRootsBeforeStartupValidation(startupSettings);
        }
        catch (Exception ex)
        {
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup custom folder repair failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            currentClassLogger.Error(ex, text + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            SettingDialog?.RequestOpen();
            return false;
        }
        if (!SettingDialog.CheckValidation(out string startupValidationErrorMessage))
        {
            NLogWrapper.FileLogger?.Warn("startup_setting_validation_failed " + (startupValidationErrorMessage ?? string.Empty).Replace(Environment.NewLine, " | "));
            if (applicationLifetime.IsFirstStartup)
            {
                _semaphore.Release();
                startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
                SettingDialog?.RequestInitialSetupLanguageDialog();
                return false;
            }
            else
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_settings_check, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Startup settings validation notification");
            }
            _semaphore.Release();
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            SettingDialog?.RequestOpen();
            return false;
        }
        try
        {
            if (startupSettings.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync(startupSettings))
            {
                _semaphore.Release();
                startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup app schema approval failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            string text2 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text2 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            SettingDialog?.RequestOpen();
            return false;
        }
        long operationToken;
        try
        {
            playHistoryWorkflowOwner.InvalidateReadCache("initialize");
            LibraryProfile libraryProfile = CreateLibraryProfileForStartup(startupSettings);
            using (chartFileOperations.Enter())
            {
                files = applicationComposition.CreateBmsLibrary(libraryProfile);
                ShellShutdownWorkflow.AttachLibrary(files);
                PackageInstallWorkflow.AttachLibrary(files);
                MaintenanceRescanWorkflow.AttachLibrary(files);
                FolderAutoRenameWorkflow.AttachLibrary(files);
                regularChartListOwner.AttachNormalLibraryRefreshSource(files);
            }
            PlaybackPanel.AttachLibrary(files);
            tables = applicationComposition.CreateBmsPlaylist(
                libraryProfile,
                () => files.GetBMSScores(),
                () => files.CreateBeatorajaBmtSongHashResolver(),
                files.Lr2PlaylistFolderSynchronization);
            ShellShutdownWorkflow.AttachPlaylist(tables);
            PlaylistWorkspace.RefreshPlaylistTreeTables(tables, files);
            PlaylistWorkspace.SetDetailDataSource(
                applicationComposition.CreatePlaylistDetailDataSource(files, tables, MainChartList));
            files.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => startupBackgroundTaskScheduler.Queue(name, reason, dependency, work);
            files.StartupBackgroundTaskReporter = startupBackgroundTaskScheduler.Report;
            files.StartupBackgroundWorkSnapshotProvider = startupBackgroundTaskScheduler.CaptureWorkSnapshot;
            tables.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => startupBackgroundTaskScheduler.Queue(name, reason, dependency, work);
            tables.BmtOutput.ExportProgressReporter = PlaylistWorkspace.ReportPlaylistSyncProgress;
            if (!libraryProfile.OperationModeLR2DB)
            {
                files.SearchTargets.AddRange(libraryProfile.SearchRoots);
            }
            LibraryFolderTree.AttachLibrary(files);
            InstallTree.AttachLibrary(files);
            MaintenanceTree.AttachLibrary(files);
            IBMSPlayer configuredBmsPlayer = applicationComposition.CreateBmsPlayer(
                startupSettings,
                () => CreateLR2PlayerConfig(startupSettings));
            if (configuredBmsPlayer != null)
            {
                await PlaybackPanel.ReplacePlayerAsync(configuredBmsPlayer);
            }
            operationToken = startupProgressWorkflowOwner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup library construction failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            string text3 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text3 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            SettingDialog?.RequestOpen();
            return false;
        }
        regularChartListOwner.InitializeColumnPresentation(treeViewFilterTypeSelected);
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSLibrary.RegisterHandler(() => files.OwnedChartCollectionVersion, delegate
        {
            PlaylistWorkspace.InvalidatePlaylistLibraryIndexSnapshot(
                "owned_collection_changed",
                startupReadyOperableReached,
                treeViewFilterTypeSelected);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressVersion, delegate
        {
            BMSLibrary.LibraryInitializationProgressSnapshot progress =
                files.GetLibraryInitializationProgressSnapshot();
            startupProgressWorkflowOwner.UpdateStartupProgressLibraryInitializationStatus(
                progress.Stage,
                progress.ScannerLabel,
                progress.TotalCount,
                progress.ProcessedCount,
                progress.CurrentPath);
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
            startupProgressWorkflowOwner.TrackStartupProgressScoreHydrationRequested(files.ScoreHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressScoreHydration(files.ScoreHydrationCompletedVersion);
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "score_hydration_completed");
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "score_hydration_completed"))
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
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressRankingRefreshRequested(files.RankingRefreshRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressRankingRefresh(files.RankingRefreshCompletedVersion);
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "ranking_refresh_completed");
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "ranking_refresh_completed"))
            {
                PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                    "ranking_refresh_completed");
                return;
            }
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "ranking_refresh_completed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceHydrationRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressMaintenanceRequested(files.MaintenanceHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceHydrationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressMaintenance(files.MaintenanceHydrationCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressInstallableMaintenanceRequested(files.InstallableMaintenanceDeferredRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressInstallableMaintenance(files.InstallableMaintenanceDeferredCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressChartDigestBackfillRequested(files.ChartDigestBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressChartDigestBackfill(files.ChartDigestBackfillCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillTotalCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillProcessedCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCurrentPath, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressChartInfoBackfillRequested(files.ChartInfoBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressChartInfoBackfill(files.ChartInfoBackfillCompletedVersion);
            if ((files?.ChartInfoBackfillDigestBackfilledCount ?? 0) > 0)
            {
                InvalidateNormalLibraryIdentitySortKeys(NormalLibraryChartInfoDigestBackfilledReason);
            }
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillTotalCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillProcessedCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCurrentPath, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressChartInfoHydrationRequested(files.ChartInfoHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressChartInfoHydration(files.ChartInfoHydrationCompletedVersion);
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationTotalCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationAppliedCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncRequestedVersion, delegate
        {
            startupProgressWorkflowOwner.TrackStartupProgressLr2SongDbSyncRequested(files.Lr2SongDbSyncRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncCompletedVersion, delegate
        {
            startupProgressWorkflowOwner.TryCompleteStartupProgressLr2SongDbSync(files.Lr2SongDbSyncCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncFailedVersion, delegate
        {
            startupProgressWorkflowOwner.TryFailStartupProgressLr2SongDbSync(files.Lr2SongDbSyncFailedVersion, files.Lr2SongDbSyncFailureMessage);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStatusVersion, delegate
        {
            UpdateLr2SongDbSyncRuntimeStatus(files.GetLr2SongDbSyncStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncTotalCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncProcessedCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStage, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStageProcessedCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStageTotalCount, delegate
        {
            startupProgressWorkflowOwner.UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.PendingEstimateQueueStatusVersion, delegate
        {
            UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallEstimationProgressVersion, delegate
        {
            UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        });
        UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        if (startupSettings.OperationModeLR2DB && startupSettings.IsLR2BackupEnabled)
        {
            Backup.Target lR2BackupTarget = startupSettings.LR2BackupTarget;
            List<string> bkPaths = [];
            string songDBPath = null;
            List<string> scoreDBPaths = [];
            if (lR2BackupTarget.HasFlag(Backup.Target.Config) && LongPathFileSystem.FileExists(startupSettings.LR2ConfigXmlPath))
            {
                bkPaths.Add(startupSettings.LR2ConfigXmlPath);
            }
            if (lR2BackupTarget.HasFlag(Backup.Target.SongDB) && LongPathFileSystem.FileExists(startupSettings.LR2SongDBPath))
            {
                songDBPath = startupSettings.LR2SongDBPath;
                bkPaths.Add(startupSettings.LR2SongDBPath);
            }
            string scoreDirectoryPath = Path.Combine(startupSettings.LR2RootPath, "LR2files", "Database", "Score");
            if (lR2BackupTarget.HasFlag(Backup.Target.ScoreDB) && LongPathFileSystem.DirectoryExists(scoreDirectoryPath))
            {
                bkPaths.Add(scoreDirectoryPath);
                try
                {
                    scoreDBPaths = [.. LongPathFileSystem.EnumerateFiles(scoreDirectoryPath, "*.db", System.IO.SearchOption.AllDirectories)];
                }
                catch
                {
                    scoreDBPaths = [];
                }
            }
            if (bkPaths.Count > 0)
            {
                Backup.BackupSaveResult backupSaveResult = null;
                backupSaveResult = await Task.Run(delegate
                {
                    try
                    {
                        Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(startupSettings.LR2BackupPath, new TimeSpan(startupSettings.LR2BackupSpan, 0, 0, 0), startupSettings.LR2BackupNum, bkPaths);
                        if (result.Saved)
                        {
                            try
                            {
                                Backup.RebuildDatabase(songDBPath, scoreDBPaths);
                            }
                            catch
                            {
                            }
                        }
                        return result;
                    }
                    catch (Exception ex)
                    {
                        return Backup.BackupSaveResult.Failure(ex);
                    }
                }).Logging("Initialize");
                if (backupSaveResult != null)
                {
                    foreach (string warning in backupSaveResult.Warnings)
                    {
                        ShowUiMessage(warning, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "LR2 backup warning notification");
                    }
                    if (backupSaveResult.FailureException != null)
                    {
                        ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_failed_backups + Environment.NewLine + backupSaveResult.FailureException.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "LR2 backup failure notification");
                    }
                }
            }
        }
        var semaphore = new SemaphoreSlim(1, 1);
        void taskAdd1()
        {
            tables.Initialize(
                reloadExtPlaylist: false,
                semaphore: semaphore,
                queueBeatorajaBmtExportAfterHydration: startupSettings.SkipInitPlaylistLoad);
        }
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
        BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
        try
        {
            await Task.Run(delegate
            {
                files.InitializeStartup(
                    [taskAdd1],
                    semaphore,
                    startupPerformanceInteraction);
            }).Logging("Initialize");
            PublishLatestLr2PlayHistorySchemaStatusSnapshotFromLibrary();
            RepairRootCustomFolderOutputSearchRootsAfterStartupPlaylistLoad(startupCustomFolderSettings);
            LogInitStage("files_initialize_done", "Initialize");
            TryLogStartupReadyData();
        }
        catch (Exception ex)
        {
            startupProgressWorkflowOwner.FailStartupProgressOperation(ex.Message);
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup library initialization failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            string text4 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text4 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);
            SettingDialog?.RequestOpen();
            return false;
        }
        finally
        {
            EndUiUpdateSuppression();
            LogInitStage("ui_suppress_end_called", "Initialize");
            startupProgressWorkflowOwner.MarkStartupProgressFailureCleanupComplete(operationToken);
        }
        if (applicationLifetime.IsFirstStartup)
        {
            applicationLifetime.CompleteFirstStartup();
            initialSetupCompletionMessagePending = true;
        }
        initializationCompleted = true;
        hasActiveLibraryProfile = true;
        RaisePropertyChanged(nameof(IsInitializationCompleted));
        RaisePropertyChanged(nameof(HasActiveLibraryProfile));
        RaiseLibraryOperationAvailabilityChanged();
        PlaylistWorkspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");
        startupBackgroundTaskScheduler.Queue(
            "external_table_catalog",
            "Initialize",
            null,
            () => PlaylistWorkspace.LoadExternalTableCollectionAsync(
                startupSettings.TableListURL,
                BMSPlaylist.GetBMSTableInfoAsync),
            _ => PlaylistWorkspace.CancelExternalTableCollectionLoadForShutdown());
        LogInitStage("deferred_playlist_ref_waiting_for_playlist_entries_hydration", "Initialize");
        if (!startupSettings.SkipInitPlaylistLoad)
        {
            PlaylistWorkspace.QueueExternalPlaylistSync(
                "Initialize",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: operationToken);
        }
        SchedulePostStartupBestEffortWarmups("startup_initialization_ready", operationToken);
        startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(
            "Initialize:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone);
        startupBackgroundTaskScheduler.MarkPostInitializationSchedulingComplete();
        _semaphore.Release();
        return true;
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
                    startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken());
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
            long operationToken = startupProgressWorkflowOwner.GetActiveStartupProgressOperationToken();
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
        startupProgressWorkflowOwner.TrackStartupProgressExternalSyncRequest(request.Reason, request.Version, request.OperationToken);
        if (request.PublishesReferenceReceipt)
        {
            startupProgressWorkflowOwner.TrackStartupProgressPlaylistReferenceRequest(
                "DeferredExternalSync:" + request.Reason,
                request.Version,
                request.OperationToken);
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
        startupProgressWorkflowOwner.TrackStartupProgressPlaylistReferenceRequest(request.Reason, request.Version, request.OperationToken);
        startupProgressWorkflowOwner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(
            request.Version,
            "playlist_ref_deferred:" + request.Reason,
            request.OperationToken);
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
        if (TryDeferStartupPresentationRefresh(
            UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree,
            "playlist_ref_apply_completed"))
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
    /// メインビュー更新共通の callback 発火と性能ログを確定します。
    /// </summary>
    private void FinalizeMainViewBuild(
        Stopwatch viewBuildStopwatch,
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        long folderStageMs,
        long keywordStageMs,
        long modeStageMs,
        long sortStageMs,
        bool sortReuse,
        string sortProfile,
        int folderCount,
        int keywordCount,
        int modeCount,
        int viewCount,
        long columnStageMs,
        long callbackStageMs)
    {
        ChartListSortParameters sortParameters = CaptureActiveMainViewSortParameters();
        string sortColumn = sortParameters?.ColumnsName ?? "(default_title)";
        string sortDirection = sortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = parameter?.GetType().Name ?? "(null)";
        bool fastSortEnabled = true;
        bool isPlaylistDetailForLog = IsPlaylistViewMode(mode) || IsPlaylistViewMode(treeViewFilterTypeSelected);
        long mainViewBuildRequestId = MainViewBuildRequestSequence.Next();
        long mainViewBuildEndTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMainViewBuildRequestId, mainViewBuildRequestId);
        Interlocked.Exchange(ref lastMainViewBuildEndTimestamp, mainViewBuildEndTimestamp);
        Volatile.Write(ref lastMainViewBuildThreadId, Thread.CurrentThread.ManagedThreadId);
        Volatile.Write(ref lastMainViewBuildMode, (int)mode);
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + parameterType + " folderMs=" + folderStageMs + " keywordMs=" + keywordStageMs + " modeMs=" + modeStageMs + " sortMs=" + sortStageMs + " sortReuse=" + sortReuse + " sortProfile=" + sortProfile + " sortEngine=fast fastSortEnabled=" + fastSortEnabled + " isPlaylistDetailView=" + isPlaylistDetailForLog + " columnMs=" + columnStageMs + " callbackMs=" + callbackStageMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds + " folderCount=" + folderCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
    }

    /// <summary>
    /// 指定された更新モードとパラメータに基づいて、メインの chart row 表示用コレクションを生成・更新します。
    /// ツリーでのフォルダ選択、プレイリストや難易度表の適用、Missingファイル等の保守フィルタ、およびキーワードやキーモードでの絞り込み等を行います。<br/>
    /// このメソッドの実行には、規模に応じて時間がかかるため内部でタイマー計測し遅延を制御・ロギングする機構が含まれています。
    /// </summary>
    /// <param name="mode">更新の契機（どのフィルタや要素が変更されたかを示す更新モード）。</param>
    /// <param name="parameter">選択されたプレイリスト（BMSTable）やフォルダ名などの追加パラメータ、無い場合は null。</param>
    private void RefreshChartRowsView(
        MainViewUpdateMode mode,
        object parameter = null,
        MainChartListSortTarget? expectedSortTarget = null)
    {
        if (expectedSortTarget.HasValue
            && expectedSortTarget.Value != (MainChartList.CurrentOperationContext.OperationSection == MainViewOperationSection.PlayHistory
                ? MainChartListSortTarget.PlayHistory
                : MainChartListSortTarget.Regular))
        {
            return;
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
            PlayHistoryViewRequest playHistoryRequest = parameter as PlayHistoryViewRequest;
            if (playHistoryRequest == null || !playHistoryWorkflowOwner.IsCurrentRequest(playHistoryRequest.RequestId))
            {
                LogStalePlayHistoryViewRequest(
                    mode,
                    requestedMode,
                    parameter,
                    playHistoryRequest?.PeriodRequest ?? parameter as PlayHistoryPeriodRequest,
                    playHistoryRequest?.RequestId ?? 0L,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return;
            }
        }
        else if (mode == MainViewUpdateMode.KeywordFilterUpdated && parameter is PlayHistoryViewRequest playHistoryKeywordRequest)
        {
            if (!playHistoryWorkflowOwner.IsCurrentRequest(playHistoryKeywordRequest.RequestId))
            {
                LogStalePlayHistoryViewRequest(
                    mode,
                    requestedMode,
                    parameter,
                    playHistoryKeywordRequest.PeriodRequest,
                    playHistoryKeywordRequest.RequestId,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return;
            }
            if (files == null)
            {
                return;
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
            return;
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
            return;
        }
        if (route.Kind == ChartListRefreshRouteKind.ApplyPlayHistoryView)
        {
            PlayHistoryViewRequest activeRequest = parameter as PlayHistoryViewRequest
                ?? playHistoryWorkflowOwner.SnapshotActiveRequest();
            if (activeRequest == null || !playHistoryWorkflowOwner.IsCurrentRequest(activeRequest.RequestId))
            {
                LogStalePlayHistoryViewRequest(
                    route.Mode,
                    route.RequestedMode,
                    parameter,
                    activeRequest?.PeriodRequest ?? parameter as PlayHistoryPeriodRequest,
                    activeRequest?.RequestId ?? 0L,
                    viewBuildStopwatch.ElapsedMilliseconds);
                return;
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
            return;
        }
        if (route.Kind == ChartListRefreshRouteKind.RegisterPlaylistSourceBuild)
        {
            UpdatePlaylistDetailActivation(route.IsPlaylistTreeActive);
            PlaylistWorkspace.RequestDetailRefresh(
                route.Mode,
                route.RequestedMode,
                activeTreeViewFilterMode,
                ShouldUsePlaylistBuildCoalescingWindow(route.Mode, route.RequestedMode),
                CapturePlaylistOpenReadinessSnapshot());
            return;
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
    }

    private void LogPlayHistoryViewExecution(PlayHistoryViewExecutionResult execution)
    {
        if (execution == null)
        {
            return;
        }

        PlayHistoryViewExecutionRequest request = execution.Request;
        PlayHistoryViewRequest viewRequest = request.ViewRequest;
        PlayHistoryPeriodRequest periodRequest = viewRequest.PeriodRequest;
        if (execution.Status == PlayHistoryViewExecutionStatus.NoCurrentMatchingState)
        {
            LogPlayHistoryEvent(
                "play_history_view_presentation_skipped",
                "period=" + periodRequest.Kind
                + " requestId=" + viewRequest.RequestId
                + " reason=no_current_matching_state"
                + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
            LogMainViewBuild(
                "main_view_build mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " parameterType=" + (request.Parameter?.GetType().Name ?? "(null)")
                + " playHistorySortOnly=true skipped=true reason=no_current_matching_state"
                + " playHistoryPeriod=" + periodRequest.Kind
                + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
            return;
        }

        PlayHistoryReadWorkflowResult readResult = execution.ReadResult;
        PlayHistoryReadPresentationBuildResult readPresentation = readResult?.Presentation;
        PlayHistoryPresentationOnlyBuildResult presentationOnly = execution.PresentationOnlyResult;
        if (readPresentation != null)
        {
            LogPlayHistoryDisplayTargetFilter(
                periodRequest,
                viewRequest.RequestId,
                PlayHistory.SelectedDisplayTarget,
                readPresentation.DisplayTargetSourceCount,
                readPresentation.DisplayTargetResultCount);
            LogPlayHistoryKeywordFilter(
                periodRequest,
                viewRequest.RequestId,
                request.KeywordFilter,
                readResult.Read.RowCount,
                readPresentation.DisplayTargetResultCount,
                readPresentation.KeywordCount,
                readPresentation.KeywordMs);
        }
        else if (presentationOnly != null)
        {
            if (presentationOnly.DisplayTargetApplied)
            {
                LogPlayHistoryDisplayTargetFilter(
                    periodRequest,
                    viewRequest.RequestId,
                    PlayHistory.SelectedDisplayTarget,
                    presentationOnly.DisplayTargetSourceCount,
                    presentationOnly.DisplayTargetResultCount);
            }
            if (presentationOnly.KeywordFilterApplied)
            {
                LogPlayHistoryKeywordFilter(
                    periodRequest,
                    viewRequest.RequestId,
                    request.KeywordFilter,
                    presentationOnly.KeywordSourceCount,
                    presentationOnly.KeywordProjectedCount,
                    presentationOnly.KeywordCount,
                    presentationOnly.KeywordMs);
            }
        }

        if (execution.Status != PlayHistoryViewExecutionStatus.Applied)
        {
            long elapsedMs = execution.Status == PlayHistoryViewExecutionStatus.ReadCanceled
                ? readResult?.ElapsedThroughCancellationMs ?? request.Stopwatch.ElapsedMilliseconds
                : request.Stopwatch.ElapsedMilliseconds;
            LogStalePlayHistoryViewRequest(
                request.Mode,
                request.RequestedMode,
                request.Parameter,
                periodRequest,
                viewRequest.RequestId,
                elapsedMs);
            return;
        }

        PlayHistoryViewState state = readPresentation?.State ?? presentationOnly?.State;
        PlayHistorySortedRowsApplyResult applyResult = execution.ApplyResult;
        PlayHistoryTerminalCommitResult terminalCommit = applyResult.TerminalCommit;
        long prepareSwapMs = terminalCommit.MainRowsApply.PrepareSwapMs;
        long columnSettingMs = terminalCommit.MainRowsApply.ColumnSettingMs;
        long setViewMs = terminalCommit.MainRowsApply.SetViewMs;
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics = applyResult.Diagnostics;
        int diagnosticsCount = diagnostics.Count;
        if (diagnosticsCount > 0)
        {
            LogPlayHistoryDiagnostics(periodRequest, applyResult.SortProfile, diagnostics);
        }
        long sortMs = execution.SortMs + applyResult.AdditionalSortMs;
        LogPlayHistoryEvent(
            "play_history_view_apply",
            "period=" + periodRequest.Kind
            + " requestId=" + viewRequest.RequestId
            + " sortOnly=" + execution.FromSortOnly.ToString().ToLowerInvariant()
            + " sortSucceeded=" + applyResult.SortSucceeded.ToString().ToLowerInvariant()
            + " sortProfile=" + (applyResult.SortProfile ?? string.Empty)
            + " schemaStatus=" + state.SchemaStatus
            + " diagnosticsCount=" + diagnosticsCount
            + " sourceCount=" + state.SourceCount
            + " projectedCount=" + state.ProjectedRows.Count
            + " viewCount=" + applyResult.ViewCount
            + " readMs=" + execution.ReadMs
            + " periodIndexMs=" + execution.PeriodIndexMs
            + " projectionIndexMs=" + execution.ProjectionIndexMs
            + " projectionIndexCacheHit=" + execution.ProjectionIndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + execution.ProjectionIndexStaleRetries
            + " projectionMs=" + execution.ProjectionMs
            + " keywordMs=" + execution.KeywordMs
            + " keywordCount=" + execution.KeywordCount
            + " sortMs=" + sortMs
            + " columnSettingMs=" + columnSettingMs
            + " setViewMs=" + setViewMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
        LogMainViewBuild(
            "main_view_build mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " parameterType=" + (request.Parameter?.GetType().Name ?? "(null)")
            + " playHistoryPeriod=" + periodRequest.Kind
            + " playHistorySortOnly=" + execution.FromSortOnly.ToString().ToLowerInvariant()
            + " readMs=" + execution.ReadMs
            + " periodIndexMs=" + execution.PeriodIndexMs
            + " projectionIndexMs=" + execution.ProjectionIndexMs
            + " projectionIndexCacheHit=" + execution.ProjectionIndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + execution.ProjectionIndexStaleRetries
            + " projectionMs=" + execution.ProjectionMs
            + " keywordMs=" + execution.KeywordMs
            + " keywordCount=" + execution.KeywordCount
            + " sortMs=" + sortMs
            + " sortProfile=" + applyResult.SortProfile
            + " schemaStatus=" + state.SchemaStatus
            + " diagnosticsCount=" + diagnosticsCount
            + " columnSettingMs=" + columnSettingMs
            + " prepareSwapMs=" + prepareSwapMs
            + " setViewMs=" + setViewMs
            + " columnSettingReuse=" + terminalCommit.MainRowsApply.ColumnSettingReuse
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds
            + " sourceCount=" + state.SourceCount
            + " projectedCount=" + state.ProjectedRows.Count
            + " viewCount=" + applyResult.ViewCount);
    }

    private void ReportPlayHistoryReadWorkflowProgress(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        PlayHistoryReadWorkflowProgress progress)
    {
        if (progress?.Stage == PlayHistoryReadWorkflowProgressStage.ReadCompleted)
        {
            if (progress.Lr2SchemaStatusSnapshot != null)
            {
                PublishLr2PlayHistorySchemaStatusSnapshot(progress.Lr2SchemaStatusSnapshot);
            }
            LogPlayHistoryEvent(
                "play_history_read_done",
                "period=" + periodRequest.Kind
                + " requestId=" + requestId
                + " provider=" + progress.Read.Provider
                + " schemaStatus=" + progress.Read.SchemaStatus
                + " cacheHit=" + progress.Read.CacheHit.ToString().ToLowerInvariant()
                + " rows=" + progress.Read.RowCount
                + " diagnosticsCount=" + progress.Read.DiagnosticCount
                + " elapsedMs=" + progress.Read.ElapsedMs);
            return;
        }

        if (progress?.Stage == PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted)
        {
            if (progress.PeriodIndex.Status == PlayHistoryPeriodIndexStageStatus.Completed)
            {
                LogPlayHistoryEvent(
                    "play_history_read_period_index_done",
                    "period=" + periodRequest.Kind
                    + " requestId=" + requestId
                    + " provider=" + progress.Read.Provider
                    + " schemaStatus=" + progress.Read.SchemaStatus
                    + " cacheHit=" + progress.PeriodIndex.CacheHit.ToString().ToLowerInvariant()
                    + " days=" + progress.PeriodIndex.DayCount
                    + " diagnosticsCount=" + progress.PeriodIndex.DiagnosticCount
                    + " elapsedMs=" + progress.PeriodIndex.ElapsedMs);
            }
            else if (progress.PeriodIndex.Status == PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable)
            {
                LogPlayHistoryEvent(
                    "play_history_read_period_index_skipped",
                    "period=" + periodRequest.Kind
                    + " requestId=" + requestId
                    + " provider=" + progress.Read.Provider
                    + " schemaStatus=" + progress.Read.SchemaStatus
                    + " reason=schema_unavailable");
            }
            return;
        }

        if (progress?.Stage != PlayHistoryReadWorkflowProgressStage.ProjectionCompleted)
        {
            return;
        }
        string projectionEventName = progress.Projection.Status switch
        {
            PlayHistoryProjectionStageStatus.SkippedNoRows => "play_history_projection_skipped",
            PlayHistoryProjectionStageStatus.Fallback => "play_history_projection_fallback",
            _ => "play_history_projection_done"
        };
        string projectionReason = progress.Projection.Status switch
        {
            PlayHistoryProjectionStageStatus.Fallback => "projection_index_failed",
            PlayHistoryProjectionStageStatus.SkippedNoRows when progress.PeriodIndex.Status == PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable => "schema_unavailable",
            PlayHistoryProjectionStageStatus.SkippedNoRows => "no_rows",
            _ => string.Empty
        };
        LogPlayHistoryEvent(
            projectionEventName,
            "period=" + periodRequest.Kind
            + " requestId=" + requestId
            + " provider=" + progress.Read.Provider
            + " schemaStatus=" + progress.Read.SchemaStatus
            + " rawCount=" + progress.Projection.RawCount
            + " projectedCount=" + progress.Projection.ProjectedCount
            + " diagnosticsCount=" + progress.Projection.DiagnosticCount
            + " fallback=" + (progress.Projection.Status == PlayHistoryProjectionStageStatus.Fallback).ToString().ToLowerInvariant()
            + " projectionIndexMs=" + progress.Projection.IndexMs
            + " projectionIndexCacheHit=" + progress.Projection.IndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + progress.Projection.IndexStaleRetries
            + " projectionMs=" + progress.Projection.ProjectionMs
            + (string.IsNullOrEmpty(projectionReason) ? string.Empty : " reason=" + projectionReason));
        if (progress.Projection.Failure != null)
        {
            NLogWrapper.FileLogger?.Warn(progress.Projection.Failure, "play_history_projection_index_failed");
        }
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

    private void LogPlayHistoryDisplayTargetFilter(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        PlayHistoryDisplayTargetItem displayTarget,
        int sourceCount,
        int targetCount)
    {
        PlayHistoryDisplayTargetItem safeTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        LogPlayHistoryEvent(
            "play_history_view_display_target_filter",
            "period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " targetKind=" + safeTarget.Kind
            + " targetMode=" + safeTarget.Mode
            + " targetIdentity=" + QuotePlayHistoryLogValue(safeTarget.Identity)
            + " active=" + safeTarget.UsesProjection.ToString().ToLowerInvariant()
            + " rowFiltering=" + safeTarget.IsFiltering.ToString().ToLowerInvariant()
            + " sourceCount=" + sourceCount
            + " targetCount=" + targetCount);
    }

    private void LogPlayHistoryKeywordFilter(PlayHistoryPeriodRequest periodRequest, long requestId, string keywordFilter, int sourceCount, int projectedCount, int keywordCount, long keywordMs)
    {
        LogPlayHistoryEvent(
            "play_history_view_keyword_filter",
            "period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " keywordActive=" + (!string.IsNullOrWhiteSpace(keywordFilter)).ToString().ToLowerInvariant()
            + " sourceCount=" + sourceCount
            + " projectedCount=" + projectedCount
            + " keywordCount=" + keywordCount
            + " elapsedMs=" + keywordMs);
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

    private bool ShouldUseBeatorajaPlayHistoryProvider()
    {
        string scoreDbPath = ResolveMainViewBeatorajaPlayHistoryScoreDbPath();
        return GetStartupSettingsSnapshot().UseBeatorajaScoreDb
            && files?.GetActiveScoreSourceForDiagnostics() == ActiveScoreSource.Beatoraja
            && !string.IsNullOrWhiteSpace(scoreDbPath)
            && File.Exists(scoreDbPath);
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
        bool useBeatorajaProvider = ShouldUseBeatorajaPlayHistoryProvider();
        return useBeatorajaProvider
            ? PlayHistoryReadSourceContext.Beatoraja(
                ResolveMainViewBeatorajaPlayHistoryScoreDbPath(),
                ResolveBeatorajaPlayHistoryScoreContext())
            : PlayHistoryReadSourceContext.Lr2(
                ResolveMainViewLr2PlayHistoryScoreDbPath(),
                settings.OperationModeLR2DB);
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

    private void LogStalePlayHistoryViewRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        long elapsedMs)
    {
        long currentRequestId = playHistoryWorkflowOwner.CurrentRequestId;
        LogPlayHistoryEvent(
            "play_history_view_stale_skipped",
            "mode=" + mode
            + " requestedMode=" + requestedMode
            + " parameterType=" + (parameter?.GetType().Name ?? "(null)")
            + " period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " currentRequestId=" + currentRequestId
            + " elapsedMs=" + elapsedMs);
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + (parameter?.GetType().Name ?? "(null)") + " playHistoryPeriod=" + (periodRequest?.Kind.ToString() ?? string.Empty) + " skipped=true reason=stale_play_history_request requestId=" + requestId + " currentRequestId=" + currentRequestId + " elapsedMs=" + elapsedMs);
    }

    private static void LogPlayHistoryDiagnostics(PlayHistoryPeriodRequest request, string sortProfile, IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        string diagnosticText = string.Join(
            ",",
            (diagnostics ?? [])
                .Take(20)
                .Select(diagnostic => "severity=" + QuotePlayHistoryLogValue(diagnostic?.Severity.ToString())
                    + " stage=" + QuotePlayHistoryLogValue(diagnostic?.Stage)
                    + " code=" + QuotePlayHistoryLogValue(diagnostic?.Code)
                    + " message=" + QuotePlayHistoryLogValue(diagnostic?.Message)
                    + " source=" + QuotePlayHistoryLogValue(diagnostic?.SourcePath)));
        LogMainViewBuildWarning("play_history_diagnostics period=" + (request?.Kind.ToString() ?? string.Empty) + " sortProfile=" + (sortProfile ?? string.Empty) + " count=" + (diagnostics?.Count ?? 0) + " items=" + diagnosticText);
    }

    private static string QuotePlayHistoryLogValue(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
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

    private void SyncBmsonLibraryRowCacheWithoutRebuild(string reason = "bmson_sync_without_rebuild")
    {
        BmsonLibraryRowCacheSyncResult result = regularChartListOwner.SyncBmsonRows(files);
        if (result.SortKeyChanged)
        {
            InvalidateNormalLibrarySortKeysForBmsonSync(result, reason + "_sort_key_changed");
        }
        if (result.SourceChanged)
        {
            IncrementNormalLibrarySourceGenerationForBmsonSync(result, reason);
        }
        if (result.SortKeyChanged || result.SourceChanged)
        {
            LogMainViewBuild("normal_library_bmson_sync reason=" + (reason ?? string.Empty)
                + " membershipChanged=" + result.MembershipChanged
                + " sourceIdentityChanged=" + result.SourceIdentityChanged
                + " sourceReferenceChanged=" + result.SourceReferenceChanged
                + " sortKeyChanged=" + result.SortKeyChanged);
        }
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

    private void FolderAutoRenameWorkflowCompletionPublished(FolderAutoRenameCompletionReceipt receipt)
    {
        if (receipt?.RefreshRequired != true)
        {
            return;
        }
        regularChartListOwner.QueueLatestNormalLibraryRefreshNotification("library_charts_changed");
        InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
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
            if (treeViewFilterTypeSelected == MainViewUpdateMode.FileMissingFilterSelected
                || treeViewFilterTypeSelected == MainViewUpdateMode.FileMissingIgnoredFilterSelected
                || treeViewFilterTypeSelected == MainViewUpdateMode.FullScanAllChartsFilterSelected)
            {
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, filterReason))
                {
                    return;
                }
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
            && !TryDeferStartupPresentationRefresh(UiRefreshChannel.DuplicateTree, refreshReason))
        {
            MaintenanceTree.ApplyDuplicateGroupsPresentation();
        }
        if (treeViewFilterTypeSelected == MainViewUpdateMode.DuplicateFilterSelected)
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, refreshReason))
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

    private void PackageInstallWorkflowCompletionPublished(PackageInstallCompletionReceipt receipt)
    {
        if (receipt?.Packages.Count > 0)
        {
            PlaylistWorkspace.AttachInstalledPackageReferences(receipt.Packages);
        }
    }

    private void PackageInstallWorkflowFailurePublished(PackageInstallFailure failure)
    {
        if (failure?.Exception == null)
        {
            return;
        }
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Msg_failed_installation
                + Environment.NewLine
                + failure.Exception.Message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxImage.Hand);
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

    private void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        DispatchMainChartListAction(() => ProgressHub.UpdatePendingEstimateQueueStatus(snapshot));
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
