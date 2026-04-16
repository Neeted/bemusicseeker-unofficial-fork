using System;
using System.IO;
using System.Diagnostics;
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
            Assert.IsTrue(result.NeedsChartDigestMapSchema);
            Assert.IsTrue(result.NeedsBmsonSongSchema);
            Assert.IsTrue(result.NeedsInitialSha256BackfillWarning);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RepairRequired);
            Assert.IsTrue(result.RequiresWarning);
            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
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
            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(result.NeedsChartDigestMapSchema);
            Assert.IsFalse(result.NeedsBmsonSongSchema);
            Assert.IsFalse(result.NeedsInitialSha256BackfillWarning);
            Assert.IsFalse(result.WarnRequired);
            Assert.IsFalse(result.RepairRequired);
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
            Assert.IsTrue(result.NeedsChartDigestMapSchema);
            Assert.IsTrue(result.NeedsBmsonSongSchema);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RepairRequired);
            Assert.IsTrue(result.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_MissingChartDigestMapOrBackfill_RequiresWarning()
    {
        string tempDbPath = CreateTempSongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult missingSchemaResult = service.Inspect(tempDbPath);

            Assert.IsFalse(missingSchemaResult.NeedsPlaylistEntrySha256Migration);
            Assert.IsTrue(missingSchemaResult.NeedsChartDigestMapSchema);
            Assert.IsTrue(missingSchemaResult.NeedsBmsonSongSchema);
            Assert.IsTrue(missingSchemaResult.NeedsInitialSha256BackfillWarning);
            Assert.IsTrue(missingSchemaResult.WarnRequired);
            Assert.IsTrue(missingSchemaResult.RepairRequired);

            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('a', 64),
                    last_seen_path = "dummy",
                    updated_at = DateTime.UtcNow
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            BmsonMigrationPreflightResult partiallyBackfilledResult = service.Inspect(tempDbPath);
            Assert.IsFalse(partiallyBackfilledResult.NeedsChartDigestMapSchema);
            Assert.IsFalse(partiallyBackfilledResult.NeedsBmsonSongSchema);
            Assert.IsTrue(partiallyBackfilledResult.NeedsInitialSha256BackfillWarning);
            Assert.IsTrue(partiallyBackfilledResult.WarnRequired);
            Assert.IsFalse(partiallyBackfilledResult.RepairRequired);
            Assert.IsTrue(partiallyBackfilledResult.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_MissingBmsonSongOnly_RequiresRepairWithoutWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.Execute("INSERT INTO song(path, hash, folder) VALUES ('chart-1.bms', '0e5751c026e543b2e8ab2eb06099daa1', 'folder');");
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('d', 64),
                    last_seen_path = "chart-1.bms",
                    updated_at = DateTime.UtcNow
                }, typeof(LR2SongDBExtended.chart_digest_map));
                db.DropTable<LR2SongDBExtended.bmson_song>();
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Migration);
            Assert.IsFalse(result.NeedsChartDigestMapSchema);
            Assert.IsTrue(result.NeedsBmsonSongSchema);
            Assert.IsFalse(result.NeedsInitialSha256BackfillWarning);
            Assert.IsTrue(result.RepairRequired);
            Assert.IsFalse(result.WarnRequired);
            Assert.IsFalse(result.RequiresWarning);
            Assert.AreEqual(RepairableBmsonSchemaIssues.BmsonSongTableMissing, result.RepairableBmsonSchemaIssues);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_ChartDigestMapPresent_ReturnsBackfillWarningWithoutScanningBmsonSchemaOnlyState()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            BMSPlaylist.EnsureSchema(tempDbPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            using (LR2SongDBExtended db = new LR2SongDBExtended(tempDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.Execute("INSERT INTO song(path, hash, folder) VALUES ('chart-1.bms', '0e5751c026e543b2e8ab2eb06099daa1', 'folder');");
                db.Execute("INSERT INTO song(path, hash, folder) VALUES ('chart-2.bms', '7d793037a0760186574b0282f2f435e7', 'folder');");
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('a', 64),
                    last_seen_path = "chart-1.bms",
                    updated_at = DateTime.UtcNow
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            BmsonMigrationPreflightService service = new BmsonMigrationPreflightService();
            BmsonMigrationPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsChartDigestMapSchema);
            Assert.IsFalse(result.NeedsBmsonSongSchema);
            Assert.IsTrue(result.NeedsInitialSha256BackfillWarning);
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
    public void BuildMissingSha256BackfillExistsSql_UsesDirectMd5ComparisonWithoutLowerCalls()
    {
        string sql = BmsonMigrationPreflightService.BuildMissingSha256BackfillExistsSql();

        StringAssert.Contains(sql, "SELECT EXISTS(");
        StringAssert.Contains(sql, "WHERE d.md5 = s.hash");
        Assert.IsFalse(sql.IndexOf("lower(", StringComparison.OrdinalIgnoreCase) >= 0, "The backfill warning query must stay index-friendly and avoid lower(...) calls.");
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
            Assert.IsFalse(repairedResult.WarnRequired);

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            string bmsonSongSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'bmson_song';");
            StringAssert.Contains(chartDigestMapSql, "sha256");
            StringAssert.Contains(chartDigestMapSql, "last_seen_path");
            StringAssert.Contains(bmsonSongSql, "mode_hint");
            StringAssert.Contains(bmsonSongSql, "sha256");
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_digest_map_idx_sha256';"));
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
    public void EnsureBmsonSchema_RecreatesMissingIndexesWithoutDroppingRows()
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
                    sha256 = new string('b', 64),
                    last_seen_path = "digest-path",
                    updated_at = DateTime.UtcNow
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
                db.Execute("DROP INDEX IF EXISTS chart_digest_map_idx_sha256;");
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_md5;");
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_sha256;");
                db.Execute("DROP INDEX IF EXISTS bmson_song_idx_folder;");
            }

            gateway.EnsureBmsonSchema();

            using LR2SongDBExtended verify = new LR2SongDBExtended(tempDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_digest_map_idx_sha256';"));
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

            Assert.IsTrue(result.RepairRequired);
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
