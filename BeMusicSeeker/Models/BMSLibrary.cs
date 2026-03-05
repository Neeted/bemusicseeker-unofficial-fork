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

public class BMSLibrary : NotificationObject
{
    public sealed class ParentFolderListCacheSnapshot
    {
        public int Version { get; set; }

        public long RebuildMs { get; set; }

        public List<string> ParentFolders { get; set; }
    }

    private enum RenameInvalidExtensionAction
    {
        Renamed,
        DeletedAsDuplicate,
        Skipped
    }

    private sealed class RenameInvalidExtensionOutcome
    {
        public RenameInvalidExtensionAction Action { get; set; }

        public string FinalPath { get; set; }

        public Exception FailureException { get; set; }

        public bool FailedDuringDelete { get; set; }
    }

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.BMSLibrary");

    private static readonly Logger everythingVerifyLogger = LogManager.GetLogger("Verify.Everything");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly bool everythingVerifyEnabled = CommandLineSwitches.IsEverythingVerifyEnabled;

    private static readonly bool everythingScanLoggingEnabled = installPerformanceLoggingEnabled;

    private static readonly FileMutationOptions targetOnlyFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly);

    private static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

    private const int playlistReferenceApplyChunkSize = 1024;

    private struct PlaylistReferenceApplyStats
    {
        public int Chunks;

        public long MaxChunkMs;

        public int YieldCount;
    }

    private static void LogInstallPerformance(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogEverythingScan(string message)
    {
        if (everythingScanLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogEverythingVerify(string message)
    {
        if (everythingVerifyEnabled)
        {
            everythingVerifyLogger.Info(message);
        }
    }

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

    private void InvalidateBMSParentFolderListCache()
    {
        lock (lockParentFolderList)
        {
            bmsParentFolderListDirty = true;
            bmsParentFolderListDirtyVersion++;
        }
    }

    public bool IsBMSParentFolderListCacheDirty()
    {
        lock (lockParentFolderList)
        {
            return bmsParentFolderListDirty;
        }
    }

    private List<string> BuildBMSParentFolderCandidates(List<BMSFile> bmsFilesSnapshot)
    {
        return getBMSDirectories().Where(delegate (string d)
        {
            if (bmsFilesSnapshot.Any(delegate (BMSFile f)
            {
                if (f != null && !string.IsNullOrWhiteSpace(f.path))
                {
                    return f.path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }))
            {
                return true;
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                if ((d + Path.DirectorySeparatorChar).StartsWith(Settings.Default.LR2CustomFolderOutputBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if ((d + Path.DirectorySeparatorChar).StartsWith(Settings.Default.LR2CustomFolderOutputBaseDirRootType + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                try
                {
                    return !Directory.EnumerateFiles(d, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any();
                }
                catch
                {
                    return false;
                }
            }
            throw new NotImplementedException();
        }).ToList();
    }

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
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<string> parentFolders = BuildBMSParentFolderCandidates(bmsFilesSnapshot);
        stopwatch.Stop();
        return new ParentFolderListCacheSnapshot
        {
            Version = version,
            RebuildMs = stopwatch.ElapsedMilliseconds,
            ParentFolders = parentFolders
        };
    }

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
        lr2config = ((getLR2Config != null) ? getLR2Config : ((Func<LR2Config>)(() => (LR2Config)null)));
        using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
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

    public List<BMSScore> GetBMSScores()
    {
        using (rwlockBMSScores.GetReaderGuard())
        {
            return BMSScores;
        }
    }

    private sealed class BmsScanPrefetchInfo
    {
        public BmsScanExecutionResult ScanResult { get; set; }

        public long ElapsedMs { get; set; }
    }

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

    public void Initialize(List<Action> tasksContinuation, SemaphoreSlim semaphore = null, bool? reloadScoresOnly = null)
    {
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        long phase1MinLoadMs = 0L;
        long phase2ScanMaintMs = 0L;
        long phase3InstallMaintenanceMs = 0L;
        long waitContinuationMs = 0L;
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
        List<Task> list = new List<Task>();
        DateTime now;
        using (rwlockBMSFilesInitializedAll.GetWriterGuard())
        {
            now = DateTime.Now;
            Stopwatch stopwatchPhase1 = Stopwatch.StartNew();
            using (rwlockBMSFilesInitializedMin.GetWriterGuard())
            {
                _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, maintenanceTblCheck: false);
            }
            stopwatchPhase1.Stop();
            phase1MinLoadMs = stopwatchPhase1.ElapsedMilliseconds;
            TimeSpan timeSpan = DateTime.Now - now;
            NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());
            long waitBeforeContinuationStartMs = 0L;
            long waitForContinuationCompleteMs = 0L;
            Stopwatch stopwatchWaitBeforeContinuationStart = Stopwatch.StartNew();
            semaphore?.Wait();
            stopwatchWaitBeforeContinuationStart.Stop();
            waitBeforeContinuationStartMs = stopwatchWaitBeforeContinuationStart.ElapsedMilliseconds;
            if (tasksContinuation != null)
            {
                for (int i = 0; i < tasksContinuation.Count; i++)
                {
                    list.Add(Task.Run(tasksContinuation[i]).Logging("Initialize"));
                }
            }
            Thread.Yield();
            now = DateTime.Now;
            Stopwatch stopwatchPhase2 = Stopwatch.StartNew();
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
            stopwatchPhase2.Stop();
            phase2ScanMaintMs = stopwatchPhase2.ElapsedMilliseconds;
            Stopwatch stopwatchPhase3 = Stopwatch.StartNew();
            _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, flag, maintenanceTblCheck: false);
            stopwatchPhase3.Stop();
            phase3InstallMaintenanceMs = stopwatchPhase3.ElapsedMilliseconds;
            if (semaphore != null && tasksContinuation != null && tasksContinuation.Count > 0)
            {
                Stopwatch stopwatchWaitForContinuationComplete = Stopwatch.StartNew();
                semaphore.Wait();
                stopwatchWaitForContinuationComplete.Stop();
                waitForContinuationCompleteMs = stopwatchWaitForContinuationComplete.ElapsedMilliseconds;
            }
            waitContinuationMs = waitBeforeContinuationStartMs + waitForContinuationCompleteMs;
            scheduleDeferredMaintenanceTableCheck = flag;
            timeSpan = DateTime.Now - now;
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
        Task.WaitAll(list.ToArray());
        if (scheduleDeferredMaintenanceTableCheck)
        {
            ScheduleDeferredMaintenanceTableCheck(deferredMaintenanceReason);
        }
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("owari: " + GC.GetTotalMemory(forceFullCollection: false));
        stopwatchTotal.Stop();
        LogInstallPerformance("init_library phase1_min_load_ms=" + phase1MinLoadMs + " phase2_scan_maint_ms=" + phase2ScanMaintMs + " phase3_install_maintenance_ms=" + phase3InstallMaintenanceMs + " wait_continuation_ms=" + waitContinuationMs + " total_ms=" + stopwatchTotal.ElapsedMilliseconds + " maintenance_tbl_check_deferred=" + scheduleDeferredMaintenanceTableCheck.ToString().ToLowerInvariant() + " set_maintenance_enabled=" + setMaintenanceInfo.ToString().ToLowerInvariant());
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
                    try
                    {
                        using LR2ScoreDBExtended lR2ScoreDBExtended = new LR2ScoreDBExtended(lr2ScoreDBPath);
                        BMSScores = lR2ScoreDBExtended.Table<BMSScore>().ToList();
                        LR2ID = lR2ScoreDBExtended.Table<LR2ScoreDB.player>().ToList().FirstOrDefault()
                            .irid.Value;
                    }
                    catch (Exception)
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
                    List<BMSPackage> list6;
                    List<BMSPackage> list7;
                    using (LR2SongDBExtended lR2SongDBExtended2 = new LR2SongDBExtended(lr2SongDBPath))
                    {
                        list6 = lR2SongDBExtended2.Table<BMSPackage>().ToList();
                        list7 = list6.Where((BMSPackage pkg) => (!File.Exists(pkg.path) && !Directory.Exists(pkg.path)) || pkg.BMSFiles.Count == 0).ToList();
                        lR2SongDBExtended2.BeginTransaction();
                        foreach (BMSPackage item8 in list7)
                        {
                            lR2SongDBExtended2.Delete<LR2SongDBExtended.install>(item8.path);
                        }
                        lR2SongDBExtended2.Commit();
                    }
                    BMSPackagesPending.AddRange(list6.Except(list7));
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
                LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
                try
                {
                    lr2Song.BeginTransaction();
                    List<string> stalePaths = (from m in lr2Song.Table<BMSFileMaintenanceInfo>().ToList()
                                               select m.path).Except(BMSFiles.Select((BMSFile i) => i.path)).ToList();
                    foreach (string stalePath in stalePaths)
                    {
                        lr2Song.Delete<LR2SongDBExtended.maintenance>(stalePath);
                    }
                    lr2Song.Commit();
                    return stalePaths.Count;
                }
                finally
                {
                    if (lr2Song != null)
                    {
                        ((IDisposable)lr2Song).Dispose();
                    }
                }
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

    private void InvalidateBMSHashIndex()
    {
        lock (lockBMSHashIndex)
        {
            bmsHashRefCount.Clear();
            bmsHashIndex.Clear();
            bmsHashIndexInitialized = false;
        }
    }

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

    private void EnsureBMSHashIndexBuiltUnsafe()
    {
        if (!bmsHashIndexInitialized)
        {
            RebuildBMSHashIndexUnsafe(BMSFiles);
        }
    }

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
                    foreach (var item in bmsFiles.Join(BMSScores, (BMSFile f) => f.hash, (BMSScore s) => s.hash, (BMSFile f, BMSScore y) => new
                    {
                        bmsFile = f,
                        bmsScores = y
                    }))
                    {
                        item.bmsFile.bmsScore = item.bmsScores;
                    }
                }
            }
        }
    }

    private LR2IRCache getIRCache(string filePath)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string rankingxml = File.ReadAllText(filePath, Encoding.GetEncoding("shift_jis"));
        DateTime lastWriteTime = File.GetLastWriteTime(filePath);
        return new LR2IRCache(rankingxml, fileNameWithoutExtension, lastWriteTime);
    }

    private void setBMSScore(LR2IRData data, LR2IRCache cache)
    {
        string text = Path.Combine(Path.Combine(Path.GetDirectoryName(lr2ScoreDBPath), "..\\..\\Ir"), data.hash + ".xml");
        BMSScore score = null;
        using (rwlockBMSScores.GetReaderGuard())
        {
            score = BMSScores.FirstOrDefault((BMSScore s) => s.hash == data.hash);
        }
        if (data.score > 0)
        {
            if (score != null)
            {
                if (score.clear < data.clear)
                {
                    score.clear = data.clear;
                }
                if (score.score < data.score)
                {
                    score.ranking = data.rank;
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                    score.perfect = data.pg;
                    score.great = data.gr;
                    score.maxcombo = data.combo;
                    score.minbp = data.minbp;
                }
                else if (score.score == data.score)
                {
                    score.ranking = data.rank;
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                }
                else
                {
                    if (!Settings.Default.SkipEstimateOfflineScoreRanking)
                    {
                        if (cache == null && File.Exists(text))
                        {
                            try
                            {
                                cache = getIRCache(text);
                            }
                            catch
                            {
                                cache = null;
                            }
                        }
                        if (cache != null)
                        {
                            score.ranking = cache.GetRankFromScore(score.score);
                        }
                    }
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                }
                score.SetStdDevVal(data.average, data.sigma);
                score.SetScoreDiffic(data.average, data.sigma);
                return;
            }
            score = new BMSScore(data);
            using (rwlockBMSScores.GetWriterGuard())
            {
                BMSScores.Add(score);
            }
            using (rwlockBMSFiles.GetReaderGuard())
            {
                foreach (BMSFile item in BMSFiles.Where((BMSFile f) => f.hash == score.hash))
                {
                    item.bmsScore = score;
                }
                return;
            }
        }
        if (score != null)
        {
            if (!Settings.Default.SkipEstimateOfflineScoreRanking)
            {
                if (cache == null && File.Exists(text))
                {
                    try
                    {
                        cache = getIRCache(text);
                    }
                    catch
                    {
                        cache = null;
                    }
                }
                if (cache != null)
                {
                    score.ranking = cache.GetRankFromScore(score.score);
                }
            }
            score.rankingNum = data.players_num;
            score.rankingLastupdate = data.lastupdate;
            score.SetStdDevVal(data.average, data.sigma);
            score.SetScoreDiffic(data.average, data.sigma);
            return;
        }
        score = new BMSScore(data);
        using (rwlockBMSScores.GetWriterGuard())
        {
            BMSScores.Add(score);
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (BMSFile item2 in BMSFiles.Where((BMSFile f) => f.hash == score.hash))
            {
                item2.bmsScore = score;
            }
        }
    }

    private List<LR2IRScore> updateLR2IRScoreTable()
    {
        if (lr2ScoreDBPath == null)
        {
            return null;
        }
        if (LR2ID == 0)
        {
            return null;
        }
        string empty = string.Empty;
        try
        {
            Uri uri = new Uri("http://www.dream-pro.info/~lavalse/LR2IR/2/getplayerxml.cgi?id=" + LR2ID);
            empty = AppHttpClient.Shared.GetString(uri, Encoding.GetEncoding("shift_jis"));
        }
        catch (Exception)
        {
            return null;
        }
        List<LR2IRScore> list;
        try
        {
            list = (from e in lr2IRScoreRegex.Matches(empty).Cast<Match>().Select(delegate (Match m)
                {
                    try
                    {
                        return new LR2IRScore(m.Groups[1].Value)
                        {
                            clear = (ClearType)int.Parse(m.Groups[2].Value),
                            notes = int.Parse(m.Groups[3].Value),
                            combo = int.Parse(m.Groups[4].Value),
                            pg = int.Parse(m.Groups[5].Value),
                            gr = int.Parse(m.Groups[6].Value),
                            gd = int.Parse(m.Groups[7].Value),
                            bd = int.Parse(m.Groups[8].Value),
                            pr = int.Parse(m.Groups[9].Value),
                            minbp = int.Parse(m.Groups[10].Value),
                            option = int.Parse(m.Groups[11].Value),
                            lastupdate = int.Parse(m.Groups[12].Value)
                        };
                    }
                    catch
                    {
                        return (LR2IRScore)null;
                    }
                })
                    where e != null
                    group e by e.hash into e
                    select e.First()).ToList();
        }
        catch (Exception)
        {
            return null;
        }
        try
        {
            if (list.Count > 0)
            {
                using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                lR2SongDBExtended.BeginTransaction();
                lR2SongDBExtended.DropTable<LR2SongDBExtended.ir_score>();
                lR2SongDBExtended.CreateTable<LR2SongDBExtended.ir_score>();
                lR2SongDBExtended.InsertAll(list, typeof(LR2SongDBExtended.ir_score));
                lR2SongDBExtended.CreateIndex("ir_score_idx_unsent", SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName(), new string[9]
                {
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.hash),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.clear),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.combo),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pg),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gr),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gd),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.bd),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pr),
                    SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.minbp)
                });
                lR2SongDBExtended.Commit();
            }
        }
        catch (Exception)
        {
            return null;
        }
        return list;
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
                foreach (BMSFile item in (from ls in BMSScores
                                          join os in scoreTable on ls.hash equals os.hash into os
                                          select new
                                          {
                                              bmsScore = ls,
                                              irScores = os.DefaultIfEmpty()
                                          } into grp
                                          from a in grp.irScores
                                          select new
                                          {
                                              bmsScore = grp.bmsScore,
                                              irScore = a
                                          } into b
                                          where b.irScore == null || b.bmsScore.score != b.irScore.score || b.bmsScore.minbp != b.irScore.minbp || (b.bmsScore.clear != b.irScore.clear && (b.bmsScore.clear != ClearType.PA || b.irScore.clear != ClearType.FC))
                                          select b).Join(BMSFiles, b => b.bmsScore.hash, bmsFile => bmsFile.hash, (a, bmsFile) => bmsFile))
                {
                    item.status |= BMSFile.BMSFileStatus.SCORE_UNSENT;
                }
            }
            IEnumerable<BMSScore> enumerable = (from os in scoreTable
                                                join ls in BMSScores on os.hash equals ls.hash into ls
                                                select new
                                                {
                                                    irScore = os,
                                                    bmsScores = ls.DefaultIfEmpty(new BMSScore(os))
                                                } into grp
                                                from a in grp.bmsScores
                                                select new
                                                {
                                                    irScore = grp.irScore,
                                                    bmsScore = a
                                                }).Select(b =>
                                            {
                                                if (b.irScore.clear > b.bmsScore.clear)
                                                {
                                                    b.bmsScore.clear = b.irScore.clear;
                                                }
                                                if (b.irScore.score > b.bmsScore.score)
                                                {
                                                    b.bmsScore.Overwrite(b.irScore);
                                                }
                                                return b.bmsScore;
                                            }).Except(BMSScores);
            BMSScores = BMSScores.Concat(enumerable).ToList();
            using (rwlockBMSFiles.GetReaderGuard())
            {
                if (BMSFiles == null)
                {
                    return;
                }
                foreach (var item2 in from f in BMSFiles
                                      join s in enumerable on f.hash equals s.hash
                                      select new
                                      {
                                          bmsFile = f,
                                          bmsScores = s
                                      })
                {
                    item2.bmsFile.bmsScore = item2.bmsScores;
                }
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
            string path = Path.Combine(Path.GetDirectoryName(lr2ScoreDBPath), "..\\..\\Ir");
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE DIR END");
            List<LR2IRData> irDataDB = null;
            try
            {
                using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                irDataDB = (from s in lR2SongDBExtended.Table<LR2IRData>().ToList()
                            where s.lr2id == LR2ID
                            select s).ToList();
            }
            catch (Exception)
            {
                return;
            }
            if (irDataDB == null)
            {
                return;
            }
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE DB DATA END");
            List<LR2IRData> irDataToBeCommited = new List<LR2IRData>();
            List<string> source = Directory.EnumerateFiles(path).AsParallel().Where(delegate (string filePath)
            {
                try
                {
                    string md5 = Path.GetFileNameWithoutExtension(filePath);
                    if (!LR2SongDB.md5HashRegex.IsMatch(Path.GetFileNameWithoutExtension(filePath)))
                    {
                        return false;
                    }
                    LR2IRData lR2IRData = irDataDB.FirstOrDefault((LR2IRData d) => d.hash == md5);
                    if (lR2IRData == null)
                    {
                        return true;
                    }
                    DateTime lastWriteTime = File.GetLastWriteTime(filePath);
                    if (lR2IRData.lastcacheupdate == lastWriteTime)
                    {
                        return false;
                    }
                    string s = string.Empty;
                    using (StreamReader reader = new StreamReader(filePath))
                    {
                        s = reader.Tail(33, 19);
                    }
                    if (!DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) || result > lR2IRData.lastupdate)
                    {
                        return true;
                    }
                    if (!lR2IRData.lastcacheupdate.HasValue)
                    {
                        lR2IRData.lastcacheupdate = lastWriteTime;
                        lock (lockRankingScores)
                        {
                            irDataToBeCommited.Add(lR2IRData);
                        }
                        setBMSScore(lR2IRData, null);
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            })
                .ToList();
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE FILE TAIL END");
            source.AsParallel().ForAll(delegate (string filePath)
            {
                LR2IRCache lR2IRCache = null;
                try
                {
                    lR2IRCache = getIRCache(filePath);
                }
                catch
                {
                    return;
                }
                LR2IRData lR2IRData = lR2IRCache.GetLR2IRData(LR2ID);
                if (lR2IRData != null)
                {
                    lock (lockRankingScores)
                    {
                        irDataToBeCommited.Add(lR2IRData);
                    }
                    setBMSScore(lR2IRData, lR2IRCache);
                }
            });
            NLogWrapper.DebuggerLogger?.Trace("IR CACHE SET SCORE END");
            using (LR2SongDBExtended lR2SongDBExtended2 = new LR2SongDBExtended(lr2SongDBPath))
            {
                lR2SongDBExtended2.BeginTransaction();
                foreach (LR2IRData item in irDataToBeCommited)
                {
                    lR2SongDBExtended2.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.hash) + " = '" + item.hash + "' AND " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id) + " = " + item.lr2id + ";");
                    lR2SongDBExtended2.InsertOrReplace(item, typeof(LR2SongDBExtended.ir_data));
                }
                lR2SongDBExtended2.Commit();
            }
            HashSet<string> hashlist = new HashSet<string>(irDataToBeCommited.Select((LR2IRData b) => b.hash), StringComparer.OrdinalIgnoreCase);
            irDataDB.AsParallel().ForAll(delegate (LR2IRData d)
            {
                if (!hashlist.Contains(d.hash))
                {
                    setBMSScore(d, null);
                }
            });
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
        List<string> list = md5s.Where((string md5) => LR2SongDB.md5HashRegex.IsMatch(md5)).ToList();
        dynamic val = new DynamicJson(DynamicJson.JsonType.array);
        for (int num = 0; num < list.Count; num++)
        {
            val[num] = list[num];
        }
        dynamic val2 = DynamicJson.Parse(AppHttpClient.Shared.PostString(rankingInfoUrl, val.ToString(), "application/json;charset=UTF-8", Encoding.UTF8, new Dictionary<string, string>
        {
            { "Accept", "application/json" }
        }));
        List<IRDataCacheInfo> outer = (from ri in ((object[])val2).Select(delegate (dynamic i)
            {
                try
                {
                    return new IRDataCacheInfo(i);
                }
                catch
                {
                    return (IRDataCacheInfo)null;
                }
            })
                                       where ri != null
                                       select ri).ToList();
        using (rwlockLR2IrDir.GetReaderGuard())
        {
            List<LR2IRData> inner;
            using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
            {
                inner = (from s in lR2SongDBExtended.Table<LR2IRData>().ToList()
                         where s.lr2id == LR2ID
                         select s).ToList();
            }
            return (from i in outer
                    join d in inner on i.md5 equals d.hash into d
                    select new
                    {
                        irCacheInfo = i,
                        irDataDB = d.DefaultIfEmpty()
                    } into grp
                    from a in grp.irDataDB
                    select new
                    {
                        irCacheInfo = grp.irCacheInfo,
                        irDataDB = a
                    } into b
                    where b.irDataDB == null || b.irCacheInfo.lastupdate > b.irDataDB.lastupdate
                    select b.irCacheInfo).ToList();
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
        List<IRDataCacheInfo> source = cacheInfo.ToList();
        List<IRDataCacheInfo> failed = new List<IRDataCacheInfo>();
        object failedLock = new object();
        List<LR2IRData> irDataToBeCommited = new List<LR2IRData>();
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            source.AsParallel().WithDegreeOfParallelism(3).ForAll(delegate (IRDataCacheInfo info)
            {
                try
                {
                    Uri address = new Uri(rankingDataUrl, "./" + info.md5 + ".xml");
                    AppHttpClient.Shared.DownloadFile(address, Path.Combine(irCacheDirPath, info.md5 + ".xml"));
                    LR2IRCache iRCache = getIRCache(Path.Combine(irCacheDirPath, info.md5 + ".xml"));
                    LR2IRData lR2IRData = iRCache.GetLR2IRData(LR2ID);
                    if (lR2IRData != null)
                    {
                        lock (lockRankingScores)
                        {
                            irDataToBeCommited.Add(lR2IRData);
                        }
                        setBMSScore(lR2IRData, iRCache);
                    }
                }
                catch
                {
                    lock (failedLock)
                    {
                        failed.Add(info);
                    }
                }
            });
        }
        using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
        {
            lR2SongDBExtended.BeginTransaction();
            foreach (LR2IRData item in irDataToBeCommited)
            {
                lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.hash) + " = '" + item.hash + "' AND " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id) + " = " + item.lr2id + ";");
                lR2SongDBExtended.InsertOrReplace(item, typeof(LR2SongDBExtended.ir_data));
            }
            lR2SongDBExtended.Commit();
        }
        return failed;
    }

    public IRSongInfo GetIRSongInfoCache(string md5orlr2bmsid, bool seaarchAggressively = false)
    {
        if (string.IsNullOrWhiteSpace(md5orlr2bmsid))
        {
            throw new ArgumentException("md5orlr2bmsid");
        }
        if (LR2SongDB.md5HashRegex.IsMatch(md5orlr2bmsid) || Regex.IsMatch(md5orlr2bmsid, "^[0-9]+$"))
        {
            try
            {
                string search_url = string.Empty;
                string search_url_sabun = string.Empty;
                IRSongInfo info = null;
                Task task = Task.Run(delegate
                {
                    dynamic val2 = DynamicJson.Parse(AppHttpClient.Shared.GetString(new Uri(songInfoUrl, "./" + md5orlr2bmsid), Encoding.UTF8));
                    info = new IRSongInfo(val2);
                });
                if (seaarchAggressively)
                {
                    Task.Run(delegate
                    {
                        dynamic val2 = DynamicJson.Parse(AppHttpClient.Shared.GetString(new Uri("http://www.ribbit.xyz/bms/search/run?search[value]=" + md5orlr2bmsid), Encoding.UTF8));
                        search_url = val2.data[0][10].ToString().Trim();
                        search_url_sabun = val2.data[0][11].ToString().Trim();
                    }).Wait();
                }
                task.Wait();
                if (seaarchAggressively)
                {
                    if (!string.IsNullOrWhiteSpace(search_url))
                    {
                        info.url = (info.url + " " + search_url).Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(search_url_sabun))
                    {
                        info.url_diff = (info.url_diff + " " + search_url_sabun).Trim();
                    }
                }
                return info;
            }
            catch
            {
                dynamic val = DynamicJson.Parse(AppHttpClient.Shared.GetString(new Uri(songInfoUrl, "./" + md5orlr2bmsid), Encoding.UTF8));
                return new IRSongInfo(val);
            }
        }
        throw new ArgumentException("md5orlr2bmsid");
    }

    private void setMaintenanceInfo(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        if (bmsFiles == null)
        {
            return;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> list = (forceUpdate ? bmsFiles.ToList() : bmsFiles.Where((BMSFile f) => !f.maintenanceInfo.IsInformationChecked() || string.IsNullOrWhiteSpace(f.maintenanceInfo.encoding)).ToList());
            bool maintenanceInfoUpdated = false;
            if (list.Count > 0)
            {
                foreach (IEnumerable<BMSFile> item in list.Section(1000))
                {
                    using (rwlockSongDBMaintenance.GetWriterGuard())
                    {
                        object bmsFilesReloadedLock = new object();
                        List<BMSFile> bmsFilesReloaded = new List<BMSFile>();
                        item.AsParallel().ForAll(delegate (BMSFile f)
                        {
                            string hash = f.hash;
                            int retryCount = 0;
                            while (true)
                            {
                                try
                                {
                                    f.SetHealthStatus(bmsFolderAllFileList, forceUpdate);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    if (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                                    {
                                        if (retryCount < 3)
                                        {
                                            retryCount++;
                                            Thread.Sleep(200);
                                            continue;
                                        }
                                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsLoadFailedSkip, f.path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                        return;
                                    }
                                    throw;
                                }
                            }
                            if (forceUpdate || string.IsNullOrWhiteSpace(f.maintenanceInfo.encoding))
                            {
                                f.SetEncosingInfo();
                            }
                            if (!string.IsNullOrWhiteSpace(f.maintenanceInfo.encoding) && !f.maintenanceInfo.encoding.StartsWith("shift_jis") && !f.maintenanceInfo.encoding.EndsWith("?") && f.maintenanceInfo.encoding != "unknown")
                            {
                                BMSFile.ReloadBMSFileWithEncoding(f, f.maintenanceInfo.encoding);
                                f.maintenanceInfo.is_encoding_fixed = true;
                                lock (bmsFilesReloadedLock)
                                {
                                    bmsFilesReloaded.Add(f);
                                    return;
                                }
                            }
                            if (hash != f.hash)
                            {
                                lock (bmsFilesReloadedLock)
                                {
                                    bmsFilesReloaded.Add(f);
                                }
                            }
                        });
                        using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                        {
                            lR2SongDBExtended.BeginTransaction();
                            foreach (BMSFile item2 in item.Where((BMSFile f) => f.maintenanceInfo.IsInformationChecked()))
                            {
                                lR2SongDBExtended.InsertOrReplace(item2.maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
                            }
                            foreach (BMSFile item3 in bmsFilesReloaded)
                            {
                                lR2SongDBExtended.InsertOrReplace(item3, typeof(LR2SongDB.song));
                            }
                            lR2SongDBExtended.Commit();
                        }
                        maintenanceInfoUpdated = true;
                    }
                }
            }
            if (maintenanceInfoUpdated)
            {
                Task.Run(delegate
                {
                    RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
                    RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
                    RaisePropertyChanged(() => BMSFilesGarbled);
                    RaisePropertyChanged(() => BMSFilesGarbledFixed);
                }).Logging("setMaintenanceInfo");
            }
            foreach (BMSFile bmsFile in bmsFiles)
            {
                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile);
            }
        }
    }

    private bool checkBMSFileNeedToBeFixedAndSetWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo mtInfo = null, bool strictCheck = false)
    {
        if (bmsFile == null)
        {
            return false;
        }
        bool flag = false;
        bool flag2 = false;
        bool flag3 = false;
        bool flag4 = false;
        bool flag5 = false;
        bool flag6 = false;
        int num = (strictCheck ? 100 : 100);
        int num2 = (strictCheck ? 100 : 100);
        int num3 = (strictCheck ? 100 : 100);
        if (mtInfo == null)
        {
            mtInfo = bmsFile.maintenanceInfo;
        }
        if (!string.IsNullOrWhiteSpace(bmsFile.warning))
        {
            bmsFile.warning = null;
        }
        int? wAVHealth = mtInfo.GetWAVHealth();
        if (wAVHealth.HasValue && wAVHealth < num)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            BMSFile bMSFile = bmsFile;
            bMSFile.warning = bMSFile.warning + string.Format(Resources.Warning_WavFilesNotFound, wAVHealth, mtInfo.wav_files_defined - mtInfo.wav_files_existing, mtInfo.wav_files_defined);
            flag = true;
        }
        int? bGAHealth = mtInfo.GetBGAHealth();
        if (bGAHealth.HasValue && bGAHealth < num2)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            BMSFile bMSFile = bmsFile;
            bMSFile.warning = bMSFile.warning + string.Format(Resources.Warning_BgaFilesNotFound, bGAHealth, mtInfo.bga_files_defined - mtInfo.bga_files_existing, mtInfo.bga_files_defined);
            flag2 = true;
        }
        int? movieHealth = mtInfo.GetMovieHealth();
        if (movieHealth.HasValue && movieHealth < num3)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            BMSFile bMSFile = bmsFile;
            bMSFile.warning = bMSFile.warning + string.Format(Resources.Warning_MovieFilesNotFound, movieHealth, mtInfo.movie_files_defined - mtInfo.movie_files_existing, mtInfo.movie_files_defined);
            flag3 = true;
        }
        if (mtInfo.GetStagefileHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += Resources.Warning_StagefileNotFound;
            flag4 = true;
        }
        if (mtInfo.GetBackbmpHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += Resources.Warning_BackbmpNotFound;
            flag5 = true;
        }
        if (mtInfo.GetBannerHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += Resources.Warning_BannerNotFound;
            flag6 = true;
        }
        return flag || flag2 || flag3 || flag4 || flag5 || flag6;
    }

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
                    List<BMSFileMaintenanceInfo> list = (from f in bmsFiles
                                                         select f.maintenanceInfo into m
                                                         where m.is_files_warning_ignored == unset
                                                         select m).ToList();
                    using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                    lR2SongDBExtended.BeginTransaction();
                    foreach (BMSFileMaintenanceInfo item in list)
                    {
                        item.is_files_warning_ignored = !unset;
                        lR2SongDBExtended.InsertOrReplace(item, typeof(LR2SongDBExtended.maintenance));
                    }
                    lR2SongDBExtended.Commit();
                }
            }
        }
        RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
        RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
    }

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
                return bmsFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.maintenanceInfo.encoding) && isInFixedList == f.maintenanceInfo.is_encoding_fixed && (f.maintenanceInfo.encoding.EndsWith("?") || f.maintenanceInfo.encoding != "unknown") && !f.maintenanceInfo.encoding.StartsWith("shift_jis")).ToList();
            }
        }
    }

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
            if (!string.IsNullOrWhiteSpace(encoding))
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    List<BMSFile> list = bmsFiles.Where((BMSFile f) => f.maintenanceInfo.encoding != encoding && (encoding != "shift_jis" || f.maintenanceInfo.is_encoding_fixed) && File.Exists(f.path)).ToList();
                    if (list.Count > 0)
                    {
                        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                        lR2SongDBExtended.BeginTransaction();
                        foreach (BMSFile item in list)
                        {
                            BMSFile.ReloadBMSFileWithEncoding(item, encoding);
                            lR2SongDBExtended.InsertOrReplace(item, typeof(LR2SongDB.song));
                        }
                        lR2SongDBExtended.Commit();
                    }
                }
            }
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                List<BMSFileMaintenanceInfo> list2 = bmsFiles.Select((BMSFile f) => f.maintenanceInfo).Where(delegate (BMSFileMaintenanceInfo m)
                {
                    if (m.encoding == encoding || (string.IsNullOrWhiteSpace(m.encoding) && string.IsNullOrWhiteSpace(encoding)))
                    {
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(encoding))
                    {
                        m.encoding = encoding;
                        m.is_encoding_fixed = true;
                        return true;
                    }
                    if ((m.encoding.EndsWith("?") && m.encoding != "shift_jis?") || m.encoding == "unknown")
                    {
                        m.encoding = "shift_jis";
                        m.is_encoding_fixed = true;
                        return true;
                    }
                    return false;
                }).ToList();
                if (list2.Count <= 0)
                {
                    return;
                }
                using LR2SongDBExtended lR2SongDBExtended2 = new LR2SongDBExtended(lr2SongDBPath);
                lR2SongDBExtended2.BeginTransaction();
                foreach (BMSFileMaintenanceInfo item2 in list2)
                {
                    lR2SongDBExtended2.InsertOrReplace(item2, typeof(LR2SongDBExtended.maintenance));
                }
                lR2SongDBExtended2.Commit();
            }
        }
    }

    private void setZeroNoteAndCommitToDB(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            bmsFiles = BMSFiles;
        }
        List<BMSFile> targetFiles = bmsFiles.Where((BMSFile f) => f != null && !string.IsNullOrWhiteSpace(f.path)).GroupBy((BMSFile f) => f.path, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, BMSFile> g) => g.First()).ToList();
        if (targetFiles.Count == 0)
        {
            return;
        }
        using (rwlockBMSFiles.GetWriterGuard())
        {
            targetFiles.Where((BMSFile f) => f.notes == 0).ToList();
            List<BMSFile> source = targetFiles.Where((BMSFile f) => !f.notes.HasValue && File.Exists(f.path)).ToList();
            source = source.AsParallel().Where(delegate (BMSFile f)
            {
                try
                {
                    return f.SetNotesIfZeroNote();
                }
                catch (Exception ex)
                {
                    if (!(ex is DirectoryNotFoundException) && !(ex is FileNotFoundException) && !(ex is IOException) && !(ex is PathTooLongException) && !(ex is SecurityException) && !(ex is UnauthorizedAccessException))
                    {
                        throw;
                    }
                    DispatcherMessageBox.Show(string.Format(Resources.Error_BmsLoadFailedSkip, f.path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    return false;
                }
            }).ToList();
            if (source.Count <= 0)
            {
                return;
            }
            using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
            {
                lR2SongDBExtended.BeginTransaction();
                foreach (BMSFile item in source)
                {
                    lR2SongDBExtended.InsertOrReplace(item, typeof(LR2SongDB.song));
                }
                lR2SongDBExtended.Commit();
            }
            Task.Run(delegate
            {
                RaisePropertyChanged(() => BMSFilesZeroNote);
            }).Logging("setZeroNoteAndCommitToDB");
        }
    }

    public void RecheckZeroNoteWarnings()
    {
        List<BMSFile> allFiles;
        using (rwlockBMSFiles.GetReaderGuard())
        {
            allFiles = ((BMSFiles == null) ? new List<BMSFile>() : BMSFiles.Where((BMSFile f) => f != null).ToList());
        }
        List<BMSFile> zeroNoteFiles = allFiles.Where((BMSFile f) => f.notes == 0 && !string.IsNullOrWhiteSpace(f.path)).ToList();
        List<BMSFile> staleMismatchFiles = allFiles.Where((BMSFile f) => f.notes != 0 && f.HasZeroNoteMismatchWarning).ToList();
        int clearedCount = 0;
        foreach (BMSFile staleMismatchFile in staleMismatchFiles)
        {
            if (staleMismatchFile.HasZeroNoteMismatchWarning)
            {
                staleMismatchFile.HasZeroNoteMismatchWarning = false;
                clearedCount++;
            }
        }
        int mismatchCount = 0;
        int skippedCount = 0;
        foreach (BMSFile zeroNoteFile in zeroNoteFiles)
        {
            try
            {
                bool isZeroNoteByFile = BMSFile.IsZeroNoteBMSFile(zeroNoteFile.path);
                if (!isZeroNoteByFile)
                {
                    zeroNoteFile.HasZeroNoteMismatchWarning = true;
                    mismatchCount++;
                }
                else
                {
                    if (zeroNoteFile.HasZeroNoteMismatchWarning)
                    {
                        clearedCount++;
                    }
                    zeroNoteFile.HasZeroNoteMismatchWarning = false;
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
            {
                if (zeroNoteFile.HasZeroNoteMismatchWarning)
                {
                    clearedCount++;
                }
                zeroNoteFile.HasZeroNoteMismatchWarning = false;
                skippedCount++;
                NLogWrapper.FileLogger?.Warn(ex, "zero_note_recheck skipped: path=" + zeroNoteFile.path);
            }
        }
        NLogWrapper.FileLogger?.Info(string.Format("zero_note_recheck total={0} mismatch={1} cleared={2} skipped={3}", zeroNoteFiles.Count, mismatchCount, clearedCount, skippedCount));
    }

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
        return bmsFiles.Where((BMSFile f) => f.notes == 0).ToList();
    }

    private void setModeAndCommitToDB(IEnumerable<BMSFile> bmsFiles, bool forceUpdate = false)
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> list = bmsFiles.Where((BMSFile f) => f != null && (forceUpdate || !f.mode.HasValue) && File.Exists(f.path)).ToList();
            if (list.Count <= 0)
            {
                return;
            }
            foreach (BMSFile item in list)
            {
                item.SetMode();
            }
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSFile item2 in list)
            {
                lR2SongDBExtended.InsertOrReplace(item2, typeof(LR2SongDB.song));
            }
            lR2SongDBExtended.Commit();
        }
    }

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
                    ClearDuplicateState(snapshot);
                    List<IGrouping<string, BMSFile>> duplicateHashGroups = snapshot.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.hash)).GroupBy((BMSFile file) => file.hash, StringComparer.OrdinalIgnoreCase).Where((IGrouping<string, BMSFile> group) => group.Count() > 1).ToList();
                    foreach (IGrouping<string, BMSFile> duplicateHashGroup in duplicateHashGroups)
                    {
                        foreach (BMSFile item in duplicateHashGroup)
                        {
                            item.IsHashDuplicated = true;
                            SetDuplicateWarning(item);
                        }
                    }

                    System.Diagnostics.Stopwatch swNew = System.Diagnostics.Stopwatch.StartNew();
                    Dictionary<string, int> dirToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    List<string> idToDir = new List<string>();
                    List<List<int>> groupIndices = new List<List<int>>();

                    foreach (IGrouping<string, BMSFile> duplicateHashGroup2 in duplicateHashGroups)
                    {
                        List<int> currentGroup = new List<int>();
                        foreach (BMSFile bmsInfo in duplicateHashGroup2)
                        {
                            string dir = DirectoryExt.GetDirectoryNameSimple(bmsInfo.path);
                            if (string.IsNullOrWhiteSpace(dir))
                            {
                                continue;
                            }
                            if (!dirToId.TryGetValue(dir, out int id))
                            {
                                id = idToDir.Count;
                                dirToId[dir] = id;
                                idToDir.Add(dir);
                            }
                            currentGroup.Add(id);
                        }
                        if (currentGroup.Count > 0)
                        {
                            groupIndices.Add(currentGroup);
                        }
                    }

                    int n = idToDir.Count;
                    int[] parent = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        parent[i] = i;
                    }

                    foreach (List<int> group in groupIndices)
                    {
                        if (group.Count > 1)
                        {
                            int first = group[0];
                            for (int i = 1; i < group.Count; i++)
                            {
                                int root1 = first;
                                while (parent[root1] != root1)
                                {
                                    parent[root1] = parent[parent[root1]];
                                    root1 = parent[root1];
                                }
                                int root2 = group[i];
                                while (parent[root2] != root2)
                                {
                                    parent[root2] = parent[parent[root2]];
                                    root2 = parent[root2];
                                }
                                if (root1 != root2)
                                {
                                    parent[root1] = root2;
                                }
                            }
                        }
                    }

                    Dictionary<int, HashSet<string>> rootViewToDirs = new Dictionary<int, HashSet<string>>();
                    for (int i = 0; i < n; i++)
                    {
                        int root = i;
                        while (parent[root] != root)
                        {
                            parent[root] = parent[parent[root]];
                            root = parent[root];
                        }
                        if (!rootViewToDirs.TryGetValue(root, out HashSet<string> set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            rootViewToDirs[root] = set;
                        }
                        set.Add(idToDir[i]);
                    }

                    List<HashSet<string>> mergedDuplicateDirectorySets = rootViewToDirs.Values.ToList();
                    // 1. 全ファイルをディレクトリごとにバケット化 (O(Files))
                    Dictionary<string, List<BMSFile>> filesByDir = new Dictionary<string, List<BMSFile>>(StringComparer.OrdinalIgnoreCase);
                    foreach (BMSFile bms in snapshot)
                    {
                        string dir = DirectoryExt.GetDirectoryNameSimple(bms.path);
                        if (!filesByDir.TryGetValue(dir, out List<BMSFile> list))
                        {
                            list = new List<BMSFile>();
                            filesByDir[dir] = list;
                        }
                        list.Add(bms);
                    }

                    // 2. 重複グループの構築 (O(Groups + FilesInGroups))
                    BMSFilesDuplicated = mergedDuplicateDirectorySets
                        .Select((HashSet<string> dirs) =>
                        {
                            List<BMSFile> groupFiles = new List<BMSFile>();
                            foreach (string d in dirs)
                            {
                                if (filesByDir.TryGetValue(d, out List<BMSFile> list))
                                {
                                    groupFiles.AddRange(list);
                                }
                            }
                            return new DuplicateGroup(groupFiles, dirs.ToList());
                        })
                        .Where((DuplicateGroup group) => group.Files.Count > 0)
                        .OrderBy((DuplicateGroup group) => group.Header)
                        .ToList();

                    swNew.Stop();
                    LogInstallPerformance($"SearchBMSFilesDuplicated: NewAlgo={swNew.ElapsedMilliseconds}ms, Groups={mergedDuplicateDirectorySets.Count}");
                }
            }
        }
    }
    private static readonly string DuplicateWarningMessage = Resources.Warning_DuplicateBmsFile;

    private static void ClearDuplicateState(IEnumerable<BMSFile> files)
    {
        foreach (BMSFile file in files)
        {
            file.IsHashDuplicated = false;
            file.warning = RemoveDuplicateWarning(file.warning);
        }
    }

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

    public List<BMSPackage> InstallBMSFilesAuto(IEnumerable<string> installPaths)
    {
        List<BMSPackage> list = new List<BMSPackage>();
        List<BMSPackage> bmsPackages = new List<BMSPackage>();
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
                            return bmsPackages;
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
                        installPaths = installPaths.Where((string file) => File.Exists(file) || Directory.Exists(file)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        IEnumerable<IGrouping<string, string>> source = from file in installPaths
                                                                        group file by Path.GetDirectoryName(file).ToUpperInvariant();
                        bmsPackages = source.SelectMany(delegate (IGrouping<string, string> paths)
                        {
                            List<string> list5 = paths.Where((string path) => File.Exists(path)).ToList();
                            List<string> list6 = paths.Where((string path) => Directory.Exists(path)).ToList();
                            List<string> source2 = list5.Where((string file) => BMSFile.bmsExtensions.Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) && File.Exists(file)).ToList();
                            string[] fileSystemEntries = Directory.GetFileSystemEntries(paths.Key, "*", System.IO.SearchOption.TopDirectoryOnly);
                            if (list5.Count + list6.Count == fileSystemEntries.Length)
                            {
                                return searchBMSFilesRecursively(paths.Key);
                            }
                            if (source2.Count() > 0 && source2.Any(delegate (string bmsFilePath)
                            {
                                BMSFile bMSFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
                                bMSFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                                return bMSFile.maintenanceInfo.wav_files_existing > 0 || bMSFile.maintenanceInfo.bga_files_existing > 0;
                            }))
                            {
                                return searchBMSFilesRecursively(paths.Key);
                            }
                            List<BMSPackage> first = source2.Select((string filePath) => new BMSPackage
                            {
                                path = filePath,
                                delete_parent = false
                            }).ToList();
                            List<BMSPackage> second = list6.SelectMany((string dir) => searchBMSFilesRecursively(dir)).ToList();
                            return first.Concat(second);
                        }).ToList();
                        bmsPackages = bmsPackages.Where((BMSPackage pkg) => !getBMSDirectories().Any((string dir) => pkg.path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (bmsPackages.Count == 0)
                        {
                            return bmsPackages;
                        }
                        bmsPackages = bmsPackages.Where((BMSPackage newPkg) => !BMSPackagesPending.Any((BMSPackage oldPkg) => newPkg.path.Equals(oldPkg.path, StringComparison.OrdinalIgnoreCase))).ToList();
                        bmsPackages = bmsPackages.Where((BMSPackage newPkg) => !BMSPackagesPending.Any((BMSPackage oldPkg) => Directory.Exists(oldPkg.path) && newPkg.path.StartsWith(oldPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (bmsPackages.Count == 0)
                        {
                            return bmsPackages;
                        }
                        if (bmsPackages.Any((BMSPackage newPkg) => Directory.Exists(newPkg.path)))
                        {
                            List<BMSPackage> list2 = BMSPackagesPending.Where(delegate (BMSPackage oldPkg)
                            {
                                IEnumerable<BMSPackage> source2 = bmsPackages.Where((BMSPackage newPkg) => Directory.Exists(newPkg.path));
                                string dir;
                                if (Directory.Exists(oldPkg.path))
                                {
                                    dir = oldPkg.path + Path.DirectorySeparatorChar;
                                }
                                else
                                {
                                    if (!File.Exists(oldPkg.path))
                                    {
                                        return true;
                                    }
                                    dir = oldPkg.path;
                                }
                                return source2.Any((BMSPackage newPkg) => dir.StartsWith(newPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                            }).ToList();
                            BMSPackagesPending.Remove(list2);
                            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                            lR2SongDBExtended.BeginTransaction();
                            foreach (BMSPackage item in list2)
                            {
                                lR2SongDBExtended.Delete<LR2SongDBExtended.install>(item.path);
                            }
                            lR2SongDBExtended.Commit();
                        }
                        ILookup<bool, BMSPackage> lookup = bmsPackages.AsParallel().ToLookup((BMSPackage pkg) => pkg.BMSFiles.AsParallel().Select(delegate (BMSFile bmsFile)
                        {
                            bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                            if (BMSFiles.Select((BMSFile x) => x.hash).Contains(bmsFile.hash))
                            {
                                bmsFile.warning = Resources.Warning_AlreadyInstalled;
                                return true;
                            }
                            if (!Directory.Exists(pkg.path))
                            {
                                bmsFile.warning = Resources.Warning_SingleBmsFile;
                                return true;
                            }
                            return checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true);
                        }).ToList()
                            .Any((bool b) => b));
                        List<BMSPackage> list3 = new List<BMSPackage>();
                        List<BMSPackage> list4 = new List<BMSPackage>();
                        if (lookup.Contains(key: false))
                        {
                            list3 = lookup[false].ToList();
                        }
                        if (lookup.Contains(key: true))
                        {
                            list4 = lookup[true].ToList();
                        }
                        if (list3.Count() > 0)
                        {
                            if (!Settings.Default.KeepInstallablePackagesPending && SearchTargets != null && SearchTargets.Count() > 0 && Directory.Exists(SearchTargets[0]))
                            {
                                list3 = installBMSPackages(list3);
                            }
                            list4 = list4.Concat(list3).ToList();
                        }
                        if (list4.Count() > 0)
                        {
                            using (LR2SongDBExtended lR2SongDBExtended2 = new LR2SongDBExtended(lr2SongDBPath))
                            {
                                lR2SongDBExtended2.InsertAll(list4, typeof(LR2SongDBExtended.install));
                            }
                            BMSPackagesPending.AddRange(list4);
                        }
                        list = list4;
                    }
                }
                foreach (BMSPackage item2 in list)
                {
                    SearchEstimatedInstallationDirectory(item2);
                }
            }
        }
        return bmsPackages;
    }

    private List<BMSPackage> searchBMSFilesRecursively(string dirfullpath, bool recursive = false)
    {
        List<BMSPackage> list = new List<BMSPackage>();
        IEnumerable<string> enumerable;
        try
        {
            enumerable = Directory.EnumerateFileSystemEntries(dirfullpath, "*", System.IO.SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return list;
        }
        if (enumerable == null || enumerable.Count() == 0)
        {
            return list;
        }
        List<string> source = enumerable.Where((string file) => File.Exists(file)).ToList();
        List<string> source2 = enumerable.Where((string file) => Directory.Exists(file)).ToList();
        List<string> source3 = source.Where((string file) => BMSFile.bmsExtensions.Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) && File.Exists(file)).ToList();
        source.Where((string file) => BMSFile.wavExtensions.Concat(BMSFile.bgaAllExtensions).Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) && File.Exists(file)).ToList();
        if (source3.Count() > 0)
        {
            if (source3.Count() == 1 || source3.Any(delegate (string bmsFilePath)
            {
                BMSFile bMSFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
                bMSFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                return bMSFile.maintenanceInfo.wav_files_existing > 0 || bMSFile.maintenanceInfo.bga_files_existing > 0;
            }))
            {
                list.Add(new BMSPackage
                {
                    path = dirfullpath,
                    delete_parent = false
                });
            }
            else
            {
                List<IEnumerable<string>> source4 = source3.Select((string file) => BMSFile.CreateBMSFileFromFile(file)).Select((Func<BMSFile, IEnumerable<string>>)((BMSFile bmsInfo) => (from s in bmsInfo.WAVfiles.Concat(bmsInfo.BGAfiles)
                                                                                                                                                                                           select Path.GetFileNameWithoutExtension(s).ToUpperInvariant()).Distinct().ToList())).ToList();
                if ((double)source4.Aggregate(Enumerable.Intersect).ToList().Count() / (double)source4.Select((IEnumerable<string> l) => l.Count()).Min() >= dupRateThreshInOnePkg)
                {
                    list.Add(new BMSPackage
                    {
                        path = dirfullpath,
                        delete_parent = false
                    });
                }
                else
                {
                    list = source3.Select((string filePath) => new BMSPackage
                    {
                        path = filePath,
                        delete_parent = recursive
                    }).ToList();
                }
            }
        }
        else if (source2.Count() > 0)
        {
            return source2.SelectMany((string dir) => searchBMSFilesRecursively(dir, recursive: true)).ToList();
        }
        return list;
    }

    private enum ComponentMoveDecision
    {
        Move,
        Overwrite,
        SkipSame,
        SkipOlderOrEqual
    }

    private enum SmartOverwriteHashCompareResult
    {
        Same,
        Different,
        Unavailable
    }

    private sealed class ComponentMovePlanItem
    {
        public string SourcePath { get; set; }

        public string DestinationPath { get; set; }
    }

    private sealed class ComponentMovePlanBuildResult
    {
        public List<ComponentMovePlanItem> PlanItems { get; } = new List<ComponentMovePlanItem>();

        public int SkippedByExclusion { get; set; }
    }

    private sealed class ComponentMoveSummary
    {
        public int Total { get; set; }

        public int Moved { get; set; }

        public int Overwritten { get; set; }

        public int SkippedSame { get; set; }

        public int SkippedOlder { get; set; }

        public int DeletedAfterSkip { get; set; }

        public int SkippedByExclusion { get; set; }

        public int SkippedSamePath { get; set; }

        public int Failed { get; set; }

        public int RenamedKeep { get; set; }

        public int RenamedFromOverwrite { get; set; }

        public int RenamedFromSkipOlder { get; set; }

        public int HashChecked { get; set; }

        public int HashSameSkip { get; set; }

        public int HashDiffRenamed { get; set; }

        public int HashUnavailableRenamed { get; set; }
    }

    private static readonly TimeSpan smartComponentOverwriteTimeTolerance = TimeSpan.FromSeconds(2.0);

    private static readonly HashSet<string> smartOverwriteProtectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".bmx",
        ".pmx",
        ".bmson"
    };

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
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && smartOverwriteProtectedExtensions.Contains(extension);
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
        if (!File.Exists(dstFilePath))
        {
            return ComponentMoveDecision.Move;
        }
        FileInfo fileInfo = new FileInfo(srcFilePath);
        FileInfo fileInfo2 = new FileInfo(dstFilePath);
        DateTime lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        DateTime lastWriteTimeUtc2 = fileInfo2.LastWriteTimeUtc;
        if (fileInfo.Length == fileInfo2.Length && Math.Abs((lastWriteTimeUtc - lastWriteTimeUtc2).TotalSeconds) <= smartComponentOverwriteTimeTolerance.TotalSeconds)
        {
            return ComponentMoveDecision.SkipSame;
        }
        if (lastWriteTimeUtc > lastWriteTimeUtc2)
        {
            return ComponentMoveDecision.Overwrite;
        }
        return ComponentMoveDecision.SkipOlderOrEqual;
    }

    private static bool IsSamePath(string path1, string path2)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetRelativePathFromDirectory(string rootDirectory, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Path.GetFileName(targetPath);
        }
        string text = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(targetPath);
        }
        return targetPath.Substring(text.Length);
    }

    private static ComponentMovePlanBuildResult BuildComponentMovePlan(IEnumerable<string> installComponentFiles, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        ComponentMovePlanBuildResult componentMovePlanBuildResult = new ComponentMovePlanBuildResult();
        HashSet<string> hashSet = ((excludedComponentPaths != null && excludedComponentPaths.Count > 0) ? new HashSet<string>(excludedComponentPaths, StringComparer.OrdinalIgnoreCase) : null);
        foreach (string installComponentFile in installComponentFiles)
        {
            if (File.Exists(installComponentFile))
            {
                if (hashSet != null && hashSet.Contains(installComponentFile))
                {
                    componentMovePlanBuildResult.SkippedByExclusion++;
                    continue;
                }
                componentMovePlanBuildResult.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = installComponentFile,
                    DestinationPath = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile))
                });
                continue;
            }
            if (!Directory.Exists(installComponentFile))
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, installComponentFile);
            }
            string text = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile));
            foreach (string item in Directory.EnumerateFiles(installComponentFile, "*", System.IO.SearchOption.AllDirectories))
            {
                if (hashSet != null && hashSet.Contains(item))
                {
                    componentMovePlanBuildResult.SkippedByExclusion++;
                    continue;
                }
                componentMovePlanBuildResult.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = item,
                    DestinationPath = Path.Combine(text, GetRelativePathFromDirectory(installComponentFile, item))
                });
            }
        }
        return componentMovePlanBuildResult;
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

    private enum CleanupSourceKind
    {
        Directory,
        File,
        MissingSource
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
                DispatcherMessageBox.Show(string.Format(Resources.Error_FolderDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            else
            {
                DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
            DispatcherMessageBox.Show(string.Format(Resources.Error_InstallFailed, pkg.path, destinationDirectory, GetDisplayedExceptionMessage(installException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        List<BMSFile> bmsFilesToBeAdded = new List<BMSFile>();
        List<BMSPackage> failedPackages = new List<BMSPackage>();
        Stopwatch stopwatchMove = Stopwatch.StartNew();
        foreach (BMSPackage package in bmsPackagesInstall)
        {
            HashSet<string> excludedComponentPaths = null;
            excludedComponentPathsByPackage?.TryGetValue(package, out excludedComponentPaths);
            if (moveBMSPackageFiles(package, installationDirectory, showMessageBoxOnInstallFail: true, deleteAllContents: deleteSourceContentsAfterSuccessfulInstall, existingHashes: existingHashes, excludedComponentPaths: excludedComponentPaths))
            {
                bmsFilesToBeAdded.AddRange(package.BMSFiles);
                if (existingHashes != null)
                {
                    // 一括導入中に追加済みハッシュを即時予約し、後続パッケージでの重複導入を防ぐ。
                    foreach (BMSFile bmsFile in package.BMSFiles)
                    {
                        if (IsBMSHashAvailable(bmsFile.hash))
                        {
                            existingHashes.Add(bmsFile.hash);
                        }
                    }
                }
                bool shouldSkipInstalledPackageRegistration = skipInstalledPackageWhenNoBms && (package.BMSFiles == null || package.BMSFiles.Count == 0);
                if (deferredInstalledPackages != null)
                {
                    if (!shouldSkipInstalledPackageRegistration)
                    {
                        deferredInstalledPackages.Add(package);
                    }
                }
                else
                {
                    if (!shouldSkipInstalledPackageRegistration)
                    {
                        BMSPackagesInstalled.Add(package);
                    }
                }
            }
            else if (File.Exists(package.path) || Directory.Exists(package.path))
            {
                failedPackages.Add(package);
            }
        }
        stopwatchMove.Stop();
        Stopwatch stopwatchSongDb = Stopwatch.StartNew();
        using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
        {
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSFile item2 in bmsFilesToBeAdded)
            {
                lR2SongDBExtended.InsertOrReplace(item2, typeof(LR2SongDB.song));
            }
            lR2SongDBExtended.Commit();
        }
        stopwatchSongDb.Stop();
        Stopwatch stopwatchMaintenance = Stopwatch.StartNew();
        if (deferredMaintenanceTargets != null)
        {
            deferredMaintenanceTargets.AddRange(bmsFilesToBeAdded);
        }
        else
        {
            setMaintenanceInfo(bmsFilesToBeAdded, forceUpdate: true);
        }
        stopwatchMaintenance.Stop();
        Stopwatch stopwatchZeroNote = Stopwatch.StartNew();
        setZeroNoteAndCommitToDB(bmsFilesToBeAdded);
        stopwatchZeroNote.Stop();
        Stopwatch stopwatchScore = Stopwatch.StartNew();
        SetBMSScore(bmsFilesToBeAdded);
        stopwatchScore.Stop();
        Stopwatch stopwatchApply = Stopwatch.StartNew();
        HashSet<string> addedPathSet = new HashSet<string>(bmsFilesToBeAdded.Select((BMSFile ff) => ff.path), StringComparer.OrdinalIgnoreCase);
        BMSFiles = BMSFiles.Where((BMSFile f) => !addedPathSet.Contains(f.path)).Concat(bmsFilesToBeAdded).ToList();
        bmsFilesToBeAdded.Select((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path)).Distinct().AsParallel()
            .ForAll(delegate (string dir)
            {
                bmsFolderAllFileList.AddDir(dir);
            });
        stopwatchApply.Stop();
        stopwatchTotal.Stop();
        LogInstallPerformance("installBMSPackages dst=" + (installationDirectory ?? "(auto)") + " packages=" + bmsPackagesInstall.Count() + " addedFiles=" + bmsFilesToBeAdded.Count + " failedPackages=" + failedPackages.Count + " deleteSourceContents=" + deleteSourceContentsAfterSuccessfulInstall + " moveMs=" + stopwatchMove.ElapsedMilliseconds + " songDbMs=" + stopwatchSongDb.ElapsedMilliseconds + " maintenanceMs=" + stopwatchMaintenance.ElapsedMilliseconds + " zeroNoteMs=" + stopwatchZeroNote.ElapsedMilliseconds + " scoreMs=" + stopwatchScore.ElapsedMilliseconds + " applyMs=" + stopwatchApply.ElapsedMilliseconds + " totalMs=" + stopwatchTotal.ElapsedMilliseconds);
        return failedPackages;
    }

    private enum InstallationEstimateMode
    {
        Normal,
        Fix,
        MergeNoSourceCompensation
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
        searchEstimatedInstallationDirectory(bmsFiles, asParallel, fixMode ? InstallationEstimateMode.Fix : InstallationEstimateMode.Normal);
    }

    private void searchEstimatedInstallationDirectory(IEnumerable<BMSFile> bmsFiles, bool asParallel, InstallationEstimateMode estimateMode)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<BMSFile> targetBmsFiles = null;
                bool isFixMode = estimateMode == InstallationEstimateMode.Fix;
                bool isMergeMode = estimateMode == InstallationEstimateMode.MergeNoSourceCompensation;
                bool isCorrectionLikeMode = isFixMode || isMergeMode;
                try
                {
                    // 多重列挙を避けるため、最初に対象を確定する。
                    targetBmsFiles = ((bmsFiles != null) ? bmsFiles.Where((BMSFile bmsInfo) => bmsInfo != null).ToList() : new List<BMSFile>());
                    if (targetBmsFiles.Count == 0 || targetBmsFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
                    {
                        return;
                    }
                    foreach (BMSFile targetBmsFile in targetBmsFiles)
                    {
                        targetBmsFile.status |= BMSFile.BMSFileStatus.SEARCHING;
                    }
                    // 【ステップ1】パッケージ内から推定の「基準」となる代表BMSファイルを1つ選出する。
                    // 既にハッシュ登録済みのファイルや、単体でWAVが十分に揃っている（差分ではなく本体の可能性が高い）ファイルは除外。
                    // WAVやBGA、その他参照画像（BackBMP等）の定義数（要求ファイル数）が多いBMSほど、
                    // スコア（Health）の計算基準が多くマッチング精度が高くなるため、優先的に代表として選定する。
                    BMSFile targetBMSInfo = (from bmsFile in targetBmsFiles
                                             where bmsFile != null && (isCorrectionLikeMode || !ContainsBMSHashUnsafe(bmsFile.hash))
                                             where isCorrectionLikeMode || bmsFile.maintenanceInfo == null || bmsFile.maintenanceInfo.GetWAVHealth() <= innerWavHealthThreshForNormalBMSFile
                                             select bmsFile).OrderByDescending(delegate (BMSFile bmsFile)
                                         {
                                             if (bmsFile.WAVfiles == null || bmsFile.BGAfiles == null)
                                             {
                                                 bmsFile.SetHealthStatus(null, forceUpdate: true, memClear: false);
                                             }
                                             return ((bmsFile.WAVfiles != null) ? bmsFile.WAVfiles.Count() : 0) + ((bmsFile.BGAfiles != null) ? bmsFile.BGAfiles.Count() : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.backbmp)) ? 1 : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.banner)) ? 1 : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.stagefile)) ? 1 : 0);
                                         }).FirstOrDefault();
                    if (targetBMSInfo == null)
                    {
                        return;
                    }
                    string targetDir = Path.GetDirectoryName(targetBMSInfo.path);
                    uint[] curDirFileNameHash = (isCorrectionLikeMode ? null : BMSDirectoryFileNameHash.GetFileNameHashArray(targetDir));
                    List<string> allCandidateDirs = bmsFolderAllFileList.Keys.Where((string dir) => !dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (allCandidateDirs.Count == 0)
                    {
                        return;
                    }

                    // 高速化のための事前候補絞り込み用ハッシュを作る。
                    // 回帰防止のため、相対パス付き参照が含まれる場合は従来どおり全候補評価にフォールバックする。
                    HashSet<uint> targetFileHashes = new HashSet<uint>();
                    bool hasNonLocalReference = false;
                    Action<string, IEnumerable<string>> addTargetFileHashes = delegate (string fileName, IEnumerable<string> fallbackExtensions)
                    {
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            return;
                        }
                        string text = fileName.Replace('/', Path.DirectorySeparatorChar).Trim();
                        if (text.IndexOf(Path.DirectorySeparatorChar) >= 0 || text.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                        {
                            hasNonLocalReference = true;
                        }
                        text = text.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string fileName2;
                        try
                        {
                            fileName2 = Path.GetFileName(text);
                        }
                        catch
                        {
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(fileName2))
                        {
                            return;
                        }
                        targetFileHashes.Add(BMSDirectoryFileNameHash.GetFileNameHash(fileName2));
                        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName2)) && fallbackExtensions != null)
                        {
                            foreach (string extension in fallbackExtensions)
                            {
                                if (!string.IsNullOrWhiteSpace(extension))
                                {
                                    targetFileHashes.Add(BMSDirectoryFileNameHash.GetFileNameHash(fileName2 + extension));
                                }
                            }
                        }
                    };
                    if (targetBMSInfo.WAVfiles != null)
                    {
                        foreach (string wAVfile in targetBMSInfo.WAVfiles)
                        {
                            addTargetFileHashes(wAVfile, BMSFile.wavExtensions);
                        }
                    }
                    if (targetBMSInfo.BGAfiles != null)
                    {
                        foreach (string bGAfile in targetBMSInfo.BGAfiles)
                        {
                            addTargetFileHashes(bGAfile, BMSFile.bgaAllExtensions);
                        }
                    }
                    addTargetFileHashes(targetBMSInfo.backbmp, BMSFile.bgaImageExtensions);
                    addTargetFileHashes(targetBMSInfo.banner, BMSFile.bgaImageExtensions);
                    addTargetFileHashes(targetBMSInfo.stagefile, BMSFile.bgaImageExtensions);

                    // 安全策:
                    // 1) 非local参照（サブフォルダ指定）がある場合は事前フィルタを使わない
                    // 2) 事前フィルタ結果が0件なら従来同等の全候補評価に戻す
                    // これにより、推定精度の回帰を防ぎつつ、絞り込み可能なケースだけ高速化を適用する。
                    List<string> candidateDirList;
                    if (!hasNonLocalReference && targetFileHashes.Count > 0)
                    {
                        ParallelQuery<string> filterSource = (asParallel ? allCandidateDirs.AsParallel() : allCandidateDirs.AsParallel().WithDegreeOfParallelism(1));
                        candidateDirList = filterSource.Where(delegate (string dir)
                        {
                            uint[] fileNameHashArray = bmsFolderAllFileList.GetFileNameHashArray(dir);
                            if (fileNameHashArray == null || fileNameHashArray.Length == 0)
                            {
                                return false;
                            }
                            for (int i = 0; i < fileNameHashArray.Length; i++)
                            {
                                if (targetFileHashes.Contains(fileNameHashArray[i]))
                                {
                                    return true;
                                }
                            }
                            return false;
                        }).ToList();
                        // 絞り込み候補が空なら従来どおり全フォルダ評価へ戻し、推定不能化の回帰を防ぐ。
                        if (candidateDirList.Count == 0)
                        {
                            candidateDirList = allCandidateDirs;
                        }
                    }
                    else
                    {
                        // 非local参照を含む場合は従来同等の探索にフォールバックする。
                        candidateDirList = allCandidateDirs;
                    }
                    ParallelQuery<string> sourceNew = (asParallel ? candidateDirList.AsParallel() : candidateDirList.AsParallel().WithDegreeOfParallelism(1));


                    // 【ステップ4】事前フィルタリングされたBMSフォルダ候補群に対して、仮想配置シミュレーションを実施する。
                    // 1. 各候補フォルダ (altdir) に代表BMS (targetBMSInfo) を置いたと仮定し、SetHealthStatus で依存ファイルの充足度（健康度）を算出する。
                    // 2. 結果としてWAVの健康度が最低閾値（本体判定）を上回り、かつ現状の配置よりも改善する（または同等以上の）フォルダのみをリストアップする。
                    // 3. 最後に評価軸（WAV健康度 -> BGA健康度 -> Movie -> OptIMG）の順に降順ソートし、最も状態が良くなるフォルダを特定する。
                    //    同率の場合は、そのフォルダに存在するWAVファイルの絶対総数が多い方を優先する（音源が豊富なディレクトリを正解としやすいヒューリスティック）。
                    List<BMSFileMaintenanceInfo> source2 = (from m in sourceNew.Select(delegate (string altdir)
                        {
                            BMSFileMaintenanceInfo bMSFileMaintenanceInfo = new BMSFileMaintenanceInfo(targetBMSInfo)
                            {
                                path = Path.Combine(altdir, Path.GetFileName(targetBMSInfo.path))
                            };
                            targetBMSInfo.SetHealthStatus(bmsFolderAllFileList, forceUpdate: false, memClear: false, bMSFileMaintenanceInfo, altdir, curDirFileNameHash);
                            return bMSFileMaintenanceInfo;
                        })
                                                            where m.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile && (!isFixMode || m.GetBGAHealth() >= targetBMSInfo.maintenanceInfo.GetBGAHealth())
                                                            select m).ToList();
                    if (isMergeMode)
                    {
                        // マージ推定では「元フォルダへ留まる」判定を避けるため、比較対象に元フォルダ自身は含めない。
                        // （高ヘルス同梱パッケージで未解決になる要因を除去）
                        source2 = source2.Where((BMSFileMaintenanceInfo m) => !string.Equals(DirectoryExt.GetDirectoryNameSimple(m.path), targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                    else
                    {
                        source2 = source2.Concat(new BMSFileMaintenanceInfo[1] { targetBMSInfo.maintenanceInfo }).Distinct().ToList();
                    }
                    if (source2.Count > 0)
                    {
                        List<BMSFileMaintenanceInfo> list = (from m in source2
                                                             orderby m.GetWAVHealth() descending, m.GetBGAHealth() descending, m.GetMovieHealth() descending, m.GetOptIMGHealth() descending
                                                             select m).ToList();
                        int? wAVHealth = list[0].GetWAVHealth();
                        int? bGAHealth = list[0].GetBGAHealth();
                        int? movieHealth = list[0].GetMovieHealth();
                        bool? optIMGHealth = list[0].GetOptIMGHealth();
                        List<BMSFileMaintenanceInfo> list2 = list.Where((BMSFileMaintenanceInfo m) => m.GetWAVHealth() == wAVHealth && m.GetBGAHealth() == bGAHealth && m.GetMovieHealth() == movieHealth && m.GetOptIMGHealth() == optIMGHealth).ToList();
                        // 同率候補に対してのみ追加I/O（ディレクトリ内WAV総数）を実施し、旧来ヒューリスティックを維持したまま負荷を下げる。
                        Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        Func<string, int> func = delegate (string dir)
                        {
                            if (dictionary.TryGetValue(dir, out var value))
                            {
                                return value;
                            }
                            int num = 0;
                            try
                            {
                                num = FastDirectoryEnumerator.GetFileNames(dir).Count((string file) => BMSFile.wavExtensions.Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                            }
                            catch
                            {
                            }
                            dictionary[dir] = num;
                            return num;
                        };
                        int num2 = list2.Max((BMSFileMaintenanceInfo m) => func(DirectoryExt.GetDirectoryNameSimple(m.path)));
                        BMSFileMaintenanceInfo candidate = list2.Where((BMSFileMaintenanceInfo m) => func(DirectoryExt.GetDirectoryNameSimple(m.path)) == num2)
                            .OrderByDescending((BMSFileMaintenanceInfo m) => (m == targetBMSInfo.maintenanceInfo) ? 1 : 0)
                            .First();
                        if (candidate == targetBMSInfo.maintenanceInfo)
                        {
                            return;
                        }
                        try
                        {
                            // candidate.path 自体が推定先ディレクトリ配下の仮想パスなので、全体走査せず直接取り出す。
                            string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(candidate.path);
                            targetBMSInfo.instl_dst = directoryNameSimple;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(targetBMSInfo.instl_dst))
                    {
                        targetBmsFiles.ForEach(delegate (BMSFile bmsFile)
                        {
                            bmsFile.instl_dst = targetBMSInfo.instl_dst;
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

    private void ApplyBatchDuplicateWarnings(IEnumerable<BMSFile> files)
    {
        if (files == null)
        {
            return;
        }
        foreach (BMSFile file in files)
        {
            if (file != null)
            {
                file.warning = Resources.Warning_AlreadyInstalled;
            }
        }
    }

    private bool TryAddBatchReservation(HashSet<string> reservedHashes, BMSFile file)
    {
        if (reservedHashes == null || file == null || !IsBMSHashAvailable(file.hash))
        {
            return false;
        }
        return reservedHashes.Add(file.hash);
    }

    private Dictionary<string, List<string>> BuildInstalledHashToDirectoryMap()
    {
        Dictionary<string, HashSet<string>> dictionary = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in BMSFiles ?? new List<BMSFile>())
        {
            if (item == null || !IsBMSHashAvailable(item.hash))
            {
                continue;
            }
            string text = null;
            try
            {
                text = DirectoryExt.GetDirectoryNameSimple(item.path);
            }
            catch
            {
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (!dictionary.TryGetValue(item.hash, out var value))
            {
                value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dictionary[item.hash] = value;
            }
            value.Add(text);
        }
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<string>> x) => x.Key, (KeyValuePair<string, HashSet<string>> x) => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private bool TryResolveInstalledDestinationFromPackage(BMSPackage package, List<BMSFile> missingFiles, out string resolvedDir)
    {
        resolvedDir = null;
        if (package == null || missingFiles == null || missingFiles.Count == 0)
        {
            return false;
        }
        Dictionary<string, List<string>> dictionary = BuildInstalledHashToDirectoryMap();
        if (dictionary.Count == 0)
        {
            LogInstallPerformance("mixed_package_resolve fallback reason=installed_index_empty");
            return false;
        }
        Dictionary<string, int> dictionary2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int num = 0;
        foreach (BMSFile item in package.BMSFiles ?? new List<BMSFile>())
        {
            if (item == null || !IsBMSHashAvailable(item.hash) || !dictionary.TryGetValue(item.hash, out var value))
            {
                continue;
            }
            num++;
            foreach (string item2 in value)
            {
                if (!dictionary2.ContainsKey(item2))
                {
                    dictionary2[item2] = 0;
                }
                dictionary2[item2]++;
            }
        }
        LogInstallPerformance("mixed_package_resolve start package=" + package.path + " missing=" + missingFiles.Count + " installedMatched=" + num + " candidateDirs=" + dictionary2.Count);
        if (dictionary2.Count == 0)
        {
            LogInstallPerformance("mixed_package_resolve fallback reason=no_installed_dir_match");
            return false;
        }
        int num2 = dictionary2.Values.Max();
        List<string> list = dictionary2.Where((KeyValuePair<string, int> kv) => kv.Value == num2).Select((KeyValuePair<string, int> kv) => kv.Key).ToList();
        if (list.Count == 1)
        {
            resolvedDir = list[0];
            LogInstallPerformance("mixed_package_resolve selected dst=" + resolvedDir + " matched=" + num2 + " tieCandidates=1");
            return true;
        }
        // 同数候補は従来の健康度評価軸（WAV/BGA/Movie/OptIMG）で比較し、推定の妥当性を維持する。
        BMSFile bMSFile = missingFiles.Where((BMSFile f) => f != null).OrderByDescending(delegate (BMSFile bmsFile)
        {
            if (bmsFile.WAVfiles == null || bmsFile.BGAfiles == null)
            {
                bmsFile.SetHealthStatus(null, forceUpdate: true, memClear: false);
            }
            return ((bmsFile.WAVfiles != null) ? bmsFile.WAVfiles.Count() : 0) + ((bmsFile.BGAfiles != null) ? bmsFile.BGAfiles.Count() : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.backbmp)) ? 1 : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.banner)) ? 1 : 0) + ((!string.IsNullOrWhiteSpace(bmsFile.stagefile)) ? 1 : 0);
        }).FirstOrDefault();
        if (bMSFile == null)
        {
            LogInstallPerformance("mixed_package_resolve fallback reason=missing_representative_not_found");
            return false;
        }
        List<BMSFileMaintenanceInfo> list2 = new List<BMSFileMaintenanceInfo>();
        foreach (string item3 in list)
        {
            BMSFileMaintenanceInfo bMSFileMaintenanceInfo = new BMSFileMaintenanceInfo(bMSFile)
            {
                path = Path.Combine(item3, Path.GetFileName(bMSFile.path))
            };
            bMSFile.SetHealthStatus(bmsFolderAllFileList, forceUpdate: false, memClear: false, bMSFileMaintenanceInfo, item3, null);
            if (bMSFileMaintenanceInfo.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile)
            {
                list2.Add(bMSFileMaintenanceInfo);
            }
        }
        if (list2.Count == 0)
        {
            LogInstallPerformance("mixed_package_resolve fallback reason=tie_health_below_threshold tieCandidates=" + list.Count);
            return false;
        }
        List<BMSFileMaintenanceInfo> list3 = (from m in list2
                                              orderby m.GetWAVHealth() descending, m.GetBGAHealth() descending, m.GetMovieHealth() descending, m.GetOptIMGHealth() descending
                                              select m).ToList();
        int? wAVHealth = list3[0].GetWAVHealth();
        int? bGAHealth = list3[0].GetBGAHealth();
        int? movieHealth = list3[0].GetMovieHealth();
        bool? optIMGHealth = list3[0].GetOptIMGHealth();
        string text = list3.Where((BMSFileMaintenanceInfo m) => m.GetWAVHealth() == wAVHealth && m.GetBGAHealth() == bGAHealth && m.GetMovieHealth() == movieHealth && m.GetOptIMGHealth() == optIMGHealth)
            .Select((BMSFileMaintenanceInfo m) => DirectoryExt.GetDirectoryNameSimple(m.path))
            .Where((string d) => !string.IsNullOrWhiteSpace(d))
            .OrderBy((string d) => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            LogInstallPerformance("mixed_package_resolve fallback reason=tie_break_unresolved tieCandidates=" + list.Count);
            return false;
        }
        resolvedDir = text;
        LogInstallPerformance("mixed_package_resolve selected dst=" + resolvedDir + " matched=" + num2 + " tieCandidates=" + list.Count);
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
        searchEstimatedInstallationDirectory(list, asParallel: true, InstallationEstimateMode.MergeNoSourceCompensation);
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
            searchEstimatedInstallationDirectory(new BMSFile[1] { item }, asParallel: true, InstallationEstimateMode.MergeNoSourceCompensation);
        }
        int num = list.Count((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved targets=" + list.Count);
            return;
        }
        LogInstallPerformance("estimated_merge_done resolved=" + num + " targets=" + list.Count);
    }

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
                        List<BMSPackage> list = new List<BMSPackage>();
                        HashSet<BMSPackage> hashSet = new HashSet<BMSPackage>();
                        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (BMSPackage item in packages.Where((BMSPackage pkg) => pkg != null))
                        {
                            if (!string.IsNullOrWhiteSpace(item.path))
                            {
                                if (!hashSet2.Add(item.path))
                                {
                                    continue;
                                }
                            }
                            else if (!hashSet.Add(item))
                            {
                                continue;
                            }
                            list.Add(item);
                        }
                        if (list.Count == 0)
                        {
                            return;
                        }
                        List<BMSPackage> list2 = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).ToList();
                        List<BMSPackage> list3 = new List<BMSPackage>();
                        List<BMSPackage> deferredInstalledPackages = new List<BMSPackage>();
                        int num = 0;
                        int num2 = 0;
                        int num3 = 0;
                        int num4 = 0;
                        NLogWrapper.FileLogger?.Info("force_install_batch start requested=" + list.Count);
                        foreach (BMSPackage requestedPackage in list)
                        {
                            BMSPackage bMSPackage = list2.FirstOrDefault((BMSPackage pkg) => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
                            if (bMSPackage == null)
                            {
                                num4++;
                                NLogWrapper.FileLogger?.Info("force_install_batch skip_not_pending path=" + (requestedPackage.path ?? "(null)"));
                                continue;
                            }
                            List<BMSFile> list4 = bMSPackage.BMSFiles.Where((BMSFile f) => f != null).ToList();
                            if (list4.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)) && MessageBox.Show(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.No)
                            {
                                num4++;
                                NLogWrapper.FileLogger?.Info("force_install_batch skipped_by_confirm path=" + (bMSPackage.path ?? "(null)"));
                                continue;
                            }
                            if (installBMSPackages(new BMSPackage[1] { bMSPackage }, null, null, deferredInstalledPackages).Count() == 0)
                            {
                                list3.Add(bMSPackage);
                                num2++;
                                list4.ForEach(delegate (BMSFile bmsFile)
                                {
                                    bmsFile.instl_dst = null;
                                });
                                NLogWrapper.FileLogger?.Info("force_install_batch success path=" + (bMSPackage.path ?? "(null)"));
                            }
                            else
                            {
                                num3++;
                                NLogWrapper.FileLogger?.Info("force_install_batch failed path=" + (bMSPackage.path ?? "(null)"));
                            }
                            num++;
                        }
                        if (list3.Count > 0)
                        {
                            ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: list3));
                        }
                        int num5 = 0;
                        if (deferredInstalledPackages.Count > 0)
                        {
                            HashSet<BMSPackage> hashSet3 = new HashSet<BMSPackage>(BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null));
                            List<BMSPackage> list5 = BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null).ToList();
                            foreach (BMSPackage deferredInstalledPackage in deferredInstalledPackages)
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
                        NLogWrapper.FileLogger?.Info("force_install_batch summary requested=" + list.Count + " processed=" + num + " succeeded=" + num2 + " failed=" + num3 + " skipped=" + num4 + " pendingRemoved=" + list3.Count + " installedAdded=" + num5);
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
                        List<BMSPackage> pendingPackagesSnapshot = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).ToList();
                        HashSet<BMSPackage> pendingPackageSet = new HashSet<BMSPackage>(pendingPackagesSnapshot);
                        Stopwatch filterStopwatch = Stopwatch.StartNew();
                        List<BMSPackage> selectedPendingPackages = packages.Where((BMSPackage pkg) => pkg != null && pendingPackageSet.Contains(pkg)).Distinct().ToList();
                        filterStopwatch.Stop();
                        if (selectedPendingPackages.Count == 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir skipped reason=no_pending_target filterMs=" + filterStopwatch.ElapsedMilliseconds + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        Stopwatch groupBuildStopwatch = Stopwatch.StartNew();
                        // 実パッケージ全体ではなく「今回導入する未所持譜面集合」を単位にグルーピングする。
                        Dictionary<string, List<BMSPackage>> packagesByDestination = new Dictionary<string, List<BMSPackage>>(StringComparer.OrdinalIgnoreCase);
                        Dictionary<BMSPackage, BMSPackage> installWorkPackageByOriginal = new Dictionary<BMSPackage, BMSPackage>();
                        Dictionary<BMSPackage, BMSPackage> originalPackageByInstallWorkPackage = new Dictionary<BMSPackage, BMSPackage>();
                        Dictionary<BMSPackage, HashSet<string>> excludedComponentPathsByWorkPackage = new Dictionary<BMSPackage, HashSet<string>>();
                        HashSet<BMSPackage> resourceOnlyOriginalPackages = new HashSet<BMSPackage>();
                        // 判定用は「既存 + バッチ内予約済み」を持つ。
                        // 実移動ガード用は「既存 + 移動成功分のみ」を持ち、自己スキップを防ぐ。
                        HashSet<string> moveGuardHashes = CreateBMSHashSnapshotExcludingUnsafe(null);
                        HashSet<string> reservedHashesForBatch = new HashSet<string>(moveGuardHashes, StringComparer.OrdinalIgnoreCase);
                        int installTargetFileCount = 0;
                        List<BMSPackage> cleanupOnlyCandidates = new List<BMSPackage>();
                        foreach (BMSPackage originalPackage in selectedPendingPackages)
                        {
                            List<BMSFile> packageFiles = (originalPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile bmsInfo) => bmsInfo != null).ToList();
                            if (packageFiles.Count == 0)
                            {
                                continue;
                            }
                            List<BMSFile> installedInLibraryFiles = new List<BMSFile>();
                            List<BMSFile> installTargetPackageFiles = new List<BMSFile>();
                            List<BMSFile> duplicateInBatchFiles = new List<BMSFile>();
                            foreach (BMSFile bmsInfo in packageFiles)
                            {
                                if (!IsBMSHashAvailable(bmsInfo.hash))
                                {
                                    installTargetPackageFiles.Add(bmsInfo);
                                }
                                else if (ContainsBMSHashUnsafe(bmsInfo.hash))
                                {
                                    installedInLibraryFiles.Add(bmsInfo);
                                }
                                else if (!TryAddBatchReservation(reservedHashesForBatch, bmsInfo))
                                {
                                    duplicateInBatchFiles.Add(bmsInfo);
                                }
                                else
                                {
                                    installTargetPackageFiles.Add(bmsInfo);
                                }
                            }
                            ApplyPackageMixedInstallWarnings(installedInLibraryFiles);
                            ApplyBatchDuplicateWarnings(duplicateInBatchFiles);
                            if (duplicateInBatchFiles.Count > 0)
                            {
                                LogInstallPerformance("estimated_install_batch_duplicate package=" + originalPackage.path + " duplicates=" + duplicateInBatchFiles.Count);
                            }
                            List<BMSFile> installWorkPackageFiles = installTargetPackageFiles;
                            bool isResourceOnlyInstall = false;
                            string destinationDirectory = null;
                            if (installTargetPackageFiles.Count == 0)
                            {
                                if (installedInLibraryFiles.Count == 0)
                                {
                                    continue;
                                }
                                List<string> distinctDestinationDirectories = packageFiles.Select((BMSFile bmsInfo) => bmsInfo.instl_dst).Where((string dst) => !string.IsNullOrWhiteSpace(dst)).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                if (distinctDestinationDirectories.Count == 0)
                                {
                                    LogInstallPerformance("estimated_install_skip reason=missing_instl_dst package=" + originalPackage.path + " installTargets=0 resourceOnly=true");
                                    continue;
                                }
                                if (distinctDestinationDirectories.Count > 1)
                                {
                                    LogInstallPerformance("estimated_install_skip reason=multi_dst package=" + originalPackage.path + " installTargets=0 resourceOnly=true");
                                    continue;
                                }
                                destinationDirectory = distinctDestinationDirectories[0];
                                isResourceOnlyInstall = true;
                                installWorkPackageFiles = packageFiles;
                            }
                            else
                            {
                                if (installTargetPackageFiles.Any((BMSFile bmsInfo) => string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
                                {
                                    LogInstallPerformance("estimated_install_skip reason=missing_instl_dst package=" + originalPackage.path + " installTargets=" + installTargetPackageFiles.Count + " resourceOnly=false");
                                    continue;
                                }
                                destinationDirectory = installTargetPackageFiles.Select((BMSFile bmsInfo) => bmsInfo.instl_dst).FirstOrDefault();
                                if (string.IsNullOrWhiteSpace(destinationDirectory) || installTargetPackageFiles.Any((BMSFile bmsInfo) => !string.Equals(bmsInfo.instl_dst, destinationDirectory, StringComparison.OrdinalIgnoreCase)))
                                {
                                    LogInstallPerformance("estimated_install_skip reason=multi_dst package=" + originalPackage.path + " installTargets=" + installTargetPackageFiles.Count + " resourceOnly=false");
                                    continue;
                                }
                            }
                            // install用ワークパッケージ: 未所持譜面のみ保持し、既所持/重複譜面は移動対象から除外する。
                            BMSPackage installWorkPackage = new BMSPackage(installWorkPackageFiles)
                            {
                                path = originalPackage.path,
                                delete_parent = originalPackage.delete_parent
                            };
                            installWorkPackageByOriginal[originalPackage] = installWorkPackage;
                            originalPackageByInstallWorkPackage[installWorkPackage] = originalPackage;
                            if (installedInLibraryFiles.Count > 0 || duplicateInBatchFiles.Count > 0 || isResourceOnlyInstall)
                            {
                                HashSet<string> excludedPaths = new HashSet<string>(installedInLibraryFiles.Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
                                foreach (BMSFile duplicateFile in duplicateInBatchFiles)
                                {
                                    if (!string.IsNullOrWhiteSpace(duplicateFile?.path))
                                    {
                                        excludedPaths.Add(duplicateFile.path);
                                    }
                                }
                                if (isResourceOnlyInstall)
                                {
                                    foreach (BMSFile packageFile in packageFiles)
                                    {
                                        if (!string.IsNullOrWhiteSpace(packageFile?.path))
                                        {
                                            excludedPaths.Add(packageFile.path);
                                        }
                                    }
                                }
                                if (excludedPaths.Count > 0)
                                {
                                    excludedComponentPathsByWorkPackage[installWorkPackage] = excludedPaths;
                                }
                            }
                            if (isResourceOnlyInstall)
                            {
                                int componentMoveTargetCount = 0;
                                if (excludedComponentPathsByWorkPackage.TryGetValue(installWorkPackage, out var excludedPathsForWorkPackage))
                                {
                                    componentMoveTargetCount = CountComponentMoveTargetsForPackage(installWorkPackage, destinationDirectory, excludedPathsForWorkPackage);
                                }
                                else
                                {
                                    componentMoveTargetCount = CountComponentMoveTargetsForPackage(installWorkPackage, destinationDirectory, null);
                                }
                                if (componentMoveTargetCount == 0)
                                {
                                    if (deletePendingPackageSourceAfterInstall)
                                    {
                                        cleanupOnlyCandidates.Add(originalPackage);
                                        LogInstallPerformance("estimated_install_cleanup_only_candidate package=" + originalPackage.path + " installTargets=0 resourceOnly=true");
                                    }
                                    else
                                    {
                                        LogInstallPerformance("estimated_install_skip reason=no_component_target package=" + originalPackage.path + " installTargets=0 resourceOnly=true");
                                    }
                                    installWorkPackageByOriginal.Remove(originalPackage);
                                    originalPackageByInstallWorkPackage.Remove(installWorkPackage);
                                    excludedComponentPathsByWorkPackage.Remove(installWorkPackage);
                                    continue;
                                }
                                resourceOnlyOriginalPackages.Add(originalPackage);
                            }
                            if (!packagesByDestination.TryGetValue(destinationDirectory, out var packagesInDestination))
                            {
                                packagesInDestination = new List<BMSPackage>();
                                packagesByDestination[destinationDirectory] = packagesInDestination;
                            }
                            packagesInDestination.Add(originalPackage);
                            installTargetFileCount += installTargetPackageFiles.Count;
                            LogInstallPerformance("estimated_install_targets package=" + originalPackage.path + " total=" + packageFiles.Count + " installTargets=" + installTargetPackageFiles.Count + " installed=" + installedInLibraryFiles.Count + " dst=" + destinationDirectory + " resourceOnly=" + isResourceOnlyInstall);
                        }
                        groupBuildStopwatch.Stop();
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir start selected=" + selectedPendingPackages.Count + " groups=" + packagesByDestination.Count + " installTargets=" + installTargetFileCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + filterStopwatch.ElapsedMilliseconds + " groupBuildMs=" + groupBuildStopwatch.ElapsedMilliseconds);
                        List<BMSFile> deferredMaintenanceTargets = new List<BMSFile>();
                        List<BMSPackage> deferredInstalledPackages = new List<BMSPackage>();
                        HashSet<BMSPackage> pendingPackagesToRemove = new HashSet<BMSPackage>();
                        int cleanupOnlySucceeded = 0;
                        int cleanupOnlyFailed = 0;
                        int cleanupOnlyMissingSource = 0;
                        foreach (var groupEntry in packagesByDestination)
                        {
                            Stopwatch groupStopwatch = Stopwatch.StartNew();
                            List<BMSPackage> destinationPackages = groupEntry.Value;
                            List<BMSPackage> installWorkPackages = destinationPackages.Where((BMSPackage pkg) => installWorkPackageByOriginal.ContainsKey(pkg)).Select((BMSPackage pkg) => installWorkPackageByOriginal[pkg]).ToList();
                            Stopwatch installStopwatch = Stopwatch.StartNew();
                            // 既所持BMSの元パスを除外して、コンポーネント移動に巻き込まないようにする。
                            List<BMSPackage> failedInstallWorkPackages = installBMSPackages(installWorkPackages, groupEntry.Key, deferredMaintenanceTargets, deferredInstalledPackages, excludedComponentPathsByWorkPackage, moveGuardHashes, skipInstalledPackageWhenNoBms: true, deleteSourceContentsAfterSuccessfulInstall: deletePendingPackageSourceAfterInstall);
                            installStopwatch.Stop();
                            HashSet<BMSPackage> failedOriginalPackages = new HashSet<BMSPackage>(failedInstallWorkPackages.Select((BMSPackage workPkg) => originalPackageByInstallWorkPackage[workPkg]));
                            Stopwatch installDbStopwatch = Stopwatch.StartNew();
                            using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                            {
                                lR2SongDBExtended.BeginTransaction();
                                foreach (BMSPackage originalPackageInGroup in destinationPackages)
                                {
                                    if (!failedOriginalPackages.Contains(originalPackageInGroup))
                                    {
                                        lR2SongDBExtended.Delete<LR2SongDBExtended.install>(originalPackageInGroup.path);
                                        if (resourceOnlyOriginalPackages.Contains(originalPackageInGroup))
                                        {
                                            BMSPackage bMSPackage = CreateInstalledDisplayPackageForResourceOnlyMerge(originalPackageInGroup, groupEntry.Key);
                                            if (bMSPackage != null && bMSPackage.BMSFiles != null && bMSPackage.BMSFiles.Count > 0)
                                            {
                                                deferredInstalledPackages.Add(bMSPackage);
                                            }
                                        }
                                        foreach (BMSFile packageFile in originalPackageInGroup.BMSFiles)
                                        {
                                            packageFile.instl_dst = null;
                                        }
                                    }
                                }
                                lR2SongDBExtended.Commit();
                            }
                            installDbStopwatch.Stop();
                            Stopwatch pendingMarkStopwatch = Stopwatch.StartNew();
                            int pendingCountBeforeRemove = BMSPackagesPending.Count;
                            int bmsFilesCountBeforeRemove = ((BMSFiles != null) ? BMSFiles.Count : (-1));
                            int removedPendingCount = 0;
                            foreach (BMSPackage pendingPackage in destinationPackages)
                            {
                                if (pendingPackagesToRemove.Add(pendingPackage))
                                {
                                    removedPendingCount++;
                                }
                            }
                            int pendingCountAfterRemove = BMSPackagesPending.Count;
                            int bmsFilesCountAfterRemove = ((BMSFiles != null) ? BMSFiles.Count : (-1));
                            pendingMarkStopwatch.Stop();
                            groupStopwatch.Stop();
                            LogInstallPerformance("InstallBMSPackagesToEstimatedDir group dst=" + groupEntry.Key + " packages=" + destinationPackages.Count + " workPackages=" + installWorkPackages.Count + " failedPackages=" + failedInstallWorkPackages.Count + " installMs=" + installStopwatch.ElapsedMilliseconds + " installDbMs=" + installDbStopwatch.ElapsedMilliseconds + " pendingMarkMs=" + pendingMarkStopwatch.ElapsedMilliseconds + " pendingBefore=" + pendingCountBeforeRemove + " pendingMarked=" + removedPendingCount + " pendingAfter=" + pendingCountAfterRemove + " bmsFilesBefore=" + bmsFilesCountBeforeRemove + " bmsFilesAfter=" + bmsFilesCountAfterRemove + " totalGroupMs=" + groupStopwatch.ElapsedMilliseconds);
                        }
                        if (deletePendingPackageSourceAfterInstall && cleanupOnlyCandidates.Count > 0)
                        {
                            List<string> cleanupOnlyInstallPathsToDelete = new List<string>();
                            foreach (BMSPackage cleanupOnlyPackage in cleanupOnlyCandidates.Distinct())
                            {
                                if (cleanupOnlyPackage == null)
                                {
                                    continue;
                                }
                                if (TryCleanupPendingPackageSourceForEstimatedInstall(cleanupOnlyPackage, out var sourceKind))
                                {
                                    cleanupOnlySucceeded++;
                                    if (sourceKind == CleanupSourceKind.MissingSource)
                                    {
                                        cleanupOnlyMissingSource++;
                                    }
                                    if (!string.IsNullOrWhiteSpace(cleanupOnlyPackage.path))
                                    {
                                        cleanupOnlyInstallPathsToDelete.Add(cleanupOnlyPackage.path);
                                    }
                                    pendingPackagesToRemove.Add(cleanupOnlyPackage);
                                    foreach (BMSFile packageFile in cleanupOnlyPackage.BMSFiles ?? Enumerable.Empty<BMSFile>())
                                    {
                                        packageFile.instl_dst = null;
                                    }
                                    LogInstallPerformance("estimated_install_cleanup_only_success package=" + cleanupOnlyPackage.path + " kind=" + sourceKind.ToString().ToLowerInvariant());
                                }
                                else
                                {
                                    cleanupOnlyFailed++;
                                    LogInstallPerformance("estimated_install_cleanup_only_failed package=" + cleanupOnlyPackage.path);
                                }
                            }
                            if (cleanupOnlyInstallPathsToDelete.Count > 0)
                            {
                                using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                                {
                                    lR2SongDBExtended.BeginTransaction();
                                    foreach (string cleanupPath in cleanupOnlyInstallPathsToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                                    {
                                        lR2SongDBExtended.Delete<LR2SongDBExtended.install>(cleanupPath);
                                    }
                                    lR2SongDBExtended.Commit();
                                }
                            }
                        }
                        Stopwatch pendingApplyStopwatch = Stopwatch.StartNew();
                        int pendingCountBeforeApply = BMSPackagesPending.Count;
                        int pendingRemovedTotal = pendingPackagesToRemove.Count;
                        if (pendingRemovedTotal > 0)
                        {
                            List<BMSPackage> remainingPending = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null && !pendingPackagesToRemove.Contains(pkg)).ToList();
                            BMSPackagesPending = new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>(remainingPending), DispatcherHelper.UIDispatcher);
                        }
                        int pendingCountAfterApply = BMSPackagesPending.Count;
                        pendingApplyStopwatch.Stop();
                        Stopwatch installedApplyStopwatch = Stopwatch.StartNew();
                        int installedCountBeforeApply = BMSPackagesInstalled.Count;
                        int installedAddedTotal = deferredInstalledPackages.Count;
                        if (installedAddedTotal > 0)
                        {
                            HashSet<BMSPackage> installedSet = new HashSet<BMSPackage>(BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null));
                            List<BMSPackage> mergedInstalled = BMSPackagesInstalled.Where((BMSPackage pkg) => pkg != null).ToList();
                            foreach (BMSPackage installedPackage in deferredInstalledPackages)
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
                        if (deferredMaintenanceTargets.Count > 0)
                        {
                            setMaintenanceInfo(deferredMaintenanceTargets, forceUpdate: true);
                        }
                        maintenanceStopwatch.Stop();
                        if (deletePendingPackageSourceAfterInstall && cleanupOnlySucceeded > 0)
                        {
                            DispatcherMessageBox.Show(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, cleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        totalStopwatch.Stop();
                        LogInstallPerformance("InstallBMSPackagesToEstimatedDir end pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingCountBeforeApply + " pendingRemovedTotal=" + pendingRemovedTotal + " pendingAfterApply=" + pendingCountAfterApply + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedCountBeforeApply + " installedAddedTotal=" + installedAddedTotal + " installedAfterApply=" + installedCountAfterApply + " maintenanceTargets=" + deferredMaintenanceTargets.Count + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + cleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + cleanupOnlySucceeded + " cleanupOnlyFailed=" + cleanupOnlyFailed + " cleanupOnlyMissingSource=" + cleanupOnlyMissingSource + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
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

    private sealed class PendingPackageMutationDelta
    {
        public bool HasChanges { get; set; }

        public List<BMSPackage> RemainingPackages { get; set; } = new List<BMSPackage>();

        public List<string> InstallPathsToDelete { get; set; } = new List<string>();
    }

    private PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<BMSPackage> packagesToRemove = null, IEnumerable<BMSFile> filesToRemove = null, bool clearAll = false)
    {
        List<BMSPackage> list = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).ToList();
        PendingPackageMutationDelta pendingPackageMutationDelta = new PendingPackageMutationDelta();
        if (clearAll)
        {
            pendingPackageMutationDelta.HasChanges = list.Count > 0;
            pendingPackageMutationDelta.InstallPathsToDelete = list.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return pendingPackageMutationDelta;
        }
        HashSet<BMSPackage> hashSet = new HashSet<BMSPackage>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null));
        HashSet<string> hashSet2 = new HashSet<string>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null && !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path), StringComparer.OrdinalIgnoreCase);
        List<BMSFile> list2 = (filesToRemove ?? Enumerable.Empty<BMSFile>()).Where((BMSFile f) => f != null).ToList();
        HashSet<string> removedPaths = new HashSet<string>(list2.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.path)).Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> removedFiles = new HashSet<BMSFile>(list2);
        bool flag = list2.Count > 0;
        HashSet<string> hashSet3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage item in list)
        {
            if (hashSet.Contains(item) || (!string.IsNullOrWhiteSpace(item.path) && hashSet2.Contains(item.path)))
            {
                pendingPackageMutationDelta.HasChanges = true;
                if (!string.IsNullOrWhiteSpace(item.path))
                {
                    hashSet3.Add(item.path);
                }
                continue;
            }
            if (!flag)
            {
                pendingPackageMutationDelta.RemainingPackages.Add(item);
                continue;
            }
            List<BMSFile> list3 = item.BMSFiles.Where((BMSFile f) => f != null).ToList();
            if (list3.Count == 0)
            {
                pendingPackageMutationDelta.RemainingPackages.Add(item);
                continue;
            }
            List<BMSFile> list4 = list3.Where((BMSFile f) => !IsMatchedRemovedFile(f, removedPaths, removedFiles)).ToList();
            if (list4.Count == list3.Count)
            {
                pendingPackageMutationDelta.RemainingPackages.Add(item);
                continue;
            }
            pendingPackageMutationDelta.HasChanges = true;
            if (list4.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(item.path))
                {
                    hashSet3.Add(item.path);
                }
                continue;
            }
            item.BMSFiles.Clear();
            item.BMSFiles.AddRange(list4);
            pendingPackageMutationDelta.RemainingPackages.Add(item);
        }
        pendingPackageMutationDelta.InstallPathsToDelete = hashSet3.ToList();
        return pendingPackageMutationDelta;
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
        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        lR2SongDBExtended.BeginTransaction();
        foreach (string item in list)
        {
            lR2SongDBExtended.Delete<LR2SongDBExtended.install>(item);
        }
        lR2SongDBExtended.Commit();
    }

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
                List<BMSPackage> list = BMSPackagesPending.Where((BMSPackage pkg) => IsPendingPackageContainingOnlyInstalledCharts(pkg)).ToList();
                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup scan pendingTotal=" + BMSPackagesPending.Count + " eligible=" + list.Count);
                return list;
            }
        }
    }

    public List<BMSFile> GetPendingBMSFilesSnapshot()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetReaderGuard())
            {
                List<BMSFile> list = new List<BMSFile>();
                HashSet<BMSFile> hashSet = new HashSet<BMSFile>();
                HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BMSFile item in BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).SelectMany((BMSPackage pkg) => pkg.BMSFiles).Where((BMSFile f) => f != null))
                {
                    if (!string.IsNullOrWhiteSpace(item.path))
                    {
                        if (!hashSet2.Add(item.path))
                        {
                            continue;
                        }
                    }
                    else if (!hashSet.Add(item))
                    {
                        continue;
                    }
                    list.Add(item);
                }
                return list;
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
                    List<BMSFile> list = new List<BMSFile>();
                    HashSet<BMSFile> hashSet = new HashSet<BMSFile>();
                    HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (BMSFile item in enumerable)
                    {
                        if (!string.IsNullOrWhiteSpace(item.path))
                        {
                            if (!hashSet2.Add(item.path))
                            {
                                continue;
                            }
                        }
                        else if (!hashSet.Add(item))
                        {
                            continue;
                        }
                        list.Add(item);
                    }
                    List<BMSFile> list2 = new List<BMSFile>();
                    int num = 0;
                    int num2 = 0;
                    int num3 = 0;
                    int num4 = 0;
                    int num5 = 0;
                    int num6 = 0;
                    bool flag = false;
                    NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename start total=" + list.Count);
                    foreach (BMSFile item2 in list)
                    {
                        if (token.IsCancellationRequested)
                        {
                            flag = true;
                            break;
                        }
                        string extension = Path.GetExtension(item2.path);
                        string text = null;
                        if (!string.IsNullOrWhiteSpace(extension))
                        {
                            if (extension.StartsWith(".b", StringComparison.OrdinalIgnoreCase))
                            {
                                text = ".bmx";
                            }
                            else if (extension.StartsWith(".p", StringComparison.OrdinalIgnoreCase))
                            {
                                text = ".pmx";
                            }
                        }
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            num6++;
                            num++;
                            NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename skip_unsupported_ext path=" + item2.path + " ext=" + extension);
                            onEachProcessed?.Invoke();
                            continue;
                        }
                        if (extension.Equals(text, StringComparison.OrdinalIgnoreCase))
                        {
                            num6++;
                            num++;
                            NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename skip_unsupported_ext path=" + item2.path + " ext=" + extension);
                            onEachProcessed?.Invoke();
                            continue;
                        }
                        bool flag2 = false;
                        try
                        {
                            flag2 = BMSFile.IsZeroNoteBMSFile(item2.path);
                        }
                        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                        {
                            num6++;
                            num++;
                            NLogWrapper.FileLogger?.Warn(ex, "advanced_pending_zero_note_rename zero_note_check_failed path=" + item2.path + " error=" + ex.Message);
                            onEachProcessed?.Invoke();
                            continue;
                        }
                        if (!flag2)
                        {
                            num6++;
                            num++;
                            NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename skip_not_zero path=" + item2.path);
                            onEachProcessed?.Invoke();
                            continue;
                        }
                        num2++;
                        NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename zero_note_detected path=" + item2.path + " targetExt=" + text);
                        string requestedPath = Path.Combine(Path.GetDirectoryName(item2.path), Path.GetFileNameWithoutExtension(item2.path) + text);
                        RenameInvalidExtensionOutcome renameInvalidExtensionOutcome = ProcessInvalidExtensionRename(item2, requestedPath, removeFromLibraryOnSuccess: false);
                        switch (renameInvalidExtensionOutcome.Action)
                        {
                            case RenameInvalidExtensionAction.Renamed:
                                list2.Add(item2);
                                num3++;
                                break;
                            case RenameInvalidExtensionAction.DeletedAsDuplicate:
                                list2.Add(item2);
                                num4++;
                                break;
                            default:
                                num5++;
                                if (renameInvalidExtensionOutcome.FailureException != null)
                                {
                                    if (renameInvalidExtensionOutcome.FailedDuringDelete)
                                    {
                                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, item2.path, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    }
                                    else
                                    {
                                        DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileMoveFailed, item2.path, renameInvalidExtensionOutcome.FinalPath, GetDisplayedExceptionMessage(renameInvalidExtensionOutcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    }
                                }
                                break;
                        }
                        num++;
                        onEachProcessed?.Invoke();
                    }
                    RemovePendingFilesFromPendingPackagesAndInstallRows(list2);
                    NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename summary total=" + list.Count + " processed=" + num + " zeroNote=" + num2 + " renamed=" + num3 + " duplicateDeleted=" + num4 + " skipped=" + num6 + " failed=" + num5 + " canceled=" + flag);
                }
            }
        }
    }

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
                    List<BMSPackage> list = new List<BMSPackage>();
                    HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<BMSPackage> hashSet2 = new HashSet<BMSPackage>();
                    foreach (BMSPackage item in packages.Where((BMSPackage pkg) => pkg != null))
                    {
                        if (!string.IsNullOrWhiteSpace(item.path))
                        {
                            if (!hashSet.Add(item.path))
                            {
                                continue;
                            }
                        }
                        else if (!hashSet2.Add(item))
                        {
                            continue;
                        }
                        list.Add(item);
                    }
                    List<BMSPackage> list2 = BMSPackagesPending.Where((BMSPackage pkg) => pkg != null).ToList();
                    List<BMSPackage> list3 = new List<BMSPackage>();
                    int num = 0;
                    int num2 = 0;
                    int num3 = 0;
                    int num4 = 0;
                    bool flag = false;
                    bool flag2 = !sendToRecycleBin;
                    RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
                    NLogWrapper.FileLogger?.Info("advanced_pending_cleanup start requested=" + list.Count + " permanent=" + flag2);
                    foreach (BMSPackage requestedPackage in list)
                    {
                        if (token.IsCancellationRequested)
                        {
                            flag = true;
                            break;
                        }
                        BMSPackage bMSPackage = list2.FirstOrDefault((BMSPackage pkg) => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
                        if (bMSPackage == null)
                        {
                            num4++;
                            num++;
                            NLogWrapper.FileLogger?.Info("advanced_pending_cleanup skipped_not_pending path=" + requestedPackage.path);
                            onEachProcessed?.Invoke();
                            continue;
                        }
                        bool flag3 = Directory.Exists(bMSPackage.path);
                        bool flag4 = !flag3 && File.Exists(bMSPackage.path);
                        try
                        {
                            if (flag3)
                            {
                                if (sendToRecycleBin)
                                {
                                    fileMutationService.DeleteDirectoryShell(bMSPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                                }
                                else
                                {
                                    fileMutationService.DeleteDirectoryDirect(bMSPackage.path, recursive: true, recursiveDirectoryTreeFileMutationOptions);
                                }
                                list3.Add(bMSPackage);
                                num2++;
                                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup deleted path=" + bMSPackage.path + " kind=directory");
                            }
                            else if (flag4)
                            {
                                if (sendToRecycleBin)
                                {
                                    fileMutationService.DeleteFileShell(bMSPackage.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                                }
                                else
                                {
                                    fileMutationService.DeleteFileDirect(bMSPackage.path, targetOnlyFileMutationOptions);
                                }
                                list3.Add(bMSPackage);
                                num2++;
                                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup deleted path=" + bMSPackage.path + " kind=file");
                            }
                            else
                            {
                                list3.Add(bMSPackage);
                                num2++;
                                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup missing_source_removed path=" + bMSPackage.path);
                            }
                        }
                        catch (Exception ex)
                        {
                            num3++;
                            NLogWrapper.FileLogger?.Warn(ex, "advanced_pending_cleanup failed path=" + bMSPackage.path + " kind=" + (flag3 ? "directory" : "file") + " error=" + GetDisplayedExceptionMessage(ex));
                            if (flag3)
                            {
                                DispatcherMessageBox.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, bMSPackage.path, GetDisplayedExceptionMessage(ex)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                            else
                            {
                                DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, bMSPackage.path, GetDisplayedExceptionMessage(ex)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                        num++;
                        onEachProcessed?.Invoke();
                    }
                    RemovePendingPackagesFromPendingListAndInstallRows(list3);
                    NLogWrapper.FileLogger?.Info("advanced_pending_cleanup summary requested=" + list.Count + " processed=" + num + " removed=" + num2 + " failed=" + num3 + " skipped=" + num4 + " canceled=" + flag);
                }
            }
        }
    }

    private void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<BMSPackage> packages)
    {
        ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packages));
    }

    public void SearchCorrectInstallationDirectory(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        bmsFiles.Where((BMSFile f) => f != null).AsParallel().ForAll(delegate (BMSFile bmsFile)
        {
            lock (bmsFile)
            {
                if (string.IsNullOrWhiteSpace(bmsFile.instl_dst))
                {
                    SearchEstimatedInstallationDirectory(bmsFile, asParallel: false, fixMode: true);
                    if (!string.IsNullOrWhiteSpace(bmsFile.instl_dst) && bmsFile.instl_dst.Equals(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), StringComparison.OrdinalIgnoreCase))
                    {
                        bmsFile.instl_dst = null;
                    }
                }
            }
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
                List<BMSFile> list = bmsFiles.Where((BMSFile f) => f != null && !string.IsNullOrWhiteSpace(f.instl_dst)).ToList();
                for (int num = 0; num < list.Count; num++)
                {
                    list[num].instl_dst = null;
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
                BMSPackage bMSPackage = BMSPackagesPending.FirstOrDefault((BMSPackage pkg) => pkg != null && pkg.BMSFiles.Any((BMSFile f) => f != null && (ReferenceEquals(f, bmsFile) || (!string.IsNullOrWhiteSpace(f.path) && !string.IsNullOrWhiteSpace(bmsFile.path) && f.path.Equals(bmsFile.path, StringComparison.OrdinalIgnoreCase)))));
                if (bMSPackage == null)
                {
                    DispatcherMessageBox.Show(Resources.Warn_PendingPackageNotFound, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    foreach (BMSFile item in bMSPackage.BMSFiles.Where((BMSFile f) => f != null))
                    {
                        item.instl_dst = null;
                    }
                    return true;
                }
                string text;
                string normalizedInput;
                try
                {
                    normalizedInput = Path.GetFullPath(destinationDirectory.Trim().Trim('"'));
                }
                catch (Exception ex)
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Warn_InvalidInstallPath, destinationDirectory, ex.Message), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                if (File.Exists(normalizedInput))
                {
                    text = DirectoryExt.GetDirectoryNameSimple(normalizedInput);
                }
                else
                {
                    text = normalizedInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                if (!Directory.Exists(text))
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Warn_InstallDirNotFound, text), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                HashSet<string> hashSet = new HashSet<string>(bmsFolderAllFileList.Keys, StringComparer.OrdinalIgnoreCase);
                if (!hashSet.Contains(text))
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Warn_InstallDirMustContainBms, text), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return false;
                }
                foreach (BMSFile item in bMSPackage.BMSFiles.Where((BMSFile f) => f != null))
                {
                    item.instl_dst = text;
                }
                return true;
            }
        }
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        if (!IsBMSHashAvailable(hash))
        {
            return false;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                string text = BMSFiles.Where((BMSFile f) => f != null && IsBMSHashAvailable(f.hash) && f.hash.Equals(hash, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.path))
                    .Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path))
                    .Where((string dir) => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }
                installDir = text;
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
        Dictionary<string, BMSTable[]> md5ToTablesMap = BuildMd5ToTablesMap(table, sourceEntries);
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
            addedSongRefs = ApplyReferenceMap(songFilesSnapshot, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
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
            addedPendingRefs = ApplyReferenceMap(pendingFilesSnapshot, md5ToTablesMap, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
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
        Dictionary<string, BMSTable[]> md5ToTablesMap = BuildMd5ToTablesMap(list);
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
                    addedSongRefs = ApplyReferenceMap(songFilesSnapshot, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
                }
                stopwatchApplySong.Stop();
                applySongMs = stopwatchApplySong.ElapsedMilliseconds;
                Stopwatch stopwatchApplyPending = Stopwatch.StartNew();
                List<BMSFile> pendingFilesSnapshot = SnapshotPendingFilesForPlaylistReferenceApply();
                if (pendingFilesSnapshot != null && pendingFilesSnapshot.Count > 0)
                {
                    addedPendingRefs = ApplyReferenceMap(pendingFilesSnapshot, md5ToTablesMap, out matchedPendingFiles, out pendingApplyStats, suppressFilePropertyChanged);
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
                        addedSongRefs = ApplyReferenceMap(files, md5ToTablesMap, out matchedSongFiles, out songApplyStats, suppressFilePropertyChanged);
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

    private Dictionary<string, BMSTable[]> BuildMd5ToTablesMap(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        Dictionary<string, HashSet<BMSTable>> dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        AddEntriesToMd5ToTablesMap(dictionary, table, entries);
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, BMSTable[]> BuildMd5ToTablesMap(IEnumerable<BMSTable> tables)
    {
        Dictionary<string, HashSet<BMSTable>> dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables)
        {
            List<BMSTableEntry> entries = null;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                entries = table.entries.ToList();
            }
            AddEntriesToMd5ToTablesMap(dictionary, table, entries);
        }
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private void AddEntriesToMd5ToTablesMap(Dictionary<string, HashSet<BMSTable>> dictionary, BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (table == null || entries == null)
        {
            return;
        }
        foreach (BMSTableEntry entry in entries)
        {
            if (entry != null && !entry.is_removed && !string.IsNullOrWhiteSpace(entry.md5))
            {
                if (!dictionary.TryGetValue(entry.md5, out HashSet<BMSTable> value))
                {
                    value = new HashSet<BMSTable>();
                    dictionary[entry.md5] = value;
                }
                value.Add(table);
            }
        }
    }

    private int ApplyReferenceMap(IEnumerable<BMSFile> files, Dictionary<string, BMSTable[]> md5ToTablesMap, out int matchedFiles, out PlaylistReferenceApplyStats applyStats, bool suppressFilePropertyChanged = false)
    {
        matchedFiles = 0;
        applyStats = default(PlaylistReferenceApplyStats);
        if (files == null || md5ToTablesMap == null || md5ToTablesMap.Count == 0)
        {
            return 0;
        }
        int addCalls = 0;
        int processed = 0;
        Stopwatch chunkStopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in files)
        {
            if (file != null && !string.IsNullOrWhiteSpace(file.hash) && md5ToTablesMap.TryGetValue(file.hash, out BMSTable[] value))
            {
                matchedFiles++;
                addCalls += file.AddRefTables(value, suppressFilePropertyChanged);
            }
            processed++;
            if (processed % playlistReferenceApplyChunkSize == 0)
            {
                chunkStopwatch.Stop();
                applyStats.Chunks++;
                if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
                {
                    applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
                }
                Thread.Sleep(0);
                applyStats.YieldCount++;
                chunkStopwatch.Restart();
            }
        }
        chunkStopwatch.Stop();
        if (processed % playlistReferenceApplyChunkSize != 0 || processed == 0)
        {
            applyStats.Chunks++;
            if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
            {
                applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
            }
        }
        return addCalls;
    }

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
            fileMutationService.MoveDirectory(srcDir, dstDir, overwrite: false, recursiveDirectoryTreeFileMutationOptions);
            foreach (string item in bmsFolderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                string newKey = item.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
                bmsFolderAllFileList.ReplaceDir(item, newKey);
            }
            foreach (BMSFile item2 in BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat(BMSFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst))))
            {
                if (!string.IsNullOrWhiteSpace(item2.instl_dst) && (item2.instl_dst + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    item2.instl_dst = item2.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
                }
            }
            foreach (BMSPackage item3 in BMSPackagesInstalled)
            {
                if ((item3.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    item3.path = item3.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
                }
            }
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
                    fileMutationService.MoveFile(bmsFile.path, dstPath, overwrite: false, targetOnlyFileMutationOptions);
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
        RenameInvalidExtensionOutcome renameInvalidExtensionOutcome = new RenameInvalidExtensionOutcome
        {
            Action = RenameInvalidExtensionAction.Skipped,
            FinalPath = requestedPath
        };
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path) || string.IsNullOrWhiteSpace(requestedPath) || !File.Exists(sourceFile.path))
        {
            return renameInvalidExtensionOutcome;
        }
        string text = requestedPath;
        if (Directory.Exists(requestedPath))
        {
            NLogWrapper.FileLogger?.Info("invalid_ext_rename collision_detected source=" + sourceFile.path + " requested=" + requestedPath + " existsType=directory removeOnSuccess=" + removeFromLibraryOnSuccess);
            text = GetNonConflictingPathWithSuffix(requestedPath);
            NLogWrapper.FileLogger?.Info("invalid_ext_rename renamed_with_suffix source=" + sourceFile.path + " requested=" + requestedPath + " resolved=" + text);
        }
        else if (File.Exists(requestedPath))
        {
            NLogWrapper.FileLogger?.Info("invalid_ext_rename collision_detected source=" + sourceFile.path + " requested=" + requestedPath + " existsType=file removeOnSuccess=" + removeFromLibraryOnSuccess);
            string text2 = TryGetSourceHashForInvalidExtensionRename(sourceFile);
            string text3 = TryComputeFileMd5ForPath(requestedPath, "invalid_ext_rename");
            if (!string.IsNullOrWhiteSpace(text2) && !string.IsNullOrWhiteSpace(text3) && text2.Equals(text3, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    fileMutationService.DeleteFileDirect(sourceFile.path, targetOnlyFileMutationOptions);
                    NLogWrapper.FileLogger?.Info("invalid_ext_rename duplicate_deleted source=" + sourceFile.path + " existing=" + requestedPath + " hash=" + text2);
                    renameInvalidExtensionOutcome.Action = RenameInvalidExtensionAction.DeletedAsDuplicate;
                    return renameInvalidExtensionOutcome;
                }
                catch (Exception ex)
                {
                    renameInvalidExtensionOutcome.FailureException = ex;
                    renameInvalidExtensionOutcome.FailedDuringDelete = true;
                    NLogWrapper.FileLogger?.Warn(ex, "invalid_ext_rename delete_failed source=" + sourceFile.path + " existing=" + requestedPath);
                    return renameInvalidExtensionOutcome;
                }
            }
            if (string.IsNullOrWhiteSpace(text2) || string.IsNullOrWhiteSpace(text3))
            {
                NLogWrapper.FileLogger?.Info("invalid_ext_rename hash_compare_unavailable source=" + sourceFile.path + " requested=" + requestedPath + " reason=" + (string.IsNullOrWhiteSpace(text2) ? "source_hash_unavailable" : "dest_hash_unavailable"));
            }
            text = GetNonConflictingPathWithSuffix(requestedPath);
            NLogWrapper.FileLogger?.Info("invalid_ext_rename renamed_with_suffix source=" + sourceFile.path + " requested=" + requestedPath + " resolved=" + text);
        }
        try
        {
            fileMutationService.MoveFile(sourceFile.path, text, overwrite: false, targetOnlyFileMutationOptions);
            renameInvalidExtensionOutcome.Action = RenameInvalidExtensionAction.Renamed;
            renameInvalidExtensionOutcome.FinalPath = text;
            return renameInvalidExtensionOutcome;
        }
        catch (Exception ex2)
        {
            renameInvalidExtensionOutcome.FailureException = ex2;
            renameInvalidExtensionOutcome.FinalPath = text;
            renameInvalidExtensionOutcome.FailedDuringDelete = false;
            NLogWrapper.FileLogger?.Warn(ex2, "invalid_ext_rename move_failed source=" + sourceFile.path + " target=" + text);
            return renameInvalidExtensionOutcome;
        }
    }

    private string GetNonConflictingPathWithSuffix(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }
        string directoryName = Path.GetDirectoryName(requestedPath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        int num = 1;
        string text = requestedPath;
        while (File.Exists(text) || Directory.Exists(text))
        {
            string text2 = fileNameWithoutExtension + "(" + num + ")" + extension;
            text = (string.IsNullOrWhiteSpace(directoryName) ? text2 : Path.Combine(directoryName, text2));
            num++;
        }
        return text;
    }

    private string TryGetSourceHashForInvalidExtensionRename(BMSFile sourceFile)
    {
        if (!IsBMSHashAvailable(sourceFile))
        {
            return TryComputeFileMd5ForPath(sourceFile?.path, "invalid_ext_rename");
        }
        return sourceFile.hash;
    }

    private string TryComputeFileMd5ForPath(string filePath, string logCategory = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }
        try
        {
            using MD5 mD = MD5.Create();
            byte[] array;
            using (FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                array = mD.ComputeHash(inputStream);
            }
            StringBuilder stringBuilder = new StringBuilder();
            byte[] array2 = array;
            foreach (byte b in array2)
            {
                stringBuilder.Append(b.ToString("x2"));
            }
            return stringBuilder.ToString();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
        {
            if (!string.IsNullOrWhiteSpace(logCategory))
            {
                NLogWrapper.FileLogger?.Info(ex, logCategory + " hash_unavailable path=" + filePath);
            }
            return null;
        }
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

    public void RemoveBMSFiles(IEnumerable<BMSFile> bmsFiles, bool sendToRecycleBin = true)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    List<BMSFile> removedBmsFiles = new List<BMSFile>();
                    foreach (IGrouping<string, BMSFile> folderGroup in from groupedFiles in bmsFiles.GroupBy((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path), StringComparer.OrdinalIgnoreCase)
                                                                        orderby groupedFiles.Key.Length descending
                                                                        select groupedFiles)
                    {
                        if (BMSFiles.Where((BMSFile bmsInfo) => bmsInfo.path.StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Except(removedBmsFiles).Count() == folderGroup.Count() && DispatcherMessageBox.Show(string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderGroup.Key), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                        {
                            if (!Directory.Exists(folderGroup.Key))
                            {
                                continue;
                            }
                            try
                            {
                                fileMutationService.DeleteDirectoryShell(folderGroup.Key, UIOption.OnlyErrorDialogs, sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently, recursiveDirectoryTreeFileMutationOptions);
                                foreach (string indexedDirectoryPath in bmsFolderAllFileList.Keys.Where((string directoryPath) => (directoryPath + Path.DirectorySeparatorChar).StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                                {
                                    bmsFolderAllFileList.RemoveDir(indexedDirectoryPath);
                                }
                                foreach (BMSFile installLinkedBmsFile in BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat(BMSFiles.Where((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst))))
                                {
                                    if (!string.IsNullOrWhiteSpace(installLinkedBmsFile.instl_dst) && (installLinkedBmsFile.instl_dst + Path.DirectorySeparatorChar).StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    {
                                        installLinkedBmsFile.instl_dst = null;
                                    }
                                }
                                List<BMSPackage> installedPackagesToRemove = BMSPackagesInstalled.Where((BMSPackage pkg) => (pkg.path + Path.DirectorySeparatorChar).StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
                                BMSPackagesInstalled.Remove(installedPackagesToRemove);
                            }
                            catch (Exception deleteException)
                            {
                                DispatcherMessageBox.Show(string.Format(Resources.Error_FolderOrTrashDeleteFailed, folderGroup.Key, GetDisplayedExceptionMessage(deleteException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                continue;
                            }
                            removedBmsFiles.AddRange(folderGroup);
                            continue;
                        }
                        foreach (BMSFile selectedBmsFile in folderGroup)
                        {
                            try
                            {
                                if (File.Exists(selectedBmsFile.path))
                                {
                                    fileMutationService.DeleteFileShell(selectedBmsFile.path, UIOption.OnlyErrorDialogs, sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently, targetOnlyFileMutationOptions);
                                    removedBmsFiles.Add(selectedBmsFile);
                                }
                            }
                            catch (Exception deleteException)
                            {
                                DispatcherMessageBox.Show(string.Format(Resources.Error_BmsFileDeleteFailed, selectedBmsFile.path, GetDisplayedExceptionMessage(deleteException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                    }
                    unregisterBMSFiles(removedBmsFiles);
                }
            }
        }
    }

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
        return BMSPackagesPending.Where((BMSPackage pkg) => pkg != null && pkg.BMSFiles.Count > 0 && pkg.BMSFiles.All((BMSFile f) => IsMatchedRemovedFile(f, selectedPaths, selectedFileRefs))).ToList();
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
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSFile item2 in list)
            {
                lR2SongDBExtended.Delete<LR2SongDB.song>(item2.path);
                lR2SongDBExtended.Delete<LR2SongDBExtended.maintenance>(item2.path);
            }
            lR2SongDBExtended.Commit();
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

    private static bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (file == null)
        {
            return false;
        }
        if (removedFiles != null && removedFiles.Contains(file))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(file.path) && removedPaths.Contains(file.path))
        {
            return true;
        }
        return false;
    }

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
                    using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        oldPath = bmsFile.path;
                    }
                    lR2SongDBExtended.BeginTransaction();
                    lR2SongDBExtended.Delete<LR2SongDB.song>(oldPath);
                    lR2SongDBExtended.Delete<LR2SongDBExtended.maintenance>(oldPath);
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
                    InvalidateBMSParentFolderListCache();
                    lR2SongDBExtended.InsertOrReplace(bmsFile.maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
                    lR2SongDBExtended.InsertOrReplace(bmsFile, typeof(LR2SongDB.song));
                    lR2SongDBExtended.Commit();
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
        try
        {
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            LR2SongDB.folder folder = lR2SongDBExtended.Table<LR2SongDB.folder>().ToList().FirstOrDefault((LR2SongDB.folder f) => f.path.Equals(oldFolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (folder == null)
            {
                return false;
            }
            lR2SongDBExtended.Delete<LR2SongDB.folder>(folder.path);
            folder.title = Path.GetFileName(newFolderPath);
            folder.path = newFolderPath + Path.DirectorySeparatorChar;
            if (folder.parent != "e2977170")
            {
                string directoryName = Path.GetDirectoryName(newFolderPath);
                folder.parent = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(directoryName + "\\\0")).ToString("x");
            }
            lR2SongDBExtended.InsertOrReplace(folder, typeof(LR2SongDB.folder));
            lR2SongDBExtended.Commit();
        }
        catch
        {
            return false;
        }
        return true;
    }

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
                using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                lR2SongDBExtended.BeginTransaction();
                foreach (BMSFile _bmsFile in _bmsFiles)
                {
                    lR2SongDBExtended.InsertOrReplace(_bmsFile, typeof(LR2SongDB.song));
                }
                lR2SongDBExtended.Commit();
            }
        }
    }
}
