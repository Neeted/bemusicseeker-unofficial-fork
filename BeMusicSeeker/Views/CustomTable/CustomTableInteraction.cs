using System;
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

/// <summary>選択の変化の通知です。通知のために主行以外の行を実体化しないよう、選択行は件数だけを持ちます。</summary>
/// <remarks>全選択行が必要な購読側は、その操作の境界で <see cref="CustomTableView.GetSelectedRowsSnapshot"/> を呼びます。</remarks>
public sealed class CustomTableSelectionChangedEventArgs : EventArgs
{
    internal CustomTableSelectionChangedEventArgs(int selectedIndex, object selectedRow, int selectedCount)
    {
        SelectedIndex = selectedIndex;
        SelectedRow = selectedRow;
        SelectedCount = selectedCount;
    }

    /// <summary>主行の添字です。主行がなければ-1です。</summary>
    public int SelectedIndex { get; }

    /// <summary>主行の行です。主行がないか範囲外ならnullです。</summary>
    public object SelectedRow { get; }

    /// <summary>選択行数です。null行を含む、モデル上の添字の件数です。</summary>
    public int SelectedCount { get; }
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

public sealed class CustomTableCellEditBeginningEventArgs : EventArgs
{
    internal CustomTableCellEditBeginningEventArgs(CustomTableHitTestResult hit, string editPropertyName)
    {
        Hit = hit;
        EditPropertyName = editPropertyName;
    }

    public CustomTableHitTestResult Hit { get; }

    public object Row => Hit?.Row;

    public int RowIndex => Hit?.RowIndex ?? -1;

    public CustomTableColumn Column => Hit?.Column;

    public string EditPropertyName { get; }

    public bool Cancel { get; set; }
}

public sealed class CustomTableCellEditStartedEventArgs : EventArgs
{
    internal CustomTableCellEditStartedEventArgs(CustomTableHitTestResult hit, string editPropertyName)
    {
        Hit = hit;
        EditPropertyName = editPropertyName;
    }

    public CustomTableHitTestResult Hit { get; }

    public object Row => Hit?.Row;

    public string EditPropertyName { get; }
}

public sealed class CustomTableCellActionRequestedEventArgs : EventArgs
{
    internal CustomTableCellActionRequestedEventArgs(CustomTableHitTestResult hit)
    {
        Hit = hit;
    }

    public CustomTableHitTestResult Hit { get; }

    public object Row => Hit?.Row;

    public int RowIndex => Hit?.RowIndex ?? -1;

    public CustomTableColumn Column => Hit?.Column;
}

public sealed class CustomTableCellEditEndedEventArgs : EventArgs
{
    /// <summary>置換前の原文と終端入力を同じ編集対象に結び付けます。</summary>
    internal CustomTableCellEditEndedEventArgs(CustomTableHitTestResult hit, string editPropertyName, string text, bool commit, string originalText)
    {
        Hit = hit;
        EditPropertyName = editPropertyName;
        OriginalText = originalText;
        Text = text ?? string.Empty;
        Commit = commit;
    }

    public CustomTableHitTestResult Hit { get; }

    public object Row => Hit?.Row;

    public int RowIndex => Hit?.RowIndex ?? -1;

    public CustomTableColumn Column => Hit?.Column;

    public string EditPropertyName { get; }

    public string Text { get; }

    /// <summary>直接打鍵で置換する前に一回捕捉したセルの編集原文です。</summary>
    public string OriginalText { get; }

    public bool Commit { get; }
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
