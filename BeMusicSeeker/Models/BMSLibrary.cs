using System;
using System.Collections;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Diagnostics;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.BmsLibraryInternal;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using NLog;
using Newtonsoft.Json.Linq;
using Ribbit.Logging;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

[Flags]
internal enum LibraryChartRefreshEffects
{
    None = 0,
    SourceChanged = 1,
    WarningPresentationChanged = 2,
    MaintenancePresentationChanged = 4,
    InstallDestinationOverlayChanged = 8
}

internal sealed class NormalLibraryRefreshNotification
{
    internal static NormalLibraryRefreshNotification Empty { get; } = new(
        0,
        0,
        LibraryChartRefreshEffects.None,
        [],
        notifiesStorageRows: false,
        resetsPriorNotifications: false);

    internal NormalLibraryRefreshNotification(
        int version,
        int ownedCollectionVersion,
        LibraryChartRefreshEffects effects,
        IReadOnlyList<ChartFile> installDestinationChangedCharts,
        bool notifiesStorageRows,
        bool resetsPriorNotifications,
        bool notifiesBmsFiles = false,
        bool notifiesBmsonSongs = false,
        IReadOnlyList<BMSFile> removedBmsFiles = null,
        IReadOnlyList<LR2SongDBExtended.bmson_song> removedBmsonSongs = null,
        bool storageRowsRemoveDeltaComplete = false)
    {
        Version = version;
        OwnedCollectionVersion = ownedCollectionVersion;
        Effects = effects;
        InstallDestinationChangedCharts = installDestinationChangedCharts ?? [];
        bool hasSpecificStorageRowNotification = notifiesBmsFiles || notifiesBmsonSongs;
        NotifiesBmsFiles = notifiesBmsFiles || (notifiesStorageRows && !hasSpecificStorageRowNotification);
        NotifiesBmsonSongs = notifiesBmsonSongs || (notifiesStorageRows && !hasSpecificStorageRowNotification);
        NotifiesStorageRows = notifiesStorageRows || NotifiesBmsFiles || NotifiesBmsonSongs;
        ResetsPriorNotifications = resetsPriorNotifications;
        RemovedBmsFiles = removedBmsFiles ?? [];
        RemovedBmsonSongs = removedBmsonSongs ?? [];
        StorageRowsRemoveDeltaComplete = storageRowsRemoveDeltaComplete && NotifiesStorageRows;
    }

    internal int Version { get; }

    internal int OwnedCollectionVersion { get; }

    internal LibraryChartRefreshEffects Effects { get; }

    internal IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; }

    internal bool NotifiesStorageRows { get; }

    internal bool NotifiesBmsFiles { get; }

    internal bool NotifiesBmsonSongs { get; }

    internal bool ResetsPriorNotifications { get; }

    internal IReadOnlyList<BMSFile> RemovedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonSongs { get; }

    internal bool StorageRowsRemoveDeltaComplete { get; }
}

internal sealed class NormalLibraryRefreshNotificationBatch
{
    internal static NormalLibraryRefreshNotificationBatch Empty { get; } = new(
        0,
        0,
        LibraryChartRefreshEffects.None,
        [],
        notifiesStorageRows: false,
        resetsPriorNotifications: false);

    internal NormalLibraryRefreshNotificationBatch(
        int latestVersion,
        int ownedCollectionVersion,
        LibraryChartRefreshEffects effects,
        IReadOnlyList<ChartFile> installDestinationChangedCharts,
        bool notifiesStorageRows,
        bool resetsPriorNotifications,
        bool notifiesBmsFiles = false,
        bool notifiesBmsonSongs = false,
        IReadOnlyList<BMSFile> removedBmsFiles = null,
        IReadOnlyList<LR2SongDBExtended.bmson_song> removedBmsonSongs = null,
        bool storageRowsRemoveDeltaComplete = false)
    {
        LatestVersion = latestVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        Effects = effects;
        InstallDestinationChangedCharts = installDestinationChangedCharts ?? [];
        bool hasSpecificStorageRowNotification = notifiesBmsFiles || notifiesBmsonSongs;
        NotifiesBmsFiles = notifiesBmsFiles || (notifiesStorageRows && !hasSpecificStorageRowNotification);
        NotifiesBmsonSongs = notifiesBmsonSongs || (notifiesStorageRows && !hasSpecificStorageRowNotification);
        NotifiesStorageRows = notifiesStorageRows || NotifiesBmsFiles || NotifiesBmsonSongs;
        ResetsPriorNotifications = resetsPriorNotifications;
        RemovedBmsFiles = removedBmsFiles ?? [];
        RemovedBmsonSongs = removedBmsonSongs ?? [];
        StorageRowsRemoveDeltaComplete = storageRowsRemoveDeltaComplete && NotifiesStorageRows;
    }

    internal int LatestVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal LibraryChartRefreshEffects Effects { get; }

    internal IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; }

    internal bool NotifiesStorageRows { get; }

    internal bool NotifiesBmsFiles { get; }

    internal bool NotifiesBmsonSongs { get; }

    internal bool ResetsPriorNotifications { get; }

    internal IReadOnlyList<BMSFile> RemovedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonSongs { get; }

    internal bool StorageRowsRemoveDeltaComplete { get; }

    internal bool HasRefreshNotification => ResetsPriorNotifications
        || Effects != LibraryChartRefreshEffects.None
        || InstallDestinationChangedCharts.Count > 0;

    internal bool HasEffect(LibraryChartRefreshEffects effect)
    {
        return (Effects & effect) != 0;
    }
}

/// <summary>
/// BMS ファイルのライブラリ管理を担う中核クラスです。
/// LR2 の song.db を読み込み、BMSファイルの走査・登録・インストール・保守（Missing/Garbled/ZeroNote/Duplicate 検出）、
/// プレイリスト (BMSTable) との参照解決、LR2IR キャッシュの取得、およびフォルダの移動・リネーム・削除といった
/// ファイルシステム操作を一手に引き受けます。
/// </summary>
public partial class BMSLibrary : ObservableObject
{
    internal sealed class DuplicateInstallRepairConfirmation
    {
        internal DuplicateInstallRepairConfirmation(ChartFile chart, IReadOnlyList<string> duplicatePaths)
        {
            Chart = chart;
            DuplicatePaths = duplicatePaths ?? [];
        }

        internal ChartFile Chart { get; }

        internal IReadOnlyList<string> DuplicatePaths { get; }
    }

    private Lr2SynchronizationRuntimeState lr2SynchronizationRuntimeState;

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler
    {
        get => lr2SynchronizationRuntimeState?.StartupBackgroundTaskScheduler;
        set
        {
            if (lr2SynchronizationRuntimeState != null)
            {
                lr2SynchronizationRuntimeState.StartupBackgroundTaskScheduler = value;
            }
        }
    }

    internal Action<string, string, long, bool, string> StartupBackgroundTaskReporter
    {
        get => lr2SynchronizationRuntimeState?.StartupBackgroundTaskReporter;
        set
        {
            if (lr2SynchronizationRuntimeState != null)
            {
                lr2SynchronizationRuntimeState.StartupBackgroundTaskReporter = value;
            }
        }
    }

    internal Func<StartupBackgroundWorkSnapshot> StartupBackgroundWorkSnapshotProvider { get; set; }

    private int shutdownRequested;

    public enum LibraryInitializeMode
    {
        Startup,
        FullReinitialize,
        ScoreOnly
    }

    public enum LibraryInitializationProgressStage
    {
        None = 0,
        DatabaseLoad = 1,
        FileEnumeration = 2,
        FileDiff = 3
    }

    public sealed class LibraryInitializationProgressSnapshot
    {
        internal LibraryInitializationProgressSnapshot(
            long version,
            LibraryInitializationProgressStage stage,
            string scannerLabel,
            int totalCount,
            int processedCount,
            string currentPath)
        {
            Version = version;
            Stage = stage;
            ScannerLabel = scannerLabel;
            TotalCount = totalCount;
            ProcessedCount = processedCount;
            CurrentPath = currentPath;
        }

        public long Version { get; }

        public LibraryInitializationProgressStage Stage { get; }

        public string ScannerLabel { get; }

        public int TotalCount { get; }

        public int ProcessedCount { get; }

        public string CurrentPath { get; }
    }

    /// <summary>
    /// BMS 親フォルダ一覧キャッシュのスナップショットを格納するクラスです。
    /// バックグラウンドスレッドで構築し、UIスレッドで適用する2段階方式に利用されます。
    /// </summary>
    public sealed class ParentFolderListCacheSnapshot
    {
        public int Version { get; set; }

        public long RebuildMs { get; set; }

        public List<string> ParentFolders { get; set; }
    }

    /// <summary>
    /// playlist score probe 用の score 反映メトリクスです。
    /// </summary>
    internal struct BmsScoreApplyMetrics
    {
        internal int TargetCount;

        internal long WaitInitializedMinMs;

        internal long WaitBmsFilesReadMs;

        internal long WaitScoresWriteMs;

        internal long WaitScoreSnapshotReadMs;

        internal long ApplyKnownScoresMs;

        internal int MatchedScoreCount;

        internal long TotalMs;
    }

    /// <summary>
    /// BMSScores の読み取り専用 snapshot です。
    /// writer lock を長時間待たずに score lookup できるよう、hash index をあわせて保持します。
    /// </summary>
    internal sealed class ScoreSnapshot
    {
        internal int Version { get; set; }

        internal DateTime LoadedAtUtc { get; set; }

        internal long BuildElapsedMs { get; set; }

        internal List<BMSScore> Scores { get; set; } = [];

        internal Dictionary<string, BMSScore> ScoresByHash { get; set; } = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, BMSScore> ScoresBySha256 { get; set; } = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);

        internal ActiveScoreSource ActiveScoreSource { get; set; }

        /// <summary>score table のロード状態です。</summary>
        internal ScoreTableLoadStatus LoadStatus { get; set; } = ScoreTableLoadStatus.NotConfigured;

        /// <summary>score table のロード失敗理由です。</summary>
        internal string LoadFailureMessage { get; set; } = string.Empty;

        /// <summary>score source の切り替え世代です。</summary>
        internal long SourceGeneration { get; set; }
    }

    private sealed class RankingDownloadContext
    {
        internal long ScoreSourceGeneration { get; init; }

        internal int Lr2Id { get; init; }

        internal string ScoreDbPath { get; init; }
    }

    /// <summary>
    /// playlist detail resolve index の runtime cache 状態です。
    /// </summary>
    internal sealed class PlaylistLibraryResolveIndexRuntimeState
    {
        internal bool IsCached { get; set; }

        internal int SnapshotVersion { get; set; }

        internal long BuildElapsedMs { get; set; }

        internal int InvalidationVersion { get; set; }

        internal int OwnedCollectionVersion { get; set; }
    }

    /// <summary>
    /// installed primary md5 lookup の warmup 結果です。
    /// </summary>
    internal sealed class InstalledPrimaryHashWarmupResult
    {
        internal string IndexName { get; set; }

        internal string Status { get; set; }

        internal long ElapsedMs { get; set; }

        internal long BuildMs { get; set; }

        internal int PrimaryHashCount { get; set; }

        internal int BmsCount { get; set; }

        internal int BmsonCount { get; set; }

        internal bool FullDirectoryLookupInitialized { get; set; }
    }

    /// <summary>
    /// owned collection 隣接 index の warmup 結果です。
    /// startup readiness 外の best-effort warmup をログで追跡するために使います。
    /// </summary>
    internal sealed class OwnedAdjacentIndexWarmupResult
    {
        internal string IndexName { get; set; }

        internal string Status { get; set; }

        internal long ElapsedMs { get; set; }

        internal int ChartRefCount { get; set; }

        internal int DirectDirectoryCount { get; set; }

        internal int SubtreeDirectoryCount { get; set; }

        internal int DirectoryCount { get; set; }

        internal int OwnedCollectionVersion { get; set; }
    }

    /// <summary>
    /// score snapshot / hydration / ranking refresh の診断状態です。
    /// playlist open readiness の記録に利用します。
    /// </summary>
    internal struct ScoreRuntimeState
    {
        internal bool SnapshotReady;

        internal int SnapshotVersion;

        /// <summary>active score source です。</summary>
        internal ActiveScoreSource ActiveScoreSource;

        /// <summary>score table のロード状態です。</summary>
        internal ScoreTableLoadStatus LoadStatus;

        /// <summary>score table のロード失敗理由です。</summary>
        internal string LoadFailureMessage;

        /// <summary>score source の切り替え世代です。</summary>
        internal long SourceGeneration;

        internal bool HydrationRunning;

        internal int HydrationCompletedVersion;

        internal bool RankingRefreshRunning;

        internal int RankingRefreshCompletedVersion;
    }

    private sealed class BackgroundPendingEstimatePreparationResult
    {
        internal List<ChartPackage> EstimablePackages { get; } = [];

        internal List<ChartPackage> DeferredPackages { get; } = [];

        internal Dictionary<ChartPackage, int> DeferredSourceHealthByPackage { get; } = [];

        internal PendingEstimateSourceBatchSnapshot BatchSourceSnapshot { get; set; }
    }

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.BMSLibrary");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly bool everythingScanLoggingEnabled = installPerformanceLoggingEnabled;

    private static readonly FileMutationOptions targetOnlyFileMutationOptions = new(ReadOnlyNormalizationScope.TargetOnly);

    private static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

    private const int playlistReferenceApplyChunkSize = 1024;

    /// <summary>
    /// インストール処理のパフォーマンスログを出力します。コマンドラインスイッチで有効化されている場合のみ動作します。
    /// </summary>
    private static void LogInstallPerformance(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogInstallPerformanceWarn(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Warn(message);
        }
    }

    private static void LogStartupMemoryCheckpoint(string phase, string point)
    {
        StartupMemoryPressureService.LogCheckpoint(LogInstallPerformance, phase, point);
    }

    private void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail = null)
    {
        GetLr2SynchronizationRuntimeState().ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);
    }

    internal bool IsShutdownRequested => GetLr2SynchronizationRuntimeState().IsShutdownRequested;

    /// <summary>新規処理の受付を止め、IR 本文受信を含む終了対象へキャンセルを通知します。</summary>
    internal void RequestShutdown(string reason)
    {
        Interlocked.Exchange(ref shutdownRequested, 1);
        GetLr2SynchronizationRuntimeState().RequestShutdown();
        irScoreShutdownCancellation.Cancel();
        lr2SynchronizationOwner.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested");
        string shutdownReason = "shutdown:" + (reason ?? "unknown");
        try
        {
            lr2SynchronizationOwner.RequestShutdownCancellation(shutdownReason);
        }
        catch (Exception ex)
        {
            LogInstallPerformance("shutdown cancel_lr2_song_db_sync_failed reason=" + shutdownReason + " message=" + ex.Message);
        }
        try
        {
            packageLifecycleOwner?.CancelPendingEstimateQueue();
        }
        catch (Exception ex)
        {
            LogInstallPerformance("shutdown cancel_pending_estimate_failed reason=" + shutdownReason + " message=" + ex.Message);
        }
    }

    /// <summary>prefetch 通信と既存 worker の実完了前には終了を許可しません。</summary>
    internal bool HasShutdownBlockingWork =>
        Lr2SongDbSyncRunning
        || ChartInfoHydrationRunning
        || ChartInfoBackfillRunning
        || ChartDigestBackfillRunning
        || MaintenanceHydrationRunning
        || InstallableMaintenanceDeferredRunning
        || ScoreHydrationRunning
        || RankingRefreshRunning
        || IrScorePrefetchRunning
        || (packageLifecycleOwner != null && !packageLifecycleOwner.IsPendingEstimateQueueIdle);

    internal string GetShutdownBlockingWorkLogFields()
    {
        return "lr2SongDbSyncRunning=" + FormatBool(Lr2SongDbSyncRunning)
            + " chartInfoHydrationRunning=" + FormatBool(ChartInfoHydrationRunning)
            + " chartInfoBackfillRunning=" + FormatBool(ChartInfoBackfillRunning)
            + " chartDigestBackfillRunning=" + FormatBool(ChartDigestBackfillRunning)
            + " maintenanceHydrationRunning=" + FormatBool(MaintenanceHydrationRunning)
            + " installableMaintenanceDeferredRunning=" + FormatBool(InstallableMaintenanceDeferredRunning)
            + " scoreHydrationRunning=" + FormatBool(ScoreHydrationRunning)
            + " rankingRefreshRunning=" + FormatBool(RankingRefreshRunning)
            + " irScorePrefetchRunning=" + FormatBool(IrScorePrefetchRunning)
            + " pendingInstallEstimateQueueIdle=" + FormatBool(packageLifecycleOwner == null || packageLifecycleOwner.IsPendingEstimateQueueIdle);
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private bool TrySkipForShutdown(string operation, string reason)
    {
        return GetLr2SynchronizationRuntimeState().TrySkipForShutdown(operation, reason);
    }

    private Lr2SynchronizationRuntimeState GetLr2SynchronizationRuntimeState()
    {
        return lr2SynchronizationRuntimeState ??= new(_ => { });
    }

    /// <summary>
    /// Everything 全件走査のログを出力します。パフォーマンスロギングが有効な場合のみ動作します。
    /// </summary>
    private static void LogEverythingScan(string message)
    {
        if (everythingScanLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    /// <summary>
    /// LR2IR ランキングデータのキャッシュ情報（MD5ハッシュ、データサイズ、最終更新日時）を保持するクラスです。
    /// ribbit.xyz サーバーから取得した JSON をパースして生成されます。
    /// </summary>
    public class IRDataCacheInfo
    {
        public string md5 { get; set; }

        public int size { get; set; }

        public DateTime lastupdate { get; set; }

        public IRDataCacheInfo(JObject json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            if (TryGetNonNullProperty(json, "md5", out JToken md5Token)
                && TryGetNonNullProperty(json, "size", out JToken sizeToken)
                && TryGetNonNullProperty(json, "lastupdate", out JToken lastUpdateToken))
            {
                string md5Value = md5Token.ToString();
                if (!LR2SongDB.md5HashRegex.IsMatch(md5Value))
                {
                    throw new ArgumentException(Resources.Error_NotMd5Hash, "json.md5");
                }
                md5 = md5Value;
                size = ParseSize(sizeToken);
                lastupdate = DateTime.ParseExact(lastUpdateToken.ToString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }
            throw new ArgumentException(Resources.Error_InvalidJsonObject, "json");
        }

        private static bool TryGetNonNullProperty(JObject source, string propertyName, out JToken value)
        {
            return source.TryGetValue(propertyName, out value) && value.Type != JTokenType.Null;
        }

        private static int ParseSize(JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }
            if (token.Type == JTokenType.Float)
            {
                double value = token.Value<double>();
                if (!double.IsNaN(value)
                    && !double.IsInfinity(value)
                    && value >= int.MinValue
                    && value <= int.MaxValue
                    && Math.Truncate(value) == value)
                {
                    return (int)value;
                }
            }
            return int.Parse(token.ToString());
        }
    }

    private readonly string lr2SongDBPath;

    private readonly IUiScheduler uiScheduler;

    private readonly object lr2PropertyPublicationGate = new();

    private readonly HashSet<string> pendingLr2PropertyNames = new(StringComparer.Ordinal);

    private long lr2PropertyPublicationVersion;

    private bool lr2PropertyPublicationScheduled;

    private UiSchedulePriority pendingLr2PropertyPublicationPriority = UiSchedulePriority.Normal;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly EverythingNative everythingNative;

    private readonly string lr2ScoreDBPath;

    private Dictionary<string, BMSScore> beatorajaScoresBySha256 = new(StringComparer.OrdinalIgnoreCase);

    private ActiveScoreSource activeScoreSource;

    private long scoreSourceGeneration;

    private ScoreTableLoadStatus scoreTableLoadStatus = ScoreTableLoadStatus.NotConfigured;

    private string scoreTableLoadFailureMessage = string.Empty;

    private Lr2PlayHistorySchemaCheckResult lr2PlayHistorySchemaCheckResult;

    private readonly Func<LR2Config> lr2config;

    private readonly LibraryResourceIndexOwner libraryResourceIndexOwner = new(
        LibraryResourceIndex.CreateFromScanResult(new ChartScanResult()));

    private readonly int innerWavHealthThreshForNormalBMSFile = 70;

    private readonly double dupRateThreshInOnePkg = 0.9;

    private readonly object lockRankingScores = new();

    private readonly object lockParentFolderList = new();

    private bool bmsParentFolderListDirty = true;

    private int bmsParentFolderListDirtyVersion;

    private readonly CatalogOwnedCollectionOwner catalogOwnedCollectionOwner = new();

    private object lockOwnedChartCollection => catalogOwnedCollectionOwner.Gate;

    private int duplicateChartGroupsInvalidationVersion;

    private HashSet<BMSFile> duplicateWarningBmsOwners = [];

    private bool duplicateWarningFullClearPending = true;

    private readonly object lockInstalledChartLookupIndex = new();

    private InstalledChartLookupIndexState installedChartLookupIndex = new();

    private bool installedChartLookupIndexInitialized;

    private long installedChartLookupGeneration;

    // Pending estimate snapshots and installed lookup publications must cross the
    // same boundary so a digest update cannot become visible between validation
    // and applying the corresponding package result.
    private readonly object pendingInstallEstimateCurrentnessGate = new();

    // Production leaves this optional diagnostic boundary unset. Tests can use
    // it to observe execution without exposing a public callback surface.
    private readonly IInstallEstimationExecutionObserver installEstimationExecutionObserver;

    private long ownedDigestMutationGeneration;

    private readonly object lockInstalledPrimaryHashLookup = new();

    private PrimaryHashLookupState installedPrimaryHashLookup = new();

    private bool installedPrimaryHashLookupInitialized;

    private readonly object lockInstallEstimationMetadataProfileCache = new();

    private readonly Dictionary<string, InstallEstimationMetadataProfile> installEstimationMetadataProfileCache = new(StringComparer.OrdinalIgnoreCase);

    // Lock acquisition order for facade orchestration:
    // rwlockBMSFilesInitializedAll / rwlockBMSFilesInitializedMin
    // -> rwlockPendingInstallCharts
    // -> rwlockBMSFiles
    // -> rwlockSongDBInstall / catalog maintenance write gate
    // -> rwlockBMSScores
    private readonly ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedAll = new();

    private readonly ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedMin = new();

    private readonly ReaderWriterLockSlimWrapper rwlockDuplicateChartGroups = new();

    private readonly ReaderWriterLockSlimWrapper rwlockPendingInstallCharts = new();

    private readonly ReaderWriterLockSlimWrapper rwlockLR2IrDir = new();

    private readonly ReaderWriterLockSlimWrapper rwlockBMSScores = new();

    private readonly CatalogStorageRowsOwner catalogStorageRowsOwner = new();

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly CatalogMaintenanceOwner catalogMaintenanceOwner;

    private readonly CatalogChartInfoOwner catalogChartInfoOwner;
    private readonly CatalogWriteFailureSubscription catalogWriteFailureSubscription;

    private ReaderWriterLockSlimWrapper rwlockBMSFiles => catalogStorageRowsOwner.WriteGate;

    private readonly ReaderWriterLockSlimWrapper rwlockSongDBInstall = new();

    private object lockStorageRowsVersion => catalogStorageRowsOwner.VersionGate;

    private object lockChartInfoBackfill => catalogChartInfoOwner.BackfillGate;

    private List<ChartInfoBackfillRequest> chartInfoBackfillRequests => catalogChartInfoOwner.BackfillRequests;

    private object lockChartInfoHydration => catalogChartInfoOwner.HydrationGate;

    private bool chartInfoHydrationRunning
    {
        get => catalogChartInfoOwner.HydrationRunningState;
        set => catalogChartInfoOwner.HydrationRunningState = value;
    }

    private bool chartInfoHydrationPending
    {
        get => catalogChartInfoOwner.HydrationPending;
        set => catalogChartInfoOwner.HydrationPending = value;
    }

    private string chartInfoHydrationPendingReason
    {
        get => catalogChartInfoOwner.HydrationPendingReason;
        set => catalogChartInfoOwner.HydrationPendingReason = value;
    }

    private bool chartInfoHydrationPendingQueueBackfill
    {
        get => catalogChartInfoOwner.HydrationPendingQueueBackfill;
        set => catalogChartInfoOwner.HydrationPendingQueueBackfill = value;
    }

    private object lockChartInfoIndex => catalogChartInfoOwner.IndexGate;

    private object lockChartInfoLazyDisplayIndexLoad => catalogChartInfoOwner.LazyDisplayIndexGate;

    private Dictionary<string, LR2SongDBExtended.chart_info> chartInfoIndexBySha256
    {
        get => catalogChartInfoOwner.IndexBySha256;
        set => catalogChartInfoOwner.IndexBySha256 = value;
    }

    private Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> chartInfoIndexByMd5
    {
        get => catalogChartInfoOwner.IndexByMd5;
        set => catalogChartInfoOwner.IndexByMd5 = value;
    }

    private bool chartInfoDisplayIndexLoaded
    {
        get => catalogChartInfoOwner.DisplayIndexLoaded;
        set => catalogChartInfoOwner.DisplayIndexLoaded = value;
    }

    private readonly object lockScoreSnapshot = new();

    private ScoreSnapshot scoreSnapshot;

    private int scoreSnapshotVersion;

    private readonly object lockPlaylistLibraryResolveIndexSnapshot = new();

    private PlaylistLibraryResolveIndexSnapshot playlistLibraryResolveIndexSnapshot;

    private int playlistLibraryResolveIndexSnapshotVersion;

    private int playlistLibraryResolveIndexInvalidationVersion;

    private int playlistLibraryResolveIndexInvalidationOwnedCollectionVersion;

    private int chartInfoBackfillRequestedVersion
    {
        get => catalogChartInfoOwner.ChartInfoBackfillRequestedVersionState;
        set => catalogChartInfoOwner.ChartInfoBackfillRequestedVersionState = value;
    }

    private int chartInfoBackfillCompletedVersion
    {
        get => catalogChartInfoOwner.ChartInfoBackfillCompletedVersionState;
        set => catalogChartInfoOwner.ChartInfoBackfillCompletedVersionState = value;
    }

    private int chartInfoBackfillHydrationBypassUntilVersion
    {
        get => catalogChartInfoOwner.ChartInfoBackfillHydrationBypassUntilVersion;
        set => catalogChartInfoOwner.ChartInfoBackfillHydrationBypassUntilVersion = value;
    }

    private readonly Lr2SynchronizationOwner lr2SynchronizationOwner;

    internal Lr2SynchronizationOwner Lr2Synchronization => lr2SynchronizationOwner;

    internal ILr2PlaylistFolderSynchronizationPort Lr2PlaylistFolderSynchronization => lr2SynchronizationOwner;

    private int chartInfoHydrationRequestedVersion
    {
        get => catalogChartInfoOwner.HydrationRequestedVersionState;
        set => catalogChartInfoOwner.HydrationRequestedVersionState = value;
    }

    private readonly object lockDeferredScoreHydration = new();

    private int deferredScoreHydrationRequestedVersion;

    private bool deferredScoreHydrationRunning;

    private int deferredScoreHydrationLastCompletedVersion;

    private readonly object lockDeferredRankingRefresh = new();

    private int deferredRankingRefreshRequestedVersion;

    private bool deferredRankingRefreshRunning;

    private int deferredRankingRefreshLastCompletedVersion;

    private readonly object lockIrScorePrefetch = new();

    private readonly CancellationTokenSource irScoreShutdownCancellation = new();

    private bool IrScorePrefetchRunning
    {
        get
        {
            lock (lockIrScorePrefetch)
            {
                return irScorePrefetchTask is { IsCompleted: false };
            }
        }
    }

    private int irScorePrefetchGeneration;

    private Task<IrScorePrefetchResult> irScorePrefetchTask;

    private int irScorePrefetchLr2Id;

    private string irScorePrefetchScoreDbPath;

    private bool irScorePrefetchEnabled;

    private readonly PropertyChangedSubscription listenerForRwlockBMSFilesInitializedAll;

    private readonly PropertyChangedSubscription listenerForRwlockBMSFilesInitializedMin;

    private readonly PropertyChangedSubscription listenerForRwlockDuplicateChartGroups;

    private readonly PropertyChangedSubscription listenerForRwlockPendingInstallCharts;

    private readonly PropertyChangedSubscription listenerForRwlockBMSFiles;

    private IReadOnlyList<BMSFile> _BMSFiles => catalogStorageRowsOwner.BmsRows;

    private IReadOnlyList<LR2SongDBExtended.bmson_song> _BmsonSongs => catalogStorageRowsOwner.BmsonRows;

    private int bmsStorageRowsVersion => catalogStorageRowsOwner.BmsRowsVersion;

    private int bmsonStorageRowsVersion => catalogStorageRowsOwner.BmsonRowsVersion;

    private readonly NormalLibraryRefreshPublisher normalLibraryRefreshPublisher = new();

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private List<DuplicateGroup> _DuplicateChartGroups;

    private List<string> bmsParentFolderListCache = [];

    private List<BMSScore> _BMSScores = [];

    private int _LR2ID;

    private bool _ScoreSnapshotReady;

    private int _ScoreSnapshotVersion;

    private bool _ScoreHydrationRunning;

    private int _ScoreHydrationRequestedVersion;

    private int _ScoreHydrationCompletedVersion;

    private bool _RankingRefreshRunning;

    private int _RankingRefreshRequestedVersion;

    private int _RankingRefreshCompletedVersion;

    private bool _ChartDigestBackfillRunning
    {
        get => catalogChartInfoOwner.ChartDigestBackfillRunning;
        set => catalogChartInfoOwner.ChartDigestBackfillRunning = value;
    }

    private int _ChartDigestBackfillRequestedVersion
    {
        get => catalogChartInfoOwner.ChartDigestBackfillRequestedVersion;
        set => catalogChartInfoOwner.ChartDigestBackfillRequestedVersion = value;
    }

    private int _ChartDigestBackfillCompletedVersion
    {
        get => catalogChartInfoOwner.ChartDigestBackfillCompletedVersion;
        set => catalogChartInfoOwner.ChartDigestBackfillCompletedVersion = value;
    }

    private int _ChartDigestBackfillTotalCount
    {
        get => catalogChartInfoOwner.ChartDigestBackfillTotalCount;
        set => catalogChartInfoOwner.ChartDigestBackfillTotalCount = value;
    }

    private int _ChartDigestBackfillProcessedCount
    {
        get => catalogChartInfoOwner.ChartDigestBackfillProcessedCount;
        set => catalogChartInfoOwner.ChartDigestBackfillProcessedCount = value;
    }

    private string _ChartDigestBackfillCurrentPath
    {
        get => catalogChartInfoOwner.ChartDigestBackfillCurrentPath;
        set => catalogChartInfoOwner.ChartDigestBackfillCurrentPath = value;
    }

    private bool _ChartInfoBackfillRunning
    {
        get => catalogChartInfoOwner.ChartInfoBackfillRunning;
        set => catalogChartInfoOwner.ChartInfoBackfillRunning = value;
    }

    private int _ChartInfoBackfillRequestedVersion
    {
        get => catalogChartInfoOwner.ChartInfoBackfillRequestedVersion;
        set => catalogChartInfoOwner.ChartInfoBackfillRequestedVersion = value;
    }

    private int _ChartInfoBackfillCompletedVersion
    {
        get => catalogChartInfoOwner.ChartInfoBackfillCompletedVersion;
        set => catalogChartInfoOwner.ChartInfoBackfillCompletedVersion = value;
    }

    private int _ChartInfoBackfillTotalCount
    {
        get => catalogChartInfoOwner.ChartInfoBackfillTotalCount;
        set => catalogChartInfoOwner.ChartInfoBackfillTotalCount = value;
    }

    private int _ChartInfoBackfillProcessedCount
    {
        get => catalogChartInfoOwner.ChartInfoBackfillProcessedCount;
        set => catalogChartInfoOwner.ChartInfoBackfillProcessedCount = value;
    }

    private int _ChartInfoBackfillDigestBackfilledCount
    {
        get => catalogChartInfoOwner.ChartInfoBackfillDigestBackfilledCount;
        set => catalogChartInfoOwner.ChartInfoBackfillDigestBackfilledCount = value;
    }

    private string _ChartInfoBackfillCurrentPath
    {
        get => catalogChartInfoOwner.ChartInfoBackfillCurrentPath;
        set => catalogChartInfoOwner.ChartInfoBackfillCurrentPath = value;
    }

    private ChartInfoHydrationAllCurrentSnapshot chartInfoHydrationAllCurrentSnapshot
    {
        get => catalogChartInfoOwner.HydrationAllCurrentSnapshot;
        set => catalogChartInfoOwner.HydrationAllCurrentSnapshot = value;
    }

    private bool _ChartInfoHydrationRunning
    {
        get => catalogChartInfoOwner.ChartInfoHydrationRunning;
        set => catalogChartInfoOwner.ChartInfoHydrationRunning = value;
    }

    private int _ChartInfoHydrationRequestedVersion
    {
        get => catalogChartInfoOwner.ChartInfoHydrationRequestedVersion;
        set => catalogChartInfoOwner.ChartInfoHydrationRequestedVersion = value;
    }

    private int _ChartInfoHydrationCompletedVersion
    {
        get => catalogChartInfoOwner.ChartInfoHydrationCompletedVersion;
        set => catalogChartInfoOwner.ChartInfoHydrationCompletedVersion = value;
    }

    private int _ChartInfoHydrationTotalCount
    {
        get => catalogChartInfoOwner.ChartInfoHydrationTotalCount;
        set => catalogChartInfoOwner.ChartInfoHydrationTotalCount = value;
    }

    private int _ChartInfoHydrationAppliedCount
    {
        get => catalogChartInfoOwner.ChartInfoHydrationAppliedCount;
        set => catalogChartInfoOwner.ChartInfoHydrationAppliedCount = value;
    }

    private LibraryInitializationProgressStage _LibraryInitializationProgressStage;

    private string _LibraryInitializationProgressScannerLabel = string.Empty;

    private int _LibraryInitializationProgressTotalCount;

    private int _LibraryInitializationProgressProcessedCount;

    private string _LibraryInitializationProgressCurrentPath = string.Empty;

    private LibraryInitializationProgressSnapshot publishedLibraryInitializationProgress =
        new(0L, LibraryInitializationProgressStage.None, string.Empty, 0, 0, string.Empty);

    private LibraryInitializationProgressSnapshot pendingLibraryInitializationProgress =
        new(0L, LibraryInitializationProgressStage.None, string.Empty, 0, 0, string.Empty);

    // A deferred UI dispatcher can leave the latest pending snapshot queued while
    // the mutation lane advances through the whole diff.  Retain only the first
    // strict LR2 FileDiff intermediate so that coalescing cannot erase the
    // observable start of a multi-item apply.  This is deliberately one bounded
    // slot, not a progress history or replay queue.
    private LibraryInitializationProgressSnapshot retainedLeadingLr2FileDiffProgress;

    private bool libraryInitializationProgressPublicationScheduled;

    private int _LibraryDatabaseLoadCompletedVersion;

    private int _LibraryFileEnumerationCompletedVersion;

    private int _LibraryFileDiffCompletedVersion;

    private long lastLibraryInitializationProgressReportTimestamp;

    private readonly object lockLibraryInitializationProgress = new();

    private int _ChartInfoIndexVersion
    {
        get => catalogChartInfoOwner.ChartInfoIndexVersion;
        set => catalogChartInfoOwner.ChartInfoIndexVersion = value;
    }

    private bool _ChartInfoIndexHydrated
    {
        get => catalogChartInfoOwner.ChartInfoIndexHydrated;
        set => catalogChartInfoOwner.ChartInfoIndexHydrated = value;
    }

    private bool _IsWriteLockHeldInitializeBMSFilesHealthStatus = true;

    private bool _IsWriteLockHeldInitializeBMSFilesEncodingInfo = true;

    private bool _IsWriteLockHeldInitializeBMSFilesZeroNote = true;

    private static readonly Regex lr2IRScoreRegex = new("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static readonly Uri rankingInfoUrl = new("http://www.ribbit.xyz/bms/services/lr2ircache/ranking");

    private static readonly Uri rankingDataUrl = new("http://www.ribbit.xyz/bms/services/lr2ircache/ranking/");

    private const int deferredScoreHydrationChunkSize = 4096;

    private const int deferredScoreHydrationChunkSlowLogThresholdMs = 500;

    private static readonly Regex customTrimStartRegex1 = new("^(\\d+(S|D)P|midi|bms|music)[.:・\\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex customTrimEndRegex1 = new("(^|\\s+)[\\-!\"#$%&`'(,./:;<=>?@\\[^_~|]+$", RegexOptions.Compiled);

    private static readonly Regex customTrimEndRegex2 = new("\\s?[\\[(](\\d+|H|SP?|\\d+key[^\\s]*|hard])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex customDeleteRegex1 = new("(^|[\\[［/(（<＜\\s]+)((.?obj|mov&obj|bga|midi|movie|object|mov|image|bgi|illust(ration|rated)?|差分|LYRICS|イラスト|絵|#include)([.:・\\s]+.*$|$)|(bms|layer|visual):.*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex customMatchRegex1 = new("\\s+[\u3000！“”＃＄％＆‘’（）＊＋，－．／：；＜＝＞？＠［￥］\uff3e\uff3f\uffe3]+\\s+", RegexOptions.Compiled);

    private static readonly Regex customMatchRegex2 = new("〔(.*)〕", RegexOptions.Compiled);

    private static readonly Regex doubleSpacesRegex = new("\\s{2,}", RegexOptions.Compiled);

    private static readonly Regex endKakkoRegex = new("\\s*([-].*[-]|[`'].*[`']|[\"].*[\"]|[～].*[～]|[－].*[－]|[‘’].*[‘’]|[“”].*[“”]|[<].*[>]|[{].*[}]|[\\(].*[\\)]|[\\[].*[\\]]|[＜].*[＞]|[（].*[）]|[「].*[」]|[【].*[】]|[『].*[』]|[［].*[］]|[〈].*[〉]|[《].*[》]|[〔].*[〕]|[｛].*[｝])$", RegexOptions.Compiled);

    private static readonly Regex kakkoInnerRegex = new("(PMS|SP|ANOTHER|HYPER|NORMAL|EMPTY|DP|BGA|4[^\\d]|5[^\\d]|7[^\\d]|9[^\\d]|10[^\\d]|14[^\\d])[^\\w]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// ライブラリが管理する全 BMS ファイルの一覧です。
    /// セッターでは関連する snapshot / index / cache を自動的にリセットします。
    /// </summary>
    public IReadOnlyList<BMSFile> BMSFiles
    {
        get
        {
            return catalogStorageRowsOwner.GetBmsRowsReadOnly();
        }
        internal set
        {
            using LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
                "catalog_storage_rows",
                showMessage: false);
            if (mutationReservation == null)
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }
            ApplyCatalogStorageRows(
                value,
                BmsonSongs,
                replaceBmsRows: true,
                replaceBmsonRows: false,
                notifyBmsRows: true,
                notifyBmsonRows: false);
        }
    }

    /// <summary>
    /// Replaces the selected raw catalog storage rows through the canonical storage-row owner.
    /// The mutation owner applies the raw rows and invalidates the derived owned collection;
    /// this facade composes cache, resource-health, and per-kind notification ordering.
    /// </summary>
    private void ApplyCatalogStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool replaceBmsRows,
        bool replaceBmsonRows,
        bool notifyBmsRows,
        bool notifyBmsonRows,
        Action<Action> postLeaseNotificationObserver = null)
    {
        List<BMSFile> normalizedBmsRows = NormalizeBmsStorageRows(bmsFiles);
        List<LR2SongDBExtended.bmson_song> normalizedBmsonRows = NormalizeBmsonStorageRows(bmsonSongs);
        CatalogStorageRowsReplacementRequest request = catalogMutationOwner.CreateStorageRowsReplacementRequest(
            normalizedBmsRows,
            normalizedBmsonRows,
            replaceBmsRows,
            replaceBmsonRows);
        bool bmsRowsChanged = request.BmsRowsChanged;
        bool bmsonRowsChanged = request.BmsonRowsChanged;
        if (!bmsRowsChanged && !bmsonRowsChanged)
        {
            return;
        }

        CatalogStorageRowsReplacementReceipt replacementReceipt;
        Action publishReplacementEffects = null;
        using (resourceHealthOwner.BeginInputMutation())
        {
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            replacementReceipt = catalogMutationOwner.ApplyStorageRowsReplacement(request);
            bmsRowsChanged = replacementReceipt.BmsRowsChanged;
            bmsonRowsChanged = replacementReceipt.BmsonRowsChanged;
            if (bmsRowsChanged)
            {
                MarkDuplicateWarningFullClearPending();
            }
            int ownedCollectionVersion = replacementReceipt.OwnedCollectionVersion;
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            InvalidateInstalledDirectoryIndex();
            InvalidateBMSParentFolderListCache();
            if (bmsonRowsChanged)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
            InvalidateDuplicateChartGroupsCache();
            resourceHealthOwner.Invalidate(bmsRowsChanged && bmsonRowsChanged
                ? "catalog_storage_rows_changed"
                : (bmsRowsChanged ? "bmsfiles_changed" : "bmsons_changed"));
            installDestinationStateOwner.PruneToCurrentOwnedCharts();

            publishReplacementEffects = () =>
            {
                int publishedOwnedCollectionVersion = NotifyOwnedChartCollectionChanged(ownedCollectionVersion);
                InvalidatePlaylistLibraryResolveIndexSnapshot(publishedOwnedCollectionVersion);
                if ((notifyBmsRows && bmsRowsChanged) || (notifyBmsonRows && bmsonRowsChanged))
                {
                    PublishExternalReplacementNormalLibraryRefreshNotification(
                        notifiesBmsFiles: notifyBmsRows && bmsRowsChanged,
                        notifiesBmsonSongs: notifyBmsonRows && bmsonRowsChanged);
                }
                if (notifyBmsRows && bmsRowsChanged)
                {
                    Task.Run(delegate
                    {
                        RaisePropertyChanged("BMSFiles");
                    }).ObserveFault("BMSFiles");
                }
                if (notifyBmsonRows && bmsonRowsChanged)
                {
                    Task.Run(delegate
                    {
                        RaisePropertyChanged("BmsonSongs");
                    }).ObserveFault("BmsonSongs");
                }
                if ((notifyBmsRows && bmsRowsChanged) || (notifyBmsonRows && bmsonRowsChanged))
                {
                    RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
                }
            };
        }
        if (postLeaseNotificationObserver != null)
        {
            postLeaseNotificationObserver(publishReplacementEffects);
        }
        else
        {
            TryInvokePostLeaseNotification(
                publishReplacementEffects,
                "catalog_storage_rows_publication_failed");
        }
    }

    internal IEnumerable<ChartFile> ChartFilesUnregistered => CreateBmsChartSubsetSnapshot(
        BMSFiles.Where(file => file?.HasWarningCategory(ChartWarningCategory.Lr2Compatibility) == true));

    internal int NormalLibraryRefreshNotificationVersion => normalLibraryRefreshPublisher.Version;

    internal NormalLibraryRefreshNotificationBatch GetNormalLibraryRefreshNotificationsAfter(int handledVersion)
    {
        return normalLibraryRefreshPublisher.GetNotificationsAfter(handledVersion);
    }

    internal int OwnedChartCollectionVersion => catalogOwnedCollectionOwner.CollectionVersion;

    internal StorageRowsVersionSnapshot CatalogStorageRowsVersion => catalogStorageRowsOwner.CaptureVersionSnapshot();

    internal IEnumerable<ChartFile> ChartFilesNeedResourceFix => GetChartsNeedResourceFix(null);

    public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs
    {
        get
        {
            return catalogStorageRowsOwner.GetBmsonRowsReadOnly();
        }
        internal set
        {
            using LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
                "catalog_storage_rows",
                showMessage: false);
            if (mutationReservation == null)
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }
            ApplyCatalogStorageRows(
                BMSFiles,
                value,
                replaceBmsRows: false,
                replaceBmsonRows: true,
                notifyBmsRows: false,
                notifyBmsonRows: true);
        }
    }

    internal IEnumerable<ChartFile> ChartFilesNeedResourceFixIgnored => GetChartsNeedResourceFix(null, forceUpdate: false, isInIgnoredList: true);

    /// <summary>
    /// 重複検出済みの chart group 一覧です。重複検出処理の結果が格納されます。
    /// </summary>
    public List<DuplicateGroup> DuplicateChartGroups
    {
        get
        {
            return _DuplicateChartGroups;
        }
        set
        {
            if (_DuplicateChartGroups != value)
            {
                _DuplicateChartGroups = value;
                Task.Run(delegate
                {
                    RaisePropertyChanged("DuplicateChartGroups");
                }).ObserveFault("DuplicateChartGroups");
            }
        }
    }

    internal int DuplicateChartGroupsInvalidationVersion => Volatile.Read(ref duplicateChartGroupsInvalidationVersion);

    private bool InvalidateDuplicateChartGroupsCache(bool publishNotification = true)
    {
        if (_DuplicateChartGroups == null)
        {
            return false;
        }
        _DuplicateChartGroups = null;
        Interlocked.Increment(ref duplicateChartGroupsInvalidationVersion);
        if (publishNotification)
        {
            Task.Run(delegate
            {
                RaisePropertyChanged(() => DuplicateChartGroupsInvalidationVersion);
            }).ObserveFault("DuplicateChartGroupsInvalidationVersion");
        }
        return true;
    }

    private void MarkDuplicateWarningFullClearPending()
    {
        duplicateWarningBmsOwners = [];
        duplicateWarningFullClearPending = true;
    }

    internal IEnumerable<ChartFile> ChartFilesGarbled => CreateBmsChartSubsetSnapshot(
        GetGarbledBmsStorageRows(BMSFiles, isInFixedList: false));

    internal IEnumerable<ChartFile> ChartFilesGarbledFixed => CreateBmsChartSubsetSnapshot(
        GetGarbledBmsStorageRows(BMSFiles, isInFixedList: true));

    internal IEnumerable<ChartFile> ChartFilesZeroNote => maintenanceService.GetZeroNoteCharts(
        CreateOwnedBmsChartFilesUnsafe(includeResourceReferences: false),
        ResolveChartInfoForChart);

    internal IEnumerable<ChartFile> ChartInfoParseFailedChartFiles => GetChartInfoParseFailedChartFiles();

    /// <summary>
    /// インストール待ち（Pending状態）の chart package を読み取り専用で公開します。
    /// コレクションの差し替えと変更通知は package lifecycle owner が行います。
    /// </summary>
    public IReadOnlyCollection<ChartPackage> ChartPackagesPending
    {
        get => packageLifecycleOwner.PendingPackagesView;
        internal set
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            packageLifecycleOwner.SetPendingPackages(
                value as ObservableCollection<ChartPackage>
                ?? new ObservableCollection<ChartPackage>(value));
        }
    }

    internal ReadOnlyObservableCollection<ChartPackage> PendingPackagesView
        => packageLifecycleOwner.PendingPackagesView;

    /// <summary>
    /// インストール済みの chart package のコレクションです。UIスレッドへのディスパッチに対応しています。
    /// </summary>
    public ObservableCollection<ChartPackage> ChartPackagesInstalled
    {
        get => packageLifecycleOwner.InstalledPackages;
        internal set => packageLifecycleOwner.SetInstalledPackages(value);
    }

    /// <summary>
    /// 親フォルダ一覧キャッシュの世代です。
    /// キャッシュ無効化のたびに増加し、UI 側はこの値の変化を契機に表示用ビューを再同期します。
    /// </summary>
    internal int BMSParentFolderListCacheVersion
    {
        get
        {
            lock (lockParentFolderList)
            {
                return bmsParentFolderListDirtyVersion;
            }
        }
    }

    /// <summary>
    /// 親フォルダ一覧キャッシュを無効化し、次回アクセス時に再構築が行われるようにマークします。
    /// </summary>
    private void InvalidateBMSParentFolderListCache()
    {
        lock (lockParentFolderList)
        {
            bmsParentFolderListDirty = true;
            bmsParentFolderListDirtyVersion++;
        }
    }

    /// <summary>
    /// BMS 検索ルートディレクトリの設定変更を親フォルダ一覧キャッシュへ反映するため、
    /// キャッシュを無効化して更新通知を発行します。
    /// </summary>
    internal void NotifyBMSDirectoriesChanged()
    {
        InvalidateBMSParentFolderListCacheAndNotify();
    }

    private void InvalidateBMSParentFolderListCacheAndNotify()
    {
        InvalidateBMSParentFolderListCache();
        NotifyBMSParentFolderListCacheChanged();
    }

    private void NotifyBMSParentFolderListCacheChanged()
    {
        RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
    }

    /// <summary>
    /// 親フォルダ一覧キャッシュが無効化されており再構築が必要かどうかを返します。
    /// </summary>
    internal bool IsBMSParentFolderListCacheDirty()
    {
        lock (lockParentFolderList)
        {
            return bmsParentFolderListDirty;
        }
    }

    private List<string> CreateInstalledChartPathSnapshotForParentFolderCache(
        Action<string, string> stageMarker = null)
    {
        stageMarker?.Invoke("model_reader_wait_start", null);
        Stopwatch readerWaitStopwatch = Stopwatch.StartNew();
        List<string> snapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            readerWaitStopwatch.Stop();
            snapshot = CreateOwnedChartPathSnapshotUnsafe();
        }
        stageMarker?.Invoke(
            "model_reader_wait_end",
            "elapsedMs=" + readerWaitStopwatch.ElapsedMilliseconds);
        return snapshot;
    }

    private List<string> CreateOwnedChartPathSnapshotUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreatePathSnapshot();
        }
    }

    /// <summary>
    /// インストール済み譜面のスナップショットから、親フォルダの候補リストを構築します。
    /// カスタムフォルダ出力先ディレクトリ配下は除外されます。
    /// </summary>
    private List<string> BuildBMSParentFolderCandidates(List<string> installedChartPaths)
    {
        return parentFolderCacheService.BuildParentFolderCandidates(getBMSDirectories(), installedChartPaths, CurrentOptionsSnapshot);
    }

    /// <summary>
    /// 親フォルダ一覧のキャッシュスナップショットをバックグラウンドで構築します。
    /// キャッシュが有効な場合は null を返します。
    /// </summary>
    internal ParentFolderListCacheSnapshot BuildBMSParentFolderListCacheSnapshot(
        Action<string, string> stageMarker = null)
    {
        int version = 0;
        lock (lockParentFolderList)
        {
            if (!bmsParentFolderListDirty)
            {
                return null;
            }
            version = bmsParentFolderListDirtyVersion;
        }
        List<string> installedChartPaths = CreateInstalledChartPathSnapshotForParentFolderCache(stageMarker);
        stageMarker?.Invoke("path_snapshot_complete", null);
        ParentFolderListCacheSnapshot snapshot = parentFolderCacheService.BuildSnapshot(
            version,
            installedChartPaths,
            getBMSDirectories(),
            CurrentOptionsSnapshot);
        stageMarker?.Invoke("cache_build_complete", null);
        return snapshot;
    }

    /// <summary>
    /// バックグラウンドで構築されたスナップショットを親フォルダ一覧キャッシュに適用します。
    /// バージョン不一致等でスキップされた場合は false を返します。
    /// </summary>
    internal bool TryApplyBMSParentFolderListCacheSnapshot(ParentFolderListCacheSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }
        lock (lockParentFolderList)
        {
            if (!bmsParentFolderListDirty)
            {
                return false;
            }
            if (snapshot.Version != bmsParentFolderListDirtyVersion)
            {
                return false;
            }
            IEnumerable<string> enumerable = snapshot.ParentFolders ?? Enumerable.Empty<string>();
            List<string> items = [.. enumerable.Except(bmsParentFolderListCache)];
            List<string> items2 = [.. bmsParentFolderListCache.Except(enumerable)];
            bmsParentFolderListCache = [.. enumerable];
            bmsParentFolderListDirty = false;
            LogInstallPerformance("parent_folder_cache rebuildMs=" + snapshot.RebuildMs + " added=" + items.Count + " removed=" + items2.Count + " total=" + bmsParentFolderListCache.Count);
            return true;
        }
    }

    /// <summary>
    /// 現在の親フォルダ候補スナップショットを返します。
    /// 必要に応じて同期的にキャッシュを再構築し、呼び出し側が UI 用ビューを独自に構築できるようにします。
    /// </summary>
    internal IReadOnlyList<string> GetBMSParentFolderListSnapshot()
    {
        while (true)
        {
            lock (lockParentFolderList)
            {
                if (!bmsParentFolderListDirty)
                {
                    return [.. bmsParentFolderListCache];
                }
            }

            ParentFolderListCacheSnapshot snapshot = BuildBMSParentFolderListCacheSnapshot();
            if (snapshot == null)
            {
                lock (lockParentFolderList)
                {
                    return [.. bmsParentFolderListCache];
                }
            }
            TryApplyBMSParentFolderListCacheSnapshot(snapshot);
        }
    }

    /// <summary>
    /// Returns the current parent-folder cache without rebuilding it.
    /// </summary>
    internal bool TryGetBMSParentFolderListCacheSnapshot(out IReadOnlyList<string> snapshot)
    {
        lock (lockParentFolderList)
        {
            if (bmsParentFolderListDirty)
            {
                snapshot = null;
                return false;
            }
            snapshot = [.. bmsParentFolderListCache];
            return true;
        }
    }

    private List<BMSScore> BMSScores
    {
        get
        {
            return _BMSScores;
        }
        set
        {
            if (_BMSScores != value)
            {
                _BMSScores = value;
                RaisePropertyChanged("BMSScores");
            }
        }
    }

    public int LR2ID
    {
        get
        {
            return _LR2ID;
        }
        private set
        {
            if (_LR2ID != value)
            {
                _LR2ID = value;
                RaisePropertyChanged("LR2ID");
            }
        }
    }

    /// <summary>
    /// score snapshot が利用可能かどうかを返します。
    /// score probe はこの状態が true であれば global hydration 完了を待たずに実行できます。
    /// </summary>
    public bool ScoreSnapshotReady
    {
        get
        {
            return _ScoreSnapshotReady;
        }
        private set
        {
            if (_ScoreSnapshotReady != value)
            {
                _ScoreSnapshotReady = value;
                RaisePropertyChanged(() => ScoreSnapshotReady);
            }
        }
    }

    /// <summary>
    /// 現在公開中の score snapshot 版数です。
    /// playlist detail view の再反映判定に利用します。
    /// </summary>
    public int ScoreSnapshotVersion
    {
        get
        {
            return _ScoreSnapshotVersion;
        }
        private set
        {
            if (_ScoreSnapshotVersion != value)
            {
                _ScoreSnapshotVersion = value;
                RaisePropertyChanged(() => ScoreSnapshotVersion);
            }
        }
    }

    /// <summary>
    /// deferred score hydration worker が現在実行中かどうかを返します。
    /// </summary>
    public bool ScoreHydrationRunning
    {
        get
        {
            return _ScoreHydrationRunning;
        }
        private set
        {
            if (_ScoreHydrationRunning != value)
            {
                _ScoreHydrationRunning = value;
                RaisePropertyChanged(() => ScoreHydrationRunning);
            }
        }
    }

    /// <summary>
    /// 最後に要求された deferred score hydration の版数です。
    /// </summary>
    public int ScoreHydrationRequestedVersion
    {
        get
        {
            return _ScoreHydrationRequestedVersion;
        }
        private set
        {
            if (_ScoreHydrationRequestedVersion != value)
            {
                _ScoreHydrationRequestedVersion = value;
                RaisePropertyChanged(() => ScoreHydrationRequestedVersion);
            }
        }
    }

    /// <summary>
    /// 最後に完了した deferred score hydration の版数です。
    /// </summary>
    public int ScoreHydrationCompletedVersion
    {
        get
        {
            return _ScoreHydrationCompletedVersion;
        }
        private set
        {
            if (_ScoreHydrationCompletedVersion != value)
            {
                _ScoreHydrationCompletedVersion = value;
                RaisePropertyChanged(() => ScoreHydrationCompletedVersion);
            }
        }
    }

    /// <summary>
    /// deferred ranking refresh worker が現在実行中かどうかを返します。
    /// </summary>
    public bool RankingRefreshRunning
    {
        get
        {
            return _RankingRefreshRunning;
        }
        private set
        {
            if (_RankingRefreshRunning != value)
            {
                _RankingRefreshRunning = value;
                RaisePropertyChanged(() => RankingRefreshRunning);
            }
        }
    }

    /// <summary>
    /// 最後に要求された deferred ranking refresh の版数です。
    /// </summary>
    public int RankingRefreshRequestedVersion
    {
        get
        {
            return _RankingRefreshRequestedVersion;
        }
        private set
        {
            if (_RankingRefreshRequestedVersion != value)
            {
                _RankingRefreshRequestedVersion = value;
                RaisePropertyChanged(() => RankingRefreshRequestedVersion);
            }
        }
    }

    /// <summary>
    /// 最後に完了した deferred ranking refresh の版数です。
    /// </summary>
    public int RankingRefreshCompletedVersion
    {
        get
        {
            return _RankingRefreshCompletedVersion;
        }
        private set
        {
            if (_RankingRefreshCompletedVersion != value)
            {
                _RankingRefreshCompletedVersion = value;
                RaisePropertyChanged(() => RankingRefreshCompletedVersion);
            }
        }
    }

    public int PendingEstimateQueueStatusVersion
    {
        get => packageLifecycleOwner.PendingEstimateQueueStatusVersion;
    }

    public int InstallEstimationProgressVersion
    {
        get => packageLifecycleOwner.InstallEstimationProgressVersion;
    }

    /// <summary>
    /// installable maintenance deferred worker が現在実行中かどうかを返します。
    /// </summary>
    public bool InstallableMaintenanceDeferredRunning
    {
        get => packageLifecycleOwner?.IsInstallableMaintenanceRunning == true;
    }

    /// <summary>
    /// 最後に要求された installable maintenance deferred worker の版数です。
    /// </summary>
    public int InstallableMaintenanceDeferredRequestedVersion
    {
        get => packageLifecycleOwner?.InstallableMaintenanceRequestedVersion ?? 0;
    }

    /// <summary>
    /// 最後に完了した installable maintenance deferred worker の版数です。
    /// </summary>
    public int InstallableMaintenanceDeferredCompletedVersion
    {
        get => packageLifecycleOwner?.InstallableMaintenanceCompletedVersion ?? 0;
    }

    public bool MaintenanceHydrationRunning
    {
        get
        {
            return catalogMaintenanceOwner?.HydrationRunning == true;
        }
    }

    public int MaintenanceHydrationRequestedVersion
    {
        get
        {
            return catalogMaintenanceOwner?.HydrationRequestedVersion ?? 0;
        }
    }

    public int MaintenanceHydrationCompletedVersion
    {
        get
        {
            return catalogMaintenanceOwner?.HydrationCompletedVersion ?? 0;
        }
    }

    private void NotifyMaintenanceHydrationStateChanged()
    {
        RaisePropertyChanged(() => MaintenanceHydrationRunning);
        RaisePropertyChanged(() => MaintenanceHydrationRequestedVersion);
        RaisePropertyChanged(() => MaintenanceHydrationCompletedVersion);
    }

    public bool ChartDigestBackfillRunning
    {
        get
        {
            return _ChartDigestBackfillRunning;
        }
        private set
        {
            if (_ChartDigestBackfillRunning != value)
            {
                _ChartDigestBackfillRunning = value;
            }
        }
    }

    public int ChartDigestBackfillRequestedVersion
    {
        get
        {
            return _ChartDigestBackfillRequestedVersion;
        }
        private set
        {
            if (_ChartDigestBackfillRequestedVersion != value)
            {
                _ChartDigestBackfillRequestedVersion = value;
            }
        }
    }

    public int ChartDigestBackfillCompletedVersion
    {
        get
        {
            return _ChartDigestBackfillCompletedVersion;
        }
        private set
        {
            if (_ChartDigestBackfillCompletedVersion != value)
            {
                _ChartDigestBackfillCompletedVersion = value;
            }
        }
    }

    public int ChartDigestBackfillTotalCount
    {
        get
        {
            return _ChartDigestBackfillTotalCount;
        }
        private set
        {
            if (_ChartDigestBackfillTotalCount != value)
            {
                _ChartDigestBackfillTotalCount = value;
            }
        }
    }

    public int ChartDigestBackfillProcessedCount
    {
        get
        {
            return _ChartDigestBackfillProcessedCount;
        }
        private set
        {
            if (_ChartDigestBackfillProcessedCount != value)
            {
                _ChartDigestBackfillProcessedCount = value;
            }
        }
    }

    public string ChartDigestBackfillCurrentPath
    {
        get
        {
            return _ChartDigestBackfillCurrentPath;
        }
        private set
        {
            value ??= string.Empty;
            if (_ChartDigestBackfillCurrentPath != value)
            {
                _ChartDigestBackfillCurrentPath = value;
            }
        }
    }

    /// <summary>
    /// chart_info のバックグラウンド構築が実行中かどうかです。
    /// </summary>
    public bool ChartInfoBackfillRunning
    {
        get
        {
            return _ChartInfoBackfillRunning;
        }
        private set
        {
            if (_ChartInfoBackfillRunning != value)
            {
                _ChartInfoBackfillRunning = value;
            }
        }
    }

    /// <summary>
    /// chart_info 構築要求の版数です。
    /// </summary>
    public int ChartInfoBackfillRequestedVersion
    {
        get
        {
            return _ChartInfoBackfillRequestedVersion;
        }
        private set
        {
            if (_ChartInfoBackfillRequestedVersion != value)
            {
                _ChartInfoBackfillRequestedVersion = value;
            }
        }
    }

    /// <summary>
    /// 最後に完了した chart_info 構築要求の版数です。
    /// </summary>
    public int ChartInfoBackfillCompletedVersion
    {
        get
        {
            return _ChartInfoBackfillCompletedVersion;
        }
        private set
        {
            if (_ChartInfoBackfillCompletedVersion != value)
            {
                _ChartInfoBackfillCompletedVersion = value;
            }
        }
    }

    /// <summary>
    /// chart_info 構築対象の総件数です。
    /// </summary>
    public int ChartInfoBackfillTotalCount
    {
        get
        {
            return _ChartInfoBackfillTotalCount;
        }
        private set
        {
            if (_ChartInfoBackfillTotalCount != value)
            {
                _ChartInfoBackfillTotalCount = value;
            }
        }
    }

    /// <summary>
    /// chart_info 構築済み件数です。
    /// </summary>
    public int ChartInfoBackfillProcessedCount
    {
        get
        {
            return _ChartInfoBackfillProcessedCount;
        }
        private set
        {
            if (_ChartInfoBackfillProcessedCount != value)
            {
                _ChartInfoBackfillProcessedCount = value;
            }
        }
    }

    /// <summary>
    /// 最後に完了した chart_info 構築で SHA-256 digest を補完した BMS 譜面数です。
    /// </summary>
    public int ChartInfoBackfillDigestBackfilledCount
    {
        get
        {
            return _ChartInfoBackfillDigestBackfilledCount;
        }
        private set
        {
            if (_ChartInfoBackfillDigestBackfilledCount != value)
            {
                _ChartInfoBackfillDigestBackfilledCount = value;
            }
        }
    }

    /// <summary>
    /// chart_info 構築中の譜面パスです。
    /// </summary>
    public string ChartInfoBackfillCurrentPath
    {
        get
        {
            return _ChartInfoBackfillCurrentPath;
        }
        private set
        {
            value ??= string.Empty;
            if (_ChartInfoBackfillCurrentPath != value)
            {
                _ChartInfoBackfillCurrentPath = value;
            }
        }
    }

    public bool Lr2SongDbSyncRunning
    {
        get => lr2SynchronizationOwner.ObservableRunning;
    }

    public int Lr2SongDbSyncRequestedVersion
    {
        get => lr2SynchronizationOwner.ObservableRequestedVersion;
    }

    public int Lr2SongDbSyncCompletedVersion
    {
        get => lr2SynchronizationOwner.ObservableCompletedVersion;
    }

    public int Lr2SongDbSyncFailedVersion
    {
        get => lr2SynchronizationOwner.ObservableFailedVersion;
    }

    public int Lr2SongDbSyncTotalCount
    {
        get => lr2SynchronizationOwner.ObservableTotalCount;
    }

    public int Lr2SongDbSyncProcessedCount
    {
        get => lr2SynchronizationOwner.ObservableProcessedCount;
    }

    public string Lr2SongDbSyncStage
    {
        get => lr2SynchronizationOwner.ObservableStage;
    }

    public int Lr2SongDbSyncStageProcessedCount
    {
        get => lr2SynchronizationOwner.ObservableStageProcessedCount;
    }

    public int Lr2SongDbSyncStageTotalCount
    {
        get => lr2SynchronizationOwner.ObservableStageTotalCount;
    }

    public string Lr2SongDbSyncFailureMessage
    {
        get => lr2SynchronizationOwner.ObservableFailureMessage;
    }

    public int Lr2SongDbSyncStatusVersion
    {
        get => lr2SynchronizationOwner.ObservableStatusVersion;
    }

    internal Lr2SongDbSyncStatusSnapshot GetLr2SongDbSyncStatusSnapshot()
    {
        return lr2SynchronizationOwner.GetStatusSnapshot();
    }

    public LibraryInitializationProgressStage LibraryInitializationProgress
    {
        get
        {
            return _LibraryInitializationProgressStage;
        }
        private set
        {
            if (_LibraryInitializationProgressStage != value)
            {
                _LibraryInitializationProgressStage = value;
                RaisePropertyChanged(() => LibraryInitializationProgress);
            }
        }
    }

    public string LibraryInitializationProgressScannerLabel
    {
        get
        {
            return _LibraryInitializationProgressScannerLabel;
        }
        private set
        {
            value ??= string.Empty;
            if (_LibraryInitializationProgressScannerLabel != value)
            {
                _LibraryInitializationProgressScannerLabel = value;
                RaisePropertyChanged(() => LibraryInitializationProgressScannerLabel);
            }
        }
    }

    public int LibraryInitializationProgressTotalCount
    {
        get
        {
            return _LibraryInitializationProgressTotalCount;
        }
        private set
        {
            if (_LibraryInitializationProgressTotalCount != value)
            {
                _LibraryInitializationProgressTotalCount = value;
                RaisePropertyChanged(() => LibraryInitializationProgressTotalCount);
            }
        }
    }

    public int LibraryInitializationProgressProcessedCount
    {
        get
        {
            return _LibraryInitializationProgressProcessedCount;
        }
        private set
        {
            if (_LibraryInitializationProgressProcessedCount != value)
            {
                _LibraryInitializationProgressProcessedCount = value;
                RaisePropertyChanged(() => LibraryInitializationProgressProcessedCount);
            }
        }
    }

    public string LibraryInitializationProgressCurrentPath
    {
        get
        {
            return _LibraryInitializationProgressCurrentPath;
        }
        private set
        {
            value ??= string.Empty;
            if (_LibraryInitializationProgressCurrentPath != value)
            {
                _LibraryInitializationProgressCurrentPath = value;
                RaisePropertyChanged(() => LibraryInitializationProgressCurrentPath);
            }
        }
    }

    public long LibraryInitializationProgressVersion =>
        Volatile.Read(ref publishedLibraryInitializationProgress).Version;

    public LibraryInitializationProgressSnapshot GetLibraryInitializationProgressSnapshot()
    {
        return Volatile.Read(ref publishedLibraryInitializationProgress);
    }

    public int LibraryDatabaseLoadCompletedVersion
    {
        get
        {
            return _LibraryDatabaseLoadCompletedVersion;
        }
        private set
        {
            if (_LibraryDatabaseLoadCompletedVersion != value)
            {
                _LibraryDatabaseLoadCompletedVersion = value;
                RaisePropertyChanged(() => LibraryDatabaseLoadCompletedVersion);
            }
        }
    }

    public int LibraryFileEnumerationCompletedVersion
    {
        get
        {
            return _LibraryFileEnumerationCompletedVersion;
        }
        private set
        {
            if (_LibraryFileEnumerationCompletedVersion != value)
            {
                _LibraryFileEnumerationCompletedVersion = value;
                RaisePropertyChanged(() => LibraryFileEnumerationCompletedVersion);
            }
        }
    }

    public int LibraryFileDiffCompletedVersion
    {
        get
        {
            return _LibraryFileDiffCompletedVersion;
        }
        private set
        {
            if (_LibraryFileDiffCompletedVersion != value)
            {
                _LibraryFileDiffCompletedVersion = value;
                RaisePropertyChanged(() => LibraryFileDiffCompletedVersion);
            }
        }
    }

    /// <summary>
    /// chart_info の既存行をメモリへ遅延適用中かどうかです。
    /// </summary>
    public bool ChartInfoHydrationRunning
    {
        get
        {
            return _ChartInfoHydrationRunning;
        }
        private set
        {
            if (_ChartInfoHydrationRunning != value)
            {
                _ChartInfoHydrationRunning = value;
            }
        }
    }

    /// <summary>
    /// chart_info 遅延適用要求の版数です。
    /// </summary>
    public int ChartInfoHydrationRequestedVersion
    {
        get
        {
            return _ChartInfoHydrationRequestedVersion;
        }
        private set
        {
            if (_ChartInfoHydrationRequestedVersion != value)
            {
                _ChartInfoHydrationRequestedVersion = value;
            }
        }
    }

    /// <summary>
    /// 最後に完了した chart_info 遅延適用要求の版数です。
    /// </summary>
    public int ChartInfoHydrationCompletedVersion
    {
        get
        {
            return _ChartInfoHydrationCompletedVersion;
        }
        private set
        {
            if (_ChartInfoHydrationCompletedVersion != value)
            {
                _ChartInfoHydrationCompletedVersion = value;
            }
        }
    }

    /// <summary>
    /// 読み込んだ chart_info 行数です。
    /// </summary>
    public int ChartInfoHydrationTotalCount
    {
        get
        {
            return _ChartInfoHydrationTotalCount;
        }
        private set
        {
            if (_ChartInfoHydrationTotalCount != value)
            {
                _ChartInfoHydrationTotalCount = value;
            }
        }
    }

    /// <summary>
    /// メモリ上の譜面へ適用した chart_info 件数です。
    /// </summary>
    public int ChartInfoHydrationAppliedCount
    {
        get
        {
            return _ChartInfoHydrationAppliedCount;
        }
        private set
        {
            if (_ChartInfoHydrationAppliedCount != value)
            {
                _ChartInfoHydrationAppliedCount = value;
            }
        }
    }

    /// <summary>
    /// このセッションで保持している chart_info index の版数です。
    /// hydration または backfill commit により、playlist 未所持行のメタデータ解決結果が変わるたびに進みます。
    /// </summary>
    public int ChartInfoIndexVersion
    {
        get
        {
            lock (lockChartInfoIndex)
            {
                return _ChartInfoIndexVersion;
            }
        }
    }

    /// <summary>
    /// chart_info index が DB snapshot で初期化済みかどうかです。
    /// </summary>
    public bool ChartInfoIndexHydrated
    {
        get
        {
            lock (lockChartInfoIndex)
            {
                return _ChartInfoIndexHydrated;
            }
        }
    }

    public bool IsWriteLockHeldInitializeAll
    {
        get
        {
            if (rwlockBMSFilesInitializedAll.LockingWriteCount == 0)
            {
                return rwlockBMSFilesInitializedAll.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeMin
    {
        get
        {
            if (rwlockBMSFilesInitializedMin.LockingWriteCount == 0)
            {
                return rwlockBMSFilesInitializedMin.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeBMSFiles
    {
        get
        {
            if (!IsWriteLockHeldInitializeMin && rwlockBMSFiles.LockingWriteCount == 0)
            {
                return rwlockBMSFiles.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializdBMSFilesHealthStatus
    {
        get
        {
            return _IsWriteLockHeldInitializeBMSFilesHealthStatus;
        }
        set
        {
            if (_IsWriteLockHeldInitializeBMSFilesHealthStatus != value)
            {
                _IsWriteLockHeldInitializeBMSFilesHealthStatus = value;
                RaisePropertyChanged("IsWriteLockHeldInitializdBMSFilesHealthStatus");
            }
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo
    {
        get
        {
            return _IsWriteLockHeldInitializeBMSFilesEncodingInfo;
        }
        set
        {
            if (_IsWriteLockHeldInitializeBMSFilesEncodingInfo != value)
            {
                _IsWriteLockHeldInitializeBMSFilesEncodingInfo = value;
                RaisePropertyChanged("IsWriteLockHeldInitializeBMSFilesEncodingInfo");
            }
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesZeroNote
    {
        get
        {
            return _IsWriteLockHeldInitializeBMSFilesZeroNote;
        }
        set
        {
            if (_IsWriteLockHeldInitializeBMSFilesZeroNote != value)
            {
                _IsWriteLockHeldInitializeBMSFilesZeroNote = value;
                RaisePropertyChanged("IsWriteLockHeldInitializeBMSFilesZeroNote");
            }
        }
    }

    public bool IsWriteLockHeldPendingInstallCharts
    {
        get
        {
            if (!IsWriteLockHeldInitializeAll && rwlockPendingInstallCharts.LockingWriteCount == 0)
            {
                return rwlockPendingInstallCharts.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldDuplicateChartGroups
    {
        get
        {
            if (rwlockDuplicateChartGroups.LockingWriteCount == 0)
            {
                return rwlockDuplicateChartGroups.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    private bool UseLR2 => CurrentOptionsSnapshot.OperationModeLR2DB;

    private readonly Lr2SearchRootSnapshotOwner lr2SearchRootSnapshotOwner;

    public List<string> SearchTargets
    {
        get => lr2SearchRootSnapshotOwner?.SearchTargets ?? [];
        set
        {
            if (lr2SearchRootSnapshotOwner != null)
            {
                lr2SearchRootSnapshotOwner.SearchTargets = value ?? [];
            }
        }
    }

    private readonly IFileMutationService fileMutationService;

    private readonly IBmsLibraryDialogService dialogService;

    private ScopedOperationDialogCoordinator scopedOperationDialogService;

    private readonly object everythingFallbackWarningGate = new();

    private long everythingFallbackWarningEpoch;

    private int everythingFallbackWarningQueued;
    private int fileScanSkippedIncompleteWarningQueued;
    private int emptyScanWithExistingDbWarningQueued;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly BmsLibraryDuplicateService duplicateService = new();

    private readonly BmsLibraryParentFolderCacheService parentFolderCacheService = new();

    private readonly BmsLibraryPlaylistReferenceOwner playlistReferenceOwner;

    private readonly BmsLibraryPackageInstallService packageInstallService = new();

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new();

    private readonly LibraryFileOperationSynchronization libraryFileOperationSynchronization;

    private readonly LibraryFileOperationOwner libraryFileOperationOwner;

    private readonly BmsLibraryIrService irService = new();

    private readonly BmsLibraryInitializationService initializationService = new();

    private readonly LibraryFileScanPipelineOwner libraryFileScanPipelineOwner;

    private readonly LibraryDirectoryPreflightService directoryPreflightService;

    private readonly InstallDestinationStateOwner installDestinationStateOwner;

    private ChartInfoBuildService chartInfoBuildService => catalogChartInfoOwner.BuildService;

    private readonly IBmsLibraryIrClient irClient;

    private readonly BmsLibraryMaintenanceService maintenanceService = new();

    private readonly PackageLifecycleOwner packageLifecycleOwner;

    private readonly string startupRequiredFileScanReason;

    private readonly Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider;

    private BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => optionsSnapshotProvider()
        ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");

    private BmsLibraryInstallEstimationService CreateInstallEstimationService()
    {
        return new BmsLibraryInstallEstimationService(CurrentOptionsSnapshot, innerWavHealthThreshForNormalBMSFile);
    }

    private BmsLibraryInstallEstimationService CreateInstallEstimationService(BmsLibraryOptionsSnapshot optionsSnapshot)
    {
        return new BmsLibraryInstallEstimationService(optionsSnapshot ?? CurrentOptionsSnapshot, innerWavHealthThreshForNormalBMSFile);
    }

    private const int PendingEstimateSourceBatchMaxRootsPerChunk = 256;

    private enum PendingInstallEstimateEvaluationOutcomeKind
    {
        NoOp,
        UnsupportedResourcePath,
        SourceSurfaceScanLimitExceeded,
        ResolvedInstalledDirectory,
        EstimatedResult,
        SkippedAsStale
    }

    private sealed class PendingInstallEstimateEvaluationRequest
    {
        public int OrderIndex { get; set; }

        public ChartPackage Package { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public List<PackageChartEntry> AlreadyInstalledEntries { get; set; } = [];

        public List<PackageChartEntry> MissingEntries { get; set; } = [];

        public ChartInstallationEstimateMode EstimateMode { get; set; }

        public bool WasPendingAtPreparation { get; set; }

        public PendingEstimateSourceBatchPackageState BatchState { get; set; }

        public bool HasMissingFiles => MissingEntries.Count > 0;

        public bool AttemptInstalledResolve => AlreadyInstalledEntries.Count > 0 && HasMissingFiles;
    }

    private sealed class PendingPackageChartEntryPartition
    {
        public List<PackageChartEntry> PackageEntries { get; } = [];

        public List<PackageChartEntry> AlreadyInstalledEntries { get; } = [];

        public List<PackageChartEntry> MissingEntries { get; } = [];
    }

    private sealed class PendingInstallEstimateEvaluationContext
    {
        public IInstalledChartLookupIndex InstalledChartLookupIndex { get; set; } = new InstalledChartLookupIndexSnapshot();

        public LibraryResourceIndexSnapshot ResourceIndexSnapshot { get; set; }

        public DirectoryResourceLookupCache DirectoryLookupCacheSnapshot => ResourceIndexSnapshot.DirectoryLookupCache;

        public PendingInstallEstimateCurrentnessStamp CurrentnessStamp { get; set; }

        public BmsLibraryOptionsSnapshot OptionsSnapshot { get; set; } = new BmsLibraryOptionsSnapshot();
    }

    private sealed class InstallEstimationEvaluationData
    {
        public int ChartCount { get; set; }

        public InstallEstimationResult Result { get; set; }

        public long LazyHashBuildMsDelta { get; set; }

        public long LazyHashLookupCountDelta { get; set; }

        public int LazyHashEntriesAdded { get; set; }

        public string LazyHashBuildReason { get; set; } = "demand";

        public long SourceSurfaceScanMs { get; set; }

        public int SourceSurfaceChartFileCount { get; set; }

        public int SourceSurfaceResourceFileCount { get; set; }

        public int SourceSurfaceTrackedFileCount { get; set; }

        public long SourceSurfaceHashMaterializeMs { get; set; }

        public bool SourceSurfaceCacheHit { get; set; }

        public bool SourceSurfaceBatchHit { get; set; }

        public string SourceSurfaceScanBackend { get; set; } = string.Empty;

        public bool SourceSurfaceScanLimitExceeded { get; set; }

        public int SourceSurfaceVisitedFileSystemEntryCount { get; set; }

        public int SourceSurfaceMaxVisitedFileSystemEntryCount { get; set; }
    }

    private sealed class PendingInstallEstimateEvaluationResult
    {
        public PendingInstallEstimateEvaluationRequest Request { get; set; }

        public PendingInstallEstimateEvaluationOutcomeKind OutcomeKind { get; set; }

        public string ResolvedDirectory { get; set; }

        public InstalledDirectoryLookupResult InstalledResolution { get; set; }

        public InstallEstimationEvaluationData EstimationData { get; set; }

        public PendingInstallEstimateCurrentnessStamp CurrentnessStamp { get; set; }
    }

    private sealed class PendingInstallEstimateRetryCapture
    {
        public PendingInstallEstimateEvaluationRequest Request { get; init; }

        public PendingInstallEstimateEvaluationContext Context { get; init; }
    }

    private sealed class PendingInstallEstimateBatchCapture
    {
        public PendingInstallEstimateEvaluationContext Context { get; init; }

        public List<PendingInstallEstimateEvaluationRequest> Requests { get; init; } = [];
    }

    /// <summary>
    /// Creates the library facade for a normal application composition.
    /// </summary>
    /// <param name="chartFileScanner">Optional captured chart scanner for deterministic internal fixtures; production callers leave it null.</param>
    /// <param name="rootFileEnumerator">Optional captured grouped enumerator for deterministic internal fixtures; production callers leave it null.</param>
    /// <param name="irClient">IR 通信境界。省略時は本文まで期限を適用する通常クライアント。</param>
    internal BMSLibrary(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        IUiScheduler uiScheduler,
        ApplicationPathSnapshot applicationPathSnapshot,
        IChartFileScanner chartFileScanner = null,
        IRootFileEnumerator rootFileEnumerator = null,
        IBmsLibraryIrClient irClient = null)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, null, null, startupRequiredFileScanReason, optionsSnapshotProvider, uiScheduler, applicationPathSnapshot, null, chartFileScanner, rootFileEnumerator, irClient)
    {
    }

    /// <summary>
    /// Creates the library facade and optionally attaches a typed install-estimation
    /// execution observer for internal behavior verification.
    /// </summary>
    /// <param name="installEstimationExecutionObserver">Optional diagnostic observer; production callers leave it null.</param>
    /// <param name="chartFileScanner">Optional captured chart scanner for deterministic internal fixtures; production callers leave it null.</param>
    /// <param name="rootFileEnumerator">Optional captured grouped enumerator for deterministic internal fixtures; production callers leave it null.</param>
    /// <param name="irClient">IR 通信境界。省略時は本文まで期限を適用する通常クライアント。</param>
    internal BMSLibrary(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        IUiScheduler uiScheduler,
        ApplicationPathSnapshot applicationPathSnapshot,
        IInstallEstimationExecutionObserver installEstimationExecutionObserver = null,
        IChartFileScanner chartFileScanner = null,
        IRootFileEnumerator rootFileEnumerator = null,
        IBmsLibraryIrClient irClient = null)
    {
        if (_lr2SongDB == null)
        {
            throw new ArgumentNullException("_LR2SongDB");
        }
        if (!LongPathFileSystem.FileExists(_lr2SongDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, _lr2SongDB), "_LR2SongDB");
        }
        if (_lr2ScoreDB != null && !LongPathFileSystem.FileExists(_lr2ScoreDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2ScoreDBNotFound, _lr2ScoreDB), "_lr2ScoreDB");
        }
        lr2SynchronizationRuntimeState = new(LogInstallPerformance);
        lr2config = getLR2Config ?? (() => null);
        playlistReferenceOwner = new(playlistReferenceApplyChunkSize);
        lr2SongDBPath = _lr2SongDB;
        lr2ScoreDBPath = _lr2ScoreDB;
        this.startupRequiredFileScanReason = startupRequiredFileScanReason;
        this.optionsSnapshotProvider = optionsSnapshotProvider
            ?? throw new ArgumentNullException(nameof(optionsSnapshotProvider));
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.installEstimationExecutionObserver = installEstimationExecutionObserver;
        this.irClient = irClient ?? new BmsLibraryIrClient();
        everythingNative = new EverythingNative(this.applicationPathSnapshot);
        this.fileMutationService = fileMutationService ?? new ResilientFileMutationService();
        this.dialogService = dialogService ?? new BmsLibraryDialogService();
        scopedOperationDialogService = new(this.dialogService);
        dbGateway = new BmsLibraryDbGateway(lr2SongDBPath, lr2ScoreDBPath);
        catalogChartInfoOwner = new(
            RaisePropertyChanged,
            () => IsShutdownRequested,
            TrySkipForShutdown,
            () => StartupBackgroundTaskScheduler,
            LogInstallPerformance);
        Lr2ChartInfoCapability lr2ChartInfoCapability = new(catalogChartInfoOwner);
        catalogMutationOwner = new(
            catalogStorageRowsOwner,
            catalogOwnedCollectionOwner,
            dbGateway);
        Lr2ConfigSnapshotProvider lr2ConfigSnapshotProvider = new(lr2config);
        lr2SearchRootSnapshotOwner = new(
            lr2ConfigSnapshotProvider,
            () => CurrentOptionsSnapshot);
        Lr2SynchronizationDataPort lr2SynchronizationDataPort = new(
            dbGateway,
            catalogMutationOwner,
            lr2ChartInfoCapability,
            catalogOwnedCollectionOwner,
            everythingNative,
            () => CurrentOptionsSnapshot,
            lr2SearchRootSnapshotOwner,
            lr2ConfigSnapshotProvider);
        Lr2SynchronizationRuntimePort lr2SynchronizationRuntimePort = new(
            lr2SynchronizationRuntimeState,
            scopedOperationDialogService);
        Lr2SynchronizationProjectionPort lr2SynchronizationProjectionPort = new(
            catalogStorageRowsOwner,
            lr2ChartInfoCapability,
            dbGateway,
            LogInstallPerformance);
        lr2SynchronizationOwner = new(
            lr2SynchronizationDataPort,
            lr2SynchronizationRuntimePort,
            lr2SynchronizationProjectionPort,
            QueueLr2ObservablePropertyChange);
        catalogWriteFailureSubscription = new(catalogMutationOwner, lr2SynchronizationOwner);
        catalogChartInfoOwner.ConfigureWorkflow(
            dbGateway,
            catalogMutationOwner,
            catalogStorageRowsOwner,
            catalogOwnedCollectionOwner,
            LogInstallPerformanceWarn,
            HandleCatalogChartInfoOwnerEvent,
            BeginOwnedDigestMutationWindow);
        resourceHealthOwner = new(
            maintenanceService,
            LogInstallPerformance,
            GetCurrentResourceHealthIndexVersion);
        installDestinationStateOwner = new(catalogStorageRowsOwner, CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe);
        directoryPreflightService = new LibraryDirectoryPreflightService();
        catalogMaintenanceOwner = new(
            initializationService,
            maintenanceService,
            catalogMutationOwner,
            dbGateway,
            resourceHealthOwner,
            () => CurrentOptionsSnapshot,
            CreateOwnedChartStorageOwnerViewUnsafe,
            CreateFullOwnedResourceMaintenanceTargetSet,
            catalogMutationOwner.EnterStorageRowsWriteGuard,
            libraryResourceIndexOwner,
            dialogService,
            () => IsShutdownRequested,
            TrySkipForShutdown,
            () => StartupBackgroundTaskScheduler,
            ReportStartupBackgroundTask,
            GetDisplayedExceptionMessage,
            PublishMaintenanceHydrationReceipt,
            LogInstallPerformance,
            NotifyMaintenanceHydrationStateChanged);
        libraryFileScanPipelineOwner = new LibraryFileScanPipelineOwner(
            dbGateway,
            catalogStorageRowsOwner,
            this.dialogService,
            () => everythingScanLoggingEnabled,
            ReportLibraryInitializationProgress,
            CompleteLibraryFileEnumerationProgress,
            CompleteLibraryFileDiffProgress,
            LogInstallPerformance,
            LogInstallPerformanceWarn,
            LogEverythingScan,
            LogStartupMemoryCheckpoint,
            GetDisplayedExceptionMessage,
            reason => { QueueEverythingFallbackWarning(reason); },
            reason => { QueueFileScanSkippedIncompleteWarning(reason); },
            reason => { QueueEmptyScanWithExistingDbWarning(reason); },
            Lr2Synchronization,
            catalogMutationOwner,
            catalogChartInfoOwner,
            resourceHealthOwner,
            ApplyFileScanCatalogReplacement,
            ApplyFileScanCatalogResidualForScan,
            initializationService,
            everythingNative,
            chartFileScanner,
            rootFileEnumerator,
            directoryPreflightService);
        packageLifecycleOwner = new PackageLifecycleOwner(
            dbGateway,
            uiScheduler,
            ProcessPendingInstallEstimateBatch,
            HandlePendingEstimateBatchException,
            propertyName => RaisePropertyChanged(propertyName),
            packages => new ObservableCollection<ChartPackage>(packages),
            () => RaisePropertyChanged(() => ChartPackagesInstalled),
            exception => NLogWrapper.FileLogger?.Warn(
                exception,
                "package_collection_post_guard_publication_failed"));
        libraryFileOperationSynchronization = new(
            new LibraryFileOperationMutationBoundary(lr2SynchronizationOwner),
            rwlockBMSFilesInitializedAll,
            rwlockBMSFilesInitializedMin,
            rwlockPendingInstallCharts,
            rwlockBMSFiles);
        libraryFileOperationOwner = new(
            libraryFileOperationSynchronization,
            libraryFileOperationsService,
            packageInstallService,
            packageLifecycleOwner,
            libraryResourceIndexOwner,
            this.fileMutationService,
            installDestinationStateOwner,
            scopedOperationDialogService,
            lr2SynchronizationOwner,
            catalogOwnedCollectionOwner,
            catalogStorageRowsOwner,
            CreateInstalledChartKeySnapshotExcludingChartsUnsafe,
            CreateInstalledChartLookupSnapshotUnsafe,
            CreateChartFolderPathFromCharts,
            GetDuplicateInstallRepairPaths,
            ApplyCatalogMaintenanceUnderExistingReservation,
            charts => ApplyCatalogMaintenance(
                charts,
                forceUpdate: true,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
                resourceHealthMutationReason: "merge_folder"),
            () =>
            {
                InvalidateDuplicateChartGroupsCache();
            },
            InvalidateInstalledDirectoryIndex,
            LogReverseLookupMutationAndQueueWarmupIfNeeded,
            LogInstallPerformance,
            LogInstallPerformanceWarn,
            info => NLogWrapper.FileLogger?.Info(info),
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message),
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions,
            ApplyLibraryMutationDeltaForFileMutationUnderExistingLease,
            ApplyLibraryMutationDeltaForFileMutationWithoutLr2NormalFolderSync);
        dbGateway.EnsureLibraryStartupSchema();
        listenerForRwlockBMSFilesInitializedAll = PropertyChangedSubscription.Create(rwlockBMSFilesInitializedAll);
        listenerForRwlockBMSFilesInitializedMin = PropertyChangedSubscription.Create(rwlockBMSFilesInitializedMin);
        listenerForRwlockDuplicateChartGroups = PropertyChangedSubscription.Create(rwlockDuplicateChartGroups);
        listenerForRwlockPendingInstallCharts = PropertyChangedSubscription.Create(rwlockPendingInstallCharts);
        listenerForRwlockBMSFiles = PropertyChangedSubscription.Create(rwlockBMSFiles);
        listenerForRwlockBMSFilesInitializedAll.RegisterHandler(() => rwlockBMSFilesInitializedAll.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeAll);
        });
        listenerForRwlockBMSFilesInitializedAll.RegisterHandler(() => rwlockBMSFilesInitializedAll.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldPendingInstallCharts);
        });
        listenerForRwlockBMSFilesInitializedMin.RegisterHandler(() => rwlockBMSFilesInitializedMin.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeMin);
        });
        listenerForRwlockBMSFilesInitializedMin.RegisterHandler(() => rwlockBMSFilesInitializedMin.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
        listenerForRwlockDuplicateChartGroups.RegisterHandler(() => rwlockDuplicateChartGroups.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldDuplicateChartGroups);
        });
        listenerForRwlockPendingInstallCharts.RegisterHandler(() => rwlockPendingInstallCharts.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldPendingInstallCharts);
        });
        listenerForRwlockBMSFiles.RegisterHandler(() => rwlockBMSFiles.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
    }

    private void QueueLr2ObservablePropertyChange(string propertyName)
    {
        long publicationVersion;
        lock (lr2PropertyPublicationGate)
        {
            pendingLr2PropertyNames.Add(propertyName);
            publicationVersion = ++lr2PropertyPublicationVersion;
            if (lr2PropertyPublicationScheduled)
            {
                return;
            }
            lr2PropertyPublicationScheduled = true;
        }

        ScheduleLr2PropertyChanges(publicationVersion);
    }

    private void ScheduleLr2PropertyChanges(
        long publicationVersion,
        UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        IUiScheduledOperation publication;
        try
        {
            publication = uiScheduler.Schedule(DrainLr2PropertyChanges, priority);
        }
        catch (Exception ex)
        {
            DiscardPendingLr2PropertyChanges();
            LogInstallPerformanceWarn(
                "lr2_sync_property_publication_schedule_failed version="
                + publicationVersion
                + " exception="
                + ex.GetType().Name);
            return;
        }
        if (!publication.IsAccepted)
        {
            DiscardPendingLr2PropertyChanges();
            LogInstallPerformanceWarn(
                "lr2_sync_property_publication_rejected version="
                + publicationVersion
                + " reason="
                + publication.RejectionReason);
            return;
        }
        _ = publication.Completion.ContinueWith(
            task =>
            {
                if (task.IsCanceled || publication.IsAborted)
                {
                    DiscardPendingLr2PropertyChanges();
                    LogInstallPerformanceWarn(
                        "lr2_sync_property_publication_canceled version=" + publicationVersion);
                    return;
                }
                LogInstallPerformanceWarn(
                    "lr2_sync_property_publication_failed version="
                    + publicationVersion
                    + " exception="
                    + (task.Exception?.GetBaseException().GetType().Name ?? "unknown"));
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DrainLr2PropertyChanges()
    {
        string[] propertyNames;
        lock (lr2PropertyPublicationGate)
        {
            propertyNames = [.. pendingLr2PropertyNames];
            pendingLr2PropertyNames.Clear();
        }

        foreach (string propertyName in propertyNames)
        {
            bool statusPublicationSelected = false;
            try
            {
                if (string.Equals(
                    propertyName,
                    nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                    StringComparison.Ordinal))
                {
                    statusPublicationSelected = lr2SynchronizationOwner.BeginLr2SongDbSyncStatusPublication();
                }
                RaisePropertyChanged(propertyName);
            }
            catch (Exception ex)
            {
                LogInstallPerformanceWarn(
                    "lr2_sync_property_subscriber_failed property="
                    + (propertyName ?? string.Empty)
                    + " exception="
                    + ex.GetType().Name
                    + " message="
                    + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }
            finally
            {
                if (statusPublicationSelected
                    && lr2SynchronizationOwner.EndLr2SongDbSyncStatusPublication())
                {
                    lock (lr2PropertyPublicationGate)
                    {
                        pendingLr2PropertyNames.Add(nameof(BMSLibrary.Lr2SongDbSyncStatusVersion));
                        lr2PropertyPublicationVersion++;
                        // The retained leading frame must be visible before
                        // the latest/terminal status is raised.  A Background
                        // continuation leaves the normal UI turn available
                        // for that frame without delaying the worker.
                        pendingLr2PropertyPublicationPriority = UiSchedulePriority.Background;
                    }
                }
            }
        }

        long nextPublicationVersion = 0;
        UiSchedulePriority nextPriority = UiSchedulePriority.Normal;
        lock (lr2PropertyPublicationGate)
        {
            if (pendingLr2PropertyNames.Count == 0)
            {
                lr2PropertyPublicationScheduled = false;
                pendingLr2PropertyPublicationPriority = UiSchedulePriority.Normal;
            }
            else
            {
                nextPublicationVersion = lr2PropertyPublicationVersion;
                nextPriority = pendingLr2PropertyPublicationPriority;
                pendingLr2PropertyPublicationPriority = UiSchedulePriority.Normal;
            }
        }
        if (nextPublicationVersion != 0)
        {
            ScheduleLr2PropertyChanges(nextPublicationVersion, nextPriority);
        }
    }

    private void DiscardPendingLr2PropertyChanges()
    {
        lock (lr2PropertyPublicationGate)
        {
            pendingLr2PropertyNames.Clear();
            lr2PropertyPublicationScheduled = false;
            pendingLr2PropertyPublicationPriority = UiSchedulePriority.Normal;
        }
        lr2SynchronizationOwner.DiscardLr2SongDbSyncStatusPublication();
    }

    /// <summary>
    /// 現在有効な score source 由来の BMS スコア情報のコピーを取得します。
    /// </summary>
    public List<BMSScore> GetBMSScores()
    {
        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
        if (snapshot != null)
        {
            if (snapshot.ActiveScoreSource == ActiveScoreSource.Beatoraja)
            {
                List<BMSScore> beatorajaScores = [];
                if (snapshot.ScoresBySha256 != null && snapshot.ScoresBySha256.Count > 0)
                {
                    List<BMSFile> bmsFilesSnapshot;
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        bmsFilesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
                    }
                    foreach (BMSFile file in bmsFilesSnapshot)
                    {
                        if (!string.IsNullOrWhiteSpace(file.hash)
                            && !string.IsNullOrWhiteSpace(file.sha256)
                            && snapshot.ScoresBySha256.TryGetValue(file.sha256, out BMSScore beatorajaScore))
                        {
                            beatorajaScores.Add(BmsLibraryIrService.CloneScoreForFileHash(beatorajaScore, file.hash));
                        }
                    }
                }
                return beatorajaScores;
            }
            if (snapshot.ActiveScoreSource == ActiveScoreSource.Lr2)
            {
                return [.. (snapshot.Scores ?? [])];
            }
            return [];
        }
        using (rwlockBMSScores.GetReaderGuard())
        {
            return BMSScores;
        }
    }

    /// <summary>
    /// 現在の score snapshot を playlist/diagnostics 向けに返します。
    /// 必要なら on-demand で構築します。
    /// </summary>
    /// <returns>現在の score snapshot。</returns>
    internal ScoreSnapshot GetScoreSnapshotForDiagnostics()
    {
        return GetScoreSnapshotForLookup(allowOnDemandBuild: true);
    }

    internal ActiveScoreSource GetActiveScoreSourceForDiagnostics()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return activeScoreSource;
        }
    }

    /// <summary>
    /// 現在の score source のロード状態を返します。
    /// </summary>
    /// <returns>score source のロード状態。</returns>
    internal ScoreTableLoadStatus GetScoreTableLoadStatusForDiagnostics()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return scoreTableLoadStatus;
        }
    }

    /// <summary>
    /// score source のロード失敗理由を返します。
    /// </summary>
    /// <returns>失敗時の診断メッセージ。失敗していない場合は空文字列。</returns>
    internal string GetScoreTableLoadFailureMessageForDiagnostics()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return scoreTableLoadFailureMessage ?? string.Empty;
        }
    }

    internal Lr2PlayHistorySchemaStatusSnapshot GetLr2PlayHistorySchemaStatusSnapshot()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return Lr2PlayHistorySchemaStatusSnapshot.FromResult(lr2PlayHistorySchemaCheckResult);
        }
    }

    /// <summary>
    /// score snapshot と deferred worker の診断状態を返します。
    /// </summary>
    /// <returns>現在の score runtime state。</returns>
    internal ScoreRuntimeState GetScoreRuntimeStateForDiagnostics()
    {
        ScoreSnapshot snapshot;
        lock (lockScoreSnapshot)
        {
            snapshot = scoreSnapshot;
        }
        lock (lockDeferredScoreHydration)
        {
            lock (lockDeferredRankingRefresh)
            {
                return new ScoreRuntimeState
                {
                    SnapshotReady = ScoreSnapshotReady,
                    SnapshotVersion = snapshot?.Version ?? 0,
                    ActiveScoreSource = snapshot?.ActiveScoreSource ?? ActiveScoreSource.None,
                    LoadStatus = snapshot?.LoadStatus ?? ScoreTableLoadStatus.NotConfigured,
                    LoadFailureMessage = snapshot?.LoadFailureMessage ?? string.Empty,
                    SourceGeneration = snapshot?.SourceGeneration ?? 0L,
                    HydrationRunning = deferredScoreHydrationRunning,
                    HydrationCompletedVersion = deferredScoreHydrationLastCompletedVersion,
                    RankingRefreshRunning = deferredRankingRefreshRunning,
                    RankingRefreshCompletedVersion = deferredRankingRefreshLastCompletedVersion
                };
            }
        }
    }

    internal PendingInstallEstimateQueueStatusSnapshot GetPendingEstimateQueueStatusSnapshot()
    {
        return packageLifecycleOwner.GetPendingEstimateQueueStatusSnapshot();
    }

    internal InstallEstimationProgressSnapshot GetInstallEstimationProgressSnapshot()
    {
        return packageLifecycleOwner.GetInstallEstimationProgressSnapshot();
    }

    private void SetInstallEstimationProgress(InstallEstimationProgressSource source, int totalWorkCount, int completedWorkCount, string currentDisplayName)
    {
        packageLifecycleOwner.SetInstallEstimationProgress(source, totalWorkCount, completedWorkCount, currentDisplayName);
        if (installEstimationExecutionObserver != null)
        {
            installEstimationExecutionObserver.ObserveProgress(new InstallEstimationProgressObservation(
                totalWorkCount > 0,
                source,
                Math.Max(totalWorkCount, 0),
                Math.Max(0, Math.Min(completedWorkCount, Math.Max(totalWorkCount, 0))),
                currentDisplayName ?? string.Empty));
        }
    }

    private void ClearInstallEstimationProgress()
    {
        packageLifecycleOwner.ClearInstallEstimationProgress();
        if (installEstimationExecutionObserver != null)
        {
            installEstimationExecutionObserver.ObserveProgress(new InstallEstimationProgressObservation(
                IsActive: false,
                Source: InstallEstimationProgressSource.None,
                TotalWorkCount: 0,
                CompletedWorkCount: 0,
                CurrentDisplayName: string.Empty));
        }
    }

    private static int GetPendingEstimateQueuedBatchCount(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.IsActive)
        {
            return 0;
        }
        return snapshot.PendingBatchCount + 1;
    }

    private void QueuePendingInstallEstimateBatch(PendingInstallEstimateBatchRequest request)
    {
        if (request == null || request.PackageCount == 0)
        {
            return;
        }
        if (TrySkipForShutdown("pending_estimate_batch", request.Source.ToString()))
        {
            return;
        }
        bool queued = packageLifecycleOwner.TryEnqueuePendingEstimateBatch(
            request,
            TrySkipForShutdown,
            () => LogPendingInstallEstimateAccepted(request));
        if (queued)
        {
            PendingInstallEstimateQueueStatusSnapshot snapshot = packageLifecycleOwner.GetPendingEstimateQueueStatusSnapshot();
            int queuedBatchCount = GetPendingEstimateQueuedBatchCount(snapshot);
            if (request.Source == PendingInstallEstimateBatchSource.StartupRestore)
            {
                LogInstallPerformance("startup_pending_estimate_queue queued batches=" + queuedBatchCount + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount);
            }
            else
            {
                LogInstallPerformance("pending_estimate_batch queued source=" + ToPendingEstimateBatchSourceLogValue(request.Source) + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " pendingBatches=" + snapshot.PendingBatchCount);
            }
        }
    }

    private static void LogPendingInstallEstimateAccepted(
        PendingInstallEstimateBatchRequest request)
    {
        if (!Net10PerformanceLog.IsEnabled)
        {
            return;
        }
        string details =
            "source=" + ToPendingEstimateBatchSourceLogValue(request.Source)
            + " packages=" + request.PackageCount;
        Net10PerformanceLog.Write(
            request.PerformanceInteraction,
            "input_accepted",
            details);
        Net10PerformanceLog.Write(
            request.PerformanceInteraction,
            "owner_queued",
            details);
    }

    private void ProcessPendingInstallEstimateBatch(PendingInstallEstimateBatchRequest request, CancellationToken token)
    {
        if (request == null || request.PackageCount == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        string source = ToPendingEstimateBatchSourceLogValue(request.Source);
        int lowConfidenceCount = 0;
        int completed = 0;
        var executionPolicy = InstallEstimationExecutionPolicy.ForPendingBatch(ResolvePendingInstallEstimateParallelPackageDegree());
        PerformanceInteraction performanceInteraction = request.PerformanceInteraction;
        PerformanceInteraction? firstVisibleInteraction = performanceInteraction;
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "owner_started",
                "source=" + source
                + " packages=" + request.PackageCount
                + " packageDegree=" + executionPolicy.WorkItemDegree);
        }
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " display=" + (request.DisplayName ?? string.Empty));
        if (!packageLifecycleOwner.TryEnterPendingOperation(out IDisposable pendingOperationLease))
        {
            throw new InvalidOperationException(
                "Pending estimate batch was deferred because a foreground pending operation is active.");
        }
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, 0, request.DisplayName ?? string.Empty);
                PendingInstallEstimateBatchCapture evaluationCapture =
                    CapturePendingInstallEstimateBatch(request);
                ProcessPendingInstallEstimateEvaluationPipeline(
                    request,
                    source,
                    token,
                    evaluationCapture.Context,
                    evaluationCapture.Requests,
                    executionPolicy,
                    ref completed,
                    ref lowConfidenceCount,
                    ref firstVisibleInteraction);
                if (!token.IsCancellationRequested && request.RegroupEligibleSourceDirectories.Length > 0)
                {
                    using IDisposable collectionMutationScope = packageLifecycleOwner.BeginCollectionMutationScope();
                    using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                    {
                        using (rwlockPendingInstallCharts.GetWriterGuard())
                        {
                            using (rwlockBMSFiles.GetReaderGuard())
                            {
                                TryRegroupPendingPackagesForSourceDirectoriesUnsafe(request.RegroupEligibleSourceDirectories);
                            }
                        }
                    }
                }
            });
            stopwatch.Stop();
            LogInstallPerformance("pending_estimate_batch done source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " estimated=" + completed + " completed=" + (completed + request.DeferredPackageCount) + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " lowConfidence=" + lowConfidenceCount);
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    performanceInteraction,
                    "result_applied",
                    "estimated=" + completed
                    + " deferred=" + request.DeferredPackageCount
                    + " lowConfidence=" + lowConfidenceCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
        }
        finally
        {
            pendingOperationLease.Dispose();
            ClearInstallEstimationProgress();
        }
    }

    private PendingInstallEstimateEvaluationContext CreatePendingInstallEstimateEvaluationContext()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return CreatePendingInstallEstimateEvaluationContextUnsafe();
            }
        }
    }

    private PendingInstallEstimateEvaluationContext CreatePendingInstallEstimateEvaluationContextUnsafe()
    {
        lock (pendingInstallEstimateCurrentnessGate)
        {
            return CreatePendingInstallEstimateEvaluationContextUnderCurrentnessGateUnsafe();
        }
    }

    private PendingInstallEstimateEvaluationContext CreatePendingInstallEstimateEvaluationContextUnderCurrentnessGateUnsafe()
    {
        LibraryResourceIndexSnapshot resourceSnapshot = libraryResourceIndexOwner.CaptureSnapshot();
        (InstalledChartLookupIndexSnapshot Snapshot, long Generation) installedSnapshot =
            CreateInstalledChartLookupVersionedSnapshotUnderCurrentnessGateUnsafe();
        return new PendingInstallEstimateEvaluationContext
        {
            InstalledChartLookupIndex = installedSnapshot.Snapshot,
            ResourceIndexSnapshot = resourceSnapshot,
            CurrentnessStamp = new PendingInstallEstimateCurrentnessStamp(
                resourceSnapshot.Generation,
                catalogOwnedCollectionOwner.CollectionVersion,
                installedSnapshot.Generation,
                ownedDigestMutationGeneration),
            OptionsSnapshot = CurrentOptionsSnapshot
        };
    }

    private bool IsPendingInstallEstimateCurrentUnderGateUnsafe(
        PendingInstallEstimateCurrentnessStamp stamp)
    {
        return libraryResourceIndexOwner.CaptureSnapshot().Generation == stamp.ResourceIndexGeneration
            && catalogOwnedCollectionOwner.CollectionVersion == stamp.OwnedCollectionVersion
            && installedChartLookupGeneration == stamp.InstalledLookupGeneration
            && ownedDigestMutationGeneration == stamp.DigestMutationGeneration
            && !IsOwnedDigestMutationWindowActive();
    }

    private PendingInstallEstimateBatchCapture CapturePendingInstallEstimateBatch(
        PendingInstallEstimateBatchRequest request)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    lock (pendingInstallEstimateCurrentnessGate)
                    {
                        PendingInstallEstimateEvaluationContext context =
                            CreatePendingInstallEstimateEvaluationContextUnderCurrentnessGateUnsafe();
                        List<PendingInstallEstimateEvaluationRequest> requests = [];
                        if (request == null)
                        {
                            return new PendingInstallEstimateBatchCapture
                            {
                                Context = context,
                                Requests = requests
                            };
                        }

                        int orderIndex = 0;
                        bool canReusePreparedState = request.BatchSourceSnapshot?.PackageStates.Count > 0
                            && request.BatchSourceSnapshot.PreparationCurrentnessStamp == context.CurrentnessStamp
                            && !IsOwnedDigestMutationWindowActive();
                        if (canReusePreparedState)
                        {
                            foreach (PendingEstimateSourceBatchPackageState state in request.BatchSourceSnapshot.PackageStates.Where(state => state?.Package != null))
                            {
                                requests.Add(new PendingInstallEstimateEvaluationRequest
                                {
                                    OrderIndex = orderIndex++,
                                    Package = state.Package,
                                    DisplayName = state.DisplayName,
                                    AlreadyInstalledEntries = [.. state.AlreadyInstalledEntries],
                                    MissingEntries = [.. state.MissingEntries],
                                    EstimateMode = state.EstimateMode,
                                    WasPendingAtPreparation = ChartPackagesPending.Contains(state.Package),
                                    BatchState = state
                                });
                            }
                        }
                        else
                        {
                            foreach (ChartPackage package in request.Packages)
                            {
                                PendingPackageChartEntryPartition partition = BuildPendingPackageChartEntryPartitionUnsafe(package);
                                requests.Add(new PendingInstallEstimateEvaluationRequest
                                {
                                    OrderIndex = orderIndex++,
                                    Package = package,
                                    DisplayName = PendingInstallEstimateBatchRequest.GetDisplayName(package?.path),
                                    AlreadyInstalledEntries = partition.AlreadyInstalledEntries,
                                    MissingEntries = partition.MissingEntries,
                                    EstimateMode = ChartInstallationEstimateMode.Normal,
                                    WasPendingAtPreparation = package != null && ChartPackagesPending.Contains(package),
                                    BatchState = null
                                });
                            }
                        }
                        return new PendingInstallEstimateBatchCapture
                        {
                            Context = context,
                            Requests = requests
                        };
                    }
                }
            }
        }
    }

    private PendingPackageChartEntryPartition BuildPendingPackageChartEntryPartitionUnsafe(ChartPackage package)
    {
        var partition = new PendingPackageChartEntryPartition();
        foreach (PackageChartEntry entry in package?.ChartEntries ?? [])
        {
            if (entry?.Chart == null)
            {
                continue;
            }
            bool isInstalled = ContainsInstalledChartUnsafe(entry.Chart);
            partition.PackageEntries.Add(entry);
            if (isInstalled)
            {
                partition.AlreadyInstalledEntries.Add(entry);
            }
            else
            {
                partition.MissingEntries.Add(entry);
            }
        }
        return partition;
    }

    internal static int ResolveInstallEstimationDefaultDegree()
    {
        return BmsLibraryInstallEstimationService.ResolveDefaultCandidateEvaluationDegree();
    }

    internal static int ResolvePendingInstallEstimateParallelPackageDegree(int configured)
    {
        int resolved = configured == 0 ? ResolveInstallEstimationDefaultDegree() : configured;
        return Math.Max(1, resolved);
    }

    internal int ResolvePendingInstallEstimateParallelPackageDegree()
    {
        return ResolvePendingInstallEstimateParallelPackageDegree(CurrentOptionsSnapshot.PendingInstallEstimateMaxParallelPackages);
    }

    private sealed class InstallEstimationExecutionPolicy
    {
        private InstallEstimationExecutionPolicy(int workItemDegree, int candidateEvaluationDegree)
        {
            WorkItemDegree = Math.Max(1, workItemDegree);
            CandidateEvaluationDegree = BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(candidateEvaluationDegree);
        }

        internal int WorkItemDegree { get; }

        internal int CandidateEvaluationDegree { get; }

        internal static InstallEstimationExecutionPolicy ForSingleWorkItem()
        {
            return new InstallEstimationExecutionPolicy(1, BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel: true));
        }

        internal static InstallEstimationExecutionPolicy ForManualBatch()
        {
            return new InstallEstimationExecutionPolicy(ResolveInstallEstimationDefaultDegree(), 1);
        }

        internal static InstallEstimationExecutionPolicy ForPendingBatch(int workItemDegree)
        {
            return new InstallEstimationExecutionPolicy(workItemDegree, 1);
        }
    }

    private void ProcessPendingInstallEstimateEvaluationPipeline(
        PendingInstallEstimateBatchRequest request,
        string source,
        CancellationToken token,
        PendingInstallEstimateEvaluationContext evaluationContext,
        List<PendingInstallEstimateEvaluationRequest> evaluationRequests,
        InstallEstimationExecutionPolicy executionPolicy,
        ref int completed,
        ref int lowConfidenceCount,
        ref PerformanceInteraction? firstVisibleInteraction)
    {
        executionPolicy ??= InstallEstimationExecutionPolicy.ForSingleWorkItem();
        List<(PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task)> inFlight = [];
        List<PendingInstallEstimateEvaluationRequest> searchingRequests = [];
        int nextDispatchIndex = 0;
        int nextApplyIndex = 0;
        try
        {
            while (nextApplyIndex < evaluationRequests.Count)
            {
                while (!token.IsCancellationRequested && nextDispatchIndex < evaluationRequests.Count && inFlight.Count < executionPolicy.WorkItemDegree)
                {
                    PendingInstallEstimateEvaluationRequest dispatchRequest = evaluationRequests[nextDispatchIndex];
                    SetPendingInstallEstimateSearchingState(dispatchRequest, isSearching: true);
                    searchingRequests.Add(dispatchRequest);
                    Task<PendingInstallEstimateEvaluationResult> evaluateTask = Task.Run(
                        () => EvaluatePendingInstallEstimateRequest(
                            request.Source,
                            dispatchRequest,
                            evaluationContext,
                            executionPolicy,
                            token),
                        token);
                    inFlight.Add((dispatchRequest, evaluateTask));
                    nextDispatchIndex++;
                }

                int applySlotIndex = inFlight.FindIndex(item => item.Request.OrderIndex == nextApplyIndex);
                if (applySlotIndex < 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    throw new InvalidOperationException("Pending install estimate pipeline lost request order.");
                }

                (PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task) applySlot = inFlight[applySlotIndex];
                PendingInstallEstimateEvaluationResult evaluationResult = applySlot.Task.GetAwaiter().GetResult();
                inFlight.RemoveAt(applySlotIndex);
                ApplyPendingInstallEstimateEvaluationResult(
                    request,
                    source,
                    applySlot.Request,
                    evaluationResult,
                    executionPolicy,
                    token,
                    ref completed,
                    ref lowConfidenceCount,
                    ref firstVisibleInteraction);
                searchingRequests.Remove(applySlot.Request);
                nextApplyIndex++;
            }

            foreach ((PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task) item in inFlight)
            {
                PendingInstallEstimateEvaluationResult evaluationResult = item.Task.GetAwaiter().GetResult();
                ApplyPendingInstallEstimateEvaluationResult(
                    request,
                    source,
                    item.Request,
                    evaluationResult,
                    executionPolicy,
                    token,
                    ref completed,
                    ref lowConfidenceCount,
                    ref firstVisibleInteraction);
                searchingRequests.Remove(item.Request);
            }
        }
        finally
        {
            SetPendingInstallEstimateSearchingEntries(
                searchingRequests.SelectMany(searchingRequest => searchingRequest?.MissingEntries ?? []),
                isSearching: false);
        }
    }

    private PendingInstallEstimateEvaluationResult EvaluatePendingInstallEstimateRequest(
        PendingInstallEstimateBatchSource source,
        PendingInstallEstimateEvaluationRequest request,
        PendingInstallEstimateEvaluationContext evaluationContext,
        InstallEstimationExecutionPolicy executionPolicy,
        CancellationToken token)
    {
        if (installEstimationExecutionObserver == null)
        {
            return EvaluatePendingInstallEstimateRequestCore(request, evaluationContext, executionPolicy, token);
        }

        using IDisposable workItemScope = installEstimationExecutionObserver.BeginWorkItem(
            new InstallEstimationWorkItemObservation(
                source,
                request?.OrderIndex ?? -1,
                request?.DisplayName ?? string.Empty,
                executionPolicy?.WorkItemDegree ?? 1,
                executionPolicy?.CandidateEvaluationDegree ?? 1));
        return EvaluatePendingInstallEstimateRequestCore(request, evaluationContext, executionPolicy, token);
    }

    private PendingInstallEstimateEvaluationResult EvaluatePendingInstallEstimateRequestCore(PendingInstallEstimateEvaluationRequest request, PendingInstallEstimateEvaluationContext evaluationContext, InstallEstimationExecutionPolicy executionPolicy, CancellationToken token)
    {
        var result = new PendingInstallEstimateEvaluationResult
        {
            Request = request,
            OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.NoOp,
            CurrentnessStamp = evaluationContext?.CurrentnessStamp ?? default
        };
        if (token.IsCancellationRequested || request == null || request.Package == null || !request.WasPendingAtPreparation)
        {
            result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.SkippedAsStale;
            return result;
        }
        if (!request.HasMissingFiles)
        {
            return result;
        }
        if (HasUnsupportedResourcePath(request.MissingEntries))
        {
            result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.UnsupportedResourcePath;
            return result;
        }

        if (request.AttemptInstalledResolve)
        {
            result.InstalledResolution = request.BatchState?.PreparationInstalledResolution?.Success == true
                && request.BatchState.PreparationCurrentnessStamp == evaluationContext?.CurrentnessStamp
                ? request.BatchState.PreparationInstalledResolution
                : EvaluateInstalledDestinationFromPackage(request.Package, request.MissingEntries, evaluationContext);
            if (result.InstalledResolution.Success)
            {
                result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.ResolvedInstalledDirectory;
                result.ResolvedDirectory = result.InstalledResolution.InstallDirectory;
                return result;
            }
            if (result.InstalledResolution.Reason == InstalledDirectoryResolveReason.MultipleCandidateDirectories)
            {
                if (!HasUsableDirectoryLookupCache(evaluationContext?.DirectoryLookupCacheSnapshot))
                {
                    LogInstallPerformance("mixed_package_resolve failed reason=resource_index_unavailable missing=" + request.MissingEntries.Count + " candidateDirs=" + result.InstalledResolution.CandidateDirectoryCount);
                    result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.NoOp;
                    return result;
                }
                result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.EstimatedResult;
                result.EstimationData = EvaluateInstallEstimation(
                    request.Package,
                    request.MissingEntries,
                    executionPolicy.CandidateEvaluationDegree,
                    request.EstimateMode,
                    evaluationContext.OptionsSnapshot,
                    useThreadSafeResolvers: true,
                    useSharedLazyHashMetrics: true,
                    evaluationContext.DirectoryLookupCacheSnapshot,
                    request.BatchState,
                    result.InstalledResolution.CandidateDirectories,
                    result.InstalledResolution.GetCandidateDirectoryUniquePrimaryHashCount,
                    markInstalledDestinationAmbiguous: true);
                if (result.EstimationData?.SourceSurfaceScanLimitExceeded == true)
                {
                    result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.SourceSurfaceScanLimitExceeded;
                }
                return result;
            }
            result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.NoOp;
            return result;
        }

        result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.EstimatedResult;
        result.EstimationData = EvaluateInstallEstimation(
            request.Package,
            request.MissingEntries,
            executionPolicy.CandidateEvaluationDegree,
            request.EstimateMode,
            evaluationContext.OptionsSnapshot,
            useThreadSafeResolvers: true,
            useSharedLazyHashMetrics: true,
            evaluationContext.DirectoryLookupCacheSnapshot,
            request.BatchState);
        if (result.EstimationData?.SourceSurfaceScanLimitExceeded == true)
        {
            result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.SourceSurfaceScanLimitExceeded;
        }
        return result;
    }

    private static bool HasUsableDirectoryLookupCache(DirectoryResourceLookupCache directoryLookupCache)
    {
        return directoryLookupCache != null && directoryLookupCache.Count > 0;
    }

    private InstalledDirectoryLookupResult EvaluateInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries, PendingInstallEstimateEvaluationContext evaluationContext)
    {
        if (package == null || missingEntries == null || missingEntries.Count == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.NoInstalledDirectoryMatch
            };
        }
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService(evaluationContext?.OptionsSnapshot);
        return installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingEntries, evaluationContext?.InstalledChartLookupIndex ?? new InstalledChartLookupIndexSnapshot());
    }

    private void ApplyPendingInstallEstimateEvaluationResult(
        PendingInstallEstimateBatchRequest batchRequest,
        string source,
        PendingInstallEstimateEvaluationRequest dispatchedRequest,
        PendingInstallEstimateEvaluationResult evaluationResult,
        InstallEstimationExecutionPolicy executionPolicy,
        CancellationToken token,
        ref int completed,
        ref int lowConfidenceCount,
        ref PerformanceInteraction? firstVisibleInteraction)
    {
        PendingInstallEstimateEvaluationRequest request = evaluationResult?.Request;
        List<PackageChartEntry> dispatchedSearchingEntries = [.. (dispatchedRequest?.MissingEntries ?? [])
            .Where(entry => entry != null)
            .Distinct()];
        string currentDisplayName = request?.DisplayName ?? string.Empty;
        SetInstallEstimationProgress(ToInstallEstimationProgressSource(batchRequest.Source), batchRequest.PackageCount, completed, currentDisplayName);
        bool isLowConfidence = false;
        List<Func<Action>> notificationDeferrals =
            DeferPackageEntryNotifications((request?.Package?.ChartEntries ?? []).Concat(dispatchedSearchingEntries));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (installEstimationExecutionObserver != null)
                {
                    installEstimationExecutionObserver.ObserveAttemptEvaluated(
                        new InstallEstimationAttemptEvaluatedObservation(
                            batchRequest.Source,
                            request?.OrderIndex ?? dispatchedRequest?.OrderIndex ?? -1,
                            currentDisplayName,
                            attempt,
                            evaluationResult?.CurrentnessStamp ?? default));
                }
                bool stale;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                {
                    using (rwlockPendingInstallCharts.GetWriterGuard())
                    {
                        using (rwlockBMSFiles.GetReaderGuard())
                        {
                            using (rwlockSongDBInstall.GetWriterGuard())
                            {
                                lock (pendingInstallEstimateCurrentnessGate)
                                {
                                    stale = !IsPendingInstallEstimateCurrentUnderGateUnsafe(
                                        evaluationResult.CurrentnessStamp);
                                    if (!stale)
                                    {
                                        isLowConfidence =
                                            ApplyPendingInstallEstimateEvaluationResultUnsafe(evaluationResult);
                                    }
                                }
                            }
                        }
                    }
                }

                if (!stale)
                {
                    break;
                }
                if (attempt > 0)
                {
                    LogInstallPerformance(
                        "pending_estimate_batch skipped reason=currentness_changed_twice package="
                        + (evaluationResult.Request?.Package?.path ?? string.Empty));
                    break;
                }

                PendingInstallEstimateRetryCapture retryCapture =
                    CapturePendingInstallEstimateRetry(evaluationResult.Request);
                evaluationResult = EvaluatePendingInstallEstimateRequest(
                    batchRequest.Source,
                    retryCapture.Request,
                    retryCapture.Context,
                    executionPolicy,
                    token);
            }
        }
        finally
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        SetPendingInstallEstimateSearchingEntriesUnsafe(
                            dispatchedSearchingEntries,
                            isSearching: false);
                    }
                }
            }
            if (QueuePackageEntryNotificationPublication(
                notificationDeferrals,
                firstVisibleInteraction))
            {
                firstVisibleInteraction = null;
            }
        }
        completed++;
        if (isLowConfidence)
        {
            lowConfidenceCount++;
        }
        SetInstallEstimationProgress(ToInstallEstimationProgressSource(batchRequest.Source), batchRequest.PackageCount, completed, currentDisplayName);
        packageLifecycleOwner.ReportPendingEstimateBatchProgress(completed);
        LogInstallPerformance("pending_estimate_batch progress source=" + source + " packageDegree=" + executionPolicy.WorkItemDegree + " completed=" + completed + "/" + batchRequest.PackageCount + " current=" + currentDisplayName);
    }

    private PendingInstallEstimateRetryCapture CapturePendingInstallEstimateRetry(
        PendingInstallEstimateEvaluationRequest previousRequest)
    {
        ChartPackage package = previousRequest?.Package;
        if (package == null)
        {
            return new PendingInstallEstimateRetryCapture
            {
                Request = previousRequest,
                Context = CreatePendingInstallEstimateEvaluationContext()
            };
        }

        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    lock (pendingInstallEstimateCurrentnessGate)
                    {
                        PendingPackageChartEntryPartition partition =
                            BuildPendingPackageChartEntryPartitionUnsafe(package);
                        PendingInstallEstimateEvaluationContext context =
                            CreatePendingInstallEstimateEvaluationContextUnderCurrentnessGateUnsafe();
                        return new PendingInstallEstimateRetryCapture
                        {
                            Request = new PendingInstallEstimateEvaluationRequest
                            {
                                OrderIndex = previousRequest.OrderIndex,
                                Package = package,
                                DisplayName = previousRequest.DisplayName,
                                AlreadyInstalledEntries = partition.AlreadyInstalledEntries,
                                MissingEntries = partition.MissingEntries,
                                EstimateMode = previousRequest.EstimateMode,
                                WasPendingAtPreparation = ChartPackagesPending.Contains(package),
                                BatchState = previousRequest.BatchState
                            },
                            Context = context
                        };
                    }
                }
            }
        }
    }

    private bool ApplyPendingInstallEstimateEvaluationResultUnsafe(PendingInstallEstimateEvaluationResult evaluationResult)
    {
        PendingInstallEstimateEvaluationRequest request = evaluationResult?.Request;
        if (request == null)
        {
            return false;
        }

        ChartPackage package = request.Package;
        if (package == null || !ChartPackagesPending.Contains(package))
        {
            return false;
        }

        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
        ApplyPackageMixedInstallWarningsToEntries(request.AlreadyInstalledEntries.Where(entry => entry?.Chart != null && ContainsInstalledChartUnsafe(entry.Chart)));

        List<PackageChartEntry> currentMissingEntries = [.. request.MissingEntries.Where(entry => entry?.Chart != null && !ContainsInstalledChartUnsafe(entry.Chart))];
        if (currentMissingEntries.Count == 0)
        {
            return false;
        }
        if (currentMissingEntries.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)))
        {
            return false;
        }

        if (request.AttemptInstalledResolve)
        {
            LogMixedPackageResolution(evaluationResult.InstalledResolution, package.path, request.MissingEntries.Count);
        }

        switch (evaluationResult.OutcomeKind)
        {
            case PendingInstallEstimateEvaluationOutcomeKind.UnsupportedResourcePath:
                ApplyUnsupportedResourcePathToPackageUnsafe(package, currentMissingEntries);
                return false;
            case PendingInstallEstimateEvaluationOutcomeKind.SourceSurfaceScanLimitExceeded:
                LogSourceSurfaceScanLimitExceeded(evaluationResult.EstimationData, package?.path);
                ApplySourceSurfaceScanLimitExceededToPackageUnsafe(
                    package,
                    currentMissingEntries,
                    evaluationResult.EstimationData?.SourceSurfaceMaxVisitedFileSystemEntryCount ?? PackageInstallEstimationSnapshotBuilder.DefaultSourceSurfaceMaxVisitedFileSystemEntries);
                return false;
            case PendingInstallEstimateEvaluationOutcomeKind.ResolvedInstalledDirectory:
                if (!string.IsNullOrWhiteSpace(evaluationResult.ResolvedDirectory))
                {
                    ApplyResolvedInstallDestinationToEntries(currentMissingEntries, evaluationResult.ResolvedDirectory);
                }
                return HasPendingLowConfidenceInstallDestination(currentMissingEntries);
            case PendingInstallEstimateEvaluationOutcomeKind.EstimatedResult:
                {
                    LogInstallEstimationEvaluation(evaluationResult.EstimationData);
                    if (request.AttemptInstalledResolve && evaluationResult.EstimationData?.Result?.HasViableDestination != true)
                    {
                        ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, currentMissingEntries);
                        return false;
                    }
                    ApplyInstallEstimationResultToEntries(currentMissingEntries, evaluationResult.EstimationData?.Result);
                    return HasPendingLowConfidenceInstallDestination(currentMissingEntries);
                }
            case PendingInstallEstimateEvaluationOutcomeKind.NoOp:
                if (request.AttemptInstalledResolve)
                {
                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, currentMissingEntries);
                }
                return false;
            default:
                return false;
        }
    }

    private void LogMixedPackageResolution(InstalledDirectoryLookupResult resolution, string packagePath, int missingFileCount)
    {
        if (resolution == null)
        {
            return;
        }
        if (resolution.Reason == InstalledDirectoryResolveReason.InstalledIndexEmpty && !resolution.Success)
        {
            LogInstallPerformance("mixed_package_resolve failed reason=installed_index_empty");
            return;
        }
        LogInstallPerformance("mixed_package_resolve start package=" + (packagePath ?? string.Empty) + " missing=" + missingFileCount + " installedMatched=" + resolution.MatchedHashCount + " candidateDirs=" + resolution.CandidateDirectoryCount);
        if (resolution.Success)
        {
            LogInstallPerformance("mixed_package_resolve selected dst=" + resolution.InstallDirectory + " matched=" + resolution.MatchedHashCount + " tieCandidates=" + resolution.CandidateDirectoryCount);
            return;
        }
        string reason = resolution.Reason switch
        {
            InstalledDirectoryResolveReason.InvalidInput => "invalid_input",
            InstalledDirectoryResolveReason.NoInstalledDirectoryMatch => "no_installed_dir_match",
            InstalledDirectoryResolveReason.MultipleCandidateDirectories => "multiple_candidate_directories",
            InstalledDirectoryResolveReason.MissingRepresentative => "missing_representative_not_found",
            InstalledDirectoryResolveReason.TieHealthBelowThreshold => "tie_health_below_threshold",
            InstalledDirectoryResolveReason.TieBreakUnresolved => "tie_break_unresolved",
            InstalledDirectoryResolveReason.InstalledIndexEmpty => "installed_index_empty",
            _ => "unknown"
        };
        LogInstallPerformance("mixed_package_resolve failed reason=" + reason + " tieCandidates=" + resolution.CandidateDirectoryCount);
    }

    private void SetPendingInstallEstimateSearchingState(PendingInstallEstimateEvaluationRequest request, bool isSearching)
    {
        SetPendingInstallEstimateSearchingEntries(request?.MissingEntries, isSearching);
    }

    private void SetPendingInstallEstimateSearchingEntries(
        IEnumerable<PackageChartEntry> entries,
        bool isSearching)
    {
        List<PackageChartEntry> distinctEntries = [.. (entries ?? [])
            .Where(entry => entry != null)
            .Distinct()];
        List<Func<Action>> notificationDeferrals =
            DeferPackageEntryNotifications(distinctEntries);
        try
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        SetPendingInstallEstimateSearchingEntriesUnsafe(distinctEntries, isSearching);
                    }
                }
            }
        }
        finally
        {
            QueuePackageEntryNotificationPublication(notificationDeferrals);
        }
    }

    private static List<Func<Action>> DeferPackageEntryNotifications(
        IEnumerable<PackageChartEntry> entries)
    {
        return [.. (entries ?? [])
            .Where(entry => entry != null)
            .Distinct()
            .Select(entry => entry.DeferPropertyChangedNotificationPublication())];
    }

    private static void DiscardPackageEntryNotificationPublication(
        IEnumerable<Func<Action>> notificationDeferrals)
    {
        foreach (Func<Action> notificationDeferral in notificationDeferrals ?? [])
        {
            try
            {
                // Release the deferral scope, but intentionally discard the
                // publication because the command has a durable finalization
                // failure and must not emit ordinary success state.
                notificationDeferral?.Invoke();
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(
                    exception,
                    "package_entry_notification_deferral_discard_failed");
            }
        }
    }

    private bool QueuePackageEntryNotificationPublication(
        IReadOnlyList<Func<Action>> notificationDeferrals,
        PerformanceInteraction? firstVisibleInteraction = null)
    {
        if (notificationDeferrals == null || notificationDeferrals.Count == 0)
        {
            return false;
        }
        List<Action> publications = [];
        for (int index = notificationDeferrals.Count - 1; index >= 0; index--)
        {
            Action entryPublication = notificationDeferrals[index]?.Invoke();
            if (entryPublication != null)
            {
                publications.Add(entryPublication);
            }
        }
        if (publications.Count == 0)
        {
            return false;
        }
        void Publish()
        {
            if (firstVisibleInteraction.HasValue && Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    firstVisibleInteraction.Value,
                    "ui_started",
                    "surface=package_entries publications=" + publications.Count);
            }
            List<Exception> failures = [];
            foreach (Action entryPublication in publications)
            {
                try
                {
                    entryPublication();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "Package entry notification publication failed.",
                    failures);
            }
            if (firstVisibleInteraction.HasValue && Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    firstVisibleInteraction.Value,
                    "ui_applied",
                    "surface=package_entries publications=" + publications.Count);
                Net10PerformanceLog.Write(
                    firstVisibleInteraction.Value,
                    "first_useful_visible",
                    "surface=package_entries");
            }
        }
        bool schedulesDispatcherWork = uiScheduler.IsAvailable;
        IUiScheduledOperation publication = uiScheduler.Schedule(Publish);
        if (!publication.IsAccepted)
        {
            LogInstallPerformanceWarn(
                "package_entry_notification_publication_rejected reason="
                + publication.RejectionReason);
            return false;
        }
        if (firstVisibleInteraction.HasValue
            && schedulesDispatcherWork
            && Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                firstVisibleInteraction.Value,
                "ui_queued",
                "surface=package_entries publications=" + publications.Count);
        }
        _ = publication.Completion.ContinueWith(
            task => LogInstallPerformanceWarn(
                task.IsCanceled || publication.IsAborted
                    ? "package_entry_notification_publication_canceled"
                    : "package_entry_notification_publication_failed exception="
                        + (task.Exception?.GetBaseException().GetType().Name ?? "unknown")),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private static void SetPendingInstallEstimateSearchingEntriesUnsafe(
        IEnumerable<PackageChartEntry> entries,
        bool isSearching)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            entry?.SetSearchingStatus(isSearching);
        }
    }

    private void HandlePendingEstimateBatchException(Exception ex)
    {
        if (ex == null)
        {
            return;
        }
        NLogWrapper.FileLogger?.Error(ex, "pending_estimate_batch failed");
    }

    private static string ToPendingEstimateBatchSourceLogValue(PendingInstallEstimateBatchSource source)
    {
        return source switch
        {
            PendingInstallEstimateBatchSource.StartupRestore => "startup_restore",
            PendingInstallEstimateBatchSource.AutoInstall => "auto_install",
            PendingInstallEstimateBatchSource.ManualReestimate => "manual_reestimate",
            _ => "unknown"
        };
    }

    private static InstallEstimationProgressSource ToInstallEstimationProgressSource(PendingInstallEstimateBatchSource source)
    {
        return source switch
        {
            PendingInstallEstimateBatchSource.StartupRestore => InstallEstimationProgressSource.StartupRestore,
            PendingInstallEstimateBatchSource.AutoInstall => InstallEstimationProgressSource.AutoInstall,
            PendingInstallEstimateBatchSource.ManualReestimate => InstallEstimationProgressSource.ManualReestimate,
            _ => InstallEstimationProgressSource.None
        };
    }

    private void LogPendingEstimateSkippedPackage(string source, ChartPackage package, int sourceHealth)
    {
        PendingEstimateDeferredReason reason = package?.DeferredEstimateReason ?? PendingEstimateDeferredReason.None;
        string reasonLog = reason switch
        {
            PendingEstimateDeferredReason.HealthySourceBaseline => "healthy_source_baseline",
            PendingEstimateDeferredReason.UnsupportedResourcePath => "unsupported_resource_path",
            PendingEstimateDeferredReason.InstalledDestinationResolveFailed => "installed_destination_resolve_failed",
            PendingEstimateDeferredReason.SourceSurfaceScanLimitExceeded => "source_surface_scan_limit_exceeded",
            _ => "unknown"
        };
        string message = "pending_estimate_batch skipped source=" + source + " package=" + (package?.path ?? string.Empty) + " reason=" + reasonLog;
        if (reason == PendingEstimateDeferredReason.HealthySourceBaseline)
        {
            message += " sourceHealth=" + sourceHealth;
        }
        LogInstallPerformance(message);
    }

    private void RunPendingEstimateExclusive(Action action)
    {
        packageLifecycleOwner.RunPendingEstimateExclusive(action);
    }

    /// <summary>
    /// Attempts to reserve the owner admission shared by foreground pending
    /// mutations and background pending-estimate publication.
    /// </summary>
    internal bool TryEnterPendingOperation(out IDisposable lease)
    {
        if (packageLifecycleOwner == null)
        {
            lease = null;
            return false;
        }
        return packageLifecycleOwner.TryEnterPendingOperation(out lease);
    }

    internal bool IsPendingOperationAdmissionReady => packageLifecycleOwner != null;

    private BackgroundPendingEstimatePreparationResult PrepareBackgroundPendingEstimatePackagesUnsafe(IEnumerable<ChartPackage> packages, PendingInstallEstimateBatchSource source)
    {
        List<ChartPackage> packageList = [.. (packages ?? []).Where(package => package != null).Distinct()];
        if (packageList.Count == 0)
        {
            return new BackgroundPendingEstimatePreparationResult();
        }

        string sourceLogValue = ToPendingEstimateBatchSourceLogValue(source);
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService(options);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PendingInstallEstimateEvaluationContext preparationContext =
                CreatePendingInstallEstimateEvaluationContextUnsafe();
            PendingEstimateSourceBatchSnapshot candidateSnapshot =
                BuildPendingEstimateSourceBatchSnapshotUnsafe(
                    packageList,
                    installEstimationService,
                    preparationContext);

            foreach (PendingEstimateSourceBatchPackageState state in candidateSnapshot.PackageStates)
            {
                if (HasUnsupportedResourcePath(state.MissingEntries)
                    || HasInstalledDestinationResolveFailed(state)
                    || HasSourceSurfaceScanLimitExceeded(state))
                {
                    continue;
                }

                bool deferred = ShouldDeferPendingEstimateBatchPackageUnsafe(
                    state,
                    installEstimationService,
                    out int sourcePrimaryHealth);
                state.BaselinePrefilter = new SourceBaselinePrefilterResult
                {
                    Deferred = deferred,
                    PrimaryHealth = sourcePrimaryHealth
                };
            }

            if (TryCommitPendingEstimatePreparationUnsafe(
                candidateSnapshot,
                out BackgroundPendingEstimatePreparationResult result))
            {
                LogInstallPerformance("pending_estimate_source_batch_build source=" + sourceLogValue
                    + " packages=" + packageList.Count
                    + " roots=" + candidateSnapshot.RootCount
                    + " chunks=" + candidateSnapshot.ChunkCount
                    + " nativeBridgeMs=" + candidateSnapshot.NativeBridgeMs
                    + " managedDecodeMs=" + candidateSnapshot.ManagedDecodeMs
                    + " managedMaterializeMs=" + candidateSnapshot.ManagedMaterializeMs
                    + " trackedFiles=" + candidateSnapshot.TrackedFileCount
                    + " resourceFiles=" + candidateSnapshot.ResourceFileCount
                    + " scanLimitExceeded=" + candidateSnapshot.ScanLimitExceeded.ToString().ToLowerInvariant()
                    + " visitedEntries=" + candidateSnapshot.VisitedFileSystemEntryCount
                    + " maxVisitedEntries=" + candidateSnapshot.MaxVisitedFileSystemEntryCount
                    + " elapsedMs=" + candidateSnapshot.ElapsedMs);
                LogInstallPerformance("pending_estimate_source_batch_prefilter source=" + sourceLogValue
                    + " packages=" + packageList.Count
                    + " estimable=" + result.EstimablePackages.Count
                    + " deferred=" + result.DeferredPackages.Count
                    + " elapsedMs=" + result.BatchSourceSnapshot.PrefilterMs);
                return result;
            }

            LogInstallPerformance("pending_estimate_source_batch retry reason=currentness_changed attempt=" + (attempt + 1));
        }

        LogInstallPerformance("pending_estimate_source_batch skipped reason=currentness_changed_twice source=" + sourceLogValue);
        return new BackgroundPendingEstimatePreparationResult();
    }

    private bool TryCommitPendingEstimatePreparationUnsafe(
        PendingEstimateSourceBatchSnapshot candidateSnapshot,
        out BackgroundPendingEstimatePreparationResult result)
    {
        result = null;
        var committedResult = new BackgroundPendingEstimatePreparationResult();
        var estimableSnapshot = new PendingEstimateSourceBatchSnapshot
        {
            PreparationCurrentnessStamp = candidateSnapshot.PreparationCurrentnessStamp,
            RootCount = candidateSnapshot.RootCount,
            ChunkCount = candidateSnapshot.ChunkCount,
            NativeBridgeMs = candidateSnapshot.NativeBridgeMs,
            ManagedDecodeMs = candidateSnapshot.ManagedDecodeMs,
            ManagedMaterializeMs = candidateSnapshot.ManagedMaterializeMs,
            TrackedFileCount = candidateSnapshot.TrackedFileCount,
            ResourceFileCount = candidateSnapshot.ResourceFileCount,
            ScanLimitExceeded = candidateSnapshot.ScanLimitExceeded,
            VisitedFileSystemEntryCount = candidateSnapshot.VisitedFileSystemEntryCount,
            MaxVisitedFileSystemEntryCount = candidateSnapshot.MaxVisitedFileSystemEntryCount,
            ElapsedMs = candidateSnapshot.ElapsedMs,
            ScanBackend = candidateSnapshot.ScanBackend
        };

        var prefilterStopwatch = Stopwatch.StartNew();
        lock (pendingInstallEstimateCurrentnessGate)
        {
            if (!IsPendingInstallEstimateCurrentUnderGateUnsafe(
                candidateSnapshot.PreparationCurrentnessStamp))
            {
                return false;
            }

            foreach (PendingEstimateSourceBatchPackageState state in candidateSnapshot.PackageStates)
            {
                if (HasUnsupportedResourcePath(state.MissingEntries))
                {
                    ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries);
                    ApplyUnsupportedResourcePathToPackageUnsafe(state.Package, state.MissingEntries);
                    committedResult.DeferredPackages.Add(state.Package);
                    continue;
                }

                if (HasInstalledDestinationResolveFailed(state))
                {
                    state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
                    ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries);
                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(state.Package, state.MissingEntries);
                    committedResult.DeferredPackages.Add(state.Package);
                    continue;
                }

                if (HasSourceSurfaceScanLimitExceeded(state))
                {
                    ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries);
                    ApplySourceSurfaceScanLimitExceededToPackageUnsafe(
                        state.Package,
                        state.MissingEntries,
                        state.SourceSurface?.MaxVisitedFileSystemEntryCount ?? PackageInstallEstimationSnapshotBuilder.DefaultSourceSurfaceMaxVisitedFileSystemEntries);
                    committedResult.DeferredPackages.Add(state.Package);
                    continue;
                }

                if (state.BaselinePrefilter.Deferred)
                {
                    state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;
                    ClearInstallEstimationStateUnsafe(state.PackageEntries);
                    committedResult.DeferredPackages.Add(state.Package);
                    committedResult.DeferredSourceHealthByPackage[state.Package] = state.BaselinePrefilter.PrimaryHealth;
                    continue;
                }

                state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
                committedResult.EstimablePackages.Add(state.Package);
                estimableSnapshot.AddState(state);
            }
        }
        prefilterStopwatch.Stop();
        estimableSnapshot.PrefilterMs = prefilterStopwatch.ElapsedMilliseconds;
        committedResult.BatchSourceSnapshot = estimableSnapshot;
        result = committedResult;
        return true;
    }

    private PendingEstimateSourceBatchSnapshot BuildPendingEstimateSourceBatchSnapshotUnsafe(
        List<ChartPackage> packageList,
        BmsLibraryInstallEstimationService installEstimationService,
        PendingInstallEstimateEvaluationContext preparationContext)
    {
        ArgumentNullException.ThrowIfNull(preparationContext);
        var snapshot = new PendingEstimateSourceBatchSnapshot
        {
            PreparationCurrentnessStamp = preparationContext.CurrentnessStamp
        };
        var stopwatch = Stopwatch.StartNew();
        var sourceSurfaceByRoot = new Dictionary<string, SourceSurfaceEntryView>(StringComparer.OrdinalIgnoreCase);
        var rootsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ChartPackage package in packageList ?? Enumerable.Empty<ChartPackage>())
        {
            PendingPackageChartEntryPartition partition = BuildPendingPackageChartEntryPartitionUnsafe(package);
            var state = new PendingEstimateSourceBatchPackageState
            {
                Package = package,
                DisplayName = PendingInstallEstimateBatchRequest.GetDisplayName(package?.path),
                SourceDirectory = ResolvePendingEstimateSourceDirectory(package?.path),
                PackageEntries = partition.PackageEntries,
                AlreadyInstalledEntries = partition.AlreadyInstalledEntries,
                MissingEntries = partition.MissingEntries,
                ChartResources = ChartResourceSnapshot.CreateAggregate(partition.MissingEntries.Select(entry => entry.Chart)),
                EstimateMode = ChartInstallationEstimateMode.Normal,
                PreparationCurrentnessStamp = preparationContext.CurrentnessStamp
            };
            if (state.AttemptInstalledResolve)
            {
                state.PreparationInstalledResolution = installEstimationService.TryResolveInstalledDestinationFromPackage(
                    package,
                    state.MissingEntries,
                    preparationContext.InstalledChartLookupIndex);
            }

            if (state.HasMissingFiles
                && !HasInstalledDestinationResolveFailed(state)
                && !HasUnsupportedResourcePath(state.MissingEntries)
                && LongPathFileSystem.DirectoryExists(state.Package?.path)
                && !string.IsNullOrWhiteSpace(state.SourceDirectory)
                && LongPathFileSystem.DirectoryExists(state.SourceDirectory))
            {
                rootsToScan.Add(state.SourceDirectory);
            }

            snapshot.PackageStates.Add(state);
        }

        TryPopulatePendingEstimateSourceSurfaceViewsUnsafe(rootsToScan, sourceSurfaceByRoot, snapshot);

        foreach (PendingEstimateSourceBatchPackageState state in snapshot.PackageStates)
        {
            if (HasInstalledDestinationResolveFailed(state))
            {
                continue;
            }

            if (HasUnsupportedResourcePath(state.MissingEntries))
            {
                continue;
            }

            if (!LongPathFileSystem.DirectoryExists(state.Package?.path))
            {
                continue;
            }

            if (!state.HasMissingFiles || string.IsNullOrWhiteSpace(state.SourceDirectory))
            {
                continue;
            }

            if (sourceSurfaceByRoot.TryGetValue(state.SourceDirectory, out SourceSurfaceEntryView sourceSurface))
            {
                state.SourceSurface = sourceSurface;
                state.UsesBatchSourceSurface = sourceSurface != null;
                continue;
            }

            PackageInstallSurfaceSnapshot fallbackSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(state.Package?.path);
            state.SourceSurface = CreateSourceSurfaceEntryView(fallbackSnapshot, state.SourceDirectory);
            state.UsesBatchSourceSurface = state.SourceSurface != null;
        }

        stopwatch.Stop();
        snapshot.ElapsedMs = stopwatch.ElapsedMilliseconds;
        if (string.IsNullOrWhiteSpace(snapshot.ScanBackend))
        {
            snapshot.ScanBackend = sourceSurfaceByRoot.Values.Select(view => view?.ScanBackend).FirstOrDefault(backend => !string.IsNullOrWhiteSpace(backend))
                ?? string.Empty;
        }

        return snapshot;
    }

    private void TryPopulatePendingEstimateSourceSurfaceViewsUnsafe(IEnumerable<string> roots, IDictionary<string, SourceSurfaceEntryView> sourceSurfaceByRoot, PendingEstimateSourceBatchSnapshot snapshot)
    {
        if (sourceSurfaceByRoot == null || snapshot == null)
        {
            return;
        }

        List<string> distinctRoots = [.. (roots ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        snapshot.RootCount = distinctRoots.Count;
        if (distinctRoots.Count == 0)
        {
            return;
        }

        snapshot.ScanBackend = "bounded_fast_source_surface";
        List<List<string>> chunks = [.. distinctRoots
            .Select((root, index) => new { Root = root, Index = index })
            .GroupBy(item => item.Index / PendingEstimateSourceBatchMaxRootsPerChunk)
            .Select(group => group.Select(item => item.Root).ToList())];
        snapshot.ChunkCount = chunks.Count;

        foreach (string root in distinctRoots)
        {
            PackageInstallSurfaceSnapshot fallbackSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(root);
            SourceSurfaceEntryView view = CreateSourceSurfaceEntryView(fallbackSnapshot, root);
            sourceSurfaceByRoot[root] = view;
            snapshot.TrackedFileCount += view.TrackedFileCount;
            snapshot.ResourceFileCount += view.ResourceFileCount;
            snapshot.ManagedMaterializeMs += view.HashMaterializeMs;
            snapshot.VisitedFileSystemEntryCount += view.VisitedFileSystemEntryCount;
            snapshot.MaxVisitedFileSystemEntryCount = Math.Max(snapshot.MaxVisitedFileSystemEntryCount, view.MaxVisitedFileSystemEntryCount);
            snapshot.ScanLimitExceeded = snapshot.ScanLimitExceeded || view.ScanLimitExceeded;
        }
    }

    private static SourceSurfaceEntryView CreateSourceSurfaceEntryView(PackageInstallSurfaceSnapshot snapshot, string sourceDirectory)
    {
        return new SourceSurfaceEntryView
        {
            SourceDirectory = sourceDirectory ?? snapshot?.SourceDirectory ?? string.Empty,
            ResourceEntry = (snapshot?.SourceCandidateResources ?? new DirectoryResourceLookupCache.Entry()).Clone(),
            ChartFileCount = snapshot?.ChartFileCount ?? 0,
            ResourceFileCount = snapshot?.ResourceFileCount ?? 0,
            TrackedFileCount = snapshot?.TrackedFileCount ?? 0,
            ScanMs = snapshot?.ScanMs ?? 0L,
            HashMaterializeMs = snapshot?.HashMaterializeMs ?? 0L,
            ScanBackend = snapshot?.ScanBackend ?? "bounded_fast_source_surface",
            ScanLimitExceeded = snapshot?.ScanLimitExceeded ?? false,
            VisitedFileSystemEntryCount = snapshot?.VisitedFileSystemEntryCount ?? 0,
            MaxVisitedFileSystemEntryCount = snapshot?.MaxVisitedFileSystemEntryCount ?? 0
        };
    }

    private static bool HasSourceSurfaceScanLimitExceeded(PendingEstimateSourceBatchPackageState state)
    {
        return state?.SourceSurface?.ScanLimitExceeded == true;
    }

    private static bool HasInstalledDestinationResolveFailed(PendingEstimateSourceBatchPackageState state)
    {
        return state?.AttemptInstalledResolve == true
            && state.PreparationInstalledResolution != null
            && !state.PreparationInstalledResolution.Success
            && state.PreparationInstalledResolution.Reason != InstalledDirectoryResolveReason.MultipleCandidateDirectories;
    }

    private bool ShouldDeferPendingEstimateBatchPackageUnsafe(PendingEstimateSourceBatchPackageState state, BmsLibraryInstallEstimationService installEstimationService, out int sourcePrimaryHealth)
    {
        sourcePrimaryHealth = 0;
        if (state?.Package == null || installEstimationService == null || !LongPathFileSystem.DirectoryExists(state.Package.path))
        {
            return false;
        }
        if (!state.HasMissingFiles)
        {
            return false;
        }
        if (state.PreparationInstalledResolution?.Success == true)
        {
            return false;
        }
        if (state.SourceSurface?.ResourceEntry == null)
        {
            return false;
        }

        BmsLibraryInstallEstimationService.SourceBaselineEvaluation baseline = installEstimationService.EvaluateSourceBaseline(
            state.ChartResources,
            state.SourceDirectory,
            state.SourceSurface.ResourceEntry,
            state.SourceSurface.ResourceEntry);
        sourcePrimaryHealth = baseline.PrimaryHealth;
        return baseline.IsViableDestination;
    }

    private static string ResolvePendingEstimateSourceDirectory(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return string.Empty;
        }

        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(packagePath);
            if (LongPathFileSystem.DirectoryExists(normalizedPath))
            {
                return LongPathFileSystem.TrimTrailingDirectorySeparators(normalizedPath);
            }

            return Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ClearInstallEstimationStateUnsafe(IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in (entries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ClearInstallDestination();
        }
    }

    private void ClearDeferredEstimateReasonForEntriesUnsafe(IEnumerable<PackageChartEntry> entries)
    {
        var entryPaths = new HashSet<string>(
            (entries ?? []).Select(entry => entry?.Chart?.Path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        if (entryPaths.Count == 0)
        {
            return;
        }

        IEnumerable<ChartPackage> pendingPackages = ChartPackagesPending ?? Enumerable.Empty<ChartPackage>();
        foreach (ChartPackage pendingPackage in pendingPackages.Where(package => package != null))
        {
            if ((pendingPackage.ChartEntries ?? []).Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && entryPaths.Contains(entry.Chart.Path)))
            {
                pendingPackage.DeferredEstimateReason = PendingEstimateDeferredReason.None;
            }
        }
    }

    private static bool PackageTargetsContainChartEntry(IEnumerable<ChartPackage> packages, PackageChartEntry chartEntry)
    {
        if (chartEntry?.Chart == null)
        {
            return false;
        }
        return (packages ?? []).Any(package => PackageContainsChartTarget(package, chartEntry));
    }

    private static bool PackageContainsAnyChartTarget(ChartPackage package, IEnumerable<PackageChartEntry> targetEntries)
    {
        List<PackageChartEntry> targets = [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
        if (targets.Count == 0)
        {
            return false;
        }
        return (package?.ChartEntries ?? []).Any(entry => targets.Any(target => IsSamePackageChartTarget(entry, target)));
    }

    private static bool PackageContainsChartTarget(ChartPackage package, PackageChartEntry targetEntry)
    {
        if (targetEntry?.Chart == null)
        {
            return false;
        }
        return (package?.ChartEntries ?? []).Any(entry => IsSamePackageChartTarget(entry, targetEntry));
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, PackageChartEntry targetEntry)
    {
        return entry?.IsSameChartTarget(targetEntry) == true;
    }

    /// <summary>
    /// 現在の BMSScores から score snapshot を再構築して公開します。
    /// </summary>
    /// <param name="reason">ログ出力用の更新理由。</param>
    private void RefreshScoreSnapshotFromCurrentScores(string reason)
    {
        List<BMSScore> scoresSnapshot;
        Dictionary<string, BMSScore> beatorajaScoresSnapshot;
        ActiveScoreSource sourceSnapshot;
        ScoreTableLoadStatus loadStatusSnapshot;
        string loadFailureMessageSnapshot;
        long sourceGenerationSnapshot;
        using (rwlockBMSScores.GetReaderGuard())
        {
            scoresSnapshot = [.. (BMSScores ?? []).Where(score => score != null)];
            beatorajaScoresSnapshot = new Dictionary<string, BMSScore>(
                beatorajaScoresBySha256 ?? new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            sourceSnapshot = activeScoreSource;
            loadStatusSnapshot = scoreTableLoadStatus;
            loadFailureMessageSnapshot = scoreTableLoadFailureMessage ?? string.Empty;
            sourceGenerationSnapshot = scoreSourceGeneration;
        }
        var stopwatch = Stopwatch.StartNew();
        var scoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore bmsScore in scoresSnapshot)
        {
            if (bmsScore != null && !string.IsNullOrWhiteSpace(bmsScore.hash))
            {
                scoresByHash[bmsScore.hash] = bmsScore;
            }
        }
        stopwatch.Stop();
        int version;
        lock (lockScoreSnapshot)
        {
            version = ++scoreSnapshotVersion;
            scoreSnapshot = new ScoreSnapshot
            {
                Version = version,
                LoadedAtUtc = DateTime.UtcNow,
                BuildElapsedMs = stopwatch.ElapsedMilliseconds,
                Scores = scoresSnapshot,
                ScoresByHash = scoresByHash,
                ScoresBySha256 = beatorajaScoresSnapshot,
                ActiveScoreSource = sourceSnapshot,
                LoadStatus = loadStatusSnapshot,
                LoadFailureMessage = loadFailureMessageSnapshot,
                SourceGeneration = sourceGenerationSnapshot
            };
        }
        ScoreSnapshotReady = sourceSnapshot != ActiveScoreSource.None
            && loadStatusSnapshot == ScoreTableLoadStatus.Loaded;
        ScoreSnapshotVersion = version;
        LogInstallPerformance("score_snapshot_load completed reason=" + (reason ?? "unknown") + " version=" + version + " source=" + sourceSnapshot + " count=" + scoresSnapshot.Count + " beatorajaCount=" + beatorajaScoresSnapshot.Count + " buildMs=" + stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// score lookup 用 snapshot を返します。未構築時は必要に応じて on-demand で生成します。
    /// </summary>
    /// <param name="allowOnDemandBuild">未構築時に on-demand 生成を許可するかどうか。</param>
    /// <returns>score snapshot。未取得の場合は null。</returns>
    private ScoreSnapshot GetScoreSnapshotForLookup(bool allowOnDemandBuild)
    {
        ScoreSnapshot snapshot;
        lock (lockScoreSnapshot)
        {
            snapshot = scoreSnapshot;
        }
        if (snapshot != null || !allowOnDemandBuild)
        {
            return snapshot;
        }
        RefreshScoreSnapshotFromCurrentScores("on_demand");
        lock (lockScoreSnapshot)
        {
            return scoreSnapshot;
        }
    }

    internal ChartScoreSnapshot ResolveChartScoreSnapshot(ChartFileKind kind, string path, string md5, string sha256)
    {
        if (kind != ChartFileKind.Bms)
        {
            return ChartScoreSnapshot.NoScore(path);
        }

        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: false);
        if (snapshot == null)
        {
            return ChartScoreSnapshot.NoScore(path);
        }

        BMSScore score = null;
        if (snapshot.ActiveScoreSource == ActiveScoreSource.Beatoraja
            && !string.IsNullOrWhiteSpace(sha256)
            && snapshot.ScoresBySha256?.TryGetValue(sha256, out BMSScore beatorajaScore) == true)
        {
            score = beatorajaScore;
        }
        else if (snapshot.ActiveScoreSource == ActiveScoreSource.Lr2
            && !string.IsNullOrWhiteSpace(md5)
            && snapshot.ScoresByHash?.TryGetValue(md5, out BMSScore lr2Score) == true)
        {
            score = lr2Score;
        }
        return ChartScoreSnapshot.FromBmsScore(score, path);
    }

    private int ApplyCurrentScoreSnapshotToFiles(IEnumerable<BMSFile> bmsFiles)
    {
        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
        return ApplyScoreSnapshotToFilesReplacingExisting(bmsFiles, snapshot);
    }

    private int ApplyScoreSnapshotToFilesReplacingExisting(IEnumerable<BMSFile> bmsFiles, ScoreSnapshot snapshot)
    {
        List<BMSFile> targetFiles = [.. (bmsFiles ?? []).Where(file => file != null)];
        foreach (BMSFile file in targetFiles)
        {
            file.bmsScore = null;
        }
        if (snapshot == null || targetFiles.Count == 0)
        {
            return 0;
        }
        return snapshot.ActiveScoreSource switch
        {
            ActiveScoreSource.Lr2 => irService.ApplyKnownScoresToFilesAndCount(targetFiles, snapshot.ScoresByHash, null),
            ActiveScoreSource.Beatoraja => irService.ApplyKnownScoresToFilesAndCount(targetFiles, null, snapshot.ScoresBySha256),
            _ => 0,
        };
    }

    private void ReportLibraryInitializationProgress(
        LibraryInitializationProgressStage stage,
        string scannerLabel = null,
        int totalCount = 0,
        int processedCount = 0,
        string currentPath = null,
        bool force = false)
    {
        ReportLibraryInitializationProgressCore(
            stage,
            scannerLabel,
            totalCount,
            processedCount,
            currentPath,
            force,
            retainLeadingLr2FileDiffProgress: false);
    }

    private void ReportLibraryInitializationProgressCore(
        LibraryInitializationProgressStage stage,
        string scannerLabel,
        int totalCount,
        int processedCount,
        string currentPath,
        bool force,
        bool retainLeadingLr2FileDiffProgress)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force)
        {
            long last = Interlocked.Read(ref lastLibraryInitializationProgressReportTimestamp);
            double elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;
            if (last != 0L && elapsedMs < 150.0)
            {
                return;
            }
        }
        Interlocked.Exchange(ref lastLibraryInitializationProgressReportTimestamp, now);
        bool schedulePublication;
        long publicationVersion;
        lock (lockLibraryInitializationProgress)
        {
            string normalizedScannerLabel = scannerLabel ?? string.Empty;
            int normalizedTotalCount = Math.Max(0, totalCount);
            int normalizedProcessedCount = Math.Max(0, processedCount);
            string normalizedCurrentPath = currentPath ?? string.Empty;
            LibraryInitializationProgressSnapshot previous = pendingLibraryInitializationProgress;
            if (previous.Stage == stage
                && previous.ScannerLabel == normalizedScannerLabel
                && previous.TotalCount == normalizedTotalCount
                && previous.ProcessedCount == normalizedProcessedCount
                && previous.CurrentPath == normalizedCurrentPath)
            {
                return;
            }

            publicationVersion = previous.Version + 1L;
            LibraryInitializationProgressSnapshot next = new(
                publicationVersion,
                stage,
                normalizedScannerLabel,
                normalizedTotalCount,
                normalizedProcessedCount,
                normalizedCurrentPath);
            if (retainLeadingLr2FileDiffProgress && IsStrictFileDiffIntermediate(next))
            {
                retainedLeadingLr2FileDiffProgress ??= next;
            }
            else if (!retainLeadingLr2FileDiffProgress
                || stage != LibraryInitializationProgressStage.FileDiff
                || normalizedProcessedCount == 0
                || normalizedTotalCount <= 1)
            {
                // A generic progress report, stage transition, or non-counted
                // FileDiff report makes any earlier leading snapshot stale for
                // the current presentation.  Only the LR2 folder-file callback
                // can retain the bounded leading frame; its terminal report is
                // kept until that frame has drained.
                retainedLeadingLr2FileDiffProgress = null;
            }
            pendingLibraryInitializationProgress = next;
            schedulePublication = !libraryInitializationProgressPublicationScheduled;
            libraryInitializationProgressPublicationScheduled = true;
        }
        if (!schedulePublication)
        {
            return;
        }
        ScheduleLibraryInitializationProgressPublication(publicationVersion);
    }

    private void ReportLr2FolderFileDiffProgress(int totalCount, int processedCount, string currentPath)
    {
        ReportLibraryInitializationProgressCore(
            LibraryInitializationProgressStage.FileDiff,
            scannerLabel: null,
            totalCount: totalCount,
            processedCount: processedCount,
            currentPath: currentPath,
            force: processedCount == 1 || processedCount >= totalCount,
            retainLeadingLr2FileDiffProgress: true);
    }

    private void ScheduleLibraryInitializationProgressPublication(
        long publicationVersion,
        UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        IUiScheduledOperation publication;
        try
        {
            publication = uiScheduler.Schedule(DrainLibraryInitializationProgressPublication, priority);
        }
        catch (Exception ex)
        {
            DiscardRetainedLeadingLr2FileDiffProgress(publicationVersion);
            ResetLibraryInitializationProgressPublicationSchedule();
            LogInstallPerformanceWarn(
                "library_initialization_progress_publication_schedule_failed version="
                + publicationVersion
                + " exception="
                + ex.GetType().Name);
            return;
        }
        if (!publication.IsAccepted)
        {
            DiscardRetainedLeadingLr2FileDiffProgress(publicationVersion);
            ResetLibraryInitializationProgressPublicationSchedule();
            LogInstallPerformanceWarn(
                "library_initialization_progress_publication_rejected reason="
                + publication.RejectionReason);
            return;
        }
        _ = publication.Completion.ContinueWith(
            task =>
            {
                DiscardRetainedLeadingLr2FileDiffProgress(publicationVersion);
                ResetLibraryInitializationProgressPublicationSchedule();
                LogInstallPerformanceWarn(
                    task.IsCanceled || publication.IsAborted
                        ? "library_initialization_progress_publication_canceled"
                        : "library_initialization_progress_publication_failed exception="
                            + (task.Exception?.GetBaseException().GetType().Name ?? "unknown"));
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ResetLibraryInitializationProgressPublicationSchedule()
    {
        lock (lockLibraryInitializationProgress)
        {
            libraryInitializationProgressPublicationScheduled = false;
        }
    }

    private void DiscardRetainedLeadingLr2FileDiffProgress(long publicationVersion)
    {
        lock (lockLibraryInitializationProgress)
        {
            if (retainedLeadingLr2FileDiffProgress?.Version == publicationVersion)
            {
                retainedLeadingLr2FileDiffProgress = null;
            }
        }
    }

    private static bool IsStrictFileDiffIntermediate(LibraryInitializationProgressSnapshot snapshot)
    {
        return snapshot.Stage == LibraryInitializationProgressStage.FileDiff
            && snapshot.TotalCount > 1
            && snapshot.ProcessedCount > 0
            && snapshot.ProcessedCount < snapshot.TotalCount;
    }

    private void DrainLibraryInitializationProgressPublication()
    {
        LibraryInitializationProgressSnapshot snapshot;
        bool scheduleBackgroundContinuation = false;
        lock (lockLibraryInitializationProgress)
        {
            if (retainedLeadingLr2FileDiffProgress == null
                && pendingLibraryInitializationProgress.Version
                    == Volatile.Read(ref publishedLibraryInitializationProgress).Version)
            {
                // A completion boundary may synchronously drain a queued
                // publication.  The already-enqueued operation then becomes
                // stale and must not replay the same snapshot after completion.
                libraryInitializationProgressPublicationScheduled = false;
                return;
            }
            snapshot = retainedLeadingLr2FileDiffProgress ?? pendingLibraryInitializationProgress;
            if (retainedLeadingLr2FileDiffProgress != null)
            {
                // Publish only the retained leading frame in this UI turn.  If
                // a newer LR2 frame is pending, keep this bounded owner claimed
                // and hand that frame to one Background continuation.  This
                // gives the leading frame a dispatcher/render opportunity
                // before the terminal frame without retaining a history.
                scheduleBackgroundContinuation =
                    pendingLibraryInitializationProgress.Version != snapshot.Version;
                retainedLeadingLr2FileDiffProgress = null;
            }
        }

        PublishLibraryInitializationProgressSnapshot(snapshot);

        long nextVersion = 0L;
        UiSchedulePriority nextPriority = UiSchedulePriority.Normal;
        lock (lockLibraryInitializationProgress)
        {
            if (scheduleBackgroundContinuation)
            {
                // The Background continuation owns the newer pending frame;
                // do not enqueue another Normal drain from this turn.
                nextVersion = pendingLibraryInitializationProgress.Version;
                nextPriority = UiSchedulePriority.Background;
            }
            else if (pendingLibraryInitializationProgress.Version == snapshot.Version)
            {
                libraryInitializationProgressPublicationScheduled = false;
            }
            else
            {
                nextVersion = pendingLibraryInitializationProgress.Version;
            }
        }
        if (nextVersion != 0L)
        {
            ScheduleLibraryInitializationProgressPublication(nextVersion, nextPriority);
        }
    }

    private void PublishLibraryInitializationProgressSnapshot(
        LibraryInitializationProgressSnapshot snapshot)
    {
        lock (lockLibraryInitializationProgress)
        {
            Volatile.Write(ref publishedLibraryInitializationProgress, snapshot);
            _LibraryInitializationProgressStage = snapshot.Stage;
            _LibraryInitializationProgressScannerLabel = snapshot.ScannerLabel;
            _LibraryInitializationProgressTotalCount = snapshot.TotalCount;
            _LibraryInitializationProgressProcessedCount = snapshot.ProcessedCount;
            _LibraryInitializationProgressCurrentPath = snapshot.CurrentPath;
        }

        try
        {
            RaisePropertyChanged(nameof(LibraryInitializationProgressVersion));
        }
        catch (Exception ex)
        {
            // Progress observation is presentation-only.  A subscriber must
            // not prevent the next bounded frame or the durable apply from
            // completing.
            LogInstallPerformanceWarn(
                "library_initialization_progress_publication_observer_failed exception="
                + ex.GetType().Name);
        }
    }

    private void CompleteLibraryDatabaseLoadProgress()
    {
        LibraryDatabaseLoadCompletedVersion++;
    }

    private void CompleteLibraryFileEnumerationProgress()
    {
        LibraryFileEnumerationCompletedVersion++;
    }

    private void CompleteLibraryFileDiffProgress()
    {
        LibraryFileDiffCompletedVersion++;
    }

    /// <summary>
    /// Queues the LR2 file-diff completion notification behind the progress
    /// publication already queued by the prepared apply.  The completion is a
    /// presentation boundary; the durable apply has already returned when this
    /// action is scheduled.
    /// </summary>
    private void ScheduleLibraryFileDiffCompletionAfterLr2Apply()
    {
        int completionInvoked = 0;
        void CompleteOnce(bool drainPendingProgress)
        {
            if (Interlocked.Exchange(ref completionInvoked, 1) == 0)
            {
                if (drainPendingProgress)
                {
                    // The apply has already reported its terminal frame, but
                    // the coalescing publication may still be queued.  Drain
                    // that bounded owner before exposing FileDiff completion
                    // so the startup-progress consumer cannot close the phase
                    // first.
                    DrainLibraryInitializationProgressPublication();
                }
                CompleteLibraryFileDiffProgress();
            }
        }

        IUiScheduledOperation completion;
        try
        {
            completion = uiScheduler.Schedule(
                () => CompleteOnce(drainPendingProgress: true),
                UiSchedulePriority.Background);
        }
        catch (Exception ex)
        {
            LogInstallPerformanceWarn(
                "library_file_diff_completion_schedule_failed exception="
                + ex.GetType().Name);
            CompleteOnce(drainPendingProgress: false);
            return;
        }
        if (completion == null || !completion.IsAccepted)
        {
            LogInstallPerformanceWarn(
                "library_file_diff_completion_schedule_rejected reason="
                + (completion?.RejectionReason ?? "unknown"));
            CompleteOnce(drainPendingProgress: false);
            return;
        }

        _ = completion.Completion.ContinueWith(
            task => LogInstallPerformanceWarn(
                task.IsCanceled || completion.IsAborted
                    ? "library_file_diff_completion_canceled"
                    : "library_file_diff_completion_failed exception="
                        + (task.Exception?.GetBaseException().GetType().Name ?? "unknown")),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static bool ShouldIncludeLr2TextSurface(BmsLibraryOptionsSnapshot options)
    {
        return options?.OperationModeLR2DB == true;
    }

    internal static bool ShouldIncludeLr2DirectorySurface(BmsLibraryOptionsSnapshot options)
    {
        return options?.OperationModeLR2DB == true;
    }

    public void Initialize(List<Action> tasksContinuation, SemaphoreSlim semaphore = null, bool? reloadScoresOnly = null)
    {
        LibraryInitializeMode mode = reloadScoresOnly == true
            ? LibraryInitializeMode.ScoreOnly
            : (reloadScoresOnly == false ? LibraryInitializeMode.FullReinitialize : LibraryInitializeMode.Startup);
        Initialize(tasksContinuation, semaphore, mode);
    }

    public void InitializeStartup(List<Action> tasksContinuation, SemaphoreSlim semaphore = null)
    {
        Initialize(tasksContinuation, semaphore, LibraryInitializeMode.Startup);
    }

    internal void InitializeStartup(
        List<Action> tasksContinuation,
        SemaphoreSlim semaphore,
        in PerformanceInteraction performanceInteraction)
    {
        InitializeCore(
            tasksContinuation,
            semaphore,
            LibraryInitializeMode.Startup,
            performanceInteraction);
    }

    public void Reinitialize(List<Action> tasksContinuation = null, SemaphoreSlim semaphore = null)
    {
        Initialize(tasksContinuation, semaphore, LibraryInitializeMode.FullReinitialize);
    }

    public void InitializeScoresOnly(List<Action> tasksContinuation, SemaphoreSlim semaphore = null)
    {
        Initialize(tasksContinuation, semaphore, LibraryInitializeMode.ScoreOnly);
    }

    /// <summary>
    /// BMS ライブラリの初期化を行います。song.db からの譜面データ読み込み、ファイルスキャン、
    /// スコア / 保守情報の取得を統合的に実行します。
    /// </summary>
    /// <param name="tasksContinuation">初期化中に並行で実行する追加タスクのリスト。</param>
    /// <param name="semaphore">追加タスクの同期用セマフォ。</param>
    /// <param name="mode">初期化 mode。</param>
    public void Initialize(List<Action> tasksContinuation, SemaphoreSlim semaphore, LibraryInitializeMode mode)
    {
        InitializeCore(tasksContinuation, semaphore, mode, null);
    }

    private void InitializeCore(
        List<Action> tasksContinuation,
        SemaphoreSlim semaphore,
        LibraryInitializeMode mode,
        PerformanceInteraction? parentPerformanceInteraction)
    {
        PerformanceInteraction performanceInteraction =
            parentPerformanceInteraction?.ForRoute("startup_library")
            ?? PerformanceInteraction.Start("startup_library", (long)mode);
        if (Net10PerformanceLog.IsEnabled)
        {
            if (!parentPerformanceInteraction.HasValue)
            {
                Net10PerformanceLog.Write(
                    performanceInteraction,
                    "input_accepted",
                    "mode=" + mode);
            }
            Net10PerformanceLog.Write(
                performanceInteraction,
                "owner_started",
                "mode=" + mode);
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool scheduleDeferredInstallableMaintenance = false;
        string deferredMaintenanceReason = mode == LibraryInitializeMode.FullReinitialize ? "full_reinitialize" : "initialize";
        bool isScoreOnly = mode == LibraryInitializeMode.ScoreOnly;
        bool isStartup = mode == LibraryInitializeMode.Startup;
        if (mode == LibraryInitializeMode.FullReinitialize
            && TryBlockLr2SongDbSyncMutation(nameof(Reinitialize)))
        {
            return;
        }
        SongTableLoadResult initialSongTableLoadResult = null;

        LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(Initialize),
            showMessage: !isScoreOnly);
        if (mutationReservation == null)
        {
            return;
        }
        LibraryFileMutationCapability mutationCapability;
        try
        {
            mutationCapability = mutationReservation.CreateMutationCapability();
            mutationCapability.Validate(lr2SynchronizationOwner);
        }
        catch
        {
            mutationReservation.Dispose();
            throw;
        }
        ResetEverythingFallbackWarningQueue();
        bool songTblLoad = !isScoreOnly;
        bool startupFileScanRequired = isStartup && !string.IsNullOrWhiteSpace(startupRequiredFileScanReason);
        bool songTblFileCheck = mode == LibraryInitializeMode.FullReinitialize || (isStartup && (options.ScanBmsFilesOnStartup || startupFileScanRequired));
        bool setMaintenanceInfo = !isScoreOnly;
        bool flag = !isScoreOnly;
        bool fileScanLifecycleStarted = false;
        long fileScanGeneration = 0L;
        LibraryDirectoryPreflightRequest directoryPreflightRequest = null;
        if (startupFileScanRequired)
        {
            LogInstallPerformance("startup_file_scan_required reason=" + startupRequiredFileScanReason + " scanSetting=" + options.ScanBmsFilesOnStartup.ToString().ToLowerInvariant());
        }
        DateTime now;
        InitializationExecutionResult initializeResult;
        IDisposable installTableCollectionMutationScope = null;
        List<Action> initializationPostLeaseEffects = [];
        try
        {
            if (!isScoreOnly)
            {
                directoryPreflightRequest = CaptureDirectoryPreflightRequest(options);
                directoryPreflightService.EnsureAvailable(
                    directoryPreflightRequest,
                    probeOutputBases: true);
            }
            if (songTblFileCheck)
            {
                List<string> fileCheckPrefetchDirectories =
                    [.. directoryPreflightRequest.ScanRootDirectories];
                fileScanGeneration = libraryFileScanPipelineOwner.BeginFileScanRequest(
                    options,
                    fileCheckPrefetchDirectories,
                    isStartup ? "initialize" : "full_reinitialize",
                    scannerLabel => ReportLibraryInitializationProgress(
                        LibraryInitializationProgressStage.FileEnumeration,
                        scannerLabel,
                        force: true),
                    directoryPreflightRequest);
                fileScanLifecycleStarted = true;
            }
            try
            {
                NLogWrapper.DebuggerLogger?.Trace("hazimari: " + GC.GetTotalMemory(forceFullCollection: false));
                LogInstallPerformance("init_library_enter mode=" + mode + " songTblLoad=" + songTblLoad.ToString().ToLowerInvariant() + " songTblFileCheck=" + songTblFileCheck.ToString().ToLowerInvariant() + " setMaintenanceInfo=" + setMaintenanceInfo.ToString().ToLowerInvariant() + " installTblCheck=" + flag.ToString().ToLowerInvariant() + " rwlockInitAll currentRead=" + rwlockBMSFilesInitializedAll.CurrentReadCount + " lockingRead=" + rwlockBMSFilesInitializedAll.LockingReadCount + " lockingWrite=" + rwlockBMSFilesInitializedAll.LockingWriteCount + " waitingWrite=" + rwlockBMSFilesInitializedAll.WaitingWriteCount);
                if (isStartup)
                {
                    TryImportChartInfoMetadataBundleAtStartup();
                }
                packageLifecycleOwner.StartupReadiness.Reset();
                using (rwlockBMSFilesInitializedAll.GetWriterGuard())
                {
                    LogInstallPerformance("init_library_lock_acquired rwlockInitAll currentRead=" + rwlockBMSFilesInitializedAll.CurrentReadCount + " lockingRead=" + rwlockBMSFilesInitializedAll.LockingReadCount + " lockingWrite=" + rwlockBMSFilesInitializedAll.LockingWriteCount + " waitingWrite=" + rwlockBMSFilesInitializedAll.WaitingWriteCount);
                    now = DateTime.Now;
                    initializeResult = initializationService.RunInitialize(
                        tasksContinuation,
                        semaphore,
                        delegate
                        {
                            using (rwlockBMSFilesInitializedMin.GetWriterGuard())
                            {
                                _initialize(songTblLoad: songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, trackLibraryDatabaseProgress: true, songTableLoadResultObserver: result => initialSongTableLoadResult = result, postLeaseEffectObserver: initializationPostLeaseEffects.Add, directoryPreflightRequest: directoryPreflightRequest);
                                if (songTblLoad)
                                {
                                    packageLifecycleOwner.StartupReadiness.MarkCatalogLoaded();
                                }
                                if (songTblFileCheck)
                                {
                                    libraryFileScanPipelineOwner.StartActiveNormalFolderMtimeSnapshot(fileScanGeneration);
                                }
                            }
                        },
                        delegate
                        {
                            Lr2FolderFileDiffPreparationResult scanPreparation = _initialize(
                                songTblLoad: false,
                                scoreTblrLoad: false,
                                songTblFileCheck: songTblFileCheck,
                                setMainteInfo: false,
                                updateIrScore: true,
                                installTblCheck: false,
                                trackLibraryFileCheckProgress: true,
                                fileScanGeneration: fileScanGeneration,
                                fileScanReason: isStartup ? "initialize" : "full_reinitialize",
                                postLeaseEffectObserver: initializationPostLeaseEffects.Add,
                                directoryPreflightRequest: directoryPreflightRequest);
                            if (scanPreparation?.Request != null)
                            {
                                libraryFileScanPipelineOwner.ApplyPreparedLr2FolderFileDiffForFileMutation(
                                    options,
                                    isStartup ? "initialize" : "full_reinitialize",
                                    scanPreparation,
                                    mutationCapability,
                                    ReportLr2FolderFileDiffProgress);
                                ScheduleLibraryFileDiffCompletionAfterLr2Apply();
                            }
                            if (!isScoreOnly)
                            {
                                packageLifecycleOwner.StartupReadiness.MarkDestinationResourceIndexReady();
                            }
                        },
                        delegate
                        {
                            _initialize(
                                songTblLoad: false,
                                scoreTblrLoad: false,
                                songTblFileCheck: false,
                                setMainteInfo: false,
                                updateIrScore: false,
                                installTblCheck: false,
                                postLeaseEffectObserver: initializationPostLeaseEffects.Add,
                                directoryPreflightRequest: directoryPreflightRequest);
                        });
                    scheduleDeferredInstallableMaintenance = setMaintenanceInfo;
                    TimeSpan timeSpan = DateTime.Now - now;
                    NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());

                }
            }
            finally
            {
                installTableCollectionMutationScope?.Dispose();
                installTableCollectionMutationScope = null;
            }

            if (flag)
            {
                IReadOnlyList<string> registeredBmsRoots =
                    lr2SearchRootSnapshotOwner.CaptureForUpdate(options).RequestedRoots;
                installTableCollectionMutationScope = packageLifecycleOwner.BeginCollectionMutationScope();
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        _ = packageLifecycleOwner.ReloadInstallTable(
                            initializationService,
                            dbGateway,
                            registeredBmsRoots,
                            ContainsInstalledChartUnsafe);
                    }
                }
                packageLifecycleOwner.StartupReadiness.MarkPendingPackagesRestored();
            }
            if (installTableCollectionMutationScope != null)
            {
                IDisposable collectionMutationScope = installTableCollectionMutationScope;
                installTableCollectionMutationScope = null;
                TryInvokePostLeaseNotification(
                    collectionMutationScope.Dispose,
                    "initialization_collection_publication_failed");
            }
        }
        catch
        {
            if (fileScanLifecycleStarted)
            {
                libraryFileScanPipelineOwner.AbortActiveFileScan(fileScanGeneration);
            }
            ResetEverythingFallbackWarningQueue();
            throw;
        }
        finally
        {
            installTableCollectionMutationScope?.Dispose();
            installTableCollectionMutationScope = null;
            mutationCapability.Dispose();
            mutationReservation?.Dispose();
        }
        FlushPostLeaseEffects(initializationPostLeaseEffects, diagnosticEffects: null);
        if (!isScoreOnly && initialSongTableLoadResult != null)
        {
            var approvedLeapYearRepairCandidates = new List<LeapYearFolderRepairCandidate>();
            foreach (LeapYearFolderRepairCandidate candidate in initialSongTableLoadResult.LeapYearRepairCandidates)
            {
                if (dialogService?.Show(
                        string.Format(
                            Resources.Warn_LR2LeapYearFolderDetected,
                            candidate.Path,
                            candidate.LastWriteTime.ToShortDateString(),
                            DateTime.Now.ToShortDateString()),
                        Resources.MessageBoxTitle_Warning,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Exclamation,
                        MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    approvedLeapYearRepairCandidates.Add(candidate);
                }
            }

            LeapYearFolderRepairResult leapYearRepairResult = new();
            if (approvedLeapYearRepairCandidates.Count > 0)
            {
                using LibraryFileMutationLease repairReservation = TryBeginLr2SongDbSyncBlockedMutation(
                    nameof(Initialize) + ".leap_year_repair",
                    showMessage: true);
                if (repairReservation != null)
                {
                    leapYearRepairResult = initializationService.RepairLeapYearFolderTimestamps(
                        dbGateway,
                        approvedLeapYearRepairCandidates,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        GetDisplayedExceptionMessage);
                }
            }
            foreach ((string path, Exception exception) in leapYearRepairResult.Failures)
            {
                ShowOperationDialog(
                    string.Format(Resources.Error_FailedToChangeDate, DisplayedExceptionMessage.Format(exception)),
                    Resources.MessageBoxTitle_Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK);
            }
            if (leapYearRepairResult.RepairedCount > 0
                || (initialSongTableLoadResult.LeapYearDetected && approvedLeapYearRepairCandidates.Count == 0))
            {
                ShowOperationDialog(
                    Resources.Warn_LR2LeapYearBugDetected,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
        }
        if (flag)
        {
            if (packageLifecycleOwner.StartupReadiness.CanStartInstallEstimation())
            {
                BackgroundPendingEstimatePreparationResult startupEstimatePreparation;
                List<ChartPackage> startupPendingPackageSnapshot;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                using (rwlockPendingInstallCharts.GetReaderGuard())
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    // Capture package references while the model is stable;
                    // source-surface enumeration and hash materialization are
                    // deliberately performed after these short snapshot guards
                    // have been released.
                    startupPendingPackageSnapshot = [.. ChartPackagesPending.Where(package => package != null)];
                }
                startupEstimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(
                    startupPendingPackageSnapshot,
                    PendingInstallEstimateBatchSource.StartupRestore);
                foreach (ChartPackage deferredPackage in startupEstimatePreparation.DeferredPackages)
                {
                    startupEstimatePreparation.DeferredSourceHealthByPackage.TryGetValue(deferredPackage, out int sourceHealth);
                    LogPendingEstimateSkippedPackage("startup_restore", deferredPackage, sourceHealth);
                }
                if (startupEstimatePreparation.EstimablePackages.Count > 0)
                {
                    QueuePendingInstallEstimateBatch(new PendingInstallEstimateBatchRequest(
                        PendingInstallEstimateBatchSource.StartupRestore,
                        startupEstimatePreparation.EstimablePackages,
                        Resources.Pending_estimate_queue_startup_display_name,
                        deferredPackageCount: startupEstimatePreparation.DeferredPackages.Count,
                        batchSourceSnapshot: startupEstimatePreparation.BatchSourceSnapshot,
                        performanceInteraction: performanceInteraction.ForRoute("install_estimation")));
                }
            }
            else
            {
                LogInstallPerformance("startup_install_estimation_blocked reason=" + packageLifecycleOwner.StartupReadiness.GetInstallEstimationBlockedReason());
            }
        }
        DirectoryResourceLookupCache installableLookupCacheSnapshot = null;
        int catalogRowCount = 0;
        int bmsonRowCount = 0;
        int resourceIndexDirectoryCount = 0;
        int pendingPackageCount = 0;
        int pendingEstimateQueueBatchCount = 0;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            installableLookupCacheSnapshot = libraryResourceIndexOwner.CaptureSnapshot().DirectoryLookupCache;
            catalogRowCount = BMSFiles?.Count ?? 0;
            bmsonRowCount = BmsonSongs?.Count ?? 0;
            resourceIndexDirectoryCount = installableLookupCacheSnapshot?.Count ?? 0;
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            pendingPackageCount = ChartPackagesPending.Count;
        }
        pendingEstimateQueueBatchCount = GetPendingEstimateQueuedBatchCount(GetPendingEstimateQueueStatusSnapshot());
        long installableElapsedMs = (long)(DateTime.Now - now).TotalMilliseconds;
        bool scheduleDeferredMaintenanceHydration = !isScoreOnly && songTblLoad;
        if (packageLifecycleOwner.StartupReadiness.TryMarkInstallEstimationReady())
        {
            LogInstallPerformance("startup_install_estimation_ready elapsedMs=" + installableElapsedMs
                + " catalogRows=" + catalogRowCount
                + " bmsonRows=" + bmsonRowCount
                + " resourceIndexReady=" + packageLifecycleOwner.StartupReadiness.DestinationResourceIndexReady.ToString().ToLowerInvariant()
                + " resourceIndexDirectories=" + resourceIndexDirectoryCount
                + " pendingPackages=" + pendingPackageCount
                + " pendingEstimateQueueBatches=" + pendingEstimateQueueBatchCount
                + " lazy_hash_cache_entries=" + (installableLookupCacheSnapshot?.LazyHashCacheEntryCount ?? 0)
                + " lazy_hash_build_ms=" + (installableLookupCacheSnapshot?.LazyHashBuildMs ?? 0L)
                + " lazy_hash_lookup_count=" + (installableLookupCacheSnapshot?.LazyHashLookupCount ?? 0L));
        }
        if (packageLifecycleOwner.StartupReadiness.TryMarkInstallReady())
        {
            LogInstallPerformance("startup_install_ready elapsedMs=" + installableElapsedMs
                + " pendingPackages=" + pendingPackageCount);
        }
        TimeSpan timeSpan2 = DateTime.Now - now;
        NLogWrapper.DebuggerLogger?.Trace(timeSpan2.ToString());
        bool chartInfoHydrationScheduled = false;
        if (!isScoreOnly)
        {
            QueueDeferredChartInfoHydration(deferredMaintenanceReason, queueFullBackfillAfterHydration: true);
            chartInfoHydrationScheduled = true;
        }
        if (scheduleDeferredMaintenanceHydration)
        {
            QueueDeferredMaintenanceHydration(deferredMaintenanceReason);
        }
        if (scheduleDeferredInstallableMaintenance)
        {
            string installableDependency = chartInfoHydrationScheduled ? "chart_info_hydration" : null;
            if (scheduleDeferredMaintenanceHydration)
            {
                installableDependency = string.IsNullOrWhiteSpace(installableDependency)
                    ? "maintenance_hydration"
                    : installableDependency + ",maintenance_hydration";
            }
            QueueDeferredInstallableMaintenance(
                deferredMaintenanceReason,
                installableElapsedMs,
                installableDependency);
        }
        else
        {
            LogInstallPerformance("init_library_installable critical_ms=" + installableElapsedMs + " deferred_ms=0");
        }
        QueuePostInitializeGarbageCollection(mode.ToString());
        LogInstallPerformance("init_library phase1_min_load_ms=" + initializeResult.Phase1MinLoadMs + " phase2_scan_maint_ms=" + initializeResult.Phase2ScanMaintMs + " phase3_install_maintenance_ms=" + initializeResult.Phase3InstallMaintenanceMs + " wait_continuation_ms=" + initializeResult.WaitContinuationMs + " wait_continuation_start_ms=" + initializeResult.WaitBeforeContinuationStartMs + " wait_continuation_signal_ms=" + initializeResult.WaitForContinuationSignalMs + " wait_continuation_tasks_ms=" + initializeResult.WaitForContinuationTasksMs + " total_ms=" + initializeResult.TotalMs + " set_maintenance_enabled=" + setMaintenanceInfo.ToString().ToLowerInvariant());
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "data_ready",
                "mode=" + mode
                + " catalogRows=" + catalogRowCount
                + " bmsonRows=" + bmsonRowCount
                + " totalMs=" + initializeResult.TotalMs);
        }
    }

    private long postInitializeGcGeneration;

    private void QueuePostInitializeGarbageCollection(string reason)
    {
        if (TrySkipForShutdown("post_initialize_gc", reason))
        {
            return;
        }
        long generation = Interlocked.Increment(ref postInitializeGcGeneration);
        PerformanceInteraction performanceInteraction =
            PerformanceInteraction.Existing("post_initialize_gc", generation, generation);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "owner_queued",
                "reason=" + (reason ?? "unknown")
                + " owner=required_scheduler_idle"
                + FormatStartupBackgroundWorkSnapshot());
        }

        Task Work()
        {
            string terminalStage = null;
            string terminalFields = null;
            try
            {
                if (IsShutdownRequested)
                {
                    LogInstallPerformance("post_initialize_gc skipped reason=shutdown_requested requestReason=" + (reason ?? "unknown"));
                    terminalStage = "terminal_skipped";
                    terminalFields = "reason=shutdown_requested";
                    return Task.CompletedTask;
                }
                long managedBytesBefore = GC.GetTotalMemory(forceFullCollection: false);
                int gen0Before = GC.CollectionCount(0);
                int gen1Before = GC.CollectionCount(1);
                int gen2Before = GC.CollectionCount(2);
                if (Net10PerformanceLog.IsEnabled)
                {
                    Net10PerformanceLog.Write(
                        performanceInteraction,
                        "owner_started",
                        "owner=required_scheduler_idle"
                        + FormatStartupBackgroundWorkSnapshot()
                        + " managedBytesBefore=" + managedBytesBefore
                        + " gen0Before=" + gen0Before
                        + " gen1Before=" + gen1Before
                        + " gen2Before=" + gen2Before);
                }
                var stopwatch = Stopwatch.StartNew();
                GC.Collect();
                stopwatch.Stop();
                long managedBytesAfter = GC.GetTotalMemory(forceFullCollection: false);
                NLogWrapper.DebuggerLogger?.Trace("owari: " + managedBytesAfter);
                LogInstallPerformance("post_initialize_gc done"
                    + " reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " managedBytes=" + managedBytesAfter);
                terminalStage = "terminal_applied";
                terminalFields = "elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " managedBytesBefore=" + managedBytesBefore
                    + " managedBytesAfter=" + managedBytesAfter
                    + " reclaimedManagedBytes=" + Math.Max(0L, managedBytesBefore - managedBytesAfter)
                    + " gen0Delta=" + (GC.CollectionCount(0) - gen0Before)
                    + " gen1Delta=" + (GC.CollectionCount(1) - gen1Before)
                    + " gen2Delta=" + (GC.CollectionCount(2) - gen2Before);
            }
            catch (Exception ex)
            {
                LogInstallPerformanceWarn("post_initialize_gc failed"
                    + " reason=" + (reason ?? "unknown")
                    + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                terminalStage = "terminal_failed";
                terminalFields = "exception=" + ex.GetType().Name;
            }
            finally
            {
                if (Net10PerformanceLog.IsEnabled && terminalStage != null)
                {
                    Net10PerformanceLog.Write(
                        performanceInteraction,
                        terminalStage,
                        terminalFields
                        + FormatStartupBackgroundWorkSnapshot());
                }
            }
            return Task.CompletedTask;
        }

        if (StartupBackgroundTaskScheduler != null)
        {
            if (!StartupBackgroundTaskScheduler("post_initialize_gc", reason ?? "queue", null, Work))
            {
                LogInstallPerformance("post_initialize_gc skipped reason=scheduler_rejected requestReason=" + (reason ?? "unknown"));
            }
            return;
        }
        LogInstallPerformance("post_initialize_gc skipped reason=no_scheduler requestReason=" + (reason ?? "unknown"));
    }

    private string FormatStartupBackgroundWorkSnapshot()
    {
        Func<StartupBackgroundWorkSnapshot> provider = StartupBackgroundWorkSnapshotProvider;
        if (provider == null)
        {
            return " startupBacklogState=unavailable";
        }
        StartupBackgroundWorkSnapshot snapshot = provider();
        return " startupQueued=" + snapshot.QueuedCount
            + " startupRunning=" + snapshot.RunningCount
            + " startupBacklog=" + snapshot.BacklogCount;
    }

    private void TryImportChartInfoMetadataBundleAtStartup()
    {
        catalogChartInfoOwner.TryImportMetadataBundle(applicationPathSnapshot.BaseDirectory, dbGateway);
    }

    /// <summary>
    /// Initialize から呼ばれる実際の初期化内部ロジックです。
    /// song.db からのデータ再取得、BMS ファイルのディレクトリ走査、スコア反映、保守テーブルチェックを順次実行します。
    /// </summary>
    private Lr2FolderFileDiffPreparationResult _initialize(
        bool songTblLoad = true,
        bool scoreTblrLoad = true,
        bool songTblFileCheck = true,
        bool setMainteInfo = true,
        bool updateIrScore = true,
        bool installTblCheck = true,
        bool trackLibraryDatabaseProgress = false,
        bool trackLibraryFileCheckProgress = false,
        long fileScanGeneration = 0L,
        string fileScanReason = "initialize",
        Action<SongTableLoadResult> songTableLoadResultObserver = null,
        Action<Action> postLeaseEffectObserver = null,
        LibraryDirectoryPreflightRequest directoryPreflightRequest = null)
    {
        var stopwatchInitialize = Stopwatch.StartNew();
        long songTblLoadMs = 0L;
        long scoreTblLoadMs = 0L;
        long songTblFileCheckMs = 0L;
        long setMaintenanceMs = 0L;
        long setModeMs = 0L;
        long setHealthMs = 0L;
        long setZeroNoteMs = 0L;
        long installTblCheckMs = 0L;
        int lr2IdAfterScoreLoad = 0;
        Lr2FolderFileDiffPreparationResult fileScanPreparation = null;
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool scoreOnlyLoad = !songTblLoad && scoreTblrLoad && !songTblFileCheck && !setMainteInfo && !installTblCheck;
        bool logRootNormalizationForFileScan = songTblFileCheck;
        List<string> bMSDirectories;
        BmsSearchRootNormalizationSnapshot rootNormalization;
        if (directoryPreflightRequest == null)
        {
            bMSDirectories = getBMSDirectories(out rootNormalization);
        }
        else
        {
            bMSDirectories = [.. directoryPreflightRequest.ScanRootDirectories];
            rootNormalization = CreateUpdateRootNormalizationSnapshot(
                directoryPreflightRequest,
                options);
        }
        if (logRootNormalizationForFileScan)
        {
            LogBmsSearchRootNormalization(fileScanReason, options, rootNormalization, bMSDirectories);
        }
        if (bMSDirectories.Count == 0 && fileScanGeneration == 0L)
        {
            songTblFileCheck = false;
        }
        if (songTblLoad)
        {
            var stopwatchSongTblLoad = Stopwatch.StartNew();
            if (trackLibraryDatabaseProgress)
            {
                ReportLibraryInitializationProgress(LibraryInitializationProgressStage.DatabaseLoad, force: true);
            }
            SongTableLoadResult songTableLoadResult = initializationService.LoadSongTable(
                dbGateway,
                options,
                dialogService,
                fileMutationService,
                targetOnlyFileMutationOptions,
                GetDisplayedExceptionMessage,
                LogInstallPerformance,
                message => NLogWrapper.DebuggerLogger?.Trace(message));
            songTableLoadResultObserver?.Invoke(songTableLoadResult);
            var stopwatchBmsFilesAssign = Stopwatch.StartNew();
            ApplyCatalogStorageRows(
                songTableLoadResult.LoadedFiles,
                songTableLoadResult.LoadedBmsonSongs,
                replaceBmsRows: true,
                replaceBmsonRows: true,
                notifyBmsRows: true,
                notifyBmsonRows: true,
                postLeaseNotificationObserver: postLeaseEffectObserver);
            stopwatchBmsFilesAssign.Stop();
            songTableLoadResult.BmsFilesAssignMs = stopwatchBmsFilesAssign.ElapsedMilliseconds;
            LogInstallPerformance("song_tbl_load_breakdown song_table_load_ms=" + songTableLoadResult.SongTableLoadMs + " song_normalize_loop_ms=" + songTableLoadResult.SongNormalizeLoopMs + " folder_table_load_ms=" + songTableLoadResult.FolderTableLoadMs + " folder_normalize_loop_ms=" + songTableLoadResult.FolderNormalizeLoopMs + " fix_apply_ms=" + songTableLoadResult.FixApplyMs + " storage_rows_assign_ms=" + songTableLoadResult.BmsFilesAssignMs + " commit_ms=" + songTableLoadResult.CommitMs);
            if (Net10PerformanceLog.IsEnabled)
            {
                PerformanceInteraction songTableInteraction =
                    PerformanceInteraction.Start("song_table", fileScanGeneration);
                Net10PerformanceLog.Write(
                    songTableInteraction,
                    "snapshot_query_projection",
                    "songs=" + songTableLoadResult.LoadedFiles.Count
                    + " bmson=" + songTableLoadResult.LoadedBmsonSongs.Count
                    + " queryMs=" + songTableLoadResult.SongTableLoadMs
                    + " normalizeMs=" + songTableLoadResult.SongNormalizeLoopMs
                    + " publishMs=" + songTableLoadResult.BmsFilesAssignMs);
            }
            stopwatchSongTblLoad.Stop();
            songTblLoadMs = stopwatchSongTblLoad.ElapsedMilliseconds;
            if (trackLibraryDatabaseProgress)
            {
                CompleteLibraryDatabaseLoadProgress();
            }
        }
        if (scoreTblrLoad)
        {
            var stopwatchScoreTblLoad = Stopwatch.StartNew();
            using (rwlockBMSScores.GetWriterGuard())
            {
                if (lr2ScoreDBPath != null || options.UseBeatorajaScoreDb)
                {
                    ScoreTableLoadResult scoreTableLoadResult = initializationService.LoadScoreTable(dbGateway, options);
                    LogInstallPerformance("score_tbl_load readOnly=" + scoreTableLoadResult.ReadOnly.ToString().ToLowerInvariant()
                        + " dbLockWaitMs=" + scoreTableLoadResult.DbLockWaitMs
                        + " source=" + scoreTableLoadResult.ActiveScoreSource
                        + " status=" + scoreTableLoadResult.Status
                        + " rows=" + scoreTableLoadResult.Scores.Count
                        + " beatorajaRows=" + scoreTableLoadResult.BeatorajaScoresBySha256.Count
                        + " lr2Id=" + scoreTableLoadResult.LR2Id
                        + " failure=" + (scoreTableLoadResult.FailureMessage ?? string.Empty)
                        + " lr2PlayHistorySchemaStatus=" + (scoreTableLoadResult.Lr2PlayHistorySchemaCheckResult?.Status.ToString() ?? "Unknown"));
                    lr2PlayHistorySchemaCheckResult = scoreTableLoadResult.Lr2PlayHistorySchemaCheckResult;
                    activeScoreSource = scoreTableLoadResult.ActiveScoreSource;
                    scoreTableLoadStatus = scoreTableLoadResult.Status;
                    scoreTableLoadFailureMessage = scoreTableLoadResult.FailureMessage ?? string.Empty;
                    if (scoreTableLoadResult.ActiveScoreSource == ActiveScoreSource.Beatoraja)
                    {
                        LR2ID = 0;
                        BMSScores = [];
                        beatorajaScoresBySha256 = new Dictionary<string, BMSScore>(scoreTableLoadResult.BeatorajaScoresBySha256, StringComparer.OrdinalIgnoreCase);
                    }
                    else if (scoreTableLoadResult.ActiveScoreSource == ActiveScoreSource.Lr2)
                    {
                        LR2ID = scoreTableLoadResult.LR2Id;
                        beatorajaScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
                        if (scoreTableLoadResult.Scores.Count > 0)
                        {
                            BMSScores = scoreTableLoadResult.Scores;
                        }
                        else
                        {
                            LR2ID = 0;
                            BMSScores = [];
                        }
                    }
                    else
                    {
                        LR2ID = 0;
                        BMSScores = [];
                        beatorajaScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    lr2PlayHistorySchemaCheckResult = null;
                    activeScoreSource = ActiveScoreSource.None;
                    scoreTableLoadStatus = ScoreTableLoadStatus.NotConfigured;
                    scoreTableLoadFailureMessage = string.Empty;
                    LR2ID = 0;
                    beatorajaScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
                    BMSScores = [];
                }
                scoreSourceGeneration++;
            }
            RefreshScoreSnapshotFromCurrentScores("score_tbl_load");
            if (scoreOnlyLoad || activeScoreSource == ActiveScoreSource.None)
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    ApplyCurrentScoreSnapshotToFiles(BMSFiles);
                }
            }
            lr2IdAfterScoreLoad = LR2ID;
            stopwatchScoreTblLoad.Stop();
            scoreTblLoadMs = stopwatchScoreTblLoad.ElapsedMilliseconds;
            if (!options.EnableDownloadLr2IrScoreAndDetectUnsent)
            {
                ClearScoreUnsentStatus();
            }
            TryStartIrScorePrefetch(lr2IdAfterScoreLoad, options, "score_tbl_load");
        }
        if (songTblFileCheck)
        {
            var stopwatchSongTblFileCheck = Stopwatch.StartNew();
            fileScanPreparation = libraryFileScanPipelineOwner.ApplyActiveFileScan(
                fileScanGeneration,
                trackLibraryFileCheckProgress,
                installDestinationStateOwner.CreateCleanupSnapshot(),
                postLeaseEffectObserver);
            stopwatchSongTblFileCheck.Stop();
            songTblFileCheckMs = stopwatchSongTblFileCheck.ElapsedMilliseconds;
        }
        else if (trackLibraryFileCheckProgress)
        {
            CompleteLibraryFileEnumerationProgress();
            CompleteLibraryFileDiffProgress();
        }
        RunChartDigestBackfill();
        if (setMainteInfo)
        {
            var stopwatchSetMaintenance = Stopwatch.StartNew();
            try
            {
                var stopwatchSetMode = Stopwatch.StartNew();
                setModeAndCommitToDB(BMSFiles);
                stopwatchSetMode.Stop();
                setModeMs = stopwatchSetMode.ElapsedMilliseconds;
                var stopwatchSetHealth = Stopwatch.StartNew();
                ApplyOwnedCatalogMaintenanceUnderExistingReservation(
                    "initialize_set_maintenance",
                    postLeaseEffectObserver: postLeaseEffectObserver);
                stopwatchSetHealth.Stop();
                setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
            }
            finally
            {
                IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
                IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
                IsWriteLockHeldInitializeBMSFilesZeroNote = false;
                stopwatchSetMaintenance.Stop();
                setMaintenanceMs = stopwatchSetMaintenance.ElapsedMilliseconds;
                GC.Collect();
                NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
            }
        }
        if (updateIrScore && activeScoreSource != ActiveScoreSource.None)
        {
            QueueDeferredScoreHydration("initialize_update_ir_score");
        }
        StartupRankingRefreshWorkPlan startupRankingRefreshPlan = StartupRankingRefreshPolicy.CreateWorkPlan(options);
        if (StartupRankingRefreshPolicy.ShouldQueue(
            updateIrScore,
            activeScoreSource,
            lr2ScoreDBPath != null,
            startupRankingRefreshPlan))
        {
            QueueDeferredRankingRefresh("initialize_update_ir_score");
        }
        stopwatchInitialize.Stop();
        LogInstallPerformance("init_library_internal song_tbl_load_ms=" + songTblLoadMs + " score_tbl_load_ms=" + scoreTblLoadMs + " song_tbl_file_check_ms=" + songTblFileCheckMs + " set_maintenance_ms=" + setMaintenanceMs + " set_mode_ms=" + setModeMs + " set_health_ms=" + setHealthMs + " set_zero_note_ms=" + setZeroNoteMs + " install_tbl_check_ms=" + installTblCheckMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
        return fileScanPreparation;
    }

    public void ReloadFileDiff()
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(ReloadFileDiff)))
        {
            return;
        }
        LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(ReloadFileDiff),
            showMessage: true);
        if (mutationReservation == null)
        {
            return;
        }
        BmsLibraryOptionsSnapshot options = null;
        LibraryDirectoryPreflightRequest directoryPreflightRequest = null;
        List<string> bmsDirectories = null;
        BmsSearchRootNormalizationSnapshot rootNormalization = null;
        ResetEverythingFallbackWarningQueue();
        long fileScanGeneration = 0L;
        var stopwatch = Stopwatch.StartNew();
        PerformanceInteraction performanceInteraction =
            PerformanceInteraction.Start("managed_file_diff");
        List<Action> postLeaseEffects = [];
        try
        {
            using (mutationReservation)
            using (LibraryFileMutationCapability mutationCapability = mutationReservation.CreateMutationCapability())
            {
                mutationCapability.Validate(lr2SynchronizationOwner);
                options = CurrentOptionsSnapshot;
                directoryPreflightRequest = CaptureDirectoryPreflightRequest(options);
                directoryPreflightService.EnsureAvailable(
                    directoryPreflightRequest,
                    probeOutputBases: true);
                bmsDirectories = [.. directoryPreflightRequest.ScanRootDirectories];
                rootNormalization = CreateUpdateRootNormalizationSnapshot(
                    directoryPreflightRequest,
                    options);
                if (Net10PerformanceLog.IsEnabled)
                {
                    Net10PerformanceLog.Write(
                        performanceInteraction,
                        "input_accepted",
                        "directories=" + bmsDirectories.Count);
                    Net10PerformanceLog.Write(
                        performanceInteraction,
                        "owner_started",
                        "directories=" + bmsDirectories.Count);
                }
                LogBmsSearchRootNormalization("reload_file_diff", options, rootNormalization, bmsDirectories);
                LogInstallPerformance("library_file_diff_reload start directories=" + bmsDirectories.Count);
                using (rwlockBMSFilesInitializedAll.GetWriterGuard())
                {
                    fileScanGeneration = libraryFileScanPipelineOwner.BeginFileScanRequest(
                        options,
                        bmsDirectories,
                        "reload_file_diff",
                        scannerLabel => ReportLibraryInitializationProgress(
                            LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            force: true),
                        directoryPreflightRequest);
                    libraryFileScanPipelineOwner.StartActiveNormalFolderMtimeSnapshot(fileScanGeneration);
                    Lr2FolderFileDiffPreparationResult scanPreparation = libraryFileScanPipelineOwner.ApplyActiveFileScan(
                        fileScanGeneration,
                        trackLibraryFileCheckProgress: true,
                        installDestinationCleanupSnapshot: installDestinationStateOwner.CreateCleanupSnapshot(),
                        postLeaseEffectObserver: action => postLeaseEffects.Add(action));
                    SongTableFileCheckResult result = scanPreparation?.FileCheckResult;
                    if (scanPreparation?.Request != null)
                    {
                        libraryFileScanPipelineOwner.ApplyPreparedLr2FolderFileDiffForFileMutation(
                            options,
                            "reload_file_diff",
                            scanPreparation,
                            mutationCapability,
                            ReportLr2FolderFileDiffProgress);
                        ScheduleLibraryFileDiffCompletionAfterLr2Apply();
                    }
                    stopwatch.Stop();
                    LogInstallPerformance("library_file_diff_reload done added=" + result.BmsAddedTargetCount
                        + " deleted=" + result.BmsDeletedTargetCount
                        + " bmsonUpserted=" + result.BmsonUpsertTargetCount
                        + " bmsonDeleted=" + result.BmsonDeletedTargetCount
                        + " dbCommitChunks=" + result.DbCommitChunks
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                    if (Net10PerformanceLog.IsEnabled)
                    {
                        Net10PerformanceLog.Write(
                            performanceInteraction,
                            "db_applied",
                            "added=" + result.BmsAddedTargetCount
                            + " deleted=" + result.BmsDeletedTargetCount
                            + " bmsonUpserted=" + result.BmsonUpsertTargetCount
                            + " commitChunks=" + result.DbCommitChunks
                            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            libraryFileScanPipelineOwner.AbortActiveFileScan(fileScanGeneration);
            ResetEverythingFallbackWarningQueue();
            stopwatch.Stop();
            LogInstallPerformance("library_file_diff_reload failed elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
            MarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(options, ex, "reload_file_diff");
            throw;
        }
        InvokePostLeaseNotificationsBestEffort(postLeaseEffects);
    }

    internal void ShowEverythingFallbackWarning(string fallbackReason)
    {
        string reason = string.IsNullOrWhiteSpace(fallbackReason) ? "unknown" : fallbackReason;
        ShowOperationDialog(
            string.Format(Resources.Warn_EverythingFallbackScanUsed, reason),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal bool QueueEverythingFallbackWarning(string fallbackReason)
    {
        long epoch;
        bool alreadyQueued;
        lock (everythingFallbackWarningGate)
        {
            epoch = everythingFallbackWarningEpoch;
            alreadyQueued = everythingFallbackWarningQueued != 0;
            if (!alreadyQueued)
            {
                everythingFallbackWarningQueued = 1;
            }
        }
        if (alreadyQueued)
        {
            LogEverythingScan("everything fallback warning skipped reason=already_queued fallbackReason=" + (fallbackReason ?? string.Empty));
            return false;
        }

        if (TryQueueEverythingFallbackWarningOnDispatcher(fallbackReason, epoch))
        {
            return true;
        }

        LogEverythingScan("everything fallback warning queued target=thread_pool fallbackReason=" + (fallbackReason ?? string.Empty));
        Task.Run(() => ShowEverythingFallbackWarningSafely(fallbackReason, epoch)).ObserveFault("EverythingFallbackWarningDialog");
        return true;
    }

    internal void ShowFileScanSkippedIncompleteWarning(string failureReason)
    {
        string reason = string.IsNullOrWhiteSpace(failureReason) ? "unknown" : failureReason;
        ShowOperationDialog(
            string.Format(Resources.Warn_FileScanSkippedIncomplete, reason),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowEmptyScanWithExistingDbWarning(string failureReason)
    {
        string reason = string.IsNullOrWhiteSpace(failureReason) ? "unknown" : failureReason;
        ShowOperationDialog(
            string.Format(Resources.Warn_EmptyScanWithExistingDbSkipped, reason),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal bool QueueFileScanSkippedIncompleteWarning(string failureReason)
    {
        if (Interlocked.Exchange(ref fileScanSkippedIncompleteWarningQueued, 1) != 0)
        {
            LogEverythingScan("file scan incomplete warning skipped reason=already_queued failureReason=" + (failureReason ?? string.Empty));
            return false;
        }

        if (TryQueueFileScanSkippedIncompleteWarningOnDispatcher(failureReason))
        {
            return true;
        }

        LogEverythingScan("file scan incomplete warning queued target=thread_pool failureReason=" + (failureReason ?? string.Empty));
        Task.Run(() => ShowFileScanSkippedIncompleteWarningSafely(failureReason)).ObserveFault("FileScanSkippedIncompleteWarningDialog");
        return true;
    }

    internal bool QueueEmptyScanWithExistingDbWarning(string failureReason)
    {
        if (Interlocked.Exchange(ref emptyScanWithExistingDbWarningQueued, 1) != 0)
        {
            LogEverythingScan("empty scan with existing db warning skipped reason=already_queued failureReason=" + (failureReason ?? string.Empty));
            return false;
        }

        if (TryQueueEmptyScanWithExistingDbWarningOnDispatcher(failureReason))
        {
            return true;
        }

        LogEverythingScan("empty scan with existing db warning queued target=thread_pool failureReason=" + (failureReason ?? string.Empty));
        Task.Run(() => ShowEmptyScanWithExistingDbWarningSafely(failureReason)).ObserveFault("EmptyScanWithExistingDbWarningDialog");
        return true;
    }

    private void ResetEverythingFallbackWarningQueue()
    {
        lock (everythingFallbackWarningGate)
        {
            everythingFallbackWarningEpoch++;
            everythingFallbackWarningQueued = 0;
        }
        Interlocked.Exchange(ref fileScanSkippedIncompleteWarningQueued, 0);
        Interlocked.Exchange(ref emptyScanWithExistingDbWarningQueued, 0);
    }

    private bool TryQueueEverythingFallbackWarningOnDispatcher(string fallbackReason, long epoch)
    {
        try
        {
            LogEverythingScan("everything fallback warning queued target=ui_dispatcher fallbackReason=" + (fallbackReason ?? string.Empty));
            IUiScheduledOperation operation = uiScheduler.Schedule(delegate
            {
                ShowEverythingFallbackWarningSafely(fallbackReason, epoch);
            });
            if (!operation.IsAccepted)
            {
                LogEverythingScan("everything fallback warning queue_failed target=ui_dispatcher fallbackReason=" + (fallbackReason ?? string.Empty));
                return false;
            }
        }
        catch (Exception ex)
        {
            LogEverythingScan("everything fallback warning queue_failed target=ui_dispatcher fallbackReason=" + (fallbackReason ?? string.Empty) + " message=" + ex.Message);
            return false;
        }
        return true;
    }

    private bool TryQueueFileScanSkippedIncompleteWarningOnDispatcher(string failureReason)
    {
        try
        {
            LogEverythingScan("file scan incomplete warning queued target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
            IUiScheduledOperation operation = uiScheduler.Schedule(delegate
            {
                ShowFileScanSkippedIncompleteWarningSafely(failureReason);
            });
            if (!operation.IsAccepted)
            {
                LogEverythingScan("file scan incomplete warning queue_failed target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
                return false;
            }
        }
        catch (Exception ex)
        {
            LogEverythingScan("file scan incomplete warning queue_failed target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty) + " message=" + ex.Message);
            return false;
        }
        return true;
    }

    private bool TryQueueEmptyScanWithExistingDbWarningOnDispatcher(string failureReason)
    {
        try
        {
            LogEverythingScan("empty scan with existing db warning queued target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
            IUiScheduledOperation operation = uiScheduler.Schedule(delegate
            {
                ShowEmptyScanWithExistingDbWarningSafely(failureReason);
            });
            if (!operation.IsAccepted)
            {
                LogEverythingScan("empty scan with existing db warning queue_failed target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
                return false;
            }
        }
        catch (Exception ex)
        {
            LogEverythingScan("empty scan with existing db warning queue_failed target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty) + " message=" + ex.Message);
            return false;
        }
        return true;
    }

    private void ShowEverythingFallbackWarningSafely(string fallbackReason, long epoch)
    {
        try
        {
            lock (everythingFallbackWarningGate)
            {
                if (epoch != everythingFallbackWarningEpoch)
                {
                    LogEverythingScan("everything fallback warning skipped reason=stale_epoch fallbackReason=" + (fallbackReason ?? string.Empty));
                    return;
                }
                ShowEverythingFallbackWarning(fallbackReason);
            }
            LogEverythingScan("everything fallback warning shown fallbackReason=" + (fallbackReason ?? string.Empty));
        }
        catch (Exception ex)
        {
            LogEverythingScan("everything fallback warning failed fallbackReason=" + (fallbackReason ?? string.Empty) + " message=" + ex.Message);
        }
    }

    private void ShowFileScanSkippedIncompleteWarningSafely(string failureReason)
    {
        try
        {
            ShowFileScanSkippedIncompleteWarning(failureReason);
            LogEverythingScan("file scan incomplete warning shown failureReason=" + (failureReason ?? string.Empty));
        }
        catch (Exception ex)
        {
            LogEverythingScan("file scan incomplete warning failed failureReason=" + (failureReason ?? string.Empty) + " message=" + ex.Message);
        }
    }

    private void ShowEmptyScanWithExistingDbWarningSafely(string failureReason)
    {
        try
        {
            ShowEmptyScanWithExistingDbWarning(failureReason);
            LogEverythingScan("empty scan with existing db warning shown failureReason=" + (failureReason ?? string.Empty));
        }
        catch (Exception ex)
        {
            LogEverythingScan("empty scan with existing db warning failed failureReason=" + (failureReason ?? string.Empty) + " message=" + ex.Message);
        }
    }
    private void RunChartDigestBackfill()
    {
        ChartDigestBackfillTotalCount = 0;
        ChartDigestBackfillProcessedCount = 0;
        ChartDigestBackfillCurrentPath = string.Empty;
        ChartDigestBackfillRunning = false;
        LogInstallPerformance("chart_digest_backfill skipped reason=combined_chart_info_pipeline");
    }

    internal Lr2SongDbSyncStatusSnapshot QueueLr2SongDbSync(
        string reason,
        bool force = false,
        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData = null,
        bool allowIncompleteToQueue = true,
        bool allowCommittedPathReceipt = false)
    {
        return Lr2SongDbSyncRequestCoordinator.Queue(
            lr2SynchronizationOwner,
            reason,
            force,
            prepareGeneratedData,
            allowIncompleteToQueue,
            allowCommittedPathReceipt);
    }

    internal bool TryRunLr2SongDbSyncDataPreparation(
        string reason,
        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        Action queueAfterPreparation = null)
    {
        if (prepareGeneratedData == null || CurrentOptionsSnapshot?.OperationModeLR2DB != true)
        {
            return false;
        }

        bool prepared = Lr2SongDbSyncRequestCoordinator.TryRunDataPreparation(
            lr2SynchronizationOwner,
            reason,
            prepareGeneratedData);
        if (!prepared)
        {
            return false;
        }

        queueAfterPreparation?.Invoke();
        return true;
    }

    internal void PublishLr2SongDbSyncExternalStageProgress(string stage, int processedCount, int totalCount, string detail = null)
    {
        Lr2SongDbSyncRequestCoordinator.PublishExternalStageProgress(lr2SynchronizationOwner, stage, processedCount, totalCount, detail);
    }

    private static Lr2SongDbSyncStatusSnapshot CreateRuntimeLr2SongDbSyncStatus(
        Lr2SongDbSyncStatusKind status,
        string signature,
        string stage,
        int? processedCursor,
        int? totalCount,
        string lastError,
        int? stageProcessedCount = null,
        int? stageTotalCount = null)
    {
        return new Lr2SongDbSyncStatusSnapshot
        {
            Status = status,
            StoredStatus = status,
            Signature = signature ?? string.Empty,
            Stage = stage ?? string.Empty,
            ProcessedCursor = processedCursor,
            TotalCount = totalCount,
            StageProcessedCount = stageProcessedCount,
            StageTotalCount = stageTotalCount,
            LastError = lastError ?? string.Empty,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private bool IsLr2SongDbSyncMutationBlocked()
    {
        return lr2SynchronizationOwner.Running;
    }

    private bool TryBlockLr2SongDbSyncMutation(string operation, bool showMessage = true)
    {
        return lr2SynchronizationOwner.TryBlockMutation(operation, showMessage);
    }

    private LibraryFileMutationLease TryBeginLr2SongDbSyncBlockedMutation(string operation, bool showMessage = true)
    {
        return lr2SynchronizationOwner.TryBeginMutation(operation, showMessage);
    }

    /// <summary>
    /// Reserves the library mutation lease for a playlist-owned file/DB command.
    /// The playlist receives the resulting lease and creates its explicit nested
    /// capability; no ambient or capability-free synchronization route exists.
    /// </summary>
    /// <param name="showMessage">Whether a busy mutation should retain the interactive warning.</param>
    internal LibraryFileMutationLease TryBeginLibraryFileMutation(string operation, bool showMessage = true)
    {
        return TryBeginLr2SongDbSyncBlockedMutation(operation, showMessage);
    }

    private void RunLr2SongDbSync(string reason, string signature, int requestVersion, bool allowCommittedPathReceipt)
    {
        Lr2SongDbSyncRequestCoordinator.Run(lr2SynchronizationOwner, reason, signature, requestVersion, allowCommittedPathReceipt);
    }

    private static bool ArePathSetsEqual(IEnumerable<string> first, IEnumerable<string> second)
    {
        return new HashSet<string>(
            (first ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal),
            StringComparer.OrdinalIgnoreCase)
            .SetEquals((second ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal));
    }

    private static bool AreLr2BuiltinCustomFolderConfigurationEqual(
        Lr2BuiltinCustomFolderSettings first,
        Lr2BuiltinCustomFolderSettings second)
    {
        return (first?.CustomFolderMask ?? 0) == (second?.CustomFolderMask ?? 0)
            && (first?.TitleFlashHours ?? 24) == (second?.TitleFlashHours ?? 24);
    }

    private static string SafeFullPathOrOriginal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        try
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }

    private sealed class Lr2NormalFolderCurrentBmsSnapshot(
        int ownedCollectionVersion,
        IReadOnlyList<string> currentBmsChartPaths)
    {
        internal int OwnedCollectionVersion { get; } = ownedCollectionVersion;

        internal IReadOnlyList<string> CurrentBmsChartPaths { get; } = currentBmsChartPaths ?? [];
    }

    private Lr2NormalFolderCurrentBmsSnapshot CreateLr2NormalFolderCurrentBmsSnapshotUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return new Lr2NormalFolderCurrentBmsSnapshot(
                OwnedChartCollectionVersion,
                catalogOwnedCollectionOwner.IsInitialized
                    ? catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefIndexSnapshot().GetCurrentBmsChartPaths()
                    : []);
        }
    }

    private Lr2NormalFolderCatalogMutationReceipt CreateLr2NormalFolderCatalogMutationReceipt(
        CatalogInstalledTargetUpsertReceipt receipt,
        int ownedCollectionVersion = 0)
    {
        if (receipt == null || CurrentOptionsSnapshot?.OperationModeLR2DB != true)
        {
            return null;
        }

        int expectedVersion = ownedCollectionVersion > 0
            ? ownedCollectionVersion
            : receipt?.OwnedCollectionVersion ?? 0;
        return new Lr2NormalFolderCatalogMutationReceipt(
            expectedVersion,
            receipt.AddedCharts?
                .Where(chart => chart?.Kind == ChartFileKind.Bms)
                .Select(chart => chart.Path),
            [],
            [],
            null);
    }

    private Lr2NormalFolderCatalogMutationReceipt CreateLr2NormalFolderCatalogMutationReceipt(
        CatalogMutationReceipt receipt,
        int ownedCollectionVersion = 0)
    {
        if (receipt == null || CurrentOptionsSnapshot?.OperationModeLR2DB != true)
        {
            return null;
        }

        int expectedVersion = ownedCollectionVersion > 0
            ? ownedCollectionVersion
            : receipt?.OwnedCollectionVersion ?? 0;
        bool requiresSnapshot = receipt.RemovedCharts?.Any(chart => chart?.Kind == ChartFileKind.Bms) == true
            || receipt.PathFacts?.Any(pathFact => pathFact?.Kind == ChartFileKind.Bms) == true;
        Lr2NormalFolderCurrentBmsSnapshot currentSnapshot = requiresSnapshot
            ? TryCreateLr2NormalFolderCurrentBmsSnapshot()
            : null;
        bool snapshotMatchesVersion = !requiresSnapshot
            || (currentSnapshot != null
                && (expectedVersion <= 0 || currentSnapshot.OwnedCollectionVersion == expectedVersion));
        return new Lr2NormalFolderCatalogMutationReceipt(
            expectedVersion > 0 ? expectedVersion : currentSnapshot?.OwnedCollectionVersion ?? 0,
            receipt.AddedCharts?
                .Where(chart => chart?.Kind == ChartFileKind.Bms)
                .Select(chart => chart.Path),
            receipt.RemovedCharts?
                .Where(chart => chart?.Kind == ChartFileKind.Bms)
                .Select(chart => chart.Path),
            receipt.PathFacts?
                .Where(pathFact => pathFact?.Kind == ChartFileKind.Bms)
                .Select(pathFact => new Lr2NormalFolderPathChange(pathFact.OldPath, pathFact.NewPath)),
            requiresSnapshot && snapshotMatchesVersion
                ? currentSnapshot?.CurrentBmsChartPaths
                : null);
    }

    private Lr2NormalFolderCurrentBmsSnapshot TryCreateLr2NormalFolderCurrentBmsSnapshot()
    {
        try
        {
            return CreateLr2NormalFolderCurrentBmsSnapshotUnsafe();
        }
        catch (Exception ex)
        {
            LogInstallPerformanceWarn("lr2_normal_folder_catalog_snapshot failed"
                + " exception=" + ex.GetType().Name
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            return null;
        }
    }

    private void MarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(
        BmsLibraryOptionsSnapshot options,
        Exception ex,
        string reason)
    {
        string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
        lr2SynchronizationOwner.MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            options,
            stage: "lr2_song_db_file_diff_write_failed",
            detail: "lr2_song_db_file_diff_write_failed: " + displayedMessage,
            logReason: string.IsNullOrWhiteSpace(reason) ? "file_diff" : reason);
    }

    private List<string> CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(
        IEnumerable<string> rootDirectories,
        BmsLibraryOptionsSnapshot options = null)
    {
        options ??= CurrentOptionsSnapshot;
        return Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            rootDirectories,
            CreateNormalCustomFolderOutputBaseDirectories(options),
            options.LR2CustomFolderOutputBaseDirRootType,
            CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options));
    }

    private static Lr2FolderFileCandidateSnapshot MergeLr2FolderFileCandidateSurface(
        Lr2FolderFileCandidateSnapshot baseCandidates,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        return Lr2FolderFileDiscoveryService.MergeCandidateSurface(baseCandidates, preparedSurface);
    }

    private static Lr2FolderFileCandidateSnapshot CreateIncompleteLr2FolderCandidateSnapshot()
    {
        return new Lr2FolderFileCandidateSnapshot(
            [],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);
    }

    private IReadOnlyList<string> CreateNormalCustomFolderOutputBaseDirectories(BmsLibraryOptionsSnapshot options = null)
    {
        options ??= CurrentOptionsSnapshot;
        return [.. new[] { options.LR2CustomFolderOutputBaseDir }
            .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> CreateLr2BuiltinCustomFolderPruneDirectories()
    {
        return Lr2FolderFileDiscoveryService.CreateBuiltinCustomFolderPruneDirectories();
    }

    private List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options = null)
    {
        options ??= CurrentOptionsSnapshot;
        return Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(options.LR2RootPath);
    }

    /// <summary>
    /// 既存 chart_info 行を起動後にメモリ上の譜面へ適用します。
    /// 一覧表示用メタデータであり、導入先推定の critical path からは外します。
    /// </summary>
    private void QueueDeferredChartInfoHydration(string reason, bool queueFullBackfillAfterHydration)
    {
        catalogChartInfoOwner.QueueDeferredHydration(reason, queueFullBackfillAfterHydration);
    }

    private void CompleteChartInfoHydrationForShutdown(string shutdownReason)
    {
        catalogChartInfoOwner.CompleteHydrationForShutdown(shutdownReason);
    }











    internal LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        return catalogChartInfoOwner.ResolveChartInfo(sha256, md5);
    }

    private bool ShouldLazyLoadChartInfoDisplayIndex()
    {
        return catalogChartInfoOwner.ShouldLazyLoadDisplayIndex();
    }

    private void EnsureChartInfoDisplayIndexLoadedForLazyResolve(string reason)
    {
        catalogChartInfoOwner.EnsureDisplayIndexLoadedForLazyResolve(reason);
    }

    private static bool IsCurrentChartInfoRow(LR2SongDBExtended.chart_info row)
    {
        return row != null && row.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    internal Func<BmtSongHashResolveRequest, Tuple<string, string>> CreateBeatorajaBmtSongHashResolver()
    {
        PlaylistLibraryResolveIndexSnapshot resolveIndex = GetPlaylistLibraryResolveIndexSnapshot(
            CancellationToken.None,
            out _,
            out _);
        var resolver = new BeatorajaBmtSongHashResolver(this, resolveIndex);
        return resolver.Resolve;
    }

    private sealed class BeatorajaBmtSongHashResolver(
        BMSLibrary library,
        PlaylistLibraryResolveIndexSnapshot resolveIndex)
    {
        public Tuple<string, string> Resolve(BmtSongHashResolveRequest request)
        {
            if (request == null)
            {
                return null;
            }
            string md5 = null;
            string sha256 = null;
            LibraryChartRef resolvedChart = resolveIndex?.ResolveChartForPlaylistHash(request.Md5, request.Sha256);
            if (IsResolvedChartCompatible(request, resolvedChart))
            {
                md5 = resolvedChart.Md5;
                sha256 = resolvedChart.Sha256;
            }
            LR2SongDBExtended.chart_info chartInfo = ResolveChartInfoForRequest(library, request);
            if (chartInfo != null)
            {
                if (string.IsNullOrWhiteSpace(md5) && TryGetChartInfoMd5(chartInfo, out string chartInfoMd5))
                {
                    md5 = chartInfoMd5;
                }
                if (string.IsNullOrWhiteSpace(sha256) && TryGetChartInfoSha256(chartInfo, out string chartInfoSha256))
                {
                    sha256 = chartInfoSha256;
                }
            }
            return string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256)
                ? null
                : Tuple.Create(md5, sha256);
        }

        private static LR2SongDBExtended.chart_info ResolveChartInfoForRequest(BMSLibrary library, BmtSongHashResolveRequest request)
        {
            if (library == null || request == null)
            {
                return null;
            }
            return string.IsNullOrWhiteSpace(request.Md5)
                ? library.ResolveChartInfo(request.Sha256, null)
                : library.ResolveChartInfo(null, request.Md5);
        }

        private static bool IsResolvedChartCompatible(BmtSongHashResolveRequest request, LibraryChartRef resolvedChart)
        {
            if (request == null || resolvedChart == null)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(request.Md5))
            {
                return string.Equals(request.Md5, resolvedChart.Md5, StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(request.Sha256)
                && string.Equals(request.Sha256, resolvedChart.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoForChart(ChartFile chart)
    {
        return chart == null ? null : ResolveChartInfo(chart.Sha256, chart.Md5);
    }

    private ChartInfoIndexUpdateResult ReplaceChartInfoIndex(IEnumerable<LR2SongDBExtended.chart_info> rows, bool hydrated)
    {
        return catalogChartInfoOwner.ReplaceIndex(rows, hydrated);
    }

    private static bool TryGetChartInfoSha256(LR2SongDBExtended.chart_info row, out string sha256)
    {
        sha256 = row?.sha256;
        if (string.IsNullOrWhiteSpace(sha256))
        {
            sha256 = null;
            return false;
        }
        sha256 = sha256.Trim();
        return true;
    }

    private static bool TryGetChartInfoMd5(LR2SongDBExtended.chart_info row, out string md5)
    {
        md5 = row?.md5;
        if (string.IsNullOrWhiteSpace(md5))
        {
            md5 = null;
            return false;
        }
        md5 = md5.Trim();
        return true;
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMapSnapshot()
    {
        return catalogChartInfoOwner.LoadChartInfoMap(dbGateway);
    }

    private Dictionary<string, LR2SongDBExtended.chart_info> CreateHydratedChartInfoIndexSha256Snapshot()
    {
        return catalogChartInfoOwner.CreateSha256Snapshot();
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosBySha256(IEnumerable<string> sha256s)
    {
        return catalogChartInfoOwner.LoadChartInfosBySha256(dbGateway, sha256s);
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosByMd5(IEnumerable<string> md5s)
    {
        return catalogChartInfoOwner.LoadChartInfosByMd5(dbGateway, md5s);
    }


    /// <summary>
    /// chart_info の不足分構築をバックグラウンドへ要求します。
    /// </summary>
    /// <param name="reason">ログに残す要求理由。</param>




    internal ChartInfoInlineBuildResult BuildAndPersistInlineChartInfoForInstalledCharts(
        string reason,
        IEnumerable<ChartFile> charts)
    {
        ChartInfoInlineBuildResult result = null;
        using (BeginOwnedDigestMutationWindow())
        {
            try
            {
                result = catalogChartInfoOwner.BuildInline(reason, charts);
                return result;
            }
            catch (Exception exception)
            {
                string displayedMessage = GetDisplayedExceptionMessage(exception).Replace(Environment.NewLine, " | ");
                lr2SynchronizationOwner.MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
                    CurrentOptionsSnapshot,
                    stage: "lr2_song_db_chart_info_inline_upsert_failed",
                    detail: "lr2_song_db_chart_info_inline_upsert_failed: " + displayedMessage,
                    logReason: reason ?? "chart_info_inline_install");
                LogInstallPerformanceWarn("lr2_song_db_chart_info_inline_upsert failed"
                    + " reason=" + (reason ?? "chart_info_inline_install")
                    + " exception=" + exception.GetType().Name
                    + " message=" + displayedMessage);
                throw;
            }
        }
    }

    /// <summary>
    /// 最新の chart_info 構築要求を処理します。
    /// 譜面削除時も chart_info は残すため、この worker は upsert のみ行います。
    /// </summary>

    private void QueueDeferredMaintenanceHydration(string reason)
    {
        catalogMaintenanceOwner.QueueHydration(reason);
    }

    private long PublishMaintenanceHydrationReceipt(CatalogMaintenanceHydrationReceipt receipt)
    {
        if (receipt == null)
        {
            return 0L;
        }
        ResourceHealthIndexMutation resourceHealthMutation = receipt.ResourceHealthMutation.ToMutation();
        var mutationResult = new OwnedChartCollectionMutationResult
        {
            WarningPresentationChanged = resourceHealthMutation.HasChanges,
            MaintenancePresentationChanged = receipt.ViewRefreshQueued
                || resourceHealthMutation.HasChanges
        };
        CopyResourceHealthIndexMutation(resourceHealthMutation, mutationResult.ResourceHealthMutation);
        DispatchOwnedChartCollectionMutation(mutationResult, "maintenance_hydration");
        return mutationResult.ResourceHealthDispatchResult?.IndexMs ?? 0L;
    }

    private void QueueDeferredInstallableMaintenance(string reason, long criticalElapsedMs, string dependency = null)
    {
        QueueInstallableMaintenanceWorker(reason, criticalElapsedMs, dependency);
    }

    private int CountInstallableMaintenanceSnapshotTargets()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return (BMSFiles?.Count ?? 0) + (BmsonSongs?.Count ?? 0);
        }
    }

    private void LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        if (!mutationResult.Changed)
        {
            return;
        }

        string sourceReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        LogInstallPerformance("reverse_lookup_incremental_update reason=" + sourceReason
            + " addedDirs=" + mutationResult.AddedDirectoryCount
            + " removedDirs=" + mutationResult.RemovedDirectoryCount
            + " replacedDirs=" + mutationResult.ReplacedDirectoryCount
            + " updatedHashes=" + mutationResult.UpdatedHashCount
            + " cancelledWarmup=" + mutationResult.CancelledWarmup
            + " fullMaintained=" + mutationResult.MaintainedFullReverseLookup
            + " requiresWarmup=" + mutationResult.RequiresDeferredWarmup);
    }

    /// <summary>
    /// deferred score hydration を要求します。
    /// BMSFile.bmsScore の全件反映は UI operable 後に後追いで実行します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    private void QueueDeferredScoreHydration(string reason)
    {
        if (TrySkipForShutdown("score_hydration_deferred", reason))
        {
            return;
        }
        int version;
        bool shouldStartWorker = false;
        bool markRunning = false;
        lock (lockDeferredScoreHydration)
        {
            deferredScoreHydrationRequestedVersion++;
            version = deferredScoreHydrationRequestedVersion;
            if (!deferredScoreHydrationRunning)
            {
                deferredScoreHydrationRunning = true;
                shouldStartWorker = true;
                markRunning = true;
            }
        }
        if (markRunning)
        {
            ScoreHydrationRunning = true;
        }
        ScoreHydrationRequestedVersion = version;
        LogInstallPerformance("score_hydration_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
        ReportStartupBackgroundTask("score_hydration_deferred", "queued", 0L, failed: false, detail: reason ?? string.Empty);
        if (shouldStartWorker)
        {
            ScheduleDeferredScoreHydrationWorker(reason);
        }
    }

    private void ScheduleDeferredScoreHydrationWorker(string reason)
    {
        if (StartupBackgroundTaskScheduler != null)
        {
            if (StartupBackgroundTaskScheduler(
                    "score_hydration_deferred",
                    reason ?? "queue",
                    null,
                    () =>
                    {
                        ProcessDeferredScoreHydrationRequests();
                        return Task.CompletedTask;
                    }))
            {
                return;
            }

            ProcessDeferredScoreHydrationRequests();
            return;
        }

        Task.Run(ProcessDeferredScoreHydrationRequests).ObserveFault("ProcessDeferredScoreHydrationRequests");
    }

    private void TryStartIrScorePrefetch(int lr2Id, BmsLibraryOptionsSnapshot options, string reason)
    {
        if (lr2Id == 0 || string.IsNullOrWhiteSpace(lr2ScoreDBPath) || options?.EnableDownloadLr2IrScoreAndDetectUnsent != true)
        {
            return;
        }
        int generation;
        string scoreDbPathSnapshot = lr2ScoreDBPath;
        lock (lockIrScorePrefetch)
        {
            if (IsShutdownRequested) return;
            if (irScorePrefetchTask != null
                && !irScorePrefetchTask.IsCompleted
                && irScorePrefetchLr2Id == lr2Id
                && string.Equals(irScorePrefetchScoreDbPath, scoreDbPathSnapshot, StringComparison.OrdinalIgnoreCase)
                && irScorePrefetchEnabled)
            {
                return;
            }
            generation = ++irScorePrefetchGeneration;
            irScorePrefetchLr2Id = lr2Id;
            irScorePrefetchScoreDbPath = scoreDbPathSnapshot;
            irScorePrefetchEnabled = true;
            LogInstallPerformance("ir_score_prefetch start generation=" + generation + " reason=" + (reason ?? "unknown") + " lr2Id=" + lr2Id);
            irScorePrefetchTask = Task.Run(delegate
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    IrScorePrefetchResult result = irService.PrefetchIrScoreTableWithMetrics(lr2Id, irClient, lr2IRScoreRegex, irScoreShutdownCancellation.Token);
                    stopwatch.Stop();
                    LogInstallPerformance("ir_score_prefetch done generation=" + generation
                        + " lr2Id=" + lr2Id
                        + " succeeded=" + result.Succeeded.ToString().ToLowerInvariant()
                        + " reason=" + (string.IsNullOrWhiteSpace(result.FailureReason) ? "ok" : result.FailureReason)
                        + " fetchMs=" + result.XmlFetchMs
                        + " parseMs=" + result.XmlParseMs
                        + " digestMs=" + result.DigestMs
                        + " parsedRows=" + result.ParsedRows
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    LogInstallPerformance("ir_score_prefetch failed generation=" + generation + " lr2Id=" + lr2Id + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                    return new IrScorePrefetchResult
                    {
                        Lr2Id = lr2Id,
                        Failure = IrScoreFailure.Unavailable,
                        FailureReason = "exception"
                    };
                }
            }).LoggingAndPropagate("IrScorePrefetch");
        }
    }

    private IrScorePrefetchResult TryConsumeIrScorePrefetch(int requestVersion, BmsLibraryOptionsSnapshot options, out long waitMs, out string status)
    {
        waitMs = 0L;
        status = "not_started";
        if (options?.EnableDownloadLr2IrScoreAndDetectUnsent != true)
        {
            status = "disabled";
            return null;
        }
        Task<IrScorePrefetchResult> task;
        int generation;
        int lr2IdSnapshot;
        string scoreDbPathSnapshot;
        lock (lockIrScorePrefetch)
        {
            task = irScorePrefetchTask;
            generation = irScorePrefetchGeneration;
            lr2IdSnapshot = irScorePrefetchLr2Id;
            scoreDbPathSnapshot = irScorePrefetchScoreDbPath;
        }
        if (task == null)
        {
            return null;
        }
        if (lr2IdSnapshot != LR2ID || !string.Equals(scoreDbPathSnapshot, lr2ScoreDBPath, StringComparison.OrdinalIgnoreCase))
        {
            status = "stale";
            LogInstallPerformance("ir_score_prefetch consume generation=" + generation + " status=stale requestVersion=" + requestVersion + " prefetchedLr2Id=" + lr2IdSnapshot + " currentLr2Id=" + LR2ID);
            return null;
        }
        var waitStopwatch = Stopwatch.StartNew();
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch
        {
            waitStopwatch.Stop();
            waitMs = waitStopwatch.ElapsedMilliseconds;
            status = "failed";
            LogInstallPerformance("ir_score_prefetch consume generation=" + generation + " status=failed requestVersion=" + requestVersion + " waitMs=" + waitMs);
            return new IrScorePrefetchResult { Lr2Id = lr2IdSnapshot, Failure = IrScoreFailure.Unavailable, FailureReason = "exception" };
        }
        waitStopwatch.Stop();
        waitMs = waitStopwatch.ElapsedMilliseconds;
        IrScorePrefetchResult result = task.Result;
        if (result == null || !result.Succeeded || result.Lr2Id != LR2ID)
        {
            status = "unavailable";
            LogInstallPerformance("ir_score_prefetch consume generation=" + generation
                + " status=unavailable requestVersion=" + requestVersion
                + " waitMs=" + waitMs
                + " reason=" + (result?.FailureReason ?? "null")
                + " prefetchedLr2Id=" + (result?.Lr2Id ?? 0)
                + " currentLr2Id=" + LR2ID);
            return result ?? new IrScorePrefetchResult { Lr2Id = lr2IdSnapshot, Failure = IrScoreFailure.Unavailable, FailureReason = "unavailable" };
        }
        status = "used";
        LogInstallPerformance("ir_score_prefetch consume generation=" + generation
            + " status=used requestVersion=" + requestVersion
            + " waitMs=" + waitMs
            + " fetchMs=" + result.XmlFetchMs
            + " parseMs=" + result.XmlParseMs
            + " digestMs=" + result.DigestMs
            + " parsedRows=" + result.ParsedRows);
        return result;
    }

    /// <summary>
    /// deferred ranking refresh を要求します。
    /// score hydration 完了後に worker が実行されます。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    private void QueueDeferredRankingRefresh(string reason)
    {
        if (TrySkipForShutdown("ranking_refresh_deferred", reason))
        {
            return;
        }
        int version;
        bool shouldStartWorker = false;
        bool markRunning = false;
        lock (lockDeferredRankingRefresh)
        {
            deferredRankingRefreshRequestedVersion++;
            version = deferredRankingRefreshRequestedVersion;
            if (!deferredRankingRefreshRunning && !ScoreHydrationRunning)
            {
                deferredRankingRefreshRunning = true;
                shouldStartWorker = true;
                markRunning = true;
            }
        }
        if (markRunning)
        {
            RankingRefreshRunning = true;
        }
        RankingRefreshRequestedVersion = version;
        LogInstallPerformance("ranking_refresh_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
        ReportStartupBackgroundTask("ranking_refresh_deferred", "queued", 0L, failed: false, detail: reason ?? string.Empty);
        if (shouldStartWorker)
        {
            Task.Run(ProcessDeferredRankingRefreshRequests).ObserveFault("ProcessDeferredRankingRefreshRequests");
        }
    }

    /// <summary>
    /// 必要であれば deferred ranking refresh worker を開始します。
    /// </summary>
    private void TryStartDeferredRankingRefreshWorker()
    {
        if (TrySkipForShutdown("ranking_refresh_deferred_start", "score_hydration_done"))
        {
            return;
        }
        bool shouldStartWorker = false;
        int version = 0;
        bool markRunning = false;
        lock (lockDeferredRankingRefresh)
        {
            if (!deferredRankingRefreshRunning && deferredRankingRefreshRequestedVersion > deferredRankingRefreshLastCompletedVersion)
            {
                deferredRankingRefreshRunning = true;
                shouldStartWorker = true;
                version = deferredRankingRefreshRequestedVersion;
                markRunning = true;
            }
        }
        if (markRunning)
        {
            RankingRefreshRunning = true;
        }
        if (shouldStartWorker)
        {
            LogInstallPerformance("ranking_refresh_deferred start version=" + version);
            Task.Run(ProcessDeferredRankingRefreshRequests).ObserveFault("ProcessDeferredRankingRefreshRequests");
        }
    }

    /// <summary>
    /// deferred score hydration worker です。
    /// 最新要求だけを最後まで処理し、中間要求は chunk 境界で打ち切ります。
    /// </summary>
    private void ProcessDeferredScoreHydrationRequests()
    {
        while (true)
        {
            int requestVersion;
            lock (lockDeferredScoreHydration)
            {
                requestVersion = deferredScoreHydrationRequestedVersion;
            }
            var stopwatch = Stopwatch.StartNew();
            if (IsShutdownRequested)
            {
                deferredScoreHydrationLastCompletedVersion = requestVersion;
                deferredScoreHydrationRunning = false;
                ScoreHydrationCompletedVersion = requestVersion;
                ScoreHydrationRunning = false;
                LogInstallPerformance("score_hydration_deferred skipped version=" + requestVersion + " reason=shutdown_requested");
                ReportStartupBackgroundTask("score_hydration_deferred", "skipped", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_requested");
                TryStartDeferredRankingRefreshWorker();
                return;
            }
            ReportStartupBackgroundTask("score_hydration_deferred", "start", 0L, failed: false, detail: "version=" + requestVersion);
            try
            {
                LogInstallPerformance("score_hydration_deferred run version=" + requestVersion);
                RunDeferredScoreHydration(requestVersion);
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred done version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                ReportStartupBackgroundTask("score_hydration_deferred", "done", stopwatch.ElapsedMilliseconds, failed: false);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred cancelled version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                ReportStartupBackgroundTask("score_hydration_deferred", "cancelled", stopwatch.ElapsedMilliseconds, failed: false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                ReportStartupBackgroundTask("score_hydration_deferred", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
            }
            bool shouldStop = false;
            bool markRunningFalse = false;
            lock (lockDeferredScoreHydration)
            {
                deferredScoreHydrationLastCompletedVersion = requestVersion;
                if (requestVersion == deferredScoreHydrationRequestedVersion)
                {
                    deferredScoreHydrationRunning = false;
                    shouldStop = true;
                    markRunningFalse = true;
                }
            }
            ScoreHydrationCompletedVersion = requestVersion;
            if (markRunningFalse)
            {
                ScoreHydrationRunning = false;
            }
            if (shouldStop)
            {
                TryStartDeferredRankingRefreshWorker();
                return;
            }
        }
    }

    /// <summary>
    /// deferred ranking refresh worker です。
    /// score hydration 完了後に最新要求だけを処理します。
    /// </summary>
    private void ProcessDeferredRankingRefreshRequests()
    {
        while (true)
        {
            int requestVersion;
            lock (lockDeferredRankingRefresh)
            {
                requestVersion = deferredRankingRefreshRequestedVersion;
            }
            var stopwatch = Stopwatch.StartNew();
            if (IsShutdownRequested)
            {
                deferredRankingRefreshLastCompletedVersion = requestVersion;
                deferredRankingRefreshRunning = false;
                RankingRefreshCompletedVersion = requestVersion;
                RankingRefreshRunning = false;
                LogInstallPerformance("ranking_refresh_deferred skipped version=" + requestVersion + " reason=shutdown_requested");
                ReportStartupBackgroundTask("ranking_refresh_deferred", "skipped", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_requested");
                return;
            }
            ReportStartupBackgroundTask("ranking_refresh_deferred", "start", 0L, failed: false, detail: "version=" + requestVersion);
            try
            {
                LogInstallPerformance("ranking_refresh_deferred run version=" + requestVersion);
                RankingRefreshRunResult result = RunDeferredRankingRefresh(requestVersion);
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred done version=" + requestVersion
                    + " irScoreMs=" + result.IrScoreMs
                    + " irScoreSkipped=" + result.IrScoreSkipped
                    + " irScoreSkipReason=" + result.IrScoreSkipReason
                    + " irScoreXmlFetchMs=" + result.IrScoreXmlFetchMs
                    + " irScoreXmlParseMs=" + result.IrScoreXmlParseMs
                    + " irScoreDigestMs=" + result.IrScoreDigestMs
                    + " irScorePrefetchUsed=" + result.IrScorePrefetchUsed
                    + " irScorePrefetchStatus=" + result.IrScorePrefetchStatus
                    + " irScorePrefetchWaitMs=" + result.IrScorePrefetchWaitMs
                    + " irScorePrefetchFetchMs=" + result.IrScorePrefetchFetchMs
                    + " irScorePrefetchParseMs=" + result.IrScorePrefetchParseMs
                    + " irScorePrefetchDigestMs=" + result.IrScorePrefetchDigestMs
                    + " irScoreDbLoadMs=" + result.IrScoreDbLoadMs
                    + " irScoreDbLockWaitMs=" + result.IrScoreDbLockWaitMs
                    + " irScoreDbReplaceMs=" + result.IrScoreDbReplaceMs
                    + " irScoreMergeMs=" + result.IrScoreMergeMs
                    + " irScoreParsedRows=" + result.IrScoreParsedRows
                    + " irScoreLoadedRows=" + result.IrScoreLoadedRows
                    + " irScoreMetadataUpdated=" + result.IrScoreMetadataUpdated
                    + " cacheMs=" + result.CacheMs
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                ReportStartupBackgroundTask("ranking_refresh_deferred", result.IrScoreFailed ? "unavailable" : "done", stopwatch.ElapsedMilliseconds, failed: result.IrScoreFailed, detail: result.IrScoreFailed ? result.IrScoreSkipReason : null);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred cancelled version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                ReportStartupBackgroundTask("ranking_refresh_deferred", "cancelled", stopwatch.ElapsedMilliseconds, failed: false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                ReportStartupBackgroundTask("ranking_refresh_deferred", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
            }
            bool shouldStop = false;
            bool markRunningFalse = false;
            lock (lockDeferredRankingRefresh)
            {
                deferredRankingRefreshLastCompletedVersion = requestVersion;
                if (requestVersion == deferredRankingRefreshRequestedVersion)
                {
                    deferredRankingRefreshRunning = false;
                    shouldStop = true;
                    markRunningFalse = true;
                }
            }
            RankingRefreshCompletedVersion = requestVersion;
            if (markRunningFalse)
            {
                RankingRefreshRunning = false;
            }
            if (shouldStop)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 現在の score snapshot を使って全 BMSFiles の bmsScore を chunk 単位で反映します。
    /// </summary>
    /// <param name="requestVersion">処理対象の要求版数。</param>
    private void RunDeferredScoreHydration(int requestVersion)
    {
        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
        if (snapshot == null)
        {
            return;
        }
        List<BMSFile> bmsFilesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
        }
        for (int offset = 0; offset < bmsFilesSnapshot.Count; offset += deferredScoreHydrationChunkSize)
        {
            if (IsDeferredScoreHydrationRequestSuperseded(requestVersion))
            {
                throw new OperationCanceledException();
            }
            int count = Math.Min(deferredScoreHydrationChunkSize, bmsFilesSnapshot.Count - offset);
            List<BMSFile> chunk = bmsFilesSnapshot.GetRange(offset, count);
            var chunkStopwatch = Stopwatch.StartNew();
            int matchedScoreCount = ApplyScoreSnapshotToFilesReplacingExisting(chunk, snapshot);
            chunkStopwatch.Stop();
            if (chunkStopwatch.ElapsedMilliseconds >= deferredScoreHydrationChunkSlowLogThresholdMs)
            {
                LogInstallPerformance("score_hydration_chunk version=" + requestVersion + " offset=" + offset + " count=" + count + " total=" + bmsFilesSnapshot.Count + " matchedScoreCount=" + matchedScoreCount + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds + " thresholdMs=" + deferredScoreHydrationChunkSlowLogThresholdMs);
            }
            Thread.Yield();
        }
    }

    /// <summary>
    /// 現在の deferred score hydration 要求が新しい要求で上書きされたかどうかを返します。
    /// </summary>
    /// <param name="requestVersion">確認対象の版数。</param>
    /// <returns>新しい要求が存在する場合は <see langword="true"/>。</returns>
    private bool IsDeferredScoreHydrationRequestSuperseded(int requestVersion)
    {
        if (IsShutdownRequested)
        {
            return true;
        }
        lock (lockDeferredScoreHydration)
        {
            return requestVersion != deferredScoreHydrationRequestedVersion;
        }
    }

    /// <summary>
    /// 最新の ranking refresh 要求を実行し、score snapshot を更新します。
    /// </summary>
    private sealed class RankingRefreshRunResult
    {
        public long IrScoreMs { get; set; }

        public long IrScoreXmlFetchMs { get; set; }

        public long IrScoreXmlParseMs { get; set; }

        public long IrScoreDigestMs { get; set; }

        public bool IrScorePrefetchUsed { get; set; }

        public long IrScorePrefetchWaitMs { get; set; }

        public long IrScorePrefetchFetchMs { get; set; }

        public long IrScorePrefetchParseMs { get; set; }

        public long IrScorePrefetchDigestMs { get; set; }

        public string IrScorePrefetchStatus { get; set; } = "not_started";

        public long IrScoreDbLoadMs { get; set; }

        public long IrScoreDbLockWaitMs { get; set; }

        public long IrScoreDbReplaceMs { get; set; }

        public long IrScoreMergeMs { get; set; }

        public int IrScoreParsedRows { get; set; }

        public int IrScoreLoadedRows { get; set; }

        public bool IrScoreSkipped { get; set; }

        public bool IrScoreFailed { get; set; }

        public string IrScoreSkipReason { get; set; } = "unavailable";

        public bool IrScoreMetadataUpdated { get; set; }

        public long CacheMs { get; set; }
    }

    private RankingRefreshRunResult RunDeferredRankingRefresh(int requestVersion)
    {
        var result = new RankingRefreshRunResult();
        if (activeScoreSource != ActiveScoreSource.Lr2 || lr2ScoreDBPath == null || LR2ID == 0)
        {
            return result;
        }
        RankingDownloadContext rankingContext = CaptureRankingDownloadContext();
        var irScoreStopwatch = Stopwatch.StartNew();
        BmsLibraryOptionsSnapshot optionsSnapshot = CurrentOptionsSnapshot;
        StartupRankingRefreshWorkPlan workPlan = StartupRankingRefreshPolicy.CreateWorkPlan(optionsSnapshot);
        if (workPlan.RefreshIrScore)
        {
            IrScorePrefetchResult prefetchedScore = TryConsumeIrScorePrefetch(requestVersion, optionsSnapshot, out long prefetchWaitMs, out string prefetchStatus);
            irScoreShutdownCancellation.Token.ThrowIfCancellationRequested();
            IrScoreTableUpdateResult irScoreUpdateResult =
                updateLR2IRScoreTableWithMetrics(
                    prefetchedScore,
                    rankingContext.Lr2Id);
            List<LR2IRScore> scoreTable = irScoreUpdateResult.ScoreTable;
            result.IrScoreXmlFetchMs = irScoreUpdateResult.XmlFetchMs;
            result.IrScoreXmlParseMs = irScoreUpdateResult.XmlParseMs;
            result.IrScoreDigestMs = irScoreUpdateResult.DigestMs;
            result.IrScorePrefetchUsed = irScoreUpdateResult.PrefetchUsed;
            result.IrScorePrefetchStatus = prefetchStatus;
            result.IrScorePrefetchWaitMs = prefetchWaitMs;
            result.IrScorePrefetchFetchMs = irScoreUpdateResult.PrefetchXmlFetchMs;
            result.IrScorePrefetchParseMs = irScoreUpdateResult.PrefetchXmlParseMs;
            result.IrScorePrefetchDigestMs = irScoreUpdateResult.PrefetchDigestMs;
            result.IrScoreDbLoadMs = irScoreUpdateResult.DbLoadMs;
            result.IrScoreDbLockWaitMs = irScoreUpdateResult.DbLockWaitMs;
            result.IrScoreDbReplaceMs = irScoreUpdateResult.DbReplaceMs;
            result.IrScoreParsedRows = irScoreUpdateResult.ParsedRows;
            result.IrScoreLoadedRows = irScoreUpdateResult.LoadedRows;
            result.IrScoreSkipped = irScoreUpdateResult.Skipped;
            result.IrScoreSkipReason = irScoreUpdateResult.SkipReason ?? "unavailable";
            result.IrScoreMetadataUpdated = irScoreUpdateResult.MetadataUpdated;
            result.IrScoreFailed = !irScoreUpdateResult.Succeeded;
            irScoreShutdownCancellation.Token.ThrowIfCancellationRequested();
            if (IsDeferredRankingRefreshRequestSuperseded(requestVersion))
            {
                throw new OperationCanceledException();
            }
            var mergeStopwatch = Stopwatch.StartNew();
            updateBMSScores(
                scoreTable,
                detectUnsentScores: true,
                rankingContext);
            mergeStopwatch.Stop();
            result.IrScoreMergeMs = mergeStopwatch.ElapsedMilliseconds;
        }
        else
        {
            result.IrScoreSkipped = true;
            result.IrScoreSkipReason = "disabled";
        }
        irScoreStopwatch.Stop();
        result.IrScoreMs = irScoreStopwatch.ElapsedMilliseconds;
        if (IsDeferredRankingRefreshRequestSuperseded(requestVersion))
        {
            throw new OperationCanceledException();
        }
        if (workPlan.RefreshRankingCache)
        {
            var cacheStopwatch = Stopwatch.StartNew();
            setRankingScore(rankingContext);
            cacheStopwatch.Stop();
            result.CacheMs = cacheStopwatch.ElapsedMilliseconds;
        }
        else
        {
            LogInstallPerformance("ranking_cache_refresh skipped reason=disabled");
        }
        return result;
    }

    /// <summary>
    /// 現在の deferred ranking refresh 要求が新しい要求で上書きされたかどうかを返します。
    /// </summary>
    /// <param name="requestVersion">確認対象の版数。</param>
    /// <returns>新しい要求が存在する場合は <see langword="true"/>。</returns>
    private bool IsDeferredRankingRefreshRequestSuperseded(int requestVersion)
    {
        if (IsShutdownRequested)
        {
            return true;
        }
        lock (lockDeferredRankingRefresh)
        {
            return requestVersion != deferredRankingRefreshRequestedVersion;
        }
    }

    private List<string> getBMSDirectories()
    {
        return getBMSDirectories(out _);
    }

    private LibraryDirectoryPreflightRequest CaptureDirectoryPreflightRequest(
        BmsLibraryOptionsSnapshot options)
    {
        Lr2SearchRootSnapshot snapshot = lr2SearchRootSnapshotOwner.CaptureForUpdate(options);
        return directoryPreflightService.CreateRequest(
            snapshot.RequestedRoots,
            snapshot.Roots,
            options);
    }

    private static BmsSearchRootNormalizationSnapshot CreateUpdateRootNormalizationSnapshot(
        LibraryDirectoryPreflightRequest request,
        BmsLibraryOptionsSnapshot options)
    {
        HashSet<string> configuredOutputBases = new(StringComparer.OrdinalIgnoreCase);
        if (options?.OperationModeLR2DB == true)
        {
            if (!string.IsNullOrWhiteSpace(options.LR2CustomFolderOutputBaseDir))
            {
                configuredOutputBases.Add(options.LR2CustomFolderOutputBaseDir);
            }
            foreach (string path in options.LR2CustomFolderAdditionalOutputBaseDirs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    configuredOutputBases.Add(path);
                }
            }
            if (!string.IsNullOrWhiteSpace(options.LR2CustomFolderOutputBaseDirRootType))
            {
                configuredOutputBases.Add(options.LR2CustomFolderOutputBaseDirRootType);
            }
        }
        return new BmsSearchRootNormalizationSnapshot
        {
            RequestedRootCount = request?.BmsRootDirectories.Count ?? 0,
            ExistingRootCount = request?.BmsRootDirectories.Count ?? 0,
            ExcludedCustomOutputRootCount = Math.Max(
                0,
                (request?.BmsRootDirectories.Count ?? 0) - (request?.ScanRootDirectories.Count ?? 0)),
            RootCount = request?.ScanRootDirectories.Count ?? 0,
            ConfiguredCustomOutputRootCount = configuredOutputBases.Count
        };
    }

    private List<string> getBMSDirectories(out BmsSearchRootNormalizationSnapshot normalizationSnapshot)
    {
        Lr2SearchRootSnapshot snapshot = lr2SynchronizationOwner.CaptureBmsDirectories();
        normalizationSnapshot = new BmsSearchRootNormalizationSnapshot
        {
            RequestedRootCount = snapshot.RequestedRootCount,
            ExistingRootCount = snapshot.ExistingRootCount,
            ExcludedCustomOutputRootCount = snapshot.ExcludedCustomOutputRootCount,
            RootCount = snapshot.Roots.Count,
            ConfiguredCustomOutputRootCount = snapshot.ConfiguredCustomOutputRootCount
        };
        return [.. snapshot.Roots];
    }

    private void LogBmsSearchRootNormalization(
        string reason,
        BmsLibraryOptionsSnapshot options,
        BmsSearchRootNormalizationSnapshot normalization,
        IReadOnlyCollection<string> roots)
    {
        normalization ??= new BmsSearchRootNormalizationSnapshot
        {
            RootCount = roots?.Count ?? 0
        };
        int lr2FolderDiscoveryRootCount = options?.OperationModeLR2DB == true
                ? CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots ?? [], options).Count
                : 0;
        LogInstallPerformance("bms_search_root_normalization"
            + " reason=" + (reason ?? "unknown")
            + " requestedRoots=" + normalization.RequestedRootCount
            + " existingRoots=" + normalization.ExistingRootCount
            + " chartResourceRoots=" + normalization.RootCount
            + " configuredCustomOutputRoots=" + normalization.ConfiguredCustomOutputRootCount
            + " excludedCustomOutputRoots=" + normalization.ExcludedCustomOutputRootCount
            + " lr2FolderDiscoveryRoots=" + lr2FolderDiscoveryRootCount);
    }

    private sealed class BmsSearchRootNormalizationSnapshot
    {
        public int RequestedRootCount { get; set; }

        public int ExistingRootCount { get; set; }

        public int RootCount { get; set; }

        public int ConfiguredCustomOutputRootCount { get; set; }

        public int ExcludedCustomOutputRootCount { get; set; }
    }

    private bool IsPendingPackageContainingOnlyInstalledCharts(ChartPackage package)
    {
        if (package == null)
        {
            return false;
        }
        List<PackageChartEntry> entries = package.ChartEntries ?? [];
        if (entries.Count == 0)
        {
            return false;
        }
        return entries.All(entry => ContainsInstalledChartUnsafe(entry?.Chart));
    }

    private void InvalidatePlaylistLibraryResolveIndexSnapshot(int ownedCollectionVersion = 0)
    {
        int resolvedOwnedCollectionVersion = ownedCollectionVersion > 0 ? ownedCollectionVersion : OwnedChartCollectionVersion;
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            playlistLibraryResolveIndexSnapshot = null;
            playlistLibraryResolveIndexInvalidationVersion++;
            playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = resolvedOwnedCollectionVersion;
        }
    }

    private IDisposable BeginOwnedDigestMutationWindow()
    {
        lock (pendingInstallEstimateCurrentnessGate)
        {
            catalogOwnedCollectionOwner.BeginDigestMutationWindow();
            ownedDigestMutationGeneration++;
        }
        try
        {
            return new OwnedDigestMutationWindowScope(this, resourceHealthOwner.BeginInputMutation());
        }
        catch
        {
            lock (pendingInstallEstimateCurrentnessGate)
            {
                catalogOwnedCollectionOwner.EndDigestMutationWindow();
                ownedDigestMutationGeneration++;
            }
            throw;
        }
    }

    private void EndOwnedDigestMutationWindow(ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation)
    {
        try
        {
            resourceHealthMutation.Dispose();
            resourceHealthOwner.RebaseAfterInputMutation(
                resourceHealthMutation,
                GetCurrentResourceHealthIndexVersion());
        }
        finally
        {
            lock (pendingInstallEstimateCurrentnessGate)
            {
                catalogOwnedCollectionOwner.EndDigestMutationWindow();
                ownedDigestMutationGeneration++;
            }
            InvalidatePlaylistLibraryResolveIndexSnapshot();
        }
    }

    private bool IsOwnedDigestMutationWindowActive()
    {
        return catalogOwnedCollectionOwner.IsDigestMutationWindowActive();
    }

    private void WaitForOwnedDigestMutationWindowIdle(CancellationToken cancellationToken = default)
    {
        catalogOwnedCollectionOwner.WaitForDigestMutationWindowIdle(cancellationToken);
    }

    private sealed class OwnedDigestMutationWindowScope(
        BMSLibrary owner,
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation) : IDisposable
    {
        private BMSLibrary owner = owner;

        private ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthMutation;

        public void Dispose()
        {
            BMSLibrary currentOwner = Interlocked.Exchange(ref owner, null);
            ResourceHealthIndexOwner.ResourceHealthInputMutation currentResourceHealthMutation = Interlocked.Exchange(ref resourceHealthMutation, null);
            if (currentOwner != null && currentResourceHealthMutation != null)
            {
                currentOwner.EndOwnedDigestMutationWindow(currentResourceHealthMutation);
            }
        }
    }

    private void InvalidateOwnedChartCollection()
    {
        catalogOwnedCollectionOwner.Invalidate();
    }

    private int NotifyOwnedChartCollectionChanged(int committedVersion = 0)
    {
        int version = committedVersion > 0
            ? committedVersion
            : catalogOwnedCollectionOwner.IncrementVersion();
        RaisePropertyChanged(() => OwnedChartCollectionVersion);
        return version;
    }

    /// <summary>
    /// インストール済み lookup index をクリアし、次回使用時に再構築されるようにマークします。
    /// </summary>
    private void InvalidateInstalledDirectoryIndex()
    {
        InvalidateInstallEstimationMetadataProfileCache();
        lock (pendingInstallEstimateCurrentnessGate)
        {
            lock (lockInstalledPrimaryHashLookup)
            {
                installedPrimaryHashLookup = new PrimaryHashLookupState();
                installedPrimaryHashLookupInitialized = false;
            }
            lock (lockInstalledChartLookupIndex)
            {
                installedChartLookupIndex = new InstalledChartLookupIndexState();
                installedChartLookupIndexInitialized = false;
                installedChartLookupGeneration++;
            }
        }
    }

    private void InvalidateInstallEstimationMetadataProfileCache()
    {
        lock (lockInstallEstimationMetadataProfileCache)
        {
            installEstimationMetadataProfileCache.Clear();
        }
    }

    private sealed class InstalledChartLookupMutation
    {
        public List<InstalledChartLookupMutationEntry> Removed { get; } = [];

        public List<InstalledChartLookupMutationEntry> Added { get; } = [];

        public List<InstalledChartLookupPathMutationEntry> Moved { get; } = [];

        public bool RequiresFullInvalidate { get; set; }

        public bool HasChanges => RequiresFullInvalidate || Removed.Count > 0 || Added.Count > 0 || Moved.Count > 0;
    }

    private sealed class OwnedChartCollectionMutationResult
    {
        public OwnedChartCollectionMutationResult()
            : this(new ResourceHealthIndexMutation())
        {
        }

        public OwnedChartCollectionMutationResult(ResourceHealthIndexMutation resourceHealthMutation)
        {
            ResourceHealthMutation = resourceHealthMutation ?? throw new ArgumentNullException(nameof(resourceHealthMutation));
        }

        public OwnedChartCollectionStorageMutation StorageMutation { get; } = new();

        public List<LibraryChartDigestChange> DigestChanges { get; } = [];

        public CatalogDigestMutationRequest DigestMutationRequest { get; set; }

        public bool DigestMutationApplied { get; set; }

        public InstalledChartLookupMutation InstalledLookupMutation { get; set; } = new();

        public bool InstalledLookupMutationApplied { get; set; }

        public bool InstalledHashIndexInvalidated { get; set; }

        public bool PlaylistResolveIndexInvalidated { get; set; }

        public bool InstallMetadataCacheInvalidated { get; set; }

        public InstallDestinationRuntimeStateMutation InstallDestinationRuntimeStateMutation { get; } = new();

        public bool InstallDestinationRuntimeStateApplied { get; set; }

        public IReadOnlyList<ChartFile> InstallDestinationChangedCharts => InstallDestinationRuntimeStateMutation.AppliedCharts;

        public bool InstallEstimationMetadataProfileCacheInvalidated { get; set; }

        public int AddedCount { get; set; }

        public int RemovedCount { get; set; }

        public int MovedCount { get; set; }

        public int DigestChangedCount => DigestChanges.Count;

        public int InstallDestinationChangedCount { get; set; }

        public int InstalledPackagePathChangedCount { get; set; }

        public bool ParentFolderInvalidated { get; set; }

        public bool DuplicateCacheInvalidated { get; set; }

        public bool OwnedCollectionChanged { get; set; }

        public bool OwnedCollectionChangeNotified { get; set; }

        public bool OwnedCollectionVersionAlreadyAdvanced { get; set; }

        public int OwnedCollectionVersion { get; set; }

        public int NormalLibraryRefreshNotificationVersion { get; set; }

        public ResourceHealthIndexMutation ResourceHealthMutation { get; }

        public ResourceHealthIndexDispatchResult ResourceHealthDispatchResult { get; set; }

        public bool ResourceHealthIndexInvalidated
        {
            get => ResourceHealthMutation.Invalidate;
            set => ResourceHealthMutation.Invalidate = value;
        }

        public bool WarningPresentationChanged { get; set; }

        public bool MaintenancePresentationChanged { get; set; }

        public bool BmsFilesStorageRowsChanged { get; set; }

        public bool BmsonSongsStorageRowsChanged { get; set; }

        public bool StorageRowsChanged => BmsFilesStorageRowsChanged || BmsonSongsStorageRowsChanged;

        public bool StorageRowsRemoveDeltaComplete { get; set; }

        public bool ShouldDispatchInstalledLookup => InstalledLookupMutation?.HasChanges == true;

        public bool HasLoggableChanges => AddedCount > 0
            || RemovedCount > 0
            || MovedCount > 0
            || DigestChangedCount > 0
            || InstallDestinationChangedCount > 0
            || InstalledPackagePathChangedCount > 0
            || ParentFolderInvalidated
            || DuplicateCacheInvalidated
            || OwnedCollectionChanged
            || ResourceHealthMutation.HasChanges
            || InstallEstimationMetadataProfileCacheInvalidated
            || WarningPresentationChanged
            || MaintenancePresentationChanged
            || StorageRowsChanged
            || InstalledLookupMutation?.HasChanges == true;
    }

    private sealed class OwnedChartCollectionStorageMutation
    {
        public List<BMSFile> AddedBmsFiles { get; } = [];

        public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = [];

        public List<ChartFile> AddedCharts { get; } = [];

        public List<OwnedChartRemoveRequest> RemoveRequests { get; } = [];

        public List<LibraryChartPathChange> PathChanges { get; } = [];

        public int AddedCount => AddedCharts.Count;

        public int RemovedCount => RemoveRequests.Count;

        public int MovedCount => PathChanges.Count;

        public bool HasChanges => AddedCount > 0 || RemovedCount > 0 || MovedCount > 0;

        public void AddAddedTargets(ChartStorageTargetSet addedTargets)
        {
            if (addedTargets == null)
            {
                return;
            }

            AddedBmsFiles.AddRange(addedTargets.BmsFiles);
            AddedBmsonSongs.AddRange(addedTargets.BmsonSongs);
            AddedCharts.AddRange(addedTargets.Charts.Where(chart => chart != null));
        }
    }

    private static bool HasOwnedHashSetChanges(OwnedChartCollectionMutationResult result)
    {
        return result != null
            && (result.StorageRowsChanged
                || result.DigestChangedCount > 0
                || result.AddedCount > 0
                || result.RemovedCount > 0
                || result.DuplicateCacheInvalidated);
    }

    private readonly struct InstalledChartLookupMutationEntry(string path, string md5, string sha256)
    {
        public string Path { get; } = path;

        public string Md5 { get; } = md5;

        public string Sha256 { get; } = sha256;
    }

    private readonly struct InstalledChartLookupPathMutationEntry(string oldPath, string newPath, string md5, string sha256)
    {
        public string OldPath { get; } = oldPath;

        public string NewPath { get; } = newPath;

        public string Md5 { get; } = md5;

        public string Sha256 { get; } = sha256;
    }

    internal OwnedChartHashIndexVersionedSnapshot GetOwnedChartHashIndexSnapshot()
    {
        return GetOwnedChartHashIndexSnapshot(CancellationToken.None);
    }

    internal OwnedChartHashIndexVersionedSnapshot GetOwnedChartHashIndexSnapshot(CancellationToken cancellationToken)
    {
        return catalogOwnedCollectionOwner.GetHashIndexSnapshot(
            catalogStorageRowsOwner,
            cancellationToken,
            out _,
            out _);
    }

    internal OwnedHashIndexWarmupResult WarmOwnedChartHashIndexSnapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        OwnedChartHashIndexVersionedSnapshot snapshot = catalogOwnedCollectionOwner.GetHashIndexSnapshot(
            catalogStorageRowsOwner,
            CancellationToken.None,
            out bool cacheHit,
            out int staleRetryCount);
        stopwatch.Stop();
        var result = new OwnedHashIndexWarmupResult
        {
            IndexName = "catalog_owned_hash",
            Status = cacheHit ? "cached" : "built",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Md5Count = snapshot?.Md5Count ?? 0,
            Sha256Count = snapshot?.Sha256Count ?? 0,
            SnapshotVersion = snapshot?.Version ?? 0,
            InvalidationVersion = snapshot?.InvalidationVersion ?? 0,
            OwnedCollectionVersion = snapshot?.OwnedCollectionVersion ?? 0,
            BmsRowsVersion = snapshot?.BmsRowsVersion ?? 0,
            BmsonRowsVersion = snapshot?.BmsonRowsVersion ?? 0,
            StaleRetryCount = staleRetryCount
        };
        LogInstallPerformance("owned_adjacent_index_warmup index=" + result.IndexName
            + " reason=" + (reason ?? string.Empty)
            + " status=" + result.Status
            + " elapsedMs=" + result.ElapsedMs
            + " md5Hashes=" + result.Md5Count
            + " sha256Hashes=" + result.Sha256Count
            + " snapshotVersion=" + result.SnapshotVersion
            + " invalidationVersion=" + result.InvalidationVersion
            + " ownedCollectionVersion=" + result.OwnedCollectionVersion
            + " bmsRowsVersion=" + result.BmsRowsVersion
            + " bmsonRowsVersion=" + result.BmsonRowsVersion
            + " staleRetries=" + result.StaleRetryCount);
        return result;
    }

    /// <summary>
    /// playlist detail の entry hash 解決に使う owned collection 隣接 index を返します。
    /// 所持譜面や digest / path 変更時に無効化し、次回要求時にだけ再構築します。
    /// </summary>
    /// <param name="cancellationToken">構築中の cancellation token。</param>
    /// <param name="cacheHit">既存 snapshot を再利用した場合は true。</param>
    /// <param name="staleRetryCount">build 中の mutation により作り直した回数。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal PlaylistLibraryResolveIndexSnapshot GetPlaylistLibraryResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        PlaylistLibraryResolveIndexSnapshot snapshot;
        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool waitForDigestWindow = false;
            int invalidationVersion;
            int invalidationOwnedCollectionVersion;
            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                int currentOwnedCollectionVersion = OwnedChartCollectionVersion;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(snapshot, currentOwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                    playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = currentOwnedCollectionVersion;
                }
                if (IsOwnedDigestMutationWindowActive())
                {
                    waitForDigestWindow = true;
                }
                invalidationVersion = playlistLibraryResolveIndexInvalidationVersion;
                invalidationOwnedCollectionVersion = playlistLibraryResolveIndexInvalidationOwnedCollectionVersion;
            }
            if (waitForDigestWindow)
            {
                WaitForOwnedDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            PlaylistLibraryResolveIndexSnapshot rebuiltSnapshot;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                rebuiltSnapshot = CreatePlaylistLibraryResolveIndexSnapshotUnsafe(cancellationToken, out storageRowsVersion);
                ownedCollectionVersion = OwnedChartCollectionVersion;
            }
            rebuiltSnapshot.BuildElapsedMs = stopwatch.ElapsedMilliseconds;
            rebuiltSnapshot.InvalidationVersion = invalidationVersion;
            rebuiltSnapshot.OwnedCollectionVersion = invalidationOwnedCollectionVersion == ownedCollectionVersion ? invalidationOwnedCollectionVersion : ownedCollectionVersion;
            rebuiltSnapshot.BmsRowsVersion = storageRowsVersion.BmsRowsVersion;
            rebuiltSnapshot.BmsonRowsVersion = storageRowsVersion.BmsonRowsVersion;

            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(snapshot, OwnedChartCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                    playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = OwnedChartCollectionVersion;
                    staleRetryCount++;
                    continue;
                }
                if (playlistLibraryResolveIndexInvalidationVersion != invalidationVersion
                    || OwnedChartCollectionVersion != ownedCollectionVersion
                    || !IsStorageRowsVersionCurrent(storageRowsVersion))
                {
                    staleRetryCount++;
                    continue;
                }
                if (IsOwnedDigestMutationWindowActive())
                {
                    staleRetryCount++;
                    continue;
                }
                rebuiltSnapshot.Version = Interlocked.Increment(ref playlistLibraryResolveIndexSnapshotVersion);
                playlistLibraryResolveIndexSnapshot = rebuiltSnapshot;
                cacheHit = false;
                return rebuiltSnapshot;
            }
        }
    }

    internal PlayHistoryProjectionIndex CreatePlayHistoryProjectionIndex(
        IEnumerable<Lr2PlayHistoryRecord> records,
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> md5s = [.. (records ?? [])
            .Select(record => record?.hash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return CreatePlayHistoryProjectionIndex(md5s, [], cancellationToken, out cacheHit, out staleRetryCount);
    }

    internal PlayHistoryProjectionIndex CreateBeatorajaPlayHistoryProjectionIndex(
        IEnumerable<BeatorajaPlayHistoryRecord> records,
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> sha256s = [.. (records ?? [])
            .Select(record => record?.sha256)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return CreatePlayHistoryProjectionIndex([], sha256s, cancellationToken, out cacheHit, out staleRetryCount);
    }

    private PlayHistoryProjectionIndex CreatePlayHistoryProjectionIndex(
        IReadOnlyList<string> md5s,
        IReadOnlyList<string> sha256s,
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistLibraryResolveIndexSnapshot resolveIndex = GetPlaylistLibraryResolveIndexSnapshot(
            cancellationToken,
            out cacheHit,
            out staleRetryCount);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> sha256ByMd5 = dbGateway.LoadChartDigestMapByMd5(md5s);
        cancellationToken.ThrowIfCancellationRequested();
        Func<string, string, LR2SongDBExtended.chart_info> chartInfoResolver = CreatePlayHistoryChartInfoResolver(
            md5s,
            sha256s,
            sha256ByMd5,
            resolveIndex,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return PlayHistoryProjectionIndex.Create(
            resolveIndex,
            sha256ByMd5,
            GetPlaylistReferenceDisplay,
            chartInfoResolver);
    }

    private Func<string, string, LR2SongDBExtended.chart_info> CreatePlayHistoryChartInfoResolver(
        IReadOnlyList<string> md5s,
        IReadOnlyList<string> sourceSha256s,
        IReadOnlyDictionary<string, string> sha256ByMd5,
        PlaylistLibraryResolveIndexSnapshot resolveIndex,
        CancellationToken cancellationToken)
    {
        List<string> sha256s = [.. (sha256ByMd5?.Values ?? Enumerable.Empty<string>())
            .Where(sha256 => !string.IsNullOrWhiteSpace(sha256))
            .Select(sha256 => sha256.Trim())];
        sha256s.AddRange((sourceSha256s ?? [])
            .Where(sha256 => !string.IsNullOrWhiteSpace(sha256))
            .Select(sha256 => sha256.Trim()));
        foreach (string md5 in md5s ?? Array.Empty<string>())
        {
            LibraryChartRef chartRef = resolveIndex?.ResolveChartForPlaylistHash(md5, null);
            if (!string.IsNullOrWhiteSpace(chartRef?.Sha256))
            {
                sha256s.Add(chartRef.Sha256.Trim());
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, LR2SongDBExtended.chart_info> chartInfoBySha256 = LoadChartInfosBySha256(sha256s);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, LR2SongDBExtended.chart_info> chartInfoByMd5 = LoadChartInfosByMd5(md5s);
        return (sha256, md5) =>
        {
            if (!string.IsNullOrWhiteSpace(sha256)
                && chartInfoBySha256.TryGetValue(sha256.Trim(), out LR2SongDBExtended.chart_info bySha256))
            {
                return bySha256;
            }
            if (!string.IsNullOrWhiteSpace(md5)
                && chartInfoByMd5.TryGetValue(md5.Trim(), out LR2SongDBExtended.chart_info byMd5))
            {
                return byMd5;
            }
            return null;
        };
    }

    internal PlaylistLibraryResolveIndexRuntimeState GetPlaylistLibraryResolveIndexRuntimeState()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
            int currentOwnedCollectionVersion = OwnedChartCollectionVersion;
            if (IsPlaylistLibraryResolveIndexSnapshotCurrent(snapshot, currentOwnedCollectionVersion))
            {
                return new PlaylistLibraryResolveIndexRuntimeState
                {
                    IsCached = true,
                    SnapshotVersion = snapshot.Version,
                    BuildElapsedMs = snapshot.BuildElapsedMs,
                    InvalidationVersion = snapshot.InvalidationVersion,
                    OwnedCollectionVersion = snapshot.OwnedCollectionVersion
                };
            }
            return new PlaylistLibraryResolveIndexRuntimeState
            {
                IsCached = false,
                InvalidationVersion = playlistLibraryResolveIndexInvalidationVersion,
                OwnedCollectionVersion = currentOwnedCollectionVersion
            };
        }
    }

    private bool IsPlaylistLibraryResolveIndexSnapshotCurrent(
        PlaylistLibraryResolveIndexSnapshot snapshot,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion
            && catalogStorageRowsOwner.BmsRowsVersion == snapshot.BmsRowsVersion
            && catalogStorageRowsOwner.BmsonRowsVersion == snapshot.BmsonRowsVersion;
    }

    private bool IsStorageRowsVersionCurrent(StorageRowsVersionSnapshot storageRowsVersion)
    {
        return catalogStorageRowsOwner.BmsRowsVersion == storageRowsVersion.BmsRowsVersion
            && catalogStorageRowsOwner.BmsonRowsVersion == storageRowsVersion.BmsonRowsVersion;
    }

    private List<ChartFile> CreateOwnedChartInfoFullBackfillTargetSnapshot()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateSnapshot(
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false);
        }
    }

    /// <summary>
    /// Creates a read-only owned chart snapshot for diagnostics and tests that need the same surface as chart-info backfill.
    /// </summary>
    /// <returns>The owned chart snapshot.</returns>
    internal List<ChartFile> CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics()
    {
        return CreateOwnedChartInfoFullBackfillTargetSnapshot();
    }

    /// <summary>
    /// Creates a read-only owned chart snapshot after applying install-destination runtime overlays.
    /// </summary>
    /// <returns>The overlaid owned chart snapshot.</returns>
    internal List<ChartFile> CreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlayForDiagnostics()
    {
        return installDestinationStateOwner.OverlayRuntimeStates(CreateOwnedChartInfoFullBackfillTargetSnapshot());
    }

    private ILibraryChartCanonicalLookup CreateOwnedCanonicalChartLookupUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateCanonicalChartLookupSnapshot();
        }
    }

    private List<LibraryChartRef> CreateOwnedRealPathChartRefsUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsUnderRealPath(directoryPath);
        }
    }

    /// <summary>
    /// folder / merge 操作で使う real path directory view を readiness 外で温めます。
    /// </summary>
    /// <param name="reason">warmup を要求した理由。</param>
    /// <returns>warmup 結果。</returns>
    internal OwnedAdjacentIndexWarmupResult WarmOwnedRealPathDirectoryView(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        string status;
        LibraryChartRefIndexSnapshot snapshot;
        int ownedVersion;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                status = catalogOwnedCollectionOwner.Collection.IsLibraryChartRefIndexSnapshotInitialized ? "cached" : "built";
                snapshot = catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefIndexSnapshot();
                ownedVersion = OwnedChartCollectionVersion;
            }
        }
        stopwatch.Stop();
        var result = new OwnedAdjacentIndexWarmupResult
        {
            IndexName = "real_path",
            Status = status,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            ChartRefCount = snapshot?.ChartRefCount ?? 0,
            DirectDirectoryCount = snapshot?.DirectDirectoryCount ?? 0,
            SubtreeDirectoryCount = snapshot?.SubtreeDirectoryCount ?? 0,
            OwnedCollectionVersion = ownedVersion
        };
        LogInstallPerformance("owned_adjacent_index_warmup index=" + result.IndexName
            + " reason=" + (reason ?? string.Empty)
            + " status=" + result.Status
            + " elapsedMs=" + result.ElapsedMs
            + " chartRefs=" + result.ChartRefCount
            + " directDirs=" + result.DirectDirectoryCount
            + " subtreeDirs=" + result.SubtreeDirectoryCount
            + " ownedCollectionVersion=" + result.OwnedCollectionVersion);
        return result;
    }

    /// <summary>
    /// folder / merge 操作で使う install destination overlay view を readiness 外で温めます。
    /// </summary>
    /// <param name="reason">warmup を要求した理由。</param>
    /// <returns>warmup 結果。</returns>
    internal OwnedAdjacentIndexWarmupResult WarmInstallDestinationOverlaySnapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        InstallDestinationOverlayChartRefSnapshot snapshot = installDestinationStateOwner.CreateOverlaySnapshot(out bool wasCached);
        string status = wasCached ? "cached" : "built";
        stopwatch.Stop();
        var result = new OwnedAdjacentIndexWarmupResult
        {
            IndexName = "install_destination_overlay",
            Status = status,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            ChartRefCount = snapshot?.ChartCount ?? 0,
            DirectoryCount = snapshot?.DirectoryCount ?? 0
        };
        LogInstallPerformance("owned_adjacent_index_warmup index=" + result.IndexName
            + " reason=" + (reason ?? string.Empty)
            + " status=" + result.Status
            + " elapsedMs=" + result.ElapsedMs
            + " chartRefs=" + result.ChartRefCount
            + " directories=" + result.DirectoryCount);
        return result;
    }

    /// <summary>
    /// duplicate merge / install state 判定用の primary md5 lookup を readiness 外で温めます。
    /// full directory lookup は構築しません。
    /// </summary>
    /// <param name="reason">warmup を要求した理由。</param>
    /// <returns>warmup 結果。</returns>
    internal InstalledPrimaryHashWarmupResult WarmInstalledPrimaryHashLookup(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        bool built = EnsureInstalledPrimaryHashLookupBuiltUnsafe(out long buildMs, out int bmsCount, out int bmsonCount);
        int primaryHashCount;
        lock (lockInstalledPrimaryHashLookup)
        {
            primaryHashCount = installedPrimaryHashLookup?.DistinctPrimaryHashCount ?? 0;
        }
        bool fullDirectoryLookupInitialized = IsInstalledChartLookupIndexInitializedUnsafe();
        stopwatch.Stop();
        var result = new InstalledPrimaryHashWarmupResult
        {
            IndexName = "installed_primary_hash",
            Status = built ? "built" : "cached",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            BuildMs = buildMs,
            PrimaryHashCount = primaryHashCount,
            BmsCount = bmsCount,
            BmsonCount = bmsonCount,
            FullDirectoryLookupInitialized = fullDirectoryLookupInitialized
        };
        LogInstallPerformance("owned_adjacent_index_warmup index=" + result.IndexName
            + " reason=" + (reason ?? string.Empty)
            + " status=" + result.Status
            + " elapsedMs=" + result.ElapsedMs
            + " buildMs=" + result.BuildMs
            + " primaryHashes=" + result.PrimaryHashCount
            + " files=" + result.BmsCount
            + " bmson=" + result.BmsonCount
            + " rows=" + (result.BmsCount + result.BmsonCount)
            + " fullDirectoryLookupInitialized=" + result.FullDirectoryLookupInitialized);
        return result;
    }

    internal bool HasOwnedChartUnderRealPath(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return catalogOwnedCollectionOwner.Collection.CountLibraryChartRefsUnderRealPath(directoryPath) > 0;
            }
        }
    }

    private List<string> CreateOwnedRealPathChartDirectoriesUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateChartDirectoriesUnderRealPath(directoryPath);
        }
    }

    private bool TryCreateOwnedChartRefsForPathsUnsafe(IEnumerable<string> paths, out List<LibraryChartRef> chartRefs)
    {
        lock (lockOwnedChartCollection)
        {
            if (!catalogOwnedCollectionOwner.IsInitialized)
            {
                chartRefs = null;
                return false;
            }
            chartRefs = catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsForPaths(paths);
            return true;
        }
    }

    private bool TryScanOwnedChartRefsForPathsUnsafe(IEnumerable<string> paths, out List<LibraryChartRef> chartRefs)
    {
        lock (lockOwnedChartCollection)
        {
            if (!catalogOwnedCollectionOwner.IsInitialized)
            {
                chartRefs = null;
                return false;
            }
            chartRefs = catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsForPathsByScan(paths);
            return true;
        }
    }

    private ChartStorageTargetSet CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateStorageTargetsForSubtreeDirectory(directoryPath);
        }
    }

    private PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshotUnsafe(
        CancellationToken cancellationToken,
        out StorageRowsVersionSnapshot storageRowsVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwnedChartCollectionBuiltUnsafe();
        List<LibraryChartRef> refs;
        lock (lockStorageRowsVersion)
        {
            StorageRowsVersionSnapshot currentVersion = CreateCurrentStorageRowsVersionSnapshotUnsafe();
            lock (lockOwnedChartCollection)
            {
                if (!catalogOwnedCollectionOwner.IsCurrent(currentVersion.BmsRowsVersion, currentVersion.BmsonRowsVersion))
                {
                    throw new InvalidOperationException("Owned chart collection storage row version is not current.");
                }
                storageRowsVersion = currentVersion;
                refs = catalogOwnedCollectionOwner.Collection.CreatePlaylistLibraryResolveRefSnapshot(cancellationToken.ThrowIfCancellationRequested);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(refs, cancellationToken.ThrowIfCancellationRequested);
    }

    private ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummaryUnsafe(
        ISet<string> currentChartInfoSha256s,
        ISet<string> currentParseFailureMd5s)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateChartInfoHydrationOwnerSummary(
                currentChartInfoSha256s,
                currentParseFailureMd5s);
        }
    }

    private List<ChartFile> CreateOwnedDirectChildChartFilesUnsafe(
        IEnumerable<string> directoryPaths,
        bool includeWarningSnapshot,
        bool includeResourceReferences)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateSnapshotForDirectChildDirectories(
                directoryPaths,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: false);
        }
    }

    private List<ChartFile> CreateOwnedChartFilesForMd5HashesUnsafe(
        ISet<string> md5Hashes,
        bool includeWarningSnapshot,
        bool includeResourceReferences)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateSnapshotForMd5Hashes(
                md5Hashes,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: false);
        }
    }

    private List<ChartFile> CreateOwnedChartFilesForExactPathsUnsafe(
        IEnumerable<string> paths,
        bool includeWarningSnapshot,
        bool includeResourceReferences)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateSnapshotForPaths(
                paths,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: false);
        }
    }

    private List<ChartFile> CreateOwnedBmsChartFilesUnsafe(bool includeResourceReferences)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateBmsSnapshot(
                includeWarningSnapshot: false,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: false);
        }
    }

    private OwnedDuplicateChartRowSnapshot CreateOwnedDuplicateChartRowSnapshotUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot();
        }
    }

    private OwnedChartStorageOwnerView CreateOwnedChartStorageOwnerViewUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateStorageOwnerView();
        }
    }

    internal OwnedChartStorageOwnerView CreateNormalLibrarySourceStorageOwnerView()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return catalogOwnedCollectionOwner.Collection.CreateNormalLibrarySourceStorageOwnerView();
            }
        }
    }

    private InstalledChartLookupIndexState CreateOwnedInstalledChartLookupIndexStateUnsafe(out int bmsCount, out int bmsonCount)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateInstalledChartLookupIndexState(out bmsCount, out bmsonCount);
        }
    }

    private PrimaryHashLookupState CreateOwnedInstalledPrimaryHashLookupStateUnsafe(out int bmsCount, out int bmsonCount)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreatePrimaryHashLookupState(out bmsCount, out bmsonCount);
        }
    }

    private HashSet<string> CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.CreateInstallDestinationRuntimeStateKeySnapshot();
        }
    }

    internal HashSet<string> CreateOwnedChartRuntimeStatePrimaryKeySnapshot()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return catalogOwnedCollectionOwner.Collection.CreateChartRuntimeStatePrimaryKeySnapshot();
            }
        }
    }

    private void EnsureOwnedChartCollectionBuiltUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe(CancellationToken.None);
    }

    private void EnsureOwnedChartCollectionBuiltUnsafe(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageRowsVersionSnapshot versions = catalogStorageRowsOwner.CaptureVersionSnapshot();
            if (catalogOwnedCollectionOwner.IsCurrent(
                versions.BmsRowsVersion,
                versions.BmsonRowsVersion))
            {
                return;
            }

            CatalogStorageRowsSnapshot storageRows = catalogStorageRowsOwner.CaptureSnapshot();
            OwnedChartCollectionState rebuiltCollection = OwnedChartCollectionState.FromStorageRows(
                storageRows.BmsRows,
                storageRows.BmsonRows,
                cancellationToken,
                out OwnedChartStorageRowFilterSummary filterSummary);
            cancellationToken.ThrowIfCancellationRequested();
            LogOwnedChartCollectionSkippedRows("build", filterSummary);
            lock (lockStorageRowsVersion)
            {
                if (storageRows.BmsRowsVersion != bmsStorageRowsVersion
                    || storageRows.BmsonRowsVersion != bmsonStorageRowsVersion)
                {
                    continue;
                }
                if (catalogOwnedCollectionOwner.IsCurrent(
                    storageRows.BmsRowsVersion,
                    storageRows.BmsonRowsVersion))
                {
                    return;
                }
                catalogOwnedCollectionOwner.ApplyBuiltCollection(
                    rebuiltCollection,
                    storageRows.BmsRowsVersion,
                    storageRows.BmsonRowsVersion);
                return;
            }
        }
    }

    private Action ApplyFileScanCatalogReplacement(FileScanCatalogReplacementEvent replacementEvent)
    {
        if (replacementEvent == null)
        {
            return null;
        }

        CatalogFileScanStorageReplacementReceipt receipt = replacementEvent.Receipt;
        if (replacementEvent.Request.HasDbDiff)
        {
            MarkDuplicateWarningFullClearPending();
        }
        libraryResourceIndexOwner.Replace(
            replacementEvent.NextResourceIndex
                ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult()));
        if (receipt.OwnedCollectionApplied)
        {
            LogOwnedChartCollectionSkippedRows("replace", receipt.FilterSummary);
        }

        OwnedChartCollectionMutationResult mutationResult = CreateFileScanMutationProjection(
            replacementEvent.Request,
            replacementEvent.ResourceHealthIndexCurrentAtBase);
        mutationResult.OwnedCollectionVersion = receipt.OwnedCollectionVersion;
        mutationResult.OwnedCollectionVersionAlreadyAdvanced = receipt.Applied;
        string dispatchReason = string.IsNullOrWhiteSpace(replacementEvent.Reason)
            ? "file_scan"
            : "file_scan_" + replacementEvent.Reason;
        Action duplicateChartGroupsPostLeaseNotification = DispatchOwnedChartCollectionMutation(
            mutationResult,
            dispatchReason,
            publishNormalRefreshNotification: false,
            publishOwnedCollectionNotifications: false,
            deferDuplicateChartGroupsNotification: true);
        return () =>
        {
            TryInvokePostLeaseNotification(
                duplicateChartGroupsPostLeaseNotification,
                "file_scan_duplicate_chart_groups_notification_failed");
            if (mutationResult.ParentFolderInvalidated)
            {
                TryInvokePostLeaseNotification(
                    NotifyBMSParentFolderListCacheChanged,
                    "file_scan_parent_folder_notification_failed");
            }
            if (mutationResult.OwnedCollectionChanged)
            {
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "file_scan_owned_collection_notification_failed");
            }
            TryInvokePostLeaseNotification(
                () => PublishNormalLibraryRefreshNotification(mutationResult),
                "file_scan_normal_refresh_publication_failed");
            TryInvokePostLeaseNotification(
                () => RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult),
                "file_scan_normal_refresh_notification_failed");
        };
    }

    private Action ApplyFileScanCatalogResidualForScan(FileScanCatalogResidualEvent residualEvent)
    {
        if (residualEvent == null)
        {
            return null;
        }

        IReadOnlyList<ChartFile> installDestinationChangedCharts =
            installDestinationStateOwner.ReattachFileScanResidualInstallDestinationCharts(
                residualEvent.InstallDestinationChangedCharts);
        var mutationResult = new OwnedChartCollectionMutationResult
        {
            InstallDestinationChangedCount = installDestinationChangedCharts.Count,
            InstallEstimationMetadataProfileCacheInvalidated = residualEvent.InvalidateInstalledDirectoryIndex,
            DuplicateCacheInvalidated = residualEvent.ClearDuplicatedCache,
            WarningPresentationChanged = residualEvent.ClearDuplicatedCache
        };
        mutationResult.InstallDestinationRuntimeStateMutation.AppliedCharts.AddRange(
            installDestinationChangedCharts);
        bool duplicateChartGroupsInvalidated = false;
        try
        {
            if (mutationResult.InstallDestinationRuntimeStateMutation.HasStateChanges)
            {
                installDestinationStateOwner.Apply(mutationResult.InstallDestinationRuntimeStateMutation);
            }
            if (mutationResult.DuplicateCacheInvalidated)
            {
                duplicateChartGroupsInvalidated = InvalidateDuplicateChartGroupsCache(
                    publishNotification: false);
            }
            if (mutationResult.InstallEstimationMetadataProfileCacheInvalidated)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
        }
        catch
        {
            if (mutationResult.DuplicateCacheInvalidated)
            {
                InvalidateDuplicateChartGroupsCache();
            }
            if (mutationResult.InstallEstimationMetadataProfileCacheInvalidated)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
            if (mutationResult.InstallDestinationRuntimeStateMutation.HasChanges)
            {
                installDestinationStateOwner.PruneToCurrentOwnedCharts();
            }
            InvalidateOwnedChartCollection();
            ClearNormalLibraryRefreshNotification(mutationResult);
            throw;
        }
        return () =>
        {
            TryInvokePostLeaseNotification(
                () =>
                {
                    if (duplicateChartGroupsInvalidated)
                    {
                        RaisePropertyChanged(() => DuplicateChartGroupsInvalidationVersion);
                    }
                },
                "file_scan_residual_duplicate_chart_groups_notification_failed");
            TryInvokePostLeaseNotification(
                () => PublishNormalLibraryRefreshNotification(mutationResult),
                "file_scan_residual_normal_refresh_publication_failed");
            TryInvokePostLeaseNotification(
                () => RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult),
                "file_scan_residual_normal_refresh_notification_failed");
        };
    }

    internal void ApplyFileScanCatalogResidual(FileScanCatalogResidualEvent residualEvent)
    {
        Action postLeaseEffects = ApplyFileScanCatalogResidualForScan(residualEvent);
        TryInvokePostLeaseNotification(
            postLeaseEffects,
            "file_scan_residual_publication_failed");
    }

    private void LogOwnedChartCollectionSkippedRows(string reason, OwnedChartStorageRowFilterSummary filterSummary)
    {
        if (!filterSummary.HasSkippedRows)
        {
            return;
        }
        LogInstallPerformance("owned_chart_collection_storage_rows_skipped reason=" + (reason ?? "(null)")
            + " pathlessBms=" + filterSummary.PathlessBmsCount
            + " pathlessBmson=" + filterSummary.PathlessBmsonCount
            + " md5lessBms=" + filterSummary.Md5lessBmsCount
            + " md5lessBmson=" + filterSummary.Md5lessBmsonCount
            + " duplicatePathBms=" + filterSummary.DuplicatePathBmsCount
            + " duplicatePathBmson=" + filterSummary.DuplicatePathBmsonCount);
    }

    internal void ApplyInstalledChartStorageTargets(ChartStorageTargetSet addedTargets, string lookupReason)
    {
        if (addedTargets == null)
        {
            return;
        }

        LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            "ApplyInstalledChartStorageTargets",
            showMessage: false);
        if (mutationReservation == null)
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }
        InstalledChartStorageTargetsApplyReceipt receipt = null;
        Exception primaryFailure = null;
        try
        {
            try
            {
                using LibraryFileMutationCapability mutationCapability = mutationReservation.CreateMutationCapability();
                mutationCapability.Validate(lr2SynchronizationOwner);
                receipt = ApplyInstalledChartStorageTargetsForDeferredDispatch(
                    addedTargets,
                    lookupReason ?? "install_package",
                    mutationCapability);
                CompleteInstalledChartStorageTargetsUnderExistingReservation(
                    receipt,
                    mutationCapability);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
        }
        finally
        {
            mutationReservation.Dispose();
        }

        try
        {
            PublishInstalledChartStorageTargetsAfterGuard(receipt);
        }
        catch (Exception exception)
        {
            // Public notification is best effort.  Keep this guard for an
            // unexpected failure in the notification owner itself, but never
            // let it replace the primary durable mutation result.
            NLogWrapper.FileLogger?.Warn(
                exception,
                "installed_chart_storage_target_publication_guard_failed");
        }
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private InstalledChartStorageTargetsApplyReceipt ApplyInstalledChartStorageTargetsForDeferredDispatch(
        ChartStorageTargetSet addedTargets,
        string lookupReason,
        LibraryFileMutationCapability mutationCapability,
        Action<string> logOverride = null,
        string installPathToDelete = null)
    {
        if (addedTargets == null)
        {
            return InstalledChartStorageTargetsApplyReceipt.Empty;
        }

        OwnedChartCollectionMutationResult mutationResult = null;
        CatalogInstalledTargetUpsertReceipt installedTargetReceipt = null;
        CatalogWriteFailureFact deferredFailureFact = null;
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        StorageRowsVersionSnapshot storageRowsBefore = catalogStorageRowsOwner.CaptureVersionSnapshot();
        bool catalogValidationPassed = false;
        try
        {
            resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            try
            {
                mutationResult = BuildOwnedChartCollectionUpsertMutationResult(
                    addedTargets,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                using (mutationResult.ResourceHealthIndexInvalidated
                    ? resourceHealthOwner.SuppressInvalidation()
                    : null)
                {
                    installedTargetReceipt = catalogMutationOwner.ApplyInstalledTargetUpsertWithDeferredFailurePublication(
                        addedTargets.BmsFiles,
                        addedTargets.BmsonSongs,
                        out deferredFailureFact,
                        () => catalogValidationPassed = true,
                        installPathToDelete);
                    mutationResult.OwnedCollectionVersion = installedTargetReceipt.OwnedCollectionVersion;
                    mutationResult.OwnedCollectionVersionAlreadyAdvanced = installedTargetReceipt.OwnedCollectionApplied;
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }

            mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion ??= resourceHealthMutation.TargetInputVersion;
            if (mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion.Value < 0)
            {
                mutationResult.ResourceHealthMutation.Invalidate = true;
            }
            return new InstalledChartStorageTargetsApplyReceipt(
                mutationResult,
                installedTargetReceipt,
                deferredFailureFact,
                lookupReason,
                logOverride,
                failureFallbackRequired: false,
                failure: null);
        }
        catch (Exception exception)
        {
            StorageRowsVersionSnapshot storageRowsAfter = catalogStorageRowsOwner.CaptureVersionSnapshot();
            bool failureFallbackRequired = !catalogValidationPassed
                || storageRowsBefore.BmsRowsVersion != storageRowsAfter.BmsRowsVersion
                || storageRowsBefore.BmsonRowsVersion != storageRowsAfter.BmsonRowsVersion;
            return new InstalledChartStorageTargetsApplyReceipt(
                mutationResult,
                installedTargetReceipt,
                deferredFailureFact,
                lookupReason,
                logOverride,
                failureFallbackRequired,
                ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    /// Completes installed-target LR2 and semantic state while the caller's
    /// original file-mutation reservation is still active.  This route never
    /// acquires a second reservation; only publication is deferred to the
    /// lease's post-commit effect queue.
    /// </summary>
    private void CompleteInstalledChartStorageTargetsUnderExistingReservation(
        InstalledChartStorageTargetsApplyReceipt receipt,
        LibraryFileMutationCapability mutationCapability)
    {
        if (receipt == null || ReferenceEquals(receipt, InstalledChartStorageTargetsApplyReceipt.Empty))
        {
            return;
        }
        if (receipt.Failure != null)
        {
            receipt.Failure.Throw();
        }

        try
        {
            lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation(
                CreateLr2NormalFolderCatalogMutationReceipt(
                    receipt.InstalledTargetReceipt,
                    receipt.MutationResult.OwnedCollectionVersion),
                receipt.LookupReason ?? "install_package",
                mutationCapability);
            ApplyInstalledChartStorageTargetsSemanticStateUnderGuard(receipt);
        }
        catch (Exception exception)
        {
            receipt.RecordCompletionFailure(exception);
            throw;
        }
    }

    private void ApplyInstalledChartStorageTargetsSemanticStateUnderGuard(
        InstalledChartStorageTargetsApplyReceipt receipt)
    {
        ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
            receipt.MutationResult,
            receipt.LookupReason,
            receipt.LogOverride);
    }

    private void ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
        OwnedChartCollectionMutationResult result,
        string reason,
        Action<string> logOverride)
    {
        if (result.InstallDestinationRuntimeStateMutation.HasStateChanges)
        {
            installDestinationStateOwner.Apply(result.InstallDestinationRuntimeStateMutation);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
        {
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
        }
        result.InstallDestinationRuntimeStateApplied = true;
        if (HasOwnedHashSetChanges(result))
        {
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            result.InstalledHashIndexInvalidated = true;
        }
        if (result.OwnedCollectionChanged)
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot(result.OwnedCollectionVersion);
            result.PlaylistResolveIndexInvalidated = true;
        }
        if (result.InstallEstimationMetadataProfileCacheInvalidated || result.ShouldDispatchInstalledLookup)
        {
            InvalidateInstallEstimationMetadataProfileCache();
            result.InstallMetadataCacheInvalidated = true;
        }
        if (result.ShouldDispatchInstalledLookup)
        {
            ApplyInstalledChartLookupMutation(
                result.InstalledLookupMutation,
                reason,
                logOverride);
            result.InstalledLookupMutationApplied = true;
        }
    }

    private void PublishInstalledChartStorageTargetsAfterGuard(
        InstalledChartStorageTargetsApplyReceipt receipt)
    {
        if (receipt == null || ReferenceEquals(receipt, InstalledChartStorageTargetsApplyReceipt.Empty))
        {
            return;
        }
        catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(receipt.DeferredFailureFact);
        if (receipt.Failure != null)
        {
            if (receipt.FailureFallbackRequired)
            {
                try
                {
                    ApplyInstalledChartStorageTargetsFailureFallback(receipt.MutationResult);
                }
                catch (Exception exception)
                {
                    // Failure-side invalidation is diagnostic once the
                    // primary catalog/DB failure has been recorded.
                    NLogWrapper.FileLogger?.Warn(
                        exception,
                        "installed_chart_storage_target_failure_fallback_failed");
                }
            }
            // Failure-side publication must not replace the primary catalog or
            // filesystem failure already carried by the durable receipt.
            return;
        }
        TryInvokePostLeaseNotification(
            () => PublishOwnedCollectionChangeNotification(receipt.MutationResult),
            "installed_chart_storage_target_collection_notification_failed");
        TryInvokePostLeaseNotification(
            () => DispatchOwnedChartCollectionMutation(receipt.MutationResult, receipt.LookupReason),
            "installed_chart_storage_target_dispatch_failed");
    }

    private sealed class InstalledChartStorageTargetsApplyReceipt(
        OwnedChartCollectionMutationResult mutationResult,
        CatalogInstalledTargetUpsertReceipt installedTargetReceipt,
        CatalogWriteFailureFact deferredFailureFact,
        string lookupReason,
        Action<string> logOverride,
        bool failureFallbackRequired,
        ExceptionDispatchInfo failure)
    {
        internal static InstalledChartStorageTargetsApplyReceipt Empty { get; } = new(
            null,
            null,
            null,
            null,
            null,
            failureFallbackRequired: false,
            failure: null);

        internal OwnedChartCollectionMutationResult MutationResult { get; } = mutationResult;

        internal CatalogInstalledTargetUpsertReceipt InstalledTargetReceipt { get; } = installedTargetReceipt;

        internal CatalogWriteFailureFact DeferredFailureFact { get; } = deferredFailureFact;

        internal string LookupReason { get; } = lookupReason;

        internal Action<string> LogOverride { get; } = logOverride;

        internal bool FailureFallbackRequired { get; private set; } = failureFallbackRequired;

        internal ExceptionDispatchInfo Failure { get; private set; } = failure;

        internal void RecordCompletionFailure(Exception exception)
        {
            FailureFallbackRequired = true;
            Failure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private void ApplyInstalledChartStorageTargetsFailureFallback(OwnedChartCollectionMutationResult mutationResult)
    {
        if (mutationResult?.ShouldDispatchInstalledLookup != false)
        {
            InvalidateInstalledDirectoryIndex();
        }
        if (HasOwnedHashSetChanges(mutationResult))
        {
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
        }
        if (mutationResult?.OwnedCollectionChanged == true)
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot();
        }
        if (mutationResult?.ParentFolderInvalidated == true)
        {
            InvalidateBMSParentFolderListCacheAndNotify();
        }
        if (mutationResult?.DuplicateCacheInvalidated == true)
        {
            InvalidateDuplicateChartGroupsCache();
        }
        if (mutationResult?.InstallDestinationRuntimeStateMutation.HasChanges == true
            || mutationResult?.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts == true)
        {
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
        }
        if (mutationResult?.OwnedCollectionChanged == true)
        {
            PublishOwnedCollectionChangeNotification(mutationResult);
        }
        if (mutationResult?.ResourceHealthMutation.HasChanges == true)
        {
            resourceHealthOwner.ForceInvalidate("install_package_failed");
        }
        InvalidateOwnedChartCollection();
        if (mutationResult != null)
        {
            ClearNormalLibraryRefreshNotification(mutationResult);
        }
    }

    private OwnedChartCollectionMutationResult CreateFileScanMutationProjection(
        CatalogFileScanStorageReplacementRequest request,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        var storageMutation = new OwnedChartCollectionStorageMutation();
        if (request.RemovedPayloadAvailable)
        {
            storageMutation.RemoveRequests.AddRange((request.RemovedCharts ?? [])
                .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
                .Where(request => request != null));
        }
        storageMutation.AddAddedTargets(
            ChartStorageTargetSet.FromRows(request.AddedBmsFiles, request.AddedBmsonSongs));
        bool bmsRowsChanged = request.DeletedBmsPaths.Count > 0 || request.AddedBmsFiles.Count > 0;
        bool bmsonRowsChanged = request.DeletedBmsonPaths.Count > 0 || request.AddedBmsonSongs.Count > 0;
        bool storageRowsChanged = bmsRowsChanged || bmsonRowsChanged || request.HasDbDiff;
        bool resourceHealthShouldInvalidate = request.HasDbDiff || (resourceHealthIndexCurrentAtBase ?? resourceHealthOwner.IsCurrent());
        bool fileScanPresentationChanged = request.HasDbDiff || resourceHealthShouldInvalidate;

        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupFileScanMutation(storageMutation, request.RemovedPayloadAvailable, request.HasDbDiff),
            InstallEstimationMetadataProfileCacheInvalidated = request.HasDbDiff,
            AddedCount = storageMutation.AddedCount,
            RemovedCount = request.RemovedPayloadAvailable
                ? storageMutation.RemovedCount
                : request.DeletedBmsPaths.Count + request.DeletedBmsonPaths.Count,
            MovedCount = storageMutation.MovedCount,
            ParentFolderInvalidated = request.HasDbDiff,
            DuplicateCacheInvalidated = request.HasDbDiff,
            OwnedCollectionChanged = storageRowsChanged,
            ResourceHealthIndexInvalidated = resourceHealthShouldInvalidate,
            WarningPresentationChanged = fileScanPresentationChanged,
            MaintenancePresentationChanged = fileScanPresentationChanged,
            BmsFilesStorageRowsChanged = bmsRowsChanged,
            BmsonSongsStorageRowsChanged = bmsonRowsChanged
        };
        result.StorageMutation.AddedBmsFiles.AddRange(storageMutation.AddedBmsFiles);
        result.StorageMutation.AddedBmsonSongs.AddRange(storageMutation.AddedBmsonSongs);
        result.StorageMutation.AddedCharts.AddRange(storageMutation.AddedCharts);
        result.StorageMutation.RemoveRequests.AddRange(storageMutation.RemoveRequests);
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = storageRowsChanged;
        return result;
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupFileScanMutation(
        OwnedChartCollectionStorageMutation storageMutation,
        bool removedPayloadAvailable,
        bool hasDbDiff)
    {
        InstalledChartLookupMutation mutation = BuildInstalledChartLookupMutation(storageMutation, null);
        if (!removedPayloadAvailable && hasDbDiff)
        {
            mutation.RequiresFullInvalidate = true;
            return mutation;
        }
        foreach (ChartFile chart in storageMutation?.AddedCharts ?? [])
        {
            mutation.Added.Add(CreateInstalledChartLookupMutationEntry(chart));
        }
        return mutation;
    }

    private static string CreateOwnedPathKey(string path)
        => OwnedChartCollectionState.CreateOwnedPathKey(path);

    private StorageRowsVersionSnapshot CaptureStorageRowsVersionUnsafe()
    {
        lock (lockStorageRowsVersion)
        {
            return CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
    }

    internal static List<BMSFile> NormalizeBmsStorageRows(IEnumerable<BMSFile> files)
    {
        return files == null ? [] : [.. files];
    }

    internal static List<LR2SongDBExtended.bmson_song> NormalizeBmsonStorageRows(
        IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        return songs == null ? [] : [.. songs];
    }

    private StorageRowsVersionSnapshot CreateCurrentStorageRowsVersionSnapshotUnsafe()
    {
        return catalogStorageRowsOwner.CaptureVersionSnapshot();
    }

    private StorageRowsVersionSnapshot CreateStorageRowsVersionSnapshotUnsafe(int previousBmsRowsVersion, int previousBmsonRowsVersion)
    {
        return new StorageRowsVersionSnapshot(
            previousBmsRowsVersion,
            previousBmsonRowsVersion,
            catalogStorageRowsOwner.BmsRowsVersion,
            catalogStorageRowsOwner.BmsonRowsVersion);
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionMutationResult(
        LibraryMutationDelta delta,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        OwnedChartCollectionStorageMutation storageMutation = BuildOwnedChartCollectionStorageMutation(delta);
        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupMutation(storageMutation, delta?.FolderPathChanges),
            InstallEstimationMetadataProfileCacheInvalidated = delta?.InvalidateInstalledDirectoryIndex == true,
            AddedCount = storageMutation.AddedCount,
            RemovedCount = storageMutation.RemovedCount,
            MovedCount = storageMutation.MovedCount,
            InstallDestinationChangedCount = delta?.UpdatedInstallDestinations.Count ?? 0,
            InstalledPackagePathChangedCount = delta?.UpdatedInstalledPackagePaths.Count ?? 0,
            ParentFolderInvalidated = delta?.InvalidateParentFolderCache == true,
            DuplicateCacheInvalidated = delta?.ClearDuplicatedCache == true,
            OwnedCollectionChanged = storageMutation.HasChanges,
            WarningPresentationChanged = delta?.ClearDuplicatedCache == true || storageMutation.HasChanges,
            BmsFilesStorageRowsChanged = HasBmsStorageRowCollectionChange(storageMutation)
                || (delta?.NotifyStorageRowPathChanges == true && HasBmsStorageRowPathChange(storageMutation)),
            BmsonSongsStorageRowsChanged = HasBmsonStorageRowCollectionChange(storageMutation)
                || (delta?.NotifyStorageRowPathChanges == true && HasBmsonStorageRowPathChange(storageMutation)),
            StorageRowsRemoveDeltaComplete = storageMutation.RemovedCount > 0
                && storageMutation.AddedCount == 0
                && storageMutation.MovedCount == 0
        };
        result.StorageMutation.AddedBmsFiles.AddRange(storageMutation.AddedBmsFiles);
        result.StorageMutation.AddedBmsonSongs.AddRange(storageMutation.AddedBmsonSongs);
        result.StorageMutation.AddedCharts.AddRange(storageMutation.AddedCharts);
        result.StorageMutation.RemoveRequests.AddRange(storageMutation.RemoveRequests);
        result.StorageMutation.PathChanges.AddRange(storageMutation.PathChanges);
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = storageMutation.RemovedCount > 0;
        result.InstallDestinationRuntimeStateMutation.PathChanges.AddRange(storageMutation.PathChanges);
        result.InstallDestinationRuntimeStateMutation.AppliedCharts.AddRange(installDestinationStateOwner.CreateChangedChartSnapshots(delta, storageMutation.PathChanges));
        ConfigureResourceHealthMutationForStorageMutation(
            result,
            storageMutation,
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion,
            resourceHealthIndexCurrentAtBase);
        return result;
    }

    private OwnedChartCollectionStorageMutation BuildOwnedChartCollectionStorageMutation(LibraryMutationDelta delta)
    {
        var mutation = new OwnedChartCollectionStorageMutation();
        if (delta == null)
        {
            return mutation;
        }

        mutation.RemoveRequests.AddRange(delta.ChartRemoveRequests.Where(request => request != null));
        ResolveCurrentOwnedRemoveRequests(mutation.RemoveRequests);
        mutation.AddAddedTargets(ChartStorageTargetSet.FromRows(delta.AddedBmsFiles, delta.AddedBmsonSongs));
        mutation.PathChanges.AddRange(delta.ChartPathChanges.Where(change => change?.Chart != null));
        return mutation;
    }

    private void ResolveCurrentOwnedRemoveRequests(List<OwnedChartRemoveRequest> removeRequests)
    {
        if (removeRequests == null || removeRequests.Count == 0)
        {
            return;
        }

        List<OwnedChartRemoveRequest> resolvedRequests;
        lock (lockStorageRowsVersion)
        {
            int bmsRowsVersion = bmsStorageRowsVersion;
            int bmsonRowsVersion = bmsonStorageRowsVersion;
            lock (lockOwnedChartCollection)
            {
                if (!catalogOwnedCollectionOwner.IsCurrent(bmsRowsVersion, bmsonRowsVersion))
                {
                    resolvedRequests = [.. removeRequests
                        .Where(request => request?.Mode == OwnedChartRemoveMode.OwnerReference
                            && (request.BmsOwner != null || request.BmsonOwner != null))];
                }
                else
                {
                    resolvedRequests = catalogOwnedCollectionOwner.Collection.ResolveCurrentRemoveRequests(removeRequests);
                }
            }
        }

        removeRequests.Clear();
        removeRequests.AddRange(resolvedRequests);
    }

    private static string CreateKindPathRemoveKey(ChartFileKind kind, string path)
    {
        string pathKey = CreateOwnedPathKey(path);
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return null;
        }
        return kind + ":" + pathKey;
    }

    private static bool HasBmsStorageRowCollectionChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation != null
            && (mutation.AddedBmsFiles.Count > 0
                || mutation.RemoveRequests.Any(request => request?.BmsOwner != null
                    || (request?.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bms)));
    }

    private static bool HasBmsonStorageRowCollectionChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation != null
            && (mutation.AddedBmsonSongs.Count > 0
                || mutation.RemoveRequests.Any(request => request?.BmsonOwner != null
                    || (request?.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bmson)));
    }

    private static bool HasBmsStorageRowPathChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation?.PathChanges.Any(change => change?.GetBmsStorageOwner() != null) == true;
    }

    private static bool HasBmsonStorageRowPathChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation?.PathChanges.Any(change => change?.GetBmsonStorageOwner() != null) == true;
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionUpsertMutationResult(
        ChartStorageTargetSet addedTargets,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        var result = new OwnedChartCollectionMutationResult();
        result.StorageMutation.AddAddedTargets(addedTargets);
        result.InstalledLookupMutation = BuildInstalledChartLookupUpsertMutation(result.StorageMutation);
        result.InstallEstimationMetadataProfileCacheInvalidated = result.StorageMutation.AddedCount > 0;
        result.AddedCount = result.StorageMutation.AddedCount;
        result.ParentFolderInvalidated = result.StorageMutation.AddedCount > 0;
        result.DuplicateCacheInvalidated = result.StorageMutation.AddedCount > 0;
        result.OwnedCollectionChanged = result.StorageMutation.HasChanges;
        result.WarningPresentationChanged = result.StorageMutation.HasChanges;
        result.BmsFilesStorageRowsChanged = result.StorageMutation.AddedBmsFiles.Count > 0;
        result.BmsonSongsStorageRowsChanged = result.StorageMutation.AddedBmsonSongs.Count > 0;
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = result.StorageMutation.AddedCount > 0;
        ConfigureResourceHealthMutationForStorageMutation(
            result,
            result.StorageMutation,
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion,
            resourceHealthIndexCurrentAtBase);
        return result;
    }

    private void ConfigureResourceHealthMutationForStorageMutation(
        OwnedChartCollectionMutationResult result,
        OwnedChartCollectionStorageMutation storageMutation,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        if (result == null || storageMutation?.HasChanges != true)
        {
            return;
        }
        bool resourceHealthIndexCurrent = resourceHealthIndexCurrentAtBase ?? resourceHealthOwner.IsCurrent();
        if (!resourceHealthIndexCurrent || storageMutation.PathChanges.Count > 0)
        {
            result.ResourceHealthIndexInvalidated = true;
            return;
        }

        if (storageMutation.RemoveRequests.Any(request => request?.Mode == OwnedChartRemoveMode.PathCleanup))
        {
            result.ResourceHealthIndexInvalidated = true;
            return;
        }

        result.ResourceHealthMutation.RemovedTargets.AddRange(CreateRemovedChartSnapshots(storageMutation));
        result.ResourceHealthMutation.UpdatedTargets.AddRange(storageMutation.AddedCharts.Where(chart => chart != null));
        result.ResourceHealthMutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion ?? resourceHealthOwner.CurrentInputVersion;
        result.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
        result.ResourceHealthMutation.InvalidateIfDeltaFails = true;
    }

    private static List<ChartFile> CreateRemovedChartSnapshots(OwnedChartCollectionStorageMutation storageMutation)
    {
        var charts = new List<ChartFile>();
        if (storageMutation == null)
        {
            return charts;
        }
        var bmsOwners = new HashSet<BMSFile>();
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>();
        var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in storageMutation.RemoveRequests
            .Select(request => request?.CreateChartSnapshot())
            .Where(chart => chart != null))
        {
            if (TryAddRemovedChartIdentity(chart, bmsOwners, bmsonOwners, pathKeys))
            {
                charts.Add(chart);
            }
        }
        return charts;
    }

    private static bool TryAddRemovedChartIdentity(
        ChartFile chart,
        ISet<BMSFile> bmsOwners,
        ISet<LR2SongDBExtended.bmson_song> bmsonOwners,
        ISet<string> pathKeys)
    {
        if (chart == null)
        {
            return false;
        }
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsOwners.Add(bmsOwner);
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return bmsonOwners.Add(bmsonOwner);
        }
        string pathKey = CreateKindPathRemoveKey(chart.Kind, chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathKeys.Add(pathKey);
    }

    private OwnedChartCollectionMutationResult CreateOwnedChartCollectionDigestMutationResult(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        bool resourceHealthIndexInvalidated = true)
    {
        List<LibraryChartDigestChange> changes = [.. (digestChanges ?? [])
            .Where(change => change?.HasDigestChange == true)];
        bool anyChanges = changes.Count > 0;
        bool primaryHashChanged = changes.Any(change => change.PrimaryHashChanged);
        bool md5Changed = changes.Any(change => change.Md5Changed);
        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupDigestMutation(changes),
            InstallEstimationMetadataProfileCacheInvalidated = anyChanges,
            DuplicateCacheInvalidated = primaryHashChanged,
            OwnedCollectionChanged = anyChanges,
            ResourceHealthIndexInvalidated = resourceHealthIndexInvalidated && md5Changed,
            WarningPresentationChanged = primaryHashChanged || (resourceHealthIndexInvalidated && md5Changed),
            BmsFilesStorageRowsChanged = changes.Any(change => change.Kind == LibraryChartKind.Bms),
            BmsonSongsStorageRowsChanged = changes.Any(change => change.Kind == LibraryChartKind.Bmson)
        };
        result.DigestChanges.AddRange(changes);
        return result;
    }

    private static OwnedChartCollectionMutationResult CreateOwnedChartCollectionPotentialDigestMutationResult(
        IEnumerable<ChartFile> charts,
        bool resourceHealthIndexInvalidated = true)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart != null)];
        if (targetCharts.Count == 0)
        {
            return new OwnedChartCollectionMutationResult();
        }

        return new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = new InstalledChartLookupMutation { RequiresFullInvalidate = true },
            InstallEstimationMetadataProfileCacheInvalidated = true,
            DuplicateCacheInvalidated = true,
            OwnedCollectionChanged = true,
            ResourceHealthIndexInvalidated = resourceHealthIndexInvalidated,
            WarningPresentationChanged = resourceHealthIndexInvalidated,
            BmsFilesStorageRowsChanged = targetCharts.Any(chart => chart.Kind == ChartFileKind.Bms),
            BmsonSongsStorageRowsChanged = targetCharts.Any(chart => chart.Kind == ChartFileKind.Bmson)
        };
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionMaintenanceMutationResult(
        ResourceHealthIndexMutationFacts resourceHealthMutation,
        bool workflowHasUpdates)
    {
        var result = new OwnedChartCollectionMutationResult();
        ResourceHealthIndexMutation mutation = resourceHealthMutation?.ToMutation() ?? new ResourceHealthIndexMutation();
        CopyResourceHealthIndexMutation(mutation, result.ResourceHealthMutation);
        bool resourceHealthChanged = result.ResourceHealthMutation.HasChanges;
        result.WarningPresentationChanged |= resourceHealthChanged;
        result.MaintenancePresentationChanged = workflowHasUpdates || resourceHealthChanged;
        return result;
    }

    private static void CopyResourceHealthIndexMutation(
        ResourceHealthIndexMutation source,
        ResourceHealthIndexMutation destination)
    {
        if (source == null || destination == null)
        {
            return;
        }
        destination.UpdatedTargets.AddRange(source.UpdatedTargets);
        destination.RemovedTargets.AddRange(source.RemovedTargets);
        destination.FullOwnedTargetSet = source.FullOwnedTargetSet;
        destination.DeltaBaseResourceHealthInputVersion = source.DeltaBaseResourceHealthInputVersion;
        destination.DeltaTargetResourceHealthInputVersion = source.DeltaTargetResourceHealthInputVersion;
        destination.Invalidate = source.Invalidate;
        destination.RebuildFull = source.RebuildFull;
        destination.Defer = source.Defer;
        destination.InvalidateIfDeltaFails = source.InvalidateIfDeltaFails;
    }

    private Action DispatchOwnedChartCollectionMutation(
        OwnedChartCollectionMutationResult result,
        string reason,
        bool publishNormalRefreshNotification = true,
        bool publishOwnedCollectionNotifications = true,
        bool deferDuplicateChartGroupsNotification = false)
    {
        if (result == null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        bool collectDispatchDetails = result.HasLoggableChanges;
        long installDestinationMs = 0;
        long digestMs = 0;
        long parentFolderMs = 0;
        long duplicateMs = 0;
        long playlistSummaryMs = 0;
        long ownedCollectionNotifyMs = 0;
        long resourceHealthMs = 0;
        long installMetadataMs = 0;
        long installedLookupMs = 0;
        long normalRefreshMs = 0;
        bool duplicateChartGroupsInvalidated = false;
        if (result.InstallDestinationRuntimeStateMutation.HasStateChanges
            && !result.InstallDestinationRuntimeStateApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.Apply(result.InstallDestinationRuntimeStateMutation);
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts
            && !result.InstallDestinationRuntimeStateApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DigestChangedCount > 0 && !result.DigestMutationApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (result.DigestMutationRequest != null)
            {
                catalogMutationOwner.ApplyDigestMutation(result.DigestMutationRequest);
            }
            digestMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.ParentFolderInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (publishOwnedCollectionNotifications)
            {
                InvalidateBMSParentFolderListCacheAndNotify();
            }
            else
            {
                InvalidateBMSParentFolderListCache();
            }
            parentFolderMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DuplicateCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            duplicateChartGroupsInvalidated = InvalidateDuplicateChartGroupsCache(
                publishNotification: !deferDuplicateChartGroupsNotification);
            duplicateMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (HasOwnedHashSetChanges(result) && !result.InstalledHashIndexInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            playlistSummaryMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.OwnedCollectionChanged)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (!result.PlaylistResolveIndexInvalidated)
            {
                InvalidatePlaylistLibraryResolveIndexSnapshot();
            }
            if (publishOwnedCollectionNotifications)
            {
                PublishOwnedCollectionChangeNotification(result);
            }
            ownedCollectionNotifyMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (HasOwnedHashSetChanges(result) && !result.InstalledHashIndexInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            playlistSummaryMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.OwnedCollectionChanged)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (!result.PlaylistResolveIndexInvalidated)
            {
                InvalidatePlaylistLibraryResolveIndexSnapshot(result.OwnedCollectionVersion);
            }
            ownedCollectionNotifyMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (result.ResourceHealthMutation.RebuildFull && result.OwnedCollectionChanged)
            {
                // collection 更新前の入力は再利用せず、実際に full rebuild する場合だけ取得し直す。
                result.ResourceHealthMutation.FullOwnedTargetSet = default;
            }
            result.ResourceHealthDispatchResult = DispatchResourceHealthIndexMutation(result.ResourceHealthMutation, reason);
            resourceHealthMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (!result.ResourceHealthMutation.HasChanges && result.OwnedCollectionChanged)
        {
            resourceHealthOwner.RebaseCurrentVersion(GetCurrentResourceHealthIndexVersion());
        }
        bool installMetadataProfileCacheInvalidated = result.InstallEstimationMetadataProfileCacheInvalidated || result.ShouldDispatchInstalledLookup;
        if (installMetadataProfileCacheInvalidated && !result.InstallMetadataCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidateInstallEstimationMetadataProfileCache();
            installMetadataMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.ShouldDispatchInstalledLookup && !result.InstalledLookupMutationApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            ApplyInstalledChartLookupMutation(result.InstalledLookupMutation, reason);
            installedLookupMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (publishNormalRefreshNotification)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            PublishNormalLibraryRefreshNotification(result);
            RaiseNormalLibraryRefreshNotificationVersionChanged(result);
            normalRefreshMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        stopwatch.Stop();

        if (result.HasLoggableChanges)
        {
            LogInstallPerformance("owned_collection_mutation_dispatch reason=" + (reason ?? "unknown")
                + " added=" + result.AddedCount
                + " removed=" + result.RemovedCount
                + " moved=" + result.MovedCount
                + " digestChanged=" + result.DigestChangedCount
                + " installDestinations=" + result.InstallDestinationChangedCount
                + " installedPackagePaths=" + result.InstalledPackagePathChangedCount
                + " installedLookup=" + ToMutationDispatchLogValue(result.InstalledLookupMutation)
                + " parentFolder=" + ToInvalidateLogValue(result.ParentFolderInvalidated)
                + " duplicate=" + ToInvalidateLogValue(result.DuplicateCacheInvalidated)
                + " catalogHashSet=" + ToInvalidateLogValue(HasOwnedHashSetChanges(result))
                + " playlistResolve=" + ToInvalidateLogValue(result.OwnedCollectionChanged)
                + " ownedCollection=" + ToInvalidateLogValue(result.OwnedCollectionChanged)
                + " resourceHealth=" + ToResourceHealthMutationDispatchLogValue(result.ResourceHealthMutation)
                + " installMetadata=" + ToInvalidateLogValue(installMetadataProfileCacheInvalidated)
                + " warningPresentation=" + ToInvalidateLogValue(result.WarningPresentationChanged)
                + " maintenancePresentation=" + ToInvalidateLogValue(result.MaintenancePresentationChanged)
                + " bmsStorageRows=" + ToInvalidateLogValue(result.BmsFilesStorageRowsChanged)
                + " bmsonStorageRows=" + ToInvalidateLogValue(result.BmsonSongsStorageRowsChanged)
                + " installDestinationMs=" + installDestinationMs
                + " digestMs=" + digestMs
                + " parentFolderMs=" + parentFolderMs
                + " duplicateMs=" + duplicateMs
                + " playlistSummaryMs=" + playlistSummaryMs
                + " ownedCollectionNotifyMs=" + ownedCollectionNotifyMs
                + " resourceHealthMs=" + resourceHealthMs
                + " installMetadataMs=" + installMetadataMs
                + " installedLookupMs=" + installedLookupMs
                + " normalRefreshMs=" + normalRefreshMs
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        return deferDuplicateChartGroupsNotification && duplicateChartGroupsInvalidated
            ? () => RaisePropertyChanged(() => DuplicateChartGroupsInvalidationVersion)
            : null;
    }

    private static Stopwatch StartPerformanceStepStopwatch(bool enabled)
    {
        return enabled ? Stopwatch.StartNew() : null;
    }

    private static long StopPerformanceStepStopwatch(Stopwatch stopwatch)
    {
        if (stopwatch == null)
        {
            return 0;
        }
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private void DispatchPreparedOwnedChartDigestChanges(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        string reason)
    {
        OwnedChartCollectionMutationResult mutationResult = CreateOwnedChartCollectionDigestMutationResult(
            digestChanges,
            resourceHealthIndexInvalidated: true);
        mutationResult.DigestMutationApplied = true;
        mutationResult.InstalledLookupMutationApplied = true;
        mutationResult.InstalledHashIndexInvalidated = true;
        mutationResult.PlaylistResolveIndexInvalidated = true;
        mutationResult.InstallMetadataCacheInvalidated = true;
        DispatchOwnedChartCollectionMutationWithResourceHealthLease(mutationResult, reason);
    }

    private void PrepareOwnedChartDigestIndexes(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        string reason)
    {
        OwnedChartCollectionMutationResult mutationResult = CreateOwnedChartCollectionDigestMutationResult(
            digestChanges);
        mutationResult.OwnedCollectionVersion = OwnedChartCollectionVersion;
        ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
            mutationResult,
            reason,
            LogInstallPerformance);
    }

    private void DispatchOwnedPotentialDigestChanges(
        IEnumerable<ChartFile> charts,
        string reason,
        bool resourceHealthIndexInvalidated = true)
    {
        DispatchOwnedChartCollectionMutationWithResourceHealthLease(
            CreateOwnedChartCollectionPotentialDigestMutationResult(charts, resourceHealthIndexInvalidated),
            reason);
    }

    private void DispatchOwnedChartCollectionMutationWithResourceHealthLease(
        OwnedChartCollectionMutationResult mutationResult,
        string reason)
    {
        if (IsOwnedDigestMutationWindowActive())
        {
            try
            {
                DispatchOwnedChartCollectionMutation(mutationResult, reason);
            }
            catch
            {
                resourceHealthOwner.ForceInvalidate((reason ?? "owned_chart_mutation") + "_failed");
                throw;
            }
            return;
        }

        if (mutationResult?.ResourceHealthMutation?.HasChanges != true)
        {
            DispatchOwnedChartCollectionMutation(mutationResult, reason);
            return;
        }

        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
        try
        {
            DispatchOwnedChartCollectionMutation(mutationResult, reason);
        }
        catch
        {
            resourceHealthOwner.ForceInvalidate((reason ?? "owned_chart_mutation") + "_failed");
            throw;
        }
        finally
        {
            resourceHealthMutation.Dispose();
        }
    }

    private void DispatchWarningPresentationChanged(string reason)
    {
        DispatchOwnedChartCollectionMutation(
            new OwnedChartCollectionMutationResult
            {
                WarningPresentationChanged = true
            },
            reason);
    }

    private void HandleCatalogChartInfoOwnerEvent(CatalogChartInfoOwnerEvent ownerEvent)
    {
        if (ownerEvent == null)
        {
            return;
        }
        switch (ownerEvent.Kind)
        {
            case CatalogChartInfoOwnerEventKind.DigestIndexesPrepared:
                PrepareOwnedChartDigestIndexes(ownerEvent.DigestChanges, ownerEvent.Reason);
                break;
            case CatalogChartInfoOwnerEventKind.DigestChanges:
                DispatchPreparedOwnedChartDigestChanges(ownerEvent.DigestChanges, ownerEvent.Reason);
                break;
            case CatalogChartInfoOwnerEventKind.PotentialDigestChanges:
                DispatchOwnedPotentialDigestChanges(ownerEvent.PotentialDigestCharts, ownerEvent.Reason);
                break;
            case CatalogChartInfoOwnerEventKind.WarningPresentationChanged:
                DispatchWarningPresentationChanged(ownerEvent.Reason);
                break;
            case CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint:
                LogStartupMemoryCheckpoint(ownerEvent.CheckpointStage, ownerEvent.CheckpointStatus);
                break;
            case CatalogChartInfoOwnerEventKind.IndexChanged:
                DispatchWarningPresentationChanged(ownerEvent.Reason);
                break;
        }
    }

    private void PublishOwnedCollectionChangeNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null)
        {
            return;
        }
        if (!result.OwnedCollectionChanged)
        {
            result.OwnedCollectionVersion = OwnedChartCollectionVersion;
            return;
        }
        if (result.OwnedCollectionChangeNotified)
        {
            return;
        }
        result.OwnedCollectionVersion = result.OwnedCollectionVersionAlreadyAdvanced
            ? NotifyOwnedChartCollectionChanged(result.OwnedCollectionVersion)
            : NotifyOwnedChartCollectionChanged();
        result.OwnedCollectionChangeNotified = true;
    }

    private static string ToMutationDispatchLogValue(InstalledChartLookupMutation mutation)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return "none";
        }
        return mutation.RequiresFullInvalidate ? "invalidate" : "delta";
    }

    private static string ToResourceHealthMutationDispatchLogValue(ResourceHealthIndexMutation mutation)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return "none";
        }
        if (mutation.Invalidate)
        {
            return "invalidate";
        }
        if (mutation.Defer)
        {
            return "defer";
        }
        if (mutation.RebuildFull)
        {
            return "full";
        }
        return mutation.HasDeltaTargets ? "delta" : "none";
    }

    private static string ToInvalidateLogValue(bool invalidated)
    {
        return invalidated ? "invalidate" : "none";
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupMutation(
        OwnedChartCollectionStorageMutation storageMutation,
        IReadOnlyCollection<LibraryFolderPathChange> folderPathChanges)
    {
        var mutation = new InstalledChartLookupMutation();
        if (storageMutation == null)
        {
            return mutation;
        }
        foreach (ChartFile chart in CreateRemovedChartSnapshots(storageMutation))
        {
            if (string.IsNullOrWhiteSpace(chart.Md5))
            {
                mutation.RequiresFullInvalidate = true;
                continue;
            }
            mutation.Removed.Add(CreateInstalledChartLookupMutationEntry(chart));
        }
        foreach (LibraryChartPathChange pathChange in storageMutation.PathChanges)
        {
            ChartFile chart = pathChange.Chart;
            string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath) ? chart.Path : pathChange.OldPath;
            string newPath = pathChange.NewPath;
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            {
                mutation.RequiresFullInvalidate = true;
                continue;
            }
            mutation.Moved.Add(new InstalledChartLookupPathMutationEntry(oldPath, newPath, chart.Md5, chart.Sha256));
        }
        if ((folderPathChanges?.Count ?? 0) > 0 && storageMutation.PathChanges.Count == 0)
        {
            mutation.RequiresFullInvalidate = true;
        }
        return mutation;
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupUpsertMutation(OwnedChartCollectionStorageMutation storageMutation)
    {
        var mutation = new InstalledChartLookupMutation();
        bool fullLookupInitialized = IsInstalledChartLookupIndexInitializedUnsafe();
        bool primaryLookupInitialized = IsInstalledPrimaryHashLookupInitializedUnsafe();
        if (storageMutation?.AddedCount > 0 != true || (!fullLookupInitialized && !primaryLookupInitialized))
        {
            return mutation;
        }

        List<ChartFile> addedChartList = [.. storageMutation.AddedCharts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        var addedBmsPaths = new HashSet<string>(
            addedChartList.Where(chart => chart.Kind == ChartFileKind.Bms).Select(chart => CreateOwnedPathKey(chart.Path)).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var addedBmsonPaths = new HashSet<string>(
            addedChartList.Where(chart => chart.Kind == ChartFileKind.Bmson).Select(chart => CreateOwnedPathKey(chart.Path)).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var addedPaths = new HashSet<string>(addedBmsPaths, StringComparer.OrdinalIgnoreCase);
        addedPaths.UnionWith(addedBmsonPaths);
        if (addedPaths.Count > 0)
        {
            bool existingRefsAvailable = fullLookupInitialized
                ? TryCreateOwnedChartRefsForPathsUnsafe(addedPaths, out List<LibraryChartRef> existingRefs)
                : TryScanOwnedChartRefsForPathsUnsafe(addedPaths, out existingRefs);
            if (!existingRefsAvailable)
            {
                mutation.RequiresFullInvalidate = true;
                return mutation;
            }
            foreach (LibraryChartRef existingRef in existingRefs)
            {
                if (existingRef == null || string.IsNullOrWhiteSpace(existingRef.Path))
                {
                    continue;
                }
                string existingPathKey = CreateOwnedPathKey(existingRef.Path);
                if (existingRef.Kind == LibraryChartKind.Bms && addedBmsPaths.Contains(existingPathKey)
                    || existingRef.Kind == LibraryChartKind.Bmson && addedBmsonPaths.Contains(existingPathKey))
                {
                    mutation.Removed.Add(new InstalledChartLookupMutationEntry(existingRef.Path, existingRef.Md5, existingRef.Sha256));
                }
            }
        }
        foreach (ChartFile chart in addedChartList)
        {
            mutation.Added.Add(new InstalledChartLookupMutationEntry(chart.Path, chart.Md5, chart.Sha256));
        }
        return mutation;
    }

    private bool IsInstalledChartLookupIndexInitializedUnsafe()
    {
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndexInitialized;
        }
    }

    private bool IsInstalledPrimaryHashLookupInitializedUnsafe()
    {
        lock (lockInstalledPrimaryHashLookup)
        {
            return installedPrimaryHashLookupInitialized;
        }
    }

    /// <summary>
    /// Gets whether the installed chart directory lookup is initialized without forcing a build.
    /// </summary>
    internal bool IsInstalledChartLookupIndexInitializedForDiagnostics()
    {
        return IsInstalledChartLookupIndexInitializedUnsafe();
    }

    /// <summary>
    /// Gets whether the installed primary hash lookup is initialized without forcing a build.
    /// </summary>
    internal bool IsInstalledPrimaryHashLookupInitializedForDiagnostics()
    {
        return IsInstalledPrimaryHashLookupInitializedUnsafe();
    }

    private static InstalledChartLookupMutationEntry CreateInstalledChartLookupMutationEntry(ChartFile chart)
    {
        return new InstalledChartLookupMutationEntry(chart?.Path, chart?.Md5, chart?.Sha256);
    }

    private static InstalledChartLookupMutation BuildInstalledChartLookupDigestMutation(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        var mutation = new InstalledChartLookupMutation();
        foreach (LibraryChartDigestChange digestChange in digestChanges ?? [])
        {
            if (digestChange?.HasDigestChange != true)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(digestChange.Path))
            {
                throw new InvalidOperationException("Owned chart digest changes require a current owner path.");
            }
            mutation.Removed.Add(new InstalledChartLookupMutationEntry(digestChange.Path, digestChange.OldMd5, digestChange.OldSha256));
            mutation.Added.Add(new InstalledChartLookupMutationEntry(digestChange.Path, digestChange.NewMd5, digestChange.NewSha256));
        }
        return mutation;
    }

    private void ApplyInstalledChartLookupMutation(
        InstalledChartLookupMutation mutation,
        string reason,
        Action<string> logOverride = null)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return;
        }
        var logMessages = new List<string>();
        lock (pendingInstallEstimateCurrentnessGate)
        {
            ApplyInstalledPrimaryHashLookupMutation(mutation, reason, logMessages.Add);
            lock (lockInstalledChartLookupIndex)
            {
                if (installedChartLookupIndexInitialized)
                {
                    if (mutation.RequiresFullInvalidate)
                    {
                        installedChartLookupIndex = new InstalledChartLookupIndexState();
                        installedChartLookupIndexInitialized = false;
                        logMessages.Add("installed_chart_lookup_index update mode=full_invalidate reason=" + reason);
                    }
                    else
                    {
                        var stopwatch = Stopwatch.StartNew();
                        foreach (InstalledChartLookupMutationEntry entry in mutation.Removed)
                        {
                            installedChartLookupIndex.RemoveChart(entry.Path, entry.Md5, entry.Sha256);
                        }
                        foreach (InstalledChartLookupPathMutationEntry entry in mutation.Moved)
                        {
                            installedChartLookupIndex.MoveChart(entry.OldPath, entry.NewPath, entry.Md5, entry.Sha256);
                        }
                        foreach (InstalledChartLookupMutationEntry entry in mutation.Added)
                        {
                            installedChartLookupIndex.AddChart(entry.Path, entry.Md5, entry.Sha256);
                        }
                        stopwatch.Stop();
                        logMessages.Add("installed_chart_lookup_index update mode=incremental reason=" + reason + " removed=" + mutation.Removed.Count + " moved=" + mutation.Moved.Count + " added=" + mutation.Added.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + installedChartLookupIndex.HashCount + " primaryHashes=" + installedChartLookupIndex.DistinctPrimaryHashCount + " dirRefs=" + installedChartLookupIndex.DirectoryReferenceCount);
                    }
                }
                installedChartLookupGeneration++;
            }
        }
        foreach (string logMessage in logMessages)
        {
            (logOverride ?? LogInstallPerformance)(logMessage);
        }
    }

    private void ApplyInstalledPrimaryHashLookupMutation(
        InstalledChartLookupMutation mutation,
        string reason,
        Action<string> logOverride = null)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return;
        }
        lock (lockInstalledPrimaryHashLookup)
        {
            if (!installedPrimaryHashLookupInitialized)
            {
                return;
            }
            if (mutation.RequiresFullInvalidate)
            {
                installedPrimaryHashLookup = new PrimaryHashLookupState();
                installedPrimaryHashLookupInitialized = false;
                (logOverride ?? LogInstallPerformance)("installed_primary_hash_lookup update mode=full_invalidate reason=" + reason);
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            foreach (InstalledChartLookupMutationEntry entry in mutation.Removed)
            {
                installedPrimaryHashLookup.RemovePrimaryHash(entry.Md5);
            }
            foreach (InstalledChartLookupMutationEntry entry in mutation.Added)
            {
                installedPrimaryHashLookup.AddPrimaryHash(entry.Md5);
            }
            stopwatch.Stop();
            (logOverride ?? LogInstallPerformance)("installed_primary_hash_lookup update mode=incremental reason=" + reason + " removed=" + mutation.Removed.Count + " moved=" + mutation.Moved.Count + " added=" + mutation.Added.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount);
        }
    }

    private void RebuildInstalledChartLookupIndexCoreUnsafe(InstalledChartLookupIndexState state)
    {
        installedChartLookupIndex = state ?? new InstalledChartLookupIndexState();
        installedChartLookupIndexInitialized = true;
    }

    /// <summary>
    /// インストール済み chart lookup index が未構築の場合にビルドします。
    /// </summary>
    private void EnsureInstalledChartLookupIndexBuiltUnsafe()
    {
        lock (lockInstalledChartLookupIndex)
        {
            if (installedChartLookupIndexInitialized)
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            InstalledChartLookupIndexState state = CreateOwnedInstalledChartLookupIndexStateUnsafe(out int bmsCount, out int bmsonCount);
            RebuildInstalledChartLookupIndexCoreUnsafe(state);
            stopwatch.Stop();
            LogInstallPerformance("installed_chart_lookup_index build mode=full buildMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + state.HashCount + " primaryHashes=" + state.DistinctPrimaryHashCount + " dirRefs=" + state.DirectoryReferenceCount + " files=" + bmsCount + " bmson=" + bmsonCount + " rows=" + (bmsCount + bmsonCount) + " source=owned_collection_lightweight singleFlight=true");
        }
    }

    private bool EnsureInstalledPrimaryHashLookupBuiltUnsafe(
        out long buildMs,
        out int bmsCount,
        out int bmsonCount,
        Action<string> logOverride = null)
    {
        lock (lockInstalledPrimaryHashLookup)
        {
            if (installedPrimaryHashLookupInitialized)
            {
                buildMs = 0L;
                bmsCount = 0;
                bmsonCount = 0;
                return false;
            }
            var stopwatch = Stopwatch.StartNew();
            PrimaryHashLookupState state = CreateOwnedInstalledPrimaryHashLookupStateUnsafe(out bmsCount, out bmsonCount);
            installedPrimaryHashLookup = state ?? new PrimaryHashLookupState();
            installedPrimaryHashLookupInitialized = true;
            stopwatch.Stop();
            buildMs = stopwatch.ElapsedMilliseconds;
            (logOverride ?? LogInstallPerformance)("installed_primary_hash_lookup build mode=full buildMs=" + buildMs + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount + " files=" + bmsCount + " bmson=" + bmsonCount + " rows=" + (bmsCount + bmsonCount) + " source=owned_collection_primary singleFlight=true");
            return true;
        }
    }

    /// <summary>
    /// 現在のインストール済み chart lookup index のスナップショットを取得します。
    /// </summary>
    private InstalledChartLookupIndexSnapshot CreateInstalledChartLookupSnapshotUnsafe()
    {
        return CreateInstalledChartLookupVersionedSnapshotUnsafe().Snapshot;
    }

    private (InstalledChartLookupIndexSnapshot Snapshot, long Generation)
        CreateInstalledChartLookupVersionedSnapshotUnsafe()
    {
        lock (pendingInstallEstimateCurrentnessGate)
        {
            return CreateInstalledChartLookupVersionedSnapshotUnderCurrentnessGateUnsafe();
        }
    }

    private (InstalledChartLookupIndexSnapshot Snapshot, long Generation)
        CreateInstalledChartLookupVersionedSnapshotUnderCurrentnessGateUnsafe()
    {
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            return (
                installedChartLookupIndex.CreateSnapshot(),
                installedChartLookupGeneration);
        }
    }

    /// <summary>
    /// Creates a read-only installed chart lookup snapshot for diagnostics and tests.
    /// </summary>
    /// <returns>The installed chart lookup snapshot.</returns>
    internal InstalledChartLookupIndexSnapshot CreateInstalledChartLookupSnapshotForDiagnostics()
    {
        return CreateInstalledChartLookupSnapshotUnsafe();
    }

    private bool ContainsInstalledChartUnsafe(ChartFile chart)
    {
        string lookupKey = ChartLookupKey.GetPrimaryHash(chart);
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return false;
        }
        EnsureInstalledPrimaryHashLookupBuiltUnsafe(out _, out _, out _);
        lock (lockInstalledPrimaryHashLookup)
        {
            return installedPrimaryHashLookup.ContainsPrimaryHash(lookupKey);
        }
    }

    private HashSet<string> CreateKnownChartDirectorySnapshotUnsafe()
    {
        LibraryResourceIndexSnapshot resourceIndexSnapshot = libraryResourceIndexOwner.CaptureSnapshot();
        var knownChartDirectories = new HashSet<string>((resourceIndexSnapshot.DirectoryLookupCache?.Keys ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        foreach (string directory in CreateInstalledChartKnownDirectorySnapshotUnsafe())
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                knownChartDirectories.Add(directory);
            }
        }
        return knownChartDirectories;
    }

    private IReadOnlyCollection<string> CreateInstalledChartKnownDirectorySnapshotUnsafe()
    {
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndex.CreateKnownChartDirectorySnapshot();
        }
    }

    private List<string> GetDistinctInstalledDirectoriesForChartUnsafe(ChartFile chart)
    {
        return GetDistinctInstalledDirectoriesByPrimaryHashUnsafe(ChartLookupKey.GetPrimaryHash(chart));
    }

    private List<string> GetDistinctInstalledDirectoriesByPrimaryHashUnsafe(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            return [.. installedChartLookupIndex.GetDistinctDirectoriesByPrimaryHash(lookupHash)];
        }
    }

    private List<string> GetInstalledDirectChildPathsByPrimaryHashesUnsafe(
        IEnumerable<string> primaryHashes,
        string destinationDirectory)
    {
        var hashes = new HashSet<string>(
            (primaryHashes ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
        string destinationDirectoryKey = CreateDirectChildDirectoryComparisonKey(destinationDirectory);
        if (hashes.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectoryKey))
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            foreach (string hash in hashes)
            {
                foreach (string path in installedChartLookupIndex.GetPathsByPrimaryHash(hash))
                {
                    if (IsDirectChildPathOfDirectory(path, destinationDirectoryKey))
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        return [.. paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsDirectChildPathOfDirectory(string path, string destinationDirectoryKey)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(destinationDirectoryKey))
        {
            return false;
        }

        string directory;
        try
        {
            directory = DirectoryExt.GetDirectoryNameSimple(path);
        }
        catch
        {
            return false;
        }
        string directoryKey = CreateDirectChildDirectoryComparisonKey(directory);
        return !string.IsNullOrWhiteSpace(directoryKey)
            && string.Equals(directoryKey, destinationDirectoryKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateDirectChildDirectoryComparisonKey(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(directory.Trim()));
        }
        catch
        {
            return directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(IEnumerable<ChartFile> excluded)
    {
        return CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, null, 0L);
    }

    /// <summary>
    /// Creates a read-only installed primary hash lookup snapshot while excluding the supplied charts.
    /// </summary>
    /// <param name="excluded">Charts to exclude from the returned lookup.</param>
    /// <returns>The primary hash lookup snapshot.</returns>
    internal IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsForDiagnostics(IEnumerable<ChartFile> excluded)
    {
        return CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded);
    }

    private IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
        IEnumerable<ChartFile> excluded,
        string reason,
        long operationId)
    {
        return CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
            excluded,
            reason,
            operationId,
            null);
    }

    private IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
        IEnumerable<ChartFile> excluded,
        string reason,
        long operationId,
        Action<string> logOverride)
    {
        var stopwatch = Stopwatch.StartNew();
        var excludedKeyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int excludedChartCount = 0;
        if (excluded != null)
        {
            foreach (ChartFile item in excluded.Where(chart => chart != null))
            {
                excludedChartCount++;
                string key = ChartLookupKey.GetPrimaryHash(item);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    excludedKeyCount[key] = excludedKeyCount.TryGetValue(key, out int value) ? value + 1 : 1;
                }
            }
        }
        bool coldBuild = EnsureInstalledPrimaryHashLookupBuiltUnsafe(
            out long buildMs,
            out int bmsCount,
            out int bmsonCount,
            logOverride);
        IPrimaryHashLookup result;
        lock (lockInstalledPrimaryHashLookup)
        {
            result = installedPrimaryHashLookup.CreateExcludingLookup(excludedKeyCount);
        }
        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            (logOverride ?? LogInstallPerformance)("installed_primary_hash_lookup excluding_snapshot reason=" + reason
                + " op=" + operationId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " coldBuild=" + coldBuild
                + " buildMs=" + buildMs
                + " excludedCharts=" + excludedChartCount
                + " excludedHashes=" + excludedKeyCount.Count
                + " primaryHashes=" + result.DistinctPrimaryHashCount
                + " files=" + bmsCount
                + " bmson=" + bmsonCount);
        }
        return result;
    }

    /// <summary>
    /// 指定された BMS ファイル群に対して、LR2 score.db からスコア情報を取得・反映します。
    /// </summary>
    public void SetBMSScore(IEnumerable<BMSFile> bmsFiles)
    {
        SetBMSScoreInternal(bmsFiles, collectMetrics: false);
    }

    /// <summary>
    /// 指定された BMS ファイル群に対して、LR2 score.db の score 反映メトリクスを取得します。
    /// </summary>
    /// <param name="bmsFiles">score 反映対象。</param>
    /// <returns>score 反映時の待機・適用メトリクス。</returns>
    internal BmsScoreApplyMetrics SetBMSScoreWithMetrics(IReadOnlyCollection<BMSFile> bmsFiles)
    {
        return SetBMSScoreInternal(bmsFiles, collectMetrics: true);
    }

    /// <summary>
    /// score 反映と必要に応じたメトリクス収集を行います。
    /// </summary>
    /// <param name="bmsFiles">score 反映対象。</param>
    /// <param name="collectMetrics">メトリクスを収集するかどうか。</param>
    /// <returns>score 反映時のメトリクス。</returns>
    private BmsScoreApplyMetrics SetBMSScoreInternal(IEnumerable<BMSFile> bmsFiles, bool collectMetrics)
    {
        BmsScoreApplyMetrics metrics = default;
        if (bmsFiles == null)
        {
            return metrics;
        }
        IEnumerable<BMSFile> effectiveFiles = bmsFiles;
        if (collectMetrics)
        {
            if (bmsFiles is IReadOnlyCollection<BMSFile> readOnlyCollection)
            {
                metrics.TargetCount = readOnlyCollection.Count;
            }
            else if (bmsFiles is ICollection<BMSFile> collection)
            {
                metrics.TargetCount = collection.Count;
            }
            else
            {
                List<BMSFile> normalizedFiles = [.. bmsFiles.Where(file => file != null)];
                effectiveFiles = normalizedFiles;
                metrics.TargetCount = normalizedFiles.Count;
            }
        }
        Stopwatch totalStopwatch = collectMetrics ? Stopwatch.StartNew() : null;
        Stopwatch stageStopwatch = collectMetrics ? Stopwatch.StartNew() : null;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            if (collectMetrics)
            {
                metrics.WaitInitializedMinMs = stageStopwatch.ElapsedMilliseconds;
                stageStopwatch.Restart();
            }
            if (collectMetrics)
            {
                metrics.WaitBmsFilesReadMs = stageStopwatch.ElapsedMilliseconds;
                stageStopwatch.Restart();
            }
            ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
            if (collectMetrics)
            {
                metrics.WaitScoreSnapshotReadMs = stageStopwatch.ElapsedMilliseconds;
                metrics.WaitScoresWriteMs = 0L;
            }
            if (snapshot == null)
            {
                if (collectMetrics)
                {
                    metrics.TotalMs = totalStopwatch.ElapsedMilliseconds;
                }
                return metrics;
            }
            if (collectMetrics)
            {
                stageStopwatch.Restart();
                metrics.MatchedScoreCount = ApplyScoreSnapshotToFilesReplacingExisting(effectiveFiles, snapshot);
                metrics.ApplyKnownScoresMs = stageStopwatch.ElapsedMilliseconds;
                metrics.TotalMs = totalStopwatch.ElapsedMilliseconds;
            }
            else
            {
                ApplyScoreSnapshotToFilesReplacingExisting(effectiveFiles, snapshot);
            }
        }
        return metrics;
    }

    private LR2IRCache getIRCache(string filePath)
    {
        return irService.LoadIrCache(filePath);
    }

    private void setBMSScore(LR2IRData data, LR2IRCache cache)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        using (rwlockBMSScores.GetWriterGuard())
        {
            List<BMSFile> bmsFilesSnapshot;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                bmsFilesSnapshot = ((BMSFiles == null) ? new List<BMSFile>() : [.. BMSFiles.Where(f => f != null)]);
            }
            irService.ApplyIrDataToScoresAndFiles(data, cache, lr2ScoreDBPath, BMSScores, bmsFilesSnapshot, options.EstimateOfflineScoreRanking);
        }
        RefreshScoreSnapshotFromCurrentScores("apply_ir_data");
    }

    private List<LR2IRScore> updateLR2IRScoreTable()
    {
        return updateLR2IRScoreTableWithMetrics().ScoreTable;
    }

    private IrScoreTableUpdateResult updateLR2IRScoreTableWithMetrics()
    {
        return updateLR2IRScoreTableWithMetrics(null);
    }

    private IrScoreTableUpdateResult updateLR2IRScoreTableWithMetrics(IrScorePrefetchResult prefetchedScore)
    {
        return updateLR2IRScoreTableWithMetrics(prefetchedScore, LR2ID);
    }

    private IrScoreTableUpdateResult updateLR2IRScoreTableWithMetrics(
        IrScorePrefetchResult prefetchedScore,
        int lr2Id)
    {
        return irService.UpdateIrScoreTableWithMetrics(
            lr2Id,
            dbGateway,
            irClient,
            lr2IRScoreRegex,
            prefetchedScore,
            irScoreShutdownCancellation.Token);
    }

    private void updateBMSScores(List<LR2IRScore> scoreTable)
    {
        updateBMSScores(
            scoreTable,
            detectUnsentScores: true,
            CaptureRankingDownloadContext());
    }

    private void updateBMSScores(
        List<LR2IRScore> scoreTable,
        bool detectUnsentScores,
        RankingDownloadContext context)
    {
        if (scoreTable == null)
        {
            return;
        }
        List<BMSFile> filesSnapshot;
        List<BMSScore> mergedScores;
        var priorUnsentByScore = new Dictionary<BMSScore, bool>();
        var priorScoreByFile = new Dictionary<BMSFile, BMSScore>();
        using (rwlockBMSScores.GetWriterGuard())
        {
            EnsureCurrentRankingDownloadContext(context);
            using (rwlockBMSFiles.GetReaderGuard())
            {
                filesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
                foreach (BMSScore score in BMSScores ?? [])
                {
                    if (score != null)
                    {
                        priorUnsentByScore[score] = score.IsLr2IrScoreUnsent;
                    }
                }
                foreach (BMSFile file in filesSnapshot)
                {
                    priorScoreByFile[file] = file.bmsScore;
                }
                using (BMSScore.SuppressPropertyChangedScope())
                using (BMSFile.SuppressPropertyChangedScope())
                {
                    mergedScores = irService.UpdateBmsScores(
                        scoreTable,
                        BMSScores,
                        filesSnapshot,
                        detectUnsentScores);
                    BMSScores = mergedScores;
                }
            }
        }
        List<BMSScore> changedUnsentScores =
        [
            .. mergedScores.Where(score =>
                score != null
                && priorUnsentByScore.TryGetValue(score, out bool priorUnsent)
                && priorUnsent != score.IsLr2IrScoreUnsent)
        ];
        List<BMSFile> changedScoreAttachments =
        [
            .. filesSnapshot.Where(file =>
                priorScoreByFile.TryGetValue(file, out BMSScore priorScore)
                && !ReferenceEquals(priorScore, file.bmsScore))
        ];
        uiScheduler.Invoke(() =>
        {
            foreach (BMSFile file in changedScoreAttachments)
            {
                file.PublishScoreAttachmentChanged();
            }
            foreach (BMSScore score in changedUnsentScores)
            {
                score.PublishLr2IrScoreUnsentChanged();
            }
            RefreshScoreSnapshotFromCurrentScores("update_ir_score_table");
        });
    }

    private void ClearScoreUnsentStatus()
    {
        using (rwlockBMSScores.GetWriterGuard())
        {
            foreach (BMSScore score in BMSScores ?? Enumerable.Empty<BMSScore>())
            {
                if (score != null)
                {
                    score.IsLr2IrScoreUnsent = false;
                }
            }
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (BMSFile file in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                if (file?.bmsScore != null)
                {
                    file.bmsScore.IsLr2IrScoreUnsent = false;
                }
            }
        }
    }

    private IrCacheRefreshResult setRankingScore(RankingDownloadContext context)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        LogInstallPerformance("ranking_cache_refresh start lr2Id=" + context.Lr2Id);
        var stopwatch = Stopwatch.StartNew();
        BmsLibraryIrService.IrCacheRefreshPlan preparedPlan =
            irService.PrepareRankingScoresRefreshPlanForLibrary(
                context.Lr2Id,
                context.ScoreDbPath,
                dbGateway,
                options.EstimateOfflineScoreRanking);
        IrCacheRefreshResult result;
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            using (rwlockBMSScores.GetWriterGuard())
            {
                EnsureCurrentRankingDownloadContext(context);
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    using (BMSScore.SuppressPropertyChangedScope())
                    using (BMSFile.SuppressPropertyChangedScope())
                    {
                        result = irService.ApplyPreparedRankingScoresRefreshPlanForLibrary(
                            preparedPlan,
                            context.ScoreDbPath,
                            BMSScores,
                            BMSFiles,
                            options.EstimateOfflineScoreRanking);
                    }
                }
            }
        }
        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        LogInstallPerformance("ranking_cache_refresh done elapsedMs=" + result.ElapsedMs
            + " dbReadMs=" + result.DbReadMs
            + " irDataDbReadMs=" + result.IrDataDbReadMs
            + " irDataMaterializeMs=" + result.IrDataMaterializeMs
            + " irDataDbLockWaitMs=" + result.IrDataDbLockWaitMs
            + " dbRows=" + result.DbRows
            + " indexBuildMs=" + result.IndexBuildMs
            + " cacheFiles=" + result.CacheFilesScanned
            + " xmlCheckMs=" + result.XmlCheckMs
            + " reloadTargets=" + result.CacheFilesReloaded
            + " xmlReloadMs=" + result.XmlReloadMs
            + " xmlReloadDegree=" + result.XmlReloadDegree
            + " xmlScoresParsed=" + result.XmlScoresParsed
            + " xmlParseFailed=" + result.XmlParseFailedCount
            + " xmlFallbackLoads=" + result.XmlFallbackLoadCount
            + " dbApplyCount=" + result.DbFallbackAppliedCount
            + " xmlApplyCount=" + result.XmlAppliedCount
            + " upsertRows=" + result.IrDataUpsertCount
            + " upsertMs=" + result.UpsertMs
            + " bulkInsertUsed=" + result.BulkInsertUsed
            + " offlineEstimateXmlLoads=" + result.OfflineEstimateXmlLoadCount);
        uiScheduler.Invoke(() =>
        {
            foreach (BMSScore changedScore in preparedPlan.ChangedScores)
            {
                changedScore.PublishRankingDataChanged();
            }
            RefreshScoreSnapshotFromCurrentScores("refresh_ranking_cache");
        });
        return result;
    }

    public List<IRDataCacheInfo> GetIRDataNeedUpdates(IEnumerable<string> md5s)
    {
        RankingDownloadContext context = CaptureRankingDownloadContext();
        List<IRDataCacheInfo> rankingInfo = irClient.GetRankingInfo(rankingInfoUrl, md5s);
        using (rwlockLR2IrDir.GetReaderGuard())
        {
            using (rwlockBMSScores.GetReaderGuard())
            {
                EnsureCurrentRankingDownloadContext(context);
                return irService.GetIRDataNeedUpdatesFromRankingInfo(
                    context.Lr2Id,
                    rankingInfo,
                    dbGateway);
            }
        }
    }

    public List<IRDataCacheInfo> DownloadIRData(IEnumerable<IRDataCacheInfo> cacheInfo)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        RankingDownloadContext context = CaptureRankingDownloadContext();
        if (cacheInfo == null)
        {
            throw new ArgumentNullException("cacheInfo");
        }
        string irCacheDirPath = Path.Combine(
            Path.GetDirectoryName(context.ScoreDbPath),
            "..\\..\\Ir");
        if (!LongPathFileSystem.DirectoryExists(irCacheDirPath))
        {
            throw new DirectoryNotFoundException(string.Format(Resources.Error_IRCacheDirNotFound, irCacheDirPath));
        }
        string stagingDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_RankingCache_" + Guid.NewGuid().ToString("N"));
        try
        {
            BmsLibraryIrService.RankingCacheDownloadBatch downloadBatch =
                irService.DownloadRankingCacheFiles(
                    cacheInfo,
                    stagingDirectoryPath,
                    irClient,
                    rankingDataUrl);
            BmsLibraryIrService.RankingCacheApplyPlan applyPlan =
                irService.PrepareDownloadedRankingCache(
                    context.Lr2Id,
                    downloadBatch);
            BmsLibraryIrService.RankingCacheApplyResult applyResult;
            using (rwlockLR2IrDir.GetWriterGuard())
            {
                using (rwlockBMSScores.GetWriterGuard())
                {
                    EnsureCurrentRankingDownloadContext(context);
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        irService.PromoteDownloadedRankingCache(
                            applyPlan,
                            irCacheDirPath);
                        using (BMSScore.SuppressPropertyChangedScope())
                        using (BMSFile.SuppressPropertyChangedScope())
                        {
                            applyResult = irService.ApplyPreparedDownloadedRankingCache(
                                applyPlan,
                                dbGateway,
                                BMSScores,
                                BMSFiles,
                                options.EstimateOfflineScoreRanking);
                        }
                    }
                }
            }
            uiScheduler.Invoke(() =>
            {
                List<Exception> publicationFailures = null;
                foreach (BMSScore changedScore in applyResult.ChangedScores)
                {
                    try
                    {
                        changedScore.PublishRankingDataChanged();
                    }
                    catch (Exception ex)
                    {
                        publicationFailures ??= [];
                        publicationFailures.Add(ex);
                    }
                }
                RefreshScoreSnapshotFromCurrentScores("download_ir_data");
                if (publicationFailures?.Count > 0)
                {
                    throw new AggregateException(
                        "One or more ranking score subscribers failed.",
                        publicationFailures);
                }
            });
            return applyResult.Failed;
        }
        finally
        {
            try
            {
                if (LongPathFileSystem.DirectoryExists(stagingDirectoryPath))
                {
                    LongPathFileSystem.DeleteDirectory(stagingDirectoryPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                LogInstallPerformanceWarn(
                    "ranking_cache_download staging_cleanup_failed path=\""
                    + stagingDirectoryPath
                    + "\" exception="
                    + ex.GetType().Name);
            }
        }
    }

    private RankingDownloadContext CaptureRankingDownloadContext()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            if (activeScoreSource != ActiveScoreSource.Lr2
                || lr2ScoreDBPath == null
                || LR2ID == 0)
            {
                throw new InvalidOperationException(Resources.Error_LR2ScoreDBNotConnected);
            }
            return new RankingDownloadContext
            {
                ScoreSourceGeneration = scoreSourceGeneration,
                Lr2Id = LR2ID,
                ScoreDbPath = lr2ScoreDBPath
            };
        }
    }

    private void EnsureCurrentRankingDownloadContext(RankingDownloadContext context)
    {
        if (context == null
            || activeScoreSource != ActiveScoreSource.Lr2
            || scoreSourceGeneration != context.ScoreSourceGeneration
            || LR2ID != context.Lr2Id
            || !string.Equals(
                lr2ScoreDBPath,
                context.ScoreDbPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The LR2 score source changed while ranking cache data was being downloaded.");
        }
    }

    private static List<ChartFile> CreateBmsChartSubsetSnapshot(IEnumerable<BMSFile> bmsFiles)
    {
        return ChartFileProjection.FromBmsFiles(
            (bmsFiles ?? []).Where(ChartFileKindResolver.IsBmsChartFile),
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);
    }

    private static List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(chart => chart != null)];
    }

    private static ResourceMaintenanceTargetSet CreateResourceMaintenanceTargetSet(IEnumerable<ChartFile> charts)
    {
        return ResourceMaintenanceTargetSet.ForSubset(NormalizeResourceMaintenanceTargetCharts(charts));
    }

    private static List<ChartFile> RefreshResourceMaintenanceTargetChartsFromCurrentStorageOwners(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false))
            .Where(chart => chart != null)];
    }

    private static bool ShouldRefreshResourceMaintenanceTargetsFromCurrentStorageOwners(MaintenanceWorkflowResult workflowResult)
    {
        return workflowResult?.HasUpdates == true;
    }

    private ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        int resourceHealthInputVersion = resourceHealthOwner.CurrentInputVersion;
        EnsureOwnedChartCollectionBuiltUnsafe();
        List<ChartFile> targets;
        StorageRowsVersionSnapshot storageRowsVersion;
        int ownedCollectionVersion;
        lock (lockOwnedChartCollection)
        {
            targets = catalogOwnedCollectionOwner.Collection.CreateFullResourceMaintenanceTargetSnapshot();
            storageRowsVersion = new StorageRowsVersionSnapshot(
                catalogOwnedCollectionOwner.BmsRowsVersion,
                catalogOwnedCollectionOwner.BmsonRowsVersion);
            ownedCollectionVersion = OwnedChartCollectionVersion;
        }
        LogInstallPerformance("resource_maintenance_target build mode=full"
            + " reason=" + (reason ?? "unknown")
            + " targetCount=" + targets.Count
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return ResourceMaintenanceTargetSet.ForFullOwned(
            targets,
            storageRowsVersion,
            ownedCollectionVersion,
            resourceHealthInputVersion);
    }

    private ResourceHealthIndexCurrentVersion GetCurrentResourceHealthIndexVersion()
    {
        return new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(
                catalogStorageRowsOwner.BmsRowsVersion,
                catalogStorageRowsOwner.BmsonRowsVersion),
            OwnedChartCollectionVersion,
            resourceHealthOwner.CurrentInputVersion);
    }

    private ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshot(string reason)
    {
        ResourceHealthIndexSnapshot currentSnapshot = resourceHealthOwner.TryGetCurrentSnapshot();
        if (currentSnapshot != ResourceHealthIndexSnapshot.Empty)
        {
            return currentSnapshot;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return resourceHealthOwner.EnsureCurrent(
                    reason,
                    CreateFullOwnedResourceMaintenanceTargetSet(reason),
                    GetCurrentResourceHealthIndexVersion());
            }
        }
    }

    private ResourceHealthIndexDispatchResult DispatchResourceHealthIndexMutation(
        ResourceHealthIndexMutation mutation,
        string reason)
    {
        return resourceHealthOwner.Apply(
            mutation?.ToFacts(),
            reason,
            GetCurrentResourceHealthIndexVersion(),
            CreateFullOwnedResourceMaintenanceTargetSet);
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFile chart)
    {
        return resourceHealthOwner.TryGetCurrentProjection(chart);
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFileKind kind, string path, string md5)
    {
        return resourceHealthOwner.TryGetCurrentProjection(kind, path, md5);
    }

    internal ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshotForView(string reason)
    {
        return GetResourceHealthIndexSnapshot(reason);
    }

    internal ResourceHealthIndexSnapshot TryGetCurrentResourceHealthIndexSnapshotForView()
    {
        return resourceHealthOwner.TryGetCurrentSnapshot();
    }

    private PendingEstimatedInstallPostGuardResult ApplyEstimatedInstallMaintenanceForDeferredDispatch(
        IEnumerable<ChartFile> charts,
        EstimatedInstallDeferredFeedback deferredFeedback,
        out Action publishAfterGuard)
    {
        List<ChartFile> targets = BuildEstimatedInstallMaintenanceTargets(charts);
        if (targets.Count == 0)
        {
            publishAfterGuard = null;
            return new PendingEstimatedInstallPostGuardResult(0);
        }

        CatalogWriteFailureFact deferredFailureFact = null;
        Action catalogPostCommitEffects = null;
        try
        {
            CatalogMaintenanceOperationReceipt ownerReceipt;
            ownerReceipt = catalogMaintenanceOwner.ApplyMaintenance(
                CreateResourceMaintenanceTargetSet(targets),
                forceUpdate: true,
                progressReporter: null,
                cancellationToken: CancellationToken.None,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                reason: "install_package_estimated",
                dialogServiceOverride: deferredFeedback.DialogService,
                logPerformanceOverride: deferredFeedback.LogInstallPerformance,
                captureFailureFact: fact => deferredFailureFact = fact,
                postCommitEffectsObserver: action => catalogPostCommitEffects = action);
            MaintenanceWorkflowResult workflowResult = ownerReceipt.WorkflowResult.ToMutable();
            ResourceHealthIndexMutation resourceHealthMutation = ownerReceipt.ResourceHealthMutation.ToMutation();
            OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
                ownerReceipt.ResourceHealthMutation,
                workflowResult.HasUpdates);
            try
            {
                // Canonical cache/resource-health state is completed while the
                // caller still owns the outer mutation lease.  Only the
                // public events and ordinary status publication remain below.
                DispatchOwnedChartCollectionMutation(
                    mutationResult,
                    ownerReceipt.Reason,
                    publishNormalRefreshNotification: false,
                    publishOwnedCollectionNotifications: false);
            }
            catch
            {
                InvalidateOwnedChartCollection();
                InvalidateInstalledDirectoryIndex();
                resourceHealthOwner.ForceInvalidate("maintenance_dispatch_failed");
                throw;
            }
            ResourceHealthIndexDispatchResult resourceHealthDispatch =
                mutationResult.ResourceHealthDispatchResult ?? new ResourceHealthIndexDispatchResult();
            workflowResult.ResourceHealthIndexMs =
                resourceHealthMutation.HasChanges && !resourceHealthDispatch.Deferred
                    ? resourceHealthDispatch.IndexMs
                    : 0L;
            workflowResult.WarningReapplyTargets = 0;
            workflowResult.WarningChangedCount = 0;
            publishAfterGuard = () =>
            {
                catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact);
                TryInvokePostLeaseNotification(
                    catalogPostCommitEffects,
                    "estimated_install_catalog_property_notification_failed");
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "estimated_install_collection_notification_failed");
                TryInvokePostLeaseNotification(
                    () =>
                    {
                        PublishNormalLibraryRefreshNotification(mutationResult);
                        RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                    },
                    "estimated_install_refresh_notification_failed");
                LogAndReturnMaintenanceWorkflowResult(
                    workflowResult,
                    resourceHealthDispatch.Snapshot ?? ResourceHealthIndexSnapshot.Empty,
                    resourceHealthDispatch.DeltaApplied,
                    resourceHealthDispatch.Deferred,
                    resourceHealthDispatch.FullRebuilt);
            };
            return new PendingEstimatedInstallPostGuardResult(targets.Count);
        }
        catch (Exception exception)
        {
            publishAfterGuard = () =>
            {
                catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact);
                InvalidateOwnedChartCollection();
                InvalidateInstalledDirectoryIndex();
                resourceHealthOwner.ForceInvalidate("maintenance_apply_failed");
            };
            return new PendingEstimatedInstallPostGuardResult(
                targets.Count,
                ExceptionDispatchInfo.Capture(exception));
        }
    }

    private PendingEstimatedInstallPostGuardResult BuildEstimatedInstallInlineChartInfoForDeferredDispatch(
        string reason,
        IEnumerable<ChartFile> charts,
        EstimatedInstallDeferredFeedback deferredFeedback,
        out Action publishAfterGuard)
    {
        List<ChartFile> targets = [.. (charts ?? []).Where(chart => chart != null)];
        List<Action> ownerPublicationEffects = [];
        ChartInfoInlineBuildResult result = null;
        ExceptionDispatchInfo failure = null;
        using (BeginOwnedDigestMutationWindow())
        {
            try
            {
                result = catalogChartInfoOwner.BuildInline(
                    reason,
                    targets,
                    ownerPublicationEffects.Add,
                    deferredFeedback.LogInstallPerformance,
                    message => deferredFeedback.LogInstallWarning(null, message));
            }
            catch (Exception exception)
            {
                string displayedMessage = GetDisplayedExceptionMessage(exception).Replace(Environment.NewLine, " | ");
                BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
                ownerPublicationEffects.Add(() =>
                    lr2SynchronizationOwner.MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
                        options,
                        stage: "lr2_song_db_chart_info_inline_upsert_failed",
                        detail: "lr2_song_db_chart_info_inline_upsert_failed: " + displayedMessage,
                        logReason: reason ?? "chart_info_inline_install"));
                deferredFeedback.LogInstallWarning(exception, "lr2_song_db_chart_info_inline_upsert failed"
                    + " reason=" + (reason ?? "chart_info_inline_install")
                    + " exception=" + exception.GetType().Name
                    + " message=" + displayedMessage);
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        publishAfterGuard = () =>
        {
            foreach (Action publishOwnerEffect in ownerPublicationEffects)
            {
                publishOwnerEffect();
            }
        };
        return new PendingEstimatedInstallPostGuardResult(targets.Count, failure);
    }

    private MaintenanceWorkflowResult ApplyCatalogMaintenance(
        IEnumerable<ChartFile> charts,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.DeltaOnUpdates,
        string resourceHealthMutationReason = null)
    {
        if (charts == null)
        {
            return new MaintenanceWorkflowResult();
        }
        LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            "catalog_maintenance",
            showMessage: false);
        if (mutationReservation == null)
        {
            return new MaintenanceWorkflowResult { Canceled = true };
        }
        Action postCommitEffect = null;
        MaintenanceWorkflowResult workflowResult;
        try
        {
            using (mutationReservation)
            {
                ResourceMaintenanceTargetSet maintenanceTargets;
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    maintenanceTargets = CreateResourceMaintenanceTargetSet(charts);
                }
                workflowResult = ApplyCatalogMaintenanceCore(
                    maintenanceTargets,
                    forceUpdate,
                    progressReporter,
                    cancellationToken,
                    resourceHealthIndexUpdateMode,
                    resourceHealthMutationReason,
                    out _,
                    out postCommitEffect);
            }
        }
        catch
        {
            mutationReservation.Dispose();
            throw;
        }
        TryInvokePostLeaseNotification(postCommitEffect, "catalog_maintenance_publication_failed");
        return workflowResult;
    }

    /// <summary>
    /// Applies install/repair maintenance while the caller owns the file-mutation
    /// lease.  Database work remains inside that lease, but projection,
    /// failure-fact publication, and collection dispatch are deferred until
    /// release so no external callback observes the lease as active.
    /// </summary>
    private MaintenanceWorkflowResult ApplyCatalogMaintenanceUnderExistingReservation(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        string resourceHealthMutationReason,
        Action<Action> postLeaseNotificationObserver)
    {
        if (charts == null)
        {
            return new MaintenanceWorkflowResult();
        }
        ResourceMaintenanceTargetSet maintenanceTargets = CreateResourceMaintenanceTargetSet(charts);
        return ApplyCatalogMaintenanceCore(
            maintenanceTargets,
            forceUpdate,
            progressReporter: null,
            cancellationToken: CancellationToken.None,
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthMutationReason,
            out _,
            out Action postCommitEffect,
            postLeaseNotificationObserver);
    }

    private MaintenanceWorkflowResult ApplyOwnedCatalogMaintenance(
        string reason,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates,
        string resourceHealthMutationReason = null)
    {
        LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            "owned_catalog_maintenance",
            showMessage: false);
        if (mutationReservation == null)
        {
            return new MaintenanceWorkflowResult { Canceled = true };
        }
        Action postCommitEffect = null;
        MaintenanceWorkflowResult workflowResult;
        using (mutationReservation)
        {
            ResourceMaintenanceTargetSet maintenanceTargets;
            using (rwlockBMSFiles.GetWriterGuard())
            {
                maintenanceTargets = CreateFullOwnedResourceMaintenanceTargetSet(reason);
            }
            workflowResult = ApplyCatalogMaintenanceCore(
                maintenanceTargets,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode,
                resourceHealthMutationReason ?? reason,
                out _,
                out postCommitEffect);
        }
        TryInvokePostLeaseNotification(postCommitEffect, "owned_catalog_maintenance_publication_failed");
        return workflowResult;
    }

    private MaintenanceWorkflowResult ApplyOwnedCatalogMaintenanceUnderExistingReservation(
        string reason,
        Action<Action> postLeaseEffectObserver,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates,
        string resourceHealthMutationReason = null)
    {
        ArgumentNullException.ThrowIfNull(postLeaseEffectObserver);
        ResourceMaintenanceTargetSet maintenanceTargets;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            maintenanceTargets = CreateFullOwnedResourceMaintenanceTargetSet(reason);
        }
        MaintenanceWorkflowResult workflowResult = ApplyCatalogMaintenanceCore(
            maintenanceTargets,
            forceUpdate,
            progressReporter,
            cancellationToken,
            resourceHealthIndexUpdateMode,
            resourceHealthMutationReason ?? reason,
            out _,
            out Action postCommitEffect);
        postLeaseEffectObserver(postCommitEffect);
        return workflowResult;
    }

    private MaintenanceWorkflowResult ApplyInstallableCatalogMaintenance(
        string reason,
        Action<Action> postLeaseEffectObserver,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseHydratedMaintenanceSnapshotForInstallableMaintenance())
        {
            return ApplyOwnedCatalogMaintenanceUnderExistingReservation(
                reason,
                forceUpdate: false,
                progressReporter: progressReporter,
                cancellationToken: cancellationToken,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.FullOnUpdates,
                resourceHealthMutationReason: reason,
                postLeaseEffectObserver: postLeaseEffectObserver);
        }
        ArgumentNullException.ThrowIfNull(postLeaseEffectObserver);
        ResourceMaintenanceTargetSet maintenanceTargets;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            maintenanceTargets = CreatePendingInstallableMaintenanceTargetSetUnsafe(reason);
        }
        MaintenanceWorkflowResult workflowResult = ApplyCatalogMaintenanceCore(
                maintenanceTargets,
                forceUpdate: false,
                progressReporter,
                cancellationToken,
                ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                reason,
                out _,
                out Action postCommitEffect);
        postLeaseEffectObserver(postCommitEffect);
        return workflowResult;
    }

    private bool CanUseHydratedMaintenanceSnapshotForInstallableMaintenance()
    {
        return catalogMaintenanceOwner?.HydrationReadyForInstallableMaintenance == true;
    }

    private ResourceMaintenanceTargetSet CreatePendingInstallableMaintenanceTargetSetUnsafe(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        OwnedChartStorageOwnerView ownerView = CreateOwnedChartStorageOwnerViewUnsafe();
        List<ChartFile> targets = [];
        int bmsMissingInfo = 0;
        int bmsMissingEncoding = 0;
        int bmsonMissingInfo = 0;
        foreach (BMSFile file in ownerView.BmsFiles)
        {
            if (file == null)
            {
                continue;
            }
            BMSFileMaintenanceInfo maintenanceInfo = file.TryGetMaintenanceInfoWithoutCreating();
            bool missingInfo = maintenanceInfo?.IsInformationChecked() != true;
            bool missingEncoding = string.IsNullOrWhiteSpace(maintenanceInfo?.encoding);
            if (!missingInfo && !missingEncoding)
            {
                continue;
            }
            ChartFile target = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);
            if (target != null)
            {
                targets.Add(target);
            }
            if (missingInfo)
            {
                bmsMissingInfo++;
            }
            if (missingEncoding)
            {
                bmsMissingEncoding++;
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in ownerView.BmsonSongs)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            BMSFileMaintenanceInfo maintenanceInfo = song.MaintenanceInfo;
            bool missingInfo = maintenanceInfo?.IsInformationChecked() != true;
            if (!missingInfo)
            {
                continue;
            }
            ChartFile target = ChartFileProjection.FromBmsonSong(
                song,
                includeWarningSnapshot: false,
                includeResourceReferences: true);
            if (target != null)
            {
                targets.Add(target);
            }
            bmsonMissingInfo++;
        }
        stopwatch.Stop();
        LogInstallPerformance("installable_maintenance_target build mode=hydrated_missing"
            + " reason=" + (reason ?? "unknown")
            + " ownerCount=" + ownerView.Count
            + " targetCount=" + targets.Count
            + " bmsMissingInfo=" + bmsMissingInfo
            + " bmsMissingEncoding=" + bmsMissingEncoding
            + " bmsonMissingInfo=" + bmsonMissingInfo
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return CreateResourceMaintenanceTargetSet(targets);
    }

    private MaintenanceWorkflowResult ApplyCatalogMaintenanceCore(
        ResourceMaintenanceTargetSet maintenanceTargets,
        bool forceUpdate,
        Action<MaintenanceWorkflowProgress> progressReporter,
        CancellationToken cancellationToken,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        string resourceHealthMutationReason,
        out List<ChartFile> currentMaintenanceTargetCharts,
        out Action postCommitEffect,
        Action<Action> postLeaseNotificationObserver = null)
    {
        postCommitEffect = null;
        CatalogWriteFailureFact deferredFailureFact = null;
        Action catalogPostCommitEffects = null;
        CatalogMaintenanceOperationReceipt receipt = catalogMaintenanceOwner.ApplyMaintenance(
            maintenanceTargets,
            forceUpdate,
            progressReporter,
            cancellationToken,
            resourceHealthIndexUpdateMode,
            resourceHealthMutationReason,
            captureFailureFact: fact => deferredFailureFact = fact,
            postCommitEffectsObserver: action => catalogPostCommitEffects = action);
        MaintenanceWorkflowResult workflowResult = receipt.WorkflowResult.ToMutable();
        currentMaintenanceTargetCharts = [.. maintenanceTargets.Charts];
        resourceHealthMutationReason = receipt.Reason;
        ResourceHealthIndexMutation resourceHealthMutation = receipt.ResourceHealthMutation.ToMutation();
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            receipt.ResourceHealthMutation,
            workflowResult.HasUpdates);
        DispatchOwnedChartCollectionMutation(
            mutationResult,
            resourceHealthMutationReason,
            publishOwnedCollectionNotifications: false,
            publishNormalRefreshNotification: false);
        postCommitEffect = () =>
        {
            TryInvokePostLeaseNotification(
                () => catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact),
                "catalog_maintenance_failure_fact_publication_failed");
            TryInvokePostLeaseNotification(
                catalogPostCommitEffects,
                "catalog_maintenance_property_notification_failed");
            TryInvokePostLeaseNotification(
                () =>
                {
                    if (mutationResult.ParentFolderInvalidated)
                    {
                        NotifyBMSParentFolderListCacheChanged();
                    }
                    PublishOwnedCollectionChangeNotification(mutationResult);
                    PublishNormalLibraryRefreshNotification(mutationResult);
                    RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                },
                "catalog_maintenance_publication_failed");
            ResourceHealthIndexDispatchResult resourceHealthDispatch =
                mutationResult.ResourceHealthDispatchResult ?? new ResourceHealthIndexDispatchResult();
            bool resourceHealthIndexDeferred = resourceHealthDispatch.Deferred;
            workflowResult.ResourceHealthIndexMs = resourceHealthMutation.HasChanges
                && !resourceHealthIndexDeferred
                ? resourceHealthDispatch.IndexMs
                : 0L;
            workflowResult.WarningReapplyTargets = 0;
            workflowResult.WarningChangedCount = 0;
            LogAndReturnMaintenanceWorkflowResult(
                workflowResult,
                resourceHealthDispatch.Snapshot ?? ResourceHealthIndexSnapshot.Empty,
                resourceHealthDispatch.DeltaApplied,
                resourceHealthIndexDeferred,
                resourceHealthDispatch.FullRebuilt);
        };
        postLeaseNotificationObserver?.Invoke(postCommitEffect);
        return workflowResult;
    }

    private MaintenanceWorkflowResult LogAndReturnMaintenanceWorkflowResult(
        MaintenanceWorkflowResult workflowResult,
        ResourceHealthIndexSnapshot resourceHealthSnapshot,
        bool resourceHealthDeltaApplied,
        bool resourceHealthIndexDeferred,
        bool resourceHealthIndexFullRebuilt)
    {
        workflowResult.ResourceHealthIndexDeltaApplied = resourceHealthDeltaApplied;
        workflowResult.ResourceHealthIndexDeferred = resourceHealthIndexDeferred;
        workflowResult.ResourceHealthIndexFullRebuilt = resourceHealthIndexFullRebuilt;
        if (workflowResult.CheckedFileCount > 0 || workflowResult.BmsonReparsedCount > 0 || workflowResult.BmsonReparseFailedCount > 0 || workflowResult.BmsonResourceReferenceReusedCount > 0 || resourceHealthSnapshot.TargetCount > 0)
        {
            LogInstallPerformance("maintenance_update checked=" + workflowResult.CheckedFileCount
                + " bmsResourceTargets=" + workflowResult.BmsResourceTargetCount
                + " bmsonResourceTargets=" + workflowResult.BmsonResourceTargetCount
                + " healthTargetCount=" + workflowResult.HealthTargetCount
                + " healthDegree=" + workflowResult.HealthDegree
                + " readerDegree=" + workflowResult.ReaderDegree
                + " readQueueCapacity=" + workflowResult.ReadQueueCapacity
                + " computedQueueCapacity=" + workflowResult.ComputedQueueCapacity
                + " forceTargets=" + workflowResult.ForceTargetCount
                + " missingInfoTargets=" + workflowResult.MissingInfoTargetCount
                + " missingEncodingTargets=" + workflowResult.MissingEncodingTargetCount
                + " bmsonMissingFreshRefs=" + workflowResult.BmsonMissingFreshResourceReferenceCount
                + " healthMs=" + workflowResult.HealthMs
                + " encodingMs=" + workflowResult.EncodingMs
                + " bmsonRefreshMs=" + workflowResult.BmsonRefreshMs
                + " healthCacheHit=" + workflowResult.HealthCacheHitCount
                + " healthFileExistsFallback=" + workflowResult.HealthFileExistsFallbackCount
                + " healthFileExistsFallbackAudio=" + workflowResult.HealthAudioFileExistsFallbackCount
                + " healthFileExistsFallbackImage=" + workflowResult.HealthImageFileExistsFallbackCount
                + " healthFileExistsFallbackMovie=" + workflowResult.HealthMovieFileExistsFallbackCount
                + " healthFileExistsFallbackOptionalImage=" + workflowResult.HealthOptionalImageFileExistsFallbackCount
                + " maintenanceUpserted=" + workflowResult.MaintenanceInfoUpsertCount
                + " maintenanceUnchanged=" + workflowResult.MaintenanceInfoUnchangedCount
                + " bmsonReparsed=" + workflowResult.BmsonReparsedCount
                + " bmsonReparseFailed=" + workflowResult.BmsonReparseFailedCount
                + " bmsonResourceRefsReused=" + workflowResult.BmsonResourceReferenceReusedCount
                + " songReloaded=" + workflowResult.ReloadedSongCount
                + " readMs=" + workflowResult.ReadMs
                + " digestMs=" + workflowResult.DigestMs
                + " computeMs=" + workflowResult.ComputeMs
                + " commitMs=" + workflowResult.CommitMs
                + " resourceHealthIndexMs=" + workflowResult.ResourceHealthIndexMs
                + " resourceHealthIndexMode=" + (resourceHealthIndexDeferred ? "deferred" : (resourceHealthDeltaApplied ? "delta" : (resourceHealthIndexFullRebuilt ? "full" : "current")))
                + " warningReapplyTargets=" + workflowResult.WarningReapplyTargets
                + " warningChanged=" + workflowResult.WarningChangedCount
                + " canceled=" + workflowResult.Canceled.ToString().ToLowerInvariant()
                + " elapsedMs=" + workflowResult.TotalMs);
        }
        return workflowResult;
    }

    internal List<ChartFile> GetChartsNeedResourceFix(IEnumerable<ChartFile> charts, bool forceUpdate = false, bool isInIgnoredList = false)
    {
        bool useOwnedSnapshot = charts == null;
        if (useOwnedSnapshot && !forceUpdate)
        {
            ResourceHealthIndexSnapshot currentSnapshot = GetResourceHealthIndexSnapshot("resource_health_filter");
            return [.. (isInIgnoredList ? currentSnapshot.IgnoredTargets : currentSnapshot.ActiveTargets)];
        }

        string resourceHealthReason = useOwnedSnapshot
            ? "force_resource_health_filter"
            : "resource_health_filter";
        MaintenanceWorkflowResult maintenanceResult = forceUpdate
            ? useOwnedSnapshot
                ? ApplyOwnedCatalogMaintenance(
                    resourceHealthReason,
                    forceUpdate: true,
                    resourceHealthMutationReason: resourceHealthReason)
                : ApplyCatalogMaintenance(
                    charts,
                    forceUpdate: true,
                    resourceHealthMutationReason: resourceHealthReason)
            : new MaintenanceWorkflowResult();
        if (useOwnedSnapshot)
        {
            ResourceHealthIndexSnapshot ownedSnapshot = GetResourceHealthIndexSnapshot(resourceHealthReason);
            return [.. (isInIgnoredList ? ownedSnapshot.IgnoredTargets : ownedSnapshot.ActiveTargets)];
        }

        List<ChartFile> targets = maintenanceResult.HasUpdates
            ? RefreshResourceMaintenanceTargetChartsFromCurrentStorageOwners(charts)
            : NormalizeResourceMaintenanceTargetCharts(charts);
        if (targets.Count == 0)
        {
            return [];
        }
        return resourceHealthOwner.FilterTargetsByWarningState(targets, isInIgnoredList);
    }

    /// <summary>
    /// 指定された譜面の構成ファイル health を明示的に再計算します。
    /// 通常の hydration とは異なり、ユーザー操作に基づいて実ファイル確認と DB 更新を行います。
    /// </summary>
    /// <param name="charts">再スキャン対象。</param>
    /// <param name="progressReporter">section 単位の進捗通知。</param>
    /// <param name="cancellationToken">section 境界で確認するキャンセル token。</param>
    /// <returns>再スキャン結果。</returns>
    internal MaintenanceWorkflowResult RescanResourceHealthCharts(
        IEnumerable<ChartFile> charts,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        MaintenanceWorkflowResult result = ApplyCatalogMaintenance(
            charts,
            forceUpdate: true,
            progressReporter: progressReporter,
            cancellationToken: cancellationToken,
            resourceHealthMutationReason: "resource_health_rescan");
        return result;
    }

    /// <summary>
    /// 全所持譜面の構成ファイル health を明示的に再計算します。
    /// 後から追加した BGA などを反映するための重い手動操作です。
    /// </summary>
    /// <param name="progressReporter">section 単位の進捗通知。</param>
    /// <param name="cancellationToken">section 境界で確認するキャンセル token。</param>
    /// <returns>再スキャン結果。</returns>
    internal MaintenanceWorkflowResult RescanAllOwnedChartMaintenance(
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        MaintenanceWorkflowResult result = ApplyOwnedCatalogMaintenance(
            "manual_rescan_all_owned",
            forceUpdate: true,
            progressReporter: progressReporter,
            cancellationToken: cancellationToken);
        return result;
    }

    internal void SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset = false)
    {
        string reason = unset ? "resource_health_unignore" : "resource_health_ignore";
        using LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            reason,
            showMessage: false);
        if (mutationReservation == null)
        {
            return;
        }
        OwnedChartCollectionMutationResult mutationResult;
        List<ChartFile> targets;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                targets = NormalizeResourceMaintenanceTargetCharts(charts);
            }
        }
        if (targets.Count == 0)
        {
            return;
        }

        // The BMS-file lock is only a snapshot boundary.  The catalog
        // owner performs the DB transaction under its own narrow guard.
        CatalogMaintenanceOperationReceipt receipt = catalogMaintenanceOwner.ApplyWarningIgnore(targets, unset, reason);
        mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            receipt.ResourceHealthMutation,
            receipt.WorkflowResult.HasUpdates);
        mutationResult.MaintenancePresentationChanged = false;
        mutationReservation.Dispose();
        DispatchOwnedChartCollectionMutation(mutationResult, reason);
    }

    /// <summary>
    /// エンコーディングが Shift_JIS 以外と推定された（文字化けの可能性がある）BMS ファイル群を取得します。
    /// </summary>
    private List<BMSFile> GetGarbledBmsStorageRows(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        bmsFiles ??= BMSFiles;
        if (bmsFiles.Count() == 0)
        {
            return [];
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return maintenanceService.GetGarbledFiles(bmsFiles, isInFixedList);
            }
        }
    }

    /// <summary>
    /// 指定された BMS ファイル群のエンコーディングを上書き設定し、song.db と maintenance テーブルに反映します。
    /// </summary>
    public void SetBMSFilesEncoding(IEnumerable<BMSFile> bmsFiles, string encoding = "")
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        if (bmsFiles == null || bmsFiles.Count() == 0)
        {
            return;
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(SetBMSFilesEncoding)))
        {
            return;
        }
        using LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(SetBMSFilesEncoding),
            showMessage: false);
        if (mutationReservation == null)
        {
            return;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                catalogMaintenanceOwner.ApplyEncoding(bmsFiles, encoding);
            }
        }
    }

    /// <summary>
    /// chart_info 上のノート数が 0 のファイルについて、実際のファイルを再確認して可視ノート風記述がないか検出します。
    /// </summary>
    public void RecheckZeroNoteWarnings()
    {
        List<ChartFile> allCharts;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            allCharts = CreateOwnedBmsChartFilesUnsafe(includeResourceReferences: false);
        }
        ZeroNoteRecheckResult result = maintenanceService.RecheckZeroNoteWarnings(
            allCharts,
            (ex, message) => NLogWrapper.FileLogger?.Warn(ex, message),
            ResolveChartInfoForChart);
        if (result.ChangedCount > 0)
        {
            DispatchWarningPresentationChanged("zero_note_recheck");
        }
        NLogWrapper.FileLogger?.Info(string.Format("zero_note_recheck total={0} mismatch={1} cleared={2} skipped={3} changed={4}", result.Total, result.MismatchCount, result.ClearedCount, result.SkippedCount, result.ChangedCount));
    }

    internal List<ChartFile> GetChartInfoParseFailedChartFiles()
    {
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures = catalogChartInfoOwner.LoadCurrentParseFailureMap(dbGateway, chartInfoBuildService.CurrentParseTimeout);
        if (failures.Count == 0)
        {
            return [];
        }
        HashSet<string> failedMd5s = new(failures.Keys.Where(hash => !string.IsNullOrWhiteSpace(hash)), StringComparer.OrdinalIgnoreCase);
        List<ChartFile> failedCharts;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            failedCharts = CreateOwnedChartFilesForMd5HashesUnsafe(
                failedMd5s,
                includeWarningSnapshot: false,
                includeResourceReferences: false);
        }
        List<ChartFile> result = [];
        foreach (ChartFile chart in failedCharts)
        {
            if (string.IsNullOrWhiteSpace(chart?.Md5) || !failures.TryGetValue(chart.Md5, out LR2SongDBExtended.chart_info_parse_failure failure))
            {
                continue;
            }
            result.Add(ApplyChartInfoParseFailureWarning(chart, failure));
        }
        return [.. result
            .Where(chart => chart != null)
            .OrderBy(chart => chart.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)];
    }

    private static ChartFile ApplyChartInfoParseFailureWarning(ChartFile chart, LR2SongDBExtended.chart_info_parse_failure failure)
    {
        if (chart == null || failure == null)
        {
            return chart;
        }
        return ChartFileProjection.WithWarnings(
            chart,
            [ChartWarning.Create(ChartWarningKind.ChartInfoParseFailure, BuildChartInfoParseFailureWarningMessage(failure))]);
    }

    private static string BuildChartInfoParseFailureWarningMessage(LR2SongDBExtended.chart_info_parse_failure failure)
    {
        string reason = !string.IsNullOrWhiteSpace(failure.exception_type)
            ? failure.exception_type
            : (!string.IsNullOrWhiteSpace(failure.failure_kind) ? failure.failure_kind : Resources.WarningDigest_ChartInfoParseFailure);
        string message = !string.IsNullOrWhiteSpace(failure.message) ? failure.message : Resources.WarningDigest_ChartInfoParseFailure;
        return string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, reason, message);
    }

    public void RemoveChartInfoParseFailuresByMd5(IEnumerable<string> md5s)
    {
        catalogChartInfoOwner.RemoveParseFailuresByMd5(md5s);
    }

    internal static string[] NormalizeChartInfoParseFailureMd5s(IEnumerable<string> md5s)
    {
        return CatalogChartInfoOwner.NormalizeParseFailureMd5s(md5s);
    }

    /// <summary>
    /// BMS ファイル群のモード（SP/DP等）を検出し、song.db にコミットします。
    /// </summary>
    private int setModeAndCommitToDB(
        IEnumerable<BMSFile> bmsFiles,
        bool forceUpdate = false)
    {
        // The caller supplies either the startup-owned file snapshot or the
        // active initialization collection.  Do not hold the BMS-file lock
        // across mode parsing or the catalog DB write.
        List<BMSFile> list = maintenanceService.DetectModeChanges(bmsFiles, forceUpdate);
        if (list.Count <= 0)
        {
            return 0;
        }
        catalogMutationOwner.ApplyModeChangeSongRows(list);
        return list.Count;
    }

    /// <summary>
    /// ライブラリ内の重複 chart を検出し、Union-Find でディレクトリグループ化した結果を <see cref="DuplicateChartGroups"/> に格納します。
    /// </summary>
    public void SearchDuplicateChartGroups()
    {
        var totalStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                using (rwlockDuplicateChartGroups.GetWriterGuard())
                {
                    if (DuplicateChartGroups != null)
                    {
                        LogInstallPerformance("SearchDuplicateChartGroups: cacheHit=true totalMs=" + totalStopwatch.ElapsedMilliseconds
                            + " Groups=" + DuplicateChartGroups.Count);
                        return;
                    }
                    long stageStartMs = totalStopwatch.ElapsedMilliseconds;
                    OwnedDuplicateChartRowSnapshot duplicateSnapshot = CreateOwnedDuplicateChartRowSnapshotUnsafe();
                    IReadOnlyList<DuplicateChartRow> snapshot = duplicateSnapshot.Rows;
                    long ownedSnapshotMs = totalStopwatch.ElapsedMilliseconds - stageStartMs;
                    stageStartMs = totalStopwatch.ElapsedMilliseconds;
                    IEnumerable<BMSFile> duplicateWarningClearTargets = duplicateWarningFullClearPending
                        ? duplicateSnapshot.BmsStorageRows
                        : duplicateWarningBmsOwners;
                    duplicateWarningFullClearPending = false;
                    duplicateWarningBmsOwners = [];
                    duplicateService.ClearDuplicateState(duplicateWarningClearTargets);
                    long clearDuplicateStateMs = totalStopwatch.ElapsedMilliseconds - stageStartMs;
                    stageStartMs = totalStopwatch.ElapsedMilliseconds;
                    DuplicateAnalysisResult analysis = duplicateService.Analyze(duplicateSnapshot, DuplicateWarningMessage);
                    long analyzeMs = totalStopwatch.ElapsedMilliseconds - stageStartMs;
                    stageStartMs = totalStopwatch.ElapsedMilliseconds;
                    duplicateWarningBmsOwners = duplicateService.ApplyDuplicateWarnings(analysis.DuplicateCharts, DuplicateWarningMessage);
                    long applyWarningsMs = totalStopwatch.ElapsedMilliseconds - stageStartMs;
                    stageStartMs = totalStopwatch.ElapsedMilliseconds;
                    DuplicateChartGroups = analysis.DuplicateGroups;
                    long propertySetMs = totalStopwatch.ElapsedMilliseconds - stageStartMs;
                    totalStopwatch.Stop();
                    LogInstallPerformance("SearchDuplicateChartGroups: cacheHit=false"
                        + " totalMs=" + totalStopwatch.ElapsedMilliseconds
                        + " ownedSnapshotMs=" + ownedSnapshotMs
                        + " clearDuplicateStateMs=" + clearDuplicateStateMs
                        + " analyzeMs=" + analyzeMs
                        + " applyWarningsMs=" + applyWarningsMs
                        + " propertySetMs=" + propertySetMs
                        + " chartCount=" + snapshot.Count
                        + " rowCount=" + snapshot.Count
                        + " duplicateHashCount=" + duplicateSnapshot.DuplicateHashCount
                        + " duplicateHashRowCount=" + duplicateSnapshot.DuplicateHashRowCount
                        + " connectedDirCount=" + analysis.ConnectedDirectoryCount
                        + " materializedChartCount=" + analysis.MaterializedChartCount
                        + " duplicateChartCount=" + analysis.DuplicateCharts.Count
                        + " Groups=" + analysis.DuplicateGroups.Count);
                }
            }
        }
    }
    private static readonly string DuplicateWarningMessage = Resources.Warning_DuplicateBmsFile;

    private sealed class InstalledChartMetadataCandidate
    {
        internal string Title { get; set; }

        internal string Artist { get; set; }

        internal string Path { get; set; }
    }

    private IEnumerable<InstalledChartMetadataCandidate> EnumerateInstalledChartMetadataCandidatesUnsafe(string normalizedDestinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(normalizedDestinationDirectory))
        {
            return [];
        }

        return CreateOwnedRealPathChartRefsUnsafe(normalizedDestinationDirectory)
            .Select(CreateInstalledChartMetadataCandidate)
            .Where(candidate => candidate != null);
    }

    private static InstalledChartMetadataCandidate CreateInstalledChartMetadataCandidate(LibraryChartRef chartRef)
    {
        BMSFile bmsFile = chartRef?.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return new InstalledChartMetadataCandidate
            {
                Title = bmsFile.Title ?? string.Empty,
                Artist = bmsFile.Artist ?? string.Empty,
                Path = bmsFile.path ?? string.Empty
            };
        }

        LR2SongDBExtended.bmson_song bmsonSong = chartRef?.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return new InstalledChartMetadataCandidate
            {
                Title = BmsonSongParser.ComposeDisplayTitle(bmsonSong),
                Artist = bmsonSong.artist ?? string.Empty,
                Path = bmsonSong.path ?? string.Empty
            };
        }

        return null;
    }

    private static string NormalizeInstallDestinationDirectoryForLookup(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return null;
        }

        string normalizedPath;
        try
        {
            normalizedPath = LongPathFileSystem.NormalizePathForStorage(destinationDirectory.Trim());
        }
        catch
        {
            normalizedPath = destinationDirectory.Trim();
        }

        return LongPathFileSystem.TrimTrailingDirectorySeparators(normalizedPath);
    }

    private InstallDestinationRepresentativeMetadata ResolveInstallDestinationRepresentativeMetadataUnsafe(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return InstallDestinationRepresentativeMetadata.Empty;
        }
        string normalizedDestinationDirectory = NormalizeInstallDestinationDirectoryForLookup(destinationDirectory);
        InstalledChartMetadataCandidate representativeChart = EnumerateInstalledChartMetadataCandidatesUnsafe(normalizedDestinationDirectory)
            .OrderByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Artist))
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (representativeChart == null)
        {
            return InstallDestinationRepresentativeMetadata.Empty;
        }
        return new InstallDestinationRepresentativeMetadata
        {
            Title = representativeChart.Title ?? string.Empty,
            Artist = representativeChart.Artist ?? string.Empty
        };
    }

    private InstallEstimationMetadataProfile ResolveInstallDestinationMetadataProfileUnsafe(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return InstallEstimationMetadataProfile.Empty;
        }

        string normalizedDestinationDirectory = NormalizeInstallDestinationDirectoryForLookup(destinationDirectory);
        lock (lockInstallEstimationMetadataProfileCache)
        {
            if (installEstimationMetadataProfileCache.TryGetValue(normalizedDestinationDirectory, out InstallEstimationMetadataProfile cachedProfile))
            {
                return cachedProfile ?? InstallEstimationMetadataProfile.Empty;
            }
        }

        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile(
            EnumerateInstalledChartMetadataCandidatesUnsafe(normalizedDestinationDirectory)
                .Select(candidate => (candidate.Title ?? string.Empty, candidate.Artist ?? string.Empty, candidate.Path ?? string.Empty)));

        lock (lockInstallEstimationMetadataProfileCache)
        {
            installEstimationMetadataProfileCache[normalizedDestinationDirectory] = profile ?? InstallEstimationMetadataProfile.Empty;
            return installEstimationMetadataProfileCache[normalizedDestinationDirectory];
        }
    }

    private InstallDestinationRepresentativeMetadata ResolveInstallDestinationRepresentativeMetadataThreadSafe(string destinationDirectory)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return ResolveInstallDestinationRepresentativeMetadataUnsafe(destinationDirectory);
            }
        }
    }

    private InstallEstimationMetadataProfile ResolveInstallDestinationMetadataProfileThreadSafe(string destinationDirectory)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return ResolveInstallDestinationMetadataProfileUnsafe(destinationDirectory);
            }
        }
    }

    private void ApplyResolvedInstallDestinationToEntries(IEnumerable<PackageChartEntry> entries, string destinationDirectory, bool preserveAmbiguousInstallContext = false)
    {
        InstallDestinationRepresentativeMetadata metadata = ResolveInstallDestinationRepresentativeMetadataUnsafe(destinationDirectory);
        foreach (PackageChartEntry entry in (entries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplyInstallDestination(destinationDirectory, metadata.Title, metadata.Artist, preserveAmbiguousInstallContext);
        }
    }

    private void ApplyResolvedInstallDestinationPathAndMetadataToEntries(IEnumerable<PackageChartEntry> entries, string destinationDirectory)
    {
        InstallDestinationRepresentativeMetadata metadata = ResolveInstallDestinationRepresentativeMetadataUnsafe(destinationDirectory);
        foreach (PackageChartEntry entry in (entries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplyInstallDestinationMetadata(destinationDirectory, metadata.Title, metadata.Artist);
        }
    }

    private void ApplyInstallEstimationResultToEntries(IEnumerable<PackageChartEntry> entries, InstallEstimationResult result)
    {
        foreach (PackageChartEntry entry in (entries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplyInstallEstimationResult(result);
        }
    }

    private static bool HasPendingLowConfidenceInstallDestination(IEnumerable<PackageChartEntry> entries)
    {
        return (entries ?? []).Any(entry => entry?.Chart != null
            && string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)
            && !string.IsNullOrWhiteSpace(entry.Chart.InstallDestinationTitle));
    }

    private InstallEstimationEvaluationData EvaluateInstallEstimation(ChartPackage package, List<PackageChartEntry> targetEntries, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, BmsLibraryOptionsSnapshot optionsSnapshot = null, bool useThreadSafeResolvers = false, bool useSharedLazyHashMetrics = false, DirectoryResourceLookupCache directoryLookupCacheSnapshot = null, PendingEstimateSourceBatchPackageState batchState = null, IReadOnlyCollection<string> candidateDirectoryOverride = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null, bool markInstalledDestinationAmbiguous = false)
    {
        List<PackageChartEntry> targetEntryList = [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
        if (targetEntryList.Count == 0)
        {
            return new InstallEstimationEvaluationData
            {
                ChartCount = 0
            };
        }
        Func<string, InstallDestinationRepresentativeMetadata> representativeResolver = useThreadSafeResolvers
            ? new Func<string, InstallDestinationRepresentativeMetadata>(ResolveInstallDestinationRepresentativeMetadataThreadSafe)
            : new Func<string, InstallDestinationRepresentativeMetadata>(ResolveInstallDestinationRepresentativeMetadataUnsafe);
        Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = useThreadSafeResolvers
            ? new Func<string, InstallEstimationMetadataProfile>(ResolveInstallDestinationMetadataProfileThreadSafe)
            : new Func<string, InstallEstimationMetadataProfile>(ResolveInstallDestinationMetadataProfileUnsafe);
        DirectoryResourceLookupCache effectiveDirectoryLookupCache = directoryLookupCacheSnapshot
            ?? libraryResourceIndexOwner.CaptureSnapshot().DirectoryLookupCache;
        long lazyHashBuildMsBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashBuildMs ?? 0L);
        long lazyHashLookupCountBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashLookupCount ?? 0L);
        int lazyHashCacheEntriesBefore = useSharedLazyHashMetrics ? 0 : (effectiveDirectoryLookupCache?.LazyHashCacheEntryCount ?? 0);
        bool sourceSurfaceBatchHit = package != null
            && LongPathFileSystem.DirectoryExists(package.path)
            && batchState?.UsesBatchSourceSurface == true
            && batchState.SourceSurface != null;
        ChartResourceSnapshot precomputedDefinedResources = ResolvePrecomputedDefinedResources(batchState, targetEntryList);
        PackageInstallEstimationSnapshot estimationSnapshot;
        if (package != null && sourceSurfaceBatchHit)
        {
            bool includeBundledResources = LongPathFileSystem.DirectoryExists(package.path);
            PackageInstallSurfaceSnapshot sharedInstallSurface = PackageInstallEstimationSnapshotBuilder.BuildSharedInstallSurfaceSnapshot(
                package.path,
                batchState.SourceDirectory,
                batchState.SourceSurface,
                includeBundledResources);
            estimationSnapshot = package.BuildInstallEstimationSnapshotFromEntries(
                targetEntryList,
                sharedInstallSurface,
                sourceSurfaceCacheHit: false,
                sourceSurfaceBatchHit: true,
                precomputedDefinedResources);
        }
        else
        {
            estimationSnapshot = package != null
                ? package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntryList, precomputedDefinedResources)
                : PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries(targetEntryList);
        }
        if (estimationSnapshot?.SourceSurfaceScanLimitExceeded == true)
        {
            return new InstallEstimationEvaluationData
            {
                ChartCount = targetEntryList.Count,
                Result = null,
                SourceSurfaceScanMs = estimationSnapshot.SourceSurfaceScanMs,
                SourceSurfaceChartFileCount = estimationSnapshot.SourceSurfaceChartFileCount,
                SourceSurfaceResourceFileCount = estimationSnapshot.SourceSurfaceResourceFileCount,
                SourceSurfaceTrackedFileCount = estimationSnapshot.SourceSurfaceTrackedFileCount,
                SourceSurfaceHashMaterializeMs = estimationSnapshot.SourceSurfaceHashMaterializeMs,
                SourceSurfaceCacheHit = estimationSnapshot.SourceSurfaceCacheHit,
                SourceSurfaceBatchHit = estimationSnapshot.SourceSurfaceBatchHit,
                SourceSurfaceScanBackend = estimationSnapshot.SourceSurfaceScanBackend,
                SourceSurfaceScanLimitExceeded = true,
                SourceSurfaceVisitedFileSystemEntryCount = estimationSnapshot.SourceSurfaceVisitedFileSystemEntryCount,
                SourceSurfaceMaxVisitedFileSystemEntryCount = estimationSnapshot.SourceSurfaceMaxVisitedFileSystemEntryCount
            };
        }
        InstallEstimationResult result = candidateDirectoryOverride == null
            ? CreateInstallEstimationService(optionsSnapshot).EstimateInstallationDirectory(
                estimationSnapshot,
                effectiveDirectoryLookupCache,
                candidateEvaluationDegree,
                estimateMode,
                representativeResolver,
                metadataProfileResolver,
                candidateDirectoryUniquePrimaryHashCountResolver)
            : CreateInstallEstimationService(optionsSnapshot).EstimateInstallationDirectoryForCandidateDirectories(
                estimationSnapshot,
                candidateDirectoryOverride,
                effectiveDirectoryLookupCache,
                candidateEvaluationDegree,
                estimateMode,
                representativeResolver,
                metadataProfileResolver,
                candidateDirectoryUniquePrimaryHashCountResolver);
        if (markInstalledDestinationAmbiguous && result?.LowConfidenceKind == InstallEstimationLowConfidenceKind.AmbiguousCandidates)
        {
            ApplyMixedInstalledDestinationAmbiguityPolicy(result);
        }
        long lazyHashBuildMsAfter = useSharedLazyHashMetrics ? lazyHashBuildMsBefore : (effectiveDirectoryLookupCache?.LazyHashBuildMs ?? lazyHashBuildMsBefore);
        long lazyHashLookupCountAfter = useSharedLazyHashMetrics ? lazyHashLookupCountBefore : (effectiveDirectoryLookupCache?.LazyHashLookupCount ?? lazyHashLookupCountBefore);
        int lazyHashCacheEntriesAfter = useSharedLazyHashMetrics ? lazyHashCacheEntriesBefore : (effectiveDirectoryLookupCache?.LazyHashCacheEntryCount ?? lazyHashCacheEntriesBefore);
        return new InstallEstimationEvaluationData
        {
            ChartCount = targetEntryList.Count,
            Result = result,
            LazyHashBuildMsDelta = useSharedLazyHashMetrics ? 0L : (lazyHashBuildMsAfter - lazyHashBuildMsBefore),
            LazyHashLookupCountDelta = useSharedLazyHashMetrics ? 0L : (lazyHashLookupCountAfter - lazyHashLookupCountBefore),
            LazyHashEntriesAdded = useSharedLazyHashMetrics ? 0 : (lazyHashCacheEntriesAfter - lazyHashCacheEntriesBefore),
            LazyHashBuildReason = useSharedLazyHashMetrics ? "parallel_shared" : "demand",
            SourceSurfaceScanMs = estimationSnapshot?.SourceSurfaceScanMs ?? 0L,
            SourceSurfaceChartFileCount = estimationSnapshot?.SourceSurfaceChartFileCount ?? 0,
            SourceSurfaceResourceFileCount = estimationSnapshot?.SourceSurfaceResourceFileCount ?? 0,
            SourceSurfaceTrackedFileCount = estimationSnapshot?.SourceSurfaceTrackedFileCount ?? 0,
            SourceSurfaceHashMaterializeMs = estimationSnapshot?.SourceSurfaceHashMaterializeMs ?? 0L,
            SourceSurfaceCacheHit = estimationSnapshot?.SourceSurfaceCacheHit ?? false,
            SourceSurfaceBatchHit = estimationSnapshot?.SourceSurfaceBatchHit ?? false,
            SourceSurfaceScanBackend = estimationSnapshot?.SourceSurfaceScanBackend ?? string.Empty,
            SourceSurfaceScanLimitExceeded = estimationSnapshot?.SourceSurfaceScanLimitExceeded ?? false,
            SourceSurfaceVisitedFileSystemEntryCount = estimationSnapshot?.SourceSurfaceVisitedFileSystemEntryCount ?? 0,
            SourceSurfaceMaxVisitedFileSystemEntryCount = estimationSnapshot?.SourceSurfaceMaxVisitedFileSystemEntryCount ?? 0
        };
    }

    private static void ApplyMixedInstalledDestinationAmbiguityPolicy(InstallEstimationResult result)
    {
        if (result == null || result.LowConfidenceKind != InstallEstimationLowConfidenceKind.AmbiguousCandidates)
        {
            return;
        }
        result.Confidence = InstallEstimationConfidence.Low;
        if (result.ShouldAutoApplyDestination
            && result.SelectedCandidateMetadataEvidenceStrong
            && !string.IsNullOrWhiteSpace(result.DestinationDirectory))
        {
            result.LowConfidenceKind = InstallEstimationLowConfidenceKind.InstalledDestinationAutoAppliedAmbiguous;
            result.ConfidenceReason = "installed_destination_multiple_viable_candidates_auto_apply_enabled";
            return;
        }

        result.LowConfidenceKind = InstallEstimationLowConfidenceKind.InstalledDestinationAmbiguous;
        result.ConfidenceReason = "installed_destination_multiple_viable_candidates";
        result.DestinationDirectory = null;
        result.ShouldAutoApplyDestination = false;
    }

    private static ChartResourceSnapshot ResolvePrecomputedDefinedResources(PendingEstimateSourceBatchPackageState batchState, IReadOnlyList<PackageChartEntry> targetEntries)
    {
        if (batchState?.ChartResources == null
            || batchState.MissingEntries == null
            || targetEntries == null
            || batchState.MissingEntries.Count != targetEntries.Count)
        {
            return null;
        }

        for (int i = 0; i < targetEntries.Count; i++)
        {
            if (!ReferenceEquals(batchState.MissingEntries[i], targetEntries[i]))
            {
                return null;
            }
        }

        return batchState.ChartResources;
    }

    private void LogInstallEstimationEvaluation(InstallEstimationEvaluationData estimationData)
    {
        InstallEstimationResult result = estimationData?.Result;
        if (result == null)
        {
            return;
        }
        if (!estimationData.SourceSurfaceCacheHit && !estimationData.SourceSurfaceBatchHit && estimationData.SourceSurfaceTrackedFileCount > 0)
        {
            LogInstallPerformance("package_surface_build backend=" + (estimationData.SourceSurfaceScanBackend ?? string.Empty) + " trackedFileCount=" + estimationData.SourceSurfaceTrackedFileCount + " chartFileCount=" + estimationData.SourceSurfaceChartFileCount + " resourceFileCount=" + estimationData.SourceSurfaceResourceFileCount + " scanMs=" + estimationData.SourceSurfaceScanMs + " hashMaterializeMs=" + estimationData.SourceSurfaceHashMaterializeMs);
        }
        LogInstallPerformance("estimate_install start chartCount=" + estimationData.ChartCount + " targetHashes=" + result.TargetResourceHashCount + " targetResources=" + result.TargetResourceCount + " pathAwareRefs=" + result.TargetPathAwareHashCount + " pathAwareAudioRefs=" + result.TargetPathAwareAudioHashCount + " pathAwareVisualRefs=" + result.TargetPathAwareVisualHashCount + " pathAwareMovieRefs=" + result.TargetPathAwareMovieHashCount + " pathAwareOptionalRefs=" + result.TargetPathAwareOptionalImageHashCount + " bundledAudioCount=" + result.BundledAudioCount + " bundledImageCount=" + result.BundledImageCount + " bundledMovieCount=" + result.BundledMovieCount + " evalMode=relative_strict candidateMode=" + (result.CandidateMode ?? string.Empty) + " coarseFilterMode=" + (result.CoarseFilterMode ?? string.Empty) + " candidateDegree=" + result.CandidateEvaluationDegree + " audioRefs=" + result.AudioReferenceCount + " visualRefs=" + result.VisualReferenceCount + " movieRefs=" + result.MovieReferenceCount + " optionalRefs=" + result.OptionalImageReferenceCount + " audioMinMatchRequired=" + result.AudioMinimumMatchRequired + " candidateDirsBefore=" + result.CandidateDirectoryCountBeforeHashFilter + " candidateDirsAfterBroadFilter=" + result.CandidateDirectoryCountAfterBroadFilter + " candidateDirsAfterAudioGate=" + result.CandidateDirectoryCountAfterAudioGate + " candidateDirsInHierarchy=" + result.HierarchyCandidateDirectoryCount + " shadowSuppressed=" + result.AncestorShadowSuppressedCount + " lazySelfOwnedCandidates=" + result.LazySelfOwnedEvaluationCount + " shadowMs=" + result.AncestorShadowEvaluationMs + " candidateViewBuildMs=" + result.CandidateViewBuildMs + " candidateMatchMs=" + result.CandidateMatchMs + " candidateViewBuildCount=" + result.CandidateViewBuildCount + " sourceSurfaceScanMs=" + estimationData.SourceSurfaceScanMs + " sourceSurfaceChartFileCount=" + estimationData.SourceSurfaceChartFileCount + " sourceSurfaceResourceFileCount=" + estimationData.SourceSurfaceResourceFileCount + " sourceSurfaceTrackedFileCount=" + estimationData.SourceSurfaceTrackedFileCount + " sourceSurfaceHashMaterializeMs=" + estimationData.SourceSurfaceHashMaterializeMs + " sourceSurfaceCacheHit=" + estimationData.SourceSurfaceCacheHit.ToString().ToLowerInvariant() + " sourceSurfaceBatchHit=" + estimationData.SourceSurfaceBatchHit.ToString().ToLowerInvariant() + " sourceSurfaceScanBackend=" + (estimationData.SourceSurfaceScanBackend ?? string.Empty) + " candidateDirsAfter=" + result.CandidateDirectoryCountAfterHashFilter + " candidateDirs=" + result.CandidateDirectoryCount + " evaluationMs=" + result.EvaluationMs + " confidence=" + result.Confidence + " autoApplied=" + result.ShouldAutoApplyDestination + " confidenceReason=" + (result.ConfidenceReason ?? string.Empty) + " lazyHashBuildMsDelta=" + estimationData.LazyHashBuildMsDelta + " lazyHashEntriesAdded=" + estimationData.LazyHashEntriesAdded + " lazyHashLookupCountDelta=" + estimationData.LazyHashLookupCountDelta + " lazyHashBuildReason=" + (estimationData.LazyHashBuildReason ?? string.Empty) + " summary=" + (result.ResourceSummary ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(result.TopCandidateSummary))
        {
            LogInstallPerformance("estimate_install candidates " + result.TopCandidateSummary);
        }
        if (!string.IsNullOrWhiteSpace(result.SelectedCandidateSummary))
        {
            LogInstallPerformance("estimate_install selected " + result.SelectedCandidateSummary + " dst=" + (result.DestinationDirectory ?? "(none)") + " second=" + (result.SecondCandidate?.DirectoryPath ?? "(none)"));
        }
        if (!string.IsNullOrWhiteSpace(result.MetadataFrontierSummary))
        {
            LogInstallPerformance("estimate_install metadata_frontier " + result.MetadataFrontierSummary);
        }
        if (!string.IsNullOrWhiteSpace(result.MetadataTieBreakSummary))
        {
            LogInstallPerformance("estimate_install metadata_tiebreak " + result.MetadataTieBreakSummary);
        }
        if (!string.IsNullOrWhiteSpace(result.MetadataValidationSummary))
        {
            LogInstallPerformance("estimate_install metadata_validation " + result.MetadataValidationSummary);
        }
        if (!string.IsNullOrWhiteSpace(result.DirectoryHashCountTieBreakSummary))
        {
            LogInstallPerformance("estimate_install directory_hash_count_tiebreak " + result.DirectoryHashCountTieBreakSummary);
        }
    }

    private void LogSourceSurfaceScanLimitExceeded(InstallEstimationEvaluationData estimationData, string packagePath)
    {
        if (estimationData == null)
        {
            return;
        }

        LogInstallPerformance("package_surface_scan_limit_exceeded package=" + (packagePath ?? string.Empty)
            + " backend=" + (estimationData.SourceSurfaceScanBackend ?? string.Empty)
            + " visitedEntries=" + estimationData.SourceSurfaceVisitedFileSystemEntryCount
            + " maxVisitedEntries=" + estimationData.SourceSurfaceMaxVisitedFileSystemEntryCount
            + " trackedFileCount=" + estimationData.SourceSurfaceTrackedFileCount
            + " chartFileCount=" + estimationData.SourceSurfaceChartFileCount
            + " resourceFileCount=" + estimationData.SourceSurfaceResourceFileCount
            + " scanMs=" + estimationData.SourceSurfaceScanMs);
    }

    /// <summary>
    /// 指定された chart file 群について、インストール先ディレクトリを再探索し、正しいパスを設定します。
    /// </summary>
    internal void SearchCorrectInstallationDirectoryCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        CreateInstallEstimationService().CorrectChartInstallationDirectory(entries, delegate (PackageChartEntry chartEntry)
        {
            SearchEstimatedInstallationDirectoryForChartsCore(null, [chartEntry], asParallel: false, ChartInstallationEstimateMode.ReinstallCorrection);
        });
    }

    internal void RemoveInstallDestination(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
                CreateInstallEstimationService().ClearInstallDestinations(entries);
            }
        }
    }

    internal bool SetPendingInstallDestination(PackageChartEntry targetEntry, string destinationDirectory)
    {
        if (targetEntry == null)
        {
            throw new ArgumentNullException("targetEntry");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                bool allowStandaloneLibraryChart;
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    allowStandaloneLibraryChart = IsKnownLibraryChartUnsafe(targetEntry.Chart);
                }
                PendingInstallDestinationSelectionResult selection = CreateInstallEstimationService().ValidateInstallDestination(targetEntry, ChartPackagesPending, CreateKnownChartDirectorySnapshotUnsafe(), destinationDirectory, allowStandaloneLibraryChart);
                if (!selection.Success)
                {
                    ShowOperationDialog(selection.WarningMessage, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                bool preserveAmbiguousInstallContext = !string.IsNullOrWhiteSpace(selection.ValidatedDestinationDirectory)
                    && selection.TargetEntries.Any(entry => entry != null
                            && entry.HasLowConfidenceInstallEstimationWarning()
                            && entry.HasInstallDestinationSuggestion(selection.ValidatedDestinationDirectory));
                ApplyResolvedInstallDestinationToEntries(selection.TargetEntries, selection.ValidatedDestinationDirectory, preserveAmbiguousInstallContext);
                ClearDeferredEstimateReasonForEntriesUnsafe(selection.TargetEntries);
                return true;
            }
        }
    }

    private bool IsKnownLibraryChartUnsafe(ChartFile chart)
    {
        if (chart == null)
        {
            return false;
        }
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return catalogOwnedCollectionOwner.Collection.ContainsKnownChart(chart);
        }
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<string> installedDirectories = [.. GetDistinctInstalledDirectoriesByPrimaryHashUnsafe(hash)
                    .Where(dir => !string.IsNullOrWhiteSpace(dir) && LongPathFileSystem.DirectoryExists(dir))
                    .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)];
                if (installedDirectories.Count == 0)
                {
                    return false;
                }
                installDir = installedDirectories[0];
                return true;
            }
        }
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(string md5, string sha256)
    {
        return playlistReferenceOwner.Find(md5, sha256);
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(ChartFile chart)
    {
        return playlistReferenceOwner.Find(chart);
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(LibraryChartRef chart)
    {
        return playlistReferenceOwner.Find(chart);
    }

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        PlaylistReferenceTableSnapshot tableSnapshot = SnapshotPlaylistReferenceTable(table, entries);
        PlaylistReferenceLookupKeys lookupKeys = playlistReferenceOwner.BuildReferenceLookupKeys(tableSnapshot);
        playlistReferenceOwner.AddReferenceBMSTable(
            tableSnapshot,
            lookupKeys,
            SnapshotLibraryChartRefsForPlaylistReferenceApply(lookupKeys),
            SnapshotPendingChartEntriesForPlaylistReferenceApply(lookupKeys),
            LogInstallPerformance);
    }

    public void AddReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        List<PlaylistReferenceTableSnapshot> tableSnapshots = SnapshotPlaylistReferenceTables(tables);
        if (tableSnapshots.Count == 0)
        {
            return;
        }
        PlaylistReferenceLookupKeys lookupKeys = playlistReferenceOwner.BuildReferenceLookupKeys(tableSnapshots);
        playlistReferenceOwner.AddReferenceBMSTables(
            tableSnapshots,
            lookupKeys,
            SnapshotLibraryChartRefsForPlaylistReferenceApply(lookupKeys),
            SnapshotPendingChartEntriesForPlaylistReferenceApply(lookupKeys),
            LogInstallPerformance);
    }

    public void AddReferenceBMSTablesIncremental(IEnumerable<BMSTable> tables)
    {
        List<PlaylistReferenceTableSnapshot> tableSnapshots = SnapshotPlaylistReferenceTables(tables)
            .GroupBy(snapshot => snapshot.Table)
            .Select(group => group.First())
            .ToList();
        if (tableSnapshots.Count == 0)
        {
            return;
        }
        PlaylistReferenceLookupKeys lookupKeys = playlistReferenceOwner.BuildReferenceLookupKeys(tableSnapshots);
        playlistReferenceOwner.AddReferenceBMSTablesIncremental(
            tableSnapshots,
            lookupKeys,
            SnapshotLibraryChartRefsForPlaylistReferenceApply(lookupKeys),
            SnapshotPendingChartEntriesForPlaylistReferenceApply(lookupKeys),
            LogInstallPerformance);
    }

    /// <summary>
    /// Applies loaded playlist references to package chart entries after package install without materializing unmatched bmson entries.
    /// </summary>
    /// <param name="tables">Loaded playlist tables whose entries should be matched by chart hash.</param>
    /// <param name="packages">Packages containing newly installed chart entries.</param>
    public void AddReferenceBMSTablesToPackageCharts(IEnumerable<BMSTable> tables, IEnumerable<ChartPackage> packages)
    {
        List<PlaylistReferenceTableSnapshot> tableSnapshots = SnapshotPlaylistReferenceTables(tables);
        List<ChartPackage> packageList = [.. (packages ?? []).Where(package => package != null)];
        if (tableSnapshots.Count == 0 || packageList.Count == 0)
        {
            return;
        }
        PlaylistReferenceLookupKeys lookupKeys = playlistReferenceOwner.BuildReferenceLookupKeys(tableSnapshots);
        playlistReferenceOwner.AddReferenceBMSTablesToPackageCharts(
            tableSnapshots,
            lookupKeys,
            SnapshotPackageChartEntriesForPlaylistReferenceApply(packageList, lookupKeys),
            packageList.Count,
            LogInstallPerformance);
    }

    internal void ReplaceReferenceBMSTable(BMSTable oldTable, BMSTable newTable, IEnumerable<BMSTableEntry> oldEntries = null, IEnumerable<BMSTableEntry> newEntries = null)
    {
        PlaylistReferenceTableSnapshot oldTableSnapshot = SnapshotPlaylistReferenceTable(oldTable, oldEntries);
        PlaylistReferenceTableSnapshot newTableSnapshot = SnapshotPlaylistReferenceTable(newTable, newEntries);
        BmsLibraryPlaylistReferenceOwner.BuildPlaylistReferenceHashSets(oldTableSnapshot?.Entries, out HashSet<string> oldMd5Hashes, out HashSet<string> oldSha256Hashes);
        BmsLibraryPlaylistReferenceOwner.BuildPlaylistReferenceHashSets(newTableSnapshot?.Entries, out HashSet<string> newMd5Hashes, out HashSet<string> newSha256Hashes);
        playlistReferenceOwner.ReplaceReferenceBMSTable(
            oldTableSnapshot,
            newTableSnapshot,
            SnapshotLibraryChartRefsForPlaylistReferenceApply(
                CreateCombinedPlaylistReferenceHashSet(oldMd5Hashes, newMd5Hashes),
                CreateCombinedPlaylistReferenceHashSet(oldSha256Hashes, newSha256Hashes)),
            SnapshotPendingChartEntriesForPlaylistReferenceApply(),
            LogInstallPerformance);
    }

    internal void AddReferenceBMSTablesToCharts(BMSTable table, IEnumerable<ChartFile> charts)
    {
        playlistReferenceOwner.AddReferenceBMSTablesToCharts(SnapshotPlaylistReferenceTable(table));
    }

    internal void RefreshReferenceDisplayForTable(BMSTable table)
    {
        playlistReferenceOwner.RefreshReferenceDisplayForTable(SnapshotPlaylistReferenceTable(table));
    }

    internal void SynchronizeReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        playlistReferenceOwner.SynchronizeReferenceBMSTables(SnapshotPlaylistReferenceTables(tables));
    }

    internal BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan
        PrepareReferenceBMSTableSynchronization(IEnumerable<BMSTable> tables)
    {
        long baseRevision = playlistReferenceOwner.CaptureReferenceIndexRevision();
        List<PlaylistReferenceTableSnapshot> snapshots = SnapshotPlaylistReferenceTables(tables);
        return playlistReferenceOwner.PrepareReferenceBMSTableSynchronization(
            baseRevision,
            snapshots);
    }

    internal bool TryCommitReferenceBMSTableSynchronization(
        BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan plan)
    {
        return playlistReferenceOwner.TryCommitReferenceBMSTableSynchronization(plan);
    }

    /// <summary>
    /// 指定されたプレイリストの参照を BMS ファイル群から削除します。
    /// </summary>
    public void RemoveReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        playlistReferenceOwner.RemoveReferenceBMSTables(
            SnapshotPlaylistReferenceTable(table),
            entries == null);
    }

    public void RemoveReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        playlistReferenceOwner.RemoveReferenceBMSTables(SnapshotPlaylistReferenceTables(tables, requireLoaded: false));
    }

    internal List<string> GetPlaylistOrgMd5sForChart(ChartFile chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return [];
        }
        return GetPlaylistPackageMd5sByDirectory(DirectoryExt.GetDirectoryNameSimple(chart.Path));
    }

    internal List<string> GetPlaylistFolderOrgMd5sForCharts(IEnumerable<ChartFile> charts)
    {
        string directoryPath = (charts ?? [])
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))
            .Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path))
            .FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory));
        return string.IsNullOrWhiteSpace(directoryPath)
            ? []
            : GetPlaylistPackageMd5sByDirectory(directoryPath);
    }

    private List<string> GetPlaylistPackageMd5sByDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return [];
        }
        var md5s = new List<string>();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                foreach (BMSFile file in BMSFiles ?? [])
                {
                    if (file == null
                        || string.IsNullOrWhiteSpace(file.path)
                        || string.IsNullOrWhiteSpace(file.hash)
                        || !LR2SongDB.md5HashRegex.IsMatch(file.hash)
                        || !string.Equals(DirectoryExt.GetDirectoryNameSimple(file.path), directoryPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    md5s.Add(file.hash);
                }
                foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? [])
                {
                    if (song == null
                        || string.IsNullOrWhiteSpace(song.path)
                        || string.IsNullOrWhiteSpace(song.md5)
                        || !LR2SongDB.md5HashRegex.IsMatch(song.md5)
                        || !string.Equals(DirectoryExt.GetDirectoryNameSimple(song.path), directoryPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    md5s.Add(song.md5);
                }
            }
        }
        return [.. md5s.Distinct(StringComparer.OrdinalIgnoreCase).Select(md5 => md5.ToLowerInvariant())];
    }

    public static string GetLongestCommonChartInfo(IEnumerable<string> strings)
    {
        List<string> list = [.. (from s in strings.Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany((s1, i) => from input in strings.Skip(i + 1)
                                                                                                                                   select new string[2]
                                                                                                                                   {
                    s1.NaturalNormalizationForFileName(),
                    input.NaturalNormalizationForFileName()
                                                                                                                                   }).Select(delegate (string[] c)
                                                                                                                               {
                                                                                                                                   Match match = endKakkoRegex.Match(c[0]);
                                                                                                                                   if (match.Success && kakkoInnerRegex.IsMatch(match.Groups[0].Value))
                                                                                                                                   {
                                                                                                                                       c[0] = c[0].ReplaceFromEnd(match.Groups[0].Value, string.Empty);
                                                                                                                                   }
                                                                                                                                   return c[0].LongestCommonSubstringHeadFixed(c[1]).Trim();
                                                                                                                               })
                             where !string.IsNullOrWhiteSpace(s) && s.Length > 1
                             select s)];
        if (list.Count == 0)
        {
            list = [.. strings.Select(delegate (string s)
            {
                s = s.NaturalNormalizationForFileName().Trim();
                Match match = endKakkoRegex.Match(s);
                if (match.Success && kakkoInnerRegex.IsMatch(match.Groups[0].Value))
                {
                    s = s.ReplaceFromEnd(match.Groups[0].Value, string.Empty);
                }
                return s;
            })];
        }
        list = [.. list.Select(delegate (string s)
        {
            s = customTrimStartRegex1.Replace(s, string.Empty);
            s = customDeleteRegex1.Replace(s.Trim(), string.Empty);
            s = customTrimEndRegex1.Replace(s, string.Empty);
            s = customTrimEndRegex2.Replace(s, string.Empty);
            s = s.ReplaceInvalidFileNameCharsByWide();
            s = customMatchRegex1.Replace(s, m => m.Value.Trim());
            s = customMatchRegex2.Replace(s, m => "[" + m.Groups[1].Value.Trim() + "]");
            s = doubleSpacesRegex.Replace(s, string.Empty).Trim();
            return s;
        })];
        return (from s in list
                group s by s into g
                orderby Math.Pow(g.Key.Length, 0.65) * (double)g.Count() descending
                select g.Key).FirstOrDefault();
    }

    private string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDir)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        int maxFolderNameBytes = 128;
        var encoding = Encoding.GetEncoding("Shift_JIS");
        string commonTitle = GetLongestCommonChartInfo((chartFiles ?? []).Select(f => f?.Title ?? string.Empty));
        string commonArtist = GetLongestCommonChartInfo((chartFiles ?? []).Select(f => f?.Artist ?? string.Empty));
        string s = options.FolderNameFormat.Replace("%ARTIST%", commonArtist).Replace("%TITLE%", commonTitle).Trim();
        if (options.UseOnlyShiftJISChars)
        {
            s = s.ToSjisSchemeString();
        }
        s = s.RemoveInvalidFileNameChars();
        if (string.IsNullOrWhiteSpace(s))
        {
            s = Resources.NewFolderName;
        }
        while (encoding.GetByteCount(s) > maxFolderNameBytes && s.Length > 1)
        {
            s = s.Substring(0, s.Length - 1);
        }
        return Path.Combine(parentDir, s).Trim();
    }

    /// <summary>
    /// 譜面ファイル群を指定された別のディレクトリへマージ（統合移動）します。
    /// </summary>
    public void MergeChartDirectory(string src, string dst)
    {
        _ = MergeChartDirectory(src, dst, operationId: 0);
    }

    /// <summary>
    /// Executes the internal duplicate-folder merge route and returns immutable maintenance facts.
    /// </summary>
    /// <param name="src">Source directory.</param>
    /// <param name="dst">Destination directory.</param>
    /// <param name="operationId">Operation identifier used by the merge lock boundary.</param>
    /// <param name="reportAtTerminal">Whether the caller owns receipt-backed failure notification.</param>
    /// <returns>Merge and intermediate resource-health dispatch facts.</returns>
    internal DuplicateMergeMaintenanceReceipt MergeChartDirectory(string src, string dst, long operationId,
        bool reportAtTerminal = false)
    {
        return libraryFileOperationOwner.MergeChartDirectory(src, dst, operationId, reportAtTerminal);
    }

    private IEnumerable<string> GetDuplicateInstallRepairPaths(ChartFile chart)
    {
        string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndex.GetPathsByPrimaryHash(lookupHash)
                .Where(path => !string.Equals(path, chart.Path, StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }
    }

    internal List<DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(IEnumerable<ChartFile> charts)
    {
        if (charts == null)
        {
            throw new ArgumentNullException("charts");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<DuplicateInstallRepairConfirmation> confirmations = [];
                foreach (ChartFile chart in charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination)))
                {
                    string[] duplicatePaths = [.. GetDuplicateInstallRepairPaths(chart)];
                    if (duplicatePaths.Length > 0)
                    {
                        confirmations.Add(new DuplicateInstallRepairConfirmation(chart, duplicatePaths));
                    }
                }
                return confirmations;
            }
        }
    }

    /// <summary>Returns repair movement, deletion, and terminal facts without retrying or inferring filesystem state.</summary>
    internal LibraryFixInstallationResult FixInstallationDirectoryCharts(IEnumerable<ChartFile> charts, IEnumerable<string> approvedDuplicateRemovalChartPaths = null)
    {
        return libraryFileOperationOwner.FixInstallationDirectoryCharts(charts, approvedDuplicateRemovalChartPaths);
    }

    /// <summary>
    /// 譜面ファイル群のフォルダ名をメタデータに基づいて自動リネームします。
    /// </summary>
    internal void AutoRenameChartFolders(IEnumerable<ChartFile> chartFiles, bool renameRootFolder = false, Action<int, int, string> progressReporter = null)
    {
        AutoRenameBatchResult result = AutoRenameChartFoldersWithResult(chartFiles, renameRootFolder, progressReporter);
        if (result?.HasDurableFinalizationFailure == true)
        {
            result.PrimaryFailure?.Throw();
        }
    }

    /// <summary>Returns batch facts; terminal-owned callers suppress receipt-backed item dialogs.</summary>
    internal AutoRenameBatchResult AutoRenameChartFoldersWithResult(
        IEnumerable<ChartFile> chartFiles,
        bool renameRootFolder = false,
        Action<int, int, string> progressReporter = null,
        bool reportAtTerminal = false)
    {
        if (chartFiles == null)
        {
            throw new ArgumentNullException("chartFiles");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(AutoRenameChartFolders)))
        {
            return new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
        }

        Tuple<int, int, string> latestProgressReport = null;
        Action<int, int, string> deferredProgressReporter = progressReporter == null
            ? null
            : (total, processed, currentPath) => latestProgressReport = Tuple.Create(total, processed, currentPath);
        AutoRenameBatchResult result = null;
        ExceptionDispatchInfo primaryFailure = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            try
            {
                libraryFileOperationOwner.RunWithFolderMoveWriteLocks(
                    mutationCapability =>
                    {
                        List<FolderAutoRenamePlan> plans = null;
                        libraryFileOperationOwner.RunWithFolderMoveSnapshotLocks(
                            () => plans = libraryFileOperationOwner.BuildAutoRenamePlans(
                                chartFiles.Where(chart => chart != null),
                                getBMSDirectories(),
                                renameRootFolder));
                        result = libraryFileOperationOwner.ApplyAutoRenamePlansWithReceipt(
                            plans,
                            deferredProgressReporter,
                            postLeaseNotifications);
                        SyncAutoRenameLr2NormalFoldersUnderExistingLease(result, mutationCapability);
                    });
            }
            catch (Exception exception)
            {
                primaryFailure = ExceptionDispatchInfo.Capture(exception);
                if (result?.HasDurableCommit == true)
                {
                    result = result.WithDurableFinalizationFailure(primaryFailure);
                }
            }
            FlushAutoRenamePostCommitEffects(result, primaryFailure, postLeaseNotifications, reportAtTerminal);
        }
        finally
        {
            FlushAutoRenameProgressReport(progressReporter, latestProgressReport);
        }
        return result ?? new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
    }

    internal bool HasAutoRenameAllChartFolderTargets(string parentDir = null)
    {
        bool hasTargets = false;
        libraryFileOperationOwner.RunWithFolderMoveReadLocks(
            () => hasTargets = HasActionableAutoRenamePlan(CreateAutoRenameAllChartFolderPlansUnsafe(parentDir)));
        return hasTargets;
    }

    internal bool AutoRenameAllChartFolders(string parentDir = null, Action<int, int, string> progressReporter = null)
    {
        AutoRenameBatchResult result = AutoRenameAllChartFoldersWithResult(parentDir, progressReporter);
        return result.HasActionablePlan && !result.HasDurableFinalizationFailure;
    }

    /// <summary>Returns batch facts; terminal-owned callers suppress receipt-backed item dialogs.</summary>
    internal AutoRenameBatchResult AutoRenameAllChartFoldersWithResult(
        string parentDir = null,
        Action<int, int, string> progressReporter = null,
        bool reportAtTerminal = false)
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(AutoRenameAllChartFolders)))
        {
            return new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
        }
        Tuple<int, int, string> latestProgressReport = null;
        Action<int, int, string> deferredProgressReporter = progressReporter == null
            ? null
            : (total, processed, currentPath) => latestProgressReport = Tuple.Create(total, processed, currentPath);
        AutoRenameBatchResult result = null;
        ExceptionDispatchInfo primaryFailure = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            try
            {
                libraryFileOperationOwner.RunWithFolderMoveWriteLocks(mutationCapability =>
                {
                    List<FolderAutoRenamePlan> plans = null;
                    libraryFileOperationOwner.RunWithFolderMoveSnapshotLocks(
                        () => plans = CreateAutoRenameAllChartFolderPlansUnsafe(parentDir));
                    if (HasActionableAutoRenamePlan(plans))
                    {
                        result = libraryFileOperationOwner.ApplyAutoRenamePlansWithReceipt(
                            plans,
                            deferredProgressReporter,
                            postLeaseNotifications);
                        SyncAutoRenameLr2NormalFoldersUnderExistingLease(result, mutationCapability);
                    }
                });
            }
            catch (Exception exception)
            {
                primaryFailure = ExceptionDispatchInfo.Capture(exception);
                if (result?.HasDurableCommit == true)
                {
                    result = result.WithDurableFinalizationFailure(primaryFailure);
                }
            }
            FlushAutoRenamePostCommitEffects(result, primaryFailure, postLeaseNotifications, reportAtTerminal);
            return result ?? new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
        }
        finally
        {
            FlushAutoRenameProgressReport(progressReporter, latestProgressReport);
        }
    }

    private List<FolderAutoRenamePlan> CreateAutoRenameAllChartFolderPlansUnsafe(string parentDir)
    {
        return libraryFileOperationOwner.BuildAutoRenamePlansForSourceFolders(parentDir);
    }

    private static bool HasActionableAutoRenamePlan(IEnumerable<FolderAutoRenamePlan> plans)
    {
        return (plans ?? []).Any(plan => !string.IsNullOrWhiteSpace(plan?.SourceDirectory)
            && !string.IsNullOrWhiteSpace(plan.DestinationDirectory));
    }

    private static void FlushAutoRenameProgressReport(
        Action<int, int, string> progressReporter,
        Tuple<int, int, string> report)
    {
        if (progressReporter == null || report == null)
        {
            return;
        }
        ReportAutoRenameProgress(progressReporter, report.Item1, report.Item2, report.Item3);
    }

    private void FlushAutoRenamePostCommitEffects(
        AutoRenameBatchResult result,
        ExceptionDispatchInfo primaryFailure,
        IEnumerable<Action> postLeaseNotifications,
        bool reportAtTerminal = false)
    {
        ExceptionDispatchInfo firstFailure = primaryFailure ?? result?.PrimaryFailure;
        if (firstFailure != null
            && result?.Lr2NormalFolderPathChanges?.Count > 0
            && CurrentOptionsSnapshot?.OperationModeLR2DB == true)
        {
            try
            {
                string failureDetail = GetDisplayedExceptionMessage(firstFailure.SourceException)
                    .Replace(Environment.NewLine, " | ");
                lr2SynchronizationOwner.MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                    CurrentOptionsSnapshot,
                    stage: "lr2_auto_rename_catalog_sync_incomplete",
                    detail: "lr2_auto_rename_catalog_sync_incomplete: " + failureDetail,
                    logReason: "auto_rename_folders");
            }
            catch (Exception exception)
            {
                LogAutoRenameDiagnosticFailure(
                    exception,
                    "auto_rename_lr2_incomplete_publish_failed_after_primary_failure");
            }
        }
        // A batch-level finalizer can fail after every individual mutation has
        // become durable.  Its queued success publications describe a result
        // that is no longer an ordinary success, so discard them while still
        // retaining diagnostics and the typed durable receipt.
        if (result?.MutationReceipt?.FinalizationFailure == null)
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        FlushAutoRenameDiagnostics(result, reportAtTerminal);
        if (firstFailure == null && result?.AppliedPlanCount > 0)
        {
            TryInvokePostLeaseNotification(
                PublishAutoRenameBatchRefreshNotification,
                "auto_rename_batch_refresh_notification_failed");
        }
        if (firstFailure != null && result?.HasDurableFinalizationFailure != true
            && !(reportAtTerminal && result?.MutationReceipt != null))
        {
            firstFailure.Throw();
        }
    }

    private void FlushAutoRenameDiagnostics(AutoRenameBatchResult result, bool reportAtTerminal)
    {
        foreach (AutoRenameBatchDiagnostic diagnostic in result?.Diagnostics ?? [])
        {
            if (diagnostic == null)
            {
                continue;
            }
            try
            {
                PublishAutoRenameDiagnostic(diagnostic, reportAtTerminal);
            }
            catch (Exception exception)
            {
                LogAutoRenameDiagnosticFailure(
                    exception,
                    "auto_rename_diagnostic_publish_failed kind=" + diagnostic.Kind);
            }
        }
    }

    private static void LogAutoRenameDiagnosticFailure(Exception exception, string message)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(exception, message);
        }
        catch
        {
            // Diagnostic publication must never replace an operation result or
            // its primary exception.
        }
    }

    private void PublishAutoRenameDiagnostic(AutoRenameBatchDiagnostic diagnostic, bool reportAtTerminal)
    {
        switch (diagnostic.Kind)
        {
            case AutoRenameBatchDiagnosticKind.PerformanceLog:
                if (!string.IsNullOrWhiteSpace(diagnostic.Message))
                {
                    libraryFileOperationOwner.LogInstallPerformance(diagnostic.Message);
                }
                break;
            case AutoRenameBatchDiagnosticKind.DriveRootSkipped:
                libraryFileOperationOwner.ShowDriveRootBmsSkipped();
                break;
            case AutoRenameBatchDiagnosticKind.RenamePlanFailed:
                if (!string.IsNullOrWhiteSpace(diagnostic.SourceDirectory)
                    && diagnostic.Failure != null)
                {
                    libraryFileOperationOwner.ShowRenameFailed(new FolderAutoRenamePlan
                    {
                        SourceDirectory = diagnostic.SourceDirectory,
                        FailureException = diagnostic.Failure
                    });
                }
                break;
            case AutoRenameBatchDiagnosticKind.SourceMissing:
                if (!string.IsNullOrWhiteSpace(diagnostic.SourceDirectory))
                {
                    libraryFileOperationOwner.ShowRenameFolderNotExists(diagnostic.SourceDirectory);
                }
                break;
            case AutoRenameBatchDiagnosticKind.DestinationAlreadyExists:
                if (!string.IsNullOrWhiteSpace(diagnostic.SourceDirectory)
                    && !string.IsNullOrWhiteSpace(diagnostic.DestinationDirectory))
                {
                    libraryFileOperationOwner.ShowMoveDestinationAlreadyExists(
                        diagnostic.SourceDirectory,
                        diagnostic.DestinationDirectory);
                }
                break;
            case AutoRenameBatchDiagnosticKind.MoveFailed:
                if (!reportAtTerminal && !string.IsNullOrWhiteSpace(diagnostic.SourceDirectory)
                    && !string.IsNullOrWhiteSpace(diagnostic.DestinationDirectory))
                {
                    libraryFileOperationOwner.ShowFolderMoveFailed(
                        diagnostic.SourceDirectory,
                        diagnostic.DestinationDirectory,
                        diagnostic.Failure ?? new IOException("Folder move failed."));
                }
                break;
            case AutoRenameBatchDiagnosticKind.ProgressReportFailed:
                NLogWrapper.FileLogger?.Warn(
                    diagnostic.Failure,
                    "auto_rename_progress_report_failed processed=" + diagnostic.Processed
                    + " total=" + diagnostic.Total);
                break;
        }
    }

    private void SyncAutoRenameLr2NormalFoldersUnderExistingLease(
        AutoRenameBatchResult result,
        LibraryFileMutationCapability mutationCapability)
    {
        if (result?.HasOperationFailure == true
            || result?.Lr2NormalFolderPathChanges?.Count <= 0
            || CurrentOptionsSnapshot?.OperationModeLR2DB != true)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(mutationCapability);
        // This snapshot is intentionally captured while the original batch
        // capability is still valid.  The deferred catalog effect has not yet
        // applied its live path projection, so project each moved path before
        // handing the immutable LR2 receipt to the owner.
        Lr2NormalFolderCurrentBmsSnapshot currentSnapshot =
            CreateAutoRenameLr2NormalFolderCurrentBmsSnapshotUnsafe();
        IReadOnlyList<string> currentBmsChartPaths = ProjectAutoRenameCurrentBmsChartPaths(
            currentSnapshot?.CurrentBmsChartPaths,
            result.Lr2NormalFolderPathChanges);
        lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation(
            new Lr2NormalFolderCatalogMutationReceipt(
                currentSnapshot?.OwnedCollectionVersion ?? 0,
                [],
                [],
                result.Lr2NormalFolderPathChanges,
                currentBmsChartPaths),
            "auto_rename_folders",
            mutationCapability);
    }

    private static IReadOnlyList<string> ProjectAutoRenameCurrentBmsChartPaths(
        IEnumerable<string> currentBmsChartPaths,
        IEnumerable<Lr2NormalFolderPathChange> pathChanges)
    {
        Dictionary<string, string> replacements = (pathChanges ?? [])
            .Where(change => change != null
                && !string.IsNullOrWhiteSpace(change.OldPath)
                && !string.IsNullOrWhiteSpace(change.NewPath))
            .GroupBy(change => change.OldPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().NewPath,
                StringComparer.OrdinalIgnoreCase);
        return (currentBmsChartPaths ?? [])
            .Select(path => replacements.TryGetValue(path, out string replacement) ? replacement : path)
            .ToArray();
    }

    private Lr2NormalFolderCurrentBmsSnapshot CreateAutoRenameLr2NormalFolderCurrentBmsSnapshotUnsafe()
    {
        lock (lockOwnedChartCollection)
        {
            return new Lr2NormalFolderCurrentBmsSnapshot(
                OwnedChartCollectionVersion,
                catalogOwnedCollectionOwner.IsInitialized
                    ? catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefIndexSnapshot().GetCurrentBmsChartPaths()
                    : []);
        }
    }

    private void PublishAutoRenameBatchRefreshNotification()
    {
        normalLibraryRefreshPublisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = OwnedChartCollectionVersion,
            Effects = LibraryChartRefreshEffects.SourceChanged,
            InstallDestinationChangedCharts = [],
            NotifiesStorageRows = false,
            ResetsPriorNotifications = false,
            NotifiesBmsFiles = false,
            NotifiesBmsonSongs = false
        });
        RaisePropertyChanged(() => NormalLibraryRefreshNotificationVersion);
    }

    private static void ReportAutoRenameProgress(Action<int, int, string> progressReporter, int total, int processed, string currentPath)
    {
        if (progressReporter == null || total <= 0)
        {
            return;
        }
        try
        {
            progressReporter(total, Math.Max(0, Math.Min(processed, total)), currentPath ?? string.Empty);
        }
        catch (Exception ex)
        {
            LogAutoRenameDiagnosticFailure(
                ex,
                "auto_rename_progress_report_failed processed=" + processed + " total=" + total);
        }
    }

    private string NormalizeAutoRenameFolderName(string folderName)
    {
        folderName = folderName ?? string.Empty;
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (options.UseOnlyShiftJISChars)
        {
            folderName = folderName.ToSjisSchemeString();
        }
        return folderName.RemoveInvalidFileNameChars();
    }

    private List<ChartFile> CreateDirectLibraryChartSnapshotsInFolders(IReadOnlyCollection<string> folderPaths)
    {
        return CreateOwnedDirectChildChartFilesUnsafe(
            folderPaths,
            includeWarningSnapshot: true,
            includeResourceReferences: false);
    }

    /// <summary>
    /// 譜面フォルダを新しい名前にリネームし、song.db のパス情報を更新します。
    /// </summary>
    public void RenameChartFolder(string srcDir, string newName, bool? unregister = false, bool renameRootFolder = false)
    {
        RenameChartFolderWithReceipt(srcDir, newName, unregister, renameRootFolder);
    }

    /// <summary>
    /// Returns folder mutation facts. An explicit terminal reporter owns receipt-backed
    /// failures only; preflight and compatibility callers retain model notifications.
    /// </summary>
    internal FileDbMutationReceipt RenameChartFolderWithReceipt(
        string srcDir,
        string newName,
        bool? unregister = false,
        bool renameRootFolder = false,
        bool reportAtTerminal = false)
    {
        if (srcDir == null)
        {
            throw new ArgumentNullException(nameof(srcDir));
        }
        if (newName == null)
        {
            throw new ArgumentNullException(nameof(newName));
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(RenameChartFolder)))
        {
            return null;
        }
        return LibraryFolderMoveCoordinator.RenameChartFolderWithReceipt(
            libraryFileOperationOwner,
            srcDir,
            newName,
            unregister,
            renameRootFolder,
            reportAtTerminal);
    }

    internal void MoveLibraryRootFolder(IEnumerable<LibraryChartRef> charts, string dstDir, bool? unregister = false)
    {
        MoveLibraryRootFolderWithReceipt(charts, dstDir, unregister);
    }

    /// <summary>Returns all folder receipts, optionally transferring their notifications to the terminal caller.</summary>
    internal FileDbMutationBatchReceipt MoveLibraryRootFolderWithReceipt(
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister = false,
        bool reportAtTerminal = false)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (dstDir == null)
        {
            throw new ArgumentNullException(nameof(dstDir));
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(MoveLibraryRootFolder)))
        {
            return new FileDbMutationBatchReceipt([]);
        }
        return LibraryFolderMoveCoordinator.MoveLibraryRootFolderWithReceipt(
            libraryFileOperationOwner,
            charts,
            dstDir,
            unregister,
            reportAtTerminal);
    }

    /// <summary>
    /// Creates a read-only install-destination overlay chart reference snapshot for diagnostics and tests.
    /// </summary>
    /// <returns>The install-destination overlay chart reference snapshot.</returns>
    internal InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshotForDiagnostics()
    {
        return installDestinationStateOwner.CreateOverlaySnapshot(out _);
    }

    private RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(BMSFile sourceFile, string requestedPath, bool removeFromLibraryOnSuccess)
    {
        _ = removeFromLibraryOnSuccess;
        return libraryFileOperationsService.ProcessInvalidExtensionRename(
            sourceFile,
            requestedPath,
            fileMutationService,
            targetOnlyFileMutationOptions,
            info => NLogWrapper.FileLogger?.Info(info),
            (ex, message) => NLogWrapper.FileLogger?.Warn(ex, message));
    }

    private string GetNonConflictingPathWithSuffix(string requestedPath)
    {
        return libraryFileOperationsService.GetNonConflictingPathWithSuffix(requestedPath);
    }

    private string TryComputeFileMd5ForPath(string filePath, string logCategory = null)
    {
        return libraryFileOperationsService.TryComputeFileMd5ForPath(filePath, logCategory, info => NLogWrapper.FileLogger?.Info(info));
    }

    internal void RenameBMSFilesExtensions(IEnumerable<ChartFile> charts, string newExt, bool? unregister = false)
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(RenameBMSFilesExtensions)))
        {
            return;
        }
        InvalidExtensionRenameCoordinator.RenameBMSFilesExtensions(
            libraryFileOperationOwner,
            charts,
            newExt,
            unregister);
    }

    internal void RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        InvalidExtensionRenameCoordinator.RenamePendingBmsFormatChartFileExtensions(
            libraryFileOperationOwner,
            charts,
            newExt);
    }

    /// <summary>
    /// 指定された chart file 群をライブラリおよびファイルシステムから削除します。
    /// </summary>
    internal List<string> GetLibraryWholeFolderDeleteConfirmationPaths(IEnumerable<LibraryChartRef> charts)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return libraryFileOperationsService.GetWholeFolderDeleteCandidatePaths(
                    charts,
                    CreateOwnedCanonicalChartLookupUnsafe());
            }
        }
    }

    /// <summary>Returns observed deletion facts without retrying or inferring filesystem state.</summary>
    internal LibraryChartRemovalOutcome RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        return libraryFileOperationOwner.RemoveLibraryCharts(
            charts,
            sendToRecycleBin,
            approvedWholeFolderDeletePaths);
    }

    internal void RemovePendingCharts(IEnumerable<ChartFile> charts, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        libraryFileOperationOwner.RemovePendingCharts(
            charts,
            sendToRecycleBin,
            deleteContainingPackageFoldersWhenNoBms);
    }

    internal void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        LibraryFileMutationLease mutationLease = TryBeginLr2SongDbSyncBlockedMutation(
            "ApplyLibraryMutationDelta",
            showMessage: false);
        if (mutationLease == null)
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }
        List<Action> postLeaseNotifications = [];
        using (mutationLease)
        using (LibraryFileMutationCapability mutationCapability = mutationLease.CreateMutationCapability())
        {
            mutationCapability.Validate(lr2SynchronizationOwner);
            ApplyLibraryMutationDeltaCore(
                delta,
                performanceLogContext: null,
                onDurableCommit: null,
                suppressNormalRefreshNotification: false,
                suppressLr2NormalFolderSync: false,
                mutationCapability: mutationCapability,
                postLeaseNotificationObserver: action => postLeaseNotifications.Add(action));
        }
        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
    }

    private void InvokePostLeaseNotificationsBestEffort(IEnumerable<Action> notifications)
    {
        foreach (Action notification in notifications ?? [])
        {
            TryInvokePostLeaseNotification(notification, "library_mutation_post_lease_notification_failed");
        }
    }

    private void TryInvokePostLeaseNotification(Action notification, string diagnostic)
    {
        if (notification == null)
        {
            return;
        }
        try
        {
            notification();
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Warn(exception, diagnostic);
        }
    }

    /// <summary>
    /// Applies a catalog delta under an already-owned file mutation lease.
    /// Callers must pass the capability issued by that lease. Standalone
    /// catalog mutations enter through <see cref="ApplyLibraryMutationDelta"/>.
    /// </summary>
    private FileDbMutationCommitResult ApplyLibraryMutationDeltaForFileMutationUnderExistingLease(
        LibraryMutationDelta delta,
        string reason,
        bool suppressNormalRefreshNotification,
        bool suppressLr2NormalFolderSync,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> postLeaseNotificationObserver)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);

        bool durableCommit = false;
        try
        {
            ApplyLibraryMutationDeltaCore(
                delta,
                reason,
                onDurableCommit: () => durableCommit = true,
                suppressNormalRefreshNotification: suppressNormalRefreshNotification,
                suppressLr2NormalFolderSync: suppressLr2NormalFolderSync,
                mutationCapability: mutationCapability,
                postLeaseNotificationObserver: postLeaseNotificationObserver);
            return FileDbMutationCommitResult.Durable();
        }
        catch (Exception exception)
        {
            return durableCommit
                ? FileDbMutationCommitResult.Durable(durableFailure: exception)
                : FileDbMutationCommitResult.Failed(exception);
        }
    }

    private FileDbMutationCommitResult ApplyLibraryMutationDeltaForFileMutationWithoutLr2NormalFolderSync(
        LibraryMutationDelta delta,
        string reason,
        bool suppressNormalRefreshNotification,
        Action<Action> postLeaseNotificationObserver)
    {
        bool durableCommit = false;
        try
        {
            ApplyLibraryMutationDeltaCore(
                delta,
                reason,
                onDurableCommit: () => durableCommit = true,
                suppressNormalRefreshNotification: suppressNormalRefreshNotification,
                suppressLr2NormalFolderSync: true,
                mutationCapability: null,
                postLeaseNotificationObserver: postLeaseNotificationObserver);
            return FileDbMutationCommitResult.Durable();
        }
        catch (Exception exception)
        {
            return durableCommit
                ? FileDbMutationCommitResult.Durable(durableFailure: exception)
                : FileDbMutationCommitResult.Failed(exception);
        }
    }

    private void ApplyLibraryMutationDeltaCore(
        LibraryMutationDelta delta,
        string performanceLogContext,
        Action onDurableCommit,
        bool suppressNormalRefreshNotification,
        bool suppressLr2NormalFolderSync,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> postLeaseNotificationObserver)
    {
        const string defaultReason = "library_delta";
        bool collectPerformanceLog = !string.IsNullOrWhiteSpace(performanceLogContext);
        Stopwatch totalStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
        var timings = new LibraryMutationDeltaApplyTimings();
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        OwnedChartCollectionMutationResult mutationResult = null;
        CatalogMutationReceipt catalogReceipt = null;
        bool catalogMutationExpected = false;
        bool catalogMutationCommitted = false;
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);
        if (!suppressLr2NormalFolderSync && mutationCapability == null)
        {
            throw new ArgumentNullException(nameof(mutationCapability));
        }
        try
        {
            Stopwatch resourceHealthBeginStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            timings.ResourceHealthBeginMs = StopPerformanceStepStopwatch(resourceHealthBeginStopwatch);
            try
            {
                Stopwatch buildMutationStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                mutationResult = BuildOwnedChartCollectionMutationResult(
                    delta,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                timings.BuildMutationMs = StopPerformanceStepStopwatch(buildMutationStopwatch);

                using (mutationResult?.ResourceHealthIndexInvalidated == true
                    ? resourceHealthOwner.SuppressInvalidation()
                    : null)
                {
                    Stopwatch stateApplyStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                    catalogMutationExpected = delta?.FolderPathChanges?.Count > 0
                        || delta?.ChartPathChanges?.Count > 0
                        || mutationResult?.StorageMutation?.RemoveRequests?.Count > 0
                        || mutationResult?.StorageMutation?.AddedBmsFiles?.Count > 0
                        || mutationResult?.StorageMutation?.AddedBmsonSongs?.Count > 0;
                    catalogReceipt = catalogMutationOwner.ApplyCatalogMutation(
                        delta,
                        mutationResult.StorageMutation.RemoveRequests,
                        mutationResult.StorageMutation.AddedBmsFiles,
                        mutationResult.StorageMutation.AddedBmsonSongs,
                        () =>
                        {
                            catalogMutationCommitted = true;
                            onDurableCommit?.Invoke();
                        });
                    ApplyCatalogMutationReceiptProjection(mutationResult, catalogReceipt);
                    PlaylistReferenceCatalogApplyResult playlistReferenceApplyResult = playlistReferenceOwner.ApplyCatalogMutationReceipt(
                        catalogReceipt,
                        SnapshotLibraryChartRefsForPlaylistReferenceApply(
                            catalogReceipt?.AddedCharts));
                    timings.PlaylistReferenceAffectedCharts = playlistReferenceApplyResult.AffectedChartCount;
                    timings.PlaylistReferenceMatchedCharts = playlistReferenceApplyResult.MatchedChartCount;
                    timings.StateApplyMs = StopPerformanceStepStopwatch(stateApplyStopwatch);
                    if (catalogReceipt?.Applied == true)
                    {
                        timings.StateFolderDbMs = catalogReceipt.FolderDbMs;
                        timings.StatePathMemoryApplyMs = catalogReceipt.LiveApplyMs;
                        timings.StateBmsPathDbMs = catalogReceipt.BmsPathDbMs;
                        timings.StateBmsonPathDbMs = catalogReceipt.BmsonPathDbMs;
                        timings.StateBmsRemovalDbMs = catalogReceipt.BmsRemovalDbMs;
                        timings.StateBmsonRemovalDbMs = catalogReceipt.BmsonRemovalDbMs;
                    }
                }

                Stopwatch residualApplyStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                BmsLibraryStateApplyResult residualStateApplyResult = packageLifecycleOwner.ApplyLibraryMutationDelta(
                    delta,
                    catalogReceipt?.RemovedCharts,
                    catalogReceipt?.PathFacts);
                timings.StateApplyMs += StopPerformanceStepStopwatch(residualApplyStopwatch);
                timings.StatePackageApplyMs = residualStateApplyResult?.PackageApplyMs ?? 0;
            }
            finally
            {
                Stopwatch resourceHealthDisposeStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                resourceHealthMutation.Dispose();
                timings.ResourceHealthDisposeMs = StopPerformanceStepStopwatch(resourceHealthDisposeStopwatch);
            }

            mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion ??= resourceHealthMutation.TargetInputVersion;
            if (mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion.Value < 0)
            {
                mutationResult.ResourceHealthMutation.Invalidate = true;
            }
            Stopwatch lr2NormalFolderSyncStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            if (!suppressLr2NormalFolderSync)
            {
                lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation(
                    CreateLr2NormalFolderCatalogMutationReceipt(
                        catalogReceipt,
                        mutationResult.OwnedCollectionVersion),
                    performanceLogContext ?? defaultReason,
                    mutationCapability);
            }
            timings.Lr2NormalFolderSyncMs = StopPerformanceStepStopwatch(lr2NormalFolderSyncStopwatch);
            Stopwatch dispatchStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            DispatchOwnedChartCollectionMutation(
                mutationResult,
                defaultReason,
                publishOwnedCollectionNotifications: false,
                publishNormalRefreshNotification: false);
            timings.DispatchMs = StopPerformanceStepStopwatch(dispatchStopwatch);

            Action publishNotifications = () =>
            {
                Stopwatch publishNotificationStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                TryInvokePostLeaseNotification(() =>
                {
                    if (mutationResult.ParentFolderInvalidated)
                    {
                        NotifyBMSParentFolderListCacheChanged();
                    }
                }, "library_mutation_parent_folder_notification_failed");
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "library_mutation_collection_notification_failed");
                timings.PublishNotificationMs = StopPerformanceStepStopwatch(publishNotificationStopwatch);
                if (!suppressNormalRefreshNotification)
                {
                    TryInvokePostLeaseNotification(
                        () =>
                        {
                            PublishNormalLibraryRefreshNotification(mutationResult);
                            RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                        },
                        "library_mutation_refresh_notification_failed");
                }
                if (collectPerformanceLog)
                {
                    timings.ElapsedMs = StopPerformanceStepStopwatch(totalStopwatch);
                    TryInvokePostLeaseNotification(
                        () => LogInstallPerformance("library_mutation_delta_apply context=" + performanceLogContext
                            + " unregisterCharts=" + (delta?.ChartRemoveRequests?.Count ?? 0)
                            + " pathChanges=" + (delta?.ChartPathChanges?.Count ?? 0)
                            + " folderPathChanges=" + (delta?.FolderPathChanges?.Count ?? 0)
                            + " installDestinations=" + (delta?.UpdatedInstallDestinations?.Count ?? 0)
                            + " installedPackagePaths=" + (delta?.UpdatedInstalledPackagePaths?.Count ?? 0)
                            + " resourceHealthBeginMs=" + timings.ResourceHealthBeginMs
                            + " buildMutationMs=" + timings.BuildMutationMs
                            + " publishNotificationMs=" + timings.PublishNotificationMs
                            + " stateApplyMs=" + timings.StateApplyMs
                            + " stateFolderDbMs=" + timings.StateFolderDbMs
                            + " statePathMemoryApplyMs=" + timings.StatePathMemoryApplyMs
                            + " stateBmsPathDbMs=" + timings.StateBmsPathDbMs
                            + " stateBmsonPathDbMs=" + timings.StateBmsonPathDbMs
                            + " stateBmsRemovalDbMs=" + timings.StateBmsRemovalDbMs
                            + " stateBmsonRemovalDbMs=" + timings.StateBmsonRemovalDbMs
                            + " statePackageApplyMs=" + timings.StatePackageApplyMs
                            + " playlistReferenceAffectedCharts=" + timings.PlaylistReferenceAffectedCharts
                            + " playlistReferenceMatchedCharts=" + timings.PlaylistReferenceMatchedCharts
                            + " resourceHealthDisposeMs=" + timings.ResourceHealthDisposeMs
                            + " lr2NormalFolderSyncMs=" + timings.Lr2NormalFolderSyncMs
                            + " dispatchMs=" + timings.DispatchMs
                            + " elapsedMs=" + timings.ElapsedMs),
                        "library_mutation_performance_log_failed");
                }
            };
            postLeaseNotificationObserver(publishNotifications);
        }
        catch
        {
            if (catalogMutationExpected && !catalogMutationCommitted)
            {
                resourceHealthOwner.RebaseAfterInputMutation(
                    resourceHealthMutation,
                    GetCurrentResourceHealthIndexVersion());
            }
            if (mutationResult?.ShouldDispatchInstalledLookup == true)
            {
                InvalidateInstalledDirectoryIndex();
            }
            else if (mutationResult?.InstallEstimationMetadataProfileCacheInvalidated == true)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
            if (mutationResult?.ParentFolderInvalidated == true)
            {
                InvalidateBMSParentFolderListCache();
            }
            if (mutationResult?.DuplicateCacheInvalidated == true)
            {
                InvalidateDuplicateChartGroupsCache();
            }
            if (HasOwnedHashSetChanges(mutationResult))
            {
                catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            }
            if (mutationResult?.OwnedCollectionChanged == true)
            {
                InvalidatePlaylistLibraryResolveIndexSnapshot();
            }
            if (mutationResult?.ResourceHealthMutation.HasChanges == true
                && !(catalogMutationExpected && !catalogMutationCommitted))
            {
                resourceHealthOwner.ForceInvalidate("library_delta_failed");
            }
            if (mutationResult?.InstallDestinationRuntimeStateMutation.HasChanges == true)
            {
                installDestinationStateOwner.PruneToCurrentOwnedCharts();
            }
            InvalidateOwnedChartCollection();
            if (mutationResult != null)
            {
                ClearNormalLibraryRefreshNotification(mutationResult);
            }
            throw;
        }
    }

    private void ApplyCatalogMutationReceiptProjection(
        OwnedChartCollectionMutationResult mutationResult,
        CatalogMutationReceipt receipt)
    {
        if (mutationResult == null || receipt?.Applied != true)
        {
            return;
        }

        mutationResult.AddedCount = receipt.AddedCharts.Count;
        mutationResult.RemovedCount = receipt.RemovedCharts.Count;
        mutationResult.MovedCount = receipt.MovedCharts.Count;
        bool hasCatalogFacts = receipt.AddedCharts.Count > 0
            || receipt.RemovedCharts.Count > 0
            || receipt.MovedCharts.Count > 0;
        mutationResult.OwnedCollectionVersion = receipt.OwnedCollectionVersion;
        mutationResult.OwnedCollectionVersionAlreadyAdvanced = hasCatalogFacts;
        mutationResult.OwnedCollectionChanged |= hasCatalogFacts;
        mutationResult.DuplicateCacheInvalidated |= receipt.AddedCharts.Count > 0
            || receipt.RemovedCharts.Count > 0;
        mutationResult.WarningPresentationChanged |= mutationResult.OwnedCollectionChanged;
    }

    private void PublishNormalLibraryRefreshNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null)
        {
            return;
        }
        IReadOnlyList<ChartFile> installDestinationChangedCharts = result.InstallDestinationChangedCharts ?? [];
        LibraryChartRefreshEffects effects = CreateLibraryChartRefreshEffects(result, installDestinationChangedCharts);
        if (effects == LibraryChartRefreshEffects.None)
        {
            return;
        }
        int ownedCollectionVersion = result.OwnedCollectionVersion > 0 ? result.OwnedCollectionVersion : OwnedChartCollectionVersion;
        bool storageRowsRemoveDeltaComplete = IsCompleteRemoveOnlyStorageRowsMutation(result);
        IReadOnlyList<BMSFile> removedBmsFiles = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsStorageRowDelta(result.StorageMutation)
            : [];
        IReadOnlyList<LR2SongDBExtended.bmson_song> removedBmsonSongs = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsonStorageRowDelta(result.StorageMutation)
            : [];
        int version = normalLibraryRefreshPublisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = ownedCollectionVersion,
            Effects = effects,
            InstallDestinationChangedCharts = installDestinationChangedCharts,
            NotifiesStorageRows = result.StorageRowsChanged,
            ResetsPriorNotifications = false,
            NotifiesBmsFiles = result.BmsFilesStorageRowsChanged,
            NotifiesBmsonSongs = result.BmsonSongsStorageRowsChanged,
            RemovedBmsFiles = removedBmsFiles,
            RemovedBmsonSongs = removedBmsonSongs,
            StorageRowsRemoveDeltaComplete = storageRowsRemoveDeltaComplete
        });
        result.NormalLibraryRefreshNotificationVersion = version;
    }

    private static bool IsCompleteRemoveOnlyStorageRowsMutation(OwnedChartCollectionMutationResult result)
    {
        OwnedChartCollectionStorageMutation mutation = result?.StorageMutation;
        return result?.StorageRowsChanged == true
            && result.StorageRowsRemoveDeltaComplete
            && result.DigestChangedCount == 0
            && mutation?.RemovedCount > 0
            && !mutation.RemoveRequests.Any(request => request?.Mode == OwnedChartRemoveMode.PathCleanup)
            && mutation.AddedCount == 0
            && mutation.MovedCount == 0;
    }

    private static IReadOnlyList<BMSFile> CreateRemovedBmsStorageRowDelta(OwnedChartCollectionStorageMutation mutation)
    {
        if (mutation?.RemovedCount > 0 != true)
        {
            return [];
        }
        return [.. mutation.RemoveRequests
            .Select(request => request?.BmsOwner)
            .Where(file => file != null)
            .Distinct()];
    }

    private static IReadOnlyList<LR2SongDBExtended.bmson_song> CreateRemovedBmsonStorageRowDelta(OwnedChartCollectionStorageMutation mutation)
    {
        if (mutation?.RemovedCount > 0 != true)
        {
            return [];
        }
        return [.. mutation.RemoveRequests
            .Select(request => request?.BmsonOwner)
            .Where(song => song != null)
            .Distinct()];
    }

    private static LibraryChartRefreshEffects CreateLibraryChartRefreshEffects(
        OwnedChartCollectionMutationResult result,
        IReadOnlyList<ChartFile> installDestinationChangedCharts)
    {
        var effects = LibraryChartRefreshEffects.None;
        if (result?.OwnedCollectionChanged == true)
        {
            effects |= LibraryChartRefreshEffects.SourceChanged;
        }
        if ((installDestinationChangedCharts?.Count ?? 0) > 0
            || result?.InstallDestinationRuntimeStateMutation?.PruneToCurrentOwnedCharts == true)
        {
            effects |= LibraryChartRefreshEffects.InstallDestinationOverlayChanged;
        }
        if (result?.WarningPresentationChanged == true)
        {
            effects |= LibraryChartRefreshEffects.WarningPresentationChanged;
        }
        if (result?.MaintenancePresentationChanged == true)
        {
            effects |= LibraryChartRefreshEffects.MaintenancePresentationChanged;
        }
        return effects;
    }

    private void PublishNormalLibraryRefreshResetNotification(bool notifiesBmsFiles, bool notifiesBmsonSongs)
    {
        bool notifiesStorageRows = notifiesBmsFiles || notifiesBmsonSongs;
        normalLibraryRefreshPublisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = OwnedChartCollectionVersion,
            Effects = LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
            InstallDestinationChangedCharts = [],
            NotifiesStorageRows = notifiesStorageRows,
            ResetsPriorNotifications = true,
            NotifiesBmsFiles = notifiesBmsFiles,
            NotifiesBmsonSongs = notifiesBmsonSongs
        });
        RaisePropertyChanged(() => NormalLibraryRefreshNotificationVersion);
    }

    private void PublishExternalReplacementNormalLibraryRefreshNotification(bool notifiesBmsFiles, bool notifiesBmsonSongs)
    {
        PublishNormalLibraryRefreshResetNotification(notifiesBmsFiles, notifiesBmsonSongs);
    }

    private void ClearNormalLibraryRefreshNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null || result.NormalLibraryRefreshNotificationVersion <= 0)
        {
            return;
        }
        normalLibraryRefreshPublisher.Clear(result.NormalLibraryRefreshNotificationVersion);
    }

    private void RaiseNormalLibraryRefreshNotificationVersionChanged(OwnedChartCollectionMutationResult result)
    {
        if (result?.NormalLibraryRefreshNotificationVersion > 0)
        {
            RaisePropertyChanged(() => NormalLibraryRefreshNotificationVersion);
        }
    }

    /// <summary>
    /// playlist entry が持つ level を LR2 song row へ反映します。
    /// これは LR2 互換の BMS-only writeback であり、bmson storage row は更新しません。
    /// </summary>
    /// <param name="bmsTable">参照元 playlist。</param>
    internal BmsFileLevelOverwriteOutcome ReplaceBmsFileLevelByTableEntryLevel(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            return BmsFileLevelOverwriteOutcome.NotStarted;
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(ReplaceBmsFileLevelByTableEntryLevel), showMessage: false))
        {
            return BmsFileLevelOverwriteOutcome.BlockedByLr2Synchronization;
        }
        using LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(ReplaceBmsFileLevelByTableEntryLevel),
            showMessage: false);
        if (mutationReservation == null)
        {
            return BmsFileLevelOverwriteOutcome.BlockedByLr2Synchronization;
        }
        using LibraryFileMutationCapability mutationCapability = mutationReservation.CreateMutationCapability();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<BMSFile> bmsFiles = [.. from file in _BMSFiles ?? []
                                             where file != null && !string.IsNullOrWhiteSpace(file.hash) && !string.IsNullOrWhiteSpace(file.path)
                                             join entry in from entry in bmsTable.GetEntriesExceptDummy()
                                                           where !entry.is_removed && entry.level.HasValue
                                                           select entry on file.hash equals entry.md5
                                             select ApplyPlaylistEntryLevel(file, entry.level) into file
                                             where file != null
                                             select file];
                catalogMutationOwner.ApplyPlaylistLevelRows(bmsFiles);
            }
        }
        return BmsFileLevelOverwriteOutcome.Completed;
    }

    /// <summary>
    /// playlist entry の level を LR2 song row の level 値へ正規化して書き込みます。
    /// </summary>
    /// <param name="file">更新対象の LR2 song row。</param>
    /// <param name="entryLevel">playlist entry 側の level。</param>
    /// <returns>更新後の BMS storage row。入力が不完全な場合は null。</returns>
    private static BMSFile ApplyPlaylistEntryLevel(BMSFile file, double? entryLevel)
    {
        if (file == null || !entryLevel.HasValue)
        {
            return null;
        }
        file.level = ((!(entryLevel.Value < 0.0)) ? ((int)entryLevel.Value) : 0);
        return file;
    }

}
