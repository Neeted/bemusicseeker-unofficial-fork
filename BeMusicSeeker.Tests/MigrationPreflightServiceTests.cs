using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MigrationPreflightServiceTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_LegacyPlaylistEntrySchema_RequiresWarningWithoutMutatingDatabase()
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

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsTrue(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(result.NeedsInitialSha256BackfillWarning);
            Assert.IsTrue(result.RequiresWarning);
            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_UpToDateSchema_DoesNotRequireWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(result.NeedsInitialSha256BackfillWarning);
            Assert.IsFalse(result.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_MissingPlaylistTables_RequiresWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsTrue(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsTrue(result.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    private static string CreateEmptySongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "MigrationPreflightServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended _ = new LR2SongDBExtended(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static string CreateTempSongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "MigrationPreflightServiceTests", Guid.NewGuid().ToString("N"));
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
