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
            if (((BMSFile.BMSFileStatus)value).HasFlag(BMSFile.BMSFileStatus.PLAY))
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
