using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeMusicSeeker.Views.Settings;

/// <summary>
/// 設定画面の縦タブです。縦のタブ列をクライアント領域の上端から下端まで届かせたまま、右の列に
/// 見出し・スクロールする本文・固定の下部領域（不備の案内と操作）を置きます。
/// </summary>
/// <remarks>
/// 下部領域は <see cref="Footer"/> として差し込みます。下部領域は画面側の要素と名前空間に属したまま、テンプレートの下端へ配置されます。
/// 標準の <see cref="TabControl"/> の Automation peer は項目しか子に公開しないため、見出しと下部領域の要素を
/// <see cref="SettingsTabControlAutomationPeer"/> が追加で公開し、ウィンドウから UI Automation の木で到達できるようにします。
/// </remarks>
public class SettingsTabControl : ScrollingTabControl
{
    /// <summary>テンプレートが選択中のタブ名を表示する見出しの部品名です。</summary>
    public const string PageHeaderPartName = "PART_PageHeader";

    /// <summary>下部領域に表示する要素を識別します。</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer),
        typeof(object),
        typeof(SettingsTabControl),
        new FrameworkPropertyMetadata(null, OnFooterChanged));

    /// <summary>右の列の下端に固定する下部領域を取得または設定します。本文のスクロールには含まれません。</summary>
    public object Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>テンプレート適用後の見出しを取得します。未適用なら null です。</summary>
    internal FrameworkElement PageHeader { get; private set; }

    /// <inheritdoc />
    protected override IEnumerator LogicalChildren
    {
        get
        {
            IEnumerator inherited = base.LogicalChildren;
            return Footer == null ? inherited : Concatenate(inherited, Footer);
        }
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        PageHeader = GetTemplateChild(PageHeaderPartName) as FrameworkElement;
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new SettingsTabControlAutomationPeer(this);

    private static void OnFooterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SettingsTabControl)d;
        if (e.OldValue != null)
        {
            control.RemoveLogicalChild(e.OldValue);
        }

        if (e.NewValue != null)
        {
            control.AddLogicalChild(e.NewValue);
        }
    }

    private static IEnumerator Concatenate(IEnumerator first, object last)
    {
        while (first.MoveNext())
        {
            yield return first.Current;
        }

        yield return last;
    }
}

/// <summary>
/// <see cref="SettingsTabControl"/> のテンプレートに置く、選択中のタブ名を表示するページ見出しです。
/// </summary>
/// <remarks>
/// 標準の <see cref="TextBlockAutomationPeer"/> は、テンプレート内の <see cref="TextBlock"/> をコントロール要素から外します。
/// その場合、見出しは UI Automation のコントロールビューに現れず、支援技術や UI Automation クライアントの既定の検索から見えません。
/// 見出しはページの名前を伝える唯一の要素なので、<see cref="SettingsPageHeaderTextAutomationPeer"/> でコントロール要素として公開します。
/// </remarks>
public class SettingsPageHeaderText : TextBlock
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new SettingsPageHeaderTextAutomationPeer(this);
}

/// <summary>
/// <see cref="SettingsPageHeaderText"/> を、テンプレート内でもコントロール要素として公開する peer です。名前と種類は標準の <see cref="TextBlockAutomationPeer"/> のままです。
/// </summary>
internal sealed class SettingsPageHeaderTextAutomationPeer : TextBlockAutomationPeer
{
    /// <summary>ページ見出しの peer を作ります。</summary>
    /// <param name="owner">peer が表す見出し。</param>
    internal SettingsPageHeaderTextAutomationPeer(SettingsPageHeaderText owner)
        : base(owner)
    {
    }

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;
}

/// <summary>
/// <see cref="SettingsTabControl"/> の項目に加えて、テンプレート内の見出しと下部領域の要素を子として公開します。
/// 選択・単一選択・TabItem の公開は標準の <see cref="TabControlAutomationPeer"/> のままです。
/// </summary>
internal sealed class SettingsTabControlAutomationPeer : TabControlAutomationPeer
{
    /// <summary>設定画面の縦タブの peer を作ります。</summary>
    /// <param name="owner">peer が表す縦タブ。</param>
    internal SettingsTabControlAutomationPeer(SettingsTabControl owner)
        : base(owner)
    {
    }

    /// <inheritdoc />
    protected override List<AutomationPeer> GetChildrenCore()
    {
        var owner = (SettingsTabControl)Owner;
        List<AutomationPeer> children = base.GetChildrenCore() ?? [];
        AppendPeers(owner.PageHeader, children);
        AppendPeers(owner.Footer as DependencyObject, children);
        return children;
    }

    // peer を持たない要素（Grid・StackPanel など）は、標準の peer と同じく視覚木の子孫から最初に見つかる peer を公開する。
    private static void AppendPeers(DependencyObject root, List<AutomationPeer> peers)
    {
        if (root == null)
        {
            return;
        }

        if (root is UIElement element)
        {
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer != null)
            {
                peers.Add(peer);
                return;
            }
        }

        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
        {
            AppendPeers(VisualTreeHelper.GetChild(root, i), peers);
        }
    }
}
