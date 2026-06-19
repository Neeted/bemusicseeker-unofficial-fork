using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Codeplex.Data;
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

public class BMSTable : LR2SongDBExtended.playlist
{
    private List<string> _Folder_order;

    private List<LR2SongDBExtended.playlist_course> _Courses = [];

    protected List<BMSTableEntry> _entries;

    private PlaylistEntriesLoadState _PlaylistEntriesLoadState = PlaylistEntriesLoadState.Loaded;

    private int _PlaylistEntriesRevision;

    private string _EntriesLoadErrorMessage = string.Empty;

    private bool _inferDefaultCompatPrefixFromDataJson;

    public override string folder_order
    {
        get
        {
            return DynamicJson.Serialize(Folder_order);
        }
        protected set
        {
            try
            {
                dynamic val = DynamicJson.Parse(value);
                Folder_order = [.. ((object[])val).Cast<string>()];
            }
            catch
            {
            }
        }
    }

    public List<string> Folder_order
    {
        get
        {
            return _Folder_order;
        }
        set
        {
            if (_Folder_order != value)
            {
                _Folder_order = value;
                RaisePropertyChanged("Folder_order");
                RebuildFolderState();
            }
        }
    }

    public override string page_url
    {
        get
        {
            if (!(Page_url == null) && !string.IsNullOrWhiteSpace(Page_url.ToString()) && Page_url.IsAbsoluteUri)
            {
                return Page_url.AbsoluteUri;
            }
            return string.Empty;
        }
        protected set
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
                return Header_url.ToString();
            }
            return string.Empty;
        }
        protected set
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
                return Data_url.ToString();
            }
            return string.Empty;
        }
        protected set
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
        catch (Exception ex)
        {
            exception = ex;
            return false;
        }
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
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve playlist " + context + ". rawValue=" + rawValue, ex);
        }
    }

    public List<BMSTableEntry> entries
    {
        get
        {
            return _entries;
        }
        internal set
        {
            if (value == null)
            {
                _entries = [];
                RebuildFolderState();
                MarkEntriesLoadedCore();
                return;
            }
            foreach (BMSTableEntry item in value)
            {
                item.parent = this;
            }
            _entries = normalizeEntries(value);
            RebuildFolderState();
            MarkEntriesLoadedCore();
        }
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
        _entries = [];
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

    private List<string> _cached_folder_list;

    private List<PlaylistFolderNode> _cached_folder_nodes;

    /// <summary>
    /// フォルダ名の並びを取得します。
    /// UI の直接バインド元ではなく、互換処理や JSON 出力でも利用する派生値です。
    /// </summary>
    public List<string> folder_list
    {
        get
        {
            EnsureFolderStateCache();
            return _cached_folder_list;
        }
        set
        {
            RebuildFolderState();
        }
    }

    /// <summary>
    /// プレイリストツリー表示用のフォルダノード一覧を取得します。
    /// 特殊ノードと通常フォルダの両方を含みます。
    /// </summary>
    public IReadOnlyList<PlaylistFolderNode> FolderNodes
    {
        get
        {
            EnsureFolderStateCache();
            return _cached_folder_nodes;
        }
    }

    public ReaderWriterLockSlimWrapper ReaderWriterLock { get; private set; }

    public BMSTable()
    {
        base.name = string.Empty;
        _entries = [];
        base.is_external_sync = false;
        base.is_root_folder = false;
        base.is_bmt_output = true;
        Folder_order = [];
        base.folder_sort_key = CustomFolderSortType.NONE;
        base.folder_sort_ascending = true;
        base.ignore_folder_output = CustomFolderType.LevelFolder
            | CustomFolderType.AlphabetFolder
            | CustomFolderType.CategoryAllFolder
            | CustomFolderType.LastPlaySortFolder;
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

    public string HeaderToJson()
    {
        return HeaderToDynamicJson().ToString();
    }

    public dynamic HeaderToDynamicJson()
    {
        dynamic val = new DynamicJson();
        val.name = base.name;
        val.symbol = base.symbol;
        val.level_order = folder_list.Select(f => ConvertBackFolderNameToCompatibleLevelName(f)).ToArray();
        val.folder_order = Folder_order.ToArray();
        val.folder_sort_key = base.folder_sort_key.ToColumnName();
        val.folder_sort_ascending = base.folder_sort_ascending;
        val.entry_type = base.entry_type.ToStringName();
        val.data_url = data_url;
        if (!string.IsNullOrWhiteSpace(base.tag))
        {
            val.tag = base.tag;
        }
        if (_Courses.Count > 0)
        {
            val.course = _Courses
                .OrderBy(course => course.course_order)
                .Select(course => DynamicJson.Parse(course.course_json))
                .ToArray();
        }
        val.compat_prefix = base.compat_prefix;
        val.last_update = base.last_update.ToShortDateString();
        val.editor_name = "BeMusicSeeker";
        val.editor_version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty;
        val.output_date = DateTime.Now.ToShortDateString();
        return val;
    }

    public dynamic DataToJson()
    {
        dynamic val = new DynamicJson(DynamicJson.JsonType.array);
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
        int num = 0;
        foreach (List<BMSTableEntry> item in list)
        {
            foreach (dynamic item2 in item.Select(e => e.ToDynamicJson()))
            {
                val[num] = item2;
                num++;
            }
        }
        return val.ToString();
    }

    public void LoadHeaderJSON(string _header_json, Uri _page_url_absolute = null, Uri __header_url = null, string _data_json = null, bool preserveLoadedCompatPrefix = false)
    {
        if (_header_json == null)
        {
            throw new ArgumentNullException("_header_json");
        }
        _inferDefaultCompatPrefixFromDataJson = false;
        try
        {
            dynamic val = DynamicJson.Parse(_header_json);
            base.header_sha256 = ComputeSha256Hex(_header_json);
            LoadCourseJsonFromHeader(_header_json);
            if (val.IsDefined("name") && val.name != null)
            {
                string text = (base.org_name = val.name.ToString());
                base.name = text;
            }
            if (val.IsDefined("symbol") && val.symbol != null)
            {
                string text = (base.org_symbol = val.symbol.ToString());
                base.symbol = text;
            }
            if (val.IsDefined("tag") && val.tag != null)
            {
                base.tag = val.tag.ToString();
            }
            if (val.IsDefined("folder_sort_key") && val.folder_sort_key != null)
            {
                base.folder_sort_key = CustomFolderSortTypeExt.FromColumnName(val.folder_sort_key.ToString());
            }
            if (val.IsDefined("folder_sort_ascending"))
            {
                base.folder_sort_ascending = (val.folder_sort_ascending as bool?) ?? true;
            }
            if (val.IsDefined("entry_type"))
            {
                base.entry_type = EntryUnitTypeExt.FromStringName(val.entry_type.ToString());
            }
            bool hasExplicitCompatPrefix = val.IsDefined("compat_prefix") && val.compat_prefix != null;
            if (hasExplicitCompatPrefix)
            {
                base.compat_prefix = val.compat_prefix.ToString();
            }
            if (val.IsDefined("last_update") && val.last_update != null && !string.IsNullOrWhiteSpace(val.last_update.ToString()))
            {
                try
                {
                    base.last_update = DateTime.Parse(val.last_update.ToString());
                }
                catch
                {
                }
            }
            List<string> headerFolderOrder = null;
            if (val.IsDefined("folder_order") && val.folder_order != null)
            {
                try
                {
                    headerFolderOrder = [.. ((object[])val.folder_order).Select(e => e.ToString()).Cast<string>()];
                    Folder_order = headerFolderOrder;
                }
                catch
                {
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
            if (val.IsDefined("data_url") && val.data_url != null)
            {
                Data_url = ParsePlaylistUriOrThrow(val.data_url.ToString(), "data_url");
            }
            if (!val.IsDefined("compat_prefix") || !val.IsDefined("folder_sort_key") || !val.IsDefined("folder_sort_ascending"))
            {
                base.ignore_folder_output |= CustomFolderType.LevelFolder;
                if (!hasExplicitCompatPrefix && !preserveLoadedCompatPrefix)
                {
                    string inferredCompatPrefix = InferDefaultCompatibleFolderPrefixFromHeader(val, base.symbol, headerFolderOrder);
                    if (inferredCompatPrefix != null)
                    {
                        base.compat_prefix = inferredCompatPrefix;
                    }
                    else
                    {
                        base.compat_prefix = string.Empty;
                        _inferDefaultCompatPrefixFromDataJson = !string.IsNullOrWhiteSpace(base.symbol);
                    }
                }
                if (val.IsDefined("level_order"))
                {
                    try
                    {
                        Folder_order = [.. ((object[])val.level_order).Select(e => ConvertCompatibleLevelNameToFolderName(e.ToString())).Cast<string>()];
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlaylistHeaderParseException("ヘッダのパースに失敗しました", ex);
        }
        if (_data_json != null)
        {
            try
            {
                LoadDataJSON(_data_json);
            }
            catch
            {
                throw;
            }
        }
        EnableExternalSync();
    }

    private static string InferDefaultCompatibleFolderPrefixFromHeader(dynamic headerJson, string symbol, IEnumerable<string> headerFolderOrder)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }
        string firstLevelOrderValue = GetFirstHeaderLevelOrderValue(headerJson);
        if (firstLevelOrderValue != null)
        {
            return IsAsciiDigitsOnly(firstLevelOrderValue) ? symbol : string.Empty;
        }
        string firstFolderOrderValue = GetFirstNonEmptyString(headerFolderOrder);
        if (firstFolderOrderValue != null)
        {
            if (firstFolderOrderValue.StartsWith(symbol, StringComparison.Ordinal))
            {
                string folderOrderSuffix = firstFolderOrderValue.Substring(symbol.Length);
                if (IsAsciiDigitsOnly(folderOrderSuffix))
                {
                    return symbol;
                }
            }
            return string.Empty;
        }
        return null;
    }

    private static string GetFirstHeaderLevelOrderValue(dynamic headerJson)
    {
        try
        {
            if (!headerJson.IsDefined("level_order") || headerJson.level_order == null)
            {
                return null;
            }
            return GetFirstNonEmptyString(((object[])headerJson.level_order).Select(e => e?.ToString()));
        }
        catch
        {
            return null;
        }
    }

    private static string GetFirstNonEmptyString(IEnumerable<string> values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsAsciiDigitsOnly(string value)
    {
        return !string.IsNullOrEmpty(value) && value.All(c => c >= '0' && c <= '9');
    }

    public void LoadDataJSON(string _data_json)
    {
        if (_data_json == null)
        {
            throw new ArgumentNullException("_data_json");
        }
        try
        {
            dynamic val = DynamicJson.Parse(_data_json);
            base.data_sha256 = ComputeSha256Hex(_data_json);
            InferDefaultCompatibleFolderPrefixFromDataJson(val);
            entries = [.. ((object[])val).Select((dynamic json) => new BMSTableEntry(json, this)).Where(entry => BMSPlaylist.CreateComparablePlaylistEntryRow(entry) != null)];
        }
        catch (Exception ex)
        {
            throw new PlaylistDataParseException("データのパースに失敗しました", ex);
        }
    }

    private void InferDefaultCompatibleFolderPrefixFromDataJson(dynamic dataJson)
    {
        if (!_inferDefaultCompatPrefixFromDataJson)
        {
            return;
        }
        _inferDefaultCompatPrefixFromDataJson = false;
        if (string.IsNullOrWhiteSpace(base.symbol))
        {
            base.compat_prefix = string.Empty;
            return;
        }
        string firstLevelValue = GetFirstDataJsonPropertyValue(dataJson, "level");
        if (firstLevelValue != null)
        {
            base.compat_prefix = IsAsciiDigitsOnly(firstLevelValue) ? base.symbol : string.Empty;
            return;
        }
        string firstFolderValue = GetFirstDataJsonPropertyValue(dataJson, "folder");
        if (firstFolderValue != null && firstFolderValue.StartsWith(base.symbol, StringComparison.Ordinal))
        {
            string folderSuffix = firstFolderValue.Substring(base.symbol.Length);
            base.compat_prefix = IsAsciiDigitsOnly(folderSuffix) ? base.symbol : string.Empty;
            return;
        }
        base.compat_prefix = string.Empty;
    }

    private static string GetFirstDataJsonPropertyValue(dynamic dataJson, string propertyName)
    {
        try
        {
            foreach (dynamic entry in (object[])dataJson)
            {
                if (entry != null && entry.IsDefined(propertyName))
                {
                    object value = entry[propertyName];
                    if (value != null)
                    {
                        string text = value.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return null;
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
        List<string> folderList = [.. (from e in entries
                                   where !e.is_removed
                                   select e.folder).Distinct()];
        IEnumerable<string> enumerable = (Folder_order ?? [])
            .Where(f => folderList.Contains(f))
            .Distinct(StringComparer.Ordinal);
        List<string> list = [.. folderList.Except(enumerable)];
        using (var comparer = new NaturalComparer<string>())
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
    private List<PlaylistFolderNode> BuildFolderNodes(List<string> orderedFolderNames)
    {
        List<PlaylistFolderNode> list = [.. (from PlaylistFolderNodeSpecialKind kind in Enum.GetValues(typeof(PlaylistFolderNodeSpecialKind))
                                         where kind != PlaylistFolderNodeSpecialKind.None
                                         select PlaylistFolderNode.CreateSpecial(kind))];
        list.AddRange(orderedFolderNames.Select(folderName => PlaylistFolderNode.CreateFolder(folderName)));
        return list;
    }

    /// <summary>
    /// フォルダ状態に関する派生キャッシュを無効化します。
    /// <see cref="folder_list"/> と <see cref="FolderNodes"/> は必ず同時に無効化します。
    /// </summary>
    private void InvalidateFolderStateCache()
    {
        _cached_folder_list = null;
        _cached_folder_nodes = null;
    }

    /// <summary>
    /// フォルダ状態キャッシュが未構築の場合に再計算します。
    /// <see cref="_cached_folder_list"/> と <see cref="_cached_folder_nodes"/> の整合性をここで揃えます。
    /// </summary>
    private void EnsureFolderStateCache()
    {
        if (_cached_folder_list == null || _cached_folder_nodes == null)
        {
            List<string> sortedFolderList = getSortedFolderList();
            _cached_folder_list = sortedFolderList;
            _cached_folder_nodes = BuildFolderNodes(sortedFolderList);
        }
    }

    /// <summary>
    /// フォルダ状態の派生値を再構築し、関連プロパティ変更通知を発行します。
    /// フォルダ構成が変わる更新経路は、このメソッドを通じて状態を同期します。
    /// </summary>
    private void RebuildFolderState()
    {
        InvalidateFolderStateCache();
        EnsureFolderStateCache();
        RaisePropertyChanged("folder_list");
        RaisePropertyChanged("FolderNodes");
    }

    /// <summary>
    /// 現在の <see cref="entries"/> から、削除済みでない実フォルダ名の集合を取得します。
    /// 新規フォルダ作成時の重複回避判定に使用します。
    /// </summary>
    /// <returns>現在の実フォルダ名セット。</returns>
    private HashSet<string> GetExistingFolderNameSet()
    {
        return new HashSet<string>(from e in entries
                                   where !e.is_removed
                                   select e.folder, StringComparer.Ordinal);
    }

    internal bool RewriteCompatibleFolderPrefix(string oldPrefix, string newPrefix)
    {
        return RewriteCompatibleFolderPrefix(oldPrefix, newPrefix, out _);
    }

    internal bool CanRewriteCompatibleFolderPrefix(string oldPrefix, string newPrefix, bool treatUnprefixedFoldersAsExternal = false)
    {
        try
        {
            CreateValidatedCompatibleFolderPrefixRewriteMap(oldPrefix, newPrefix, treatUnprefixedFoldersAsExternal);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal IReadOnlyDictionary<string, string> CreateValidatedCompatibleFolderPrefixRewriteMap(string oldPrefix, string newPrefix, bool treatUnprefixedFoldersAsExternal = false)
    {
        oldPrefix ??= string.Empty;
        newPrefix ??= string.Empty;
        if (string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal) || entries == null || entries.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        Dictionary<string, string> folderMap = CreateCompatibleFolderPrefixRewriteMapCore(oldPrefix, newPrefix, treatUnprefixedFoldersAsExternal);
        if (folderMap.Count > 0)
        {
            ValidateCompatibleFolderRewriteMap(folderMap);
        }
        return folderMap;
    }

    internal bool RewriteCompatibleFolderPrefix(string oldPrefix, string newPrefix, out IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        IReadOnlyDictionary<string, string> folderMap = CreateValidatedCompatibleFolderPrefixRewriteMap(oldPrefix, newPrefix);
        if (folderMap.Count == 0)
        {
            rewrittenFolders = folderMap;
            return false;
        }
        rewrittenFolders = folderMap;

        foreach (BMSTableEntry entry in entries)
        {
            if (entry != null && folderMap.TryGetValue(entry.folder ?? string.Empty, out string rewrittenFolder))
            {
                entry.folder = rewrittenFolder;
            }
        }
        Folder_order = [.. (Folder_order ?? []).Select(folder => folderMap.TryGetValue(folder ?? string.Empty, out string rewrittenFolder) ? rewrittenFolder : folder).Distinct(StringComparer.Ordinal)];
        RebuildFolderState();
        TouchPlaylistEntriesRevision();
        return true;
    }

    internal bool RewriteCompatibleFolderPrefix(string oldPrefix, string newPrefix, bool treatUnprefixedFoldersAsExternal, out IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        IReadOnlyDictionary<string, string> folderMap = CreateValidatedCompatibleFolderPrefixRewriteMap(oldPrefix, newPrefix, treatUnprefixedFoldersAsExternal);
        if (folderMap.Count == 0)
        {
            rewrittenFolders = folderMap;
            return false;
        }
        rewrittenFolders = folderMap;

        foreach (BMSTableEntry entry in entries)
        {
            if (entry != null && folderMap.TryGetValue(entry.folder ?? string.Empty, out string rewrittenFolder))
            {
                entry.folder = rewrittenFolder;
            }
        }
        Folder_order = [.. (Folder_order ?? []).Select(folder => folderMap.TryGetValue(folder ?? string.Empty, out string rewrittenFolder) ? rewrittenFolder : folder).Distinct(StringComparer.Ordinal)];
        RebuildFolderState();
        TouchPlaylistEntriesRevision();
        return true;
    }

    private Dictionary<string, string> CreateCompatibleFolderPrefixRewriteMapCore(string oldPrefix, string newPrefix, bool treatUnprefixedFoldersAsExternal)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<string> sourceFolders = (entries ?? [])
            .Select(entry => entry?.folder ?? string.Empty)
            .Concat(Folder_order ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.Ordinal);

        foreach (string folder in sourceFolders)
        {
            string compatibleLevelName = null;
            if (oldPrefix.Length > 0)
            {
                if (!folder.StartsWith(oldPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                compatibleLevelName = folder.Substring(oldPrefix.Length);
            }
            else if ((base.is_external_sync || treatUnprefixedFoldersAsExternal) && (newPrefix.Length == 0 || !folder.StartsWith(newPrefix, StringComparison.Ordinal)))
            {
                compatibleLevelName = folder;
            }
            if (string.IsNullOrWhiteSpace(compatibleLevelName))
            {
                continue;
            }
            string rewrittenFolder = newPrefix + compatibleLevelName;
            if (!string.Equals(folder, rewrittenFolder, StringComparison.Ordinal))
            {
                map[folder] = rewrittenFolder;
            }
        }
        return map;
    }

    private void ValidateCompatibleFolderRewriteMap(IReadOnlyDictionary<string, string> folderMap)
    {
        HashSet<string> existingFolders = [.. (entries ?? [])
            .Select(entry => entry?.folder ?? string.Empty)
            .Concat(Folder_order ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.Ordinal)];
        var rewrittenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in folderMap)
        {
            if (!rewrittenTargets.Add(pair.Value))
            {
                throw new InvalidOperationException("Compatible playlist folder prefix rewrite creates duplicate folder: " + pair.Value);
            }
            if (existingFolders.Contains(pair.Value) && !folderMap.ContainsKey(pair.Value))
            {
                throw new InvalidOperationException("Compatible playlist folder prefix rewrite collides with existing folder: " + pair.Value);
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
        foreach (BMSTableEntry entry in entries)
        {
            if (entry.folder == folderNameBefore)
            {
                entry.folder = folderNameAfter;
            }
        }
        _entries = rebuildFolder(folderNameAfter);
        if (string.IsNullOrWhiteSpace(folderNameAfter))
        {
            if (entries.Any(e => string.IsNullOrWhiteSpace(e.folder) && e.md5 != "00000000000000000000000000000000") && !Folder_order.Contains(string.Empty))
            {
                Folder_order.Insert(0, string.Empty);
            }
            Folder_order = [.. Folder_order.Where(f => f != folderNameBefore)];
        }
        else
        {
            Folder_order = [.. Folder_order.Select(f => (!(f == folderNameBefore)) ? f : folderNameAfter).Distinct()];
        }
        TouchLastUpdate();
        TouchPlaylistEntriesRevision();
    }

    public void RemoveFolder(string folderNameDelete)
    {
        RenameFolder(folderNameDelete, string.Empty);
    }

    public IEnumerable<BMSTableEntry> GetEntriesExceptDummy()
    {
        return entries.Where(e => e.md5 != "00000000000000000000000000000000");
    }

    public string CreateNewFolder(string newName)
    {
        string text = (newName = (string.IsNullOrWhiteSpace(newName) ? "新しいフォルダー" : newName));
        HashSet<string> existingFolderNames = GetExistingFolderNameSet();
        int num = 1;
        while (existingFolderNames.Contains(text))
        {
            num++;
            text = newName + " (" + num + ")";
        }
        var bMSTableEntry = BMSTableEntry.CreateDummyBMSTableEntry();
        bMSTableEntry.parent = this;
        bMSTableEntry.folder = text;
        _entries.Add(bMSTableEntry);
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
        List<BMSTableEntry> entriesToAdd = [.. bmsEntries];
        if (entriesToAdd.Count == 0)
        {
            return;
        }
        bool shouldRebuildFolderState = !GetExistingFolderNameSet().Contains(folderName);
        foreach (BMSTableEntry entryToAdd in entriesToAdd)
        {
            entryToAdd.folder = folderName;
            entryToAdd.parent = this;
        }
        _entries = rebuildFolder(folderName, entries.Concat(entriesToAdd));
        TouchLastUpdate();
        if (shouldRebuildFolderState)
        {
            RebuildFolderState();
        }
        TouchPlaylistEntriesRevision();
    }

    public void RemoveBMSTableEntries(IEnumerable<BMSTableEntry> bmsEntries)
    {
        List<string> source = [.. bmsEntries.Select(e => e.folder).Distinct()];
        _entries = [.. entries.Except(bmsEntries)];
        bool flag = false;
        foreach (string item in source.Where(f => _entries.Where(e => e.folder == f).Count() == 0))
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                flag = true;
                continue;
            }
            var bMSTableEntry = BMSTableEntry.CreateDummyBMSTableEntry();
            bMSTableEntry.parent = this;
            bMSTableEntry.folder = item;
            _entries.Add(bMSTableEntry);
        }
        if (flag)
        {
            RebuildFolderState();
        }
        TouchLastUpdate();
        TouchPlaylistEntriesRevision();
    }

    private List<BMSTableEntry> rebuildFolder(string folderName, IEnumerable<BMSTableEntry> inputEntries = null)
    {
        inputEntries ??= entries;
        List<BMSTableEntry> list = [.. inputEntries.Where(e => e.folder == folderName)];
        IEnumerable<IGrouping<string, BMSTableEntry>> source = list.GroupBy(delegate (BMSTableEntry e)
        {
            string text = string.Empty;
            if (!string.IsNullOrWhiteSpace(e.md5))
            {
                text += e.md5;
            }
            else if (!string.IsNullOrWhiteSpace(e.sha256))
            {
                text += e.sha256;
            }
            else if (!string.IsNullOrWhiteSpace(e.lr2_bmsid))
            {
                text += e.lr2_bmsid;
            }
            else if (!string.IsNullOrWhiteSpace(e.title))
            {
                text += e.title;
            }
            return text;
        });
        List<BMSTableEntry> second = [.. list.Except(source.Select(g => g.First()))];
        List<BMSTableEntry> second2 = [];
        if ((list.Count > 1 || folderName == string.Empty) && list.Any(e => e.md5 == "00000000000000000000000000000000"))
        {
            second2 = [.. list.Where(e => e.md5 == "00000000000000000000000000000000")];
        }
        return [.. inputEntries.Except(second).Except(second2)];
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
            var header = JObject.Parse(headerJson);
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
        catch
        {
            _Courses = [];
        }
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
            return JToken.Parse(json).ToString(Formatting.None);
        }
        catch
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
}
