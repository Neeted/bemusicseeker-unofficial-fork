using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト詳細表示の source snapshot 1 行分を表します。
/// keyword/mode/sort 用の軽量な元データとして保持し、UI へは <see cref="PlaylistDetailRow"/> を生成して渡します。
/// </summary>
internal sealed class PlaylistDetailSourceRow
{
    private static readonly Regex levelParseRegex = new("([+-]?\\d+(\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

    /// <summary>
    /// プレイリストエントリ本体です。
    /// </summary>
    internal BMSTableEntry Entry { get; }

    private readonly ChartFile resolvedChartSnapshot;

    private readonly LR2SongDBExtended.bmson_song resolvedBmson;

    private readonly Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider;

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned => HasOwnedChart(Chart);

    internal string Title { get; }

    internal string Artist { get; }

    internal string genre { get; }

    internal int? mode { get; }

    internal string tag { get; }

    internal Uri Url { get; private set; }

    internal Uri Url_diff { get; private set; }

    internal string name_diff { get; }

    internal bool HasZeroNoteMismatchWarning { get; }

    internal bool HasHighlightedWarning { get; }

    internal string DisplayWarning { get; }

    internal string WarningDigestText { get; }

    internal string WarningTooltipText { get; }

    internal string comment { get; private set; }

    internal string memo { get; private set; }

    internal string hash { get; }

    internal string sha256 { get; private set; }

    internal string Folder { get; }

    internal string path { get; }

    internal string instl_dst { get; }

    internal string InstallDestinationTitle { get; }

    internal string InstallDestinationArtist { get; }

    internal int? WAVHealth { get; }

    internal int? BGAHealth { get; }

    internal int? MovieHealth { get; }

    internal string encoding { get; }

    internal string RefTablesSymbols { get; }

    internal string RefTablesNames { get; }

    internal ClearType clear { get; }

    internal RankType rank { get; }

    internal string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    internal string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    internal double? rate { get; }

    internal double? rateDouble => score.HasValue && totalnotes.HasValue && totalnotes.Value > 0 ? (double?)((double)score.Value / 2.0 / totalnotes.Value) : null;

    internal int? score { get; }

    internal int? totalnotes { get; }

    internal int? maxcombo { get; }

    internal int? minbp { get; }

    internal string rankingString { get; }

    internal DateTime? rankingLastupdate { get; }

    internal double? stddevVal { get; }

    internal double? scoreDifficulty { get; }

    internal ChartFileStatus status { get; }

    internal string lr2_bmsid { get; }

    internal double? EntryLevelSortKey { get; private set; }

    internal string Level { get; private set; }

    internal ChartFile Chart { get; private set; }

    internal LR2SongDBExtended.chart_info EntryChartInfo { get; private set; }

    internal LR2SongDBExtended.chart_info ChartInfo => resolvedChartSnapshot?.BmsFile?.ChartInfo ?? resolvedBmson?.ChartInfo ?? EntryChartInfo;

    internal bool HasEntryChartInfoDependency => resolvedChartSnapshot?.BmsFile == null && resolvedBmson == null;

    internal string ChartLevelText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.level);

    internal double? ChartLevelSortKey => ChartInfo?.level ?? 0;

    internal bool ChartLevelUndefined => ChartInfo == null || !ChartInfo.level.HasValue;

    internal string ChartDifficultyText => ChartInfoDisplayFormatter.FormatDifficulty(ChartInfo?.difficulty);

    internal int? ChartDifficultySortKey => ChartInfo?.difficulty;

    internal string ChartDifficultyColorKey => ChartInfoDisplayFormatter.GetDifficultyColorKey(ChartInfo?.difficulty);

    internal bool ChartDifficultyUndefined => ChartInfo == null || !ChartInfo.difficulty_defined;

