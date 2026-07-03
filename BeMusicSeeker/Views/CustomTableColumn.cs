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
    private static readonly FontFamily DefaultFontFamily = new("Meiryo UI");

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
        Func<object, string, IReadOnlyList<CustomTableTextRunStyle>> textRunSelector = null,
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
        Func<object, IEnumerable<string>> editSuggestionsSelector = null,
        double? tooltipTextWidth = null,
        bool autoTrimTooltip = true)
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
        TextRunSelector = textRunSelector;
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
        TooltipTextWidth = NormalizeTooltipTextWidth(tooltipTextWidth);
        AutoTrimTooltip = autoTrimTooltip;
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

    public double? TooltipTextWidth { get; }

    public bool AutoTrimTooltip { get; }

    public int DisplayIndex => Layout?.DisplayIndex ?? -1;

    public int Width => ClampWidth(Layout?.Width ?? 50);

    public bool IsVisible => Layout == null || Layout.Visibility == Visibility.Visible;

    internal Func<object, string> TextSelector { get; }

    internal Func<object, Brush> ForegroundSelector { get; }

    internal Func<object, Brush> BackgroundSelector { get; }

    internal Func<object, string, IReadOnlyList<CustomTableTextRunStyle>> TextRunSelector { get; }

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

    internal IReadOnlyList<CustomTableTextRunStyle> GetTextRuns(object row)
    {
        return GetTextRuns(row, GetText(row));
    }

    internal IReadOnlyList<CustomTableTextRunStyle> GetTextRuns(object row, string text)
    {
        if (TextRunSelector == null)
        {
            return [];
        }
        IReadOnlyList<CustomTableTextRunStyle> textRuns = TextRunSelector(row, text) ?? [];
        ValidateTextRuns(text, textRuns);
        return textRuns;
    }

    private static void ValidateTextRuns(string text, IReadOnlyList<CustomTableTextRunStyle> textRuns)
    {
        int textLength = text?.Length ?? 0;
        foreach (CustomTableTextRunStyle run in textRuns)
        {
            if (run.StartIndex < 0
                || run.Length < 0
                || run.StartIndex > textLength
                || run.StartIndex + run.Length > textLength)
            {
                throw new InvalidOperationException("Custom table text run is outside the cell text.");
            }
        }
    }

    internal bool? GetChecked(object row)
    {
        return CheckedSelector == null ? null : CheckedSelector(row);
    }

    internal IReadOnlyList<string> GetEditSuggestions(object row)
    {
        return EditSuggestionsSelector == null
            ? []
            : EditSuggestionsSelector(row)?.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() ?? [];
    }

    internal int ClampWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return MinWidth;
        }
        return Math.Max(MinWidth, Math.Min(MaxWidth, (int)Math.Round(width)));
    }

    private static double? NormalizeTooltipTextWidth(double? width)
    {
        if (!width.HasValue || width.Value <= 0d || double.IsNaN(width.Value) || double.IsInfinity(width.Value))
        {
            return null;
        }
        return width.Value;
    }
}

internal static class CustomTableColumnFactory
{
    private const string DownloadIconGlyphText = "\uE14F";
    private const double WrappedTooltipTextWidth = 420d;
    private static Brush UndefinedCellBackgroundBrush => CustomTablePalette.Current.UndefinedCellBackground;

    internal static IReadOnlyList<CustomTableColumn> CreateMainColumns(CustomTableColumnSettings settings)
    {
        if (settings == null)
        {
            return [];
        }
        CustomTableColumn[] columns = settings.Kind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY
            ? CreateAllPlayHistoryColumns(settings)
            : CreateAllMainColumns(settings);
        return [.. columns
            .Where(column => column.IsVisible)
            .OrderBy(column => column.DisplayIndex >= 0 ? column.DisplayIndex : int.MaxValue)
            .ThenBy(column => column.FallbackOrder)];
    }

