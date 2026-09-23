using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class windowsFormsHostVisibilitiesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (values.Length != 4
                || values[0] is not Visibility overlayVisibility
                || values[1] is not bool isEnabled
                || values[3] is not UIElement host)
            {
                return DependencyProperty.UnsetValue;
            }
            if (overlayVisibility == Visibility.Visible)
            {
                return Visibility.Collapsed;
            }
            if (!isEnabled || values[2] == null)
            {
                return Visibility.Collapsed;
            }
            return host.Visibility;
        }
        catch
        {
            return values is { Length: > 0 } && values[^1] is UIElement host
                ? host.Visibility
                : DependencyProperty.UnsetValue;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
