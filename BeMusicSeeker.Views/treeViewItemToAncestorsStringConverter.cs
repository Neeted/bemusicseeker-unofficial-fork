using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

internal class treeViewItemToAncestorsStringConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		try
		{
			if (values[0] == null)
			{
				return string.Empty;
			}
			if (!(values[1] is TreeView))
			{
				throw new InvalidOperationException();
			}
			List<object> result = new List<object>();
			bool flag = false;
			if (!((!(values[0] is DependencyObject)) ? WPFUtil.FindVisualChildPathSearchedByDataContext((TreeView)values[1], values[0], ref result) : WPFUtil.FindVisualChildPath((TreeView)values[1], (DependencyObject)values[0], ref result)))
			{
				return string.Empty;
			}
			List<string> list = (from t in result.Where((object o) => o is TreeViewItem).Reverse()
				select ((TreeViewItem)t).Header.ToString()).ToList();
			if (list.Count > 1 && list[1] == Resources.Folder)
			{
				list.RemoveAt(1);
			}
			else if (list.Count > 1 && list[0] == Resources.Maintenance)
			{
				list.RemoveAt(0);
			}
			else if (list.Count > 1 && list[0] == Resources.Playlist)
			{
				list.RemoveAt(0);
			}
			return string.Join(" ≫ ", list);
		}
		catch
		{
			return string.Empty;
		}
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
