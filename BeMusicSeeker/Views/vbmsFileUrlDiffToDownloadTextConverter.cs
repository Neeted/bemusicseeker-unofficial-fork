using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlDiffToDownloadTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Uri urlDiff = GridRowResolver.GetUrlDiff(value);
        if (urlDiff != null && urlDiff.IsAbsoluteUri)
        {
            return "download";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
