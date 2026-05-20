using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
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

        var row = (LibraryChartRow)view[0];

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
        var converter = new ChartRowsViewToSummaryTextConverter();

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
        var converter = new ChartRowsViewToSummaryTextConverter();

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
        var file = new ThrowingFolderBmsFile();
        file.Apply(@"folder-a\alpha.bms", "Alpha");
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows([file], null);
        var order = ChartListOrder.CreateTitleAscending(sourceRows);

        var view = new ChartListVirtualView(sourceRows, order, row => LibraryChartRow.FromBmsFile(row.BmsFile));

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
    public void FolderAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void FolderDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Descending);
    }

    [TestMethod]
    public void AdditionalSupportedOrders_MatchExistingDefaultLibraryChartRowSort()
    {
        string[] columns =
        [
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.WarningDigestText),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.WAVHealth),
            nameof(LibraryChartRow.BGAHealth),
            nameof(LibraryChartRow.MovieHealth),
            nameof(LibraryChartRow.encoding),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols)
        ];

        foreach (string column in columns)
        {
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Ascending);
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Descending);
        }
    }

    [TestMethod]
    public void PathDescendingOrder_KeepsTitleAscendingSecondaryKey()
    {
        List<BMSFile> files =
        [
            CreateFile(@"folder-z\same.bms", "Gamma", "folder-z"),
            CreateFile(@"folder-z\same.bms", "Alpha", "folder-z"),
            CreateFile(@"folder-a\other.bms", "Beta", "folder-a")
        ];
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

        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "Path", ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "EntryLevelSortKey", ListSortDirection.Ascending, out _));
    }

    [TestMethod]
    public void SortUpdatedOrderSwap_DoesNotRealizeRowsUntilIndexed()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, out ChartListOrder titleOrder));
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder pathOrder));
        int createdCount = 0;

        var titleView = new ChartListVirtualView(sourceRows, titleOrder, row =>
        {
            createdCount++;
            return LibraryChartRow.FromBmsFile(row.BmsFile);
        });
        var pathView = new ChartListVirtualView(sourceRows, pathOrder, row =>
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
    public void NormalLibraryFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<BMSFile> files =
        [
            CreateFile(@"FOLDER-Z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<LR2SongDBExtended.bmson_song> bmsons =
        [
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-z\echo.bmson",
                folder = "folder-z",
                title = "Echo",
                artist = "Target Artist",
                mode_hint = "beat-7k",
                level = 7,
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = "6666666666666666666666666666666666666666666666666666666666666666"
            }
        ];
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, bmsons);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        int[] existingOrderIndexes = [.. fullOrder.Indexes.Reverse()];
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");
        Func<ChartListSourceRow, bool> folderFilter = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            "folder-z",
            out string folderFilterIdentity);

        int[] filteredIndexes = MainWindowViewModel.ApplyVirtualNormalLibraryFiltersForTest(
            sourceRows,
            existingOrderIndexes,
            folderFilter,
            keywordQuery,
            MainWindowViewModel.ModeFilterType._5KEYS | MainWindowViewModel.ModeFilterType._7KEYS,
            out int folderCount,
            out int keywordCount,
            out int modeCount);
        ChartListOrder filteredOrder = fullOrder.WithIndexes(filteredIndexes);
        int createdCount = 0;
        var view = new ChartListVirtualView(sourceRows, filteredOrder, row =>
        {
            createdCount++;
            return LibraryChartRow.FromBmsFile(row.BmsFile);
        });

        Assert.AreEqual("directory:folder-z" + Path.DirectorySeparatorChar, folderFilterIdentity);
        CollectionAssert.AreEqual(new[] { "Bravo", "Delta", "Echo" }, filteredIndexes.Select(index => sourceRows[index].Title).ToArray());
        Assert.AreEqual(3, folderCount);
        Assert.AreEqual(3, keywordCount);
        Assert.AreEqual(3, modeCount);
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        Assert.AreEqual("Bravo", ((LibraryChartRow)view[0]).Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    [TestMethod]
    public void VirtualNormalLibraryFilterIdentity_UsesStableFolderFilterIdentity()
    {
        _ = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            @"D:\BMS\1 EVENT",
            out string folderIdentity);
        _ = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            @"D:\BMS\1 EVENT\",
            out string sameFolderIdentity);

        string identity = MainWindowViewModel.CreateVirtualNormalLibraryFilterIdentityForTest(folderIdentity, string.Empty, MainWindowViewModel.ModeFilterType.All, 1, 1);
        string sameIdentity = MainWindowViewModel.CreateVirtualNormalLibraryFilterIdentityForTest(sameFolderIdentity, string.Empty, MainWindowViewModel.ModeFilterType.All, 9, 9);

        Assert.AreEqual(folderIdentity, sameFolderIdentity);
        Assert.AreEqual(identity, sameIdentity);
        Assert.AreNotEqual("normal_default", identity);
    }

    [TestMethod]
    public void VirtualChartSubsetFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<BMSFile> files =
        [
            CreateFile(@"folder-z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");

        int[] filteredIndexes = MainWindowViewModel.ApplyVirtualChartSubsetFiltersForTest(
            sourceRows,
            fullOrder.Indexes,
            keywordQuery,
            MainWindowViewModel.ModeFilterType._5KEYS | MainWindowViewModel.ModeFilterType._7KEYS,
            out int keywordCount,
            out int modeCount);
        ChartListOrder filteredOrder = fullOrder.WithIndexes(filteredIndexes);
        int createdCount = 0;
        var view = new ChartListVirtualView(sourceRows, filteredOrder, row =>
        {
            createdCount++;
            return LibraryChartRow.FromBmsFile(row.BmsFile);
        });

        CollectionAssert.AreEqual(new[] { "Delta", "Bravo", "Alpha" }, filteredIndexes.Select(index => sourceRows[index].Title).ToArray());
        Assert.AreEqual(3, keywordCount);
        Assert.AreEqual(3, modeCount);
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        Assert.AreEqual("Delta", ((LibraryChartRow)view[0]).Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    [TestMethod]
    public void VirtualChartSubsetSortCacheKey_UsesSubsetSignatureAndDependencyGeneration()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);
        List<ChartListSourceRow> reorderedRows = [.. sourceRows.AsEnumerable().Reverse()];
        long signature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(sourceRows);
        long sameSignature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null));
        long reorderedSignature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(reorderedRows);
        int treeMode = (int)MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected;

        var current = new VirtualChartSubsetSortCacheKey(
            7,
            11,
            1,
            2,
            3,
            treeMode,
            "file_missing",
            signature,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            sourceRows.Count);
        var same = new VirtualChartSubsetSortCacheKey(
            7,
            11,
            1,
            2,
            3,
            treeMode,
            "file_missing",
            sameSignature,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            sourceRows.Count);

        Assert.AreEqual(signature, sameSignature);
        Assert.AreNotEqual(signature, reorderedSignature);
        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(8, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 12, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 2, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 3, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 4, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, (int)MainWindowViewModel.viewUpdateMode.DuplicateFilterSelected, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "duplicate_all", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", reorderedSignature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Descending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count + 1));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_AreRegistryOrderAscDesc()
    {
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest();

        CollectionAssert.AreEqual(
            CreateExpectedDefaultPrewarmDescriptors(),
            descriptors.ToArray());
    }

    [TestMethod]
    public void VirtualSortRegistryMetadata_DescribesSupportedColumnsAndDependencies()
    {
        ChartListOrderColumnMetadata[] metadata = [.. ChartListOrder.GetVirtualSortColumnMetadata()];

        CollectionAssert.AreEqual(
            CreateExpectedVirtualSortColumnNames(),
            metadata.Select(column => column.NormalizedColumnName).ToArray());
        foreach (ChartListOrderColumnMetadata column in metadata)
        {
            Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(column.NormalizedColumnName, out ChartListOrderColumnMetadata resolved));
            Assert.AreEqual(column.Dependency, resolved.Dependency);
        }

        AssertRegistryDependency(metadata, MainViewDataDependency.IdentitySortKey, nameof(LibraryChartRow.Title));
        AssertRegistryDependency(metadata, MainViewDataDependency.Score, nameof(LibraryChartRow.rateDouble));
        AssertRegistryDependency(metadata, MainViewDataDependency.ChartInfo, nameof(LibraryChartRow.ChartTotalSortKey));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.WAVHealth));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.encoding));
        AssertRegistryDependency(metadata, MainViewDataDependency.Warning, nameof(LibraryChartRow.WarningDigestText));
        AssertRegistryPrewarmPriority(metadata, 1, nameof(LibraryChartRow.Title));
        AssertRegistryPrewarmPriority(metadata, 1, nameof(LibraryChartRow.Folder));
        AssertRegistryPrewarmPriority(metadata, 2, nameof(LibraryChartRow.rateDouble));
        AssertRegistryPrewarmPriority(metadata, 2, nameof(LibraryChartRow.minbp));
        AssertRegistryPrewarmPriority(metadata, 3, nameof(LibraryChartRow.maxcombo));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.WAVHealth));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.encoding));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.WarningDigestText));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.level));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_FollowVisibleColumnsAndPriorities()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        string[] columns = [.. MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)];

        CollectionAssert.Contains(columns, nameof(LibraryChartRow.Title));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.rateDouble));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartTotalSortKey));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartFeatureSortKey));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.hash));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.score));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.maxcombo));

        settings.Combo.Visibility = Visibility.Visible;
        columns = [.. MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)];

        CollectionAssert.Contains(columns, nameof(LibraryChartRow.maxcombo));
    }

    [TestMethod]
    public void VirtualOrderPrewarmDegree_IsBoundedByProcessorAndCap()
    {
        Assert.AreEqual(1, MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(0));
        Assert.AreEqual(1, MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(1));

        int degree = MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(128);

        Assert.IsTrue(degree >= 1);
        Assert.IsTrue(degree <= 4);
        Assert.IsTrue(degree <= Math.Max(1, Environment.ProcessorCount - 1));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_DoNotRequireRowRealization()
    {
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), null);
        int createdCount = 0;

        foreach (VirtualNormalLibrarySortDescriptor descriptor in MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest())
        {
            Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, descriptor.ColumnName, descriptor.Direction, out ChartListOrder order));
            var view = new ChartListVirtualView(sourceRows, order, row =>
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
    public void VirtualChartSubsetTreeModes_AreLimitedToChartCollections()
    {
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.DuplicateFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.GarbledFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.GarbleFixedFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.NewlyInstalledFolderSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected));

        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.PlaylistFilterSelected));
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_DoesNotMaterializeAdapterlessBmsonEntry()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));

        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsNotNull(row);
        Assert.AreEqual(bmson.path, row.path);
        Assert.AreEqual("BmsonTitle Subtitle", row.Title);
        Assert.IsNull(entry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_TreatsBmsonEntryAsBmsonRow()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));

        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsNotNull(row);
        Assert.IsNull(row.BmsFile);
        Assert.AreSame(bmson, row.BmsonSong);
        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreSame(entry, row.PackageEntry);
        Assert.AreEqual(bmson.path, row.path);
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_ReflectsUpdatedAdapterlessBmsonEntryState()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);
        var result = new InstallEstimationResult
        {
            Confidence = InstallEstimationConfidence.Low,
            HasViableDestination = true,
            LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates
        };
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\A",
            RepresentativeTitle = "Candidate A",
            RepresentativeArtist = "Artist A"
        });
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\B",
            RepresentativeTitle = "Candidate B",
            RepresentativeArtist = "Artist B"
        });
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\A");
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\B");

        entry.ApplyInstallEstimationResult(result);

        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, row.instl_dst);
        Assert.AreEqual("Candidate A", row.InstallDestinationTitle);
        Assert.AreEqual("Artist A", row.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { @"C:\Candidate\A", @"C:\Candidate\B" }, row.Chart.InstallDestinationSuggestions.ToArray());
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void PackageChartSourceSnapshot_SplitsAdapterlessBmsonEntryWithoutMaterializing()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChartAdapter(bms),
            adapterlessBmsonEntry
        ]);

        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);

        Assert.AreSame(bms, snapshot.BmsFiles.Single());
        Assert.AreSame(bmson, snapshot.BmsonSongs.Single());
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void PackageChartSourceSnapshot_TreatsBmsonEntryAsBmsonSource()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChartAdapter(bms),
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson))
        ]);

        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);

        Assert.AreSame(bms, snapshot.BmsFiles.Single());
        Assert.AreSame(bmson, snapshot.BmsonSongs.Single());
    }

    [TestMethod]
    public void PackageChartSourceRows_DoNotMaterializeAdapterlessBmsonDuringVirtualSort()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildStandardLibraryRows(
            snapshot.BmsFiles,
            snapshot.BmsonSongs);

        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.instl_dst), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WAVHealth), ListSortDirection.Ascending, out _));

        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreSame(bmson, rows.Single().Chart.BmsonSong);
    }

    [TestMethod]
    public void PackagePlaybackTargetSnapshot_UsesOnlyExistingBmsAdapters()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(CreateBmsonSong()));
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChartAdapter(bms),
            adapterlessBmsonEntry
        ]);

        List<BMSFile> targets = MainWindowViewModel.CreatePackagePlaybackTargetSnapshot([package]);

        Assert.AreSame(bms, targets.Single());
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingPackages_ClearsAdapterlessBmsonEntryWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(CreateBmsonSong()),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

        viewModel.ClearInstallDestinationForPendingPackages([package]);

        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingCharts_ClearsAdapterlessBmsonPackageEntryWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var selectedChart = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([selectedChart]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_DoesNotResolveLooseBmsonCompatibilityAdapterWhenStandaloneTargetSharesPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var packageTarget = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);
            ChartFile standaloneChart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Standalone",
                "Standalone",
                "Artist",
                []);
            var standaloneTarget = new ChartOperationTarget(
                standaloneChart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination);

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([packageTarget, standaloneTarget]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
            Assert.AreEqual(@"C:\Installed\Standalone", standaloneChart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PackageEntryRowsCarryPackageEntryIntoChartOperationTarget()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile adapter = CreateFile(
            @"C:\Pkg\chart.bms",
            "BMS",
            "Pkg",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        PackageChartEntry entry = PackageChartEntry.FromChartAdapter(adapter);
        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));
        Assert.AreSame(entry, target.PackageEntry);
        Assert.AreSame(entry, target.ToPackageChartEntry());
    }

    [TestMethod]
    public void SearchInstallDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            viewModel.SearchInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            viewModel.SearchMergeDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_ResolvesReplacedPackageEntryByChartIdentity()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry staleEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Old",
                "Old",
                "Artist",
                []));
        PackageChartEntry currentEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Current",
                "Current",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([currentEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                staleEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                staleEntry);

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(currentEntry.GetBmsOwnerForTest());
            Assert.AreEqual(string.Empty, currentEntry.Chart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void VirtualNormalLibraryRequestModes_CoverRootAndFullScanTrees()
    {
        MainWindowViewModel.viewUpdateMode[] treeModes =
        [
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected
        ];
        MainWindowViewModel.viewUpdateMode[] refreshModes =
        [
            MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged,
            MainWindowViewModel.viewUpdateMode.KeywordFilterUpdated,
            MainWindowViewModel.viewUpdateMode.ModeFilterUpdated,
            MainWindowViewModel.viewUpdateMode.SortUpdated
        ];

        MainWindowViewModel.viewUpdateMode[] allModes = [.. Enum.GetValues(typeof(MainWindowViewModel.viewUpdateMode)).Cast<MainWindowViewModel.viewUpdateMode>()];
        foreach (MainWindowViewModel.viewUpdateMode treeMode in allModes)
        {
            bool expectedTreeSupport = treeModes.Contains(treeMode);
            Assert.AreEqual(
                expectedTreeSupport,
                MainWindowViewModel.IsVirtualNormalLibraryTreeModeSupportedForTest((int)treeMode),
                treeMode + " tree support");
            foreach (MainWindowViewModel.viewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    MainWindowViewModel.IsVirtualNormalLibraryRequestModeSupportedForTest((int)requestMode, (int)treeMode),
                    treeMode + " request " + requestMode);
            }
        }
    }

    [TestMethod]
    public void VirtualNormalLibraryFullScanTree_IgnoresFolderFilter()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldApplyVirtualNormalLibraryFolderFilterForTest((int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyVirtualNormalLibraryFolderFilterForTest((int)MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected));
    }

    [TestMethod]
    public void VirtualChartSubsetRequestModes_CoverFilterAndSortUpdates()
    {
        MainWindowViewModel.viewUpdateMode[] treeModes =
        [
            MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected,
            MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected,
            MainWindowViewModel.viewUpdateMode.DuplicateFilterSelected,
            MainWindowViewModel.viewUpdateMode.GarbledFilterSelected,
            MainWindowViewModel.viewUpdateMode.GarbleFixedFilterSelected,
            MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected,
            MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected,
            MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainWindowViewModel.viewUpdateMode.NewlyInstalledFolderSelected,
            MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected
        ];
        MainWindowViewModel.viewUpdateMode[] refreshModes =
        [
            MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged,
            MainWindowViewModel.viewUpdateMode.KeywordFilterUpdated,
            MainWindowViewModel.viewUpdateMode.ModeFilterUpdated,
            MainWindowViewModel.viewUpdateMode.SortUpdated
        ];

        MainWindowViewModel.viewUpdateMode[] allModes = [.. Enum.GetValues(typeof(MainWindowViewModel.viewUpdateMode)).Cast<MainWindowViewModel.viewUpdateMode>()];
        foreach (MainWindowViewModel.viewUpdateMode treeMode in allModes)
        {
            bool expectedTreeSupport = treeModes.Contains(treeMode);
            Assert.AreEqual(
                expectedTreeSupport,
                MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)treeMode),
                treeMode + " tree support");
            foreach (MainWindowViewModel.viewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    MainWindowViewModel.IsVirtualChartSubsetRequestModeSupportedForTest((int)requestMode, (int)treeMode),
                    treeMode + " request " + requestMode);
            }
        }
    }

    [TestMethod]
    public void DuplicateVirtualSourceRows_PreserveBmsonStorageRowsWithoutCompatibilityFiles()
    {
        BMSFile bmsFile = CreateFile(
            Path.Combine("C:\\BMS", "DirA", "a.bms"),
            "BMS Alpha",
            "DirA",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            folder = Path.Combine("C:\\BMS", "DirB"),
            title = "Bmson Beta",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var duplicateGroup = new DuplicateGroup(
            [
                ChartFileProjection.WithWarnings(
                    ChartFileProjection.FromBmsFile(bmsFile),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")]),
                ChartFileProjection.WithWarnings(
                    ChartFileProjection.FromBmsonSong(bmsonSong),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")])
            ],
            [Path.Combine("C:\\BMS", "DirA"), Path.Combine("C:\\BMS", "DirB")]);

        List<ChartListSourceRow> rows = MainWindowViewModel.CreateDuplicateVirtualSourceRowsForTest([duplicateGroup], duplicateGroup);

        Assert.AreEqual(2, rows.Count);
        Assert.AreSame(bmsFile, rows.Single(row => row.BmsFile != null).BmsFile);
        ChartListSourceRow bmsonRow = rows.Single(row => row.BmsonSong != null);
        Assert.AreSame(bmsonSong, bmsonRow.BmsonSong);
        Assert.IsNull(bmsonRow.BmsFile);
        StringAssert.Contains(bmsonRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
    }

    [TestMethod]
    public void VirtualSortRouteColumns_CreateOrdersForAllChartListViewKinds()
    {
        var settings = new[]
        {
            new { Name = "STANDARD", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD) },
            new { Name = "DUPLICATE", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE) },
            new { Name = "FULLSCAN", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN) },
            new { Name = "INSTALL", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL) }
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(CreateSampleSortFiles(), CreateSampleSortBmsons());

        foreach (var item in settings)
        {
            foreach (CustomTableColumn column in CustomTableColumnFactory.CreateMainColumns(item.Setting).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath)))
            {
                Assert.IsTrue(
                    ChartListOrder.TryCreate(sourceRows, column.SortMemberPath, ListSortDirection.Ascending, out _),
                    item.Name + "." + column.Id + " uses unsupported SortMemberPath " + column.SortMemberPath);
            }
        }
    }

    [TestMethod]
    public void VirtualChartSubsetResourceHealthProjection_MatchesMaterializedModes()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.NewlyInstalledFolderSelected));

        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.GarbledFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected));
    }

    [TestMethod]
    public void NormalLibrarySortCacheKey_UsesSortKeyGenerationForOrderIdentity()
    {
        var current = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var same = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedSourceGeneration = new NormalLibrarySortCacheKey(8, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedSortKeyGeneration = new NormalLibrarySortCacheKey(7, 12, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedColumn = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Artist), ListSortDirection.Ascending, 3);
        var changedDirection = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Descending, 3);
        var changedRowCount = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 4);

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSourceGeneration);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.AreNotEqual(current, changedColumn);
        Assert.AreNotEqual(current, changedDirection);
        Assert.AreNotEqual(current, changedRowCount);

        var scoreAware = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var scoreAwareSame = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedScoreGeneration = new NormalLibrarySortCacheKey(7, 11, 2, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedChartInfoGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 3, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedMaintenanceGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 4, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);

        Assert.AreEqual(scoreAware, scoreAwareSame);
        Assert.AreNotEqual(scoreAware, changedScoreGeneration);
        Assert.AreNotEqual(scoreAware, changedChartInfoGeneration);
        Assert.AreNotEqual(scoreAware, changedMaintenanceGeneration);
    }

    [TestMethod]
    public void MainSummaryCacheKey_UsesGenerationsForIdentity()
    {
        var current = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        var same = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        var changedSortKeyGeneration = new MainViewSummaryCacheKey(7, 12, 3, includeBmsonRows: true, "normal_default");

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.IsFalse(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 11));
        Assert.IsTrue(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 12));
    }

    [TestMethod]
    public void LibraryChartSortMetrics_CarriesVirtualOrderTimingBreakdown()
    {
        var metrics = new LibraryChartSortMetrics(
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
    public void SourceRow_UsesIdentitySnapshotAndCurrentMutableState()
    {
        var file = new TestableBmsFile();
        file.Apply(@"folder-b\old.bms", "Old", "folder-b");
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows([file], null);

        file.Apply(
            @"folder-a\new.bms",
            "New",
            "folder-a",
            artistName: "Artist",
            genreName: "Genre",
            modeValue: 7,
            tagText: "Tag",
            md5: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256Text: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        file.instl_dst = "Destination";
        file.InstallDestinationTitle = "Destination Title";
        file.InstallDestinationArtist = "Destination Artist";
        file.AddRefTable(new BMSTable { symbol = "REF", name = "Reference Table" });

        Assert.AreEqual("Old", sourceRows[0].Title);
        Assert.AreEqual(string.Empty, sourceRows[0].Artist);
        Assert.AreEqual(string.Empty, sourceRows[0].Genre);
        Assert.AreEqual(@"folder-b\old.bms", sourceRows[0].Path);
        Assert.AreEqual("folder-b", sourceRows[0].Folder);
        Assert.IsNull(sourceRows[0].Mode);
        Assert.AreEqual(string.Empty, sourceRows[0].Tag);
        Assert.AreEqual("0123456789abcdef0123456789abcdef", sourceRows[0].Hash);
        Assert.AreEqual(string.Empty, sourceRows[0].Sha256);
        Assert.AreEqual("Destination", sourceRows[0].InstallDestination);
        Assert.AreEqual("Destination Title", sourceRows[0].InstallDestinationTitle);
        Assert.AreEqual("Destination Artist", sourceRows[0].InstallDestinationArtist);
        Assert.AreEqual("REF", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Reference Table", sourceRows[0].RefTablesNames);
    }

    [TestMethod]
    public void SourceRow_AndLibraryRowUsePlaylistReferenceProjectionForBmson()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        var table = new BMSTable
        {
            symbol = "BMSN",
            name = "Bmson Table",
            entries =
            [
                new TestablePlaylistEntry(null, bmson.sha256)
            ]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceTable(table, table.entries);
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(
            null,
            [bmson],
            null,
            row => index.Find(row.Hash, row.Sha256));
        var chartRow = LibraryChartRow.FromBmsonSong(bmson);
        chartRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.hash, row.sha256));

        Assert.AreEqual("BMSN", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Bmson Table", sourceRows[0].RefTablesNames);
        Assert.AreEqual("BMSN", chartRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Table", chartRow.RefTablesNames);
    }

    [TestMethod]
    public void SourceRow_SortKeysMatchLibraryChartRowForBmsAndBmson()
    {
        BMSFile file = CreateFile(
            @"folder-a\bms.bms",
            "BmsTitle",
            "folder-a",
            artist: "BmsArtist",
            genre: "BmsGenre",
            level: 12,
            mode: 5,
            tag: "BmsTag",
            hash: "11111111111111111111111111111111",
            sha256: "1111111111111111111111111111111111111111111111111111111111111111");
        file.bmsScore = CreateScore(file.hash, ClearType.HARD, RankType.AA, perfect: 800, great: 200, totalNotes: 1200, maxCombo: 999, minBp: 12, ranking: 42, rankingNum: 500, rankingLastUpdate: new DateTime(2026, 5, 15, 1, 2, 3, DateTimeKind.Local), stdDevVal: 51.5, scoreDifficulty: 78.25);
        file.SetChartInfo(CreateChartInfo(file.sha256, file.hash, level: 7, difficulty: 3, mainBpm: 150.5, total: 340.0));
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file.path, file.hash, 3), suppressPropertyChanged: true);
        file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
        file.instl_dst = "Installed";
        file.InstallDestinationTitle = "Installed Title";
        file.InstallDestinationArtist = "Installed Artist";
        file.AddRefTable(new BMSTable { symbol = "BMS", name = "BMS Table" });
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = @"folder-b\bmson.bmson",
            folder = "folder-b",
            title = "BmsonTitle",
            subtitle = "Another",
            artist = "BmsonArtist",
            genre = "BmsonGenre",
            mode_hint = "beat-7k",
            level = 9,
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            ChartInfo = CreateChartInfo("2222222222222222222222222222222222222222222222222222222222222222", "22222222222222222222222222222222", level: 4, difficulty: 1, mainBpm: 99.5, total: 240.0),
            MaintenanceInfo = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 4)
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows([file], [bmson]);

        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.BmsFile != null), LibraryChartRow.FromBmsFile(file));
        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.BmsonSong != null), LibraryChartRow.FromBmsonSong(bmson));
    }

    [TestMethod]
    public void SourceRow_WarningDigestMatchesLibraryChartRowWithResourceProjection()
    {
        BMSFile file = CreateFile(
            @"folder-a\warning.bms",
            "Warning",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "stale resource warning");
        file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
        var projection = new ResourceHealthWarningProjection(
            1,
            [ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "projected resource warning")],
            isIgnored: false);
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(
            [file],
            null,
            _ => projection);
        var chartRow = LibraryChartRow.FromBmsFile(file);
        chartRow.SetResourceHealthProjectionProvider(_ => projection);

        Assert.AreEqual(chartRow.WarningDigestText, sourceRows[0].WarningDigestText);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
    }

    [TestMethod]
    public void BmsonSortKeyChangeDetection_CoversVirtualRegistryColumns()
    {
        CollectionAssert.AreEquivalent(
            ChartListOrder.GetVirtualSortColumnMetadata().Select(column => column.NormalizedColumnName).ToArray(),
            MainWindowViewModel.GetBmsonLibrarySortKeySnapshotColumnNamesForTest().ToArray());

        AssertBmsonSortKeyChange(song => song.title = "ChangedTitle");
        AssertBmsonSortKeyChange(song => song.folder = "changed-folder");
        AssertBmsonSortKeyChange(song => song.path = @"changed-folder\changed.bmson");
        AssertBmsonSortKeyChange(song => song.artist = "ChangedArtist");
        AssertBmsonSortKeyChange(song => song.genre = "ChangedGenre");
        AssertBmsonSortKeyChange(song => song.mode_hint = "beat-5k");
        AssertBmsonSortKeyChange(song => song.md5 = "33333333333333333333333333333333");
        AssertBmsonSortKeyChange(song => song.sha256 = "3333333333333333333333333333333333333333333333333333333333333333");
        AssertBmsonSortKeyChange(song => song.level = 10);
        AssertBmsonSortKeyChange(song => song.MaintenanceInfo = CreateMaintenanceInfo(song.path, song.md5, 5));
        AssertBmsonSortKeyChange(song => song.ChartInfo = CreateChartInfo(song.sha256, song.md5, level: 12, difficulty: 4, mainBpm: 180.0, total: 360.0));
    }

    [TestMethod]
    public void BmsonSortKeyChangeDetection_CoversSameReferenceMutation()
    {
        AssertBmsonSameReferenceSortKeyChange(song => song.title = "ChangedTitle");
        AssertBmsonSameReferenceSortKeyChange(song => song.folder = "changed-folder");
        AssertBmsonSameReferenceSortKeyChange(song => song.path = @"changed-folder\changed.bmson");
        AssertBmsonSameReferenceSortKeyChange(song => song.artist = "ChangedArtist");
        AssertBmsonSameReferenceSortKeyChange(song => song.genre = "ChangedGenre");
        AssertBmsonSameReferenceSortKeyChange(song => song.mode_hint = "beat-5k");
        AssertBmsonSameReferenceSortKeyChange(song => song.md5 = "33333333333333333333333333333333");
        AssertBmsonSameReferenceSortKeyChange(song => song.sha256 = "3333333333333333333333333333333333333333333333333333333333333333");
        AssertBmsonSameReferenceSortKeyChange(song => song.level = 10);
        AssertBmsonSameReferenceSortKeyChange(song => song.MaintenanceInfo.encoding = "utf-16");
        AssertBmsonSameReferenceSortKeyChange(song => song.MaintenanceInfo.wav_files_existing = 1);
        AssertBmsonSameReferenceSortKeyChange(song => song.ChartInfo = CreateChartInfo(song.sha256, song.md5, level: 12, difficulty: 4, mainBpm: 180.0, total: 360.0));
    }

    private static ChartListVirtualView CreateView(out Func<int> getCreatedCount, int distinctFolderCount = -1)
    {
        List<BMSFile> files =
        [
            CreateFile(@"folder-b\charlie.bms", "Charlie", "folder-b"),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a"),
            CreateFile(@"folder-a\bravo.bms", "Bravo", "folder-a")
        ];
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, null);
        var order = ChartListOrder.CreateTitleAscending(sourceRows);
        int localCreatedCount = 0;
        var view = new ChartListVirtualView(
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
        List<LR2SongDBExtended.bmson_song> bmsons = CreateSampleSortBmsons();
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, bmsons);
        bool created = ChartListOrder.TryCreate(sourceRows, columnName, direction, out ChartListOrder order);
        Assert.IsTrue(created);
        var view = new ChartListVirtualView(
            sourceRows,
            order,
            row => row.BmsFile != null
                ? LibraryChartRow.FromBmsFile(row.BmsFile)
                : LibraryChartRow.FromBmsonSong(row.BmsonSong));
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(
            files.Select(LibraryChartRow.FromBmsFile).Concat(bmsons.Select(LibraryChartRow.FromBmsonSong)),
            sortParameters,
            isPlaylistDetailView: false,
            useLegacySortForDataGrid: false,
            out _);

        CollectionAssert.AreEqual(
            legacySorted.Select(row => row.path).ToArray(),
            Enumerable.Range(0, view.Count).Select(index => ((LibraryChartRow)view[index]).path).ToArray(),
            columnName + " " + direction + " order mismatch.");
    }

    private static List<BMSFile> CreateSampleSortFiles()
    {
        return
        [
            CreateFile(
                @"folder-a\z_item10.bms",
                "item10",
                "folder-a",
                artist: "Zulu",
                genre: "GenreC",
                level: 10,
                mode: 14,
                tag: "TagC",
                hash: "cccccccccccccccccccccccccccccccc",
                sha256: "3333333333333333333333333333333333333333333333333333333333333333",
                installDestination: "InstallC",
                installDestinationTitle: "InstallTitleC",
                installDestinationArtist: "InstallArtistC",
                refTableSymbol: "C",
                scoreSeed: 3,
                chartSeed: 3,
                maintenanceSeed: 3,
                warningSeed: 3),
            CreateFile(
                @"folder-a\a_item2.bms",
                "item2",
                "folder-a",
                artist: "Alpha",
                genre: "GenreB",
                level: 2,
                mode: 5,
                tag: "TagB",
                hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256: "1111111111111111111111111111111111111111111111111111111111111111",
                installDestination: "InstallA",
                installDestinationTitle: "InstallTitleA",
                installDestinationArtist: "InstallArtistA",
                refTableSymbol: "A",
                scoreSeed: 1,
                chartSeed: 1,
                maintenanceSeed: 1,
                warningSeed: 1),
            CreateFile(
                @"folder-b\m_alpha.bms",
                "Alpha",
                "folder-b",
                artist: "Middle",
                genre: "GenreA",
                level: 7,
                mode: null,
                tag: "TagA",
                hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256: "2222222222222222222222222222222222222222222222222222222222222222",
                installDestination: "InstallB",
                installDestinationTitle: "InstallTitleB",
                installDestinationArtist: "InstallArtistB",
                refTableSymbol: "B",
                scoreSeed: 2,
                chartSeed: 2,
                maintenanceSeed: 2,
                warningSeed: 2)
        ];
    }

    private static List<LR2SongDBExtended.bmson_song> CreateSampleSortBmsons()
    {
        return
        [
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-c\bmson-beta.bmson",
                folder = "folder-c",
                title = "BmsonBeta",
                artist = "Beta",
                genre = "GenreD",
                mode_hint = "beat-7k",
                level = 8,
                md5 = "dddddddddddddddddddddddddddddddd",
                sha256 = "4444444444444444444444444444444444444444444444444444444444444444",
                ChartInfo = CreateChartInfo("4444444444444444444444444444444444444444444444444444444444444444", "dddddddddddddddddddddddddddddddd", level: 6, difficulty: 2, mainBpm: 133.0, total: 270.0),
                MaintenanceInfo = CreateMaintenanceInfo(@"folder-c\bmson-beta.bmson", "dddddddddddddddddddddddddddddddd", 4)
            },
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-d\bmson-alpha.bmson",
                folder = "folder-d",
                title = "BmsonAlpha",
                artist = "AlphaBmson",
                genre = "Genre0",
                mode_hint = "beat-5k",
                level = 3,
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = "5555555555555555555555555555555555555555555555555555555555555555",
                ChartInfo = CreateChartInfo("5555555555555555555555555555555555555555555555555555555555555555", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 2, difficulty: 0, mainBpm: 90.0, total: 180.0),
                MaintenanceInfo = CreateMaintenanceInfo(@"folder-d\bmson-alpha.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", 5)
            }
        ];
    }

    private static BMSFile CreateFile(
        string path,
        string title,
        string folder,
        string artist = "",
        string genre = "",
        int? level = null,
        int? mode = null,
        string tag = "",
        string hash = "0123456789abcdef0123456789abcdef",
        string sha256 = "",
        string installDestination = "",
        string installDestinationTitle = "",
        string installDestinationArtist = "",
        string refTableSymbol = "",
        int? scoreSeed = null,
        int? chartSeed = null,
        int? maintenanceSeed = null,
        int? warningSeed = null)
    {
        var file = new TestableBmsFile();
        file.Apply(path, title, folder, artist, genre, level, mode, tag, hash, sha256);
        file.instl_dst = installDestination;
        file.InstallDestinationTitle = installDestinationTitle;
        file.InstallDestinationArtist = installDestinationArtist;
        if (scoreSeed.HasValue)
        {
            int seed = scoreSeed.Value;
            file.bmsScore = CreateScore(
                file.hash,
                seed == 1 ? ClearType.HARD : seed == 2 ? ClearType.EASY : ClearType.FC,
                seed == 1 ? RankType.A : seed == 2 ? RankType.AA : RankType.B,
                perfect: 300 + seed * 100,
                great: 50 + seed * 20,
                totalNotes: 600 + seed * 100,
                maxCombo: 400 + seed * 10,
                minBp: seed == 2 ? -1 : seed * 5,
                ranking: seed * 10,
                rankingNum: 100,
                rankingLastUpdate: new DateTime(2026, 5, 15, seed, 0, 0, DateTimeKind.Local),
                stdDevVal: 40.0 + seed,
                scoreDifficulty: 70.0 + seed);
        }
        if (chartSeed.HasValue)
        {
            int seed = chartSeed.Value;
            file.SetChartInfo(CreateChartInfo(file.sha256, file.hash, level: seed + 2, difficulty: seed, mainBpm: 100.0 + seed * 25.0, total: 200.0 + seed * 10.0));
        }
        if (maintenanceSeed.HasValue)
        {
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file.path, file.hash, maintenanceSeed.Value), suppressPropertyChanged: true);
        }
        if (!string.IsNullOrEmpty(refTableSymbol))
        {
            file.AddRefTable(new BMSTable { symbol = refTableSymbol, name = refTableSymbol });
        }
        if (warningSeed.HasValue)
        {
            switch (warningSeed.Value)
            {
                case 1:
                    file.SetWarning(ChartWarningKind.ZeroNoteMismatch, "zero note warning");
                    break;
                case 2:
                    file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
                    break;
                default:
                    file.SetWarning(ChartWarningKind.ChartInfoParseFailure, "chart info warning");
                    break;
            }
        }
        return file;
    }

    private static VirtualNormalLibrarySortDescriptor[] CreateExpectedDefaultPrewarmDescriptors()
    {
        return [.. CreateExpectedDefaultPrewarmColumnNames()
            .SelectMany(column => new[]
            {
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Ascending, GetPrewarmPriority(column)),
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Descending, GetPrewarmPriority(column))
            })];
    }

    private static int GetPrewarmPriority(string columnName)
    {
        Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata));
        return metadata.PrewarmPriority;
    }

    private static string[] CreateExpectedDefaultPrewarmColumnNames()
    {
        return
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.clear),
            nameof(LibraryChartRow.rateDouble),
            nameof(LibraryChartRow.minbp),
            nameof(LibraryChartRow.ChartJudgeSortKey),
            nameof(LibraryChartRow.ChartNotes),
            nameof(LibraryChartRow.ChartLongNotes),
            nameof(LibraryChartRow.ChartScratchNotes),
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            nameof(LibraryChartRow.ChartSoflanCount),
            nameof(LibraryChartRow.ChartTotalSortKey),
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            nameof(LibraryChartRow.ChartDurationSortKey),
            nameof(LibraryChartRow.ChartDensitySortKey),
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.RefTablesSymbols),
            nameof(LibraryChartRow.ChartLevelSortKey),
            nameof(LibraryChartRow.ChartDifficultySortKey),
            nameof(LibraryChartRow.ChartFeatureSortKey)
        ];
    }

    private static string[] CreateExpectedVirtualSortColumnNames()
    {
        return
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.WarningDigestText),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols),
            nameof(LibraryChartRow.WAVHealth),
            nameof(LibraryChartRow.BGAHealth),
            nameof(LibraryChartRow.MovieHealth),
            nameof(LibraryChartRow.encoding),
            nameof(LibraryChartRow.level),
            nameof(LibraryChartRow.clear),
            nameof(LibraryChartRow.rateDouble),
            nameof(LibraryChartRow.score),
            nameof(LibraryChartRow.maxcombo),
            nameof(LibraryChartRow.minbp),
            nameof(LibraryChartRow.rankingString),
            nameof(LibraryChartRow.rankingLastupdate),
            nameof(LibraryChartRow.stddevVal),
            nameof(LibraryChartRow.scoreDifficulty),
            nameof(LibraryChartRow.ChartLevelSortKey),
            nameof(LibraryChartRow.ChartDifficultySortKey),
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            nameof(LibraryChartRow.ChartDurationSortKey),
            nameof(LibraryChartRow.ChartJudgeSortKey),
            nameof(LibraryChartRow.ChartFeatureSortKey),
            nameof(LibraryChartRow.ChartNotes),
            nameof(LibraryChartRow.ChartLongNotes),
            nameof(LibraryChartRow.ChartScratchNotes),
            nameof(LibraryChartRow.ChartTotalSortKey),
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            nameof(LibraryChartRow.ChartDensitySortKey),
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            nameof(LibraryChartRow.ChartSoflanCount)
        ];
    }

    private static void AssertRegistryDependency(IEnumerable<ChartListOrderColumnMetadata> metadata, MainViewDataDependency dependency, string columnName)
    {
        Assert.AreEqual(
            dependency,
            metadata.Single(column => column.NormalizedColumnName == columnName).Dependency,
            columnName);
    }

    private static void AssertRegistryPrewarmPriority(IEnumerable<ChartListOrderColumnMetadata> metadata, int priority, string columnName)
    {
        Assert.AreEqual(
            priority,
            metadata.Single(column => column.NormalizedColumnName == columnName).PrewarmPriority,
            columnName);
    }

    private static void AssertSourceRowMatchesLibraryChartRow(ChartListSourceRow sourceRow, LibraryChartRow chartRow)
    {
        Assert.AreEqual(chartRow.Title, sourceRow.Title);
        Assert.AreEqual(chartRow.Artist, sourceRow.Artist);
        Assert.AreEqual(chartRow.genre, sourceRow.Genre);
        Assert.AreEqual(chartRow.Level, sourceRow.Level);
        Assert.AreEqual(chartRow.level, sourceRow.LevelValue);
        Assert.AreEqual(chartRow.Folder, sourceRow.Folder);
        Assert.AreEqual(chartRow.path, sourceRow.Path);
        Assert.AreEqual(chartRow.mode, sourceRow.Mode);
        Assert.AreEqual(chartRow.tag, sourceRow.Tag);
        Assert.AreEqual(chartRow.hash, sourceRow.Hash);
        Assert.AreEqual(chartRow.sha256, sourceRow.Sha256);
        Assert.AreEqual(chartRow.clear, sourceRow.Clear);
        Assert.AreEqual(chartRow.rank, sourceRow.Rank);
        Assert.AreEqual(chartRow.rateDouble, sourceRow.RateDouble);
        Assert.AreEqual(chartRow.rate, sourceRow.Rate);
        Assert.AreEqual(chartRow.score, sourceRow.Score);
        Assert.AreEqual(chartRow.totalnotes, sourceRow.TotalNotes);
        Assert.AreEqual(chartRow.maxcombo, sourceRow.MaxCombo);
        Assert.AreEqual(chartRow.minbp, sourceRow.MinBp);
        Assert.AreEqual(chartRow.ranking, sourceRow.Ranking);
        Assert.AreEqual(chartRow.rankingNum, sourceRow.RankingNum);
        Assert.AreEqual(chartRow.rankingString, sourceRow.RankingString);
        Assert.AreEqual(chartRow.rankingLastupdate, sourceRow.RankingLastUpdate);
        Assert.AreEqual(chartRow.stddevVal, sourceRow.StdDevVal);
        Assert.AreEqual(chartRow.scoreDifficulty, sourceRow.ScoreDifficulty);
        Assert.AreEqual(chartRow.ChartLevelSortKey, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(chartRow.ChartDifficultySortKey, sourceRow.ChartDifficultySortKey);
        Assert.AreEqual(chartRow.ChartMainBpmSortKey, sourceRow.ChartMainBpmSortKey);
        Assert.AreEqual(chartRow.ChartMaxBpmSortKey, sourceRow.ChartMaxBpmSortKey);
        Assert.AreEqual(chartRow.ChartMinBpmSortKey, sourceRow.ChartMinBpmSortKey);
        Assert.AreEqual(chartRow.ChartDurationSortKey, sourceRow.ChartDurationSortKey);
        Assert.AreEqual(chartRow.ChartJudgeSortKey, sourceRow.ChartJudgeSortKey);
        Assert.AreEqual(chartRow.ChartFeatureSortKey, sourceRow.ChartFeatureSortKey);
        Assert.AreEqual(chartRow.ChartNotes, sourceRow.ChartNotes);
        Assert.AreEqual(chartRow.ChartLongNotes, sourceRow.ChartLongNotes);
        Assert.AreEqual(chartRow.ChartScratchNotes, sourceRow.ChartScratchNotes);
        Assert.AreEqual(chartRow.ChartTotalSortKey, sourceRow.ChartTotalSortKey);
        Assert.AreEqual(chartRow.ChartTotalPerNoteSortKey, sourceRow.ChartTotalPerNoteSortKey);
        Assert.AreEqual(chartRow.ChartDensitySortKey, sourceRow.ChartDensitySortKey);
        Assert.AreEqual(chartRow.ChartPeakDensitySortKey, sourceRow.ChartPeakDensitySortKey);
        Assert.AreEqual(chartRow.ChartEndDensitySortKey, sourceRow.ChartEndDensitySortKey);
        Assert.AreEqual(chartRow.ChartSoflanCount, sourceRow.ChartSoflanCount);
        Assert.AreEqual(chartRow.instl_dst, sourceRow.InstallDestination);
        Assert.AreEqual(chartRow.InstallDestinationTitle, sourceRow.InstallDestinationTitle);
        Assert.AreEqual(chartRow.InstallDestinationArtist, sourceRow.InstallDestinationArtist);
        Assert.AreEqual(chartRow.RefTablesSymbols, sourceRow.RefTablesSymbols);
        Assert.AreEqual(chartRow.WarningDigestText, sourceRow.WarningDigestText);
        Assert.AreEqual(chartRow.WAVHealth, sourceRow.WAVHealth);
        Assert.AreEqual(chartRow.BGAHealth, sourceRow.BGAHealth);
        Assert.AreEqual(chartRow.MovieHealth, sourceRow.MovieHealth);
        Assert.AreEqual(chartRow.encoding, sourceRow.EncodingName);
    }

    private static void AssertBmsonSortKeyChange(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LR2SongDBExtended.bmson_song original = CreateBmsonSong();
        LR2SongDBExtended.bmson_song next = CreateBmsonSong();
        mutate(next);

        Assert.IsTrue(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow.FromBmsonSong(original), next));
    }

    private static void AssertBmsonSameReferenceSortKeyChange(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        Assert.IsTrue(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow.FromBmsonSong(CreateBmsonSong()), mutate));
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSong()
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = @"folder-b\bmson.bmson",
            folder = "folder-b",
            title = "BmsonTitle",
            subtitle = "Subtitle",
            artist = "BmsonArtist",
            genre = "BmsonGenre",
            mode_hint = "beat-7k",
            level = 6,
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            ChartInfo = CreateChartInfo("2222222222222222222222222222222222222222222222222222222222222222", "22222222222222222222222222222222", level: 5, difficulty: 2, mainBpm: 120.0, total: 300.0),
            MaintenanceInfo = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 3)
        };
    }

    private static BMSScore CreateScore(
        string hash,
        ClearType clear,
        RankType rank,
        int perfect,
        int great,
        int totalNotes,
        int maxCombo,
        int minBp,
        int ranking,
        int rankingNum,
        DateTime rankingLastUpdate,
        double stdDevVal,
        double scoreDifficulty)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = perfect,
            great = great,
            totalnotes = totalNotes,
            maxcombo = maxCombo,
            minbp = minBp,
            ranking = ranking,
            rankingNum = rankingNum,
            rankingLastupdate = rankingLastUpdate,
            stddevVal = stdDevVal,
            scoreDifficulty = scoreDifficulty
        };
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int level, int difficulty, double mainBpm, double total)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            level = level,
            difficulty = difficulty,
            difficulty_defined = true,
            mainbpm = mainBpm,
            maxbpm = mainBpm + 25.0,
            minbpm = Math.Max(1.0, mainBpm - 25.0),
            length = 120000 + level * 1000,
            judge = 100 + difficulty,
            feature = difficulty + 1,
            notes = 1000 + level * 10,
            ln = 100 + level,
            s = 20 + difficulty,
            ls = 5 + difficulty,
            total = total,
            total_defined = true,
            density = 8.0 + level,
            peakdensity = 16.0 + level,
            enddensity = 4.0 + difficulty,
            speedchange_count = difficulty + 2
        };
    }

    private static BMSFileMaintenanceInfo CreateMaintenanceInfo(string path, string hash, int seed)
    {
        return new BMSFileMaintenanceInfo
        {
            path = path,
            hash = hash,
            encoding = seed % 2 == 0 ? "utf-8" : "shift_jis",
            wav_files_defined = 100,
            wav_files_existing = 20 + seed * 10,
            bga_files_defined = 50,
            bga_files_existing = 10 + seed * 5,
            movie_files_defined = 20,
            movie_files_existing = seed
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(
            string filePath,
            string fileTitle,
            string folderName,
            string artistName = "",
            string genreName = "",
            int? levelValue = null,
            int? modeValue = null,
            string tagText = "",
            string md5 = "0123456789abcdef0123456789abcdef",
            string sha256Text = "")
        {
            path = filePath;
            title = fileTitle;
            folder = folderName;
            artist = artistName;
            genre = genreName;
            level = levelValue;
            mode = modeValue;
            tag = tagText;
            hash = md5;
            sha256 = sha256Text;
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        internal TestablePlaylistEntry(string? md5Value, string? sha256Value)
        {
            md5 = md5Value;
            sha256 = sha256Value;
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
