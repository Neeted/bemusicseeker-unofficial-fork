using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class addStringToEndConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = string.Empty;
		string result = string.Empty;
		if (parameter != null && parameter is string)
		{
			string[] array = ((string)parameter).Split(new char[1] { '|' }, 2, StringSplitOptions.None);
			text = array[0];
			result = array[1];
		}
		if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
		{
			return result;
		}
		return value.ToString() + text;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
