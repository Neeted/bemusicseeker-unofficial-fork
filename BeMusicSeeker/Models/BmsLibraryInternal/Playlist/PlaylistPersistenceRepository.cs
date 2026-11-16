using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private readonly ApplicationDatabaseScope databaseScope;

    private readonly Action<LR2SongDBExtended> initializeEntryWriteConnection;

    /// <summary>接続所有者、保存先とentry書込み時の初期化を受け取り、実SQLの観測も各保存の接続寿命内に限定します。</summary>
    internal PlaylistPersistenceRepository(ApplicationDatabaseScope databaseScope, string songDbPath, Action<LR2SongDBExtended> initializeEntryWriteConnection = null)
    {
        this.databaseScope = databaseScope ?? throw new ArgumentNullException(nameof(databaseScope));
        this.songDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
        this.initializeEntryWriteConnection = initializeEntryWriteConnection;
    }

    /// <summary>
    /// 指定された song.db の playlist schema を transaction 単位で現在の形へ揃えます。
    /// </summary>
    internal static void EnsureSchema(ApplicationDatabaseScope databaseScope, string songDbPath)
    {
        ArgumentNullException.ThrowIfNull(databaseScope);
        if (songDbPath == null)
        {
            throw new ArgumentNullException(nameof(songDbPath));
        }
        if (!File.Exists(songDbPath))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, songDbPath), nameof(songDbPath));
        }
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
        return Lr2FolderExistingRowLookup.QueryExactPathsForCustomFolderLayout(db, pathList).ToList();
    }

    internal List<BMSTable> LoadPlaylistHeaders()
    {
        List<BMSTable> tables;
        using (LR2SongDBExtended db = databaseScope.OpenSongReadOnly(songDbPath))
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
        using LR2SongDBExtended db = databaseScope.OpenSongReadOnly(songDbPath);
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

    /// <summary>起動時の所属行を読取り専用で生成し、SQLiteの数値型と旧TEXT ticksを保持します。不適合IDは除外し、その他の不適合値は既定値へ渡します。</summary>
    internal PlaylistEntriesHydrationLoadResult LoadStartupPlaylistEntries()
    {
        var result = new PlaylistEntriesHydrationLoadResult();
        using LR2SongDBExtended db = databaseScope.OpenSongReadOnly(songDbPath);
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
        command.ForEachRawRow(values =>
        {
            if (values.Count < 16 || values.GetInt32(0) is not int playlistId)
            {
                return;
            }
            result.Entries.Add(BMSTableEntry.CreateHydratedPlaylistEntry(
                playlistId,
                values.GetString(1),
                values.GetString(2),
                values.GetDouble(3),
                values.GetString(4),
                values.GetString(5),
                values.GetString(6),
                values.GetString(7),
                values.GetString(8),
                values.GetString(9),
                values.GetString(10),
                values.GetString(11),
                values.GetDateTime(12),
                values.GetString(13),
                values.GetString(14),
                values.GetBoolean(15)));
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath, initializeEntryWriteConnection);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath, initializeEntryWriteConnection);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
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
        var args = new List<object>();
        string sql = BuildEquivalentEntryLookupSql(entry, args);
        foreach (PlaylistEntryRowLocator row in db.Query<PlaylistEntryRowLocator>(sql + ";", [.. args]))
        {
            db.Execute("DELETE FROM playlist_entry WHERE rowid = ?;", row.RowId);
        }
    }

    private sealed class PlaylistEntryRowLocator
    {
        public PlaylistEntryRowLocator() { }
        public long RowId { get; set; }
    }

    /// <summary>
    /// live の既存正規化に対応する NULL/空/空白だけの hash、NULL/空の folder・title を同値候補に含め、削除状態を区別する rowid の取得 SQL を返します。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 非blank の hash は前後を削らず ASCII の大小文字だけを同一視し、従来受理する最終 LF 付き hash も別キーとして保持します。
    /// folder・title は NULL と空だけを同一視して他は完全一致、lr2_bmsid は NULL と空も区別した完全一致、
    /// is_removed は NULL と 0 を同じ生存状態とします。
    /// </para>
    /// <para>
    /// 候補索引 <c>(playlist_id, md5 COLLATE NOCASE, sha256 COLLATE NOCASE, folder, lr2_bmsid, title, is_removed)</c> で同表の背景件数に比例せず絞れるよう、
    /// blank の hash を索引で一点または狭い範囲に探索できる互いに素な形へ分け、形ごとの SELECT を UNION ALL で連結します。
    /// 一つの WHERE の OR にすると、小さな表ではプランナーが表全体の走査を選ぶため、各 SELECT を単純な索引探索に保ちます。
    /// 非blank の hash の一致で候補が一点に絞れる形（非blank の MD5、または一点の形の MD5 と非blank の SHA-256）は、後続列を残余条件だけで照合します。
    /// 両 hash が blank で、どちらも一点探索の形（NULL または空）のときだけ、folder・lr2_bmsid・title・is_removed も <see cref="EnumerateKeySeekForms"/> の形で探索します。
    /// hash が空白文字で始まる範囲の形では後続列を探索に使えないため、残余条件だけで照合します。
    /// 各形は互いに素なので、同じ行が二度返ることはありません。条件の値は <paramref name="args"/> へ SQL 内の出現順に追加します。
    /// </para>
    /// </remarks>
    private static string BuildEquivalentEntryLookupSql(BMSTableEntry entry, List<object> args)
    {
        var selects = new List<string>();
        void AddSelect(Func<string> hashCondition, bool seekKeyColumns)
        {
            if (!seekKeyColumns)
            {
                args.Add(entry.playlist_id.Value);
                string hash = hashCondition();
                selects.Add(CandidateSelectPrefix + hash + ExactKeyConditions(entry, args));
                return;
            }
            // 両 hash 列が blank の一点探索の形のときだけ、後続の所属キー列も索引で探索します。
            // NULL と空を同一視する列は互いに素な一点探索の形へ分け、元の残余条件も残して意味を変えません。
            foreach (HashKeyForm[] keyForms in EnumerateKeySeekForms(entry))
            {
                args.Add(entry.playlist_id.Value);
                string hash = hashCondition();
                string seek = string.Concat(keyForms.Select(form => " AND " + form.Append(args)));
                selects.Add(CandidateSelectPrefix + hash + seek + ExactKeyConditions(entry, args));
            }
        }

        if (!IsBlankHash(entry.md5))
        {
            // 非blank の MD5 の一致だけで同表の候補は一点に絞れるため、SHA-256 は残余条件で照合します。
            AddSelect(() => HashEquality("md5", entry.md5, args) + " AND " + HashResidual("sha256", entry.sha256, args), seekKeyColumns: false);
            return string.Join(" UNION ALL ", selects);
        }
        foreach (HashKeyForm md5Form in EnumerateBlankHashForms("md5"))
        {
            if (md5Form.IsRange)
            {
                // 範囲探索の後ろの索引列は探索に使えないため、SHA-256 は残余条件で照合します。
                AddSelect(() => md5Form.Append(args) + " AND " + HashResidual("sha256", entry.sha256, args), seekKeyColumns: false);
            }
            else if (!IsBlankHash(entry.sha256))
            {
                // MD5 が一点の形なら非blank の SHA-256 の一致で同表の候補は一点に絞れるため、後続の所属キー列は残余条件で照合します。
                AddSelect(() => md5Form.Append(args) + " AND " + HashEquality("sha256", entry.sha256, args), seekKeyColumns: false);
            }
            else
            {
                // MD5 が blank の行は SHA-256 だけの表で多数になり得るため、SHA-256 の blank も索引で探索します。
                foreach (HashKeyForm shaForm in EnumerateBlankHashForms("sha256"))
                {
                    AddSelect(() => md5Form.Append(args) + " AND " + shaForm.Append(args), seekKeyColumns: !shaForm.IsRange);
                }
            }
        }
        return string.Join(" UNION ALL ", selects);
    }

    private const string CandidateSelectPrefix = "SELECT rowid AS RowId FROM playlist_entry WHERE playlist_id = ? AND ";

    /// <summary>
    /// folder・lr2_bmsid・title・is_removed を候補索引の順に一点探索する条件の組合せを、互いに素に列挙します。
    /// 非空の folder・title は <c>= ?</c>、空は <c>IS NULL</c> と <c>= ''</c>、生存は <c>IS NULL</c> と <c>= 0</c> に分けます。
    /// </summary>
    /// <remarks>
    /// 両 hash が blank の項目では hash 列の探索だけでは同表の hash なしの行全体に広がるため、後続列でも絞ります。
    /// 各条件は <see cref="ExactKeyConditions"/> の残余条件を満たす行だけを選ぶ冗長な条件で、組合せの和は残余条件と一致します。
    /// </remarks>
    private static IEnumerable<HashKeyForm[]> EnumerateKeySeekForms(BMSTableEntry entry)
    {
        HashKeyForm[] folders = EmptyEquivalentSeekForms("folder", entry.folder);
        HashKeyForm bmsid = entry.lr2_bmsid == null
            ? new HashKeyForm("lr2_bmsid IS NULL", [], IsRange: false)
            : new HashKeyForm("lr2_bmsid = ?", [entry.lr2_bmsid], IsRange: false);
        HashKeyForm[] titles = EmptyEquivalentSeekForms("title", entry.title);
        HashKeyForm[] states = entry.is_removed
            ? [new HashKeyForm("is_removed = 1", [], IsRange: false)]
            : [new HashKeyForm("is_removed IS NULL", [], IsRange: false), new HashKeyForm("is_removed = 0", [], IsRange: false)];
        foreach (HashKeyForm folder in folders)
        {
            foreach (HashKeyForm title in titles)
            {
                foreach (HashKeyForm state in states)
                {
                    yield return [folder, bmsid, title, state];
                }
            }
        }
    }

    private static HashKeyForm[] EmptyEquivalentSeekForms(string column, string value)
        => string.IsNullOrEmpty(value)
            ? [new HashKeyForm(column + " IS NULL", [], IsRange: false), new HashKeyForm(column + " = ''", [], IsRange: false)]
            : [new HashKeyForm(column + " = ?", [value], IsRange: false)];

    /// <summary>hash 以外の所属キーと生存状態の残余条件を返し、値を <paramref name="args"/> へ出現順に追加します。</summary>
    private static string ExactKeyConditions(BMSTableEntry entry, List<object> args)
    {
        args.Add(entry.folder ?? string.Empty);
        string sql = " AND ifnull(folder, '') = ?";
        if (entry.lr2_bmsid == null)
        {
            sql += " AND lr2_bmsid IS NULL";
        }
        else
        {
            args.Add(entry.lr2_bmsid);
            sql += " AND lr2_bmsid = ?";
        }
        args.Add(entry.title ?? string.Empty);
        sql += " AND ifnull(title, '') = ?";
        return sql + " AND ifnull(is_removed, 0) = " + (entry.is_removed ? "1" : "0");
    }

    /// <summary>.NET の <see cref="string.IsNullOrWhiteSpace(string)"/> が hash setter で空とみなす値かどうかを返します。</summary>
    private static bool IsBlankHash(string value) => string.IsNullOrWhiteSpace(value);

    private static string HashEquality(string column, string value, List<object> args)
    {
        args.Add(value);
        return column + " COLLATE NOCASE = ?";
    }

    /// <summary>索引を使わない残余条件として、hash の同値を判定します。blank は NULL・空・空白だけの値をまとめて扱います。</summary>
    private static string HashResidual(string column, string value, List<object> args)
    {
        if (!IsBlankHash(value))
        {
            return HashEquality(column, value, args);
        }
        args.Add(BlankHashCharacters);
        return "(" + column + " IS NULL OR trim(" + column + ", ?) = '')";
    }

    /// <summary>
    /// blank の hash を、NULL、空、空白文字で始まる値の範囲（残余の <c>trim</c> で空白だけと確認）へ排他的に分けます。
    /// 各形は候補索引の hash 列で一点または範囲探索できます。
    /// </summary>
    private static IEnumerable<HashKeyForm> EnumerateBlankHashForms(string column)
    {
        yield return new HashKeyForm(column + " IS NULL", [], IsRange: false);
        yield return new HashKeyForm(column + " COLLATE NOCASE = ''", [], IsRange: false);
        foreach ((string lower, string upper) in BlankHashLeadingRanges)
        {
            yield return new HashKeyForm(
                column + " COLLATE NOCASE >= ? AND " + column + " COLLATE NOCASE < ? AND trim(" + column + ", ?) = ''",
                [lower, upper, BlankHashCharacters],
                IsRange: true);
        }
    }

    /// <summary>候補 SELECT の一つの条件式（hash または所属キーの探索形）と、その式が SQL 内で参照する値です。</summary>
    private readonly record struct HashKeyForm(string Sql, object[] Args, bool IsRange)
    {
        /// <summary>値を出現順に追加し、条件式を返します。</summary>
        internal string Append(List<object> args)
        {
            args.AddRange(Args);
            return Sql;
        }
    }

    /// <summary>
    /// .NET の <see cref="char.IsWhiteSpace(char)"/> が空白とする25文字です。
    /// SQLite 3.6.7 にある <c>trim(X, Y)</c> の Y へ束縛し、3.7.16 で追加された <c>char()</c> を問い合わせに使いません。
    /// </summary>
    private const string BlankHashCharacters =
        "\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";

    /// <summary>
    /// <see cref="BlankHashCharacters"/> を連続する文字コードごとにまとめた半開区間です。
    /// 空白だけの非空の値は、先頭文字によっていずれか一つの区間に入ります。
    /// NOCASE は ASCII の英字だけを畳むため、英字を含まない区間の境界は UTF-8 の符号位置順と一致します。
    /// </summary>
    private static readonly (string Lower, string Upper)[] BlankHashLeadingRanges =
    [
        ("\t", "\u000e"),
        (" ", "!"),
        ("\u0085", "\u0086"),
        ("\u00a0", "\u00a1"),
        ("\u1680", "\u1681"),
        ("\u2000", "\u200b"),
        ("\u2028", "\u202a"),
        ("\u202f", "\u2030"),
        ("\u205f", "\u2060"),
        ("\u3000", "\u3001")
    ];

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
        // UNIQUE と既存の raw 値用索引は変えず、局所保存の正規化同値候補を hash で探索します。
        // LR2 の SQLite 3.6.7 が解析できるよう列名と COLLATE だけで定義し、blank・NULL の正規化は問い合わせ側で行います。
        // 残余条件の列も含めて候補の照合を索引だけで完結させ、MD5 IS NULL などで他の被覆索引が選ばれないようにします。
        // 定義が異なる既存の同名索引（開発版の式索引を含む）は下の不一致判定で作り直し、一致すれば作り直しません。
        string candidateIndexSql = "CREATE INDEX playlist_entry_idx_candidate ON " + tableName
            + " (playlist_id, md5 COLLATE NOCASE, sha256 COLLATE NOCASE, folder, lr2_bmsid, title, is_removed)";
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
