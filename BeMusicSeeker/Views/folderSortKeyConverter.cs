using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class folderSortKeyConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = ((LR2SongDBExtended.playlist.CustomFolderSortType)value).ToDisplayName();
		switch (text)
		{
		case "(無し)":
			text = Resources.None;
			break;
		case "レベル":
			text = Resources.Level;
			break;
		case "タイトル":
			text = Resources.Title;
			break;
		case "アーティスト":
			text = Resources.Artist;
			break;
		case "スコア":
			text = Resources.Score;
			break;
		case "ミスカウント":
			text = Resources.MissCount;
			break;
		case "プレイカウント":
			text = Resources.PlayCount;
			break;
		case "追加日時":
			text = Resources.AddDate;
			break;
		}
		return text;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = value as string;
		if (text == Resources.None)
		{
			text = "(無し)";
		}
		else if (text == Resources.Level)
		{
			text = "レベル";
		}
		else if (text == Resources.Title)
		{
			text = "タイトル";
		}
		else if (text == Resources.Artist)
		{
			text = "アーティスト";
		}
		else if (text == Resources.Score)
		{
			text = "スコア";
		}
		else if (text == Resources.MissCount)
		{
			text = "ミスカウント";
		}
		else if (text == Resources.PlayCount)
		{
			text = "プレイカウント";
		}
		else if (text == Resources.AddDate)
		{
			text = "追加日時";
		}
		return CustomFolderSortTypeExt.FromDisplayName(text);
	}
}
