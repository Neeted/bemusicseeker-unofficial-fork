using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListFilterServiceTests
{
    /// <summary>題名限定検索を通常一覧へ適用し、一致する譜面だけを返して別題名を混ぜません。</summary>
    [TestMethod]
    public void ApplyKeywordFilter_UsesGridKeywordSearchQuery()
    {
        List<LibraryChartRow> rows =
        [
            CreateRow("alpha.bms", "Alpha Song", 7),
            CreateRow("beta.bms", "Beta Song", 5)
        ];

        var filtered = RegularChartListFilterService
            .ApplyKeywordFilter(rows, "title:alpha")
            .ToList();

        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual("Alpha Song", filtered[0].Title);
    }

    /// <summary>7鍵盤指定では5鍵盤を除き、鍵盤不明の旧譜面を7鍵盤とともに元順で表示します。</summary>
    [TestMethod]
    public void ApplyModeFilter_IncludesUnknownAndSelectedModes()
    {
        LibraryChartRow unknown = CreateRow("unknown.bms", "Unknown", null);
        LibraryChartRow fiveKeys = CreateRow("five.bms", "Five", 5);
        LibraryChartRow sevenKeys = CreateRow("seven.bms", "Seven", 7);
        List<LibraryChartRow> rows = [unknown, fiveKeys, sevenKeys];

        var filtered = RegularChartListFilterService
            .ApplyModeFilter(rows, ChartModeFilter._7KEYS)
            .ToList();

        CollectionAssert.AreEqual(new[] { unknown, sevenKeys }, filtered);
    }

    /// <summary>5・14鍵盤の複合指定へ旧譜面の鍵盤不明値も含め、旧データをフィルターで消さず未選択鍵盤を混ぜません。</summary>
    [TestMethod]
    public void CreateModeFilterValueSet_PreservesLegacyUnknownMode()
    {
        HashSet<int?> values = RegularChartListFilterService.CreateModeFilterValueSet(
            ChartModeFilter._5KEYS | ChartModeFilter._14KEYS);

        CollectionAssert.AreEquivalent(new int?[] { null, 5, 14 }, values.ToArray());
    }

    /// <summary>元一覧が欠損したキーワード・鍵盤絞込みを例外で拒否し、空一覧成功へ置き換えて呼出し誤りを隠しません。</summary>
    [TestMethod]
    public void ApplyFilters_RejectNullSourceRows()
    {
        Assert.ThrowsException<ArgumentNullException>(() => RegularChartListFilterService.ApplyKeywordFilter(null, "alpha").ToList());
        Assert.ThrowsException<ArgumentNullException>(() => RegularChartListFilterService.ApplyModeFilter(null, ChartModeFilter._7KEYS).ToList());
    }

    private static LibraryChartRow CreateRow(string path, string title, int? mode)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, RawTitle = title, Folder = "folder", Mode = mode, Level = 1, LevelText = "1", Md5 = new string('a', 32) };
        return LibraryChartRow.FromChartFile(file);
    }

}
