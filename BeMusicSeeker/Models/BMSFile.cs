using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet.EventListeners;
using Ribbit.Threading;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class BMSFile : LR2SongDB.song
{
    private sealed class BulkLoadNotificationScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                if (suppressPropertyChangedDepth > 0)
                {
                    suppressPropertyChangedDepth--;
                }
            }
        }
    }

    [ThreadStatic]
    private static int suppressPropertyChangedDepth;

    private static bool IsPropertyChangedSuppressed => suppressPropertyChangedDepth > 0;

    public static IDisposable SuppressPropertyChangedScope()
    {
        suppressPropertyChangedDepth++;
        return new BulkLoadNotificationScope();
    }

    protected new void RaisePropertyChanged(string propertyName)
    {
        if (!IsPropertyChangedSuppressed)
        {
            base.RaisePropertyChanged(propertyName);
        }
    }

    protected new void RaisePropertyChanged<T>(Expression<Func<T>> propertyExpression)
    {
        if (!IsPropertyChangedSuppressed)
        {
            base.RaisePropertyChanged(propertyExpression);
        }
    }

    [Flags]
    public enum BMSFileStatus
    {
        NONE = 0,
        PLAY = 1,
        LOADING = 2,
        PAUSE = 4,
        FORWARD = 8,
        BACKWARD = 0x10,
        PLAYALL = 0x1F,
        SEARCHING = 0x400,
        SCORE_UNSENT = 0x800
    }

    private string _warning;

    private string _instl_dst;

    private BMSFileStatus _status;

    private bool _isHashDuplicated;

    private bool _hasZeroNoteMismatchWarning;

    private string _cachedComposedTitle;

    private string _cachedComposedTitleSource;

    private string _cachedComposedSubtitleSource;

    private PropertyChangedEventListener listenerForMaintenanceInfo;

    private BMSFileMaintenanceInfo _maintenanceInfo;

    private object lockObject = new object();

    private ReaderWriterLockSlim rwlockRefTables = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

    private List<BMSTable> refTables = new List<BMSTable>();

    private string refTablesSymbolsCache = string.Empty;

    private string refTablesNamesCache;

    private PropertyChangedEventListener listenerForBMSScore;

    private BMSScore _bmsScore;

    private uint[] localWAVfilesNameHashArray;

    private uint[] localBGAfilesNameHashArray;

    private uint[] localBGAfilesMovieNameHashArray;

    private List<string> nonlocalWAVfiles;

    private List<string> nonlocalBGAfiles;

    private List<string> nonlocalBGAfilesMovie;

    private Dictionary<List<string>, SimpleDirectoryStructure> directoryStructureCache;

    private ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim();

    private object filesCacheLock = new object();

    private static Regex chRegex = new Regex("^[\\s\u3000]*#[0-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex commentRegex = new Regex("^[\\s\u3000]*[^#]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex whitespaceRegex = new Regex("^[\\s\u3000]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex titleRegex = new Regex("^[\\s\u3000]*#TITLE\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex subtitleRegex = new Regex("^[\\s\u3000]*#SUBTITLE\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex genreRegex = new Regex("^[\\s\u3000]*#GENRE\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex artistRegex = new Regex("^[\\s\u3000]*#ARTIST\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex subartistRegex = new Regex("^[\\s\u3000]*#SUBARTIST\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex stagefileRegex = new Regex("^[\\s\u3000]*#STAGEFILE\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex backbmpRegex = new Regex("^[\\s\u3000]*#BACKBMP\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex bannerRegex = new Regex("^[\\s\u3000]*#BANNER\\s+(.*?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex playlevelRegex = new Regex("^[\\s\u3000]*#PLAYLEVEL\\s+([0-9]+.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex difficultyRegex = new Regex("^[\\s\u3000]*#DIFFICULTY\\s+([0-9]+.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex rankRegex = new Regex("^[\\s\u3000]*#RANK\\s+([0-9]+.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex wavfileRegex = new Regex("^[\\s\u3000]*#WAV[A-Z0-9]{2}(?>\\s+)(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex bgafileRegex = new Regex("^[\\s\u3000]*#BMP[A-Z0-9]{2}(?>\\s+)(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Note: Although ':' is the common delimiter in BMS channel lines, some charts used in the wild
    // are accepted by LR2/beatoraja even when the command/value boundary is written with whitespace only.
    // Zero-note detection follows that de facto parsing behavior and treats ':' or whitespace runs as delimiters.
    private static Regex visibleObjectChRegex = new Regex("^[\\s\u3000]*#[0-9]{3}[12][1-9A-Z][:\\s\u3000]+[\\s0]*[^\\s0]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex objectCh11Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]1\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh12Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]2\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh13Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]3\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh14Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]4\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh15Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]5\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh16Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]6\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh17Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]7\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh18Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]8\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh19Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[13]9\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh21Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]1\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh22Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]2\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh23Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]3\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh24Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]4\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh25Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]5\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh26Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]6\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh27Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]7\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh28Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]8\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex objectCh29Regex = new Regex("^[\\s\u3000]*#[0-9]{3}[24]9\\s*:[\\s0]*[^\\s0]", RegexOptions.Compiled);

    private static Regex spaceAndReturnPattern = new Regex("[\\s\\r\\n]", RegexOptions.Compiled);

    private static Regex asciiPattern = new Regex("[\\p{IsBasicLatin}]+", RegexOptions.Compiled);

    private static Regex exceptAsciiPattern = new Regex("[^\\p{IsBasicLatin}]", RegexOptions.Compiled);

    private static Regex japanese2charasPattern = new Regex("[\\p{IsCJKUnifiedIdeographs}々ぁ-んァ-ヶ！-＠、。]{2,}", RegexOptions.Compiled);

    private static Regex hangul2charasPattern = new Regex("[\\p{IsHangulSyllables}]{2,}", RegexOptions.Compiled);

    private static Regex genericKanaSymbolRule = new Regex("([ｦ-ｯﾞﾟ])\\1", RegexOptions.Compiled);

    private static Regex semivoicedSoundSymbolRule = new Regex("[^ﾊ-ﾎ]ﾟ", RegexOptions.Compiled);

    private static Regex voicedSoundSymbolRule = new Regex("[^ｶ-ﾄﾊ-ﾎ]ﾞ", RegexOptions.Compiled);

    private static Regex contractedSoundSymbolRule = new Regex("[^ｷｼﾁﾆﾋﾐﾘﾞﾟ][ｬｭｮ]", RegexOptions.Compiled);

    private static Regex allKanaSymbolRule = new Regex("[ｦ-ｯﾞﾟ][ｦ-ｯﾞﾟ]|[^ﾊ-ﾎ]ﾟ|[^ｶ-ﾄﾊ-ﾎ]ﾞ|[^ｷｼﾁﾆﾋﾐﾘﾞﾟ][ｬｭｮ]", RegexOptions.Compiled);

    private static Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static Encoding koreanEnc = Encoding.GetEncoding("ks_c_5601-1987", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static Encoding utf8Enc = Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    public static readonly string[] bmsExtensions = new string[4] { ".bme", ".bms", ".bml", ".pms" };

    public static readonly string wavExtensionBase = ".wav";

    public static readonly string bgaImageExtensionBase = ".png";

    public static readonly string[] bgaImageExtensionsExtend = new string[2] { ".bmp", ".jpg" };

    public static readonly string[] wavExtensionsExtend = new string[2] { ".ogg", ".mp3" };

    public static readonly string[] wavExtensions = new string[1] { wavExtensionBase }.Concat(wavExtensionsExtend).ToArray();

    public static readonly string[] bgaImageExtensions = new string[1] { bgaImageExtensionBase }.Concat(bgaImageExtensionsExtend).ToArray();

    public static readonly Regex wavExtensionsExtendRegex = new Regex("(\\" + string.Join("|\\", wavExtensionsExtend) + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Regex bgaImageExtensionsExtendRegex = new Regex("(\\" + string.Join("|\\", bgaImageExtensionsExtend) + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly string[] bgaMovieExtensions = new string[8] { ".mpg", ".mpeg", ".mp4", ".m4v", ".mp4v", ".avi", ".wmv", ".mov" };

    public static readonly string[] bgaAllExtensions = bgaImageExtensions.Concat(bgaMovieExtensions).ToArray();

    private static readonly char[] invalidPathCharas = Path.GetInvalidPathChars();

    public override string hash
    {
        get
        {
            return base.hash;
        }
        protected set
        {
            if (!(base.hash == value))
            {
                base.hash = value;
                RaisePropertyChanged("hash");
            }
        }
    }

    public virtual string sha256 => string.Empty;

    public virtual string Title
    {
        get
        {
            return GetComposedTitle();
        }
        protected set
        {
            if (!(Title == value))
            {
                _title = value;
                _subtitle = "";
                _cachedComposedTitleSource = _title ?? string.Empty;
                _cachedComposedSubtitleSource = string.Empty;
                _cachedComposedTitle = _cachedComposedTitleSource;
                RaisePropertyChanged("Title");
            }
        }
    }

    public virtual string Artist
    {
        get
        {
            return (string.IsNullOrWhiteSpace(_subartist) ? _artist : (_artist + " " + _subartist)) ?? "";
        }
        protected set
        {
            if (!(Artist == value))
            {
                _artist = value;
                _subartist = "";
                RaisePropertyChanged("Artist");
            }
        }
    }

    private string GetComposedTitle()
    {
        string currentTitle = _title ?? string.Empty;
        string currentSubtitle = _subtitle ?? string.Empty;
        if (_cachedComposedTitle == null || !string.Equals(_cachedComposedTitleSource, currentTitle, StringComparison.Ordinal) || !string.Equals(_cachedComposedSubtitleSource, currentSubtitle, StringComparison.Ordinal))
        {
            // NOTE:
            // 20万件規模の一覧ソートでは Title getter が大量に呼ばれるため、
            // 毎回の string 連結を避けるために _title/_subtitle の組をキーにキャッシュします。
            _cachedComposedTitleSource = currentTitle;
            _cachedComposedSubtitleSource = currentSubtitle;
            _cachedComposedTitle = string.IsNullOrWhiteSpace(currentSubtitle) ? currentTitle : (currentTitle + " " + currentSubtitle);
        }
        return _cachedComposedTitle;
    }

    public int? notes
    {
        get
        {
            return base.karinotes;
        }
        protected set
        {
            base.karinotes = value;
        }
    }

    public virtual string Level
    {
        get
        {
            return base.level.ToString();
        }
        set
        {
            throw new NotImplementedException();
        }
    }

    public virtual string Folder
    {
        get
        {
            return Path.GetFileName(Path.GetDirectoryName(path));
        }
        set
        {
            throw new NotImplementedException();
        }
    }

    public override string path
    {
        get
        {
            return base.path;
        }
        set
        {
            base.path = value;
            if (hash != null)
            {
                maintenanceInfo.path = value;
            }
            RaisePropertyChanged(() => Folder);
        }
    }

    public HashSet<string> WAVfiles { get; set; }

    public HashSet<string> BGAfiles { get; set; }

    public virtual string warning
    {
        get
        {
            return _warning ?? string.Empty;
        }
        set
        {
            if (!(_warning == value))
            {
                _warning = value;
                RaisePropertyChanged("warning");
                RaisePropertyChanged(() => DisplayWarning);
            }
        }
    }

    public virtual bool HasZeroNoteMismatchWarning
    {
        get
        {
            return _hasZeroNoteMismatchWarning;
        }
        set
        {
            if (_hasZeroNoteMismatchWarning != value)
            {
                _hasZeroNoteMismatchWarning = value;
                RaisePropertyChanged("HasZeroNoteMismatchWarning");
                RaisePropertyChanged(() => HasHighlightedWarning);
                RaisePropertyChanged(() => DisplayWarning);
            }
        }
    }

    public virtual bool HasHighlightedWarning => IsHashDuplicated || HasZeroNoteMismatchWarning;

    public virtual string DisplayWarning
    {
        get
        {
            if (!HasZeroNoteMismatchWarning)
            {
                return warning;
            }
            if (string.IsNullOrWhiteSpace(warning))
            {
                return Resources.Warning_ZeroNoteMismatch;
            }
            return warning + Environment.NewLine + Resources.Warning_ZeroNoteMismatch;
        }
    }

    public virtual string instl_dst
    {
        get
        {
            return _instl_dst;
        }
        set
        {
            if (!(_instl_dst == value))
            {
                _instl_dst = value;
                RaisePropertyChanged("instl_dst");
            }
        }
    }

    public virtual BMSFileStatus status
    {
        get
        {
            return _status;
        }
        set
        {
            if (_status != value)
            {
                _status = value;
                RaisePropertyChanged("status");
            }
        }
    }

    public string encoding => maintenanceInfo.encoding;

    public int? WAVHealth => maintenanceInfo.WAVHealth;

    public int? BGAHealth => maintenanceInfo.BGAHealth;

    public int? MovieHealth => maintenanceInfo.MovieHealth;

    public bool? StagefileHealth => maintenanceInfo.StagefileHealth;

    public bool? BannerHealth => maintenanceInfo.BannerHealth;

    public bool? BackbmpHealth => maintenanceInfo.BackbmpHealth;

    public ClearType clear
    {
        get
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ClearType.NO_SONG;
            }
            if (bmsScore == null)
            {
                return ClearType.NO_PLAY;
            }
            if (bmsScore.clear >= ClearType.EASY && rank == RankType.INVALID)
            {
                return ClearType.INVALID;
            }
            return bmsScore.clear;
        }
    }

    public RankType rank
    {
        get
        {
            if (bmsScore != null)
            {
                if (bmsScore.rank != RankType.INVALID)
                {
                    return bmsScore.rank;
                }
                return RankType.F;
            }
            return RankType.INVALID;
        }
    }

    public int? score
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.score;
            }
            return null;
        }
    }

    public int? rate
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.rate;
            }
            return null;
        }
    }

    public double? rateDouble
    {
        get
        {
            if (bmsScore != null)
            {
                return (double)bmsScore.score / 2.0 / (double)bmsScore.totalnotes;
            }
            return null;
        }
    }

    public int? totalnotes
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.totalnotes;
            }
            return null;
        }
    }

    public int? minbp
    {
        get
        {
            if (bmsScore != null)
            {
                return (bmsScore.minbp == -1) ? bmsScore.totalnotes : bmsScore.minbp;
            }
            return null;
        }
    }

    public int? maxcombo
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.maxcombo;
            }
            return null;
        }
    }

    public int? ranking
    {
        get
        {
            if (bmsScore != null && bmsScore.ranking != 0)
            {
                return bmsScore.ranking;
            }
            return null;
        }
    }

    public int? rankingNum
    {
        get
        {
            if (bmsScore != null && bmsScore.rankingNum != 0)
            {
                return bmsScore.rankingNum;
            }
            return null;
        }
    }

    public string rankingString
    {
        get
        {
            if (bmsScore != null && ranking.HasValue && ranking != -1 && rankingNum.HasValue)
            {
                return bmsScore.ranking.ToString().PadLeft(bmsScore.rankingNum.ToString().Length) + "/" + bmsScore.rankingNum;
            }
            return string.Empty;
        }
    }

    public DateTime? rankingLastupdate
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.rankingLastupdate;
            }
            return null;
        }
    }

    public double? stddevVal
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.stddevVal;
            }
            return null;
        }
    }

    public double? scoreDifficulty
    {
        get
        {
            if (bmsScore != null)
            {
                return bmsScore.scoreDifficulty;
            }
            return null;
        }
    }

    public BMSFileMaintenanceInfo maintenanceInfo
    {
        get
        {
            if (_maintenanceInfo == null)
            {
                _maintenanceInfo = new BMSFileMaintenanceInfo(this);
            }
            return _maintenanceInfo;
        }
        set
        {
            SetMaintenanceInfo(value, suppressPropertyChanged: false, registerEventHandlers: true);
        }
    }

    public virtual bool IsHashDuplicated
    {
        get
        {
            return _isHashDuplicated;
        }
        set
        {
            if (_isHashDuplicated != value)
            {
                _isHashDuplicated = value;
                RaisePropertyChanged("IsHashDuplicated");
                RaisePropertyChanged(() => HasHighlightedWarning);
            }
        }
    }

    public void SetMaintenanceInfo(BMSFileMaintenanceInfo value, bool suppressPropertyChanged = false, bool registerEventHandlers = true)
    {
        if (value == null)
        {
            value = new BMSFileMaintenanceInfo(this);
        }
        if (_maintenanceInfo == value)
        {
            return;
        }
        if (value.hash != hash)
        {
            throw new ArgumentException("maintenanceInfo の MD5 が一致しません。");
        }
        DisposeMaintenanceInfoListener();
        _maintenanceInfo = value;
        if (registerEventHandlers)
        {
            registrateMaintenanceInfoPropertyChangedEventHandlers();
        }
        if (!suppressPropertyChanged)
        {
            RaisePropertyChanged("maintenanceInfo");
        }
    }

    public bool HasMaintenanceInfoHash(string expectedHash)
    {
        if (_maintenanceInfo == null || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }
        return string.Equals(_maintenanceInfo.hash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    internal void NotifyMaintenanceInfoChanged(bool encodingChanged, bool healthChanged)
    {
        if (encodingChanged)
        {
            RaisePropertyChanged(() => encoding);
        }
        if (!healthChanged)
        {
            return;
        }
        RaisePropertyChanged(() => WAVHealth);
        RaisePropertyChanged(() => BGAHealth);
        RaisePropertyChanged(() => MovieHealth);
        RaisePropertyChanged(() => StagefileHealth);
        RaisePropertyChanged(() => BannerHealth);
        RaisePropertyChanged(() => BackbmpHealth);
    }

    public virtual string RefTablesSymbols
    {
        get
        {
            using (new ReaderGuard(rwlockRefTables))
            {
                return refTablesSymbolsCache;
            }
        }
    }

    public virtual string RefTablesNames
    {
        get
        {
            using (new ReaderGuard(rwlockRefTables))
            {
                return refTablesNamesCache;
            }
        }
    }

    public virtual List<BMSTable> RefTables
    {
        get
        {
            using (new ReaderGuard(rwlockRefTables))
            {
                return refTables.ToList();
            }
        }
    }

    public BMSScore bmsScore
    {
        get
        {
            return _bmsScore;
        }
        set
        {
            if (_bmsScore != value)
            {
                if (value != null && value.hash != hash)
                {
                    throw new ArgumentException("bmsScore の MD5 が一致しません。");
                }
                DisposeBmsScoreListener();
                _bmsScore = value;
                registrateBMSScorePropertyChangedEventHandlers();
                RaisePropertyChanged("bmsScore");
                RaisePropertyChanged(() => ranking);
                RaisePropertyChanged(() => rankingNum);
                RaisePropertyChanged(() => rankingString);
                RaisePropertyChanged(() => rankingLastupdate);
                RaisePropertyChanged(() => stddevVal);
                RaisePropertyChanged(() => scoreDifficulty);
            }
        }
    }

    private void registrateMaintenanceInfoPropertyChangedEventHandlers()
    {
        DisposeMaintenanceInfoListener();
        if (maintenanceInfo == null)
        {
            return;
        }
        listenerForMaintenanceInfo = new PropertyChangedEventListener(maintenanceInfo);
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.encoding, delegate
        {
            RaisePropertyChanged(() => encoding);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.WAVHealth, delegate
        {
            RaisePropertyChanged(() => WAVHealth);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.BGAHealth, delegate
        {
            RaisePropertyChanged(() => BGAHealth);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.MovieHealth, delegate
        {
            RaisePropertyChanged(() => MovieHealth);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.StagefileHealth, delegate
        {
            RaisePropertyChanged(() => StagefileHealth);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.BannerHealth, delegate
        {
            RaisePropertyChanged(() => BannerHealth);
        });
        listenerForMaintenanceInfo.RegisterHandler(() => maintenanceInfo.BackbmpHealth, delegate
        {
            RaisePropertyChanged(() => BackbmpHealth);
        });
    }

    public void AddRefTable(BMSTable table)
    {
        AddRefTables(new BMSTable[1] { table });
    }

    public int AddRefTables(IEnumerable<BMSTable> tables, bool suppressPropertyChanged = false)
    {
        if (tables == null)
        {
            return 0;
        }
        bool changed = false;
        int addedCount = 0;
        using (new WriterGuard(rwlockRefTables))
        {
            foreach (BMSTable item in tables)
            {
                if (item == null || refTables.Contains(item))
                {
                    continue;
                }
                refTables.Add(item);
                addedCount++;
                changed = true;
            }
            if (changed)
            {
                refreshRefTablesDisplayCacheUnsafe();
            }
        }
        if (changed && !suppressPropertyChanged)
        {
            raiseRefTablesPropertyChanged(includeRefTables: true);
        }
        return addedCount;
    }

    public void RemoveRefTable(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        bool removed = false;
        using (new WriterGuard(rwlockRefTables))
        {
            if (refTables.Remove(table))
            {
                refreshRefTablesDisplayCacheUnsafe();
                removed = true;
            }
        }
        if (removed)
        {
            raiseRefTablesPropertyChanged(includeRefTables: true);
        }
    }

    internal bool RemoveRefTablesNotIn(ISet<BMSTable> tables, bool suppressPropertyChanged = false)
    {
        bool changed = false;
        using (new WriterGuard(rwlockRefTables))
        {
            if (tables == null)
            {
                if (refTables.Count > 0)
                {
                    refTables.Clear();
                    refreshRefTablesDisplayCacheUnsafe();
                    changed = true;
                }
            }
            else if (refTables.RemoveAll((BMSTable table) => !tables.Contains(table)) > 0)
            {
                refreshRefTablesDisplayCacheUnsafe();
                changed = true;
            }
        }
        if (changed && !suppressPropertyChanged)
        {
            raiseRefTablesPropertyChanged(includeRefTables: true);
        }
        return changed;
    }

    private void registrateBMSScorePropertyChangedEventHandlers()
    {
        DisposeBmsScoreListener();
        if (bmsScore == null)
        {
            return;
        }
        listenerForBMSScore = new PropertyChangedEventListener(bmsScore);
        listenerForBMSScore.RegisterHandler(() => bmsScore.ranking, delegate
        {
            RaisePropertyChanged(() => ranking);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.rankingNum, delegate
        {
            RaisePropertyChanged(() => rankingNum);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.ranking, delegate
        {
            RaisePropertyChanged(() => rankingString);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.rankingNum, delegate
        {
            RaisePropertyChanged(() => rankingString);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.rankingLastupdate, delegate
        {
            RaisePropertyChanged(() => rankingLastupdate);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.stddevVal, delegate
        {
            RaisePropertyChanged(() => stddevVal);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.scoreDifficulty, delegate
        {
            RaisePropertyChanged(() => scoreDifficulty);
        });
    }

    internal int ReleaseTransientListeners(bool clearRefTables = false, bool releaseOwnedListeners = true)
    {
        int num = 0;
        if (releaseOwnedListeners)
        {
            num += DisposeMaintenanceInfoListener();
            num += DisposeBmsScoreListener();
        }
        if (clearRefTables)
        {
            using (new WriterGuard(rwlockRefTables))
            {
                if (refTables.Count > 0)
                {
                    num += refTables.Count;
                    refTables.Clear();
                    refreshRefTablesDisplayCacheUnsafe();
                }
            }
        }
        return num;
    }

    private int DisposeMaintenanceInfoListener()
    {
        int result = ((listenerForMaintenanceInfo != null) ? 1 : 0);
        listenerForMaintenanceInfo?.Dispose();
        listenerForMaintenanceInfo = null;
        return result;
    }

    private int DisposeBmsScoreListener()
    {
        int result = ((listenerForBMSScore != null) ? 1 : 0);
        listenerForBMSScore?.Dispose();
        listenerForBMSScore = null;
        return result;
    }

    private void refreshRefTablesDisplayCacheUnsafe()
    {
        refTablesSymbolsCache = string.Join(" ", refTables.Select((BMSTable t) => t.symbol));
        string text = string.Join(Environment.NewLine, refTables.Select((BMSTable t) => t.name));
        refTablesNamesCache = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void raiseRefTablesPropertyChanged(bool includeRefTables)
    {
        RaisePropertyChanged(() => RefTablesSymbols);
        RaisePropertyChanged(() => RefTablesNames);
        if (includeRefTables)
        {
            RaisePropertyChanged(() => RefTables);
        }
    }

    internal void RefreshRefTablesDisplayCache(bool suppressPropertyChanged = false)
    {
        bool symbolsChanged;
        bool namesChanged;
        using (new WriterGuard(rwlockRefTables))
        {
            string refTablesSymbolsCache2 = refTablesSymbolsCache;
            string refTablesNamesCache2 = refTablesNamesCache;
            refreshRefTablesDisplayCacheUnsafe();
            symbolsChanged = !string.Equals(refTablesSymbolsCache2, refTablesSymbolsCache, StringComparison.Ordinal);
            namesChanged = !string.Equals(refTablesNamesCache2, refTablesNamesCache, StringComparison.Ordinal);
        }
        if (!suppressPropertyChanged)
        {
            if (symbolsChanged)
            {
                RaisePropertyChanged(() => RefTablesSymbols);
            }
            if (namesChanged)
            {
                RaisePropertyChanged(() => RefTablesNames);
            }
        }
    }

    internal bool HasRefTable(BMSTable table)
    {
        if (table == null)
        {
            return false;
        }
        using (new ReaderGuard(rwlockRefTables))
        {
            return refTables.Contains(table);
        }
    }

    public void SetEncosingInfo(BMSFileMaintenanceInfo mtInfo = null)
    {
        if (File.Exists(path))
        {
            mtInfo = mtInfo ?? maintenanceInfo;
            mtInfo.encoding = DetectEncodingOfBMSFile(this);
            mtInfo.is_encoding_fixed = false;
        }
    }

    public void SetHealthStatus(BMSDirectoryFileNameHash fListCache = null, bool forceUpdate = false, bool memClear = true, BMSFileMaintenanceInfo mtInfo = null, string altSearchDir = null, uint[] curDirFileNameHashArrayAdd = null)
    {
        if (!File.Exists(path) || (altSearchDir != null && !Directory.Exists(altSearchDir)))
        {
            return;
        }
        mtInfo = mtInfo ?? maintenanceInfo;
        if (!forceUpdate && mtInfo.IsInformationChecked())
        {
            return;
        }
        IDisposable disposable2;
        if (!memClear)
        {
            IDisposable disposable = new ReaderGuard(rwlock);
            disposable2 = disposable;
        }
        else
        {
            IDisposable disposable = new WriterGuard(rwlock);
            disposable2 = disposable;
        }
        using (disposable2)
        {
            lock (filesCacheLock)
            {
                if (WAVfiles == null || BGAfiles == null)
                {
                    SetBMSComponentFilesFromBMSFile(this);
                }
                if (localWAVfilesNameHashArray == null || localBGAfilesNameHashArray == null || localBGAfilesMovieNameHashArray == null || directoryStructureCache == null)
                {
                    ILookup<bool, string> lookup = BGAfiles.ToLookup((string f) => bgaMovieExtensions.Any((string e) => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
                    ILookup<bool, string> lookup2 = lookup[false].ToLookup((string f) => !f.Contains('\\'));
                    ILookup<bool, string> lookup3 = lookup[true].ToLookup((string f) => !f.Contains('\\'));
                    nonlocalBGAfiles = lookup2[false].ToList();
                    localBGAfilesNameHashArray = BMSDirectoryFileNameHash.GetFileNameHashArray(lookup2[true]);
                    nonlocalBGAfilesMovie = lookup3[false].ToList();
                    localBGAfilesMovieNameHashArray = BMSDirectoryFileNameHash.GetFileNameHashArray(lookup3[true]);
                    ILookup<bool, string> lookup4 = WAVfiles.ToLookup((string f) => !f.Contains('\\'));
                    nonlocalWAVfiles = lookup4[false].ToList();
                    localWAVfilesNameHashArray = BMSDirectoryFileNameHash.GetFileNameHashArray(lookup4[true]);
                    directoryStructureCache = new Dictionary<List<string>, SimpleDirectoryStructure>();
                }
            }
            string dir = (string.IsNullOrWhiteSpace(altSearchDir) ? DirectoryExt.GetDirectoryNameSimple(path) : altSearchDir.TrimEnd('\\'));
            uint[] curDirFileNameHashArray = ((fListCache != null) ? fListCache.GetFileNameHashArray(dir, forceUpdate) : BMSDirectoryFileNameHash.GetFileNameHashArray(dir));
            dir = dir.TrimEnd('\\') + Path.DirectorySeparatorChar;
            Func<uint[], List<string>, IEnumerable<string>, int> func = delegate (uint[] localHashSet, List<string> nonlocalFileList, IEnumerable<string> extensions)
            {
                int num = 0;
                if (localHashSet.Length != 0)
                {
                    num = ((curDirFileNameHashArrayAdd == null || curDirFileNameHashArrayAdd.Length == 0 || string.IsNullOrWhiteSpace(altSearchDir)) ? (num + localHashSet.Except(curDirFileNameHashArray).Count()) : (num + localHashSet.Except(curDirFileNameHashArray).Except(curDirFileNameHashArrayAdd).Count()));
                }
                if (nonlocalFileList.Count > 0)
                {
                    SimpleDirectoryStructure simpleDirectoryStructure = null;
                    lock (filesCacheLock)
                    {
                        simpleDirectoryStructure = directoryStructureCache.TryGetValue(nonlocalFileList);
                        if (simpleDirectoryStructure == null)
                        {
                            simpleDirectoryStructure = new SimpleDirectoryStructure(nonlocalFileList);
                            directoryStructureCache[nonlocalFileList] = simpleDirectoryStructure;
                        }
                    }
                    new List<string>();
                    Func<SimpleDirectoryStructure, IEnumerable<string>> fileNotFound = null;
                    fileNotFound = (SimpleDirectoryStructure fileStructures) => (fileStructures.DirName != null) ? ((!Directory.Exists(dir + fileStructures.DirName)) ? fileStructures.GetAllFiles() : fileStructures.FileList.Where(delegate (string file)
                    {
                        try
                        {
                            string fullPath = Path.GetFullPath(dir + file);
                            string dirname = DirectoryExt.GetDirectoryNameSimple(fullPath) + Path.DirectorySeparatorChar;
                            string basename = Path.GetFileNameWithoutExtension(fullPath);
                            return !File.Exists(fullPath) && extensions.All((string ext) => !File.Exists(dirname + basename + ext));
                        }
                        catch
                        {
                            return true;
                        }
                    }).Concat(fileStructures.DirList.SelectMany((SimpleDirectoryStructure dirStr) => fileNotFound(dirStr)))) : fileStructures.FileList.Where(delegate (string file)
                    {
                        try
                        {
                            string fullPath = Path.GetFullPath(dir + file);
                            string dirname = DirectoryExt.GetDirectoryNameSimple(fullPath) + Path.DirectorySeparatorChar;
                            string basename = Path.GetFileNameWithoutExtension(fullPath);
                            return !File.Exists(fullPath) && extensions.All((string ext) => !File.Exists(dirname + basename + ext));
                        }
                        catch
                        {
                            return true;
                        }
                    }).Concat(fileStructures.DirList.SelectMany((SimpleDirectoryStructure dirStr) => fileNotFound(dirStr)));
                    num += fileNotFound(simpleDirectoryStructure).Count();
                }
                return num;
            };
            Func<string, IEnumerable<string>, bool> func2 = delegate (string filename, IEnumerable<string> extensions)
            {
                string text = filename.TrimStart(Path.DirectorySeparatorChar);
                if (text.IndexOfAny(invalidPathCharas) < 0)
                {
                    try
                    {
                        text = Path.GetFullPath(dir + text);
                        string dirname = DirectoryExt.GetDirectoryNameSimple(text) + Path.DirectorySeparatorChar;
                        string basename = Path.GetFileNameWithoutExtension(text);
                        return extensions.Any((string ext) => File.Exists(dirname + basename + ext));
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            };
            mtInfo.wav_files_defined = WAVfiles.Count;
            if (mtInfo.wav_files_defined > 0)
            {
                mtInfo.wav_files_existing = mtInfo.wav_files_defined - func(localWAVfilesNameHashArray, nonlocalWAVfiles, wavExtensions);
            }
            mtInfo.bga_files_defined = localBGAfilesNameHashArray.Length + nonlocalBGAfiles.Count;
            if (mtInfo.bga_files_defined > 0)
            {
                mtInfo.bga_files_existing = mtInfo.bga_files_defined - func(localBGAfilesNameHashArray, nonlocalBGAfiles, bgaImageExtensions);
            }
            mtInfo.movie_files_defined = localBGAfilesMovieNameHashArray.Length + nonlocalBGAfilesMovie.Count;
            if (mtInfo.movie_files_defined > 0)
            {
                mtInfo.movie_files_existing = mtInfo.movie_files_defined - func(localBGAfilesMovieNameHashArray, nonlocalBGAfilesMovie, Enumerable.Empty<string>());
            }
            mtInfo.is_stagefile_defined = !string.IsNullOrWhiteSpace(stagefile);
            if (mtInfo.is_stagefile_defined == true)
            {
                mtInfo.is_stagefile_existing = func2(stagefile, bgaImageExtensions);
            }
            mtInfo.is_backbmp_defined = !string.IsNullOrWhiteSpace(backbmp);
            if (mtInfo.is_backbmp_defined == true)
            {
                mtInfo.is_backbmp_existing = func2(backbmp, bgaImageExtensions);
            }
            mtInfo.is_banner_defined = !string.IsNullOrWhiteSpace(banner);
            if (mtInfo.is_banner_defined == true)
            {
                mtInfo.is_banner_existing = func2(banner, bgaImageExtensions);
            }
            if (forceUpdate)
            {
                mtInfo.is_files_warning_ignored = false;
            }
            if (memClear)
            {
                WAVfiles = null;
                BGAfiles = null;
                localWAVfilesNameHashArray = null;
                localBGAfilesNameHashArray = null;
                localBGAfilesMovieNameHashArray = null;
                nonlocalWAVfiles = null;
                nonlocalBGAfiles = null;
                nonlocalBGAfilesMovie = null;
                directoryStructureCache = null;
            }
        }
    }

    public bool SetNotesIfZeroNote()
    {
        if (IsZeroNoteBMSFile(path))
        {
            notes = 0;
            return true;
        }
        return false;
    }

    public void SetMode()
    {
        BMSFile bMSFile = CreateBMSFileFromFile(path);
        mode = bMSFile.mode;
    }

    public static BMSFile CreateBMSFileFromFile(string filePath, string codepageName = "shift_jis")
    {
        IEnumerable<string> enumerable = File.ReadLines(filePath, Encoding.GetEncoding(codepageName));
        BMSFile bMSFile = new BMSFile();
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool flag = false;
        bool flag2 = false;
        bool flag3 = false;
        bool flag4 = false;
        bool flag5 = false;
        bool flag6 = false;
        bool flag7 = false;
        bool flag8 = false;
        bool flag9 = false;
        bool flag10 = false;
        bool flag11 = false;
        bool flag12 = false;
        bool flag13 = false;
        bool flag14 = false;
        bool flag15 = false;
        bool flag16 = false;
        bool flag17 = false;
        bool flag18 = false;
        foreach (string item in enumerable)
        {
            Match match;
            if (!chRegex.Match(item).Success)
            {
                if ((match = wavfileRegex.Match(item)).Success)
                {
                    string text = match.Groups[1].ToString();
                    if (text.IndexOfAny(invalidPathCharas) < 0)
                    {
                        hashSet.Add(text.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
                    }
                }
                else if ((match = bgafileRegex.Match(item)).Success)
                {
                    string text2 = match.Groups[1].ToString();
                    if (text2.IndexOfAny(invalidPathCharas) < 0)
                    {
                        hashSet2.Add(text2.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
                    }
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.title) && (match = titleRegex.Match(item)).Success)
                {
                    bMSFile.title = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.genre) && (match = genreRegex.Match(item)).Success)
                {
                    bMSFile.genre = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.artist) && (match = artistRegex.Match(item)).Success)
                {
                    bMSFile.artist = match.Groups[1].ToString();
                }
                else if (!bMSFile.level.HasValue && (match = playlevelRegex.Match(item)).Success)
                {
                    try
                    {
                        bMSFile.level = int.Parse(match.Groups[1].ToString());
                    }
                    catch
                    {
                    }
                }
                else if (!bMSFile.difficulty.HasValue && (match = difficultyRegex.Match(item)).Success)
                {
                    try
                    {
                        bMSFile.difficulty = int.Parse(match.Groups[1].ToString());
                    }
                    catch
                    {
                    }
                }
                else if (!bMSFile.judge.HasValue && (match = rankRegex.Match(item)).Success)
                {
                    try
                    {
                        bMSFile.judge = int.Parse(match.Groups[1].ToString());
                    }
                    catch
                    {
                    }
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.subtitle) && (match = subtitleRegex.Match(item)).Success)
                {
                    bMSFile.subtitle = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.subartist) && (match = subartistRegex.Match(item)).Success)
                {
                    bMSFile.subartist = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.banner) && (match = bannerRegex.Match(item)).Success)
                {
                    bMSFile.banner = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.stagefile) && (match = stagefileRegex.Match(item)).Success)
                {
                    bMSFile.stagefile = match.Groups[1].ToString();
                }
                else if (string.IsNullOrWhiteSpace(bMSFile.backbmp) && (match = backbmpRegex.Match(item)).Success)
                {
                    bMSFile.backbmp = match.Groups[1].ToString();
                }
            }
            else if (!flag && (match = objectCh11Regex.Match(item)).Success)
            {
                flag = true;
            }
            else if (!flag2 && (match = objectCh12Regex.Match(item)).Success)
            {
                flag2 = true;
            }
            else if (!flag3 && (match = objectCh13Regex.Match(item)).Success)
            {
                flag3 = true;
            }
            else if (!flag4 && (match = objectCh14Regex.Match(item)).Success)
            {
                flag4 = true;
            }
            else if (!flag5 && (match = objectCh15Regex.Match(item)).Success)
            {
                flag5 = true;
            }
            else if (!flag6 && (match = objectCh16Regex.Match(item)).Success)
            {
                flag6 = true;
            }
            else if (!flag7 && (match = objectCh17Regex.Match(item)).Success)
            {
                flag7 = true;
            }
            else if (!flag8 && (match = objectCh18Regex.Match(item)).Success)
            {
                flag8 = true;
            }
            else if (!flag9 && (match = objectCh19Regex.Match(item)).Success)
            {
                flag9 = true;
            }
            else if (!flag10 && (match = objectCh21Regex.Match(item)).Success)
            {
                flag10 = true;
            }
            else if (!flag11 && (match = objectCh22Regex.Match(item)).Success)
            {
                flag11 = true;
            }
            else if (!flag12 && (match = objectCh23Regex.Match(item)).Success)
            {
                flag12 = true;
            }
            else if (!flag13 && (match = objectCh24Regex.Match(item)).Success)
            {
                flag13 = true;
            }
            else if (!flag14 && (match = objectCh25Regex.Match(item)).Success)
            {
                flag14 = true;
            }
            else if (!flag15 && (match = objectCh26Regex.Match(item)).Success)
            {
                flag15 = true;
            }
            else if (!flag16 && (match = objectCh27Regex.Match(item)).Success)
            {
                flag16 = true;
            }
            else if (!flag17 && (match = objectCh28Regex.Match(item)).Success)
            {
                flag17 = true;
            }
            else if (!flag18 && (match = objectCh29Regex.Match(item)).Success)
            {
                flag18 = true;
            }
        }
        bMSFile.WAVfiles = hashSet;
        bMSFile.BGAfiles = hashSet2;
        bMSFile.hash = getMD5Hash(filePath);
        bMSFile.path = filePath;
        if (bMSFile.path.EndsWith(".pms", StringComparison.OrdinalIgnoreCase))
        {
            bMSFile.mode = 9;
        }
        else if (!flag8 && !flag9 && !flag10 && !flag11 && !flag12 && !flag13 && !flag14 && !flag15 && !flag16 && !flag17 && !flag18)
        {
            if (!flag && !flag2 && !flag3 && !flag4 && !flag5)
            {
                bMSFile.mode = 7;
            }
            else
            {
                bMSFile.mode = 5;
            }
        }
        else if (!flag6 && !flag7 && !flag8 && !flag9 && !flag10 && !flag15 && !flag16 && !flag17 && !flag18)
        {
            bMSFile.mode = 9;
        }
        else if (!flag8 && !flag9 && !flag17 && !flag18)
        {
            bMSFile.mode = 10;
        }
        else if (!flag10 && !flag11 && !flag12 && !flag13 && !flag14 && !flag15 && !flag16 && !flag17 && !flag18)
        {
            bMSFile.mode = 7;
        }
        else
        {
            bMSFile.mode = 14;
        }
        return bMSFile;
    }

    public static void SetBMSComponentFilesFromBMSFile(BMSFile bmsFile, string codepageName = "shift_jis")
    {
        IEnumerable<string> enumerable = File.ReadLines(bmsFile.path, Encoding.GetEncoding(codepageName));
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in enumerable)
        {
            Match match = wavfileRegex.Match(item);
            if (match.Success)
            {
                string text = match.Groups[1].ToString();
                if (text.IndexOfAny(invalidPathCharas) < 0)
                {
                    hashSet.Add(text.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
                }
                continue;
            }
            Match match2 = bgafileRegex.Match(item);
            if (match2.Success)
            {
                string text2 = match2.Groups[1].ToString();
                if (text2.IndexOfAny(invalidPathCharas) < 0)
                {
                    hashSet2.Add(text2.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
                }
            }
        }
        bmsFile.WAVfiles = hashSet;
        bmsFile.BGAfiles = hashSet2;
        bmsFile.hash = getMD5Hash(bmsFile.path);
    }

    private static string getMD5Hash(string filePath)
    {
        MD5 mD = MD5.Create();
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

    public static void ReloadBMSFileWithEncoding(BMSFile bmsFile, string codepageName = "")
    {
        if (string.IsNullOrWhiteSpace(bmsFile.path) || !File.Exists(bmsFile.path))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", bmsFile.path ?? "");
        }
        if (string.IsNullOrWhiteSpace(codepageName))
        {
            codepageName = DetectEncodingOfBMSFile(bmsFile);
            if (codepageName == "unknown")
            {
                codepageName = "shift_jis";
            }
            codepageName.TrimEnd('?');
        }
        BMSFile bMSFile = CreateBMSFileFromFile(bmsFile.path, codepageName);
        bmsFile.Title = bMSFile.Title;
        bmsFile.Artist = bMSFile.Artist;
        bmsFile.genre = bMSFile.genre;
        if (codepageName == "shift_jis")
        {
            bmsFile.adddate = null;
            bmsFile.date = null;
        }
    }

    public static string DetectEncodingOfBMSFile(BMSFile bmsInfo)
    {
        return DetectEncodingOfBMSFile(bmsInfo.path);
    }

    public static string DetectEncodingOfBMSFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", path ?? "");
        }
        try
        {
            string input = File.ReadAllText(path, sjisEnc);
            input = asciiPattern.Replace(input, " ");
            if (string.IsNullOrWhiteSpace(input))
            {
                return "shift_jis";
            }
            if (isInvalidJapaneseKanaString(input))
            {
                return "ks_c_5601-1987?";
            }
            if (japanese2charasPattern.IsMatch(input))
            {
                return "shift_jis?";
            }
        }
        catch
        {
            try
            {
                File.ReadAllText(path, koreanEnc);
                return "ks_c_5601-1987";
            }
            catch
            {
                try
                {
                    File.ReadAllText(path, utf8Enc);
                    return "utf-8";
                }
                catch
                {
                    return "unknown";
                }
            }
        }
        try
        {
            string input2 = File.ReadAllText(path, koreanEnc);
            input2 = spaceAndReturnPattern.Replace(input2, "");
            if (hangul2charasPattern.IsMatch(input2))
            {
                return "ks_c_5601-1987?";
            }
            return "shift_jis?";
        }
        catch
        {
            return "shift_jis";
        }
    }

    public static bool IsZeroNoteBMSFile(string filePath)
    {
        return !File.ReadLines(filePath, Encoding.GetEncoding("shift_jis")).Any((string line) => visibleObjectChRegex.IsMatch(line));
    }

    private static bool isInvalidJapaneseKanaString(string str)
    {
        if (allKanaSymbolRule.IsMatch(str))
        {
            return true;
        }
        return false;
    }
}
