using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

public sealed class MenuMaxHeightConverter : IValueConverter
{
    private const double MinimumMenuHeight = 160d;
    private const double WorkAreaRatio = 0.75d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!TryGetHeight(value, out double height))
        {
            return MinimumMenuHeight;
        }

        return Math.Max(MinimumMenuHeight, Math.Floor(height * WorkAreaRatio));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static bool TryGetHeight(object value, out double height)
    {
        height = 0d;
        if (value is double doubleValue)
        {
            height = doubleValue;
        }
        else if (value is int intValue)
        {
            height = intValue;
        }
        else if (value is float floatValue)
        {
            height = floatValue;
        }

        return !double.IsNaN(height) && !double.IsInfinity(height) && height > 0d;
    }
}
