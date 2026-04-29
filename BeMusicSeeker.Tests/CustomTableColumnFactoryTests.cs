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
    public void CreateMainColumns_UsesPhaseOneColumnsOnly()
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
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);

        var paths = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id, column => column.SortMemberPath);

        Assert.AreEqual("Title", paths["Title"]);
        Assert.AreEqual("Artist", paths["Artist"]);
        Assert.AreEqual("path", paths["Path"]);
        Assert.AreEqual("ClearDisplayText", paths["Clear"]);
        Assert.AreEqual("RankDisplayText", paths["Rank"]);
        Assert.AreEqual("ChartLevelSortKey", paths["Level"]);
        Assert.AreEqual("ChartDifficultySortKey", paths["ChartDifficulty"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudge"]);
    }
}
