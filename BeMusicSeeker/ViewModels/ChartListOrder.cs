using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListOrder
{
    private ChartListOrder(int[] indexes, string columnName, ListSortDirection direction, string sortProfile)
    {
        Indexes = indexes ?? Array.Empty<int>();
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        SortProfile = sortProfile ?? string.Empty;
    }

    internal int[] Indexes { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal string SortProfile { get; }

    internal int Count => Indexes.Length;

    internal static ChartListOrder CreateTitleAscending(IReadOnlyList<ChartListSourceRow> rows)
    {
        TryCreate(rows, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, out ChartListOrder order);
        return order;
    }

    internal static bool TryCreate(
        IReadOnlyList<ChartListSourceRow> rows,
        string columnName,
        ListSortDirection direction,
        out ChartListOrder order)
    {
        order = null;
        if (!TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            return false;
        }

        IReadOnlyList<ChartListSourceRow> safeRows = rows ?? Array.Empty<ChartListSourceRow>();
        Func<int, string> primaryKeySelector = normalizedColumnName == nameof(LibraryChartRow.path)
            ? index => safeRows[index]?.Path ?? string.Empty
            : index => safeRows[index]?.Title ?? string.Empty;
        Func<int, string> titleKeySelector = index => safeRows[index]?.Title ?? string.Empty;

        IOrderedEnumerable<int> orderedIndexes = direction == ListSortDirection.Descending
            ? Enumerable.Range(0, safeRows.Count)
                .OrderByDescending(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Range(0, safeRows.Count)
                .OrderBy(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase);

        string sortProfile = normalizedColumnName == nameof(LibraryChartRow.path)
            ? "virtual_path_order"
            : "virtual_title_order";
        order = new ChartListOrder(orderedIndexes.ToArray(), normalizedColumnName, direction, sortProfile);
        return true;
    }

    internal static bool TryNormalizeVirtualSortColumn(string columnName, out string normalizedColumnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)
            || string.Equals(columnName, nameof(LibraryChartRow.Title), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(BMSFile.Title), StringComparison.Ordinal))
        {
            normalizedColumnName = nameof(LibraryChartRow.Title);
            return true;
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.path), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(BMSFile.path), StringComparison.Ordinal))
        {
            normalizedColumnName = nameof(LibraryChartRow.path);
            return true;
        }

        normalizedColumnName = string.Empty;
        return false;
    }
}
