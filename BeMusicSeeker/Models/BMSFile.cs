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
using Ribbit.Threading;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models;

/// <summary>
/// `maintenanceInfo` がどの経路で得られた値かを表します。
/// ResourceHealth の正本判定で lazy placeholder と DB/計算済み snapshot を区別するために使います。
/// </summary>
internal enum MaintenanceInfoOrigin
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
    private const int ChartInfoFeatureUndefinedLongNote = 1;
    private const int ChartInfoFeatureRandom = 4;
    private const int ChartInfoFeatureLongNote = 8;
    private const int ChartInfoFeatureChargeNote = 16;
    private const int ChartInfoFeatureHellChargeNote = 32;

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

    internal static IDisposable SuppressPropertyChangedScope()
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
        var file = new BMSFile
        {
            _hash = NormalizeMd5HashFromDb(GetRawValue(values, 0)),
            _title = GetRawValue(values, 1),
            _subtitle = GetRawValue(values, 2),
            _artist = GetRawValue(values, 3),
            _subartist = GetRawValue(values, 4),
            genre = GetRawValue(values, 5),
            _tag = GetRawValue(values, 6),
            _path = GetRawValue(values, 7),
            type = ParseNullableIntFromDb(GetRawValue(values, 8)),
            folder = GetRawValue(values, 9),
            _stagefile = GetRawValue(values, 10),
            _banner = GetRawValue(values, 11),
            _backbmp = GetRawValue(values, 12),
            parent = GetRawValue(values, 13),
            level = ParseNullableIntFromDb(GetRawValue(values, 14)),
            difficulty = ParseNullableIntFromDb(GetRawValue(values, 15)),
            maxbpm = ParseNullableIntFromDb(GetRawValue(values, 16)),
            minbpm = ParseNullableIntFromDb(GetRawValue(values, 17)),
            mode = ParseNullableIntFromDb(GetRawValue(values, 18)),
            _judge = ParseNullableIntFromDb(GetRawValue(values, 19)),
            longnote = ParseNullableIntFromDb(GetRawValue(values, 20)),
            bga = ParseNullableIntFromDb(GetRawValue(values, 21)),
            random = ParseNullableIntFromDb(GetRawValue(values, 22)),
            date = ParseNullableIntFromDb(GetRawValue(values, 23)),
            favorite = ParseNullableIntFromDb(GetRawValue(values, 24)),
            txt = ParseNullableIntFromDb(GetRawValue(values, 25)),
            _karinotes = ParseNullableIntFromDb(GetRawValue(values, 26)),
            adddate = ParseNullableIntFromDb(GetRawValue(values, 27)),
            exlevel = ParseNullableIntFromDb(GetRawValue(values, 28))
        };
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
        PLAYALL = 0x1F
    }

    private BMSFileStatus _status;

    private ChartWarningCollection _warnings;

    private string _cachedComposedTitle;

    private string _cachedComposedTitleSource;

    private string _cachedComposedSubtitleSource;

    private BMSFileMaintenanceInfo _maintenanceInfo;

    private MaintenanceInfoOrigin maintenanceInfoOrigin;

    private string _sha256;

    private BmsEncodingDetectionResult snapshotEncodingDetectionResult;

    private string snapshotEncodingDetectionPath;

    private string snapshotEncodingDetectionMd5;

    private string snapshotEncodingDetectionSha256;

    private readonly object lockObject = new();

    private PropertyChangedSubscription listenerForBMSScore;

    private BMSScore _bmsScore;

    private readonly ReaderWriterLockSlim rwlock = new();

    private readonly object filesCacheLock = new();

    // BMS のチャンネル行は ':' 区切りが一般的だが、LR2/beatoraja では空白だけで
    // コマンドと値を区切る譜面も実質的に受け入れられているため、ゼロノート検出でも
    // ':' または空白列を区切りとして扱う。
    // 通常ノート (11-19/21-29) に加え、RDM 記法の LN チャンネル (51-69 系。互換のため 5Z/6Z まで)
    // も可視ノートとして扱う。データ部は 2 桁 object 列として見て、00 だけの行は無視する。
    private static readonly Regex visibleObjectChRegex = new("^[\\s\u3000]*#[0-9]{3}(?:[12][1-9A-Z]|[56][1-9A-Z])(?:[\\s\u3000]*:[\\s\u3000]*|[\\s\u3000]+)(?:[\\s\u3000]*00)*[\\s\u3000]*(?!00)[0-9A-Z]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static readonly Encoding koreanEnc = Encoding.GetEncoding("ks_c_5601-1987", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static readonly Encoding utf8Enc = Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback());

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

    private static readonly char[] invalidPathCharas = Path.GetInvalidPathChars();

    public override string hash
    {
        get
        {
            return base.hash;
        }
        set
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

    internal string GetRawTitleForDisplay()
    {
        return _title ?? string.Empty;
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

    public virtual string Level
    {
        get
        {
            return base.level.ToString();
        }
    }

    public virtual string Folder
    {
        get
        {
            return Path.GetFileName(Path.GetDirectoryName(path));
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

    internal List<ChartResourceReference> ResourceReferences { get; set; } = [];

    internal List<UnsupportedChartResourceReference> UnsupportedResourceReferences { get; set; } = [];

    internal ChartWarningCollection Warnings => _warnings ??= new ChartWarningCollection(RaiseWarningPresentationChanged, () => string.Empty);

    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        _warnings?.RemoveCategory(category);
    }

    internal bool HasWarningCategory(ChartWarningCategory category)
    {
        return _warnings?.ContainsCategory(category) == true;
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

    internal void RaiseWarningPresentationChanged()
    {
        RaisePropertyChanged(() => Warnings);
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
            SetMaintenanceInfo(value, suppressPropertyChanged: false);
        }
    }

    /// <summary>
    /// 現在保持している `maintenanceInfo` がどの経路で得られたかを返します。
    /// </summary>
    internal MaintenanceInfoOrigin MaintenanceInfoOrigin => maintenanceInfoOrigin;

    /// <summary>
    /// maintenance snapshot を差し替えます。
    /// origin を明示しない既存呼び出しは、null を placeholder、非 null を計算済み snapshot として扱います。
    /// </summary>
    /// <param name="value">設定する maintenance snapshot。null の場合は placeholder を作ります。</param>
    /// <param name="suppressPropertyChanged">`maintenanceInfo` 自体の PropertyChanged を抑止するかどうか。</param>
    /// <param name="origin">snapshot の由来。未指定の場合は互換既定値を使います。</param>
    internal void SetMaintenanceInfo(BMSFileMaintenanceInfo value, bool suppressPropertyChanged = false, MaintenanceInfoOrigin? origin = null)
    {
        MaintenanceInfoOrigin nextOrigin = origin ?? (value == null ? MaintenanceInfoOrigin.Placeholder : MaintenanceInfoOrigin.Calculated);
        value ??= new BMSFileMaintenanceInfo(this);
        if (_maintenanceInfo == value)
        {
            maintenanceInfoOrigin = nextOrigin;
            Lr2CompatibilityWarningProjection.ApplyTo(this, value);
            return;
        }
        if (value.hash != hash)
        {
            throw new ArgumentException("maintenanceInfo の MD5 が一致しません。");
        }
        _maintenanceInfo = value;
        maintenanceInfoOrigin = nextOrigin;
        Lr2CompatibilityWarningProjection.ApplyTo(this, value);
        if (!suppressPropertyChanged)
        {
            RaisePropertyChanged("maintenanceInfo");
        }
    }

    internal bool HasMaintenanceInfoHash(string expectedHash)
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
        if (encodingChanged || healthChanged)
        {
            RaisePropertyChanged(() => maintenanceInfo);
        }
    }

    /// <summary>
    /// maintenance evaluation が durable write 前に触れる runtime state を保存します。
    /// durable write が失敗した場合に live state と notification を元へ戻すために使います。
    /// </summary>
    internal sealed class MaintenanceMutationSnapshot
    {
        private readonly BMSFile file;
        private readonly string title;
        private readonly string subtitle;
        private readonly string artist;
        private readonly string subartist;
        private readonly string genre;
        private readonly int? date;
        private readonly int? adddate;
        private readonly string hash;
        private readonly string sha256;
        private readonly HashSet<string> wavFiles;
        private readonly HashSet<string> bgaFiles;
        private readonly List<ChartResourceReference> resourceReferences;
        private readonly List<UnsupportedChartResourceReference> unsupportedResourceReferences;
        private readonly BMSFileMaintenanceInfo originalMaintenanceInfo;
        private readonly BMSFileMaintenanceInfo maintenanceInfo;
        private readonly MaintenanceInfoOrigin maintenanceInfoOrigin;

        private MaintenanceMutationSnapshot(BMSFile file)
        {
            this.file = file ?? throw new ArgumentNullException(nameof(file));
            title = file._title;
            subtitle = file._subtitle;
            artist = file._artist;
            subartist = file._subartist;
            genre = file.genre;
            date = file.date;
            adddate = file.adddate;
            hash = file.hash;
            sha256 = file.sha256;
            wavFiles = file.WAVfiles == null ? null : new HashSet<string>(file.WAVfiles, StringComparer.OrdinalIgnoreCase);
            bgaFiles = file.BGAfiles == null ? null : new HashSet<string>(file.BGAfiles, StringComparer.OrdinalIgnoreCase);
            resourceReferences = file.ResourceReferences == null ? null : [.. file.ResourceReferences];
            unsupportedResourceReferences = file.UnsupportedResourceReferences == null ? null : [.. file.UnsupportedResourceReferences];
            originalMaintenanceInfo = file._maintenanceInfo;
            maintenanceInfo = file._maintenanceInfo?.CreatePersistenceCopy();
            maintenanceInfoOrigin = file.maintenanceInfoOrigin;
        }

        internal static MaintenanceMutationSnapshot Capture(BMSFile file)
        {
            return file == null ? null : new MaintenanceMutationSnapshot(file);
        }

        internal void Restore()
        {
            using (SuppressPropertyChangedScope())
            using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
            {
                file._title = title;
                file._subtitle = subtitle;
                file._artist = artist;
                file._subartist = subartist;
                if (!string.Equals(file.genre, genre, StringComparison.Ordinal))
                {
                    file.genre = genre;
                }
                file.date = date;
                file.adddate = adddate;
                file.hash = hash;
                file.sha256 = sha256;
                file.WAVfiles = wavFiles == null ? null : new HashSet<string>(wavFiles, StringComparer.OrdinalIgnoreCase);
                file.BGAfiles = bgaFiles == null ? null : new HashSet<string>(bgaFiles, StringComparer.OrdinalIgnoreCase);
                file.ResourceReferences = resourceReferences == null ? null : [.. resourceReferences];
                file.UnsupportedResourceReferences = unsupportedResourceReferences == null ? null : [.. unsupportedResourceReferences];
                RestoreMaintenanceInfo(originalMaintenanceInfo, maintenanceInfo);
                file.maintenanceInfoOrigin = maintenanceInfoOrigin;
                file._cachedComposedTitle = null;
                file._cachedComposedTitleSource = null;
                file._cachedComposedSubtitleSource = null;
                file.ReplaceWarningsByCategory(
                    ChartWarningCategory.Lr2Compatibility,
                    Lr2CompatibilityWarningProjection.BuildWarnings(file._maintenanceInfo));
            }
        }

        internal void ApplyPreparedState()
        {
            using (SuppressPropertyChangedScope())
            using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
            {
                file._title = title;
                file._subtitle = subtitle;
                file._artist = artist;
                file._subartist = subartist;
                file.genre = genre;
                file.date = date;
                file.adddate = adddate;
                file.hash = hash;
                file.sha256 = sha256;
                file.WAVfiles = wavFiles == null ? null : new HashSet<string>(wavFiles, StringComparer.OrdinalIgnoreCase);
                file.BGAfiles = bgaFiles == null ? null : new HashSet<string>(bgaFiles, StringComparer.OrdinalIgnoreCase);
                file.ResourceReferences = resourceReferences == null ? null : [.. resourceReferences];
                file.UnsupportedResourceReferences = unsupportedResourceReferences == null ? null : [.. unsupportedResourceReferences];
                RestoreMaintenanceInfo(file._maintenanceInfo, maintenanceInfo);
                file.maintenanceInfoOrigin = maintenanceInfoOrigin;
                file._cachedComposedTitle = null;
                file._cachedComposedTitleSource = null;
                file._cachedComposedSubtitleSource = null;
                file.ReplaceWarningsByCategory(
                    ChartWarningCategory.Lr2Compatibility,
                    Lr2CompatibilityWarningProjection.BuildWarnings(file._maintenanceInfo));
            }
        }

        internal void NotifyCommittedChanges()
        {
            bool titleChanged = !string.Equals(file._title, title, StringComparison.Ordinal)
                || !string.Equals(file._subtitle, subtitle, StringComparison.Ordinal);
            bool artistChanged = !string.Equals(file._artist, artist, StringComparison.Ordinal)
                || !string.Equals(file._subartist, subartist, StringComparison.Ordinal);
            bool encodingChanged = !string.Equals(
                file.TryGetMaintenanceInfoWithoutCreating()?.encoding,
                maintenanceInfo?.encoding,
                StringComparison.Ordinal);
            bool healthChanged = !HasSameHealth(file.TryGetMaintenanceInfoWithoutCreating(), maintenanceInfo);
            if (titleChanged)
            {
                file.RaisePropertyChanged("Title");
            }
            if (artistChanged)
            {
                file.RaisePropertyChanged("Artist");
            }
            file.NotifyMaintenanceInfoChanged(encodingChanged, healthChanged);
        }

        private static bool HasSameHealth(BMSFileMaintenanceInfo left, BMSFileMaintenanceInfo right)
        {
            return left?.WAVHealth == right?.WAVHealth
                && left?.BGAHealth == right?.BGAHealth
                && left?.MovieHealth == right?.MovieHealth
                && left?.StagefileHealth == right?.StagefileHealth
                && left?.BannerHealth == right?.BannerHealth
                && left?.BackbmpHealth == right?.BackbmpHealth;
        }

        private void RestoreMaintenanceInfo(
            BMSFileMaintenanceInfo target,
            BMSFileMaintenanceInfo source)
        {
            if (source == null)
            {
                file._maintenanceInfo = null;
                return;
            }
            target ??= new BMSFileMaintenanceInfo();
            target.ApplyPersistenceCopyFrom(source);
            file._maintenanceInfo = target;
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
            }
        }
    }

    internal void PublishScoreAttachmentChanged()
    {
        RaisePropertyChanged(nameof(bmsScore));
    }

    private void registrateBMSScorePropertyChangedEventHandlers()
    {
        DisposeBmsScoreListener();
        if (bmsScore == null)
        {
            return;
        }
        listenerForBMSScore = PropertyChangedSubscription.Create(bmsScore);
        listenerForBMSScore.RegisterHandler(() => bmsScore.ranking, delegate
        {
            RaisePropertyChanged(() => bmsScore);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.rankingNum, delegate
        {
            RaisePropertyChanged(() => bmsScore);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.rankingLastupdate, delegate
        {
            RaisePropertyChanged(() => bmsScore);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.stddevVal, delegate
        {
            RaisePropertyChanged(() => bmsScore);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.scoreDifficulty, delegate
        {
            RaisePropertyChanged(() => bmsScore);
        });
        listenerForBMSScore.RegisterHandler(() => bmsScore.IsLr2IrScoreUnsent, delegate
        {
            RaisePropertyChanged(() => bmsScore);
            RaisePropertyChanged(() => status);
        });
    }

    private int DisposeBmsScoreListener()
    {
        int result = ((listenerForBMSScore != null) ? 1 : 0);
        listenerForBMSScore?.Dispose();
        listenerForBMSScore = null;
        return result;
    }

    internal void SetEncosingInfo(BMSFileMaintenanceInfo mtInfo = null)
    {
        if (LongPathFileSystem.FileExists(path))
        {
            mtInfo ??= maintenanceInfo;
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
        BmsEncodingDetectionResult detectionResult = TryGetSnapshotEncodingDetectionResult(snapshot)
            ?? DetectEncodingOfBMSFileDetailed(snapshot);
        SetEncodingInfoFromDetection(detectionResult, mtInfo);
        return detectionResult;
    }

    internal void RememberSnapshotEncodingDetectionResult(
        ChartFileSnapshot snapshot,
        BmsEncodingDetectionResult detectionResult)
    {
        if (snapshot == null || detectionResult == null)
        {
            return;
        }
        snapshotEncodingDetectionResult = detectionResult;
        snapshotEncodingDetectionPath = snapshot.Path;
        snapshotEncodingDetectionMd5 = snapshot.Md5;
        snapshotEncodingDetectionSha256 = snapshot.Sha256;
    }

    internal BmsEncodingDetectionResult TryGetSnapshotEncodingDetectionResult(ChartFileSnapshot snapshot)
    {
        if (snapshot == null
            || snapshotEncodingDetectionResult == null
            || !string.Equals(snapshotEncodingDetectionPath, snapshot.Path, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshotEncodingDetectionMd5, snapshot.Md5, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshotEncodingDetectionSha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return snapshotEncodingDetectionResult;
    }

    private void SetEncodingInfoFromDetection(BmsEncodingDetectionResult detectionResult, BMSFileMaintenanceInfo mtInfo = null)
    {
        if (detectionResult == null)
        {
            throw new ArgumentNullException(nameof(detectionResult));
        }
        mtInfo ??= maintenanceInfo;
        mtInfo.encoding = detectionResult.EncodingName;
        mtInfo.is_encoding_fixed = false;
    }

    internal void ClearResourceReferenceCollections()
    {
        lock (filesCacheLock)
        {
            WAVfiles = null;
            BGAfiles = null;
            ResourceReferences = null;
            UnsupportedResourceReferences = null;
        }
    }

    internal void ReplaceResourceReferences(
        string stagefile,
        string banner,
        string backbmp,
        IEnumerable<string> wavFiles,
        IEnumerable<string> bgaFiles,
        IEnumerable<UnsupportedChartResourceReference> unsupportedResourceReferences = null)
    {
        this.stagefile = stagefile;
        this.banner = banner;
        this.backbmp = backbmp;
        WAVfiles = new HashSet<string>(wavFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BGAfiles = new HashSet<string>(bgaFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ResourceReferences = [];
        UnsupportedResourceReferences = [.. (unsupportedResourceReferences ?? [])];
    }

    internal void SetMode()
    {
        BMSFile bMSFile = CreateBMSFileFromFile(path);
        mode = bMSFile.mode;
    }

    /// <summary>
    /// Path-based parser entry point. New single-read flows should prefer
    /// <see cref="CreateBMSFileFromSnapshot"/> so lightweight metadata and chart_info can share bytes.
    /// </summary>
    internal static BMSFile CreateBMSFileFromFile(string filePath, string codepageName = "shift_jis")
    {
        return CreateBMSFileFromLines(
            ReadFileLines(filePath, codepageName),
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
        var encoding = Encoding.GetEncoding(codepageName);
        bool detectEncodingFromByteOrderMarks = !IsShiftJisEncodingName(codepageName);
        BMSFile file = CreateBMSFileFromLines(
            ReadSnapshotLines(snapshot, encoding, detectEncodingFromByteOrderMarks),
            snapshot.Path,
            () => snapshot.Md5,
            () => snapshot.Sha256);
        ApplyLr2Cp932ResourceDecodeWarning(file, snapshot);
        return file;
    }

    internal static BMSFile CreateBMSFileFromSnapshot(
        ChartFileSnapshot snapshot,
        BmsEncodingDetectionResult detectionResult)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (detectionResult == null)
        {
            return CreateBMSFileFromSnapshot(snapshot);
        }

        BMSFile file = CreateBMSFileFromSnapshot(snapshot);
        ApplyDetectedBmsMetadata(file, snapshot, detectionResult);
        return file;
    }

    internal void PreserveUserSongColumnsFrom(BMSFile existing)
    {
        if (existing == null)
        {
            return;
        }
        PreserveUserSongColumns(existing.favorite, existing.adddate, existing.tag);
    }

    internal void PreserveUserSongColumns(int? favoriteValue, int? adddateValue, string tagValue)
    {
        favorite = favoriteValue;
        adddate = adddateValue;
        tag = tagValue;
    }

    internal void ApplyLr2LightweightDefaults()
    {
        level ??= 0;
        difficulty ??= -1;
        mode ??= 5;
        judge ??= 2;
    }

    internal BMSFile CreateSongRowPersistenceCopy()
    {
        return new BMSFile
        {
            _hash = _hash,
            _title = _title,
            _subtitle = _subtitle,
            _artist = _artist,
            _subartist = _subartist,
            genre = genre,
            _tag = _tag,
            _path = _path,
            type = type,
            folder = null,
            _stagefile = _stagefile,
            _banner = _banner,
            _backbmp = _backbmp,
            parent = null,
            level = level,
            difficulty = difficulty,
            maxbpm = maxbpm,
            minbpm = minbpm,
            mode = mode,
            _judge = _judge,
            longnote = longnote,
            bga = bga,
            random = random,
            date = date,
            favorite = favorite,
            txt = txt,
            _karinotes = _karinotes,
            adddate = adddate,
            exlevel = exlevel,
            _sha256 = _sha256
        };
    }

    internal void SetTextGroupFlag(int value)
    {
        txt = value == 0 ? 0 : 1;
    }

    internal void ApplyLr2ChartInfoColumns(LR2SongDBExtended.chart_info chartInfo)
    {
        ApplyLr2ChartInfoColumns(chartInfo, includeLightweightMetadata: true);
    }

    internal void ApplyLr2ChartInfoDetailedColumns(LR2SongDBExtended.chart_info chartInfo)
    {
        ApplyLr2ChartInfoColumns(chartInfo, includeLightweightMetadata: false, includeChartMetadata: true);
    }

    private void ApplyLr2ChartInfoColumns(
        LR2SongDBExtended.chart_info chartInfo,
        bool includeLightweightMetadata,
        bool includeChartMetadata = false)
    {
        if (chartInfo == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(hash)
            && !string.IsNullOrWhiteSpace(chartInfo.md5)
            && !string.Equals(hash, chartInfo.md5, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (includeLightweightMetadata)
        {
            level = chartInfo.level;
            difficulty = chartInfo.difficulty;
            mode = chartInfo.mode;
        }
        else if (includeChartMetadata)
        {
            level = chartInfo.level;
            difficulty = chartInfo.difficulty;
        }
        maxbpm = ToLr2SongInteger(chartInfo.maxbpm);
        minbpm = ToLr2SongInteger(chartInfo.minbpm);
        bga = chartInfo.bga;
        exlevel = chartInfo.exlevel ?? 0;
        longnote = HasLongNoteFeature(chartInfo.feature) ? 1 : 0;
        random = (chartInfo.feature & ChartInfoFeatureRandom) != 0 ? 1 : 0;
        karinotes = chartInfo.notes;
    }

    private static int? ToLr2SongInteger(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }
        if (value.Value > int.MaxValue)
        {
            return int.MaxValue;
        }
        if (value.Value < int.MinValue)
        {
            return int.MinValue;
        }
        return (int)value.Value;
    }

    private static bool HasLongNoteFeature(int feature)
    {
        const int longNoteFlags = ChartInfoFeatureUndefinedLongNote
            | ChartInfoFeatureLongNote
            | ChartInfoFeatureChargeNote
            | ChartInfoFeatureHellChargeNote;
        return (feature & longNoteFlags) != 0;
    }

    private static IEnumerable<string> ReadSnapshotLines(ChartFileSnapshot snapshot, Encoding encoding, bool detectEncodingFromByteOrderMarks)
    {
        using var stream = new MemoryStream(snapshot.Bytes, writable: false);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static BMSFile CreateBMSFileFromLines(
        IEnumerable<string> enumerable,
        string filePath,
        Func<string> md5Provider,
        Func<string> sha256Provider)
    {
        var bMSFile = new BMSFile
        {
            level = 0,
            difficulty = -1,
            mode = 5,
            judge = 2
        };
        var hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceReferences = new List<ChartResourceReference>();
        var unsupportedReferences = new List<UnsupportedChartResourceReference>();
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
        bool forcePmsMode = filePath.EndsWith(".pms", StringComparison.OrdinalIgnoreCase);
        foreach (string item in enumerable)
        {
            if (IsFpDscDirective(item))
            {
                forcePmsMode = true;
                continue;
            }
            if (IsLr2CustomFolderDirective(item))
            {
                bMSFile.judge = 2;
                continue;
            }
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
                    case '5':
                        switch (lane)
                        {
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
                    case '6':
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
                    AddNormalizedResourceReference(hashSet, value, unsupportedReferences, resourceReferences, ChartResourceKind.Audio);
                    break;
                case BmsDirective.Bmp:
                    AddNormalizedResourceReference(hashSet2, value, unsupportedReferences, resourceReferences, ChartResourceKind.Unknown);
                    break;
                case BmsDirective.Title:
                    if (string.IsNullOrWhiteSpace(bMSFile.title))
                    {
                        bMSFile.title = value;
                    }
                    bMSFile.difficulty = InferLr2DifficultyFromText(value, GetCurrentLr2Difficulty(bMSFile));
                    break;
                case BmsDirective.Genre:
                    if (string.IsNullOrWhiteSpace(bMSFile.genre))
                    {
                        bMSFile.genre = value;
                    }
                    bMSFile.difficulty = InferLr2DifficultyFromText(value, GetCurrentLr2Difficulty(bMSFile));
                    break;
                case BmsDirective.Artist:
                    if (string.IsNullOrWhiteSpace(bMSFile.artist))
                    {
                        bMSFile.artist = value;
                    }
                    break;
                case BmsDirective.PlayLevel:
                    bMSFile.level = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.MaxTracks:
                    bMSFile.level = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.Difficulty:
                    bMSFile.difficulty = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.Rank:
                    bMSFile.judge = ParseLr2DirectiveInt(value);
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
        bMSFile.ResourceReferences = resourceReferences;
        bMSFile.UnsupportedResourceReferences = unsupportedReferences;
        bMSFile.hash = md5Provider();
        bMSFile.ApplySha256(sha256Provider());
        bMSFile.path = filePath;
        if (forcePmsMode)
        {
            bMSFile.mode = 9;
        }
        else if (flag17 || flag18)
        {
            bMSFile.mode = 14;
        }
        else if (flag10 || flag11 || flag12 || flag13 || flag14 || flag15 || flag16)
        {
            bMSFile.mode = flag8 || flag9 ? 14 : 10;
        }
        else if (flag8 || flag9)
        {
            bMSFile.mode = 7;
        }
        return bMSFile;
    }

    private static int GetCurrentLr2Difficulty(BMSFile bMSFile)
    {
        return bMSFile?.difficulty ?? -1;
    }

    private static string TrimLr2HeaderValue(string value)
    {
        return (value ?? string.Empty).TrimEnd(' ', '\t', '\r', '\n');
    }

    private static int InferLr2DifficultyFromText(string value, int currentDifficulty)
    {
        string text = TrimLr2HeaderValue(value);
        int difficulty = currentDifficulty;
        if (EndsWithAsciiIgnoreCase(text, "HARD"))
        {
            difficulty = 2;
        }
        if (EndsWithAsciiIgnoreCase(text, "HYPER"))
        {
            difficulty = 3;
        }
        if (EndsWithAsciiIgnoreCase(text, "ANOTHER"))
        {
            difficulty = 4;
        }
        if (EndsWithAsciiIgnoreCase(text, "EASY"))
        {
            difficulty = 1;
        }
        if (EndsWithAsciiIgnoreCase(text, "EX"))
        {
            difficulty = 4;
        }
        if (EndsWithAsciiIgnoreCase(text, "MANIAC"))
        {
            difficulty = 4;
        }
        if (TrySplitLr2DifficultyToken(text, out _, out _, out int tokenDifficulty) && tokenDifficulty != 0)
        {
            difficulty = tokenDifficulty;
        }
        return difficulty;
    }

    private static bool TrySplitLr2DifficultyToken(string value, out string left, out string right, out int difficulty)
    {
        string text = TrimLr2HeaderValue(value);
        if (TrySplitLr2DifficultyToken(text, "(", ")", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "[", "]", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "-", "-", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "\"", "\"", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "<", ">", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "～", "～", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "【", "】", out left, out right, out difficulty))
        {
            return true;
        }

        left = text;
        right = string.Empty;
        difficulty = 0;
        return false;
    }

    private static bool TrySplitLr2DifficultyToken(
        string value,
        string tokenLeft,
        string tokenRight,
        out string left,
        out string right,
        out int difficulty)
    {
        left = string.Empty;
        right = string.Empty;
        difficulty = 0;
        if (string.IsNullOrEmpty(value) || !value.EndsWith(tokenRight, StringComparison.Ordinal))
        {
            return false;
        }

        int searchLength = value.Length - tokenRight.Length;
        if (searchLength <= 0)
        {
            return false;
        }
        int position = value.LastIndexOf(tokenLeft, searchLength - 1, searchLength, StringComparison.Ordinal);
        if (position < 1)
        {
            return false;
        }

        left = value.Substring(0, position).Trim(' ', '\t');
        right = value.Substring(left.Length).Trim(' ', '\t');
        if (!TryInferLr2DifficultyFromToken(right, out difficulty))
        {
            difficulty = 0;
        }
        return true;
    }

    private static bool TryInferLr2DifficultyFromToken(string value, out int difficulty)
    {
        string text = value ?? string.Empty;
        if (IndexOfAsciiIgnoreCase(text, "BEG") > 0)
        {
            difficulty = 1;
            return true;
        }
        if (IndexOfAsciiIgnoreCase(text, "HARD") > 0
            || IndexOfAsciiIgnoreCase(text, "HYPE") > 0
            || IndexOfAsciiIgnoreCase(text, "HD") > 0
            || IndexOfAsciiIgnoreCase(text, "5H") > 0
            || IndexOfAsciiIgnoreCase(text, "7H") > 0
            || IndexOfAsciiIgnoreCase(text, "10H") > 0
            || IndexOfAsciiIgnoreCase(text, "14H") > 0
            || IndexOfAsciiIgnoreCase(text, "9H") > 0
            || IndexOfAsciiIgnoreCase(text, "DIF") > 0)
        {
            difficulty = 3;
            return true;
        }
        if (IndexOfAsciiIgnoreCase(text, "VERYHARD") > 0
            || IndexOfAsciiIgnoreCase(text, "EX") > 0
            || IndexOfAsciiIgnoreCase(text, "AN") > 0
            || IndexOfAsciiIgnoreCase(text, "SHD") > 0
            || IndexOfAsciiIgnoreCase(text, "5A") > 0
            || IndexOfAsciiIgnoreCase(text, "7A") > 0
            || IndexOfAsciiIgnoreCase(text, "10A") > 0
            || IndexOfAsciiIgnoreCase(text, "14A") > 0
            || IndexOfAsciiIgnoreCase(text, "9A") > 0
            || IndexOfAsciiIgnoreCase(text, "ULT") > 0
            || IndexOfAsciiIgnoreCase(text, "MANI") > 0
            || IndexOfAsciiIgnoreCase(text, "LUNA") > 0
            || IndexOfAsciiIgnoreCase(text, "AHO") > 0
            || IndexOfAsciiIgnoreCase(text, "AFO") > 0
            || IndexOfAsciiIgnoreCase(text, "ASDF") > 0
            || IndexOfAsciiIgnoreCase(text, "HELL") > 0)
        {
            difficulty = 4;
            return true;
        }

        difficulty = 0;
        return false;
    }

    private static bool EndsWithAsciiIgnoreCase(string value, string suffix)
    {
        if (value == null || suffix == null || value.Length < suffix.Length)
        {
            return false;
        }
        int start = value.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (ToUpperAscii(value[start + i]) != suffix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static int IndexOfAsciiIgnoreCase(string value, string match)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(match) || match.Length > value.Length)
        {
            return -1;
        }
        for (int i = 0; i <= value.Length - match.Length; i++)
        {
            if (StartsWithAsciiIgnoreCase(value, i, match))
            {
                return i;
            }
        }
        return -1;
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
        MaxTracks,
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
        else if (IsLr2NumericDirective(directive))
        {
            if (index >= line.Length)
            {
                directive = BmsDirective.Unknown;
                return false;
            }
            index++;
        }
        else if (index >= line.Length || !char.IsWhiteSpace(line[index]))
        {
            directive = BmsDirective.Unknown;
            return false;
        }
        else
        {
            index++;
        }
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        valueStart = index;
        return true;
    }

    private static bool IsLr2NumericDirective(BmsDirective directive)
    {
        return directive == BmsDirective.PlayLevel
            || directive == BmsDirective.MaxTracks
            || directive == BmsDirective.Difficulty
            || directive == BmsDirective.Rank;
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
                if (EqualsAsciiIgnoreCase(line, start, "MAXTRACKS"))
                {
                    directive = BmsDirective.MaxTracks;
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

    private static void AddNormalizedResourceReference(
        HashSet<string> references,
        string value,
        ICollection<UnsupportedChartResourceReference> unsupportedReferences = null,
        ICollection<ChartResourceReference> resourceReferences = null,
        ChartResourceKind kind = ChartResourceKind.Unknown)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(value);
        if (result.IsValid)
        {
            references.Add(result.NormalizedPath);
            ChartResourceKind resolvedKind = kind == ChartResourceKind.Unknown ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(value) : kind;
            resourceReferences?.Add(new ChartResourceReference(resolvedKind, value, result.NormalizedPath));
            return;
        }
        if (result.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
        {
            unsupportedReferences?.Add(new UnsupportedChartResourceReference(
                kind == ChartResourceKind.Unknown ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(value) : kind,
                value,
                result.Status));
        }
    }

    private static bool IsFpDscDirective(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.TrimStart();
        return trimmed.StartsWith("#FP/DSC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLr2CustomFolderDirective(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.TrimStart();
        return trimmed.StartsWith("#CUSTOMFOLDER", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseLr2DirectiveInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        string trimmed = value.TrimStart();
        int sign = 1;
        int index = 0;
        if (index < trimmed.Length && (trimmed[index] == '+' || trimmed[index] == '-'))
        {
            sign = trimmed[index] == '-' ? -1 : 1;
            index++;
        }

        long result = 0;
        bool hasDigit = false;
        while (index < trimmed.Length && IsAsciiDigit(trimmed[index]))
        {
            hasDigit = true;
            result = (result * 10) + (trimmed[index] - '0');
            long signed = sign < 0 ? -result : result;
            if (signed > int.MaxValue)
            {
                return int.MaxValue;
            }
            if (signed < int.MinValue)
            {
                return int.MinValue;
            }
            index++;
        }
        return hasDigit ? (int)(sign * result) : 0;
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
        if (!((channelGroup == '1'
                || channelGroup == '2'
                || channelGroup == '3'
                || channelGroup == '4'
                || channelGroup == '5'
                || channelGroup == '6')
            && lane >= '1'
            && lane <= '9'))
        {
            return false;
        }
        return true;
    }

    private static bool IsAsciiDigit(char value)
    {
        return value >= '0' && value <= '9';
    }

    internal static void SetBMSComponentFilesFromBMSFile(BMSFile bmsFile, string codepageName = "shift_jis")
    {
        IEnumerable<string> enumerable = ReadFileLines(bmsFile.path, codepageName);
        var hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceReferences = new List<ChartResourceReference>();
        var unsupportedReferences = new List<UnsupportedChartResourceReference>();
        foreach (string item in enumerable)
        {
            if (!TryParseDirectiveLine(item, out BmsDirective directive, out int valueStart))
            {
                continue;
            }
            string value = valueStart >= 0 && valueStart <= item.Length ? item.Substring(valueStart) : string.Empty;
            if (directive == BmsDirective.Wav)
            {
                AddNormalizedResourceReference(hashSet, value, unsupportedReferences, resourceReferences, ChartResourceKind.Audio);
            }
            else if (directive == BmsDirective.Bmp)
            {
                AddNormalizedResourceReference(hashSet2, value, unsupportedReferences, resourceReferences, ChartResourceKind.Unknown);
            }
        }
        bmsFile.WAVfiles = hashSet;
        bmsFile.BGAfiles = hashSet2;
        bmsFile.ResourceReferences = resourceReferences;
        bmsFile.UnsupportedResourceReferences = unsupportedReferences;
        bmsFile.hash = getMD5Hash(bmsFile.path);
        bmsFile.ApplySha256(GetSHA256Hash(bmsFile.path));
    }

    internal void ApplyComponentFilesFromParsedSnapshot(BMSFile parsedFile)
    {
        if (parsedFile == null)
        {
            return;
        }
        WAVfiles = parsedFile.WAVfiles;
        BGAfiles = parsedFile.BGAfiles;
        ResourceReferences = [.. (parsedFile.ResourceReferences ?? [])];
        UnsupportedResourceReferences = [.. (parsedFile.UnsupportedResourceReferences ?? [])];
        hash = parsedFile.hash;
        ApplySha256(parsedFile.sha256);
    }

    private static string getMD5Hash(string filePath)
    {
        var mD = MD5.Create();
        byte[] array;
        using (var inputStream = LongPathFileSystem.OpenRead(filePath))
        {
            array = mD.ComputeHash(inputStream);
        }
        var stringBuilder = new StringBuilder();
        byte[] array2 = array;
        foreach (byte b in array2)
        {
            stringBuilder.Append(b.ToString("x2"));
        }
        return stringBuilder.ToString();
    }

    internal static string GetSHA256Hash(string filePath)
    {
        using var sHA = SHA256.Create();
        byte[] hash;
        using (var inputStream = LongPathFileSystem.OpenRead(filePath))
        {
            hash = sHA.ComputeHash(inputStream);
        }
        var stringBuilder = new StringBuilder(hash.Length * 2);
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

    internal static void ReloadBMSFileWithEncoding(BMSFile bmsFile, string codepageName = "")
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (string.IsNullOrWhiteSpace(bmsFile.path) || !LongPathFileSystem.FileExists(bmsFile.path))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", bmsFile.path ?? "");
        }
        if (string.IsNullOrWhiteSpace(codepageName))
        {
            codepageName = DetectEncodingOfBMSFile(bmsFile);
        }
        codepageName = NormalizeReloadEncodingName(codepageName);
        ApplyDecodedBmsMetadataWithEncoding(bmsFile, LongPathFileSystem.ReadAllBytes(bmsFile.path), codepageName);
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
        string decodedText = detectionResult.DecodedText ?? DecodeBytes(snapshot.Bytes, Encoding.GetEncoding(codepageName));
        ApplyDecodedBmsMetadata(bmsFile, decodedText, codepageName);
    }

    private static void ApplyDetectedBmsMetadata(
        BMSFile bmsFile,
        ChartFileSnapshot snapshot,
        BmsEncodingDetectionResult detectionResult)
    {
        if (bmsFile == null || snapshot == null || detectionResult == null)
        {
            return;
        }
        if (!ShouldApplyDetectedMetadataEncoding(detectionResult.EncodingName))
        {
            return;
        }
        string codepageName = NormalizeReloadEncodingName(detectionResult.EncodingName);
        string decodedText = detectionResult.DecodedText ?? DecodeBytes(snapshot.Bytes, Encoding.GetEncoding(codepageName));
        ApplyDecodedBmsMetadata(bmsFile, decodedText, codepageName);
    }

    private static void ApplyLr2Cp932ResourceDecodeWarning(
        BMSFile bmsFile,
        ChartFileSnapshot snapshot)
    {
        // LR2 interprets resource definition values as CP932 even when display
        // metadata is corrected with another detected encoding.
        if (bmsFile == null
            || snapshot?.Bytes == null)
        {
            return;
        }
        if (!HasCp932DecodeUnsupportedResourceValue(snapshot.Bytes, out ChartResourceKind kind))
        {
            return;
        }
        bmsFile.UnsupportedResourceReferences ??= [];
        if (bmsFile.UnsupportedResourceReferences.Any(reference => reference.Reason == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported))
        {
            return;
        }
        bmsFile.UnsupportedResourceReferences.Add(new UnsupportedChartResourceReference(
            kind,
            string.Empty,
            ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
    }

    private static bool HasCp932DecodeUnsupportedResourceValue(byte[] bytes, out ChartResourceKind kind)
    {
        kind = ChartResourceKind.Unknown;
        if (bytes == null || bytes.Length == 0)
        {
            return false;
        }

        int lineStart = 0;
        while (lineStart < bytes.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < bytes.Length && bytes[lineEnd] != '\r' && bytes[lineEnd] != '\n')
            {
                lineEnd++;
            }
            if (TryGetBmsResourceDirectiveValue(bytes, lineStart, lineEnd, out ChartResourceKind resourceKind, out int valueStart, out int valueLength)
                && !CanDecodeCp932(bytes, valueStart, valueLength))
            {
                kind = resourceKind;
                return true;
            }
            if (lineEnd >= bytes.Length)
            {
                break;
            }
            lineStart = lineEnd + 1;
            if (bytes[lineEnd] == '\r' && lineStart < bytes.Length && bytes[lineStart] == '\n')
            {
                lineStart++;
            }
        }
        return false;
    }

    private static bool TryGetBmsResourceDirectiveValue(
        byte[] bytes,
        int lineStart,
        int lineEnd,
        out ChartResourceKind kind,
        out int valueStart,
        out int valueLength)
    {
        kind = ChartResourceKind.Unknown;
        valueStart = -1;
        valueLength = 0;
        if (bytes == null || lineStart < 0 || lineStart >= lineEnd || lineEnd > bytes.Length)
        {
            return false;
        }

        int index = lineStart;
        if (index == 0 && lineEnd - index >= 3 && bytes[index] == 0xEF && bytes[index + 1] == 0xBB && bytes[index + 2] == 0xBF)
        {
            index += 3;
        }
        while (index < lineEnd && IsAsciiWhitespace(bytes[index]))
        {
            index++;
        }
        if (index >= lineEnd || bytes[index] != '#')
        {
            return false;
        }
        index++;
        int tokenStart = index;
        while (index < lineEnd && IsAsciiAlphaNumericByte(bytes[index]))
        {
            index++;
        }
        int tokenLength = index - tokenStart;
        if (!TryGetResourceDirectiveKind(bytes, tokenStart, tokenLength, out kind))
        {
            return false;
        }
        if (index >= lineEnd || !IsAsciiWhitespace(bytes[index]))
        {
            return false;
        }
        index++;
        while (index < lineEnd && IsAsciiWhitespace(bytes[index]))
        {
            index++;
        }

        int valueEnd = lineEnd;
        while (valueEnd > index && IsAsciiWhitespace(bytes[valueEnd - 1]))
        {
            valueEnd--;
        }
        valueStart = index;
        valueLength = Math.Max(0, valueEnd - index);
        return true;
    }

    private static bool TryGetResourceDirectiveKind(byte[] bytes, int start, int length, out ChartResourceKind kind)
    {
        kind = ChartResourceKind.Unknown;
        if (bytes == null || start < 0 || start + length > bytes.Length)
        {
            return false;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(bytes, start, "WAV") && IsBase36Byte(bytes[start + 3]) && IsBase36Byte(bytes[start + 4]))
        {
            kind = ChartResourceKind.Audio;
            return true;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(bytes, start, "BMP") && IsBase36Byte(bytes[start + 3]) && IsBase36Byte(bytes[start + 4]))
        {
            kind = ChartResourceKind.Unknown;
            return true;
        }
        switch (length)
        {
            case 6:
                if (EqualsAsciiIgnoreCase(bytes, start, "BANNER"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
            case 7:
                if (EqualsAsciiIgnoreCase(bytes, start, "BACKBMP"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
            case 9:
                if (EqualsAsciiIgnoreCase(bytes, start, "STAGEFILE"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
        }
        return false;
    }

    private static bool CanDecodeCp932(byte[] bytes, int start, int length)
    {
        if (bytes == null || start < 0 || length < 0 || start + length > bytes.Length)
        {
            return true;
        }
        try
        {
            sjisEnc.GetString(bytes, start, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool StartsWithAsciiIgnoreCase(byte[] value, int start, string prefix)
    {
        if (value == null || prefix == null || start < 0 || start + prefix.Length > value.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (ToUpperAscii((char)value[start + i]) != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool EqualsAsciiIgnoreCase(byte[] value, int start, string expected)
    {
        return start >= 0
            && expected != null
            && value != null
            && start + expected.Length <= value.Length
            && StartsWithAsciiIgnoreCase(value, start, expected);
    }

    private static bool IsAsciiAlphaNumericByte(byte value)
    {
        return (value >= '0' && value <= '9')
            || (value >= 'A' && value <= 'Z')
            || (value >= 'a' && value <= 'z');
    }

    private static bool IsBase36Byte(byte value)
    {
        return IsAsciiAlphaNumericByte(value);
    }

    private static bool IsAsciiWhitespace(byte value)
    {
        return value == ' '
            || value == '\t'
            || value == '\r'
            || value == '\n'
            || value == '\f'
            || value == '\v';
    }

    internal static bool ShouldApplyDetectedMetadataEncoding(string encodingName)
    {
        return !string.IsNullOrWhiteSpace(encodingName)
            && !IsShiftJisEncodingName(encodingName)
            && !encodingName.EndsWith("?", StringComparison.Ordinal)
            && !string.Equals(encodingName, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsShiftJisEncodingName(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return false;
        }
        string normalized = encodingName.TrimEnd('?');
        return string.Equals(normalized, "shift_jis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "shift-jis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "sjis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "cp932", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "windows-31j", StringComparison.OrdinalIgnoreCase);
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
        BmsDecodedMetadata metadata = ReadBmsMetadataFromDecodedText(decodedText);
        ApplyBmsMetadata(bmsFile, metadata);
    }

    internal static bool WouldBmsMetadataChangeWithEncoding(BMSFile bmsFile, string codepageName)
    {
        if (bmsFile == null || string.IsNullOrWhiteSpace(bmsFile.path) || !LongPathFileSystem.FileExists(bmsFile.path))
        {
            return false;
        }
        codepageName = NormalizeReloadEncodingName(codepageName);
        string decodedText = DecodeBytes(LongPathFileSystem.ReadAllBytes(bmsFile.path), Encoding.GetEncoding(codepageName));
        BmsDecodedMetadata metadata = ReadBmsMetadataFromDecodedText(decodedText);
        return !MetadataEquals(bmsFile._title, metadata.Title)
            || !MetadataEquals(bmsFile._subtitle, metadata.Subtitle)
            || !MetadataEquals(bmsFile._artist, metadata.Artist)
            || !MetadataEquals(bmsFile._subartist, metadata.SubArtist)
            || !MetadataEquals(bmsFile.genre, metadata.Genre);
    }

    private static BmsDecodedMetadata ReadBmsMetadataFromDecodedText(string decodedText)
    {
        var metadata = new BmsDecodedMetadata();
        using (var reader = new StringReader(decodedText ?? string.Empty))
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
                        if (string.IsNullOrWhiteSpace(metadata.Title))
                        {
                            metadata.Title = value;
                        }
                        break;
                    case BmsDirective.SubTitle:
                        if (string.IsNullOrWhiteSpace(metadata.Subtitle))
                        {
                            metadata.Subtitle = value;
                        }
                        break;
                    case BmsDirective.Artist:
                        if (string.IsNullOrWhiteSpace(metadata.Artist))
                        {
                            metadata.Artist = value;
                        }
                        break;
                    case BmsDirective.SubArtist:
                        if (string.IsNullOrWhiteSpace(metadata.SubArtist))
                        {
                            metadata.SubArtist = value;
                        }
                        break;
                    case BmsDirective.Genre:
                        if (string.IsNullOrWhiteSpace(metadata.Genre))
                        {
                            metadata.Genre = value;
                        }
                        break;
                }
            }
        }
        return metadata;
    }

    private static void ApplyBmsMetadata(BMSFile bmsFile, BmsDecodedMetadata metadata)
    {
        bool titleChanged = !string.Equals(bmsFile._title, metadata.Title, StringComparison.Ordinal)
            || !string.Equals(bmsFile._subtitle, metadata.Subtitle, StringComparison.Ordinal);
        bmsFile._title = metadata.Title;
        bmsFile._subtitle = metadata.Subtitle;
        bmsFile._cachedComposedTitle = null;
        bmsFile._cachedComposedTitleSource = null;
        bmsFile._cachedComposedSubtitleSource = null;
        if (titleChanged)
        {
            bmsFile.RaisePropertyChanged("Title");
        }

        bool artistChanged = !string.Equals(bmsFile._artist, metadata.Artist, StringComparison.Ordinal)
            || !string.Equals(bmsFile._subartist, metadata.SubArtist, StringComparison.Ordinal);
        bmsFile._artist = metadata.Artist;
        bmsFile._subartist = metadata.SubArtist;
        if (artistChanged)
        {
            bmsFile.RaisePropertyChanged("Artist");
        }
        bmsFile.genre = metadata.Genre;
    }

    private static bool MetadataEquals(string left, string right)
    {
        return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class BmsDecodedMetadata
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Artist { get; set; }
        public string SubArtist { get; set; }
        public string Genre { get; set; }
    }

    internal static string DetectEncodingOfBMSFile(BMSFile bmsInfo)
    {
        return DetectEncodingOfBMSFile(bmsInfo.path);
    }

    internal static string DetectEncodingOfBMSFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !LongPathFileSystem.FileExists(path))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", path ?? "");
        }
        return DetectEncodingOfBMSFileCore(LongPathFileSystem.ReadAllBytes(path)).EncodingName;
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
        using var stream = new MemoryStream(bytes ?? [], writable: false);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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

    internal static bool IsZeroNoteBMSFile(string filePath)
    {
        return !ReadFileLines(filePath, "shift_jis").Any(line => visibleObjectChRegex.IsMatch(line));
    }

    private static IEnumerable<string> ReadFileLines(string filePath, string codepageName)
    {
        Encoding encoding = Encoding.GetEncoding(codepageName);
        bool detectEncodingFromByteOrderMarks = !IsShiftJisEncodingName(codepageName);
        using var stream = LongPathFileSystem.OpenRead(filePath);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }
}
