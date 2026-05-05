using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections;
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
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Logging;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;
using NLog;

namespace BeMusicSeeker.Models;

/// <summary>
/// BMS ファイルのライブラリ管理を担う中核クラスです。
/// LR2 の song.db を読み込み、BMSファイルの走査・登録・インストール・保守（Missing/Garbled/ZeroNote/Duplicate 検出）、
/// プレイリスト (BMSTable) との参照解決、LR2IR キャッシュの取得、およびフォルダの移動・リネーム・削除といった
/// ファイルシステム操作を一手に引き受けます。
/// </summary>
public class BMSLibrary : NotificationObject
{
    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    internal Action<string, string, long, bool, string> StartupBackgroundTaskReporter { get; set; }

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

        internal List<BMSScore> Scores { get; set; } = new List<BMSScore>();

        internal Dictionary<string, BMSScore> ScoresByHash { get; set; } = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// プレイリストサマリー集計で再利用する所持譜面ハッシュ一覧の snapshot です。
    /// BMSFiles 全体から毎回 HashSet を作り直す一時 allocation を避けるために使用します。
    /// </summary>
    internal sealed class PlaylistSummaryOwnedHashSnapshot
    {
        internal int Version { get; set; }

        internal long BuildElapsedMs { get; set; }

        internal HashSet<string> Md5Hashes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> Sha256Hashes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        internal List<BMSPackage> EstimablePackages { get; } = new List<BMSPackage>();

        internal List<BMSPackage> DeferredPackages { get; } = new List<BMSPackage>();

        internal Dictionary<BMSPackage, int> DeferredSourceHealthByPackage { get; } = new Dictionary<BMSPackage, int>();

        internal PendingEstimateSourceBatchSnapshot BatchSourceSnapshot { get; set; }
    }

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.BMSLibrary");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly bool everythingScanLoggingEnabled = installPerformanceLoggingEnabled;

