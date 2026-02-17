using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Views;

internal class cleartypeToStringConvberter : IValueConverter
{
	private static readonly Dictionary<LR2ScoreDB.score.ClearType, string> table = new Dictionary<LR2ScoreDB.score.ClearType, string>
	{
		{
			LR2ScoreDB.score.ClearType.NO_SONG,
			"NO SONG"
		},
		{
			LR2ScoreDB.score.ClearType.NO_PLAY,
			"NO PLAY"
		},
		{
			LR2ScoreDB.score.ClearType.FAILED,
			"FAILED"
		},
		{
			LR2ScoreDB.score.ClearType.EASY,
			"EASY CLEAR"
		},
		{
			LR2ScoreDB.score.ClearType.CLEAR,
			"CLEAR"
		},
		{
			LR2ScoreDB.score.ClearType.HARD,
			"HARD CLEAR"
		},
		{
			LR2ScoreDB.score.ClearType.FC,
			"FULL COMBO"
		},
		{
			LR2ScoreDB.score.ClearType.PA,
			"PERFECT ATTACK"
		},
		{
			LR2ScoreDB.score.ClearType.INVALID,
			"ASSIST CLEAR"
		}
	};

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is LR2ScoreDB.score.ClearType)
		{
			return table[(LR2ScoreDB.score.ClearType)value];
		}
		return Binding.DoNothing;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
