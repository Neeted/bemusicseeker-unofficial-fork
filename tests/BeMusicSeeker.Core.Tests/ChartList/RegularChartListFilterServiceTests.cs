using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;

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

    /// <summary>7種・null・未列挙値の入力をAll、各単独、複合と5解除で絞り、元順と詳細25/50との区別を確認します。</summary>
    [TestMethod]
    [DataRow(ChartModeFilter.All, new int[] { 5, 7, 9, 10, 14, 24, 48, 25, 50, 99 })]
    [DataRow(ChartModeFilter._5KEYS, new int[] { 5 })]
    [DataRow(ChartModeFilter._7KEYS, new int[] { 7 })]
    [DataRow(ChartModeFilter._9KEYS, new int[] { 9 })]
    [DataRow(ChartModeFilter._10KEYS, new int[] { 10 })]
    [DataRow(ChartModeFilter._14KEYS, new int[] { 14 })]
    [DataRow(ChartModeFilter._24KEYS, new int[] { 24 })]
    [DataRow(ChartModeFilter._48KEYS, new int[] { 48 })]
    [DataRow(ChartModeFilter._24KEYS | ChartModeFilter._48KEYS, new int[] { 24, 48 })]
    [DataRow(ChartModeFilter.All & ~ChartModeFilter._5KEYS, new int[] { 7, 9, 10, 14, 24, 48 })]
    public void ApplyModeFilter_IncludesUnknownAndSelectedModes(ChartModeFilter filter, int[] expected)
    {
        int?[] modes = [null, 5, 7, 9, 10, 14, 24, 48, 25, 50, 99];
        LibraryChartRow[] rows = modes.Select((mode, index) => CreateRow(index + ".bms", "Mode " + mode, mode)).ToArray();
        LibraryChartRow[] filtered = RegularChartListFilterService.ApplyModeFilter(rows, filter).ToArray();
        CollectionAssert.AreEqual(new int?[] { null }.Concat(expected.Select(mode => (int?)mode)).ToArray(), filtered.Select(row => row.mode).ToArray());
        if (filter == ChartModeFilter.All) { Assert.AreSame(rows, RegularChartListFilterService.ApplyModeFilter(rows, filter)); }
    }

    /// <summary>差分用の集合も5・14・24・48の複合選択とnullを保ち、未選択モードを混ぜません。</summary>
    [TestMethod]
    public void CreateModeFilterValueSet_PreservesLegacyUnknownMode()
    {
        HashSet<int?> values = RegularChartListFilterService.CreateModeFilterValueSet(
            ChartModeFilter._5KEYS | ChartModeFilter._14KEYS | ChartModeFilter._24KEYS | ChartModeFilter._48KEYS);
        CollectionAssert.AreEquivalent(new int?[] { null, 5, 14, 24, 48 }, values.ToArray());
    }

    /// <summary>元一覧が欠損したキーワード・鍵盤絞込みを例外で拒否し、空一覧成功へ置き換えて呼出し誤りを隠しません。</summary>
    [TestMethod]
    public void ApplyFilters_RejectNullSourceRows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => RegularChartListFilterService.ApplyKeywordFilter(null, "alpha").ToList());
        Assert.ThrowsExactly<ArgumentNullException>(() => RegularChartListFilterService.ApplyModeFilter(null, ChartModeFilter._7KEYS).ToList());
    }

    private static LibraryChartRow CreateRow(string path, string title, int? mode)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, RawTitle = title, Folder = "folder", Mode = mode, Level = 1, LevelText = "1", Md5 = new string('a', 32) };
        return LibraryChartRow.FromChartFile(file);
    }

}
