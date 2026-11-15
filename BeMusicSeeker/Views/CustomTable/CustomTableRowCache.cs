using System.Collections.Generic;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>保持範囲内で読んだ行の表示値と警告を、同じ参照寿命で保持します。行やセルの描画結果は保持しません。</summary>
internal sealed class CustomTableRowCache
{
    private readonly Dictionary<object, CustomTableRowEntry> rows = new(ReferenceEqualityComparer<object>.Instance);

    /// <summary>値を取得済みの保持行数を返します。</summary>
    internal int Count => rows.Count;

    /// <summary>行参照に対応するエントリを遅延作成します。列値の先行生成は行いません。</summary>
    internal CustomTableRowEntry GetOrCreate(object row)
    {
        if (row == null)
        {
            return new CustomTableRowEntry(null);
        }
        if (!rows.TryGetValue(row, out CustomTableRowEntry entry))
        {
            entry = new CustomTableRowEntry(row);
            rows.Add(row, entry);
        }
        return entry;
    }

    /// <summary>変更された行、または最後の参照が保持範囲から離れた行の値を破棄します。</summary>
    internal void InvalidateRow(object row) => rows.Remove(row);

    /// <summary>ソース・列・表示値の全体更新時に値を破棄します。</summary>
    internal void Clear() => rows.Clear();

    /// <summary>保持済みの行だけで指定列の表示値を失効させ、他列の値と行参照を保ちます。幅依存のURL表示切替で使います。</summary>
    internal void InvalidateColumnValues(string columnId)
    {
        foreach (CustomTableRowEntry row in rows.Values)
        {
            row.InvalidateColumnValue(columnId);
        }
    }
}

/// <summary>一つの行参照の表示値と警告を所有します。</summary>
internal sealed class CustomTableRowEntry
{
    private readonly object row;
    private readonly Dictionary<string, CustomTableCellValue> values = [];
    private bool? highlightedWarning;

    /// <summary>値と警告を取得する行を指定します。</summary>
    internal CustomTableRowEntry(object row) => this.row = row;

    /// <summary>警告も列値と同じ行寿命で遅延取得します。</summary>
    internal bool HasHighlightedWarning => highlightedWarning ??= CustomTableColumnFactory.HasHighlightedWarning(row);

    /// <summary>列幅で表示が変わる一列の値だけを除去します。</summary>
    internal void InvalidateColumnValue(string columnId) => values.Remove(columnId);

    /// <summary>必要になった列だけを取得して再利用します。null行は列の取得処理を呼ばず空セルを返します。</summary>
    internal CustomTableCellValue GetValue(CustomTableColumn column, out bool hit)
    {
        if (row == null)
        {
            hit = false;
            return CustomTableCellValue.Empty;
        }
        if (values.TryGetValue(column.Id, out CustomTableCellValue value))
        {
            hit = true;
            return value;
        }
        hit = false;
        value = CustomTableCellValue.Create(row, column);
        values.Add(column.Id, value);
        return value;
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
            || IsDefaultForeground(foreground))
        {
            return textRuns;
        }
        return [new CustomTableTextRunStyle(0, text.Length, foreground)];
    }

    private static bool IsDefaultForeground(Brush foreground)
    {
        Brush defaultForeground = CustomTableScoreBrushProvider.DefaultForeground;
        if (foreground == null || ReferenceEquals(foreground, defaultForeground))
        {
            return true;
        }

        // NOTE:
        // Palette resources can be rebuilt between the column selector and normalization during parallel tests or theme changes.
        // Treat an equivalent solid brush as default so normal text still switches to the selected/current-cell foreground.
        return foreground is SolidColorBrush foregroundBrush
            && defaultForeground is SolidColorBrush defaultBrush
            && foregroundBrush.Color == defaultBrush.Color
            && foregroundBrush.Opacity == defaultBrush.Opacity;
    }

    private static bool UsesTextRunsForSelection(CustomTableCellKind cellKind)
    {
        return cellKind == CustomTableCellKind.Text || cellKind == CustomTableCellKind.ActionText;
    }
}
