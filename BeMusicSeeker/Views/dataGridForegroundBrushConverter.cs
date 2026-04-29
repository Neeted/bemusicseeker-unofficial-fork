using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class dataGridForegroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mode = parameter as string;
        switch (mode)
        {
            case "difficulty":
                return CustomTableScoreBrushProvider.ConvertDifficulty(value as string);
            case "judge":
                return CustomTableScoreBrushProvider.ConvertJudge(value as string);
            case "clear":
                return value is ClearType clear ? CustomTableScoreBrushProvider.ConvertClear(clear) : Binding.DoNothing;
            case "rank":
                return value is RankType rank ? CustomTableScoreBrushProvider.ConvertRank(rank) : Binding.DoNothing;
            default:
                return Binding.DoNothing;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
