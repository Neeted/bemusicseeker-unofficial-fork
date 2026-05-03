using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
            Assert.IsTrue(result.NeedsBmsonAppSchemaMigration);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RequiresWarning);

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'app_schema_version';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_CurrentVersion_DoesNotRequireWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            gateway.CompleteBmsonStartupMigration();

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(result.NeedsBmsonAppSchemaMigration);
            Assert.IsFalse(result.WarnRequired);
            Assert.IsFalse(result.RequiresWarning);
            Assert.IsFalse(result.RepairRequired);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_MissingVersionRow_RequiresWarningEvenWhenSchemaExists()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsTrue(result.NeedsBmsonAppSchemaMigration);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsFalse(result.RepairRequired);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CompleteBmsonStartupMigration_MissingVersionRowConvergesPreflight()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsTrue(before.NeedsBmsonAppSchemaMigration);

            gateway.CompleteBmsonStartupMigration();

            BmsonMigrationPreflightResult after = service.Inspect(tempDbPath);
            Assert.IsFalse(after.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(after.NeedsBmsonAppSchemaMigration);
            Assert.IsFalse(after.RepairRequired);
            Assert.IsFalse(after.WarnRequired);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_OrphanDigestRow_DoesNotRequireWarningWhenVersionCurrent()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            gateway.CompleteBmsonStartupMigration();

            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('d', 64)
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.WarnRequired);
            Assert.IsFalse(result.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void EnsureBmsonSchema_RebuildsInvalidAppOwnedTables()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
                db.Execute("CREATE TABLE bmson_song (path TEXT PRIMARY KEY, title TEXT NULL);");
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult invalidResult = service.Inspect(tempDbPath);
            Assert.IsTrue(invalidResult.RepairRequired);
            Assert.IsTrue(invalidResult.RepairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid));
            Assert.IsTrue(invalidResult.RepairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid));

            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();

            BmsonMigrationPreflightResult repairedResult = service.Inspect(tempDbPath);
            Assert.IsFalse(repairedResult.NeedsChartDigestMapSchema);
            Assert.IsFalse(repairedResult.NeedsBmsonSongSchema);
            Assert.IsFalse(repairedResult.RepairRequired);

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            string bmsonSongSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'bmson_song';");
            Assert.IsFalse(chartDigestMapSql.IndexOf("last_seen_path", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.IsFalse(chartDigestMapSql.IndexOf("updated_at", StringComparison.OrdinalIgnoreCase) >= 0);
            StringAssert.Contains(chartDigestMapSql, "sha256");
            StringAssert.Contains(bmsonSongSql, "mode_hint");
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'app_schema_version';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_md5';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_sha256';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_folder';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CompleteBmsonStartupMigration_NormalizesChartDigestMapAndSetsVersion()
    {
        string tempDbPath = CreateEmptySongDbPath();
        string chartPath = string.Empty;
        try
        {
            chartPath = Path.Combine(Path.GetDirectoryName(tempDbPath), "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Test\r\n");
            BMSPlaylist.EnsureSchema(tempDbPath);
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
                db.InsertOrReplace(file, typeof(LR2SongDB.song));
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, sha256 TEXT NULL, last_seen_path TEXT NULL, updated_at TEXT NULL);");
                db.Execute("INSERT INTO chart_digest_map(md5, sha256, last_seen_path, updated_at) VALUES ('" + file.hash + "', '" + file.sha256 + "', '" + chartPath.Replace("'", "''") + "', 'legacy');");
            }

            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.CompleteBmsonStartupMigration();

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            Assert.IsFalse(chartDigestMapSql.IndexOf("last_seen_path", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.IsFalse(chartDigestMapSql.IndexOf("updated_at", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'bmson_app_schema' AND version >= 1;"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CompleteBmsonStartupMigration_CurrentVersionWithInvalidDigestMapStillRepairsSchema()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.CompleteBmsonStartupMigration();
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.chart_digest_map>();
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsFalse(before.NeedsBmsonAppSchemaMigration);
            Assert.IsTrue(before.RepairRequired);

            gateway.CompleteBmsonStartupMigration();

            BmsonMigrationPreflightResult after = service.Inspect(tempDbPath);
            Assert.IsFalse(after.NeedsBmsonAppSchemaMigration);
            Assert.IsFalse(after.RepairRequired);
            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            StringAssert.Contains(chartDigestMapSql, "sha256");
            Assert.IsFalse(chartDigestMapSql.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void EnsureBmsonSchema_RecreatesMissingBmsonIndexesWithoutDroppingRows()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('b', 64)
                }, typeof(LR2SongDBExtended.chart_digest_map));
                db.InsertOrReplace(new LR2SongDBExtended.bmson_song
                {
                    path = "song-path",
                    folder = "folder",
                    title = "title",
                    subtitle = "subtitle",
                    artist = "artist",
                    genre = "genre",
                    level = 12,
                    mode_hint = "beat-7k",
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('c', 64),
                    banner = "banner.png",
                    backbmp = "back.png",
                    stagefile = "stage.png",
                    preview_music = "preview.ogg",
                    updated_at = DateTime.UtcNow
                }, typeof(LR2SongDBExtended.bmson_song));
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_md5;");
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_sha256;");
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_folder;");
            }

            gateway.EnsureBmsonSchema();

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_md5';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_sha256';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'bmson_song_idx_folder';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_PathOverload_DoesNotWaitForLr2SongDbExtendedMonitorLock()
    {
        string tempDbPath = CreateEmptySongDbPath();
        ManualResetEventSlim lockTaken = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim releaseLock = new ManualResetEventSlim(initialState: false);
        Task holderTask = Task.Run(delegate
        {
            Assert.IsTrue(LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5)));
            lockTaken.Set();
            try
            {
                releaseLock.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                LR2SongDBExtended.Unlock();
            }
        });
        try
        {
            Assert.IsTrue(lockTaken.Wait(TimeSpan.FromSeconds(5)));
            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            Stopwatch stopwatch = Stopwatch.StartNew();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);
            stopwatch.Stop();

            Assert.IsTrue(result.RequiresWarning);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Inspect should not block on LR2SongDBExtended monitor lock.");
        }
        finally
        {
            releaseLock.Set();
            holderTask.Wait(TimeSpan.FromSeconds(10));
            lockTaken.Dispose();
            releaseLock.Dispose();
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
