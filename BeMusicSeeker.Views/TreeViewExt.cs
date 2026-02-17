using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

public static class TreeViewExt
{
	private static TreeViewItem findTreeViewChildSearchedByHeader(DependencyObject d, string x)
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(d) - 1; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(d, i);
				if (child != null && child is TreeViewItem && ((TreeViewItem)child).Header.ToString() == x)
				{
					return (TreeViewItem)child;
				}
				TreeViewItem treeViewItem = findTreeViewChildSearchedByHeader(child, x);
				if (treeViewItem != null)
				{
					return treeViewItem;
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static bool selectChildTreeViewItemSearchedByHeader(DependencyObject d, string headerName)
	{
		TreeViewItem treeViewItem = findTreeViewChildSearchedByHeader(d, headerName);
		if (treeViewItem != null)
		{
			treeViewItem.IsSelected = true;
			treeViewItem.Focus();
			return true;
		}
		return false;
	}

	public static bool SelectTreeViewItemSearchedByHeader(this TreeView treeView, string headerName)
	{
		return selectChildTreeViewItemSearchedByHeader(treeView, headerName);
	}

	public static bool SelectChildTreeViewItemSearchedByHeader(this TreeViewItem treeViewItem, string headerName)
	{
		return selectChildTreeViewItemSearchedByHeader(treeViewItem, headerName);
	}
}
