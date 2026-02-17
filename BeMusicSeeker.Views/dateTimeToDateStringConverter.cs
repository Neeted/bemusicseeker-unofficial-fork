using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class dateTimeToDateStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return string.Empty;
        }
        // 元のデコンパイルコードのロジックを維持:
        // value が DateTime? (Nullable<DateTime>) として扱える場合、
        // HasValue なら日付文字列を返し、そうでなければ空文字列を返す。
        if (value is DateTime || value is DateTime?)
        {
            DateTime? dateTime = value as DateTime?;
            if (dateTime.HasValue)
            {
                return dateTime.Value.ToShortDateString();
            }
            return string.Empty;
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
