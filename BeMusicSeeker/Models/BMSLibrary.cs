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
    }

    /// <summary>
    /// deferred maintenance table check の進行状態を表します。
    /// </summary>
    internal struct DeferredMaintenanceTableCheckState
    {
        internal bool Running;

        internal int RequestedVersion;

        internal int LastCompletedVersion;
    }

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.BMSLibrary");

    private static readonly Logger everythingVerifyLogger = LogManager.GetLogger("Verify.Everything");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly bool everythingVerifyEnabled = CommandLineSwitches.IsEverythingVerifyEnabled;

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
    /// Everything 検証ログを出力します。検証ロギングが有効な場合のみ動作します。
    /// </summary>
    private static void LogEverythingVerify(string message)
    {
        if (everythingVerifyEnabled)
        {
            everythingVerifyLogger.Info(message);
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

    private int deferredMaintenanceTableCheckRequestedVersion;

    private bool deferredMaintenanceTableCheckRunning;

    private int deferredMaintenanceTableCheckLastCompletedVersion;

    private readonly object lockDeferredMaintenanceTableCheck = new object();

    private int deferredInstallableMaintenanceRequestedVersion;

    private bool deferredInstallableMaintenanceRunning;

    private int deferredInstallableMaintenanceLastCompletedVersion;

    private long deferredInstallableMaintenanceCriticalElapsedMs;

    private readonly object lockDeferredInstallableMaintenance = new object();

    private readonly object lockChartDigestBackfill = new object();

    private readonly object lockScoreSnapshot = new object();

    private ScoreSnapshot scoreSnapshot;

    private int scoreSnapshotVersion;

    private readonly object lockPlaylistSummaryOwnedHashSnapshot = new object();

    private PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot;

    private int chartDigestBackfillRequestedVersion;

    private int chartDigestBackfillCompletedVersion;

    private readonly object lockDeferredScoreHydration = new object();

    private int deferredScoreHydrationRequestedVersion;

    private bool deferredScoreHydrationRunning;

    private int deferredScoreHydrationLastCompletedVersion;

    private readonly object lockDeferredRankingRefresh = new object();

    private int deferredRankingRefreshRequestedVersion;

    private bool deferredRankingRefreshRunning;

    private int deferredRankingRefreshLastCompletedVersion;

    private readonly object lockDeferredReverseLookupWarmup = new object();

    private int deferredReverseLookupWarmupRequestedVersion;

    private bool deferredReverseLookupWarmupRunning;

    private int deferredReverseLookupWarmupLastCompletedVersion;

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

    private List<DuplicateGroup> _BMSFilesDuplicated;

    private DispatcherCollection<BMSPackage> _BMSPackagesPending = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private DispatcherCollection<BMSPackage> _BMSPackagesInstalled = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private List<string> bmsParentFolderListCache = new List<string>();

    private List<BMSScore> _BMSScores = new List<BMSScore>();

    private int _LR2ID;

    private bool _ScoreSnapshotReady;

    private int _ScoreSnapshotVersion;

    private bool _ScoreHydrationRunning;

    private int _ScoreHydrationCompletedVersion;

    private bool _RankingRefreshRunning;

    private int _RankingRefreshCompletedVersion;

    private bool _MaintenanceDeferredRunning;

    private int _PendingEstimateQueueStatusVersion;

    private int _InstallEstimationProgressVersion;

    private int _MaintenanceDeferredRequestedVersion;

    private int _MaintenanceDeferredCompletedVersion;

    private bool _ChartDigestBackfillRunning;

    private int _ChartDigestBackfillRequestedVersion;

    private int _ChartDigestBackfillCompletedVersion;

    private int _ChartDigestBackfillTotalCount;

    private int _ChartDigestBackfillProcessedCount;

    private string _ChartDigestBackfillCurrentPath = string.Empty;

    private bool _IsWriteLockHeldInitializeBMSFilesHealthStatus = true;

    private bool _IsWriteLockHeldInitializeBMSFilesEncodingInfo = true;

    private bool _IsWriteLockHeldInitializeBMSFilesZeroNote = true;

    private static Regex lr2IRScoreRegex = new Regex("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static readonly Uri rankingInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking");

    private static readonly Uri rankingDataUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking/");

    private static readonly Uri songInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/info/");

    private const int deferredScoreHydrationChunkSize = 4096;

    private const int deferredScoreHydrationChunkSlowLogThresholdMs = 500;

    private const int reverseLookupWarmupChunkEntryCount = 1024;

    private const int reverseLookupWarmupChunkCpuBudgetMs = 250;

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
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                }).Logging("BMSFiles");
                RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
            }
        }
    }

    public List<BMSFile> BMSFilesUnregistered => BMSFiles.Where((BMSFile f) => string.IsNullOrWhiteSpace(f.parent)).ToList();

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixed => GetBMSFilesNeedToBeFixed(BMSFiles);

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
                InvalidateInstallEstimationMetadataProfileCache();
                InvalidateDuplicatedCache();
                Task.Run(delegate
                {
                    RaisePropertyChanged("BmsonSongs");
                }).Logging("BmsonSongs");
            }
        }
    }

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixedIgnored => GetBMSFilesNeedToBeFixed(BMSFiles, forceUpdate: false, isInIgnoredList: true);

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

    /// <summary>
    /// deferred maintenance table check worker が現在実行中かどうかを返します。
    /// </summary>
    public bool MaintenanceDeferredRunning
    {
        get
        {
            return _MaintenanceDeferredRunning;
        }
        private set
        {
            if (_MaintenanceDeferredRunning != value)
            {
                _MaintenanceDeferredRunning = value;
                RaisePropertyChanged(() => MaintenanceDeferredRunning);
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
    /// 最後に要求された deferred maintenance table check の版数です。
    /// </summary>
    public int MaintenanceDeferredRequestedVersion
    {
        get
        {
            return _MaintenanceDeferredRequestedVersion;
        }
        private set
        {
            if (_MaintenanceDeferredRequestedVersion != value)
            {
                _MaintenanceDeferredRequestedVersion = value;
                RaisePropertyChanged(() => MaintenanceDeferredRequestedVersion);
            }
        }
    }

    /// <summary>
    /// 最後に完了した deferred maintenance table check の版数です。
    /// </summary>
    public int MaintenanceDeferredCompletedVersion
    {
        get
        {
            return _MaintenanceDeferredCompletedVersion;
        }
        private set
        {
            if (_MaintenanceDeferredCompletedVersion != value)
            {
                _MaintenanceDeferredCompletedVersion = value;
                RaisePropertyChanged(() => MaintenanceDeferredCompletedVersion);
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

    private readonly IBmsLibraryIrClient irClient = new BmsLibraryIrClient();

    private readonly BmsLibraryMaintenanceService maintenanceService = new BmsLibraryMaintenanceService();

    private readonly BmsLibraryStateApplier stateApplier;

    private BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => BmsLibraryOptionsSnapshot.CreateCurrent();

    private BmsLibraryInstallEstimationService CreateInstallEstimationService()
    {
        return new BmsLibraryInstallEstimationService(CurrentOptionsSnapshot, innerWavHealthThreshForNormalBMSFile);
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
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " display=" + (request.DisplayName ?? string.Empty));
        try
        {
            PreparePendingInstallEstimateBatchDemandBuild(request, source, token);
            SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, 0, request.DisplayName ?? string.Empty);
            foreach (BMSPackage package in request.Packages)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                if (package == null)
                {
                    continue;
                }
                string currentDisplayName = PendingInstallEstimateBatchRequest.GetDisplayName(package.path);
                SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, completed, currentDisplayName);
                RunPendingEstimateExclusive(delegate
                {
                    SearchEstimatedInstallationDirectoryCore(package);
                });
                completed++;
                SetInstallEstimationProgress(ToInstallEstimationProgressSource(request.Source), request.PackageCount, completed, currentDisplayName);
                pendingInstallEstimateQueueProcessor.ReportActiveBatchProgress(completed);
                if ((package.BMSFiles ?? new List<BMSFile>()).Any((BMSFile file) => file != null && string.IsNullOrWhiteSpace(file.instl_dst) && !string.IsNullOrWhiteSpace(file.InstallDestinationTitle)))
                {
                    lowConfidenceCount++;
                }
                LogInstallPerformance("pending_estimate_batch progress source=" + source + " completed=" + completed + "/" + request.PackageCount + " current=" + currentDisplayName);
            }
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
            stopwatch.Stop();
            LogInstallPerformance("pending_estimate_batch done source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " estimated=" + completed + " completed=" + (completed + request.DeferredPackageCount) + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " lowConfidence=" + lowConfidenceCount);
        }
        finally
        {
            ClearInstallEstimationProgress();
        }
    }

    private void PreparePendingInstallEstimateBatchDemandBuild(PendingInstallEstimateBatchRequest request, string source, CancellationToken token)
    {
        if (request == null || request.PackageCount == 0 || token.IsCancellationRequested)
        {
            return;
        }

        RunPendingEstimateExclusive(delegate
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            HashSet<uint> targetHashes = new HashSet<uint>();
            DirectoryResourceLookupCache directoryLookupCacheSnapshot;
            long lazyHashBuildMsBefore;
            int lazyHashCacheEntriesBefore;

            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockBMSFilesPendingInstall.GetReaderGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        directoryLookupCacheSnapshot = directoryResourceLookupCache;
                        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService();

                        foreach (BMSPackage package in request.Packages.Where((BMSPackage package) => package != null))
                        {
                            List<BMSFile> packageFiles = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                            if (packageFiles.Count == 0)
                            {
                                continue;
                            }

                            List<BMSFile> missingFiles = packageFiles.Where((BMSFile file) => !ContainsInstalledChartUnsafe(file)).ToList();
                            if (missingFiles.Count == 0)
                            {
                                continue;
                            }
                            targetHashes.UnionWith(installEstimationService.CollectTargetResourceHashes(package.GetOrBuildInstallEstimationSnapshot(missingFiles)));
                        }

                        lazyHashBuildMsBefore = directoryLookupCacheSnapshot?.LazyHashBuildMs ?? 0L;
                        lazyHashCacheEntriesBefore = directoryLookupCacheSnapshot?.LazyHashCacheEntryCount ?? 0;
                    }
                }
            }

            if (directoryLookupCacheSnapshot == null || targetHashes.Count == 0)
            {
                LogInstallPerformance("pending_estimate_batch demand_build source=" + source + " packages=" + request.PackageCount + " estimablePackages=" + request.PackageCount + " deferredPackages=" + request.DeferredPackageCount + " targetHashes=" + targetHashes.Count + " builtMs=0 entriesAdded=0");
                return;
            }

            directoryLookupCacheSnapshot.EnsureDirectoriesByHashes(targetHashes);
            long lazyHashBuildMsAfter = directoryLookupCacheSnapshot.LazyHashBuildMs;
            int lazyHashCacheEntriesAfter = directoryLookupCacheSnapshot.LazyHashCacheEntryCount;
            LogInstallPerformance("pending_estimate_batch demand_build source=" + source + " packages=" + request.PackageCount + " estimablePackages=" + request.PackageCount + " deferredPackages=" + request.DeferredPackageCount + " targetHashes=" + targetHashes.Count + " builtMs=" + (lazyHashBuildMsAfter - lazyHashBuildMsBefore) + " entriesAdded=" + (lazyHashCacheEntriesAfter - lazyHashCacheEntriesBefore));
        });
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

    private BackgroundPendingEstimatePreparationResult PrepareBackgroundPendingEstimatePackagesUnsafe(IEnumerable<BMSPackage> packages)
    {
        BackgroundPendingEstimatePreparationResult result = new BackgroundPendingEstimatePreparationResult();
        List<BMSPackage> packageList = (packages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage package) => package != null).Distinct().ToList();
        if (packageList.Count == 0)
        {
            return result;
        }

        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService();
        InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();

        foreach (BMSPackage package in packageList)
        {
            if (ShouldDeferBackgroundInstallEstimateUnsafe(package, installEstimationService, installedDirectoryIndexSnapshot, out int sourcePrimaryHealth))
            {
                package.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;
                ClearInstallEstimationStateUnsafe(package.BMSFiles);
                result.DeferredPackages.Add(package);
                result.DeferredSourceHealthByPackage[package] = sourcePrimaryHealth;
            }
            else
            {
                package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
                result.EstimablePackages.Add(package);
            }
        }

        return result;
    }

    private bool ShouldDeferBackgroundInstallEstimateUnsafe(BMSPackage package, BmsLibraryInstallEstimationService installEstimationService, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, out int sourcePrimaryHealth)
    {
        sourcePrimaryHealth = 0;
        if (package == null || installEstimationService == null || !Directory.Exists(package.path))
        {
            return false;
        }

        List<BMSFile> packageFiles = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        if (packageFiles.Count == 0)
        {
            return false;
        }

        List<BMSFile> alreadyInstalledFiles = packageFiles.Where(ContainsInstalledChartUnsafe).ToList();
        List<BMSFile> missingFiles = packageFiles.Where((BMSFile file) => !ContainsInstalledChartUnsafe(file)).ToList();
        if (missingFiles.Count == 0)
        {
            return false;
        }

        if (alreadyInstalledFiles.Count > 0)
        {
            InstalledDirectoryLookupResult resolution = installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingFiles, installedDirectoryIndexSnapshot, bmsFolderAllFileList);
            if (resolution.Success)
            {
                return false;
            }
        }

        PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(missingFiles);
        BmsLibraryInstallEstimationService.SourceBaselineEvaluation baseline = installEstimationService.EvaluateSourceBaseline(snapshot);
        sourcePrimaryHealth = baseline.PrimaryHealth;
        return baseline.IsViableDestination;
    }

    private void ClearInstallEstimationStateUnsafe(IEnumerable<BMSFile> bmsFiles)
    {
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.instl_dst = null;
            bmsFile.warning = RemoveInstallEstimationWarnings(bmsFile.warning);
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            bmsFile.HasLowConfidenceInstallWarning = false;
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

    /// <summary>
    /// BMS ファイル走査のプリフェッチ結果（走査結果と所要時間）を保持するクラスです。
    /// </summary>
    private sealed class BmsScanPrefetchInfo
    {
        public BmsScanExecutionResult ScanResult { get; set; }

        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Everything ファイルスキャナーで走査を試み、失敗時にはディレクトリ形式のフォールバックスキャナーを使用します。
    /// </summary>
    private BmsScanExecutionResult ExecuteBmsScanWithFallback(List<string> bmsDirectories)
    {
        IBmsFileScanner fallbackScanner = new FastDirectoryFileScanner();
        IBmsFileScanner scanner = new EverythingFileScanner();
        BmsScanExecutionResult scanResult = scanner.Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        if (!scanResult.Success || scanResult.Result == null)
        {
            LogEverythingScan("BMS file scan fallback reason=" + (scanResult?.ErrorReason ?? "unknown"));
            scanResult = fallbackScanner.Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingScanLoggingEnabled);
        }
        if (scanResult?.Result == null)
        {
            throw new InvalidOperationException("BMS file scan failed");
        }
        if (everythingVerifyEnabled)
        {
            Stopwatch stopwatchVerify = Stopwatch.StartNew();
            BmsScanExecutionResult fastScanResult = fallbackScanner.Scan(bmsDirectories, ChartDirectoryScanBuilder.ChartExtensions, everythingVerifyEnabled);
            stopwatchVerify.Stop();
            if (fastScanResult.Success && fastScanResult.Result != null)
            {
                BmsScanDiffReport report = BmsScanResultComparer.Compare(scanResult.Result, fastScanResult.Result);
                LogEverythingVerify("everything_verify comparedMs=" + stopwatchVerify.ElapsedMilliseconds + " chartDiff=" + report.ChartPathDiffCount + " dirDiff=" + report.ChartDirectoryDiffCount + " hashDiff=" + report.CategoryHashDiffCount + " match=" + report.IsMatch.ToString().ToLowerInvariant());
                foreach (string sample in report.Samples.Take(10))
                {
                    LogEverythingVerify("everything_verify sample " + sample);
                }
            }
            else
            {
                LogEverythingVerify("everything_verify fast_scan_failed reason=" + (fastScanResult?.ErrorReason ?? "unknown"));
            }
        }
        return scanResult;
    }

    /// <summary>
    /// BMS ライブラリの初期化を行います。song.db からの譜面データ読み込み、ファイルスキャン、
    /// スコア / 保守情報の取得を統合的に実行します。
    /// </summary>
    /// <param name="tasksContinuation">初期化中に並行で実行する追加タスクのリスト。</param>
    /// <param name="semaphore">追加タスクの同期用セマフォ。</param>
    /// <param name="reloadScoresOnly">スコアのみの再読み込み指定。</param>
    public void Initialize(List<Action> tasksContinuation, SemaphoreSlim semaphore = null, bool? reloadScoresOnly = null)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool scheduleDeferredMaintenanceTableCheck = false;
        bool scheduleDeferredInstallableMaintenance = false;
        string deferredMaintenanceReason = ((reloadScoresOnly == false) ? "reload_files" : "initialize");
        bool songTblLoad = reloadScoresOnly != true;
        bool songTblFileCheck = reloadScoresOnly == false || (reloadScoresOnly != true && !options.SkipInitFileCheck);
        bool setMaintenanceInfo = reloadScoresOnly != true;
        bool flag = reloadScoresOnly != true;
        Task<BmsScanPrefetchInfo> bmsScanPrefetchTask = null;
        if (songTblFileCheck)
        {
            List<string> prefetchDirectories = getBMSDirectories();
            if (prefetchDirectories.Count > 0)
            {
                bmsScanPrefetchTask = Task.Run(delegate
                {
                    Stopwatch stopwatchPrefetch = Stopwatch.StartNew();
                    BmsScanExecutionResult scanResult = ExecuteBmsScanWithFallback(prefetchDirectories);
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
        LogInstallPerformance("init_library_enter reloadScoresOnly=" + (reloadScoresOnly?.ToString() ?? "(null)") + " songTblLoad=" + songTblLoad.ToString().ToLowerInvariant() + " songTblFileCheck=" + songTblFileCheck.ToString().ToLowerInvariant() + " setMaintenanceInfo=" + setMaintenanceInfo.ToString().ToLowerInvariant() + " installTblCheck=" + flag.ToString().ToLowerInvariant() + " rwlockInitAll currentRead=" + rwlockBMSFilesInitializedAll.CurrentReadCount + " lockingRead=" + rwlockBMSFilesInitializedAll.LockingReadCount + " lockingWrite=" + rwlockBMSFilesInitializedAll.LockingWriteCount + " waitingWrite=" + rwlockBMSFilesInitializedAll.WaitingWriteCount);
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
                        _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, maintenanceTblCheck: false);
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
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck, setMainteInfo: false, updateIrScore: true, installTblCheck: false, maintenanceTblCheck: false, bmsScanPrefetchInfo);
                },
                delegate
                {
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, flag, maintenanceTblCheck: false);
                });
            scheduleDeferredMaintenanceTableCheck = flag;
            scheduleDeferredInstallableMaintenance = setMaintenanceInfo;
            TimeSpan timeSpan = DateTime.Now - now;
            NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());
        }
        if (flag)
        {
            BackgroundPendingEstimatePreparationResult startupEstimatePreparation;
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockBMSFilesPendingInstall.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        startupEstimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(BMSPackagesPending);
                    }
                }
            }
            foreach (BMSPackage deferredPackage in startupEstimatePreparation.DeferredPackages)
            {
                int sourceHealth = 0;
                startupEstimatePreparation.DeferredSourceHealthByPackage.TryGetValue(deferredPackage, out sourceHealth);
                LogInstallPerformance("pending_estimate_batch skipped source=startup_restore package=" + (deferredPackage?.path ?? string.Empty) + " reason=healthy_source_baseline sourceHealth=" + sourceHealth);
            }
            if (startupEstimatePreparation.EstimablePackages.Count > 0)
            {
                QueuePendingInstallEstimateBatch(new PendingInstallEstimateBatchRequest(
                    PendingInstallEstimateBatchSource.StartupRestore,
                    startupEstimatePreparation.EstimablePackages,
                    Resources.Pending_estimate_queue_startup_display_name,
                    deferredPackageCount: startupEstimatePreparation.DeferredPackages.Count));
            }
        }
        DirectoryResourceLookupCache installableLookupCacheSnapshot = null;
        int pendingPackageCount = 0;
        int pendingEstimateQueueBatchCount = 0;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            installableLookupCacheSnapshot = directoryResourceLookupCache;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            pendingPackageCount = BMSPackagesPending.Count;
        }
        pendingEstimateQueueBatchCount = GetPendingEstimateQueuedBatchCount(GetPendingEstimateQueueStatusSnapshot());
        long installableElapsedMs = (long)(DateTime.Now - now).TotalMilliseconds;
        LogInstallPerformance("startup_ready_installable elapsedMs=" + installableElapsedMs
            + " pendingPackages=" + pendingPackageCount
            + " pendingEstimateQueueBatches=" + pendingEstimateQueueBatchCount
            + " lazy_hash_cache_entries=" + (installableLookupCacheSnapshot?.LazyHashCacheEntryCount ?? 0)
            + " lazy_hash_build_ms=" + (installableLookupCacheSnapshot?.LazyHashBuildMs ?? 0L)
            + " lazy_hash_lookup_count=" + (installableLookupCacheSnapshot?.LazyHashLookupCount ?? 0L));
        QueueDeferredReverseLookupWarmup(deferredMaintenanceReason);
        if (scheduleDeferredInstallableMaintenance)
        {
            QueueDeferredInstallableMaintenance(deferredMaintenanceReason, installableElapsedMs);
        }
        else
        {
            LogInstallPerformance("init_library_installable critical_ms=" + installableElapsedMs + " deferred_ms=0");
        }
        TimeSpan timeSpan2 = DateTime.Now - now;
        NLogWrapper.DebuggerLogger?.Trace(timeSpan2.ToString());
        if (scheduleDeferredMaintenanceTableCheck)
        {
            ScheduleDeferredMaintenanceTableCheck(deferredMaintenanceReason);
        }
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("owari: " + GC.GetTotalMemory(forceFullCollection: false));
        LogInstallPerformance("init_library phase1_min_load_ms=" + initializeResult.Phase1MinLoadMs + " phase2_scan_maint_ms=" + initializeResult.Phase2ScanMaintMs + " phase3_install_maintenance_ms=" + initializeResult.Phase3InstallMaintenanceMs + " wait_continuation_ms=" + initializeResult.WaitContinuationMs + " total_ms=" + initializeResult.TotalMs + " maintenance_tbl_check_deferred=" + scheduleDeferredMaintenanceTableCheck.ToString().ToLowerInvariant() + " set_maintenance_enabled=" + setMaintenanceInfo.ToString().ToLowerInvariant());
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
    private void _initialize(bool songTblLoad = true, bool scoreTblrLoad = true, bool songTblFileCheck = true, bool setMainteInfo = true, bool updateIrScore = true, bool installTblCheck = true, bool maintenanceTblCheck = true, BmsScanPrefetchInfo bmsScanPrefetchInfo = null)
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
        long maintenanceTblCheckMs = 0L;
        BmsLibraryOptionsSnapshot options = BmsLibraryOptionsSnapshot.CreateCurrent();
        List<string> bMSDirectories = getBMSDirectories();
        if (bMSDirectories.Count == 0)
        {
            songTblFileCheck = false;
        }
        if (songTblLoad)
        {
            Stopwatch stopwatchSongTblLoad = Stopwatch.StartNew();
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
                LogInstallPerformance("song_tbl_load_breakdown song_table_load_ms=" + songTableLoadResult.SongTableLoadMs + " song_normalize_loop_ms=" + songTableLoadResult.SongNormalizeLoopMs + " folder_table_load_ms=" + songTableLoadResult.FolderTableLoadMs + " folder_normalize_loop_ms=" + songTableLoadResult.FolderNormalizeLoopMs + " fix_apply_ms=" + songTableLoadResult.FixApplyMs + " maintenance_table_load_ms=" + songTableLoadResult.MaintenanceTableLoadMs + " maintenance_map_build_ms=" + songTableLoadResult.MaintenanceMapBuildMs + " maintenance_apply_ms=" + songTableLoadResult.MaintenanceApplyMs + " bmsfiles_assign_ms=" + songTableLoadResult.BmsFilesAssignMs + " commit_ms=" + songTableLoadResult.CommitMs);
            }
            stopwatchSongTblLoad.Stop();
            songTblLoadMs = stopwatchSongTblLoad.ElapsedMilliseconds;
        }
        if (scoreTblrLoad)
        {
            Stopwatch stopwatchScoreTblLoad = Stopwatch.StartNew();
            using (rwlockBMSScores.GetWriterGuard())
            {
                if (lr2ScoreDBPath != null)
                {
                    ScoreTableLoadResult scoreTableLoadResult = initializationService.LoadScoreTable(dbGateway);
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
            stopwatchScoreTblLoad.Stop();
            scoreTblLoadMs = stopwatchScoreTblLoad.ElapsedMilliseconds;
        }
        if (songTblFileCheck)
        {
            Stopwatch stopwatchSongTblFileCheck = Stopwatch.StartNew();
            SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
                dbGateway,
                options,
                BMSFiles,
                bmsScanPrefetchInfo?.ScanResult,
                bmsScanPrefetchInfo?.ElapsedMs ?? 0L,
                () => ExecuteBmsScanWithFallback(bMSDirectories),
                dialogService,
                LogInstallPerformance,
                LogEverythingScan,
                BmsonSongs,
                null);
            using (rwlockBMSFiles.GetWriterGuard())
            {
                BMSFiles = fileCheckResult.NextFiles;
                BmsonSongs = fileCheckResult.NextBmsonSongs;
                bmsFolderAllFileList = fileCheckResult.NextFolderAllFileList ?? new BMSDirectoryFileNameHash();
                directoryResourceLookupCache = fileCheckResult.NextDirectoryResourceLookupCache ?? new DirectoryResourceLookupCache();
            }
            if (fileCheckResult.HasDbDiff)
            {
                InvalidateBMSHashIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
            }
            stopwatchSongTblFileCheck.Stop();
            songTblFileCheckMs = stopwatchSongTblFileCheck.ElapsedMilliseconds;
        }
        RunChartDigestBackfill();
        if (setMainteInfo)
        {
            Stopwatch stopwatchSetMaintenance = Stopwatch.StartNew();
            Stopwatch stopwatchSetMode = Stopwatch.StartNew();
            setModeAndCommitToDB(BMSFiles);
            stopwatchSetMode.Stop();
            setModeMs = stopwatchSetMode.ElapsedMilliseconds;
            Stopwatch stopwatchSetHealth = Stopwatch.StartNew();
            setMaintenanceInfo(BMSFiles);
            stopwatchSetHealth.Stop();
            setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
            IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
            IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
            Stopwatch stopwatchSetZeroNote = Stopwatch.StartNew();
            setZeroNoteAndCommitToDB(BMSFiles);
            stopwatchSetZeroNote.Stop();
            setZeroNoteMs = stopwatchSetZeroNote.ElapsedMilliseconds;
            IsWriteLockHeldInitializeBMSFilesZeroNote = false;
            stopwatchSetMaintenance.Stop();
            setMaintenanceMs = stopwatchSetMaintenance.ElapsedMilliseconds;
            GC.Collect();
            NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
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
                        (bmsFile) => checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true));
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
        if (maintenanceTblCheck)
        {
            Stopwatch stopwatchMaintenanceTblCheck = Stopwatch.StartNew();
            int maintenanceDeleted = CleanupMaintenanceTable();
            stopwatchMaintenanceTblCheck.Stop();
            maintenanceTblCheckMs = stopwatchMaintenanceTblCheck.ElapsedMilliseconds;
            LogInstallPerformance("maintenance_tbl_check mode=immediate deleted=" + maintenanceDeleted + " elapsedMs=" + maintenanceTblCheckMs);
        }
        stopwatchInitialize.Stop();
        LogInstallPerformance("init_library_internal song_tbl_load_ms=" + songTblLoadMs + " score_tbl_load_ms=" + scoreTblLoadMs + " song_tbl_file_check_ms=" + songTblFileCheckMs + " set_maintenance_ms=" + setMaintenanceMs + " set_mode_ms=" + setModeMs + " set_health_ms=" + setHealthMs + " set_zero_note_ms=" + setZeroNoteMs + " install_tbl_check_ms=" + installTblCheckMs + " rebuild_hash_index_ms=" + rebuildHashIndexMs + " maintenance_tbl_check_ms=" + maintenanceTblCheckMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
    }

    private void RunChartDigestBackfill()
    {
        bool needsBmsonSchemaMigration = !dbGateway.IsBmsonAppSchemaCurrent();
        if (!needsBmsonSchemaMigration)
        {
            ChartDigestBackfillRunning = false;
            ChartDigestBackfillCurrentPath = string.Empty;
            return;
        }
        int requestVersion;
        lock (lockChartDigestBackfill)
        {
            chartDigestBackfillRequestedVersion++;
            requestVersion = chartDigestBackfillRequestedVersion;
        }
        ChartDigestBackfillRequestedVersion = requestVersion;
        ChartDigestBackfillRunning = true;
        ChartDigestBackfillTotalCount = 0;
        ChartDigestBackfillProcessedCount = 0;
        ChartDigestBackfillCurrentPath = string.Empty;
        List<BMSFile> filesSnapshot;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            filesSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        }
        try
        {
            ChartDigestBackfillResult result = initializationService.BackfillChartDigests(
                dbGateway,
                filesSnapshot,
                delegate (int total, int processed, string currentPath)
                {
                    ChartDigestBackfillTotalCount = total;
                    ChartDigestBackfillProcessedCount = processed;
                    ChartDigestBackfillCurrentPath = currentPath ?? string.Empty;
                },
                LogInstallPerformance);
            if (result.FailedCount <= 0)
            {
                dbGateway.MarkBmsonAppSchemaCurrent();
            }
            lock (lockPlaylistSummaryOwnedHashSnapshot)
            {
                playlistSummaryOwnedHashSnapshot = null;
            }
        }
        finally
        {
            lock (lockChartDigestBackfill)
            {
                chartDigestBackfillCompletedVersion = requestVersion;
            }
            ChartDigestBackfillCurrentPath = string.Empty;
            ChartDigestBackfillCompletedVersion = requestVersion;
            ChartDigestBackfillRunning = false;
        }
    }

    private int CleanupMaintenanceTable()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                return maintenanceService.CleanupMaintenanceTable(BMSFiles, dbGateway);
            }
        }
    }

    private void QueueDeferredInstallableMaintenance(string reason, long criticalElapsedMs)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredInstallableMaintenance)
        {
            deferredInstallableMaintenanceRequestedVersion++;
            version = deferredInstallableMaintenanceRequestedVersion;
            deferredInstallableMaintenanceCriticalElapsedMs = criticalElapsedMs;
            if (!deferredInstallableMaintenanceRunning)
            {
                deferredInstallableMaintenanceRunning = true;
                shouldStartWorker = true;
            }
        }
        LogInstallPerformance("installable_maintenance_deferred queue reason=" + (reason ?? "unknown") + " version=" + version + " criticalMs=" + criticalElapsedMs);
        if (!shouldStartWorker)
        {
            return;
        }
        Task.Run(delegate
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
                try
                {
                    List<BMSFile> filesSnapshot;
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        filesSnapshot = (BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
                    }
                    Stopwatch stopwatchSetMode = Stopwatch.StartNew();
                    setModeAndCommitToDB(filesSnapshot);
                    stopwatchSetMode.Stop();
                    setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                    Stopwatch stopwatchSetHealth = Stopwatch.StartNew();
                    setMaintenanceInfo(filesSnapshot);
                    stopwatchSetHealth.Stop();
                    setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
                    IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
                    IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;

                    Stopwatch stopwatchSetZeroNote = Stopwatch.StartNew();
                    setZeroNoteAndCommitToDB(filesSnapshot);
                    stopwatchSetZeroNote.Stop();
                    setZeroNoteMs = stopwatchSetZeroNote.ElapsedMilliseconds;
                    IsWriteLockHeldInitializeBMSFilesZeroNote = false;

                    stopwatch.Stop();
                    LogInstallPerformance("installable_maintenance_deferred done version=" + requestVersion
                        + " criticalMs=" + requestCriticalElapsedMs
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
                        + " set_mode_ms=" + setModeMs
                        + " set_health_ms=" + setHealthMs
                        + " set_zero_note_ms=" + setZeroNoteMs
                        + " deferred_ms=" + stopwatch.ElapsedMilliseconds
                        + " message=" + ex.Message);
                }

                lock (lockDeferredInstallableMaintenance)
                {
                    deferredInstallableMaintenanceLastCompletedVersion = requestVersion;
                    if (requestVersion == deferredInstallableMaintenanceRequestedVersion)
                    {
                        deferredInstallableMaintenanceRunning = false;
                        return;
                    }
                }
            }
        }).Logging("ProcessDeferredInstallableMaintenance");
    }

    private void QueueDeferredReverseLookupWarmup(string reason)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredReverseLookupWarmup)
        {
            deferredReverseLookupWarmupRequestedVersion++;
            version = deferredReverseLookupWarmupRequestedVersion;
            if (!deferredReverseLookupWarmupRunning)
            {
                deferredReverseLookupWarmupRunning = true;
                shouldStartWorker = true;
            }
        }
        LogInstallPerformance("reverse_lookup_warmup_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Task.Run(ProcessDeferredReverseLookupWarmupRequests).Logging("ProcessDeferredReverseLookupWarmupRequests");
    }

    private async Task ProcessDeferredReverseLookupWarmupRequests()
    {
        while (true)
        {
            int requestVersion;
            lock (lockDeferredReverseLookupWarmup)
            {
                requestVersion = deferredReverseLookupWarmupRequestedVersion;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool superseded = false;
            try
            {
                DirectoryResourceLookupCache lookupCacheSnapshot;
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    lookupCacheSnapshot = directoryResourceLookupCache;
                }
                if (lookupCacheSnapshot == null)
                {
                    stopwatch.Stop();
                    LogInstallPerformance("reverse_lookup_warmup_deferred done version=" + requestVersion + " warmedHashes=0 elapsedMs=" + stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    int warmupVersion = lookupCacheSnapshot.WarmupVersion;
                    int totalEntries = lookupCacheSnapshot.PrepareWarmupState();
                    LogInstallPerformance("reverse_lookup_warmup_deferred run version=" + requestVersion + " totalEntries=" + totalEntries + " warmupVersion=" + warmupVersion);
                    int warmedHashes = 0;
                    long totalBuildMs = 0L;
                    while (true)
                    {
                        await WaitForUiIdleAsync().ConfigureAwait(false);

                        lock (lockDeferredReverseLookupWarmup)
                        {
                            if (requestVersion != deferredReverseLookupWarmupRequestedVersion)
                            {
                                superseded = true;
                            }
                        }
                        if (superseded)
                        {
                            stopwatch.Stop();
                            LogInstallPerformance("reverse_lookup_warmup_deferred cancelled version=" + requestVersion + " totalBuildMs=" + totalBuildMs + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " reason=superseded");
                            break;
                        }

                        DirectoryResourceLookupCache currentLookupCache;
                        using (rwlockBMSFiles.GetReaderGuard())
                        {
                            currentLookupCache = directoryResourceLookupCache;
                        }
                        if (!object.ReferenceEquals(lookupCacheSnapshot, currentLookupCache) || lookupCacheSnapshot.WarmupVersion != warmupVersion)
                        {
                            stopwatch.Stop();
                            LogInstallPerformance("reverse_lookup_warmup_deferred cancelled version=" + requestVersion + " totalBuildMs=" + totalBuildMs + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " reason=cache_replaced");
                            break;
                        }

                        DirectoryResourceLookupCache.ReverseLookupWarmupStepResult stepResult = lookupCacheSnapshot.WarmupReverseLookupStep(
                            reverseLookupWarmupChunkEntryCount,
                            reverseLookupWarmupChunkCpuBudgetMs,
                            CancellationToken.None);
                        if (stepResult.Cancelled)
                        {
                            stopwatch.Stop();
                            LogInstallPerformance("reverse_lookup_warmup_deferred cancelled version=" + requestVersion + " totalBuildMs=" + totalBuildMs + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " reason=token");
                            break;
                        }
                        if (stepResult.Paused)
                        {
                            continue;
                        }
                        if (stepResult.ChunkEntryCount > 0)
                        {
                            warmedHashes = stepResult.BuiltHashCount;
                            totalBuildMs = stepResult.TotalBuildMs;
                            LogInstallPerformance("reverse_lookup_warmup_chunk version=" + requestVersion
                                + " chunkEntries=" + stepResult.ChunkEntryCount
                                + " processedEntries=" + stepResult.ProcessedEntryCount
                                + " chunkBuildMs=" + stepResult.ChunkBuildMs
                                + " totalBuildMs=" + stepResult.TotalBuildMs
                                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                                + " totalEntries=" + stepResult.TotalEntryCount);
                        }
                        if (stepResult.Completed)
                        {
                            stopwatch.Stop();
                            LogInstallPerformance("reverse_lookup_warmup_deferred done version=" + requestVersion + " warmedHashes=" + warmedHashes + " totalBuildMs=" + totalBuildMs + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("reverse_lookup_warmup_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
            }

            lock (lockDeferredReverseLookupWarmup)
            {
                deferredReverseLookupWarmupLastCompletedVersion = requestVersion;
                if (requestVersion == deferredReverseLookupWarmupRequestedVersion)
                {
                    deferredReverseLookupWarmupRunning = false;
                    return;
                }
            }
        }
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

    private void ScheduleDeferredMaintenanceTableCheck(string reason)
    {
        int version = 0;
        bool shouldStartWorker = false;
        bool markRunning = false;
        lock (lockDeferredMaintenanceTableCheck)
        {
            deferredMaintenanceTableCheckRequestedVersion++;
            version = deferredMaintenanceTableCheckRequestedVersion;
            if (!deferredMaintenanceTableCheckRunning)
            {
                deferredMaintenanceTableCheckRunning = true;
                shouldStartWorker = true;
                markRunning = true;
            }
        }
        MaintenanceDeferredRequestedVersion = version;
        if (markRunning)
        {
            MaintenanceDeferredRunning = true;
        }
        LogInstallPerformance("maintenance_tbl_check_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Task.Run(delegate
        {
            while (true)
            {
                int requestVersion = 0;
                lock (lockDeferredMaintenanceTableCheck)
                {
                    requestVersion = deferredMaintenanceTableCheckRequestedVersion;
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    LogInstallPerformance("maintenance_tbl_check_deferred run version=" + requestVersion);
                    int maintenanceDeleted = CleanupMaintenanceTable();
                    stopwatch.Stop();
                    LogInstallPerformance("maintenance_tbl_check_deferred done version=" + requestVersion + " deleted=" + maintenanceDeleted + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    LogInstallPerformance("maintenance_tbl_check_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                }
                bool markRunningFalse = false;
                lock (lockDeferredMaintenanceTableCheck)
                {
                    deferredMaintenanceTableCheckLastCompletedVersion = requestVersion;
                    if (requestVersion == deferredMaintenanceTableCheckRequestedVersion)
                    {
                        deferredMaintenanceTableCheckRunning = false;
                        markRunningFalse = true;
                    }
                }
                MaintenanceDeferredCompletedVersion = requestVersion;
                if (markRunningFalse)
                {
                    MaintenanceDeferredRunning = false;
                    return;
                }
            }
        });
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
        LogInstallPerformance("score_hydration_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
        if (shouldStartWorker)
        {
            Task.Run(ProcessDeferredScoreHydrationRequests).Logging("ProcessDeferredScoreHydrationRequests");
        }
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
        LogInstallPerformance("ranking_refresh_deferred queue reason=" + (reason ?? "unknown") + " version=" + version);
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
            try
            {
                LogInstallPerformance("score_hydration_deferred run version=" + requestVersion);
                RunDeferredScoreHydration(requestVersion);
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred done version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred cancelled version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("score_hydration_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
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
            try
            {
                LogInstallPerformance("ranking_refresh_deferred run version=" + requestVersion);
                RunDeferredRankingRefresh(requestVersion);
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred done version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred cancelled version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogInstallPerformance("ranking_refresh_deferred failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
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
    private void RunDeferredRankingRefresh(int requestVersion)
    {
        if (lr2ScoreDBPath == null || LR2ID == 0)
        {
            return;
        }
        List<LR2IRScore> scoreTable = updateLR2IRScoreTable();
        if (IsDeferredRankingRefreshRequestSuperseded(requestVersion))
        {
            throw new OperationCanceledException();
        }
        updateBMSScores(scoreTable);
        RefreshScoreSnapshotFromCurrentScores("deferred_ranking_refresh_ir_score");
        if (IsDeferredRankingRefreshRequestSuperseded(requestVersion))
        {
            throw new OperationCanceledException();
        }
        setRankingScore();
        RefreshScoreSnapshotFromCurrentScores("deferred_ranking_refresh_cache");
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
    /// deferred maintenance table check の進行状態を診断用に返します。
    /// </summary>
    /// <returns>現在の deferred maintenance 状態。</returns>
    internal DeferredMaintenanceTableCheckState GetDeferredMaintenanceTableCheckStateForDiagnostics()
    {
        return new DeferredMaintenanceTableCheckState
        {
            Running = MaintenanceDeferredRunning,
            RequestedVersion = MaintenanceDeferredRequestedVersion,
            LastCompletedVersion = MaintenanceDeferredCompletedVersion
        };
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
        return irService.UpdateIrScoreTable(LR2ID, dbGateway, irClient, lr2IRScoreRegex);
    }

    private void updateBMSScores(List<LR2IRScore> scoreTable)
    {
        if (lr2ScoreDBPath == null || LR2ID == 0 || scoreTable == null)
        {
            return;
        }
        using (rwlockBMSScores.GetWriterGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                BMSScores = irService.UpdateBmsScores(scoreTable, BMSScores, BMSFiles);
            }
        }
        RefreshScoreSnapshotFromCurrentScores("update_ir_score_table");
    }

    private void setRankingScore()
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        NLogWrapper.DebuggerLogger?.Trace("IR CACHE START");
        if (lr2ScoreDBPath == null || LR2ID == 0)
        {
            return;
        }
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE DIR END");
            using (rwlockBMSScores.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    irService.RefreshRankingScoresFromCache(LR2ID, lr2ScoreDBPath, dbGateway, BMSScores, BMSFiles, options.SkipEstimateOfflineScoreRanking);
                }
            }
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE END");
            GC.Collect();
            NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
        }
        RefreshScoreSnapshotFromCurrentScores("refresh_ranking_cache");
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
    private void setMaintenanceInfo(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        if (bmsFiles == null)
        {
            return;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                MaintenanceWorkflowResult workflowResult = maintenanceService.UpdateMaintenanceInfo(bmsFiles, forceUpdate, bmsFolderAllFileList, dbGateway, dialogService);
                if (workflowResult.HasUpdates)
                {
                    Task.Run(delegate
                    {
                        RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
                        RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
                        RaisePropertyChanged(() => BMSFilesGarbled);
                        RaisePropertyChanged(() => BMSFilesGarbledFixed);
                    }).Logging("setMaintenanceInfo");
                }
            }
            foreach (BMSFile bmsFile in bmsFiles)
            {
                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile);
            }
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
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        if (bmsFiles.Count() == 0)
        {
            return new List<BMSFile>();
        }
        _ = rwlockBMSFilesInitializedMin.IsWriteLockHeld;
        _ = rwlockBMSFiles.IsWriteLockHeld;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (forceUpdate)
                {
                    setMaintenanceInfo(bmsFiles, forceUpdate);
                }
                return bmsFiles.Where((BMSFile file) => checkBMSFileNeedToBeFixedAndSetWarnings(file) && isInIgnoredList == file.maintenanceInfo.is_files_warning_ignored).ToList();
            }
        }
    }

    /// <summary>
    /// 指定された BMS ファイル群の保守警告を無視リストに追加（または解除）し、DB に反映します。
    /// </summary>
    public void SetBMSFilesToBeFixedIgnored(IEnumerable<BMSFile> bmsFiles, bool unset = false)
    {
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        if (bmsFiles.Count() == 0)
        {
            return;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    List<BMSFileMaintenanceInfo> changes = maintenanceService.SetFilesWarningIgnored(bmsFiles, unset);
                    dbGateway.UpsertMaintenanceInfos(changes);
                }
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
    /// BMS ファイルのノート数がゼロかどうかをチェックし、該当する場合は DB にコミットします。
    /// </summary>
    private void setZeroNoteAndCommitToDB(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        using (rwlockBMSFiles.GetWriterGuard())
        {
            MaintenanceWorkflowResult workflowResult = maintenanceService.UpdateZeroNoteAndCommit(bmsFiles, dbGateway, dialogService);
            if (!workflowResult.HasUpdates)
            {
                return;
            }
            Task.Run(delegate
            {
                RaisePropertyChanged(() => BMSFilesZeroNote);
            }).Logging("setZeroNoteAndCommitToDB");
        }
    }

    /// <summary>
    /// DB 上のノート数が 0 のファイルについて、実際のファイルを再パースしてゼロノートかどうかを再検証します。
    /// </summary>
    public void RecheckZeroNoteWarnings()
    {
        List<BMSFile> allFiles;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            allFiles = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile f) => f != null).ToList());
        }
        ZeroNoteRecheckResult result = maintenanceService.RecheckZeroNoteWarnings(allFiles, (ex, message) => NLogWrapper.FileLogger?.Warn(ex, message));
        NLogWrapper.FileLogger?.Info(string.Format("zero_note_recheck total={0} mismatch={1} cleared={2} skipped={3}", result.Total, result.MismatchCount, result.ClearedCount, result.SkippedCount));
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

    /// <summary>
    /// BMS ファイル群のモード（SP/DP等）を検出し、song.db にコミットします。
    /// </summary>
    private void setModeAndCommitToDB(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> list = maintenanceService.DetectModeChanges(bmsFiles, forceUpdate);
            if (list.Count <= 0)
            {
                return;
            }
            dbGateway.UpsertSongs(list);
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
            file.IsHashDuplicated = false;
            file.warning = RemoveDuplicateWarning(file.warning);
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
        if (HasDuplicateWarning(file.warning))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(file.warning))
        {
            file.warning = DuplicateWarningMessage;
        }
        else
        {
            file.warning = file.warning + Environment.NewLine + DuplicateWarningMessage;
        }
    }

    private static bool HasDuplicateWarning(string warning)
    {
        return warning?.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any((string line) => string.Equals(line.Trim(), DuplicateWarningMessage, StringComparison.Ordinal)) ?? false;
    }

    private static string RemoveDuplicateWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return string.Empty;
        }
        return string.Join(Environment.NewLine, warning.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select((string line) => line.Trim()).Where((string line) => !string.Equals(line, DuplicateWarningMessage, StringComparison.Ordinal)).ToArray());
    }

    private static string AppendWarningLine(string warning, string warningLine)
    {
        if (string.IsNullOrWhiteSpace(warningLine))
        {
            return warning ?? string.Empty;
        }
        List<string> lines = (warning ?? string.Empty)
            .Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select((string line) => line.Trim())
            .Where((string line) => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (!lines.Contains(warningLine, StringComparer.Ordinal))
        {
            lines.Add(warningLine);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string RemoveInstallEstimationWarnings(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return string.Empty;
        }
        string[] warningPrefixes = new[]
            {
                Resources.Warning_InstallEstimationAmbiguousPrefix,
                Resources.Warning_InstallEstimationMetadataMismatchPrefix
            }
            .Concat(new[]
            {
                Resources.Warning_InstallEstimationAmbiguous,
                Resources.Warning_InstallEstimationMetadataMismatch
            }
                .Where((string template) => !string.IsNullOrWhiteSpace(template))
                .SelectMany((string template) => template.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Select((string line) => line.Trim())
                .Where((string line) => !string.IsNullOrWhiteSpace(line))
                .Select(delegate (string line)
                {
                    int placeholderIndex = line.IndexOf('{');
                    return placeholderIndex >= 0 ? line.Substring(0, placeholderIndex).TrimEnd() : line;
                }))
            .Where((string line) => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return string.Join(
            Environment.NewLine,
            warning
                .Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select((string line) => line.Trim())
                .Where((string line) => !string.IsNullOrWhiteSpace(line)
                    && !warningPrefixes.Any((string prefix) => line.StartsWith(prefix, StringComparison.Ordinal))));
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
                bmsFile.warning = RemoveInstallEstimationWarnings(bmsFile.warning);
            }
            if (!preserveAmbiguousInstallContext)
            {
                bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
                bmsFile.HasLowConfidenceInstallWarning = false;
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
        string ambiguousWarning = selectedCandidate == null || secondCandidate == null
            ? string.Empty
            : string.Format(Resources.Warning_InstallEstimationAmbiguous, selectedCandidate.DirectoryPath, secondCandidate.DirectoryPath);
        string metadataMismatchWarning = selectedCandidate == null
            ? string.Empty
            : string.Format(Resources.Warning_InstallEstimationMetadataMismatch, selectedCandidate.DirectoryPath);
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            bmsFile.warning = RemoveInstallEstimationWarnings(bmsFile.warning);
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
            bmsFile.HasLowConfidenceInstallWarning = isLowConfidenceAmbiguous || isLowConfidenceMetadataMismatch;
            if (isLowConfidenceAmbiguous && !string.IsNullOrWhiteSpace(ambiguousWarning))
            {
                bmsFile.warning = AppendWarningLine(bmsFile.warning, ambiguousWarning);
            }
            else if (isLowConfidenceMetadataMismatch && !string.IsNullOrWhiteSpace(metadataMismatchWarning))
            {
                bmsFile.warning = AppendWarningLine(bmsFile.warning, metadataMismatchWarning);
            }
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
                            (bmsFile) => checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true),
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
                        BackgroundPendingEstimatePreparationResult estimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(applyResult.EstimateTargets);
                        pendingPackagesToEstimate = estimatePreparation.EstimablePackages;
                        deferredPendingEstimatePackages = estimatePreparation.DeferredPackages;
                        deferredPendingEstimateHealthByPackage = estimatePreparation.DeferredSourceHealthByPackage;
                        regroupEligibleSourceDirectories = workflow.RegroupEligibleSourceDirectories.ToList();
                        registeredPackages = discoveredPackages;
                    }
                }
                foreach (BMSPackage deferredPackage in deferredPendingEstimatePackages)
                {
                    int sourceHealth = 0;
                    deferredPendingEstimateHealthByPackage.TryGetValue(deferredPackage, out sourceHealth);
                    LogInstallPerformance("pending_estimate_batch skipped source=auto_install package=" + (deferredPackage?.path ?? string.Empty) + " reason=healthy_source_baseline sourceHealth=" + sourceHealth);
                }
                if (!token.IsCancellationRequested && pendingPackagesToEstimate.Count > 0)
                {
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(pendingPackagesToEstimate.FirstOrDefault()?.path);
                    QueuePendingInstallEstimateBatch(new PendingInstallEstimateBatchRequest(
                        PendingInstallEstimateBatchSource.AutoInstall,
                        pendingPackagesToEstimate,
                        displayName,
                        regroupEligibleSourceDirectories,
                        deferredPendingEstimatePackages.Count));
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
            (files) => setZeroNoteAndCommitToDB(files),
            (files) => SetBMSScore(files),
            delegate (IEnumerable<BMSFile> files)
            {
                List<BMSFile> addedFiles = files.Where((BMSFile file) => file != null).ToList();
                List<BMSFile> addedBmsFiles = addedFiles.Where(PendingChartEntry.IsBmsChartFile).ToList();
                List<LR2SongDBExtended.bmson_song> addedBmsonSongs = BuildBmsonSongsFromChartRows(addedFiles);
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
                foreach (string dir in addedDirectoryScan.ChartDirectories)
                {
                    if (addedDirectoryScan.AllResourceBaseNameHashesByChartDirectory.TryGetValue(dir, out uint[] hashes))
                    {
                        bmsFolderAllFileList.AddDirHashed(dir, hashes);
                    }
                    directoryResourceLookupCache.AddDir(dir, addedDirectoryScan);
                }
                QueueDeferredReverseLookupWarmup("install_package");
                InvalidateInstalledDirectoryIndex();
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
                return new LR2SongDBExtended.bmson_song
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
                    preview_music = source.preview_music
                };
            })
            .ToList();
    }

    /// <summary>
    /// BMSファイルの差分譜面導入先（インストール先ディレクトリ）を推定します。
    /// </summary>
    /// <param name="bmsFiles">インストール対象のBMSファイルリスト（通常は同一パッケージ内のファイル群）</param>
    /// <param name="asParallel">既存フォルダの走査（各フォルダとのマッチング評価）を並列実行するかどうか</param>
    /// <param name="fixMode">手動修正モードフラグ（登録済みのファイルでも強制的に再推定を実施するかどうか）</param>
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
        searchEstimatedInstallationDirectory(bmsFiles, asParallel, fixMode ? BmsInstallationEstimateMode.Fix : BmsInstallationEstimateMode.Normal);
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
                    DirectoryResourceLookupCache directoryLookupCacheSnapshot = directoryResourceLookupCache;
                    long lazyHashBuildMsBefore = directoryLookupCacheSnapshot?.LazyHashBuildMs ?? 0L;
                    long lazyHashLookupCountBefore = directoryLookupCacheSnapshot?.LazyHashLookupCount ?? 0L;
                    int lazyHashCacheEntriesBefore = directoryLookupCacheSnapshot?.LazyHashCacheEntryCount ?? 0;
                    PackageInstallEstimationSnapshot estimationSnapshot = package != null
                        ? package.GetOrBuildInstallEstimationSnapshot(targetBmsFiles)
                        : PackageInstallEstimationSnapshotBuilder.BuildForLooseFiles(targetBmsFiles);
                    InstallEstimationResult result = CreateInstallEstimationService().EstimateInstallationDirectory(
                        estimationSnapshot,
                        bmsFolderAllFileList,
                        directoryResourceLookupCache,
                        asParallel,
                        estimateMode,
                        ResolveInstallDestinationRepresentativeMetadataUnsafe,
                        ResolveInstallDestinationMetadataProfileUnsafe);
                    long lazyHashBuildMsAfter = directoryLookupCacheSnapshot?.LazyHashBuildMs ?? lazyHashBuildMsBefore;
                    long lazyHashLookupCountAfter = directoryLookupCacheSnapshot?.LazyHashLookupCount ?? lazyHashLookupCountBefore;
                    int lazyHashCacheEntriesAfter = directoryLookupCacheSnapshot?.LazyHashCacheEntryCount ?? lazyHashCacheEntriesBefore;
                    LogInstallPerformance("estimate_install start chartCount=" + targetBmsFiles.Count + " targetHashes=" + result.TargetResourceHashCount + " bundledAudioCount=" + result.BundledAudioCount + " bundledImageCount=" + result.BundledImageCount + " bundledMovieCount=" + result.BundledMovieCount + " candidateMode=" + (result.CandidateMode ?? string.Empty) + " coarseFilterMode=" + (result.CoarseFilterMode ?? string.Empty) + " audioRefs=" + result.AudioReferenceCount + " audioMinMatchRequired=" + result.AudioMinimumMatchRequired + " candidateDirsBefore=" + result.CandidateDirectoryCountBeforeHashFilter + " candidateDirsAfterBroadFilter=" + result.CandidateDirectoryCountAfterBroadFilter + " candidateDirsAfterAudioGate=" + result.CandidateDirectoryCountAfterAudioGate + " candidateDirsAfter=" + result.CandidateDirectoryCountAfterHashFilter + " candidateDirs=" + result.CandidateDirectoryCount + " evaluationMs=" + result.EvaluationMs + " fallback=" + result.UsedFallbackCandidateExpansion + " confidence=" + result.Confidence + " autoApplied=" + result.ShouldAutoApplyDestination + " confidenceReason=" + (result.ConfidenceReason ?? string.Empty) + " lazyHashBuildMsDelta=" + (lazyHashBuildMsAfter - lazyHashBuildMsBefore) + " lazyHashEntriesAdded=" + (lazyHashCacheEntriesAfter - lazyHashCacheEntriesBefore) + " lazyHashLookupCountDelta=" + (lazyHashLookupCountAfter - lazyHashLookupCountBefore) + " lazyHashBuildReason=demand summary=" + (result.ResourceSummary ?? string.Empty));
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
                    ApplyInstallEstimationResultToFiles(targetBmsFiles, result);
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
                item.warning = Resources.Warning_AlreadyInstalled;
            }
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
            LogInstallPerformance("mixed_package_resolve fallback reason=installed_index_empty");
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
            LogInstallPerformance("mixed_package_resolve fallback reason=" + reason + " tieCandidates=" + resolution.CandidateDirectoryCount);
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
                            // 解決できない場合だけ従来推定へフォールバックし、空欄のまま残るケースを減らす。
                            LogInstallPerformance("mixed_package_resolve fallback reason=use_legacy_search missing=" + missingFiles.Count);
                            searchEstimatedInstallationDirectory(package, missingFiles, asParallel: true, BmsInstallationEstimateMode.Fix);
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
            bmsFile.warning = null;
            string key = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
            if (!string.IsNullOrWhiteSpace(key) && installedHashSet.Contains(key))
            {
                bmsFile.warning = Resources.Warning_AlreadyInstalled;
            }
            else if (isSingleFilePackage)
            {
                bmsFile.warning = isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile;
            }
            else
            {
                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true);
            }
        }
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
                    bmsFile.warning = RemoveInstallEstimationWarnings(bmsFile.warning);
                    bmsFile.InstallDestinationTitle = string.Empty;
                    bmsFile.InstallDestinationArtist = string.Empty;
                    bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
                    bmsFile.HasLowConfidenceInstallWarning = false;
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
                PendingInstallDestinationSelectionResult selection = CreateInstallEstimationService().ValidatePendingInstallDestination(bmsFile, BMSPackagesPending, CreateKnownChartDirectorySnapshotUnsafe(), destinationDirectory);
                if (!selection.Success)
                {
                    dialogService.Show(selection.WarningMessage, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                bool preserveAmbiguousInstallContext = !string.IsNullOrWhiteSpace(selection.ValidatedDestinationDirectory)
                    && selection.TargetFiles.Any((BMSFile file) => file != null
                        && file.HasLowConfidenceInstallWarning
                        && (file.InstallDestinationSuggestions?.Any((string path) => string.Equals(path, selection.ValidatedDestinationDirectory, StringComparison.OrdinalIgnoreCase)) ?? false));
                ApplyResolvedInstallDestinationToFiles(selection.TargetFiles, selection.ValidatedDestinationDirectory, preserveAmbiguousInstallContext);
                ClearDeferredEstimateReasonForFilesUnsafe(selection.TargetFiles);
                return true;
            }
        }
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                InstalledDirectoryLookupResult result = CreateInstallEstimationService().TryGetInstalledDirectoryByHash(BMSFiles, hash);
                if (!result.Success)
                {
                    return false;
                }
                installDir = result.InstallDirectory;
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
                    foreach (string item in bmsFolderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        bmsFolderAllFileList.RemoveDir(item);
                        directoryResourceLookupCache.RemoveDir(item);
                    }
                    if (!moveBMSPackageFiles(mergeResult.Repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true, existingHashes: mergeResult.ExistingHashes))
                    {
                        dialogService.Show(string.Format(Resources.Error_BmsFolderMergeFailed, src, dst), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    BmsScanResult mergedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots(new[] { dst });
                    foreach (string chartDirectory in mergedDirectoryScan.ChartDirectories)
                    {
                        if (mergedDirectoryScan.AllResourceBaseNameHashesByChartDirectory.TryGetValue(chartDirectory, out uint[] hashes))
                        {
                            bmsFolderAllFileList.AddDirHashed(chartDirectory, hashes);
                        }
                        directoryResourceLookupCache.AddDir(chartDirectory, mergedDirectoryScan);
                    }
                    QueueDeferredReverseLookupWarmup("merge_folder");
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
                    List<FolderAutoRenamePlan> plans = libraryFileOperationsService.BuildRootFolderMovePlans(bmsFiles, dstDir);
                    if ((bmsFiles ?? Enumerable.Empty<BMSFile>()).Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase).Any((string f) => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)))
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
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                if (!File.Exists(bmsFile.path) || BMSFiles.Any((BMSFile f) => f.path.Equals(dstPath, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                if (File.Exists(dstPath) || Directory.Exists(dstPath))
                {
                    dialogService.Show(string.Format(Resources.Warn_RenameDestAlreadyExists, bmsFile.path, dstPath), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return;
                }
                try
                {
                    libraryFileOperationsService.MoveFileOnDisk(bmsFile, dstPath, fileMutationService, targetOnlyFileMutationOptions);
                }
                catch (Exception moveException)
                {
                    dialogService.Show(string.Format(Resources.Error_BmsFileMoveFailed, bmsFile.path, dstPath, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    return;
                }
                if (unregister != false && unregister != true)
                {
                    return;
                }
                ApplyLibraryMutationDelta(libraryFileOperationsService.BuildFileMoveDelta(bmsFile, dstPath, unregister == true));
            }
        }
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
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LibraryRemovalResult result = libraryFileOperationsService.DeleteLibraryFiles(
                        bmsFiles,
                        BMSFiles,
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
                    List<BMSFile> removedBmsFiles = result.RemovedFiles.Where((BMSFile file) => file != null && !PendingChartEntry.IsBmsonChartFile(file)).ToList();
                    List<LR2SongDBExtended.bmson_song> removedBmsonSongs = result.RemovedFiles
                        .OfType<PendingChartEntry>()
                        .Where((PendingChartEntry entry) => entry.BmsonSong != null)
                        .Select((PendingChartEntry entry) => entry.BmsonSong)
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
