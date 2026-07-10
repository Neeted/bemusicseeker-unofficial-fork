using System.Collections.Generic;
using System.Linq;
using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListFilterServiceTests
{
    [TestMethod]
    public void ApplyKeywordFilter_UsesGridKeywordSearchQuery()
    {
        List<LibraryChartRow> rows =
        [
            CreateRow("alpha.bms", "Alpha Song", 7),
            CreateRow("beta.bms", "Beta Song", 5)
        ];

        List<LibraryChartRow> filtered = RegularChartListFilterService
            .ApplyKeywordFilter(rows, "title:alpha")
            .ToList();

        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual("Alpha Song", filtered[0].Title);
    }

    [TestMethod]
    public void ApplyModeFilter_IncludesUnknownAndSelectedModes()
    {
        LibraryChartRow unknown = CreateRow("unknown.bms", "Unknown", null);
        LibraryChartRow fiveKeys = CreateRow("five.bms", "Five", 5);
        LibraryChartRow sevenKeys = CreateRow("seven.bms", "Seven", 7);
        List<LibraryChartRow> rows = [unknown, fiveKeys, sevenKeys];

        List<LibraryChartRow> filtered = RegularChartListFilterService
            .ApplyModeFilter(rows, RegularChartModeFilter.SevenKeys)
            .ToList();

        CollectionAssert.AreEqual(new[] { unknown, sevenKeys }, filtered);
    }

    [TestMethod]
    public void CreateModeFilterValueSet_PreservesLegacyUnknownMode()
    {
        HashSet<int?> values = RegularChartListFilterService.CreateModeFilterValueSet(
            RegularChartModeFilter.FiveKeys | RegularChartModeFilter.FourteenKeys);

        CollectionAssert.AreEquivalent(new int?[] { null, 5, 14 }, values.ToArray());
    }

    [TestMethod]
    public void ApplyFilters_RejectNullSourceRows()
    {
        Assert.ThrowsException<ArgumentNullException>(() => RegularChartListFilterService.ApplyKeywordFilter(null, "alpha").ToList());
        Assert.ThrowsException<ArgumentNullException>(() => RegularChartListFilterService.ApplyModeFilter(null, RegularChartModeFilter.SevenKeys).ToList());
    }

    private static LibraryChartRow CreateRow(string path, string title, int? mode)
    {
        var file = new TestableBmsFile();
        file.Apply(path, title, "folder", mode);
        return LibraryChartRow.FromBmsFile(file);
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(string filePath, string fileTitle, string folderName, int? modeValue)
        {
            path = filePath;
            title = fileTitle;
            folder = folderName;
            level = 1;
            mode = modeValue;
            hash = new string('a', 32);
        }
    }
}
