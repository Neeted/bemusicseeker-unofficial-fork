using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

[Flags]
internal enum RepairableBmsonSchemaIssues
{
    None = 0,
    ChartDigestMapTableMissing = 1,
    ChartDigestMapTableInvalid = 2,
    BmsonSongTableMissing = 4,
    BmsonSongTableInvalid = 8,
    BmsonSongMd5IndexMissing = 0x10,
    BmsonSongSha256IndexMissing = 0x20,
    BmsonSongFolderIndexMissing = 0x40
}

internal sealed class AppSchemaPreflightResult(bool needsPlaylistEntrySha256Repair, bool needsChartDigestMapSchema, bool needsBmsonSongSchema, bool needsAppSchemaVersionRepair, RepairableBmsonSchemaIssues repairableBmsonSchemaIssues, bool? needsAppSchemaVersionWarning = null)
{
    public bool NeedsPlaylistEntrySha256Repair { get; } = needsPlaylistEntrySha256Repair;

    public bool NeedsChartDigestMapSchema { get; } = needsChartDigestMapSchema;

    public bool NeedsBmsonSongSchema { get; } = needsBmsonSongSchema;

    public bool NeedsAppSchemaVersionRepair { get; } = needsAppSchemaVersionRepair;

    public bool NeedsAppSchemaVersionWarning { get; } = needsAppSchemaVersionWarning ?? needsAppSchemaVersionRepair;

    public RepairableBmsonSchemaIssues RepairableBmsonSchemaIssues { get; } = repairableBmsonSchemaIssues;

    public bool RepairRequired => NeedsPlaylistEntrySha256Repair || RepairableBmsonSchemaIssues != 0;

    public bool WarnRequired => NeedsPlaylistEntrySha256Repair || NeedsAppSchemaVersionWarning;

    public bool RequiresWarning => WarnRequired;
}

