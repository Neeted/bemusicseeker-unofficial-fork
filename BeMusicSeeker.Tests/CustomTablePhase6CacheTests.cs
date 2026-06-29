using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTablePhase6CacheTests
{
    [TestMethod]
    public void ColumnLayoutSnapshot_ResolvesVisibleColumnsAndResizeEdges()
    {
        CustomTableColumn[] columns =
        [
            CreateColumn("A", 50),
            CreateColumn("B", 80),
            CreateColumn("C", 30)
        ];

        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, horizontalOffset: 20d, viewportWidth: 100d);

        Assert.AreEqual(170d, snapshot.ExtentWidth);
        Assert.AreEqual(2, snapshot.VisibleColumnCount);
        Assert.IsTrue(snapshot.TryResolveColumn(60d, out CustomTableColumnLayoutEntry entry));
        Assert.AreEqual("B", entry.Column.Id);
        Assert.AreEqual(50d, entry.TableX);
        Assert.IsTrue(snapshot.TryResolveResizeColumn(30d, 4d, out CustomTableColumnLayoutEntry resizeEntry, out Rect resizeRect));
        Assert.AreEqual("A", resizeEntry.Column.Id);
        Assert.AreEqual(26d, resizeRect.X);
    }

    [TestMethod]
    public void ColumnLayoutSnapshot_MatchesOnlySameViewportAndColumns()
    {
        CustomTableColumn[] columns = [CreateColumn("A", 50)];
        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, 0d, 100d);

        Assert.IsTrue(snapshot.Matches(columns, 0d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 1d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 0d, 101d));
        Assert.IsFalse(snapshot.Matches([CreateColumn("A", 50)], 0d, 100d));
    }

    [TestMethod]
    public void CellValueCache_ReusesValuesUntilRowInvalidated()
    {
        int textCalls = 0;
        int foregroundCalls = 0;
        int backgroundCalls = 0;
        object row = new();
        bool undefined = true;
        var column = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: delegate
            {
                textCalls++;
                return "hello";
            },
            foregroundSelector: delegate
            {
                foregroundCalls++;
                return Brushes.Red;
            },
            backgroundSelector: delegate
            {
                backgroundCalls++;
                return undefined ? Brushes.Yellow : null;
            },
            textStyle: CustomTableTextStyle.Score);
        var cache = new CustomTableCellValueCache();

        CustomTableCellValue first = cache.GetOrCreate(row, column, 0, 0, out bool firstHit);
        CustomTableCellValue second = cache.GetOrCreate(row, column, 0, 0, out bool secondHit);
        undefined = false;
        cache.InvalidateRow(row);
        CustomTableCellValue third = cache.GetOrCreate(row, column, 0, 0, out bool thirdHit);

        Assert.IsFalse(firstHit);
        Assert.IsTrue(secondHit);
        Assert.IsFalse(thirdHit);
        Assert.AreEqual("hello", first.Text);
        Assert.AreEqual("hello", second.Text);
        Assert.AreEqual("hello", third.Text);
        Assert.AreSame(Brushes.Yellow, first.Background);
        Assert.AreSame(Brushes.Yellow, second.Background);
        Assert.IsNull(third.Background);
        Assert.AreSame(CustomTableTextStyle.Score, first.TextStyle);
        Assert.AreSame(CustomTableTextStyle.Score, third.TextStyle);
        Assert.AreEqual(2, textCalls);
        Assert.AreEqual(2, foregroundCalls);
        Assert.AreEqual(2, backgroundCalls);
    }

    [TestMethod]
    public void CellValueCache_TreatsGenerationChangesAsMisses()
    {
        int textCalls = 0;
        object row = new();
        var column = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: delegate
            {
                textCalls++;
                return textCalls.ToString();
            });
        var cache = new CustomTableCellValueCache();

        CustomTableCellValue first = cache.GetOrCreate(row, column, 0, 0, out bool firstHit);
        CustomTableCellValue second = cache.GetOrCreate(row, column, 1, 0, out bool secondHit);
        CustomTableCellValue third = cache.GetOrCreate(row, column, 1, 1, out bool thirdHit);

        Assert.IsFalse(firstHit);
        Assert.IsFalse(secondHit);
        Assert.IsFalse(thirdHit);
        Assert.AreEqual("1", first.Text);
        Assert.AreEqual("2", second.Text);
        Assert.AreEqual("3", third.Text);
    }

    [TestMethod]
    public void CustomTableView_RendersCellTextRunsAndPreservesRunsForSelection()
    {
        RunOnSta(delegate
        {
            object row = new();
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 210,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Delta",
                "DELTA",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "MMMM    MMMM",
                foregroundSelector: _ => Brushes.Black,
                textRunSelector: (_, _) =>
                [
                    new CustomTableTextRunStyle(0, 4, Brushes.Red),
                    new CustomTableTextRunStyle(8, 4, Brushes.Blue)
                ]);

            RenderTable(row, column, selectedIndex: -1, currentCell: false, out int normalRedPixels, out int normalBluePixels);
            RenderTable(row, column, selectedIndex: 0, currentCell: false, out int selectedRedPixels, out int selectedBluePixels);
            RenderTable(row, column, selectedIndex: -1, currentCell: true, out int currentCellRedPixels, out int currentCellBluePixels);

            Assert.IsTrue(normalRedPixels > 0, "Expected red pixels when text runs are active.");
            Assert.IsTrue(normalBluePixels > 0, "Expected blue pixels when text runs are active.");
            Assert.IsTrue(selectedRedPixels > 0, "Selected rows should preserve explicit text-run foregrounds.");
            Assert.IsTrue(selectedBluePixels > 0, "Selected rows should preserve explicit text-run foregrounds.");
            Assert.IsTrue(currentCellRedPixels > 0, "Current cells should preserve explicit text-run foregrounds.");
            Assert.IsTrue(currentCellBluePixels > 0, "Current cells should preserve explicit text-run foregrounds.");
        });
    }

    [TestMethod]
    public void CustomTableView_CreatesWrappedTooltipContentWhenWidthIsSpecified()
    {
        RunOnSta(delegate
        {
            object plainContent = CustomTableView.CreateCellToolTipContent("short tooltip", null);
            object wrappedContent = CustomTableView.CreateCellToolTipContent("long tooltip", 420d);

            Assert.AreEqual("short tooltip", plainContent);
            Assert.IsInstanceOfType(wrappedContent, typeof(TextBlock));
            var textBlock = (TextBlock)wrappedContent;
            Assert.AreEqual("long tooltip", textBlock.Text);
            Assert.AreEqual(420d, textBlock.Width);
            Assert.AreEqual(TextWrapping.Wrap, textBlock.TextWrapping);
        });
    }

    private static CustomTableColumn CreateColumn(string id, int width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(id, id, layout, 0, null, TextAlignment.Left, row => id);
    }

    private static void RenderTable(object row, CustomTableColumn column, int selectedIndex, bool currentCell, out int redPixels, out int bluePixels)
    {
        const int width = 240;
        const int height = 70;
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = 60d,
            Columns = [column],
            ItemsSource = new List<object> { row },
            SelectedIndex = selectedIndex
        };
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0, 0, width, height));
        table.UpdateLayout();
        if (currentCell)
        {
            CustomTableHitTestResult hit = table.HitTestTable(new Point(10d, 30d));
            typeof(CustomTableView)
                .GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(table, hit);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(table);
        CountDominantPixels(bitmap, out redPixels, out bluePixels);
    }

    private static void CountDominantPixels(BitmapSource bitmap, out int redPixels, out int bluePixels)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        redPixels = 0;
        bluePixels = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                if (red > 120 && green < 100 && blue < 100)
                {
                    redPixels++;
                }
                if (blue > 120 && green < 100 && red < 100)
                {
                    bluePixels++;
                }
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception exception = null!;
        var thread = new Thread((ThreadStart)delegate
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
