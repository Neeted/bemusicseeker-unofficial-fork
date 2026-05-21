using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal sealed class ChartScoreSnapshot
{
    internal static readonly ChartScoreSnapshot MissingChart = new(ClearType.NO_SONG);

    internal ChartScoreSnapshot(
        ClearType clear,
        RankType rank = RankType.INVALID,
        int? score = null,
        int? rate = null,
        double? rateDouble = null,
        int? totalNotes = null,
        int? minBp = null,
        int? maxCombo = null,
        int? ranking = null,
        int? rankingNum = null,
        string rankingString = "",
        DateTime? rankingLastUpdate = null,
        double? stdDevVal = null,
        double? scoreDifficulty = null)
    {
        Clear = clear;
        Rank = rank;
        Score = score;
        Rate = rate;
        RateDouble = rateDouble;
        TotalNotes = totalNotes;
        MinBp = minBp;
        MaxCombo = maxCombo;
        Ranking = ranking;
        RankingNum = rankingNum;
        RankingString = rankingString ?? string.Empty;
        RankingLastUpdate = rankingLastUpdate;
        StdDevVal = stdDevVal;
        ScoreDifficulty = scoreDifficulty;
    }

    internal ClearType Clear { get; }

    internal RankType Rank { get; }

    internal int? Score { get; }

    internal int? Rate { get; }

    internal double? RateDouble { get; }

    internal int? TotalNotes { get; }

    internal int? MinBp { get; }

    internal int? MaxCombo { get; }

    internal int? Ranking { get; }

    internal int? RankingNum { get; }

    internal string RankingString { get; }

    internal DateTime? RankingLastUpdate { get; }

    internal double? StdDevVal { get; }

    internal double? ScoreDifficulty { get; }

    internal static ChartScoreSnapshot NoScore(string chartPath)
    {
        return string.IsNullOrWhiteSpace(chartPath)
            ? MissingChart
            : new ChartScoreSnapshot(ClearType.NO_PLAY);
    }

    internal static ChartScoreSnapshot FromBmsFile(BMSFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.path))
        {
            return MissingChart;
        }

        BMSScore score = file.bmsScore;
        if (score == null)
        {
            return NoScore(file.path);
        }

        RankType rank = score.rank != RankType.INVALID ? score.rank : RankType.F;
        ClearType clear = score.clear >= ClearType.EASY && rank == RankType.INVALID
            ? ClearType.INVALID
            : score.clear;
        int? ranking = score.ranking != 0 ? score.ranking : null;
        int? rankingNum = score.rankingNum != 0 ? score.rankingNum : null;
        return new ChartScoreSnapshot(
            clear,
            rank,
            score.score,
            score.rate,
            score.totalnotes > 0 ? (double)score.score / 2.0 / score.totalnotes : null,
            score.totalnotes,
            score.minbp == -1 ? score.totalnotes : score.minbp,
            score.maxcombo,
            ranking,
            rankingNum,
            ranking.HasValue && ranking != -1 && rankingNum.HasValue
                ? score.ranking.ToString().PadLeft(score.rankingNum.ToString().Length) + "/" + score.rankingNum
                : string.Empty,
            score.rankingLastupdate,
            score.stddevVal,
            score.scoreDifficulty);
    }
}
