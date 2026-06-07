using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct Lr2SongPruneResult(int deletedCount, int currentPathCount)
{
    public int DeletedCount { get; } = deletedCount;

    public int CurrentPathCount { get; } = currentPathCount;
}

internal readonly struct Lr2SongDifficultyNormalizationResult(int scannedCount, int updatedCount)
{
    public int ScannedCount { get; } = scannedCount;

    public int UpdatedCount { get; } = updatedCount;
}

internal static class Lr2SongDbWriter
{
    private const string TempCurrentSongPathTable = "lr2_full_generation_current_song_path";

    private const string TempDeletedSongHashTable = "lr2_full_generation_deleted_song_hash";

    private const string TempDifficultyUpdateTable = "lr2_full_generation_difficulty_update";

    private const string TempGeneratedSongUpdateTable = "lr2_full_generation_generated_song_update";

    private const string TempGeneratedSongUpsertTable = "lr2_full_generation_generated_song_upsert";

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

    internal static int UpsertGeneratedSongsForFullGeneration(LR2SongDBExtended songDb, IReadOnlyList<BMSFile> songs)
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

        foreach (BMSFile song in rows)
        {
            Lr2SongRowEnricher.EnrichGeneratedSong(song);
            ApplyGeneratedPersistenceDefaults(song, isNewRow: true);
        }

