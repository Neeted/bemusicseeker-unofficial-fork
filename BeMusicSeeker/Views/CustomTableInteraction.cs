using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;

namespace BeMusicSeeker.Views;

public enum CustomTableHitKind
{
    Empty,
    Header,
    HeaderResize,
    Cell
}

public sealed class CustomTableHitTestResult
{
    internal CustomTableHitTestResult(CustomTableHitKind kind, int rowIndex, object row, CustomTableColumn column, int columnIndex, Rect cellRect)
    {
        Kind = kind;
        RowIndex = rowIndex;
        Row = row;
        Column = column;
        ColumnIndex = columnIndex;
        CellRect = cellRect;
    }

    public CustomTableHitKind Kind { get; }

    public int RowIndex { get; }

    public object Row { get; }

    public CustomTableColumn Column { get; }

    public int ColumnIndex { get; }

    public Rect CellRect { get; }
}

public sealed class CustomTableSortRequestedEventArgs : EventArgs
{
    internal CustomTableSortRequestedEventArgs(CustomTableColumn column, ListSortDirection direction)
    {
        Column = column;
        SortMemberPath = column?.SortMemberPath;
        Direction = direction;
    }

    public CustomTableColumn Column { get; }

    public string SortMemberPath { get; }

    public ListSortDirection Direction { get; }
}

public sealed class CustomTableSelectionChangedEventArgs : EventArgs
{
    internal CustomTableSelectionChangedEventArgs(int selectedIndex, object selectedRow, IReadOnlyList<object> selectedRows)
    {
        SelectedIndex = selectedIndex;
        SelectedRow = selectedRow;
        SelectedRows = selectedRows;
    }

    public int SelectedIndex { get; }

    public object SelectedRow { get; }

    public IReadOnlyList<object> SelectedRows { get; }
}

public sealed class CustomTableRowRequestedEventArgs : EventArgs
{
    internal CustomTableRowRequestedEventArgs(CustomTableHitTestResult hit, bool openAtMousePosition)
    {
        Hit = hit;
        OpenAtMousePosition = openAtMousePosition;
    }

    public CustomTableHitTestResult Hit { get; }

    public object Row => Hit?.Row;

    public int RowIndex => Hit?.RowIndex ?? -1;

    public bool OpenAtMousePosition { get; }
}

public sealed class CustomTableHeaderRequestedEventArgs : EventArgs
{
    internal CustomTableHeaderRequestedEventArgs(CustomTableHitTestResult hit, bool openAtMousePosition)
    {
        Hit = hit;
        OpenAtMousePosition = openAtMousePosition;
    }

    public CustomTableHitTestResult Hit { get; }

    public CustomTableColumn Column => Hit?.Column;

    public bool OpenAtMousePosition { get; }
}

internal sealed class CustomTableContextMenuContext
{
    internal CustomTableContextMenuContext(object row, int rowIndex)
    {
        Row = row;
        RowIndex = rowIndex;
    }

    internal object Row { get; }

    internal int RowIndex { get; }
}
