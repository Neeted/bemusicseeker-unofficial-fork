using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppSchemaPreflightServiceTests
{
    [TestMethod]
    public void BmsLibraryDbGatewaySqlQuote_PreservesLiteralEscapingBoundary()
    {
        Assert.AreEqual("'O''Reilly'", BmsLibraryDbGateway.SqlQuote("O'Reilly"));
        Assert.AreEqual("''", BmsLibraryDbGateway.SqlQuote("  "));
        Assert.AreEqual("''", BmsLibraryDbGateway.SqlQuote(null));
    }

    [TestMethod]
    public void EnsureLibraryStartupSchema_ContainsLibraryOwnedTables()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            new BmsLibraryDbGateway(tempDbPath).EnsureLibraryStartupSchema();

            using var db = new LR2SongDBExtended(tempDbPath);
            List<string> tableNames = [.. db.Query<ColumnNameRow>(
                "SELECT name FROM sqlite_master WHERE type = 'table';")
                .Select(row => row.name)];
            CollectionAssert.IsSubsetOf(
                new[] { "folder", "install", "ir_score", "ir_data", "chart_info" },
                tableNames);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_LegacyPlaylistEntrySchema_RequiresWarningWithoutMutatingDatabase()
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

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsTrue(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RequiresWarning);

            using var verify = new LR2SongDBExtended(tempDbPath);
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
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            gateway.RepairAppOwnedSchema();

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsFalse(result.NeedsAppSchemaVersionRepair);
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
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
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
    public void Inspect_FreshLr2Database_DoesNotRequireWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
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
    public void EnsureAppOwnedSchema_FreshLr2DatabaseConvergesPreflightWithoutDigestMigration()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);

            gateway.EnsureAppOwnedSchema();

            AppSchemaPreflightResult result = new AppSchemaPreflightService().Inspect(tempDbPath);
            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsFalse(result.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(result.RepairRequired);
            Assert.IsFalse(result.WarnRequired);

            using var verify = new LR2SongDBExtended(tempDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version >= 1;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'bmson_song';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_OldBmsonAppSchemaVersion_RequiresWarning()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureAppOwnedSchema();
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("UPDATE app_schema_version SET version = 0 WHERE name = 'app_schema';");
            }

            AppSchemaPreflightResult result = new AppSchemaPreflightService().Inspect(tempDbPath);

            Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RequiresWarning);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RepairAppOwnedSchema_MissingVersionRowConvergesPreflight()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsTrue(before.NeedsAppSchemaVersionRepair);

            gateway.RepairAppOwnedSchema();

            AppSchemaPreflightResult after = service.Inspect(tempDbPath);
            Assert.IsFalse(after.NeedsPlaylistEntrySha256Repair);
            Assert.IsFalse(after.NeedsAppSchemaVersionRepair);
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
    public void RepairAppOwnedSchema_DoesNotBackfillMissingDigestFromSongFiles()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(tempDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Test\r\n");
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                db.InsertOrReplace(file, typeof(LR2SongDB.song));
                db.CreateTable<LR2SongDBExtended.chart_digest_map>();
            }

            new BmsLibraryDbGateway(tempDbPath).RepairAppOwnedSchema();

            using var verify = new LR2SongDBExtended(tempDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version >= 1;"));
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
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            gateway.RepairAppOwnedSchema();

            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                    sha256 = new string('d', 64)
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

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
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
                db.Execute("CREATE TABLE bmson_song (path TEXT PRIMARY KEY, title TEXT NULL);");
            }

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult invalidResult = service.Inspect(tempDbPath);
            Assert.IsTrue(invalidResult.RepairRequired);
            Assert.IsTrue(invalidResult.RepairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid));
            Assert.IsTrue(invalidResult.RepairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid));

            new BmsLibraryDbGateway(tempDbPath).EnsureBmsonSchema();

            AppSchemaPreflightResult repairedResult = service.Inspect(tempDbPath);
            Assert.IsFalse(repairedResult.NeedsChartDigestMapSchema);
            Assert.IsFalse(repairedResult.NeedsBmsonSongSchema);
            Assert.IsFalse(repairedResult.RepairRequired);

            using var verify = new LR2SongDBExtended(tempDbPath);
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
    public void RepairAppOwnedSchema_NormalizesChartDigestMapAndSetsVersion()
    {
        string tempDbPath = CreateEmptySongDbPath();
        string md5 = string.Empty;
        string sha256 = string.Empty;
        try
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(tempDbPath), "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Test\r\n");
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                md5 = file.hash;
                sha256 = file.sha256;
                db.InsertOrReplace(file, typeof(LR2SongDB.song));
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, sha256 TEXT NULL, last_seen_path TEXT NULL, updated_at TEXT NULL);");
                db.Execute("INSERT INTO chart_digest_map(md5, sha256, last_seen_path, updated_at) VALUES ('" + md5 + "', '" + sha256 + "', '" + chartPath.Replace("'", "''") + "', 'legacy');");
            }

            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.RepairAppOwnedSchema();

            using var verify = new LR2SongDBExtended(tempDbPath);
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            Assert.IsFalse(chartDigestMapSql.IndexOf("last_seen_path", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.IsFalse(chartDigestMapSql.IndexOf("updated_at", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(sha256, verify.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5 = '" + md5 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version >= 1;"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RepairAppOwnedSchema_CurrentVersionWithInvalidDigestMapStillRepairsSchema()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.RepairAppOwnedSchema();
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.chart_digest_map>();
                db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
            }

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsFalse(before.NeedsAppSchemaVersionRepair);
            Assert.IsTrue(before.RepairRequired);

            gateway.RepairAppOwnedSchema();

            AppSchemaPreflightResult after = service.Inspect(tempDbPath);
            Assert.IsFalse(after.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(after.RepairRequired);
            using var verify = new LR2SongDBExtended(tempDbPath);
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
    public void RepairAppOwnedSchema_LateFailureRollsBackNormalizationSchemaVersionDigestAndData()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            SeedLateFailureSchemaRepairDatabase(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            SchemaRepairSnapshot before = ReadSchemaRepairSnapshot(tempDbPath);
            Exception failure = null;
            try
            {
                gateway.RepairAppOwnedSchema();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            SchemaRepairSnapshot afterFailure = ReadSchemaRepairSnapshot(tempDbPath);
            Assert.AreEqual(before.PlaylistEntryTableSql, afterFailure.PlaylistEntryTableSql);
            Assert.AreEqual(before.PlaylistEntryUniqueIndexSql, afterFailure.PlaylistEntryUniqueIndexSql);
            Assert.AreEqual(before.PlaylistEntrySha256IndexCount, afterFailure.PlaylistEntrySha256IndexCount);
            Assert.AreEqual(before.AppSchemaVersion, afterFailure.AppSchemaVersion);
            Assert.AreEqual(before.DigestSha256, afterFailure.DigestSha256);
            Assert.AreEqual(before.PlaylistBmtSort, afterFailure.PlaylistBmtSort);
            Assert.AreEqual(before.PlaylistIsBmtOutput, afterFailure.PlaylistIsBmtOutput);
            Assert.AreEqual(before.EntryTitle, afterFailure.EntryTitle);
            Assert.AreEqual(before.PlaylistRowCount, afterFailure.PlaylistRowCount);
            Assert.AreEqual(before.PlaylistEntryRowCount, afterFailure.PlaylistEntryRowCount);
            Assert.AreEqual(before.DigestRowCount, afterFailure.DigestRowCount);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RepairAppOwnedSchema_LateFailureRetryConvergesOnSameDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            SeedLateFailureSchemaRepairDatabase(tempDbPath);
            SchemaRepairSnapshot before = ReadSchemaRepairSnapshot(tempDbPath);
            Exception failure = null;
            try
            {
                new BmsLibraryDbGateway(tempDbPath).RepairAppOwnedSchema();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            using (var removeFailure = new LR2SongDBExtended(tempDbPath))
            {
                removeFailure.Execute("DROP TRIGGER app_schema_repair_late_failure;");
            }

            new BmsLibraryDbGateway(tempDbPath).RepairAppOwnedSchema();

            AppSchemaPreflightResult result = new AppSchemaPreflightService().Inspect(tempDbPath);
            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsFalse(result.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(result.RepairRequired);
            SchemaRepairSnapshot afterRetry = ReadSchemaRepairSnapshot(tempDbPath);
            StringAssert.Contains(afterRetry.PlaylistEntryTableSql, "sha256");
            StringAssert.Contains(afterRetry.PlaylistEntryUniqueIndexSql, "sha256");
            Assert.AreEqual(1L, afterRetry.PlaylistEntrySha256IndexCount);
            Assert.AreEqual(1, afterRetry.AppSchemaVersion);
            Assert.AreEqual(before.DigestSha256, afterRetry.DigestSha256);
            Assert.AreEqual(1, afterRetry.PlaylistBmtSort);
            Assert.IsTrue(afterRetry.PlaylistIsBmtOutput);
            Assert.AreEqual(before.EntryTitle, afterRetry.EntryTitle);
            Assert.AreEqual(before.PlaylistRowCount, afterRetry.PlaylistRowCount);
            Assert.AreEqual(before.PlaylistEntryRowCount, afterRetry.PlaylistEntryRowCount);
            Assert.AreEqual(before.DigestRowCount, afterRetry.DigestRowCount);
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
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureBmsonSchema();
            using (var db = new LR2SongDBExtended(tempDbPath))
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

            using var verify = new LR2SongDBExtended(tempDbPath);
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
        var lockTaken = new ManualResetEventSlim(initialState: false);
        var releaseLock = new ManualResetEventSlim(initialState: false);
        var holderReady = new ManualResetEventSlim(initialState: false);
        ExceptionDispatchInfo? holderFailure = null;
        ExceptionDispatchInfo? primaryFailure = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        bool holderStarted = false;
        bool holderJoined = false;
        var holderThread = new Thread((ThreadStart)delegate
        {
            bool lockAcquired = false;
            try
            {
                try
                {
                    lockAcquired = LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5));
                    if (!lockAcquired)
                    {
                        throw new TimeoutException("The LR2SongDBExtended monitor lock holder could not acquire its lock.");
                    }

                    lockTaken.Set();
                }
                catch (Exception exception)
                {
                    holderFailure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    holderReady.Set();
                }

                if (lockAcquired && !releaseLock.Wait(TimeSpan.FromSeconds(30)))
                {
                    holderFailure ??= ExceptionDispatchInfo.Capture(
                        new TimeoutException("The LR2SongDBExtended monitor lock holder was not released in time."));
                }
            }
            catch (Exception exception)
            {
                holderFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        LR2SongDBExtended.Unlock();
                    }
                    catch (Exception exception)
                    {
                        holderFailure ??= ExceptionDispatchInfo.Capture(exception);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = nameof(Inspect_PathOverload_DoesNotWaitForLr2SongDbExtendedMonitorLock) + ".LockHolder"
        };

        void CaptureCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        try
        {
            holderThread.Start();
            holderStarted = true;
            Assert.IsTrue(holderReady.Wait(TimeSpan.FromSeconds(5)), "The dedicated lock-holder thread did not start in time.");
            holderFailure?.Throw();
            Assert.IsTrue(lockTaken.IsSet, "The dedicated lock-holder thread did not acquire the LR2SongDBExtended monitor lock.");

            var service = new AppSchemaPreflightService();
            var stopwatch = Stopwatch.StartNew();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);
            stopwatch.Stop();

            Assert.IsFalse(result.RequiresWarning);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Inspect should not block on LR2SongDBExtended monitor lock.");
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            CaptureCleanup(releaseLock.Set);
            if (holderStarted)
            {
                CaptureCleanup(() =>
                {
                    holderJoined = holderThread.Join(TimeSpan.FromSeconds(10));
                    Assert.IsTrue(holderJoined, "The dedicated lock-holder thread did not terminate in time.");
                });
            }

            if (holderJoined || !holderStarted)
            {
                CaptureCleanup(lockTaken.Dispose);
                CaptureCleanup(releaseLock.Dispose);
                CaptureCleanup(holderReady.Dispose);
            }
            CaptureCleanup(() => DeleteTempSongDbDirectory(tempDbPath));

            primaryFailure ??= holderFailure;
            primaryFailure ??= cleanupFailure;
        }

        primaryFailure?.Throw();
    }

    [TestMethod]
    public void EnsureMaintenanceSchema_AddsLr2CompatibilityColumnsWithoutDroppingRows()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("CREATE TABLE maintenance (hash TEXT NULL, path TEXT PRIMARY KEY, encoding TEXT NULL, is_files_warning_ignored INTEGER NOT NULL DEFAULT 0);");
                db.Execute("INSERT INTO maintenance (hash, path, encoding, is_files_warning_ignored) VALUES ('0123456789abcdef0123456789abcdef', 'D:\\BMS\\chart.bms', 'shift_jis', 0);");
            }

            new BmsLibraryDbGateway(tempDbPath).EnsureMaintenanceSchema();

            using var verify = new LR2SongDBExtended(tempDbPath);
            string[] columns = [.. verify.Query<ColumnNameRow>("PRAGMA table_info(maintenance);").Select(row => row.name)];
            CollectionAssert.Contains(columns, "lr2_warning_flags");
            CollectionAssert.Contains(columns, "lr2_resource_max_relative_cp932_bytes");
            CollectionAssert.Contains(columns, "lr2_resource_has_parent_traversal");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = 'D:\\BMS\\chart.bms';"));
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;",
                    BmsLibraryDbGateway.MaintenancePathNocaseIndexName));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    public void UpsertMaintenanceInfos_AddsLr2CompatibilityColumnsAndRoundTripsValues()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.Execute("CREATE TABLE maintenance (hash TEXT NULL, path TEXT PRIMARY KEY, encoding TEXT NULL, is_files_warning_ignored INTEGER NOT NULL DEFAULT 0);");
            }
            var info = new BMSFileMaintenanceInfo
            {
                path = @"D:\BMS\chart.bms",
                hash = "0123456789abcdef0123456789abcdef",
                lr2_warning_flags = 5,
                lr2_resource_max_relative_cp932_bytes = 12,
                lr2_resource_has_parent_traversal = true
            };

            new BmsLibraryDbGateway(tempDbPath).UpsertMaintenanceInfos([info]);

            using var verify = new LR2SongDBExtended(tempDbPath);
            BMSFileMaintenanceInfo row = verify.Table<BMSFileMaintenanceInfo>().Single(item => item.path == info.path);
            Assert.AreEqual(5, row.lr2_warning_flags);
            Assert.AreEqual(12, row.lr2_resource_max_relative_cp932_bytes);
            Assert.AreEqual(true, row.lr2_resource_has_parent_traversal);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    public void CommitFileScanDiffChunk_BulkUpsertsMaintenanceRowsWithExactCaseDistinctPaths()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var chunk = new FileScanDiffCommitChunk();
            chunk.AddMaintenanceInfoRow(new BMSFileMaintenanceInfo
            {
                path = @"D:\BMS\Pack\Song\Chart.bms",
                hash = "11111111111111111111111111111111",
                encoding = "shift_jis",
                wav_files_defined = 1,
                wav_files_existing = 0,
                is_stagefile_defined = true,
                is_stagefile_existing = false,
                is_files_warning_ignored = false
            }, countMutation: true);
            chunk.AddMaintenanceInfoRow(new BMSFileMaintenanceInfo
            {
                path = @"D:\BMS\Pack\Song\chart.bms",
                hash = "22222222222222222222222222222222",
                encoding = "utf-8",
                wav_files_defined = 2,
                wav_files_existing = 2,
                is_stagefile_defined = true,
                is_stagefile_existing = true,
                is_files_warning_ignored = true,
                lr2_warning_flags = 4,
                lr2_resource_max_relative_cp932_bytes = 120
            }, countMutation: true);

            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                BmsLibraryDbGateway.CommitFileScanDiffChunk(db, chunk);
            }

            using var verify = new LR2SongDBExtended(tempDbPath);
            List<BMSFileMaintenanceInfo> rows = [.. verify.Table<BMSFileMaintenanceInfo>().OrderBy(row => row.path, StringComparer.Ordinal)];
            Assert.AreEqual(2, rows.Count);
            BMSFileMaintenanceInfo upperCaseRow = rows.Single(row => row.path == @"D:\BMS\Pack\Song\Chart.bms");
            Assert.AreEqual("11111111111111111111111111111111", upperCaseRow.hash);
            Assert.AreEqual("shift_jis", upperCaseRow.encoding);
            Assert.AreEqual(1, upperCaseRow.wav_files_defined);
            Assert.AreEqual(0, upperCaseRow.wav_files_existing);
            Assert.AreEqual(true, upperCaseRow.is_stagefile_defined);
            Assert.AreEqual(false, upperCaseRow.is_stagefile_existing);
            Assert.IsFalse(upperCaseRow.is_files_warning_ignored);

            BMSFileMaintenanceInfo lowerCaseRow = rows.Single(row => row.path == @"D:\BMS\Pack\Song\chart.bms");
            Assert.AreEqual("22222222222222222222222222222222", lowerCaseRow.hash);
            Assert.AreEqual("utf-8", lowerCaseRow.encoding);
            Assert.AreEqual(2, lowerCaseRow.wav_files_defined);
            Assert.AreEqual(2, lowerCaseRow.wav_files_existing);
            Assert.AreEqual(true, lowerCaseRow.is_stagefile_defined);
            Assert.AreEqual(true, lowerCaseRow.is_stagefile_existing);
            Assert.IsTrue(lowerCaseRow.is_files_warning_ignored);
            Assert.AreEqual(4, lowerCaseRow.lr2_warning_flags);
            Assert.AreEqual(120, lowerCaseRow.lr2_resource_max_relative_cp932_bytes);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    private static string CreateEmptySongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "AppSchemaPreflightServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (var _ = new LR2SongDBExtended(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static void SeedLateFailureSchemaRepairDatabase(string songDbPath)
    {
        var gateway = new BmsLibraryDbGateway(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        gateway.EnsureAppOwnedSchema();

        using var seed = new LR2SongDBExtended(songDbPath);
        seed.DropTable<LR2SongDBExtended.playlist_entry>();
        seed.Execute(
            "CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
        seed.Execute(
            "CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
        seed.Execute(
            "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, is_external_sync, is_root_folder, bmt_sort, is_bmt_output) "
            + "VALUES (91, 'Legacy playlist', 'legacy', '', 0, 1, 0, 0, 0, NULL, NULL);");
        seed.Execute(
            "INSERT INTO playlist_entry (playlist_id, md5, level, title, artist, folder, is_removed) "
            + "VALUES (91, '0123456789abcdef0123456789abcdef', 12, 'Legacy entry', 'Artist', '', 0);");
        seed.Execute(
            "INSERT OR REPLACE INTO chart_digest_map (md5, sha256) "
            + "VALUES ('0123456789abcdef0123456789abcdef', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');");
        seed.Execute("UPDATE app_schema_version SET version = 0 WHERE name = 'app_schema';");
        seed.Execute(
            "CREATE TRIGGER app_schema_repair_late_failure BEFORE INSERT ON app_schema_version "
            + "BEGIN SELECT RAISE(ABORT, 'deterministic late schema repair failure'); END;");
    }

    private static void DeleteTempSongDbDirectory(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath))
        {
            return;
        }
        string directoryPath = Path.GetDirectoryName(songDbPath)!;
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static SchemaRepairSnapshot ReadSchemaRepairSnapshot(string songDbPath)
    {
        using var db = new LR2SongDBExtended(songDbPath);
        SchemaRepairRow row = db.Query<SchemaRepairRow>(
                "SELECT bmt_sort AS BmtSort, is_bmt_output AS IsBmtOutput FROM playlist WHERE playlist_id = 91;")
            .Single();
        return new SchemaRepairSnapshot(
            db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';"),
            db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';"),
            db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"),
            db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema';"),
            db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5 = '0123456789abcdef0123456789abcdef';"),
            row.BmtSort,
            row.IsBmtOutput,
            db.ExecuteScalar<string>("SELECT title FROM playlist_entry WHERE playlist_id = 91;") ?? string.Empty,
            db.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist;"),
            db.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry;"),
            db.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
    }

    private sealed class SchemaRepairRow
    {
        public int? BmtSort { get; set; }

        public bool? IsBmtOutput { get; set; }
    }

    private sealed record SchemaRepairSnapshot(
        string PlaylistEntryTableSql,
        string PlaylistEntryUniqueIndexSql,
        long PlaylistEntrySha256IndexCount,
        int AppSchemaVersion,
        string DigestSha256,
        int? PlaylistBmtSort,
        bool? PlaylistIsBmtOutput,
        string EntryTitle,
        long PlaylistRowCount,
        long PlaylistEntryRowCount,
        long DigestRowCount);

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }
}
