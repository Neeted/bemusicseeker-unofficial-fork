using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal sealed class entryUnitTypeIsFileConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        return string.Equals(text, Resources.File, StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
