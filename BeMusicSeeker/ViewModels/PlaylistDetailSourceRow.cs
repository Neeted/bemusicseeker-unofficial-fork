using System;
using System.Globalization;
using System.Linq;
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

    private readonly Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider;

    private readonly Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider;

    private readonly ChartScoreSnapshot effectiveScoreSnapshot;

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

    internal LR2SongDBExtended.chart_info ChartInfo => ResolveChartInfoProjection();

    internal bool HasEntryChartInfoDependency => resolvedChartSnapshot?.GetBmsStorageOwner() == null && resolvedBmson == null;

    private ChartInfoDisplaySnapshot ChartInfoDisplay => ChartInfoDisplaySnapshot.FromChartInfo(ChartInfo);

    internal string ChartLevelText => ChartInfoDisplay.ChartLevelText;

    internal double? ChartLevelSortKey => ChartInfoDisplay.ChartLevelSortKey;

    internal bool ChartLevelUndefined => ChartInfoDisplay.ChartLevelUndefined;

    internal string ChartDifficultyText => ChartInfoDisplay.ChartDifficultyText;

    internal int? ChartDifficultySortKey => ChartInfoDisplay.ChartDifficultySortKey;

    internal string ChartDifficultyColorKey => ChartInfoDisplay.ChartDifficultyColorKey;

    internal bool ChartDifficultyUndefined => ChartInfoDisplay.ChartDifficultyUndefined;

    internal string ChartMainBpmText => ChartInfoDisplay.ChartMainBpmText;

    internal double? ChartMainBpmSortKey => ChartInfoDisplay.ChartMainBpmSortKey;

    internal string ChartMaxBpmText => ChartInfoDisplay.ChartMaxBpmText;

    internal double? ChartMaxBpmSortKey => ChartInfoDisplay.ChartMaxBpmSortKey;

    internal string ChartMinBpmText => ChartInfoDisplay.ChartMinBpmText;

    internal double? ChartMinBpmSortKey => ChartInfoDisplay.ChartMinBpmSortKey;

    internal string ChartDurationText => ChartInfoDisplay.ChartDurationText;

    internal int? ChartDurationSortKey => ChartInfoDisplay.ChartDurationSortKey;

    internal string ChartJudgeText => ChartInfoDisplay.ChartJudgeText;

    internal int? ChartJudgeSortKey => ChartInfoDisplay.ChartJudgeSortKey;

    internal string ChartJudgeColorKey => ChartInfoDisplay.ChartJudgeColorKey;

    internal string ChartJudgePercentText => ChartInfoDisplay.ChartJudgePercentText;

    internal string ChartFeatureText => ChartInfoDisplay.ChartFeatureText;

    internal int? ChartFeatureSortKey => ChartInfoDisplay.ChartFeatureSortKey;

    internal int? ChartNotes => ChartInfoDisplay.ChartNotes;

    internal int? ChartLongNotes => ChartInfoDisplay.ChartLongNotes;

    internal int? ChartScratchNotes => ChartInfoDisplay.ChartScratchNotes;

    internal string ChartTotalText => ChartInfoDisplay.ChartTotalText;

    internal double? ChartTotalSortKey => ChartInfoDisplay.ChartTotalSortKey;

    internal bool ChartTotalUndefined => ChartInfoDisplay.ChartTotalUndefined;

    internal string ChartTotalPerNoteText => ChartInfoDisplay.ChartTotalPerNoteText;

    internal double? ChartTotalPerNoteSortKey => ChartInfoDisplay.ChartTotalPerNoteSortKey;

    internal string ChartDensityText => ChartInfoDisplay.ChartDensityText;

    internal double? ChartDensitySortKey => ChartInfoDisplay.ChartDensitySortKey;

    internal string ChartPeakDensityText => ChartInfoDisplay.ChartPeakDensityText;

    internal double? ChartPeakDensitySortKey => ChartInfoDisplay.ChartPeakDensitySortKey;

    internal string ChartEndDensityText => ChartInfoDisplay.ChartEndDensityText;

    internal double? ChartEndDensitySortKey => ChartInfoDisplay.ChartEndDensitySortKey;

    internal int? ChartSoflanCount => ChartInfoDisplay.ChartSoflanCount;

    internal string SearchText { get; private set; }

    internal PlaylistDetailSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot = null,
        LR2SongDBExtended.chart_info entryChartInfo = null,
        Func<ChartFile, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        resolvedChartSnapshot = resolvedChart;
        resolvedBmson = resolvedChart?.GetBmsonStorageOwner();
        EntryChartInfo = entryChartInfo;
        this.chartTransientStateProvider = chartTransientStateProvider;
        this.chartInfoProjectionProvider = chartInfoProjectionProvider;
        Chart = CreateChartFile();
        ChartFile chart = Chart;
        effectiveScoreSnapshot = ResolveEffectiveScore(chart, scoreSnapshot);
        if (!ReferenceEquals(effectiveScoreSnapshot, chart?.Score))
        {
            Chart = ChartFileProjection.WithScore(chart, effectiveScoreSnapshot);
            chart = Chart;
        }
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
        PlaylistReferenceDisplay playlistReferenceDisplay = playlistReferenceDisplayProvider != null
            ? playlistReferenceDisplayProvider.Invoke(Chart) ?? PlaylistReferenceDisplay.Empty
            : PlaylistReferenceDisplay.Empty;
        HasZeroNoteMismatchWarning = Chart?.Warnings?.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch) ?? false;
        HasHighlightedWarning = HasProjectedWarning(Chart);
        DisplayWarning = ChartWarningProjectionFormatter.BuildDisplayText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false);
        WarningDigestText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildDigestText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), DisplayWarning);
        WarningTooltipText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildTooltipText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), DisplayWarning);
        instl_dst = Chart?.InstallDestination ?? string.Empty;
        InstallDestinationTitle = Chart?.InstallDestinationTitle ?? string.Empty;
        InstallDestinationArtist = Chart?.InstallDestinationArtist ?? string.Empty;
        WAVHealth = Chart?.WAVHealth;
        BGAHealth = Chart?.BGAHealth;
        MovieHealth = Chart?.MovieHealth;
        encoding = Chart?.EncodingName ?? string.Empty;
        RefTablesSymbols = playlistReferenceDisplay.Symbols;
        RefTablesNames = playlistReferenceDisplay.Names;
        clear = effectiveScoreSnapshot.Clear;
        rank = effectiveScoreSnapshot.Rank;
        rate = effectiveScoreSnapshot.Rate;
        score = effectiveScoreSnapshot.Score;
        totalnotes = effectiveScoreSnapshot.TotalNotes;
        maxcombo = effectiveScoreSnapshot.MaxCombo;
        minbp = effectiveScoreSnapshot.MinBp;
        rankingString = effectiveScoreSnapshot.RankingString;
        rankingLastupdate = effectiveScoreSnapshot.RankingLastUpdate;
        stddevVal = effectiveScoreSnapshot.StdDevVal;
        scoreDifficulty = effectiveScoreSnapshot.ScoreDifficulty;
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
        Chart = CreateChartFile();
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
        BMSFile bmsOwner = resolvedChartSnapshot?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            ChartFile currentChart = ChartFileProjection.FromStorageOwner(
                resolvedChartSnapshot,
                includeResourceReferences: false,
                includeScoreSnapshot: false);
            currentChart = ChartFileProjection.WithScore(currentChart, effectiveScoreSnapshot ?? resolvedChartSnapshot.Score);
            currentChart = ApplyChartInfoProjection(currentChart, resolvedChartSnapshot);
            return ChartFileProjection.WithTransientState(
                currentChart,
                GetChartTransientState(currentChart, includeWarningSnapshot: true));
        }
        if (resolvedBmson != null)
        {
            if (resolvedChartSnapshot != null)
            {
                ChartFile ownerSource = ChartFileProjection.FromBmsonStorageOwnerIdentity(resolvedBmson);
                ChartFile projectedChart = resolvedChartSnapshot;
                projectedChart = ApplyChartInfoProjection(projectedChart, ownerSource);
                return ChartFileProjection.WithTransientState(
                    projectedChart,
                    GetChartTransientState(ChartFileProjection.FromStorageOwner(ownerSource, includeWarningSnapshot: false, includeResourceReferences: false), includeWarningSnapshot: true));
            }
            ChartFile identityChart = ChartFileProjection.FromBmsonSong(resolvedBmson, includeWarningSnapshot: false, includeResourceReferences: false);
            return ChartFileProjection.FromStorageOwnerWithTransientState(
                identityChart,
                GetChartTransientState(identityChart, includeWarningSnapshot: true));
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
            EntryChartInfo);
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoProjection()
    {
        BMSFile bmsOwner = resolvedChartSnapshot?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return ResolveChartInfoFromProvider(resolvedChartSnapshot)
                ?? resolvedChartSnapshot.ChartInfo;
        }
        if (resolvedBmson != null)
        {
            ChartFile ownerSource = resolvedChartSnapshot ?? ChartFileProjection.FromBmsonStorageOwnerIdentity(resolvedBmson);
            return ResolveChartInfoFromProvider(ownerSource)
                ?? ownerSource?.ChartInfo;
        }
        if (resolvedChartSnapshot != null && EntryChartInfo == null)
        {
            return resolvedChartSnapshot.ChartInfo;
        }
        return EntryChartInfo;
    }

    private ChartFile ApplyChartInfoProjection(ChartFile chart, ChartFile identitySource)
    {
        LR2SongDBExtended.chart_info resolved = ResolveChartInfoFromProvider(identitySource ?? chart);
        if (chart == null || resolved == null || ReferenceEquals(resolved, chart.ChartInfo))
        {
            return chart;
        }
        return ChartFileProjection.WithChartInfo(chart, resolved);
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoFromProvider(ChartFile chart)
    {
        return chart == null ? null : chartInfoProjectionProvider?.Invoke(chart);
    }

    private ChartFileTransientState GetChartTransientState(ChartFile chart, bool includeWarningSnapshot)
    {
        if (chart == null)
        {
            return ChartFileTransientState.Empty;
        }
        return chartTransientStateProvider?.Invoke(chart, includeWarningSnapshot)
            ?? ChartFileTransientState.Empty;
    }

    private static ChartScoreSnapshot ResolveEffectiveScore(ChartFile chart, BMSScore scoreSnapshot)
    {
        return scoreSnapshot != null
            ? ChartScoreSnapshot.FromBmsScore(scoreSnapshot, chart?.Path)
            : chart?.Score ?? ChartScoreSnapshot.MissingChart;
    }

    private static bool HasProjectedWarning(ChartFile chart)
    {
        return ChartWarningProjectionFormatter.HasHighlightedWarning(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false);
    }

    private static bool HasOwnedChart(ChartFile chart)
    {
        return !string.IsNullOrWhiteSpace(chart?.GetBmsStorageOwner()?.path)
            || !string.IsNullOrWhiteSpace(chart?.GetBmsonStorageOwner()?.path);
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
