using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IInstalledChartLookupIndex : IPrimaryHashLookup
{
    int HashCount { get; }

    IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash);

    int GetUniquePrimaryHashCountByDirectory(string directoryPath);

    /// <summary>
    /// 主ハッシュを持つ所持譜面のパスを、大文字小文字を無視した昇順で返します。
    /// 所持の実ファイルが存在することの裏付けを、再ハッシュや走査なしで確かめるために、DB上の所持の記録から引きます。
    /// </summary>
    /// <param name="lookupHash">検索する主ハッシュ。</param>
    /// <returns>譜面のパス。該当がなければ空です。</returns>
    IReadOnlyList<string> GetPathsByPrimaryHash(string lookupHash);
}

internal interface IPrimaryHashLookup
{
    int DistinctPrimaryHashCount { get; }

    bool ContainsPrimaryHash(string lookupHash);

    int GetPrimaryHashCount(string lookupHash);
}

internal interface IMutablePrimaryHashLookup : IPrimaryHashLookup
{
    void AddPrimaryHash(string lookupHash);
}

/// <summary>
/// primary md5 count だけを保持する immutable snapshot です。
/// full installed directory lookup を必要としない duplicate merge / installed 判定で
/// directory map のコピーを避けるために使います。
/// </summary>
internal sealed class PrimaryHashLookupSnapshot : IPrimaryHashLookup
{
    private static readonly ImmutableDictionary<string, int> EmptyCounts =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public PrimaryHashLookupSnapshot()
        : this(EmptyCounts)
    {
    }

    private PrimaryHashLookupSnapshot(ImmutableDictionary<string, int> primaryHashCounts)
    {
        this.primaryHashCounts = primaryHashCounts ?? EmptyCounts;
    }

    /// <summary>
    /// mutable state から primary md5 count root を作成します。
    /// </summary>
    /// <param name="source">コピー元の count map。</param>
    /// <returns>primary md5 count snapshot。</returns>
    internal static PrimaryHashLookupSnapshot Create(IReadOnlyDictionary<string, int> source)
    {
        if (source is ImmutableDictionary<string, int> immutableSource
            && immutableSource.KeyComparer == StringComparer.OrdinalIgnoreCase)
        {
            return new PrimaryHashLookupSnapshot(immutableSource);
        }

        var builder = EmptyCounts.ToBuilder();
        foreach (KeyValuePair<string, int> item in source ?? new Dictionary<string, int>())
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && item.Value > 0)
            {
                builder[item.Key] = item.Value;
            }
        }
        return new PrimaryHashLookupSnapshot(builder.ToImmutable());
    }

    private readonly ImmutableDictionary<string, int> primaryHashCounts;

    /// <summary>primary md5 count の immutable root view。</summary>
    internal IReadOnlyDictionary<string, int> PrimaryHashCounts => primaryHashCounts;

    public int DistinctPrimaryHashCount => primaryHashCounts.Count;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashCounts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        return excludedCounts == null || excludedCounts.Count == 0
            ? this
            : new ExcludingPrimaryHashLookup(this, excludedCounts);
    }

}

/// <summary>
/// current owned chart の primary md5 count を mutation 同期する軽量 state です。
/// directory / sha256 lookup を含む full installed lookup より先に温められるよう、
/// md5 identity 判定だけを独立して保持します。
/// </summary>
internal sealed class PrimaryHashLookupState : IPrimaryHashLookup
{
    private static readonly ImmutableDictionary<string, int> EmptyCounts =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private ImmutableDictionary<string, int> primaryHashCounts = EmptyCounts;

    private PrimaryHashLookupSnapshot snapshot;

    private bool snapshotDirty = true;

