using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

public static class TreeViewExt
{
    private static TreeViewItem findTreeViewChildSearchedByDataContext(ItemsControl parent, object targetDataContext)
    {
        if (parent == null || targetDataContext == null)
        {
            return null;
        }
        for (int i = 0; i < parent.Items.Count; i++)
        {
            object item = parent.Items[i];
            TreeViewItem treeViewItem = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (treeViewItem == null)
            {
                parent.UpdateLayout();
                treeViewItem = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            }
            if (treeViewItem != null)
            {
                if (ReferenceEquals(treeViewItem.DataContext, targetDataContext))
                {
                    return treeViewItem;
                }
                TreeViewItem treeViewItem2 = findTreeViewChildSearchedByDataContext(treeViewItem, targetDataContext);
                if (treeViewItem2 != null)
                {
                    return treeViewItem2;
                }
            }
            else if (ReferenceEquals(item, targetDataContext))
            {
                return null;
            }
        }
        return null;
    }

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

    private static bool selectTreeViewItemSearchedByDataContext(ItemsControl tree, object targetDataContext)
    {
        TreeViewItem treeViewItem = findTreeViewChildSearchedByDataContext(tree, targetDataContext);
        if (treeViewItem == null)
        {
            return false;
        }
        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
        return true;
    }

    public static bool SelectTreeViewItemSearchedByHeader(this TreeView treeView, string headerName)
    {
        return selectChildTreeViewItemSearchedByHeader(treeView, headerName);
    }

    public static bool SelectChildTreeViewItemSearchedByHeader(this TreeViewItem treeViewItem, string headerName)
    {
        return selectChildTreeViewItemSearchedByHeader(treeViewItem, headerName);
    }

    public static bool SelectTreeViewItemSearchedByDataContext(this TreeView treeView, object targetDataContext)
    {
        return selectTreeViewItemSearchedByDataContext(treeView, targetDataContext);
    }

    public static bool SelectChildTreeViewItemSearchedByDataContext(this TreeViewItem treeViewItem, object targetDataContext)
    {
        return selectTreeViewItemSearchedByDataContext(treeViewItem, targetDataContext);
    }
}
