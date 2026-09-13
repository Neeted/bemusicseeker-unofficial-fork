using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryCountAndPresentationTests
{
    [TestMethod]
    public void CalculatePlaylistSummaryCounts_CountsActiveHashedRowsWithoutDedup()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateEntry(null, null),
            CreateEntry("cccccccccccccccccccccccccccccccc", null, isRemoved: true)
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(3, result.TotalCharts);
        Assert.AreEqual(3, result.OwnedCharts);
    }

    [TestMethod]
    public void CalculatePlaylistSummaryCounts_DoesNotFallbackToSha256WhenMd5Exists()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet("cccccccccccccccccccccccccccccccc"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(0, result.OwnedCharts);
    }

    [TestMethod]
    public void CalculatePlaylistSummaryCounts_UsesSha256WhenMd5IsMissing()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet(),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(1, result.OwnedCharts);
    }

    [TestMethod]
    public void PlaylistCatalogSummaryOwner_DoesNotReuseCountForReplacedTableInstance()
    {
        var ownedHashes = new OwnedChartHashIndexSnapshot();
        ownedHashes.AddMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownedSnapshot = new OwnedChartHashIndexVersionedSnapshot(
            ownedHashes,
            version: 1,
            buildElapsedMs: 0,
            invalidationVersion: 1,
            ownedCollectionVersion: 1,
            bmsRowsVersion: 1,
            bmsonRowsVersion: 0);
        var owner = new PlaylistCatalogSummaryOwner();
        var firstTable = new BMSTable
        {
            playlist_id = 42
        };
        firstTable.entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)];

        PlaylistSummaryCountResult first = owner.GetOrBuildTableCount(
            firstTable,
            ownedSnapshot,
            CancellationToken.None,
            out bool firstCacheHit);

        var replacementTable = new BMSTable
        {
            playlist_id = 42
        };
        replacementTable.entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null)];

        PlaylistSummaryCountResult replacement = owner.GetOrBuildTableCount(
            replacementTable,
            ownedSnapshot,
            CancellationToken.None,
            out bool replacementCacheHit);

        Assert.IsFalse(firstCacheHit);
        Assert.AreEqual(1, first.TotalCharts);
        Assert.AreEqual(1, first.OwnedCharts);
        Assert.IsFalse(replacementCacheHit);
        Assert.AreEqual(1, replacement.TotalCharts);
        Assert.AreEqual(0, replacement.OwnedCharts);
    }

    [TestMethod]
    public void PlaylistCatalogSummaryOwner_DoesNotPublishCountAfterTableRevisionChanges()
    {
        var ownedHashes = new OwnedChartHashIndexSnapshot();
        ownedHashes.AddMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownedSnapshot = new OwnedChartHashIndexVersionedSnapshot(
            ownedHashes,
            version: 1,
            buildElapsedMs: 0,
            invalidationVersion: 1,
            ownedCollectionVersion: 1,
            bmsRowsVersion: 1,
            bmsonRowsVersion: 0);
        var owner = new PlaylistCatalogSummaryOwner();
        var table = new BMSTable
        {
            playlist_id = 42
        };
        table.entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)];

        PlaylistSummaryCountResult first = owner.GetOrBuildTableCount(
            table,
            ownedSnapshot,
            CancellationToken.None,
            out bool firstCacheHit);

        table.entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null)];

        PlaylistSummaryCountResult changed = owner.GetOrBuildTableCount(
            table,
            ownedSnapshot,
            CancellationToken.None,
            out bool changedCacheHit);

        Assert.IsFalse(firstCacheHit);
        Assert.AreEqual(1, first.OwnedCharts);
        Assert.IsFalse(changedCacheHit);
        Assert.AreEqual(0, changed.OwnedCharts);
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_AppliesFilterAndSortWithoutRebuildLogic()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                Name = "beta",
                Symbol = "B",
                TotalCharts = 4,
                OwnedCharts = 2
            },
            new PlaylistSummaryRow
            {
                Name = "alpha",
                Symbol = "A",
                TotalCharts = 3,
                OwnedCharts = 3
            },
            new PlaylistSummaryRow
            {
                Name = "gamma",
                Symbol = "G",
                TotalCharts = 1,
                OwnedCharts = 0
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "a",
            PlaylistOwnedFilter.OwnedIncomplete,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.Name),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(2, result.FilteredCount);
        CollectionAssert.AreEqual(new[] { "beta", "gamma" }, result.Rows.Select(row => row.Name).ToArray());
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsAndAndFieldQueries()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                FolderName = "folderalpha",
                CompatPrefix = "A",
                Symbol = "A",
                HeaderUri = new Uri("https://example.com/header-alpha.json"),
                DataUri = new Uri("https://example.com/data-alpha.json")
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha other",
                FolderName = "folderbeta",
                CompatPrefix = "B",
                Symbol = "B",
                HeaderUri = new Uri("https://example.com/header-beta.json"),
                DataUri = new Uri("https://example.com/data-beta.json")
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:alpha foldername:folderalpha prefix:A symbol:A header:header-alpha data:data-alpha",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_UnknownFieldDoesNotMatch()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "md5:aaaaaaaa",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(0, result.FilteredCount);
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha pack",
                Symbol = "B"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 30,
                Name = "beta pack",
                Symbol = "C"
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:\"alpha pack\" symbol:A|C -id:20 name:re:^alpha",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

}
