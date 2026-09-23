using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// Materializes playlist entries while holding the table reader lock.
    /// Entry references are preserved so later identity-based mutations remain valid.
    /// </summary>
    internal static IReadOnlyList<BMSTableEntry> SnapshotPlaylistEntriesExceptDummy(BMSTable table)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return [.. table.GetEntriesExceptDummy()];
        }
    }
}
