using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class pathAbbreviationConvberter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string result = string.Empty;
        if (value != null && value is string)
        {
            string[] array = ((string)value).Split(new char[1] { '\\' }, StringSplitOptions.None);
            if (array.Length > 1)
            {
                result = ((array.Length <= 3) ? ((string)value) : string.Join("\\", array[0], "...", array[array.Length - 2], array[array.Length - 1]));
            }
        }
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
