using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// beatoraja の player score.db からアプリ内表示用 score を読み込みます。
/// </summary>
internal sealed class BeatorajaScoreDbLoader
{
    private const int NormalScoreMode = 0;

    /// <summary>
    /// 通常プレイ結果として扱う mode=0 の score rows を sha256 keyed score として読み込みます。
    /// </summary>
    /// <param name="scoreDbPath">beatoraja の player score.db path。</param>
    /// <returns>sha256 をキーにした score map。</returns>
    internal Dictionary<string, BMSScore> LoadModeZeroScores(string scoreDbPath)
    {
        Dictionary<string, BMSScore> scores = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(scoreDbPath) || !File.Exists(scoreDbPath))
        {
            return scores;
        }

        using SQLiteConnection connection = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        List<BeatorajaScoreRow> rows = connection.Query<BeatorajaScoreRow>(
            "SELECT sha256, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount FROM score WHERE mode = ?",
            NormalScoreMode);
        foreach (BeatorajaScoreRow row in rows)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.sha256))
            {
                continue;
            }

            string sha256 = NormalizeSha256(row.sha256);
            if (sha256.Length == 0)
            {
                continue;
            }

            scores[sha256] = Convert(row, sha256);
        }

        return scores;
    }

    private static BMSScore Convert(BeatorajaScoreRow row, string sha256)
    {
        int perfect = Math.Max(0, row.epg) + Math.Max(0, row.lpg);
        int great = Math.Max(0, row.egr) + Math.Max(0, row.lgr);
        int notes = Math.Max(0, row.notes);
        int score = perfect * 2 + great;
        return new BMSScore
        {
            hash = sha256,
            clear = ConvertClear(row.clear),
            perfect = perfect,
            great = great,
            totalnotes = notes,
            maxcombo = Math.Max(0, row.combo),
            minbp = row.minbp,
            playcount = Math.Max(0, row.playcount),
            clearcount = Math.Max(0, row.clearcount),
            rate = CalculateRate(score, notes),
            rank = CalculateRank(score, notes)
        };
    }

    private static ClearType ConvertClear(int clear)
    {
        if (clear < (int)ClearType.NO_PLAY || clear > (int)ClearType.MAX)
        {
            return ClearType.NO_PLAY;
        }

        return (ClearType)clear;
    }

    private static int CalculateRate(int score, int notes)
    {
        if (notes <= 0)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100, (int)Math.Floor(score * 100.0 / (notes * 2.0))));
    }

    private static RankType CalculateRank(int score, int notes)
    {
        if (notes <= 0)
        {
            return RankType.INVALID;
        }

        long scaledScore = (long)Math.Max(0, score) * 27L;
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

    private static string NormalizeSha256(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private sealed class BeatorajaScoreRow
    {
        public string sha256 { get; set; }

        public int clear { get; set; }

        public int epg { get; set; }

        public int lpg { get; set; }

        public int egr { get; set; }

        public int lgr { get; set; }

        public int notes { get; set; }

        public int combo { get; set; }

        public int minbp { get; set; }

        public int playcount { get; set; }

        public int clearcount { get; set; }
    }
}
