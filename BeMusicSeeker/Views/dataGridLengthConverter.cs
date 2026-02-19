using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class dataGridLengthConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is int))
		{
			return Binding.DoNothing;
		}
		return new DataGridLength((int)value);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is DataGridLength dataGridLength))
		{
			return Binding.DoNothing;
		}
		return (int)dataGridLength.Value;
	}
}
