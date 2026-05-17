using System.Windows;
using System.Windows.Media;
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

    private static CustomTableColumn CreateColumn(string id, int width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(id, id, layout, 0, null, TextAlignment.Left, row => id);
    }
}
