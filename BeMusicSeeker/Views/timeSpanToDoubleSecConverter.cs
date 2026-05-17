using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class timeSpanToDoubleSecConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan timeSpan)
        {
            return Binding.DoNothing;
        }
        return timeSpan.TotalSeconds;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double)
        {
            return Binding.DoNothing;
        }
        return TimeSpan.FromSeconds((double)value);
    }
}
