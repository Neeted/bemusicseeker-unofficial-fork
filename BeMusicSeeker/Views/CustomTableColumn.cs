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
    StatusIcon,
    ActionText,
    CheckBox
}

internal sealed class CustomTableTextStyle
{
    private static readonly FontFamily DefaultFontFamily = new FontFamily("Meiryo UI");

    private CustomTableTextStyle(string cacheKey, FontFamily fontFamily, double fontSize, double verticalOffset, bool useBoldText, bool usesScoreFontFamily = false)
    {
        CacheKey = cacheKey;
        FontFamily = fontFamily;
        FontSize = fontSize;
        VerticalOffset = verticalOffset;
        UseBoldText = useBoldText;
        UsesScoreFontFamily = usesScoreFontFamily;
    }

    internal static CustomTableTextStyle Normal { get; } = new CustomTableTextStyle(
        "normal",
        DefaultFontFamily,
        11d,
        0d,
        false);

    internal static CustomTableTextStyle NormalBold { get; } = new CustomTableTextStyle(
        "normal-bold",
        DefaultFontFamily,
        11d,
        0d,
        true);

    internal static CustomTableTextStyle Score { get; } = new CustomTableTextStyle(
        "score-sovjetbox",
        DefaultFontFamily,
        11d,
        1d,
        false,
        usesScoreFontFamily: true);

    internal static CustomTableTextStyle Rank { get; } = new CustomTableTextStyle(
        "rank-sovjetbox",
        DefaultFontFamily,
        16d,
        3d,
        false,
        usesScoreFontFamily: true);

    internal string CacheKey { get; }

    private FontFamily FontFamily { get; }

    internal double FontSize { get; }

    internal double VerticalOffset { get; }

    internal bool UseBoldText { get; }

    private bool UsesScoreFontFamily { get; }

    internal Typeface CreateTypeface(FontFamily scoreFontFamily)
    {
        FontFamily fontFamily = UsesScoreFontFamily && scoreFontFamily != null ? scoreFontFamily : FontFamily;
        FontWeight fontWeight = UseBoldText ? FontWeights.Bold : FontWeights.Normal;
        return new Typeface(fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal);
    }

