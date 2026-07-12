using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class panelStateToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PlayerPanelState)
        {
            if (((PlayerPanelState)value).HasFlag(PlayerPanelState.BMS_PLAYER))
            {
                return "music";
            }
            if (((PlayerPanelState)value).HasFlag(PlayerPanelState.MOVIE_PLAYER))
            {
                return "video";
            }
            return "image";
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
