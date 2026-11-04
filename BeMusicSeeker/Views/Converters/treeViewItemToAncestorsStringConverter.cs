using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

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
    /// <param name="values">対象ノード、親 TreeView、言語変更で再評価するための現在の資源（値は使用しません）を含む入力配列。</param>
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
            if (values[1] is not TreeView)
            {
                throw new InvalidOperationException();
            }
            List<object> visualPathNodes = [];
            if (!((values[0] is not DependencyObject) ? WPFUtil.FindVisualChildPathSearchedByDataContext((TreeView)values[1], values[0], ref visualPathNodes) : WPFUtil.FindVisualChildPath((TreeView)values[1], (DependencyObject)values[0], ref visualPathNodes)))
            {
                return string.Empty;
            }
            List<string> ancestorHeaders = [.. (from node in visualPathNodes.Where(o => o is TreeViewItem).Reverse()
                                            select GetTreeViewHeaderDisplayText((TreeViewItem)node))];
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
    /// 見出しが資源 Binding の項目は、現在の資源値を取り直してから読みます。
    /// </summary>
    /// <param name="treeViewItem">対象のツリー項目。</param>
    /// <returns>表示用文字列。取得できない場合は空文字列。</returns>
    private static string GetTreeViewHeaderDisplayText(TreeViewItem treeViewItem)
    {
        // 採用理由: 言語変更の通知では、見出しの資源 Binding と経路の MultiBinding が同じ ResourceService の通知を
        // 別々に受けるため、WPF は更新順を保証しない。経路側が先に動くと Header はまだ旧語で、旧語を経路へ写してしまう。
        // そこで Header の Binding を先に評価し直し、通知の配送順に依存せず現在語を読む。
        // 副作用がない根拠: 対象は OneWay の Binding（資源値・データ項目の参照）だけで、UpdateTarget はソースの現在値を
        // 目標へ再代入するだけである。ソースへの書戻し・選択・展開・再生成は起こさず、値が同じなら観測可能な変化もない。
        // データ項目そのものへの Binding（PlaylistFolderNode やライブラリのフォルダ項目の `{Binding}`）は同じ参照を再代入するだけで、
        // Binding を持たない Header は GetBindingExpression が null で何もしない。
        treeViewItem?.GetBindingExpression(HeaderedItemsControl.HeaderProperty)?.UpdateTarget();
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
        if (treeViewItem.Header is PlayHistoryPeriodTreeItem playHistoryPeriod)
        {
            return playHistoryPeriod.Label ?? string.Empty;
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