    internal string ChartMainBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.mainbpm);

    internal double? ChartMainBpmSortKey => ChartInfo?.mainbpm;

    internal string ChartMaxBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.maxbpm);

    internal double? ChartMaxBpmSortKey => ChartInfo?.maxbpm;

    internal string ChartMinBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.minbpm);

    internal double? ChartMinBpmSortKey => ChartInfo?.minbpm;

    internal string ChartDurationText => ChartInfoDisplayFormatter.FormatDuration(ChartInfo?.length);

    internal int? ChartDurationSortKey => ChartInfo?.length;

    internal string ChartJudgeText => ChartInfoDisplayFormatter.FormatJudge(ChartInfo?.judge);

    internal int? ChartJudgeSortKey => ChartInfo?.judge;

    internal string ChartJudgeColorKey => ChartInfoDisplayFormatter.GetJudgeColorKey(ChartInfo?.judge);

    internal string ChartJudgePercentText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.judge);

    internal string ChartFeatureText => ChartInfo == null ? string.Empty : ChartInfoDisplayFormatter.FormatFeature(ChartInfo.feature);

    internal int? ChartFeatureSortKey => ChartInfo?.feature;

    internal int? ChartNotes => ChartInfo?.notes;

    internal int? ChartLongNotes => ChartInfo?.ln;

    internal int? ChartScratchNotes => ChartInfoDisplayFormatter.GetScratchNotes(ChartInfo);

    internal string ChartTotalText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.total);

    internal double? ChartTotalSortKey => ChartInfo?.total;

    internal bool ChartTotalUndefined => ChartInfo == null || !ChartInfo.total_defined;

    internal string ChartTotalPerNoteText => ChartInfoDisplayFormatter.FormatFixedTwo(ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo));

    internal double? ChartTotalPerNoteSortKey => ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo);

    internal string ChartDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.density);

    internal double? ChartDensitySortKey => ChartInfo?.density;

    internal string ChartPeakDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.peakdensity);

    internal double? ChartPeakDensitySortKey => ChartInfo?.peakdensity;

    internal string ChartEndDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.enddensity);

    internal double? ChartEndDensitySortKey => ChartInfo?.enddensity;

    internal int? ChartSoflanCount => ChartInfo?.speedchange_count;

    internal string SearchText { get; private set; }

    internal PlaylistDetailSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot = null,
        LR2SongDBExtended.chart_info entryChartInfo = null,
        Func<ChartFile, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        resolvedChartSnapshot = resolvedChart;
        resolvedBmson = resolvedChart?.BmsonSong;
        EntryChartInfo = entryChartInfo;
        this.bmsonTransientStateProvider = bmsonTransientStateProvider;
        Chart = CreateChartFile();
        ChartFile chart = Chart;
        bool isBmsOwned = HasOwnedBmsChart(chart);
        bool isBmsonOwned = !isBmsOwned && HasOwnedBmsonChart(chart);
        BMSFile bmsOwner = chart?.BmsFile;
        BMSScore effectiveScore = scoreSnapshot ?? bmsOwner?.bmsScore;
        Title = FirstNonEmpty(chart?.Title, entry.title);
        Artist = FirstNonEmpty(chart?.Artist, entry.artist);
        genre = FirstNonEmpty(chart?.Genre);
        mode = chart?.Mode;
        tag = chart?.Tag ?? string.Empty;
        Url = entry.EffectiveUrl;
        Url_diff = entry.EffectiveUrlDiff;
        name_diff = entry.name_diff ?? string.Empty;
        comment = entry.comment ?? string.Empty;
        memo = entry.memo ?? string.Empty;
        hash = FirstNonEmpty(chart?.Md5, entry.md5);
        sha256 = FirstNonEmpty(chart?.Sha256, entry.sha256, entryChartInfo?.sha256);
        Folder = FirstNonEmpty(entry.folder, chart?.Folder);
        path = FirstNonEmpty(chart?.Path);
        PlaylistReferenceDisplay playlistReferenceDisplay = bmsOwner == null && playlistReferenceDisplayProvider != null
            ? playlistReferenceDisplayProvider.Invoke(Chart) ?? PlaylistReferenceDisplay.Empty
            : PlaylistReferenceDisplay.Empty;
        HasZeroNoteMismatchWarning = bmsOwner?.HasZeroNoteMismatchWarning ?? false;
        HasHighlightedWarning = HasProjectedWarning(Chart, bmsOwner);
        DisplayWarning = FirstNonEmpty(ChartWarningProjectionFormatter.BuildDisplayText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), bmsOwner?.DisplayWarning);
        WarningDigestText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildDigestText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), bmsOwner?.WarningDigestText, DisplayWarning);
        WarningTooltipText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildTooltipText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), bmsOwner?.WarningTooltipText, DisplayWarning);
        instl_dst = FirstNonEmpty(Chart?.InstallDestination, bmsOwner?.instl_dst);
        InstallDestinationTitle = FirstNonEmpty(Chart?.InstallDestinationTitle, bmsOwner?.InstallDestinationTitle);
        InstallDestinationArtist = FirstNonEmpty(Chart?.InstallDestinationArtist, bmsOwner?.InstallDestinationArtist);
        WAVHealth = Chart?.WAVHealth ?? bmsOwner?.WAVHealth;
        BGAHealth = Chart?.BGAHealth ?? bmsOwner?.BGAHealth;
        MovieHealth = Chart?.MovieHealth ?? bmsOwner?.MovieHealth;
        encoding = FirstNonEmpty(Chart?.EncodingName, bmsOwner?.encoding);
        RefTablesSymbols = bmsOwner?.RefTablesSymbols ?? playlistReferenceDisplay.Symbols;
        RefTablesNames = bmsOwner?.RefTablesNames ?? playlistReferenceDisplay.Names;
        clear = ResolveClear(isBmsOwned || isBmsonOwned, effectiveScore);
        rank = ResolveRank(effectiveScore);
        rate = effectiveScore?.rate;
        score = effectiveScore?.score;
        totalnotes = effectiveScore?.totalnotes;
        maxcombo = effectiveScore?.maxcombo;
        minbp = ResolveMinBp(effectiveScore);
        rankingString = BuildRankingString(effectiveScore) ?? string.Empty;
        rankingLastupdate = effectiveScore?.rankingLastupdate;
        stddevVal = effectiveScore?.stddevVal;
        scoreDifficulty = effectiveScore?.scoreDifficulty;
        status = Chart?.Status ?? ChartFileStatus.NONE;
        lr2_bmsid = entry.lr2_bmsid ?? string.Empty;
        EntryLevelSortKey = entry.level;
        Level = BuildLevelText(entry, Chart);
        SearchText = BuildSearchText();
    }

    internal bool SetEntryChartInfo(LR2SongDBExtended.chart_info chartInfo)
    {
        if (!HasEntryChartInfoDependency || ReferenceEquals(EntryChartInfo, chartInfo))
        {
            return false;
        }
        EntryChartInfo = chartInfo;
        if (string.IsNullOrWhiteSpace(sha256) && !string.IsNullOrWhiteSpace(chartInfo?.sha256))
        {
            sha256 = chartInfo.sha256;
        }
        Chart = CreateChartFile();
        return true;
    }

    /// <summary>
    /// UI 表示用の lightweight row を生成します。
    /// </summary>
    /// <returns>一覧表示用 row。</returns>
    internal PlaylistDetailRow CreateViewRow()
    {
        return new PlaylistDetailRow(this);
    }

    /// <summary>
    /// 編集済み view row の値で editable snapshot を更新します。
    /// 再 sort/filter 時の再生成元となるため、playlist row の保存前に同期します。
    /// </summary>
    /// <param name="editedRow">編集済み row。</param>
    internal void SynchronizeEditableSnapshot(PlaylistDetailRow editedRow)
    {
        if (editedRow == null)
        {
            throw new ArgumentNullException(nameof(editedRow));
        }
        if (!ReferenceEquals(Entry, editedRow.Entry))
        {
            throw new InvalidOperationException("PlaylistDetailSourceRow entry mismatch.");
        }
        Level = editedRow.Level ?? string.Empty;
        Url = editedRow.Url;
        Url_diff = editedRow.Url_diff;
        comment = editedRow.comment ?? string.Empty;
        memo = editedRow.memo ?? string.Empty;
        EntryLevelSortKey = ResolveEntryLevelSortKey(editedRow.Level, Entry.level, editedRow.EntryLevelSortKey);
        Chart = CreateChartFile();
        SearchText = BuildSearchText();
    }

    private ChartFile CreateChartFile()
    {
        BMSFile bmsOwner = resolvedChartSnapshot?.BmsFile;
        if (bmsOwner != null)
        {
            return ChartFileProjection.FromBmsFile(bmsOwner);
        }
        if (resolvedBmson != null)
        {
            if (bmsonTransientStateProvider == null && resolvedChartSnapshot != null)
            {
                return resolvedChartSnapshot;
            }
            return ChartFileProjection.FromBmsonSong(
                resolvedBmson,
                GetBmsonTransientState(includeWarningSnapshot: true));
        }
        if (resolvedChartSnapshot != null && EntryChartInfo == null)
        {
            return resolvedChartSnapshot;
        }
        return ChartFileProjection.FromBmsMetadata(
            path,
            FirstNonEmpty(hash, Entry?.md5),
            FirstNonEmpty(sha256, Entry?.sha256, EntryChartInfo?.sha256),
            FirstNonEmpty(Title, Entry?.title),
            FirstNonEmpty(Artist, Entry?.artist),
            genre,
            FirstNonEmpty(Folder, Entry?.folder),
            tag,
            Entry?.level,
            mode,
            ChartInfo);
    }

    private ChartFileTransientState GetBmsonTransientState(bool includeWarningSnapshot)
    {
        if (resolvedBmson == null)
        {
            return ChartFileTransientState.Empty;
        }
        return bmsonTransientStateProvider?.Invoke(resolvedBmson, includeWarningSnapshot)
            ?? ChartFileTransientState.Empty;
    }

    private static bool HasProjectedWarning(ChartFile chart, BMSFile fallback)
    {
        return ChartWarningProjectionFormatter.HasHighlightedWarning(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false)
            || (chart?.Warnings.Count == 0 && (fallback?.HasHighlightedWarning ?? false));
    }

    private static bool HasOwnedChart(ChartFile chart)
    {
        return HasOwnedBmsChart(chart) || HasOwnedBmsonChart(chart);
    }

    private static bool HasOwnedBmsChart(ChartFile chart)
    {
        return chart?.BmsFile != null && !string.IsNullOrWhiteSpace(chart.BmsFile.path);
    }

    private static bool HasOwnedBmsonChart(ChartFile chart)
    {
        return chart?.BmsonSong != null && !string.IsNullOrWhiteSpace(chart.BmsonSong.path);
    }

    private static string BuildLevelText(BMSTableEntry entry, ChartFile chart)
    {
        if (entry?.level != null)
        {
            return entry.level.ToString();
        }
        return chart?.LevelText ?? string.Empty;
    }

    private static double? ResolveEntryLevelSortKey(string levelText, double? entryLevel, double? rowSortKey)
    {
        if (entryLevel.HasValue)
        {
            return entryLevel;
        }
        if (rowSortKey.HasValue)
        {
            return rowSortKey;
        }
        if (string.IsNullOrWhiteSpace(levelText))
        {
            return null;
        }
        Match match = levelParseRegex.Match(levelText.Trim());
        if (!match.Success)
        {
            return null;
        }
        if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedLevel))
        {
            return parsedLevel;
        }
        return null;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        if (candidates == null)
        {
            return string.Empty;
        }
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }

    private static ClearType ResolveClear(bool isOwned, BMSScore effectiveScore)
    {
        if (effectiveScore != null)
        {
            return effectiveScore.clear;
        }
        return isOwned ? ClearType.NO_PLAY : ClearType.NO_SONG;
    }

    private static RankType ResolveRank(BMSScore effectiveScore)
    {
        if (effectiveScore == null)
        {
            return RankType.INVALID;
        }
        return (effectiveScore.rank != RankType.INVALID) ? effectiveScore.rank : RankType.F;
    }

    private static int? ResolveMinBp(BMSScore effectiveScore)
    {
        if (effectiveScore != null)
        {
            return (effectiveScore.minbp == -1) ? effectiveScore.totalnotes : effectiveScore.minbp;
        }
        return null;
    }

    private static string BuildRankingString(BMSScore effectiveScore)
    {
        if (effectiveScore == null || effectiveScore.ranking == 0 || effectiveScore.ranking == -1 || effectiveScore.rankingNum == 0)
        {
            return string.Empty;
        }
        return effectiveScore.ranking.ToString().PadLeft(effectiveScore.rankingNum.ToString().Length) + "/" + effectiveScore.rankingNum;
    }

    private string BuildSearchText()
    {
        var builder = new StringBuilder(128);
        builder.Append(Title);
        builder.Append('@');
        builder.Append(genre);
        builder.Append('@');
        builder.Append(Artist);
        builder.Append('@');
        builder.Append(tag);
        builder.Append('@');
        builder.Append(path);
        builder.Append('@');
        builder.Append(RefTablesSymbols);
        builder.Append('@');
        builder.Append(memo);
        builder.Append('@');
        builder.Append(comment);
        return builder.ToString().ToUpperInvariant();
    }
}
