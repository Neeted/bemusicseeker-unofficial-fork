using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class entryUnitTypeConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = ((LR2SongDBExtended.playlist.EntryUnitType)value).ToDisplayName();
		if (!(text == "ファイル"))
		{
			if (text == "フォルダ")
			{
				text = Resources.Folder;
			}
		}
		else
		{
			text = Resources.File;
		}
		return text;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = value as string;
		if (text == Resources.File)
		{
			text = "ファイル";
		}
		else if (text == Resources.Folder)
		{
			text = "フォルダ";
		}
		return EntryUnitTypeExt.FromDisplayName(text);
	}
}
