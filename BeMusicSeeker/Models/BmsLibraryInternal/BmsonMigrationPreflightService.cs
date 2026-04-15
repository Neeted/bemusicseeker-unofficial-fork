using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsonMigrationPreflightResult
{
    public bool NeedsPlaylistEntrySha256Migration { get; }

    public bool NeedsInitialSha256BackfillWarning { get; }

    public bool RequiresWarning => NeedsPlaylistEntrySha256Migration || NeedsInitialSha256BackfillWarning;

    public BmsonMigrationPreflightResult(bool needsPlaylistEntrySha256Migration, bool needsInitialSha256BackfillWarning)
    {
        NeedsPlaylistEntrySha256Migration = needsPlaylistEntrySha256Migration;
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
        return new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration, needsInitialSha256BackfillWarning: false);
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

    private static bool TableExists(LR2SongDBExtended db, string tableName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static bool IndexExists(LR2SongDBExtended db, string indexName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + BMSPlaylist.SqlQuoteForTest(indexName) + ";") > 0;
    }
}
