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

internal sealed class BmsonMigrationPreflightResult
{
    public bool NeedsPlaylistEntrySha256Migration { get; }

    public bool NeedsChartDigestMapSchema { get; }

    public bool NeedsBmsonSongSchema { get; }

    public bool NeedsBmsonAppSchemaMigration { get; }

    public bool NeedsBmsonAppSchemaWarning { get; }

    public RepairableBmsonSchemaIssues RepairableBmsonSchemaIssues { get; }

    public bool RepairRequired => RepairableBmsonSchemaIssues != 0;

    public bool WarnRequired => NeedsPlaylistEntrySha256Migration || NeedsBmsonAppSchemaWarning;

    public bool RequiresWarning => WarnRequired;

    public BmsonMigrationPreflightResult(bool needsPlaylistEntrySha256Migration, bool needsChartDigestMapSchema, bool needsBmsonSongSchema, bool needsBmsonAppSchemaMigration, RepairableBmsonSchemaIssues repairableBmsonSchemaIssues, bool? needsBmsonAppSchemaWarning = null)
    {
        NeedsPlaylistEntrySha256Migration = needsPlaylistEntrySha256Migration;
        NeedsChartDigestMapSchema = needsChartDigestMapSchema;
        NeedsBmsonSongSchema = needsBmsonSongSchema;
        NeedsBmsonAppSchemaMigration = needsBmsonAppSchemaMigration;
        NeedsBmsonAppSchemaWarning = needsBmsonAppSchemaWarning ?? needsBmsonAppSchemaMigration;
        RepairableBmsonSchemaIssues = repairableBmsonSchemaIssues;
    }
}

internal sealed class BmsonMigrationPreflightService
{
    public BmsonMigrationPreflightResult Inspect(string songDbPath)
    {
        if (songDbPath == null)
        {
            throw new ArgumentNullException(nameof(songDbPath));
        }
        if (!File.Exists(songDbPath))
        {
            throw new ArgumentException(songDbPath, nameof(songDbPath));
        }
        using SQLiteConnection db = new SQLiteConnection(songDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        return Inspect(db);
    }

    internal BmsonMigrationPreflightResult Inspect(SQLiteConnection db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        RepairableBmsonSchemaIssues repairableBmsonSchemaIssues = AnalyzeRepairableBmsonSchemaIssues(db);
        bool needsPlaylistEntrySha256Migration = NeedsPlaylistEntrySha256Migration(db);
        bool needsChartDigestMapSchema = repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid);
        bool needsBmsonSongSchema = repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongMd5IndexMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongSha256IndexMissing)
            || repairableBmsonSchemaIssues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongFolderIndexMissing);
        BmsonAppSchemaPreflight appSchemaPreflight = InspectBmsonAppSchema(db);
        return new BmsonMigrationPreflightResult(
            needsPlaylistEntrySha256Migration,
            needsChartDigestMapSchema,
            needsBmsonSongSchema,
            appSchemaPreflight.NeedsMigration,
            repairableBmsonSchemaIssues,
            appSchemaPreflight.NeedsWarning);
    }

    private static bool NeedsPlaylistEntrySha256Migration(SQLiteConnection db)
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

    private static BmsonAppSchemaPreflight InspectBmsonAppSchema(SQLiteConnection db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName();
        if (!TableExists(db, tableName))
        {
            return new BmsonAppSchemaPreflight(needsMigration: true, needsWarning: AppOwnedBmsonTablesExist(db));
        }
        long currentCount = db.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + tableName
            + " WHERE name = " + BMSPlaylist.SqlQuoteForTest(BmsLibraryDbGateway.BmsonAppSchemaVersionName)
            + " AND version >= " + BmsLibraryDbGateway.CurrentBmsonAppSchemaVersion + ";");
        if (currentCount > 0)
        {
            return new BmsonAppSchemaPreflight(needsMigration: false, needsWarning: false);
        }
        long rowCount = db.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + tableName
            + " WHERE name = " + BMSPlaylist.SqlQuoteForTest(BmsLibraryDbGateway.BmsonAppSchemaVersionName) + ";");
        bool rowExists = rowCount > 0;
        bool needsWarning = rowExists || AppOwnedBmsonTablesExist(db);
        return new BmsonAppSchemaPreflight(needsMigration: true, needsWarning: needsWarning);
    }

    private static bool AppOwnedBmsonTablesExist(SQLiteConnection db)
    {
        return TableExists(db, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName())
            || TableExists(db, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName());
    }

    private static bool TableExists(SQLiteConnection db, string tableName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static bool IndexExists(SQLiteConnection db, string indexName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + BMSPlaylist.SqlQuoteForTest(indexName) + ";") > 0;
    }

    private readonly struct BmsonAppSchemaPreflight
    {
        public BmsonAppSchemaPreflight(bool needsMigration, bool needsWarning)
        {
            NeedsMigration = needsMigration;
            NeedsWarning = needsWarning;
        }

        public bool NeedsMigration { get; }

        public bool NeedsWarning { get; }
    }
}
