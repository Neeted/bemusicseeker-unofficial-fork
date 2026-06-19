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
    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
        SourceProfile = sourceProfile ?? PlayHistorySourceProfile.Lr2(string.Empty);
        Provider = SourceProfile.Provider;
        Source = SourceProfile.DisplayName;
        SourcePath = SourceProfile.SourcePath;
        SourceKey = Raw.history_id.ToString(CultureInfo.InvariantCulture);
        HistoryId = Raw.history_id;
        PlayedAtUnix = Raw.played_at;
        PlayedAt = UnixEpochUtc.AddSeconds(Raw.played_at).ToLocalTime();
        Finalized = Raw.finalized != 0;
        ScoreWriteType = Raw.score_write_type ?? string.Empty;
        RawHash = rawHash ?? string.Empty;
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
        ResolveScoreProjection();
        ResolveActualResultProjection();
        Kind = ResolveKind();
        Option = BestScoreUpdated ? DecodeLr2OpBest(Raw.new_op_best) : string.Empty;
        OpHistory = FormatOpHistoryNewBits(OpHistoryNewBits);
    }

    internal Lr2PlayHistoryRecord Raw { get; }

    internal PlayHistorySourceProfile SourceProfile { get; }

    internal LibraryChartRef ResolvedChartRef { get; }

    internal ChartFile ResolvedChart { get; }

    public PlayHistoryProvider Provider { get; }

    public string Source { get; }

    public string SourcePath { get; }

    public string SourceKey { get; }

    public long HistoryId { get; }

    public long PlayedAtUnix { get; }

    public DateTime PlayedAt { get; }

    public bool Finalized { get; }

    public string ScoreWriteType { get; }

    public string RawHash { get; }

    public string Sha256 { get; }

    public PlayHistoryHashKind HashKind { get; }

    public bool IsSummaryEligible { get; }

    public string Title { get; }

    public string Artist { get; }

    public string FolderLabels { get; private set; }

    public string PlaylistNames { get; }

    public string Path { get; }

    public string Kind { get; private set; }

    public int? OldPlaycount => Raw.old_playcount;

    public int NewPlaycount => Raw.new_playcount;

    public int PlaycountDelta => Raw.playcount_delta;

    public ClearType? OldBestClear { get; private set; }

    public ClearType? NewBestClear { get; private set; }

    public string BestClear { get; private set; }

    public RankType BestDjLevel { get; private set; }

    public string BestDjLevelText { get; private set; }

    public double? BestRate { get; private set; }

    public int? BestRatePercent { get; private set; }

    public int? OldBestExscore => Raw.old_exscore;

    public int? NewBestExscore => Raw.new_exscore;

    public string BestExscore { get; private set; }

    public int? OldBestBp => Raw.old_minbp;

    public int? NewBestBp => Raw.new_minbp;

    public string BestBp { get; private set; }

    public int? OldBestCombo => Raw.old_maxcombo;

    public int? NewBestCombo => Raw.new_maxcombo;

    public string BestCombo { get; private set; }

    public int? PlayExscore { get; private set; }

    public int? JudgeTotal => Raw.judge_delta;

    public int? PlaytimeSeconds => Raw.playtime_delta;

    public int? Perfect => Raw.perfect_delta;

    public int? Great => Raw.great_delta;

    public int? Good => Raw.good_delta;

    public int? Bad => Raw.bad_delta;

    public int? Poor => Raw.poor_delta;

    public string Judges { get; private set; }

    public string Option { get; private set; }

    public int OpHistoryNewBits { get; private set; }

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

    internal PlayHistoryRow WithPlaylistDisplay(string folderLabels)
    {
        var clone = (PlayHistoryRow)MemberwiseClone();
        clone.FolderLabels = folderLabels ?? string.Empty;
        return clone;
    }

    private void ResolveScoreProjection()
    {
        OldBestClear = ConvertLr2Clear(Raw.old_clear, Raw.old_op_history);
        NewBestClear = ConvertLr2Clear(Raw.new_clear, Raw.new_op_history);
        BestClearUpdated = NewBestClear.HasValue && OldBestClear != NewBestClear;
        BestClear = BestClearUpdated ? FormatClearDelta(OldBestClear, NewBestClear) : string.Empty;

        BestScoreUpdated = Raw.new_exscore.HasValue && Raw.old_exscore != Raw.new_exscore;
        BestDjLevel = BestScoreUpdated
            ? ScoreValueCalculator.CalculateRank(Raw.new_exscore, Raw.new_totalnotes)
            : RankType.INVALID;
        BestDjLevelText = ScoreDisplayTextFormatter.FormatRank(BestDjLevel);
        BestRate = BestScoreUpdated ? ScoreValueCalculator.CalculateRateDouble(Raw.new_exscore, Raw.new_totalnotes) : null;
        BestRatePercent = BestScoreUpdated ? ScoreValueCalculator.CalculateRatePercent(Raw.new_exscore, Raw.new_totalnotes) : null;
        BestExscore = BestScoreUpdated ? FormatNullableDelta(Raw.old_exscore, Raw.new_exscore) : string.Empty;

        BestBpUpdated = Raw.new_minbp.HasValue && (!Raw.old_minbp.HasValue || Raw.new_minbp.Value < Raw.old_minbp.Value);
        BestBp = BestBpUpdated ? FormatBpDelta(Raw.old_minbp, Raw.new_minbp) : string.Empty;

        BestComboUpdated = Raw.new_maxcombo.HasValue && (!Raw.old_maxcombo.HasValue || Raw.new_maxcombo.Value > Raw.old_maxcombo.Value);
        BestCombo = BestComboUpdated ? FormatNullableDelta(Raw.old_maxcombo, Raw.new_maxcombo) : string.Empty;

        OpHistoryNewBits = (Raw.new_op_history ?? 0) & ~(Raw.old_op_history ?? 0);
    }

    private void ResolveActualResultProjection()
    {
        if (!Finalized)
        {
            Judges = string.Empty;
            return;
        }

        if (Raw.perfect_delta.HasValue || Raw.great_delta.HasValue)
        {
            PlayExscore = ScoreValueCalculator.CalculateExScore(Raw.perfect_delta ?? 0, Raw.great_delta ?? 0);
        }
        Judges = FormatJudges(Raw.perfect_delta, Raw.great_delta, Raw.good_delta, Raw.bad_delta, Raw.poor_delta);
    }

    private string ResolveKind()
    {
        if (BestScoreUpdated)
        {
            return "score";
        }
        if (BestBpUpdated)
        {
            return "bp";
        }
        if (BestClearUpdated)
        {
            return "clear";
        }
        if (BestComboUpdated)
        {
            return "combo";
        }
        return "play";
    }

    private static string ResolveFolderLabels(PlaylistReferenceDisplay playlistReference)
    {
        return playlistReference?.Symbols ?? string.Empty;
    }

    private static ClearType? ConvertLr2Clear(int? value, int? opHistory)
    {
        if (!value.HasValue)
        {
            return null;
        }
        ClearType clear = ClearTypeStorageConverter.FromLr2Value(value.Value);
        return clear == ClearType.FC && ((opHistory ?? 0) & 0x10) != 0
            ? ClearType.PA
            : clear;
    }

    private static string FormatClearDelta(ClearType? oldValue, ClearType? newValue)
    {
        if (!newValue.HasValue)
        {
            return string.Empty;
        }
        string newText = ScoreDisplayTextFormatter.FormatClear(newValue.Value);
        if (!oldValue.HasValue)
        {
            return ScoreDisplayTextFormatter.FormatClear(ClearType.NO_PLAY) + " -> " + newText;
        }
        string oldText = ScoreDisplayTextFormatter.FormatClear(oldValue.Value);
        return string.Equals(oldText, newText, StringComparison.Ordinal) ? newText : oldText + " -> " + newText;
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
            return "BP " + newValue.Value.ToString(CultureInfo.InvariantCulture);
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

    private static string FormatOpHistoryNewBits(int bits)
    {
        return bits == 0 ? string.Empty : "0x" + bits.ToString("X8", CultureInfo.InvariantCulture);
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
        return new PlayHistoryDiagnostic
        {
            Provider = PlayHistoryProvider.Lr2,
            Stage = "projection",
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }
}
