using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlDiffToTooltipTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is VirtualBMSFile) || ((VirtualBMSFile)value).Url_diff == null || !((VirtualBMSFile)value).Url_diff.IsAbsoluteUri)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(((VirtualBMSFile)value).name_diff))
		{
			if (!((VirtualBMSFile)value).name_diff.ToString().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			{
				return ((VirtualBMSFile)value).name_diff + Environment.NewLine + ((VirtualBMSFile)value).Url_diff.ToString();
			}
			return ((VirtualBMSFile)value).Url_diff.ToString();
		}
		return ((VirtualBMSFile)value).Url_diff.ToString();
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
