using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlToTooltipTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Uri url = GridRowResolver.GetUrl(value);
        if (url != null && url.IsAbsoluteUri)
        {
            return url.ToString();
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
