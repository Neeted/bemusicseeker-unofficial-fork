using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class folderSortKeyListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!(value is IEnumerable<string> source))
        {
            return value;
        }
        return source.Select((string v) => v switch
        {
            "(無し)" => Resources.None,
            "レベル" => Resources.Level,
            "タイトル" => Resources.Title,
            "アーティスト" => Resources.Artist,
            "スコア" => Resources.Score,
            "ミスカウント" => Resources.MissCount,
            "プレイカウント" => Resources.PlayCount,
            "追加日時" => Resources.AddDate,
            _ => string.Empty,
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value;
    }
}
