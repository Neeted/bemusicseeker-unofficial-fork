using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlToDownloadTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is VirtualBMSFile && !(((VirtualBMSFile)value).Url == null) && ((VirtualBMSFile)value).Url.IsAbsoluteUri)
		{
			return "download";
		}
		return string.Empty;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
