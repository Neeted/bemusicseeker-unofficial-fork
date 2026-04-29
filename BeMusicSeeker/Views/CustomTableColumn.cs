using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public enum CustomTableCellKind
{
    Text,
    DownloadIcon,
    StatusIcon
}

internal enum CustomTableStatusIconKind
{
    None,
    Forward,
    Backward,
    Play,
    Loading,
    Pause,
    Searching,
    ScoreUnsent
}

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
        CustomTableCellKind cellKind = CustomTableCellKind.Text,
        string editPropertyName = null,
        bool editTextWrapping = false)
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
        CellKind = cellKind;
        EditPropertyName = editPropertyName;
        EditTextWrapping = editTextWrapping;
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

    public bool UseIconText => CellKind != CustomTableCellKind.Text;

    public CustomTableCellKind CellKind { get; }

    public string EditPropertyName { get; }

    public bool EditTextWrapping { get; }

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
        CustomTableColumn[] columns = CreateAllMainColumns(settings);
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
        foreach (CustomTableColumn column in CreateAllMainColumns(settings))
        {
            if (column.Layout != null)
            {
                yield return column.Layout;
            }
        }
    }

    private static CustomTableColumn[] CreateAllMainColumns(dataGridColumnsSettings settings)
    {
        return new[]
        {
            new CustomTableColumn("Status", "♬", settings.Status, 0, null, TextAlignment.Center, row => GetStatusIconText(row), tooltipSelector: GetStatusTooltip, minWidth: 18, maxWidth: 18, canResize: false, cellKind: CustomTableCellKind.StatusIcon),
            new CustomTableColumn("EntryLevel", "ENTRY LEVEL", settings.EntryLevel, 1, "EntryLevelSortKey", TextAlignment.Right, row => GetString(row, "Level"), editPropertyName: "Level"),
            new CustomTableColumn("Title", "TITLE", settings.Title, 2, nameof(LibraryChartRow.Title), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 3, nameof(LibraryChartRow.Artist), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Artist)), minWidth: 50),
            new CustomTableColumn("Genre", "GENRE", settings.Genre, 4, "genre", TextAlignment.Left, row => GetString(row, "genre")),
            new CustomTableColumn("Mode", "KEYS", settings.Mode, 5, "mode", TextAlignment.Right, row => FormatSuffix(GetValue(row, "mode"), "KEYS", "?KEYS"), maxWidth: 50),
            new CustomTableColumn("Tag", "TAG", settings.Tag, 6, "tag", TextAlignment.Left, row => GetString(row, "tag")),
            new CustomTableColumn("Url1", "URL1", settings.Url1, 7, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon),
            new CustomTableColumn("Url2", "URL2", settings.Url2, 8, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDiffDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlDiffToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon),
            new CustomTableColumn("Warning", "WARNING", settings.Warning, 9, nameof(LibraryChartRow.DisplayWarning), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.DisplayWarning)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.DisplayWarning))),
            new CustomTableColumn("Comment", "COMMENT", settings.Comment, 10, null, TextAlignment.Left, row => GetString(row, "comment"), tooltipSelector: row => GetString(row, "comment"), editPropertyName: "comment", editTextWrapping: true),
            new CustomTableColumn("Memo", "MEMO", settings.Memo, 11, null, TextAlignment.Left, row => GetString(row, "memo"), tooltipSelector: row => GetString(row, "memo"), editPropertyName: "memo", editTextWrapping: true),
            new CustomTableColumn("Hash", "MD5 HASH", settings.Hash, 12, "hash", TextAlignment.Center, row => GetString(row, "hash"), minWidth: 240, maxWidth: 240),
            new CustomTableColumn("Sha256", "SHA256 HASH", settings.Sha256, 13, "sha256", TextAlignment.Center, row => GetString(row, "sha256"), maxWidth: 480),
            new CustomTableColumn("Folder", "FOLDER", settings.Folder, 14, "Folder", TextAlignment.Left, row => GetString(row, "Folder")),
            new CustomTableColumn("Path", "PATH", settings.Path, 15, "path", TextAlignment.Left, row => GetString(row, "path")),
            new CustomTableColumn("InstallDst", "INSTL DST", settings.InstallDst, 16, "instl_dst", TextAlignment.Left, row => GetString(row, "instl_dst")),
            new CustomTableColumn("InstallDstTitle", Resources.Header_InstallDstTitle, settings.InstallDstTitle, 17, "InstallDestinationTitle", TextAlignment.Left, row => GetString(row, "InstallDestinationTitle")),
            new CustomTableColumn("InstallDstArtist", Resources.Header_InstallDstArtist, settings.InstallDstArtist, 18, "InstallDestinationArtist", TextAlignment.Left, row => GetString(row, "InstallDestinationArtist")),
            new CustomTableColumn("WavHealth", "WAV", settings.WavHealth, 19, "WAVHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "WAVHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("BgaHealth", "BGA", settings.BgaHealth, 20, "BGAHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "BGAHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("MovieHealth", "MOVIE", settings.MovieHealth, 21, "MovieHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "MovieHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("CharcterEncoding", "ENCODING", settings.CharcterEncoding, 22, "encoding", TextAlignment.Left, row => GetString(row, "encoding"), maxWidth: 130),
            new CustomTableColumn("PlaylistSymbols", "PLAYLIST", settings.PlaylistSymbols, 23, nameof(LibraryChartRow.RefTablesSymbols), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.RefTablesSymbols)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.RefTablesNames))),
            new CustomTableColumn("Level", "LEVEL", settings.Level, 24, nameof(LibraryChartRow.ChartLevelSortKey), TextAlignment.Right, row => GetString(row, nameof(LibraryChartRow.ChartLevelText))),
            new CustomTableColumn("ChartDifficulty", "DIFFICULTY", settings.ChartDifficulty, 25, nameof(LibraryChartRow.ChartDifficultySortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartDifficultyText)), row => GetDifficultyBrush(row), useBoldText: true),
            new CustomTableColumn("ChartMainBpm", "MAINBPM", settings.ChartMainBpm, 26, "ChartMainBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMainBpmText")),
            new CustomTableColumn("ChartMaxBpm", "MAXBPM", settings.ChartMaxBpm, 27, "ChartMaxBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMaxBpmText")),
            new CustomTableColumn("ChartMinBpm", "MINBPM", settings.ChartMinBpm, 28, "ChartMinBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMinBpmText")),
            new CustomTableColumn("ChartDuration", "DURATION", settings.ChartDuration, 29, "ChartDurationSortKey", TextAlignment.Right, row => GetString(row, "ChartDurationText")),
            new CustomTableColumn("ChartJudge", "JUDGE", settings.ChartJudge, 30, nameof(LibraryChartRow.ChartJudgeSortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartJudgeText)), row => GetJudgeBrush(row), useBoldText: true),
            new CustomTableColumn("ChartJudgePercent", "JUDGE%", settings.ChartJudgePercent, 31, "ChartJudgeSortKey", TextAlignment.Right, row => GetString(row, "ChartJudgePercentText")),
            new CustomTableColumn("ChartFeature", "FEATURE", settings.ChartFeature, 32, "ChartFeatureSortKey", TextAlignment.Left, row => GetString(row, "ChartFeatureText")),
            new CustomTableColumn("Notes", "NOTES", settings.Notes, 33, "ChartNotes", TextAlignment.Right, row => GetString(row, "ChartNotes")),
            new CustomTableColumn("ChartLongNotes", "LONG", settings.ChartLongNotes, 34, "ChartLongNotes", TextAlignment.Right, row => GetString(row, "ChartLongNotes")),
            new CustomTableColumn("ChartScratchNotes", "SCRATCH", settings.ChartScratchNotes, 35, "ChartScratchNotes", TextAlignment.Right, row => GetString(row, "ChartScratchNotes")),
            new CustomTableColumn("ChartTotal", "TOTAL", settings.ChartTotal, 36, "ChartTotalSortKey", TextAlignment.Right, row => GetString(row, "ChartTotalText")),
            new CustomTableColumn("ChartTotalPerNote", "T/N", settings.ChartTotalPerNote, 37, "ChartTotalPerNoteSortKey", TextAlignment.Right, row => GetString(row, "ChartTotalPerNoteText")),
            new CustomTableColumn("ChartDensity", "DENSITY", settings.ChartDensity, 38, "ChartDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartDensityText")),
            new CustomTableColumn("ChartPeakDensity", "PEAK", settings.ChartPeakDensity, 39, "ChartPeakDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartPeakDensityText")),
            new CustomTableColumn("ChartEndDensity", "END", settings.ChartEndDensity, 40, "ChartEndDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartEndDensityText")),
            new CustomTableColumn("ChartSoflan", "SOFLAN", settings.ChartSoflan, 41, "ChartSoflanCount", TextAlignment.Right, row => GetString(row, "ChartSoflanCount")),
            new CustomTableColumn("Clear", "CLEAR", settings.Clear, 42, nameof(LibraryChartRow.ClearDisplayText), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ClearDisplayText)), row => GetClearBrush(row), useBoldText: true),
            new CustomTableColumn("Rank", "DJ LEVEL", settings.Rank, 43, nameof(LibraryChartRow.RankDisplayText), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.RankDisplayText)), row => GetRankBrush(row), useBoldText: true),
            new CustomTableColumn("Rate", "RATE", settings.Rate, 44, "rate", TextAlignment.Right, row => FormatSuffix(GetValue(row, "rate"), "%", string.Empty)),
            new CustomTableColumn("Score", "SCORE", settings.Score, 45, "score", TextAlignment.Right, row => GetString(row, "score")),
            new CustomTableColumn("Combo", "COMBO", settings.Combo, 46, "maxcombo", TextAlignment.Right, row => GetString(row, "maxcombo")),
            new CustomTableColumn("Bp", "BP", settings.Bp, 47, "minbp", TextAlignment.Right, row => GetString(row, "minbp")),
            new CustomTableColumn("Ranking", "RANKING", settings.Ranking, 48, "rankingString", TextAlignment.Center, row => GetString(row, "rankingString"), minWidth: 95),
            new CustomTableColumn("RankingLastupdate", "RANK UPDATE", settings.RankingLastupdate, 49, "rankingLastupdate", TextAlignment.Center, row => FormatShortDate(GetValue(row, "rankingLastupdate"))),
            new CustomTableColumn("TScore", "T-SCORE", settings.TScore, 50, "stddevVal", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "stddevVal"))),
            new CustomTableColumn("ScoreDifficulty", "ΔMAX", settings.ScoreDifficulty, 51, "scoreDifficulty", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "scoreDifficulty")))
        };
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

    internal static string ConvertStatusToIconText(BMSFile.BMSFileStatus status)
    {
        return ConvertStatusToIconKind(status).ToString();
    }

    internal static CustomTableStatusIconKind ConvertStatusToIconKind(BMSFile.BMSFileStatus status)
    {
        if (status == BMSFile.BMSFileStatus.NONE)
        {
            return CustomTableStatusIconKind.None;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.FORWARD))
        {
            return CustomTableStatusIconKind.Forward;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.BACKWARD))
        {
            return CustomTableStatusIconKind.Backward;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.PLAY))
        {
            return CustomTableStatusIconKind.Play;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.LOADING))
        {
            return CustomTableStatusIconKind.Loading;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            return CustomTableStatusIconKind.Pause;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.SEARCHING))
        {
            return CustomTableStatusIconKind.Searching;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.SCORE_UNSENT))
        {
            return CustomTableStatusIconKind.ScoreUnsent;
        }
        return CustomTableStatusIconKind.None;
    }

    private static string GetStatusIconText(object row)
    {
        return ConvertStatusToIconText(GetStatus(row));
    }

    private static string GetStatusTooltip(object row)
    {
        BMSFile.BMSFileStatus status = GetStatus(row);
        if (status == BMSFile.BMSFileStatus.NONE)
        {
            return null;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.PLAY))
        {
            return Resources.Tooltip_play;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.LOADING))
        {
            return Resources.Tooltip_loading;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            return Resources.Tooltip_pause;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.FORWARD))
        {
            return Resources.Tooltip_fast_forward;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.BACKWARD))
        {
            return Resources.Tooltip_rewind;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.SEARCHING))
        {
            return Resources.Tooltip_searching;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.SCORE_UNSENT))
        {
            return Resources.Tooltip_score_unsent;
        }
        return null;
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
        return FormatValue(GetValue(row, propertyName));
    }

    private static object GetValue(object row, string propertyName)
    {
        return row?.GetType().GetProperty(propertyName)?.GetValue(row, null);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            null => string.Empty,
            double d => d.ToString(CultureInfo.CurrentCulture),
            float f => f.ToString(CultureInfo.CurrentCulture),
            decimal m => m.ToString(CultureInfo.CurrentCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString()
        };
    }

    private static string FormatSuffix(object value, string suffix, string nullValue)
    {
        return value == null ? nullValue : FormatValue(value) + suffix;
    }

    private static string FormatShortDate(object value)
    {
        if (value is DateTime dateTime)
        {
            return dateTime.ToShortDateString();
        }
        return string.Empty;
    }

    private static string FormatFixedTwo(object value)
    {
        return value is IFormattable formattable ? formattable.ToString("F2", CultureInfo.CurrentCulture) : string.Empty;
    }

    private static BMSFile.BMSFileStatus GetStatus(object row)
    {
        if (row is LibraryChartRow libraryRow)
        {
            return libraryRow.status;
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            return playlistRow.status;
        }
        object value = GetValue(row, "status");
        return value is BMSFile.BMSFileStatus status ? status : BMSFile.BMSFileStatus.NONE;
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
