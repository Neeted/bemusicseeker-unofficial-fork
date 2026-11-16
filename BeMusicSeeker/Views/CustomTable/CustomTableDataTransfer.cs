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

    /// <summary>
    /// 可視列の編集値をコピー用に正規化し、空行を含む行数と列区切りのTAB・行区切りのCRLFを保ったTSVを作ります。
    /// </summary>
    /// <param name="rows">コピーする行。nullの行は除外します。</param>
    /// <param name="columns">出力順の列。非表示列は除外します。</param>
    /// <param name="text">生成したTSV。対象がない場合も空文字を返します。</param>
    /// <returns>可視列とnull以外の行があればtrue。生成文字列が空でもtrueを返し、呼び出し側で既存Clipboardを空に置換できるようにします。</returns>
    internal static bool TryBuildTsv(IReadOnlyList<object> rows, IReadOnlyList<CustomTableColumn> columns, out string text)
    {
        text = string.Empty;
        if (rows == null || rows.Count == 0 || columns == null || columns.Count == 0)
        {
            return false;
        }
        CustomTableColumn[] visibleColumns = [.. columns.Where(column => column?.IsVisible == true)];
        if (visibleColumns.Length == 0)
        {
            return false;
        }
        var builder = new StringBuilder();
        bool hasRow = false;
        foreach (object row in rows)
        {
            if (row == null)
            {
                continue;
            }
            if (hasRow)
            {
                builder.Append("\r\n");
            }
            bool firstColumn = true;
            foreach (CustomTableColumn column in visibleColumns)
            {
                if (!firstColumn)
                {
                    builder.Append('\t');
                }
                builder.Append(BuildCellText(row, column));
                firstColumn = false;
            }
            hasRow = true;
        }
        text = builder.ToString();
        return hasRow;
    }

    /// <summary>
    /// モデルの編集値を変更せず、単一セルとTSVで共通のコピー用正規化を適用します。
    /// </summary>
    /// <param name="row">コピー元の行。</param>
    /// <param name="column">表示値と区別した編集値を取得する列。</param>
    /// <returns>制御文字を空白へ置換した編集値。行・列・編集値がnull、または編集値が空なら空文字。</returns>
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

    /// <summary>
    /// セル内のUnicode制御文字（U+0000–001F、U+007F–009F）を1文字につきASCII空白1個へ置換します。
    /// </summary>
    /// <param name="text">コピーするセルの編集値。</param>
    /// <returns>制御文字以外を保持した文字列。nullまたは空なら空文字。</returns>
    /// <remarks>通常のセルで不要な文字列を生成しないよう、制御文字がある場合だけ複製します。</remarks>
    internal static string NormalizeCellText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsControl(text[i]))
            {
                continue;
            }
            char[] characters = text.ToCharArray();
            for (int j = i; j < characters.Length; j++)
            {
                if (char.IsControl(characters[j]))
                {
                    characters[j] = ' ';
                }
            }
            return new string(characters);
        }
        return text;
    }
}
