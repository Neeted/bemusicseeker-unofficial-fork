using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSchemaMigrationTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_Constructor_MigratesLegacyPlaylistEntrySchema()
    {
        string tempDbPath = CreateTempSongDbPath();
        try
        {
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
                db.CreateTable<LR2SongDBExtended.playlist>();
                db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
            }

            _ = new BMSPlaylist(tempDbPath);

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            StringAssert.Contains(tableSql, "sha256");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            StringAssert.Contains(uniqueIndexSql, "sha256");
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadPlaylistDump_RestoresSha256Indexes()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist playlist = new BMSPlaylist(tempDbPath);
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("DELETE FROM playlist_entry;");
                db.Execute("DELETE FROM playlist;");
            }
            string dump = playlist.GetPlaylistDump();

            playlist.LoadPlaylistDump(dump);

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            StringAssert.Contains(tableSql, "sha256");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            StringAssert.Contains(uniqueIndexSql, "sha256");
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    private static string CreateEmptySongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistSchemaMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended _ = new LR2SongDBExtended(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static string CreateTempSongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistSchemaMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string sourceSongDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        File.Copy(sourceSongDbPath, tempDbPath, overwrite: true);
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
}
