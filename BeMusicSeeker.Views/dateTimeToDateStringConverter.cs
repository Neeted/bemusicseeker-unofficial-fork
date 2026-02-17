using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class dateTimeToDateStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (value is DateTime dateTime)
        {
            return dateTime.ToShortDateString();
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
