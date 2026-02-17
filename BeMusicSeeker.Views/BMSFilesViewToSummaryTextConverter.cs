using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class BMSFilesViewToSummaryTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is List<BMSFile>)
		{
			int count = ((List<BMSFile>)value).Count;
			int num = ((List<BMSFile>)value).Select((BMSFile f) => f.Folder).Distinct().Count();
			string text = "[" + count + Resources.Num_songs;
			if (num > 1)
			{
				return text + " / " + num + Resources.Num_folders + "]";
			}
			return text + "]";
		}
		return string.Empty;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
