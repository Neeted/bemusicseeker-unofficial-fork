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
        if (parameter == null)
        {
            return false;
        }

        PlaylistOwnedFilter currentFilter;
        if (value is PlaylistOwnedFilter ownerFilter)
        {
            currentFilter = ownerFilter;
        }
        else if (value is MainWindowViewModel.PlaylistSummaryOwnedFilterType legacyFilter)
        {
            currentFilter = (PlaylistOwnedFilter)(int)legacyFilter;
        }
        else
        {
            return false;
        }

        var requestedFilter = (PlaylistOwnedFilter)Enum.Parse(typeof(PlaylistOwnedFilter), parameter.ToString());
        return currentFilter == requestedFilter;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool flag || !flag || parameter == null)
        {
            return Binding.DoNothing;
        }

        var requestedFilter = (PlaylistOwnedFilter)Enum.Parse(typeof(PlaylistOwnedFilter), parameter.ToString());
        if (targetType == typeof(MainWindowViewModel.PlaylistSummaryOwnedFilterType))
        {
            return (MainWindowViewModel.PlaylistSummaryOwnedFilterType)(int)requestedFilter;
        }

        return requestedFilter;
    }
}
