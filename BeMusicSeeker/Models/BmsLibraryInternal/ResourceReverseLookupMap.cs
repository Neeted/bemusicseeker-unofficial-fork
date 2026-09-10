using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Shares an owned scan's reverse lookup and path-copies only subsequent key changes.
/// </summary>
/// <remarks>
/// The base and candidate arrays are immutable by ownership. A fork has its own builder over
/// shared immutable change nodes, not a link to the preceding generation. Neither a fork nor
/// its first write enumerates the base. Callers serialize access to each map with the cache lock.
/// </remarks>
internal sealed class ResourceReverseLookupMap
{
    private readonly IReadOnlyDictionary<uint, string[]> baseline;
    private readonly ImmutableDictionary<uint, string[]>.Builder changes;

    /// <summary>
    /// Transfers ownership of a complete base without enumerating or copying it.
    /// The caller must not subsequently modify the dictionary or its candidate arrays.
    /// </summary>
    internal ResourceReverseLookupMap(IReadOnlyDictionary<uint, string[]> baseline = null)
        : this(baseline ?? new Dictionary<uint, string[]>(),
            ImmutableDictionary<uint, string[]>.Empty.ToBuilder(), baseline?.Count ?? 0)
    {
    }

    private ResourceReverseLookupMap(
        IReadOnlyDictionary<uint, string[]> baseline,
        ImmutableDictionary<uint, string[]>.Builder changes,
        int count)
    {
        this.baseline = baseline;
        this.changes = changes;
        Count = count;
    }

    /// <summary>Gets the number of cached keys, including cached empty candidate lists.</summary>
    internal int Count { get; private set; }

    /// <summary>
    /// Forks the latest changes without copying the complete base or retaining a generation chain.
    /// Freezing a builder visits only its still-mutable change nodes; already frozen nodes are shared.
    /// </summary>
    internal ResourceReverseLookupMap Fork()
    {
        return new ResourceReverseLookupMap(baseline, changes.ToImmutable().ToBuilder(), Count);
    }

    /// <summary>
    /// Seals computed change nodes at publication, rather than charging a later mutation for
    /// freezing a preceding lazy-cache fill. This neither copies nor enumerates the scan base.
    /// </summary>
    internal void FreezeChanges()
    {
        changes.ToImmutable();
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

        changes[hash] = directories;
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
