using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

internal sealed class CustomTableCellValueCache
{
    private const int DefaultMaxEntryCount = 16384;
    private readonly Dictionary<Key, CustomTableCellValue> cache;
    private readonly Dictionary<object, bool> highlightedRows;
    private readonly Queue<Key> insertionOrder;
    private readonly int maxEntryCount;

    internal CustomTableCellValueCache(int maxEntryCount = DefaultMaxEntryCount)
    {
        this.maxEntryCount = Math.Max(1, maxEntryCount);
        cache = [];
        highlightedRows = new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance);
        insertionOrder = new Queue<Key>();
    }

    internal int Count => cache.Count;

    internal CustomTableCellValue GetOrCreate(object row, CustomTableColumn column, int rowGeneration, int columnGeneration, out bool hit)
    {
        if (row == null || column == null)
        {
            hit = false;
            return CustomTableCellValue.Empty;
        }
        var key = new Key(row, column.Id, rowGeneration, columnGeneration);
        if (cache.TryGetValue(key, out CustomTableCellValue value))
        {
            hit = true;
            return value;
        }
        hit = false;
        value = CustomTableCellValue.Create(row, column);
        cache.Add(key, value);
        insertionOrder.Enqueue(key);
        TrimToCapacity();
        return value;
    }

    internal void InvalidateRow(object row)
    {
        if (row == null || cache.Count == 0)
        {
            highlightedRows.Remove(row);
            if (cache.Count == 0)
            {
                insertionOrder.Clear();
            }
            return;
        }
        highlightedRows.Remove(row);
        foreach (Key key in new List<Key>(cache.Keys))
        {
            if (ReferenceEquals(key.Row, row))
            {
                cache.Remove(key);
            }
        }
        RebuildInsertionOrder();
    }

    internal void Clear()
    {
        cache.Clear();
        highlightedRows.Clear();
        insertionOrder.Clear();
    }

    internal bool HasHighlightedWarning(object row)
    {
        if (row == null)
        {
            return false;
        }
        if (highlightedRows.TryGetValue(row, out bool highlighted))
        {
            return highlighted;
        }
        highlighted = CustomTableColumnFactory.HasHighlightedWarning(row);
        highlightedRows[row] = highlighted;
        return highlighted;
    }

    private void TrimToCapacity()
    {
        while (cache.Count > maxEntryCount && insertionOrder.Count > 0)
        {
            cache.Remove(insertionOrder.Dequeue());
        }
    }

    private void RebuildInsertionOrder()
    {
        insertionOrder.Clear();
        foreach (Key key in cache.Keys)
        {
            insertionOrder.Enqueue(key);
        }
    }

    private readonly struct Key : IEquatable<Key>
    {
        internal Key(object row, string columnId, int rowGeneration, int columnGeneration)
        {
            Row = row;
            ColumnId = columnId ?? string.Empty;
            RowGeneration = rowGeneration;
            ColumnGeneration = columnGeneration;
        }

        internal object Row { get; }

        private string ColumnId { get; }

        private int RowGeneration { get; }

        private int ColumnGeneration { get; }

        public bool Equals(Key other)
        {
            return ReferenceEquals(Row, other.Row)
                && ColumnId == other.ColumnId
                && RowGeneration == other.RowGeneration
                && ColumnGeneration == other.ColumnGeneration;
        }

        public override bool Equals(object obj)
        {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = RuntimeHelpers.GetHashCode(Row);
                hash = (hash * 31) + ColumnId.GetHashCode();
                hash = (hash * 31) + RowGeneration;
                hash = (hash * 31) + ColumnGeneration;
                return hash;
            }
        }
    }
}

internal readonly struct CustomTableTextRunStyle
{
    internal CustomTableTextRunStyle(int startIndex, int length, Brush foreground)
    {
        StartIndex = startIndex;
        Length = length;
        Foreground = foreground;
    }

    internal int StartIndex { get; }

    internal int Length { get; }

    internal Brush Foreground { get; }
}

internal readonly struct CustomTableCellValue
{
    private CustomTableCellValue(string text, Brush foreground, Brush background, IReadOnlyList<CustomTableTextRunStyle> textRuns, CustomTableCellKind cellKind, CustomTableTextStyle textStyle, System.Windows.TextAlignment alignment, bool? isChecked)
    {
        Text = text ?? string.Empty;
        Foreground = foreground ?? CustomTableScoreBrushProvider.DefaultForeground;
        Background = background;
        TextRuns = textRuns ?? [];
        CellKind = cellKind;
        TextStyle = textStyle ?? CustomTableTextStyle.Normal;
        Alignment = alignment;
        IsChecked = isChecked;
    }

    internal static CustomTableCellValue Empty { get; } = new CustomTableCellValue(string.Empty, CustomTableScoreBrushProvider.DefaultForeground, null, [], CustomTableCellKind.Text, CustomTableTextStyle.Normal, System.Windows.TextAlignment.Left, null);

    internal string Text { get; }

    internal Brush Foreground { get; }

    internal Brush Background { get; }

    internal IReadOnlyList<CustomTableTextRunStyle> TextRuns { get; }

    internal CustomTableCellKind CellKind { get; }

    internal CustomTableTextStyle TextStyle { get; }

    internal bool UseBoldText => TextStyle.UseBoldText;

    internal System.Windows.TextAlignment Alignment { get; }

    internal bool? IsChecked { get; }

    internal static CustomTableCellValue Create(object row, CustomTableColumn column)
    {
        string text = column.GetText(row);
        Brush foreground = column.GetForeground(row);
        IReadOnlyList<CustomTableTextRunStyle> textRuns = NormalizeTextRuns(text, foreground, column.GetTextRuns(row, text), column.CellKind);
        return new CustomTableCellValue(
            text,
            foreground,
            column.GetBackground(row),
            textRuns,
            column.CellKind,
            column.TextStyle,
            column.Alignment,
            column.GetChecked(row));
    }

    private static IReadOnlyList<CustomTableTextRunStyle> NormalizeTextRuns(string text, Brush foreground, IReadOnlyList<CustomTableTextRunStyle> textRuns, CustomTableCellKind cellKind)
    {
        if (textRuns.Count > 0
            || string.IsNullOrEmpty(text)
            || !UsesTextRunsForSelection(cellKind)
            || ReferenceEquals(foreground, CustomTableScoreBrushProvider.DefaultForeground))
        {
            return textRuns;
        }
        return [new CustomTableTextRunStyle(0, text.Length, foreground)];
    }

    private static bool UsesTextRunsForSelection(CustomTableCellKind cellKind)
    {
        return cellKind == CustomTableCellKind.Text || cellKind == CustomTableCellKind.ActionText;
    }
}