    public int DistinctPrimaryHashCount => primaryHashCounts.Count;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashCounts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    /// <summary>
    /// chart の primary md5 を追加します。
    /// </summary>
    /// <param name="lookupHash">owned chart の md5。</param>
    internal void AddPrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash))
        {
            primaryHashCounts = primaryHashCounts.SetItem(
                lookupHash,
                primaryHashCounts.TryGetValue(lookupHash, out int count) ? count + 1 : 1);
            MarkDirty();
        }
    }

    /// <summary>
    /// chart の primary md5 を削除します。
    /// </summary>
    /// <param name="lookupHash">owned chart の md5。</param>
    internal void RemovePrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash)
            && primaryHashCounts.TryGetValue(lookupHash, out int currentCount))
        {
            primaryHashCounts = currentCount <= 1
                ? primaryHashCounts.Remove(lookupHash)
                : primaryHashCounts.SetItem(lookupHash, currentCount - 1);
            MarkDirty();
        }
    }

    /// <summary>
    /// snapshot を返します。構築済み immutable count root を共有し、directory map は作りません。
    /// </summary>
    /// <returns>primary md5 lookup snapshot。</returns>
    internal PrimaryHashLookupSnapshot CreateSnapshot()
    {
        if (!snapshotDirty && snapshot != null)
        {
            return snapshot;
        }
        snapshot = PrimaryHashLookupSnapshot.Create(primaryHashCounts);
        snapshotDirty = false;
        return snapshot;
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        PrimaryHashLookupSnapshot baseline = CreateSnapshot();
        return excludedCounts == null || excludedCounts.Count == 0
            ? baseline
            : new ExcludingPrimaryHashLookup(baseline, excludedCounts);
    }

    private void MarkDirty()
    {
        snapshotDirty = true;
    }
}

/// <summary>
/// installed chart の digest bucket、primary count、既知 directory を共有 immutable root として公開する snapshot です。
/// </summary>
internal sealed class InstalledChartLookupIndexSnapshot : IInstalledChartLookupIndex
{
    private static readonly ImmutableDictionary<string, IReadOnlyList<string>> EmptyDirectoryMap =
        ImmutableDictionary<string, IReadOnlyList<string>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableSortedSet<string> EmptyDirectorySet =
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, int> EmptyCountMap =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, PrimaryHashEntry> EmptyEntryMap =
        ImmutableDictionary<string, PrimaryHashEntry>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private readonly ImmutableDictionary<string, IReadOnlyList<string>> md5Directories;

    private readonly ImmutableDictionary<string, IReadOnlyList<string>> sha256Directories;

    private readonly ImmutableSortedSet<string> knownChartDirectories;

    private readonly ImmutableDictionary<string, PrimaryHashEntry> primaryHashEntries;

    private readonly ImmutableDictionary<string, int> uniquePrimaryHashCountsByDirectory;

    private readonly int directoryReferenceCount;

    private readonly InstalledLookupMapView<IReadOnlyList<string>, IReadOnlyList<string>> md5DirectoriesView;

    private readonly InstalledLookupMapView<IReadOnlyList<string>, IReadOnlyList<string>> sha256DirectoriesView;

    private readonly InstalledLookupMapView<PrimaryHashEntry, int> primaryHashCountsView;

    private readonly InstalledLookupMapView<int, int> uniquePrimaryHashCountsByDirectoryView;

    public InstalledChartLookupIndexSnapshot()
        : this(
            EmptyDirectoryMap,
            EmptyDirectoryMap,
            EmptyDirectorySet,
            EmptyEntryMap,
            EmptyCountMap,
            directoryReferenceCount: 0,
            storeWorkObserver: null)
    {
    }

    private InstalledChartLookupIndexSnapshot(
        ImmutableDictionary<string, IReadOnlyList<string>> md5Directories,
        ImmutableDictionary<string, IReadOnlyList<string>> sha256Directories,
        ImmutableSortedSet<string> knownChartDirectories,
        ImmutableDictionary<string, PrimaryHashEntry> primaryHashEntries,
        ImmutableDictionary<string, int> uniquePrimaryHashCountsByDirectory,
        int directoryReferenceCount,
        Action<string> storeWorkObserver)
    {
        this.md5Directories = md5Directories ?? EmptyDirectoryMap;
        this.sha256Directories = sha256Directories ?? EmptyDirectoryMap;
        this.knownChartDirectories = knownChartDirectories ?? EmptyDirectorySet;
        this.primaryHashEntries = primaryHashEntries ?? EmptyEntryMap;
        this.uniquePrimaryHashCountsByDirectory = uniquePrimaryHashCountsByDirectory ?? EmptyCountMap;
        this.directoryReferenceCount = directoryReferenceCount;
        StoreWorkObserver = storeWorkObserver;
        md5DirectoriesView = new(this.md5Directories, stored => stored, () => StoreWorkObserver);
        sha256DirectoriesView = new(this.sha256Directories, stored => stored, () => StoreWorkObserver);
        primaryHashCountsView = new(this.primaryHashEntries, entry => entry.Count, () => StoreWorkObserver);
        uniquePrimaryHashCountsByDirectoryView = new(this.uniquePrimaryHashCountsByDirectory, stored => stored, () => StoreWorkObserver);
    }

