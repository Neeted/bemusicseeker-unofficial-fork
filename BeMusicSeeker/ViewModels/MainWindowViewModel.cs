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

internal sealed class ShutdownPreparationResult
{
    internal ShutdownPreparationResult(
        string reason,
        long elapsedMs,
        bool slowWaitLogged,
        int sqliteCloseFailureCount)
    {
        Reason = reason ?? "shutdown";
        ElapsedMs = elapsedMs;
        SlowWaitLogged = slowWaitLogged;
        SqliteCloseFailureCount = Math.Max(0, sqliteCloseFailureCount);
    }

    internal string Reason { get; }

    internal long ElapsedMs { get; }

    internal bool SlowWaitLogged { get; }

    internal int SqliteCloseFailureCount { get; }

    internal string ToLogFields()
    {
        return "reason=" + FormatForLog(Reason)
            + " elapsedMs=" + ElapsedMs
            + " slowWaitLogged=" + FormatBool(SlowWaitLogged)
            + " sqliteCloseFailureCount=" + SqliteCloseFailureCount;
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private static string FormatForLog(string value)
    {
        return (value ?? string.Empty).Replace(Environment.NewLine, " | ");
    }
}


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
    /// Gets status-bar progress presentation state owned outside the shell ViewModel while legacy root bindings remain in place.
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

    /// <summary>
    /// Gets the one-shot startup update workflow owned by application composition.
    /// </summary>
    internal StartupUpdateWorkflowOwner StartupUpdateWorkflow { get; private set; }

    internal ElevatedProcessWarningWorkflowOwner ElevatedProcessWarningWorkflow { get; private set; }

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

    public PlayHistoryWorkflowOwner PlayHistory { get; }

    /// <summary>
    /// Compatibility name for callers compiled against the former nested sort contract.
    /// New code uses the feature-owned <see cref="global::BeMusicSeeker.ViewModels.ChartListSortParameters"/>.
    /// </summary>
    public class cSortParameters : ChartListSortParameters
    {
    }

    /// <summary>
    /// Compatibility name for callers compiled against the former nested chart mode filter.
    /// The main-window binding uses <see cref="ChartFilters"/> instead.
    /// </summary>
    [Flags]
    public enum ModeFilterType
    {
        None = 0,
        _5KEYS = 1,
        _7KEYS = 2,
        _9KEYS = 4,
        _10KEYS = 8,
        _14KEYS = 0x10,
        All = 0x1F
    }

    public enum FolderFilterType
    {
        DirectoryFilter,
        ArtistFilter,
        PlayListFilter,
        FilterNone
    }

    /// <summary>
    /// 起動・リロード進捗の対象 operation 種別です。
    /// </summary>
    private enum StartupProgressOperationKind
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

    private sealed class StartupBackgroundTaskRequest
    {
        internal string Name;

        internal string Reason;

        internal string Dependency;

        internal string CoalesceKey;

        internal string Lane;

        internal int Priority;

        internal long Version;

        internal Func<Task> Work;
    }

    private sealed class StartupBackgroundTaskMetric
    {
        internal string Name = string.Empty;

        internal string Reason = string.Empty;

        internal string Dependency = string.Empty;

        internal string Lane = string.Empty;

        internal long QueuedCount;

        internal long StartedCount;

        internal long CompletedCount;

        internal long FailedCount;

        internal long TotalElapsedMs;

        internal long LastElapsedMs;

        internal string LastStatus = string.Empty;

        internal string LastDetail = string.Empty;
    }

    public enum MaintenanceFilterType
    {
        FullScanAllChartsFilter = 32,
        FileMissingFilter = 33,
        FileMissingIgnoredFilter,
        DuplicateFilter,
        GarbledFilter,
        GarbleFixedFilter,
        UnregisteredFilter,
        ZeroNoteFilter,
        ChartInfoParseErrorFilter,
        FilterNone = 255
    }

    public enum InstallFilterType
    {
        NewlyInstalledFilter = 49,
        PendingInstallFilter
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

    /// <summary>
    /// 設定画面から LR2 `song` / `folder` 派生データの手動再同期を要求できる状態かどうかを返します。
    /// この再同期は現在の所持譜面とプレイリストを入力にするため、初回設定中のように
    /// ライブラリプロファイルがまだ成立していない間は許可しません。
    /// </summary>
    public bool CanRequestLr2SongDbSyncDataResync => HasActiveLibraryProfile
        && settingDialog?.OperationModeLR2DB == true
        && !IsLibraryOperationInProgress;

    private void RaiseLr2SongDbSyncDataResyncAvailabilityChanged()
    {
        RaisePropertyChanged(() => CanRequestLr2SongDbSyncDataResync);
    }

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

    private readonly IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    private StartupSettingsSnapshot GetStartupSettingsSnapshot()
    {
        return startupSettingsProvider()
            ?? throw new InvalidOperationException("Startup settings provider returned null.");
    }

    private LR2Config lr2config;

    private PropertyChangedEventListener listenerForBMSLibrary;

    private CollectionChangedEventListener listenerForBMSLibraryChartPackagesPendingCollection;

    private CollectionChangedEventListener listenerForBMSLibraryChartPackagesInstalledCollection;

    private readonly object lockThis = new();

    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private static readonly TimeSpan ShutdownDrainWarningThreshold = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ShutdownQueueDrainWarningThreshold = TimeSpan.FromSeconds(20);

    private readonly object shutdownPreparationLock = new();

    private int shutdownRequested;

    private Task<ShutdownPreparationResult> shutdownPreparationTask;

    private readonly ChartFileOperationSynchronizer chartFileOperations = new();

    private readonly SemaphoreSlim packageInstallLibraryGate = new(1, 1);

    private int chartPackageMutationDepth;

    private readonly object duplicateChartGroupsRefreshLock = new();

    private bool duplicateChartGroupsRefreshRunning;

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel deferredStartupPresentationMask = UiRefreshChannel.None;

    private readonly object lockUiSuppression = new();

    private bool deferredLibraryFolderTreeRefreshQueued;

    private readonly object lockDeferredLibraryFolderTreeRefresh = new();

    private readonly object startupBackgroundTaskLock = new();

    private readonly List<StartupBackgroundTaskRequest> startupBackgroundTaskQueue = [];

    private readonly HashSet<string> startupBackgroundTaskCompletedNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> startupBackgroundTaskRunningCountByLane = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, StartupBackgroundTaskMetric> startupBackgroundTaskMetrics = new(StringComparer.OrdinalIgnoreCase);

    private bool startupBackgroundTaskSchedulerStarted;

    private int startupBackgroundTaskRunningCount;

    private long startupBackgroundTaskVersion;

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

    private Lr2SongDbSyncRuntimeStatus latestLr2SongDbSyncStatus = Lr2SongDbSyncStatusMapper.CreateNone();

    private string _KeywordSearchWarningText = string.Empty;

    private bool _IsKeywordSearchHelpOpen;

    private readonly ObservableCollection<KeywordSearchSuggestionItem> _KeywordSearchSuggestions = [];

    private readonly List<string> keywordSearchHistory = [];

    private bool _IsKeywordSearchSuggestionPopupOpen;

    private string _KeywordSearchSuggestionHeaderText = string.Empty;

    private readonly DispatcherCollection<string> _sortedBmsParentFolderList = new(DispatcherHelper.UIDispatcher);

    private bool bmsParentFolderListViewInitialized;

    private MainViewUpdateMode treeViewFilterTypeSelected;

    private object treeViewFilterParameterSelected;

    private static readonly string scoreRegisterUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/register";

    private static readonly string scoreStatusUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/status?md5=";

    private static readonly string scoreViewUrl = "https://bms-score-viewer.pages.dev/view?md5=";

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

    internal static string BuildKeywordSearchWarningText(string keywordFilter, GridKeywordSearchContext context)
    {
        return KeywordSearchPresentationText.BuildWarningText(keywordFilter, context);
    }

    internal static string BuildKeywordSearchHelpText(GridKeywordSearchContext context)
    {
        return KeywordSearchPresentationText.BuildHelpText(context);
    }

    /// <summary>
    /// 検索候補 popup の見出しを返します。
    /// field 補完と履歴は同じ popup に載るため、候補種別に応じて表示を切り替えます。
    /// </summary>
    /// <param name="kind">候補種別。</param>
    /// <returns>popup 見出し。</returns>
    internal static string BuildKeywordSearchSuggestionHeaderText(KeywordSearchSuggestionKind kind)
    {
        return KeywordSearchPresentationText.BuildSuggestionHeaderText(kind);
    }

    /// <summary>
    /// 検索履歴候補を作ります。
    /// </summary>
    /// <param name="history">履歴一覧。</param>
    /// <param name="currentText">現在の検索文字列。</param>
    /// <returns>履歴候補一覧。</returns>
    internal static IReadOnlyList<KeywordSearchSuggestionItem> BuildKeywordSearchHistorySuggestions(IEnumerable<string> history, string currentText)
    {
        return KeywordSearchPresentationText.BuildHistorySuggestions(history, currentText);
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
        lock (startupBackgroundTaskLock)
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
        StartStartupBackgroundTaskScheduler();
        TryCompleteStartupBackgroundTasksPhaseIfIdle();
    }

    private bool QueueStartupBackgroundTask(string name, string reason, string dependency, Func<Task> work)
    {
        if (work == null)
        {
            return false;
        }
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        if (IsShutdownRequested)
        {
            LogUiSuppression("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested");
            return false;
        }
        string normalizedDependency = string.IsNullOrWhiteSpace(dependency) ? null : dependency;
        string normalizedLane = GetStartupBackgroundTaskLane(normalizedName);
        string coalesceKey = normalizedName;
        long version;
        bool shouldStartWorker = false;
        lock (startupBackgroundTaskLock)
        {
            if (IsShutdownRequested)
            {
                LogUiSuppression("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested_after_lock");
                return false;
            }
            RecordStartupBackgroundTaskQueuedUnsafe(normalizedName, normalizedReason, normalizedDependency, normalizedLane);
            version = ++startupBackgroundTaskVersion;
            StartupBackgroundTaskRequest existing = startupBackgroundTaskQueue.LastOrDefault(item => string.Equals(item.CoalesceKey, coalesceKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Reason = normalizedReason;
                existing.Dependency = normalizedDependency;
                existing.Lane = normalizedLane;
                existing.Priority = GetStartupBackgroundTaskPriority(normalizedName);
                existing.Version = version;
                existing.Work = work;
                LogUiSuppression("startup_background_task skipped name=" + normalizedName + " version=" + version + " reason=" + normalizedReason + " coalesceKey=" + coalesceKey + " replaced=true");
            }
            else
            {
                startupBackgroundTaskQueue.Add(new StartupBackgroundTaskRequest
                {
                    Name = normalizedName,
                    Reason = normalizedReason,
                    Dependency = normalizedDependency,
                    Lane = normalizedLane,
                    CoalesceKey = coalesceKey,
                    Priority = GetStartupBackgroundTaskPriority(normalizedName),
                    Version = version,
                    Work = work
                });
            }
            LogUiSuppression("startup_background_task queue name=" + normalizedName + " version=" + version + " reason=" + normalizedReason + " dependency=" + (normalizedDependency ?? "(none)") + " lane=" + normalizedLane + " priority=" + GetStartupBackgroundTaskPriority(normalizedName));
            shouldStartWorker = startupBackgroundTaskSchedulerStarted;
        }
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorkers();
        }
        return true;
    }

    private void TryCompleteStartupBackgroundTasksPhaseIfIdle()
    {
        bool schedulerIdle;
        lock (startupBackgroundTaskLock)
        {
            schedulerIdle = startupBackgroundTaskSchedulerStarted
                && startupBackgroundTaskQueue.Count == 0
                && startupBackgroundTaskRunningCount == 0;
        }
        if (!schedulerIdle)
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

    private StartupBackgroundTaskMetric GetOrCreateStartupBackgroundTaskMetricUnsafe(string name)
    {
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        if (!startupBackgroundTaskMetrics.TryGetValue(normalizedName, out StartupBackgroundTaskMetric metric))
        {
            metric = new StartupBackgroundTaskMetric
            {
                Name = normalizedName
            };
            startupBackgroundTaskMetrics[normalizedName] = metric;
        }
        return metric;
    }

    private void RecordStartupBackgroundTaskQueuedUnsafe(string name, string reason, string dependency, string lane)
    {
        StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
        metric.QueuedCount++;
        metric.Reason = reason ?? string.Empty;
        metric.Dependency = dependency ?? string.Empty;
        metric.Lane = lane ?? string.Empty;
        metric.LastStatus = "queued";
    }

    private void RecordStartupBackgroundTaskStarted(string name)
    {
        lock (startupBackgroundTaskLock)
        {
            StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
            metric.StartedCount++;
            metric.LastStatus = "running";
        }
    }

    private void RecordStartupBackgroundTaskCompleted(string name, string status, long elapsedMs, bool failed, string detail)
    {
        lock (startupBackgroundTaskLock)
        {
            StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                metric.QueuedCount++;
                metric.Reason = detail ?? string.Empty;
                metric.Lane = GetStartupBackgroundTaskLane(name);
                metric.LastStatus = "queued";
                return;
            }
            if (string.Equals(status, "start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                metric.StartedCount++;
                if (string.IsNullOrWhiteSpace(metric.Lane))
                {
                    metric.Lane = GetStartupBackgroundTaskLane(name);
                }
                metric.LastStatus = "running";
                metric.LastDetail = detail ?? string.Empty;
                return;
            }
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = string.IsNullOrWhiteSpace(status) ? (failed ? "failed" : "done") : status;
            if (string.IsNullOrWhiteSpace(metric.Lane))
            {
                metric.Lane = GetStartupBackgroundTaskLane(name);
            }
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
    }

    private static int GetStartupBackgroundTaskPriority(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }
        if (string.Equals(name, "playlist_library_index_prewarm", StringComparison.OrdinalIgnoreCase))
        {
            return 15;
        }
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }
        if (string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }
        if (string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }
        if (string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 55;
        }
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }
        if (string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }
        if (string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }
        return 100;
    }

