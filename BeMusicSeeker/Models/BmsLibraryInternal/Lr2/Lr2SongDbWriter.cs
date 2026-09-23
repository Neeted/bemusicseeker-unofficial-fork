using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct Lr2GeneratedSongWriteResult(
    int changedCount,
    int updatedCount,
    int insertedCount,
    long enrichmentMs,
    long tempStageMs,
    long previousHashStageMs,
    long updateStageMs,
    long insertStageMs,
    long digestUpsertStageMs,
    long digestCleanupStageMs,
    long tempCleanupStageMs)
{
    public int ChangedCount { get; } = changedCount;

    public int UpdatedCount { get; } = updatedCount;

    public int InsertedCount { get; } = insertedCount;

    public long EnrichmentMs { get; } = enrichmentMs;

    public long TempStageMs { get; } = tempStageMs;

    public long PreviousHashStageMs { get; } = previousHashStageMs;

    public long UpdateStageMs { get; } = updateStageMs;

    public long InsertStageMs { get; } = insertStageMs;

    public long DigestUpsertStageMs { get; } = digestUpsertStageMs;

    public long DigestCleanupStageMs { get; } = digestCleanupStageMs;

    public long TempCleanupStageMs { get; } = tempCleanupStageMs;
}

internal static class Lr2SongDbWriter
{
    private const string TempDeletedSongHashTable = "lr2_song_db_sync_deleted_song_hash";

    private const string TempChartDigestUpsertTable = "lr2_song_db_sync_chart_digest_upsert";

    private const string TempGeneratedSongUpdateTable = "lr2_song_db_sync_generated_song_update";

    private const string TempGeneratedSongUpsertTable = "lr2_song_db_sync_generated_song_upsert";

    private const string TempChartInfoSongProjectionTable = "chart_info_song_projection_update";

