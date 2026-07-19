using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト永続化の DB 境界です。
/// schema、hydration、playlist/course/entry の置換、削除、dump/restore を
/// ここで transaction とともに扱い、<see cref="BMSPlaylist"/> へ DB 実装を漏らしません。
/// </summary>
internal sealed class PlaylistPersistenceRepository
{
    private readonly string songDbPath;

    internal PlaylistPersistenceRepository(string songDbPath)
    {
        this.songDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
    }

    internal static void EnsureSchema(string songDbPath)
    {
        if (songDbPath == null)
        {
            throw new ArgumentNullException(nameof(songDbPath));
        }
        if (!File.Exists(songDbPath))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, songDbPath), nameof(songDbPath));
        }
        using var db = new LR2SongDBExtended(songDbPath);
        EnsureSchema(db);
    }

    internal static void EnsureSchema(LR2SongDBExtended db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        db.CreateTable<LR2SongDBExtended.playlist>();
        db.CreateTable<LR2SongDBExtended.playlist_course>();
        db.CreateTable<LR2SongDBExtended.playlist_entry>();
        EnsurePlaylistMetadataColumns(db);
        EnsurePlaylistEntrySha256Column(db);
        EnsureCustomFolderOutputStatusTable(db);
        EnsurePlaylistCourseIndexes(db);
        RebuildPlaylistEntryIndexes(db);
    }

    /// <summary>
    /// custom-folder の durable status table を現在の schema へ揃えます。
    /// </summary>
    internal static void EnsureCustomFolderOutputStatusTable(LR2SongDBExtended db)
    {
        const string createSql =
            "CREATE TABLE IF NOT EXISTS playlist_custom_folder_output_status ("
            + "playlist_id INTEGER PRIMARY KEY,"
            + "output_directory TEXT NOT NULL,"
            + "is_root_folder INTEGER NOT NULL,"
            + "ignore_folder_output INTEGER NOT NULL,"
            + "entry_type INTEGER NOT NULL,"
            + "folder_sort_key INTEGER NOT NULL,"
            + "folder_sort_ascending INTEGER NOT NULL,"
            + "enable_unsent INTEGER NOT NULL,"
            + "header_sha256 TEXT NULL,"
            + "data_sha256 TEXT NULL,"
            + "last_update_ticks INTEGER NOT NULL,"
            + "physical_mtime_signature TEXT NOT NULL"
            + ");";
        db.Execute(createSql);
        if (!IsCustomFolderOutputStatusSchemaCurrent(db))
        {
            db.Execute("DROP TABLE IF EXISTS playlist_custom_folder_output_status;");
            db.Execute(createSql);
        }
    }

    internal List<BMSTable> LoadPlaylistHeaders()
    {
        List<BMSTable> tables;
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
        {
            tables = [.. (from table in db.Table<BMSTable>()
                          orderby table.name
                          select table)];
            AttachPersistedCourses(db, tables);
        }
        foreach (BMSTable table in tables)
        {
            table.MarkEntriesNotLoaded();
        }
        return tables;
    }

    internal IReadOnlyList<BMSTableEntry> LoadPersistedPlaylistEntries(int? playlistId, bool activeOnly)
    {
        if (!playlistId.HasValue)
        {
            return [];
        }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        using (BMSTableEntry.BeginBulkLoadParseSuppression())
        {
            string sql = "SELECT * FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName()
                + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.playlist_id) + " = ?";
            if (activeOnly)
            {
                sql += " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.is_removed) + " = 0";
            }
            sql += ";";
            return db.Query<BMSTableEntry>(sql, playlistId.Value);
        }
    }

    internal PlaylistEntriesHydrationLoadResult LoadStartupPlaylistEntries()
    {
        var result = new PlaylistEntriesHydrationLoadResult();
        using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        result.ReadOnly = db.IsReadOnlyConnection;
        result.DbLockWaitMs = db.ProcessLockWaitMs;
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.playlist_id);
        string sql =
            "SELECT "
            + string.Join(", ",
            [
                playlistIdColumn,
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.sha256),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.level),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.title),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.artist),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.folder),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.lr2_bmsid),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.url),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.url_diff),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.name_diff),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.org_md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.adddate),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.comment),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.memo),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.is_removed)
            ])
            + " FROM " + tableName
            + " WHERE " + playlistIdColumn + " IS NOT NULL;";
        var stopwatch = Stopwatch.StartNew();
        var command = (LR2SongDBExtended.SQLiteCommandExtended)db.CreateCommand(sql);
        command.ForEachRawValueAsString(values =>
        {
            if (values == null || values.Length < 16 || !TryParseNullableInt(values[0], out int playlistId))
            {
                return;
            }
            result.Entries.Add(BMSTableEntry.CreateHydratedPlaylistEntry(
                playlistId,
                values[1],
                values[2],
                ParseNullableDouble(values[3]),
                values[4],
                values[5],
                values[6],
                values[7],
                values[8],
                values[9],
                values[10],
                values[11],
                ParseNullableDateTime(values[12]),
                values[13],
                values[14],
                ParseBoolean(values[15])));
        });
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    internal void ReplaceTablesWithEntries(IEnumerable<BMSTable> tables, Action<int, int, string> progressCallback = null)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (tableList.Count == 0)
        {
            return;
        }
        using var db = new LR2SongDBExtended(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            for (int index = 0; index < tableList.Count; index++)
            {
                BMSTable table = tableList[index];
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                ReplacePersistedCourses(db, table);
                DeletePersistedEntries(db, table.playlist_id);
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    if (entry == null)
                    {
                        throw new InvalidOperationException("Playlist persistence cannot contain a null entry.");
                    }
                    entry.NormalizeForPlaylistPersistence();
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
                progressCallback?.Invoke(index + 1, tableList.Count, table.name ?? string.Empty);
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    internal void ReplaceHeaders(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (tableList.Count == 0)
        {
            return;
        }
        using var db = new LR2SongDBExtended(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (BMSTable table in tableList)
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                ReplacePersistedCourses(db, table);
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    internal void ReplaceHeader(BMSTable table)
    {
        ReplaceHeaders([table]);
    }

    internal void ReplaceEntry(BMSTableEntry entry, BMSTable owningTable)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        using var db = new LR2SongDBExtended(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            db.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName()
                + " WHERE " + BuildPlaylistEntryReplacementPredicate(entry) + ";");
            db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            if (owningTable != null)
            {
                db.InsertOrReplace(owningTable, typeof(LR2SongDBExtended.playlist));
                ReplacePersistedCourses(db, owningTable);
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    internal bool HasPlaylistHeader(int playlistId)
    {
        using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        return db.Table<BMSTable>().Any(table => table?.playlist_id == playlistId);
    }

    internal void DeleteTables(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (tableList.Count == 0)
        {
            return;
        }
        using var db = new LR2SongDBExtended(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (BMSTable table in tableList)
            {
                if (!table.playlist_id.HasValue)
                {
                    continue;
                }
                db.Delete<LR2SongDBExtended.playlist>(table.playlist_id);
                DeletePersistedEntries(db, table.playlist_id);
                db.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName()
                    + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id)
                    + " = " + table.playlist_id + ";");
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    internal string GetPlaylistDump()
    {
        using var db = new LR2SongDBExtended(songDbPath);
        string separator = "\v" + Environment.NewLine;
        string playlistDump = string.Join(separator, from sql in db.Dump<LR2SongDBExtended.playlist>()
                                                     select sql.Replace(separator, Environment.NewLine));
        string courseDump = string.Join(separator, from sql in db.Dump<LR2SongDBExtended.playlist_course>()
                                                   select sql.Replace(separator, Environment.NewLine));
        string entryDump = string.Join(separator, from sql in db.Dump<LR2SongDBExtended.playlist_entry>()
                                                  select sql.Replace(separator, Environment.NewLine));
        return string.Join(separator, [playlistDump, courseDump, entryDump]);
    }

    internal void LoadPlaylistDump(string sql)
    {
        if (sql == null)
        {
            throw new ArgumentNullException(nameof(sql));
        }
        using var db = new LR2SongDBExtended(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            db.DropTable<LR2SongDBExtended.playlist>();
            db.DropTable<LR2SongDBExtended.playlist_course>();
            db.DropTable<LR2SongDBExtended.playlist_entry>();
            EnsureSchema(db);
            string[] source = sql.Split(["\v" + Environment.NewLine], StringSplitOptions.None);
            if (source.Count() <= 1)
            {
                throw new InvalidDataException(Resources.Error_InvalidBackupData);
            }
            foreach (string item in source.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                db.Execute(item);
            }
            List<BMSTable> restoredTables = [.. db.Table<BMSTable>()];
            if (PlaylistBmtOutputOwner.NormalizePersistedBeatorajaBmtPlaylistSettings(restoredTables) > 0)
            {
                foreach (BMSTable table in restoredTables)
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                }
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    private static bool TryParseNullableInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double invariantValue))
        {
            return invariantValue;
        }
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double currentValue))
        {
            return currentValue;
        }
        return null;
    }

    private static DateTime? ParseNullableDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            try
            {
                return new DateTime(ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime invariantValue))
        {
            return invariantValue;
        }
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime currentValue))
        {
            return currentValue;
        }
        return null;
    }

    private static bool ParseBoolean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (bool.TryParse(value, out bool boolValue))
        {
            return boolValue;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
        {
            return integerValue != 0;
        }
        return false;
    }

    private static void AttachPersistedCourses(LR2SongDBExtended db, IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = tables?.Where(table => table != null && table.playlist_id.HasValue).ToList() ?? [];
        if (tableList.Count == 0)
        {
            return;
        }
        var coursesByPlaylistId = db.Table<LR2SongDBExtended.playlist_course>()
            .ToList()
            .Where(course => course.playlist_id.HasValue)
            .GroupBy(course => course.playlist_id.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(course => course.course_order).ToList());
        foreach (BMSTable table in tableList)
        {
            if (coursesByPlaylistId.TryGetValue(table.playlist_id.Value, out List<LR2SongDBExtended.playlist_course> courses))
            {
                table.SetPersistedCourses(courses);
            }
        }
    }

    private static void ReplacePersistedCourses(LR2SongDBExtended db, BMSTable table)
    {
        if (db == null || table == null || !table.playlist_id.HasValue)
        {
            return;
        }
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName();
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id);
        db.Execute("DELETE FROM " + tableName + " WHERE " + playlistIdColumn + " = " + table.playlist_id + ";");
        int order = 0;
        foreach (LR2SongDBExtended.playlist_course course in table.Courses ?? [])
        {
            if (string.IsNullOrWhiteSpace(course?.course_json))
            {
                continue;
            }
            var row = new LR2SongDBExtended.playlist_course
            {
                playlist_id = table.playlist_id,
                course_order = order++,
                course_json = course.course_json
            };
            db.InsertOrReplace(row, typeof(LR2SongDBExtended.playlist_course));
        }
    }

    private static void DeletePersistedEntries(LR2SongDBExtended db, int? playlistId)
    {
        if (!playlistId.HasValue)
        {
            return;
        }
        db.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id)
            + " = " + playlistId + ";");
    }

    private static string BuildPlaylistEntryReplacementPredicate(BMSTableEntry entry)
    {
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string md5Column = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string sha256Column = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256);
        string folderColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder);
        string lr2BmsIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.lr2_bmsid);
        string titleColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title);

        string hashPredicate;
        if (!string.IsNullOrWhiteSpace(entry.md5))
        {
            hashPredicate = md5Column + " = " + SqlQuote(entry.md5);
            if (!string.IsNullOrWhiteSpace(entry.sha256))
            {
                hashPredicate = "(" + hashPredicate + " OR (" + md5Column + " IS NULL AND " + sha256Column + " = " + SqlQuote(entry.sha256) + "))";
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.sha256))
        {
            hashPredicate = md5Column + " IS NULL AND " + sha256Column + " = " + SqlQuote(entry.sha256);
        }
        else
        {
            hashPredicate = md5Column + " IS NULL AND " + sha256Column + " IS NULL";
        }

        return playlistIdColumn + " = " + entry.playlist_id
            + " AND " + hashPredicate
            + " AND " + folderColumn + " = " + SqlQuote(entry.folder)
            + " AND " + lr2BmsIdColumn + BuildNullableSqlEquality(entry.lr2_bmsid, blankAsNull: true)
            + " AND " + titleColumn + BuildNullableSqlEquality(entry.title);
    }

    private static string BuildNullableSqlEquality(string value, bool blankAsNull = false)
    {
        if (value == null || (blankAsNull && string.IsNullOrWhiteSpace(value)))
        {
            return " IS NULL ";
        }
        return " = " + SqlQuote(value);
    }

    private static string SqlQuote(string value = null)
    {
        return !string.IsNullOrWhiteSpace(value) ? "'" + value.Replace("'", "''") + "'" : "''";
    }

    private static bool IsCustomFolderOutputStatusSchemaCurrent(LR2SongDBExtended db)
    {
        string[] expectedColumns =
        [
            "playlist_id",
            "output_directory",
            "is_root_folder",
            "ignore_folder_output",
            "entry_type",
            "folder_sort_key",
            "folder_sort_ascending",
            "enable_unsent",
            "header_sha256",
            "data_sha256",
            "last_update_ticks",
            "physical_mtime_signature"
        ];
        try
        {
            string[] actualColumns = [.. db.Query<CustomFolderOutputStatusColumnRow>(
                "PRAGMA table_info(playlist_custom_folder_output_status);")
                .Select(row => row?.name)
                .Where(name => !string.IsNullOrWhiteSpace(name))];
            return actualColumns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SQLiteException || ex is InvalidOperationException)
        {
            return false;
        }
    }

    private static void EnsurePlaylistMetadataColumns(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
        EnsureColumn(db, tableName, "tag", "TEXT NULL");
        EnsureColumn(db, tableName, "header_sha256", "TEXT NULL");
        EnsureColumn(db, tableName, "data_sha256", "TEXT NULL");
        EnsureColumn(db, tableName, "custom_folder_output_base_name", "TEXT NULL");
        EnsureColumn(db, tableName, "bmt_sort", "INTEGER NULL");
        EnsureColumn(db, tableName, "is_bmt_output", "INTEGER NULL");
        NormalizePersistedBeatorajaBmtPlaylistSettings(db);
    }

    private static void NormalizePersistedBeatorajaBmtPlaylistSettings(LR2SongDBExtended db)
    {
        List<BMSTable> tables = [.. db.Table<BMSTable>()];
        if (PlaylistBmtOutputOwner.NormalizePersistedBeatorajaBmtPlaylistSettings(tables) == 0)
        {
            return;
        }
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (BMSTable table in tables)
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    private static void EnsureColumn(LR2SongDBExtended db, string tableName, string columnName, string columnType)
    {
        string sql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + SqlQuote(tableName) + ";");
        if (string.IsNullOrWhiteSpace(sql)
            || sql.IndexOf("\"" + columnName + "\"", StringComparison.OrdinalIgnoreCase) >= 0
            || System.Text.RegularExpressions.Regex.IsMatch(sql, "(^|[^A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(columnName) + "([^A-Za-z0-9_]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return;
        }
        db.Execute("ALTER TABLE \"" + tableName + "\" ADD COLUMN \"" + columnName + "\" " + columnType + ";");
    }

    private static void EnsurePlaylistEntrySha256Column(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string sql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + SqlQuote(tableName) + ";");
        if (string.IsNullOrWhiteSpace(sql) || sql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            db.Execute("ALTER TABLE \"" + tableName + "\" ADD COLUMN \"sha256\" TEXT NULL;");
        }
    }

    private static void EnsurePlaylistCourseIndexes(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName();
        EnsurePlaylistEntryIndex(db, tableName, "playlist_course_idx_id",
        [
            SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id)
        ]);
        long count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + SqlQuote("playlist_course_idx_uniq") + ";");
        if (count == 0)
        {
            db.CreateIndex("playlist_course_idx_uniq", tableName,
            [
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.course_order)
            ], unique: true);
        }
    }

    private static void RebuildPlaylistEntryIndexes(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string uniqueIndexSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
        if (string.IsNullOrWhiteSpace(uniqueIndexSql) || uniqueIndexSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (!string.IsNullOrWhiteSpace(uniqueIndexSql))
            {
                db.Execute("DROP INDEX IF EXISTS 'playlist_entry_idx_uniq';");
            }
            db.CreateIndex("playlist_entry_idx_uniq", tableName,
            [
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.lr2_bmsid),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
            ], unique: true);
        }
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_id",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_folder",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_title",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_md5",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_sha256",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_level",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.level),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_adddate",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.adddate),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
    }

    private static void EnsurePlaylistEntryIndex(LR2SongDBExtended db, string tableName, string indexName, string[] columnNames)
    {
        long count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + SqlQuote(indexName) + ";");
        if (count == 0)
        {
            db.CreateIndex(indexName, tableName, columnNames);
        }
    }

    private sealed class CustomFolderOutputStatusColumnRow
    {
        public string name { get; set; }
    }
}
