using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistSummaryBmtSortCoordinator
{
    private readonly Func<BMSPlaylist> playlistProvider;
    private readonly Func<IEnumerable<BMSTable>> tableSnapshotProvider;

    private readonly object reorderGate = new();

    internal PlaylistSummaryBmtSortCoordinator(
        Func<BMSPlaylist> playlistProvider,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider)
    {
        this.playlistProvider = playlistProvider ?? throw new ArgumentNullException(nameof(playlistProvider));
        this.tableSnapshotProvider = tableSnapshotProvider ?? throw new ArgumentNullException(nameof(tableSnapshotProvider));
    }

    internal bool ApplyCurrentVisibleOrder(IEnumerable<PlaylistSummaryRow> visibleRows)
    {
        List<PlaylistSummaryRow> visibleRowsSnapshot = [.. (visibleRows ?? [])];
        return ExecuteSerialized(
            "playlist_summary_apply_current_order_to_bmt_sort",
            [],
            (playlist, fullOrder, activeTables) =>
            {
                List<PlaylistSummaryRow> activeVisibleRows = FilterRowsToActiveTables(visibleRowsSnapshot, activeTables);
                return activeVisibleRows.Count == 0
                    ? false
                    : PersistOrder(
                        playlist,
                        PlaylistSummaryBmtSortOrderPlanner.BuildOrderByReplacingVisibleSlots(fullOrder, activeVisibleRows),
                        activeTables);
            },
            requirePlaylist: false);
    }

    internal bool MoveRowsToTop(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return ExecuteSerialized(
            "playlist_summary_move_to_bmt_sort_top",
            [],
            (playlist, fullOrder, activeTables) =>
            {
                List<PlaylistSummaryRow> activeRows = FilterRowsToActiveTables(rowsSnapshot, activeTables);
                return activeRows.Count == 0
                    ? false
                    : PersistOrder(
                        playlist,
                        PlaylistSummaryBmtSortOrderPlanner.BuildOrderByMovingRows(fullOrder, activeRows, insertAtTop: true),
                        activeTables);
            },
            requirePlaylist: false);
    }

    internal bool MoveRowsToBottom(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return ExecuteSerialized(
            "playlist_summary_move_to_bmt_sort_bottom",
            [],
            (playlist, fullOrder, activeTables) =>
            {
                List<PlaylistSummaryRow> activeRows = FilterRowsToActiveTables(rowsSnapshot, activeTables);
                return activeRows.Count == 0
                    ? false
                    : PersistOrder(
                        playlist,
                        PlaylistSummaryBmtSortOrderPlanner.BuildOrderByMovingRows(fullOrder, activeRows, insertAtTop: false),
                        activeTables);
            },
            requirePlaylist: false);
    }

    internal bool DropRows(
        IEnumerable<PlaylistSummaryRow> visibleRows,
        IEnumerable<PlaylistSummaryRow> draggedRows,
        int visibleInsertIndex,
        Action<IReadOnlyList<BMSTable>> activeDraggedRowsApplied = null)
    {
        List<PlaylistSummaryRow> visibleRowsSnapshot = [.. (visibleRows ?? [])];
        List<PlaylistSummaryRow> draggedRowsSnapshot = [.. (draggedRows ?? [])];
        IReadOnlyList<BMSTable> appliedDraggedTables = [];
        return ExecuteSerialized(
            "playlist_summary_bmt_sort_drag_drop",
            [],
            (playlist, fullOrder, activeTables) =>
            {
                List<PlaylistSummaryRow> activeVisibleRows = FilterRowsToActiveTables(visibleRowsSnapshot, activeTables);
                List<PlaylistSummaryRow> activeDraggedRows = FilterRowsToActiveTables(draggedRowsSnapshot, activeTables);
                int activeInsertIndex = CountActiveRowsBeforeIndex(visibleRowsSnapshot, visibleInsertIndex, activeTables);
                return activeVisibleRows.Count == 0 || activeDraggedRows.Count == 0
                    ? false
                    : ApplyDropOrder(
                        playlist,
                        fullOrder,
                        activeVisibleRows,
                        activeDraggedRows,
                        activeInsertIndex,
                        activeTables,
                        activeTablesApplied => appliedDraggedTables = activeTablesApplied);
            },
            requirePlaylist: false,
            completion: changed =>
            {
                if (changed)
                {
                    activeDraggedRowsApplied?.Invoke(appliedDraggedTables);
                }
            });
    }

    internal bool ApplyImportedTablesToFront(IReadOnlyList<BMSTable> importedTables)
    {
        List<BMSTable> frontTables = [.. (importedTables ?? [])
            .Where(table => table != null)
            .Distinct()];
        if (frontTables.Count == 0)
        {
            return false;
        }
        return ExecuteSerialized(
            "beatoraja_table_url_import",
            frontTables,
            (playlist, fullOrder, activeTables) => ApplyImportedTablesToFront(playlist, frontTables, fullOrder, activeTables),
            requirePlaylist: true);
    }

    private bool ExecuteSerialized(
        string reason,
        IEnumerable<BMSTable> additionalTables,
        Func<BMSPlaylist, List<BMSTable>, HashSet<BMSTable>, bool> operation,
        bool requirePlaylist,
        Action<bool> completion = null)
    {
        // Keep the collection read lock through the snapshot and header commit.  Publish the
        // completion only after releasing it because the callback may synchronously cross the
        // UI dispatcher, which can be waiting for a collection writer.
        lock (reorderGate)
        {
            BMSPlaylist playlist = playlistProvider();
            if (playlist == null)
            {
                if (requirePlaylist)
                {
                    throw new InvalidOperationException("A playlist store is required to apply imported BMT sort order.");
                }
                return false;
            }

            bool changed;
            playlist.AcquireReaderLockBMSTables();
            var writerGuards = new List<IDisposable>();
            try
            {
                List<BMSTable> fullOrder = PlaylistSummaryBmtSortOrderPlanner.GetFullOrder(tableSnapshotProvider());
                var activeTables = new HashSet<BMSTable>(fullOrder);
                List<BMSTable> lockTargets = [.. fullOrder
                    .Concat(additionalTables ?? [])
                    .Where(table => table != null)
                    .Distinct()
                    .OrderBy(table => table.playlist_id ?? int.MaxValue)
                    .ThenBy(table => table.name ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(RuntimeHelpers.GetHashCode)];
                foreach (BMSTable table in lockTargets)
                {
                    writerGuards.Add(table.ReaderWriterLock.GetWriterGuard());
                }
                changed = operation(playlist, fullOrder, activeTables);
                for (int index = writerGuards.Count - 1; index >= 0; index--)
                {
                    writerGuards[index]?.Dispose();
                }
                writerGuards.Clear();
                if (changed)
                {
                    playlist.BmtOutput.QueueBeatorajaBmtUrlSync(reason);
                }
            }
            finally
            {
                for (int index = writerGuards.Count - 1; index >= 0; index--)
                {
                    writerGuards[index]?.Dispose();
                }
                playlist.FreeReaderLockBMSTables();
            }
            completion?.Invoke(changed);
            return changed;
        }
    }

    private bool ApplyImportedTablesToFront(
        BMSPlaylist playlist,
        IReadOnlyList<BMSTable> frontTables,
        IReadOnlyList<BMSTable> fullOrder,
        ISet<BMSTable> activeTables)
    {
        List<BMSTable> activeFrontTables = [.. frontTables.Where(activeTables.Contains)];
        if (activeFrontTables.Count == 0)
        {
            return false;
        }
        var frontSet = new HashSet<BMSTable>(activeFrontTables);
        List<BMSTable> tailTables = [.. fullOrder.Where(table => table != null && !frontSet.Contains(table))];
        var desiredSortByTable = new Dictionary<BMSTable, int?>();
        var usedSortValues = new HashSet<int>();
        for (int i = 0; i < activeFrontTables.Count; i++)
        {
            desiredSortByTable[activeFrontTables[i]] = i + 1;
            usedSortValues.Add(i + 1);
        }
        int nextTailSort = activeFrontTables.Count + 1;
        foreach (BMSTable table in tailTables)
        {
            int? currentSort = table.bmt_sort;
            if (currentSort.HasValue
                && currentSort.Value > activeFrontTables.Count
                && usedSortValues.Add(currentSort.Value))
            {
                nextTailSort = Math.Max(nextTailSort, currentSort.Value + 1);
                desiredSortByTable[table] = currentSort;
                continue;
            }
            while (usedSortValues.Contains(nextTailSort))
            {
                nextTailSort++;
            }
            desiredSortByTable[table] = nextTailSort;
            usedSortValues.Add(nextTailSort);
            nextTailSort++;
        }
        return PersistDesiredSort(playlist, desiredSortByTable, activeTables);
    }

    private bool PersistOrder(BMSPlaylist playlist, IReadOnlyList<BMSTable> orderedTables, ISet<BMSTable> activeTables)
    {
        if (orderedTables == null || orderedTables.Count == 0)
        {
            return false;
        }
        var desiredSortByTable = new Dictionary<BMSTable, int?>();
        for (int i = 0; i < orderedTables.Count; i++)
        {
            BMSTable table = orderedTables[i];
            int newSort = i + 1;
            if (table != null)
            {
                desiredSortByTable[table] = newSort;
            }
        }
        return PersistDesiredSort(playlist, desiredSortByTable, activeTables);
    }

    private bool ApplyDropOrder(
        BMSPlaylist playlist,
        IReadOnlyList<BMSTable> fullOrder,
        IReadOnlyList<PlaylistSummaryRow> activeVisibleRows,
        IReadOnlyList<PlaylistSummaryRow> activeDraggedRows,
        int activeInsertIndex,
        ISet<BMSTable> activeTables,
        Action<IReadOnlyList<BMSTable>> appliedTables)
    {
        bool changed = PersistOrder(
            playlist,
            PlaylistSummaryBmtSortOrderPlanner.BuildOrderByVisibleDrop(
                fullOrder,
                activeVisibleRows,
                activeDraggedRows,
                activeInsertIndex),
            activeTables);
        if (changed)
        {
            appliedTables?.Invoke([.. activeDraggedRows.Select(row => row.TableRef).Distinct()]);
        }
        return changed;
    }

    private bool PersistDesiredSort(
        BMSPlaylist playlist,
        IReadOnlyDictionary<BMSTable, int?> desiredSortByTable,
        ISet<BMSTable> activeTables)
    {
        if (desiredSortByTable == null || desiredSortByTable.Count == 0)
        {
            return false;
        }
        var changedTables = new List<BMSTable>();
        var previousSortByTable = new Dictionary<BMSTable, int?>();
        foreach (KeyValuePair<BMSTable, int?> entry in desiredSortByTable.OrderBy(entry => entry.Value ?? int.MaxValue))
        {
            BMSTable table = entry.Key;
            int? desiredSort = entry.Value;
            if (table == null || !activeTables.Contains(table) || table.bmt_sort == desiredSort)
            {
                continue;
            }
            previousSortByTable[table] = table.bmt_sort;
            table.bmt_sort = desiredSort;
            changedTables.Add(table);
        }
        if (changedTables.Count == 0)
        {
            return false;
        }
        try
        {
            playlist.CommitBMSTableHeadersToDB(
                changedTables,
                requireCurrentTarget: true,
                collectionReadLockHeld: true);
        }
        catch
        {
            foreach (KeyValuePair<BMSTable, int?> entry in previousSortByTable)
            {
                entry.Key.bmt_sort = entry.Value;
            }
            throw;
        }
        return true;
    }

    private static List<PlaylistSummaryRow> FilterRowsToActiveTables(
        IEnumerable<PlaylistSummaryRow> rows,
        ISet<BMSTable> activeTables)
    {
        return [.. (rows ?? []).Where(row => row?.TableRef != null && activeTables.Contains(row.TableRef))];
    }

    private static int CountActiveRowsBeforeIndex(
        IReadOnlyList<PlaylistSummaryRow> rows,
        int visibleInsertIndex,
        ISet<BMSTable> activeTables)
    {
        int boundedIndex = Math.Max(0, Math.Min(visibleInsertIndex, rows?.Count ?? 0));
        int activeCount = 0;
        for (int index = 0; index < boundedIndex; index++)
        {
            if (rows[index]?.TableRef != null && activeTables.Contains(rows[index].TableRef))
            {
                activeCount++;
            }
        }
        return activeCount;
    }
}
