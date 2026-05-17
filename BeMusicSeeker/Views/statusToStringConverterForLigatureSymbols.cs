using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class statusToStringConverterForLigatureSymbols : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is BMSFile.BMSFileStatus bMSFileStatus)
        {
            if (bMSFileStatus == BMSFile.BMSFileStatus.NONE)
            {
                return string.Empty;
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.FORWARD))
            {
                return "rightright";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.BACKWARD))
            {
                return "leftleft";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.PLAY))
            {
                return "volumeup";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.LOADING))
            {
                return "etc";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.PAUSE))
            {
                return "volumeoff";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.SEARCHING))
            {
                return "search";
            }
            if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.SCORE_UNSENT))
            {
                return "refresh";
            }
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
