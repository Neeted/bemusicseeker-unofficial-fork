using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

internal class vbmsFileToIsReadOnlyConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return !(value is VirtualBMSFile) || ((VirtualBMSFile)value).ToBMSTableEntry().parent == null || ((VirtualBMSFile)value).ToBMSTableEntry().parent.is_external_sync;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
