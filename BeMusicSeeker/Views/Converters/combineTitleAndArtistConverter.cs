using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class combineTitleAndArtistConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string title = GetString(values, 0);
        string subtitle = GetString(values, 1);
        string artist = GetString(values, 2);
        string titleWithSubtitle = title + ((!string.IsNullOrWhiteSpace(subtitle)) ? (" " + subtitle) : string.Empty);
        if (!string.IsNullOrWhiteSpace(titleWithSubtitle) && !string.IsNullOrWhiteSpace(artist))
        {
            return titleWithSubtitle + " / " + artist;
        }
        return titleWithSubtitle + artist;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static string GetString(object[] values, int index)
    {
        return values != null && index >= 0 && index < values.Length ? values[index] as string ?? string.Empty : string.Empty;
    }
}
