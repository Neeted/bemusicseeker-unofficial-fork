using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class currentTimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length != 2)
        {
            return string.Empty;
        }
        try
        {
            TimeSpan timeSpan = (TimeSpan)values[1];
            TimeSpan timeSpan2 = (TimeSpan)values[0];
            string obj = ((!(timeSpan2.TotalHours > 1.0)) ? timeSpan.ToString("mm\\:ss") : ((timeSpan2.TotalDays > 1.0) ? timeSpan.ToString("d\\:hh\\:mm\\:ss") : timeSpan.ToString("hh\\:mm\\:ss")));
            string text = ((!(timeSpan2.TotalHours > 1.0)) ? timeSpan2.ToString("mm\\:ss") : ((timeSpan2.TotalDays > 1.0) ? timeSpan2.ToString("d\\:hh\\:mm\\:ss") : timeSpan2.ToString("hh\\:mm\\:ss")));
            return obj + " / " + text;
        }
        catch
        {
            return string.Empty;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
