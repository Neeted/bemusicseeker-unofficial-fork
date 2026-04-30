using System;
using System.Collections.Generic;
using System.Windows;

namespace BeMusicSeeker.Views;

internal sealed class CustomTableColumnLayoutSnapshot
{
    private readonly IReadOnlyList<CustomTableColumn> sourceColumns;
    private readonly double horizontalOffset;
    private readonly double viewportWidth;

    private CustomTableColumnLayoutSnapshot(
        IReadOnlyList<CustomTableColumn> sourceColumns,
        double horizontalOffset,
        double viewportWidth,
        IReadOnlyList<CustomTableColumnLayoutEntry> entries,
        IReadOnlyList<CustomTableColumnLayoutEntry> visibleEntries,
        double extentWidth)
    {
        this.sourceColumns = sourceColumns;
        this.horizontalOffset = horizontalOffset;
        this.viewportWidth = viewportWidth;
        Entries = entries;
        VisibleEntries = visibleEntries;
        ExtentWidth = extentWidth;
    }

    internal IReadOnlyList<CustomTableColumnLayoutEntry> Entries { get; }

    internal IReadOnlyList<CustomTableColumnLayoutEntry> VisibleEntries { get; }

    internal double ExtentWidth { get; }

    internal int VisibleColumnCount => VisibleEntries.Count;

    internal bool Matches(IReadOnlyList<CustomTableColumn> columns, double nextHorizontalOffset, double nextViewportWidth)
    {
        return ReferenceEquals(sourceColumns, columns)
            && horizontalOffset.Equals(nextHorizontalOffset)
            && viewportWidth.Equals(nextViewportWidth);
    }

    internal static CustomTableColumnLayoutSnapshot Create(IReadOnlyList<CustomTableColumn> columns, double horizontalOffset, double viewportWidth)
    {
        List<CustomTableColumnLayoutEntry> entries = new List<CustomTableColumnLayoutEntry>();
        List<CustomTableColumnLayoutEntry> visibleEntries = new List<CustomTableColumnLayoutEntry>();
        double x = 0d;
        if (columns != null)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                CustomTableColumn column = columns[i];
                double width = column?.Width ?? 0d;
                CustomTableColumnLayoutEntry entry = new CustomTableColumnLayoutEntry(column, i, x, width, horizontalOffset, viewportWidth);
                entries.Add(entry);
                if (entry.IntersectsViewport)
                {
                    visibleEntries.Add(entry);
                }
                x += width;
            }
        }
        return new CustomTableColumnLayoutSnapshot(
            columns,
            horizontalOffset,
            viewportWidth,
            entries,
            visibleEntries,
            x);
    }

    internal bool TryResolveColumn(double tableX, out CustomTableColumnLayoutEntry entry)
    {
        foreach (CustomTableColumnLayoutEntry candidate in Entries)
        {
            if (tableX >= candidate.TableX && tableX < candidate.TableX + candidate.Width)
            {
                entry = candidate;
                return candidate.Column != null;
            }
        }
        entry = default;
        return false;
    }

    internal bool TryResolveResizeColumn(double surfaceX, double margin, out CustomTableColumnLayoutEntry entry, out Rect resizeRect)
    {
        foreach (CustomTableColumnLayoutEntry candidate in Entries)
        {
            double edgeX = candidate.TableX + candidate.Width - horizontalOffset;
            if (candidate.Column != null && candidate.Column.CanResize && edgeX >= 0d && edgeX <= viewportWidth && Math.Abs(surfaceX - edgeX) <= margin)
            {
                entry = candidate;
                resizeRect = new Rect(Math.Max(0d, edgeX - margin), 0d, margin * 2d, 0d);
                return true;
            }
        }
        entry = default;
        resizeRect = Rect.Empty;
        return false;
    }
}

internal readonly struct CustomTableColumnLayoutEntry
{
    internal CustomTableColumnLayoutEntry(CustomTableColumn column, int columnIndex, double tableX, double width, double horizontalOffset, double viewportWidth)
    {
        Column = column;
        ColumnIndex = columnIndex;
        TableX = tableX;
        Width = width;
        ScreenX = tableX - horizontalOffset;
        IntersectsViewport = tableX + width > horizontalOffset && tableX < horizontalOffset + viewportWidth;
    }

    internal CustomTableColumn Column { get; }

    internal int ColumnIndex { get; }

    internal double TableX { get; }

    internal double Width { get; }

    internal double ScreenX { get; }

    internal bool IntersectsViewport { get; }

    internal Rect CreateVisibleRect(double horizontalOffset, double viewportWidth, double y, double height)
    {
        return CustomTableColumnLayout.CreateVisibleColumnRect(TableX, Width, horizontalOffset, viewportWidth, y, height);
    }

    internal Rect CreateContentRect(double horizontalOffset, double y, double height)
    {
        return CustomTableColumnLayout.CreateContentColumnRect(TableX, Width, horizontalOffset, y, height);
    }
}
