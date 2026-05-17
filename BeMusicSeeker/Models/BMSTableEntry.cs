using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.LR2;
using Codeplex.Data;

namespace BeMusicSeeker.Models;

public partial class BMSTableEntry : LR2SongDBExtended.playlist_entry
{
    private enum PlaylistHashIdentityKind
    {
        Automatic,
        Md5Only,
        Sha256Only
    }

    protected BMSFile _bmsfile;

    private static readonly object bulkLoadParseSuppressionLock = new object();

    private static int bulkLoadParseSuppressionCount = 0;

    private string deferredUrlRaw;

    private string deferredUrlDiffRaw;

    private string deferredOrgMd5Raw;

    private static readonly Regex sha256HashRegex = new Regex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    private Uri _url;

    private Uri _urlDiff;

    private List<string> _orgMd5 = new List<string>();

    private PlaylistHashIdentityKind playlistHashIdentityKind;

    public const string DUMMY_MD5_FOR_EMPTY_FOLDER = "00000000000000000000000000000000";

    private static Regex numParseRegex = new Regex("([+-]?\\d+(?:\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

    private static Regex dateparseRegex = new Regex("((?:\\d{4}|\\d{2})[^\\d]\\d{2}[^\\d]\\d{2})(?:[^\\d].*(\\d{2}[^\\d]\\d{2}[^\\d]\\d{2,3}))?", RegexOptions.Compiled);

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
        protected set
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
            throw new FormatException("MD5 HASH ではありません。値: " + value.ToString());
        }
    }

    public override string sha256
    {
        get
        {
            return _sha256;
        }
        protected set
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
            throw new FormatException("SHA256 HASH ではありません。値: " + value.ToString());
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
        protected set
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
        protected set
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
        protected set
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
        protected set
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
            return (Org_md5 == null || Org_md5.Count == 0) ? string.Empty : DynamicJson.Serialize(Org_md5);
        }
        protected set
        {
            if (IsBulkLoadParseSuppressed)
            {
                deferredOrgMd5Raw = value;
                return;
            }
            deferredOrgMd5Raw = null;
            if (value == null || string.IsNullOrWhiteSpace(value))
            {
                Org_md5 = new List<string>();
                return;
            }
            Org_md5 = parseOrgMd5(value);
        }
    }

    public List<string> Org_md5
    {
        get
        {
            return _orgMd5 ?? (_orgMd5 = new List<string>());
        }
        set
        {
            _orgMd5 = normalizeOrgMd5Collection(value);
        }
    }

    public BMSTableEntry()
    {
        Org_md5 = new List<string>();
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
        BMSTableEntry entry = new BMSTableEntry();
        entry.playlist_id = playlistId;
        entry.md5 = md5Value;
        entry.sha256 = sha256Value;
        entry.level = levelValue;
        entry.title = titleValue;
        entry.artist = artistValue;
        entry.folder = folderValue;
        entry.lr2_bmsid = lr2BmsIdValue;
        entry.deferredUrlRaw = urlValue;
        entry.deferredUrlDiffRaw = urlDiffValue;
        entry.name_diff = nameDiffValue;
        entry.deferredOrgMd5Raw = orgMd5Value;
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
        if (PendingChartEntry.IsBmsonChartFile(bmsFile))
        {
            title = bmsFile.Title;
            artist = bmsFile.Artist;
            base.level = bmsFile.level;
            base.folder = bmsFile.folder ?? string.Empty;
            MarkAsBmsonPlaylistIdentity(bmsFile.sha256);
            return;
        }
        md5 = bmsFile.hash;
        bmsfile = bmsFile;
        base.level = bmsFile.level;
        playlistHashIdentityKind = PlaylistHashIdentityKind.Md5Only;
    }

    public BMSTableEntry(dynamic data_json, BMSTable _parent = null)
        : this()
    {
        if (_parent != null)
        {
            parent = _parent;
        }
        bool flag = (data_json.IsDefined("md5") && data_json.md5 != null) ? true : false;
        bool flag2 = (data_json.IsDefined("sha256") && data_json.sha256 != null) ? true : false;
        bool flag3 = (data_json.IsDefined("lr2_bmsid") && data_json.lr2_bmsid != null) ? true : false;
        bool flag4 = (data_json.IsDefined("title") && data_json.title != null) ? true : false;
        if (!flag && !flag2 && !flag3 && !flag4)
        {
            return;
        }
        if (flag)
        {
            try
            {
                md5 = data_json.md5.ToString();
            }
            catch
            {
            }
        }
        if (flag2)
        {
            try
            {
                sha256 = data_json.sha256.ToString();
            }
            catch
            {
            }
        }
        if (data_json.IsDefined("org_level"))
        {
            base.level = data_json.org_level;
            if (data_json.IsDefined("folder") && data_json.folder != null)
            {
                base.folder = data_json.folder.ToString();
            }
            else if (data_json.IsDefined("level") && data_json.level != null)
            {
                if (parent != null)
                {
                    base.folder = parent.ConvertCompatibleLevelNameToFolderName(data_json.level.ToString());
                }
                else
                {
                    base.folder = data_json.level.ToString();
                }
            }
        }
        else if (data_json.IsDefined("level") && data_json.level != null)
        {
            MatchCollection matchCollection = numParseRegex.Matches((string)data_json.level.ToString());
            if (matchCollection.Count == 1)
            {
                base.level = (double.TryParse(matchCollection[0].ToString(), out var result) ? new double?(result) : ((double?)null));
            }
            if (parent != null)
            {
                base.folder = parent.ConvertCompatibleLevelNameToFolderName(data_json.level.ToString());
            }
            else
            {
                base.folder = data_json.level.ToString();
            }
        }
        if (data_json.IsDefined("title") && data_json.title != null)
        {
            title = data_json.title.ToString();
        }
        if (data_json.IsDefined("artist") && data_json.artist != null)
        {
            artist = data_json.artist.ToString();
        }
        if (data_json.IsDefined("lr2_bmsid") && data_json.lr2_bmsid != null)
        {
            base.lr2_bmsid = data_json.lr2_bmsid.ToString();
        }
        if (data_json.IsDefined("comment") && data_json.comment != null)
        {
            base.comment = data_json.comment.ToString();
        }
        if (data_json.IsDefined("url") && data_json.url != null && !string.IsNullOrWhiteSpace(data_json.url.ToString()))
        {
            url = data_json.url.ToString();
        }
        if (data_json.IsDefined("url_diff") && data_json.url_diff != null && !string.IsNullOrWhiteSpace(data_json.url_diff.ToString()))
        {
            url_diff = data_json.url_diff.ToString();
        }
        if (data_json.IsDefined("name_diff") && data_json.name_diff != null && !string.IsNullOrWhiteSpace(data_json.name_diff.ToString()))
        {
            base.name_diff = data_json.name_diff.ToString();
        }
        if (data_json.IsDefined("adddate") && data_json.adddate != null && !string.IsNullOrWhiteSpace(data_json.adddate.ToString()))
        {
            try
            {
                string text = (string)data_json.adddate.ToString();
                Match match = dateparseRegex.Match(text);
                if (match.Success)
                {
                    text = match.Groups[1].ToString() + ((match.Groups.Count == 1) ? "" : (" " + match.Groups[2].ToString()));
                }
                base.adddate = DateTime.Parse(text);
            }
            catch
            {
            }
        }
        try
        {
            if (data_json.IsDefined("org_md5s") && data_json.org_md5s != null)
            {
                Org_md5 = ((object[])data_json.org_md5s).Select((object e) => e.ToString()).Cast<string>().ToList();
            }
            else if (data_json.IsDefined("org_md5") && data_json.org_md5 != null && !string.IsNullOrWhiteSpace(data_json.org_md5.ToString()))
            {
                Org_md5.Add(data_json.org_md5.ToString());
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
        if (Org_md5.Count() == 0 || Org_md5.All((string _md5) => LR2SongDB.md5HashRegex.IsMatch(_md5)))
        {
            Org_md5 = Org_md5.Select((string _md5) => _md5 = _md5.ToLowerInvariant()).ToList();
        }
        else
        {
            Org_md5 = new List<string>();
        }
        if (!string.IsNullOrWhiteSpace(base.lr2_bmsid) && !Regex.IsMatch(base.lr2_bmsid, "^[\\d]+$"))
        {
            base.lr2_bmsid = null;
        }
    }

    public BMSTableEntry Duplicate()
    {
        BMSTableEntry obj = (BMSTableEntry)MemberwiseClone();
        obj.adddate = DateTime.Now;
        obj.parent = null;
        obj.playlist_id = null;
        obj.Org_md5 = new List<string>(Org_md5 ?? new List<string>());
        obj.ClearRuntimeUrlCompletions();
        return obj;
    }

    public dynamic ToDynamicJson()
    {
        NormalizeForPlaylistPersistence();
        ensureDeferredOrgMd5Parsed();
        dynamic val = new DynamicJson();
        val.md5 = md5;
        val.sha256 = sha256;
        val.org_level = base.level;
        val.title = title;
        val.artist = artist;
        val.folder = base.folder;
        val.level = ((parent == null) ? base.folder : parent.ConvertBackFolderNameToCompatibleLevelName(base.folder));
        val.lr2_bmsid = base.lr2_bmsid;
        val.url = url;
        val.url_diff = url_diff;
        val.name_diff = base.name_diff;
        val.org_md5s = Org_md5.ToArray();
        val.org_md5 = ((Org_md5.Count == 0) ? string.Empty : Org_md5[0]);
        val.comment = base.comment ?? string.Empty;
        val.adddate = base.adddate.ToShortDateString();
        return val;
    }

    internal void MarkAsBmsPlaylistIdentity()
    {
        playlistHashIdentityKind = PlaylistHashIdentityKind.Md5Only;
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
        Org_md5 = new List<string>();
    }

    internal void NormalizeForPlaylistPersistence()
    {
        ensureDeferredOrgMd5Parsed();
        Org_md5 = Org_md5;
        switch (playlistHashIdentityKind)
        {
            case PlaylistHashIdentityKind.Md5Only:
                sha256 = null;
                break;
            case PlaylistHashIdentityKind.Sha256Only:
                MarkAsBmsonPlaylistIdentity(sha256);
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
            return new List<string>();
        }
        try
        {
            dynamic val = DynamicJson.Parse(value);
            if (val == null)
            {
                return new List<string>();
            }
            return ((object[])val).Select((object e) => e.ToString()).Cast<string>().ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> normalizeOrgMd5Collection(IEnumerable<string> values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Where((string value) => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            .Select((string value) => value.Trim())
            .ToList();
    }

    private void materializeCurrentDisplayValues()
    {
        if (_bmsfile == null)
        {
            return;
        }
        title = this.title;
        artist = this.artist;
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
        return ToDynamicJson().ToString();
    }

    public static BMSTableEntry CreateDummyBMSTableEntry()
    {
        return new BMSTableEntry
        {
            md5 = "00000000000000000000000000000000"
        };
    }
}
