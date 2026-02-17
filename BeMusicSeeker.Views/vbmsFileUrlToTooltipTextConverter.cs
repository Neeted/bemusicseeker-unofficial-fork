using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlToTooltipTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is VirtualBMSFile && !(((VirtualBMSFile)value).Url == null) && ((VirtualBMSFile)value).Url.IsAbsoluteUri)
		{
			return ((VirtualBMSFile)value).Url.ToString();
		}
		return null;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
