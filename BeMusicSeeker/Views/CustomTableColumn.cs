using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public sealed class CustomTableColumn
{
    internal CustomTableColumn(
        string id,
        string header,
        dataGridColumnsSettings.dataGridColumnlayouts layout,
        int fallbackOrder,
        string sortMemberPath,
        TextAlignment alignment,
        Func<object, string> textSelector,
        Func<object, Brush> foregroundSelector = null,
        bool useBoldText = false,
        Func<object, string> tooltipSelector = null,
        int minWidth = 40,
        int? maxWidth = null,
        bool canResize = true,
        bool useIconText = false)
    {
        Id = id;
        Header = header;
        Layout = layout;
        FallbackOrder = fallbackOrder;
        SortMemberPath = sortMemberPath;
        Alignment = alignment;
        TextSelector = textSelector;
        ForegroundSelector = foregroundSelector;
        UseBoldText = useBoldText;
        TooltipSelector = tooltipSelector;
        MinWidth = Math.Max(1, minWidth);
        MaxWidth = maxWidth.HasValue ? Math.Max(MinWidth, maxWidth.Value) : int.MaxValue;
        CanResize = canResize;
        UseIconText = useIconText;
    }

    public string Id { get; }

    public string Header { get; }

    public dataGridColumnsSettings.dataGridColumnlayouts Layout { get; }

    public int FallbackOrder { get; }

    public string SortMemberPath { get; }

    public TextAlignment Alignment { get; }

    public bool UseBoldText { get; }

    public int MinWidth { get; }

    public int MaxWidth { get; }

    public bool CanResize { get; }

    public bool UseIconText { get; }

    public int DisplayIndex => Layout?.DisplayIndex ?? -1;

    public int Width => ClampWidth(Layout?.Width ?? 50);

    public bool IsVisible => Layout == null || Layout.Visibility == Visibility.Visible;

    internal Func<object, string> TextSelector { get; }

    internal Func<object, Brush> ForegroundSelector { get; }

    internal Func<object, string> TooltipSelector { get; }

    internal string GetText(object row)
    {
        return TextSelector == null ? string.Empty : TextSelector(row) ?? string.Empty;
    }

    internal string GetTooltip(object row)
    {
        string tooltip = TooltipSelector == null ? null : TooltipSelector(row);
        return string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;
    }

    internal Brush GetForeground(object row)
    {
        return ForegroundSelector == null ? CustomTableScoreBrushProvider.DefaultForeground : ForegroundSelector(row) ?? CustomTableScoreBrushProvider.DefaultForeground;
    }

    internal int ClampWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return MinWidth;
        }
        return Math.Max(MinWidth, Math.Min(MaxWidth, (int)Math.Round(width)));
    }
}

internal static class CustomTableColumnFactory
{
    private const string DownloadIconGlyphText = "\uE14F";