    /// <summary>
    /// 差分更新済みの immutable root から installed lookup snapshot を作成します。
    /// root は state が更新時に差し替えるため、snapshot 作成時に全 map を複製しません。
    /// </summary>
    /// <param name="md5Directories">MD5 ごとのソート済み directory bucket。</param>
    /// <param name="sha256Directories">SHA-256 ごとのソート済み directory bucket。</param>
    /// <param name="knownChartDirectories">譜面を含む既知 directory の root。</param>
    /// <param name="primaryHashEntries">primary hash ごとの所持数と所持譜面パスを一つの値にまとめた root。</param>
    /// <param name="uniquePrimaryHashCountsByDirectory">directory ごとの distinct primary hash 数 root。</param>
    /// <param name="directoryReferenceCount">MD5/SHA bucket の distinct directory 参照数。</param>
    /// <param name="storeWorkObserver">実際の差分 store 処理を任意に観測する内部 callback。</param>
    /// <returns>指定された root を保持する immutable snapshot。</returns>
    internal static InstalledChartLookupIndexSnapshot Create(
        ImmutableDictionary<string, IReadOnlyList<string>> md5Directories,
        ImmutableDictionary<string, IReadOnlyList<string>> sha256Directories,
        ImmutableSortedSet<string> knownChartDirectories,
        ImmutableDictionary<string, PrimaryHashEntry> primaryHashEntries,
        ImmutableDictionary<string, int> uniquePrimaryHashCountsByDirectory,
        int directoryReferenceCount,
        Action<string> storeWorkObserver = null)
    {
        return new InstalledChartLookupIndexSnapshot(
            md5Directories,
            sha256Directories,
            knownChartDirectories,
            primaryHashEntries,
            uniquePrimaryHashCountsByDirectory,
            directoryReferenceCount,
            storeWorkObserver);
    }

    /// <summary>MD5 hash ごとのソート済み installed directory bucket を返します。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Md5Directories => md5DirectoriesView;

    /// <summary>SHA-256 hash ごとのソート済み installed directory bucket を返します。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sha256Directories => sha256DirectoriesView;

    /// <summary>MD5/SHA の distinct hash bucket 数を返します。</summary>
    public int HashCount => md5Directories.Count + sha256Directories.Count;

    /// <summary>
    /// MD5/SHA bucket に属する distinct directory 参照数を返します。
    /// 保存済み scalar を読むだけで、全 bucket を再集計しません。
    /// </summary>
    public int DirectoryReferenceCount
    {
        get
        {
            StoreWorkObserver?.Invoke("installed_directory_reference_count_read");
            return directoryReferenceCount;
        }
    }

    /// <summary>
    /// immutable store の実処理を任意に記録する内部観測口です。
    /// production では未設定のまま使用します。設定時も callback は state へ再入せず、待機や例外送出をしません。
    /// </summary>
    internal Action<string> StoreWorkObserver { get; set; }

    /// <summary>installed chart が存在する既知 directory の read-only collection を返します。</summary>
    public IReadOnlyCollection<string> KnownChartDirectories => knownChartDirectories;

    /// <summary>primary hash ごとの installed chart 所持数を返します。</summary>
    public IReadOnlyDictionary<string, int> PrimaryHashCounts => primaryHashCountsView;

    /// <summary>directory ごとの distinct primary hash 数を返します。</summary>
    public IReadOnlyDictionary<string, int> UniquePrimaryHashCountsByDirectory => uniquePrimaryHashCountsByDirectoryView;

