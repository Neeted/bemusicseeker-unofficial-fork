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
			List<object> visualPathNodes = new List<object>();
			if (!((!(values[0] is DependencyObject)) ? WPFUtil.FindVisualChildPathSearchedByDataContext((TreeView)values[1], values[0], ref visualPathNodes) : WPFUtil.FindVisualChildPath((TreeView)values[1], (DependencyObject)values[0], ref visualPathNodes)))
			{
				return string.Empty;
			}
			List<string> ancestorHeaders = (from node in visualPathNodes.Where((object o) => o is TreeViewItem).Reverse()
				select GetTreeViewHeaderDisplayText((TreeViewItem)node)).ToList();
			if (ancestorHeaders.Count > 1 && ancestorHeaders[1] == Resources.Folder)
			{
				ancestorHeaders.RemoveAt(1);
			}
			else if (ancestorHeaders.Count > 1 && ancestorHeaders[0] == Resources.Maintenance)
			{
				ancestorHeaders.RemoveAt(0);
			}
			else if (ancestorHeaders.Count > 1 && ancestorHeaders[0] == Resources.Playlist)
			{
				ancestorHeaders.RemoveAt(0);
			}
			return string.Join(" ≫ ", ancestorHeaders);
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string GetTreeViewHeaderDisplayText(TreeViewItem treeViewItem)
	{
		if (treeViewItem?.Header == null)
		{
			return string.Empty;
		}
		if (treeViewItem.Header is Tuple<string, bool> tupleHeader)
		{
			return tupleHeader.Item1 ?? string.Empty;
		}
		return treeViewItem.Header.ToString();
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
