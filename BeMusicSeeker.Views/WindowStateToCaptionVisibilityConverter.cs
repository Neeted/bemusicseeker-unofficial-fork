using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class WindowStateToCaptionVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if ((string)parameter == "Maximized")
		{
			return ((WindowState)value == WindowState.Maximized) ? Visibility.Collapsed : Visibility.Visible;
		}
		if ((string)parameter == "Normal")
		{
			return ((WindowState)value == WindowState.Normal) ? Visibility.Collapsed : Visibility.Visible;
		}
		return Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return !(bool)value;
	}
}
