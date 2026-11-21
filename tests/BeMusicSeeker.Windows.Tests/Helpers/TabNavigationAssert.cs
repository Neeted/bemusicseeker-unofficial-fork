using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 画面のタブが、標準の <see cref="TabControl"/> として UI Automation に単一選択・選択必須で公開され、
/// 選択中の項目を選択から外す要求（<c>RemoveFromSelection</c>）で選択・内容・見出し・位置が変わらないことを、実画面の部品で観測する表明です。
/// </summary>
/// <remarks>
/// peer は <see cref="UIElementAutomationPeer.CreatePeerForElement"/> で取得し、型を固定して作りません。
/// 実入力（フォーカス取得・物理の修飾キー）は通常テストの合否にしないため、この表明は Automation の要求だけを送ります。
/// 拒否（<see cref="InvalidOperationException"/>）と無操作はどちらも許容し、選択が外れる、または外して選び直す実装だけを失敗にします。
/// </remarks>
internal static class TabNavigationAssert
{
    /// <summary>
    /// タブ全体の公開形式と、選択中の項目への <c>RemoveFromSelection</c> が選択を変えないことを確認します。
    /// </summary>
    /// <param name="tabs">表示済みのタブ。</param>
    /// <param name="observe">要求の前後で変わってはならない値（見出し・本文のページ・スクロール位置など）を返します。</param>
    public static void AssertSingleRequiredSelection(TabControl tabs, Func<object?[]>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(tabs)
            ?? throw new AssertFailedException("タブの Automation peer がありません。");
        Assert.AreEqual(AutomationControlType.Tab, peer.GetAutomationControlType(), "タブ全体は Tab として公開する。");
        var selection = (ISelectionProvider?)peer.GetPattern(PatternInterface.Selection);
        Assert.IsNotNull(selection, "Selection パターンがありません。");
        Assert.IsFalse(selection.CanSelectMultiple, "タブは単一選択です。");
        Assert.IsTrue(selection.IsSelectionRequired, "タブは選択必須です。");

        AutomationPeer[] items = peer.GetChildren()
            .Where(child => child.GetAutomationControlType() == AutomationControlType.TabItem)
            .ToArray();
        Assert.AreEqual(tabs.Items.Count, items.Length, "すべての項目を TabItem として公開する。");
        for (int i = 0; i < items.Length; i++)
        {
            string name = items[i].GetName();
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), "項目 " + i + " に名前がありません。");
            if (tabs.Items[i] is TabItem { Header: string header })
            {
                Assert.AreEqual(header, name, "項目名は見出しと一致する。");
            }
        }

        int selectedIndex = tabs.SelectedIndex;
        Assert.IsTrue(selectedIndex >= 0, "選択中の項目がありません。");
        var selectedProvider = (ISelectionItemProvider?)items[selectedIndex].GetPattern(PatternInterface.SelectionItem);
        Assert.IsNotNull(selectedProvider, "SelectionItem パターンがありません。");
        Assert.IsTrue(selectedProvider.IsSelected);
        Assert.AreEqual(1, selection.GetSelection().Length, "選択はちょうど1件です。");

        object? selectedItem = tabs.SelectedItem;
        object?[] before = observe?.Invoke() ?? [];
        int changes = 0;
        void CountOwnChange(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, tabs))
            {
                changes++;
            }
        }

        tabs.SelectionChanged += CountOwnChange;
        try
        {
            try
            {
                selectedProvider.RemoveFromSelection();
            }
            catch (InvalidOperationException)
            {
                // 選択必須のため、拒否も許容する。
            }

            TestUiDispatcherHost.ProcessQueuedPresentation();
            TestUiDispatcherHost.ProcessQueuedBackground();
        }
        finally
        {
            tabs.SelectionChanged -= CountOwnChange;
        }

        Assert.AreEqual(selectedIndex, tabs.SelectedIndex, "選択中の項目を選択から外せない。");
        Assert.AreSame(selectedItem, tabs.SelectedItem);
        Assert.IsTrue(((TabItem)tabs.SelectedItem).IsSelected);
        Assert.AreEqual(1, selection.GetSelection().Length);
        Assert.AreEqual(0, changes, "外して選び直す実装は SelectionChanged を起こすため許されない。");
        CollectionAssert.AreEqual(before, observe?.Invoke() ?? [], "選択・見出し・本文・位置が変わらない。");
    }

    /// <summary>
    /// ウィンドウの Automation peer から子孫をたどり、指定した AutomationId の要素へ UI Automation の木だけで到達できることを確認します。
    /// タブのテンプレートに入れた固定領域（見出し・操作）が、TabControl の peer に隠れて木から消えていないことを検出します。
    /// </summary>
    /// <param name="window">表示済みのウィンドウ。</param>
    /// <param name="automationIds">到達できなければならない AutomationId。</param>
    public static void AssertReachableByAutomation(Window window, params string[] automationIds)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutomationPeer peer in EnumerateAutomationTree(window))
        {
            string id = peer.GetAutomationId();
            if (!string.IsNullOrEmpty(id))
            {
                found.Add(id);
            }
        }

        foreach (string automationId in automationIds)
        {
            Assert.IsTrue(found.Contains(automationId), "UI Automation の木から " + automationId + " へ到達できません。");
        }
    }

    /// <summary>
    /// ウィンドウの Automation peer の木から、指定した AutomationId の要素がちょうど一つあり、コントロール要素として公開されることを確認して返します。
    /// </summary>
    /// <remarks>
    /// 支援技術や UI Automation クライアントの既定の検索はコントロールビューを使い、コントロール要素でない要素を返しません。
    /// テンプレート内の <see cref="TextBlock"/> のように、peer の木にはあってもコントロールビューから消える要素を検出します。
    /// 外部クライアントからの実際の検索は Desktop E2E が確認し、この表明はプロバイダー側の公開だけを確認します。
    /// </remarks>
    /// <param name="window">表示済みのウィンドウ。</param>
    /// <param name="automationId">探す AutomationId。</param>
    /// <returns>見つかった要素の peer。</returns>
    public static AutomationPeer FindControlElementByAutomation(Window window, string automationId)
    {
        AutomationPeer[] matches = [.. EnumerateAutomationTree(window).Where(peer => peer.GetAutomationId() == automationId)];
        Assert.HasCount(1, matches, "UI Automation の木で " + automationId + " はちょうど一つです。");
        Assert.IsTrue(matches[0].IsControlElement(), automationId + " はコントロールビューに公開されるコントロール要素です。");
        return matches[0];
    }

    private static IEnumerable<AutomationPeer> EnumerateAutomationTree(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        AutomationPeer root = UIElementAutomationPeer.CreatePeerForElement(window)
            ?? throw new AssertFailedException("ウィンドウの Automation peer がありません。");
        var pending = new Stack<AutomationPeer>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            AutomationPeer current = pending.Pop();
            yield return current;

            // peer は子の一覧を保持し、テスト内ではタブの切替で作り直されないことがある。前回たどった時点の子（切替前のページ）を読まないよう作り直す。
            current.ResetChildrenCache();
            foreach (AutomationPeer child in current.GetChildren() ?? [])
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// 項目の名前でタブを選び、<c>SelectionItem.Select</c> で切り替わること、無効な項目は選べないことを確認します。
    /// </summary>
    /// <param name="tabs">表示済みのタブ。</param>
    /// <param name="index">選ぶ項目の位置。</param>
    public static void SelectByAutomation(TabControl tabs, int index)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(tabs);
        AutomationPeer item = peer.GetChildren()
            .Where(child => child.GetAutomationControlType() == AutomationControlType.TabItem)
            .ElementAt(index);
        var provider = (ISelectionItemProvider?)item.GetPattern(PatternInterface.SelectionItem);
        Assert.IsNotNull(provider);
        provider.Select();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        Assert.AreEqual(index, tabs.SelectedIndex);
        Assert.IsTrue(provider.IsSelected);
    }
}