        PrepareTempGeneratedSongUpsertTable(songDb);
        BulkInsertGeneratedSongUpsertTempRows(songDb, rows);
        PrepareTempHashTable(songDb, TempDeletedSongHashTable);
        InsertChangedPreviousHashesIntoTemp(songDb, TempGeneratedSongUpsertTable, TempDeletedSongHashTable);

        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
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
            + " = COALESCE((SELECT txt FROM temp." + TempGeneratedSongUpsertTable + " u WHERE u.path = " + songTable + "." + songPathColumn + " COLLATE NOCASE), "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.karinotes), TempGeneratedSongUpsertTable) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.exlevel), TempGeneratedSongUpsertTable)
            + " WHERE rowid IN ("
            + "SELECT s.rowid FROM temp." + TempGeneratedSongUpsertTable + " u "
            + "JOIN " + songTable + " s INDEXED BY " + BmsLibraryDbGateway.SongPathNocaseIndexName
            + " ON s." + songPathColumn + " = u.path COLLATE NOCASE "
            + "WHERE s." + songPathColumn + " COLLATE NOCASE IN (SELECT path FROM temp." + TempGeneratedSongUpsertTable + ") "
            + "AND " + BuildGeneratedColumnChangePredicate("s", "u") + ");");

        int inserted = InsertMissingGeneratedSongsFromTemp(songDb, TempGeneratedSongUpsertTable);
        UpsertChartDigests(songDb, rows);
        DeleteOrphanedChartDigestsFromTemp(songDb, TempDeletedSongHashTable);
        ClearTempTable(songDb, TempDeletedSongHashTable);
        ClearTempTable(songDb, TempGeneratedSongUpsertTable);
        return updated + inserted;
    }

    internal static Lr2SongPruneResult DeleteSongsExceptCurrentPaths(
        LR2SongDBExtended songDb,
        IEnumerable<string> currentPaths)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        string[] sourcePaths = [.. (currentPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();

        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            PrepareTempPathTable(songDb, TempCurrentSongPathTable);
            BulkInsertTempPaths(songDb, TempCurrentSongPathTable, sourcePaths);

            PrepareTempHashTable(songDb, TempDeletedSongHashTable);
            string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
            string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
            string songHashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
            string maintenanceTable = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
            string maintenancePathColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path);

            songDb.Execute(
                "INSERT OR IGNORE INTO temp." + TempDeletedSongHashTable + " (md5) "
                + "SELECT DISTINCT lower(trim(s." + songHashColumn + ")) "
                + "FROM " + songTable + " s "
                + "WHERE s." + songHashColumn + " IS NOT NULL "
                + "AND trim(s." + songHashColumn + ") <> '' "
                + "AND NOT EXISTS (SELECT 1 FROM temp." + TempCurrentSongPathTable + " c "
                + "WHERE c.path = s." + songPathColumn + " COLLATE NOCASE);");

            songDb.Execute(
                "DELETE FROM " + maintenanceTable
                + " WHERE " + maintenancePathColumn + " IN ("
                + "SELECT s." + songPathColumn + " FROM " + songTable + " s "
                + "WHERE NOT EXISTS (SELECT 1 FROM temp." + TempCurrentSongPathTable + " c "
                + "WHERE c.path = s." + songPathColumn + " COLLATE NOCASE));");

            int deleted = songDb.Execute(
                "DELETE FROM " + songTable
                + " WHERE rowid IN ("
                + "SELECT s.rowid FROM " + songTable + " s "
                + "WHERE NOT EXISTS (SELECT 1 FROM temp." + TempCurrentSongPathTable + " c "
                + "WHERE c.path = s." + songPathColumn + " COLLATE NOCASE));");

            DeleteOrphanedChartDigestsFromTemp(songDb, TempDeletedSongHashTable);
            ClearTempTable(songDb, TempDeletedSongHashTable);
            ClearTempTable(songDb, TempCurrentSongPathTable);
            songDb.Commit();
            return new Lr2SongPruneResult(deleted, sourcePaths.Length);
        }
        catch
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    internal static Lr2SongDifficultyNormalizationResult NormalizeUndefinedSongDifficulties(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        songDb.CreateTable<LR2SongDB.song>();
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        List<SongDifficultyRow> rows = songDb.Query<SongDifficultyRow>(
            "SELECT path, folder, mode, difficulty, karinotes "
            + "FROM " + songTable + " "
            + "WHERE path IS NOT NULL AND trim(path) <> '' "
            + "ORDER BY folder, mode, karinotes;");
        if (rows.Count == 0)
        {
            return new Lr2SongDifficultyNormalizationResult(0, 0);
        }

        var updates = new List<SongDifficultyUpdate>();
        string currentFolder = null;
        int? currentMode = null;
        int difficulty = 0;
        bool hasCurrentGroup = false;
        foreach (SongDifficultyRow row in rows)
        {
            int rowMode = row.mode.GetValueOrDefault();
            int rowDifficulty = row.difficulty.GetValueOrDefault();
            if (rowDifficulty >= 0 && rowDifficulty <= 5)
            {
                if (!row.difficulty.HasValue)
                {
                    updates.Add(new SongDifficultyUpdate
                    {
                        path = row.path,
                        difficulty = rowDifficulty
                    });
                }
                currentFolder = row.folder;
                currentMode = rowMode;
                difficulty = rowDifficulty;
                hasCurrentGroup = true;
                continue;
            }

            if (hasCurrentGroup
                && string.Equals(currentFolder, row.folder, StringComparison.Ordinal)
                && currentMode == rowMode)
            {
                difficulty++;
                if (difficulty == 5)
                {
                    difficulty = 4;
                }
                else if (difficulty < 0)
                {
                    difficulty = 2;
                }
                else if (difficulty > 5)
                {
                    difficulty = 5;
                }
            }
            else
            {
                difficulty = 2;
            }

            updates.Add(new SongDifficultyUpdate
            {
                path = row.path,
                difficulty = difficulty
            });
            currentFolder = row.folder;
            currentMode = rowMode;
            hasCurrentGroup = true;
        }

        if (updates.Count == 0)
        {
            return new Lr2SongDifficultyNormalizationResult(rows.Count, 0);
        }

        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            PrepareTempDifficultyUpdateTable(songDb);
            const int chunkSize = 200;
            for (int offset = 0; offset < updates.Count; offset += chunkSize)
            {
                List<SongDifficultyUpdate> chunk = updates.Skip(offset).Take(chunkSize).ToList();
                string placeholders = string.Join(",", chunk.Select(_ => "(?,?)"));
                var args = new List<object>(chunk.Count * 2);
                foreach (SongDifficultyUpdate update in chunk)
                {
                    args.Add(update.path);
                    args.Add(update.difficulty);
                }
                songDb.Execute(
                    "INSERT OR REPLACE INTO temp." + TempDifficultyUpdateTable
                    + " (path, difficulty) VALUES " + placeholders + ";",
                    [.. args]);
            }

            string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
            string difficultyColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty);
            int updated = songDb.Execute(
                "UPDATE " + songTable
                + " SET " + difficultyColumn + " = ("
                + "SELECT u.difficulty FROM temp." + TempDifficultyUpdateTable + " u "
                + "WHERE u.path = " + songTable + "." + songPathColumn + " COLLATE NOCASE) "
                + "WHERE EXISTS (SELECT 1 FROM temp." + TempDifficultyUpdateTable + " u "
                + "WHERE u.path = " + songTable + "." + songPathColumn + " COLLATE NOCASE);");
            ClearTempTable(songDb, TempDifficultyUpdateTable);
            songDb.Commit();
            return new Lr2SongDifficultyNormalizationResult(rows.Count, updated);
        }
        catch
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
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

    private static void PrepareTempPathTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("CREATE TEMP TABLE IF NOT EXISTS temp." + tableName + " (path TEXT PRIMARY KEY COLLATE NOCASE);");
        ClearTempTable(songDb, tableName);
    }

    private static void PrepareTempHashTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("CREATE TEMP TABLE IF NOT EXISTS temp." + tableName + " (md5 TEXT PRIMARY KEY);");
        ClearTempTable(songDb, tableName);
    }

    private static void ClearTempTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("DELETE FROM temp." + tableName + ";");
    }

    private static void BulkInsertTempPaths(
        LR2SongDBExtended songDb,
        string tableName,
        IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
        {
            return;
        }

        const int chunkSize = 200;
        for (int offset = 0; offset < paths.Count; offset += chunkSize)
        {
            string[] chunk = [.. paths.Skip(offset).Take(chunkSize)];
            string placeholders = string.Join(",", chunk.Select(_ => "(?)"));
            object[] args = [.. chunk.Cast<object>()];
            songDb.Execute(
                "INSERT OR IGNORE INTO temp." + tableName + " (path) VALUES " + placeholders + ";",
                args);
        }
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
            List<BMSFile> chunk = songs.Skip(offset).Take(chunkSize).ToList();
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
            + "path TEXT PRIMARY KEY COLLATE NOCASE, type INTEGER, folder TEXT, stagefile TEXT, banner TEXT, backbmp TEXT, parent TEXT, "
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
            List<BMSFile> chunk = songs.Skip(offset).Take(chunkSize).ToList();
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
            + "WHERE NOT EXISTS (SELECT 1 FROM " + songTable + " s INDEXED BY " + BmsLibraryDbGateway.SongPathNocaseIndexName
            + " WHERE s." + songPathColumn + " = u.path COLLATE NOCASE);");
    }

    private static void InsertChangedPreviousHashesIntoTemp(LR2SongDBExtended songDb, string tempTableName, string tempHashTableName)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songPathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        string songHashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        songDb.Execute(
            "INSERT OR IGNORE INTO temp." + tempHashTableName + " (md5) "
            + "SELECT DISTINCT lower(trim(s." + songHashColumn + ")) "
            + "FROM " + songTable + " s INDEXED BY " + BmsLibraryDbGateway.SongPathNocaseIndexName + " "
            + "JOIN temp." + tempTableName + " u ON u.path = s." + songPathColumn + " COLLATE NOCASE "
            + "WHERE s." + songHashColumn + " IS NOT NULL "
            + "AND trim(s." + songHashColumn + ") <> '' "
            + "AND s." + songPathColumn + " COLLATE NOCASE IN (SELECT path FROM temp." + tempTableName + ") "
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
            List<BMSFile> chunk = songs.Skip(offset).Take(chunkSize).ToList();
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
            + " = COALESCE((SELECT txt FROM temp." + TempGeneratedSongUpdateTable + " u WHERE u.path = " + songTable + "." + songPathColumn + " COLLATE NOCASE), "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.karinotes)) + ", "
            + BuildGeneratedColumnAssignment(nameof(LR2SongDB.song.exlevel))
            + " WHERE EXISTS (SELECT 1 FROM temp." + TempGeneratedSongUpdateTable + " u WHERE u.path = "
            + songTable + "." + songPathColumn + " COLLATE NOCASE);");
        ClearTempTable(songDb, TempGeneratedSongUpdateTable);
    }

    private static void PrepareTempGeneratedSongUpdateTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempGeneratedSongUpdateTable + " ("
            + "path TEXT PRIMARY KEY COLLATE NOCASE, "
            + "hash TEXT, title TEXT, subtitle TEXT, artist TEXT, subartist TEXT, genre TEXT, "
            + "type INTEGER, folder TEXT, stagefile TEXT, banner TEXT, backbmp TEXT, parent TEXT, "
            + "level INTEGER, difficulty INTEGER, maxbpm INTEGER, minbpm INTEGER, mode INTEGER, judge INTEGER, "
            + "longnote INTEGER, bga INTEGER, random INTEGER, date INTEGER, txt INTEGER, karinotes INTEGER, exlevel INTEGER);");
        ClearTempTable(songDb, TempGeneratedSongUpdateTable);
    }

    private static void PrepareTempDifficultyUpdateTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempDifficultyUpdateTable + " ("
            + "path TEXT PRIMARY KEY COLLATE NOCASE, "
            + "difficulty INTEGER NOT NULL);");
        ClearTempTable(songDb, TempDifficultyUpdateTable);
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
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " COLLATE NOCASE)";
    }

    private static string BuildGeneratedColumnChangePredicate(string songAlias, string tempAlias)
    {
        return "(" + string.Join(" OR ",
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.hash)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.title)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.subtitle)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.artist)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.subartist)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.genre)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.type)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.folder)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.stagefile)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.banner)),
            BuildColumnChangedCondition(songAlias, tempAlias, nameof(LR2SongDB.song.backbmp)),
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

    private static string BuildTextColumnChangedCondition(string songAlias, string tempAlias)
    {
        string sqlColumn = nameof(LR2SongDB.song.txt);
        return "(" + tempAlias + "." + sqlColumn + " IS NOT NULL AND "
            + songAlias + "." + sqlColumn + " IS NOT " + tempAlias + "." + sqlColumn + ")";
    }

    private static void UpsertChartDigests(LR2SongDBExtended songDb, IEnumerable<BMSFile> songs)
    {
        var digestsByMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile song in songs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.hash) || string.IsNullOrWhiteSpace(song.sha256))
            {
                continue;
            }
            digestsByMd5[song.hash] = song.sha256;
        }
        if (digestsByMd5.Count == 0)
        {
            return;
        }

        string digestTable = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        string digestMd5Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.md5);
        string digestSha256Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.sha256);
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();

        const int chunkSize = 400;
        List<KeyValuePair<string, string>> digests = [.. digestsByMd5];
        for (int offset = 0; offset < digests.Count; offset += chunkSize)
        {
            List<KeyValuePair<string, string>> chunk = digests.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "(?, ?)"));
            var args = new List<object>(chunk.Count * 2);
            foreach (KeyValuePair<string, string> digest in chunk)
            {
                args.Add(digest.Key);
                args.Add(digest.Value);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO " + digestTable + " ("
                + digestMd5Column + ", " + digestSha256Column + ") VALUES " + placeholders + ";",
                [.. args]);
        }
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
            List<string> chunk = hashes.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "(?)"));
            songDb.Execute(
                "INSERT OR IGNORE INTO temp." + tableName + " (md5) VALUES " + placeholders + ";",
                [.. chunk.Cast<object>()]);
        }
    }

    private static void DeleteOrphanedChartDigestsFromTemp(LR2SongDBExtended songDb, string tempHashTableName)
    {
        string digestTable = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        string digestMd5Column = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.md5);
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string songHashColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash);
        string bmsonTable = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        string bmsonMd5Column = SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.md5);
        if (!TableExists(songDb, digestTable))
        {
            return;
        }

        var liveExistsClauses = new List<string>();
        if (TableExists(songDb, songTable))
        {
            liveExistsClauses.Add(
                "EXISTS (SELECT 1 FROM " + songTable + " s WHERE s." + songHashColumn
                + " = " + digestTable + "." + digestMd5Column + ")");
        }
        if (TableExists(songDb, bmsonTable))
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
        var existingRows = songDb.Query<GeneratedSongRow>(
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
        var result = new Dictionary<string, GeneratedSongRow>(StringComparer.OrdinalIgnoreCase);
        List<string> normalizedPaths = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (normalizedPaths.Count == 0)
        {
            return result;
        }

        const int chunkSize = 500;
        for (int offset = 0; offset < normalizedPaths.Count; offset += chunkSize)
        {
            List<string> chunk = normalizedPaths.Skip(offset).Take(chunkSize).ToList();
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
            && string.Equals(expected.hash, existing.hash, StringComparison.Ordinal)
            && string.Equals(expected.title, existing.title, StringComparison.Ordinal)
            && string.Equals(expected.subtitle, existing.subtitle, StringComparison.Ordinal)
            && string.Equals(expected.artist, existing.artist, StringComparison.Ordinal)
            && string.Equals(expected.subartist, existing.subartist, StringComparison.Ordinal)
            && string.Equals(expected.genre, existing.genre, StringComparison.Ordinal)
            && expected.type == existing.type
            && string.Equals(expected.folder, existing.folder, StringComparison.Ordinal)
            && string.Equals(expected.stagefile, existing.stagefile, StringComparison.Ordinal)
            && string.Equals(expected.banner, existing.banner, StringComparison.Ordinal)
            && string.Equals(expected.backbmp, existing.backbmp, StringComparison.Ordinal)
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

    private static void UpdateGeneratedColumns(LR2SongDBExtended songDb, BMSFile song)
    {
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET "
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

    private sealed class SongDifficultyRow
    {
        public string path { get; set; }

        public string folder { get; set; }

        public int? mode { get; set; }

        public int? difficulty { get; set; }

        public int? karinotes { get; set; }
    }

    private sealed class SongDifficultyUpdate
    {
        public string path { get; set; }

        public int difficulty { get; set; }
    }
}
