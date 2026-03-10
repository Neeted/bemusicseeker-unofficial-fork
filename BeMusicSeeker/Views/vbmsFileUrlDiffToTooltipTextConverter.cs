using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class vbmsFileUrlDiffToTooltipTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		Uri urlDiff = GridRowResolver.GetUrlDiff(value);
		if (urlDiff == null || !urlDiff.IsAbsoluteUri)
		{
			return null;
		}
		string nameDiff = GridRowResolver.GetNameDiff(value);
		if (!string.IsNullOrWhiteSpace(nameDiff))
		{
			if (!nameDiff.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			{
				return nameDiff + Environment.NewLine + urlDiff;
			}
			return urlDiff.ToString();
		}
		return urlDiff.ToString();
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