    internal static IEnumerable<CustomTableColumnSettings.ColumnLayout> EnumerateMainColumnLayouts(CustomTableColumnSettings settings)
    {
        if (settings == null)
        {
            yield break;
        }
        CustomTableColumn[] columns = settings.Kind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY
            ? CreateAllPlayHistoryColumns(settings)
            : CreateAllMainColumns(settings);
        foreach (CustomTableColumn column in columns)
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
            return [];
        }
        CustomTableColumn[] columns = CreateAllPlaylistSummaryColumns(settings);
        return [.. columns
            .Where(column => column.IsVisible)
            .OrderBy(column => column.DisplayIndex >= 0 ? column.DisplayIndex : int.MaxValue)
            .ThenBy(column => column.FallbackOrder)];
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
        return
        [
            new CustomTableColumn("Status", "♬", settings.Status, 0, null, TextAlignment.Center, row => GetStatusIconText(row), tooltipSelector: GetStatusTooltip, minWidth: 18, maxWidth: 18, canResize: false, canReorder: false, cellKind: CustomTableCellKind.StatusIcon, autoTrimTooltip: false),
            new CustomTableColumn("EntryLevel", "ENTRY LEVEL", settings.EntryLevel, 1, "EntryLevelSortKey", TextAlignment.Right, row => GetString(row, "Level"), editPropertyName: "Level"),
            new CustomTableColumn("Title", "TITLE", settings.Title, 2, nameof(LibraryChartRow.Title), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 3, nameof(LibraryChartRow.Artist), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.Artist)), minWidth: 50),
            new CustomTableColumn("Genre", "GENRE", settings.Genre, 4, "genre", TextAlignment.Left, row => GetString(row, "genre")),
            new CustomTableColumn("Mode", "KEYS", settings.Mode, 5, "mode", TextAlignment.Right, row => FormatSuffix(GetValue(row, "mode"), "KEYS", "?KEYS"), minWidth: 50, maxWidth: 50, canResize: false, autoTrimTooltip: false),
            new CustomTableColumn("Tag", "TAG", settings.Tag, 6, "tag", TextAlignment.Left, row => GetString(row, "tag")),
            new CustomTableColumn("Url1", "URL1", settings.Url1, 7, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon, editPropertyName: nameof(PlaylistDetailRow.Url), editOnRepeatClick: false, editOverlayWidth: 250, editTextSelector: row => GridRowResolver.GetUrl(row)?.ToString()),
            new CustomTableColumn("Url2", "URL2", settings.Url2, 8, null, TextAlignment.Center, row => ConvertLigatureSymbolText(GetString(row, nameof(PlaylistDetailRow.UrlDiffDownloadIconText))), tooltipSelector: row => GetString(row, nameof(PlaylistDetailRow.UrlDiffToolTipText)), minWidth: 40, maxWidth: 40, canResize: false, cellKind: CustomTableCellKind.DownloadIcon, editPropertyName: nameof(PlaylistDetailRow.Url_diff), editOnRepeatClick: false, editOverlayWidth: 250, editTextSelector: row => GridRowResolver.GetUrlDiff(row)?.ToString()),
            new CustomTableColumn("Warning", "WARNING", settings.Warning, 9, nameof(LibraryChartRow.WarningDigestText), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.WarningDigestText)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.WarningTooltipText))),
            new CustomTableColumn("Comment", "COMMENT", settings.Comment, 10, null, TextAlignment.Left, row => GetString(row, "comment"), tooltipSelector: row => GetString(row, "comment"), editPropertyName: "comment", editTextWrapping: true, tooltipTextWidth: WrappedTooltipTextWidth),
            new CustomTableColumn("Memo", "MEMO", settings.Memo, 11, null, TextAlignment.Left, row => GetString(row, "memo"), tooltipSelector: row => GetString(row, "memo"), editPropertyName: "memo", editTextWrapping: true, tooltipTextWidth: WrappedTooltipTextWidth),
            new CustomTableColumn("Hash", "MD5 HASH", settings.Hash, 12, "hash", TextAlignment.Center, row => GetString(row, "hash"), maxWidth: 240),
            new CustomTableColumn("Sha256", "SHA256 HASH", settings.Sha256, 13, "sha256", TextAlignment.Center, row => GetString(row, "sha256"), maxWidth: 480),
            new CustomTableColumn("Folder", "FOLDER", settings.Folder, 14, "Folder", TextAlignment.Left, row => GetString(row, "Folder"), editPropertyName: "Folder"),
            new CustomTableColumn("Path", "PATH", settings.Path, 15, "path", TextAlignment.Left, row => GetString(row, "path")),
            new CustomTableColumn("InstallDst", "INSTL DST", settings.InstallDst, 16, "instl_dst", TextAlignment.Left, row => GetString(row, "instl_dst"), editPropertyName: "instl_dst", editSuggestionsSelector: GetInstallDestinationSuggestions),
            new CustomTableColumn("InstallDstTitle", Resources.Header_InstallDstTitle, settings.InstallDstTitle, 17, "InstallDestinationTitle", TextAlignment.Left, row => GetString(row, "InstallDestinationTitle")),
            new CustomTableColumn("InstallDstArtist", Resources.Header_InstallDstArtist, settings.InstallDstArtist, 18, "InstallDestinationArtist", TextAlignment.Left, row => GetString(row, "InstallDestinationArtist")),
            new CustomTableColumn("WavHealth", "WAV", settings.WavHealth, 19, "WAVHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "WAVHealth"), "%", string.Empty), maxWidth: 50, autoTrimTooltip: false),
            new CustomTableColumn("BgaHealth", "BGA", settings.BgaHealth, 20, "BGAHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "BGAHealth"), "%", string.Empty), maxWidth: 50, autoTrimTooltip: false),
            new CustomTableColumn("MovieHealth", "MOVIE", settings.MovieHealth, 21, "MovieHealth", TextAlignment.Right, row => FormatSuffix(GetValue(row, "MovieHealth"), "%", string.Empty), maxWidth: 50, autoTrimTooltip: false),
            new CustomTableColumn("CharcterEncoding", "ENCODING", settings.CharcterEncoding, 22, "encoding", TextAlignment.Left, row => GetString(row, "encoding"), maxWidth: 130),
            new CustomTableColumn("PlaylistSymbols", "PLAYLIST", settings.PlaylistSymbols, 23, nameof(LibraryChartRow.RefTablesSymbols), TextAlignment.Left, row => GetString(row, nameof(LibraryChartRow.RefTablesSymbols)), tooltipSelector: row => GetString(row, nameof(LibraryChartRow.RefTablesNames))),
            new CustomTableColumn("Level", "LEVEL", settings.Level, 24, nameof(LibraryChartRow.ChartLevelSortKey), TextAlignment.Right, row => GetString(row, nameof(LibraryChartRow.ChartLevelText)), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartLevelUndefined))),
            new CustomTableColumn("ChartDifficulty", "DIFFICULTY", settings.ChartDifficulty, 25, nameof(LibraryChartRow.ChartDifficultySortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartDifficultyText)), row => GetDifficultyBrush(row), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(LibraryChartRow.ChartDifficultyUndefined)), textStyle: CustomTableTextStyle.Score, autoTrimTooltip: false),
            new CustomTableColumn("ChartMainBpm", "MAINBPM", settings.ChartMainBpm, 26, "ChartMainBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMainBpmText")),
            new CustomTableColumn("ChartMaxBpm", "MAXBPM", settings.ChartMaxBpm, 27, "ChartMaxBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMaxBpmText")),
            new CustomTableColumn("ChartMinBpm", "MINBPM", settings.ChartMinBpm, 28, "ChartMinBpmSortKey", TextAlignment.Right, row => GetString(row, "ChartMinBpmText")),
            new CustomTableColumn("ChartDuration", "DURATION", settings.ChartDuration, 29, "ChartDurationSortKey", TextAlignment.Right, row => GetString(row, "ChartDurationText")),
            new CustomTableColumn("ChartJudge", "JUDGE", settings.ChartJudge, 30, nameof(LibraryChartRow.ChartJudgeSortKey), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ChartJudgeText)), row => GetJudgeBrush(row), textStyle: CustomTableTextStyle.Score, autoTrimTooltip: false),
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
            new CustomTableColumn("Clear", "CLEAR", settings.Clear, 42, nameof(LibraryChartRow.clear), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.ClearDisplayText)), row => GetClearBrush(row), textStyle: CustomTableTextStyle.Score, autoTrimTooltip: false),
            new CustomTableColumn("Rank", "DJ LEVEL", settings.Rank, 43, nameof(LibraryChartRow.rank), TextAlignment.Center, row => GetString(row, nameof(LibraryChartRow.RankDisplayText)), row => GetRankBrush(row), textStyle: CustomTableTextStyle.Rank, autoTrimTooltip: false),
            new CustomTableColumn("Rate", "RATE", settings.Rate, 44, nameof(LibraryChartRow.rateDouble), TextAlignment.Right, row => FormatPercentTwo(GetValue(row, nameof(LibraryChartRow.rateDouble))), autoTrimTooltip: false),
            new CustomTableColumn("Score", "SCORE", settings.Score, 45, "score", TextAlignment.Right, row => GetString(row, "score")),
            new CustomTableColumn("Combo", "COMBO", settings.Combo, 46, "maxcombo", TextAlignment.Right, row => GetString(row, "maxcombo")),
            new CustomTableColumn("Bp", "BP", settings.Bp, 47, "minbp", TextAlignment.Right, row => GetString(row, "minbp")),
            new CustomTableColumn("Ranking", "RANKING", settings.Ranking, 48, "rankingString", TextAlignment.Center, row => GetString(row, "rankingString")),
            new CustomTableColumn("RankingLastupdate", "RANK UPDATE", settings.RankingLastupdate, 49, "rankingLastupdate", TextAlignment.Center, row => FormatShortDate(GetValue(row, "rankingLastupdate"))),
            new CustomTableColumn("TScore", "T-SCORE", settings.TScore, 50, "stddevVal", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "stddevVal"))),
            new CustomTableColumn("ScoreDifficulty", "ΔMAX", settings.ScoreDifficulty, 51, "scoreDifficulty", TextAlignment.Right, row => FormatFixedTwo(GetValue(row, "scoreDifficulty")))
        ];
    }

    private static CustomTableColumn[] CreateAllPlayHistoryColumns(CustomTableColumnSettings settings)
    {
        return
        [
            new CustomTableColumn("PlayedAt", "DATE", settings.PlayHistoryPlayedAt, 0, nameof(PlayHistoryRow.PlayedAt), TextAlignment.Center, row => FormatDateTime(GetValue(row, nameof(PlayHistoryRow.PlayedAt))), minWidth: 120),
            new CustomTableColumn("FolderLabels", "FOLDER", settings.PlayHistoryFolderLabels, 1, nameof(PlayHistoryRow.FolderLabels), TextAlignment.Left, row => GetString(row, nameof(PlayHistoryRow.FolderLabels)), tooltipSelector: row => GetString(row, nameof(PlayHistoryRow.PlaylistNames))),
            new CustomTableColumn("Title", "TITLE", settings.Title, 2, nameof(PlayHistoryRow.Title), TextAlignment.Left, row => GetString(row, nameof(PlayHistoryRow.Title))),
            new CustomTableColumn("Artist", "ARTIST", settings.Artist, 3, nameof(PlayHistoryRow.Artist), TextAlignment.Left, row => GetString(row, nameof(PlayHistoryRow.Artist))),
            new CustomTableColumn("BestClear", "CLEAR", settings.PlayHistoryBestClear, 4, "BestClear", TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.BestClear)), row => GetPlayHistoryBestClearBrush(row), textRunSelector: GetPlayHistoryBestClearTextRuns, textStyle: CustomTableTextStyle.Score),
            new CustomTableColumn("BestDjLevel", "BEST DJ", settings.PlayHistoryBestDjLevel, 5, "BestDjLevel", TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.BestDjLevelText)), row => GetPlayHistoryBestDjLevelBrush(row), textRunSelector: GetPlayHistoryBestDjLevelTextRuns, textStyle: CustomTableTextStyle.Score),
            new CustomTableColumn("BestRate", "BEST RATE", settings.PlayHistoryBestRate, 6, "BestRate", TextAlignment.Right, row => GetString(row, nameof(PlayHistoryRow.BestRateText))),
            new CustomTableColumn("BestExscore", "BEST EXSCORE", settings.PlayHistoryBestExscore, 7, "BestExscore", TextAlignment.Right, row => GetString(row, nameof(PlayHistoryRow.BestExscore))),
            new CustomTableColumn("BestBp", "BP", settings.PlayHistoryBestBp, 8, "BestBp", TextAlignment.Right, row => GetString(row, nameof(PlayHistoryRow.BestBp))),
            new CustomTableColumn("BestCombo", "COMBO", settings.PlayHistoryBestCombo, 9, "BestCombo", TextAlignment.Right, row => GetString(row, nameof(PlayHistoryRow.BestCombo))),
            new CustomTableColumn("Kind", "TYPE", settings.PlayHistoryKind, 10, nameof(PlayHistoryRow.Kind), TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.Kind))),
            new CustomTableColumn("Option", "OPTION", settings.PlayHistoryOption, 11, nameof(PlayHistoryRow.Option), TextAlignment.Left, row => GetString(row, nameof(PlayHistoryRow.Option))),
            new CustomTableColumn("OpHistory", "OP HISTORY", settings.PlayHistoryOpHistory, 12, "OpHistory", TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.OpHistory))),
            new CustomTableColumn("PlayExscore", "PLAY EXSCORE", settings.PlayHistoryPlayExscore, 13, "PlayExscore", TextAlignment.Right, row => GetString(row, nameof(PlayHistoryRow.PlayExscore))),
            new CustomTableColumn("Judges", "JUDGES", settings.PlayHistoryJudges, 14, "JudgeTotal", TextAlignment.Left, row => GetString(row, nameof(PlayHistoryRow.Judges))),
            new CustomTableColumn("Sha256", "SHA256", settings.Sha256, 15, nameof(PlayHistoryRow.Sha256), TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.Sha256)), maxWidth: 480),
            new CustomTableColumn("RawHash", "RAW HASH", settings.PlayHistoryRawHash, 18, nameof(PlayHistoryRow.RawHash), TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.RawHash)), maxWidth: 240),
            new CustomTableColumn("Finalized", "FINALIZED", settings.PlayHistoryFinalized, 19, nameof(PlayHistoryRow.Finalized), TextAlignment.Center, row => GetString(row, nameof(PlayHistoryRow.Finalized)))
        ];
    }

    private static CustomTableColumn[] CreateAllPlaylistSummaryColumns(PlaylistSummaryColumnSettings settings)
    {
        return
        [
            new CustomTableColumn("PlaylistId", "ID", settings.PlaylistId, 0, nameof(PlaylistSummaryRow.PlaylistId), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.PlaylistId)), autoTrimTooltip: false),
            new CustomTableColumn("OutputBase", "OUTPUT", settings.OutputBase, 1, nameof(PlaylistSummaryRow.OutputBaseDisplayName), TextAlignment.Left, row => GetString(row, nameof(PlaylistSummaryRow.OutputBaseDisplayName)), autoTrimTooltip: false),
            new CustomTableColumn("Name", "NAME", settings.Name, 2, nameof(PlaylistSummaryRow.Name), TextAlignment.Left, row => GetString(row, nameof(PlaylistSummaryRow.Name)), editPropertyName: nameof(PlaylistSummaryRow.Name)),
            new CustomTableColumn("FolderName", "FOLDER NAME", settings.FolderName, 3, nameof(PlaylistSummaryRow.FolderName), TextAlignment.Left, row => GetString(row, nameof(PlaylistSummaryRow.FolderName)), backgroundSelector: row => GetUndefinedCellBackground(row, nameof(PlaylistSummaryRow.FolderNameUndefined)), editPropertyName: nameof(PlaylistSummaryRow.FolderName)),
            new CustomTableColumn("CompatPrefix", "PREFIX", settings.CompatPrefix, 4, nameof(PlaylistSummaryRow.CompatPrefix), TextAlignment.Left, row => GetString(row, nameof(PlaylistSummaryRow.CompatPrefix)), editPropertyName: nameof(PlaylistSummaryRow.CompatPrefix), autoTrimTooltip: false),
            new CustomTableColumn("Symbol", "SYMBOL", settings.Symbol, 5, nameof(PlaylistSummaryRow.Symbol), TextAlignment.Center, row => GetString(row, nameof(PlaylistSummaryRow.Symbol)), editPropertyName: nameof(PlaylistSummaryRow.Symbol), autoTrimTooltip: false),
            new CustomTableColumn("LastUpdate", "LAST UPDATE", settings.LastUpdate, 6, nameof(PlaylistSummaryRow.LastUpdate), TextAlignment.Center, row => FormatDateTime(GetValue(row, nameof(PlaylistSummaryRow.LastUpdate)))),
            new CustomTableColumn("TotalCharts", "TOTAL", settings.TotalCharts, 7, nameof(PlaylistSummaryRow.TotalCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.TotalCharts))),
            new CustomTableColumn("OwnedCharts", "OWNED", settings.OwnedCharts, 8, nameof(PlaylistSummaryRow.OwnedCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.OwnedCharts))),
            new CustomTableColumn("MissingCharts", "MISSING", settings.MissingCharts, 9, nameof(PlaylistSummaryRow.MissingCharts), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.MissingCharts))),
            new CustomTableColumn("OwnedRatio", "OWNED %", settings.OwnedRatio, 10, nameof(PlaylistSummaryRow.OwnedRatio), TextAlignment.Right, row => FormatPercentOne(GetValue(row, nameof(PlaylistSummaryRow.OwnedRatio)))),
            new CustomTableColumn("Link", "LINK", settings.Link, 11, null, TextAlignment.Center, row => GetPlaylistSummaryLinkText(row), tooltipSelector: GetPlaylistSummaryLinkTooltip, cellKind: CustomTableCellKind.ActionText, editTextSelector: GetPlaylistSummaryLinkTooltip),
            new CustomTableColumn("Header", "HEADER", settings.Header, 12, nameof(PlaylistSummaryRow.HeaderUriText), TextAlignment.Center, row => GetPlaylistSummaryHeaderText(row), tooltipSelector: GetPlaylistSummaryHeaderTooltip, cellKind: CustomTableCellKind.ActionText, editTextSelector: GetPlaylistSummaryHeaderTooltip),
            new CustomTableColumn("Data", "DATA", settings.Data, 13, nameof(PlaylistSummaryRow.DataUriText), TextAlignment.Center, row => GetPlaylistSummaryDataText(row), tooltipSelector: GetPlaylistSummaryDataTooltip, cellKind: CustomTableCellKind.ActionText, editTextSelector: GetPlaylistSummaryDataTooltip),
            new CustomTableColumn("IsExternalSync", "SYNC", settings.IsExternalSync, 14, nameof(PlaylistSummaryRow.IsExternalSync), TextAlignment.Center, row => string.Empty, checkedSelector: row => GetNullableBool(row, nameof(PlaylistSummaryRow.IsExternalSync)), cellKind: CustomTableCellKind.CheckBox, autoTrimTooltip: false),
            new CustomTableColumn("Status", Resources.Playlist_summary_status_header, settings.Status, 15, nameof(PlaylistSummaryRow.StatusSortOrder), TextAlignment.Center, row => GetString(row, nameof(PlaylistSummaryRow.Status)), tooltipSelector: row => GetString(row, nameof(PlaylistSummaryRow.StatusDetail))),
            new CustomTableColumn("IsRootFolder", "ROOT", settings.IsRootFolder, 16, nameof(PlaylistSummaryRow.IsRootFolder), TextAlignment.Center, row => string.Empty, checkedSelector: row => GetNullableBool(row, nameof(PlaylistSummaryRow.IsRootFolder)), cellKind: CustomTableCellKind.CheckBox, autoTrimTooltip: false),
            new CustomTableColumn("BmtSort", "BMT SORT", settings.BmtSort, 17, nameof(PlaylistSummaryRow.BmtSort), TextAlignment.Right, row => GetString(row, nameof(PlaylistSummaryRow.BmtSort)), autoTrimTooltip: false),
            new CustomTableColumn("IsBmtOutput", "BMT OUTPUT", settings.IsBmtOutput, 18, nameof(PlaylistSummaryRow.IsBmtOutput), TextAlignment.Center, row => string.Empty, checkedSelector: row => GetNullableBool(row, nameof(PlaylistSummaryRow.IsBmtOutput)), cellKind: CustomTableCellKind.CheckBox, autoTrimTooltip: false)
        ];
    }

    private static string GetString(object row, string propertyName)
    {
        if (row == null)
        {
            return string.Empty;
        }
        return propertyName switch
        {
            nameof(LibraryChartRow.Title) => GetTitle(row),
            nameof(LibraryChartRow.Artist) => GetArtist(row),
            "path" => GetPath(row),
            nameof(LibraryChartRow.DisplayWarning) => GetDisplayWarning(row),
            nameof(LibraryChartRow.WarningDigestText) => GetWarningDigestText(row),
            nameof(LibraryChartRow.WarningTooltipText) => GetWarningTooltipText(row),
            nameof(LibraryChartRow.RefTablesSymbols) => GetRefTablesSymbols(row),
            nameof(LibraryChartRow.RefTablesNames) => GetRefTablesNames(row),
            nameof(LibraryChartRow.ClearDisplayText) => GetClearDisplayText(row),
            nameof(LibraryChartRow.RankDisplayText) => GetRankDisplayText(row),
            nameof(LibraryChartRow.ChartLevelText) => GetChartLevelText(row),
            nameof(LibraryChartRow.ChartDifficultyText) => GetChartDifficultyText(row),
            nameof(LibraryChartRow.ChartJudgeText) => GetChartJudgeText(row),
            _ => GetReflectionString(row, propertyName),
        };
    }

    private static IEnumerable<string> GetInstallDestinationSuggestions(object row)
    {
        return GridRowResolver.TryGetChartFile(row, out ChartFile chart)
            ? chart.InstallDestinationSuggestions
            : [];
    }

    internal static string ConvertLigatureSymbolText(string text)
    {
        return string.Equals(text, "download", StringComparison.Ordinal) ? DownloadIconGlyphText : text;
    }

    internal static string ConvertStatusToIconText(ChartFileStatus status)
    {
        return ConvertStatusToIconKind(status).ToString();
    }

    internal static CustomTableStatusIconKind ConvertStatusToIconKind(ChartFileStatus status)
    {
        if (status == ChartFileStatus.NONE)
        {
            return CustomTableStatusIconKind.None;
        }
        if (status.HasFlag(ChartFileStatus.FORWARD))
        {
            return CustomTableStatusIconKind.Forward;
        }
        if (status.HasFlag(ChartFileStatus.BACKWARD))
        {
            return CustomTableStatusIconKind.Backward;
        }
        if (status.HasFlag(ChartFileStatus.PLAY))
        {
            return CustomTableStatusIconKind.Play;
        }
        if (status.HasFlag(ChartFileStatus.LOADING))
        {
            return CustomTableStatusIconKind.Loading;
        }
        if (status.HasFlag(ChartFileStatus.PAUSE))
        {
            return CustomTableStatusIconKind.Pause;
        }
        if (status.HasFlag(ChartFileStatus.SEARCHING))
        {
            return CustomTableStatusIconKind.Searching;
        }
        if (status.HasFlag(ChartFileStatus.SCORE_UNSENT))
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
        ChartFileStatus status = GetStatus(row);
        if (status == ChartFileStatus.NONE)
        {
            return null;
        }
        if (status.HasFlag(ChartFileStatus.PLAY))
        {
            return Resources.Tooltip_play;
        }
        if (status.HasFlag(ChartFileStatus.LOADING))
        {
            return Resources.Tooltip_loading;
        }
        if (status.HasFlag(ChartFileStatus.PAUSE))
        {
            return Resources.Tooltip_pause;
        }
        if (status.HasFlag(ChartFileStatus.FORWARD))
        {
            return Resources.Tooltip_fast_forward;
        }
        if (status.HasFlag(ChartFileStatus.BACKWARD))
        {
            return Resources.Tooltip_rewind;
        }
        if (status.HasFlag(ChartFileStatus.SEARCHING))
        {
            return Resources.Tooltip_searching;
        }
        if (status.HasFlag(ChartFileStatus.SCORE_UNSENT))
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
        if (row is PlayHistoryRow playHistoryRow)
        {
            return propertyName switch
            {
                nameof(PlayHistoryRow.PlayedAt) => playHistoryRow.PlayedAt,
                nameof(PlayHistoryRow.FolderLabels) => playHistoryRow.FolderLabels,
                nameof(PlayHistoryRow.PlaylistNames) => playHistoryRow.PlaylistNames,
                nameof(PlayHistoryRow.Title) => playHistoryRow.Title,
                nameof(PlayHistoryRow.Artist) => playHistoryRow.Artist,
                nameof(PlayHistoryRow.BestClear) => playHistoryRow.BestClear,
                nameof(PlayHistoryRow.BestDjLevel) => playHistoryRow.BestDjLevel,
                nameof(PlayHistoryRow.BestDjLevelText) => playHistoryRow.BestDjLevelText,
                nameof(PlayHistoryRow.BestRate) => playHistoryRow.BestRate,
                nameof(PlayHistoryRow.BestRateText) => playHistoryRow.BestRateText,
                nameof(PlayHistoryRow.BestBp) => playHistoryRow.BestBp,
                nameof(PlayHistoryRow.BestCombo) => playHistoryRow.BestCombo,
                nameof(PlayHistoryRow.Kind) => playHistoryRow.Kind,
                nameof(PlayHistoryRow.OpHistory) => playHistoryRow.OpHistory,
                nameof(PlayHistoryRow.BestExscore) => playHistoryRow.BestExscore,
                nameof(PlayHistoryRow.PlayExscore) => playHistoryRow.PlayExscore,
                nameof(PlayHistoryRow.Judges) => playHistoryRow.Judges,
                nameof(PlayHistoryRow.JudgeTotal) => playHistoryRow.JudgeTotal,
                nameof(PlayHistoryRow.Option) => playHistoryRow.Option,
                nameof(PlayHistoryRow.Sha256) => playHistoryRow.Sha256,
                nameof(PlayHistoryRow.Provider) => playHistoryRow.Provider,
                nameof(PlayHistoryRow.Source) => playHistoryRow.Source,
                nameof(PlayHistoryRow.SourcePath) => playHistoryRow.SourcePath,
                nameof(PlayHistoryRow.RawHash) => playHistoryRow.RawHash,
                nameof(PlayHistoryRow.Finalized) => playHistoryRow.Finalized,
                _ => row?.GetType().GetProperty(propertyName)?.GetValue(row, null),
            };
        }
        if (row is LibraryChartRow libraryRow)
        {
            return propertyName switch
            {
                nameof(LibraryChartRow.Title) => libraryRow.Title,
                nameof(LibraryChartRow.Artist) => libraryRow.Artist,
                nameof(LibraryChartRow.genre) => libraryRow.genre,
                nameof(LibraryChartRow.mode) => libraryRow.mode,
                nameof(LibraryChartRow.tag) => libraryRow.tag,
                nameof(LibraryChartRow.Level) => libraryRow.Level,
                nameof(LibraryChartRow.hash) => libraryRow.hash,
                nameof(LibraryChartRow.sha256) => libraryRow.sha256,
                nameof(LibraryChartRow.Folder) => libraryRow.Folder,
                nameof(LibraryChartRow.path) => libraryRow.path,
                nameof(LibraryChartRow.instl_dst) => libraryRow.instl_dst,
                nameof(LibraryChartRow.InstallDestinationTitle) => libraryRow.InstallDestinationTitle,
                nameof(LibraryChartRow.InstallDestinationArtist) => libraryRow.InstallDestinationArtist,
                nameof(LibraryChartRow.WAVHealth) => libraryRow.WAVHealth,
                nameof(LibraryChartRow.BGAHealth) => libraryRow.BGAHealth,
                nameof(LibraryChartRow.MovieHealth) => libraryRow.MovieHealth,
                nameof(LibraryChartRow.encoding) => libraryRow.encoding,
                nameof(LibraryChartRow.ClearDisplayText) => libraryRow.ClearDisplayText,
                nameof(LibraryChartRow.RankDisplayText) => libraryRow.RankDisplayText,
                nameof(LibraryChartRow.rateDouble) => libraryRow.rateDouble,
                nameof(LibraryChartRow.score) => libraryRow.score,
                nameof(LibraryChartRow.maxcombo) => libraryRow.maxcombo,
                nameof(LibraryChartRow.minbp) => libraryRow.minbp,
                nameof(LibraryChartRow.rankingString) => libraryRow.rankingString,
                nameof(LibraryChartRow.rankingLastupdate) => libraryRow.rankingLastupdate,
                nameof(LibraryChartRow.stddevVal) => libraryRow.stddevVal,
                nameof(LibraryChartRow.scoreDifficulty) => libraryRow.scoreDifficulty,
                nameof(LibraryChartRow.ChartMainBpmText) => libraryRow.ChartMainBpmText,
                nameof(LibraryChartRow.ChartMaxBpmText) => libraryRow.ChartMaxBpmText,
                nameof(LibraryChartRow.ChartMinBpmText) => libraryRow.ChartMinBpmText,
                nameof(LibraryChartRow.ChartDurationText) => libraryRow.ChartDurationText,
                nameof(LibraryChartRow.ChartJudgePercentText) => libraryRow.ChartJudgePercentText,
                nameof(LibraryChartRow.ChartFeatureText) => libraryRow.ChartFeatureText,
                nameof(LibraryChartRow.ChartNotes) => libraryRow.ChartNotes,
                nameof(LibraryChartRow.ChartLongNotes) => libraryRow.ChartLongNotes,
                nameof(LibraryChartRow.ChartScratchNotes) => libraryRow.ChartScratchNotes,
                nameof(LibraryChartRow.ChartTotalText) => libraryRow.ChartTotalText,
                nameof(LibraryChartRow.ChartTotalPerNoteText) => libraryRow.ChartTotalPerNoteText,
                nameof(LibraryChartRow.ChartDensityText) => libraryRow.ChartDensityText,
                nameof(LibraryChartRow.ChartPeakDensityText) => libraryRow.ChartPeakDensityText,
                nameof(LibraryChartRow.ChartEndDensityText) => libraryRow.ChartEndDensityText,
                nameof(LibraryChartRow.ChartSoflanCount) => libraryRow.ChartSoflanCount,
                nameof(LibraryChartRow.status) => libraryRow.status,
                _ => row?.GetType().GetProperty(propertyName)?.GetValue(row, null),
            };
        }
        if (row is PlaylistDetailRow playlistRow)
        {
            return propertyName switch
            {
                nameof(LibraryChartRow.Title) => playlistRow.Title,
                nameof(LibraryChartRow.Artist) => playlistRow.Artist,
                nameof(LibraryChartRow.genre) => playlistRow.genre,
                nameof(LibraryChartRow.mode) => playlistRow.mode,
                nameof(LibraryChartRow.tag) => playlistRow.tag,
                nameof(LibraryChartRow.Level) => playlistRow.Level,
                nameof(LibraryChartRow.hash) => playlistRow.hash,
                nameof(LibraryChartRow.sha256) => playlistRow.sha256,
                nameof(LibraryChartRow.Folder) => playlistRow.Folder,
                nameof(LibraryChartRow.path) => playlistRow.path,
                nameof(LibraryChartRow.instl_dst) => playlistRow.instl_dst,
                nameof(LibraryChartRow.InstallDestinationTitle) => playlistRow.InstallDestinationTitle,
                nameof(LibraryChartRow.InstallDestinationArtist) => playlistRow.InstallDestinationArtist,
                nameof(LibraryChartRow.WAVHealth) => playlistRow.WAVHealth,
                nameof(LibraryChartRow.BGAHealth) => playlistRow.BGAHealth,
                nameof(LibraryChartRow.MovieHealth) => playlistRow.MovieHealth,
                nameof(LibraryChartRow.encoding) => playlistRow.encoding,
                nameof(LibraryChartRow.ClearDisplayText) => playlistRow.ClearDisplayText,
                nameof(LibraryChartRow.RankDisplayText) => playlistRow.RankDisplayText,
                nameof(LibraryChartRow.rateDouble) => playlistRow.rateDouble,
                nameof(LibraryChartRow.score) => playlistRow.score,
                nameof(LibraryChartRow.maxcombo) => playlistRow.maxcombo,
                nameof(LibraryChartRow.minbp) => playlistRow.minbp,
                nameof(LibraryChartRow.rankingString) => playlistRow.rankingString,
                nameof(LibraryChartRow.rankingLastupdate) => playlistRow.rankingLastupdate,
                nameof(LibraryChartRow.stddevVal) => playlistRow.stddevVal,
                nameof(LibraryChartRow.scoreDifficulty) => playlistRow.scoreDifficulty,
                nameof(LibraryChartRow.ChartMainBpmText) => playlistRow.ChartMainBpmText,
                nameof(LibraryChartRow.ChartMaxBpmText) => playlistRow.ChartMaxBpmText,
                nameof(LibraryChartRow.ChartMinBpmText) => playlistRow.ChartMinBpmText,
                nameof(LibraryChartRow.ChartDurationText) => playlistRow.ChartDurationText,
                nameof(LibraryChartRow.ChartJudgePercentText) => playlistRow.ChartJudgePercentText,
                nameof(LibraryChartRow.ChartFeatureText) => playlistRow.ChartFeatureText,
                nameof(LibraryChartRow.ChartNotes) => playlistRow.ChartNotes,
                nameof(LibraryChartRow.ChartLongNotes) => playlistRow.ChartLongNotes,
                nameof(LibraryChartRow.ChartScratchNotes) => playlistRow.ChartScratchNotes,
                nameof(LibraryChartRow.ChartTotalText) => playlistRow.ChartTotalText,
                nameof(LibraryChartRow.ChartTotalPerNoteText) => playlistRow.ChartTotalPerNoteText,
                nameof(LibraryChartRow.ChartDensityText) => playlistRow.ChartDensityText,
                nameof(LibraryChartRow.ChartPeakDensityText) => playlistRow.ChartPeakDensityText,
                nameof(LibraryChartRow.ChartEndDensityText) => playlistRow.ChartEndDensityText,
                nameof(LibraryChartRow.ChartSoflanCount) => playlistRow.ChartSoflanCount,
                nameof(LibraryChartRow.status) => playlistRow.status,
                _ => row?.GetType().GetProperty(propertyName)?.GetValue(row, null),
            };
        }
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

    private static string GetPlaylistSummaryLinkTooltip(object row)
    {
        return row is PlaylistSummaryRow { LinkUri: not null } summaryRow ? summaryRow.LinkUri.ToString() : null;
    }

    private static string GetPlaylistSummaryHeaderText(object row)
    {
        return row is PlaylistSummaryRow { HeaderUri: not null } ? "Open" : string.Empty;
    }

    private static string GetPlaylistSummaryHeaderTooltip(object row)
    {
        return row is PlaylistSummaryRow { HeaderUri: not null } summaryRow ? summaryRow.HeaderUri.ToString() : null;
    }

    private static string GetPlaylistSummaryDataText(object row)
    {
        return row is PlaylistSummaryRow { DataUri: not null } ? "Open" : string.Empty;
    }

    private static string GetPlaylistSummaryDataTooltip(object row)
    {
        return row is PlaylistSummaryRow { DataUri: not null } summaryRow ? summaryRow.DataUri.ToString() : null;
    }

    private static bool? GetNullableBool(object row, string propertyName)
    {
        object value = GetValue(row, propertyName);
        return value is bool flag ? flag : null;
    }

    private static ChartFileStatus GetStatus(object row)
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
        return value is ChartFileStatus chartStatus ? chartStatus : ChartFileStatus.NONE;
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

    private static Brush GetPlayHistoryBestClearBrush(object row)
    {
        return row is PlayHistoryRow playHistoryRow && playHistoryRow.NewBestClear.HasValue
            ? CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertClear(playHistoryRow.NewBestClear.Value), CustomTableScoreBrushProvider.DefaultForeground)
            : CustomTableScoreBrushProvider.DefaultForeground;
    }

    private static Brush GetPlayHistoryBestDjLevelBrush(object row)
    {
        return row is PlayHistoryRow playHistoryRow
            ? CustomTableScoreBrushProvider.ResolveBrush(CustomTableScoreBrushProvider.ConvertRank(playHistoryRow.BestDjLevel), CustomTableScoreBrushProvider.DefaultForeground)
            : CustomTableScoreBrushProvider.DefaultForeground;
    }

    private static IReadOnlyList<CustomTableTextRunStyle> GetPlayHistoryBestClearTextRuns(object row, string text)
    {
        if (row is not PlayHistoryRow playHistoryRow || string.IsNullOrEmpty(text) || !playHistoryRow.NewBestClear.HasValue)
        {
            return [];
        }
        return CreateTransitionTextRuns(
            text,
            CustomTableScoreBrushProvider.ResolveBrush(
                CustomTableScoreBrushProvider.ConvertClear(playHistoryRow.OldBestClear ?? ClearType.NO_PLAY),
                CustomTableScoreBrushProvider.DefaultForeground),
            CustomTableScoreBrushProvider.ResolveBrush(
                CustomTableScoreBrushProvider.ConvertClear(playHistoryRow.NewBestClear.Value),
                CustomTableScoreBrushProvider.DefaultForeground));
    }

    private static IReadOnlyList<CustomTableTextRunStyle> GetPlayHistoryBestDjLevelTextRuns(object row, string text)
    {
        if (row is not PlayHistoryRow playHistoryRow || string.IsNullOrEmpty(text))
        {
            return [];
        }
        return CreateTransitionTextRuns(
            text,
            CustomTableScoreBrushProvider.ResolveBrush(
                CustomTableScoreBrushProvider.ConvertRank(playHistoryRow.OldBestDjLevel),
                CustomTableScoreBrushProvider.DefaultForeground),
            CustomTableScoreBrushProvider.ResolveBrush(
                CustomTableScoreBrushProvider.ConvertRank(playHistoryRow.BestDjLevel),
                CustomTableScoreBrushProvider.DefaultForeground));
    }

    private static IReadOnlyList<CustomTableTextRunStyle> CreateTransitionTextRuns(string text, Brush oldBrush, Brush newBrush)
    {
        const string Separator = " -> ";
        int separatorIndex = text.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return [new CustomTableTextRunStyle(0, text.Length, newBrush)];
        }
        return
        [
            new CustomTableTextRunStyle(0, separatorIndex, oldBrush),
            new CustomTableTextRunStyle(separatorIndex, Separator.Length, CustomTableScoreBrushProvider.GrayBrush),
            new CustomTableTextRunStyle(separatorIndex + Separator.Length, text.Length - separatorIndex - Separator.Length, newBrush)
        ];
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
