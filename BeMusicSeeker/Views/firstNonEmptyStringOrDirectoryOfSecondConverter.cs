using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal sealed class firstNonEmptyStringOrDirectoryOfSecondConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string first = (values != null && values.Length > 0) ? values[0] as string : null;
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }
        string second = (values != null && values.Length > 1) ? values[1] as string : null;
        if (string.IsNullOrWhiteSpace(second))
        {
            return string.Empty;
        }
        return Path.GetDirectoryName(second) ?? string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
