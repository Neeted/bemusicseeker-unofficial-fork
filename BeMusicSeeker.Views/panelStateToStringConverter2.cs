using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class panelStateToStringConverter2 : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is MainWindowViewModel.PanelState)
		{
			if (((MainWindowViewModel.PanelState)value).HasFlag(MainWindowViewModel.PanelState.TITLE_SMALL))
			{
				return "full";
			}
			return "small";
		}
		return Binding.DoNothing;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
