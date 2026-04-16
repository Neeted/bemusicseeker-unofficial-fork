using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsonMigrationPreflightResult
{
    public bool NeedsPlaylistEntrySha256Migration { get; }

    public bool NeedsChartDigestMapSchema { get; }

    public bool NeedsInitialSha256BackfillWarning { get; }

    public bool RequiresWarning => NeedsPlaylistEntrySha256Migration || NeedsChartDigestMapSchema || NeedsInitialSha256BackfillWarning;

    public BmsonMigrationPreflightResult(bool needsPlaylistEntrySha256Migration, bool needsChartDigestMapSchema, bool needsInitialSha256BackfillWarning)
    {
        NeedsPlaylistEntrySha256Migration = needsPlaylistEntrySha256Migration;
        NeedsChartDigestMapSchema = needsChartDigestMapSchema;
        NeedsInitialSha256BackfillWarning = needsInitialSha256BackfillWarning;
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
        using LR2SongDBExtended db = new LR2SongDBExtended(songDbPath);
        return Inspect(db);
    }

    internal BmsonMigrationPreflightResult Inspect(LR2SongDBExtended db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        bool needsPlaylistEntrySha256Migration = NeedsPlaylistEntrySha256Migration(db);
        bool needsChartDigestMapSchema = NeedsChartDigestMapSchema(db);
        bool needsInitialSha256BackfillWarning = NeedsInitialSha256BackfillWarning(db, needsChartDigestMapSchema);
        return new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration, needsChartDigestMapSchema, needsInitialSha256BackfillWarning);
    }

    private static bool NeedsPlaylistEntrySha256Migration(LR2SongDBExtended db)
    {
        string playlistTableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
        string playlistEntryTableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        if (!TableExists(db, playlistTableName) || !TableExists(db, playlistEntryTableName))
        {
            return true;
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

    private static bool NeedsChartDigestMapSchema(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(db, tableName))
        {
            return true;
        }
        string tableSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";");
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.IndexOf("md5", StringComparison.OrdinalIgnoreCase) < 0
            || tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0
            || tableSql.IndexOf("last_seen_path", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }
        return !IndexExists(db, "chart_digest_map_idx_sha256");
    }

    private static bool NeedsInitialSha256BackfillWarning(LR2SongDBExtended db, bool needsChartDigestMapSchema)
    {
        bool songTableExists = TableExists(db, "song");
        if (!songTableExists)
        {
            return false;
        }
        long songCount = db.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE hash IS NOT NULL AND TRIM(hash) <> '';");
        if (songCount <= 0)
        {
            return false;
        }
        if (needsChartDigestMapSchema)
        {
            return true;
        }
        string digestTableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        long missingCount = db.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM song s "
            + "WHERE s.hash IS NOT NULL AND TRIM(s.hash) <> '' "
            + "AND NOT EXISTS ("
            + "SELECT 1 FROM " + digestTableName + " d "
            + "WHERE lower(d.md5) = lower(s.hash) AND d.sha256 IS NOT NULL AND TRIM(d.sha256) <> ''"
            + ");");
        return missingCount > 0;
    }

    private static bool TableExists(LR2SongDBExtended db, string tableName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static bool IndexExists(LR2SongDBExtended db, string indexName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + BMSPlaylist.SqlQuoteForTest(indexName) + ";") > 0;
    }
}
