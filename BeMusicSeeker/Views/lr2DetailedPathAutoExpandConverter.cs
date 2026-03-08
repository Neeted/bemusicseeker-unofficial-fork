using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal sealed class lr2DetailedPathAutoExpandConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string lr2RootPath = (values != null && values.Length > 0) ? values[0] as string : null;
        string lr2SongDbPath = (values != null && values.Length > 1) ? values[1] as string : null;
        string lr2ConfigXmlPath = (values != null && values.Length > 2) ? values[2] as string : null;
        return string.IsNullOrWhiteSpace(lr2RootPath)
            && !string.IsNullOrWhiteSpace(lr2SongDbPath)
            && !string.IsNullOrWhiteSpace(lr2ConfigXmlPath);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
