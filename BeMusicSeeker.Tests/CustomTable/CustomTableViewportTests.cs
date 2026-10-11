using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableViewportTests
{
    /// <summary>行高の端数が残る可視領域では完全に見える五行だけを容量とし最下端の開始行を求めることを確認し、部分行を容量へ数えて最終行を隠す誤りを検出します。</summary>
    [TestMethod]
    public void FullVisibleRows_UsesFloorForFractionalViewport()
    {
        Assert.AreEqual(5, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 100d, rowHeight: 19d));
        Assert.AreEqual(95, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 5));
    }

    /// <summary>可視領域が行高の整数倍なら五行をそのまま容量へ数えることを確認し、端の一行を引き過ぎて余分にスクロールする誤りを検出します。</summary>
    [TestMethod]
    public void FullVisibleRows_ExactMultipleKeepsExactCapacity()
    {
        Assert.AreEqual(5, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 95d, rowHeight: 19d));
        Assert.AreEqual(95, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 5));
    }

    /// <summary>領域が一行未満またはゼロでも容量を一行とすることを確認し、スクロール最大位置が行数外へ進む誤りを検出します。</summary>
    [TestMethod]
    public void FullVisibleRows_HasMinimumOneRow()
    {
        Assert.AreEqual(1, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 10d, rowHeight: 19d));
        Assert.AreEqual(1, CustomTableView.CalculateFullVisibleRowCapacity(bodyHeight: 0d, rowHeight: 19d));
        Assert.AreEqual(99, CustomTableView.CalculateVerticalScrollMaximum(rowCount: 100, fullVisibleRows: 1));
    }

    /// <summary>描画容量には端数の最下行も含め、整数倍では追加しないことを確認し、完全可視容量を描画へ流用して部分行を描かない誤りを検出します。</summary>
    [TestMethod]
    public void DrawableRows_StillIncludesPartialBottomRow()
    {
        Assert.AreEqual(6, CustomTableView.CalculateDrawableRowCapacity(bodyHeight: 100d, rowHeight: 19d));
        Assert.AreEqual(5, CustomTableView.CalculateDrawableRowCapacity(bodyHeight: 95d, rowHeight: 19d));
    }

    /// <summary>行高の端数を持つ領域で最大スクロール時の最終行下辺が領域内へ収まることを確認し、容量と最大位置の接続で最終行を切る誤りを検出します。</summary>
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
