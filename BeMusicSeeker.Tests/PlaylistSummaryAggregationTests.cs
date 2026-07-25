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

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryAggregationTests
{
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RejectsCanceledBuildBeforeReadingStorage()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                library.GetOwnedChartHashIndexSnapshot(cancellation.Token));
        });
    }

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
        ownedHashes.Md5Hashes.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
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
        ownedHashes.Md5Hashes.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
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
    public void GetOwnedChartHashIndexSnapshot_IncludesBmsonHashes()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            SetLibraryFilesWithoutNotification(library,
            [
                CreateLibraryFile(@"C:\Songs\bms.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            ]);
            SetLibraryBmsonSongsWithoutNotification(library,
            [
                new LR2SongDBExtended.bmson_song
                {
                    path = @"C:\Songs\bmson\chart.bmson",
                    folder = @"C:\Songs\bmson",
                    md5 = "cccccccccccccccccccccccccccccccc",
                    sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                }
            ]);

            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        });
    }

    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterLibraryOwnershipChanges()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();

            library.BMSFiles =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterLibraryMutationDelta()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            BMSFile removedFile = CreateLibraryFile(@"C:\Songs\removed.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            BMSFile keptFile = CreateLibraryFile(@"C:\Songs\kept.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [removedFile, keptFile]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedFile));

            InvokeApplyLibraryMutationDelta(library, delta);
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterInstalledChartUpsert()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string chartPath = @"C:\Songs\replace.bms";
            BMSFile replacedFile = CreateLibraryFile(chartPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            BMSFile newFile = CreateLibraryFile(chartPath, "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [replacedFile]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newFile], []));
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    [TestMethod]
    public void WarmOwnedChartHashIndexSnapshot_BuildsReusesAndRebuildsAfterOwnershipChange()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };

            OwnedHashIndexWarmupResult first = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedHashIndexWarmupResult second = library.WarmOwnedChartHashIndexSnapshot("test");

            Assert.AreEqual("catalog_owned_hash", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(1, first.Md5Count);
            Assert.AreEqual(1, first.Sha256Count);
            Assert.AreEqual(0, first.StaleRetryCount);
            Assert.AreEqual(library.OwnedChartCollectionVersion, first.OwnedCollectionVersion);
            Assert.IsTrue(first.BmsRowsVersion > 0);
            Assert.AreEqual(0, first.BmsonRowsVersion);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.SnapshotVersion, second.SnapshotVersion);
            Assert.AreEqual(first.InvalidationVersion, second.InvalidationVersion);
            Assert.AreEqual(first.OwnedCollectionVersion, second.OwnedCollectionVersion);
            Assert.AreEqual(first.BmsRowsVersion, second.BmsRowsVersion);
            Assert.AreEqual(first.BmsonRowsVersion, second.BmsonRowsVersion);

            library.BMSFiles =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            OwnedHashIndexWarmupResult third = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual("built", third.Status);
            Assert.IsTrue(third.SnapshotVersion > second.SnapshotVersion);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.OwnedChartCollectionVersion, third.OwnedCollectionVersion);
            Assert.IsTrue(third.BmsRowsVersion > second.BmsRowsVersion);
            Assert.AreEqual(third.SnapshotVersion, snapshot.Version);
            CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_CachesAndRebuildsAfterOwnershipChange()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };

            PlaylistLibraryResolveIndexSnapshot first = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool firstCacheHit, out int firstStaleRetries);
            PlaylistLibraryResolveIndexSnapshot second = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool secondCacheHit, out int secondStaleRetries);
            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState cachedState = library.GetPlaylistLibraryResolveIndexRuntimeState();

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(0, firstStaleRetries);
            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(0, secondStaleRetries);
            Assert.AreEqual(first.Version, second.Version);
            Assert.IsTrue(cachedState.IsCached);
            Assert.AreEqual(first.Version, cachedState.SnapshotVersion);
            Assert.IsTrue(first.ChartsByMd5.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

            library.BMSFiles =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState invalidatedState = library.GetPlaylistLibraryResolveIndexRuntimeState();
            PlaylistLibraryResolveIndexSnapshot third = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool thirdCacheHit, out int thirdStaleRetries);

            Assert.IsFalse(invalidatedState.IsCached);
            Assert.IsFalse(thirdCacheHit);
            Assert.AreEqual(0, thirdStaleRetries);
            Assert.IsTrue(third.Version > second.Version);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.OwnedChartCollectionVersion, third.OwnedCollectionVersion);
            Assert.IsTrue(third.BmsRowsVersion > second.BmsRowsVersion);
            Assert.IsFalse(third.ChartsByMd5.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(third.ChartsByMd5.ContainsKey("cccccccccccccccccccccccccccccccc"));
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_ReturnsCachedDuringDigestMutationWindowAndRebuildsAfter()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool initialCacheHit, out int _);
            Assert.IsFalse(initialCacheHit);

            using (BeginOwnedDigestMutationWindow(library))
            {
                BMSLibrary.PlaylistLibraryResolveIndexRuntimeState transientState = library.GetPlaylistLibraryResolveIndexRuntimeState();
                PlaylistLibraryResolveIndexSnapshot transientSnapshot = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool transientCacheHit, out int transientStaleRetries);

                Assert.IsTrue(transientState.IsCached);
                Assert.AreEqual(initial.Version, transientState.SnapshotVersion);
                Assert.IsTrue(transientCacheHit);
                Assert.AreEqual(0, transientStaleRetries);
                Assert.AreEqual(initial.Version, transientSnapshot.Version);
            }

            PlaylistLibraryResolveIndexSnapshot rebuilt = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool rebuiltCacheHit, out int _);
            PlaylistLibraryResolveIndexSnapshot cached = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool cachedCacheHit, out int _);

            Assert.IsFalse(rebuiltCacheHit);
            Assert.IsTrue(rebuilt.Version > initial.Version);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(rebuilt.Version, cached.Version);
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexRuntimeState_ReportsCachedRefDuringDigestMutationWindow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            PlaylistLibraryResolveIndexSnapshot snapshot = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool cacheHit, out int _);
            Assert.IsFalse(cacheHit);

            using (BeginOwnedDigestMutationWindow(library))
            {
                BMSLibrary.PlaylistLibraryResolveIndexRuntimeState state = library.GetPlaylistLibraryResolveIndexRuntimeState();

                Assert.IsTrue(state.IsCached);
                Assert.AreEqual(snapshot.Version, state.SnapshotVersion);
            }
        });
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

    private static HashSet<string> CreateHashSet(params string[] values)
    {
        return new HashSet<string>(values ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private static BMSTableEntry CreateEntry(string? md5, string? sha256, bool isRemoved = false)
    {
        var entry = new TestablePlaylistEntry
        {
            is_removed = isRemoved
        };
        if (md5 != null)
        {
            entry.SetHash(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return entry;
    }

    private static BMSFile CreateLibraryFile(string path, string md5, string sha256)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(md5);
        file.SetSha256(sha256);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetHash(string value)
        {
            md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    private static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange(files ?? []);
        result.NextBmsonSongs.AddRange(library.BmsonSongs);
        InvokeApplyCatalogStorageRowsWithoutNotification(library, result);
    }

    private static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange(library.BMSFiles);
        result.NextBmsonSongs.AddRange(songs ?? []);
        InvokeApplyCatalogStorageRowsWithoutNotification(library, result);
    }

    private static void InvokeApplyCatalogStorageRowsWithoutNotification(
        BMSLibrary library,
        SongTableFileCheckResult result)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod(
            "ApplyCatalogStorageRows",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(
            library,
            [
                result.NextFiles,
                result.NextBmsonSongs,
                true,
                true,
                false,
                false
            ]);
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        library.ApplyLibraryMutationDelta(delta);
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet addedTargets)
    {
        library.ApplyInstalledChartStorageTargets(addedTargets, "install_package");
    }

    private static IDisposable BeginOwnedDigestMutationWindow(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("BeginOwnedDigestMutationWindow", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (IDisposable)methodInfo.Invoke(library, []);
    }

    private static void WithTemporarySongDb(System.Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaylistSummaryAggregation_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
