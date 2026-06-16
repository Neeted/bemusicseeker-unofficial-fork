using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class customfolderOutputCheckBoxConverter : IValueConverter
{
    private LR2SongDBExtended.playlist.CustomFolderType flags;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var customFolderType = (LR2SongDBExtended.playlist.CustomFolderType)value;
        string value2 = parameter as string;
        var customFolderType2 = (LR2SongDBExtended.playlist.CustomFolderType)Enum.Parse(typeof(LR2SongDBExtended.playlist.CustomFolderType), value2);
        flags = LR2SongDBExtended.playlist.NormalizeCustomFolderOutputMask(customFolderType);
        return LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(customFolderType, customFolderType2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool num = (bool)value;
        string value2 = parameter as string;
        var customFolderType = (LR2SongDBExtended.playlist.CustomFolderType)Enum.Parse(typeof(LR2SongDBExtended.playlist.CustomFolderType), value2);
        if (num)
        {
            flags &= ~customFolderType;
        }
        else
        {
            flags |= customFolderType;
        }
        return flags;
    }
}
