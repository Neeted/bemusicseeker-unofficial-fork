using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Shares an owned scan's reverse lookup and path-copies only subsequent key changes.
/// </summary>
/// <remarks>
/// baseと候補配列は所有権によりimmutableである。forkは直前世代へのlinkではなく、immutableな
/// change rootを共有する。forkと最初の書き込みはいずれもbaseを列挙しない。呼び出し側はcache
/// lockで各mapへのアクセスを直列化する。
/// </remarks>
internal sealed class ResourceReverseLookupMap
{
    private readonly IReadOnlyDictionary<uint, string[]> baseline;
    private ImmutableDictionary<uint, string[]> changes;

    /// <summary>
    /// Transfers ownership of a complete base without enumerating or copying it.
    /// The caller must not subsequently modify the dictionary or its candidate arrays.
    /// </summary>
    internal ResourceReverseLookupMap(IReadOnlyDictionary<uint, string[]> baseline = null)
        : this(baseline ?? new Dictionary<uint, string[]>(),
            ImmutableDictionary<uint, string[]>.Empty,
            baseline?.Count ?? 0)
    {
    }

    private ResourceReverseLookupMap(
        IReadOnlyDictionary<uint, string[]> baseline,
        ImmutableDictionary<uint, string[]> changes,
        int count)
    {
        this.baseline = baseline;
        this.changes = changes;
        Count = count;
    }

    /// <summary>Gets the number of cached keys, including cached empty candidate lists.</summary>
    internal int Count { get; private set; }

    /// <summary>
    /// 完全なbaseのcopyや世代chainの保持を行わず、最新のchangeをforkする。
    /// immutableなchange rootと所有されたbaseは参照共有する。
    /// </summary>
    internal ResourceReverseLookupMap Fork()
    {
        return new ResourceReverseLookupMap(baseline, changes, Count);
    }

    /// <summary>Looks up a key in the latest changes, then the shared scan base.</summary>
    internal bool TryGetValue(uint hash, out string[] directories)
    {
        return changes.TryGetValue(hash, out directories) || baseline.TryGetValue(hash, out directories);
    }

    /// <summary>Distinguishes a cached empty result from a key not yet cached.</summary>
    internal bool ContainsKey(uint hash)
    {
        return changes.ContainsKey(hash) || baseline.ContainsKey(hash);
    }

    /// <summary>
    /// Replaces one candidate list only when its final contents differ. The provided array is
    /// transferred to the map and must not be modified afterwards. Empty lists remain cached keys.
    /// </summary>
    internal bool Set(uint hash, string[] directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        bool existed = TryGetValue(hash, out string[] previous);
        if (existed && (ReferenceEquals(previous, directories)
            || (previous != null && previous.SequenceEqual(directories, StringComparer.Ordinal))))
        {
            return false;
        }

        changes = changes.SetItem(hash, directories);
        if (!existed)
        {
            Count++;
        }
        return true;
    }

    /// <summary>
    /// Enumerates each current key once for explicit whole-map operations such as folder rewrites.
    /// Incremental add/remove and fork do not use this enumeration.
    /// </summary>
    internal IEnumerable<uint> Keys
    {
        get
        {
            foreach (uint hash in baseline.Keys)
            {
                yield return hash;
            }
            foreach (uint hash in changes.Keys)
            {
                if (!baseline.ContainsKey(hash))
                {
                    yield return hash;
                }
            }
        }
    }
}
