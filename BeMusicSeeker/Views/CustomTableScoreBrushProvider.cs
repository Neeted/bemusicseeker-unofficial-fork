using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal static class CustomTableScoreBrushProvider
{
    internal static readonly Brush DefaultForeground = CreateBrush(Colors.Black);
    internal static readonly Brush SelectedForeground = CreateBrush(Colors.White);
    internal static readonly Brush GrayBrush = CreateBrush(Colors.Gray);
    internal static readonly Brush BlackBrush = CreateBrush(Colors.Black);
    internal static readonly Brush DarkBrownBrush = CreateBrush(Color.FromRgb(0x8B, 0x45, 0x13));
    internal static readonly Brush GreenBrush = CreateBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    internal static readonly Brush BlueBrush = CreateBrush(Color.FromRgb(0x00, 0x87, 0xB0));
    internal static readonly Brush RedBrush = CreateBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
    internal static readonly Brush DeepSkyBlueBrush = CreateBrush(Colors.DeepSkyBlue);
    internal static readonly Brush HotPinkBrush = CreateBrush(Colors.HotPink);
    internal static readonly Brush PurpleBrush = CreateBrush(Color.FromRgb(0x7B, 0x1F, 0xA2));
    internal static readonly Brush LightPurpleBrush = CreateBrush(Color.FromRgb(0xBA, 0x68, 0xC8));
    internal static readonly Brush YellowBrush = CreateBrush(Color.FromRgb(0xF9, 0xA8, 0x25));
    internal static readonly Brush YellowOrangeBrush = CreateBrush(Color.FromRgb(0xD9, 0x82, 0x00));
    internal static readonly Brush VeryHardBrush = CreateBrush(Color.FromRgb(0x8B, 0x00, 0x00));
    internal static readonly Brush VeryEasyBrush = CreateBrush(Color.FromRgb(0x66, 0xA0, 0x00));

    internal static object ConvertDifficulty(string key)
    {
        switch (key)
        {
            case "Beginner":
                return GreenBrush;
            case "Normal":
                return BlueBrush;
            case "Hyper":
                return YellowOrangeBrush;
            case "Another":
                return RedBrush;
            case "Insane":
                return PurpleBrush;
            default:
                return Binding.DoNothing;
        }
    }

    internal static object ConvertJudge(string key)
    {
        switch (key)
        {
            case "VeryHard":
                return VeryHardBrush;
            case "Hard":
                return RedBrush;
            case "Normal":
                return BlueBrush;
            case "Easy":
                return GreenBrush;
            case "VeryEasy":
                return VeryEasyBrush;
            default:
                return Binding.DoNothing;
        }
    }

    internal static object ConvertClear(ClearType clear)
    {
        switch (clear)
        {
            case ClearType.NO_SONG:
                return GrayBrush;
            case ClearType.NO_PLAY:
                return BlackBrush;
            case ClearType.FAILED:
                return DarkBrownBrush;
            case ClearType.EASY:
                return GreenBrush;
            case ClearType.INVALID:
                return PurpleBrush;
            case ClearType.L_ASSIST:
                return LightPurpleBrush;
            case ClearType.CLEAR:
                return BlueBrush;
            case ClearType.HARD:
                return RedBrush;
            case ClearType.EX_HARD:
                return YellowBrush;
            case ClearType.FC:
                return DeepSkyBlueBrush;
            case ClearType.PA:
                return HotPinkBrush;
            case ClearType.MAX:
                return YellowOrangeBrush;
            default:
                return Binding.DoNothing;
        }
    }

    internal static object ConvertRank(RankType rank)
    {
        switch (rank)
        {
            case RankType.F:
            case RankType.E:
            case RankType.D:
            case RankType.C:
            case RankType.B:
            case RankType.AA:
                return GrayBrush;
            case RankType.A:
                return GreenBrush;
            case RankType.AAA:
            case RankType.MAX:
                return YellowOrangeBrush;
            default:
                return Binding.DoNothing;
        }
    }

    internal static Brush ResolveBrush(object candidate, Brush fallback)
    {
        return candidate is Brush brush ? brush : fallback;
    }

    private static Brush CreateBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
