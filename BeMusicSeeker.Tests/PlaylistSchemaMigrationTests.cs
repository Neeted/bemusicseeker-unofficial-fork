using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSchemaMigrationTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_Constructor_DoesNotMigrateLegacyPlaylistEntrySchema()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
                db.CreateTable<LR2SongDBExtended.playlist>();
                db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
            }

            _ = new BMSPlaylist(tempDbPath);

            using var verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            Assert.IsFalse(uniqueIndexSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_EnsureSchema_MigratesLegacyPlaylistEntrySchema()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
                db.CreateTable<LR2SongDBExtended.playlist>();
                db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
            }

            BMSPlaylist.EnsureSchema(tempDbPath);

            using var verify = new LR2SongDBExtended(tempDbPath);
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
    public void BMSPlaylist_EnsureSchema_AddsPlaylistMetadataAndCourseStorage()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist_course>();
            }

            BMSPlaylist.EnsureSchema(tempDbPath);

            using var verify = new LR2SongDBExtended(tempDbPath);
            string playlistSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist';");
            StringAssert.Contains(playlistSql, "tag");
            StringAssert.Contains(playlistSql, "header_sha256");
            StringAssert.Contains(playlistSql, "data_sha256");
            StringAssert.Contains(playlistSql, "bmt_sort");
            StringAssert.Contains(playlistSql, "is_bmt_output");
            string courseSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_course';");
            StringAssert.Contains(courseSql, "course_json");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_course_idx_id';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_course_idx_uniq';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadPlaylistDump_RestoresLegacyDumpWithoutBmtColumns()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var playlist = new BMSPlaylist(tempDbPath);
            string separator = "\v" + Environment.NewLine;
            string legacyPlaylistDump =
                "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) "
                + "VALUES (1, 'Legacy', 'lg', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);";
            string dump = string.Join(separator, [legacyPlaylistDump, string.Empty, string.Empty]);

            playlist.LoadPlaylistDump(dump);

            using var verify = new LR2SongDBExtended(tempDbPath);
            BMSTable restored = verify.Table<BMSTable>().Single();
            Assert.AreEqual(1, restored.bmt_sort);
            Assert.AreEqual(true, restored.is_bmt_output);
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
            BMSPlaylist.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("DELETE FROM playlist_entry;");
                db.Execute("DELETE FROM playlist;");
            }
            string dump = playlist.GetPlaylistDump();

            playlist.LoadPlaylistDump(dump);

            using var verify = new LR2SongDBExtended(tempDbPath);
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
        using (var _ = new LR2SongDBExtended(tempDbPath))
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
}
