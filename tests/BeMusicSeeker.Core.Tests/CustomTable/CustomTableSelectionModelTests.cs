using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableSelectionModelTests
{
    /// <summary>全選択後は集合が同じでも主行が移れば変更を返し、同じ末尾への反復では返さないことを確認し、主行だけの通知漏れを検出します。</summary>
    [TestMethod]
    public void SelectRange_ReportsCurrentChangeWithinSameSelectionAndIgnoresRepeatedInput()
    {
        CustomTableSelectionModel model = CreateModel(4);
        Assert.IsTrue(model.SelectAll());

        Assert.IsTrue(model.SelectRange(3));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(3, model.CurrentIndex);
        Assert.AreEqual(0, model.AnchorIndex);
        Assert.IsFalse(model.SelectRange(3));

        // 集合と主行が共に変わる縮小、主行を保つ集合だけの変更も通知対象です。
        Assert.IsTrue(model.SelectRange(2));
        Assert.IsTrue(model.RestoreIndices([0, 2], 2, 0));
        Assert.IsFalse(model.RestoreIndices([0, 2], 2, 0));
    }

    /// <summary>単一選択の集合と主行が同じなら基点だけを戻しても変更を返さず、その基点が次のShift範囲へ反映されることを確認します。</summary>
    [TestMethod]
    public void SelectSingle_UpdatesOnlyAnchorWithoutReportingSelectionChange()
    {
        CustomTableSelectionModel model = CreateModel(3);
        Assert.IsTrue(model.SelectSingle(1));
        Assert.IsTrue(model.Toggle(0));
        Assert.IsTrue(model.Toggle(0));
        Assert.AreEqual(0, model.AnchorIndex);

        Assert.IsFalse(model.SelectSingle(1));
        Assert.AreEqual(1, model.AnchorIndex);
        Assert.IsFalse(model.SyncCurrentIndex(1));
        Assert.IsFalse(model.MoveCurrent(0));
        Assert.IsTrue(model.ExtendRangeBy(1));
        CollectionAssert.AreEqual(new[] { 1, 2 }, model.SelectedIndices.ToArray());
    }

    /// <summary>復元と全選択でも基点だけの変更は通知せず内部に採用し、後続範囲と空状態の反復を同じ判定へ揃えることを確認します。</summary>
    [TestMethod]
    public void RestoreAndSelectAll_UpdateAnchorWithoutReportingSelectionChange()
    {
        CustomTableSelectionModel model = CreateModel(3);
        Assert.IsTrue(model.RestoreIndices([0, 1, 2], 0, 2));
        Assert.IsFalse(model.SelectAll());
        Assert.AreEqual(0, model.AnchorIndex);
        Assert.IsFalse(model.SelectAll());
        Assert.IsFalse(model.RestoreIndices([0, 1, 2], 0, 2));
        Assert.AreEqual(2, model.AnchorIndex);
        Assert.IsTrue(model.SelectRange(1));
        CollectionAssert.AreEqual(new[] { 1, 2 }, model.SelectedIndices.ToArray());
        Assert.IsFalse(model.SelectForRightClick(1));
        Assert.IsFalse(model.SelectForLeftMouseDownDragCandidate(1));
        Assert.IsFalse(model.Toggle(-1));
        Assert.IsFalse(model.CoerceToItemCount());
        Assert.IsTrue(model.Clear());
        Assert.IsFalse(model.Clear());
        Assert.IsFalse(model.CoerceToItemCount());
    }

    /// <summary>識別復元では主行と基点を独立に戻し、主行消失・基点消失・全消失を既定の別行選択へ置き換えないことを確認します。</summary>
    [TestMethod]
    public void RestoreIndices_PreservesIndependentAnchorAndClearsMissingSelection()
    {
        CustomTableSelectionModel model = CreateModel(5);
        model.RestoreIndices([0, 3], 0, 3);
        Assert.AreEqual(0, model.CurrentIndex);
        Assert.AreEqual(3, model.AnchorIndex);
        model.SelectRange(2);
        CollectionAssert.AreEqual(new[] { 2, 3 }, model.SelectedIndices.ToArray());

        model.RestoreIndices([1, 4], -1, 4);
        Assert.AreEqual(1, model.CurrentIndex);
        Assert.AreEqual(4, model.AnchorIndex);
        model.RestoreIndices([1, 4], 4, -1);
        Assert.AreEqual(4, model.CurrentIndex);
        Assert.AreEqual(4, model.AnchorIndex);
        model.RestoreIndices([-1], -1, 2);
        Assert.AreEqual(-1, model.CurrentIndex);
        Assert.AreEqual(-1, model.AnchorIndex);
        Assert.AreEqual(0, model.SelectedCount);

        // 明示選択の既存APIは選択外の主行候補を基点として残しません。
        model.SelectIndices([1, 4], 2);
        Assert.AreEqual(1, model.CurrentIndex);
        Assert.AreEqual(1, model.AnchorIndex);
    }

    /// <summary>既存選択の後に別行を単一選択すると旧選択を除いて現在行と範囲起点も移ることを確認し、旧選択や起点が残る誤りを検出します。</summary>
    [TestMethod]
    public void SelectSingle_ReplacesSelectionAndSetsAnchor()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.SelectSingle(5);

        CollectionAssert.AreEqual(new[] { 5 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(5, model.CurrentIndex);
        Assert.AreEqual(5, model.AnchorIndex);
    }

    /// <summary>別行を追加して元行を切り替えると追加行だけが選択と現在行に残ることを確認し、切替による全解除や現在行の取違えを検出します。</summary>
    [TestMethod]
    public void Toggle_AddsAndRemovesSelection()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);
        model.Toggle(2);

        CollectionAssert.AreEqual(new[] { 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(4, model.CurrentIndex);
    }

    /// <summary>単一選択の起点から別行へ範囲選択すると両端を含み起点を保つことを確認し、範囲境界や起点を移す誤りを検出します。</summary>
    [TestMethod]
    public void SelectRange_UsesAnchor()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.SelectRange(5);

        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(5, model.CurrentIndex);
        Assert.AreEqual(2, model.AnchorIndex);
    }

    /// <summary>選択済み行への右クリックは複数選択と範囲基点を維持して現在行だけ変えることを確認し、メニュー操作の対象を一行へ狭める誤りを検出します。</summary>
    [TestMethod]
    public void SelectForRightClick_PreservesExistingMultiSelection()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForRightClick(2);

        CollectionAssert.AreEqual(new[] { 2, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(2, model.CurrentIndex);
        Assert.AreEqual(4, model.AnchorIndex);
    }

    /// <summary>未選択行への右クリックは旧複数選択を置換して現在行と起点をその行へ移すことを確認し、別行のメニューで旧選択を操作する誤りを検出します。</summary>
    [TestMethod]
    public void SelectForRightClick_SelectsUnselectedRow()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForRightClick(7);

        CollectionAssert.AreEqual(new[] { 7 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(7, model.CurrentIndex);
        Assert.AreEqual(7, model.AnchorIndex);
    }

    /// <summary>選択済み行でドラッグを始める押下は複数選択を維持することを確認し、ドラッグ開始時に移す対象を一行へ縮める誤りを検出します。</summary>
    [TestMethod]
    public void SelectForLeftMouseDownDragCandidate_PreservesExistingMultiSelectionForDragStart()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForLeftMouseDownDragCandidate(2);

        CollectionAssert.AreEqual(new[] { 2, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(2, model.CurrentIndex);
    }

    /// <summary>未選択行でドラッグを始める押下はその行だけを選んで現在行と起点を移すことを確認し、押下した行と異なる旧選択をドラッグする誤りを検出します。</summary>
    [TestMethod]
    public void SelectForLeftMouseDownDragCandidate_SelectsUnselectedRow()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForLeftMouseDownDragCandidate(7);

        CollectionAssert.AreEqual(new[] { 7 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(7, model.CurrentIndex);
        Assert.AreEqual(7, model.AnchorIndex);
    }

    /// <summary>行数を減らすと範囲外の選択だけを除いて残る選択を現在行に保つことを確認し、存在しない行を操作対象として残す誤りを検出します。</summary>
    [TestMethod]
    public void SetItemCount_RemovesOutOfRangeSelection()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(8);

        model.SetItemCount(5);

        CollectionAssert.AreEqual(new[] { 2 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(2, model.CurrentIndex);
    }

    /// <summary>全選択は全行を選び現在行と起点を先頭へ揃えることを確認し、行の欠落や全選択後の操作起点の誤りを検出します。</summary>
    [TestMethod]
    public void SelectAll_SelectsEveryRowAndSetsCurrentAndAnchor()
    {
        CustomTableSelectionModel model = CreateModel(4);

        model.SelectAll();

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(0, model.CurrentIndex);
        Assert.AreEqual(0, model.AnchorIndex);
    }

    /// <summary>重複と範囲外を混ぜた指定から有効な行だけ重複除去し指定現在行を起点にすることを確認し、選択復元で無効行を残す誤りを検出します。</summary>
    [TestMethod]
    public void SelectIndices_SelectsDistinctValidIndicesAndUsesCurrent()
    {
        CustomTableSelectionModel model = CreateModel(6);

        model.SelectIndices(new[] { 4, 1, 4, -1, 9 }, 4);

        CollectionAssert.AreEqual(new[] { 1, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(4, model.CurrentIndex);
        Assert.AreEqual(4, model.AnchorIndex);
    }

    /// <summary>指定現在行が選択集合にない場合は選択中の先頭を現在行と起点にすることを確認し、非選択行を操作対象にする誤りを検出します。</summary>
    [TestMethod]
    public void SelectIndices_FallsBackToFirstSelectedIndexWhenCurrentIsNotSelected()
    {
        CustomTableSelectionModel model = CreateModel(6);

        model.SelectIndices(new[] { 3, 1 }, 4);

        CollectionAssert.AreEqual(new[] { 1, 3 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(1, model.CurrentIndex);
        Assert.AreEqual(1, model.AnchorIndex);
    }

    /// <summary>有効行のない復元指定で既存選択・現在行・起点を全て解除することを確認し、無効な復元で古い操作対象を残す誤りを検出します。</summary>
    [TestMethod]
    public void SelectIndices_ClearsSelectionWhenNoValidIndexExists()
    {
        CustomTableSelectionModel model = CreateModel(2);
        model.SelectSingle(1);

        model.SelectIndices(new[] { -1, 2 }, -1);

        CollectionAssert.AreEqual(Array.Empty<int>(), model.SelectedIndices.ToArray());
        Assert.AreEqual(-1, model.CurrentIndex);
        Assert.AreEqual(-1, model.AnchorIndex);
    }

    /// <summary>単一選択から上下へ行数を超えて移動しても両端へ収めて選択を置き換え、範囲外の現在行や元の単一選択を残す誤りを検出します。</summary>
    [TestMethod]
    public void MoveCurrent_ClampsAndSelectsSingleRow()
    {
        CustomTableSelectionModel model = CreateModel(4);
        model.SelectSingle(2);
        model.MoveCurrent(10);

        CollectionAssert.AreEqual(new[] { 3 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(3, model.CurrentIndex);
        Assert.AreEqual(3, model.AnchorIndex);

        model.MoveCurrent(-10);

        CollectionAssert.AreEqual(new[] { 0 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(0, model.CurrentIndex);
    }

    /// <summary>起点から下へ伸ばした後に起点を越えて上へ移る範囲選択でも起点を保持することを確認し、方向反転で範囲や現在行を取り違える誤りを検出します。</summary>
    [TestMethod]
    public void ExtendRangeBy_KeepsAnchorAndExtendsSelection()
    {
        CustomTableSelectionModel model = CreateModel(6);
        model.SelectSingle(2);
        model.ExtendRangeBy(2);

        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(4, model.CurrentIndex);
        Assert.AreEqual(2, model.AnchorIndex);

        model.ExtendRangeBy(-3);

        CollectionAssert.AreEqual(new[] { 1, 2 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(1, model.CurrentIndex);
        Assert.AreEqual(2, model.AnchorIndex);
    }

    /// <summary>個別追加と解除で区間が結合・分割・端の縮小をしても、列挙・件数・境界の判定が同じ集合を指すことを確認し、区間の結合漏れや端の取違えを検出します。</summary>
    [TestMethod]
    public void Toggle_MergesSplitsAndTrimsIntervalsConsistently()
    {
        CustomTableSelectionModel model = CreateModel(10);
        foreach (int index in new[] { 1, 2, 3, 5, 6, 7 })
        {
            Assert.IsTrue(model.Toggle(index));
        }
        AssertSelection(model, 10, 1, 2, 3, 5, 6, 7);

        Assert.IsTrue(model.Toggle(4));
        AssertSelection(model, 10, 1, 2, 3, 4, 5, 6, 7);

        Assert.IsTrue(model.Toggle(5));
        AssertSelection(model, 10, 1, 2, 3, 4, 6, 7);

        Assert.IsTrue(model.Toggle(1));
        AssertSelection(model, 10, 2, 3, 4, 6, 7);

        Assert.IsTrue(model.Toggle(7));
        AssertSelection(model, 10, 2, 3, 4, 6);

        Assert.IsTrue(model.Toggle(3));
        AssertSelection(model, 10, 2, 4, 6);

        Assert.IsTrue(model.Toggle(0));
        Assert.IsTrue(model.Toggle(9));
        AssertSelection(model, 10, 0, 2, 4, 6, 9);
        Assert.IsFalse(model.Toggle(10));
        Assert.IsFalse(model.Toggle(-1));
        AssertSelection(model, 10, 0, 2, 4, 6, 9);
    }

    /// <summary>行数の変更で区間を途中で切る・消す・増やしても残る集合が正しいことを確認し、切詰め後の件数の不一致や増加時の選択の自動拡張を検出します。</summary>
    [TestMethod]
    public void SetItemCount_TrimsIntervalsAndKeepsSelectionOnGrowth()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(1);
        model.SelectRange(7);
        AssertSelection(model, 10, 1, 2, 3, 4, 5, 6, 7);

        model.SetItemCount(5);
        AssertSelection(model, 5, 1, 2, 3, 4);
        Assert.AreEqual(4, model.CurrentIndex);

        model.SetItemCount(10);
        AssertSelection(model, 10, 1, 2, 3, 4);

        model.Toggle(8);
        model.SetItemCount(6);
        AssertSelection(model, 6, 1, 2, 3, 4);

        model.SetItemCount(0);
        AssertSelection(model, 3);
        Assert.AreEqual(-1, model.CurrentIndex);
        Assert.AreEqual(-1, model.AnchorIndex);

        model.SelectRange(0);
        model.SetItemCount(-3);
        AssertSelection(model, 3);
        model.SetItemCount(10);
        AssertSelection(model, 10);
    }

    /// <summary>復元へ重複・順不同・範囲外を与えても昇順で重複のない集合になることを確認し、並びや重複を信用して区間が壊れる誤りを検出します。</summary>
    [TestMethod]
    public void RestoreIndices_NormalizesUnorderedDuplicatedAndOutOfRangeInput()
    {
        CustomTableSelectionModel model = CreateModel(10);

        Assert.IsTrue(model.RestoreIndices([5, 3, 4, 4, -1, 9, 1], 4, 3));

        AssertSelection(model, 10, 1, 3, 4, 5, 9);
        Assert.AreEqual(4, model.CurrentIndex);
        Assert.AreEqual(3, model.AnchorIndex);
        Assert.AreEqual(5, model.SelectedCount);
    }

    /// <summary>別経路で作った同じ集合と主行ではfalse、件数も端も同じ別集合ではtrueになることを確認し、件数や端だけで同一とみなす誤りを検出します。</summary>
    [TestMethod]
    public void SetEquality_DistinguishesSameCountAndEndsButDifferentMiddle()
    {
        CustomTableSelectionModel built = CreateModel(10);
        built.Toggle(1);
        built.Toggle(2);
        built.Toggle(3);
        Assert.IsFalse(built.RestoreIndices([3, 2, 1], 3, 1));
        Assert.IsFalse(built.SelectRange(3));
        AssertSelection(built, 10, 1, 2, 3);

        CustomTableSelectionModel ranged = CreateModel(10);
        ranged.SelectSingle(1);
        Assert.IsTrue(ranged.SelectRange(3));
        Assert.IsFalse(ranged.RestoreIndices([1, 2, 3], 3, 1));

        CustomTableSelectionModel model = CreateModel(10);
        Assert.IsTrue(model.RestoreIndices([0, 1, 3], 0, 0));
        Assert.IsFalse(model.RestoreIndices([3, 1, 0], 0, 0));
        Assert.IsTrue(model.RestoreIndices([0, 2, 3], 0, 0));
        AssertSelection(model, 10, 0, 2, 3);
    }

    /// <summary>複数区間での主行・基点の既存規則と、行数増加後の全選択の変化判定を固定し、区間化で規則が変わる誤りを検出します。</summary>
    [TestMethod]
    public void MultiInterval_KeepsCurrentAnchorAndSelectAllRules()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.RestoreIndices([1, 2, 5, 6, 8], 5, 5);

        // Ctrlで外した主行は、残りの最大の添字になる。
        Assert.IsTrue(model.Toggle(6));
        AssertSelection(model, 10, 1, 2, 5, 8);
        Assert.AreEqual(8, model.CurrentIndex);
        Assert.AreEqual(6, model.AnchorIndex);
        Assert.IsTrue(model.Toggle(8));
        Assert.AreEqual(5, model.CurrentIndex);

        // 行数補正で主行が範囲外なら残りの最大、基点も範囲外なら主行へ揃える。
        model.RestoreIndices([1, 2, 5, 8], 5, 8);
        model.SetItemCount(2);
        AssertSelection(model, 2, 1);
        Assert.AreEqual(1, model.CurrentIndex);
        Assert.AreEqual(1, model.AnchorIndex);

        // 主行が消えた復元は残りの最小の添字になる。
        model.SetItemCount(10);
        Assert.IsTrue(model.RestoreIndices([7, 3, 2], 5, 3));
        Assert.AreEqual(2, model.CurrentIndex);
        Assert.AreEqual(3, model.AnchorIndex);

        // 件数が行数と同じでも主行が先頭でなければ全選択は変更になる。
        model.RestoreIndices([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], 3, 0);
        Assert.IsTrue(model.SelectAll());
        Assert.IsFalse(model.SelectAll());

        // 行数が増えた後の全選択は、主行が先頭でも件数の不足で変更になる。
        model.SetItemCount(12);
        AssertSelection(model, 12, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.IsTrue(model.SelectAll());
        AssertSelection(model, 12, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    }

    /// <summary>範囲選択・全選択・件数補正・判定の操作列で割り当てる量が行数に依存しないことを確認し、行数や選択行数に比例する複製・走査の再導入を検出します。</summary>
    [TestMethod]
    public void SelectionOperations_AllocateIndependentlyOfItemCount()
    {
        const int Small = 1_000;
        const int Large = 210_000;
        RunWorkload(Small);
        RunWorkload(Small);
        RunWorkload(Large);

        long beforeSmall = GC.GetAllocatedBytesForCurrentThread();
        (bool isSelected, bool isToggledOut, int shrunk, int final) small = RunWorkload(Small);
        long smallBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSmall;

        long beforeLarge = GC.GetAllocatedBytesForCurrentThread();
        (bool isSelected, bool isToggledOut, int shrunk, int final) large = RunWorkload(Large);
        long largeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeLarge;

        Assert.IsTrue(small.isSelected && large.isSelected);
        Assert.IsFalse(small.isToggledOut || large.isToggledOut, "解除した行が選択に残っている");
        Assert.AreEqual(Small / 16, small.shrunk);
        Assert.AreEqual(Large / 16, large.shrunk);
        Assert.AreEqual(Small / 16, small.final);
        Assert.AreEqual(Large / 16, large.final);
        Assert.IsTrue(largeBytes <= smallBytes + 4096, $"small={smallBytes} large={largeBytes}");
    }

    /// <summary>測定対象の操作列です。割り当てを測るため、LINQの列挙も複製も行いません。</summary>
    private static (bool IsSelected, bool IsToggledOut, int Shrunk, int Final) RunWorkload(int n)
    {
        CustomTableSelectionModel model = CreateModel(n);
        model.SelectAll();
        model.SelectRange(n - 1);
        model.SelectRange(n / 2);
        model.Toggle(n / 4);
        model.Toggle(n / 4);
        model.Toggle(n / 8);
        bool isSelected = model.IsSelected(n / 3);
        bool isToggledOut = model.IsSelected(n / 8);
        model.SetItemCount(n / 16);
        int shrunk = model.SelectedCount;
        model.SelectAll();
        return (isSelected, isToggledOut, shrunk, model.SelectedCount);
    }

    /// <summary>列挙・件数・範囲前後の添字を含む判定が、期待する集合と一致することを確認します。</summary>
    private static void AssertSelection(CustomTableSelectionModel model, int itemCount, params int[] expected)
    {
        CollectionAssert.AreEqual(expected, model.SelectedIndices.ToArray());
        Assert.AreEqual(expected.Length, model.SelectedCount);
        var set = new HashSet<int>(expected);
        for (int index = -2; index <= itemCount + 2; index++)
        {
            Assert.AreEqual(set.Contains(index), model.IsSelected(index), "index=" + index);
        }
    }

    private static CustomTableSelectionModel CreateModel(int itemCount)
    {
        var model = new CustomTableSelectionModel();
        model.SetItemCount(itemCount);
        return model;
    }

}
