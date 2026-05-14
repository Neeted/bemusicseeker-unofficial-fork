using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListOrder
{
    private ChartListOrder(int[] indexes, string columnName, ListSortDirection direction)
    {
        Indexes = indexes ?? Array.Empty<int>();
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
    }

    internal int[] Indexes { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int Count => Indexes.Length;

    internal static ChartListOrder CreateTitleAscending(IReadOnlyList<ChartListSourceRow> rows)
    {
        IReadOnlyList<ChartListSourceRow> safeRows = rows ?? Array.Empty<ChartListSourceRow>();
        int[] indexes = Enumerable.Range(0, safeRows.Count)
            .OrderBy(index => safeRows[index]?.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ChartListOrder(indexes, nameof(LibraryChartRow.Title), ListSortDirection.Ascending);
    }
}
