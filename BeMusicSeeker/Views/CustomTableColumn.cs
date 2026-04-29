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
        TextAlignment alignment,
        Func<object, string> textSelector,
        Func<object, Brush> foregroundSelector = null,
        bool useBoldText = false)
    {
        Id = id;
        Header = header;
        Layout = layout;
        FallbackOrder = fallbackOrder;
        Alignment = alignment;
        TextSelector = textSelector;
        ForegroundSelector = foregroundSelector;
        UseBoldText = useBoldText;
    }

    public string Id { get; }

    public string Header { get; }

    public dataGridColumnsSettings.dataGridColumnlayouts Layout { get; }

    public int FallbackOrder { get; }

    public TextAlignment Alignment { get; }

    public bool UseBoldText { get; }

    public int DisplayIndex => Layout?.DisplayIndex ?? -1;

    public int Width => Math.Max(1, Layout?.Width ?? 50);

    public bool IsVisible => Layout == null || Layout.Visibility == Visibility.Visible;

    internal Func<object, string> TextSelector { get; }

    internal Func<object, Brush> ForegroundSelector { get; }

    internal string GetText(object row)
    {
        return TextSelector == null ? string.Empty : TextSelector(row) ?? string.Empty;
    }

    internal Brush GetForeground(object row)
    {
        return ForegroundSelector == null ? CustomTableScoreBrushProvider.DefaultForeground : ForegroundSelector(row) ?? CustomTableScoreBrushProvider.DefaultForeground;
    }
}

internal static class CustomTableColumnFactory
{
    internal static IReadOnlyList<CustomTableColumn> CreateMainColumns(dataGridColumnsSettings settings)
    {
        if (settings == null)
        {
            return Array.Empty<CustomTableColumn>();
        }
        CustomTableColumn[] columns =
        {
            new CustomTableColumn("Title", "TITLE", settings.Title, 0, TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 1, TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Artist))),
            new CustomTableColumn("Path", "PATH", settings.Path, 2, TextAlignment.Left, row => GetString(row, "path")),
            new CustomTableColumn("Clear", "CLEAR", settings.Clear, 3, TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ClearDisplayText)), row => GetClearBrush(row), useBoldText: true),
            new CustomTableColumn("Rank", "DJ LEVEL", settings.Rank, 4, TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.RankDisplayText)), row => GetRankBrush(row), useBoldText: true),
            new CustomTableColumn("Level", "LEVEL", settings.Level, 5, TextAlignment.Right, row => GetString(row, nameof(LibraryChartRow.ChartLevelText))),
            new CustomTableColumn("ChartDifficulty", "DIFFICULTY", settings.ChartDifficulty, 6, TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartDifficultyText)), row => GetDifficultyBrush(row), useBoldText: true),
            new CustomTableColumn("ChartJudge", "JUDGE", settings.ChartJudge, 7, TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartJudgeText)), row => GetJudgeBrush(row), useBoldText: true)
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
        yield return settings.Path;
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
