using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPlaylistReferenceServiceTests
{
    [TestMethod]
    public void BuildMd5ToTablesMap_AndApplyReferenceMap_MatchesSongAndPendingFiles()
    {
        BmsLibraryPlaylistReferenceService service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("cccccccccccccccccccccccccccccccc")
        };

        Dictionary<string, BMSTable[]> map = service.BuildMd5ToTablesMap(table, table.entries);
        int addedRefs = service.ApplyReferenceMap(files, map, out int matchedFiles, out PlaylistReferenceApplyStats stats);

        Assert.AreEqual(2, map.Count);
        Assert.AreEqual(2, matchedFiles);
        Assert.AreEqual(2, addedRefs);
        Assert.AreEqual(2, stats.Chunks);
        Assert.IsTrue(files[0].RefTables.Contains(table));
        Assert.IsTrue(files[1].RefTables.Contains(table));
        Assert.IsFalse(files[2].RefTables.Contains(table));
    }

    private static BMSTable CreateTable(params TestablePlaylistEntry[] entries)
    {
        BMSTable table = new BMSTable
        {
            name = "Playlist"
        };
        table.entries = entries.Cast<BMSTableEntry>().ToList();
        return table;
    }

    private static TestablePlaylistEntry CreateEntry(string md5)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5(md5);
        entry.folder = "Folder";
        return entry;
    }

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile
        {
            path = "C:\\Dummy\\" + Guid.NewGuid().ToString("N") + ".bms"
        };
        file.SetHash(hash);
        return file;
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetMd5(string value)
        {
            md5 = value;
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
