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

internal sealed class CustomFolderOutputStatusRow
{
    public int PlaylistId { get; set; }

    public string OutputDirectory { get; set; }

    public int IsRootFolder { get; set; }

    public int IgnoreFolderOutput { get; set; }

    public int EntryType { get; set; }

    public int FolderSortKey { get; set; }

    public int FolderSortAscending { get; set; }

    public int EnableUnsent { get; set; }

    public string HeaderSha256 { get; set; }

    public string DataSha256 { get; set; }

    public long LastUpdateTicks { get; set; }

    public string PhysicalMtimeSignature { get; set; }
}

/// <summary>
/// プレイリスト永続化の DB 境界です。
/// schema、hydration、playlist/course/entry の置換、削除、dump/restore を
/// ここで transaction とともに扱い、<see cref="BMSPlaylist"/> へ DB 実装を漏らしません。
/// </summary>
internal sealed class PlaylistPersistenceRepository
{
    private readonly string songDbPath;

    private readonly Func<string, LR2SongDBExtended> openEntryWriteConnection;

    /// <summary>保存先と entry 書込み接続を受け取り、実 SQL の観測も各保存の接続寿命内に限定します。</summary>
    internal PlaylistPersistenceRepository(string songDbPath, Func<string, LR2SongDBExtended> openEntryWriteConnection = null)
    {
        this.songDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
        this.openEntryWriteConnection = openEntryWriteConnection ?? (path => new LR2SongDBExtended(path));
    }