    private static string GetStartupBackgroundTaskLane(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return "read_hydration";
        }
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase))
        {
            // Startup completion waits for installable maintenance, which depends on maintenance hydration.
            // Keep it off the playlist/chart read lane so it can overlap with independent post-operable reads.
            return "maintenance_hydration";
        }
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase))
        {
            return "playlist_followup";
        }
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase))
        {
            return "dependent_maintenance";
        }
        return "default";
    }

    private static int GetStartupBackgroundTaskLaneConcurrency(string lane)
    {
        if (string.Equals(lane, "read_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return 1;
    }

    private static int GetStartupBackgroundTaskTotalConcurrency()
    {
        return 4;
    }

    private void StartStartupBackgroundTaskScheduler()
    {
        bool shouldStartWorker;
        lock (startupBackgroundTaskLock)
        {
            if (startupBackgroundTaskSchedulerStarted)
            {
                return;
            }
            startupBackgroundTaskSchedulerStarted = true;
            shouldStartWorker = startupBackgroundTaskQueue.Count > 0;
        }
        LogUiSuppression("startup_background_task scheduler_start");
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorkers();
        }
        else
        {
            TryCompleteStartupBackgroundTasksPhaseIfIdle();
        }
    }

    private void TryStartStartupBackgroundTaskWorkers()
    {
        while (true)
        {
            StartupBackgroundTaskRequest request = null;
            int laneRunningCount = 0;
            int totalRunningCount = 0;
            lock (startupBackgroundTaskLock)
            {
                if (!startupBackgroundTaskSchedulerStarted
                    || startupBackgroundTaskRunningCount >= GetStartupBackgroundTaskTotalConcurrency())
                {
                    return;
                }
                int index = FindNextStartupBackgroundTaskIndexUnsafe();
                if (index < 0)
                {
                    return;
                }
                request = startupBackgroundTaskQueue[index];
                startupBackgroundTaskQueue.RemoveAt(index);
                startupBackgroundTaskRunningCount++;
                startupBackgroundTaskRunningCountByLane.TryGetValue(request.Lane, out int runningInLane);
                startupBackgroundTaskRunningCountByLane[request.Lane] = runningInLane + 1;
                laneRunningCount = runningInLane + 1;
                totalRunningCount = startupBackgroundTaskRunningCount;
            }
            StartStartupBackgroundTaskWorker(request, laneRunningCount, totalRunningCount);
        }
    }

    private int FindNextStartupBackgroundTaskIndexUnsafe()
    {
        int index = -1;
        int bestPriority = int.MaxValue;
        long bestVersion = long.MaxValue;
        bool shutdownRequestedSnapshot = IsShutdownRequested;
        for (int i = 0; i < startupBackgroundTaskQueue.Count; i++)
        {
            StartupBackgroundTaskRequest candidate = startupBackgroundTaskQueue[i];
            if (!shutdownRequestedSnapshot && !AreStartupBackgroundDependenciesCompletedUnsafe(candidate.Dependency))
            {
                continue;
            }
            if (!CanStartStartupBackgroundTaskInLaneUnsafe(candidate.Lane))
            {
                continue;
            }
            if (candidate.Priority < bestPriority || (candidate.Priority == bestPriority && candidate.Version < bestVersion))
            {
                index = i;
                bestPriority = candidate.Priority;
                bestVersion = candidate.Version;
            }
        }
        return index;
    }

    private bool CanStartStartupBackgroundTaskInLaneUnsafe(string lane)
    {
        string normalizedLane = string.IsNullOrWhiteSpace(lane) ? "default" : lane;
        startupBackgroundTaskRunningCountByLane.TryGetValue(normalizedLane, out int runningCount);
        return runningCount < GetStartupBackgroundTaskLaneConcurrency(normalizedLane);
    }

    private void StartStartupBackgroundTaskWorker(StartupBackgroundTaskRequest request, int laneRunningCount, int totalRunningCount)
    {
        Task.Run(async delegate
        {
            var stopwatch = Stopwatch.StartNew();
            LogUiSuppression("startup_background_task start name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " dependency=" + (request.Dependency ?? "(none)") + " lane=" + request.Lane + " laneRunning=" + laneRunningCount + " totalRunning=" + totalRunningCount);
            RecordStartupBackgroundTaskStarted(request.Name);
            try
            {
                await request.Work().ConfigureAwait(false);
                stopwatch.Stop();
                LogUiSuppression("startup_background_task done name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                RecordStartupBackgroundTaskCompleted(request.Name, "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "reason=" + request.Reason);
                StartupMemoryPressureService.LogCheckpoint(LogUiSuppression, "startup_background_task", request.Name + "_done");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogUiSuppressionWarning("startup_background_task failed name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                RecordStartupBackgroundTaskCompleted(request.Name, "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
                StartupMemoryPressureService.LogCheckpoint(LogUiSuppression, "startup_background_task", request.Name + "_failed");
            }
            finally
            {
                lock (startupBackgroundTaskLock)
                {
                    startupBackgroundTaskCompletedNames.Add(request.Name);
                    startupBackgroundTaskRunningCount = Math.Max(0, startupBackgroundTaskRunningCount - 1);
                    if (!string.IsNullOrWhiteSpace(request.Lane)
                        && startupBackgroundTaskRunningCountByLane.TryGetValue(request.Lane, out int runningInLane))
                    {
                        runningInLane = Math.Max(0, runningInLane - 1);
                        if (runningInLane == 0)
                        {
                            startupBackgroundTaskRunningCountByLane.Remove(request.Lane);
                        }
                        else
                        {
                            startupBackgroundTaskRunningCountByLane[request.Lane] = runningInLane;
                        }
                    }
                }
                TryStartStartupBackgroundTaskWorkers();
                TryCompleteStartupBackgroundTasksPhaseIfIdle();
            }
        }).Logging("StartupBackgroundTaskScheduler");
    }

    private bool AreStartupBackgroundDependenciesCompletedUnsafe(string dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency))
        {
            return true;
        }
        string[] dependencies = dependency.Split([','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in dependencies)
        {
            string dependencyName = item.Trim();
            if (dependencyName.Length > 0 && !startupBackgroundTaskCompletedNames.Contains(dependencyName))
            {
                return false;
            }
        }
        return true;
    }

    private void RefreshLibraryMainViewForCurrentFilter()
    {
        if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
        {
            ExecMaintenanceFilter((MaintenanceFilterType)treeViewFilterTypeSelected, treeViewFilterParameterSelected);
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

    private void ScheduleDeferredLibraryFolderTreeRefresh()
    {
        ScheduleDeferredLibraryFolderTreeRefresh(GetActiveStartupProgressOperationToken());
    }

    private void ScheduleDeferredLibraryFolderTreeRefresh(long operationToken)
    {
        bool shouldSchedule = false;
        lock (lockDeferredLibraryFolderTreeRefresh)
        {
            if (!deferredLibraryFolderTreeRefreshQueued)
            {
                deferredLibraryFolderTreeRefreshQueued = true;
                shouldSchedule = true;
            }
        }
        if (!shouldSchedule)
        {
            return;
        }
        Task.Run(delegate
        {
            BMSLibrary.ParentFolderListCacheSnapshot snapshot = null;
            try
            {
                snapshot = files.BuildBMSParentFolderListCacheSnapshot();
            }
            catch (Exception ex)
            {
                LogUiSuppressionWarning("ui_stall_library_folder_tree_prepare_failed message=" + ex.Message);
            }
            DispatcherHelper.UIDispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                var stopwatch = Stopwatch.StartNew();
                bool shouldReschedule = false;
                try
                {
                    bool refreshed = false;
                    if (snapshot != null)
                    {
                        refreshed = files.TryApplyBMSParentFolderListCacheSnapshot(snapshot);
                        if (!refreshed && files.IsBMSParentFolderListCacheDirty())
                        {
                            shouldReschedule = true;
                        }
                    }
                    if (!shouldReschedule)
                    {
                        RefreshBmsParentFolderListView();
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    LogUiSuppression("ui_suppress flush_library_folder_tree_deferred_ms=" + stopwatch.ElapsedMilliseconds);
                    lock (lockDeferredLibraryFolderTreeRefresh)
                    {
                        deferredLibraryFolderTreeRefreshQueued = false;
                    }
                    if (shouldReschedule)
                    {
                        ScheduleDeferredLibraryFolderTreeRefresh(operationToken);
                    }
                    else
                    {
                        TryLogStartupReadyOperable(operationToken);
                    }
                }
            });
        });
    }

    /// <summary>
    /// BMS 親フォルダ一覧の安定したソート済みビューを、現在のライブラリ状態から更新します。
    /// 他経路でキャッシュが先に構築された場合でも、ツリーと移動メニューが同じ正本を参照できるようにします。
    /// </summary>
    private bool RefreshBmsParentFolderListView(bool raisePropertyChanged = true)
    {
        IEnumerable<string> source = (files != null) ? files.GetBMSParentFolderListSnapshot() : Enumerable.Empty<string>();
        List<string> sortedParentFolders = [.. source.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        bool changed = !_sortedBmsParentFolderList.SequenceEqual(sortedParentFolders, StringComparer.OrdinalIgnoreCase);
        if (changed)
        {
            _sortedBmsParentFolderList.Clear();
            _sortedBmsParentFolderList.AddRange(sortedParentFolders);
        }
        bmsParentFolderListViewInitialized = true;
        if (raisePropertyChanged)
        {
            RaisePropertyChanged(() => BMSParentFolderList);
        }
        return changed;
    }

    /// <summary>
    /// BMS 検索ルートディレクトリ設定の変更を UI 側の親フォルダ一覧へ反映させます。
    /// </summary>
    private void NotifyBmsParentFolderListChanged()
    {
        bmsParentFolderListViewInitialized = false;
        files?.NotifyBMSDirectoriesChanged();
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
            RaisePropertyChanged(() => ChartPackagesInstalled);
            RaisePropertyChanged(() => ChartPackagesPending);
            stopwatch.Stop();
            num = stopwatch.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.PlaylistTree) != 0)
        {
            var stopwatch2 = Stopwatch.StartNew();
            RefreshPlayHistoryDisplayTargets();
            PlaylistWorkspace.RefreshPlaylistTreePresentation();
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
            RaisePropertyChanged(() => DuplicateChartGroups);
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
            ScheduleDeferredLibraryFolderTreeRefresh(operationToken);
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
            RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        }
    }

    private void EndChartPackageMutation()
    {
        int depth = Interlocked.Decrement(ref chartPackageMutationDepth);
        if (depth == 0)
        {
            RaisePropertyChanged(() => IsChartPackageMutationInProgress);
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
            RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        }
        else if (depth < 0)
        {
            Interlocked.Exchange(ref chartPackageMutationDepth, 0);
            RaisePropertyChanged(() => IsChartPackageMutationInProgress);
            RaisePropertyChanged(() => IsLibraryOperationInProgress);
            RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
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

    private void HandleChartPackagesInstalledCollectionChanged()
    {
        if (treeViewFilterTypeSelected == MainViewUpdateMode.NewlyInstalledFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => ChartPackagesInstalled);
    }

    private void HandleChartPackagesPendingCollectionChanged()
    {
        if (treeViewFilterTypeSelected == MainViewUpdateMode.PendingInstallFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => ChartPackagesPending);
    }

    private void RebindChartPackagesInstalledCollectionListener()
    {
        if (listenerForBMSLibraryChartPackagesInstalledCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.ChartPackagesInstalled == null)
        {
            listenerForBMSLibraryChartPackagesInstalledCollection = null;
            return;
        }
        listenerForBMSLibraryChartPackagesInstalledCollection = new CollectionChangedEventListener(files.ChartPackagesInstalled);
        listenerForBMSLibraryChartPackagesInstalledCollection.RegisterHandler(delegate
        {
            HandleChartPackagesInstalledCollectionChanged();
        });
    }

    private void RebindChartPackagesPendingCollectionListener()
    {
        if (listenerForBMSLibraryChartPackagesPendingCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.ChartPackagesPending == null)
        {
            listenerForBMSLibraryChartPackagesPendingCollection = null;
            return;
        }
        listenerForBMSLibraryChartPackagesPendingCollection = new CollectionChangedEventListener(files.ChartPackagesPending);
        listenerForBMSLibraryChartPackagesPendingCollection.RegisterHandler(delegate
        {
            HandleChartPackagesPendingCollectionChanged();
        });
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

    public List<DuplicateGroup> DuplicateChartGroups
    {
        get
        {
            if (files != null)
            {
                return files.DuplicateChartGroups;
            }
            return null;
        }
    }

    public DispatcherCollection<ChartPackage> ChartPackagesInstalled
    {
        get
        {
            if (files != null)
            {
                return files.ChartPackagesInstalled;
            }
            return null;
        }
    }

    public DispatcherCollection<ChartPackage> ChartPackagesPending
    {
        get
        {
            if (files != null)
            {
                return files.ChartPackagesPending;
            }
            return null;
        }
    }

    /// <summary>
    /// ライブラリツリーや移動メニューが共有する、UI 向けの安定したソート済み親フォルダ一覧です。
    /// ライブラリ側の候補 cache から同期される表示用正本であり、昇順表示を保証します。
    /// </summary>
    public DispatcherCollection<string> BMSParentFolderList
    {
        get
        {
            if (files != null && !bmsParentFolderListViewInitialized)
            {
                RefreshBmsParentFolderListView(raisePropertyChanged: false);
            }
            return _sortedBmsParentFolderList;
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
        if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
        {
            var type = (MaintenanceFilterType)treeViewFilterTypeSelected;
            if (type != MaintenanceFilterType.DuplicateFilter)
            {
                ExecMaintenanceFilter(type);
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

    public bool IsPlayHistoryViewActive => CurrentMainViewOperationSection == MainViewOperationSection.PlayHistory;

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
        string fullGenerationReason = "post_startup_" + (reason ?? string.Empty);
        Task.Run(delegate
        {
            files?.QueueLr2SongDbSync(
                fullGenerationReason,
                prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(fullGenerationReason));
        }).Logging("PostStartupLr2SongDbSync");
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
                + " message=" + SanitizeStartupBackgroundSummaryValue(ex.Message));
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

    public cSortParameters SortParameters =>
        ToCompatibilitySortParameters(regularChartListOwner?.CaptureSortParameters());

    public cSortParameters PlayHistorySortParameters
    {
        get
        {
            ChartListSortParameters value = playHistoryWorkflowOwner.CaptureSortParameters(out _);
            return ToCompatibilitySortParameters(value);
        }
        private set
        {
            SetPlayHistorySortParameters(value);
        }
    }

    private void SetPlayHistorySortParameters(ChartListSortParameters value)
    {
        bool changed = playHistoryWorkflowOwner.UpdateSortParameters(value);
        if (changed)
        {
            RaisePropertyChanged("PlayHistorySortParameters");
            if (IsPlayHistoryViewActive)
            {
                SyncMainChartListSortPresentation();
            }
        }
    }

    private static cSortParameters ToCompatibilitySortParameters(ChartListSortParameters value)
    {
        ChartListSortParameters clone = CloneSortParameters(value);
        return clone == null
            ? null
            : new cSortParameters
            {
                ColumnsName = clone.ColumnsName,
                Direction = clone.Direction
            };
    }

    private static ChartListSortParameters CloneSortParameters(ChartListSortParameters value)
    {
        return value == null
            ? null
            : new ChartListSortParameters
            {
                ColumnsName = value.ColumnsName,
                Direction = value.Direction
            };
    }

    private void SyncMainChartListSortPresentation()
    {
        bool isPlayHistory = IsPlayHistoryViewActive;
        ChartListSortParameters sortParameters = isPlayHistory
            ? PlayHistorySortParameters
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
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
    }

    public bool IsLibraryOperationInProgress
    {
        get
        {
            lock (startupProgressLock)
            {
                return _IsStartupUiInteractionBlocked || startupProgressState.IsActive || IsChartPackageMutationInProgress;
            }
        }
    }

    public bool IsChartPackageMutationInProgress => Volatile.Read(ref chartPackageMutationDepth) > 0;

    /// <summary>
    /// Gets whether playlist sync progress is visible.
    /// </summary>
    public bool IsPlaylistSyncProgressActive
    {
        get => ProgressHub.IsPlaylistSyncProgressActive;
        private set => ProgressHub.IsPlaylistSyncProgressActive = value;
    }

    /// <summary>
    /// Gets the primary playlist sync progress label.
    /// </summary>
    public string PlaylistSyncProgressLabel
    {
        get => ProgressHub.PlaylistSyncProgressLabel;
        private set => ProgressHub.PlaylistSyncProgressLabel = value;
    }

    /// <summary>
    /// Gets the secondary playlist sync progress label.
    /// </summary>
    public string PlaylistSyncProgressSubLabel
    {
        get => ProgressHub.PlaylistSyncProgressSubLabel;
        private set => ProgressHub.PlaylistSyncProgressSubLabel = value;
    }

    /// <summary>
    /// Gets the current playlist sync progress value.
    /// </summary>
    public double PlaylistSyncProgressValue
    {
        get => ProgressHub.PlaylistSyncProgressValue;
        private set => ProgressHub.PlaylistSyncProgressValue = value;
    }

    /// <summary>
    /// Gets the playlist sync progress maximum.
    /// </summary>
    public double PlaylistSyncProgressMaximum
    {
        get => ProgressHub.PlaylistSyncProgressMaximum;
        private set => ProgressHub.PlaylistSyncProgressMaximum = value;
    }

    /// <summary>
    /// 起動・リロード進捗をステータスバーへ表示中かどうかを返します。
    /// </summary>
    public bool IsStartupProgressActive
    {
        get => ProgressHub.IsStartupProgressActive;
        private set => ProgressHub.IsStartupProgressActive = value;
    }

    /// <summary>
    /// 起動・リロード進捗の主ラベルを返します。
    /// </summary>
    public string StartupProgressLabel
    {
        get => ProgressHub.StartupProgressLabel;
        private set => ProgressHub.StartupProgressLabel = value;
    }

    /// <summary>
    /// 起動・リロード進捗の補助ラベルを返します。
    /// </summary>
    public string StartupProgressSubLabel
    {
        get => ProgressHub.StartupProgressSubLabel;
        private set => ProgressHub.StartupProgressSubLabel = value;
    }

    /// <summary>
    /// 起動・リロード進捗バーの現在値を返します。
    /// </summary>
    public double StartupProgressValue
    {
        get => ProgressHub.StartupProgressValue;
        private set => ProgressHub.StartupProgressValue = value;
    }

    /// <summary>
    /// 起動・リロード進捗バーの最大値を返します。
    /// </summary>
    public double StartupProgressMaximum
    {
        get => ProgressHub.StartupProgressMaximum;
        private set => ProgressHub.StartupProgressMaximum = value;
    }

    /// <summary>
    /// Gets whether LR2 song DB sync status is visible.
    /// </summary>
    public bool IsLr2SongDbSyncStatusActive
    {
        get => ProgressHub.IsLr2SongDbSyncStatusActive;
        private set => ProgressHub.IsLr2SongDbSyncStatusActive = value;
    }

    /// <summary>
    /// Gets the primary LR2 song DB sync status label.
    /// </summary>
    public string Lr2SongDbSyncStatusLabel
    {
        get => ProgressHub.Lr2SongDbSyncStatusLabel;
        private set => ProgressHub.Lr2SongDbSyncStatusLabel = value;
    }

    /// <summary>
    /// Gets the secondary LR2 song DB sync status label.
    /// </summary>
    public string Lr2SongDbSyncStatusSubLabel
    {
        get => ProgressHub.Lr2SongDbSyncStatusSubLabel;
        private set => ProgressHub.Lr2SongDbSyncStatusSubLabel = value;
    }

    /// <summary>
    /// Gets LR2 song DB sync status detail text for tooltips.
    /// </summary>
    public string Lr2SongDbSyncStatusToolTip
    {
        get => ProgressHub.Lr2SongDbSyncStatusToolTip;
        private set => ProgressHub.Lr2SongDbSyncStatusToolTip = value;
    }

    /// <summary>
    /// Gets the current LR2 song DB sync status progress value.
    /// </summary>
    public double Lr2SongDbSyncStatusProgressValue
    {
        get => ProgressHub.Lr2SongDbSyncStatusProgressValue;
        private set => ProgressHub.Lr2SongDbSyncStatusProgressValue = value;
    }

    /// <summary>
    /// Gets the LR2 song DB sync status progress maximum.
    /// </summary>
    public double Lr2SongDbSyncStatusProgressMaximum
    {
        get => ProgressHub.Lr2SongDbSyncStatusProgressMaximum;
        private set => ProgressHub.Lr2SongDbSyncStatusProgressMaximum = value;
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync status progress bar is visible.
    /// </summary>
    public bool IsLr2SongDbSyncStatusProgressVisible
    {
        get => ProgressHub.IsLr2SongDbSyncStatusProgressVisible;
        private set => ProgressHub.IsLr2SongDbSyncStatusProgressVisible = value;
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync retry action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncRetryVisible
    {
        get => ProgressHub.IsLr2SongDbSyncRetryVisible;
        set => ProgressHub.IsLr2SongDbSyncRetryVisible = value;
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync cancel action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncCancelVisible
    {
        get => ProgressHub.IsLr2SongDbSyncCancelVisible;
        set => ProgressHub.IsLr2SongDbSyncCancelVisible = value;
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync cleanup action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncCleanupVisible
    {
        get => ProgressHub.IsLr2SongDbSyncCleanupVisible;
        set => ProgressHub.IsLr2SongDbSyncCleanupVisible = value;
    }
    /// <summary>
    /// Compatibility forwarder for callers that still address the former root filter property.
    /// </summary>
    public ModeFilterType ModeFilter
    {
        get => (ModeFilterType)(int)ChartFilters.ModeFilter;
        set
        {
            ChartFilters.ModeFilter = (ChartModeFilter)(int)value;
            if (value == ModeFilterType.None)
            {
                RaisePropertyChanged("ModeFilter");
            }
        }
    }

    /// <summary>
    /// Compatibility forwarder for callers that still address the former root keyword property.
    /// </summary>
    public string KeywordFilter
    {
        get => ChartFilters.KeywordFilter;
        set => ChartFilters.KeywordFilter = value;
    }

    private void ChartFiltersModeFilterChanged(object sender, EventArgs e)
    {
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        if (filters.ModeFilter == ChartModeFilter.None)
        {
            return;
        }

        RaisePropertyChanged("ModeFilter");
        if (PlaylistWorkspace.TryRequestPlaylistDetailFilter(MainViewUpdateMode.ModeFilterUpdated, filters))
        {
            return;
        }
        RefreshChartRowsView(MainViewUpdateMode.ModeFilterUpdated);
    }

    private void ChartFiltersKeywordFilterChanged(object sender, EventArgs e)
    {
        ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();
        RaisePropertyChanged("KeywordFilter");
        UpdateKeywordSearchPresentation();
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

    public string KeywordSearchWarningText => _KeywordSearchWarningText;

    public bool HasKeywordSearchWarning => !string.IsNullOrWhiteSpace(_KeywordSearchWarningText);

    public string KeywordSearchHelpText => BuildKeywordSearchHelpText(GetCurrentKeywordSearchContext());

    public bool IsKeywordSearchHelpOpen
    {
        get
        {
            return _IsKeywordSearchHelpOpen;
        }
        set
        {
            if (_IsKeywordSearchHelpOpen != value)
            {
                _IsKeywordSearchHelpOpen = value;
                RaisePropertyChanged("IsKeywordSearchHelpOpen");
            }
        }
    }

    /// <summary>
    /// 通常検索欄に表示する field 補完・履歴候補です。
    /// </summary>
    public ObservableCollection<KeywordSearchSuggestionItem> KeywordSearchSuggestions => _KeywordSearchSuggestions;

    /// <summary>
    /// 通常検索欄の候補 popup が開いているかどうかを取得または設定します。
    /// </summary>
    public bool IsKeywordSearchSuggestionPopupOpen
    {
        get
        {
            return _IsKeywordSearchSuggestionPopupOpen;
        }
        set
        {
            if (_IsKeywordSearchSuggestionPopupOpen != value)
            {
                _IsKeywordSearchSuggestionPopupOpen = value;
                RaisePropertyChanged("IsKeywordSearchSuggestionPopupOpen");
            }
        }
    }

    /// <summary>
    /// 通常検索欄の候補 popup 見出しです。
    /// </summary>
    public string KeywordSearchSuggestionHeaderText => _KeywordSearchSuggestionHeaderText;

    internal GridKeywordSearchContext CurrentKeywordSearchContext => GetCurrentKeywordSearchContext();

    /// <summary>
    /// 通常検索欄の補完・履歴候補を更新します。
    /// </summary>
    /// <param name="keywordFilter">検索欄の現在値。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="forceHistory">field 補完が無い時に履歴を表示するか。</param>
    internal void RefreshKeywordSearchSuggestions(string keywordFilter, int caretIndex, bool forceHistory)
    {
        RefreshKeywordSearchSuggestions(
            _KeywordSearchSuggestions,
            GetCurrentKeywordSearchContext(),
            keywordSearchHistory,
            keywordFilter,
            caretIndex,
            forceHistory);
    }

    /// <summary>
    /// 通常検索欄の候補 popup を閉じます。
    /// </summary>
    internal void CloseKeywordSearchSuggestions()
    {
        IsKeywordSearchSuggestionPopupOpen = false;
    }

    /// <summary>
    /// 通常検索欄の検索履歴へ現在値を追加します。
    /// </summary>
    /// <param name="keywordFilter">保存する検索文字列。</param>
    internal void CommitKeywordSearchHistory(string keywordFilter)
    {
        ReplaceKeywordSearchHistory(keywordSearchHistory, KeywordSearchHistoryStore.AddEntry(keywordSearchHistory, keywordFilter));
        keywordSearchHistorySettingsStore.KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(keywordSearchHistory);
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

    private GridKeywordSearchContext GetCurrentKeywordSearchContext()
    {
        if (treeViewFilterTypeSelected == MainViewUpdateMode.PlayHistorySelected)
        {
            return GridKeywordSearchContext.PlayHistory;
        }
        return IsPlaylistViewMode(treeViewFilterTypeSelected)
            ? GridKeywordSearchContext.PlaylistDetail
            : GridKeywordSearchContext.ChartList;
    }

    private void UpdateKeywordSearchPresentation()
    {
        string warningText = BuildKeywordSearchWarningText(ChartFilters.KeywordFilter, GetCurrentKeywordSearchContext());
        if (!string.Equals(_KeywordSearchWarningText, warningText, StringComparison.Ordinal))
        {
            _KeywordSearchWarningText = warningText;
            RaisePropertyChanged("KeywordSearchWarningText");
            RaisePropertyChanged("HasKeywordSearchWarning");
        }
        RaisePropertyChanged("KeywordSearchHelpText");
    }

    private void RefreshKeywordSearchSuggestions(ObservableCollection<KeywordSearchSuggestionItem> targetSuggestions, GridKeywordSearchContext context, IReadOnlyList<string> history, string keywordFilter, int caretIndex, bool forceHistory)
    {
        GridKeywordSearchCompletionResult fieldCompletion = GridKeywordSearchCompletion.CreateFieldCompletion(keywordFilter, caretIndex, context);
        if (fieldCompletion.Items.Count > 0)
        {
            SetKeywordSearchSuggestions(targetSuggestions, fieldCompletion.Items, KeywordSearchSuggestionKind.Field);
            return;
        }
        if (GridKeywordSearchCompletion.IsPlaylistValueCompletionContext(keywordFilter, caretIndex, context))
        {
            IReadOnlyList<string> playlistNameCandidates = context == GridKeywordSearchContext.PlaylistSummary
                ? []
                : PlaylistWorkspace.GetPlaylistKeywordValueCandidates();
            GridKeywordSearchCompletionResult playlistValueCompletion = GridKeywordSearchCompletion.CreatePlaylistValueCompletion(
                keywordFilter,
                caretIndex,
                context,
                playlistNameCandidates);
            if (playlistValueCompletion.Items.Count > 0)
            {
                SetKeywordSearchSuggestions(targetSuggestions, playlistValueCompletion.Items, KeywordSearchSuggestionKind.Value);
                return;
            }
        }
        if (forceHistory)
        {
            IReadOnlyList<KeywordSearchSuggestionItem> historySuggestions = BuildKeywordSearchHistorySuggestions(history, keywordFilter);
            SetKeywordSearchSuggestions(targetSuggestions, historySuggestions, KeywordSearchSuggestionKind.History);
            return;
        }
        SetKeywordSearchSuggestions(targetSuggestions, [], KeywordSearchSuggestionKind.Field);
    }

    private void SetKeywordSearchSuggestions(ObservableCollection<KeywordSearchSuggestionItem> targetSuggestions, IReadOnlyList<KeywordSearchSuggestionItem> suggestions, KeywordSearchSuggestionKind kind)
    {
        targetSuggestions.Clear();
        foreach (KeywordSearchSuggestionItem suggestion in suggestions ?? [])
        {
            targetSuggestions.Add(suggestion);
        }
        string headerText = targetSuggestions.Count == 0 ? string.Empty : BuildKeywordSearchSuggestionHeaderText(kind);
        _KeywordSearchSuggestionHeaderText = headerText;
        RaisePropertyChanged("KeywordSearchSuggestionHeaderText");
        IsKeywordSearchSuggestionPopupOpen = targetSuggestions.Count > 0;
    }

    private static void ReplaceKeywordSearchHistory(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        target.AddRange(source ?? []);
    }

    private void SetNormalLibraryTreeFilter(RegularNormalLibraryTreeFilter filter)
    {
        regularChartListOwner.SetTreeFilter(filter);
        RaisePropertyChanged("FolderFilter");
        RefreshChartRowsView(MainViewUpdateMode.FolderFilterSelected);
    }

    public bool IsWriteLockHeldInitializeBMSFiles
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFiles;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializdBMSFilesHealthStatus
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializdBMSFilesHealthStatus;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFilesEncodingInfo;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesZeroNote
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFilesZeroNote;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldPendingInstallCharts
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldPendingInstallCharts;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldDuplicateChartGroups
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldDuplicateChartGroups;
            }
            return false;
        }
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
        treeViewFilterTypeSelected = ApplicationSettings.StartupSelectInstallPending
            ? MainViewUpdateMode.PendingInstallFolderSelected
            : MainViewUpdateMode.FolderFilterSelected;
        startupSettingsProvider = composition.StartupSettingsProvider;
        customFolderOutputSettingsProvider = composition.CustomFolderOutputSettingsProvider;
        firstStartupProvider = composition.FirstStartupProvider;
        completeFirstStartup = composition.CompleteFirstStartup;
        reloadSettings = composition.ReloadSettings;
        saveSettings = composition.SaveSettings;
        keywordSearchHistorySettingsStore = composition.KeywordSearchHistorySettingsStore;
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
            (reason, work) => QueueStartupBackgroundTask("playlist_library_index_prewarm", reason, null, work),
            () => startupReadyOperableReached,
            () => treeViewFilterTypeSelected,
            WaitForPlaylistReloadCleanupDispatcherIdleAsync,
            () => IsShutdownRequested,
            CollectPlaylistReloadCleanupGarbage,
            LogPlaylistReload,
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message),
            InvokePlaylistSummaryPresentationRefreshGate,
            InvokePlaylistSummaryDataRefreshGate,
            () => TrySuppress(UiRefreshChannel.PlaylistTree),
            reason => TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, reason),
            (reason, work) => QueueStartupBackgroundTask(
                "external_playlist_sync",
                reason,
                "playlist_entries_hydration",
                work),
            (reason, work) => QueueStartupBackgroundTask(
                "playlist_ref_apply",
                reason,
                null,
                work),
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
        PlaylistWorkspace.PlaylistEntriesHydrationRequested += PlaylistWorkspacePlaylistEntriesHydrationRequested;
        PlaylistWorkspace.PlaylistEntriesHydrationCompleted += PlaylistWorkspacePlaylistEntriesHydrationCompleted;
        PlaylistWorkspace.PlaylistExternalSyncQueued += PlaylistWorkspacePlaylistExternalSyncQueued;
        PlaylistWorkspace.PlaylistExternalSyncCompleted += PlaylistWorkspacePlaylistExternalSyncCompleted;
        PlaylistWorkspace.PlaylistExternalSyncReferenceApplyRequested += PlaylistWorkspacePlaylistExternalSyncReferenceApplyRequested;
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
            ReportFolderAutoRenameWorkflowFailure);
        ProgressHub = childComposition.ProgressHub;
        PlaybackPanel = childComposition.PlaybackPanel;
        ChartFilters = childComposition.ChartFilters;
        ChartFilters.ModeFilterChanged += ChartFiltersModeFilterChanged;
        ChartFilters.KeywordFilterChanged += ChartFiltersKeywordFilterChanged;
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
        PlayHistory.ConfigureDisplayTargetPersistence(identity => playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = identity);
        PlayHistory.ConfigureDisplayTargetCatalogRefresh(
            () => IsShutdownRequested,
            PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot,
            SchedulePlayHistoryDisplayTargetCatalogRefresh);
        PlayHistory.PeriodRequestActivated += PlayHistoryPeriodRequestActivated;
        PlayHistory.ConfigureViewRefreshScheduler(
            () => IsShutdownRequested,
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
        MainChartList.SortRequested += MainChartListSortRequested;
        MainChartList.CellEditBeginningRequested += MainChartListCellEditBeginningRequested;
        MainChartList.CellEditStarted += MainChartListCellEditStarted;
        MainChartList.CellEditEndedRequested += MainChartListCellEditEndedRequested;
        regularChartListOwner.SortChanged += RegularChartListOwnerSortChanged;
        regularChartListOwner.SortRefreshRequested += ChartListOwnerSortRefreshRequested;
        regularChartListOwner.FolderEditRequested += RegularChartListOwnerFolderEditRequested;
        regularChartListOwner.InstallDestinationEditRequested += RegularChartListOwnerInstallDestinationEditRequested;
        PlayHistory.SortChanged += PlayHistorySortChanged;
        PlayHistory.SortRefreshRequested += ChartListOwnerSortRefreshRequested;
        ReplaceKeywordSearchHistory(keywordSearchHistory, KeywordSearchHistoryStore.Deserialize(keywordSearchHistorySettingsStore.KeywordSearchHistory));
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

    private void RegularChartListOwnerInstallDestinationEditRequested(
        object sender,
        RegularChartInstallDestinationEditRequestedEventArgs request)
    {
        Task.Run(() =>
        {
            SetPendingInstallDestination(request.Request, request.DestinationDirectory);
            MainChartList.RequestDisplayRefresh();
        }).Logging("regularChartListInstallDestinationEditRequested");
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
            bool wasPlayHistoryViewActive = IsPlayHistoryViewActive;
            SetTreeViewFilterSelection(MainViewUpdateMode.PlaylistFilterSelected, null);
            PlayHistory.ClearSummaryPresentation();
            if (wasPlayHistoryViewActive != IsPlayHistoryViewActive)
            {
                RaisePropertyChanged(() => IsPlayHistoryViewActive);
                RaisePropertyChanged(() => CurrentMainViewOperationSection);
                SyncMainChartListSortPresentation();
            }
            if (request.SummaryModeChanged)
            {
                UpdateKeywordSearchPresentation();
            }
            return;
        }
        PlaylistDetailSelection selection = request.Detail;
        if (request.SummaryModeChanged)
        {
            UpdateKeywordSearchPresentation();
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
        RaisePropertyChanged(nameof(SortParameters));
        if (!IsPlayHistoryViewActive && !IsPlaylistDetailWorkflowActive)
        {
            SyncMainChartListSortPresentation();
        }
    }

    private void PlayHistorySortChanged(object sender, MainChartListSortRequestedEventArgs request)
    {
        RaisePropertyChanged("PlayHistorySortParameters");
        if (IsPlayHistoryViewActive)
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
            UpdateKeywordSearchPresentation();
        }
        PlaylistWorkspace.ClearPlaylistDetailSelection();
        MainViewOperationSection previousOperationSection = CurrentMainViewOperationSection;
        lock (playHistoryViewRequestLock)
        {
            treeViewFilterTypeSelected = MainViewUpdateMode.PlayHistorySelected;
            treeViewFilterParameterSelected = request;
        }
        UpdateKeywordSearchPresentation();
        if (previousOperationSection != CurrentMainViewOperationSection)
        {
            RaisePropertyChanged(() => CurrentMainViewOperationSection);
            RaisePropertyChanged(() => CurrentMainViewChartOperationSourceScope);
            RaisePropertyChanged(() => IsPlayHistoryViewActive);
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
                ? IsPlayHistoryViewActive
                : !IsPlayHistoryViewActive && !IsPlaylistDetailWorkflowActive))
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
        string propertyName = e?.PropertyName;
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        bool installPipelineProperty = propertyName == nameof(OperationProgressHubViewModel.IsInstallPipelineStatusActive)
            || propertyName == nameof(OperationProgressHubViewModel.InstallPipelineLabel)
            || propertyName == nameof(OperationProgressHubViewModel.InstallPipelineSubLabel)
            || propertyName == nameof(OperationProgressHubViewModel.InstallPipelineValue)
            || propertyName == nameof(OperationProgressHubViewModel.InstallPipelineMaximum)
            || propertyName == nameof(OperationProgressHubViewModel.InstallPipelineCanCancel);
        bool maintenanceRescanProperty = propertyName == nameof(OperationProgressHubViewModel.IsMaintenanceRescanProgressActive)
            || propertyName == nameof(OperationProgressHubViewModel.MaintenanceRescanLabel)
            || propertyName == nameof(OperationProgressHubViewModel.MaintenanceRescanSubLabel)
            || propertyName == nameof(OperationProgressHubViewModel.MaintenanceRescanValue)
            || propertyName == nameof(OperationProgressHubViewModel.MaintenanceRescanMaximum)
            || propertyName == nameof(OperationProgressHubViewModel.MaintenanceRescanCanCancel);
        bool folderAutoRenameProperty = propertyName == nameof(OperationProgressHubViewModel.IsFolderAutoRenameProgressActive)
            || propertyName == nameof(OperationProgressHubViewModel.FolderAutoRenameProgressLabel)
            || propertyName == nameof(OperationProgressHubViewModel.FolderAutoRenameProgressSubLabel)
            || propertyName == nameof(OperationProgressHubViewModel.FolderAutoRenameProgressValue)
            || propertyName == nameof(OperationProgressHubViewModel.FolderAutoRenameProgressMaximum);
        if (!installPipelineProperty && !maintenanceRescanProperty && !folderAutoRenameProperty)
        {
            RaisePropertyChanged(propertyName);
        }
        if (propertyName == nameof(IsStartupProgressActive))
        {
            RaisePropertyChanged(nameof(IsLibraryOperationInProgress));
            RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
            RecomputeLr2SongDbSyncStatusPresentation();
        }
    }

    public bool IsShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    internal Task<ShutdownPreparationResult> PrepareShutdownAsync(string reason)
    {
        lock (shutdownPreparationLock)
        {
            if (shutdownPreparationTask == null)
            {
                PlaylistWorkspace.MarkPlaylistLibraryIndexShutdownRequested();
                Interlocked.Exchange(ref shutdownRequested, 1);
                Task regularChartListStopTask = regularChartListOwner.StopAsync();
                shutdownPreparationTask = PrepareShutdownCoreAsync(reason ?? "shutdown", regularChartListStopTask);
            }
            return shutdownPreparationTask;
        }
    }

    private async Task<ShutdownPreparationResult> PrepareShutdownCoreAsync(string reason, Task regularChartListStopTask)
    {
        var stopwatch = Stopwatch.StartNew();
        LogShutdown("prepare_start reason=" + FormatTextForLog(reason));
        int sqliteCloseFailureBaseline = ShutdownOperationTracker.SqliteCloseFailureCount;
        TryShutdownStep("set_ui_blocked", () => SetStartupUiInteractionBlocked(true));
        RequestShutdownCancellation(reason);

        ShutdownPreparationResult result = await CollectShutdownPreparationResultAsync(
            reason,
            stopwatch,
            sqliteCloseFailureBaseline,
            regularChartListStopTask).ConfigureAwait(false);
        LogShutdown("prepare_done " + result.ToLogFields());
        return result;
    }

    private async Task<ShutdownPreparationResult> CollectShutdownPreparationResultAsync(
        string reason,
        Stopwatch stopwatch = null,
        int? sqliteCloseFailureBaseline = null,
        Task regularChartListStopTask = null)
    {
        stopwatch ??= Stopwatch.StartNew();
        sqliteCloseFailureBaseline ??= ShutdownOperationTracker.SqliteCloseFailureCount;
        var waitTracker = new ShutdownWaitTracker();
        await WaitForTaskCompletionAsync(
            "regularChartListWarmup",
            regularChartListStopTask,
            ShutdownDrainWarningThreshold,
            waitTracker,
            () => "running=" + regularChartListOwner.IsVirtualOrderPrewarmRunning).ConfigureAwait(false);
        await (regularChartListStopTask ?? Task.CompletedTask).ConfigureAwait(false);
        await WaitForDropInstallQueueIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForMaintenanceRescanIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForFolderAutoRenameIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistBuildIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistSummaryDataBuildIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForStartupBackgroundTasksIdleAsync(waitTracker).ConfigureAwait(false);
        Task playlistLibraryIndexPrewarmTask = PlaylistWorkspace.GetPlaylistLibraryIndexPrewarmTask();
        await WaitForTaskCompletionAsync(
            "playlistLibraryIndexPrewarm",
            playlistLibraryIndexPrewarmTask,
            ShutdownQueueDrainWarningThreshold,
            waitTracker,
            () => "completed=" + FormatBool(playlistLibraryIndexPrewarmTask == null || playlistLibraryIndexPrewarmTask.IsCompleted)).ConfigureAwait(false);
        await WaitForPlayHistoryRefreshIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForMainOperationIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForLibraryShutdownBlockingWorkAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistShutdownBlockingWorkAsync(waitTracker).ConfigureAwait(false);
        await WaitForDeferredPlaylistWorkersIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistReloadCleanupIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForLr2DbProcessLocksAsync(waitTracker).ConfigureAwait(false);
        await WaitForSqliteConnectionsIdleAsync(waitTracker).ConfigureAwait(false);
        int sqliteCloseFailureCount = Math.Max(0, ShutdownOperationTracker.SqliteCloseFailureCount - sqliteCloseFailureBaseline.Value);
        stopwatch.Stop();
        return new ShutdownPreparationResult(
            reason,
            stopwatch.ElapsedMilliseconds,
            waitTracker.SlowWaitLogged,
            sqliteCloseFailureCount);
    }

    private void RequestShutdownCancellation(string reason)
    {
        TryShutdownStep("library", () => files?.RequestShutdown(reason));
        TryShutdownStep("playlist", () => tables?.RequestShutdown(reason));
        TryShutdownStep("playlist_build", CancelPlaylistBuildRequestsForShutdown);
        TryShutdownStep("playlist_summary", PlaylistWorkspace.StopPlaylistSummaryDataBuild);
        TryShutdownStep("play_history", CancelPlayHistoryRequestsForShutdown);
        TryShutdownStep("playlist_index_prewarm", PlaylistWorkspace.CancelPlaylistLibraryIndexPrewarmForShutdown);
        TryShutdownStep("playlist_reload_cleanup", PlaylistWorkspace.CancelPlaylistReloadCleanupForShutdown);
        TryShutdownStep("maintenance_rescan", () => MaintenanceRescanWorkflow?.RequestShutdown());
        TryShutdownStep("folder_auto_rename", () => FolderAutoRenameWorkflow?.RequestShutdown());
        TryShutdownStep("package_install", () => PackageInstallWorkflow?.RequestShutdown());
        TryShutdownStep("startup_background_queue", () => CancelStartupBackgroundTasksForShutdown(reason));
    }

    private void CancelStartupBackgroundTasksForShutdown(string reason)
    {
        bool shouldStartWorker;
        int originalQueuedCount;
        int discardedCount = 0;
        int drainQueuedCount;
        List<StartupBackgroundTaskRequest> discardedRequests = null;
        lock (startupBackgroundTaskLock)
        {
            originalQueuedCount = startupBackgroundTaskQueue.Count;
            for (int i = startupBackgroundTaskQueue.Count - 1; i >= 0; i--)
            {
                StartupBackgroundTaskRequest request = startupBackgroundTaskQueue[i];
                if (IsStartupBackgroundTaskShutdownDrainRequired(request.Name))
                {
                    continue;
                }
                startupBackgroundTaskQueue.RemoveAt(i);
                RecordStartupBackgroundTaskDiscardedForShutdownUnsafe(request, reason);
                discardedRequests ??= [];
                discardedRequests.Add(request);
                discardedCount++;
            }
            drainQueuedCount = startupBackgroundTaskQueue.Count;
            if (drainQueuedCount > 0)
            {
                startupBackgroundTaskSchedulerStarted = true;
            }
            shouldStartWorker = drainQueuedCount > 0;
            LogShutdown("startup_background_task drain_queued reason=" + FormatTextForLog(reason)
                + " queued=" + originalQueuedCount
                + " drainQueued=" + drainQueuedCount
                + " discarded=" + discardedCount
                + " running=" + startupBackgroundTaskRunningCount);
        }
        if (discardedRequests != null)
        {
            foreach (StartupBackgroundTaskRequest request in discardedRequests)
            {
                CompleteStartupBackgroundTaskDiscardSideEffectsForShutdown(request, reason);
            }
        }
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorkers();
        }
        else
        {
            TryCompleteStartupBackgroundTasksPhaseIfIdle();
        }
    }

    private static bool IsStartupBackgroundTaskShutdownDrainRequired(string name)
    {
        return string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase);
    }

    private void CompleteStartupBackgroundTaskDiscardSideEffectsForShutdown(StartupBackgroundTaskRequest request, string reason)
    {
        if (request == null)
        {
            return;
        }
        if (string.Equals(request.Name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase))
        {
            PlaylistWorkspace.DiscardPlaylistReferenceApplyForShutdown(reason);
            return;
        }
        if (string.Equals(request.Name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase))
        {
            PlaylistWorkspace.DiscardDeferredExternalPlaylistSyncForShutdown(reason);
        }
    }

    private void RecordStartupBackgroundTaskDiscardedForShutdownUnsafe(StartupBackgroundTaskRequest request, string reason)
    {
        if (request == null)
        {
            return;
        }
        startupBackgroundTaskCompletedNames.Add(request.Name);
        StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(request.Name);
        metric.CompletedCount++;
        metric.LastStatus = "discarded";
        metric.LastElapsedMs = 0L;
        metric.LastDetail = "shutdown_requested reason=" + FormatTextForLog(reason);
        if (string.IsNullOrWhiteSpace(metric.Reason))
        {
            metric.Reason = request.Reason ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Dependency))
        {
            metric.Dependency = request.Dependency ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Lane))
        {
            metric.Lane = request.Lane ?? GetStartupBackgroundTaskLane(request.Name);
        }
        LogUiSuppression("startup_background_task discarded name=" + request.Name
            + " version=" + request.Version
            + " reason=" + request.Reason
            + " shutdownReason=" + FormatTextForLog(reason));
    }

    private void CancelPlaylistBuildRequestsForShutdown()
    {
        PlaylistWorkspace.CancelDetailBuilds();
    }

    private void CancelPlayHistoryRequestsForShutdown()
    {
        playHistoryWorkflowOwner.Deactivate();
        playHistoryWorkflowOwner.ClearQueuedRefreshes();
        playHistoryWorkflowOwner.CancelDisplayTargetCatalogRefreshesForShutdown();
    }

    private sealed class ShutdownWaitTracker
    {
        private int slowWaitLogged;

        internal bool SlowWaitLogged => Volatile.Read(ref slowWaitLogged) != 0;

        internal void MarkSlowWaitLogged()
        {
            Interlocked.Exchange(ref slowWaitLogged, 1);
        }
    }

    private async Task WaitForDropInstallQueueIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "dropInstallQueue",
            () => PackageInstallWorkflow == null || PackageInstallWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(PackageInstallWorkflow == null || PackageInstallWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForMaintenanceRescanIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "maintenanceRescan",
            () => MaintenanceRescanWorkflow == null || MaintenanceRescanWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(MaintenanceRescanWorkflow == null || MaintenanceRescanWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForFolderAutoRenameIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "folderAutoRename",
            () => FolderAutoRenameWorkflow == null || FolderAutoRenameWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(FolderAutoRenameWorkflow == null || FolderAutoRenameWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistBuildIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playlistBuild",
            IsPlaylistBuildIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            DescribePlaylistBuildWaitState).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistSummaryDataBuildIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playlistSummaryDataBuild",
            () => PlaylistWorkspace.IsPlaylistSummaryDataBuildIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(PlaylistWorkspace.IsPlaylistSummaryDataBuildIdle)).ConfigureAwait(false);
    }

    private bool IsPlaylistBuildIdle()
    {
        return PlaylistWorkspace.IsDetailBuildIdle;
    }

    private async Task WaitForStartupBackgroundTasksIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "startupBackgroundTasks",
            IsStartupBackgroundTaskSchedulerIdle,
            ShutdownDrainWarningThreshold,
            tracker,
            DescribeStartupBackgroundTaskWaitState).ConfigureAwait(false);
    }

    private bool IsStartupBackgroundTaskSchedulerIdle()
    {
        lock (startupBackgroundTaskLock)
        {
            return startupBackgroundTaskQueue.Count == 0 && startupBackgroundTaskRunningCount == 0;
        }
    }

    private async Task WaitForPlayHistoryRefreshIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playHistoryRefresh",
            IsPlayHistoryRefreshIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            DescribePlayHistoryRefreshWaitState).ConfigureAwait(false);
    }

    private bool IsPlayHistoryRefreshIdle()
    {
        return playHistoryWorkflowOwner.AreRefreshQueuesIdle
            && playHistoryWorkflowOwner.IsDisplayTargetCatalogRefreshIdle;
    }

    private async Task WaitForDeferredPlaylistWorkersIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "deferredPlaylistWorkers",
            IsDeferredPlaylistWorkersIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            DescribeDeferredPlaylistWorkersWaitState).ConfigureAwait(false);
    }

    private bool IsDeferredPlaylistWorkersIdle()
    {
        return PlaylistWorkspace.IsPlaylistReferenceApplyIdle
            && PlaylistWorkspace.IsDeferredExternalPlaylistSyncIdle;
    }

    private async Task WaitForPlaylistReloadCleanupIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playlistReloadCleanup",
            () => PlaylistWorkspace.IsPlaylistReloadCleanupIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            PlaylistWorkspace.DescribePlaylistReloadCleanupWaitState).ConfigureAwait(false);
    }

    private async Task WaitForLibraryShutdownBlockingWorkAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "libraryShutdownWork",
            () => files == null || !files.HasShutdownBlockingWork,
            ShutdownDrainWarningThreshold,
            tracker,
            () => files == null ? "library=null" : files.GetShutdownBlockingWorkLogFields()).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistShutdownBlockingWorkAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playlistShutdownWork",
            () => tables == null || !tables.HasShutdownBlockingWork,
            ShutdownDrainWarningThreshold,
            tracker,
            () => tables == null ? "playlist=null" : tables.GetShutdownBlockingWorkLogFields()).ConfigureAwait(false);
    }

    private static async Task WaitForSqliteConnectionsIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "sqliteConnections",
            () => ShutdownOperationTracker.ActiveSqliteConnectionCount == 0,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "activeSqliteConnectionCount=" + ShutdownOperationTracker.ActiveSqliteConnectionCount).ConfigureAwait(false);
    }

    private static async Task WaitForTaskCompletionAsync(string target, Task task, TimeSpan warningThreshold, ShutdownWaitTracker tracker, Func<string> describeState)
    {
        if (task == null || task.IsCompleted)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        bool warningLogged = false;
        while (!task.IsCompleted)
        {
            await Task.Delay(100).ConfigureAwait(false);
            LogSlowWaitIfNeeded(target, stopwatch, warningThreshold, tracker, ref warningLogged, describeState);
        }
    }

    private static async Task WaitForConditionAsync(string target, Func<bool> isIdle, TimeSpan warningThreshold, ShutdownWaitTracker tracker, Func<string> describeState)
    {
        if (isIdle())
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        bool warningLogged = false;
        while (true)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (isIdle())
            {
                return;
            }
            LogSlowWaitIfNeeded(target, stopwatch, warningThreshold, tracker, ref warningLogged, describeState);
        }
    }

    private static async Task WaitForMainOperationIdleAsync(ShutdownWaitTracker tracker)
    {
        Task waitTask = _semaphore.WaitAsync();
        await WaitForTaskCompletionAsync(
            "mainOperationSemaphore",
            waitTask,
            ShutdownDrainWarningThreshold,
            tracker,
            () => "semaphoreAvailable=false").ConfigureAwait(false);
        await waitTask.ConfigureAwait(false);
        _semaphore.Release();
    }

    private static async Task WaitForLr2DbProcessLocksAsync(ShutdownWaitTracker tracker)
    {
        await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            bool warningLogged = false;
            while (true)
            {
                bool songLockTaken = false;
                bool scoreLockTaken = false;
                try
                {
                    songLockTaken = LR2SongDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                    if (songLockTaken)
                    {
                        scoreLockTaken = LR2ScoreDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                    }
                    if (songLockTaken && scoreLockTaken)
                    {
                        return;
                    }
                }
                finally
                {
                    if (scoreLockTaken)
                    {
                        LR2ScoreDBExtended.Unlock();
                    }
                    if (songLockTaken)
                    {
                        LR2SongDBExtended.Unlock();
                    }
                }
                LogSlowWaitIfNeeded(
                    "lr2DbProcessLocks",
                    stopwatch,
                    ShutdownDrainWarningThreshold,
                    tracker,
                    ref warningLogged,
                    () => "songLockTaken=" + FormatBool(songLockTaken) + " scoreLockTaken=" + FormatBool(scoreLockTaken));
                Thread.Sleep(100);
            }
        }).ConfigureAwait(false);
    }

    private string DescribePlaylistBuildWaitState()
    {
        return PlaylistWorkspace.DescribeDetailBuildState(FormatBool);
    }

    private string DescribeStartupBackgroundTaskWaitState()
    {
        lock (startupBackgroundTaskLock)
        {
            return "queueCount=" + startupBackgroundTaskQueue.Count
                + " runningCount=" + startupBackgroundTaskRunningCount;
        }
    }

    private string DescribePlayHistoryRefreshWaitState()
    {
        return playHistoryWorkflowOwner.DescribeRefreshQueues()
            + " " + playHistoryWorkflowOwner.DescribeDisplayTargetCatalogRefresh();
    }

    private string DescribeDeferredPlaylistWorkersWaitState()
    {
        return PlaylistWorkspace.DescribePlaylistReferenceApplyWaitState()
            + " " + PlaylistWorkspace.DescribeDeferredExternalPlaylistSyncWaitState();
    }

    private static void LogSlowWaitIfNeeded(
        string target,
        Stopwatch stopwatch,
        TimeSpan warningThreshold,
        ShutdownWaitTracker tracker,
        ref bool warningLogged,
        Func<string> describeState)
    {
        if (warningLogged || stopwatch.Elapsed < warningThreshold)
        {
            return;
        }
        warningLogged = true;
        tracker?.MarkSlowWaitLogged();
        string state = string.Empty;
        if (describeState != null)
        {
            try
            {
                state = describeState();
            }
            catch (Exception ex)
            {
                state = "stateFailed=" + ex.GetType().Name;
            }
        }
        LogShutdownWarning("wait_slow target=" + FormatTextForLog(target)
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds
            + (string.IsNullOrWhiteSpace(state) ? string.Empty : " " + state));
    }

    private static void TryShutdownStep(string name, Action action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            LogShutdown((name ?? "step") + "_failed message=" + ex.Message);
        }
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
    public async void ReloadTables()
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
            }).Logging("ReloadTables");
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
                PlaylistWorkspace.CreateReferenceReplaceUpdateCallback(),
                operationToken);
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
    public async void ReloadScoresOnly()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadScoresOnly");
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        bool refreshViews = false;
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView);
            InvalidatePlayHistoryReadCache("score_reload");
            LogInitStage("score_reload_task_start", "ReloadScoresOnly");
            await Task.Run(delegate
            {
                LogInitStage("score_reload_call", "ReloadScoresOnly");
                files.InitializeScoresOnly(null);
            }).Logging("ReloadScoresOnly");
            PublishLatestLr2PlayHistorySchemaCheckResultFromLibrary();
            LogInitStage("score_reload_done", "ReloadScoresOnly");
            refreshViews = true;
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
        }
        if (refreshViews)
        {
            RefreshLibraryMainViewForCurrentFilter();
            PlaylistWorkspace.RequestPlaylistSummaryDataRefresh(
                "score_only_reload");
        }
        SkipUnrequestedStartupProgressPhases(
            "ReloadScoresOnly:scheduled",
            operationToken,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone);
    }

    internal void InvalidatePlayHistoryReadCache(string reason)
    {
        playHistoryWorkflowOwner.Deactivate();
        playHistoryWorkflowOwner.InvalidateReadCache();
        LogPlayHistoryEvent("play_history_read_cache_invalidated", "reason=" + (reason ?? string.Empty));
    }

    public async void ReloadFileDiff()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadFileDiff");
        bool scheduleDeferredPlaylistRef = false;
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
                files.QueueLr2SongDbSync(
                    "ReloadFileDiff",
                    prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync("ReloadFileDiff"));
            }).Logging("ReloadFileDiff");
            LogInitStage("file_diff_reload_done", "ReloadFileDiff");
            scheduleDeferredPlaylistRef = true;
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                bmsParentFolderListViewInitialized = false;
                ScheduleDeferredLibraryFolderTreeRefresh();
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
            LogInitStage("ui_suppress_end_called", "ReloadFileDiff");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            PlaylistWorkspace.QueuePlaylistReferenceApply("ReloadFileDiff", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "ReloadFileDiff");
        }
        SkipUnrequestedStartupProgressPhases(
            "ReloadFileDiff:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.PlaylistEntriesHydrationDone);
    }

    public async void ReinitializeLibrary()
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
            }).Logging("FullReinitialize");
            LogInitStage("files_initialize_done", "FullReinitialize");
            scheduleDeferredPlaylistRef = true;
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                bmsParentFolderListViewInitialized = false;
                ScheduleDeferredLibraryFolderTreeRefresh();
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

    public async void InitializeAsync()
    {
        await InitializeForSettingsAsync();
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

    internal async Task<bool> InitializeForSettingsAsync()
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
        string text = Assembly.GetEntryAssembly().GetName().Version.ToString();
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
            PlaylistWorkspace.RefreshPlaylistTreeTables(tables);
            PlaylistWorkspace.SetDetailDataSource(
                applicationComposition.CreatePlaylistDetailDataSource(files, tables, MainChartList));
            files.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
            files.StartupBackgroundTaskReporter = RecordStartupBackgroundTaskCompleted;
            tables.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
            tables.BmtOutput.ExportProgressReporter = PlaylistWorkspace.ReportPlaylistSyncProgress;
            if (!libraryProfile.OperationModeLR2DB)
            {
                files.SearchTargets.AddRange(libraryProfile.SearchRoots);
            }
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
        listenerForBMSLibrary.RegisterHandler(() => files.DuplicateChartGroups, delegate
        {
            RefreshDuplicatePresentationAfterGroupsChanged("bms_files_duplicated_changed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.DuplicateChartGroupsInvalidationVersion, delegate
        {
            RefreshDuplicatePresentationAfterGroupsChanged("bms_files_duplicated_invalidated");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartPackagesInstalled, delegate
        {
            RebindChartPackagesInstalledCollectionListener();
            HandleChartPackagesInstalledCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartPackagesPending, delegate
        {
            RebindChartPackagesPendingCollectionListener();
            HandleChartPackagesPendingCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.PendingEstimateQueueStatusVersion, delegate
        {
            UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallEstimationProgressVersion, delegate
        {
            UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        });
        RebindChartPackagesInstalledCollectionListener();
        RebindChartPackagesPendingCollectionListener();
        UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        listenerForBMSLibrary.RegisterHandler(() => files.BMSParentFolderListCacheVersion, delegate
        {
            bmsParentFolderListViewInitialized = false;
            if (TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryFolderTree, "parent_folder_cache_changed"))
            {
                return;
            }
            ScheduleDeferredLibraryFolderTreeRefresh();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFiles, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializdBMSFilesHealthStatus, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializdBMSFilesHealthStatus);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFilesEncodingInfo, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesEncodingInfo);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFilesZeroNote, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesZeroNote);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldPendingInstallCharts, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldPendingInstallCharts);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldDuplicateChartGroups, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldDuplicateChartGroups);
        });
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
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        PlaylistWorkspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");
        _semaphore.Release();
        LogInitStage("deferred_playlist_ref_waiting_for_playlist_entries_hydration", "Initialize");
        if (!startupSettings.SkipInitPlaylistLoad)
        {
            PlaylistWorkspace.QueueExternalPlaylistSync(
                "Initialize",
                fromReloadTables: false,
                PlaylistWorkspace.CreateReferenceReplaceUpdateCallback(),
                operationToken);
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
        PlayHistory.QueueDisplayTargetCatalogRefresh();
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
        if (request.HasUpdateCallback)
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
        if (completion.WasSkipped && completion.HasUpdateCallback)
        {
            TryCompleteStartupProgressPlaylistReference(completion.Version);
        }
        TryCompleteStartupProgressExternalSync(completion.Version);
    }

    private void PlaylistWorkspacePlaylistExternalSyncReferenceApplyRequested(
        object sender,
        PlaylistExternalSyncReferenceApplyRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        PlaylistWorkspace.QueuePlaylistReferenceApply(request.Reason, request.OperationToken);
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

    public void CloseProcess()
    {
        if (!IsShutdownRequested)
        {
            PlaylistWorkspace.MarkPlaylistLibraryIndexShutdownRequested();
            Interlocked.Exchange(ref shutdownRequested, 1);
            RequestShutdownCancellation("CloseProcess");
        }
        try
        {
            saveSettings();
        }
        catch (Exception ex)
        {
            LogShutdown("settings_save_failed message=" + ex.Message);
        }
        try
        {
            PlaybackPanel.CloseProcess();
        }
        catch (Exception ex)
        {
            LogShutdown("player_close_failed message=" + ex.Message);
        }

        WaitForFinalLr2DbProcessLocks();
        try
        {
            TempDirectoryPublisher.RemoveAll((path, ex) => LogShutdown("temp_remove_failed path=" + path + " message=" + ex.Message));
        }
        catch (Exception ex)
        {
            LogShutdown("temp_remove_failed message=" + ex.Message);
        }
    }

    internal void SaveSettingsForShutdown()
    {
        saveSettings();
    }

    private static void WaitForFinalLr2DbProcessLocks()
    {
        var stopwatch = Stopwatch.StartNew();
        var tracker = new ShutdownWaitTracker();
        bool warningLogged = false;
        while (true)
        {
            bool songLockTaken = false;
            bool scoreLockTaken = false;
            try
            {
                songLockTaken = LR2SongDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                if (songLockTaken)
                {
                    scoreLockTaken = LR2ScoreDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                }
                if (songLockTaken && scoreLockTaken)
                {
                    return;
                }
            }
            finally
            {
                if (scoreLockTaken)
                {
                    LR2ScoreDBExtended.Unlock();
                }
                if (songLockTaken)
                {
                    LR2SongDBExtended.Unlock();
                }
            }
            LogSlowWaitIfNeeded(
                "lr2DbProcessLocksFinal",
                stopwatch,
                ShutdownDrainWarningThreshold,
                tracker,
                ref warningLogged,
                () => "songLockTaken=" + FormatBool(songLockTaken) + " scoreLockTaken=" + FormatBool(scoreLockTaken));
            Thread.Sleep(100);
        }
    }

    private static List<ChartFile> GetBmsFormatCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private static List<LibraryChartRef> GetBmsLibraryChartRefs(IEnumerable<LibraryChartRef> charts)
    {
        return [.. (charts ?? []).Where(chart => chart?.Kind == LibraryChartKind.Bms)];
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
            && expectedSortTarget.Value != (IsPlayHistoryViewActive
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
            UpdateKeywordSearchPresentation();
        }
        if (previousOperationSection != CurrentMainViewOperationSection)
        {
            RaisePropertyChanged(() => CurrentMainViewOperationSection);
            RaisePropertyChanged(() => CurrentMainViewChartOperationSourceScope);
            RaisePropertyChanged(() => IsPlayHistoryViewActive);
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
            RaisePropertyChanged(nameof(SortParameters));
            if (!IsPlayHistoryViewActive)
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

    public void ExecFolderFilter(FolderFilterType type, string filterKey = null)
    {
        if (PlaylistWorkspace.SetPlaylistSummaryMode(enabled: false))
        {
            UpdateKeywordSearchPresentation();
        }
        if (!string.IsNullOrWhiteSpace(filterKey))
        {
            switch (type)
            {
                case FolderFilterType.FilterNone:
                    break;
                default:
                    return;
                case FolderFilterType.DirectoryFilter:
                    SetNormalLibraryTreeFilter(RegularNormalLibraryTreeFilter.Create(RegularChartFolderFilterKind.Directory, filterKey));
                    return;
                case FolderFilterType.ArtistFilter:
                    SetNormalLibraryTreeFilter(RegularNormalLibraryTreeFilter.Create(RegularChartFolderFilterKind.Artist, filterKey));
                    return;
            }
        }
        SetNormalLibraryTreeFilter(null);
    }

    public void ExecMaintenanceFilter(MaintenanceFilterType type, object parameter = null)
    {
        if (PlaylistWorkspace.SetPlaylistSummaryMode(enabled: false))
        {
            UpdateKeywordSearchPresentation();
        }
        if (files != null)
        {
            if (type == MaintenanceFilterType.DuplicateFilter)
            {
                EnsureDuplicateChartGroupsReady("exec_maintenance_filter");
            }
            if (Enum.IsDefined(typeof(MainViewUpdateMode), (int)type))
            {
                RefreshChartRowsView((MainViewUpdateMode)type, parameter);
            }
        }
    }

    private bool EnsureDuplicateChartGroupsReady(string reason)
    {
        if (files == null)
        {
            return false;
        }
        if (files.DuplicateChartGroups != null)
        {
            return true;
        }

        int version = files.DuplicateChartGroupsInvalidationVersion;
        var waitStopwatch = Stopwatch.StartNew();
        bool waited = false;
        lock (duplicateChartGroupsRefreshLock)
        {
            while (duplicateChartGroupsRefreshRunning)
            {
                waited = true;
                Monitor.Wait(duplicateChartGroupsRefreshLock);
                if (files.DuplicateChartGroups != null)
                {
                    LogDuplicateRefreshCoalesce(reason, version, "joined", waitStopwatch.ElapsedMilliseconds);
                    return true;
                }
                version = files.DuplicateChartGroupsInvalidationVersion;
            }

            if (files.DuplicateChartGroups != null)
            {
                if (waited)
                {
                    LogDuplicateRefreshCoalesce(reason, version, "joined", waitStopwatch.ElapsedMilliseconds);
                }
                return true;
            }

            duplicateChartGroupsRefreshRunning = true;
        }

        int searchVersion = version;
        var searchStopwatch = Stopwatch.StartNew();
        try
        {
            files.SearchDuplicateChartGroups();
            return files.DuplicateChartGroups != null;
        }
        finally
        {
            searchStopwatch.Stop();
            lock (duplicateChartGroupsRefreshLock)
            {
                duplicateChartGroupsRefreshRunning = false;
                Monitor.PulseAll(duplicateChartGroupsRefreshLock);
            }
            LogDuplicateRefreshCoalesce(reason, searchVersion, "searched", searchStopwatch.ElapsedMilliseconds);
        }
    }

    private static void LogDuplicateRefreshCoalesce(string reason, int version, string action, long elapsedMs)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        installPerformanceLogger.Info("duplicate_refresh_coalesce action=" + action
            + " reason=" + reason
            + " version=" + version
            + " elapsedMs=" + elapsedMs);
    }

    public void RemoveChartInfoParseFailuresByMd5(IEnumerable<string> md5s)
    {
        files?.RemoveChartInfoParseFailuresByMd5(NormalizeChartInfoParseFailureMd5s(md5s));
    }

    internal static string[] NormalizeChartInfoParseFailureMd5s(IEnumerable<string> md5s)
    {
        return BMSLibrary.NormalizeChartInfoParseFailureMd5s(md5s);
    }

    public void ExecInstallFilter(InstallFilterType type, object parameter = null)
    {
        if (PlaylistWorkspace.SetPlaylistSummaryMode(enabled: false))
        {
            UpdateKeywordSearchPresentation();
        }
        switch (type)
        {
            case InstallFilterType.NewlyInstalledFilter:
                RefreshChartRowsView(MainViewUpdateMode.NewlyInstalledFolderSelected, parameter);
                break;
            case InstallFilterType.PendingInstallFilter:
                RefreshChartRowsView(MainViewUpdateMode.PendingInstallFolderSelected, parameter);
                break;
        }
    }

    internal void FixEncodingBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string encoding = "")
    {
        files.SetBMSFilesEncoding(bmsFiles, encoding);
        InvalidateNormalLibraryIdentitySortKeys(NormalLibraryBmsTitleChangedReason);
    }

    private void ForceResourceHealthCheckCharts(IEnumerable<ChartFile> charts)
    {
        MaintenanceWorkflowResult result = files.RescanResourceHealthCharts(charts);
        if (result?.Canceled == true)
        {
            ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Warn_Lr2SongDbSyncRunning,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Resource health rescan blocked notification");
            return;
        }
        RefreshResourceHealthViewsAfterMaintenanceChanged(
            "maintenance_hydration_completed",
            "maintenance_changed",
            invalidateSortDependency: false);
    }

    internal void ForceResourceHealthCheckCharts(ChartResourceHealthRequest request)
    {
        if (request?.HasTargets != true)
        {
            return;
        }
        ForceResourceHealthCheckCharts(request.Charts);
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
            RaisePropertyChanged(() => DuplicateChartGroups);
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
            if (files.DuplicateChartGroups == null)
            {
                EnsureDuplicateChartGroupsReady(refreshReason);
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

    private void SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset = false)
    {
        files.SetChartResourceWarningsIgnored(charts, unset);
    }

    internal void SetChartResourceWarningsIgnored(ChartResourceHealthRequest request, bool unset = false)
    {
        if (request?.HasTargets != true)
        {
            return;
        }
        SetChartResourceWarningsIgnored(request.Charts, unset);
    }

    /// <summary>
    /// リンク切れ等の問題がある ChartPackage (インストーラーまたはアーカイブ単位) について、正しいインストール先のディレクトリをヒューリスティックに探索します。
    /// 探索結果は内部の BMSLibrary に対して適用されます。
    /// </summary>
    /// <param name="packages">探索・復旧対象となるBMSパッケージのコレクション。</param>
    public void SearchInstallDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        RunChartPackageMutation(delegate
        {
            files.SearchEstimatedInstallationDirectory(packages);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    public void SearchMergeDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        RunChartPackageMutation(delegate
        {
            for (int num = 0; num < list.Count; num++)
            {
                files.SearchMergeDestinationForPendingPackage(list[num]);
            }
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    internal void SearchInstallDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        if (!targets.HasTargets)
        {
            return;
        }
        SearchInstallDestinationForPendingCharts(targets.PackageTargets, targets.LooseEntries);
    }

    internal void SearchMergeDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        if (!targets.HasTargets)
        {
            return;
        }
        SearchMergeDestinationForPendingCharts(targets.PackageTargets, targets.LooseEntries);
    }

    internal void SearchPendingInstallDestination(PendingInstallDestinationSearchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return;
        }

        switch (request.Kind)
        {
            case PendingInstallDestinationSearchKind.InstallDestination:
                SearchInstallDestinationForPendingCharts(request.PackageTargets, request.LooseEntries);
                return;
            case PendingInstallDestinationSearchKind.MergeDestination:
                SearchMergeDestinationForPendingCharts(request.PackageTargets, request.LooseEntries);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported pending install destination search operation.");
        }
    }

    private void SearchInstallDestinationForPendingCharts(IReadOnlyList<ChartOperationTarget> packageTargets, IReadOnlyList<PackageChartEntry> looseEntries)
    {
        RunChartPackageMutation(delegate
        {
            List<ChartOperationTarget> mutablePackageTargets = packageTargets.ToList();
            List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref mutablePackageTargets);
            if (packages.Count > 0)
            {
                files.SearchEstimatedInstallationDirectory(packages);
            }
            if (looseEntries.Count > 0)
            {
                files.SearchEstimatedInstallationDirectoryForLooseCharts(looseEntries);
                MainChartList.RowProjection.UpdateTransientStates(looseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    private void SearchMergeDestinationForPendingCharts(IReadOnlyList<ChartOperationTarget> packageTargets, IReadOnlyList<PackageChartEntry> looseEntries)
    {
        RunChartPackageMutation(delegate
        {
            List<ChartOperationTarget> mutablePackageTargets = packageTargets.ToList();
            List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref mutablePackageTargets);
            for (int num = 0; num < packages.Count; num++)
            {
                files.SearchMergeDestinationForPendingPackage(packages[num]);
            }
            if (looseEntries.Count > 0)
            {
                files.SearchMergeDestinationForPendingCharts(looseEntries);
                MainChartList.RowProjection.UpdateTransientStates(looseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
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
            bool isActive = snapshot != null && snapshot.IsActive;
            IsPlaylistSyncProgressActive = isActive;
            if (!isActive)
            {
                PlaylistSyncProgressLabel = string.Empty;
                PlaylistSyncProgressSubLabel = string.Empty;
                PlaylistSyncProgressValue = 0.0;
                PlaylistSyncProgressMaximum = 0.0;
                return;
            }
            int total = Math.Max(snapshot.TotalTableCount, 1);
            int completed = Math.Max(0, Math.Min(snapshot.CompletedTableCount, total));
            PlaylistSyncProgressMaximum = total;
            PlaylistSyncProgressValue = completed;
            string labelFormat = !string.IsNullOrWhiteSpace(snapshot.LabelFormat) ? snapshot.LabelFormat : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_label_format;
            string singleLabel = !string.IsNullOrWhiteSpace(snapshot.SingleLabel) ? snapshot.SingleLabel : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_single_label;
            PlaylistSyncProgressLabel = (snapshot.TotalTableCount > 0) ? string.Format(labelFormat, completed, total) : singleLabel;
            PlaylistSyncProgressSubLabel = !string.IsNullOrWhiteSpace(snapshot.CurrentTableName) ? snapshot.CurrentTableName : (snapshot.CurrentUri?.ToString() ?? string.Empty);
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
        lock (startupBackgroundTaskLock)
        {
            startupBackgroundTaskQueue.Clear();
            startupBackgroundTaskCompletedNames.Clear();
            startupBackgroundTaskRunningCountByLane.Clear();
            startupBackgroundTaskMetrics.Clear();
            startupBackgroundTaskRunningCount = 0;
            startupBackgroundTaskSchedulerStarted = ShouldStartStartupBackgroundTaskSchedulerAfterReset(operationKind, startupReadyOperableReached);
        }
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask = UiRefreshChannel.None;
        }
        startupInitializationCompleteStopwatch = null;
        startupInitializationCompleteLogged = false;
        startupInitializationCompleteRetryQueued = false;
    }

    private static bool ShouldStartStartupBackgroundTaskSchedulerAfterReset(StartupProgressOperationKind operationKind, bool operableReached)
    {
        return operationKind != StartupProgressOperationKind.Startup && operableReached;
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
        latestLr2SongDbSyncStatus = Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.Now);
        RecomputeLr2SongDbSyncStatusPresentation();
    }

    private void RecomputeLr2SongDbSyncStatusPresentation()
    {
        Lr2SongDbSyncRuntimeStatus status = latestLr2SongDbSyncStatus ?? Lr2SongDbSyncStatusMapper.CreateNone();
        bool isActive = ShouldShowLr2SongDbSyncStatus(status.HasWarningStatus, IsStartupProgressBlockingLr2SongDbSyncStatus());
        IsLr2SongDbSyncStatusActive = isActive;
        Lr2SongDbSyncStatusLabel = isActive ? status.StatusText : string.Empty;
        Lr2SongDbSyncStatusSubLabel = isActive ? status.ProgressText : string.Empty;
        Lr2SongDbSyncStatusToolTip = isActive ? status.Detail : string.Empty;
        Lr2SongDbSyncStatusProgressValue = isActive ? status.ProgressValue : 0.0;
        Lr2SongDbSyncStatusProgressMaximum = isActive ? status.ProgressMaximum : 1.0;
        IsLr2SongDbSyncStatusProgressVisible = isActive && status.HasProgress;
        IsLr2SongDbSyncRetryVisible = isActive && status.CanRetry;
        IsLr2SongDbSyncCancelVisible = isActive && status.CanCancel;
        IsLr2SongDbSyncCleanupVisible = isActive && status.CanCleanupStartupScanBlockers;
    }

    private bool IsStartupProgressBlockingLr2SongDbSyncStatus()
    {
        lock (startupProgressLock)
        {
            return IsStartupProgressBlockingLr2SongDbSyncStatus(
                startupProgressState.IsActive,
                startupProgressState.IsFailed,
                CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.Lr2SongDbSyncDone));
        }
    }

    private static bool IsStartupProgressBlockingLr2SongDbSyncStatus(bool startupProgressActive, bool startupProgressFailed, bool startupProgressTracksLr2SongDbSync)
    {
        return startupProgressActive && !startupProgressFailed && startupProgressTracksLr2SongDbSync;
    }

    private static bool ShouldShowLr2SongDbSyncStatus(bool hasWarningStatus, bool startupProgressBlocksLr2Status)
    {
        return hasWarningStatus && !startupProgressBlocksLr2Status;
    }

    internal static bool ShouldShowLr2SongDbSyncStatusForTest(bool hasWarningStatus, bool startupProgressActive, bool startupProgressFailed, bool startupProgressTracksLr2SongDbSync = true)
    {
        return ShouldShowLr2SongDbSyncStatus(
            hasWarningStatus,
            IsStartupProgressBlockingLr2SongDbSyncStatus(startupProgressActive, startupProgressFailed, startupProgressTracksLr2SongDbSync));
    }

    public void RetryLr2SongDbSync()
    {
        RequestLr2SongDbSync("status_bar_retry", force: false);
    }

    public void RequestLr2SongDbSync(string reason, bool force)
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return;
        }
        Task.Run(delegate
        {
            files?.QueueLr2SongDbSync(
                reason,
                force,
                () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(reason));
        }).Logging("RequestLr2SongDbSync");
    }

    public async Task RequestLr2SongDbSyncAsync(string reason, bool force)
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return;
        }

        await Task.Run(async delegate
        {
            files?.QueueLr2SongDbSync(
                reason,
                force,
                () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(reason));
            await Task.CompletedTask.ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public void SyncLr2SongDbSyncFolderDataAfterSettingsChange(string reason)
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return;
        }

        Task.Run(delegate
        {
            files?.TryRunLr2SongDbSyncDataPreparation(
                reason,
                () =>
                {
                    Lr2SongDbSyncPreparedDataSurface playlistSurface = ReOutputAllCustomFoldersForLr2GeneratedDataSync(reason);
                    Lr2SongDbSyncPreparedDataSurface builtinSurface =
                        files?.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows(reason) ?? Lr2SongDbSyncPreparedDataSurface.Empty;
                    return Lr2SongDbSyncPreparedDataSurface.Merge(playlistSurface, builtinSurface);
                },
                () => files?.QueueLr2SongDbSync(reason, force: false, allowIncompleteToQueue: false));
        }).Logging("SyncLr2SongDbSyncFolderDataAfterSettingsChange");
    }

    public void SyncExternalLr2FolderRowsAfterCustomFolderOutputBaseSettingsChange(string reason)
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return;
        }

        Task.Run(delegate
        {
            files?.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange(reason);
        }).Logging("SyncExternalLr2FolderRowsAfterCustomFolderOutputBaseSettingsChange");
    }

    private Lr2SongDbSyncPreparedDataSurface ReOutputAllCustomFoldersForLr2GeneratedDataSync(string reason)
    {
        return tables?.ReOutputAllCustomFoldersForLr2SongDbSync(
            reason,
            (processed, total, tableName) => files?.PublishLr2SongDbSyncExternalStageProgress(
                "playlist_materialization",
                processed,
                total,
                tableName))
            ?? Lr2SongDbSyncPreparedDataSurface.Empty;
    }

    public void CancelLr2SongDbSync()
    {
        files?.CancelLr2SongDbSync("status_bar_cancel");
    }

    public void CleanupLr2SongDbSyncStartupScanBlockersAndRetry()
    {
        if (files == null)
        {
            return;
        }

        if (!ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_song_db_sync_startup_scan_blocker_cleanup,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            MessageBoxButton.OKCancel,
            "LR2 song DB sync startup blocker cleanup confirmation"))
        {
            return;
        }

        try
        {
            files.CleanupLr2SongDbSyncStartupScanBlockerFolderRows("status_bar_cleanup");
            Task.Run(delegate
            {
                files.QueueLr2SongDbSync(
                    "status_bar_cleanup_retry",
                    prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync("status_bar_cleanup_retry"));
            }).Logging("Lr2SongDbSyncStartupScanBlockerCleanupRetry");
        }
        catch (Exception ex)
        {
            ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand);
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
            IsStartupProgressActive = isActive;
            StartupProgressLabel = label;
            StartupProgressSubLabel = subLabel;
            StartupProgressValue = value;
            StartupProgressMaximum = maximum;
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
        lock (startupBackgroundTaskLock)
        {
            if (startupInitializationCompleteLogged || startupInitializationCompleteStopwatch == null)
            {
                return;
            }
            bool schedulerIdle = startupBackgroundTaskQueue.Count == 0 && startupBackgroundTaskRunningCount == 0;
            if (!schedulerIdle)
            {
                QueueStartupInitializationCompleteRetryUnsafe();
                return;
            }
            startupInitializationCompleteLogged = true;
            elapsedMs = startupInitializationCompleteStopwatch.ElapsedMilliseconds;
        }
        LogUiSuppression("startup_initialization_complete elapsedMs=" + elapsedMs);
        LogUiSuppression(BuildStartupBackgroundSummaryLog(elapsedMs));
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
            lock (startupBackgroundTaskLock)
            {
                startupInitializationCompleteRetryQueued = false;
            }
            TryLogStartupInitializationComplete();
        });
    }

    private string BuildStartupBackgroundSummaryLog(long elapsedMs)
    {
        List<StartupBackgroundTaskMetric> metrics;
        lock (startupBackgroundTaskLock)
        {
            metrics = [.. startupBackgroundTaskMetrics.Values
                .OrderBy(metric => metric.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneStartupBackgroundTaskMetric)];
        }
        long queued = metrics.Sum(metric => metric.QueuedCount);
        long started = metrics.Sum(metric => metric.StartedCount);
        long completed = metrics.Sum(metric => metric.CompletedCount);
        long failed = metrics.Sum(metric => metric.FailedCount);
        string taskSummary = metrics.Count == 0
            ? "(none)"
            : string.Join(";", metrics.Select(FormatStartupBackgroundTaskMetric));
        return "startup_background_summary elapsedMs=" + elapsedMs
            + " queued=" + queued
            + " started=" + started
            + " completed=" + completed
            + " failed=" + failed
            + " tasks=" + taskSummary;
    }

    private static StartupBackgroundTaskMetric CloneStartupBackgroundTaskMetric(StartupBackgroundTaskMetric metric)
    {
        return new StartupBackgroundTaskMetric
        {
            Name = metric.Name,
            Reason = metric.Reason,
            Dependency = metric.Dependency,
            Lane = metric.Lane,
            QueuedCount = metric.QueuedCount,
            StartedCount = metric.StartedCount,
            CompletedCount = metric.CompletedCount,
            FailedCount = metric.FailedCount,
            TotalElapsedMs = metric.TotalElapsedMs,
            LastElapsedMs = metric.LastElapsedMs,
            LastStatus = metric.LastStatus,
            LastDetail = metric.LastDetail
        };
    }

    private static string FormatStartupBackgroundTaskMetric(StartupBackgroundTaskMetric metric)
    {
        return SanitizeStartupBackgroundSummaryValue(metric.Name)
            + "{queued=" + metric.QueuedCount
            + ",started=" + metric.StartedCount
            + ",completed=" + metric.CompletedCount
            + ",failed=" + metric.FailedCount
            + ",lastStatus=" + SanitizeStartupBackgroundSummaryValue(metric.LastStatus)
            + ",lastMs=" + metric.LastElapsedMs
            + ",totalMs=" + metric.TotalElapsedMs
            + ",reason=" + SanitizeStartupBackgroundSummaryValue(metric.Reason)
            + ",dependency=" + SanitizeStartupBackgroundSummaryValue(metric.Dependency)
            + ",lane=" + SanitizeStartupBackgroundSummaryValue(metric.Lane)
            + ",detail=" + SanitizeStartupBackgroundSummaryValue(metric.LastDetail)
            + "}";
    }

    private static string SanitizeStartupBackgroundSummaryValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }
        return value
            .Replace(Environment.NewLine, " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("{", "(")
            .Replace("}", ")")
            .Replace(" ", "_");
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

    internal static bool ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest(string operationKindName, bool operableReached)
    {
        return ShouldStartStartupBackgroundTaskSchedulerAfterReset(ParseStartupProgressOperationKindForTest(operationKindName), operableReached);
    }

    /// <summary>
    /// 起動後バックグラウンドタスクの lane 割り当てをテストから検証します。
    /// </summary>
    /// <param name="taskName">検証対象のタスク名。</param>
    /// <returns>タスクに割り当てられる scheduler lane。</returns>
    internal static string GetStartupBackgroundTaskLaneForTest(string taskName)
    {
        return GetStartupBackgroundTaskLane(taskName);
    }

    /// <summary>
    /// 起動後バックグラウンドタスクの lane 別同時実行数をテストから検証します。
    /// </summary>
    /// <param name="lane">検証対象の scheduler lane。</param>
    /// <returns>指定 lane の同時実行上限。</returns>
    internal static int GetStartupBackgroundTaskLaneConcurrencyForTest(string lane)
    {
        return GetStartupBackgroundTaskLaneConcurrency(lane);
    }

    /// <summary>
    /// 起動後バックグラウンドタスク全体の同時実行数をテストから検証します。
    /// </summary>
    /// <returns>全体の同時実行上限。</returns>
    internal static int GetStartupBackgroundTaskTotalConcurrencyForTest()
    {
        return GetStartupBackgroundTaskTotalConcurrency();
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

    private List<ChartPackage> ExtractChartPackagesFromChartEntries(ref List<PackageChartEntry> entries, bool isInstalled = false)
    {
        DispatcherCollection<ChartPackage> source = (isInstalled ? ChartPackagesInstalled : ChartPackagesPending);
        List<PackageChartEntry> remainingEntries = [];
        List<ChartPackage> packages = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartPackage chartPackage = source?.FirstOrDefault(p => ContainsChartTarget(p, entry));
            if (chartPackage == null)
            {
                remainingEntries.Add(entry);
            }
            else
            {
                packages.Add(chartPackage);
            }
        }
        entries = remainingEntries;
        return [.. packages.Distinct()];
    }

    private List<ChartPackage> ExtractChartPackagesFromChartTargets(ref List<ChartOperationTarget> targets, bool isInstalled = false)
    {
        DispatcherCollection<ChartPackage> source = (isInstalled ? ChartPackagesInstalled : ChartPackagesPending);
        List<ChartOperationTarget> remainingTargets = [];
        List<ChartPackage> packages = [];
        foreach (ChartOperationTarget target in targets)
        {
            ChartPackage chartPackage = source?.FirstOrDefault(p => ContainsChartTarget(p, target));
            if (chartPackage == null)
            {
                remainingTargets.Add(target);
            }
            else
            {
                packages.Add(chartPackage);
            }
        }
        targets = remainingTargets;
        return [.. packages.Distinct()];
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, PackageChartEntry targetEntry)
    {
        ChartFile chart = targetEntry?.Chart;
        if (chartPackage == null || chart == null)
        {
            return false;
        }
        return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(targetEntry) == true);
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, ChartOperationTarget target)
    {
        ChartFile chart = target?.Chart;
        if (chartPackage == null || chart == null)
        {
            return false;
        }
        if (target.PackageEntry != null)
        {
            return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.PackageEntry) == true);
        }
        return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(chart) == true);
    }

    public void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        HashSet<ChartPackage> approvedNormalInstallOverridePackages = [];
        foreach (ChartPackage package in list.Where(pkg => (pkg.ChartEntries ?? []).Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestination))))
        {
            bool approved = ShowUiConfirmation(
                BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride,
                BeMusicSeeker.Properties.Resources.Confirm_NormalInstallTitle,
                MessageBoxImage.Question,
                MessageBoxButton.YesNo,
                "Pending package normal install override confirmation",
                MessageBoxResult.Yes);
            if (approved)
            {
                approvedNormalInstallOverridePackages.Add(package);
            }
        }
        RunPendingInstallMutation(delegate
        {
            files.ForceInstallPendingPackages(list, approveNormalInstallOverride: false, approvedNormalInstallOverridePackages: approvedNormalInstallOverridePackages);
        }, CreatePackagePlaybackTargetSnapshot(list), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    private void ForceInstallPendingCharts(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
        ForceInstallPendingPackages(chartPackages);
    }

    public void ManualInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        RunPendingInstallMutation(delegate
        {
            files.InstallPendingPackagesToEstimatedDestinations(list);
        }, CreatePackagePlaybackTargetSnapshot(list), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    private void ManualInstallPendingCharts(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
        ManualInstallPendingPackages(chartPackages);
    }

    internal void InstallPendingCharts(PendingInstallPackageOperationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        switch (request.Kind)
        {
            case PendingInstallPackageOperationKind.ForceInstall:
                ForceInstallPendingCharts(request.Targets);
                return;
            case PendingInstallPackageOperationKind.ManualInstall:
                ManualInstallPendingCharts(request.Targets);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported pending install package operation.");
        }
    }

    public void RemovePendingPackagesAll()
    {
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingPackagesAll();
        });
    }

    public void RemovePendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingPackages(packages);
        });
    }

    internal void RemovePendingPackages(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
        RemovePendingPackages(chartPackages);
    }

    internal void DeleteInstallPackageRecords(DeleteInstallPackageRecordsRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.IsPending)
        {
            RemovePendingPackages(request.Targets);
            return;
        }

        RemoveInstalledPackageRecords(request.Targets);
    }

    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        if (files == null)
        {
            return [];
        }
        using (chartFileOperations.Enter())
        {
            return files.GetPendingPackagesContainingOnlyInstalledCharts();
        }
    }

    internal List<ChartFile> GetPendingBmsFormatChartFilesSnapshot()
    {
        if (files == null)
        {
            return [];
        }
        using (chartFileOperations.Enter())
        {
            return files.GetPendingBmsFormatChartFilesSnapshot();
        }
    }

    public void DeletePendingPackageSources(IEnumerable<ChartPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        RunPendingInstallMutation(delegate
        {
            files.DeletePendingPackageSources(packages, sendToRecycleBin, token, onEachProcessed);
        });
    }

    internal void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(IEnumerable<ChartFile> targetCharts, CancellationToken token = default, Action onEachProcessed = null)
    {
        List<ChartFile> list = GetBmsFormatCharts((targetCharts != null) ? [.. targetCharts.Where(chart => chart != null)] : GetPendingBmsFormatChartFilesSnapshot());
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, token, onEachProcessed);
        }, list);
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<ChartPackage> packages, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        List<ChartFile> playbackTargetCharts = CreatePackagePlaybackTargetSnapshot(list);
        return RunPendingInstallMutation(() => files.OverwritePendingInstalledOnlyPackagesResources(list, token, onEachProcessed), playbackTargetCharts);
    }

    public void RemoveInstalledPackageRecordsAll()
    {
        RunChartPackageMutation(delegate
        {
            files.RemoveInstalledPackageRecordsAll();
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
    }

    public void RemoveInstalledPackageRecords(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        RunChartPackageMutation(delegate
        {
            files.RemoveInstalledPackageRecords(packageList);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
    }

    internal void RemoveInstalledPackageRecords(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets, isInstalled: true);
        RemoveInstalledPackageRecords(chartPackages);
    }

    private void SearchCorrectInstallationDirectoryCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            return;
        }
        RunChartPackageMutation(delegate
        {
            files.SearchCorrectInstallationDirectoryCharts(entries);
            MainChartList.RowProjection.UpdateTransientStates(entries.Select(entry => entry.Chart), forceInstallDestinationProjection: true);
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
    }

    internal void SearchCorrectInstallationDirectoryCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        SearchCorrectInstallationDirectoryCharts(snapshot.RepairEntries);
    }

    internal void SearchCorrectInstallationDirectoryCharts(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return;
        }
        SearchCorrectInstallationDirectoryCharts(request.RepairEntries);
    }

    public void ClearInstallDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> list = [.. packages.Where(f => f != null)];
        List<PackageChartEntry> changedEntries = [.. list.SelectMany(package => package.ChartEntries ?? []).Where(entry => entry?.Chart != null)];
        RunChartPackageMutation(delegate
        {
            for (int num = 0; num < list.Count; num++)
            {
                ClearChartPackageInstallDestinations(list[num]);
            }
            MainChartList.RowProjection.UpdateTransientStates(changedEntries.Select(entry => entry.Chart), forceInstallDestinationProjection: true);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree, requiresLibrary: false);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
    }

    internal void ClearInstallDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        if (files == null)
        {
            return;
        }
        if (!targets.HasTargets)
        {
            return;
        }
        ClearPendingInstallDestination(targets.PackageTargets, targets.LooseEntries);
    }

    internal void ClearPendingInstallDestination(PendingInstallDestinationClearRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (files == null)
        {
            return;
        }
        if (!request.HasTargets)
        {
            return;
        }
        ClearPendingInstallDestination(request.PackageTargets, request.LooseEntries);
    }

    private void ClearPendingInstallDestination(IReadOnlyList<ChartOperationTarget> packageTargets, IReadOnlyList<PackageChartEntry> looseEntries)
    {
        RunChartPackageMutation(delegate
        {
            List<ChartOperationTarget> mutablePackageTargets = packageTargets.ToList();
            List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref mutablePackageTargets);
            for (int num = 0; num < packages.Count; num++)
            {
                ClearChartPackageInstallDestinations(packages[num]);
            }
            if (looseEntries.Count > 0)
            {
                files.RemoveInstallDestination(looseEntries);
                MainChartList.RowProjection.UpdateTransientStates(looseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
    }

    internal void ClearInstallDestinationForCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        ClearInstallDestinationForCharts(snapshot.RepairEntries);
    }

    internal void ClearInstallDestinationForCharts(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return;
        }
        ClearInstallDestinationForCharts(request.RepairEntries);
    }

    private void ClearInstallDestinationForCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (files != null)
        {
            if (chartEntries == null)
            {
                throw new ArgumentNullException(nameof(chartEntries));
            }
            RunChartPackageMutation(delegate
            {
                List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
                List<ChartPackage> chartPackages = ExtractChartPackagesFromChartEntries(ref entries);
                for (int num = 0; num < chartPackages.Count; num++)
                {
                    ClearChartPackageInstallDestinations(chartPackages[num]);
                }
                files.RemoveInstallDestination(entries);
                MainChartList.RowProjection.UpdateTransientStates(entries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        }
    }

    private static void ClearChartPackageInstallDestinations(ChartPackage chartPackage)
    {
        foreach (PackageChartEntry entry in chartPackage?.ChartEntries ?? [])
        {
            entry?.ClearInstallDestination();
        }
    }

    internal bool SetPendingInstallDestination(PendingInstallDestinationEditTargetSnapshot target, string destinationDirectory)
    {
        if (files == null)
        {
            return false;
        }
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        return SetPendingInstallDestination(target.PackageEntry, target.GetOrCreateChartEntry, destinationDirectory);
    }

    internal bool SetPendingInstallDestination(PendingInstallDestinationEditRequest request, string destinationDirectory)
    {
        if (files == null)
        {
            return false;
        }
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        return SetPendingInstallDestination(request.PackageEntry, request.GetOrCreateChartEntry, destinationDirectory);
    }

    private bool SetPendingInstallDestination(PackageChartEntry packageEntry, Func<PackageChartEntry> chartEntryFactory, string destinationDirectory)
    {
        PackageChartEntry changedEntry = null;
        bool changed = RunChartPackageMutation(delegate
        {
            if (packageEntry != null)
            {
                changedEntry = packageEntry;
                return files.SetPendingInstallDestination(changedEntry, destinationDirectory);
            }
            PackageChartEntry chartEntry = chartEntryFactory?.Invoke();
            if (chartEntry == null)
            {
                return false;
            }
            changedEntry = chartEntry;
            return files.SetPendingInstallDestination(changedEntry, destinationDirectory);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree);
        if (changed)
        {
            MainChartList.RowProjection.UpdateTransientStates([changedEntry.Chart], forceInstallDestinationProjection: true);
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination, NormalLibraryInstallDestinationChangedReason);
        }
        return changed;
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        if (files == null)
        {
            return false;
        }
        return files.TryGetInstalledDirectoryByHash(hash, out installDir);
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

    internal void UninstallAllData()
    {
        if (string.IsNullOrWhiteSpace(ApplicationSettings.LR2SongDBPath) || !File.Exists(ApplicationSettings.LR2SongDBPath))
        {
            return;
        }
        bool unlockAfterOperation = !LR2SongDBExtended.IsProcessLockEnteredByCurrentThread();
        if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
        {
            throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_uninstall);
        }
        try
        {
            using var lR2SongDBExtended = new LR2SongDBExtended(ApplicationSettings.LR2SongDBPath);
            string savepoint = lR2SongDBExtended.SaveTransactionPoint();
            try
            {
                lR2SongDBExtended.Uninstall();
                lR2SongDBExtended.Commit();
            }
            catch (Exception)
            {
                lR2SongDBExtended.RollbackTo(savepoint);
                throw;
            }
        }
        finally
        {
            if (unlockAfterOperation)
            {
                LR2SongDBExtended.Unlock();
            }
        }
    }

    public void MergeChartDirectory(string src, string dst)
    {
        MergeChartDirectory(src, dst, operationId: 0);
    }

    internal void MergeChartDirectory(string src, string dst, long operationId)
    {
        var totalStopwatch = Stopwatch.StartNew();
        LogDuplicateMergePerformance("duplicate_merge_vm enter op=" + operationId + " src=" + src + " dst=" + dst);
        RunChartPackageMutation(
            delegate
            {
                var modelStopwatch = Stopwatch.StartNew();
                files.MergeChartDirectory(src, dst, operationId);
                LogDuplicateMergePerformance("duplicate_merge_vm model_done op=" + operationId + " elapsedMs=" + modelStopwatch.ElapsedMilliseconds + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            },
            refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree,
            stopPlayback: () =>
            {
                var playEndStopwatch = Stopwatch.StartNew();
                PlaybackPanel.StopPlayback(closeProcess: true);
                LogDuplicateMergePerformance("duplicate_merge_vm play_end_done op=" + operationId + " elapsedMs=" + playEndStopwatch.ElapsedMilliseconds);
            },
            beforeAction: () => PlaylistWorkspace.BeginDuplicateRefreshPriorityWindow("merge_folder"),
            afterUiRefresh: () => ReleaseDuplicateRefreshPriorityWindowAfterUiRefresh("merge_folder_ui_refresh_done"));
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

    private List<string> ConfirmDuplicateInstallRepairRemovals(IEnumerable<ChartFile> repairCharts)
    {
        if (files == null)
        {
            return [];
        }
        List<string> approvedPaths = [];
        List<BMSLibrary.DuplicateInstallRepairConfirmation> confirmations = files.GetDuplicateInstallRepairConfirmations(repairCharts);
        foreach (BMSLibrary.DuplicateInstallRepairConfirmation confirmation in confirmations)
        {
            ChartFile chart = confirmation.Chart;
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }
            if (ShowUiConfirmation(
                string.Format(BeMusicSeeker.Properties.Resources.Confirm_DuplicateReinstallSkipped, chart.Path, string.Join(Environment.NewLine, confirmation.DuplicatePaths)),
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                MessageBoxImage.Question,
                MessageBoxButton.YesNo,
                "Duplicate reinstall repair confirmation",
                MessageBoxResult.Yes))
            {
                approvedPaths.Add(chart.Path);
            }
        }
        return approvedPaths;
    }

    private static void LogDuplicateMergePerformance(string message)
    {
        if (CommandLineSwitches.IsInfoLoggingEnabled)
        {
            NLogWrapper.GetLogger("InstallPerformance.DuplicateMerge").Info(message);
        }
    }

    internal void FixInstallationDirectoryCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        IReadOnlyList<ChartFile> repairCharts = snapshot.RepairCharts;
        List<string> approvedDuplicateRemovalChartPaths = ConfirmDuplicateInstallRepairRemovals(repairCharts);
        RunChartPackageMutation(delegate
        {
            files.FixInstallationDirectoryCharts(repairCharts, approvedDuplicateRemovalChartPaths);
        }, GetBmsFormatCharts(repairCharts), UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    internal void FixInstallationDirectoryCharts(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return;
        }
        IReadOnlyList<ChartFile> repairCharts = request.RepairCharts;
        List<string> approvedDuplicateRemovalChartPaths = ConfirmDuplicateInstallRepairRemovals(repairCharts);
        RunChartPackageMutation(delegate
        {
            files.FixInstallationDirectoryCharts(repairCharts, approvedDuplicateRemovalChartPaths);
        }, GetBmsFormatCharts(repairCharts), UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    internal List<string> GetLibraryWholeFolderDeleteConfirmationPaths(IEnumerable<ChartOperationTarget> targets)
    {
        List<LibraryChartRef> charts = [.. ToLibraryChartRefs(targets, ChartOperationCapabilities.RemoveFromLibrary)];
        if (charts.Count == 0)
        {
            return [];
        }
        using (chartFileOperations.Enter())
        {
            return files.GetLibraryWholeFolderDeleteConfirmationPaths(charts);
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<ChartOperationTarget> targets, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        List<LibraryChartRef> charts = [.. ToLibraryChartRefs(targets, ChartOperationCapabilities.RemoveFromLibrary)];
        if (charts.Count == 0)
        {
            return;
        }
        RunChartPackageMutation(delegate
        {
            files.RemoveLibraryCharts(charts, approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree, stopPlayback: () =>
        {
            PlaybackPanel.StopIfPlayingLibraryCharts(GetBmsLibraryChartRefs(charts));
            PlaybackPanel.StopIfPlayingChartDirectories(approvedWholeFolderDeletePaths);
        });
    }

    internal void RemoveLibraryCharts(IEnumerable<ChartFile> charts, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        List<ChartFile> chartSnapshot = [.. (charts ?? []).Where(chart => chart != null)];
        List<LibraryChartRef> chartRefs = [.. chartSnapshot
            .Select(chart => LibraryChartRef.FromChartFile(chart))
            .Where(chart => chart != null)];
        if (chartRefs.Count == 0)
        {
            return;
        }
        RunChartPackageMutation(delegate
        {
            files.RemoveLibraryCharts(chartRefs, approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree, stopPlayback: () =>
        {
            PlaybackPanel.StopIfPlayingCharts(GetBmsFormatCharts(chartSnapshot));
            PlaybackPanel.StopIfPlayingChartDirectories(approvedWholeFolderDeletePaths);
        });
    }

    internal void RemovePendingCharts(IEnumerable<ChartOperationTarget> targets, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        List<ChartFile> charts = [.. (targets ?? [])
            .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
            .Select(target => target.Chart)
            .Where(chart => chart != null)];
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingCharts(charts, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
        }, deleteContainingPackageFoldersWhenNoBms ? charts : GetBmsFormatCharts(charts));
    }

    public void RecheckZeroNoteWarnings()
    {
        using (chartFileOperations.Enter())
        {
            if (files == null)
            {
                return;
            }
            files.RecheckZeroNoteWarnings();
        }
    }

    internal void RenameBMSFilesExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        List<ChartFile> chartList = GetBmsFormatCharts(charts);
        RunChartPackageMutation(delegate
        {
            files.RenameBMSFilesExtensions(chartList, newExt, true);
        }, chartList, UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
    }

    internal void RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        List<ChartFile> chartList = GetBmsFormatCharts(charts);
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingBmsFormatChartFileExtensions(chartList, newExt);
        }, chartList);
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

    internal void MoveLibraryCharts(ChartLibraryMoveRequest request)
    {
        if (request?.HasTargets != true || string.IsNullOrWhiteSpace(request.NewParentDirectory))
        {
            return;
        }
        RunChartPackageMutation(delegate
        {
            files.MoveLibraryRootFolder(request.Charts, request.NewParentDirectory, false);
            regularChartListOwner.ApplyLatestNormalLibraryRefreshNotification("library_charts_changed");
            InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
        }, refreshMask: UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree, stopPlayback: () => PlaybackPanel.StopIfPlayingLibraryCharts(request.Charts));
    }

    private static IEnumerable<LibraryChartRef> ToLibraryChartRefs(IEnumerable<ChartOperationTarget> targets, ChartOperationCapabilities requiredCapability)
    {
        return (targets ?? [])
            .Where(target => target != null && target.HasCapability(requiredCapability))
            .Select(target => target.ToLibraryChartRef())
            .Where(chart => chart != null);
    }

    internal IRepairInstalledLocationTargetSnapshot CreateRepairInstalledLocationTargetSnapshot(IEnumerable<ChartOperationTarget> targets)
    {
        List<ChartOperationTarget> targetList = [.. (targets ?? []).Where(target => target != null && target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation))];
        List<ChartFile> charts = [.. targetList.Select(target => target.Chart).Where(chart => chart != null)];
        return new RepairInstalledLocationTargetSnapshot(
            charts,
            () => [.. targetList
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal PendingInstallDestinationTargetSnapshot CreatePendingInstallDestinationTargetSnapshot(IEnumerable<ChartOperationTarget> targets)
    {
        List<ChartOperationTarget> remainingTargets = [.. (targets ?? [])
            .Where(target => target?.Chart != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))];
        List<ChartOperationTarget> packageTargets = [.. remainingTargets.Where(target => target.PackageEntry != null)];
        remainingTargets = [.. remainingTargets.Where(target => target.PackageEntry == null)];
        List<ChartFile> charts = [.. remainingTargets.Select(target => target.Chart).Where(chart => chart != null)];
        return new PendingInstallDestinationTargetSnapshot(
            packageTargets,
            charts,
            () => [.. remainingTargets
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal PendingInstallDestinationEditTargetSnapshot CreatePendingInstallDestinationEditTargetSnapshot(ChartOperationTarget target)
    {
        if (target == null || !target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
        {
            return PendingInstallDestinationEditTargetSnapshot.Empty;
        }
        if (target.PackageEntry != null)
        {
            return new PendingInstallDestinationEditTargetSnapshot(target.PackageEntry, null, null);
        }
        return new PendingInstallDestinationEditTargetSnapshot(
            null,
            target.Chart,
            target.ToPackageChartEntry);
    }

    internal sealed class PendingInstallDestinationTargetSnapshot
    {
        private readonly Lazy<IReadOnlyList<PackageChartEntry>> looseEntries;

        internal PendingInstallDestinationTargetSnapshot(
            IEnumerable<ChartOperationTarget> packageTargets,
            IEnumerable<ChartFile> charts,
            Func<IReadOnlyList<PackageChartEntry>> looseEntryFactory)
        {
            PackageTargets = [.. (packageTargets ?? []).Where(target => target?.PackageEntry != null && target.Chart != null)];
            Charts = [.. (charts ?? []).Where(chart => chart != null)];
            looseEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
                () => [.. (looseEntryFactory?.Invoke() ?? []).Where(entry => entry?.Chart != null)]);
        }

        internal IReadOnlyList<ChartOperationTarget> PackageTargets { get; }

        internal IReadOnlyList<ChartFile> Charts { get; }

        internal IReadOnlyList<PackageChartEntry> LooseEntries => looseEntries.Value;

        internal bool HasTargets => PackageTargets.Count > 0 || Charts.Count > 0;

        internal void MaterializeLooseEntries()
        {
            _ = LooseEntries.Count;
        }
    }

    internal sealed class PendingInstallDestinationEditTargetSnapshot
    {
        private readonly Lazy<PackageChartEntry> chartEntry;

        internal static PendingInstallDestinationEditTargetSnapshot Empty { get; } = new(null, null, null);

        internal PendingInstallDestinationEditTargetSnapshot(PackageChartEntry packageEntry, ChartFile chartFile, Func<PackageChartEntry> chartEntryFactory)
        {
            PackageEntry = packageEntry;
            ChartFile = chartFile;
            chartEntry = new Lazy<PackageChartEntry>(() => chartEntryFactory?.Invoke());
        }

        internal PackageChartEntry PackageEntry { get; }

        internal ChartFile ChartFile { get; }

        internal bool HasTarget => PackageEntry != null || ChartFile != null;

        internal PackageChartEntry GetOrCreateChartEntry()
        {
            return PackageEntry ?? chartEntry.Value;
        }
    }

    internal interface IRepairInstalledLocationTargetSnapshot
    {
        bool HasTargets { get; }

        bool HasInstallDestination { get; }

        IReadOnlyList<ChartFile> RepairCharts { get; }

        void MaterializeRepairEntries();
    }

    private static RepairInstalledLocationTargetSnapshot AsRepairInstalledLocationTargetSnapshot(IRepairInstalledLocationTargetSnapshot snapshot)
    {
        return snapshot as RepairInstalledLocationTargetSnapshot;
    }

    private sealed class RepairInstalledLocationTargetSnapshot : IRepairInstalledLocationTargetSnapshot
    {
        private readonly Lazy<IReadOnlyList<PackageChartEntry>> repairEntries;

        internal RepairInstalledLocationTargetSnapshot(IEnumerable<ChartFile> charts, Func<IReadOnlyList<PackageChartEntry>> repairEntryFactory)
        {
            Charts = [.. (charts ?? []).Where(chart => chart != null)];
            repairEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
                () => [.. (repairEntryFactory?.Invoke() ?? []).Where(entry => entry?.Chart != null)]);
            repairCharts = new Lazy<IReadOnlyList<ChartFile>>(CreateRepairCharts);
        }

        private readonly Lazy<IReadOnlyList<ChartFile>> repairCharts;

        internal bool HasTargets => Charts.Count > 0;

        public bool HasInstallDestination => RepairCharts.Any(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination));

        internal IReadOnlyList<ChartFile> Charts { get; }

        public IReadOnlyList<ChartFile> RepairCharts => repairCharts.Value;

        internal IReadOnlyList<PackageChartEntry> RepairEntries => repairEntries.Value;

        private IReadOnlyList<ChartFile> CreateRepairCharts()
        {
            IReadOnlyList<PackageChartEntry> entries = RepairEntries;
            if (entries.Count == 0)
            {
                return Charts;
            }

            var entriesByChartKey = entries
                .Select(entry => new
                {
                    Entry = entry,
                    Key = CreateRepairChartKey(entry.Chart)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            return [.. Charts.Select(chart =>
            {
                string key = CreateRepairChartKey(chart);
                if (string.IsNullOrWhiteSpace(key) || !entriesByChartKey.TryGetValue(key, out var entry))
                {
                    return chart;
                }
                if (!HasInstallDestinationState(entry.Entry.Chart))
                {
                    return chart;
                }

                return ChartFileProjection.WithPackageState(
                    chart,
                    entry.Entry.Chart.InstallDestination,
                    entry.Entry.Chart.InstallDestinationTitle,
                    entry.Entry.Chart.InstallDestinationArtist,
                    entry.Entry.Chart.InstallDestinationSuggestions,
                    chart.Warnings);
            })];
        }

        private static bool HasInstallDestinationState(ChartFile chart)
        {
            return chart != null
                && (!string.IsNullOrWhiteSpace(chart.InstallDestination)
                    || !string.IsNullOrWhiteSpace(chart.InstallDestinationTitle)
                    || !string.IsNullOrWhiteSpace(chart.InstallDestinationArtist)
                    || (chart.InstallDestinationSuggestions?.Count ?? 0) > 0);
        }

        private static string CreateRepairChartKey(ChartFile chart)
        {
            if (chart == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(chart.Path))
            {
                return "path:" + chart.Path;
            }

            string hash = ChartLookupKey.GetPrimaryHash(chart);
            return string.IsNullOrWhiteSpace(hash) ? null : "hash:" + hash;
        }

        public void MaterializeRepairEntries()
        {
            _ = RepairEntries.Count;
        }

        bool IRepairInstalledLocationTargetSnapshot.HasTargets => HasTargets;
    }

    /// <summary>
    /// 指定されたハッシュ群をキーに LR2IR キャッシュを更新します。
    /// 実ファイル未所持の playlist 行でもランキングデータ更新を行えるようにします。
    /// </summary>
    /// <param name="hashes">更新対象の MD5 ハッシュ一覧。</param>
    public void GetLR2IRCacheHashes(IEnumerable<string> hashes)
    {
        using (chartFileOperations.Enter())
        {
            if (hashes == null)
            {
                throw new ArgumentNullException(nameof(hashes));
            }
            try
            {
                List<string> normalizedHashes = [.. hashes.Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (normalizedHashes.Count == 0)
                {
                    return;
                }
                List<BMSLibrary.IRDataCacheInfo> iRDataNeedUpdates = files.GetIRDataNeedUpdates(normalizedHashes);
                if (iRDataNeedUpdates.Count > 0)
                {
                    if (ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_download_ranking_cache + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Download + ": " + iRDataNeedUpdates.Count + Environment.NewLine + BeMusicSeeker.Properties.Resources.Skip + ": " + (normalizedHashes.Count - iRDataNeedUpdates.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Size + ": " + FileSizeHelper.GetReadableFileSize(iRDataNeedUpdates.Select(c => c.size).Sum()), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk, MessageBoxButton.OKCancel, "Ranking cache download confirmation", MessageBoxResult.OK))
                    {
                        List<BMSLibrary.IRDataCacheInfo> list = files.DownloadIRData(iRDataNeedUpdates);
                        ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_download_completed + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (iRDataNeedUpdates.Count - list.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + list.Count, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk, "Ranking cache download completion notification");
                    }
                }
                else
                {
                    ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_ranking_cache_notfound, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Hand, "Ranking cache not found notification");
                }
            }
            catch (InvalidOperationException ex) when (ex is not UiDialogDisplayException)
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_warn_cache_download, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Ranking cache download warning notification");
            }
            catch (Exception ex) when (ex is not UiDialogDisplayException)
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_error_cache_download + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "Ranking cache download failure notification");
            }
        }
    }

    /// <summary>
    /// Score Viewer 登録対象の状態を調べ、UI 確認前の実行計画を作成します。
    /// 未所持 playlist 行では path が null でも閲覧 URL を返せます。
    /// </summary>
    /// <param name="targets">登録対象の軽量ターゲット一覧。</param>
    /// <returns>hash-only、登録済み、upload 必要、status 失敗を区別した計画。</returns>
    internal ScoreViewerRegistrationPlan PrepareScoreViewerRegistration(List<ScoreViewerTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        List<ScoreViewerTarget> normalizedTargets = [.. targets.Where(target => target != null && !string.IsNullOrWhiteSpace(target.Hash))];
        if (normalizedTargets.Count == 0)
        {
            return new ScoreViewerRegistrationPlan([]);
        }
        var items = new List<ScoreViewerRegistrationItem>();
        foreach (ScoreViewerTarget target in normalizedTargets)
        {
            string currentFileHash = target.Hash;
            if (string.IsNullOrWhiteSpace(target.Path) || !LongPathFileSystem.FileExists(target.Path))
            {
                items.Add(ScoreViewerRegistrationItem.HashOnly(target, currentFileHash, scoreViewUrl + currentFileHash));
                continue;
            }

            try
            {
                string statusJson = AppHttpClient.Shared.GetString(new Uri(scoreStatusUrl + currentFileHash), Encoding.UTF8);
                dynamic statusVal = DynamicJson.Parse(statusJson);
                if (statusVal.status == "OK")
                {
                    items.Add(ScoreViewerRegistrationItem.AlreadyRegistered(target, currentFileHash, scoreViewUrl + currentFileHash));
                    continue;
                }
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn(ex, "score_viewer_status_failed path=" + (target.Path ?? string.Empty) + " md5=" + (target.Hash ?? string.Empty));
                items.Add(ScoreViewerRegistrationItem.StatusCheckFailed(target, currentFileHash, ex));
                continue;
            }

            items.Add(ScoreViewerRegistrationItem.NeedsUpload(target, currentFileHash));
        }
        return new ScoreViewerRegistrationPlan(items);
    }

    /// <summary>
    /// UI 側で確認済みの Score Viewer upload を実行し、登録結果を返します。
    /// </summary>
    /// <param name="plan">事前に作成された登録計画。</param>
    /// <param name="uploadConfirmed">upload 必要 target の登録を UI 側で確認済みなら true。</param>
    /// <returns>登録済み、upload 成功、失敗、キャンセルを区別した結果。</returns>
    internal ScoreViewerRegistrationResult CompleteScoreViewerRegistration(ScoreViewerRegistrationPlan plan, bool uploadConfirmed)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        var items = new List<ScoreViewerRegistrationItem>();
        foreach (ScoreViewerRegistrationItem item in plan.Items)
        {
            if (!item.IsUploadCandidate)
            {
                items.Add(item);
                continue;
            }
            if (!uploadConfirmed)
            {
                items.Add(ScoreViewerRegistrationItem.UploadDeclined(item.Target, item.Hash));
                continue;
            }

            try
            {
                string registerResponseJson = AppHttpClient.Shared.PostFile(new Uri(scoreRegisterUrl), item.Target.Path, responseEncoding: Encoding.UTF8, headers: new Dictionary<string, string> { { "Accept", "application/json" } }, logErrorResponseBody: true);
                dynamic registerResponseVal = DynamicJson.Parse(registerResponseJson);
                string currentFileHash = item.Hash;
                if (registerResponseVal.status == "OK")
                {
                    string responseHash = Convert.ToString(registerResponseVal.md5, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(responseHash))
                    {
                        currentFileHash = responseHash;
                    }
                    items.Add(ScoreViewerRegistrationItem.Uploaded(item.Target, currentFileHash, scoreViewUrl + currentFileHash));
                    continue;
                }
                string failureStatus = Convert.ToString(registerResponseVal.status, CultureInfo.InvariantCulture);
                string failureMessage = string.IsNullOrWhiteSpace(failureStatus) ? "Unexpected Score Viewer upload response." : "Score Viewer upload status: " + failureStatus;
                NLogWrapper.FileLogger?.Warn("score_viewer_upload_rejected path=" + (item.Target.Path ?? string.Empty) + " md5=" + (item.Hash ?? string.Empty) + " status=" + failureStatus);
                items.Add(ScoreViewerRegistrationItem.UploadFailed(item.Target, item.Hash, failureMessage));
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn(ex, "score_viewer_upload_failed path=" + (item.Target.Path ?? string.Empty) + " md5=" + (item.Hash ?? string.Empty));
                items.Add(ScoreViewerRegistrationItem.UploadFailed(item.Target, item.Hash, ex.Message, ex));
            }
        }
        return new ScoreViewerRegistrationResult(items);
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

    public void ConvertBMSToAudioFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string saveDir, CancellationToken token = default, Action<bool> onEachCompleted = null)
    {
        if (!LongPathFileSystem.DirectoryExists(saveDir))
        {
            throw new DirectoryNotFoundException("Directory " + saveDir + " not found");
        }
        bmsFiles = bmsFiles.Materialize();
        PlaybackPanel.StopPlayback(closeProcess: true);
        BassAudioPlayer.Frequency = ApplicationSettings.EncoderSampleRate;
        BassAudioPlayer.Format = ApplicationSettings.EncoderFormat;
        BassAudioWriter.EncoderDirectory = ApplicationSettings.EncoderExeDir;
        BassAudioWriter.Initialize();
        int num = 0;
        foreach (BeMusicSeeker.Models.BMSFile bmsFile in bmsFiles)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            bool obj = true;
            BMSAutoPlayWriter bMSAutoPlayWriter = null;
            try
            {
                num++;
                var bMSFile = new Ribbit.BMS.BMSFile(bmsFile.path);
                string text = new Dictionary<string, string>
                {
                    {
                        "%ARTIST%",
                        ((bMSFile.Artist.Trim() ?? string.Empty) + " " + (bMSFile.Subartist?.Trim() ?? string.Empty)).Trim()
                    },
                    {
                        "%TITLE%",
                        ((bMSFile.Title.Trim() ?? string.Empty) + " " + (bMSFile.Subtitle?.Trim() ?? string.Empty)).Trim()
                    },
                    {
                        "%GENRE%",
                        bMSFile.Genre.Trim() ?? string.Empty
                    },
                    {
                        "%NO%",
                        num.ToString().PadLeft(Math.Max(2, bmsFiles.Count().ToString().Length), '0')
                    },
                    {
                        "%FILE%",
                        Path.GetFileName(bmsFile.path)
                    },
                    { "%HASH%", bMSFile.Md5 }
                }.Aggregate(ApplicationSettings.EncodeFileNameFormat, (i, r) => i.Replace(r.Key, r.Value)).NaturalNormalizationForFileName().ReplaceInvalidFileNameCharsByWide()
                    .RemoveInvalidFileNameChars()
                    .Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = num.ToString();
                }
                int num2 = saveDir.Length + text.Length;
                if (num2 > 240 && text.Length > num2 - 240 + 5)
                {
                    text = text.Substring(0, text.Length - (num2 - 235));
                }
                string filePathWithoutExtension = Path.Combine(saveDir, text);
                bMSAutoPlayWriter = new BMSAutoPlayWriter(bMSFile);
                if (!BassAudioWriter.IsEncoderAvailable(ApplicationSettings.Encoder))
                {
                    ApplicationSettings.Encoder = EncoderType.WAVE;
                }
                bMSAutoPlayWriter.LoadResources();
                bMSAutoPlayWriter.Write(ApplicationSettings.Encoder, ApplicationSettings.EncoderQuality, filePathWithoutExtension, ApplicationSettings.EncoderNormalization, ApplicationSettings.EncoderAmplifier);
            }
            catch (Exception ex)
            {
                obj = false;
                NLogWrapper.GetLogger()?.Warn(ex.ToString());
            }
            bMSAutoPlayWriter?.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            onEachCompleted?.Invoke(obj);
        }
        BassAudioPlayer.Free();
    }
}
