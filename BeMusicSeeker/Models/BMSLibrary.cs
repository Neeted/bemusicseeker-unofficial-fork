using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using NLog;
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
public partial class BMSLibrary : NotificationObject
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

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    internal Action<string, string, long, bool, string> StartupBackgroundTaskReporter { get; set; }

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
    }

    /// <summary>
    /// プレイリストサマリー集計で再利用する所持譜面ハッシュ一覧の snapshot です。
    /// owned chart collection から hash だけを抽出し、集計ごとの一時 allocation を避けるために使用します。
    /// </summary>
    internal sealed class PlaylistSummaryOwnedHashSnapshot
    {
        private HashSet<string> md5Hashes;
        private HashSet<string> sha256Hashes;
        private IReadOnlyCollection<string> md5HashSnapshot;
        private IReadOnlyCollection<string> sha256HashSnapshot;

        internal PlaylistSummaryOwnedHashSnapshot(
            HashSet<string> md5Hashes = null,
            HashSet<string> sha256Hashes = null)
        {
            this.md5Hashes = new HashSet<string>(md5Hashes ?? [], StringComparer.OrdinalIgnoreCase);
            this.sha256Hashes = new HashSet<string>(sha256Hashes ?? [], StringComparer.OrdinalIgnoreCase);
            md5HashSnapshot = new ReadOnlyCollection<string>([.. this.md5Hashes]);
            sha256HashSnapshot = new ReadOnlyCollection<string>([.. this.sha256Hashes]);
        }

        internal int Version { get; set; }

        internal long BuildElapsedMs { get; set; }

        internal int InvalidationVersion { get; set; }

        internal int OwnedCollectionVersion { get; set; }

        internal int BmsRowsVersion { get; set; }

        internal int BmsonRowsVersion { get; set; }

        internal IReadOnlyCollection<string> Md5Hashes => md5HashSnapshot;

        internal IReadOnlyCollection<string> Sha256Hashes => sha256HashSnapshot;

        internal int Md5Count => md5Hashes.Count;

        internal int Sha256Count => sha256Hashes.Count;

        internal bool ContainsMd5(string md5)
        {
            return !string.IsNullOrWhiteSpace(md5) && md5Hashes.Contains(md5);
        }

        internal bool ContainsSha256(string sha256)
        {
            return !string.IsNullOrWhiteSpace(sha256) && sha256Hashes.Contains(sha256);
        }
    }

    /// <summary>
    /// playlist summary 用 owned hash snapshot の warmup 結果です。
    /// </summary>
    internal sealed class OwnedHashIndexWarmupResult
    {
        internal string IndexName { get; set; }

        internal string Status { get; set; }

        internal long ElapsedMs { get; set; }

        internal int Md5Count { get; set; }

        internal int Sha256Count { get; set; }

        internal int SnapshotVersion { get; set; }

        internal int InvalidationVersion { get; set; }

        internal int OwnedCollectionVersion { get; set; }

        internal int BmsRowsVersion { get; set; }

        internal int BmsonRowsVersion { get; set; }

        internal int StaleRetryCount { get; set; }
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
        try
        {
            StartupBackgroundTaskReporter?.Invoke(name ?? "unknown", status ?? string.Empty, elapsedMs, failed, detail ?? string.Empty);
        }
        catch
        {
        }
    }

    internal bool IsShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    internal void RequestShutdown(string reason)
    {
        Interlocked.Exchange(ref shutdownRequested, 1);
        string shutdownReason = "shutdown:" + (reason ?? "unknown");
        try
        {
            CancelLr2SongDbSync(shutdownReason);
        }
        catch (Exception ex)
        {
            LogInstallPerformance("shutdown cancel_lr2_song_db_sync_failed reason=" + shutdownReason + " message=" + ex.Message);
        }
        try
        {
            pendingInstallEstimateQueueProcessor?.CancelAll();
        }
        catch (Exception ex)
        {
            LogInstallPerformance("shutdown cancel_pending_estimate_failed reason=" + shutdownReason + " message=" + ex.Message);
        }
    }

    internal bool HasShutdownBlockingWork =>
        Lr2SongDbSyncRunning
        || ChartInfoHydrationRunning
        || ChartInfoBackfillRunning
        || ChartDigestBackfillRunning
        || MaintenanceHydrationRunning
        || InstallableMaintenanceDeferredRunning
        || ScoreHydrationRunning
        || RankingRefreshRunning
        || (pendingInstallEstimateQueueProcessor != null && !pendingInstallEstimateQueueProcessor.IsIdle);

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
            + " pendingInstallEstimateQueueIdle=" + FormatBool(pendingInstallEstimateQueueProcessor == null || pendingInstallEstimateQueueProcessor.IsIdle);
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private bool TrySkipForShutdown(string operation, string reason)
    {
        if (!IsShutdownRequested)
        {
            return false;
        }
        LogInstallPerformance((operation ?? "background_work") + " skipped reason=shutdown_requested requestReason=" + (reason ?? "unknown"));
        return true;
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

        public IRDataCacheInfo(dynamic json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }
            if (json.IsDefined("md5") && json.md5 != null && json.IsDefined("size") && json.size != null && json.IsDefined("lastupdate") && json.lastupdate != null)
            {
                if ((!LR2SongDB.md5HashRegex.IsMatch(json.md5.ToString())))
                {
                    throw new ArgumentException(Resources.Error_NotMd5Hash, "json.md5");
                }
                md5 = json.md5.ToString();
                size = int.Parse(json.size.ToString());
                lastupdate = DateTime.ParseExact(json.lastupdate.ToString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }
            throw new ArgumentException(Resources.Error_InvalidJsonObject, "json");
        }
    }

    private readonly string lr2SongDBPath;

    private readonly string lr2ScoreDBPath;

    private Dictionary<string, BMSScore> beatorajaScoresBySha256 = new(StringComparer.OrdinalIgnoreCase);

    private ActiveScoreSource activeScoreSource;

    private Lr2PlayHistorySchemaCheckResult lr2PlayHistorySchemaCheckResult;

    private readonly Func<LR2Config> lr2config;

    private DirectoryResourceLookupCache directoryResourceLookupCache = new();

    private LibraryResourceIndex libraryResourceIndex = LibraryResourceIndex.CreateFromScanResult(new ChartScanResult());

    private readonly StartupInstallReadinessState startupInstallReadinessState = new();

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

    private ReaderWriterLockSlimWrapper rwlockBMSFiles => catalogStorageRowsOwner.WriteGate;

    private readonly ReaderWriterLockSlimWrapper rwlockSongDBInstall = new();

    private object lockStorageRowsVersion => catalogStorageRowsOwner.VersionGate;

    private int deferredInstallableMaintenanceRequestedVersion;

    private bool deferredInstallableMaintenanceRunning;

    private int deferredInstallableMaintenanceLastCompletedVersion;

    private long deferredInstallableMaintenanceCriticalElapsedMs;

    private readonly object lockDeferredInstallableMaintenance = new();

    private const long Lr2SongDbSyncCompletedStatusImplicitChartInfoParseTimeoutMs = 60000L;

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

    private readonly object lockPlaylistSummaryOwnedHashSnapshot = new();

    private PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot;

    private int playlistSummaryOwnedHashSnapshotVersion;

    private int playlistSummaryOwnedHashInvalidationVersion;

    private int playlistSummaryOwnedHashInvalidationOwnedCollectionVersion;

    private readonly object lockPlaylistLibraryResolveIndexSnapshot = new();

    private PlaylistLibraryResolveIndexSnapshot playlistLibraryResolveIndexSnapshot;

    private int playlistLibraryResolveIndexSnapshotVersion;

    private int playlistLibraryResolveIndexInvalidationVersion;

    private int playlistLibraryResolveIndexInvalidationOwnedCollectionVersion;

    private int ownedDigestMutationWindowDepth;

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

    private readonly object lockLr2SongDbSync = new();

    private int lr2SongDbSyncRequestedVersion;

    private int lr2SongDbSyncCompletedVersion;

    private int lr2SongDbSyncFailedVersion;

    private readonly object lockLr2SongDbSyncStatus = new();

    private Lr2SongDbSyncStatusSnapshot lr2SongDbSyncStatus = new()
    {
        Status = Lr2SongDbSyncStatusKind.NotNeeded
    };

    private readonly object lockLr2SongDbSyncScanSurface = new();

    private Lr2SongDbSyncScanSurfaceSnapshot lr2SongDbSyncScanSurfaceSnapshot;

    private CustomFolderOutputPhysicalSurface appManagedCustomFolderOutputPhysicalSurface = new(
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        discoveryComplete: false);

    private int lr2SongDbSyncScanSurfaceGeneration;

    private readonly object lockLr2SongDbSyncFileDiffFreshness = new();

    private Lr2SongDbSyncFileDiffFreshnessSnapshot lr2SongDbSyncFileDiffFreshnessSnapshot;

    private Lr2SongDbSyncPreparedDataSurface lr2SongDbSyncPreparedDataSurface =
        Lr2SongDbSyncPreparedDataSurface.Empty;

    private int lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration;

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

    private int irScorePrefetchGeneration;

    private Task<IrScorePrefetchResult> irScorePrefetchTask;

    private int irScorePrefetchLr2Id;

    private string irScorePrefetchScoreDbPath;

    private bool irScorePrefetchEnabled;

    private readonly object lockPendingEstimateQueueStatus = new();

    private readonly object lockInstallEstimationProgress = new();

    private readonly SemaphoreSlim pendingEstimateExecutionGate = new(1, 1);

    private readonly PendingInstallEstimateQueueProcessor pendingInstallEstimateQueueProcessor;

    private PendingInstallEstimateQueueStatusSnapshot pendingEstimateQueueStatus = new();

    private InstallEstimationProgressSnapshot installEstimationProgress = new();

    private readonly PropertyChangedEventListener listenerForRwlockBMSFilesInitializedAll;

    private readonly PropertyChangedEventListener listenerForRwlockBMSFilesInitializedMin;

    private readonly PropertyChangedEventListener listenerForRwlockDuplicateChartGroups;

    private readonly PropertyChangedEventListener listenerForRwlockPendingInstallCharts;

    private readonly PropertyChangedEventListener listenerForRwlockBMSFiles;

    private IReadOnlyList<BMSFile> _BMSFiles => catalogStorageRowsOwner.BmsRows;

    private IReadOnlyList<LR2SongDBExtended.bmson_song> _BmsonSongs => catalogStorageRowsOwner.BmsonRows;

    private int bmsStorageRowsVersion => catalogStorageRowsOwner.BmsRowsVersion;

    private int bmsonStorageRowsVersion => catalogStorageRowsOwner.BmsonRowsVersion;

    private readonly NormalLibraryRefreshPublisher normalLibraryRefreshPublisher = new();

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private List<DuplicateGroup> _DuplicateChartGroups;

    private DispatcherCollection<ChartPackage> _ChartPackagesPending = new(DispatcherHelper.UIDispatcher);

    private DispatcherCollection<ChartPackage> _ChartPackagesInstalled = new(DispatcherHelper.UIDispatcher);

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

    private int _PendingEstimateQueueStatusVersion;

    private int _InstallEstimationProgressVersion;

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

    private ChartInfoCompletedLr2SongDbSyncTrustSnapshot chartInfoCompletedLr2SongDbSyncTrustSnapshot
    {
        get => catalogChartInfoOwner.CompletedLr2SongDbSyncTrustSnapshot;
        set => catalogChartInfoOwner.CompletedLr2SongDbSyncTrustSnapshot = value;
    }

    private bool _Lr2SongDbSyncRunning;

    private bool lr2SongDbSyncPrepareInProgress;

    private int lr2SongDbSyncMutationInProgress;

    private CancellationTokenSource lr2SongDbSyncCancellation;

    private int _Lr2SongDbSyncRequestedVersion;

    private int _Lr2SongDbSyncCompletedVersion;

    private int _Lr2SongDbSyncFailedVersion;

    private int _Lr2SongDbSyncTotalCount;

    private int _Lr2SongDbSyncProcessedCount;

    private string _Lr2SongDbSyncStage = string.Empty;

    private int _Lr2SongDbSyncStageProcessedCount;

    private int _Lr2SongDbSyncStageTotalCount;

    private string _Lr2SongDbSyncFailureMessage = string.Empty;

    private int _Lr2SongDbSyncStatusVersion;

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
        bool notifyBmsonRows)
    {
        List<BMSFile> normalizedBmsRows = NormalizeBmsStorageRows(bmsFiles?.ToList());
        List<LR2SongDBExtended.bmson_song> normalizedBmsonRows = NormalizeBmsonStorageRows(bmsonSongs?.ToList());
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
        using (resourceHealthOwner.BeginInputMutation())
        {
            InvalidatePlaylistSummaryOwnedHashSnapshot();
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            replacementReceipt = catalogMutationOwner.ApplyStorageRowsReplacement(request);
            bmsRowsChanged = replacementReceipt.BmsRowsChanged;
            bmsonRowsChanged = replacementReceipt.BmsonRowsChanged;
            if (bmsRowsChanged)
            {
                MarkDuplicateWarningFullClearPending();
            }
            int ownedCollectionVersion = NotifyOwnedChartCollectionChanged();
            InvalidatePlaylistSummaryOwnedHashSnapshot(ownedCollectionVersion);
            InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
            if ((notifyBmsRows && bmsRowsChanged) || (notifyBmsonRows && bmsonRowsChanged))
            {
                PublishExternalReplacementNormalLibraryRefreshNotification(
                    notifiesBmsFiles: notifyBmsRows && bmsRowsChanged,
                    notifiesBmsonSongs: notifyBmsonRows && bmsonRowsChanged);
            }
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
        }

        if (notifyBmsRows && bmsRowsChanged)
        {
            Task.Run(delegate
            {
                RaisePropertyChanged("BMSFiles");
            }).Logging("BMSFiles");
        }
        if (notifyBmsonRows && bmsonRowsChanged)
        {
            Task.Run(delegate
            {
                RaisePropertyChanged("BmsonSongs");
            }).Logging("BmsonSongs");
        }
        if ((notifyBmsRows && bmsRowsChanged) || (notifyBmsonRows && bmsonRowsChanged))
        {
            RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
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
                }).Logging("DuplicateChartGroups");
            }
        }
    }

    internal int DuplicateChartGroupsInvalidationVersion => Volatile.Read(ref duplicateChartGroupsInvalidationVersion);

    private void InvalidateDuplicateChartGroupsCache()
    {
        if (_DuplicateChartGroups == null)
        {
            return;
        }
        _DuplicateChartGroups = null;
        Interlocked.Increment(ref duplicateChartGroupsInvalidationVersion);
        Task.Run(delegate
        {
            RaisePropertyChanged(() => DuplicateChartGroupsInvalidationVersion);
        }).Logging("DuplicateChartGroupsInvalidationVersion");
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
    /// インストール待ち（Pending状態）の chart package のコレクションです。UIスレッドへのディスパッチに対応しています。
    /// </summary>
    public DispatcherCollection<ChartPackage> ChartPackagesPending
    {
        get
        {
            return _ChartPackagesPending;
        }
        set
        {
            if (_ChartPackagesPending != value)
            {
                _ChartPackagesPending = value;
                RaisePropertyChanged("ChartPackagesPending");
            }
        }
    }

    /// <summary>
    /// インストール済みの chart package のコレクションです。UIスレッドへのディスパッチに対応しています。
    /// </summary>
    public DispatcherCollection<ChartPackage> ChartPackagesInstalled
    {
        get
        {
            return _ChartPackagesInstalled;
        }
        set
        {
            if (_ChartPackagesInstalled != value)
            {
                _ChartPackagesInstalled = value;
                RaisePropertyChanged("ChartPackagesInstalled");
            }
        }
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

    private List<string> CreateInstalledChartPathSnapshotForParentFolderCache()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return CreateOwnedChartPathSnapshotUnsafe();
        }
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
    internal ParentFolderListCacheSnapshot BuildBMSParentFolderListCacheSnapshot()
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
        List<string> installedChartPaths = CreateInstalledChartPathSnapshotForParentFolderCache();
        return parentFolderCacheService.BuildSnapshot(version, installedChartPaths, getBMSDirectories(), CurrentOptionsSnapshot);
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
        get
        {
            return _PendingEstimateQueueStatusVersion;
        }
        private set
        {
            if (_PendingEstimateQueueStatusVersion != value)
            {
                _PendingEstimateQueueStatusVersion = value;
                RaisePropertyChanged(() => PendingEstimateQueueStatusVersion);
            }
        }
    }

    public int InstallEstimationProgressVersion
    {
        get
        {
            return _InstallEstimationProgressVersion;
        }
        private set
        {
            if (_InstallEstimationProgressVersion != value)
            {
                _InstallEstimationProgressVersion = value;
                RaisePropertyChanged(() => InstallEstimationProgressVersion);
            }
        }
    }

    /// <summary>
    /// installable maintenance deferred worker が現在実行中かどうかを返します。
    /// </summary>
    public bool InstallableMaintenanceDeferredRunning
    {
        get
        {
            return deferredInstallableMaintenanceRunning;
        }
        private set
        {
            if (deferredInstallableMaintenanceRunning != value)
            {
                deferredInstallableMaintenanceRunning = value;
                RaisePropertyChanged(() => InstallableMaintenanceDeferredRunning);
            }
        }
    }

    /// <summary>
    /// 最後に要求された installable maintenance deferred worker の版数です。
    /// </summary>
    public int InstallableMaintenanceDeferredRequestedVersion
    {
        get
        {
            return deferredInstallableMaintenanceRequestedVersion;
        }
        private set
        {
            if (deferredInstallableMaintenanceRequestedVersion != value)
            {
                deferredInstallableMaintenanceRequestedVersion = value;
                RaisePropertyChanged(() => InstallableMaintenanceDeferredRequestedVersion);
            }
        }
    }

    /// <summary>
    /// 最後に完了した installable maintenance deferred worker の版数です。
    /// </summary>
    public int InstallableMaintenanceDeferredCompletedVersion
    {
        get
        {
            return deferredInstallableMaintenanceLastCompletedVersion;
        }
        private set
        {
            if (deferredInstallableMaintenanceLastCompletedVersion != value)
            {
                deferredInstallableMaintenanceLastCompletedVersion = value;
                RaisePropertyChanged(() => InstallableMaintenanceDeferredCompletedVersion);
            }
        }
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
        get
        {
            return _Lr2SongDbSyncRunning;
        }
        private set
        {
            if (_Lr2SongDbSyncRunning != value)
            {
                _Lr2SongDbSyncRunning = value;
                RaisePropertyChanged(() => Lr2SongDbSyncRunning);
            }
        }
    }

    public int Lr2SongDbSyncRequestedVersion
    {
        get
        {
            return _Lr2SongDbSyncRequestedVersion;
        }
        private set
        {
            if (_Lr2SongDbSyncRequestedVersion != value)
            {
                _Lr2SongDbSyncRequestedVersion = value;
                RaisePropertyChanged(() => Lr2SongDbSyncRequestedVersion);
            }
        }
    }

    public int Lr2SongDbSyncCompletedVersion
    {
        get
        {
            return _Lr2SongDbSyncCompletedVersion;
        }
        private set
        {
            if (_Lr2SongDbSyncCompletedVersion != value)
            {
                _Lr2SongDbSyncCompletedVersion = value;
                RaisePropertyChanged(() => Lr2SongDbSyncCompletedVersion);
            }
        }
    }

    public int Lr2SongDbSyncFailedVersion
    {
        get
        {
            return _Lr2SongDbSyncFailedVersion;
        }
        private set
        {
            if (_Lr2SongDbSyncFailedVersion != value)
            {
                _Lr2SongDbSyncFailedVersion = value;
                RaisePropertyChanged(() => Lr2SongDbSyncFailedVersion);
            }
        }
    }

    public int Lr2SongDbSyncTotalCount
    {
        get
        {
            return _Lr2SongDbSyncTotalCount;
        }
        private set
        {
            if (_Lr2SongDbSyncTotalCount != value)
            {
                _Lr2SongDbSyncTotalCount = value;
                RaisePropertyChanged(() => Lr2SongDbSyncTotalCount);
            }
        }
    }

    public int Lr2SongDbSyncProcessedCount
    {
        get
        {
            return _Lr2SongDbSyncProcessedCount;
        }
        private set
        {
            if (_Lr2SongDbSyncProcessedCount != value)
            {
                _Lr2SongDbSyncProcessedCount = value;
                RaisePropertyChanged(() => Lr2SongDbSyncProcessedCount);
            }
        }
    }

    public string Lr2SongDbSyncStage
    {
        get
        {
            return _Lr2SongDbSyncStage;
        }
        private set
        {
            value ??= string.Empty;
            if (_Lr2SongDbSyncStage != value)
            {
                _Lr2SongDbSyncStage = value;
                RaisePropertyChanged(() => Lr2SongDbSyncStage);
            }
        }
    }

    public int Lr2SongDbSyncStageProcessedCount
    {
        get
        {
            return _Lr2SongDbSyncStageProcessedCount;
        }
        private set
        {
            if (_Lr2SongDbSyncStageProcessedCount != value)
            {
                _Lr2SongDbSyncStageProcessedCount = value;
                RaisePropertyChanged(() => Lr2SongDbSyncStageProcessedCount);
            }
        }
    }

    public int Lr2SongDbSyncStageTotalCount
    {
        get
        {
            return _Lr2SongDbSyncStageTotalCount;
        }
        private set
        {
            if (_Lr2SongDbSyncStageTotalCount != value)
            {
                _Lr2SongDbSyncStageTotalCount = value;
                RaisePropertyChanged(() => Lr2SongDbSyncStageTotalCount);
            }
        }
    }

    public string Lr2SongDbSyncFailureMessage
    {
        get
        {
            return _Lr2SongDbSyncFailureMessage;
        }
        private set
        {
            value ??= string.Empty;
            if (_Lr2SongDbSyncFailureMessage != value)
            {
                _Lr2SongDbSyncFailureMessage = value;
                RaisePropertyChanged(() => Lr2SongDbSyncFailureMessage);
            }
        }
    }

    public int Lr2SongDbSyncStatusVersion
    {
        get
        {
            return _Lr2SongDbSyncStatusVersion;
        }
        private set
        {
            if (_Lr2SongDbSyncStatusVersion != value)
            {
                _Lr2SongDbSyncStatusVersion = value;
                RaisePropertyChanged(() => Lr2SongDbSyncStatusVersion);
            }
        }
    }

    internal Lr2SongDbSyncStatusSnapshot GetLr2SongDbSyncStatusSnapshot()
    {
        lock (lockLr2SongDbSyncStatus)
        {
            return lr2SongDbSyncStatus?.Clone() ?? new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.NotNeeded
            };
        }
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

    public List<string> SearchTargets { get; set; } = [];

    private readonly IFileMutationService fileMutationService;

    private readonly IBmsLibraryDialogService dialogService;

    private readonly IBmsLibraryDialogService scopedOperationDialogService;

    private readonly object everythingFallbackWarningGate = new();

    private long everythingFallbackWarningEpoch;

    private int everythingFallbackWarningQueued;
    private int fileScanSkippedIncompleteWarningQueued;
    private int emptyScanWithExistingDbWarningQueued;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly BmsLibraryDuplicateService duplicateService = new();

    private readonly BmsLibraryParentFolderCacheService parentFolderCacheService = new();

    private readonly BmsLibraryPlaylistReferenceService playlistReferenceService = new(playlistReferenceApplyChunkSize);

    private readonly PlaylistReferenceManager playlistReferenceManager = new();

    private readonly BmsLibraryPackageInstallService packageInstallService = new();

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new();

    private readonly BmsLibraryIrService irService = new();

    private readonly BmsLibraryInitializationService initializationService = new();

    private readonly LibraryFileScanPipelineOwner libraryFileScanPipelineOwner;

    private readonly InstallDestinationStateOwner installDestinationStateOwner;

    private ChartInfoBuildService chartInfoBuildService => catalogChartInfoOwner.BuildService;

    private readonly IBmsLibraryIrClient irClient = new BmsLibraryIrClient();

    private readonly BmsLibraryMaintenanceService maintenanceService = new();

    private readonly BmsLibraryStateApplier stateApplier;

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

        public LibraryResourceIndex ResourceIndexSnapshot { get; set; }

        public DirectoryResourceLookupCache DirectoryLookupCacheSnapshot => ResourceIndexSnapshot?.DirectoryLookupCache;

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
    }

    /// <summary>
    /// LR2 の song.db を読み込み、このセッションで利用する BMS ライブラリを初期化します。
    /// </summary>
    /// <param name="_lr2SongDB">必須の LR2 song.db パスです。</param>
    /// <param name="getLR2Config">必要時に LR2 設定を取得するコールバックです。</param>
    /// <param name="_lr2ScoreDB">任意の LR2 score.db パスです。</param>
    /// <exception cref="ArgumentNullException">song.db パスが null の場合に送出されます。</exception>
    /// <exception cref="ArgumentException">指定された DB ファイルが存在しない場合に送出されます。</exception>
    public BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config = null, string _lr2ScoreDB = null, string startupRequiredFileScanReason = null)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, null, null, startupRequiredFileScanReason, null)
    {
    }

    /// <summary>
    /// テストや内部差し替え用の変更系ファイル操作サービスを指定して BMS ライブラリを初期化します。
    /// </summary>
    /// <param name="_lr2SongDB">必須の LR2 song.db パスです。</param>
    /// <param name="getLR2Config">必要時に LR2 設定を取得するコールバックです。</param>
    /// <param name="_lr2ScoreDB">任意の LR2 score.db パスです。</param>
    /// <param name="fileMutationService">ReadOnly 補正や再試行を担う変更系ファイル操作サービスです。</param>
    /// <exception cref="ArgumentNullException">song.db パスが null の場合に送出されます。</exception>
    /// <exception cref="ArgumentException">指定された DB ファイルが存在しない場合に送出されます。</exception>
    internal BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config, string _lr2ScoreDB, IFileMutationService fileMutationService)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, fileMutationService, null, null, null)
    {
    }

    internal BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config, string _lr2ScoreDB, IFileMutationService fileMutationService, IBmsLibraryDialogService dialogService)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, fileMutationService, dialogService, null, null)
    {
    }

    internal BMSLibrary(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, null, null, startupRequiredFileScanReason, optionsSnapshotProvider)
    {
    }

    internal BMSLibrary(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider)
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
        lr2SongDBPath = _lr2SongDB;
        lr2ScoreDBPath = _lr2ScoreDB;
        this.startupRequiredFileScanReason = startupRequiredFileScanReason;
        this.optionsSnapshotProvider = optionsSnapshotProvider ?? BmsLibraryOptionsSnapshot.CreateCurrent;
        this.fileMutationService = fileMutationService ?? new ResilientFileMutationService();
        this.dialogService = dialogService ?? new BmsLibraryDialogService();
        scopedOperationDialogService = new ScopedOperationDialogService(this);
        dbGateway = new BmsLibraryDbGateway(lr2SongDBPath, lr2ScoreDBPath);
        catalogChartInfoOwner = new(
            RaisePropertyChanged,
            () => IsShutdownRequested,
            TrySkipForShutdown,
            () => StartupBackgroundTaskScheduler,
            LogInstallPerformance);
        var libraryFileScanHost = new LibraryFileScanPipelineHost(this);
        catalogMutationOwner = new(
            catalogStorageRowsOwner,
            catalogOwnedCollectionOwner,
            dbGateway,
            MarkLr2SongDbSyncIncompleteAfterStateApplierSongDbWriteFailure);
        catalogChartInfoOwner.ConfigureWorkflow(
            dbGateway,
            catalogMutationOwner,
            catalogStorageRowsOwner,
            catalogOwnedCollectionOwner,
            () => CurrentOptionsSnapshot,
            LogInstallPerformanceWarn,
            HandleCatalogChartInfoOwnerEvent,
            BeginOwnedDigestMutationWindow);
        resourceHealthOwner = new(
            maintenanceService,
            LogInstallPerformance,
            GetCurrentResourceHealthIndexVersion);
        installDestinationStateOwner = new(CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe);
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
            directoryResourceLookupCache,
            dialogService,
            () => IsShutdownRequested,
            TrySkipForShutdown,
            () => StartupBackgroundTaskScheduler,
            ReportStartupBackgroundTask,
            GetDisplayedExceptionMessage,
            PublishMaintenanceHydrationReceipt,
            LogInstallPerformance,
            MarkLr2SongDbSyncIncompleteAfterMaintenanceSongDbWriteFailure,
            NotifyMaintenanceHydrationStateChanged);
        libraryFileScanPipelineOwner = new LibraryFileScanPipelineOwner(
            libraryFileScanHost,
            libraryFileScanHost,
            initializationService,
            ApplyLibraryMutationDelta,
            initializationService.ParseCommitOwner);
        stateApplier = new BmsLibraryStateApplier(
            dbGateway,
            () => ChartPackagesPending,
            pendingPackages => ChartPackagesPending = pendingPackages,
            () => ChartPackagesInstalled,
            installedPackages => ChartPackagesInstalled = installedPackages,
            () => RaisePropertyChanged(() => ChartPackagesInstalled));
        pendingInstallEstimateQueueProcessor = new PendingInstallEstimateQueueProcessor(ProcessPendingInstallEstimateBatch, UpdatePendingEstimateQueueStatus, HandlePendingEstimateBatchException);
        lr2config = (getLR2Config ?? (Func<LR2Config>)(() => (LR2Config)null));
        using (LR2SongDBExtended lR2SongDBExtended = dbGateway.OpenSongDb())
        {
            BmsLibraryDbGateway.EnsureSongLookupIndexes(lR2SongDBExtended);
            lR2SongDBExtended.CreateTable<LR2SongDB.folder>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.install>();
            BmsLibraryDbGateway.EnsureMaintenanceSchema(lR2SongDBExtended);
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.ir_score>();
            BmsLibraryDbGateway.EnsureIrDataSchema(lR2SongDBExtended);
            BmsLibraryDbGateway.EnsureChartInfoSchema(lR2SongDBExtended);
        }
        listenerForRwlockBMSFilesInitializedAll = new PropertyChangedEventListener(rwlockBMSFilesInitializedAll);
        listenerForRwlockBMSFilesInitializedMin = new PropertyChangedEventListener(rwlockBMSFilesInitializedMin);
        listenerForRwlockDuplicateChartGroups = new PropertyChangedEventListener(rwlockDuplicateChartGroups);
        listenerForRwlockPendingInstallCharts = new PropertyChangedEventListener(rwlockPendingInstallCharts);
        listenerForRwlockBMSFiles = new PropertyChangedEventListener(rwlockBMSFiles);
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

    internal Lr2PlayHistorySchemaCheckResult GetLr2PlayHistorySchemaCheckResultForDiagnostics()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return lr2PlayHistorySchemaCheckResult;
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
        lock (lockPendingEstimateQueueStatus)
        {
            return pendingEstimateQueueStatus?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
        }
    }

    internal InstallEstimationProgressSnapshot GetInstallEstimationProgressSnapshot()
    {
        lock (lockInstallEstimationProgress)
        {
            return installEstimationProgress?.Clone() ?? new InstallEstimationProgressSnapshot();
        }
    }

    private void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        lock (lockPendingEstimateQueueStatus)
        {
            pendingEstimateQueueStatus = snapshot?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
            PendingEstimateQueueStatusVersion++;
        }
    }

    private void UpdateInstallEstimationProgress(InstallEstimationProgressSnapshot snapshot)
    {
        lock (lockInstallEstimationProgress)
        {
            installEstimationProgress = snapshot?.Clone() ?? new InstallEstimationProgressSnapshot();
            InstallEstimationProgressVersion++;
        }
    }

    private void SetInstallEstimationProgress(InstallEstimationProgressSource source, int totalWorkCount, int completedWorkCount, string currentDisplayName)
    {
        UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
        {
            IsActive = totalWorkCount > 0,
            Source = source,
            TotalWorkCount = Math.Max(totalWorkCount, 0),
            CompletedWorkCount = Math.Max(0, Math.Min(completedWorkCount, Math.Max(totalWorkCount, 0))),
            CurrentDisplayName = currentDisplayName ?? string.Empty
        });
    }

    private void ClearInstallEstimationProgress()
    {
        UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot());
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
        pendingInstallEstimateQueueProcessor.Enqueue(request);
        PendingInstallEstimateQueueStatusSnapshot snapshot = pendingInstallEstimateQueueProcessor.GetStatusSnapshot();
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
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " display=" + (request.DisplayName ?? string.Empty));
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, 0, request.DisplayName ?? string.Empty);
                PendingInstallEstimateEvaluationContext evaluationContext = CreatePendingInstallEstimateEvaluationContext();
                List<PendingInstallEstimateEvaluationRequest> evaluationRequests = PreparePendingInstallEstimateEvaluationRequests(request);
                ProcessPendingInstallEstimateEvaluationPipeline(request, source, token, evaluationContext, evaluationRequests, executionPolicy, ref completed, ref lowConfidenceCount);
                if (!token.IsCancellationRequested && request.RegroupEligibleSourceDirectories.Length > 0)
                {
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
        }
        finally
        {
            ClearInstallEstimationProgress();
        }
    }

    private PendingInstallEstimateEvaluationContext CreatePendingInstallEstimateEvaluationContext()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return new PendingInstallEstimateEvaluationContext
                {
                    InstalledChartLookupIndex = CreateInstalledChartLookupSnapshotUnsafe(),
                    ResourceIndexSnapshot = libraryResourceIndex,
                    OptionsSnapshot = CurrentOptionsSnapshot
                };
            }
        }
    }

    private List<PendingInstallEstimateEvaluationRequest> PreparePendingInstallEstimateEvaluationRequests(PendingInstallEstimateBatchRequest request)
    {
        List<PendingInstallEstimateEvaluationRequest> requests = [];
        if (request == null)
        {
            return requests;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    int orderIndex = 0;
                    if (request.BatchSourceSnapshot?.PackageStates.Count > 0)
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
                        return requests;
                    }

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
            }
        }
        return requests;
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

    private void ProcessPendingInstallEstimateEvaluationPipeline(PendingInstallEstimateBatchRequest request, string source, CancellationToken token, PendingInstallEstimateEvaluationContext evaluationContext, List<PendingInstallEstimateEvaluationRequest> evaluationRequests, InstallEstimationExecutionPolicy executionPolicy, ref int completed, ref int lowConfidenceCount)
    {
        executionPolicy ??= InstallEstimationExecutionPolicy.ForSingleWorkItem();
        List<(PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task)> inFlight = [];
        int nextDispatchIndex = 0;
        int nextApplyIndex = 0;
        while (nextApplyIndex < evaluationRequests.Count)
        {
            while (!token.IsCancellationRequested && nextDispatchIndex < evaluationRequests.Count && inFlight.Count < executionPolicy.WorkItemDegree)
            {
                PendingInstallEstimateEvaluationRequest dispatchRequest = evaluationRequests[nextDispatchIndex];
                SetPendingInstallEstimateSearchingState(dispatchRequest, isSearching: true);
                Task<PendingInstallEstimateEvaluationResult> evaluateTask = Task.Run(() => EvaluatePendingInstallEstimateRequest(dispatchRequest, evaluationContext, executionPolicy, token), token);
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
            ApplyPendingInstallEstimateEvaluationResult(request, source, evaluationResult, executionPolicy.WorkItemDegree, ref completed, ref lowConfidenceCount);
            nextApplyIndex++;
        }

        foreach ((PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task) item in inFlight)
        {
            PendingInstallEstimateEvaluationResult evaluationResult = item.Task.GetAwaiter().GetResult();
            ApplyPendingInstallEstimateEvaluationResult(request, source, evaluationResult, executionPolicy.WorkItemDegree, ref completed, ref lowConfidenceCount);
        }
    }

    private PendingInstallEstimateEvaluationResult EvaluatePendingInstallEstimateRequest(PendingInstallEstimateEvaluationRequest request, PendingInstallEstimateEvaluationContext evaluationContext, InstallEstimationExecutionPolicy executionPolicy, CancellationToken token)
    {
        var result = new PendingInstallEstimateEvaluationResult
        {
            Request = request,
            OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.NoOp
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

    private void ApplyPendingInstallEstimateEvaluationResult(PendingInstallEstimateBatchRequest batchRequest, string source, PendingInstallEstimateEvaluationResult evaluationResult, int packageDegree, ref int completed, ref int lowConfidenceCount)
    {
        PendingInstallEstimateEvaluationRequest request = evaluationResult?.Request;
        string currentDisplayName = request?.DisplayName ?? string.Empty;
        SetInstallEstimationProgress(ToInstallEstimationProgressSource(batchRequest.Source), batchRequest.PackageCount, completed, currentDisplayName);
        bool isLowConfidence = false;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        isLowConfidence = ApplyPendingInstallEstimateEvaluationResultUnsafe(evaluationResult);
                    }
                }
            }
        }
        completed++;
        if (isLowConfidence)
        {
            lowConfidenceCount++;
        }
        SetInstallEstimationProgress(ToInstallEstimationProgressSource(batchRequest.Source), batchRequest.PackageCount, completed, currentDisplayName);
        pendingInstallEstimateQueueProcessor.ReportActiveBatchProgress(completed);
        LogInstallPerformance("pending_estimate_batch progress source=" + source + " packageDegree=" + packageDegree + " completed=" + completed + "/" + batchRequest.PackageCount + " current=" + currentDisplayName);
    }

    private bool ApplyPendingInstallEstimateEvaluationResultUnsafe(PendingInstallEstimateEvaluationResult evaluationResult)
    {
        PendingInstallEstimateEvaluationRequest request = evaluationResult?.Request;
        if (request == null)
        {
            return false;
        }

        SetPendingInstallEstimateSearchingStateUnsafe(request, isSearching: false);
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
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    SetPendingInstallEstimateSearchingStateUnsafe(request, isSearching);
                }
            }
        }
    }

    private static void SetPendingInstallEstimateSearchingStateUnsafe(PendingInstallEstimateEvaluationRequest request, bool isSearching)
    {
        if (request == null)
        {
            return;
        }
        foreach (PackageChartEntry entry in request.MissingEntries ?? [])
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
        pendingEstimateExecutionGate.Wait();
        try
        {
            action?.Invoke();
        }
        finally
        {
            pendingEstimateExecutionGate.Release();
        }
    }

    private BackgroundPendingEstimatePreparationResult PrepareBackgroundPendingEstimatePackagesUnsafe(IEnumerable<ChartPackage> packages, PendingInstallEstimateBatchSource source)
    {
        var result = new BackgroundPendingEstimatePreparationResult();
        List<ChartPackage> packageList = [.. (packages ?? []).Where(package => package != null).Distinct()];
        if (packageList.Count == 0)
        {
            return result;
        }

        string sourceLogValue = ToPendingEstimateBatchSourceLogValue(source);
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService(options);
        IInstalledChartLookupIndex installedDirectoryIndex = CreateInstalledChartLookupSnapshotUnsafe();
        PendingEstimateSourceBatchSnapshot candidateSnapshot = BuildPendingEstimateSourceBatchSnapshotUnsafe(packageList, installEstimationService, installedDirectoryIndex, sourceLogValue);
        var estimableSnapshot = new PendingEstimateSourceBatchSnapshot
        {
            RootCount = candidateSnapshot.RootCount,
            ChunkCount = candidateSnapshot.ChunkCount,
            NativeBridgeMs = candidateSnapshot.NativeBridgeMs,
            ManagedDecodeMs = candidateSnapshot.ManagedDecodeMs,
            ManagedMaterializeMs = candidateSnapshot.ManagedMaterializeMs,
            TrackedFileCount = candidateSnapshot.TrackedFileCount,
            ResourceFileCount = candidateSnapshot.ResourceFileCount,
            ElapsedMs = candidateSnapshot.ElapsedMs,
            ScanBackend = candidateSnapshot.ScanBackend
        };

        var prefilterStopwatch = Stopwatch.StartNew();
        foreach (PendingEstimateSourceBatchPackageState state in candidateSnapshot.PackageStates)
        {
            if (HasUnsupportedResourcePath(state.MissingEntries))
            {
                ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries.Where(entry => entry?.Chart != null && ContainsInstalledChartUnsafe(entry.Chart)));
                ApplyUnsupportedResourcePathToPackageUnsafe(state.Package, state.MissingEntries);
                result.DeferredPackages.Add(state.Package);
                continue;
            }

            if (HasInstalledDestinationResolveFailed(state))
            {
                state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
                ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries.Where(entry => entry?.Chart != null && ContainsInstalledChartUnsafe(entry.Chart)));
                ApplyInstalledDestinationResolveFailedToPackageUnsafe(state.Package, state.MissingEntries);
                result.DeferredPackages.Add(state.Package);
                continue;
            }

            if (HasSourceSurfaceScanLimitExceeded(state))
            {
                ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries.Where(entry => entry?.Chart != null && ContainsInstalledChartUnsafe(entry.Chart)));
                ApplySourceSurfaceScanLimitExceededToPackageUnsafe(
                    state.Package,
                    state.MissingEntries,
                    state.SourceSurface?.MaxVisitedFileSystemEntryCount ?? PackageInstallEstimationSnapshotBuilder.DefaultSourceSurfaceMaxVisitedFileSystemEntries);
                result.DeferredPackages.Add(state.Package);
                continue;
            }

            if (ShouldDeferPendingEstimateBatchPackageUnsafe(state, installEstimationService, out int sourcePrimaryHealth))
            {
                state.BaselinePrefilter = new SourceBaselinePrefilterResult
                {
                    Deferred = true,
                    PrimaryHealth = sourcePrimaryHealth
                };
                state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;
                ClearInstallEstimationStateUnsafe(state.PackageEntries);
                result.DeferredPackages.Add(state.Package);
                result.DeferredSourceHealthByPackage[state.Package] = sourcePrimaryHealth;
                continue;
            }

            state.BaselinePrefilter = new SourceBaselinePrefilterResult
            {
                Deferred = false,
                PrimaryHealth = sourcePrimaryHealth
            };
            state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
            result.EstimablePackages.Add(state.Package);
            estimableSnapshot.AddState(state);
        }
        prefilterStopwatch.Stop();
        estimableSnapshot.PrefilterMs = prefilterStopwatch.ElapsedMilliseconds;
        result.BatchSourceSnapshot = estimableSnapshot;

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
            + " elapsedMs=" + estimableSnapshot.PrefilterMs);

        return result;
    }

    private PendingEstimateSourceBatchSnapshot BuildPendingEstimateSourceBatchSnapshotUnsafe(List<ChartPackage> packageList, BmsLibraryInstallEstimationService installEstimationService, IInstalledChartLookupIndex installedDirectoryIndex, string sourceLogValue)
    {
        var snapshot = new PendingEstimateSourceBatchSnapshot();
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
                EstimateMode = ChartInstallationEstimateMode.Normal
            };
            if (state.AttemptInstalledResolve)
            {
                state.PreparationInstalledResolution = installEstimationService.TryResolveInstalledDestinationFromPackage(package, state.MissingEntries, installedDirectoryIndex);
            }

            if (state.HasMissingFiles
                && !HasInstalledDestinationResolveFailed(state)
                && !HasUnsupportedResourcePath(state.MissingEntries)
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
                ?? "fast";
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
        using (rwlockBMSScores.GetReaderGuard())
        {
            scoresSnapshot = [.. (BMSScores ?? []).Where(score => score != null)];
            beatorajaScoresSnapshot = new Dictionary<string, BMSScore>(
                beatorajaScoresBySha256 ?? new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            sourceSnapshot = activeScoreSource;
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
                ActiveScoreSource = sourceSnapshot
            };
        }
        ScoreSnapshotReady = sourceSnapshot != ActiveScoreSource.None;
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
        lock (lockLibraryInitializationProgress)
        {
            LibraryInitializationProgress = stage;
            LibraryInitializationProgressScannerLabel = scannerLabel ?? string.Empty;
            LibraryInitializationProgressTotalCount = Math.Max(0, totalCount);
            LibraryInitializationProgressProcessedCount = Math.Max(0, processedCount);
            LibraryInitializationProgressCurrentPath = currentPath ?? string.Empty;
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

    internal sealed class LibraryFileScanPipelineHost(BMSLibrary owner) : ILibraryFileScanPipelineHost, ILibraryFileScanLr2FolderHost
    {
        public BmsLibraryDbGateway DbGateway => owner.dbGateway;

        public IReadOnlyList<BMSFile> BmsFiles => owner.BMSFiles;

        public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs => owner.BmsonSongs;

        public IBmsLibraryDialogService DialogService => owner.dialogService;

        public bool EverythingScanLoggingEnabled => everythingScanLoggingEnabled;

        public void ThrowIfLr2SongDbSyncMutationBlocked(string operation)
        {
            owner.ThrowIfLr2SongDbSyncMutationBlocked(operation);
        }

        public void ReportLibraryInitializationProgress(
            LibraryInitializationProgressStage stage,
            string scannerLabel = null,
            int totalCount = 0,
            int processedCount = 0,
            string currentPath = null,
            bool force = false)
        {
            owner.ReportLibraryInitializationProgress(stage, scannerLabel, totalCount, processedCount, currentPath, force);
        }

        public void CompleteLibraryFileEnumerationProgress()
        {
            owner.CompleteLibraryFileEnumerationProgress();
        }

        public void CompleteLibraryFileDiffProgress()
        {
            owner.CompleteLibraryFileDiffProgress();
        }

        public void LogInstallPerformance(string message)
        {
            BMSLibrary.LogInstallPerformance(message);
        }

        public void LogInstallPerformanceWarn(string message)
        {
            BMSLibrary.LogInstallPerformanceWarn(message);
        }

        public void LogEverythingScan(string message)
        {
            BMSLibrary.LogEverythingScan(message);
        }

        public void LogStartupMemoryCheckpoint(string phase, string point)
        {
            BMSLibrary.LogStartupMemoryCheckpoint(phase, point);
        }

        public string GetDisplayedExceptionMessage(Exception exception)
        {
            return BMSLibrary.GetDisplayedExceptionMessage(exception);
        }

        public void QueueEverythingFallbackWarning(string fallbackReason)
        {
            owner.QueueEverythingFallbackWarning(fallbackReason);
        }

        public void QueueFileScanSkippedIncompleteWarning(string failureReason)
        {
            owner.QueueFileScanSkippedIncompleteWarning(failureReason);
        }

        public void QueueEmptyScanWithExistingDbWarning(string failureReason)
        {
            owner.QueueEmptyScanWithExistingDbWarning(failureReason);
        }

        public void ApplyFileScanStorageMutation(SongTableFileCheckResult fileCheckResult, string reason)
        {
            owner.ApplyFileScanStorageMutation(fileCheckResult, reason);
        }

        public BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => owner.CurrentOptionsSnapshot;

        public List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options)
        {
            return owner.CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
        }

        public List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> builtinSourceDirectories,
            BmsLibraryOptionsSnapshot options)
        {
            return owner.CreateLr2SongDbSyncLr2FolderPruneDirectories(
                rootDirectories,
                builtinSourceDirectories,
                options: options);
        }

        public Lr2FolderFileDbSyncResult SyncLr2FolderFileRows(
            BmsLibraryOptionsSnapshot options,
            Lr2SongDbSyncRequest request,
            string reason,
            string logName,
            bool allowPrune = true,
            IReadOnlyCollection<string> pruneExcludedDirectories = null,
            IReadOnlyCollection<string> pruneExcludedPaths = null,
            bool scopeReadLr2FolderRowsOnly = false,
            bool updateParentDirectoryRowsForPreservedItems = true)
        {
            return owner.SyncLr2FolderFileRows(
                options,
                request,
                reason,
                logName,
                allowPrune,
                pruneExcludedDirectories,
                pruneExcludedPaths,
                scopeReadLr2FolderRowsOnly,
                updateParentDirectoryRowsForPreservedItems);
        }

        public Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
        {
            return owner.CreateLr2SongDbSyncAppManagedOutputScope();
        }

        public Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
        {
            return owner.CreateCurrentLr2BuiltinCustomFolderSettings(nowUtc);
        }

        public bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options)
        {
            return owner.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
        }

        public void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            owner.CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(options, fileCheckResult, reason);
        }



        public void UpsertChartInfoIndexRows(
            IEnumerable<LR2SongDBExtended.chart_info> rows,
            string reason,
            bool dispatchPresentation = true)
        {
            owner.UpsertChartInfoIndexRows(rows, reason, dispatchPresentation);
        }

        public void DispatchWarningPresentationChanged(string reason)
        {
            owner.DispatchWarningPresentationChanged(reason);
        }

        public void CaptureLr2SongDbSyncScanSurface(
            BmsLibraryOptionsSnapshot options,
            IEnumerable<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult)
        {
            owner.CaptureLr2SongDbSyncScanSurface(options, rootDirectories, fileCheckResult);
        }

        public void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            owner.CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(options, fileCheckResult, reason);
        }

        public void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result)
        {
            owner.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, result);
        }
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
        ResetEverythingFallbackWarningQueue();
        bool songTblLoad = !isScoreOnly;
        bool startupFileScanRequired = isStartup && !string.IsNullOrWhiteSpace(startupRequiredFileScanReason);
        bool songTblFileCheck = mode == LibraryInitializeMode.FullReinitialize || (isStartup && (options.ScanBmsFilesOnStartup || startupFileScanRequired));
        bool setMaintenanceInfo = !isScoreOnly;
        bool flag = !isScoreOnly;
        bool fileScanLifecycleStarted = false;
        long fileScanGeneration = 0L;
        if (startupFileScanRequired)
        {
            LogInstallPerformance("startup_file_scan_required reason=" + startupRequiredFileScanReason + " scanSetting=" + options.ScanBmsFilesOnStartup.ToString().ToLowerInvariant());
        }
        if (songTblFileCheck)
        {
            List<string> fileCheckPrefetchDirectories = getBMSDirectories();
            fileScanGeneration = libraryFileScanPipelineOwner.BeginFileScanRequest(
                options,
                fileCheckPrefetchDirectories,
                isStartup ? "initialize" : "full_reinitialize",
                scannerLabel => ReportLibraryInitializationProgress(
                    LibraryInitializationProgressStage.FileEnumeration,
                    scannerLabel,
                    force: true));
            fileScanLifecycleStarted = true;
        }
        DateTime now;
        InitializationExecutionResult initializeResult;
        try
        {
            GC.Collect();
            NLogWrapper.DebuggerLogger?.Trace("hazimari: " + GC.GetTotalMemory(forceFullCollection: false));
            LogInstallPerformance("init_library_enter mode=" + mode + " songTblLoad=" + songTblLoad.ToString().ToLowerInvariant() + " songTblFileCheck=" + songTblFileCheck.ToString().ToLowerInvariant() + " setMaintenanceInfo=" + setMaintenanceInfo.ToString().ToLowerInvariant() + " installTblCheck=" + flag.ToString().ToLowerInvariant() + " rwlockInitAll currentRead=" + rwlockBMSFilesInitializedAll.CurrentReadCount + " lockingRead=" + rwlockBMSFilesInitializedAll.LockingReadCount + " lockingWrite=" + rwlockBMSFilesInitializedAll.LockingWriteCount + " waitingWrite=" + rwlockBMSFilesInitializedAll.WaitingWriteCount);
            if (isStartup)
            {
                TryImportChartInfoMetadataBundleAtStartup();
            }
            startupInstallReadinessState.Reset();
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
                            _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, trackLibraryDatabaseProgress: true);
                            if (songTblLoad)
                            {
                                startupInstallReadinessState.MarkCatalogLoaded();
                            }
                            if (songTblFileCheck)
                            {
                                libraryFileScanPipelineOwner.StartActiveNormalFolderMtimeSnapshot(fileScanGeneration);
                            }
                        }
                    },
                    delegate
                    {
                        _initialize(
                            songTblLoad: false,
                            scoreTblrLoad: false,
                            songTblFileCheck,
                            setMainteInfo: false,
                            updateIrScore: true,
                            installTblCheck: false,
                            trackLibraryFileCheckProgress: true,
                            fileScanGeneration: fileScanGeneration,
                            fileScanReason: isStartup ? "initialize" : "full_reinitialize");
                        if (!isScoreOnly)
                        {
                            startupInstallReadinessState.MarkDestinationResourceIndexReady();
                        }
                    },
                    delegate
                    {
                        _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, flag);
                        if (flag)
                        {
                            startupInstallReadinessState.MarkPendingPackagesRestored();
                        }
                    });
                scheduleDeferredInstallableMaintenance = setMaintenanceInfo;
                TimeSpan timeSpan = DateTime.Now - now;
                NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());
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
        if (flag)
        {
            if (startupInstallReadinessState.CanStartInstallEstimation())
            {
                BackgroundPendingEstimatePreparationResult startupEstimatePreparation;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                {
                    using (rwlockPendingInstallCharts.GetWriterGuard())
                    {
                        using (rwlockBMSFiles.GetReaderGuard())
                        {
                            startupEstimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(ChartPackagesPending, PendingInstallEstimateBatchSource.StartupRestore);
                        }
                    }
                }
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
                        batchSourceSnapshot: startupEstimatePreparation.BatchSourceSnapshot));
                }
            }
            else
            {
                LogInstallPerformance("startup_install_estimation_blocked reason=" + startupInstallReadinessState.GetInstallEstimationBlockedReason());
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
            installableLookupCacheSnapshot = directoryResourceLookupCache;
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
        if (startupInstallReadinessState.TryMarkInstallEstimationReady())
        {
            LogInstallPerformance("startup_install_estimation_ready elapsedMs=" + installableElapsedMs
                + " catalogRows=" + catalogRowCount
                + " bmsonRows=" + bmsonRowCount
                + " resourceIndexReady=" + startupInstallReadinessState.DestinationResourceIndexReady.ToString().ToLowerInvariant()
                + " resourceIndexDirectories=" + resourceIndexDirectoryCount
                + " pendingPackages=" + pendingPackageCount
                + " pendingEstimateQueueBatches=" + pendingEstimateQueueBatchCount
                + " lazy_hash_cache_entries=" + (installableLookupCacheSnapshot?.LazyHashCacheEntryCount ?? 0)
                + " lazy_hash_build_ms=" + (installableLookupCacheSnapshot?.LazyHashBuildMs ?? 0L)
                + " lazy_hash_lookup_count=" + (installableLookupCacheSnapshot?.LazyHashLookupCount ?? 0L));
        }
        if (startupInstallReadinessState.TryMarkInstallReady())
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
    }

    private void QueuePostInitializeGarbageCollection(string reason)
    {
        if (TrySkipForShutdown("post_initialize_gc", reason))
        {
            return;
        }
        const int delayMs = 30000;
        Task.Run(async delegate
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (IsShutdownRequested)
                {
                    LogInstallPerformance("post_initialize_gc skipped reason=shutdown_requested requestReason=" + (reason ?? "unknown"));
                    return;
                }
                var stopwatch = Stopwatch.StartNew();
                GC.Collect();
                stopwatch.Stop();
                long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                NLogWrapper.DebuggerLogger?.Trace("owari: " + managedBytes);
                LogInstallPerformance("post_initialize_gc done"
                    + " reason=" + (reason ?? "unknown")
                    + " delayMs=" + delayMs
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " managedBytes=" + managedBytes);
            }
            catch (Exception ex)
            {
                LogInstallPerformanceWarn("post_initialize_gc failed"
                    + " reason=" + (reason ?? "unknown")
                    + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }
        }).Logging("PostInitializeGarbageCollection");
    }

    private void TryImportChartInfoMetadataBundleAtStartup()
    {
        catalogChartInfoOwner.TryImportMetadataBundle(AppDomain.CurrentDomain.BaseDirectory, dbGateway);
    }

    /// <summary>
    /// Initialize から呼ばれる実際の初期化内部ロジックです。
    /// song.db からのデータ再取得、BMS ファイルのディレクトリ走査、スコア反映、保守テーブルチェックを順次実行します。
    /// </summary>
    private void _initialize(
        bool songTblLoad = true,
        bool scoreTblrLoad = true,
        bool songTblFileCheck = true,
        bool setMainteInfo = true,
        bool updateIrScore = true,
        bool installTblCheck = true,
        bool trackLibraryDatabaseProgress = false,
        bool trackLibraryFileCheckProgress = false,
        long fileScanGeneration = 0L,
        string fileScanReason = "initialize")
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
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool scoreOnlyLoad = !songTblLoad && scoreTblrLoad && !songTblFileCheck && !setMainteInfo && !installTblCheck;
        bool logRootNormalizationForFileScan = songTblFileCheck;
        List<string> bMSDirectories = getBMSDirectories(out BmsSearchRootNormalizationSnapshot rootNormalization);
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
            using (rwlockBMSFiles.GetWriterGuard())
            {
                SongTableLoadResult songTableLoadResult = initializationService.LoadSongTable(
                    dbGateway,
                    options,
                    dialogService,
                    fileMutationService,
                    targetOnlyFileMutationOptions,
                    GetDisplayedExceptionMessage,
                    LogInstallPerformance,
                    message => NLogWrapper.DebuggerLogger?.Trace(message));
                var stopwatchBmsFilesAssign = Stopwatch.StartNew();
                var stopwatchBmsOnlyAssign = Stopwatch.StartNew();
                BMSFiles = songTableLoadResult.LoadedFiles;
                stopwatchBmsOnlyAssign.Stop();
                var stopwatchBmsonAssign = Stopwatch.StartNew();
                BmsonSongs = songTableLoadResult.LoadedBmsonSongs;
                stopwatchBmsonAssign.Stop();
                stopwatchBmsFilesAssign.Stop();
                songTableLoadResult.BmsFilesAssignMs = stopwatchBmsFilesAssign.ElapsedMilliseconds;
                LogInstallPerformance("song_tbl_load_breakdown song_table_load_ms=" + songTableLoadResult.SongTableLoadMs + " song_normalize_loop_ms=" + songTableLoadResult.SongNormalizeLoopMs + " folder_table_load_ms=" + songTableLoadResult.FolderTableLoadMs + " folder_normalize_loop_ms=" + songTableLoadResult.FolderNormalizeLoopMs + " fix_apply_ms=" + songTableLoadResult.FixApplyMs + " bmsfiles_assign_ms=" + songTableLoadResult.BmsFilesAssignMs + " bmsfiles_assign_bms_ms=" + stopwatchBmsOnlyAssign.ElapsedMilliseconds + " bmsfiles_assign_bmson_ms=" + stopwatchBmsonAssign.ElapsedMilliseconds + " commit_ms=" + songTableLoadResult.CommitMs);
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
                        + " rows=" + scoreTableLoadResult.Scores.Count
                        + " beatorajaRows=" + scoreTableLoadResult.BeatorajaScoresBySha256.Count
                        + " lr2Id=" + scoreTableLoadResult.LR2Id
                        + " lr2PlayHistorySchemaStatus=" + (scoreTableLoadResult.Lr2PlayHistorySchemaCheckResult?.Status.ToString() ?? "Unknown"));
                    lr2PlayHistorySchemaCheckResult = scoreTableLoadResult.Lr2PlayHistorySchemaCheckResult;
                    activeScoreSource = scoreTableLoadResult.ActiveScoreSource;
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
                    LR2ID = 0;
                    beatorajaScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
                    BMSScores = [];
                }
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
            libraryFileScanPipelineOwner.ApplyActiveFileScan(
                fileScanGeneration,
                trackLibraryFileCheckProgress,
                installDestinationStateOwner.CreateCleanupSnapshot());
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
                ApplyOwnedCatalogMaintenance("initialize_set_maintenance");
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
        if (updateIrScore
            && activeScoreSource == ActiveScoreSource.Lr2
            && lr2ScoreDBPath != null
            && (options.EnableDownloadLr2IrScoreAndDetectUnsent || options.UpdateLr2IrRankingCacheOnStartup))
        {
            QueueDeferredRankingRefresh("initialize_update_ir_score");
        }
        if (installTblCheck)
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    ChartPackagesPending.Clear();
                    ChartPackagesInstalled.Clear();
                    InstallTableLoadResult installTableLoadResult = initializationService.LoadInstallTable(
                        dbGateway,
                        ContainsInstalledChartUnsafe);
                    if (installTableLoadResult.StaleInstallPaths.Count > 0)
                    {
                        dbGateway.DeleteInstallRows(installTableLoadResult.StaleInstallPaths);
                    }
                    ChartPackagesPending.AddRange(installTableLoadResult.PendingPackages);
                    installTblCheckMs = installTableLoadResult.TotalMs;
                }
            }
        }
        stopwatchInitialize.Stop();
        LogInstallPerformance("init_library_internal song_tbl_load_ms=" + songTblLoadMs + " score_tbl_load_ms=" + scoreTblLoadMs + " song_tbl_file_check_ms=" + songTblFileCheckMs + " set_maintenance_ms=" + setMaintenanceMs + " set_mode_ms=" + setModeMs + " set_health_ms=" + setHealthMs + " set_zero_note_ms=" + setZeroNoteMs + " install_tbl_check_ms=" + installTblCheckMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
    }

    public void ReloadFileDiff()
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(ReloadFileDiff)))
        {
            return;
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<string> bmsDirectories = getBMSDirectories(out BmsSearchRootNormalizationSnapshot rootNormalization);
        ResetEverythingFallbackWarningQueue();
        long fileScanGeneration = 0L;
        var stopwatch = Stopwatch.StartNew();
        LogBmsSearchRootNormalization("reload_file_diff", options, rootNormalization, bmsDirectories);
        LogInstallPerformance("library_file_diff_reload start directories=" + bmsDirectories.Count);
        try
        {
            using (rwlockBMSFilesInitializedAll.GetWriterGuard())
            {
                fileScanGeneration = libraryFileScanPipelineOwner.BeginFileScanRequest(
                    options,
                    bmsDirectories,
                    "reload_file_diff",
                    scannerLabel => ReportLibraryInitializationProgress(
                        LibraryInitializationProgressStage.FileEnumeration,
                        scannerLabel,
                        force: true));
                libraryFileScanPipelineOwner.StartActiveNormalFolderMtimeSnapshot(fileScanGeneration);
                SongTableFileCheckResult result = libraryFileScanPipelineOwner.ApplyActiveFileScan(
                    fileScanGeneration,
                    trackLibraryFileCheckProgress: true,
                    installDestinationCleanupSnapshot: installDestinationStateOwner.CreateCleanupSnapshot());
                stopwatch.Stop();
                LogInstallPerformance("library_file_diff_reload done added=" + result.BmsAddedTargetCount
                    + " deleted=" + result.BmsDeletedTargetCount
                    + " bmsonUpserted=" + result.BmsonUpsertTargetCount
                    + " bmsonDeleted=" + result.BmsonDeletedTargetCount
                    + " dbCommitChunks=" + result.DbCommitChunks
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
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
        Task.Run(() => ShowEverythingFallbackWarningSafely(fallbackReason, epoch)).Logging("EverythingFallbackWarningDialog");
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
        Task.Run(() => ShowFileScanSkippedIncompleteWarningSafely(failureReason)).Logging("FileScanSkippedIncompleteWarningDialog");
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
        Task.Run(() => ShowEmptyScanWithExistingDbWarningSafely(failureReason)).Logging("EmptyScanWithExistingDbWarningDialog");
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
        Dispatcher dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            LogEverythingScan("everything fallback warning queued target=ui_dispatcher fallbackReason=" + (fallbackReason ?? string.Empty));
            dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
            {
                ShowEverythingFallbackWarningSafely(fallbackReason, epoch);
            });
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
        Dispatcher dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            LogEverythingScan("file scan incomplete warning queued target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
            dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
            {
                ShowFileScanSkippedIncompleteWarningSafely(failureReason);
            });
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
        Dispatcher dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            LogEverythingScan("empty scan with existing db warning queued target=ui_dispatcher failureReason=" + (failureReason ?? string.Empty));
            dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
            {
                ShowEmptyScanWithExistingDbWarningSafely(failureReason);
            });
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
    private bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options)
    {
        if (options?.OperationModeLR2DB != true)
        {
            return false;
        }

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        Lr2SongDbSyncStatusSnapshot status = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature,
            DateTime.UtcNow);
        return status == null || status.Status != Lr2SongDbSyncStatusKind.Completed;
    }
    private static long RestartElapsed(Stopwatch stopwatch)
    {
        long elapsedMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        return elapsedMs;
    }

    internal Lr2SongDbSyncPreparedDataSurface SyncLr2BuiltinCustomFolderRows(string reason)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (options?.OperationModeLR2DB != true)
        {
            return Lr2SongDbSyncPreparedDataSurface.Empty;
        }

        List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
        Lr2FolderFileCandidateSnapshot candidates = builtinSourceDirectories.Count > 0
            ? CreateLr2SongDbSyncLr2FolderFileCandidates(
                builtinSourceDirectories,
                options.LR2RootPath,
                CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow))
            : new Lr2FolderFileCandidateSnapshot(
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: true);
        var request = new Lr2SongDbSyncRequest
        {
            RootDirectories = [],
            Lr2FolderDiscoveryDirectories = builtinSourceDirectories,
            Lr2FolderPruneDirectories = CreateLr2BuiltinCustomFolderPruneDirectories(),
            Lr2FolderFilePaths = candidates.Paths,
            Lr2FolderFileEntries = candidates.EntriesByPath,
            Lr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete,
            Lr2RootPath = options.LR2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDir,
            Lr2AdditionalNormalCustomFolderOutputBaseDirs = options.LR2CustomFolderAdditionalOutputBaseDirs,
            Lr2RootCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDirRootType,
            Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
        };
        PrepareLr2FolderParentDirectoryEntrySurface(request);
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
            builtinSourceDirectories,
            request.DirectoryEntries.Keys);
        ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, builtinSourceDirectories);
        SyncLr2FolderFileRows(options, request, reason, "lr2_builtin_folder_scoped_sync");
        return new Lr2SongDbSyncPreparedDataSurface(
            builtinSourceDirectories,
            candidates.Paths,
            candidates.EntriesByPath,
            request.DirectoryEntries,
            textMetadataSnapshot.FolderInfoCandidates.Paths,
            textMetadataSnapshot.FolderInfoCandidates.EntriesByPath,
            textMetadataSnapshot.TextFileDirectories,
            candidates.DiscoveryComplete);
    }

    internal void SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange(string reason)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (options?.OperationModeLR2DB != true)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = getBMSDirectories();
        List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
        List<string> discoveryDirectories = NormalizeDistinctDirectories(CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options));
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
        if (!appManagedOutputScope.IsComplete)
        {
            LogInstallPerformance("lr2folder_settings_output_base_sync skipped"
                + " reason=" + (reason ?? "unknown")
                + " detail=app_managed_scope_incomplete"
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return;
        }

        Lr2FolderFileCandidateSnapshot candidates = discoveryDirectories.Count > 0
            ? CreateLr2SongDbSyncLr2FolderFileCandidates(
                discoveryDirectories,
                options.LR2RootPath,
                CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
                appManagedOutputScope.Directories)
            : new Lr2FolderFileCandidateSnapshot(
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: true);
        candidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
            candidates.Paths,
            candidates.EntriesByPath,
            appManagedOutputScope.FilePaths,
            candidates.DiscoveryComplete,
            out int appManagedCandidateCount,
            appManagedOutputScope.Directories);

        List<string> pruneDirectories = NormalizeDistinctDirectories(
            CreateLr2SongDbSyncLr2FolderPruneDirectories(roots, builtinSourceDirectories, options: options));
        var request = new Lr2SongDbSyncRequest
        {
            RootDirectories = roots,
            Lr2FolderDiscoveryDirectories = discoveryDirectories,
            Lr2FolderPruneDirectories = pruneDirectories,
            Lr2FolderFilePaths = candidates.Paths,
            Lr2FolderFileEntries = candidates.EntriesByPath,
            Lr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete,
            Lr2RootPath = options.LR2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDir,
            Lr2AdditionalNormalCustomFolderOutputBaseDirs = options.LR2CustomFolderAdditionalOutputBaseDirs,
            Lr2RootCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDirRootType,
            Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
        };
        PrepareLr2FolderParentDirectoryEntrySurface(request);
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
            request.Lr2FolderDiscoveryDirectories,
            request.DirectoryEntries.Keys);
        ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, request.Lr2FolderDiscoveryDirectories);
        Lr2FolderFileDbSyncResult syncResult = SyncLr2FolderFileRows(
            options,
            request,
            reason,
            "lr2folder_settings_output_base_sync",
            allowPrune: true,
            pruneExcludedDirectories: appManagedOutputScope.Directories,
            pruneExcludedPaths: appManagedOutputScope.PruneExcludedPaths,
            scopeReadLr2FolderRowsOnly: true);
        LogInstallPerformance("lr2folder_settings_output_base_sync summary"
            + " reason=" + (reason ?? "unknown")
            + " roots=" + roots.Count
            + " discoveryDirs=" + discoveryDirectories.Count
            + " candidates=" + candidates.Paths.Count
            + " appManagedFiltered=" + appManagedCandidateCount
            + " pruneDirs=" + pruneDirectories.Count
            + " existingRows=" + (syncResult?.ExistingReadCount ?? 0)
            + " upserted=" + (syncResult?.UpsertedCount ?? 0)
            + " deleted=" + (syncResult?.DeletedCount ?? 0)
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
    }

    private Lr2FolderFileDbSyncResult SyncLr2FolderFileRows(
        BmsLibraryOptionsSnapshot options,
        Lr2SongDbSyncRequest request,
        string reason,
        string logName,
        bool allowPrune = true,
        IReadOnlyCollection<string> pruneExcludedDirectories = null,
        IReadOnlyCollection<string> pruneExcludedPaths = null,
        bool scopeReadLr2FolderRowsOnly = false,
        bool updateParentDirectoryRowsForPreservedItems = true)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            PrepareLr2FolderParentDirectoryEntrySurface(request);
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath =
                Lr2SongDbSyncService.CreateExistingLr2FolderRowMap(songDb, request);
            Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult syncItems =
                Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                    request.Lr2FolderFilePaths,
                    request,
                    request.Lr2FolderFileEntries,
                    path => existingRowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null);
            IReadOnlyCollection<Lr2FolderFileSyncItem> parentDirectorySyncItems = updateParentDirectoryRowsForPreservedItems
                ? syncItems.Items
                : [.. syncItems.Items.Where(item => item != null && !item.PreserveExistingRowOnly)];
            Lr2FolderDirectoryMetadataSnapshot parentDirectoryMetadata =
                Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(parentDirectorySyncItems, request);
            bool effectiveAllowPrune = allowPrune
                && request.Lr2FolderFileDiscoveryComplete
                && !syncItems.HasReadFailures;
            string savepoint = songDb.SaveTransactionPoint();
            Lr2FolderFileDbSyncResult syncResult;
            try
            {
                syncResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
                {
                    Items = syncItems.Items,
                    ScopeDirectories = request.Lr2FolderPruneDirectories,
                    DirectoryRowScopeDirectories = Lr2SongDbSyncService.CreateLr2FolderDirectoryRowScopeDirectories(request),
                    DirectoryRowGenerationScopeDirectories = Lr2SongDbSyncService.CreateLr2FolderDirectoryRowGenerationScopeDirectories(request),
                    ScopePaths = request.Lr2FolderFilePaths,
                    PruneExcludedDirectories = pruneExcludedDirectories ?? [],
                    PruneExcludedPaths = pruneExcludedPaths ?? [],
                    DirectoryMetadataResolver = parentDirectoryMetadata.Resolve,
                    GeneratedAtUtc = DateTime.UtcNow,
                    AllowPrune = effectiveAllowPrune,
                    ScopeReadLr2FolderRowsOnly = scopeReadLr2FolderRowsOnly,
                    UpdateParentDirectoryRowsForPreservedItems = updateParentDirectoryRowsForPreservedItems
                });
                songDb.Commit();
            }
            catch
            {
                songDb.RollbackTo(savepoint);
                throw;
            }

            stopwatch.Stop();
            LogInstallPerformance(logName + " done"
                + " reason=" + (reason ?? "unknown")
                + " roots=" + request.Lr2FolderDiscoveryDirectories.Count
                + " candidates=" + request.Lr2FolderFilePaths.Count
                + " discoveryComplete=" + request.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " readFailures=" + syncItems.HasReadFailures.ToString().ToLowerInvariant()
                + " allowPrune=" + effectiveAllowPrune.ToString().ToLowerInvariant()
                + " pruneDeferred=" + (!effectiveAllowPrune && allowPrune == false).ToString().ToLowerInvariant()
                + " pruneExcludedDirs=" + (pruneExcludedDirectories?.Count ?? 0)
                + " pruneExcludedPaths=" + (pruneExcludedPaths?.Count ?? 0)
                + " existingRows=" + syncResult.ExistingReadCount
                + " existingExactRows=" + syncResult.ExistingExactReadCount
                + " existingScopeRows=" + syncResult.ExistingScopeReadCount
                + " generated=" + syncResult.GeneratedCount
                + " preserved=" + syncResult.PreservedCount
                + " upserted=" + syncResult.UpsertedCount
                + " deleted=" + syncResult.DeletedCount
                + " skippedUnsupported=" + syncResult.SkippedUnsupportedPathCount
                + " skippedMissingMetadata=" + syncResult.SkippedMissingMetadataCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return syncResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            MarkLr2SongDbSyncIncomplete(
                options,
                runId: logName,
                stage: logName + "_failed",
                detail: logName + "_failed: " + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "),
                logReason: logName + "_failed");
            LogInstallPerformanceWarn(logName + " failed"
                + " reason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            return null;
        }
    }

    private static void PrepareLr2FolderParentDirectoryEntrySurface(Lr2SongDbSyncRequest request)
    {
        if (request == null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var stopwatchStage = Stopwatch.StartNew();
        IReadOnlyCollection<string> parentDirectoryTargets = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateTargets(
            (request.Lr2FolderFilePaths ?? []).Concat(request.Lr2FolderFileEntries?.Keys ?? []),
            request.RootDirectories,
            request.Lr2NormalCustomFolderOutputBaseDir,
            request.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
            request.Lr2RootCustomFolderOutputBaseDir,
            request.Lr2BuiltinFolderSourceDirectories);
        long targetMs = RestartElapsed(stopwatchStage);
        if (parentDirectoryTargets.Count == 0)
        {
            LogInstallPerformance("lr2folder_parent_directory_surface targets=0 targetMs=" + targetMs + " totalMs=" + stopwatch.ElapsedMilliseconds);
            return;
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> parentDirectoryEntries = CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
            request.DirectoryEntries,
            request.Lr2FolderDiscoveryDirectories,
            parentDirectoryTargets);
        long entryMs = RestartElapsed(stopwatchStage);
        request.DirectoryEntries = MergeMissingLr2DirectoryEntrySurface(
            request.DirectoryEntries,
            parentDirectoryEntries);
        long overlayMs = RestartElapsed(stopwatchStage);
        LogInstallPerformance("lr2folder_parent_directory_surface"
            + " targets=" + parentDirectoryTargets.Count
            + " entries=" + (parentDirectoryEntries?.Count ?? 0)
            + " targetMs=" + targetMs
            + " entryMs=" + entryMs
            + " overlayMs=" + overlayMs
            + " totalMs=" + stopwatch.ElapsedMilliseconds);
    }

    private void CaptureLr2SongDbSyncScanSurface(
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult)
    {
        if (options?.OperationModeLR2DB != true
            || fileCheckResult?.Lr2ScanSurfaceAvailable != true)
        {
            return;
        }

        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeFullPathOrOriginal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return;
        }
        if (fileCheckResult.Lr2NormalFolderSyncFailed
            || fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount > 0
            || fileCheckResult.Lr2NormalFolderInfoReadFailureCount > 0)
        {
            int preservedGeneration;
            lock (lockLr2SongDbSyncScanSurface)
            {
                preservedGeneration = lr2SongDbSyncScanSurfaceSnapshot?.Generation ?? 0;
                appManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    discoveryComplete: false);
            }
            LogInstallPerformance("lr2_song_db_sync_scan_surface skipped"
                + " reason=normal_folder_sync_unapplied"
                + " failed=" + fileCheckResult.Lr2NormalFolderSyncFailed.ToString().ToLowerInvariant()
                + " skippedMissingMetadata=" + fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount
                + " folderInfoReadFailures=" + fileCheckResult.Lr2NormalFolderInfoReadFailureCount
                + " preservedGeneration=" + preservedGeneration);
            return;
        }

        IReadOnlyList<string> lr2FolderDiscoveryDirectories = fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories ?? [];
        IReadOnlyList<string> appManagedOutputDirectories = [];
        IReadOnlyList<string> appManagedOutputFilePaths = [];
        int appManagedCandidateCount = 0;
        Lr2FolderFileCandidateSnapshot lr2FolderCandidates;
        bool reusedFilteredLr2FolderCandidates = fileCheckResult.Lr2ScanLr2FolderCandidatesAlreadyFiltered;
        if (reusedFilteredLr2FolderCandidates)
        {
            lr2FolderCandidates = new Lr2FolderFileCandidateSnapshot(
                fileCheckResult.Lr2ScanLr2FolderFilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileEntries,
                fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete);
            appManagedCandidateCount = fileCheckResult.Lr2ScanLr2FolderAppManagedFilteredCount;
        }
        else
        {
            Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
            appManagedOutputDirectories = appManagedOutputScope.Directories;
            appManagedOutputFilePaths = appManagedOutputScope.FilePaths;
            if (!appManagedOutputScope.IsComplete)
            {
                lr2FolderCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
                appManagedCandidateCount = 0;
            }
            else
            {
                lr2FolderCandidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                    fileCheckResult.Lr2ScanLr2FolderFilePaths,
                    fileCheckResult.Lr2ScanLr2FolderFileEntries,
                    appManagedOutputFilePaths,
                    fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete,
                    out appManagedCandidateCount,
                    appManagedOutputScope.Directories);
            }
        }
        IReadOnlyList<string> lr2FolderFilePaths = lr2FolderCandidates.Paths;
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries = lr2FolderCandidates.EntriesByPath;
        bool lr2FolderFileDiscoveryComplete = lr2FolderCandidates.DiscoveryComplete;
        if (lr2FolderDiscoveryDirectories.Count == 0)
        {
            int previousGeneration = 0;
            lock (lockLr2SongDbSyncScanSurface)
            {
                previousGeneration = lr2SongDbSyncScanSurfaceSnapshot?.Generation ?? 0;
                lr2SongDbSyncScanSurfaceSnapshot = null;
                appManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    discoveryComplete: false);
                lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = 0;
            }
            LogInstallPerformance("lr2_song_db_sync_scan_surface skipped"
                + " reason=missing_lr2folder_surface"
                + " roots=" + roots.Count
                + " invalidatedGeneration=" + previousGeneration);
            return;
        }
        Lr2SongDbSyncScanSurfaceSnapshot snapshot;
        lock (lockLr2SongDbSyncScanSurface)
        {
            int generation = lr2SongDbSyncScanSurfaceGeneration == int.MaxValue
                ? 1
                : lr2SongDbSyncScanSurfaceGeneration + 1;
            lr2SongDbSyncScanSurfaceGeneration = generation;
            snapshot = new Lr2SongDbSyncScanSurfaceSnapshot(
                generation,
                roots,
                fileCheckResult.Lr2ScanNormalFolderDirectoryPaths,
                fileCheckResult.Lr2ScanDirectoryEntries,
                fileCheckResult.Lr2ScanNormalFolderDirectoryEntries,
                fileCheckResult.Lr2ScanFolderInfoFilePaths,
                fileCheckResult.Lr2ScanFolderInfoFileEntries,
                fileCheckResult.Lr2ScanTextFileDirectories,
                lr2FolderDiscoveryDirectories,
                lr2FolderFilePaths,
                lr2FolderFileEntries,
                lr2FolderFileDiscoveryComplete,
                OwnedChartCollectionVersion,
                catalogStorageRowsOwner.BmsRowsVersion,
                catalogStorageRowsOwner.BmsonRowsVersion);
            lr2SongDbSyncScanSurfaceSnapshot = snapshot;
            appManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                fileCheckResult.Lr2ScanAppManagedCustomFolderOutputFileEntries,
                fileCheckResult.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete);
            lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = 0;
        }
        LogInstallPerformance("lr2_song_db_sync_scan_surface captured"
            + " generation=" + snapshot.Generation
            + " roots=" + snapshot.RootDirectories.Count
            + " normalFolderDirs=" + snapshot.NormalFolderDirectoryPaths.Count
            + " directorySurfaceEntries=" + snapshot.DirectoryEntries.Count
            + " folderInfoCandidates=" + snapshot.FolderInfoFilePaths.Count
            + " lr2FolderDiscoveryRoots=" + snapshot.Lr2FolderDiscoveryDirectories.Count
            + " lr2FolderCandidates=" + snapshot.Lr2FolderFilePaths.Count
            + " reusedFilteredLr2FolderCandidates=" + reusedFilteredLr2FolderCandidates.ToString().ToLowerInvariant()
            + " appManagedFiltered=" + appManagedCandidateCount
            + " appManagedScopeDirs=" + (reusedFilteredLr2FolderCandidates
                ? fileCheckResult.Lr2ScanLr2FolderAppManagedScopeDirectoryCount
                : appManagedOutputDirectories.Count)
            + " appManagedExactFiles=" + (reusedFilteredLr2FolderCandidates
                ? fileCheckResult.Lr2ScanLr2FolderAppManagedExactFileCount
                : appManagedOutputFilePaths.Count)
            + " appManagedPhysicalCandidates=" + (fileCheckResult.Lr2ScanAppManagedCustomFolderOutputFilePaths?.Count ?? 0)
            + " appManagedPhysicalDiscoveryComplete=" + fileCheckResult.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete.ToString().ToLowerInvariant()
            + " lr2FolderDiscoveryComplete=" + snapshot.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
            + " textFileDirs=" + snapshot.TextFileDirectories.Count
            + " ownedCollectionVersion=" + snapshot.OwnedCollectionVersion
            + " bmsRowsVersion=" + snapshot.BmsRowsVersion
            + " bmsonRowsVersion=" + snapshot.BmsonRowsVersion);
    }

    private void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (options?.OperationModeLR2DB != true
            || fileCheckResult == null)
        {
            ClearLr2SongDbSyncFileDiffFreshnessSnapshot("file_diff_unavailable_" + (reason ?? "unknown"));
            return;
        }

        ChartInfoOwnerVersionSnapshot version = catalogChartInfoOwner.CaptureOwnerVersionSnapshot();
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface;
        lock (lockLr2SongDbSyncScanSurface)
        {
            scanSurface = lr2SongDbSyncScanSurfaceSnapshot;
        }
        string missReason = null;
        if (scanSurface == null
            || scanSurface.OwnedCollectionVersion != version.OwnedCollectionVersion
            || scanSurface.BmsRowsVersion != version.BmsRowsVersion
            || scanSurface.BmsonRowsVersion != version.BmsonRowsVersion)
        {
            missReason = "scan_surface_not_current";
        }
        else if (!fileCheckResult.HasDbDiff)
        {
            missReason = "no_db_diff";
        }
        else if (version.BmsOwnerCount <= 0)
        {
            missReason = "no_bms_rows";
        }
        bool canVerifyFullSongRows = fileCheckResult.BmsAddedTargetCount >= version.BmsOwnerCount
            && fileCheckResult.InlineMaintenanceBmsCount >= version.BmsOwnerCount;
        bool hasTransientNewInsertSkipRows = fileCheckResult.NewlyInsertedBmsPaths.Count > 0;
        if (missReason == null && !canVerifyFullSongRows && !hasTransientNewInsertSkipRows)
        {
            missReason = "no_fresh_song_rows";
        }
        else if (missReason == null && fileCheckResult.InlineMaintenanceFailedCount > 0)
        {
            missReason = "inline_maintenance_failed";
        }
        else if (missReason == null && fileCheckResult.BmsMovedHashRelinkAmbiguousCount > 0)
        {
            missReason = "ambiguous_hash_relink";
        }
        else if (missReason == null
            && (fileCheckResult.Lr2NormalFolderSyncFailed
            || fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount > 0
            || fileCheckResult.Lr2NormalFolderInfoReadFailureCount > 0))
        {
            missReason = "normal_folder_sync_unapplied";
        }

        if (missReason != null)
        {
            ClearLr2SongDbSyncFileDiffFreshnessSnapshot(missReason + "_" + (reason ?? "unknown"));
            LogInstallPerformance("lr2_song_db_sync_file_diff_freshness skipped"
                + " reason=" + (reason ?? "unknown")
                + " skipReason=" + missReason
                + " bmsOwners=" + version.BmsOwnerCount
                + " bmsonOwners=" + version.BmsonOwnerCount
                + " bmsTargets=" + fileCheckResult.BmsAddedTargetCount
                + " newInsertSkipRows=" + fileCheckResult.NewlyInsertedBmsPaths.Count
                + " inlineMaintenanceBms=" + fileCheckResult.InlineMaintenanceBmsCount
                + " inlineMaintenanceFailed=" + fileCheckResult.InlineMaintenanceFailedCount
                + " scanSurfaceGeneration=" + (scanSurface?.Generation ?? 0));
            return;
        }

        var snapshot = new Lr2SongDbSyncFileDiffFreshnessSnapshot(
            reason ?? "unknown",
            scanSurface.Generation,
            version.OwnedCollectionVersion,
            version.BmsRowsVersion,
            version.BmsonRowsVersion,
            version.BmsOwnerCount,
            version.BmsonOwnerCount,
            fileCheckResult.BmsAddedTargetCount,
            fileCheckResult.BmsDeletedTargetCount,
            fileCheckResult.BmsDateOnlyUpdateCount,
            fileCheckResult.BmsTextOnlyUpdateCount,
            fileCheckResult.BmsMovedHashRelinkCount,
            fileCheckResult.BmsMovedHashRelinkAmbiguousCount,
            fileCheckResult.InlineChartInfoTargetCount,
            fileCheckResult.InlineChartInfoSuccessCount,
            fileCheckResult.InlineChartInfoCurrentSkippedCount,
            fileCheckResult.InlineChartInfoParseFailedCount,
            fileCheckResult.InlineMaintenanceTargetCount,
            fileCheckResult.InlineMaintenanceBmsCount,
            fileCheckResult.InlineMaintenanceBmsonCount,
            fileCheckResult.InlineMaintenanceFailedCount,
            fileCheckResult.NewlyInsertedBmsPaths);
        lock (lockLr2SongDbSyncFileDiffFreshness)
        {
            lr2SongDbSyncFileDiffFreshnessSnapshot = snapshot;
        }
        LogInstallPerformance("lr2_song_db_sync_file_diff_freshness captured"
            + " reason=" + snapshot.Reason
            + " scanSurfaceGeneration=" + snapshot.ScanSurfaceGeneration
            + " bmsOwners=" + snapshot.BmsOwnerCount
            + " bmsonOwners=" + snapshot.BmsonOwnerCount
            + " bmsTargets=" + snapshot.BmsTargetCount
            + " newInsertSkipRows=" + snapshot.TransientSongRowSkipPaths.Count
            + " bmsDeleted=" + snapshot.BmsDeletedCount
            + " inlineChartInfoTargets=" + snapshot.InlineChartInfoTargetCount
            + " inlineMaintenanceBms=" + snapshot.InlineMaintenanceBmsCount
            + " inlineMaintenanceBmson=" + snapshot.InlineMaintenanceBmsonCount
            + " ownedCollectionVersion=" + snapshot.OwnedCollectionVersion
            + " bmsRowsVersion=" + snapshot.BmsRowsVersion
            + " bmsonRowsVersion=" + snapshot.BmsonRowsVersion);
    }

    private void ClearLr2SongDbSyncFileDiffFreshnessSnapshot(string reason)
    {
        bool cleared;
        lock (lockLr2SongDbSyncFileDiffFreshness)
        {
            cleared = lr2SongDbSyncFileDiffFreshnessSnapshot != null;
            lr2SongDbSyncFileDiffFreshnessSnapshot = null;
        }
        if (cleared)
        {
            LogInstallPerformance("lr2_song_db_sync_file_diff_freshness cleared reason=" + (reason ?? "unknown"));
        }
    }

    private void ApplyLr2SongDbSyncPreparedDataSurface(
        string reason,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        var applyStopwatch = Stopwatch.StartNew();
        var lockWaitStopwatch = Stopwatch.StartNew();
        long lockWaitMs = 0;
        long discoveryRootsMs = 0;
        long lr2FolderCandidatesMs = 0;
        long folderInfoCandidatesMs = 0;
        long directoryOverlayMs = 0;
        long textFileDirsMs = 0;
        string mergeResult = "unknown";
        preparedSurface ??= Lr2SongDbSyncPreparedDataSurface.Empty;
        BmsLibraryOptionsSnapshot currentSettings = CurrentOptionsSnapshot;
        Lr2SongDbSyncScanSurfaceSnapshot snapshot = null;
        lock (lockLr2SongDbSyncScanSurface)
        {
            lockWaitStopwatch.Stop();
            lockWaitMs = lockWaitStopwatch.ElapsedMilliseconds;
            lr2SongDbSyncPreparedDataSurface = preparedSurface;
            lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = 0;
            snapshot = lr2SongDbSyncScanSurfaceSnapshot;
            if (snapshot == null)
            {
                mergeResult = "no_scan_surface";
                snapshot = null;
            }
            else if (!preparedSurface.HasPreparedDataSurface)
            {
                mergeResult = "no_prepared_surface";
                snapshot = null;
            }
            else
            {
                var discoveryRootsStopwatch = Stopwatch.StartNew();
                bool discoveryRootsCurrent = ArePathSetsEqual(
                    snapshot.Lr2FolderDiscoveryDirectories,
                    CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(snapshot.RootDirectories, currentSettings));
                discoveryRootsStopwatch.Stop();
                discoveryRootsMs = discoveryRootsStopwatch.ElapsedMilliseconds;
                if (!discoveryRootsCurrent)
                {
                    mergeResult = "lr2folder_roots_changed";
                    snapshot = null;
                }
                else
                {
                    mergeResult = "merged";
                    int generation = lr2SongDbSyncScanSurfaceGeneration == int.MaxValue
                        ? 1
                        : lr2SongDbSyncScanSurfaceGeneration + 1;
                    lr2SongDbSyncScanSurfaceGeneration = generation;
                    var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
                    Lr2FolderFileCandidateSnapshot candidates = MergeLr2FolderFileCandidateSurface(
                        new Lr2FolderFileCandidateSnapshot(
                            snapshot.Lr2FolderFilePaths,
                            snapshot.Lr2FolderFileEntries,
                            snapshot.Lr2FolderFileDiscoveryComplete),
                        preparedSurface);
                    lr2FolderCandidatesStopwatch.Stop();
                    lr2FolderCandidatesMs = lr2FolderCandidatesStopwatch.ElapsedMilliseconds;
                    var folderInfoCandidatesStopwatch = Stopwatch.StartNew();
                    IReadOnlyList<string> mergedFolderInfoPaths = MergePreparedFileSurface(
                        snapshot.FolderInfoFilePaths,
                        snapshot.FolderInfoFileEntries,
                        preparedSurface.FolderInfoFilePaths,
                        preparedSurface.FolderInfoFileEntries,
                        preparedSurface.Lr2FolderScopeDirectories,
                        out IReadOnlyDictionary<string, RootFileEnumerationEntry> mergedFolderInfoEntries);
                    folderInfoCandidatesStopwatch.Stop();
                    folderInfoCandidatesMs = folderInfoCandidatesStopwatch.ElapsedMilliseconds;
                    var textFileDirsStopwatch = Stopwatch.StartNew();
                    IReadOnlyList<string> mergedTextFileDirectories = MergePreparedDirectoryList(
                        snapshot.TextFileDirectories,
                        preparedSurface.TextFileDirectories,
                        preparedSurface.Lr2FolderScopeDirectories);
                    textFileDirsStopwatch.Stop();
                    textFileDirsMs = textFileDirsStopwatch.ElapsedMilliseconds;
                    var directoryOverlayStopwatch = Stopwatch.StartNew();
                    IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries =
                        OverlayLr2DirectoryEntrySurface(snapshot.DirectoryEntries, preparedSurface.DirectoryEntries);
                    directoryOverlayStopwatch.Stop();
                    directoryOverlayMs = directoryOverlayStopwatch.ElapsedMilliseconds;
                    snapshot = new Lr2SongDbSyncScanSurfaceSnapshot(
                        generation,
                        snapshot.RootDirectories,
                        snapshot.NormalFolderDirectoryPaths,
                        directoryEntries,
                        snapshot.NormalFolderDirectoryEntries,
                        mergedFolderInfoPaths,
                        mergedFolderInfoEntries,
                        mergedTextFileDirectories,
                        snapshot.Lr2FolderDiscoveryDirectories,
                        candidates.Paths,
                        candidates.EntriesByPath,
                        candidates.DiscoveryComplete,
                        snapshot.OwnedCollectionVersion,
                        snapshot.BmsRowsVersion,
                        snapshot.BmsonRowsVersion);
                    lr2SongDbSyncScanSurfaceSnapshot = snapshot;
                    lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = generation;
                }
            }
        }
        applyStopwatch.Stop();

        LogInstallPerformance("lr2_song_db_sync_prepared_surface applied"
            + " reason=" + (reason ?? "unknown")
            + " mergeResult=" + mergeResult
            + " scopeDirs=" + preparedSurface.Lr2FolderScopeDirectories.Count
            + " lr2FolderCandidates=" + preparedSurface.Lr2FolderFilePaths.Count
            + " directoryEntries=" + preparedSurface.DirectoryEntries.Count
            + " folderInfoCandidates=" + preparedSurface.FolderInfoFilePaths.Count
            + " textFileDirs=" + preparedSurface.TextFileDirectories.Count
            + " discoveryComplete=" + preparedSurface.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
            + " mergedScanSurfaceGeneration=" + (snapshot?.Generation ?? 0)
            + " lockWaitMs=" + lockWaitMs
            + " discoveryRootsMs=" + discoveryRootsMs
            + " lr2FolderCandidatesMs=" + lr2FolderCandidatesMs
            + " folderInfoCandidatesMs=" + folderInfoCandidatesMs
            + " textFileDirsMs=" + textFileDirsMs
            + " directoryOverlayMs=" + directoryOverlayMs
            + " elapsedMs=" + applyStopwatch.ElapsedMilliseconds);
    }

    private bool HasLr2SongDbSyncPreparedDataSurface()
    {
        lock (lockLr2SongDbSyncScanSurface)
        {
            return lr2SongDbSyncPreparedDataSurface?.HasPreparedDataSurface == true;
        }
    }

    private void ClearLr2SongDbSyncPreparedDataSurface(string reason)
    {
        bool hadSurface;
        lock (lockLr2SongDbSyncScanSurface)
        {
            hadSurface = lr2SongDbSyncPreparedDataSurface?.HasPreparedDataSurface == true;
            lr2SongDbSyncPreparedDataSurface = Lr2SongDbSyncPreparedDataSurface.Empty;
            lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = 0;
        }
        if (hadSurface)
        {
            LogInstallPerformance("lr2_song_db_sync_prepared_surface cleared reason=" + (reason ?? "unknown"));
        }
    }

    private Lr2SongDbSyncPreparedDataSurface TakeLr2SongDbSyncPreparedDataSurface(
        out int appliedScanSurfaceGeneration)
    {
        lock (lockLr2SongDbSyncScanSurface)
        {
            Lr2SongDbSyncPreparedDataSurface surface = lr2SongDbSyncPreparedDataSurface
                ?? Lr2SongDbSyncPreparedDataSurface.Empty;
            appliedScanSurfaceGeneration = lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration;
            lr2SongDbSyncPreparedDataSurface = Lr2SongDbSyncPreparedDataSurface.Empty;
            lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration = 0;
            return surface;
        }
    }

    internal CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
    {
        lock (lockLr2SongDbSyncScanSurface)
        {
            return appManagedCustomFolderOutputPhysicalSurface ?? CustomFolderOutputPhysicalSurface.Empty;
        }
    }

    private Lr2SongDbSyncScanSurfaceSnapshot GetCurrentLr2SongDbSyncScanSurface(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> lr2FolderDiscoveryDirectories,
        Lr2SongDbSyncInputRowSnapshot rowSnapshot,
        out string missReason)
    {
        missReason = string.Empty;
        Lr2SongDbSyncScanSurfaceSnapshot snapshot;
        lock (lockLr2SongDbSyncScanSurface)
        {
            snapshot = lr2SongDbSyncScanSurfaceSnapshot;
        }
        if (snapshot == null)
        {
            missReason = "none";
            return null;
        }
        if (rowSnapshot == null)
        {
            missReason = "row_snapshot";
            return null;
        }
        if (snapshot.OwnedCollectionVersion != rowSnapshot.OwnedCollectionVersion)
        {
            missReason = "owned_collection_version";
            return null;
        }
        if (snapshot.BmsRowsVersion != rowSnapshot.BmsRowsVersion)
        {
            missReason = "bms_rows_version";
            return null;
        }
        if (snapshot.BmsonRowsVersion != rowSnapshot.BmsonRowsVersion)
        {
            missReason = "bmson_rows_version";
            return null;
        }
        if (!ArePathSetsEqual(snapshot.RootDirectories, rootDirectories))
        {
            missReason = "roots";
            return null;
        }
        if (!ArePathSetsEqual(snapshot.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
        {
            missReason = "lr2folder_roots";
            return null;
        }
        return snapshot;
    }

    private bool IsCurrentLr2SongDbSyncScanSurface(Lr2SongDbSyncInput input)
    {
        if (input == null || input.ScanSurfaceGeneration <= 0)
        {
            return true;
        }

        Lr2SongDbSyncScanSurfaceSnapshot snapshot;
        lock (lockLr2SongDbSyncScanSurface)
        {
            snapshot = lr2SongDbSyncScanSurfaceSnapshot;
        }
        return snapshot != null
            && snapshot.Generation == input.ScanSurfaceGeneration
            && snapshot.OwnedCollectionVersion == input.OwnedChartCollectionVersion
            && snapshot.BmsRowsVersion == input.BmsRowsVersion
            && snapshot.BmsonRowsVersion == input.BmsonRowsVersion
            && ArePathSetsEqual(snapshot.RootDirectories, input.RootDirectories)
            && ArePathSetsEqual(snapshot.Lr2FolderDiscoveryDirectories, input.Lr2FolderDiscoveryDirectories);
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
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData = null,
        bool allowIncompleteToQueue = true)
    {
        return Lr2SongDbSyncRequestCoordinator.Queue(this, reason, force, prepareGeneratedData, allowIncompleteToQueue);
    }

    internal bool TryRunLr2SongDbSyncDataPreparation(string reason, Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData)
    {
        return Lr2SongDbSyncRequestCoordinator.TryRunDataPreparation(this, reason, prepareGeneratedData);
    }

    internal Lr2StartupScanBlockerCleanupResult CleanupLr2SongDbSyncStartupScanBlockerFolderRows(string reason)
    {
        return Lr2SongDbSyncRequestCoordinator.CleanupStartupScanBlockerFolderRows(this, reason);
    }

    internal void PublishLr2SongDbSyncExternalStageProgress(string stage, int processedCount, int totalCount, string detail = null)
    {
        Lr2SongDbSyncRequestCoordinator.PublishExternalStageProgress(this, stage, processedCount, totalCount, detail);
    }

    private void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
    {
        lock (lockLr2SongDbSyncStatus)
        {
            lr2SongDbSyncStatus = status?.Clone() ?? new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.NotNeeded
            };
        }
        Lr2SongDbSyncStatusVersion++;
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

    private bool TryBeginLr2SongDbSyncRequest(out int requestVersion)
    {
        lock (lockLr2SongDbSync)
        {
            if (_Lr2SongDbSyncRunning || lr2SongDbSyncMutationInProgress > 0)
            {
                requestVersion = _Lr2SongDbSyncRequestedVersion;
                return false;
            }
            lr2SongDbSyncRequestedVersion++;
            requestVersion = lr2SongDbSyncRequestedVersion;

            lr2SongDbSyncCancellation?.Dispose();
            lr2SongDbSyncCancellation = new CancellationTokenSource();
            Lr2SongDbSyncRequestedVersion = requestVersion;
            Lr2SongDbSyncTotalCount = 0;
            Lr2SongDbSyncProcessedCount = 0;
            Lr2SongDbSyncStage = "queued";
            Lr2SongDbSyncStageProcessedCount = 0;
            Lr2SongDbSyncStageTotalCount = 0;
            Lr2SongDbSyncFailureMessage = string.Empty;
            Lr2SongDbSyncRunning = true;
            return true;
        }
    }

    private void ClearLr2SongDbSyncPrepareReservation()
    {
        lock (lockLr2SongDbSync)
        {
            lr2SongDbSyncPrepareInProgress = false;
        }
    }

    private void UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress)
    {
        if (progress == null)
        {
            return;
        }

        Lr2SongDbSyncTotalCount = Math.Max(0, progress.TotalCount);
        Lr2SongDbSyncProcessedCount = Math.Max(0, progress.ProcessedCursor);
        Lr2SongDbSyncStage = progress.Stage ?? string.Empty;
        Lr2SongDbSyncStageProcessedCount = Math.Max(0, progress.StageProcessedCount);
        Lr2SongDbSyncStageTotalCount = Math.Max(0, progress.StageTotalCount);
        PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            GetLr2SongDbSyncStatusSnapshot().Signature,
            Lr2SongDbSyncStage,
            Lr2SongDbSyncProcessedCount,
            Lr2SongDbSyncTotalCount,
            lastError: null,
            Lr2SongDbSyncStageProcessedCount,
            Lr2SongDbSyncStageTotalCount));
    }

    private void PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId)
    {
        UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
        {
            Stage = stage ?? string.Empty,
            ProcessedCursor = 0,
            TotalCount = 0,
            StageProcessedCount = 0,
            StageTotalCount = 0
        });
        LogInstallPerformance("lr2_song_db_sync preflight_stage_start"
            + " stage=" + (stage ?? string.Empty)
            + " reason=" + (reason ?? "unknown")
            + " runId=" + (runId ?? string.Empty));
    }

    private void LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs)
    {
        LogInstallPerformance("lr2_song_db_sync preflight_stage_done"
            + " stage=" + (stage ?? string.Empty)
            + " reason=" + (reason ?? "unknown")
            + " runId=" + (runId ?? string.Empty)
            + " elapsedMs=" + elapsedMs);
    }

    private void CompleteLr2SongDbSyncRequest(int requestVersion, string stage)
    {
        lock (lockLr2SongDbSync)
        {
            lr2SongDbSyncCompletedVersion = Math.Max(lr2SongDbSyncCompletedVersion, requestVersion);
        }

        Lr2SongDbSyncCompletedVersion = lr2SongDbSyncCompletedVersion;
        Lr2SongDbSyncStage = stage ?? string.Empty;
        Lr2SongDbSyncStageProcessedCount = Lr2SongDbSyncStageTotalCount > 0
            ? Lr2SongDbSyncStageTotalCount
            : Lr2SongDbSyncProcessedCount;
        Lr2SongDbSyncStageTotalCount = Lr2SongDbSyncStageTotalCount > 0
            ? Lr2SongDbSyncStageTotalCount
            : Lr2SongDbSyncTotalCount;
        Lr2SongDbSyncRunning = false;
        DisposeLr2SongDbSyncCancellation();
        PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Completed,
            GetLr2SongDbSyncStatusSnapshot().Signature,
            Lr2SongDbSyncStage,
            Lr2SongDbSyncProcessedCount,
            Lr2SongDbSyncTotalCount,
            lastError: null,
            Lr2SongDbSyncStageProcessedCount,
            Lr2SongDbSyncStageTotalCount));
    }

    private void FailLr2SongDbSyncRequest(int requestVersion, Lr2SongDbSyncStatusKind status, string stage, string message)
    {
        lock (lockLr2SongDbSync)
        {
            lr2SongDbSyncFailedVersion = Math.Max(lr2SongDbSyncFailedVersion, requestVersion);
        }

        Lr2SongDbSyncFailedVersion = lr2SongDbSyncFailedVersion;
        Lr2SongDbSyncFailureMessage = message ?? string.Empty;
        Lr2SongDbSyncStage = stage ?? string.Empty;
        Lr2SongDbSyncRunning = false;
        DisposeLr2SongDbSyncCancellation();
        PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            status,
            GetLr2SongDbSyncStatusSnapshot().Signature,
            Lr2SongDbSyncStage,
            Lr2SongDbSyncProcessedCount,
            Lr2SongDbSyncTotalCount,
            Lr2SongDbSyncFailureMessage,
            Lr2SongDbSyncStageProcessedCount,
            Lr2SongDbSyncStageTotalCount));
    }

    internal bool CancelLr2SongDbSync(string reason)
    {
        lock (lockLr2SongDbSync)
        {
            if (!_Lr2SongDbSyncRunning || lr2SongDbSyncCancellation == null)
            {
                return false;
            }
            LogInstallPerformance("lr2_song_db_sync cancel_requested"
                + " reason=" + (reason ?? "unknown")
                + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                + " processed=" + Lr2SongDbSyncProcessedCount
                + " total=" + Lr2SongDbSyncTotalCount);
            lr2SongDbSyncCancellation.Cancel();
            return true;
        }
    }

    private void DisposeLr2SongDbSyncCancellation()
    {
        lock (lockLr2SongDbSync)
        {
            lr2SongDbSyncCancellation?.Dispose();
            lr2SongDbSyncCancellation = null;
        }
    }

    private bool IsLr2SongDbSyncMutationBlocked()
    {
        return Lr2SongDbSyncRunning;
    }

    private bool TryBlockLr2SongDbSyncMutation(string operation, bool showMessage = true)
    {
        if (!IsLr2SongDbSyncMutationBlocked())
        {
            return false;
        }
        LogInstallPerformance("lr2_song_db_sync_mutation_blocked operation=" + (operation ?? "(unknown)")
            + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
            + " processed=" + Lr2SongDbSyncProcessedCount
            + " total=" + Lr2SongDbSyncTotalCount);
        if (showMessage)
        {
            ShowOperationDialog(
                Resources.Warn_Lr2SongDbSyncRunning,
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }
        return true;
    }

    private IDisposable TryBeginLr2SongDbSyncBlockedMutation(string operation, bool showMessage = true)
    {
        lock (lockLr2SongDbSync)
        {
            if (!_Lr2SongDbSyncRunning && !lr2SongDbSyncPrepareInProgress)
            {
                lr2SongDbSyncMutationInProgress++;
                return new Lr2SongDbSyncBlockedMutationScope(this);
            }
            LogInstallPerformance("lr2_song_db_sync_mutation_blocked operation=" + (operation ?? "(unknown)")
                + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                + " processed=" + Lr2SongDbSyncProcessedCount
                + " total=" + Lr2SongDbSyncTotalCount
                + " preparing=" + lr2SongDbSyncPrepareInProgress.ToString().ToLowerInvariant());
        }
        if (showMessage)
        {
            ShowOperationDialog(
                Resources.Warn_Lr2SongDbSyncRunning,
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }
        return null;
    }

    private void EndLr2SongDbSyncBlockedMutation()
    {
        lock (lockLr2SongDbSync)
        {
            lr2SongDbSyncMutationInProgress = Math.Max(0, lr2SongDbSyncMutationInProgress - 1);
        }
    }

    private sealed class Lr2SongDbSyncBlockedMutationScope(BMSLibrary owner) : IDisposable
    {
        private BMSLibrary owner = owner;

        public void Dispose()
        {
            BMSLibrary currentOwner = Interlocked.Exchange(ref owner, null);
            currentOwner?.EndLr2SongDbSyncBlockedMutation();
        }
    }

    private void ThrowIfLr2SongDbSyncMutationBlocked(string operation)
    {
        if (TryBlockLr2SongDbSyncMutation(operation, showMessage: false))
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }
    }

    internal void ThrowIfLr2SongDbSyncMutationBlockedForPlaylist(string operation)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(operation);
    }

    private void RunLr2SongDbSync(string reason, string signature, int requestVersion)
    {
        Lr2SongDbSyncRequestCoordinator.Run(this, reason, signature, requestVersion);
    }

    private void MarkLr2SongDbSyncPreflightCancelled(string signature, string runId, string stage)
    {
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            Lr2SongDbSyncStatusService.MarkCancelled(
                songDb,
                signature,
                runId,
                processedCursor: 0,
                totalCount: 0,
                stage,
                DateTime.UtcNow);
        }
        catch
        {
            // Runtime cancellation state is still reported by the caller's catch block.
        }
    }

    private int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, BMSFile> bmsByPath = null,
        bool logSummary = true,
        bool dispatchPresentation = true)
    {
        List<BMSFileMaintenanceInfo> infoList = [.. (maintenanceInfos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.path))];
        if (infoList.Count == 0)
        {
            if (logSummary)
            {
                LogInstallPerformance("lr2_song_db_sync_compatibility_projection skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " input=0 applied=0");
            }
            return 0;
        }

        int applied = 0;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            bmsByPath ??= (_BMSFiles ?? [])
                .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
                .GroupBy(file => file.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (BMSFileMaintenanceInfo sourceInfo in infoList)
            {
                if (!bmsByPath.TryGetValue(sourceInfo.path, out BMSFile file)
                    || file == null
                    || (!string.IsNullOrWhiteSpace(sourceInfo.hash)
                        && !string.Equals(sourceInfo.hash, file.hash, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                BMSFileMaintenanceInfo targetInfo = file.TryGetMaintenanceInfoWithoutCreating();
                if (targetInfo == null || !file.HasMaintenanceInfoHash(file.hash))
                {
                    targetInfo = new BMSFileMaintenanceInfo(file);
                }
                if (targetInfo.HasSameLr2CompatibilityFacts(sourceInfo))
                {
                    continue;
                }
                targetInfo.ApplyLr2CompatibilityFactsFrom(sourceInfo);
                file.SetMaintenanceInfo(targetInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.Calculated);
                applied++;
            }
        }

        if (logSummary)
        {
            LogInstallPerformance("lr2_song_db_sync_compatibility_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + infoList.Count
                + " applied=" + applied);
        }
        if (dispatchPresentation && applied > 0)
        {
            DispatchWarningPresentationChanged("lr2_song_db_sync_compatibility_projection");
        }
        return applied;
    }

    private Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return (_BMSFiles ?? [])
                .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
                .GroupBy(file => file.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason)
    {
        catalogChartInfoOwner.EnsureHydratedForLr2(reason);
    }

    private void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        catalogChartInfoOwner.CaptureCompletedLr2TrustFromFileDiff(
            ChartInfoLr2TrustInput.Create(options, fileCheckResult),
            reason);
    }

    private HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures =
                catalogChartInfoOwner.LoadCurrentParseFailureMap(dbGateway, chartInfoBuildService.CurrentParseTimeout);
            stopwatch.Stop();
            var result = new HashSet<string>(
                (failures?.Keys ?? Enumerable.Empty<string>()).Where(md5 => !string.IsNullOrWhiteSpace(md5)),
                StringComparer.OrdinalIgnoreCase);
            LogInstallPerformance("lr2_song_db_sync_chart_info_parse_failure_snapshot"
                + " reason=" + (reason ?? "unknown")
                + " count=" + result.Count
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogInstallPerformance("lr2_song_db_sync_chart_info_parse_failure_snapshot_failed"
                + " reason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " message=" + ex.Message);
            return [];
        }
    }

    private Lr2SongDbSyncInput CreateLr2SongDbSyncInput()
    {
        var inputStopwatch = Stopwatch.StartNew();
        var rowSnapshotStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncInputRowSnapshot rowSnapshot = CreateLr2SongDbSyncInputRowSnapshot();
        rowSnapshotStopwatch.Stop();

        var rootsStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncInputRootSnapshot rootSnapshot = CreateLr2SongDbSyncInputRootSnapshot();
        rootsStopwatch.Stop();

        var builtinSettingsStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot = CreateLr2SongDbSyncInputSettingsSnapshot(
            rowSnapshot.SongRows,
            rootSnapshot.CapturedAtUtc,
            rootSnapshot.RootDirectories);
        builtinSettingsStopwatch.Stop();

        var scanSurfaceStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection =
            CreateLr2SongDbSyncScanSurfaceSelection(rootSnapshot, rowSnapshot);
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = scanSurfaceSelection.Surface;
        scanSurfaceStopwatch.Stop();

        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection =
            CreateLr2SongDbSyncPreparedSurfaceSelection(scanSurface);
        var inputBuilder = new Lr2SongDbSyncInputBuilder(
            LogEverythingScan,
            LogInstallPerformance);

        var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();

        return inputBuilder.Create(
            rowSnapshot,
            rootSnapshot,
            settingsSnapshot,
            scanSurfaceSelection,
            preparedSurfaceSelection,
            appManagedOutputScope,
            inputStopwatch,
            rowSnapshotStopwatch,
            rootsStopwatch,
            builtinSettingsStopwatch,
            scanSurfaceStopwatch,
            lr2FolderCandidatesStopwatch);
    }

    private Lr2SongDbSyncScanSurfaceSelection CreateLr2SongDbSyncScanSurfaceSelection(
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputRowSnapshot rowSnapshot)
    {
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = GetCurrentLr2SongDbSyncScanSurface(
            rootSnapshot.RootDirectories,
            rootSnapshot.Lr2FolderDiscoveryDirectories,
            rowSnapshot,
            out string scanSurfaceMissReason);

        return new Lr2SongDbSyncScanSurfaceSelection(scanSurface, scanSurfaceMissReason);
    }

    private Lr2SongDbSyncInputRowSnapshot CreateLr2SongDbSyncInputRowSnapshot()
    {
        List<string> chartPaths;
        List<BMSFile> songRows;
        int ownedCollectionVersion;
        StorageRowsVersionSnapshot storageRowsVersion;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            var chartPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            chartPaths = [];
            songRows = [];
            foreach (BMSFile file in _BMSFiles ?? [])
            {
                if (file == null || string.IsNullOrWhiteSpace(file.path))
                {
                    continue;
                }
                songRows.Add(file);
                if (chartPathSet.Add(file.path))
                {
                    chartPaths.Add(file.path);
                }
            }
            ownedCollectionVersion = OwnedChartCollectionVersion;
            storageRowsVersion = CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
        return new Lr2SongDbSyncInputRowSnapshot(
            chartPaths,
            songRows,
            ownedCollectionVersion,
            storageRowsVersion.BmsRowsVersion,
            storageRowsVersion.BmsonRowsVersion);
    }

    private Lr2SongDbSyncInputRootSnapshot CreateLr2SongDbSyncInputRootSnapshot()
    {
        DateTime capturedAtUtc = DateTime.UtcNow;
        List<string> rootDirectories = getBMSDirectories();
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        return new Lr2SongDbSyncInputRootSnapshot(
            capturedAtUtc,
            rootDirectories,
            CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(rootDirectories, options),
            options.LR2RootPath);
    }

    private Lr2SongDbSyncInputSettingsSnapshot CreateLr2SongDbSyncInputSettingsSnapshot(
        IEnumerable<BMSFile> songRows,
        DateTime nowUtc,
        IEnumerable<string> rootDirectories)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<string> lr2BuiltinFolderSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
        return new Lr2SongDbSyncInputSettingsSnapshot(
            CreateLr2BuiltinCustomFolderSettings(songRows, nowUtc),
            lr2BuiltinFolderSourceDirectories,
            options.LR2CustomFolderOutputBaseDir,
            options.LR2CustomFolderAdditionalOutputBaseDirs,
            options.LR2CustomFolderOutputBaseDirRootType,
            CreateLr2SongDbSyncLr2FolderPruneDirectories(
                rootDirectories,
                lr2BuiltinFolderSourceDirectories,
                options: options));
    }

    private Lr2SongDbSyncPreparedSurfaceSelection CreateLr2SongDbSyncPreparedSurfaceSelection(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface)
    {
        Lr2SongDbSyncPreparedDataSurface pendingPreparedSurface =
            TakeLr2SongDbSyncPreparedDataSurface(out int appliedScanGeneration);
        bool alreadyAppliedToScanSurface = scanSurface != null
            && pendingPreparedSurface?.HasPreparedDataSurface == true
            && appliedScanGeneration == scanSurface.Generation;
        return new Lr2SongDbSyncPreparedSurfaceSelection(
            pendingPreparedSurface,
            alreadyAppliedToScanSurface ? Lr2SongDbSyncPreparedDataSurface.Empty : pendingPreparedSurface,
            appliedScanGeneration,
            alreadyAppliedToScanSurface);
    }

    private bool IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input)
    {
        if (input == null
            || OwnedChartCollectionVersion != input.OwnedChartCollectionVersion
            || catalogStorageRowsOwner.BmsRowsVersion != input.BmsRowsVersion
            || catalogStorageRowsOwner.BmsonRowsVersion != input.BmsonRowsVersion)
        {
            return false;
        }

        List<string> roots = getBMSDirectories();
        if (!ArePathSetsEqual(input.RootDirectories, roots))
        {
            return false;
        }
        if (!IsCurrentLr2SongDbSyncScanSurface(input))
        {
            return false;
        }

        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<string> lr2FolderDiscoveryDirectories = CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options);
        if (!ArePathSetsEqual(input.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
        {
            return false;
        }
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
        if (!appManagedOutputScope.IsComplete
            || !ArePathSetsEqual(input.Lr2FolderPruneExcludedDirectories, appManagedOutputScope.Directories))
        {
            return false;
        }
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings = Lr2BuiltinCustomFolderSettings.Create(CreateCurrentLr2ConfigOrNull(), [], DateTime.UtcNow);
        if (!AreLr2BuiltinCustomFolderConfigurationEqual(input.Lr2BuiltinCustomFolderSettings, builtinCustomFolderSettings))
        {
            return false;
        }
        if (!string.Equals(SafeFullPathOrOriginal(input.Lr2RootPath), SafeFullPathOrOriginal(options.LR2RootPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SafeFullPathOrOriginal(input.Lr2NormalCustomFolderOutputBaseDir), SafeFullPathOrOriginal(options.LR2CustomFolderOutputBaseDir), StringComparison.OrdinalIgnoreCase)
            || !ArePathSetsEqual(input.Lr2AdditionalNormalCustomFolderOutputBaseDirs, options.LR2CustomFolderAdditionalOutputBaseDirs)
            || !string.Equals(SafeFullPathOrOriginal(input.Lr2RootCustomFolderOutputBaseDir), SafeFullPathOrOriginal(options.LR2CustomFolderOutputBaseDirRootType), StringComparison.OrdinalIgnoreCase)
            || !ArePathSetsEqual(input.Lr2BuiltinFolderSourceDirectories, CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options)))
        {
            return false;
        }

        return true;
    }

    private Lr2SongDbSyncSongRowsSkipVerificationResult VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songRows,
        Lr2SongDbSyncInput input,
        string reason)
    {
        int targetRows = songRows?.Count ?? 0;
        if (!IsAutomaticLr2SongDbSyncFileDiffFollowupReason(reason))
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "not_automatic_file_diff_followup", targetRows);
        }
        if (songDb == null)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "song_db_unavailable", targetRows);
        }
        if (!IsLr2SongDbSyncInputCurrent(input))
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "input_not_current", targetRows);
        }

        Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot;
        lock (lockLr2SongDbSyncFileDiffFreshness)
        {
            snapshot = lr2SongDbSyncFileDiffFreshnessSnapshot;
        }
        if (snapshot == null)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "missing_file_diff_freshness_snapshot", targetRows);
        }
        if (input == null
            || snapshot.ScanSurfaceGeneration != input.ScanSurfaceGeneration
            || snapshot.OwnedCollectionVersion != input.OwnedChartCollectionVersion
            || snapshot.BmsRowsVersion != input.BmsRowsVersion
            || snapshot.BmsonRowsVersion != input.BmsonRowsVersion)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "file_diff_freshness_not_current", targetRows);
        }
        if (snapshot.BmsOwnerCount != targetRows)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "song_row_count_mismatch", targetRows);
        }
        if (snapshot.BmsTargetCount < snapshot.BmsOwnerCount)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "insufficient_bms_target_coverage", targetRows);
        }
        if (snapshot.InlineMaintenanceBmsCount < snapshot.BmsOwnerCount || snapshot.InlineMaintenanceFailedCount > 0)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "insufficient_maintenance_coverage", targetRows);
        }
        if (snapshot.BmsMovedHashRelinkAmbiguousCount > 0)
        {
            return CreateLr2SongDbSyncSongRowsSkipResult(false, "ambiguous_hash_relink", targetRows);
        }
        if (AreAllLr2SongDbSyncSongRowsFreshNewInserts(snapshot, songRows))
        {
            return new Lr2SongDbSyncSongRowsSkipVerificationResult
            {
                CanSkip = true,
                Reason = "file_diff_new_insert_projection_current",
                TargetRows = targetRows,
                VerifiedRows = targetRows,
                MissingRows = 0,
                MismatchedRows = 0,
                DuplicatePathRows = 0,
                DigestCheckedRows = 0,
                DigestMissingRows = 0,
                DigestMismatchedRows = 0,
                ProjectionMs = 0,
                ExistingReadMs = 0,
                DigestReadMs = 0,
                ElapsedMs = 0,
                DiagnosticSamples = []
            };
        }

        return CreateLr2SongDbSyncSongRowsSkipResult(
            false,
            "file_diff_transient_coverage_incomplete",
            targetRows);
    }

    private static bool AreAllLr2SongDbSyncSongRowsFreshNewInserts(
        Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot,
        IReadOnlyList<BMSFile> songRows)
    {
        int targetRows = songRows?.Count ?? 0;
        if (targetRows <= 0
            || snapshot?.TransientSongRowSkipPaths == null
            || snapshot.TransientSongRowSkipPaths.Count < targetRows)
        {
            return false;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile row in songRows)
        {
            if (row == null
                || string.IsNullOrWhiteSpace(row.path)
                || !snapshot.TransientSongRowSkipPaths.Contains(row.path)
                || !seenPaths.Add(row.path))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAutomaticLr2SongDbSyncFileDiffFollowupReason(string reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && reason.StartsWith("post_startup_", StringComparison.OrdinalIgnoreCase);
    }

    private ISet<string> GetLr2SongDbSyncTransientSongRowsSkipPaths(
        Lr2SongDbSyncInput input,
        string reason)
    {
        if (!IsAutomaticLr2SongDbSyncFileDiffFollowupReason(reason)
            || !IsLr2SongDbSyncInputCurrent(input))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot;
        lock (lockLr2SongDbSyncFileDiffFreshness)
        {
            snapshot = lr2SongDbSyncFileDiffFreshnessSnapshot;
        }
        if (snapshot == null
            || input == null
            || snapshot.ScanSurfaceGeneration != input.ScanSurfaceGeneration
            || snapshot.OwnedCollectionVersion != input.OwnedChartCollectionVersion
            || snapshot.BmsRowsVersion != input.BmsRowsVersion
            || snapshot.BmsonRowsVersion != input.BmsonRowsVersion
            || snapshot.InlineMaintenanceFailedCount > 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(snapshot.TransientSongRowSkipPaths, StringComparer.OrdinalIgnoreCase);
    }

    private static Lr2SongDbSyncSongRowsSkipVerificationResult CreateLr2SongDbSyncSongRowsSkipResult(
        bool canSkip,
        string reason,
        int targetRows)
    {
        return new Lr2SongDbSyncSongRowsSkipVerificationResult
        {
            CanSkip = canSkip,
            Reason = reason ?? "unknown",
            TargetRows = Math.Max(0, targetRows)
        };
    }

    private static bool ArePathSetsEqual(IEnumerable<string> first, IEnumerable<string> second)
    {
        return new HashSet<string>(
            (first ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal),
            StringComparer.OrdinalIgnoreCase)
            .SetEquals((second ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal));
    }

    private static List<string> NormalizeDistinctDirectories(IEnumerable<string> directories)
    {
        return [.. (directories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeFullPathOrOriginal)
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
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

    private static Lr2TextMetadataCandidateSnapshot CreateLr2PreparedTextMetadataCandidates(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        if (!(rootDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path))
            || !(targetDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path)))
        {
            return new Lr2TextMetadataCandidateSnapshot(
                new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true),
                []);
        }

        return CreateLr2SongDbSyncTextMetadataCandidates(rootDirectories, targetDirectories);
    }

    private static IReadOnlyList<string> CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
        IEnumerable<string> sourceDirectories,
        IEnumerable<string> coveredRoots)
    {
        List<string> roots = [.. NormalizeLr2DirectoryMetadataTargets(coveredRoots)];
        return [.. NormalizeLr2DirectoryMetadataTargets(sourceDirectories)
            .Where(source => !roots.Any(root => Lr2FolderPath.IsSameOrDescendant(source, root)
                || Lr2FolderPath.IsSameOrDescendant(root, source)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static void ApplyLr2TextMetadataCandidatesToRequest(
        Lr2SongDbSyncRequest request,
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot,
        IEnumerable<string> metadataScopeDirectories)
    {
        if (request == null || textMetadataSnapshot == null)
        {
            return;
        }

        IReadOnlyList<string> folderInfoPaths = MergePreparedFileSurface(
            request.FolderInfoFilePaths,
            request.FolderInfoFileEntries,
            textMetadataSnapshot.FolderInfoCandidates.Paths,
            textMetadataSnapshot.FolderInfoCandidates.EntriesByPath,
            metadataScopeDirectories,
            out IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoEntries);
        request.FolderInfoFilePaths = folderInfoPaths;
        request.FolderInfoFileEntries = folderInfoEntries;
        request.TextFileDirectories = MergePreparedDirectoryList(
            request.TextFileDirectories,
            textMetadataSnapshot.TextFileDirectories,
            metadataScopeDirectories);
    }

    private static Lr2FolderInfoCandidateSnapshot CreateLr2OwnedMutationFolderInfoCandidates(
        IEnumerable<string> targetDirectories)
    {
        IReadOnlyList<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
        var entries = new List<RootFileEnumerationEntry>();
        foreach (string targetDirectory in targets)
        {
            RootFileEnumerationEntry entry = CreateLr2OwnedMutationFolderInfoEntry(targetDirectory);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        return Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(entries, targets);
    }

    private static Lr2OwnedMutationDirectoryMetadataSurface CreateLr2OwnedMutationDirectoryMetadataSurface(
        IEnumerable<string> targetDirectories)
    {
        return new Lr2OwnedMutationDirectoryMetadataSurface(
            CreateLr2OwnedMutationFolderInfoCandidates(targetDirectories),
            CreateLr2OwnedMutationDirectoryEntries(targetDirectories));
    }

    private static RootFileEnumerationEntry CreateLr2OwnedMutationFolderInfoEntry(string directoryPath)
    {
        try
        {
            string folderInfoPath = Path.Combine(directoryPath, "folderinfo.txt");
            if (!LongPathFileSystem.FileExists(folderInfoPath))
            {
                return null;
            }
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(folderInfoPath);
            LongPathFileSystem.FileMetadata metadata = LongPathFileSystem.GetFileMetadata(normalizedPath);
            return LongPathFileSystem.FileExists(normalizedPath)
                ? new RootFileEnumerationEntry(normalizedPath, metadata.LastWriteTimeUtc, metadata.Length)
                : null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
        {
            return null;
        }
    }

    private Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
    {
        LR2Config config = CreateCurrentLr2ConfigOrNull();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            return Lr2BuiltinCustomFolderSettings.CreateFromAddDates(
                config,
                (_BMSFiles ?? [])
                    .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
                    .Select(file => file.adddate),
                nowUtc);
        }
    }

    private Lr2BuiltinCustomFolderSettings CreateLr2BuiltinCustomFolderSettings(IEnumerable<BMSFile> songRows, DateTime nowUtc)
    {
        return Lr2BuiltinCustomFolderSettings.Create(CreateCurrentLr2ConfigOrNull(), songRows, nowUtc);
    }

    private LR2Config CreateCurrentLr2ConfigOrNull()
    {
        LR2Config config = null;
        try
        {
            config = lr2config?.Invoke();
        }
        catch
        {
            config = null;
        }
        return config;
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2OwnedMutationDirectoryEntries(
        IEnumerable<string> targetDirectories)
    {
        IReadOnlyList<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string targetDirectory in targets)
        {
            RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory);
            string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
            if (!string.IsNullOrWhiteSpace(key) && targetSet.Contains(key))
            {
                entries[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }

        return entries;
    }

    private sealed class Lr2OwnedMutationDirectoryMetadataSurface(
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries)
    {
        public Lr2FolderInfoCandidateSnapshot FolderInfoCandidates { get; } =
            folderInfoCandidates ?? new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
            directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static Func<string, DateTime?> CreateLastWriteTimeResolver(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        if (entriesByPath == null || entriesByPath.Count == 0)
        {
            return null;
        }

        return path =>
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(key)
                && entriesByPath.TryGetValue(key, out RootFileEnumerationEntry entry)
                    ? entry.LastWriteTimeUtc
                    : null;
        };
    }

    private void SyncLr2NormalFoldersForOwnedMutation(OwnedChartCollectionStorageMutation mutation, string reason)
    {
        if (mutation == null)
        {
            return;
        }

        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (options?.OperationModeLR2DB != true)
        {
            return;
        }
        if (!HasLr2NormalFolderRelevantStorageMutation(mutation))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = [];
        Lr2NormalFolderSyncScope syncInput = Lr2NormalFolderSyncScope.Empty;
        try
        {
            roots = getBMSDirectories();
            if (roots.Count == 0)
            {
                return;
            }

            syncInput = Lr2NormalFolderSyncScopeBuilder.CreateForStorageMutation(
                roots,
                mutation.AddedBmsFiles,
                mutation.PathChanges,
                mutation.RemoveRequests,
                CreateLr2NormalFolderCurrentBmsLookupUnsafe());
            if (syncInput.ChartPaths.Count == 0
                && syncInput.PruneScopeDirectories.Count == 0
                && syncInput.PruneExactDirectories.Count == 0)
            {
                return;
            }

            IReadOnlyCollection<string> directoryMetadataTargets = Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, syncInput.ChartPaths);
            Lr2OwnedMutationDirectoryMetadataSurface metadataSurface = CreateLr2OwnedMutationDirectoryMetadataSurface(directoryMetadataTargets);
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            Lr2NormalFolderDbSyncResult syncResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = syncInput.ChartPaths,
                DirectoryPaths = directoryMetadataTargets,
                FolderInfoFilePaths = metadataSurface.FolderInfoCandidates.Paths,
                FolderInfoFileEntries = metadataSurface.FolderInfoCandidates.EntriesByPath,
                DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(metadataSurface.DirectoryEntries),
                PruneScopeDirectories = syncInput.PruneScopeDirectories,
                PruneExactDirectories = syncInput.PruneExactDirectories,
                GeneratedAtUtc = DateTime.UtcNow,
                AllowPrune = syncInput.PruneScopeDirectories.Count > 0 || syncInput.PruneExactDirectories.Count > 0,
                UseScopedExistingRows = true
            });
            stopwatch.Stop();
            LogInstallPerformance("lr2_normal_folder_mutation_sync done"
                + " reason=" + (reason ?? "unknown")
                + " paths=" + syncInput.ChartPaths.Count
                + " directoryPaths=" + directoryMetadataTargets.Count
                + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                + " roots=" + roots.Count
                + " generated=" + syncResult.GeneratedCount
                + " upserted=" + syncResult.UpsertedCount
                + " deleted=" + syncResult.DeletedCount
                + " skippedUnsupported=" + syncResult.SkippedUnsupportedPathCount
                + " skippedMissingMetadata=" + syncResult.SkippedMissingMetadataCount
                + " skippedIncompatibleChart=" + syncResult.SkippedIncompatibleChartPathCount
                + " folderInfoCandidates=" + syncResult.FolderInfoCandidateCount
                + " folderInfoApplied=" + syncResult.FolderInfoAppliedCount
                + " targetBuildMs=" + syncResult.TargetBuildMs
                + " metadataBuildMs=" + syncResult.MetadataBuildMs
                + " existingReadMs=" + syncResult.ExistingReadMs
                + " rowGenerateMs=" + syncResult.RowGenerateMs
                + " planMs=" + syncResult.PlanMs
                + " writeMs=" + syncResult.WriteMs
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                options,
                stage: "lr2_normal_folder_mutation_sync_failed",
                detail: "lr2_normal_folder_mutation_sync_failed: " + (ex.Message ?? ex.GetType().Name ?? "unknown"),
                logReason: "lr2_normal_folder_mutation_sync_failed");
            LogInstallPerformanceWarn("lr2_normal_folder_mutation_sync failed"
                + " reason=" + (reason ?? "unknown")
                + " paths=" + syncInput.ChartPaths.Count
                + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                + " roots=" + roots.Count
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
        }
    }

    private static bool HasLr2NormalFolderRelevantStorageMutation(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation != null
            && (mutation.AddedBmsFiles.Count > 0
                || mutation.PathChanges.Any(pathChange => pathChange?.GetBmsStorageOwner() != null)
                || mutation.RemoveRequests.Any(removeRequest => removeRequest?.Kind == ChartFileKind.Bms));
    }

    private Lr2NormalFolderCurrentBmsLookup CreateLr2NormalFolderCurrentBmsLookupUnsafe()
    {
        LibraryChartRefIndexSnapshot snapshot = null;

        LibraryChartRefIndexSnapshot GetSnapshot()
        {
            if (snapshot != null)
            {
                return snapshot;
            }

            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                snapshot = catalogOwnedCollectionOwner.IsInitialized
                    ? catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefIndexSnapshot()
                    : LibraryChartRefIndexSnapshot.Empty;
            }
            return snapshot;
        }

        return new Lr2NormalFolderCurrentBmsLookup(
            directoryPath => GetSnapshot().CountBmsChartRefsUnderRealPath(directoryPath) > 0,
            directoryPath => GetSnapshot().GetBmsChartPathsUnderRealPath(directoryPath));
    }

    private void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result)
    {
        if (result?.Lr2NormalFolderSyncFailed != true)
        {
            return;
        }

        string failureReason = string.IsNullOrWhiteSpace(result.Lr2NormalFolderSyncFailureReason)
            ? "unknown"
            : result.Lr2NormalFolderSyncFailureReason;
        MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
            options,
            stage: "lr2_normal_folder_file_diff_sync_failed",
            detail: "lr2_normal_folder_file_diff_sync_failed: " + failureReason,
            logReason: "lr2_normal_folder_file_diff_sync_failed");
    }

    private void MarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(
        BmsLibraryOptionsSnapshot options,
        Exception ex,
        string reason)
    {
        string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
        MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            options,
            stage: "lr2_song_db_file_diff_write_failed",
            detail: "lr2_song_db_file_diff_write_failed: " + displayedMessage,
            logReason: string.IsNullOrWhiteSpace(reason) ? "file_diff" : reason);
    }

    private void MarkLr2SongDbSyncIncompleteAfterMaintenanceSongDbWriteFailure(Exception ex, string reason)
    {
        string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
        bool encodingUpsert = string.Equals(reason, "lr2_song_db_encoding_upsert_failed", StringComparison.Ordinal);
        MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            CurrentOptionsSnapshot,
            stage: encodingUpsert ? "lr2_song_db_encoding_upsert_failed" : "lr2_song_db_maintenance_write_failed",
            detail: (encodingUpsert ? "lr2_song_db_encoding_upsert_failed: " : "lr2_song_db_maintenance_write_failed: ") + displayedMessage,
            logReason: string.IsNullOrWhiteSpace(reason) ? "maintenance_update" : reason);
    }

    internal void MarkLr2SongDbSyncIncompleteAfterPlaylistLr2FolderSyncFailure(Exception ex, string reason)
    {
        string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
        MarkLr2SongDbSyncIncomplete(
            CurrentOptionsSnapshot,
            runId: "playlist_lr2folder_sync",
            stage: "lr2_playlist_lr2folder_sync_failed",
            detail: "lr2_playlist_lr2folder_sync_failed: " + displayedMessage,
            logReason: string.IsNullOrWhiteSpace(reason) ? "playlist_lr2folder_sync" : reason);
    }

    private void MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        MarkLr2SongDbSyncIncomplete(
            options,
            runId: "normal_folder_sync",
            stage: stage,
            detail: detail,
            logReason: logReason);
    }

    private void MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        MarkLr2SongDbSyncIncomplete(
            options,
            runId: "song_db_write",
            stage: stage,
            detail: detail,
            logReason: logReason);
    }

    private void MarkLr2SongDbSyncIncomplete(
        BmsLibraryOptionsSnapshot options,
        string runId,
        string stage,
        string detail,
        string logReason)
    {
        if (options?.OperationModeLR2DB != true)
        {
            return;
        }

        try
        {
            string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            Lr2SongDbSyncStatusSnapshot status = Lr2SongDbSyncStatusService.MarkIncomplete(
                songDb,
                signature,
                runId: string.IsNullOrWhiteSpace(runId) ? "runtime_write" : runId,
                processedCursor: null,
                totalCount: null,
                stage: stage,
                detail: detail,
                nowUtc: DateTime.UtcNow);
            PublishLr2SongDbSyncStatus(status);
        }
        catch (Exception ex)
        {
            LogInstallPerformanceWarn("lr2_song_db_sync_status mark_incomplete_failed"
                + " reason=" + (logReason ?? "unknown")
                + " exception=" + ex.GetType().Name
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
        }
    }

    private void ExecuteLr2SongDbWrite(Action writeAction, string stage, string logReason)
    {
        if (writeAction == null)
        {
            return;
        }

        try
        {
            writeAction();
        }
        catch (Exception ex)
        {
            string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
            MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
                CurrentOptionsSnapshot,
                stage: string.IsNullOrWhiteSpace(stage) ? "lr2_song_db_write_failed" : stage,
                detail: (string.IsNullOrWhiteSpace(stage) ? "lr2_song_db_write_failed" : stage) + ": " + displayedMessage,
                logReason: string.IsNullOrWhiteSpace(logReason) ? "lr2_song_db_write_failed" : logReason);
            LogInstallPerformanceWarn("lr2_song_db_write failed"
                + " reason=" + (string.IsNullOrWhiteSpace(logReason) ? "unknown" : logReason)
                + " stage=" + (string.IsNullOrWhiteSpace(stage) ? "lr2_song_db_write_failed" : stage)
                + " exception=" + ex.GetType().Name
                + " message=" + displayedMessage);
            throw;
        }
    }

    private void MarkLr2SongDbSyncIncompleteAfterStateApplierSongDbWriteFailure(string stage, Exception ex)
    {
        string resolvedStage = string.IsNullOrWhiteSpace(stage) ? "lr2_song_db_library_mutation_write_failed" : stage;
        string displayedMessage = GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | ");
        MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            CurrentOptionsSnapshot,
            stage: resolvedStage,
            detail: resolvedStage + ": " + displayedMessage,
            logReason: resolvedStage);
        LogInstallPerformanceWarn("lr2_song_db_write failed"
            + " reason=" + resolvedStage
            + " stage=" + resolvedStage
            + " exception=" + (ex?.GetType().Name ?? "unknown")
            + " message=" + displayedMessage);
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

    private static Lr2FolderFileCandidateSnapshot CreateLr2SongDbSyncLr2FolderFileCandidates(
        IEnumerable<string> rootDirectories,
        string lr2RootPath,
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings,
        IEnumerable<string> excludedDirectories = null)
    {
        return Lr2FolderFileDiscoveryService.CreateFileCandidates(
            rootDirectories,
            lr2RootPath,
            builtinCustomFolderSettings,
            LogEverythingScan,
            excludedDirectories);
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

    private List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> builtinSourceDirectories,
        bool includeAppManagedOutputDirectories = true,
        BmsLibraryOptionsSnapshot options = null)
    {
        options ??= CurrentOptionsSnapshot;
        return Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            rootDirectories,
            CreateNormalCustomFolderOutputBaseDirectories(options),
            options.LR2CustomFolderOutputBaseDirRootType,
            builtinSourceDirectories,
            includeAppManagedOutputDirectories);
    }

    private IReadOnlyList<string> CreateNormalCustomFolderOutputBaseDirectories(BmsLibraryOptionsSnapshot options = null)
    {
        options ??= CurrentOptionsSnapshot;
        return [.. new[] { options.LR2CustomFolderOutputBaseDir }
            .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private IReadOnlyList<string> CreateLr2SongDbSyncAppManagedOutputDirectories()
    {
        return CreateLr2SongDbSyncAppManagedOutputScope().Directories;
    }

    private Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isComplete = true;
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDbReadOnly();
            string playlistTableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
            long playlistTableExists = songDb.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                playlistTableName);
            if (playlistTableExists == 0)
            {
                return new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: true);
            }

            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            foreach (BMSTable table in songDb.Table<BMSTable>())
            {
                string outputDirectory = ResolveManagedPlaylistOutputDirectory(table, options);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    directories.Add(outputDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            isComplete = false;
            LogInstallPerformanceWarn("lr2folder_app_managed_output_scope failed"
                + " exception=" + ex.GetType().Name
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
        }

        return new Lr2SongDbSyncAppManagedOutputScope(
            [.. directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [],
            [],
            isComplete);
    }

    private static string ResolveManagedPlaylistOutputDirectory(BMSTable table, BmsLibraryOptionsSnapshot options)
    {
        if (table == null)
        {
            return null;
        }

        string outputDir;
        try
        {
            outputDir = table.Output_dir;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return null;
        }

        try
        {
            return SafeFullPathOrOriginal(BMSPlaylist.GetCustomFolderOutputDirectory(
                table,
                options.LR2CustomFolderOutputBaseDir,
                options.LR2CustomFolderOutputBaseDirRootType,
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories(options.LR2CustomFolderAdditionalOutputBaseDirs)));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
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

    private Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot()
    {
        return catalogChartInfoOwner.CreateLr2ResolverSnapshot();
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

    private ChartInfoIndexUpdateResult UpsertChartInfoIndexRows(IEnumerable<LR2SongDBExtended.chart_info> rows, string reason, bool dispatchPresentation = true)
    {
        return catalogChartInfoOwner.UpsertIndex(rows, reason, dispatchPresentation);
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
        bool completed = false;
        using (BeginOwnedDigestMutationWindow())
        {
            try
            {
                ExecuteLr2SongDbWrite(
                    () => result = catalogChartInfoOwner.BuildInline(reason, charts),
                    stage: "lr2_song_db_chart_info_inline_upsert_failed",
                    logReason: reason ?? "chart_info_inline_install");
                if (result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0)
                {
                    DispatchWarningPresentationChanged("install_package_inline_chart_info_parse_failure");
                }
                completed = true;
                return result;
            }
            finally
            {
                if (completed)
                {
                    DispatchOwnedChartDigestChanges(result.DigestChanges, reason ?? "install_package_inline");
                }
                else
                {
                    DispatchOwnedPotentialDigestChanges(
                        [.. (charts ?? []).Where(chart => chart != null)],
                        (reason ?? "install_package_inline") + "_failed");
                }
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
        InstallableMaintenanceDeferredCoordinator.Queue(this, reason, criticalElapsedMs, dependency);
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

    private static async Task WaitForUiIdleAsync()
    {
        if (DispatcherHelper.UIDispatcher == null)
        {
            return;
        }
        await DispatcherHelper.UIDispatcher.InvokeAsync(delegate
        {
        }, DispatcherPriority.ContextIdle).Task.ConfigureAwait(false);
        await DispatcherHelper.UIDispatcher.InvokeAsync(delegate
        {
        }, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
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
            Task.Run(ProcessDeferredScoreHydrationRequests).Logging("ProcessDeferredScoreHydrationRequests");
        }
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
                    IrScorePrefetchResult result = irService.PrefetchIrScoreTableWithMetrics(lr2Id, irClient, lr2IRScoreRegex);
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
                        FailureReason = "exception"
                    };
                }
            }).Logging("IrScorePrefetch");
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
            task.Wait();
        }
        catch
        {
            waitStopwatch.Stop();
            waitMs = waitStopwatch.ElapsedMilliseconds;
            status = "failed";
            LogInstallPerformance("ir_score_prefetch consume generation=" + generation + " status=failed requestVersion=" + requestVersion + " waitMs=" + waitMs);
            return null;
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
            return null;
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
            Task.Run(ProcessDeferredRankingRefreshRequests).Logging("ProcessDeferredRankingRefreshRequests");
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
            Task.Run(ProcessDeferredRankingRefreshRequests).Logging("ProcessDeferredRankingRefreshRequests");
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
                ReportStartupBackgroundTask("ranking_refresh_deferred", "done", stopwatch.ElapsedMilliseconds, failed: false);
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
        var irScoreStopwatch = Stopwatch.StartNew();
        BmsLibraryOptionsSnapshot optionsSnapshot = CurrentOptionsSnapshot;
        if (optionsSnapshot.EnableDownloadLr2IrScoreAndDetectUnsent)
        {
            IrScorePrefetchResult prefetchedScore = TryConsumeIrScorePrefetch(requestVersion, optionsSnapshot, out long prefetchWaitMs, out string prefetchStatus);
            IrScoreTableUpdateResult irScoreUpdateResult = updateLR2IRScoreTableWithMetrics(prefetchedScore);
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
            if (IsDeferredRankingRefreshRequestSuperseded(requestVersion))
            {
                throw new OperationCanceledException();
            }
            var mergeStopwatch = Stopwatch.StartNew();
            updateBMSScores(scoreTable, detectUnsentScores: true);
            RefreshScoreSnapshotFromCurrentScores("deferred_ranking_refresh_ir_score");
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
        if (optionsSnapshot.UpdateLr2IrRankingCacheOnStartup)
        {
            var cacheStopwatch = Stopwatch.StartNew();
            setRankingScore();
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

    private List<string> getBMSDirectories(out BmsSearchRootNormalizationSnapshot normalizationSnapshot)
    {
        if (UseLR2 && lr2config != null)
        {
            try
            {
                LR2Config config = lr2config();
                if (config != null)
                {
                    SearchTargets = config.GetBMSSearchDirectories();
                }
            }
            catch
            {
                SearchTargets = [];
            }
        }
        HashSet<string> excludedCustomOutputSearchRoots = BuildExcludedCustomOutputSearchRootDirectories();
        List<string> requestedRoots = [.. (SearchTargets ?? Enumerable.Empty<string>())];
        List<string> existingRoots = [.. requestedRoots.Where(d => !string.IsNullOrWhiteSpace(d) && LongPathFileSystem.DirectoryExists(d))];
        List<string> roots = [.. existingRoots
            .Where(d => !IsExcludedCustomOutputSearchRoot(d, excludedCustomOutputSearchRoots))];
        normalizationSnapshot = new BmsSearchRootNormalizationSnapshot
        {
            RequestedRootCount = requestedRoots.Count,
            ExistingRootCount = existingRoots.Count,
            ExcludedCustomOutputRootCount = existingRoots.Count - roots.Count,
            RootCount = roots.Count,
            ConfiguredCustomOutputRootCount = excludedCustomOutputSearchRoots.Count
        };
        return roots;
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

    private HashSet<string> BuildExcludedCustomOutputSearchRootDirectories()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (!options.OperationModeLR2DB)
        {
            return excluded;
        }
        foreach (string additionalOutputBase in options.LR2CustomFolderAdditionalOutputBaseDirs)
        {
            AddNormalizedDirectory(excluded, additionalOutputBase);
        }
        AddNormalizedDirectory(excluded, options.LR2CustomFolderOutputBaseDirRootType);
        return excluded;
    }

    private static void AddNormalizedDirectory(HashSet<string> directories, string path)
    {
        if (directories == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            directories.Add(LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(path)));
        }
        catch
        {
        }
    }

    private static bool IsExcludedCustomOutputSearchRoot(string directory, HashSet<string> excludedCustomOutputSearchRoots)
    {
        if (string.IsNullOrWhiteSpace(directory) || excludedCustomOutputSearchRoots == null || excludedCustomOutputSearchRoots.Count == 0)
        {
            return false;
        }
        string normalized;
        try
        {
            normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
        }
        catch
        {
            return false;
        }
        return excludedCustomOutputSearchRoots.Any(excluded => Lr2FolderPath.IsSameOrDescendant(normalized, excluded));
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

    private void InvalidatePlaylistSummaryOwnedHashSnapshot(int ownedCollectionVersion = 0)
    {
        int resolvedOwnedCollectionVersion = ownedCollectionVersion > 0 ? ownedCollectionVersion : OwnedChartCollectionVersion;
        lock (lockPlaylistSummaryOwnedHashSnapshot)
        {
            playlistSummaryOwnedHashSnapshot = null;
            playlistSummaryOwnedHashInvalidationVersion++;
            playlistSummaryOwnedHashInvalidationOwnedCollectionVersion = resolvedOwnedCollectionVersion;
        }
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
        Interlocked.Increment(ref ownedDigestMutationWindowDepth);
        return new OwnedDigestMutationWindowScope(this, resourceHealthOwner.BeginInputMutation());
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
            Interlocked.Decrement(ref ownedDigestMutationWindowDepth);
            InvalidatePlaylistSummaryOwnedHashSnapshot();
            InvalidatePlaylistLibraryResolveIndexSnapshot();
        }
    }

    private bool IsOwnedDigestMutationWindowActive()
    {
        return Volatile.Read(ref ownedDigestMutationWindowDepth) > 0;
    }

    private void WaitForOwnedDigestMutationWindowIdle(CancellationToken cancellationToken = default)
    {
        while (IsOwnedDigestMutationWindowActive())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(20);
        }
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
        lock (lockInstalledPrimaryHashLookup)
        {
            installedPrimaryHashLookup = new PrimaryHashLookupState();
            installedPrimaryHashLookupInitialized = false;
        }
        lock (lockInstalledChartLookupIndex)
        {
            installedChartLookupIndex = new InstalledChartLookupIndexState();
            installedChartLookupIndexInitialized = false;
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

        public InstalledChartLookupMutation InstalledLookupMutation { get; set; } = new();

        public InstallDestinationRuntimeStateMutation InstallDestinationRuntimeStateMutation { get; } = new();

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

        public bool PlaylistSummaryOwnedHashInvalidated { get; set; }

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
            || PlaylistSummaryOwnedHashInvalidated
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

        public bool HasHashSetChanges => AddedCount > 0 || RemovedCount > 0;

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

    /// <summary>
    /// playlist summary 集計用の所持譜面ハッシュ snapshot を返します。
    /// 所持譜面や digest 変更時に無効化し、次回要求時にだけ再構築します。
    /// </summary>
    internal PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot()
    {
        return GetPlaylistSummaryOwnedHashSnapshot(out _, out _);
    }

    internal PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot(CancellationToken cancellationToken)
    {
        return GetPlaylistSummaryOwnedHashSnapshot(cancellationToken, out _, out _);
    }

    private PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot(out bool cacheHit, out int staleRetryCount)
    {
        return GetPlaylistSummaryOwnedHashSnapshot(CancellationToken.None, out cacheHit, out staleRetryCount);
    }

    private PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot(CancellationToken cancellationToken, out bool cacheHit, out int staleRetryCount)
    {
        PlaylistSummaryOwnedHashSnapshot snapshot;
        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool waitForDigestWindow = false;
            int invalidationVersion;
            int invalidationOwnedCollectionVersion;
            lock (lockPlaylistSummaryOwnedHashSnapshot)
            {
                snapshot = playlistSummaryOwnedHashSnapshot;
                int currentOwnedCollectionVersion = OwnedChartCollectionVersion;
                if (snapshot != null)
                {
                    if (IsPlaylistSummaryOwnedHashSnapshotCurrent(snapshot, currentOwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistSummaryOwnedHashSnapshot = null;
                    playlistSummaryOwnedHashInvalidationVersion++;
                    playlistSummaryOwnedHashInvalidationOwnedCollectionVersion = currentOwnedCollectionVersion;
                }
                if (IsOwnedDigestMutationWindowActive())
                {
                    waitForDigestWindow = true;
                }
                invalidationVersion = playlistSummaryOwnedHashInvalidationVersion;
                invalidationOwnedCollectionVersion = playlistSummaryOwnedHashInvalidationOwnedCollectionVersion;
            }
            if (waitForDigestWindow)
            {
                WaitForOwnedDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            OwnedChartHashIndexSnapshot ownedHashSnapshot;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                ownedHashSnapshot = CreateOwnedHashIndexSnapshotUnsafe(cancellationToken, out storageRowsVersion);
                ownedCollectionVersion = OwnedChartCollectionVersion;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var rebuiltSnapshot = new PlaylistSummaryOwnedHashSnapshot(
                ownedHashSnapshot.Md5Hashes,
                ownedHashSnapshot.Sha256Hashes)
            {
                BuildElapsedMs = stopwatch.ElapsedMilliseconds,
                InvalidationVersion = invalidationVersion,
                OwnedCollectionVersion = invalidationOwnedCollectionVersion == ownedCollectionVersion ? invalidationOwnedCollectionVersion : ownedCollectionVersion,
                BmsRowsVersion = storageRowsVersion.BmsRowsVersion,
                BmsonRowsVersion = storageRowsVersion.BmsonRowsVersion
            };
            lock (lockPlaylistSummaryOwnedHashSnapshot)
            {
                snapshot = playlistSummaryOwnedHashSnapshot;
                if (snapshot != null)
                {
                    if (IsPlaylistSummaryOwnedHashSnapshotCurrent(snapshot, OwnedChartCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistSummaryOwnedHashSnapshot = null;
                    playlistSummaryOwnedHashInvalidationVersion++;
                    playlistSummaryOwnedHashInvalidationOwnedCollectionVersion = OwnedChartCollectionVersion;
                    staleRetryCount++;
                    continue;
                }
                if (playlistSummaryOwnedHashInvalidationVersion != invalidationVersion
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
                rebuiltSnapshot.Version = Interlocked.Increment(ref playlistSummaryOwnedHashSnapshotVersion);
                playlistSummaryOwnedHashSnapshot = rebuiltSnapshot;
                cacheHit = false;
                return rebuiltSnapshot;
            }
        }
    }

    private bool IsPlaylistSummaryOwnedHashSnapshotCurrent(
        PlaylistSummaryOwnedHashSnapshot snapshot,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion
            && catalogStorageRowsOwner.BmsRowsVersion == snapshot.BmsRowsVersion
            && catalogStorageRowsOwner.BmsonRowsVersion == snapshot.BmsonRowsVersion;
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

    private OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshotUnsafe()
    {
        return CreateOwnedHashIndexSnapshotUnsafe(out _);
    }

    private OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshotUnsafe(out StorageRowsVersionSnapshot storageRowsVersion)
    {
        return CreateOwnedHashIndexSnapshotUnsafe(CancellationToken.None, out storageRowsVersion);
    }

    private OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshotUnsafe(
        CancellationToken cancellationToken,
        out StorageRowsVersionSnapshot storageRowsVersion)
    {
        EnsureOwnedChartCollectionBuiltUnsafe(cancellationToken);
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
                return catalogOwnedCollectionOwner.Collection.CreateOwnedHashIndexSnapshot(cancellationToken);
            }
        }
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

    /// <summary>
    /// playlist summary の所持 hash snapshot を readiness 外で温めます。
    /// </summary>
    /// <param name="reason">warmup を要求した理由。</param>
    /// <returns>warmup 結果。</returns>
    internal OwnedHashIndexWarmupResult WarmPlaylistSummaryOwnedHashSnapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        PlaylistSummaryOwnedHashSnapshot snapshot = GetPlaylistSummaryOwnedHashSnapshot(out bool cacheHit, out int staleRetryCount);
        stopwatch.Stop();
        var result = new OwnedHashIndexWarmupResult
        {
            IndexName = "playlist_summary_owned_hash",
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
            CaptureStorageRowsForOwnedCollectionUnsafe(
                out List<BMSFile> bmsFiles,
                out List<LR2SongDBExtended.bmson_song> bmsonSongs,
                out int bmsRowsVersion,
                out int bmsonRowsVersion);
            if (catalogOwnedCollectionOwner.IsCurrent(bmsRowsVersion, bmsonRowsVersion))
            {
                return;
            }

            OwnedChartCollectionState rebuiltCollection = OwnedChartCollectionState.FromStorageRows(
                bmsFiles,
                bmsonSongs,
                cancellationToken,
                out OwnedChartStorageRowFilterSummary filterSummary);
            cancellationToken.ThrowIfCancellationRequested();
            LogOwnedChartCollectionSkippedRows("build", filterSummary);
            lock (lockStorageRowsVersion)
            {
                if (bmsRowsVersion != bmsStorageRowsVersion
                    || bmsonRowsVersion != bmsonStorageRowsVersion)
                {
                    continue;
                }
                if (catalogOwnedCollectionOwner.IsCurrent(bmsRowsVersion, bmsonRowsVersion))
                {
                    return;
                }
                catalogOwnedCollectionOwner.ApplyBuiltCollection(
                    rebuiltCollection,
                    bmsRowsVersion,
                    bmsonRowsVersion);
                return;
            }
        }
    }

    internal void ApplyFileScanStorageMutation(SongTableFileCheckResult fileCheckResult, string reason)
    {
        if (fileCheckResult == null)
        {
            return;
        }

        OwnedChartCollectionMutationResult mutationResult = null;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            CatalogFileScanStorageReplacementRequest request = catalogMutationOwner.CreateFileScanStorageReplacementRequest(
                fileCheckResult.HasDbDiff,
                fileCheckResult.NextFiles,
                fileCheckResult.NextBmsonSongs,
                fileCheckResult.DeletedPaths,
                fileCheckResult.DeletedBmsonPaths,
                fileCheckResult.AddedFiles,
                fileCheckResult.AddedBmsonSongs);
            ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            try
            {
                mutationResult = CreateFileScanMutationProjection(
                    request,
                    resourceHealthMutation.BaseIndexCurrent);
                PublishOwnedCollectionChangeNotification(mutationResult);
                try
                {
                    using (mutationResult.ResourceHealthIndexInvalidated
                        ? resourceHealthOwner.SuppressInvalidation()
                        : null)
                    {
                        CatalogFileScanStorageReplacementReceipt receipt = catalogMutationOwner.ApplyFileScanStorageReplacement(
                            request,
                            storageRows =>
                            {
                                if (fileCheckResult.HasDbDiff)
                                {
                                    MarkDuplicateWarningFullClearPending();
                                }
                                libraryResourceIndex = fileCheckResult.NextResourceIndex ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult());
                                directoryResourceLookupCache = libraryResourceIndex.DirectoryLookupCache ?? new DirectoryResourceLookupCache();
                            });
                        if (receipt.OwnedCollectionApplied)
                        {
                            LogOwnedChartCollectionSkippedRows("replace", receipt.FilterSummary);
                        }
                    }
                }
                catch
                {
                    ApplyFileScanStorageMutationFailureFallback(mutationResult);
                    throw;
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }
        }

        string dispatchReason = string.IsNullOrWhiteSpace(reason)
            ? "file_scan"
            : "file_scan_" + reason;
        DispatchOwnedChartCollectionMutation(mutationResult, dispatchReason);
    }

    private void ApplyFileScanStorageMutationFailureFallback(OwnedChartCollectionMutationResult mutationResult)
    {
        if (mutationResult.ShouldDispatchInstalledLookup)
        {
            InvalidateInstalledDirectoryIndex();
        }
        else if (mutationResult.InstallEstimationMetadataProfileCacheInvalidated)
        {
            InvalidateInstallEstimationMetadataProfileCache();
        }
        if (mutationResult.ParentFolderInvalidated)
        {
            InvalidateBMSParentFolderListCacheAndNotify();
        }
        if (mutationResult.DuplicateCacheInvalidated)
        {
            InvalidateDuplicateChartGroupsCache();
        }
        if (mutationResult.PlaylistSummaryOwnedHashInvalidated)
        {
            InvalidatePlaylistSummaryOwnedHashSnapshot();
        }
        if (mutationResult.OwnedCollectionChanged)
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            PublishOwnedCollectionChangeNotification(mutationResult);
        }
        if (mutationResult.ResourceHealthMutation.HasChanges)
        {
            resourceHealthOwner.ForceInvalidate("file_scan_storage_failed");
        }
        if (mutationResult.InstallDestinationRuntimeStateMutation.HasChanges
            || mutationResult.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
        {
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
        }
        InvalidateOwnedChartCollection();
        ClearNormalLibraryRefreshNotification(mutationResult);
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

    private void ApplyInstalledChartStorageTargets(ChartStorageTargetSet addedTargets, string lookupReason)
    {
        if (addedTargets == null)
        {
            return;
        }

        ThrowIfLr2SongDbSyncMutationBlocked("ApplyInstalledChartStorageTargets");
        OwnedChartCollectionMutationResult mutationResult = null;
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        try
        {
            resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            try
            {
                mutationResult = BuildOwnedChartCollectionUpsertMutationResult(
                    addedTargets,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                PublishOwnedCollectionChangeNotification(mutationResult);
                using (mutationResult.ResourceHealthIndexInvalidated
                    ? resourceHealthOwner.SuppressInvalidation()
                    : null)
                {
                    catalogMutationOwner.ApplyInstalledTargetUpsert(
                        addedTargets.BmsFiles,
                        addedTargets.BmsonSongs);
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
            SyncLr2NormalFoldersForOwnedMutation(
                mutationResult.StorageMutation,
                lookupReason ?? "install_package");
            DispatchOwnedChartCollectionMutation(mutationResult, lookupReason);
        }
        catch
        {
            ApplyInstalledChartStorageTargetsFailureFallback(mutationResult);
            throw;
        }
    }

    private void ApplyInstalledChartStorageTargetsFailureFallback(OwnedChartCollectionMutationResult mutationResult)
    {
        if (mutationResult?.ShouldDispatchInstalledLookup != false)
        {
            InvalidateInstalledDirectoryIndex();
        }
        if (mutationResult?.PlaylistSummaryOwnedHashInvalidated == true)
        {
            InvalidatePlaylistSummaryOwnedHashSnapshot();
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
            PlaylistSummaryOwnedHashInvalidated = storageRowsChanged,
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

    private void CaptureStorageRowsForOwnedCollectionUnsafe(
        out List<BMSFile> bmsFiles,
        out List<LR2SongDBExtended.bmson_song> bmsonSongs,
        out int bmsRowsVersion,
        out int bmsonRowsVersion)
    {
        lock (lockStorageRowsVersion)
        {
            CatalogStorageRowsSnapshot snapshot = catalogStorageRowsOwner.CaptureSnapshot();
            bmsFiles = [.. snapshot.BmsRows];
            bmsonSongs = [.. snapshot.BmsonRows];
            bmsRowsVersion = snapshot.BmsRowsVersion;
            bmsonRowsVersion = snapshot.BmsonRowsVersion;
        }
    }

    private StorageRowsVersionSnapshot CaptureStorageRowsVersionUnsafe()
    {
        lock (lockStorageRowsVersion)
        {
            return CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
    }

    private static List<BMSFile> NormalizeBmsStorageRows(IReadOnlyList<BMSFile> files)
    {
        return files == null ? [] : [.. files];
    }

    private static List<LR2SongDBExtended.bmson_song> NormalizeBmsonStorageRows(IReadOnlyList<LR2SongDBExtended.bmson_song> songs)
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
            PlaylistSummaryOwnedHashInvalidated = storageMutation.HasHashSetChanges,
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
        result.PlaylistSummaryOwnedHashInvalidated = result.StorageMutation.HasHashSetChanges;
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
            PlaylistSummaryOwnedHashInvalidated = anyChanges,
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
            PlaylistSummaryOwnedHashInvalidated = true,
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

    private void DispatchOwnedChartCollectionMutation(OwnedChartCollectionMutationResult result, string reason)
    {
        if (result == null)
        {
            return;
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
        if (result.InstallDestinationRuntimeStateMutation.HasStateChanges)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.Apply(result.InstallDestinationRuntimeStateMutation);
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DigestChangedCount > 0)
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
            InvalidateBMSParentFolderListCacheAndNotify();
            parentFolderMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DuplicateCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidateDuplicateChartGroupsCache();
            duplicateMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.PlaylistSummaryOwnedHashInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidatePlaylistSummaryOwnedHashSnapshot();
            playlistSummaryMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.OwnedCollectionChanged)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            PublishOwnedCollectionChangeNotification(result);
            ownedCollectionNotifyMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.PlaylistSummaryOwnedHashInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidatePlaylistSummaryOwnedHashSnapshot(result.OwnedCollectionVersion);
            playlistSummaryMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.OwnedCollectionChanged)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidatePlaylistLibraryResolveIndexSnapshot(result.OwnedCollectionVersion);
            ownedCollectionNotifyMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (result.ResourceHealthMutation.RebuildFull && result.OwnedCollectionChanged)
            {
                result.ResourceHealthMutation.FullOwnedTargetSet = CreateFullOwnedResourceMaintenanceTargetSet(reason);
            }
            result.ResourceHealthDispatchResult = DispatchResourceHealthIndexMutation(result.ResourceHealthMutation, reason);
            resourceHealthMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (!result.ResourceHealthMutation.HasChanges && result.OwnedCollectionChanged)
        {
            resourceHealthOwner.RebaseCurrentVersion(GetCurrentResourceHealthIndexVersion());
        }
        bool installMetadataProfileCacheInvalidated = result.InstallEstimationMetadataProfileCacheInvalidated || result.ShouldDispatchInstalledLookup;
        if (installMetadataProfileCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidateInstallEstimationMetadataProfileCache();
            installMetadataMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.ShouldDispatchInstalledLookup)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            ApplyInstalledChartLookupMutation(result.InstalledLookupMutation, reason);
            installedLookupMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
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
                + " playlistSummaryHash=" + ToInvalidateLogValue(result.PlaylistSummaryOwnedHashInvalidated)
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

    private void DispatchOwnedChartDigestChanges(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        string reason,
        bool resourceHealthIndexInvalidated = true)
    {
        CatalogDigestMutationRequest request = catalogMutationOwner.CreateDigestMutationRequest(digestChanges);
        OwnedChartCollectionMutationResult mutationResult = CreateOwnedChartCollectionDigestMutationResult(
            request.DigestChanges,
            resourceHealthIndexInvalidated);
        mutationResult.DigestMutationRequest = request;
        DispatchOwnedChartCollectionMutationWithResourceHealthLease(mutationResult, reason);
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
            case CatalogChartInfoOwnerEventKind.DigestChanges:
                DispatchOwnedChartDigestChanges(ownerEvent.DigestChanges, ownerEvent.Reason);
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

    private void ApplyInstalledChartLookupMutation(InstalledChartLookupMutation mutation, string reason)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return;
        }
        ApplyInstalledPrimaryHashLookupMutation(mutation, reason);
        lock (lockInstalledChartLookupIndex)
        {
            if (!installedChartLookupIndexInitialized)
            {
                return;
            }
            if (mutation.RequiresFullInvalidate)
            {
                installedChartLookupIndex = new InstalledChartLookupIndexState();
                installedChartLookupIndexInitialized = false;
                LogInstallPerformance("installed_chart_lookup_index update mode=full_invalidate reason=" + reason);
                return;
            }
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
            LogInstallPerformance("installed_chart_lookup_index update mode=incremental reason=" + reason + " removed=" + mutation.Removed.Count + " moved=" + mutation.Moved.Count + " added=" + mutation.Added.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + installedChartLookupIndex.HashCount + " primaryHashes=" + installedChartLookupIndex.DistinctPrimaryHashCount + " dirRefs=" + installedChartLookupIndex.DirectoryReferenceCount);
        }
    }

    private void ApplyInstalledPrimaryHashLookupMutation(InstalledChartLookupMutation mutation, string reason)
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
                LogInstallPerformance("installed_primary_hash_lookup update mode=full_invalidate reason=" + reason);
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
            LogInstallPerformance("installed_primary_hash_lookup update mode=incremental reason=" + reason + " removed=" + mutation.Removed.Count + " moved=" + mutation.Moved.Count + " added=" + mutation.Added.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount);
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

    private bool EnsureInstalledPrimaryHashLookupBuiltUnsafe(out long buildMs, out int bmsCount, out int bmsonCount)
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
            LogInstallPerformance("installed_primary_hash_lookup build mode=full buildMs=" + buildMs + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount + " files=" + bmsCount + " bmson=" + bmsonCount + " rows=" + (bmsCount + bmsonCount) + " source=owned_collection_primary singleFlight=true");
            return true;
        }
    }

    /// <summary>
    /// 現在のインストール済み chart lookup index のスナップショットを取得します。
    /// </summary>
    private InstalledChartLookupIndexSnapshot CreateInstalledChartLookupSnapshotUnsafe()
    {
        EnsureInstalledChartLookupIndexBuiltUnsafe();
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndex.CreateSnapshot();
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
        var knownChartDirectories = new HashSet<string>((directoryResourceLookupCache?.Keys ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
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

    private IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(IEnumerable<ChartFile> excluded, string reason, long operationId)
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
        bool coldBuild = EnsureInstalledPrimaryHashLookupBuiltUnsafe(out long buildMs, out int bmsCount, out int bmsonCount);
        IPrimaryHashLookup result;
        lock (lockInstalledPrimaryHashLookup)
        {
            result = installedPrimaryHashLookup.CreateExcludingLookup(excludedKeyCount);
        }
        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            LogInstallPerformance("installed_primary_hash_lookup excluding_snapshot reason=" + reason
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
        return irService.UpdateIrScoreTableWithMetrics(LR2ID, dbGateway, irClient, lr2IRScoreRegex, prefetchedScore);
    }

    private void updateBMSScores(List<LR2IRScore> scoreTable)
    {
        updateBMSScores(scoreTable, detectUnsentScores: true);
    }

    private void updateBMSScores(List<LR2IRScore> scoreTable, bool detectUnsentScores)
    {
        if (activeScoreSource != ActiveScoreSource.Lr2 || lr2ScoreDBPath == null || LR2ID == 0 || scoreTable == null)
        {
            return;
        }
        using (rwlockBMSScores.GetWriterGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                BMSScores = irService.UpdateBmsScores(scoreTable, BMSScores, BMSFiles, detectUnsentScores);
            }
        }
        RefreshScoreSnapshotFromCurrentScores("update_ir_score_table");
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ApplyCurrentScoreSnapshotToFiles(BMSFiles);
        }
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

    private IrCacheRefreshResult setRankingScore()
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        var result = new IrCacheRefreshResult();
        if (activeScoreSource != ActiveScoreSource.Lr2 || lr2ScoreDBPath == null || LR2ID == 0)
        {
            return result;
        }
        LogInstallPerformance("ranking_cache_refresh start lr2Id=" + LR2ID);
        var stopwatch = Stopwatch.StartNew();
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            BmsLibraryIrService.IrCacheRefreshPlan preparedPlan = irService.PrepareRankingScoresRefreshPlanForLibrary(LR2ID, lr2ScoreDBPath, dbGateway);
            using (rwlockBMSScores.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    result = irService.ApplyPreparedRankingScoresRefreshPlanForLibrary(preparedPlan, lr2ScoreDBPath, BMSScores, BMSFiles, options.EstimateOfflineScoreRanking);
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
        RefreshScoreSnapshotFromCurrentScores("refresh_ranking_cache");
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ApplyCurrentScoreSnapshotToFiles(BMSFiles);
        }
        return result;
    }

    public List<IRDataCacheInfo> GetIRDataNeedUpdates(IEnumerable<string> md5s)
    {
        if (activeScoreSource != ActiveScoreSource.Lr2 || lr2ScoreDBPath == null || LR2ID == 0)
        {
            throw new InvalidOperationException(Resources.Error_LR2ScoreDBNotConnected);
        }
        using (rwlockLR2IrDir.GetReaderGuard())
        {
            return irService.GetIRDataNeedUpdates(LR2ID, md5s, dbGateway, irClient, rankingInfoUrl);
        }
    }

    public List<IRDataCacheInfo> DownloadIRData(IEnumerable<IRDataCacheInfo> cacheInfo)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (activeScoreSource != ActiveScoreSource.Lr2 || lr2ScoreDBPath == null || LR2ID == 0)
        {
            throw new InvalidOperationException(Resources.Error_LR2ScoreDBNotConnected);
        }
        if (cacheInfo == null)
        {
            throw new ArgumentNullException("cacheInfo");
        }
        string irCacheDirPath = Path.Combine(Path.GetDirectoryName(lr2ScoreDBPath), "..\\..\\Ir");
        if (!LongPathFileSystem.DirectoryExists(irCacheDirPath))
        {
            throw new DirectoryNotFoundException(string.Format(Resources.Error_IRCacheDirNotFound, irCacheDirPath));
        }
        List<IRDataCacheInfo> failed;
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            using (rwlockBMSScores.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    failed = irService.DownloadIRData(LR2ID, cacheInfo, irCacheDirPath, dbGateway, irClient, rankingDataUrl, BMSScores, BMSFiles, options.EstimateOfflineScoreRanking);
                }
            }
        }
        RefreshScoreSnapshotFromCurrentScores("download_ir_data");
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ApplyCurrentScoreSnapshotToFiles(BMSFiles);
        }
        return failed;
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
        if (mutation != null
            && (mutation.RebuildFull || (mutation.HasDeltaTargets && !mutation.InvalidateIfDeltaFails))
            && !mutation.FullOwnedTargetSet.HasFullOwnedVersion)
        {
            mutation.FullOwnedTargetSet = CreateFullOwnedResourceMaintenanceTargetSet(reason);
        }
        return resourceHealthOwner.Apply(
            mutation?.ToFacts(),
            reason,
            GetCurrentResourceHealthIndexVersion());
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
        using (rwlockBMSFiles.GetWriterGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreateResourceMaintenanceTargetSet(charts);
            return ApplyCatalogMaintenanceCore(
                maintenanceTargets,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode,
                resourceHealthMutationReason,
                out _);
        }
    }

    private MaintenanceWorkflowResult ApplyOwnedCatalogMaintenance(
        string reason,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates,
        string resourceHealthMutationReason = null)
    {
        using (rwlockBMSFiles.GetWriterGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreateFullOwnedResourceMaintenanceTargetSet(reason);
            return ApplyCatalogMaintenanceCore(
                maintenanceTargets,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode,
                resourceHealthMutationReason ?? reason,
                out _);
        }
    }

    private MaintenanceWorkflowResult ApplyInstallableCatalogMaintenance(
        string reason,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseHydratedMaintenanceSnapshotForInstallableMaintenance())
        {
            return ApplyOwnedCatalogMaintenance(
                reason,
                forceUpdate: false,
                progressReporter: progressReporter,
                cancellationToken: cancellationToken,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.FullOnUpdates,
                resourceHealthMutationReason: reason);
        }

        using (rwlockBMSFiles.GetWriterGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreatePendingInstallableMaintenanceTargetSetUnsafe(reason);
            return ApplyCatalogMaintenanceCore(
                maintenanceTargets,
                forceUpdate: false,
                progressReporter,
                cancellationToken,
                ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                reason,
                out _);
        }
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
        out List<ChartFile> currentMaintenanceTargetCharts)
    {
        CatalogMaintenanceOperationReceipt receipt = catalogMaintenanceOwner.ApplyMaintenance(
            maintenanceTargets,
            forceUpdate,
            progressReporter,
            cancellationToken,
            resourceHealthIndexUpdateMode,
            resourceHealthMutationReason);
        MaintenanceWorkflowResult workflowResult = receipt.WorkflowResult.ToMutable();
        currentMaintenanceTargetCharts = [.. maintenanceTargets.Charts];
        resourceHealthMutationReason = receipt.Reason;
        ResourceHealthIndexMutation resourceHealthMutation = receipt.ResourceHealthMutation.ToMutation();
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            receipt.ResourceHealthMutation,
            workflowResult.HasUpdates);
        try
        {
            DispatchOwnedChartCollectionMutation(mutationResult, resourceHealthMutationReason);
        }
        catch
        {
            InvalidateOwnedChartCollection();
            InvalidateInstalledDirectoryIndex();
            resourceHealthOwner.ForceInvalidate("maintenance_dispatch_failed");
            throw;
        }
        ResourceHealthIndexDispatchResult resourceHealthDispatch = mutationResult.ResourceHealthDispatchResult ?? new ResourceHealthIndexDispatchResult();
        bool resourceHealthDeltaApplied = resourceHealthDispatch.DeltaApplied;
        bool resourceHealthIndexDeferred = resourceHealthDispatch.Deferred;
        bool resourceHealthIndexFullRebuilt = resourceHealthDispatch.FullRebuilt;
        ResourceHealthIndexSnapshot resourceHealthSnapshot = resourceHealthDispatch.Snapshot ?? ResourceHealthIndexSnapshot.Empty;
        workflowResult.ResourceHealthIndexMs = resourceHealthMutation.HasChanges && !resourceHealthIndexDeferred ? resourceHealthDispatch.IndexMs : 0L;
        workflowResult.WarningReapplyTargets = 0;
        workflowResult.WarningChangedCount = 0;
        return LogAndReturnMaintenanceWorkflowResult(
            workflowResult,
            resourceHealthSnapshot,
            resourceHealthDeltaApplied,
            resourceHealthIndexDeferred,
            resourceHealthIndexFullRebuilt);
    }

    private MaintenanceWorkflowResult LogAndReturnMaintenanceWorkflowResult(
        MaintenanceWorkflowResult workflowResult,
        ResourceHealthIndexSnapshot resourceHealthSnapshot,
        bool resourceHealthDeltaApplied,
        bool resourceHealthIndexDeferred,
        bool resourceHealthIndexFullRebuilt)
    {
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
        _ = rwlockBMSFilesInitializedMin.IsWriteLockHeld;
        _ = rwlockBMSFiles.IsWriteLockHeld;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                if (useOwnedSnapshot && !forceUpdate)
                {
                    ResourceHealthIndexSnapshot currentSnapshot = GetResourceHealthIndexSnapshot("resource_health_filter");
                    return [.. (isInIgnoredList ? currentSnapshot.IgnoredTargets : currentSnapshot.ActiveTargets)];
                }
                ResourceMaintenanceTargetSet targetSet = useOwnedSnapshot
                    ? CreateFullOwnedResourceMaintenanceTargetSet("force_resource_health_filter")
                    : CreateResourceMaintenanceTargetSet(charts);
                List<ChartFile> targets = [.. targetSet.Charts];
                string resourceHealthReason = useOwnedSnapshot && forceUpdate
                    ? "force_resource_health_filter"
                    : "resource_health_filter";
                if (forceUpdate)
                {
                    ApplyCatalogMaintenanceCore(
                        targetSet,
                        forceUpdate: true,
                        progressReporter: null,
                        cancellationToken: default,
                        resourceHealthIndexUpdateMode: useOwnedSnapshot ? ResourceHealthIndexUpdateMode.FullOnUpdates : ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                        resourceHealthMutationReason: resourceHealthReason,
                        currentMaintenanceTargetCharts: out targets);
                }
                if (useOwnedSnapshot)
                {
                    ResourceHealthIndexSnapshot ownedSnapshot = GetResourceHealthIndexSnapshot(resourceHealthReason);
                    return [.. (isInIgnoredList ? ownedSnapshot.IgnoredTargets : ownedSnapshot.ActiveTargets)];
                }
                if (targets.Count == 0)
                {
                    return [];
                }
                return resourceHealthOwner.FilterTargetsByWarningState(targets, isInIgnoredList);
            }
        }
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
        OwnedChartCollectionMutationResult mutationResult = null;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<ChartFile> targets = NormalizeResourceMaintenanceTargetCharts(charts);
                if (targets.Count == 0)
                {
                    return;
                }
                CatalogMaintenanceOperationReceipt receipt = catalogMaintenanceOwner.ApplyWarningIgnore(targets, unset, reason);
                mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
                    receipt.ResourceHealthMutation,
                    receipt.WorkflowResult.HasUpdates);
                mutationResult.MaintenancePresentationChanged = false;
            }
        }
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
    private int setModeAndCommitToDB(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(setModeAndCommitToDB));
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> list = maintenanceService.DetectModeChanges(bmsFiles, forceUpdate);
            if (list.Count <= 0)
            {
                return 0;
            }
            ExecuteLr2SongDbWrite(
                () => dbGateway.UpsertSongs(list),
                stage: "lr2_song_db_mode_upsert_failed",
                logReason: nameof(setModeAndCommitToDB));
            return list.Count;
        }
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
        DirectoryResourceLookupCache effectiveDirectoryLookupCache = directoryLookupCacheSnapshot ?? directoryResourceLookupCache;
        long lazyHashBuildMsBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashBuildMs ?? 0L);
        long lazyHashLookupCountBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashLookupCount ?? 0L);
        int lazyHashCacheEntriesBefore = useSharedLazyHashMetrics ? 0 : (effectiveDirectoryLookupCache?.LazyHashCacheEntryCount ?? 0);
        bool sourceSurfaceBatchHit = batchState?.UsesBatchSourceSurface == true && batchState.SourceSurface != null;
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
        return playlistReferenceManager.Find(md5, sha256);
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(ChartFile chart)
    {
        return playlistReferenceManager.Find(chart);
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(LibraryChartRef chart)
    {
        return playlistReferenceManager.Find(chart);
    }

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        PlaylistReferenceApplyCoordinator.AddReferenceBMSTables(playlistReferenceService, this, table, entries);
    }

    public void AddReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        PlaylistReferenceApplyCoordinator.AddReferenceBMSTables(playlistReferenceService, this, tables);
    }

    public void AddReferenceBMSTablesIncremental(IEnumerable<BMSTable> tables)
    {
        PlaylistReferenceApplyCoordinator.AddReferenceBMSTablesIncremental(playlistReferenceService, this, tables);
    }

    /// <summary>
    /// Applies loaded playlist references to package chart entries after package install without materializing unmatched bmson entries.
    /// </summary>
    /// <param name="tables">Loaded playlist tables whose entries should be matched by chart hash.</param>
    /// <param name="packages">Packages containing newly installed chart entries.</param>
    public void AddReferenceBMSTablesToPackageCharts(IEnumerable<BMSTable> tables, IEnumerable<ChartPackage> packages)
    {
        PlaylistReferenceApplyCoordinator.AddReferenceBMSTablesToPackageCharts(playlistReferenceService, this, tables, packages);
    }

    internal void ReplaceReferenceBMSTable(BMSTable oldTable, BMSTable newTable, IEnumerable<BMSTableEntry> oldEntries = null, IEnumerable<BMSTableEntry> newEntries = null)
    {
        PlaylistReferenceApplyCoordinator.ReplaceReferenceBMSTable(this, oldTable, newTable, oldEntries, newEntries);
    }

    internal void AddReferenceBMSTablesToCharts(BMSTable table, IEnumerable<ChartFile> charts)
    {
        PlaylistReferenceApplyCoordinator.AddReferenceBMSTablesToCharts(this, table, charts);
    }

    internal void RefreshReferenceDisplayForTable(BMSTable table)
    {
        PlaylistReferenceApplyCoordinator.RefreshReferenceDisplayForTable(this, table);
    }

    internal void SynchronizeReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        PlaylistReferenceApplyCoordinator.SynchronizeReferenceBMSTables(this, tables);
    }

    /// <summary>
    /// 指定されたプレイリストの参照を BMS ファイル群から削除します。
    /// </summary>
    public void RemoveReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        PlaylistReferenceApplyCoordinator.RemoveReferenceBMSTables(this, table, entries);
    }

    public void RemoveReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        PlaylistReferenceApplyCoordinator.RemoveReferenceBMSTables(this, tables);
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
        MergeChartDirectory(src, dst, operationId: 0);
    }

    internal void MergeChartDirectory(string src, string dst, long operationId)
    {
        LibraryMergeDirectoryCoordinator.MergeChartDirectory(this, src, dst, operationId);
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

    internal void FixInstallationDirectoryCharts(IEnumerable<ChartFile> charts, IEnumerable<string> approvedDuplicateRemovalChartPaths = null)
    {
        LibraryFixInstallationCoordinator.FixInstallationDirectoryCharts(this, charts, approvedDuplicateRemovalChartPaths);
    }

    /// <summary>
    /// 譜面ファイル群のフォルダ名をメタデータに基づいて自動リネームします。
    /// </summary>
    internal void AutoRenameChartFolders(IEnumerable<ChartFile> chartFiles, bool renameRootFolder = false, Action<int, int, string> progressReporter = null)
    {
        if (chartFiles == null)
        {
            throw new ArgumentNullException("chartFiles");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(AutoRenameChartFolders)))
        {
            return;
        }
        List<Tuple<int, int, string>> deferredProgressReports = [];
        Action<int, int, string> deferredProgressReporter = progressReporter == null
            ? null
            : (total, processed, currentPath) => deferredProgressReports.Add(Tuple.Create(total, processed, currentPath));
        try
        {
            using (rwlockBMSFilesInitializedMin.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetWriterGuard())
                    {
                        List<ChartFile> selectedCharts = [.. chartFiles.Where(chart => chart != null)];
                        List<string> rootFolders = getBMSDirectories();
                        List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildAutoRenamePlans(
                            selectedCharts,
                            rootFolders,
                            renameRootFolder,
                            CreateDirectLibraryChartSnapshotsInFolders,
                            CreateChartFolderPathFromCharts,
                            NormalizeAutoRenameFolderName);
                        ApplyAutoRenamePlans(plans, deferredProgressReporter);
                    }
                }
            }
        }
        finally
        {
            FlushAutoRenameProgressReports(progressReporter, deferredProgressReports);
        }
    }

    internal bool HasAutoRenameAllChartFolderTargets(string parentDir = null)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return HasActionableAutoRenamePlan(CreateAutoRenameAllChartFolderPlansUnsafe(parentDir));
            }
        }
    }

    internal bool AutoRenameAllChartFolders(string parentDir = null, Action<int, int, string> progressReporter = null)
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(AutoRenameAllChartFolders)))
        {
            return false;
        }
        List<Tuple<int, int, string>> deferredProgressReports = [];
        Action<int, int, string> deferredProgressReporter = progressReporter == null
            ? null
            : (total, processed, currentPath) => deferredProgressReports.Add(Tuple.Create(total, processed, currentPath));
        try
        {
            using (rwlockBMSFilesInitializedMin.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetWriterGuard())
                    {
                        List<FolderAutoRenamePlan> plans = CreateAutoRenameAllChartFolderPlansUnsafe(parentDir);
                        if (!HasActionableAutoRenamePlan(plans))
                        {
                            return false;
                        }
                        return ApplyAutoRenamePlans(plans, deferredProgressReporter);
                    }
                }
            }
        }
        finally
        {
            FlushAutoRenameProgressReports(progressReporter, deferredProgressReports);
        }
    }

    private List<FolderAutoRenamePlan> CreateAutoRenameAllChartFolderPlansUnsafe(string parentDir)
    {
        List<string> sourceFolders = CreateOwnedRealPathChartDirectoriesUnsafe(parentDir);
        if (sourceFolders.Count == 0)
        {
            return [];
        }
        List<string> rootFolders = getBMSDirectories();
        return libraryFileOperationsService.BuildAutoRenamePlansForSourceFolders(
            sourceFolders,
            rootFolders,
            renameRootFolder: false,
            CreateDirectLibraryChartSnapshotsInFolders,
            CreateChartFolderPathFromCharts,
            NormalizeAutoRenameFolderName);
    }

    private static bool HasActionableAutoRenamePlan(IEnumerable<FolderAutoRenamePlan> plans)
    {
        return (plans ?? []).Any(plan => !string.IsNullOrWhiteSpace(plan?.SourceDirectory)
            && !string.IsNullOrWhiteSpace(plan.DestinationDirectory));
    }

    private bool ApplyAutoRenamePlans(IEnumerable<FolderAutoRenamePlan> plans, Action<int, int, string> progressReporter = null)
    {
        var coordinator = new AutoRenameBatchCoordinator(new AutoRenameBatchHost(this));
        return coordinator.Apply(plans, progressReporter);
    }

    internal sealed class AutoRenameBatchHost(BMSLibrary owner) : IAutoRenameBatchHost
    {
        public void LogInstallPerformance(string message)
        {
            BMSLibrary.LogInstallPerformance(message);
        }

        public void ShowDriveRootBmsSkipped()
        {
            owner.ShowOperationDialog(Resources.Warn_DriveRootBmsSkipped, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }

        public void ShowRenameFailed(FolderAutoRenamePlan plan)
        {
            owner.ShowOperationDialog(string.Format(Resources.Error_RenameFailed, plan.SourceDirectory, plan.FailureException.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }

        public void ShowRenameFolderNotExists(string sourceDirectory)
        {
            owner.ShowOperationDialog(string.Format(Resources.Warn_RenameFolderNotExists, sourceDirectory), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }

        public string NormalizeAutoRenameFolderName(string folderName)
        {
            return owner.NormalizeAutoRenameFolderName(folderName);
        }

        public bool DirectoryExists(string directoryPath)
        {
            return LongPathFileSystem.DirectoryExists(directoryPath);
        }

        public InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
        {
            return owner.installDestinationStateOwner.CreateOverlaySnapshot(out _);
        }

        public LibraryMutationDelta BuildFolderMoveDelta(
            string sourceDirectory,
            string destinationDirectory,
            InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts)
        {
            return owner.libraryFileOperationsService.BuildFolderMoveDelta(
                sourceDirectory,
                destinationDirectory,
                owner.CreateOwnedRealPathChartRefsUnsafe(sourceDirectory),
                installDestinationOverlayCharts,
                owner.ChartPackagesPending,
                owner.ChartPackagesInstalled,
                unregister: false,
                notifyStorageRowPathChanges: false);
        }

        public bool TryMoveLibraryChartFolderFileOnly(string sourceDirectory, string destinationDirectory)
        {
            return owner.TryMoveLibraryChartFolderFileOnly(sourceDirectory, destinationDirectory);
        }

        public MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(List<LibraryFolderPathChange> movedFolders)
        {
            return owner.libraryFileOperationsService.UpdateMovedFolderReferences(movedFolders, owner.directoryResourceLookupCache);
        }

        public void LogReverseLookupMutationAndQueueWarmupIfNeeded(
            string reason,
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
        {
            owner.LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
        }

        public void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string reason)
        {
            owner.ApplyLibraryMutationDeltaWithPerformanceContext(delta, reason);
        }
    }

    private static void FlushAutoRenameProgressReports(Action<int, int, string> progressReporter, IEnumerable<Tuple<int, int, string>> reports)
    {
        if (progressReporter == null)
        {
            return;
        }
        foreach (Tuple<int, int, string> report in reports ?? [])
        {
            ReportAutoRenameProgress(progressReporter, report.Item1, report.Item2, report.Item3);
        }
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
            NLogWrapper.FileLogger?.Warn(ex, "auto_rename_progress_report_failed processed=" + processed + " total=" + total);
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
        LibraryFolderMoveCoordinator.RenameChartFolder(this, srcDir, newName, unregister, renameRootFolder);
    }

    internal void MoveLibraryRootFolder(IEnumerable<LibraryChartRef> charts, string dstDir, bool? unregister = false)
    {
        LibraryFolderMoveCoordinator.MoveLibraryRootFolder(this, charts, dstDir, unregister);
    }

    private bool TryMoveLibraryChartFolderFileOnly(string srcDir, string dstDir)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (LongPathFileSystem.EntryExists(dstDir))
        {
            ShowOperationDialog(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return false;
        }
        try
        {
            libraryFileOperationsService.MoveFolder(srcDir, dstDir, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
            return true;
        }
        catch (Exception moveException)
        {
            ShowOperationDialog(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }
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
        InvalidExtensionRenameCoordinator.RenameBMSFilesExtensions(
            this,
            charts,
            newExt,
            unregister);
    }

    internal void RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        InvalidExtensionRenameCoordinator.RenamePendingBmsFormatChartFileExtensions(
            this,
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

    internal void RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        LibraryChartRemovalCoordinator.RemoveLibraryCharts(
            this,
            charts,
            sendToRecycleBin,
            approvedWholeFolderDeletePaths);
    }

    internal void RemovePendingCharts(IEnumerable<ChartFile> charts, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        if (charts == null)
        {
            throw new ArgumentNullException("charts");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    PendingFileDeletionResult result = packageInstallService.DeletePendingCharts(
                        charts,
                        ChartPackagesPending,
                        sendToRecycleBin,
                        deleteContainingPackageFoldersWhenNoBms,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        recursiveDirectoryTreeFileMutationOptions);
                    foreach (PendingFileDeletionFailure failure in result.Failures)
                    {
                        if (failure?.Exception == null)
                        {
                            continue;
                        }
                        if (failure.IsDirectory)
                        {
                            ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
                }
            }
        }
    }

    private void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
    {
        List<string> paths = [.. (chartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Count == 0)
        {
            return;
        }
        stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(chartPathsToRemove: paths));
    }

    internal void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        ApplyLibraryMutationDeltaCore(delta, performanceLogContext: null);
    }

    private void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext)
    {
        ApplyLibraryMutationDeltaCore(delta, performanceLogContext);
    }

    private void ApplyLibraryMutationDeltaCore(LibraryMutationDelta delta, string performanceLogContext)
    {
        const string defaultReason = "library_delta";
        ThrowIfLr2SongDbSyncMutationBlocked("ApplyLibraryMutationDelta");
        bool collectPerformanceLog = !string.IsNullOrWhiteSpace(performanceLogContext);
        Stopwatch totalStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
        var timings = new LibraryMutationDeltaApplyTimings();
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        OwnedChartCollectionMutationResult mutationResult = null;
        CatalogMutationReceipt catalogReceipt = null;
        bool catalogMutationExpected = false;
        bool catalogMutationCommitted = false;
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
                        || mutationResult?.StorageMutation?.RemoveRequests?.Count > 0;
                    catalogReceipt = catalogMutationOwner.ApplyCatalogMutation(
                        delta,
                        mutationResult.StorageMutation.RemoveRequests,
                        mutationResult.StorageMutation.AddedBmsFiles,
                        mutationResult.StorageMutation.AddedBmsonSongs,
                        () => catalogMutationCommitted = true);
                    ApplyCatalogMutationReceiptProjection(mutationResult, catalogReceipt);
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

                Stopwatch publishNotificationStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                PublishOwnedCollectionChangeNotification(mutationResult);
                timings.PublishNotificationMs = StopPerformanceStepStopwatch(publishNotificationStopwatch);

                Stopwatch residualApplyStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                BmsLibraryStateApplyResult residualStateApplyResult = stateApplier.ApplyLibraryMutationDelta(
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
            SyncLr2NormalFoldersForOwnedMutation(mutationResult.StorageMutation, performanceLogContext ?? defaultReason);
            timings.Lr2NormalFolderSyncMs = StopPerformanceStepStopwatch(lr2NormalFolderSyncStopwatch);
            Stopwatch dispatchStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            DispatchOwnedChartCollectionMutation(mutationResult, defaultReason);
            timings.DispatchMs = StopPerformanceStepStopwatch(dispatchStopwatch);
            if (collectPerformanceLog)
            {
                timings.ElapsedMs = StopPerformanceStepStopwatch(totalStopwatch);
                LogInstallPerformance("library_mutation_delta_apply context=" + performanceLogContext
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
                    + " resourceHealthDisposeMs=" + timings.ResourceHealthDisposeMs
                    + " lr2NormalFolderSyncMs=" + timings.Lr2NormalFolderSyncMs
                    + " dispatchMs=" + timings.DispatchMs
                    + " elapsedMs=" + timings.ElapsedMs);
            }
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
                InvalidateBMSParentFolderListCacheAndNotify();
            }
            if (mutationResult?.DuplicateCacheInvalidated == true)
            {
                InvalidateDuplicateChartGroupsCache();
            }
            if (mutationResult?.PlaylistSummaryOwnedHashInvalidated == true)
            {
                InvalidatePlaylistSummaryOwnedHashSnapshot();
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
        mutationResult.PlaylistSummaryOwnedHashInvalidated |= receipt.AddedCharts.Count > 0
            || receipt.RemovedCharts.Count > 0;
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
    internal void ReplaceBmsFileLevelByTableEntryLevel(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            return;
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(ReplaceBmsFileLevelByTableEntryLevel)))
        {
            return;
        }
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
                ExecuteLr2SongDbWrite(
                    () => dbGateway.UpdateSongLevels(bmsFiles),
                    stage: "lr2_song_db_playlist_level_update_failed",
                    logReason: nameof(ReplaceBmsFileLevelByTableEntryLevel));
            }
        }
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

    /// <summary>
    /// 指定された BMS ファイル群の現在の状態を song.db にコミット（永続化）します。
    /// </summary>
    public void CommitBMSFiles(IEnumerable<BMSFile> _bmsFiles)
    {
        if (_bmsFiles == null)
        {
            throw new ArgumentNullException("_bmsFiles");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(CommitBMSFiles)))
        {
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                ExecuteLr2SongDbWrite(
                    () => dbGateway.UpsertSongs(_bmsFiles),
                    stage: "lr2_song_db_commit_bms_files_failed",
                    logReason: nameof(CommitBMSFiles));
            }
        }
    }
}
