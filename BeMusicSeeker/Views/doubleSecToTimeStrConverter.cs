using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class doubleSecToTimeStrConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double)
        {
            return Binding.DoNothing;
        }
        var timeSpan = TimeSpan.FromSeconds((double)value);
        if (!(timeSpan.TotalHours > 1.0))
        {
            return timeSpan.ToString("mm\\:ss");
        }
        if (!(timeSpan.TotalDays > 1.0))
        {
            return timeSpan.ToString("hh\\:mm\\:ss");
        }
        return timeSpan.ToString("d\\:hh\\:mm\\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