    internal static bool UpsertGeneratedSong(LR2SongDBExtended songDb, BMSFile song)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return false;
        }

        GeneratedSongRow existingSong = FindSongByPath(songDb, song.path);
        Lr2SongRowEnricher.EnrichGeneratedSong(song);
        ApplyGeneratedPersistenceDefaults(song, isNewRow: existingSong == null);
        string previousHash = existingSong?.hash;
        bool changed = false;
        if (existingSong == null)
        {
            songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            changed = true;
        }
        else if (!HasSameGeneratedColumns(song, existingSong))
        {
            UpdateGeneratedColumns(songDb, song);
            changed = true;
        }
        BmsLibraryDbGateway.UpsertChartDigest(songDb, song);
        BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, previousHash, song.hash);
        return changed;
    }

    internal static int UpsertGeneratedSongs(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        List<BMSFile> rows = [.. (songs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        if (rows.Count == 0)
        {
            return 0;
        }

        Dictionary<string, GeneratedSongRow> existingByPath = FindSongsByPaths(songDb, rows.Select(song => song.path));
        var previousHashesToCheck = new List<string>();
        var rowsToInsert = new List<BMSFile>();
        var rowsToUpdate = new List<BMSFile>();
        int changedCount = 0;
        foreach (BMSFile song in rows)
        {
            existingByPath.TryGetValue(song.path, out GeneratedSongRow existingSong);
            Lr2SongRowEnricher.EnrichGeneratedSong(song);
            ApplyGeneratedPersistenceDefaults(song, isNewRow: existingSong == null);
            string previousHash = existingSong?.hash;
            bool changed = false;
            if (existingSong == null)
            {
                rowsToInsert.Add(song);
                changed = true;
            }
            else if (!HasSameGeneratedColumns(song, existingSong))
            {
                rowsToUpdate.Add(song);
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(previousHash)
                && !string.Equals(previousHash, song.hash, StringComparison.OrdinalIgnoreCase))
            {
                previousHashesToCheck.Add(previousHash);
            }
            if (changed)
            {
                changedCount++;
            }
        }
        BulkInsertGeneratedSongs(songDb, rowsToInsert);
        BulkUpdateGeneratedColumns(songDb, rowsToUpdate);
        UpsertChartDigests(songDb, rows);
        DeleteOrphanedChartDigests(songDb, previousHashesToCheck);
        return changedCount;
    }

    /// <summary>
    /// Updates only chart-info-derived columns on existing LR2 song rows.
    /// Missing or identity-mismatched rows are reported and are never inserted.
    /// </summary>
    internal static Lr2ChartInfoSongProjectionWriteResult UpdateChartInfoSongProjections(
        LR2SongDBExtended songDb,
        IReadOnlyList<Lr2ChartInfoSongProjection> projections)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        List<Lr2ChartInfoSongProjection> rows = [.. (projections ?? [])
            .Where(projection => projection != null)
            .GroupBy(projection => projection.Identity)
            .Select(group => group.Last())];
        if (rows.Count == 0)
        {
            return Lr2ChartInfoSongProjectionWriteResult.Empty;
        }

        if (!TableExists(songDb, "song"))
        {
            return new Lr2ChartInfoSongProjectionWriteResult([], rows, 0);
        }

        PrepareTempChartInfoSongProjectionTable(songDb);
        try
        {
            BulkInsertChartInfoSongProjections(songDb, rows);
            HashSet<Lr2ChartInfoSongProjectionIdentity> matchedIdentities = [.. LoadMatchedChartInfoSongProjectionIdentities(songDb)];
            int changedCount = UpdateMatchedChartInfoSongProjections(songDb);
            return new Lr2ChartInfoSongProjectionWriteResult(
                rows.Where(projection => matchedIdentities.Contains(projection.Identity)),
                rows.Where(projection => !matchedIdentities.Contains(projection.Identity)),
                changedCount);
        }
        finally
        {
            ClearTempTable(songDb, TempChartInfoSongProjectionTable);
        }
    }

    internal static int UpsertGeneratedSongsForLr2SongDbSync(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
    {
        return UpsertGeneratedSongsForLr2SongDbSyncWithResult(songDb, songs).ChangedCount;
    }

    /// <summary>
    /// Updates generated columns on existing song rows only.  Missing paths
    /// are intentionally ignored because full LR2 reconciliation does not own
    /// song membership; file-diff commits own insertion and deletion.
    /// </summary>
    internal static Lr2GeneratedSongWriteResult UpdateGeneratedSongsForLr2SongDbSyncWithResult(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songs)
    {
        return WriteGeneratedSongsForLr2SongDbSyncWithResult(songDb, songs, allowMembershipMutation: false);
    }

    internal static Lr2GeneratedSongWriteResult UpsertGeneratedSongsForLr2SongDbSyncWithResult(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songs)
    {
        return WriteGeneratedSongsForLr2SongDbSyncWithResult(songDb, songs, allowMembershipMutation: true);
    }

    private static Lr2GeneratedSongWriteResult WriteGeneratedSongsForLr2SongDbSyncWithResult(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songs,
        bool allowMembershipMutation)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        List<BMSFile> rows = [.. (songs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        if (rows.Count == 0)
        {
            return new Lr2GeneratedSongWriteResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var stageStopwatch = Stopwatch.StartNew();
        foreach (BMSFile song in rows)
        {
            Lr2SongRowEnricher.EnrichGeneratedSong(song);
            ApplyGeneratedPersistenceDefaults(song, isNewRow: allowMembershipMutation);
        }
        stageStopwatch.Stop();
        long enrichmentMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        PrepareTempGeneratedSongUpsertTable(songDb);
        BulkInsertGeneratedSongUpsertTempRows(songDb, rows);
        stageStopwatch.Stop();
        long tempStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        PrepareTempHashTable(songDb, TempDeletedSongHashTable);
        InsertChangedPreviousHashesIntoTemp(songDb, TempGeneratedSongUpsertTable, TempDeletedSongHashTable);
        stageStopwatch.Stop();
        long previousHashStageMs = stageStopwatch.ElapsedMilliseconds;

        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        stageStopwatch.Restart();
        int updated = songDb.Execute(
            "UPDATE " + songTable
            + " SET "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.hash), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.title), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.subtitle), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.artist), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.subartist), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.genre), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.type), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.folder), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.stagefile), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.banner), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.backbmp), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.parent), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.level), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.difficulty), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.maxbpm), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.minbpm), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.mode), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.judge), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.longnote), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.bga), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.random), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.date), TempGeneratedSongUpsertTable) + ", "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt)
            + " = COALESCE((SELECT txt FROM temp." + TempGeneratedSongUpsertTable + " u WHERE u.path = " + songTable + "." + songPathColumn + "), "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.karinotes), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.exlevel), TempGeneratedSongUpsertTable)
            + " WHERE rowid IN ("
            + "SELECT s.rowid FROM temp." + TempGeneratedSongUpsertTable + " u "
            + "JOIN " + songTable + " s ON s." + songPathColumn + " = u.path "
            + "WHERE s." + songPathColumn + " IN (SELECT path FROM temp." + TempGeneratedSongUpsertTable + ") "
            + "AND " + BuildGeneratedColumnChangePredicate("s", "u") + ");");
        stageStopwatch.Stop();
        long updateStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        int inserted = allowMembershipMutation
            ? InsertMissingGeneratedSongsFromTemp(songDb, TempGeneratedSongUpsertTable)
            : 0;
        stageStopwatch.Stop();
        long insertStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        UpsertChartDigests(songDb, rows, ensureTable: false);
        stageStopwatch.Stop();
        long digestUpsertStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        DeleteOrphanedChartDigestsFromTemp(songDb, TempDeletedSongHashTable, assumeLiveTablesExist: true);
        stageStopwatch.Stop();
        long digestCleanupStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        ClearTempTable(songDb, TempDeletedSongHashTable);
        ClearTempTable(songDb, TempGeneratedSongUpsertTable);
        stageStopwatch.Stop();
        long tempCleanupStageMs = stageStopwatch.ElapsedMilliseconds;
        return new Lr2GeneratedSongWriteResult(
            updated + inserted,
            updated,
            inserted,
            enrichmentMs,
            tempStageMs,
            previousHashStageMs,
            updateStageMs,
            insertStageMs,
            digestUpsertStageMs,
            digestCleanupStageMs,
            tempCleanupStageMs);
    }

    private static void ApplyGeneratedPersistenceDefaults(BMSFile song, bool isNewRow)
    {
        if (song == null)
        {
            return;
        }
        if (isNewRow && (!song.adddate.HasValue || song.adddate <= 0))
        {
            song.adddate = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow);
        }
    }


    private static void PrepareTempHashTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("CREATE TEMP TABLE IF NOT EXISTS temp." + tableName + " (md5 TEXT PRIMARY KEY);");
        ClearTempTable(songDb, tableName);
    }

    private static void PrepareTempChartDigestUpsertTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempChartDigestUpsertTable
            + " (md5 TEXT PRIMARY KEY, sha256 TEXT);");
        ClearTempTable(songDb, TempChartDigestUpsertTable);
    }

    private static void PrepareTempChartInfoSongProjectionTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempChartInfoSongProjectionTable + " ("
            + "path TEXT NOT NULL, md5 TEXT NOT NULL, "
            + "level INTEGER, difficulty INTEGER NOT NULL, maxbpm INTEGER, minbpm INTEGER, bga INTEGER, "
            + "exlevel INTEGER NOT NULL, longnote INTEGER NOT NULL, random INTEGER NOT NULL, karinotes INTEGER NOT NULL, "
            + "PRIMARY KEY(path, md5));");
        ClearTempTable(songDb, TempChartInfoSongProjectionTable);
    }

    private static void BulkInsertChartInfoSongProjections(
        LR2SongDBExtended songDb,
        IReadOnlyList<Lr2ChartInfoSongProjection> projections)
    {
        const int columnCount = 11;
        const int chunkSize = 80;
        for (int offset = 0; offset < projections.Count; offset += chunkSize)
        {
            var chunk = projections.Skip(offset).Take(chunkSize).ToList();
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", chunk.Select(_ => rowPlaceholders));
            var args = new List<object>(chunk.Count * columnCount);
            foreach (Lr2ChartInfoSongProjection projection in chunk)
            {
                args.Add(projection.Path);
                args.Add(projection.Md5);
                args.Add(projection.Level);
                args.Add(projection.Difficulty);
                args.Add(projection.MaxBpm);
                args.Add(projection.MinBpm);
                args.Add(projection.Bga);
                args.Add(projection.ExLevel);
                args.Add(projection.LongNote);
                args.Add(projection.Random);
                args.Add(projection.KariNotes);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempChartInfoSongProjectionTable
                + " (path, md5, level, difficulty, maxbpm, minbpm, bga, exlevel, longnote, random, karinotes) VALUES "
                + placeholders + ";",
                [.. args]);
        }
    }

    private static IReadOnlyList<Lr2ChartInfoSongProjectionIdentity> LoadMatchedChartInfoSongProjectionIdentities(
        LR2SongDBExtended songDb)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        string hashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        return [.. songDb.Query<ChartInfoSongProjectionIdentityRow>(
                "SELECT DISTINCT u.path AS path, u.md5 AS md5 "
                + "FROM temp." + TempChartInfoSongProjectionTable + " u "
                + "JOIN " + songTable + " s ON s." + pathColumn + " = u.path "
                + "AND lower(trim(s." + hashColumn + ")) = u.md5;")
            .Where(row => row != null)
            .Select(row => new Lr2ChartInfoSongProjectionIdentity(row.path, row.md5))];
    }

    private static int UpdateMatchedChartInfoSongProjections(LR2SongDBExtended songDb)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        string hashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        string match = "u.path = " + songTable + "." + pathColumn
            + " AND u.md5 = lower(trim(" + songTable + "." + hashColumn + "))";
        string changed = string.Join(" OR ", new[]
        {
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.level)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.difficulty)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.maxbpm)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.minbpm)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.bga)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.exlevel)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.longnote)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.random)),
            BuildChartInfoProjectionColumnChanged(nameof(LR2SongDB.song.karinotes))
        });
        return songDb.Execute(
            "UPDATE " + songTable + " SET "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.level), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.difficulty), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.maxbpm), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.minbpm), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.bga), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.exlevel), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.longnote), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.random), match) + ", "
            + BuildChartInfoProjectionAssignment(nameof(LR2SongDB.song.karinotes), match)
            + " WHERE EXISTS (SELECT 1 FROM temp." + TempChartInfoSongProjectionTable + " u WHERE "
            + match + " AND (" + changed + "));");
    }

    private static string BuildChartInfoProjectionAssignment(string columnName, string match)
    {
        return columnName + " = (SELECT u." + columnName + " FROM temp."
            + TempChartInfoSongProjectionTable + " u WHERE " + match + ")";
    }

    private static string BuildChartInfoProjectionColumnChanged(string columnName)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        return songTable + "." + columnName + " IS NOT u." + columnName;
    }

    private static void ClearTempTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("DELETE FROM temp." + tableName + ";");
    }


    private static void BulkInsertGeneratedSongs(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
    {
        if (songs == null || songs.Count == 0)
        {
            return;
        }

        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string[] columns =
        [
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.tag),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.favorite),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.adddate),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel)
        ];
        int columnCount = columns.Length;
        const int chunkSize = 30;
        for (int offset = 0; offset < songs.Count; offset += chunkSize)
        {
            var chunk = songs.Skip(offset).Take(chunkSize).ToList();
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", chunk.Select(_ => rowPlaceholders));
            var args = new List<object>(chunk.Count * columnCount);
            foreach (BMSFile song in chunk)
            {
                AddGeneratedSongInsertArgs(args, song);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO " + songTable + " ("
                + string.Join(",", columns) + ") VALUES " + placeholders + ";",
                [.. args]);
        }
    }

    private static void PrepareTempGeneratedSongUpsertTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempGeneratedSongUpsertTable + " ("
            + "hash TEXT, title TEXT, subtitle TEXT, artist TEXT, subartist TEXT, genre TEXT, tag TEXT, "
            + "path TEXT PRIMARY KEY, type INTEGER, folder TEXT, stagefile TEXT, banner TEXT, backbmp TEXT, parent TEXT, "
            + "level INTEGER, difficulty INTEGER, maxbpm INTEGER, minbpm INTEGER, mode INTEGER, judge INTEGER, "
            + "longnote INTEGER, bga INTEGER, random INTEGER, date INTEGER, favorite INTEGER, txt INTEGER, "
            + "karinotes INTEGER, adddate INTEGER, exlevel INTEGER);");
        ClearTempTable(songDb, TempGeneratedSongUpsertTable);
    }

    private static void BulkInsertGeneratedSongUpsertTempRows(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
    {
        if (songs == null || songs.Count == 0)
        {
            return;
        }

        string[] columns =
        [
            "hash",
            "title",
            "subtitle",
            "artist",
            "subartist",
            "genre",
            "tag",
            "path",
            "type",
            "folder",
            "stagefile",
            "banner",
            "backbmp",
            "parent",
            "level",
            "difficulty",
            "maxbpm",
            "minbpm",
            "mode",
            "judge",
            "longnote",
            "bga",
            "random",
            "date",
            "favorite",
            "txt",
            "karinotes",
            "adddate",
            "exlevel"
        ];
        int columnCount = columns.Length;
        const int chunkSize = 30;
        for (int offset = 0; offset < songs.Count; offset += chunkSize)
        {
            var chunk = songs.Skip(offset).Take(chunkSize).ToList();
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", chunk.Select(_ => rowPlaceholders));
            var args = new List<object>(chunk.Count * columnCount);
            foreach (BMSFile song in chunk)
            {
                AddGeneratedSongInsertArgs(args, song);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempGeneratedSongUpsertTable
                + " (" + string.Join(",", columns) + ") VALUES " + placeholders + ";",
                [.. args]);
        }
    }

    private static int InsertMissingGeneratedSongsFromTemp(LR2SongDBExtended songDb, string tempTableName)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string[] columns =
        [
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.tag),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.favorite),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.adddate),
            SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel)
        ];
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        return songDb.Execute(
            "INSERT INTO " + songTable + " (" + string.Join(",", columns) + ") "
            + "SELECT " + string.Join(",", columns) + " FROM temp." + tempTableName + " u "
            + "WHERE NOT EXISTS (SELECT 1 FROM " + songTable + " s"
            + " WHERE s." + songPathColumn + " = u.path);");
    }

    private static void InsertChangedPreviousHashesIntoTemp(LR2SongDBExtended songDb, string tempTableName, string tempHashTableName)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        string songHashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        songDb.Execute(
            "INSERT OR IGNORE INTO temp." + tempHashTableName + " (md5) "
            + "SELECT DISTINCT lower(trim(s." + songHashColumn + ")) "
            + "FROM " + songTable + " s "
            + "JOIN temp." + tempTableName + " u ON u.path = s." + songPathColumn + " "
            + "WHERE s." + songHashColumn + " IS NOT NULL "
            + "AND trim(s." + songHashColumn + ") <> '' "
            + "AND s." + songPathColumn + " IN (SELECT path FROM temp." + tempTableName + ") "
            + "AND (u.hash IS NULL OR trim(u.hash) = '' OR lower(trim(s." + songHashColumn + ")) <> lower(trim(u.hash)));");
    }

    private static void AddGeneratedSongInsertArgs(List<object> args, BMSFile song)
    {
        args.Add(song.hash);
        args.Add(song.title);
        args.Add(song.subtitle);
        args.Add(song.artist);
        args.Add(song.subartist);
        args.Add(song.genre);
        args.Add(song.tag);
        args.Add(song.path);
        args.Add(song.type);
        args.Add(song.folder);
        args.Add(song.stagefile);
        args.Add(song.banner);
        args.Add(song.backbmp);
        args.Add(song.parent);
        args.Add(song.level);
        args.Add(song.difficulty);
        args.Add(song.maxbpm);
        args.Add(song.minbpm);
        args.Add(song.mode);
        args.Add(song.judge);
        args.Add(song.longnote);
        args.Add(song.bga);
        args.Add(song.random);
        args.Add(song.date);
        args.Add(song.favorite);
        args.Add(song.txt);
        args.Add(song.karinotes);
        args.Add(song.adddate);
        args.Add(song.exlevel);
    }

    private static void BulkUpdateGeneratedColumns(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
    {
        if (songs == null || songs.Count == 0)
        {
            return;
        }

        PrepareTempGeneratedSongUpdateTable(songDb);
        const int chunkSize = 35;
        for (int offset = 0; offset < songs.Count; offset += chunkSize)
        {
            var chunk = songs.Skip(offset).Take(chunkSize).ToList();
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", 26)) + ")";
            string placeholders = string.Join(",", chunk.Select(_ => rowPlaceholders));
            var args = new List<object>(chunk.Count * 26);
            foreach (BMSFile song in chunk)
            {
                AddGeneratedSongUpdateArgs(args, song);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempGeneratedSongUpdateTable
                + " (path, hash, title, subtitle, artist, subartist, genre, type, folder, stagefile, banner, backbmp, parent, "
                + "level, difficulty, maxbpm, minbpm, mode, judge, longnote, bga, random, date, txt, karinotes, exlevel) "
                + "VALUES " + placeholders + ";",
                [.. args]);
        }

        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        songDb.Execute(
            "UPDATE " + songTable
            + " SET "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.hash)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.title)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.subtitle)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.artist)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.subartist)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.genre)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.type)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.folder)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.stagefile)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.banner)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.backbmp)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.parent)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.level)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.difficulty)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.maxbpm)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.minbpm)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.mode)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.judge)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.longnote)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.bga)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.random)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.date)) + ", "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt)
            + " = COALESCE((SELECT txt FROM temp." + TempGeneratedSongUpdateTable + " u WHERE u.path = " + songTable + "." + songPathColumn + "), "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.karinotes)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.exlevel))
            + " WHERE EXISTS (SELECT 1 FROM temp." + TempGeneratedSongUpdateTable + " u WHERE u.path = "
            + songTable + "." + songPathColumn + ");");
        ClearTempTable(songDb, TempGeneratedSongUpdateTable);
    }

    private static void PrepareTempGeneratedSongUpdateTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempGeneratedSongUpdateTable + " ("
            + "path TEXT PRIMARY KEY, "
            + "hash TEXT, title TEXT, subtitle TEXT, artist TEXT, subartist TEXT, genre TEXT, "
            + "type INTEGER, folder TEXT, stagefile TEXT, banner TEXT, backbmp TEXT, parent TEXT, "
            + "level INTEGER, difficulty INTEGER, maxbpm INTEGER, minbpm INTEGER, mode INTEGER, judge INTEGER, "
            + "longnote INTEGER, bga INTEGER, random INTEGER, date INTEGER, txt INTEGER, karinotes INTEGER, exlevel INTEGER);");
        ClearTempTable(songDb, TempGeneratedSongUpdateTable);
    }

    private static void AddGeneratedSongUpdateArgs(List<object> args, BMSFile song)
    {
        args.Add(song.path);
        args.Add(song.hash);
        args.Add(song.title);
        args.Add(song.subtitle);
        args.Add(song.artist);
        args.Add(song.subartist);
        args.Add(song.genre);
        args.Add(song.type);
        args.Add(song.folder);
        args.Add(song.stagefile);
        args.Add(song.banner);
        args.Add(song.backbmp);
        args.Add(song.parent);
        args.Add(song.level);
        args.Add(song.difficulty);
        args.Add(song.maxbpm);
        args.Add(song.minbpm);
        args.Add(song.mode);
        args.Add(song.judge);
        args.Add(song.longnote);
        args.Add(song.bga);
        args.Add(song.random);
        args.Add(song.date);
        args.Add(song.txt);
        args.Add(song.karinotes);
        args.Add(song.exlevel);
    }

    private static string BuildGeneratedColumnAssignment(string columnName)
    {
        return BuildGeneratedColumnAssignment(columnName, TempGeneratedSongUpdateTable);
    }

    private static string BuildGeneratedColumnAssignment(string columnName, string tempTableName)
    {
        string sqlColumn = columnName;
        return sqlColumn + " = (SELECT " + sqlColumn + " FROM temp." + tempTableName
            + " u WHERE u.path = " + SQLiteTable<LR2SongDB.song>.GetTableName() + "."
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + ")";
    }

    private static string BuildGeneratedColumnChangePredicate(string songAlias, string tempAlias)
    {
        return "(" + string.Join(" OR ",
            songAlias + "." + nameof(LR2SongDB.song.path) + " IS NOT " + tempAlias + "." + nameof(LR2SongDB.song.path),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.hash)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.title)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.subtitle)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.artist)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.subartist)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.genre)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.type)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.folder)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.stagefile)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.banner)),
            BuildDisplayStringColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.backbmp)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.parent)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.level)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.difficulty)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.maxbpm)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.minbpm)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.mode)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.judge)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.longnote)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.bga)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.random)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.date)),
            BuildTextColumnChangedCondition(songAlias, tempAlias),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.karinotes)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.exlevel))) + ")";
    }

    private static string BuildColumnChangedCondition(string songAlias, string tempAlias, string columnName)
    {
        string sqlColumn = columnName;
        return songAlias + "." + sqlColumn + " IS NOT " + tempAlias + "." + sqlColumn;
    }

    private static string BuildDisplayStringColumnChangedCondition(string songAlias, string tempAlias, string columnName)
    {
        string sqlColumn = columnName;
        return "COALESCE(" + songAlias + "." + sqlColumn + ", '') IS NOT COALESCE(" + tempAlias + "." + sqlColumn + ", '')";
    }

    private static string BuildTextColumnChangedCondition(string songAlias, string tempAlias)
    {
        string sqlColumn = nameof(LR2SongDB.song.txt);
        return "(" + tempAlias + "." + sqlColumn + " IS NOT NULL AND "
            + songAlias + "." + sqlColumn + " IS NOT " + tempAlias + "." + sqlColumn + ")";
    }

    private static void UpsertChartDigests(
        LR2SongDBExtended songDb,
        IEnumerable<BMSFile> songs,
        bool ensureTable = true)
    {
        var digestsByMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile song in songs ?? [])
        {
            if (song == null)
            {
                continue;
            }
            string md5 = NormalizeHashForTemp(song.hash);
            string sha256 = NormalizeSha256ForTemp(song.sha256);
            if (string.IsNullOrWhiteSpace(md5) || string.IsNullOrWhiteSpace(sha256))
            {
                continue;
            }
            digestsByMd5[md5] = sha256;
        }
        if (digestsByMd5.Count == 0)
        {
            return;
        }

        string digestTable = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        string digestMd5Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.md5);
        string digestSha256Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.sha256);
        if (ensureTable)
        {
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        }

        PrepareTempChartDigestUpsertTable(songDb);
        const int chunkSize = 400;
        List<KeyValuePair<string, string>> digests = [.. digestsByMd5];
        for (int offset = 0; offset < digests.Count; offset += chunkSize)
        {
            var chunk = digests.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "(?, ?)"));
            var args = new List<object>(chunk.Count * 2);
            foreach (KeyValuePair<string, string> digest in chunk)
            {
                args.Add(digest.Key);
                args.Add(digest.Value);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempChartDigestUpsertTable
                + " (md5, sha256) VALUES " + placeholders + ";",
                [.. args]);
        }
        songDb.Execute(
            "INSERT OR IGNORE INTO " + digestTable + " (" + digestMd5Column + ", " + digestSha256Column + ") "
            + "SELECT md5, sha256 FROM temp." + TempChartDigestUpsertTable + ";");
        songDb.Execute(
            "UPDATE " + digestTable
            + " SET " + digestSha256Column + " = (SELECT u.sha256 FROM temp." + TempChartDigestUpsertTable
            + " u WHERE u.md5 = " + digestTable + "." + digestMd5Column + ") "
            + "WHERE rowid IN (SELECT d.rowid FROM temp." + TempChartDigestUpsertTable
            + " u JOIN " + digestTable + " d ON d." + digestMd5Column + " = u.md5 "
            + "WHERE d." + digestSha256Column + " IS NOT u.sha256) "
            + "AND " + digestSha256Column + " IS NOT (SELECT u.sha256 FROM temp." + TempChartDigestUpsertTable
            + " u WHERE u.md5 = " + digestTable + "." + digestMd5Column + ");");
        ClearTempTable(songDb, TempChartDigestUpsertTable);
    }

    private static void DeleteOrphanedChartDigests(LR2SongDBExtended songDb, IEnumerable<string> md5s)
    {
        List<string> normalizedHashes = [.. (md5s ?? [])
            .Select(NormalizeHashForTemp)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (normalizedHashes.Count == 0)
        {
            return;
        }

        PrepareTempHashTable(songDb, TempDeletedSongHashTable);
        InsertHashesIntoTempHashTable(songDb, TempDeletedSongHashTable, normalizedHashes);
        DeleteOrphanedChartDigestsFromTemp(songDb, TempDeletedSongHashTable);
        ClearTempTable(songDb, TempDeletedSongHashTable);
    }

    private static void InsertHashesIntoTempHashTable(LR2SongDBExtended songDb, string tableName, IReadOnlyList<string> hashes)
    {
        if (hashes == null || hashes.Count == 0)
        {
            return;
        }

        const int chunkSize = 500;
        for (int offset = 0; offset < hashes.Count; offset += chunkSize)
        {
            var chunk = hashes.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "(?)"));
            songDb.Execute(
                "INSERT OR IGNORE INTO temp." + tableName + " (md5) VALUES " + placeholders + ";",
                [.. chunk.Cast<object>()]);
        }
    }

    private static void DeleteOrphanedChartDigestsFromTemp(
        LR2SongDBExtended songDb,
        string tempHashTableName,
        bool assumeLiveTablesExist = false)
    {
        string digestTable = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        string digestMd5Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.md5);
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songHashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        string bmsonTable = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        string bmsonMd5Column = SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.md5);
        if (!assumeLiveTablesExist && !TableExists(songDb, digestTable))
        {
            return;
        }

        var liveExistsClauses = new List<string>();
        if (assumeLiveTablesExist || TableExists(songDb, songTable))
        {
            liveExistsClauses.Add(
                "EXISTS (SELECT 1 FROM " + songTable + " s WHERE s." + songHashColumn
                + " = " + digestTable + "." + digestMd5Column + ")");
        }
        if (assumeLiveTablesExist || TableExists(songDb, bmsonTable))
        {
            liveExistsClauses.Add(
                "EXISTS (SELECT 1 FROM " + bmsonTable + " b WHERE b." + bmsonMd5Column
                + " = " + digestTable + "." + digestMd5Column + ")");
        }
        string liveExistsCondition = liveExistsClauses.Count == 0 ? "0" : string.Join(" OR ", liveExistsClauses);
        songDb.Execute(
            "DELETE FROM " + digestTable
            + " WHERE " + digestMd5Column + " IN (SELECT md5 FROM temp." + tempHashTableName + ") "
            + "AND NOT (" + liveExistsCondition + ");");
    }

    private static string NormalizeHashForTemp(string hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
    }

    private static string NormalizeSha256ForTemp(string hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
    }

    private static bool TableExists(LR2SongDBExtended songDb, string tableName)
    {
        return songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
            tableName) > 0;
    }

    internal static void UpdateDate(LR2SongDBExtended songDb, string path, int date)
    {
        UpdateMetadata(songDb, path, date, null);
    }

    internal static void UpdateMetadata(LR2SongDBExtended songDb, string path, int date, int? textFlag)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (textFlag.HasValue)
        {
            songDb.Execute(
                "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
                + " SET " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " = ?"
                + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
                date,
                textFlag.Value,
                path);
            return;
        }
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
            date,
            path);
    }

    private static GeneratedSongRow FindSongByPath(LR2SongDBExtended songDb, string path)
    {
        List<GeneratedSongRow> existingRows = songDb.Query<GeneratedSongRow>(
            "SELECT "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " AS path, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " AS hash, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title) + " AS title, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle) + " AS subtitle, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist) + " AS artist, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist) + " AS subartist, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre) + " AS genre, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type) + " AS type, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder) + " AS folder, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile) + " AS stagefile, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner) + " AS banner, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp) + " AS backbmp, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent) + " AS parent, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level) + " AS level, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty) + " AS difficulty, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm) + " AS maxbpm, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm) + " AS minbpm, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode) + " AS mode, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge) + " AS judge, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote) + " AS longnote, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga) + " AS bga, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random) + " AS random, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " AS date, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " AS txt, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes) + " AS karinotes, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel) + " AS exlevel"
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " = ? LIMIT 1;",
            path);
        return existingRows.Count == 0 ? null : existingRows[0];
    }

    private static Dictionary<string, GeneratedSongRow> FindSongsByPaths(LR2SongDBExtended songDb, IEnumerable<string> paths)
    {
        var result = new Dictionary<string, GeneratedSongRow>(StringComparer.Ordinal);
        List<string> normalizedPaths = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        if (normalizedPaths.Count == 0)
        {
            return result;
        }

        const int chunkSize = 500;
        for (int offset = 0; offset < normalizedPaths.Count; offset += chunkSize)
        {
            var chunk = normalizedPaths.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "?"));
            string sql = "SELECT "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " AS path, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " AS hash, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title) + " AS title, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle) + " AS subtitle, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist) + " AS artist, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist) + " AS subartist, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre) + " AS genre, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type) + " AS type, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder) + " AS folder, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile) + " AS stagefile, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner) + " AS banner, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp) + " AS backbmp, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent) + " AS parent, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level) + " AS level, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty) + " AS difficulty, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm) + " AS maxbpm, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm) + " AS minbpm, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode) + " AS mode, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge) + " AS judge, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote) + " AS longnote, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga) + " AS bga, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random) + " AS random, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " AS date, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " AS txt, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes) + " AS karinotes, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel) + " AS exlevel"
                + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
                + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
                + " IN (" + placeholders + ");";
            foreach (GeneratedSongRow row in songDb.Query<GeneratedSongRow>(sql, [.. chunk.Cast<object>()]))
            {
                if (!string.IsNullOrWhiteSpace(row?.path) && !result.ContainsKey(row.path))
                {
                    result[row.path] = row;
                }
            }
        }
        return result;
    }


    private static bool HasSameGeneratedColumns(BMSFile expected, GeneratedSongRow existing)
    {
        return expected != null
            && existing != null
            && string.Equals(expected.path, existing.path, StringComparison.Ordinal)
            && string.Equals(expected.hash, existing.hash, StringComparison.Ordinal)
            && HasSameGeneratedDisplayString(expected.title, existing.title)
            && HasSameGeneratedDisplayString(expected.subtitle, existing.subtitle)
            && HasSameGeneratedDisplayString(expected.artist, existing.artist)
            && HasSameGeneratedDisplayString(expected.subartist, existing.subartist)
            && HasSameGeneratedDisplayString(expected.genre, existing.genre)
            && expected.type == existing.type
            && string.Equals(expected.folder, existing.folder, StringComparison.Ordinal)
            && HasSameGeneratedDisplayString(expected.stagefile, existing.stagefile)
            && HasSameGeneratedDisplayString(expected.banner, existing.banner)
            && HasSameGeneratedDisplayString(expected.backbmp, existing.backbmp)
            && string.Equals(expected.parent, existing.parent, StringComparison.Ordinal)
            && expected.level == existing.level
            && expected.difficulty == existing.difficulty
            && expected.maxbpm == existing.maxbpm
            && expected.minbpm == existing.minbpm
            && expected.mode == existing.mode
            && expected.judge == existing.judge
            && expected.longnote == existing.longnote
            && expected.bga == existing.bga
            && expected.random == existing.random
            && expected.date == existing.date
            && (!expected.txt.HasValue || expected.txt == existing.txt)
            && expected.karinotes == existing.karinotes
            && expected.exlevel == existing.exlevel;
    }

    private static bool HasSameGeneratedDisplayString(string expected, string existing)
    {
        return string.Equals(expected ?? string.Empty, existing ?? string.Empty, StringComparison.Ordinal);
    }


    private static string DescribeGeneratedColumnMismatch(BMSFile expected, GeneratedSongRow existing)
    {
        var names = new List<string>();
        AddMismatchName(names, "path", !string.Equals(expected.path, existing.path, StringComparison.Ordinal));
        AddMismatchName(names, "hash", !string.Equals(expected.hash, existing.hash, StringComparison.Ordinal));
        AddMismatchName(names, "title", !HasSameGeneratedDisplayString(expected.title, existing.title));
        AddMismatchName(names, "subtitle", !HasSameGeneratedDisplayString(expected.subtitle, existing.subtitle));
        AddMismatchName(names, "artist", !HasSameGeneratedDisplayString(expected.artist, existing.artist));
        AddMismatchName(names, "subartist", !HasSameGeneratedDisplayString(expected.subartist, existing.subartist));
        AddMismatchName(names, "genre", !HasSameGeneratedDisplayString(expected.genre, existing.genre));
        AddMismatchName(names, "type", expected.type != existing.type);
        AddMismatchName(names, "folder", !string.Equals(expected.folder, existing.folder, StringComparison.Ordinal));
        AddMismatchName(names, "stagefile", !HasSameGeneratedDisplayString(expected.stagefile, existing.stagefile));
        AddMismatchName(names, "banner", !HasSameGeneratedDisplayString(expected.banner, existing.banner));
        AddMismatchName(names, "backbmp", !HasSameGeneratedDisplayString(expected.backbmp, existing.backbmp));
        AddMismatchName(names, "parent", !string.Equals(expected.parent, existing.parent, StringComparison.Ordinal));
        AddMismatchName(names, "level", expected.level != existing.level);
        AddMismatchName(names, "difficulty", expected.difficulty != existing.difficulty);
        AddMismatchName(names, "maxbpm", expected.maxbpm != existing.maxbpm);
        AddMismatchName(names, "minbpm", expected.minbpm != existing.minbpm);
        AddMismatchName(names, "mode", expected.mode != existing.mode);
        AddMismatchName(names, "judge", expected.judge != existing.judge);
        AddMismatchName(names, "longnote", expected.longnote != existing.longnote);
        AddMismatchName(names, "bga", expected.bga != existing.bga);
        AddMismatchName(names, "random", expected.random != existing.random);
        AddMismatchName(names, "date", expected.date != existing.date);
        AddMismatchName(names, "txt", expected.txt.HasValue && expected.txt != existing.txt);
        AddMismatchName(names, "karinotes", expected.karinotes != existing.karinotes);
        AddMismatchName(names, "exlevel", expected.exlevel != existing.exlevel);
        return names.Count == 0 ? "unknown" : string.Join(",", names);
    }

    private static void AddMismatchName(List<string> names, string name, bool mismatched)
    {
        if (mismatched)
        {
            names.Add(name);
        }
    }

    private static void UpdateGeneratedColumns(LR2SongDBExtended songDb, BMSFile song)
    {
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " = COALESCE(?, " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
            song.path,
            song.hash,
            song.title,
            song.subtitle,
            song.artist,
            song.subartist,
            song.genre,
            song.type,
            song.folder,
            song.stagefile,
            song.banner,
            song.backbmp,
            song.parent,
            song.level,
            song.difficulty,
            song.maxbpm,
            song.minbpm,
            song.mode,
            song.judge,
            song.longnote,
            song.bga,
            song.random,
            song.date,
            song.txt,
            song.karinotes,
            song.exlevel,
            song.path);
    }

    private sealed class ChartInfoSongProjectionIdentityRow
    {
        public string path { get; set; }

        public string md5 { get; set; }
    }

    private sealed class GeneratedSongRow
    {
        public string path { get; set; }

        public string hash { get; set; }

        public string title { get; set; }

        public string subtitle { get; set; }

        public string artist { get; set; }

        public string subartist { get; set; }

        public string genre { get; set; }

        public int? type { get; set; }

        public string folder { get; set; }

        public string stagefile { get; set; }

        public string banner { get; set; }

        public string backbmp { get; set; }

        public string parent { get; set; }

        public int? level { get; set; }

        public int? difficulty { get; set; }

        public int? maxbpm { get; set; }

        public int? minbpm { get; set; }

        public int? mode { get; set; }

        public int? judge { get; set; }

        public int? longnote { get; set; }

        public int? bga { get; set; }

        public int? random { get; set; }

        public int? date { get; set; }

        public int? txt { get; set; }

        public int? karinotes { get; set; }

        public int? exlevel { get; set; }
    }

}
