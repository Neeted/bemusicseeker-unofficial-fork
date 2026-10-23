#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>
/// 内容領域を縮めないScrollViewerで、バーの表示切替に伴うWPF論理スクロールの再測定を接続します。
/// スクロール位置・範囲・仮想化は既存のScrollViewerとIScrollInfoが所有します。
/// </summary>
public sealed class OverlayScrollBar : ScrollBar
{
    /// <summary>
    /// 論理スクロールのバー表示が切り替わるときだけ、接続済みの内容からpresenterまでを再測定対象にします。
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != VisibilityProperty
            || TemplatedParent is not ScrollViewer { CanContentScroll: true } owner
            || owner.Template?.FindName("PART_ScrollContentPresenter", owner) is not ScrollContentPresenter presenter)
        {
            return;
        }

        IScrollInfo? candidate = FindScrollInfoCandidate(presenter, owner);
        if (candidate is not UIElement content
            || !ReferenceEquals(candidate.ScrollOwner, owner)
            || !presenter.IsAncestorOf(content))
        {
            return;
        }

        // WPFの仮想化パネルはバー切替後の二回目のMeasureを前提とする。
        // overlayではconstraintが同じため省略されるので、その経路だけを無効化する。
        // 自前のMeasureや予約は行わず、現在のWPFの測定パスで終端させる。
        for (DependencyObject? current = content; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element)
            {
                element.InvalidateMeasure();
            }
            if (ReferenceEquals(current, presenter))
            {
                break;
            }
        }
    }

    private static IScrollInfo? FindScrollInfoCandidate(ScrollContentPresenter presenter, ScrollViewer owner)
    {
        // WPFのHookupScrollingComponentsと同じ順序で一つだけ選び、未実体化なら何もしない。
        if (presenter.Content is IScrollInfo direct)
        {
            return direct;
        }
        if (presenter.Content is not Visual)
        {
            return null;
        }

        var itemsPresenter = presenter.Content as ItemsPresenter;
        if (itemsPresenter == null && owner.TemplatedParent is Control control)
        {
            itemsPresenter = control.Template?.FindName("ItemsPresenter", control) as ItemsPresenter;
        }
        return itemsPresenter != null && VisualTreeHelper.GetChildrenCount(itemsPresenter) > 0
            ? VisualTreeHelper.GetChild(itemsPresenter, 0) as IScrollInfo
            : null;
    }
}
