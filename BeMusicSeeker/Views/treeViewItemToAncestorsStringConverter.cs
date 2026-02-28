using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

/// <summary>
/// TreeView 上の選択項目から、祖先を含む表示パス文字列を生成する converter です。
/// </summary>
internal class treeViewItemToAncestorsStringConverter : IMultiValueConverter
{
	/// <summary>
	/// 選択ノードから表示パスを生成します。
	/// <see cref="PlaylistFolderNode"/> を優先解釈し、互換のため <see cref="Tuple{T1,T2}"/> も引き続き扱います。
	/// 例外時は安全側で空文字列を返します。
	/// </summary>
	/// <param name="values">対象ノードと親 TreeView を含む入力配列。</param>
	/// <param name="targetType">未使用。</param>
	/// <param name="parameter">未使用。</param>
	/// <param name="culture">未使用。</param>
	/// <returns><c>A ≫ B ≫ C</c> 形式の表示パス。解決失敗時は空文字列。</returns>
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

	/// <summary>
	/// TreeViewItem の Header から、経路表示用の文字列を取得します。
	/// <see cref="PlaylistFolderNode"/> は <see cref="PlaylistFolderNode.DisplayName"/> を優先し、
	/// 旧互換として <see cref="Tuple{T1,T2}"/> も処理します。
	/// </summary>
	/// <param name="treeViewItem">対象のツリー項目。</param>
	/// <returns>表示用文字列。取得できない場合は空文字列。</returns>
	private static string GetTreeViewHeaderDisplayText(TreeViewItem treeViewItem)
	{
		if (treeViewItem?.Header == null)
		{
			return string.Empty;
		}
        if (treeViewItem.Header is PlaylistFolderNode folderNode)
        {
            return folderNode.DisplayName ?? string.Empty;
        }
        if (treeViewItem.Header is Tuple<string, bool> tupleHeader)
        {
            return tupleHeader.Item1 ?? string.Empty;
        }
		return treeViewItem.Header.ToString();
	}

	/// <summary>
	/// 未実装です。
	/// </summary>
	/// <param name="value">未使用。</param>
	/// <param name="targetTypes">未使用。</param>
	/// <param name="parameter">未使用。</param>
	/// <param name="culture">未使用。</param>
	/// <returns>常に例外を送出します。</returns>
	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
