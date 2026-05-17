using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal static class CustomTableScoreBrushProvider
{
    internal static Brush DefaultForeground => CustomTablePalette.Current.DefaultForeground;
    internal static Brush SelectedForeground => CustomTablePalette.Current.SelectedForeground;
    internal static Brush GrayBrush => CustomTablePalette.Current.SubtleForeground;
    internal static Brush BlackBrush => CustomTablePalette.Current.DefaultForeground;
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
        return key switch
        {
            "Beginner" => GreenBrush,
            "Normal" => BlueBrush,
            "Hyper" => YellowOrangeBrush,
            "Another" => RedBrush,
            "Insane" => PurpleBrush,
            _ => Binding.DoNothing,
        };
    }

    internal static object ConvertJudge(string key)
    {
        return key switch
        {
            "VeryHard" => VeryHardBrush,
            "Hard" => RedBrush,
            "Normal" => BlueBrush,
            "Easy" => GreenBrush,
            "VeryEasy" => VeryEasyBrush,
            _ => Binding.DoNothing,
        };
    }

    internal static object ConvertClear(ClearType clear)
    {
        return clear switch
        {
            ClearType.NO_SONG => GrayBrush,
            ClearType.NO_PLAY => BlackBrush,
            ClearType.FAILED => DarkBrownBrush,
            ClearType.EASY => GreenBrush,
            ClearType.INVALID => PurpleBrush,
            ClearType.L_ASSIST => LightPurpleBrush,
            ClearType.CLEAR => BlueBrush,
            ClearType.HARD => RedBrush,
            ClearType.EX_HARD => YellowBrush,
            ClearType.FC => DeepSkyBlueBrush,
            ClearType.PA => HotPinkBrush,
            ClearType.MAX => YellowOrangeBrush,
            _ => Binding.DoNothing,
        };
    }

    internal static object ConvertRank(RankType rank)
    {
        return rank switch
        {
            RankType.F or RankType.E or RankType.D or RankType.C or RankType.B or RankType.AA => GrayBrush,
            RankType.A => GreenBrush,
            RankType.AAA or RankType.MAX => YellowOrangeBrush,
            _ => Binding.DoNothing,
        };
    }

    internal static Brush ResolveBrush(object candidate, Brush fallback)
    {
        return candidate is Brush brush ? brush : fallback;
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
