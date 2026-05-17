using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class textToCharBlockConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        ItemsControl itemsControl = new ItemsControl();
        if (!(value is string))
        {
            return itemsControl;
        }
        char[] array = (value as string).ToCharArray();
        FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(StackPanel));
        frameworkElementFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        frameworkElementFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        itemsControl.ItemsPanel = new ItemsPanelTemplate
        {
            VisualTree = frameworkElementFactory
        };
        char[] array2 = array;
        foreach (char c in array2)
        {
            itemsControl.Items.Add(new TextBlock
            {
                Text = c.ToString(),
                Margin = new Thickness
                {
                    Right = -2.0
                }
            });
        }
        return itemsControl;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
