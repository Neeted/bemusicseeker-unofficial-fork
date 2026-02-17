using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class windowsFormsHostIsEnabledConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if (values.Length != 4)
		{
			return false;
		}
		bool flag = (bool)values[0];
		bool flag2 = (bool)values[1];
		bool flag3 = (bool)values[2];
		bool flag4 = (bool)values[3];
		if (flag && flag2)
		{
			return false;
		}
		if (!flag && flag2)
		{
			return true;
		}
		if (flag3)
		{
			return false;
		}
		if (flag4)
		{
			return true;
		}
		return false;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
