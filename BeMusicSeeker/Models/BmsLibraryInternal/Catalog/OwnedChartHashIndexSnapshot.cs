using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// owned chart の hash build 入力を保持する一時 projection です。
/// </summary>
internal sealed class OwnedChartHashIndexSnapshot
{
    private readonly Dictionary<string, int> md5Counts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> sha256Counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>build 中に捕捉した MD5 key の read-only view です。</summary>
    internal IReadOnlyCollection<string> Md5Hashes => md5Counts.Keys;

    /// <summary>build 中に捕捉した SHA-256 key の read-only view です。</summary>
    internal IReadOnlyCollection<string> Sha256Hashes => sha256Counts.Keys;

    /// <summary>build 中に捕捉した MD5 owner count です。</summary>
    internal IReadOnlyDictionary<string, int> Md5Counts => md5Counts;

    /// <summary>build 中に捕捉した SHA-256 owner count です。</summary>
    internal IReadOnlyDictionary<string, int> Sha256Counts => sha256Counts;

    /// <summary>MD5 owner を一件追加します。</summary>
    /// <param name="hash">追加する MD5。</param>
    internal void AddMd5(string hash)
    {
        AddHash(md5Counts, hash);
    }

    /// <summary>SHA-256 owner を一件追加します。</summary>
    /// <param name="hash">追加する SHA-256。</param>
    internal void AddSha256(string hash)
    {
        AddHash(sha256Counts, hash);
    }

    private static void AddHash(
        Dictionary<string, int> counts,
        string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        counts[hash] = counts.TryGetValue(hash, out int count) ? count + 1 : 1;
    }
}

/// <summary>
/// owned chart の MD5/SHA-256 owner count を共有する immutable root です。
/// </summary>
internal sealed class OwnedChartHashIndexRoot
{
    private static readonly ImmutableDictionary<string, int> EmptyCounts =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private readonly ImmutableDictionary<string, int> md5Counts;

    private readonly ImmutableDictionary<string, int> sha256Counts;

    private OwnedChartHashIndexRoot(
        ImmutableDictionary<string, int> md5Counts,
        ImmutableDictionary<string, int> sha256Counts)
    {
        this.md5Counts = md5Counts ?? EmptyCounts;
        this.sha256Counts = sha256Counts ?? EmptyCounts;
    }

    internal static OwnedChartHashIndexRoot Empty { get; } = new(EmptyCounts, EmptyCounts);

    internal ImmutableDictionary<string, int> Md5Counts => md5Counts;

    internal ImmutableDictionary<string, int> Sha256Counts => sha256Counts;

    /// <summary>
    /// 初回 build の結果から immutable count root を作成します。
    /// </summary>
    /// <param name="source">build 入力。</param>
    /// <returns>MD5/SHA-256 count root。</returns>
    internal static OwnedChartHashIndexRoot Create(OwnedChartHashIndexSnapshot source)
    {
        return source == null
            ? Empty
            : new OwnedChartHashIndexRoot(
                CreateCounts(source.Md5Counts),
                CreateCounts(source.Sha256Counts));
    }

    /// <summary>
    /// 旧新 hash facts を hash ごとに集約して count root へ適用します。
    /// </summary>
    /// <param name="deltas">一件ごとの旧新 hash facts。</param>
    /// <param name="storeWorkObserver">実際の key 更新を記録する任意の内部 observer。</param>
    /// <param name="membershipChanged">distinct hash membership が変わったかどうか。</param>
    /// <returns>差分適用後の root。</returns>
    internal OwnedChartHashIndexRoot ApplyDeltas(
        IEnumerable<OwnedChartHashIndexDelta> deltas,
        Action<string> storeWorkObserver,
        out bool membershipChanged)
    {
        var md5Deltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sha256Deltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (OwnedChartHashIndexDelta delta in deltas ?? [])
        {
            AddDelta(md5Deltas, delta.OldMd5, -1);
            AddDelta(md5Deltas, delta.NewMd5, 1);
            AddDelta(sha256Deltas, delta.OldSha256, -1);
            AddDelta(sha256Deltas, delta.NewSha256, 1);
        }

        membershipChanged = false;
        ImmutableDictionary<string, int> nextMd5Counts = ApplyCountDeltas(
            md5Counts,
            md5Deltas,
            storeWorkObserver,
            ref membershipChanged);
        ImmutableDictionary<string, int> nextSha256Counts = ApplyCountDeltas(
            sha256Counts,
            sha256Deltas,
            storeWorkObserver,
            ref membershipChanged);
        return ReferenceEquals(nextMd5Counts, md5Counts) && ReferenceEquals(nextSha256Counts, sha256Counts)
            ? this
            : new OwnedChartHashIndexRoot(nextMd5Counts, nextSha256Counts);
    }

