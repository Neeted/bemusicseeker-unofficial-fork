using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryProjectionResult
{
    internal PlayHistoryProjectionResult(IReadOnlyList<PlayHistoryRow> rows, IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        Rows = rows ?? [];
        Diagnostics = diagnostics ?? [];
    }

    internal IReadOnlyList<PlayHistoryRow> Rows { get; }

    internal IReadOnlyList<PlayHistoryDiagnostic> Diagnostics { get; }
}

internal sealed class PlayHistoryRow
{
    private PlayHistoryRow(
        Lr2PlayHistoryRecord raw,
        PlayHistorySourceProfile sourceProfile,
        string rawHash,
        string sha256,
        LibraryChartRef chartRef,
        ChartFile chart,
        PlaylistReferenceDisplay playlistReference,
        PlayHistoryHashKind hashKind)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        BeatorajaRaw = null;
        SourceProfile = sourceProfile ?? PlayHistorySourceProfile.Lr2(string.Empty);
        Provider = SourceProfile.Provider;
        Source = SourceProfile.DisplayName;
        SourcePath = SourceProfile.SourcePath;
        SourceKey = Raw.history_id.ToString(CultureInfo.InvariantCulture);
        HistoryId = Raw.history_id;
        PlayedAtUnix = Raw.played_at;
        PlayedAtWallClockSecond = PlayHistoryWallClockSecond.FromUnixSeconds(Raw.played_at, TimeZoneInfo.Local);
        PlayedAt = ToLocalDateTime(PlayedAtWallClockSecond);
        Finalized = Raw.finalized != 0;
        ScoreWriteType = Raw.score_write_type ?? string.Empty;
        RawHash = rawHash ?? string.Empty;
        Md5 = rawHash ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        ResolvedChart = chart;
        ResolvedChartRef = chartRef;
        HashKind = hashKind;
        IsSummaryEligible = HashKind is PlayHistoryHashKind.Chart or PlayHistoryHashKind.NonstopCourse;
        Title = chart?.Title ?? string.Empty;
        Artist = chart?.Artist ?? string.Empty;
        Path = chart?.Path ?? string.Empty;
        FolderLabels = ResolveFolderLabels(playlistReference);
        PlaylistNames = playlistReference?.Names ?? string.Empty;
        OldPlaycount = Raw.old_playcount;
        NewPlaycount = Raw.new_playcount;
        PlaycountDelta = Raw.playcount_delta;
        bool oldScoreDetailUnavailable = IsLr2ScoreDetailUnavailable(Raw.old_clear, Raw.old_op_history, Raw.old_minbp);
        bool newScoreDetailUnavailable = IsLr2ScoreDetailUnavailable(Raw.new_clear, Raw.new_op_history, Raw.new_minbp);
        OldBestExscore = oldScoreDetailUnavailable ? null : Raw.old_exscore;
        NewBestExscore = newScoreDetailUnavailable ? null : Raw.new_exscore;
        OldBestBp = NormalizeLr2MinBp(Raw.old_minbp);
        NewBestBp = NormalizeLr2MinBp(Raw.new_minbp);
        OldBestCombo = oldScoreDetailUnavailable ? null : Raw.old_maxcombo;
        NewBestCombo = newScoreDetailUnavailable ? null : Raw.new_maxcombo;
        JudgeTotal = Raw.judge_delta;
        PlaytimeSeconds = Raw.playtime_delta;
        Perfect = Raw.perfect_delta;
        Great = Raw.great_delta;
        Good = Raw.good_delta;
        Bad = Raw.bad_delta;
        Poor = Raw.poor_delta;
        ResolveScoreProjection(Raw.old_clear, Raw.new_clear, Raw.old_op_history, Raw.new_op_history);
        ResolveActualResultProjection();
        Kind = ResolveKind();
        Option = BestScoreUpdated ? DecodeLr2OpBest(Raw.new_op_best) : string.Empty;
        OpHistory = FormatOpHistoryDelta(OpHistoryNewBits, OpHistoryRemovedBits);
    }

    private PlayHistoryRow(
        BeatorajaPlayHistoryRecord raw,
        PlayHistorySourceProfile sourceProfile,
        string md5,
        string sha256,
        LibraryChartRef chartRef,
        ChartFile chart,
        PlaylistReferenceDisplay playlistReference,
        PlayHistoryHashKind hashKind)
    {
        BeatorajaRaw = raw ?? throw new ArgumentNullException(nameof(raw));
        Raw = null;
        SourceProfile = sourceProfile ?? PlayHistorySourceProfile.Beatoraja(string.Empty);
        Provider = SourceProfile.Provider;
        Source = SourceProfile.DisplayName;
        SourcePath = SourceProfile.SourcePath;
        SourceKey = BeatorajaRaw.history_id.ToString(CultureInfo.InvariantCulture);
        HistoryId = BeatorajaRaw.history_id;
        PlayedAtUnix = BeatorajaRaw.played_at;
        PlayedAtWallClockSecond = PlayHistoryWallClockSecond.FromUnixSeconds(BeatorajaRaw.played_at, TimeZoneInfo.Local);
        PlayedAt = ToLocalDateTime(PlayedAtWallClockSecond);
        Finalized = true;
        ScoreWriteType = BeatorajaRaw.HasBestDelta ? "best-update" : "play";
        RawHash = sha256 ?? string.Empty;
        Md5 = md5 ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        ResolvedChart = chart;
        ResolvedChartRef = chartRef;
        HashKind = hashKind;
        IsSummaryEligible = HashKind is PlayHistoryHashKind.Chart or PlayHistoryHashKind.NonstopCourse;
        Title = chart?.Title ?? string.Empty;
        Artist = chart?.Artist ?? string.Empty;
        Path = chart?.Path ?? string.Empty;
        FolderLabels = ResolveFolderLabels(playlistReference);
        PlaylistNames = playlistReference?.Names ?? string.Empty;
        bool hasActualResult = BeatorajaRaw.has_actual_result;
        OldPlaycount = hasActualResult && BeatorajaRaw.playcount > 0 ? BeatorajaRaw.playcount - 1 : null;
        NewPlaycount = hasActualResult ? BeatorajaRaw.playcount : 0;
        PlaycountDelta = hasActualResult ? 1 : 0;
        OldBestExscore = BeatorajaRaw.old_exscore;
        NewBestExscore = BeatorajaRaw.new_exscore;
        OldBestBp = BeatorajaRaw.old_minbp;
        NewBestBp = BeatorajaRaw.new_minbp;
        OldBestCombo = BeatorajaRaw.old_maxcombo;
        NewBestCombo = BeatorajaRaw.new_maxcombo;
        Perfect = hasActualResult ? BeatorajaRaw.epg + BeatorajaRaw.lpg : null;
        Great = hasActualResult ? BeatorajaRaw.egr + BeatorajaRaw.lgr : null;
        Good = hasActualResult ? BeatorajaRaw.egd + BeatorajaRaw.lgd : null;
        Bad = hasActualResult ? BeatorajaRaw.ebd + BeatorajaRaw.lbd : null;
        Poor = hasActualResult ? BeatorajaRaw.epr + BeatorajaRaw.lpr + BeatorajaRaw.ems + BeatorajaRaw.lms : null;
        JudgeTotal = hasActualResult ? Perfect + Great + Good + Bad + Poor : null;
        PlaytimeSeconds = null;
        ResolveScoreProjection(BeatorajaRaw.old_clear, BeatorajaRaw.new_clear, null, null);
        ResolveActualResultProjection();
        Kind = ResolveKind();
        Option = hasActualResult ? FormatBeatorajaOption(BeatorajaRaw.option, BeatorajaRaw.random, BeatorajaRaw.seed) : string.Empty;
        OpHistoryNewBits = 0;
        OpHistory = string.Empty;
    }

    internal Lr2PlayHistoryRecord Raw { get; }

    internal BeatorajaPlayHistoryRecord BeatorajaRaw { get; }

    internal PlayHistorySourceProfile SourceProfile { get; }

    internal LibraryChartRef ResolvedChartRef { get; }

    internal ChartFile ResolvedChart { get; }

    public PlayHistoryProvider Provider { get; }

    public string Source { get; }

    public string SourcePath { get; }

    public string SourceKey { get; }

    public long HistoryId { get; }

    public long PlayedAtUnix { get; }

    /// <summary>
    /// Gets the displayed local wall-clock timestamp at whole-second precision.
    /// </summary>
    internal PlayHistoryWallClockSecond PlayedAtWallClockSecond { get; }

    public DateTime PlayedAt { get; }

    public bool Finalized { get; }

    public string ScoreWriteType { get; }

    public string RawHash { get; }

    public string Md5 { get; }

    public string Sha256 { get; }

    public PlayHistoryHashKind HashKind { get; }

    public bool IsSummaryEligible { get; }

    public string Title { get; }

    public string Artist { get; }

    public string FolderLabels { get; private set; }

    public string PlaylistNames { get; }

    public string Path { get; }

    public string Kind { get; private set; }

    public int? OldPlaycount { get; private set; }

    public int NewPlaycount { get; private set; }

    public int PlaycountDelta { get; private set; }

    public ClearType? OldBestClear { get; private set; }

    public ClearType? NewBestClear { get; private set; }

    public string BestClear { get; private set; }

    public RankType OldBestDjLevel { get; private set; }

    public RankType BestDjLevel { get; private set; }

    public string BestDjLevelText { get; private set; }

    public double? BestRate { get; private set; }

    public int? BestRatePercent { get; private set; }

    public string BestRateText { get; private set; }

    public int? OldBestExscore { get; private set; }

    public int? NewBestExscore { get; private set; }

    public string BestExscore { get; private set; }

    public int? OldBestBp { get; private set; }

    public int? NewBestBp { get; private set; }

    public string BestBp { get; private set; }

    public int? OldBestCombo { get; private set; }

    public int? NewBestCombo { get; private set; }

    public string BestCombo { get; private set; }

    public int? PlayExscore { get; private set; }

    public int? JudgeTotal { get; private set; }

    public int? PlaytimeSeconds { get; private set; }

    public int? Perfect { get; private set; }

    public int? Great { get; private set; }

    public int? Good { get; private set; }

    public int? Bad { get; private set; }

    public int? Poor { get; private set; }

    public string Judges { get; private set; }

    public string Option { get; private set; }

    public int OpHistoryNewBits { get; private set; }

    public int OpHistoryRemovedBits { get; private set; }

    public string OpHistory { get; private set; }

    public bool BestScoreUpdated { get; private set; }

    public bool BestClearUpdated { get; private set; }

    public bool BestBpUpdated { get; private set; }

    public bool BestComboUpdated { get; private set; }

    internal static PlayHistoryProjectionResult ProjectLr2Rows(Lr2PlayHistoryReadResult readResult, PlayHistoryProjectionIndex projectionIndex)
    {
        var diagnostics = new List<PlayHistoryDiagnostic>(readResult?.Diagnostics ?? []);
        var rows = new List<PlayHistoryRow>();
        PlayHistoryProjectionIndex safeIndex = projectionIndex ?? PlayHistoryProjectionIndex.Empty;
        PlayHistorySourceProfile sourceProfile = readResult?.SourceProfile ?? PlayHistorySourceProfile.Lr2(string.Empty);
        foreach (Lr2PlayHistoryRecord record in readResult?.Rows ?? [])
        {
            string md5 = NormalizeHash(record?.hash);
            if (string.IsNullOrWhiteSpace(md5))
            {
                diagnostics.Add(CreateProjectionDiagnostic(
                    PlayHistoryDiagnosticSeverity.Warning,
                    "play_history_projection_empty_hash",
                    "Play history row has no hash.",
                    sourceProfile.SourcePath));
            }

            string digestSha256 = safeIndex.ResolveSha256(md5, null);
            LibraryChartRef chartRef = safeIndex.ResolveChartByMd5(md5, digestSha256);
            string resolvedSha256 = safeIndex.ResolveSha256(md5, chartRef?.Sha256 ?? digestSha256);
            LR2SongDBExtended.chart_info chartInfo = safeIndex.ResolveChartInfo(resolvedSha256, md5);
            resolvedSha256 = safeIndex.ResolveSha256(md5, FirstNonEmpty(resolvedSha256, chartInfo?.sha256));
            ChartFile chart = chartRef?.ToChartFileIdentity() ?? chartRef?.ToChartFile();
            PlaylistReferenceDisplay playlistReference = safeIndex.ResolvePlaylistReference(md5, resolvedSha256);
            rows.Add(new PlayHistoryRow(
                record,
                sourceProfile,
                md5,
                resolvedSha256,
                chartRef,
                chart,
                playlistReference,
                chartRef != null ? PlayHistoryHashKind.Chart : PlayHistoryHashKind.Unknown));
        }

        return new PlayHistoryProjectionResult(rows, diagnostics);
    }

    internal static PlayHistoryProjectionResult ProjectBeatorajaRows(BeatorajaPlayHistoryReadResult readResult, PlayHistoryProjectionIndex projectionIndex)
    {
        var diagnostics = new List<PlayHistoryDiagnostic>(readResult?.Diagnostics ?? []);
        var rows = new List<PlayHistoryRow>();
        PlayHistoryProjectionIndex safeIndex = projectionIndex ?? PlayHistoryProjectionIndex.Empty;
        PlayHistorySourceProfile sourceProfile = readResult?.SourceProfile ?? PlayHistorySourceProfile.Beatoraja(string.Empty);
        foreach (BeatorajaPlayHistoryRecord record in readResult?.Rows ?? [])
        {
            string sha256 = NormalizeHash(record?.sha256);
            if (string.IsNullOrWhiteSpace(sha256))
            {
                diagnostics.Add(CreateProjectionDiagnostic(
                    PlayHistoryProvider.Beatoraja,
                    PlayHistoryDiagnosticSeverity.Warning,
                    "play_history_projection_empty_sha256",
                    "beatoraja play history row has no SHA-256.",
                    sourceProfile.SourcePath));
            }

            LibraryChartRef chartRef = safeIndex.ResolveChartByMd5(string.Empty, sha256);
            LR2SongDBExtended.chart_info chartInfo = safeIndex.ResolveChartInfo(sha256, string.Empty);
            string resolvedMd5 = FirstNonEmpty(chartRef?.Md5, chartInfo?.md5);
            ChartFile chart = chartRef?.ToChartFileIdentity() ?? chartRef?.ToChartFile();
            PlaylistReferenceDisplay playlistReference = safeIndex.ResolvePlaylistReference(resolvedMd5, sha256);
            rows.Add(new PlayHistoryRow(
                record,
                sourceProfile,
                resolvedMd5,
                sha256,
                chartRef,
                chart,
                playlistReference,
                chartRef != null || chartInfo != null ? PlayHistoryHashKind.Chart : PlayHistoryHashKind.Unknown));
        }

        return new PlayHistoryProjectionResult(rows, diagnostics);
    }

    internal PlayHistoryRow WithPlaylistDisplay(string folderLabels)
    {
        var clone = (PlayHistoryRow)MemberwiseClone();
        clone.FolderLabels = folderLabels ?? string.Empty;
        return clone;
    }

    private static DateTime ToLocalDateTime(PlayHistoryWallClockSecond wallClock)
    {
        return DateTime.SpecifyKind(wallClock.ToDateTime(), DateTimeKind.Local);
    }

    private void ResolveScoreProjection(int? oldClear, int? newClear, int? oldOpHistory, int? newOpHistory)
    {
        OldBestClear = ConvertClear(oldClear, oldOpHistory, Provider);
        NewBestClear = ConvertClear(newClear, newOpHistory, Provider);
        BestClearUpdated = NewBestClear.HasValue && OldBestClear != NewBestClear;
        BestClear = BestClearUpdated ? FormatClearDelta(OldBestClear, NewBestClear) : string.Empty;

        BestScoreUpdated = NewBestExscore.HasValue && OldBestExscore != NewBestExscore;
        int? bestTotalNotes = ResolveBestTotalNotes();
        OldBestDjLevel = BestScoreUpdated
            ? ScoreValueCalculator.CalculateRank(OldBestExscore, bestTotalNotes)
            : RankType.INVALID;
        BestDjLevel = BestScoreUpdated
            ? ScoreValueCalculator.CalculateRank(NewBestExscore, bestTotalNotes)
            : RankType.INVALID;
        BestDjLevelText = BestScoreUpdated ? FormatRankDelta(OldBestExscore, NewBestExscore, bestTotalNotes) : string.Empty;
        BestRate = BestScoreUpdated ? ScoreValueCalculator.CalculateRateDouble(NewBestExscore, bestTotalNotes) : null;
        BestRatePercent = BestScoreUpdated ? ScoreValueCalculator.CalculateRatePercent(NewBestExscore, bestTotalNotes) : null;
        BestRateText = BestScoreUpdated ? FormatRateDelta(OldBestExscore, NewBestExscore, bestTotalNotes) : string.Empty;
        BestExscore = BestScoreUpdated ? FormatNullableDelta(OldBestExscore, NewBestExscore) : string.Empty;

        BestBpUpdated = NewBestBp.HasValue && (!OldBestBp.HasValue || NewBestBp.Value < OldBestBp.Value);
        BestBp = BestBpUpdated ? FormatBpDelta(OldBestBp, NewBestBp) : string.Empty;

        BestComboUpdated = NewBestCombo.HasValue && (!OldBestCombo.HasValue || NewBestCombo.Value > OldBestCombo.Value);
        BestCombo = BestComboUpdated ? FormatNullableDelta(OldBestCombo, NewBestCombo) : string.Empty;

        OpHistoryNewBits = (newOpHistory ?? 0) & ~(oldOpHistory ?? 0);
        OpHistoryRemovedBits = (oldOpHistory ?? 0) & ~(newOpHistory ?? 0);
    }

    private void ResolveActualResultProjection()
    {
        if (!Finalized)
        {
            Judges = string.Empty;
            return;
        }

        if (Perfect.HasValue || Great.HasValue)
        {
            PlayExscore = ScoreValueCalculator.CalculateExScore(Perfect ?? 0, Great ?? 0);
        }
        Judges = FormatJudges(Perfect, Great, Good, Bad, Poor);
    }

    private string ResolveKind()
    {
        var states = new List<string>();
        if (BestScoreUpdated)
        {
            states.Add("score");
        }
        if (BestBpUpdated)
        {
            states.Add("bp");
        }
        if (BestClearUpdated)
        {
            states.Add("clear");
        }
        if (BestComboUpdated)
        {
            states.Add("combo");
        }
        return states.Count > 0 ? string.Join(" ", states) : "play";
    }

    private static string ResolveFolderLabels(PlaylistReferenceDisplay playlistReference)
    {
        return playlistReference?.Symbols ?? string.Empty;
    }

    private int? ResolveBestTotalNotes()
    {
        if (Provider == PlayHistoryProvider.Beatoraja)
        {
            return BeatorajaRaw?.notes;
        }
        return Raw?.new_totalnotes;
    }

    private static ClearType? ConvertClear(int? value, int? opHistory, PlayHistoryProvider provider)
    {
        if (!value.HasValue)
        {
            return null;
        }
        if (provider == PlayHistoryProvider.Beatoraja)
        {
            return value.Value < (int)ClearType.NO_PLAY || value.Value > (int)ClearType.MAX
                ? ClearType.NO_PLAY
                : (ClearType)value.Value;
        }
        return ClearTypeStorageConverter.FromLr2ScoreValue(value.Value, opHistory ?? 0);
    }

    private static bool IsLr2ScoreDetailUnavailable(int? clear, int? opHistory, int? minBp)
    {
        return clear == 2
            && ((opHistory ?? 0) & ClearTypeStorageConverter.OptionHistoryEasy) == 0
            && minBp.HasValue
            && minBp.Value < 0;
    }

    private static int? NormalizeLr2MinBp(int? value)
    {
        return value.HasValue && value.Value >= 0 ? value : null;
    }

    private static string FormatClearDelta(ClearType? oldValue, ClearType? newValue)
    {
        if (!newValue.HasValue)
        {
            return string.Empty;
        }
        string newText = FormatClearShort(newValue.Value);
        if (!oldValue.HasValue)
        {
            return FormatClearShort(ClearType.NO_PLAY) + " -> " + newText;
        }
        string oldText = FormatClearShort(oldValue.Value);
        return string.Equals(oldText, newText, StringComparison.Ordinal) ? newText : oldText + " -> " + newText;
    }

    private static string FormatClearShort(ClearType clear)
    {
        return clear switch
        {
            ClearType.NO_SONG => "NO SONG",
            ClearType.NO_PLAY => "NP",
            ClearType.INVALID or ClearType.L_ASSIST => "ASSIST",
            ClearType.EASY => "EASY",
            ClearType.CLEAR => "NORMAL",
            ClearType.HARD => "HARD",
            ClearType.EX_HARD => "EXH",
            ClearType.FC => "FC",
            ClearType.PA => "PA",
            _ => ScoreDisplayTextFormatter.FormatClear(clear),
        };
    }

    private static string FormatNullableDelta(int? oldValue, int? newValue)
    {
        if (!newValue.HasValue)
        {
            return string.Empty;
        }
        if (!oldValue.HasValue)
        {
            return newValue.Value.ToString(CultureInfo.InvariantCulture);
        }
        return oldValue.Value == newValue.Value
            ? newValue.Value.ToString(CultureInfo.InvariantCulture)
            : oldValue.Value.ToString(CultureInfo.InvariantCulture) + " -> " + newValue.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatBpDelta(int? oldValue, int? newValue)
    {
        if (!newValue.HasValue)
        {
            return string.Empty;
        }
        if (!oldValue.HasValue)
        {
            return newValue.Value.ToString(CultureInfo.InvariantCulture);
        }
        return oldValue.Value == newValue.Value
            ? newValue.Value.ToString(CultureInfo.InvariantCulture)
            : oldValue.Value.ToString(CultureInfo.InvariantCulture) + " -> " + newValue.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatJudges(int? perfect, int? great, int? good, int? bad, int? poor)
    {
        if (!perfect.HasValue && !great.HasValue && !good.HasValue && !bad.HasValue && !poor.HasValue)
        {
            return string.Empty;
        }
        return "PG " + (perfect ?? 0).ToString(CultureInfo.InvariantCulture)
            + " / GR " + (great ?? 0).ToString(CultureInfo.InvariantCulture)
            + " / GD " + (good ?? 0).ToString(CultureInfo.InvariantCulture)
            + " / BD " + (bad ?? 0).ToString(CultureInfo.InvariantCulture)
            + " / PR " + (poor ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatRankDelta(int? oldExscore, int? newExscore, int? totalNotes)
    {
        if (!newExscore.HasValue)
        {
            return string.Empty;
        }
        RankType newRank = ScoreValueCalculator.CalculateRank(newExscore, totalNotes);
        string newText = ScoreDisplayTextFormatter.FormatRank(newRank);
        if (!oldExscore.HasValue)
        {
            return newText;
        }
        RankType oldRank = ScoreValueCalculator.CalculateRank(oldExscore, totalNotes);
        string oldText = ScoreDisplayTextFormatter.FormatRank(oldRank);
        return string.Equals(oldText, newText, StringComparison.Ordinal) ? newText : oldText + " -> " + newText;
    }

    private static string FormatRateDelta(int? oldExscore, int? newExscore, int? totalNotes)
    {
        if (!newExscore.HasValue)
        {
            return string.Empty;
        }
        string newText = FormatRate(ScoreValueCalculator.CalculateRateDouble(newExscore, totalNotes));
        if (!oldExscore.HasValue)
        {
            return newText;
        }
        string oldText = FormatRate(ScoreValueCalculator.CalculateRateDouble(oldExscore, totalNotes));
        return string.Equals(oldText, newText, StringComparison.Ordinal) ? newText : oldText + " -> " + newText;
    }

    private static string FormatRate(double? rate)
    {
        return rate.HasValue
            ? (rate.Value * 100d).ToString("F2", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private static string DecodeLr2OpBest(int? opBest)
    {
        if (!opBest.HasValue)
        {
            return string.Empty;
        }

        int value = opBest.Value;
        int gauge = value % 10;
        int random1 = (value / 10) % 10;
        int random2 = (value / 100) % 10;
        int dpFlip = (value / 1000) % 10;
        string text = GetGaugeName(gauge) + " " + GetRandomName(random1);
        if (random2 != 0 || dpFlip != 0)
        {
            text += " / " + GetRandomName(random2);
        }
        if (dpFlip != 0)
        {
            text += " DPFLIP";
        }
        return text.Trim();
    }

    private static string FormatBeatorajaOption(int option, int random, long seed)
    {
        var parts = new List<string>();
        int option1p = option % 10;
        int option2p = (option / 10) % 10;
        int doubleOption = (option / 100) % 10;
        if (option2p != 0 || doubleOption != 0)
        {
            parts.Add("1P " + GetBeatorajaRandomOptionName(option1p));
            parts.Add("2P " + GetBeatorajaRandomOptionName(option2p));
            if (doubleOption != 0)
            {
                parts.Add(GetBeatorajaDoubleOptionName(doubleOption));
            }
        }
        else if (option1p != 0)
        {
            parts.Add(GetBeatorajaRandomOptionName(option1p));
        }
        return string.Join(" / ", parts);
    }

    private static string GetBeatorajaRandomOptionName(int value)
    {
        return value switch
        {
            0 => "OFF",
            1 => "MIRROR",
            2 => "RANDOM",
            3 => "R-RANDOM",
            4 => "S-RANDOM",
            5 => "SPIRAL",
            6 => "H-RANDOM",
            7 => "ALL-SCR",
            8 => "RANDOM-EX",
            9 => "S-RANDOM-EX",
            _ => "OPTION " + value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string GetBeatorajaDoubleOptionName(int value)
    {
        return value switch
        {
            1 => "FLIP",
            2 => "BATTLE",
            3 => "BATTLE AS",
            _ => "DP OPTION " + value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string GetGaugeName(int value)
    {
        return value switch
        {
            0 => "GROOVE",
            1 => "SURVIVAL",
            2 => "DEATH",
            3 => "EASY",
            4 => "P-ATTACK",
            5 => "G-ATTACK",
            _ => "GAUGE " + value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string GetRandomName(int value)
    {
        return value switch
        {
            0 => "NORMAL",
            1 => "MIRROR",
            2 => "RANDOM",
            3 => "S-RANDOM",
            4 => "SCATTER",
            5 => "CONVERGE",
            _ => "RANDOM " + value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string FormatOpHistoryDelta(int newBits, int removedBits)
    {
        var parts = new List<string>();
        AddOpHistoryBitNames(parts, newBits, removed: false);
        AddOpHistoryBitNames(parts, removedBits, removed: true);
        return string.Join(" / ", parts);
    }

    private static void AddOpHistoryBitNames(List<string> parts, int bits, bool removed)
    {
        if (bits == 0)
        {
            return;
        }

        uint remaining = unchecked((uint)bits);
        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            if ((remaining & mask) == 0u)
            {
                continue;
            }
            string name = GetOpHistoryBitName(mask);
            parts.Add(removed ? name + " off" : name);
            remaining &= ~mask;
        }
    }

    private static string GetOpHistoryBitName(uint bit)
    {
        return bit switch
        {
            0x00000001u => "GROOVE",
            0x00000002u => "SURVIVAL",
            0x00000004u => "DEATH",
            0x00000008u => "EASY",
            0x00000010u => "P.A",
            0x00000020u => "G.A",
            0x00000040u => "GAUGE 6",
            0x00000080u => "GAUGE 7",
            0x00000100u => "NORMAL",
            0x00000200u => "MIRROR",
            0x00000400u => "RANDOM",
            0x00000800u => "S-RANDOM",
            0x00001000u => "SCATTER",
            0x00002000u => "CONVERGE",
            0x00004000u => "RANDOM 6",
            0x00008000u => "RANDOM 7",
            0x00010000u => "HIDSUD 0",
            0x00020000u => "HIDSUD 1",
            0x00040000u => "HIDSUD 2",
            0x00080000u => "HIDSUD 3",
            0x00100000u => "HIDSUD 4",
            0x00200000u => "HIDSUD 5",
            0x00400000u => "HIDSUD 6",
            0x00800000u => "HIDSUD 7",
            0x01000000u => "ASSIST",
            0x02000000u => "EXTRA",
            0x04000000u => "D-BATTLE",
            0x08000000u => "SP-to-DP",
            _ => "0x" + bit.ToString("X8", CultureInfo.InvariantCulture)
        };
    }

    private static string NormalizeHash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static PlayHistoryDiagnostic CreateProjectionDiagnostic(PlayHistoryDiagnosticSeverity severity, string code, string message, string sourcePath)
    {
        return CreateProjectionDiagnostic(PlayHistoryProvider.Lr2, severity, code, message, sourcePath);
    }

    private static PlayHistoryDiagnostic CreateProjectionDiagnostic(PlayHistoryProvider provider, PlayHistoryDiagnosticSeverity severity, string code, string message, string sourcePath)
    {
        return new PlayHistoryDiagnostic
        {
            Provider = provider,
            Stage = "projection",
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }
}
