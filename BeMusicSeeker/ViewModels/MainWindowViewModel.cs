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
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Codeplex.Data;
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
public partial class MainWindowViewModel : ViewModel
{
    internal event EventHandler InitialSetupLanguageDialogRequested;

    internal event EventHandler InitializationSucceeded;

    /// <summary>
    /// Gets status-bar progress presentation state owned by the composed progress hub.
    /// </summary>
    public OperationProgressHubViewModel ProgressHub { get; }

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
    internal enum StartupProgressOperationKind
    {
        None,
        Startup,
        ReloadFileDiff,
        ScoreOnly,
        FullReinitialize,
        ReloadTables
    }

    /// <summary>
    /// 起動・リロード進捗の内部フェーズ集合です。
    /// </summary>
    [Flags]
    private enum StartupProgressPhase
    {
        None = 0,
        CoreInitializeStarted = 1,
        StartupReadyData = 2,
        StartupReadyUi = 4,
        StartupReadyOperable = 8,
        PlaylistReferenceApplied = 16,
        ExternalPlaylistSyncDone = 32,
        ScoreHydrationDone = 64,
        RankingRefreshDone = 128,
        MaintenanceDeferredDone = 256,
        ChartDigestBackfillDone = 512,
        ChartInfoBackfillDone = 1024,
        ChartInfoHydrationDone = 2048,
        PlaylistEntriesHydrationDone = 4096,
        LibraryDatabaseLoadDone = 8192,
        LibraryFileEnumerationDone = 16384,
        LibraryFileDiffDone = 32768,
        InstallableMaintenanceDeferredDone = 65536,
        Lr2SongDbSyncDone = 131072,
        StartupBackgroundTasksDone = 262144
    }

    /// <summary>
    /// 起動・リロード進捗の内部状態です。
    /// UI 表示は Completed/Expected フェーズ集合から再計算します。
    /// </summary>
    private sealed class StartupProgressState
    {
        internal long OperationToken;

        internal StartupProgressOperationKind OperationKind;

        internal bool IsActive;

        internal bool IsFailed;

        internal bool IsRetryableFailure;

        internal string FailureSubLabel = string.Empty;

        internal StartupProgressPhase CompletedPhases;

        internal StartupProgressPhase ExpectedPhases;

        internal StartupProgressPhase RequestedPhases;

        internal StartupProgressPhase SkippedPhases;

        internal int ScoreHydrationBaselineCompletedVersion;

        internal int ScoreHydrationRequestedBaselineVersion;

        internal int RequiredScoreHydrationCompletedVersion;

        internal int RankingRefreshBaselineCompletedVersion;

        internal int RankingRefreshRequestedBaselineVersion;

        internal int RequiredRankingRefreshCompletedVersion;

        internal int MaintenanceRequestedBaselineVersion;

        internal int InstallableMaintenanceRequestedBaselineVersion;

        internal int ChartDigestBackfillBaselineCompletedVersion;

        internal int ChartInfoBackfillBaselineCompletedVersion;

        internal int ChartInfoHydrationBaselineCompletedVersion;

        internal int Lr2SongDbSyncBaselineCompletedVersion;

        internal int PlaylistEntriesHydrationBaselineCompletedVersion;

        internal int LibraryDatabaseLoadBaselineCompletedVersion;

        internal int LibraryFileEnumerationBaselineCompletedVersion;

        internal int LibraryFileDiffBaselineCompletedVersion;

        internal int RequiredPlaylistReferenceVersion;

        internal bool PlaylistReferenceFromHydrationRequested;

        internal int RequiredExternalSyncVersion;

        internal int RequiredPlaylistEntriesHydrationCompletedVersion;

        internal int RequiredMaintenanceCompletedVersion;

        internal int RequiredInstallableMaintenanceCompletedVersion;

        internal int RequiredChartDigestBackfillCompletedVersion;

        internal int RequiredChartInfoBackfillCompletedVersion;

        internal int RequiredChartInfoHydrationCompletedVersion;

        internal int RequiredLr2SongDbSyncCompletedVersion;

        internal int ChartDigestBackfillTotalCount;

        internal int ChartDigestBackfillProcessedCount;

        internal string ChartDigestBackfillCurrentPath = string.Empty;

        internal int ChartInfoBackfillTotalCount;

        internal int ChartInfoBackfillProcessedCount;

        internal string ChartInfoBackfillCurrentPath = string.Empty;

        internal int ChartInfoHydrationTotalCount;

        internal int ChartInfoHydrationAppliedCount;

        internal int Lr2SongDbSyncTotalCount;

        internal int Lr2SongDbSyncProcessedCount;

        internal string Lr2SongDbSyncStage = string.Empty;

        internal int Lr2SongDbSyncStageProcessedCount;

        internal int Lr2SongDbSyncStageTotalCount;

        internal BMSLibrary.LibraryInitializationProgressStage LibraryInitializationProgressStage;

        internal string LibraryInitializationProgressScannerLabel = string.Empty;

        internal int LibraryInitializationProgressTotalCount;

        internal int LibraryInitializationProgressProcessedCount;

        internal string LibraryInitializationProgressCurrentPath = string.Empty;

        internal bool CompletionHideScheduled;

        internal DateTime? LastCompletedAtUtc;
    }

    internal sealed class StartupProgressTestResult
    {
        internal int ExpectedCount { get; set; }

        internal int CompletedCount { get; set; }

        internal int RequestedCount { get; set; }

        internal int SkippedCount { get; set; }

        internal int IgnoredRequestCount { get; set; }

        internal int IgnoredCompleteCount { get; set; }

        internal string Label { get; set; } = string.Empty;

        internal string SubLabel { get; set; } = string.Empty;

        internal bool IsCompleted { get; set; }

        internal bool IsFailed { get; set; }

        internal double ProgressValue { get; set; }

        internal double ProgressMaximum { get; set; }
    }

    [Flags]
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

    internal bool IsFirstStartup => firstStartupProvider();

    private bool bmsonMigrationApprovedForSession;

    private bool initialSetupCompletionMessagePending;

    private BMSLibrary files;

    private BMSPlaylist tables;

    private readonly ApplicationComposition applicationComposition;

    private Settings ApplicationSettings => applicationComposition.SettingsEditSession.Values;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<bool> firstStartupProvider;

    private readonly Action completeFirstStartup;

    private readonly Action reloadSettings;

    private readonly Action saveSettings;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    private StartupSettingsSnapshot GetStartupSettingsSnapshot()
    {
        return startupSettingsProvider()
            ?? throw new InvalidOperationException("Startup settings provider returned null.");
    }

    private LR2Config lr2config;

    private PropertyChangedEventListener listenerForBMSLibrary;

    private readonly object lockThis = new();

    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ChartFileOperationSynchronizer chartFileOperations = new();

    private readonly SemaphoreSlim packageInstallLibraryGate = new(1, 1);

    private int chartPackageMutationDepth;

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel deferredStartupPresentationMask = UiRefreshChannel.None;

    private readonly object lockUiSuppression = new();

    private readonly object startupInitializationCompletionLock = new();

    private readonly StartupBackgroundTaskSchedulerOwner startupBackgroundTaskScheduler;

    private Stopwatch startupInitializationCompleteStopwatch;

    private bool startupInitializationCompleteLogged;

    private bool startupInitializationCompleteRetryQueued;

    private Stopwatch startupReadyInstallStopwatch;

    private Stopwatch startupReadyOperableStopwatch;

    private bool startupReadyDataLogged;

    private bool startupReadyUiLogged;

    private bool startupReadyDataReached;

    private bool startupReadyUiReached;

    private bool startupReadyOperableReached;

    private string _WindowTitle = "BeMusicSeeker Unofficial Fork - ";

    private PlayHistoryWorkflowOwner playHistoryWorkflowOwner => PlayHistory;

    private PlayHistoryPresentationState playHistoryPresentationState => playHistoryWorkflowOwner.PresentationState;


    private object playHistoryViewRequestLock => playHistoryPresentationState.SyncRoot;

    private Uri _BrowserSource;

    private string _BrowserHtml;

    private readonly RegularChartListOwner regularChartListOwner;

    private long lastMainViewBuildRequestId;

    private long lastMainViewBuildEndTimestamp;

    private int lastMainViewBuildThreadId;

    private int lastMainViewBuildMode;

    private long playlistSyncProgressUiVersion;

    private readonly object startupProgressLock = new();

    private StartupProgressState startupProgressState = new();

    private long startupProgressOperationTokenSeed;

    private bool _IsStartupUiInteractionBlocked;

    private MainViewUpdateMode treeViewFilterTypeSelected;

    private object treeViewFilterParameterSelected;

