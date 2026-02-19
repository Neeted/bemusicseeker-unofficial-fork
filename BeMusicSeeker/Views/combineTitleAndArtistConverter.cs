using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class combineTitleAndArtistConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if ((MainWindowViewModel.PanelState)values[6] == MainWindowViewModel.PanelState.MOVIE_PLAYER && !string.IsNullOrWhiteSpace((string)values[3] + (string)values[4] + (string)values[5]))
		{
			string text = (string)values[3] + ((!string.IsNullOrWhiteSpace((string)values[4])) ? (" " + values[4]) : string.Empty);
			string text2 = (string)values[5];
			if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(text2))
			{
				return text + " / " + text2;
			}
			return text + text2;
		}
		string text3 = (string)values[0] + ((!string.IsNullOrWhiteSpace((string)values[1])) ? (" " + values[1]) : string.Empty);
		string text4 = (string)values[2];
		if (!string.IsNullOrWhiteSpace(text3) && !string.IsNullOrWhiteSpace(text4))
		{
			return text3 + " / " + text4;
		}
		return text3 + text4;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
