using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class playlistSummaryOwnedFilterConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is MainWindowViewModel.PlaylistSummaryOwnedFilterType playlistSummaryOwnedFilterType))
		{
			return false;
		}
		if (parameter == null)
		{
			return false;
		}
		MainWindowViewModel.PlaylistSummaryOwnedFilterType playlistSummaryOwnedFilterType2 = (MainWindowViewModel.PlaylistSummaryOwnedFilterType)Enum.Parse(typeof(MainWindowViewModel.PlaylistSummaryOwnedFilterType), parameter.ToString());
		return playlistSummaryOwnedFilterType == playlistSummaryOwnedFilterType2;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is bool flag) || !flag || parameter == null)
		{
			return Binding.DoNothing;
		}
		return (MainWindowViewModel.PlaylistSummaryOwnedFilterType)Enum.Parse(typeof(MainWindowViewModel.PlaylistSummaryOwnedFilterType), parameter.ToString());
	}
}
