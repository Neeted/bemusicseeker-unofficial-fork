using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistSummaryBmtSortOrderPlanner
{
    internal static bool IsSortedByBmtSortAscending(ChartListSortParameters sortParameters)
    {
        return sortParameters != null
            && sortParameters.ColumnsName == nameof(PlaylistSummaryRow.BmtSort)
            && sortParameters.Direction == ListSortDirection.Ascending;
    }

    internal static List<BMSTable> BuildOrderByReplacingVisibleSlots(IEnumerable<BMSTable> fullOrder, IEnumerable<PlaylistSummaryRow> visibleRows)
    {
        List<BMSTable> fullOrderList = GetFullOrder(fullOrder);
        List<BMSTable> visibleOrder = GetDistinctTablesFromRows(visibleRows);
        if (fullOrderList.Count == 0 || visibleOrder.Count == 0)
        {
            return fullOrderList;
        }
        var visibleSet = new HashSet<BMSTable>(visibleOrder);
        int visibleIndex = 0;
        return [.. fullOrderList.Select(table => visibleSet.Contains(table) && visibleIndex < visibleOrder.Count ? visibleOrder[visibleIndex++] : table)];
    }

    internal static List<BMSTable> BuildOrderByMovingRows(IEnumerable<BMSTable> fullOrder, IEnumerable<PlaylistSummaryRow> rows, bool insertAtTop)
    {
        List<BMSTable> fullOrderList = GetFullOrder(fullOrder);
        List<BMSTable> movingTables = GetDistinctTablesFromRows(rows);
        if (fullOrderList.Count == 0 || movingTables.Count == 0)
        {
            return fullOrderList;
        }
        var movingSet = new HashSet<BMSTable>(movingTables);
        List<BMSTable> remaining = [.. fullOrderList.Where(table => !movingSet.Contains(table))];
        if (insertAtTop)
        {
            remaining.InsertRange(0, movingTables);
        }
        else
        {
            remaining.AddRange(movingTables);
        }
        return remaining;
    }

    internal static List<BMSTable> BuildOrderByVisibleDrop(IEnumerable<BMSTable> fullOrder, IEnumerable<PlaylistSummaryRow> visibleRows, IEnumerable<PlaylistSummaryRow> draggedRows, int visibleInsertIndex)
    {
        List<BMSTable> visibleOrder = GetDistinctTablesFromRows(visibleRows);
        List<BMSTable> fullOrderList = GetFullOrder(fullOrder);
        List<BMSTable> movingTables = GetDistinctTablesFromRows(draggedRows);
        if (fullOrderList.Count == 0 || visibleOrder.Count == 0 || movingTables.Count == 0)
        {
            return fullOrderList;
        }
        var movingSet = new HashSet<BMSTable>(movingTables);
        List<BMSTable> remaining = [.. fullOrderList.Where(table => !movingSet.Contains(table))];
        List<BMSTable> visibleRemaining = [.. visibleOrder.Where(table => !movingSet.Contains(table))];
        int safeInsertIndex = Math.Max(0, Math.Min(visibleInsertIndex, visibleOrder.Count));
        BMSTable previousAnchor = null;
        for (int i = safeInsertIndex - 1; i >= 0; i--)
        {
            if (!movingSet.Contains(visibleOrder[i]))
            {
                previousAnchor = visibleOrder[i];
                break;
            }
        }
        int insertIndex;
        if (previousAnchor != null)
        {
            int anchorIndex = remaining.IndexOf(previousAnchor);
            insertIndex = anchorIndex < 0 ? remaining.Count : anchorIndex + 1;
        }
        else
        {
            BMSTable nextAnchor = visibleRemaining.FirstOrDefault();
            int anchorIndex = nextAnchor == null ? -1 : remaining.IndexOf(nextAnchor);
            insertIndex = anchorIndex < 0 ? 0 : anchorIndex;
        }
        remaining.InsertRange(insertIndex, movingTables);
        return remaining;
    }

    internal static List<BMSTable> GetFullOrder(IEnumerable<BMSTable> tables)
    {
        return [.. (tables ?? [])
            .Where(table => table != null)
            .Distinct()
            .OrderBy(table => GetBmtSortOrTail(table.bmt_sort))
            .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(table => table.playlist_id ?? int.MaxValue)];
    }

    private static int GetBmtSortOrTail(int? sort)
    {
        return sort.HasValue && sort.Value > 0 ? sort.Value : int.MaxValue;
    }

    private static List<BMSTable> GetDistinctTablesFromRows(IEnumerable<PlaylistSummaryRow> rows)
    {
        return [.. (rows ?? [])
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct()];
    }
}
