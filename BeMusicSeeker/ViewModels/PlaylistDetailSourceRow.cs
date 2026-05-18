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

    /// <summary>
    /// ライブラリ上の実体譜面です。未所持行では null です。
    /// </summary>
    internal BMSFile RealFile { get; }

    /// <summary>
    /// ライブラリ上の対応 bmson 実体です。未所持または BMS 優先解決時は null です。
    /// </summary>
    internal LR2SongDBExtended.bmson_song ResolvedBmson { get; }

    private PendingChartEntry bmsonChartAdapter;

    private readonly Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider;

    internal BMSFile CompatibilityBmsFile => RealFile ?? GetOrCreateBmsonChartAdapter();

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned => (RealFile != null && !string.IsNullOrWhiteSpace(RealFile.path)) || (ResolvedBmson != null && !string.IsNullOrWhiteSpace(ResolvedBmson.path));

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

    internal BMSFile.BMSFileStatus status { get; }

    internal string lr2_bmsid { get; }

    internal double? EntryLevelSortKey { get; private set; }

    internal string Level { get; private set; }

    internal ChartFile Chart { get; private set; }

    internal LR2SongDBExtended.chart_info EntryChartInfo { get; private set; }

    internal LR2SongDBExtended.chart_info ChartInfo => RealFile?.ChartInfo ?? ResolvedBmson?.ChartInfo ?? EntryChartInfo;

    internal bool HasEntryChartInfoDependency => RealFile == null && ResolvedBmson == null;

    private PendingChartEntry GetOrCreateBmsonChartAdapter()
    {
        if (ResolvedBmson == null)
        {
            return null;
        }
        PendingChartEntry provided = bmsonChartAdapterProvider?.Invoke(ResolvedBmson);
        if (provided != null)
        {
            bmsonChartAdapter = provided;
            return bmsonChartAdapter;
        }
        if (bmsonChartAdapter == null || !ReferenceEquals(bmsonChartAdapter.BmsonSong, ResolvedBmson))
        {
            bmsonChartAdapter = PendingChartEntry.CreateFromBmsonSong(ResolvedBmson);
        }
        else if (!string.Equals(bmsonChartAdapter.path, ResolvedBmson.path, StringComparison.OrdinalIgnoreCase))
        {
            bmsonChartAdapter.UpdateFromBmsonSong(ResolvedBmson);
        }
        return bmsonChartAdapter;
    }

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
        BMSFile realFile,
        LR2SongDBExtended.bmson_song resolvedBmson = null,
        BMSFile scoreProbe = null,
        BMSScore scoreSnapshot = null,
        LR2SongDBExtended.chart_info entryChartInfo = null,
        Func<string, string, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        RealFile = realFile;
        ResolvedBmson = resolvedBmson;
        EntryChartInfo = entryChartInfo;
        this.bmsonChartAdapterProvider = bmsonChartAdapterProvider;
        bool isBmsOwned = realFile != null && !string.IsNullOrWhiteSpace(realFile.path);
        bool isBmsonOwned = !isBmsOwned && resolvedBmson != null && !string.IsNullOrWhiteSpace(resolvedBmson.path);
        BMSFile compatibilityBmsFile = CompatibilityBmsFile;
        BMSFile snapshotSource = realFile ?? compatibilityBmsFile ?? scoreProbe;
        BMSScore effectiveScore = scoreSnapshot ?? realFile?.bmsScore ?? scoreProbe?.bmsScore;
        Title = FirstNonEmpty(realFile?.Title, BmsonSongParser.ComposeDisplayTitle(resolvedBmson), entry.title);
        Artist = FirstNonEmpty(realFile?.Artist, resolvedBmson?.artist, entry.artist);
        genre = FirstNonEmpty(realFile?.genre, resolvedBmson?.genre);
        mode = realFile?.mode ?? BmsonSongParser.ResolvePlaylistMode(resolvedBmson?.mode_hint) ?? scoreProbe?.mode;
        tag = realFile?.tag ?? string.Empty;
        Url = entry.EffectiveUrl;
        Url_diff = entry.EffectiveUrlDiff;
        name_diff = entry.name_diff ?? string.Empty;
        comment = entry.comment ?? string.Empty;
        memo = entry.memo ?? string.Empty;
        hash = FirstNonEmpty(realFile?.hash, resolvedBmson?.md5, entry.md5);
        sha256 = FirstNonEmpty(realFile?.sha256, resolvedBmson?.sha256, entry.sha256, entryChartInfo?.sha256);
        PlaylistReferenceDisplay playlistReferenceDisplay = realFile == null && playlistReferenceDisplayProvider != null
            ? playlistReferenceDisplayProvider.Invoke(hash, sha256) ?? PlaylistReferenceDisplay.Empty
            : PlaylistReferenceDisplay.Empty;
        Folder = FirstNonEmpty(entry.folder, BmsonSongParser.ComposeDisplayFolder(resolvedBmson));
        path = FirstNonEmpty(realFile?.path, resolvedBmson?.path);
        Chart = CreateChartFile();
        HasZeroNoteMismatchWarning = snapshotSource?.HasZeroNoteMismatchWarning ?? false;
        HasHighlightedWarning = HasProjectedWarning(Chart, snapshotSource);
        DisplayWarning = FirstNonEmpty(ChartWarningProjectionFormatter.BuildDisplayText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), snapshotSource?.DisplayWarning);
        WarningDigestText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildDigestText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), snapshotSource?.WarningDigestText, DisplayWarning);
        WarningTooltipText = FirstNonEmpty(ChartWarningProjectionFormatter.BuildTooltipText(Chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), snapshotSource?.WarningTooltipText, DisplayWarning);
        instl_dst = FirstNonEmpty(Chart?.InstallDestination, snapshotSource?.instl_dst);
        InstallDestinationTitle = FirstNonEmpty(Chart?.InstallDestinationTitle, snapshotSource?.InstallDestinationTitle);
        InstallDestinationArtist = FirstNonEmpty(Chart?.InstallDestinationArtist, snapshotSource?.InstallDestinationArtist);
        WAVHealth = Chart?.WAVHealth ?? snapshotSource?.WAVHealth;
        BGAHealth = Chart?.BGAHealth ?? snapshotSource?.BGAHealth;
        MovieHealth = Chart?.MovieHealth ?? snapshotSource?.MovieHealth;
        encoding = FirstNonEmpty(Chart?.EncodingName, snapshotSource?.encoding);
        RefTablesSymbols = realFile?.RefTablesSymbols ?? playlistReferenceDisplay.Symbols;
        RefTablesNames = realFile?.RefTablesNames ?? playlistReferenceDisplay.Names;
        clear = ResolveClear(isBmsOwned, isBmsonOwned, scoreProbe, effectiveScore);
        rank = ResolveRank(isBmsOwned, isBmsonOwned, scoreProbe, effectiveScore);
        rate = effectiveScore?.rate ?? scoreProbe?.rate;
        score = effectiveScore?.score ?? scoreProbe?.score;
        totalnotes = effectiveScore?.totalnotes ?? scoreProbe?.totalnotes;
        maxcombo = effectiveScore?.maxcombo ?? scoreProbe?.maxcombo;
        minbp = ResolveMinBp(scoreProbe, effectiveScore);
        rankingString = BuildRankingString(effectiveScore) ?? scoreProbe?.rankingString ?? string.Empty;
        rankingLastupdate = effectiveScore?.rankingLastupdate ?? scoreProbe?.rankingLastupdate;
        stddevVal = effectiveScore?.stddevVal ?? scoreProbe?.stddevVal;
        scoreDifficulty = effectiveScore?.scoreDifficulty ?? scoreProbe?.scoreDifficulty;
        status = snapshotSource?.status ?? BMSFile.BMSFileStatus.NONE;
        lr2_bmsid = entry.lr2_bmsid ?? string.Empty;
        EntryLevelSortKey = entry.level;
        Level = BuildLevelText(entry, realFile, resolvedBmson);
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
        if (RealFile != null)
        {
            return ChartFileProjection.FromBmsFile(RealFile);
        }
        if (ResolvedBmson != null)
        {
            return ChartFileProjection.FromBmsonSong(ResolvedBmson, CompatibilityBmsFile);
        }
        return ChartFileProjection.FromBmsMetadata(
            path,
            hash,
            sha256,
            Title,
            Artist,
            genre,
            Folder,
            tag,
            Entry?.level,
            mode,
            ChartInfo);
    }

    private static bool HasProjectedWarning(ChartFile chart, BMSFile fallback)
    {
        return ChartWarningProjectionFormatter.HasHighlightedWarning(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false)
            || (chart?.Warnings.Count == 0 && (fallback?.HasHighlightedWarning ?? false));
    }

    private static string BuildLevelText(BMSTableEntry entry, BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson)
    {
        if (entry?.level != null)
        {
            return entry.level.ToString();
        }
        return realFile?.level?.ToString() ?? resolvedBmson?.level?.ToString() ?? string.Empty;
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

    private static ClearType ResolveClear(bool isBmsOwned, bool isBmsonOwned, BMSFile scoreProbe, BMSScore effectiveScore)
    {
        if (isBmsOwned)
        {
            return effectiveScore?.clear ?? ClearType.NO_PLAY;
        }
        if (isBmsonOwned)
        {
            return ClearType.NO_PLAY;
        }
        return scoreProbe?.clear ?? ClearType.NO_SONG;
    }

    private static RankType ResolveRank(bool isBmsOwned, bool isBmsonOwned, BMSFile scoreProbe, BMSScore effectiveScore)
    {
        if (isBmsOwned)
        {
            if (effectiveScore == null)
            {
                return RankType.INVALID;
            }
            return (effectiveScore.rank != RankType.INVALID) ? effectiveScore.rank : RankType.F;
        }
        if (isBmsonOwned)
        {
            return RankType.INVALID;
        }
        return scoreProbe?.rank ?? RankType.INVALID;
    }

    private static int? ResolveMinBp(BMSFile scoreProbe, BMSScore effectiveScore)
    {
        if (effectiveScore != null)
        {
            return (effectiveScore.minbp == -1) ? effectiveScore.totalnotes : effectiveScore.minbp;
        }
        return scoreProbe?.minbp;
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

/// <summary>
/// playlist 未所持行の score snapshot 取得に使う最小の BMSFile 実装です。
/// UI 表示へは公開せず、source build 中だけ利用します。
/// </summary>
internal sealed class PlaylistScoreProbeBmsFile : BMSFile
{
    /// <summary>
    /// エントリ情報から score lookup 用の最小 snapshot を適用します。
    /// </summary>
    /// <param name="entry">対象エントリ。</param>
    internal void ApplyEntrySnapshot(BMSTableEntry entry, int? resolvedMode = null, string chartInfoSha256 = null)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        hash = entry.md5;
        sha256 = string.IsNullOrWhiteSpace(entry.sha256) ? chartInfoSha256 : entry.sha256;
        path = string.Empty;
        mode = resolvedMode;
    }
}
