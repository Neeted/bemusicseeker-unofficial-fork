using System;
using System.Globalization;
using System.Linq;
using System.Text;
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

    /// <summary>
    /// プレイリストエントリ本体です。
    /// </summary>
    internal BMSTableEntry Entry { get; }

    private readonly ChartFile resolvedChartSnapshot;
    private ResourceHealthWarningProjection resourceHealthProjection;
    private bool hasResourceHealthProjection;

    /// <summary>reader または確定結果から受け取った entry 値です。Entry は識別にだけ使います。</summary>
    internal PlaylistEntryValue EntryValue { get; private set; }
    /// <summary>表 owner が確定した基準順です。</summary>
    internal long EntryOrder { get; private set; }
    /// <summary>投影前の所持 current です。</summary>
    internal ChartFile OwnedCurrent => resolvedChartSnapshot;
    /// <summary>候補再解決の依存キーです。MD5 がある場合は SHA-256 へ広げません。</summary>
    internal PlaylistEntryLookupKey LookupKey => !string.IsNullOrWhiteSpace(EntryValue.Md5)
        ? new(PlaylistEntryLookupKeyKind.Md5, EntryValue.Md5)
        : new(PlaylistEntryLookupKeyKind.Sha256, EntryValue.Sha256);


    private readonly Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider;

    private readonly Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider;

    private ChartScoreSnapshot effectiveScoreSnapshot;

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

    internal bool HasHighlightedWarning => ChartWarningProjectionFormatter.HasHighlightedWarning(Chart, resourceHealthProjection, hasResourceHealthProjection);

    internal string DisplayWarning => ChartWarningProjectionFormatter.BuildDisplayText(Chart, resourceHealthProjection, hasResourceHealthProjection);

    internal string WarningDigestText => FirstNonEmpty(ChartWarningProjectionFormatter.BuildDigestText(Chart, resourceHealthProjection, hasResourceHealthProjection), DisplayWarning);

    internal string WarningTooltipText => FirstNonEmpty(ChartWarningProjectionFormatter.BuildTooltipText(Chart, resourceHealthProjection, hasResourceHealthProjection), DisplayWarning);

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

    internal string RefTablesSymbols { get; private set; }

    internal string RefTablesNames { get; private set; }

    internal ClearType clear { get; private set; }

    internal RankType rank { get; private set; }

    internal string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    internal string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    internal double? rate { get; private set; }

    internal double? rateDouble => score.HasValue && totalnotes.HasValue && totalnotes.Value > 0 ? (double?)((double)score.Value / 2.0 / totalnotes.Value) : null;

    internal int? score { get; private set; }

    internal int? totalnotes { get; private set; }

    internal int? maxcombo { get; private set; }

    internal int? minbp { get; private set; }

    internal string rankingString { get; private set; }

    internal DateTime? rankingLastupdate { get; private set; }

    internal double? stddevVal { get; private set; }

    internal double? scoreDifficulty { get; private set; }

    internal string lr2_bmsid { get; }

    internal double? EntryLevelSortKey { get; private set; }

    internal string Level { get; private set; }

    internal ChartFile Chart { get; private set; }

    internal BeMusicSeeker.Models.ChartDetails EntryChartInfo { get; private set; }

    internal BeMusicSeeker.Models.ChartDetails ChartInfo => Chart?.ChartInfo ?? EntryChartInfo;

    internal bool HasEntryChartInfoDependency => resolvedChartSnapshot == null
        || string.IsNullOrWhiteSpace(resolvedChartSnapshot.Path);

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
        BeMusicSeeker.Models.ChartDetails entryChartInfo = null,
        Func<ChartFile, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        LibraryChartRef resolvedChartRef = null,
        PlaylistEntryValue entryValue = null,
        long entryOrder = 0)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        resolvedChartSnapshot = resolvedChart;
        EntryValue = entryValue ?? entry.CaptureValue();
        EntryOrder = entryOrder;
        EntryChartInfo = entryChartInfo;
        this.chartTransientStateProvider = chartTransientStateProvider;
        this.chartInfoProjectionProvider = chartInfoProjectionProvider;
        Chart = CreateChartFile();
        ChartFile chart = Chart;
        effectiveScoreSnapshot = ResolveInitialScore(chart, scoreSnapshot);
        if (!ReferenceEquals(effectiveScoreSnapshot, chart?.Score))
        {
            Chart = ChartFileProjection.WithScore(chart, effectiveScoreSnapshot);
            chart = Chart;
        }
        Title = FirstNonEmpty(chart?.Title, EntryValue.Title);
        Artist = FirstNonEmpty(chart?.Artist, EntryValue.Artist);
        genre = FirstNonEmpty(chart?.Genre);
        mode = chart?.Mode;
        tag = chart?.Tag ?? string.Empty;
        Url = EntryValue.EffectiveUrl;
        Url_diff = EntryValue.EffectiveUrlDiff;
        name_diff = EntryValue.NameDiff ?? string.Empty;
        comment = EntryValue.Comment ?? string.Empty;
        memo = EntryValue.Memo ?? string.Empty;
        hash = FirstNonEmpty(chart?.Md5, EntryValue.Md5);
        sha256 = FirstNonEmpty(chart?.Sha256, EntryValue.Sha256, entryChartInfo?.sha256);
        Folder = FirstNonEmpty(EntryValue.Folder, chart?.Folder);
        path = FirstNonEmpty(chart?.Path);
        PlaylistReferenceDisplay playlistReferenceDisplay = playlistReferenceDisplayProvider != null
            ? playlistReferenceDisplayProvider.Invoke(Chart) ?? PlaylistReferenceDisplay.Empty
            : PlaylistReferenceDisplay.Empty;
        HasZeroNoteMismatchWarning = Chart?.Warnings?.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch) ?? false;
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
        lr2_bmsid = EntryValue.Lr2BmsId ?? string.Empty;
        EntryLevelSortKey = EntryValue.Level;
        Level = EntryValue.Level?.ToString("R", CultureInfo.InvariantCulture) ?? Chart?.LevelText ?? string.Empty;
        SearchText = BuildSearchText();
    }

    private bool SetEntryChartInfo(BeMusicSeeker.Models.ChartDetails chartInfo)
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
        Chart = ChartFileProjection.WithChartInfo(Chart, chartInfo);
        return true;
    }

    private void SetScoreSnapshot(BMSScore scoreSnapshot)
    {
        effectiveScoreSnapshot = ChartScoreSnapshot.FromBmsScore(scoreSnapshot, Chart?.Path);
        Chart = ChartFileProjection.WithScore(Chart, effectiveScoreSnapshot);
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
    }

    /// <summary>同じentryの確定編集属性だけを置換し、遅延中の所持・参照投影を保持します。lookupや所属を変更する差分には使いません。</summary>
    internal PlaylistDetailSourceRow WithEditedValues(PlaylistEntryValue value)
    {
        var copy = (PlaylistDetailSourceRow)MemberwiseClone();
        copy.EntryValue = EntryValue with
        {
            Level = value.Level,
            Url = value.Url,
            UrlDiff = value.UrlDiff,
            EffectiveUrl = value.EffectiveUrl,
            EffectiveUrlDiff = value.EffectiveUrlDiff,
            Comment = value.Comment,
            Memo = value.Memo
        };
        if (resolvedChartSnapshot == null) { copy.Chart = ChartFileProjection.WithScore(copy.CreateChartFile(), effectiveScoreSnapshot); }
        copy.EntryLevelSortKey = value.Level;
        copy.Level = value.Level?.ToString("R", CultureInfo.InvariantCulture) ?? copy.Chart?.LevelText ?? string.Empty;
        copy.Url = value.EffectiveUrl;
        copy.Url_diff = value.EffectiveUrlDiff;
        copy.comment = value.Comment ?? string.Empty;
        copy.memo = value.Memo ?? string.Empty;
        copy.SearchText = copy.BuildSearchText();
        return copy;
    }

    /// <summary>確定編集属性だけが現在の入力へ反映済みかを比較し、同値保存で不要な投影を繰り返しません。</summary>
    internal bool HasEditedValues(PlaylistEntryValue value)
        => EntryValue.Level == value.Level && Equals(EntryValue.Url, value.Url) && Equals(EntryValue.UrlDiff, value.UrlDiff)
            && Equals(EntryValue.EffectiveUrl, value.EffectiveUrl) && Equals(EntryValue.EffectiveUrlDiff, value.EffectiveUrlDiff)
            && EntryValue.Comment == value.Comment && EntryValue.Memo == value.Memo;

    internal PlaylistDetailSourceRow WithEntryChartInfo(BeMusicSeeker.Models.ChartDetails chartInfo)
    {
        var copy = (PlaylistDetailSourceRow)MemberwiseClone();
        copy.SetEntryChartInfo(chartInfo);
        return copy;
    }

    /// <summary>同じ捕捉入力の参照表示だけを置換し、検索・並べ替えへ渡します。</summary>
    internal PlaylistDetailSourceRow WithReferenceDisplay(ChartListReferenceInput input)
    {
        PlaylistReferenceDisplay display = input.Resolve(hash, sha256);
        var next = (PlaylistDetailSourceRow)MemberwiseClone();
        next.RefTablesSymbols = display.Symbols;
        next.RefTablesNames = display.Names;
        next.SearchText = next.BuildSearchText();
        return next;
    }

    /// <summary>
    /// chart_info patch と同じ immutable row replacement で score projection も更新します。
    /// chart_info hydration 後に未所持 entry の score hash が初めて解決できる場合に使います。
    /// </summary>
    /// <param name="chartInfo">更新された entry chart_info。</param>
    /// <param name="scoreSnapshot">現在のスコア。null は記録なしとして採用し、コピー元の旧スコアを消去します。</param>
    /// <returns>更新後の source row。</returns>
    internal PlaylistDetailSourceRow WithEntryChartInfoAndScore(
        BeMusicSeeker.Models.ChartDetails chartInfo,
        BMSScore scoreSnapshot)
    {
        var copy = (PlaylistDetailSourceRow)MemberwiseClone();
        copy.SetEntryChartInfo(chartInfo);
        copy.SetScoreSnapshot(scoreSnapshot);
        return copy;
    }

    /// <summary>表示行生成時にstatus読取りへ渡す、現在の再生状態のdelegateです。</summary>
    internal Func<ChartFile, ChartFileStatus> PlaybackStatusProvider { get; set; }

    /// <summary>UI表示用の行を生成し、再生状態の読取り口へ接続します。</summary>
    internal PlaylistDetailRow CreateViewRow()
    {
        var row = new PlaylistDetailRow(this);
        row.SetPlaybackStatusProvider(PlaybackStatusProvider);
        return row;
    }

    /// <summary>専門 owner の捕捉済み警告を共通 formatter へ接続します。</summary>
    internal PlaylistDetailSourceRow WithResourceHealthProjection(ResourceHealthWarningProjection projection)
    {
        var next = (PlaylistDetailSourceRow)MemberwiseClone();
        next.resourceHealthProjection = projection ?? ResourceHealthWarningProjection.Empty;
        next.hasResourceHealthProjection = true;
        return next;
    }

    private ChartFile CreateChartFile()
    {

        if (resolvedChartSnapshot != null)
        {
            ChartFile projectedChart = ApplyChartInfoProjection(resolvedChartSnapshot, resolvedChartSnapshot);
            return ChartFileProjection.WithTransientState(
                projectedChart,
                GetChartTransientState(projectedChart, includeWarningSnapshot: true));
        }
        return ChartFileProjection.FromBmsMetadata(
            path,
            FirstNonEmpty(hash, EntryValue?.Md5),
            FirstNonEmpty(sha256, EntryValue?.Sha256, EntryChartInfo?.sha256),
            FirstNonEmpty(Title, EntryValue?.Title),
            FirstNonEmpty(Artist, EntryValue?.Artist),
            genre,
            FirstNonEmpty(Folder, EntryValue?.Folder),
            tag,
            EntryValue?.Level,
            mode,
            EntryChartInfo);
    }

    private ChartFile ApplyChartInfoProjection(ChartFile chart, ChartFile identitySource)
    {
        BeMusicSeeker.Models.ChartDetails resolved = ResolveChartInfoFromProvider(identitySource ?? chart);
        if (chart == null || resolved == null || ReferenceEquals(resolved, chart.ChartInfo))
        {
            return chart;
        }
        return ChartFileProjection.WithChartInfo(chart, resolved);
    }

    private BeMusicSeeker.Models.ChartDetails ResolveChartInfoFromProvider(ChartFile chart)
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

    private static ChartScoreSnapshot ResolveInitialScore(ChartFile chart, BMSScore scoreSnapshot)
    {
        return scoreSnapshot != null
            ? ChartScoreSnapshot.FromBmsScore(scoreSnapshot, chart?.Path)
            : chart?.Score ?? ChartScoreSnapshot.MissingChart;
    }

    private static bool HasOwnedChart(ChartFile chart)
    {
        return !string.IsNullOrWhiteSpace(chart?.Path)
            && !string.IsNullOrWhiteSpace(chart?.Md5);
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
