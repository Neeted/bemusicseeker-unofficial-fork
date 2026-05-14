using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
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
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
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
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.ChartLevelSortKey), ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.WAVHealth), ListSortDirection.Ascending, out _));
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
    public void VirtualSortRegistryMetadata_DescribesDefaultIdentityColumns()
    {
        ChartListOrderColumnMetadata[] metadata = ChartListOrder.GetVirtualSortColumnMetadata().ToArray();

        CollectionAssert.AreEqual(
            CreateExpectedVirtualSortColumnNames(),
            metadata.Select(column => column.NormalizedColumnName).ToArray());
        foreach (ChartListOrderColumnMetadata column in metadata)
        {
            Assert.AreEqual(MainViewDataDependency.IdentitySortKey, column.Dependency, column.NormalizedColumnName);
            Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(column.NormalizedColumnName, out ChartListOrderColumnMetadata resolved));
            Assert.AreEqual(column.Dependency, resolved.Dependency);
        }
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
            mode: 5,
            tag: "BmsTag",
            hash: "11111111111111111111111111111111",
            sha256: "1111111111111111111111111111111111111111111111111111111111111111");
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
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
        };
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(new[] { file }, new[] { bmson });

        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.BmsFile != null), LibraryChartRow.FromBmsFile(file));
        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.BmsonSong != null), LibraryChartRow.FromBmsonSong(bmson));
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
                mode: 14,
                tag: "TagC",
                hash: "cccccccccccccccccccccccccccccccc",
                sha256: "3333333333333333333333333333333333333333333333333333333333333333",
                installDestination: "InstallC",
                installDestinationTitle: "InstallTitleC",
                installDestinationArtist: "InstallArtistC",
                refTableSymbol: "C"),
            CreateFile(
                @"folder-a\a_item2.bms",
                "item2",
                "folder-a",
                artist: "Alpha",
                genre: "GenreB",
                mode: 5,
                tag: "TagB",
                hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256: "1111111111111111111111111111111111111111111111111111111111111111",
                installDestination: "InstallA",
                installDestinationTitle: "InstallTitleA",
                installDestinationArtist: "InstallArtistA",
                refTableSymbol: "A"),
            CreateFile(
                @"folder-b\m_alpha.bms",
                "Alpha",
                "folder-b",
                artist: "Middle",
                genre: "GenreA",
                mode: null,
                tag: "TagA",
                hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256: "2222222222222222222222222222222222222222222222222222222222222222",
                installDestination: "InstallB",
                installDestinationTitle: "InstallTitleB",
                installDestinationArtist: "InstallArtistB",
                refTableSymbol: "B")
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
                md5 = "dddddddddddddddddddddddddddddddd",
                sha256 = "4444444444444444444444444444444444444444444444444444444444444444"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-d\bmson-alpha.bmson",
                folder = "folder-d",
                title = "BmsonAlpha",
                artist = "AlphaBmson",
                genre = "Genre0",
                mode_hint = "beat-5k",
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = "5555555555555555555555555555555555555555555555555555555555555555"
            }
        };
    }

    private static BMSFile CreateFile(
        string path,
        string title,
        string folder,
        string artist = "",
        string genre = "",
        int? mode = null,
        string tag = "",
        string hash = "0123456789abcdef0123456789abcdef",
        string sha256 = "",
        string installDestination = "",
        string installDestinationTitle = "",
        string installDestinationArtist = "",
        string refTableSymbol = "")
    {
        TestableBmsFile file = new TestableBmsFile();
        file.Apply(path, title, folder, artist, genre, mode, tag, hash, sha256);
        file.instl_dst = installDestination;
        file.InstallDestinationTitle = installDestinationTitle;
        file.InstallDestinationArtist = installDestinationArtist;
        if (!string.IsNullOrEmpty(refTableSymbol))
        {
            file.AddRefTable(new BMSTable { symbol = refTableSymbol, name = refTableSymbol });
        }
        return file;
    }

    private static VirtualNormalLibrarySortDescriptor[] CreateExpectedDefaultPrewarmDescriptors()
    {
        return CreateExpectedDefaultPrewarmColumnNames()
            .SelectMany(column => new[]
            {
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Ascending),
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Descending)
            })
            .ToArray();
    }

    private static string[] CreateExpectedDefaultPrewarmColumnNames()
    {
        return new[]
        {
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.RefTablesSymbols)
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
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols)
        };
    }

    private static void AssertSourceRowMatchesLibraryChartRow(ChartListSourceRow sourceRow, LibraryChartRow chartRow)
    {
        Assert.AreEqual(chartRow.Title, sourceRow.Title);
        Assert.AreEqual(chartRow.Artist, sourceRow.Artist);
        Assert.AreEqual(chartRow.genre, sourceRow.Genre);
        Assert.AreEqual(chartRow.Folder, sourceRow.Folder);
        Assert.AreEqual(chartRow.path, sourceRow.Path);
        Assert.AreEqual(chartRow.mode, sourceRow.Mode);
        Assert.AreEqual(chartRow.tag, sourceRow.Tag);
        Assert.AreEqual(chartRow.hash, sourceRow.Hash);
        Assert.AreEqual(chartRow.sha256, sourceRow.Sha256);
        Assert.AreEqual(chartRow.instl_dst, sourceRow.InstallDestination);
        Assert.AreEqual(chartRow.InstallDestinationTitle, sourceRow.InstallDestinationTitle);
        Assert.AreEqual(chartRow.InstallDestinationArtist, sourceRow.InstallDestinationArtist);
        Assert.AreEqual(chartRow.RefTablesSymbols, sourceRow.RefTablesSymbols);
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
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
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
