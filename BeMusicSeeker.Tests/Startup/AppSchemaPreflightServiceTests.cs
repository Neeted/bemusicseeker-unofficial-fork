using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppSchemaPreflightServiceTests
{
    /// <summary>
    /// startup schemaの表・索引・現行版を新しい読取り接続で確認し、刷新履歴の初回保存・読戻しと起動対象の準備漏れを検出する。
    /// </summary>
    [TestMethod]
    public void EnsureLibraryStartupSchema_ContainsLibraryOwnedTables()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureLibraryStartupSchema();
            AssertIrScoreRefreshMetadataFirstSave(gateway);

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            AssertLibraryStartupSchema(db);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 起動準備終盤の実SQLite障害で今回の表・索引・列・版を全て戻し、元の行を保持して原因除去後の明示再実行を成立させます。
    /// </summary>
    [TestMethod]
    public void EnsureLibraryStartupSchema_LateFailureRollsBackAllChangesAndAllowsExplicitRetry()
    {
        string tempDbPath = CreateEmptySongDbPath();
        const string hash = "0123456789abcdef0123456789abcdef";
        const string chartPath = @"D:\BMS\startup-original.bms";
        const string failureMessage = "deterministic startup schema late failure";
        try
        {
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(tempDbPath, seed =>
            {
                seed.CreateTable<LR2SongDB.song>();
                seed.Insert(new LR2SongDB.song { hash = hash, path = chartPath, title = "Original startup song" });
                seed.Execute("CREATE TABLE maintenance (hash TEXT NULL, path TEXT PRIMARY KEY, encoding TEXT NULL, is_files_warning_ignored INTEGER NOT NULL DEFAULT 0);");
                seed.Execute("INSERT INTO maintenance (hash, path, encoding, is_files_warning_ignored) VALUES (?, ?, 'shift_jis', 1);", hash, chartPath);
                seed.CreateTable<LR2SongDBExtended.app_schema_version>();
                seed.Insert(new LR2SongDBExtended.app_schema_version { name = "app_schema", version = 0 });
                seed.Execute("CREATE TRIGGER startup_schema_late_failure BEFORE INSERT ON app_schema_version BEGIN SELECT RAISE(ABORT, '" + failureMessage + "'); END;");
            });

            var gateway = new BmsLibraryDbGateway(tempDbPath);
            string[] beforeSchema;
            string[] beforeColumns;
            using (LR2SongDBExtended before = gateway.OpenSongDbReadOnly())
            {
                beforeSchema = ReadLogicalSchema(before);
                beforeColumns = ReadMaintenanceColumnDefinitions(before);
            }

            Exception? failure = null;
            try { gateway.EnsureLibraryStartupSchema(); }
            catch (Exception exception) { failure = exception; }
            Assert.IsNotNull(failure);
            bool sqliteCausePreserved = false;
            for (Exception? cause = failure; cause != null; cause = cause.InnerException)
            {
                sqliteCausePreserved |= cause is SQLiteException && cause.Message.Contains(failureMessage, StringComparison.Ordinal);
            }
            Assert.IsTrue(sqliteCausePreserved, failure.ToString());

            using (LR2SongDBExtended afterFailure = gateway.OpenSongDbReadOnly())
            {
                CollectionAssert.AreEqual(beforeSchema, ReadLogicalSchema(afterFailure));
                CollectionAssert.AreEqual(beforeColumns, ReadMaintenanceColumnDefinitions(afterFailure));
                Assert.AreEqual(0, afterFailure.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema';"));
                AssertOriginalRows(afterFailure);
            }
            using (var removeFailure = new LR2SongDBExtended(tempDbPath))
            {
                removeFailure.Execute("DROP TRIGGER startup_schema_late_failure;");
            }

            gateway.EnsureLibraryStartupSchema();
            using LR2SongDBExtended afterRetry = gateway.OpenSongDbReadOnly();
            AssertLibraryStartupSchema(afterRetry);
            AssertOriginalRows(afterRetry);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }

        static void AssertOriginalRows(LR2SongDBExtended db)
        {
            LR2SongDB.song song = db.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(hash, song.hash);
            Assert.AreEqual(chartPath, song.path);
            Assert.AreEqual("Original startup song", song.title);
            LR2SongDBExtended.maintenance maintenance = db.Table<LR2SongDBExtended.maintenance>().Single();
            Assert.AreEqual(hash, maintenance.hash);
            Assert.AreEqual(chartPath, maintenance.path);
            Assert.AreEqual("shift_jis", maintenance.encoding);
            Assert.IsTrue(maintenance.is_files_warning_ignored);
        }
    }

    private static void AssertLibraryStartupSchema(LR2SongDBExtended db)
    {
        string[] tableNames = db.Query<ColumnNameRow>("SELECT name FROM sqlite_master WHERE type = 'table';")
            .Select(row => row.name).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "song", "folder", "install", "maintenance", "ir_score", "ir_score_refresh_metadata", "ir_data",
            "chart_info", "chart_info_parse_failure", "chart_info_import_history", "app_schema_version" }, tableNames);
        string[] indexNames = db.Query<ColumnNameRow>("SELECT name FROM sqlite_master WHERE type = 'index';")
            .Select(row => row.name).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "hashidx", "parentidx", "song_idx_folder", "song_idx_path_nocase", "hashidx_mtn", "maintenance_idx_path_nocase",
            "ir_data_idx", "ir_data_idx_lr2id_hash", "chart_info_idx_md5", "chart_info_idx_charthash", "chart_info_idx_parser_version",
            "chart_info_parse_failure_idx_sha256", "chart_info_parse_failure_idx_parser_version", "chart_info_import_history_idx_bundle_sha256" }, indexNames);
        Assert.AreEqual(BmsLibraryDbGateway.CurrentAppSchemaVersion,
            db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema';"));
    }

    private static string[] ReadLogicalSchema(LR2SongDBExtended db)
        => db.Query<ColumnNameRow>("SELECT type || ':' || name || ':' || tbl_name || ':' || COALESCE(sql, '') AS name FROM sqlite_master ORDER BY type, name;")
            .Select(row => row.name).ToArray();

    private static string[] ReadMaintenanceColumnDefinitions(LR2SongDBExtended db)
        => db.Query<ColumnNameRow>("SELECT cid || ':' || name || ':' || type || ':' || [notnull] || ':' || COALESCE(dflt_value, '') || ':' || pk AS name FROM pragma_table_info('maintenance') ORDER BY cid;")
            .Select(row => row.name).ToArray();

    /// <summary>
    /// 旧playlist_entryを検査してSHA256とschema版の修復警告を返し、Inspect自体は旧表や版行を変更しない。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void Inspect_LegacyPlaylistEntrySchema_RequiresWarningWithoutMutatingDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.DropTable<LR2SongDBExtended.playlist>();
                    db.DropTable<LR2SongDBExtended.playlist_entry>();
                    db.CreateTable<LR2SongDBExtended.playlist>();
                    db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                    db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
                });
            }

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsTrue(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
            Assert.IsTrue(result.WarnRequired);
            Assert.IsTrue(result.RequiresWarning);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'app_schema_version';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 現行のplaylist・bmson・schema版を持つDBでは修復と警告を要求せず、正常DBへ不要な修復を促さない。
    /// </summary>
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

    /// <summary>
    /// 新規LR2 DBのEnsure・Repairは無警告でapp所有schemaを作成し、preflightの収束とdigest・bmson表、刷新履歴の初回読取り・保存を保証する。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public void EnsureAppOwnedSchema_FreshLr2DatabaseConvergesPreflightWithoutDigestMigration(bool repair)
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsFalse(before.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(before.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(before.WarnRequired);
            Assert.IsFalse(before.RequiresWarning);

            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var gateway = new BmsLibraryDbGateway(tempDbPath);

            if (repair)
            {
                gateway.RepairAppOwnedSchema();
            }
            else
            {
                gateway.EnsureAppOwnedSchema();
            }
            AssertIrScoreRefreshMetadataFirstSave(gateway);

            AppSchemaPreflightResult result = service.Inspect(tempDbPath);
            Assert.IsFalse(result.NeedsPlaylistEntrySha256Repair);
            Assert.IsFalse(result.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(result.RepairRequired);
            Assert.IsFalse(result.WarnRequired);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version >= 1;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'bmson_song';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 既存の刷新履歴・本人スコア・楽曲行を各明示準備の再実行後も保持し、記録あり・未記録の通常読取りで構造版や保存行を変えない。
    /// </summary>
    [TestMethod]
    public void PrepareSchema_ExistingIrRefreshMetadataPreservesRowsAndReadDoesNotMutateDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.EnsureLibraryStartupSchema();
            const string hash = "0123456789abcdef0123456789abcdef";
            const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            DateTime updatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using (var seed = new LR2SongDBExtended(tempDbPath))
            {
                seed.RunInTransaction(() =>
                {
                    seed.CreateTable<LR2SongDB.song>();
                    seed.Insert(new LR2SongDB.song { hash = hash, path = @"D:\BMS\chart.bms", title = "Existing song" });
                    seed.Insert(new LR2SongDBExtended.ir_score { hash = hash, clearValue = 3, notes = 100, pg = 80, gr = 20, lastupdate = 123456 });
                    seed.Insert(new LR2SongDBExtended.ir_score_refresh_metadata { lr2id = 123, score_digest_sha256 = digest, updated_at = updatedAt });
                });
            }

            Action[] prepareSchema = [gateway.EnsureLibraryStartupSchema, gateway.EnsureAppOwnedSchema, gateway.RepairAppOwnedSchema];
            foreach (Action prepare in prepareSchema)
            {
                prepare();
                int schemaVersion;
                int appSchemaVersion;
                using (LR2SongDBExtended beforeRead = gateway.OpenSongDbReadOnly())
                {
                    schemaVersion = beforeRead.ExecuteScalar<int>("PRAGMA schema_version;");
                    appSchemaVersion = beforeRead.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema';");
                }

                LR2SongDBExtended.ir_score_refresh_metadata metadata = gateway.LoadIrScoreRefreshMetadata(123, out _);
                Assert.IsNotNull(metadata);
                Assert.AreEqual(123, metadata.lr2id);
                Assert.AreEqual(digest, metadata.score_digest_sha256);
                Assert.AreEqual(updatedAt, metadata.updated_at);
                Assert.IsNull(gateway.LoadIrScoreRefreshMetadata(456));

                using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(schemaVersion, verify.ExecuteScalar<int>("PRAGMA schema_version;"));
                Assert.AreEqual(appSchemaVersion, verify.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema';"));
                LR2SongDBExtended.ir_score_refresh_metadata storedMetadata = verify.Table<LR2SongDBExtended.ir_score_refresh_metadata>().Single();
                Assert.AreEqual(123, storedMetadata.lr2id);
                Assert.AreEqual(digest, storedMetadata.score_digest_sha256);
                Assert.AreEqual(updatedAt, storedMetadata.updated_at);
                LR2SongDBExtended.ir_score score = verify.Table<LR2SongDBExtended.ir_score>().Single();
                Assert.AreEqual(hash, score.hash);
                Assert.AreEqual(3, score.clearValue);
                Assert.AreEqual(100, score.notes);
                Assert.AreEqual(80, score.pg);
                Assert.AreEqual(20, score.gr);
                Assert.AreEqual(123456, score.lastupdate);
                LR2SongDB.song song = verify.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(hash, song.hash);
                Assert.AreEqual(@"D:\BMS\chart.bms", song.path);
                Assert.AreEqual("Existing song", song.title);
            }
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 刷新履歴表が未準備なら通常読取りの失敗を観測し、未記録nullへの変換や表作成、既存楽曲行の変更を検出する。
    /// </summary>
    [TestMethod]
    public void LoadIrScoreRefreshMetadata_UnpreparedDatabaseFailsWithoutCreatingTableOrChangingRows()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var gateway = new BmsLibraryDbGateway(tempDbPath);
            using (var seed = new LR2SongDBExtended(tempDbPath))
            {
                seed.RunInTransaction(() =>
                {
                    seed.CreateTable<LR2SongDB.song>();
                    seed.Insert(new LR2SongDB.song { hash = "0123456789abcdef0123456789abcdef", path = @"D:\BMS\chart.bms", title = "Existing song" });
                });
            }
            int schemaVersion;
            using (LR2SongDBExtended beforeRead = gateway.OpenSongDbReadOnly())
            {
                schemaVersion = beforeRead.ExecuteScalar<int>("PRAGMA schema_version;");
                Assert.AreEqual(0L, beforeRead.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'ir_score_refresh_metadata';"));
            }

            Action[] readMetadata = [() => gateway.LoadIrScoreRefreshMetadata(123), () => gateway.LoadIrScoreRefreshMetadata(123, out _)];
            foreach (Action read in readMetadata)
            {
                bool failed = false;
                try
                {
                    read();
                }
                catch (Exception)
                {
                    failed = true;
                }
                Assert.IsTrue(failed, "未準備DBの刷新履歴読取りは失敗を返す必要があります。");
            }

            using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
            Assert.AreEqual(schemaVersion, verify.ExecuteScalar<int>("PRAGMA schema_version;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'ir_score_refresh_metadata';"));
            LR2SongDB.song song = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("0123456789abcdef0123456789abcdef", song.hash);
            Assert.AreEqual(@"D:\BMS\chart.bms", song.path);
            Assert.AreEqual("Existing song", song.title);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 旧app_schema版を検出して修復警告を要求し、版が古いDBを現行として通過させない。
    /// </summary>
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

    /// <summary>
    /// 版行だけがない既存DBには警告を返し、実Repair後は同じDBへの再Inspectで修復要求が収束する。
    /// </summary>
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
            Assert.IsFalse(before.NeedsPlaylistEntrySha256Repair);
            Assert.IsTrue(before.WarnRequired);
            Assert.IsFalse(before.RepairRequired);

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

    /// <summary>
    /// schema修復時に実譜面から欠けたdigestを補完せず、版更新と重い譜面解析を混同しない。
    /// </summary>
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
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    db.CreateTable<LR2SongDBExtended.chart_digest_map>();
                });
            }

            new BmsLibraryDbGateway(tempDbPath).RepairAppOwnedSchema();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version >= 1;"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 不正なdigest・bmson表を再構築して保存互換列と索引を揃え、Inspectの構造修復要求を収束させる。
    /// </summary>
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
                db.RunInTransaction(() =>
                {
                    db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
                    db.Execute("CREATE TABLE bmson_song (path TEXT PRIMARY KEY, title TEXT NULL);");
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// 旧digest表の余分な列を退役してMD5-SHA256対応を保持し、schema版の更新で既存digestを失わない。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RepairAppOwnedSchema_NormalizesChartDigestMapAndSetsVersion()
    {
        string tempDbPath = CreateEmptySongDbPath();
        string md5 = string.Empty;
        string sha256 = string.Empty;
        try
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(tempDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Test\r\n");
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                    md5 = file.Md5;
                    sha256 = file.Sha256;
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, sha256 TEXT NULL, last_seen_path TEXT NULL, updated_at TEXT NULL);");
                    db.Execute("INSERT INTO chart_digest_map(md5, sha256, last_seen_path, updated_at) VALUES ('" + md5 + "', '" + sha256 + "', '" + chartPath.Replace("'", "''") + "', 'legacy');");
                });
            }

            var gateway = new BmsLibraryDbGateway(tempDbPath);
            gateway.RepairAppOwnedSchema();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// 現行版でも壊れたdigest表を修復し、修復後に保存できる孤立digestがInspectによって削除されない。
    /// </summary>
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
                db.RunInTransaction(() =>
                {
                    db.DropTable<LR2SongDBExtended.chart_digest_map>();
                    db.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, broken TEXT NULL);");
                });
            }

            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult before = service.Inspect(tempDbPath);
            Assert.IsFalse(before.NeedsAppSchemaVersionRepair);
            Assert.IsTrue(before.RepairRequired);

            gateway.RepairAppOwnedSchema();

            using (var orphan = new LR2SongDBExtended(tempDbPath))
            {
                orphan.Execute(
                    "INSERT INTO chart_digest_map (md5, sha256) VALUES (?, ?);",
                    new string('e', 32),
                    new string('f', 64));
            }
            AppSchemaPreflightResult after = service.Inspect(tempDbPath);
            Assert.IsFalse(after.NeedsAppSchemaVersionRepair);
            Assert.IsFalse(after.RepairRequired);
            Assert.IsFalse(after.WarnRequired);
            Assert.IsFalse(after.RequiresWarning);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            string chartDigestMapSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';");
            StringAssert.Contains(chartDigestMapSql, "sha256");
            Assert.IsFalse(chartDigestMapSql.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", new string('e', 32), new string('f', 64)));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 修復の終盤失敗では旧表・索引・版・playlist・digestを全て保持し、原因除去後の再試行で同じDBが収束する。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RepairAppOwnedSchema_LateFailureRetryConvergesOnSameDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            SeedLateFailureSchemaRepairDatabase(tempDbPath);
            SchemaRepairSnapshot before = ReadSchemaRepairSnapshot(tempDbPath);
            Exception? failure = null;
            try
            {
                new BmsLibraryDbGateway(tempDbPath).RepairAppOwnedSchema();
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

    /// <summary>
    /// bmson索引が欠けたDBでは既存bmson・digest行を落とさず必要な索引を再作成する。
    /// </summary>
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
                db.RunInTransaction(() =>
                {
                    db.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                    {
                        md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                        sha256 = new string('b', 64)
                    }, typeof(LR2SongDBExtended.chart_digest_map));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = "song-path",
                        Folder = "folder",
                        RawTitle = "title",
                        Subtitle = "subtitle",
                        RawArtist = "artist",
                        Genre = "genre",
                        Level = 12,
                        ModeHint = "beat-7k",
                        Md5 = "0e5751c026e543b2e8ab2eb06099daa1",
                        Sha256 = new string('c', 64),
                        Banner = "banner.png",
                        Backbmp = "back.png",
                        Stagefile = "stage.png",
                        PreviewMusic = "preview.ogg",
                        LastWriteTimeUtc = DateTime.UtcNow
                    }), typeof(LR2SongDBExtended.bmson_song));
                    db.Execute("DROP INDEX IF EXISTS bmson_song_idx_md5;");
                    db.Execute("DROP INDEX IF EXISTS bmson_song_idx_sha256;");
                    db.Execute("DROP INDEX IF EXISTS bmson_song_idx_folder;");
                });
            }

            gateway.EnsureBmsonSchema();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// 別threadがLR2共通Monitorを保持中でもpathからのInspectが完了し、read-only検査をwriter待ちに接続しない。
    /// </summary>
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
                    lockAcquired = LR2SongDBExtended.Lock(Timeout.InfiniteTimeSpan);
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

                if (lockAcquired)
                {
                    releaseLock.Wait();
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
            holderReady.Wait();
            holderFailure?.Throw();
            Assert.IsTrue(lockTaken.IsSet, "The dedicated lock-holder thread did not acquire the LR2SongDBExtended monitor lock.");

            var service = new AppSchemaPreflightService();
            // Monitorの保持中に検査が完了することを確認する。応答秒数を契約にはしない。
            // 保持側はfinallyでだけ解放し、停止する誤実装は検証全体の期限で検出する。
            AppSchemaPreflightResult result = service.Inspect(tempDbPath);

            Assert.IsFalse(result.RequiresWarning);
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

    /// <summary>
    /// 旧maintenanceにLR2互換列とpath索引を追加し、既存の譜面状態行をschema更新で落とさない。
    /// </summary>
    [TestMethod]
    public void EnsureMaintenanceSchema_AddsLr2CompatibilityColumnsWithoutDroppingRows()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.Execute("CREATE TABLE maintenance (hash TEXT NULL, path TEXT PRIMARY KEY, encoding TEXT NULL, is_files_warning_ignored INTEGER NOT NULL DEFAULT 0);");
                    db.Execute("INSERT INTO maintenance (hash, path, encoding, is_files_warning_ignored) VALUES ('0123456789abcdef0123456789abcdef', 'D:\\BMS\\chart.bms', 'shift_jis', 0);");
                });
            }

            new BmsLibraryDbGateway(tempDbPath).EnsureMaintenanceSchema();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// 旧maintenanceへLR2警告とpath長・親参照値を保存して読み戻し、列追加による指定値の欠落を検出する。
    /// </summary>
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
            var info = new LR2SongDBExtended.maintenance { path = @"D:\BMS\chart.bms", hash = "0123456789abcdef0123456789abcdef", lr2_warning_flags = 5, lr2_resource_max_relative_cp932_bytes = 12, lr2_resource_has_parent_traversal = true };

            new BmsLibraryDbGateway(tempDbPath).UpsertMaintenanceInfos([MaintenanceStorageMapping.ToCommon(info)]);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.maintenance row = verify.Table<LR2SongDBExtended.maintenance>().Single(item => item.path == info.path);
            Assert.AreEqual(5, row.lr2_warning_flags);
            Assert.AreEqual(12, row.lr2_resource_max_relative_cp932_bytes);
            Assert.AreEqual(true, row.lr2_resource_has_parent_traversal);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 大文字小文字だけ異なる二つのpathへmaintenanceを一括保存し、別キーのhash・encoding・状態を混ぜない。
    /// </summary>
    [TestMethod]
    public void CommitFileScanDiffChunk_BulkUpsertsMaintenanceRowsWithExactCaseDistinctPaths()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            var chunk = new FileScanDiffCommitChunk();
            chunk.AddMaintenanceInfoRow(MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = @"D:\BMS\Pack\Song\Chart.bms", hash = "11111111111111111111111111111111", encoding = "shift_jis", wav_files_defined = 1, wav_files_existing = 0, is_stagefile_defined = true, is_stagefile_existing = false, is_files_warning_ignored = false }), countMutation: true);
            chunk.AddMaintenanceInfoRow(MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = @"D:\BMS\Pack\Song\chart.bms", hash = "22222222222222222222222222222222", encoding = "utf-8", wav_files_defined = 2, wav_files_existing = 2, is_stagefile_defined = true, is_stagefile_existing = true, is_files_warning_ignored = true, lr2_warning_flags = 4, lr2_resource_max_relative_cp932_bytes = 120 }), countMutation: true);

            using (var db = new LR2SongDBExtended(tempDbPath))
            {
                BmsLibraryDbGateway.CommitFileScanDiffChunk(db, chunk);
            }

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            List<LR2SongDBExtended.maintenance> rows = [.. verify.Table<LR2SongDBExtended.maintenance>().OrderBy(row => row.path, StringComparer.Ordinal)];
            Assert.AreEqual(2, rows.Count);
            LR2SongDBExtended.maintenance upperCaseRow = rows.Single(row => row.path == @"D:\BMS\Pack\Song\Chart.bms");
            Assert.AreEqual("11111111111111111111111111111111", upperCaseRow.hash);
            Assert.AreEqual("shift_jis", upperCaseRow.encoding);
            Assert.AreEqual(1, upperCaseRow.wav_files_defined);
            Assert.AreEqual(0, upperCaseRow.wav_files_existing);
            Assert.AreEqual(true, upperCaseRow.is_stagefile_defined);
            Assert.AreEqual(false, upperCaseRow.is_stagefile_existing);
            Assert.IsFalse(upperCaseRow.is_files_warning_ignored);

            LR2SongDBExtended.maintenance lowerCaseRow = rows.Single(row => row.path == @"D:\BMS\Pack\Song\chart.bms");
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

    private static void AssertIrScoreRefreshMetadataFirstSave(BmsLibraryDbGateway gateway)
    {
        Assert.IsNull(gateway.LoadIrScoreRefreshMetadata(123));
        const string digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        gateway.UpsertIrScoreRefreshMetadata(123, digest);
        LR2SongDBExtended.ir_score_refresh_metadata metadata = gateway.LoadIrScoreRefreshMetadata(123, out _);
        Assert.IsNotNull(metadata);
        Assert.AreEqual(123, metadata.lr2id);
        Assert.AreEqual(digest, metadata.score_digest_sha256);
        Assert.IsTrue(metadata.updated_at > DateTime.MinValue);
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
        seed.RunInTransaction(() =>
        {
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
        });
    }

    private static void DeleteTempSongDbDirectory(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath))
        {
            return;
        }
        string? directoryPath = Path.GetDirectoryName(songDbPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static SchemaRepairSnapshot ReadSchemaRepairSnapshot(string songDbPath)
    {
        using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
