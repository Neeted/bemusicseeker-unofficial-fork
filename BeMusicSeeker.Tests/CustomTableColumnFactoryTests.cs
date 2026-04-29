using System.Linq;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableColumnFactoryTests
{
    [TestMethod]
    public void UseCustomTableView_DefaultValueIsFalse()
    {
        Assert.AreEqual("False", Settings.Default.Properties["UseCustomTableView"].DefaultValue);
    }

    [TestMethod]
    public void CreateMainColumns_UsesVisiblePhaseThreeColumnsForStandardView()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);

        string[] ids = CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "Title",
                "Artist",
                "Path",
                "Clear",
                "Rank",
                "Level",
                "ChartDifficulty",
                "ChartJudge",
                "PlaylistSymbols"
            },
            ids);
    }

    [TestMethod]
    public void CreateMainColumns_CanCreateAllPhaseThreeColumns()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        string[] ids = CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "Title",
                "Artist",
                "Url1",
                "Url2",
                "Warning",
                "Comment",
                "Memo",
                "Path",
                "PlaylistSymbols",
                "Clear",
                "Rank",
                "Level",
                "ChartDifficulty",
                "ChartJudge"
            },
            ids);
    }

    [TestMethod]
    public void CreateMainColumns_ReflectsVisibilityWidthAndDisplayIndex()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        settings.Title.Visibility = Visibility.Hidden;
        settings.ChartJudge.DisplayIndex = 0;
        settings.ChartJudge.Width = 77;

        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

        Assert.IsFalse(columns.Any(column => column.Id == "Title"));
        Assert.AreEqual("ChartJudge", columns[0].Id);
        Assert.AreEqual(77, columns[0].Width);
    }

    [TestMethod]
    public void CreateMainColumns_AssignsPhaseTwoSortMemberPaths()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        var paths = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id, column => column.SortMemberPath);

        Assert.AreEqual("Title", paths["Title"]);
        Assert.AreEqual("Artist", paths["Artist"]);
        Assert.AreEqual("path", paths["Path"]);
        Assert.IsNull(paths["Url1"]);
        Assert.IsNull(paths["Url2"]);
        Assert.AreEqual("DisplayWarning", paths["Warning"]);
        Assert.IsNull(paths["Comment"]);
        Assert.IsNull(paths["Memo"]);
        Assert.AreEqual("RefTablesSymbols", paths["PlaylistSymbols"]);
        Assert.AreEqual("ClearDisplayText", paths["Clear"]);
        Assert.AreEqual("RankDisplayText", paths["Rank"]);
        Assert.AreEqual("ChartLevelSortKey", paths["Level"]);
        Assert.AreEqual("ChartDifficultySortKey", paths["ChartDifficulty"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudge"]);
    }

    [TestMethod]
    public void CreateMainColumns_AssignsTooltipSelectors()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var row = new
        {
            DisplayWarning = "warning text",
            comment = "comment text",
            memo = "memo text",
            UrlToolTipText = "https://example.test/main",
            UrlDiffToolTipText = "diff\nhttps://example.test/diff",
            RefTablesNames = "table A"
        };

        Assert.AreEqual("warning text", columns["Warning"].GetTooltip(row));
        Assert.AreEqual("comment text", columns["Comment"].GetTooltip(row));
        Assert.AreEqual("memo text", columns["Memo"].GetTooltip(row));
        Assert.AreEqual("https://example.test/main", columns["Url1"].GetTooltip(row));
        Assert.AreEqual("diff\nhttps://example.test/diff", columns["Url2"].GetTooltip(row));
        Assert.AreEqual("table A", columns["PlaylistSymbols"].GetTooltip(row));
    }

    [TestMethod]
    public void CreateMainColumns_ConvertsUrlDownloadTextToIconGlyph()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        settings.Url1.Visibility = Visibility.Visible;
        CustomTableColumn urlColumn = CustomTableColumnFactory.CreateMainColumns(settings).Single(column => column.Id == "Url1");
        var row = new { UrlDownloadIconText = "download" };

        Assert.AreEqual("\uE14F", urlColumn.GetText(row));
    }

    [TestMethod]
    public void CustomTableColumnLayout_ResolvesHorizontalOffset()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Path.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Path.Width = 70;
        settings.Url1.Visibility = Visibility.Hidden;
        settings.Url2.Visibility = Visibility.Hidden;
        settings.Warning.Visibility = Visibility.Hidden;
        settings.Comment.Visibility = Visibility.Hidden;
        settings.Memo.Visibility = Visibility.Hidden;
        settings.PlaylistSymbols.Visibility = Visibility.Hidden;
        settings.Clear.Visibility = Visibility.Hidden;
        settings.Rank.Visibility = Visibility.Hidden;
        settings.Level.Visibility = Visibility.Hidden;
        settings.ChartDifficulty.Visibility = Visibility.Hidden;
        settings.ChartJudge.Visibility = Visibility.Hidden;
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

        bool resolved = CustomTableColumnLayout.TryResolveColumn(columns, tableX: 125, out CustomTableColumn column, out int columnIndex, out double columnX);

        Assert.IsTrue(resolved);
        Assert.AreEqual("Artist", column.Id);
        Assert.AreEqual(1, columnIndex);
        Assert.AreEqual(100d, columnX);
        Assert.AreEqual(3, CustomTableColumnLayout.CountColumnsWithinViewport(columns, horizontalOffset: 90, viewportWidth: 100));
    }

    [TestMethod]
    public void CustomTableColumnLayout_ResizeHitPrefersResizableColumnBoundary()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Url1.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Url1.Width = 40;
        settings.Path.Visibility = Visibility.Hidden;
        settings.Url2.Visibility = Visibility.Hidden;
        settings.Warning.Visibility = Visibility.Hidden;
        settings.Comment.Visibility = Visibility.Hidden;
        settings.Memo.Visibility = Visibility.Hidden;
        settings.PlaylistSymbols.Visibility = Visibility.Hidden;
        settings.Clear.Visibility = Visibility.Hidden;
        settings.Rank.Visibility = Visibility.Hidden;
        settings.Level.Visibility = Visibility.Hidden;
        settings.ChartDifficulty.Visibility = Visibility.Hidden;
        settings.ChartJudge.Visibility = Visibility.Hidden;
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

        bool titleResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 99, horizontalOffset: 0, viewportWidth: 200, margin: 4, out CustomTableColumn titleColumn, out _, out _);
        bool fixedUrlResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 220, horizontalOffset: 0, viewportWidth: 240, margin: 4, out CustomTableColumn urlColumn, out _, out _);

        Assert.IsTrue(titleResize);
        Assert.AreEqual("Title", titleColumn.Id);
        Assert.IsFalse(fixedUrlResize);
        Assert.IsNull(urlColumn);
    }
}
