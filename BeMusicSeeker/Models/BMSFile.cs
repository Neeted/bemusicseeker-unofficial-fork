using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet.EventListeners;
using Ribbit.Threading;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models;

/// <summary>
/// `maintenanceInfo` がどの経路で得られた値かを表します。
/// ResourceHealth の正本判定で lazy placeholder と DB/計算済み snapshot を区別するために使います。
/// </summary>
public enum MaintenanceInfoOrigin
{
    /// <summary>
    /// maintenance 情報がまだ materialize されていない状態です。
    /// </summary>
    None,

    /// <summary>
    /// lazy getter などで作られた未検査の placeholder です。
    /// </summary>
    Placeholder,

    /// <summary>
    /// DB の maintenance table から hydrate された snapshot です。
    /// </summary>
    DbHydrated,

    /// <summary>
    /// file diff、導入処理、または明示的な再スキャンで計算された snapshot です。
    /// </summary>
    Calculated
}

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

    internal static BMSFile FromSongTableRawValues(string[] values)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }
        BMSFile file = new BMSFile();
        file._hash = NormalizeMd5HashFromDb(GetRawValue(values, 0));
        file._title = GetRawValue(values, 1);
        file._subtitle = GetRawValue(values, 2);
        file._artist = GetRawValue(values, 3);
        file._subartist = GetRawValue(values, 4);
        file.genre = GetRawValue(values, 5);
        file._tag = GetRawValue(values, 6);
        file._path = GetRawValue(values, 7);
        file.type = ParseNullableIntFromDb(GetRawValue(values, 8));
        file.folder = GetRawValue(values, 9);
        file._stagefile = GetRawValue(values, 10);
        file._banner = GetRawValue(values, 11);
        file._backbmp = GetRawValue(values, 12);
        file.parent = GetRawValue(values, 13);
        file.level = ParseNullableIntFromDb(GetRawValue(values, 14));
        file.difficulty = ParseNullableIntFromDb(GetRawValue(values, 15));
        file.maxbpm = ParseNullableIntFromDb(GetRawValue(values, 16));
        file.minbpm = ParseNullableIntFromDb(GetRawValue(values, 17));
        file.mode = ParseNullableIntFromDb(GetRawValue(values, 18));
        file._judge = ParseNullableIntFromDb(GetRawValue(values, 19));
        file.longnote = ParseNullableIntFromDb(GetRawValue(values, 20));
        file.bga = ParseNullableIntFromDb(GetRawValue(values, 21));
        file.random = ParseNullableIntFromDb(GetRawValue(values, 22));
        file.date = ParseNullableIntFromDb(GetRawValue(values, 23));
        file.favorite = ParseNullableIntFromDb(GetRawValue(values, 24));
        file.txt = ParseNullableIntFromDb(GetRawValue(values, 25));
        file._karinotes = ParseNullableIntFromDb(GetRawValue(values, 26));
        file.adddate = ParseNullableIntFromDb(GetRawValue(values, 27));
        file.exlevel = ParseNullableIntFromDb(GetRawValue(values, 28));
        return file;
    }

    private static string GetRawValue(string[] values, int index)
    {
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static int? ParseNullableIntFromDb(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static string NormalizeMd5HashFromDb(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
        {
            return null;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return null;
            }
        }
        return value.ToLowerInvariant();
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

    private string _instl_dst;

    private string _installDestinationTitle;

    private string _installDestinationArtist;

    private IReadOnlyList<string> _installDestinationSuggestions = Array.Empty<string>();

    private bool _isInstallDestinationSuggestionPopupOpen;

    private BMSFileStatus _status;

    private ChartWarningCollection _warnings;

    private string _cachedComposedTitle;

    private string _cachedComposedTitleSource;

    private string _cachedComposedSubtitleSource;

    private PropertyChangedEventListener listenerForMaintenanceInfo;

    private BMSFileMaintenanceInfo _maintenanceInfo;

    private MaintenanceInfoOrigin maintenanceInfoOrigin;

    private string _sha256;

    private LR2SongDBExtended.chart_info _chartInfo;

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

    private ResourceReferenceHealthCache resourceReferenceHealthCache;

    private ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim();

    private object filesCacheLock = new object();

    // BMS のチャンネル行は ':' 区切りが一般的だが、LR2/beatoraja では空白だけで
    // コマンドと値を区切る譜面も実質的に受け入れられているため、ゼロノート検出でも
    // ':' または空白列を区切りとして扱う。
    // 通常ノート (11-19/21-29) に加え、RDM 記法の LN チャンネル (51-69 系。互換のため 5Z/6Z まで)
    // も可視ノートとして扱う。データ部は 2 桁 object 列として見て、00 だけの行は無視する。
    private static Regex visibleObjectChRegex = new Regex("^[\\s\u3000]*#[0-9]{3}(?:[12][1-9A-Z]|[56][1-9A-Z])(?:[\\s\u3000]*:[\\s\u3000]*|[\\s\u3000]+)(?:[\\s\u3000]*00)*[\\s\u3000]*(?!00)[0-9A-Z]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static Encoding koreanEnc = Encoding.GetEncoding("ks_c_5601-1987", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static Encoding utf8Enc = Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    internal enum EncodingDetectionOutcome
    {
        Other = 0,
        ShiftJis,
        ShiftJisQuestion,
        Korean,
        KoreanQuestion,
        Utf8,
        Unknown
    }

    internal sealed class BmsEncodingDetectionResult
    {
        internal BmsEncodingDetectionResult(
            string encodingName,
            EncodingDetectionOutcome outcome,
            bool fastAscii,
            string decodedText)
        {
            EncodingName = string.IsNullOrWhiteSpace(encodingName) ? "unknown" : encodingName;
            Outcome = outcome;
            FastAscii = fastAscii;
            DecodedText = decodedText;
        }

        public string EncodingName { get; }

        public EncodingDetectionOutcome Outcome { get; }

        public bool FastAscii { get; }

        public string DecodedText { get; }
    }

    public static readonly string[] bmsExtensions = new string[4] { ".bme", ".bms", ".bml", ".pms" };

    public static readonly string wavExtensionBase = ".wav";

    public static readonly string bgaImageExtensionBase = ".png";

    public static readonly string[] bgaImageExtensionsExtend = new string[3] { ".bmp", ".jpg", ".jpeg" };

    public static readonly string[] wavExtensionsExtend = new string[3] { ".ogg", ".mp3", ".flac" };

    public static readonly string[] wavExtensions = new string[1] { wavExtensionBase }.Concat(wavExtensionsExtend).ToArray();

    public static readonly string[] bgaImageExtensions = new string[1] { bgaImageExtensionBase }.Concat(bgaImageExtensionsExtend).ToArray();

    public static readonly Regex wavExtensionsExtendRegex = new Regex("(\\" + string.Join("|\\", wavExtensionsExtend) + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Regex bgaImageExtensionsExtendRegex = new Regex("(\\" + string.Join("|\\", bgaImageExtensionsExtend) + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly string[] bgaMovieExtensions = new string[15] { ".mpg", ".mpeg", ".mp4", ".m4v", ".mp4v", ".avi", ".wmv", ".mov", ".webm", ".mkv", ".m1v", ".m2v", ".3gp", ".flv", ".rm" };

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

    public virtual string sha256
    {
        get
        {
            return _sha256 ?? string.Empty;
        }
        protected set
        {
            string normalized = NormalizeSha256(value);
            if (!string.Equals(_sha256, normalized, StringComparison.Ordinal))
            {
                _sha256 = normalized;
                RaisePropertyChanged("sha256");
            }
        }
    }

    /// <summary>
    /// sha256 で照合した譜面解析メタデータです。
    /// song テーブルの列ではないため SQLite の永続化対象から除外します。
    /// </summary>
    [Ignore]
    public virtual LR2SongDBExtended.chart_info ChartInfo
    {
        get
        {
            return _chartInfo;
        }
        private set
        {
            if (!ReferenceEquals(_chartInfo, value))
            {
                _chartInfo = value;
                RaisePropertyChanged(() => ChartInfo);
                RaiseChartInfoDisplayPropertiesChanged();
            }
        }
    }

    /// <summary>
    /// DB から読み込んだ、またはバックグラウンド解析で生成した譜面解析メタデータを関連付けます。
    /// </summary>
    /// <param name="chartInfo">関連付ける解析メタデータ。</param>
    internal void SetChartInfo(LR2SongDBExtended.chart_info chartInfo)
    {
        ChartInfo = chartInfo;
    }

    /// <summary>
    /// full hydration 用に chart_info を関連付けます。
    /// 一覧全行へ大量通知を流さず、表示中 view の batch refresh に任せます。
    /// </summary>
    /// <param name="chartInfo">関連付ける解析メタデータ。</param>
    /// <returns>値が差し替わった場合は <see langword="true"/>。</returns>
    internal bool SetChartInfoSilently(LR2SongDBExtended.chart_info chartInfo)
    {
        if (ReferenceEquals(_chartInfo, chartInfo))
        {
            return false;
        }
        _chartInfo = chartInfo;
        return true;
    }

    private void RaiseChartInfoDisplayPropertiesChanged()
    {
        RaisePropertyChanged(() => ChartLevelText);
        RaisePropertyChanged(() => ChartLevelSortKey);
        RaisePropertyChanged(() => ChartLevelUndefined);
        RaisePropertyChanged(() => ChartDifficultyText);
        RaisePropertyChanged(() => ChartDifficultySortKey);
        RaisePropertyChanged(() => ChartDifficultyColorKey);
        RaisePropertyChanged(() => ChartDifficultyUndefined);
        RaisePropertyChanged(() => ChartMainBpmText);
        RaisePropertyChanged(() => ChartMainBpmSortKey);
        RaisePropertyChanged(() => ChartMaxBpmText);
        RaisePropertyChanged(() => ChartMaxBpmSortKey);
        RaisePropertyChanged(() => ChartMinBpmText);
        RaisePropertyChanged(() => ChartMinBpmSortKey);
        RaisePropertyChanged(() => ChartDurationText);
        RaisePropertyChanged(() => ChartDurationSortKey);
        RaisePropertyChanged(() => ChartJudgeText);
        RaisePropertyChanged(() => ChartJudgeSortKey);
        RaisePropertyChanged(() => ChartJudgeColorKey);
        RaisePropertyChanged(() => ChartJudgePercentText);
        RaisePropertyChanged(() => ChartFeatureText);
        RaisePropertyChanged(() => ChartFeatureSortKey);
        RaisePropertyChanged(() => ChartNotes);
        RaisePropertyChanged(() => ChartLongNotes);
        RaisePropertyChanged(() => ChartScratchNotes);
        RaisePropertyChanged(() => ChartTotalText);
        RaisePropertyChanged(() => ChartTotalSortKey);
        RaisePropertyChanged(() => ChartTotalUndefined);
        RaisePropertyChanged(() => ChartTotalPerNoteText);
        RaisePropertyChanged(() => ChartTotalPerNoteSortKey);
        RaisePropertyChanged(() => ChartDensityText);
        RaisePropertyChanged(() => ChartDensitySortKey);
        RaisePropertyChanged(() => ChartPeakDensityText);
        RaisePropertyChanged(() => ChartPeakDensitySortKey);
        RaisePropertyChanged(() => ChartEndDensityText);
        RaisePropertyChanged(() => ChartEndDensitySortKey);
        RaisePropertyChanged(() => ChartSoflanCount);
    }

    public virtual string ChartLevelText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.level);

    public virtual double? ChartLevelSortKey => ChartInfo?.level ?? 0;

    public virtual bool ChartLevelUndefined => ChartInfo == null || !ChartInfo.level.HasValue;

    public virtual string ChartDifficultyText => ChartInfoDisplayFormatter.FormatDifficulty(ChartInfo?.difficulty);

    public virtual int? ChartDifficultySortKey => ChartInfo?.difficulty;

    public virtual string ChartDifficultyColorKey => ChartInfoDisplayFormatter.GetDifficultyColorKey(ChartInfo?.difficulty);

    public virtual bool ChartDifficultyUndefined => ChartInfo == null || !ChartInfo.difficulty_defined;

    public virtual string ChartMainBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.mainbpm);

    public virtual double? ChartMainBpmSortKey => ChartInfo?.mainbpm;

    public virtual string ChartMaxBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.maxbpm);

    public virtual double? ChartMaxBpmSortKey => ChartInfo?.maxbpm;

    public virtual string ChartMinBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.minbpm);

    public virtual double? ChartMinBpmSortKey => ChartInfo?.minbpm;

    public virtual string ChartDurationText => ChartInfoDisplayFormatter.FormatDuration(ChartInfo?.length);

    public virtual int? ChartDurationSortKey => ChartInfo?.length;

    public virtual string ChartJudgeText => ChartInfoDisplayFormatter.FormatJudge(ChartInfo?.judge);

    public virtual int? ChartJudgeSortKey => ChartInfo?.judge;

    public virtual string ChartJudgeColorKey => ChartInfoDisplayFormatter.GetJudgeColorKey(ChartInfo?.judge);

    public virtual string ChartJudgePercentText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.judge);

    public virtual string ChartFeatureText => ChartInfo == null ? string.Empty : ChartInfoDisplayFormatter.FormatFeature(ChartInfo.feature);

    public virtual int? ChartFeatureSortKey => ChartInfo?.feature;

    public virtual int? ChartNotes => ChartInfo?.notes;

    public virtual int? ChartLongNotes => ChartInfo?.ln;

    public virtual int? ChartScratchNotes => ChartInfoDisplayFormatter.GetScratchNotes(ChartInfo);

    public virtual string ChartTotalText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.total);

    public virtual double? ChartTotalSortKey => ChartInfo?.total;

    public virtual bool ChartTotalUndefined => ChartInfo == null || !ChartInfo.total_defined;

    public virtual string ChartTotalPerNoteText => ChartInfoDisplayFormatter.FormatFixedTwo(ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo));

    public virtual double? ChartTotalPerNoteSortKey => ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo);

    public virtual string ChartDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.density);

    public virtual double? ChartDensitySortKey => ChartInfo?.density;

    public virtual string ChartPeakDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.peakdensity);

    public virtual double? ChartPeakDensitySortKey => ChartInfo?.peakdensity;

    public virtual string ChartEndDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.enddensity);

    public virtual double? ChartEndDensitySortKey => ChartInfo?.enddensity;

    public virtual int? ChartSoflanCount => ChartInfo?.speedchange_count;

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
            if (hash != null && _maintenanceInfo != null)
            {
                _maintenanceInfo.path = value;
            }
            RaisePropertyChanged(() => Folder);
        }
    }

    public HashSet<string> WAVfiles { get; set; }

    public HashSet<string> BGAfiles { get; set; }

    internal ChartWarningCollection Warnings => _warnings ?? (_warnings = new ChartWarningCollection(this));

    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        _warnings?.RemoveCategory(category);
    }

    internal void ClearWarning(ChartWarningKind kind)
    {
        _warnings?.Remove(kind);
    }

    internal void ClearStructuredWarnings()
    {
        Warnings.Clear();
    }

    internal void SetWarning(ChartWarningKind kind, string message)
    {
        Warnings.Set(ChartWarning.Create(kind, message));
    }

    internal void ReplaceWarningsByCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        Warnings.ReplaceCategory(category, warnings);
    }

    internal void CopyStructuredWarningsFrom(BMSFile source)
    {
        Warnings.ReplaceAll(source?.Warnings.ToStructuredList() ?? Enumerable.Empty<ChartWarning>());
    }

    internal void ReplaceStructuredWarnings(IEnumerable<ChartWarning> warnings)
    {
        Warnings.ReplaceAll(warnings);
    }

    internal void RaiseWarningPresentationChanged()
    {
        RaisePropertyChanged(() => DisplayWarning);
        RaisePropertyChanged(() => WarningDigestText);
        RaisePropertyChanged(() => WarningTooltipText);
        RaisePropertyChanged(() => HasHighlightedWarning);
        RaisePropertyChanged(() => HasZeroNoteMismatchWarning);
        RaisePropertyChanged(() => HasLowConfidenceInstallWarning);
        RaisePropertyChanged(() => IsHashDuplicated);
        RaisePropertyChanged(() => HasChartInfoParseFailureWarning);
    }

    public virtual bool HasZeroNoteMismatchWarning
    {
        get
        {
            return Warnings.Contains(ChartWarningKind.ZeroNoteMismatch);
        }
        set
        {
            if (HasZeroNoteMismatchWarning == value)
            {
                return;
            }
            if (value)
            {
                SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
            }
            else
            {
                ClearWarning(ChartWarningKind.ZeroNoteMismatch);
            }
        }
    }

    public virtual bool HasChartInfoParseFailureWarning => Warnings.Contains(ChartWarningKind.ChartInfoParseFailure);

    public virtual bool HasHighlightedWarning => Warnings.HasHighlightedWarning;

    public virtual bool HasLowConfidenceInstallWarning
    {
        get
        {
            return HasLowConfidenceInstallEstimationWarning();
        }
        set
        {
            if (HasLowConfidenceInstallWarning == value)
            {
                return;
            }
            if (value)
            {
                SetWarning(ChartWarningKind.InstallEstimationLowConfidence, Resources.WarningDigest_InstallEstimationLowConfidence);
            }
            else
            {
                ClearLowConfidenceInstallEstimationWarnings();
            }
        }
    }

    public virtual bool HasInstallDestinationSuggestions => (InstallDestinationSuggestions?.Count ?? 0) >= 1;

    public virtual bool HasInstallDestinationSuggestionChoices => (InstallDestinationSuggestions?.Count ?? 0) >= 2;

    public virtual bool IsInstallDestinationSuggestionPopupOpen
    {
        get
        {
            return _isInstallDestinationSuggestionPopupOpen;
        }
        set
        {
            bool normalized = value && HasInstallDestinationSuggestions;
            if (_isInstallDestinationSuggestionPopupOpen != normalized)
            {
                _isInstallDestinationSuggestionPopupOpen = normalized;
                RaisePropertyChanged("IsInstallDestinationSuggestionPopupOpen");
            }
        }
    }

    public virtual string DisplayWarning
    {
        get
        {
            return Warnings.BuildDisplayText();
        }
    }

    public virtual string WarningDigestText => Warnings.BuildDigestText();

    public virtual string WarningTooltipText => Warnings.BuildTooltipText();

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
                if (string.IsNullOrWhiteSpace(value))
                {
                    InstallDestinationTitle = string.Empty;
                    InstallDestinationArtist = string.Empty;
                }
                RaisePropertyChanged("instl_dst");
                RaiseWarningPresentationChanged();
            }
        }
    }

    /// <summary>
    /// 保留画面で確認に使う、推定先フォルダの代表譜面タイトルです。
    /// INSTL DST が未確定でも上位候補 metadata を見せるため、instl_dst とは独立して保持します。
    /// </summary>
    public virtual string InstallDestinationTitle
    {
        get
        {
            return _installDestinationTitle ?? string.Empty;
        }
        set
        {
            string normalized = value ?? string.Empty;
            if (!(_installDestinationTitle == normalized))
            {
                _installDestinationTitle = normalized;
                RaisePropertyChanged("InstallDestinationTitle");
            }
        }
    }

    /// <summary>
    /// 保留画面で確認に使う、推定先フォルダの代表譜面アーティストです。
    /// </summary>
    public virtual string InstallDestinationArtist
    {
        get
        {
            return _installDestinationArtist ?? string.Empty;
        }
        set
        {
            string normalized = value ?? string.Empty;
            if (!(_installDestinationArtist == normalized))
            {
                _installDestinationArtist = normalized;
                RaisePropertyChanged("InstallDestinationArtist");
            }
        }
    }

    /// <summary>
    /// Pending 画面の INSTL DST 編集候補です。
    /// low-confidence 時のみ上位候補の path を保持します。
    /// </summary>
    public virtual IReadOnlyList<string> InstallDestinationSuggestions
    {
        get
        {
            return _installDestinationSuggestions ?? Array.Empty<string>();
        }
        set
        {
            IReadOnlyList<string> normalized = value ?? Array.Empty<string>();
            bool hadSuggestions = HasInstallDestinationSuggestions;
            bool hadChoices = HasInstallDestinationSuggestionChoices;
            if (!ReferenceEquals(_installDestinationSuggestions, normalized))
            {
                _installDestinationSuggestions = normalized;
                RaisePropertyChanged("InstallDestinationSuggestions");
                RaisePropertyChanged("HasInstallDestinationSuggestions");
                RaisePropertyChanged("HasInstallDestinationSuggestionChoices");
                if ((hadSuggestions != HasInstallDestinationSuggestions && !HasInstallDestinationSuggestions)
                    || (hadChoices != HasInstallDestinationSuggestionChoices && !HasInstallDestinationSuggestions))
                {
                    IsInstallDestinationSuggestionPopupOpen = false;
                }
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

    public string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    public string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

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
            if (bmsScore != null && bmsScore.totalnotes > 0)
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
                maintenanceInfoOrigin = MaintenanceInfoOrigin.Placeholder;
            }
            return _maintenanceInfo;
        }
        set
        {
            SetMaintenanceInfo(value, suppressPropertyChanged: false, registerEventHandlers: true);
        }
    }

    /// <summary>
    /// 現在保持している `maintenanceInfo` がどの経路で得られたかを返します。
    /// </summary>
    public MaintenanceInfoOrigin MaintenanceInfoOrigin => maintenanceInfoOrigin;

    public virtual bool IsHashDuplicated
    {
        get
        {
            return Warnings.Contains(ChartWarningKind.DuplicateChart);
        }
        set
        {
            if (IsHashDuplicated == value)
            {
                return;
            }
            if (value)
            {
                SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
            }
            else
            {
                ClearWarning(ChartWarningKind.DuplicateChart);
            }
        }
    }

    internal bool HasLowConfidenceInstallEstimationWarning()
    {
        return Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous)
            || Warnings.Contains(ChartWarningKind.InstallEstimationMetadataMismatch)
            || Warnings.Contains(ChartWarningKind.InstallEstimationReinstallNotImproved)
            || Warnings.Contains(ChartWarningKind.InstalledDestinationAmbiguous)
            || Warnings.Contains(ChartWarningKind.InstallEstimationLowConfidence);
    }

    private void ClearLowConfidenceInstallEstimationWarnings()
    {
        ClearWarning(ChartWarningKind.InstallEstimationAmbiguous);
        ClearWarning(ChartWarningKind.InstallEstimationMetadataMismatch);
        ClearWarning(ChartWarningKind.InstallEstimationReinstallNotImproved);
        ClearWarning(ChartWarningKind.InstalledDestinationAmbiguous);
        ClearWarning(ChartWarningKind.InstallEstimationLowConfidence);
    }

    /// <summary>
    /// maintenance snapshot を差し替えます。
    /// origin を明示しない既存呼び出しは、null を placeholder、非 null を計算済み snapshot として扱います。
    /// </summary>
    /// <param name="value">設定する maintenance snapshot。null の場合は placeholder を作ります。</param>
    /// <param name="suppressPropertyChanged">`maintenanceInfo` 自体の PropertyChanged を抑止するかどうか。</param>
    /// <param name="registerEventHandlers">snapshot 内 property の変更を BMSFile へ転送する listener を登録するかどうか。</param>
    /// <param name="origin">snapshot の由来。未指定の場合は互換既定値を使います。</param>
    public void SetMaintenanceInfo(BMSFileMaintenanceInfo value, bool suppressPropertyChanged = false, bool registerEventHandlers = true, MaintenanceInfoOrigin? origin = null)
    {
        MaintenanceInfoOrigin nextOrigin = origin ?? (value == null ? MaintenanceInfoOrigin.Placeholder : MaintenanceInfoOrigin.Calculated);
        if (value == null)
        {
            value = new BMSFileMaintenanceInfo(this);
        }
        if (_maintenanceInfo == value)
        {
            maintenanceInfoOrigin = nextOrigin;
            return;
        }
        if (value.hash != hash)
        {
            throw new ArgumentException("maintenanceInfo の MD5 が一致しません。");
        }
        DisposeMaintenanceInfoListener();
        _maintenanceInfo = value;
        maintenanceInfoOrigin = nextOrigin;
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

    /// <summary>
    /// lazy placeholder を生成せず、現在 materialize 済みの maintenance snapshot を返します。
    /// </summary>
    /// <returns>materialize 済み snapshot。未作成なら null。</returns>
    internal BMSFileMaintenanceInfo TryGetMaintenanceInfoWithoutCreating()
    {
        return _maintenanceInfo;
    }

    /// <summary>
    /// ResourceHealth の正本として使える DB 由来または計算済み snapshot を持つかどうかを返します。
    /// </summary>
    internal bool HasValidMaintenanceInfoSnapshot =>
        _maintenanceInfo != null
        && (maintenanceInfoOrigin == MaintenanceInfoOrigin.DbHydrated || maintenanceInfoOrigin == MaintenanceInfoOrigin.Calculated);

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
                RaisePropertyChanged(() => clear);
                RaisePropertyChanged(() => rank);
                RaisePropertyChanged(() => ClearDisplayText);
                RaisePropertyChanged(() => RankDisplayText);
                RaisePropertyChanged(() => score);
                RaisePropertyChanged(() => rate);
                RaisePropertyChanged(() => rateDouble);
                RaisePropertyChanged(() => totalnotes);
                RaisePropertyChanged(() => minbp);
                RaisePropertyChanged(() => maxcombo);
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

    internal void SetEncosingInfo(ChartFileSnapshot snapshot, BMSFileMaintenanceInfo mtInfo = null)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        SetEncodingInfoFromDetection(DetectEncodingOfBMSFileDetailed(snapshot), mtInfo);
    }

    internal BmsEncodingDetectionResult SetEncodingInfoFromSnapshotDetailed(ChartFileSnapshot snapshot, BMSFileMaintenanceInfo mtInfo = null)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        BmsEncodingDetectionResult detectionResult = DetectEncodingOfBMSFileDetailed(snapshot);
        SetEncodingInfoFromDetection(detectionResult, mtInfo);
        return detectionResult;
    }

    private void SetEncodingInfoFromDetection(BmsEncodingDetectionResult detectionResult, BMSFileMaintenanceInfo mtInfo = null)
    {
        if (detectionResult == null)
        {
            throw new ArgumentNullException(nameof(detectionResult));
        }
        mtInfo = mtInfo ?? maintenanceInfo;
        mtInfo.encoding = detectionResult.EncodingName;
        mtInfo.is_encoding_fixed = false;
    }

    public void SetHealthStatus(bool forceUpdate = false, bool memClear = true, BMSFileMaintenanceInfo mtInfo = null, string altSearchDir = null)
    {
        SetHealthStatusCore(forceUpdate, memClear, mtInfo, altSearchDir, null);
    }

    internal void SetHealthStatusUsingLookupContext(ResourceHealthLookupContext lookupContext, bool forceUpdate = false, bool memClear = true, BMSFileMaintenanceInfo mtInfo = null)
    {
        SetHealthStatusCore(forceUpdate, memClear, mtInfo, null, lookupContext);
    }

    private void SetHealthStatusCore(bool forceUpdate, bool memClear, BMSFileMaintenanceInfo mtInfo, string altSearchDir, ResourceHealthLookupContext lookupContext)
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
                if (localWAVfilesNameHashArray == null || localBGAfilesNameHashArray == null || localBGAfilesMovieNameHashArray == null)
                {
                    ResourceReferenceHealthCache cache = resourceReferenceHealthCache;
                    if (cache != null)
                    {
                        localWAVfilesNameHashArray = cache.LocalWavHashes;
                        nonlocalWAVfiles = cache.NonLocalWavReferences;
                        localBGAfilesNameHashArray = cache.LocalImageHashes;
                        nonlocalBGAfiles = cache.NonLocalImageReferences;
                        localBGAfilesMovieNameHashArray = cache.LocalMovieHashes;
                        nonlocalBGAfilesMovie = cache.NonLocalMovieReferences;
                    }
                    else
                    {
                        ILookup<bool, string> lookup = BGAfiles.ToLookup((string f) => bgaMovieExtensions.Any((string e) => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
                        ILookup<bool, string> lookup2 = lookup[false].ToLookup((string f) => !f.Contains('\\'));
                        ILookup<bool, string> lookup3 = lookup[true].ToLookup((string f) => !f.Contains('\\'));
                        nonlocalBGAfiles = lookup2[false].ToList();
                        localBGAfilesNameHashArray = GetResourceReferenceHashArray(lookup2[true]);
                        nonlocalBGAfilesMovie = lookup3[false].ToList();
                        localBGAfilesMovieNameHashArray = GetResourceReferenceHashArray(lookup3[true]);
                        ILookup<bool, string> lookup4 = WAVfiles.ToLookup((string f) => !f.Contains('\\'));
                        nonlocalWAVfiles = lookup4[false].ToList();
                        localWAVfilesNameHashArray = GetResourceReferenceHashArray(lookup4[true]);
                    }
                }
            }
            string dir = (string.IsNullOrWhiteSpace(altSearchDir) ? DirectoryExt.GetDirectoryNameSimple(path) : altSearchDir.TrimEnd('\\'));
            string lookupDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryResourceLookupCache.Entry resourceEntry = lookupContext?.GetResourceEntryOrNull(lookupDir);
            long cacheHitCount = 0L;
            long audioFileExistsFallbackCount = 0L;
            long imageFileExistsFallbackCount = 0L;
            long movieFileExistsFallbackCount = 0L;
            long optionalImageFileExistsFallbackCount = 0L;
            dir = lookupDir + Path.DirectorySeparatorChar;
            uint[] localAudioResourceHashes = null;
            uint[] localImageResourceHashes = null;
            uint[] localMovieResourceHashes = null;
            Func<uint[], List<string>, IEnumerable<string>, ChartResourceKind, int> func = delegate (uint[] localHashSet, List<string> nonlocalFileList, IEnumerable<string> extensions, ChartResourceKind resourceKind)
            {
                int num = 0;
                if (localHashSet.Length != 0)
                {
                    uint[] categoryResourceHashes = GetLocalResourceHashesForHealth(
                        lookupDir,
                        resourceKind,
                        resourceEntry,
                        ref localAudioResourceHashes,
                        ref localImageResourceHashes,
                        ref localMovieResourceHashes);
                    num += CountMissingLocalHashes(
                        localHashSet,
                        categoryResourceHashes);
                }
                if (nonlocalFileList.Count > 0)
                {
                    ResourceHealthFallbackKind fallbackKind = GetFallbackKind(resourceKind);
                    foreach (string file in nonlocalFileList)
                    {
                        if (TryResolveResourceReferenceFromCache(resourceEntry, file, resourceKind, out bool existsInCache))
                        {
                            cacheHitCount++;
                            if (!existsInCache)
                            {
                                num++;
                            }
                            continue;
                        }

                        IncrementFallbackCounter(fallbackKind, ref audioFileExistsFallbackCount, ref imageFileExistsFallbackCount, ref movieFileExistsFallbackCount, ref optionalImageFileExistsFallbackCount);
                        if (!ExistsWithCompatibleExtensions(dir, file, extensions))
                        {
                            num++;
                        }
                    }
                }
                return num;
            };
            Func<string, IEnumerable<string>, bool> func2 = delegate (string filename, IEnumerable<string> extensions)
            {
                string text = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(filename);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (TryResolveResourceReferenceFromCache(resourceEntry, text, ChartResourceKind.Image, out bool existsInCache))
                    {
                        cacheHitCount++;
                        return existsInCache;
                    }

                    optionalImageFileExistsFallbackCount++;
                    return ExistsWithCompatibleExtensions(dir, text, extensions);
                }
                return false;
            };
            mtInfo.wav_files_defined = WAVfiles.Count;
            if (mtInfo.wav_files_defined > 0)
            {
                mtInfo.wav_files_existing = mtInfo.wav_files_defined - func(localWAVfilesNameHashArray, nonlocalWAVfiles, wavExtensions, ChartResourceKind.Audio);
            }
            mtInfo.bga_files_defined = localBGAfilesNameHashArray.Length + nonlocalBGAfiles.Count;
            if (mtInfo.bga_files_defined > 0)
            {
                mtInfo.bga_files_existing = mtInfo.bga_files_defined - func(localBGAfilesNameHashArray, nonlocalBGAfiles, bgaImageExtensions, ChartResourceKind.Image);
            }
            mtInfo.movie_files_defined = localBGAfilesMovieNameHashArray.Length + nonlocalBGAfilesMovie.Count;
            if (mtInfo.movie_files_defined > 0)
            {
                mtInfo.movie_files_existing = mtInfo.movie_files_defined - func(localBGAfilesMovieNameHashArray, nonlocalBGAfilesMovie, Enumerable.Empty<string>(), ChartResourceKind.Movie);
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
                resourceReferenceHealthCache = null;
            }
            lookupContext?.AddCacheHits(cacheHitCount);
            lookupContext?.AddFileExistsFallbacks(ResourceHealthFallbackKind.Audio, audioFileExistsFallbackCount);
            lookupContext?.AddFileExistsFallbacks(ResourceHealthFallbackKind.Image, imageFileExistsFallbackCount);
            lookupContext?.AddFileExistsFallbacks(ResourceHealthFallbackKind.Movie, movieFileExistsFallbackCount);
            lookupContext?.AddFileExistsFallbacks(ResourceHealthFallbackKind.OptionalImage, optionalImageFileExistsFallbackCount);
            if (ReferenceEquals(mtInfo, _maintenanceInfo))
            {
                maintenanceInfoOrigin = MaintenanceInfoOrigin.Calculated;
            }
        }
    }

    private static uint[] GetLocalResourceHashesForHealth(
        string directory,
        ChartResourceKind resourceKind,
        DirectoryResourceLookupCache.Entry resourceEntry,
        ref uint[] audioResourceHashes,
        ref uint[] imageResourceHashes,
        ref uint[] movieResourceHashes)
    {
        uint[] cachedHashes = GetCategoryRelativePathHashArray(resourceEntry, resourceKind);
        if (cachedHashes != null)
        {
            return cachedHashes;
        }

        switch (resourceKind)
        {
            case ChartResourceKind.Audio:
                return audioResourceHashes ??= GetDirectoryResourceHashArray(directory, ChartResourceKind.Audio);
            case ChartResourceKind.Image:
                return imageResourceHashes ??= GetDirectoryResourceHashArray(directory, ChartResourceKind.Image);
            case ChartResourceKind.Movie:
                return movieResourceHashes ??= GetDirectoryResourceHashArray(directory, ChartResourceKind.Movie);
            default:
                return Array.Empty<uint>();
        }
    }

    private static uint[] GetCategoryRelativePathHashArray(DirectoryResourceLookupCache.Entry resourceEntry, ChartResourceKind resourceKind)
    {
        if (resourceEntry != null)
        {
            return resourceKind switch
            {
                ChartResourceKind.Audio => resourceEntry.AudioRelativePathHashArray,
                ChartResourceKind.Image => resourceEntry.ImageRelativePathHashArray,
                ChartResourceKind.Movie => resourceEntry.MovieRelativePathHashArray,
                _ => Array.Empty<uint>()
            };
        }

        return null;
    }

    private static uint[] GetDirectoryResourceHashArray(string directory, ChartResourceKind resourceKind)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<uint>();
        }

        uint[] hashes = FastDirectoryEnumerator.GetFileNames(directory)
            .Where((string fileName) => IsResourceFileNameForKind(fileName, resourceKind))
            .Select(GetResourceReferenceHash)
            .Where((uint hash) => hash != 0u)
            .ToArray();
        Array.Sort(hashes);
        return hashes;
    }

    private sealed class ResourceReferenceHealthCache
    {
        private ResourceReferenceHealthCache(
            uint[] localWavHashes,
            List<string> nonLocalWavReferences,
            uint[] localImageHashes,
            List<string> nonLocalImageReferences,
            uint[] localMovieHashes,
            List<string> nonLocalMovieReferences)
        {
            LocalWavHashes = localWavHashes ?? Array.Empty<uint>();
            NonLocalWavReferences = nonLocalWavReferences ?? new List<string>();
            LocalImageHashes = localImageHashes ?? Array.Empty<uint>();
            NonLocalImageReferences = nonLocalImageReferences ?? new List<string>();
            LocalMovieHashes = localMovieHashes ?? Array.Empty<uint>();
            NonLocalMovieReferences = nonLocalMovieReferences ?? new List<string>();
        }

        public uint[] LocalWavHashes { get; }

        public List<string> NonLocalWavReferences { get; }

        public uint[] LocalImageHashes { get; }

        public List<string> NonLocalImageReferences { get; }

        public uint[] LocalMovieHashes { get; }

        public List<string> NonLocalMovieReferences { get; }

        public static ResourceReferenceHealthCache Create(HashSet<string> wavReferences, HashSet<string> bgaReferences)
        {
            uint[] localWavHashes = GetNormalizedResourceReferenceHashArray((wavReferences ?? new HashSet<string>()).Where(IsLocalReference));
            List<string> nonLocalWavReferences = (wavReferences ?? new HashSet<string>()).Where((string reference) => !IsLocalReference(reference)).ToList();
            ILookup<bool, string> bgaByMovie = (bgaReferences ?? new HashSet<string>())
                .ToLookup((string reference) => bgaMovieExtensions.Any((string extension) => reference.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
            ILookup<bool, string> imageByLocal = bgaByMovie[false].ToLookup(IsLocalReference);
            ILookup<bool, string> movieByLocal = bgaByMovie[true].ToLookup(IsLocalReference);
            return new ResourceReferenceHealthCache(
                localWavHashes,
                nonLocalWavReferences,
                GetNormalizedResourceReferenceHashArray(imageByLocal[true]),
                imageByLocal[false].ToList(),
                GetNormalizedResourceReferenceHashArray(movieByLocal[true]),
                movieByLocal[false].ToList());
        }

        private static bool IsLocalReference(string reference)
        {
            return !string.IsNullOrWhiteSpace(reference) && !reference.Contains('\\');
        }
    }

    private static uint[] GetResourceReferenceHashArray(IEnumerable<string> references)
    {
        return (references ?? Enumerable.Empty<string>())
            .Select(GetResourceReferenceHash)
            .Where((uint hash) => hash != 0u)
            .ToArray();
    }

    private static uint[] GetNormalizedResourceReferenceHashArray(IEnumerable<string> references)
    {
        return (references ?? Enumerable.Empty<string>())
            .Select(GetNormalizedResourceReferenceHash)
            .Where((uint hash) => hash != 0u)
            .ToArray();
    }

    private static uint GetResourceReferenceHash(string referencePath)
    {
        string normalized = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(referencePath);
        return ChartResourceKeyHash.GetLookupHash(normalized);
    }

    private static uint GetNormalizedResourceReferenceHash(string normalizedReferencePath)
    {
        if (string.IsNullOrWhiteSpace(normalizedReferencePath))
        {
            return 0u;
        }
        string extension = Path.GetExtension(normalizedReferencePath);
        string key = string.IsNullOrWhiteSpace(extension) ? normalizedReferencePath : Path.ChangeExtension(normalizedReferencePath, null);
        return ChartResourceKeyHash.GetLookupHash(key);
    }

    private static bool IsResourceFileNameForKind(string fileName, ChartResourceKind resourceKind)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return resourceKind switch
        {
            ChartResourceKind.Audio => wavExtensions.Any((string extension) => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)),
            ChartResourceKind.Image => bgaImageExtensions.Any((string extension) => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)),
            ChartResourceKind.Movie => bgaMovieExtensions.Any((string extension) => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static int CountMissingLocalHashes(
        uint[] requiredHashes,
        uint[] availableHashes)
    {
        if (requiredHashes == null || requiredHashes.Length == 0)
        {
            return 0;
        }

        int missing = 0;
        foreach (uint requiredHash in requiredHashes)
        {
            if (Array.BinarySearch(availableHashes ?? Array.Empty<uint>(), requiredHash) < 0)
            {
                missing++;
            }
        }
        return missing;
    }

    private static ResourceHealthFallbackKind GetFallbackKind(ChartResourceKind resourceKind)
    {
        return resourceKind switch
        {
            ChartResourceKind.Audio => ResourceHealthFallbackKind.Audio,
            ChartResourceKind.Image => ResourceHealthFallbackKind.Image,
            ChartResourceKind.Movie => ResourceHealthFallbackKind.Movie,
            _ => ResourceHealthFallbackKind.Unknown
        };
    }

    private static void IncrementFallbackCounter(ResourceHealthFallbackKind kind, ref long audio, ref long image, ref long movie, ref long optionalImage)
    {
        switch (kind)
        {
            case ResourceHealthFallbackKind.Audio:
                audio++;
                break;
            case ResourceHealthFallbackKind.Image:
                image++;
                break;
            case ResourceHealthFallbackKind.Movie:
                movie++;
                break;
            case ResourceHealthFallbackKind.OptionalImage:
                optionalImage++;
                break;
        }
    }

    private static bool ExistsWithCompatibleExtensions(string directoryWithSeparator, string file, IEnumerable<string> extensions)
    {
        try
        {
            string fullPath = Path.GetFullPath(directoryWithSeparator + file);
            string dirname = DirectoryExt.GetDirectoryNameSimple(fullPath) + Path.DirectorySeparatorChar;
            string basename = Path.GetFileNameWithoutExtension(fullPath);
            return File.Exists(fullPath) || extensions.Any((string ext) => File.Exists(dirname + basename + ext));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveResourceReferenceFromCache(DirectoryResourceLookupCache.Entry resourceEntry, string referencePath, ChartResourceKind resourceKind, out bool exists)
    {
        exists = false;
        if (resourceEntry == null || resourceKind == ChartResourceKind.Unknown)
        {
            return false;
        }
        string normalized = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(referencePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }
        uint hash = ChartResourceKeyHash.GetLookupHash(normalized);
        if (hash == 0u)
        {
            return false;
        }

        exists = ContainsResourceHash(resourceEntry, resourceKind, hash);
        return true;
    }

    private static bool ContainsResourceHash(DirectoryResourceLookupCache.Entry entry, ChartResourceKind resourceKind, uint hash)
    {
        return resourceKind switch
        {
            ChartResourceKind.Audio => ContainsHash(entry.AudioRelativePathHashArray, hash),
            ChartResourceKind.Image => ContainsHash(entry.ImageRelativePathHashArray, hash),
            ChartResourceKind.Movie => ContainsHash(entry.MovieRelativePathHashArray, hash),
            _ => false
        };
    }

    private static bool ContainsHash(uint[] hashes, uint hash)
    {
        if (hashes == null || hashes.Length == 0 || hash == 0u)
        {
            return false;
        }
        return Array.BinarySearch(hashes, hash) >= 0;
    }

    internal void ClearComponentFileCache()
    {
        lock (filesCacheLock)
        {
            localWAVfilesNameHashArray = null;
            localBGAfilesNameHashArray = null;
            localBGAfilesMovieNameHashArray = null;
            nonlocalWAVfiles = null;
            nonlocalBGAfiles = null;
            nonlocalBGAfilesMovie = null;
            resourceReferenceHealthCache = null;
        }
    }

    public void SetMode()
    {
        BMSFile bMSFile = CreateBMSFileFromFile(path);
        mode = bMSFile.mode;
    }

    /// <summary>
    /// Path-based compatibility API. New single-read flows should prefer
    /// <see cref="CreateBMSFileFromSnapshot"/> so lightweight metadata and chart_info can share bytes.
    /// </summary>
    public static BMSFile CreateBMSFileFromFile(string filePath, string codepageName = "shift_jis")
    {
        IEnumerable<string> enumerable = File.ReadLines(filePath, Encoding.GetEncoding(codepageName));
        return CreateBMSFileFromLines(
            enumerable,
            filePath,
            () => getMD5Hash(filePath),
            () => GetSHA256Hash(filePath));
    }

    internal static BMSFile CreateBMSFileFromSnapshot(ChartFileSnapshot snapshot, string codepageName = "shift_jis")
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        Encoding encoding = Encoding.GetEncoding(codepageName);
        return CreateBMSFileFromLines(
            ReadSnapshotLines(snapshot, encoding),
            snapshot.Path,
            () => snapshot.Md5,
            () => snapshot.Sha256);
    }

    private static IEnumerable<string> ReadSnapshotLines(ChartFileSnapshot snapshot, Encoding encoding)
    {
        using (MemoryStream stream = new MemoryStream(snapshot.Bytes, writable: false))
        using (StreamReader reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }
    }

    private static BMSFile CreateBMSFileFromLines(
        IEnumerable<string> enumerable,
        string filePath,
        Func<string> md5Provider,
        Func<string> sha256Provider)
    {
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
            if (!TryParseDirectiveLine(item, out BmsDirective directive, out int valueStart))
            {
                continue;
            }
            if (directive == BmsDirective.ModeChannel && TryParseModeChannelLine(item, out char channelGroup, out char lane))
            {
                switch (channelGroup)
                {
                    case '1':
                    case '3':
                        switch (lane)
                        {
                            case '1':
                                flag = true;
                                break;
                            case '2':
                                flag2 = true;
                                break;
                            case '3':
                                flag3 = true;
                                break;
                            case '4':
                                flag4 = true;
                                break;
                            case '5':
                                flag5 = true;
                                break;
                            case '6':
                                flag6 = true;
                                break;
                            case '7':
                                flag7 = true;
                                break;
                            case '8':
                                flag8 = true;
                                break;
                            case '9':
                                flag9 = true;
                                break;
                        }
                        break;
                    case '2':
                    case '4':
                        switch (lane)
                        {
                            case '1':
                                flag10 = true;
                                break;
                            case '2':
                                flag11 = true;
                                break;
                            case '3':
                                flag12 = true;
                                break;
                            case '4':
                                flag13 = true;
                                break;
                            case '5':
                                flag14 = true;
                                break;
                            case '6':
                                flag15 = true;
                                break;
                            case '7':
                                flag16 = true;
                                break;
                            case '8':
                                flag17 = true;
                                break;
                            case '9':
                                flag18 = true;
                                break;
                        }
                        break;
                }
                continue;
            }
            string value = valueStart >= 0 && valueStart <= item.Length ? item.Substring(valueStart) : string.Empty;
            switch (directive)
            {
                case BmsDirective.Wav:
                    AddNormalizedResourceReference(hashSet, value);
                    break;
                case BmsDirective.Bmp:
                    AddNormalizedResourceReference(hashSet2, value);
                    break;
                case BmsDirective.Title:
                    if (string.IsNullOrWhiteSpace(bMSFile.title))
                    {
                        bMSFile.title = value;
                    }
                    break;
                case BmsDirective.Genre:
                    if (string.IsNullOrWhiteSpace(bMSFile.genre))
                    {
                        bMSFile.genre = value;
                    }
                    break;
                case BmsDirective.Artist:
                    if (string.IsNullOrWhiteSpace(bMSFile.artist))
                    {
                        bMSFile.artist = value;
                    }
                    break;
                case BmsDirective.PlayLevel:
                    if (!bMSFile.level.HasValue)
                    {
                        bMSFile.level = TryParseDirectiveInt(value);
                    }
                    break;
                case BmsDirective.Difficulty:
                    if (!bMSFile.difficulty.HasValue)
                    {
                        bMSFile.difficulty = TryParseDirectiveInt(value);
                    }
                    break;
                case BmsDirective.Rank:
                    if (!bMSFile.judge.HasValue)
                    {
                        bMSFile.judge = TryParseDirectiveInt(value);
                    }
                    break;
                case BmsDirective.SubTitle:
                    if (string.IsNullOrWhiteSpace(bMSFile.subtitle))
                    {
                        bMSFile.subtitle = value;
                    }
                    break;
                case BmsDirective.SubArtist:
                    if (string.IsNullOrWhiteSpace(bMSFile.subartist))
                    {
                        bMSFile.subartist = value;
                    }
                    break;
                case BmsDirective.Banner:
                    if (string.IsNullOrWhiteSpace(bMSFile.banner))
                    {
                        bMSFile.banner = value;
                    }
                    break;
                case BmsDirective.StageFile:
                    if (string.IsNullOrWhiteSpace(bMSFile.stagefile))
                    {
                        bMSFile.stagefile = value;
                    }
                    break;
                case BmsDirective.BackBmp:
                    if (string.IsNullOrWhiteSpace(bMSFile.backbmp))
                    {
                        bMSFile.backbmp = value;
                    }
                    break;
            }
        }
        bMSFile.WAVfiles = hashSet;
        bMSFile.BGAfiles = hashSet2;
        bMSFile.resourceReferenceHealthCache = ResourceReferenceHealthCache.Create(hashSet, hashSet2);
        bMSFile.hash = md5Provider();
        bMSFile.ApplySha256(sha256Provider());
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

    private enum BmsDirective
    {
        Unknown,
        ModeChannel,
        Wav,
        Bmp,
        Title,
        SubTitle,
        Genre,
        Artist,
        SubArtist,
        StageFile,
        BackBmp,
        Banner,
        PlayLevel,
        Difficulty,
        Rank
    }

    private static bool TryParseDirectiveLine(string line, out BmsDirective directive, out int valueStart)
    {
        directive = BmsDirective.Unknown;
        valueStart = -1;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        if (index >= line.Length || line[index] != '#')
        {
            return false;
        }
        index++;
        if (index < line.Length && IsAsciiDigit(line[index]))
        {
            directive = BmsDirective.ModeChannel;
            return true;
        }
        int tokenStart = index;
        while (index < line.Length && IsAsciiAlphaNumeric(line[index]))
        {
            index++;
        }
        int tokenLength = index - tokenStart;
        if (tokenLength <= 0)
        {
            return false;
        }
        if (!TryGetDirectiveFromToken(line, tokenStart, tokenLength, out directive))
        {
            return false;
        }
        if (directive == BmsDirective.Wav || directive == BmsDirective.Bmp)
        {
            if (tokenLength != 5 || index >= line.Length || !char.IsWhiteSpace(line[index]))
            {
                directive = BmsDirective.Unknown;
                return false;
            }
        }
        else if (index >= line.Length || !char.IsWhiteSpace(line[index]))
        {
            directive = BmsDirective.Unknown;
            return false;
        }
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        valueStart = index;
        return true;
    }

    private static bool TryGetDirectiveFromToken(string line, int start, int length, out BmsDirective directive)
    {
        directive = BmsDirective.Unknown;
        if (length == 5 && StartsWithAsciiIgnoreCase(line, start, "WAV") && IsBase36(line[start + 3]) && IsBase36(line[start + 4]))
        {
            directive = BmsDirective.Wav;
            return true;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(line, start, "BMP") && IsBase36(line[start + 3]) && IsBase36(line[start + 4]))
        {
            directive = BmsDirective.Bmp;
            return true;
        }
        switch (length)
        {
            case 4:
                if (EqualsAsciiIgnoreCase(line, start, "RANK"))
                {
                    directive = BmsDirective.Rank;
                    return true;
                }
                break;
            case 5:
                if (EqualsAsciiIgnoreCase(line, start, "TITLE"))
                {
                    directive = BmsDirective.Title;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "GENRE"))
                {
                    directive = BmsDirective.Genre;
                    return true;
                }
                break;
            case 6:
                if (EqualsAsciiIgnoreCase(line, start, "ARTIST"))
                {
                    directive = BmsDirective.Artist;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "BANNER"))
                {
                    directive = BmsDirective.Banner;
                    return true;
                }
                break;
            case 7:
                if (EqualsAsciiIgnoreCase(line, start, "BACKBMP"))
                {
                    directive = BmsDirective.BackBmp;
                    return true;
                }
                break;
            case 8:
                if (EqualsAsciiIgnoreCase(line, start, "SUBTITLE"))
                {
                    directive = BmsDirective.SubTitle;
                    return true;
                }
                break;
            case 9:
                if (EqualsAsciiIgnoreCase(line, start, "SUBARTIST"))
                {
                    directive = BmsDirective.SubArtist;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "STAGEFILE"))
                {
                    directive = BmsDirective.StageFile;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "PLAYLEVEL"))
                {
                    directive = BmsDirective.PlayLevel;
                    return true;
                }
                break;
            case 10:
                if (EqualsAsciiIgnoreCase(line, start, "DIFFICULTY"))
                {
                    directive = BmsDirective.Difficulty;
                    return true;
                }
                break;
        }
        return false;
    }

    private static bool StartsWithAsciiIgnoreCase(string value, int start, string prefix)
    {
        if (value == null || prefix == null || start < 0 || start + prefix.Length > value.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (ToUpperAscii(value[start + i]) != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool EqualsAsciiIgnoreCase(string value, int start, string expected)
    {
        return start >= 0
            && expected != null
            && value != null
            && start + expected.Length <= value.Length
            && StartsWithAsciiIgnoreCase(value, start, expected);
    }

    private static char ToUpperAscii(char value)
    {
        return value >= 'a' && value <= 'z' ? (char)(value - ('a' - 'A')) : value;
    }

    private static bool IsAsciiAlphaNumeric(char value)
    {
        return IsAsciiDigit(value) || (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
    }

    private static bool IsBase36(char value)
    {
        return IsAsciiAlphaNumeric(value);
    }

    private static void AddNormalizedResourceReference(HashSet<string> references, string value)
    {
        string normalized = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            references.Add(normalized);
        }
    }

    private static int? TryParseDirectiveInt(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsAsciiDigit(value[0]))
        {
            return null;
        }
        try
        {
            return int.Parse(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseModeChannelLine(string line, out char channelGroup, out char lane)
    {
        channelGroup = '\0';
        lane = '\0';
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        if (index >= line.Length || line[index] != '#')
        {
            return false;
        }
        index++;
        if (index + 5 > line.Length
            || !IsAsciiDigit(line[index])
            || !IsAsciiDigit(line[index + 1])
            || !IsAsciiDigit(line[index + 2]))
        {
            return false;
        }
        channelGroup = line[index + 3];
        lane = line[index + 4];
        if (!((channelGroup == '1' || channelGroup == '2' || channelGroup == '3' || channelGroup == '4')
            && lane >= '1'
            && lane <= '9'))
        {
            return false;
        }
        index += 5;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        if (index >= line.Length || line[index] != ':')
        {
            return false;
        }
        index++;
        while (index < line.Length && (char.IsWhiteSpace(line[index]) || line[index] == '0'))
        {
            index++;
        }
        return index < line.Length && !char.IsWhiteSpace(line[index]);
    }

    private static bool IsAsciiDigit(char value)
    {
        return value >= '0' && value <= '9';
    }

    public static void SetBMSComponentFilesFromBMSFile(BMSFile bmsFile, string codepageName = "shift_jis")
    {
        IEnumerable<string> enumerable = File.ReadLines(bmsFile.path, Encoding.GetEncoding(codepageName));
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in enumerable)
        {
            if (!TryParseDirectiveLine(item, out BmsDirective directive, out int valueStart))
            {
                continue;
            }
            string value = valueStart >= 0 && valueStart <= item.Length ? item.Substring(valueStart) : string.Empty;
            if (directive == BmsDirective.Wav)
            {
                AddNormalizedResourceReference(hashSet, value);
            }
            else if (directive == BmsDirective.Bmp)
            {
                AddNormalizedResourceReference(hashSet2, value);
            }
        }
        bmsFile.WAVfiles = hashSet;
        bmsFile.BGAfiles = hashSet2;
        bmsFile.resourceReferenceHealthCache = ResourceReferenceHealthCache.Create(hashSet, hashSet2);
        bmsFile.hash = getMD5Hash(bmsFile.path);
        bmsFile.ApplySha256(GetSHA256Hash(bmsFile.path));
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

    internal static string GetSHA256Hash(string filePath)
    {
        using SHA256 sHA = SHA256.Create();
        byte[] hash;
        using (FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hash = sHA.ComputeHash(inputStream);
        }
        StringBuilder stringBuilder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            stringBuilder.Append(b.ToString("x2"));
        }
        return stringBuilder.ToString();
    }

    internal void ApplySha256(string value)
    {
        sha256 = value;
    }

    internal void ApplySnapshotDigest(string md5, string sha256Value)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            hash = md5;
        }
        ApplySha256(sha256Value);
    }

    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        int start = 0;
        int end = value.Length - 1;
        while (start <= end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }
        while (end >= start && char.IsWhiteSpace(value[end]))
        {
            end--;
        }
        int length = end - start + 1;
        if (length != 64)
        {
            throw new FormatException("SHA256 HASH ではありません");
        }
        bool hasUpper = false;
        for (int i = start; i <= end; i++)
        {
            char c = value[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
            {
                continue;
            }
            if (c >= 'A' && c <= 'F')
            {
                hasUpper = true;
                continue;
            }
            throw new FormatException("SHA256 HASH ではありません");
        }
        if (start == 0 && length == value.Length)
        {
            return hasUpper ? value.ToLowerInvariant() : value;
        }
        string normalized = value.Substring(start, length);
        if (hasUpper)
        {
            normalized = normalized.ToLowerInvariant();
        }
        return normalized;
    }

    public static void ReloadBMSFileWithEncoding(BMSFile bmsFile, string codepageName = "")
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (string.IsNullOrWhiteSpace(bmsFile.path) || !File.Exists(bmsFile.path))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", bmsFile.path ?? "");
        }
        if (string.IsNullOrWhiteSpace(codepageName))
        {
            codepageName = DetectEncodingOfBMSFile(bmsFile);
        }
        codepageName = NormalizeReloadEncodingName(codepageName);
        ApplyDecodedBmsMetadataWithEncoding(bmsFile, File.ReadAllBytes(bmsFile.path), codepageName);
    }

    internal static void ReloadBMSFileWithEncoding(BMSFile bmsFile, ChartFileSnapshot snapshot, string codepageName = "")
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (string.IsNullOrWhiteSpace(codepageName))
        {
            codepageName = DetectEncodingOfBMSFile(snapshot);
        }
        codepageName = NormalizeReloadEncodingName(codepageName);
        ApplyDecodedBmsMetadataWithEncoding(bmsFile, snapshot.Bytes, codepageName);
    }

    internal static void ReloadBMSMetadataWithEncodingDetection(
        BMSFile bmsFile,
        ChartFileSnapshot snapshot,
        BmsEncodingDetectionResult detectionResult)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (detectionResult == null)
        {
            throw new ArgumentNullException(nameof(detectionResult));
        }
        string codepageName = NormalizeReloadEncodingName(detectionResult.EncodingName);
        string decodedText = detectionResult.DecodedText;
        if (decodedText == null)
        {
            decodedText = DecodeBytes(snapshot.Bytes, Encoding.GetEncoding(codepageName));
        }
        ApplyDecodedBmsMetadata(bmsFile, decodedText, codepageName);
    }

    private static string NormalizeReloadEncodingName(string codepageName)
    {
        if (string.Equals(codepageName, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            codepageName = "shift_jis";
        }
        return codepageName.TrimEnd('?');
    }

    private static void ApplyDecodedBmsMetadataWithEncoding(BMSFile bmsFile, byte[] bytes, string codepageName)
    {
        string decodedText = DecodeBytes(bytes, Encoding.GetEncoding(codepageName));
        ApplyDecodedBmsMetadata(bmsFile, decodedText, codepageName);
    }

    private static void ApplyDecodedBmsMetadata(BMSFile bmsFile, string decodedText, string codepageName)
    {
        ApplyBmsMetadataFromDecodedText(bmsFile, decodedText);
        if (string.Equals(codepageName, "shift_jis", StringComparison.OrdinalIgnoreCase))
        {
            bmsFile.adddate = null;
            bmsFile.date = null;
        }
    }

    private static void ApplyBmsMetadataFromDecodedText(BMSFile bmsFile, string decodedText)
    {
        string title = null;
        string subtitle = null;
        string artist = null;
        string subartist = null;
        string genre = null;
        using (StringReader reader = new StringReader(decodedText ?? string.Empty))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!TryParseDirectiveLine(line, out BmsDirective directive, out int valueStart))
                {
                    continue;
                }
                string value = valueStart >= 0 && valueStart <= line.Length ? line.Substring(valueStart) : string.Empty;
                switch (directive)
                {
                    case BmsDirective.Title:
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            title = value;
                        }
                        break;
                    case BmsDirective.SubTitle:
                        if (string.IsNullOrWhiteSpace(subtitle))
                        {
                            subtitle = value;
                        }
                        break;
                    case BmsDirective.Artist:
                        if (string.IsNullOrWhiteSpace(artist))
                        {
                            artist = value;
                        }
                        break;
                    case BmsDirective.SubArtist:
                        if (string.IsNullOrWhiteSpace(subartist))
                        {
                            subartist = value;
                        }
                        break;
                    case BmsDirective.Genre:
                        if (string.IsNullOrWhiteSpace(genre))
                        {
                            genre = value;
                        }
                        break;
                }
            }
        }
        bool titleChanged = !string.Equals(bmsFile._title, title, StringComparison.Ordinal)
            || !string.Equals(bmsFile._subtitle, subtitle, StringComparison.Ordinal);
        bmsFile._title = title;
        bmsFile._subtitle = subtitle;
        bmsFile._cachedComposedTitle = null;
        bmsFile._cachedComposedTitleSource = null;
        bmsFile._cachedComposedSubtitleSource = null;
        if (titleChanged)
        {
            bmsFile.RaisePropertyChanged("Title");
        }

        bool artistChanged = !string.Equals(bmsFile._artist, artist, StringComparison.Ordinal)
            || !string.Equals(bmsFile._subartist, subartist, StringComparison.Ordinal);
        bmsFile._artist = artist;
        bmsFile._subartist = subartist;
        if (artistChanged)
        {
            bmsFile.RaisePropertyChanged("Artist");
        }
        bmsFile.genre = genre;
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
        return DetectEncodingOfBMSFileCore(File.ReadAllBytes(path)).EncodingName;
    }

    internal static string DetectEncodingOfBMSFile(ChartFileSnapshot snapshot)
    {
        return DetectEncodingOfBMSFileDetailed(snapshot).EncodingName;
    }

    internal static BmsEncodingDetectionResult DetectEncodingOfBMSFileDetailed(ChartFileSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        return DetectEncodingOfBMSFileCore(snapshot.Bytes);
    }

    private static BmsEncodingDetectionResult DetectEncodingOfBMSFileCore(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        if (IsAsciiOnly(bytes))
        {
            return CreateEncodingDetectionResult("shift_jis", EncodingDetectionOutcome.ShiftJis, fastAscii: true, decodedText: null);
        }

        if (!TryDecodeBytes(bytes, sjisEnc, out string sjisText))
        {
            if (TryDecodeBytes(bytes, koreanEnc, out string koreanText))
            {
                return CreateEncodingDetectionResult("ks_c_5601-1987", EncodingDetectionOutcome.Korean, fastAscii: false, decodedText: koreanText);
            }
            if (TryDecodeBytes(bytes, utf8Enc, out string utf8Text))
            {
                return CreateEncodingDetectionResult("utf-8", EncodingDetectionOutcome.Utf8, fastAscii: false, decodedText: utf8Text);
            }
            return CreateEncodingDetectionResult("unknown", EncodingDetectionOutcome.Unknown, fastAscii: false, decodedText: null);
        }

        if (!ContainsNonBasicLatinNonWhitespace(sjisText))
        {
            return CreateEncodingDetectionResult("shift_jis", EncodingDetectionOutcome.ShiftJis, fastAscii: false, decodedText: null);
        }
        if (HasInvalidJapaneseKanaSequence(sjisText))
        {
            return CreateEncodingDetectionResult("ks_c_5601-1987?", EncodingDetectionOutcome.KoreanQuestion, fastAscii: false, decodedText: null);
        }
        if (HasJapaneseDetectionRun(sjisText))
        {
            return CreateEncodingDetectionResult("shift_jis?", EncodingDetectionOutcome.ShiftJisQuestion, fastAscii: false, decodedText: null);
        }
        if (TryDecodeBytes(bytes, koreanEnc, out string koreanQuestionText))
        {
            if (HasHangulDetectionRunIgnoringWhitespace(koreanQuestionText))
            {
                return CreateEncodingDetectionResult("ks_c_5601-1987?", EncodingDetectionOutcome.KoreanQuestion, fastAscii: false, decodedText: null);
            }
            return CreateEncodingDetectionResult("shift_jis?", EncodingDetectionOutcome.ShiftJisQuestion, fastAscii: false, decodedText: null);
        }
        return CreateEncodingDetectionResult("shift_jis", EncodingDetectionOutcome.ShiftJis, fastAscii: false, decodedText: null);
    }

    private static BmsEncodingDetectionResult CreateEncodingDetectionResult(
        string encodingName,
        EncodingDetectionOutcome outcome,
        bool fastAscii,
        string decodedText)
    {
        return new BmsEncodingDetectionResult(encodingName, outcome, fastAscii, decodedText);
    }

    private static bool IsAsciiOnly(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 0x7F)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryDecodeBytes(byte[] bytes, Encoding encoding, out string text)
    {
        try
        {
            text = DecodeBytes(bytes, encoding);
            return true;
        }
        catch
        {
            text = null;
            return false;
        }
    }

    private static string DecodeBytes(byte[] bytes, Encoding encoding)
    {
        using (MemoryStream stream = new MemoryStream(bytes ?? Array.Empty<byte>(), writable: false))
        using (StreamReader reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
        {
            return reader.ReadToEnd();
        }
    }

    private static bool ContainsNonBasicLatinNonWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > '\u007F' && !char.IsWhiteSpace(c))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasJapaneseDetectionRun(string text)
    {
        int runLength = 0;
        for (int i = 0; i < (text?.Length ?? 0); i++)
        {
            if (IsJapaneseDetectionChar(text[i]))
            {
                runLength++;
                if (runLength >= 2)
                {
                    return true;
                }
            }
            else
            {
                runLength = 0;
            }
        }
        return false;
    }

    private static bool IsJapaneseDetectionChar(char c)
    {
        return (c >= '\u4E00' && c <= '\u9FFF')
            || c == '々'
            || (c >= 'ぁ' && c <= 'ん')
            || (c >= 'ァ' && c <= 'ヶ')
            || (c >= '！' && c <= '＠')
            || c == '、'
            || c == '。';
    }

    private static bool HasHangulDetectionRunIgnoringWhitespace(string text)
    {
        int runLength = 0;
        for (int i = 0; i < (text?.Length ?? 0); i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            if (c >= '\uAC00' && c <= '\uD7AF')
            {
                runLength++;
                if (runLength >= 2)
                {
                    return true;
                }
            }
            else
            {
                runLength = 0;
            }
        }
        return false;
    }

    private static bool HasInvalidJapaneseKanaSequence(string text)
    {
        for (int i = 0; i + 1 < (text?.Length ?? 0); i++)
        {
            char previous = text[i];
            char current = text[i + 1];
            if (IsHalfWidthKanaSymbol(previous) && IsHalfWidthKanaSymbol(current))
            {
                return true;
            }
            if (current == 'ﾟ' && !(previous >= 'ﾊ' && previous <= 'ﾎ'))
            {
                return true;
            }
            if (current == 'ﾞ' && !((previous >= 'ｶ' && previous <= 'ﾄ') || (previous >= 'ﾊ' && previous <= 'ﾎ')))
            {
                return true;
            }
            if (IsHalfWidthSmallYaYuYo(current)
                && previous != 'ｷ'
                && previous != 'ｼ'
                && previous != 'ﾁ'
                && previous != 'ﾆ'
                && previous != 'ﾋ'
                && previous != 'ﾐ'
                && previous != 'ﾘ'
                && previous != 'ﾞ'
                && previous != 'ﾟ')
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsHalfWidthKanaSymbol(char c)
    {
        return (c >= 'ｦ' && c <= 'ｯ') || c == 'ﾞ' || c == 'ﾟ';
    }

    private static bool IsHalfWidthSmallYaYuYo(char c)
    {
        return c == 'ｬ' || c == 'ｭ' || c == 'ｮ';
    }

    public static bool IsZeroNoteBMSFile(string filePath)
    {
        return !File.ReadLines(filePath, Encoding.GetEncoding("shift_jis")).Any((string line) => visibleObjectChRegex.IsMatch(line));
    }
}