    /// <summary>distinct primary hash 数を返します。</summary>
    public int DistinctPrimaryHashCount => primaryHashEntries.Count;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public bool ContainsPrimaryHashAfterExcluding(string lookupHash, IReadOnlyDictionary<string, int> excludedCounts)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return false;
        }
        int count = GetPrimaryHashCount(lookupHash);
        int excludedCount = excludedCounts != null && excludedCounts.TryGetValue(lookupHash, out int value) ? value : 0;
        return count - excludedCount > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashEntries.TryGetValue(lookupHash, out PrimaryHashEntry entry)
            ? entry.Count
            : 0;
    }

    /// <summary>primary hash に対応するソート済み directory bucket を返します。</summary>
    /// <param name="lookupHash">検索する primary hash。</param>
    /// <returns>primary hash を含む directory の read-only list。</returns>
    public IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        if (md5DirectoriesView.TryGetValue(lookupHash, out IReadOnlyList<string> md5DirectoryList) && md5DirectoryList != null)
        {
            return md5DirectoryList;
        }
        return [];
    }

    /// <summary>primary hash に対応する所持譜面のパスを、大文字小文字を無視した昇順で返します。</summary>
    /// <param name="lookupHash">検索する primary hash。</param>
    /// <returns>所持譜面のパス。該当がなければ空です。</returns>
    public IReadOnlyList<string> GetPathsByPrimaryHash(string lookupHash)
    {
        return PrimaryHashEntry.ToSortedPaths(primaryHashEntries, lookupHash);
    }

    /// <summary>指定 directory に属する distinct primary hash 数を返します。</summary>
    /// <param name="directoryPath">照会する directory path。</param>
    /// <returns>directory に属する distinct primary hash 数。</returns>
    public int GetUniquePrimaryHashCountByDirectory(string directoryPath)
    {
        return !string.IsNullOrWhiteSpace(directoryPath)
            && uniquePrimaryHashCountsByDirectory.TryGetValue(directoryPath, out int count)
            ? count
            : 0;
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        return excludedCounts == null || excludedCounts.Count == 0
            ? this
            : new ExcludingPrimaryHashLookup(this, excludedCounts);
    }

    /// <summary>
    /// immutable map root の read-only view です。列挙時だけ任意の内部観測 callback を呼び、
    /// snapshot 自体の root は複製しません。
    /// </summary>
    private sealed class InstalledLookupMapView<TStored, TValue> : IReadOnlyDictionary<string, TValue>
    {
        private readonly ImmutableDictionary<string, TStored> root;

        private readonly Func<TStored, TValue> project;

        private readonly Func<Action<string>> observerProvider;

        internal InstalledLookupMapView(
            ImmutableDictionary<string, TStored> root,
            Func<TStored, TValue> project,
            Func<Action<string>> observerProvider)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.project = project ?? throw new ArgumentNullException(nameof(project));
            this.observerProvider = observerProvider ?? throw new ArgumentNullException(nameof(observerProvider));
        }

        public int Count => root.Count;

        public IEnumerable<string> Keys => EnumerateKeys();

        public IEnumerable<TValue> Values => EnumerateValues();

        public TValue this[string key]
        {
            get
            {
                Observe("installed_root_map_lookup");
                return project(root[key]);
            }
        }

        public bool ContainsKey(string key)
        {
            Observe("installed_root_map_lookup");
            return root.ContainsKey(key);
        }

        public bool TryGetValue(string key, out TValue value)
        {
            Observe("installed_root_map_lookup");
            bool found = root.TryGetValue(key, out TStored stored);
            value = found ? project(stored) : default;
            return found;
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            Observe("installed_root_map_enumeration");
            foreach (KeyValuePair<string, TStored> item in root)
            {
                Observe("installed_root_map_key_visited");
                yield return new KeyValuePair<string, TValue>(item.Key, project(item.Value));
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private IEnumerable<string> EnumerateKeys()
        {
            foreach (KeyValuePair<string, TValue> item in this)
            {
                yield return item.Key;
            }
        }

        private IEnumerable<TValue> EnumerateValues()
        {
            foreach (KeyValuePair<string, TValue> item in this)
            {
                yield return item.Value;
            }
        }

        private void Observe(string operation)
        {
            Action<string> observer = observerProvider();
            observer?.Invoke(operation);
        }
    }

}

/// <summary>
/// installed chart の owner count と差分更新済み immutable root を保持する full lookup state です。
/// </summary>
internal sealed class InstalledChartLookupIndexState : IPrimaryHashLookup
{
    private static readonly ImmutableDictionary<string, IReadOnlyList<string>> EmptyDirectoryMap =
        ImmutableDictionary<string, IReadOnlyList<string>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableSortedSet<string> EmptyDirectorySet =
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, int> EmptyCountMap =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> md5DirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> sha256DirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> knownChartDirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    // 所持数と所持譜面パスを一つの値へまとめ、譜面ごとの変更不能な辞書への挿入回数を増やしません。
    // 公開 snapshot が root を共有できるよう immutable で持ちます。同じパスが重なる場合は重なった数だけ並べます。
    private ImmutableDictionary<string, PrimaryHashEntry> primaryHashEntries =
        ImmutableDictionary<string, PrimaryHashEntry>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> directoryPrimaryHashCounts = new(StringComparer.OrdinalIgnoreCase);

    private ImmutableDictionary<string, int> uniquePrimaryHashCountsByDirectory = EmptyCountMap;

