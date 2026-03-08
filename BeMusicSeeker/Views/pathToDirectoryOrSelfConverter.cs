using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal sealed class pathToDirectoryOrSelfConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string path = value as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            return value;
        }
        string directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? path : directory;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value;
    }
}
