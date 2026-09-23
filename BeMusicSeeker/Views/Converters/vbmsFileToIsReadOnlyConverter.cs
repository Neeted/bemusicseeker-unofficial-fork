using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class vbmsFileToIsReadOnlyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(value);
        return entry == null || entry.parent == null || entry.parent.is_external_sync;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
