using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class backupTargetConverter : IValueConverter
{
    private Backup.Target flags;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var target = (Backup.Target)value;
        string value2 = parameter as string;
        var target2 = (Backup.Target)Enum.Parse(typeof(Backup.Target), value2);
        flags = target;
        return (target & target2) == target2;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool num = (bool)value;
        string value2 = parameter as string;
        var target = (Backup.Target)Enum.Parse(typeof(Backup.Target), value2);
        if (num)
        {
            flags |= target;
        }
        else
        {
            flags &= ~target;
        }
        return flags;
    }
}
