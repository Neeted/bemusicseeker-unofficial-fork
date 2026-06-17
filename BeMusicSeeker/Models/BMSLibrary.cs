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

    private readonly object lockOwnedChartCollection = new();

    private OwnedChartCollectionState ownedChartCollection = new();

    private bool ownedChartCollectionInitialized;

    private int ownedChartCollectionBmsStorageRowsVersion = -1;

    private int ownedChartCollectionBmsonStorageRowsVersion = -1;

    private int ownedChartCollectionVersion;

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

    private readonly object lockStorageRowsVersion = new();

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

    private const long Lr2SongDbSyncCompletedStatusImplicitChartInfoParseTimeoutMs = 60000L;

    private bool chartInfoHydrationPending;

    private string chartInfoHydrationPendingReason;

    private bool chartInfoHydrationPendingQueueBackfill;

    private readonly object lockChartInfoIndex = new();

    private readonly object lockChartInfoLazyDisplayIndexLoad = new();

    private Dictionary<string, LR2SongDBExtended.chart_info> chartInfoIndexBySha256 = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> chartInfoIndexByMd5 = new(StringComparer.OrdinalIgnoreCase);

    private bool chartInfoDisplayIndexLoaded;

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

    private int chartInfoBackfillRequestedVersion;

    private int chartInfoBackfillCompletedVersion;

    private int chartInfoBackfillHydrationBypassUntilVersion;

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

    private int lr2SongDbSyncScanSurfaceGeneration;

    private readonly object lockLr2SongDbSyncFileDiffFreshness = new();

    private Lr2SongDbSyncFileDiffFreshnessSnapshot lr2SongDbSyncFileDiffFreshnessSnapshot;

    private Lr2SongDbSyncPreparedDataSurface lr2SongDbSyncPreparedDataSurface =
        Lr2SongDbSyncPreparedDataSurface.Empty;

    private int lr2SongDbSyncPreparedDataSurfaceAppliedScanGeneration;

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

    private int bmsStorageRowsVersion;

    private int bmsonStorageRowsVersion;

    private NormalLibraryRefreshNotification latestNormalLibraryRefreshNotification = NormalLibraryRefreshNotification.Empty;

    private readonly List<NormalLibraryRefreshNotification> normalLibraryRefreshNotifications = [];

    private readonly object latestNormalLibraryRefreshNotificationLock = new();

    private int latestNormalLibraryRefreshNotificationVersion;

    private readonly object installDestinationRuntimeStatesLock = new();

    private readonly Dictionary<string, InstallDestinationRuntimeStateEntry> installDestinationRuntimeStatesByKey = new(StringComparer.OrdinalIgnoreCase);

    private InstallDestinationOverlayChartRefSnapshot installDestinationOverlayChartRefSnapshot;

    private readonly object resourceHealthIndexLock = new();

    private ResourceHealthIndexSnapshotState resourceHealthIndexState = new(ResourceHealthIndexSnapshot.Empty, -1);

    private bool resourceHealthIndexInvalidated = true;

    private int suppressResourceHealthIndexInvalidation;

    private int resourceHealthIndexVersionSeed;

    private int resourceHealthInputVersion;

    private int resourceHealthInputMutationDepth;

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

    private ChartInfoHydrationAllCurrentSnapshot chartInfoHydrationAllCurrentSnapshot;

    private ChartInfoCompletedLr2SongDbSyncTrustSnapshot chartInfoCompletedLr2SongDbSyncTrustSnapshot;

    private bool _Lr2SongDbSyncRunning;

    private bool lr2SongDbSyncPrepareInProgress;

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
    public IReadOnlyList<BMSFile> BMSFiles
    {
        get
        {
            return (_BMSFiles ?? []).AsReadOnly();
        }
        set
        {
            List<BMSFile> normalized = NormalizeBmsStorageRows(value);
            if (!ReferenceEquals(_BMSFiles, normalized))
            {
                using (BeginResourceHealthInputMutation())
                {
                    InvalidatePlaylistSummaryOwnedHashSnapshot();
                    InvalidatePlaylistLibraryResolveIndexSnapshot();
                    SetBmsStorageRowsCoreUnsafe(normalized);
                    InvalidateOwnedChartCollection();
                    int ownedCollectionVersion = NotifyOwnedChartCollectionChanged();
                    InvalidatePlaylistSummaryOwnedHashSnapshot(ownedCollectionVersion);
                    InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
                    PublishExternalReplacementNormalLibraryRefreshNotification(
                        notifiesBmsFiles: true,
                        notifiesBmsonSongs: false);
                    InvalidateInstalledDirectoryIndex();
                    InvalidateBMSParentFolderListCache();
                    InvalidateDuplicateChartGroupsCache();
                    InvalidateResourceHealthIndex("bmsfiles_changed");
                    PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
                }
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                }).Logging("BMSFiles");
                RaisePropertyChanged(() => BMSParentFolderListCacheVersion);
            }
        }
    }

    internal IEnumerable<ChartFile> ChartFilesUnregistered => CreateBmsChartSubsetSnapshot(
        BMSFiles.Where(file => file?.HasWarningCategory(ChartWarningCategory.Lr2Compatibility) == true));

    internal int NormalLibraryRefreshNotificationVersion => Volatile.Read(ref latestNormalLibraryRefreshNotificationVersion);

    internal NormalLibraryRefreshNotificationBatch GetNormalLibraryRefreshNotificationsAfter(int handledVersion)
    {
        lock (latestNormalLibraryRefreshNotificationLock)
        {
            normalLibraryRefreshNotifications.RemoveAll(notification => notification == null || notification.Version <= handledVersion);
            List<NormalLibraryRefreshNotification> notifications = [.. normalLibraryRefreshNotifications
                .Where(notification => notification != null && notification.Version > handledVersion)];
            int resetIndex = notifications.FindLastIndex(notification => notification.ResetsPriorNotifications);
            if (resetIndex >= 0)
            {
                notifications = [.. notifications.Skip(resetIndex)];
            }
            if (notifications.Count == 0)
            {
                return new NormalLibraryRefreshNotificationBatch(
                    handledVersion,
                    0,
                    LibraryChartRefreshEffects.None,
                    [],
                    notifiesStorageRows: false,
                    resetsPriorNotifications: false);
            }
            bool resetsPriorNotifications = resetIndex >= 0;
            int latestVersion = notifications[notifications.Count - 1].Version;
            int ownedCollectionVersion = notifications[notifications.Count - 1].OwnedCollectionVersion;
            LibraryChartRefreshEffects effects = notifications.Aggregate(
                LibraryChartRefreshEffects.None,
                (current, notification) => current | notification.Effects);
            bool notifiesStorageRows = notifications.Any(notification => notification.NotifiesStorageRows);
            bool notifiesBmsFiles = notifications.Any(notification => notification.NotifiesBmsFiles);
            bool notifiesBmsonSongs = notifications.Any(notification => notification.NotifiesBmsonSongs);
            bool storageRowsRemoveDeltaComplete = notifiesStorageRows
                && notifications
                    .Where(notification => notification.NotifiesStorageRows)
                    .All(notification => notification.StorageRowsRemoveDeltaComplete);
            List<BMSFile> removedBmsFiles = storageRowsRemoveDeltaComplete
                ? [.. notifications.SelectMany(notification => notification.RemovedBmsFiles ?? []).Where(file => file != null).Distinct()]
                : [];
            List<LR2SongDBExtended.bmson_song> removedBmsonSongs = storageRowsRemoveDeltaComplete
                ? [.. notifications.SelectMany(notification => notification.RemovedBmsonSongs ?? []).Where(song => song != null).Distinct()]
                : [];
            List<ChartFile> installDestinationChangedCharts = [.. notifications
                .SelectMany(notification => notification.InstallDestinationChangedCharts ?? [])
                .Where(chart => chart != null)];
            return new NormalLibraryRefreshNotificationBatch(
                latestVersion,
                ownedCollectionVersion,
                effects,
                DistinctChartsByNotificationKey(installDestinationChangedCharts),
                notifiesStorageRows,
                resetsPriorNotifications,
                notifiesBmsFiles,
                notifiesBmsonSongs,
                removedBmsFiles,
                removedBmsonSongs,
                storageRowsRemoveDeltaComplete);
        }
    }

    internal int OwnedChartCollectionVersion => Volatile.Read(ref ownedChartCollectionVersion);

    internal IEnumerable<ChartFile> ChartFilesNeedResourceFix => GetChartsNeedResourceFix(null);

    public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs
    {
        get
        {
            return (_BmsonSongs ?? []).AsReadOnly();
        }
        set
        {
            List<LR2SongDBExtended.bmson_song> normalized = NormalizeBmsonStorageRows(value);
            if (!ReferenceEquals(_BmsonSongs, normalized))
            {
                using (BeginResourceHealthInputMutation())
                {
                    InvalidatePlaylistSummaryOwnedHashSnapshot();
                    InvalidatePlaylistLibraryResolveIndexSnapshot();
                    SetBmsonStorageRowsCoreUnsafe(normalized);
                    InvalidateOwnedChartCollection();
                    int ownedCollectionVersion = NotifyOwnedChartCollectionChanged();
                    InvalidatePlaylistSummaryOwnedHashSnapshot(ownedCollectionVersion);
                    InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
                    PublishExternalReplacementNormalLibraryRefreshNotification(
                        notifiesBmsFiles: false,
                        notifiesBmsonSongs: true);
                    InvalidateInstalledDirectoryIndex();
                    InvalidateBMSParentFolderListCache();
                    InvalidateInstallEstimationMetadataProfileCache();
                    InvalidateDuplicateChartGroupsCache();
                    InvalidateResourceHealthIndex("bmsons_changed");
                    PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
                }
                Task.Run(delegate
                {
                    RaisePropertyChanged("BmsonSongs");
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
            return ownedChartCollection.CreatePathSnapshot();
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

    private int everythingFallbackWarningQueued;

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
        UnsupportedResourcePath,
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

        public string DbMaterializeMode { get; set; }

        public int DbRawRows { get; set; }

        public long DbRawReadMs { get; set; }

        public long DbRawObjectMs { get; set; }

        public bool FastPath { get; set; }

        public long CandidateSummaryMs { get; set; }

        public bool DbReadOnly { get; set; }

        public long DbLockWaitMs { get; set; }

        public int ParseFailureRows { get; set; }

        public long IndexBuildMs { get; set; }

        public long OwnerApplyMs { get; set; }

        public long TotalMs { get; set; }

        public bool Succeeded { get; set; }
    }

    private sealed class ChartInfoHydrationAllCurrentSnapshot
    {
        public int OwnedCollectionVersion { get; set; }

        public int BmsRowsVersion { get; set; }

        public int BmsonRowsVersion { get; set; }

        public int OwnerCount { get; set; }

        public int CurrentChartInfoOwnerCount { get; set; }

        public int CurrentParseFailureOwnerCount { get; set; }

        public int ParserVersion { get; set; }

        public long ParseTimeoutMs { get; set; }
    }

    private sealed class ChartInfoOwnerVersionSnapshot
    {
        public int OwnedCollectionVersion { get; set; }

        public int BmsRowsVersion { get; set; }

        public int BmsonRowsVersion { get; set; }

        public int BmsOwnerCount { get; set; }

        public int BmsonOwnerCount { get; set; }

        public int OwnerCount => BmsOwnerCount + BmsonOwnerCount;
    }

    private sealed class ChartInfoCompletedLr2SongDbSyncTrustSnapshot
    {
        public int OwnedCollectionVersion { get; set; }

        public int BmsRowsVersion { get; set; }

        public int BmsonRowsVersion { get; set; }

        public int BmsOwnerCount { get; set; }

        public int BmsonOwnerCount { get; set; }

        public string Reason { get; set; }

        public int OwnerCount => BmsOwnerCount + BmsonOwnerCount;

        public bool IsCurrent(ChartInfoOwnerVersionSnapshot version)
        {
            return version != null
                && OwnedCollectionVersion == version.OwnedCollectionVersion
                && BmsRowsVersion == version.BmsRowsVersion
                && BmsonRowsVersion == version.BmsonRowsVersion
                && BmsOwnerCount == version.BmsOwnerCount
                && BmsonOwnerCount == version.BmsonOwnerCount;
        }
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
            () => ChartPackagesPending,
            pendingPackages => ChartPackagesPending = pendingPackages,
            () => ChartPackagesInstalled,
            installedPackages => ChartPackagesInstalled = installedPackages,
            () => RaisePropertyChanged(() => ChartPackagesInstalled),
            MarkLr2SongDbSyncIncompleteAfterStateApplierSongDbWriteFailure);
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
        PendingEstimateSourceBatchSnapshot candidateSnapshot = BuildPendingEstimateSourceBatchSnapshotUnsafe(packageList, installEstimationService, installedDirectoryIndex, sourceLogValue, options.UseEverythingForPendingPackageSourceScan);
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

    private PendingEstimateSourceBatchSnapshot BuildPendingEstimateSourceBatchSnapshotUnsafe(List<ChartPackage> packageList, BmsLibraryInstallEstimationService installEstimationService, IInstalledChartLookupIndex installedDirectoryIndex, string sourceLogValue, bool useEverythingForPendingPackageSourceScan)
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
    private ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner = null)
    {
        IChartFileScanner scanner = new EverythingFileScanner();
        reportScanner?.Invoke("Native");
        ChartScanExecutionResult scanResult = scanner.Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            everythingScanLoggingEnabled,
            includeTextSurface,
            includeDirectorySurface);
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
        QueueEverythingFallbackWarning(nativeFailureReason);
        reportScanner?.Invoke("Fallback");
        ChartScanExecutionResult fallbackResult = new FastDirectoryFileScanner().Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            everythingScanLoggingEnabled,
            includeTextSurface,
            includeDirectorySurface);
        if (!fallbackResult.Success || fallbackResult.Result == null)
        {
            string fallbackFailureReason = fallbackResult?.ErrorReason ?? "unknown";
            LogEverythingScan("chart fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
            throw new InvalidOperationException("chart fallback file scan failed: " + fallbackFailureReason + " (native: " + nativeFailureReason + ")");
        }
        fallbackResult.FallbackUsed = true;
        fallbackResult.FallbackReason = nativeFailureReason;
        LogEverythingScan("chart fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
        return fallbackResult;
    }

    internal static bool ShouldIncludeLr2TextSurface(BmsLibraryOptionsSnapshot options)
    {
        return options?.OperationModeLR2DB == true;
    }

    internal static bool ShouldIncludeLr2DirectorySurface(BmsLibraryOptionsSnapshot options)
    {
        return options?.OperationModeLR2DB == true;
    }

    private static bool IsNativeBridgeContractFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }
        if (reason.StartsWith("directory_surface_failed:", StringComparison.OrdinalIgnoreCase))
        {
            return RootFileEnumerationService.IsBridgeContractFailure(reason.Substring("directory_surface_failed:".Length));
        }
        return RootFileEnumerationService.IsBridgeContractFailure(reason);
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
        bool songTblFileCheck = mode == LibraryInitializeMode.FullReinitialize || (isStartup && !options.SkipInitFileCheck);
        bool setMaintenanceInfo = !isScoreOnly;
        bool flag = !isScoreOnly;
        Task<ChartScanPrefetchInfo> chartScanPrefetchTask = null;
        Task<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotTask = null;
        List<string> fileCheckPrefetchDirectories = null;
        if (songTblFileCheck)
        {
            fileCheckPrefetchDirectories = getBMSDirectories();
            if (fileCheckPrefetchDirectories.Count > 0)
            {
                chartScanPrefetchTask = Task.Run(delegate
                {
                    var stopwatchPrefetch = Stopwatch.StartNew();
                    ChartScanExecutionResult scanResult = ExecuteChartScanWithManagedFallback(
                        fileCheckPrefetchDirectories,
                        ShouldIncludeLr2TextSurface(options),
                        ShouldIncludeLr2DirectorySurface(options),
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
                        if (songTblFileCheck
                            && options?.OperationModeLR2DB == true
                            && fileCheckPrefetchDirectories?.Count > 0)
                        {
                            normalFolderMtimeSnapshotTask = Task.Run(() =>
                                initializationService.LoadNormalFolderMtimeSnapshot(
                                    dbGateway,
                                    options,
                                    fileCheckPrefetchDirectories,
                                    LogInstallPerformance)).Logging("Lr2NormalFolderMtimeSnapshotPrefetch");
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
                    _initialize(
                        songTblLoad: false,
                        scoreTblrLoad: false,
                        songTblFileCheck,
                        setMainteInfo: false,
                        updateIrScore: true,
                        installTblCheck: false,
                        chartScanPrefetchInfo,
                        normalFolderMtimeSnapshotTask,
                        trackLibraryFileCheckProgress: true,
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
        const int delayMs = 30000;
        Task.Run(async delegate
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
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
        ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(AppDomain.CurrentDomain.BaseDirectory, dbGateway, LogInstallPerformance);
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
        Task<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotTask = null,
        bool trackLibraryDatabaseProgress = false,
        bool trackLibraryFileCheckProgress = false,
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
        var options = BmsLibraryOptionsSnapshot.CreateCurrent();
        bool scoreOnlyLoad = !songTblLoad && scoreTblrLoad && !songTblFileCheck && !setMainteInfo && !installTblCheck;
        bool logRootNormalizationForFileScan = songTblFileCheck;
        List<string> bMSDirectories = getBMSDirectories(out BmsSearchRootNormalizationSnapshot rootNormalization);
        if (logRootNormalizationForFileScan)
        {
            LogBmsSearchRootNormalization(fileScanReason, options, rootNormalization, bMSDirectories);
        }
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
            ApplyLibraryFileScanDiff(
                options,
                bMSDirectories,
                chartScanPrefetchInfo,
                normalFolderMtimeSnapshotTask,
                trackLibraryFileCheckProgress,
                fileScanReason);
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
                setOwnedMaintenanceInfo("initialize_set_maintenance");
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
        var stopwatch = Stopwatch.StartNew();
        LogBmsSearchRootNormalization("reload_file_diff", options, rootNormalization, bmsDirectories);
        LogInstallPerformance("library_file_diff_reload start directories=" + bmsDirectories.Count);
        try
        {
            using (rwlockBMSFilesInitializedAll.GetWriterGuard())
            {
                SongTableFileCheckResult result = ApplyLibraryFileScanDiff(options, bmsDirectories, null, null, trackLibraryFileCheckProgress: true, "reload_file_diff");
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
            MarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(options, ex, "reload_file_diff");
            throw;
        }
    }

    internal void ShowEverythingFallbackWarning(string fallbackReason)
    {
        string reason = string.IsNullOrWhiteSpace(fallbackReason) ? "unknown" : fallbackReason;
        dialogService.Show(
            string.Format(Resources.Warn_EverythingFallbackScanUsed, reason),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal bool QueueEverythingFallbackWarning(string fallbackReason)
    {
        if (Interlocked.Exchange(ref everythingFallbackWarningQueued, 1) != 0)
        {
            LogEverythingScan("everything fallback warning skipped reason=already_queued fallbackReason=" + (fallbackReason ?? string.Empty));
            return false;
        }

        if (TryQueueEverythingFallbackWarningOnDispatcher(fallbackReason))
        {
            return true;
        }

        LogEverythingScan("everything fallback warning queued target=thread_pool fallbackReason=" + (fallbackReason ?? string.Empty));
        Task.Run(() => ShowEverythingFallbackWarningSafely(fallbackReason)).Logging("EverythingFallbackWarningDialog");
        return true;
    }

    private void ResetEverythingFallbackWarningQueue()
    {
        Interlocked.Exchange(ref everythingFallbackWarningQueued, 0);
    }

    private bool TryQueueEverythingFallbackWarningOnDispatcher(string fallbackReason)
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
                ShowEverythingFallbackWarningSafely(fallbackReason);
            });
        }
        catch (Exception ex)
        {
            LogEverythingScan("everything fallback warning queue_failed target=ui_dispatcher fallbackReason=" + (fallbackReason ?? string.Empty) + " message=" + ex.Message);
            return false;
        }
        return true;
    }

    private void ShowEverythingFallbackWarningSafely(string fallbackReason)
    {
        try
        {
            ShowEverythingFallbackWarning(fallbackReason);
            LogEverythingScan("everything fallback warning shown fallbackReason=" + (fallbackReason ?? string.Empty));
        }
        catch (Exception ex)
        {
            LogEverythingScan("everything fallback warning failed fallbackReason=" + (fallbackReason ?? string.Empty) + " message=" + ex.Message);
        }
    }

    private SongTableFileCheckResult ApplyLibraryFileScanDiff(
        BmsLibraryOptionsSnapshot options,
        List<string> bmsDirectories,
        ChartScanPrefetchInfo chartScanPrefetchInfo,
        Task<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotTask,
        bool trackLibraryFileCheckProgress,
        string reason)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(ApplyLibraryFileScanDiff));
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
        Lr2NormalFolderMtimeSnapshot resolveNormalFolderMtimeSnapshot()
        {
            if (normalFolderMtimeSnapshotTask == null)
            {
                return null;
            }

            var stopwatchSnapshotWait = Stopwatch.StartNew();
            try
            {
                Lr2NormalFolderMtimeSnapshot snapshot = normalFolderMtimeSnapshotTask.GetAwaiter().GetResult();
                stopwatchSnapshotWait.Stop();
                LogInstallPerformance("lr2_normal_folder_mtime_snapshot_prefetch_wait"
                    + " status=completed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " rows=" + (snapshot?.ExistingRowCount ?? 0)
                    + " elapsedMs=" + (snapshot?.ElapsedMs ?? 0L));
                return snapshot;
            }
            catch (Exception ex)
            {
                stopwatchSnapshotWait.Stop();
                LogInstallPerformanceWarn("lr2_normal_folder_mtime_snapshot_prefetch failed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                return null;
            }
        }

        List<LR2SongDBExtended.chart_info> committedInlineChartInfoRows = [];
        List<ChartFile> currentInstallDestinationCharts = CreateCurrentInstallDestinationCleanupCharts();
        Task<Lr2FolderFileDiffPreparationResult> lr2FolderFileDiffPreparationTask = null;
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration = ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
        void StartLr2FolderFileDiffPreparation(SongTableFileCheckResult partialResult)
        {
            if (lr2FolderFileDiffPreparationTask != null)
            {
                return;
            }
            if (!CanPrepareLr2FolderFileDiff(options, partialResult))
            {
                return;
            }

            lr2FolderFileDiffPreparationTask = Task.Run(() =>
                PrepareLr2FolderFileDiffSync(options, bmsDirectories, partialResult, reason))
                .Logging("Lr2FolderFileDiffPrepare");
        }

        SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
            dbGateway,
            options,
            BMSFiles,
            chartScanPrefetchInfo?.ScanResult,
            chartScanPrefetchInfo?.ElapsedMs ?? 0L,
            () => ExecuteChartScanWithManagedFallback(
                bmsDirectories,
                ShouldIncludeLr2TextSurface(options),
                ShouldIncludeLr2DirectorySurface(options),
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
            currentInstallDestinationCharts,
            bmsDirectories,
            lr2FolderDiscoveryRootDirectories: bmsDirectories,
            lr2BuiltinCustomFolderSettings: CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
            normalFolderMtimeSnapshotProvider: resolveNormalFolderMtimeSnapshot,
            lr2ScanSurfacePrepared: StartLr2FolderFileDiffPreparation,
            protectExistingBmsRowsFromLr2SongDbSyncMigration: protectExistingBmsRowsFromLr2SongDbSyncMigration);
        ApplyLr2FolderFileDiffSync(options, bmsDirectories, fileCheckResult, reason, lr2FolderFileDiffPreparationTask);
        completeFileEnumerationOnce();
        ApplyLibraryFileScanStorageMutation(fileCheckResult, reason);
        CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(options, fileCheckResult, reason);
        if (committedInlineChartInfoRows.Count > 0)
        {
            UpsertChartInfoIndexRows(committedInlineChartInfoRows, "file_diff_inline");
            committedInlineChartInfoRows.Clear();
        }
        if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
            || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
            || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
            || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
        {
            DispatchWarningPresentationChanged("file_diff_inline_chart_info_parse_failure");
        }
        ApplyLibraryMutationDelta(fileCheckResult.MutationDelta);
        CaptureLr2SongDbSyncScanSurface(options, bmsDirectories, fileCheckResult);
        CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(options, fileCheckResult, reason);
        MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, fileCheckResult);
        if (trackLibraryFileCheckProgress)
        {
            CompleteLibraryFileDiffProgress();
        }
        fileCheckResult.ReleasePostApplyTransientBuffers();
        LogStartupMemoryCheckpoint("file_diff", "after_release");
        return fileCheckResult;
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

    private sealed class Lr2FolderFileDiffPreparationResult(
        Lr2SongDbSyncRequest request,
        IReadOnlyList<string> appManagedOutputDirectories,
        IReadOnlyList<string> appManagedOutputFilePaths,
        IReadOnlyList<string> appManagedPruneExcludedPaths,
        int appManagedCandidateCount,
        long rootsMs,
        long builtinSourceMs,
        long appManagedScopeMs,
        long filterMs,
        long parentSurfaceMs,
        long extraTextRootsMs,
        long textMetadataMs,
        long totalElapsedMs)
    {
        public Lr2SongDbSyncRequest Request { get; } = request;

        public IReadOnlyList<string> AppManagedOutputDirectories { get; } = appManagedOutputDirectories ?? [];

        public IReadOnlyList<string> AppManagedOutputFilePaths { get; } = appManagedOutputFilePaths ?? [];

        public IReadOnlyList<string> AppManagedPruneExcludedPaths { get; } = appManagedPruneExcludedPaths ?? [];

        public int AppManagedCandidateCount { get; } = appManagedCandidateCount;

        public long RootsMs { get; } = rootsMs;

        public long BuiltinSourceMs { get; } = builtinSourceMs;

        public long AppManagedScopeMs { get; } = appManagedScopeMs;

        public long FilterMs { get; } = filterMs;

        public long ParentSurfaceMs { get; } = parentSurfaceMs;

        public long ExtraTextRootsMs { get; } = extraTextRootsMs;

        public long TextMetadataMs { get; } = textMetadataMs;

        public long TotalElapsedMs { get; } = totalElapsedMs;
    }

    private void ApplyLr2FolderFileDiffSync(
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason,
        Task<Lr2FolderFileDiffPreparationResult> preparationTask = null)
    {
        if (!CanPrepareLr2FolderFileDiff(options, fileCheckResult))
        {
            return;
        }

        Lr2FolderFileDiffPreparationResult preparation = WaitForLr2FolderFileDiffPreparation(
            preparationTask,
            options,
            rootDirectories,
            fileCheckResult,
            reason);
        if (preparation?.Request == null)
        {
            return;
        }

        int scanCandidateCount = fileCheckResult.Lr2ScanLr2FolderFilePaths?.Count ?? 0;
        var stopwatchSurfaceApply = Stopwatch.StartNew();
        ApplyLr2SyncRequestSurfaceToFileCheckResult(fileCheckResult, preparation.Request);
        ApplyLr2FilteredFolderCandidateSurfaceToFileCheckResult(fileCheckResult, preparation);
        stopwatchSurfaceApply.Stop();
        long surfaceApplyMs = stopwatchSurfaceApply.ElapsedMilliseconds;
        bool allowPrune = ShouldPruneLr2FolderFileRowsDuringFileDiff(reason);
        LogInstallPerformance("lr2folder_file_diff_prepare"
            + " reason=" + (reason ?? "unknown")
            + " rootsMs=" + preparation.RootsMs
            + " builtinSourceMs=" + preparation.BuiltinSourceMs
            + " appManagedScopeMs=" + preparation.AppManagedScopeMs
            + " filterMs=" + preparation.FilterMs
            + " parentSurfaceMs=" + preparation.ParentSurfaceMs
            + " extraTextRootsMs=" + preparation.ExtraTextRootsMs
            + " textMetadataMs=" + preparation.TextMetadataMs
            + " surfaceApplyMs=" + surfaceApplyMs
            + " totalMs=" + (preparation.TotalElapsedMs + surfaceApplyMs));
        LogInstallPerformance("lr2folder_file_diff_filter"
            + " reason=" + (reason ?? "unknown")
            + " candidates=" + scanCandidateCount
            + " externalCandidates=" + preparation.Request.Lr2FolderFilePaths.Count
            + " appManagedFiltered=" + preparation.AppManagedCandidateCount
            + " appManagedScopeDirs=" + preparation.AppManagedOutputDirectories.Count
            + " appManagedExactFiles=" + preparation.AppManagedOutputFilePaths.Count
            + " appManagedPruneExcludedPaths=" + preparation.AppManagedPruneExcludedPaths.Count
            + " allowPruneRequested=" + allowPrune.ToString().ToLowerInvariant());
        SyncLr2FolderFileRows(
            options,
            preparation.Request,
            reason,
            "lr2folder_file_diff_sync",
            allowPrune,
            pruneExcludedPaths: preparation.AppManagedPruneExcludedPaths,
            scopeReadLr2FolderRowsOnly: true);
    }

    private static bool CanPrepareLr2FolderFileDiff(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult)
    {
        return options?.OperationModeLR2DB == true
            && fileCheckResult?.Lr2ScanSurfaceAvailable == true
            && fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories?.Count > 0;
    }

    private Lr2FolderFileDiffPreparationResult WaitForLr2FolderFileDiffPreparation(
        Task<Lr2FolderFileDiffPreparationResult> preparationTask,
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (preparationTask == null)
        {
            return PrepareLr2FolderFileDiffSync(options, rootDirectories, fileCheckResult, reason);
        }

        var stopwatchWait = Stopwatch.StartNew();
        try
        {
            Lr2FolderFileDiffPreparationResult result = preparationTask.GetAwaiter().GetResult();
            stopwatchWait.Stop();
            LogInstallPerformance("lr2folder_file_diff_prepare_wait"
                + " reason=" + (reason ?? "unknown")
                + " status=completed"
                + " waitMs=" + stopwatchWait.ElapsedMilliseconds
                + " preparedMs=" + (result?.TotalElapsedMs ?? 0L));
            return result;
        }
        catch (Exception ex)
        {
            stopwatchWait.Stop();
            LogInstallPerformanceWarn("lr2folder_file_diff_prepare_wait failed"
                + " reason=" + (reason ?? "unknown")
                + " waitMs=" + stopwatchWait.ElapsedMilliseconds
                + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            return PrepareLr2FolderFileDiffSync(options, rootDirectories, fileCheckResult, reason);
        }
    }

    private Lr2FolderFileDiffPreparationResult PrepareLr2FolderFileDiffSync(
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (!CanPrepareLr2FolderFileDiff(options, fileCheckResult))
        {
            return null;
        }

        var stopwatchPrepare = Stopwatch.StartNew();
        var stopwatchStage = Stopwatch.StartNew();
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeFullPathOrOriginal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        long rootsMs = RestartElapsed(stopwatchStage);
        List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories();
        long builtinSourceMs = RestartElapsed(stopwatchStage);
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
        long appManagedScopeMs = RestartElapsed(stopwatchStage);
        Lr2FolderFileCandidateSnapshot fileDiffCandidates;
        int appManagedCandidateCount;
        if (!appManagedOutputScope.IsComplete)
        {
            fileDiffCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
            appManagedCandidateCount = 0;
        }
        else
        {
            fileDiffCandidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                fileCheckResult.Lr2ScanLr2FolderFilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileEntries,
                appManagedOutputScope.FilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete,
                out appManagedCandidateCount,
                appManagedOutputScope.Directories);
        }
        long filterMs = RestartElapsed(stopwatchStage);
        var request = new Lr2SongDbSyncRequest
        {
            RootDirectories = roots,
            Lr2FolderDiscoveryDirectories = fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories,
            Lr2FolderPruneDirectories = CreateLr2SongDbSyncLr2FolderPruneDirectories(
                roots,
                builtinSourceDirectories),
            Lr2FolderFilePaths = fileDiffCandidates.Paths,
            Lr2FolderFileEntries = fileDiffCandidates.EntriesByPath,
            FolderInfoFilePaths = fileCheckResult.Lr2ScanFolderInfoFilePaths,
            FolderInfoFileEntries = fileCheckResult.Lr2ScanFolderInfoFileEntries,
            TextFileDirectories = fileCheckResult.Lr2ScanTextFileDirectories,
            DirectoryEntries = MergeMissingLr2DirectoryEntrySurface(
                fileCheckResult.Lr2ScanDirectoryEntries,
                fileCheckResult.Lr2ScanNormalFolderDirectoryEntries),
            Lr2FolderFileDiscoveryComplete = fileDiffCandidates.DiscoveryComplete,
            Lr2RootPath = Settings.Default.LR2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            Lr2RootCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
        };
        PrepareLr2FolderParentDirectoryEntrySurface(request);
        long parentSurfaceMs = RestartElapsed(stopwatchStage);
        IReadOnlyList<string> extraTextMetadataSourceDirectories = CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
            request.Lr2FolderDiscoveryDirectories,
            roots);
        long extraTextRootsMs = RestartElapsed(stopwatchStage);
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
            extraTextMetadataSourceDirectories,
            request.DirectoryEntries.Keys);
        long textMetadataMs = RestartElapsed(stopwatchStage);
        ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, extraTextMetadataSourceDirectories);
        stopwatchPrepare.Stop();
        return new Lr2FolderFileDiffPreparationResult(
            request,
            appManagedOutputScope.Directories,
            appManagedOutputScope.FilePaths,
            appManagedOutputScope.PruneExcludedPaths,
            appManagedCandidateCount,
            rootsMs,
            builtinSourceMs,
            appManagedScopeMs,
            filterMs,
            parentSurfaceMs,
            extraTextRootsMs,
            textMetadataMs,
            stopwatchPrepare.ElapsedMilliseconds);
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

        List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories();
        Lr2FolderFileCandidateSnapshot candidates = builtinSourceDirectories.Count > 0
            ? CreateLr2SongDbSyncLr2FolderFileCandidates(
                builtinSourceDirectories,
                Settings.Default.LR2RootPath,
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
            Lr2RootPath = Settings.Default.LR2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            Lr2RootCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
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

    private static bool ShouldPruneLr2FolderFileRowsDuringFileDiff(string reason)
    {
        return true;
    }

    private static void PrepareLr2FolderParentDirectoryEntrySurface(Lr2SongDbSyncRequest request)
    {
        if (request == null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var stopwatchStage = Stopwatch.StartNew();
        IReadOnlyCollection<string> parentDirectoryTargets = CreateLr2FolderPhysicalParentDirectoryMetadataTargets(
            (request.Lr2FolderFilePaths ?? []).Concat(request.Lr2FolderFileEntries?.Keys ?? []),
            request.RootDirectories,
            request.Lr2NormalCustomFolderOutputBaseDir,
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
                Volatile.Read(ref bmsStorageRowsVersion),
                Volatile.Read(ref bmsonStorageRowsVersion));
            lr2SongDbSyncScanSurfaceSnapshot = snapshot;
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

        ChartInfoOwnerVersionSnapshot version = CaptureChartInfoOwnerVersionSnapshot();
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
                    CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(snapshot.RootDirectories));
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

    private Lr2SongDbSyncScanSurfaceSnapshot GetCurrentLr2SongDbSyncScanSurface(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> lr2FolderDiscoveryDirectories,
        int ownedCollectionVersion,
        StorageRowsVersionSnapshot storageRowsVersion,
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
        if (snapshot.OwnedCollectionVersion != ownedCollectionVersion)
        {
            missReason = "owned_collection_version";
            return null;
        }
        if (snapshot.BmsRowsVersion != storageRowsVersion.BmsRowsVersion)
        {
            missReason = "bms_rows_version";
            return null;
        }
        if (snapshot.BmsonRowsVersion != storageRowsVersion.BmsonRowsVersion)
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
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        Lr2SongDbSyncStatusSnapshot status;
        using (LR2SongDBExtended songDb = dbGateway.OpenSongDb())
        {
            status = Lr2SongDbSyncStatusService.Evaluate(songDb, enabled, signature, DateTime.UtcNow);
        }
        PublishLr2SongDbSyncStatus(status);

        LogInstallPerformance("lr2_song_db_sync_status evaluate reason=" + (reason ?? "unknown")
            + " enabled=" + enabled.ToString().ToLowerInvariant()
            + " force=" + force.ToString().ToLowerInvariant()
            + " status=" + status.Status
            + " storedStatus=" + (status.StoredStatus?.ToString() ?? "(none)")
            + " signature=" + (status.Signature ?? string.Empty));

        if (!enabled
            || (!force && !status.IsNeeded)
            || (!force
                && !allowIncompleteToQueue
                && (status.Status == Lr2SongDbSyncStatusKind.Incomplete
                    || status.StoredStatus == Lr2SongDbSyncStatusKind.Incomplete)))
        {
            ClearLr2SongDbSyncPreparedDataSurface("queue_not_needed");
            return status;
        }

        bool prepareReserved = false;
        lock (lockLr2SongDbSync)
        {
            if (_Lr2SongDbSyncRunning || lr2SongDbSyncPrepareInProgress)
            {
                LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                    + " status=" + status.Status
                    + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                    + " requestedVersion=" + Lr2SongDbSyncRequestedVersion
                    + " preparing=" + lr2SongDbSyncPrepareInProgress.ToString().ToLowerInvariant());
                status.Status = Lr2SongDbSyncStatusKind.Running;
                status.Stage = Lr2SongDbSyncStage;
                status.ProcessedCursor = Lr2SongDbSyncProcessedCount;
                status.TotalCount = Lr2SongDbSyncTotalCount;
                status.StageProcessedCount = Lr2SongDbSyncStageProcessedCount;
                status.StageTotalCount = Lr2SongDbSyncStageTotalCount;
                PublishLr2SongDbSyncStatus(status);
                return status;
            }
            if (prepareGeneratedData != null)
            {
                lr2SongDbSyncPrepareInProgress = true;
                prepareReserved = true;
            }
        }

        if (prepareGeneratedData != null)
        {
            var prepareStopwatch = Stopwatch.StartNew();
            try
            {
                LogInstallPerformance("lr2_song_db_sync prepare_start"
                    + " reason=" + (reason ?? "unknown"));
                Lr2SongDbSyncPreparedDataSurface preparedSurface = prepareGeneratedData();
                prepareStopwatch.Stop();
                LogInstallPerformance("lr2_song_db_sync prepare_done"
                    + " reason=" + (reason ?? "unknown")
                    + " scopeDirs=" + (preparedSurface?.Lr2FolderScopeDirectories?.Count ?? 0)
                    + " lr2FolderCandidates=" + (preparedSurface?.Lr2FolderFilePaths?.Count ?? 0)
                    + " directoryEntries=" + (preparedSurface?.DirectoryEntries?.Count ?? 0)
                    + " folderInfoCandidates=" + (preparedSurface?.FolderInfoFilePaths?.Count ?? 0)
                    + " textFileDirs=" + (preparedSurface?.TextFileDirectories?.Count ?? 0)
                    + " discoveryComplete=" + (preparedSurface?.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant() ?? "true")
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds);
                ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", preparedSurface);
            }
            catch (Exception ex)
            {
                prepareStopwatch.Stop();
                ClearLr2SongDbSyncPreparedDataSurface("prepare_failed");
                LogInstallPerformance("lr2_song_db_sync prepare_failed reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds
                    + " message=" + ex.Message);
                if (prepareReserved)
                {
                    ClearLr2SongDbSyncPrepareReservation();
                    prepareReserved = false;
                }
                throw;
            }
        }

        if (!TryBeginLr2SongDbSyncRequest(out int requestVersion))
        {
            ClearLr2SongDbSyncPreparedDataSurface("queue_skipped");
            if (prepareReserved)
            {
                ClearLr2SongDbSyncPrepareReservation();
                prepareReserved = false;
            }
            LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                + " status=" + status.Status
                + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                + " requestedVersion=" + Lr2SongDbSyncRequestedVersion);
            status.Status = Lr2SongDbSyncStatusKind.Running;
            status.Stage = Lr2SongDbSyncStage;
            status.ProcessedCursor = Lr2SongDbSyncProcessedCount;
            status.TotalCount = Lr2SongDbSyncTotalCount;
            status.StageProcessedCount = Lr2SongDbSyncStageProcessedCount;
            status.StageTotalCount = Lr2SongDbSyncStageTotalCount;
            PublishLr2SongDbSyncStatus(status);
            return status;
        }
        if (prepareReserved)
        {
            ClearLr2SongDbSyncPrepareReservation();
            prepareReserved = false;
        }

        PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            signature,
            stage: "queued",
            processedCursor: 0,
            totalCount: 0,
            lastError: null,
            stageProcessedCount: 0,
            stageTotalCount: 0));
        Task work()
        {
            RunLr2SongDbSync(reason, signature, requestVersion);
            return Task.CompletedTask;
        }
        if (StartupBackgroundTaskScheduler != null
            && StartupBackgroundTaskScheduler("lr2_song_db_sync", reason ?? "queue", null, work))
        {
            return status;
        }
        Task.Run(() => RunLr2SongDbSync(reason, signature, requestVersion)).Logging("Lr2SongDbSync");
        return status;
    }

    internal bool TryRunLr2SongDbSyncDataPreparation(string reason, Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData)
    {
        if (prepareGeneratedData == null)
        {
            return false;
        }

        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return false;
        }

        lock (lockLr2SongDbSync)
        {
            if (_Lr2SongDbSyncRunning || lr2SongDbSyncPrepareInProgress)
            {
                LogInstallPerformance("lr2_song_db_sync_data_prepare skipped reason=" + (reason ?? "unknown")
                    + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                    + " requestedVersion=" + Lr2SongDbSyncRequestedVersion
                    + " preparing=" + lr2SongDbSyncPrepareInProgress.ToString().ToLowerInvariant());
                return false;
            }
            lr2SongDbSyncPrepareInProgress = true;
        }

        try
        {
            LogInstallPerformance("lr2_song_db_sync_data_prepare start reason=" + (reason ?? "unknown"));
            Lr2SongDbSyncPreparedDataSurface preparedSurface = prepareGeneratedData();
            ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", preparedSurface);
            LogInstallPerformance("lr2_song_db_sync_data_prepare done reason=" + (reason ?? "unknown"));
            return true;
        }
        catch (Exception ex)
        {
            LogInstallPerformance("lr2_song_db_sync_data_prepare failed reason=" + (reason ?? "unknown")
                + " message=" + ex.Message);
            throw;
        }
        finally
        {
            ClearLr2SongDbSyncPrepareReservation();
        }
    }

    internal Lr2StartupScanBlockerCleanupResult CleanupLr2SongDbSyncStartupScanBlockerFolderRows(string reason)
    {
        if (Lr2SongDbSyncRunning)
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }

        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return null;
        }

        Lr2SongDbSyncInput input = CreateLr2SongDbSyncInput();
        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        Lr2StartupScanBlockerCleanupResult result;
        Lr2SongDbSyncStatusSnapshot status;
        using (LR2SongDBExtended songDb = dbGateway.OpenSongDb())
        {
            result = Lr2SongDbSyncService.CleanupStartupScanBlockerFolderRows(
                songDb,
                input.RootDirectories,
                input.Lr2FolderDiscoveryDirectories,
                input.SongRows,
                input.Lr2RootPath);
            status = Lr2SongDbSyncStatusService.Evaluate(songDb, enabled, signature, DateTime.UtcNow);
        }

        LogInstallPerformance("lr2_song_db_sync_startup_scan_blocker_cleanup reason=" + (reason ?? "unknown")
            + " deletedFolderRows=" + (result?.DeletedFolderRowCount ?? 0)
            + " beforeBlockers=" + (result?.DiagnosticBefore?.TotalBlockerCount ?? 0)
            + " beforeCleanupFolderRows=" + (result?.DiagnosticBefore?.CleanupFolderRowCount ?? 0)
            + " afterBlockers=" + (result?.DiagnosticAfter?.TotalBlockerCount ?? 0)
            + " signature=" + signature);
        PublishLr2SongDbSyncStatus(status);
        return result;
    }

    internal void PublishLr2SongDbSyncExternalStageProgress(string stage, int processedCount, int totalCount, string detail = null)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return;
        }

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        int safeTotal = Math.Max(0, totalCount);
        int safeProcessed = Math.Max(0, Math.Min(Math.Max(0, processedCount), safeTotal));
        PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            signature,
            stage: stage,
            processedCursor: safeProcessed,
            totalCount: safeTotal,
            lastError: detail,
            stageProcessedCount: safeProcessed,
            stageTotalCount: safeTotal));
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
            if (_Lr2SongDbSyncRunning)
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
            dialogService.Show(
                Resources.Warn_Lr2SongDbSyncRunning,
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }
        return true;
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
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        CancellationToken cancellationToken;
        bool enteredSyncService = false;
        lock (lockLr2SongDbSync)
        {
            cancellationToken = lr2SongDbSyncCancellation?.Token ?? CancellationToken.None;
        }
        try
        {
            ReportStartupBackgroundTask("lr2_song_db_sync", "start", 0L, failed: false, detail: "runId=" + runId);
            PublishLr2SongDbSyncPreflightStage("chart_info_hydration", reason, runId);
            var preflightStageStopwatch = Stopwatch.StartNew();
            EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);
            LogLr2SongDbSyncPreflightStageDone("chart_info_hydration", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            PublishLr2SongDbSyncPreflightStage("input_surface", reason, runId);
            preflightStageStopwatch.Restart();
            Lr2SongDbSyncInput input = CreateLr2SongDbSyncInput();
            LogLr2SongDbSyncPreflightStageDone("input_surface", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            PublishLr2SongDbSyncPreflightStage("compatibility_projection_index", reason, runId);
            preflightStageStopwatch.Restart();
            Dictionary<string, BMSFile> compatibilityProjectionIndex = CreateLr2SongDbSyncCompatibilityProjectionIndex();
            LogLr2SongDbSyncPreflightStageDone("compatibility_projection_index", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            PublishLr2SongDbSyncPreflightStage("chart_info_resolver_snapshot", reason, runId);
            preflightStageStopwatch.Restart();
            Func<BMSFile, LR2SongDBExtended.chart_info> chartInfoResolver = CreateLr2SongDbSyncChartInfoResolverSnapshot();
            HashSet<string> currentChartInfoParseFailureMd5s = CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);
            LogLr2SongDbSyncPreflightStageDone("chart_info_resolver_snapshot", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            int projectedCompatibilityWarningCount = 0;
            int committedChartInfoRowCount = 0;
            int committedChartInfoParseFailureChangeCount = 0;
            Lr2SongDbSyncResult result;
            using (LR2SongDBExtended songDb = dbGateway.OpenSongDb())
            {
                enteredSyncService = true;
                result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
                {
                    Signature = signature,
                    RunId = runId,
                    RootDirectories = input.RootDirectories,
                    ChartPaths = input.ChartPaths,
                    NormalFolderDirectoryPaths = input.NormalFolderDirectoryPaths,
                    FolderInfoFilePaths = input.FolderInfoFilePaths,
                    FolderInfoFileEntries = input.FolderInfoFileEntries,
                    DirectoryEntries = input.DirectoryEntries,
                    Lr2FolderFilePaths = input.Lr2FolderFilePaths,
                    Lr2FolderFileEntries = input.Lr2FolderFileEntries,
                    Lr2FolderDiscoveryDirectories = input.Lr2FolderDiscoveryDirectories,
                    Lr2FolderPruneDirectories = input.Lr2FolderPruneDirectories,
                    Lr2FolderPruneExcludedPaths = input.Lr2FolderPruneExcludedPaths,
                    Lr2RootPath = input.Lr2RootPath,
                    Lr2NormalCustomFolderOutputBaseDir = input.Lr2NormalCustomFolderOutputBaseDir,
                    Lr2RootCustomFolderOutputBaseDir = input.Lr2RootCustomFolderOutputBaseDir,
                    Lr2BuiltinFolderSourceDirectories = input.Lr2BuiltinFolderSourceDirectories,
                    Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete,
                    SongRows = input.SongRows,
                    TextFileDirectories = input.TextFileDirectories,
                    ChartInfoResolver = chartInfoResolver,
                    ChartInfoResolverIsThreadSafe = true,
                    ChartInfoParseTimeout = chartInfoBuildService.CurrentParseTimeout,
                    CurrentChartInfoParseFailureMd5s = currentChartInfoParseFailureMd5s,
                    ChartInfoRowsCommitted = rows =>
                    {
                        int count = rows?.Count ?? 0;
                        if (count > 0)
                        {
                            UpsertChartInfoIndexRows(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);
                            Interlocked.Add(ref committedChartInfoRowCount, count);
                        }
                    },
                    ChartInfoParseFailuresCommitted = (persisted, cleared) =>
                    {
                        int count = Math.Max(0, persisted) + Math.Max(0, cleared);
                        if (count > 0)
                        {
                            Interlocked.Add(ref committedChartInfoParseFailureChangeCount, count);
                        }
                    },
                    StartedAtUtc = DateTime.UtcNow,
                    CancellationToken = cancellationToken,
                    IsSourceCurrent = () => IsLr2SongDbSyncInputCurrent(input),
                    ProgressReporter = UpdateLr2SongDbSyncProgress,
                    Lr2CompatibilityFactsCommitted = infos =>
                    {
                        int applied = ApplyLr2SongDbSyncCompatibilityProjection(
                            infos,
                            reason,
                            compatibilityProjectionIndex,
                            logSummary: false,
                            dispatchPresentation: false);
                        if (applied > 0)
                        {
                            Interlocked.Add(ref projectedCompatibilityWarningCount, applied);
                        }
                    },
                    TransientSongRowsSkipPaths = GetLr2SongDbSyncTransientSongRowsSkipPaths(input, reason),
                    SongRowsSkipVerifier = (songRowsSongDb, songRows) =>
                        VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
                            songRowsSongDb,
                            songRows,
                            input,
                            reason),
                    LogInstallPerformance = LogInstallPerformance
                });
            }
            stopwatch.Stop();
            bool completed = string.Equals(result.FinalStage, Lr2SongDbSyncService.CompletedStage, StringComparison.Ordinal);
            LogInstallPerformance("lr2_song_db_sync " + (completed ? "completed" : "incomplete")
                + " reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " roots=" + input.RootDirectories.Count
                + " charts=" + input.ChartPaths.Count
                + " normalFolderDirs=" + input.NormalFolderDirectoryPaths.Count
                + " folderInfoCandidates=" + input.FolderInfoFilePaths.Count
                + " lr2FolderRoots=" + input.Lr2FolderDiscoveryDirectories.Count
                + " lr2FolderPruneRoots=" + input.Lr2FolderPruneDirectories.Count
                + " lr2FolderCandidates=" + input.Lr2FolderFilePaths.Count
                + " lr2FolderDiscoveryComplete=" + input.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " textFileDirs=" + input.TextFileDirectories.Count
                + " songRows=" + input.SongRows.Count
                + " normalFolderGenerated=" + (result.NormalFolderSyncResult?.GeneratedCount ?? 0)
                + " normalFolderUpserted=" + (result.NormalFolderSyncResult?.UpsertedCount ?? 0)
                + " normalFolderDeleted=" + (result.NormalFolderSyncResult?.DeletedCount ?? 0)
                + " normalFolderSkippedUnsupported=" + (result.NormalFolderSyncResult?.SkippedUnsupportedPathCount ?? 0)
                + " normalFolderSkippedMissingMetadata=" + (result.NormalFolderSyncResult?.SkippedMissingMetadataCount ?? 0)
                + " normalFolderSkippedIncompatibleChart=" + (result.NormalFolderSyncResult?.SkippedIncompatibleChartPathCount ?? 0)
                + " normalFolderTargetBuildMs=" + (result.NormalFolderSyncResult?.TargetBuildMs ?? 0)
                + " normalFolderMetadataBuildMs=" + (result.NormalFolderSyncResult?.MetadataBuildMs ?? 0)
                + " normalFolderExistingReadMs=" + (result.NormalFolderSyncResult?.ExistingReadMs ?? 0)
                + " normalFolderRowGenerateMs=" + (result.NormalFolderSyncResult?.RowGenerateMs ?? 0)
                + " normalFolderPlanMs=" + (result.NormalFolderSyncResult?.PlanMs ?? 0)
                + " normalFolderWriteMs=" + (result.NormalFolderSyncResult?.WriteMs ?? 0)
                + " lr2FolderGenerated=" + (result.Lr2FolderFileSyncResult?.GeneratedCount ?? 0)
                + " lr2FolderUpserted=" + (result.Lr2FolderFileSyncResult?.UpsertedCount ?? 0)
                + " lr2FolderDeleted=" + (result.Lr2FolderFileSyncResult?.DeletedCount ?? 0)
                + " lr2FolderExistingRows=" + (result.Lr2FolderFileSyncResult?.ExistingReadCount ?? 0)
                + " lr2FolderSkippedUnsupported=" + (result.Lr2FolderFileSyncResult?.SkippedUnsupportedPathCount ?? 0)
                + " lr2FolderSkippedMissingMetadata=" + (result.Lr2FolderFileSyncResult?.SkippedMissingMetadataCount ?? 0)
                + " lr2FolderProcessed=" + result.Lr2FolderFileProcessedCount
                + " songRowProcessed=" + result.SongRowProcessedCount
                + " songRowSkipped=" + result.SongRowSkippedCount
                + " songRowParseFailed=" + result.SongRowParseFailureCount
                + " songRowChartInfoApplied=" + result.SongRowChartInfoAppliedCount
                + " songRowLr2CompatibilityApplied=" + result.SongRowLr2CompatibilityAppliedCount
                + " staleSongRowsPruned=" + result.StaleSongRowPrunedCount
                + " processed=" + result.ProcessedCount
                + " total=" + result.TotalCount
                + " startupScanBlockers=" + (result.StartupScanDiagnosticResult?.TotalBlockerCount ?? 0)
                + " startupScanNoRootSet=" + (result.StartupScanDiagnosticResult?.NoRootSetBlockerCount ?? 0)
                + " startupScanMissingSongRows=" + (result.StartupScanDiagnosticResult?.MissingCurrentSongRowCount ?? 0)
                + " startupScanDateMissingSongRows=" + (result.StartupScanDiagnosticResult?.DateMissingSongRowCount ?? 0)
                + " startupScanUnknownRootSongRows=" + (result.StartupScanDiagnosticResult?.UnknownRootSongRowCount ?? 0)
                + " startupScanDateMissingFolderRows=" + (result.StartupScanDiagnosticResult?.DateMissingFolderRowCount ?? 0)
                + " startupScanDateStaleFolderRows=" + (result.StartupScanDiagnosticResult?.DateStaleFolderRowCount ?? 0)
                + " startupScanUnknownRootFolderRows=" + (result.StartupScanDiagnosticResult?.UnknownRootFolderRowCount ?? 0)
                + " startupScanCleanupFolderRows=" + (result.StartupScanDiagnosticResult?.CleanupFolderRowCount ?? 0)
                + " startupScanFolderDateUpdates=" + (result.StartupScanDiagnosticResult?.FolderDateUpdateCount ?? 0)
                + " stage=" + result.FinalStage
                + " detail=" + (result.IncompleteReason ?? "completed")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            LogInstallPerformance("lr2_song_db_sync_compatibility_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + result.SongRowLr2CompatibilityAppliedCount
                + " applied=" + projectedCompatibilityWarningCount);
            LogInstallPerformance("lr2_song_db_sync_chart_info_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " rows=" + committedChartInfoRowCount
                + " parseFailureChanges=" + committedChartInfoParseFailureChangeCount);
            if (committedChartInfoRowCount > 0 || committedChartInfoParseFailureChangeCount > 0)
            {
                DispatchWarningPresentationChanged("lr2_song_db_sync_inline_chart_info");
            }
            if (projectedCompatibilityWarningCount > 0)
            {
                DispatchWarningPresentationChanged("lr2_song_db_sync_compatibility_projection");
            }
            if (completed)
            {
                CompleteLr2SongDbSyncRequest(requestVersion, result.FinalStage);
            }
            else
            {
                FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Incomplete, result.FinalStage, BuildLr2SongDbSyncIncompleteDetail(result));
            }
            ReportStartupBackgroundTask("lr2_song_db_sync", completed ? "done" : "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: completed ? "completed" : BuildLr2SongDbSyncIncompleteDetail(result));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (!enteredSyncService)
            {
                MarkLr2SongDbSyncPreflightCancelled(signature, runId, Lr2SongDbSyncStage);
            }
            LogInstallPerformance("lr2_song_db_sync cancelled reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " stage=" + (Lr2SongDbSyncStage ?? string.Empty)
                + " processed=" + Lr2SongDbSyncProcessedCount
                + " total=" + Lr2SongDbSyncTotalCount);
            FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Cancelled, Lr2SongDbSyncStage, string.Empty);
            ReportStartupBackgroundTask("lr2_song_db_sync", "cancelled", stopwatch.ElapsedMilliseconds, failed: false, detail: "cancelled");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            try
            {
                using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
                Lr2SongDbSyncStatusService.MarkFailed(
                    songDb,
                    signature,
                    runId,
                    processedCursor: null,
                    totalCount: null,
                    stage: "failed",
                    error: ex.Message,
                    nowUtc: DateTime.UtcNow);
            }
            catch
            {
                // Preserve the original failure in the startup task report.
            }
            LogInstallPerformance("lr2_song_db_sync failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + ex.Message);
            FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, "failed", ex.Message);
            ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
        }
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

    private static string BuildLr2SongDbSyncIncompleteDetail(Lr2SongDbSyncResult result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        string reason = result.IncompleteReason ?? result.FinalStage ?? string.Empty;
        if (string.Equals(result.FinalStage, Lr2SongDbSyncService.StartupScanBlockersStage, StringComparison.Ordinal)
            && result.StartupScanDiagnosticResult != null)
        {
            return reason + " " + result.StartupScanDiagnosticResult.ToLogDetail();
        }
        return reason;
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
        WaitForChartInfoHydrationIdle();
        lock (lockChartInfoIndex)
        {
            if (_ChartInfoIndexHydrated)
            {
                return;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        ChartInfoHydrationResult result = HydrateChartInfos("lr2_song_db_sync_" + (string.IsNullOrWhiteSpace(reason) ? "sync" : reason));
        stopwatch.Stop();
        LogInstallPerformance("lr2_song_db_sync_chart_info_hydration ensured"
            + " reason=" + (reason ?? "unknown")
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds
            + " succeeded=" + result.Succeeded.ToString().ToLowerInvariant()
            + " totalRows=" + result.TotalRows
            + " dbLoadMs=" + result.DbLoadMs
            + " indexBuildMs=" + result.IndexBuildMs);
    }

    private HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures =
                dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout);
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
        List<string> chartPaths;
        List<BMSFile> songRows;
        int ownedCollectionVersion;
        StorageRowsVersionSnapshot storageRowsVersion;
        var rowSnapshotStopwatch = Stopwatch.StartNew();
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
        rowSnapshotStopwatch.Stop();

        var rootsStopwatch = Stopwatch.StartNew();
        DateTime nowUtc = DateTime.UtcNow;
        List<string> roots = getBMSDirectories();
        List<string> lr2FolderDiscoveryDirectories = CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots);
        string lr2RootPath = Settings.Default.LR2RootPath;
        rootsStopwatch.Stop();

        var builtinSettingsStopwatch = Stopwatch.StartNew();
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings = CreateLr2BuiltinCustomFolderSettings(songRows, nowUtc);
        List<string> lr2BuiltinFolderSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories();
        string lr2NormalCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string lr2RootCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        List<string> lr2FolderPruneDirectories = CreateLr2SongDbSyncLr2FolderPruneDirectories(
            roots,
            lr2BuiltinFolderSourceDirectories);
        builtinSettingsStopwatch.Stop();

        var scanSurfaceStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = GetCurrentLr2SongDbSyncScanSurface(
            roots,
            lr2FolderDiscoveryDirectories,
            ownedCollectionVersion,
            storageRowsVersion,
            out string scanSurfaceMissReason);
        scanSurfaceStopwatch.Stop();

        var directoryTargetsStopwatch = Stopwatch.StartNew();
        IReadOnlyCollection<string> directoryMetadataTargets = scanSurface?.NormalFolderDirectoryPaths?.Count > 0
            ? scanSurface.NormalFolderDirectoryPaths
            : Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, chartPaths);
        directoryTargetsStopwatch.Stop();

        Lr2SongDbSyncPreparedDataSurface pendingPreparedSurface =
            TakeLr2SongDbSyncPreparedDataSurface(out int preparedSurfaceAppliedScanGeneration);
        bool preparedSurfaceAlreadyAppliedToScanSurface = scanSurface != null
            && pendingPreparedSurface?.HasPreparedDataSurface == true
            && preparedSurfaceAppliedScanGeneration == scanSurface.Generation;
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceAlreadyAppliedToScanSurface
            ? Lr2SongDbSyncPreparedDataSurface.Empty
            : pendingPreparedSurface;
        bool hasPreparedSurface = preparedSurface?.HasPreparedDataSurface == true;
        bool hasPreparedLr2FolderSurface = preparedSurface?.HasLr2FolderSurface == true;
        bool reusedLr2FolderSurface = scanSurface?.Lr2FolderFileDiscoveryComplete == true;
        string lr2FolderCandidatesSource;
        var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates;
        int enumeratedAppManagedCandidateCount = 0;
        int enumeratedAppManagedExactFileCount = 0;
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
        enumeratedAppManagedExactFileCount = appManagedOutputScope.FilePaths.Count;
        if (scanSurface != null)
        {
            if (!appManagedOutputScope.IsComplete)
            {
                lr2FolderCandidatesSource = hasPreparedLr2FolderSurface
                    ? "scan_surface_prepared_only_app_managed_incomplete"
                    : "scan_surface_app_managed_incomplete";
                lr2FolderFileCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
                if (hasPreparedLr2FolderSurface)
                {
                    lr2FolderFileCandidates = MergeLr2FolderFileCandidateSurface(lr2FolderFileCandidates, preparedSurface);
                }
            }
            else
            {
                lr2FolderCandidatesSource = hasPreparedLr2FolderSurface
                    ? "scan_surface_prepared_merge"
                    : "scan_surface_direct";
                lr2FolderFileCandidates = new Lr2FolderFileCandidateSnapshot(
                    scanSurface.Lr2FolderFilePaths,
                    scanSurface.Lr2FolderFileEntries,
                    scanSurface.Lr2FolderFileDiscoveryComplete);
                if (hasPreparedLr2FolderSurface)
                {
                    lr2FolderFileCandidates = MergeLr2FolderFileCandidateSurface(lr2FolderFileCandidates, preparedSurface);
                }
                if (preparedSurface?.Lr2FolderFileDiscoveryComplete == false)
                {
                    lr2FolderFileCandidates = new Lr2FolderFileCandidateSnapshot(
                        lr2FolderFileCandidates.Paths,
                        lr2FolderFileCandidates.EntriesByPath,
                        discoveryComplete: false);
                }
            }
        }
        else
        {
            if (!appManagedOutputScope.IsComplete)
            {
                lr2FolderCandidatesSource = hasPreparedLr2FolderSurface
                    ? "enumeration_prepared_only_app_managed_incomplete"
                    : "enumeration_app_managed_incomplete";
                lr2FolderFileCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
            }
            else
            {
                lr2FolderCandidatesSource = hasPreparedLr2FolderSurface
                    ? "enumeration_prepared_merge"
                    : "enumeration";
                lr2FolderFileCandidates = CreateLr2SongDbSyncLr2FolderFileCandidates(
                    CreateLr2FolderDiscoveryDirectoriesForEnumeration(lr2FolderDiscoveryDirectories, preparedSurface),
                    lr2RootPath,
                    builtinCustomFolderSettings);
                lr2FolderFileCandidates =
                    Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                        lr2FolderFileCandidates.Paths,
                        lr2FolderFileCandidates.EntriesByPath,
                        appManagedOutputScope.FilePaths,
                        lr2FolderFileCandidates.DiscoveryComplete,
                        out enumeratedAppManagedCandidateCount,
                        appManagedOutputScope.Directories);
                if (enumeratedAppManagedCandidateCount > 0)
                {
                    lr2FolderCandidatesSource += "_app_managed_filtered";
                }
            }
            if (hasPreparedLr2FolderSurface)
            {
                lr2FolderFileCandidates = MergeLr2FolderFileCandidateSurface(lr2FolderFileCandidates, preparedSurface);
            }
        }
        lr2FolderCandidatesStopwatch.Stop();
        IReadOnlyCollection<string> lr2FolderParentDirectoryTargets = CreateLr2FolderPhysicalParentDirectoryMetadataTargets(
            lr2FolderFileCandidates.Paths,
            roots,
            lr2NormalCustomFolderOutputBaseDir,
            lr2RootCustomFolderOutputBaseDir,
            lr2BuiltinFolderSourceDirectories);
        IReadOnlyCollection<string> directoryEntryTargets = MergeLr2DirectoryMetadataTargets(
            directoryMetadataTargets,
            lr2FolderParentDirectoryTargets);
        Lr2TextMetadataCandidateSnapshot textMetadataCandidates = null;
        var folderInfoCandidatesStopwatch = Stopwatch.StartNew();
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates;
        if (scanSurface != null)
        {
            folderInfoCandidates = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
                scanSurface.FolderInfoFilePaths,
                scanSurface.FolderInfoFileEntries.Values,
                directoryEntryTargets);
        }
        else
        {
            textMetadataCandidates = CreateLr2SongDbSyncTextMetadataCandidates(lr2FolderDiscoveryDirectories, directoryEntryTargets);
            folderInfoCandidates = textMetadataCandidates.FolderInfoCandidates;
        }
        if (hasPreparedSurface && preparedSurface.FolderInfoFilePaths.Count > 0)
        {
            IReadOnlyList<string> folderInfoPaths = MergePreparedFileSurface(
                folderInfoCandidates.Paths,
                folderInfoCandidates.EntriesByPath,
                preparedSurface.FolderInfoFilePaths,
                preparedSurface.FolderInfoFileEntries,
                preparedSurface.Lr2FolderScopeDirectories,
                out IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoEntries);
            folderInfoCandidates = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
                folderInfoPaths,
                folderInfoEntries.Values,
                directoryEntryTargets);
        }
        folderInfoCandidatesStopwatch.Stop();
        var directoryEntriesStopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = scanSurface != null
            ? CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
                OverlayLr2DirectoryEntrySurface(
                    MergeMissingLr2DirectoryEntrySurface(scanSurface.DirectoryEntries, scanSurface.NormalFolderDirectoryEntries),
                    preparedSurface.DirectoryEntries),
                lr2FolderDiscoveryDirectories,
                directoryEntryTargets)
            : OverlayLr2DirectoryEntrySurface(
                CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(
                    lr2FolderDiscoveryDirectories,
                    directoryEntryTargets),
                preparedSurface.DirectoryEntries);
        directoryEntriesStopwatch.Stop();
        int missingDirectoryEntries = Math.Max(0, directoryEntryTargets.Count - directoryEntries.Count);
        string textFileDirsSource;
        var textFileDirsStopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> textFileDirectories;
        if (scanSurface?.TextFileDirectories != null)
        {
            textFileDirsSource = hasPreparedSurface
                ? "scan_surface_prepared_merge"
                : "scan_surface_direct";
            textFileDirectories = hasPreparedSurface
                ? MergePreparedDirectoryList(
                    scanSurface.TextFileDirectories,
                    preparedSurface.TextFileDirectories,
                    preparedSurface.Lr2FolderScopeDirectories)
                : scanSurface.TextFileDirectories;
        }
        else if (textMetadataCandidates?.TextFileDirectories != null)
        {
            textFileDirsSource = hasPreparedSurface
                ? "enumeration_prepared_merge"
                : "enumeration";
            textFileDirectories = hasPreparedSurface
                ? MergePreparedDirectoryList(
                    textMetadataCandidates.TextFileDirectories,
                    preparedSurface.TextFileDirectories,
                    preparedSurface.Lr2FolderScopeDirectories)
                : textMetadataCandidates.TextFileDirectories;
        }
        else
        {
            textFileDirsSource = hasPreparedSurface
                ? "prepared_only"
                : "empty";
            textFileDirectories = hasPreparedSurface
                ? MergePreparedDirectoryList(
                    [],
                    preparedSurface.TextFileDirectories,
                    preparedSurface.Lr2FolderScopeDirectories)
                : [];
        }
        textFileDirsStopwatch.Stop();
        inputStopwatch.Stop();
        LogInstallPerformance("lr2_song_db_sync_input_surface"
            + " reusedScanSurface=" + (scanSurface != null).ToString().ToLowerInvariant()
            + " scanSurfaceMissReason=" + (scanSurfaceMissReason ?? string.Empty)
            + " scanSurfaceGeneration=" + (scanSurface?.Generation ?? 0)
            + " rootDirs=" + roots.Count
            + " lr2FolderDiscoveryDirs=" + lr2FolderDiscoveryDirectories.Count
            + " lr2BuiltinFolderSourceDirs=" + lr2BuiltinFolderSourceDirectories.Count
            + " lr2FolderPruneDirs=" + lr2FolderPruneDirectories.Count
            + " scanSurfaceNormalFolderDirs=" + (scanSurface?.NormalFolderDirectoryPaths?.Count ?? 0)
            + " normalDirectoryTargets=" + directoryMetadataTargets.Count
            + " lr2FolderParentDirectoryTargets=" + lr2FolderParentDirectoryTargets.Count
            + " directoryTargets=" + directoryEntryTargets.Count
            + " directoryEntries=" + directoryEntries.Count
            + " missingDirectoryEntries=" + missingDirectoryEntries
            + " directoryEntriesMs=" + directoryEntriesStopwatch.ElapsedMilliseconds
            + " folderInfoCandidates=" + folderInfoCandidates.Paths.Count
            + " lr2FolderCandidates=" + lr2FolderFileCandidates.Paths.Count
            + " enumeratedAppManagedFiltered=" + enumeratedAppManagedCandidateCount
            + " enumeratedAppManagedExactFiles=" + enumeratedAppManagedExactFileCount
            + " reusedLr2FolderSurface=" + reusedLr2FolderSurface.ToString().ToLowerInvariant()
            + " hasPreparedSurface=" + hasPreparedSurface.ToString().ToLowerInvariant()
            + " hasPreparedLr2FolderSurface=" + hasPreparedLr2FolderSurface.ToString().ToLowerInvariant()
            + " preparedSurfaceAlreadyAppliedToScanSurface=" + preparedSurfaceAlreadyAppliedToScanSurface.ToString().ToLowerInvariant()
            + " preparedSurfaceAppliedScanGeneration=" + preparedSurfaceAppliedScanGeneration
            + " pendingPreparedScopeDirs=" + (pendingPreparedSurface?.Lr2FolderScopeDirectories?.Count ?? 0)
            + " pendingPreparedLr2FolderCandidates=" + (pendingPreparedSurface?.Lr2FolderFilePaths?.Count ?? 0)
            + " pendingPreparedDirectoryEntries=" + (pendingPreparedSurface?.DirectoryEntries?.Count ?? 0)
            + " pendingPreparedFolderInfoCandidates=" + (pendingPreparedSurface?.FolderInfoFilePaths?.Count ?? 0)
            + " pendingPreparedTextFileDirs=" + (pendingPreparedSurface?.TextFileDirectories?.Count ?? 0)
            + " preparedDirectoryEntries=" + (preparedSurface?.DirectoryEntries?.Count ?? 0)
            + " preparedFolderInfoCandidates=" + (preparedSurface?.FolderInfoFilePaths?.Count ?? 0)
            + " preparedTextFileDirs=" + (preparedSurface?.TextFileDirectories?.Count ?? 0)
            + " lr2FolderDiscoveryComplete=" + lr2FolderFileCandidates.DiscoveryComplete.ToString().ToLowerInvariant()
            + " textFileDirs=" + textFileDirectories.Count
            + " lr2FolderCandidatesSource=" + lr2FolderCandidatesSource
            + " textFileDirsSource=" + textFileDirsSource
            + " rowSnapshotMs=" + rowSnapshotStopwatch.ElapsedMilliseconds
            + " rootsMs=" + rootsStopwatch.ElapsedMilliseconds
            + " builtinSettingsMs=" + builtinSettingsStopwatch.ElapsedMilliseconds
            + " scanSurfaceLookupMs=" + scanSurfaceStopwatch.ElapsedMilliseconds
            + " directoryTargetsMs=" + directoryTargetsStopwatch.ElapsedMilliseconds
            + " lr2FolderCandidatesMs=" + lr2FolderCandidatesStopwatch.ElapsedMilliseconds
            + " folderInfoCandidatesMs=" + folderInfoCandidatesStopwatch.ElapsedMilliseconds
            + " textFileDirsMs=" + textFileDirsStopwatch.ElapsedMilliseconds
            + " totalMs=" + inputStopwatch.ElapsedMilliseconds);
        return new Lr2SongDbSyncInput(
            roots,
            chartPaths,
            [.. directoryMetadataTargets],
            folderInfoCandidates.Paths,
            folderInfoCandidates.EntriesByPath,
            directoryEntries,
            lr2FolderDiscoveryDirectories,
            lr2FolderPruneDirectories,
            appManagedOutputScope.PruneExcludedPaths,
            lr2RootPath,
            lr2NormalCustomFolderOutputBaseDir,
            lr2RootCustomFolderOutputBaseDir,
            lr2BuiltinFolderSourceDirectories,
            builtinCustomFolderSettings,
            lr2FolderFileCandidates.Paths,
            lr2FolderFileCandidates.EntriesByPath,
            lr2FolderFileCandidates.DiscoveryComplete,
            songRows,
            textFileDirectories,
            scanSurface?.Generation ?? 0,
            ownedCollectionVersion,
            storageRowsVersion.BmsRowsVersion,
            storageRowsVersion.BmsonRowsVersion);
    }

    private bool IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input)
    {
        if (input == null
            || OwnedChartCollectionVersion != input.OwnedChartCollectionVersion
            || Volatile.Read(ref bmsStorageRowsVersion) != input.BmsRowsVersion
            || Volatile.Read(ref bmsonStorageRowsVersion) != input.BmsonRowsVersion)
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

        List<string> lr2FolderDiscoveryDirectories = CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots);
        if (!ArePathSetsEqual(input.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
        {
            return false;
        }
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings = Lr2BuiltinCustomFolderSettings.Create(CreateCurrentLr2ConfigOrNull(), [], DateTime.UtcNow);
        if (!AreLr2BuiltinCustomFolderConfigurationEqual(input.Lr2BuiltinCustomFolderSettings, builtinCustomFolderSettings))
        {
            return false;
        }
        if (!string.Equals(SafeFullPathOrOriginal(input.Lr2RootPath), SafeFullPathOrOriginal(Settings.Default.LR2RootPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SafeFullPathOrOriginal(input.Lr2NormalCustomFolderOutputBaseDir), SafeFullPathOrOriginal(Settings.Default.LR2CustomFolderOutputBaseDir), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SafeFullPathOrOriginal(input.Lr2RootCustomFolderOutputBaseDir), SafeFullPathOrOriginal(Settings.Default.LR2CustomFolderOutputBaseDirRootType), StringComparison.OrdinalIgnoreCase)
            || !ArePathSetsEqual(input.Lr2BuiltinFolderSourceDirectories, CreateLr2SongDbSyncBuiltinFolderSourceDirectories()))
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
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }

    private static Lr2TextMetadataCandidateSnapshot CreateLr2SongDbSyncTextMetadataCandidates(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        return Lr2FolderInfoCandidateEnumerationService.CreateTextMetadataSnapshot(
            rootDirectories,
            targetDirectories);
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

    private static void ApplyLr2SyncRequestSurfaceToFileCheckResult(
        SongTableFileCheckResult result,
        Lr2SongDbSyncRequest request)
    {
        if (result == null || request == null)
        {
            return;
        }

        if (request.DirectoryEntries != null)
        {
            result.Lr2ScanDirectoryEntries = request.DirectoryEntries;
        }
        if (request.FolderInfoFilePaths != null)
        {
            result.Lr2ScanFolderInfoFilePaths = ToReadOnlyList(request.FolderInfoFilePaths);
        }
        if (request.FolderInfoFileEntries != null)
        {
            result.Lr2ScanFolderInfoFileEntries = request.FolderInfoFileEntries;
        }
        if (request.TextFileDirectories != null)
        {
            result.Lr2ScanTextFileDirectories = ToReadOnlyList(request.TextFileDirectories);
        }
    }

    private static void ApplyLr2FilteredFolderCandidateSurfaceToFileCheckResult(
        SongTableFileCheckResult result,
        Lr2FolderFileDiffPreparationResult preparation)
    {
        if (result == null || preparation?.Request == null)
        {
            return;
        }

        result.Lr2ScanLr2FolderFilePaths = ToReadOnlyList(preparation.Request.Lr2FolderFilePaths);
        result.Lr2ScanLr2FolderFileEntries = preparation.Request.Lr2FolderFileEntries
            ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        result.Lr2ScanLr2FolderFileDiscoveryComplete = preparation.Request.Lr2FolderFileDiscoveryComplete;
        result.Lr2ScanLr2FolderCandidatesAlreadyFiltered = true;
        result.Lr2ScanLr2FolderAppManagedFilteredCount = preparation.AppManagedCandidateCount;
        result.Lr2ScanLr2FolderAppManagedScopeDirectoryCount = preparation.AppManagedOutputDirectories.Count;
        result.Lr2ScanLr2FolderAppManagedExactFileCount = preparation.AppManagedOutputFilePaths.Count;
    }

    private static IReadOnlyList<T> ToReadOnlyList<T>(IReadOnlyCollection<T> values)
    {
        if (values == null)
        {
            return [];
        }

        return values as IReadOnlyList<T> ?? [.. values];
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
            var fileInfo = new FileInfo(Path.Combine(directoryPath, "folderinfo.txt"));
            return fileInfo.Exists
                ? new RootFileEnumerationEntry(fileInfo.FullName, fileInfo.LastWriteTimeUtc, fileInfo.Length)
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

    private static IReadOnlyList<string> NormalizeLr2DirectoryMetadataTargets(IEnumerable<string> targetDirectories)
    {
        return [.. (targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyCollection<string> MergeLr2DirectoryMetadataTargets(params IEnumerable<string>[] targetSets)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IEnumerable<string> targetSet in targetSets ?? [])
        {
            foreach (string target in NormalizeLr2DirectoryMetadataTargets(targetSet))
            {
                result.Add(target);
            }
        }
        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyCollection<string> CreateLr2FolderPhysicalParentDirectoryMetadataTargets(
        IEnumerable<string> lr2FolderFilePaths,
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        IReadOnlyList<string> boundaries = CreateLr2FolderPhysicalParentDirectoryBoundaries(
            rootDirectories,
            normalCustomFolderOutputBaseDir,
            rootCustomFolderOutputBaseDir,
            builtinSourceDirectories);
        if (boundaries.Count == 0)
        {
            return [];
        }

        var boundarySet = new HashSet<string>(boundaries, StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in lr2FolderFilePaths ?? [])
        {
            string normalizedFilePath = Lr2FolderPath.NormalizeDirectoryPath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedFilePath) || !visitedFilePaths.Add(normalizedFilePath))
            {
                continue;
            }

            string directory = Lr2FolderPath.SafeGetParentNormalizedDirectory(normalizedFilePath);
            if (string.IsNullOrWhiteSpace(directory) || !visitedDirectories.Add(directory))
            {
                continue;
            }

            string boundary = FindNearestLr2DirectoryBoundary(directory, boundarySet);
            while (!string.IsNullOrWhiteSpace(directory)
                && !string.IsNullOrWhiteSpace(boundary)
                && !string.Equals(directory, boundary, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(directory);
                string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(directory);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                directory = parent;
            }
        }
        return [.. targets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> CreateLr2FolderPhysicalParentDirectoryBoundaries(
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        var boundaries = new List<string>();
        foreach (string rootDirectory in rootDirectories ?? [])
        {
            boundaries.Add(CreateLr2FolderPhysicalParentDirectoryBoundary(rootDirectory));
        }
        string normalOutputBase = Lr2FolderPath.NormalizeDirectoryPath(normalCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(normalOutputBase))
        {
            boundaries.Add(CreateLr2FolderPhysicalParentDirectoryBoundary(normalOutputBase));
        }
        string rootOutputBase = Lr2FolderPath.NormalizeDirectoryPath(rootCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(rootOutputBase))
        {
            boundaries.Add(rootOutputBase);
        }
        boundaries.AddRange(builtinSourceDirectories ?? []);
        return [.. boundaries
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string CreateLr2FolderPhysicalParentDirectoryBoundary(string rootEquivalentDirectory)
    {
        string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(rootEquivalentDirectory);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return null;
        }

        string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(normalizedRoot));
        return string.IsNullOrWhiteSpace(parent) ? normalizedRoot : parent;
    }

    private static string FindNearestLr2DirectoryBoundary(string normalizedDirectory, ISet<string> boundaries)
    {
        string current = normalizedDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (boundaries?.Contains(current) == true)
            {
                return current;
            }

            string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        IEnumerable<string> groupedSourceDirectories,
        IEnumerable<string> targetDirectories)
    {
        IReadOnlyCollection<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(sourceEntries, targets);
        List<string> missingTargets = [.. targets
            .Where(target => !entries.TryGetValue(target, out RootFileEnumerationEntry entry)
                || entry.LastWriteTimeUtc == null)];
        if (missingTargets.Count == 0)
        {
            return entries;
        }

        IReadOnlyCollection<string> groupedEntriesSourceDirectories = [.. (groupedSourceDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (groupedEntriesSourceDirectories.Count == 0)
        {
            return entries;
        }
        IReadOnlyDictionary<string, RootFileEnumerationEntry> missingEntries =
            CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(groupedEntriesSourceDirectories, missingTargets);
        return MergeLr2DirectoryEntrySurfaces(entries, missingEntries);
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> MergeLr2DirectoryEntrySurfaces(
        params IReadOnlyDictionary<string, RootFileEnumerationEntry>[] entrySets)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, RootFileEnumerationEntry> entries in entrySets ?? [])
        {
            foreach (RootFileEnumerationEntry entry in entries?.Values ?? [])
            {
                string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                if (!result.TryGetValue(key, out RootFileEnumerationEntry existing)
                    || existing.LastWriteTimeUtc == null && entry.LastWriteTimeUtc != null)
                {
                    result[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
                }
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        return Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedEnumeration(rootDirectories, targetDirectories);
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
                snapshot = ownedChartCollectionInitialized
                    ? ownedChartCollection.CreateLibraryChartRefIndexSnapshot()
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
        MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            CurrentOptionsSnapshot,
            stage: "lr2_song_db_maintenance_write_failed",
            detail: "lr2_song_db_maintenance_write_failed: " + displayedMessage,
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

    private static List<string> CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(IEnumerable<string> rootDirectories)
    {
        return Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            rootDirectories,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            CreateLr2SongDbSyncBuiltinFolderSourceDirectories());
    }

    private static Lr2FolderFileCandidateSnapshot CreateLr2SongDbSyncLr2FolderFileCandidates(
        IEnumerable<string> rootDirectories,
        string lr2RootPath,
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings)
    {
        return Lr2FolderFileDiscoveryService.CreateFileCandidates(
            rootDirectories,
            lr2RootPath,
            builtinCustomFolderSettings,
            LogEverythingScan);
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

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> OverlayLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> overlayEntries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(result, baseEntries, overwriteExisting: false);
        AddDirectoryEntries(result, overlayEntries, overwriteExisting: true);
        return result;
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> MergeMissingLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> additions)
    {
        if (additions == null || additions.Count == 0)
        {
            return baseEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, RootFileEnumerationEntry> result = null;
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in additions)
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = Lr2FolderPath.NormalizeDirectoryPath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            RootFileEnumerationEntry next = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            bool shouldApply = baseEntries == null
                || !baseEntries.TryGetValue(path, out RootFileEnumerationEntry existing)
                || existing.LastWriteTimeUtc == null && next.LastWriteTimeUtc != null;
            if (!shouldApply)
            {
                continue;
            }

            result ??= CopyLr2DirectoryEntrySurface(baseEntries);
            result[path] = next;
        }

        return result ?? baseEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, RootFileEnumerationEntry> CopyLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(result, entries, overwriteExisting: false);
        return result;
    }

    private static IReadOnlyList<string> MergePreparedFileSurface(
        IEnumerable<string> basePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IEnumerable<string> preparedPaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> preparedEntries,
        IEnumerable<string> preparedScopeDirectories,
        out IReadOnlyDictionary<string, RootFileEnumerationEntry> mergedEntries)
    {
        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedScopeDirectories);
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in CreateNormalizedFileEntries(basePaths, baseEntries))
        {
            if (scopeMatcher.ContainsFilePath(entry.Path))
            {
                continue;
            }
            entriesByPath[entry.Path] = entry;
        }
        foreach (RootFileEnumerationEntry entry in CreateNormalizedFileEntries(preparedPaths, preparedEntries))
        {
            entriesByPath[entry.Path] = entry;
        }

        mergedEntries = entriesByPath;
        return [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> MergePreparedDirectoryList(
        IEnumerable<string> baseDirectories,
        IEnumerable<string> preparedDirectories,
        IEnumerable<string> preparedScopeDirectories)
    {
        IReadOnlyList<string> preparedDirectoryTargets = NormalizeLr2DirectoryMetadataTargets(preparedDirectories);
        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedScopeDirectories);
        if (scopeMatcher.IsEmpty && preparedDirectoryTargets.Count == 0)
        {
            return TryUseNormalizedDirectoryList(baseDirectories, out IReadOnlyList<string> normalizedBaseDirectories)
                ? normalizedBaseDirectories
                : NormalizeLr2DirectoryMetadataTargets(baseDirectories);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in baseDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (string.IsNullOrWhiteSpace(normalized)
                || scopeMatcher.ContainsNormalizedDirectory(normalized)
                || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
        }
        foreach (string directory in preparedDirectoryTargets)
        {
            if (seen.Add(directory))
            {
                result.Add(directory);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool TryUseNormalizedDirectoryList(
        IEnumerable<string> directories,
        out IReadOnlyList<string> normalizedDirectories)
    {
        normalizedDirectories = directories as IReadOnlyList<string>;
        if (normalizedDirectories == null)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in normalizedDirectories)
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (string.IsNullOrWhiteSpace(normalized)
                || !string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(normalized))
            {
                normalizedDirectories = null;
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<RootFileEnumerationEntry> CreateNormalizedFileEntries(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in paths ?? [])
        {
            string path = SafeFullPathOrOriginal(rawPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            RootFileEnumerationEntry entry = entriesByPath != null
                && entriesByPath.TryGetValue(rawPath, out RootFileEnumerationEntry rawEntry)
                    ? rawEntry
                    : null;
            entries[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            string path = SafeFullPathOrOriginal(!string.IsNullOrWhiteSpace(pair.Value?.Path) ? pair.Value.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path) || entries.ContainsKey(path))
            {
                continue;
            }
            RootFileEnumerationEntry entry = pair.Value;
            entries[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
        return entries.Values;
    }

    private static void AddDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
        bool overwriteExisting)
    {
        if (result == null)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = Lr2FolderPath.NormalizeDirectoryPath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (!overwriteExisting && result.ContainsKey(path))
            {
                continue;
            }
            result[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
    }

    private static IReadOnlyList<string> CreateLr2FolderDiscoveryDirectoriesForEnumeration(
        IEnumerable<string> discoveryDirectories,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        return Lr2FolderFileDiscoveryService.CreateDiscoveryDirectoriesForEnumeration(discoveryDirectories, preparedSurface);
    }

    private static List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> builtinSourceDirectories,
        bool includeAppManagedOutputDirectories = true)
    {
        return Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            rootDirectories,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            builtinSourceDirectories,
            includeAppManagedOutputDirectories);
    }

    private IReadOnlyList<string> CreateLr2SongDbSyncAppManagedOutputDirectories()
    {
        return CreateLr2SongDbSyncAppManagedOutputScope().Directories;
    }

    private sealed class Lr2SongDbSyncAppManagedOutputScope(
        IReadOnlyList<string> directories,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<string> pruneExcludedPaths,
        bool isComplete)
    {
        public IReadOnlyList<string> Directories { get; } = directories ?? [];

        public IReadOnlyList<string> FilePaths { get; } = filePaths ?? [];

        public IReadOnlyList<string> PruneExcludedPaths { get; } = pruneExcludedPaths ?? [];

        public bool IsComplete { get; } = isComplete;
    }

    private Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExcludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            Lr2ManagedCustomFolderOutputCounts outputCounts =
                CreateLr2SongDbSyncAppManagedOutputCounts(songDb);
            foreach (BMSTable table in songDb.Table<BMSTable>())
            {
                string outputDirectory = ResolveManagedPlaylistOutputDirectory(table);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    directories.Add(outputDirectory);
                    IReadOnlyList<string> relativePaths = CreateManagedCustomFolderOutputRelativeFilePaths(table, outputCounts);
                    foreach (string relativePath in relativePaths)
                    {
                        string filePath = SafeFullPathOrOriginal(Path.Combine(outputDirectory, relativePath));
                        filePaths.Add(filePath);
                        AddManagedPlaylistPruneExcludedPaths(table, filePath, pruneExcludedPaths);
                    }
                    foreach (string rowPath in CreateManagedCustomFolderOutputParentDirectoryRowPaths(table, outputDirectory, relativePaths))
                    {
                        AddManagedPlaylistPruneExcludedPaths(table, rowPath, pruneExcludedPaths);
                    }
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
            [.. filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. pruneExcludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            isComplete);
    }

    private static void AddManagedPlaylistPruneExcludedPaths(
        BMSTable table,
        string filePath,
        ISet<string> pruneExcludedPaths)
    {
        if (string.IsNullOrWhiteSpace(filePath) || pruneExcludedPaths == null)
        {
            return;
        }

        pruneExcludedPaths.Add(filePath);
        string databasePath = ResolveManagedPlaylistOutputDatabasePath(table, filePath);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            pruneExcludedPaths.Add(databasePath);
        }
    }

    private static IReadOnlyList<string> CreateManagedCustomFolderOutputParentDirectoryRowPaths(
        BMSTable table,
        string outputDirectory,
        IEnumerable<string> relativeFilePaths)
    {
        string normalizedOutputDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
        string generationBoundary = CreateManagedCustomFolderDirectoryRowGenerationBoundary(table, normalizedOutputDirectory);
        if (string.IsNullOrWhiteSpace(normalizedOutputDirectory)
            || string.IsNullOrWhiteSpace(generationBoundary))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeFilePath in relativeFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath))
            {
                continue;
            }

            string filePath = SafeFullPathOrOriginal(Path.Combine(normalizedOutputDirectory, relativeFilePath));
            string directory = Lr2FolderPath.SafeGetParentNormalizedDirectory(Lr2FolderPath.NormalizeDirectoryPath(filePath));
            while (!string.IsNullOrWhiteSpace(directory)
                && Lr2FolderPath.IsSameOrDescendantNormalized(directory, generationBoundary)
                && !string.Equals(directory, generationBoundary, StringComparison.OrdinalIgnoreCase))
            {
                string rowPath = Lr2FolderPath.ToFolderPathFromNormalizedDirectory(directory);
                if (!string.IsNullOrWhiteSpace(rowPath))
                {
                    result.Add(rowPath);
                }

                string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(directory);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                directory = parent;
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string CreateManagedCustomFolderDirectoryRowGenerationBoundary(BMSTable table, string outputDirectory)
    {
        string normalizedOutputDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
        if (string.IsNullOrWhiteSpace(normalizedOutputDirectory))
        {
            return null;
        }

        if (table?.is_root_folder != true)
        {
            string normalOutputBase = Lr2FolderPath.NormalizeDirectoryPath(Settings.Default.LR2CustomFolderOutputBaseDir);
            if (!string.IsNullOrWhiteSpace(normalOutputBase)
                && Lr2FolderPath.IsSameOrDescendantNormalized(normalizedOutputDirectory, normalOutputBase))
            {
                return CreateLr2FolderPhysicalParentDirectoryBoundary(normalOutputBase);
            }
        }

        return CreateLr2FolderPhysicalParentDirectoryBoundary(normalizedOutputDirectory);
    }

    private static string ResolveManagedPlaylistOutputDatabasePath(BMSTable table, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        if (table?.is_root_folder != true)
        {
            return Lr2FolderFileProjection.NormalizeDatabasePath(filePath);
        }

        try
        {
            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = filePath,
                Lr2RootPath = Settings.Default.LR2RootPath,
                RootCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType
            });
            return Lr2FolderFileProjection.NormalizeDatabasePath(classification.DatabasePath ?? filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return Lr2FolderFileProjection.NormalizeDatabasePath(filePath);
        }
    }

    private sealed class PlaylistCountRow
    {
        public int PlaylistId { get; set; }

        public int Count { get; set; }
    }

    private sealed class PlaylistIdRow
    {
        public int PlaylistId { get; set; }
    }

    private static Lr2ManagedCustomFolderOutputCounts CreateLr2SongDbSyncAppManagedOutputCounts(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return Lr2ManagedCustomFolderOutputCounts.Empty;
        }

        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(row => row.playlist_id);
        string folderColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(row => row.folder);
        string normalizedFolderExpression = "COALESCE(" + folderColumn + ", '')";
        string removedColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(row => row.is_removed);
        string levelColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(row => row.level);
        string levelBucketExpression = "CASE WHEN " + levelColumn + " >= 0 OR CAST(" + levelColumn + " AS INTEGER) = " + levelColumn
            + " THEN CAST(" + levelColumn + " AS INTEGER) ELSE CAST(" + levelColumn + " - 1.0 AS INTEGER) END";

        Dictionary<int, int> userFolderCounts = songDb.Query<PlaylistCountRow>(
            "SELECT " + playlistIdColumn + " AS PlaylistId, COUNT(1) AS Count"
            + " FROM (SELECT " + playlistIdColumn + ", " + normalizedFolderExpression + " AS FolderKey"
            + " FROM " + tableName
            + " WHERE " + removedColumn + " = 0"
            + " GROUP BY " + playlistIdColumn + ", FolderKey)"
            + " GROUP BY " + playlistIdColumn + ";")
            .ToDictionary(row => row.PlaylistId, row => row.Count);

        Dictionary<int, int> levelFolderCounts = songDb.Query<PlaylistCountRow>(
            "SELECT " + playlistIdColumn + " AS PlaylistId, COUNT(1) AS Count"
            + " FROM (SELECT " + playlistIdColumn + ", " + levelBucketExpression + " AS LevelBucket"
            + " FROM " + tableName
            + " WHERE " + levelColumn + " IS NOT NULL"
            + " GROUP BY " + playlistIdColumn + ", LevelBucket)"
            + " GROUP BY " + playlistIdColumn + ";")
            .ToDictionary(row => row.PlaylistId, row => row.Count);

        HashSet<int> nullLevelPlaylistIds = [.. songDb.Query<PlaylistIdRow>(
            "SELECT " + playlistIdColumn + " AS PlaylistId"
            + " FROM " + tableName
            + " WHERE " + levelColumn + " IS NULL"
            + " GROUP BY " + playlistIdColumn + ";")
            .Select(row => row.PlaylistId)];

        return new Lr2ManagedCustomFolderOutputCounts(
            userFolderCounts,
            levelFolderCounts,
            nullLevelPlaylistIds);
    }

    private static IReadOnlyList<string> CreateManagedCustomFolderOutputRelativeFilePaths(
        BMSTable table,
        Lr2ManagedCustomFolderOutputCounts outputCounts)
    {
        return Lr2ManagedCustomFolderOutputLayout.CreateRelativeFilePaths(
            table,
            outputCounts,
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent);
    }

    private static string ResolveManagedPlaylistOutputDirectory(BMSTable table)
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

        string outputBase = table.is_root_folder
            ? Settings.Default.LR2CustomFolderOutputBaseDirRootType
            : Settings.Default.LR2CustomFolderOutputBaseDir;
        if (string.IsNullOrWhiteSpace(outputBase) || string.IsNullOrWhiteSpace(outputDir))
        {
            return null;
        }

        try
        {
            return SafeFullPathOrOriginal(Path.Combine(outputBase, outputDir));
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

    private static List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories()
    {
        return Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(Settings.Default.LR2RootPath);
    }

    private sealed class Lr2SongDbSyncInput(
        IReadOnlyList<string> rootDirectories,
        IReadOnlyList<string> chartPaths,
        IReadOnlyList<string> normalFolderDirectoryPaths,
        IReadOnlyList<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        IReadOnlyList<string> lr2FolderDiscoveryDirectories,
        IReadOnlyList<string> lr2FolderPruneDirectories,
        IReadOnlyList<string> lr2FolderPruneExcludedPaths,
        string lr2RootPath,
        string lr2NormalCustomFolderOutputBaseDir,
        string lr2RootCustomFolderOutputBaseDir,
        IReadOnlyList<string> lr2BuiltinFolderSourceDirectories,
        Lr2BuiltinCustomFolderSettings lr2BuiltinCustomFolderSettings,
        IReadOnlyList<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        bool lr2FolderFileDiscoveryComplete,
        IReadOnlyList<BMSFile> songRows,
        IReadOnlyList<string> textFileDirectories,
        int scanSurfaceGeneration,
        int ownedChartCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

        public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

        public IReadOnlyList<string> NormalFolderDirectoryPaths { get; } = normalFolderDirectoryPaths ?? [];

        public IReadOnlyList<string> FolderInfoFilePaths { get; } = folderInfoFilePaths ?? [];

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; } =
            folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
            directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Lr2FolderDiscoveryDirectories { get; } = lr2FolderDiscoveryDirectories ?? [];

        public IReadOnlyList<string> Lr2FolderPruneDirectories { get; } = lr2FolderPruneDirectories ?? [];

        public IReadOnlyList<string> Lr2FolderPruneExcludedPaths { get; } = lr2FolderPruneExcludedPaths ?? [];

        public string Lr2RootPath { get; } = lr2RootPath;

        public string Lr2NormalCustomFolderOutputBaseDir { get; } = lr2NormalCustomFolderOutputBaseDir;

        public string Lr2RootCustomFolderOutputBaseDir { get; } = lr2RootCustomFolderOutputBaseDir;

        public IReadOnlyList<string> Lr2BuiltinFolderSourceDirectories { get; } = lr2BuiltinFolderSourceDirectories ?? [];

        public Lr2BuiltinCustomFolderSettings Lr2BuiltinCustomFolderSettings { get; } =
            lr2BuiltinCustomFolderSettings ?? new Lr2BuiltinCustomFolderSettings(0, 24, false);

        public IReadOnlyList<string> Lr2FolderFilePaths { get; } = lr2FolderFilePaths ?? [];

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; } =
            lr2FolderFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public bool Lr2FolderFileDiscoveryComplete { get; } = lr2FolderFileDiscoveryComplete;

        public IReadOnlyList<BMSFile> SongRows { get; } = songRows ?? [];

        public IReadOnlyList<string> TextFileDirectories { get; } = textFileDirectories ?? [];

        public int ScanSurfaceGeneration { get; } = scanSurfaceGeneration;

        public int OwnedChartCollectionVersion { get; } = ownedChartCollectionVersion;

        public int BmsRowsVersion { get; } = bmsRowsVersion;

        public int BmsonRowsVersion { get; } = bmsonRowsVersion;
    }

    private sealed class Lr2SongDbSyncScanSurfaceSnapshot(
        int generation,
        IReadOnlyList<string> rootDirectories,
        IReadOnlyList<string> normalFolderDirectoryPaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> normalFolderDirectoryEntries,
        IReadOnlyList<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        IReadOnlyList<string> textFileDirectories,
        IReadOnlyList<string> lr2FolderDiscoveryDirectories,
        IReadOnlyList<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        bool lr2FolderFileDiscoveryComplete,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        public int Generation { get; } = generation;

        public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

        public IReadOnlyList<string> NormalFolderDirectoryPaths { get; } = normalFolderDirectoryPaths ?? [];

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
            directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalFolderDirectoryEntries { get; } =
            normalFolderDirectoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> FolderInfoFilePaths { get; } = folderInfoFilePaths ?? [];

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; } =
            folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> TextFileDirectories { get; } = textFileDirectories ?? [];

        public IReadOnlyList<string> Lr2FolderDiscoveryDirectories { get; } = lr2FolderDiscoveryDirectories ?? [];

        public IReadOnlyList<string> Lr2FolderFilePaths { get; } = lr2FolderFilePaths ?? [];

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; } =
            lr2FolderFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        public bool Lr2FolderFileDiscoveryComplete { get; } = lr2FolderFileDiscoveryComplete;

        public int OwnedCollectionVersion { get; } = ownedCollectionVersion;

        public int BmsRowsVersion { get; } = bmsRowsVersion;

        public int BmsonRowsVersion { get; } = bmsonRowsVersion;
    }

    private sealed class Lr2SongDbSyncFileDiffFreshnessSnapshot(
        string reason,
        int scanSurfaceGeneration,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion,
        int bmsOwnerCount,
        int bmsonOwnerCount,
        int bmsTargetCount,
        int bmsDeletedCount,
        int bmsDateOnlyUpdateCount,
        int bmsTextOnlyUpdateCount,
        int bmsMovedHashRelinkCount,
        int bmsMovedHashRelinkAmbiguousCount,
        int inlineChartInfoTargetCount,
        int inlineChartInfoSuccessCount,
        int inlineChartInfoCurrentSkippedCount,
        int inlineChartInfoParseFailedCount,
        int inlineMaintenanceTargetCount,
        int inlineMaintenanceBmsCount,
        int inlineMaintenanceBmsonCount,
        int inlineMaintenanceFailedCount,
        IEnumerable<string> transientSongRowSkipPaths)
    {
        public string Reason { get; } = reason ?? "unknown";

        public int ScanSurfaceGeneration { get; } = scanSurfaceGeneration;

        public int OwnedCollectionVersion { get; } = ownedCollectionVersion;

        public int BmsRowsVersion { get; } = bmsRowsVersion;

        public int BmsonRowsVersion { get; } = bmsonRowsVersion;

        public int BmsOwnerCount { get; } = bmsOwnerCount;

        public int BmsonOwnerCount { get; } = bmsonOwnerCount;

        public int BmsTargetCount { get; } = bmsTargetCount;

        public int BmsDeletedCount { get; } = bmsDeletedCount;

        public int BmsDateOnlyUpdateCount { get; } = bmsDateOnlyUpdateCount;

        public int BmsTextOnlyUpdateCount { get; } = bmsTextOnlyUpdateCount;

        public int BmsMovedHashRelinkCount { get; } = bmsMovedHashRelinkCount;

        public int BmsMovedHashRelinkAmbiguousCount { get; } = bmsMovedHashRelinkAmbiguousCount;

        public int InlineChartInfoTargetCount { get; } = inlineChartInfoTargetCount;

        public int InlineChartInfoSuccessCount { get; } = inlineChartInfoSuccessCount;

        public int InlineChartInfoCurrentSkippedCount { get; } = inlineChartInfoCurrentSkippedCount;

        public int InlineChartInfoParseFailedCount { get; } = inlineChartInfoParseFailedCount;

        public int InlineMaintenanceTargetCount { get; } = inlineMaintenanceTargetCount;

        public int InlineMaintenanceBmsCount { get; } = inlineMaintenanceBmsCount;

        public int InlineMaintenanceBmsonCount { get; } = inlineMaintenanceBmsonCount;

        public int InlineMaintenanceFailedCount { get; } = inlineMaintenanceFailedCount;

        public HashSet<string> TransientSongRowSkipPaths { get; } =
            new HashSet<string>(
                (transientSongRowSkipPaths ?? [])
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
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
                result = HydrateChartInfos(reason, allowAllCurrentFastPath: queueBackfillAfterHydration);
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
                + " fastPath=" + result.FastPath.ToString().ToLowerInvariant()
                + " candidateSummaryMs=" + result.CandidateSummaryMs
                + " dbMode=" + (string.IsNullOrWhiteSpace(result.DbMaterializeMode) ? "unknown" : result.DbMaterializeMode)
                + " dbLoadMs=" + result.DbLoadMs
                + " dbMaterializeMs=" + result.DbMaterializeMs
                + " rawRows=" + result.DbRawRows
                + " rawReadMs=" + result.DbRawReadMs
                + " rawObjectMs=" + result.DbRawObjectMs
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

    private ChartInfoHydrationResult HydrateChartInfos(string reason, bool allowAllCurrentFastPath = false)
    {
        var result = new ChartInfoHydrationResult();
        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("chart_info_hydration start reason=" + (reason ?? "unknown"));
        if (allowAllCurrentFastPath && TryCreateAllCurrentChartInfoHydrationResultFromCompletedLr2SongDbSync(reason, totalStopwatch, out ChartInfoHydrationResult fastPathResult))
        {
            return fastPathResult;
        }

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
            result.DbMaterializeMode = loadResult.MaterializeMode;
            result.DbRawRows = loadResult.RawRows;
            result.DbRawReadMs = loadResult.RawReadMs;
            result.DbRawObjectMs = loadResult.RawObjectMs;
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
        int ownedCollectionVersionAtSummary = 0;
        int bmsRowsVersionAtSummary = 0;
        int bmsonRowsVersionAtSummary = 0;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ChartInfoHydrationOwnerSummary ownerSummary = CreateChartInfoHydrationOwnerSummaryUnsafe(
                currentChartInfoSha256s,
                currentParseFailureMd5s);
            result.OwnerCount = ownerSummary.OwnerCount;
            result.CurrentChartInfoOwnerCount = ownerSummary.CurrentChartInfoOwnerCount;
            result.CurrentParseFailureOwnerCount = ownerSummary.CurrentParseFailureOwnerCount;
            result.BackfillCandidateOwnerCount = ownerSummary.BackfillCandidateOwnerCount;
            result.OwnerApplySkippedCount = ownerSummary.OwnerApplySkippedCount;
            ownedCollectionVersionAtSummary = OwnedChartCollectionVersion;
            bmsRowsVersionAtSummary = Volatile.Read(ref bmsStorageRowsVersion);
            bmsonRowsVersionAtSummary = Volatile.Read(ref bmsonStorageRowsVersion);
        }
        ownerClassifyStopwatch.Stop();
        result.OwnerApplyMs = ownerClassifyStopwatch.ElapsedMilliseconds;
        result.ApplyMs = result.OwnerApplyMs;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        result.Succeeded = true;
        CaptureChartInfoHydrationAllCurrentSnapshot(
            result,
            ownedCollectionVersionAtSummary,
            bmsRowsVersionAtSummary,
            bmsonRowsVersionAtSummary);
        return result;
    }

    private bool TryCreateAllCurrentChartInfoHydrationResultFromCompletedLr2SongDbSync(
        string reason,
        Stopwatch totalStopwatch,
        out ChartInfoHydrationResult result)
    {
        result = null;
        var stopwatch = Stopwatch.StartNew();
        ChartInfoCompletedLr2SongDbSyncTrustSnapshot trustSnapshot = GetCurrentChartInfoCompletedLr2SongDbSyncTrustSnapshot();
        if (trustSnapshot == null)
        {
            stopwatch.Stop();
            LogInstallPerformance("chart_info_hydration_fast_path skipped reason=no_completed_song_db_sync_trust"
                + " requestReason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return false;
        }

        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        if (options?.OperationModeLR2DB != true)
        {
            stopwatch.Stop();
            LogInstallPerformance("chart_info_hydration_fast_path skipped reason=lr2_mode_disabled"
                + " requestReason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return false;
        }

        long parseTimeoutMs = Math.Max(0L, (long)Math.Ceiling(chartInfoBuildService.CurrentParseTimeout.TotalMilliseconds));
        if (parseTimeoutMs != Lr2SongDbSyncCompletedStatusImplicitChartInfoParseTimeoutMs)
        {
            stopwatch.Stop();
            LogInstallPerformance("chart_info_hydration_fast_path skipped reason=parse_timeout_not_represented_in_status"
                + " requestReason=" + (reason ?? "unknown")
                + " parseTimeoutMs=" + parseTimeoutMs
                + " implicitStatusParseTimeoutMs=" + Lr2SongDbSyncCompletedStatusImplicitChartInfoParseTimeoutMs
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return false;
        }

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        Lr2SongDbSyncStatusSnapshot status;
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            status = Lr2SongDbSyncStatusService.Evaluate(
                songDb,
                enabled: true,
                signature,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogInstallPerformance("chart_info_hydration_fast_path skipped reason=status_failed"
                + " requestReason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " message=" + ex.Message);
            return false;
        }

        if (status == null || status.Status != Lr2SongDbSyncStatusKind.Completed)
        {
            stopwatch.Stop();
            LogInstallPerformance("chart_info_hydration_fast_path skipped reason=status_not_completed"
                + " requestReason=" + (reason ?? "unknown")
                + " status=" + (status?.Status.ToString() ?? "(null)")
                + " storedStatus=" + (status?.StoredStatus?.ToString() ?? "(none)")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return false;
        }

        stopwatch.Stop();
        totalStopwatch.Stop();
        result = new ChartInfoHydrationResult
        {
            Succeeded = true,
            FastPath = true,
            CandidateSummaryMs = stopwatch.ElapsedMilliseconds,
            TotalRows = trustSnapshot.OwnerCount,
            ChartInfoRows = 0,
            OwnerCount = trustSnapshot.OwnerCount,
            CurrentChartInfoOwnerCount = trustSnapshot.OwnerCount,
            CurrentParseFailureOwnerCount = 0,
            BackfillCandidateOwnerCount = 0,
            LoadMs = stopwatch.ElapsedMilliseconds,
            TotalMs = totalStopwatch.ElapsedMilliseconds
        };
        CaptureChartInfoHydrationAllCurrentSnapshot(
            result,
            trustSnapshot.OwnedCollectionVersion,
            trustSnapshot.BmsRowsVersion,
            trustSnapshot.BmsonRowsVersion);
        LogInstallPerformance("chart_info_hydration_fast_path used"
            + " source=completed_song_db_sync"
            + " requestReason=" + (reason ?? "unknown")
            + " trustReason=" + (trustSnapshot.Reason ?? "unknown")
            + " owners=" + trustSnapshot.OwnerCount
            + " status=" + status.Status
            + " elapsedMs=" + result.TotalMs);
        return true;
    }

    private ChartInfoOwnerVersionSnapshot CaptureChartInfoOwnerVersionSnapshot()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return new ChartInfoOwnerVersionSnapshot
            {
                OwnedCollectionVersion = OwnedChartCollectionVersion,
                BmsRowsVersion = Volatile.Read(ref bmsStorageRowsVersion),
                BmsonRowsVersion = Volatile.Read(ref bmsonStorageRowsVersion),
                BmsOwnerCount = _BMSFiles?.Count ?? 0,
                BmsonOwnerCount = _BmsonSongs?.Count ?? 0
            };
        }
    }

    private void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (!CanTrustCompletedLr2SongDbSyncForChartInfo(options, fileCheckResult))
        {
            ClearChartInfoCompletedLr2SongDbSyncTrustSnapshot("file_diff_changed_" + (reason ?? "unknown"));
            return;
        }

        ChartInfoOwnerVersionSnapshot version = CaptureChartInfoOwnerVersionSnapshot();
        var trustSnapshot = new ChartInfoCompletedLr2SongDbSyncTrustSnapshot
        {
            OwnedCollectionVersion = version.OwnedCollectionVersion,
            BmsRowsVersion = version.BmsRowsVersion,
            BmsonRowsVersion = version.BmsonRowsVersion,
            BmsOwnerCount = version.BmsOwnerCount,
            BmsonOwnerCount = version.BmsonOwnerCount,
            Reason = reason ?? "unknown"
        };
        lock (lockChartInfoHydration)
        {
            chartInfoCompletedLr2SongDbSyncTrustSnapshot = trustSnapshot;
        }
        LogInstallPerformance("chart_info_song_db_sync_trust captured"
            + " reason=" + (reason ?? "unknown")
            + " ownerCount=" + trustSnapshot.OwnerCount
            + " bmsOwners=" + trustSnapshot.BmsOwnerCount
            + " bmsonOwners=" + trustSnapshot.BmsonOwnerCount
            + " ownedCollectionVersion=" + trustSnapshot.OwnedCollectionVersion
            + " bmsRowsVersion=" + trustSnapshot.BmsRowsVersion
            + " bmsonRowsVersion=" + trustSnapshot.BmsonRowsVersion);
    }

    private static bool CanTrustCompletedLr2SongDbSyncForChartInfo(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult)
    {
        if (options?.OperationModeLR2DB != true
            || fileCheckResult == null)
        {
            return false;
        }

        return fileCheckResult.BmsAddedTargetCount == 0
            && fileCheckResult.BmsDeletedTargetCount == 0
            && fileCheckResult.BmsMovedHashRelinkCount == 0
            && fileCheckResult.BmsMovedHashRelinkAmbiguousCount == 0
            && fileCheckResult.BmsonUpsertTargetCount == 0
            && fileCheckResult.BmsonDeletedTargetCount == 0
            && fileCheckResult.InlineChartInfoTargetCount == 0
            && fileCheckResult.InlineChartInfoSuccessCount == 0
            && fileCheckResult.InlineChartInfoParseFailedCount == 0
            && fileCheckResult.InlineChartInfoFailurePersistedCount == 0
            && fileCheckResult.InlineChartInfoFailureClearedCount == 0
            && fileCheckResult.InlineChartInfoParseFailureRows.Count == 0
            && fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count == 0;
    }

    private ChartInfoCompletedLr2SongDbSyncTrustSnapshot GetCurrentChartInfoCompletedLr2SongDbSyncTrustSnapshot()
    {
        ChartInfoCompletedLr2SongDbSyncTrustSnapshot snapshot;
        lock (lockChartInfoHydration)
        {
            snapshot = chartInfoCompletedLr2SongDbSyncTrustSnapshot;
        }
        ChartInfoOwnerVersionSnapshot currentVersion = CaptureChartInfoOwnerVersionSnapshot();
        if (snapshot == null || !snapshot.IsCurrent(currentVersion))
        {
            return null;
        }
        return snapshot;
    }

    private void ClearChartInfoCompletedLr2SongDbSyncTrustSnapshot(string reason)
    {
        bool cleared = false;
        lock (lockChartInfoHydration)
        {
            if (chartInfoCompletedLr2SongDbSyncTrustSnapshot != null)
            {
                chartInfoCompletedLr2SongDbSyncTrustSnapshot = null;
                cleared = true;
            }
        }
        if (cleared)
        {
            LogInstallPerformance("chart_info_song_db_sync_trust cleared reason=" + (reason ?? "unknown"));
        }
    }

    private void ClearChartInfoHydrationAllCurrentSnapshot(string reason)
    {
        bool cleared = false;
        lock (lockChartInfoHydration)
        {
            if (chartInfoHydrationAllCurrentSnapshot != null)
            {
                chartInfoHydrationAllCurrentSnapshot = null;
                cleared = true;
            }
        }
        if (cleared)
        {
            LogInstallPerformance("chart_info_hydration_all_current cleared reason=" + (reason ?? "unknown"));
        }
    }

    private void CaptureChartInfoHydrationAllCurrentSnapshot(
        ChartInfoHydrationResult result,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        ChartInfoHydrationAllCurrentSnapshot snapshot = null;
        if (result != null
            && result.Succeeded
            && result.OwnerCount > 0
            && result.BackfillCandidateOwnerCount <= 0)
        {
            snapshot = new ChartInfoHydrationAllCurrentSnapshot
            {
                OwnedCollectionVersion = ownedCollectionVersion,
                BmsRowsVersion = bmsRowsVersion,
                BmsonRowsVersion = bmsonRowsVersion,
                OwnerCount = result.OwnerCount,
                CurrentChartInfoOwnerCount = result.CurrentChartInfoOwnerCount,
                CurrentParseFailureOwnerCount = result.CurrentParseFailureOwnerCount,
                ParserVersion = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                ParseTimeoutMs = Math.Max(0L, (long)Math.Ceiling(chartInfoBuildService.CurrentParseTimeout.TotalMilliseconds))
            };
        }

        lock (lockChartInfoHydration)
        {
            chartInfoHydrationAllCurrentSnapshot = snapshot;
        }
    }

    private ChartInfoHydrationResult CreateCurrentChartInfoHydrationAllCurrentResult()
    {
        ChartInfoHydrationAllCurrentSnapshot snapshot;
        lock (lockChartInfoHydration)
        {
            snapshot = chartInfoHydrationAllCurrentSnapshot;
        }
        if (snapshot == null
            || snapshot.OwnedCollectionVersion != OwnedChartCollectionVersion
            || snapshot.BmsRowsVersion != Volatile.Read(ref bmsStorageRowsVersion)
            || snapshot.BmsonRowsVersion != Volatile.Read(ref bmsonStorageRowsVersion)
            || snapshot.ParserVersion != BmsLibraryDbGateway.CurrentChartInfoParserVersion
            || snapshot.ParseTimeoutMs != Math.Max(0L, (long)Math.Ceiling(chartInfoBuildService.CurrentParseTimeout.TotalMilliseconds)))
        {
            return null;
        }

        return new ChartInfoHydrationResult
        {
            Succeeded = true,
            OwnerCount = snapshot.OwnerCount,
            CurrentChartInfoOwnerCount = snapshot.CurrentChartInfoOwnerCount,
            CurrentParseFailureOwnerCount = snapshot.CurrentParseFailureOwnerCount,
            BackfillCandidateOwnerCount = 0
        };
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        LR2SongDBExtended.chart_info resolved = ResolveChartInfoFromIndex(sha256, md5);
        if (resolved != null || !ShouldLazyLoadChartInfoDisplayIndex())
        {
            return resolved;
        }
        EnsureChartInfoDisplayIndexLoadedForLazyResolve("resolve_chart_info");
        return ResolveChartInfoFromIndex(sha256, md5);
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoFromIndex(string sha256, string md5)
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

    private bool ShouldLazyLoadChartInfoDisplayIndex()
    {
        lock (lockChartInfoIndex)
        {
            if (_ChartInfoIndexHydrated || chartInfoDisplayIndexLoaded)
            {
                return false;
            }
        }
        return CreateCurrentChartInfoHydrationAllCurrentResult() != null;
    }

    private void EnsureChartInfoDisplayIndexLoadedForLazyResolve(string reason)
    {
        if (!ShouldLazyLoadChartInfoDisplayIndex())
        {
            return;
        }
        lock (lockChartInfoLazyDisplayIndexLoad)
        {
            if (!ShouldLazyLoadChartInfoDisplayIndex())
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            LogInstallPerformance("chart_info_lazy_display_index_load start reason=" + (reason ?? "unknown"));
            try
            {
                ChartInfoHydrationLoadResult loadResult = dbGateway.LoadChartInfoHydrationData(chartInfoBuildService.CurrentParseTimeout);
                ChartInfoIndexUpdateResult indexUpdateResult = UpsertChartInfoIndexRows(
                    loadResult.ChartInfoBySha256.Values,
                    "lazy_display_index",
                    dispatchPresentation: false);
                lock (lockChartInfoIndex)
                {
                    chartInfoDisplayIndexLoaded = true;
                }
                stopwatch.Stop();
                LogInstallPerformance("chart_info_lazy_display_index_load done reason=" + (reason ?? "unknown")
                    + " rows=" + loadResult.ChartInfoRows
                    + " dbMode=" + (string.IsNullOrWhiteSpace(loadResult.MaterializeMode) ? "unknown" : loadResult.MaterializeMode)
                    + " dbLoadMs=" + loadResult.DbReadMs
                    + " rawRows=" + loadResult.RawRows
                    + " rawReadMs=" + loadResult.RawReadMs
                    + " rawObjectMs=" + loadResult.RawObjectMs
                    + " indexVersion=" + indexUpdateResult.Version
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ClearChartInfoCompletedLr2SongDbSyncTrustSnapshot("lazy_display_index_load_failed");
                ClearChartInfoHydrationAllCurrentSnapshot("lazy_display_index_load_failed");
                LogInstallPerformanceWarn("chart_info_lazy_display_index_load failed reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " message=" + GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }
        }
    }

    private Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot()
    {
        Dictionary<string, LR2SongDBExtended.chart_info> bySha256;
        Dictionary<string, LR2SongDBExtended.chart_info> byMd5;
        lock (lockChartInfoIndex)
        {
            bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(
                chartInfoIndexBySha256
                    .Where(pair => IsCurrentChartInfoRow(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            byMd5 = chartInfoIndexByMd5
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null && pair.Value.Count > 0)
                .Select(pair => new
                {
                    pair.Key,
                    Row = pair.Value.Values.FirstOrDefault(IsCurrentChartInfoRow)
                })
                .Where(pair => pair.Row != null)
                .ToDictionary(pair => pair.Key, pair => pair.Row, StringComparer.OrdinalIgnoreCase);
        }

        return row =>
        {
            if (row == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256)
                && bySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.hash)
                && byMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5Row))
            {
                return byMd5Row;
            }
            return null;
        };
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
            chartInfoDisplayIndexLoaded = hydrated;
            _ChartInfoIndexVersion++;
            result.Version = _ChartInfoIndexVersion;
        }
        RaisePropertyChanged(() => ChartInfoIndexVersion);
        if (result.HydrationChanged)
        {
            RaisePropertyChanged(() => ChartInfoIndexHydrated);
        }
        DispatchWarningPresentationChanged("chart_info_index_snapshot");
        return result;
    }

    private ChartInfoIndexUpdateResult UpsertChartInfoIndexRows(IEnumerable<LR2SongDBExtended.chart_info> rows, string reason, bool dispatchPresentation = true)
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
        if (dispatchPresentation)
        {
            DispatchWarningPresentationChanged("chart_info_index_delta");
        }
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
        ChartInfoHydrationResult currentAllCurrentResult = null;
        if (hydrationResult != null && hydrationResult.Succeeded && hydrationResult.OwnerCount > 0 && hydrationResult.BackfillCandidateOwnerCount <= 0)
        {
            currentAllCurrentResult = CreateCurrentChartInfoHydrationAllCurrentResult();
        }
        if (currentAllCurrentResult != null && currentAllCurrentResult.OwnerCount == hydrationResult.OwnerCount)
        {
            int skippedVersion = CompleteSkippedChartInfoBackfillRequestIfIdle();
            LogInstallPerformance("chart_info_backfill skipped reason=hydration_all_current"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " ownerCount=" + currentAllCurrentResult.OwnerCount
                + " currentChartInfo=" + currentAllCurrentResult.CurrentChartInfoOwnerCount
                + " currentParseFailure=" + currentAllCurrentResult.CurrentParseFailureOwnerCount
                + " candidates=" + currentAllCurrentResult.BackfillCandidateOwnerCount);
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
        List<ChartFile> targetCharts = NormalizeResourceMaintenanceTargetCharts(charts);
        ChartStorageTargetSet storageTargets = ChartStorageTargetSet.FromCharts(targetCharts);
        var result = new ChartInfoInlineBuildResult();
        bool completed = false;
        if (targetCharts.Count == 0)
        {
            LogInstallPerformance("chart_info_inline_install reason=" + (reason ?? "unknown") + " target=0 success=0 currentSkipped=0 failureSkipped=0 parseFailed=0 failurePersisted=0 failureCleared=0 readFailed=0 parseMs=0");
            return result;
        }

        using (BeginOwnedDigestMutationWindow())
        {
            try
            {
                var inlineBuildService = new ChartInfoInlineBuildService(
                    chartInfoBuildService,
                    BmsLibraryInitializationService.ResolveDefaultFileDiffParserDegree());
                result = inlineBuildService.BuildForExistingCharts(
                    dbGateway,
                    targetCharts,
                    LogInstallPerformance,
                    LogInstallPerformanceWarn);
                int songRowChartInfoApplied = ApplyChartInfoRowsToBmsStorageRows(
                    storageTargets.BmsFiles,
                    result.AppliedRows);
                if (storageTargets.BmsFiles.Count > 0)
                {
                    ExecuteLr2SongDbWrite(
                        () => dbGateway.UpsertSongs(storageTargets.BmsFiles),
                        stage: "lr2_song_db_chart_info_inline_upsert_failed",
                        logReason: reason ?? "chart_info_inline_install");
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
                }
                if (result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0)
                {
                    DispatchWarningPresentationChanged("install_package_inline_chart_info_parse_failure");
                }
                completed = true;
                LogInstallPerformance("chart_info_inline_install reason=" + (reason ?? "unknown")
                    + " target=" + result.TargetCount
                    + " success=" + result.SuccessCount
                    + " currentSkipped=" + result.CurrentSkippedCount
                    + " failureSkipped=" + result.FailureSkippedCount
                    + " parseFailed=" + result.ParseFailedCount
                    + " failurePersisted=" + result.FailurePersistedCount
                    + " failureCleared=" + result.FailureClearedCount
                    + " readFailed=" + result.ReadFailedCount
                    + " songRowChartInfoApplied=" + songRowChartInfoApplied
                    + " parseMs=" + result.ParseMs);
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
                    DispatchOwnedPotentialDigestChanges(targetCharts, (reason ?? "install_package_inline") + "_failed");
                }
            }
        }
    }

    private static int ApplyChartInfoRowsToBmsStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.chart_info> chartInfoRows)
    {
        List<BMSFile> files = [.. (bmsFiles ?? []).Where(file => file != null)];
        if (files.Count == 0)
        {
            return 0;
        }

        Dictionary<string, LR2SongDBExtended.chart_info> rowsBySha256 = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.chart_info> rowsByMd5 = new(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info row in chartInfoRows ?? [])
        {
            if (row == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256))
            {
                rowsBySha256[row.sha256] = row;
            }
            if (!string.IsNullOrWhiteSpace(row.md5) && !rowsByMd5.ContainsKey(row.md5))
            {
                rowsByMd5[row.md5] = row;
            }
        }
        if (rowsBySha256.Count == 0 && rowsByMd5.Count == 0)
        {
            return 0;
        }

        int applied = 0;
        foreach (BMSFile file in files)
        {
            LR2SongDBExtended.chart_info row = null;
            if (!string.IsNullOrWhiteSpace(file.sha256))
            {
                rowsBySha256.TryGetValue(file.sha256, out row);
            }
            if (row == null && !string.IsNullOrWhiteSpace(file.hash))
            {
                rowsByMd5.TryGetValue(file.hash, out row);
            }
            if (row == null)
            {
                continue;
            }
            file.ApplyLr2ChartInfoColumns(row);
            applied++;
        }
        return applied;
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
                chartSnapshot = CreateOwnedChartInfoFullBackfillTargetSnapshot();
            }
            int snapshotCount = chartSnapshot.Count;
            bool completedLatestRequest = false;
            Dictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot = null;
            ChartInfoBackfillResult result = null;
            using (BeginOwnedDigestMutationWindow())
            {
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
                    LogInstallPerformance("chart_info_backfill done version=" + requestVersion + " mode=full total=" + result.TargetCount + " success=" + result.BackfilledCount + " failed=" + result.FailedCount + " timeoutFailed=" + result.TimeoutFailedCount + " digestBackfilled=" + result.DigestBackfilledCount + " digestFailed=" + result.DigestFailedCount + " fileReadCount=" + result.FileReadCount + " fileReadBytes=" + result.FileReadBytes + " currentRowSkipped=" + result.CurrentRowSkippedCount + " parseFailureSkipped=" + result.FailureSkippedCount);
                }
                catch (Exception ex)
                {
                    DispatchOwnedPotentialDigestChanges(chartSnapshot, "chart_info_backfill_digest_failed");
                    LogInstallPerformance("chart_info_backfill failed version=" + requestVersion + " message=" + ex.Message);
                }
                finally
                {
                    if (result != null)
                    {
                        DispatchOwnedChartDigestChanges(result.DigestChanges, "chart_info_backfill_digest");
                    }
                    ChartInfoBackfillCurrentPath = string.Empty;
                    ChartInfoBackfillDigestBackfilledCount = result?.DigestBackfilledCount ?? 0;
                    ChartInfoBackfillCompletedVersion = requestVersion;
                    DispatchWarningPresentationChanged("chart_info_backfill_parse_failure");
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
                    + " mode=" + (string.IsNullOrWhiteSpace(result.MaintenanceMaterializeMode) ? "unknown" : result.MaintenanceMaterializeMode)
                    + " rawRows=" + result.MaintenanceRawRows
                    + " rawReadMs=" + result.MaintenanceRawReadMs
                    + " rawObjectMs=" + result.MaintenanceRawObjectMs
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
        var applyStopwatch = Stopwatch.StartNew();
        ResourceMaintenanceTargetSet resourceHealthTargets = default;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            OwnedChartStorageOwnerView ownerView = CreateOwnedChartStorageOwnerViewUnsafe();
            var attachStopwatch = Stopwatch.StartNew();
            using (BeginResourceHealthInputMutation())
            {
                foreach (BMSFile item in ownerView.BmsFiles)
                {
                    if (item == null)
                    {
                        continue;
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
                foreach (LR2SongDBExtended.bmson_song item in ownerView.BmsonSongs)
                {
                    if (item == null)
                    {
                        continue;
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
            }
            attachStopwatch.Stop();
            result.MaintenanceAttachMs = attachStopwatch.ElapsedMilliseconds;
            resourceHealthTargets = CreateFullOwnedResourceMaintenanceTargetSet("maintenance_hydration");
            result.OwnerPathCount = ownerView.OwnerPathCount;
            foreach (string maintenancePath in result.MaintenanceMap.Keys)
            {
                if (!ownerView.ContainsOwnerPath(maintenancePath))
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
                try
                {
                    using (rwlockSongDBMaintenance.GetWriterGuard())
                    {
                        result.CleanupDeletedCount = dbGateway.DeleteMaintenanceRows(result.StaleMaintenancePaths);
                    }
                }
                catch
                {
                    ForceInvalidateResourceHealthIndex("maintenance_hydration_cleanup_failed");
                    throw;
                }
            }
            cleanupStopwatch.Stop();
            result.CleanupMs = cleanupStopwatch.ElapsedMilliseconds;
        }
        result.ViewRefreshQueued = true;
        DispatchMaintenanceHydrationResult(result, resourceHealthTargets);
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
                try
                {
                    using (rwlockBMSFiles.GetReaderGuard())
                    {
                        filesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
                        snapshotCount = CreateOwnedChartStorageOwnerViewUnsafe().Count;
                    }
                    LogInstallPerformance("installable_maintenance_deferred run version=" + requestVersion
                        + " snapshotCount=" + snapshotCount
                        + " criticalMs=" + requestCriticalElapsedMs);
                    var stopwatchSetMode = Stopwatch.StartNew();
                    setModeTargetCount = setModeAndCommitToDB(filesSnapshot);
                    stopwatchSetMode.Stop();
                    setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                    var stopwatchSetHealth = Stopwatch.StartNew();
                    maintenanceResult = setInstallableMaintenanceInfo("installable_maintenance_deferred") ?? new MaintenanceWorkflowResult();
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
                        + " maintenanceUnchanged=" + maintenanceResult.MaintenanceInfoUnchangedCount
                        + " bmsonReparsed=" + maintenanceResult.BmsonReparsedCount
                        + " bmsonReparseFailed=" + maintenanceResult.BmsonReparseFailedCount
                        + " bmsonResourceRefsReused=" + maintenanceResult.BmsonResourceReferenceReusedCount
                        + " songReloaded=" + maintenanceResult.ReloadedSongCount
                        + " readMs=" + maintenanceResult.ReadMs
                        + " computeMs=" + maintenanceResult.ComputeMs
                        + " commitMs=" + maintenanceResult.CommitMs
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
        List<string> existingRoots = [.. requestedRoots.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))];
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
                ? CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots ?? []).Count
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
        if (!Settings.Default.OperationModeLR2DB)
        {
            return excluded;
        }
        AddNormalizedDirectory(excluded, Settings.Default.LR2CustomFolderOutputBaseDir);
        AddNormalizedDirectory(excluded, Settings.Default.LR2CustomFolderOutputBaseDirRootType);
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
            directories.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
        return new OwnedDigestMutationWindowScope(this);
    }

    private void EndOwnedDigestMutationWindow()
    {
        Interlocked.Decrement(ref ownedDigestMutationWindowDepth);
        InvalidatePlaylistSummaryOwnedHashSnapshot();
        InvalidatePlaylistLibraryResolveIndexSnapshot();
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

    private sealed class OwnedDigestMutationWindowScope(BMSLibrary owner) : IDisposable
    {
        private BMSLibrary owner = owner;

        public void Dispose()
        {
            BMSLibrary currentOwner = Interlocked.Exchange(ref owner, null);
            currentOwner?.EndOwnedDigestMutationWindow();
        }
    }

    private void InvalidateOwnedChartCollection()
    {
        lock (lockOwnedChartCollection)
        {
            ownedChartCollection = new OwnedChartCollectionState();
            ownedChartCollectionInitialized = false;
            ownedChartCollectionBmsStorageRowsVersion = -1;
            ownedChartCollectionBmsonStorageRowsVersion = -1;
        }
    }

    private int NotifyOwnedChartCollectionChanged()
    {
        int version = Interlocked.Increment(ref ownedChartCollectionVersion);
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
        public OwnedChartCollectionStorageMutation StorageMutation { get; } = new();

        public List<LibraryChartDigestChange> DigestChanges { get; } = [];

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

        public int OwnedCollectionVersion { get; set; }

        public int NormalLibraryRefreshNotificationVersion { get; set; }

        public ResourceHealthIndexMutation ResourceHealthMutation { get; } = new();

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

    private sealed class ResourceHealthIndexMutation
    {
        public List<ChartFile> UpdatedTargets { get; } = [];

        public List<ChartFile> RemovedTargets { get; } = [];

        public ResourceMaintenanceTargetSet FullOwnedTargetSet { get; set; }

        public int? DeltaBaseResourceHealthInputVersion { get; set; }

        public int? DeltaTargetResourceHealthInputVersion { get; set; }

        public bool Invalidate { get; set; }

        public bool RebuildFull { get; set; }

        public bool Defer { get; set; }

        public bool InvalidateIfDeltaFails { get; set; }

        public int UpdateTargetCount => UpdatedTargets.Count + RemovedTargets.Count;

        public bool HasDeltaTargets => UpdatedTargets.Count > 0 || RemovedTargets.Count > 0;

        public bool HasChanges => Invalidate || RebuildFull || Defer || HasDeltaTargets;
    }

    private sealed class ResourceHealthIndexDispatchResult
    {
        public ResourceHealthIndexSnapshot Snapshot { get; set; }

        public bool DeltaApplied { get; set; }

        public bool Deferred { get; set; }

        public bool FullRebuilt { get; set; }

        public long IndexMs { get; set; }
    }

    private sealed class InstallDestinationRuntimeStateMutation
    {
        public List<LibraryChartPathChange> PathChanges { get; } = [];

        public List<ChartFile> AppliedCharts { get; } = [];

        public bool PruneToCurrentOwnedCharts { get; set; }

        public bool HasStateChanges => PathChanges.Count > 0 || AppliedCharts.Count > 0;

        public bool HasChanges => HasStateChanges || PruneToCurrentOwnedCharts;
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

    private PlaylistSummaryOwnedHashSnapshot GetPlaylistSummaryOwnedHashSnapshot(out bool cacheHit, out int staleRetryCount)
    {
        PlaylistSummaryOwnedHashSnapshot snapshot;
        staleRetryCount = 0;
        while (true)
        {
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
                WaitForOwnedDigestMutationWindowIdle();
                staleRetryCount++;
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            OwnedChartHashIndexSnapshot ownedHashSnapshot;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                ownedHashSnapshot = CreateOwnedHashIndexSnapshotUnsafe(out storageRowsVersion);
                ownedCollectionVersion = OwnedChartCollectionVersion;
            }
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
            && Volatile.Read(ref bmsStorageRowsVersion) == snapshot.BmsRowsVersion
            && Volatile.Read(ref bmsonStorageRowsVersion) == snapshot.BmsonRowsVersion;
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
            && Volatile.Read(ref bmsStorageRowsVersion) == snapshot.BmsRowsVersion
            && Volatile.Read(ref bmsonStorageRowsVersion) == snapshot.BmsonRowsVersion;
    }

    private bool IsStorageRowsVersionCurrent(StorageRowsVersionSnapshot storageRowsVersion)
    {
        return Volatile.Read(ref bmsStorageRowsVersion) == storageRowsVersion.BmsRowsVersion
            && Volatile.Read(ref bmsonStorageRowsVersion) == storageRowsVersion.BmsonRowsVersion;
    }

    private OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshotUnsafe()
    {
        return CreateOwnedHashIndexSnapshotUnsafe(out _);
    }

    private OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshotUnsafe(out StorageRowsVersionSnapshot storageRowsVersion)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockStorageRowsVersion)
        {
            StorageRowsVersionSnapshot currentVersion = CreateCurrentStorageRowsVersionSnapshotUnsafe();
            lock (lockOwnedChartCollection)
            {
                if (!ownedChartCollectionInitialized
                    || ownedChartCollectionBmsStorageRowsVersion != currentVersion.BmsRowsVersion
                    || ownedChartCollectionBmsonStorageRowsVersion != currentVersion.BmsonRowsVersion)
                {
                    throw new InvalidOperationException("Owned chart collection storage row version is not current.");
                }
                storageRowsVersion = currentVersion;
                return ownedChartCollection.CreateOwnedHashIndexSnapshot();
            }
        }
    }

    private List<ChartFile> CreateOwnedChartInfoFullBackfillTargetSnapshot()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateSnapshot(
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false);
        }
    }

    private ILibraryChartCanonicalLookup CreateOwnedCanonicalChartLookupUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateCanonicalChartLookupSnapshot();
        }
    }

    private List<LibraryChartRef> CreateOwnedRealPathChartRefsUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateLibraryChartRefsUnderRealPath(directoryPath);
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
                status = ownedChartCollection.IsLibraryChartRefIndexSnapshotInitialized ? "cached" : "built";
                snapshot = ownedChartCollection.CreateLibraryChartRefIndexSnapshot();
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
        string status;
        InstallDestinationOverlayChartRefSnapshot snapshot;
        lock (installDestinationRuntimeStatesLock)
        {
            status = installDestinationOverlayChartRefSnapshot == null ? "built" : "cached";
            snapshot = installDestinationOverlayChartRefSnapshot ??= InstallDestinationOverlayChartRefSnapshot
                .FromCharts(CreateCurrentInstallDestinationCleanupChartsUnsafe());
        }
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
                return ownedChartCollection.CountLibraryChartRefsUnderRealPath(directoryPath) > 0;
            }
        }
    }

    private List<string> CreateOwnedRealPathChartDirectoriesUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateChartDirectoriesUnderRealPath(directoryPath);
        }
    }

    private bool TryCreateOwnedChartRefsForPathsUnsafe(IEnumerable<string> paths, out List<LibraryChartRef> chartRefs)
    {
        lock (lockOwnedChartCollection)
        {
            if (!ownedChartCollectionInitialized)
            {
                chartRefs = null;
                return false;
            }
            chartRefs = ownedChartCollection.CreateLibraryChartRefsForPaths(paths);
            return true;
        }
    }

    private bool TryScanOwnedChartRefsForPathsUnsafe(IEnumerable<string> paths, out List<LibraryChartRef> chartRefs)
    {
        lock (lockOwnedChartCollection)
        {
            if (!ownedChartCollectionInitialized)
            {
                chartRefs = null;
                return false;
            }
            chartRefs = ownedChartCollection.CreateLibraryChartRefsForPathsByScan(paths);
            return true;
        }
    }

    private ChartStorageTargetSet CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateStorageTargetsForSubtreeDirectory(directoryPath);
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
                if (!ownedChartCollectionInitialized
                    || ownedChartCollectionBmsStorageRowsVersion != currentVersion.BmsRowsVersion
                    || ownedChartCollectionBmsonStorageRowsVersion != currentVersion.BmsonRowsVersion)
                {
                    throw new InvalidOperationException("Owned chart collection storage row version is not current.");
                }
                storageRowsVersion = currentVersion;
                refs = ownedChartCollection.CreatePlaylistLibraryResolveRefSnapshot(cancellationToken.ThrowIfCancellationRequested);
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
            return ownedChartCollection.CreateChartInfoHydrationOwnerSummary(
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
            return ownedChartCollection.CreateSnapshotForDirectChildDirectories(
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
            return ownedChartCollection.CreateSnapshotForMd5Hashes(
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
            return ownedChartCollection.CreateSnapshotForPaths(
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
            return ownedChartCollection.CreateBmsSnapshot(
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
            return ownedChartCollection.CreateDuplicateChartRowSnapshot();
        }
    }

    private OwnedChartStorageOwnerView CreateOwnedChartStorageOwnerViewUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateStorageOwnerView();
        }
    }

    internal OwnedChartStorageOwnerView CreateNormalLibrarySourceStorageOwnerView()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return ownedChartCollection.CreateNormalLibrarySourceStorageOwnerView();
            }
        }
    }

    private InstalledChartLookupIndexState CreateOwnedInstalledChartLookupIndexStateUnsafe(out int bmsCount, out int bmsonCount)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateInstalledChartLookupIndexState(out bmsCount, out bmsonCount);
        }
    }

    private PrimaryHashLookupState CreateOwnedInstalledPrimaryHashLookupStateUnsafe(out int bmsCount, out int bmsonCount)
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreatePrimaryHashLookupState(out bmsCount, out bmsonCount);
        }
    }

    private HashSet<string> CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe()
    {
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.CreateInstallDestinationRuntimeStateKeySnapshot();
        }
    }

    internal HashSet<string> CreateOwnedChartRuntimeStatePrimaryKeySnapshot()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return ownedChartCollection.CreateChartRuntimeStatePrimaryKeySnapshot();
            }
        }
    }

    private void EnsureOwnedChartCollectionBuiltUnsafe()
    {
        while (true)
        {
            CaptureStorageRowsForOwnedCollectionUnsafe(
                out List<BMSFile> bmsFiles,
                out List<LR2SongDBExtended.bmson_song> bmsonSongs,
                out int bmsRowsVersion,
                out int bmsonRowsVersion);
            lock (lockOwnedChartCollection)
            {
                if (ownedChartCollectionInitialized
                    && ownedChartCollectionBmsStorageRowsVersion == bmsRowsVersion
                    && ownedChartCollectionBmsonStorageRowsVersion == bmsonRowsVersion)
                {
                    return;
                }
            }

            OwnedChartCollectionState rebuiltCollection = OwnedChartCollectionState.FromStorageRows(
                bmsFiles,
                bmsonSongs,
                out OwnedChartStorageRowFilterSummary filterSummary);
            LogOwnedChartCollectionSkippedRows("build", filterSummary);
            lock (lockStorageRowsVersion)
            {
                if (bmsRowsVersion != bmsStorageRowsVersion
                    || bmsonRowsVersion != bmsonStorageRowsVersion)
                {
                    continue;
                }
                lock (lockOwnedChartCollection)
                {
                    if (ownedChartCollectionInitialized
                        && ownedChartCollectionBmsStorageRowsVersion == bmsRowsVersion
                        && ownedChartCollectionBmsonStorageRowsVersion == bmsonRowsVersion)
                    {
                        return;
                    }
                    ownedChartCollection = rebuiltCollection;
                    ownedChartCollectionInitialized = true;
                    SetOwnedChartCollectionStorageRowsVersionUnsafe(bmsRowsVersion, bmsonRowsVersion);
                    return;
                }
            }
        }
    }

    private void ApplyOwnedChartCollectionMutation(OwnedChartCollectionStorageMutation mutation, StorageRowsVersionSnapshot storageRowsVersion)
    {
        if (mutation == null)
        {
            return;
        }
        lock (lockOwnedChartCollection)
        {
            if (!ownedChartCollectionInitialized)
            {
                return;
            }
            if (ownedChartCollectionBmsStorageRowsVersion != storageRowsVersion.PreviousBmsRowsVersion
                || ownedChartCollectionBmsonStorageRowsVersion != storageRowsVersion.PreviousBmsonRowsVersion)
            {
                ownedChartCollection = new OwnedChartCollectionState();
                ownedChartCollectionInitialized = false;
                SetOwnedChartCollectionStorageRowsVersionUnsafe(-1, -1);
                return;
            }
            if (mutation.RemoveRequests.Count > 0)
            {
                ownedChartCollection.RemoveChartRequests(mutation.RemoveRequests);
            }
            if (mutation.PathChanges.Count > 0)
            {
                ownedChartCollection.ApplyPathChanges(mutation.PathChanges);
            }
            if (mutation.AddedCount > 0)
            {
                ownedChartCollection.UpsertStorageRows(mutation.AddedBmsFiles, mutation.AddedBmsonSongs);
            }
            SetOwnedChartCollectionStorageRowsVersionUnsafe(storageRowsVersion.BmsRowsVersion, storageRowsVersion.BmsonRowsVersion);
        }
    }

    private void ApplyOwnedChartCollectionDigestChanges(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        List<LibraryChartDigestChange> changes = [.. (digestChanges ?? []).Where(change => change?.Md5Changed == true)];
        if (changes.Count == 0)
        {
            return;
        }
        lock (lockOwnedChartCollection)
        {
            if (!ownedChartCollectionInitialized)
            {
                return;
            }
            ownedChartCollection.ApplyDigestChanges(changes);
        }
    }

    private void ApplyOwnedChartCollectionStorageReplacement(StorageRowsSnapshot storageRows)
    {
        lock (lockOwnedChartCollection)
        {
            if (!ownedChartCollectionInitialized)
            {
                return;
            }
        }

        OwnedChartCollectionState replacementCollection = OwnedChartCollectionState.FromStorageRows(
            storageRows.BmsFiles,
            storageRows.BmsonSongs,
            out OwnedChartStorageRowFilterSummary filterSummary);
        LogOwnedChartCollectionSkippedRows("replace", filterSummary);
        lock (lockStorageRowsVersion)
        {
            if (storageRows.BmsRowsVersion != bmsStorageRowsVersion
                || storageRows.BmsonRowsVersion != bmsonStorageRowsVersion)
            {
                return;
            }
            lock (lockOwnedChartCollection)
            {
                if (!ownedChartCollectionInitialized)
                {
                    return;
                }
                ownedChartCollection = replacementCollection;
                ownedChartCollectionInitialized = true;
                SetOwnedChartCollectionStorageRowsVersionUnsafe(storageRows.BmsRowsVersion, storageRows.BmsonRowsVersion);
            }
        }
    }

    private void ApplyLibraryFileScanStorageMutation(SongTableFileCheckResult fileCheckResult, string reason)
    {
        if (fileCheckResult == null)
        {
            return;
        }

        OwnedChartCollectionMutationResult mutationResult;
        using (rwlockBMSFiles.GetWriterGuard())
        {
            bool removedPayloadAvailable = TryCreateOwnedFileScanRemovedStorageOwnerIdentityChartsUnsafe(fileCheckResult, out List<ChartFile> removedCharts);
            ResourceHealthInputMutationScope resourceHealthMutation = BeginResourceHealthInputMutation();
            try
            {
                mutationResult = BuildOwnedChartCollectionFileScanMutationResult(
                    fileCheckResult,
                    removedCharts,
                    removedPayloadAvailable,
                    resourceHealthMutation.BaseIndexCurrent);
                PublishOwnedCollectionChangeNotification(mutationResult);
                try
                {
                    using (mutationResult.ResourceHealthIndexInvalidated ? SuppressResourceHealthIndexInvalidation() : null)
                    {
                        StorageRowsSnapshot storageRows = fileCheckResult.HasDbDiff
                            ? SetStorageRowsFromInternalMutationUnsafe(
                                fileCheckResult.NextFiles,
                                fileCheckResult.NextBmsonSongs)
                            : CreateStorageRowsSnapshotUnsafe();
                        libraryResourceIndex = fileCheckResult.NextResourceIndex ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult());
                        directoryResourceLookupCache = libraryResourceIndex.DirectoryLookupCache ?? new DirectoryResourceLookupCache();
                        if (fileCheckResult.HasDbDiff)
                        {
                            ApplyOwnedChartCollectionStorageReplacement(storageRows);
                        }
                    }
                }
                catch
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
                        ForceInvalidateResourceHealthIndex("file_scan_storage_failed");
                    }
                    if (mutationResult.InstallDestinationRuntimeStateMutation.HasChanges
                        || mutationResult.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
                    {
                        PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
                    }
                    InvalidateOwnedChartCollection();
                    ClearNormalLibraryRefreshNotification(mutationResult);
                    throw;
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }
        }

        DispatchOwnedChartCollectionMutation(mutationResult, CreateFileScanMutationReason(reason));
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
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(ApplyInstalledChartStorageTargets));
        OwnedChartCollectionMutationResult mutationResult = null;
        try
        {
            ResourceHealthInputMutationScope resourceHealthMutation = BeginResourceHealthInputMutation();
            try
            {
                mutationResult = BuildOwnedChartCollectionUpsertMutationResult(
                    addedTargets,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                PublishOwnedCollectionChangeNotification(mutationResult);
                using (mutationResult.ResourceHealthIndexInvalidated ? SuppressResourceHealthIndexInvalidation() : null)
                {
                    StorageRowsVersionSnapshot storageRowsVersion = ApplyInstalledChartStorageRowsUnsafe(addedTargets);
                    ApplyOwnedChartCollectionMutation(mutationResult.StorageMutation, storageRowsVersion);
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
            SyncLr2NormalFoldersForOwnedMutation(mutationResult.StorageMutation, lookupReason ?? "install_package");
            DispatchOwnedChartCollectionMutation(mutationResult, lookupReason);
        }
        catch
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
                PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
            }
            if (mutationResult?.OwnedCollectionChanged == true)
            {
                PublishOwnedCollectionChangeNotification(mutationResult);
            }
            if (mutationResult?.ResourceHealthMutation.HasChanges == true)
            {
                ForceInvalidateResourceHealthIndex("install_package_failed");
            }
            InvalidateOwnedChartCollection();
            if (mutationResult != null)
            {
                ClearNormalLibraryRefreshNotification(mutationResult);
            }
            throw;
        }
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionFileScanMutationResult(
        SongTableFileCheckResult fileCheckResult,
        List<ChartFile> removedCharts,
        bool removedPayloadAvailable,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        var storageMutation = new OwnedChartCollectionStorageMutation();
        if (removedPayloadAvailable)
        {
            storageMutation.RemoveRequests.AddRange((removedCharts ?? [])
                .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
                .Where(request => request != null));
        }
        storageMutation.AddAddedTargets(ChartStorageTargetSet.FromRows(fileCheckResult.AddedFiles, fileCheckResult.AddedBmsonSongs));
        bool bmsRowsChanged = fileCheckResult.DeletedPaths.Count > 0 || fileCheckResult.AddedFiles.Count > 0;
        bool bmsonRowsChanged = fileCheckResult.DeletedBmsonPaths.Count > 0 || fileCheckResult.AddedBmsonSongs.Count > 0;
        bool storageRowsChanged = bmsRowsChanged || bmsonRowsChanged || fileCheckResult.HasDbDiff;
        bool resourceHealthShouldInvalidate = fileCheckResult.HasDbDiff || (resourceHealthIndexCurrentAtBase ?? IsResourceHealthIndexCurrent());
        bool fileScanPresentationChanged = fileCheckResult.HasDbDiff || resourceHealthShouldInvalidate;

        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupFileScanMutation(storageMutation, removedPayloadAvailable, fileCheckResult),
            InstallEstimationMetadataProfileCacheInvalidated = fileCheckResult.HasDbDiff,
            AddedCount = storageMutation.AddedCount,
            RemovedCount = removedPayloadAvailable ? storageMutation.RemovedCount : fileCheckResult.DeletedPaths.Count + fileCheckResult.DeletedBmsonPaths.Count,
            MovedCount = storageMutation.MovedCount,
            ParentFolderInvalidated = fileCheckResult.HasDbDiff,
            DuplicateCacheInvalidated = fileCheckResult.HasDbDiff,
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

    private bool TryCreateOwnedFileScanRemovedStorageOwnerIdentityChartsUnsafe(
        SongTableFileCheckResult fileCheckResult,
        out List<ChartFile> removedCharts)
    {
        removedCharts = [];
        if (fileCheckResult == null)
        {
            return true;
        }
        lock (lockStorageRowsVersion)
        {
            int bmsRowsVersion = bmsStorageRowsVersion;
            int bmsonRowsVersion = bmsonStorageRowsVersion;
            lock (lockOwnedChartCollection)
            {
                if (!ownedChartCollectionInitialized
                    || ownedChartCollectionBmsStorageRowsVersion != bmsRowsVersion
                    || ownedChartCollectionBmsonStorageRowsVersion != bmsonRowsVersion)
                {
                    return false;
                }
                removedCharts = ownedChartCollection.CreateFileScanRemovedStorageOwnerIdentityCharts(
                    fileCheckResult.DeletedPaths,
                    fileCheckResult.DeletedBmsonPaths,
                    fileCheckResult.NextFiles,
                    fileCheckResult.NextBmsonSongs);
                return true;
            }
        }
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupFileScanMutation(
        OwnedChartCollectionStorageMutation storageMutation,
        bool removedPayloadAvailable,
        SongTableFileCheckResult fileCheckResult)
    {
        InstalledChartLookupMutation mutation = BuildInstalledChartLookupMutation(storageMutation, null);
        if (!removedPayloadAvailable && fileCheckResult?.HasDbDiff == true)
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

    private static string CreateFileScanMutationReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? "file_scan"
            : "file_scan_" + reason;
    }

    private StorageRowsVersionSnapshot ApplyInstalledChartStorageRowsUnsafe(ChartStorageTargetSet addedTargets)
    {
        lock (lockStorageRowsVersion)
        {
            int previousBmsRowsVersion = bmsStorageRowsVersion;
            int previousBmsonRowsVersion = bmsonStorageRowsVersion;
            if (addedTargets.BmsFiles.Count > 0)
            {
                var addedBmsPathSet = new HashSet<string>(
                    addedTargets.BmsFiles.Select(file => CreateOwnedPathKey(file?.path)).Where(path => !string.IsNullOrWhiteSpace(path)),
                    StringComparer.OrdinalIgnoreCase);
                _BMSFiles = [.. (_BMSFiles ?? []).Where(file => file != null && !addedBmsPathSet.Contains(CreateOwnedPathKey(file.path))), .. addedTargets.BmsFiles];
                IncrementBmsStorageRowsVersion();
            }
            if (addedTargets.BmsonSongs.Count > 0)
            {
                var nextBmsonByPath = (_BmsonSongs ?? [])
                    .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                    .GroupBy(song => CreateOwnedPathKey(song.path), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedTargets.BmsonSongs)
                {
                    nextBmsonByPath[CreateOwnedPathKey(addedBmsonSong.path)] = addedBmsonSong;
                }
                _BmsonSongs = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
                IncrementBmsonStorageRowsVersion();
            }
            return CreateStorageRowsVersionSnapshotUnsafe(previousBmsRowsVersion, previousBmsonRowsVersion);
        }
    }

    private StorageRowsSnapshot SetStorageRowsFromInternalMutationUnsafe(
        List<BMSFile> files,
        List<LR2SongDBExtended.bmson_song> songs)
    {
        lock (lockStorageRowsVersion)
        {
            _BMSFiles = files ?? [];
            _BmsonSongs = songs ?? [];
            MarkDuplicateWarningFullClearPending();
            IncrementBmsStorageRowsVersion();
            IncrementBmsonStorageRowsVersion();
            return CreateStorageRowsSnapshotUnsafe();
        }
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
            bmsFiles = _BMSFiles ?? [];
            bmsonSongs = _BmsonSongs ?? [];
            bmsRowsVersion = bmsStorageRowsVersion;
            bmsonRowsVersion = bmsonStorageRowsVersion;
        }
    }

    private StorageRowsVersionSnapshot CaptureStorageRowsVersionUnsafe()
    {
        lock (lockStorageRowsVersion)
        {
            return CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
    }

    private StorageRowsVersionSnapshot SetBmsStorageRowsCoreUnsafe(List<BMSFile> files)
    {
        lock (lockStorageRowsVersion)
        {
            _BMSFiles = files ?? [];
            MarkDuplicateWarningFullClearPending();
            IncrementBmsStorageRowsVersion();
            return CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
    }

    private StorageRowsVersionSnapshot SetBmsonStorageRowsCoreUnsafe(List<LR2SongDBExtended.bmson_song> songs)
    {
        lock (lockStorageRowsVersion)
        {
            _BmsonSongs = songs ?? [];
            IncrementBmsonStorageRowsVersion();
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

    private void IncrementBmsStorageRowsVersion()
    {
        Interlocked.Increment(ref bmsStorageRowsVersion);
    }

    private void IncrementBmsonStorageRowsVersion()
    {
        Interlocked.Increment(ref bmsonStorageRowsVersion);
    }

    private StorageRowsVersionSnapshot CreateCurrentStorageRowsVersionSnapshotUnsafe()
    {
        return new StorageRowsVersionSnapshot(bmsStorageRowsVersion, bmsonStorageRowsVersion);
    }

    private StorageRowsVersionSnapshot CreateStorageRowsVersionSnapshotUnsafe(int previousBmsRowsVersion, int previousBmsonRowsVersion)
    {
        return new StorageRowsVersionSnapshot(
            previousBmsRowsVersion,
            previousBmsonRowsVersion,
            bmsStorageRowsVersion,
            bmsonStorageRowsVersion);
    }

    private StorageRowsSnapshot CreateStorageRowsSnapshotUnsafe()
    {
        return new StorageRowsSnapshot(_BMSFiles ?? [], _BmsonSongs ?? [], bmsStorageRowsVersion, bmsonStorageRowsVersion);
    }

    private void SetOwnedChartCollectionStorageRowsVersionUnsafe(int bmsRowsVersion, int bmsonRowsVersion)
    {
        ownedChartCollectionBmsStorageRowsVersion = bmsRowsVersion;
        ownedChartCollectionBmsonStorageRowsVersion = bmsonRowsVersion;
    }

    private readonly struct StorageRowsVersionSnapshot
    {
        internal StorageRowsVersionSnapshot(int bmsRowsVersion, int bmsonRowsVersion)
            : this(bmsRowsVersion, bmsonRowsVersion, bmsRowsVersion, bmsonRowsVersion)
        {
        }

        internal StorageRowsVersionSnapshot(
            int previousBmsRowsVersion,
            int previousBmsonRowsVersion,
            int bmsRowsVersion,
            int bmsonRowsVersion)
        {
            PreviousBmsRowsVersion = previousBmsRowsVersion;
            PreviousBmsonRowsVersion = previousBmsonRowsVersion;
            BmsRowsVersion = bmsRowsVersion;
            BmsonRowsVersion = bmsonRowsVersion;
        }

        internal int PreviousBmsRowsVersion { get; }

        internal int PreviousBmsonRowsVersion { get; }

        internal int BmsRowsVersion { get; }

        internal int BmsonRowsVersion { get; }
    }

    private readonly struct StorageRowsSnapshot
    {
        internal StorageRowsSnapshot(
            List<BMSFile> bmsFiles,
            List<LR2SongDBExtended.bmson_song> bmsonSongs,
            int bmsRowsVersion,
            int bmsonRowsVersion)
        {
            BmsFiles = bmsFiles ?? [];
            BmsonSongs = bmsonSongs ?? [];
            BmsRowsVersion = bmsRowsVersion;
            BmsonRowsVersion = bmsonRowsVersion;
        }

        internal List<BMSFile> BmsFiles { get; }

        internal List<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

        internal int BmsRowsVersion { get; }

        internal int BmsonRowsVersion { get; }
    }

    private readonly struct ResourceMaintenanceTargetSet
    {
        private readonly List<ChartFile> charts;

        private ResourceMaintenanceTargetSet(
            List<ChartFile> charts,
            bool isFullOwned,
            StorageRowsVersionSnapshot? storageRowsVersion,
            int? ownedCollectionVersion,
            int? resourceHealthInputVersion)
        {
            this.charts = charts ?? [];
            IsSpecified = true;
            IsFullOwned = isFullOwned;
            StorageRowsVersion = storageRowsVersion;
            OwnedCollectionVersion = ownedCollectionVersion;
            ResourceHealthInputVersion = resourceHealthInputVersion;
        }

        internal List<ChartFile> Charts => charts ?? [];

        internal bool IsSpecified { get; }

        internal bool IsFullOwned { get; }

        internal StorageRowsVersionSnapshot? StorageRowsVersion { get; }

        internal int? OwnedCollectionVersion { get; }

        internal int? ResourceHealthInputVersion { get; }

        internal int Count => Charts.Count;

        internal static ResourceMaintenanceTargetSet ForSubset(List<ChartFile> charts)
        {
            return new ResourceMaintenanceTargetSet(charts, false, null, null, null);
        }

        internal static ResourceMaintenanceTargetSet ForFullOwned(
            List<ChartFile> charts,
            StorageRowsVersionSnapshot storageRowsVersion,
            int ownedCollectionVersion,
            int resourceHealthInputVersion)
        {
            return new ResourceMaintenanceTargetSet(
                charts,
                true,
                storageRowsVersion,
                ownedCollectionVersion,
                resourceHealthInputVersion);
        }

        internal ResourceMaintenanceTargetSet WithCharts(List<ChartFile> charts)
        {
            return IsSpecified
                ? new ResourceMaintenanceTargetSet(
                    charts,
                    IsFullOwned,
                    StorageRowsVersion,
                    OwnedCollectionVersion,
                    ResourceHealthInputVersion)
                : default;
        }

        internal ResourceMaintenanceTargetSet WithOwnedCollectionVersion(int ownedCollectionVersion)
        {
            return IsSpecified
                ? new ResourceMaintenanceTargetSet(
                    Charts,
                    IsFullOwned,
                    StorageRowsVersion,
                    ownedCollectionVersion,
                    ResourceHealthInputVersion)
                : default;
        }

        internal ResourceMaintenanceTargetSet WithResourceHealthInputVersion(int resourceHealthInputVersion)
        {
            return IsSpecified
                ? new ResourceMaintenanceTargetSet(
                    Charts,
                    IsFullOwned,
                    StorageRowsVersion,
                    OwnedCollectionVersion,
                    resourceHealthInputVersion)
                : default;
        }
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
        result.InstallDestinationRuntimeStateMutation.AppliedCharts.AddRange(CreateInstallDestinationChangedChartSnapshots(delta, storageMutation.PathChanges));
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
                if (!ownedChartCollectionInitialized
                    || ownedChartCollectionBmsStorageRowsVersion != bmsRowsVersion
                    || ownedChartCollectionBmsonStorageRowsVersion != bmsonRowsVersion)
                {
                    resolvedRequests = [.. removeRequests
                        .Where(request => request?.Mode == OwnedChartRemoveMode.OwnerReference
                            && (request.BmsOwner != null || request.BmsonOwner != null))];
                }
                else
                {
                    resolvedRequests = ownedChartCollection.ResolveCurrentRemoveRequests(removeRequests);
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
        bool resourceHealthIndexCurrent = resourceHealthIndexCurrentAtBase ?? IsResourceHealthIndexCurrent();
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
        result.ResourceHealthMutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion ?? Volatile.Read(ref resourceHealthInputVersion);
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

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionDigestMutationResult(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        bool resourceHealthIndexInvalidated = true)
    {
        List<LibraryChartDigestChange> changes = [.. (digestChanges ?? []).Where(change => change?.HasDigestChange == true)];
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

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionPotentialDigestMutationResult(
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
        ResourceHealthIndexMutation resourceHealthMutation,
        bool workflowHasUpdates)
    {
        var result = new OwnedChartCollectionMutationResult();
        CopyResourceHealthIndexMutation(resourceHealthMutation, result.ResourceHealthMutation);
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

    private static OwnedChartCollectionMutationResult BuildResourceHealthWarningPresentationMutationResult(
        IEnumerable<ChartFile> updatedTargets,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null)
    {
        var result = new OwnedChartCollectionMutationResult
        {
            WarningPresentationChanged = true
        };
        result.ResourceHealthMutation.UpdatedTargets.AddRange((updatedTargets ?? []).Where(chart => chart != null));
        result.ResourceHealthMutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion;
        result.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
        result.ResourceHealthMutation.InvalidateIfDeltaFails = true;
        return result;
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
            UpdateInstallDestinationRuntimeStates(result.InstallDestinationRuntimeStateMutation);
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DigestChangedCount > 0)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            ApplyOwnedChartCollectionDigestChanges(result.DigestChanges);
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
            AlignResourceHealthFullOwnedTargetVersionAfterOwnedCollectionNotification(result);
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
            result.ResourceHealthDispatchResult = DispatchResourceHealthIndexMutation(result.ResourceHealthMutation, reason);
            resourceHealthMs += StopPerformanceStepStopwatch(stepStopwatch);
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

    private static void AlignResourceHealthFullOwnedTargetVersionAfterOwnedCollectionNotification(OwnedChartCollectionMutationResult result)
    {
        ResourceHealthIndexMutation mutation = result?.ResourceHealthMutation;
        if (result?.OwnedCollectionChanged != true
            || result.OwnedCollectionVersion <= 0
            || mutation?.RebuildFull != true
            || !HasFullOwnedResourceHealthTargetVersion(mutation.FullOwnedTargetSet))
        {
            return;
        }

        mutation.FullOwnedTargetSet = mutation.FullOwnedTargetSet.WithOwnedCollectionVersion(result.OwnedCollectionVersion);
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
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionDigestMutationResult(
            digestChanges,
            resourceHealthIndexInvalidated);
        DispatchOwnedChartCollectionMutation(mutationResult, reason);
    }

    private void DispatchOwnedPotentialDigestChanges(
        IEnumerable<ChartFile> charts,
        string reason,
        bool resourceHealthIndexInvalidated = true)
    {
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionPotentialDigestMutationResult(
            charts,
            resourceHealthIndexInvalidated);
        DispatchOwnedChartCollectionMutation(mutationResult, reason);
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

    private void DispatchMaintenanceHydrationResult(
        MaintenanceTableHydrationResult hydrationResult,
        ResourceMaintenanceTargetSet fullOwnedTargets)
    {
        OwnedChartCollectionMutationResult mutationResult = BuildMaintenanceHydrationMutationResult(fullOwnedTargets);
        DispatchOwnedChartCollectionMutation(mutationResult, "maintenance_hydration");
        if (hydrationResult != null)
        {
            hydrationResult.ResourceHealthIndexMs = mutationResult.ResourceHealthDispatchResult?.IndexMs ?? 0L;
        }
    }

    private static OwnedChartCollectionMutationResult BuildMaintenanceHydrationMutationResult(
        ResourceMaintenanceTargetSet fullOwnedTargets)
    {
        var result = new OwnedChartCollectionMutationResult
        {
            WarningPresentationChanged = true,
            MaintenancePresentationChanged = true
        };
        result.ResourceHealthMutation.RebuildFull = true;
        result.ResourceHealthMutation.FullOwnedTargetSet = fullOwnedTargets;
        return result;
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
        result.OwnedCollectionVersion = NotifyOwnedChartCollectionChanged();
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

    private List<ChartFile> OverlayInstallDestinationRuntimeStates(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Select(OverlayInstallDestinationRuntimeState).Where(chart => chart != null)];
    }

    private List<ChartFile> CreateCurrentInstallDestinationCleanupCharts()
    {
        lock (installDestinationRuntimeStatesLock)
        {
            return CreateCurrentInstallDestinationCleanupChartsUnsafe();
        }
    }

    private List<ChartFile> CreateCurrentInstallDestinationCleanupChartsUnsafe()
    {
        return [.. installDestinationRuntimeStatesByKey.Values
            .Distinct()
            .Select(entry => entry.CreateChartSnapshot())
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
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

    private void UpdateInstallDestinationRuntimeStates(InstallDestinationRuntimeStateMutation mutation)
    {
        if (mutation == null || !mutation.HasStateChanges)
        {
            return;
        }
        lock (installDestinationRuntimeStatesLock)
        {
            foreach (LibraryChartPathChange pathChange in mutation.PathChanges)
            {
                MoveInstallDestinationRuntimeState(pathChange);
            }

            foreach (ChartFile chart in mutation.AppliedCharts)
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
            InvalidateInstallDestinationOverlayChartRefSnapshotUnsafe();
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
            return CanApplyTo(chart?.GetBmsStorageOwner(), chart?.GetBmsonStorageOwner(), requireOwnerMatch);
        }

        internal bool CanApplyTo(
            BMSFile currentBmsOwner,
            LR2SongDBExtended.bmson_song currentBmsonOwner,
            bool requireOwnerMatch)
        {
            if (State?.HasState != true)
            {
                return false;
            }
            if (!requireOwnerMatch)
            {
                return true;
            }

            if (bmsOwner != null || currentBmsOwner != null)
            {
                return ReferenceEquals(bmsOwner, currentBmsOwner);
            }

            return (bmsonOwner != null || currentBmsonOwner != null)
                && ReferenceEquals(bmsonOwner, currentBmsonOwner);
        }

        internal ChartFile CreateChartSnapshot()
        {
            if (State?.HasInstallDestinationState != true)
            {
                return null;
            }

            ChartFile source = bmsOwner != null
                ? ChartFileProjection.FromBmsStorageOwnerIdentity(bmsOwner)
                : bmsonOwner != null
                    ? ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonOwner)
                    : null;
            return ChartFileProjection.WithTransientState(source, State, includeWarningSnapshot: false);
        }
    }

    private List<ChartFile> CreateInstallDestinationChangedChartSnapshots(
        LibraryMutationDelta delta,
        IReadOnlyCollection<LibraryChartPathChange> pathChanges)
    {
        var chartsByKey = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in delta?.CreateAppliedInstallDestinationChartSnapshots() ?? [])
        {
            AddInstallDestinationChangedChart(chartsByKey, chart);
        }

        foreach (ChartFile chart in CreateMovedInstallDestinationRuntimeStateSnapshots(pathChanges))
        {
            AddInstallDestinationChangedChart(chartsByKey, chart);
        }

        return [.. chartsByKey.Values];
    }

    private IEnumerable<ChartFile> CreateMovedInstallDestinationRuntimeStateSnapshots(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        if (pathChanges == null)
        {
            yield break;
        }

        foreach (LibraryChartPathChange pathChange in pathChanges)
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

    private void InvalidateInstallDestinationOverlayChartRefSnapshotUnsafe()
    {
        installDestinationOverlayChartRefSnapshot = null;
    }

    private void PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts()
    {
        lock (installDestinationRuntimeStatesLock)
        {
            // Startup assigns the full owned chart set before any runtime install
            // destination overlay exists. Avoid building 210k+ ChartFile projection
            // keys for that empty-cache case.
            if (installDestinationRuntimeStatesByKey.Count == 0)
            {
                return;
            }
        }

        HashSet<string> currentKeys = CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe();

        lock (installDestinationRuntimeStatesLock)
        {
            bool removedAny = false;
            foreach (string key in installDestinationRuntimeStatesByKey.Keys.ToList())
            {
                if (!currentKeys.Contains(key))
                {
                    installDestinationRuntimeStatesByKey.Remove(key);
                    removedAny = true;
                }
            }
            if (removedAny)
            {
                InvalidateInstallDestinationOverlayChartRefSnapshotUnsafe();
            }
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
            string fullPath = Path.GetFullPath(directory.Trim());
            string root = Path.GetPathRoot(fullPath);
            string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootTrimmed = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(rootTrimmed)
                && string.Equals(trimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase)
                ? root
                : trimmed;
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

    public IRSongInfo GetIRSongInfoCache(string md5orlr2bmsid, bool seaarchAggressively = false)
    {
        return irService.GetIRSongInfoCache(md5orlr2bmsid, seaarchAggressively, irClient, songInfoUrl);
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
        int resourceHealthInputVersion = Volatile.Read(ref this.resourceHealthInputVersion);
        EnsureOwnedChartCollectionBuiltUnsafe();
        List<ChartFile> targets;
        StorageRowsVersionSnapshot storageRowsVersion;
        int ownedCollectionVersion;
        lock (lockOwnedChartCollection)
        {
            targets = ownedChartCollection.CreateFullResourceMaintenanceTargetSnapshot();
            storageRowsVersion = new StorageRowsVersionSnapshot(
                ownedChartCollectionBmsStorageRowsVersion,
                ownedChartCollectionBmsonStorageRowsVersion);
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

    private void InvalidateResourceHealthIndex(string reason)
    {
        InvalidateResourceHealthIndexCore(reason, ignoreSuppression: false);
    }

    private void ForceInvalidateResourceHealthIndex(string reason)
    {
        InvalidateResourceHealthIndexCore(reason, ignoreSuppression: true);
    }

    private void InvalidateResourceHealthIndexCore(string reason, bool ignoreSuppression)
    {
        _ = reason;
        lock (resourceHealthIndexLock)
        {
            BumpResourceHealthInputVersionUnsafe();
            if (!ignoreSuppression && suppressResourceHealthIndexInvalidation > 0)
            {
                return;
            }
            Volatile.Write(ref resourceHealthIndexInvalidated, true);
        }
    }

    private void IncrementResourceHealthInputVersionUnsafe()
    {
        unchecked
        {
            resourceHealthInputVersion++;
        }
    }

    private void BumpResourceHealthInputVersionUnsafe()
    {
        unchecked
        {
            resourceHealthInputVersion += 2;
        }
    }

    private static bool IsStableResourceHealthInputVersion(int version)
    {
        return (version & 1) == 0;
    }

    private int GetCurrentStableResourceHealthInputVersion()
    {
        int version = Volatile.Read(ref resourceHealthInputVersion);
        return IsStableResourceHealthInputVersion(version) ? version : -1;
    }

    private IDisposable SuppressResourceHealthIndexInvalidation()
    {
        lock (resourceHealthIndexLock)
        {
            suppressResourceHealthIndexInvalidation++;
        }
        return new ResourceHealthIndexInvalidationSuppression(this);
    }

    private sealed class ResourceHealthIndexInvalidationSuppression(BMSLibrary owner) : IDisposable
    {
        private BMSLibrary owner = owner;

        public void Dispose()
        {
            if (owner != null)
            {
                lock (owner.resourceHealthIndexLock)
                {
                    owner.suppressResourceHealthIndexInvalidation = Math.Max(0, owner.suppressResourceHealthIndexInvalidation - 1);
                }
                owner = null;
            }
        }
    }

    private sealed class ResourceHealthIndexSnapshotState(ResourceHealthIndexSnapshot snapshot, int inputVersion)
    {
        public ResourceHealthIndexSnapshot Snapshot { get; } = snapshot ?? ResourceHealthIndexSnapshot.Empty;

        public int InputVersion { get; } = inputVersion;
    }

    private bool IsResourceHealthIndexCurrent()
    {
        return IsResourceHealthIndexStateCurrent(Volatile.Read(ref resourceHealthIndexState));
    }

    private bool IsResourceHealthIndexStateCurrent(ResourceHealthIndexSnapshotState state)
    {
        return state?.Snapshot != null
            && !Volatile.Read(ref resourceHealthIndexInvalidated)
            && IsStableCurrentResourceHealthInputVersion(state.InputVersion);
    }

    private bool IsStableCurrentResourceHealthInputVersion(int snapshotInputVersion)
    {
        int currentInputVersion = Volatile.Read(ref resourceHealthInputVersion);
        return snapshotInputVersion == currentInputVersion
            && IsStableResourceHealthInputVersion(currentInputVersion);
    }

    private ResourceHealthIndexSnapshot GetPublishedResourceHealthIndexSnapshotOrEmpty()
    {
        return Volatile.Read(ref resourceHealthIndexState)?.Snapshot ?? ResourceHealthIndexSnapshot.Empty;
    }

    private ResourceHealthInputMutationScope BeginResourceHealthInputMutation()
    {
        lock (resourceHealthIndexLock)
        {
            int baseInputVersion = resourceHealthInputVersion;
            bool baseIndexCurrent = resourceHealthInputMutationDepth == 0
                && resourceHealthIndexState?.InputVersion == baseInputVersion
                && !resourceHealthIndexInvalidated
                && IsStableResourceHealthInputVersion(baseInputVersion);
            if (resourceHealthInputMutationDepth == 0)
            {
                IncrementResourceHealthInputVersionUnsafe();
            }
            resourceHealthInputMutationDepth++;
            return new ResourceHealthInputMutationScope(this, baseInputVersion, baseIndexCurrent);
        }
    }

    private int EndResourceHealthInputMutation()
    {
        lock (resourceHealthIndexLock)
        {
            resourceHealthInputMutationDepth = Math.Max(0, resourceHealthInputMutationDepth - 1);
            if (resourceHealthInputMutationDepth == 0)
            {
                IncrementResourceHealthInputVersionUnsafe();
            }
            return resourceHealthInputVersion;
        }
    }

    private sealed class ResourceHealthInputMutationScope(BMSLibrary owner, int baseInputVersion, bool baseIndexCurrent) : IDisposable
    {
        private BMSLibrary owner = owner;

        public int BaseInputVersion { get; } = baseInputVersion;

        public bool BaseIndexCurrent { get; } = baseIndexCurrent;

        public int TargetInputVersion { get; private set; } = -1;

        public void Dispose()
        {
            if (owner != null)
            {
                TargetInputVersion = owner.EndResourceHealthInputMutation();
                owner = null;
            }
        }
    }

    private ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshot(string reason)
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
        if (IsResourceHealthIndexStateCurrent(currentState))
        {
            return currentState.Snapshot;
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
        return RebuildResourceHealthIndexSnapshotLocked(
            reason,
            default,
            out _);
    }

    private ResourceHealthIndexSnapshot RebuildResourceHealthIndexSnapshotLocked(
        string reason,
        ResourceMaintenanceTargetSet fullOwnedTargetSet,
        out bool staleFullOwnedTarget)
    {
        staleFullOwnedTarget = false;
        ResourceMaintenanceTargetSet targetSet = fullOwnedTargetSet;
        if (targetSet.IsSpecified && !HasFullOwnedResourceHealthTargetVersion(targetSet))
        {
            targetSet = default;
        }
        if (!targetSet.IsSpecified)
        {
            targetSet = CreateFullOwnedResourceMaintenanceTargetSet(reason);
        }
        List<ChartFile> targets = targetSet.Charts;
        int version = Interlocked.Increment(ref resourceHealthIndexVersionSeed);
        var snapshot = ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version);
        if (HasFullOwnedResourceHealthTargetVersion(targetSet))
        {
            lock (resourceHealthIndexLock)
            {
                if (!IsCurrentFullOwnedResourceHealthTargetVersion(targetSet))
                {
                    InvalidateResourceHealthIndexIfSnapshotInputIsStaleUnsafe();
                    staleFullOwnedTarget = true;
                    LogInstallPerformance("resource_health_index_full_target_stale reason=" + (reason ?? "unknown")
                        + " targetCount=" + targets.Count);
                    return GetPublishedResourceHealthIndexSnapshotOrEmpty();
                }
                PublishResourceHealthIndexSnapshotUnsafe(snapshot);
            }
        }
        else
        {
            lock (resourceHealthIndexLock)
            {
                PublishResourceHealthIndexSnapshotUnsafe(snapshot);
            }
        }
        LogInstallPerformance("resource_health_index_build reason=" + (reason ?? "unknown")
            + " version=" + snapshot.Version
            + " targetCount=" + snapshot.TargetCount
            + " needFix=" + snapshot.NeedFixCount
            + " ignored=" + snapshot.IgnoredCount
            + " buildMs=" + snapshot.BuildMs);
        return snapshot;
    }

    private static bool HasFullOwnedResourceHealthTargetVersion(ResourceMaintenanceTargetSet targetSet)
    {
        return targetSet.IsFullOwned
            && HasFullOwnedResourceHealthTargetVersion(
                targetSet.StorageRowsVersion,
                targetSet.OwnedCollectionVersion,
                targetSet.ResourceHealthInputVersion);
    }

    private static bool HasFullOwnedResourceHealthTargetVersion(
        StorageRowsVersionSnapshot? storageRowsVersion,
        int? ownedCollectionVersion,
        int? resourceHealthInputVersion)
    {
        return storageRowsVersion.HasValue
            && ownedCollectionVersion.HasValue
            && resourceHealthInputVersion.HasValue;
    }

    private bool IsCurrentFullOwnedResourceHealthTargetVersion(ResourceMaintenanceTargetSet targetSet)
    {
        return targetSet.IsFullOwned
            && IsCurrentFullOwnedResourceHealthTargetVersion(
                targetSet.StorageRowsVersion,
                targetSet.OwnedCollectionVersion,
                targetSet.ResourceHealthInputVersion);
    }

    private bool IsCurrentFullOwnedResourceHealthTargetVersion(
        StorageRowsVersionSnapshot? storageRowsVersion,
        int? ownedCollectionVersion,
        int? resourceHealthInputVersion)
    {
        return HasFullOwnedResourceHealthTargetVersion(storageRowsVersion, ownedCollectionVersion, resourceHealthInputVersion)
            && Volatile.Read(ref bmsStorageRowsVersion) == storageRowsVersion.Value.BmsRowsVersion
            && Volatile.Read(ref bmsonStorageRowsVersion) == storageRowsVersion.Value.BmsonRowsVersion
            && OwnedChartCollectionVersion == ownedCollectionVersion.Value
            && Volatile.Read(ref this.resourceHealthInputVersion) == resourceHealthInputVersion.Value
            && IsStableResourceHealthInputVersion(resourceHealthInputVersion.Value);
    }

    private void InvalidateResourceHealthIndexIfSnapshotInputIsStaleUnsafe()
    {
        int currentInputVersion = resourceHealthInputVersion;
        ResourceHealthIndexSnapshotState currentState = resourceHealthIndexState;
        if (currentState?.InputVersion != currentInputVersion
            || !IsStableResourceHealthInputVersion(currentInputVersion))
        {
            Volatile.Write(ref resourceHealthIndexInvalidated, true);
        }
    }

    private void PublishResourceHealthIndexSnapshotUnsafe(ResourceHealthIndexSnapshot snapshot)
    {
        resourceHealthIndexState = new ResourceHealthIndexSnapshotState(snapshot, resourceHealthInputVersion);
        Volatile.Write(ref resourceHealthIndexInvalidated, false);
    }

    private enum ResourceHealthIndexUpdateMode
    {
        FullOnUpdates,
        DeltaOnUpdates,
        DeferOnUpdates
    }

    private bool TryApplyResourceHealthIndexDeltaLocked(
        string reason,
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        int? deltaBaseResourceHealthInputVersion,
        int? deltaTargetResourceHealthInputVersion,
        out ResourceHealthIndexSnapshot snapshot)
    {
        snapshot = null;
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
        ResourceHealthIndexSnapshot currentSnapshot = currentState?.Snapshot;
        int baseInputVersion = deltaBaseResourceHealthInputVersion ?? currentState?.InputVersion ?? -1;
        if (!deltaTargetResourceHealthInputVersion.HasValue)
        {
            return false;
        }
        int targetInputVersion = deltaTargetResourceHealthInputVersion.Value;
        if (currentState == null
            || currentSnapshot == null
            || Volatile.Read(ref resourceHealthIndexInvalidated)
            || currentState.InputVersion != baseInputVersion
            || !IsStableResourceHealthInputVersion(baseInputVersion)
            || !IsStableResourceHealthInputVersion(targetInputVersion))
        {
            return false;
        }
        List<ChartFile> updatedTargetList = NormalizeResourceMaintenanceTargetCharts(updatedTargets);
        List<ChartFile> removedTargetList = NormalizeResourceMaintenanceTargetCharts(removedTargets);
        if (updatedTargetList.Count == 0 && removedTargetList.Count == 0)
        {
            return false;
        }
        int version = Interlocked.Increment(ref resourceHealthIndexVersionSeed);
        snapshot = currentSnapshot.ApplyDelta(updatedTargetList, removedTargetList, maintenanceService, version);
        lock (resourceHealthIndexLock)
        {
            if (Volatile.Read(ref resourceHealthIndexInvalidated)
                || !ReferenceEquals(resourceHealthIndexState, currentState))
            {
                snapshot = null;
                return false;
            }
            if (resourceHealthInputVersion != targetInputVersion
                || !IsStableResourceHealthInputVersion(targetInputVersion))
            {
                snapshot = null;
                return false;
            }
            resourceHealthIndexState = new ResourceHealthIndexSnapshotState(snapshot, targetInputVersion);
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

    private ResourceHealthIndexDispatchResult DispatchResourceHealthIndexMutation(
        ResourceHealthIndexMutation mutation,
        string reason)
    {
        var result = new ResourceHealthIndexDispatchResult
        {
            Snapshot = GetPublishedResourceHealthIndexSnapshotOrEmpty()
        };
        if (mutation == null || !mutation.HasChanges)
        {
            return result;
        }
        if (mutation.Invalidate)
        {
            InvalidateResourceHealthIndex(reason);
            result.Snapshot = GetPublishedResourceHealthIndexSnapshotOrEmpty();
            return result;
        }
        if (mutation.Defer)
        {
            result.Deferred = true;
            result.Snapshot = GetPublishedResourceHealthIndexSnapshotOrEmpty();
            LogInstallPerformance("resource_health_index_deferred reason=" + (reason ?? "unknown")
                + " targetCount=" + result.Snapshot.TargetCount
                + " updateTargets=" + mutation.UpdateTargetCount
                + " invalidated=" + Volatile.Read(ref resourceHealthIndexInvalidated).ToString().ToLowerInvariant());
            return result;
        }
        if (!mutation.RebuildFull
            && mutation.HasDeltaTargets
            && TryApplyResourceHealthIndexDeltaLocked(
                reason,
                mutation.UpdatedTargets,
                mutation.RemovedTargets,
                mutation.DeltaBaseResourceHealthInputVersion,
                mutation.DeltaTargetResourceHealthInputVersion,
                out ResourceHealthIndexSnapshot deltaSnapshot))
        {
            result.Snapshot = deltaSnapshot;
            result.DeltaApplied = true;
            result.IndexMs = deltaSnapshot.BuildMs;
            return result;
        }
        if (!mutation.RebuildFull && mutation.HasDeltaTargets && mutation.InvalidateIfDeltaFails)
        {
            InvalidateResourceHealthIndex(reason);
            result.Snapshot = GetPublishedResourceHealthIndexSnapshotOrEmpty();
            return result;
        }
        if (mutation.RebuildFull || mutation.HasDeltaTargets)
        {
            result.Snapshot = RebuildResourceHealthIndexSnapshotLocked(
                reason,
                mutation.FullOwnedTargetSet,
                out bool staleFullOwnedTarget);
            if (staleFullOwnedTarget)
            {
                return result;
            }
            result.FullRebuilt = true;
            result.IndexMs = result.Snapshot.BuildMs;
        }
        return result;
    }

    private static ResourceHealthIndexMutation BuildMaintenanceResourceHealthIndexMutation(
        ResourceMaintenanceTargetSet maintenanceTargets,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        bool resourceHealthIndexCurrent,
        bool workflowHasUpdates,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null)
    {
        var mutation = new ResourceHealthIndexMutation();
        List<ChartFile> maintenanceTargetCharts = maintenanceTargets.Charts;
        bool forceResourceHealthDelta = resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeltaOnUpdates && resourceHealthIndexCurrent;
        bool shouldUpdateIndex = workflowHasUpdates || !resourceHealthIndexCurrent || forceResourceHealthDelta;
        if (!shouldUpdateIndex)
        {
            return mutation;
        }
        if (resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeferOnUpdates)
        {
            mutation.Defer = true;
            mutation.UpdatedTargets.AddRange(maintenanceTargetCharts ?? []);
            return mutation;
        }
        if (resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeltaOnUpdates)
        {
            if (resourceHealthIndexCurrent)
            {
                mutation.UpdatedTargets.AddRange(maintenanceTargetCharts ?? []);
                mutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion;
                mutation.DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
                mutation.InvalidateIfDeltaFails = true;
            }
            else
            {
                mutation.Invalidate = true;
            }
            return mutation;
        }
        mutation.RebuildFull = true;
        if (maintenanceTargets.IsFullOwned)
        {
            if (HasFullOwnedResourceHealthTargetVersion(maintenanceTargets))
            {
                mutation.FullOwnedTargetSet = maintenanceTargets;
            }
        }
        return mutation;
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFile chart)
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
        return !IsResourceHealthIndexStateCurrent(currentState)
            ? ResourceHealthWarningProjection.Empty
            : currentState.Snapshot.GetProjection(chart);
    }

    internal ResourceHealthWarningProjection TryGetCurrentResourceHealthWarningProjection(ChartFileKind kind, string path, string md5)
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
        return !IsResourceHealthIndexStateCurrent(currentState)
            ? ResourceHealthWarningProjection.Empty
            : currentState.Snapshot.GetProjection(kind, path, md5);
    }

    internal ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshotForView(string reason)
    {
        return GetResourceHealthIndexSnapshot(reason);
    }

    internal ResourceHealthIndexSnapshot TryGetCurrentResourceHealthIndexSnapshotForView()
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
        return IsResourceHealthIndexStateCurrent(currentState)
            ? currentState.Snapshot
            : ResourceHealthIndexSnapshot.Empty;
    }

    private MaintenanceWorkflowResult setMaintenanceInfo(
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
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreateResourceMaintenanceTargetSet(charts);
            return setMaintenanceInfoCoreLocked(
                maintenanceTargets,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode,
                resourceHealthMutationReason,
                out _);
        }
    }

    private MaintenanceWorkflowResult setOwnedMaintenanceInfo(
        string reason,
        bool forceUpdate = false,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.FullOnUpdates,
        string resourceHealthMutationReason = null)
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreateFullOwnedResourceMaintenanceTargetSet(reason);
            return setMaintenanceInfoCoreLocked(
                maintenanceTargets,
                forceUpdate,
                progressReporter,
                cancellationToken,
                resourceHealthIndexUpdateMode,
                resourceHealthMutationReason ?? reason,
                out _);
        }
    }

    private MaintenanceWorkflowResult setInstallableMaintenanceInfo(
        string reason,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseHydratedMaintenanceSnapshotForInstallableMaintenance())
        {
            return setOwnedMaintenanceInfo(
                reason,
                forceUpdate: false,
                progressReporter: progressReporter,
                cancellationToken: cancellationToken,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.FullOnUpdates,
                resourceHealthMutationReason: reason);
        }

        using (rwlockBMSFiles.GetReaderGuard())
        {
            ResourceMaintenanceTargetSet maintenanceTargets = CreatePendingInstallableMaintenanceTargetSetUnsafe(reason);
            return setMaintenanceInfoCoreLocked(
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
        lock (lockDeferredMaintenanceHydration)
        {
            return MaintenanceHydrationRequestedVersion > 0
                && MaintenanceHydrationCompletedVersion >= MaintenanceHydrationRequestedVersion
                && !MaintenanceHydrationRunning;
        }
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

    private MaintenanceWorkflowResult setMaintenanceInfoCoreLocked(
        ResourceMaintenanceTargetSet maintenanceTargets,
        bool forceUpdate,
        Action<MaintenanceWorkflowProgress> progressReporter,
        CancellationToken cancellationToken,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        string resourceHealthMutationReason,
        out List<ChartFile> currentMaintenanceTargetCharts)
    {
        List<ChartFile> maintenanceTargetCharts = maintenanceTargets.Charts;
        currentMaintenanceTargetCharts = maintenanceTargetCharts ?? [];
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
        int deltaBaseResourceHealthInputVersion;
        int deltaTargetResourceHealthInputVersion;
        bool resourceHealthIndexCurrentBeforeUpdate;
        try
        {
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                var resourceLookupContext = new ResourceHealthLookupContext(directoryResourceLookupCache);
                ResourceHealthInputMutationScope resourceHealthInputMutation = BeginResourceHealthInputMutation();
                deltaBaseResourceHealthInputVersion = resourceHealthInputMutation.BaseInputVersion;
                resourceHealthIndexCurrentBeforeUpdate = resourceHealthInputMutation.BaseIndexCurrent;
                try
                {
                    workflowResult = maintenanceService.UpdateMaintenanceInfo(maintenanceTargetCharts, forceUpdate, dbGateway, dialogService, resourceLookupContext, LogInstallPerformance, progressReporter, cancellationToken);
                    if (ShouldRefreshResourceMaintenanceTargetsFromCurrentStorageOwners(workflowResult))
                    {
                        maintenanceTargetCharts = RefreshResourceMaintenanceTargetChartsFromCurrentStorageOwners(maintenanceTargetCharts);
                        maintenanceTargets = maintenanceTargets.WithCharts(maintenanceTargetCharts);
                    }
                }
                finally
                {
                    resourceHealthInputMutation.Dispose();
                }
                deltaTargetResourceHealthInputVersion = resourceHealthInputMutation.TargetInputVersion;
            }
        }
        catch (Exception ex)
        {
            MarkLr2SongDbSyncIncompleteAfterMaintenanceSongDbWriteFailure(ex, resourceHealthMutationReason);
            throw;
        }
        currentMaintenanceTargetCharts = maintenanceTargetCharts;
        ResourceHealthIndexMutation resourceHealthMutation = BuildMaintenanceResourceHealthIndexMutation(
            maintenanceTargets.WithResourceHealthInputVersion(deltaTargetResourceHealthInputVersion),
            resourceHealthIndexUpdateMode,
            resourceHealthIndexCurrentBeforeUpdate,
            workflowResult.HasUpdates,
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion);
        resourceHealthMutationReason = string.IsNullOrWhiteSpace(resourceHealthMutationReason)
            ? "setMaintenanceInfo"
            : resourceHealthMutationReason;
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            resourceHealthMutation,
            workflowResult.HasUpdates);
        try
        {
            DispatchOwnedChartCollectionMutation(mutationResult, resourceHealthMutationReason);
        }
        catch
        {
            InvalidateOwnedChartCollection();
            InvalidateInstalledDirectoryIndex();
            ForceInvalidateResourceHealthIndex("maintenance_dispatch_failed");
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
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (useOwnedSnapshot && !forceUpdate)
                {
                    ResourceHealthIndexSnapshot currentSnapshot = GetResourceHealthIndexSnapshot("resource_health_filter");
                    return [.. (isInIgnoredList ? currentSnapshot.IgnoredTargets : currentSnapshot.ActiveTargets)];
                }
                ResourceMaintenanceTargetSet targetSet = useOwnedSnapshot
                    ? CreateFullOwnedResourceMaintenanceTargetSet("force_resource_health_filter")
                    : CreateResourceMaintenanceTargetSet(charts);
                List<ChartFile> targets = targetSet.Charts;
                string resourceHealthReason = useOwnedSnapshot && forceUpdate
                    ? "force_resource_health_filter"
                    : "resource_health_filter";
                if (forceUpdate)
                {
                    setMaintenanceInfoCoreLocked(
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
                ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref resourceHealthIndexState);
                ResourceHealthIndexSnapshot snapshot = currentState?.Snapshot;
                if (!IsResourceHealthIndexStateCurrent(currentState))
                {
                    snapshot = ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version: 0);
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
        MaintenanceWorkflowResult result = setMaintenanceInfo(
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
        MaintenanceWorkflowResult result = setOwnedMaintenanceInfo(
            "manual_rescan_all_owned",
            forceUpdate: true,
            progressReporter: progressReporter,
            cancellationToken: cancellationToken);
        return result;
    }

    internal void SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset = false)
    {
        OwnedChartCollectionMutationResult mutationResult = null;
        string reason = unset ? "resource_health_unignore" : "resource_health_ignore";
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<ChartFile> targets = NormalizeResourceMaintenanceTargetCharts(charts);
                if (targets.Count == 0)
                {
                    return;
                }
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    ResourceHealthInputMutationScope resourceHealthMutation = BeginResourceHealthInputMutation();
                    try
                    {
                        try
                        {
                            List<BMSFileMaintenanceInfo> changes = maintenanceService.SetChartResourceWarningsIgnored(targets, unset);
                            dbGateway.UpsertMaintenanceInfos(changes);
                        }
                        catch
                        {
                            ForceInvalidateResourceHealthIndex("resource_health_ignore_failed");
                            throw;
                        }
                    }
                    finally
                    {
                        resourceHealthMutation.Dispose();
                    }
                    mutationResult = BuildResourceHealthWarningPresentationMutationResult(
                        targets,
                        resourceHealthMutation.BaseInputVersion,
                        resourceHealthMutation.TargetInputVersion);
                }
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
                MaintenanceEncodingUpdateResult updateResult = maintenanceService.ApplyEncoding(bmsFiles, encoding);
                if (updateResult.SongsToUpsert.Count > 0)
                {
                    ExecuteLr2SongDbWrite(
                        () => dbGateway.UpsertSongs(updateResult.SongsToUpsert),
                        stage: "lr2_song_db_encoding_upsert_failed",
                        logReason: nameof(SetBMSFilesEncoding));
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
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures = dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout);
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
        string[] normalizedMd5s = NormalizeChartInfoParseFailureMd5s(md5s);
        if (normalizedMd5s.Length == 0)
        {
            return;
        }
        dbGateway.DeleteChartInfoParseFailuresByMd5(normalizedMd5s);
        DispatchWarningPresentationChanged("chart_info_parse_failure_remove");
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
            normalizedPath = Path.GetFullPath(destinationDirectory.Trim());
        }
        catch
        {
            normalizedPath = destinationDirectory.Trim();
        }

        string rootPath = Path.GetPathRoot(normalizedPath);
        string trimmedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return normalizedPath;
        }

        string rootTrimmed = rootPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(rootTrimmed) && string.Equals(trimmedPath, rootTrimmed, StringComparison.OrdinalIgnoreCase)
            ? rootPath
            : trimmedPath;
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
        ChartResourceSnapshot precomputedDefinedResources = ResolvePrecomputedDefinedResources(batchState, targetEntryList);
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
                sourceSurfaceBatchHit: true,
                precomputedDefinedResources);
        }
        else
        {
            estimationSnapshot = package != null
                ? package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntryList, precomputedDefinedResources)
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
        if (TryBlockLr2SongDbSyncMutation(nameof(InstallChartPackagesAuto)))
        {
            return registeredPackages;
        }
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
                            CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "auto_install_prepare", 0L),
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
    /// <param name="existingHashes">既存譜面ハッシュの lookup（重複スキップ用）</param>
    /// <param name="excludedComponentPaths">移動対象外のコンポーネントパス</param>
    /// <returns>移動成功時true</returns>
    private bool MoveChartPackageFiles(ChartPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false, IPrimaryHashLookup existingHashes = null, ISet<string> excludedComponentPaths = null)
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
        public List<ChartFile> AddedCharts { get; } = [];

        public HashSet<string> AffectedDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void AddInstalledTargets(ChartStorageTargetSet addedTargets, string destinationDirectory)
        {
            AddedCharts.AddRange((addedTargets?.Charts ?? []).Where(chart => chart != null));
            AddAffectedDirectory(destinationDirectory);
            foreach (ChartFile addedChart in addedTargets?.Charts ?? [])
            {
                AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedChart.Path));
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

    private List<ChartPackage> installChartPackages(IEnumerable<ChartPackage> chartPackagesInstall, string installationDirectory = null, List<ChartFile> deferredMaintenanceCharts = null, List<ChartPackage> deferredInstalledPackages = null, Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null, IPrimaryHashLookup existingHashes = null, bool skipInstalledPackageWhenNoBms = false, bool deleteSourceContentsAfterSuccessfulInstall = false, EstimatedInstallBatchApplyContext estimatedInstallBatchApplyContext = null)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(installChartPackages));
        List<ChartPackage> installPackageList = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        List<ChartFile> addedChartsForChartInfo = [];

        static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)
        {
            return ChartStorageTargetSet.FromCharts(installResult?.AddedCharts);
        }

        void UpsertInstalledChartRows(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = CreateAddedStorageTargets(installResult);
            if (addedTargets.BmsFiles.Count > 0)
            {
                ExecuteLr2SongDbWrite(
                    () => dbGateway.UpsertSongs(addedTargets.BmsFiles),
                    stage: "lr2_song_db_install_upsert_failed",
                    logReason: "install_package");
            }
            if (addedTargets.BmsonSongs.Count > 0)
            {
                dbGateway.UpsertBmsonSongs(addedTargets.BmsonSongs);
            }
        }

        void UpdateInstalledChartMaintenance(PackageInstallExecutionResult installResult)
        {
            List<ChartFile> addedCharts = CreateAddedStorageTargets(installResult).Charts;
            if (deferredMaintenanceCharts != null)
            {
                deferredMaintenanceCharts.AddRange(addedCharts);
                return;
            }
            if (addedCharts.Count > 0)
            {
                setMaintenanceInfo(
                    addedCharts,
                    forceUpdate: true,
                    resourceHealthMutationReason: "install_package");
            }
        }

        void ApplyInstalledChartState(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = CreateAddedStorageTargets(installResult);
            List<ChartFile> addedCharts = addedTargets.Charts;
            addedChartsForChartInfo.AddRange(addedCharts);
            if (estimatedInstallBatchApplyContext != null)
            {
                estimatedInstallBatchApplyContext.AddInstalledTargets(addedTargets, installationDirectory);
                return;
            }
            ApplyInstalledChartStorageTargets(addedTargets, "install_package");
            List<string> addedDirectories = addedTargets.GetDistinctChartDirectories();
            if (addedDirectories.Count > 0)
            {
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
            result => SetBMSScore(CreateAddedStorageTargets(result).BmsFiles),
            ApplyInstalledChartState,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
        if (estimatedInstallBatchApplyContext != null && result.FailedPackages.Count < installPackageList.Count)
        {
            estimatedInstallBatchApplyContext.AddInstalledTargets(ChartStorageTargetSet.FromCharts([]), installationDirectory);
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
        ApplyInstalledChartStorageTargets(
            ChartStorageTargetSet.FromCharts(context.AddedCharts),
            "install_package_batch");
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
        foreach (ChartFile target in NormalizeResourceMaintenanceTargetCharts(charts))
        {
            string key = target.Kind + "|" + target.Path;
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                targetsByKey[key] = target;
            }
        }
        return [.. targetsByKey.Values];
    }

    private static List<ChartFile> CreateAddedBmsonChartProjections(IEnumerable<ChartFile> addedCharts)
    {
        var chartsByPath = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in addedCharts ?? [])
        {
            if (chart?.Kind != ChartFileKind.Bmson)
            {
                continue;
            }
            ChartFile projected = ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);
            if (projected?.Kind != ChartFileKind.Bmson || string.IsNullOrWhiteSpace(projected.Path))
            {
                continue;
            }
            chartsByPath[projected.Path] = projected;
        }
        return [.. chartsByPath.Values];
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
                    if (HasUnsupportedResourcePath(targetEntryList))
                    {
                        ApplyUnsupportedResourcePathToPackageUnsafe(package, targetEntryList);
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

    private static bool HasUnsupportedResourcePath(IEnumerable<PackageChartEntry> entries)
    {
        return (entries ?? []).Any(entry => entry?.Chart != null && entry.ResourceSnapshot.HasUnsupportedParentTraversalReference);
    }

    private static void ApplyUnsupportedResourcePathToPackageUnsafe(ChartPackage package, IEnumerable<PackageChartEntry> missingEntries)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.UnsupportedResourcePath;
        }
        foreach (PackageChartEntry entry in (missingEntries ?? []).Where(entry => entry?.Chart != null))
        {
            if (entry.ResourceSnapshot.HasUnsupportedParentTraversalReference)
            {
                entry.ApplyUnsupportedResourcePathWarning();
            }
            else
            {
                entry.ClearInstallDestination();
            }
        }
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
        IInstalledChartLookupIndex installedDirectoryIndex = CreateInstalledChartLookupSnapshotUnsafe();
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
                        if (HasUnsupportedResourcePath(missingEntries))
                        {
                            ApplyUnsupportedResourcePathToPackageUnsafe(package, missingEntries);
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
                        List<string> installedDirectories = GetDistinctInstalledDirectoriesForChartUnsafe(chartEntry.Chart);
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
        if (TryBlockLr2SongDbSyncMutation(nameof(ForceInstallPendingPackages)))
        {
            return;
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
        if (originalPackage == null || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return null;
        }
        var hashSet = new HashSet<string>((originalPackage.ChartEntries ?? [])
            .Select(entry => ChartLookupKey.GetPrimaryHash(entry?.Chart))
            .Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
        if (hashSet.Count == 0)
        {
            return null;
        }
        List<string> destinationPaths = GetInstalledDirectChildPathsByPrimaryHashesUnsafe(hashSet, destinationDirectory);
        if (destinationPaths.Count == 0)
        {
            return null;
        }
        List<ChartFile> destinationCharts = OverlayInstallDestinationRuntimeStates(CreateOwnedChartFilesForExactPathsUnsafe(
            destinationPaths,
            includeWarningSnapshot: false,
            includeResourceReferences: false));
        List<PackageChartEntry> entries = [.. destinationCharts.Where(delegate (ChartFile chart)
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                return false;
            }
            string key = ChartLookupKey.GetPrimaryHash(chart);
            if (string.IsNullOrWhiteSpace(key) || !hashSet.Contains(key))
            {
                return false;
            }
            return true;
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
        if (TryBlockLr2SongDbSyncMutation(nameof(InstallPendingPackagesToEstimatedDestinations)))
        {
            return;
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
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                            packages,
                            ChartPackagesPending,
                            CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "install_pending_estimated_filter", 0L),
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
                        using (canUseResourceHealthIndexDelta ? SuppressResourceHealthIndexInvalidation() : null)
                        {
                            ApplyEstimatedInstallBatchLibraryState(batchApplyContext);
                        }
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
                                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                                resourceHealthMutationReason: "install_package_estimated");
                        }
                        List<ChartFile> estimatedInstallInlineTargets = BuildEstimatedInstallMaintenanceTargets(
                            estimatedInstallMaintenanceTargets.Concat(
                                CreateAddedBmsonChartProjections(batchApplyContext.AddedCharts)));
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

    private void TryRegroupPendingPackagesForSourceDirectoriesUnsafe(IEnumerable<string> sourceDirectoryPaths)
    {
        List<string> sourceDirectories = [.. (sourceDirectoryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (sourceDirectories.Count == 0)
        {
            return;
        }
        IInstalledChartLookupIndex installedDirectoryIndex = CreateInstalledChartLookupSnapshotUnsafe();
        foreach (string sourceDirectoryPath in sourceDirectories)
        {
            TryRegroupPendingPackagesForSourceDirectoryUnsafe(sourceDirectoryPath, installedDirectoryIndex);
        }
    }

    private void TryRegroupPendingPackagesForSourceDirectoryUnsafe(string sourceDirectoryPath, IInstalledChartLookupIndex installedDirectoryIndex)
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
        if (!TryBuildRegroupedPendingPackage(sourceDirectoryPath, sourcePackages, installedDirectoryIndex, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason))
        {
            LogInstallPerformance("pending_regroup skip reason=" + skipReason + " source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        ReinitializePendingWarningsForPackageUnsafe(regroupedPackage, CreateInstalledChartKeySnapshotExcludingChartsUnsafe([]));
        ReplacePendingPackagesWithRegroupedPackageUnsafe(sourcePackages, regroupedPackage);
        dbGateway.DeleteInstallRows(sourcePackages.Select(pendingPackage => pendingPackage.path));
        dbGateway.UpsertInstallRows([regroupedPackage]);
        List<PackageChartEntry> regroupedPackageEntries = regroupedPackage.ChartEntries;
        bool metadataResolved = regroupedPackageEntries.Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationArtist));
        LogInstallPerformance("pending_regroup success source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count + " files=" + regroupedPackageEntries.Count + " dst=" + resolvedDestinationDirectory + " metadataResolved=" + metadataResolved);
    }

    private bool TryBuildRegroupedPendingPackage(string sourceDirectoryPath, List<ChartPackage> sourcePackages, IInstalledChartLookupIndex installedDirectoryIndex, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason)
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
            if (!TryResolvePendingFileExpectedInstallDirectory(regroupedEntry.Chart, installedDirectoryIndex, out string expectedDirectory, out string unresolvedReason))
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

    private bool TryResolvePendingFileExpectedInstallDirectory(ChartFile chart, IInstalledChartLookupIndex installedDirectoryIndex, out string expectedDirectory, out string reason)
    {
        expectedDirectory = null;
        reason = "missing_expected_destination";
        if (chart == null)
        {
            reason = "null_chart";
            return false;
        }
        List<string> installedDirectories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, chart);
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

    private void ReinitializePendingWarningsForPackageUnsafe(ChartPackage package, IPrimaryHashLookup installedHashes)
    {
        if (package == null)
        {
            return;
        }
        bool isSingleFilePackage = !Directory.Exists(package.path);
        IPrimaryHashLookup installedHashLookup = installedHashes ?? EmptyPrimaryHashLookup.Instance;
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null)
            {
                continue;
            }

            bool isBmson = chart.Kind == ChartFileKind.Bmson;
            entry.ClearStructuredWarnings();

            string key = ChartLookupKey.GetPrimaryHash(chart);
            if (!string.IsNullOrWhiteSpace(key) && installedHashLookup.ContainsPrimaryHash(key))
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
            }
            else if (isSingleFilePackage)
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
            }
        }
        BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries(package.ChartEntries);
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
                        InstalledChartLookupIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledChartLookupSnapshotUnsafe();
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
                                        ChartFile multipleDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot);
                                        return "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (multipleDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(multipleDirectoryChart) ?? "(null)") + " dirCount=" + ((multipleDirectoryChart == null) ? 0 : BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndexSnapshot, multipleDirectoryChart).Count);
                                    case InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories:
                                        return "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndexSnapshot);
                                    default:
                                        ChartFile missingDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot);
                                        return "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (missingDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(missingDirectoryChart) ?? "(null)");
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
        EnsureOwnedChartCollectionBuiltUnsafe();
        lock (lockOwnedChartCollection)
        {
            return ownedChartCollection.ContainsKnownChart(chart);
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
        List<LibraryChartRef> libraryChartsSnapshot = null;
        if ((referenceMaps.Md5ToTablesMap.Count + referenceMaps.Sha256ToTablesMap.Count) > 0)
        {
            libraryChartsSnapshot = SnapshotLibraryChartRefsForPlaylistReferenceApply(referenceMaps);
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
            List<LibraryChartRef> libraryChartsSnapshot = SnapshotLibraryChartRefsForPlaylistReferenceApply(referenceMaps);
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

    private List<LibraryChartRef> SnapshotLibraryChartRefsForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps)
    {
        return SnapshotLibraryChartRefsForPlaylistReferenceApply(
            CreatePlaylistReferenceHashSet(referenceMaps?.Md5ToTablesMap?.Keys),
            CreatePlaylistReferenceHashSet(referenceMaps?.Sha256ToTablesMap?.Keys));
    }

    private List<LibraryChartRef> SnapshotLibraryChartRefsForPlaylistReferenceApply(
        ISet<string> md5Hashes,
        ISet<string> sha256Hashes)
    {
        if ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0)
        {
            return null;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            List<LibraryChartRef> charts;
            lock (lockOwnedChartCollection)
            {
                charts = ownedChartCollection.CreateLibraryChartRefsForHashes(md5Hashes, sha256Hashes);
            }
            return charts.Count == 0 ? null : charts;
        }
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
        List<LibraryChartRef> libraryChartsSnapshot = SnapshotLibraryChartRefsForPlaylistReferenceApply(
            CreateCombinedPlaylistReferenceHashSet(oldMd5Hashes, newMd5Hashes),
            CreateCombinedPlaylistReferenceHashSet(oldSha256Hashes, newSha256Hashes));
        List<LibraryChartRef> oldLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, oldMd5Hashes, oldSha256Hashes);
        List<PackageChartEntry> oldPendingEntries = FilterPendingPlaylistReferenceTargetEntries(SnapshotPendingChartEntriesForPlaylistReferenceApply(), oldMd5Hashes, oldSha256Hashes);
        List<LibraryChartRef> newLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, newMd5Hashes, newSha256Hashes);
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

    private static List<LibraryChartRef> FilterPlaylistReferenceTargets(IEnumerable<LibraryChartRef> charts, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (charts == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        return [.. charts.Where(delegate (LibraryChartRef chart)
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

    private static HashSet<string> CreatePlaylistReferenceHashSet(IEnumerable<string> hashes)
    {
        return new HashSet<string>(
            (hashes ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CreateCombinedPlaylistReferenceHashSet(params IEnumerable<string>[] hashSets)
    {
        var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IEnumerable<string> hashes in hashSets ?? [])
        {
            foreach (string hash in hashes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    combined.Add(hash);
                }
            }
        }
        return combined;
    }

    private static void BuildPlaylistReferenceHashSets(IEnumerable<BMSTableEntry> entries, out HashSet<string> md5Hashes, out HashSet<string> sha256Hashes)
    {
        md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? [])
        {
            PlaylistEntryLookupKey lookupKey = PlaylistEntryLookupKey.FromEntry(entry);
            if (!lookupKey.HasValue)
            {
                continue;
            }
            if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5)
            {
                md5Hashes.Add(lookupKey.Hash);
            }
            else
            {
                sha256Hashes.Add(lookupKey.Hash);
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
        MergeChartDirectory(src, dst, operationId: 0);
    }

    internal void MergeChartDirectory(string src, string dst, long operationId)
    {
        if (src == null)
        {
            throw new ArgumentNullException("src");
        }
        if (dst == null)
        {
            throw new ArgumentNullException("dst");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(MergeChartDirectory)))
        {
            return;
        }
        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + src + " dst=" + dst);
        try
        {
            var initializedLockWaitStopwatch = Stopwatch.StartNew();
            using (rwlockBMSFilesInitializedMin.GetReaderGuard())
            {
                LogInstallPerformance("duplicate_merge_model initialized_lock_acquired op=" + operationId + " waitMs=" + initializedLockWaitStopwatch.ElapsedMilliseconds);
                var pendingLockWaitStopwatch = Stopwatch.StartNew();
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    LogInstallPerformance("duplicate_merge_model pending_lock_acquired op=" + operationId + " waitMs=" + pendingLockWaitStopwatch.ElapsedMilliseconds);
                    var bmsLockWaitStopwatch = Stopwatch.StartNew();
                    using (rwlockBMSFiles.GetWriterGuard())
                    {
                        LogInstallPerformance("duplicate_merge_model bms_lock_acquired op=" + operationId + " waitMs=" + bmsLockWaitStopwatch.ElapsedMilliseconds);
                        var prepareStopwatch = Stopwatch.StartNew();
                        var sourceRefsStopwatch = Stopwatch.StartNew();
                        List<LibraryChartRef> sourceChartRefs = CreateOwnedRealPathChartRefsUnsafe(src);
                        LogInstallPerformance("duplicate_merge_model prepare_source_refs_done op=" + operationId
                            + " elapsedMs=" + sourceRefsStopwatch.ElapsedMilliseconds
                            + " count=" + sourceChartRefs.Count);
                        var overlayStopwatch = Stopwatch.StartNew();
                        InstallDestinationOverlayChartRefSnapshot overlayChartRefs = CreateInstallDestinationOverlayChartRefSnapshotUnsafe();
                        LogInstallPerformance("duplicate_merge_model prepare_overlay_refs_done op=" + operationId
                            + " elapsedMs=" + overlayStopwatch.ElapsedMilliseconds
                            + " count=" + (overlayChartRefs?.ChartCount ?? 0));
                        var prepareCoreStopwatch = Stopwatch.StartNew();
                        LibraryMergeResult mergeResult = libraryFileOperationsService.PrepareMergeDirectory(
                            src,
                            dst,
                            sourceChartRefs,
                            overlayChartRefs,
                            ChartPackagesPending,
                            ChartPackagesInstalled,
                            excluded => CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, "duplicate_merge_prepare", operationId));
                        LogInstallPerformance("duplicate_merge_model prepare_core_done op=" + operationId
                            + " elapsedMs=" + prepareCoreStopwatch.ElapsedMilliseconds);
                        int repackageEntryCount = mergeResult.Repackage?.ChartEntries?.Count ?? 0;
                        LogInstallPerformance("duplicate_merge_model prepare_done op=" + operationId
                            + " success=" + mergeResult.Success
                            + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds
                            + " sourceCharts=" + mergeResult.SourceCharts.Count
                            + " repackageEntries=" + repackageEntryCount
                            + " existingHashes=" + (mergeResult.ExistingHashes?.DistinctPrimaryHashCount ?? 0)
                            + " installDestinations=" + mergeResult.ReferenceMutationDelta.UpdatedInstallDestinations.Count
                            + " installedPackagePaths=" + mergeResult.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count);
                        if (!mergeResult.Success)
                        {
                            LogInstallPerformance("duplicate_merge_model skipped op=" + operationId + " reason=no_source_charts totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        var sourceSnapshotStopwatch = Stopwatch.StartNew();
                        List<BMSFile> sourceBmsFiles = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetBmsStorageOwner())
                        .Where(ChartFileKindResolver.IsBmsChartFile)];
                        List<LR2SongDBExtended.bmson_song> sourceBmsonSongs = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetBmsonStorageOwner())
                        .Where(song => song != null)
                        .Distinct()];
                        IReadOnlyDictionary<string, Lr2SongUserColumns> sourceUserColumnsByPath = dbGateway.CreateSongUserColumnSnapshot(sourceBmsFiles.Select(file => file?.path));
                        var sourceUserColumnsByOwner = new Dictionary<BMSFile, Lr2SongUserColumns>();
                        foreach (BMSFile sourceBmsFile in sourceBmsFiles)
                        {
                            if (sourceBmsFile != null
                                && !string.IsNullOrWhiteSpace(sourceBmsFile.path)
                                && sourceUserColumnsByPath.TryGetValue(sourceBmsFile.path, out Lr2SongUserColumns userColumns))
                            {
                                sourceUserColumnsByOwner[sourceBmsFile] = userColumns;
                            }
                        }
                        LogInstallPerformance("duplicate_merge_model source_snapshot_done op=" + operationId
                            + " elapsedMs=" + sourceSnapshotStopwatch.ElapsedMilliseconds
                            + " bms=" + sourceBmsFiles.Count
                            + " bmson=" + sourceBmsonSongs.Count);
                        var unregisterStopwatch = Stopwatch.StartNew();
                        ApplyLibraryMutationDeltaWithPerformanceContext(
                            BuildLibrarySourceUnregisterMutationDelta(sourceBmsFiles, sourceBmsonSongs),
                            "duplicate_merge_unregister op=" + operationId);
                        LogInstallPerformance("duplicate_merge_model unregister_source_done op=" + operationId
                            + " elapsedMs=" + unregisterStopwatch.ElapsedMilliseconds
                            + " bms=" + sourceBmsFiles.Count
                            + " bmson=" + sourceBmsonSongs.Count);
                        var reverseLookupRemoveStopwatch = Stopwatch.StartNew();
                        DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                        List<string> reverseLookupRemovedDirs = [.. (directoryResourceLookupCache?.Keys ?? []).Where(f => (f + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
                        foreach (string item in reverseLookupRemovedDirs)
                        {
                            reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.RemoveDirWithResult(item));
                        }
                        LogInstallPerformance("duplicate_merge_model reverse_lookup_remove_done op=" + operationId + " elapsedMs=" + reverseLookupRemoveStopwatch.ElapsedMilliseconds + " dirs=" + reverseLookupRemovedDirs.Count);
                        var moveStopwatch = Stopwatch.StartNew();
                        LogInstallPerformance("duplicate_merge_model move_files_start op=" + operationId + " entries=" + repackageEntryCount + " src=" + src + " dst=" + dst);
                        if (!MoveChartPackageFiles(mergeResult.Repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true, existingHashes: mergeResult.ExistingHashes))
                        {
                            InvalidateInstalledDirectoryIndex();
                            LogInstallPerformance("duplicate_merge_model move_files_failed op=" + operationId + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            dialogService.Show(string.Format(Resources.Error_BmsFolderMergeFailed, src, dst), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            return;
                        }
                        LogInstallPerformance("duplicate_merge_model move_files_done op=" + operationId + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds);
                        var scanStopwatch = Stopwatch.StartNew();
                        ChartScanResult mergedDirectoryScan = ChartDirectoryScanBuilder.BuildFromRoots([dst]);
                        LogInstallPerformance("duplicate_merge_model dst_scan_done op=" + operationId + " elapsedMs=" + scanStopwatch.ElapsedMilliseconds + " chartDirs=" + mergedDirectoryScan.ChartDirectories.Count);
                        var reverseLookupAddStopwatch = Stopwatch.StartNew();
                        foreach (string chartDirectory in mergedDirectoryScan.ChartDirectories)
                        {
                            reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(chartDirectory, mergedDirectoryScan));
                        }
                        LogInstallPerformance("duplicate_merge_model reverse_lookup_add_done op=" + operationId + " elapsedMs=" + reverseLookupAddStopwatch.ElapsedMilliseconds + " dirs=" + mergedDirectoryScan.ChartDirectories.Count);
                        LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);
                        var applyDeltaStopwatch = Stopwatch.StartNew();
                        ApplyLibraryMutationDeltaWithPerformanceContext(
                            mergeResult.ReferenceMutationDelta,
                            "duplicate_merge_reference_delta op=" + operationId);
                        LogInstallPerformance("duplicate_merge_model apply_delta_done op=" + operationId
                            + " elapsedMs=" + applyDeltaStopwatch.ElapsedMilliseconds
                            + " installDestinations=" + mergeResult.ReferenceMutationDelta.UpdatedInstallDestinations.Count
                            + " installedPackagePaths=" + mergeResult.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count);
                        var movedSnapshotStopwatch = Stopwatch.StartNew();
                        List<PackageChartEntry> movedPackageEntries = mergeResult.Repackage.ChartEntries;
                        ChartStorageTargetSet movedTargets = ChartStorageTargetSet.FromCharts(movedPackageEntries
                        .Select(entry => entry?.Chart)
                        .Where(chart => chart != null && IsFilePathUnderDirectory(chart.Path, dst)));
                        List<BMSFile> movedBmsFiles = movedTargets.BmsFiles;
                        List<LR2SongDBExtended.bmson_song> movedBmsonSongs = movedTargets.BmsonSongs;
                        foreach (BMSFile movedBmsFile in movedBmsFiles)
                        {
                            if (movedBmsFile != null && sourceUserColumnsByOwner.TryGetValue(movedBmsFile, out Lr2SongUserColumns userColumns))
                            {
                                BmsLibraryDbGateway.ApplySongUserColumns(movedBmsFile, userColumns);
                            }
                        }
                        LogInstallPerformance("duplicate_merge_model moved_snapshot_done op=" + operationId
                            + " elapsedMs=" + movedSnapshotStopwatch.ElapsedMilliseconds
                            + " bms=" + movedBmsFiles.Count
                            + " bmson=" + movedBmsonSongs.Count);
                        var dbStopwatch = Stopwatch.StartNew();
                        ExecuteLr2SongDbWrite(
                            () => dbGateway.UpsertSongs(movedBmsFiles),
                            stage: "lr2_song_db_duplicate_merge_upsert_failed",
                            logReason: "duplicate_merge");
                        if (movedBmsonSongs.Count > 0)
                        {
                            dbGateway.UpsertBmsonSongs(movedBmsonSongs);
                        }
                        LogInstallPerformance("duplicate_merge_model db_upsert_done op=" + operationId + " elapsedMs=" + dbStopwatch.ElapsedMilliseconds + " bms=" + movedBmsFiles.Count + " bmson=" + movedBmsonSongs.Count);
                        var maintenanceTargetStopwatch = Stopwatch.StartNew();
                        ChartStorageTargetSet destinationMaintenanceTargets = CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(dst);
                        ChartStorageTargetSet maintenanceTargets = ChartStorageTargetSet.FromCharts(destinationMaintenanceTargets.Charts.Concat(movedTargets.Charts));
                        LogInstallPerformance("duplicate_merge_model maintenance_targets_done op=" + operationId
                            + " elapsedMs=" + maintenanceTargetStopwatch.ElapsedMilliseconds
                            + " bms=" + maintenanceTargets.BmsFiles.Count
                            + " bmson=" + maintenanceTargets.BmsonSongs.Count
                            + " destinationBmson=" + destinationMaintenanceTargets.BmsonSongs.Count);
                        // NOTE:
                        // Merge finalizes BMSFiles/BmsonSongs after maintenance. Building the full warning index here
                        // would immediately be invalidated by that final library replacement, so defer it to the next view
                        // that actually needs the resource-health projection.
                        var maintenanceStopwatch = Stopwatch.StartNew();
                        setMaintenanceInfo(
                            maintenanceTargets.Charts,
                            forceUpdate: true,
                            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
                            resourceHealthMutationReason: "merge_folder");
                        LogInstallPerformance("duplicate_merge_model maintenance_done op=" + operationId + " elapsedMs=" + maintenanceStopwatch.ElapsedMilliseconds);
                        var upsertStopwatch = Stopwatch.StartNew();
                        ApplyInstalledChartStorageTargets(movedTargets, "merge_folder");
                        LogInstallPerformance("duplicate_merge_model upsert_library_done op=" + operationId
                            + " elapsedMs=" + upsertStopwatch.ElapsedMilliseconds
                            + " movedBms=" + movedBmsFiles.Count
                            + " movedBmson=" + movedBmsonSongs.Count
                            + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            InvalidateInstalledDirectoryIndex();
            LogInstallPerformanceWarn("duplicate_merge_model failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name);
            throw;
        }
    }

    private static LibraryMutationDelta BuildLibrarySourceUnregisterMutationDelta(
        IEnumerable<BMSFile> sourceBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> sourceBmsonSongs)
    {
        var delta = new LibraryMutationDelta();
        delta.ChartRemoveRequests.AddRange((sourceBmsFiles ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReference)
            .Where(request => request != null));
        delta.ChartRemoveRequests.AddRange((sourceBmsonSongs ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReference)
            .Where(request => request != null));
        bool hasCharts = delta.ChartRemoveRequests.Count > 0;
        delta.InvalidateInstalledDirectoryIndex = hasCharts;
        delta.InvalidateParentFolderCache = hasCharts;
        delta.ClearDuplicatedCache = hasCharts;
        return delta;
    }

    private static bool IsFilePathUnderDirectory(string filePath, string directoryPath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && !string.IsNullOrWhiteSpace(directoryPath)
            && filePath.StartsWith(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    internal void FixInstallationDirectoryCharts(IEnumerable<ChartFile> charts)
    {
        if (charts == null)
        {
            throw new ArgumentNullException("charts");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(FixInstallationDirectoryCharts)))
        {
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
                IPrimaryHashLookup existingHashes = CreateInstalledChartKeySnapshotExcludingChartsUnsafe(chartList);
                LibraryFixInstallationResult result = libraryFileOperationsService.FixInstallationDirectory(
                    chartList,
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
                List<ChartFile> maintenanceTargets = NormalizeResourceMaintenanceTargetCharts(result.MaintenanceCharts);
                if (maintenanceTargets.Count > 0)
                {
                    setMaintenanceInfo(
                        maintenanceTargets,
                        forceUpdate: true,
                        resourceHealthMutationReason: "fix_installation_directory");
                }
            }
        }
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
                    ApplyAutoRenamePlans(plans, progressReporter);
                }
            }
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
                    return ApplyAutoRenamePlans(plans, progressReporter);
                }
            }
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
        long operationId = Stopwatch.GetTimestamp();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        List<FolderAutoRenamePlan> planList = [.. (plans ?? []).Where(plan => plan != null)];
        bool hasActionablePlan = false;
        LibraryMutationDelta batchMutation = new LibraryMutationDelta();
        List<LibraryFolderPathChange> movedFolders = [];
        var metrics = new AutoRenameBatchMetrics(operationId, planList.Count);
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts = CreateInstallDestinationOverlayChartRefSnapshotUnsafe();
        HashSet<string> movedSourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int progressTotal = CountAutoRenameProgressPlans(planList);
        metrics.ProgressTotal = progressTotal;
        int progressProcessed = 0;
        LogInstallPerformance("auto_rename_folders_batch start op=" + operationId
            + " planCount=" + planList.Count
            + " progressTotal=" + progressTotal);
        ReportAutoRenameProgress(progressReporter, progressTotal, progressProcessed, string.Empty);
        if (planList.Any(plan => !string.IsNullOrWhiteSpace(plan.SourceDirectory) && Path.GetPathRoot(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            dialogService.Show(Resources.Warn_DriveRootBmsSkipped, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        Stopwatch moveLoopStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (FolderAutoRenamePlan plan in planList)
            {
                bool reportProgress = IsAutoRenameProgressPlan(plan);
                try
                {
                    if (plan.FailureException != null)
                    {
                        dialogService.Show(string.Format(Resources.Error_RenameFailed, plan.SourceDirectory, plan.FailureException.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(plan.DestinationDirectory) || string.IsNullOrWhiteSpace(plan.SourceDirectory))
                    {
                        continue;
                    }
                    hasActionablePlan = true;
                    ApplyAutoRenamePlanToBatch(plan, batchMutation, movedFolders, installDestinationOverlayCharts, movedSourceDirectories, metrics);
                }
                finally
                {
                    if (reportProgress)
                    {
                        progressProcessed = Math.Min(progressProcessed + 1, progressTotal);
                        ReportAutoRenameProgress(progressReporter, progressTotal, progressProcessed, plan.SourceDirectory);
                    }
                }
            }
        }
        finally
        {
            moveLoopStopwatch.Stop();
            metrics.MoveLoopMs = moveLoopStopwatch.ElapsedMilliseconds;
            LogInstallPerformance("auto_rename_folders_batch move_loop_done op=" + operationId
                + " planCount=" + metrics.PlanCount
                + " progressTotal=" + metrics.ProgressTotal
                + " actionable=" + metrics.ActionablePlanCount
                + " movedFolders=" + movedFolders.Count
                + " skippedDuplicateSource=" + metrics.SkippedDuplicateSourceCount
                + " skippedMissingSource=" + metrics.SkippedMissingSourceCount
                + " moveFailed=" + metrics.MoveFailedCount
                + " slowMoves=" + metrics.SlowMoveCount
                + " pathChanges=" + batchMutation.ChartPathChanges.Count
                + " folderPathChanges=" + batchMutation.FolderPathChanges.Count
                + " buildDeltaMs=" + metrics.BuildDeltaMs
                + " moveFileMs=" + metrics.MoveFileMs
                + " appendDeltaMs=" + metrics.AppendDeltaMs
                + " elapsedMs=" + metrics.MoveLoopMs);
            ApplyAutoRenameBatchChanges(batchMutation, movedFolders, metrics);
            totalStopwatch.Stop();
            LogInstallPerformance("auto_rename_folders_batch done op=" + operationId
                + " hasActionablePlan=" + hasActionablePlan
                + " movedFolders=" + movedFolders.Count
                + " pathChanges=" + batchMutation.ChartPathChanges.Count
                + " folderPathChanges=" + batchMutation.FolderPathChanges.Count
                + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
        }
        return hasActionablePlan;
    }

    private static int CountAutoRenameProgressPlans(IEnumerable<FolderAutoRenamePlan> plans)
    {
        return (plans ?? []).Count(IsAutoRenameProgressPlan);
    }

    private static bool IsAutoRenameProgressPlan(FolderAutoRenamePlan plan)
    {
        return plan?.FailureException != null
            || (!string.IsNullOrWhiteSpace(plan?.SourceDirectory)
                && !string.IsNullOrWhiteSpace(plan.DestinationDirectory));
    }

    private sealed class AutoRenameBatchMetrics(long operationId, int planCount)
    {
        public const long SlowMoveLogThresholdMs = 500;

        public long OperationId { get; } = operationId;

        public int PlanCount { get; } = planCount;

        public int ProgressTotal { get; set; }

        public int ActionablePlanCount { get; set; }

        public int SkippedDuplicateSourceCount { get; set; }

        public int SkippedMissingSourceCount { get; set; }

        public int MoveFailedCount { get; set; }

        public int SlowMoveCount { get; set; }

        public long BuildDeltaMs { get; set; }

        public long MoveFileMs { get; set; }

        public long AppendDeltaMs { get; set; }

        public long MoveLoopMs { get; set; }
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

    private void ApplyAutoRenamePlanToBatch(
        FolderAutoRenamePlan plan,
        LibraryMutationDelta batchMutation,
        List<LibraryFolderPathChange> movedFolders,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        HashSet<string> movedSourceDirectories,
        AutoRenameBatchMetrics metrics)
    {
        string srcDir = plan.SourceDirectory;
        string newName = NormalizeAutoRenameFolderName(Path.GetFileName(plan.DestinationDirectory));
        if (string.IsNullOrWhiteSpace(newName) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (movedSourceDirectories.Contains(srcDir))
        {
            metrics.SkippedDuplicateSourceCount++;
            return;
        }
        if (!Directory.Exists(srcDir))
        {
            metrics.SkippedMissingSourceCount++;
            dialogService.Show(string.Format(Resources.Warn_RenameFolderNotExists, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }

        string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
        metrics.ActionablePlanCount++;
        Stopwatch stopwatch = Stopwatch.StartNew();
        LibraryMutationDelta delta = libraryFileOperationsService.BuildFolderMoveDelta(
            srcDir,
            dstDir,
            CreateOwnedRealPathChartRefsUnsafe(srcDir),
            installDestinationOverlayCharts,
            ChartPackagesPending,
            ChartPackagesInstalled,
            unregister: false,
            notifyStorageRowPathChanges: false);
        stopwatch.Stop();
        metrics.BuildDeltaMs += stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        if (!TryMoveLibraryChartFolderFileOnly(srcDir, dstDir))
        {
            stopwatch.Stop();
            metrics.MoveFileMs += stopwatch.ElapsedMilliseconds;
            metrics.MoveFailedCount++;
            return;
        }
        stopwatch.Stop();
        metrics.MoveFileMs += stopwatch.ElapsedMilliseconds;
        if (stopwatch.ElapsedMilliseconds >= AutoRenameBatchMetrics.SlowMoveLogThresholdMs)
        {
            metrics.SlowMoveCount++;
            LogInstallPerformance("auto_rename_folder_move slow op=" + metrics.OperationId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " src=" + srcDir
                + " dst=" + dstDir
                + " pathChanges=" + delta.ChartPathChanges.Count
                + " folderPathChanges=" + delta.FolderPathChanges.Count);
        }
        movedSourceDirectories.Add(srcDir);
        movedFolders?.Add(new LibraryFolderPathChange
        {
            OldFolderPath = srcDir,
            NewFolderPath = dstDir
        });
        stopwatch.Restart();
        AppendLibraryMutationDelta(batchMutation, delta);
        stopwatch.Stop();
        metrics.AppendDeltaMs += stopwatch.ElapsedMilliseconds;
    }

    private void ApplyAutoRenameBatchChanges(LibraryMutationDelta batchMutation, List<LibraryFolderPathChange> movedFolders, AutoRenameBatchMetrics metrics)
    {
        Stopwatch tailStopwatch = Stopwatch.StartNew();
        long reverseLookupMs = 0;
        long mutationApplyMs = 0;
        int movedFolderCount = movedFolders?.Count ?? 0;
        LogInstallPerformance("auto_rename_folders_batch tail_start op=" + metrics.OperationId
            + " movedFolders=" + movedFolderCount
            + " pathChanges=" + (batchMutation?.ChartPathChanges.Count ?? 0)
            + " folderPathChanges=" + (batchMutation?.FolderPathChanges.Count ?? 0));
        try
        {
            if (movedFolders?.Count > 0)
            {
                Stopwatch reverseLookupStopwatch = Stopwatch.StartNew();
                MovedFolderReferenceUpdateResult updateResult = libraryFileOperationsService.UpdateMovedFolderReferences(movedFolders, directoryResourceLookupCache);
                reverseLookupStopwatch.Stop();
                reverseLookupMs = reverseLookupStopwatch.ElapsedMilliseconds;
                LogInstallPerformance("auto_rename_folders_reverse_lookup done op=" + metrics.OperationId
                    + " moves=" + updateResult.MoveCount
                    + " lookupKeys=" + updateResult.LookupKeyCount
                    + " matchedKeys=" + updateResult.MatchedKeyCount
                    + " serviceElapsedMs=" + updateResult.ElapsedMs
                    + " elapsedMs=" + reverseLookupMs);
                LogReverseLookupMutationAndQueueWarmupIfNeeded("auto_rename_folders", updateResult.MutationResult);
            }
        }
        finally
        {
            if (HasLibraryMutationDeltaChanges(batchMutation))
            {
                Stopwatch mutationStopwatch = Stopwatch.StartNew();
                ApplyLibraryMutationDeltaWithPerformanceContext(batchMutation, "auto_rename_folders");
                mutationStopwatch.Stop();
                mutationApplyMs = mutationStopwatch.ElapsedMilliseconds;
            }
            tailStopwatch.Stop();
            LogInstallPerformance("auto_rename_folders_batch tail_done op=" + metrics.OperationId
                + " movedFolders=" + movedFolderCount
                + " reverseLookupMs=" + reverseLookupMs
                + " mutationApplyMs=" + mutationApplyMs
                + " elapsedMs=" + tailStopwatch.ElapsedMilliseconds);
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

    private static void AppendLibraryMutationDelta(LibraryMutationDelta target, LibraryMutationDelta source)
    {
        if (target == null || source == null)
        {
            return;
        }
        target.ChartRemoveRequests.AddRange(source.ChartRemoveRequests);
        target.ChartPathChanges.AddRange(source.ChartPathChanges);
        target.FolderPathChanges.AddRange(source.FolderPathChanges);
        target.UpdatedInstallDestinations.AddRange(source.UpdatedInstallDestinations);
        target.UpdatedInstalledPackagePaths.AddRange(source.UpdatedInstalledPackagePaths);
        target.Failures.AddRange(source.Failures);
        target.NotifyStorageRowPathChanges |= source.NotifyStorageRowPathChanges;
        target.RaiseInstalledPackagesChanged |= source.RaiseInstalledPackagesChanged;
        target.InvalidateInstalledDirectoryIndex |= source.InvalidateInstalledDirectoryIndex;
        target.InvalidateParentFolderCache |= source.InvalidateParentFolderCache;
        target.ClearDuplicatedCache |= source.ClearDuplicatedCache;
        target.RenamedCount += source.RenamedCount;
        target.DuplicateDeletedCount += source.DuplicateDeletedCount;
        target.SkippedCount += source.SkippedCount;
        target.TotalMs += source.TotalMs;
    }

    private static bool HasLibraryMutationDeltaChanges(LibraryMutationDelta delta)
    {
        return delta != null
            && (delta.ChartRemoveRequests.Count > 0
                || delta.ChartPathChanges.Count > 0
                || delta.FolderPathChanges.Count > 0
                || delta.UpdatedInstallDestinations.Count > 0
                || delta.UpdatedInstalledPackagePaths.Count > 0
                || delta.Failures.Count > 0
                || delta.NotifyStorageRowPathChanges
                || delta.RaiseInstalledPackagesChanged
                || delta.InvalidateInstalledDirectoryIndex
                || delta.InvalidateParentFolderCache
                || delta.ClearDuplicatedCache);
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
        if (srcDir == null)
        {
            throw new ArgumentNullException("srcDir");
        }
        if (newName == null)
        {
            throw new ArgumentNullException("newName");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(RenameChartFolder)))
        {
            return;
        }
        if (!renameRootFolder && getBMSDirectories().Contains(srcDir, StringComparer.OrdinalIgnoreCase))
        {
            dialogService.Show(string.Format(Resources.Warn_CannotRenameRootFolder, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        newName = NormalizeAutoRenameFolderName(newName);
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
                    MoveLibraryChartFolderInternal(srcDir, dstDir, unregister, notifyStorageRowPathChanges: false);
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
        if (TryBlockLr2SongDbSyncMutation(nameof(MoveLibraryRootFolder)))
        {
            return;
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
                        MoveLibraryChartFolderInternal(plan.SourceDirectory, plan.DestinationDirectory, unregister, notifyStorageRowPathChanges: true);
                    }
                }
            }
        }
    }

    private void MoveLibraryChartFolderInternal(string srcDir, string dstDir, bool? unregister, bool notifyStorageRowPathChanges)
    {
        if (!TryMoveLibraryChartFolder(srcDir, dstDir))
        {
            return;
        }
        if (unregister != false && unregister != true)
        {
            return;
        }
        LibraryMutationDelta delta = libraryFileOperationsService.BuildFolderMoveDelta(
            srcDir,
            dstDir,
            CreateOwnedRealPathChartRefsUnsafe(srcDir),
            CreateInstallDestinationOverlayChartRefSnapshotUnsafe(),
            ChartPackagesPending,
            ChartPackagesInstalled,
            unregister == true,
            notifyStorageRowPathChanges: notifyStorageRowPathChanges);
        ApplyLibraryMutationDelta(delta);
    }

    private bool TryMoveLibraryChartFolder(string srcDir, string dstDir)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (File.Exists(dstDir) || Directory.Exists(dstDir))
        {
            dialogService.Show(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return false;
        }
        try
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = libraryFileOperationsService.MoveFolderAndUpdateReferences(srcDir, dstDir, directoryResourceLookupCache, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
            LogReverseLookupMutationAndQueueWarmupIfNeeded("move_folder", reverseLookupMutation);
            return true;
        }
        catch (Exception moveException)
        {
            dialogService.Show(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }
    }

    private bool TryMoveLibraryChartFolderFileOnly(string srcDir, string dstDir)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (File.Exists(dstDir) || Directory.Exists(dstDir))
        {
            dialogService.Show(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return false;
        }
        try
        {
            libraryFileOperationsService.MoveFolder(srcDir, dstDir, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
            return true;
        }
        catch (Exception moveException)
        {
            dialogService.Show(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }
    }

    private InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshotUnsafe()
    {
        lock (installDestinationRuntimeStatesLock)
        {
            return installDestinationOverlayChartRefSnapshot ??= InstallDestinationOverlayChartRefSnapshot
                .FromCharts(CreateCurrentInstallDestinationCleanupChartsUnsafe());
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

    internal void RenameBMSFilesExtensions(IEnumerable<ChartFile> charts, string newExt, bool? unregister = false)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        if (TryBlockLr2SongDbSyncMutation(nameof(RenameBMSFilesExtensions)))
        {
            return;
        }
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
                    CreateOwnedCanonicalChartLookupUnsafe());
            }
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts, bool sendToRecycleBin = true, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        if (TryBlockLr2SongDbSyncMutation(nameof(RemoveLibraryCharts)))
        {
            return;
        }
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
                        CreateOwnedCanonicalChartLookupUnsafe(),
                        CreateInstallDestinationOverlayChartRefSnapshotUnsafe(),
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
        ApplyLibraryMutationDeltaWithPerformanceContext(delta, performanceLogContext: null);
    }

    private void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(ApplyLibraryMutationDelta));
        OwnedChartCollectionMutationResult mutationResult = null;
        bool collectPerformanceLog = !string.IsNullOrWhiteSpace(performanceLogContext);
        Stopwatch totalStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
        long resourceHealthBeginMs = 0;
        long buildMutationMs = 0;
        long publishNotificationMs = 0;
        long unregisterStorageRowsMs = 0;
        long stateApplyMs = 0;
        long stateFolderDbMs = 0;
        long statePathMemoryApplyMs = 0;
        long stateBmsPathDbMs = 0;
        long stateBmsonPathDbMs = 0;
        long statePackageApplyMs = 0;
        long ownedCollectionApplyMs = 0;
        long resourceHealthDisposeMs = 0;
        long lr2NormalFolderSyncMs = 0;
        long dispatchMs = 0;
        try
        {
            Stopwatch resourceHealthBeginStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
            ResourceHealthInputMutationScope resourceHealthMutation = BeginResourceHealthInputMutation();
            resourceHealthBeginMs = StopPerformanceStepStopwatch(resourceHealthBeginStopwatch);
            try
            {
                Stopwatch buildMutationStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                mutationResult = BuildOwnedChartCollectionMutationResult(
                    delta,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                buildMutationMs = StopPerformanceStepStopwatch(buildMutationStopwatch);

                Stopwatch publishNotificationStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                PublishOwnedCollectionChangeNotification(mutationResult);
                publishNotificationMs = StopPerformanceStepStopwatch(publishNotificationStopwatch);
                using (mutationResult.ResourceHealthIndexInvalidated ? SuppressResourceHealthIndexInvalidation() : null)
                {
                    Stopwatch unregisterStorageRowsStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                    StorageRowsVersionSnapshot storageRowsVersion = ApplyLibraryUnregisterStorageRowsUnsafe(mutationResult.StorageMutation);
                    unregisterStorageRowsMs = StopPerformanceStepStopwatch(unregisterStorageRowsStopwatch);

                    Stopwatch stateApplyStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                    BmsLibraryStateApplyResult stateApplyResult = stateApplier.ApplyLibraryMutationDelta(delta, mutationResult.StorageMutation.RemoveRequests);
                    stateApplyMs = StopPerformanceStepStopwatch(stateApplyStopwatch);
                    stateFolderDbMs = stateApplyResult?.FolderDbMs ?? 0;
                    statePathMemoryApplyMs = stateApplyResult?.PathMemoryApplyMs ?? 0;
                    stateBmsPathDbMs = stateApplyResult?.BmsPathDbMs ?? 0;
                    stateBmsonPathDbMs = stateApplyResult?.BmsonPathDbMs ?? 0;
                    statePackageApplyMs = stateApplyResult?.PackageApplyMs ?? 0;

                    Stopwatch ownedCollectionApplyStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                    ApplyOwnedChartCollectionMutation(mutationResult.StorageMutation, storageRowsVersion);
                    ownedCollectionApplyMs = StopPerformanceStepStopwatch(ownedCollectionApplyStopwatch);
                }
            }
            finally
            {
                Stopwatch resourceHealthDisposeStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                resourceHealthMutation.Dispose();
                resourceHealthDisposeMs = StopPerformanceStepStopwatch(resourceHealthDisposeStopwatch);
            }
            mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion ??= resourceHealthMutation.TargetInputVersion;
            if (mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion.Value < 0)
            {
                mutationResult.ResourceHealthMutation.Invalidate = true;
            }
            Stopwatch lr2NormalFolderSyncStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
            SyncLr2NormalFoldersForOwnedMutation(mutationResult.StorageMutation, performanceLogContext ?? "library_delta");
            lr2NormalFolderSyncMs = StopPerformanceStepStopwatch(lr2NormalFolderSyncStopwatch);
        }
        catch
        {
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
                PublishOwnedCollectionChangeNotification(mutationResult);
            }
            if (mutationResult?.ResourceHealthMutation.HasChanges == true)
            {
                ForceInvalidateResourceHealthIndex("library_delta_failed");
            }
            if (mutationResult?.InstallDestinationRuntimeStateMutation.HasChanges == true)
            {
                PruneInstallDestinationRuntimeStatesToCurrentOwnedCharts();
            }
            InvalidateOwnedChartCollection();
            if (mutationResult != null)
            {
                ClearNormalLibraryRefreshNotification(mutationResult);
            }
            throw;
        }
        Stopwatch dispatchStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
        DispatchOwnedChartCollectionMutation(mutationResult, "library_delta");
        dispatchMs = StopPerformanceStepStopwatch(dispatchStopwatch);
        if (collectPerformanceLog)
        {
            LogInstallPerformance("library_mutation_delta_apply context=" + performanceLogContext
                + " unregisterCharts=" + (delta?.ChartRemoveRequests?.Count ?? 0)
                + " pathChanges=" + (delta?.ChartPathChanges?.Count ?? 0)
                + " folderPathChanges=" + (delta?.FolderPathChanges?.Count ?? 0)
                + " installDestinations=" + (delta?.UpdatedInstallDestinations?.Count ?? 0)
                + " installedPackagePaths=" + (delta?.UpdatedInstalledPackagePaths?.Count ?? 0)
                + " resourceHealthBeginMs=" + resourceHealthBeginMs
                + " buildMutationMs=" + buildMutationMs
                + " publishNotificationMs=" + publishNotificationMs
                + " unregisterStorageRowsMs=" + unregisterStorageRowsMs
                + " stateApplyMs=" + stateApplyMs
                + " stateFolderDbMs=" + stateFolderDbMs
                + " statePathMemoryApplyMs=" + statePathMemoryApplyMs
                + " stateBmsPathDbMs=" + stateBmsPathDbMs
                + " stateBmsonPathDbMs=" + stateBmsonPathDbMs
                + " statePackageApplyMs=" + statePackageApplyMs
                + " ownedCollectionApplyMs=" + ownedCollectionApplyMs
                + " resourceHealthDisposeMs=" + resourceHealthDisposeMs
                + " lr2NormalFolderSyncMs=" + lr2NormalFolderSyncMs
                + " dispatchMs=" + dispatchMs
                + " elapsedMs=" + StopPerformanceStepStopwatch(totalStopwatch));
        }
    }

    private StorageRowsVersionSnapshot ApplyLibraryUnregisterStorageRowsUnsafe(OwnedChartCollectionStorageMutation mutation)
    {
        List<OwnedChartRemoveRequest> removeRequests = [.. (mutation?.RemoveRequests ?? []).Where(request => request != null)];
        if (removeRequests.Count == 0)
        {
            return CaptureStorageRowsVersionUnsafe();
        }

        List<BMSFile> bmsFilesToUnregister = [.. removeRequests
            .Select(request => request.BmsOwner)
            .Where(file => file != null)
            .Distinct()];
        HashSet<BMSFile> removedFileRefs = null;
        if (bmsFilesToUnregister.Count > 0)
        {
            removedFileRefs = new HashSet<BMSFile>(bmsFilesToUnregister);
        }

        var bmsPathCleanupKeys = new HashSet<string>(
            removeRequests
                .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bms)
                .Select(request => CreateOwnedPathKey(request.Path))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        List<LR2SongDBExtended.bmson_song> bmsonSongsToUnregister = [.. removeRequests
            .Select(request => request.BmsonOwner)
            .Where(song => song != null)
            .Distinct()];
        HashSet<LR2SongDBExtended.bmson_song> removedSongRefs = null;
        if (bmsonSongsToUnregister.Count > 0)
        {
            removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(bmsonSongsToUnregister);
        }
        var bmsonPathCleanupKeys = new HashSet<string>(
            removeRequests
                .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bmson)
                .Select(request => CreateOwnedPathKey(request.Path))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        lock (lockStorageRowsVersion)
        {
            int previousBmsRowsVersion = bmsStorageRowsVersion;
            int previousBmsonRowsVersion = bmsonStorageRowsVersion;
            if (bmsFilesToUnregister.Count > 0 || bmsPathCleanupKeys.Count > 0)
            {
                _BMSFiles = [.. (_BMSFiles ?? []).Where(file => !IsMatchedUnregisteredBmsFile(file, removedFileRefs, bmsPathCleanupKeys))];
                IncrementBmsStorageRowsVersion();
            }
            if (bmsonSongsToUnregister.Count > 0 || bmsonPathCleanupKeys.Count > 0)
            {
                _BmsonSongs = [.. (_BmsonSongs ?? []).Where(song => !IsMatchedUnregisteredBmsonSong(song, removedSongRefs, bmsonPathCleanupKeys))];
                IncrementBmsonStorageRowsVersion();
            }
            return CreateStorageRowsVersionSnapshotUnsafe(previousBmsRowsVersion, previousBmsonRowsVersion);
        }
    }

    private static bool IsMatchedUnregisteredBmsFile(BMSFile file, ISet<BMSFile> removedFiles, ISet<string> pathCleanupKeys)
    {
        if (file == null)
        {
            return false;
        }
        if (removedFiles?.Contains(file) == true)
        {
            return true;
        }
        if (pathCleanupKeys?.Count > 0 != true)
        {
            return false;
        }
        string pathKey = CreateOwnedPathKey(file.path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupKeys?.Contains(pathKey) == true;
    }

    private static bool IsMatchedUnregisteredBmsonSong(LR2SongDBExtended.bmson_song song, ISet<LR2SongDBExtended.bmson_song> removedSongs, ISet<string> pathCleanupKeys)
    {
        if (song == null)
        {
            return false;
        }
        if (removedSongs?.Contains(song) == true)
        {
            return true;
        }
        if (pathCleanupKeys?.Count > 0 != true)
        {
            return false;
        }
        string pathKey = CreateOwnedPathKey(song.path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupKeys?.Contains(pathKey) == true;
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
        int version = Interlocked.Increment(ref latestNormalLibraryRefreshNotificationVersion);
        int ownedCollectionVersion = result.OwnedCollectionVersion > 0 ? result.OwnedCollectionVersion : OwnedChartCollectionVersion;
        bool storageRowsRemoveDeltaComplete = IsCompleteRemoveOnlyStorageRowsMutation(result);
        IReadOnlyList<BMSFile> removedBmsFiles = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsStorageRowDelta(result.StorageMutation)
            : [];
        IReadOnlyList<LR2SongDBExtended.bmson_song> removedBmsonSongs = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsonStorageRowDelta(result.StorageMutation)
            : [];
        var notification = new NormalLibraryRefreshNotification(
            version,
            ownedCollectionVersion,
            effects,
            installDestinationChangedCharts,
            result.StorageRowsChanged,
            resetsPriorNotifications: false,
            notifiesBmsFiles: result.BmsFilesStorageRowsChanged,
            notifiesBmsonSongs: result.BmsonSongsStorageRowsChanged,
            removedBmsFiles: removedBmsFiles,
            removedBmsonSongs: removedBmsonSongs,
            storageRowsRemoveDeltaComplete: storageRowsRemoveDeltaComplete);
        lock (latestNormalLibraryRefreshNotificationLock)
        {
            latestNormalLibraryRefreshNotification = notification;
            normalLibraryRefreshNotifications.Add(notification);
            result.NormalLibraryRefreshNotificationVersion = version;
        }
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
        int version = Interlocked.Increment(ref latestNormalLibraryRefreshNotificationVersion);
        bool notifiesStorageRows = notifiesBmsFiles || notifiesBmsonSongs;
        var notification = new NormalLibraryRefreshNotification(
            version,
            OwnedChartCollectionVersion,
            LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
            [],
            notifiesStorageRows: notifiesStorageRows,
            resetsPriorNotifications: true,
            notifiesBmsFiles: notifiesBmsFiles,
            notifiesBmsonSongs: notifiesBmsonSongs);
        lock (latestNormalLibraryRefreshNotificationLock)
        {
            latestNormalLibraryRefreshNotification = notification;
            normalLibraryRefreshNotifications.Add(notification);
        }
        RaisePropertyChanged(() => NormalLibraryRefreshNotificationVersion);
    }

    private void PublishExternalReplacementNormalLibraryRefreshNotification(bool notifiesBmsFiles, bool notifiesBmsonSongs)
    {
        PublishNormalLibraryRefreshResetNotification(notifiesBmsFiles, notifiesBmsonSongs);
    }

    private static IReadOnlyList<ChartFile> DistinctChartsByNotificationKey(IEnumerable<ChartFile> charts)
    {
        var result = new List<ChartFile>();
        var resultIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }
            string key = CreateNormalLibraryRefreshNotificationKey(chart);
            if (resultIndexByKey.TryGetValue(key, out int index))
            {
                result[index] = chart;
            }
            else
            {
                resultIndexByKey[key] = result.Count;
                result.Add(chart);
            }
        }
        return result;
    }

    private static string CreateNormalLibraryRefreshNotificationKey(ChartFile chart)
    {
        string kind = chart?.Kind.ToString() ?? string.Empty;
        string path = chart?.Path ?? string.Empty;
        string md5 = chart?.Md5 ?? string.Empty;
        string sha256 = chart?.Sha256 ?? string.Empty;
        return kind + "\n" + path + "\n" + md5 + "\n" + sha256;
    }

    private void ClearNormalLibraryRefreshNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null || result.NormalLibraryRefreshNotificationVersion <= 0)
        {
            return;
        }
        lock (latestNormalLibraryRefreshNotificationLock)
        {
            if (latestNormalLibraryRefreshNotification.Version == result.NormalLibraryRefreshNotificationVersion)
            {
                latestNormalLibraryRefreshNotification = NormalLibraryRefreshNotification.Empty;
            }
            normalLibraryRefreshNotifications.RemoveAll(notification => notification.Version == result.NormalLibraryRefreshNotificationVersion);
        }
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
