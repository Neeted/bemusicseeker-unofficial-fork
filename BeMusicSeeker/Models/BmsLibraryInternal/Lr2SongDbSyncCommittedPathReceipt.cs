using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Describes the BMS paths whose file-diff work is already committed and may
/// be consumed by exactly one immediate LR2 synchronization request.
/// </summary>
internal sealed class Lr2SongDbSyncCommittedPathReceipt
{
    internal Lr2SongDbSyncCommittedPathReceipt(
        int bmsRowsVersion,
        IEnumerable<string> committedBmsPaths)
    {
        BmsRowsVersion = bmsRowsVersion;
        CommittedBmsPaths = new HashSet<string>(
            (committedBmsPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the BMS storage-row version captured after the file-diff commit.
    /// </summary>
    internal int BmsRowsVersion { get; }

    /// <summary>
    /// Gets the committed BMS paths.  This collection is process-local and is
    /// never persisted or reconstructed from durable state.
    /// </summary>
    internal IReadOnlySet<string> CommittedBmsPaths { get; }

    internal bool Matches(Lr2SongDbSyncInput input)
    {
        // This receipt proves a committed set of BMS storage rows. Keep that
        // narrow guard without coupling the proof to unrelated catalog changes.
        return input != null
            && BmsRowsVersion == input.BmsRowsVersion;
    }
}
