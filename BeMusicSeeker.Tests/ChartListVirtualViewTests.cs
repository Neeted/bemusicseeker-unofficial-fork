using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
        {
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
        };

        foreach (string column in columns)
        {
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Ascending);
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Descending);
        }
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
        ChartListOrderColumnMetadata[] metadata = ChartListOrder.GetVirtualSortColumnMetadata().ToArray();

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
        CustomTableColumnSettings settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        string[] columns = MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)
            .ToArray();

        CollectionAssert.Contains(columns, nameof(LibraryChartRow.Title));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.rateDouble));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartTotalSortKey));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartFeatureSortKey));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.hash));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.score));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.maxcombo));

        settings.Combo.Visibility = Visibility.Visible;
        columns = MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)
            .ToArray();

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
    public void VirtualBmsFileSubsetTreeModes_AreLimitedToSimpleBmsFileCollections()
    {
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.GarbledFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.GarbleFixedFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected));

        Assert.IsFalse(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.DuplicateFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.NewlyInstalledFolderSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualBmsFileSubsetTreeModeSupportedForTest((int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected));
    }

    [TestMethod]
    public void VirtualBmsFileSubsetResourceHealthProjection_IsLimitedToFileMissingModes()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected));

        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.GarbledFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected));
    }

    [TestMethod]
    public void NormalLibrarySortCacheKey_UsesSortKeyGenerationForOrderIdentity()
    {
        NormalLibrarySortCacheKey current = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey same = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedSourceGeneration = new NormalLibrarySortCacheKey(8, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedSortKeyGeneration = new NormalLibrarySortCacheKey(7, 12, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedColumn = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Artist), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedDirection = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Descending, 3);
        NormalLibrarySortCacheKey changedRowCount = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 4);

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSourceGeneration);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.AreNotEqual(current, changedColumn);
        Assert.AreNotEqual(current, changedDirection);
        Assert.AreNotEqual(current, changedRowCount);

        NormalLibrarySortCacheKey scoreAware = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey scoreAwareSame = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedScoreGeneration = new NormalLibrarySortCacheKey(7, 11, 2, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedChartInfoGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 3, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        NormalLibrarySortCacheKey changedMaintenanceGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 4, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);

        Assert.AreEqual(scoreAware, scoreAwareSame);
        Assert.AreNotEqual(scoreAware, changedScoreGeneration);
        Assert.AreNotEqual(scoreAware, changedChartInfoGeneration);
        Assert.AreNotEqual(scoreAware, changedMaintenanceGeneration);
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

        Assert.AreEqual("New", sourceRows[0].Title);
        Assert.AreEqual("Artist", sourceRows[0].Artist);
        Assert.AreEqual("Genre", sourceRows[0].Genre);
        Assert.AreEqual(@"folder-a\new.bms", sourceRows[0].Path);
        Assert.AreEqual("folder-a", sourceRows[0].Folder);
        Assert.AreEqual(7, sourceRows[0].Mode);
        Assert.AreEqual("Tag", sourceRows[0].Tag);
        Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sourceRows[0].Hash);
        Assert.AreEqual("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", sourceRows[0].Sha256);
        Assert.AreEqual("Destination", sourceRows[0].InstallDestination);
        Assert.AreEqual("Destination Title", sourceRows[0].InstallDestinationTitle);
        Assert.AreEqual("Destination Artist", sourceRows[0].InstallDestinationArtist);
        Assert.AreEqual("REF", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Reference Table", sourceRows[0].RefTablesNames);
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
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
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
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(new[] { file }, new[] { bmson });

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
        ResourceHealthWarningProjection projection = new ResourceHealthWarningProjection(
            1,
            new[] { ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "projected resource warning") },
            isIgnored: false);
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(
            new[] { file },
            null,
            _ => projection);
        LibraryChartRow chartRow = LibraryChartRow.FromBmsFile(file);
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
        List<LR2SongDBExtended.bmson_song> bmsons = CreateSampleSortBmsons();
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(files, bmsons);
        bool created = ChartListOrder.TryCreate(sourceRows, columnName, direction, out ChartListOrder order);
        Assert.IsTrue(created);
        ChartListVirtualView view = new ChartListVirtualView(
            sourceRows,
            order,
            row => row.BmsFile != null
                ? LibraryChartRow.FromBmsFile(row.BmsFile)
                : LibraryChartRow.FromBmsonSong(row.BmsonSong));
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
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
        return new List<BMSFile>
        {
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
        };
    }

    private static List<LR2SongDBExtended.bmson_song> CreateSampleSortBmsons()
    {
        return new List<LR2SongDBExtended.bmson_song>
        {
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
        };
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
        TestableBmsFile file = new TestableBmsFile();
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
        return CreateExpectedDefaultPrewarmColumnNames()
            .SelectMany(column => new[]
            {
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Ascending, GetPrewarmPriority(column)),
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Descending, GetPrewarmPriority(column))
            })
            .ToArray();
    }

    private static int GetPrewarmPriority(string columnName)
    {
        Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata));
        return metadata.PrewarmPriority;
    }

    private static string[] CreateExpectedDefaultPrewarmColumnNames()
    {
        return new[]
        {
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
        };
    }

    private static string[] CreateExpectedVirtualSortColumnNames()
    {
        return new[]
        {
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
        };
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
