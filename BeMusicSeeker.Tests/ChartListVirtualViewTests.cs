using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartListVirtualViewTests
{
    [TestMethod]
    public void Count_DoesNotRealizeRowsUntilIndexed()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(3, view.RowCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());

        LibraryChartRow row = (LibraryChartRow)view[0];

        Assert.AreEqual("Alpha", row.Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void SameIndex_ReturnsCachedRow()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        object first = view[1];
        object second = view[1];

        Assert.AreSame(first, second);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void IndexOf_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        object realized = view[1];

        Assert.AreEqual(1, view.IndexOf(realized));
        Assert.AreEqual(-1, view.IndexOf(new object()));
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    [TestMethod]
    public void SummaryConverter_UsesMetadataWithoutEnumeratingRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        BMSFilesViewToSummaryTextConverter converter = new BMSFilesViewToSummaryTextConverter();

        object text = converter.Convert(view, typeof(string), null, CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[3");
        Assert.AreEqual(-1, view.DistinctFolderCount);
        Assert.IsFalse(text.ToString().Contains("/"));
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());
    }

    [TestMethod]
    public void SummaryConverter_UsesSuppliedFolderCountWithoutEnumeratingRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount, distinctFolderCount: 2);
        BMSFilesViewToSummaryTextConverter converter = new BMSFilesViewToSummaryTextConverter();

        object text = converter.Convert(view, typeof(string), null, CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[3");
        StringAssert.Contains(text.ToString(), "/ 2");
        Assert.AreEqual(2, view.DistinctFolderCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());
    }

    [TestMethod]
    public void Constructor_DoesNotReadFolderCountFromSourceRows()
    {
        ThrowingFolderBmsFile file = new ThrowingFolderBmsFile();
        file.Apply(@"folder-a\alpha.bms", "Alpha");
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(new[] { file }, null);
        ChartListOrder order = ChartListOrder.CreateTitleAscending(sourceRows);

        ChartListVirtualView view = new ChartListVirtualView(sourceRows, order, row => LibraryChartRow.FromBmsFile(row.BmsFile));

        Assert.AreEqual(1, view.Count);
        Assert.AreEqual(-1, view.DistinctFolderCount);
    }

    [TestMethod]
    public void SummaryFormatter_OmitsUnknownFolderCount()
    {
        string unknown = MainWindowViewModel.FormatMainGridSummaryTextForTest(3, -1);
        string known = MainWindowViewModel.FormatMainGridSummaryTextForTest(3, 2);

        StringAssert.StartsWith(unknown, "[3");
        Assert.IsFalse(unknown.Contains("/"));
        StringAssert.Contains(known, "/ 2");
    }

    [TestMethod]
    public void DisposeRealizedRows_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        view.DisposeRealizedRows();

        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(2, getCreatedCount());
    }

    [TestMethod]
    public void TitleAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void TitleDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Descending);
    }

    [TestMethod]
    public void PathAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void PathDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Descending);
    }

    [TestMethod]
    public void PathDescendingOrder_KeepsTitleAscendingSecondaryKey()
    {
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile(@"folder-z\same.bms", "Gamma", "folder-z"),
            CreateFile(@"folder-z\same.bms", "Alpha", "folder-z"),
            CreateFile(@"folder-a\other.bms", "Beta", "folder-a")
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);

        bool created = ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder order);

        Assert.IsTrue(created);
        CollectionAssert.AreEqual(
            new[] { "Alpha", "Gamma", "Beta" },
            order.Indexes.Select(index => sourceRows[index].Title).ToArray());
    }

    [TestMethod]
    public void UnsupportedColumn_CannotCreateVirtualOrder()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);

        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.Folder), ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "mode", ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "Path", ListSortDirection.Ascending, out _));
    }

    [TestMethod]
    public void SortUpdatedOrderSwap_DoesNotRealizeRowsUntilIndexed()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, out ChartListOrder titleOrder));
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder pathOrder));
        int createdCount = 0;

        ChartListVirtualView titleView = new ChartListVirtualView(sourceRows, titleOrder, row =>
        {
            createdCount++;
            return LibraryChartRow.FromBmsFile(row.BmsFile);
        });
        ChartListVirtualView pathView = new ChartListVirtualView(sourceRows, pathOrder, row =>
        {
            createdCount++;
            return LibraryChartRow.FromBmsFile(row.BmsFile);
        });

        Assert.AreEqual(3, titleView.Count);
        Assert.AreEqual(3, pathView.Count);
        Assert.AreEqual(0, titleView.RealizedRowCount);
        Assert.AreEqual(0, pathView.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        _ = pathView[0];

        Assert.AreEqual(0, titleView.RealizedRowCount);
        Assert.AreEqual(1, pathView.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_AreTitleThenPathAscDesc()
    {
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest();

        CollectionAssert.AreEqual(
            new[]
            {
                new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.Title), ListSortDirection.Ascending),
                new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.Title), ListSortDirection.Descending),
                new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.path), ListSortDirection.Ascending),
                new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.path), ListSortDirection.Descending)
            },
            descriptors.ToArray());
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_DoNotRequireRowRealization()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);
        int createdCount = 0;

        foreach (VirtualNormalLibrarySortDescriptor descriptor in MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest())
        {
            Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, descriptor.ColumnName, descriptor.Direction, out ChartListOrder order));
            ChartListVirtualView view = new ChartListVirtualView(sourceRows, order, row =>
            {
                createdCount++;
                return LibraryChartRow.FromBmsFile(row.BmsFile);
            });

            Assert.AreEqual(sourceRows.Count, view.Count);
            Assert.AreEqual(0, view.RealizedRowCount);
        }
        Assert.AreEqual(0, createdCount);
    }

    [TestMethod]
    public void VirtualOrderPrewarmStaleCheck_RequiresBothGenerationsToMatch()
    {
        Assert.IsFalse(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 1, 2));
        Assert.IsTrue(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 3, 2));
        Assert.IsTrue(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 1, 3));
    }

    [TestMethod]
    public void NormalLibrarySortCacheKey_UsesSortKeyGenerationForOrderIdentity()
    {
        NormalLibrarySortCacheKey current = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey same = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedSortKeyGeneration = new NormalLibrarySortCacheKey(7, 12, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
    }

    [TestMethod]
    public void MainSummaryCacheKey_UsesGenerationsForIdentity()
    {
        MainViewSummaryCacheKey current = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        MainViewSummaryCacheKey same = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        MainViewSummaryCacheKey changedSortKeyGeneration = new MainViewSummaryCacheKey(7, 12, 3, includeBmsonRows: true, "normal_default");

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.IsFalse(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 11));
        Assert.IsTrue(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 12));
    }

    [TestMethod]
    public void LibraryChartSortMetrics_CarriesVirtualOrderTimingBreakdown()
    {
        LibraryChartSortMetrics metrics = new LibraryChartSortMetrics(
            3,
            nameof(LibraryChartRow.path),
            ListSortDirection.Descending,
            nameof(String),
            "virtual_path_order",
            "ordinal_ignore_case",
            42,
            sortReuse: true,
            sortCacheKey: nameof(LibraryChartRow.path),
            sortCacheGeneration: 5,
            sortCacheHit: true,
            orderCacheLookupMs: 1,
            orderBuildMs: 0);

        Assert.AreEqual(1, metrics.OrderCacheLookupMs);
        Assert.AreEqual(0, metrics.OrderBuildMs);
    }

    [TestMethod]
    public void SourceRow_ReadsCurrentBmsFileSortKeys()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.Apply(@"folder-b\old.bms", "Old", "folder-b");
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(new[] { file }, null);

        file.Apply(@"folder-a\new.bms", "New", "folder-a");

        Assert.AreEqual("New", sourceRows[0].Title);
        Assert.AreEqual(@"folder-a\new.bms", sourceRows[0].Path);
        Assert.AreEqual("folder-a", sourceRows[0].Folder);
    }

    private static ChartListVirtualView CreateView(out Func<int> getCreatedCount, int distinctFolderCount = -1)
    {
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile(@"folder-b\charlie.bms", "Charlie", "folder-b"),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a"),
            CreateFile(@"folder-a\bravo.bms", "Bravo", "folder-a")
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        ChartListOrder order = ChartListOrder.CreateTitleAscending(sourceRows);
        int localCreatedCount = 0;
        ChartListVirtualView view = new ChartListVirtualView(
            sourceRows,
            order,
            row =>
            {
                localCreatedCount++;
                return LibraryChartRow.FromBmsFile(row.BmsFile);
            },
            distinctFolderCount);
        getCreatedCount = () => localCreatedCount;
        return view;
    }

    private static void AssertVirtualOrderMatchesExistingSort(string columnName, ListSortDirection direction)
    {
        List<BMSFile> files = CreateSampleSortFiles();
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        bool created = ChartListOrder.TryCreate(sourceRows, columnName, direction, out ChartListOrder order);
        Assert.IsTrue(created);
        ChartListVirtualView view = new ChartListVirtualView(
            sourceRows,
            order,
            row => LibraryChartRow.FromBmsFile(row.BmsFile));
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(
            files.Select(LibraryChartRow.FromBmsFile),
            sortParameters,
            isPlaylistDetailView: false,
            useLegacySortForDataGrid: false,
            out _);

        CollectionAssert.AreEqual(
            legacySorted.Select(row => row.path).ToArray(),
            Enumerable.Range(0, view.Count).Select(index => ((LibraryChartRow)view[index]).path).ToArray());
    }

    private static List<BMSFile> CreateSampleSortFiles()
    {
        return new List<BMSFile>
        {
            CreateFile(@"folder-a\z_item10.bms", "item10", "folder-a"),
            CreateFile(@"folder-a\a_item2.bms", "item2", "folder-a"),
            CreateFile(@"folder-b\m_alpha.bms", "Alpha", "folder-b")
        };
    }

    private static BMSFile CreateFile(string path, string title, string folder)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.Apply(path, title, folder);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(string filePath, string fileTitle, string folderName)
        {
            path = filePath;
            title = fileTitle;
            folder = folderName;
            hash = "0123456789abcdef0123456789abcdef";
        }
    }

    private sealed class ThrowingFolderBmsFile : BMSFile
    {
        public override string Folder
        {
            get => throw new InvalidOperationException("Folder should not be read while constructing the virtual view.");
            set => throw new NotSupportedException();
        }

        internal void Apply(string filePath, string fileTitle)
        {
            path = filePath;
            title = fileTitle;
        }
    }
}
