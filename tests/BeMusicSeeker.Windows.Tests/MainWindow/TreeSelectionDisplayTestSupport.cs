using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 実MainWindowの左ツリーで、仮想化による入れ物の解放・再利用を起こすスクロールの前後に、選択表示が選択項目だけに付くことを観測する補助です。
/// </summary>
/// <remarks>
/// 実体化済みの入れ物は <see cref="ItemContainerGenerator"/> から辿ります。visual treeから列挙すると、再利用待ちで表示されない入れ物を数えるためです。
/// </remarks>
internal static class TreeSelectionDisplayTestSupport
{
    /// <summary>
    /// 判定範囲の根より下で実体化されている入れ物のうち、選択表示を持つのは選択したデータ項目の入れ物だけ（0か1）であることを確認します。
    /// requireRealizedでは、選択項目の入れ物が実体化されて選択表示を持ち、根より下の全祖先の入れ物が展開されていることも確認します。
    /// </summary>
    internal static void AssertSelectionDisplay(TreeView tree, ItemsControl scope, object selected, string stage, bool requireRealized)
    {
        Assert.AreSame(selected, tree.SelectedItem, stage + "：左の選択");
        var realized = new List<(object Item, TreeViewItem Container, TreeViewItem[] Ancestors)>();
        CollectRealized(scope, []);
        var displayed = realized.Where(entry => entry.Container.IsSelected).ToList();
        Assert.IsTrue(displayed.All(entry => ReferenceEquals(entry.Item, selected)),
            stage + "：選択表示が別の項目にあります: " + string.Join(", ", displayed.Select(entry => entry.Item)));
        Assert.IsTrue(displayed.Count <= 1, stage + "：選択表示が複数あります。");
        if (requireRealized)
        {
            Assert.AreEqual(1, displayed.Count, stage + "：選択項目の入れ物が実体化され、選択表示を持ちます。");
            Assert.IsTrue(displayed[0].Ancestors.All(ancestor => ancestor.IsExpanded), stage + "：選択項目の祖先は展開されたままです。");
        }

        void CollectRealized(ItemsControl owner, TreeViewItem[] ancestors)
        {
            for (int index = 0; index < owner.Items.Count; index++)
            {
                if (owner.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem container) { continue; }
                realized.Add((owner.Items[index], container, ancestors));
                CollectRealized(container, [.. ancestors, container]);
            }
        }
    }

    /// <summary>
    /// スクロールの前後で、左の選択参照と呼出し側が渡す要求の記録が変わらず、判定範囲の根が展開されたままであることを確認します。
    /// </summary>
    /// <param name="captureRequests">一覧要求の件数・改訂番号・行集合など、スクロールだけでは変わらない値を比較できる文字列で返します。</param>
    internal static void ScrollAndAssertSelectionUnchanged(TreeView tree, ItemsControl scope, object selected,
        Func<string> captureRequests, Action scrollSteps)
    {
        Assert.AreSame(selected, tree.SelectedItem, "スクロール前の左の選択");
        string requestsBefore = captureRequests();
        scrollSteps();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        // Loaded優先度で予約される選択の同期処理を先に進め、その処理が積んだBackgroundの予約も含めて消化してから、要求が増えていないことを確認する。
        TestUiDispatcherHost.ProcessQueuedBackground();
        Assert.AreSame(selected, tree.SelectedItem, "スクロール後の左の選択");
        Assert.AreEqual(requestsBefore, captureRequests(), "スクロールだけでは一覧要求と一覧を変えません。");
        Assert.IsTrue(scope is not TreeViewItem scopeItem || scopeItem.IsExpanded, "判定範囲の根は展開されたままです。");
    }

    /// <summary>スクロール後のレイアウトと遅延した仮想化の解放・生成を、条件の成立まで実Dispatcherで待ちます。</summary>
    internal static void AwaitTreeLayout(TreeView tree, Func<bool> condition, string operationName)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler check = (_, _) => { if (condition()) { reached.TrySetResult(); } };
        tree.LayoutUpdated += check;
        try
        {
            tree.UpdateLayout();
            check(tree, EventArgs.Empty);
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(reached.Task, operationName);
        }
        finally { tree.LayoutUpdated -= check; }
    }

    /// <summary>指定したItemsControlが項目を並べる仮想化パネルを返します。</summary>
    internal static VirtualizingStackPanel FindItemsHostPanel(ItemsControl owner)
        => FindDescendants<VirtualizingStackPanel>(owner).First(panel => ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner));

    /// <summary>visual treeの子孫から指定型の要素を深さ優先で列挙します。</summary>
    internal static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { yield return match; }
            foreach (T nested in FindDescendants<T>(child)) { yield return nested; }
        }
    }
}
