using System;
using System.Collections.Generic;
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
    public void BuildReferenceMaps_AndApplyReferenceMap_MatchesSongAndPendingFiles()
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

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        int addedRefs = service.ApplyReferenceMap(files, maps, out int matchedFiles, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(2, maps.Md5ToTablesMap.Count);
        Assert.AreEqual(0, maps.Sha256ToTablesMap.Count);
        Assert.AreEqual(2, matchedFiles);
        Assert.AreEqual(2, addedRefs);
        Assert.AreEqual(2, stats.Chunks);
        Assert.IsTrue(files[0].RefTables.Contains(table));
        Assert.IsTrue(files[1].RefTables.Contains(table));
        Assert.IsFalse(files[2].RefTables.Contains(table));
    }

    [TestMethod]
    public void ApplyReferenceMap_FallsBackToSha256WhenMd5IsMissing()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(CreateEntry(null, new string('a', 64)));
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('a', 64));

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        int addedRefs = service.ApplyReferenceMap([file], maps, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(0, maps.Md5ToTablesMap.Count);
        Assert.AreEqual(1, maps.Sha256ToTablesMap.Count);
        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, addedRefs);
        Assert.IsTrue(file.RefTables.Contains(table));
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

        PlaylistReferenceMaps maps = service.BuildReferenceMaps([md5Table, shaTable]);
        int addedRefs = service.ApplyReferenceMap([file], maps, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, addedRefs);
        Assert.IsTrue(file.RefTables.Contains(md5Table));
        Assert.IsFalse(file.RefTables.Contains(shaTable));
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

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        int addedRefs = service.ApplyReferenceMap([entry], maps, out int matchedCharts, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(0, addedRefs);
        Assert.AreEqual(1, stats.Chunks);
        Assert.IsNull(entry.GetCompatibilityAdapterForTest());
    }

    [TestMethod]
    public void ApplyReferenceMap_PackageEntryMutatesExistingCompatibilityAdapter()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        TestableBmsFile file = CreateFile(md5);
        PackageChartEntry entry = PackageChartEntry.FromChartAdapter(file);

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        int addedRefs = service.ApplyReferenceMap([entry], maps, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, addedRefs);
        Assert.IsTrue(file.RefTables.Contains(table));
    }

    [TestMethod]
    public void ApplyReferenceMap_PackageEntryDropsBmsonAdapterAndDoesNotMutateRefTables()
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

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        int addedRefs = service.ApplyReferenceMap([entry], maps, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(0, addedRefs);
        Assert.IsNull(entry.GetCompatibilityAdapterForTest());
    }

    [TestMethod]
    public void PlaylistReferenceIndex_FindsSha256OnlyBmsonReference()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(CreateEntry(null, new string('c', 64)));
        table.symbol = "BMSN";
        table.name = "Bmson Table";

        PlaylistReferenceMaps maps = service.BuildReferenceMaps(table, table.entries);
        var index = PlaylistReferenceIndex.FromReferenceMaps(maps);
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
        PlaylistReferenceIndex index = PlaylistReferenceIndex.FromReferenceMaps(service.BuildReferenceMaps(table, table.entries));
        ChartFile chart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = md5,
            sha256 = sha256
        });
        LibraryChartRef chartRef = LibraryChartRef.FromChartFile(chart);

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
        index.ReplaceTable(table, table.entries);

        Assert.AreEqual("OLD", index.Find(null, new string('d', 64)).Symbols);

        table.entries =
        [
            CreateEntry(null, new string('e', 64))
        ];
        table.symbol = "NEW";
        table.name = "New Table";
        index.ReplaceTable(table, table.entries);

        Assert.AreEqual(string.Empty, index.Find(null, new string('d', 64)).Symbols);
        Assert.AreEqual("NEW", index.Find(null, new string('e', 64)).Symbols);
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
