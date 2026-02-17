using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
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

namespace BeMusicSeeker.Models;

public class BMSLibrary : NotificationObject
{
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
                    throw new ArgumentException("md5 hashではありません", "json.md5");
                }
                md5 = json.md5.ToString();
                size = int.Parse(json.size.ToString());
                lastupdate = DateTime.ParseExact(json.lastupdate.ToString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }
            throw new ArgumentException("無効なJSONオブジェクトです", "json");
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
                throw new ArgumentException("無効なJSONオブジェクトです", "json");
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

    private ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedAll = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesInitializedMin = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesDuplicated = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFilesPendingInstall = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockLR2IrDir = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSScores = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockBMSFiles = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockSongDBInstall = new ReaderWriterLockSlimWrapper();

    private ReaderWriterLockSlimWrapper rwlockSongDBMaintenance = new ReaderWriterLockSlimWrapper();

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedAll;

    private PropertyChangedEventListener listenerForRwlockBMSFilesInitializedMin;

    private PropertyChangedEventListener listenerForRwlockBMSFilesDuplicated;

    private PropertyChangedEventListener listenerForRwlockBMSFilesPendingInstall;

    private PropertyChangedEventListener listenerForRwlockBMSFiles;

    private List<BMSFile> _BMSFiles = new List<BMSFile>();

    private List<List<BMSFile>> _BMSFilesDuplicated;

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
                BMSFilesDuplicated = null;
                Task.Run(delegate
                {
                    RaisePropertyChanged("BMSFiles");
                }).Logging("BMSFiles", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 166);
                RaisePropertyChanged(() => BMSParentFolderList);
            }
        }
    }

    public List<BMSFile> BMSFilesUnregistered => BMSFiles.Where((BMSFile f) => string.IsNullOrWhiteSpace(f.parent)).ToList();

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixed => GetBMSFilesNeedToBeFixed(BMSFiles);

    public IEnumerable<BMSFile> BMSFilesNeedToBeFixedIgnored => GetBMSFilesNeedToBeFixed(BMSFiles, forceUpdate: false, isInIgnoredList: true);

    public List<List<BMSFile>> BMSFilesDuplicated
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
                }).Logging("BMSFilesDuplicated", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 203);
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
                IEnumerable<string> enumerable = getBMSDirectories().Where(delegate (string d)
                {
                    if (BMSFiles.Any((BMSFile f) => f.path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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
                });
                List<string> items = enumerable.Except(_BMSParentFolderList).ToList();
                List<string> items2 = _BMSParentFolderList.Except(enumerable).ToList();
                _BMSParentFolderList.AddRange(items);
                _BMSParentFolderList.Remove(items2);
                return _BMSParentFolderList;
            }
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

    public BMSLibrary(string _lr2SongDB, Func<LR2Config> getLR2Config = null, string _lr2ScoreDB = null)
    {
        if (_lr2SongDB == null)
        {
            throw new ArgumentNullException("_LR2SongDB");
        }
        if (!File.Exists(_lr2SongDB))
        {
            throw new ArgumentException("LR2 song DB が見つかりませんでした。パス: " + _lr2SongDB, "_LR2SongDB");
        }
        if (_lr2ScoreDB != null && !File.Exists(_lr2ScoreDB))
        {
            throw new ArgumentException("LR2 score DB が見つかりませんでした。パス: " + _lr2ScoreDB, "_lr2ScoreDB");
        }
        lr2SongDBPath = _lr2SongDB;
        lr2ScoreDBPath = _lr2ScoreDB;
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

    public void Initialize(List<Action> tasksContinuation, SemaphoreSlim semaphore = null, bool? reloadScoresOnly = null)
    {
        bool songTblLoad = reloadScoresOnly != true;
        bool songTblFileCheck = reloadScoresOnly == false || (reloadScoresOnly != true && !Settings.Default.SkipInitFileCheck);
        bool flag = reloadScoresOnly != true;
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("hazimari: " + GC.GetTotalMemory(forceFullCollection: false));
        List<Task> list = new List<Task>();
        DateTime now;
        using (rwlockBMSFilesInitializedAll.GetWriterGuard())
        {
            now = DateTime.Now;
            using (rwlockBMSFilesInitializedMin.GetWriterGuard())
            {
                _initialize(songTblLoad, scoreTblrLoad: true, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, installTblCheck: false, maintenanceTblCheck: false);
            }
            TimeSpan timeSpan = DateTime.Now - now;
            NLogWrapper.DebuggerLogger?.Trace(timeSpan.ToString());
            semaphore?.Wait();
            if (tasksContinuation != null)
            {
                for (int i = 0; i < tasksContinuation.Count; i++)
                {
                    list.Add(Task.Run(tasksContinuation[i]).Logging("Initialize", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 447));
                }
            }
            Thread.Yield();
            semaphore?.Wait();
            now = DateTime.Now;
            _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck, setMainteInfo: true, updateIrScore: true, installTblCheck: false, maintenanceTblCheck: false);
            semaphore?.Release();
            _initialize(songTblLoad: false, scoreTblrLoad: false, songTblFileCheck: false, setMainteInfo: false, updateIrScore: false, flag, flag);
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
        GC.Collect();
        NLogWrapper.DebuggerLogger?.Trace("owari: " + GC.GetTotalMemory(forceFullCollection: false));
    }

    private void _initialize(bool songTblLoad = true, bool scoreTblrLoad = true, bool songTblFileCheck = true, bool setMainteInfo = true, bool updateIrScore = true, bool installTblCheck = true, bool maintenanceTblCheck = true)
    {
        List<string> bMSDirectories = getBMSDirectories();
        if (bMSDirectories.Count == 0)
        {
            songTblFileCheck = false;
        }
        if (songTblLoad)
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
                try
                {
                    lr2Song.BeginTransaction();
                    List<BMSFile> list = lr2Song.Table<BMSFile>().ToList();
                    List<BMSFile> deleteFiles = new List<BMSFile>();
                    NLogWrapper.DebuggerLogger?.Trace("relative path and invalid md5 check");
                    if (!string.IsNullOrWhiteSpace(lr2Song.LR2RootPath))
                    {
                        List<string> deletePaths = new List<string>();
                        int unixtime = (DateTime.Now + new TimeSpan(30, 0, 0, 0)).ToUnixtime();
                        bool leapYearDetected = false;
                        List<BMSFile> list2 = list.Where(delegate (BMSFile e)
                        {
                            try
                            {
                                if (string.IsNullOrWhiteSpace(e.hash))
                                {
                                    deletePaths.Add(e.path);
                                    deleteFiles.Add(e);
                                }
                                else
                                {
                                    if (!Path.IsPathRooted(e.path))
                                    {
                                        deletePaths.Add(e.path);
                                        e.path = Path.Combine(lr2Song.LR2RootPath, e.path);
                                        string directoryName = Path.GetDirectoryName(e.path);
                                        e.folder = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(directoryName + "\\\0")).ToString("x");
                                        e.parent = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(Path.GetDirectoryName(directoryName) + "\\\0")).ToString("x");
                                        return true;
                                    }
                                    if (e.adddate < 0 || e.adddate > unixtime)
                                    {
                                        e.adddate = null;
                                        e.date = null;
                                        leapYearDetected = true;
                                        return true;
                                    }
                                    string directoryName2 = Path.GetDirectoryName(e.path);
                                    string text = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(directoryName2 + "\\\0")).ToString("x");
                                    string text2 = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(Path.GetDirectoryName(directoryName2) + "\\\0")).ToString("x");
                                    if (text != e.folder || text2 != e.parent)
                                    {
                                        e.folder = text;
                                        e.parent = text2;
                                        return true;
                                    }
                                }
                            }
                            catch
                            {
                                deletePaths.Add(e.path);
                                deleteFiles.Add(e);
                            }
                            return false;
                        }).ToList();
                        List<string> deleteFolders = new List<string>();
                        List<LR2SongDB.folder> list3 = lr2Song.Table<LR2SongDB.folder>().ToList().Where(delegate (LR2SongDB.folder e)
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
                                            e.parent = LR2CRC32.Compute(Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(directoryName + "\\\0")).ToString("x");
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
                                                if (DispatcherMessageBox.Show("LR2で扱えない更新日時のフォルダを検出しました" + Environment.NewLine + "対象のフォルダはLR2において楽曲の認識に不具合が生じる可能性があります" + Environment.NewLine + "フォルダの更新日時を現在の日時で置き換えこの問題を回避しますか？" + Environment.NewLine + Environment.NewLine + "対象: " + text + Environment.NewLine + "更新日時(変更前): " + lastWriteTime.ToShortDateString() + Environment.NewLine + "更新日時(変更後): " + now.ToShortDateString(), "警告", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) == MessageBoxResult.Yes)
                                                {
                                                    try
                                                    {
                                                        Directory.SetLastWriteTime(text, now);
                                                        e.adddate = null;
                                                        e.date = null;
                                                        leapYearDetected = true;
                                                        return true;
                                                    }
                                                    catch (Exception ex2)
                                                    {
                                                        DispatcherMessageBox.Show("更新日時の変更に失敗しました" + Environment.NewLine + ex2.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                        if (leapYearDetected)
                        {
                            DispatcherMessageBox.Show("LR2の閏年(2/29)バグを検出しました" + Environment.NewLine + "修正を行ったため一部の楽曲でリロードが発生する場合があります", "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                    }
                    NLogWrapper.DebuggerLogger?.Trace("relative path check end");
                    BMSFiles = (from bmsFile in list.Except(deleteFiles)
                                join mtInfo in lr2Song.Table<BMSFileMaintenanceInfo>().ToList() on bmsFile.path equals mtInfo.path into bmsMtInfo
                                select new
                                {
                                    BMSFile = bmsFile,
                                    MaintenanceInfo = bmsMtInfo.DefaultIfEmpty(new BMSFileMaintenanceInfo(bmsFile))
                                }).SelectMany(x => x.MaintenanceInfo, (x, MaintenanceInfo) =>
                            {
                                if (x.BMSFile.maintenanceInfo.hash == MaintenanceInfo.hash)
                                {
                                    x.BMSFile.maintenanceInfo = MaintenanceInfo;
                                }
                                else
                                {
                                    x.BMSFile.maintenanceInfo = new BMSFileMaintenanceInfo(x.BMSFile);
                                }
                                return x.BMSFile;
                            }).ToList();
                    lr2Song.Commit();
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
        if (scoreTblrLoad)
        {
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
        }
        if (songTblFileCheck)
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                bmsFolderAllFileList = new BMSDirectoryFileNameHash();
                HashSet<string> hashSet = new HashSet<string>(bMSDirectories.AsParallel().SelectMany((string dir) => FastDirectoryEnumerator.GetFilePathsAsParallel(dir, bmsFolderAllFileList, BMSFile.bmsExtensions, System.IO.SearchOption.AllDirectories)), StringComparer.OrdinalIgnoreCase);
                HashSet<string> hashSet2 = new HashSet<string>(BMSFiles.Select((BMSFile x) => x.path), StringComparer.OrdinalIgnoreCase);
                HashSet<string> bmsFilesDeletedPaths = new HashSet<string>(hashSet2.Except(hashSet), StringComparer.OrdinalIgnoreCase);
                List<string> list4 = hashSet.Except(hashSet2).ToList();
                List<BMSFile> list5 = ((list4.Count <= 0) ? new List<BMSFile>() : (from x in list4.AsParallel().Select(delegate (string l)
                    {
                        BMSFile bMSFile = null;
                        try
                        {
                            bMSFile = BMSFile.CreateBMSFileFromFile(l);
                        }
                        catch (IOException ex2)
                        {
                            DispatcherMessageBox.Show("初期化中に下記のエラーが発生しました。" + Environment.NewLine + "ファイルへのアクセスが可能か確認して下さい。" + Environment.NewLine + "対象ファイル: " + l + Environment.NewLine + Environment.NewLine + "アプリケーションの動作が不安定になる場合があります。" + Environment.NewLine + Environment.NewLine + ex2.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                if (bmsFilesDeletedPaths.Count > 0 || list5.Count > 0)
                {
                    if (bmsFilesDeletedPaths.Count > 0)
                    {
                        BMSFiles.RemoveAll((BMSFile f) => bmsFilesDeletedPaths.Contains(f.path));
                    }
                    if (list5.Count > 0)
                    {
                        BMSFiles.AddRange(list5);
                    }
                    using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                    lR2SongDBExtended.BeginTransaction();
                    if (bmsFilesDeletedPaths.Count > 0)
                    {
                        foreach (string item5 in bmsFilesDeletedPaths)
                        {
                            lR2SongDBExtended.Delete<LR2SongDB.song>(item5);
                        }
                    }
                    if (list5.Count > 0)
                    {
                        foreach (BMSFile item6 in list5)
                        {
                            lR2SongDBExtended.InsertOrReplace(item6, typeof(LR2SongDB.song));
                        }
                    }
                    lR2SongDBExtended.Commit();
                }
                List<string> keys = bmsFolderAllFileList.Keys;
                foreach (BMSFile item7 in BMSFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst)))
                {
                    if (!keys.Contains(item7.instl_dst, StringComparer.OrdinalIgnoreCase))
                    {
                        item7.instl_dst = null;
                    }
                }
            }
        }
        if (setMainteInfo)
        {
            setModeAndCommitToDB(BMSFiles);
            setMaintenanceInfo(BMSFiles);
            IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
            IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
            setZeroNoteAndCommitToDB(BMSFiles);
            IsWriteLockHeldInitializeBMSFilesZeroNote = false;
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
            }).Logging("_initialize", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 892);
        }
        if (installTblCheck)
        {
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
                            if (BMSFiles.Select((BMSFile x) => x.hash).Contains(bmsFile.hash))
                            {
                                bmsFile.warning = "インストールされています";
                            }
                            else if (!Directory.Exists(pkg.path))
                            {
                                bmsFile.warning = "BMSファイル単体です";
                            }
                            else
                            {
                                checkBMSFileNeedToBeFixedAndSetWarnings(bmsFile, bmsFile.maintenanceInfo, strictCheck: true);
                            }
                        });
                    });
                }
            }
        }
        if (!maintenanceTblCheck)
        {
            return;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            using (rwlockSongDBMaintenance.GetWriterGuard())
            {
                LR2SongDBExtended lr2Song2 = new LR2SongDBExtended(lr2SongDBPath);
                try
                {
                    lr2Song2.BeginTransaction();
                    (from m in lr2Song2.Table<BMSFileMaintenanceInfo>().ToList()
                     select m.path).Except(BMSFiles.Select((BMSFile i) => i.path)).ToList().ForEach(delegate (string path)
                 {
                     lr2Song2.Delete<LR2SongDBExtended.maintenance>(path);
                 });
                    lr2Song2.Commit();
                }
                finally
                {
                    if (lr2Song2 != null)
                    {
                        ((IDisposable)lr2Song2).Dispose();
                    }
                }
            }
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
        return SearchTargets.Where((string d) => Directory.Exists(d)).ToList();
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
            empty = new GZipWebClient
            {
                Encoding = Encoding.GetEncoding("shift_jis")
            }.DownloadString(uri.AbsoluteUri);
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
            throw new InvalidOperationException("LR2スコアDBと接続されていません。");
        }
        List<string> list = md5s.Where((string md5) => LR2SongDB.md5HashRegex.IsMatch(md5)).ToList();
        dynamic val = new DynamicJson(DynamicJson.JsonType.array);
        for (int num = 0; num < list.Count; num++)
        {
            val[num] = list[num];
        }
        GZipWebClient gZipWebClient = new GZipWebClient();
        gZipWebClient.Headers[HttpRequestHeader.ContentType] = "application/json;charset=UTF-8";
        gZipWebClient.Headers[HttpRequestHeader.Accept] = "application/json";
        gZipWebClient.Encoding = Encoding.UTF8;
        dynamic val2 = DynamicJson.Parse((string)(object)gZipWebClient.UploadString(rankingInfoUrl, val.ToString()));
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
            throw new InvalidOperationException("LR2スコアDBと接続されていません。");
        }
        if (cacheInfo == null)
        {
            throw new ArgumentNullException("cacheInfo");
        }
        string irCacheDirPath = Path.Combine(Path.GetDirectoryName(lr2ScoreDBPath), "..\\..\\Ir");
        if (!Directory.Exists(irCacheDirPath))
        {
            throw new DirectoryNotFoundException(irCacheDirPath + "が見つかりませんでした");
        }
        List<IRDataCacheInfo> source = cacheInfo.ToList();
        List<IRDataCacheInfo> failed = new List<IRDataCacheInfo>();
        object failedLock = new object();
        List<LR2IRData> irDataToBeCommited = new List<LR2IRData>();
        using (rwlockLR2IrDir.GetWriterGuard())
        {
            source.AsParallel().WithDegreeOfParallelism(3).ForAll(delegate (IRDataCacheInfo info)
            {
                GZipWebClient gZipWebClient = new GZipWebClient();
                try
                {
                    Uri address = new Uri(rankingDataUrl, "./" + info.md5 + ".xml");
                    gZipWebClient.DownloadFile(address, Path.Combine(irCacheDirPath, info.md5 + ".xml"));
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
                    dynamic val2 = DynamicJson.Parse(new GZipWebClient
                    {
                        Encoding = Encoding.UTF8
                    }.DownloadString(new Uri(songInfoUrl, "./" + md5orlr2bmsid)));
                    info = new IRSongInfo(val2);
                });
                if (seaarchAggressively)
                {
                    Task.Run(delegate
                    {
                        dynamic val2 = DynamicJson.Parse(new GZipWebClient
                        {
                            Encoding = Encoding.UTF8
                        }.DownloadString(new Uri("http://www.ribbit.xyz/bms/search/run?search[value]=" + md5orlr2bmsid)));
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
                dynamic val = DynamicJson.Parse(new GZipWebClient
                {
                    Encoding = Encoding.UTF8
                }.DownloadString(new Uri(songInfoUrl, "./" + md5orlr2bmsid)));
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
                            try
                            {
                                f.SetHealthStatus(bmsFolderAllFileList, forceUpdate);
                            }
                            catch (Exception ex)
                            {
                                if (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                                {
                                    DispatcherMessageBox.Show("BMSファイルの読み込みに失敗しました。" + Environment.NewLine + "このファイルの調査はスキップされます。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + f.path + Environment.NewLine + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                    return;
                                }
                                throw;
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
                        Task.Run(delegate
                        {
                            RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
                        }).Logging("setMaintenanceInfo", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 1912);
                        Task.Run(delegate
                        {
                            RaisePropertyChanged(() => BMSFilesNeedToBeFixedIgnored);
                        }).Logging("setMaintenanceInfo", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 1913);
                        Task.Run(delegate
                        {
                            RaisePropertyChanged(() => BMSFilesGarbled);
                        }).Logging("setMaintenanceInfo", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 1914);
                        Task.Run(delegate
                        {
                            RaisePropertyChanged(() => BMSFilesGarbledFixed);
                        }).Logging("setMaintenanceInfo", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 1915);
                        NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
                        GC.Collect();
                        NLogWrapper.DebuggerLogger?.Trace(GC.GetTotalMemory(forceFullCollection: false));
                    }
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
            bMSFile.warning = bMSFile.warning + "[" + $"{wAVHealth,2}" + "%] WAVファイルが見つかりませんでした。(" + (mtInfo.wav_files_defined - mtInfo.wav_files_existing) + "/" + mtInfo.wav_files_defined + ")";
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
            bMSFile.warning = bMSFile.warning + "[" + $"{bGAHealth,2}" + "%] BGAファイルが見つかりませんでした。(" + (mtInfo.bga_files_defined - mtInfo.bga_files_existing) + "/" + mtInfo.bga_files_defined + ")";
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
            bMSFile.warning = bMSFile.warning + "[" + $"{movieHealth,2}" + "%] MOVIEファイルが見つかりませんでした。(" + (mtInfo.movie_files_defined - mtInfo.movie_files_existing) + "/" + mtInfo.movie_files_defined + ")";
            flag3 = true;
        }
        if (mtInfo.GetStagefileHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += "[ 0%] STAGEFILEが見つかりませんでした。(1/1)";
            flag4 = true;
        }
        if (mtInfo.GetBackbmpHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += "[ 0%] BACKBMPが見つかりませんでした。(1/1)";
            flag5 = true;
        }
        if (mtInfo.GetBannerHealth() == false)
        {
            if (!string.IsNullOrWhiteSpace(bmsFile.warning))
            {
                bmsFile.warning += Environment.NewLine;
            }
            bmsFile.warning += "[ 0%] BANNERが見つかりませんでした。(1/1)";
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
        if (bmsFiles.Count() == 0)
        {
            return;
        }
        using (rwlockBMSFiles.GetWriterGuard())
        {
            BMSFiles.Where((BMSFile f) => f.notes == 0).ToList();
            List<BMSFile> source = BMSFiles.Where((BMSFile f) => !f.notes.HasValue && File.Exists(f.path)).ToList();
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
                    DispatcherMessageBox.Show("BMSファイルの読み込みに失敗しました。" + Environment.NewLine + "このファイルの調査はスキップされます。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + f.path + Environment.NewLine + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
            }).Logging("setZeroNoteAndCommitToDB", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSLibrary.cs", 2239);
        }
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
                    List<List<string>> list = (from file in BMSFiles.AsParallel()
                                               group file by file.hash into g
                                               where g.Count() > 1
                                               select g.Select(delegate (BMSFile bmsInfo)
                                               {
                                                   if (!string.IsNullOrWhiteSpace(bmsInfo.warning))
                                                   {
                                                       bmsInfo.warning += Environment.NewLine;
                                                   }
                                                   bmsInfo.warning += "BMSファイルが重複しています";
                                                   return DirectoryExt.GetDirectoryNameSimple(bmsInfo.path);
                                               }).Distinct(StringComparer.OrdinalIgnoreCase).ToList()).ToList();
                    List<List<string>> list2 = new List<List<string>>();
                    foreach (List<string> item in list)
                    {
                        bool flag = false;
                        for (int num = 0; num < list2.Count; num++)
                        {
                            List<string> y = list2[num];
                            flag = item.Any((string i) => y.Contains(i, StringComparer.OrdinalIgnoreCase));
                            if (flag)
                            {
                                y = y.Union(item).ToList();
                                break;
                            }
                        }
                        if (!flag)
                        {
                            list2.Add(item);
                        }
                    }
                    BMSFilesDuplicated = (from source in list2.AsParallel()
                                          select (from dir in source
                                                  from bmsInfo in BMSFiles
                                                  where DirectoryExt.GetDirectoryNameSimple(bmsInfo.path).ToUpperInvariant() == dir.ToUpperInvariant()
                                                  select bmsInfo).ToList() into l
                                          orderby l.First().title
                                          select l).ToList();
                }
            }
        }
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
                            DispatcherMessageBox.Show("ファイルの一部または全てが見つからなかったため、" + Environment.NewLine + "インストールを中断しました。", "警告", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                                        archiveFile.Extract(text);
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
                                bmsFile.warning = "インストールされています";
                                return true;
                            }
                            if (!Directory.Exists(pkg.path))
                            {
                                bmsFile.warning = "BMSファイル単体です";
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
                            if (SearchTargets != null && SearchTargets.Count() > 0 && Directory.Exists(SearchTargets[0]))
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

    private bool moveBMSPackageFiles(BMSPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false)
    {
        string path = pkg.path;
        string dirname = string.Empty;
        string bMSInstallDir = Settings.Default.BMSInstallDir;
        bool flag = false;
        List<string> installComponentFiles = new List<string>();
        List<BMSFile> installBMSFiles = new List<BMSFile>();
        List<BMSFile> list = new List<BMSFile>();
        bool flag2 = false;
        if (File.Exists(path))
        {
            installComponentFiles = new List<string> { path };
            flag = true;
        }
        else
        {
            if (!Directory.Exists(path))
            {
                return false;
            }
            installComponentFiles = Directory.EnumerateFileSystemEntries(path).ToList();
            flag2 = string.IsNullOrWhiteSpace(installationDirectory);
        }
        installBMSFiles = pkg.BMSFiles.Where((BMSFile f) => installComponentFiles.Contains(f.path, StringComparer.OrdinalIgnoreCase)).ToList();
        installComponentFiles = installComponentFiles.Except(installBMSFiles.Select((BMSFile f) => f.path), StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            list = installBMSFiles.Where((BMSFile bmsFile) => (from x in BMSFiles.Except(installBMSFiles)
                                                               select x.hash).Contains(bmsFile.hash)).ToList();
            if (list.Count != 0)
            {
                installBMSFiles = installBMSFiles.Except(list).ToList();
                foreach (BMSFile item in list)
                {
                    pkg.BMSFiles.Remove(item);
                }
            }
        }
        try
        {
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                dirname = createBMSFolderPath(pkg.BMSFiles, bMSInstallDir, (from file in installComponentFiles
                                                                            select Path.GetFileName(file) into f
                                                                            orderby f.Length descending
                                                                            select f).FirstOrDefault() ?? string.Empty);
            }
            else
            {
                dirname = installationDirectory;
            }
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                int num = 1;
                string text = dirname;
                while (Directory.Exists(dirname) || File.Exists(dirname))
                {
                    num++;
                    dirname = text + "(" + num + ")";
                }
                if (!flag2)
                {
                    Directory.CreateDirectory(dirname);
                }
            }
            if (flag2)
            {
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException("ディレクトリが見つかりませんでした: " + path);
                }
                FileSystem.MoveDirectory(path, dirname, overwrite: true);
            }
            else
            {
                installComponentFiles.AsParallel().ForAll(delegate (string file)
                {
                    string text3 = Path.Combine(dirname, Path.GetFileName(file));
                    if (File.Exists(file))
                    {
                        FileSystem.MoveFile(file, text3, overwrite: true);
                    }
                    else
                    {
                        if (!Directory.Exists(file))
                        {
                            throw new FileNotFoundException("ファイルが見つかりませんでした", file);
                        }
                        FileSystem.MoveDirectory(file, text3, overwrite: true);
                    }
                });
                foreach (BMSFile item2 in installBMSFiles)
                {
                    string text2 = Path.Combine(dirname, Path.GetFileName(item2.path));
                    while (File.Exists(text2) || Directory.Exists(text2))
                    {
                        string path2 = Path.GetFileNameWithoutExtension(text2) + "_" + Path.GetExtension(text2);
                        text2 = Path.Combine(dirname, Path.GetFileName(path2));
                    }
                    if (!File.Exists(item2.path))
                    {
                        throw new FileNotFoundException("ファイルが見つかりませんでした", item2.path);
                    }
                    FileSystem.MoveFile(item2.path, text2, overwrite: true);
                    item2.path = item2.path.ReplaceFromEnd(Path.GetFileName(item2.path), Path.GetFileName(text2), isIgnoreCase: true);
                }
            }
        }
        catch (Exception ex)
        {
            if (!showMessageBoxOnInstallFail)
            {
                return false;
            }
            DispatcherMessageBox.Show("インストール中に下記のエラーが発生しました。" + Environment.NewLine + "操作は取り消され、インストール一覧からは削除されます。" + Environment.NewLine + Environment.NewLine + "パッケージ及びインストール先を確認して下さい。" + Environment.NewLine + "パッケージ: " + pkg.path + Environment.NewLine + "インストール先: " + dirname + Environment.NewLine + Environment.NewLine + ((ex is AggregateException) ? string.Join(Environment.NewLine, ((AggregateException)ex).Flatten().InnerExceptions.Select((Exception e) => e.Message)) : ex.Message), "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }
        if (flag)
        {
            pkg.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = Path.Combine(dirname, Path.GetFileName(bmsInfo.path));
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            pkg.path = Path.Combine(dirname, Path.GetFileName(pkg.path));
        }
        else
        {
            if (!(Directory.Exists(path) || flag2))
            {
                return false;
            }
            pkg.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = bmsInfo.path.ReplaceFromStart(path + Path.DirectorySeparatorChar, dirname + Path.DirectorySeparatorChar, isIgnoreCase: true);
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            pkg.path = dirname;
        }
        string dirToBeDeleted = string.Empty;
        if (pkg.delete_parent)
        {
            dirToBeDeleted = Path.GetDirectoryName(path);
        }
        else if (Directory.Exists(path))
        {
            dirToBeDeleted = path;
        }
        if (!string.IsNullOrWhiteSpace(dirToBeDeleted) && Directory.Exists(dirToBeDeleted))
        {
            RetryHelper.RetryIfError(delegate
            {
                if (deleteAllContents || Directory.GetFileSystemEntries(dirToBeDeleted, "*").Count() == 0)
                {
                    FileSystem.DeleteDirectory(dirToBeDeleted, DeleteDirectoryOption.DeleteAllContents);
                }
            }, delegate (Exception ex2)
            {
                DispatcherMessageBox.Show("フォルダの削除に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + dirToBeDeleted + Environment.NewLine + Environment.NewLine + ex2.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }, delegate
            {
                Thread.Sleep(1000);
            }, 10u);
        }
        return true;
    }

    private List<BMSPackage> installBMSPackages(IEnumerable<BMSPackage> bmsPackagesInstall, string installationDirectory = null)
    {
        List<BMSFile> bmsFilesToBeAdded = new List<BMSFile>();
        List<BMSPackage> list = new List<BMSPackage>();
        foreach (BMSPackage item in bmsPackagesInstall)
        {
            if (moveBMSPackageFiles(item, installationDirectory))
            {
                bmsFilesToBeAdded.AddRange(item.BMSFiles);
                BMSPackagesInstalled.Add(item);
            }
            else if (File.Exists(item.path) || Directory.Exists(item.path))
            {
                list.Add(item);
            }
        }
        using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
        {
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSFile item2 in bmsFilesToBeAdded)
            {
                lR2SongDBExtended.InsertOrReplace(item2, typeof(LR2SongDB.song));
            }
            lR2SongDBExtended.Commit();
        }
        setMaintenanceInfo(bmsFilesToBeAdded, forceUpdate: true);
        setZeroNoteAndCommitToDB(bmsFilesToBeAdded);
        SetBMSScore(bmsFilesToBeAdded);
        BMSFiles = BMSFiles.Where((BMSFile f) => !bmsFilesToBeAdded.Select((BMSFile ff) => ff.path).Contains(f.path, StringComparer.OrdinalIgnoreCase)).Concat(bmsFilesToBeAdded).ToList();
        bmsFilesToBeAdded.Select((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path)).Distinct().AsParallel()
            .ForAll(delegate (string dir)
            {
                bmsFolderAllFileList.AddDir(dir);
            });
        return list;
    }

    private void searchEstimatedInstallationDirectory(IEnumerable<BMSFile> bmsFiles, bool asParallel = true, bool fixMode = false)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                try
                {
                    if (bmsFiles.Count() == 0 || bmsFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
                    {
                        return;
                    }
                    foreach (BMSFile bmsFile in bmsFiles)
                    {
                        bmsFile.status |= BMSFile.BMSFileStatus.SEARCHING;
                    }
                    BMSFile targetBMSInfo = (from bmsFile in bmsFiles
                                             where bmsFile != null && (fixMode || !BMSFiles.Select((BMSFile i) => i.hash).Contains(bmsFile.hash))
                                             where fixMode || bmsFile.maintenanceInfo == null || bmsFile.maintenanceInfo.GetWAVHealth() <= innerWavHealthThreshForNormalBMSFile
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
                    ParallelQuery<string> source = (asParallel ? bmsFolderAllFileList.Keys.AsParallel() : bmsFolderAllFileList.Keys.AsParallel().WithDegreeOfParallelism(1));
                    uint[] curDirFileNameHash = (fixMode ? null : BMSDirectoryFileNameHash.GetFileNameHashArray(Path.GetDirectoryName(targetBMSInfo.path)));
                    List<BMSFileMaintenanceInfo> source2 = (from m in (from m in source.Where((string dir) => !dir.Equals(Path.GetDirectoryName(targetBMSInfo.path), StringComparison.OrdinalIgnoreCase)).Select(delegate (string altdir)
                            {
                                BMSFileMaintenanceInfo bMSFileMaintenanceInfo = new BMSFileMaintenanceInfo(targetBMSInfo)
                                {
                                    path = Path.Combine(altdir, Path.GetFileName(targetBMSInfo.path))
                                };
                                targetBMSInfo.SetHealthStatus(bmsFolderAllFileList, forceUpdate: false, memClear: false, bMSFileMaintenanceInfo, altdir, curDirFileNameHash);
                                return bMSFileMaintenanceInfo;
                            })
                                                                       where m.GetWAVHealth() > innerWavHealthThreshForNormalBMSFile && (!fixMode || m.GetBGAHealth() >= targetBMSInfo.maintenanceInfo.GetBGAHealth())
                                                                       select m).ToList().Concat(new BMSFileMaintenanceInfo[1] { targetBMSInfo.maintenanceInfo }).Distinct()
                                                            orderby m.GetWAVHealth() descending, m.GetBGAHealth() descending, m.GetMovieHealth() descending, m.GetOptIMGHealth() descending, (from file in FastDirectoryEnumerator.GetFileNames(DirectoryExt.GetDirectoryNameSimple(m.path))
                                                                                                                                                                                              where BMSFile.wavExtensions.Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                                                                                                                                                                              select file).Count(), (m == targetBMSInfo.maintenanceInfo) ? 1 : 0 descending
                                                            select m).ToList();
                    if (source2.Count() > 0)
                    {
                        BMSFileMaintenanceInfo candidate = source2.First();
                        if (candidate == targetBMSInfo.maintenanceInfo)
                        {
                            return;
                        }
                        try
                        {
                            string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(BMSFiles.First((BMSFile i) => DirectoryExt.GetDirectoryNameSimple(candidate.path).Equals(DirectoryExt.GetDirectoryNameSimple(i.path), StringComparison.OrdinalIgnoreCase)).path);
                            targetBMSInfo.instl_dst = directoryNameSimple;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(targetBMSInfo.instl_dst))
                    {
                        bmsFiles.ToList().ForEach(delegate (BMSFile bmsFile)
                        {
                            bmsFile.instl_dst = targetBMSInfo.instl_dst;
                        });
                    }
                }
                finally
                {
                    foreach (BMSFile bmsFile2 in bmsFiles)
                    {
                        bmsFile2.status &= ~BMSFile.BMSFileStatus.SEARCHING;
                    }
                }
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(BMSPackage package)
    {
        if (BMSPackagesPending.Contains(package))
        {
            List<BMSFile> bMSFiles = package.BMSFiles;
            if (!bMSFiles.Select(delegate (BMSFile bmsInfo)
            {
                if (BMSFiles.Select((BMSFile x) => x.hash).Contains(bmsInfo.hash))
                {
                    bmsInfo.warning = "インストールされています";
                    return true;
                }
                return false;
            }).ToList().All((bool b) => b))
            {
                searchEstimatedInstallationDirectory(bMSFiles);
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(BMSFile bmsFile, bool asParallel = true, bool fixMode = false)
    {
        searchEstimatedInstallationDirectory(new BMSFile[1] { bmsFile }, asParallel, fixMode);
    }

    public void InstallBMSPackageForce(BMSPackage package)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (!BMSPackagesPending.Contains(package) || BMSFiles == null)
                        {
                            return;
                        }
                        List<BMSFile> bMSFiles = package.BMSFiles;
                        if (bMSFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)) && MessageBox.Show("インストール先が推定されていますが、" + Environment.NewLine + "通常インストールを実行しようとしています。" + Environment.NewLine + "インストールは独立したフォルダに行われます。" + Environment.NewLine + "続行しますか？", "通常インストール機能の通知", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.No)
                        {
                            return;
                        }
                        string path = package.path;
                        if (installBMSPackages(new BMSPackage[1] { package }).Count() == 0)
                        {
                            using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                            {
                                lR2SongDBExtended.BeginTransaction();
                                lR2SongDBExtended.Delete<LR2SongDBExtended.install>(path);
                                lR2SongDBExtended.Commit();
                            }
                            BMSPackagesPending.RemoveExt(package);
                            bMSFiles.ForEach(delegate (BMSFile bmsFile)
                            {
                                bmsFile.instl_dst = null;
                            });
                        }
                    }
                }
            }
        }
    }

    public void InstallBMSPackageToEstimatedDir(BMSPackage package)
    {
        List<BMSFile> list = new List<BMSFile>();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (!BMSPackagesPending.Contains(package) || BMSFiles == null)
                        {
                            return;
                        }
                        list = package.BMSFiles;
                        if (list.Any((BMSFile bmsInfo) => string.IsNullOrWhiteSpace(bmsInfo.instl_dst)) || list.Select(delegate (BMSFile bmsInfo)
                        {
                            if (BMSFiles.Select((BMSFile x) => x.hash).Contains(bmsInfo.hash))
                            {
                                bmsInfo.warning = "インストールされています";
                                return true;
                            }
                            return false;
                        }).ToList().All((bool b) => b))
                        {
                            return;
                        }
                        string installationDirectory = list.Select((BMSFile bmsInfo) => bmsInfo.instl_dst).First();
                        string path = package.path;
                        if (installBMSPackages(new BMSPackage[1] { package }, installationDirectory).Count() == 0)
                        {
                            using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
                            {
                                lR2SongDBExtended.BeginTransaction();
                                lR2SongDBExtended.Delete<LR2SongDBExtended.install>(path);
                                lR2SongDBExtended.Commit();
                            }
                            foreach (BMSFile bMSFile in package.BMSFiles)
                            {
                                bMSFile.instl_dst = null;
                            }
                        }
                        BMSPackagesPending.RemoveExt(package);
                    }
                }
            }
        }
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

    public void RemoveBMSPackagesPending(IEnumerable<BMSPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFilesPendingInstall.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    List<BMSPackage> list = BMSPackagesPending.Remove(packages);
                    if (list.Count == 0)
                    {
                        return;
                    }
                    using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                    lR2SongDBExtended.BeginTransaction();
                    foreach (BMSPackage item in list)
                    {
                        lR2SongDBExtended.Delete<LR2SongDBExtended.install>(item.path);
                    }
                    lR2SongDBExtended.Commit();
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
                    BMSPackagesPending.Clear();
                    using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
                    lR2SongDBExtended.BeginTransaction();
                    lR2SongDBExtended.DeleteAll<LR2SongDBExtended.install>();
                    lR2SongDBExtended.Commit();
                }
            }
        }
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

    public void AddReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (entries == null)
        {
            entries = table.entries;
        }
        if (BMSFiles != null && BMSFiles.Count > 0)
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                addReferenceBMSTables(table, entries, BMSFiles);
            }
        }
        if (BMSPackagesPending == null || BMSPackagesPending.Count <= 0)
        {
            return;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            addReferenceBMSTables(table, entries, BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles));
        }
    }

    public void AddReferenceBMSTables(IEnumerable<BMSTable> tables, IEnumerable<BMSFile> files = null)
    {
        Action<IEnumerable<BMSFile>> action = delegate (IEnumerable<BMSFile> l)
        {
            tables.AsParallel().ForAll(delegate (BMSTable table)
            {
                addReferenceBMSTables(table, table.entries, l);
            });
        };
        if (files == null)
        {
            NLogWrapper.DebuggerLogger?.Trace("ReferenceBMSTableStart");
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
            NLogWrapper.DebuggerLogger?.Trace("ReferenceBMSTableEnd");
            return;
        }
        using (rwlockBMSFilesPendingInstall.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                action(files);
            }
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

    private void addReferenceBMSTables(BMSTable table, IEnumerable<BMSTableEntry> entries, IEnumerable<BMSFile> files)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            AddReferenceBMSTables(table, from f in files
                                         join t in from e in table.entries
                                                   where !string.IsNullOrWhiteSpace(e.md5) && !e.is_removed
                                                   select e on f.hash equals t.md5
                                         select f);
        }
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
            s = "新しいフォルダー";
        }
        while (encoding.GetByteCount(parentDir + Path.DirectorySeparatorChar + s + Path.DirectorySeparatorChar + longestFileName) > num || encoding.GetByteCount(s) > num2)
        {
            if (s.Length <= 1)
            {
                throw new PathTooLongException("パスが長過ぎます: " + Environment.NewLine + parentDir + Path.DirectorySeparatorChar + s + Path.DirectorySeparatorChar + longestFileName);
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
                    if (!moveBMSPackageFiles(repackage, dst, showMessageBoxOnInstallFail: false, deleteAllContents: true))
                    {
                        DispatcherMessageBox.Show("BMSフォルダのマージ中にエラーが発生したため中断しました。" + Environment.NewLine + "移動元と移動先のパスに正常にアクセスできるか確認して下さい。" + Environment.NewLine + "移動元: " + src + Environment.NewLine + "移動先: " + dst, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                    BMSFiles = BMSFiles.Where((BMSFile f) => !repackage.BMSFiles.Select((BMSFile ff) => ff.path).Contains(f.path, StringComparer.OrdinalIgnoreCase)).Concat(repackage.BMSFiles).ToList();
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
                int i;
                for (i = 0; i < files.Count; i++)
                {
                    BMSPackage bMSPackage = new BMSPackage(files[i])
                    {
                        delete_parent = false
                    };
                    string path = files[i].path;
                    if (!moveBMSPackageFiles(bMSPackage, bMSPackage.BMSFiles[0].instl_dst))
                    {
                        continue;
                    }
                    if (bMSPackage.BMSFiles.Count == 0)
                    {
                        if (DispatcherMessageBox.Show("同一のBMSファイルが既にインストールされているため" + Environment.NewLine + "再インストールがスキップされました。" + Environment.NewLine + "対象のファイルをごみ箱へ移動しますか?" + Environment.NewLine + Environment.NewLine + "再インストール対象: " + Environment.NewLine + files[i].path + Environment.NewLine + Environment.NewLine + "インストール済み: " + Environment.NewLine + string.Join(Environment.NewLine, from x in BMSFiles.Where((BMSFile f) => f.hash == files[i].hash).Except(new BMSFile[1] { files[i] })
                                                                                                                                                                                                                                                                                                                                                                                                    select x.path) + Environment.NewLine + Environment.NewLine, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                        {
                            RemoveBMSFiles(new BMSFile[1] { files[i] });
                        }
                        continue;
                    }
                    files[i].instl_dst = null;
                    files[i].SetHealthStatus(bmsFolderAllFileList, forceUpdate: true);
                    checkBMSFileNeedToBeFixedAndSetWarnings(files[i]);
                    replaceBMSFilePath(files[i], files[i].path, path);
                    RaisePropertyChanged(() => BMSFilesNeedToBeFixed);
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
                        DispatcherMessageBox.Show("ドライブ直下にあるBMSファイルはスキップされます。", "確認", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
                            DispatcherMessageBox.Show("リネーム中に下記のエラーが発生しました。" + Environment.NewLine + "操作をスキップします" + Environment.NewLine + Environment.NewLine + "対象: " + folder + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
            DispatcherMessageBox.Show("ルートフォルダをリネームすることは出来ません" + Environment.NewLine + Environment.NewLine + "対象: " + srcDir, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
            DispatcherMessageBox.Show("対象のフォルダが存在しないためリネームを中止しました。" + Environment.NewLine + "対象: " + srcDir, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
                        DispatcherMessageBox.Show("移動先ルートフォルダが存在しません。" + Environment.NewLine + dstDir, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                        DispatcherMessageBox.Show("ドライブ直下にあるBMSファイルのルートパスを" + Environment.NewLine + "変更することは出来ません。処理はスキップされます。", "確認", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
            DispatcherMessageBox.Show("移動先フォルダが既に存在するため中止しました。" + Environment.NewLine + "移動元: " + srcDir + Environment.NewLine + "移動先: " + dstDir, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        try
        {
            FileSystem.MoveDirectory(srcDir, dstDir);
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
        catch (Exception ex)
        {
            DispatcherMessageBox.Show("フォルダの移動に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + "移動元: " + srcDir + Environment.NewLine + "移動先: " + dstDir + Environment.NewLine + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
                    DispatcherMessageBox.Show("変更先ファイルが既に存在するため中止しました。" + Environment.NewLine + "変更元: " + bmsFile.path + Environment.NewLine + "変更先: " + dstPath, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    return;
                }
                try
                {
                    FileSystem.MoveFile(bmsFile.path, dstPath);
                }
                catch (Exception ex)
                {
                    DispatcherMessageBox.Show("BMSファイルの移動に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + "移動元: " + bmsFile.path + Environment.NewLine + "移動先: " + dstPath + Environment.NewLine + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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

    public void RenameBMSFilesExtensions(IEnumerable<BMSFile> bmsFiles, string newExt, bool? unregister = false)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                List<BMSFile> list = bmsFiles.ToList();
                foreach (BMSFile item in list.Where((BMSFile f) => File.Exists(f.path)))
                {
                    string text = Path.Combine(Path.GetDirectoryName(item.path), Path.GetFileNameWithoutExtension(item.path) + newExt);
                    if (File.Exists(text) || Directory.Exists(text))
                    {
                        DispatcherMessageBox.Show("変更先ファイルが既に存在するため中止しました。" + Environment.NewLine + "変更元: " + item.path + Environment.NewLine + "変更先: " + text, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    }
                    else
                    {
                        MoveBMSFile(item, text, (unregister == false) ? new bool?(false) : ((bool?)null));
                    }
                }
                if (unregister == true)
                {
                    unregisterBMSFiles(list);
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
                    List<BMSFile> list = new List<BMSFile>();
                    foreach (IGrouping<string, BMSFile> fGrp in from g in bmsFiles.GroupBy((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path), StringComparer.OrdinalIgnoreCase)
                                                                orderby g.Key.Length descending
                                                                select g)
                    {
                        if (BMSFiles.Where((BMSFile f) => f.path.StartsWith(fGrp.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Except(list).Count() == fGrp.Count() && DispatcherMessageBox.Show("削除を続行すると対象のフォルダにはBMSファイルが" + Environment.NewLine + "含まれなくなります。フォルダごと削除しますか?" + Environment.NewLine + "(その他のファイルは含まれている場合があります)" + Environment.NewLine + Environment.NewLine + "対象: " + Environment.NewLine + fGrp.Key, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                        {
                            if (!Directory.Exists(fGrp.Key))
                            {
                                continue;
                            }
                            try
                            {
                                FileSystem.DeleteDirectory(fGrp.Key, UIOption.OnlyErrorDialogs, sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                                foreach (string item in bmsFolderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(fGrp.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                                {
                                    bmsFolderAllFileList.RemoveDir(item);
                                }
                                foreach (BMSFile item2 in BMSPackagesPending.SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat(BMSFiles.Where((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst))))
                                {
                                    if (!string.IsNullOrWhiteSpace(item2.instl_dst) && (item2.instl_dst + Path.DirectorySeparatorChar).StartsWith(fGrp.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    {
                                        item2.instl_dst = null;
                                    }
                                }
                                List<BMSPackage> items = BMSPackagesInstalled.Where((BMSPackage pkg) => (pkg.path + Path.DirectorySeparatorChar).StartsWith(fGrp.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
                                BMSPackagesInstalled.Remove(items);
                            }
                            catch (Exception ex)
                            {
                                DispatcherMessageBox.Show("フォルダの削除またはごみ箱への移動に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + fGrp.Key + Environment.NewLine + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                continue;
                            }
                            list.AddRange(fGrp);
                            continue;
                        }
                        foreach (BMSFile item3 in fGrp)
                        {
                            try
                            {
                                if (File.Exists(item3.path))
                                {
                                    FileSystem.DeleteFile(item3.path, UIOption.OnlyErrorDialogs, sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                                    list.Add(item3);
                                }
                            }
                            catch (Exception ex2)
                            {
                                DispatcherMessageBox.Show("BMSファイルの削除またはごみ箱への移動に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + item3.path + Environment.NewLine + Environment.NewLine + ex2.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                    }
                    unregisterBMSFiles(list);
                }
            }
        }
    }

    private void unregisterBMSFiles(List<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        BMSFiles = BMSFiles.Except(bmsFiles).ToList();
        using (rwlockSongDBMaintenance.GetWriterGuard())
        {
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSFile bmsFile in bmsFiles)
            {
                lR2SongDBExtended.Delete<LR2SongDB.song>(bmsFile.path);
                lR2SongDBExtended.Delete<LR2SongDBExtended.maintenance>(bmsFile.path);
            }
            lR2SongDBExtended.Commit();
        }
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
            throw new FileNotFoundException("変更先のファイルパスと一致するファイルが見つかりませんでした", newPath);
        }
        if (!string.IsNullOrWhiteSpace(oldPath) && !bmsFile.path.Equals(newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException("oldPathが指定されてしますが、bmsFile.pathとnewPathが一致しません");
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
            throw new DirectoryNotFoundException("変更先のディレクトリが見つかりませんでした: " + newFolderPath);
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