    // 可変の owner-count 表とは分離し、公開 snapshot が保持する root を更新ごとに差し替えます。
    private ImmutableDictionary<string, IReadOnlyList<string>> md5DirectoryLists = EmptyDirectoryMap;

    private ImmutableDictionary<string, IReadOnlyList<string>> sha256DirectoryLists = EmptyDirectoryMap;

    private ImmutableSortedSet<string> knownChartDirectories = EmptyDirectorySet;

    private InstalledChartLookupIndexSnapshot snapshot;

    private bool snapshotDirty = true;

    private int directoryReferenceCount;

    /// <summary>
    /// installed lookup の差分更新・snapshot 取得で実際に行った store 処理を任意に記録する内部観測口です。
    /// production では未設定のまま使用します。設定時も callback は state へ再入せず、待機や例外送出をしません。
    /// </summary>
    internal Action<string> StoreWorkObserver { get; set; }

    public int DistinctPrimaryHashCount => primaryHashEntries.Count;

    internal int HashCount => md5DirectoryCounts.Count + sha256DirectoryCounts.Count;

    internal int DirectoryReferenceCount => directoryReferenceCount;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashEntries.TryGetValue(lookupHash, out PrimaryHashEntry entry)
            ? entry.Count
            : 0;
    }

    internal IReadOnlyList<string> GetPathsByPrimaryHash(string lookupHash)
    {
        return PrimaryHashEntry.ToSortedPaths(primaryHashEntries, lookupHash);
    }

    /// <summary>
    /// primary hash のソート済み directory bucket を返します。bucket の再構成は membership 変更時だけ行います。
    /// </summary>
    /// <param name="lookupHash">検索する primary hash。</param>
    /// <returns>primary hash を含む directory の read-only list。</returns>
    internal IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        if (md5DirectoryLists.TryGetValue(lookupHash, out IReadOnlyList<string> md5DirectoryList) && md5DirectoryList != null)
        {
            return md5DirectoryList;
        }
        return [];
    }

    /// <summary>
    /// 既知 chart directory の immutable root を返します。全件列挙は呼び出し側が必要な場合だけ行います。
    /// </summary>
    /// <returns>既知 chart directory の read-only collection。</returns>
    internal IReadOnlyCollection<string> CreateKnownChartDirectorySnapshot()
    {
        return knownChartDirectories;
    }

    internal void AddChart(string path, string md5, string sha256)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        string directory = GetDirectory(path);
        string primaryHash = GetPrimaryHash(md5);
        AddKnownDirectory(directory);
        AddDirectoryHash(md5DirectoryCounts, md5, directory);
        AddDirectoryHash(sha256DirectoryCounts, sha256, directory);
        AddPrimaryHash(primaryHash, path);
        AddDirectoryPrimaryHash(directory, primaryHash);
    }

    internal void RemoveChart(string path, string md5, string sha256)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        string directory = GetDirectory(path);
        string primaryHash = GetPrimaryHash(md5);
        RemoveKnownDirectory(directory);
        RemoveDirectoryHash(md5DirectoryCounts, md5, directory);
        RemoveDirectoryHash(sha256DirectoryCounts, sha256, directory);
        RemovePrimaryHash(primaryHash, path);
        RemoveDirectoryPrimaryHash(directory, primaryHash);
    }

    internal void MoveChart(string oldPath, string newPath, string md5, string sha256)
    {
        RemoveChart(oldPath, md5, sha256);
        AddChart(newPath, md5, sha256);
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        InstalledChartLookupIndexSnapshot baseline = CreateSnapshot();
        return excludedCounts == null || excludedCounts.Count == 0
            ? baseline
            : new ExcludingPrimaryHashLookup(baseline, excludedCounts);
    }

    /// <summary>
    /// 更新済み immutable root を保持する installed lookup snapshot を返します。
    /// dirty でない場合は既存 snapshot を再利用し、dirty 時も root 全件を複製しません。
    /// </summary>
    /// <returns>現在の installed lookup snapshot。</returns>
    internal InstalledChartLookupIndexSnapshot CreateSnapshot()
    {
        if (!snapshotDirty && snapshot != null)
        {
            return snapshot;
        }
        StoreWorkObserver?.Invoke("installed_snapshot_root_capture");
        snapshot = InstalledChartLookupIndexSnapshot.Create(
            md5DirectoryLists,
            sha256DirectoryLists,
            knownChartDirectories,
            primaryHashEntries,
            uniquePrimaryHashCountsByDirectory,
            directoryReferenceCount,
            StoreWorkObserver);
        snapshotDirty = false;
        return snapshot;
    }

    private void AddKnownDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            bool added = !knownChartDirectoryCounts.ContainsKey(directory);
            Increment(knownChartDirectoryCounts, directory);
            if (added)
            {
                knownChartDirectories = knownChartDirectories.Add(directory);
                StoreWorkObserver?.Invoke("installed_known_directory_update");
            }
            MarkDirty();
        }
    }

    private void RemoveKnownDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Decrement(knownChartDirectoryCounts, directory))
        {
            if (!knownChartDirectoryCounts.ContainsKey(directory))
            {
                knownChartDirectories = knownChartDirectories.Remove(directory);
                StoreWorkObserver?.Invoke("installed_known_directory_update");
            }
            MarkDirty();
        }
    }

    private void AddDirectoryHash(Dictionary<string, Dictionary<string, int>> directoryCountsByHash, string hash, string directory)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (!directoryCountsByHash.TryGetValue(hash, out Dictionary<string, int> directoryCounts))
        {
            directoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            directoryCountsByHash[hash] = directoryCounts;
        }
        bool added = !directoryCounts.ContainsKey(directory);
        if (added)
        {
            directoryReferenceCount++;
            if (ReferenceEquals(directoryCountsByHash, md5DirectoryCounts))
            {
                md5DirectoryLists = AddDirectoryToSortedBuckets(md5DirectoryLists, hash, directory, StoreWorkObserver);
            }
            else
            {
                sha256DirectoryLists = AddDirectoryToSortedBuckets(sha256DirectoryLists, hash, directory, StoreWorkObserver);
            }
        }
        Increment(directoryCounts, directory);
        MarkDirty();
    }

    private void RemoveDirectoryHash(Dictionary<string, Dictionary<string, int>> directoryCountsByHash, string hash, string directory)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (directoryCountsByHash.TryGetValue(hash, out Dictionary<string, int> directoryCounts)
            && Decrement(directoryCounts, directory))
        {
            bool removed = !directoryCounts.ContainsKey(directory);
            if (removed)
            {
                directoryReferenceCount--;
                if (ReferenceEquals(directoryCountsByHash, md5DirectoryCounts))
                {
                    md5DirectoryLists = RemoveDirectoryFromSortedBuckets(md5DirectoryLists, hash, directory, StoreWorkObserver);
                }
                else
                {
                    sha256DirectoryLists = RemoveDirectoryFromSortedBuckets(sha256DirectoryLists, hash, directory, StoreWorkObserver);
                }
            }
            if (directoryCounts.Count == 0)
            {
                directoryCountsByHash.Remove(hash);
            }
            MarkDirty();
        }
    }

    private void AddPrimaryHash(string lookupHash, string path)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return;
        }
        bool hasPath = !string.IsNullOrWhiteSpace(path);
        primaryHashEntries = primaryHashEntries.SetItem(
            lookupHash,
            primaryHashEntries.TryGetValue(lookupHash, out PrimaryHashEntry current)
                ? new PrimaryHashEntry(current.Count + 1, hasPath ? current.Paths.Add(path) : current.Paths)
                : new PrimaryHashEntry(1, hasPath ? ImmutableArray.Create(path) : ImmutableArray<string>.Empty));
        StoreWorkObserver?.Invoke("installed_primary_hash_count_update");
        MarkDirty();
    }

    private void RemovePrimaryHash(string lookupHash, string path)
    {
        if (string.IsNullOrWhiteSpace(lookupHash)
            || !primaryHashEntries.TryGetValue(lookupHash, out PrimaryHashEntry current))
        {
            return;
        }
        ImmutableArray<string> paths = current.Paths;
        if (!string.IsNullOrWhiteSpace(path))
        {
            int index = paths.IndexOf(path, 0, StringComparer.OrdinalIgnoreCase);
            if (index >= 0)
            {
                paths = paths.RemoveAt(index);
            }
        }
        primaryHashEntries = current.Count <= 1
            ? primaryHashEntries.Remove(lookupHash)
            : primaryHashEntries.SetItem(lookupHash, new PrimaryHashEntry(current.Count - 1, paths));
        StoreWorkObserver?.Invoke("installed_primary_hash_count_update");
        MarkDirty();
    }

    private void AddDirectoryPrimaryHash(string directory, string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(lookupHash))
        {
            return;
        }
        if (!directoryPrimaryHashCounts.TryGetValue(directory, out Dictionary<string, int> hashCounts))
        {
            hashCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            directoryPrimaryHashCounts[directory] = hashCounts;
        }
        bool added = !hashCounts.ContainsKey(lookupHash);
        if (added)
        {
            int count = uniquePrimaryHashCountsByDirectory.TryGetValue(directory, out int currentCount)
                ? currentCount + 1
                : 1;
            uniquePrimaryHashCountsByDirectory = uniquePrimaryHashCountsByDirectory.SetItem(directory, count);
            StoreWorkObserver?.Invoke("installed_unique_hash_count_update");
        }
        Increment(hashCounts, lookupHash);
        MarkDirty();
    }

    private void RemoveDirectoryPrimaryHash(string directory, string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(lookupHash))
        {
            return;
        }
        if (directoryPrimaryHashCounts.TryGetValue(directory, out Dictionary<string, int> hashCounts)
            && Decrement(hashCounts, lookupHash))
        {
            bool removed = !hashCounts.ContainsKey(lookupHash);
            if (removed)
            {
                if (uniquePrimaryHashCountsByDirectory.TryGetValue(directory, out int currentCount))
                {
                    uniquePrimaryHashCountsByDirectory = currentCount <= 1
                        ? uniquePrimaryHashCountsByDirectory.Remove(directory)
                        : uniquePrimaryHashCountsByDirectory.SetItem(directory, currentCount - 1);
                }
                StoreWorkObserver?.Invoke("installed_unique_hash_count_update");
            }
            if (hashCounts.Count == 0)
            {
                directoryPrimaryHashCounts.Remove(directory);
            }
            MarkDirty();
        }
    }

    private static ImmutableDictionary<string, IReadOnlyList<string>> AddDirectoryToSortedBuckets(
        ImmutableDictionary<string, IReadOnlyList<string>> buckets,
        string hash,
        string directory,
        Action<string> storeWorkObserver)
    {
        if (!buckets.TryGetValue(hash, out IReadOnlyList<string> existing) || existing == null || existing.Count == 0)
        {
            storeWorkObserver?.Invoke("installed_directory_bucket_update");
            return buckets.SetItem(hash, ImmutableArray.Create<string>(directory));
        }

        int insertIndex = FindDirectoryIndex(existing, directory);
        if (insertIndex >= 0)
        {
            return buckets;
        }

        insertIndex = ~insertIndex;
        ImmutableArray<string>.Builder next = ImmutableArray.CreateBuilder<string>(existing.Count + 1);
        for (int index = 0; index < insertIndex; index++)
        {
            next.Add(existing[index]);
            storeWorkObserver?.Invoke("installed_directory_bucket_entry_copied");
        }
        next.Add(directory);
        for (int index = insertIndex; index < existing.Count; index++)
        {
            next.Add(existing[index]);
            storeWorkObserver?.Invoke("installed_directory_bucket_entry_copied");
        }
        storeWorkObserver?.Invoke("installed_directory_bucket_update");
        return buckets.SetItem(hash, next.MoveToImmutable());
    }

    private static ImmutableDictionary<string, IReadOnlyList<string>> RemoveDirectoryFromSortedBuckets(
        ImmutableDictionary<string, IReadOnlyList<string>> buckets,
        string hash,
        string directory,
        Action<string> storeWorkObserver)
    {
        if (!buckets.TryGetValue(hash, out IReadOnlyList<string> existing) || existing == null)
        {
            return buckets;
        }

        int removeIndex = FindDirectoryIndex(existing, directory);
        if (removeIndex < 0)
        {
            return buckets;
        }
        if (existing.Count == 1)
        {
            storeWorkObserver?.Invoke("installed_directory_bucket_update");
            return buckets.Remove(hash);
        }

        ImmutableArray<string>.Builder next = ImmutableArray.CreateBuilder<string>(existing.Count - 1);
        for (int index = 0; index < existing.Count; index++)
        {
            if (index != removeIndex)
            {
                next.Add(existing[index]);
                storeWorkObserver?.Invoke("installed_directory_bucket_entry_copied");
            }
        }
        storeWorkObserver?.Invoke("installed_directory_bucket_update");
        return buckets.SetItem(hash, next.MoveToImmutable());
    }

    private static int FindDirectoryIndex(IReadOnlyList<string> directories, string directory)
    {
        int low = 0;
        int high = directories.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(directories[middle], directory);
            if (comparison == 0)
            {
                return middle;
            }
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return ~low;
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static bool Decrement(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out int count))
        {
            return false;
        }
        if (count <= 1)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count - 1;
        }
        return true;
    }

    private static string GetPrimaryHash(string md5)
    {
        return string.IsNullOrWhiteSpace(md5) ? null : md5;
    }

    private static string GetDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return DirectoryExt.GetDirectoryNameSimple(path);
        }
        catch
        {
            return null;
        }
    }

    private void MarkDirty()
    {
        snapshotDirty = true;
    }
}

