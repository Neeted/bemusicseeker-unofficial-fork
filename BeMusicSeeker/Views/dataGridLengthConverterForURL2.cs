using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class dataGridLengthConverterForURL2 : IValueConverter
{
	public static bool IsEditingMode;

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is int) || IsEditingMode)
		{
			return Binding.DoNothing;
		}
		return new DataGridLength((int)value);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is DataGridLength) || IsEditingMode)
		{
			return Binding.DoNothing;
		}
		return (int)((DataGridLength)value).Value;
	}
}
