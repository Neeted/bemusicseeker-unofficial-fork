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

internal class ChartRowsViewToSummaryTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IChartListViewMetadata metadata)
        {
            return FormatSummaryText(metadata.RowCount, metadata.DistinctFolderCount);
        }
        if (value is IEnumerable rows)
        {
            List<object> rowList = [.. rows.Cast<object>().Where(row => row != null)];
            int count = rowList.Count;
            int num = rowList.Select(GetFolderName).Where(folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return FormatSummaryText(count, num);
        }
        return string.Empty;
    }

    internal static string FormatSummaryText(int rowCount, int distinctFolderCount)
    {
        string text = "[" + rowCount + Resources.Num_songs;
        if (distinctFolderCount > 1)
        {
            return text + " / " + distinctFolderCount + Resources.Num_folders + "]";
        }
        return text + "]";
    }

    private static string GetFolderName(object row)
    {
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.Folder;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.Folder;
        }
        if (GridRowResolver.TryGetChartFile(row, out ChartFile chart))
        {
            return chart.Folder;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
