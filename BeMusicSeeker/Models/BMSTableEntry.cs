using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    protected BMSFile _bmsfile;

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

    private static readonly Regex dateparseRegex = new("((?:\\d{4}|\\d{2})[^\\d]\\d{2}[^\\d]\\d{2})(?:[^\\d].*(\\d{2}[^\\d]\\d{2}[^\\d]\\d{2,3}))?", RegexOptions.Compiled);

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

    public BMSFile bmsfile
    {
        get
        {
            return _bmsfile;
        }
        set
        {
            if (value == null || md5 == value.hash)
            {
                _bmsfile = value;
                return;
            }
            throw new ArgumentException();
        }
    }

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
            if (bmsfile == null)
            {
                return _md5;
            }
            return bmsfile.hash;
        }
        set
        {
            if (bmsfile != null)
            {
                throw new InvalidOperationException("bmsfile が null ではないため md5 を変更することが出来ません。");
            }
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
            if (bmsfile == null)
            {
                return _title;
            }
            return bmsfile.Title;
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
            if (bmsfile == null)
            {
                return _artist;
            }
            return bmsfile.Artist;
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

    public List<string> Org_md5
    {
        get
        {
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

    public BMSTableEntry(BMSFile bmsFile)
        : this()
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        md5 = bmsFile.hash;
        sha256 = bmsFile.sha256;
        bmsfile = bmsFile;
        base.level = bmsFile.level;
    }

    internal BMSTableEntry(ChartFile chart)
        : this()
    {
        if (chart == null)
        {
            throw new ArgumentNullException(nameof(chart));
        }
        if (chart.Kind == ChartFileKind.Bmson)
        {
            md5 = chart.Md5;
            sha256 = chart.Sha256;
            title = chart.Title;
            artist = chart.Artist;
            base.level = chart.Level;
            base.folder = chart.Folder ?? string.Empty;
            return;
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            md5 = bmsFile.hash;
            sha256 = bmsFile.sha256;
            bmsfile = bmsFile;
            base.level = bmsFile.level;
            return;
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
            catch
            {
            }
        }
        if (hasSha256)
        {
            try
            {
                sha256 = sha256Token.ToString();
            }
            catch
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
                string compatibleLevel = compatibleLevelToken.ToString();
                base.folder = parent != null
                    ? parent.ConvertCompatibleLevelNameToFolderName(compatibleLevel)
                    : compatibleLevel;
            }
        }
        else if (TryGetNonNullProperty(dataJson, "level", out JToken levelToken))
        {
            string levelText = levelToken.ToString();
            MatchCollection matchCollection = numParseRegex.Matches(levelText);
            if (matchCollection.Count == 1)
            {
                base.level = double.TryParse(matchCollection[0].ToString(), out double result) ? result : null;
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
        if (TryGetNonNullProperty(dataJson, "adddate", out JToken addDateToken) && !string.IsNullOrWhiteSpace(addDateToken.ToString()))
        {
            try
            {
                string text = addDateToken.ToString();
                Match match = dateparseRegex.Match(text);
                if (match.Success)
                {
                    text = match.Groups[1].ToString() + (match.Groups.Count == 1 ? "" : " " + match.Groups[2].ToString());
                }
                base.adddate = DateTime.Parse(text);
            }
            catch
            {
            }
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
        catch
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

    /// <summary>
    /// プレイリスト mutation の一時補償に使う、この entry の状態を取得します。
    /// </summary>
    /// <returns>同じ entry object へ状態を戻せる不変スナップショット。</returns>
    internal MutationState CaptureMutationState()
    {
        return new MutationState(this);
    }

    /// <summary>
    /// operation 内の補償用 entry 状態です。保存境界を越えて保持しません。
    /// </summary>
    internal sealed class MutationState
    {
        private readonly BMSTableEntry snapshot;

        /// <summary>行の参照を維持して復元できるよう、操作前の値を保全します。</summary>
        internal MutationState(BMSTableEntry source)
        {
            snapshot = (BMSTableEntry)source.MemberwiseClone();
            snapshot.parent = null;
            snapshot._orgMd5 = source._orgMd5 == null ? null : [.. source._orgMd5];
        }

        /// <summary>
        /// 同一 entry object へ、補償開始時点の値を戻します。
        /// </summary>
        /// <param name="target">復元対象の entry。</param>
        /// <param name="parent">復元後に設定する親 table。</param>
        internal void Restore(BMSTableEntry target, BMSTable parent)
        {
            // Local playlist edits only change folder/parent membership.  The
            // remaining assignments cover the fields that
            // NormalizeForPlaylistPersistence can materialize before a later
            // database write fails.  Detail-cell fields (for example comment,
            // memo, or adddate) are intentionally left untouched because this
            // operation does not own those edits.
            target._bmsfile = snapshot._bmsfile;
            target._md5 = snapshot._md5;
            target.level = snapshot.level;
            target._title = snapshot._title;
            target._artist = snapshot._artist;
            target.folder = snapshot.folder;
            target.deferredOrgMd5Raw = snapshot.deferredOrgMd5Raw;
            target._orgMd5 = snapshot._orgMd5 == null ? null : [.. snapshot._orgMd5];
            target.parent = parent;
        }
    }

    internal void ApplyPlaylistEditableStateFrom(BMSTableEntry source)
    {
        ApplyPlaylistEditableStateFrom(source, null);
    }

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
                level = source.level;
                Url = source.Url;
                Url_diff = source.Url_diff;
                comment = source.comment;
                memo = source.memo;
                break;
        }
    }

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
            ["adddate"] = base.adddate.ToShortDateString()
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
        JToken token = JToken.ReadFrom(reader, PlaylistJsonLoadSettings);
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
        materializeCurrentDisplayValues();
        playlistHashIdentityKind = PlaylistHashIdentityKind.Sha256Only;
        _bmsfile = null;
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
        if (_bmsfile == null)
        {
            md5 = chart.Md5;
        }
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
                materializeCurrentDisplayValues();
                _bmsfile = null;
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
        catch
        {
        }
    }

    private void ensureDeferredOrgMd5Parsed()
    {
        if (string.IsNullOrWhiteSpace(deferredOrgMd5Raw))
        {
            return;
        }
        Org_md5 = Org_md5;
        if (Org_md5.Count == 0)
        {
            Org_md5 = parseOrgMd5(deferredOrgMd5Raw);
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
        catch
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

    private void materializeCurrentDisplayValues()
    {
        if (_bmsfile == null)
        {
            return;
        }
        title = title;
        artist = artist;
        if (!base.level.HasValue)
        {
            base.level = _bmsfile.level;
        }
        if (string.IsNullOrWhiteSpace(base.folder))
        {
            base.folder = _bmsfile.folder ?? string.Empty;
        }
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
