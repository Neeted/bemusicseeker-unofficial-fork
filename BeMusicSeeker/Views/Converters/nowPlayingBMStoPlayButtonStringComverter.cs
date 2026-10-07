using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class nowPlayingBMStoPlayButtonStringComverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (((ChartFileStatus)System.Convert.ToInt32(value, culture)).HasFlag(ChartFileStatus.PLAY))
            {
                return "pause";
            }
        }
        catch
        {
            return "play";
        }
        return "play";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
