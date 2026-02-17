using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class ranktypeToStringConvberter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is LR2ScoreDB.score.RankType)
		{
			if ((LR2ScoreDB.score.RankType)value != LR2ScoreDB.score.RankType.INVALID)
			{
				return value.ToString();
			}
			return string.Empty;
		}
		return Binding.DoNothing;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
