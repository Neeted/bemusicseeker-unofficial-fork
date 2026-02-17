using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class panelStateToTooltipConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is MainWindowViewModel.PanelState && value is MainWindowViewModel.PanelState)
		{
			if (((MainWindowViewModel.PanelState)value).HasFlag(MainWindowViewModel.PanelState.BMS_PLAYER))
			{
				return Resources.Tooltip_view_mode + ": " + Resources.Tooltip_bms_player;
			}
			if (((MainWindowViewModel.PanelState)value).HasFlag(MainWindowViewModel.PanelState.MOVIE_PLAYER))
			{
				return Resources.Tooltip_view_mode + ": " + Resources.Tooltip_movie_preview;
			}
			return Resources.Tooltip_view_mode + ": " + Resources.Tooltip_image_file;
		}
		return Binding.DoNothing;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