    private static readonly FileMutationOptions targetOnlyFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly);

    private static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

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

    /// <summary>
    /// LR2IR に登録された楽曲の詳細情報（タイトル、アーティスト、BPM、各種クリアレート等）を保持するクラスです。
    /// ribbit.xyz サーバーから取得した JSON をパースして生成されます。
    /// </summary>
    public class IRSongInfo
    {
        private string _md5;

        public string md5
        {
            get
            {
                return _md5;
            }
            set
            {
                if (LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    if (!(_md5 == value))
                    {
                        _md5 = value.ToLowerInvariant();
                    }
                }
                else
                {
                    _md5 = null;
                }
            }
        }

        public string level { get; set; }

        public int lr2_bmsid { get; set; }

        public string title { get; set; }

        public string artist { get; set; }

        public string genre { get; set; }

        public string bpm { get; set; }

        public string keys { get; set; }

        public string rank { get; set; }

        public string tag1 { get; set; }

        public string tag2 { get; set; }

        public string tag3 { get; set; }

        public string tag4 { get; set; }

        public string tag5 { get; set; }

        public string tag6 { get; set; }

        public string tag7 { get; set; }

        public string tag8 { get; set; }

        public string tag9 { get; set; }

        public string tag10 { get; set; }

        public string url { get; set; }

        public string url_diff { get; set; }

        public string comment { get; set; }

        public string youtube_url { get; set; }

        public string niconico_url { get; set; }

        public int? play_count { get; set; }

        public int? clear_count { get; set; }

        public double? clear_rate { get; set; }

        public int? players_count { get; set; }

        public int? players_clear_count { get; set; }

        public double? players_clear_rate { get; set; }

        public int? players_fullcombo_count { get; set; }

        public int? players_hard_count { get; set; }

        public int? players_normal_count { get; set; }

        public int? players_easy_count { get; set; }

        public int? players_failed_count { get; set; }

        public double? players_fullcombo_rate { get; set; }

        public double? players_hard_rate { get; set; }

        public double? players_normal_rate { get; set; }

        public double? players_easy_rate { get; set; }

        public double? players_failed_rate { get; set; }

        public DateTime? timestamp { get; set; }

        public IRSongInfo(dynamic json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }
            try
            {
                lr2_bmsid = int.Parse(json.lr2_bmsid.ToString());
            }
            catch
            {
                throw new ArgumentException(Resources.Error_InvalidJsonObject, "json");
            }
            try
            {
                md5 = json.md5.ToString().Trim();
            }
            catch
            {
                md5 = null;
            }
            try
            {
                level = json.level.ToString().Trim();
            }
            catch
            {
                level = null;
            }
            try
            {
                title = json.title.ToString().Trim();
            }
            catch
            {
                title = null;
            }
            try
            {
                artist = json.artist.ToString().Trim();
            }
            catch
            {
                artist = null;
            }
            try
            {
                genre = json.genre.ToString().Trim();
            }
            catch
            {
                genre = null;
            }
            try
            {
                bpm = json.bpm.ToString().Trim();
            }
            catch
            {
                bpm = null;
            }
            try
            {
                keys = json.keys.ToString().Trim();
            }
            catch
            {
                keys = null;
            }
            try
            {
                rank = json.rank.ToString().Trim();
            }
            catch
            {
                rank = null;
            }
            try
            {
                tag1 = json.tag1.ToString().Trim();
            }
            catch
            {
                tag1 = null;
            }
            try
            {
                tag2 = json.tag2.ToString().Trim();
            }
            catch
            {
                tag2 = null;
            }
            try
            {
                tag3 = json.tag3.ToString().Trim();
            }
            catch
            {
                tag3 = null;
            }
            try
            {
                tag4 = json.tag4.ToString().Trim();
            }
            catch
            {
                tag4 = null;
            }
            try
            {
                tag5 = json.tag5.ToString().Trim();
            }
            catch
            {
                tag5 = null;
            }
            try
            {
                tag6 = json.tag6.ToString().Trim();
            }
            catch
            {
                tag6 = null;
            }
            try
            {
                tag7 = json.tag7.ToString().Trim();
            }
            catch
            {
                tag7 = null;
            }
            try
            {
                tag8 = json.tag8.ToString().Trim();
            }
            catch
            {
                tag8 = null;
            }
            try
            {
                tag9 = json.tag9.ToString().Trim();
            }
            catch
            {
                tag9 = null;
            }
            try
            {
                tag10 = json.tag10.ToString().Trim();
            }
            catch
            {
                tag10 = null;
            }
            try
            {
                url = json.url.ToString().Trim();
            }
            catch
            {
                url = null;
            }
            try
            {
                url_diff = json.url_diff.ToString().Trim();
            }
            catch
            {
                url_diff = null;
            }
            try
            {
                comment = json.comment.ToString().Trim();
            }
            catch
            {
                comment = null;
            }
            try
            {
                youtube_url = json.youtube_url.ToString().Trim();
            }
            catch
            {
                youtube_url = null;
            }
            try
            {
                niconico_url = json.niconico_url.ToString().Trim();
            }
            catch
            {
                niconico_url = null;
            }
            try
            {
                play_count = int.Parse(json.play_count.ToString().Trim());
            }
            catch
            {
                play_count = null;
            }
            try
            {
                clear_count = int.Parse(json.clear_count.ToString().Trim());
            }
            catch
            {
                clear_count = null;
            }
            try
            {
                clear_rate = double.Parse(json.clear_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                clear_rate = null;
            }
            try
            {
                players_count = int.Parse(json.players_count.ToString().Trim());
            }
            catch
            {
                players_count = null;
            }
            try
            {
                players_clear_count = int.Parse(json.players_clear_count.ToString().Trim());
            }
            catch
            {
                players_clear_count = null;
            }
            try
            {
                players_clear_rate = double.Parse(json.players_clear_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_clear_rate = null;
            }
            try
            {
                players_fullcombo_count = int.Parse(json.players_fullcombo_count.ToString().Trim());
            }
            catch
            {
                players_fullcombo_count = null;
            }
            try
            {
                players_hard_count = int.Parse(json.players_hard_count.ToString().Trim());
            }
            catch
            {
                players_hard_count = null;
            }
            try
            {
                players_normal_count = int.Parse(json.players_normal_count.ToString().Trim());
            }
            catch
            {
                players_normal_count = null;
            }
            try
            {
                players_easy_count = int.Parse(json.players_easy_count.ToString().Trim());
            }
            catch
            {
                players_easy_count = null;
            }
            try
            {
                players_failed_count = int.Parse(json.players_failed_count.ToString().Trim());
            }
            catch
            {
                players_failed_count = null;
            }
            try
            {
                players_fullcombo_rate = double.Parse(json.players_fullcombo_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_fullcombo_rate = null;
            }
            try
            {
                players_hard_rate = double.Parse(json.players_hard_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_hard_rate = null;
            }
            try
            {
                players_normal_rate = double.Parse(json.players_normal_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_normal_rate = null;
            }
            try
            {
                players_easy_rate = double.Parse(json.players_easy_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_easy_rate = null;
            }
            try
            {
                players_failed_rate = double.Parse(json.players_failed_rate.ToString().Trim().TrimEnd('%'));
            }
            catch
            {
                players_failed_rate = null;
            }
            try
            {
                timestamp = DateTime.ParseExact(json.timestamp.ToString().Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                timestamp = null;
            }
        }
    }

    private string lr2SongDBPath;

    private string lr2ScoreDBPath;

    private Func<LR2Config> lr2config;

    private BMSDirectoryFileNameHash bmsFolderAllFileList = new BMSDirectoryFileNameHash();

    private DirectoryResourceLookupCache directoryResourceLookupCache = new DirectoryResourceLookupCache();

    private DirectoryRelativePathHashIndex directoryRelativePathHashIndex = new DirectoryRelativePathHashIndex();

    private LibraryResourceIndex libraryResourceIndex = LibraryResourceIndex.CreateFromScanResult(new BmsScanResult());

    private readonly StartupInstallReadinessState startupInstallReadinessState = new StartupInstallReadinessState();

    private readonly int innerWavHealthThreshForNormalBMSFile = 70;

    private readonly double dupRateThreshInOnePkg = 0.9;

    private object lockRankingScores = new object();

    private object lockParentFolderList = new object();

    private bool bmsParentFolderListDirty = true;

    private int bmsParentFolderListDirtyVersion;

    private object lockBMSHashIndex = new object();

    private Dictionary<string, int> bmsHashRefCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> bmsHashIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private bool bmsHashIndexInitialized;

    private readonly object lockInstalledDirectoryIndex = new object();

    private InstalledChartDirectoryIndexSnapshot installedDirectoryIndex = new InstalledChartDirectoryIndexSnapshot();

    private bool installedDirectoryIndexInitialized;

    private readonly object lockInstalledChartKeyIndex = new object();

    private HashSet<string> installedChartKeyIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private bool installedChartKeyIndexInitialized;

    private readonly object lockInstallEstimationMetadataProfileCache = new object();

    private Dictionary<string, InstallEstimationMetadataProfile> installEstimationMetadataProfileCache = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase);

    // Lock acquisition order for facade orchestration:
    // rwlockBMSFilesInitializedAll / rwlockBMSFilesInitializedMin
    // -> rwlockBMSFilesPendingInstall
    // -> rwlockBMSFiles
    // -> rwlockSongDBInstall / rwlockSongDBMaintenance
    // -> rwlockBMSScores
    private ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedAll = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedMin = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesDuplicated = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesPendingInstall = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockLR2IrDir = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSScores = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFiles = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockSongDBInstall = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockSongDBMaintenance = new ReaderWriterLockSlimWrapper();

    private int deferredInstallableMaintenanceRequestedVersion;

    private bool deferredInstallableMaintenanceRunning;

    private int deferredInstallableMaintenanceLastCompletedVersion;

    private long deferredInstallableMaintenanceCriticalElapsedMs;

    private readonly object lockDeferredInstallableMaintenance = new object();

    private int deferredMaintenanceHydrationRequestedVersion;

    private bool deferredMaintenanceHydrationRunning;

    private int deferredMaintenanceHydrationLastCompletedVersion;

    private readonly object lockDeferredMaintenanceHydration = new object();

    private readonly object lockChartInfoBackfill = new object();

    private readonly List<ChartInfoBackfillRequest> chartInfoBackfillRequests = new List<ChartInfoBackfillRequest>();

    private readonly object lockChartInfoHydration = new object();

    private bool chartInfoHydrationRunning;

    private bool chartInfoHydrationPending;

    private string chartInfoHydrationPendingReason;

    private bool chartInfoHydrationPendingQueueBackfill;

    private readonly object lockChartInfoIndex = new object();

    private Dictionary<string, LR2SongDBExtended.chart_info> chartInfoIndexBySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> chartInfoIndexByMd5 = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);

    private readonly object lockScoreSnapshot = new object();

    private ScoreSnapshot scoreSnapshot;

    private int scoreSnapshotVersion;

    private readonly object lockPlaylistSummaryOwnedHashSnapshot = new object();

    private PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot;

    private int playlistSummaryOwnedHashSnapshotVersion;

    private int chartInfoBackfillRequestedVersion;

    private int chartInfoBackfillCompletedVersion;

    private int chartInfoBackfillHydrationBypassUntilVersion;

    private int chartInfoHydrationRequestedVersion;

    private readonly object lockDeferredScoreHydration = new object();

    private int deferredScoreHydrationRequestedVersion;

    private bool deferredScoreHydrationRunning;

    private int deferredScoreHydrationLastCompletedVersion;

    private readonly object lockDeferredRankingRefresh = new object();

    private int deferredRankingRefreshRequestedVersion;

    private bool deferredRankingRefreshRunning;

    private int deferredRankingRefreshLastCompletedVersion;

    private readonly object lockIrScorePrefetch = new object();

    private int irScorePrefetchGeneration;

    private Task<IrScorePrefetchResult> irScorePrefetchTask;

    private int irScorePrefetchLr2Id;

    private string irScorePrefetchScoreDbPath;

    private bool irScorePrefetchEnabled;

    private readonly object lockPendingEstimateQueueStatus = new object();

    private readonly object lockInstallEstimationProgress = new object();

    private readonly SemaphoreSlim pendingEstimateExecutionGate = new SemaphoreSlim(1, 1);

    private readonly PendingInstallEstimateQueueProcessor pendingInstallEstimateQueueProcessor;

    private PendingInstallEstimateQueueStatusSnapshot pendingEstimateQueueStatus = new PendingInstallEstimateQueueStatusSnapshot();

    private InstallEstimationProgressSnapshot installEstimationProgress = new InstallEstimationProgressSnapshot();

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedAll;

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedMin;

    private PropertyChangedEventListener listenerForRwlockBMSFilesDuplicated;

    private PropertyChangedEventListener listenerForRwlockBMSFilesPendingInstall;

    private PropertyChangedEventListener listenerForRwlockBMSFiles;

    private List<BMSFile> _BMSFiles = new List<BMSFile>();

    private List<LR2SongDBExtended.bmson_song> _BmsonSongs = new List<LR2SongDBExtended.bmson_song>();

    private readonly object resourceHealthIndexLock = new object();

    private ResourceHealthIndexSnapshot resourceHealthIndexSnapshot = ResourceHealthIndexSnapshot.Empty;

    private bool resourceHealthIndexInvalidated = true;

    private int resourceHealthIndexVersionSeed;

    private List<DuplicateGroup> _BMSFilesDuplicated;

    private DispatcherCollection<BMSPackage> _BMSPackagesPending = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private DispatcherCollection<BMSPackage> _BMSPackagesInstalled = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private List<string> bmsParentFolderListCache = new List<string>();

    private List<BMSScore> _BMSScores = new List<BMSScore>();

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

    private bool _ChartDigestBackfillRunning;

    private int _ChartDigestBackfillRequestedVersion;

    private int _ChartDigestBackfillCompletedVersion;

    private int _ChartDigestBackfillTotalCount;

    private int _ChartDigestBackfillProcessedCount;

    private string _ChartDigestBackfillCurrentPath = string.Empty;

    private bool _ChartInfoBackfillRunning;

    private int _ChartInfoBackfillRequestedVersion;

    private int _ChartInfoBackfillCompletedVersion;

    private int _ChartInfoBackfillTotalCount;

    private int _ChartInfoBackfillProcessedCount;

    private string _ChartInfoBackfillCurrentPath = string.Empty;

    private bool _ChartInfoHydrationRunning;

    private int _ChartInfoHydrationRequestedVersion;

    private int _ChartInfoHydrationCompletedVersion;

    private int _ChartInfoHydrationTotalCount;

    private int _ChartInfoHydrationAppliedCount;

    private LibraryInitializationProgressStage _LibraryInitializationProgressStage;

    private string _LibraryInitializationProgressScannerLabel = string.Empty;

    private int _LibraryInitializationProgressTotalCount;

    private int _LibraryInitializationProgressProcessedCount;

    private string _LibraryInitializationProgressCurrentPath = string.Empty;

    private int _LibraryDatabaseLoadCompletedVersion;

    private int _LibraryFileEnumerationCompletedVersion;

    private int _LibraryFileDiffCompletedVersion;

    private long lastLibraryInitializationProgressReportTimestamp;

    private readonly object lockLibraryInitializationProgress = new object();

    private int _ChartInfoIndexVersion;

    private bool _ChartInfoIndexHydrated;

    private bool _IsWriteLockHeldInitializeBMSFilesHealthStatus = true;

    private bool _IsWriteLockHeldInitializeBMSFilesEncodingInfo = true;

    private bool _IsWriteLockHeldInitializeBMSFilesZeroNote = true;

    private static Regex lr2IRScoreRegex = new Regex("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static readonly Uri rankingInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking");

    private static readonly Uri rankingDataUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking/");

    private static readonly Uri songInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/info/");

    private const int deferredScoreHydrationChunkSize = 4096;

    private const int deferredScoreHydrationChunkSlowLogThresholdMs = 500;

    private static Regex customTrimStartRegex1 = new Regex("^(\\d+(S|D)P|midi|bms|music)[.:・\\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex customTrimEndRegex1 = new Regex("(^|\\s+)[\\-!\"#$%&`'(,./:;<=>?@\\[^_~|]+$", RegexOptions.Compiled);

    private static Regex customTrimEndRegex2 = new Regex("\\s?[\\[(](\\d+|H|SP?|\\d+key[^\\s]*|hard])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex customDeleteRegex1 = new Regex("(^|[\\[［/(（<＜\\s]+)((.?obj|mov&obj|bga|midi|movie|object|mov|image|bgi|illust(ration|rated)?|差分|LYRICS|イラスト|絵|#include)([.:・\\s]+.*$|$)|(bms|layer|visual):.*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex customMatchRegex1 = new Regex("\\s+[\u3000！“”＃＄％＆‘’（）＊＋，－．／：；＜＝＞？＠［￥］\uff3e\uff3f\uffe3]+\\s+", RegexOptions.Compiled);

    private static Regex customMatchRegex2 = new Regex("〔(.*)〕", RegexOptions.Compiled);

    private static Regex doubleSpacesRegex = new Regex("\\s{2,}", RegexOptions.Compiled);

    private static Regex endKakkoRegex = new Regex("\\s*([-].*[-]|[`'].*[`']|[\"].*[\"]|[～].*[～]|[－].*[－]|[‘’].*[‘’]|[“”].*[“”]|[<].*[>]|[{].*[}]|[\\(].*[\\)]|[\\[].*[\\]]|[＜].*[＞]|[（].*[）]|[「].*[」]|[【].*[】]|[『].*[』]|[［].*[］]|[〈].*[〉]|[《].*[》]|[〔].*[〕]|[｛].*[｝])$", RegexOptions.Compiled);

    private static Regex kakkoInnerRegex = new Regex("(PMS|SP|ANOTHER|HYPER|NORMAL|EMPTY|DP|BGA|4[^\\d]|5[^\\d]|7[^\\d]|9[^\\d]|10[^\\d]|14[^\\d])[^\\w]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// ライブラリが管理する全 BMS ファイルの一覧です。
    /// セッターでは関連するハッシュインデックス・フォルダキャッシュ・重複リストを自動的にリセットします。
    /// </summary>
    public List<BMSFile> BMSFiles
    {
        get
        {
            return _BMSFiles;
        }
        set
        {
            if (_BMSFiles != value)
            {
                _BMSFiles = value;
                InvalidateBMSHashIndex();
                InvalidateInstalledChartKeyIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
                InvalidateDuplicatedCache();
                InvalidateResourceHealthIndex("bmsfiles_changed");
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                    RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
                }).Logging("BMSFiles");
                RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
            }
        }
    }

    public List<BMSFile> BMSFilesUnregistered => BMSFiles.Where((BMSFile f) => string.IsNullOrWhiteSpace(f.parent)).ToList();

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixed => GetBMSFilesNeedToBeFixed(null);

    public List<LR2SongDBExtended.bmson_song> BmsonSongs
    {
        get
        {
            return _BmsonSongs;
        }
        set
        {
            List<LR2SongDBExtended.bmson_song> normalized = value ?? new List<LR2SongDBExtended.bmson_song>();
            if (_BmsonSongs != normalized)
            {
                _BmsonSongs = normalized;
                lock (lockPlaylistSummaryOwnedHashSnapshot)
                {
                    playlistSummaryOwnedHashSnapshot = null;
                }
                InvalidateInstalledChartKeyIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
                InvalidateInstallEstimationMetadataProfileCache();
                InvalidateDuplicatedCache();
                InvalidateResourceHealthIndex("bmsons_changed");
                Task.Run(delegate
                {
                    RaisePropertyChanged("BmsonSongs");
                    RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
                }).Logging("BmsonSongs");
            }
        }
    }

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixedIgnored => GetBMSFilesNeedToBeFixed(null, forceUpdate: false, isInIgnoredList: true);

    /// <summary>
    /// 重複検出済みの BMS ファイルグループ一覧です。重複検出処理の結果が格納されます。
    /// </summary>
    public List<DuplicateGroup> BMSFilesDuplicated
    {
        get
        {
            return _BMSFilesDuplicated;
        }
        set
        {
            if (_BMSFilesDuplicated != value)
            {
                _BMSFilesDuplicated = value;
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFilesDuplicated");
                }).Logging("BMSFilesDuplicated");
            }
        }
    }

    private void InvalidateDuplicatedCache()
    {
        BMSFilesDuplicated = null;
    }

    public IEnumerable<BMSFile> BMSFilesGarbled => GetBMSFilesGarbled(BMSFiles);

    public IEnumerable<BMSFile> BMSFilesGarbledFixed => GetBMSFilesGarbled(BMSFiles, forceUpdate: false, isInFixedList: true);

    public IEnumerable<BMSFile> BMSFilesZeroNote => GetBMSFilesZeroNote(BMSFiles);

    public IEnumerable<BMSFile> BMSFilesChartInfoParseFailed => GetBMSFilesChartInfoParseFailed();

    /// <summary>
    /// インストール待ち（Pending状態）の BMS パッケージのコレクションです。UIスレッドへのディスパッチに対応しています。
    /// </summary>
    public DispatcherCollection<BMSPackage> BMSPackagesPending
    {
        get
        {
            return _BMSPackagesPending;
        }
        set
        {
            if (_BMSPackagesPending != value)
            {
                _BMSPackagesPending = value;
                RaisePropertyChanged("BMSPackagesPending");
            }
        }
    }

    /// <summary>
    /// インストール済みの BMS パッケージのコレクションです。UIスレッドへのディスパッチに対応しています。
    /// </summary>
    public DispatcherCollection<BMSPackage> BMSPackagesInstalled
    {
        get
        {
            return _BMSPackagesInstalled;
        }
        set
        {
            if (_BMSPackagesInstalled != value)
            {
                _BMSPackagesInstalled = value;
                RaisePropertyChanged("BMSPackagesInstalled");
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

    /// <summary>
    /// BMS ファイルのスナップショットから、親フォルダの候補リストを構築します。
    /// カスタムフォルダ出力先ディレクトリ配下は除外されます。
    /// </summary>
    private List<string> BuildBMSParentFolderCandidates(List<BMSFile> bmsFilesSnapshot)
    {
        return parentFolderCacheService.BuildParentFolderCandidates(getBMSDirectories(), bmsFilesSnapshot, CurrentOptionsSnapshot);
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
        List<BMSFile> bmsFilesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = BMSFiles.ToList();
        }
        return parentFolderCacheService.BuildSnapshot(version, bmsFilesSnapshot, getBMSDirectories(), CurrentOptionsSnapshot);
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
            List<string> items = enumerable.Except(bmsParentFolderListCache).ToList();
            List<string> items2 = bmsParentFolderListCache.Except(enumerable).ToList();
            bmsParentFolderListCache = enumerable.ToList();
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
        lock (lockParentFolderList)
        {
            RefreshBMSParentFolderListCacheUnsafe();
            return bmsParentFolderListCache.ToList();
        }
    }

    /// <summary>
    /// 親フォルダ一覧キャッシュを同期的に再構築します（ロック外から呼ばれることを前提としない）。
    /// </summary>
    private void RefreshBMSParentFolderListCacheUnsafe()
    {
        if (!bmsParentFolderListDirty)
        {
            return;
        }
        List<BMSFile> bmsFilesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = BMSFiles.ToList();
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        IEnumerable<string> enumerable = BuildBMSParentFolderCandidates(bmsFilesSnapshot);
        List<string> items = enumerable.Except(bmsParentFolderListCache).ToList();
        List<string> items2 = bmsParentFolderListCache.Except(enumerable).ToList();
        bmsParentFolderListCache = enumerable.ToList();
        bmsParentFolderListDirty = false;
        stopwatch.Stop();
        LogInstallPerformance("parent_folder_cache rebuildMs=" + stopwatch.ElapsedMilliseconds + " added=" + items.Count + " removed=" + items2.Count + " total=" + bmsParentFolderListCache.Count);
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
            return deferredMaintenanceHydrationRunning;
        }
        private set
        {
            if (deferredMaintenanceHydrationRunning != value)
            {
                deferredMaintenanceHydrationRunning = value;
                RaisePropertyChanged(() => MaintenanceHydrationRunning);
            }
        }
    }

    public int MaintenanceHydrationRequestedVersion
    {
        get
        {
            return deferredMaintenanceHydrationRequestedVersion;
        }
        private set
        {
            if (deferredMaintenanceHydrationRequestedVersion != value)
            {
                deferredMaintenanceHydrationRequestedVersion = value;
                RaisePropertyChanged(() => MaintenanceHydrationRequestedVersion);
            }
        }
    }

    public int MaintenanceHydrationCompletedVersion
    {
        get
        {
            return deferredMaintenanceHydrationLastCompletedVersion;
        }
        private set
        {
            if (deferredMaintenanceHydrationLastCompletedVersion != value)
            {
                deferredMaintenanceHydrationLastCompletedVersion = value;
                RaisePropertyChanged(() => MaintenanceHydrationCompletedVersion);
            }
        }
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
                RaisePropertyChanged(() => ChartDigestBackfillRunning);
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
                RaisePropertyChanged(() => ChartDigestBackfillRequestedVersion);
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
                RaisePropertyChanged(() => ChartDigestBackfillCompletedVersion);
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
                RaisePropertyChanged(() => ChartDigestBackfillTotalCount);
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
                RaisePropertyChanged(() => ChartDigestBackfillProcessedCount);
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
            value = value ?? string.Empty;
            if (_ChartDigestBackfillCurrentPath != value)
            {
                _ChartDigestBackfillCurrentPath = value;
                RaisePropertyChanged(() => ChartDigestBackfillCurrentPath);
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
                RaisePropertyChanged(() => ChartInfoBackfillRunning);
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
                RaisePropertyChanged(() => ChartInfoBackfillRequestedVersion);
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
                RaisePropertyChanged(() => ChartInfoBackfillCompletedVersion);
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
                RaisePropertyChanged(() => ChartInfoBackfillTotalCount);
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
                RaisePropertyChanged(() => ChartInfoBackfillProcessedCount);
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
            value = value ?? string.Empty;
            if (_ChartInfoBackfillCurrentPath != value)
            {
                _ChartInfoBackfillCurrentPath = value;
                RaisePropertyChanged(() => ChartInfoBackfillCurrentPath);
            }
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
            value = value ?? string.Empty;
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
            value = value ?? string.Empty;
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
                RaisePropertyChanged(() => ChartInfoHydrationRunning);
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
                RaisePropertyChanged(() => ChartInfoHydrationRequestedVersion);
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
                RaisePropertyChanged(() => ChartInfoHydrationCompletedVersion);
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
                RaisePropertyChanged(() => ChartInfoHydrationTotalCount);
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
                RaisePropertyChanged(() => ChartInfoHydrationAppliedCount);
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

    public bool IsWriteLockHeldBMSFilesPendingInstall
    {
        get
        {
            if (!IsWriteLockHeldInitializeAll && rwlockBMSFilesPendingInstall.LockingWriteCount == 0)
            {
                return rwlockBMSFilesPendingInstall.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldBMSFilesDuplicated
    {
        get
        {
            if (rwlockBMSFilesDuplicated.LockingWriteCount == 0)
            {
                return rwlockBMSFilesDuplicated.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    private bool UseLR2 => CurrentOptionsSnapshot.OperationModeLR2DB;

    public List<string> SearchTargets { get; set; }

    private readonly IFileMutationService fileMutationService;

    private readonly IBmsLibraryDialogService dialogService;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly BmsLibraryDuplicateService duplicateService = new BmsLibraryDuplicateService();

    private readonly BmsLibraryParentFolderCacheService parentFolderCacheService = new BmsLibraryParentFolderCacheService();

    private readonly BmsLibraryPlaylistReferenceService playlistReferenceService = new BmsLibraryPlaylistReferenceService(playlistReferenceApplyChunkSize);

    private readonly BmsLibraryPackageInstallService packageInstallService = new BmsLibraryPackageInstallService();

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new BmsLibraryLibraryFileOperationsService();

    private readonly BmsLibraryIrService irService = new BmsLibraryIrService();

    private readonly BmsLibraryInitializationService initializationService = new BmsLibraryInitializationService();

    private readonly ChartInfoBuildService chartInfoBuildService = new ChartInfoBuildService();

    private readonly IBmsLibraryIrClient irClient = new BmsLibraryIrClient();

    private readonly BmsLibraryMaintenanceService maintenanceService = new BmsLibraryMaintenanceService();

    private readonly BmsLibraryStateApplier stateApplier;

    private BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => BmsLibraryOptionsSnapshot.CreateCurrent();

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
        ResolvedInstalledDirectory,
        EstimatedResult,
        SkippedAsStale
    }

    private sealed class PendingInstallEstimateEvaluationRequest
    {
        public int OrderIndex { get; set; }

        public BMSPackage Package { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public List<BMSFile> AlreadyInstalledFiles { get; set; } = new List<BMSFile>();

        public List<BMSFile> MissingFiles { get; set; } = new List<BMSFile>();

        public BmsInstallationEstimateMode EstimateMode { get; set; }

        public bool WasPendingAtPreparation { get; set; }

        public PendingEstimateSourceBatchPackageState BatchState { get; set; }

        public bool HasMissingFiles => MissingFiles.Count > 0;

        public bool AttemptInstalledResolve => AlreadyInstalledFiles.Count > 0 && MissingFiles.Count > 0;
    }

    private sealed class PendingInstallEstimateEvaluationContext
    {
        public InstalledChartDirectoryIndexSnapshot InstalledDirectoryIndexSnapshot { get; set; } = new InstalledChartDirectoryIndexSnapshot();

        public LibraryResourceIndex ResourceIndexSnapshot { get; set; }

        public DirectoryResourceLookupCache DirectoryLookupCacheSnapshot => ResourceIndexSnapshot?.DirectoryLookupCache;

        public BMSDirectoryFileNameHash FolderAllFileListSnapshot => ResourceIndexSnapshot?.FolderAllFileList;

        public DirectoryRelativePathHashIndex RelativePathHashIndexSnapshot => ResourceIndexSnapshot?.RelativePathHashIndex;

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
    }

    private sealed class PendingInstallEstimateEvaluationResult
    {
        public PendingInstallEstimateEvaluationRequest Request { get; set; }

        public PendingInstallEstimateEvaluationOutcomeKind OutcomeKind { get; set; }

        public string ResolvedDirectory { get; set; }

        public InstalledDirectoryLookupResult InstalledResolution { get; set; }

        public InstallEstimationEvaluationData EstimationData { get; set; }
    }

    private sealed class ChartInfoBackfillRequest
    {
        private ChartInfoBackfillRequest(string reason)
        {
            Reason = reason ?? "unknown";
        }

        public string Reason { get; }

        public static ChartInfoBackfillRequest Full(string reason)
        {
            return new ChartInfoBackfillRequest(reason);
        }
    }

    private sealed class ChartInfoHydrationResult
    {
        public int TotalRows { get; set; }

        public int ChartInfoRows { get; set; }

        public int AppliedBmsCount { get; set; }

        public int AppliedBmsonCount { get; set; }

        public int OwnerApplyUpdatedCount { get; set; }

        public int OwnerApplySkippedCount { get; set; }

        public int OwnerApplySilentCount { get; set; }

        public int OwnerApplyNotifiedCount { get; set; }

        public int OwnerCount { get; set; }

        public int CurrentChartInfoOwnerCount { get; set; }

        public int CurrentParseFailureOwnerCount { get; set; }

        public int BackfillCandidateOwnerCount { get; set; }

        public long LoadMs { get; set; }

        public long ApplyMs { get; set; }

        public long DbLoadMs { get; set; }

        public long DbMaterializeMs { get; set; }

        public bool DbReadOnly { get; set; }

        public long DbLockWaitMs { get; set; }

        public int ParseFailureRows { get; set; }

        public long IndexBuildMs { get; set; }

        public long OwnerApplyMs { get; set; }

        public long TotalMs { get; set; }

        public bool Succeeded { get; set; }
    }

    private sealed class ChartInfoIndexUpdateResult
    {
        public int InputRows { get; set; }

        public int BySha256Count { get; set; }

        public int ByMd5Count { get; set; }

        public int Version { get; set; }

        public bool HydrationChanged { get; set; }
    }

    /// <summary>
    /// LR2 の song.db を読み込み、このセッションで利用する BMS ライブラリを初期化します。
    /// </summary>
    /// <param name="_lr2SongDB">必須の LR2 song.db パスです。</param>
    /// <param name="getLR2Config">必要時に LR2 設定を取得するコールバックです。</param>
    /// <param name="_lr2ScoreDB">任意の LR2 score.db パスです。</param>
    /// <exception cref="ArgumentNullException">song.db パスが null の場合に送出されます。</exception>
    /// <exception cref="ArgumentException">指定された DB ファイルが存在しない場合に送出されます。</exception>
    public BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config = null, string _lr2ScoreDB = null)
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, null)
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
        : this(_lr2SongDB, getLR2Config, _lr2ScoreDB, fileMutationService, null)
    {
    }

    internal BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config, string _lr2ScoreDB, IFileMutationService fileMutationService, IBmsLibraryDialogService dialogService)
    {
        if (_lr2SongDB == null)
        {
            throw new ArgumentNullException("_LR2SongDB");
        }
        if (!File.Exists(_lr2SongDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, _lr2SongDB), "_LR2SongDB");
        }
        if (_lr2ScoreDB != null && !File.Exists(_lr2ScoreDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2ScoreDBNotFound, _lr2ScoreDB), "_lr2ScoreDB");
        }
        lr2SongDBPath = _lr2SongDB;
        lr2ScoreDBPath = _lr2ScoreDB;
        this.fileMutationService = fileMutationService ?? new ResilientFileMutationService();
        this.dialogService = dialogService ?? new BmsLibraryDialogService();
        dbGateway = new BmsLibraryDbGateway(lr2SongDBPath, lr2ScoreDBPath);
        stateApplier = new BmsLibraryStateApplier(
            dbGateway,
            () => BMSFiles,
            files => BMSFiles = files,
            () => BmsonSongs,
            songs => BmsonSongs = songs,
            () => BMSPackagesPending,
            pendingPackages => BMSPackagesPending = pendingPackages,
            () => BMSPackagesInstalled,
            installedPackages => BMSPackagesInstalled = installedPackages,
            InvalidateBMSHashIndex,
            InvalidateInstalledDirectoryIndex,
            InvalidateBMSParentFolderListCache,
            InvalidateDuplicatedCache,
            () => RaisePropertyChanged(() => BMSFiles),
            () => RaisePropertyChanged(() => BMSPackagesInstalled));
        pendingInstallEstimateQueueProcessor = new PendingInstallEstimateQueueProcessor(ProcessPendingInstallEstimateBatch, UpdatePendingEstimateQueueStatus, HandlePendingEstimateBatchException);
        lr2config = ((getLR2Config != null) ? getLR2Config : ((Func<LR2Config>)(() => (LR2Config)null)));
        using (LR2SongDBExtended lR2SongDBExtended = dbGateway.OpenSongDb())
        {
            lR2SongDBExtended.CreateTable<LR2SongDB.song>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.install>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.maintenance>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.ir_score>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.ir_data>();
            lR2SongDBExtended.CreateIndex("song_idx_folder", SQLiteTable<LR2SongDB.song>.GetTableName(), new string[1] { SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.folder) });
            lR2SongDBExtended.CreateIndex("ir_data_idx", SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName(), new string[1] { SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id) });
            BmsLibraryDbGateway.EnsureChartInfoSchema(lR2SongDBExtended);
        }
        listenerForRwlockBMSFilesInitializedAll = new PropertyChangedEventListener(rwlockBMSFilesInitializedAll);
        listenerForRwlockBMSFilesInitializedMin = new PropertyChangedEventListener(rwlockBMSFilesInitializedMin);
        listenerForRwlockBMSFilesDuplicated = new PropertyChangedEventListener(rwlockBMSFilesDuplicated);
        listenerForRwlockBMSFilesPendingInstall = new PropertyChangedEventListener(rwlockBMSFilesPendingInstall);
        listenerForRwlockBMSFiles = new PropertyChangedEventListener(rwlockBMSFiles);
        listenerForRwlockBMSFilesInitializedAll.RegisterHandler(() => rwlockBMSFilesInitializedAll.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeAll);
        });
        listenerForRwlockBMSFilesInitializedAll.RegisterHandler(() => rwlockBMSFilesInitializedAll.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSFilesPendingInstall);
        });
        listenerForRwlockBMSFilesInitializedMin.RegisterHandler(() => rwlockBMSFilesInitializedMin.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeMin);
        });
        listenerForRwlockBMSFilesInitializedMin.RegisterHandler(() => rwlockBMSFilesInitializedMin.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
        listenerForRwlockBMSFilesDuplicated.RegisterHandler(() => rwlockBMSFilesDuplicated.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSFilesDuplicated);
        });
        listenerForRwlockBMSFilesPendingInstall.RegisterHandler(() => rwlockBMSFilesPendingInstall.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSFilesPendingInstall);
        });
        listenerForRwlockBMSFiles.RegisterHandler(() => rwlockBMSFiles.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
    }

    /// <summary>
    /// 現在の BMS スコア情報 (LR2 score.db 由来) のコピーを取得します。
    /// </summary>
    public List<BMSScore> GetBMSScores()
    {
        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
        if (snapshot != null)
        {
            return snapshot.Scores;
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
        Stopwatch stopwatch = Stopwatch.StartNew();
        string source = ToPendingEstimateBatchSourceLogValue(request.Source);
        int lowConfidenceCount = 0;
        int completed = 0;
        int maxParallelPackages = ResolvePendingInstallEstimateParallelPackageDegree();
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + maxParallelPackages + " display=" + (request.DisplayName ?? string.Empty));
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, 0, request.DisplayName ?? string.Empty);
                PendingInstallEstimateEvaluationContext evaluationContext = CreatePendingInstallEstimateEvaluationContext();
                List<PendingInstallEstimateEvaluationRequest> evaluationRequests = PreparePendingInstallEstimateEvaluationRequests(request);
                ProcessPendingInstallEstimateEvaluationPipeline(request, source, token, evaluationContext, evaluationRequests, maxParallelPackages, ref completed, ref lowConfidenceCount);
                if (!token.IsCancellationRequested && request.RegroupEligibleSourceDirectories.Length > 0)
                {
                    using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                    {
                        using (rwlockBMSFilesPendingInstall.GetWriterGuard())
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
            LogInstallPerformance("pending_estimate_batch done source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + maxParallelPackages + " estimated=" + completed + " completed=" + (completed + request.DeferredPackageCount) + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " lowConfidence=" + lowConfidenceCount);
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
                    InstalledDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe(),
                    ResourceIndexSnapshot = libraryResourceIndex,
                    OptionsSnapshot = CurrentOptionsSnapshot
                };
            }
        }
    }

    private List<PendingInstallEstimateEvaluationRequest> PreparePendingInstallEstimateEvaluationRequests(PendingInstallEstimateBatchRequest request)
    {
        List<PendingInstallEstimateEvaluationRequest> requests = new List<PendingInstallEstimateEvaluationRequest>();
        if (request == null)
        {
            return requests;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    int orderIndex = 0;
                    if (request.BatchSourceSnapshot?.PackageStates.Count > 0)
                    {
                        foreach (PendingEstimateSourceBatchPackageState state in request.BatchSourceSnapshot.PackageStates.Where((PendingEstimateSourceBatchPackageState state) => state?.Package != null))
                        {
                            requests.Add(new PendingInstallEstimateEvaluationRequest
                            {
                                OrderIndex = orderIndex++,
                                Package = state.Package,
                                DisplayName = state.DisplayName,
                                AlreadyInstalledFiles = state.AlreadyInstalledFiles.ToList(),
                                MissingFiles = state.MissingFiles.ToList(),
                                EstimateMode = state.EstimateMode,
                                WasPendingAtPreparation = BMSPackagesPending.Contains(state.Package),
                                BatchState = state
                            });
                        }
                        return requests;
                    }

                    foreach (BMSPackage package in request.Packages)
                    {
                        List<BMSFile> packageFiles = (package?.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                        List<BMSFile> alreadyInstalledFiles = packageFiles.Where(ContainsInstalledChartUnsafe).ToList();
                        List<BMSFile> missingFiles = packageFiles.Where((BMSFile file) => !ContainsInstalledChartUnsafe(file)).ToList();
                        requests.Add(new PendingInstallEstimateEvaluationRequest
                        {
                            OrderIndex = orderIndex++,
                            Package = package,
                            DisplayName = PendingInstallEstimateBatchRequest.GetDisplayName(package?.path),
                            AlreadyInstalledFiles = alreadyInstalledFiles,
                            MissingFiles = missingFiles,
                            EstimateMode = BmsInstallationEstimateMode.Normal,
                            WasPendingAtPreparation = package != null && BMSPackagesPending.Contains(package),
                            BatchState = null
                        });
                    }
                }
            }
        }
        return requests;
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

    private int ResolvePendingInstallEstimateParallelPackageDegree()
    {
        return ResolvePendingInstallEstimateParallelPackageDegree(Settings.Default.PendingInstallEstimateMaxParallelPackages);
    }

    private void ProcessPendingInstallEstimateEvaluationPipeline(PendingInstallEstimateBatchRequest request, string source, CancellationToken token, PendingInstallEstimateEvaluationContext evaluationContext, List<PendingInstallEstimateEvaluationRequest> evaluationRequests, int maxParallelPackages, ref int completed, ref int lowConfidenceCount)
    {
        List<(PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task)> inFlight = new List<(PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task)>();
        int nextDispatchIndex = 0;
        int nextApplyIndex = 0;
        while (nextApplyIndex < evaluationRequests.Count)
        {
            while (!token.IsCancellationRequested && nextDispatchIndex < evaluationRequests.Count && inFlight.Count < maxParallelPackages)
            {
                PendingInstallEstimateEvaluationRequest dispatchRequest = evaluationRequests[nextDispatchIndex];
                SetPendingInstallEstimateSearchingState(dispatchRequest, isSearching: true);
                Task<PendingInstallEstimateEvaluationResult> evaluateTask = Task.Run(() => EvaluatePendingInstallEstimateRequest(dispatchRequest, evaluationContext, token), token);
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
            ApplyPendingInstallEstimateEvaluationResult(request, source, evaluationResult, maxParallelPackages, ref completed, ref lowConfidenceCount);
            nextApplyIndex++;
        }

        foreach ((PendingInstallEstimateEvaluationRequest Request, Task<PendingInstallEstimateEvaluationResult> Task) item in inFlight)
        {
            PendingInstallEstimateEvaluationResult evaluationResult = item.Task.GetAwaiter().GetResult();
            ApplyPendingInstallEstimateEvaluationResult(request, source, evaluationResult, maxParallelPackages, ref completed, ref lowConfidenceCount);
        }
    }

    private PendingInstallEstimateEvaluationResult EvaluatePendingInstallEstimateRequest(PendingInstallEstimateEvaluationRequest request, PendingInstallEstimateEvaluationContext evaluationContext, CancellationToken token)
    {
        PendingInstallEstimateEvaluationResult result = new PendingInstallEstimateEvaluationResult
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

        if (request.AttemptInstalledResolve)
        {
            result.InstalledResolution = request.BatchState?.PreparationInstalledResolution?.Success == true
                ? request.BatchState.PreparationInstalledResolution
                : EvaluateInstalledDestinationFromPackage(request.Package, request.MissingFiles, evaluationContext);
            if (result.InstalledResolution.Success)
            {
                result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.ResolvedInstalledDirectory;
                result.ResolvedDirectory = result.InstalledResolution.InstallDirectory;
                return result;
            }
            result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.NoOp;
            return result;
        }

        result.OutcomeKind = PendingInstallEstimateEvaluationOutcomeKind.EstimatedResult;
        result.EstimationData = EvaluateInstallEstimation(
            request.Package,
            request.MissingFiles,
            asParallel: false,
            request.EstimateMode,
            evaluationContext.OptionsSnapshot,
            useThreadSafeResolvers: true,
            useSharedLazyHashMetrics: true,
            evaluationContext.FolderAllFileListSnapshot,
            evaluationContext.DirectoryLookupCacheSnapshot,
            evaluationContext.RelativePathHashIndexSnapshot,
            request.BatchState);
        return result;
    }

    private InstalledDirectoryLookupResult EvaluateInstalledDestinationFromPackage(BMSPackage package, List<BMSFile> missingFiles, PendingInstallEstimateEvaluationContext evaluationContext)
    {
        if (package == null || missingFiles == null || missingFiles.Count == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.NoInstalledDirectoryMatch
            };
        }
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService(evaluationContext?.OptionsSnapshot);
        return installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingFiles, evaluationContext?.InstalledDirectoryIndexSnapshot ?? new InstalledChartDirectoryIndexSnapshot(), evaluationContext?.FolderAllFileListSnapshot ?? bmsFolderAllFileList);
    }

    private void ApplyPendingInstallEstimateEvaluationResult(PendingInstallEstimateBatchRequest batchRequest, string source, PendingInstallEstimateEvaluationResult evaluationResult, int packageDegree, ref int completed, ref int lowConfidenceCount)
    {
        PendingInstallEstimateEvaluationRequest request = evaluationResult?.Request;
        string currentDisplayName = request?.DisplayName ?? string.Empty;
        SetInstallEstimationProgress(ToInstallEstimationProgressSource(batchRequest.Source), batchRequest.PackageCount, completed, currentDisplayName);
        bool isLowConfidence = false;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
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
        BMSPackage package = request.Package;
        if (package == null || !BMSPackagesPending.Contains(package))
        {
            return false;
        }

        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
        ApplyPackageMixedInstallWarnings(request.AlreadyInstalledFiles.Where(ContainsInstalledChartUnsafe));

        List<BMSFile> currentMissingFiles = request.MissingFiles
            .Where((BMSFile file) => file != null && !ContainsInstalledChartUnsafe(file))
            .ToList();
        if (currentMissingFiles.Count == 0)
        {
            return false;
        }
        if (request.MissingFiles.Any((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.instl_dst)))
        {
            return false;
        }

        if (request.AttemptInstalledResolve)
        {
            LogMixedPackageResolution(evaluationResult.InstalledResolution, package.path, request.MissingFiles.Count);
        }

        switch (evaluationResult.OutcomeKind)
        {
            case PendingInstallEstimateEvaluationOutcomeKind.ResolvedInstalledDirectory:
                if (!string.IsNullOrWhiteSpace(evaluationResult.ResolvedDirectory))
                {
                    ApplyResolvedInstallDestinationToFiles(currentMissingFiles, evaluationResult.ResolvedDirectory);
                }
                return currentMissingFiles.Any((BMSFile file) => file != null && string.IsNullOrWhiteSpace(file.instl_dst) && !string.IsNullOrWhiteSpace(file.InstallDestinationTitle));
            case PendingInstallEstimateEvaluationOutcomeKind.EstimatedResult:
            {
                LogInstallEstimationEvaluation(evaluationResult.EstimationData);
                ApplyInstallEstimationResultToFiles(currentMissingFiles, evaluationResult.EstimationData?.Result);
                return currentMissingFiles.Any((BMSFile file) => file != null && string.IsNullOrWhiteSpace(file.instl_dst) && !string.IsNullOrWhiteSpace(file.InstallDestinationTitle));
            }
            case PendingInstallEstimateEvaluationOutcomeKind.NoOp:
                if (request.AttemptInstalledResolve)
                {
                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, currentMissingFiles);
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
            InstalledDirectoryResolveReason.NoInstalledDirectoryMatch => "no_installed_dir_match",
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
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
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
        foreach (BMSFile file in request.MissingFiles.Where((BMSFile file) => file != null))
        {
            if (isSearching)
            {
                file.status |= BMSFile.BMSFileStatus.SEARCHING;
            }
            else
            {
                file.status &= ~BMSFile.BMSFileStatus.SEARCHING;
            }
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
            _ => "unknown"
        };
    }

    private static InstallEstimationProgressSource ToInstallEstimationProgressSource(PendingInstallEstimateBatchSource source)
    {
        return source switch
        {
            PendingInstallEstimateBatchSource.StartupRestore => InstallEstimationProgressSource.StartupRestore,
            PendingInstallEstimateBatchSource.AutoInstall => InstallEstimationProgressSource.AutoInstall,
            _ => InstallEstimationProgressSource.None
        };
    }

    private void LogPendingEstimateSkippedPackage(string source, BMSPackage package, int sourceHealth)
    {
        PendingEstimateDeferredReason reason = package?.DeferredEstimateReason ?? PendingEstimateDeferredReason.None;
        string reasonLog = reason switch
        {
            PendingEstimateDeferredReason.HealthySourceBaseline => "healthy_source_baseline",
            PendingEstimateDeferredReason.InstalledDestinationResolveFailed => "installed_destination_resolve_failed",
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

    private BackgroundPendingEstimatePreparationResult PrepareBackgroundPendingEstimatePackagesUnsafe(IEnumerable<BMSPackage> packages, PendingInstallEstimateBatchSource source)
    {
        BackgroundPendingEstimatePreparationResult result = new BackgroundPendingEstimatePreparationResult();
        List<BMSPackage> packageList = (packages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage package) => package != null).Distinct().ToList();
        if (packageList.Count == 0)
        {
            return result;
        }

        string sourceLogValue = ToPendingEstimateBatchSourceLogValue(source);
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService(options);
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();
        PendingEstimateSourceBatchSnapshot candidateSnapshot = BuildPendingEstimateSourceBatchSnapshotUnsafe(packageList, installEstimationService, installedDirectoryIndexSnapshot, sourceLogValue, options.UseEverythingForPendingPackageSourceScan);
        PendingEstimateSourceBatchSnapshot estimableSnapshot = new PendingEstimateSourceBatchSnapshot
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

        Stopwatch prefilterStopwatch = Stopwatch.StartNew();
        foreach (PendingEstimateSourceBatchPackageState state in candidateSnapshot.PackageStates)
        {
            if (HasInstalledDestinationResolveFailed(state))
            {
                state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
                ApplyPackageMixedInstallWarnings(state.AlreadyInstalledFiles.Where(ContainsInstalledChartUnsafe));
                ApplyInstalledDestinationResolveFailedToFilesUnsafe(state.MissingFiles);
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
                ClearInstallEstimationStateUnsafe(state.PackageFiles);
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
            + " elapsedMs=" + candidateSnapshot.ElapsedMs);
        LogInstallPerformance("pending_estimate_source_batch_prefilter source=" + sourceLogValue
            + " packages=" + packageList.Count
            + " estimable=" + result.EstimablePackages.Count
            + " deferred=" + result.DeferredPackages.Count
            + " elapsedMs=" + estimableSnapshot.PrefilterMs);

        return result;
    }

    private PendingEstimateSourceBatchSnapshot BuildPendingEstimateSourceBatchSnapshotUnsafe(List<BMSPackage> packageList, BmsLibraryInstallEstimationService installEstimationService, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, string sourceLogValue, bool useEverythingForPendingPackageSourceScan)
    {
        PendingEstimateSourceBatchSnapshot snapshot = new PendingEstimateSourceBatchSnapshot();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Dictionary<string, SourceSurfaceEntryView> sourceSurfaceByRoot = new Dictionary<string, SourceSurfaceEntryView>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> rootsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BMSPackage package in packageList ?? Enumerable.Empty<BMSPackage>())
        {
            List<BMSFile> packageFiles = (package?.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            List<BMSFile> alreadyInstalledFiles = packageFiles.Where(ContainsInstalledChartUnsafe).ToList();
            List<BMSFile> missingFiles = packageFiles.Where((BMSFile file) => !ContainsInstalledChartUnsafe(file)).ToList();
            PendingEstimateSourceBatchPackageState state = new PendingEstimateSourceBatchPackageState
            {
                Package = package,
                DisplayName = PendingInstallEstimateBatchRequest.GetDisplayName(package?.path),
                SourceDirectory = ResolvePendingEstimateSourceDirectory(package?.path),
                PackageFiles = packageFiles,
                AlreadyInstalledFiles = alreadyInstalledFiles,
                MissingFiles = missingFiles,
                ChartResources = ChartResourceSnapshot.CreateAggregate(missingFiles),
                EstimateMode = BmsInstallationEstimateMode.Normal
            };
            if (state.AttemptInstalledResolve)
            {
                state.PreparationInstalledResolution = installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingFiles, installedDirectoryIndexSnapshot, bmsFolderAllFileList);
            }

            if (state.HasMissingFiles && !HasInstalledDestinationResolveFailed(state) && !string.IsNullOrWhiteSpace(state.SourceDirectory) && Directory.Exists(state.SourceDirectory))
            {
                rootsToScan.Add(state.SourceDirectory);
            }

            snapshot.PackageStates.Add(state);
        }

        TryPopulatePendingEstimateSourceSurfaceViewsUnsafe(rootsToScan, sourceSurfaceByRoot, snapshot, useEverythingForPendingPackageSourceScan);

        foreach (PendingEstimateSourceBatchPackageState state in snapshot.PackageStates)
        {
            if (HasInstalledDestinationResolveFailed(state))
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
            snapshot.ScanBackend = sourceSurfaceByRoot.Values.Select((SourceSurfaceEntryView view) => view?.ScanBackend).FirstOrDefault((string backend) => !string.IsNullOrWhiteSpace(backend))
                ?? "fast";
        }

        return snapshot;
    }

    private void TryPopulatePendingEstimateSourceSurfaceViewsUnsafe(IEnumerable<string> roots, IDictionary<string, SourceSurfaceEntryView> sourceSurfaceByRoot, PendingEstimateSourceBatchSnapshot snapshot, bool useEverythingForPendingPackageSourceScan)
    {
        if (sourceSurfaceByRoot == null || snapshot == null)
        {
            return;
        }

        List<string> distinctRoots = (roots ?? Enumerable.Empty<string>())
            .Where((string root) => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        snapshot.RootCount = distinctRoots.Count;
        if (distinctRoots.Count == 0)
        {
            return;
        }

        if (!useEverythingForPendingPackageSourceScan)
        {
            snapshot.ScanBackend = "fast";
            foreach (string root in distinctRoots)
            {
                PackageInstallSurfaceSnapshot fastSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(root, useEverythingForPendingPackageSourceScan: false);
                SourceSurfaceEntryView view = CreateSourceSurfaceEntryView(fastSnapshot, root);
                sourceSurfaceByRoot[root] = view;
                snapshot.TrackedFileCount += view.TrackedFileCount;
                snapshot.ResourceFileCount += view.ResourceFileCount;
            }
            return;
        }

        bool batchScanSucceeded = true;
        List<List<string>> chunks = distinctRoots
            .Select((string root, int index) => new { Root = root, Index = index })
            .GroupBy((item) => item.Index / PendingEstimateSourceBatchMaxRootsPerChunk)
            .Select((group) => group.Select((item) => item.Root).ToList())
            .ToList();
        snapshot.ChunkCount = chunks.Count;

        foreach (List<string> chunk in chunks)
        {
            if (!EverythingNative.TryScanSourceRoots(chunk, out EverythingNative.BridgeSourceRootScanResult scanResult, out _)
                || scanResult == null)
            {
                batchScanSucceeded = false;
                break;
            }

            snapshot.NativeBridgeMs += scanResult.NativeBridgeMs;
            snapshot.ManagedDecodeMs += scanResult.ManagedDecodeMs;
            snapshot.ManagedMaterializeMs += scanResult.ManagedMaterializeMs;
            snapshot.ScanBackend = "everything_bridge_source_surface_batch";
            foreach (string root in chunk)
            {
                if (!scanResult.TryGetEntry(root, out EverythingNative.BridgeSourceRootEntryResult entryResult) || entryResult == null)
                {
                    continue;
                }

                SourceSurfaceEntryView surfaceEntry = new SourceSurfaceEntryView
                {
                    SourceDirectory = entryResult.RootPath ?? root,
                    ResourceEntry = entryResult.ResourceEntry?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
                    ChartFileCount = entryResult.ChartFileCount,
                    ResourceFileCount = entryResult.ResourceFileCount,
                    TrackedFileCount = entryResult.TrackedFileCount,
                    ScanMs = 0L,
                    HashMaterializeMs = 0L,
                    ScanBackend = "everything_bridge_source_surface_batch"
                };
                sourceSurfaceByRoot[surfaceEntry.SourceDirectory] = surfaceEntry;
                snapshot.TrackedFileCount += surfaceEntry.TrackedFileCount;
                snapshot.ResourceFileCount += surfaceEntry.ResourceFileCount;
            }
        }

        if (batchScanSucceeded)
        {
            return;
        }

        snapshot.ScanBackend = "fast";
        snapshot.NativeBridgeMs = 0L;
        snapshot.ManagedDecodeMs = 0L;
        snapshot.ManagedMaterializeMs = 0L;
        snapshot.TrackedFileCount = 0;
        snapshot.ResourceFileCount = 0;
        sourceSurfaceByRoot.Clear();

        foreach (string root in distinctRoots)
        {
            PackageInstallSurfaceSnapshot fallbackSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(root, useEverythingForPendingPackageSourceScan: false);
            SourceSurfaceEntryView view = CreateSourceSurfaceEntryView(fallbackSnapshot, root);
            sourceSurfaceByRoot[root] = view;
            snapshot.TrackedFileCount += view.TrackedFileCount;
            snapshot.ResourceFileCount += view.ResourceFileCount;
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
            ScanBackend = snapshot?.ScanBackend ?? "fast"
        };
    }

    private static bool HasInstalledDestinationResolveFailed(PendingEstimateSourceBatchPackageState state)
    {
        return state?.AttemptInstalledResolve == true
            && state.PreparationInstalledResolution != null
            && !state.PreparationInstalledResolution.Success;
    }

    private bool ShouldDeferPendingEstimateBatchPackageUnsafe(PendingEstimateSourceBatchPackageState state, BmsLibraryInstallEstimationService installEstimationService, out int sourcePrimaryHealth)
    {
        sourcePrimaryHealth = 0;
        if (state?.Package == null || installEstimationService == null || !Directory.Exists(state.Package.path))
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
            string normalizedPath = Path.GetFullPath(packagePath);
            if (Directory.Exists(normalizedPath))
            {
                return normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ClearInstallEstimationStateUnsafe(IEnumerable<BMSFile> bmsFiles)
    {
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.instl_dst = null;
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        }
    }

    private void ClearDeferredEstimateReasonForFilesUnsafe(IEnumerable<BMSFile> bmsFiles)
    {
        HashSet<BMSFile> fileSet = new HashSet<BMSFile>((bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null));
        if (fileSet.Count == 0)
        {
            return;
        }

        IEnumerable<BMSPackage> pendingPackages = BMSPackagesPending ?? Enumerable.Empty<BMSPackage>();
        foreach (BMSPackage pendingPackage in pendingPackages.Where((BMSPackage package) => package != null))
        {
            if ((pendingPackage.BMSFiles ?? new List<BMSFile>()).Any((BMSFile file) => file != null && fileSet.Contains(file)))
            {
                pendingPackage.DeferredEstimateReason = PendingEstimateDeferredReason.None;
            }
        }
    }

    /// <summary>
    /// 現在の BMSScores から score snapshot を再構築して公開します。
    /// </summary>
    /// <param name="reason">ログ出力用の更新理由。</param>
    private void RefreshScoreSnapshotFromCurrentScores(string reason)
    {
        List<BMSScore> scoresSnapshot;
        using (rwlockBMSScores.GetReaderGuard())
        {
            scoresSnapshot = (BMSScores ?? new List<BMSScore>()).Where((BMSScore score) => score != null).ToList();
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        Dictionary<string, BMSScore> scoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
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
                ScoresByHash = scoresByHash
            };
        }
        ScoreSnapshotReady = lr2ScoreDBPath != null;
        ScoreSnapshotVersion = version;
        LogInstallPerformance("score_snapshot_load completed reason=" + (reason ?? "unknown") + " version=" + version + " count=" + scoresSnapshot.Count + " buildMs=" + stopwatch.ElapsedMilliseconds);
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
        LibraryDatabaseLoadCompletedVersion = LibraryDatabaseLoadCompletedVersion + 1;
    }

    private void CompleteLibraryFileEnumerationProgress()
    {
        LibraryFileEnumerationCompletedVersion = LibraryFileEnumerationCompletedVersion + 1;
    }

    private void CompleteLibraryFileDiffProgress()
    {
        LibraryFileDiffCompletedVersion = LibraryFileDiffCompletedVersion + 1;
    }

    /// <summary>
    /// BMS ファイル走査のプリフェッチ結果（走査結果と所要時間）を保持するクラスです。
    /// </summary>
    private sealed class BmsScanPrefetchInfo
    {
        public BmsScanExecutionResult ScanResult { get; set; }

        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// native bridge を優先し、Everything API が使えない場合は managed scan で BMS ファイルを走査します。
    /// </summary>
    private BmsScanExecutionResult ExecuteBmsScanWithManagedFallback(List<string> bmsDirectories, Action<string> reportScanner = null)
    {
        IBmsFileScanner scanner = new EverythingFileScanner();
        reportScanner?.Invoke("Native");
        BmsScanExecutionResult scanResult = scanner.Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        if (scanResult.Success && scanResult.Result != null)
        {
            return scanResult;
        }

        string nativeFailureReason = scanResult?.ErrorReason ?? "unknown";
        if (IsNativeBridgeContractFailure(nativeFailureReason))
        {
            LogEverythingScan("BMS native file scan failed reason=" + nativeFailureReason);
            throw new InvalidOperationException("BMS native file scan failed: " + nativeFailureReason);
        }

        LogEverythingScan("BMS native file scan unavailable reason=" + nativeFailureReason + " fallback=managed");
        reportScanner?.Invoke("Fallback");
        BmsScanExecutionResult fallbackResult = new FastDirectoryFileScanner().Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        if (!fallbackResult.Success || fallbackResult.Result == null)
        {
            string fallbackFailureReason = fallbackResult?.ErrorReason ?? "unknown";
            LogEverythingScan("BMS fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
            throw new InvalidOperationException("BMS fallback file scan failed: " + fallbackFailureReason + " (native: " + nativeFailureReason + ")");
        }
        LogEverythingScan("BMS fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
        return fallbackResult;
    }

    private static bool IsNativeBridgeContractFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }
        return reason.StartsWith("bridge_contract_mismatch:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_header_size_mismatch:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_fixed_scan_export_missing", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_dll_not_found:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_dll_load_failed:", StringComparison.OrdinalIgnoreCase);
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
        bool songTblLoad = !isScoreOnly;
        bool songTblFileCheck = mode == LibraryInitializeMode.FullReinitialize || (isStartup && !options.SkipInitFileCheck);
        bool setMaintenanceInfo = !isScoreOnly;
        bool flag = !isScoreOnly;
        Task<BmsScanPrefetchInfo> bmsScanPrefetchTask = null;
        if (songTblFileCheck)
        {
            List<string> prefetchDirectories = getBMSDirectories();
            if (prefetchDirectories.Count > 0)
            {
                bmsScanPrefetchTask = Task.Run(delegate
                {
                    Stopwatch stopwatchPrefetch = Stopwatch.StartNew();
                    BmsScanExecutionResult scanResult = ExecuteBmsScanWithManagedFallback(
                        prefetchDirectories,
                        scannerLabel => ReportLibraryInitializationProgress(
                            LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            force: true));
                    stopwatchPrefetch.Stop();
                    return new BmsScanPrefetchInfo
                    {
                        ScanResult = scanResult,
                        ElapsedMs = stopwatchPrefetch.ElapsedMilliseconds
                    };
                });
            }
        }
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("hazimari: " + GC.GetTotalMemory(forceFullCollection: false));
        DateTime now;
        InitializationExecutionResult initializeResult;
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
                        _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, bmsScanPrefetchInfo: null, trackLibraryDatabaseProgress: true);
                        if (songTblLoad)
                        {
                            startupInstallReadinessState.MarkCatalogLoaded();
                        }
                    }
                },
                delegate
                {
                    BmsScanPrefetchInfo bmsScanPrefetchInfo = null;
                    if (songTblFileCheck && bmsScanPrefetchTask != null)
                    {
                        try
                        {
                            bmsScanPrefetchInfo = bmsScanPrefetchTask.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            LogEverythingScan("bms_scan_prefetch failed message=" + ex.Message);
                            bmsScanPrefetchInfo = null;
                        }
                    }
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck, setMainteInfo: false, updateIrScore: true, installTblCheck: false, bmsScanPrefetchInfo, trackLibraryFileCheckProgress: true);
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
        if (flag)
        {
            if (startupInstallReadinessState.CanStartInstallEstimation())
            {
                BackgroundPendingEstimatePreparationResult startupEstimatePreparation;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                {
                    using (rwlockBMSFilesPendingInstall.GetWriterGuard())
                    {
                        using (rwlockBMSFiles.GetReaderGuard())
                        {
                            startupEstimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(BMSPackagesPending, PendingInstallEstimateBatchSource.StartupRestore);
                        }
                    }
                }
                foreach (BMSPackage deferredPackage in startupEstimatePreparation.DeferredPackages)
                {
                    int sourceHealth = 0;
                    startupEstimatePreparation.DeferredSourceHealthByPackage.TryGetValue(deferredPackage, out sourceHealth);
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
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            pendingPackageCount = BMSPackagesPending.Count;
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
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("owari: " + GC.GetTotalMemory(forceFullCollection: false));
        LogInstallPerformance("init_library phase1_min_load_ms=" + initializeResult.Phase1MinLoadMs + " phase2_scan_maint_ms=" + initializeResult.Phase2ScanMaintMs + " phase3_install_maintenance_ms=" + initializeResult.Phase3InstallMaintenanceMs + " wait_continuation_ms=" + initializeResult.WaitContinuationMs + " wait_continuation_start_ms=" + initializeResult.WaitBeforeContinuationStartMs + " wait_continuation_signal_ms=" + initializeResult.WaitForContinuationSignalMs + " wait_continuation_tasks_ms=" + initializeResult.WaitForContinuationTasksMs + " total_ms=" + initializeResult.TotalMs + " set_maintenance_enabled=" + setMaintenanceInfo.ToString().ToLowerInvariant());
    }

    private void TryImportChartInfoMetadataBundleAtStartup()
    {
        ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(AppDomain.CurrentDomain.BaseDirectory, dbGateway, LogInstallPerformance);
    }

    private static bool IsLikelyCrcHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 1 || value.Length > 8)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeLR2DirectoryHash(string directoryPath, Encoding encoding)
    {
        return LR2CRC32.Compute(encoding.GetBytes((directoryPath ?? string.Empty) + "\\\0")).ToString("x");
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
        BmsScanPrefetchInfo bmsScanPrefetchInfo = null,
        bool trackLibraryDatabaseProgress = false,
        bool trackLibraryFileCheckProgress = false)
    {
        Stopwatch stopwatchInitialize = Stopwatch.StartNew();
        long songTblLoadMs = 0L;
        long scoreTblLoadMs = 0L;
        long songTblFileCheckMs = 0L;
        long setMaintenanceMs = 0L;
        long setModeMs = 0L;
        long setHealthMs = 0L;
        long setZeroNoteMs = 0L;
        long installTblCheckMs = 0L;
        long rebuildHashIndexMs = 0L;
        int lr2IdAfterScoreLoad = 0;
        BmsLibraryOptionsSnapshot options = BmsLibraryOptionsSnapshot.CreateCurrent();
        List<string> bMSDirectories = getBMSDirectories();
        if (bMSDirectories.Count == 0)
        {
            songTblFileCheck = false;
        }
        if (songTblLoad)
        {
            Stopwatch stopwatchSongTblLoad = Stopwatch.StartNew();
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
                Stopwatch stopwatchBmsFilesAssign = Stopwatch.StartNew();
                BMSFiles = songTableLoadResult.LoadedFiles;
                BmsonSongs = songTableLoadResult.LoadedBmsonSongs;
                stopwatchBmsFilesAssign.Stop();
                songTableLoadResult.BmsFilesAssignMs = stopwatchBmsFilesAssign.ElapsedMilliseconds;
                LogInstallPerformance("song_tbl_load_breakdown song_table_load_ms=" + songTableLoadResult.SongTableLoadMs + " song_normalize_loop_ms=" + songTableLoadResult.SongNormalizeLoopMs + " folder_table_load_ms=" + songTableLoadResult.FolderTableLoadMs + " folder_normalize_loop_ms=" + songTableLoadResult.FolderNormalizeLoopMs + " fix_apply_ms=" + songTableLoadResult.FixApplyMs + " bmsfiles_assign_ms=" + songTableLoadResult.BmsFilesAssignMs + " commit_ms=" + songTableLoadResult.CommitMs);
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
            Stopwatch stopwatchScoreTblLoad = Stopwatch.StartNew();
            using (rwlockBMSScores.GetWriterGuard())
            {
                if (lr2ScoreDBPath != null)
                {
                    ScoreTableLoadResult scoreTableLoadResult = initializationService.LoadScoreTable(dbGateway, options);
                    LogInstallPerformance("score_tbl_load readOnly=" + scoreTableLoadResult.ReadOnly.ToString().ToLowerInvariant()
                        + " dbLockWaitMs=" + scoreTableLoadResult.DbLockWaitMs
                        + " rows=" + scoreTableLoadResult.Scores.Count
                        + " lr2Id=" + scoreTableLoadResult.LR2Id);
                    LR2ID = scoreTableLoadResult.LR2Id;
                    if (scoreTableLoadResult.Scores.Count > 0)
                    {
                        BMSScores = scoreTableLoadResult.Scores;
                    }
                    else
                    {
                        LR2ID = 0;
                        if (BMSScores == null)
                        {
                            BMSScores = new List<BMSScore>();
                        }
                    }
                }
                else if (BMSScores == null)
                {
                    BMSScores = new List<BMSScore>();
                }
            }
            RefreshScoreSnapshotFromCurrentScores("score_tbl_load");
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
            Stopwatch stopwatchSongTblFileCheck = Stopwatch.StartNew();
            ApplyLibraryFileScanDiff(options, bMSDirectories, bmsScanPrefetchInfo, trackLibraryFileCheckProgress, "initialize");
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
            Stopwatch stopwatchSetMaintenance = Stopwatch.StartNew();
            try
            {
                Stopwatch stopwatchSetMode = Stopwatch.StartNew();
                setModeAndCommitToDB(BMSFiles);
                stopwatchSetMode.Stop();
                setModeMs = stopwatchSetMode.ElapsedMilliseconds;
                Stopwatch stopwatchSetHealth = Stopwatch.StartNew();
                setMaintenanceInfo(BMSFiles, includeInstalledBmson: true);
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
        if (updateIrScore && lr2ScoreDBPath != null)
        {
            QueueDeferredScoreHydration("initialize_update_ir_score");
            QueueDeferredRankingRefresh("initialize_update_ir_score");
        }
        if (installTblCheck)
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    BMSPackagesPending.Clear();
                    BMSPackagesInstalled.Clear();
                    InstallTableLoadResult installTableLoadResult = initializationService.LoadInstallTable(
                        dbGateway,
                        ContainsInstalledChartUnsafe,
                        (bmsFile) => checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile?.HasValidMaintenanceInfoSnapshot == true ? bmsFile.TryGetMaintenanceInfoWithoutCreating() : null, strictCheck: true));
                    if (installTableLoadResult.StaleInstallPaths.Count > 0)
                    {
                        dbGateway.DeleteInstallRows(installTableLoadResult.StaleInstallPaths);
                    }
                    BMSPackagesPending.AddRange(installTableLoadResult.PendingPackages);
                    installTblCheckMs = installTableLoadResult.TotalMs;
                }
            }
        }
        Stopwatch stopwatchRebuildHashIndex = Stopwatch.StartNew();
        using (rwlockBMSFiles.GetReaderGuard())
        {
            RebuildBMSHashIndexUnsafe(BMSFiles);
        }
        stopwatchRebuildHashIndex.Stop();
        rebuildHashIndexMs = stopwatchRebuildHashIndex.ElapsedMilliseconds;
        stopwatchInitialize.Stop();
        LogInstallPerformance("init_library_internal song_tbl_load_ms=" + songTblLoadMs + " score_tbl_load_ms=" + scoreTblLoadMs + " song_tbl_file_check_ms=" + songTblFileCheckMs + " set_maintenance_ms=" + setMaintenanceMs + " set_mode_ms=" + setModeMs + " set_health_ms=" + setHealthMs + " set_zero_note_ms=" + setZeroNoteMs + " install_tbl_check_ms=" + installTblCheckMs + " rebuild_hash_index_ms=" + rebuildHashIndexMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
    }

    public void ReloadFileDiff()
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<string> bmsDirectories = getBMSDirectories();
        Stopwatch stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("library_file_diff_reload start directories=" + bmsDirectories.Count);
        try
        {
            using (rwlockBMSFilesInitializedAll.GetWriterGuard())
            {
                SongTableFileCheckResult result = ApplyLibraryFileScanDiff(options, bmsDirectories, null, trackLibraryFileCheckProgress: true, "reload_file_diff");
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
            stopwatch.Stop();
            LogInstallPerformance("library_file_diff_reload failed elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
            throw;
        }
    }

    private SongTableFileCheckResult ApplyLibraryFileScanDiff(
        BmsLibraryOptionsSnapshot options,
        List<string> bmsDirectories,
        BmsScanPrefetchInfo bmsScanPrefetchInfo,
        bool trackLibraryFileCheckProgress,
        string reason)
    {
        SongTableFileCheckResult emptyResult = new SongTableFileCheckResult();
        if (bmsDirectories == null || bmsDirectories.Count == 0)
        {
            if (trackLibraryFileCheckProgress)
            {
                CompleteLibraryFileEnumerationProgress();
                CompleteLibraryFileDiffProgress();
            }
            LogInstallPerformance("song_tbl_file_check skipped reason=no_bms_directories operation=" + (reason ?? string.Empty));
            return emptyResult;
        }

        LogStartupMemoryCheckpoint("file_diff", "before");
        bool fileEnumerationCompleted = false;
        Action completeFileEnumerationOnce = delegate
        {
            if (fileEnumerationCompleted)
            {
                return;
            }
            fileEnumerationCompleted = true;
            if (trackLibraryFileCheckProgress)
            {
                CompleteLibraryFileEnumerationProgress();
            }
        };

        bool inlineChartInfoApplied = false;
        List<LR2SongDBExtended.chart_info> committedInlineChartInfoRows = new List<LR2SongDBExtended.chart_info>();
        SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
            dbGateway,
            options,
            BMSFiles,
            bmsScanPrefetchInfo?.ScanResult,
            bmsScanPrefetchInfo?.ElapsedMs ?? 0L,
            () => ExecuteBmsScanWithManagedFallback(
                bmsDirectories,
                scannerLabel =>
                {
                    if (trackLibraryFileCheckProgress)
                    {
                        ReportLibraryInitializationProgress(
                            LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            force: true);
                    }
                }),
            dialogService,
            LogInstallPerformance,
            LogEverythingScan,
            BmsonSongs,
            null,
            completeFileEnumerationOnce,
            () =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    ReportLibraryInitializationProgress(LibraryInitializationProgressStage.FileDiff, force: true);
                }
            },
            (total, processed, path) =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    ReportLibraryInitializationProgress(
                        LibraryInitializationProgressStage.FileDiff,
                        totalCount: total,
                        processedCount: processed,
                        currentPath: path,
                        force: processed >= total);
                }
            },
            LogInstallPerformanceWarn,
            delegate(IReadOnlyList<LR2SongDBExtended.chart_info> rows)
            {
                if (rows == null || rows.Count == 0)
                {
                    return;
                }
                committedInlineChartInfoRows.AddRange(rows.Where((LR2SongDBExtended.chart_info row) => row != null));
            });
        completeFileEnumerationOnce();
        using (rwlockBMSFiles.GetWriterGuard())
        {
            BMSFiles = fileCheckResult.NextFiles;
            BmsonSongs = fileCheckResult.NextBmsonSongs;
            libraryResourceIndex = fileCheckResult.NextResourceIndex ?? LibraryResourceIndex.CreateFromScanResult(new BmsScanResult());
            bmsFolderAllFileList = libraryResourceIndex.FolderAllFileList ?? new BMSDirectoryFileNameHash();
            directoryResourceLookupCache = libraryResourceIndex.DirectoryLookupCache ?? new DirectoryResourceLookupCache();
            directoryRelativePathHashIndex = libraryResourceIndex.RelativePathHashIndex ?? new DirectoryRelativePathHashIndex();
        }
        if (committedInlineChartInfoRows.Count > 0)
        {
            UpsertChartInfoIndexRows(committedInlineChartInfoRows, "file_diff_inline");
            inlineChartInfoApplied = true;
            committedInlineChartInfoRows.Clear();
        }
        if (inlineChartInfoApplied)
        {
            RaisePropertyChanged(() => BMSFilesZeroNote);
        }
        if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
            || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
            || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
            || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
        {
            RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
        }
        if (fileCheckResult.HasDbDiff)
        {
            InvalidateBMSHashIndex();
            InvalidateInstalledDirectoryIndex();
            InvalidateBMSParentFolderListCache();
        }
        if (trackLibraryFileCheckProgress)
        {
            CompleteLibraryFileDiffProgress();
        }
        fileCheckResult.ReleasePostApplyTransientBuffers();
        LogStartupMemoryCheckpoint("file_diff", "after_release");
        return fileCheckResult;
    }

    private void RunChartDigestBackfill()
    {
        ChartDigestBackfillTotalCount = 0;
        ChartDigestBackfillProcessedCount = 0;
        ChartDigestBackfillCurrentPath = string.Empty;
        ChartDigestBackfillRunning = false;
        LogInstallPerformance("chart_digest_backfill skipped reason=combined_chart_info_pipeline");
    }

    /// <summary>
    /// 既存 chart_info 行を起動後にメモリ上の譜面へ適用します。
    /// 一覧表示用メタデータであり、導入先推定の critical path からは外します。
    /// </summary>
    private void QueueDeferredChartInfoHydration(string reason, bool queueFullBackfillAfterHydration)
    {
        int requestVersion;
        bool shouldStartWorker = false;
        lock (lockChartInfoHydration)
        {
            chartInfoHydrationRequestedVersion++;
            requestVersion = chartInfoHydrationRequestedVersion;
            chartInfoHydrationPending = true;
            chartInfoHydrationPendingReason = reason;
            chartInfoHydrationPendingQueueBackfill = chartInfoHydrationPendingQueueBackfill || queueFullBackfillAfterHydration;
            if (!chartInfoHydrationRunning)
            {
                chartInfoHydrationRunning = true;
                shouldStartWorker = true;
            }
        }
        ChartInfoHydrationRequestedVersion = requestVersion;
        ChartInfoHydrationTotalCount = 0;
        ChartInfoHydrationAppliedCount = 0;
        ChartInfoHydrationRunning = true;
        LogInstallPerformance("chart_info_hydration queue reason=" + (reason ?? "unknown") + " version=" + requestVersion + " queueBackfill=" + queueFullBackfillAfterHydration.ToString().ToLowerInvariant());
        if (shouldStartWorker)
        {
            Func<Task> work = delegate
            {
                ProcessDeferredChartInfoHydrationRequests();
                return Task.CompletedTask;
            };
            if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("chart_info_hydration", reason ?? "queue", null, work))
            {
                return;
            }
            Task.Run(ProcessDeferredChartInfoHydrationRequests).Logging("ProcessDeferredChartInfoHydrationRequests");
        }
    }

    private void ProcessDeferredChartInfoHydrationRequests()
    {
        while (true)
        {
            int requestVersion;
            string reason;
            bool queueBackfillAfterHydration;
            lock (lockChartInfoHydration)
            {
                requestVersion = chartInfoHydrationRequestedVersion;
                reason = chartInfoHydrationPendingReason;
                queueBackfillAfterHydration = chartInfoHydrationPendingQueueBackfill;
                chartInfoHydrationPending = false;
                chartInfoHydrationPendingReason = null;
                chartInfoHydrationPendingQueueBackfill = false;
            }

            ChartInfoHydrationResult result;
            try
            {
                result = HydrateChartInfos(reason);
            }
            catch (Exception ex)
            {
                result = new ChartInfoHydrationResult();
                LogInstallPerformance("chart_info_hydration failed reason=" + (reason ?? "unknown") + " message=" + ex.Message);
            }
            ChartInfoHydrationTotalCount = result.TotalRows;
            ChartInfoHydrationAppliedCount = result.AppliedBmsCount + result.AppliedBmsonCount;
            ChartInfoHydrationCompletedVersion = requestVersion;
            LogInstallPerformance("chart_info_hydration done version=" + requestVersion
                + " reason=" + (reason ?? "unknown")
                + " totalRows=" + result.TotalRows
                + " chartInfoRows=" + result.ChartInfoRows
                + " appliedBms=" + result.AppliedBmsCount
                + " appliedBmson=" + result.AppliedBmsonCount
                + " ownerApplyUpdated=" + result.OwnerApplyUpdatedCount
                + " ownerApplySkipped=" + result.OwnerApplySkippedCount
                + " ownerApplySilent=" + result.OwnerApplySilentCount
                + " ownerApplyNotified=" + result.OwnerApplyNotifiedCount
                + " ownerCount=" + result.OwnerCount
                + " currentChartInfoOwners=" + result.CurrentChartInfoOwnerCount
                + " currentParseFailureOwners=" + result.CurrentParseFailureOwnerCount
                + " backfillCandidateOwners=" + result.BackfillCandidateOwnerCount
                + " dbLoadMs=" + result.DbLoadMs
                + " dbMaterializeMs=" + result.DbMaterializeMs
                + " readOnly=" + result.DbReadOnly.ToString().ToLowerInvariant()
                + " dbLockWaitMs=" + result.DbLockWaitMs
                + " parseFailureRows=" + result.ParseFailureRows
                + " indexBuildMs=" + result.IndexBuildMs
                + " ownerApplyMs=" + result.OwnerApplyMs
                + " totalMs=" + result.TotalMs);
            LogStartupMemoryCheckpoint("chart_info_hydration", "after");

            bool completedLatestRequest = false;
            bool shouldQueueBackfillAfterCompletion = false;
            lock (lockChartInfoHydration)
            {
                if (!chartInfoHydrationPending)
                {
                    completedLatestRequest = true;
                    shouldQueueBackfillAfterCompletion = queueBackfillAfterHydration;
                }
                else if (queueBackfillAfterHydration)
                {
                    chartInfoHydrationPendingQueueBackfill = true;
                }
            }
            if (completedLatestRequest)
            {
                bool shouldReturnAfterCompletion = false;
                try
                {
                    if (shouldQueueBackfillAfterCompletion)
                    {
                        QueueChartInfoBackfill(reason, processSynchronously: true, hydrationResult: result);
                    }
                }
                finally
                {
                    lock (lockChartInfoHydration)
                    {
                        if (!chartInfoHydrationPending)
                        {
                            chartInfoHydrationRunning = false;
                            ChartInfoHydrationRunning = false;
                            shouldReturnAfterCompletion = true;
                        }
                    }
                }
                if (shouldReturnAfterCompletion)
                {
                    return;
                }
            }
        }
    }

    private ChartInfoHydrationResult HydrateChartInfos(string reason)
    {
        ChartInfoHydrationResult result = new ChartInfoHydrationResult();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("chart_info_hydration start reason=" + (reason ?? "unknown"));

        Dictionary<string, LR2SongDBExtended.chart_info> chartInfoMap;
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentParseFailures;
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        try
        {
            ChartInfoHydrationLoadResult loadResult = dbGateway.LoadChartInfoHydrationData(chartInfoBuildService.CurrentParseTimeout);
            chartInfoMap = loadResult.ChartInfoBySha256;
            currentParseFailures = loadResult.CurrentParseFailuresByMd5;
            result.ParseFailureRows = loadResult.ParseFailureRows;
            result.ChartInfoRows = loadResult.ChartInfoRows;
            result.DbMaterializeMs = loadResult.MaterializeMs;
            result.DbLoadMs = loadResult.DbReadMs;
            result.DbReadOnly = loadResult.ReadOnly;
            result.DbLockWaitMs = loadResult.DbLockWaitMs;
        }
        catch (Exception ex)
        {
            loadStopwatch.Stop();
            totalStopwatch.Stop();
            result.DbLoadMs = result.DbLoadMs == 0 ? loadStopwatch.ElapsedMilliseconds : result.DbLoadMs;
            result.LoadMs = result.DbLoadMs;
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogInstallPerformance("chart_info_hydration failed reason=" + (reason ?? "unknown") + " message=" + ex.Message);
            return result;
        }
        loadStopwatch.Stop();
        if (result.DbLoadMs == 0)
        {
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
        }
        result.LoadMs = result.DbLoadMs;
        result.TotalRows = chartInfoMap.Count;
        if (result.ChartInfoRows == 0)
        {
            result.ChartInfoRows = chartInfoMap.Count;
        }
        Stopwatch indexStopwatch = Stopwatch.StartNew();
        ChartInfoIndexUpdateResult indexUpdateResult = ReplaceChartInfoIndex(chartInfoMap.Values, hydrated: true);
        indexStopwatch.Stop();
        result.IndexBuildMs = indexStopwatch.ElapsedMilliseconds;
        LogInstallPerformance("chart_info_index_hydrated rows=" + indexUpdateResult.InputRows
            + " bySha256=" + indexUpdateResult.BySha256Count
            + " byMd5=" + indexUpdateResult.ByMd5Count
            + " version=" + indexUpdateResult.Version
            + " indexBuildMs=" + result.IndexBuildMs);

        Stopwatch applyStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (BMSFile file in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                if (file != null)
                {
                    ClassifyChartInfoHydrationOwner(result, file.sha256, file.hash, chartInfoMap, currentParseFailures);
                }
                if (file != null && !string.IsNullOrWhiteSpace(file.sha256) && chartInfoMap.TryGetValue(file.sha256, out LR2SongDBExtended.chart_info chartInfo))
                {
                    if (IsSameChartInfoIdentity(file.ChartInfo, chartInfo))
                    {
                        result.OwnerApplySkippedCount++;
                        continue;
                    }
                    if (file.SetChartInfoSilently(chartInfo))
                    {
                        result.AppliedBmsCount++;
                        result.OwnerApplyUpdatedCount++;
                        result.OwnerApplySilentCount++;
                    }
                }
            }
            foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            {
                if (song != null)
                {
                    ClassifyChartInfoHydrationOwner(result, song.sha256, song.md5, chartInfoMap, currentParseFailures);
                }
                if (song != null && !string.IsNullOrWhiteSpace(song.sha256) && chartInfoMap.TryGetValue(song.sha256, out LR2SongDBExtended.chart_info chartInfo))
                {
                    if (IsSameChartInfoIdentity(song.ChartInfo, chartInfo))
                    {
                        result.OwnerApplySkippedCount++;
                        continue;
                    }
                    song.ChartInfo = chartInfo;
                    result.AppliedBmsonCount++;
                    result.OwnerApplyUpdatedCount++;
                    result.OwnerApplySilentCount++;
                }
            }
        }
        applyStopwatch.Stop();
        result.OwnerApplyMs = applyStopwatch.ElapsedMilliseconds;
        result.ApplyMs = result.OwnerApplyMs;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        result.Succeeded = true;
        return result;
    }

    private static void ClassifyChartInfoHydrationOwner(
        ChartInfoHydrationResult result,
        string sha256,
        string md5,
        IDictionary<string, LR2SongDBExtended.chart_info> chartInfoBySha256,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentParseFailuresByMd5)
    {
        if (result == null)
        {
            return;
        }
        result.OwnerCount++;
        if (!string.IsNullOrWhiteSpace(sha256)
            && chartInfoBySha256 != null
            && chartInfoBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info chartInfo)
            && IsCurrentChartInfo(chartInfo))
        {
            result.CurrentChartInfoOwnerCount++;
            return;
        }
        if (!string.IsNullOrWhiteSpace(md5)
            && currentParseFailuresByMd5 != null
            && currentParseFailuresByMd5.ContainsKey(md5))
        {
            result.CurrentParseFailureOwnerCount++;
            return;
        }
        result.BackfillCandidateOwnerCount++;
    }

    private static bool IsCurrentChartInfo(LR2SongDBExtended.chart_info chartInfo)
    {
        return chartInfo != null && chartInfo.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        lock (lockChartInfoIndex)
        {
            if (!string.IsNullOrWhiteSpace(sha256) && chartInfoIndexBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info resolvedBySha256))
            {
                return resolvedBySha256;
            }
            if (!string.IsNullOrWhiteSpace(md5) && chartInfoIndexByMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates) && candidates.Count > 0)
            {
                return candidates.First().Value;
            }
        }
        return null;
    }

    private static bool IsSameChartInfoIdentity(LR2SongDBExtended.chart_info existing, LR2SongDBExtended.chart_info incoming)
    {
        if (existing == null || incoming == null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(existing.sha256) || string.IsNullOrWhiteSpace(incoming.sha256))
        {
            return false;
        }
        if (existing.parser_version <= 0 || incoming.parser_version <= 0)
        {
            return false;
        }
        if (existing.updated_at == default(DateTime) || incoming.updated_at == default(DateTime))
        {
            return false;
        }
        return string.Equals(existing.sha256, incoming.sha256, StringComparison.OrdinalIgnoreCase)
            && existing.parser_version == incoming.parser_version
            && existing.updated_at == incoming.updated_at;
    }

    private ChartInfoIndexUpdateResult ReplaceChartInfoIndex(IEnumerable<LR2SongDBExtended.chart_info> rows, bool hydrated)
    {
        Dictionary<string, LR2SongDBExtended.chart_info> bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> byMd5 = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);
        int inputRows = 0;
        foreach (LR2SongDBExtended.chart_info row in rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
        {
            if (!TryGetChartInfoSha256(row, out string sha256))
            {
                continue;
            }
            inputRows++;
            bySha256[sha256] = row;
            AddChartInfoMd5Candidate(byMd5, row, sha256);
        }

        ChartInfoIndexUpdateResult result = new ChartInfoIndexUpdateResult
        {
            InputRows = inputRows,
            BySha256Count = bySha256.Count,
            ByMd5Count = byMd5.Count
        };
        lock (lockChartInfoIndex)
        {
            chartInfoIndexBySha256 = bySha256;
            chartInfoIndexByMd5 = byMd5;
            result.HydrationChanged = _ChartInfoIndexHydrated != hydrated;
            _ChartInfoIndexHydrated = hydrated;
            _ChartInfoIndexVersion++;
            result.Version = _ChartInfoIndexVersion;
        }
        RaisePropertyChanged(() => ChartInfoIndexVersion);
        if (result.HydrationChanged)
        {
            RaisePropertyChanged(() => ChartInfoIndexHydrated);
        }
        RaisePropertyChanged(() => BMSFilesZeroNote);
        return result;
    }

    private ChartInfoIndexUpdateResult UpsertChartInfoIndexRows(IEnumerable<LR2SongDBExtended.chart_info> rows, string reason)
    {
        List<LR2SongDBExtended.chart_info> rowList = (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
            .Where((LR2SongDBExtended.chart_info row) => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .ToList();
        if (rowList.Count == 0)
        {
            return new ChartInfoIndexUpdateResult();
        }

        ChartInfoIndexUpdateResult result = new ChartInfoIndexUpdateResult
        {
            InputRows = rowList.Count
        };
        lock (lockChartInfoIndex)
        {
            foreach (LR2SongDBExtended.chart_info row in rowList)
            {
                if (!TryGetChartInfoSha256(row, out string sha256))
                {
                    continue;
                }
                if (chartInfoIndexBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info previousRow))
                {
                    RemoveChartInfoMd5Candidate(chartInfoIndexByMd5, previousRow, sha256);
                }
                chartInfoIndexBySha256[sha256] = row;
                AddChartInfoMd5Candidate(chartInfoIndexByMd5, row, sha256);
            }
            _ChartInfoIndexVersion++;
            result.Version = _ChartInfoIndexVersion;
            result.BySha256Count = chartInfoIndexBySha256.Count;
            result.ByMd5Count = chartInfoIndexByMd5.Count;
        }
        RaisePropertyChanged(() => ChartInfoIndexVersion);
        RaisePropertyChanged(() => BMSFilesZeroNote);
        LogInstallPerformance("chart_info_index_delta upserted=" + rowList.Count
            + " bySha256=" + result.BySha256Count
            + " byMd5=" + result.ByMd5Count
            + " version=" + result.Version
            + " reason=" + (reason ?? "unknown"));
        return result;
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

    private static void AddChartInfoMd5Candidate(
        IDictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> byMd5,
        LR2SongDBExtended.chart_info row,
        string sha256)
    {
        if (byMd5 == null || row == null || string.IsNullOrWhiteSpace(sha256) || !TryGetChartInfoMd5(row, out string md5))
        {
            return;
        }
        if (!byMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
        {
            candidates = new SortedDictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            byMd5[md5] = candidates;
        }
        candidates[sha256] = row;
    }

    private static void RemoveChartInfoMd5Candidate(
        IDictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> byMd5,
        LR2SongDBExtended.chart_info row,
        string sha256)
    {
        if (byMd5 == null || row == null || string.IsNullOrWhiteSpace(sha256) || !TryGetChartInfoMd5(row, out string md5))
        {
            return;
        }
        if (!byMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
        {
            return;
        }
        candidates.Remove(sha256);
        if (candidates.Count == 0)
        {
            byMd5.Remove(md5);
        }
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMapSnapshot()
    {
        return dbGateway.LoadChartInfoMap();
    }

    private Dictionary<string, LR2SongDBExtended.chart_info> CreateHydratedChartInfoIndexSha256Snapshot()
    {
        lock (lockChartInfoIndex)
        {
            if (!_ChartInfoIndexHydrated)
            {
                return null;
            }
            return new Dictionary<string, LR2SongDBExtended.chart_info>(chartInfoIndexBySha256, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosBySha256(IEnumerable<string> sha256s)
    {
        return dbGateway.LoadChartInfosBySha256(sha256s);
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosByMd5(IEnumerable<string> md5s)
    {
        return dbGateway.LoadChartInfosByMd5(md5s);
    }

    private void WaitForChartInfoHydrationIdle()
    {
        while (true)
        {
            lock (lockChartInfoHydration)
            {
                if (!chartInfoHydrationRunning)
                {
                    return;
                }
            }
            lock (lockChartInfoBackfill)
            {
                if (chartInfoBackfillHydrationBypassUntilVersion > chartInfoBackfillCompletedVersion)
                {
                    return;
                }
            }
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// chart_info の不足分構築をバックグラウンドへ要求します。
    /// </summary>
    /// <param name="reason">ログに残す要求理由。</param>
    private void QueueChartInfoBackfill(string reason, bool processSynchronously = false, ChartInfoHydrationResult hydrationResult = null)
    {
        if (hydrationResult != null && hydrationResult.Succeeded && hydrationResult.BackfillCandidateOwnerCount <= 0)
        {
            int skippedVersion = CompleteSkippedChartInfoBackfillRequestIfIdle();
            LogInstallPerformance("chart_info_backfill skipped reason=hydration_all_current"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " ownerCount=" + hydrationResult.OwnerCount
                + " currentChartInfo=" + hydrationResult.CurrentChartInfoOwnerCount
                + " currentParseFailure=" + hydrationResult.CurrentParseFailureOwnerCount
                + " candidates=" + hydrationResult.BackfillCandidateOwnerCount);
            LogStartupMemoryCheckpoint("chart_info_backfill", "skipped");
            return;
        }
        ChartInfoBackfillCandidateSummary summary = null;
        Stopwatch candidateSummaryStopwatch = Stopwatch.StartNew();
        try
        {
            LogInstallPerformance("chart_info_backfill candidate_summary_start reason=" + (reason ?? "unknown"));
            summary = dbGateway.GetChartInfoBackfillCandidateSummary(chartInfoBuildService.CurrentParseTimeout);
            candidateSummaryStopwatch.Stop();
            LogInstallPerformance("chart_info_backfill candidate_summary_done reason=" + (reason ?? "unknown")
                + " elapsedMs=" + candidateSummaryStopwatch.ElapsedMilliseconds
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        }
        catch (Exception ex)
        {
            candidateSummaryStopwatch.Stop();
            LogInstallPerformance("chart_info_backfill candidate_summary_failed reason=" + (reason ?? "unknown")
                + " elapsedMs=" + candidateSummaryStopwatch.ElapsedMilliseconds
                + " message=" + ex.Message);
        }
        if (summary != null && summary.CandidateOwnerCount <= 0)
        {
            int skippedVersion = CompleteSkippedChartInfoBackfillRequestIfIdle();
            LogInstallPerformance("chart_info_backfill skipped reason=no_candidates"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
            LogStartupMemoryCheckpoint("chart_info_backfill", "skipped");
            return;
        }
        if (summary != null)
        {
            LogInstallPerformance("chart_info_backfill candidates"
                + " reason=" + (reason ?? "unknown")
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        }
        QueueChartInfoBackfillRequest(ChartInfoBackfillRequest.Full(reason), processSynchronously);
    }

    private int CompleteSkippedChartInfoBackfillRequestIfIdle()
    {
        int requestVersion = 0;
        lock (lockChartInfoBackfill)
        {
            if (ChartInfoBackfillRunning)
            {
                return chartInfoBackfillRequestedVersion;
            }
            chartInfoBackfillRequestedVersion++;
            requestVersion = chartInfoBackfillRequestedVersion;
            chartInfoBackfillCompletedVersion = requestVersion;
        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        ChartInfoBackfillTotalCount = 0;
        ChartInfoBackfillProcessedCount = 0;
        ChartInfoBackfillCurrentPath = string.Empty;
        ChartInfoBackfillCompletedVersion = requestVersion;
        ChartInfoBackfillRunning = false;
        return requestVersion;
    }

    private void QueueChartInfoBackfillRequest(ChartInfoBackfillRequest request, bool processSynchronously = false)
    {
        int requestVersion;
        bool shouldStartWorker = false;
        bool shouldWaitForCompletion = false;
        if (request == null)
        {
            return;
        }
        lock (lockChartInfoBackfill)
        {
            chartInfoBackfillRequestedVersion++;
            requestVersion = chartInfoBackfillRequestedVersion;
            chartInfoBackfillRequests.Add(request);
            if (!ChartInfoBackfillRunning)
            {
                shouldStartWorker = true;
                ChartInfoBackfillRunning = true;
            }
            shouldWaitForCompletion = processSynchronously && !shouldStartWorker;
            if (shouldWaitForCompletion)
            {
                chartInfoBackfillHydrationBypassUntilVersion = Math.Max(chartInfoBackfillHydrationBypassUntilVersion, requestVersion);
            }
        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        ChartInfoBackfillTotalCount = 0;
        ChartInfoBackfillProcessedCount = 0;
        ChartInfoBackfillCurrentPath = string.Empty;
        LogInstallPerformance("chart_info_backfill queue"
            + " reason=" + (request.Reason ?? "unknown")
            + " mode=full"
            + " version=" + requestVersion);
        if (shouldStartWorker)
        {
            if (processSynchronously)
            {
                ProcessChartInfoBackfillRequests(waitForChartInfoHydrationIdle: false);
                return;
            }
            Task.Run(() => ProcessChartInfoBackfillRequests()).Logging("ProcessChartInfoBackfillRequests");
        }
        if (shouldWaitForCompletion)
        {
            WaitForChartInfoBackfillVersion(requestVersion);
        }
    }

    private void WaitForChartInfoBackfillVersion(int requestVersion)
    {
        try
        {
            while (true)
            {
                bool completed;
                lock (lockChartInfoBackfill)
                {
                    completed = chartInfoBackfillCompletedVersion >= requestVersion;
                }
                if (completed)
                {
                    return;
                }
                Thread.Sleep(50);
            }
        }
        finally
        {
            lock (lockChartInfoBackfill)
            {
                if (chartInfoBackfillHydrationBypassUntilVersion <= requestVersion)
                {
                    chartInfoBackfillHydrationBypassUntilVersion = 0;
                }
            }
        }
    }

    private ChartInfoInlineBuildResult BuildAndPersistInlineChartInfoForInstalledCharts(
        string reason,
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsTargets = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && PendingChartEntry.IsBmsChartFile(file))
            .ToList();
        List<LR2SongDBExtended.bmson_song> bmsonTargets = (bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .ToList();
        ChartInfoInlineBuildResult result = new ChartInfoInlineBuildResult();
        if (bmsTargets.Count == 0 && bmsonTargets.Count == 0)
        {
            LogInstallPerformance("chart_info_inline_install reason=" + (reason ?? "unknown") + " target=0 success=0 currentSkipped=0 failureSkipped=0 parseFailed=0 failurePersisted=0 failureCleared=0 readFailed=0 parseMs=0");
            return result;
        }

        ChartInfoInlineBuildService inlineBuildService = new ChartInfoInlineBuildService(
            chartInfoBuildService,
            BmsLibraryInitializationService.ResolveDefaultFileDiffParserDegree());
        result = inlineBuildService.BuildForExistingFiles(
            dbGateway,
            bmsTargets,
            bmsonTargets,
            LogInstallPerformance,
            LogInstallPerformanceWarn);
        if (bmsTargets.Count > 0)
        {
            dbGateway.UpsertSongs(bmsTargets);
        }
        if (bmsonTargets.Count > 0)
        {
            dbGateway.UpsertBmsonSongs(bmsonTargets);
        }
        dbGateway.UpsertChartInfoBackfillChunk(
            Enumerable.Empty<ChartDigestBackfillEntry>(),
            result.ChartInfoRows,
            result.ParseFailureRows,
            result.ParseFailureDeleteMd5s);
        if (result.AppliedRows.Count > 0)
        {
            UpsertChartInfoIndexRows(result.AppliedRows, reason ?? "install_package_inline");
            RaisePropertyChanged(() => BMSFilesZeroNote);
        }
        if (result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0)
        {
            RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
        }
        LogInstallPerformance("chart_info_inline_install reason=" + (reason ?? "unknown")
            + " target=" + result.TargetCount
            + " success=" + result.SuccessCount
            + " currentSkipped=" + result.CurrentSkippedCount
            + " failureSkipped=" + result.FailureSkippedCount
            + " parseFailed=" + result.ParseFailedCount
            + " failurePersisted=" + result.FailurePersistedCount
            + " failureCleared=" + result.FailureClearedCount
            + " readFailed=" + result.ReadFailedCount
            + " parseMs=" + result.ParseMs);
        return result;
    }

    /// <summary>
    /// 最新の chart_info 構築要求を処理します。
    /// 譜面削除時も chart_info は残すため、この worker は upsert のみ行います。
    /// </summary>
    private void ProcessChartInfoBackfillRequests(bool waitForChartInfoHydrationIdle = true)
    {
        while (true)
        {
            if (waitForChartInfoHydrationIdle)
            {
                WaitForChartInfoHydrationIdle();
            }
            int requestVersion;
            List<ChartInfoBackfillRequest> requests;
            lock (lockChartInfoBackfill)
            {
                requestVersion = chartInfoBackfillRequestedVersion;
                requests = chartInfoBackfillRequests.ToList();
                chartInfoBackfillRequests.Clear();
            }
            List<BMSFile> filesSnapshot;
            List<LR2SongDBExtended.bmson_song> bmsonSongsSnapshot;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                filesSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                bmsonSongsSnapshot = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Where((LR2SongDBExtended.bmson_song song) => song != null).ToList();
            }
            int snapshotCount = filesSnapshot.Count + bmsonSongsSnapshot.Count;
            bool completedLatestRequest = false;
            Dictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot = null;
            ChartInfoBackfillResult result = null;
            try
            {
                ChartInfoBackfillRunning = true;
                ChartInfoBackfillTotalCount = 0;
                ChartInfoBackfillProcessedCount = 0;
                ChartInfoBackfillCurrentPath = string.Empty;
                Action<int, int, string> reportProgress = delegate (int total, int processed, string currentPath)
                {
                    ChartInfoBackfillTotalCount = total;
                    ChartInfoBackfillProcessedCount = processed;
                    ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
                };
                existingRowsSnapshot = CreateHydratedChartInfoIndexSha256Snapshot();
                result = chartInfoBuildService.BackfillChartInfos(
                    dbGateway,
                    filesSnapshot,
                    bmsonSongsSnapshot,
                    reportProgress,
                    LogInstallPerformance,
                    LogInstallPerformanceWarn,
                    (IReadOnlyList<LR2SongDBExtended.chart_info> rows) => UpsertChartInfoIndexRows(rows, "backfill"),
                    existingRowsSnapshot);
                if (result.DigestBackfilledCount > 0)
                {
                    lock (lockPlaylistSummaryOwnedHashSnapshot)
                    {
                        playlistSummaryOwnedHashSnapshot = null;
                    }
                }
                LogInstallPerformance("chart_info_backfill done version=" + requestVersion + " mode=full total=" + result.TargetCount + " success=" + result.BackfilledCount + " failed=" + result.FailedCount + " timeoutFailed=" + result.TimeoutFailedCount + " digestBackfilled=" + result.DigestBackfilledCount + " digestFailed=" + result.DigestFailedCount + " fileReadCount=" + result.FileReadCount + " fileReadBytes=" + result.FileReadBytes + " currentRowSkipped=" + result.CurrentRowSkippedCount + " parseFailureSkipped=" + result.FailureSkippedCount);
            }
            catch (Exception ex)
            {
                LogInstallPerformance("chart_info_backfill failed version=" + requestVersion + " message=" + ex.Message);
            }
            finally
            {
                ChartInfoBackfillCurrentPath = string.Empty;
                ChartInfoBackfillCompletedVersion = requestVersion;
                RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
                lock (lockChartInfoBackfill)
                {
                    chartInfoBackfillCompletedVersion = requestVersion;
                    if (requestVersion == chartInfoBackfillRequestedVersion)
                    {
                        ChartInfoBackfillRunning = false;
                        completedLatestRequest = true;
                    }
                }
                filesSnapshot?.Clear();
                bmsonSongsSnapshot?.Clear();
                existingRowsSnapshot?.Clear();
                LogStartupMemoryCheckpoint("chart_info_backfill", "after_release");
            }
            if (completedLatestRequest)
            {
                return;
            }
        }
    }

    private void QueueDeferredMaintenanceHydration(string reason)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredMaintenanceHydration)
        {
            MaintenanceHydrationRequestedVersion = MaintenanceHydrationRequestedVersion + 1;
            version = MaintenanceHydrationRequestedVersion;
            if (!MaintenanceHydrationRunning)
            {
                MaintenanceHydrationRunning = true;
                shouldStartWorker = true;
            }
        }
        LogInstallPerformance("maintenance_hydration queue reason=" + (reason ?? "unknown") + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Func<bool, Action> createWorker = reportDirect => delegate
        {
            ProcessDeferredMaintenanceHydrationRequests(reportDirect);
        };
        Func<Task> work = delegate
        {
            createWorker(false)();
            return Task.CompletedTask;
        };
        if (StartupBackgroundTaskScheduler != null
            && StartupBackgroundTaskScheduler("maintenance_hydration", reason ?? "queue", null, work))
        {
            return;
        }
        ReportStartupBackgroundTask("maintenance_hydration", "queued", 0L, failed: false, detail: reason ?? string.Empty);
        Task.Run(createWorker(true)).Logging("ProcessDeferredMaintenanceHydrationRequests");
    }

    private void ProcessDeferredMaintenanceHydrationRequests(bool reportDirect)
    {
        while (true)
        {
            int requestVersion;
            lock (lockDeferredMaintenanceHydration)
            {
                requestVersion = MaintenanceHydrationRequestedVersion;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (reportDirect)
            {
                ReportStartupBackgroundTask("maintenance_hydration", "start", 0L, failed: false, detail: "version=" + requestVersion);
            }
            try
            {
                BmsLibraryOptionsSnapshot options = BmsLibraryOptionsSnapshot.CreateCurrent();
                LogInstallPerformance("maintenance_hydration start version=" + requestVersion);
                MaintenanceTableHydrationResult result = initializationService.LoadMaintenanceTable(
                    dbGateway,
                    options,
                    LogInstallPerformance);
                ApplyMaintenanceHydrationResult(result);
                stopwatch.Stop();
                result.TotalMs = stopwatch.ElapsedMilliseconds;
                LogInstallPerformance("maintenance_hydration done version=" + requestVersion
                    + " rows=" + result.MaintenanceTableCount
                    + " keys=" + result.MaintenanceMap.Count
                    + " readOnly=" + result.ReadOnly.ToString().ToLowerInvariant()
                    + " dbLockWaitMs=" + result.DbLockWaitMs
                    + " readMs=" + result.MaintenanceTableLoadMs
                    + " countMs=" + result.MaintenanceCountMs
                    + " materializeMs=" + result.MaintenanceMaterializeMs
                    + " mapBuildMs=" + result.MaintenanceMapBuildMs
                    + " applyMs=" + result.MaintenanceApplyMs
                    + " attachMs=" + result.MaintenanceAttachMs
                    + " indexBuildMs=" + result.ResourceHealthIndexMs
                    + " cleanupDeleted=" + result.CleanupDeletedCount
                    + " cleanupMs=" + result.CleanupMs
                    + " ownerPathCount=" + result.OwnerPathCount
                    + " stalePathCount=" + result.StalePathCount
                    + " validSnapshotCount=" + result.ValidSnapshotCount
                    + " placeholderCount=" + result.PlaceholderCount
                    + " viewRefreshQueued=" + result.ViewRefreshQueued.ToString().ToLowerInvariant()
                    + " appliedBms=" + result.AppliedBmsCount
                    + " appliedBmson=" + result.AppliedBmsonCount
                    + " defaultBms=" + result.DefaultBmsCount
                    + " defaultBmson=" + result.DefaultBmsonCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                if (reportDirect)
                {
                    ReportStartupBackgroundTask("maintenance_hydration", "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "rows=" + result.MaintenanceTableCount + "_appliedBms=" + result.AppliedBmsCount + "_appliedBmson=" + result.AppliedBmsonCount);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("maintenance_hydration failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                if (reportDirect)
                {
                    ReportStartupBackgroundTask("maintenance_hydration", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
                }
            }

            lock (lockDeferredMaintenanceHydration)
            {
                MaintenanceHydrationCompletedVersion = requestVersion;
                if (requestVersion == MaintenanceHydrationRequestedVersion)
                {
                    MaintenanceHydrationRunning = false;
                    return;
                }
            }
        }
    }

    private void ApplyMaintenanceHydrationResult(MaintenanceTableHydrationResult result)
    {
        if (result == null)
        {
            return;
        }
        HashSet<string> ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stopwatch applyStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFiles.GetWriterGuard())
        {
            Stopwatch attachStopwatch = Stopwatch.StartNew();
            foreach (BMSFile item in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                if (item == null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.path))
                {
                    ownerPaths.Add(item.path);
                }
                BMSFileMaintenanceInfo nextInfo = null;
                if (!string.IsNullOrWhiteSpace(item.path)
                    && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                    && (item.HasMaintenanceInfoHash(value.hash) || string.Equals(value.hash, item.hash, StringComparison.OrdinalIgnoreCase)))
                {
                    nextInfo = value;
                    result.AppliedBmsCount++;
                    item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, registerEventHandlers: false, MaintenanceInfoOrigin.DbHydrated);
                    result.ValidSnapshotCount++;
                }
                else
                {
                    result.DefaultBmsCount++;
                    if (item.HasValidMaintenanceInfoSnapshot)
                    {
                        result.ValidSnapshotCount++;
                    }
                    else
                    {
                        nextInfo = item.TryGetMaintenanceInfoWithoutCreating() ?? new BMSFileMaintenanceInfo(item);
                        item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, registerEventHandlers: false, MaintenanceInfoOrigin.Placeholder);
                        result.PlaceholderCount++;
                    }
                }
            }
            foreach (LR2SongDBExtended.bmson_song item in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            {
                if (item == null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.path))
                {
                    ownerPaths.Add(item.path);
                }
                if (!string.IsNullOrWhiteSpace(item.path)
                    && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                    && string.Equals(value.hash, item.md5, StringComparison.OrdinalIgnoreCase))
                {
                    value.NormalizeForBmson(item.path, item.md5);
                    item.MaintenanceInfo = value;
                    result.AppliedBmsonCount++;
                    result.ValidSnapshotCount++;
                }
                else
                {
                    result.DefaultBmsonCount++;
                    if (item.MaintenanceInfo != null)
                    {
                        result.ValidSnapshotCount++;
                    }
                    else
                    {
                        result.PlaceholderCount++;
                    }
                }
            }
            attachStopwatch.Stop();
            result.MaintenanceAttachMs = attachStopwatch.ElapsedMilliseconds;
            ResourceHealthIndexSnapshot healthIndexSnapshot = RebuildResourceHealthIndexSnapshotLocked("maintenance_hydration");
            result.ResourceHealthIndexMs = healthIndexSnapshot.BuildMs;
            result.OwnerPathCount = ownerPaths.Count;
            foreach (string maintenancePath in result.MaintenanceMap.Keys)
            {
                if (!string.IsNullOrWhiteSpace(maintenancePath) && !ownerPaths.Contains(maintenancePath))
                {
                    result.StaleMaintenancePaths.Add(maintenancePath);
                }
            }
            result.StalePathCount = result.StaleMaintenancePaths.Count;
            applyStopwatch.Stop();
            result.MaintenanceApplyMs = applyStopwatch.ElapsedMilliseconds;
            Stopwatch cleanupStopwatch = Stopwatch.StartNew();
            if (result.StaleMaintenancePaths.Count > 0)
            {
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    result.CleanupDeletedCount = dbGateway.DeleteMaintenanceRows(result.StaleMaintenancePaths);
                }
            }
            cleanupStopwatch.Stop();
            result.CleanupMs = cleanupStopwatch.ElapsedMilliseconds;
        }
        result.ViewRefreshQueued = true;
        RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
        RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
        RaisePropertyChanged(() => BMSFilesGarbled);
        RaisePropertyChanged(() => BMSFilesGarbledFixed);
        RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
    }

    private void QueueDeferredInstallableMaintenance(string reason, long criticalElapsedMs, string dependency = null)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredInstallableMaintenance)
        {
            InstallableMaintenanceDeferredRequestedVersion = InstallableMaintenanceDeferredRequestedVersion + 1;
            version = InstallableMaintenanceDeferredRequestedVersion;
            deferredInstallableMaintenanceCriticalElapsedMs = criticalElapsedMs;
            if (!InstallableMaintenanceDeferredRunning)
            {
                InstallableMaintenanceDeferredRunning = true;
                shouldStartWorker = true;
            }
        }
        int queueSnapshotCount = CountInstallableMaintenanceSnapshotTargets();
        LogInstallPerformance("installable_maintenance_deferred queue reason=" + (reason ?? "unknown")
            + " version=" + version
            + " snapshotCount=" + queueSnapshotCount
            + " criticalMs=" + criticalElapsedMs);
        if (!shouldStartWorker)
        {
            return;
        }
        Action worker = delegate
        {
            while (true)
            {
                int requestVersion;
                long requestCriticalElapsedMs;
                lock (lockDeferredInstallableMaintenance)
                {
                    requestVersion = deferredInstallableMaintenanceRequestedVersion;
                    requestCriticalElapsedMs = deferredInstallableMaintenanceCriticalElapsedMs;
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                long setModeMs = 0L;
                long setHealthMs = 0L;
                long setZeroNoteMs = 0L;
                int snapshotCount = 0;
                int setModeTargetCount = 0;
                MaintenanceWorkflowResult maintenanceResult = new MaintenanceWorkflowResult();
                List<BMSFile> filesSnapshot = null;
                try
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        filesSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                        snapshotCount = filesSnapshot.Count + ((BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Count((LR2SongDBExtended.bmson_song song) => song != null));
                    }
                    LogInstallPerformance("installable_maintenance_deferred run version=" + requestVersion
                        + " snapshotCount=" + snapshotCount
                        + " criticalMs=" + requestCriticalElapsedMs);
                    Stopwatch stopwatchSetMode = Stopwatch.StartNew();
                    setModeTargetCount = setModeAndCommitToDB(filesSnapshot);
                    stopwatchSetMode.Stop();
                    setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                    Stopwatch stopwatchSetHealth = Stopwatch.StartNew();
                    maintenanceResult = setMaintenanceInfo(filesSnapshot, includeInstalledBmson: true) ?? new MaintenanceWorkflowResult();
                    stopwatchSetHealth.Stop();
                    setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
                    IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
                    IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;

                    IsWriteLockHeldInitializeBMSFilesZeroNote = false;

                    stopwatch.Stop();
                    LogInstallPerformance("installable_maintenance_deferred done version=" + requestVersion
                        + " criticalMs=" + requestCriticalElapsedMs
                        + " snapshotCount=" + snapshotCount
                        + " setModeTargets=" + setModeTargetCount
                        + " maintenanceChecked=" + maintenanceResult.CheckedFileCount
                        + " bmsResourceTargets=" + maintenanceResult.BmsResourceTargetCount
                        + " bmsonResourceTargets=" + maintenanceResult.BmsonResourceTargetCount
                        + " healthTargetCount=" + maintenanceResult.HealthTargetCount
                        + " healthDegree=" + maintenanceResult.HealthDegree
                        + " forceTargets=" + maintenanceResult.ForceTargetCount
                        + " missingInfoTargets=" + maintenanceResult.MissingInfoTargetCount
                        + " missingEncodingTargets=" + maintenanceResult.MissingEncodingTargetCount
                        + " bmsonMissingFreshRefs=" + maintenanceResult.BmsonMissingFreshResourceReferenceCount
                        + " healthMs=" + maintenanceResult.HealthMs
                        + " encodingMs=" + maintenanceResult.EncodingMs
                        + " bmsonRefreshMs=" + maintenanceResult.BmsonRefreshMs
                        + " healthCacheHit=" + maintenanceResult.HealthCacheHitCount
                        + " healthFileExistsFallback=" + maintenanceResult.HealthFileExistsFallbackCount
                        + " healthFileExistsFallbackAudio=" + maintenanceResult.HealthAudioFileExistsFallbackCount
                        + " healthFileExistsFallbackImage=" + maintenanceResult.HealthImageFileExistsFallbackCount
                        + " healthFileExistsFallbackMovie=" + maintenanceResult.HealthMovieFileExistsFallbackCount
                        + " healthFileExistsFallbackOptionalImage=" + maintenanceResult.HealthOptionalImageFileExistsFallbackCount
                        + " maintenanceUpserted=" + maintenanceResult.MaintenanceInfoUpsertCount
                        + " bmsonReparsed=" + maintenanceResult.BmsonReparsedCount
                        + " bmsonReparseFailed=" + maintenanceResult.BmsonReparseFailedCount
                        + " bmsonResourceRefsReused=" + maintenanceResult.BmsonResourceReferenceReusedCount
                        + " songReloaded=" + maintenanceResult.ReloadedSongCount
                        + " resourceHealthIndexMs=" + maintenanceResult.ResourceHealthIndexMs
                        + " warningReapplyTargets=" + maintenanceResult.WarningReapplyTargets
                        + " warningChanged=" + maintenanceResult.WarningChangedCount
                        + " set_mode_ms=" + setModeMs
                        + " set_health_ms=" + setHealthMs
                        + " set_zero_note_ms=" + setZeroNoteMs
                        + " deferred_ms=" + stopwatch.ElapsedMilliseconds);
                    LogInstallPerformance("init_library_installable critical_ms=" + requestCriticalElapsedMs + " deferred_ms=" + stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    LogInstallPerformance("installable_maintenance_deferred failed version=" + requestVersion
                        + " criticalMs=" + requestCriticalElapsedMs
                        + " snapshotCount=" + snapshotCount
                        + " setModeTargets=" + setModeTargetCount
                        + " maintenanceChecked=" + maintenanceResult.CheckedFileCount
                        + " bmsResourceTargets=" + maintenanceResult.BmsResourceTargetCount
                        + " bmsonResourceTargets=" + maintenanceResult.BmsonResourceTargetCount
                        + " healthTargetCount=" + maintenanceResult.HealthTargetCount
                        + " healthDegree=" + maintenanceResult.HealthDegree
                        + " forceTargets=" + maintenanceResult.ForceTargetCount
                        + " missingInfoTargets=" + maintenanceResult.MissingInfoTargetCount
                        + " missingEncodingTargets=" + maintenanceResult.MissingEncodingTargetCount
                        + " bmsonMissingFreshRefs=" + maintenanceResult.BmsonMissingFreshResourceReferenceCount
                        + " healthMs=" + maintenanceResult.HealthMs
                        + " encodingMs=" + maintenanceResult.EncodingMs
                        + " bmsonRefreshMs=" + maintenanceResult.BmsonRefreshMs
                        + " healthCacheHit=" + maintenanceResult.HealthCacheHitCount
                        + " healthFileExistsFallback=" + maintenanceResult.HealthFileExistsFallbackCount
                        + " healthFileExistsFallbackAudio=" + maintenanceResult.HealthAudioFileExistsFallbackCount
                        + " healthFileExistsFallbackImage=" + maintenanceResult.HealthImageFileExistsFallbackCount
                        + " healthFileExistsFallbackMovie=" + maintenanceResult.HealthMovieFileExistsFallbackCount
                        + " healthFileExistsFallbackOptionalImage=" + maintenanceResult.HealthOptionalImageFileExistsFallbackCount
                        + " maintenanceUpserted=" + maintenanceResult.MaintenanceInfoUpsertCount
                        + " bmsonReparsed=" + maintenanceResult.BmsonReparsedCount
                        + " bmsonReparseFailed=" + maintenanceResult.BmsonReparseFailedCount
                        + " bmsonResourceRefsReused=" + maintenanceResult.BmsonResourceReferenceReusedCount
                        + " songReloaded=" + maintenanceResult.ReloadedSongCount
                        + " resourceHealthIndexMs=" + maintenanceResult.ResourceHealthIndexMs
                        + " warningReapplyTargets=" + maintenanceResult.WarningReapplyTargets
                        + " warningChanged=" + maintenanceResult.WarningChangedCount
                        + " set_mode_ms=" + setModeMs
                        + " set_health_ms=" + setHealthMs
                        + " set_zero_note_ms=" + setZeroNoteMs
                        + " deferred_ms=" + stopwatch.ElapsedMilliseconds
                        + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                }
                finally
                {
                    IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
                    IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
                    IsWriteLockHeldInitializeBMSFilesZeroNote = false;
                    filesSnapshot?.Clear();
                    filesSnapshot = null;
                    LogStartupMemoryCheckpoint("installable_maintenance_deferred", "after_release");
                }

                lock (lockDeferredInstallableMaintenance)
                {
                    InstallableMaintenanceDeferredCompletedVersion = requestVersion;
                    if (requestVersion == InstallableMaintenanceDeferredRequestedVersion)
                    {
                        InstallableMaintenanceDeferredRunning = false;
                        return;
                    }
                }
            }
        };
        Func<Task> work = delegate
        {
            worker();
            return Task.CompletedTask;
        };
        if (StartupBackgroundTaskScheduler != null
            && StartupBackgroundTaskScheduler("installable_maintenance", reason ?? "queue", dependency, work))
        {
            return;
        }
        Task.Run(worker).Logging("ProcessDeferredInstallableMaintenance");
    }

    private int CountInstallableMaintenanceSnapshotTargets()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            int bmsCount = (BMSFiles ?? new List<BMSFile>()).Count((BMSFile file) => file != null);
            int bmsonCount = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Count((LR2SongDBExtended.bmson_song song) => song != null);
            return bmsCount + bmsonCount;
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
                Stopwatch stopwatch = Stopwatch.StartNew();
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
        Stopwatch waitStopwatch = Stopwatch.StartNew();
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
            Stopwatch stopwatch = Stopwatch.StartNew();
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
            Stopwatch stopwatch = Stopwatch.StartNew();
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
        if (lr2ScoreDBPath == null)
        {
            return;
        }
        ScoreSnapshot snapshot = GetScoreSnapshotForLookup(allowOnDemandBuild: true);
        if (snapshot == null || snapshot.ScoresByHash == null || snapshot.ScoresByHash.Count == 0)
        {
            return;
        }
        List<BMSFile> bmsFilesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        }
        for (int offset = 0; offset < bmsFilesSnapshot.Count; offset += deferredScoreHydrationChunkSize)
        {
            if (IsDeferredScoreHydrationRequestSuperseded(requestVersion))
            {
                throw new OperationCanceledException();
            }
            int count = Math.Min(deferredScoreHydrationChunkSize, bmsFilesSnapshot.Count - offset);
            List<BMSFile> chunk = bmsFilesSnapshot.GetRange(offset, count);
            Stopwatch chunkStopwatch = Stopwatch.StartNew();
            int matchedScoreCount = irService.ApplyKnownScoresToFilesAndCount(chunk, snapshot.ScoresByHash);
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
        lock (lockDeferredScoreHydration)
        {
            return requestVersion != deferredScoreHydrationRequestedVersion;
        }
    }

    /// <summary>
    /// 最新の ranking refresh 要求を実行し、score snapshot を更新します。
    /// </summary>
    /// <param name="requestVersion">処理対象の要求版数。</param>
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
        RankingRefreshRunResult result = new RankingRefreshRunResult();
        if (lr2ScoreDBPath == null || LR2ID == 0)
        {
            return result;
        }
        Stopwatch irScoreStopwatch = Stopwatch.StartNew();
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
            Stopwatch mergeStopwatch = Stopwatch.StartNew();
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
        Stopwatch cacheStopwatch = Stopwatch.StartNew();
        setRankingScore();
        cacheStopwatch.Stop();
        result.CacheMs = cacheStopwatch.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// 現在の deferred ranking refresh 要求が新しい要求で上書きされたかどうかを返します。
    /// </summary>
    /// <param name="requestVersion">確認対象の版数。</param>
    /// <returns>新しい要求が存在する場合は <see langword="true"/>。</returns>
    private bool IsDeferredRankingRefreshRequestSuperseded(int requestVersion)
    {
        lock (lockDeferredRankingRefresh)
        {
            return requestVersion != deferredRankingRefreshRequestedVersion;
        }
    }

    private List<string> getBMSDirectories()
    {
        if (lr2config != null)
        {
            try
            {
                SearchTargets = lr2config().GetBMSSearchDirectories();
            }
            catch
            {
                SearchTargets = new List<string>();
            }
        }
        HashSet<string> excludedRootCustomOutputDirs = BuildExcludedRootCustomOutputDirectories();
        return SearchTargets
            .Where((string d) => Directory.Exists(d))
            .Where((string d) => !excludedRootCustomOutputDirs.Contains(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .ToList();
    }

    private HashSet<string> BuildExcludedRootCustomOutputDirectories()
    {
        HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string rootBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        if (string.IsNullOrWhiteSpace(rootBaseDir))
        {
            return excluded;
        }
        string normalizedRootBaseDir;
        try
        {
            normalizedRootBaseDir = Path.GetFullPath(rootBaseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return excluded;
        }
        foreach (string searchTarget in SearchTargets ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(searchTarget))
            {
                continue;
            }
            try
            {
                string normalizedSearchTarget = Path.GetFullPath(searchTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parentDirectory = Path.GetDirectoryName(normalizedSearchTarget);
                if (!string.IsNullOrWhiteSpace(parentDirectory)
                    && string.Equals(parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRootBaseDir, StringComparison.OrdinalIgnoreCase))
                {
                    excluded.Add(normalizedSearchTarget);
                }
            }
            catch
            {
            }
        }
        return excluded;
    }

    private static bool IsBMSHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }

    private static bool IsBMSHashAvailable(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return false;
        }
        return IsBMSHashAvailable(bmsFile.hash);
    }

    private bool IsPendingPackageContainingOnlyInstalledCharts(BMSPackage package)
    {
        if (package == null)
        {
            return false;
        }
        List<BMSFile> list = package.BMSFiles.Where((BMSFile bmsFile) => bmsFile != null).ToList();
        if (list.Count == 0)
        {
            return false;
        }
        return list.All(ContainsInstalledChartUnsafe);
    }

    /// <summary>
    /// BMS ハッシュインデックスをクリアし、次回使用時に再構築されるようにマークします。
    /// </summary>
    private void InvalidateBMSHashIndex()
    {
        lock (lockBMSHashIndex)
        {
            bmsHashRefCount.Clear();
            bmsHashIndex.Clear();
            bmsHashIndexInitialized = false;
        }
        lock (lockPlaylistSummaryOwnedHashSnapshot)
        {
            playlistSummaryOwnedHashSnapshot = null;
        }
    }

    /// <summary>
    /// インストール済みディレクトリインデックスをクリアし、次回使用時に再構築されるようにマークします。
    /// </summary>
    private void InvalidateInstalledDirectoryIndex()
    {
        lock (lockInstalledDirectoryIndex)
        {
            installedDirectoryIndex = new InstalledChartDirectoryIndexSnapshot();
            installedDirectoryIndexInitialized = false;
        }
        InvalidateInstallEstimationMetadataProfileCache();
    }

    private void InvalidateInstalledChartKeyIndex()
    {
        lock (lockInstalledChartKeyIndex)
        {
            installedChartKeyIndex.Clear();
            installedChartKeyIndexInitialized = false;
        }
    }

    private void InvalidateInstallEstimationMetadataProfileCache()
    {
        lock (lockInstallEstimationMetadataProfileCache)
        {
            installEstimationMetadataProfileCache.Clear();
        }
    }

    /// <summary>
    /// BMS ファイル群から MD5 ハッシュの参照カウントインデックスを再構築します。
    /// </summary>
    private void RebuildBMSHashIndexUnsafe(IEnumerable<BMSFile> bmsFiles)
    {
        lock (lockBMSHashIndex)
        {
            bmsHashRefCount.Clear();
            bmsHashIndex.Clear();
            if (bmsFiles != null)
            {
                foreach (BMSFile bmsFile in bmsFiles)
                {
                    if (!IsBMSHashAvailable(bmsFile))
                    {
                        continue;
                    }
                    if (!bmsHashRefCount.TryGetValue(bmsFile.hash, out var value))
                    {
                        value = 0;
                    }
                    bmsHashRefCount[bmsFile.hash] = value + 1;
                }
                bmsHashIndex = new HashSet<string>(bmsHashRefCount.Keys, StringComparer.OrdinalIgnoreCase);
            }
            bmsHashIndexInitialized = true;
        }
    }

    private void RebuildInstalledChartKeyIndexUnsafe()
    {
        lock (lockInstalledChartKeyIndex)
        {
            installedChartKeyIndex.Clear();
            foreach (BMSFile bmsFile in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                string key = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    installedChartKeyIndex.Add(key);
                }
            }
            foreach (LR2SongDBExtended.bmson_song bmsonSong in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            {
                string key2 = PendingChartEntry.GetPrimaryLookupHash(bmsonSong);
                if (!string.IsNullOrWhiteSpace(key2))
                {
                    installedChartKeyIndex.Add(key2);
                }
            }
            installedChartKeyIndexInitialized = true;
        }
    }

    /// <summary>
    /// playlist summary 集計用の所持譜面ハッシュ snapshot を返します。
    /// BMSFiles 変更時に無効化し、次回要求時にだけ再構築します。
    /// </summary>
    internal PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot()
    {
        PlaylistSummaryOwnedHashSnapshot snapshot;
        lock (lockPlaylistSummaryOwnedHashSnapshot)
        {
            snapshot = playlistSummaryOwnedHashSnapshot;
        }
        if (snapshot != null)
        {
            return snapshot;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        HashSet<string> md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<BMSFile> bmsFilesSnapshot;
        List<LR2SongDBExtended.bmson_song> bmsonSongsSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile file) => file != null).ToList());
            bmsonSongsSnapshot = ((BmsonSongs == null) ? new List<LR2SongDBExtended.bmson_song>() : BmsonSongs.Where((LR2SongDBExtended.bmson_song song) => song != null).ToList());
        }
        foreach (BMSFile item in bmsFilesSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(item.hash))
            {
                md5Hashes.Add(item.hash);
            }
            if (!string.IsNullOrWhiteSpace(item.sha256))
            {
                sha256Hashes.Add(item.sha256);
            }
        }
        foreach (LR2SongDBExtended.bmson_song item2 in bmsonSongsSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(item2.md5))
            {
                md5Hashes.Add(item2.md5);
            }
            if (!string.IsNullOrWhiteSpace(item2.sha256))
            {
                sha256Hashes.Add(item2.sha256);
            }
        }
        PlaylistSummaryOwnedHashSnapshot rebuiltSnapshot = new PlaylistSummaryOwnedHashSnapshot
        {
            Version = Interlocked.Increment(ref playlistSummaryOwnedHashSnapshotVersion),
            BuildElapsedMs = stopwatch.ElapsedMilliseconds,
            Md5Hashes = md5Hashes,
            Sha256Hashes = sha256Hashes
        };
        lock (lockPlaylistSummaryOwnedHashSnapshot)
        {
            if (playlistSummaryOwnedHashSnapshot == null)
            {
                playlistSummaryOwnedHashSnapshot = rebuiltSnapshot;
            }
            return playlistSummaryOwnedHashSnapshot;
        }
    }

    /// <summary>
    /// ハッシュインデックスが未構築の場合にビルドします。
    /// </summary>
    private void EnsureBMSHashIndexBuiltUnsafe()
    {
        if (!bmsHashIndexInitialized)
        {
            RebuildBMSHashIndexUnsafe(BMSFiles);
        }
    }

    private void EnsureInstalledChartKeyIndexBuiltUnsafe()
    {
        if (installedChartKeyIndexInitialized)
        {
            return;
        }
        RebuildInstalledChartKeyIndexUnsafe();
    }

    /// <summary>
    /// BMS ファイル群から MD5 ハッシュ⇒インストール済みディレクトリのマップを再構築します。
    /// </summary>
    private void RebuildInstalledDirectoryIndexUnsafe(IEnumerable<BMSFile> bmsFiles)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int num = 0;
        foreach (BMSFile item in bmsFiles ?? Enumerable.Empty<BMSFile>())
        {
            num++;
        }
        InstalledChartDirectoryIndexSnapshot dictionary2 = CreateInstallEstimationService().BuildInstalledHashToDirectoryMap(bmsFiles, BmsonSongs);
        int num2 = dictionary2.DirectoryReferenceCount;
        lock (lockInstalledDirectoryIndex)
        {
            RebuildInstalledDirectoryIndexCoreUnsafe(dictionary2);
        }
        stopwatch.Stop();
        LogInstallPerformance("installed_dir_index rebuildMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + dictionary2.HashCount + " dirRefs=" + num2 + " files=" + num + " bmson=" + (BmsonSongs?.Count ?? 0));
    }

    private void RebuildInstalledDirectoryIndexCoreUnsafe(InstalledChartDirectoryIndexSnapshot snapshot)
    {
        installedDirectoryIndex = snapshot ?? new InstalledChartDirectoryIndexSnapshot();
        installedDirectoryIndexInitialized = true;
    }

    /// <summary>
    /// インストール済みディレクトリインデックスが未構築の場合にビルドします。
    /// </summary>
    private void EnsureInstalledDirectoryIndexBuiltUnsafe()
    {
        lock (lockInstalledDirectoryIndex)
        {
            if (installedDirectoryIndexInitialized)
            {
                return;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<BMSFile> bmsFilesSnapshot = (BMSFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            InstalledChartDirectoryIndexSnapshot snapshot = CreateInstallEstimationService().BuildInstalledHashToDirectoryMap(bmsFilesSnapshot, BmsonSongs);
            RebuildInstalledDirectoryIndexCoreUnsafe(snapshot);
            stopwatch.Stop();
            LogInstallPerformance("installed_dir_index rebuildMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + snapshot.HashCount + " dirRefs=" + snapshot.DirectoryReferenceCount + " files=" + bmsFilesSnapshot.Count + " bmson=" + (BmsonSongs?.Count ?? 0) + " singleFlight=true");
        }
    }

    /// <summary>
    /// 現在のインストール済みディレクトリインデックスのスナップショットを作成します。
    /// </summary>
    private InstalledChartDirectoryIndexSnapshot CreateInstalledDirectoryIndexSnapshotUnsafe()
    {
        EnsureInstalledDirectoryIndexBuiltUnsafe();
        lock (lockInstalledDirectoryIndex)
        {
            return installedDirectoryIndex.Clone();
        }
    }

    /// <summary>
    /// 指定の MD5 ハッシュを持つファイルがライブラリに存在するかを確認します。
    /// </summary>
    private bool ContainsBMSHashUnsafe(string hash)
    {
        if (!IsBMSHashAvailable(hash))
        {
            return false;
        }
        EnsureBMSHashIndexBuiltUnsafe();
        lock (lockBMSHashIndex)
        {
            return bmsHashIndex.Contains(hash);
        }
    }

    private bool ContainsInstalledChartUnsafe(BMSFile file)
    {
        if (file == null)
        {
            return false;
        }
        string lookupKey = PendingChartEntry.GetPrimaryLookupHash(file);
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return false;
        }
        EnsureInstalledChartKeyIndexBuiltUnsafe();
        lock (lockInstalledChartKeyIndex)
        {
            return installedChartKeyIndex.Contains(lookupKey);
        }
    }

    private HashSet<string> CreateKnownChartDirectorySnapshotUnsafe()
    {
        HashSet<string> knownChartDirectories = new HashSet<string>((bmsFolderAllFileList?.Keys ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile bmsFile in BMSFiles ?? Enumerable.Empty<BMSFile>())
        {
            string path = null;
            try
            {
                path = !string.IsNullOrWhiteSpace(bmsFile?.folder)
                    ? bmsFile.folder
                    : DirectoryExt.GetDirectoryNameSimple(bmsFile?.path);
            }
            catch
            {
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                knownChartDirectories.Add(path);
            }
        }
        foreach (LR2SongDBExtended.bmson_song bmsonSong in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            string path = null;
            try
            {
                path = !string.IsNullOrWhiteSpace(bmsonSong?.folder)
                    ? bmsonSong.folder
                    : DirectoryExt.GetDirectoryNameSimple(bmsonSong?.path);
            }
            catch
            {
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                knownChartDirectories.Add(path);
            }
        }
        return knownChartDirectories;
    }

    /// <summary>
    /// 指定された BMS ファイル群を除外したハッシュセットのスナップショットを生成します。
    /// 重複検出等において、自身を除いた状態でハッシュの存在確認を行う際に使用されます。
    /// </summary>
    private HashSet<string> CreateBMSHashSnapshotExcludingUnsafe(IEnumerable<BMSFile> excluded)
    {
        EnsureBMSHashIndexBuiltUnsafe();
        Dictionary<string, int> excludedHashCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (excluded != null)
        {
            foreach (BMSFile item in excluded)
            {
                if (!IsBMSHashAvailable(item))
                {
                    continue;
                }
                if (!excludedHashCount.TryGetValue(item.hash, out var value))
                {
                    value = 0;
                }
                excludedHashCount[item.hash] = value + 1;
            }
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (lockBMSHashIndex)
        {
            foreach (KeyValuePair<string, int> item2 in bmsHashRefCount)
            {
                int num = 0;
                excludedHashCount.TryGetValue(item2.Key, out num);
                if (item2.Value > num)
                {
                    hashSet.Add(item2.Key);
                }
            }
        }
        return hashSet;
    }

    private HashSet<string> CreateInstalledChartKeySnapshotExcludingUnsafe(IEnumerable<BMSFile> excluded)
    {
        Dictionary<string, int> excludedKeyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (excluded != null)
        {
            foreach (BMSFile item in excluded.Where((BMSFile file) => file != null))
            {
                string key = PendingChartEntry.GetPrimaryLookupHash(item);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    excludedKeyCount[key] = excludedKeyCount.TryGetValue(key, out int value) ? value + 1 : 1;
                }
            }
        }
        Dictionary<string, int> installedKeyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile bmsFile in BMSFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (bmsFile == null)
            {
                continue;
            }
            string key2 = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
            if (!string.IsNullOrWhiteSpace(key2))
            {
                installedKeyCounts[key2] = installedKeyCounts.TryGetValue(key2, out int value2) ? value2 + 1 : 1;
            }
        }
        foreach (LR2SongDBExtended.bmson_song bmsonSong in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            string key3 = PendingChartEntry.GetPrimaryLookupHash(bmsonSong);
            if (!string.IsNullOrWhiteSpace(key3))
            {
                installedKeyCounts[key3] = installedKeyCounts.TryGetValue(key3, out int value3) ? value3 + 1 : 1;
            }
        }
        HashSet<string> snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> installedKeyCount in installedKeyCounts)
        {
            int excludedCount = excludedKeyCount.TryGetValue(installedKeyCount.Key, out int value5) ? value5 : 0;
            if (installedKeyCount.Value - excludedCount > 0)
            {
                snapshot.Add(installedKeyCount.Key);
            }
        }
        return snapshot;
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
        BmsScoreApplyMetrics metrics = default(BmsScoreApplyMetrics);
        if (bmsFiles == null || lr2ScoreDBPath == null)
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
                List<BMSFile> normalizedFiles = bmsFiles.Where((BMSFile file) => file != null).ToList();
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
            if (snapshot == null || snapshot.ScoresByHash == null || snapshot.ScoresByHash.Count == 0)
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
                metrics.MatchedScoreCount = irService.ApplyKnownScoresToFilesAndCount(effectiveFiles, snapshot.ScoresByHash);
                metrics.ApplyKnownScoresMs = stageStopwatch.ElapsedMilliseconds;
                metrics.TotalMs = totalStopwatch.ElapsedMilliseconds;
            }
            else
            {
                irService.ApplyKnownScoresToFilesAndCount(effectiveFiles, snapshot.ScoresByHash);
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
                bmsFilesSnapshot = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile f) => f != null).ToList());
            }
            irService.ApplyIrDataToScoresAndFiles(data, cache, lr2ScoreDBPath, BMSScores, bmsFilesSnapshot, options.SkipEstimateOfflineScoreRanking);
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
        if (lr2ScoreDBPath == null || LR2ID == 0 || scoreTable == null)
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
    }

    private void ClearScoreUnsentStatus()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (BMSFile file in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                if (file != null)
                {
                    file.status &= ~BMSFile.BMSFileStatus.SCORE_UNSENT;
                }
            }
        }
    }

    private IrCacheRefreshResult setRankingScore()
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        IrCacheRefreshResult result = new IrCacheRefreshResult();
        if (lr2ScoreDBPath == null || LR2ID == 0)
        {
            return result;
        }
        LogInstallPerformance("ranking_cache_refresh start lr2Id=" + LR2ID);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            BmsLibraryIrService.IrCacheRefreshPlan preparedPlan = irService.PrepareRankingScoresRefreshPlanForLibrary(LR2ID, lr2ScoreDBPath, dbGateway);
            using (rwlockBMSScores.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    result = irService.ApplyPreparedRankingScoresRefreshPlanForLibrary(preparedPlan, lr2ScoreDBPath, BMSScores, BMSFiles, options.SkipEstimateOfflineScoreRanking);
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
            + " dbApplyCount=" + result.DbFallbackAppliedCount
            + " xmlApplyCount=" + result.XmlAppliedCount
            + " upsertRows=" + result.IrDataUpsertCount
            + " upsertMs=" + result.UpsertMs
            + " offlineEstimateXmlLoads=" + result.OfflineEstimateXmlLoadCount);
        RefreshScoreSnapshotFromCurrentScores("refresh_ranking_cache");
        return result;
    }

    public List<IRDataCacheInfo> GetIRDataNeedUpdates(IEnumerable<string> md5s)
    {
        if (lr2ScoreDBPath == null || LR2ID == 0)
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
        if (lr2ScoreDBPath == null || LR2ID == 0)
        {
            throw new InvalidOperationException(Resources.Error_LR2ScoreDBNotConnected);
        }
        if (cacheInfo == null)
        {
            throw new ArgumentNullException("cacheInfo");
        }
        string irCacheDirPath = Path.Combine(Path.GetDirectoryName(lr2ScoreDBPath), "..\\..\\Ir");
        if (!Directory.Exists(irCacheDirPath))
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
                    failed = irService.DownloadIRData(LR2ID, cacheInfo, irCacheDirPath, dbGateway, irClient, rankingDataUrl, BMSScores, BMSFiles, options.SkipEstimateOfflineScoreRanking);
                }
            }
        }
        RefreshScoreSnapshotFromCurrentScores("download_ir_data");
        return failed;
    }

    public IRSongInfo GetIRSongInfoCache(string md5orlr2bmsid, bool seaarchAggressively = false)
    {
        return irService.GetIRSongInfoCache(md5orlr2bmsid, seaarchAggressively, irClient, songInfoUrl);
    }

    /// <summary>
    /// BMS ファイル群の保守情報（ファイル存在チェック、エンコーディング検出等）を設定し、必要に応じて DB に永続化します。
    /// </summary>
    private List<BMSFile> CreateResourceMaintenanceTargets(IEnumerable<BMSFile> bmsFiles, bool includeInstalledBmson)
    {
        List<BMSFile> targets = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null)
            .ToList();
        if (!includeInstalledBmson)
        {
            return targets;
        }
        foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            PendingChartEntry row = PendingChartEntry.CreateFromBmsonSong(song);
            if (row != null)
            {
                targets.Add(row);
            }
        }
        return targets;
    }

    private void InvalidateResourceHealthIndex(string reason)
    {
        _ = reason;
        Volatile.Write(ref resourceHealthIndexInvalidated, true);
    }

    private bool IsResourceHealthIndexCurrent()
    {
        return !Volatile.Read(ref resourceHealthIndexInvalidated)
            && Volatile.Read(ref resourceHealthIndexSnapshot) != null;
    }

    private ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshot(string reason)
    {
        ResourceHealthIndexSnapshot currentSnapshot = Volatile.Read(ref resourceHealthIndexSnapshot);
        if (!Volatile.Read(ref resourceHealthIndexInvalidated) && currentSnapshot != null)
        {
            return currentSnapshot;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return RebuildResourceHealthIndexSnapshotLocked(reason);
            }
        }
    }

    private ResourceHealthIndexSnapshot RebuildResourceHealthIndexSnapshotLocked(string reason)
    {
        List<BMSFile> targets = CreateResourceMaintenanceTargets(BMSFiles, includeInstalledBmson: true);
        int version = Interlocked.Increment(ref resourceHealthIndexVersionSeed);
        ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version);
        lock (resourceHealthIndexLock)
        {
            resourceHealthIndexSnapshot = snapshot;
            Volatile.Write(ref resourceHealthIndexInvalidated, false);
        }
        LogInstallPerformance("resource_health_index_build reason=" + (reason ?? "unknown")
            + " version=" + snapshot.Version
            + " targetCount=" + snapshot.TargetCount
            + " needFix=" + snapshot.NeedFixCount
            + " ignored=" + snapshot.IgnoredCount
            + " buildMs=" + snapshot.BuildMs);
        return snapshot;
    }

    internal ResourceHealthWarningProjection GetResourceHealthWarningProjection(BMSFile chartFile)
    {
        return GetResourceHealthIndexSnapshot("projection_read").GetProjection(chartFile);
    }

    internal ResourceHealthWarningProjection GetResourceHealthWarningProjection(LR2SongDBExtended.bmson_song bmsonSong)
    {
        return GetResourceHealthIndexSnapshot("projection_read").GetProjection(bmsonSong);
    }

    internal ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshotForView(string reason)
    {
        return GetResourceHealthIndexSnapshot(reason);
    }

    private MaintenanceWorkflowResult setMaintenanceInfo(
        IEnumerable<BMSFile> bmsFiles,
        bool forceUpdate = false,
        bool includeInstalledBmson = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        if (bmsFiles == null)
        {
            return new MaintenanceWorkflowResult();
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> maintenanceTargets = CreateResourceMaintenanceTargets(bmsFiles, includeInstalledBmson);
            LogInstallPerformance("maintenance_update start inputCount=" + maintenanceTargets.Count
                + " forceUpdate=" + forceUpdate
                + " includeInstalledBmson=" + includeInstalledBmson);
            MaintenanceWorkflowResult workflowResult;
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                ResourceHealthLookupContext resourceLookupContext = new ResourceHealthLookupContext(bmsFolderAllFileList, directoryResourceLookupCache, directoryRelativePathHashIndex);
                workflowResult = maintenanceService.UpdateMaintenanceInfo(maintenanceTargets, forceUpdate, bmsFolderAllFileList, dbGateway, dialogService, resourceLookupContext, LogInstallPerformance, progressReporter, cancellationToken);
            }
            bool rebuildResourceHealthIndex = workflowResult.HasUpdates || !IsResourceHealthIndexCurrent();
            ResourceHealthIndexSnapshot resourceHealthSnapshot = rebuildResourceHealthIndex
                ? RebuildResourceHealthIndexSnapshotLocked("setMaintenanceInfo")
                : Volatile.Read(ref resourceHealthIndexSnapshot) ?? ResourceHealthIndexSnapshot.Empty;
            workflowResult.ResourceHealthIndexMs = rebuildResourceHealthIndex ? resourceHealthSnapshot.BuildMs : 0L;
            workflowResult.WarningReapplyTargets = 0;
            workflowResult.WarningChangedCount = 0;
            if (workflowResult.CheckedFileCount > 0 || workflowResult.BmsonReparsedCount > 0 || workflowResult.BmsonReparseFailedCount > 0 || workflowResult.BmsonResourceReferenceReusedCount > 0 || resourceHealthSnapshot.TargetCount > 0)
            {
                LogInstallPerformance("maintenance_update checked=" + workflowResult.CheckedFileCount
                    + " bmsResourceTargets=" + workflowResult.BmsResourceTargetCount
                    + " bmsonResourceTargets=" + workflowResult.BmsonResourceTargetCount
                    + " healthTargetCount=" + workflowResult.HealthTargetCount
                    + " healthDegree=" + workflowResult.HealthDegree
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
                    + " bmsonReparsed=" + workflowResult.BmsonReparsedCount
                    + " bmsonReparseFailed=" + workflowResult.BmsonReparseFailedCount
                    + " bmsonResourceRefsReused=" + workflowResult.BmsonResourceReferenceReusedCount
                    + " songReloaded=" + workflowResult.ReloadedSongCount
                    + " resourceHealthIndexMs=" + workflowResult.ResourceHealthIndexMs
                    + " warningReapplyTargets=" + workflowResult.WarningReapplyTargets
                    + " warningChanged=" + workflowResult.WarningChangedCount
                    + " canceled=" + workflowResult.Canceled.ToString().ToLowerInvariant()
                    + " elapsedMs=" + workflowResult.TotalMs);
            }
            Task.Run(delegate
            {
                RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
                RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
                if (workflowResult.HasUpdates)
                {
                    RaisePropertyChanged(() => BMSFilesGarbled);
                    RaisePropertyChanged(() => BMSFilesGarbledFixed);
                }
            }).Logging("setMaintenanceInfo");
            return workflowResult;
        }
    }

    private bool checkBMSFileNeedToBeFixedAndSetWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo mtInfo = null, bool strictCheck = false)
    {
        return maintenanceService.ApplyNeedToBeFixedWarnings(bmsFile, mtInfo, strictCheck);
    }

    /// <summary>
    /// ファイルが欠損・破損等で修復が必要な BMS ファイルの一覧を取得します。
    /// </summary>
    public List<BMSFile> GetBMSFilesNeedToBeFixed(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false, bool isInIgnoredList = false)
    {
        return GetChartsNeedResourceFix(bmsFiles, forceUpdate, isInIgnoredList);
    }

    public List<BMSFile> GetChartsNeedResourceFix(IEnumerable<BMSFile> chartFiles, bool forceUpdate = false, bool isInIgnoredList = false)
    {
        bool includeInstalledBmson = chartFiles == null;
        if (chartFiles == null)
        {
            chartFiles = BMSFiles;
        }
        _ = rwlockBMSFilesInitializedMin.IsWriteLockHeld;
        _ = rwlockBMSFiles.IsWriteLockHeld;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (forceUpdate)
                {
                    RescanResourceHealthCharts(chartFiles, includeInstalledBmson);
                }
                ResourceHealthIndexSnapshot snapshot = GetResourceHealthIndexSnapshot(forceUpdate ? "force_resource_health_filter" : "resource_health_filter");
                if (includeInstalledBmson)
                {
                    return (isInIgnoredList ? snapshot.IgnoredFiles : snapshot.ActiveFiles).ToList();
                }
                List<BMSFile> targets = CreateResourceMaintenanceTargets(chartFiles, includeInstalledBmson);
                if (targets.Count == 0)
                {
                    return new List<BMSFile>();
                }
                return targets.Where((BMSFile file) =>
                {
                    ResourceHealthWarningProjection projection = snapshot.GetProjection(file);
                    return projection.HasIssues && projection.IsIgnored == isInIgnoredList;
                }).ToList();
            }
        }
    }

    /// <summary>
    /// 指定された譜面の構成ファイル health を明示的に再計算します。
    /// 通常の hydration とは異なり、ユーザー操作に基づいて実ファイル確認と DB 更新を行います。
    /// </summary>
    /// <param name="chartFiles">再スキャン対象。null の場合は全所持譜面を対象にします。</param>
    /// <param name="includeInstalledBmson">installed bmson も対象に含めるかどうか。</param>
    /// <param name="progressReporter">section 単位の進捗通知。</param>
    /// <param name="cancellationToken">section 境界で確認するキャンセル token。</param>
    /// <returns>再スキャン結果。</returns>
    internal MaintenanceWorkflowResult RescanResourceHealthCharts(
        IEnumerable<BMSFile> chartFiles,
        bool includeInstalledBmson,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<BMSFile> targets = chartFiles ?? BMSFiles;
        MaintenanceWorkflowResult result = setMaintenanceInfo(targets, forceUpdate: true, includeInstalledBmson, progressReporter, cancellationToken);
        RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
        RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
        RaisePropertyChanged(() => BMSFilesGarbled);
        RaisePropertyChanged(() => BMSFilesGarbledFixed);
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
        return RescanResourceHealthCharts(BMSFiles, includeInstalledBmson: true, progressReporter, cancellationToken);
    }

    /// <summary>
    /// 指定された BMS ファイル群の保守警告を無視リストに追加（または解除）し、DB に反映します。
    /// </summary>
    public void SetBMSFilesToBeFixedIgnored(IEnumerable<BMSFile> bmsFiles, bool unset = false)
    {
        SetChartResourceWarningsIgnored(bmsFiles, unset);
    }

    public void SetChartResourceWarningsIgnored(IEnumerable<BMSFile> chartFiles, bool unset = false)
    {
        bool includeInstalledBmson = chartFiles == null;
        if (chartFiles == null)
        {
            chartFiles = BMSFiles;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<BMSFile> targets = CreateResourceMaintenanceTargets(chartFiles, includeInstalledBmson);
                if (targets.Count == 0)
                {
                    return;
                }
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    List<BMSFileMaintenanceInfo> changes = maintenanceService.SetFilesWarningIgnored(targets, unset);
                    dbGateway.UpsertMaintenanceInfos(changes);
                }
                RebuildResourceHealthIndexSnapshotLocked(unset ? "resource_health_unignore" : "resource_health_ignore");
            }
        }
        RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
        RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
    }

    /// <summary>
    /// エンコーディングが Shift_JIS 以外と推定された（文字化けの可能性がある）BMS ファイル群を取得します。
    /// </summary>
    public List<BMSFile> GetBMSFilesGarbled(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false, bool isInFixedList = false)
    {
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        if (bmsFiles.Count() == 0)
        {
            return new List<BMSFile>();
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (forceUpdate)
                {
                    setMaintenanceInfo(bmsFiles, forceUpdate);
                }
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
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                MaintenanceEncodingUpdateResult updateResult = maintenanceService.ApplyEncoding(bmsFiles, encoding);
                if (updateResult.SongsToUpsert.Count > 0)
                {
                    dbGateway.UpsertSongs(updateResult.SongsToUpsert);
                }
                if (updateResult.MaintenanceInfosToUpsert.Count > 0)
                {
                    using (rwlockSongDBMaintenance.GetWriterGuard())
                    {
                        dbGateway.UpsertMaintenanceInfos(updateResult.MaintenanceInfosToUpsert);
                    }
                }
            }
        }
    }

    /// <summary>
    /// chart_info 上のノート数が 0 のファイルについて、実際のファイルを再確認して可視ノート風記述がないか検出します。
    /// </summary>
    public void RecheckZeroNoteWarnings()
    {
        List<BMSFile> allFiles;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            allFiles = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile f) => f != null).ToList());
        }
        ZeroNoteRecheckResult result = maintenanceService.RecheckZeroNoteWarnings(allFiles, (ex, message) => NLogWrapper.FileLogger?.Warn(ex, message));
        if (result.ChangedCount > 0)
        {
            RaisePropertyChanged(() => BMSFilesZeroNote);
        }
        NLogWrapper.FileLogger?.Info(string.Format("zero_note_recheck total={0} mismatch={1} cleared={2} skipped={3} changed={4}", result.Total, result.MismatchCount, result.ClearedCount, result.SkippedCount, result.ChangedCount));
    }

    /// <summary>
    /// ノート数が 0 の BMS ファイルの一覧を取得します。
    /// </summary>
    public List<BMSFile> GetBMSFilesZeroNote(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        if (bmsFiles.Count() == 0)
        {
            return new List<BMSFile>();
        }
        return maintenanceService.GetZeroNoteFiles(bmsFiles);
    }

    public List<BMSFile> GetBMSFilesChartInfoParseFailed()
    {
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures = dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout);
        if (failures.Count == 0)
        {
            return new List<BMSFile>();
        }
        List<BMSFile> bmsSnapshot;
        List<LR2SongDBExtended.bmson_song> bmsonSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            bmsonSnapshot = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Where((LR2SongDBExtended.bmson_song song) => song != null).ToList();
        }
        List<BMSFile> result = new List<BMSFile>();
        foreach (BMSFile file in bmsSnapshot)
        {
            if (string.IsNullOrWhiteSpace(file.hash) || !failures.TryGetValue(file.hash, out LR2SongDBExtended.chart_info_parse_failure failure))
            {
                continue;
            }
            PendingChartEntry entry = PendingChartEntry.CreateFromBmsFile(file);
            ApplyChartInfoParseFailureWarning(entry, failure);
            result.Add(entry);
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSnapshot)
        {
            if (string.IsNullOrWhiteSpace(song.md5) || !failures.TryGetValue(song.md5, out LR2SongDBExtended.chart_info_parse_failure failure))
            {
                continue;
            }
            PendingChartEntry entry = PendingChartEntry.CreateFromBmsonSong(song);
            ApplyChartInfoParseFailureWarning(entry, failure);
            result.Add(entry);
        }
        return result
            .Where((BMSFile file) => file != null)
            .OrderBy((BMSFile file) => file.path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyChartInfoParseFailureWarning(BMSFile file, LR2SongDBExtended.chart_info_parse_failure failure)
    {
        if (file == null || failure == null)
        {
            return;
        }
        file.SetWarning(ChartWarningKind.ChartInfoParseFailure, BuildChartInfoParseFailureWarningMessage(failure));
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
        string[] normalizedMd5s = NormalizeChartInfoParseFailureMd5s(md5s);
        if (normalizedMd5s.Length == 0)
        {
            return;
        }
        dbGateway.DeleteChartInfoParseFailuresByMd5(normalizedMd5s);
        RaisePropertyChanged(() => BMSFilesChartInfoParseFailed);
    }

    internal static string[] NormalizeChartInfoParseFailureMd5s(IEnumerable<string> md5s)
    {
        return (md5s ?? Enumerable.Empty<string>())
            .Where((string md5) => !string.IsNullOrWhiteSpace(md5))
            .Select((string md5) => md5.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// BMS ファイル群のモード（SP/DP等）を検出し、song.db にコミットします。
    /// </summary>
    private int setModeAndCommitToDB(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> list = maintenanceService.DetectModeChanges(bmsFiles, forceUpdate);
            if (list.Count <= 0)
            {
                return 0;
            }
            dbGateway.UpsertSongs(list);
            return list.Count;
        }
    }

    /// <summary>
    /// ライブラリ内の重複 BMS ファイルを検出し、Union-Find でディレクトリグループ化した結果を <see cref="BMSFilesDuplicated"/> に格納します。
    /// </summary>
    public void SearchBMSFilesDuplicated()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesDuplicated.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    if (BMSFilesDuplicated != null)
                    {
                        return;
                    }
                    List<BMSFile> bmsSnapshot = BMSFiles.Where((BMSFile f) => f != null).ToList();
                    List<LR2SongDBExtended.bmson_song> bmsonSnapshot = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Where((LR2SongDBExtended.bmson_song song) => song != null).ToList();
                    duplicateService.ClearDuplicateState(bmsSnapshot, DuplicateWarningMessage);
                    List<DuplicateChartRow> snapshot = duplicateService.BuildSnapshot(bmsSnapshot, bmsonSnapshot);
                    System.Diagnostics.Stopwatch swNew = System.Diagnostics.Stopwatch.StartNew();
                    DuplicateAnalysisResult analysis = duplicateService.Analyze(snapshot);
                    duplicateService.ApplyDuplicateWarnings(analysis.DuplicateFiles, DuplicateWarningMessage);
                    BMSFilesDuplicated = analysis.DuplicateGroups;
                    swNew.Stop();
                    LogInstallPerformance($"SearchBMSFilesDuplicated: NewAlgo={swNew.ElapsedMilliseconds}ms, Groups={analysis.DuplicateGroups.Count}");
                }
            }
        }
    }
    private static readonly string DuplicateWarningMessage = Resources.Warning_DuplicateBmsFile;

    /// <summary>
    /// 全 BMS ファイルの重複状態フラグと警告メッセージをクリアします。
    /// </summary>
    private static void ClearDuplicateState(IEnumerable<BMSFile> files)
    {
        foreach (BMSFile file in files)
        {
            file.ClearWarning(ChartWarningKind.DuplicateChart);
        }
    }

    /// <summary>
    /// BMS ファイルに重複警告メッセージを付与します。
    /// </summary>
    private static void SetDuplicateWarning(BMSFile file)
    {
        if (file == null)
        {
            return;
        }
        file.ClearWarning(ChartWarningKind.DuplicateChart);
        file.SetWarning(ChartWarningKind.DuplicateChart, DuplicateWarningMessage);
    }

    private sealed class InstalledChartMetadataCandidate
    {
        internal string Title { get; set; }

        internal string Artist { get; set; }

        internal string Path { get; set; }
    }

    private static bool IsChartPathWithinDestinationDirectory(string destinationDirectory, string chartPath)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory) || string.IsNullOrWhiteSpace(chartPath))
        {
            return false;
        }
        string chartDirectory = DirectoryExt.GetDirectoryNameSimple(chartPath);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return false;
        }
        if (string.Equals(chartDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return (chartDirectory + Path.DirectorySeparatorChar).StartsWith(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<InstalledChartMetadataCandidate> EnumerateInstalledChartMetadataCandidatesUnsafe(string normalizedDestinationDirectory)
    {
        return (BMSFiles ?? new List<BMSFile>())
            .Where((BMSFile file) => file != null && IsChartPathWithinDestinationDirectory(normalizedDestinationDirectory, file.path))
            .Select((BMSFile file) => new InstalledChartMetadataCandidate
            {
                Title = file.Title ?? string.Empty,
                Artist = file.Artist ?? string.Empty,
                Path = file.path ?? string.Empty
            })
            .Concat((BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>())
                .Where((LR2SongDBExtended.bmson_song song) => song != null && IsChartPathWithinDestinationDirectory(normalizedDestinationDirectory, song.path))
                .Select((LR2SongDBExtended.bmson_song song) => new InstalledChartMetadataCandidate
                {
                    Title = BmsonSongParser.ComposeDisplayTitle(song),
                    Artist = song.artist ?? string.Empty,
                    Path = song.path ?? string.Empty
                }));
    }

    private InstallDestinationRepresentativeMetadata ResolveInstallDestinationRepresentativeMetadataUnsafe(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return InstallDestinationRepresentativeMetadata.Empty;
        }
        string normalizedDestinationDirectory = destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        InstalledChartMetadataCandidate representativeChart = EnumerateInstalledChartMetadataCandidatesUnsafe(normalizedDestinationDirectory)
            .OrderByDescending((InstalledChartMetadataCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.Title))
            .ThenByDescending((InstalledChartMetadataCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.Artist))
            .ThenBy((InstalledChartMetadataCandidate candidate) => candidate.Path, StringComparer.OrdinalIgnoreCase)
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

        string normalizedDestinationDirectory = destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (lockInstallEstimationMetadataProfileCache)
        {
            if (installEstimationMetadataProfileCache.TryGetValue(normalizedDestinationDirectory, out InstallEstimationMetadataProfile cachedProfile))
            {
                return cachedProfile ?? InstallEstimationMetadataProfile.Empty;
            }
        }

        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile(
            EnumerateInstalledChartMetadataCandidatesUnsafe(normalizedDestinationDirectory)
                .Select((InstalledChartMetadataCandidate candidate) => (candidate.Title ?? string.Empty, candidate.Artist ?? string.Empty, candidate.Path ?? string.Empty)));

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

    private InstallDestinationRepresentativeMetadata ApplyResolvedInstallDestinationPathAndMetadataToFiles(IEnumerable<BMSFile> bmsFiles, string destinationDirectory)
    {
        InstallDestinationRepresentativeMetadata metadata = ResolveInstallDestinationRepresentativeMetadataUnsafe(destinationDirectory);
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.instl_dst = destinationDirectory;
            bmsFile.InstallDestinationTitle = metadata.Title;
            bmsFile.InstallDestinationArtist = metadata.Artist;
        }
        return metadata;
    }

    private void ApplyResolvedInstallDestinationToFiles(IEnumerable<BMSFile> bmsFiles, string destinationDirectory, bool preserveAmbiguousInstallContext = false)
    {
        ApplyResolvedInstallDestinationPathAndMetadataToFiles(bmsFiles, destinationDirectory);
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            if (!preserveAmbiguousInstallContext)
            {
                bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            }
            if (!preserveAmbiguousInstallContext)
            {
                bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            }
        }
    }

    private void ApplyInstallEstimationResultToFiles(IEnumerable<BMSFile> bmsFiles, InstallEstimationResult result)
    {
        InstallEstimationCandidate selectedCandidate = result?.SelectedCandidate;
        InstallEstimationCandidate secondCandidate = result?.SecondCandidate;
        string[] suggestionPaths = (result?.SuggestedDestinationDirectories ?? new List<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        bool isLowConfidence = result?.Confidence == InstallEstimationConfidence.Low;
        InstallEstimationLowConfidenceKind lowConfidenceKind = result?.LowConfidenceKind ?? InstallEstimationLowConfidenceKind.None;
        bool isLowConfidenceAmbiguous = isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.AmbiguousCandidates && suggestionPaths.Length >= 2;
        bool isLowConfidenceMetadataMismatch = isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.MetadataMismatch && suggestionPaths.Length >= 1;
        bool isLowConfidenceReinstallNotImproved = isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.ReinstallNotImproved && suggestionPaths.Length >= 1;
        string ambiguousWarning = selectedCandidate == null || secondCandidate == null
            ? string.Empty
            : string.Format(Resources.Warning_InstallEstimationAmbiguous, selectedCandidate.DirectoryPath, secondCandidate.DirectoryPath);
        string metadataMismatchWarning = selectedCandidate == null
            ? string.Empty
            : string.Format(Resources.Warning_InstallEstimationMetadataMismatch, selectedCandidate.DirectoryPath);
        string reinstallNotImprovedWarning = selectedCandidate == null
            ? string.Empty
            : string.Format(Resources.Warning_InstallEstimationReinstallNotImproved, selectedCandidate.DirectoryPath);
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            if (result?.ShouldAutoApplyDestination == true && !string.IsNullOrWhiteSpace(result.DestinationDirectory))
            {
                bmsFile.instl_dst = result.DestinationDirectory;
            }
            else
            {
                bmsFile.instl_dst = null;
            }
            if (result?.HasViableDestination == true)
            {
                bmsFile.InstallDestinationTitle = selectedCandidate?.RepresentativeTitle ?? string.Empty;
                bmsFile.InstallDestinationArtist = selectedCandidate?.RepresentativeArtist ?? string.Empty;
            }
            else
            {
                bmsFile.InstallDestinationTitle = string.Empty;
                bmsFile.InstallDestinationArtist = string.Empty;
            }
            bmsFile.InstallDestinationSuggestions = isLowConfidence ? suggestionPaths : Array.Empty<string>();
            if (isLowConfidenceAmbiguous && !string.IsNullOrWhiteSpace(ambiguousWarning))
            {
                bmsFile.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, ambiguousWarning);
            }
            else if (isLowConfidenceMetadataMismatch && !string.IsNullOrWhiteSpace(metadataMismatchWarning))
            {
                bmsFile.SetWarning(ChartWarningKind.InstallEstimationMetadataMismatch, metadataMismatchWarning);
            }
            else if (isLowConfidenceReinstallNotImproved && !string.IsNullOrWhiteSpace(reinstallNotImprovedWarning))
            {
                bmsFile.SetWarning(ChartWarningKind.InstallEstimationReinstallNotImproved, reinstallNotImprovedWarning);
            }
        }
    }

    private InstallEstimationEvaluationData EvaluateInstallEstimation(BMSPackage package, List<BMSFile> targetBmsFiles, bool asParallel, BmsInstallationEstimateMode estimateMode, BmsLibraryOptionsSnapshot optionsSnapshot = null, bool useThreadSafeResolvers = false, bool useSharedLazyHashMetrics = false, BMSDirectoryFileNameHash folderAllFileListSnapshot = null, DirectoryResourceLookupCache directoryLookupCacheSnapshot = null, DirectoryRelativePathHashIndex relativePathHashIndexSnapshot = null, PendingEstimateSourceBatchPackageState batchState = null)
    {
        List<BMSFile> targetFileList = (targetBmsFiles ?? new List<BMSFile>()).Where((BMSFile bmsInfo) => bmsInfo != null).ToList();
        if (targetFileList.Count == 0)
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
        BMSDirectoryFileNameHash effectiveFolderAllFileList = folderAllFileListSnapshot ?? bmsFolderAllFileList;
        DirectoryResourceLookupCache effectiveDirectoryLookupCache = directoryLookupCacheSnapshot ?? this.directoryResourceLookupCache;
        DirectoryRelativePathHashIndex effectiveRelativePathHashIndex = relativePathHashIndexSnapshot ?? directoryRelativePathHashIndex;
        long lazyHashBuildMsBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashBuildMs ?? 0L);
        long lazyHashLookupCountBefore = useSharedLazyHashMetrics ? 0L : (effectiveDirectoryLookupCache?.LazyHashLookupCount ?? 0L);
        int lazyHashCacheEntriesBefore = useSharedLazyHashMetrics ? 0 : (effectiveDirectoryLookupCache?.LazyHashCacheEntryCount ?? 0);
        bool sourceSurfaceBatchHit = batchState?.UsesBatchSourceSurface == true && batchState.SourceSurface != null;
        PackageInstallEstimationSnapshot estimationSnapshot;
        if (package != null && sourceSurfaceBatchHit)
        {
            bool includeBundledResources = Directory.Exists(package.path);
            PackageInstallSurfaceSnapshot sharedInstallSurface = PackageInstallEstimationSnapshotBuilder.BuildSharedInstallSurfaceSnapshot(
                package.path,
                batchState.SourceDirectory,
                batchState.SourceSurface,
                includeBundledResources);
            estimationSnapshot = PackageInstallEstimationSnapshotBuilder.Build(
                package,
                targetFileList,
                sharedInstallSurface,
                sourceSurfaceCacheHit: false,
                sourceSurfaceBatchHit: true);
        }
        else
        {
            estimationSnapshot = package != null
                ? package.GetOrBuildInstallEstimationSnapshot(targetFileList)
                : PackageInstallEstimationSnapshotBuilder.BuildForLooseFiles(targetFileList);
        }
        InstallEstimationResult result = CreateInstallEstimationService(optionsSnapshot).EstimateInstallationDirectory(
            estimationSnapshot,
            effectiveFolderAllFileList,
            effectiveDirectoryLookupCache,
            asParallel,
            estimateMode,
            representativeResolver,
            metadataProfileResolver,
            effectiveRelativePathHashIndex);
        long lazyHashBuildMsAfter = useSharedLazyHashMetrics ? lazyHashBuildMsBefore : (effectiveDirectoryLookupCache?.LazyHashBuildMs ?? lazyHashBuildMsBefore);
        long lazyHashLookupCountAfter = useSharedLazyHashMetrics ? lazyHashLookupCountBefore : (effectiveDirectoryLookupCache?.LazyHashLookupCount ?? lazyHashLookupCountBefore);
        int lazyHashCacheEntriesAfter = useSharedLazyHashMetrics ? lazyHashCacheEntriesBefore : (effectiveDirectoryLookupCache?.LazyHashCacheEntryCount ?? lazyHashCacheEntriesBefore);
        return new InstallEstimationEvaluationData
        {
            ChartCount = targetFileList.Count,
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
            SourceSurfaceScanBackend = estimationSnapshot?.SourceSurfaceScanBackend ?? string.Empty
        };
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
        LogInstallPerformance("estimate_install start chartCount=" + estimationData.ChartCount + " targetHashes=" + result.TargetResourceHashCount + " targetResources=" + result.TargetResourceCount + " pathAwareRefs=" + result.TargetPathAwareHashCount + " pathAwareAudioRefs=" + result.TargetPathAwareAudioHashCount + " pathAwareVisualRefs=" + result.TargetPathAwareVisualHashCount + " pathAwareMovieRefs=" + result.TargetPathAwareMovieHashCount + " pathAwareOptionalRefs=" + result.TargetPathAwareOptionalImageHashCount + " bundledAudioCount=" + result.BundledAudioCount + " bundledImageCount=" + result.BundledImageCount + " bundledMovieCount=" + result.BundledMovieCount + " evalMode=relative_strict candidateMode=" + (result.CandidateMode ?? string.Empty) + " coarseFilterMode=" + (result.CoarseFilterMode ?? string.Empty) + " candidateDegree=" + result.CandidateEvaluationDegree + " audioRefs=" + result.AudioReferenceCount + " visualRefs=" + result.VisualReferenceCount + " movieRefs=" + result.MovieReferenceCount + " optionalRefs=" + result.OptionalImageReferenceCount + " audioMinMatchRequired=" + result.AudioMinimumMatchRequired + " candidateDirsBefore=" + result.CandidateDirectoryCountBeforeHashFilter + " candidateDirsAfterBroadFilter=" + result.CandidateDirectoryCountAfterBroadFilter + " candidateDirsAfterAudioGate=" + result.CandidateDirectoryCountAfterAudioGate + " candidateDirsInHierarchy=" + result.HierarchyCandidateDirectoryCount + " shadowSuppressed=" + result.AncestorShadowSuppressedCount + " lazySelfOwnedCandidates=" + result.LazySelfOwnedEvaluationCount + " shadowMs=" + result.AncestorShadowEvaluationMs + " candidateViewBuildMs=" + result.CandidateViewBuildMs + " candidateMatchMs=" + result.CandidateMatchMs + " candidateViewBuildCount=" + result.CandidateViewBuildCount + " candidateViewFallbackCount=" + result.CandidateViewFallbackCount + " sourceSurfaceScanMs=" + estimationData.SourceSurfaceScanMs + " sourceSurfaceChartFileCount=" + estimationData.SourceSurfaceChartFileCount + " sourceSurfaceResourceFileCount=" + estimationData.SourceSurfaceResourceFileCount + " sourceSurfaceTrackedFileCount=" + estimationData.SourceSurfaceTrackedFileCount + " sourceSurfaceHashMaterializeMs=" + estimationData.SourceSurfaceHashMaterializeMs + " sourceSurfaceCacheHit=" + estimationData.SourceSurfaceCacheHit.ToString().ToLowerInvariant() + " sourceSurfaceBatchHit=" + estimationData.SourceSurfaceBatchHit.ToString().ToLowerInvariant() + " sourceSurfaceScanBackend=" + (estimationData.SourceSurfaceScanBackend ?? string.Empty) + " candidateDirsAfter=" + result.CandidateDirectoryCountAfterHashFilter + " candidateDirs=" + result.CandidateDirectoryCount + " evaluationMs=" + result.EvaluationMs + " fallback=" + result.UsedFallbackCandidateExpansion + " confidence=" + result.Confidence + " autoApplied=" + result.ShouldAutoApplyDestination + " confidenceReason=" + (result.ConfidenceReason ?? string.Empty) + " lazyHashBuildMsDelta=" + estimationData.LazyHashBuildMsDelta + " lazyHashEntriesAdded=" + estimationData.LazyHashEntriesAdded + " lazyHashLookupCountDelta=" + estimationData.LazyHashLookupCountDelta + " lazyHashBuildReason=" + (estimationData.LazyHashBuildReason ?? string.Empty) + " summary=" + (result.ResourceSummary ?? string.Empty));
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
    }

    /// <summary>
    /// 指定されたパス群（ファイルまたはディレクトリ）から BMS ファイルを自動検出・インストールします。
    /// アーカイブの展開、song.db への登録、Pendingパッケージ生成を一括で行います。
    /// </summary>
    /// <param name="installPaths">インストール元のファイル/ディレクトリパスのコレクション。</param>
    /// <returns>インストール処理された BMS パッケージのリスト。</returns>
    public List<BMSPackage> InstallBMSFilesAuto(IEnumerable<string> installPaths, CancellationToken token = default(CancellationToken), Action onEachSourceProcessed = null)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<BMSPackage> pendingPackagesToEstimate = new List<BMSPackage>();
        List<BMSPackage> deferredPendingEstimatePackages = new List<BMSPackage>();
        Dictionary<BMSPackage, int> deferredPendingEstimateHealthByPackage = new Dictionary<BMSPackage, int>();
        List<BMSPackage> registeredPackages = new List<BMSPackage>();
        List<string> regroupEligibleSourceDirectories = new List<string>();
        PendingEstimateSourceBatchSnapshot pendingBatchSourceSnapshot = null;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (installPaths == null || installPaths.Any((string path) => !Directory.Exists(path) && !File.Exists(path)))
                        {
                            dialogService.Show(Resources.Warn_InstallAbortedFilesNotFound, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            return registeredPackages;
                        }
                        if (token.IsCancellationRequested)
                        {
                            return registeredPackages;
                        }
                        installPaths = packageInstallService.ExpandInstallSources(
                            installPaths,
                            fileMutationService,
                            targetOnlyFileMutationOptions,
                            info => NLogWrapper.FileLogger?.Info(info),
                            dialogService,
                            onEachSourceProcessed,
                            token);
                        if (token.IsCancellationRequested)
                        {
                            return registeredPackages;
                        }
                        AutoInstallWorkflowResult workflow = packageInstallService.PrepareAutoInstallWorkflow(
                            installPaths,
                            BMSPackagesPending,
                            CreateKnownChartDirectorySnapshotUnsafe(),
                            ContainsInstalledChartUnsafe,
                            dupRateThreshInOnePkg,
                            (bmsFile) => checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile?.HasValidMaintenanceInfoSnapshot == true ? bmsFile.TryGetMaintenanceInfoWithoutCreating() : null, strictCheck: true),
                            token);
                        List<BMSPackage> discoveredPackages = workflow.DiscoveredPackages.ToList();
                        LogInstallPerformance("auto_install_prepare discovered=" + discoveredPackages.Count + " autoInstall=" + workflow.AutoInstallCandidates.Count + " pendingAdd=" + workflow.PendingPackagesToAdd.Count + " pendingRemove=" + workflow.PendingPackagesToRemove.Count + " discoveryMs=" + workflow.DiscoveryMs + " installedCheckMs=" + workflow.InstalledCheckMs + " warningClassifyMs=" + workflow.WarningClassificationMs + " classificationMs=" + workflow.ClassificationMs + " totalMs=" + workflow.TotalMs);
                        if (discoveredPackages.Count == 0 || token.IsCancellationRequested)
                        {
                            return registeredPackages;
                        }
                        AutoInstallApplyResult applyResult = packageInstallService.ApplyAutoInstallWorkflow(
                            workflow,
                            options.KeepInstallablePackagesPending,
                            SearchTargets != null && SearchTargets.Count() > 0 && Directory.Exists(SearchTargets[0]),
                            (packagesToInstall) => installBMSPackages(packagesToInstall),
                            token);
                        LogInstallPerformance("auto_install_apply pendingAdd=" + applyResult.PendingPackagesToAdd.Count + " pendingRemove=" + applyResult.PendingPackagesToRemove.Count + " autoInstalled=" + applyResult.AutoInstalledPackages.Count + " autoFailed=" + applyResult.AutoInstallFailures.Count + " installMs=" + applyResult.InstallMs + " applyMs=" + applyResult.ApplyMs + " totalMs=" + applyResult.TotalMs);
                        if (applyResult.PendingPackagesToRemove.Count > 0)
                        {
                            stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: applyResult.PendingPackagesToRemove));
                        }
                        if (applyResult.InstallRowsToUpsert.Count > 0)
                        {
                            dbGateway.UpsertInstallRows(applyResult.InstallRowsToUpsert);
                            BMSPackagesPending.AddRange(applyResult.PendingPackagesToAdd);
                        }
                        BackgroundPendingEstimatePreparationResult estimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(applyResult.EstimateTargets, PendingInstallEstimateBatchSource.AutoInstall);
                        pendingPackagesToEstimate = estimatePreparation.EstimablePackages;
                        deferredPendingEstimatePackages = estimatePreparation.DeferredPackages;
                        deferredPendingEstimateHealthByPackage = estimatePreparation.DeferredSourceHealthByPackage;
                        pendingBatchSourceSnapshot = estimatePreparation.BatchSourceSnapshot;
                        regroupEligibleSourceDirectories = workflow.RegroupEligibleSourceDirectories.ToList();
                        registeredPackages = discoveredPackages;
                    }
                }
                foreach (BMSPackage deferredPackage in deferredPendingEstimatePackages)
                {
                    int sourceHealth = 0;
                    deferredPendingEstimateHealthByPackage.TryGetValue(deferredPackage, out sourceHealth);
                    LogPendingEstimateSkippedPackage("auto_install", deferredPackage, sourceHealth);
                }
                if (!token.IsCancellationRequested && pendingPackagesToEstimate.Count > 0)
                {
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(pendingPackagesToEstimate.FirstOrDefault()?.path);
                    QueuePendingInstallEstimateBatch(new PendingInstallEstimateBatchRequest(
                        PendingInstallEstimateBatchSource.AutoInstall,
                        pendingPackagesToEstimate,
                        displayName,
                        regroupEligibleSourceDirectories,
                        deferredPendingEstimatePackages.Count,
                        pendingBatchSourceSnapshot));
                }
            }
        }
        return registeredPackages;
    }

    private List<BMSPackage> searchBMSFilesRecursively(string dirfullpath, bool recursive = false)
    {
        return packageInstallService.SearchBmsFilesRecursively(dirfullpath, dupRateThreshInOnePkg, recursive);
    }

    private static ComponentMoveDecision DecideComponentMove(string srcFilePath, string dstFilePath)
    {
        return new BmsLibraryPackageInstallService().DecideComponentMove(srcFilePath, dstFilePath);
    }

    private static bool IsSamePath(string path1, string path2)
    {
        return new BmsLibraryPackageInstallService().IsSamePath(path1, path2);
    }

    private static ComponentMovePlanBuildResult BuildComponentMovePlan(IEnumerable<string> installComponentFiles, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        return new BmsLibraryPackageInstallService().BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths);
    }

    private void CleanupEmptyComponentDirectories(IEnumerable<string> installComponentDirectories)
    {
        foreach (string installComponentDirectory in installComponentDirectories.Where((string path) => Directory.Exists(path)).OrderByDescending((string path) => path.Length))
        {
            TryDeleteEmptyDirectoryTree(installComponentDirectory);
        }
    }

    private void TryDeleteEmptyDirectoryTree(string rootDirectoryPath)
    {
        try
        {
            if (!Directory.Exists(rootDirectoryPath))
            {
                return;
            }

            foreach (string childDirectoryPath in Directory.EnumerateDirectories(rootDirectoryPath).ToList())
            {
                TryDeleteEmptyDirectoryTree(childDirectoryPath);
            }

            if (!Directory.EnumerateFileSystemEntries(rootDirectoryPath).Any())
            {
                fileMutationService.DeleteDirectoryDirect(rootDirectoryPath, recursive: false, targetOnlyFileMutationOptions);
            }
        }
        catch
        {
        }
    }

    private static string GetDisplayedExceptionMessage(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            return string.Join(Environment.NewLine, aggregateException.Flatten().InnerExceptions.Select(GetDisplayedExceptionMessage));
        }

        if (exception is FileMutationException fileMutationException)
        {
            return fileMutationException.InnerException?.Message ?? fileMutationException.Message;
        }

        return exception?.Message ?? string.Empty;
    }

    private static bool HasRemainingDirectoryEntries(string directoryPath)
    {
        try
        {
            return Directory.Exists(directoryPath) && Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false;
        }
    }

    private bool TryCleanupPendingPackageSourceForEstimatedInstall(BMSPackage package, out CleanupSourceKind sourceKind)
    {
        sourceKind = CleanupSourceKind.MissingSource;
        if (package == null || string.IsNullOrWhiteSpace(package.path))
        {
            return true;
        }

        string packagePath = package.path;
        bool sourceDirectoryExists = Directory.Exists(packagePath);
        bool sourceFileExists = File.Exists(packagePath);

        if (!sourceDirectoryExists && !sourceFileExists)
        {
            return true;
        }

        sourceKind = sourceDirectoryExists ? CleanupSourceKind.Directory : CleanupSourceKind.File;
        try
        {
            if (sourceDirectoryExists)
            {
                fileMutationService.DeleteDirectoryDirect(packagePath, recursive: true, recursiveDirectoryTreeFileMutationOptions);
            }
            else
            {
                fileMutationService.DeleteFileDirect(packagePath, targetOnlyFileMutationOptions);
            }
            return true;
        }
        catch (Exception ex)
        {
            string displayedMessage = GetDisplayedExceptionMessage(ex);
            NLogWrapper.FileLogger?.Warn(ex, "estimated_install_cleanup_only_failed path=" + packagePath + " kind=" + sourceKind.ToString().ToLowerInvariant() + " error=" + displayedMessage);
            if (sourceKind == CleanupSourceKind.Directory)
            {
                dialogService.Show(string.Format(Resources.Error_FolderDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            else
            {
                dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            return false;
        }
    }

    /// <summary>
    /// BMSパッケージのファイル群を指定ディレクトリに移動し、移動元の空フォルダを削除する。
    /// マージ処理（MergeBMSDirectory）やインストール処理（installBMSPackages）から呼ばれる共通メソッド。
    /// </summary>
    /// <param name="pkg">移動対象のBMSパッケージ</param>
    /// <param name="installationDirectory">移動先ディレクトリ（nullの場合は自動命名）</param>
    /// <param name="showMessageBoxOnInstallFail">移動失敗時にエラーダイアログを表示するか</param>
    /// <param name="deleteAllContents">移動元フォルダを再帰削除対象として扱うか（通常インストール時は安全判定を通過した場合のみ削除）</param>
    /// <param name="existingHashes">既存BMSハッシュのスナップショット（重複スキップ用）</param>
    /// <param name="excludedComponentPaths">移動対象外のコンポーネントパス</param>
    /// <returns>移動成功時true</returns>
    private bool moveBMSPackageFiles(BMSPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false, HashSet<string> existingHashes = null, ISet<string> excludedComponentPaths = null)
    {
        return packageInstallService.MovePackageFiles(
            pkg,
            installationDirectory,
            BmsLibraryOptionsSnapshot.CreateCurrent(),
            createBMSFolderPath,
            GetDisplayedExceptionMessage,
            fileMutationService,
            dialogService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions,
            LogInstallPerformance,
            showMessageBoxOnInstallFail,
            deleteAllContents,
            existingHashes,
            excludedComponentPaths);
    }

    private List<BMSPackage> installBMSPackages(IEnumerable<BMSPackage> bmsPackagesInstall, string installationDirectory = null, List<BMSFile> deferredMaintenanceTargets = null, List<BMSPackage> deferredInstalledPackages = null, Dictionary<BMSPackage, HashSet<string>> excludedComponentPathsByPackage = null, HashSet<string> existingHashes = null, bool skipInstalledPackageWhenNoBms = false, bool deleteSourceContentsAfterSuccessfulInstall = false)
    {
        List<BMSFile> addedBmsFilesForChartInfo = new List<BMSFile>();
        List<LR2SongDBExtended.bmson_song> addedBmsonSongsForChartInfo = new List<LR2SongDBExtended.bmson_song>();
        PackageInstallExecutionResult result = packageInstallService.InstallPackages(
            bmsPackagesInstall,
            installationDirectory,
            (package, destinationDirectory, deleteAllContents, hashSnapshot, excludedComponentPaths) => moveBMSPackageFiles(package, destinationDirectory, true, deleteAllContents, hashSnapshot, excludedComponentPaths),
            (files) => dbGateway.UpsertSongs(files),
            delegate (IEnumerable<BMSFile> files)
            {
                if (deferredMaintenanceTargets != null)
                {
                    deferredMaintenanceTargets.AddRange(files);
                }
                else
                {
                    setMaintenanceInfo(files, forceUpdate: true);
                }
            },
            null,
            (files) => SetBMSScore(files),
            delegate (IEnumerable<BMSFile> files)
            {
                List<BMSFile> addedFiles = files.Where((BMSFile file) => file != null).ToList();
                List<BMSFile> addedBmsFiles = addedFiles.Where(PendingChartEntry.IsBmsChartFile).ToList();
                List<LR2SongDBExtended.bmson_song> addedBmsonSongs = BuildBmsonSongsFromChartRows(addedFiles);
                addedBmsFilesForChartInfo.AddRange(addedBmsFiles);
                addedBmsonSongsForChartInfo.AddRange(addedBmsonSongs);
                HashSet<string> addedBmsPathSet = new HashSet<string>(addedBmsFiles.Select((BMSFile ff) => ff.path), StringComparer.OrdinalIgnoreCase);
                BMSFiles = BMSFiles.Where((BMSFile f) => !addedBmsPathSet.Contains(f.path)).Concat(addedBmsFiles).ToList();
                if (addedBmsonSongs.Count > 0)
                {
                    dbGateway.UpsertBmsonSongs(addedBmsonSongs);
                    Dictionary<string, LR2SongDBExtended.bmson_song> nextBmsonByPath = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path)).ToDictionary((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase);
                    foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedBmsonSongs)
                    {
                        nextBmsonByPath[addedBmsonSong.path] = addedBmsonSong;
                    }
                    BmsonSongs = nextBmsonByPath.Values.OrderBy((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase).ToList();
                }
                BmsScanResult addedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots(addedFiles.Select((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path)).Distinct(StringComparer.OrdinalIgnoreCase));
                DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                foreach (string dir in addedDirectoryScan.ChartDirectories)
                {
                    if (!addedDirectoryScan.SelfOwnedAllResourceBaseNameHashesByChartDirectory.TryGetValue(dir, out uint[] hashes))
                    {
                        addedDirectoryScan.AllResourceBaseNameHashesByChartDirectory.TryGetValue(dir, out hashes);
                    }
                    if (hashes != null)
                    {
                        bmsFolderAllFileList.AddDirHashed(dir, hashes);
                    }
                    reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
                    directoryRelativePathHashIndex.AddDir(dir, addedDirectoryScan);
                }
                LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
            },
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
        if (result.InstalledPackagesToRegister.Count > 0)
        {
            if (deferredInstalledPackages != null)
            {
                deferredInstalledPackages.AddRange(result.InstalledPackagesToRegister);
            }
            else
            {
                BMSPackagesInstalled.AddRange(result.InstalledPackagesToRegister);
            }
        }
        LogInstallPerformance("installBMSPackages dst=" + (installationDirectory ?? "(auto)") + " packages=" + bmsPackagesInstall.Count() + " addedFiles=" + result.AddedFiles.Count + " failedPackages=" + result.FailedPackages.Count + " deleteSourceContents=" + deleteSourceContentsAfterSuccessfulInstall + " moveMs=" + result.MoveMs + " songDbMs=" + result.SongDbMs + " maintenanceMs=" + result.MaintenanceMs + " zeroNoteMs=" + result.ZeroNoteMs + " scoreMs=" + result.ScoreMs + " applyMs=" + result.ApplyMs + " totalMs=" + result.TotalMs);
        if (deferredMaintenanceTargets == null)
        {
            BuildAndPersistInlineChartInfoForInstalledCharts("install_package_inline", addedBmsFilesForChartInfo, addedBmsonSongsForChartInfo);
        }
        return result.FailedPackages;
    }

    private static List<LR2SongDBExtended.bmson_song> BuildBmsonSongsFromChartRows(IEnumerable<BMSFile> files)
    {
        return (files ?? Enumerable.Empty<BMSFile>())
            .OfType<PendingChartEntry>()
            .Where((PendingChartEntry file) => file.IsBmsonChart)
            .Select(delegate (PendingChartEntry file)
            {
                LR2SongDBExtended.bmson_song source = file.BmsonSong ?? new LR2SongDBExtended.bmson_song();
                LR2SongDBExtended.bmson_song result = new LR2SongDBExtended.bmson_song
                {
                    path = file.path,
                    folder = DirectoryExt.GetDirectoryNameSimple(file.path),
                    title = source.title,
                    subtitle = source.subtitle,
                    artist = source.artist,
                    genre = source.genre,
                    level = source.level,
                    mode_hint = source.mode_hint,
                    md5 = source.md5 ?? file.hash,
                    sha256 = source.sha256 ?? file.sha256,
                    banner = source.banner,
                    backbmp = source.backbmp,
                    stagefile = source.stagefile,
                    preview_music = source.preview_music,
                    MaintenanceInfo = file.maintenanceInfo
                };
                result.wav_files = source.wav_files != null && source.wav_files.Count > 0 ? source.wav_files : file.WAVfiles?.ToList() ?? new List<string>();
                result.bga_files = source.bga_files != null && source.bga_files.Count > 0 ? source.bga_files : file.BGAfiles?.ToList() ?? new List<string>();
                result.MaintenanceInfo?.NormalizeForBmson(result.path, result.md5);
                file.ReplaceBmsonSongReferenceAfterInstall(result);
                return result;
            })
            .ToList();
    }

    private List<LR2SongDBExtended.bmson_song> ResolveAddedBmsonSongsFromInstalledPackages(IEnumerable<BMSPackage> installedPackages)
    {
        List<string> addedBmsonPaths = (installedPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage package) => package != null)
            .SelectMany((BMSPackage package) => package.BMSFiles ?? new List<BMSFile>())
            .Where(PendingChartEntry.IsBmsonChartFile)
            .Select((BMSFile file) => file.path)
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addedBmsonPaths.Count == 0)
        {
            return new List<LR2SongDBExtended.bmson_song>();
        }
        Dictionary<string, LR2SongDBExtended.bmson_song> bmsonByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>())
        {
            if (song != null && !string.IsNullOrWhiteSpace(song.path))
            {
                bmsonByPath[song.path] = song;
            }
        }
        List<LR2SongDBExtended.bmson_song> result = new List<LR2SongDBExtended.bmson_song>();
        foreach (string path in addedBmsonPaths)
        {
            if (bmsonByPath.TryGetValue(path, out LR2SongDBExtended.bmson_song song))
            {
                result.Add(song);
            }
        }
        return result;
    }

    /// <summary>
    /// BMSファイルの差分譜面導入先（インストール先ディレクトリ）を推定します。
    /// </summary>
    /// <param name="bmsFiles">インストール対象のBMSファイルリスト（通常は同一パッケージ内のファイル群）</param>
    /// <param name="asParallel">既存フォルダの走査（各フォルダとのマッチング評価）を並列実行するかどうか</param>
    /// <param name="fixMode">再インストール先修正モードフラグ（登録済みのファイルでも強制的に再推定を実施するかどうか）</param>
    /// <remarks>
    /// 【設計意図・背景】
    /// 差分BMSパッケージ（追加の譜面データや難易度変更ファイル等）は、音源（WAV/OGGやBGA等）の実体を含まないことが多いため、
    /// そのまま独立してインストールしてもゲームプレイ時に音が鳴らないなどの不具合が生じます。
    /// ユーザーが手動で適切なベースとなる楽曲フォルダを探して統合する手間を省くべく、本ロジックでは
    /// 対象差分BMSファイルが必要とする依存ファイルのハッシュ群をキーとして、既存の全楽曲フォルダを事前フィルタリングし、
    /// 関連性が疑われるフォルダに対してのみ「仮想的にBMSを配置したシミュレーション(SetHealthStatus)」を行います。
    /// 全てのフォルダを計算すると重すぎるため、事前のハッシュマッチで候補を絞り込むことで劇的な高速化を図りつつ、
    /// 根本的には旧来と同じく、最もファイルの依存関係が解決される（健康度/Health が高まる）フォルダを自動算出して提案します。
    /// </remarks>
    private void searchEstimatedInstallationDirectory(IEnumerable<BMSFile> bmsFiles, bool asParallel = true, bool fixMode = false)
    {
        searchEstimatedInstallationDirectory(bmsFiles, asParallel, fixMode ? BmsInstallationEstimateMode.ReinstallCorrection : BmsInstallationEstimateMode.Normal);
    }

    private void searchEstimatedInstallationDirectory(IEnumerable<BMSFile> bmsFiles, bool asParallel, BmsInstallationEstimateMode estimateMode)
    {
        searchEstimatedInstallationDirectory(null, bmsFiles, asParallel, estimateMode);
    }

    private void searchEstimatedInstallationDirectory(BMSPackage package, IEnumerable<BMSFile> bmsFiles, bool asParallel, BmsInstallationEstimateMode estimateMode)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<BMSFile> targetBmsFiles = null;
                try
                {
                    targetBmsFiles = ((bmsFiles != null) ? bmsFiles.Where((BMSFile bmsInfo) => bmsInfo != null).ToList() : new List<BMSFile>());
                    if (targetBmsFiles.Count == 0 || targetBmsFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
                    {
                        return;
                    }
                    foreach (BMSFile targetBmsFile in targetBmsFiles)
                    {
                        targetBmsFile.status |= BMSFile.BMSFileStatus.SEARCHING;
                    }
                    InstallEstimationEvaluationData estimationData = EvaluateInstallEstimation(package, targetBmsFiles, asParallel, estimateMode);
                    LogInstallEstimationEvaluation(estimationData);
                    ApplyInstallEstimationResultToFiles(targetBmsFiles, estimationData.Result);
                }
                finally
                {
                    foreach (BMSFile bmsFile2 in (targetBmsFiles ?? Enumerable.Empty<BMSFile>()))
                    {
                        bmsFile2.status &= ~BMSFile.BMSFileStatus.SEARCHING;
                    }
                }
            }
        }
    }

    private void ApplyPackageMixedInstallWarnings(IEnumerable<BMSFile> installedInLibrary)
    {
        if (installedInLibrary == null)
        {
            return;
        }
        foreach (BMSFile item in installedInLibrary)
        {
            if (item != null)
            {
                item.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                item.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
            }
        }
    }

    private void ApplyInstalledDestinationResolveFailedToPackageUnsafe(BMSPackage package, IEnumerable<BMSFile> missingFiles)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
        }
        ApplyInstalledDestinationResolveFailedToFilesUnsafe(missingFiles);
    }

    private static void ApplyInstalledDestinationResolveFailedToFilesUnsafe(IEnumerable<BMSFile> missingFiles)
    {
        foreach (BMSFile bmsFile in (missingFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.instl_dst = null;
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Resources.Warning_InstalledDestinationResolveFailed);
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        }
    }

    private InstalledChartDirectoryIndexSnapshot BuildInstalledHashToDirectoryMap()
    {
        return CreateInstalledDirectoryIndexSnapshotUnsafe();
    }

    private bool TryResolveInstalledDestinationFromPackage(BMSPackage package, List<BMSFile> missingFiles, out string resolvedDir)
    {
        resolvedDir = null;
        if (package == null || missingFiles == null || missingFiles.Count == 0)
        {
            return false;
        }
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndex = BuildInstalledHashToDirectoryMap();
        if (installedDirectoryIndex.HashCount == 0)
        {
            LogInstallPerformance("mixed_package_resolve failed reason=installed_index_empty");
            return false;
        }
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService();
        InstalledDirectoryLookupResult resolution = installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingFiles, installedDirectoryIndex, bmsFolderAllFileList);
        LogInstallPerformance("mixed_package_resolve start package=" + package.path + " missing=" + missingFiles.Count + " installedMatched=" + resolution.MatchedHashCount + " candidateDirs=" + resolution.CandidateDirectoryCount);
        if (!resolution.Success)
        {
            string reason = resolution.Reason switch
            {
                InstalledDirectoryResolveReason.NoInstalledDirectoryMatch => "no_installed_dir_match",
                InstalledDirectoryResolveReason.MissingRepresentative => "missing_representative_not_found",
                InstalledDirectoryResolveReason.TieHealthBelowThreshold => "tie_health_below_threshold",
                InstalledDirectoryResolveReason.TieBreakUnresolved => "tie_break_unresolved",
                InstalledDirectoryResolveReason.InstalledIndexEmpty => "installed_index_empty",
                _ => "unknown"
            };
            LogInstallPerformance("mixed_package_resolve failed reason=" + reason + " tieCandidates=" + resolution.CandidateDirectoryCount);
            return false;
        }
        resolvedDir = resolution.InstallDirectory;
        LogInstallPerformance("mixed_package_resolve selected dst=" + resolvedDir + " matched=" + resolution.MatchedHashCount + " tieCandidates=" + resolution.CandidateDirectoryCount);
        return true;
    }

    /// <summary>
    /// 指定されたBMSパッケージに対して、最適な導入先ディレクトリへの推論処理をキューイングします。
    /// （UIからのドラッグ＆ドロップ登録時などに呼び出されます）
    /// </summary>
    /// <param name="package">推定を行うBMS差分パッケージオブジェクト</param>
    private void SearchEstimatedInstallationDirectoryCore(BMSPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (!BMSPackagesPending.Contains(package))
                        {
                            return;
                        }
                        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
                        List<BMSFile> packageFiles = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                        if (packageFiles.Count == 0)
                        {
                            return;
                        }
                        List<BMSFile> alreadyInstalledFiles = packageFiles.Where(ContainsInstalledChartUnsafe).ToList();
                        List<BMSFile> missingFiles = packageFiles.Where((BMSFile file) => !ContainsInstalledChartUnsafe(file)).ToList();
                        ApplyPackageMixedInstallWarnings(alreadyInstalledFiles);
                        if (missingFiles.Count == 0)
                        {
                            return;
                        }
                        // 部分既所持パッケージでは、既存譜面の実配置先を優先利用して未所持譜面の導入先を補完する。
                        if (alreadyInstalledFiles.Count > 0)
                        {
                            if (TryResolveInstalledDestinationFromPackage(package, missingFiles, out var resolvedDir))
                            {
                                ApplyResolvedInstallDestinationToFiles(missingFiles, resolvedDir);
                                return;
                            }
                            LogInstallPerformance("mixed_package_resolve failed reason=installed_destination_unresolved missing=" + missingFiles.Count);
                            ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingFiles);
                            return;
                        }
                        if (missingFiles.Count > 0)
                        {
                            searchEstimatedInstallationDirectory(package, missingFiles, asParallel: true, BmsInstallationEstimateMode.Normal);
                        }
                    }
                }
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(BMSPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(package.path);
        LogInstallPerformance("manual_estimate_progress start kind=package total=1 current=" + displayName);
        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SearchEstimatedInstallationDirectoryCore(package);
            });
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=package total=1 elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchEstimatedInstallationDirectory(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> packageList = packages.Where((BMSPackage package) => package != null).ToList();
        if (packageList.Count == 0)
        {
            return;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=packages total=" + packageList.Count);
        try
        {
            for (int i = 0; i < packageList.Count; i++)
            {
                BMSPackage package = packageList[i];
                string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(package.path);
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, packageList.Count, i, displayName);
                RunPendingEstimateExclusive(delegate
                {
                    SearchEstimatedInstallationDirectoryCore(package);
                });
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, packageList.Count, i + 1, displayName);
            }
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=packages total=" + packageList.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    private void SearchEstimatedInstallationDirectoryCore(BMSFile bmsFile, bool asParallel = true, bool fixMode = false)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                ClearDeferredEstimateReasonForFilesUnsafe(new BMSFile[1] { bmsFile });
            }
        }
        bool resolvedInstalledDirectory = false;
        if (PendingChartEntry.IsBmsonChartFile(bmsFile))
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockBMSFilesPendingInstall.GetReaderGuard())
                {
                    if (ContainsInstalledChartUnsafe(bmsFile))
                    {
                        List<string> installedDirectories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(CreateInstalledDirectoryIndexSnapshotUnsafe(), bmsFile);
                        if (installedDirectories.Count == 1)
                        {
                            ApplyResolvedInstallDestinationToFiles(new BMSFile[1] { bmsFile }, installedDirectories[0]);
                            resolvedInstalledDirectory = true;
                        }
                    }
                }
            }
            if (resolvedInstalledDirectory)
            {
                return;
            }
        }
        searchEstimatedInstallationDirectory(new BMSFile[1] { bmsFile }, asParallel, fixMode);
    }

    public void SearchEstimatedInstallationDirectory(BMSFile bmsFile, bool asParallel = true, bool fixMode = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(bmsFile?.path);
        LogInstallPerformance("manual_estimate_progress start kind=file total=1 current=" + displayName);
        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SearchEstimatedInstallationDirectoryCore(bmsFile, asParallel, fixMode);
            });
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=file total=1 elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchEstimatedInstallationDirectory(IEnumerable<BMSFile> bmsFiles, bool asParallel = true, bool fixMode = false)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        List<BMSFile> targetFiles = bmsFiles.Where((BMSFile file) => file != null).ToList();
        if (targetFiles.Count == 0)
        {
            return;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=files total=" + targetFiles.Count);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                HashSet<BMSFile> targetFileSet = new HashSet<BMSFile>(targetFiles);
                List<BMSPackage> packageTargets;
                using (rwlockBMSFilesPendingInstall.GetReaderGuard())
                {
                    packageTargets = BMSPackagesPending
                        .Where((BMSPackage package) => package != null && (package.BMSFiles ?? new List<BMSFile>()).Any((BMSFile file) => file != null && targetFileSet.Contains(file)))
                        .ToList();
                }
                HashSet<BMSFile> packageFiles = new HashSet<BMSFile>(packageTargets.SelectMany((BMSPackage package) => package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null));
                List<object> workItems = packageTargets.Cast<object>()
                    .Concat(targetFiles.Where((BMSFile file) => !packageFiles.Contains(file)).Cast<object>())
                    .ToList();
                int totalWorkCount = workItems.Count;
                for (int i = 0; i < totalWorkCount; i++)
                {
                    object workItem = workItems[i];
                    string displayName = workItem is BMSPackage package
                        ? PendingInstallEstimateBatchRequest.GetDisplayName(package.path)
                        : PendingInstallEstimateBatchRequest.GetDisplayName(((BMSFile)workItem).path);
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i, displayName);
                    if (workItem is BMSPackage targetPackage)
                    {
                        SearchEstimatedInstallationDirectoryCore(targetPackage);
                    }
                    else
                    {
                        SearchEstimatedInstallationDirectoryCore((BMSFile)workItem, asParallel, fixMode);
                    }
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i + 1, displayName);
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=files total=" + targetFiles.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchMergeDestination(BMSPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        if (!BMSPackagesPending.Contains(package))
        {
            return;
        }
        List<BMSFile> list = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile x) => x != null).ToList();
        if (list.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target package=" + package.path);
            return;
        }
        foreach (BMSFile item in list)
        {
            item.instl_dst = null;
        }
        LogInstallPerformance("estimated_merge_start package=" + package.path + " targets=" + list.Count);
        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
        if (!TryResolveInstalledDestinationFromPackage(package, list, out string resolvedDir))
        {
            searchEstimatedInstallationDirectory(package, list, asParallel: true, BmsInstallationEstimateMode.MergeCandidateOnly);
        }
        else
        {
            ApplyResolvedInstallDestinationToFiles(list, resolvedDir);
        }
        int num = list.Count((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved package=" + package.path + " targets=" + list.Count);
            return;
        }
        string resolvedDestination = list.Select((BMSFile f) => f.instl_dst).Where((string d) => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        bool metadataResolved = list.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(f.InstallDestinationArtist));
        LogInstallPerformance("estimated_merge_done package=" + package.path + " resolved=" + num + " targets=" + list.Count + " dst=" + resolvedDestination + " metadataResolved=" + metadataResolved);
    }

    public void SearchMergeDestination(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        List<BMSFile> list = bmsFiles.Where((BMSFile x) => x != null).ToList();
        if (list.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target");
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    ClearDeferredEstimateReasonForFilesUnsafe(list);
                }
            }
        }
        foreach (BMSFile item in list)
        {
            item.instl_dst = null;
            searchEstimatedInstallationDirectory(new BMSFile[1] { item }, asParallel: true, BmsInstallationEstimateMode.MergeCandidateOnly);
        }
        int num = list.Count((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved targets=" + list.Count);
            return;
        }
        LogInstallPerformance("estimated_merge_done resolved=" + num + " targets=" + list.Count);
    }

    /// <summary>
    /// 指定された BMS パッケージ群を、インストール先ディレクトリへ強制インストールします。
    /// </summary>
    public void InstallBMSPackagesForce(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (BMSFiles == null)
                        {
                            return;
                        }
                        ForceInstallBatchResult result = packageInstallService.ForceInstallPackages(
                            packages,
                            BMSPackagesPending,
                            delegate (BMSPackage pendingPackage)
                            {
                                return dialogService.Show(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                            },
                            delegate (IEnumerable<BMSPackage> packagesToInstall, List<BMSPackage> deferredInstalledPackages)
                            {
                                return installBMSPackages(packagesToInstall, null, null, deferredInstalledPackages);
                            },
                            info => NLogWrapper.FileLogger?.Info(info));
                        if (result.Requested == 0)
                        {
                            return;
                        }
                        NLogWrapper.FileLogger?.Info("force_install_batch start requested=" + result.Requested);
                        if (result.PendingPackagesToRemove.Count > 0)
                        {
                            stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: result.PendingPackagesToRemove));
                        }
                        int num5 = 0;
                        if (result.DeferredInstalledPackages.Count > 0)
                        {
                            HashSet<BMSPackage> hashSet3 = new HashSet<BMSPackage>(BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null));
                            List<BMSPackage> list5 = BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null).ToList();
                            foreach (BMSPackage deferredInstalledPackage in result.DeferredInstalledPackages)
                            {
                                if (deferredInstalledPackage != null && hashSet3.Add(deferredInstalledPackage))
                                {
                                    list5.Add(deferredInstalledPackage);
                                    num5++;
                                }
                            }
                            if (num5 > 0)
                            {
                                BMSPackagesInstalled = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(list5), DispatcherHelper.UIDispatcher);
                            }
                        }
                        NLogWrapper.FileLogger?.Info("force_install_batch summary requested=" + result.Requested + " processed=" + result.Processed + " succeeded=" + result.Succeeded + " failed=" + result.Failed + " skipped=" + result.Skipped + " pendingRemoved=" + result.PendingPackagesToRemove.Count + " installedAdded=" + num5);
                    }
                }
            }
        }
    }

    public void InstallBMSPackageForce(BMSPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        InstallBMSPackagesForce(new BMSPackage[1] { package });
    }

    private int CountComponentMoveTargetsForPackage(BMSPackage package, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        if (package == null || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return 0;
        }
        try
        {
            string path = package.path;
            List<string> list = null;
            if (File.Exists(path))
            {
                list = new List<string> { path };
            }
            else
            {
                if (!Directory.Exists(path))
                {
                    return 0;
                }
                list = Directory.EnumerateFileSystemEntries(path).ToList();
            }
            List<BMSFile> list2 = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile f) => f != null).ToList();
            HashSet<string> hashSet = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            HashSet<string> hashSet2 = new HashSet<string>(list2.Where((BMSFile f) => hashSet.Contains(f.path)).Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
            List<string> installComponentFiles = list.Where((string p) => !hashSet2.Contains(p)).ToList();
            return BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths).PlanItems.Count;
        }
        catch
        {
            return 0;
        }
    }

    private BMSPackage CreateInstalledDisplayPackageForResourceOnlyMerge(BMSPackage originalPackage, string destinationDirectory)
    {
        if (originalPackage == null || string.IsNullOrWhiteSpace(destinationDirectory) || BMSFiles == null)
        {
            return null;
        }
        HashSet<string> hashSet = new HashSet<string>((originalPackage.BMSFiles ?? new List<BMSFile>())
            .Where((BMSFile f) => f != null)
            .Select(PendingChartEntry.GetPrimaryLookupHash)
            .Where((string key) => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
        if (hashSet.Count == 0)
        {
            return null;
        }
        List<BMSFile> list = BMSFiles.Where(delegate (BMSFile f)
        {
            if (f == null || string.IsNullOrWhiteSpace(f.path))
            {
                return false;
            }
            string key = PendingChartEntry.GetPrimaryLookupHash(f);
            if (string.IsNullOrWhiteSpace(key) || !hashSet.Contains(key))
            {
                return false;
            }
            return string.Equals(DirectoryExt.GetDirectoryNameSimple(f.path), destinationDirectory, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        list.AddRange((BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>()).Where(delegate (LR2SongDBExtended.bmson_song song)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                return false;
            }
            string key = PendingChartEntry.GetPrimaryLookupHash(song);
            if (!string.IsNullOrWhiteSpace(key) && hashSet.Contains(key))
            {
                return string.Equals(DirectoryExt.GetDirectoryNameSimple(song.path), destinationDirectory, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }).Select(PendingChartEntry.CreateFromBmsonSong).Where((PendingChartEntry file) => file != null));
        if (list.Count == 0)
        {
            return null;
        }
        return new BMSPackage(list)
        {
            path = destinationDirectory,
            delete_parent = false
        };
    }

    /// <summary>
    /// 指定された BMS パッケージ群を推定されたインストール先ディレクトリへインストールします。
    /// SmartOverwrite ロジックによるコンポーネント移動計画を構築して実行します。
    /// </summary>
    public void InstallBMSPackagesToEstimatedDir(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (BMSFiles == null)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir skipped reason=BMSFiles_null totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(packages, BMSPackagesPending, BMSFiles, BmsonSongs, deletePendingPackageSourceAfterInstall, CountComponentMoveTargetsForPackage);
                        if (installPlan.SelectedPendingPackages.Count == 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
                        PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
                            installPlan,
                            deletePendingPackageSourceAfterInstall,
                            (installPackages, destinationDirectory, deferredMaintenanceTargets, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => installBMSPackages(installPackages, destinationDirectory, deferredMaintenanceTargets, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall),
                            CreateInstalledDisplayPackageForResourceOnlyMerge,
                            (cleanupOnlyPackage) =>
                            {
                                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupOnlyPackage, out var sourceKind);
                                return (cleanupSucceeded, sourceKind);
                            },
                            LogInstallPerformance);
                        dbGateway.DeleteInstallRows(batchResult.InstallRowsToDelete);
                        Stopwatch pendingApplyStopwatch = Stopwatch.StartNew();
                        int pendingCountBeforeApply = BMSPackagesPending.Count;
                        int pendingRemovedTotal = batchResult.PendingPackagesToRemove.Count;
                        if (pendingRemovedTotal > 0)
                        {
                            List<BMSPackage> remainingPending = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null && !batchResult.PendingPackagesToRemove.Contains(pkg)).ToList();
                            BMSPackagesPending = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(remainingPending), DispatcherHelper.UIDispatcher);
                        }
                        int pendingCountAfterApply = BMSPackagesPending.Count;
                        pendingApplyStopwatch.Stop();
                        Stopwatch installedApplyStopwatch = Stopwatch.StartNew();
                        int installedCountBeforeApply = BMSPackagesInstalled.Count;
                        int installedAddedTotal = batchResult.DeferredInstalledPackages.Count;
                        if (installedAddedTotal > 0)
                        {
                            HashSet<BMSPackage> installedSet = new HashSet<BMSPackage>(BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null));
                            List<BMSPackage> mergedInstalled = BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null).ToList();
                            foreach (BMSPackage installedPackage in batchResult.DeferredInstalledPackages)
                            {
                                if (installedPackage != null && installedSet.Add(installedPackage))
                                {
                                    mergedInstalled.Add(installedPackage);
                                }
                            }
                            BMSPackagesInstalled = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(mergedInstalled), DispatcherHelper.UIDispatcher);
                        }
                        int installedCountAfterApply = BMSPackagesInstalled.Count;
                        installedApplyStopwatch.Stop();
                        Stopwatch maintenanceStopwatch = Stopwatch.StartNew();
                        if (batchResult.DeferredMaintenanceTargets.Count > 0)
                        {
                            setMaintenanceInfo(batchResult.DeferredMaintenanceTargets, forceUpdate: true);
                        }
                        BuildAndPersistInlineChartInfoForInstalledCharts(
                            "install_package_estimated_inline",
                            batchResult.DeferredMaintenanceTargets,
                            ResolveAddedBmsonSongsFromInstalledPackages(batchResult.DeferredInstalledPackages));
                        maintenanceStopwatch.Stop();
                        if (deletePendingPackageSourceAfterInstall && batchResult.CleanupOnlySucceeded > 0)
                        {
                            dialogService.Show(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, batchResult.CleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        totalStopwatch.Stop();
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir end pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingCountBeforeApply + " pendingRemovedTotal=" + pendingRemovedTotal + " pendingAfterApply=" + pendingCountAfterApply + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedCountBeforeApply + " installedAddedTotal=" + installedAddedTotal + " installedAfterApply=" + installedCountAfterApply + " maintenanceTargets=" + batchResult.DeferredMaintenanceTargets.Count + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }
    }

    public void InstallBMSPackageToEstimatedDir(BMSPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        InstallBMSPackagesToEstimatedDir(new BMSPackage[1] { package });
    }

    /// <summary>
    /// 指定されたインストール済みパッケージ群をリストから削除します。
    /// </summary>
    public void RemoveBMSPackagesInstalled(IEnumerable<BMSPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    BMSPackagesInstalled.Remove(packages);
                }
            }
        }
    }

    private PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<BMSPackage> packagesToRemove = null, IEnumerable<BMSFile> filesToRemove = null, bool clearAll = false)
    {
        return packageInstallService.BuildPendingPackageMutationDelta(BMSPackagesPending, packagesToRemove, filesToRemove, clearAll);
    }

    private void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        stateApplier.ApplyPendingPackageMutationDelta(delta);
    }

    /// <summary>
    /// 指定された Pending パッケージ群を Pending リストから削除します。
    /// </summary>
    public void RemoveBMSPackagesPending(IEnumerable<BMSPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    RemovePendingPackagesFromPendingListAndInstallRows(packages);
                }
            }
        }
    }

    /// <summary>
    /// 全てのインストール済みパッケージをリストからクリアします。
    /// </summary>
    public void RemoveBMSPackagesInstalledAll()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    BMSPackagesInstalled.Clear();
                }
            }
        }
    }

    /// <summary>
    /// 全ての Pending パッケージをリストからクリアします。
    /// </summary>
    public void RemoveBMSPackagesPendingAll()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(clearAll: true));
                }
            }
        }
    }

    public List<BMSPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                List<BMSPackage> list = packageInstallService.GetPendingPackagesContainingOnlyInstalledCharts(BMSPackagesPending, ContainsInstalledChartUnsafe);
                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup scan pendingTotal=" + BMSPackagesPending.Count + " eligible=" + list.Count);
                return list;
            }
        }
    }

    private enum PrepareSkipReason
    {
        None,
        MissingInstallDestination,
        ChartHasMultipleInstalledDirectories,
        PackageHasSplitInstalledDirectories
    }

    private static List<string> GetDistinctInstalledDirectoriesByHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, string md5, string sha256 = null)
    {
        return BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, md5, sha256);
    }

    private static List<string> GetDistinctInstalledDirectoriesByHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, BMSFile file)
    {
        return BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file);
    }

    private static BMSFile FindChartWithMissingInstalledDirectory(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(package, installedDirectoryIndexSnapshot);
    }

    private static BMSFile FindChartWithMultipleInstalledDirectories(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(package, installedDirectoryIndexSnapshot);
    }

    private static int CountDistinctInstalledDirectoriesForPackage(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(package, installedDirectoryIndexSnapshot);
    }

    private bool TryPrepareInstalledOnlyPackageDestination(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out string destinationDir, out PrepareSkipReason reason)
    {
        InstalledOnlyPackageResolutionResult resolution = CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(package, installedDirectoryIndexSnapshot);
        destinationDir = resolution.DestinationDirectory;
        reason = resolution.Reason switch
        {
            InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories => PrepareSkipReason.ChartHasMultipleInstalledDirectories,
            InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories => PrepareSkipReason.PackageHasSplitInstalledDirectories,
            InstalledDirectoryResolveReason.None => PrepareSkipReason.None,
            _ => PrepareSkipReason.MissingInstallDestination
        };
        return resolution.Success;
    }

    private void TryRegroupPendingPackagesForSourceDirectoriesUnsafe(IEnumerable<string> sourceDirectoryPaths)
    {
        List<string> sourceDirectories = (sourceDirectoryPaths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceDirectories.Count == 0)
        {
            return;
        }
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = BuildInstalledHashToDirectoryMap();
        foreach (string sourceDirectoryPath in sourceDirectories)
        {
            TryRegroupPendingPackagesForSourceDirectoryUnsafe(sourceDirectoryPath, installedDirectoryIndexSnapshot);
        }
    }

    private void TryRegroupPendingPackagesForSourceDirectoryUnsafe(string sourceDirectoryPath, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
        {
            return;
        }
        List<BMSPackage> sourcePackages = BMSPackagesPending
            .Where((BMSPackage pendingPackage) => pendingPackage != null && string.Equals(GetPendingPackageSourceDirectoryPath(pendingPackage), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sourcePackages.Count < 2)
        {
            return;
        }
        if (sourcePackages.Any((BMSPackage pendingPackage) => string.Equals(NormalizePendingPackagePath(pendingPackage.path), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            LogInstallPerformance("pending_regroup skip reason=already_directory_package source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (sourcePackages.Any((BMSPackage pendingPackage) => pendingPackage != null && pendingPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None))
        {
            LogInstallPerformance("pending_regroup skip reason=deferred_estimate source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (!TryBuildRegroupedPendingPackage(sourceDirectoryPath, sourcePackages, installedDirectoryIndexSnapshot, out BMSPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason))
        {
            LogInstallPerformance("pending_regroup skip reason=" + skipReason + " source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        ReinitializePendingWarningsForPackageUnsafe(regroupedPackage, CreateInstalledChartKeySnapshotExcludingUnsafe(null));
        ReplacePendingPackagesWithRegroupedPackageUnsafe(sourcePackages, regroupedPackage);
        dbGateway.DeleteInstallRows(sourcePackages.Select((BMSPackage pendingPackage) => pendingPackage.path));
        dbGateway.UpsertInstallRows(new BMSPackage[1] { regroupedPackage });
        bool metadataResolved = (regroupedPackage.BMSFiles ?? new List<BMSFile>()).Any((BMSFile file) => file != null && (!string.IsNullOrWhiteSpace(file.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(file.InstallDestinationArtist)));
        LogInstallPerformance("pending_regroup success source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count + " files=" + regroupedPackage.BMSFiles.Count + " dst=" + resolvedDestinationDirectory + " metadataResolved=" + metadataResolved);
    }

    private bool TryBuildRegroupedPendingPackage(string sourceDirectoryPath, List<BMSPackage> sourcePackages, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out BMSPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason)
    {
        regroupedPackage = null;
        resolvedDestinationDirectory = null;
        skipReason = "unknown";
        List<BMSFile> regroupedFiles = new List<BMSFile>();
        HashSet<BMSFile> seenFileReferences = new HashSet<BMSFile>();
        HashSet<string> seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage sourcePackage in sourcePackages)
        {
            foreach (BMSFile sourceFile in (sourcePackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null))
            {
                if (seenFileReferences.Add(sourceFile) && (string.IsNullOrWhiteSpace(sourceFile.path) || seenFilePaths.Add(sourceFile.path)))
                {
                    regroupedFiles.Add(sourceFile);
                }
            }
        }
        if (regroupedFiles.Count == 0)
        {
            skipReason = "no_files";
            return false;
        }
        HashSet<string> expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile regroupedFile in regroupedFiles)
        {
            if (!TryResolvePendingFileExpectedInstallDirectory(regroupedFile, installedDirectoryIndexSnapshot, out string expectedDirectory, out string unresolvedReason))
            {
                skipReason = unresolvedReason;
                return false;
            }
            expectedDirectories.Add(expectedDirectory);
            if (expectedDirectories.Count > 1)
            {
                skipReason = "split_expected_destination";
                return false;
            }
        }
        resolvedDestinationDirectory = expectedDirectories.Single();
        ApplyResolvedInstallDestinationPathAndMetadataToFiles(regroupedFiles, resolvedDestinationDirectory);
        regroupedPackage = new BMSPackage(regroupedFiles)
        {
            path = sourceDirectoryPath,
            delete_parent = false
        };
        return true;
    }

    private bool TryResolvePendingFileExpectedInstallDirectory(BMSFile bmsFile, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out string expectedDirectory, out string reason)
    {
        expectedDirectory = null;
        reason = "missing_expected_destination";
        if (bmsFile == null)
        {
            reason = "null_file";
            return false;
        }
        List<string> installedDirectories = GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, bmsFile);
        if (installedDirectories.Count > 1)
        {
            reason = "multiple_installed_directories";
            return false;
        }
        string installedDirectory = installedDirectories.FirstOrDefault();
        string estimatedDirectory = string.IsNullOrWhiteSpace(bmsFile.instl_dst) ? null : bmsFile.instl_dst;
        if (!string.IsNullOrWhiteSpace(installedDirectory) && !string.IsNullOrWhiteSpace(estimatedDirectory) && !installedDirectory.Equals(estimatedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            reason = "installed_directory_conflict";
            return false;
        }
        expectedDirectory = installedDirectory ?? estimatedDirectory;
        if (string.IsNullOrWhiteSpace(expectedDirectory))
        {
            reason = "missing_expected_destination";
            return false;
        }
        return true;
    }

    private void ReinitializePendingWarningsForPackageUnsafe(BMSPackage package, ISet<string> installedHashes)
    {
        if (package == null)
        {
            return;
        }
        bool isSingleFilePackage = !Directory.Exists(package.path);
        HashSet<string> installedHashSet = installedHashes as HashSet<string> ?? new HashSet<string>(installedHashes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile bmsFile in (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bool isBmson = PendingChartEntry.IsBmsonChartFile(bmsFile);
            bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
            bmsFile.ClearStructuredWarnings();
            string key = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
            if (!string.IsNullOrWhiteSpace(key) && installedHashSet.Contains(key))
            {
                bmsFile.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
            }
            else if (isSingleFilePackage)
            {
                bmsFile.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
            }
            else
            {
                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true);
            }
        }
        BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);
    }

    private void ReplacePendingPackagesWithRegroupedPackageUnsafe(List<BMSPackage> sourcePackages, BMSPackage regroupedPackage)
    {
        if (sourcePackages == null || sourcePackages.Count == 0 || regroupedPackage == null)
        {
            return;
        }
        List<BMSPackage> currentPendingPackages = BMSPackagesPending.Where((BMSPackage pendingPackage) => pendingPackage != null).ToList();
        int insertIndex = currentPendingPackages.FindIndex((BMSPackage pendingPackage) => sourcePackages.Contains(pendingPackage));
        if (insertIndex < 0)
        {
            insertIndex = currentPendingPackages.Count;
        }
        List<BMSPackage> replacedPendingPackages = currentPendingPackages.Where((BMSPackage pendingPackage) => !sourcePackages.Contains(pendingPackage)).ToList();
        replacedPendingPackages.Insert(insertIndex, regroupedPackage);
        BMSPackagesPending = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(replacedPendingPackages), DispatcherHelper.UIDispatcher);
    }

    private static string GetPendingPackageSourceDirectoryPath(BMSPackage package)
    {
        return NormalizePendingPackagePath(package?.path) switch
        {
            string normalizedPath when string.IsNullOrWhiteSpace(normalizedPath) => null,
            string normalizedPath when PendingChartEntry.IsSupportedChartFilePath(normalizedPath) => NormalizePendingPackagePath(Path.GetDirectoryName(normalizedPath)),
            string normalizedPath => normalizedPath
        };
    }

    private static string NormalizePendingPackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private bool HasResourceOverwriteTargetsForInstalledOnlyPackage(BMSPackage package, string destinationDir)
    {
        if (package == null || string.IsNullOrWhiteSpace(destinationDir))
        {
            return false;
        }
        List<BMSFile> list = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile f) => f != null).ToList();
        if (list.Count == 0)
        {
            return false;
        }
        HashSet<string> excludedPaths = new HashSet<string>(list.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.path)).Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
        BMSPackage bMSPackage = new BMSPackage(list)
        {
            path = package.path,
            delete_parent = package.delete_parent
        };
        return CountComponentMoveTargetsForPackage(bMSPackage, destinationDir, excludedPaths) > 0;
    }

    private bool IsPackageStillPending(BMSPackage package)
    {
        if (package == null)
        {
            return false;
        }
        return BMSPackagesPending.Any((BMSPackage pendingPkg) => pendingPkg != null && (ReferenceEquals(pendingPkg, package) || (!string.IsNullOrWhiteSpace(pendingPkg.path) && !string.IsNullOrWhiteSpace(package.path) && pendingPkg.path.Equals(package.path, StringComparison.OrdinalIgnoreCase))));
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<BMSPackage> packages, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite scan pendingTotal=" + BMSPackagesPending.Count + " eligible=" + packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count);
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.HashCount);
                        PendingResourceOverwriteExecutionResult executionResult = packageInstallService.ExecuteInstalledOnlyResourceOverwrite(
                            packages,
                            BMSPackagesPending,
                            deletePendingPackageSourceAfterInstall,
                            (pendingPackage) => CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndexSnapshot),
                            delegate (InstalledOnlyPackageResolutionResult resolution, BMSPackage pendingPackage)
                            {
                                if (pendingPackage == null)
                                {
                                    return null;
                                }
                                return resolution.Reason switch
                                {
                                    InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories => "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot)?.path ?? "(null)") + " hash=" + (FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot)?.hash ?? "(null)") + " dirCount=" + ((FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot) == null) ? 0 : GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot)).Count),
                                    InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories => "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndexSnapshot),
                                    _ => "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot)?.path ?? "(null)") + " hash=" + (FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot)?.hash ?? "(null)")
                                };
                            },
                            (pendingPackage, destinationDir) => HasResourceOverwriteTargetsForInstalledOnlyPackage(pendingPackage, destinationDir),
                            delegate (BMSPackage pendingPackage, string destinationDir)
                            {
                                try
                                {
                                    InstallBMSPackageToEstimatedDir(pendingPackage);
                                    return true;
                                }
                                catch (Exception ex)
                                {
                                    string displayedExceptionMessage = GetDisplayedExceptionMessage(ex);
                                    NLogWrapper.FileLogger?.Warn(ex, "advanced_pending_resource_overwrite install_failed_exception path=" + pendingPackage.path + " dst=" + destinationDir + " error=" + displayedExceptionMessage);
                                    dialogService.Show(string.Format(Resources.Error_InstallFailed, pendingPackage.path, destinationDir, displayedExceptionMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    return false;
                                }
                            },
                            (cleanupPackage) =>
                            {
                                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupPackage, out var sourceKind);
                                return (cleanupSucceeded, sourceKind);
                            },
                            (pendingPackage) => IsPackageStillPending(pendingPackage),
                            token,
                            onEachProcessed,
                            info =>
                            {
                                if (!string.IsNullOrWhiteSpace(info))
                                {
                                    NLogWrapper.FileLogger?.Info(info);
                                }
                            });
                        if (executionResult.PendingPackagesToRemove.Count > 0)
                        {
                            RemovePendingPackagesFromPendingListAndInstallRows(executionResult.PendingPackagesToRemove);
                        }
                        PendingInstalledOnlyResourceOverwriteResult publicResult = executionResult.ToPublicResult();
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite summary requested=" + publicResult.Requested + " processed=" + publicResult.Processed + " succeededInstall=" + publicResult.SucceededInstall + " succeededCleanupOnly=" + publicResult.SucceededCleanupOnly + " skippedNotPending=" + publicResult.SkippedNotPending + " skippedMissingInstlDst=" + publicResult.SkippedMissingInstlDst + " skippedMultiDst=" + publicResult.SkippedMultiDestination + " skippedNoComponentTarget=" + publicResult.SkippedNoComponentTarget + " failed=" + publicResult.Failed + " canceled=" + publicResult.Canceled);
                        return publicResult;
                    }
                }
            }
        }
    }

    public List<BMSFile> GetPendingBMSFilesSnapshot()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                return packageInstallService.GetPendingBmsFilesSnapshot(BMSPackagesPending);
            }
        }
    }

    public void RenamePendingZeroNoteChartsToInvalidExtensions(IEnumerable<BMSFile> targetFiles, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    IEnumerable<BMSFile> enumerable = targetFiles ?? BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).SelectMany((BMSPackage pkg) => pkg.BMSFiles).Where((BMSFile f) => f != null);
                    PendingZeroNoteRenameResult result = packageInstallService.RenamePendingZeroNoteChartsToInvalidExtensions(
                        enumerable,
                        (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false),
                        token,
                        onEachProcessed,
                        info => NLogWrapper.FileLogger?.Info(info));
                    foreach (PendingZeroNoteRenameFailure failure in result.Failures)
                    {
                        if (failure?.Outcome?.FailureException == null || failure.File == null)
                        {
                            continue;
                        }
                        if (failure.Outcome.FailedDuringDelete)
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(result.FilesToRemove);
                    NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename summary total=" + result.Total + " processed=" + result.Processed + " zeroNote=" + result.ZeroNote + " renamed=" + result.Renamed + " duplicateDeleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " canceled=" + result.Canceled);
                }
            }
        }
    }

    /// <summary>
    /// Pending パッケージのソースファイル群を削除（またはゴミ箱へ移動）します。
    /// </summary>
    public void DeletePendingPackageSources(IEnumerable<BMSPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    List<BMSPackage> list = packageInstallService.DeduplicatePackagesByPathOrReference(packages);
                    bool flag2 = !sendToRecycleBin;
                    NLogWrapper.FileLogger?.Info("advanced_pending_cleanup start requested=" + list.Count + " permanent=" + flag2);
                    PendingPackageSourceDeletionResult result = packageInstallService.DeletePendingPackageSources(
                        list,
                        BMSPackagesPending,
                        sendToRecycleBin,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        recursiveDirectoryTreeFileMutationOptions,
                        token,
                        onEachProcessed,
                        info => NLogWrapper.FileLogger?.Info(info));
                    foreach (PendingPackageSourceDeletionFailure failure in result.Failures)
                    {
                        if (failure?.Package == null)
                        {
                            continue;
                        }
                        NLogWrapper.FileLogger?.Warn(failure.Exception, "advanced_pending_cleanup failed path=" + failure.Package.path + " kind=" + (failure.IsDirectory ? "directory" : "file") + " error=" + GetDisplayedExceptionMessage(failure.Exception));
                        if (failure.IsDirectory)
                        {
                            dialogService.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingPackagesFromPendingListAndInstallRows(result.PackagesToRemove);
                    NLogWrapper.FileLogger?.Info("advanced_pending_cleanup summary requested=" + result.Requested + " processed=" + result.Processed + " removed=" + result.Removed + " failed=" + result.Failed + " skipped=" + result.Skipped + " canceled=" + result.Canceled);
                }
            }
        }
    }

    private void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<BMSPackage> packages)
    {
        stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packages));
    }

    /// <summary>
    /// 指定された BMS ファイル群について、インストール先ディレクトリを再探索し、正しいパスを設定します。
    /// </summary>
    public void SearchCorrectInstallationDirectory(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        CreateInstallEstimationService().CorrectInstallationDirectory(bmsFiles, delegate (BMSFile bmsFile)
        {
            SearchEstimatedInstallationDirectory(bmsFile, asParallel: false, fixMode: true);
        });
    }

    public void RemoveInstallDestination(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                CreateInstallEstimationService().ClearInstallDestinations(bmsFiles);
                foreach (BMSFile bmsFile in bmsFiles.Where((BMSFile file) => file != null))
                {
                    bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
                    bmsFile.InstallDestinationTitle = string.Empty;
                    bmsFile.InstallDestinationArtist = string.Empty;
                    bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
                    bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
                }
            }
        }
    }

    public bool SetPendingInstallDestination(BMSFile bmsFile, string destinationDirectory)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                bool allowStandaloneLibraryFile;
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    allowStandaloneLibraryFile = IsKnownLibraryChartFileUnsafe(bmsFile);
                }
                PendingInstallDestinationSelectionResult selection = CreateInstallEstimationService().ValidateInstallDestination(bmsFile, BMSPackagesPending, CreateKnownChartDirectorySnapshotUnsafe(), destinationDirectory, allowStandaloneLibraryFile);
                if (!selection.Success)
                {
                    dialogService.Show(selection.WarningMessage, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                bool preserveAmbiguousInstallContext = !string.IsNullOrWhiteSpace(selection.ValidatedDestinationDirectory)
                    && selection.TargetFiles.Any((BMSFile file) => file != null
                        && file.HasLowConfidenceInstallEstimationWarning()
                        && (file.InstallDestinationSuggestions?.Any((string path) => string.Equals(path, selection.ValidatedDestinationDirectory, StringComparison.OrdinalIgnoreCase)) ?? false));
                ApplyResolvedInstallDestinationToFiles(selection.TargetFiles, selection.ValidatedDestinationDirectory, preserveAmbiguousInstallContext);
                ClearDeferredEstimateReasonForFilesUnsafe(selection.TargetFiles);
                return true;
            }
        }
    }

    private bool IsKnownLibraryChartFileUnsafe(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return false;
        }
        return BMSFiles.Any((BMSFile file) => IsSameChartFile(file, bmsFile))
            || BmsonSongs.Any((LR2SongDBExtended.bmson_song song) => !string.IsNullOrWhiteSpace(song?.path) && IsSamePath(song.path, bmsFile.path));
    }

    private static bool IsSameChartFile(BMSFile left, BMSFile right)
    {
        if (left == null || right == null)
        {
            return false;
        }
        return ReferenceEquals(left, right)
            || (!string.IsNullOrWhiteSpace(left.path) && !string.IsNullOrWhiteSpace(right.path) && IsSamePath(left.path, right.path));
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<string> installedDirectories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByPrimaryHash(CreateInstalledDirectoryIndexSnapshotUnsafe(), hash)
                    .Where((string dir) => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    .OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (installedDirectories.Count == 0)
                {
                    return false;
                }
                installDir = installedDirectories[0];
                return true;
            }
        }
    }

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null, bool suppressFilePropertyChanged = false)
    {
        if (table == null)
        {
            return;
        }
        Stopwatch stopwatchBuildMap = Stopwatch.StartNew();
        IEnumerable<BMSTableEntry> sourceEntries = entries;
        if (sourceEntries == null)
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                sourceEntries = table.entries.ToList();
            }
        }
        else
        {
            sourceEntries = sourceEntries.ToList();
        }
        PlaylistReferenceMaps referenceMaps = playlistReferenceService.BuildReferenceMaps(table, sourceEntries);
        stopwatchBuildMap.Stop();
        int matchedSongFiles = 0;
        int matchedPendingFiles = 0;
        int addedSongRefs = 0;
        int addedPendingRefs = 0;
        PlaylistReferenceApplyStats songApplyStats = default(PlaylistReferenceApplyStats);
        Stopwatch stopwatchApplySong = Stopwatch.StartNew();
        List<BMSFile> songFilesSnapshot = null;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            songFilesSnapshot = SnapshotSongFilesForPlaylistReferenceApply();
        }
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0 && songFilesSnapshot != null && songFilesSnapshot.Count > 0)
        {
            addedSongRefs = playlistReferenceService.ApplyReferenceMap(songFilesSnapshot, referenceMaps, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
        }
        stopwatchApplySong.Stop();
        PlaylistReferenceApplyStats pendingApplyStats = default(PlaylistReferenceApplyStats);
        Stopwatch stopwatchApplyPending = Stopwatch.StartNew();
        List<BMSFile> pendingFilesSnapshot = null;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            pendingFilesSnapshot = SnapshotPendingFilesForPlaylistReferenceApply();
        }
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0 && pendingFilesSnapshot != null && pendingFilesSnapshot.Count > 0)
        {
            addedPendingRefs = playlistReferenceService.ApplyReferenceMap(pendingFilesSnapshot, referenceMaps, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
        }
        stopwatchApplyPending.Stop();
        int tableCount = 1;
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + stopwatchApplySong.ElapsedMilliseconds + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + stopwatchApplyPending.ElapsedMilliseconds + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + tableCount + " matchedSongFiles=" + matchedSongFiles + " addSongCalls=" + addedSongRefs + " matchedPendingFiles=" + matchedPendingFiles + " addPendingCalls=" + addedPendingRefs + " suppressNotify=" + suppressFilePropertyChanged);
    }

    public void AddReferenceBMSTables(IEnumerable<BMSTable> tables, IEnumerable<BMSFile> files = null, bool suppressFilePropertyChanged = false)
    {
        if (tables == null)
        {
            return;
        }
        List<BMSTable> list = tables.Where((BMSTable t) => t != null).ToList();
        if (list.Count == 0)
        {
            return;
        }
        Stopwatch stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = playlistReferenceService.BuildReferenceMaps(list);
        stopwatchBuildMap.Stop();
        long applySongMs = 0L;
        long applyPendingMs = 0L;
        int matchedSongFiles = 0;
        int matchedPendingFiles = 0;
        int addedSongRefs = 0;
        int addedPendingRefs = 0;
        PlaylistReferenceApplyStats songApplyStats = default(PlaylistReferenceApplyStats);
        PlaylistReferenceApplyStats pendingApplyStats = default(PlaylistReferenceApplyStats);
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            if (files == null)
            {
                Stopwatch stopwatchApplySong = Stopwatch.StartNew();
                List<BMSFile> songFilesSnapshot = SnapshotSongFilesForPlaylistReferenceApply();
                if (songFilesSnapshot != null && songFilesSnapshot.Count > 0)
                {
                    addedSongRefs = playlistReferenceService.ApplyReferenceMap(songFilesSnapshot, referenceMaps, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
                }
                stopwatchApplySong.Stop();
                applySongMs = stopwatchApplySong.ElapsedMilliseconds;
                Stopwatch stopwatchApplyPending = Stopwatch.StartNew();
                List<BMSFile> pendingFilesSnapshot = SnapshotPendingFilesForPlaylistReferenceApply();
                if (pendingFilesSnapshot != null && pendingFilesSnapshot.Count > 0)
                {
                    addedPendingRefs = playlistReferenceService.ApplyReferenceMap(pendingFilesSnapshot, referenceMaps, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
                }
                stopwatchApplyPending.Stop();
                applyPendingMs = stopwatchApplyPending.ElapsedMilliseconds;
            }
            else
            {
                Stopwatch stopwatchApplySong = Stopwatch.StartNew();
                using (rwlockBMSFilesPendingInstall.GetReaderGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        addedSongRefs = playlistReferenceService.ApplyReferenceMap(files, referenceMaps, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
                    }
                }
                stopwatchApplySong.Stop();
                applySongMs = stopwatchApplySong.ElapsedMilliseconds;
            }
        }
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + applySongMs + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + applyPendingMs + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + list.Count + " matchedSongFiles=" + matchedSongFiles + " addSongCalls=" + addedSongRefs + " matchedPendingFiles=" + matchedPendingFiles + " addPendingCalls=" + addedPendingRefs + " suppressNotify=" + suppressFilePropertyChanged);
    }

    private List<BMSFile> SnapshotSongFilesForPlaylistReferenceApply()
    {
        if (BMSFiles == null || BMSFiles.Count == 0)
        {
            return null;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return BMSFiles.Where((BMSFile file) => file != null).ToList();
        }
    }

    private List<BMSFile> SnapshotPendingFilesForPlaylistReferenceApply()
    {
        if (BMSPackagesPending == null || BMSPackagesPending.Count == 0)
        {
            return null;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            return BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Where((BMSFile file) => file != null).ToList();
        }
    }

    internal void ReplaceReferenceBMSTable(BMSTable oldTable, BMSTable newTable, IEnumerable<BMSTableEntry> oldEntries = null, IEnumerable<BMSTableEntry> newEntries = null)
    {
        List<BMSTableEntry> oldEntriesSnapshot = SnapshotPlaylistReferenceEntries(oldTable, oldEntries);
        List<BMSTableEntry> newEntriesSnapshot = SnapshotPlaylistReferenceEntries(newTable, newEntries);
        (List<BMSFile> SongFiles, List<BMSFile> PendingFiles) targets = ResolvePlaylistReferenceTargets(oldEntriesSnapshot.Concat(newEntriesSnapshot));
        LogInstallPerformance("playlist_ref_replace targetsSong=" + targets.SongFiles.Count + " targetsPending=" + targets.PendingFiles.Count + " oldEntryCount=" + oldEntriesSnapshot.Count + " newEntryCount=" + newEntriesSnapshot.Count);
        if (oldTable != null)
        {
            RemoveReferenceBMSTables(oldTable, targets.SongFiles);
            RemoveReferenceBMSTables(oldTable, targets.PendingFiles);
        }
        if (newTable != null)
        {
            AddReferenceBMSTables(newTable, targets.SongFiles);
            AddReferenceBMSTables(newTable, targets.PendingFiles);
        }
    }

    private List<BMSTableEntry> SnapshotPlaylistReferenceEntries(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (entries != null)
        {
            return entries.Where((BMSTableEntry entry) => entry != null && !entry.is_removed).ToList();
        }
        if (table == null)
        {
            return new List<BMSTableEntry>();
        }
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return table.entries.Where((BMSTableEntry entry) => entry != null && !entry.is_removed).ToList();
        }
    }

    private (List<BMSFile> SongFiles, List<BMSFile> PendingFiles) ResolvePlaylistReferenceTargets(IEnumerable<BMSTableEntry> entries)
    {
        HashSet<string> md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? Enumerable.Empty<BMSTableEntry>())
        {
            if (entry == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entry.md5))
            {
                md5Hashes.Add(entry.md5);
            }
            if (!string.IsNullOrWhiteSpace(entry.sha256))
            {
                sha256Hashes.Add(entry.sha256);
            }
        }
        return (FilterPlaylistReferenceTargets(SnapshotSongFilesForPlaylistReferenceApply(), md5Hashes, sha256Hashes), FilterPlaylistReferenceTargets(SnapshotPendingFilesForPlaylistReferenceApply(), md5Hashes, sha256Hashes));
    }

    private static List<BMSFile> FilterPlaylistReferenceTargets(IEnumerable<BMSFile> files, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (files == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return new List<BMSFile>();
        }
        return files.Where(delegate(BMSFile file)
        {
            if (file == null)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(file.hash) && md5Hashes.Contains(file.hash))
            {
                return true;
            }
            return !string.IsNullOrWhiteSpace(file.sha256) && sha256Hashes.Contains(file.sha256);
        }).Distinct().ToList();
    }

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSFile> files)
    {
        if (table == null || files == null)
        {
            return;
        }
        foreach (BMSFile file in files)
        {
            file.AddRefTable(table);
        }
    }

    internal void RefreshReferenceDisplayForFiles(IEnumerable<BMSFile> files, bool suppressFilePropertyChanged = false)
    {
        if (files == null)
        {
            return;
        }
        foreach (BMSFile file in files.Where((BMSFile file) => file != null).Distinct())
        {
            file.RefreshRefTablesDisplayCache(suppressFilePropertyChanged);
        }
    }

    internal void RefreshReferenceDisplayForTable(BMSTable table, IEnumerable<BMSFile> files = null, bool suppressFilePropertyChanged = false)
    {
        if (table == null)
        {
            return;
        }
        if (files != null)
        {
            RefreshReferenceDisplayForFiles(files.Where((BMSFile file) => file != null && file.HasRefTable(table)), suppressFilePropertyChanged);
            return;
        }
        List<BMSFile> list = new List<BMSFile>();
        using (rwlockBMSFiles.GetReaderGuard())
        {
            if (BMSFiles != null)
            {
                list.AddRange(BMSFiles.Where((BMSFile file) => file != null && file.HasRefTable(table)));
            }
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            if (BMSPackagesPending != null)
            {
                list.AddRange(BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Where((BMSFile file) => file != null && file.HasRefTable(table)));
            }
        }
        RefreshReferenceDisplayForFiles(list, suppressFilePropertyChanged);
    }

    internal void SynchronizeReferenceBMSTables(IEnumerable<BMSTable> tables, bool suppressFilePropertyChanged = false)
    {
        List<BMSTable> list = ((tables != null) ? tables.Where((BMSTable table) => table != null).Distinct().ToList() : new List<BMSTable>());
        HashSet<BMSTable> hashSet = new HashSet<BMSTable>(list);
        Action<IEnumerable<BMSFile>> action = delegate(IEnumerable<BMSFile> files)
        {
            foreach (BMSFile file in files.Where((BMSFile file) => file != null))
            {
                file.RemoveRefTablesNotIn(hashSet, suppressFilePropertyChanged);
            }
        };
        if (BMSFiles != null && BMSFiles.Count > 0)
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                action(BMSFiles);
            }
        }
        if (BMSPackagesPending != null && BMSPackagesPending.Count > 0)
        {
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                action(BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles));
            }
        }
        if (list.Count > 0)
        {
            AddReferenceBMSTables(list, null, suppressFilePropertyChanged);
        }
    }

    /// <summary>
    /// 指定されたプレイリストの参照を BMS ファイル群から削除します。
    /// </summary>
    public void RemoveReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        if (entries == null)
        {
            Action<IEnumerable<BMSFile>> action = delegate(IEnumerable<BMSFile> files)
            {
                foreach (BMSFile file in files.Where((BMSFile file) => file != null))
                {
                    file.RemoveRefTable(table);
                }
            };
            if (BMSFiles != null && BMSFiles.Count > 0)
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    action(BMSFiles);
                }
            }
            if (BMSPackagesPending == null || BMSPackagesPending.Count <= 0)
            {
                return;
            }
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                action(BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles));
            }
            return;
        }
        if (BMSFiles != null && BMSFiles.Count > 0)
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                removeReferenceBMSTables(table, entries, BMSFiles);
            }
        }
        if (BMSPackagesPending == null || BMSPackagesPending.Count <= 0)
        {
            return;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            removeReferenceBMSTables(table, entries, BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles));
        }
    }

    public void RemoveReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> list = tables?.Where((BMSTable table) => table != null).Distinct().ToList();
        if (list == null || list.Count == 0)
        {
            return;
        }
        Action<IEnumerable<BMSFile>> action = delegate (IEnumerable<BMSFile> l)
        {
            list.AsParallel().ForAll(delegate (BMSTable table)
            {
                foreach (BMSFile file in l.Where((BMSFile file) => file != null))
                {
                    file.RemoveRefTable(table);
                }
            });
        };
        if (BMSFiles != null && BMSFiles.Count > 0)
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                action(BMSFiles);
            }
        }
        if (BMSPackagesPending == null || BMSPackagesPending.Count <= 0)
        {
            return;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            action(BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles));
        }
    }

    public void RemoveReferenceBMSTables(BMSTable table, IEnumerable<BMSFile> files)
    {
        if (table == null || files == null)
        {
            return;
        }
        foreach (BMSFile file in files)
        {
            file.RemoveRefTable(table);
        }
    }

    private void removeReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries, IEnumerable<BMSFile> files)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            RemoveReferenceBMSTables(table, files.Where((BMSFile f) => (from e in entries
                                                                        where !string.IsNullOrWhiteSpace(e.md5) && !e.is_removed
                                                                        select e.md5).Contains(f.hash)));
        }
    }

    public List<string> GetMD5sOfTheSameSong(BMSFile file)
    {
        if (file == null)
        {
            throw new ArgumentNullException("file");
        }
        List<string> list = new List<string>();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (!BMSFiles.Contains(file))
                {
                    return null;
                }
                file.SetHealthStatus(bmsFolderAllFileList);
                if (file.maintenanceInfo.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile)
                {
                    string dirname = DirectoryExt.GetDirectoryNameSimple(file.path);
                    return (from f in BMSFiles.Where(delegate (BMSFile f)
                        {
                            if (!DirectoryExt.GetDirectoryNameSimple(f.path).Equals(dirname, StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                            f.SetHealthStatus(bmsFolderAllFileList);
                            return file.maintenanceInfo.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile;
                        })
                            select f.hash).Distinct().ToList();
                }
                return null;
            }
        }
    }

    public static string GetLCSBMSInfo(IEnumerable<string> strings)
    {
        List<string> list = (from s in strings.Where((string s) => !string.IsNullOrWhiteSpace(s)).SelectMany((string s1, int i) => from input in strings.Skip(i + 1)
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
                             select s).ToList();
        if (list.Count == 0)
        {
            list = strings.Select(delegate (string s)
            {
                s = s.NaturalNormalizationForFileName().Trim();
                Match match = endKakkoRegex.Match(s);
                if (match.Success && kakkoInnerRegex.IsMatch(match.Groups[0].Value))
                {
                    s = s.ReplaceFromEnd(match.Groups[0].Value, string.Empty);
                }
                return s;
            }).ToList();
        }
        list = list.Select(delegate (string s)
        {
            s = customTrimStartRegex1.Replace(s, string.Empty);
            s = customDeleteRegex1.Replace(s.Trim(), string.Empty);
            s = customTrimEndRegex1.Replace(s, string.Empty);
            s = customTrimEndRegex2.Replace(s, string.Empty);
            s = s.ReplaceInvalidFileNameCharsByWide();
            s = customMatchRegex1.Replace(s, (Match m) => m.Value.Trim());
            s = customMatchRegex2.Replace(s, (Match m) => "[" + m.Groups[1].Value.Trim() + "]");
            s = doubleSpacesRegex.Replace(s, string.Empty).Trim();
            return s;
        }).ToList();
        return (from s in list
                group s by s into g
                orderby Math.Pow(g.Key.Length, 0.65) * (double)g.Count() descending
                select g.Key).FirstOrDefault();
    }

    private string createBMSFolderPath(IEnumerable<BMSFile> bmsFiles, string parentDir, string longestFileName = "")
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        int num = 250;
        int num2 = 128;
        Encoding encoding = Encoding.GetEncoding("Shift_JIS");
        string lCSBMSInfo = GetLCSBMSInfo(bmsFiles.Select((BMSFile f) => f.Title));
        string lCSBMSInfo2 = GetLCSBMSInfo(bmsFiles.Select((BMSFile f) => f.Artist));
        string s = options.FolderNameFormat.Replace("%ARTIST%", lCSBMSInfo2).Replace("%TITLE%", lCSBMSInfo).Trim();
        if (options.UseOnlyShiftJISChars)
        {
            s = s.ToSjisSchemeString();
        }
        s = s.RemoveInvalidFileNameChars();
        if (string.IsNullOrWhiteSpace(s))
        {
            s = Resources.NewFolderName;
        }
        while (encoding.GetByteCount(parentDir + Path.DirectorySeparatorChar + s + Path.DirectorySeparatorChar + longestFileName) > num || encoding.GetByteCount(s) > num2)
        {
            if (s.Length <= 1)
            {
                throw new PathTooLongException(string.Format(Resources.Error_PathTooLong, parentDir + Path.DirectorySeparatorChar + s + Path.DirectorySeparatorChar + longestFileName));
            }
            s = s.Substring(0, s.Length - 1);
        }
        return Path.Combine(parentDir, s).Trim();
    }

    /// <summary>
    /// BMS ファイル群を指定された別のディレクトリへマージ（統合移動）します。
    /// </summary>
    public void MergeBMSDirectory(string src, string dst)
    {
        if (src == null)
        {
            throw new ArgumentNullException("src");
        }
        if (dst == null)
        {
            throw new ArgumentNullException("dst");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LibraryMergeResult mergeResult = libraryFileOperationsService.PrepareMergeDirectory(
                        src,
                        dst,
                        BMSFiles,
                        BmsonSongs,
                        BMSPackagesPending,
                        BMSPackagesInstalled,
                        CreateInstalledChartKeySnapshotExcludingUnsafe);
                    if (!mergeResult.Success)
                    {
                        return;
                    }
                    List<BMSFile> sourceBmsFiles = mergeResult.SourceFiles.Where(PendingChartEntry.IsBmsChartFile).ToList();
                    List<LR2SongDBExtended.bmson_song> sourceBmsonSongs = mergeResult.SourceFiles
                        .OfType<PendingChartEntry>()
                        .Where((PendingChartEntry entry) => entry.BmsonSong != null)
                        .Select((PendingChartEntry entry) => entry.BmsonSong)
                        .Distinct()
                        .ToList();
                    unregisterBMSFiles(sourceBmsFiles);
                    unregisterBmsonSongs(sourceBmsonSongs);
                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                    foreach (string item in bmsFolderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        bmsFolderAllFileList.RemoveDir(item);
                        reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.RemoveDirWithResult(item));
                        directoryRelativePathHashIndex.RemoveDir(item);
                    }
                    if (!moveBMSPackageFiles(mergeResult.Repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true, existingHashes: mergeResult.ExistingHashes))
                    {
                        dialogService.Show(string.Format(Resources.Error_BmsFolderMergeFailed, src, dst), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    BmsScanResult mergedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots(new[] { dst });
                    foreach (string chartDirectory in mergedDirectoryScan.ChartDirectories)
                    {
                        if (!mergedDirectoryScan.SelfOwnedAllResourceBaseNameHashesByChartDirectory.TryGetValue(chartDirectory, out uint[] hashes))
                        {
                            mergedDirectoryScan.AllResourceBaseNameHashesByChartDirectory.TryGetValue(chartDirectory, out hashes);
                        }
                        if (hashes != null)
                        {
                            bmsFolderAllFileList.AddDirHashed(chartDirectory, hashes);
                        }
                        reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(chartDirectory, mergedDirectoryScan));
                        directoryRelativePathHashIndex.AddDir(chartDirectory, mergedDirectoryScan);
                    }
                    LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);
                    ApplyLibraryMutationDelta(mergeResult.ReferenceMutationDelta);
                    List<BMSFile> movedBmsFiles = mergeResult.Repackage.BMSFiles.Where(PendingChartEntry.IsBmsChartFile).ToList();
                    List<LR2SongDBExtended.bmson_song> movedBmsonSongs = BuildBmsonSongsFromChartRows(mergeResult.Repackage.BMSFiles);
                    dbGateway.UpsertSongs(movedBmsFiles);
                    if (movedBmsonSongs.Count > 0)
                    {
                        dbGateway.UpsertBmsonSongs(movedBmsonSongs);
                    }
                    List<BMSFile> maintenanceTargets = movedBmsFiles.Concat(BMSFiles.Where((BMSFile f) => f.path.StartsWith(dst + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
                    setMaintenanceInfo(maintenanceTargets, forceUpdate: true);
                    HashSet<string> repackageBmsPathSet = new HashSet<string>(movedBmsFiles.Select((BMSFile ff) => ff.path), StringComparer.OrdinalIgnoreCase);
                    BMSFiles = BMSFiles.Where((BMSFile f) => !repackageBmsPathSet.Contains(f.path)).Concat(movedBmsFiles).ToList();
                    if (movedBmsonSongs.Count > 0)
                    {
                        Dictionary<string, LR2SongDBExtended.bmson_song> nextBmsonByPath = (BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>())
                            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
                            .ToDictionary((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase);
                        foreach (LR2SongDBExtended.bmson_song movedBmsonSong in movedBmsonSongs)
                        {
                            nextBmsonByPath[movedBmsonSong.path] = movedBmsonSong;
                        }
                        BmsonSongs = nextBmsonByPath.Values.OrderBy((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            }
        }
    }

    /// <summary>
    /// リンク切れ BMS ファイルのインストール先ディレクトリを修正し、song.db のパスを更新します。
    /// </summary>
    public void FixInstallationDirectory(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<BMSFile> files = bmsFiles.Where((BMSFile f) => f != null && !string.IsNullOrWhiteSpace(f.instl_dst)).ToList();
                HashSet<string> existingHashes = CreateBMSHashSnapshotExcludingUnsafe(files);
                LibraryFixInstallationResult result = libraryFileOperationsService.FixInstallationDirectory(
                    files,
                    existingHashes,
                    (package, destinationDirectory) => moveBMSPackageFiles(package, destinationDirectory, showMessageBoxOnInstallFail: true, deleteAllContents: false, existingHashes: existingHashes),
                    delegate (BMSFile file)
                    {
                        return dialogService.Show(string.Format(Resources.Confirm_DuplicateReinstallSkipped, file.path, string.Join(Environment.NewLine, from x in BMSFiles.Where((BMSFile f) => f.hash == file.hash).Except(new BMSFile[1] { file })
                                                                                                                                                select x.path)), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                    });
                ApplyLibraryMutationDelta(result.MutationDelta);
                if (result.FilesToRemove.Count > 0)
                {
                    RemoveBMSFiles(result.FilesToRemove);
                }
                if (result.MaintenanceTargets.Count > 0)
                {
                    setMaintenanceInfo(result.MaintenanceTargets, forceUpdate: true);
                }
            }
        }
    }

    /// <summary>
    /// BMS ファイル群のフォルダ名をメタデータに基づいて自動リネームします。
    /// </summary>
    public void AutoRenameBMSFolder(IEnumerable<BMSFile> bmsFiles, bool renameRootFolder = false)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    List<string> rootFolders = getBMSDirectories();
                    List<BMSFile> chartRows = GetLibraryChartRowsForFolderOperations();
                    List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildAutoRenamePlans(bmsFiles, chartRows, rootFolders, renameRootFolder, createBMSFolderPath);
                    if (plans.Any((FolderAutoRenamePlan plan) => !string.IsNullOrWhiteSpace(plan.SourceDirectory) && Path.GetPathRoot(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase)))
                    {
                        dialogService.Show(Resources.Warn_DriveRootBmsSkipped, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    foreach (FolderAutoRenamePlan plan in plans)
                    {
                        if (plan?.FailureException != null)
                        {
                            dialogService.Show(string.Format(Resources.Error_RenameFailed, plan.SourceDirectory, plan.FailureException.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(plan?.DestinationDirectory) || string.IsNullOrWhiteSpace(plan.SourceDirectory))
                        {
                            continue;
                        }
                        RenameBMSFolder(plan.SourceDirectory, Path.GetFileName(plan.DestinationDirectory), false, renameRootFolder: true);
                    }
                }
            }
        }
    }

    private List<BMSFile> GetLibraryChartRowsForFolderOperations()
    {
        List<BMSFile> chartRows = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        chartRows.AddRange((BmsonSongs ?? new List<LR2SongDBExtended.bmson_song>())
            .Select(PendingChartEntry.CreateFromBmsonSong)
            .Where((PendingChartEntry entry) => entry != null));
        return chartRows;
    }

    /// <summary>
    /// BMS フォルダを新しい名前にリネームし、song.db のパス情報を更新します。
    /// </summary>
    public void RenameBMSFolder(string srcDir, string newName, bool? unregister = false, bool renameRootFolder = false)
    {
        if (srcDir == null)
        {
            throw new ArgumentNullException("srcDir");
        }
        if (newName == null)
        {
            throw new ArgumentNullException("newName");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (!renameRootFolder && getBMSDirectories().Contains(srcDir, StringComparer.OrdinalIgnoreCase))
        {
            dialogService.Show(string.Format(Resources.Warn_CannotRenameRootFolder, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (options.UseOnlyShiftJISChars)
        {
            newName = newName.ToSjisSchemeString();
        }
        newName = newName.RemoveInvalidFileNameChars();
        if (string.IsNullOrWhiteSpace(newName) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!Directory.Exists(srcDir))
        {
            dialogService.Show(string.Format(Resources.Warn_RenameFolderNotExists, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
                    MoveBmsFolderInternal(srcDir, dstDir, unregister, raiseBmsFilesChanged: false);
                    if (unregister == false)
                    {
                        InvalidateDuplicatedCache();
                    }
                }
            }
        }
    }

    /// <summary>
    /// BMS ファイル群のルートフォルダを別の親ディレクトリへ移動します。
    /// </summary>
    public void MoveBMSRootFolder(IEnumerable<BMSFile> bmsFiles, string dstDir, bool? unregister = false)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        MoveLibraryRootFolder((bmsFiles ?? Enumerable.Empty<BMSFile>()).Select(LibraryChartRef.FromBmsFile), dstDir, unregister);
    }

    internal void MoveLibraryRootFolder(IEnumerable<LibraryChartRef> charts, string dstDir, bool? unregister = false)
    {
        if (charts == null)
        {
            throw new ArgumentNullException("charts");
        }
        if (dstDir == null)
        {
            throw new ArgumentNullException("dstDir");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    if (!Directory.Exists(dstDir))
                    {
                        dialogService.Show(string.Format(Resources.Error_MoveDestRootNotFound, dstDir), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    List<LibraryChartRef> chartList = (charts ?? Enumerable.Empty<LibraryChartRef>()).Where((LibraryChartRef chart) => chart != null).ToList();
                    List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildRootFolderMovePlans(chartList, dstDir);
                    if (chartList.Select((LibraryChartRef chart) => DirectoryExt.GetDirectoryNameSimple(chart.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Any((string f) => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        dialogService.Show(Resources.Warn_DriveRootCannotChangeRoot, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    foreach (FolderAutoRenamePlan plan in plans)
                    {
                        MoveBmsFolderInternal(plan.SourceDirectory, plan.DestinationDirectory, unregister, raiseBmsFilesChanged: true);
                    }
                    if (unregister == false)
                    {
                        RaisePropertyChanged(() => BMSFiles);
                    }
                }
            }
        }
    }

    public void moveBMSFolder(string srcDir, string dstDir, bool? unregister = false)
    {
        MoveBmsFolderInternal(srcDir, dstDir, unregister, raiseBmsFilesChanged: true);
    }

    private void MoveBmsFolderInternal(string srcDir, string dstDir, bool? unregister, bool raiseBmsFilesChanged)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (File.Exists(dstDir) || Directory.Exists(dstDir))
        {
            dialogService.Show(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        try
        {
            libraryFileOperationsService.MoveFolderAndUpdateReferences(srcDir, dstDir, bmsFolderAllFileList, directoryResourceLookupCache, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
        }
        catch (Exception moveException)
        {
            dialogService.Show(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        if (unregister != false && unregister != true)
        {
            return;
        }
        LibraryMutationDelta delta = libraryFileOperationsService.BuildFolderMoveDelta(srcDir, dstDir, BMSFiles, BmsonSongs, BMSPackagesPending, BMSPackagesInstalled, unregister == true, raiseBmsFilesChanged);
        ApplyLibraryMutationDelta(delta);
    }

    public void MoveBMSFile(BMSFile bmsFile, string dstPath, bool? unregister = false)
    {
        MoveChartFile(LibraryChartRef.FromBmsFile(bmsFile), dstPath, unregister);
    }

    internal void MoveChartFile(LibraryChartRef chart, string dstPath, bool? unregister = false)
    {
        MoveLibraryChart(chart, dstPath, unregister);
    }

    internal void MoveLibraryChart(LibraryChartRef chart, string dstPath, bool? unregister = false)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                if (chart == null || string.IsNullOrWhiteSpace(chart.Path) || !File.Exists(chart.Path) || IsRegisteredChartPath(dstPath))
                {
                    return;
                }
                if (File.Exists(dstPath) || Directory.Exists(dstPath))
                {
                    dialogService.Show(string.Format(Resources.Warn_RenameDestAlreadyExists, chart.Path, dstPath), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return;
                }
                try
                {
                    libraryFileOperationsService.MoveChartFileOnDisk(chart, dstPath, fileMutationService, targetOnlyFileMutationOptions);
                }
                catch (Exception moveException)
                {
                    dialogService.Show(string.Format(Resources.Error_BmsFileMoveFailed, chart.Path, dstPath, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    return;
                }
                if (unregister != false && unregister != true)
                {
                    return;
                }
                ApplyLibraryMutationDelta(libraryFileOperationsService.BuildChartFileMoveDelta(chart, dstPath, unregister == true));
            }
        }
    }

    private bool IsRegisteredChartPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && ((BMSFiles?.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f?.path) && f.path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? false)
                || (BmsonSongs?.Any((LR2SongDBExtended.bmson_song song) => !string.IsNullOrWhiteSpace(song?.path) && song.path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? false));
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

    public void RenameBMSFilesExtensions(IEnumerable<BMSFile> bmsFiles, string newExt, bool? unregister = false)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                LibraryMutationDelta delta = libraryFileOperationsService.RenameLibraryFileExtensions(
                    bmsFiles,
                    newExt,
                    unregister == true,
                    (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, unregister == true));
                foreach (LibraryDeleteFailure failure in delta.Failures)
                {
                    if (failure.Exception == null)
                    {
                        continue;
                    }
                    dialogService.Show(
                        string.Format(Resources.Error_BmsFileMoveFailed, failure.Path, newExt, GetDisplayedExceptionMessage(failure.Exception)),
                        Resources.MessageBoxTitle_Error,
                        MessageBoxButton.OK,
                        MessageBoxImage.Hand,
                        MessageBoxResult.OK);
                }
                ApplyLibraryMutationDelta(delta);
                NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=normal total=" + (bmsFiles?.Count() ?? 0) + " renamed=" + delta.RenamedCount + " deleted=" + delta.DuplicateDeletedCount + " skipped=" + delta.SkippedCount);
            }
        }
    }

    public void RenamePendingBMSFilesExtensions(IEnumerable<BMSFile> bmsFiles, string newExt)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    PendingExtensionRenameResult result = packageInstallService.RenamePendingFileExtensions(
                        bmsFiles,
                        newExt,
                        (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false));
                    foreach (PendingExtensionRenameFailure failure in result.Failures)
                    {
                        if (failure?.Outcome?.FailureException == null || failure.File == null)
                        {
                            continue;
                        }
                        if (failure.Outcome.FailedDuringDelete)
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(result.FilesToRemove);
                    NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=pending total=" + result.Total + " renamed=" + result.Renamed + " deleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " totalMs=" + result.TotalMs);
                }
            }
        }
    }

    /// <summary>
    /// 指定された BMS ファイル群をライブラリおよびファイルシステムから削除します。
    /// </summary>
    public void RemoveBMSFiles(IEnumerable<BMSFile> bmsFiles, bool sendToRecycleBin = true)
    {
        RemoveChartFiles((bmsFiles ?? Enumerable.Empty<BMSFile>()).Select(LibraryChartRef.FromBmsFile), sendToRecycleBin);
    }

    internal void RemoveChartFiles(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true)
    {
        RemoveLibraryCharts(charts, sendToRecycleBin);
    }

    internal void RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LibraryRemovalResult result = libraryFileOperationsService.DeleteLibraryCharts(
                        charts,
                        (BMSFiles ?? Enumerable.Empty<BMSFile>()).Select(LibraryChartRef.FromBmsFile)
                            .Concat((BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>()).Select(LibraryChartRef.FromBmsonSong)),
                        BMSPackagesPending,
                        bmsFolderAllFileList,
                        directoryResourceLookupCache,
                        sendToRecycleBin,
                        (folderPath) => dialogService.Show(string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        recursiveDirectoryTreeFileMutationOptions);
                    foreach (LibraryDeleteFailure failure in result.Failures)
                    {
                        if (failure.IsDirectory)
                        {
                            dialogService.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    List<BMSFile> removedBmsFiles = result.RemovedCharts
                        .Where((LibraryChartRef chart) => chart?.Kind == LibraryChartKind.Bms && chart.BmsFile != null)
                        .Select((LibraryChartRef chart) => chart.BmsFile)
                        .ToList();
                    List<LR2SongDBExtended.bmson_song> removedBmsonSongs = result.RemovedCharts
                        .Where((LibraryChartRef chart) => chart?.Kind == LibraryChartKind.Bmson && chart.BmsonSong != null)
                        .Select((LibraryChartRef chart) => chart.BmsonSong)
                        .Distinct()
                        .ToList();
                    if (removedBmsFiles.Count > 0)
                    {
                        unregisterBMSFiles(removedBmsFiles);
                    }
                    if (removedBmsonSongs.Count > 0)
                    {
                        unregisterBmsonSongs(removedBmsonSongs);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pending 状態の BMS ファイル群を Pending リストおよびファイルシステムから削除します。
    /// </summary>
    public void RemovePendingBMSFiles(IEnumerable<BMSFile> bmsFiles, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    PendingFileDeletionResult result = packageInstallService.DeletePendingFiles(
                        bmsFiles,
                        BMSPackagesPending,
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
                            dialogService.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(result.FilesToRemove);
                }
            }
        }
    }

    private List<BMSPackage> GetPendingPackagesFullyCoveredBySelection(HashSet<string> selectedPaths, HashSet<BMSFile> selectedFileRefs)
    {
        return libraryFileOperationsService.GetPendingPackagesFullyCoveredBySelection(BMSPackagesPending, selectedPaths, selectedFileRefs);
    }

    private void RemovePendingFilesFromPendingPackagesAndInstallRows(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> list = bmsFiles.Where((BMSFile f) => f != null).ToList();
        if (list.Count == 0)
        {
            return;
        }
        stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(filesToRemove: list));
    }

    private void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        stateApplier.ApplyLibraryMutationDelta(delta);
    }

    /// <summary>
    /// BMS ファイル群を song.db から登録解除（レコード削除）します。
    /// </summary>
    private void unregisterBMSFiles(List<BMSFile> bmsFiles)
    {
        stateApplier.UnregisterBmsFiles(bmsFiles);
    }

    private void unregisterBmsonSongs(List<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        stateApplier.UnregisterBmsonSongs(bmsonSongs);
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
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                dbGateway.UpsertSongs(_bmsFiles);
            }
        }
    }
}
