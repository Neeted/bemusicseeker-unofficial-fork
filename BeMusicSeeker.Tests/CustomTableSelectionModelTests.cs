using System.Linq;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableSelectionModelTests
{
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

    [TestMethod]
    public void SelectForLeftMouseDown_PreservesExistingMultiSelectionForDragStart()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForLeftMouseDown(2);

        CollectionAssert.AreEqual(new[] { 2, 4 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(2, model.CurrentIndex);
    }

    [TestMethod]
    public void SelectForLeftMouseDown_SelectsUnselectedRow()
    {
        CustomTableSelectionModel model = CreateModel(10);
        model.SelectSingle(2);
        model.Toggle(4);

        model.SelectForLeftMouseDown(7);

        CollectionAssert.AreEqual(new[] { 7 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(7, model.CurrentIndex);
        Assert.AreEqual(7, model.AnchorIndex);
    }

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

    [TestMethod]
    public void SelectAll_SelectsEveryRowAndSetsCurrentAndAnchor()
    {
        CustomTableSelectionModel model = CreateModel(4);

        model.SelectAll();

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, model.SelectedIndices.ToArray());
        Assert.AreEqual(0, model.CurrentIndex);
        Assert.AreEqual(0, model.AnchorIndex);
    }

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
        CustomTableSelectionModel model = new CustomTableSelectionModel();
        model.SetItemCount(itemCount);
        return model;
    }
}
