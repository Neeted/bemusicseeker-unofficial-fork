using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

public partial class BMSTableEntry : LR2SongDBExtended.playlist_entry
{
    private static readonly JsonLoadSettings PlaylistJsonLoadSettings = new();

    private enum PlaylistHashIdentityKind
    {
        Automatic,
        Sha256Only
    }


    private static readonly object bulkLoadParseSuppressionLock = new();

    private static int bulkLoadParseSuppressionCount = 0;

    private string deferredUrlRaw;

    private string deferredUrlDiffRaw;

    private string deferredOrgMd5Raw;

    private static readonly Regex sha256HashRegex = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    private Uri _url;

    private Uri _urlDiff;

    private List<string> _orgMd5 = [];

    private PlaylistHashIdentityKind playlistHashIdentityKind;

    public const string DUMMY_MD5_FOR_EMPTY_FOLDER = "00000000000000000000000000000000";

    private static readonly Regex numParseRegex = new("([+-]?\\d+(?:\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

    private static bool IsBulkLoadParseSuppressed
    {
        get
        {
            lock (bulkLoadParseSuppressionLock)
            {
                return bulkLoadParseSuppressionCount > 0;
            }
        }
    }

    public static IDisposable BeginBulkLoadParseSuppression()
    {
        lock (bulkLoadParseSuppressionLock)
        {
            bulkLoadParseSuppressionCount++;
        }
        return new BulkLoadParseSuppressionScope();
    }

    private sealed class BulkLoadParseSuppressionScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                lock (bulkLoadParseSuppressionLock)
                {
                    if (bulkLoadParseSuppressionCount > 0)
                    {
                        bulkLoadParseSuppressionCount--;
                    }
                }
                disposed = true;
            }
        }
    }

    public BMSTable parent { get; set; }

    /// <summary>所属表があればその現在の採番値を返し、保存行だけの場合は読込み済みの所属IDを返します。</summary>
    public override int? playlist_id
    {
        get
        {
            if (parent != null)
            {
                return parent.playlist_id;
            }
            return _playlist_id;
        }
        set
        {
            _playlist_id = value;
        }
    }

    public override string md5
    {
        get
        {
            return _md5;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _md5 = null;
                return;
            }
            if (LR2SongDB.md5HashRegex.IsMatch(value))
            {
                if (!(_md5 == value))
                {
                    _md5 = value.ToLowerInvariant();
                }
                return;
            }
            throw new FormatException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidHashValueFormat, "MD5", value.ToString()));
        }
    }

    public override string sha256
    {
        get
        {
            return _sha256;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _sha256 = null;
                return;
            }
            if (sha256HashRegex.IsMatch(value))
            {
                string text = value.ToLowerInvariant();
                if (!(_sha256 == text))
                {
                    _sha256 = text;
                }
                return;
            }
            throw new FormatException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidHashValueFormat, "SHA256", value.ToString()));
        }
    }

    public override string title
    {
        get
        {
            return _title;
        }
        set
        {
            _title = value;
        }
    }

    public override string artist
    {
        get
        {
            return _artist;
        }
        set
        {
            _artist = value;
        }
    }

    public override string url
    {
        get
        {
            ensureDeferredUrlParsed(isDiff: false);
            if (!(Url == null) && !string.IsNullOrWhiteSpace(Url.ToString()) && Url.IsAbsoluteUri)
            {
                return Url.AbsoluteUri;
            }
            return string.Empty;
        }
        set
        {
            if (IsBulkLoadParseSuppressed)
            {
                deferredUrlRaw = value;
                return;
            }
            deferredUrlRaw = null;
            _url = null;
            runtimeUrlCompletion = null;
            if (value == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            tryApplyUriValue(value, isDiff: false);
        }
    }

    /// <summary>保存対象の URL1 です。明示設定時は null による解除を含め、この欄のランタイム補完を解除します。</summary>
    public Uri Url
    {
        get
        {
            ensureDeferredUrlParsed(isDiff: false);
            return _url;
        }
        set
        {
            deferredUrlRaw = null;
            _url = value;
            runtimeUrlCompletion = null;
        }
    }

    public override string url_diff
    {
        get
        {
            ensureDeferredUrlParsed(isDiff: true);
            if (!(Url_diff == null) && !string.IsNullOrWhiteSpace(Url_diff.ToString()) && Url_diff.IsAbsoluteUri)
            {
                return Url_diff.AbsoluteUri;
            }
            return string.Empty;
        }
        set
        {
            if (IsBulkLoadParseSuppressed)
            {
                deferredUrlDiffRaw = value;
                return;
            }
            deferredUrlDiffRaw = null;
            _urlDiff = null;
            runtimeUrlDiffCompletion = null;
            if (value == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            tryApplyUriValue(value, isDiff: true);
        }
    }

    /// <summary>保存対象の URL2 です。明示設定時は null による解除を含め、この欄のランタイム補完を解除します。</summary>
    public Uri Url_diff
    {
        get
        {
            ensureDeferredUrlParsed(isDiff: true);
            return _urlDiff;
        }
        set
        {
            deferredUrlDiffRaw = null;
            _urlDiff = value;
            runtimeUrlDiffCompletion = null;
        }
    }

    /// <summary>保存生値を必要時に解決し、親MD5を既存のJSON配列文字列で返します。親なしは空文字です。</summary>
    public override string org_md5
    {
        get
        {
            ensureDeferredOrgMd5Parsed();
            return (Org_md5 == null || Org_md5.Count == 0) ? string.Empty : new JArray(Org_md5).ToString(Formatting.Indented);
        }
        set
        {
            if (IsBulkLoadParseSuppressed)
            {
                deferredOrgMd5Raw = value;
                return;
            }
            deferredOrgMd5Raw = null;
            if (value == null || string.IsNullOrWhiteSpace(value))
            {
                Org_md5 = [];
                return;
            }
            Org_md5 = parseOrgMd5(value);
        }
    }

    /// <summary>保存生値を最初の読取りで解決した親MD5列です。既存の正規化と入力順を保持します。</summary>
    public List<string> Org_md5
    {
        get
        {
            ensureDeferredOrgMd5Parsed();
            return _orgMd5 ??= [];
        }
        set
        {
            _orgMd5 = normalizeOrgMd5Collection(value);
        }
    }

    public BMSTableEntry()
    {
        Org_md5 = [];
        base.adddate = DateTime.Now;
    }

    internal static BMSTableEntry CreateHydratedPlaylistEntry(
        int playlistId,
        string md5Value,
        string sha256Value,
        double? levelValue,
        string titleValue,
        string artistValue,
        string folderValue,
        string lr2BmsIdValue,
        string urlValue,
        string urlDiffValue,
        string nameDiffValue,
        string orgMd5Value,
        DateTime? addDateValue,
        string commentValue,
        string memoValue,
        bool isRemovedValue)
    {
        var entry = new BMSTableEntry
        {
            playlist_id = playlistId,
            md5 = md5Value,
            sha256 = sha256Value,
            level = levelValue,
            title = titleValue,
            artist = artistValue,
            folder = folderValue,
            lr2_bmsid = lr2BmsIdValue,
            deferredUrlRaw = urlValue,
            deferredUrlDiffRaw = urlDiffValue,
            name_diff = nameDiffValue,
            deferredOrgMd5Raw = orgMd5Value
        };
        if (addDateValue.HasValue)
        {
            entry.adddate = addDateValue.Value;
        }
        entry.comment = commentValue;
        entry.memo = memoValue;
        entry.is_removed = isRemovedValue;
        return entry;
    }

    internal BMSTableEntry(ChartFile chart)
        : this()
    {
        if (chart == null)
        {
            throw new ArgumentNullException(nameof(chart));
        }
        md5 = chart.Md5;
        sha256 = chart.Sha256;
        title = chart.Title;
        artist = chart.Artist;
        base.level = chart.Level;
        base.folder = chart.Folder ?? string.Empty;
    }

    internal static BMSTableEntry CreateForPlaylistDrop(ChartFile chart, IEnumerable<string> packageOrgMd5s = null)
    {
        var entry = new BMSTableEntry(chart);
        entry.Org_md5 = [.. packageOrgMd5s ?? []];
        return entry;
    }

    /// <summary>外部表の行を読み込みます。採用する行の追加日は固定文法で全体解析し、不適合なら表全体の保存前に拒否します。</summary>
    public BMSTableEntry(JObject dataJson, BMSTable parentTable = null)
        : this()
    {
        if (dataJson == null)
        {
            throw new ArgumentNullException(nameof(dataJson));
        }
        parent = parentTable;
        bool hasMd5 = TryGetNonNullProperty(dataJson, "md5", out JToken md5Token);
        bool hasSha256 = TryGetNonNullProperty(dataJson, "sha256", out JToken sha256Token);
        bool hasLr2BmsId = TryGetNonNullProperty(dataJson, "lr2_bmsid", out JToken lr2BmsIdToken);
        bool hasTitle = TryGetNonNullProperty(dataJson, "title", out JToken titleToken);
        if (!hasMd5 && !hasSha256 && !hasLr2BmsId && !hasTitle)
        {
            return;
        }
        if (hasMd5)
        {
            try
            {
                md5 = md5Token.ToString();
            }
            catch (FormatException)
            {
            }
        }
        if (hasSha256)
        {
            try
            {
                sha256 = sha256Token.ToString();
            }
            catch (FormatException)
            {
            }
        }
        if (dataJson.TryGetValue("org_level", out JToken orgLevelToken))
        {
            base.level = orgLevelToken.Type switch
            {
                JTokenType.Null => null,
                JTokenType.Integer or JTokenType.Float => orgLevelToken.ToObject<double?>(),
                _ => throw new FormatException("org_level must be a JSON number or null.")
            };
            if (TryGetNonNullProperty(dataJson, "folder", out JToken folderToken))
            {
                base.folder = folderToken.ToString();
            }
            else if (TryGetNonNullProperty(dataJson, "level", out JToken compatibleLevelToken))
            {
                string compatibleLevel = compatibleLevelToken.Type is JTokenType.Integer or JTokenType.Float ? Convert.ToString(((JValue)compatibleLevelToken).Value, CultureInfo.InvariantCulture) : compatibleLevelToken.ToString();
                base.folder = parent != null
                    ? parent.ConvertCompatibleLevelNameToFolderName(compatibleLevel)
                    : compatibleLevel;
            }
        }
        else if (TryGetNonNullProperty(dataJson, "level", out JToken levelToken))
        {
            string levelText = levelToken.Type is JTokenType.Integer or JTokenType.Float ? Convert.ToString(((JValue)levelToken).Value, CultureInfo.InvariantCulture) : levelToken.ToString();
            MatchCollection matchCollection = numParseRegex.Matches(levelText);
            if (levelToken.Type is JTokenType.Integer or JTokenType.Float || matchCollection.Count == 1)
            {
                base.level = levelToken.Type is JTokenType.Integer or JTokenType.Float
                    ? levelToken.Value<double>()
                    : double.TryParse(matchCollection[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : null;
            }
            base.folder = parent != null
                ? parent.ConvertCompatibleLevelNameToFolderName(levelText)
                : levelText;
        }
        if (hasTitle)
        {
            title = titleToken.ToString();
        }
        if (TryGetNonNullProperty(dataJson, "artist", out JToken artistToken))
        {
            artist = artistToken.ToString();
        }
        if (hasLr2BmsId)
        {
            base.lr2_bmsid = lr2BmsIdToken.ToString();
        }
        if (TryGetNonNullProperty(dataJson, "comment", out JToken commentToken))
        {
            base.comment = commentToken.ToString();
        }
        if (TryGetNonNullProperty(dataJson, "url", out JToken urlToken) && !string.IsNullOrWhiteSpace(urlToken.ToString()))
        {
            url = urlToken.ToString();
        }
        if (TryGetNonNullProperty(dataJson, "url_diff", out JToken urlDiffToken) && !string.IsNullOrWhiteSpace(urlDiffToken.ToString()))
        {
            url_diff = urlDiffToken.ToString();
        }
        if (TryGetNonNullProperty(dataJson, "name_diff", out JToken nameDiffToken) && !string.IsNullOrWhiteSpace(nameDiffToken.ToString()))
        {
            base.name_diff = nameDiffToken.ToString();
        }
        try
        {
            if (TryGetNonNullProperty(dataJson, "org_md5s", out JToken orgMd5sToken))
            {
                if (orgMd5sToken is not JArray orgMd5sArray)
                {
                    throw new FormatException("org_md5s must be an array.");
                }
                Org_md5 = [.. orgMd5sArray.Select(e => e.ToString())];
            }
            else if (TryGetNonNullProperty(dataJson, "org_md5", out JToken orgMd5Token) && !string.IsNullOrWhiteSpace(orgMd5Token.ToString()))
            {
                Org_md5.Add(orgMd5Token.ToString());
            }
        }
        catch (FormatException)
        {
        }
        if (md5 != null)
        {
            md5 = md5.ToLowerInvariant();
        }
        if (sha256 != null)
        {
            sha256 = sha256.ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
        {
            playlistHashIdentityKind = PlaylistHashIdentityKind.Sha256Only;
        }
        if (Org_md5.Count() == 0 || Org_md5.All(_md5 => LR2SongDB.md5HashRegex.IsMatch(_md5)))
        {
            Org_md5 = [.. Org_md5.Select(_md5 => _md5 = _md5.ToLowerInvariant())];
        }
        else
        {
            Org_md5 = [];
        }
        if (!string.IsNullOrWhiteSpace(base.lr2_bmsid) && !Regex.IsMatch(base.lr2_bmsid, "^[\\d]+$"))
        {
            base.lr2_bmsid = null;
        }
        if (TryGetNonNullProperty(dataJson, "adddate", out JToken addDateToken)
            && !string.IsNullOrWhiteSpace(addDateToken.ToString())
            && PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(this) != null)
        {
            base.adddate = PlaylistJsonDate.Parse(addDateToken.ToString(), "adddate");
        }
    }

    public BMSTableEntry Duplicate()
    {
        var obj = (BMSTableEntry)MemberwiseClone();
        obj.adddate = DateTime.Now;
        obj.parent = null;
        obj.playlist_id = null;
        obj.Org_md5 = [.. Org_md5 ?? []];
        obj.ClearRuntimeUrlCompletions();
        return obj;
    }

    internal BMSTableEntry CreatePlaylistReloadSnapshot()
    {
        var snapshot = (BMSTableEntry)MemberwiseClone();
        snapshot.parent = null;
        snapshot.Org_md5 = [.. Org_md5 ?? []];
        return snapshot;
    }

    /// <summary>現在の entry の表示・解決値を不変値へ捕捉します。所属表の reader または writer 内で呼びます。</summary>
    internal PlaylistEntryValue CaptureValue() => new(md5, sha256, level, title, artist,
        folder, lr2_bmsid, Url, Url_diff, EffectiveUrl, EffectiveUrlDiff, name_diff,
        comment, memo, adddate, is_removed, org_md5);

    /// <summary>URL 編集の空白だけの入力を未設定へ、それ以外を絶対 URI へ変換し、画面受付と保存候補で同じ検査を使います。</summary>
    /// <param name="text">利用者の URL 入力。</param>
    /// <param name="uri">受理した絶対 URI。空白だけの入力では null。</param>
    /// <returns>未設定または有効な絶対 URI として受理できる場合は true。</returns>
    internal static bool TryParseEditedUrl(string text, out Uri uri)
    {
        uri = null;
        return string.IsNullOrWhiteSpace(text) || Uri.TryCreate(text, UriKind.Absolute, out uri);
    }

    /// <summary>ENTRY編集の数値全体を地域非依存で検査します。外部表の装飾level抽出とは文法を分けます。</summary>
    /// <param name="text">確定するセル全文。空白だけなら未設定にします。</param>
    /// <param name="value">受理した有限値または未設定のnull。</param>
    /// <returns>Invariant Float全体または空白として受理できる場合はtrue。</returns>
    internal static bool TryParseEditedLevel(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) { return true; }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) { return false; }
        value = number;
        return true;
    }

    /// <summary>空白のLevelはnull、非空のLevelは固定dotの有限数値全体として保存候補へ適用します。URLの空白は解除します。</summary>
    /// <exception cref="FormatException">LevelがInvariant Float全体または有限値に適合しません。</exception>
    /// <exception cref="UriFormatException">非空URLが絶対URIとして無効です。</exception>
    internal void ApplyEditDraft(string propertyName, string text)
    {
        switch (propertyName)
        {
            case "Level":
            case nameof(level):
                if (!TryParseEditedLevel(text, out double? number))
                {
                    throw new FormatException(BeMusicSeeker.Properties.Resources.Error_PlaylistEntryLevelFormat);
                }
                level = number;
                break;
            case nameof(Url):
            case nameof(Url_diff):
                if (!TryParseEditedUrl(text, out Uri uri))
                {
                    throw new UriFormatException("Playlist edit requires an absolute URI.");
                }
                if (propertyName == nameof(Url)) { Url = uri; } else { Url_diff = uri; }
                break;
            case nameof(comment): comment = text; break;
            case nameof(memo): memo = text; break;
            default: throw new ArgumentOutOfRangeException(nameof(propertyName));
        }
    }

    /// <summary>同じ writer 内で DB 確定した候補の、保存操作が所有する値だけを現行 entry へ適用します。</summary>
    internal void ApplyCommittedEdit(BMSTableEntry candidate)
    {
        level = candidate.level;
        comment = candidate.comment;
        memo = candidate.memo;
        _md5 = candidate._md5;
        _sha256 = candidate._sha256;
        playlistHashIdentityKind = candidate.playlistHashIdentityKind;
        deferredOrgMd5Raw = candidate.deferredOrgMd5Raw;
        _orgMd5 = candidate._orgMd5;
        deferredUrlRaw = candidate.deferredUrlRaw;
        deferredUrlDiffRaw = candidate.deferredUrlDiffRaw;
        _url = candidate._url;
        _urlDiff = candidate._urlDiff;
        runtimeUrlCompletion = candidate.runtimeUrlCompletion;
        runtimeUrlDiffCompletion = candidate.runtimeUrlDiffCompletion;
    }

    /// <summary>詳細編集の保存前失敗で、同じ項目の編集列だけを編集前値へ戻します。</summary>
    /// <param name="source">編集前に捕捉した元項目の値。</param>
    /// <param name="propertyName">詳細編集が受理した列名。</param>
    /// <exception cref="ArgumentOutOfRangeException">詳細編集が扱う列以外を渡した場合。</exception>
    internal void ApplyPlaylistEditableStateFrom(BMSTableEntry source, string propertyName)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        switch (propertyName)
        {
            case nameof(level):
            case "Level":
                level = source.level;
                break;
            case nameof(Url):
                Url = source.Url;
                break;
            case nameof(Url_diff):
                Url_diff = source.Url_diff;
                break;
            case nameof(comment):
                comment = source.comment;
                break;
            case nameof(memo):
                memo = source.memo;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName));
        }
    }

    /// <summary>保存対象の項目をJSONへ変換し、追加日は地域・offsetを付けず7桁小数秒で壁時計のtickを保持します。</summary>
    public JObject ToJsonObject()
    {
        NormalizeForPlaylistPersistence();
        ensureDeferredOrgMd5Parsed();
        var val = new JObject
        {
            ["md5"] = md5,
            ["sha256"] = sha256,
            ["org_level"] = base.level,
            ["title"] = title,
            ["artist"] = artist,
            ["folder"] = base.folder,
            ["level"] = parent == null ? base.folder : parent.ConvertBackFolderNameToCompatibleLevelName(base.folder),
            ["lr2_bmsid"] = base.lr2_bmsid,
            ["url"] = url,
            ["url_diff"] = url_diff,
            ["name_diff"] = base.name_diff,
            ["org_md5s"] = new JArray(Org_md5),
            ["org_md5"] = Org_md5.Count == 0 ? string.Empty : Org_md5[0],
            ["comment"] = base.comment ?? string.Empty,
            ["adddate"] = PlaylistJsonDate.Format(base.adddate)
        };
        return val;
    }

    private static bool TryGetNonNullProperty(JObject source, string propertyName, out JToken value)
    {
        return source.TryGetValue(propertyName, out value) && value.Type != JTokenType.Null;
    }

    private static JToken ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        var token = JToken.ReadFrom(reader, PlaylistJsonLoadSettings);
        if (reader.Read())
        {
            throw new JsonReaderException("JSON document contains trailing content.");
        }
        return token;
    }

    internal void MarkAsBmsPlaylistIdentity()
    {
        playlistHashIdentityKind = PlaylistHashIdentityKind.Automatic;
    }

    internal void MarkAsBmsonPlaylistIdentity(string preferredSha256 = null)
    {
        playlistHashIdentityKind = PlaylistHashIdentityKind.Sha256Only;

        md5 = null;
        if (!string.IsNullOrWhiteSpace(preferredSha256))
        {
            sha256 = preferredSha256;
        }
    }

    internal void ApplyPlaylistHashesFromChart(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }
        md5 = chart.Md5;
        if (!string.IsNullOrWhiteSpace(chart.Sha256))
        {
            sha256 = chart.Sha256;
        }
        playlistHashIdentityKind = PlaylistHashIdentityKind.Automatic;
    }

    internal void NormalizeForPlaylistPersistence()
    {
        ensureDeferredOrgMd5Parsed();
        Org_md5 = Org_md5;
        switch (playlistHashIdentityKind)
        {
            case PlaylistHashIdentityKind.Sha256Only:

                md5 = null;
                break;
        }
    }

    private void ensureDeferredUrlParsed(bool isDiff)
    {
        string text = isDiff ? deferredUrlDiffRaw : deferredUrlRaw;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (isDiff)
        {
            deferredUrlDiffRaw = null;
        }
        else
        {
            deferredUrlRaw = null;
        }
        tryApplyUriValue(text, isDiff);
    }

    private void tryApplyUriValue(string value, bool isDiff)
    {
        try
        {
            if (new Uri(value, UriKind.RelativeOrAbsolute).IsAbsoluteUri)
            {
                if (isDiff)
                {
                    _urlDiff = new Uri(value, UriKind.Absolute);
                }
                else
                {
                    _url = new Uri(value, UriKind.Absolute);
                }
            }
            else if (parent != null)
            {
                Uri absoluteDataUrl = parent.GetAbsoluteDataUrl();
                if (absoluteDataUrl != null)
                {
                    if (isDiff)
                    {
                        _urlDiff = new Uri(absoluteDataUrl, value);
                    }
                    else
                    {
                        _url = new Uri(absoluteDataUrl, value);
                    }
                }
            }
        }
        catch (UriFormatException)
        {
        }
    }

    /// <summary>既存の親列を優先して保存生値を遅延解決します。公開getterへ再入せず同じ正規化を適用します。</summary>
    private void ensureDeferredOrgMd5Parsed()
    {
        if (string.IsNullOrWhiteSpace(deferredOrgMd5Raw))
        {
            return;
        }
        _orgMd5 = normalizeOrgMd5Collection(_orgMd5);
        if (_orgMd5.Count == 0)
        {
            _orgMd5 = normalizeOrgMd5Collection(parseOrgMd5(deferredOrgMd5Raw));
        }
        deferredOrgMd5Raw = null;
    }

    private static List<string> parseOrgMd5(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        try
        {
            JToken val = ParseJson(value);
            if (val is not JArray array)
            {
                return [];
            }
            return [.. array.Select(e => e.ToString())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> normalizeOrgMd5Collection(IEnumerable<string> values)
    {
        return [.. (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Trim())];
    }

    public string ToJson()
    {
        return ToJsonObject().ToString(Formatting.Indented);
    }

    public static BMSTableEntry CreateDummyBMSTableEntry()
    {
        return new BMSTableEntry
        {
            md5 = "00000000000000000000000000000000"
        };
    }
}
