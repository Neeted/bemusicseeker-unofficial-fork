using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>初回・実全置換だけで、表reader内のheaderとentry keyを正本表順とともに捕捉します。</summary>
    private List<PlaylistReferenceTableSnapshot> SnapshotPlaylistReferenceTables(IEnumerable<BMSTable> tables)
    {
        var result = new List<PlaylistReferenceTableSnapshot>();
        foreach (BMSTable table in (tables ?? []).Where(table => table != null))
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded) { throw new System.InvalidOperationException("Playlist entries are not loaded. table=" + table.name); }
                result.Add(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries, result.Count, PlaylistReferenceWorkObserver));
            }
        }
        return result;
    }
}
