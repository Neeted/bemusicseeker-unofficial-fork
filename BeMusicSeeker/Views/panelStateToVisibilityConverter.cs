using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal class panelStateToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
    {
        return ((Visibility)value[0] != Visibility.Collapsed) ? ((!((MainWindowViewModel.PanelState)value[1]).HasFlag(MainWindowViewModel.PanelState.TITLE_SMALL)) ? Visibility.Collapsed : Visibility.Visible) : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