    private static ImmutableDictionary<string, int> CreateCounts(
        IReadOnlyDictionary<string, int> capturedCounts)
    {
        var builder = EmptyCounts.ToBuilder();
        foreach (KeyValuePair<string, int> item in capturedCounts ?? new Dictionary<string, int>())
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && item.Value > 0)
            {
                builder[item.Key] = item.Value;
            }
        }
        return builder.ToImmutable();
    }

    private static void AddDelta(Dictionary<string, int> deltas, string hash, int amount)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        deltas[hash] = deltas.TryGetValue(hash, out int current) ? current + amount : amount;
        if (deltas[hash] == 0)
        {
            deltas.Remove(hash);
        }
    }

    private static ImmutableDictionary<string, int> ApplyCountDeltas(
        ImmutableDictionary<string, int> counts,
        IReadOnlyDictionary<string, int> deltas,
        Action<string> storeWorkObserver,
        ref bool membershipChanged)
    {
        ImmutableDictionary<string, int> next = counts;
        foreach (KeyValuePair<string, int> delta in deltas ?? new Dictionary<string, int>())
        {
            if (delta.Value == 0)
            {
                continue;
            }
            int oldCount = next.TryGetValue(delta.Key, out int current) ? current : 0;
            int nextCount = oldCount + delta.Value;
            if (nextCount < 0)
            {
                throw new InvalidOperationException("Owned chart hash owner count delta underflow.");
            }
            if (nextCount == oldCount)
            {
                continue;
            }
            if ((oldCount == 0) != (nextCount == 0))
            {
                membershipChanged = true;
            }
            next = nextCount == 0
                ? next.Remove(delta.Key)
                : next.SetItem(delta.Key, nextCount);
            storeWorkObserver?.Invoke("owned_hash_count_root_update");
        }
        return next;
    }
}

/// <summary>
/// owned chart hash の旧新 facts を一件分保持する immutable delta です。
/// </summary>
internal readonly struct OwnedChartHashIndexDelta
{
    /// <summary>旧 MD5。</summary>
    internal string OldMd5 { get; }

    /// <summary>旧 SHA-256。</summary>
    internal string OldSha256 { get; }

    /// <summary>新 MD5。</summary>
    internal string NewMd5 { get; }

    /// <summary>新 SHA-256。</summary>
    internal string NewSha256 { get; }

    /// <summary>旧新 hash facts を作成します。</summary>
    internal OwnedChartHashIndexDelta(
        string oldMd5,
        string oldSha256,
        string newMd5,
        string newSha256)
    {
        OldMd5 = oldMd5;
        OldSha256 = oldSha256;
        NewMd5 = newMd5;
        NewSha256 = newSha256;
    }
}

/// <summary>
/// Immutable, versioned catalog hash projection consumed by aggregate summaries.
/// </summary>
internal sealed class OwnedChartHashIndexVersionedSnapshot
{
    private readonly OwnedChartHashIndexRoot root;

    private readonly HashKeyCollection md5HashSnapshot;

    private readonly HashKeyCollection sha256HashSnapshot;

    private readonly HashCountMapView md5CountSnapshot;

    private readonly HashCountMapView sha256CountSnapshot;

