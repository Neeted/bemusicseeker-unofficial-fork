using System.Globalization;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ScoreDisplayTextFormatter
{
    internal static string FormatClear(ClearType clear)
    {
        return clear switch
        {
            ClearType.NO_SONG => "NO SONG",
            ClearType.NO_PLAY => "NO PLAY",
            ClearType.FAILED => "FAILED",
            ClearType.INVALID => "ASSIST",
            ClearType.L_ASSIST => "L-ASSIST",
            ClearType.EASY => "EASY CLEAR",
            ClearType.CLEAR => "CLEAR",
            ClearType.HARD => "HARD CLEAR",
            ClearType.EX_HARD => "EX HARD",
            ClearType.FC => "FULL COMBO",
            ClearType.PA => "PERFECT",
            ClearType.MAX => "MAX",
            _ => ((int)clear).ToString(CultureInfo.InvariantCulture),
        };
    }

    internal static string FormatRank(RankType rank)
    {
        return rank == RankType.INVALID ? string.Empty : rank.ToString();
    }
}
