using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

namespace BeMusicSeeker.Views;

public enum CustomTableRowDragKind
{
    GenericSelectedRows,
    PlaylistDropCandidateRows,
    PlaylistSummaryRows
}

internal static class CustomTableDataTransfer
{
    internal const string SelectedRowsDataFormat = "BeMusicSeeker.CustomTable.SelectedRows";
    internal const string LegacySelectedRowsDataFormat = "System.Windows.Controls.SelectedItemCollection";
    internal const string RowDragKindDataFormat = "BeMusicSeeker.CustomTable.RowDragKind";
    internal const string PrimaryRowDataFormat = "BeMusicSeeker.CustomTable.PrimaryRow";

    internal static DataObject CreateSelectedRowsDataObject(IReadOnlyList<object> selectedRows, CustomTableRowDragKind rowDragKind = CustomTableRowDragKind.GenericSelectedRows, object primaryRow = null)
    {
        List<object> rows = selectedRows?.Where(row => row != null).ToList() ?? [];
        var dataObject = new DataObject();
        dataObject.SetData(SelectedRowsDataFormat, rows);
        dataObject.SetData(LegacySelectedRowsDataFormat, rows);
        dataObject.SetData(RowDragKindDataFormat, rowDragKind.ToString());
        if (primaryRow != null)
        {
            dataObject.SetData(PrimaryRowDataFormat, primaryRow);
        }
        return dataObject;
    }

    internal static bool TryGetSelectedRows(IDataObject dataObject, out List<object> selectedRows)
    {
        selectedRows = null;
        if (dataObject == null)
        {
            return false;
        }
        if (!TryGetRowsForFormat(dataObject, SelectedRowsDataFormat, out selectedRows)
            && !TryGetRowsForFormat(dataObject, LegacySelectedRowsDataFormat, out selectedRows))
        {
            return false;
        }
        selectedRows = [.. selectedRows.Where(row => row != null)];
        return selectedRows.Count > 0;
    }

    internal static bool TryGetPrimaryRow(IDataObject dataObject, out object primaryRow)
    {
        primaryRow = null;
        if (dataObject == null || !dataObject.GetDataPresent(PrimaryRowDataFormat))
        {
            return false;
        }
        try
        {
            primaryRow = dataObject.GetData(PrimaryRowDataFormat);
            return primaryRow != null;
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasRowDragKind(IDataObject dataObject, CustomTableRowDragKind expectedKind)
    {
        return TryGetRowDragKind(dataObject, out CustomTableRowDragKind actualKind) && actualKind == expectedKind;
    }

    internal static bool TryGetRowDragKind(IDataObject dataObject, out CustomTableRowDragKind rowDragKind)
    {
        rowDragKind = CustomTableRowDragKind.GenericSelectedRows;
        if (dataObject == null || !dataObject.GetDataPresent(RowDragKindDataFormat))
        {
            return false;
        }
        try
        {
            object value = dataObject.GetData(RowDragKindDataFormat);
            if (value is CustomTableRowDragKind typedKind)
            {
                rowDragKind = typedKind;
                return true;
            }
            return value is string text
                && Enum.TryParse(text, ignoreCase: false, out rowDragKind)
                && Enum.IsDefined(typeof(CustomTableRowDragKind), rowDragKind);
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildTsv(IReadOnlyList<object> rows, IReadOnlyList<CustomTableColumn> columns)
    {
        if (rows == null || rows.Count == 0 || columns == null || columns.Count == 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        foreach (object row in rows.Where(row => row != null))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            bool firstColumn = true;
            foreach (CustomTableColumn column in columns.Where(column => column?.IsVisible == true))
            {
                if (!firstColumn)
                {
                    builder.Append('\t');
                }
                builder.Append(BuildCellText(row, column));
                firstColumn = false;
            }
        }
        return builder.ToString();
    }

    internal static string BuildCellText(object row, CustomTableColumn column)
    {
        if (row == null || column == null)
        {
            return string.Empty;
        }
        return NormalizeCellText(column.GetEditText(row));
    }

    internal static bool TryReorderVisibleColumns(IReadOnlyList<CustomTableColumn> visibleColumns, CustomTableColumn sourceColumn, CustomTableColumn targetColumn, bool insertAfterTarget)
    {
        if (visibleColumns == null || sourceColumn == null || !sourceColumn.CanReorder || !visibleColumns.Contains(sourceColumn))
        {
            return false;
        }
        if (ReferenceEquals(sourceColumn, targetColumn))
        {
            return false;
        }
        List<CustomTableColumn> reordered = [.. visibleColumns.Where(column => column != null)];
        int originalIndex = reordered.IndexOf(sourceColumn);
        if (originalIndex < 0)
        {
            return false;
        }
        reordered.RemoveAt(originalIndex);
        int firstReorderableIndex = reordered.FindIndex(column => column.CanReorder);
        if (firstReorderableIndex < 0)
        {
            return false;
        }
        int targetIndex = targetColumn == null ? reordered.Count : reordered.IndexOf(targetColumn);
        if (targetIndex < 0)
        {
            targetIndex = reordered.Count;
        }
        else if (insertAfterTarget)
        {
            targetIndex++;
        }
        targetIndex = Math.Max(firstReorderableIndex, Math.Min(reordered.Count, targetIndex));
        reordered.Insert(targetIndex, sourceColumn);
        if (reordered.SequenceEqual(visibleColumns))
        {
            return false;
        }
        for (int i = 0; i < reordered.Count; i++)
        {
            if (reordered[i].Layout != null)
            {
                reordered[i].Layout.DisplayIndex = i;
            }
        }
        return true;
    }

    private static bool TryGetRowsForFormat(IDataObject dataObject, string format, out List<object> selectedRows)
    {
        selectedRows = null;
        if (!dataObject.GetDataPresent(format))
        {
            return false;
        }
        try
        {
            if (dataObject.GetData(format) is System.Collections.IEnumerable enumerable)
            {
                selectedRows = [.. enumerable.Cast<object>().Where(row => row != null)];
                return selectedRows.Count > 0;
            }
        }
        catch
        {
        }
        return false;
    }

    internal static string NormalizeCellText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
