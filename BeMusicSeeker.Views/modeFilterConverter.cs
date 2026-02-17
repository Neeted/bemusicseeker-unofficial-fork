using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class modeFilterConverter : IValueConverter
{
	private MainWindowViewModel.ModeFilterType flags;

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		MainWindowViewModel.ModeFilterType modeFilterType = (MainWindowViewModel.ModeFilterType)value;
		string value2 = parameter as string;
		MainWindowViewModel.ModeFilterType modeFilterType2 = (MainWindowViewModel.ModeFilterType)Enum.Parse(typeof(MainWindowViewModel.ModeFilterType), value2);
		flags = modeFilterType;
		return (modeFilterType & modeFilterType2) == modeFilterType2;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool num = (bool)value;
		string value2 = parameter as string;
		MainWindowViewModel.ModeFilterType modeFilterType = (MainWindowViewModel.ModeFilterType)Enum.Parse(typeof(MainWindowViewModel.ModeFilterType), value2);
		if (num)
		{
			flags |= modeFilterType;
		}
		else
		{
			flags &= ~modeFilterType;
		}
		return flags;
	}
}