/// <summary>
/// primary hash ごとの所持数と所持譜面パスを一つにまとめた値です。
/// 一つの不変辞書へ一回で挿入できるため、所持索引の構築時間は件数だけを持つ場合と同じ挿入回数に保たれます。
/// </summary>
/// <param name="Count">所持数。</param>
/// <param name="Paths">所持譜面のパス（重なる場合は重なった数だけ）。パスが空の所持は数にだけ入ります。</param>
internal readonly record struct PrimaryHashEntry(int Count, ImmutableArray<string> Paths)
{
    /// <summary>主ハッシュの所持譜面のパスを、大文字小文字を無視した昇順の一覧で返します。</summary>
    internal static IReadOnlyList<string> ToSortedPaths(
        ImmutableDictionary<string, PrimaryHashEntry> root,
        string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && root.TryGetValue(lookupHash, out PrimaryHashEntry entry)
            ? [.. entry.Paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
            : [];
    }
}

internal sealed class PrimaryHashSetLookup : IMutablePrimaryHashLookup
{
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public PrimaryHashSetLookup()
    {
    }

    public PrimaryHashSetLookup(IEnumerable<string> hashes)
    {
        foreach (string hash in hashes ?? [])
        {
            AddPrimaryHash(hash);
        }
    }

    public int DistinctPrimaryHashCount => counts.Count;

    internal IEnumerable<string> PrimaryHashes => counts.Keys;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && counts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    public void AddPrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash))
        {
            counts[lookupHash] = counts.TryGetValue(lookupHash, out int count) ? count + 1 : 1;
        }
    }
}

