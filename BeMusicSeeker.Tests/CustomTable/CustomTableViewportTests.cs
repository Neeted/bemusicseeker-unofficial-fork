using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableViewportTests
{
    [TestMethod]
    public void FullVisibleRows_UsesFloorForFractionalViewport()
    {
        Assert.AreEqual(5, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 100d, rowHeight: 19d));
        Assert.AreEqual(95, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 5));
    }

    [TestMethod]
    public void FullVisibleRows_ExactMultipleKeepsExactCapacity()
    {
        Assert.AreEqual(5, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 95d, rowHeight: 19d));
        Assert.AreEqual(95, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 5));
    }

    [TestMethod]
    public void FullVisibleRows_HasMinimumOneRow()
    {
        Assert.AreEqual(1, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 10d, rowHeight: 19d));
        Assert.AreEqual(1, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 0d, rowHeight: 19d));
        Assert.AreEqual(99, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 1));
    }

    [TestMethod]
    public void DrawableRows_StillIncludesPartialBottomRow()
    {
        Assert.AreEqual(6, CustomTableView.CalculateDrawableRowCapacity(bodyHeight: 100d, rowHeight: 19d));
        Assert.AreEqual(5, CustomTableView.CalculateDrawableRowCapacity(bodyHeight: 95d, rowHeight: 19d));
    }

    [TestMethod]
    public void MaxScrollPosition_AllowsLastRowToFitWhenViewportIsFractional()
    {
        const int rowCount = 100;
        const double headerHeight = 22d;
        const double rowHeight = 19d;
        const double surfaceHeight = 122d;
        double bodyHeight = surfaceHeight - headerHeight;
        int fullRows = CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight, rowHeight);
        int firstVisibleAtBottom = CustomTableView.CalculateVerticalScrollMaximum(rowCount, fullRows);

        double lastRowY = headerHeight + (rowCount - 1 - firstVisibleAtBottom) * rowHeight;
        double lastRowBottom = lastRowY + rowHeight;

        Assert.IsTrue(lastRowBottom <= surfaceHeight);
    }
}
