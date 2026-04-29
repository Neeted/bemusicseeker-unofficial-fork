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
            case ClearType.EASY:
                return "EASY CLEAR";
            case ClearType.CLEAR:
                return "CLEAR";
            case ClearType.HARD:
                return "HARD CLEAR";
            case ClearType.FC:
                return "FULL COMBO";
            case ClearType.PA:
                return "PERFECT ATTACK";
            case ClearType.INVALID:
                return "ASSIST CLEAR";
            default:
                return string.Empty;
        }
    }

    internal static string FormatRank(RankType rank)
    {
        return rank == RankType.INVALID ? string.Empty : rank.ToString();
    }
}
