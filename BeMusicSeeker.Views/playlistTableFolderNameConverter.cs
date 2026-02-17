using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class playlistTableFolderNameConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!string.IsNullOrWhiteSpace(value.ToString()))
		{
			return value.ToString();
		}
		return "(" + Resources.No_folder_name + ")";
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
