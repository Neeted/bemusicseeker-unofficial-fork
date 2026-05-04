using System;
using System.IO;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowContextMenuResourceTests
{
    [TestMethod]
    public void DeleteContextMenuItems_UseSpecificDeleteResourceKeys()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteEntry\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove_playlist_entry, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteFile\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_file, Mode=OneWay"));
    }

    [TestMethod]
    public void ChartInfoParseFailureContextMenu_UsesDedicatedResourceAndVisibilityPolicy()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemRemoveChartInfoParseFailure\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_info_parse_failure_record, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"tableContextMenuItemRemoveChartInfoParseFailureClick\""));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Remove_chart_info_parse_failure_record));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Msg_remove_chart_info_parse_failure_record));
        Assert.IsTrue(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, new[] { new string('a', 32) }));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(false, new[] { new string('a', 32) }));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, new[] { " " }));
    }

    [TestMethod]
    public void LibraryFolderContextMenus_ExposeLightReloadAndFullReinitialize()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReloadFileDiff\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReinitializeLibrary\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Path=Resources.Reinitialize_library, Mode=OneWay"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Reinitialize_library));
    }

    [TestMethod]
    public void SidebarLayout_UsesUnifiedSplitterStyleAndMinimumWidth()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "x:Key=\"SidebarSplitterStyle\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Style=\"{StaticResource SidebarSplitterStyle}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridSplitter\" Style=\"{StaticResource SidebarSplitterStyle}\" ResizeDirection=\"Columns\" ResizeBehavior=\"CurrentAndNext\" Margin=\"0\" Grid.RowSpan=\"2\" Width=\"5\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridSplitterTree\" Style=\"{StaticResource SidebarSplitterStyle}\" Grid.Row=\"1\" Height=\"5\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridColumn0\" MinWidth=\"160\" Width=\"{Binding TreeViewWidth, Source={x:Static prop:Settings.Default}, Mode=OneTime}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridTreePane\" Margin=\"0,0,5,0\" Grid.Row=\"1\" Grid.RowSpan=\"2\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "<Border BorderThickness=\"0\" Grid.Row=\"1\" Grid.ColumnSpan=\"1\" Grid.Column=\"1\" Background=\"#FFF0F0F0\">"));
        Assert.AreEqual(0, CountOccurrences(xaml, "BorderBrush=\"#FF828790\" BorderThickness=\"1,0,0,0\" Grid.Row=\"1\" Grid.ColumnSpan=\"1\" Grid.Column=\"1\""));
    }

    [TestMethod]
    public void SidebarTreeViewWidthPolicy_NormalizesInvalidPersistedValues()
    {
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NaN));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.PositiveInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NegativeInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(-1d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(0d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth - 1d));
        Assert.AreEqual(Settings.MinTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth));
        Assert.AreEqual(250d, Settings.NormalizeTreeViewWidth(250d));
        Assert.AreEqual(320d, Settings.NormalizeTreeViewWidth(320d));
    }

    [TestMethod]
    public void SidebarTreeViewWidthSetting_PropertyNormalizesBackingValue()
    {
        Settings settings = new Settings();

        settings["TreeViewWidth"] = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, settings.TreeViewWidth);

        settings.TreeViewWidth = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, (double)settings["TreeViewWidth"]);

        settings.TreeViewWidth = Settings.MinTreeViewWidth;
        Assert.AreEqual(Settings.MinTreeViewWidth, (double)settings["TreeViewWidth"]);
    }

    [TestMethod]
    public void SidebarTreeViewWidthSavePolicy_UsesMeasuredColumnWhenValid()
    {
        Assert.AreEqual(240d, MainWindow.ResolveTreeViewWidthForSave(240d, 250d, 300d));
        Assert.AreEqual(260d, MainWindow.ResolveTreeViewWidthForSave(double.NaN, 260d, 300d));
        Assert.AreEqual(320d, MainWindow.ResolveTreeViewWidthForSave(0d, double.NaN, 320d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, MainWindow.ResolveTreeViewWidthForSave(0d, double.NaN, 0d));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker-decomp.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
