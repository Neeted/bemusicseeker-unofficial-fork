using System.Windows.Controls;
using System.Windows.Input;

namespace BeMusicSeeker.Views;

/// <summary>
/// 画面の切替に使う標準の <see cref="TabControl"/> です。タブ項目の選択・矢印・Ctrl+Tab は標準の動作を使い、
/// ページ内の操作部品から上がったキーでタブが切り替わらないようにだけ絞ります。
/// </summary>
/// <remarks>
/// 標準の <see cref="TabControl"/> は、ページ内の Button や CheckBox など Home / End を処理しない部品から上がったキーでも先頭・末尾のタブへ切り替えます。
/// 画面の入力中に選択が動かないよう、Home / End はタブ項目にフォーカスがあるときだけ受け付けます。
/// </remarks>
public class NavigationTabControl : TabControl
{
    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Home or Key.End && e.OriginalSource is not TabItem)
        {
            return;
        }

        base.OnKeyDown(e);
    }
}
