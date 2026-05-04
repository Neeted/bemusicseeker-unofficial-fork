using System.Windows.Media;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal sealed class CustomTablePalette
{
    private static int cachedVersion = -1;
    private static CustomTablePalette current;

    private CustomTablePalette(
        Brush headerBackground,
        Brush rowBackground,
        Brush alternatingRowBackground,
        Brush warningRowBackground,
        Brush selectedRowBackground,
        Brush currentCellBackground,
        Brush currentCellForeground,
        Brush reorderSourceHeader,
        Brush sortGlyph,
        Brush checkBoxBackground,
        Brush defaultForeground,
        Brush selectedForeground,
        Brush subtleForeground,
        Brush undefinedCellBackground,
        Pen cellBorder,
        Pen headerBorder,
        Pen columnReorderInsert,
        Pen checkBoxBorder,
        Pen checkBoxCheck)
    {
        HeaderBackground = headerBackground;
        RowBackground = rowBackground;
        AlternatingRowBackground = alternatingRowBackground;
        WarningRowBackground = warningRowBackground;
        SelectedRowBackground = selectedRowBackground;
        CurrentCellBackground = currentCellBackground;
        CurrentCellForeground = currentCellForeground;
        ReorderSourceHeader = reorderSourceHeader;
        SortGlyph = sortGlyph;
        CheckBoxBackground = checkBoxBackground;
        DefaultForeground = defaultForeground;
        SelectedForeground = selectedForeground;
        SubtleForeground = subtleForeground;
        UndefinedCellBackground = undefinedCellBackground;
        CellBorder = cellBorder;
        HeaderBorder = headerBorder;
        ColumnReorderInsert = columnReorderInsert;
        CheckBoxBorder = checkBoxBorder;
        CheckBoxCheck = checkBoxCheck;
    }

    internal static CustomTablePalette Current
    {
        get
        {
            if (current == null || cachedVersion != AppThemeService.Version)
            {
                current = CreateFromResources();
                cachedVersion = AppThemeService.Version;
            }
            return current;
        }
    }

    internal Brush HeaderBackground { get; }

    internal Brush RowBackground { get; }

    internal Brush AlternatingRowBackground { get; }

    internal Brush WarningRowBackground { get; }

    internal Brush SelectedRowBackground { get; }

    internal Brush CurrentCellBackground { get; }

    internal Brush CurrentCellForeground { get; }

    internal Brush ReorderSourceHeader { get; }

    internal Brush SortGlyph { get; }

    internal Brush CheckBoxBackground { get; }

    internal Brush DefaultForeground { get; }

    internal Brush SelectedForeground { get; }

    internal Brush SubtleForeground { get; }

    internal Brush UndefinedCellBackground { get; }

    internal Pen CellBorder { get; }

    internal Pen HeaderBorder { get; }

    internal Pen ColumnReorderInsert { get; }

    internal Pen CheckBoxBorder { get; }

    internal Pen CheckBoxCheck { get; }

    internal static void Invalidate()
    {
        cachedVersion = -1;
        current = null;
    }

    private static CustomTablePalette CreateFromResources()
    {
        Brush headerBackground = FindBrush("Table.HeaderBackgroundBrush", Color.FromRgb(0xF6, 0xF7, 0xF8));
        Brush rowBackground = FindBrush("Table.RowBackgroundBrush", Colors.White);
        Brush alternatingRowBackground = FindBrush("Table.AlternatingRowBackgroundBrush", Color.FromRgb(0xF1, 0xF4, 0xF7));
        Brush warningRowBackground = FindBrush("Table.WarningRowBackgroundBrush", Color.FromRgb(0xFD, 0xE4, 0xE4));
        Brush selectedRowBackground = FindBrush("Table.SelectedRowBackgroundBrush", Color.FromRgb(0xD7, 0xE9, 0xFF));
        Brush currentCellBackground = FindBrush("Table.CurrentCellBackgroundBrush", Colors.DodgerBlue);
        Brush currentCellForeground = FindBrush("Table.CurrentCellTextBrush", Colors.White);
        Brush reorderSourceHeader = FindBrush("Table.ReorderSourceHeaderBrush", Color.FromRgb(0xE1, 0xE5, 0xEA));
        Brush sortGlyph = FindBrush("Table.SortGlyphBrush", Color.FromRgb(0x45, 0x4A, 0x50));
        Brush checkBoxBackground = FindBrush("Table.CheckBoxBackgroundBrush", Colors.White);
        Brush defaultForeground = FindBrush("Table.TextBrush", Colors.Black);
        Brush selectedForeground = FindBrush("Table.SelectedTextBrush", Colors.White);
        Brush subtleForeground = FindBrush("Table.SubtleTextBrush", Colors.Gray);
        Brush undefinedCellBackground = FindBrush("Table.UndefinedCellBackgroundBrush", Color.FromRgb(0xFF, 0xF6, 0xD5));

        return new CustomTablePalette(
            headerBackground,
            rowBackground,
            alternatingRowBackground,
            warningRowBackground,
            selectedRowBackground,
            currentCellBackground,
            currentCellForeground,
            reorderSourceHeader,
            sortGlyph,
            checkBoxBackground,
            defaultForeground,
            selectedForeground,
            subtleForeground,
            undefinedCellBackground,
            CreatePen(FindBrush("Table.GridLineBrush", Color.FromRgb(0xE7, 0xE9, 0xEC)), 1d),
            CreatePen(FindBrush("Table.HeaderGridLineBrush", Color.FromRgb(0xC8, 0xCC, 0xD1)), 1d),
            CreatePen(FindBrush("Table.ColumnReorderInsertBrush", Colors.Black), 3d),
            CreatePen(FindBrush("Table.CheckBoxBorderBrush", Color.FromRgb(0x45, 0x4A, 0x50)), 1d),
            CreatePen(FindBrush("Table.CheckBoxCheckBrush", Color.FromRgb(0x20, 0x20, 0x20)), 1.8d));
    }

    private static Brush FindBrush(string key, Color fallbackColor)
    {
        return AppThemeService.FindBrush(key, CreateBrush(fallbackColor));
    }

    private static Brush CreateBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        Pen pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
    }
}