    internal OwnedChartHashIndexVersionedSnapshot(
        OwnedChartHashIndexSnapshot source,
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
        : this(
            OwnedChartHashIndexRoot.Create(source),
            version,
            buildElapsedMs,
            invalidationVersion,
            ownedCollectionVersion,
            bmsRowsVersion,
            bmsonRowsVersion,
            null)
    {
    }

    private OwnedChartHashIndexVersionedSnapshot(
        OwnedChartHashIndexRoot root,
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion,
        Action<string> storeWorkObserver)
    {
        this.root = root ?? OwnedChartHashIndexRoot.Empty;
        Version = version;
        BuildElapsedMs = buildElapsedMs;
        InvalidationVersion = invalidationVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
        StoreWorkObserver = storeWorkObserver;
        md5HashSnapshot = new HashKeyCollection(this.root.Md5Counts, () => StoreWorkObserver);
        sha256HashSnapshot = new HashKeyCollection(this.root.Sha256Counts, () => StoreWorkObserver);
        md5CountSnapshot = new HashCountMapView(this.root.Md5Counts, () => StoreWorkObserver);
        sha256CountSnapshot = new HashCountMapView(this.root.Sha256Counts, () => StoreWorkObserver);
    }

    /// <summary>
    /// 既存 immutable root を共有して metadata だけを更新した snapshot を作成します。
    /// </summary>
    /// <param name="root">共有する count root。</param>
    /// <param name="version">distinct membership content version。</param>
    /// <param name="buildElapsedMs">root build の経過時間。</param>
    /// <param name="invalidationVersion">full invalidation version。</param>
    /// <param name="ownedCollectionVersion">owned collection version。</param>
    /// <param name="bmsRowsVersion">BMS storage rows version。</param>
    /// <param name="bmsonRowsVersion">BMSON storage rows version。</param>
    /// <param name="storeWorkObserver">実処理を記録する任意の内部 observer。</param>
    /// <returns>rootを共有する versioned snapshot。</returns>
    internal static OwnedChartHashIndexVersionedSnapshot CreateFromRoot(
        OwnedChartHashIndexRoot root,
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion,
        Action<string> storeWorkObserver = null)
    {
        return new OwnedChartHashIndexVersionedSnapshot(
            root,
            version,
            buildElapsedMs,
            invalidationVersion,
            ownedCollectionVersion,
            bmsRowsVersion,
            bmsonRowsVersion,
            storeWorkObserver);
    }

    /// <summary>distinct hash membershipのcontent version。</summary>
    internal int Version { get; }

    /// <summary>root buildまたはsnapshot再捕捉の経過時間。</summary>
    internal long BuildElapsedMs { get; }

    /// <summary>full invalidationの世代。</summary>
    internal int InvalidationVersion { get; }

    /// <summary>snapshot作成時のowned collection version。</summary>
    internal int OwnedCollectionVersion { get; }

    /// <summary>snapshot作成時のBMS storage rows version。</summary>
    internal int BmsRowsVersion { get; }

    /// <summary>snapshot作成時のBMSON storage rows version。</summary>
    internal int BmsonRowsVersion { get; }

    /// <summary>MD5 membershipのimmutable read-only view。</summary>
    internal IReadOnlyCollection<string> Md5Hashes => md5HashSnapshot;

    /// <summary>SHA-256 membershipのimmutable read-only view。</summary>
    internal IReadOnlyCollection<string> Sha256Hashes => sha256HashSnapshot;

    /// <summary>MD5 hash ごとの owner count root view。</summary>
    internal IReadOnlyDictionary<string, int> Md5Counts => md5CountSnapshot;

    /// <summary>SHA-256 hash ごとの owner count root view。</summary>
    internal IReadOnlyDictionary<string, int> Sha256Counts => sha256CountSnapshot;

    /// <summary>distinct MD5 membership数。</summary>
    internal int Md5Count => root.Md5Counts.Count;

    /// <summary>distinct SHA-256 membership数。</summary>
    internal int Sha256Count => root.Sha256Counts.Count;

    /// <summary>
    /// immutable root の実 read-only view を任意の内部 observer から観測します。
    /// </summary>
    internal Action<string> StoreWorkObserver { get; set; }

    internal bool ContainsMd5(string md5)
    {
        return !string.IsNullOrWhiteSpace(md5) && root.Md5Counts.ContainsKey(md5);
    }

    internal bool ContainsSha256(string sha256)
    {
        return !string.IsNullOrWhiteSpace(sha256) && root.Sha256Counts.ContainsKey(sha256);
    }

    internal int GetMd5OwnerCount(string md5)
    {
        return !string.IsNullOrWhiteSpace(md5) && root.Md5Counts.TryGetValue(md5, out int count) ? count : 0;
    }

    internal int GetSha256OwnerCount(string sha256)
    {
        return !string.IsNullOrWhiteSpace(sha256) && root.Sha256Counts.TryGetValue(sha256, out int count) ? count : 0;
    }

    private sealed class HashKeyCollection : IReadOnlyCollection<string>
    {
        private readonly ImmutableDictionary<string, int> root;

        private readonly Func<Action<string>> observerProvider;

        internal HashKeyCollection(
            ImmutableDictionary<string, int> root,
            Func<Action<string>> observerProvider)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.observerProvider = observerProvider ?? throw new ArgumentNullException(nameof(observerProvider));
        }

        public int Count => root.Count;

        public IEnumerator<string> GetEnumerator()
        {
            Observe("owned_hash_root_enumeration");
            foreach (KeyValuePair<string, int> item in root)
            {
                Observe("owned_hash_root_key_visited");
                yield return item.Key;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void Observe(string operation)
        {
            observerProvider()?.Invoke(operation);
        }
    }

    private sealed class HashCountMapView : IReadOnlyDictionary<string, int>
    {
        private readonly ImmutableDictionary<string, int> root;

        private readonly Func<Action<string>> observerProvider;

        internal HashCountMapView(
            ImmutableDictionary<string, int> root,
            Func<Action<string>> observerProvider)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.observerProvider = observerProvider ?? throw new ArgumentNullException(nameof(observerProvider));
        }

        public int Count => root.Count;

        public IEnumerable<string> Keys => EnumerateKeys();

        public IEnumerable<int> Values => EnumerateValues();

        public int this[string key]
        {
            get
            {
                Observe("owned_hash_root_lookup");
                return root[key];
            }
        }

        public bool ContainsKey(string key)
        {
            Observe("owned_hash_root_lookup");
            return root.ContainsKey(key);
        }

        public bool TryGetValue(string key, out int value)
        {
            Observe("owned_hash_root_lookup");
            return root.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
        {
            Observe("owned_hash_root_enumeration");
            foreach (KeyValuePair<string, int> item in root)
            {
                Observe("owned_hash_root_key_visited");
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<string> EnumerateKeys()
        {
            foreach (KeyValuePair<string, int> item in this)
            {
                yield return item.Key;
            }
        }

        private IEnumerable<int> EnumerateValues()
        {
            foreach (KeyValuePair<string, int> item in this)
            {
                yield return item.Value;
            }
        }

        private void Observe(string operation)
        {
            observerProvider()?.Invoke(operation);
        }
    }
}
