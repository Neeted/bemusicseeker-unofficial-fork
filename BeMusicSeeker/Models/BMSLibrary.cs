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
using SevenZipExtractor;
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

    private Dictionary<string, List<string>> installedDirectoryIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    private bool installedDirectoryIndexInitialized;

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

    private readonly object lockDeferredMaintenanceTableCheck = new object();

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedAll;

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedMin;

    private PropertyChangedEventListener listenerForRwlockBMSFilesDuplicated;

    private PropertyChangedEventListener listenerForRwlockBMSFilesPendingInstall;

    private PropertyChangedEventListener listenerForRwlockBMSFiles;

    private List<BMSFile> _BMSFiles = new List<BMSFile>();

    private List<DuplicateGroup> _BMSFilesDuplicated;

    private DispatcherCollection<BMSPackage> _BMSPackagesPending = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private DispatcherCollection<BMSPackage> _BMSPackagesInstalled = new DispatcherCollection<BMSPackage>(DispatcherHelper.UIDispatcher);

    private DispatcherCollection<string> _BMSParentFolderList = new DispatcherCollection<string>(DispatcherHelper.UIDispatcher);

    private List<BMSScore> _BMSScores = new List<BMSScore>();

    private int _LR2ID;

    private bool _IsWriteLockHeldInitializeBMSFilesHealthStatus = true;

    private bool _IsWriteLockHeldInitializeBMSFilesEncodingInfo = true;

    private bool _IsWriteLockHeldInitializeBMSFilesZeroNote = true;

    private static Regex lr2IRScoreRegex = new Regex("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static readonly Uri rankingInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking");

    private static readonly Uri rankingDataUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/ranking/");

    private static readonly Uri songInfoUrl = new Uri("http://www.ribbit.xyz/bms/services/lr2ircache/info/");

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
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
                BMSFilesDuplicated = null;
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                }).Logging("BMSFiles");
                RaisePropertyChanged(() => BMSParentFolderList);
            }
        }
    }

    public List<BMSFile> BMSFilesUnregistered => BMSFiles.Where((BMSFile f) => string.IsNullOrWhiteSpace(f.parent)).ToList();

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixed => GetBMSFilesNeedToBeFixed(BMSFiles);

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
    /// BMS ファイルが格納されている親フォルダ一覧です。キャッシュ機構により遅延再構築されます。
    /// </summary>
    public DispatcherCollection<string> BMSParentFolderList
    {
        get
        {
            lock (lockParentFolderList)
            {
                RefreshBMSParentFolderListCacheUnsafe();
                return _BMSParentFolderList;
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
    /// 親フォルダ一覧キャッシュが無効化されており再構築が必要かどうかを返します。
    /// </summary>
    public bool IsBMSParentFolderListCacheDirty()
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
    public ParentFolderListCacheSnapshot BuildBMSParentFolderListCacheSnapshot()
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
    public bool TryApplyBMSParentFolderListCacheSnapshot(ParentFolderListCacheSnapshot snapshot)
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
            List<string> items = enumerable.Except(_BMSParentFolderList).ToList();
            List<string> items2 = _BMSParentFolderList.Except(enumerable).ToList();
            _BMSParentFolderList.AddRange(items);
            _BMSParentFolderList.Remove(items2);
            bmsParentFolderListDirty = false;
            LogInstallPerformance("parent_folder_cache rebuildMs=" + snapshot.RebuildMs + " added=" + items.Count + " removed=" + items2.Count + " total=" + _BMSParentFolderList.Count);
            return true;
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
        List<string> items = enumerable.Except(_BMSParentFolderList).ToList();
        List<string> items2 = _BMSParentFolderList.Except(enumerable).ToList();
        _BMSParentFolderList.AddRange(items);
        _BMSParentFolderList.Remove(items2);
        bmsParentFolderListDirty = false;
        stopwatch.Stop();
        LogInstallPerformance("parent_folder_cache rebuildMs=" + stopwatch.ElapsedMilliseconds + " added=" + items.Count + " removed=" + items2.Count + " total=" + _BMSParentFolderList.Count);
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

    private bool UseLR2 => Settings.Default.OperationModeLR2DB;

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
        dialogService = new BmsLibraryDialogService();
        dbGateway = new BmsLibraryDbGateway(lr2SongDBPath, lr2ScoreDBPath);
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
        using (rwlockBMSScores.GetReaderGuard())
        {
            return BMSScores;
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
        BmsScanExecutionResult scanResult = scanner.Scan(bmsDirectories, BMSFile.bmsExtensions, everythingScanLoggingEnabled);
        if (!scanResult.Success || scanResult.Result == null)
        {
            LogEverythingScan("BMS file scan fallback reason=" + (scanResult?.ErrorReason ?? "unknown"));
            scanResult = fallbackScanner.Scan(bmsDirectories, BMSFile.bmsExtensions, everythingScanLoggingEnabled);
        }
        if (scanResult?.Result == null)
        {
            throw new InvalidOperationException("BMS file scan failed");
        }
        if (everythingVerifyEnabled)
        {
            Stopwatch stopwatchVerify = Stopwatch.StartNew();
            BmsScanExecutionResult fastScanResult = fallbackScanner.Scan(bmsDirectories, BMSFile.bmsExtensions, everythingVerifyEnabled);
            stopwatchVerify.Stop();
            if (fastScanResult.Success && fastScanResult.Result != null)
            {
                BmsScanDiffReport report = BmsScanResultComparer.Compare(scanResult.Result, fastScanResult.Result);
                LogEverythingVerify("everything_verify comparedMs=" + stopwatchVerify.ElapsedMilliseconds + " bmsDiff=" + report.BmsPathDiffCount + " dirDiff=" + report.DirectoryDiffCount + " fileDiff=" + report.FileDiffCount + " match=" + report.IsMatch.ToString().ToLowerInvariant());
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
        bool scheduleDeferredMaintenanceTableCheck = false;
        string deferredMaintenanceReason = ((reloadScoresOnly == false) ? "reload_files" : "initialize");
        bool songTblLoad = reloadScoresOnly != true;
        bool songTblFileCheck = reloadScoresOnly == false || (reloadScoresOnly != true && !Settings.Default.SkipInitFileCheck);
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
        using (rwlockBMSFilesInitializedAll.GetWriterGuard())
        {
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
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck, setMainteInfo: setMaintenanceInfo, updateIrScore: true, installTblCheck: false, maintenanceTblCheck: false, bmsScanPrefetchInfo);
                },
                delegate
                {
                    _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, flag, maintenanceTblCheck: false);
                });
            scheduleDeferredMaintenanceTableCheck = flag;
            TimeSpan timeSpan = DateTime.Now - now;
            NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());
        }
        if (flag)
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                foreach (BMSPackage item in BMSPackagesPending)
                {
                    SearchEstimatedInstallationDirectory(item);
                }
            }
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
                LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
                try
                {
                    List<string> pragmaLogs = lr2Song.TryApplyReadOptimizedPragmas(Settings.Default.EnableReadOptimizedPragmas);
                    LogInstallPerformance("db_read_pragmas scope=song_tbl_load " + string.Join(" ", pragmaLogs));
                    long songTableLoadMs = 0L;
                    long songNormalizeLoopMs = 0L;
                    long folderTableLoadMs = 0L;
                    long folderNormalizeLoopMs = 0L;
                    long fixApplyMs = 0L;
                    long maintenanceTableLoadMs = 0L;
                    long maintenanceMapBuildMs = 0L;
                    long maintenanceApplyMs = 0L;
                    long bmsFilesAssignMs = 0L;
                    long commitMs = 0L;
                    bool dbWriteRequired = false;
                    long dbWriteMs = 0L;
                    long songCountMs = 0L;
                    long songMaterializeMs = 0L;
                    long maintenanceCountMs = 0L;
                    long maintenanceMaterializeMs = 0L;
                    Stopwatch stopwatchSongTableLoad = Stopwatch.StartNew();
                    long songTableCount = 0L;
                    Stopwatch stopwatchSongCount = Stopwatch.StartNew();
                    try
                    {
                        songTableCount = Math.Max(0L, lr2Song.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
                    }
                    catch
                    {
                    }
                    stopwatchSongCount.Stop();
                    songCountMs = stopwatchSongCount.ElapsedMilliseconds;
                    List<BMSFile> list = (songTableCount > 0 && songTableCount <= int.MaxValue) ? new List<BMSFile>((int)songTableCount) : new List<BMSFile>();
                    Stopwatch stopwatchSongMaterialize = Stopwatch.StartNew();
                    using (BMSFile.SuppressPropertyChangedScope())
                    {
                        foreach (BMSFile item in lr2Song.Table<BMSFile>())
                        {
                            list.Add(item);
                        }
                    }
                    stopwatchSongMaterialize.Stop();
                    songMaterializeMs = stopwatchSongMaterialize.ElapsedMilliseconds;
                    stopwatchSongTableLoad.Stop();
                    songTableLoadMs = stopwatchSongTableLoad.ElapsedMilliseconds;
                    List<BMSFile> deleteFiles = new List<BMSFile>();
                    NLogWrapper.DebuggerLogger?.Trace("relative path and invalid md5 check");
                    if (!string.IsNullOrWhiteSpace(lr2Song.LR2RootPath))
                    {
                        Encoding crcEncoding = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                        List<string> deletePaths = new List<string>();
                        int unixtime = (DateTime.Now + new TimeSpan(30, 0, 0, 0)).ToUnixtime();
                        bool leapYearDetected = false;
                        int relativePathFixed = 0;
                        int crcRecalculated = 0;
                        int crcSkipped = 0;
                        List<BMSFile> list2 = new List<BMSFile>();
                        Stopwatch stopwatchSongNormalizeLoop = Stopwatch.StartNew();
                        foreach (BMSFile e in list)
                        {
                            try
                            {
                                if (string.IsNullOrWhiteSpace(e.hash))
                                {
                                    deletePaths.Add(e.path);
                                    deleteFiles.Add(e);
                                    continue;
                                }
                                if (!Path.IsPathRooted(e.path))
                                {
                                    deletePaths.Add(e.path);
                                    e.path = Path.Combine(lr2Song.LR2RootPath, e.path);
                                    string directoryName = Path.GetDirectoryName(e.path);
                                    e.folder = ComputeLR2DirectoryHash(directoryName, crcEncoding);
                                    e.parent = ComputeLR2DirectoryHash(Path.GetDirectoryName(directoryName), crcEncoding);
                                    list2.Add(e);
                                    relativePathFixed++;
                                    crcRecalculated++;
                                    continue;
                                }
                                if (e.adddate < 0 || e.adddate > unixtime)
                                {
                                    e.adddate = null;
                                    e.date = null;
                                    leapYearDetected = true;
                                    list2.Add(e);
                                    continue;
                                }
                                if (IsLikelyCrcHex(e.folder) && IsLikelyCrcHex(e.parent))
                                {
                                    crcSkipped++;
                                    continue;
                                }
                                string directoryName2 = Path.GetDirectoryName(e.path);
                                string text = ComputeLR2DirectoryHash(directoryName2, crcEncoding);
                                string text2 = ComputeLR2DirectoryHash(Path.GetDirectoryName(directoryName2), crcEncoding);
                                if (text != e.folder || text2 != e.parent)
                                {
                                    e.folder = text;
                                    e.parent = text2;
                                    list2.Add(e);
                                }
                                crcRecalculated++;
                            }
                            catch
                            {
                                deletePaths.Add(e.path);
                                deleteFiles.Add(e);
                            }
                        }
                        stopwatchSongNormalizeLoop.Stop();
                        songNormalizeLoopMs = stopwatchSongNormalizeLoop.ElapsedMilliseconds;
                        List<string> deleteFolders = new List<string>();
                        Stopwatch stopwatchFolderTableLoad = Stopwatch.StartNew();
                        List<LR2SongDB.folder> list3 = lr2Song.Table<LR2SongDB.folder>().ToList();
                        stopwatchFolderTableLoad.Stop();
                        folderTableLoadMs = stopwatchFolderTableLoad.ElapsedMilliseconds;
                        Stopwatch stopwatchFolderNormalizeLoop = Stopwatch.StartNew();
                        list3 = list3.Where(delegate (LR2SongDB.folder e)
                        {
                            try
                            {
                                if (!Path.IsPathRooted(e.path))
                                {
                                    if (!e.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !e.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "Rival" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    {
                                        deleteFolders.Add(e.path);
                                        e.path = Path.Combine(lr2Song.LR2RootPath, e.path);
                                        string directoryName = Path.GetDirectoryName(e.path);
                                        if (e.path.EndsWith("\\"))
                                        {
                                            directoryName = Path.GetDirectoryName(directoryName);
                                        }
                                        if (e.parent != "e2977170")
                                        {
                                            e.parent = ComputeLR2DirectoryHash(directoryName, crcEncoding);
                                        }
                                        return true;
                                    }
                                }
                                else
                                {
                                    if (e.adddate < 0 || e.adddate > unixtime)
                                    {
                                        e.adddate = null;
                                        e.date = null;
                                        leapYearDetected = true;
                                        return true;
                                    }
                                    if ((e.date < 0 || !e.date.HasValue) && e.type == 1 && !string.IsNullOrWhiteSpace(e.path))
                                    {
                                        string text = e.path.TrimEnd('\\');
                                        if (Directory.Exists(text))
                                        {
                                            DateTime lastWriteTime = Directory.GetLastWriteTime(text);
                                            if ((new DateTime(2012, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2012, 3, 2)) || (new DateTime(2016, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2016, 3, 2)) || (new DateTime(2020, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2020, 3, 2)))
                                            {
                                                DateTime now = DateTime.Now;
                                                if (DispatcherMessageBox.Show(string.Format(Resources.Warn_LR2LeapYearFolderDetected, text, lastWriteTime.ToShortDateString(), now.ToShortDateString()), Resources.MessageBoxTitle_Warning, MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) == MessageBoxResult.Yes)
                                                {
                                                    try
                                                    {
                                                        fileMutationService.SetTimestamps(text, isDirectory: true, creationTime: null, lastWriteTime: now, targetOnlyFileMutationOptions);
                                                        e.adddate = null;
                                                        e.date = null;
                                                        leapYearDetected = true;
                                                        return true;
                                                    }
                                                    catch (Exception dateUpdateException)
                                                    {
                                                        DispatcherMessageBox.Show(string.Format(Resources.Error_FailedToChangeDate, GetDisplayedExceptionMessage(dateUpdateException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                deleteFolders.Add(e.path);
                            }
                            return false;
                        })
                            .ToList();
                        stopwatchFolderNormalizeLoop.Stop();
                        folderNormalizeLoopMs = stopwatchFolderNormalizeLoop.ElapsedMilliseconds;
                        Stopwatch stopwatchFixApply = Stopwatch.StartNew();
                        dbWriteRequired = deletePaths.Count > 0 || list2.Count > 0 || deleteFolders.Count > 0 || list3.Count > 0;
                        if (dbWriteRequired)
                        {
                            Stopwatch stopwatchDbWrite = Stopwatch.StartNew();
                            lr2Song.BeginTransaction();
                            foreach (string item in deletePaths)
                            {
                                lr2Song.Delete<LR2SongDB.song>(item);
                            }
                            foreach (BMSFile item2 in list2)
                            {
                                lr2Song.InsertOrReplace(item2, typeof(LR2SongDB.song));
                            }
                            foreach (string item3 in deleteFolders)
                            {
                                lr2Song.Delete<LR2SongDB.folder>(item3);
                            }
                            foreach (LR2SongDB.folder item4 in list3)
                            {
                                lr2Song.InsertOrReplace(item4, typeof(LR2SongDB.folder));
                            }
                            Stopwatch stopwatchCommit = Stopwatch.StartNew();
                            lr2Song.Commit();
                            stopwatchCommit.Stop();
                            commitMs = stopwatchCommit.ElapsedMilliseconds;
                            stopwatchDbWrite.Stop();
                            dbWriteMs = stopwatchDbWrite.ElapsedMilliseconds;
                        }
                        if (leapYearDetected)
                        {
                            DispatcherMessageBox.Show(Resources.Warn_LR2LeapYearBugDetected, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        stopwatchFixApply.Stop();
                        fixApplyMs = stopwatchFixApply.ElapsedMilliseconds;
                        LogInstallPerformance("song_tbl_load_detail totalSongs=" + list.Count + " deletedSongs=" + deleteFiles.Count + " updatedSongs=" + list2.Count + " relativePathFixed=" + relativePathFixed + " crcRecalculated=" + crcRecalculated + " crcSkipped=" + crcSkipped + " updatedFolders=" + list3.Count + " deletedFolders=" + deleteFolders.Count);
                    }
                    NLogWrapper.DebuggerLogger?.Trace("relative path check end");
                    Stopwatch stopwatchMaintenanceTableLoad = Stopwatch.StartNew();
                    long maintenanceTableCount = 0L;
                    Stopwatch stopwatchMaintenanceCount = Stopwatch.StartNew();
                    try
                    {
                        maintenanceTableCount = Math.Max(0L, lr2Song.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
                    }
                    catch
                    {
                    }
                    stopwatchMaintenanceCount.Stop();
                    maintenanceCountMs = stopwatchMaintenanceCount.ElapsedMilliseconds;
                    List<BMSFileMaintenanceInfo> list4 = (maintenanceTableCount > 0 && maintenanceTableCount <= int.MaxValue) ? new List<BMSFileMaintenanceInfo>((int)maintenanceTableCount) : new List<BMSFileMaintenanceInfo>();
                    Stopwatch stopwatchMaintenanceMaterialize = Stopwatch.StartNew();
                    using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
                    {
                        foreach (BMSFileMaintenanceInfo item in lr2Song.Table<BMSFileMaintenanceInfo>())
                        {
                            list4.Add(item);
                        }
                    }
                    stopwatchMaintenanceMaterialize.Stop();
                    maintenanceMaterializeMs = stopwatchMaintenanceMaterialize.ElapsedMilliseconds;
                    stopwatchMaintenanceTableLoad.Stop();
                    maintenanceTableLoadMs = stopwatchMaintenanceTableLoad.ElapsedMilliseconds;
                    Stopwatch stopwatchMaintenanceMapBuild = Stopwatch.StartNew();
                    Dictionary<string, BMSFileMaintenanceInfo> dictionary = new Dictionary<string, BMSFileMaintenanceInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (BMSFileMaintenanceInfo item in list4)
                    {
                        if (!string.IsNullOrWhiteSpace(item.path) && !dictionary.ContainsKey(item.path))
                        {
                            dictionary[item.path] = item;
                        }
                    }
                    stopwatchMaintenanceMapBuild.Stop();
                    maintenanceMapBuildMs = stopwatchMaintenanceMapBuild.ElapsedMilliseconds;
                    HashSet<BMSFile> hashSet = ((deleteFiles.Count > 0) ? new HashSet<BMSFile>(deleteFiles) : null);
                    List<BMSFile> list5 = new List<BMSFile>(list.Count - deleteFiles.Count);
                    Stopwatch stopwatchMaintenanceApply = Stopwatch.StartNew();
                    foreach (BMSFile item2 in list)
                    {
                        if (hashSet != null && hashSet.Contains(item2))
                        {
                            continue;
                        }
                        if (dictionary.TryGetValue(item2.path, out BMSFileMaintenanceInfo value))
                        {
                            if (item2.HasMaintenanceInfoHash(value.hash) || string.Equals(value.hash, item2.hash, StringComparison.OrdinalIgnoreCase))
                            {
                                item2.SetMaintenanceInfo(value, suppressPropertyChanged: true, registerEventHandlers: false);
                            }
                            else
                            {
                                item2.SetMaintenanceInfo(new BMSFileMaintenanceInfo(item2), suppressPropertyChanged: true, registerEventHandlers: false);
                            }
                        }
                        else
                        {
                            item2.SetMaintenanceInfo(new BMSFileMaintenanceInfo(item2), suppressPropertyChanged: true, registerEventHandlers: false);
                        }
                        list5.Add(item2);
                    }
                    stopwatchMaintenanceApply.Stop();
                    maintenanceApplyMs = stopwatchMaintenanceApply.ElapsedMilliseconds;
                    Stopwatch stopwatchBmsFilesAssign = Stopwatch.StartNew();
                    BMSFiles = list5;
                    stopwatchBmsFilesAssign.Stop();
                    bmsFilesAssignMs = stopwatchBmsFilesAssign.ElapsedMilliseconds;
                    LogInstallPerformance("song_tbl_load_maintenance_detail bmsCount=" + list5.Count + " maintenanceCount=" + list4.Count + " maintenanceKeyCount=" + dictionary.Count);
                    LogInstallPerformance("song_tbl_load_io song_read_ms=" + songTableLoadMs + " song_count_ms=" + songCountMs + " song_materialize_ms=" + songMaterializeMs + " song_count=" + songTableCount + " maintenance_read_ms=" + maintenanceTableLoadMs + " maintenance_count_ms=" + maintenanceCountMs + " maintenance_materialize_ms=" + maintenanceMaterializeMs + " maintenance_count=" + maintenanceTableCount + " folder_read_ms=" + folderTableLoadMs + " db_write_required=" + dbWriteRequired.ToString().ToLowerInvariant() + " db_write_ms=" + dbWriteMs);
                    LogInstallPerformance("song_tbl_load_breakdown song_table_load_ms=" + songTableLoadMs + " song_normalize_loop_ms=" + songNormalizeLoopMs + " folder_table_load_ms=" + folderTableLoadMs + " folder_normalize_loop_ms=" + folderNormalizeLoopMs + " fix_apply_ms=" + fixApplyMs + " maintenance_table_load_ms=" + maintenanceTableLoadMs + " maintenance_map_build_ms=" + maintenanceMapBuildMs + " maintenance_apply_ms=" + maintenanceApplyMs + " bmsfiles_assign_ms=" + bmsFilesAssignMs + " commit_ms=" + commitMs);
                }
                finally
                {
                    if (lr2Song != null)
                    {
                        ((IDisposable)lr2Song).Dispose();
                    }
                }
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
            stopwatchScoreTblLoad.Stop();
            scoreTblLoadMs = stopwatchScoreTblLoad.ElapsedMilliseconds;
        }
        if (songTblFileCheck)
        {
            Stopwatch stopwatchSongTblFileCheck = Stopwatch.StartNew();
            long scanElapsedMs = 0L;
            long dirhashBuildMs = 0L;
            long diffMs = 0L;
            long newfileParseMs = 0L;
            long applyMs = 0L;
            long dbCommitMs = 0L;
            long instlDstCleanupMs = 0L;
            bool prefetchedScanUsed = false;
            long prefetchedScanElapsedMs = 0L;
            BmsScanExecutionResult scanResult = null;
            Stopwatch stopwatchScan = Stopwatch.StartNew();
            if (bmsScanPrefetchInfo != null && bmsScanPrefetchInfo.ScanResult != null && bmsScanPrefetchInfo.ScanResult.Result != null)
            {
                scanResult = bmsScanPrefetchInfo.ScanResult;
                prefetchedScanElapsedMs = bmsScanPrefetchInfo.ElapsedMs;
                prefetchedScanUsed = true;
            }
            else
            {
                scanResult = ExecuteBmsScanWithFallback(bMSDirectories);
            }
            stopwatchScan.Stop();
            scanElapsedMs = stopwatchScan.ElapsedMilliseconds + (prefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
            BMSDirectoryFileNameHash nextFolderAllFileList = new BMSDirectoryFileNameHash();
            Stopwatch stopwatchDirhashBuild = Stopwatch.StartNew();
            if (scanResult.Result.FileNameHashesByDirectory != null && scanResult.Result.FileNameHashesByDirectory.Count > 0)
            {
                foreach (KeyValuePair<string, uint[]> item4 in scanResult.Result.FileNameHashesByDirectory)
                {
                    nextFolderAllFileList.AddDirHashed(item4.Key, item4.Value);
                }
            }
            else
            {
                foreach (KeyValuePair<string, List<string>> item5 in scanResult.Result.FilesByDirectory)
                {
                    nextFolderAllFileList.AddDir(item5.Key, item5.Value);
                }
            }
            stopwatchDirhashBuild.Stop();
            dirhashBuildMs = stopwatchDirhashBuild.ElapsedMilliseconds;
            HashSet<string> hashSet = new HashSet<string>(scanResult.Result.BmsFilePaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> hashSet2;
            Stopwatch stopwatchDiff = Stopwatch.StartNew();
            using (rwlockBMSFiles.GetReaderGuard())
            {
                hashSet2 = new HashSet<string>(BMSFiles.Select((BMSFile x) => x.path), StringComparer.OrdinalIgnoreCase);
            }
            HashSet<string> bmsFilesDeletedPaths = new HashSet<string>(hashSet2.Except(hashSet), StringComparer.OrdinalIgnoreCase);
            List<string> list4 = hashSet.Except(hashSet2).ToList();
            stopwatchDiff.Stop();
            diffMs = stopwatchDiff.ElapsedMilliseconds;
            Stopwatch stopwatchNewFileParse = Stopwatch.StartNew();
            List<BMSFile> list5 = ((list4.Count <= 0) ? new List<BMSFile>() : (from x in list4.AsParallel().Select(delegate (string l)
                {
                    BMSFile bMSFile = null;
                    try
                    {
                        bMSFile = BMSFile.CreateBMSFileFromFile(l);
                    }
                    catch (IOException ex2)
                    {
                        DispatcherMessageBox.Show(string.Format(Resources.Error_InitializationFailed, l, ex2.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        bMSFile = null;
                    }
                    return new
                    {
                        path = l,
                        file = bMSFile
                    };
                })
                                                                               where x.file != null
                                                                               select x.file).ToList());
            stopwatchNewFileParse.Stop();
            newfileParseMs = stopwatchNewFileParse.ElapsedMilliseconds;
            bool hasDbDiff = bmsFilesDeletedPaths.Count > 0 || list5.Count > 0;
            Stopwatch stopwatchApply = Stopwatch.StartNew();
            using (rwlockBMSFiles.GetWriterGuard())
            {
                if (bmsFilesDeletedPaths.Count > 0)
                {
                    BMSFiles.RemoveAll((BMSFile f) => bmsFilesDeletedPaths.Contains(f.path));
                }
                if (list5.Count > 0)
                {
                    BMSFiles.AddRange(list5);
                }
                bmsFolderAllFileList = nextFolderAllFileList;
                Stopwatch stopwatchInstlDstCleanup = Stopwatch.StartNew();
                HashSet<string> directoryKeys = new HashSet<string>(bmsFolderAllFileList.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (BMSFile item6 in BMSFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst)))
                {
                    if (!directoryKeys.Contains(item6.instl_dst))
                    {
                        item6.instl_dst = null;
                    }
                }
                stopwatchInstlDstCleanup.Stop();
                instlDstCleanupMs = stopwatchInstlDstCleanup.ElapsedMilliseconds;
            }
            stopwatchApply.Stop();
            applyMs = stopwatchApply.ElapsedMilliseconds;
            if (hasDbDiff)
            {
                Stopwatch stopwatchDbCommit = Stopwatch.StartNew();
                using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                List<string> pragmaLogs = lR2SongDBExtended.TryApplyReadOptimizedPragmas(Settings.Default.EnableReadOptimizedPragmas);
                LogInstallPerformance("db_read_pragmas scope=song_tbl_file_check " + string.Join(" ", pragmaLogs));
                lR2SongDBExtended.BeginTransaction();
                if (bmsFilesDeletedPaths.Count > 0)
                {
                    foreach (string item7 in bmsFilesDeletedPaths)
                    {
                        lR2SongDBExtended.Delete<LR2SongDB.song>(item7);
                    }
                }
                if (list5.Count > 0)
                {
                    foreach (BMSFile item8 in list5)
                    {
                        lR2SongDBExtended.InsertOrReplace(item8, typeof(LR2SongDB.song));
                    }
                }
                lR2SongDBExtended.Commit();
                stopwatchDbCommit.Stop();
                dbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
                InvalidateBMSHashIndex();
                InvalidateInstalledDirectoryIndex();
                InvalidateBMSParentFolderListCache();
            }
            LogInstallPerformance("song_tbl_file_check_breakdown scan_ms=" + scanElapsedMs + " dirhash_build_ms=" + dirhashBuildMs + " diff_ms=" + diffMs + " deleted_count=" + bmsFilesDeletedPaths.Count + " added_count=" + list5.Count + " newfile_parse_ms=" + newfileParseMs + " apply_ms=" + applyMs + " db_commit_ms=" + dbCommitMs + " instl_dst_cleanup_ms=" + instlDstCleanupMs);
            LogEverythingScan("bms_scan totalMs=" + scanElapsedMs + " bmsPaths=" + hashSet.Count + " dirs=" + bmsFolderAllFileList.Keys.Count + " prefetched=" + prefetchedScanUsed.ToString().ToLowerInvariant());
            stopwatchSongTblFileCheck.Stop();
            songTblFileCheckMs = stopwatchSongTblFileCheck.ElapsedMilliseconds;
        }
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
            SetBMSScore(BMSFiles);
            Task.Run(delegate
            {
                List<LR2IRScore> scoreTable = updateLR2IRScoreTable();
                updateBMSScores(scoreTable);
                setRankingScore();
            }).Logging("_initialize");
        }
        if (installTblCheck)
        {
            Stopwatch stopwatchInstallTblCheck = Stopwatch.StartNew();
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    BMSPackagesPending.Clear();
                    BMSPackagesInstalled.Clear();
                    InstallTableLoadResult installTableLoadResult = initializationService.LoadInstallTable(dbGateway);
                    if (installTableLoadResult.StaleInstallPaths.Count > 0)
                    {
                        dbGateway.DeleteInstallRows(installTableLoadResult.StaleInstallPaths);
                    }
                    BMSPackagesPending.AddRange(installTableLoadResult.PendingPackages);
                    BMSPackagesPending.AsParallel().ForAll(delegate (BMSPackage pkg)
                    {
                        pkg.BMSFiles.AsParallel().ForAll(delegate (BMSFile bmsFile)
                        {
                            bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                            if (ContainsBMSHashUnsafe(bmsFile.hash))
                            {
                                bmsFile.warning = Resources.Warning_AlreadyInstalled;
                            }
                            else if (!Directory.Exists(pkg.path))
                            {
                                bmsFile.warning = Resources.Warning_SingleBmsFile;
                            }
                            else
                            {
                                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true);
                            }
                        });
                    });
                }
            }
            stopwatchInstallTblCheck.Stop();
            installTblCheckMs = stopwatchInstallTblCheck.ElapsedMilliseconds;
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

    private void ScheduleDeferredMaintenanceTableCheck(string reason)
    {
        int version = 0;
        bool shouldStartWorker = false;
        lock (lockDeferredMaintenanceTableCheck)
        {
            deferredMaintenanceTableCheckRequestedVersion++;
            version = deferredMaintenanceTableCheckRequestedVersion;
            if (!deferredMaintenanceTableCheckRunning)
            {
                deferredMaintenanceTableCheckRunning = true;
                shouldStartWorker = true;
            }
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
                lock (lockDeferredMaintenanceTableCheck)
                {
                    if (requestVersion == deferredMaintenanceTableCheckRequestedVersion)
                    {
                        deferredMaintenanceTableCheckRunning = false;
                        return;
                    }
                }
            }
        });
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
        return SearchTargets.Where((string d) => Directory.Exists(d)).ToList();
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
        return list.All((BMSFile file) => IsBMSHashAvailable(file.hash) && ContainsBMSHashUnsafe(file.hash));
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
    }

    /// <summary>
    /// インストール済みディレクトリインデックスをクリアし、次回使用時に再構築されるようにマークします。
    /// </summary>
    private void InvalidateInstalledDirectoryIndex()
    {
        lock (lockInstalledDirectoryIndex)
        {
            installedDirectoryIndex.Clear();
            installedDirectoryIndexInitialized = false;
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
        Dictionary<string, List<string>> dictionary2 = CreateInstallEstimationService().BuildInstalledHashToDirectoryMap(bmsFiles);
        int num2 = dictionary2.Sum((KeyValuePair<string, List<string>> x) => x.Value.Count);
        lock (lockInstalledDirectoryIndex)
        {
            installedDirectoryIndex = dictionary2;
            installedDirectoryIndexInitialized = true;
        }
        stopwatch.Stop();
        LogInstallPerformance("installed_dir_index rebuildMs=" + stopwatch.ElapsedMilliseconds + " hashes=" + dictionary2.Count + " dirRefs=" + num2 + " files=" + num);
    }

    /// <summary>
    /// インストール済みディレクトリインデックスが未構築の場合にビルドします。
    /// </summary>
    private void EnsureInstalledDirectoryIndexBuiltUnsafe()
    {
        if (!installedDirectoryIndexInitialized)
        {
            RebuildInstalledDirectoryIndexUnsafe(BMSFiles);
        }
    }

    /// <summary>
    /// 現在のインストール済みディレクトリインデックスのスナップショットを作成します。
    /// </summary>
    private Dictionary<string, List<string>> CreateInstalledDirectoryIndexSnapshotUnsafe()
    {
        EnsureInstalledDirectoryIndexBuiltUnsafe();
        lock (lockInstalledDirectoryIndex)
        {
            return installedDirectoryIndex.ToDictionary((KeyValuePair<string, List<string>> x) => x.Key, (KeyValuePair<string, List<string>> x) => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// 指定された BMS ファイル群に対して、LR2 score.db からスコア情報を取得・反映します。
    /// </summary>
    public void SetBMSScore(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null || lr2ScoreDBPath == null)
        {
            return;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                using (rwlockBMSScores.GetWriterGuard())
                {
                    if (BMSScores == null)
                    {
                        return;
                    }
                    irService.ApplyKnownScoresToFiles(bmsFiles, BMSScores);
                }
            }
        }
    }

    private LR2IRCache getIRCache(string filePath)
    {
        return irService.LoadIrCache(filePath);
    }

    private void setBMSScore(LR2IRData data, LR2IRCache cache)
    {
        using (rwlockBMSScores.GetWriterGuard())
        {
            List<BMSFile> bmsFilesSnapshot;
            using (rwlockBMSFiles.GetReaderGuard())
            {
                bmsFilesSnapshot = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile f) => f != null).ToList());
            }
            irService.ApplyIrDataToScoresAndFiles(data, cache, lr2ScoreDBPath, BMSScores, bmsFilesSnapshot, Settings.Default.SkipEstimateOfflineScoreRanking);
        }
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
    }

    private void setRankingScore()
    {
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
                    irService.RefreshRankingScoresFromCache(LR2ID, lr2ScoreDBPath, dbGateway, BMSScores, BMSFiles, Settings.Default.SkipEstimateOfflineScoreRanking);
                }
            }
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE END");
            GC.Collect();
            NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
        }
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
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            using (rwlockBMSScores.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    return irService.DownloadIRData(LR2ID, cacheInfo, irCacheDirPath, dbGateway, irClient, rankingDataUrl, BMSScores, BMSFiles, Settings.Default.SkipEstimateOfflineScoreRanking);
                }
            }
        }
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
                    List<BMSFile> snapshot = BMSFiles.Where((BMSFile f) => f != null).ToList();
                    duplicateService.ClearDuplicateState(snapshot, DuplicateWarningMessage);
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

    /// <summary>
    /// 指定されたパス群（ファイルまたはディレクトリ）から BMS ファイルを自動検出・インストールします。
    /// アーカイブの展開、song.db への登録、Pendingパッケージ生成を一括で行います。
    /// </summary>
    /// <param name="installPaths">インストール元のファイル/ディレクトリパスのコレクション。</param>
    /// <returns>インストール処理された BMS パッケージのリスト。</returns>
    public List<BMSPackage> InstallBMSFilesAuto(IEnumerable<string> installPaths)
    {
        List<BMSPackage> pendingPackagesToEstimate = new List<BMSPackage>();
        List<BMSPackage> discoveredPackages = new List<BMSPackage>();
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
                            DispatcherMessageBox.Show(Resources.Warn_InstallAbortedFilesNotFound, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            return discoveredPackages;
                        }
                        string[] exts = new string[4] { ".zip", ".7z", ".rar", "lzh" };
                        installPaths = installPaths.Select(delegate (string p)
                        {
                            if (exts.Any((string ext) => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    string text = TempDirectoryPublisher.Get();
                                    using (ArchiveFile archiveFile = new ArchiveFile(p))
                                    {
                                        // 1. 一括解凍（ソリッド圧縮などのパフォーマンス向上のため）
                                        archiveFile.Extract(text);

                                        // 2. メタデータの復元とセキュリティチェック
                                        foreach (Entry entry in archiveFile.Entries)
                                        {
                                            string entryPath = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
                                            string fullPath = Path.GetFullPath(Path.Combine(text, entryPath));

                                            // Zip Slip 対策: 解凍先ディレクトリの中に収まっているか確認
                                            if (!fullPath.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                                            {
                                                continue;
                                            }

                                            bool isFile = !entry.IsFolder && File.Exists(fullPath);
                                            bool isDir = entry.IsFolder && Directory.Exists(fullPath);

                                            if (isFile || isDir)
                                            {
                                                // メタデータの復元
                                                try
                                                {
                                                    DateTime? creationTimeToRestore = (entry.CreationTime > DateTime.MinValue) ? entry.CreationTime : ((DateTime?)null);
                                                    DateTime? lastWriteTimeToRestore = (entry.LastWriteTime > DateTime.MinValue) ? entry.LastWriteTime : ((DateTime?)null);
                                                    if (creationTimeToRestore.HasValue || lastWriteTimeToRestore.HasValue)
                                                    {
                                                        fileMutationService.SetTimestamps(fullPath, isDir, creationTimeToRestore, lastWriteTimeToRestore, targetOnlyFileMutationOptions);
                                                    }
                                                    if (entry.LastAccessTime > DateTime.MinValue)
                                                    {
                                                        if (isFile) File.SetLastAccessTime(fullPath, entry.LastAccessTime);
                                                        else Directory.SetLastAccessTime(fullPath, entry.LastAccessTime);
                                                    }
                                                }
                                                catch (Exception metadataRestoreException)
                                                {
                                                    // 時刻設定の失敗は致命的ではないため無視
                                                    Ribbit.Logging.NLogWrapper.FileLogger?.Info(metadataRestoreException, "Failed to restore metadata for " + fullPath);
                                                }
                                            }
                                        }
                                    }
                                    return text;
                                }
                                catch
                                {
                                    DispatcherMessageBox.Show("Extract failed:" + Environment.NewLine + p, "Warning", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                                }
                            }
                            return p;
                        }).ToList();
                        AutoInstallWorkflowResult workflow = packageInstallService.PrepareAutoInstallWorkflow(
                            installPaths,
                            BMSPackagesPending,
                            BMSFiles,
                            getBMSDirectories(),
                            dupRateThreshInOnePkg,
                            (bmsFile) => checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true));
                        discoveredPackages = workflow.DiscoveredPackages.ToList();
                        LogInstallPerformance("auto_install_prepare discovered=" + discoveredPackages.Count + " autoInstall=" + workflow.AutoInstallCandidates.Count + " pendingAdd=" + workflow.PendingPackagesToAdd.Count + " pendingRemove=" + workflow.PendingPackagesToRemove.Count + " discoveryMs=" + workflow.DiscoveryMs + " classificationMs=" + workflow.ClassificationMs + " totalMs=" + workflow.TotalMs);
                        if (discoveredPackages.Count == 0)
                        {
                            return discoveredPackages;
                        }
                        if (workflow.PendingPackagesToRemove.Count > 0)
                        {
                            ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: workflow.PendingPackagesToRemove));
                        }
                        List<BMSPackage> pendingPackagesToAdd = workflow.PendingPackagesToAdd.ToList();
                        if (workflow.AutoInstallCandidates.Count > 0)
                        {
                            if (!Settings.Default.KeepInstallablePackagesPending && SearchTargets != null && SearchTargets.Count() > 0 && Directory.Exists(SearchTargets[0]))
                            {
                                List<BMSPackage> failedPackages = installBMSPackages(workflow.AutoInstallCandidates);
                                pendingPackagesToAdd = pendingPackagesToAdd.Concat(failedPackages).ToList();
                            }
                            else
                            {
                                pendingPackagesToAdd = pendingPackagesToAdd.Concat(workflow.AutoInstallCandidates).ToList();
                            }
                        }
                        if (pendingPackagesToAdd.Count > 0)
                        {
                            dbGateway.UpsertInstallRows(pendingPackagesToAdd);
                            BMSPackagesPending.AddRange(pendingPackagesToAdd);
                        }
                        pendingPackagesToEstimate = pendingPackagesToAdd;
                    }
                }
                foreach (BMSPackage item2 in pendingPackagesToEstimate)
                {
                    SearchEstimatedInstallationDirectory(item2);
                }
            }
        }
        return discoveredPackages;
    }

    private List<BMSPackage> searchBMSFilesRecursively(string dirfullpath, bool recursive = false)
    {
        return packageInstallService.SearchBmsFilesRecursively(dirfullpath, dupRateThreshInOnePkg, recursive);
    }

    private static bool IsSmartComponentOverwriteEnabled()
    {
        return Settings.Default.EnableSmartComponentOverwrite;
    }

    private static bool IsKeepSmartOverwriteProtectedFilesByRenamingEnabled()
    {
        return Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming;
    }

    private static bool IsSmartOverwriteProtectedExtension(string filePath)
    {
        return new BmsLibraryPackageInstallService().IsSmartOverwriteProtectedExtension(filePath);
    }

    private SmartOverwriteHashCompareResult CompareHashForSmartOverwrite(string srcFilePath, string dstFilePath)
    {
        string text = TryComputeFileMd5ForPath(srcFilePath, "smart_component_overwrite_hash");
        string text2 = TryComputeFileMd5ForPath(dstFilePath, "smart_component_overwrite_hash");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
        {
            return SmartOverwriteHashCompareResult.Unavailable;
        }
        if (text.Equals(text2, StringComparison.OrdinalIgnoreCase))
        {
            return SmartOverwriteHashCompareResult.Same;
        }
        return SmartOverwriteHashCompareResult.Different;
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
    /// <param name="deleteAllContents">移動元フォルダを中身ごと強制削除するか（マージ時はtrue）</param>
    /// <param name="existingHashes">既存BMSハッシュのスナップショット（重複スキップ用）</param>
    /// <param name="excludedComponentPaths">移動対象外のコンポーネントパス</param>
    /// <returns>移動成功時true</returns>
    private bool moveBMSPackageFiles(BMSPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false, HashSet<string> existingHashes = null, ISet<string> excludedComponentPaths = null)
    {
        string sourcePath = pkg.path;
        string destinationDirectory = string.Empty;
        string bMSInstallDir = Settings.Default.BMSInstallDir;
        bool isSingleFile = false;
        List<string> installComponentFiles = new List<string>();
        List<BMSFile> installBMSFiles = new List<BMSFile>();
        List<BMSFile> skippedBMSFiles = new List<BMSFile>();
        bool isAutoNaming = false;

        // --- ソースの種類判定: 単一ファイル or ディレクトリ ---
        if (File.Exists(sourcePath))
        {
            installComponentFiles = new List<string> { sourcePath };
            isSingleFile = true;
        }
        else
        {
            if (!Directory.Exists(sourcePath))
            {
                return false;
            }
            installComponentFiles = Directory.EnumerateFileSystemEntries(sourcePath).ToList();
            // 移動先が未指定の場合はフォルダ名を自動生成する
            isAutoNaming = string.IsNullOrWhiteSpace(installationDirectory);
        }

        // --- BMSファイルとコンポーネントファイルを分類 ---
        HashSet<string> installComponentPathSet = new HashSet<string>(installComponentFiles, StringComparer.OrdinalIgnoreCase);
        installBMSFiles = pkg.BMSFiles.Where((BMSFile f) => installComponentPathSet.Contains(f.path)).ToList();
        HashSet<string> installBMSPathSet = new HashSet<string>(installBMSFiles.Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
        // コンポーネントファイルからBMSファイルを除外（BMSは個別に移動する）
        installComponentFiles = installComponentFiles.Where((string p) => !installBMSPathSet.Contains(p)).ToList();
        if (excludedComponentPaths != null && excludedComponentPaths.Count > 0)
        {
            // 既所持譜面など「今回の導入対象外BMS」は、コンポーネント側の移動にも巻き込まない。
            installComponentFiles = installComponentFiles.Where((string p) => !excludedComponentPaths.Contains(p)).ToList();
        }

        // --- 既存ハッシュとの重複チェック: 同一ハッシュのBMSはスキップ ---
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            HashSet<string> hashSnapshot = existingHashes ?? CreateBMSHashSnapshotExcludingUnsafe(installBMSFiles);
            skippedBMSFiles = installBMSFiles.Where((BMSFile bmsFile) => IsBMSHashAvailable(bmsFile.hash) && hashSnapshot.Contains(bmsFile.hash)).ToList();
            if (skippedBMSFiles.Count != 0)
            {
                HashSet<string> skipPathSet = new HashSet<string>(skippedBMSFiles.Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
                installBMSFiles = installBMSFiles.Where((BMSFile f) => !skipPathSet.Contains(f.path)).ToList();
                pkg.BMSFiles.RemoveAll((BMSFile f) => skipPathSet.Contains(f.path));
            }
        }

        // --- ファイル移動の実行 ---
        try
        {
            // 移動先ディレクトリの決定
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                destinationDirectory = createBMSFolderPath(pkg.BMSFiles, bMSInstallDir, (from file in installComponentFiles
                                                                                         select Path.GetFileName(file) into f
                                                                                         orderby f.Length descending
                                                                                         select f).FirstOrDefault() ?? string.Empty);
            }
            else
            {
                destinationDirectory = installationDirectory;
            }

            // 自動命名の場合: 重複しないディレクトリ名を生成
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                int dirSuffix = 1;
                string baseDirName = destinationDirectory;
                while (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                {
                    dirSuffix++;
                    destinationDirectory = baseDirName + "(" + dirSuffix + ")";
                }
                if (!isAutoNaming)
                {
                    fileMutationService.EnsureDirectory(destinationDirectory, targetOnlyFileMutationOptions);
                }
            }

            // ディレクトリ名変更モード（自動命名 + ソースがディレクトリ）
            if (isAutoNaming)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, sourcePath));
                }
                fileMutationService.MoveDirectory(sourcePath, destinationDirectory, overwrite: true, recursiveDirectoryTreeFileMutationOptions);
            }
            else
            {
                // コンポーネントファイル（音源・画像等）の移動
                if (IsSmartComponentOverwriteEnabled())
                {
                    // スマートコンポーネント上書きモード: 同一ファイルのスキップ、古いファイルの上書き判定
                    ComponentMoveSummary componentMoveSummary = new ComponentMoveSummary();
                    ComponentMovePlanBuildResult movePlanResult = BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths);
                    List<ComponentMovePlanItem> movePlanItems = movePlanResult.PlanItems;
                    bool keepProtectedFilesByRenaming = IsKeepSmartOverwriteProtectedFilesByRenamingEnabled();
                    componentMoveSummary.Total = movePlanItems.Count;
                    componentMoveSummary.SkippedByExclusion = movePlanResult.SkippedByExclusion;

                    foreach (ComponentMovePlanItem planItem in movePlanItems)
                    {
                        string srcFilePath = planItem.SourcePath;
                        string dstFilePath = planItem.DestinationPath;
                        if (!File.Exists(srcFilePath))
                        {
                            throw new FileNotFoundException(Resources.Error_FileNotFound, srcFilePath);
                        }
                        if (IsSamePath(srcFilePath, dstFilePath))
                        {
                            // 同一パスは自己上書きになるため安全側でスキップする。
                            componentMoveSummary.SkippedSame++;
                            componentMoveSummary.SkippedSamePath++;
                            continue;
                        }
                        string dstDirPath = Path.GetDirectoryName(dstFilePath);
                        if (!string.IsNullOrWhiteSpace(dstDirPath))
                        {
                            fileMutationService.EnsureDirectory(dstDirPath, targetOnlyFileMutationOptions);
                        }

                        // 移動判定: Move / Overwrite / SkipSame / SkipOlderOrEqual
                        ComponentMoveDecision moveDecision = DecideComponentMove(srcFilePath, dstFilePath);
                        bool keepByRename = keepProtectedFilesByRenaming && File.Exists(dstFilePath) && IsSmartOverwriteProtectedExtension(srcFilePath) && moveDecision != ComponentMoveDecision.SkipSame;
                        if (keepByRename)
                        {
                            componentMoveSummary.HashChecked++;
                            SmartOverwriteHashCompareResult smartOverwriteHashCompareResult = CompareHashForSmartOverwrite(srcFilePath, dstFilePath);
                            if (smartOverwriteHashCompareResult == SmartOverwriteHashCompareResult.Same)
                            {
                                fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                                componentMoveSummary.SkippedSame++;
                                componentMoveSummary.DeletedAfterSkip++;
                                componentMoveSummary.HashSameSkip++;
                                continue;
                            }
                            string nonConflictingDestination = GetNonConflictingPathWithSuffix(dstFilePath);
                            fileMutationService.MoveFile(srcFilePath, nonConflictingDestination, overwrite: false, targetOnlyFileMutationOptions);
                            componentMoveSummary.Moved++;
                            componentMoveSummary.RenamedKeep++;
                            if (smartOverwriteHashCompareResult == SmartOverwriteHashCompareResult.Different)
                            {
                                componentMoveSummary.HashDiffRenamed++;
                            }
                            else
                            {
                                componentMoveSummary.HashUnavailableRenamed++;
                            }
                            if (moveDecision == ComponentMoveDecision.Overwrite)
                            {
                                componentMoveSummary.RenamedFromOverwrite++;
                            }
                            else
                            {
                                componentMoveSummary.RenamedFromSkipOlder++;
                            }
                            continue;
                        }
                        switch (moveDecision)
                        {
                            case ComponentMoveDecision.Move:
                                fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                                componentMoveSummary.Moved++;
                                break;
                            case ComponentMoveDecision.Overwrite:
                                fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                                componentMoveSummary.Moved++;
                                componentMoveSummary.Overwritten++;
                                break;
                            case ComponentMoveDecision.SkipSame:
                                fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                                componentMoveSummary.SkippedSame++;
                                componentMoveSummary.DeletedAfterSkip++;
                                break;
                            default:
                                fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                                componentMoveSummary.SkippedOlder++;
                                componentMoveSummary.DeletedAfterSkip++;
                                break;
                        }
                    }
                    // スキップ後の空ディレクトリを掃除して、後段のフォルダ削除を成功しやすくする。
                    CleanupEmptyComponentDirectories(installComponentFiles);
                    int movedNewCount = Math.Max(0, componentMoveSummary.Moved - componentMoveSummary.Overwritten);
                    LogInstallPerformance("component_move_summary package=" + pkg.path + " total=" + componentMoveSummary.Total + " moved=" + componentMoveSummary.Moved + " moved_new=" + movedNewCount + " overwritten=" + componentMoveSummary.Overwritten + " skipped_same=" + componentMoveSummary.SkippedSame + " skipped_same_path=" + componentMoveSummary.SkippedSamePath + " skipped_older=" + componentMoveSummary.SkippedOlder + " skipped_by_exclusion=" + componentMoveSummary.SkippedByExclusion + " deleted_after_skip=" + componentMoveSummary.DeletedAfterSkip + " renamed_keep=" + componentMoveSummary.RenamedKeep + " renamed_from_overwrite=" + componentMoveSummary.RenamedFromOverwrite + " renamed_from_skip_older=" + componentMoveSummary.RenamedFromSkipOlder + " hash_checked=" + componentMoveSummary.HashChecked + " hash_same_skip=" + componentMoveSummary.HashSameSkip + " hash_diff_renamed=" + componentMoveSummary.HashDiffRenamed + " hash_unavailable_renamed=" + componentMoveSummary.HashUnavailableRenamed + " failed=" + componentMoveSummary.Failed);
                }
                else
                {
                    // 通常モード: 全コンポーネントを並列に移動
                    installComponentFiles.AsParallel().ForAll(delegate (string file)
                    {
                        string componentDstPath = Path.Combine(destinationDirectory, Path.GetFileName(file));
                        if (File.Exists(file))
                        {
                            fileMutationService.MoveFile(file, componentDstPath, overwrite: true, targetOnlyFileMutationOptions);
                        }
                        else
                        {
                            if (!Directory.Exists(file))
                            {
                                throw new FileNotFoundException(Resources.Error_FileNotFound, file);
                            }
                            fileMutationService.MoveDirectory(file, componentDstPath, overwrite: true, recursiveDirectoryTreeFileMutationOptions);
                        }
                    });
                }

                // BMSファイルの個別移動（ファイル名衝突を回避しつつ移動）
                foreach (BMSFile bmsFile in installBMSFiles)
                {
                    string destinationBmsPath = Path.Combine(destinationDirectory, Path.GetFileName(bmsFile.path));
                    // 移動先に同名ファイル/ディレクトリが存在する場合はサフィックスを追加
                    while (File.Exists(destinationBmsPath) || Directory.Exists(destinationBmsPath))
                    {
                        string renamedFileName = Path.GetFileNameWithoutExtension(destinationBmsPath) + "_" + Path.GetExtension(destinationBmsPath);
                        destinationBmsPath = Path.Combine(destinationDirectory, Path.GetFileName(renamedFileName));
                    }
                    if (!File.Exists(bmsFile.path))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, bmsFile.path);
                    }
                    fileMutationService.MoveFile(bmsFile.path, destinationBmsPath, overwrite: true, targetOnlyFileMutationOptions);
                    bmsFile.path = bmsFile.path.ReplaceFromEnd(Path.GetFileName(bmsFile.path), Path.GetFileName(destinationBmsPath), isIgnoreCase: true);
                }
            }
        }
        catch (Exception installException)
        {
            if (!showMessageBoxOnInstallFail)
            {
                return false;
            }
            dialogService.Show(string.Format(Resources.Error_InstallFailed, pkg.path, destinationDirectory, GetDisplayedExceptionMessage(installException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }

        // --- パッケージ内のBMSFileパスを移動先に更新 ---
        if (isSingleFile)
        {
            pkg.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = Path.Combine(destinationDirectory, Path.GetFileName(bmsInfo.path));
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            pkg.path = Path.Combine(destinationDirectory, Path.GetFileName(pkg.path));
        }
        else
        {
            if (!(Directory.Exists(sourcePath) || isAutoNaming))
            {
                return false;
            }
            pkg.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = bmsInfo.path.ReplaceFromStart(sourcePath + Path.DirectorySeparatorChar, destinationDirectory + Path.DirectorySeparatorChar, isIgnoreCase: true);
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            pkg.path = destinationDirectory;
        }

        // --- 移動元フォルダの削除 ---
        string dirToBeDeleted = string.Empty;
        if (pkg.delete_parent)
        {
            // アーカイブ展開元の親ディレクトリを削除
            dirToBeDeleted = Path.GetDirectoryName(sourcePath);
        }
        else if (Directory.Exists(sourcePath))
        {
            // ソースディレクトリ自体を削除
            dirToBeDeleted = sourcePath;
        }
        if (!string.IsNullOrWhiteSpace(dirToBeDeleted) && Directory.Exists(dirToBeDeleted))
        {
            if (!deleteAllContents && HasRemainingDirectoryEntries(dirToBeDeleted))
            {
                // NOTE:
                // 保留からの通常インストールでは、既所持譜面を残した結果として移動元フォルダが非空のままになる。
                // 旧実装はこの状態を「削除不要」として成功扱いにしていたため、その互換挙動を維持する。
                NLogWrapper.FileLogger?.Info("Folder deletion skipped: path=" + dirToBeDeleted + " reason=not_empty deleteAllContents=False");
                return true;
            }

            FileMutationOptions deleteOptions = deleteAllContents ? recursiveDirectoryTreeFileMutationOptions : targetOnlyFileMutationOptions;
            try
            {
                fileMutationService.DeleteDirectoryDirect(dirToBeDeleted, deleteAllContents, deleteOptions);
                NLogWrapper.FileLogger?.Info("Folder deletion success: path=" + dirToBeDeleted + " deleteAllContents=" + deleteAllContents);
            }
            catch (FileMutationException fileMutationException)
            {
                if (!deleteAllContents && HasRemainingDirectoryEntries(dirToBeDeleted))
                {
                    // NOTE:
                    // 事前チェック後に別ファイルが残る競合でも、旧実装は非空なら失敗扱いにしなかった。
                    // 同じく通常インストール時だけはダイアログを出さずにスキップ成功へ寄せる。
                    NLogWrapper.FileLogger?.Info("Folder deletion skipped after failure: path=" + dirToBeDeleted + " reason=not_empty_after_failure deleteAllContents=False attempts=" + fileMutationException.AttemptCount);
                    return true;
                }

                NLogWrapper.FileLogger?.Info(string.Format(
                    "Folder deletion failed: path={0} attempts={1} normalizedReadOnly={2} win32={3} errorType={4} error={5}",
                    dirToBeDeleted,
                    fileMutationException.AttemptCount,
                    fileMutationException.NormalizedReadOnlyCount,
                    fileMutationException.Win32ErrorCode,
                    fileMutationException.RootCause.GetType().FullName,
                    GetDisplayedExceptionMessage(fileMutationException)));
                DispatcherMessageBox.Show(string.Format(Resources.Error_FolderDeleteFailed, dirToBeDeleted, GetDisplayedExceptionMessage(fileMutationException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
        }
        return true;
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
                HashSet<string> addedPathSet = new HashSet<string>(addedFiles.Select((BMSFile ff) => ff.path), StringComparer.OrdinalIgnoreCase);
                BMSFiles = BMSFiles.Where((BMSFile f) => !addedPathSet.Contains(f.path)).Concat(addedFiles).ToList();
                addedFiles.Select((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path)).Distinct().AsParallel()
                    .ForAll(delegate (string dir)
                    {
                        bmsFolderAllFileList.AddDir(dir);
                    });
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
                    HashSet<string> installedHashes = CreateBMSHashSnapshotExcludingUnsafe(null);
                    InstallEstimationResult result = CreateInstallEstimationService().EstimateInstallationDirectory(targetBmsFiles, installedHashes, bmsFolderAllFileList, asParallel, estimateMode);
                    if (!string.IsNullOrWhiteSpace(result.DestinationDirectory))
                    {
                        targetBmsFiles.ForEach(delegate (BMSFile bmsFile)
                        {
                            bmsFile.instl_dst = result.DestinationDirectory;
                        });
                    }
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

    private Dictionary<string, List<string>> BuildInstalledHashToDirectoryMap()
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
        Dictionary<string, List<string>> installedDirectoryIndex = BuildInstalledHashToDirectoryMap();
        if (installedDirectoryIndex.Count == 0)
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
    public void SearchEstimatedInstallationDirectory(BMSPackage package)
    {
        if (BMSPackagesPending.Contains(package))
        {
            List<BMSFile> list = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile x) => x != null).ToList();
            if (list.Count == 0)
            {
                return;
            }
            IEnumerable<BMSFile> installedFiles = BMSFiles ?? new List<BMSFile>();
            HashSet<string> hashSet = new HashSet<string>(installedFiles.Where((BMSFile x) => x != null).Select((BMSFile x) => x.hash), StringComparer.OrdinalIgnoreCase);
            List<BMSFile> list2 = list.Where((BMSFile f) => IsBMSHashAvailable(f.hash) && hashSet.Contains(f.hash)).ToList();
            List<BMSFile> list3 = list.Where((BMSFile f) => !IsBMSHashAvailable(f.hash) || !hashSet.Contains(f.hash)).ToList();
            ApplyPackageMixedInstallWarnings(list2);
            if (list3.Count == 0)
            {
                return;
            }
            // 部分既所持パッケージでは、既存譜面の実配置先を優先利用して未所持譜面の導入先を補完する。
            if (list2.Count > 0)
            {
                if (TryResolveInstalledDestinationFromPackage(package, list3, out var resolvedDir))
                {
                    foreach (BMSFile item in list3)
                    {
                        item.instl_dst = resolvedDir;
                    }
                    return;
                }
                // 解決できない場合だけ従来推定へフォールバックし、空欄のまま残るケースを減らす。
                LogInstallPerformance("mixed_package_resolve fallback reason=use_legacy_search missing=" + list3.Count);
                searchEstimatedInstallationDirectory(list3, asParallel: true, fixMode: true);
                return;
            }
            if (list3.Count > 0)
            {
                searchEstimatedInstallationDirectory(list3);
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(BMSFile bmsFile, bool asParallel = true, bool fixMode = false)
    {
        searchEstimatedInstallationDirectory(new BMSFile[1] { bmsFile }, asParallel, fixMode);
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
        searchEstimatedInstallationDirectory(list, asParallel: true, BmsInstallationEstimateMode.MergeNoSourceCompensation);
        int num = list.Count((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved package=" + package.path + " targets=" + list.Count);
            return;
        }
        LogInstallPerformance("estimated_merge_done package=" + package.path + " resolved=" + num + " targets=" + list.Count + " dst=" + list.Select((BMSFile f) => f.instl_dst).Where((string d) => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault());
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
        foreach (BMSFile item in list)
        {
            item.instl_dst = null;
            searchEstimatedInstallationDirectory(new BMSFile[1] { item }, asParallel: true, BmsInstallationEstimateMode.MergeNoSourceCompensation);
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
                                return MessageBox.Show(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
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
                            ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: result.PendingPackagesToRemove));
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
        HashSet<string> hashSet = new HashSet<string>((originalPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile f) => f != null && IsBMSHashAvailable(f.hash)).Select((BMSFile f) => f.hash), StringComparer.OrdinalIgnoreCase);
        if (hashSet.Count == 0)
        {
            return null;
        }
        List<BMSFile> list = BMSFiles.Where(delegate (BMSFile f)
        {
            if (f == null || string.IsNullOrWhiteSpace(f.path) || !IsBMSHashAvailable(f.hash) || !hashSet.Contains(f.hash))
            {
                return false;
            }
            return string.Equals(DirectoryExt.GetDirectoryNameSimple(f.path), destinationDirectory, StringComparison.OrdinalIgnoreCase);
        }).ToList();
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
                        bool deletePendingPackageSourceAfterInstall = Settings.Default.DeletePendingPackageSourceAfterInstall;
                        PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(packages, BMSPackagesPending, BMSFiles, deletePendingPackageSourceAfterInstall, CountComponentMoveTargetsForPackage);
                        if (installPlan.SelectedPendingPackages.Count == 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
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
                            DispatcherMessageBox.Show(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, batchResult.CleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        totalStopwatch.Stop();
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir end pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingCountBeforeApply + " pendingRemovedTotal=" + pendingRemovedTotal + " pendingAfterApply=" + pendingCountAfterApply + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedCountBeforeApply + " installedAddedTotal=" + installedAddedTotal + " installedAfterApply=" + installedCountAfterApply + " maintenanceTargets=" + batchResult.DeferredMaintenanceTargets.Count + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
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
        if (delta == null || !delta.HasChanges)
        {
            return;
        }
        BMSPackagesPending = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(delta.RemainingPackages ?? new List<BMSPackage>()), DispatcherHelper.UIDispatcher);
        List<string> list = (delta.InstallPathsToDelete ?? new List<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return;
        }
        dbGateway.DeleteInstallRows(list);
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
                    ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(clearAll: true));
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
                List<BMSPackage> list = packageInstallService.GetPendingPackagesContainingOnlyInstalledCharts(BMSPackagesPending, ContainsBMSHashUnsafe);
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

    private static List<string> GetDistinctInstalledDirectoriesByHash(Dictionary<string, List<string>> installedDirectoryIndexSnapshot, string hash)
    {
        return BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, hash);
    }

    private static BMSFile FindChartWithMissingInstalledDirectory(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(package, installedDirectoryIndexSnapshot);
    }

    private static BMSFile FindChartWithMultipleInstalledDirectories(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(package, installedDirectoryIndexSnapshot);
    }

    private static int CountDistinctInstalledDirectoriesForPackage(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(package, installedDirectoryIndexSnapshot);
    }

    private bool TryPrepareInstalledOnlyPackageDestination(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot, out string destinationDir, out PrepareSkipReason reason)
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
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        bool deletePendingPackageSourceAfterInstall = Settings.Default.DeletePendingPackageSourceAfterInstall;
                        Dictionary<string, List<string>> installedDirectoryIndexSnapshot = CreateInstalledDirectoryIndexSnapshotUnsafe();
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite scan pendingTotal=" + BMSPackagesPending.Count + " eligible=" + packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count);
                        NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.Count);
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
                                    InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories => "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot)?.path ?? "(null)") + " hash=" + (FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot)?.hash ?? "(null)") + " dirCount=" + ((FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot) == null) ? 0 : GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot).hash).Count),
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
                                    DispatcherMessageBox.Show(string.Format(Resources.Error_InstallFailed, pendingPackage.path, destinationDir, displayedExceptionMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                            DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                            DispatcherMessageBox.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
        ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packages));
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
                PendingInstallDestinationSelectionResult selection = CreateInstallEstimationService().ValidatePendingInstallDestination(bmsFile, BMSPackagesPending, bmsFolderAllFileList.Keys, destinationDirectory);
                if (!selection.Success)
                {
                    DispatcherMessageBox.Show(selection.WarningMessage, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                foreach (BMSFile item in selection.TargetFiles)
                {
                    item.instl_dst = selection.ValidatedDestinationDirectory;
                }
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
        Dictionary<string, BMSTable[]> md5ToTablesMap = playlistReferenceService.BuildMd5ToTablesMap(table, sourceEntries);
        stopwatchBuildMap.Stop();
        int matchedSongFiles = 0;
        int matchedPendingFiles = 0;
        int addedSongRefs = 0;
        int addedPendingRefs = 0;
        PlaylistReferenceApplyStats songApplyStats = default(PlaylistReferenceApplyStats);
        Stopwatch stopwatchApplySong = Stopwatch.StartNew();
        List<BMSFile> songFilesSnapshot = null;
        if (md5ToTablesMap.Count > 0)
        {
            songFilesSnapshot = SnapshotSongFilesForPlaylistReferenceApply();
        }
        if (md5ToTablesMap.Count > 0 && songFilesSnapshot != null && songFilesSnapshot.Count > 0)
        {
            addedSongRefs = playlistReferenceService.ApplyReferenceMap(songFilesSnapshot, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
        }
        stopwatchApplySong.Stop();
        PlaylistReferenceApplyStats pendingApplyStats = default(PlaylistReferenceApplyStats);
        Stopwatch stopwatchApplyPending = Stopwatch.StartNew();
        List<BMSFile> pendingFilesSnapshot = null;
        if (md5ToTablesMap.Count > 0)
        {
            pendingFilesSnapshot = SnapshotPendingFilesForPlaylistReferenceApply();
        }
        if (md5ToTablesMap.Count > 0 && pendingFilesSnapshot != null && pendingFilesSnapshot.Count > 0)
        {
            addedPendingRefs = playlistReferenceService.ApplyReferenceMap(pendingFilesSnapshot, md5ToTablesMap, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
        }
        stopwatchApplyPending.Stop();
        int tableCount = 1;
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + stopwatchApplySong.ElapsedMilliseconds + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + stopwatchApplyPending.ElapsedMilliseconds + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + md5ToTablesMap.Count + " tableCount=" + tableCount + " matchedSongFiles=" + matchedSongFiles + " addSongCalls=" + addedSongRefs + " matchedPendingFiles=" + matchedPendingFiles + " addPendingCalls=" + addedPendingRefs + " suppressNotify=" + suppressFilePropertyChanged);
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
        Dictionary<string, BMSTable[]> md5ToTablesMap = playlistReferenceService.BuildMd5ToTablesMap(list);
        stopwatchBuildMap.Stop();
        long applySongMs = 0L;
        long applyPendingMs = 0L;
        int matchedSongFiles = 0;
        int matchedPendingFiles = 0;
        int addedSongRefs = 0;
        int addedPendingRefs = 0;
        PlaylistReferenceApplyStats songApplyStats = default(PlaylistReferenceApplyStats);
        PlaylistReferenceApplyStats pendingApplyStats = default(PlaylistReferenceApplyStats);
        if (md5ToTablesMap.Count > 0)
        {
            if (files == null)
            {
                Stopwatch stopwatchApplySong = Stopwatch.StartNew();
                List<BMSFile> songFilesSnapshot = SnapshotSongFilesForPlaylistReferenceApply();
                if (songFilesSnapshot != null && songFilesSnapshot.Count > 0)
                {
                    addedSongRefs = playlistReferenceService.ApplyReferenceMap(songFilesSnapshot, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
                }
                stopwatchApplySong.Stop();
                applySongMs = stopwatchApplySong.ElapsedMilliseconds;
                Stopwatch stopwatchApplyPending = Stopwatch.StartNew();
                List<BMSFile> pendingFilesSnapshot = SnapshotPendingFilesForPlaylistReferenceApply();
                if (pendingFilesSnapshot != null && pendingFilesSnapshot.Count > 0)
                {
                    addedPendingRefs = playlistReferenceService.ApplyReferenceMap(pendingFilesSnapshot, md5ToTablesMap, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
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
                        addedSongRefs = playlistReferenceService.ApplyReferenceMap(files, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
                    }
                }
                stopwatchApplySong.Stop();
                applySongMs = stopwatchApplySong.ElapsedMilliseconds;
            }
        }
        LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + applySongMs + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + applyPendingMs + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + md5ToTablesMap.Count + " tableCount=" + list.Count + " matchedSongFiles=" + matchedSongFiles + " addSongCalls=" + addedSongRefs + " matchedPendingFiles=" + matchedPendingFiles + " addPendingCalls=" + addedPendingRefs + " suppressNotify=" + suppressFilePropertyChanged);
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

    /// <summary>
    /// 指定されたプレイリストの参照を BMS ファイル群から削除します。
    /// </summary>
    public void RemoveReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (entries == null)
        {
            RemoveReferenceBMSTables(new BMSTable[1] { table });
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
        Action<IEnumerable<BMSFile>> action = delegate (IEnumerable<BMSFile> l)
        {
            tables.AsParallel().ForAll(delegate (BMSTable table)
            {
                removeReferenceBMSTables(table, table.entries, l);
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
        int num = 250;
        int num2 = 128;
        Encoding encoding = Encoding.GetEncoding("Shift_JIS");
        string lCSBMSInfo = GetLCSBMSInfo(bmsFiles.Select((BMSFile f) => f.Title));
        string lCSBMSInfo2 = GetLCSBMSInfo(bmsFiles.Select((BMSFile f) => f.Artist));
        string s = Settings.Default.FolderNameFormat.Replace("%ARTIST%", lCSBMSInfo2).Replace("%TITLE%", lCSBMSInfo).Trim();
        if (Settings.Default.UseOnlyShiftJISChars)
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
                    if (!Directory.Exists(src) || !Directory.Exists(dst) || src.Equals(dst, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    List<BMSFile> bmsFiles = BMSFiles.Where((BMSFile f) => f.path.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
                    HashSet<string> snapshot = CreateBMSHashSnapshotExcludingUnsafe(bmsFiles);
                    unregisterBMSFiles(bmsFiles);
                    foreach (string item in bmsFolderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        bmsFolderAllFileList.RemoveDir(item);
                    }
                    BMSPackagesInstalled.Where((BMSPackage pkg) => (pkg.path + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
                    BMSPackage repackage = new BMSPackage(bmsFiles)
                    {
                        path = src,
                        delete_parent = false
                    };
                    if (!moveBMSPackageFiles(repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true, existingHashes: snapshot))
                    {
                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFolderMergeFailed, src, dst), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    bmsFolderAllFileList.AddDir(dst, update: true);
                    foreach (BMSFile item2 in BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat(BMSFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst))))
                    {
                        if (!string.IsNullOrWhiteSpace(item2.instl_dst) && (item2.instl_dst + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            item2.instl_dst = item2.instl_dst.ReplaceFromStart(src, dst, isIgnoreCase: true);
                        }
                    }
                    using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                    {
                        lR2SongDBExtended.BeginTransaction();
                        foreach (BMSFile bMSFile in repackage.BMSFiles)
                        {
                            lR2SongDBExtended.InsertOrReplace(bMSFile, typeof(LR2SongDB.song));
                        }
                        lR2SongDBExtended.Commit();
                    }
                    List<BMSFile> bmsFiles2 = repackage.BMSFiles.Concat(BMSFiles.Where((BMSFile f) => f.path.StartsWith(dst + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
                    setMaintenanceInfo(bmsFiles2, forceUpdate: true);
                    HashSet<string> repackagePathSet = new HashSet<string>(repackage.BMSFiles.Select((BMSFile ff) => ff.path), StringComparer.OrdinalIgnoreCase);
                    BMSFiles = BMSFiles.Where((BMSFile f) => !repackagePathSet.Contains(f.path)).Concat(repackage.BMSFiles).ToList();
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
                List<BMSFile> movedFiles = new List<BMSFile>();
                int i;
                for (i = 0; i < files.Count; i++)
                {
                    BMSPackage bMSPackage = new BMSPackage(files[i])
                    {
                        delete_parent = false
                    };
                    string path = files[i].path;
                    if (!moveBMSPackageFiles(bMSPackage, bMSPackage.BMSFiles[0].instl_dst, showMessageBoxOnInstallFail: true, deleteAllContents: false, existingHashes: existingHashes))
                    {
                        continue;
                    }
                    if (bMSPackage.BMSFiles.Count == 0)
                    {
                        if (DispatcherMessageBox.Show(string.Format(Resources.Confirm_DuplicateReinstallSkipped, files[i].path, string.Join(Environment.NewLine, from x in BMSFiles.Where((BMSFile f) => f.hash == files[i].hash).Except(new BMSFile[1] { files[i] })
                                                                                                                                                                 select x.path)), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                        {
                            RemoveBMSFiles(new BMSFile[1] { files[i] });
                        }
                        continue;
                    }
                    files[i].instl_dst = null;
                    replaceBMSFilePath(files[i], files[i].path, path);
                    movedFiles.Add(files[i]);
                }
                if (movedFiles.Count > 0)
                {
                    setMaintenanceInfo(movedFiles, forceUpdate: true);
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
                    List<string> source = (from d in bmsFiles.Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                           orderby d.Length
                                           select d).ToList();
                    List<string> list = new List<string>();
                    List<string> rootFolders = getBMSDirectories();
                    foreach (string p in source.Where((string f) => renameRootFolder || !rootFolders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    {
                        if (!list.Any((string pp) => p.StartsWith(pp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(p);
                        }
                    }
                    if (list.Any((string f) => Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        DispatcherMessageBox.Show(Resources.Warn_DriveRootBmsSkipped, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    foreach (string folder in list)
                    {
                        try
                        {
                            if (!Directory.Exists(folder) || Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            List<BMSFile> bmsFiles2 = (from f in BMSFiles.AsParallel()
                                                       where f.path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && DirectoryExt.GetDirectoryNameSimple(f.path).Equals(folder, StringComparison.OrdinalIgnoreCase)
                                                       select f).ToList();
                            string text = createBMSFolderPath(bmsFiles2, DirectoryExt.GetDirectoryNameSimple(folder), (from f in FastDirectoryEnumerator.GetFileNames(folder)
                                                                                                                       orderby f.Length descending
                                                                                                                       select f).FirstOrDefault() ?? string.Empty);
                            if (!folder.Equals(text, StringComparison.OrdinalIgnoreCase))
                            {
                                int num = 1;
                                string path = text;
                                while (File.Exists(path) || Directory.Exists(path))
                                {
                                    num++;
                                    path = text + " (" + num + ")";
                                }
                                RenameBMSFolder(folder, Path.GetFileName(path), false, renameRootFolder: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_RenameFailed, folder, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                }
            }
        }
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
        if (!renameRootFolder && getBMSDirectories().Contains(srcDir, StringComparer.OrdinalIgnoreCase))
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_CannotRenameRootFolder, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (Settings.Default.UseOnlyShiftJISChars)
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
            DispatcherMessageBox.Show(string.Format(Resources.Warn_RenameFolderNotExists, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
                    moveBMSFolder(srcDir, dstDir, unregister);
                    if (unregister == false)
                    {
                        BMSFilesDuplicated = null;
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
                        DispatcherMessageBox.Show(string.Format(Resources.Error_MoveDestRootNotFound, dstDir), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        return;
                    }
                    List<string> list = (from d in bmsFiles.Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                         orderby d.Length
                                         select d).ToList();
                    List<string> list2 = new List<string>();
                    foreach (string p in list)
                    {
                        if (!list2.Any((string pp) => p.StartsWith(pp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        {
                            list2.Add(p);
                        }
                    }
                    if (list2.Any((string f) => Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        DispatcherMessageBox.Show(Resources.Warn_DriveRootCannotChangeRoot, Resources.MessageBoxTitle_Confirm, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    foreach (string item in from f in list2
                                            where !Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase)
                                            where Directory.Exists(f)
                                            select f)
                    {
                        string dstDir2 = Path.Combine(dstDir, Path.GetFileName(item));
                        moveBMSFolder(item, dstDir2, unregister);
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
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (File.Exists(dstDir) || Directory.Exists(dstDir))
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        try
        {
            libraryFileOperationsService.MoveFolderAndUpdateReferences(srcDir, dstDir, BMSFiles, BMSPackagesPending, BMSPackagesInstalled, bmsFolderAllFileList, fileMutationService, recursiveDirectoryTreeFileMutationOptions);
        }
        catch (Exception moveException)
        {
            DispatcherMessageBox.Show(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        List<BMSFile> list = BMSFiles.Where((BMSFile f) => f.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unregister == true)
        {
            unregisterBMSFiles(list);
        }
        else
        {
            if (unregister != false)
            {
                return;
            }
            foreach (IGrouping<string, BMSFile> item4 in from target in list
                                                         group target by Path.GetDirectoryName(target.path))
            {
                string newFolderPath = item4.Key.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
                bool calcFolderParent = replaceBMSFolder(newFolderPath, item4.Key);
                foreach (BMSFile item5 in item4)
                {
                    string newPath = item5.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
                    replaceBMSFilePath(item5, newPath, null, calcFolderParent);
                }
            }
        }
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
                    DispatcherMessageBox.Show(string.Format(Resources.Warn_RenameDestAlreadyExists, bmsFile.path, dstPath), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return;
                }
                try
                {
                    libraryFileOperationsService.MoveFileOnDisk(bmsFile, dstPath, fileMutationService, targetOnlyFileMutationOptions);
                }
                catch (Exception moveException)
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileMoveFailed, bmsFile.path, dstPath, GetDisplayedExceptionMessage(moveException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    return;
                }
                if (unregister == true)
                {
                    unregisterBMSFiles(new List<BMSFile> { bmsFile });
                }
                else if (unregister == false)
                {
                    replaceBMSFilePath(bmsFile, dstPath);
                    RaisePropertyChanged(() => BMSFiles);
                }
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
                List<BMSFile> list = bmsFiles.ToList();
                List<BMSFile> list2 = list.Where((BMSFile f) => f != null && File.Exists(f.path)).ToList();
                List<BMSFile> list3 = new List<BMSFile>();
                List<BMSFile> list4 = new List<BMSFile>();
                bool flag = false;
                int num = 0;
                int num2 = 0;
                int num3 = 0;
                foreach (BMSFile item in list2)
                {
                    string text = Path.Combine(Path.GetDirectoryName(item.path), Path.GetFileNameWithoutExtension(item.path) + newExt);
                    RenameInvalidExtensionOutcome renameInvalidExtensionOutcome = ProcessInvalidExtensionRename(item, text, unregister == true);
                    switch (renameInvalidExtensionOutcome.Action)
                    {
                        case RenameInvalidExtensionAction.Renamed:
                            num++;
                            if (unregister == false)
                            {
                                replaceBMSFilePath(item, renameInvalidExtensionOutcome.FinalPath);
                                flag = true;
                            }
                            else
                            {
                                list3.Add(item);
                            }
                            break;
                        case RenameInvalidExtensionAction.DeletedAsDuplicate:
                            num2++;
                            if (unregister == false)
                            {
                                list4.Add(item);
                            }
                            else
                            {
                                list3.Add(item);
                            }
                            break;
                        default:
                            num3++;
                            if (renameInvalidExtensionOutcome.FailureException != null)
                            {
                                if (renameInvalidExtensionOutcome.FailedDuringDelete)
                                {
                                    DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, item.path, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                }
                                else
                                {
                                    DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileMoveFailed, item.path, renameInvalidExtensionOutcome.FinalPath, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                }
                            }
                            break;
                    }
                }
                if (unregister == true)
                {
                    unregisterBMSFiles(list3);
                }
                else
                {
                    if (list4.Count > 0)
                    {
                        unregisterBMSFiles(list4);
                    }
                    if (flag)
                    {
                        RaisePropertyChanged(() => BMSFiles);
                    }
                }
                NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=normal total=" + list2.Count + " renamed=" + num + " deleted=" + num2 + " skipped=" + num3);
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
                    List<BMSFile> list = bmsFiles.Where((BMSFile f) => f != null && File.Exists(f.path)).ToList();
                    List<BMSFile> list2 = new List<BMSFile>();
                    int num = 0;
                    int num2 = 0;
                    int num3 = 0;
                    foreach (BMSFile item in list)
                    {
                        string text = Path.Combine(Path.GetDirectoryName(item.path), Path.GetFileNameWithoutExtension(item.path) + newExt);
                        RenameInvalidExtensionOutcome renameInvalidExtensionOutcome = ProcessInvalidExtensionRename(item, text, removeFromLibraryOnSuccess: false);
                        switch (renameInvalidExtensionOutcome.Action)
                        {
                            case RenameInvalidExtensionAction.Renamed:
                                num++;
                                list2.Add(item);
                                break;
                            case RenameInvalidExtensionAction.DeletedAsDuplicate:
                                num2++;
                                list2.Add(item);
                                break;
                            default:
                                num3++;
                                if (renameInvalidExtensionOutcome.FailureException != null)
                                {
                                    if (renameInvalidExtensionOutcome.FailedDuringDelete)
                                    {
                                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, item.path, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    }
                                    else
                                    {
                                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileMoveFailed, item.path, renameInvalidExtensionOutcome.FinalPath, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    }
                                }
                                break;
                        }
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(list2);
                    NLogWrapper.FileLogger?.Info("invalid_ext_rename summary scope=pending total=" + list.Count + " renamed=" + num + " deleted=" + num2 + " skipped=" + num3);
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
                        sendToRecycleBin,
                        (folderPath) => DispatcherMessageBox.Show(string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        recursiveDirectoryTreeFileMutationOptions);
                    foreach (LibraryDeleteFailure failure in result.Failures)
                    {
                        if (failure.IsDirectory)
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                        else
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    unregisterBMSFiles(result.RemovedFiles);
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
                    List<BMSFile> selectedFiles = bmsFiles.Where((BMSFile bmsInfo) => bmsInfo != null).ToList();
                    if (selectedFiles.Count == 0)
                    {
                        return;
                    }
                    List<BMSFile> removedFiles = new List<BMSFile>();
                    HashSet<string> selectedPaths = new HashSet<string>(selectedFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.path)).Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
                    HashSet<BMSFile> selectedFileRefs = new HashSet<BMSFile>(selectedFiles);
                    HashSet<string> handledByFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> blockedByFailedFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
                    if (deleteContainingPackageFoldersWhenNoBms)
                    {
                        foreach (BMSPackage pendingPackage in GetPendingPackagesFullyCoveredBySelection(selectedPaths, selectedFileRefs))
                        {
                            if (!Directory.Exists(pendingPackage.path))
                            {
                                continue;
                            }
                            List<BMSFile> packageFiles = pendingPackage.BMSFiles.Where((BMSFile f) => f != null).ToList();
                            try
                            {
                                fileMutationService.DeleteDirectoryShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                                foreach (BMSFile packageFile in packageFiles)
                                {
                                    removedFiles.Add(packageFile);
                                    if (!string.IsNullOrWhiteSpace(packageFile.path))
                                    {
                                        handledByFolderDeletePaths.Add(packageFile.path);
                                    }
                                }
                            }
                            catch (Exception deleteException)
                            {
                                foreach (BMSFile packageFile in packageFiles)
                                {
                                    if (!string.IsNullOrWhiteSpace(packageFile.path))
                                    {
                                        blockedByFailedFolderDeletePaths.Add(packageFile.path);
                                    }
                                }
                                DispatcherMessageBox.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, pendingPackage.path, GetDisplayedExceptionMessage(deleteException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                    }
                    foreach (BMSFile pendingBmsFile in selectedFiles)
                    {
                        if (!string.IsNullOrWhiteSpace(pendingBmsFile.path) && (handledByFolderDeletePaths.Contains(pendingBmsFile.path) || blockedByFailedFolderDeletePaths.Contains(pendingBmsFile.path)))
                        {
                            continue;
                        }
                        try
                        {
                            if (File.Exists(pendingBmsFile.path))
                            {
                                fileMutationService.DeleteFileShell(pendingBmsFile.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                                removedFiles.Add(pendingBmsFile);
                            }
                        }
                        catch (Exception deleteException)
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, pendingBmsFile.path, GetDisplayedExceptionMessage(deleteException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                        }
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(removedFiles);
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
        ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(filesToRemove: list));
    }

    /// <summary>
    /// BMS ファイル群を song.db から登録解除（レコード削除）します。
    /// </summary>
    private void unregisterBMSFiles(List<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        List<BMSFile> list = bmsFiles.Where((BMSFile f) => f != null).ToList();
        if (list.Count == 0)
        {
            return;
        }
        BMSFiles = BMSFiles.Except(list).ToList();
        using (rwlockSongDBMaintenance.GetWriterGuard())
        {
            dbGateway.DeleteSongsAndMaintenance(list);
        }
        HashSet<string> removedPaths = new HashSet<string>(list.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.path)).Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> removedFiles = new HashSet<BMSFile>(list);
        bool changed = false;
        List<BMSPackage> list2 = new List<BMSPackage>();
        foreach (BMSPackage item3 in BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null).ToList())
        {
            int count = item3.BMSFiles.Count;
            item3.BMSFiles.RemoveAll((BMSFile f) => IsMatchedRemovedFile(f, removedPaths, removedFiles));
            if (item3.BMSFiles.Count != count)
            {
                changed = true;
                if (item3.BMSFiles.Count == 0)
                {
                    list2.Add(item3);
                }
            }
        }
        if (list2.Count > 0)
        {
            BMSPackagesInstalled.Remove(list2);
        }
        if (changed)
        {
            RaisePropertyChanged(() => BMSPackagesInstalled);
        }
    }

    private bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        return libraryFileOperationsService.IsMatchedRemovedFile(file, removedPaths, removedFiles);
    }

    /// <summary>
    /// BMS ファイルのパスを新しいパスに差し替え、song.db に反映します。
    /// </summary>
    private void replaceBMSFilePath(BMSFile bmsFile, string newPath, string oldPath = null, bool calcFolderParent = true)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        if (newPath == null)
        {
            throw new ArgumentNullException("newPath");
        }
        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }
        if (!string.IsNullOrWhiteSpace(oldPath) && !bmsFile.path.Equals(newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                using (rwlockSongDBMaintenance.GetWriterGuard())
                {
                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        oldPath = bmsFile.path;
                    }
                    bmsFile.path = newPath;
                    if (calcFolderParent)
                    {
                        try
                        {
                            string directoryName = Path.GetDirectoryName(bmsFile.path);
                            bmsFile.folder = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(directoryName + "\\\0")).ToString("x");
                            bmsFile.parent = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(Path.GetDirectoryName(directoryName) + "\\\0")).ToString("x");
                        }
                        catch
                        {
                            bmsFile.parent = null;
                            bmsFile.folder = null;
                            bmsFile.adddate = null;
                            bmsFile.date = null;
                        }
                    }
                    else
                    {
                        bmsFile.parent = null;
                        bmsFile.folder = null;
                        bmsFile.adddate = null;
                        bmsFile.date = null;
                    }
                    InvalidateInstalledDirectoryIndex();
                    InvalidateBMSParentFolderListCache();
                    dbGateway.ReplaceSongPathWithMaintenance(bmsFile, oldPath);
                }
            }
        }
    }

    private bool replaceBMSFolder(string newFolderPath, string oldFolderPath)
    {
        if (string.IsNullOrWhiteSpace(newFolderPath))
        {
            throw new ArgumentNullException("newFolderPath");
        }
        if (string.IsNullOrWhiteSpace(oldFolderPath))
        {
            throw new ArgumentNullException("oldFolderPath");
        }
        newFolderPath = newFolderPath.TrimEnd(Path.DirectorySeparatorChar);
        oldFolderPath = oldFolderPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(newFolderPath))
        {
            throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, newFolderPath));
        }
        return dbGateway.ReplaceFolderRecord(oldFolderPath, newFolderPath);
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
