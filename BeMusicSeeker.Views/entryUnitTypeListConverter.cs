using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class entryUnitTypeListConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is IEnumerable<string> source))
		{
			return value;
		}
		return source.Select(delegate(string v)
		{
			if (v == "ファイル")
			{
				return Resources.File;
			}
			return (v == "フォルダ") ? Resources.Folder : string.Empty;
		});
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return value;
	}
}
