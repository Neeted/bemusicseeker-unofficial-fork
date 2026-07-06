using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListSortCoordinatorTests
{
    [TestMethod]
    public void ApplySort_BypassReturnsFreshRowsAndPreservesFolderSnapshotState()
    {
        List<LibraryChartRow> sourceRows = [];
        List<LibraryChartRow> previousFolderSource = [];
        List<LibraryChartRow> previousFolderResult = [];
        RegularChartListRefreshRequest request = CreateRequest(
            MainViewUpdateMode.UpdatedNone,
            MainViewUpdateMode.TreeViewFilterNotChanged);
        RegularChartListStageState stage = CreateStage(sourceRows);
        var context = new RegularChartListSortContext
        {
            FolderSortSourceSnapshot = previousFolderSource,
            FolderSortResultSnapshot = previousFolderResult,
            FolderSortColumnName = nameof(LibraryChartRow.Title),
            FolderSortDirection = ListSortDirection.Descending
        };

        RegularChartListSortResult result = RegularChartListSortCoordinator.ApplySort(request, stage, context);

        Assert.AreNotSame(sourceRows, result.RowsView);
        Assert.AreEqual(0, result.RowsView.Count);
        Assert.IsFalse(result.SortReuse);
        Assert.AreEqual("bypass", result.SortProfile);
        Assert.AreSame(previousFolderSource, result.FolderSortSourceSnapshot);
        Assert.AreSame(previousFolderResult, result.FolderSortResultSnapshot);
        Assert.AreEqual(nameof(LibraryChartRow.Title), result.FolderSortColumnName);
        Assert.AreEqual(ListSortDirection.Descending, result.FolderSortDirection);
    }

    [TestMethod]
    public void ApplySort_ReusesFolderSortSnapshotForSameReferenceSequence()
    {
        List<LibraryChartRow> sourceRows = [];
        List<LibraryChartRow> sortedSnapshot = [];
        RegularChartListRefreshRequest request = CreateRequest(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.DuplicateFilterSelected);
        RegularChartListStageState stage = CreateStage(sourceRows);
        var context = new RegularChartListSortContext
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            FolderSortSourceSnapshot = sourceRows,
            FolderSortResultSnapshot = sortedSnapshot,
            FolderSortColumnName = nameof(LibraryChartRow.Title),
            FolderSortDirection = ListSortDirection.Ascending,
            CreateSortCacheMetrics = CreateSortCacheMetrics
        };

        RegularChartListSortResult result = RegularChartListSortCoordinator.ApplySort(request, stage, context);

        Assert.AreSame(sortedSnapshot, result.RowsView);
        Assert.IsTrue(result.SortReuse);
        Assert.AreEqual("reuse", result.SortProfile);
        Assert.AreSame(sourceRows, result.FolderSortSourceSnapshot);
        Assert.AreSame(sortedSnapshot, result.FolderSortResultSnapshot);
    }

    [TestMethod]
    public void ApplySort_NormalCacheHitPreservesExistingFolderResultSnapshot()
    {
        List<LibraryChartRow> sourceRows = [];
        List<LibraryChartRow> cachedRows = [];
        List<LibraryChartRow> previousFolderResult = [];
        var cacheKey = new NormalLibrarySortCacheKey(1, 2, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, cachedRows.Count);
        bool logged = false;
        RegularChartListRefreshRequest request = CreateRequest(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.DuplicateFilterSelected);
        RegularChartListStageState stage = CreateStage(sourceRows);
        var context = new RegularChartListSortContext
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            FolderSortResultSnapshot = previousFolderResult,
            CreateSortCacheMetrics = CreateSortCacheMetrics,
            LogSortDetail = _ => logged = true,
            TryGetNormalLibrarySortCache = (bool isEligible, string columnName, ListSortDirection direction, int rowCount, out List<LibraryChartRow> rows, out NormalLibrarySortCacheKey key) =>
            {
                rows = cachedRows;
                key = cacheKey;
                return isEligible;
            }
        };

        RegularChartListSortResult result = RegularChartListSortCoordinator.ApplySort(request, stage, context);

        Assert.AreSame(cachedRows, result.RowsView);
        Assert.IsTrue(result.SortReuse);
        Assert.IsTrue(logged);
        Assert.AreSame(sourceRows, result.FolderSortSourceSnapshot);
        Assert.AreSame(previousFolderResult, result.FolderSortResultSnapshot);
        Assert.AreEqual(nameof(LibraryChartRow.Title), result.FolderSortColumnName);
        Assert.AreEqual(ListSortDirection.Ascending, result.FolderSortDirection);
    }

    [TestMethod]
    public void ApplySort_StoresFullNormalLibrarySortCacheAfterSorting()
    {
        List<LibraryChartRow> sourceRows = [];
        NormalLibrarySortCacheKey storedKey = default;
        List<LibraryChartRow> storedRows = [];
        RegularChartListRefreshRequest request = CreateRequest(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.DuplicateFilterSelected);
        RegularChartListStageState stage = CreateStage(sourceRows);
        var context = new RegularChartListSortContext
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            CreateSortCacheMetrics = CreateSortCacheMetrics,
            TryGetNormalLibrarySortCache = (bool isEligible, string columnName, ListSortDirection direction, int rowCount, out List<LibraryChartRow> rows, out NormalLibrarySortCacheKey key) =>
            {
                rows = [];
                key = default;
                return false;
            },
            TryNormalizeSortCacheColumn = (string columnName, out string normalizedColumnName) =>
            {
                normalizedColumnName = columnName;
                return true;
            },
            CreateSortCacheKey = (string normalizedColumnName, ListSortDirection direction, int rowCount) =>
                new NormalLibrarySortCacheKey(3, 4, normalizedColumnName, direction, rowCount),
            StoreSortCache = (key, rows) =>
            {
                storedKey = key;
                storedRows = rows;
            }
        };

        RegularChartListSortResult result = RegularChartListSortCoordinator.ApplySort(request, stage, context);

        Assert.IsFalse(result.SortReuse);
        Assert.AreEqual("library_chart_string_fast_ordinal_ignore_case", result.SortProfile);
        Assert.IsNotNull(storedRows);
        Assert.AreEqual(nameof(LibraryChartRow.Title), storedKey.ColumnName);
        Assert.AreEqual(0, storedKey.RowCount);
    }

    private static RegularChartListRefreshRequest CreateRequest(MainViewUpdateMode mode, MainViewUpdateMode requestedMode)
    {
        return new RegularChartListRefreshRequest(
            mode,
            requestedMode,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            MainWindowViewModel.ModeFilterType.All,
            sortParameters: null);
    }

    private static RegularChartListStageState CreateStage(List<LibraryChartRow> rows)
    {
        return new RegularChartListStageState
        {
            FolderRows = rows,
            KeywordRows = rows,
            ModeRows = rows
        };
    }

    private static LibraryChartSortMetrics CreateSortCacheMetrics(NormalLibrarySortCacheKey cacheKey, long sortMs, bool cacheHit)
    {
        return new LibraryChartSortMetrics(
            cacheKey.RowCount,
            cacheKey.ColumnName,
            cacheKey.Direction,
            "String",
            "library_chart_string_fast_ordinal_ignore_case",
            "ordinal_ignore_case",
            sortMs,
            sortReuse: cacheHit,
            sortCacheKey: cacheKey.ColumnName,
            sortCacheGeneration: 0,
            sortCacheHit: cacheHit);
    }
}