internal sealed class AppSchemaPreflightService
{
    public AppSchemaPreflightResult Inspect(string songDbPath)
    {
        if (songDbPath == null)
        {
            throw new ArgumentNullException(nameof(songDbPath));
        }
        if (!File.Exists(songDbPath))
        {
            throw new ArgumentException(songDbPath, nameof(songDbPath));
        }
        using var db = new SQLiteConnection(songDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        return Inspect(db);
    }

    internal AppSchemaPreflightResult Inspect(SQLiteConnection db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        RepairableBmsonSchemaIssues repairableBmsonSchemaIssues = AnalyzeRepairableBmsonSchemaIssues(db);
        bool needsPlaylistEntrySha256Repair = NeedsPlaylistEntrySha256Repair(db);
        bool needsChartDigestMapSchema = repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid);
        bool needsBmsonSongSchema = repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongMd5IndexMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongSha256IndexMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongFolderIndexMissing);
        AppSchemaVersionPreflight appSchemaPreflight = InspectAppSchemaVersion(db);
        return new AppSchemaPreflightResult(
            needsPlaylistEntrySha256Repair,
            needsChartDigestMapSchema,
            needsBmsonSongSchema,
            appSchemaPreflight.NeedsRepair,
            repairableBmsonSchemaIssues,
            appSchemaPreflight.NeedsWarning);
    }

    private static bool NeedsPlaylistEntrySha256Repair(SQLiteConnection db)
    {
        string playlistEntryTableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        if (!TableExists(db, playlistEntryTableName))
        {
            return false;
        }
        string playlistEntrySql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(playlistEntryTableName) + ";");
        if (string.IsNullOrWhiteSpace(playlistEntrySql) || playlistEntrySql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }
        if (!IndexExists(db, "playlist_entry_idx_sha256"))
        {
            return true;
        }
        string uniqueIndexSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
        if (string.IsNullOrWhiteSpace(uniqueIndexSql) || uniqueIndexSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }
        return false;
    }

    internal static RepairableBmsonSchemaIssues AnalyzeRepairableBmsonSchemaIssues(SQLiteConnection db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        RepairableBmsonSchemaIssues issues = RepairableBmsonSchemaIssues.None;
        string chartDigestMapTableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(db, chartDigestMapTableName))
        {
            issues |= RepairableBmsonSchemaIssues.ChartDigestMapTableMissing;
        }
        else
        {
            string chartDigestMapTableSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(chartDigestMapTableName) + ";");
            if (string.IsNullOrWhiteSpace(chartDigestMapTableSql)
                || chartDigestMapTableSql.IndexOf("md5", StringComparison.OrdinalIgnoreCase) < 0
                || chartDigestMapTableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
            {
                issues |= RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid;
            }
        }
        string bmsonSongTableName = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        if (!TableExists(db, bmsonSongTableName))
        {
            issues |= RepairableBmsonSchemaIssues.BmsonSongTableMissing;
        }
        else
        {
            string bmsonSongTableSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(bmsonSongTableName) + ";");
            if (string.IsNullOrWhiteSpace(bmsonSongTableSql)
                || bmsonSongTableSql.IndexOf("path", StringComparison.OrdinalIgnoreCase) < 0
                || bmsonSongTableSql.IndexOf("md5", StringComparison.OrdinalIgnoreCase) < 0
                || bmsonSongTableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0
                || bmsonSongTableSql.IndexOf("mode_hint", StringComparison.OrdinalIgnoreCase) < 0)
            {
                issues |= RepairableBmsonSchemaIssues.BmsonSongTableInvalid;
            }
            else
            {
                if (!IndexExists(db, "bmson_song_idx_md5"))
                {
                    issues |= RepairableBmsonSchemaIssues.BmsonSongMd5IndexMissing;
                }
                if (!IndexExists(db, "bmson_song_idx_sha256"))
                {
                    issues |= RepairableBmsonSchemaIssues.BmsonSongSha256IndexMissing;
                }
                if (!IndexExists(db, "bmson_song_idx_folder"))
                {
                    issues |= RepairableBmsonSchemaIssues.BmsonSongFolderIndexMissing;
                }
            }
        }
        return issues;
    }

    private static AppSchemaVersionPreflight InspectAppSchemaVersion(SQLiteConnection db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName();
        if (!TableExists(db, tableName))
        {
            return new AppSchemaVersionPreflight(needsRepair: true, needsWarning: AppOwnedSchemaExists(db));
        }
        long currentCount = db.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + tableName
            + " WHERE name = " + BMSPlaylist.SqlQuoteForTest(BmsLibraryDbGateway.AppSchemaVersionName)
            + " AND version >= " + BmsLibraryDbGateway.CurrentAppSchemaVersion + ";");
        if (currentCount > 0)
        {
            return new AppSchemaVersionPreflight(needsRepair: false, needsWarning: false);
        }
        long rowCount = db.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + tableName
            + " WHERE name = " + BMSPlaylist.SqlQuoteForTest(BmsLibraryDbGateway.AppSchemaVersionName) + ";");
        bool schemaRowExists = rowCount > 0;
        bool needsWarning = schemaRowExists || AppOwnedSchemaExists(db);
        return new AppSchemaVersionPreflight(needsRepair: true, needsWarning: needsWarning);
    }

    private static bool AppOwnedSchemaExists(SQLiteConnection db)
    {
        return TableExists(db, SQLiteTable<LR2SongDBExtended.playlist>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.lr2_song_db_sync_status>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName());
    }

    private static bool TableExists(SQLiteConnection db, string tableName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static bool IndexExists(SQLiteConnection db, string indexName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + BMSPlaylist.SqlQuoteForTest(indexName) + ";") > 0;
    }

    private readonly struct AppSchemaVersionPreflight(bool needsRepair, bool needsWarning)
    {
        public bool NeedsRepair { get; } = needsRepair;

        public bool NeedsWarning { get; } = needsWarning;
    }
}
