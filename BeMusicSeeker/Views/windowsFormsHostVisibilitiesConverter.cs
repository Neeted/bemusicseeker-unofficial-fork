using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class windowsFormsHostVisibilitiesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            int contentStateIndex = values.Length - 3;
            if (values.Take(contentStateIndex).Cast<Visibility>().Any(v => v == Visibility.Visible))
            {
                return Visibility.Collapsed;
            }
            if (!(bool)values[contentStateIndex])
            {
                return Visibility.Collapsed;
            }
            if (values[contentStateIndex + 1] == null)
            {
                return Visibility.Collapsed;
            }
            return ((UIElement)values[values.Length - 1]).Visibility;
        }
        catch
        {
            return ((UIElement)values[values.Length - 1]).Visibility;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
