using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class cleartypeToStringConvberter : IValueConverter
{
    private static readonly Dictionary<ClearType, string> table = new Dictionary<ClearType, string>
    {
        {
            ClearType.NO_SONG,
            "NO SONG"
        },
        {
            ClearType.NO_PLAY,
            "NO PLAY"
        },
        {
            ClearType.FAILED,
            "FAILED"
        },
        {
            ClearType.EASY,
            "EASY CLEAR"
        },
        {
            ClearType.CLEAR,
            "CLEAR"
        },
        {
            ClearType.HARD,
            "HARD CLEAR"
        },
        {
            ClearType.FC,
            "FULL COMBO"
        },
        {
            ClearType.PA,
            "PERFECT ATTACK"
        },
        {
            ClearType.INVALID,
            "ASSIST CLEAR"
        }
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ClearType)
        {
            return table[(ClearType)value];
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