    internal string CreateCacheKey(FontFamily scoreFontFamily)
    {
        if (!UsesScoreFontFamily || scoreFontFamily == null)
        {
            return CacheKey;
        }
        return CacheKey + ":" + scoreFontFamily.Source;
    }
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
        ICustomTableColumnLayout layout,
        int fallbackOrder,
        string sortMemberPath,
        TextAlignment alignment,
        Func<object, string> textSelector,
        Func<object, Brush> foregroundSelector = null,
        Func<object, Brush> backgroundSelector = null,
        CustomTableTextStyle textStyle = null,
        bool useBoldText = false,
        Func<object, string> tooltipSelector = null,
        Func<object, bool?> checkedSelector = null,
        int minWidth = 40,
        int? maxWidth = null,
        bool canResize = true,
        bool canReorder = true,
        CustomTableCellKind cellKind = CustomTableCellKind.Text,
        string editPropertyName = null,
        bool editTextWrapping = false,
        bool editOnRepeatClick = true,
        int? editOverlayWidth = null,
        Func<object, string> editTextSelector = null,
        Func<object, IEnumerable<string>> editSuggestionsSelector = null)
    {
        Id = id;
        Header = header;
        Layout = layout;
        FallbackOrder = fallbackOrder;
        SortMemberPath = sortMemberPath;
        Alignment = alignment;
        TextSelector = textSelector;
        ForegroundSelector = foregroundSelector;
        BackgroundSelector = backgroundSelector;
        TextStyle = textStyle ?? (useBoldText ? CustomTableTextStyle.NormalBold : CustomTableTextStyle.Normal);
        UseBoldText = TextStyle.UseBoldText;
        TooltipSelector = tooltipSelector;
        CheckedSelector = checkedSelector;
        MinWidth = Math.Max(1, minWidth);
        MaxWidth = maxWidth.HasValue ? Math.Max(MinWidth, maxWidth.Value) : int.MaxValue;
        CanResize = canResize;
        CanReorder = canReorder;
        CellKind = cellKind;
        EditPropertyName = editPropertyName;
        EditTextWrapping = editTextWrapping;
        EditOnRepeatClick = editOnRepeatClick;
        EditOverlayWidth = editOverlayWidth;
        EditTextSelector = editTextSelector;
        EditSuggestionsSelector = editSuggestionsSelector;
    }

    public string Id { get; }

    public string Header { get; }

    public ICustomTableColumnLayout Layout { get; }

    public int FallbackOrder { get; }

    public string SortMemberPath { get; }

    public TextAlignment Alignment { get; }

    public bool UseBoldText { get; }

    internal CustomTableTextStyle TextStyle { get; }

    public int MinWidth { get; }

    public int MaxWidth { get; }

    public bool CanResize { get; }

    public bool CanReorder { get; }

    public bool UseIconText => CellKind != CustomTableCellKind.Text;

    public CustomTableCellKind CellKind { get; }

    public string EditPropertyName { get; }

    public bool EditTextWrapping { get; }

    public bool EditOnRepeatClick { get; }

    public int? EditOverlayWidth { get; }

    public int DisplayIndex => Layout?.DisplayIndex ?? -1;

    public int Width => ClampWidth(Layout?.Width ?? 50);

    public bool IsVisible => Layout == null || Layout.Visibility == Visibility.Visible;

    internal Func<object, string> TextSelector { get; }

    internal Func<object, Brush> ForegroundSelector { get; }

    internal Func<object, Brush> BackgroundSelector { get; }

    internal Func<object, string> TooltipSelector { get; }

    internal Func<object, bool?> CheckedSelector { get; }

    internal Func<object, string> EditTextSelector { get; }

    internal Func<object, IEnumerable<string>> EditSuggestionsSelector { get; }

    internal string GetText(object row)
    {
        return TextSelector == null ? string.Empty : TextSelector(row) ?? string.Empty;
    }

    internal string GetTooltip(object row)
    {
        string tooltip = TooltipSelector == null ? null : TooltipSelector(row);
        return string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;
    }

    internal string GetEditText(object row)
    {
        return EditTextSelector == null ? GetText(row) : EditTextSelector(row) ?? string.Empty;
    }

    internal Brush GetForeground(object row)
    {
        return ForegroundSelector == null ? CustomTableScoreBrushProvider.DefaultForeground : ForegroundSelector(row) ?? CustomTableScoreBrushProvider.DefaultForeground;
    }

    internal Brush GetBackground(object row)
    {
        return BackgroundSelector == null ? null : BackgroundSelector(row);
    }

    internal bool? GetChecked(object row)
    {
        return CheckedSelector == null ? null : CheckedSelector(row);
    }

    internal IReadOnlyList<string> GetEditSuggestions(object row)
    {
        return EditSuggestionsSelector == null
            ? Array.Empty<string>()
            : EditSuggestionsSelector(row)?.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() ?? Array.Empty<string>();
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
    private static Brush UndefinedCellBackgroundBrush => CustomTablePalette.Current.UndefinedCellBackground;

    internal static IReadOnlyList<CustomTableColumn> CreateMainColumns(CustomTableColumnSettings settings)
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

    internal static IEnumerable<CustomTableColumnSettings.ColumnLayout> EnumerateMainColumnLayouts(CustomTableColumnSettings settings)
    {
        if (settings == null)
        {
            yield break;
        }
        foreach (CustomTableColumn column in CreateAllMainColumns(settings))
        {
            if (column.Layout is CustomTableColumnSettings.ColumnLayout layout)
            {
                yield return layout;
            }
        }
    }

    internal static IReadOnlyList<CustomTableColumn> CreatePlaylistSummaryColumns(PlaylistSummaryColumnSettings settings)
    {
        if (settings == null)
        {
            return Array.Empty<CustomTableColumn>();
        }
        CustomTableColumn[] columns = CreateAllPlaylistSummaryColumns(settings);
        return columns
            .Where(column => column.IsVisible)
            .OrderBy(column => column.DisplayIndex >= 0 ? column.DisplayIndex : int.MaxValue)
            .ThenBy(column => column.FallbackOrder)
            .ToArray();
    }

    internal static IEnumerable<PlaylistSummaryColumnSettings.ColumnLayout> EnumeratePlaylistSummaryColumnLayouts(PlaylistSummaryColumnSettings settings)
    {
        if (settings == null)
        {
            yield break;
        }
        foreach (CustomTableColumn column in CreateAllPlaylistSummaryColumns(settings))
        {
            if (column.Layout is PlaylistSummaryColumnSettings.ColumnLayout layout)
            {
                yield return layout;
            }
        }
    }

    private static CustomTableColumn[] CreateAllMainColumns(CustomTableColumnSettings settings)
    {
        return new[]
        {
            new CustomTableColumn("Status", "♬", settings.Status, 0, null, TextAlignment.Center, row => GetStatusIconText(row), tooltipSelector: GetStatusTooltip, minWidth: 18, maxWidth: 18, canResize: false, canReorder: false, cellKind: CustomTableCellKind.StatusIcon),
            new CustomTableColumn("EntryLevel", "ENTRY LEVEL", settings.EntryLevel, 1, "EntryLevelSortKey", TextAlignment.Right, row => GetString(row, "Level"), editPropertyName: "Level"),
            new CustomTableColumn("Title", "TITLE", settings.Title, 2, nameof(LibraryChartRow.Title), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 3, nameof(LibraryChartRow.Artist), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Artist)), minWidth: 50),
            new CustomTableColumn("Genre", "GENRE", settings.Genre, 4, "genre", TextAlignment.Left, row => GetString(row, "genre")),
            new CustomTableColumn("Mode", "KEYS", settings.Mode, 5, "mode", TextAlignment.Right, row => FormatSuffix(GetValue(row, "mode"), "KEYS", "?KEYS"), minWidth: 50, maxWidth: 50, canResize: false),
            new CustomTableColumn("Tag", "TAG", settings.Tag, 6, "tag", TextAlignment.Left, row => GetString(row, "tag")),
            new CustomTableColumn("Url1", "URL1", settings.Url1, 7, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon, editPropertyName: nameof(PlaylistDetailRow.Url), editOnRepeatClick: false, editOverlayWidth: 250, editTextSelector: row => GridRowResolver.GetUrl(row)?.ToString()),
            new CustomTableColumn("Url2", "URL2", settings.Url2, 8, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDiffDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlDiffToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon, editPropertyName: nameof(PlaylistDetailRow.Url_diff), editOnRepeatClick: false, editOverlayWidth: 250, editTextSelector: row => GridRowResolver.GetUrlDiff(row)?.ToString()),
            new CustomTableColumn("Warning", "WARNING", settings.Warning, 9, nameof(LibraryChartRow.WarningDigestText), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.WarningDigestText)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.WarningTooltipText))),
            new CustomTableColumn("Comment", "COMMENT", settings.Comment, 10, null, TextAlignment.Left, row => GetString(row, "comment"), tooltipSelector: row => GetString(row, "comment"), editPropertyName: "comment", editTextWrapping: true),
            new CustomTableColumn("Memo", "MEMO", settings.Memo, 11, null, TextAlignment.Left, row => GetString(row, "memo"), tooltipSelector: row => GetString(row, "memo"), editPropertyName: "memo", editTextWrapping: true),
            new CustomTableColumn("Hash", "MD5 HASH", settings.Hash, 12, "hash", TextAlignment.Center, row => GetString(row, "hash"), maxWidth: 240),
            new CustomTableColumn("Sha256", "SHA256 HASH", settings.Sha256, 13, "sha256", TextAlignment.Center, row => GetString(row, "sha256"), maxWidth: 480),
            new CustomTableColumn("Folder", "FOLDER", settings.Folder, 14, "Folder", TextAlignment.Left, row => GetString(row, "Folder"), editPropertyName: "Folder"),
            new CustomTableColumn("Path", "PATH", settings.Path, 15, "path", TextAlignment.Left, row => GetString(row, "path")),
            new CustomTableColumn("InstallDst", "INSTL DST", settings.InstallDst, 16, "instl_dst", TextAlignment.Left, row => GetString(row, "instl_dst"), editPropertyName: "instl_dst", editSuggestionsSelector: GetInstallDestinationSuggestions),
            new CustomTableColumn("InstallDstTitle", Resources.Header_InstallDstTitle, settings.InstallDstTitle, 17, "InstallDestinationTitle", TextAlignment.Left, row => GetString(row, "InstallDestinationTitle")),
            new CustomTableColumn("InstallDstArtist", Resources.Header_InstallDstArtist, settings.InstallDstArtist, 18, "InstallDestinationArtist", TextAlignment.Left, row => GetString(row, "InstallDestinationArtist")),
            new CustomTableColumn("WavHealth", "WAV", settings.WavHealth, 19, "WAVHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "WAVHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("BgaHealth", "BGA", settings.BgaHealth, 20, "BGAHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "BGAHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("MovieHealth", "MOVIE", settings.MovieHealth, 21, "MovieHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "MovieHealth"), "%", string.Empty), maxWidth: 50),
            new CustomTableColumn("CharcterEncoding", "ENCODING", settings.CharcterEncoding, 22, "encoding", TextAlignment.Left, row => GetString(row, "encoding"), maxWidth: 130),
            new CustomTableColumn("PlaylistSymbols", "PLAYLIST", settings.PlaylistSymbols, 23, nameof(LibraryChartRow.RefTablesSymbols), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.RefTablesSymbols)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.RefTablesNames))),
            new CustomTableColumn("Level", "LEVEL", settings.Level, 24, nameof(LibraryChartRow.ChartLevelSortKey), TextAlignment.Right, row => GetString(row, nameof(LibraryChartRow.ChartLevelText)), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartLevelUndefined))),
            new CustomTableColumn("ChartDifficulty", "DIFFICULTY", settings.ChartDifficulty, 25, nameof(LibraryChartRow.ChartDifficultySortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartDifficultyText)), row => GetDifficultyBrush(row), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartDifficultyUndefined)), textStyle: CustomTableTextStyle.Score),
            new CustomTableColumn("ChartMainBpm", "MAINBPM", settings.ChartMainBpm, 26, "ChartMainBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMainBpmText")),
            new CustomTableColumn("ChartMaxBpm", "MAXBPM", settings.ChartMaxBpm, 27, "ChartMaxBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMaxBpmText")),
            new CustomTableColumn("ChartMinBpm", "MINBPM", settings.ChartMinBpm, 28, "ChartMinBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMinBpmText")),
            new CustomTableColumn("ChartDuration", "DURATION", settings.ChartDuration, 29, "ChartDurationSortKey", TextAlignment.Right, row => GetString(row, "ChartDurationText")),
            new CustomTableColumn("ChartJudge", "JUDGE", settings.ChartJudge, 30, nameof(LibraryChartRow.ChartJudgeSortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartJudgeText)), row => GetJudgeBrush(row), textStyle: CustomTableTextStyle.Score),
            new CustomTableColumn("ChartJudgePercent", "JUDGE%", settings.ChartJudgePercent, 31, "ChartJudgeSortKey", TextAlignment.Right, row => GetString(row, "ChartJudgePercentText")),
            new CustomTableColumn("ChartFeature", "FEATURE", settings.ChartFeature, 32, "ChartFeatureSortKey", TextAlignment.Left, row => GetString(row, "ChartFeatureText")),
            new CustomTableColumn("Notes", "NOTES", settings.Notes, 33, "ChartNotes", TextAlignment.Right, row => GetString(row, "ChartNotes")),
            new CustomTableColumn("ChartLongNotes", "LONG", settings.ChartLongNotes, 34, "ChartLongNotes", TextAlignment.Right, row => GetString(row, "ChartLongNotes")),
            new CustomTableColumn("ChartScratchNotes", "SCRATCH", settings.ChartScratchNotes, 35, "ChartScratchNotes", TextAlignment.Right, row => GetString(row, "ChartScratchNotes")),
            new CustomTableColumn("ChartTotal", "TOTAL", settings.ChartTotal, 36, "ChartTotalSortKey", TextAlignment.Right, row => GetString(row, "ChartTotalText"), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartTotalUndefined))),
            new CustomTableColumn("ChartTotalPerNote", "T/N", settings.ChartTotalPerNote, 37, "ChartTotalPerNoteSortKey", TextAlignment.Right, row => GetString(row, "ChartTotalPerNoteText"), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartTotalUndefined))),
            new CustomTableColumn("ChartDensity", "DENSITY", settings.ChartDensity, 38, "ChartDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartDensityText")),
            new CustomTableColumn("ChartPeakDensity", "PEAK", settings.ChartPeakDensity, 39, "ChartPeakDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartPeakDensityText")),
            new CustomTableColumn("ChartEndDensity", "END", settings.ChartEndDensity, 40, "ChartEndDensitySortKey", TextAlignment.Right, row => GetString(row, "ChartEndDensityText")),
            new CustomTableColumn("ChartSoflan", "SOFLAN", settings.ChartSoflan, 41, "ChartSoflanCount", TextAlignment.Right, row => GetString(row, "ChartSoflanCount")),
            new CustomTableColumn("Clear", "CLEAR", settings.Clear, 42, nameof(LibraryChartRow.clear), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ClearDisplayText)), row => GetClearBrush(row), textStyle: CustomTableTextStyle.Score),
            new CustomTableColumn("Rank", "DJ LEVEL", settings.Rank, 43, nameof(LibraryChartRow.rank), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.RankDisplayText)), row => GetRankBrush(row), textStyle: CustomTableTextStyle.Rank),
            new CustomTableColumn("Rate", "RATE", settings.Rate, 44, nameof(LibraryChartRow.rateDouble), TextAlignment.Right, row => FormatPercentTwo(GetValue(row, nameof(LibraryChartRow.rateDouble)))),
            new CustomTableColumn("Score", "SCORE", settings.Score, 45, "score", TextAlignment.Right, row => GetString(row, "score")),
            new CustomTableColumn("Combo", "COMBO", settings.Combo, 46, "maxcombo", TextAlignment.Right, row => GetString(row, "maxcombo")),
            new CustomTableColumn("Bp", "BP", settings.Bp, 47, "minbp", TextAlignment.Right, row => GetString(row, "minbp")),
            new CustomTableColumn("Ranking", "RANKING", settings.Ranking, 48, "rankingString", TextAlignment.Center, row => GetString(row, "rankingString")),
            new CustomTableColumn("RankingLastupdate", "RANK UPDATE", settings.RankingLastupdate, 49, "rankingLastupdate", TextAlignment.Center, row => FormatShortDate(GetValue(row, "rankingLastupdate"))),
            new CustomTableColumn("TScore", "T-SCORE", settings.TScore, 50, "stddevVal", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "stddevVal"))),
            new CustomTableColumn("ScoreDifficulty", "ΔMAX", settings.ScoreDifficulty, 51, "scoreDifficulty", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "scoreDifficulty")))
        };
    }

    private static CustomTableColumn[] CreateAllPlaylistSummaryColumns(PlaylistSummaryColumnSettings settings)
    {
        return new[]
        {
            new CustomTableColumn("PlaylistId", "ID", settings.PlaylistId, 0, nameof(PlaylistSummaryRow.PlaylistId), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.PlaylistId))),
            new CustomTableColumn("Name", "NAME", settings.Name, 1, nameof(PlaylistSummaryRow.Name), TextAlignment.Left, row => GetString(row, nameof(PlaylistSummaryRow.Name))),
            new CustomTableColumn("Symbol", "SYMBOL", settings.Symbol, 2, nameof(PlaylistSummaryRow.Symbol), TextAlignment.Center, row => GetString(row, nameof(PlaylistSummaryRow.Symbol))),
            new CustomTableColumn("LastUpdate", "LAST UPDATE", settings.LastUpdate, 3, nameof(PlaylistSummaryRow.LastUpdate), TextAlignment.Center, row => FormatDateTime(GetValue(row, nameof(PlaylistSummaryRow.LastUpdate)))),
            new CustomTableColumn("TotalCharts", "TOTAL", settings.TotalCharts, 4, nameof(PlaylistSummaryRow.TotalCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.TotalCharts))),
            new CustomTableColumn("OwnedCharts", "OWNED", settings.OwnedCharts, 5, nameof(PlaylistSummaryRow.OwnedCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.OwnedCharts))),
            new CustomTableColumn("MissingCharts", "MISSING", settings.MissingCharts, 6, nameof(PlaylistSummaryRow.MissingCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.MissingCharts))),
            new CustomTableColumn("OwnedRatio", "OWNED %", settings.OwnedRatio, 7, nameof(PlaylistSummaryRow.OwnedRatio), TextAlignment.Right, row => FormatPercentOne(GetValue(row, nameof(PlaylistSummaryRow.OwnedRatio)))),
            new CustomTableColumn("Link", "LINK", settings.Link, 8, null, TextAlignment.Center, row => GetPlaylistSummaryLinkText(row), cellKind: CustomTableCellKind.ActionText),
            new CustomTableColumn("IsExternalSync", "SYNC", settings.IsExternalSync, 9, nameof(PlaylistSummaryRow.IsExternalSync), TextAlignment.Center, row => string.Empty, checkedSelector: row => GetNullableBool(row, nameof(PlaylistSummaryRow.IsExternalSync)), cellKind: CustomTableCellKind.CheckBox),
            new CustomTableColumn("Status", Resources.Playlist_summary_status_header, settings.Status, 10, nameof(PlaylistSummaryRow.StatusSortOrder), TextAlignment.Center, row => GetString(row, nameof(PlaylistSummaryRow.Status)), tooltipSelector: row => GetString(row, nameof(PlaylistSummaryRow.StatusDetail))),
            new CustomTableColumn("IsRootFolder", "ROOT", settings.IsRootFolder, 11, nameof(PlaylistSummaryRow.IsRootFolder), TextAlignment.Center, row => string.Empty, checkedSelector: row => GetNullableBool(row, nameof(PlaylistSummaryRow.IsRootFolder)), cellKind: CustomTableCellKind.CheckBox)
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
            case nameof(LibraryChartRow.WarningDigestText):
                return GetWarningDigestText(row);
            case nameof(LibraryChartRow.WarningTooltipText):
                return GetWarningTooltipText(row);
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

    private static IEnumerable<string> GetInstallDestinationSuggestions(object row)
    {
        return GridRowResolver.GetOperationBmsFile(row)?.InstallDestinationSuggestions;
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

    private static string GetWarningDigestText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.WarningDigestText : row is PlaylistDetailRow playlistRow ? playlistRow.WarningDigestText : GetReflectionString(row, nameof(LibraryChartRow.WarningDigestText));
    }

    private static string GetWarningTooltipText(object row)
    {
        return row is LibraryChartRow libraryRow ? libraryRow.WarningTooltipText : row is PlaylistDetailRow playlistRow ? playlistRow.WarningTooltipText : GetReflectionString(row, nameof(LibraryChartRow.WarningTooltipText));
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

    private static string FormatDateTime(object value)
    {
        return value is DateTime dateTime ? dateTime.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture) : string.Empty;
    }

    private static string FormatPercentOne(object value)
    {
        return value is IFormattable formattable ? formattable.ToString("F1", CultureInfo.CurrentCulture) + "%" : string.Empty;
    }

    private static string FormatPercentTwo(object value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (value is IConvertible convertible)
        {
            double ratio = convertible.ToDouble(CultureInfo.CurrentCulture);
            return (ratio * 100.0).ToString("F2", CultureInfo.CurrentCulture) + "%";
        }
        return string.Empty;
    }

    private static string GetPlaylistSummaryLinkText(object row)
    {
        return row is PlaylistSummaryRow { LinkUri: not null } ? "Open" : string.Empty;
    }

    private static bool? GetNullableBool(object row, string propertyName)
    {
        object value = GetValue(row, propertyName);
        return value is bool flag ? flag : null;
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

    internal static Brush GetUndefinedCellBackground(object row, string propertyName)
    {
        return GetBoolean(row, propertyName) ? UndefinedCellBackgroundBrush : null;
    }

    private static bool GetBoolean(object row, string propertyName)
    {
        if (row == null)
        {
            return false;
        }
        if (row is LibraryChartRow libraryRow)
        {
            switch (propertyName)
            {
                case nameof(LibraryChartRow.ChartLevelUndefined):
                    return libraryRow.ChartLevelUndefined;
                case nameof(LibraryChartRow.ChartDifficultyUndefined):
                    return libraryRow.ChartDifficultyUndefined;
                case nameof(LibraryChartRow.ChartTotalUndefined):
                    return libraryRow.ChartTotalUndefined;
            }
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            switch (propertyName)
            {
                case nameof(LibraryChartRow.ChartLevelUndefined):
                    return playlistRow.ChartLevelUndefined;
                case nameof(LibraryChartRow.ChartDifficultyUndefined):
                    return playlistRow.ChartDifficultyUndefined;
                case nameof(LibraryChartRow.ChartTotalUndefined):
                    return playlistRow.ChartTotalUndefined;
            }
        }
        object value = GetValue(row, propertyName);
        return value is bool flag && flag;
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
        if (row is PlaylistSummaryRow summaryRow)
        {
            return summaryRow.HasFailureStatus;
        }
        object value = row?.GetType().GetProperty(nameof(LibraryChartRow.HasHighlightedWarning))?.GetValue(row, null);
        if (value is bool highlighted)
        {
            return highlighted;
        }
        value = row?.GetType().GetProperty(nameof(PlaylistSummaryRow.HasFailureStatus))?.GetValue(row, null);
        return value is bool failed && failed;
    }
}