internal sealed class PrimaryHashGuardLookup : IMutablePrimaryHashLookup
{
    private readonly IPrimaryHashLookup baseline;

    private readonly PrimaryHashSetLookup additions = new();

    public PrimaryHashGuardLookup(IPrimaryHashLookup baseline)
    {
        this.baseline = baseline ?? EmptyPrimaryHashLookup.Instance;
    }

    public int DistinctPrimaryHashCount => baseline.DistinctPrimaryHashCount
        + additions.PrimaryHashes.Count(hash => baseline.GetPrimaryHashCount(hash) == 0);

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return baseline.ContainsPrimaryHash(lookupHash) || additions.ContainsPrimaryHash(lookupHash);
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return baseline.GetPrimaryHashCount(lookupHash) + additions.GetPrimaryHashCount(lookupHash);
    }

    public void AddPrimaryHash(string lookupHash)
    {
        additions.AddPrimaryHash(lookupHash);
    }
}

internal sealed class ExcludingPrimaryHashLookup : IPrimaryHashLookup
{
    private readonly IPrimaryHashLookup source;

    private readonly IReadOnlyDictionary<string, int> excludedCounts;

    private readonly int distinctPrimaryHashCount;

    public ExcludingPrimaryHashLookup(IPrimaryHashLookup source, IReadOnlyDictionary<string, int> excludedCounts)
    {
        this.source = source ?? EmptyPrimaryHashLookup.Instance;
        this.excludedCounts = excludedCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        distinctPrimaryHashCount = this.source.DistinctPrimaryHashCount
            - this.excludedCounts.Count(delegate (KeyValuePair<string, int> excluded)
            {
                int sourceCount = this.source.GetPrimaryHashCount(excluded.Key);
                return sourceCount > 0 && sourceCount - excluded.Value <= 0;
            });
    }

    public int DistinctPrimaryHashCount => Math.Max(0, distinctPrimaryHashCount);

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return 0;
        }
        int excludedCount = excludedCounts.TryGetValue(lookupHash, out int value) ? value : 0;
        return Math.Max(0, source.GetPrimaryHashCount(lookupHash) - excludedCount);
    }
}

internal sealed class EmptyPrimaryHashLookup : IPrimaryHashLookup
{
    public static EmptyPrimaryHashLookup Instance { get; } = new();

    private EmptyPrimaryHashLookup()
    {
    }

    public int DistinctPrimaryHashCount => 0;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return false;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return 0;
    }
}
