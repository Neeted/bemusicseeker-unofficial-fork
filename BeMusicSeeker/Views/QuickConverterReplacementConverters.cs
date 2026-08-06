using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal sealed class visibilityToIsCheckedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility
            ? visibility == Visibility.Visible
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool isChecked
            ? isChecked ? Visibility.Visible : Visibility.Hidden
            : Binding.DoNothing;
    }
}

internal sealed class booleanToVisibilityCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag
            ? flag ? Visibility.Visible : Visibility.Collapsed
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class notBooleanToVisibilityCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag
            ? flag ? Visibility.Collapsed : Visibility.Visible
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class stringToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text
            ? !string.IsNullOrWhiteSpace(text)
            : value is null ? false : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class stringToVisibilityHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is null
            ? Visibility.Hidden
            : value is string text
                ? string.IsNullOrWhiteSpace(text) ? Visibility.Hidden : Visibility.Visible
                : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class stringToVisibilityCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is null
            ? Visibility.Collapsed
            : value is string text
                ? string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible
                : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class fileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string path
            ? Path.GetFileName(path) ?? string.Empty
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class positiveIntToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            int count => count > 0,
            uint count => count > 0,
            _ => DependencyProperty.UnsetValue
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class mainWindowMinHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3 || values[0] is not double panelHeight || values[1] is not Visibility statusVisibility || values[2] is not double statusHeight)
        {
            return DependencyProperty.UnsetValue;
        }

        return statusVisibility == Visibility.Visible ? panelHeight + statusHeight : panelHeight;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class mainWindowMinWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not double treeWidth || values[1] is not double tableWidth)
        {
            return DependencyProperty.UnsetValue;
        }

        return 50d + treeWidth + tableWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class playerTitleVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not Visibility bmsVisibility || values[1] is not Visibility movieVisibility)
        {
            return DependencyProperty.UnsetValue;
        }

        return bmsVisibility == Visibility.Visible || movieVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class positiveTimeSpanToVisibilityHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TimeSpan duration
            ? duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class mainSummaryVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not bool playlistSummaryMode || values[1] is not bool playHistoryActive)
        {
            return DependencyProperty.UnsetValue;
        }

        return !playlistSummaryMode && !playHistoryActive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class folderPathWhenColumnsHiddenVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not bool playlistSummaryMode || values[1] is not Visibility columnsVisibility)
        {
            return DependencyProperty.UnsetValue;
        }

        return !playlistSummaryMode && columnsVisibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class folderPathWhenColumnsVisibleVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not bool playlistSummaryMode || values[1] is not Visibility columnsVisibility)
        {
            return DependencyProperty.UnsetValue;
        }

        return !playlistSummaryMode && columnsVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class playHistorySummaryVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not bool playHistoryActive || values[1] is not bool playlistSummaryMode)
        {
            return DependencyProperty.UnsetValue;
        }

        return playHistoryActive && !playlistSummaryMode ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal sealed class editableTextBlockWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double width ? width + 20d : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class encoderDriverSelectionEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int driverIndex ? driverIndex != 0 && driverIndex != 4 : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal sealed class backupSpanRadioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int currentSpan || !TryGetTargetSpan(parameter, out int targetSpan))
        {
            return DependencyProperty.UnsetValue;
        }

        return currentSpan == targetSpan;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool isChecked || !TryGetTargetSpan(parameter, out int targetSpan))
        {
            return Binding.DoNothing;
        }

        return isChecked ? targetSpan : 0;
    }

    private static bool TryGetTargetSpan(object parameter, out int targetSpan)
    {
        return int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out targetSpan)
            && targetSpan > 0;
    }
}
