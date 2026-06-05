using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbWriterTests
{
    [TestMethod]
    public void UpsertGeneratedSong_SkipsUnchangedGeneratedColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");

            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            Assert.IsFalse(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Count());
        });
    }

    [TestMethod]
    public void UpsertGeneratedSong_UpdatesChangedGeneratedColumnsAndPreservesUserColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            file.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile updated = CreateSong(file.path, file.hash, "New");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, updated));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(7, row.favorite);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void UpsertGeneratedSongs_UpdatesChunkWithSingleExistingLookupAndPreservesUserColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            existing.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            TestableBmsFile updated = CreateSong(existing.path, existing.hash, "New");
            TestableBmsFile added = CreateSong(@"D:\BMS\Pack\added.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Added");

            int changed = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [updated, added]);

            Assert.AreEqual(2, changed);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            LR2SongDB.song updatedRow = songDb.Table<LR2SongDB.song>().Single(row => row.path == existing.path);
            Assert.AreEqual("New", updatedRow.title);
            Assert.AreEqual(7, updatedRow.favorite);
            Assert.AreEqual(12345, updatedRow.adddate);
            Assert.AreEqual("keep", updatedRow.tag);
            Assert.AreEqual("Added", songDb.Table<LR2SongDB.song>().Single(row => row.path == added.path).title);
        });
    }

    [TestMethod]
    public void UpsertGeneratedSong_TreatsNullTextFlagAsUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetTextFlagForTest(1);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile withoutTextFlag = CreateSong(file.path, file.hash, "Title");
            withoutTextFlag.CopyGeneratedHashesFrom(file);
            withoutTextFlag.SetTextFlagForTest(null);
            Assert.IsFalse(Lr2SongDbWriter.UpsertGeneratedSong(songDb, withoutTextFlag));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Single().txt);
        });
    }

    [TestMethod]
    public void UpsertGeneratedSong_NormalizesExistingUppercaseHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            songDb.Execute(
                "UPDATE song SET hash = ? WHERE path = ?;",
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                file.path);

            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", file.path));
        });
    }

    [TestMethod]
    public void UpsertGeneratedSong_DoesNotStatMissingDateAndFillsNewAddDate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Missing\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.date = null;
            file.adddate = null;

            int before = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow.AddSeconds(-1));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            int after = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow.AddSeconds(1));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.IsFalse(row.date.HasValue);
            Assert.IsTrue(row.adddate >= before && row.adddate <= after, "New rows should receive a current adddate.");
        });
    }

    [TestMethod]
    public void UpsertGeneratedSong_DoesNotStatMissingDateButPreservesExistingAddDate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Missing\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            file.adddate = 98765;
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile updated = CreateSong(file.path, file.hash, "New");
            updated.date = null;
            updated.adddate = null;
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, updated));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.IsFalse(row.date.HasValue);
            Assert.AreEqual(98765, row.adddate);
        });
    }

    private static TestableBmsFile CreateSong(string path, string hash, string title)
    {
        var file = new TestableBmsFile
        {
            path = path,
            type = 0,
            level = 12,
            difficulty = 3,
            date = 123456,
            exlevel = 0
        };
        file.SetHash(hash);
        file.SetTitleForTest(title);
        file.SetArtistForTest("Artist");
        file.SetTextFlagForTest(0);
        return file;
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbWriterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetTitleForTest(string value)
        {
            Title = value;
        }

        public void SetArtistForTest(string value)
        {
            Artist = value;
        }

        public void SetTextFlagForTest(int? value)
        {
            txt = value;
        }

        public void SetUserColumns(int? favoriteValue, int? addDateValue, string tagValue)
        {
            favorite = favoriteValue;
            adddate = addDateValue;
            tag = tagValue;
        }

        public void CopyGeneratedHashesFrom(BMSFile source)
        {
            folder = source.folder;
            parent = source.parent;
        }
    }
}
