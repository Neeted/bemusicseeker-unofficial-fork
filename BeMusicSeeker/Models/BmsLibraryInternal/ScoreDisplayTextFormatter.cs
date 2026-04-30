using System.Globalization;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ScoreDisplayTextFormatter
{
    internal static string FormatClear(ClearType clear)
    {
        switch (clear)
        {
            case ClearType.NO_SONG:
                return "NO SONG";
            case ClearType.NO_PLAY:
                return "NO PLAY";
            case ClearType.FAILED:
                return "FAILED";
            case ClearType.INVALID:
                return "ASSIST";
            case ClearType.L_ASSIST:
                return "L-ASSIST";
            case ClearType.EASY:
                return "EASY CLEAR";
            case ClearType.CLEAR:
                return "CLEAR";
            case ClearType.HARD:
                return "HARD CLEAR";
            case ClearType.EX_HARD:
                return "EX HARD";
            case ClearType.FC:
                return "FULL COMBO";
            case ClearType.PA:
                return "PERFECT";
            case ClearType.MAX:
                return "MAX";
            default:
                return ((int)clear).ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static string FormatRank(RankType rank)
    {
        return rank == RankType.INVALID ? string.Empty : rank.ToString();
    }
}
