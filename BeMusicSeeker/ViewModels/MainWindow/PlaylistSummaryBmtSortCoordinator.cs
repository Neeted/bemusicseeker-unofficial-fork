using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistSummaryBmtSortCoordinator
{
    private readonly Func<BMSPlaylist> playlistProvider;
    private readonly Func<IEnumerable<BMSTable>> tableSnapshotProvider;
    private readonly Func<string, bool, bool, long> refreshPlaylistSummary;

    internal PlaylistSummaryBmtSortCoordinator(
        Func<BMSPlaylist> playlistProvider,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Func<string, bool, bool, long> refreshPlaylistSummary)
    {
        this.playlistProvider = playlistProvider ?? throw new ArgumentNullException(nameof(playlistProvider));
        this.tableSnapshotProvider = tableSnapshotProvider ?? throw new ArgumentNullException(nameof(tableSnapshotProvider));
        this.refreshPlaylistSummary = refreshPlaylistSummary ?? throw new ArgumentNullException(nameof(refreshPlaylistSummary));
    }

    internal void ApplyCurrentVisibleOrder(IEnumerable<PlaylistSummaryRow> visibleRows)
    {
        List<BMSTable> orderedTables = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByReplacingVisibleSlots(GetFullOrderSnapshot(), visibleRows);
        PersistOrder(orderedTables, "playlist_summary_apply_current_order_to_bmt_sort");
    }

    internal void MoveRowsToTop(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<BMSTable> orderedTables = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByMovingRows(GetFullOrderSnapshot(), rows, insertAtTop: true);
        PersistOrder(orderedTables, "playlist_summary_move_to_bmt_sort_top");
    }

    internal void MoveRowsToBottom(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<BMSTable> orderedTables = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByMovingRows(GetFullOrderSnapshot(), rows, insertAtTop: false);
        PersistOrder(orderedTables, "playlist_summary_move_to_bmt_sort_bottom");
    }

    internal long DropRows(IEnumerable<PlaylistSummaryRow> visibleRows, IEnumerable<PlaylistSummaryRow> draggedRows, int visibleInsertIndex)
    {
        List<BMSTable> orderedTables = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByVisibleDrop(GetFullOrderSnapshot(), visibleRows, draggedRows, visibleInsertIndex);
        return PersistOrder(orderedTables, "playlist_summary_bmt_sort_drag_drop");
    }

    internal void ApplyImportedTablesToFront(IReadOnlyList<BMSTable> importedTables)
    {
        List<BMSTable> frontTables = [.. (importedTables ?? [])
            .Where(table => table != null)
            .Distinct()];
        if (frontTables.Count == 0)
        {
            return;
        }
        BMSPlaylist playlist = playlistProvider()
            ?? throw new InvalidOperationException("A playlist store is required to apply imported BMT sort order.");
        var frontSet = new HashSet<BMSTable>(frontTables);
        List<BMSTable> tailTables = [.. GetFullOrderSnapshot(playlist).Where(table => table != null && !frontSet.Contains(table))];
        var changedTables = new List<BMSTable>();
        var usedSortValues = new HashSet<int>();
        for (int i = 0; i < frontTables.Count; i++)
        {
            BMSTable table = frontTables[i];
            int desiredSort = i + 1;
            if (table.bmt_sort != desiredSort)
            {
                table.bmt_sort = desiredSort;
                changedTables.Add(table);
            }
            usedSortValues.Add(desiredSort);
        }
        int nextTailSort = frontTables.Count + 1;
        foreach (BMSTable table in tailTables)
        {
            int? currentSort = table.bmt_sort;
            if (currentSort.HasValue
                && currentSort.Value > frontTables.Count
                && usedSortValues.Add(currentSort.Value))
            {
                nextTailSort = Math.Max(nextTailSort, currentSort.Value + 1);
                continue;
            }
            while (usedSortValues.Contains(nextTailSort))
            {
                nextTailSort++;
            }
            table.bmt_sort = nextTailSort;
            changedTables.Add(table);
            usedSortValues.Add(nextTailSort);
            nextTailSort++;
        }
        if (changedTables.Count == 0)
        {
            return;
        }
        playlist.CommitBMSTableHeadersToDB(changedTables);
        playlist.QueueBeatorajaBmtUrlSync("beatoraja_table_url_import");
        refreshPlaylistSummary("beatoraja_table_url_import", false, false);
    }

    private List<BMSTable> GetFullOrderSnapshot()
    {
        BMSPlaylist playlist = playlistProvider();
        if (playlist == null)
        {
            return [];
        }
        return GetFullOrderSnapshot(playlist);
    }

    private List<BMSTable> GetFullOrderSnapshot(BMSPlaylist playlist)
    {
        playlist.AcquireReaderLockBMSTables();
        try
        {
            return PlaylistSummaryBmtSortOrderPlanner.GetFullOrder(tableSnapshotProvider());
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
    }

    private long PersistOrder(IReadOnlyList<BMSTable> orderedTables, string reason)
    {
        BMSPlaylist playlist = playlistProvider();
        if (orderedTables == null || orderedTables.Count == 0 || playlist == null)
        {
            return 0L;
        }
        List<BMSTable> changedTables = [];
        for (int i = 0; i < orderedTables.Count; i++)
        {
            BMSTable table = orderedTables[i];
            int newSort = i + 1;
            if (table != null && table.bmt_sort != newSort)
            {
                table.bmt_sort = newSort;
                changedTables.Add(table);
            }
        }
        if (changedTables.Count == 0)
        {
            return 0L;
        }
        playlist.CommitBMSTableHeadersToDB(changedTables);
        playlist.QueueBeatorajaBmtUrlSync(reason);
        return refreshPlaylistSummary(reason, false, false);
    }
}
