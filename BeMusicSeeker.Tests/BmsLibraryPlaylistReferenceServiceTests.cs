using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPlaylistReferenceServiceTests
{
    [TestMethod]
    public void BuildReferenceLookupKeys_AndApplyReferenceMap_MatchesSongAndPendingFiles()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        List<BMSFile> files =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("cccccccccccccccccccccccccccccccc")
        ];

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots(files), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(2, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(0, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(2, matchedFiles);
        Assert.AreEqual(2, appliedCharts);
        Assert.AreEqual(2, stats.Chunks);
    }

    [TestMethod]
    public void ApplyReferenceMap_UsesSha256WhenMd5IsMissing()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(CreateEntry(null, new string('a', 64)));
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('a', 64));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(0, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(1, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, appliedCharts);
    }

    [TestMethod]
    public void ApplyReferenceMap_DoesNotFallbackToSha256WhenEntryMd5Exists()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string sha256 = new string('a', 64);
        BMSTable table = CreateTable(CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sha256));
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sha256);

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(0, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(0, matchedFiles);
        Assert.AreEqual(0, appliedCharts);
    }

    [TestMethod]
    public void ApplyReferenceMap_PrefersMd5WhenBothHashesExist()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable md5Table = CreateTable(CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        md5Table.name = "MD5";
        BMSTable shaTable = CreateTable(CreateEntry(null, new string('b', 64)));
        shaTable.name = "SHA";
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys([md5Table, shaTable]);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, appliedCharts);
    }

    [TestMethod]
    public void ApplyReferenceMap_ChartFileBmsonMatchesWithoutBmsRefTableMutation()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        LibraryChartRef chart = LibraryChartRef.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = md5,
            sha256 = new string('a', 64)
        });

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromLibraryChartRef(chart)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
        Assert.AreEqual(1, stats.Chunks);
        Assert.IsNull(chart.GetBmsStorageOwner());
    }

    [TestMethod]
    public void ApplyReferenceMap_PackageEntryDoesNotMaterializeAdapterlessBmson()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Pending\chart.bmson",
            md5 = md5,
            sha256 = new string('a', 64)
        }));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromPackageChartEntry(entry)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
        Assert.AreEqual(1, stats.Chunks);
        Assert.IsNull(entry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void ApplyReferenceMap_PackageEntryMatchesBmsStorageOwnerWithoutMutation()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        TestableBmsFile file = CreateFile(md5);
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromPackageChartEntry(entry)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
    }

    [TestMethod]
    public void ApplyReferenceMap_PackageEntryBmsonDoesNotMutateBmsStorageOwner()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Pending\chart.bmson",
            md5 = md5,
            sha256 = new string('a', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromPackageChartEntry(entry)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
        Assert.IsNull(entry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void PlaylistReferenceIndex_FindsSha256OnlyBmsonReference()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(CreateEntry(null, new string('c', 64)));
        table.symbol = "BMSN";
        table.name = "Bmson Table";

        var index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        PlaylistReferenceDisplay display = index.Find(null, new string('c', 64));

        Assert.AreEqual("BMSN", display.Symbols);
        Assert.AreEqual("Bmson Table", display.Names);
    }

    [TestMethod]
    public void PlaylistReferenceIndex_FindsReferencesFromChartIdentity()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "dddddddddddddddddddddddddddddddd";
        string sha256 = new string('d', 64);
        BMSTable table = CreateTable(CreateEntry(md5, sha256));
        table.symbol = "ID";
        table.name = "Identity";
        PlaylistReferenceIndex index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        ChartFile chart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = md5,
            sha256 = sha256
        });
        LibraryChartRef chartRef = LibraryChartRef.FromChartFile(chart);

        Assert.AreEqual(1, index.Md5Count);
        Assert.AreEqual(0, index.Sha256Count);
        Assert.AreEqual("ID", index.Find(chart).Symbols);
        Assert.AreEqual("Identity", index.Find(chartRef).Names);
    }

    [TestMethod]
    public void PlaylistReferenceIndex_ReplacesTableEntries()
    {
        BMSTable table = CreateTable(CreateEntry(null, new string('d', 64)));
        table.symbol = "OLD";
        table.name = "Old Table";
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        Assert.AreEqual("OLD", index.Find(null, new string('d', 64)).Symbols);

        table.entries =
        [
            CreateEntry(null, new string('e', 64))
        ];
        table.symbol = "NEW";
        table.name = "New Table";
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        Assert.AreEqual(string.Empty, index.Find(null, new string('d', 64)).Symbols);
        Assert.AreEqual("NEW", index.Find(null, new string('e', 64)).Symbols);
    }

    [TestMethod]
    public void PlaylistReferenceOwner_AppliesCatalogReceiptToChartSnapshot()
    {
        string md5 = "11111111111111111111111111111111";
        BMSTable table = CreateTable(CreateEntry(md5));
        var owner = new BmsLibraryPlaylistReferenceOwner(2);
        var tableSnapshot = new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries);
        owner.AddReferenceBMSTable(
            tableSnapshot,
            owner.BuildReferenceLookupKeys(tableSnapshot),
            [],
            [],
            null);

        var receipt = new CatalogMutationReceipt(
            applied: true,
            new StorageRowsVersionSnapshot(0, 0),
            folderDbMs: 0,
            bmsPathDbMs: 0,
            bmsonPathDbMs: 0,
            bmsRemovalDbMs: 0,
            bmsonRemovalDbMs: 0,
            liveApplyMs: 0,
            ownedCollectionApplied: true,
            ownedCollectionVersion: 17,
            addedCharts:
            [
                new CatalogChartMutationFact(
                    ChartFileKind.Bms,
                    @"C:\\Library\\chart.bms",
                    md5,
                    null)
            ],
            pathFacts: [],
            removalRequests: []);

        PlaylistReferenceCatalogApplyResult result = owner.ApplyCatalogMutationReceipt(
            receipt,
            [new PlaylistReferenceChartSnapshot(md5, null)]);

        Assert.AreEqual(17, result.CatalogVersion);
        Assert.AreEqual(1, result.AffectedChartCount);
        Assert.AreEqual(1, result.MatchedChartCount);
    }

    [TestMethod]
    public void PlaylistReferenceOwner_ReplaceReferenceBMSTablePublishesNewSnapshot()
    {
        string oldMd5 = "22222222222222222222222222222222";
        string newMd5 = "33333333333333333333333333333333";
        BMSTable oldTable = CreateTable(CreateEntry(oldMd5));
        BMSTable newTable = CreateTable(CreateEntry(newMd5));
        oldTable.symbol = "OLD";
        newTable.symbol = "NEW";
        var owner = new BmsLibraryPlaylistReferenceOwner(2);
        var oldSnapshot = new PlaylistReferenceTableSnapshot(oldTable, oldTable.symbol, oldTable.name, oldTable.entries);
        var newSnapshot = new PlaylistReferenceTableSnapshot(newTable, newTable.symbol, newTable.name, newTable.entries);

        owner.AddReferenceBMSTable(oldSnapshot, owner.BuildReferenceLookupKeys(oldSnapshot), [], [], null);
        owner.ReplaceReferenceBMSTable(oldSnapshot, newSnapshot, [], [], null);

        Assert.AreEqual(string.Empty, owner.Find(oldMd5, null).Symbols);
        Assert.AreEqual("NEW", owner.Find(newMd5, null).Symbols);
    }

    [TestMethod]
    public void PlaylistReferenceIndex_FromSnapshotsDeduplicatesDuplicateEntriesForSameTable()
    {
        string md5 = "ffffffffffffffffffffffffffffffff";
        BMSTable table = CreateTable(
            CreateEntry(md5),
            CreateEntry(md5));
        table.symbol = "DUP";
        table.name = "Duplicate";

        PlaylistReferenceIndex index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        PlaylistReferenceDisplay display = index.Find(md5, null);

        Assert.AreEqual("DUP", display.Symbols);
        Assert.AreEqual("Duplicate", display.Names);
    }

    [TestMethod]
    public void GetPlaylistOrgMd5sForChart_ReturnsBmsAndBmsonMd5sInSameDirectory()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        string libraryDirectoryPath = Path.Combine(tempRootPath, "Library");
        string otherDirectoryPath = Path.Combine(tempRootPath, "Other");
        Directory.CreateDirectory(libraryDirectoryPath);
        Directory.CreateDirectory(otherDirectoryPath);
        try
        {
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            string sourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string includedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string bmsonHash = "cccccccccccccccccccccccccccccccc";
            string otherHash = "dddddddddddddddddddddddddddddddd";
            TestableBmsFile source = CreateLibraryFile(sourceHash, Path.Combine(libraryDirectoryPath, "source.bms"));
            TestableBmsFile included = CreateLibraryFile(includedHash, Path.Combine(libraryDirectoryPath, "included.bms"));
            TestableBmsFile otherDirectory = CreateLibraryFile(otherHash, Path.Combine(otherDirectoryPath, "other.bms"));
            var bmson = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(libraryDirectoryPath, "chart.bmson"),
                md5 = bmsonHash,
                sha256 = new string('c', 64)
            };
            var md5lessBmson = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(libraryDirectoryPath, "md5less.bmson"),
                md5 = null,
                sha256 = new string('e', 64)
            };
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [source, included, otherDirectory],
                BmsonSongs = [bmson, md5lessBmson]
            };

            List<string> orgMd5s = library.GetPlaylistOrgMd5sForChart(ChartFileProjection.FromBmsFile(source));

            CollectionAssert.AreEqual(new[] { sourceHash, includedHash, bmsonHash }, orgMd5s);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static BMSTable CreateTable(params TestablePlaylistEntry[] entries)
    {
        var table = new BMSTable
        {
            name = "Playlist",
            entries = [.. entries.Cast<BMSTableEntry>()]
        };
        return table;
    }

    private static PlaylistReferenceLookupKeys BuildReferenceLookupKeys(BMSTable table)
    {
        return new BmsLibraryPlaylistReferenceOwner(2).BuildReferenceLookupKeys(
            new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));
    }

    private static PlaylistReferenceLookupKeys BuildReferenceLookupKeys(IEnumerable<BMSTable> tables)
    {
        return new BmsLibraryPlaylistReferenceOwner(2).BuildReferenceLookupKeys(
            (tables ?? []).Where(table => table != null)
                .Select(table => new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)));
    }

    private static TestablePlaylistEntry CreateEntry(string? md5, string? sha256 = null)
    {
        var entry = new TestablePlaylistEntry();
        if (md5 != null)
        {
            entry.SetMd5(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        entry.folder = "Folder";
        return entry;
    }

    private static TestableBmsFile CreateFile(string hash, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = "C:\\Dummy\\" + Guid.NewGuid().ToString("N") + ".bms"
        };
        file.SetHash(hash);
        if (sha256 != null)
        {
            file.SetSha256(sha256);
        }
        return file;
    }

    private static TestableBmsFile CreateLibraryFile(string hash, string path, params string[] wavFiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        var file = new TestableBmsFile
        {
            path = path,
            WAVfiles = [.. wavFiles],
            BGAfiles = []
        };
        file.SetHash(hash);
        return file;
    }

    private static List<PlaylistReferenceChartSnapshot> ToChartSnapshots(IEnumerable<BMSFile> files)
    {
        return [.. files.Select(LibraryChartRef.FromBmsFile)
            .Select(PlaylistReferenceChartSnapshot.FromLibraryChartRef)
            .Where(chart => chart != null)];
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }
    }
}
