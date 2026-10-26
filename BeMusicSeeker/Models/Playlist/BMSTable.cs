using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public enum PlaylistEntriesLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed
}

public partial class BMSTable : LR2SongDBExtended.playlist
{
    private const string Lr2CompatibleFolderFallbackPrefix = "LEVEL ";

    private static readonly JsonLoadSettings PlaylistJsonLoadSettings = new();

    // 順序は全置換するため木構造を持たず、不変配列を構築時に一度だけinterfaceとして保持します。
    private IReadOnlyList<string> _Folder_order = ImmutableArray<string>.Empty;

    /// <summary>確定した順序と表示構成を一組で公開します。取得側は再構築せず同じ不変入力を参照します。</summary>
    private sealed record FolderState(IReadOnlyList<string> Order, IReadOnlyList<string> Names, IReadOnlyList<PlaylistFolderNode> Nodes);

    private FolderState folderState = new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, BuildFolderNodes([]));

    private List<LR2SongDBExtended.playlist_course> _Courses = [];

    private ImmutableList<BMSTableEntry> _entries;

    private IReadOnlyList<BMSTableEntry> _entriesView;

    private PlaylistEntriesLoadState _PlaylistEntriesLoadState = PlaylistEntriesLoadState.Loaded;

    private int _PlaylistEntriesRevision;

    private string _EntriesLoadErrorMessage = string.Empty;

    private bool _resolveDefaultCompatPrefixFolderOrderAfterDataLoad;

    private bool _rewriteLoadedCompatPrefixFolderOrderAfterDataLoad;

    private string _loadedFolderOrderCompatPrefix;

    public string LoadedRawHeaderSha256 { get; private set; }

    /// <summary>DB互換の明示順JSONを読み書きします。保存候補の符号化は未公開の候補順を使い、公開入力の取得とは分けます。</summary>
    public override string folder_order
    {
        get
        {
            // DB保存候補は未公開のheaderも使うため、この保存属性だけは候補の明示順を読みます。
            return new JArray(_Folder_order).ToString(Formatting.Indented);
        }
        set
        {
            List<string> parsedOrder;
            try
            {
                JArray val = ParseJson(value) as JArray ?? throw new FormatException("folder_order must be an array.");
                if (val.Any(token => token.Type != JTokenType.String))
                {
                    throw new FormatException("folder_order must contain only strings.");
                }
                parsedOrder = [.. val.Select(token => token.Value<string>())];
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
            {
                return;
            }
            Folder_order = parsedOrder;
        }
    }

    /// <summary>確定済みの明示フォルダ順を取得し、設定時は入力を所有して表示構成と一緒に公開します。</summary>
    public IReadOnlyList<string> Folder_order
    {
        get
        {
            return Volatile.Read(ref folderState).Order;
        }
        set
        {
            if ((_Folder_order == null) != (value == null)
                || !(_Folder_order ?? []).SequenceEqual(value ?? [], StringComparer.Ordinal))
            {
                _Folder_order = value == null ? null : value.ToImmutableArray();
                RebuildFolderState();
                RaisePropertyChanged("Folder_order");
            }
        }
    }

    public override string page_url
    {
        get
        {
            if (!(Page_url == null) && Page_url.IsAbsoluteUri)
            {
                return GetPersistedUriText(Page_url);
            }
            return string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Page_url = null;
                return;
            }
            if (TryParseStoredUri(value, UriKind.Absolute, out Uri uri, out Exception exception))
            {
                Page_url = uri;
                return;
            }
            Page_url = null;
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "playlist_invalid_page_url_from_db value=" + value);
        }
    }

    public Uri Page_url { get; set; }

    public override string header_url
    {
        get
        {
            if (!(Header_url == null))
            {
                return GetPersistedUriText(Header_url);
            }
            return string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Header_url = null;
                return;
            }
            if (TryParseStoredUri(value, UriKind.RelativeOrAbsolute, out Uri uri, out Exception exception))
            {
                Header_url = uri;
                return;
            }
            Header_url = null;
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "playlist_invalid_header_url_from_db value=" + value);
        }
    }

    public Uri Header_url { get; set; }

    public override string data_url
    {
        get
        {
            if (!(Data_url == null))
            {
                return GetPersistedUriText(Data_url);
            }
            return string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Data_url = null;
                return;
            }
            if (TryParseStoredUri(value, UriKind.RelativeOrAbsolute, out Uri uri, out Exception exception))
            {
                Data_url = uri;
                return;
            }
            Data_url = null;
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "playlist_invalid_data_url_from_db value=" + value);
        }
    }

    public Uri Data_url { get; set; }

    public IReadOnlyList<LR2SongDBExtended.playlist_course> Courses => _Courses;

    internal void SetPersistedCourses(IEnumerable<LR2SongDBExtended.playlist_course> courses)
    {
        _Courses = [.. (courses ?? [])
            .Where(course => course != null && !string.IsNullOrWhiteSpace(course.course_json))
            .OrderBy(course => course.course_order)
            .Select((course, index) => new LR2SongDBExtended.playlist_course
            {
                course_id = course.course_id,
                playlist_id = course.playlist_id,
                course_order = index,
                course_json = NormalizeJsonOrNull(course.course_json)
            })
            .Where(course => !string.IsNullOrWhiteSpace(course.course_json))];
    }

    private static bool TryParseStoredUri(string value, UriKind uriKind, out Uri uri, out Exception exception)
    {
        uri = null;
        exception = null;
        try
        {
            uri = new Uri(value, uriKind);
            return true;
        }
        catch (UriFormatException ex)
        {
            exception = ex;
            return false;
        }
    }

    private static string GetPersistedUriText(Uri uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }
        string original = uri.OriginalString;
        return string.IsNullOrWhiteSpace(original) ? uri.ToString() : original;
    }

    private static Uri ParsePlaylistUriOrThrow(string rawValue, string context)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }
        try
        {
            return new Uri(rawValue, UriKind.RelativeOrAbsolute);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException("Failed to resolve playlist " + context + ". rawValue=" + rawValue, ex);
        }
    }

    /// <summary>
    /// 確定済み行の構成を変更できない一覧です。行自体の参照と編集可能性は保持します。
    /// 完成集合の取込み時に入力集合を一度だけ複写し、所属、正規化、フォルダ状態と読込版を揃えます。
    /// 少数の追加・削除・改名は専用操作を使い、全件の再取込みを行いません。
    /// </summary>
    public IReadOnlyList<BMSTableEntry> entries
    {
        get
        {
            return _entriesView;
        }
        internal set
        {
            List<BMSTableEntry> ownedEntries = value == null ? [] : [.. value];
            foreach (BMSTableEntry item in ownedEntries)
            {
                item.parent = this;
            }
            ReplaceEntriesCore(normalizeEntries(ownedEntries));
            RebuildFolderState();
            MarkEntriesLoadedCore();
        }
    }

    private void ReplaceEntriesCore(List<BMSTableEntry> ownedEntries)
    {
        _entries = ownedEntries.ToImmutableList();
        _entriesView = _entries;
        RebuildEntryMembership();
    }

    public PlaylistEntriesLoadState PlaylistEntriesLoadState
    {
        get
        {
            return _PlaylistEntriesLoadState;
        }
        private set
        {
            if (_PlaylistEntriesLoadState != value)
            {
                _PlaylistEntriesLoadState = value;
                RaisePropertyChanged("PlaylistEntriesLoadState");
                RaisePropertyChanged("ArePlaylistEntriesLoaded");
            }
        }
    }

    public bool ArePlaylistEntriesLoaded
    {
        get
        {
            return PlaylistEntriesLoadState == PlaylistEntriesLoadState.Loaded;
        }
    }

    public int PlaylistEntriesRevision
    {
        get
        {
            return _PlaylistEntriesRevision;
        }
        private set
        {
            if (_PlaylistEntriesRevision != value)
            {
                _PlaylistEntriesRevision = value;
                RaisePropertyChanged("PlaylistEntriesRevision");
            }
        }
    }

    public string EntriesLoadErrorMessage
    {
        get
        {
            return _EntriesLoadErrorMessage;
        }
        private set
        {
            if (_EntriesLoadErrorMessage != value)
            {
                _EntriesLoadErrorMessage = value;
                RaisePropertyChanged("EntriesLoadErrorMessage");
            }
        }
    }

    internal void MarkEntriesNotLoaded()
    {
        ReplaceEntriesCore([]);
        EntriesLoadErrorMessage = string.Empty;
        PlaylistEntriesLoadState = PlaylistEntriesLoadState.NotLoaded;
        RebuildFolderState();
    }

    internal void MarkEntriesLoading()
    {
        EntriesLoadErrorMessage = string.Empty;
        PlaylistEntriesLoadState = PlaylistEntriesLoadState.Loading;
    }

    internal void MarkEntriesLoadFailed(string message)
    {
        EntriesLoadErrorMessage = message ?? string.Empty;
        PlaylistEntriesLoadState = PlaylistEntriesLoadState.Failed;
    }

    private void MarkEntriesLoadedCore()
    {
        EntriesLoadErrorMessage = string.Empty;
        PlaylistEntriesLoadState = PlaylistEntriesLoadState.Loaded;
        PlaylistEntriesRevision++;
    }

    private void TouchPlaylistEntriesRevision()
    {
        PlaylistEntriesRevision++;
    }

    internal static DateTime GetNextLastUpdate(DateTime currentLastUpdate)
    {
        DateTime now = DateTime.Now;
        return now > currentLastUpdate ? now : currentLastUpdate.AddTicks(1);
    }

    private void TouchLastUpdate()
    {
        base.last_update = GetNextLastUpdate(base.last_update);
    }

    public string Output_dir
    {
        get
        {
            return ResolveOutputDirectoryName(base.name, base.output_dir);
        }
        set
        {
            value = NormalizeOutputDirectoryName(value);
            string defaultOutputDirectoryName = CreateDefaultOutputDirectoryName(base.name);
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(defaultOutputDirectoryName, value, StringComparison.Ordinal))
            {
                base.output_dir = value;
            }
            else if (string.IsNullOrWhiteSpace(value)
                || string.Equals(defaultOutputDirectoryName, value, StringComparison.Ordinal))
            {
                base.output_dir = null;
            }
        }
    }

    internal static string CreateDefaultOutputDirectoryName(string name)
    {
        return NormalizeOutputDirectorySegment(name);
    }

    internal static string NormalizeOutputDirectoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] segments = value.Trim()
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var normalizedSegments = new List<string>(segments.Length);
        foreach (string segment in segments)
        {
            string trimmedSegment = segment.Trim();
            if (trimmedSegment == ".")
            {
                continue;
            }
            if (trimmedSegment == "..")
            {
                if (normalizedSegments.Count > 0)
                {
                    normalizedSegments.RemoveAt(normalizedSegments.Count - 1);
                }
                continue;
            }

            string normalizedSegment = NormalizeOutputDirectorySegment(trimmedSegment);
            if (!string.IsNullOrWhiteSpace(normalizedSegment))
            {
                normalizedSegments.Add(normalizedSegment);
            }
        }

        return normalizedSegments.Count == 0
            ? null
            : Path.Combine([.. normalizedSegments]);
    }

    private static string NormalizeOutputDirectorySegment(string value)
    {
        return (value ?? string.Empty).Trim().ToSjisSchemeString().RemoveInvalidFileNameChars();
    }

    internal static string ResolveOutputDirectoryName(string name, string outputDir)
    {
        string defaultOutputDirectoryName = CreateDefaultOutputDirectoryName(name);
        string explicitOutputDirectoryName = NormalizeOutputDirectoryName(outputDir);
        if (!string.IsNullOrWhiteSpace(explicitOutputDirectoryName)
            && !string.Equals(explicitOutputDirectoryName, defaultOutputDirectoryName, StringComparison.Ordinal))
        {
            return explicitOutputDirectoryName;
        }
        return defaultOutputDirectoryName;
    }

    /// <summary>
    /// フォルダ名の並びを取得します。
    /// 互換処理や JSON 出力が使う確定済みの不変入力であり、取得時に構築・複製しません。
    /// </summary>
    public IReadOnlyList<string> folder_list
    {
        get
        {
            return Volatile.Read(ref folderState).Names;
        }
        set
        {
            RebuildFolderState();
        }
    }

    /// <summary>
    /// プレイリストツリー表示用のフォルダノード一覧を取得します。
    /// 特殊ノードと通常フォルダを含む確定済みの不変入力です。画面に渡した同じ構成を選択の出自として保持できます。
    /// </summary>
    public IReadOnlyList<PlaylistFolderNode> FolderNodes
    {
        get
        {
            return Volatile.Read(ref folderState).Nodes;
        }
    }

    public ReaderWriterLockSlimWrapper ReaderWriterLock { get; private set; }

    public BMSTable()
    {
        base.name = string.Empty;
        ReplaceEntriesCore([]);
        base.is_external_sync = false;
        base.is_root_folder = false;
        base.is_bmt_output = true;
        Folder_order = [];
        base.folder_sort_key = CustomFolderSortType.NONE;
        base.folder_sort_ascending = true;
        base.ignore_folder_output = CustomFolderType.LevelFolder
            | CustomFolderType.AlphabetFolder
            | CustomFolderType.CategoryAllFolder
            | CustomFolderType.OtherFolder;
        base.compat_prefix = string.Empty;
        base.entry_type = EntryUnitType.File;
        ReaderWriterLock = new ReaderWriterLockSlimWrapper();
    }

    public BMSTable(string _header_json, Uri _page_url_absolute = null, Uri __header_url = null, string _data_json = null)
        : this()
    {
        LoadHeaderJSON(_header_json, _page_url_absolute, __header_url, _data_json);
    }

    public Uri GetAbsoluteHeaderUrl()
    {
        if (Header_url != null)
        {
            if (Header_url.IsAbsoluteUri)
            {
                return Header_url;
            }
            if (Page_url != null)
            {
                return new Uri(Page_url, Header_url);
            }
        }
        return null;
    }

    public Uri GetAbsoluteDataUrl()
    {
        if (Data_url != null)
        {
            if (Data_url.IsAbsoluteUri)
            {
                return Data_url;
            }
            if (GetAbsoluteHeaderUrl() != null)
            {
                return new Uri(GetAbsoluteHeaderUrl(), Data_url);
            }
            if (Page_url != null)
            {
                return new Uri(Page_url, Data_url);
            }
        }
        return null;
    }

    /// <summary>正本を変更せず、ヘッダーJSONを生成します。日時はInvariantの固定日/7桁小数秒で出力し、Data_urlが空の場合だけ出力用の相対URLを使います。</summary>
    /// <param name="outputDataUrl">ファイル出力用のURL。正本の値があれば正本を優先し、省略時は従来の値をそのまま出力します。</param>
    public string HeaderToJson(string outputDataUrl = null)
    {
        return CreateHeaderJson(outputDataUrl).ToString(Formatting.Indented);
    }

    private JObject CreateHeaderJson(string outputDataUrl = null)
    {
        var val = new JObject();
        val["name"] = base.name;
        val["symbol"] = base.symbol;
        val["level_order"] = new JArray(folder_list.Select(f => ConvertBackFolderNameToCompatibleLevelName(f)));
        val["folder_order"] = new JArray(Folder_order);
        val["folder_sort_key"] = base.folder_sort_key.ToColumnName();
        val["folder_sort_ascending"] = base.folder_sort_ascending;
        val["entry_type"] = base.entry_type.ToStringName();
        val["data_url"] = string.IsNullOrWhiteSpace(data_url) ? outputDataUrl ?? data_url : data_url;
        if (!string.IsNullOrWhiteSpace(base.tag))
        {
            val["tag"] = base.tag;
        }
        if (_Courses.Count > 0)
        {
            val["course"] = new JArray(_Courses
                .OrderBy(course => course.course_order)
                .Select(course => ParseJson(course.course_json)));
        }
        val["compat_prefix"] = base.compat_prefix;
        val["last_update"] = PlaylistJsonDate.Format(base.last_update);
        val["editor_name"] = "BeMusicSeeker";
        val["editor_version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty;
        val["output_date"] = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return val;
    }

    public string DataToJson()
    {
        var val = new JArray();
        List<List<BMSTableEntry>> list = [.. folder_list.Select(delegate (string f)
        {
            IEnumerable<BMSTableEntry> source = entries.Where(e => !e.is_removed && e.folder == f);
            return (base.folder_sort_key switch
            {
                CustomFolderSortType.LEVEL => (!base.folder_sort_ascending) ? source.OrderByDescending(e => e.level) : source.OrderBy(e => e.level),
                CustomFolderSortType.ARTIST => (!base.folder_sort_ascending) ? source.OrderByDescending(e => e.artist) : source.OrderBy(e => e.artist),
                CustomFolderSortType.ADDDATE => (!base.folder_sort_ascending) ? source.OrderByDescending(e => e.adddate.ToLocalTime()) : source.OrderBy(e => e.adddate.ToLocalTime()),
                _ => (!base.folder_sort_ascending) ? source.OrderByDescending(e => e.title) : source.OrderBy(e => e.title),
            }).ToList();
        })];
        foreach (List<BMSTableEntry> item in list)
        {
            foreach (JObject item2 in item.Select(e => e.ToJsonObject()))
            {
                val.Add(item2);
            }
        }
        return val.ToString(Formatting.Indented);
    }

    /// <summary>表ヘッダーを読み込み、入力形式の失敗だけをヘッダー解析失敗へ変換します。採用するlast_updateは固定日時文法を要求し、任意の順序の不正値は従来どおり省略します。</summary>
    public void LoadHeaderJSON(string _header_json, Uri _page_url_absolute = null, Uri __header_url = null, string _data_json = null, bool preserveLoadedCompatPrefix = false)
    {
        if (_header_json == null)
        {
            throw new ArgumentNullException("_header_json");
        }
        _resolveDefaultCompatPrefixFolderOrderAfterDataLoad = false;
        _rewriteLoadedCompatPrefixFolderOrderAfterDataLoad = false;
        _loadedFolderOrderCompatPrefix = null;
        JObject val;
        try
        {
            val = ParseJson(_header_json) as JObject ?? throw new FormatException("playlist header must be an object.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new PlaylistHeaderParseException(BeMusicSeeker.Properties.Resources.Error_PlaylistHeaderParseFailed, ex);
        }
        LoadedRawHeaderSha256 = ComputeSha256Hex(_header_json);
        base.header_sha256 = ComputeHeaderSha256Hex(_header_json);
        LoadCourseJsonFromHeader(_header_json);
        if (TryGetNonNullProperty(val, "name", out JToken nameToken))
        {
            string text = (base.org_name = nameToken.ToString());
            base.name = text;
        }
        if (TryGetNonNullProperty(val, "symbol", out JToken symbolToken))
        {
            string text = (base.org_symbol = symbolToken.ToString());
            base.symbol = text;
        }
        if (TryGetNonNullProperty(val, "tag", out JToken tagToken))
        {
            base.tag = tagToken.ToString();
        }
        if (TryGetNonNullProperty(val, "folder_sort_key", out JToken folderSortKeyToken))
        {
            base.folder_sort_key = CustomFolderSortTypeExt.FromColumnName(folderSortKeyToken.ToString());
        }
        if (val.TryGetValue("folder_sort_ascending", out JToken folderSortAscendingToken))
        {
            base.folder_sort_ascending = folderSortAscendingToken.Type == JTokenType.Boolean
                ? folderSortAscendingToken.Value<bool>()
                : true;
        }
        if (val.TryGetValue("entry_type", out JToken entryTypeToken))
        {
            if (entryTypeToken.Type == JTokenType.Null)
            {
                throw new PlaylistHeaderParseException(BeMusicSeeker.Properties.Resources.Error_PlaylistHeaderParseFailed,
                    new FormatException("entry_type must not be null."));
            }
            base.entry_type = EntryUnitTypeExt.FromStringName(entryTypeToken.ToString());
        }
        bool hasExplicitCompatPrefix = TryGetNonNullProperty(val, "compat_prefix", out JToken compatPrefixToken);
        string explicitCompatPrefix = hasExplicitCompatPrefix ? compatPrefixToken.ToString() : null;
        if (hasExplicitCompatPrefix && !preserveLoadedCompatPrefix)
        {
            base.compat_prefix = explicitCompatPrefix;
        }
        List<string> headerFolderOrder = null;
        if (TryGetNonNullProperty(val, "last_update", out JToken lastUpdateToken) && !string.IsNullOrWhiteSpace(lastUpdateToken.ToString()))
        {
            try
            {
                base.last_update = PlaylistJsonDate.Parse(lastUpdateToken.ToString(), "last_update");
            }
            catch (PlaylistJsonDateException ex)
            {
                throw new PlaylistHeaderParseException(ex.Message, ex);
            }
        }
        if (TryGetNonNullProperty(val, "folder_order", out JToken folderOrderToken))
        {
            try
            {
                headerFolderOrder = [.. RequireStringArray(folderOrderToken)];
            }
            catch (FormatException)
            {
            }
            if (headerFolderOrder != null)
            {
                Folder_order = headerFolderOrder;
                if (preserveLoadedCompatPrefix
                    && hasExplicitCompatPrefix
                    && !string.Equals(explicitCompatPrefix ?? string.Empty, base.compat_prefix ?? string.Empty, StringComparison.Ordinal))
                {
                    _rewriteLoadedCompatPrefixFolderOrderAfterDataLoad = true;
                    _loadedFolderOrderCompatPrefix = explicitCompatPrefix ?? string.Empty;
                }
            }
        }
        if (_page_url_absolute != null && _page_url_absolute.IsAbsoluteUri)
        {
            Page_url = _page_url_absolute;
        }
        if (__header_url != null)
        {
            Header_url = __header_url;
        }
        if (TryGetNonNullProperty(val, "data_url", out JToken dataUrlToken))
        {
            Data_url = ParsePlaylistUriOrThrow(dataUrlToken.ToString(), "data_url");
        }
        if (!hasExplicitCompatPrefix || !val.ContainsKey("folder_sort_key") || !val.ContainsKey("folder_sort_ascending"))
        {
            base.ignore_folder_output |= CustomFolderType.LevelFolder;
            if (!hasExplicitCompatPrefix && !preserveLoadedCompatPrefix)
            {
                base.compat_prefix = ResolveDefaultCompatibleFolderPrefix(base.tag, base.symbol);
            }
            if (val.TryGetValue("level_order", out JToken levelOrderToken))
            {
                List<string> levelOrder = null;
                try
                {
                    levelOrder = [.. RequireStringArray(levelOrderToken).Select(ConvertCompatibleLevelNameToFolderName)];
                }
                catch (FormatException)
                {
                }
                if (levelOrder != null)
                {
                    Folder_order = levelOrder;
                }
            }
            else if (headerFolderOrder != null && !hasExplicitCompatPrefix && !preserveLoadedCompatPrefix)
            {
                _resolveDefaultCompatPrefixFolderOrderAfterDataLoad = true;
            }
        }
        if (_data_json != null)
        {
            LoadDataJSON(_data_json);
        }
        EnableExternalSync();
    }

    private static string ResolveDefaultCompatibleFolderPrefix(string tag, string symbol)
    {
        string candidate = !string.IsNullOrWhiteSpace(tag)
            ? tag
            : !string.IsNullOrWhiteSpace(symbol)
                ? symbol
                : null;

        // LR2 custom folder paths are CP932. Keep the source value intact only when LR2 can encode it;
        // silently removing or replacing characters would create a different, misleading folder prefix.
        return candidate != null && Lr2CompatibilityEvaluator.TryGetCp932ByteCount(candidate, out _)
            ? candidate
            : Lr2CompatibleFolderFallbackPrefix;
    }

    /// <summary>表データを全行解析後に採用します。採用行の非空追加日が固定文法に不適合なら一表の保存前に拒否し、内部故障・資源不足は呼出元へ伝播します。</summary>
    public void LoadDataJSON(string _data_json)
    {
        if (_data_json == null)
        {
            throw new ArgumentNullException("_data_json");
        }
        List<BMSTableEntry> parsedEntries;
        try
        {
            JArray val = ParseJson(_data_json) as JArray ?? throw new FormatException("playlist data must be an array.");
            base.data_sha256 = ComputeSha256Hex(_data_json);
            parsedEntries = [.. val
                .Select(token => token as JObject ?? throw new FormatException("playlist entry must be an object"))
                .Select(json => new BMSTableEntry(json, this))
                .Where(entry => PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(entry) != null)];
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or OverflowException)
        {
            throw new PlaylistDataParseException(ex is PlaylistJsonDateException ? ex.Message : BeMusicSeeker.Properties.Resources.Error_PlaylistDataParseFailed, ex);
        }
        entries = parsedEntries;
        ResolveDefaultCompatPrefixFolderOrderAfterDataLoad();
        RewriteLoadedCompatPrefixFolderOrderAfterDataLoad();
    }

    private void ResolveDefaultCompatPrefixFolderOrderAfterDataLoad()
    {
        if (!_resolveDefaultCompatPrefixFolderOrderAfterDataLoad)
        {
            return;
        }
        _resolveDefaultCompatPrefixFolderOrderAfterDataLoad = false;
        if (Folder_order == null || Folder_order.Count == 0)
        {
            return;
        }
        HashSet<string> entryFolders = [.. entries
            .Where(entry => !entry.is_removed)
            .Select(entry => entry.folder ?? string.Empty)];
        Folder_order = [.. Folder_order
            .Select(folder => entryFolders.Contains(folder ?? string.Empty) ? folder : ConvertCompatibleLevelNameToFolderName(folder ?? string.Empty))
            .Distinct(StringComparer.Ordinal)];
    }

    private void RewriteLoadedCompatPrefixFolderOrderAfterDataLoad()
    {
        if (!_rewriteLoadedCompatPrefixFolderOrderAfterDataLoad)
        {
            return;
        }
        _rewriteLoadedCompatPrefixFolderOrderAfterDataLoad = false;
        if (Folder_order == null || Folder_order.Count == 0)
        {
            return;
        }
        HashSet<string> entryFolders = [.. entries
            .Where(entry => !entry.is_removed)
            .Select(entry => entry.folder ?? string.Empty)];
        string sourceCompatPrefix = _loadedFolderOrderCompatPrefix ?? string.Empty;
        Folder_order = [.. Folder_order
            .Select(folder => entryFolders.Contains(folder ?? string.Empty)
                ? folder
                : MaterializeCompatibleFolderNameWithPrefix(folder ?? string.Empty, sourceCompatPrefix, base.compat_prefix ?? string.Empty))
            .Distinct(StringComparer.Ordinal)];
        _loadedFolderOrderCompatPrefix = null;
    }

    public bool IsCommitedToDB()
    {
        return base.playlist_id.HasValue;
    }

    /// <summary>
    /// 現在の <see cref="entries"/> と <see cref="Folder_order"/> から、
    /// 通常フォルダの表示順を算出します。
    /// </summary>
    /// <returns>特殊ノードを含まない、並び順適用後のフォルダ名一覧。</returns>
    private List<string> getSortedFolderList()
    {
        return GetSortedFolderList(activeFolderCounts.Keys.Select(key => key.Name), _Folder_order);
    }

    /// <summary>
    /// 指定した entry と明示順から、実 table と同じフォルダ表示順を算出します。
    /// Root-folder drop の計画中も live table を変更せず同じ順序規則を使うために公開します。
    /// </summary>
    /// <param name="sourceEntries">現在または計画中の playlist entry 群。</param>
    /// <param name="folderOrder">永続化されている明示フォルダ順。</param>
    /// <returns>特殊ノードを含まない、並び順適用後のフォルダ名一覧。</returns>
    internal static List<string> GetSortedFolderList(
        IEnumerable<BMSTableEntry> sourceEntries,
        IEnumerable<string> folderOrder)
    {
        return GetSortedFolderList(
            (sourceEntries ?? Enumerable.Empty<BMSTableEntry>())
            .Where(entry => entry != null && !entry.is_removed)
            .Select(entry => entry.folder),
            folderOrder);
    }

    /// <summary>
    /// フォルダ名集合と明示順から、実 table と同じフォルダ表示順を算出します。
    /// 計画中の entry がまだ folder field を持たない場合も、所属予定名をそのまま利用できます。
    /// </summary>
    /// <param name="sourceFolderNames">現在または計画中のフォルダ名。</param>
    /// <param name="folderOrder">永続化されている明示フォルダ順。</param>
    /// <returns>特殊ノードを含まない、並び順適用後のフォルダ名一覧。</returns>
    internal static List<string> GetSortedFolderList(
        IEnumerable<string> sourceFolderNames,
        IEnumerable<string> folderOrder)
    {
        List<string> folderList = [.. (sourceFolderNames ?? Enumerable.Empty<string>()).Distinct()];
        IEnumerable<string> enumerable = (folderOrder ?? Enumerable.Empty<string>())
            .Where(f => folderList.Contains(f))
            .Distinct(StringComparer.Ordinal);
        List<string> list = [.. folderList.Except(enumerable)];
        using (var comparer = new NaturalComparer())
        {
            list.Sort(comparer);
        }
        return [.. enumerable, .. list];
    }

    /// <summary>
    /// 並び順確定後のフォルダ名一覧から、ツリー表示用ノード一覧を構築します。
    /// 先頭に特殊ノードを配置し、その後に通常フォルダを並べます。
    /// </summary>
    /// <param name="orderedFolderNames">表示順確定後の通常フォルダ名一覧。</param>
    /// <returns>プレイリストツリー表示用ノード一覧。</returns>
    private static IReadOnlyList<PlaylistFolderNode> BuildFolderNodes(List<string> orderedFolderNames)
    {
        List<PlaylistFolderNode> list = [.. (from PlaylistFolderNodeSpecialKind kind in Enum.GetValues(typeof(PlaylistFolderNodeSpecialKind))
                                         where kind != PlaylistFolderNodeSpecialKind.None
                                         select PlaylistFolderNode.CreateSpecial(kind))];
        list.AddRange(orderedFolderNames.Select(folderName => PlaylistFolderNode.CreateFolder(folderName)));
        return list.AsReadOnly();
    }

    /// <summary>
    /// フォルダ状態を完成させて一度公開します。writer内の確定では通知を後段へ分離し、
    /// 同じ表示順は再利用します。getterや選択受付へ構築を遅延しません。
    /// </summary>
    private void RebuildFolderState(bool publishNotifications = true)
    {
        FolderState previous = Volatile.Read(ref folderState);
        List<string> sorted = getSortedFolderList();
        bool namesChanged = !previous.Names.SequenceEqual(sorted, StringComparer.Ordinal);
        IReadOnlyList<string> order = (_Folder_order == null) == (previous.Order == null)
            && (_Folder_order ?? []).SequenceEqual(previous.Order ?? [], StringComparer.Ordinal)
            ? previous.Order : _Folder_order;
        if (namesChanged || !ReferenceEquals(order, previous.Order))
        {
            Volatile.Write(ref folderState, new FolderState(order,
                namesChanged ? sorted.AsReadOnly() : previous.Names,
                namesChanged ? BuildFolderNodes(sorted) : previous.Nodes));
        }
        if (publishNotifications && namesChanged)
        {
            RaisePropertyChanged("folder_list");
            RaisePropertyChanged("FolderNodes");
        }
    }

    /// <summary>
    /// 現在の <see cref="entries"/> から、削除済みでない実フォルダ名の集合を取得します。
    /// 新規フォルダ作成時の重複回避判定に使用します。
    /// </summary>
    /// <returns>現在の実フォルダ名セット。</returns>
    private HashSet<string> GetExistingFolderNameSet()
    {
        return new HashSet<string>(activeFolderCounts.Keys.Select(key => key.Name), StringComparer.Ordinal);
    }

    internal bool CanRewriteCompatibleFolderPrefix(string oldPrefix, string newPrefix)
    {
        try
        {
            CreateValidatedCompatibleFolderPrefixRewriteMap(oldPrefix, newPrefix);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal IReadOnlyDictionary<string, string> CreateValidatedCompatibleFolderPrefixRewriteMap(string oldPrefix, string newPrefix)
    {
        oldPrefix ??= string.Empty;
        newPrefix ??= string.Empty;
        if (string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        Dictionary<string, string> folderMap = CreateCompatibleFolderPrefixRewriteMapCore(oldPrefix, newPrefix);
        if (folderMap.Count > 0)
        {
            ValidateCompatibleFolderRewriteMap(folderMap);
        }
        return folderMap;
    }

    private Dictionary<string, string> CreateCompatibleFolderPrefixRewriteMapCore(string oldPrefix, string newPrefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in EnumerateCompatibleFolderPrefixRewriteSourceFolders())
        {
            string rewrittenFolder = RewriteCompatibleFolderPrefixName(folder, oldPrefix, newPrefix);
            if (!string.Equals(folder, rewrittenFolder, StringComparison.Ordinal))
            {
                map[folder] = rewrittenFolder;
            }
        }
        return map;
    }

    private IEnumerable<string> EnumerateCompatibleFolderPrefixRewriteSourceFolders()
    {
        return entriesByFolder.Keys
            .Select(key => key.Name ?? string.Empty)
            .Concat((_Folder_order ?? []).Select(folder => folder ?? string.Empty))
            .Distinct(StringComparer.Ordinal);
    }

    private static string RewriteCompatibleFolderPrefixName(string folder, string oldPrefix, string newPrefix)
    {
        folder ??= string.Empty;
        oldPrefix ??= string.Empty;
        newPrefix ??= string.Empty;
        if (oldPrefix.Length > 0 && folder.StartsWith(oldPrefix, StringComparison.Ordinal))
        {
            folder = folder.Substring(oldPrefix.Length);
        }
        return newPrefix + folder;
    }

    private void ValidateCompatibleFolderRewriteMap(IReadOnlyDictionary<string, string> folderMap)
    {
        var finalFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in EnumerateCompatibleFolderPrefixRewriteSourceFolders())
        {
            string finalFolder = folderMap.TryGetValue(folder, out string rewrittenFolder)
                ? rewrittenFolder
                : folder;
            if (!finalFolders.Add(finalFolder))
            {
                throw new InvalidOperationException("Compatible playlist folder prefix rewrite creates duplicate folder: " + finalFolder);
            }
        }
    }

    public bool EnableExternalSync()
    {
        if (Page_url != null && Page_url.Scheme == "bmseeker")
        {
            return base.is_external_sync = true;
        }
        if (Header_url != null && Data_url != null && ((Page_url != null && Page_url.IsAbsoluteUri) || Header_url.IsAbsoluteUri))
        {
            return base.is_external_sync = true;
        }
        return base.is_external_sync = false;
    }

    public bool DisableExternalSync()
    {
        return base.is_external_sync = false;
    }

    public void RenameFolder(string folderNameBefore, string folderNameAfter)
    {
        EntryMutation mutation = PrepareFolderRename(folderNameBefore, folderNameAfter);
        ApplyEntryMutation(mutation);
        if (mutation.HasChanges) { PublishEntryCommitProperties(membershipChanged: true); }
    }
    public void RemoveFolder(string folderNameDelete)
    {
        RenameFolder(folderNameDelete, string.Empty);
    }

    public IEnumerable<BMSTableEntry> GetEntriesExceptDummy()
    {
        return entries.Where(e => e.md5 != "00000000000000000000000000000000");
    }

    /// <summary>
    /// 既存フォルダ名と衝突しない新規フォルダ名を解決します。
    /// Root-folder drop の計画中も同じ命名規則を使えるよう、table の変更を行わずに判定します。
    /// </summary>
    /// <param name="newName">希望するフォルダ名。</param>
    /// <param name="existingFolderNames">現在または計画済みのフォルダ名。</param>
    /// <returns>既存名と衝突しないフォルダ名。</returns>
    internal static string ResolveNewFolderName(
        string newName,
        IEnumerable<string> existingFolderNames)
    {
        string baseName = string.IsNullOrWhiteSpace(newName) ? BeMusicSeeker.Properties.Resources.NewFolderName : newName;
        HashSet<string> names = new(
            existingFolderNames ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        string resolvedName = baseName;
        int suffix = 1;
        while (names.Contains(resolvedName))
        {
            suffix++;
            resolvedName = baseName + " (" + suffix + ")";
        }
        return resolvedName;
    }

    public string CreateNewFolder(string newName)
    {
        string text = ResolveNewFolderName(newName, GetExistingFolderNameSet());
        var bMSTableEntry = BMSTableEntry.CreateDummyBMSTableEntry();
        bMSTableEntry.parent = this;
        bMSTableEntry.folder = text;
        AppendEntry(bMSTableEntry);
        TouchLastUpdate();
        RebuildFolderState();
        TouchPlaylistEntriesRevision();
        return text;
    }

    /// <summary>
    /// 指定した playlist entry をフォルダへ追加し、実際に追加があった場合だけ playlist の更新情報を進めます。
    /// </summary>
    /// <param name="bmsEntries">追加する playlist entry 群。</param>
    /// <param name="folderName">追加先フォルダ名。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsEntries"/> が <see langword="null"/> の場合。</exception>
    public void AddBMSTableEntriesToFolder(IEnumerable<BMSTableEntry> bmsEntries, string folderName = "")
    {
        if (bmsEntries == null)
        {
            throw new ArgumentNullException(nameof(bmsEntries));
        }
        EntryMutation mutation = PrepareEntryMutation([], [.. bmsEntries.Select(entry => (entry, folderName))]);
        ApplyEntryMutation(mutation);
    }

    /// <summary>対象参照だけを除去し、空になった非ルートフォルダのダミーを同じ局所変更で補います。</summary>
    public void RemoveBMSTableEntries(IEnumerable<BMSTableEntry> bmsEntries)
    {
        EntryMutation mutation = PrepareEntryMutation([.. bmsEntries], []);
        ApplyEntryMutation(mutation);
    }
    private List<BMSTableEntry> normalizeEntries(List<BMSTableEntry> inputEntries)
    {
        if (inputEntries.Count == 0)
        {
            return inputEntries;
        }
        var dictionary = new Dictionary<string, FolderNormalizeState>(StringComparer.Ordinal);
        FolderNormalizeState folderNormalizeState = null;
        HashSet<BMSTableEntry> hashSet = null;
        foreach (BMSTableEntry inputEntry in inputEntries)
        {
            FolderNormalizeState value;
            if (inputEntry.folder == null)
            {
                folderNormalizeState ??= new FolderNormalizeState();
                value = folderNormalizeState;
            }
            else if (!dictionary.TryGetValue(inputEntry.folder, out value))
            {
                value = new FolderNormalizeState();
                dictionary[inputEntry.folder] = value;
            }
            value.Count++;
            if (!value.SeenKeys.Add(getEntryIdentityKey(inputEntry)))
            {
                hashSet ??= [];
                hashSet.Add(inputEntry);
            }
            if (inputEntry.md5 == BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
            {
                value.DummyEntries.Add(inputEntry);
            }
        }
        addDummyRemovalTargets(dictionary, ref hashSet);
        if (folderNormalizeState != null && folderNormalizeState.DummyEntries.Count > 0 && folderNormalizeState.Count > 1)
        {
            hashSet ??= [];
            foreach (BMSTableEntry dummyEntry in folderNormalizeState.DummyEntries)
            {
                hashSet.Add(dummyEntry);
            }
        }
        if (hashSet == null || hashSet.Count == 0)
        {
            return inputEntries;
        }
        return [.. inputEntries.Where(e => !hashSet.Contains(e))];
    }

    private static void addDummyRemovalTargets(Dictionary<string, FolderNormalizeState> statesByFolder, ref HashSet<BMSTableEntry> removalSet)
    {
        foreach (KeyValuePair<string, FolderNormalizeState> item in statesByFolder)
        {
            FolderNormalizeState value = item.Value;
            if (value.DummyEntries.Count > 0 && (value.Count > 1 || item.Key == string.Empty))
            {
                removalSet ??= [];
                foreach (BMSTableEntry dummyEntry in value.DummyEntries)
                {
                    removalSet.Add(dummyEntry);
                }
            }
        }
    }

    private static string getEntryIdentityKey(BMSTableEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.md5))
        {
            return entry.md5;
        }
        if (!string.IsNullOrWhiteSpace(entry.sha256))
        {
            return entry.sha256;
        }
        if (!string.IsNullOrWhiteSpace(entry.lr2_bmsid))
        {
            return entry.lr2_bmsid;
        }
        if (!string.IsNullOrWhiteSpace(entry.title))
        {
            return entry.title;
        }
        return string.Empty;
    }

    private sealed class FolderNormalizeState
    {
        public readonly HashSet<string> SeenKeys = new(StringComparer.Ordinal);

        public readonly List<BMSTableEntry> DummyEntries = [];

        public int Count;
    }

    public string ConvertCompatibleLevelNameToFolderName(string levelValue)
    {
        return base.compat_prefix + levelValue;
    }

    public string ConvertBackFolderNameToCompatibleLevelName(string folderName)
    {
        return folderName.ReplaceFromStart(base.compat_prefix, "");
    }

    private void LoadCourseJsonFromHeader(string headerJson)
    {
        _Courses = [];
        try
        {
            JObject header = ParseJson(headerJson) as JObject ?? throw new FormatException("playlist header must be an object.");
            if (header["course"] == null)
            {
                return;
            }
            int order = 0;
            foreach (JObject courseToken in EnumerateCourseObjects(header["course"]))
            {
                string courseJson = courseToken.ToString(Formatting.None);
                _Courses.Add(new LR2SongDBExtended.playlist_course
                {
                    course_order = order++,
                    course_json = courseJson
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            _Courses = [];
        }
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

    private static IEnumerable<string> RequireStringArray(JToken value)
    {
        if (value is not JArray array
            || array.Any(token => token.Type is JTokenType.Null or JTokenType.Object or JTokenType.Array))
        {
            throw new FormatException("JSON value must be an array of primitive values.");
        }

        return array.Select(token => token.ToString());
    }

    private static IEnumerable<JObject> EnumerateCourseObjects(JToken token)
    {
        if (token is JObject obj)
        {
            yield return obj;
            yield break;
        }
        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                foreach (JObject course in EnumerateCourseObjects(item))
                {
                    yield return course;
                }
            }
        }
    }

    private static string NormalizeJsonOrNull(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return ParseJson(json).ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ComputeSha256Hex(string value)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    private static string ComputeHeaderSha256Hex(string headerJson)
    {
        try
        {
            JObject header = ParseJson(headerJson ?? string.Empty) as JObject ?? throw new FormatException("playlist header must be an object.");
            string compatPrefix = header.TryGetValue("compat_prefix", out JToken compatPrefixToken) && compatPrefixToken.Type != JTokenType.Null
                ? compatPrefixToken.ToString()
                : string.Empty;
            if (header.TryGetValue("folder_order", out JToken folderOrderToken) && folderOrderToken is JArray folderOrder)
            {
                for (int i = 0; i < folderOrder.Count; i++)
                {
                    if (folderOrder[i]?.Type == JTokenType.String)
                    {
                        folderOrder[i] = string.IsNullOrEmpty(compatPrefix)
                            ? folderOrder[i].ToString()
                            : folderOrder[i].ToString().ReplaceFromStart(compatPrefix, string.Empty);
                    }
                }
            }
            header.Remove("compat_prefix");
            return ComputeSha256Hex(header.ToString(Formatting.None));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return ComputeSha256Hex(headerJson);
        }
    }

    private static string MaterializeCompatibleFolderNameWithPrefix(string folderName, string sourceCompatPrefix, string targetCompatPrefix)
    {
        string compatibleLevel = string.IsNullOrEmpty(sourceCompatPrefix)
            ? folderName
            : folderName.ReplaceFromStart(sourceCompatPrefix, string.Empty);
        return (targetCompatPrefix ?? string.Empty) + compatibleLevel;
    }
}
