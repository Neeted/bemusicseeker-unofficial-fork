using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class BMSFilesViewToSummaryTextConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is IChartListViewMetadata metadata)
		{
			string text = "[" + metadata.RowCount + Resources.Num_songs;
			if (metadata.DistinctFolderCount > 1)
			{
				return text + " / " + metadata.DistinctFolderCount + Resources.Num_folders + "]";
			}
			return text + "]";
		}
		if (value is IEnumerable rows)
		{
			List<object> rowList = rows.Cast<object>().Where((object row) => row != null).ToList();
			int count = rowList.Count;
			int num = rowList.Select(GetFolderName).Where((string folder) => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
			string text = "[" + count + Resources.Num_songs;
			if (num > 1)
			{
				return text + " / " + num + Resources.Num_folders + "]";
			}
			return text + "]";
		}
		return string.Empty;
	}

	private static string GetFolderName(object row)
	{
		if (row is BMSFile bMSFile)
		{
			return bMSFile.Folder;
		}
		if (row is PlaylistDetailRow playlistDetailRow)
		{
			return playlistDetailRow.Folder;
		}
		if (row is LibraryChartRow libraryChartRow)
		{
			return libraryChartRow.Folder;
		}
		return string.Empty;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
