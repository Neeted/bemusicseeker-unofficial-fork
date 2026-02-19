using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class statusToTooltipStringConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is BMSFile.BMSFileStatus bMSFileStatus)
		{
			if (bMSFileStatus == BMSFile.BMSFileStatus.NONE)
			{
				return null;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.PLAY))
			{
				return Resources.Tooltip_play;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.LOADING))
			{
				return Resources.Tooltip_loading;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.PAUSE))
			{
				return Resources.Tooltip_pause;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.FORWARD))
			{
				return Resources.Tooltip_fast_forward;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.BACKWARD))
			{
				return Resources.Tooltip_rewind;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.SEARCHING))
			{
				return Resources.Tooltip_searching;
			}
			if (bMSFileStatus.HasFlag(BMSFile.BMSFileStatus.SCORE_UNSENT))
			{
				return Resources.Tooltip_score_unsent;
			}
		}
		return Binding.DoNothing;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