    /// <summary>
    /// 指定された song.db の playlist schema を transaction 単位で現在の形へ揃えます。
    /// </summary>
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
        string savepoint = db.SaveTransactionPoint();
        try
        {
            EnsureSchemaOnConnection(db);
            db.Commit();
        }
        catch
        {
            try
            {
                db.RollbackTo(savepoint);
            }
            catch
            {
                // Keep the schema failure authoritative when rollback itself fails.
            }
            throw;
        }
    }

    /// <summary>
    /// 借用済み connection 上で playlist schema を準備します。
    /// transaction の開始、commit、rollback は connection owner が行います。
    /// </summary>
    internal static void EnsureSchemaOnConnection(LR2SongDBExtended db)
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

    internal Dictionary<int, CustomFolderOutputStatusRow> ReadCustomFolderOutputStatusRows()
    {
        using var db = new LR2SongDBExtended(songDbPath);
        EnsureCustomFolderOutputStatusTable(db);
        return db.Query<CustomFolderOutputStatusRow>(
                "SELECT "
                + "playlist_id AS PlaylistId,"
                + "output_directory AS OutputDirectory,"
                + "is_root_folder AS IsRootFolder,"
                + "ignore_folder_output AS IgnoreFolderOutput,"
                + "entry_type AS EntryType,"
                + "folder_sort_key AS FolderSortKey,"
                + "folder_sort_ascending AS FolderSortAscending,"
                + "enable_unsent AS EnableUnsent,"
                + "header_sha256 AS HeaderSha256,"
                + "data_sha256 AS DataSha256,"
                + "last_update_ticks AS LastUpdateTicks,"
                + "physical_mtime_signature AS PhysicalMtimeSignature "
                + "FROM playlist_custom_folder_output_status;")
            .Where(row => row != null)
            .GroupBy(row => row.PlaylistId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    internal void PersistCustomFolderOutputStatusRows(IEnumerable<CustomFolderOutputStatusRow> rows)
    {
        List<CustomFolderOutputStatusRow> rowList = [.. (rows ?? []).Where(row => row?.PlaylistId > 0)];
        if (rowList.Count == 0)
        {
            return;
        }

        using var db = new LR2SongDBExtended(songDbPath);
        EnsureCustomFolderOutputStatusTable(db);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (CustomFolderOutputStatusRow row in rowList)
            {
                db.Execute(
                    "INSERT OR REPLACE INTO playlist_custom_folder_output_status ("
                    + "playlist_id, output_directory, is_root_folder, ignore_folder_output, entry_type, folder_sort_key, folder_sort_ascending, enable_unsent, header_sha256, data_sha256, last_update_ticks, physical_mtime_signature"
                    + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    row.PlaylistId,
                    row.OutputDirectory ?? string.Empty,
                    row.IsRootFolder,
                    row.IgnoreFolderOutput,
                    row.EntryType,
                    row.FolderSortKey,
                    row.FolderSortAscending,
                    row.EnableUnsent,
                    row.HeaderSha256 ?? string.Empty,
                    row.DataSha256 ?? string.Empty,
                    row.LastUpdateTicks,
                    row.PhysicalMtimeSignature ?? string.Empty);
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    internal void DeleteCustomFolderOutputStatus(int? playlistId)
    {
        if (!playlistId.HasValue)
        {
            return;
        }

        using var db = new LR2SongDBExtended(songDbPath);
        EnsureCustomFolderOutputStatusTable(db);
        db.Execute(
            "DELETE FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
            playlistId.Value);
    }

    internal IReadOnlyList<LR2SongDB.folder> QueryCustomFolderOutputRows(
        IEnumerable<string> exactPaths,
        IEnumerable<string> scopePaths,
        bool layoutOnly)
    {
        List<string> exactPathList = [.. (exactPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> scopePathList = [.. (scopePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (exactPathList.Count == 0 && scopePathList.Count == 0)
        {
            return [];
        }

        using var db = new LR2SongDBExtended(songDbPath);
        IEnumerable<LR2SongDB.folder> exactRows = layoutOnly
            ? Lr2FolderExistingRowLookup.QueryExactPathsForCustomFolderLayout(db, exactPathList)
            : Lr2FolderExistingRowLookup.QueryExactPaths(db, exactPathList);
        IEnumerable<LR2SongDB.folder> scopeRows = layoutOnly
            ? Lr2FolderExistingRowLookup.QueryCustomFolderLayoutPathPrefixScopes(db, scopePathList)
            : Lr2FolderExistingRowLookup.QueryPathPrefixScopes(db, scopePathList);
        return exactRows.Concat(scopeRows).ToList();
    }

    internal IReadOnlyList<LR2SongDB.folder> QueryCustomFolderLayoutRowsByExactPath(IEnumerable<string> paths)
    {
        List<string> pathList = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (pathList.Count == 0)
        {
            return [];
        }

        using var db = new LR2SongDBExtended(songDbPath);
        return Lr2FolderExistingRowLookup.QueryExactPathsForCustomFolderLayout(db, pathList).ToList();
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
                sql += " AND ifnull(" + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.is_removed) + ", 0) = 0";
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

    internal void ReplaceEntry(BMSTableEntry entry, BMSTable owningTable, BMSTableEntry originalEntry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        using LR2SongDBExtended db = openEntryWriteConnection(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            DeleteEquivalentEntries(db, originalEntry);
            DeleteEquivalentEntries(db, entry);
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

    /// <summary>少数追加削除だけを、複数表で一つの transaction として保存します。対象外の保存行は書き直しません。</summary>
    internal void CommitEntryMutations(IReadOnlyList<BMSTable.EntryMutation> mutations)
    {
        using LR2SongDBExtended db = openEntryWriteConnection(songDbPath);
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (BMSTable.EntryMutation mutation in mutations)
            {
                db.InsertOrReplace(mutation.Header, typeof(LR2SongDBExtended.playlist));
                foreach (PlaylistEntryChange change in mutation.Changes)
                {
                    if (change.Before != null)
                    {
                        DeleteEquivalentEntries(db, change.Entry);
                    }
                }
                foreach ((BMSTableEntry _, BMSTableEntry candidate) in mutation.Additions)
                {
                    // 新規IDもDB候補内だけで解決し、commit前の正本tableへ採番を漏らしません。
                    candidate.parent = mutation.Header;
                    candidate.playlist_id = mutation.Header.playlist_id;
                    DeleteEquivalentEntries(db, candidate);
                    db.InsertOrReplace(candidate, typeof(LR2SongDBExtended.playlist_entry));
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

    /// <summary>保存済み三表の値とSQLite型を保持するSQLバックアップを生成します。</summary>
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

    /// <summary>三表をSQLから復元し、旧BMT設定の二列だけを補正して出力状態を無効化します。途中失敗では旧三表と出力状態を保持します。</summary>
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
            EnsureSchemaOnConnection(db);
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
                    // モデル既定値や派生プロパティで復元済みの他列を再保存すると、NULLなどの原値が失われる。
                    db.Execute("UPDATE playlist SET bmt_sort=?, is_bmt_output=? WHERE playlist_id=?;",
                        table.bmt_sort, table.is_bmt_output, table.playlist_id);
                }
            }
            // 同じID・ハッシュ・更新日時のバックアップでも、復元前の出力を現行とは扱いません。
            db.Execute("DELETE FROM playlist_custom_folder_output_status;");
            db.Commit();
        }
        catch
        {
            try
            {
                db.RollbackTo(savepoint);
            }
            catch
            {
                // Keep the restore failure authoritative when rollback itself fails.
            }
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

    /// <summary>保存候補の旧新キーに影響する DB 行だけを取得し、transaction 内の短命 rowid で削除します。</summary>
    private static void DeleteEquivalentEntries(LR2SongDBExtended db, BMSTableEntry entry)
    {
        if (!entry.playlist_id.HasValue) { return; }
        foreach (PlaylistEntryRowLocator row in db.Query<PlaylistEntryRowLocator>(
            "SELECT rowid AS RowId FROM playlist_entry WHERE " + BuildExactPlaylistEntryPredicate(entry) + ";"))
        {
            db.Execute("DELETE FROM playlist_entry WHERE rowid = ?;", row.RowId);
        }
    }

    private sealed class PlaylistEntryRowLocator
    {
        public PlaylistEntryRowLocator() { }
        public long RowId { get; set; }
    }

    /// <summary>live の既存正規化に対応する NULL/空 hash・folder・title を同値候補に含め、削除状態を区別します。</summary>
    private static string BuildExactPlaylistEntryPredicate(BMSTableEntry entry)
        => "playlist_id = " + entry.playlist_id
            + " AND " + HashEquality("md5", entry.md5)
            + " AND " + HashEquality("sha256", entry.sha256)
            + " AND ifnull(folder, '')" + ExactEquality(entry.folder ?? string.Empty)
            + " AND lr2_bmsid" + ExactEquality(entry.lr2_bmsid)
            + " AND ifnull(title, '')" + ExactEquality(entry.title ?? string.Empty)
            + " AND ifnull(is_removed, 0) = " + (entry.is_removed ? "1" : "0");

    private static string ExactEquality(string value)
        => value == null ? " IS NULL" : " = '" + value.Replace("'", "''") + "'";

    private static string HashEquality(string column, string value)
        => HashComparisonExpression(column) + ExactEquality(value ?? string.Empty);

    /// <summary>
    /// hash setter の blank→null と ASCII hash の小文字化に対応する索引式です。
    /// IsNullOrWhiteSpace の文字集合だけを blank 判定へ使い、非blank の原値は削りません。
    /// 正規表現の $ が従来受理する最終 LF 付き hash も、LF を含む別の保存キーとして保持します。
    /// </summary>
    private static string HashComparisonExpression(string column)
        => "(CASE WHEN " + column + " IS NULL OR trim(" + column
            + ", char(9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)) = '' THEN '' ELSE "
            + column + " END) COLLATE NOCASE";

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
        foreach (BMSTable table in tables)
        {
            db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
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
        // UNIQUE と既存の raw 値用索引は変えず、局所保存の正規化同値候補だけを全キーで探索します。
        string candidateIndexSql = "CREATE INDEX playlist_entry_idx_candidate ON " + tableName
            + " (playlist_id, ifnull(is_removed, 0), " + HashComparisonExpression("md5") + ", "
            + HashComparisonExpression("sha256") + ", ifnull(folder, ''), lr2_bmsid, ifnull(title, ''))";
        string currentCandidateIndexSql = db.ExecuteScalar<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_candidate';");
        if (!string.Equals(currentCandidateIndexSql?.TrimEnd(';'), candidateIndexSql, StringComparison.Ordinal))
        {
            db.Execute("DROP INDEX IF EXISTS playlist_entry_idx_candidate;");
            db.Execute(candidateIndexSql + ";");
        }
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
