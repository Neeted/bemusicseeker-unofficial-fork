using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PackageMaintenanceEditorTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static IEnumerable<TextBox> FindCurrentEditors(DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox editor)
            {
                yield return editor;
            }
            foreach (TextBox nested in FindCurrentEditors(child))
            {
                yield return nested;
            }
        }
    }

}