    internal static IReadOnlyList<CustomTableColumn> CreateMainColumns(dataGridColumnsSettings settings)
    {
        if (settings == null)
        {
            return Array.Empty<CustomTableColumn>();
        }
        CustomTableColumn[] columns =
        {
            new CustomTableColumn("Title", "TITLE", settings.Title, 0, nameof(LibraryChartRow.Title), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 1, nameof(LibraryChartRow.Artist), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Artist)), minWidth: 50),
            new CustomTableColumn("Url1", "URL1", settings.Url1, 2, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, useIconText: true),
            new CustomTableColumn("Url2", "URL2", settings.Url2, 3, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDiffDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlDiffToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, useIconText: true),
            new CustomTableColumn("Warning", "WARNING", settings.Warning, 4, nameof(LibraryChartRow.DisplayWarning), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.DisplayWarning)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.DisplayWarning))),
            new CustomTableColumn("Comment", "COMMENT", settings.Comment, 5, null, TextAlignment.Left, row => GetString(row, "comment"), tooltipSelector: row => GetString(row, "comment")),
            new CustomTableColumn("Memo", "MEMO", settings.Memo, 6, null, TextAlignment.Left, row => GetString(row, "memo"), tooltipSelector: row => GetString(row, "memo")),
            new CustomTableColumn("Path", "PATH", settings.Path, 7, "path", TextAlignment.Left, row => GetString(row, "path")),
            new CustomTableColumn("PlaylistSymbols", "PLAYLIST", settings.PlaylistSymbols, 8, nameof(LibraryChartRow.RefTablesSymbols), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.RefTablesSymbols)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.RefTablesNames))),
            new CustomTableColumn("Clear", "CLEAR", settings.Clear, 9, nameof(LibraryChartRow.ClearDisplayText), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ClearDisplayText)), row => GetClearBrush(row), useBoldText: true),
            new CustomTableColumn("Rank", "DJ LEVEL", settings.Rank, 10, nameof(LibraryChartRow.RankDisplayText), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.RankDisplayText)), row => GetRankBrush(row), useBoldText: true),
            new CustomTableColumn("Level", "LEVEL", settings.Level, 11, nameof(LibraryChartRow.ChartLevelSortKey), TextAlignment.Right, row => GetString(row, nameof(LibraryChartRow.ChartLevelText))),
            new CustomTableColumn("ChartDifficulty", "DIFFICULTY", settings.ChartDifficulty, 12, nameof(LibraryChartRow.ChartDifficultySortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartDifficultyText)), row => GetDifficultyBrush(row), useBoldText: true),
            new CustomTableColumn("ChartJudge", "JUDGE", settings.ChartJudge, 13, nameof(LibraryChartRow.ChartJudgeSortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartJudgeText)), row => GetJudgeBrush(row), useBoldText: true)
        };
        return columns
            .Where(column => column.IsVisible)
            .OrderBy(column => column.DisplayIndex >= 0 ? column.DisplayIndex : int.MaxValue)
            .ThenBy(column => column.FallbackOrder)
            .ToArray();
    }

    internal static IEnumerable<dataGridColumnsSettings.dataGridColumnlayouts> EnumerateMainColumnLayouts(dataGridColumnsSettings settings)
    {
        if (settings == null)
        {
            yield break;
        }
        yield return settings.Title;
        yield return settings.Artist;
        yield return settings.Url1;
        yield return settings.Url2;
        yield return settings.Warning;
        yield return settings.Comment;
        yield return settings.Memo;
        yield return settings.Path;
        yield return settings.PlaylistSymbols;
        yield return settings.Clear;
        yield return settings.Rank;
        yield return settings.Level;
        yield return settings.ChartDifficulty;
        yield return settings.ChartJudge;
    }

    private static string GetString(object row, string propertyName)
    {
        if (row == null)
        {
            return string.Empty;
        }
        switch (propertyName)
        {
            case nameof(LibraryChartRow.Title):
                return GetTitle(row);
            case nameof(LibraryChartRow.Artist):
                return GetArtist(row);
            case "path":
                return GetPath(row);
            case nameof(LibraryChartRow.DisplayWarning):
                return GetDisplayWarning(row);
            case nameof(LibraryChartRow.RefTablesSymbols):
                return GetRefTablesSymbols(row);
            case nameof(LibraryChartRow.RefTablesNames):
                return GetRefTablesNames(row);
            case nameof(LibraryChartRow.ClearDisplayText):
                return GetClearDisplayText(row);
            case nameof(LibraryChartRow.RankDisplayText):
                return GetRankDisplayText(row);
            case nameof(LibraryChartRow.ChartLevelText):
                return GetChartLevelText(row);
            case nameof(LibraryChartRow.ChartDifficultyText):
                return GetChartDifficultyText(row);
            case nameof(LibraryChartRow.ChartJudgeText):
                return GetChartJudgeText(row);
            default:
                return GetReflectionString(row, propertyName);
        }
    }

    internal static string ConvertLigatureSymbolText(string text)
    {
        return string.Equals(text, "download", StringComparison.Ordinal) ? DownloadIconGlyphText : text;
    }

    private static string GetTitle(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.Title : row is PlaylistDetailRow playlistRow ? playlistRow.Title : GetReflectionString(row, nameof(LibraryChartRow.Title));
    }

    private static string GetArtist(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.Artist : row is PlaylistDetailRow playlistRow ? playlistRow.Artist : GetReflectionString(row, nameof(LibraryChartRow.Artist));
    }

    private static string GetPath(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.path : row is PlaylistDetailRow playlistRow ? playlistRow.path : GetReflectionString(row, "path");
    }

    private static string GetDisplayWarning(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.DisplayWarning : row is PlaylistDetailRow playlistRow ? playlistRow.DisplayWarning : GetReflectionString(row, nameof(LibraryChartRow.DisplayWarning));
    }

    private static string GetRefTablesSymbols(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.RefTablesSymbols : row is PlaylistDetailRow playlistRow ? playlistRow.RefTablesSymbols : GetReflectionString(row, nameof(LibraryChartRow.RefTablesSymbols));
    }

    private static string GetRefTablesNames(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.RefTablesNames : row is PlaylistDetailRow playlistRow ? playlistRow.RefTablesNames : GetReflectionString(row, nameof(LibraryChartRow.RefTablesNames));
    }

    private static string GetClearDisplayText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.ClearDisplayText : row is PlaylistDetailRow playlistRow ? playlistRow.ClearDisplayText : GetReflectionString(row, nameof(LibraryChartRow.ClearDisplayText));
    }

    private static string GetRankDisplayText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.RankDisplayText : row is PlaylistDetailRow playlistRow ? playlistRow.RankDisplayText : GetReflectionString(row, nameof(LibraryChartRow.RankDisplayText));
    }

    private static string GetChartLevelText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.ChartLevelText : row is PlaylistDetailRow playlistRow ? playlistRow.ChartLevelText : GetReflectionString(row, nameof(LibraryChartRow.ChartLevelText));
    }

    private static string GetChartDifficultyText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.ChartDifficultyText : row is PlaylistDetailRow playlistRow ? playlistRow.ChartDifficultyText : GetReflectionString(row, nameof(LibraryChartRow.ChartDifficultyText));
    }

    private static string GetChartJudgeText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.ChartJudgeText : row is PlaylistDetailRow playlistRow ? playlistRow.ChartJudgeText : GetReflectionString(row, nameof(LibraryChartRow.ChartJudgeText));
    }

    private static string GetReflectionString(object row, string propertyName)
    {
        object value = row.GetType().GetProperty(propertyName)?.GetValue(row, null);
        return value?.ToString() ?? string.Empty;
    }

    private static Brush GetClearBrush(object row)
    {
        if (row is LibraryChartRow libraryRow)
        {
            return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertClear(libraryRow.clear), CustomTableScoreBrushProvider.DefaultForeground);
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertClear(playlistRow.clear), CustomTableScoreBrushProvider.DefaultForeground);
        }
        return CustomTableScoreBrushProvider.DefaultForeground;
    }

    private static Brush GetRankBrush(object row)
    {
        if (row is LibraryChartRow libraryRow)
        {
            return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertRank(libraryRow.rank), CustomTableScoreBrushProvider.DefaultForeground);
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertRank(playlistRow.rank), CustomTableScoreBrushProvider.DefaultForeground);
        }
        return CustomTableScoreBrushProvider.DefaultForeground;
    }

    private static Brush GetDifficultyBrush(object row)
    {
        string key = row is LibraryChartRow libraryRow
            ? libraryRow.ChartDifficultyColorKey
            : row is PlaylistDetailRow playlistRow
                ? playlistRow.ChartDifficultyColorKey
                : GetReflectionString(row, nameof(LibraryChartRow.ChartDifficultyColorKey));
        return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertDifficulty(key), CustomTableScoreBrushProvider.DefaultForeground);
    }

    private static Brush GetJudgeBrush(object row)
    {
        string key = row is LibraryChartRow libraryRow
            ? libraryRow.ChartJudgeColorKey
            : row is PlaylistDetailRow playlistRow
                ? playlistRow.ChartJudgeColorKey
                : GetReflectionString(row, nameof(LibraryChartRow.ChartJudgeColorKey));
        return CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertJudge(key), CustomTableScoreBrushProvider.DefaultForeground);
    }

    internal static bool HasHighlightedWarning(object row)
    {
        if (row is LibraryChartRow libraryRow)
        {
            return libraryRow.HasHighlightedWarning;
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            return playlistRow.HasHighlightedWarning;
        }
        object value = row?.GetType().GetProperty(nameof(LibraryChartRow.HasHighlightedWarning))?.GetValue(row, null);
        return value is bool highlighted && highlighted;
    }
}
