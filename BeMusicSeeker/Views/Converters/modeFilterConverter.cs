using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class modeFilterConverter : IValueConverter
{
    private ChartModeFilter flags;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var modeFilterType = (ChartModeFilter)value;
        string value2 = parameter as string;
        var modeFilterType2 = (ChartModeFilter)Enum.Parse(typeof(ChartModeFilter), value2);
        flags = modeFilterType;
        return (modeFilterType & modeFilterType2) == modeFilterType2;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool num = (bool)value;
        string value2 = parameter as string;
        var modeFilterType = (ChartModeFilter)Enum.Parse(typeof(ChartModeFilter), value2);
        if (num)
        {
            flags |= modeFilterType;
        }
        else
        {
            flags &= ~modeFilterType;
        }
        return flags;
    }
}
