using System.Collections.Generic;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>保持範囲内で読んだ行の値・警告・位置別描画を、同じ参照寿命で保持します。</summary>
internal sealed class CustomTableRowCache
{
    private readonly Dictionary<object, CustomTableRowEntry> rows = new(ReferenceEqualityComparer<object>.Instance);

    /// <summary>値または描画を取得済みの保持行数を返します。</summary>
    internal int Count => rows.Count;

    /// <summary>行参照に対応するエントリを遅延作成します。列値や描画の先行生成は行いません。</summary>
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

    /// <summary>変更された行、または最後の参照が保持範囲から離れた行の値と描画を破棄します。</summary>
    internal void InvalidateRow(object row) => rows.Remove(row);

    /// <summary>ソース・列・表示値の全体更新時に値と描画を一緒に破棄します。</summary>
    internal void Clear() => rows.Clear();

    /// <summary>保持済みの行だけで指定列の表示値を失効させ、他列の値と行参照を保ちます。幅依存のURL表示切替で使います。</summary>
    internal void InvalidateColumnValues(string columnId)
    {
        foreach (CustomTableRowEntry row in rows.Values)
        {
            row.InvalidateColumnValue(columnId);
        }
    }

    /// <summary>値を保持したまま、文字・列配置などの描画条件だけを失効させます。</summary>
    internal void ClearDrawings()
    {
        foreach (CustomTableRowEntry row in rows.Values)
        {
            row.Drawings.Clear();
        }
    }

    /// <summary>同じ行参照が残っていても、保持範囲を離れた表示位置の描画は破棄します。</summary>
    internal void RetainDrawingPositions(int firstIndex, int count)
    {
        foreach (CustomTableRowEntry row in rows.Values)
        {
            foreach (int position in new List<int>(row.Drawings.Keys))
            {
                if (position < firstIndex || position >= firstIndex + count)
                {
                    row.Drawings.Remove(position);
                }
            }
        }
    }
}

/// <summary>一つの行参照の値・警告と、その参照が表示される各位置のセル描画を所有します。</summary>
internal sealed class CustomTableRowEntry
{
    private readonly object row;
    private readonly Dictionary<string, CustomTableCellValue> values = [];
    private bool? highlightedWarning;

    /// <summary>値と警告を取得する行を指定します。</summary>
    internal CustomTableRowEntry(object row) => this.row = row;

    /// <summary>選択・現在セルが異なり得る表示位置ごとの描画を保持します。</summary>
    internal Dictionary<int, CustomTableRowDrawing> Drawings { get; } = [];

    /// <summary>警告も列値と同じ行寿命で遅延取得します。</summary>
    internal bool HasHighlightedWarning => highlightedWarning ??= CustomTableColumnFactory.HasHighlightedWarning(row);

    /// <summary>列幅で表示が変わる一列の値だけを除去します。配置変更による描画失効は表側で処理します。</summary>
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

/// <summary>行ローカル座標のセル群と、その表示位置の選択・現在セル条件を保持します。</summary>
internal sealed class CustomTableRowDrawing
{
    /// <summary>行高全体を描いたセル群を、表示状態とともに保持します。</summary>
    internal CustomTableRowDrawing(DrawingGroup drawing, bool selected, CustomTableColumn currentColumn)
    {
        Drawing = drawing;
        Selected = selected;
        CurrentColumn = currentColumn;
    }

    /// <summary>位置移動だけで再利用するセル群です。</summary>
    internal DrawingGroup Drawing { get; }

    /// <summary>描画を作成したときの行選択状態です。</summary>
    internal bool Selected { get; }

    /// <summary>描画を作成したときの現在セル列です。該当しない行ではnullです。</summary>
    internal CustomTableColumn CurrentColumn { get; }
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
