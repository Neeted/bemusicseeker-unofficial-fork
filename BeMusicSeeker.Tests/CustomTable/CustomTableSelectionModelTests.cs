using System;
using System.Linq;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableSelectionModelTests
{
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

    /// <summary>選択済み行への右クリックは複数選択を維持して現在行だけ変えることを確認し、メニュー操作の対象を一行へ狭める誤りを検出します。</summary>
    [TestMethod]
    public void SelectForRightClick_PreservesExistingMultiSelection()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForRightClick(2);

        CollectionAssert.AreEqual(new[] { 2, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(2, model.CurrentIndex);
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

    private static CustomTableSelectionModel CreateModel(int itemCount)
    {
        var model = new CustomTableSelectionModel();
        model.SetItemCount(itemCount);
        return model;
    }
}
