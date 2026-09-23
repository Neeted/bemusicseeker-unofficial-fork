using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistEntriesHydrationLoadResult
{
    public string Projection { get; set; } = "startup_entries";

    public List<BMSTableEntry> Entries { get; } = [];

    public int RowCount => Entries.Count;

    public long DbReadMs { get; set; }

    public long MaterializeMs { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
