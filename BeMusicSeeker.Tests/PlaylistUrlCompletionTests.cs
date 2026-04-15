using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistUrlCompletionTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseMd5UrlMappingTsv_SkipsHeaderInvalidRowsAndKeepsFirstDuplicate()
    {
        string content = string.Join("\r\n", new[]
        {
            "md5\turl_diff\turl",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-a\thttps://example.com/main-a",
            "not-md5\thttps://example.com/diff-invalid\thttps://example.com/main-invalid",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-duplicate\thttps://example.com/main-duplicate",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\t\t",
            "cccccccccccccccccccccccccccccccc\t\thttps://example.com/main-c"
        });

        PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseMd5UrlMappingTsv(content);

        Assert.AreEqual(2, snapshot.CandidateCount);
        Assert.AreEqual(1, snapshot.DuplicateCount);
        Assert.AreEqual(2, snapshot.IgnoredRowCount);
        Assert.AreEqual(new Uri("https://example.com/main-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].Url);
        Assert.AreEqual(new Uri("https://example.com/diff-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].UrlDiff);
        Assert.AreEqual(new Uri("https://example.com/main-c"), snapshot.Candidates["cccccccccccccccccccccccccccccccc"].Url);
        Assert.IsNull(snapshot.Candidates["cccccccccccccccccccccccccccccccc"].UrlDiff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseStellaUploadJson_ParsesLongSubmissionAndKeepsFirstDuplicate()
    {
        string content = "[" +
            "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"submission\":9876543210123}," +
            "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"submission\":111}," +
            "{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"submission\":\"222\"}," +
            "{\"md5\":\"invalid\",\"submission\":333}" +
            "]";

        PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseStellaUploadJson(content);

        Assert.AreEqual(2, snapshot.CandidateCount);
        Assert.AreEqual(1, snapshot.DuplicateCount);
        Assert.AreEqual(1, snapshot.IgnoredRowCount);
        Assert.AreEqual(new Uri("https://stellabms.xyz/upload/9876543210123"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].UrlDiff);
        Assert.AreEqual(new Uri("https://stellabms.xyz/upload/222"), snapshot.Candidates["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"].UrlDiff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void TryResolveSourceUri_LocalFilePath_ResolvesFileUri()
    {
        string tempFilePath = Path.GetTempFileName();
        try
        {
            bool resolved = PlaylistUrlCompletionSupport.TryResolveSourceUri(tempFilePath, out Uri uri);

            Assert.IsTrue(resolved);
            Assert.IsNotNull(uri);
            Assert.IsTrue(uri.IsAbsoluteUri);
            Assert.AreEqual(Uri.UriSchemeFile, uri.Scheme);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_EffectiveUrlPrefersRuntimeCompletionWhenOverwriteEnabled()
    {
        BMSTableEntry entry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "SongA");
        entry.Url = new Uri("https://example.com/persisted");

        bool changed = entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), null, overwriteExisting: true);

        Assert.IsTrue(changed);
        Assert.AreEqual(new Uri("https://example.com/persisted"), entry.Url);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.RuntimeUrlCompletion);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ExplicitUrlEditClearsRuntimeCompletion()
    {
        BMSTableEntry entry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "SongB");
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        entry.Url = new Uri("https://example.com/manual");
        entry.Url_diff = new Uri("https://example.com/manual-diff");

        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.IsNull(entry.RuntimeUrlDiffCompletion);
        Assert.AreEqual(new Uri("https://example.com/manual"), entry.EffectiveUrl);
        Assert.AreEqual(new Uri("https://example.com/manual-diff"), entry.EffectiveUrlDiff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_OverwriteDisabledKeepsPersistedUrl()
    {
        BMSTableEntry entry = CreateEntry("cccccccccccccccccccccccccccccccc", "SongC");
        entry.Url = new Uri("https://example.com/persisted");

        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), null, overwriteExisting: false);

        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.AreEqual(new Uri("https://example.com/persisted"), entry.EffectiveUrl);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ApplyRuntimeUrlCompletion_ClearsWhenCandidateDisappears()
    {
        BMSTableEntry entry = CreateEntry("dddddddddddddddddddddddddddddddd", "SongD");
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        bool changed = entry.ApplyRuntimeUrlCompletion(null, null, overwriteExisting: false);

        Assert.IsTrue(changed);
        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.IsNull(entry.RuntimeUrlDiffCompletion);
        Assert.IsNull(entry.EffectiveUrl);
        Assert.IsNull(entry.EffectiveUrlDiff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void PlaylistDetailSourceRow_UsesEffectiveUrlForDisplay()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "SongE", 7);
        BMSTableEntry entry = new BMSTableEntry(file);
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        PlaylistDetailSourceRow sourceRow = new PlaylistDetailSourceRow(entry, file);

        Assert.AreEqual(new Uri("https://example.com/runtime"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), sourceRow.Url_diff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_LocalPlaylistMaterializesEffectiveUrlsIntoDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4001, "LocalTable");
            BMSTableEntry entry = CreateEntry("ffffffffffffffffffffffffffffffff", "LocalSong");
            table.entries = new List<BMSTableEntry> { entry };
            InsertPlaylistHeader(tempDbPath, table);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            playlist.CommitBMSTableEntry(entry);

            using LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath);
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single((BMSTableEntry row) => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.Url_diff);
            Assert.IsNull(entry.RuntimeUrlCompletion);
            Assert.IsNull(entry.RuntimeUrlDiffCompletion);
            Assert.AreEqual(new Uri("https://example.com/runtime"), storedEntry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), storedEntry.Url_diff);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_ExternalSyncPlaylistDoesNotMaterializeEffectiveUrls()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4002, "ExternalTable");
            table.Page_url = new Uri("https://example.com/page.html");
            table.Header_url = new Uri("https://example.com/header.json");
            table.Data_url = new Uri("https://example.com/data.json");
            table.EnableExternalSync();
            BMSTableEntry entry = CreateEntry("12121212121212121212121212121212", "ExternalSong");
            table.entries = new List<BMSTableEntry> { entry };
            InsertPlaylistHeader(tempDbPath, table);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            playlist.CommitBMSTableEntry(entry);

            using LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath);
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single((BMSTableEntry row) => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.IsNull(entry.Url);
            Assert.IsNull(entry.Url_diff);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
            Assert.IsNull(storedEntry.Url);
            Assert.IsNull(storedEntry.Url_diff);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_Sha256Identity_ReplacesExistingRowWithoutDuplicates()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4003, "ShaTable");
            InsertPlaylistHeader(tempDbPath, table);
            TestablePlaylistEntry first = CreateShaOnlyEntry("3434343434343434343434343434343434343434343434343434343434343434", "ShaSong", "memo-1");
            first.playlist_id = table.playlist_id;
            TestablePlaylistEntry second = CreateShaOnlyEntry("3434343434343434343434343434343434343434343434343434343434343434", "ShaSong", "memo-2");
            second.playlist_id = table.playlist_id;

            playlist.CommitBMSTableEntry(first);
            playlist.CommitBMSTableEntry(second);

            using LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath);
            List<BMSTableEntry> rows = db.Table<BMSTableEntry>().Where((BMSTableEntry row) => row.playlist_id == table.playlist_id && row.sha256 == second.sha256).ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("memo-2", rows[0].memo);
            Assert.IsNull(rows[0].md5);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ToDynamicJson_RoundTripsSha256()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("5656565656565656565656565656565656565656565656565656565656565656", "ShaRoundTrip", "memo");

        BMSTableEntry reloaded = new BMSTableEntry(entry.ToDynamicJson());

        Assert.AreEqual(entry.sha256, reloaded.sha256);
        Assert.AreEqual(entry.title, reloaded.title);
    }

    private static BMSTableEntry CreateEntry(string md5, string title)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(md5, title, 7);
        return new BMSTableEntry(file)
        {
            folder = string.Empty
        };
    }

    private static TestablePlaylistEntry CreateShaOnlyEntry(string sha256, string title, string memo)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
        {
            folder = string.Empty,
            memo = memo
        };
        entry.SetSha256(sha256);
        entry.SetTitle(title);
        return entry;
    }

    private static BMSTable CreateTable(int playlistId, string name)
    {
        return new BMSTable
        {
            playlist_id = playlistId,
            name = name,
            symbol = name.Substring(0, 1),
            Output_dir = name
        };
    }

    private static void InsertPlaylistHeader(string songDbPath, BMSTable table)
    {
        using LR2SongDBExtended db = new LR2SongDBExtended(songDbPath);
        db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
    }

    private static string CreateTempSongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistUrlCompletionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string sourceSongDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        File.Copy(sourceSongDbPath, tempDbPath, overwrite: true);
        return tempDbPath;
    }

    private static string CreateEmptySongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistUrlCompletionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended _ = new LR2SongDBExtended(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static void DeleteTempSongDbDirectory(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath))
        {
            return;
        }
        string directoryPath = Path.GetDirectoryName(songDbPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void ApplySnapshot(string snapshotHash, string snapshotTitle, int? snapshotMode)
        {
            hash = snapshotHash;
            path = snapshotTitle + ".bms";
            Title = snapshotTitle;
            Artist = "TestArtist";
            genre = "TestGenre";
            mode = snapshotMode;
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }
    }
}
