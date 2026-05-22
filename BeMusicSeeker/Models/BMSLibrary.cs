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

        internal List<BMSScore> Scores { get; set; } = [];

        internal Dictionary<string, BMSScore> ScoresByHash { get; set; } = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, BMSScore> ScoresBySha256 { get; set; } = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);

        internal ActiveScoreSource ActiveScoreSource { get; set; }
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
        internal List<ChartPackage> EstimablePackages { get; } = [];

        internal List<ChartPackage> DeferredPackages { get; } = [];

        internal Dictionary<ChartPackage, int> DeferredSourceHealthByPackage { get; } = [];

        internal PendingEstimateSourceBatchSnapshot BatchSourceSnapshot { get; set; }
    }

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.BMSLibrary");

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

    private readonly string lr2SongDBPath;

    private readonly string lr2ScoreDBPath;

    private Dictionary<string, BMSScore> beatorajaScoresBySha256 = new(StringComparer.OrdinalIgnoreCase);

    private ActiveScoreSource activeScoreSource;

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

    private readonly object lockInstalledDirectoryIndex = new();

    private InstalledChartDirectoryIndexSnapshot installedDirectoryIndex = new();

    private bool installedDirectoryIndexInitialized;

    private readonly object lockInstalledChartKeyIndex = new();

    private readonly HashSet<string> installedChartKeyIndex = new(StringComparer.OrdinalIgnoreCase);

    private bool installedChartKeyIndexInitialized;

    private readonly object lockInstallEstimationMetadataProfileCache = new();

    private readonly Dictionary<string, InstallEstimationMetadataProfile> installEstimationMetadataProfileCache = new(StringComparer.OrdinalIgnoreCase);

    // Lock acquisition order for facade orchestration:
    // rwlockBMSFilesInitializedAll / rwlockBMSFilesInitializedMin
    // -> rwlockPendingInstallCharts
    // -> rwlockBMSFiles
    // -> rwlockSongDBInstall / rwlockSongDBMaintenance
    // -> rwlockBMSScores
    private readonly ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedAll = new();

    private readonly ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedMin = new();

    private readonly ReaderWriterLockSlimWrapper rwlockDuplicateChartGroups = new();

    private readonly ReaderWriterLockSlimWrapper rwlockPendingInstallCharts = new();

    private readonly ReaderWriterLockSlimWrapper rwlockLR2IrDir = new();

    private readonly ReaderWriterLockSlimWrapper rwlockBMSScores = new();

    private readonly ReaderWriterLockSlimWrapper rwlockBMSFiles = new();

    private readonly ReaderWriterLockSlimWrapper rwlockSongDBInstall = new();

    private readonly ReaderWriterLockSlimWrapper rwlockSongDBMaintenance = new();

    private int deferredInstallableMaintenanceRequestedVersion;

    private bool deferredInstallableMaintenanceRunning;

    private int deferredInstallableMaintenanceLastCompletedVersion;

    private long deferredInstallableMaintenanceCriticalElapsedMs;

    private readonly object lockDeferredInstallableMaintenance = new();

    private int deferredMaintenanceHydrationRequestedVersion;

    private bool deferredMaintenanceHydrationRunning;

    private int deferredMaintenanceHydrationLastCompletedVersion;

    private readonly object lockDeferredMaintenanceHydration = new();

    private readonly object lockChartInfoBackfill = new();

    private readonly List<ChartInfoBackfillRequest> chartInfoBackfillRequests = [];

    private readonly object lockChartInfoHydration = new();

    private bool chartInfoHydrationRunning;

    private bool chartInfoHydrationPending;

    private string chartInfoHydrationPendingReason;

    private bool chartInfoHydrationPendingQueueBackfill;

    private readonly object lockChartInfoIndex = new();

    private Dictionary<string, LR2SongDBExtended.chart_info> chartInfoIndexBySha256 = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> chartInfoIndexByMd5 = new(StringComparer.OrdinalIgnoreCase);

    private readonly object lockScoreSnapshot = new();

    private ScoreSnapshot scoreSnapshot;

    private int scoreSnapshotVersion;

    private readonly object lockPlaylistSummaryOwnedHashSnapshot = new();

    private PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot;

    private int playlistSummaryOwnedHashSnapshotVersion;

    private int chartInfoBackfillRequestedVersion;

    private int chartInfoBackfillCompletedVersion;

    private int chartInfoBackfillHydrationBypassUntilVersion;

    private int chartInfoHydrationRequestedVersion;

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

    private List<BMSFile> _BMSFiles = [];

    private List<LR2SongDBExtended.bmson_song> _BmsonSongs = [];

    private IReadOnlyList<ChartFile> latestInstallDestinationChangedCharts = [];

    private readonly object latestInstallDestinationChangedChartsLock = new();

    private readonly object installDestinationRuntimeStatesLock = new();

    private readonly Dictionary<string, InstallDestinationRuntimeStateEntry> installDestinationRuntimeStatesByKey = new(StringComparer.OrdinalIgnoreCase);

    private readonly object resourceHealthIndexLock = new();

    private ResourceHealthIndexSnapshot resourceHealthIndexSnapshot = ResourceHealthIndexSnapshot.Empty;

    private bool resourceHealthIndexInvalidated = true;

    private int resourceHealthIndexVersionSeed;

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

    private int _ChartInfoBackfillDigestBackfilledCount;

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

    private readonly object lockLibraryInitializationProgress = new();

    private int _ChartInfoIndexVersion;

    private bool _ChartInfoIndexHydrated;

    private bool _IsWriteLockHeldInitializeBMSFilesHealthStatus = true;

    private bool _IsWriteLockHeldInitializeBMSFilesEncodingInfo = true;

    private bool _IsWriteLockHeldInitializeBMSFilesZeroNote = true;

    private static readonly Regex lr2IRScoreRegex = new("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static readonly Uri rankingInfoUrl = new("http://www.ribbit.xyz/bms/services/lr2ircache/ranking");

    private static readonly Uri rankingDataUrl = new("http://www.ribbit.xyz/bms/services/lr2ircache/ranking/");

    private static readonly Uri songInfoUrl = new("http://www.ribbit.xyz/bms/services/lr2ircache/info/");

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
                InvalidatePlaylistSummaryOwnedHashSnapshot();
                InvalidateInstalledChartKeyIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
                InvalidateDuplicateChartGroupsCache();
                InvalidateResourceHealthIndex("bmsfiles_changed");
                PruneInstallDestinationRuntimeStatesToCurrentStorageRows();
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                    RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
                }).Logging("BMSFiles");
                RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
            }
        }
    }

    public List<BMSFile> BMSFilesUnregistered => [.. BMSFiles.Where(f => string.IsNullOrWhiteSpace(f.parent))];

    internal IReadOnlyList<ChartFile> ConsumeLatestInstallDestinationChangedCharts()
    {
        lock (latestInstallDestinationChangedChartsLock)
        {
            IReadOnlyList<ChartFile> charts = latestInstallDestinationChangedCharts;
            latestInstallDestinationChangedCharts = [];
            return charts;
        }
    }

    internal IEnumerable<ChartFile> ChartFilesNeedResourceFix => GetChartsNeedResourceFix(null);

    public List<LR2SongDBExtended.bmson_song> BmsonSongs
    {
        get
        {
            return _BmsonSongs;
        }
        set
        {
            List<LR2SongDBExtended.bmson_song> normalized = value ?? [];
            if (_BmsonSongs != normalized)
            {
                _BmsonSongs = normalized;
                InvalidatePlaylistSummaryOwnedHashSnapshot();
                InvalidateInstalledChartKeyIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
                InvalidateInstallEstimationMetadataProfileCache();
                InvalidateDuplicateChartGroupsCache();
                InvalidateResourceHealthIndex("bmsons_changed");
                PruneInstallDestinationRuntimeStatesToCurrentStorageRows();
                Task.Run(delegate
                {
                    RaisePropertyChanged("BmsonSongs");
                    RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
                }).Logging("BmsonSongs");
                RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
            }
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

    private void InvalidateDuplicateChartGroupsCache()
    {
        DuplicateChartGroups = null;
    }

    public IEnumerable<BMSFile> BMSFilesGarbled => GetBMSFilesGarbled(BMSFiles);

    public IEnumerable<BMSFile> BMSFilesGarbledFixed => GetBMSFilesGarbled(BMSFiles, forceUpdate: false, isInFixedList: true);

    internal IEnumerable<ChartFile> ChartFilesZeroNote => maintenanceService.GetZeroNoteCharts(
        ChartFileProjection.FromBmsFiles(BMSFiles, includeWarningSnapshot: false),
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

    private List<ChartFile> CreateInstalledChartSnapshotForParentFolderCache()
    {
        List<BMSFile> bmsFilesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = [.. BMSFiles];
        }
        return CreateInstalledChartSnapshot(bmsFilesSnapshot, BmsonSongs);
    }

    /// <summary>
    /// インストール済み譜面のスナップショットから、親フォルダの候補リストを構築します。
    /// カスタムフォルダ出力先ディレクトリ配下は除外されます。
    /// </summary>
    private List<string> BuildBMSParentFolderCandidates(List<ChartFile> installedChartsSnapshot)
    {
        return parentFolderCacheService.BuildParentFolderCandidates(getBMSDirectories(), installedChartsSnapshot, CurrentOptionsSnapshot);
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
        List<ChartFile> installedChartsSnapshot = CreateInstalledChartSnapshotForParentFolderCache();
        return parentFolderCacheService.BuildSnapshot(version, installedChartsSnapshot, getBMSDirectories(), CurrentOptionsSnapshot);
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
        lock (lockParentFolderList)
        {
            RefreshBMSParentFolderListCacheUnsafe();
            return [.. bmsParentFolderListCache];
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
        List<ChartFile> installedChartsSnapshot = CreateInstalledChartSnapshotForParentFolderCache();
        var stopwatch = Stopwatch.StartNew();
        IEnumerable<string> enumerable = BuildBMSParentFolderCandidates(installedChartsSnapshot);
        List<string> items = [.. enumerable.Except(bmsParentFolderListCache)];
        List<string> items2 = [.. bmsParentFolderListCache.Except(enumerable)];
        bmsParentFolderListCache = [.. enumerable];
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
            value ??= string.Empty;
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
                RaisePropertyChanged(() => ChartInfoBackfillDigestBackfilledCount);
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

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly BmsLibraryDuplicateService duplicateService = new();

    private readonly BmsLibraryParentFolderCacheService parentFolderCacheService = new();

    private readonly BmsLibraryPlaylistReferenceService playlistReferenceService = new(playlistReferenceApplyChunkSize);

    private readonly object playlistReferenceIndexLock = new();

    private PlaylistReferenceIndex playlistReferenceIndex = PlaylistReferenceIndex.Empty;

    private readonly BmsLibraryPackageInstallService packageInstallService = new();

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new();

    private readonly BmsLibraryIrService irService = new();

    private readonly BmsLibraryInitializationService initializationService = new();

    private readonly ChartInfoBuildService chartInfoBuildService = new();

    private readonly IBmsLibraryIrClient irClient = new BmsLibraryIrClient();

    private readonly BmsLibraryMaintenanceService maintenanceService = new();

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
        public InstalledChartDirectoryIndexSnapshot InstalledDirectoryIndexSnapshot { get; set; } = new InstalledChartDirectoryIndexSnapshot();

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
            () => ChartPackagesPending,
            pendingPackages => ChartPackagesPending = pendingPackages,
            () => ChartPackagesInstalled,
            installedPackages => ChartPackagesInstalled = installedPackages,
            InvalidateInstalledDirectoryIndex,
            InvalidateBMSParentFolderListCache,
            InvalidateDuplicateChartGroupsCache,
            () => RaisePropertyChanged(() => BMSFiles),
            () => RaisePropertyChanged(() => ChartPackagesInstalled));
        pendingInstallEstimateQueueProcessor = new PendingInstallEstimateQueueProcessor(ProcessPendingInstallEstimateBatch, UpdatePendingEstimateQueueStatus, HandlePendingEstimateBatchException);
        lr2config = (getLR2Config ?? (Func<LR2Config>)(() => (LR2Config)null));
        using (LR2SongDBExtended lR2SongDBExtended = dbGateway.OpenSongDb())
        {
            BmsLibraryDbGateway.EnsureSongLookupIndexes(lR2SongDBExtended);
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.install>();
            lR2SongDBExtended.CreateTable<LR2SongDBExtended.maintenance>();
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
                    InstalledDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe(),
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

    private int ResolvePendingInstallEstimateParallelPackageDegree()
    {
        return ResolvePendingInstallEstimateParallelPackageDegree(Settings.Default.PendingInstallEstimateMaxParallelPackages);
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
                    markInstalledDestinationAmbiguous: true);
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
        return installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingEntries, evaluationContext?.InstalledDirectoryIndexSnapshot ?? new InstalledChartDirectoryIndexSnapshot());
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
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();
        PendingEstimateSourceBatchSnapshot candidateSnapshot = BuildPendingEstimateSourceBatchSnapshotUnsafe(packageList, installEstimationService, installedDirectoryIndexSnapshot, sourceLogValue, options.UseEverythingForPendingPackageSourceScan);
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
            if (HasInstalledDestinationResolveFailed(state))
            {
                state.Package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
                ApplyPackageMixedInstallWarningsToEntries(state.AlreadyInstalledEntries.Where(entry => entry?.Chart != null && ContainsInstalledChartUnsafe(entry.Chart)));
                ApplyInstalledDestinationResolveFailedToPackageUnsafe(state.Package, state.MissingEntries);
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
            + " elapsedMs=" + candidateSnapshot.ElapsedMs);
        LogInstallPerformance("pending_estimate_source_batch_prefilter source=" + sourceLogValue
            + " packages=" + packageList.Count
            + " estimable=" + result.EstimablePackages.Count
            + " deferred=" + result.DeferredPackages.Count
            + " elapsedMs=" + estimableSnapshot.PrefilterMs);

        return result;
    }

    private PendingEstimateSourceBatchSnapshot BuildPendingEstimateSourceBatchSnapshotUnsafe(List<ChartPackage> packageList, BmsLibraryInstallEstimationService installEstimationService, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, string sourceLogValue, bool useEverythingForPendingPackageSourceScan)
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
                state.PreparationInstalledResolution = installEstimationService.TryResolveInstalledDestinationFromPackage(package, state.MissingEntries, installedDirectoryIndexSnapshot);
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
            snapshot.ScanBackend = sourceSurfaceByRoot.Values.Select(view => view?.ScanBackend).FirstOrDefault(backend => !string.IsNullOrWhiteSpace(backend))
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

        List<string> distinctRoots = [.. (roots ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
        List<List<string>> chunks = [.. distinctRoots
            .Select((root, index) => new { Root = root, Index = index })
            .GroupBy((item) => item.Index / PendingEstimateSourceBatchMaxRootsPerChunk)
            .Select((group) => group.Select((item) => item.Root).ToList())];
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

                var surfaceEntry = new SourceSurfaceEntryView
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
            && !state.PreparationInstalledResolution.Success
            && state.PreparationInstalledResolution.Reason != InstalledDirectoryResolveReason.MultipleCandidateDirectories;
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

    /// <summary>
    /// Chart file scan prefetch result and elapsed time.
    /// </summary>
    private sealed class ChartScanPrefetchInfo
    {
        public ChartScanExecutionResult ScanResult { get; set; }

        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// native bridge を優先し、Everything API が使えない場合は managed scan で chart files を走査します。
    /// </summary>
    private ChartScanExecutionResult ExecuteChartScanWithManagedFallback(List<string> bmsDirectories, Action<string> reportScanner = null)
    {
        IChartFileScanner scanner = new EverythingFileScanner();
        reportScanner?.Invoke("Native");
        ChartScanExecutionResult scanResult = scanner.Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        if (scanResult.Success && scanResult.Result != null)
        {
            return scanResult;
        }

        string nativeFailureReason = scanResult?.ErrorReason ?? "unknown";
        if (IsNativeBridgeContractFailure(nativeFailureReason))
        {
            LogEverythingScan("chart native file scan failed reason=" + nativeFailureReason);
            throw new InvalidOperationException("chart native file scan failed: " + nativeFailureReason);
        }

        LogEverythingScan("chart native file scan unavailable reason=" + nativeFailureReason + " fallback=managed");
        reportScanner?.Invoke("Fallback");
        ChartScanExecutionResult fallbackResult = new FastDirectoryFileScanner().Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        if (!fallbackResult.Success || fallbackResult.Result == null)
        {
            string fallbackFailureReason = fallbackResult?.ErrorReason ?? "unknown";
            LogEverythingScan("chart fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
            throw new InvalidOperationException("chart fallback file scan failed: " + fallbackFailureReason + " (native: " + nativeFailureReason + ")");
        }
        LogEverythingScan("chart fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
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
        Task<ChartScanPrefetchInfo> chartScanPrefetchTask = null;
        if (songTblFileCheck)
        {
            List<string> prefetchDirectories = getBMSDirectories();
            if (prefetchDirectories.Count > 0)
            {
                chartScanPrefetchTask = Task.Run(delegate
                {
                    var stopwatchPrefetch = Stopwatch.StartNew();
                    ChartScanExecutionResult scanResult = ExecuteChartScanWithManagedFallback(
                        prefetchDirectories,
                        scannerLabel => ReportLibraryInitializationProgress(
                            LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            force: true));
                    stopwatchPrefetch.Stop();
                    return new ChartScanPrefetchInfo
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
                        _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, chartScanPrefetchInfo: null, trackLibraryDatabaseProgress: true);
                        if (songTblLoad)
                        {
                            startupInstallReadinessState.MarkCatalogLoaded();
                        }
                    }
                },
                delegate
                {
                    ChartScanPrefetchInfo chartScanPrefetchInfo = null;
                    if (songTblFileCheck && chartScanPrefetchTask != null)
                    {
                        try
                        {
                            chartScanPrefetchInfo = chartScanPrefetchTask.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            LogEverythingScan("chart_scan_prefetch failed message=" + ex.Message);
                            chartScanPrefetchInfo = null;
                        }
                    }
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck, setMainteInfo: false, updateIrScore: true, installTblCheck: false, chartScanPrefetchInfo, trackLibraryFileCheckProgress: true);
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
        ChartScanPrefetchInfo chartScanPrefetchInfo = null,
        bool trackLibraryDatabaseProgress = false,
        bool trackLibraryFileCheckProgress = false)
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
        var options = BmsLibraryOptionsSnapshot.CreateCurrent();
        bool scoreOnlyLoad = !songTblLoad && scoreTblrLoad && !songTblFileCheck && !setMainteInfo && !installTblCheck;
        List<string> bMSDirectories = getBMSDirectories();
        if (bMSDirectories.Count == 0)
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
                        + " lr2Id=" + scoreTableLoadResult.LR2Id);
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
            ApplyLibraryFileScanDiff(options, bMSDirectories, chartScanPrefetchInfo, trackLibraryFileCheckProgress, "initialize");
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
                setOwnedMaintenanceInfo();
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
        if (updateIrScore && activeScoreSource == ActiveScoreSource.Lr2 && lr2ScoreDBPath != null)
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
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<string> bmsDirectories = getBMSDirectories();
        var stopwatch = Stopwatch.StartNew();
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
        ChartScanPrefetchInfo chartScanPrefetchInfo,
        bool trackLibraryFileCheckProgress,
        string reason)
    {
        var emptyResult = new SongTableFileCheckResult();
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
        void completeFileEnumerationOnce()
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
        }

        bool inlineChartInfoApplied = false;
        List<LR2SongDBExtended.chart_info> committedInlineChartInfoRows = [];
        List<ChartFile> currentInstallDestinationCharts = CreateInstalledChartSnapshot(BMSFiles, BmsonSongs, includeResourceReferences: false);
        SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
            dbGateway,
            options,
            BMSFiles,
            chartScanPrefetchInfo?.ScanResult,
            chartScanPrefetchInfo?.ElapsedMs ?? 0L,
            () => ExecuteChartScanWithManagedFallback(
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
            delegate (IReadOnlyList<LR2SongDBExtended.chart_info> rows)
            {
                if (rows == null || rows.Count == 0)
                {
                    return;
                }
                committedInlineChartInfoRows.AddRange(rows.Where(row => row != null));
            },
            currentInstallDestinationCharts);
        completeFileEnumerationOnce();
        using (rwlockBMSFiles.GetWriterGuard())
        {
            BMSFiles = fileCheckResult.NextFiles;
            BmsonSongs = fileCheckResult.NextBmsonSongs;
            libraryResourceIndex = fileCheckResult.NextResourceIndex ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult());
            directoryResourceLookupCache = libraryResourceIndex.DirectoryLookupCache ?? new DirectoryResourceLookupCache();
        }
        if (committedInlineChartInfoRows.Count > 0)
        {
            UpsertChartInfoIndexRows(committedInlineChartInfoRows, "file_diff_inline");
            inlineChartInfoApplied = true;
            committedInlineChartInfoRows.Clear();
        }
        if (inlineChartInfoApplied)
        {
            RaisePropertyChanged(() => ChartFilesZeroNote);
        }
        if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
            || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
            || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
            || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
        {
            RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
        }
        if (fileCheckResult.HasDbDiff)
        {
            InvalidatePlaylistSummaryOwnedHashSnapshot();
            InvalidateInstalledDirectoryIndex();
            InvalidateBMSParentFolderListCache();
        }
        ApplyLibraryMutationDelta(fileCheckResult.MutationDelta);
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
            Task work()
            {
                ProcessDeferredChartInfoHydrationRequests();
                return Task.CompletedTask;
            }
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
        var result = new ChartInfoHydrationResult();
        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("chart_info_hydration start reason=" + (reason ?? "unknown"));

        Dictionary<string, LR2SongDBExtended.chart_info> chartInfoMap;
        HashSet<string> currentChartInfoSha256s;
        HashSet<string> currentParseFailureMd5s;
        var loadStopwatch = Stopwatch.StartNew();
        try
        {
            ChartInfoHydrationLoadResult loadResult = dbGateway.LoadChartInfoHydrationData(chartInfoBuildService.CurrentParseTimeout);
            chartInfoMap = loadResult.ChartInfoBySha256;
            currentChartInfoSha256s = loadResult.CurrentChartInfoSha256s;
            currentParseFailureMd5s = loadResult.CurrentParseFailureMd5s;
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
        var indexStopwatch = Stopwatch.StartNew();
        ChartInfoIndexUpdateResult indexUpdateResult = ReplaceChartInfoIndex(chartInfoMap.Values, hydrated: true);
        indexStopwatch.Stop();
        result.IndexBuildMs = indexStopwatch.ElapsedMilliseconds;
        LogInstallPerformance("chart_info_index_hydrated rows=" + indexUpdateResult.InputRows
            + " bySha256=" + indexUpdateResult.BySha256Count
            + " byMd5=" + indexUpdateResult.ByMd5Count
            + " version=" + indexUpdateResult.Version
            + " indexBuildMs=" + result.IndexBuildMs);

        var ownerClassifyStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (BMSFile file in BMSFiles ?? Enumerable.Empty<BMSFile>())
            {
                if (file == null)
                {
                    continue;
                }
                ClassifyChartInfoHydrationOwner(result, file.sha256, file.hash, currentChartInfoSha256s, currentParseFailureMd5s);
            }
            foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            {
                if (song == null)
                {
                    continue;
                }
                ClassifyChartInfoHydrationOwner(result, song.sha256, song.md5, currentChartInfoSha256s, currentParseFailureMd5s);
            }
        }
        ownerClassifyStopwatch.Stop();
        result.OwnerApplyMs = ownerClassifyStopwatch.ElapsedMilliseconds;
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
        ISet<string> currentChartInfoSha256s,
        ISet<string> currentParseFailureMd5s)
    {
        if (result == null)
        {
            return;
        }
        result.OwnerCount++;
        if (!string.IsNullOrWhiteSpace(sha256)
            && currentChartInfoSha256s != null
            && currentChartInfoSha256s.Contains(sha256))
        {
            result.CurrentChartInfoOwnerCount++;
            result.OwnerApplySkippedCount++;
            return;
        }
        if (!string.IsNullOrWhiteSpace(md5)
            && currentParseFailureMd5s != null
            && currentParseFailureMd5s.Contains(md5))
        {
            result.CurrentParseFailureOwnerCount++;
            return;
        }
        result.BackfillCandidateOwnerCount++;
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

    private LR2SongDBExtended.chart_info ResolveChartInfoForChart(ChartFile chart)
    {
        return chart == null ? null : ResolveChartInfo(chart.Sha256, chart.Md5);
    }

    private ChartInfoIndexUpdateResult ReplaceChartInfoIndex(IEnumerable<LR2SongDBExtended.chart_info> rows, bool hydrated)
    {
        var bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        var byMd5 = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);
        int inputRows = 0;
        foreach (LR2SongDBExtended.chart_info row in rows ?? [])
        {
            if (!TryGetChartInfoSha256(row, out string sha256))
            {
                continue;
            }
            inputRows++;
            bySha256[sha256] = row;
            AddChartInfoMd5Candidate(byMd5, row, sha256);
        }

        var result = new ChartInfoIndexUpdateResult
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
        RaisePropertyChanged(() => ChartFilesZeroNote);
        return result;
    }

    private ChartInfoIndexUpdateResult UpsertChartInfoIndexRows(IEnumerable<LR2SongDBExtended.chart_info> rows, string reason)
    {
        List<LR2SongDBExtended.chart_info> rowList = [.. (rows ?? []).Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        if (rowList.Count == 0)
        {
            return new ChartInfoIndexUpdateResult();
        }

        var result = new ChartInfoIndexUpdateResult
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
        RaisePropertyChanged(() => ChartFilesZeroNote);
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
        if (hydrationResult != null && hydrationResult.Succeeded && hydrationResult.OwnerCount > 0 && hydrationResult.BackfillCandidateOwnerCount <= 0)
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
        var candidateSummaryStopwatch = Stopwatch.StartNew();
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
        ChartInfoBackfillDigestBackfilledCount = 0;
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
        ChartInfoBackfillDigestBackfilledCount = 0;
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
        IEnumerable<ChartFile> charts)
    {
        List<ChartFile> targetCharts = CreateResourceMaintenanceCharts(charts);
        ChartStorageTargetSet storageTargets = ChartStorageTargetSet.FromCharts(targetCharts);
        var result = new ChartInfoInlineBuildResult();
        if (targetCharts.Count == 0)
        {
            LogInstallPerformance("chart_info_inline_install reason=" + (reason ?? "unknown") + " target=0 success=0 currentSkipped=0 failureSkipped=0 parseFailed=0 failurePersisted=0 failureCleared=0 readFailed=0 parseMs=0");
            return result;
        }

        var inlineBuildService = new ChartInfoInlineBuildService(
            chartInfoBuildService,
            BmsLibraryInitializationService.ResolveDefaultFileDiffParserDegree());
        result = inlineBuildService.BuildForExistingCharts(
            dbGateway,
            targetCharts,
            LogInstallPerformance,
            LogInstallPerformanceWarn);
        if (storageTargets.BmsFiles.Count > 0)
        {
            dbGateway.UpsertSongs(storageTargets.BmsFiles);
        }
        if (storageTargets.BmsonSongs.Count > 0)
        {
            dbGateway.UpsertBmsonSongs(storageTargets.BmsonSongs);
        }
        dbGateway.UpsertChartInfoBackfillChunk(
            [],
            result.ChartInfoRows,
            result.ParseFailureRows,
            result.ParseFailureDeleteMd5s);
        if (result.AppliedRows.Count > 0)
        {
            UpsertChartInfoIndexRows(result.AppliedRows, reason ?? "install_package_inline");
            RaisePropertyChanged(() => ChartFilesZeroNote);
        }
        if (result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0)
        {
            RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
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
                requests = [.. chartInfoBackfillRequests];
                chartInfoBackfillRequests.Clear();
            }
            List<ChartFile> chartSnapshot;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                chartSnapshot = CreateInstalledChartSnapshot(BMSFiles, BmsonSongs);
            }
            int snapshotCount = chartSnapshot.Count;
            bool completedLatestRequest = false;
            Dictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot = null;
            ChartInfoBackfillResult result = null;
            try
            {
                ChartInfoBackfillRunning = true;
                ChartInfoBackfillTotalCount = 0;
                ChartInfoBackfillProcessedCount = 0;
                ChartInfoBackfillCurrentPath = string.Empty;
                void reportProgress(int total, int processed, string currentPath)
                {
                    ChartInfoBackfillTotalCount = total;
                    ChartInfoBackfillProcessedCount = processed;
                    ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
                }
                existingRowsSnapshot = CreateHydratedChartInfoIndexSha256Snapshot();
                result = chartInfoBuildService.BackfillChartInfos(
                    dbGateway,
                    chartSnapshot,
reportProgress,
                    LogInstallPerformance,
                    LogInstallPerformanceWarn,
                    rows => UpsertChartInfoIndexRows(rows, "backfill"),
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
                ChartInfoBackfillDigestBackfilledCount = result?.DigestBackfilledCount ?? 0;
                ChartInfoBackfillCompletedVersion = requestVersion;
                RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
                lock (lockChartInfoBackfill)
                {
                    chartInfoBackfillCompletedVersion = requestVersion;
                    if (requestVersion == chartInfoBackfillRequestedVersion)
                    {
                        ChartInfoBackfillRunning = false;
                        completedLatestRequest = true;
                    }
                }
                chartSnapshot?.Clear();
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
            MaintenanceHydrationRequestedVersion++;
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
        Action createWorker(bool reportDirect) => delegate
        {
            ProcessDeferredMaintenanceHydrationRequests(reportDirect);
        };
        Task work()
        {
            createWorker(false)();
            return Task.CompletedTask;
        }
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
            var stopwatch = Stopwatch.StartNew();
            if (reportDirect)
            {
                ReportStartupBackgroundTask("maintenance_hydration", "start", 0L, failed: false, detail: "version=" + requestVersion);
            }
            try
            {
                var options = BmsLibraryOptionsSnapshot.CreateCurrent();
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
        var ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applyStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFiles.GetWriterGuard())
        {
            var attachStopwatch = Stopwatch.StartNew();
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
                    item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.DbHydrated);
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
                        item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.Placeholder);
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
            var cleanupStopwatch = Stopwatch.StartNew();
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
        RaisePropertyChanged(() => ChartFilesNeedResourceFix);
        RaisePropertyChanged(() => ChartFilesNeedResourceFixIgnored);
        RaisePropertyChanged(() => BMSFilesGarbled);
        RaisePropertyChanged(() => BMSFilesGarbledFixed);
        RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
    }

    private void QueueDeferredInstallableMaintenance(string reason, long criticalElapsedMs, string dependency = null)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredInstallableMaintenance)
        {
            InstallableMaintenanceDeferredRequestedVersion++;
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
        void worker()
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
                var stopwatch = Stopwatch.StartNew();
                long setModeMs = 0L;
                long setHealthMs = 0L;
                long setZeroNoteMs = 0L;
                int snapshotCount = 0;
                int setModeTargetCount = 0;
                var maintenanceResult = new MaintenanceWorkflowResult();
                List<BMSFile> filesSnapshot = null;
                List<LR2SongDBExtended.bmson_song> bmsonSnapshot = null;
                try
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        filesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
                        bmsonSnapshot = [.. (BmsonSongs ?? []).Where(song => song != null)];
                        snapshotCount = filesSnapshot.Count + bmsonSnapshot.Count;
                    }
                    LogInstallPerformance("installable_maintenance_deferred run version=" + requestVersion
                        + " snapshotCount=" + snapshotCount
                        + " criticalMs=" + requestCriticalElapsedMs);
                    var stopwatchSetMode = Stopwatch.StartNew();
                    setModeTargetCount = setModeAndCommitToDB(filesSnapshot);
                    stopwatchSetMode.Stop();
                    setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                    var stopwatchSetHealth = Stopwatch.StartNew();
                    maintenanceResult = setMaintenanceInfo(CreateResourceMaintenanceCharts(filesSnapshot, bmsonSnapshot)) ?? new MaintenanceWorkflowResult();
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
        }
        Task work()
        {
            worker();
            return Task.CompletedTask;
        }
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
            int bmsCount = (BMSFiles ?? []).Count(file => file != null);
            int bmsonCount = (BmsonSongs ?? []).Count(song => song != null);
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
            var stopwatch = Stopwatch.StartNew();
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
        var cacheStopwatch = Stopwatch.StartNew();
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
        HashSet<string> excludedRootCustomOutputDirs = BuildExcludedRootCustomOutputDirectories();
        return [.. (SearchTargets ?? Enumerable.Empty<string>())
            .Where(d => Directory.Exists(d))
            .Where(d => !excludedRootCustomOutputDirs.Contains(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))];
    }

    private HashSet<string> BuildExcludedRootCustomOutputDirectories()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Settings.Default.OperationModeLR2DB)
        {
            return excluded;
        }
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

    private void InvalidatePlaylistSummaryOwnedHashSnapshot()
    {
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

    private void RebuildInstalledChartKeyIndexUnsafe()
    {
        lock (lockInstalledChartKeyIndex)
        {
            installedChartKeyIndex.Clear();
            foreach (ChartFile installedChart in CreateInstalledChartSnapshot(BMSFiles, BmsonSongs))
            {
                string key = installedChart.PrimaryLookupHash;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    installedChartKeyIndex.Add(key);
                }
            }
            installedChartKeyIndexInitialized = true;
        }
    }

    /// <summary>
    /// playlist summary 集計用の所持譜面ハッシュ snapshot を返します。
    /// 所持譜面や digest 変更時に無効化し、次回要求時にだけ再構築します。
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
        var stopwatch = Stopwatch.StartNew();
        var md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<BMSFile> bmsFilesSnapshot;
        List<LR2SongDBExtended.bmson_song> bmsonSongsSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsFilesSnapshot = ((BMSFiles == null) ? new List<BMSFile>() : [.. BMSFiles.Where(file => file != null)]);
            bmsonSongsSnapshot = ((BmsonSongs == null) ? new List<LR2SongDBExtended.bmson_song>() : [.. BmsonSongs.Where(song => song != null)]);
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
        var rebuiltSnapshot = new PlaylistSummaryOwnedHashSnapshot
        {
            Version = Interlocked.Increment(ref playlistSummaryOwnedHashSnapshotVersion),
            BuildElapsedMs = stopwatch.ElapsedMilliseconds,
            Md5Hashes = md5Hashes,
            Sha256Hashes = sha256Hashes
        };
        lock (lockPlaylistSummaryOwnedHashSnapshot)
        {
            playlistSummaryOwnedHashSnapshot ??= rebuiltSnapshot;
            return playlistSummaryOwnedHashSnapshot;
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

    private List<ChartFile> CreateInstalledChartSnapshot(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool includeResourceReferences = true)
    {
        return OverlayInstallDestinationRuntimeStates(ChartFileProjection.FromStorageRows(
            bmsFiles,
            bmsonSongs,
            includeWarningSnapshot: false,
            includeResourceReferences: includeResourceReferences));
    }

    private List<ChartFile> OverlayInstallDestinationRuntimeStates(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Select(OverlayInstallDestinationRuntimeState).Where(chart => chart != null)];
    }

    private ChartFile OverlayInstallDestinationRuntimeState(ChartFile chart)
    {
        lock (installDestinationRuntimeStatesLock)
        {
            foreach (InstallDestinationRuntimeStateKey key in EnumerateChartRuntimeStateLookupKeys(chart))
            {
                if (installDestinationRuntimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry entry)
                    && entry.CanApplyTo(chart, key.RequireOwnerMatch))
                {
                    return ChartFileProjection.WithTransientState(chart, entry.State, includeWarningSnapshot: false);
                }
            }
        }
        return chart;
    }

    private void UpdateInstallDestinationRuntimeStates(LibraryMutationDelta delta, IEnumerable<ChartFile> appliedCharts)
    {
        lock (installDestinationRuntimeStatesLock)
        {
            foreach (LibraryChartPathChange pathChange in delta?.ChartPathChanges ?? [])
            {
                MoveInstallDestinationRuntimeState(pathChange);
            }

            foreach (ChartFile chart in appliedCharts ?? [])
            {
                ChartFileTransientState state = ChartFileTransientState.FromInstallDestinationState(
                    chart,
                    includeWarningSnapshot: true,
                    forceInstallDestinationProjection: true,
                    forceWarningProjection: true);
                foreach (string key in EnumerateInstallDestinationRuntimeStateKeys(chart))
                {
                    if (state.HasState)
                    {
                        installDestinationRuntimeStatesByKey[key] = InstallDestinationRuntimeStateEntry.FromChart(chart, state);
                    }
                    else
                    {
                        installDestinationRuntimeStatesByKey.Remove(key);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateInstallDestinationRuntimeStateKeys(ChartFile chart)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstallDestinationRuntimeStateKey key in EnumerateChartRuntimeStateLookupKeys(chart))
        {
            if (seenKeys.Add(key.Key))
            {
                yield return key.Key;
            }
        }

        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            foreach (InstallDestinationRuntimeStateKey ownerKey in EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsOwner)))
            {
                if (seenKeys.Add(ownerKey.Key))
                {
                    yield return ownerKey.Key;
                }
            }
            yield break;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            foreach (InstallDestinationRuntimeStateKey ownerKey in EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonOwner)))
            {
                if (seenKeys.Add(ownerKey.Key))
                {
                    yield return ownerKey.Key;
                }
            }
        }
    }

    private static IEnumerable<InstallDestinationRuntimeStateKey> EnumerateChartRuntimeStateLookupKeys(ChartFile chart)
    {
        string primaryKey = ChartFileRuntimeStateKey.Create(chart);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            yield return new InstallDestinationRuntimeStateKey(primaryKey, requireOwnerMatch: false);
        }

        // Maintenance can recalculate a BMS hash after a runtime state is published.
        // Keep an owner-guarded path key so the overlay survives that owner refresh
        // without leaking to a different chart later installed at the same path.
        string pathKey = ChartFileRuntimeStateKey.CreatePathKey(chart);
        if (!string.IsNullOrWhiteSpace(pathKey) && !string.Equals(pathKey, primaryKey, StringComparison.OrdinalIgnoreCase))
        {
            yield return new InstallDestinationRuntimeStateKey(pathKey, requireOwnerMatch: true);
        }
    }

    private readonly struct InstallDestinationRuntimeStateKey
    {
        internal InstallDestinationRuntimeStateKey(string key, bool requireOwnerMatch)
        {
            Key = key;
            RequireOwnerMatch = requireOwnerMatch;
        }

        internal string Key { get; }

        internal bool RequireOwnerMatch { get; }
    }

    private sealed class InstallDestinationRuntimeStateEntry
    {
        private readonly BMSFile bmsOwner;
        private readonly LR2SongDBExtended.bmson_song bmsonOwner;

        private InstallDestinationRuntimeStateEntry(
            ChartFileTransientState state,
            BMSFile bmsOwner,
            LR2SongDBExtended.bmson_song bmsonOwner)
        {
            State = state ?? ChartFileTransientState.Empty;
            this.bmsOwner = bmsOwner;
            this.bmsonOwner = bmsonOwner;
        }

        internal ChartFileTransientState State { get; }

        internal static InstallDestinationRuntimeStateEntry FromChart(ChartFile chart, ChartFileTransientState state)
        {
            return new InstallDestinationRuntimeStateEntry(
                state,
                chart?.GetBmsStorageOwner(),
                chart?.GetBmsonStorageOwner());
        }

        internal bool CanApplyTo(ChartFile chart, bool requireOwnerMatch)
        {
            if (State?.HasState != true)
            {
                return false;
            }
            if (!requireOwnerMatch)
            {
                return true;
            }

            BMSFile currentBmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null || currentBmsOwner != null)
            {
                return ReferenceEquals(bmsOwner, currentBmsOwner);
            }

            LR2SongDBExtended.bmson_song currentBmsonOwner = chart?.GetBmsonStorageOwner();
            return (bmsonOwner != null || currentBmsonOwner != null)
                && ReferenceEquals(bmsonOwner, currentBmsonOwner);
        }
    }

    private List<ChartFile> CreateInstallDestinationChangedChartSnapshots(LibraryMutationDelta delta)
    {
        var chartsByKey = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in delta?.CreateAppliedInstallDestinationChartSnapshots() ?? [])
        {
            AddInstallDestinationChangedChart(chartsByKey, chart);
        }

        foreach (ChartFile chart in CreateMovedInstallDestinationRuntimeStateSnapshots(delta))
        {
            AddInstallDestinationChangedChart(chartsByKey, chart);
        }

        return [.. chartsByKey.Values];
    }

    private IEnumerable<ChartFile> CreateMovedInstallDestinationRuntimeStateSnapshots(LibraryMutationDelta delta)
    {
        if (delta?.ChartPathChanges == null)
        {
            yield break;
        }

        foreach (LibraryChartPathChange pathChange in delta.ChartPathChanges)
        {
            ChartFile movedChart = CreateMovedInstallDestinationRuntimeStateSnapshot(pathChange);
            if (movedChart != null)
            {
                yield return movedChart;
            }
        }
    }

    private ChartFile CreateMovedInstallDestinationRuntimeStateSnapshot(LibraryChartPathChange pathChange)
    {
        if (pathChange?.Chart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
        {
            return null;
        }

        string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath)
            ? pathChange.Chart.Path
            : pathChange.OldPath;
        ChartFile oldChart = ChartFileProjection.WithPath(pathChange.Chart, oldPath);
        List<InstallDestinationRuntimeStateKey> oldKeys = [.. EnumerateChartRuntimeStateLookupKeys(oldChart)];
        if (oldKeys.Count == 0)
        {
            return null;
        }

        InstallDestinationRuntimeStateEntry entry;
        lock (installDestinationRuntimeStatesLock)
        {
            entry = oldKeys
                .Select(key => installDestinationRuntimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry value) && value.CanApplyTo(oldChart, key.RequireOwnerMatch) ? value : null)
                .FirstOrDefault(value => value?.State?.HasState == true);
        }
        return entry?.State?.HasState == true
            ? ChartFileProjection.WithTransientState(ChartFileProjection.WithPath(pathChange.Chart, pathChange.NewPath), entry.State, includeWarningSnapshot: false)
            : null;
    }

    private static void AddInstallDestinationChangedChart(Dictionary<string, ChartFile> chartsByKey, ChartFile chart)
    {
        string key = ChartFileRuntimeStateKey.Create(chart);
        if (!string.IsNullOrWhiteSpace(key))
        {
            chartsByKey[key] = chart;
        }
    }

    private void MoveInstallDestinationRuntimeState(LibraryChartPathChange pathChange)
    {
        if (pathChange?.Chart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
        {
            return;
        }

        string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath)
            ? pathChange.Chart.Path
            : pathChange.OldPath;
        ChartFile oldChart = ChartFileProjection.WithPath(pathChange.Chart, oldPath);
        ChartFile newChart = ChartFileProjection.WithPath(pathChange.Chart, pathChange.NewPath);
        List<InstallDestinationRuntimeStateKey> oldKeys = [.. EnumerateChartRuntimeStateLookupKeys(oldChart)];
        List<InstallDestinationRuntimeStateKey> newKeys = [.. EnumerateChartRuntimeStateLookupKeys(newChart)];
        InstallDestinationRuntimeStateEntry entry = oldKeys
            .Select(key => installDestinationRuntimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry value) && value.CanApplyTo(oldChart, key.RequireOwnerMatch) ? value : null)
            .FirstOrDefault(value => value?.State?.HasState == true);
        if (entry?.State?.HasState != true)
        {
            return;
        }

        InstallDestinationRuntimeStateEntry movedEntry = InstallDestinationRuntimeStateEntry.FromChart(newChart, entry.State);
        foreach (InstallDestinationRuntimeStateKey newKey in newKeys)
        {
            installDestinationRuntimeStatesByKey[newKey.Key] = movedEntry;
        }
        foreach (InstallDestinationRuntimeStateKey oldKey in oldKeys)
        {
            if (!newKeys.Any(key => string.Equals(key.Key, oldKey.Key, StringComparison.OrdinalIgnoreCase)))
            {
                installDestinationRuntimeStatesByKey.Remove(oldKey.Key);
            }
        }
    }

    private void PruneInstallDestinationRuntimeStatesToCurrentStorageRows()
    {
        lock (installDestinationRuntimeStatesLock)
        {
            // Startup assigns the full storage row sets before any runtime install
            // destination overlay exists. Avoid building 210k+ ChartFile projection
            // keys for that empty-cache case.
            if (installDestinationRuntimeStatesByKey.Count == 0)
            {
                return;
            }
        }

        var currentKeys = new HashSet<string>(
            (BMSFiles ?? [])
                .SelectMany(file => EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsStorageOwnerIdentity(file)).Select(key => key.Key))
                .Concat((BmsonSongs ?? [])
                    .SelectMany(song => EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsonStorageOwnerIdentity(song)).Select(key => key.Key)))
                .Where(key => !string.IsNullOrWhiteSpace(key)),
            StringComparer.OrdinalIgnoreCase);

        lock (installDestinationRuntimeStatesLock)
        {
            foreach (string key in installDestinationRuntimeStatesByKey.Keys.ToList())
            {
                if (!currentKeys.Contains(key))
                {
                    installDestinationRuntimeStatesByKey.Remove(key);
                }
            }
        }
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
            var stopwatch = Stopwatch.StartNew();
            List<BMSFile> bmsFilesSnapshot = [.. (BMSFiles ?? Enumerable.Empty<BMSFile>()).Where(file => file != null)];
            List<ChartFile> installedCharts = CreateInstalledChartSnapshot(bmsFilesSnapshot, BmsonSongs);
            InstalledChartDirectoryIndexSnapshot snapshot = CreateInstallEstimationService().BuildInstalledHashToDirectoryMap(installedCharts);
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

    private bool ContainsInstalledChartUnsafe(ChartFile chart)
    {
        string lookupKey = chart?.PrimaryLookupHash;
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
        var knownChartDirectories = new HashSet<string>((directoryResourceLookupCache?.Keys ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
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

    private HashSet<string> CreateInstalledChartKeySnapshotExcludingChartsUnsafe(IEnumerable<ChartFile> excluded)
    {
        var excludedKeyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (excluded != null)
        {
            foreach (ChartFile item in excluded.Where(chart => chart != null))
            {
                string key = item.PrimaryLookupHash;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    excludedKeyCount[key] = excludedKeyCount.TryGetValue(key, out int value) ? value + 1 : 1;
                }
            }
        }
        var installedKeyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile installedChart in CreateInstalledChartSnapshot(BMSFiles, BmsonSongs))
        {
            string key2 = installedChart.PrimaryLookupHash;
            if (!string.IsNullOrWhiteSpace(key2))
            {
                installedKeyCounts[key2] = installedKeyCounts.TryGetValue(key2, out int value2) ? value2 + 1 : 1;
            }
        }
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ApplyCurrentScoreSnapshotToFiles(BMSFiles);
        }
        return failed;
    }

    public IRSongInfo GetIRSongInfoCache(string md5orlr2bmsid, bool seaarchAggressively = false)
    {
        return irService.GetIRSongInfoCache(md5orlr2bmsid, seaarchAggressively, irClient, songInfoUrl);
    }

    private static List<ChartFile> CreateResourceMaintenanceCharts(IEnumerable<BMSFile> bmsFiles, IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        return ChartFileProjection.FromStorageRows(
            (bmsFiles ?? []).Where(ChartFileKindResolver.IsBmsChartFile),
            bmsonSongs,
            includeWarningSnapshot: false,
            requireBmsonPath: true);
    }

    private static List<ChartFile> CreateResourceMaintenanceCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(chart => chart != null)];
    }

    private List<ChartFile> CreateOwnedResourceMaintenanceCharts()
    {
        return CreateInstalledChartSnapshot(BMSFiles, BmsonSongs);
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
        List<ChartFile> targets = CreateOwnedResourceMaintenanceCharts();
        int version = Interlocked.Increment(ref resourceHealthIndexVersionSeed);
        var snapshot = ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version);
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

    private enum ResourceHealthIndexUpdateMode
    {
        FullOnUpdates,
        DeltaOnUpdates
    }

    private bool TryApplyResourceHealthIndexDeltaLocked(
        string reason,
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        out ResourceHealthIndexSnapshot snapshot)
    {
        snapshot = null;
        ResourceHealthIndexSnapshot currentSnapshot = Volatile.Read(ref resourceHealthIndexSnapshot);
        if (currentSnapshot == null || currentSnapshot.TargetCount <= 0)
        {
            return false;
        }
        List<ChartFile> updatedTargetList = CreateResourceMaintenanceCharts(updatedTargets);
        List<ChartFile> removedTargetList = CreateResourceMaintenanceCharts(removedTargets);
        if (updatedTargetList.Count == 0 && removedTargetList.Count == 0)
        {
            return false;
        }
        int version = Interlocked.Increment(ref resourceHealthIndexVersionSeed);
        snapshot = currentSnapshot.ApplyDelta(updatedTargetList, removedTargetList, maintenanceService, version);
        lock (resourceHealthIndexLock)
        {
            resourceHealthIndexSnapshot = snapshot;
            Volatile.Write(ref resourceHealthIndexInvalidated, false);
        }
        LogInstallPerformance("resource_health_index_delta reason=" + (reason ?? "unknown")
            + " version=" + snapshot.Version
            + " targetCount=" + snapshot.TargetCount
            + " updated=" + updatedTargetList.Count
            + " removed=" + removedTargetList.Count
            + " needFix=" + snapshot.NeedFixCount
            + " ignored=" + snapshot.IgnoredCount
            + " buildMs=" + snapshot.BuildMs);
        return true;
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFile chart)
    {
        ResourceHealthIndexSnapshot currentSnapshot = Volatile.Read(ref resourceHealthIndexSnapshot);
        return Volatile.Read(ref resourceHealthIndexInvalidated) || currentSnapshot == null
            ? ResourceHealthWarningProjection.Empty
            : currentSnapshot.GetProjection(chart);
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFileKind kind, string path, string md5)
    {
        ResourceHealthIndexSnapshot currentSnapshot = Volatile.Read(ref resourceHealthIndexSnapshot);
        return Volatile.Read(ref resourceHealthIndexInvalidated) || currentSnapshot == null
            ? ResourceHealthWarningProjection.Empty
            : currentSnapshot.GetProjection(kind, path, md5);
    }

    internal ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshotForView(string reason)
    {
        return GetResourceHealthIndexSnapshot(reason);
    }

    private MaintenanceWorkflowResult setMaintenanceInfo(
        IEnumerable<ChartFile> charts,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates)
    {
        if (charts == null)
        {
            return new MaintenanceWorkflowResult();
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<ChartFile> maintenanceTargetCharts = CreateResourceMaintenanceCharts(charts);
            return setMaintenanceInfoCoreLocked(
                maintenanceTargetCharts,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode);
        }
    }

    private MaintenanceWorkflowResult setOwnedMaintenanceInfo(
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates)
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return setMaintenanceInfoCoreLocked(
                CreateOwnedResourceMaintenanceCharts(),
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode);
        }
    }

    private MaintenanceWorkflowResult setMaintenanceInfoCoreLocked(
        List<ChartFile> maintenanceTargetCharts,
        bool forceUpdate,
        Action<MaintenanceWorkflowProgress> progressReporter,
        CancellationToken cancellationToken,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode)
    {
        if (maintenanceTargetCharts == null || maintenanceTargetCharts.Count == 0)
        {
            return new MaintenanceWorkflowResult();
        }
        int bmsTargetCount = maintenanceTargetCharts.Count(chart => ChartFileKindResolver.IsBmsChartFile(chart?.GetBmsStorageOwner()));
        int bmsonTargetCount = maintenanceTargetCharts
            .Select(chart => chart?.GetBmsonStorageOwner())
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .Select(song => song.path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        LogInstallPerformance("maintenance_update start inputCount=" + maintenanceTargetCharts.Count
            + " forceUpdate=" + forceUpdate
            + " bmsTargets=" + bmsTargetCount
            + " bmsonTargets=" + bmsonTargetCount);
        MaintenanceWorkflowResult workflowResult;
        using (rwlockSongDBMaintenance.GetWriterGuard())
        {
            var resourceLookupContext = new ResourceHealthLookupContext(directoryResourceLookupCache);
            workflowResult = maintenanceService.UpdateMaintenanceInfo(maintenanceTargetCharts, forceUpdate, dbGateway, dialogService, resourceLookupContext, LogInstallPerformance, progressReporter, cancellationToken);
        }
        bool rebuildResourceHealthIndex = workflowResult.HasUpdates || !IsResourceHealthIndexCurrent();
        bool resourceHealthDeltaApplied = false;
        if (rebuildResourceHealthIndex
            && resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeltaOnUpdates
            && TryApplyResourceHealthIndexDeltaLocked("install_package_estimated", maintenanceTargetCharts, null, out ResourceHealthIndexSnapshot resourceHealthSnapshot))
        {
            resourceHealthDeltaApplied = true;
        }
        else
        {
            resourceHealthSnapshot = rebuildResourceHealthIndex
                ? RebuildResourceHealthIndexSnapshotLocked("setMaintenanceInfo")
                : Volatile.Read(ref resourceHealthIndexSnapshot) ?? ResourceHealthIndexSnapshot.Empty;
        }
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
                + " resourceHealthIndexMode=" + (resourceHealthDeltaApplied ? "delta" : (rebuildResourceHealthIndex ? "full" : "current"))
                + " warningReapplyTargets=" + workflowResult.WarningReapplyTargets
                + " warningChanged=" + workflowResult.WarningChangedCount
                + " canceled=" + workflowResult.Canceled.ToString().ToLowerInvariant()
                + " elapsedMs=" + workflowResult.TotalMs);
        }
        Task.Run(delegate
        {
            RaisePropertyChanged(() => ChartFilesNeedResourceFix);
            RaisePropertyChanged(() => ChartFilesNeedResourceFixIgnored);
            if (workflowResult.HasUpdates)
            {
                RaisePropertyChanged(() => BMSFilesGarbled);
                RaisePropertyChanged(() => BMSFilesGarbledFixed);
            }
        }).Logging("setMaintenanceInfo");
        return workflowResult;
    }

    internal List<ChartFile> GetChartsNeedResourceFix(IEnumerable<ChartFile> charts, bool forceUpdate = false, bool isInIgnoredList = false)
    {
        bool useOwnedSnapshot = charts == null;
        _ = rwlockBMSFilesInitializedMin.IsWriteLockHeld;
        _ = rwlockBMSFiles.IsWriteLockHeld;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<ChartFile> targets = useOwnedSnapshot
                    ? CreateOwnedResourceMaintenanceCharts()
                    : CreateResourceMaintenanceCharts(charts);
                if (forceUpdate)
                {
                    RescanResourceHealthCharts(targets);
                }
                ResourceHealthIndexSnapshot snapshot = GetResourceHealthIndexSnapshot(forceUpdate ? "force_resource_health_filter" : "resource_health_filter");
                if (useOwnedSnapshot)
                {
                    return [.. (isInIgnoredList ? snapshot.IgnoredTargets : snapshot.ActiveTargets)];
                }
                if (targets.Count == 0)
                {
                    return [];
                }
                return [.. targets.Where(chart =>
                {
                    ResourceHealthWarningProjection projection = snapshot.GetProjection(chart);
                    return projection.HasIssues && projection.IsIgnored == isInIgnoredList;
                })];
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
        MaintenanceWorkflowResult result = setMaintenanceInfo(charts, forceUpdate: true, progressReporter: progressReporter, cancellationToken: cancellationToken);
        RaisePropertyChanged(() => ChartFilesNeedResourceFix);
        RaisePropertyChanged(() => ChartFilesNeedResourceFixIgnored);
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
        MaintenanceWorkflowResult result = setOwnedMaintenanceInfo(
            forceUpdate: true,
            progressReporter: progressReporter,
            cancellationToken: cancellationToken);
        RaisePropertyChanged(() => ChartFilesNeedResourceFix);
        RaisePropertyChanged(() => ChartFilesNeedResourceFixIgnored);
        RaisePropertyChanged(() => BMSFilesGarbled);
        RaisePropertyChanged(() => BMSFilesGarbledFixed);
        return result;
    }

    internal void SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset = false)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<ChartFile> targets = CreateResourceMaintenanceCharts(charts);
                if (targets.Count == 0)
                {
                    return;
                }
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    List<BMSFileMaintenanceInfo> changes = maintenanceService.SetChartResourceWarningsIgnored(targets, unset);
                    dbGateway.UpsertMaintenanceInfos(changes);
                }
                RebuildResourceHealthIndexSnapshotLocked(unset ? "resource_health_unignore" : "resource_health_ignore");
            }
        }
        RaisePropertyChanged(() => ChartFilesNeedResourceFix);
        RaisePropertyChanged(() => ChartFilesNeedResourceFixIgnored);
    }

    /// <summary>
    /// エンコーディングが Shift_JIS 以外と推定された（文字化けの可能性がある）BMS ファイル群を取得します。
    /// </summary>
    public List<BMSFile> GetBMSFilesGarbled(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false, bool isInFixedList = false)
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
                if (forceUpdate)
                {
                    setMaintenanceInfo(CreateResourceMaintenanceCharts(bmsFiles, null), forceUpdate);
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
        List<ChartFile> allCharts;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            allCharts = ChartFileProjection.FromBmsFiles(BMSFiles, includeWarningSnapshot: false);
        }
        ZeroNoteRecheckResult result = maintenanceService.RecheckZeroNoteWarnings(
            allCharts,
            (ex, message) => NLogWrapper.FileLogger?.Warn(ex, message),
            ResolveChartInfoForChart);
        if (result.ChangedCount > 0)
        {
            RaisePropertyChanged(() => ChartFilesZeroNote);
        }
        NLogWrapper.FileLogger?.Info(string.Format("zero_note_recheck total={0} mismatch={1} cleared={2} skipped={3} changed={4}", result.Total, result.MismatchCount, result.ClearedCount, result.SkippedCount, result.ChangedCount));
    }

    internal List<ChartFile> GetChartInfoParseFailedChartFiles()
    {
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures = dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout);
        if (failures.Count == 0)
        {
            return [];
        }
        List<BMSFile> bmsSnapshot;
        List<LR2SongDBExtended.bmson_song> bmsonSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            bmsSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
            bmsonSnapshot = [.. (BmsonSongs ?? []).Where(song => song != null)];
        }
        List<ChartFile> result = [];
        foreach (BMSFile file in bmsSnapshot)
        {
            if (string.IsNullOrWhiteSpace(file.hash) || !failures.TryGetValue(file.hash, out LR2SongDBExtended.chart_info_parse_failure failure))
            {
                continue;
            }
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);
            result.Add(ApplyChartInfoParseFailureWarning(chart, failure));
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSnapshot)
        {
            if (string.IsNullOrWhiteSpace(song.md5) || !failures.TryGetValue(song.md5, out LR2SongDBExtended.chart_info_parse_failure failure))
            {
                continue;
            }
            ChartFile chart = ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false);
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
        string[] normalizedMd5s = NormalizeChartInfoParseFailureMd5s(md5s);
        if (normalizedMd5s.Length == 0)
        {
            return;
        }
        dbGateway.DeleteChartInfoParseFailuresByMd5(normalizedMd5s);
        RaisePropertyChanged(() => ChartInfoParseFailedChartFiles);
    }

    internal static string[] NormalizeChartInfoParseFailureMd5s(IEnumerable<string> md5s)
    {
        return [.. (md5s ?? [])
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Select(md5 => md5.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
    /// ライブラリ内の重複 chart を検出し、Union-Find でディレクトリグループ化した結果を <see cref="DuplicateChartGroups"/> に格納します。
    /// </summary>
    public void SearchDuplicateChartGroups()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockDuplicateChartGroups.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    if (DuplicateChartGroups != null)
                    {
                        return;
                    }
                    List<BMSFile> bmsSnapshot = [.. BMSFiles.Where(f => f != null)];
                    List<LR2SongDBExtended.bmson_song> bmsonSnapshot = [.. (BmsonSongs ?? []).Where(song => song != null)];
                    List<ChartFile> installedChartSnapshot = CreateInstalledChartSnapshot(bmsSnapshot, bmsonSnapshot);
                    duplicateService.ClearDuplicateState(installedChartSnapshot);
                    List<DuplicateChartRow> snapshot = duplicateService.BuildSnapshot(installedChartSnapshot);
                    var swNew = System.Diagnostics.Stopwatch.StartNew();
                    DuplicateAnalysisResult analysis = duplicateService.Analyze(snapshot, DuplicateWarningMessage);
                    duplicateService.ApplyDuplicateWarnings(analysis.DuplicateCharts, DuplicateWarningMessage);
                    DuplicateChartGroups = analysis.DuplicateGroups;
                    swNew.Stop();
                    LogInstallPerformance($"SearchDuplicateChartGroups: NewAlgo={swNew.ElapsedMilliseconds}ms, Groups={analysis.DuplicateGroups.Count}");
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
        return (BMSFiles ?? [])
            .Where(file => file != null && IsChartPathWithinDestinationDirectory(normalizedDestinationDirectory, file.path))
            .Select(file => new InstalledChartMetadataCandidate
            {
                Title = file.Title ?? string.Empty,
                Artist = file.Artist ?? string.Empty,
                Path = file.path ?? string.Empty
            })
            .Concat((BmsonSongs ?? [])
                .Where(song => song != null && IsChartPathWithinDestinationDirectory(normalizedDestinationDirectory, song.path))
                .Select(song => new InstalledChartMetadataCandidate
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

    private InstallEstimationEvaluationData EvaluateInstallEstimation(ChartPackage package, List<PackageChartEntry> targetEntries, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, BmsLibraryOptionsSnapshot optionsSnapshot = null, bool useThreadSafeResolvers = false, bool useSharedLazyHashMetrics = false, DirectoryResourceLookupCache directoryLookupCacheSnapshot = null, PendingEstimateSourceBatchPackageState batchState = null, IReadOnlyCollection<string> candidateDirectoryOverride = null, bool markInstalledDestinationAmbiguous = false)
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
        PackageInstallEstimationSnapshot estimationSnapshot;
        if (package != null && sourceSurfaceBatchHit)
        {
            bool includeBundledResources = Directory.Exists(package.path);
            PackageInstallSurfaceSnapshot sharedInstallSurface = PackageInstallEstimationSnapshotBuilder.BuildSharedInstallSurfaceSnapshot(
                package.path,
                batchState.SourceDirectory,
                batchState.SourceSurface,
                includeBundledResources);
            estimationSnapshot = package.BuildInstallEstimationSnapshotFromEntries(
                targetEntryList,
                sharedInstallSurface,
                sourceSurfaceCacheHit: false,
                sourceSurfaceBatchHit: true);
        }
        else
        {
            estimationSnapshot = package != null
                ? package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntryList)
                : PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries(targetEntryList);
        }
        InstallEstimationResult result = candidateDirectoryOverride == null
            ? CreateInstallEstimationService(optionsSnapshot).EstimateInstallationDirectory(
                estimationSnapshot,
                effectiveDirectoryLookupCache,
                candidateEvaluationDegree,
                estimateMode,
                representativeResolver,
                metadataProfileResolver)
            : CreateInstallEstimationService(optionsSnapshot).EstimateInstallationDirectoryForCandidateDirectories(
                estimationSnapshot,
                candidateDirectoryOverride,
                effectiveDirectoryLookupCache,
                candidateEvaluationDegree,
                estimateMode,
                representativeResolver,
                metadataProfileResolver);
        if (markInstalledDestinationAmbiguous && result?.LowConfidenceKind == InstallEstimationLowConfidenceKind.AmbiguousCandidates)
        {
            result.Confidence = InstallEstimationConfidence.Low;
            result.LowConfidenceKind = InstallEstimationLowConfidenceKind.InstalledDestinationAmbiguous;
            result.ConfidenceReason = "installed_destination_multiple_viable_candidates";
            result.DestinationDirectory = null;
            result.ShouldAutoApplyDestination = false;
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
    }

    /// <summary>
    /// 指定されたパス群（ファイルまたはディレクトリ）から chart package を自動検出・インストールします。
    /// アーカイブの展開、song.db への登録、Pendingパッケージ生成を一括で行います。
    /// </summary>
    /// <param name="installPaths">インストール元のファイル/ディレクトリパスのコレクション。</param>
    /// <returns>インストール処理された chart package のリスト。</returns>
    public List<ChartPackage> InstallChartPackagesAuto(IEnumerable<string> installPaths, CancellationToken token = default, Action onEachSourceProcessed = null)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<ChartPackage> pendingPackagesToEstimate = [];
        List<ChartPackage> deferredPendingEstimatePackages = [];
        Dictionary<ChartPackage, int> deferredPendingEstimateHealthByPackage = [];
        List<ChartPackage> registeredPackages = [];
        List<string> regroupEligibleSourceDirectories = [];
        PendingEstimateSourceBatchSnapshot pendingBatchSourceSnapshot = null;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (installPaths == null || installPaths.Any(path => !Directory.Exists(path) && !File.Exists(path)))
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
                            ChartPackagesPending,
                            CreateKnownChartDirectorySnapshotUnsafe(),
                            ContainsInstalledChartUnsafe,
                            dupRateThreshInOnePkg,
                            token);
                        List<ChartPackage> discoveredPackages = [.. workflow.DiscoveredPackages];
                        LogInstallPerformance("auto_install_prepare discovered=" + discoveredPackages.Count + " autoInstall=" + workflow.AutoInstallCandidates.Count + " pendingAdd=" + workflow.PendingPackagesToAdd.Count + " pendingRemove=" + workflow.PendingPackagesToRemove.Count + " discoveryMs=" + workflow.DiscoveryMs + " installedCheckMs=" + workflow.InstalledCheckMs + " warningClassifyMs=" + workflow.WarningClassificationMs + " classificationMs=" + workflow.ClassificationMs + " totalMs=" + workflow.TotalMs);
                        if (discoveredPackages.Count == 0 || token.IsCancellationRequested)
                        {
                            return registeredPackages;
                        }
                        AutoInstallApplyResult applyResult = packageInstallService.ApplyAutoInstallWorkflow(
                            workflow,
                            options.KeepInstallablePackagesPending,
                            SearchTargets != null && SearchTargets.Count() > 0 && Directory.Exists(SearchTargets[0]),
                            (packagesToInstall) => installChartPackages(packagesToInstall),
                            token);
                        LogInstallPerformance("auto_install_apply pendingAdd=" + applyResult.PendingPackagesToAdd.Count + " pendingRemove=" + applyResult.PendingPackagesToRemove.Count + " autoInstalled=" + applyResult.AutoInstalledPackages.Count + " autoFailed=" + applyResult.AutoInstallFailures.Count + " installMs=" + applyResult.InstallMs + " applyMs=" + applyResult.ApplyMs + " totalMs=" + applyResult.TotalMs);
                        if (applyResult.PendingPackagesToRemove.Count > 0)
                        {
                            stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: applyResult.PendingPackagesToRemove));
                        }
                        if (applyResult.InstallRowsToUpsert.Count > 0)
                        {
                            dbGateway.UpsertInstallRows(applyResult.InstallRowsToUpsert);
                            ChartPackagesPending.AddRange(applyResult.PendingPackagesToAdd);
                        }
                        BackgroundPendingEstimatePreparationResult estimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(applyResult.EstimateTargets, PendingInstallEstimateBatchSource.AutoInstall);
                        pendingPackagesToEstimate = estimatePreparation.EstimablePackages;
                        deferredPendingEstimatePackages = estimatePreparation.DeferredPackages;
                        deferredPendingEstimateHealthByPackage = estimatePreparation.DeferredSourceHealthByPackage;
                        pendingBatchSourceSnapshot = estimatePreparation.BatchSourceSnapshot;
                        regroupEligibleSourceDirectories = [.. workflow.RegroupEligibleSourceDirectories];
                        registeredPackages = discoveredPackages;
                    }
                }
                foreach (ChartPackage deferredPackage in deferredPendingEstimatePackages)
                {
                    deferredPendingEstimateHealthByPackage.TryGetValue(deferredPackage, out int sourceHealth);
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
        foreach (string installComponentDirectory in installComponentDirectories.Where(path => Directory.Exists(path)).OrderByDescending(path => path.Length))
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

    private bool TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package, out CleanupSourceKind sourceKind)
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
    /// chart package のファイル群を指定ディレクトリに移動し、移動元の空フォルダを削除する。
    /// マージ処理（MergeChartDirectory）やインストール処理（installChartPackages）から呼ばれる共通メソッド。
    /// </summary>
    /// <param name="pkg">移動対象の譜面パッケージ</param>
    /// <param name="installationDirectory">移動先ディレクトリ（nullの場合は自動命名）</param>
    /// <param name="showMessageBoxOnInstallFail">移動失敗時にエラーダイアログを表示するか</param>
    /// <param name="deleteAllContents">移動元フォルダを再帰削除対象として扱うか（通常インストール時は安全判定を通過した場合のみ削除）</param>
    /// <param name="existingHashes">既存譜面ハッシュのスナップショット（重複スキップ用）</param>
    /// <param name="excludedComponentPaths">移動対象外のコンポーネントパス</param>
    /// <returns>移動成功時true</returns>
    private bool MoveChartPackageFiles(ChartPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false, HashSet<string> existingHashes = null, ISet<string> excludedComponentPaths = null)
    {
        return packageInstallService.MovePackageFiles(
            pkg,
            installationDirectory,
            BmsLibraryOptionsSnapshot.CreateCurrent(),
            CreateChartFolderPathFromCharts,
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

    private sealed class EstimatedInstallBatchApplyContext
    {
        public List<BMSFile> AddedBmsFiles { get; } = [];

        public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = [];

        public HashSet<string> AffectedDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void AddInstalledCharts(IEnumerable<ChartFile> addedCharts, string destinationDirectory)
        {
            ChartStorageTargetSet addedTargets = ChartStorageTargetSet.FromCharts(CreateResourceMaintenanceCharts(addedCharts));
            AddedBmsFiles.AddRange(addedTargets.BmsFiles);
            AddedBmsonSongs.AddRange(addedTargets.BmsonSongs);
            AddAffectedDirectory(destinationDirectory);
            foreach (BMSFile addedFile in addedTargets.BmsFiles)
            {
                AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedFile.path));
            }
            foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedTargets.BmsonSongs)
            {
                AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedBmsonSong.path));
            }
        }

        private void AddAffectedDirectory(string directoryPath)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                AffectedDirectories.Add(directoryPath);
            }
        }

    }

    private List<ChartPackage> installChartPackages(IEnumerable<ChartPackage> chartPackagesInstall, string installationDirectory = null, List<ChartFile> deferredMaintenanceCharts = null, List<ChartPackage> deferredInstalledPackages = null, Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null, HashSet<string> existingHashes = null, bool skipInstalledPackageWhenNoBms = false, bool deleteSourceContentsAfterSuccessfulInstall = false, EstimatedInstallBatchApplyContext estimatedInstallBatchApplyContext = null)
    {
        List<ChartPackage> installPackageList = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        List<ChartFile> addedChartsForChartInfo = [];

        void UpsertInstalledChartRows(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = ChartStorageTargetSet.FromCharts(CreateResourceMaintenanceCharts(installResult?.AddedCharts));
            if (addedTargets.BmsFiles.Count > 0)
            {
                dbGateway.UpsertSongs(addedTargets.BmsFiles);
            }
            if (addedTargets.BmsonSongs.Count > 0)
            {
                dbGateway.UpsertBmsonSongs(addedTargets.BmsonSongs);
            }
        }

        void UpdateInstalledChartMaintenance(PackageInstallExecutionResult installResult)
        {
            List<ChartFile> addedCharts = CreateResourceMaintenanceCharts(installResult?.AddedCharts);
            if (deferredMaintenanceCharts != null)
            {
                deferredMaintenanceCharts.AddRange(addedCharts);
                return;
            }
            if (addedCharts.Count > 0)
            {
                setMaintenanceInfo(addedCharts, forceUpdate: true);
            }
        }

        void ApplyInstalledChartState(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = ChartStorageTargetSet.FromCharts(CreateResourceMaintenanceCharts(installResult?.AddedCharts));
            addedChartsForChartInfo.AddRange(CreateResourceMaintenanceCharts(installResult?.AddedCharts));
            if (estimatedInstallBatchApplyContext != null)
            {
                estimatedInstallBatchApplyContext.AddInstalledCharts(installResult?.AddedCharts, installationDirectory);
                return;
            }
            if (addedTargets.BmsFiles.Count > 0)
            {
                var addedBmsPathSet = new HashSet<string>(addedTargets.BmsFiles.Select(file => file.path), StringComparer.OrdinalIgnoreCase);
                BMSFiles = [.. BMSFiles.Where(file => !addedBmsPathSet.Contains(file.path)), .. addedTargets.BmsFiles];
            }
            if (addedTargets.BmsonSongs.Count > 0)
            {
                var nextBmsonByPath = (BmsonSongs ?? [])
                    .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                    .ToDictionary(song => song.path, StringComparer.OrdinalIgnoreCase);
                foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedTargets.BmsonSongs)
                {
                    nextBmsonByPath[addedBmsonSong.path] = addedBmsonSong;
                }
                BmsonSongs = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
            }
            if (addedTargets.BmsFiles.Count > 0 || addedTargets.BmsonSongs.Count > 0)
            {
                IEnumerable<string> addedDirectories = addedTargets.BmsFiles
                    .Select(file => DirectoryExt.GetDirectoryNameSimple(file.path))
                    .Concat(addedTargets.BmsonSongs.Select(song => DirectoryExt.GetDirectoryNameSimple(song.path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                ChartScanResult addedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots(addedDirectories);
                DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                foreach (string dir in addedDirectoryScan.ChartDirectories)
                {
                    reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
                }
                LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
            }
        }

        PackageInstallExecutionResult result = packageInstallService.InstallPackages(
            installPackageList,
            installationDirectory,
            (package, destinationDirectory, deleteAllContents, hashSnapshot, excludedComponentPaths) => MoveChartPackageFiles(package, destinationDirectory, true, deleteAllContents, hashSnapshot, excludedComponentPaths),
            UpsertInstalledChartRows,
            UpdateInstalledChartMaintenance,
            result => SetBMSScore(ChartStorageTargetSet.FromCharts(result?.AddedCharts).BmsFiles),
            ApplyInstalledChartState,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
        if (estimatedInstallBatchApplyContext != null && result.FailedPackages.Count < installPackageList.Count)
        {
            estimatedInstallBatchApplyContext.AddInstalledCharts([], installationDirectory);
        }
        if (result.InstalledPackagesToRegister.Count > 0)
        {
            if (deferredInstalledPackages != null)
            {
                deferredInstalledPackages.AddRange(result.InstalledPackagesToRegister);
            }
            else
            {
                ChartPackagesInstalled.AddRange(result.InstalledPackagesToRegister);
            }
        }
        LogInstallPerformance("install_chart_packages dst=" + (installationDirectory ?? "(auto)") + " packages=" + installPackageList.Count + " addedFiles=" + result.AddedEntries.Count + " failedPackages=" + result.FailedPackages.Count + " deleteSourceContents=" + deleteSourceContentsAfterSuccessfulInstall + " moveMs=" + result.MoveMs + " songDbMs=" + result.SongDbMs + " maintenanceMs=" + result.MaintenanceMs + " scoreMs=" + result.ScoreMs + " applyMs=" + result.ApplyMs + " totalMs=" + result.TotalMs);
        if (deferredMaintenanceCharts == null)
        {
            BuildAndPersistInlineChartInfoForInstalledCharts("install_package_inline", addedChartsForChartInfo);
        }
        return result.FailedPackages;
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context)
    {
        if (context == null)
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        if (context.AddedBmsFiles.Count > 0)
        {
            var addedBmsPathSet = new HashSet<string>(
                context.AddedBmsFiles
                    .Select(file => file.path)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            BMSFiles = [.. BMSFiles.Where(file => file != null && !addedBmsPathSet.Contains(file.path)), .. context.AddedBmsFiles];
        }
        if (context.AddedBmsonSongs.Count > 0)
        {
            var nextBmsonByPath = (BmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .ToDictionary(song => song.path, StringComparer.OrdinalIgnoreCase);
            foreach (LR2SongDBExtended.bmson_song addedBmsonSong in context.AddedBmsonSongs)
            {
                nextBmsonByPath[addedBmsonSong.path] = addedBmsonSong;
            }
            BmsonSongs = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
        }
        List<string> affectedDirectories = [.. context.AffectedDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (affectedDirectories.Count == 0)
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        ChartScanResult addedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots(affectedDirectories);
        DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string dir in affectedDirectories)
        {
            reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
        }
        LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
        return reverseLookupMutation;
    }

    private static List<ChartFile> BuildEstimatedInstallMaintenanceTargets(IEnumerable<ChartFile> charts)
    {
        var targetsByKey = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile target in CreateResourceMaintenanceCharts(charts))
        {
            string key = target.Kind + "|" + target.Path;
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                targetsByKey[key] = target;
            }
        }
        return [.. targetsByKey.Values];
    }

    private List<LR2SongDBExtended.bmson_song> ResolveAddedBmsonSongsFromInstalledPackages(IEnumerable<ChartPackage> installedPackages)
    {
        List<string> addedBmsonPaths = [.. (installedPackages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Select(entry => entry?.Chart)
            .Where(chart => chart?.Kind == ChartFileKind.Bmson)
            .Select(chart => chart.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (addedBmsonPaths.Count == 0)
        {
            return [];
        }
        var bmsonByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song song in BmsonSongs ?? [])
        {
            if (song != null && !string.IsNullOrWhiteSpace(song.path))
            {
                bmsonByPath[song.path] = song;
            }
        }
        List<LR2SongDBExtended.bmson_song> result = [];
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
    /// chart file の導入先（インストール先ディレクトリ）を推定します。
    /// </summary>
    /// <param name="package">推定対象 package。loose chart の場合は null。</param>
    /// <param name="targetEntries">インストール対象の chart entry リスト（通常は同一パッケージ内の譜面群）</param>
    /// <param name="asParallel">既存フォルダの走査（各フォルダとのマッチング評価）を並列実行するかどうか</param>
    /// <param name="estimateMode">通常推定、merge 候補探索、再インストール先修正などの推定モード</param>
    /// <remarks>
    /// 【設計意図・背景】
    /// 差分 chart package（追加の譜面データや難易度変更ファイル等）は、音源（WAV/OGGやBGA等）の実体を含まないことが多いため、
    /// そのまま独立してインストールしてもゲームプレイ時に音が鳴らないなどの不具合が生じます。
    /// ユーザーが手動で適切なベースとなる楽曲フォルダを探して統合する手間を省くべく、本ロジックでは
    /// 対象 chart file が必要とする依存ファイルのハッシュ群をキーとして、既存の全楽曲フォルダを事前フィルタリングし、
    /// 関連性が疑われるフォルダに対してのみ「仮想的に chart を配置したシミュレーション」を行います。
    /// 全てのフォルダを計算すると重すぎるため、事前のハッシュマッチで候補を絞り込むことで劇的な高速化を図りつつ、
    /// 根本的には旧来と同じく、最もファイルの依存関係が解決される（健康度/Health が高まる）フォルダを自動算出して提案します。
    /// </remarks>
    private void SearchEstimatedInstallationDirectoryForChartsCore(ChartPackage package, IEnumerable<PackageChartEntry> targetEntries, bool asParallel, ChartInstallationEstimateMode estimateMode)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<PackageChartEntry> targetEntryList = null;
                try
                {
                    targetEntryList = [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
                    if (targetEntryList.Count == 0
                        || targetEntryList.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)))
                    {
                        return;
                    }
                    foreach (PackageChartEntry entry in targetEntryList)
                    {
                        entry.SetSearchingStatus(isSearching: true);
                    }
                    InstallEstimationEvaluationData estimationData = EvaluateInstallEstimation(
                        package,
                        targetEntryList,
                        BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel),
                        estimateMode);
                    LogInstallEstimationEvaluation(estimationData);
                    ApplyInstallEstimationResultToEntries(targetEntryList, estimationData.Result);
                }
                finally
                {
                    foreach (PackageChartEntry entry in targetEntryList ?? [])
                    {
                        entry?.SetSearchingStatus(isSearching: false);
                    }
                }
            }
        }
    }

    private static void ApplyPackageMixedInstallWarningsToEntries(IEnumerable<PackageChartEntry> installedEntries)
    {
        foreach (PackageChartEntry entry in (installedEntries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
            entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
        }
    }

    private static void ApplyInstalledDestinationResolveFailedToPackageUnsafe(ChartPackage package, IEnumerable<PackageChartEntry> missingEntries)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
        }
        foreach (PackageChartEntry entry in (missingEntries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplyInstalledDestinationResolveFailed();
        }
    }

    private InstalledChartDirectoryIndexSnapshot BuildInstalledHashToDirectoryMap()
    {
        return CreateInstalledDirectoryIndexSnapshotUnsafe();
    }

    private bool TryResolveInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries, out string resolvedDir)
    {
        resolvedDir = null;
        InstalledDirectoryLookupResult resolution = ResolveInstalledDestinationFromPackage(package, missingEntries);
        LogMixedPackageResolution(resolution, package?.path, missingEntries?.Count ?? 0);
        if (!resolution.Success)
        {
            return false;
        }
        resolvedDir = resolution.InstallDirectory;
        return true;
    }

    private InstalledDirectoryLookupResult ResolveInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries)
    {
        if (package == null || missingEntries == null || missingEntries.Count == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.InvalidInput
            };
        }
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndex = BuildInstalledHashToDirectoryMap();
        if (installedDirectoryIndex.HashCount == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.InstalledIndexEmpty
            };
        }
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService();
        return installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingEntries, installedDirectoryIndex);
    }

    /// <summary>
    /// 指定されたBMSパッケージに対して、最適な導入先ディレクトリへの推論処理をキューイングします。
    /// （UIからのドラッグ＆ドロップ登録時などに呼び出されます）
    /// </summary>
    /// <param name="package">推定を行う chart package オブジェクト</param>
    private void SearchEstimatedInstallationDirectoryCore(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (!ChartPackagesPending.Contains(package))
                        {
                            return;
                        }
                        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
                        PendingPackageChartEntryPartition partition = BuildPendingPackageChartEntryPartitionUnsafe(package);
                        if (partition.PackageEntries.Count == 0)
                        {
                            return;
                        }
                        List<PackageChartEntry> alreadyInstalledEntries = partition.AlreadyInstalledEntries;
                        List<PackageChartEntry> missingEntries = partition.MissingEntries;
                        ApplyPackageMixedInstallWarningsToEntries(alreadyInstalledEntries);
                        if (missingEntries.Count == 0)
                        {
                            return;
                        }
                        // 部分既所持パッケージでは、既存譜面の実配置先を優先利用して未所持譜面の導入先を補完する。
                        if (alreadyInstalledEntries.Count > 0)
                        {
                            InstalledDirectoryLookupResult resolution = ResolveInstalledDestinationFromPackage(package, missingEntries);
                            LogMixedPackageResolution(resolution, package.path, missingEntries.Count);
                            if (resolution.Success)
                            {
                                ApplyResolvedInstallDestinationToEntries(missingEntries, resolution.InstallDirectory);
                                return;
                            }
                            if (resolution.Reason == InstalledDirectoryResolveReason.MultipleCandidateDirectories)
                            {
                                if (!HasUsableDirectoryLookupCache(directoryResourceLookupCache))
                                {
                                    LogInstallPerformance("mixed_package_resolve failed reason=resource_index_unavailable missing=" + missingEntries.Count + " candidateDirs=" + resolution.CandidateDirectoryCount);
                                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                                    return;
                                }
                                InstallEstimationEvaluationData estimationData = EvaluateInstallEstimation(
                                    package,
                                    missingEntries,
                                    BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel: true),
                                    ChartInstallationEstimateMode.Normal,
                                    candidateDirectoryOverride: resolution.CandidateDirectories,
                                    markInstalledDestinationAmbiguous: true);
                                LogInstallEstimationEvaluation(estimationData);
                                if (estimationData.Result?.HasViableDestination == true)
                                {
                                    ApplyInstallEstimationResultToEntries(missingEntries, estimationData.Result);
                                }
                                else
                                {
                                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                                }
                                return;
                            }
                            LogInstallPerformance("mixed_package_resolve failed reason=installed_destination_unresolved missing=" + missingEntries.Count);
                            ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                            return;
                        }
                        if (missingEntries.Count > 0)
                        {
                            SearchEstimatedInstallationDirectoryForChartsCore(package, missingEntries, asParallel: true, ChartInstallationEstimateMode.Normal);
                        }
                    }
                }
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        var stopwatch = Stopwatch.StartNew();
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

    public void SearchEstimatedInstallationDirectory(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        if (packageList.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=packages total=" + packageList.Count);
        try
        {
            if (packageList.Count == 1)
            {
                string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(packageList[0].path);
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
                RunPendingEstimateExclusive(delegate
                {
                    SearchEstimatedInstallationDirectoryCore(packageList[0]);
                });
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
            }
            else
            {
                ProcessManualPackageEstimateBatch(packageList);
            }
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=packages total=" + packageList.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    private void ProcessManualPackageEstimateBatch(IReadOnlyList<ChartPackage> packageList)
    {
        if (packageList == null || packageList.Count == 0)
        {
            return;
        }
        var request = new PendingInstallEstimateBatchRequest(
            PendingInstallEstimateBatchSource.ManualReestimate,
            packageList,
            PendingInstallEstimateBatchRequest.GetDisplayName(packageList.FirstOrDefault()?.path));
        string source = ToPendingEstimateBatchSourceLogValue(request.Source);
        int lowConfidenceCount = 0;
        int completed = 0;
        var executionPolicy = InstallEstimationExecutionPolicy.ForManualBatch();
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " display=" + (request.DisplayName ?? string.Empty));
        RunPendingEstimateExclusive(delegate
        {
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, request.PackageCount, 0, request.DisplayName ?? string.Empty);
            PendingInstallEstimateEvaluationContext evaluationContext = CreatePendingInstallEstimateEvaluationContext();
            List<PendingInstallEstimateEvaluationRequest> evaluationRequests = PreparePendingInstallEstimateEvaluationRequests(request);
            ProcessPendingInstallEstimateEvaluationPipeline(request, source, CancellationToken.None, evaluationContext, evaluationRequests, executionPolicy, ref completed, ref lowConfidenceCount);
        });
        stopwatch.Stop();
        LogInstallPerformance("pending_estimate_batch done source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " estimated=" + completed + " completed=" + completed + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " lowConfidence=" + lowConfidenceCount);
    }

    private void SearchEstimatedInstallationDirectoryCore(PackageChartEntry chartEntry, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntry?.Chart == null)
        {
            throw new ArgumentNullException("chartEntry");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                ClearDeferredEstimateReasonForEntriesUnsafe([chartEntry]);
            }
        }
        bool resolvedInstalledDirectory = false;
        if (!fixMode && chartEntry.Chart.Kind == ChartFileKind.Bmson)
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetReaderGuard())
                {
                    if (ContainsInstalledChartUnsafe(chartEntry.Chart))
                    {
                        List<string> installedDirectories = GetDistinctInstalledDirectoriesByHash(CreateInstalledDirectoryIndexSnapshotUnsafe(), chartEntry.Chart);
                        if (installedDirectories.Count == 1)
                        {
                            ApplyResolvedInstallDestinationToEntries([chartEntry], installedDirectories[0]);
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
        SearchEstimatedInstallationDirectoryForChartsCore(null, [chartEntry], asParallel, fixMode ? ChartInstallationEstimateMode.ReinstallCorrection : ChartInstallationEstimateMode.Normal);
    }

    internal void SearchEstimatedInstallationDirectory(PackageChartEntry chartEntry, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntry?.Chart == null)
        {
            throw new ArgumentNullException(nameof(chartEntry));
        }
        var stopwatch = Stopwatch.StartNew();
        string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(chartEntry.Chart.Path);
        LogInstallPerformance("manual_estimate_progress start kind=chart total=1 current=" + displayName);
        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SearchEstimatedInstallationDirectoryCore(chartEntry, asParallel, fixMode);
            });
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=chart total=1 elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    internal void SearchEstimatedInstallationDirectory(IEnumerable<PackageChartEntry> chartEntries, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> targetEntries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=charts total=" + targetEntries.Count);
        try
        {
            List<ChartPackage> packageTargets;
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                packageTargets = [.. ChartPackagesPending.Where(package => package != null && PackageContainsAnyChartTarget(package, targetEntries))];
            }
            List<PackageChartEntry> looseEntries = [.. targetEntries
                .Where(entry => !PackageTargetsContainChartEntry(packageTargets, entry))];
            if (!fixMode && looseEntries.Count == 0 && packageTargets.Count > 1)
            {
                ProcessManualPackageEstimateBatch(packageTargets);
            }
            else
            {
                List<object> workItems = [.. packageTargets.Cast<object>()
, .. looseEntries.Cast<object>()];
                int totalWorkCount = workItems.Count;
                RunPendingEstimateExclusive(delegate
                {
                    for (int i = 0; i < totalWorkCount; i++)
                    {
                        object workItem = workItems[i];
                        string displayName = workItem is ChartPackage package
                            ? PendingInstallEstimateBatchRequest.GetDisplayName(package.path)
                            : PendingInstallEstimateBatchRequest.GetDisplayName(((PackageChartEntry)workItem).Chart.Path);
                        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i, displayName);
                        if (workItem is ChartPackage targetPackage)
                        {
                            SearchEstimatedInstallationDirectoryCore(targetPackage);
                        }
                        else
                        {
                            SearchEstimatedInstallationDirectoryCore((PackageChartEntry)workItem, asParallel, fixMode);
                        }
                        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i + 1, displayName);
                    }
                });
            }
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=charts total=" + targetEntries.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    internal void SearchEstimatedInstallationDirectoryForLooseCharts(IEnumerable<PackageChartEntry> chartEntries, bool asParallel = true)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException("chartEntries");
        }
        List<PackageChartEntry> targetEntries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=loose_files total=" + targetEntries.Count);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                int totalWorkCount = targetEntries.Count;
                for (int i = 0; i < totalWorkCount; i++)
                {
                    PackageChartEntry targetEntry = targetEntries[i];
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(targetEntry.Chart.Path);
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i, displayName);
                    SearchEstimatedInstallationDirectoryCore(targetEntry, asParallel);
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i + 1, displayName);
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=loose_files total=" + targetEntries.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchMergeDestinationForPendingPackage(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        if (!ChartPackagesPending.Contains(package))
        {
            return;
        }
        List<PackageChartEntry> entries = [.. package.ChartEntries.Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target package=" + package.path);
            return;
        }
        foreach (PackageChartEntry entry in entries)
        {
            entry.ClearInstallDestination();
        }
        LogInstallPerformance("estimated_merge_start package=" + package.path + " targets=" + entries.Count);
        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
        if (!TryResolveInstalledDestinationFromPackage(package, entries, out string resolvedDir))
        {
            SearchEstimatedInstallationDirectoryForChartsCore(package, entries, asParallel: true, ChartInstallationEstimateMode.MergeCandidateOnly);
            LogMergeDestinationResult(package, entries);
            return;
        }
        ApplyResolvedInstallDestinationToEntries(entries, resolvedDir);
        LogMergeDestinationResult(package, entries);
    }

    private void LogMergeDestinationResult(ChartPackage package, IReadOnlyCollection<PackageChartEntry> entries)
    {
        List<PackageChartEntry> targetEntries = [.. (entries ?? []).Where(entry => entry?.Chart != null)];
        int targetCount = targetEntries.Count;
        int num = targetEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved package=" + package.path + " targets=" + targetCount);
            return;
        }
        string resolvedDestination = targetEntries.Select(entry => entry.Chart?.InstallDestination).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        bool metadataResolved = targetEntries.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestinationArtist));
        LogInstallPerformance("estimated_merge_done package=" + package.path + " resolved=" + num + " targets=" + targetCount + " dst=" + resolvedDestination + " metadataResolved=" + metadataResolved);
    }

    internal void SearchMergeDestinationForPendingCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target");
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    ClearDeferredEstimateReasonForEntriesUnsafe(entries);
                }
            }
        }
        foreach (PackageChartEntry entry in entries)
        {
            entry.ClearInstallDestination();
            SearchEstimatedInstallationDirectoryForChartsCore(null, [entry], asParallel: true, ChartInstallationEstimateMode.MergeCandidateOnly);
        }
        int num = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved targets=" + entries.Count);
            return;
        }
        LogInstallPerformance("estimated_merge_done resolved=" + num + " targets=" + entries.Count);
    }

    /// <summary>
    /// 指定された pending package 群を、インストール先ディレクトリへ強制インストールします。
    /// </summary>
    public void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
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
                            ChartPackagesPending,
                            delegate (ChartPackage pendingPackage)
                            {
                                return dialogService.Show(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                            },
                            delegate (IEnumerable<ChartPackage> packagesToInstall, List<ChartPackage> deferredInstalledPackages)
                            {
                                return installChartPackages(packagesToInstall, null, null, deferredInstalledPackages);
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
                            var hashSet3 = new HashSet<ChartPackage>(ChartPackagesInstalled.Where(pkg => pkg != null));
                            List<ChartPackage> list5 = [.. ChartPackagesInstalled.Where(pkg => pkg != null)];
                            foreach (ChartPackage deferredInstalledPackage in result.DeferredInstalledPackages)
                            {
                                if (deferredInstalledPackage != null && hashSet3.Add(deferredInstalledPackage))
                                {
                                    list5.Add(deferredInstalledPackage);
                                    num5++;
                                }
                            }
                            if (num5 > 0)
                            {
                                ChartPackagesInstalled = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(list5), DispatcherHelper.UIDispatcher);
                            }
                        }
                        NLogWrapper.FileLogger?.Info("force_install_batch summary requested=" + result.Requested + " processed=" + result.Processed + " succeeded=" + result.Succeeded + " failed=" + result.Failed + " skipped=" + result.Skipped + " pendingRemoved=" + result.PendingPackagesToRemove.Count + " installedAdded=" + num5);
                    }
                }
            }
        }
    }

    private int CountComponentMoveTargetsForPackage(ChartPackage package, string destinationDirectory, ISet<string> excludedComponentPaths)
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
                list = [path];
            }
            else
            {
                if (!Directory.Exists(path))
                {
                    return 0;
                }
                list = [.. Directory.EnumerateFileSystemEntries(path)];
            }
            List<ChartFile> charts = [.. (package.ChartEntries ?? [])
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)];
            var hashSet = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            var hashSet2 = new HashSet<string>(charts.Where(chart => hashSet.Contains(chart.Path)).Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
            List<string> installComponentFiles = [.. list.Where(p => !hashSet2.Contains(p))];
            var excludedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedComponentPaths != null)
            {
                foreach (string excludedPath in excludedComponentPaths)
                {
                    if (!string.IsNullOrWhiteSpace(excludedPath))
                    {
                        excludedPathSet.Add(excludedPath);
                    }
                }
            }
            foreach (ChartFile chart in charts)
            {
                if (!string.IsNullOrWhiteSpace(chart.Path))
                {
                    excludedPathSet.Add(chart.Path);
                }
            }
            installComponentFiles = [.. installComponentFiles.Where(p => !excludedPathSet.Contains(p))];
            return BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedPathSet).PlanItems.Count;
        }
        catch
        {
            return 0;
        }
    }

    private ChartPackage CreateInstalledDisplayPackageForResourceOnlyMerge(ChartPackage originalPackage, string destinationDirectory)
    {
        if (originalPackage == null || string.IsNullOrWhiteSpace(destinationDirectory) || BMSFiles == null)
        {
            return null;
        }
        var hashSet = new HashSet<string>((originalPackage.ChartEntries ?? [])
            .Select(entry => entry?.Chart?.PrimaryLookupHash)
            .Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
        if (hashSet.Count == 0)
        {
            return null;
        }
        List<PackageChartEntry> entries = [.. CreateInstalledChartSnapshot(BMSFiles, BmsonSongs).Where(delegate (ChartFile chart)
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                return false;
            }
            string key = chart.PrimaryLookupHash;
            if (string.IsNullOrWhiteSpace(key) || !hashSet.Contains(key))
            {
                return false;
            }
            return string.Equals(DirectoryExt.GetDirectoryNameSimple(chart.Path), destinationDirectory, StringComparison.OrdinalIgnoreCase);
        }).Select(PackageChartEntry.FromChart).Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            return null;
        }
        ChartPackage displayPackage = ChartPackage.FromChartEntries(entries);
        displayPackage.path = destinationDirectory;
        displayPackage.delete_parent = false;
        return displayPackage;
    }

    /// <summary>
    /// 指定された pending package 群を推定されたインストール先ディレクトリへインストールします。
    /// SmartOverwrite ロジックによるコンポーネント移動計画を構築して実行します。
    /// </summary>
    public void InstallPendingPackagesToEstimatedDestinations(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        var totalStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (BMSFiles == null)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=BMSFiles_null totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                            packages,
                            ChartPackagesPending,
                            CreateInstalledChartSnapshot(BMSFiles, BmsonSongs),
                            deletePendingPackageSourceAfterInstall,
                            CountComponentMoveTargetsForPackage);
                        if (installPlan.SelectedPendingPackages.Count == 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        LogInstallPerformance("install_pending_packages_to_estimated_destinations start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
                        var batchApplyContext = new EstimatedInstallBatchApplyContext();
                        PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
                            installPlan,
                            deletePendingPackageSourceAfterInstall,
                            (installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => installChartPackages(installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall, batchApplyContext),
                            CreateInstalledDisplayPackageForResourceOnlyMerge,
                            (cleanupOnlyPackage) =>
                            {
                                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupOnlyPackage, out CleanupSourceKind sourceKind);
                                return (cleanupSucceeded, sourceKind);
                            },
                            LogInstallPerformance);
                        dbGateway.DeleteInstallRows(batchResult.InstallRowsToDelete);
                        bool canUseResourceHealthIndexDelta = IsResourceHealthIndexCurrent();
                        var libraryStateApplyStopwatch = Stopwatch.StartNew();
                        ApplyEstimatedInstallBatchLibraryState(batchApplyContext);
                        libraryStateApplyStopwatch.Stop();
                        var pendingApplyStopwatch = Stopwatch.StartNew();
                        int pendingCountBeforeApply = ChartPackagesPending.Count;
                        int pendingRemovedTotal = batchResult.PendingPackagesToRemove.Count;
                        if (pendingRemovedTotal > 0)
                        {
                            List<ChartPackage> remainingPending = [.. ChartPackagesPending.Where(pkg => pkg != null && !batchResult.PendingPackagesToRemove.Contains(pkg))];
                            ChartPackagesPending = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(remainingPending), DispatcherHelper.UIDispatcher);
                        }
                        int pendingCountAfterApply = ChartPackagesPending.Count;
                        pendingApplyStopwatch.Stop();
                        var installedApplyStopwatch = Stopwatch.StartNew();
                        int installedCountBeforeApply = ChartPackagesInstalled.Count;
                        int installedAddedTotal = batchResult.DeferredInstalledPackages.Count;
                        if (installedAddedTotal > 0)
                        {
                            var installedSet = new HashSet<ChartPackage>(ChartPackagesInstalled.Where(pkg => pkg != null));
                            List<ChartPackage> mergedInstalled = [.. ChartPackagesInstalled.Where(pkg => pkg != null)];
                            foreach (ChartPackage installedPackage in batchResult.DeferredInstalledPackages)
                            {
                                if (installedPackage != null && installedSet.Add(installedPackage))
                                {
                                    mergedInstalled.Add(installedPackage);
                                }
                            }
                            ChartPackagesInstalled = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(mergedInstalled), DispatcherHelper.UIDispatcher);
                        }
                        int installedCountAfterApply = ChartPackagesInstalled.Count;
                        installedApplyStopwatch.Stop();
                        var maintenanceStopwatch = Stopwatch.StartNew();
                        List<ChartFile> estimatedInstallMaintenanceTargets = BuildEstimatedInstallMaintenanceTargets(batchResult.DeferredMaintenanceCharts);
                        if (estimatedInstallMaintenanceTargets.Count > 0)
                        {
                            setMaintenanceInfo(
                                estimatedInstallMaintenanceTargets,
                                forceUpdate: true,
                                resourceHealthIndexUpdateMode: canUseResourceHealthIndexDelta ? ResourceHealthIndexUpdateMode.DeltaOnUpdates : ResourceHealthIndexUpdateMode.FullOnUpdates);
                        }
                        List<ChartFile> estimatedInstallInlineTargets = BuildEstimatedInstallMaintenanceTargets(
                            estimatedInstallMaintenanceTargets.Concat(
                                ChartFileProjection.FromBmsonSongs(ResolveAddedBmsonSongsFromInstalledPackages(batchResult.DeferredInstalledPackages), includeWarningSnapshot: false, requirePath: true)));
                        BuildAndPersistInlineChartInfoForInstalledCharts(
                            "install_package_estimated_inline",
                            estimatedInstallInlineTargets);
                        maintenanceStopwatch.Stop();
                        if (deletePendingPackageSourceAfterInstall && batchResult.CleanupOnlySucceeded > 0)
                        {
                            dialogService.Show(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, batchResult.CleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        totalStopwatch.Stop();
                        LogInstallPerformance("install_pending_packages_to_estimated_destinations end libraryStateApplyMs=" + libraryStateApplyStopwatch.ElapsedMilliseconds + " pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingCountBeforeApply + " pendingRemovedTotal=" + pendingRemovedTotal + " pendingAfterApply=" + pendingCountAfterApply + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedCountBeforeApply + " installedAddedTotal=" + installedAddedTotal + " installedAfterApply=" + installedCountAfterApply + " maintenanceTargets=" + estimatedInstallMaintenanceTargets.Count + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 指定された installed package record 群をリストから削除します。
    /// </summary>
    public void RemoveInstalledPackageRecords(IEnumerable<ChartPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    ChartPackagesInstalled.Remove(packages);
                }
            }
        }
    }

    private PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<ChartPackage> packagesToRemove = null, IEnumerable<string> chartPathsToRemove = null, bool clearAll = false)
    {
        return packageInstallService.BuildPendingPackageMutationDelta(ChartPackagesPending, packagesToRemove, chartPathsToRemove, clearAll);
    }

    private void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        stateApplier.ApplyPendingPackageMutationDelta(delta);
    }

    /// <summary>
    /// 指定された pending package 群を pending リストから削除します。
    /// </summary>
    public void RemovePendingPackages(IEnumerable<ChartPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    RemovePendingPackagesFromPendingListAndInstallRows(packages);
                }
            }
        }
    }

    /// <summary>
    /// 全ての installed package record をリストからクリアします。
    /// </summary>
    public void RemoveInstalledPackageRecordsAll()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    ChartPackagesInstalled.Clear();
                }
            }
        }
    }

    /// <summary>
    /// 全ての Pending パッケージをリストからクリアします。
    /// </summary>
    public void RemovePendingPackagesAll()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(clearAll: true));
                }
            }
        }
    }

    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                List<ChartPackage> list = packageInstallService.GetPendingPackagesContainingOnlyInstalledCharts(ChartPackagesPending, chart => ContainsInstalledChartUnsafe(chart));
                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup scan pendingTotal=" + ChartPackagesPending.Count + " eligible=" + list.Count);
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

    private static List<string> GetDistinctInstalledDirectoriesByHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, ChartFile chart)
    {
        return BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, chart);
    }

    private static ChartFile FindChartWithMissingInstalledDirectory(ChartPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(package, installedDirectoryIndexSnapshot);
    }

    private static ChartFile FindChartWithMultipleInstalledDirectories(ChartPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(package, installedDirectoryIndexSnapshot);
    }

    private static int CountDistinctInstalledDirectoriesForPackage(ChartPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(package, installedDirectoryIndexSnapshot);
    }

    private void TryRegroupPendingPackagesForSourceDirectoriesUnsafe(IEnumerable<string> sourceDirectoryPaths)
    {
        List<string> sourceDirectories = [.. (sourceDirectoryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
        List<ChartPackage> sourcePackages = [.. ChartPackagesPending.Where(pendingPackage => pendingPackage != null && string.Equals(GetPendingPackageSourceDirectoryPath(pendingPackage), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase))];
        if (sourcePackages.Count < 2)
        {
            return;
        }
        if (sourcePackages.Any(pendingPackage => string.Equals(NormalizePendingPackagePath(pendingPackage.path), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            LogInstallPerformance("pending_regroup skip reason=already_directory_package source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (sourcePackages.Any(pendingPackage => pendingPackage != null && pendingPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None))
        {
            LogInstallPerformance("pending_regroup skip reason=deferred_estimate source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (!TryBuildRegroupedPendingPackage(sourceDirectoryPath, sourcePackages, installedDirectoryIndexSnapshot, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason))
        {
            LogInstallPerformance("pending_regroup skip reason=" + skipReason + " source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        ReinitializePendingWarningsForPackageUnsafe(regroupedPackage, CreateInstalledChartKeySnapshotExcludingChartsUnsafe(null));
        ReplacePendingPackagesWithRegroupedPackageUnsafe(sourcePackages, regroupedPackage);
        dbGateway.DeleteInstallRows(sourcePackages.Select(pendingPackage => pendingPackage.path));
        dbGateway.UpsertInstallRows([regroupedPackage]);
        List<PackageChartEntry> regroupedPackageEntries = regroupedPackage.ChartEntries;
        bool metadataResolved = regroupedPackageEntries.Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationArtist));
        LogInstallPerformance("pending_regroup success source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count + " files=" + regroupedPackageEntries.Count + " dst=" + resolvedDestinationDirectory + " metadataResolved=" + metadataResolved);
    }

    private bool TryBuildRegroupedPendingPackage(string sourceDirectoryPath, List<ChartPackage> sourcePackages, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason)
    {
        regroupedPackage = null;
        resolvedDestinationDirectory = null;
        skipReason = "unknown";
        List<PackageChartEntry> regroupedEntries = [];
        foreach (ChartPackage sourcePackage in sourcePackages)
        {
            foreach (PackageChartEntry sourceEntry in sourcePackage.ChartEntries)
            {
                ChartFile sourceChart = sourceEntry?.Chart;
                if (sourceChart == null)
                {
                    continue;
                }
                if (!regroupedEntries.Any(entry => entry.IsSameChartTarget(sourceEntry)))
                {
                    regroupedEntries.Add(sourceEntry);
                }
            }
        }
        if (regroupedEntries.Count == 0)
        {
            skipReason = "no_files";
            return false;
        }
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry regroupedEntry in regroupedEntries)
        {
            if (!TryResolvePendingFileExpectedInstallDirectory(regroupedEntry.Chart, installedDirectoryIndexSnapshot, out string expectedDirectory, out string unresolvedReason))
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
        if (regroupedEntries.Count == 0)
        {
            skipReason = "no_files";
            return false;
        }
        ApplyResolvedInstallDestinationPathAndMetadataToEntries(regroupedEntries, resolvedDestinationDirectory);
        regroupedPackage = ChartPackage.FromChartEntries(regroupedEntries);
        regroupedPackage.path = sourceDirectoryPath;
        regroupedPackage.delete_parent = false;
        return true;
    }

    private bool TryResolvePendingFileExpectedInstallDirectory(ChartFile chart, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out string expectedDirectory, out string reason)
    {
        expectedDirectory = null;
        reason = "missing_expected_destination";
        if (chart == null)
        {
            reason = "null_chart";
            return false;
        }
        List<string> installedDirectories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByPrimaryHash(installedDirectoryIndexSnapshot, chart.PrimaryLookupHash);
        if (installedDirectories.Count > 1)
        {
            reason = "multiple_installed_directories";
            return false;
        }
        string installedDirectory = installedDirectories.FirstOrDefault();
        string estimatedDirectory = string.IsNullOrWhiteSpace(chart.InstallDestination) ? null : chart.InstallDestination;
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

    private void ReinitializePendingWarningsForPackageUnsafe(ChartPackage package, ISet<string> installedHashes)
    {
        if (package == null)
        {
            return;
        }
        bool isSingleFilePackage = !Directory.Exists(package.path);
        HashSet<string> installedHashSet = installedHashes as HashSet<string> ?? new HashSet<string>(installedHashes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null)
            {
                continue;
            }

            bool isBmson = chart.Kind == ChartFileKind.Bmson;
            entry.ClearStructuredWarnings();

            string key = chart.PrimaryLookupHash;
            if (!string.IsNullOrWhiteSpace(key) && installedHashSet.Contains(key))
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
            }
            else if (isSingleFilePackage)
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
            }
            else if (entry.ResourceSnapshot.TotalReferenceCount > 0)
            {
                BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);
            }
        }
        BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);
    }

    private void ReplacePendingPackagesWithRegroupedPackageUnsafe(List<ChartPackage> sourcePackages, ChartPackage regroupedPackage)
    {
        if (sourcePackages == null || sourcePackages.Count == 0 || regroupedPackage == null)
        {
            return;
        }
        List<ChartPackage> currentPendingPackages = [.. ChartPackagesPending.Where(pendingPackage => pendingPackage != null)];
        int insertIndex = currentPendingPackages.FindIndex(pendingPackage => sourcePackages.Contains(pendingPackage));
        if (insertIndex < 0)
        {
            insertIndex = currentPendingPackages.Count;
        }
        List<ChartPackage> replacedPendingPackages = [.. currentPendingPackages.Where(pendingPackage => !sourcePackages.Contains(pendingPackage))];
        replacedPendingPackages.Insert(insertIndex, regroupedPackage);
        ChartPackagesPending = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(replacedPendingPackages), DispatcherHelper.UIDispatcher);
    }

    private static string GetPendingPackageSourceDirectoryPath(ChartPackage package)
    {
        return NormalizePendingPackagePath(package?.path) switch
        {
            string normalizedPath when string.IsNullOrWhiteSpace(normalizedPath) => null,
            string normalizedPath when ChartFileKindResolver.IsSupportedChartFilePath(normalizedPath) => NormalizePendingPackagePath(Path.GetDirectoryName(normalizedPath)),
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

    private bool HasResourceOverwriteTargetsForInstalledOnlyPackage(ChartPackage package, string destinationDir)
    {
        if (package == null || string.IsNullOrWhiteSpace(destinationDir))
        {
            return false;
        }
        List<ChartFile> charts = [.. (package.ChartEntries ?? [])
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null)];
        if (charts.Count == 0)
        {
            return false;
        }
        var excludedPaths = new HashSet<string>(charts.Where(chart => !string.IsNullOrWhiteSpace(chart.Path)).Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
        return CountComponentMoveTargetsForPackage(package, destinationDir, excludedPaths) > 0;
    }

    private bool IsPackageStillPending(ChartPackage package)
    {
        if (package == null)
        {
            return false;
        }
        return ChartPackagesPending.Any(pendingPkg => pendingPkg != null && (ReferenceEquals(pendingPkg, package) || (!string.IsNullOrWhiteSpace(pendingPkg.path) && !string.IsNullOrWhiteSpace(package.path) && pendingPkg.path.Equals(package.path, StringComparison.OrdinalIgnoreCase))));
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<ChartPackage> packages, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite scan pendingTotal=" + ChartPackagesPending.Count + " eligible=" + packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count);
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.HashCount);
                        PendingResourceOverwriteExecutionResult executionResult = packageInstallService.ExecuteInstalledOnlyResourceOverwrite(
                            packages,
                            ChartPackagesPending,
                            deletePendingPackageSourceAfterInstall,
                            (pendingPackage) => CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndexSnapshot),
                            delegate (InstalledOnlyPackageResolutionResult resolution, ChartPackage pendingPackage)
                            {
                                if (pendingPackage == null)
                                {
                                    return null;
                                }
                                switch (resolution.Reason)
                                {
                                    case InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories:
                                        ChartFile multipleDirectoryChart = FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot);
                                        return "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (multipleDirectoryChart?.Path ?? "(null)") + " hash=" + (multipleDirectoryChart?.PrimaryLookupHash ?? "(null)") + " dirCount=" + ((multipleDirectoryChart == null) ? 0 : GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, multipleDirectoryChart).Count);
                                    case InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories:
                                        return "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndexSnapshot);
                                    default:
                                        ChartFile missingDirectoryChart = FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot);
                                        return "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (missingDirectoryChart?.Path ?? "(null)") + " hash=" + (missingDirectoryChart?.PrimaryLookupHash ?? "(null)");
                                }
                            },
                            (pendingPackage, destinationDir) => HasResourceOverwriteTargetsForInstalledOnlyPackage(pendingPackage, destinationDir),
                            delegate (ChartPackage pendingPackage, string destinationDir)
                            {
                                try
                                {
                                    InstallPendingPackagesToEstimatedDestinations([pendingPackage]);
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
                                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupPackage, out CleanupSourceKind sourceKind);
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

    internal List<ChartFile> GetPendingBmsFormatChartFilesSnapshot()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                return packageInstallService.GetPendingBmsFormatChartFilesSnapshot(ChartPackagesPending);
            }
        }
    }

    internal void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(IEnumerable<ChartFile> targetCharts, CancellationToken token = default, Action onEachProcessed = null)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    IEnumerable<ChartFile> enumerable = targetCharts ?? packageInstallService.GetPendingBmsFormatChartFilesSnapshot(ChartPackagesPending);
                    PendingZeroNoteRenameResult result = packageInstallService.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
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
                    RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
                    NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename summary total=" + result.Total + " processed=" + result.Processed + " zeroNote=" + result.ZeroNote + " renamed=" + result.Renamed + " duplicateDeleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " canceled=" + result.Canceled);
                }
            }
        }
    }

    /// <summary>
    /// Pending パッケージのソースファイル群を削除（またはゴミ箱へ移動）します。
    /// </summary>
    public void DeletePendingPackageSources(IEnumerable<ChartPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    List<ChartPackage> list = packageInstallService.DeduplicatePackagesByPathOrReference(packages);
                    bool flag2 = !sendToRecycleBin;
                    NLogWrapper.FileLogger?.Info("advanced_pending_cleanup start requested=" + list.Count + " permanent=" + flag2);
                    PendingPackageSourceDeletionResult result = packageInstallService.DeletePendingPackageSources(
                        list,
                        ChartPackagesPending,
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

    private void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages)
    {
        stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packages));
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
                    dialogService.Show(selection.WarningMessage, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
        if (chart.Kind == ChartFileKind.Bmson)
        {
            return BmsonSongs.Any(song => !string.IsNullOrWhiteSpace(song?.path) && IsSamePath(song.path, chart.Path));
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null && BMSFiles.Any(file => IsSameChartFile(file, bmsFile)))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(chart.Path)
            && BMSFiles.Any(file => !string.IsNullOrWhiteSpace(file?.path) && IsSamePath(file.path, chart.Path));
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
                List<string> installedDirectories = [.. BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByPrimaryHash(CreateInstalledDirectoryIndexSnapshotUnsafe(), hash)
                    .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
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
        lock (playlistReferenceIndexLock)
        {
            return (playlistReferenceIndex ?? PlaylistReferenceIndex.Empty).Find(md5, sha256);
        }
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(ChartFile chart)
    {
        lock (playlistReferenceIndexLock)
        {
            return (playlistReferenceIndex ?? PlaylistReferenceIndex.Empty).Find(chart);
        }
    }

    internal PlaylistReferenceDisplay GetPlaylistReferenceDisplay(LibraryChartRef chart)
    {
        lock (playlistReferenceIndexLock)
        {
            return (playlistReferenceIndex ?? PlaylistReferenceIndex.Empty).Find(chart);
        }
    }

    private void ReplacePlaylistReferenceIndexTable(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        List<BMSTableEntry> entrySnapshot = SnapshotPlaylistReferenceEntries(table, entries);
        lock (playlistReferenceIndexLock)
        {
            playlistReferenceIndex ??= PlaylistReferenceIndex.Empty;
            playlistReferenceIndex.ReplaceTable(table, entrySnapshot);
        }
    }

    private void RemovePlaylistReferenceIndexTable(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        lock (playlistReferenceIndexLock)
        {
            playlistReferenceIndex?.RemoveTable(table);
        }
    }

    private void RemovePlaylistReferenceIndexTables(IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return;
        }
        lock (playlistReferenceIndexLock)
        {
            foreach (BMSTable table in tables.Where(table => table != null).Distinct())
            {
                playlistReferenceIndex?.RemoveTable(table);
            }
        }
    }

    private void SynchronizePlaylistReferenceIndex(IEnumerable<BMSTable> tables)
    {
        lock (playlistReferenceIndexLock)
        {
            playlistReferenceIndex = PlaylistReferenceIndex.FromTables(tables);
        }
    }

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        var stopwatchBuildMap = Stopwatch.StartNew();
        IEnumerable<BMSTableEntry> sourceEntries = entries;
        if (sourceEntries == null)
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                sourceEntries = [.. table.entries];
            }
        }
        else
        {
            sourceEntries = [.. sourceEntries];
        }
        PlaylistReferenceMaps referenceMaps = playlistReferenceService.BuildReferenceMaps(table, sourceEntries);
        ReplacePlaylistReferenceIndexTable(table, sourceEntries);
        stopwatchBuildMap.Stop();
        int matchedLibraryCharts = 0;
        int matchedPendingFiles = 0;
        int appliedLibraryCharts = 0;
        int appliedPendingCharts = 0;
        PlaylistReferenceApplyStats songApplyStats = default;
        var stopwatchApplySong = Stopwatch.StartNew();
        List<ChartFile> libraryChartsSnapshot = null;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            libraryChartsSnapshot = SnapshotLibraryChartsForPlaylistReferenceApply();
        }
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0 && libraryChartsSnapshot != null && libraryChartsSnapshot.Count > 0)
        {
            appliedLibraryCharts = playlistReferenceService.ApplyReferenceMap(libraryChartsSnapshot, referenceMaps, out matchedLibraryCharts, out songApplyStats);
        }
        stopwatchApplySong.Stop();
        PlaylistReferenceApplyStats pendingApplyStats = default;
        var stopwatchApplyPending = Stopwatch.StartNew();
        List<PackageChartEntry> pendingEntriesSnapshot = null;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            pendingEntriesSnapshot = SnapshotPendingChartEntriesForPlaylistReferenceApply(referenceMaps);
        }
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0 && pendingEntriesSnapshot != null && pendingEntriesSnapshot.Count > 0)
        {
            appliedPendingCharts = playlistReferenceService.ApplyReferenceMap(pendingEntriesSnapshot, referenceMaps, out matchedPendingFiles, out pendingApplyStats);
        }
        stopwatchApplyPending.Stop();
        int tableCount = 1;
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + stopwatchApplySong.ElapsedMilliseconds + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + stopwatchApplyPending.ElapsedMilliseconds + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + tableCount + " matchedLibraryCharts=" + matchedLibraryCharts + " appliedLibraryCharts=" + appliedLibraryCharts + " matchedPendingFiles=" + matchedPendingFiles + " appliedPendingCharts=" + appliedPendingCharts);
    }

    public void AddReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return;
        }
        List<BMSTable> list = [.. tables.Where(t => t != null)];
        if (list.Count == 0)
        {
            return;
        }
        var stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = playlistReferenceService.BuildReferenceMaps(list);
        SynchronizePlaylistReferenceIndex(list);
        stopwatchBuildMap.Stop();
        long applySongMs = 0L;
        long applyPendingMs = 0L;
        int matchedLibraryCharts = 0;
        int matchedPendingFiles = 0;
        int appliedLibraryCharts = 0;
        int appliedPendingCharts = 0;
        PlaylistReferenceApplyStats songApplyStats = default;
        PlaylistReferenceApplyStats pendingApplyStats = default;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            var stopwatchApplySong = Stopwatch.StartNew();
            List<ChartFile> libraryChartsSnapshot = SnapshotLibraryChartsForPlaylistReferenceApply();
            if (libraryChartsSnapshot != null && libraryChartsSnapshot.Count > 0)
            {
                appliedLibraryCharts = playlistReferenceService.ApplyReferenceMap(libraryChartsSnapshot, referenceMaps, out matchedLibraryCharts, out songApplyStats);
            }
            stopwatchApplySong.Stop();
            applySongMs = stopwatchApplySong.ElapsedMilliseconds;
            var stopwatchApplyPending = Stopwatch.StartNew();
            List<PackageChartEntry> pendingEntriesSnapshot = SnapshotPendingChartEntriesForPlaylistReferenceApply(referenceMaps);
            if (pendingEntriesSnapshot != null && pendingEntriesSnapshot.Count > 0)
            {
                appliedPendingCharts = playlistReferenceService.ApplyReferenceMap(pendingEntriesSnapshot, referenceMaps, out matchedPendingFiles, out pendingApplyStats);
            }
            stopwatchApplyPending.Stop();
            applyPendingMs = stopwatchApplyPending.ElapsedMilliseconds;
        }
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + applySongMs + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + applyPendingMs + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + list.Count + " matchedLibraryCharts=" + matchedLibraryCharts + " appliedLibraryCharts=" + appliedLibraryCharts + " matchedPendingFiles=" + matchedPendingFiles + " appliedPendingCharts=" + appliedPendingCharts);
    }

    /// <summary>
    /// Applies loaded playlist references to package chart entries after package install without materializing unmatched bmson entries.
    /// </summary>
    /// <param name="tables">Loaded playlist tables whose entries should be matched by chart hash.</param>
    /// <param name="packages">Packages containing newly installed chart entries.</param>
    public void AddReferenceBMSTablesToPackageCharts(IEnumerable<BMSTable> tables, IEnumerable<ChartPackage> packages)
    {
        if (tables == null || packages == null)
        {
            return;
        }
        List<BMSTable> tableList = [.. tables.Where(table => table != null)];
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        if (tableList.Count == 0 || packageList.Count == 0)
        {
            return;
        }

        var stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = playlistReferenceService.BuildReferenceMaps(tableList);
        stopwatchBuildMap.Stop();

        int matchedPackageFiles = 0;
        int appliedPackageCharts = 0;
        PlaylistReferenceApplyStats packageApplyStats = default;
        long applyPackageMs = 0L;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            var stopwatchApplyPackage = Stopwatch.StartNew();
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    List<PackageChartEntry> packageEntriesSnapshot = FilterPackagePlaylistReferenceTargets(SnapshotPackageChartEntriesForPlaylistReferenceApply(packageList), referenceMaps);
                    if (packageEntriesSnapshot.Count > 0)
                    {
                        appliedPackageCharts = playlistReferenceService.ApplyReferenceMap(packageEntriesSnapshot, referenceMaps, out matchedPackageFiles, out packageApplyStats);
                    }
                }
            }
            stopwatchApplyPackage.Stop();
            applyPackageMs = stopwatchApplyPackage.ElapsedMilliseconds;
        }
        LogInstallPerformance("playlist_ref_package_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applyPackageMs=" + applyPackageMs + " applyPackageChunks=" + packageApplyStats.Chunks + " applyPackageChunkMaxMs=" + packageApplyStats.MaxChunkMs + " applyPackageYieldCount=" + packageApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + tableList.Count + " packageCount=" + packageList.Count + " matchedPackageFiles=" + matchedPackageFiles + " appliedPackageCharts=" + appliedPackageCharts);
    }

    private List<ChartFile> SnapshotLibraryChartsForPlaylistReferenceApply()
    {
        if ((BMSFiles == null || BMSFiles.Count == 0) && (BmsonSongs == null || BmsonSongs.Count == 0))
        {
            return null;
        }
        var charts = new List<ChartFile>();
        using (rwlockBMSFiles.GetReaderGuard())
        {
            charts.AddRange(ChartFileProjection.FromBmsFiles(BMSFiles, includeWarningSnapshot: false));
        }
        charts.AddRange(ChartFileProjection.FromBmsonSongs(BmsonSongs, includeWarningSnapshot: false));
        return charts.Count == 0 ? null : charts;
    }

    private List<PackageChartEntry> SnapshotPendingChartEntriesForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps)
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0)
        {
            return null;
        }
        if ((referenceMaps?.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps?.Sha256ToTablesMap?.Count ?? 0) == 0)
        {
            return null;
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            List<PackageChartEntry> matchedEntries = [];
            foreach (PackageChartEntry entry in ChartPackagesPending.Where(pkg => pkg != null).SelectMany(pkg => pkg.ChartEntries))
            {
                ChartFile chart = entry?.Chart;
                if (chart == null || !HasPlaylistReferenceMapMatch(chart, referenceMaps))
                {
                    continue;
                }
                matchedEntries.Add(entry);
            }
            return [.. matchedEntries.Distinct()];
        }
    }

    private static List<PackageChartEntry> SnapshotPackageChartEntriesForPlaylistReferenceApply(IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(entry => entry?.Chart != null)];
    }

    internal void ReplaceReferenceBMSTable(BMSTable oldTable, BMSTable newTable, IEnumerable<BMSTableEntry> oldEntries = null, IEnumerable<BMSTableEntry> newEntries = null)
    {
        List<BMSTableEntry> oldEntriesSnapshot = SnapshotPlaylistReferenceEntries(oldTable, oldEntries);
        List<BMSTableEntry> newEntriesSnapshot = SnapshotPlaylistReferenceEntries(newTable, newEntries);
        BuildPlaylistReferenceHashSets(oldEntriesSnapshot, out HashSet<string> oldMd5Hashes, out HashSet<string> oldSha256Hashes);
        BuildPlaylistReferenceHashSets(newEntriesSnapshot, out HashSet<string> newMd5Hashes, out HashSet<string> newSha256Hashes);
        List<ChartFile> oldLibraryCharts = FilterPlaylistReferenceTargets(SnapshotLibraryChartsForPlaylistReferenceApply(), oldMd5Hashes, oldSha256Hashes);
        List<PackageChartEntry> oldPendingEntries = FilterPendingPlaylistReferenceTargetEntries(SnapshotPendingChartEntriesForPlaylistReferenceApply(), oldMd5Hashes, oldSha256Hashes);
        List<ChartFile> newLibraryCharts = FilterPlaylistReferenceTargets(SnapshotLibraryChartsForPlaylistReferenceApply(), newMd5Hashes, newSha256Hashes);
        List<PackageChartEntry> newPendingEntries = FilterPendingPlaylistReferenceTargetEntries(SnapshotPendingChartEntriesForPlaylistReferenceApply(), newMd5Hashes, newSha256Hashes);
        LogInstallPerformance("playlist_ref_replace targetsOldLibraryCharts=" + oldLibraryCharts.Count + " targetsOldPending=" + oldPendingEntries.Count + " targetsNewLibraryCharts=" + newLibraryCharts.Count + " targetsNewPending=" + newPendingEntries.Count + " oldEntryCount=" + oldEntriesSnapshot.Count + " newEntryCount=" + newEntriesSnapshot.Count);
        if (oldTable != null)
        {
            RemovePlaylistReferenceIndexTable(oldTable);
        }
        if (newTable != null)
        {
            ReplacePlaylistReferenceIndexTable(newTable, newEntriesSnapshot);
        }
    }

    private List<BMSTableEntry> SnapshotPlaylistReferenceEntries(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (entries != null)
        {
            return [.. entries.Where(entry => entry != null && !entry.is_removed)];
        }
        if (table == null)
        {
            return [];
        }
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return [.. table.entries.Where(entry => entry != null && !entry.is_removed)];
        }
    }

    private List<PackageChartEntry> SnapshotPendingChartEntriesForPlaylistReferenceApply()
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0)
        {
            return null;
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            return [.. ChartPackagesPending.Where(pkg => pkg != null).SelectMany(pkg => pkg.ChartEntries).Where(entry => entry?.Chart != null)];
        }
    }

    private static List<ChartFile> FilterPlaylistReferenceTargets(IEnumerable<ChartFile> charts, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (charts == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        return [.. charts.Where(delegate (ChartFile chart)
        {
            if (chart == null)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(chart.Md5) && md5Hashes.Contains(chart.Md5))
            {
                return true;
            }
            return !string.IsNullOrWhiteSpace(chart.Sha256) && sha256Hashes.Contains(chart.Sha256);
        }).Distinct()];
    }

    private static List<PackageChartEntry> FilterPendingPlaylistReferenceTargetEntries(IEnumerable<PackageChartEntry> entries, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (entries == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        List<PackageChartEntry> matchedEntries = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null || !HasPlaylistReferenceHashMatch(chart, md5Hashes, sha256Hashes))
            {
                continue;
            }
            matchedEntries.Add(entry);
        }
        return [.. matchedEntries.Distinct()];
    }

    private static List<PackageChartEntry> FilterPackagePlaylistReferenceTargets(IEnumerable<PackageChartEntry> entries, PlaylistReferenceMaps referenceMaps)
    {
        if (entries == null || ((referenceMaps?.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps?.Sha256ToTablesMap?.Count ?? 0) == 0))
        {
            return [];
        }
        List<PackageChartEntry> matchedEntries = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null || !HasPlaylistReferenceMapMatch(chart, referenceMaps))
            {
                continue;
            }
            matchedEntries.Add(entry);
        }
        return [.. matchedEntries.Distinct()];
    }

    private static bool HasPlaylistReferenceMapMatch(ChartFile chart, PlaylistReferenceMaps referenceMaps)
    {
        return chart != null
            && referenceMaps != null
            && ((!string.IsNullOrWhiteSpace(chart.Md5) && referenceMaps.Md5ToTablesMap != null && referenceMaps.Md5ToTablesMap.ContainsKey(chart.Md5))
                || (!string.IsNullOrWhiteSpace(chart.Sha256) && referenceMaps.Sha256ToTablesMap != null && referenceMaps.Sha256ToTablesMap.ContainsKey(chart.Sha256)));
    }

    private static bool HasPlaylistReferenceHashMatch(ChartFile chart, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        return chart != null
            && ((!string.IsNullOrWhiteSpace(chart.Md5) && md5Hashes != null && md5Hashes.Contains(chart.Md5))
                || (!string.IsNullOrWhiteSpace(chart.Sha256) && sha256Hashes != null && sha256Hashes.Contains(chart.Sha256)));
    }

    private static void BuildPlaylistReferenceHashSets(IEnumerable<BMSTableEntry> entries, out HashSet<string> md5Hashes, out HashSet<string> sha256Hashes)
    {
        md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? [])
        {
            if (entry == null || entry.is_removed)
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
    }

    internal void AddReferenceBMSTablesToCharts(BMSTable table, IEnumerable<ChartFile> charts)
    {
        if (table == null || charts == null)
        {
            return;
        }
        ReplacePlaylistReferenceIndexTable(table);
    }

    internal void RefreshReferenceDisplayForTable(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        ReplacePlaylistReferenceIndexTable(table);
    }

    internal void SynchronizeReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> list = ((tables != null) ? [.. tables.Where(table => table != null).Distinct()] : new List<BMSTable>());
        SynchronizePlaylistReferenceIndex(list);
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
            RemovePlaylistReferenceIndexTable(table);
            return;
        }
        ReplacePlaylistReferenceIndexTable(table);
    }

    public void RemoveReferenceBMSTables(IEnumerable<BMSTable> tables)
    {
        var list = tables?.Where(table => table != null).Distinct().ToList();
        if (list == null || list.Count == 0)
        {
            return;
        }
        RemovePlaylistReferenceIndexTables(list);
    }

    internal List<string> GetPlaylistOrgMd5sForChart(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        return bmsFile == null
            ? []
            : GetMD5sOfTheSameSong(bmsFile);
    }

    internal List<string> GetPlaylistFolderOrgMd5sForCharts(IEnumerable<ChartFile> charts)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            List<string> orgMd5s = GetPlaylistOrgMd5sForChart(chart);
            if (orgMd5s?.Count > 0)
            {
                return orgMd5s;
            }
        }
        return [];
    }

    private List<string> GetMD5sOfTheSameSong(BMSFile file)
    {
        if (file == null)
        {
            throw new ArgumentNullException("file");
        }
        List<string> list = [];
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (!BMSFiles.Contains(file))
                {
                    return null;
                }
                var lookupContext = new ResourceHealthLookupContext(directoryResourceLookupCache);
                file.SetHealthStatusUsingLookupContext(lookupContext);
                if (file.maintenanceInfo.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile)
                {
                    string dirname = DirectoryExt.GetDirectoryNameSimple(file.path);
                    return [.. (from f in BMSFiles.Where(delegate (BMSFile f)
                        {
                            if (!DirectoryExt.GetDirectoryNameSimple(f.path).Equals(dirname, StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                            f.SetHealthStatusUsingLookupContext(lookupContext);
                            return f.maintenanceInfo.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile;
                        })
                            select f.hash).Distinct()];
                }
                return null;
            }
        }
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

    private string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDir, string longestFileName = "")
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        int num = 250;
        int num2 = 128;
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
    /// 譜面ファイル群を指定された別のディレクトリへマージ（統合移動）します。
    /// </summary>
    public void MergeChartDirectory(string src, string dst)
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
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LibraryMergeResult mergeResult = libraryFileOperationsService.PrepareMergeDirectory(
                        src,
                        dst,
                        CreateLibraryChartRefSnapshotUnsafe(),
                        ChartPackagesPending,
                        ChartPackagesInstalled,
                        CreateInstalledChartKeySnapshotExcludingChartsUnsafe);
                    if (!mergeResult.Success)
                    {
                        return;
                    }
                    List<BMSFile> sourceBmsFiles = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetBmsStorageOwner())
                        .Where(ChartFileKindResolver.IsBmsChartFile)];
                    List<LR2SongDBExtended.bmson_song> sourceBmsonSongs = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetBmsonStorageOwner())
                        .Where(song => song != null)
                        .Distinct()];
                    unregisterBMSFiles(sourceBmsFiles);
                    unregisterBmsonSongs(sourceBmsonSongs);
                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                    foreach (string item in (directoryResourceLookupCache?.Keys ?? []).Where(f => (f + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.RemoveDirWithResult(item));
                    }
                    if (!MoveChartPackageFiles(mergeResult.Repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true, existingHashes: mergeResult.ExistingHashes))
                    {
                        dialogService.Show(string.Format(Resources.Error_BmsFolderMergeFailed, src, dst), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    ChartScanResult mergedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots([dst]);
                    foreach (string chartDirectory in mergedDirectoryScan.ChartDirectories)
                    {
                        reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(chartDirectory, mergedDirectoryScan));
                    }
                    LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);
                    ApplyLibraryMutationDelta(mergeResult.ReferenceMutationDelta);
                    List<PackageChartEntry> movedPackageEntries = mergeResult.Repackage.ChartEntries;
                    List<BMSFile> movedBmsFiles = [.. movedPackageEntries
                        .Select(entry => entry?.Chart?.GetBmsStorageOwner())
                        .Where(ChartFileKindResolver.IsBmsChartFile)];
                    List<LR2SongDBExtended.bmson_song> movedBmsonSongs = [.. movedPackageEntries
                        .Select(entry => entry?.Chart?.GetBmsonStorageOwner())
                        .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
                    dbGateway.UpsertSongs(movedBmsFiles);
                    if (movedBmsonSongs.Count > 0)
                    {
                        dbGateway.UpsertBmsonSongs(movedBmsonSongs);
                    }
                    List<LR2SongDBExtended.bmson_song> destinationBmsonMaintenanceSongs = [.. (BmsonSongs ?? [])
                        .Where(song => song != null
                            && !string.IsNullOrWhiteSpace(song.path)
                            && song.path.StartsWith(dst + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
                    List<LR2SongDBExtended.bmson_song> maintenanceBmsonSongs = [.. movedBmsonSongs
                        .Concat(destinationBmsonMaintenanceSongs)
                        .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                        .GroupBy(song => song.path, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())];
                    List<BMSFile> maintenanceTargets =
                    [
                        .. movedBmsFiles,
                        .. BMSFiles.Where(f => f.path.StartsWith(dst + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                    ];
                    setMaintenanceInfo(CreateResourceMaintenanceCharts(maintenanceTargets, maintenanceBmsonSongs), forceUpdate: true);
                    var repackageBmsPathSet = new HashSet<string>(movedBmsFiles.Select(ff => ff.path), StringComparer.OrdinalIgnoreCase);
                    BMSFiles = [.. BMSFiles.Where(f => !repackageBmsPathSet.Contains(f.path)), .. movedBmsFiles];
                    if (movedBmsonSongs.Count > 0)
                    {
                        var nextBmsonByPath = (BmsonSongs ?? [])
                            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                            .ToDictionary(song => song.path, StringComparer.OrdinalIgnoreCase);
                        foreach (LR2SongDBExtended.bmson_song movedBmsonSong in movedBmsonSongs)
                        {
                            nextBmsonByPath[movedBmsonSong.path] = movedBmsonSong;
                        }
                        BmsonSongs = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
                    }
                }
            }
        }
    }

    private IEnumerable<string> GetDuplicateInstallRepairPaths(ChartFile chart)
    {
        string lookupHash = chart?.PrimaryLookupHash;
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        return CreateInstalledChartSnapshot(BMSFiles, BmsonSongs)
            .Where(installedChart => installedChart != null
                && !string.Equals(installedChart.Path, chart.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(installedChart.PrimaryLookupHash, lookupHash, StringComparison.OrdinalIgnoreCase))
            .Select(installedChart => installedChart.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path));
    }

    internal void FixInstallationDirectoryCharts(IEnumerable<ChartFile> charts)
    {
        if (charts == null)
        {
            throw new ArgumentNullException("charts");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
                HashSet<string> existingHashes = CreateInstalledChartKeySnapshotExcludingChartsUnsafe(chartList);
                LibraryFixInstallationResult result = libraryFileOperationsService.FixInstallationDirectory(
                    chartList,
                    existingHashes,
                    (package, destinationDirectory) => MoveChartPackageFiles(package, destinationDirectory, showMessageBoxOnInstallFail: true, deleteAllContents: false, existingHashes: existingHashes),
                    delegate (ChartFile chart)
                    {
                        return dialogService.Show(string.Format(Resources.Confirm_DuplicateReinstallSkipped, chart.Path, string.Join(Environment.NewLine, GetDuplicateInstallRepairPaths(chart))), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                    });
                ApplyLibraryMutationDelta(result.MutationDelta);
                if (result.ChartsToRemove.Count > 0)
                {
                    RemoveLibraryCharts(result.ChartsToRemove);
                }
                List<ChartFile> maintenanceTargets = CreateResourceMaintenanceCharts(result.MaintenanceCharts);
                if (maintenanceTargets.Count > 0)
                {
                    setMaintenanceInfo(maintenanceTargets, forceUpdate: true);
                }
            }
        }
    }

    /// <summary>
    /// 譜面ファイル群のフォルダ名をメタデータに基づいて自動リネームします。
    /// </summary>
    internal void AutoRenameChartFolders(IEnumerable<ChartFile> chartFiles, bool renameRootFolder = false)
    {
        if (chartFiles == null)
        {
            throw new ArgumentNullException("chartFiles");
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    List<string> rootFolders = getBMSDirectories();
                    List<ChartFile> chartRows = GetLibraryChartsForFolderOperations();
                    List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildAutoRenamePlans(chartFiles, chartRows, rootFolders, renameRootFolder, CreateChartFolderPathFromCharts);
                    if (plans.Any(plan => !string.IsNullOrWhiteSpace(plan.SourceDirectory) && Path.GetPathRoot(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase)))
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
                        RenameChartFolder(plan.SourceDirectory, Path.GetFileName(plan.DestinationDirectory), false, renameRootFolder: true);
                    }
                }
            }
        }
    }

    private List<ChartFile> GetLibraryChartsForFolderOperations()
    {
        List<ChartFile> charts = [.. (BMSFiles ?? []).Where(file => file != null).Select(file => ChartFileProjection.FromBmsFile(file))];
        charts.AddRange((BmsonSongs ?? [])
            .Select(song => ChartFileProjection.FromBmsonSong(song))
            .Where(chart => chart != null));
        return charts;
    }

    /// <summary>
    /// 譜面フォルダを新しい名前にリネームし、song.db のパス情報を更新します。
    /// </summary>
    public void RenameChartFolder(string srcDir, string newName, bool? unregister = false, bool renameRootFolder = false)
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
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
                    MoveLibraryChartFolderInternal(srcDir, dstDir, unregister, raiseLibraryChartsChanged: false);
                    if (unregister == false)
                    {
                        InvalidateDuplicateChartGroupsCache();
                    }
                }
            }
        }
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
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    if (!Directory.Exists(dstDir))
                    {
                        dialogService.Show(string.Format(Resources.Error_MoveDestRootNotFound, dstDir), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    List<LibraryChartRef> chartList = [.. (charts ?? []).Where(chart => chart != null)];
                    List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildRootFolderMovePlans(chartList, dstDir);
                    if (chartList.Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Any(f => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        dialogService.Show(Resources.Warn_DriveRootCannotChangeRoot, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    foreach (FolderAutoRenamePlan plan in plans)
                    {
                        MoveLibraryChartFolderInternal(plan.SourceDirectory, plan.DestinationDirectory, unregister, raiseLibraryChartsChanged: true);
                    }
                    if (unregister == false)
                    {
                        RaisePropertyChanged(() => BMSFiles);
                    }
                }
            }
        }
    }

    private void MoveLibraryChartFolderInternal(string srcDir, string dstDir, bool? unregister, bool raiseLibraryChartsChanged)
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
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = libraryFileOperationsService.MoveFolderAndUpdateReferences(srcDir, dstDir, directoryResourceLookupCache, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
            LogReverseLookupMutationAndQueueWarmupIfNeeded("move_folder", reverseLookupMutation);
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
        LibraryMutationDelta delta = libraryFileOperationsService.BuildFolderMoveDelta(srcDir, dstDir, CreateLibraryChartRefSnapshotUnsafe(), ChartPackagesPending, ChartPackagesInstalled, unregister == true, raiseLibraryChartsChanged: raiseLibraryChartsChanged);
        ApplyLibraryMutationDelta(delta);
    }

    private IEnumerable<LibraryChartRef> CreateLibraryChartRefSnapshotUnsafe()
    {
        return CreateInstalledChartSnapshot(BMSFiles, BmsonSongs)
            .Select(LibraryChartRef.FromChartFile)
            .Where(chart => chart != null);
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
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                LibraryMutationDelta delta = libraryFileOperationsService.RenameLibraryFileExtensions(
                    targetCharts,
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
                NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=normal total=" + targetCharts.Count + " renamed=" + delta.RenamedCount + " deleted=" + delta.DuplicateDeletedCount + " skipped=" + delta.SkippedCount);
            }
        }
    }

    internal void RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt)
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
                    PendingExtensionRenameResult result = packageInstallService.RenamePendingBmsFormatChartFileExtensions(
                        charts,
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
                    RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
                    NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=pending total=" + result.Total + " renamed=" + result.Renamed + " deleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " totalMs=" + result.TotalMs);
                }
            }
        }
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
                    CreateLibraryChartRefSnapshotUnsafe());
            }
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        HashSet<string> approvedWholeFolderDeletes = approvedWholeFolderDeletePaths == null
            ? null
            : new HashSet<string>(approvedWholeFolderDeletePaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LibraryRemovalResult result = libraryFileOperationsService.DeleteLibraryCharts(
                        charts,
                        CreateLibraryChartRefSnapshotUnsafe(),
                        ChartPackagesPending,
                        directoryResourceLookupCache,
                        sendToRecycleBin,
                        (folderPath) => approvedWholeFolderDeletes != null
                            ? approvedWholeFolderDeletes.Contains(folderPath)
                            : dialogService.Show(string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        recursiveDirectoryTreeFileMutationOptions);
                    LogInstallPerformance("delete_library_result input=" + result.InputChartCount
                        + " canonical=" + result.CanonicalChartCount
                        + " unresolved=" + result.UnresolvedChartCount
                        + " pathOnly=" + result.PathOnlyInputCount
                        + " removed=" + result.RemovedCharts.Count
                        + " failures=" + result.Failures.Count
                        + " folderDeletes=" + result.FolderDeleteCount
                        + " fileDeletes=" + result.FileDeleteCount);
                    LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", result.ResourceIndexMutation);
                    ApplyLibraryMutationDelta(result.MutationDelta);
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
                    List<BMSFile> removedBmsFiles = [.. result.RemovedCharts
                        .Select(chart => chart?.GetBmsStorageOwner())
                        .Where(file => file != null)];
                    List<LR2SongDBExtended.bmson_song> removedBmsonSongs = [.. result.RemovedCharts
                        .Select(chart => chart?.GetBmsonStorageOwner())
                        .Where(song => song != null)
                        .Distinct()];
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
                            dialogService.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            dialogService.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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

    private void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        List<ChartFile> installDestinationChangedCharts = CreateInstallDestinationChangedChartSnapshots(delta);
        PublishLatestInstallDestinationChangedCharts(installDestinationChangedCharts);
        try
        {
            stateApplier.ApplyLibraryMutationDelta(delta);
            UpdateInstallDestinationRuntimeStates(delta, installDestinationChangedCharts);
            PruneInstallDestinationRuntimeStatesToCurrentStorageRows();
        }
        catch
        {
            ClearLatestInstallDestinationChangedCharts(installDestinationChangedCharts);
            throw;
        }
    }

    private void PublishLatestInstallDestinationChangedCharts(IReadOnlyList<ChartFile> charts)
    {
        if ((charts?.Count ?? 0) == 0)
        {
            return;
        }
        lock (latestInstallDestinationChangedChartsLock)
        {
            latestInstallDestinationChangedCharts = charts;
        }
    }

    private void ClearLatestInstallDestinationChangedCharts(IReadOnlyList<ChartFile> charts)
    {
        if ((charts?.Count ?? 0) == 0)
        {
            return;
        }
        lock (latestInstallDestinationChangedChartsLock)
        {
            if (ReferenceEquals(latestInstallDestinationChangedCharts, charts))
            {
                latestInstallDestinationChangedCharts = [];
            }
        }
    }

    /// <summary>
    /// BMS ファイル群を song.db から登録解除（レコード削除）します。
    /// </summary>
    private void unregisterBMSFiles(List<BMSFile> bmsFiles)
    {
        stateApplier.UnregisterCharts(ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFiles));
    }

    private void unregisterBmsonSongs(List<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        stateApplier.UnregisterCharts(ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongs));
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
