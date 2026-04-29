using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class dataGridForegroundBrushConverter : IValueConverter
{
    private static readonly Brush GrayBrush = CreateBrush(Colors.Gray);
    private static readonly Brush BlackBrush = CreateBrush(Colors.Black);
    private static readonly Brush DarkBrownBrush = CreateBrush(Color.FromRgb(0x8B, 0x45, 0x13));
    private static readonly Brush GreenBrush = CreateBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush BlueBrush = CreateBrush(Color.FromRgb(0x00, 0x87, 0xB0));
    private static readonly Brush RedBrush = CreateBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
    private static readonly Brush DeepSkyBlueBrush = CreateBrush(Colors.DeepSkyBlue);
    private static readonly Brush HotPinkBrush = CreateBrush(Colors.HotPink);
    private static readonly Brush PurpleBrush = CreateBrush(Color.FromRgb(0x7B, 0x1F, 0xA2));
    private static readonly Brush YellowOrangeBrush = CreateBrush(Color.FromRgb(0xD9, 0x82, 0x00));
    private static readonly Brush VeryHardBrush = CreateBrush(Color.FromRgb(0x8B, 0x00, 0x00));
    private static readonly Brush VeryEasyBrush = CreateBrush(Color.FromRgb(0x66, 0xA0, 0x00));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mode = parameter as string;
        switch (mode)
        {
            case "difficulty":
                return ConvertDifficulty(value as string);
            case "judge":
                return ConvertJudge(value as string);
            case "clear":
                return value is ClearType clear ? ConvertClear(clear) : Binding.DoNothing;
            case "rank":
                return value is RankType rank ? ConvertRank(rank) : Binding.DoNothing;
            default:
                return Binding.DoNothing;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static object ConvertDifficulty(string key)
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

    private static object ConvertJudge(string key)
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

    private static object ConvertClear(ClearType clear)
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
            case ClearType.CLEAR:
                return BlueBrush;
            case ClearType.HARD:
                return RedBrush;
            case ClearType.FC:
                return DeepSkyBlueBrush;
            case ClearType.PA:
                return HotPinkBrush;
            default:
                return Binding.DoNothing;
        }
    }

    private static object ConvertRank(RankType rank)
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

    private static Brush CreateBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
