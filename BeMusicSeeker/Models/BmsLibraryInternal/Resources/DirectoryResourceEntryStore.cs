using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// immutableなpath/order rootとcommand内の空slotでresource entryを保持する。
/// </summary>
/// <remarks>
/// path mapは直接lookupを提供し、順序付きmapは既存Dictionaryのslot順を保つ。mutation commandは
/// 削除slotをLIFO順に再利用する。forkはimmutable rootだけを共有し、空slot stackを空で開始する。
/// これは旧Dictionaryを新世代へcopyした際のcompactと同じ順序を保つためである。
/// </remarks>
internal sealed class DirectoryResourceEntryStore
{
    private readonly struct StoredEntry(string path, DirectoryResourceLookupCache.Entry entry)
    {
        internal string Path { get; } = path;

        internal DirectoryResourceLookupCache.Entry Entry { get; } = entry;
    }

    private ImmutableDictionary<string, long> pathToOrder;

    private ImmutableSortedDictionary<long, StoredEntry> entriesByOrder;

    private readonly Stack<long> reusableOrders;

    private long nextOrder;

    /// <summary>
    /// immutableな両rootの置換を伴う実entry mutationを観測する。
    /// </summary>
    internal Action<string> MutationObserver { get; set; }

    /// <summary>
    /// 明示的なstore traversalで訪問した各entryを観測する。
    /// </summary>
    internal Action<string> EntryVisitedObserver { get; set; }

    /// <summary>空のentry storeを作成する。</summary>
    internal DirectoryResourceEntryStore()
        : this(
            ImmutableDictionary<string, long>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableSortedDictionary<long, StoredEntry>.Empty,
            nextOrder: 0L,
            mutationObserver: null,
            entryVisitedObserver: null)
    {
    }

    private DirectoryResourceEntryStore(
        ImmutableDictionary<string, long> pathToOrder,
        ImmutableSortedDictionary<long, StoredEntry> entriesByOrder,
        long nextOrder,
        Action<string> mutationObserver,
        Action<string> entryVisitedObserver)
    {
        this.pathToOrder = pathToOrder;
        this.entriesByOrder = entriesByOrder;
        this.nextOrder = nextOrder;
        reusableOrders = new Stack<long>();
        MutationObserver = mutationObserver;
        EntryVisitedObserver = entryVisitedObserver;
    }

    /// <summary>live entry数を取得する。</summary>
    internal int Count => pathToOrder.Count;

    /// <summary>
    /// immutable rootを共有し、旧commandの空slotを破棄した世代local storeを作成する。
    /// </summary>
    internal DirectoryResourceEntryStore Fork()
    {
        // sourceが次に直接書き込む場合も、旧Dictionaryのcopy-on-write後と同じく
        // 既存holeをcompact済みとして扱う。clone側はconstructorで空stackを持つ。
        reusableOrders.Clear();
        return new DirectoryResourceEntryStore(
            pathToOrder,
            entriesByOrder,
            nextOrder,
            MutationObserver,
            EntryVisitedObserver);
    }

    /// <summary>大文字小文字を区別しないpathでentryをlookupする。</summary>
    internal bool TryGetValue(string path, out DirectoryResourceLookupCache.Entry entry)
    {
        if (pathToOrder.TryGetValue(path, out long order)
            && entriesByOrder.TryGetValue(order, out StoredEntry stored))
        {
            entry = stored.Entry;
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// live store全体をcopyせずentryを追加または置換する。既存pathは順序を維持し、新規pathは
    /// 利用可能ならcommand内で直近に削除したslotを再利用する。
    /// </summary>
    /// <param name="path">lookupに使うpath。新規keyでは列挙時にも保持する。</param>
    /// <param name="entry">保持するimmutable entry payload。</param>
    internal void Set(
        string path,
        DirectoryResourceLookupCache.Entry entry)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entry);
        if (pathToOrder.TryGetValue(path, out long existingOrder))
        {
            StoredEntry existing = entriesByOrder[existingOrder];
            entriesByOrder = entriesByOrder.SetItem(
                existingOrder,
                new StoredEntry(existing.Path, entry));
            MutationObserver?.Invoke(existing.Path);
            return;
        }

        long order = reusableOrders.Count > 0
            ? reusableOrders.Pop()
            : nextOrder++;
        pathToOrder = pathToOrder.SetItem(path, order);
        entriesByOrder = entriesByOrder.SetItem(order, new StoredEntry(path, entry));
        MutationObserver?.Invoke(path);
    }

    /// <summary>entryを一つ削除し、このcommandの後続追加用に順序を記録する。</summary>
    internal bool Remove(string path, out DirectoryResourceLookupCache.Entry removedEntry)
    {
        if (!pathToOrder.TryGetValue(path, out long order)
            || !entriesByOrder.TryGetValue(order, out StoredEntry stored))
        {
            removedEntry = null;
            return false;
        }

        pathToOrder = pathToOrder.Remove(path);
        entriesByOrder = entriesByOrder.Remove(order);
        reusableOrders.Push(order);
        removedEntry = stored.Entry;
        MutationObserver?.Invoke(stored.Path);
        return true;
    }

    /// <summary>
    /// live entryを一度ずつ訪問し、predicateを受け入れたpathだけを返す。
    /// </summary>
    internal List<string> CollectPaths(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<string> matched = [];
        foreach (KeyValuePair<long, StoredEntry> pair in entriesByOrder)
        {
            string path = pair.Value.Path;
            EntryVisitedObserver?.Invoke(path);
            if (predicate(path))
            {
                matched.Add(path);
            }
        }
        return matched;
    }

    /// <summary>live pathを安定順で列挙する。</summary>
    internal IEnumerable<string> EnumeratePaths()
    {
        foreach (KeyValuePair<long, StoredEntry> pair in entriesByOrder)
        {
            EntryVisitedObserver?.Invoke(pair.Value.Path);
            yield return pair.Value.Path;
        }
    }

    /// <summary>live entryを安定順で列挙する。</summary>
    internal IEnumerable<KeyValuePair<string, DirectoryResourceLookupCache.Entry>> EnumerateEntries()
    {
        foreach (KeyValuePair<long, StoredEntry> pair in entriesByOrder)
        {
            EntryVisitedObserver?.Invoke(pair.Value.Path);
            yield return new KeyValuePair<string, DirectoryResourceLookupCache.Entry>(
                pair.Value.Path,
                pair.Value.Entry);
        }
    }
}
