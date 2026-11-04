using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

/// <summary>
/// ツリーのフォルダノードを表示名へ変換します。2番目の入力に <c>ResourceService.Resources</c> を受け、
/// 言語変更で再評価させるためだけに使います。ノード自体は作り直さず、空名の案内は評価時点の現在語で解決します。
/// </summary>
internal sealed class playlistFolderNodeDisplayConverter : IMultiValueConverter
{
    /// <summary>ノードの <see cref="PlaylistFolderNode.DisplayName"/> を返します。ノード以外は空文字列です。</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values != null && values.Length > 0 && values[0] is PlaylistFolderNode node
            ? node.DisplayName
            : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
