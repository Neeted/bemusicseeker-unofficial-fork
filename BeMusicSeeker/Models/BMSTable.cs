using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Codeplex.Data;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class BMSTable : LR2SongDBExtended.playlist
{
    private List<string> _Folder_order;

    protected List<BMSTableEntry> _entries;

    private const string compat_prefix_external_default = "LEVEL ";

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
                Folder_order = ((object[])val).Cast<string>().ToList();
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
                _cached_folder_list = null;
                RaisePropertyChanged("Folder_order");
                RaisePropertyChanged(() => folder_list);
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
            try
            {
                Page_url = new Uri(value, UriKind.Absolute);
            }
            catch
            {
            }
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
            try
            {
                Header_url = new Uri(value, UriKind.RelativeOrAbsolute);
            }
            catch
            {
            }
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
            try
            {
                Data_url = new Uri(value, UriKind.RelativeOrAbsolute);
            }
            catch
            {
            }
        }
    }

    public Uri Data_url { get; set; }

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
                _entries = new List<BMSTableEntry>();
                _cached_folder_list = null;
                RaisePropertyChanged("folder_list");
                return;
            }
            foreach (BMSTableEntry item in value)
            {
                item.parent = this;
            }
            _entries = normalizeEntries(value);
            _cached_folder_list = null;
            RaisePropertyChanged("folder_list");
        }
    }

    public string Output_dir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(base.output_dir) && base.output_dir != base.name.Trim().ToSjisSchemeString())
            {
                return base.output_dir;
            }
            return base.name.Trim().ToSjisSchemeString();
        }
        set
        {
            value = value?.Trim().ToSjisSchemeString();
            if (base.name != null && base.name.Trim().ToSjisSchemeString() != value)
            {
                base.output_dir = value;
            }
            else if (string.IsNullOrWhiteSpace(value) || value == base.name.Trim().ToSjisSchemeString())
            {
                base.output_dir = null;
            }
        }
    }

    private List<string> _cached_folder_list;
    public List<string> folder_list
    {
        get
        {
            if (_cached_folder_list == null)
            {
                _cached_folder_list = getSortedFolderList();
            }
            return _cached_folder_list;
        }
        set
        {
            _cached_folder_list = null;
            RaisePropertyChanged("folder_list");
        }
    }

    public ReaderWriterLockSlimWrapper ReaderWriterLock { get; private set; }

    public BMSTable()
    {
        base.name = string.Empty;
        _entries = new List<BMSTableEntry>();
        base.is_external_sync = false;
        base.is_root_folder = false;
        Folder_order = new List<string>();
        base.folder_sort_key = CustomFolderSortType.NONE;
        base.folder_sort_ascending = true;
        base.ignore_folder_output = CustomFolderType.None;
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
        val.level_order = folder_list.Select((string f) => ConvertBackFolderNameToCompatibleLevelName(f)).ToArray();
        val.folder_order = Folder_order.ToArray();
        val.folder_sort_key = base.folder_sort_key.ToColumnName();
        val.folder_sort_ascending = base.folder_sort_ascending;
        val.entry_type = base.entry_type.ToStringName();
        val.data_url = data_url;
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
        List<List<BMSTableEntry>> list = folder_list.Select(delegate (string f)
        {
            IEnumerable<BMSTableEntry> source = entries.Where((BMSTableEntry e) => !e.is_removed && e.folder == f);
            return (base.folder_sort_key switch
            {
                CustomFolderSortType.LEVEL => (!base.folder_sort_ascending) ? source.OrderByDescending((BMSTableEntry e) => e.level) : source.OrderBy((BMSTableEntry e) => e.level),
                CustomFolderSortType.ARTIST => (!base.folder_sort_ascending) ? source.OrderByDescending((BMSTableEntry e) => e.artist) : source.OrderBy((BMSTableEntry e) => e.artist),
                CustomFolderSortType.ADDDATE => (!base.folder_sort_ascending) ? source.OrderByDescending((BMSTableEntry e) => e.adddate.ToLocalTime()) : source.OrderBy((BMSTableEntry e) => e.adddate.ToLocalTime()),
                _ => (!base.folder_sort_ascending) ? source.OrderByDescending((BMSTableEntry e) => e.title) : source.OrderBy((BMSTableEntry e) => e.title),
            }).ToList();
        }).ToList();
        int num = 0;
        foreach (List<BMSTableEntry> item in list)
        {
            foreach (dynamic item2 in item.Select((BMSTableEntry e) => e.ToDynamicJson()))
            {
                val[num] = item2;
                num++;
            }
        }
        return val.ToString();
    }

    public void LoadHeaderJSON(string _header_json, Uri _page_url_absolute = null, Uri __header_url = null, string _data_json = null)
    {
        if (_header_json == null)
        {
            throw new ArgumentNullException("_header_json");
        }
        try
        {
            dynamic val = DynamicJson.Parse(_header_json);
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
            if (val.IsDefined("compat_prefix") && val.compat_prefix != null)
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
            if (val.IsDefined("folder_order") && val.folder_order != null)
            {
                try
                {
                    Folder_order = ((object[])val.folder_order).Select((object e) => e.ToString()).Cast<string>().ToList();
                }
                catch
                {
                }
            }
            if (_page_url_absolute != null && _page_url_absolute.IsAbsoluteUri)
            {
                Page_url = _page_url_absolute;
            }
            if (header_url != null)
            {
                Header_url = __header_url;
            }
            if (val.IsDefined("data_url") && val.data_url != null)
            {
                data_url = val.data_url.ToString();
            }
            if (!val.IsDefined("compat_prefix") || !val.IsDefined("folder_sort_key") || !val.IsDefined("folder_sort_ascending"))
            {
                base.ignore_folder_output |= CustomFolderType.LevelFolder;
                if (string.IsNullOrWhiteSpace(base.compat_prefix))
                {
                    base.compat_prefix = "LEVEL ";
                }
                if (val.IsDefined("level_order"))
                {
                    try
                    {
                        Folder_order = ((object[])val.level_order).Select((object e) => ConvertCompatibleLevelNameToFolderName(e.ToString())).Cast<string>().ToList();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
            throw new ArgumentException("ヘッダのパースに失敗しました", "_header_json");
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

    public void LoadDataJSON(string _data_json)
    {
        if (_data_json == null)
        {
            throw new ArgumentNullException("_data_json");
        }
        try
        {
            dynamic val = DynamicJson.Parse(_data_json);
            entries = ((object[])val).Select((dynamic json) => new BMSTableEntry(json, this)).ToList();
        }
        catch
        {
            throw new ArgumentException("データのパースに失敗しました", "_data_json");
        }
    }

    public bool IsCommitedToDB()
    {
        return base.playlist_id.HasValue;
    }

    private List<string> getSortedFolderList()
    {
        List<string> folderList = (from e in entries
                                   where !e.is_removed
                                   select e.folder).Distinct().ToList();
        IEnumerable<string> enumerable = Folder_order.Where((string f) => folderList.Contains(f));
        List<string> list = folderList.Except(enumerable).ToList();
        using (NaturalComparer<string> comparer = new NaturalComparer<string>())
        {
            list.Sort(comparer);
        }
        return enumerable.Concat(list).ToList();
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
            if (entries.Any((BMSTableEntry e) => string.IsNullOrWhiteSpace(e.folder) && e.md5 != "00000000000000000000000000000000") && !Folder_order.Contains(string.Empty))
            {
                Folder_order.Insert(0, string.Empty);
            }
            Folder_order = Folder_order.Where((string f) => f != folderNameBefore).ToList();
        }
        else
        {
            Folder_order = Folder_order.Select((string f) => (!(f == folderNameBefore)) ? f : folderNameAfter).Distinct().ToList();
        }
        base.last_update = DateTime.Now;
    }

    public void RemoveFolder(string folderNameDelete)
    {
        RenameFolder(folderNameDelete, string.Empty);
    }

    public IEnumerable<BMSTableEntry> GetEntriesExceptDummy()
    {
        return entries.Where((BMSTableEntry e) => e.md5 != "00000000000000000000000000000000");
    }

    public string CreateNewFolder(string newName)
    {
        string text = (newName = (string.IsNullOrWhiteSpace(newName) ? "新しいフォルダー" : newName));
        int num = 1;
        while (folder_list.Contains(text))
        {
            num++;
            text = newName + " (" + num + ")";
        }
        BMSTableEntry bMSTableEntry = BMSTableEntry.CreateDummyBMSTableEntry();
        bMSTableEntry.parent = this;
        bMSTableEntry.folder = text;
        _entries.Add(bMSTableEntry);
        base.last_update = DateTime.Now;
        RaisePropertyChanged(() => folder_list);
        return text;
    }

    public void AddBMSTableEntriesToFolder(IEnumerable<BMSFile> bmsFiles, string folderName = "")
    {
        List<BMSTableEntry> bmsEntries = bmsFiles.Select((BMSFile f) => new BMSTableEntry(f)).ToList();
        AddBMSTableEntriesToFolder(bmsEntries, folderName);
    }

    public void AddBMSTableEntriesToFolder(IEnumerable<BMSTableEntry> bmsEntries, string folderName = "")
    {
        bool flag = !folder_list.Contains(folderName);
        List<BMSTableEntry> list = bmsEntries.ToList();
        foreach (BMSTableEntry item in list)
        {
            item.folder = folderName;
            item.parent = this;
        }
        _entries = rebuildFolder(folderName, entries.Concat(list));
        base.last_update = DateTime.Now;
        if (flag)
        {
            RaisePropertyChanged(() => folder_list);
        }
    }

    public void RemoveBMSTableEntries(IEnumerable<BMSTableEntry> bmsEntries)
    {
        List<string> source = bmsEntries.Select((BMSTableEntry e) => e.folder).Distinct().ToList();
        _entries = entries.Except(bmsEntries).ToList();
        bool flag = false;
        foreach (string item in source.Where((string f) => _entries.Where((BMSTableEntry e) => e.folder == f).Count() == 0))
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                flag = true;
                continue;
            }
            BMSTableEntry bMSTableEntry = BMSTableEntry.CreateDummyBMSTableEntry();
            bMSTableEntry.parent = this;
            bMSTableEntry.folder = item;
            _entries.Add(bMSTableEntry);
        }
        if (flag)
        {
            RaisePropertyChanged(() => folder_list);
        }
        base.last_update = DateTime.Now;
    }

    private List<BMSTableEntry> rebuildFolder(string folderName, IEnumerable<BMSTableEntry> inputEntries = null)
    {
        if (inputEntries == null)
        {
            inputEntries = entries;
        }
        List<BMSTableEntry> list = inputEntries.Where((BMSTableEntry e) => e.folder == folderName).ToList();
        IEnumerable<IGrouping<string, BMSTableEntry>> source = list.GroupBy(delegate (BMSTableEntry e)
        {
            string text = string.Empty;
            if (!string.IsNullOrWhiteSpace(e.md5))
            {
                text += e.md5;
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
        List<BMSTableEntry> second = list.Except(source.Select((IGrouping<string, BMSTableEntry> g) => g.First())).ToList();
        List<BMSTableEntry> second2 = new List<BMSTableEntry>();
        if ((list.Count > 1 || folderName == string.Empty) && list.Any((BMSTableEntry e) => e.md5 == "00000000000000000000000000000000"))
        {
            second2 = list.Where((BMSTableEntry e) => e.md5 == "00000000000000000000000000000000").ToList();
        }
        return inputEntries.Except(second).Except(second2).ToList();
    }

    private List<BMSTableEntry> normalizeEntries(List<BMSTableEntry> inputEntries)
    {
        if (inputEntries.Count == 0)
        {
            return inputEntries;
        }
        Dictionary<string, FolderNormalizeState> dictionary = new Dictionary<string, FolderNormalizeState>(StringComparer.Ordinal);
        FolderNormalizeState folderNormalizeState = null;
        HashSet<BMSTableEntry> hashSet = null;
        foreach (BMSTableEntry inputEntry in inputEntries)
        {
            FolderNormalizeState value;
            if (inputEntry.folder == null)
            {
                if (folderNormalizeState == null)
                {
                    folderNormalizeState = new FolderNormalizeState();
                }
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
                hashSet = hashSet ?? new HashSet<BMSTableEntry>();
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
            hashSet = hashSet ?? new HashSet<BMSTableEntry>();
            foreach (BMSTableEntry dummyEntry in folderNormalizeState.DummyEntries)
            {
                hashSet.Add(dummyEntry);
            }
        }
        if (hashSet == null || hashSet.Count == 0)
        {
            return inputEntries;
        }
        return inputEntries.Where((BMSTableEntry e) => !hashSet.Contains(e)).ToList();
    }

    private static void addDummyRemovalTargets(Dictionary<string, FolderNormalizeState> statesByFolder, ref HashSet<BMSTableEntry> removalSet)
    {
        foreach (KeyValuePair<string, FolderNormalizeState> item in statesByFolder)
        {
            FolderNormalizeState value = item.Value;
            if (value.DummyEntries.Count > 0 && (value.Count > 1 || item.Key == string.Empty))
            {
                removalSet = removalSet ?? new HashSet<BMSTableEntry>();
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
        public readonly HashSet<string> SeenKeys = new HashSet<string>(StringComparer.Ordinal);

        public readonly List<BMSTableEntry> DummyEntries = new List<BMSTableEntry>();

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
}
