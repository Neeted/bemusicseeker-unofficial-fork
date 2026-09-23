using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ScoreValueCalculator
{
    internal static int CalculateExScore(int perfect, int great)
    {
        return Math.Max(0, perfect) * 2 + Math.Max(0, great);
    }

    internal static int? CalculateRatePercent(int? exScore, int? totalNotes)
    {
        double? rate = CalculateRateDouble(exScore, totalNotes);
        return rate.HasValue ? Math.Max(0, Math.Min(100, (int)Math.Floor(rate.Value * 100.0))) : null;
    }

    internal static double? CalculateRateDouble(int? exScore, int? totalNotes)
    {
        if (!exScore.HasValue || !totalNotes.HasValue || totalNotes.Value <= 0)
        {
            return null;
        }
        return Math.Max(0, exScore.Value) / 2.0 / totalNotes.Value;
    }

    internal static RankType CalculateRank(int? exScore, int? totalNotes)
    {
        if (!exScore.HasValue || !totalNotes.HasValue || totalNotes.Value <= 0)
        {
            return RankType.INVALID;
        }

        int score = Math.Max(0, exScore.Value);
        int notes = totalNotes.Value;
        long scaledScore = (long)score * 27L;
        long maxScore = (long)notes * 2L;
        if (score >= notes * 2)
        {
            return RankType.MAX;
        }
        if (scaledScore >= maxScore * 24L)
        {
            return RankType.AAA;
        }
        if (scaledScore >= maxScore * 21L)
        {
            return RankType.AA;
        }
        if (scaledScore >= maxScore * 18L)
        {
            return RankType.A;
        }
        if (scaledScore >= maxScore * 15L)
        {
            return RankType.B;
        }
        if (scaledScore >= maxScore * 12L)
        {
            return RankType.C;
        }
        if (scaledScore >= maxScore * 9L)
        {
            return RankType.D;
        }
        if (scaledScore >= maxScore * 6L)
        {
            return RankType.E;
        }

        return RankType.F;
    }
}