    public SettingDialogViewModel settingDialog { get; private set; }

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
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
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
    /// playlist 詳細表示時の一覧反映方式を更新します。
    /// </summary>
    /// <param name="playlistDetailActive">playlist 詳細表示中かどうか。</param>
    private void UpdateBmsFilesViewBindingMode(bool playlistDetailActive)
    {
        PlaylistBindingModeCommit commit = PlaylistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive);
        if (playlistDetailActive)
        {
            if (commit.DetailActiveChanged)
            {
                PlaylistWorkspace.InitializePlaylistDetailSort(regularChartListOwner.CaptureSortParameters());
            }
            PlaylistWorkspace.InitializePlaylistDetailFilter(ChartFilters.CaptureSnapshot());
        }
        PlaylistWorkspace.PublishBindingMode(commit);
        if (commit.DetailActiveChanged)
        {
            SyncMainChartListSortPresentation();
        }
    }

    /// <summary>
    /// 現在の playlist open readiness snapshot を返します。
    /// </summary>
    private PlaylistOpenReadinessSnapshot CapturePlaylistOpenReadinessSnapshot()
    {
        bool playlistRefRunning = !PlaylistWorkspace.IsPlaylistReferenceApplyIdle;
        int playlistRefLastCompletedVersion = PlaylistWorkspace.PlaylistReferenceApplyLastCompletedVersion;
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
        Dispatcher dispatcher = DispatcherHelper.UIDispatcher;
        if (dispatcher == null)
        {
            return;
        }
        await dispatcher.InvokeAsync(delegate
        {
        }, DispatcherPriority.ContextIdle).Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(delegate
        {
        }, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
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
        long operationToken = GetActiveStartupProgressOperationToken();
        if (!ShouldDeferStartupPresentationRefresh(channel, operationToken))
        {
            return false;
        }
        UiRefreshChannel deferredChannel = channel & StartupDeferredPresentationChannels;
        if (deferredChannel == UiRefreshChannel.None)
        {
            return false;
        }
        UiRefreshChannel pendingMask;
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask |= deferredChannel;
            pendingMask = deferredStartupPresentationMask;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return true;
    }

    private bool ShouldDeferStartupPresentationRefresh(UiRefreshChannel channel, long operationToken)
    {
        if ((channel & StartupDeferredPresentationChannels) == 0)
        {
            return false;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return false;
        }
        bool initializationCompleteLogged;
        lock (startupInitializationCompletionLock)
        {
            initializationCompleteLogged = startupInitializationCompleteLogged;
        }
        if (initializationCompleteLogged)
        {
            return false;
        }
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive && startupProgressState.OperationKind == StartupProgressOperationKind.Startup;
        }
    }

    private UiRefreshChannel DeferStartupPresentationChannels(UiRefreshChannel mask, long operationToken, string reason)
    {
        UiRefreshChannel deferredChannel = GetStartupPresentationDeferredChannels(mask, reason, CanShowStartupBasicLibraryMainView(treeViewFilterTypeSelected));
        if (deferredChannel == UiRefreshChannel.None || !ShouldDeferStartupPresentationRefresh(deferredChannel, operationToken))
        {
            return mask;
        }
        UiRefreshChannel pendingMask;
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask |= deferredChannel;
            pendingMask = deferredStartupPresentationMask;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return mask & ~deferredChannel;
    }

    private static bool CanShowStartupBasicLibraryMainView(MainViewUpdateMode currentTreeMode)
    {
        return currentTreeMode == MainViewUpdateMode.FolderFilterSelected
            || currentTreeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected;
    }

    private static UiRefreshChannel GetStartupBasicPresentationChannels(bool includeLibraryMainView)
    {
        UiRefreshChannel channels = StartupBasicPresentationChannels;
        if (includeLibraryMainView)
        {
            channels |= UiRefreshChannel.LibraryMainView;
        }
        return channels;
    }

    private static UiRefreshChannel GetStartupPresentationDeferredChannels(UiRefreshChannel mask, string reason, bool includeBasicLibraryMainView)
    {
        UiRefreshChannel deferredChannel = mask & StartupDeferredPresentationChannels;
        if (string.Equals(reason, StartupUiSuppressFlushReason, StringComparison.Ordinal))
        {
            deferredChannel &= ~GetStartupBasicPresentationChannels(includeBasicLibraryMainView);
        }
        return deferredChannel;
    }

    internal static bool IsStartupPresentationDeferredForTest(
        MainViewUpdateMode currentTreeMode,
        bool startupUiSuppressFlush,
        bool libraryMainView,
        bool libraryFolderTree,
        bool playlistTree,
        bool duplicateTree)
    {
        UiRefreshChannel mask = UiRefreshChannel.None;
        if (libraryMainView)
        {
            mask |= UiRefreshChannel.LibraryMainView;
        }
        if (libraryFolderTree)
        {
            mask |= UiRefreshChannel.LibraryFolderTree;
        }
        if (playlistTree)
        {
            mask |= UiRefreshChannel.PlaylistTree;
        }
        if (duplicateTree)
        {
            mask |= UiRefreshChannel.DuplicateTree;
        }
        string reason = startupUiSuppressFlush ? StartupUiSuppressFlushReason : "background_hydration";
        return GetStartupPresentationDeferredChannels(mask, reason, CanShowStartupBasicLibraryMainView(currentTreeMode)) != UiRefreshChannel.None;
    }

    private void InvokePlaylistSummaryPresentationRefreshGate(Action<bool> request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        lock (lockUiSuppression)
        {
            request(suppressUiUpdateDepth > 0);
        }
    }

    private void InvokePlaylistSummaryDataRefreshGate(Action<bool> request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        lock (lockUiSuppression)
        {
            UiRefreshChannel summaryMask = UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView;
            bool deferred = suppressUiUpdateDepth > 0
                || (deferredStartupPresentationMask & summaryMask) != UiRefreshChannel.None;
            request(deferred);
            if (suppressUiUpdateDepth > 0)
            {
                pendingUiRefreshMask |= suppressedUiRefreshMask & summaryMask;
            }
        }
    }

    private long GetActiveStartupProgressOperationToken()
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive ? startupProgressState.OperationToken : 0L;
        }
    }

    private bool IsStartupProgressOperationTokenCurrent(long operationToken)
    {
        if (operationToken == 0L)
        {
            return true;
        }
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive && startupProgressState.OperationToken == operationToken;
        }
    }

    private void EndUiUpdateSuppression()
    {
        long operationToken = GetActiveStartupProgressOperationToken();
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
        DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
        {
            if (!IsStartupProgressOperationTokenCurrent(operationToken))
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
        });
    }

    private void TryLogStartupReadyData()
    {
        TryLogStartupReadyData(GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyData(long operationToken)
    {
        if (startupReadyInstallStopwatch == null || startupReadyDataLogged)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_data elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        startupReadyDataLogged = true;
        startupReadyDataReached = true;
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData);
    }

    private void TryLogStartupReadyUi(UiRefreshChannel mask)
    {
        TryLogStartupReadyUi(mask, GetActiveStartupProgressOperationToken());
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
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        bool flag = (mask & UiRefreshChannel.PlaylistTree) != 0;
        LogUiSuppression("startup_ready_ui elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds + " playlistRefreshed=" + flag.ToString().ToLowerInvariant());
        startupReadyUiLogged = true;
        startupReadyUiReached = true;
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyUi);
    }

    private static bool IsStartupReadyUiMaskSatisfied(UiRefreshChannel mask)
    {
        return (mask & UiRefreshChannel.InstallTree) != 0;
    }

    internal static bool IsStartupReadyUiMaskSatisfiedForTest(bool installTree, bool libraryMainView, bool playlistTree)
    {
        UiRefreshChannel mask = UiRefreshChannel.None;
        if (installTree)
        {
            mask |= UiRefreshChannel.InstallTree;
        }
        if (libraryMainView)
        {
            mask |= UiRefreshChannel.LibraryMainView;
        }
        if (playlistTree)
        {
            mask |= UiRefreshChannel.PlaylistTree;
        }
        return IsStartupReadyUiMaskSatisfied(mask);
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask)
    {
        TryLogStartupReadyInstall(mask, GetActiveStartupProgressOperationToken());
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
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
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
        TryLogStartupReadyOperable(GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyOperable(long operationToken)
    {
        if (startupReadyOperableStopwatch == null)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_operable elapsedMs=" + startupReadyOperableStopwatch.ElapsedMilliseconds);
        startupReadyOperableStopwatch = null;
        startupReadyOperableReached = true;
        SetStartupUiInteractionBlocked(false);
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
        startupBackgroundTaskScheduler.Start();
        TryCompleteStartupBackgroundTasksPhaseIfIdle();
    }

    private void TryCompleteStartupBackgroundTasksPhaseIfIdle()
    {
        if (!startupBackgroundTaskScheduler.IsStarted || !startupBackgroundTaskScheduler.IsIdle)
        {
            return;
        }

        bool shouldComplete;
        lock (startupProgressLock)
        {
            StartupProgressPhase expectedExceptBackgroundTasks =
                startupProgressState.ExpectedPhases & ~StartupProgressPhase.StartupBackgroundTasksDone;
            shouldComplete = startupProgressState.IsActive
                && (startupProgressState.ExpectedPhases & StartupProgressPhase.StartupBackgroundTasksDone) != 0
                && (startupProgressState.CompletedPhases & StartupProgressPhase.StartupBackgroundTasksDone) == 0
                && (startupProgressState.CompletedPhases & expectedExceptBackgroundTasks) == expectedExceptBackgroundTasks;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupBackgroundTasksDone);
        }
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

    private void LibraryFolderTreeCacheRefreshRequested(object sender, EventArgs e)
    {
        if (TrySuppress(UiRefreshChannel.LibraryFolderTree))
        {
            return;
        }
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryFolderTree, "parent_folder_cache_changed"))
        {
            return;
        }
        LibraryFolderTree.ScheduleDeferredRefresh(GetActiveStartupProgressOperationToken());
    }

    private void LibraryFolderTreeDeferredRefreshCompleted(
        object sender,
        LibraryFolderTreeRefreshCompletedEventArgs e)
    {
        TryLogStartupReadyOperable(e.OperationToken);
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
        FlushPendingUiRefresh(mask, GetActiveStartupProgressOperationToken());
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
            RefreshPlayHistoryDisplayTargets();
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
            LibraryFolderTree.ScheduleDeferredRefresh(operationToken);
        }
        else if (logReadiness)
        {
            TryLogStartupReadyOperable(operationToken);
        }
    }

    private void BeginChartPackageMutation()
    {
        if (Interlocked.Increment(ref chartPackageMutationDepth) == 1)
        {
            RaisePropertyChanged(() => IsChartPackageMutationInProgress);
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
        }
    }

    private void EndChartPackageMutation()
    {
        int depth = Interlocked.Decrement(ref chartPackageMutationDepth);
        if (depth == 0)
        {
            RaisePropertyChanged(() => IsChartPackageMutationInProgress);
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
        }
        else if (depth < 0)
        {
            Interlocked.Exchange(ref chartPackageMutationDepth, 0);
            RaisePropertyChanged(() => IsChartPackageMutationInProgress);
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
        }
    }

    private void PackageCatalogMutationPhasePublished(
        object sender,
        PackageCatalogMutationPhaseEventArgs e)
    {
        switch (e.Phase)
        {
            case PackageCatalogMutationPhase.ActivityStarted:
                BeginChartPackageMutation();
                break;
            case PackageCatalogMutationPhase.RefreshSuppressionStarted:
                BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
                break;
            case PackageCatalogMutationPhase.RefreshSuppressionEnded:
                EndUiUpdateSuppression();
                break;
            case PackageCatalogMutationPhase.ActivityEnded:
                EndChartPackageMutation();
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
            case DuplicateMaintenanceActivityChangedEventArgs activityChanged:
                if (activityChanged.IsActive)
                {
                    BeginChartPackageMutation();
                }
                else
                {
                    EndChartPackageMutation();
                }
                break;
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
            case SelectedChartMutationActivityChangedEventArgs activityChanged:
                if (activityChanged.IsActive)
                {
                    BeginChartPackageMutation();
                }
                else
                {
                    EndChartPackageMutation();
                }
                break;
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
                    regularChartListOwner.ApplyLatestNormalLibraryRefreshNotification("library_charts_changed");
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
            case PendingPackageActivityChangedEventArgs activityChanged:
                if (activityChanged.IsActive)
                {
                    BeginChartPackageMutation();
                }
                else
                {
                    EndChartPackageMutation();
                }
                break;
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

    private void RunChartPackageMutation(
        Action action,
        IEnumerable<ChartFile> playbackTargetCharts = null,
        UiRefreshChannel refreshMask = UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree,
        Action stopPlayback = null,
        Action beforeAction = null,
        Action afterUiRefresh = null,
        bool requiresLibrary = true)
    {
        if (action == null)
        {
            throw new ArgumentNullException("action");
        }
        if (requiresLibrary && files == null)
        {
            return;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        bool mutationStarted = false;
        try
        {
            dialogScope = files?.BeginOperationDialogScope();
            mutationStarted = true;
            BeginChartPackageMutation();
            using (chartFileOperations.Enter())
            {
                if (stopPlayback != null)
                {
                    stopPlayback();
                }
                else if (playbackTargetCharts != null)
                {
                    PlaybackPanel.StopIfPlayingCharts(playbackTargetCharts);
                }
                bool uiSuppressionStarted = refreshMask != UiRefreshChannel.None;
                if (uiSuppressionStarted)
                {
                    BeginUiUpdateSuppression(refreshMask);
                }
                try
                {
                    beforeAction?.Invoke();
                    action();
                }
                finally
                {
                    try
                    {
                        if (uiSuppressionStarted)
                        {
                            EndUiUpdateSuppression();
                        }
                    }
                    finally
                    {
                        afterUiRefresh?.Invoke();
                    }
                }
            }
        }
        finally
        {
            if (mutationStarted)
            {
                EndChartPackageMutation();
            }
            dialogScope?.Dispose();
            dialogScope?.Flush();
        }
    }

    private T RunChartPackageMutation<T>(
        Func<T> func,
        IEnumerable<ChartFile> playbackTargetCharts = null,
        UiRefreshChannel refreshMask = UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree,
        Action stopPlayback = null,
        Action beforeAction = null,
        Action afterUiRefresh = null,
        bool requiresLibrary = true)
    {
        if (func == null)
        {
            throw new ArgumentNullException("func");
        }
        if (requiresLibrary && files == null)
        {
            return default;
        }
        T result = default;
        BMSLibrary.OperationDialogScope dialogScope = null;
        bool mutationStarted = false;
        try
        {
            dialogScope = files?.BeginOperationDialogScope();
            mutationStarted = true;
            BeginChartPackageMutation();
            using (chartFileOperations.Enter())
            {
                if (stopPlayback != null)
                {
                    stopPlayback();
                }
                else if (playbackTargetCharts != null)
                {
                    PlaybackPanel.StopIfPlayingCharts(playbackTargetCharts);
                }
                bool uiSuppressionStarted = refreshMask != UiRefreshChannel.None;
                if (uiSuppressionStarted)
                {
                    BeginUiUpdateSuppression(refreshMask);
                }
                try
                {
                    beforeAction?.Invoke();
                    result = func();
                }
                finally
                {
                    try
                    {
                        if (uiSuppressionStarted)
                        {
                            EndUiUpdateSuppression();
                        }
                    }
                    finally
                    {
                        afterUiRefresh?.Invoke();
                    }
                }
            }
        }
        finally
        {
            if (mutationStarted)
            {
                EndChartPackageMutation();
            }
            dialogScope?.Dispose();
            dialogScope?.Flush();
        }
        return result;
    }

    private void RunPendingInstallMutation(Action action, IEnumerable<ChartFile> playbackTargetCharts = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
    {
        RunChartPackageMutation(action, playbackTargetCharts, UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | extraMask);
    }

    private T RunPendingInstallMutation<T>(Func<T> func, IEnumerable<ChartFile> playbackTargetCharts = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
    {
        return RunChartPackageMutation(func, playbackTargetCharts, UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | extraMask);
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

    internal MainViewOperationSection CurrentMainViewOperationSection => ResolveMainViewOperationSection(treeViewFilterTypeSelected);

    internal ChartOperationSourceScope CurrentMainViewChartOperationSourceScope => ResolveMainViewChartOperationSourceScope(CurrentMainViewOperationSection);

    internal static MainViewOperationSection ResolveMainViewOperationSection(MainViewUpdateMode mode)
    {
        return mode switch
        {
            MainViewUpdateMode.PendingInstallFolderSelected => MainViewOperationSection.InstallPending,
            MainViewUpdateMode.NewlyInstalledFolderSelected => MainViewOperationSection.InstallInstalled,
            MainViewUpdateMode.PlaylistFilterSelected or MainViewUpdateMode.PlaylistNotOwnedFilterSelected => MainViewOperationSection.Playlist,
            MainViewUpdateMode.FullScanAllChartsFilterSelected or MainViewUpdateMode.FileMissingFilterSelected or MainViewUpdateMode.FileMissingIgnoredFilterSelected => MainViewOperationSection.FullScanCheck,
            MainViewUpdateMode.ChartInfoParseErrorFilterSelected => MainViewOperationSection.ChartInfoParseError,
            MainViewUpdateMode.PlayHistorySelected => MainViewOperationSection.PlayHistory,
            _ => MainViewOperationSection.Library,
        };
    }

    internal static ChartOperationSourceScope ResolveMainViewChartOperationSourceScope(MainViewOperationSection section)
    {
        return section switch
        {
            MainViewOperationSection.InstallPending => ChartOperationSourceScope.PendingPackage,
            MainViewOperationSection.InstallInstalled => ChartOperationSourceScope.NewlyInstalledPackage,
            _ => ChartOperationSourceScope.Library,
        };
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
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            notify();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(notify);
        }
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

    private void SchedulePostStartupBestEffortWarmups(string reason)
    {
        Lr2SongDbSyncWorkflow.SchedulePostStartupSync(reason);
        ScheduleVirtualNormalLibraryOrderPrewarm(reason);
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

    private void ScheduleVirtualNormalLibraryOrderPrewarm(string reason)
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
        try
        {
            _ = Task.Run(() =>
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
            });
        }
        catch
        {
            lease.Dispose();
            throw;
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
        bool isPlayHistory = CurrentMainViewOperationSection == MainViewOperationSection.PlayHistory;
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

    public Uri BrowserSource
    {
        get
        {
            return _BrowserSource;
        }
        set
        {
            if (!(_BrowserSource == value))
            {
                _BrowserSource = value;
                RaisePropertyChanged("BrowserSource");
            }
        }
    }

    public string BrowserHtml
    {
        get
        {
            return _BrowserHtml;
        }
        set
        {
            if (!(_BrowserHtml == value))
            {
                _BrowserHtml = value;
                RaisePropertyChanged("BrowserHtml");
            }
        }
    }

    public bool IsStartupUiInteractionBlocked
    {
        get
        {
            return _IsStartupUiInteractionBlocked;
        }
    }

    internal void SetStartupUiInteractionBlocked(bool value)
    {
        if (_IsStartupUiInteractionBlocked == value)
        {
            return;
        }
        _IsStartupUiInteractionBlocked = value;
        RaisePropertyChanged("IsStartupUiInteractionBlocked");
        RaisePropertyChanged("IsLibraryOperationInProgress");
    }

    public bool IsLibraryOperationInProgress
    {
        get
        {
            lock (startupProgressLock)
            {
                return _IsStartupUiInteractionBlocked
                    || (startupProgressState.IsActive
                        && (!startupProgressState.IsFailed || !startupProgressState.IsRetryableFailure))
                    || IsChartPackageMutationInProgress;
            }
        }
    }

    public bool IsChartPackageMutationInProgress => Volatile.Read(ref chartPackageMutationDepth) > 0;

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
        lock (playHistoryViewRequestLock)
        {
            if (treeViewFilterTypeSelected == MainViewUpdateMode.PlayHistorySelected)
            {
                playHistoryWorkflowOwner.UpdateKeywordIdentity(
                    NormalizePlaylistKeywordFilter(filters.KeywordFilter),
                    advanceRevision: true);
            }
        }
        if (treeViewFilterTypeSelected == MainViewUpdateMode.PlayHistorySelected)
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

    private void RefreshPlayHistoryDisplayTargets(bool queueRefreshWhenSelectionChanges = true)
    {
        PlayHistory.ReplaceDisplayTargetCatalog(
            PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot(),
            queueRefreshWhenSelectionChanges);
    }

    private void SchedulePlayHistoryDisplayTargetCatalogRefresh(Action refresh)
    {
        Dispatcher dispatcher = DispatcherHelper.UIDispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Task.Run(refresh).Logging("QueuePlayHistoryDisplayTargetsRefresh");
            return;
        }
        dispatcher.BeginInvoke(refresh, DispatcherPriority.Background);
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

    public int LR2ID
    {
        get
        {
            if (files != null)
            {
                return files.LR2ID;
            }
            return 0;
        }
    }

    public bool IS_WIN8OR10 => Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012);

    /// <summary>
    /// <see cref="MainWindowViewModel"/> クラスの新しいインスタンスを初期化します。
    /// 設定情報に基づくプレースホルダーの初期状態設定や、内包される <see cref="SettingDialogViewModel"/> の生成を行います。
    /// </summary>
    public MainWindowViewModel()
        : this(ApplicationComposition.CreateDefault())
    {
    }

    internal MainWindowViewModel(ApplicationComposition composition)
    {
        if (composition == null)
        {
            throw new ArgumentNullException(nameof(composition));
        }
        applicationComposition = composition;
        startupBackgroundTaskScheduler = new StartupBackgroundTaskSchedulerOwner(
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            LogUiSuppression,
            LogUiSuppressionWarning,
            LogShutdown,
            FormatTextForLog,
            TryCompleteStartupBackgroundTasksPhaseIfIdle);
        treeViewFilterTypeSelected = ApplicationSettings.StartupSelectInstallPending
            ? MainViewUpdateMode.PendingInstallFolderSelected
            : MainViewUpdateMode.FolderFilterSelected;
        startupSettingsProvider = composition.StartupSettingsProvider;
        customFolderOutputSettingsProvider = composition.CustomFolderOutputSettingsProvider;
        firstStartupProvider = composition.FirstStartupProvider;
        completeFirstStartup = composition.CompleteFirstStartup;
        reloadSettings = composition.ReloadSettings;
        saveSettings = composition.SaveSettings;
        playHistoryDisplaySettingsStore = composition.PlayHistoryDisplaySettingsStore;
        MainChartList = composition.CreateMainChartListViewModel(
            DispatchMainChartListPresentationAction,
            LogMainViewBuild);
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
            uri => Process.Start(uri.ToString()),
            () => PlaylistUrlInstallTreeExpansionRequested?.Invoke(),
            request => PlaylistSummarySelectionRestoreRequested?.Invoke(request),
            LogExternalPlaylistImportWarning,
            LogExternalPlaylistImportInfo,
            LogBeatorajaTableUrlImportWarning,
            LogBeatorajaTableUrlImportInfo,
            () => files,
            () => lr2config,
            LogPlaylistSummaryBulkWarning,
            new DispatcherCollection<BMSTable>(DispatcherHelper.UIDispatcher),
            (reason, work) => startupBackgroundTaskScheduler.Queue("playlist_library_index_prewarm", reason, null, work),
            () => startupReadyOperableReached,
            () => treeViewFilterTypeSelected,
            WaitForPlaylistReloadCleanupDispatcherIdleAsync,
            () => ShellShutdownWorkflow?.IsShutdownRequested == true,
            CollectPlaylistReloadCleanupGarbage,
            LogPlaylistReload,
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message),
            InvokePlaylistSummaryPresentationRefreshGate,
            InvokePlaylistSummaryDataRefreshGate,
            () => TrySuppress(UiRefreshChannel.PlaylistTree),
            reason => TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, reason),
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
                shutdownReason => PlaylistWorkspace.DiscardPlaylistReferenceApplyForShutdown(shutdownReason)),
            ApplyMainChartListPresentationActionAsync,
            () => DispatcherHelper.UIDispatcher.CheckAccess());
        PlaylistWorkspace.TreeSelectionActivated += PlaylistWorkspaceTreeSelectionActivated;
        PlaylistWorkspace.PlaylistDetailScoreSnapshotRefreshRequested += PlaylistWorkspacePlaylistDetailScoreSnapshotRefreshRequested;
        PlaylistWorkspace.MutationRejected += PlaylistWorkspaceMutationRejected;
        PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += PlaylistWorkspacePlaylistOperationNotificationPresentationRequested;
        PlaylistWorkspace.PlaylistReferenceSortInvalidationRequested += PlaylistWorkspacePlaylistReferenceSortInvalidationRequested;
        PlaylistWorkspace.PlaylistTableRemovalInvalidOutputDirectoryRequested += PlaylistWorkspacePlaylistTableRemovalInvalidOutputDirectoryRequested;
        PlaylistWorkspace.PlaylistTableRemovalConfirmationRequested += PlaylistWorkspacePlaylistTableRemovalConfirmationRequested;
        PlaylistWorkspace.PlaylistTableLevelOverwriteConfirmationRequested += PlaylistWorkspacePlaylistTableLevelOverwriteConfirmationRequested;
        PlaylistWorkspace.PlaylistSummaryColumnResetConfirmationRequested += PlaylistWorkspacePlaylistSummaryColumnResetConfirmationRequested;
        PlaylistWorkspace.PlaylistRecommendedTableImportConfirmationRequested += PlaylistWorkspacePlaylistRecommendedTableImportConfirmationRequested;
        PlaylistWorkspace.PlaylistSummaryExternalSyncConfirmationRequested += PlaylistWorkspacePlaylistSummaryExternalSyncConfirmationRequested;
        PlaylistWorkspace.PlaylistSummaryRemovalConfirmationRequested += PlaylistWorkspacePlaylistSummaryRemovalConfirmationRequested;
        PlaylistWorkspace.PlaylistFolderRemovalConfirmationRequested += PlaylistWorkspacePlaylistFolderRemovalConfirmationRequested;
        PlaylistWorkspace.ExternalPlaylistImportQueueSummaryReady += PlaylistWorkspaceExternalPlaylistImportQueueSummaryReady;
        PlaylistWorkspace.ExternalPlaylistImportSummaryRefreshFailed += PlaylistWorkspaceExternalPlaylistImportSummaryRefreshFailed;
        PlaylistWorkspace.BeatorajaTableUrlImportConfirmationRequested += PlaylistWorkspaceBeatorajaTableUrlImportConfirmationRequested;
        PlaylistWorkspace.BeatorajaTableUrlImportNotificationRequested += PlaylistWorkspaceBeatorajaTableUrlImportNotificationRequested;
        PlaylistWorkspace.BeatorajaTableUrlImportSummaryReady += PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady;
        PlaylistWorkspace.PlaylistSummaryBulkInvalidOutputDirectoryRequested += PlaylistWorkspacePlaylistSummaryBulkInvalidOutputDirectoryRequested;
        PlaylistWorkspace.PlaylistSyncProgressChanged += PlaylistWorkspacePlaylistSyncProgressChanged;
        PlaylistWorkspace.PlaylistDetailReloadRefreshRequested += PlaylistWorkspacePlaylistDetailReloadRefreshRequested;
        PlaylistWorkspace.PlaylistPropertyValidationError += PlaylistWorkspacePlaylistPropertyValidationError;
        PlaylistWorkspace.PlaylistPropertyExternalSyncConfirmationRequested += PlaylistWorkspacePlaylistPropertyExternalSyncConfirmationRequested;
        PlaylistWorkspace.PlaylistPropertyInvalidOutputDirectoryRequested += PlaylistWorkspacePlaylistPropertyInvalidOutputDirectoryRequested;
        PlaylistWorkspace.PlaylistPropertyExternalSyncFailed += PlaylistWorkspacePlaylistPropertyExternalSyncFailed;
        PlaylistWorkspace.PlaylistUrlDownloadStatusChanged += PlaylistWorkspacePlaylistUrlDownloadStatusChanged;
        PlaylistWorkspace.PlaylistUrlAcquisitionConfirmationRequested += PlaylistWorkspacePlaylistUrlAcquisitionConfirmationRequested;
        PlaylistWorkspace.PlaylistUrlAcquisitionNotificationRequested += PlaylistWorkspacePlaylistUrlAcquisitionNotificationRequested;
        PlaylistWorkspace.PlaylistUrlAcquisitionSummaryReady += PlaylistWorkspacePlaylistUrlAcquisitionSummaryReady;
        PlaylistWorkspace.PlaylistTablesPresentationChanged += PlaylistWorkspacePlaylistTablesPresentationChanged;
        PlaylistWorkspace.PlaylistKeywordValueCandidatesChanged += PlaylistWorkspacePlaylistKeywordValueCandidatesChanged;
        PlaylistWorkspace.PlaylistEntriesHydrationRequested += PlaylistWorkspacePlaylistEntriesHydrationRequested;
        PlaylistWorkspace.PlaylistEntriesHydrationCompleted += PlaylistWorkspacePlaylistEntriesHydrationCompleted;
        PlaylistWorkspace.PlaylistExternalSyncQueued += PlaylistWorkspacePlaylistExternalSyncQueued;
        PlaylistWorkspace.PlaylistExternalSyncCompleted += PlaylistWorkspacePlaylistExternalSyncCompleted;
        PlaylistWorkspace.PlaylistExternalSyncReferenceApplied += PlaylistWorkspacePlaylistExternalSyncReferenceApplied;
        PlaylistWorkspace.PlaylistReferenceApplyQueued += PlaylistWorkspacePlaylistReferenceApplyQueued;
        PlaylistWorkspace.PlaylistReferenceApplyCompleted += PlaylistWorkspacePlaylistReferenceApplyCompleted;
        PlaylistWorkspace.PlaylistReferenceApplyPresentationRequested += PlaylistWorkspacePlaylistReferenceApplyPresentationRequested;
        MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
            MainChartList,
            PlaylistWorkspace,
            applicationComposition.CreateDefaultBmsPlayer,
            () => DispatcherHelper.UIDispatcher,
            chartFileOperations,
            LogMainViewBuild,
            DispatchMainChartListAction,
             LogMainViewBuildWarning,
             ExecutePackageInstallMutation,
             DispatchPackageInstallUi,
             () => files,
             new UiDialogCoordinator(),
            ReportPackageInstallWorkflowNotificationFailure,
            (library, progress, cancellationToken) => library.RescanAllOwnedChartMaintenance(progress, cancellationToken),
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportMaintenanceRescanWorkflowNotificationFailure,
            ReportMaintenanceRescanWorkflowFailure,
            ExecuteFolderAutoRenameSelectedMutation,
            ExecuteFolderAutoRenameAllMutation,
            HasFolderAutoRenameAllTargets,
            action => Task.Run(action),
            message => NLogWrapper.FileLogger?.Info(message),
            ReportFolderAutoRenameWorkflowNotificationFailure,
            ReportFolderAutoRenameWorkflowFailure,
            new UiDialogCoordinator(),
            zeroNoteLibraryProvider: () => files,
            packageCatalogLibraryProvider: () => files,
            duplicateMaintenanceDialogService: new UiDialogCoordinator(),
            showDuplicateFileCheckConfirmProvider: () => ApplicationSettings.ShowDuplicateFileCheckConfirmMsg,
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
                    () => ApplicationSettings.OperationModeLR2DB),
                new UiDialogCoordinator()),
            rankingCacheDownloadWorkflow: new RankingCacheDownloadWorkflowOwner(
                new BmsRankingCacheDownloadRuntime(() => files),
                chartFileOperations,
                new UiDialogCoordinator()),
            libraryFolderTreeLog: LogUiSuppression,
            libraryFolderTreeLogWarning: LogUiSuppressionWarning);
        ProgressHub = childComposition.ProgressHub;
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
        PackageInstallWorkflow.StatusChanged += PackageInstallWorkflowStatusChanged;
        PackageInstallWorkflow.CompletionPublished += PackageInstallWorkflowCompletionPublished;
        PackageInstallWorkflow.FailurePublished += PackageInstallWorkflowFailurePublished;
        MaintenanceRescanWorkflow = childComposition.MaintenanceRescanWorkflow;
        MaintenanceRescanWorkflow.ProgressChanged += MaintenanceRescanWorkflowProgressChanged;
        MaintenanceRescanWorkflow.CompletionPublished += MaintenanceRescanWorkflowCompletionPublished;
        FolderAutoRenameWorkflow = childComposition.FolderAutoRenameWorkflow;
        FolderAutoRenameWorkflow.ProgressChanged += FolderAutoRenameWorkflowProgressChanged;
        FolderAutoRenameWorkflow.CompletionPublished += FolderAutoRenameWorkflowCompletionPublished;
        StartupUpdateWorkflow = childComposition.StartupUpdateWorkflow;
        ElevatedProcessWarningWorkflow = childComposition.ElevatedProcessWarningWorkflow;
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
        ProgressHub.PropertyChanged += ProgressHubPropertyChanged;
        PlaylistWorkspace.PlaylistDetailSortChanged += PlaylistWorkspacePlaylistDetailSortChanged;
        PlaylistWorkspace.PlaylistDetailFilterChanged += PlaylistWorkspacePlaylistDetailFilterChanged;
        regularChartListOwner = childComposition.RegularChartListOwner;
        regularChartListOwner.NormalLibraryRefreshApplied += RegularChartListOwnerNormalLibraryRefreshApplied;
        regularChartListOwner.TreeNavigationPresentationRequested += RegularChartListOwnerTreeNavigationPresentationRequested;
        regularChartListOwner.MaintenanceNavigationPresentationRequested += RegularChartListOwnerMaintenanceNavigationPresentationRequested;
        regularChartListOwner.InstallNavigationPresentationRequested += RegularChartListOwnerInstallNavigationPresentationRequested;
        MainChartList.SortRequested += MainChartListSortRequested;
        MainChartList.CellEditBeginningRequested += MainChartListCellEditBeginningRequested;
        MainChartList.CellEditStarted += MainChartListCellEditStarted;
        MainChartList.CellEditEndedRequested += MainChartListCellEditEndedRequested;
        regularChartListOwner.SortChanged += RegularChartListOwnerSortChanged;
        regularChartListOwner.SortRefreshRequested += ChartListOwnerSortRefreshRequested;
        regularChartListOwner.FolderEditRequested += RegularChartListOwnerFolderEditRequested;

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
            _semaphore,
            SetStartupUiInteractionBlocked,
            App.MarkCoordinatedShutdownStarted,
            DispatchShellShutdownActionAsync,
            LogShutdown,
            LogShutdownWarning,
            FormatTextForLog);
        PlayHistory.SortChanged += PlayHistorySortChanged;
        PlayHistory.SortRefreshRequested += ChartListOwnerSortRefreshRequested;
        PlayHistory.RestoreDisplayTargetIdentity(playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity);
        RefreshPlayHistoryDisplayTargetSetsFromSettings(queueRefreshWhenSelectionChanges: false);
        settingDialog = applicationComposition.CreateSettingDialogViewModel(this);
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

    private void RegularChartListOwnerFolderEditRequested(
        object sender,
        RegularChartFolderEditRequestedEventArgs request)
    {
        Task.Run(() =>
        {
            try
            {
                RenameChartFolder(request.Request, request.FolderName);
            }
            finally
            {
                MainChartList.RequestDisplayRefresh();
            }
        }).Logging("regularChartListFolderEditRequested");
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
            MainViewOperationSection previousOperationSection = CurrentMainViewOperationSection;
            SetTreeViewFilterSelection(MainViewUpdateMode.PlaylistFilterSelected, null);
            PlayHistory.ClearSummaryPresentation();
            if (previousOperationSection != CurrentMainViewOperationSection)
            {
                RaisePropertyChanged(() => CurrentMainViewOperationSection);
                SyncMainChartListSortPresentation();
            }
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

    private static void PlaylistWorkspaceMutationRejected(
        object sender,
        PlaylistWorkspaceMutationRejectedEventArgs request)
    {
        string message = request.Kind switch
        {
            PlaylistWorkspaceMutationKind.RenameFolder => BeMusicSeeker.Properties.Resources.Msg_failed_rename_playlist_folder,
            PlaylistWorkspaceMutationKind.RemoveFolder => BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_folder,
            PlaylistWorkspaceMutationKind.CreateFolder => BeMusicSeeker.Properties.Resources.Msg_failed_create_playlist_folder,
            PlaylistWorkspaceMutationKind.AddEntries => BeMusicSeeker.Properties.Resources.Msg_failed_add_playlist_entry,
            PlaylistWorkspaceMutationKind.RemoveEntries => BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_entry,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, null)
        };
        ShowUiMessage(message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    private void PlaylistWorkspacePlaylistSummaryBulkInvalidOutputDirectoryRequested(
        object sender,
        PlaylistSummaryBulkInvalidOutputDirectoryEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            request.RouteName);
    }

    private void PlaylistWorkspacePlaylistSyncProgressChanged(
        object sender,
        PlaylistSyncProgressChangedEventArgs request)
    {
        UpdatePlaylistSyncProgressStatus(request.Snapshot);
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
        if (CurrentMainViewOperationSection != MainViewOperationSection.PlayHistory && !IsPlaylistDetailWorkflowActive)
        {
            SyncMainChartListSortPresentation();
        }
    }

    private void PlayHistorySortChanged(object sender, MainChartListSortRequestedEventArgs request)
    {
        if (CurrentMainViewOperationSection == MainViewOperationSection.PlayHistory)
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

        if (PlaylistWorkspace.SetPlaylistSummaryMode(enabled: false))
        {
            UpdateChartKeywordSearchContext();
        }
        PlaylistWorkspace.ClearPlaylistDetailSelection();
        MainViewOperationSection previousOperationSection = CurrentMainViewOperationSection;
        lock (playHistoryViewRequestLock)
        {
            treeViewFilterTypeSelected = MainViewUpdateMode.PlayHistorySelected;
            treeViewFilterParameterSelected = request;
        }
        UpdateChartKeywordSearchContext();
        if (previousOperationSection != CurrentMainViewOperationSection)
        {
            RaisePropertyChanged(() => CurrentMainViewOperationSection);
            RaisePropertyChanged(() => CurrentMainViewChartOperationSourceScope);
            SyncMainChartListSortPresentation();
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
                ? CurrentMainViewOperationSection == MainViewOperationSection.PlayHistory
                : CurrentMainViewOperationSection != MainViewOperationSection.PlayHistory && !IsPlaylistDetailWorkflowActive))
        {
            RefreshChartRowsView(MainViewUpdateMode.SortUpdated, expectedSortTarget: request.Target);
        }
    }

    private static void DispatchMainChartListAction(Action action)
    {
        if (action == null)
        {
            return;
        }
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(action);
        }
    }

    private static Task DispatchShellShutdownActionAsync(Func<Task> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        Dispatcher dispatcher = DispatcherHelper.UIDispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return action();
        }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromException(new InvalidOperationException("The UI dispatcher is shutting down."));
        }
        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.Unwrap();
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

        Dispatcher dispatcher = DispatcherHelper.UIDispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return true;
        }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            dispatcher.Invoke(DispatcherPriority.Normal, action);
            return true;
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }
        catch (OperationCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
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


    private void ProgressHubPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e?.PropertyName != nameof(OperationProgressHubViewModel.IsStartupProgressActive))
        {
            return;
        }

        RaisePropertyChanged(nameof(IsLibraryOperationInProgress));
        ProgressHub.UpdateLr2SongDbSyncStatusSuppression(IsStartupProgressBlockingLr2SongDbSyncStatus());
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

    internal void SaveSettingsForShutdown()
    {
        saveSettings();
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private void RefreshPlayHistoryDisplayTargetSetsFromSettings(bool queueRefreshWhenSelectionChanges)
    {
        PlayHistory.ReplaceDisplayTargetSetsFromSettings(
            playHistoryDisplaySettingsStore.DisplayTargetSetsJson,
            PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot(),
            queueRefreshWhenSelectionChanges);
    }

    private void RaiseUiInteractionOnUiThread(EventHandler handler, string interactionName)
    {
        if (handler == null)
        {
            return;
        }

        void RaiseInteraction()
        {
            handler(this, EventArgs.Empty);
        }

        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            RaiseInteraction();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            LogUiInteractionSkippedOnShutdown(interactionName);
            return;
        }

        try
        {
            dispatcher.Invoke(DispatcherPriority.Normal, (Action)RaiseInteraction);
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            LogUiInteractionSkippedOnShutdown(interactionName);
        }
    }

    private void RaiseSettingDialogOpenRequested()
    {
        if (settingDialog == null)
        {
            return;
        }

        RaiseUiInteractionOnUiThread(
            (_, _) => settingDialog.RequestOpen(),
            nameof(SettingDialogViewModel.OpenRequested));
    }

    private void RaiseInitialSetupLanguageDialogRequested()
    {
        RaiseUiInteractionOnUiThread(InitialSetupLanguageDialogRequested, nameof(InitialSetupLanguageDialogRequested));
    }

    private void RaiseInitializationSucceeded()
    {
        RaiseUiInteractionOnUiThread(InitializationSucceeded, nameof(InitializationSucceeded));
    }

    private static void LogUiInteractionSkippedOnShutdown(string interactionName)
    {
        NLogWrapper.FileLogger?.Warn("ui_interaction skipped reason=dispatcher_shutdown name=" + (interactionName ?? string.Empty));
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
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);
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
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            _semaphore.Release();
        }
        if (scheduleDeferredExternalSync)
        {
            PlaylistWorkspace.QueueExternalPlaylistSync(
                "ReloadTables",
                fromReloadTables: true,
                publishReferenceReceipt: true,
                operationToken: operationToken);
        }
        SkipUnrequestedStartupProgressPhases(
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
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView);
            InvalidatePlayHistoryReadCache("score_reload");
            LogInitStage("score_reload_task_start", "ReloadScoresOnly");
            await Task.Run(delegate
            {
                LogInitStage("score_reload_call", "ReloadScoresOnly");
                files.InitializeScoresOnly(null);
            }).LoggingAndPropagate("ReloadScoresOnly");
            PublishLatestLr2PlayHistorySchemaCheckResultFromLibrary();
            LogInitStage("score_reload_done", "ReloadScoresOnly");
            RefreshLibraryMainViewForCurrentFilter();
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "score_only_reload");
            SkipUnrequestedStartupProgressPhases(
                "ReloadScoresOnly:scheduled",
                operationToken,
                StartupProgressPhase.ScoreHydrationDone,
                StartupProgressPhase.RankingRefreshDone);
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            LogInitStage("ui_suppress_end_called", "ReloadScoresOnly");
            _semaphore.Release();
            MarkStartupProgressFailureCleanupComplete(operationToken);
        }
    }

    internal void InvalidatePlayHistoryReadCache(string reason)
    {
        playHistoryWorkflowOwner.Deactivate(clearViewActivity: false);
        playHistoryWorkflowOwner.InvalidateReadCache();
        LogPlayHistoryEvent("play_history_read_cache_invalidated", "reason=" + (reason ?? string.Empty));
    }

    internal async Task ReloadFileDiffAsync()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadFileDiff");
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
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
            PlaylistWorkspace.QueuePlaylistReferenceApply("ReloadFileDiff", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "ReloadFileDiff");
            SkipUnrequestedStartupProgressPhases(
                "ReloadFileDiff:scheduled",
                operationToken,
                StartupProgressPhase.PlaylistReferenceApplied,
                StartupProgressPhase.PlaylistEntriesHydrationDone);
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            LogInitStage("ui_suppress_end_called", "ReloadFileDiff");
            _semaphore.Release();
            MarkStartupProgressFailureCleanupComplete(operationToken);
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
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.FullReinitialize);
        try
        {
            InvalidatePlayHistoryReadCache("full_reinitialize");
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
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            LogInitStage("ui_suppress_end_called", "FullReinitialize");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            PlaylistWorkspace.QueuePlaylistReferenceApply("FullReinitialize", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "FullReinitialize");
        }
        SkipUnrequestedStartupProgressPhases(
            "FullReinitialize:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
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
                System.Windows.Application.Current?.Shutdown();
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

        if (settingDialog.SyncRootCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(startupCustomFolderSettings))
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

    internal void MarkLibraryInitializationFailed()
    {
        if (initializationCompleted)
        {
            initializationCompleted = false;
            RaisePropertyChanged(() => IsInitializationCompleted);
        }
        if (hasActiveLibraryProfile)
        {
            hasActiveLibraryProfile = false;
            RaisePropertyChanged(() => HasActiveLibraryProfile);
        }
    }

    internal async Task<bool> InitializeAsync()
    {
        await _semaphore.WaitAsync();
        SetStartupUiInteractionBlocked(true);
        LogInitStage("start", "Initialize");
        initializationCompleted = false;
        RaisePropertyChanged(() => IsInitializationCompleted);
        if (hasActiveLibraryProfile)
        {
            hasActiveLibraryProfile = false;
            RaisePropertyChanged(() => HasActiveLibraryProfile);
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
            SetStartupUiInteractionBlocked(false);
            RaiseSettingDialogOpenRequested();
            return false;
        }
        if (!settingDialog.CheckValidation(out string startupValidationErrorMessage))
        {
            NLogWrapper.FileLogger?.Warn("startup_setting_validation_failed " + (startupValidationErrorMessage ?? string.Empty).Replace(Environment.NewLine, " | "));
            if (firstStartupProvider())
            {
                _semaphore.Release();
                SetStartupUiInteractionBlocked(false);
                RaiseInitialSetupLanguageDialogRequested();
                return false;
            }
            else
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_settings_check, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Startup settings validation notification");
            }
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            RaiseSettingDialogOpenRequested();
            return false;
        }
        try
        {
            if (startupSettings.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync(startupSettings))
            {
                _semaphore.Release();
                SetStartupUiInteractionBlocked(false);
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
            SetStartupUiInteractionBlocked(false);
            RaiseSettingDialogOpenRequested();
            return false;
        }
        long operationToken;
        try
        {
            InvalidatePlayHistoryReadCache("initialize");
            LibraryProfile libraryProfile = CreateLibraryProfileForStartup(startupSettings);
            await packageInstallLibraryGate.WaitAsync();
            try
            {
                files = applicationComposition.CreateBmsLibrary(libraryProfile);
                ShellShutdownWorkflow.AttachLibrary(files);
                PackageInstallWorkflow.AttachLibrary(files);
                MaintenanceRescanWorkflow.AttachLibrary(files);
                FolderAutoRenameWorkflow.AttachLibrary(files);
            }
            finally
            {
                packageInstallLibraryGate.Release();
            }
            regularChartListOwner.AttachNormalLibraryRefreshSource(files);
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
                PlaybackPanel.ReplacePlayer(configuredBmsPlayer);
            }
            operationToken = StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup library construction failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            string text3 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text3 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            RaiseSettingDialogOpenRequested();
            return false;
        }
        LoadColumnSetting();
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSLibrary.RegisterHandler(() => files.OwnedChartCollectionVersion, delegate
        {
            PlaylistWorkspace.InvalidatePlaylistLibraryIndexSnapshot(
                "owned_collection_changed",
                startupReadyOperableReached,
                treeViewFilterTypeSelected);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgress, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressScannerLabel, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressTotalCount, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressProcessedCount, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressCurrentPath, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryDatabaseLoadCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryDatabaseLoad(files.LibraryDatabaseLoadCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileEnumerationCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryFileEnumeration(files.LibraryFileEnumerationCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileDiffCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryFileDiff(files.LibraryFileDiffCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationRequestedVersion, delegate
        {
            TrackStartupProgressScoreHydrationRequested(files.ScoreHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressScoreHydration(files.ScoreHydrationCompletedVersion);
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
            TrackStartupProgressRankingRefreshRequested(files.RankingRefreshRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshCompletedVersion, delegate
        {
            TryCompleteStartupProgressRankingRefresh(files.RankingRefreshCompletedVersion);
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
            TrackStartupProgressMaintenanceRequested(files.MaintenanceHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressMaintenance(files.MaintenanceHydrationCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredRequestedVersion, delegate
        {
            TrackStartupProgressInstallableMaintenanceRequested(files.InstallableMaintenanceDeferredRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredCompletedVersion, delegate
        {
            TryCompleteStartupProgressInstallableMaintenance(files.InstallableMaintenanceDeferredCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillRequestedVersion, delegate
        {
            TrackStartupProgressChartDigestBackfillRequested(files.ChartDigestBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartDigestBackfill(files.ChartDigestBackfillCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillTotalCount, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillProcessedCount, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCurrentPath, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillRequestedVersion, delegate
        {
            TrackStartupProgressChartInfoBackfillRequested(files.ChartInfoBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartInfoBackfill(files.ChartInfoBackfillCompletedVersion);
            if ((files?.ChartInfoBackfillDigestBackfilledCount ?? 0) > 0)
            {
                InvalidateNormalLibraryIdentitySortKeys(NormalLibraryChartInfoDigestBackfilledReason);
            }
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillTotalCount, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillProcessedCount, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCurrentPath, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationRequestedVersion, delegate
        {
            TrackStartupProgressChartInfoHydrationRequested(files.ChartInfoHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartInfoHydration(files.ChartInfoHydrationCompletedVersion);
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationTotalCount, delegate
        {
            UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationAppliedCount, delegate
        {
            UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncRequestedVersion, delegate
        {
            TrackStartupProgressLr2SongDbSyncRequested(files.Lr2SongDbSyncRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncCompletedVersion, delegate
        {
            TryCompleteStartupProgressLr2SongDbSync(files.Lr2SongDbSyncCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncFailedVersion, delegate
        {
            TryFailStartupProgressLr2SongDbSync(files.Lr2SongDbSyncFailedVersion, files.Lr2SongDbSyncFailureMessage);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStatusVersion, delegate
        {
            UpdateLr2SongDbSyncRuntimeStatus(files.GetLr2SongDbSyncStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncTotalCount, delegate
        {
            UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncProcessedCount, delegate
        {
            UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStage, delegate
        {
            UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStageProcessedCount, delegate
        {
            UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.Lr2SongDbSyncStageTotalCount, delegate
        {
            UpdateStartupProgressLr2SongDbSyncStatus(files.Lr2SongDbSyncTotalCount, files.Lr2SongDbSyncProcessedCount, files.Lr2SongDbSyncStage, files.Lr2SongDbSyncStageProcessedCount, files.Lr2SongDbSyncStageTotalCount);
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
        RaiseInitializationSucceeded();
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
        void taskAdd2()
        {
            PlaylistWorkspace.LoadExternalTableCollection(startupSettings.TableListURL);
        }
        Thread.Yield();
        startupReadyInstallStopwatch = Stopwatch.StartNew();
        startupReadyOperableStopwatch = Stopwatch.StartNew();
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
                files.InitializeStartup([taskAdd1, taskAdd2], semaphore);
            }).Logging("Initialize");
            PublishLatestLr2PlayHistorySchemaCheckResultFromLibrary();
            RepairRootCustomFolderOutputSearchRootsAfterStartupPlaylistLoad(startupCustomFolderSettings);
            LogInitStage("files_initialize_done", "Initialize");
            TryLogStartupReadyData();
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Startup library initialization failure notification");
            Logger currentClassLogger = NLogWrapper.GetLogger(typeof(MainWindowViewModel));
            string text4 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text4 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            RaiseSettingDialogOpenRequested();
            return false;
        }
        finally
        {
            EndUiUpdateSuppression();
            LogInitStage("ui_suppress_end_called", "Initialize");
            MarkStartupProgressFailureCleanupComplete(operationToken);
        }
        if (firstStartupProvider())
        {
            completeFirstStartup();
            initialSetupCompletionMessagePending = true;
        }
        initializationCompleted = true;
        hasActiveLibraryProfile = true;
        RaisePropertyChanged(() => IsInitializationCompleted);
        RaisePropertyChanged(() => HasActiveLibraryProfile);
        PlaylistWorkspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");
        _semaphore.Release();
        LogInitStage("deferred_playlist_ref_waiting_for_playlist_entries_hydration", "Initialize");
        if (!startupSettings.SkipInitPlaylistLoad)
        {
            PlaylistWorkspace.QueueExternalPlaylistSync(
                "Initialize",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: operationToken);
        }
        SkipUnrequestedStartupProgressPhases(
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
        TrackStartupProgressPlaylistEntriesHydrationRequested(e?.Version ?? 0);
    }

    private void PlaylistWorkspacePlaylistEntriesHydrationCompleted(
        object sender,
        PlaylistEntriesHydrationVersionChangedEventArgs e)
    {
        int version = e?.Version ?? 0;
        TryCompleteStartupProgressPlaylistEntriesHydration(version);
        if (ShouldCompletePlaylistReferenceFromHydration(version))
        {
            TrackStartupProgressPlaylistReferenceRequest("PlaylistEntriesHydration", version);
            TryCompleteStartupProgressPlaylistReference(version);
        }
        if (PlayHistory.SelectedDisplayTarget.UsesProjection)
        {
            playHistoryWorkflowOwner.QueueDisplayTargetRefresh(
                PlayHistory.SelectedDisplayTarget?.Identity ?? string.Empty);
        }
    }

    private void PlaylistWorkspacePlaylistExternalSyncQueued(
        object sender,
        PlaylistExternalSyncRequestEventArgs request)
    {
        if (request == null || !IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        TrackStartupProgressExternalSyncRequest(request.Reason, request.Version);
        if (request.PublishesReferenceReceipt)
        {
            TrackStartupProgressPlaylistReferenceRequest(
                "DeferredExternalSync:" + request.Reason,
                request.Version);
        }
    }

    private void PlaylistWorkspacePlaylistExternalSyncCompleted(
        object sender,
        PlaylistExternalSyncCompletionEventArgs completion)
    {
        if (completion == null || !IsStartupProgressOperationTokenCurrent(completion.OperationToken))
        {
            return;
        }
        if (completion.WasSkipped && completion.PublishesReferenceReceipt)
        {
            TryCompleteStartupProgressPlaylistReference(completion.Version);
        }
        TryCompleteStartupProgressExternalSync(completion.Version);
    }

    private void PlaylistWorkspacePlaylistExternalSyncReferenceApplied(
        object sender,
        PlaylistExternalSyncReferenceAppliedEventArgs request)
    {
        if (request == null || !IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        TryCompleteStartupProgressPlaylistReference(request.Version);
    }

    private void PlaylistWorkspacePlaylistReferenceApplyQueued(
        object sender,
        PlaylistReferenceApplyQueuedEventArgs request)
    {
        if (request == null || !IsStartupProgressOperationTokenCurrent(request.OperationToken))
        {
            return;
        }
        TrackStartupProgressPlaylistReferenceRequest(request.Reason, request.Version);
        TrackStartupProgressPlaylistEntriesHydrationDirectRequest(
            request.Version,
            "playlist_ref_deferred:" + request.Reason);
    }

    private void PlaylistWorkspacePlaylistReferenceApplyCompleted(
        object sender,
        PlaylistReferenceApplyCompletedEventArgs completion)
    {
        if (completion == null || !IsStartupProgressOperationTokenCurrent(completion.OperationToken))
        {
            return;
        }
        TryCompleteStartupProgressPlaylistEntriesHydration(completion.Version);
        TryCompleteStartupProgressPlaylistReference(completion.Version);
    }

    private void PlaylistWorkspacePlaylistReferenceApplyPresentationRequested(
        object sender,
        PlaylistReferenceApplyPresentationRequestedEventArgs request)
    {
        if (request == null || !IsStartupProgressOperationTokenCurrent(request.OperationToken))
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
            && expectedSortTarget.Value != (CurrentMainViewOperationSection == MainViewOperationSection.PlayHistory
                ? MainChartListSortTarget.PlayHistory
                : MainChartListSortTarget.Regular))
        {
            return;
        }
        regularChartListOwner.PrepareForMainViewRefresh();
        var viewBuildStopwatch = Stopwatch.StartNew();
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        MainViewUpdateMode requestedMode = mode;
        MainViewOperationSection previousOperationSection = CurrentMainViewOperationSection;
        if (mode == MainViewUpdateMode.TreeViewFilterNotChanged)
        {
            GetTreeViewFilterSelection(out mode, out parameter);
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
            UpdateChartKeywordSearchContext();
        }
        if (previousOperationSection != CurrentMainViewOperationSection)
        {
            RaisePropertyChanged(() => CurrentMainViewOperationSection);
            RaisePropertyChanged(() => CurrentMainViewChartOperationSourceScope);
            SyncMainChartListSortPresentation();
        }
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(mode, requestedMode, treeViewFilterTypeSelected, files != null);
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
            UpdateBmsFilesViewBindingMode(route.IsPlaylistTreeActive);
            PlaylistWorkspace.RequestDetailRefresh(
                route.Mode,
                route.RequestedMode,
                treeViewFilterTypeSelected,
                ShouldUsePlaylistBuildCoalescingWindow(route.Mode, route.RequestedMode),
                CapturePlaylistOpenReadinessSnapshot());
            return;
        }
        RegularChartListEntryResult regularResult = regularChartListOwner.ApplyMainLibraryView(
            route,
            files,
            parameter,
            treeViewFilterParameterSelected,
            PlaylistWorkspace.IsPlaylistSummaryMode,
            viewBuildStopwatch,
            filters);
        if (regularResult.WasCommitted && regularResult.SortWasReset)
        {
            if (CurrentMainViewOperationSection != MainViewOperationSection.PlayHistory)
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
            if (progress.Lr2SchemaCheckResult != null)
            {
                ApplyLr2PlayHistorySchemaCheckResultFromRead(progress.Lr2SchemaCheckResult);
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

    private void ApplyLr2PlayHistorySchemaCheckResultFromRead(Lr2PlayHistorySchemaCheckResult result)
    {
        if (result == null || !ApplicationSettings.OperationModeLR2DB)
        {
            return;
        }
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)(() => ApplyLr2PlayHistorySchemaCheckResultFromRead(result)));
            return;
        }
        if (settingDialog?.OperationModeLR2DB == true
            && string.Equals(ResolveMainViewLr2PlayHistoryScoreDbPath() ?? string.Empty, result.ScoreDbPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            settingDialog.ApplyLr2PlayHistorySchemaCheckResult(result);
        }
    }

    private void PublishLatestLr2PlayHistorySchemaCheckResultFromLibrary()
    {
        var stopwatch = Stopwatch.StartNew();
        Lr2PlayHistorySchemaCheckResult result = files?.GetLr2PlayHistorySchemaCheckResultForDiagnostics();
        if (result == null)
        {
            ResetLr2PlayHistorySchemaStatusFromLibrary();
            LogSettingsPerformance("settings_schema_status_publish", stopwatch, "result=null action=reset");
            return;
        }
        ApplyLr2PlayHistorySchemaCheckResultFromRead(result);
        LogSettingsPerformance(
            "settings_schema_status_publish",
            stopwatch,
            "result=published status=" + result.Status
            + " path=" + (result.ScoreDbPath ?? string.Empty));
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

    private void ResetLr2PlayHistorySchemaStatusFromLibrary()
    {
        if (settingDialog == null)
        {
            return;
        }
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)ResetLr2PlayHistorySchemaStatusFromLibrary);
            return;
        }
        settingDialog.ClearLr2PlayHistorySchemaStatus();
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
            treeViewFilterTypeSelected = mode;
            treeViewFilterParameterSelected = IsPlaylistViewMode(mode) ? null : parameter;
        });
        playHistoryWorkflowOwner.ClearSummaryFilters();
    }

    private string ResolveMainViewLr2PlayHistoryScoreDbPath()
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return null;
        }
        if (lr2config == null
            && !string.IsNullOrWhiteSpace(ApplicationSettings.LR2ConfigXmlPath)
            && File.Exists(ApplicationSettings.LR2ConfigXmlPath))
        {
            lr2config = new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
        }
        return Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(ApplicationSettings.LR2RootPath, () => lr2config?.GetPlayerId());
    }

    private bool ShouldUseBeatorajaPlayHistoryProvider()
    {
        string scoreDbPath = ResolveMainViewBeatorajaPlayHistoryScoreDbPath();
        return ApplicationSettings.UseBeatorajaScoreDb
            && files?.GetActiveScoreSourceForDiagnostics() == ActiveScoreSource.Beatoraja
            && !string.IsNullOrWhiteSpace(scoreDbPath)
            && File.Exists(scoreDbPath);
    }

    private string ResolveMainViewBeatorajaPlayHistoryScoreDbPath()
    {
        if (BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath))
        {
            return BeatorajaConfigService.GetScoreDbPath(ApplicationSettings.BeatorajaRootPath, ApplicationSettings.BeatorajaPlayerId);
        }
        return ApplicationSettings.BeatorajaScoreDbPath;
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
        bool useBeatorajaProvider = ShouldUseBeatorajaPlayHistoryProvider();
        return useBeatorajaProvider
            ? PlayHistoryReadSourceContext.Beatoraja(
                ResolveMainViewBeatorajaPlayHistoryScoreDbPath(),
                ResolveBeatorajaPlayHistoryScoreContext())
            : PlayHistoryReadSourceContext.Lr2(
                ResolveMainViewLr2PlayHistoryScoreDbPath(),
                ApplicationSettings.OperationModeLR2DB);
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
        long currentRequestId;
        lock (playHistoryViewRequestLock)
        {
            currentRequestId = playHistoryWorkflowOwner.CurrentRequestId;
        }
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

    public void LoadColumnSetting()
    {
        MainChartListColumnSelection selection = MainChartList.LoadColumnSetting(
            MainViewUpdateMode.TreeViewFilterNotChanged,
            treeViewFilterTypeSelected,
            isInit: true);
        PlaylistWorkspace.CommitMainTableColumnSetting(MainChartList, selection);
    }

    private void MaintenanceRescanWorkflowProgressChanged(MaintenanceWorkflowProgress progress)
    {
        ProgressHub.UpdateMaintenanceRescanProgress(progress);
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

    private void FolderAutoRenameWorkflowProgressChanged(FolderAutoRenameProgressSnapshot progress)
    {
        ProgressHub.UpdateFolderAutoRenameProgress(progress);
    }

    private void FolderAutoRenameWorkflowCompletionPublished(FolderAutoRenameCompletionReceipt receipt)
    {
        if (receipt?.RefreshRequired != true)
        {
            return;
        }
        regularChartListOwner.ApplyLatestNormalLibraryRefreshNotification("library_charts_changed");
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
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            refresh();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(refresh);
        }
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

    private IReadOnlyList<ChartPackage> ExecutePackageInstallMutation(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        packageInstallLibraryGate.Wait();
        try
        {
            if (library == null || !ReferenceEquals(files, library))
            {
                throw new InvalidOperationException("The package-install library generation is no longer active.");
            }
            string[] normalizedInstallPaths = [.. (installPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
            if (normalizedInstallPaths.Length == 0 || token.IsCancellationRequested)
            {
                return [];
            }
            List<ChartPackage> installedPackages = [];
            RunChartPackageMutation(
                () => installedPackages.AddRange(library.InstallChartPackagesAuto(
                    normalizedInstallPaths,
                    token,
                    onEachPathProcessed,
                    onEachArchiveExtractStarted)),
                refreshMask: UiRefreshChannel.LibraryMainView
                    | UiRefreshChannel.InstallTree
                    | UiRefreshChannel.LibraryFolderTree
                    | UiRefreshChannel.DuplicateTree);
            return installedPackages;
        }
        finally
        {
            packageInstallLibraryGate.Release();
        }
    }

    private void DispatchPackageInstallUi(Action action)
    {
        InvokeMainChartListPresentationAction(action);
    }

    private void PackageInstallWorkflowStatusChanged(DropInstallQueueStatusSnapshot snapshot)
    {
        ProgressHub.UpdateDropInstallQueueStatus(snapshot);
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

    private void UpdatePlaylistSyncProgressStatus(PlaylistSyncProgressSnapshot snapshot)
    {
        long uiVersion = Interlocked.Increment(ref playlistSyncProgressUiVersion);
        Action reflect = delegate
        {
            if (uiVersion != Interlocked.Read(ref playlistSyncProgressUiVersion))
            {
                return;
            }
            ProgressHub.UpdatePlaylistSyncProgress(snapshot);
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }

    /// <summary>
    /// 起動・リロード進捗の状態を開始し、基準版数を初期化します。
    /// </summary>
    /// <param name="operationKind">進捗対象の operation 種別。</param>
    private long StartStartupProgressOperation(StartupProgressOperationKind operationKind)
    {
        ResetStartupBackgroundTaskSchedulerState(operationKind);
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            startupInitializationCompleteStopwatch = Stopwatch.StartNew();
            startupInitializationCompleteLogged = false;
        }
        var state = new StartupProgressState
        {
            OperationKind = operationKind,
            OperationToken = Interlocked.Increment(ref startupProgressOperationTokenSeed),
            IsActive = true,
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted,
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(operationKind),
            ScoreHydrationBaselineCompletedVersion = files?.ScoreHydrationCompletedVersion ?? 0,
            ScoreHydrationRequestedBaselineVersion = files?.ScoreHydrationRequestedVersion ?? 0,
            RankingRefreshBaselineCompletedVersion = files?.RankingRefreshCompletedVersion ?? 0,
            RankingRefreshRequestedBaselineVersion = files?.RankingRefreshRequestedVersion ?? 0,
            MaintenanceRequestedBaselineVersion = files?.MaintenanceHydrationRequestedVersion ?? 0,
            InstallableMaintenanceRequestedBaselineVersion = files?.InstallableMaintenanceDeferredRequestedVersion ?? 0,
            ChartDigestBackfillBaselineCompletedVersion = files?.ChartDigestBackfillCompletedVersion ?? 0,
            ChartInfoBackfillBaselineCompletedVersion = files?.ChartInfoBackfillCompletedVersion ?? 0,
            ChartInfoHydrationBaselineCompletedVersion = files?.ChartInfoHydrationCompletedVersion ?? 0,
            Lr2SongDbSyncBaselineCompletedVersion = files?.Lr2SongDbSyncCompletedVersion ?? 0,
            PlaylistEntriesHydrationBaselineCompletedVersion = tables?.PlaylistEntriesHydrationCompletedVersion ?? 0,
            LibraryDatabaseLoadBaselineCompletedVersion = files?.LibraryDatabaseLoadCompletedVersion ?? 0,
            LibraryFileEnumerationBaselineCompletedVersion = files?.LibraryFileEnumerationCompletedVersion ?? 0,
            LibraryFileDiffBaselineCompletedVersion = files?.LibraryFileDiffCompletedVersion ?? 0
        };
        lock (startupProgressLock)
        {
            startupProgressState = state;
        }
        RaisePropertyChanged(() => IsLibraryOperationInProgress);
        RecomputeStartupProgressPresentation();
        return state.OperationToken;
    }

    /// <summary>
    /// 起動・リロード進捗を失敗表示へ切り替えます。
    /// </summary>
    /// <param name="subLabel">失敗時に表示する補足文言。</param>
    private void FailStartupProgressOperation(string subLabel)
    {
        SetStartupUiInteractionBlocked(false);
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.IsFailed = true;
            startupProgressState.FailureSubLabel = subLabel ?? string.Empty;
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void MarkStartupProgressFailureCleanupComplete(long operationToken)
    {
        bool retryable = false;
        lock (startupProgressLock)
        {
            if (startupProgressState.IsActive
                && startupProgressState.OperationToken == operationToken
                && (startupProgressState.OperationKind == StartupProgressOperationKind.ScoreOnly
                    || startupProgressState.OperationKind == StartupProgressOperationKind.ReloadFileDiff
                    || startupProgressState.OperationKind == StartupProgressOperationKind.Startup)
                && startupProgressState.IsFailed)
            {
                startupProgressState.IsRetryableFailure = true;
                retryable = true;
            }
        }

        if (retryable)
        {
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
        }
    }

    /// <summary>
    /// 起動・リロード進捗のフェーズを完了済みにします。
    /// </summary>
    /// <param name="phase">完了したフェーズ。</param>
    private void MarkStartupProgressPhaseCompleted(StartupProgressPhase phase)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.CompletedPhases |= phase;
            if (phase == StartupProgressPhase.RankingRefreshDone
                || phase == StartupProgressPhase.MaintenanceDeferredDone
                || phase == StartupProgressPhase.InstallableMaintenanceDeferredDone)
            {
                startupProgressState.LastCompletedAtUtc = DateTime.UtcNow;
            }
        }
        RecomputeStartupProgressPresentation();
        if (phase != StartupProgressPhase.StartupBackgroundTasksDone)
        {
            TryCompleteStartupBackgroundTasksPhaseIfIdle();
        }
    }

    private void ResetStartupBackgroundTaskSchedulerState(StartupProgressOperationKind operationKind)
    {
        startupBackgroundTaskScheduler.Reset(
            operationKind != StartupProgressOperationKind.Startup && startupReadyOperableReached);
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask = UiRefreshChannel.None;
        }
        lock (startupInitializationCompletionLock)
        {
            startupInitializationCompleteStopwatch = null;
            startupInitializationCompleteLogged = false;
            startupInitializationCompleteRetryQueued = false;
        }
    }

    private void SkipStartupProgressPhaseIfExpected(StartupProgressPhase phase, string reason)
    {
        bool skipped = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & phase) == 0 || (startupProgressState.CompletedPhases & phase) != 0)
            {
                return;
            }
            operationKind = startupProgressState.OperationKind;
            startupProgressState.CompletedPhases |= phase;
            startupProgressState.SkippedPhases |= phase;
            skipped = true;
        }
        if (skipped)
        {
            LogUiSuppression("startup_progress_phase_skipped operation=" + operationKind + " phase=" + phase + " reason=" + (reason ?? string.Empty));
            RecomputeStartupProgressPresentation();
            if (phase != StartupProgressPhase.StartupBackgroundTasksDone)
            {
                TryCompleteStartupBackgroundTasksPhaseIfIdle();
            }
        }
    }

    private void SkipUnrequestedStartupProgressPhases(string reason, params StartupProgressPhase[] phases)
    {
        SkipUnrequestedStartupProgressPhases(reason, 0L, phases);
    }

    private void SkipUnrequestedStartupProgressPhases(string reason, long operationToken, params StartupProgressPhase[] phases)
    {
        if (phases == null || phases.Length == 0)
        {
            return;
        }
        if (operationToken != 0L && !IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        foreach (StartupProgressPhase phase in phases)
        {
            bool shouldSkip;
            lock (startupProgressLock)
            {
                shouldSkip = startupProgressState.IsActive
                    && (startupProgressState.ExpectedPhases & phase) != 0
                    && (startupProgressState.RequestedPhases & phase) == 0
                    && (startupProgressState.CompletedPhases & phase) == 0;
            }
            if (shouldSkip)
            {
                SkipStartupProgressPhaseIfExpected(phase, reason);
            }
        }
    }

    private bool TryTrackStartupProgressPhaseRequest(StartupProgressPhase phase, int version, string reason, Action<StartupProgressState> updateRequiredVersion)
    {
        bool ignored = false;
        bool requestAfterSkip = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return false;
            }
            operationKind = startupProgressState.OperationKind;
            if ((startupProgressState.ExpectedPhases & phase) == 0)
            {
                ignored = true;
            }
            else
            {
                startupProgressState.RequestedPhases |= phase;
                updateRequiredVersion?.Invoke(startupProgressState);
                requestAfterSkip = (startupProgressState.SkippedPhases & phase) != 0;
                if ((startupProgressState.CompletedPhases & phase) == 0)
                {
                    startupProgressState.CompletionHideScheduled = false;
                }
            }
        }
        if (ignored)
        {
            LogUiSuppression("startup_progress_request_ignored operation=" + operationKind + " phase=" + phase + " reason=not_expected requestReason=" + (reason ?? string.Empty) + " version=" + version);
            return false;
        }
        if (requestAfterSkip)
        {
            LogUiSuppression("startup_progress_request_after_skip operation=" + operationKind + " phase=" + phase + " requestReason=" + (reason ?? string.Empty) + " version=" + version);
        }
        RecomputeStartupProgressPresentation();
        return true;
    }

    private static bool RequiresStartupProgressRequestBeforeCompletion(StartupProgressPhase phase)
    {
        return phase != StartupProgressPhase.CoreInitializeStarted
            && phase != StartupProgressPhase.StartupReadyData
            && phase != StartupProgressPhase.StartupReadyUi
            && phase != StartupProgressPhase.StartupReadyOperable
            && phase != StartupProgressPhase.LibraryDatabaseLoadDone
            && phase != StartupProgressPhase.LibraryFileEnumerationDone
            && phase != StartupProgressPhase.LibraryFileDiffDone
            && phase != StartupProgressPhase.StartupBackgroundTasksDone;
    }

    private static bool CanCompleteStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0
            && (!RequiresStartupProgressRequestBeforeCompletion(phase) || (state.RequestedPhases & phase) != 0);
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    private void TrackStartupProgressPlaylistReferenceRequest(string reason, int version)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && ShouldTrackStartupProgressPlaylistReference(reason, startupProgressState.OperationKind);
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistReferenceApplied,
            version,
            reason,
            state => state.RequiredPlaylistReferenceVersion = Math.Max(state.RequiredPlaylistReferenceVersion, version));
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    private void TryCompleteStartupProgressPlaylistReference(int version)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistReferenceApplied))
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredPlaylistReferenceVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistReferenceApplied);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    private void TrackStartupProgressExternalSyncRequest(string reason, int version)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && ShouldTrackStartupProgressExternalSync(reason, startupProgressState.OperationKind);
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ExternalPlaylistSyncDone,
            version,
            reason,
            state => state.RequiredExternalSyncVersion = Math.Max(state.RequiredExternalSyncVersion, version));
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    private void TryCompleteStartupProgressExternalSync(int version)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ExternalPlaylistSyncDone))
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredExternalSyncVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ExternalPlaylistSyncDone);
        }
    }

    /// <summary>
    /// maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    private void TrackStartupProgressMaintenanceRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.MaintenanceRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.MaintenanceDeferredDone,
            requestedVersion,
            "maintenance_deferred",
            state => state.RequiredMaintenanceCompletedVersion = Math.Max(state.RequiredMaintenanceCompletedVersion, requestedVersion));
    }

    /// <summary>
    /// installable maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    private void TrackStartupProgressInstallableMaintenanceRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.InstallableMaintenanceRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.InstallableMaintenanceDeferredDone,
            requestedVersion,
            "installable_maintenance_deferred",
            state => state.RequiredInstallableMaintenanceCompletedVersion = Math.Max(state.RequiredInstallableMaintenanceCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressScoreHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ScoreHydrationRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ScoreHydrationDone,
            requestedVersion,
            "score_hydration",
            state => state.RequiredScoreHydrationCompletedVersion = Math.Max(state.RequiredScoreHydrationCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressRankingRefreshRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.RankingRefreshRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.RankingRefreshDone,
            requestedVersion,
            "ranking_refresh",
            state => state.RequiredRankingRefreshCompletedVersion = Math.Max(state.RequiredRankingRefreshCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartDigestBackfillRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartDigestBackfillBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartDigestBackfillDone,
            requestedVersion,
            "chart_digest_backfill",
            state => state.RequiredChartDigestBackfillCompletedVersion = Math.Max(state.RequiredChartDigestBackfillCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartInfoBackfillRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartInfoBackfillBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            requestedVersion,
            "chart_info_backfill",
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartInfoHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        int expectedBackfillVersion;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartInfoHydrationBaselineCompletedVersion;
            expectedBackfillVersion = (files?.ChartInfoBackfillRequestedVersion ?? 0) + 1;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoHydrationDone,
            requestedVersion,
            "chart_info_hydration",
            state => state.RequiredChartInfoHydrationCompletedVersion = Math.Max(state.RequiredChartInfoHydrationCompletedVersion, requestedVersion));
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            expectedBackfillVersion,
            "chart_info_backfill_after_hydration",
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, expectedBackfillVersion));
    }

    private void TrackStartupProgressLr2SongDbSyncRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.Lr2SongDbSyncBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.Lr2SongDbSyncDone,
            requestedVersion,
            "lr2_song_db_sync",
            state => state.RequiredLr2SongDbSyncCompletedVersion = Math.Max(state.RequiredLr2SongDbSyncCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressPlaylistEntriesHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.PlaylistEntriesHydrationBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        lock (startupProgressLock)
        {
            if (startupProgressState.IsActive)
            {
                startupProgressState.PlaylistReferenceFromHydrationRequested = true;
            }
        }
        TrackStartupProgressPlaylistEntriesHydrationDirectRequest(requestedVersion, "playlist_entries_hydration");
    }

    private bool ShouldCompletePlaylistReferenceFromHydration(int completedVersion)
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive
                && startupProgressState.PlaylistReferenceFromHydrationRequested
                && completedVersion >= startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion;
        }
    }

    private void TrackStartupProgressPlaylistEntriesHydrationDirectRequest(int requestedVersion, string reason)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            reason,
            state => state.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(state.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion));
    }

    private void UpdateStartupProgressChartDigestBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
            {
                return;
            }
            startupProgressState.ChartDigestBackfillTotalCount = totalCount;
            startupProgressState.ChartDigestBackfillProcessedCount = processedCount;
            startupProgressState.ChartDigestBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressLibraryInitializationStatus(BMSLibrary.LibraryInitializationProgressStage stage, string scannerLabel, int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.LibraryInitializationProgressStage = stage;
            startupProgressState.LibraryInitializationProgressScannerLabel = scannerLabel ?? string.Empty;
            startupProgressState.LibraryInitializationProgressTotalCount = Math.Max(0, totalCount);
            startupProgressState.LibraryInitializationProgressProcessedCount = Math.Max(0, processedCount);
            startupProgressState.LibraryInitializationProgressCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartInfoBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
            {
                return;
            }
            startupProgressState.ChartInfoBackfillTotalCount = totalCount;
            startupProgressState.ChartInfoBackfillProcessedCount = processedCount;
            startupProgressState.ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartInfoHydrationStatus(int totalCount, int appliedCount)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }
            startupProgressState.ChartInfoHydrationTotalCount = totalCount;
            startupProgressState.ChartInfoHydrationAppliedCount = appliedCount;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressLr2SongDbSyncStatus(
        int totalCount,
        int processedCount,
        string stage,
        int stageProcessedCount,
        int stageTotalCount)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.Lr2SongDbSyncDone))
            {
                return;
            }
            startupProgressState.Lr2SongDbSyncTotalCount = totalCount;
            startupProgressState.Lr2SongDbSyncProcessedCount = processedCount;
            startupProgressState.Lr2SongDbSyncStage = stage ?? string.Empty;
            startupProgressState.Lr2SongDbSyncStageProcessedCount = Math.Max(0, stageProcessedCount);
            startupProgressState.Lr2SongDbSyncStageTotalCount = Math.Max(0, stageTotalCount);
        }
        RecomputeStartupProgressPresentation();
    }

    private void TryCompleteStartupProgressLibraryDatabaseLoad(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryDatabaseLoadDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryDatabaseLoadBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryDatabaseLoadDone);
        }
    }

    private void TryCompleteStartupProgressLibraryFileEnumeration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileEnumerationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileEnumerationBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileEnumerationDone);
        }
    }

    private void TryCompleteStartupProgressLibraryFileDiff(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileDiffDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileDiffBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileDiffDone);
        }
    }

    private void TryCompleteStartupProgressChartDigestBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartDigestBackfillCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartDigestBackfillDone);
        }
    }

    private void TryCompleteStartupProgressChartInfoBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoBackfillCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoBackfillDone);
        }
    }

    private void TryCompleteStartupProgressChartInfoHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoHydrationCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoHydrationDone);
        }
    }

    private void TryCompleteStartupProgressLr2SongDbSync(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.Lr2SongDbSyncDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredLr2SongDbSyncCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.Lr2SongDbSyncDone);
        }
    }

    private void TryFailStartupProgressLr2SongDbSync(int failedVersion, string message)
    {
        bool shouldFail = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.Lr2SongDbSyncDone))
            {
                return;
            }
            shouldFail = failedVersion >= startupProgressState.RequiredLr2SongDbSyncCompletedVersion
                && failedVersion > startupProgressState.Lr2SongDbSyncBaselineCompletedVersion;
            if (shouldFail)
            {
                startupProgressState.IsFailed = true;
                startupProgressState.FailureSubLabel = string.IsNullOrWhiteSpace(message)
                    ? GetStartupProgressSubLabel(startupProgressState)
                    : message;
                startupProgressState.CompletionHideScheduled = false;
            }
        }
        if (shouldFail)
        {
            RecomputeStartupProgressPresentation();
        }
    }

    private void UpdateLr2SongDbSyncRuntimeStatus(Lr2SongDbSyncStatusSnapshot snapshot)
    {
        ProgressHub.UpdateLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.Now),
            IsStartupProgressBlockingLr2SongDbSyncStatus());
    }

    private bool IsStartupProgressBlockingLr2SongDbSyncStatus()
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive
                && !startupProgressState.IsFailed
                && CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.Lr2SongDbSyncDone);
        }
    }

    private void TryCompleteStartupProgressPlaylistEntriesHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistEntriesHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistEntriesHydrationDone);
        }
    }

    /// <summary>
    /// maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.MaintenanceDeferredDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredMaintenanceCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.MaintenanceDeferredDone);
        }
    }

    /// <summary>
    /// installable maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressInstallableMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.InstallableMaintenanceDeferredDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredInstallableMaintenanceCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.InstallableMaintenanceDeferredDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred score hydration 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressScoreHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ScoreHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredScoreHydrationCompletedVersion
                && completedVersion > startupProgressState.ScoreHydrationBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ScoreHydrationDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred ranking refresh 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressRankingRefresh(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.RankingRefreshDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredRankingRefreshCompletedVersion
                && completedVersion > startupProgressState.RankingRefreshBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.RankingRefreshDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗の表示を現在の内部状態から再計算します。
    /// </summary>
    private void RecomputeStartupProgressPresentation()
    {
        bool isActive;
        string label;
        string subLabel;
        double value;
        double maximum;
        bool shouldHideLater = false;
        long hideOperationToken = 0L;
        long reflectOperationToken = 0L;
        bool operationCompletedForLog = false;
        StartupProgressOperationKind operationKindForLog = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            StartupProgressState state = startupProgressState;
            isActive = state.IsActive;
            reflectOperationToken = state.OperationToken;
            operationKindForLog = state.OperationKind;
            if (!isActive)
            {
                label = string.Empty;
                subLabel = string.Empty;
                value = 0.0;
                maximum = 1.0;
            }
            else
            {
                maximum = Math.Max(1.0, CountExpectedStartupProgressPhases(state));
                value = CountCompletedExpectedStartupProgressPhases(state);
                if (TryGetStartupProgressStageValue(state, out double stageValue, out double stageMaximum))
                {
                    value = stageValue;
                    maximum = stageMaximum;
                }
                bool operableCompleted = (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
                bool operationCompleted = !state.IsFailed && AreExpectedStartupProgressPhasesCompleted(state);
                operationCompletedForLog = operationCompleted;
                if (state.IsFailed)
                {
                    label = GetStartupProgressFailedLabel(state.OperationKind);
                    subLabel = !string.IsNullOrWhiteSpace(state.FailureSubLabel) ? state.FailureSubLabel : GetStartupProgressSubLabel(state);
                }
                else if (operationCompleted)
                {
                    label = GetStartupProgressCompletedLabel(state.OperationKind);
                    subLabel = string.Empty;
                    if (!state.CompletionHideScheduled)
                    {
                        state.CompletionHideScheduled = true;
                        startupProgressState = state;
                        shouldHideLater = true;
                        hideOperationToken = state.OperationToken;
                    }
                }
                else if (operableCompleted)
                {
                    label = BeMusicSeeker.Properties.Resources.Statusbar_progress_operable_background;
                    subLabel = GetStartupProgressSubLabel(state);
                }
                else
                {
                    label = GetStartupProgressRunningLabel(state.OperationKind);
                    subLabel = GetStartupProgressSubLabel(state);
                }
            }
        }
        if (operationCompletedForLog && operationKindForLog == StartupProgressOperationKind.Startup)
        {
            TryLogStartupInitializationComplete();
        }
        Action reflect = delegate
        {
            lock (startupProgressLock)
            {
                if (isActive)
                {
                    if (!startupProgressState.IsActive || startupProgressState.OperationToken != reflectOperationToken)
                    {
                        return;
                    }
                }
                else if (startupProgressState.IsActive)
                {
                    return;
                }
            }
            ProgressHub.UpdateStartupProgress(isActive, label, subLabel, value, maximum);
            ProgressHub.UpdateLr2SongDbSyncStatusSuppression(IsStartupProgressBlockingLr2SongDbSyncStatus());
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
        if (shouldHideLater)
        {
            ScheduleStartupProgressHide(hideOperationToken);
        }
    }

    private void TryLogStartupInitializationComplete()
    {
        long elapsedMs;
        lock (startupInitializationCompletionLock)
        {
            if (startupInitializationCompleteLogged || startupInitializationCompleteStopwatch == null)
            {
                return;
            }
            if (!startupBackgroundTaskScheduler.IsStarted || !startupBackgroundTaskScheduler.IsIdle)
            {
                QueueStartupInitializationCompleteRetryUnsafe();
                return;
            }
            startupInitializationCompleteLogged = true;
            elapsedMs = startupInitializationCompleteStopwatch.ElapsedMilliseconds;
        }
        LogUiSuppression("startup_initialization_complete elapsedMs=" + elapsedMs);
        LogUiSuppression(startupBackgroundTaskScheduler.BuildSummaryLog(elapsedMs));
        if (!QueueDeferredStartupPresentationFlushAfterInitialization())
        {
            SchedulePostStartupBestEffortWarmups("startup_initialization_complete");
            ShowInitialSetupCompletionMessageIfPending();
        }
    }

    private bool QueueDeferredStartupPresentationFlushAfterInitialization()
    {
        UiRefreshChannel mask;
        lock (lockUiSuppression)
        {
            mask = deferredStartupPresentationMask;
            deferredStartupPresentationMask = UiRefreshChannel.None;
        }
        if (mask == UiRefreshChannel.None)
        {
            return false;
        }
        Action flush = delegate
        {
            var stopwatch = Stopwatch.StartNew();
            LogUiSuppression("startup_presentation_flush start mask=" + mask);
            FlushPendingUiRefresh(mask, GetActiveStartupProgressOperationToken(), allowStartupPresentationDefer: false, logReadiness: false);
            stopwatch.Stop();
            LogUiSuppression("startup_presentation_flush done elapsedMs=" + stopwatch.ElapsedMilliseconds + " mask=" + mask);
            SchedulePostStartupBestEffortWarmups("startup_presentation_flush_done");
            ShowInitialSetupCompletionMessageIfPending();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            flush();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(flush);
        }
        return true;
    }

    private void ShowInitialSetupCompletionMessageIfPending()
    {
        if (!initialSetupCompletionMessagePending)
        {
            return;
        }
        initialSetupCompletionMessagePending = false;
        Action showMessage = delegate
        {
            ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, MessageBoxImage.Asterisk, "Initial setup completion notification");
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            showMessage();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(showMessage);
        }
    }

    private void QueueStartupInitializationCompleteRetryUnsafe()
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
            TryLogStartupInitializationComplete();
        });
    }

    /// <summary>
    /// 完了表示後に起動・リロード進捗を非表示にします。
    /// </summary>
    /// <param name="operationToken">非表示対象の operation token。</param>
    private void ScheduleStartupProgressHide(long operationToken)
    {
        Task.Run(async delegate
        {
            await Task.Delay(2000).ConfigureAwait(false);
            bool shouldClear = false;
            lock (startupProgressLock)
            {
                if (startupProgressState.IsActive && !startupProgressState.IsFailed && startupProgressState.OperationToken == operationToken && startupProgressState.CompletionHideScheduled)
                {
                    startupProgressState = new StartupProgressState();
                    shouldClear = true;
                }
            }
            if (shouldClear)
            {
                RecomputeStartupProgressPresentation();
            }
        });
    }

    /// <summary>
    /// operation 種別に応じた進行中ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>進行中ラベル。</returns>
    private static string GetStartupProgressRunningLabel(StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.ReloadFileDiff => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_files,
            StartupProgressOperationKind.ScoreOnly => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_scores,
            StartupProgressOperationKind.FullReinitialize => BeMusicSeeker.Properties.Resources.Statusbar_progress_full_reinitialize,
            StartupProgressOperationKind.ReloadTables => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_tables,
            _ => BeMusicSeeker.Properties.Resources.Statusbar_progress_startup,
        };
    }

    /// <summary>
    /// operation 種別に応じた完了ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>完了ラベル。</returns>
    private static string GetStartupProgressCompletedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete;
        }
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_scores;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reload;
    }

    /// <summary>
    /// operation 種別に応じた失敗ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>失敗ラベル。</returns>
    private static string GetStartupProgressFailedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed;
        }
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_scores;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reload;
    }

    /// <summary>
    /// 現在の未完了フェーズに対応するサブラベルを返します。
    /// </summary>
    /// <param name="state">進捗状態。</param>
    /// <returns>サブラベル。</returns>
    private static string GetStartupProgressSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressLibraryLoadCompleted(state))
        {
            return GetStartupProgressLibraryLoadSubLabel(state);
        }
        if (!IsStartupProgressUiPrepareCompleted(state))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if ((state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) == 0)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistEntriesHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartInfoBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartInfoBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartInfoBackfillProcessedCount, state.ChartInfoBackfillTotalCount, fileName);
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartDigestBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartDigestBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartDigestBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartDigestBackfillProcessedCount, state.ChartDigestBackfillTotalCount, fileName);
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.Lr2SongDbSyncDone))
        {
            int displayedProcessedCount = state.Lr2SongDbSyncStageTotalCount > 0
                ? state.Lr2SongDbSyncStageProcessedCount
                : state.Lr2SongDbSyncProcessedCount;
            int displayedTotalCount = state.Lr2SongDbSyncStageTotalCount > 0
                ? state.Lr2SongDbSyncStageTotalCount
                : state.Lr2SongDbSyncTotalCount;
            return FormatStartupProgressCountLabel(
                BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_lr2_song_db_sync,
                displayedProcessedCount,
                displayedTotalCount,
                FormatLr2SongDbSyncStageLabel(state.Lr2SongDbSyncStage));
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied) || !IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_ref;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ScoreHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_score_hydration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.RankingRefreshDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ranking_refresh;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_maintenance;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_installable_maintenance;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background;
    }

    private static string FormatLr2SongDbSyncStageLabel(string stage)
    {
        return string.IsNullOrWhiteSpace(stage)
            ? string.Empty
            : stage.Replace('_', ' ');
    }

    private static bool TryGetStartupProgressStageValue(StartupProgressState state, out double value, out double maximum)
    {
        value = 0.0;
        maximum = 1.0;
        if (state == null
            || !state.IsActive
            || state.IsFailed
            || !CanCompleteStartupProgressPhase(state, StartupProgressPhase.Lr2SongDbSyncDone)
            || state.Lr2SongDbSyncStageTotalCount <= 0)
        {
            return false;
        }

        maximum = Math.Max(1.0, state.Lr2SongDbSyncStageTotalCount);
        value = Math.Max(0.0, Math.Min(maximum, state.Lr2SongDbSyncStageProcessedCount));
        return true;
    }

    private static string GetStartupProgressLibraryLoadSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryDatabaseLoadDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_db_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileEnumerationDone))
        {
            string scanner = state.LibraryInitializationProgressScannerLabel;
            if (!string.IsNullOrWhiteSpace(scanner))
            {
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration + " (" + scanner + ")";
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileDiffDone))
        {
            if (state.LibraryInitializationProgressTotalCount > 0)
            {
                string fileName = string.IsNullOrWhiteSpace(state.LibraryInitializationProgressCurrentPath) ? string.Empty : Path.GetFileName(state.LibraryInitializationProgressCurrentPath);
                return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff, state.LibraryInitializationProgressProcessedCount, state.LibraryInitializationProgressTotalCount, fileName);
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_load;
    }

    private static string FormatStartupProgressCountLabel(string phaseLabel, int processedCount, int totalCount, string fileName)
    {
        string prefix = "[" + Math.Max(0, processedCount) + "/" + Math.Max(0, totalCount) + "] " + (phaseLabel ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return prefix;
        }
        return prefix + " " + fileName;
    }

    /// <summary>
    /// ライブラリ読込フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressLibraryLoadCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyData) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// 画面準備フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressUiPrepareCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyUi) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// プレイリスト参照/保守更新フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressReferencePhaseCompleted(StartupProgressState state)
    {
        return IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone);
    }

    private static int CountExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.Lr2SongDbSyncDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupBackgroundTasksDone, ref count);
        return count;
    }

    private static StartupProgressPhase GetInitialExpectedStartupProgressPhases(StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyData
                                | StartupProgressPhase.StartupReadyUi
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ExternalPlaylistSyncDone
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone
                                | StartupProgressPhase.MaintenanceDeferredDone
                                | StartupProgressPhase.InstallableMaintenanceDeferredDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.Lr2SongDbSyncDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone
                                | StartupProgressPhase.StartupBackgroundTasksDone,
            StartupProgressOperationKind.FullReinitialize => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone
                                | StartupProgressPhase.MaintenanceDeferredDone
                                | StartupProgressPhase.InstallableMaintenanceDeferredDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.Lr2SongDbSyncDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ReloadFileDiff => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ScoreOnly => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone,
            StartupProgressOperationKind.ReloadTables => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ExternalPlaylistSyncDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            _ => StartupProgressPhase.CoreInitializeStarted | StartupProgressPhase.StartupReadyOperable,
        };
    }

    private static int CountCompletedExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.Lr2SongDbSyncDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupBackgroundTasksDone, ref count);
        return count;
    }

    internal static StartupProgressTestResult ReduceStartupProgressForTest(string operationKindName, params string[] actions)
    {
        StartupProgressOperationKind operationKind = ParseStartupProgressOperationKindForTest(operationKindName);
        var state = new StartupProgressState
        {
            OperationKind = operationKind,
            IsActive = true,
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(operationKind),
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted
        };
        int ignoredRequests = 0;
        int ignoredCompletes = 0;
        foreach (string rawAction in actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawAction))
            {
                continue;
            }
            string[] parts = rawAction.Split([':'], 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException("Action must be formatted as verb:PhaseName.", nameof(actions));
            }
            string verb = parts[0].Trim();
            if (string.Equals(verb, "request", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if ((state.ExpectedPhases & phase) == 0)
                {
                    ignoredRequests++;
                    continue;
                }
                state.RequestedPhases |= phase;
            }
            else if (string.Equals(verb, "complete", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if (CanCompleteStartupProgressPhase(state, phase))
                {
                    state.CompletedPhases |= phase;
                }
                else
                {
                    ignoredCompletes++;
                }
            }
            else if (string.Equals(verb, "skip", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if ((state.ExpectedPhases & phase) != 0 && (state.RequestedPhases & phase) == 0)
                {
                    state.CompletedPhases |= phase;
                    state.SkippedPhases |= phase;
                }
            }
            else if (string.Equals(verb, "fail", StringComparison.OrdinalIgnoreCase))
            {
                state.IsFailed = true;
                state.FailureSubLabel = parts[1].Trim();
                state.CompletionHideScheduled = false;
            }
            else if (string.Equals(verb, "library", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                state.LibraryInitializationProgressStage = (BMSLibrary.LibraryInitializationProgressStage)Enum.Parse(typeof(BMSLibrary.LibraryInitializationProgressStage), statusParts[0], ignoreCase: true);
                if (statusParts.Length > 1)
                {
                    state.LibraryInitializationProgressTotalCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 2)
                {
                    state.LibraryInitializationProgressProcessedCount = int.Parse(statusParts[2], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 3)
                {
                    state.LibraryInitializationProgressCurrentPath = statusParts[3];
                }
                if (statusParts.Length > 4)
                {
                    state.LibraryInitializationProgressScannerLabel = statusParts[4];
                }
            }
            else if (string.Equals(verb, "chartinfo", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                if (statusParts.Length > 0)
                {
                    state.ChartInfoBackfillTotalCount = int.Parse(statusParts[0], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 1)
                {
                    state.ChartInfoBackfillProcessedCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 2)
                {
                    state.ChartInfoBackfillCurrentPath = statusParts[2];
                }
            }
            else if (string.Equals(verb, "hydrate", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                if (statusParts.Length > 0)
                {
                    state.ChartInfoHydrationTotalCount = int.Parse(statusParts[0], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 1)
                {
                    state.ChartInfoHydrationAppliedCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
            }
            else if (string.Equals(verb, "lr2songdbsync", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                if (statusParts.Length > 0)
                {
                    state.Lr2SongDbSyncTotalCount = int.Parse(statusParts[0], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 1)
                {
                    state.Lr2SongDbSyncProcessedCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 2)
                {
                    state.Lr2SongDbSyncStage = statusParts[2];
                }
                if (statusParts.Length > 3)
                {
                    state.Lr2SongDbSyncStageTotalCount = int.Parse(statusParts[3], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 4)
                {
                    state.Lr2SongDbSyncStageProcessedCount = int.Parse(statusParts[4], CultureInfo.InvariantCulture);
                }
            }
            else
            {
                throw new ArgumentException("Unsupported action verb: " + verb, nameof(actions));
            }
        }
        bool completed = AreExpectedStartupProgressPhasesCompleted(state);
        bool operableCompleted = (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
        string label = state.IsFailed
            ? GetStartupProgressFailedLabel(operationKind)
            : completed
            ? GetStartupProgressCompletedLabel(operationKind)
            : (operableCompleted ? BeMusicSeeker.Properties.Resources.Statusbar_progress_operable_background : GetStartupProgressRunningLabel(operationKind));
        string subLabel = state.IsFailed
            ? (!string.IsNullOrWhiteSpace(state.FailureSubLabel) ? state.FailureSubLabel : GetStartupProgressSubLabel(state))
            : completed ? string.Empty : GetStartupProgressSubLabel(state);
        double progressMaximum = Math.Max(1.0, CountExpectedStartupProgressPhases(state));
        double progressValue = CountCompletedExpectedStartupProgressPhases(state);
        if (TryGetStartupProgressStageValue(state, out double stageValue, out double stageMaximum))
        {
            progressValue = stageValue;
            progressMaximum = stageMaximum;
        }
        return new StartupProgressTestResult
        {
            ExpectedCount = CountExpectedStartupProgressPhases(state),
            CompletedCount = CountCompletedExpectedStartupProgressPhases(state),
            RequestedCount = CountStartupProgressPhases(state.RequestedPhases & state.ExpectedPhases),
            SkippedCount = CountStartupProgressPhases(state.SkippedPhases & state.ExpectedPhases),
            IgnoredRequestCount = ignoredRequests,
            IgnoredCompleteCount = ignoredCompletes,
            Label = label,
            SubLabel = subLabel,
            IsCompleted = completed,
            IsFailed = state.IsFailed,
            ProgressValue = progressValue,
            ProgressMaximum = progressMaximum
        };
    }

    internal static int GetInitialStartupProgressExpectedCountForTest(string operationKindName)
    {
        var state = new StartupProgressState
        {
            OperationKind = ParseStartupProgressOperationKindForTest(operationKindName),
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(ParseStartupProgressOperationKindForTest(operationKindName))
        };
        return CountExpectedStartupProgressPhases(state);
    }

    private static int CountStartupProgressPhases(StartupProgressPhase phases)
    {
        int count = 0;
        CountStartupProgressPhase(phases, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyData, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyUi, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyOperable, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.Lr2SongDbSyncDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.RankingRefreshDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupBackgroundTasksDone, ref count);
        return count;
    }

    private static void CountStartupProgressPhase(StartupProgressPhase phases, StartupProgressPhase phase, ref int count)
    {
        if ((phases & phase) != 0)
        {
            count++;
        }
    }

    private static StartupProgressOperationKind ParseStartupProgressOperationKindForTest(string operationKindName)
    {
        return (StartupProgressOperationKind)Enum.Parse(typeof(StartupProgressOperationKind), operationKindName, ignoreCase: true);
    }

    private static StartupProgressPhase ParseStartupProgressPhaseForTest(string phaseName)
    {
        return (StartupProgressPhase)Enum.Parse(typeof(StartupProgressPhase), phaseName, ignoreCase: true);
    }

    private static void CountExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase))
        {
            count++;
        }
    }

    private static void CountCompletedExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase) && (state.CompletedPhases & phase) != 0)
        {
            count++;
        }
    }

    private static bool AreExpectedStartupProgressPhasesCompleted(StartupProgressState state)
    {
        StartupProgressPhase expected = state.ExpectedPhases;
        return expected == StartupProgressPhase.None || (state.CompletedPhases & expected) == expected;
    }

    /// <summary>
    /// 指定フェーズが待機対象かどうかを返します。
    /// </summary>
    private static bool IsStartupProgressPhaseExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0;
    }

    /// <summary>
    /// 指定フェーズが完了済み、または待機対象外かどうかを返します。
    /// </summary>
    private static bool IsStartupProgressPhaseCompletedOrNotExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return !IsStartupProgressPhaseExpected(state, phase) || (state.CompletedPhases & phase) != 0;
    }

    /// <summary>
    /// deferred playlist 参照要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    private static bool ShouldTrackStartupProgressPlaylistReference(string reason, StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal) || string.Equals(reason, "DeferredExternalSync:Initialize", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadFileDiff => string.Equals(reason, "ReloadFileDiff", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.FullReinitialize => string.Equals(reason, "FullReinitialize", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "DeferredExternalSync:ReloadTables", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// deferred 外部プレイリスト同期要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    private static bool ShouldTrackStartupProgressExternalSync(string reason, StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "ReloadTables", StringComparison.Ordinal),
            _ => false,
        };
    }

    private void ShowPlaylistLoadFailure(Exception ex)
    {
        string message = BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist;
        if (ex != null && !string.IsNullOrWhiteSpace(ex.Message))
        {
            message = message + Environment.NewLine + ex.Message;
        }
        ShowUiMessage(message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    private void ShowBeatorajaTableUrlImportSummary(BeatorajaTableUrlImportSummary summary)
    {
        if (summary == null)
        {
            return;
        }
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_summary_format,
            summary.ExistingCount,
            summary.ImportedCount,
            summary.RestoredFromBmtCount,
            summary.FailedCount,
            summary.WarningCount);
        AppendBeatorajaTableUrlImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_warning_header, summary.WarningOutcomes);
        AppendBeatorajaTableUrlImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_failed_header, summary.FailedOutcomes);
        ShowUiMessage(
            message.ToString(),
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_title,
            summary.FailedCount > 0 || summary.WarningCount > 0 ? MessageBoxImage.Exclamation : MessageBoxImage.Information);
    }

    private static void AppendBeatorajaTableUrlImportOutcomeSamples(StringBuilder message, string header, IReadOnlyList<BeatorajaTableUrlImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (message == null || outcomes == null || outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (BeatorajaTableUrlImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? outcome.RawUrl ?? string.Empty);
            if (outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message))
            {
                message.AppendLine("- " + nameOrUri + " (" + outcome.Exception.Message + ")");
            }
            else
            {
                message.AppendLine("- " + nameOrUri);
            }
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
    }

    private void ShowExternalPlaylistImportQueueSummary(ExternalPlaylistImportQueueSummary summary)
    {
        if (summary == null || !summary.HasNotifiableItems)
        {
            return;
        }
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Playlist_import_result_summary_format,
            summary.ImportedCount,
            summary.SkippedDuplicateNameCount,
            summary.FailedCount);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_skipped_header, summary.SkippedDuplicateNameOutcomes);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_failed_header, summary.FailedOutcomes);
        ShowUiMessage(
            message.ToString(),
            BeMusicSeeker.Properties.Resources.Playlist_import_result_title,
            summary.FailedCount > 0 ? MessageBoxImage.Exclamation : MessageBoxImage.Information);
    }

    private static void AppendImportOutcomeSamples(StringBuilder message, string header, IReadOnlyList<ExternalPlaylistImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (message == null || outcomes == null || outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (ExternalPlaylistImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? string.Empty);
            if (outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed && outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message))
            {
                message.AppendLine("- " + nameOrUri + " (" + outcome.Exception.Message + ")");
            }
            else
            {
                message.AppendLine("- " + nameOrUri);
            }
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
    }

    private void ReleaseDuplicateRefreshPriorityWindowAfterUiRefresh(string reason)
    {
        try
        {
            DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
            {
                PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
            }, DispatcherPriority.ApplicationIdle);
        }
        catch
        {
            PlaylistWorkspace.ReleaseDuplicateRefreshPriorityWindow(reason);
        }
    }

    internal void RenameChartFolder(RenameChartFolderRequest request, string newFolder)
    {
        string chartPath = request?.Chart?.Path;
        if (request?.HasTarget != true
            || string.IsNullOrWhiteSpace(chartPath)
            || string.IsNullOrWhiteSpace(newFolder))
        {
            return;
        }
        RunChartPackageMutation(delegate
        {
            string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(chartPath);
            if (!string.IsNullOrWhiteSpace(directoryNameSimple) && LongPathFileSystem.DirectoryExists(directoryNameSimple))
            {
                files.RenameChartFolder(directoryNameSimple, newFolder, false);
                regularChartListOwner.ApplyLatestNormalLibraryRefreshNotification("library_charts_changed");
                InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
            }
        }, [request.Chart], UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
    }

    private bool HasFolderAutoRenameAllTargets(BMSLibrary library, string parentDirectory)
    {
        if (!ReferenceEquals(files, library) || library == null)
        {
            return false;
        }
        bool hasTargets = false;
        RunChartPackageMutation(
            () => hasTargets = library.HasAutoRenameAllChartFolderTargets(parentDirectory),
            refreshMask: UiRefreshChannel.None,
            stopPlayback: () => { });
        return hasTargets;
    }

    private FolderAutoRenameExecutionResult ExecuteFolderAutoRenameSelectedMutation(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter)
    {
        if (!ReferenceEquals(files, library) || request?.HasTargets != true)
        {
            return new FolderAutoRenameExecutionResult();
        }
        RunChartPackageMutation(
            () => library.AutoRenameChartFolders(request.Charts, progressReporter: progressReporter),
            request.Charts,
            UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
        return new FolderAutoRenameExecutionResult { RefreshRequired = true };
    }

    private FolderAutoRenameExecutionResult ExecuteFolderAutoRenameAllMutation(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter)
    {
        if (!ReferenceEquals(files, library))
        {
            return new FolderAutoRenameExecutionResult();
        }
        bool changed = false;
        RunChartPackageMutation(
            () => changed = library.AutoRenameAllChartFolders(parentDirectory, progressReporter),
            refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree,
            stopPlayback: () => PlaybackPanel.StopPlayback(closeProcess: true));
        return new FolderAutoRenameExecutionResult { RefreshRequired = changed };
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

    private static void PresentPlaylistOperationNotifications(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName)
    {
        if (receipt == null)
        {
            return;
        }
        foreach (PlaylistOperationNotificationOwner.OperationNotification notification in receipt.Notifications)
        {
            MessageBoxImage icon = notification.Severity switch
            {
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information => MessageBoxImage.Asterisk,
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning => MessageBoxImage.Exclamation,
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error => MessageBoxImage.Hand,
                _ => MessageBoxImage.None,
            };
            ShowUiMessage(notification.Message, notification.Caption, icon, routeName);
        }
    }

    private static bool ToUiConfirmationDecision(UiDialogResult result, string routeName)
    {
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
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
