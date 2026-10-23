using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.ViewModels;
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

    /// <summary>全面100×120・見出し/行20の実配置と算定入口の0.5 DIP境界で両軸の誘発を確認し、旧ガターや前回の表示状態へ依存する判定を検出します。</summary>
    [TestMethod]
    [DataRow(90d, 5, false, false, 0d, 0d, 100d, 5)]
    [DataRow(80d, 6, true, false, 0d, 1d, 84d, 5)]
    [DataRow(120d, 4, false, true, 20d, 0d, 100d, 4)]
    [DataRow(90d, 6, true, true, 6d, 2d, 84d, 4)]
    [DataRow(101d, 5, true, true, 17d, 1d, 84d, 4)]
    [DataRow(100.5d, 5, false, false, 0.5d, 0d, 100d, 5)]
    [DataRow(100.5001d, 5, true, true, 16.5001d, 1d, 84d, 4)]
    public void OverlayScrollBars_UseEffectiveViewportWithoutShrinkingSurface(
        double extent, int rows, bool hasVertical, bool hasHorizontal,
        double horizontalMaximum, double verticalMaximum, double viewportWidth, int rowCapacity)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            (bool HasVertical, bool HasHorizontal, int FullVisibleRows, double ViewportWidth) result = CustomTableView.CalculateScrollBarLayout(100d, 100d, extent, rows, 20d);
            Assert.AreEqual(hasVertical, result.HasVertical);
            Assert.AreEqual(hasHorizontal, result.HasHorizontal);
            Assert.AreEqual(viewportWidth, result.ViewportWidth);
            Assert.AreEqual(rowCapacity, result.FullVisibleRows);
            // 列幅は整数契約。小数の許容差境界は本番の範囲計算へ明示入力して分担する。
            if (extent != Math.Floor(extent))
            {
                Assert.AreEqual(horizontalMaximum, Math.Max(0d, extent - result.ViewportWidth), 0.00001d);
                Assert.AreEqual(verticalMaximum, CustomTableView.CalculateVerticalScrollMaximum(rows, result.FullVisibleRows));
                return;
            }
            CustomTableView table = CreateTable((int)extent, rows);
            try
            {
                Arrange(table, 100d, 120d);
                ScrollBar vertical = Bar(table, Orientation.Vertical);
                ScrollBar horizontal = Bar(table, Orientation.Horizontal);
                Assert.AreEqual(100d, table.SurfaceWidth);
                Assert.AreEqual(120d, table.SurfaceHeight);
                Assert.AreEqual(hasVertical ? Visibility.Visible : Visibility.Collapsed, vertical.Visibility);
                Assert.AreEqual(hasHorizontal ? Visibility.Visible : Visibility.Collapsed, horizontal.Visibility);
                Assert.AreEqual(horizontalMaximum, horizontal.Maximum, 0.00001d);
                Assert.AreEqual(verticalMaximum, vertical.Maximum);
                Assert.AreEqual(viewportWidth, horizontal.ViewportSize);
                Assert.AreEqual(rowCapacity, vertical.ViewportSize);
                Assert.AreEqual(rowCapacity, table.CalculateFullVisibleRowCapacity());
                Assert.AreEqual(5, table.CalculateDrawableRowCapacity());
                Assert.AreEqual(16d, vertical.Width);
                Assert.AreEqual(16d, horizontal.Height);
                if (hasHorizontal && hasVertical)
                {
                    Rect h = horizontal.TransformToAncestor(table).TransformBounds(new Rect(horizontal.RenderSize));
                    Rect v = vertical.TransformToAncestor(table).TransformBounds(new Rect(vertical.RenderSize));
                    Assert.IsTrue(h.Right <= v.Left);
                }
                // 必要な軸を消した後にも全面寸法を保ち、古い表示を次の判定へ持ち越さない。
                table.Columns = Columns(80);
                table.ItemsSource = Enumerable.Range(0, 4).Select(_ => new object()).ToArray();
                table.UpdateLayout();
                Assert.AreEqual(Visibility.Collapsed, vertical.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontal.Visibility);
                Assert.AreEqual(100d, table.SurfaceWidth);
                Assert.AreEqual(120d, table.SurfaceHeight);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>端まで移動後の列・行・寸法・行高変更と空・未配置・極小を実表で確認し、範囲外値と横バー容量を描画容量へ流用する誤りを検出します。</summary>
    [TestMethod]
    public void OverlayScrollBars_ClampChangesAndKeepDrawableCapacityIndependent()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(300, 20);
            try
            {
                ScrollBar vertical = Bar(table, Orientation.Vertical);
                ScrollBar horizontal = Bar(table, Orientation.Horizontal);
                Assert.IsTrue(double.IsFinite(vertical.Maximum) && double.IsFinite(horizontal.Maximum));
                Arrange(table, 100d, 126d);
                Assert.AreEqual(4, table.CalculateFullVisibleRowCapacity());
                Assert.AreEqual(6, table.CalculateDrawableRowCapacity());
                horizontal.Value = horizontal.Maximum;
                vertical.Value = vertical.Maximum;
                table.Columns = Columns(120);
                Assert.AreEqual(36d, horizontal.Value);
                table.ItemsSource = Enumerable.Range(0, 6).Select(_ => new object()).ToArray();
                Assert.AreEqual(2d, vertical.Value);
                table.RowHeight = 16d;
                Assert.AreEqual(1d, vertical.Value);
                Arrange(table, 400d, 200d);
                Assert.AreEqual(0d, horizontal.Value);
                Assert.AreEqual(Visibility.Collapsed, vertical.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontal.Visibility);
                table.Columns = Array.Empty<CustomTableColumn>();
                table.ItemsSource = Array.Empty<object>();
                Arrange(table, 0d, 0d);
                foreach (ScrollBar bar in new[] { vertical, horizontal })
                {
                    Assert.AreEqual(0d, bar.Value);
                    Assert.AreEqual(0d, bar.Maximum);
                    Assert.IsTrue(double.IsFinite(bar.ViewportSize));
                }
                table.Columns = Columns(120);
                table.ItemsSource = Enumerable.Range(0, 6).Select(_ => new object()).ToArray();
                Arrange(table, 8d, 8d);
                Assert.AreEqual(1, table.CalculateFullVisibleRowCapacity());
                Assert.AreEqual(5d, vertical.Maximum);
                Assert.AreEqual(120d, horizontal.Maximum);
            }
            finally { table.ItemsSource = null; }
        });
    }

    private static CustomTableView CreateTable(int extent, int rows)
        => new()
        {
            HeaderHeight = 20d,
            RowHeight = 20d,
            Columns = Columns(extent),
            ItemsSource = Enumerable.Range(0, rows).Select(_ => new object()).ToArray()
        };

    private static CustomTableColumn[] Columns(int extent)
        => [new CustomTableColumn("C", "C", new CustomTableColumnSettings.ColumnLayout { Width = extent }, 0,
            string.Empty, TextAlignment.Left, _ => string.Empty)];

    private static ScrollBar Bar(CustomTableView table, Orientation orientation)
        => table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == orientation);

    private static void Arrange(CustomTableView table, double width, double height)
    {
        table.Width = width;
        table.Height = height;
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
    }

}
