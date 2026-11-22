using System.Windows;
using System.Windows.Controls;

namespace BeMusicSeeker.Views;

/// <summary>
/// タブ列を固定し、選択中のページだけを一つの縦 <see cref="ScrollViewer"/> へ入れる <see cref="TabControl"/> です。
/// テンプレートは本文の <see cref="ScrollViewer"/> を <c>PART_PageScrollViewer</c> として宣言します。
/// </summary>
/// <remarks>
/// すべてのページが同じ表示領域を共有するため、ページを切り替えるたびに先頭へ戻し、直前のページの位置を引き継がせません。
/// 選択の切替自体は標準の <see cref="TabControl"/> に任せ、選択を外してから選び直す処理は行いません。
/// ページは論理木ではタブ項目の子で、本文の <see cref="ScrollViewer"/> は論理木に現れないため、
/// ページが本文の表示高に合わせたい場合は <see cref="PageViewportHeight"/> へ結び付けます。
/// </remarks>
public class ScrollingTabControl : NavigationTabControl
{
    /// <summary>テンプレートが本文のスクロールに使う <see cref="ScrollViewer"/> の部品名です。</summary>
    public const string PageScrollViewerPartName = "PART_PageScrollViewer";

    private static readonly DependencyPropertyKey PageViewportHeightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PageViewportHeight),
        typeof(double),
        typeof(ScrollingTabControl),
        new FrameworkPropertyMetadata(0d));

    /// <summary>本文の表示高（<see cref="ScrollViewer.ViewportHeight"/>）を識別します。</summary>
    public static readonly DependencyProperty PageViewportHeightProperty = PageViewportHeightPropertyKey.DependencyProperty;

    /// <summary>テンプレート適用後の本文の <see cref="ScrollViewer"/> を取得します。未適用なら null です。</summary>
    public ScrollViewer PageScrollViewer { get; private set; }

    /// <summary>本文の表示高を取得します。テンプレートの適用前は 0 です。</summary>
    public double PageViewportHeight => (double)GetValue(PageViewportHeightProperty);

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        if (PageScrollViewer != null)
        {
            PageScrollViewer.ScrollChanged -= PageScrollChanged;
        }

        base.OnApplyTemplate();
        PageScrollViewer = GetTemplateChild(PageScrollViewerPartName) as ScrollViewer;
        if (PageScrollViewer != null)
        {
            PageScrollViewer.ScrollChanged += PageScrollChanged;
        }

        SetValue(PageViewportHeightPropertyKey, PageScrollViewer?.ViewportHeight ?? 0d);
    }

    /// <inheritdoc />
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (e.AddedItems.Count > 0)
        {
            PageScrollViewer?.ScrollToTop();
        }
    }

    private void PageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0d)
        {
            SetValue(PageViewportHeightPropertyKey, PageScrollViewer.ViewportHeight);
        }
    }
}
