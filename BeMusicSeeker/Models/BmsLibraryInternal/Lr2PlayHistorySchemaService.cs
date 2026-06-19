using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum Lr2PlayHistorySchemaStatus
{
    Installed,
    NotInstalled,
    Repairable,
    ManualRepairRequired,
    Unreadable,
    SkippedProfile
}

/// <summary>
/// LR2 play history schema のアンインストールで、記録停止だけにするか履歴データも削除するかを表します。
/// </summary>
internal enum Lr2PlayHistorySchemaUninstallMode
{
    /// <summary>
    /// score/player table に設置した trigger だけを削除し、既存の履歴 table は保持します。
    /// </summary>
    TriggersOnly,

    /// <summary>
    /// trigger と BeMusicSeeker 管理の play history table/index を削除します。
    /// </summary>
    TablesAndTriggers
}

internal sealed class Lr2PlayHistorySchemaCheckResult
{
    public Lr2PlayHistorySchemaStatus Status { get; set; }

    public string ScoreDbPath { get; set; }

    public string Message { get; set; }

    public List<string> MissingTables { get; } = [];

    public List<string> MissingBaseTables { get; } = [];

    public List<string> MissingIndexes { get; } = [];

    public List<string> MismatchedIndexes { get; } = [];

    public List<string> MissingTriggers { get; } = [];

    public List<string> MismatchedTriggers { get; } = [];

    public List<string> MissingColumns { get; } = [];

    public List<string> MissingBaseColumns { get; } = [];

    public List<string> IncompatibleColumns { get; } = [];

    public List<string> IncompatibleBaseColumns { get; } = [];

    public List<string> ObjectNameCollisions { get; } = [];

    public bool CanInstall => Status == Lr2PlayHistorySchemaStatus.NotInstalled;

    public bool CanRepair => Status == Lr2PlayHistorySchemaStatus.Repairable;
}

internal sealed class Lr2PlayHistorySchemaService
{
    internal const string LastPlayTableName = "bms_lr2_last_play";
    internal const string PlayHistoryTableName = "bms_lr2_play_history";
    internal const string PlayPendingTableName = "bms_lr2_play_pending";

    internal const string HashTimeIndexName = "idx_bms_lr2_play_history_hash_time";
    internal const string TimeIndexName = "idx_bms_lr2_play_history_time";

    internal const string ScoreInsertTriggerName = "bms_lr2_score_history_after_insert";
    internal const string ScoreUpdateTriggerName = "bms_lr2_score_history_after_update_playcount";
    internal const string PlayerUpdateTriggerName = "bms_lr2_player_history_after_update";
    internal const string PlayerCleanupTriggerName = "bms_lr2_player_history_cleanup_stale_pending";

    private static readonly string[] ExpectedTableNames =
    [
        LastPlayTableName,
        PlayHistoryTableName,
        PlayPendingTableName
    ];

    private static readonly string[] ExpectedIndexNames =
    [
        HashTimeIndexName,
        TimeIndexName
    ];

    private static readonly string[] ExpectedTriggerNames =
    [
        ScoreInsertTriggerName,
        ScoreUpdateTriggerName,
        PlayerUpdateTriggerName,
        PlayerCleanupTriggerName
    ];

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, RequiredColumn[]> RequiredBaseTables =
        new Dictionary<string, RequiredColumn[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["score"] =
            [
                new("hash", "TEXT", notNull: false, primaryKey: true),
                new("clear", "INTEGER", notNull: false),
                new("perfect", "INTEGER", notNull: false),
                new("great", "INTEGER", notNull: false),
                new("good", "INTEGER", notNull: false),
                new("bad", "INTEGER", notNull: false),
                new("poor", "INTEGER", notNull: false),
                new("totalnotes", "INTEGER", notNull: false),
                new("maxcombo", "INTEGER", notNull: false),
                new("minbp", "INTEGER", notNull: false),
                new("playcount", "INTEGER", notNull: false),
                new("clearcount", "INTEGER", notNull: false),
                new("failcount", "INTEGER", notNull: false),
                new("clear_db", "INTEGER", notNull: false),
                new("op_history", "INTEGER", notNull: false),
                new("scorehash", "TEXT", notNull: false),
                new("clear_sd", "INTEGER", notNull: false),
                new("clear_ex", "INTEGER", notNull: false),
                new("op_best", "INTEGER", notNull: false),
                new("rseed", "INTEGER", notNull: false),
                new("complete", "INTEGER", notNull: false)
            ],
            ["player"] =
            [
                new("id", "TEXT", notNull: false, primaryKey: true),
                new("playcount", "INTEGER", notNull: false),
                new("clear", "INTEGER", notNull: false),
                new("fail", "INTEGER", notNull: false),
                new("perfect", "INTEGER", notNull: false),
                new("great", "INTEGER", notNull: false),
                new("good", "INTEGER", notNull: false),
                new("bad", "INTEGER", notNull: false),
                new("poor", "INTEGER", notNull: false),
                new("playtime", "INTEGER", notNull: false),
                new("maxcombo", "INTEGER", notNull: false)
            ]
        };

    private static readonly IReadOnlyDictionary<string, RequiredColumn[]> RequiredTables =
        new Dictionary<string, RequiredColumn[]>(StringComparer.OrdinalIgnoreCase)
        {
            [LastPlayTableName] =
            [
                new("hash", "TEXT", notNull: false, primaryKey: true),
                new("last_play_at", "INTEGER", notNull: true)
            ],
            [PlayHistoryTableName] =
            [
                new("history_id", "INTEGER", notNull: false, primaryKey: true),
                new("hash", "TEXT", notNull: true),
                new("played_at", "INTEGER", notNull: true),
                new("finalized", "INTEGER", notNull: true, defaultValue: "0"),
                new("score_write_type", "TEXT", notNull: true),
                new("old_playcount", "INTEGER", notNull: false),
                new("new_playcount", "INTEGER", notNull: true),
                new("playcount_delta", "INTEGER", notNull: true),
                new("old_clearcount", "INTEGER", notNull: false),
                new("new_clearcount", "INTEGER", notNull: false),
                new("clearcount_delta", "INTEGER", notNull: false),
                new("old_failcount", "INTEGER", notNull: false),
                new("new_failcount", "INTEGER", notNull: false),
                new("failcount_delta", "INTEGER", notNull: false),
                new("old_clear", "INTEGER", notNull: false),
                new("new_clear", "INTEGER", notNull: false),
                new("old_clear_db", "INTEGER", notNull: false),
                new("new_clear_db", "INTEGER", notNull: false),
                new("old_clear_sd", "INTEGER", notNull: false),
                new("new_clear_sd", "INTEGER", notNull: false),
                new("old_clear_ex", "INTEGER", notNull: false),
                new("new_clear_ex", "INTEGER", notNull: false),
                new("old_minbp", "INTEGER", notNull: false),
                new("new_minbp", "INTEGER", notNull: false),
                new("old_exscore", "INTEGER", notNull: false),
                new("new_exscore", "INTEGER", notNull: false),
                new("old_maxcombo", "INTEGER", notNull: false),
                new("new_maxcombo", "INTEGER", notNull: false),
                new("old_totalnotes", "INTEGER", notNull: false),
                new("new_totalnotes", "INTEGER", notNull: false),
                new("old_complete", "INTEGER", notNull: false),
                new("new_complete", "INTEGER", notNull: false),
                new("old_op_best", "INTEGER", notNull: false),
                new("new_op_best", "INTEGER", notNull: false),
                new("old_op_history", "INTEGER", notNull: false),
                new("new_op_history", "INTEGER", notNull: false),
                new("old_rseed", "INTEGER", notNull: false),
                new("new_rseed", "INTEGER", notNull: false),
                new("old_scorehash", "TEXT", notNull: false),
                new("new_scorehash", "TEXT", notNull: false),
                new("old_player_playcount", "INTEGER", notNull: false),
                new("new_player_playcount", "INTEGER", notNull: false),
                new("player_playcount_delta", "INTEGER", notNull: false),
                new("old_playtime_total", "INTEGER", notNull: false),
                new("new_playtime_total", "INTEGER", notNull: false),
                new("playtime_delta", "INTEGER", notNull: false),
                new("old_judge_total", "INTEGER", notNull: false),
                new("new_judge_total", "INTEGER", notNull: false),
                new("judge_delta", "INTEGER", notNull: false),
                new("old_player_perfect", "INTEGER", notNull: false),
                new("new_player_perfect", "INTEGER", notNull: false),
                new("perfect_delta", "INTEGER", notNull: false),
                new("old_player_great", "INTEGER", notNull: false),
                new("new_player_great", "INTEGER", notNull: false),
                new("great_delta", "INTEGER", notNull: false),
                new("old_player_good", "INTEGER", notNull: false),
                new("new_player_good", "INTEGER", notNull: false),
                new("good_delta", "INTEGER", notNull: false),
                new("old_player_bad", "INTEGER", notNull: false),
                new("new_player_bad", "INTEGER", notNull: false),
                new("bad_delta", "INTEGER", notNull: false),
                new("old_player_poor", "INTEGER", notNull: false),
                new("new_player_poor", "INTEGER", notNull: false),
                new("poor_delta", "INTEGER", notNull: false),
                new("old_player_maxcombo", "INTEGER", notNull: false),
                new("new_player_maxcombo", "INTEGER", notNull: false)
            ],
            [PlayPendingTableName] =
            [
                new("id", "INTEGER", notNull: false, primaryKey: true),
                new("history_id", "INTEGER", notNull: true),
                new("hash", "TEXT", notNull: true),
                new("created_at", "INTEGER", notNull: true)
            ]
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedIndexSqlByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HashTimeIndexName] = CreatePlayHistoryHashTimeIndexSql,
            [TimeIndexName] = CreatePlayHistoryTimeIndexSql
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedTriggerSqlByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ScoreInsertTriggerName] = CreateScoreInsertTriggerSql,
            [ScoreUpdateTriggerName] = CreateScoreUpdateTriggerSql,
            [PlayerUpdateTriggerName] = CreatePlayerUpdateTriggerSql,
            [PlayerCleanupTriggerName] = CreatePlayerCleanupTriggerSql
        };

    internal static IReadOnlyList<string> InstallSqlStatements { get; } =
    [
        CreateLastPlayTableSql,
        CreatePlayHistoryTableSql,
        CreatePlayHistoryHashTimeIndexSql,
        CreatePlayHistoryTimeIndexSql,
        CreatePlayPendingTableSql,
        CreateTriggerIfNotExists(CreateScoreInsertTriggerSql),
        CreateTriggerIfNotExists(CreateScoreUpdateTriggerSql),
        CreateTriggerIfNotExists(CreatePlayerUpdateTriggerSql),
        CreateTriggerIfNotExists(CreatePlayerCleanupTriggerSql)
    ];

    public Lr2PlayHistorySchemaCheckResult Check(string scoreDbPath, bool isLr2LinkedProfile)
    {
        if (!isLr2LinkedProfile)
        {
            return new Lr2PlayHistorySchemaCheckResult
            {
                Status = Lr2PlayHistorySchemaStatus.SkippedProfile,
                ScoreDbPath = scoreDbPath,
                Message = Resources.Lr2_play_history_schema_message_skipped_profile
            };
        }
        if (string.IsNullOrWhiteSpace(scoreDbPath))
        {
            return CreateUnreadableResult(scoreDbPath, Resources.Lr2_play_history_schema_message_score_db_path_not_configured);
        }
        if (!File.Exists(scoreDbPath))
        {
            return CreateUnreadableResult(scoreDbPath, Resources.Lr2_play_history_schema_message_score_db_file_not_found);
        }

        try
        {
            using LR2ScoreDBExtended db = new(scoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
            return CheckOpenConnection(db, scoreDbPath);
        }
        catch (Exception ex)
        {
            return CreateUnreadableResult(scoreDbPath, ex.Message);
        }
    }

    public Lr2PlayHistorySchemaCheckResult InstallOrRepair(string scoreDbPath, bool isLr2LinkedProfile)
    {
        Lr2PlayHistorySchemaCheckResult before = Check(scoreDbPath, isLr2LinkedProfile);
        if (before.Status is Lr2PlayHistorySchemaStatus.SkippedProfile
            or Lr2PlayHistorySchemaStatus.Unreadable
            or Lr2PlayHistorySchemaStatus.ManualRepairRequired
            or Lr2PlayHistorySchemaStatus.Installed)
        {
            return before;
        }

        try
        {
            using LR2ScoreDBExtended db = new(scoreDbPath);
            ExecuteInTransaction(db, delegate
            {
                if (before.Status == Lr2PlayHistorySchemaStatus.NotInstalled)
                {
                    foreach (string sql in InstallSqlStatements)
                    {
                        db.Execute(sql);
                    }
                }
                else
                {
                    foreach (string indexName in before.MissingIndexes.Concat(before.MismatchedIndexes).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        db.Execute("DROP INDEX IF EXISTS " + indexName + ";");
                        db.Execute(ExpectedIndexSqlByName[indexName]);
                    }
                    foreach (string triggerName in before.MissingTriggers.Concat(before.MismatchedTriggers).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        db.Execute("DROP TRIGGER IF EXISTS " + triggerName + ";");
                        db.Execute(ExpectedTriggerSqlByName[triggerName]);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            return CreateUnreadableResult(
                scoreDbPath,
                string.Format(Resources.Lr2_play_history_schema_message_install_or_repair_failed_format, ex.Message));
        }

        return Check(scoreDbPath, isLr2LinkedProfile);
    }

    /// <summary>
    /// LR2 score DB に追加した play history schema を指定された範囲で削除します。
    /// </summary>
    /// <param name="scoreDbPath">対象の player score DB path。</param>
    /// <param name="isLr2LinkedProfile">LR2 連携プロファイルとして操作してよい場合は <c>true</c>。</param>
    /// <param name="mode">trigger のみ削除するか、履歴 table も削除するか。</param>
    /// <returns>削除後に再確認した schema 状態。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> が定義済みの削除範囲ではない場合。</exception>
    public Lr2PlayHistorySchemaCheckResult Uninstall(string scoreDbPath, bool isLr2LinkedProfile, Lr2PlayHistorySchemaUninstallMode mode)
    {
        if (mode is not Lr2PlayHistorySchemaUninstallMode.TriggersOnly and not Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown LR2 play history schema uninstall mode.");
        }

        Lr2PlayHistorySchemaCheckResult before = Check(scoreDbPath, isLr2LinkedProfile);
        if (before.Status is Lr2PlayHistorySchemaStatus.SkippedProfile
            or Lr2PlayHistorySchemaStatus.Unreadable
            or Lr2PlayHistorySchemaStatus.NotInstalled)
        {
            return before;
        }

        try
        {
            using LR2ScoreDBExtended db = new(scoreDbPath);
            ExecuteInTransaction(db, delegate
            {
                DropExpectedTriggers(db);
                if (mode == Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers)
                {
                    DropExpectedIndexesAndTables(db);
                }
            });
        }
        catch (Exception ex)
        {
            return CreateUnreadableResult(
                scoreDbPath,
                string.Format(Resources.Lr2_play_history_schema_message_uninstall_failed_format, ex.Message));
        }

        return Check(scoreDbPath, isLr2LinkedProfile);
    }

    private static void DropExpectedTriggers(SQLiteConnection db)
    {
        Dictionary<string, string> objectTypesByName = LoadObjectTypes(db);
        foreach (string triggerName in ExpectedTriggerNames)
        {
            if (HasExpectedObjectType(objectTypesByName, triggerName, "trigger"))
            {
                db.Execute("DROP TRIGGER IF EXISTS " + triggerName + ";");
            }
        }
    }

    private static void DropExpectedIndexesAndTables(SQLiteConnection db)
    {
        Dictionary<string, string> objectTypesByName = LoadObjectTypes(db);
        foreach (string indexName in ExpectedIndexNames)
        {
            if (HasExpectedObjectType(objectTypesByName, indexName, "index"))
            {
                db.Execute("DROP INDEX IF EXISTS " + indexName + ";");
            }
        }
        foreach (string tableName in ExpectedTableNames)
        {
            if (HasExpectedObjectType(objectTypesByName, tableName, "table"))
            {
                db.Execute("DROP TABLE IF EXISTS " + tableName + ";");
            }
        }
    }

    private static bool HasExpectedObjectType(Dictionary<string, string> objectTypesByName, string objectName, string expectedType)
    {
        return objectTypesByName.TryGetValue(objectName, out string actualType)
            && string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase);
    }

    private static Lr2PlayHistorySchemaCheckResult CheckOpenConnection(LR2ScoreDBExtended db, string scoreDbPath)
    {
        var result = new Lr2PlayHistorySchemaCheckResult
        {
            ScoreDbPath = scoreDbPath
        };

        Dictionary<string, string> objectTypesByName = LoadObjectTypes(db);
        var existingTables = LoadObjectNames(db, "table");
        CheckRequiredTables(db, existingTables, RequiredBaseTables, result, isBaseSchema: true);
        if (result.MissingBaseTables.Count > 0
            || result.MissingBaseColumns.Count > 0
            || result.IncompatibleBaseColumns.Count > 0)
        {
            result.Status = Lr2PlayHistorySchemaStatus.ManualRepairRequired;
            result.Message = Resources.Lr2_play_history_schema_message_base_schema_incompatible;
            return result;
        }

        CheckReservedObjectNameCollisions(objectTypesByName, result);
        if (result.ObjectNameCollisions.Count > 0)
        {
            result.Status = Lr2PlayHistorySchemaStatus.ManualRepairRequired;
            result.Message = Resources.Lr2_play_history_schema_message_object_name_collision;
            return result;
        }

        bool hasAnyPlayHistoryObject = objectTypesByName.Keys.Any(name =>
            name.StartsWith("bms_lr2_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("idx_bms_lr2_", StringComparison.OrdinalIgnoreCase));

        CheckRequiredTables(db, existingTables, RequiredTables, result, isBaseSchema: false);

        if (result.MissingColumns.Count > 0 || result.IncompatibleColumns.Count > 0)
        {
            result.Status = Lr2PlayHistorySchemaStatus.ManualRepairRequired;
            result.Message = Resources.Lr2_play_history_schema_message_table_columns_incompatible;
            return result;
        }

        if (result.MissingTables.Count > 0)
        {
            result.Status = hasAnyPlayHistoryObject
                ? Lr2PlayHistorySchemaStatus.ManualRepairRequired
                : Lr2PlayHistorySchemaStatus.NotInstalled;
            result.Message = hasAnyPlayHistoryObject
                ? Resources.Lr2_play_history_schema_message_partial_schema_manual_action_required
                : Resources.Lr2_play_history_schema_message_not_installed;
            return result;
        }

        Dictionary<string, string> indexes = LoadObjectSql(db, "index");
        foreach (KeyValuePair<string, string> expected in ExpectedIndexSqlByName)
        {
            if (!indexes.TryGetValue(expected.Key, out string actualSql))
            {
                result.MissingIndexes.Add(expected.Key);
                continue;
            }
            if (!string.Equals(NormalizeSql(actualSql), NormalizeSql(expected.Value), StringComparison.Ordinal))
            {
                result.MismatchedIndexes.Add(expected.Key);
            }
        }

        Dictionary<string, string> triggers = LoadObjectSql(db, "trigger");
        foreach (KeyValuePair<string, string> expected in ExpectedTriggerSqlByName)
        {
            if (!triggers.TryGetValue(expected.Key, out string actualSql))
            {
                result.MissingTriggers.Add(expected.Key);
                continue;
            }
            if (!string.Equals(NormalizeSql(actualSql), NormalizeSql(expected.Value), StringComparison.Ordinal))
            {
                result.MismatchedTriggers.Add(expected.Key);
            }
        }

        result.Status = result.MissingIndexes.Count > 0
            || result.MismatchedIndexes.Count > 0
            || result.MissingTriggers.Count > 0
            || result.MismatchedTriggers.Count > 0
                ? Lr2PlayHistorySchemaStatus.Repairable
                : Lr2PlayHistorySchemaStatus.Installed;
        result.Message = result.Status == Lr2PlayHistorySchemaStatus.Installed
            ? Resources.Lr2_play_history_schema_message_installed
            : Resources.Lr2_play_history_schema_message_install_or_repair_available;
        return result;
    }

    private static Lr2PlayHistorySchemaCheckResult CreateUnreadableResult(string scoreDbPath, string message)
    {
        return new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.Unreadable,
            ScoreDbPath = scoreDbPath,
            Message = message
        };
    }

    private static void CheckRequiredTables(
        SQLiteConnection db,
        HashSet<string> existingTables,
        IReadOnlyDictionary<string, RequiredColumn[]> requiredTables,
        Lr2PlayHistorySchemaCheckResult result,
        bool isBaseSchema)
    {
        foreach (KeyValuePair<string, RequiredColumn[]> requiredTable in requiredTables)
        {
            if (!existingTables.Contains(requiredTable.Key))
            {
                if (isBaseSchema)
                {
                    result.MissingBaseTables.Add(requiredTable.Key);
                }
                else
                {
                    result.MissingTables.Add(requiredTable.Key);
                }
                continue;
            }

            Dictionary<string, TableColumnInfo> existingColumns = LoadTableColumns(db, requiredTable.Key);
            foreach (RequiredColumn requiredColumn in requiredTable.Value)
            {
                string columnDisplayName = requiredTable.Key + "." + requiredColumn.Name;
                if (!existingColumns.TryGetValue(requiredColumn.Name, out TableColumnInfo existingColumn))
                {
                    if (isBaseSchema)
                    {
                        result.MissingBaseColumns.Add(columnDisplayName);
                    }
                    else
                    {
                        result.MissingColumns.Add(columnDisplayName);
                    }
                    continue;
                }
                if (!IsCompatibleColumn(existingColumn, requiredColumn))
                {
                    if (isBaseSchema)
                    {
                        result.IncompatibleBaseColumns.Add(columnDisplayName);
                    }
                    else
                    {
                        result.IncompatibleColumns.Add(columnDisplayName);
                    }
                }
            }
        }
    }

    private static HashSet<string> LoadObjectNames(SQLiteConnection db, string type)
    {
        return new HashSet<string>(db.Query<SqliteMasterRow>("SELECT name FROM sqlite_master WHERE type = ?;", type)
            .Select(row => row.name)
            .Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> LoadObjectSql(SQLiteConnection db, string type)
    {
        return db.Query<SqliteMasterRow>("SELECT name, sql FROM sqlite_master WHERE type = ?;", type)
            .Where(row => !string.IsNullOrWhiteSpace(row.name))
            .ToDictionary(row => row.name, row => row.sql ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> LoadObjectTypes(SQLiteConnection db)
    {
        return db.Query<SqliteMasterRow>("SELECT name, type FROM sqlite_master;")
            .Where(row => !string.IsNullOrWhiteSpace(row.name))
            .ToDictionary(row => row.name, row => row.type ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, TableColumnInfo> LoadTableColumns(SQLiteConnection db, string tableName)
    {
        return db.Query<TableColumnInfo>("PRAGMA table_info(" + tableName + ");")
            .Where(column => !string.IsNullOrWhiteSpace(column.name))
            .ToDictionary(column => column.name, StringComparer.OrdinalIgnoreCase);
    }

    private static void CheckReservedObjectNameCollisions(Dictionary<string, string> objectTypesByName, Lr2PlayHistorySchemaCheckResult result)
    {
        foreach (string tableName in RequiredTables.Keys)
        {
            AddObjectNameCollisionIfTypeDiffers(objectTypesByName, tableName, "table", result);
        }
        foreach (string indexName in ExpectedIndexSqlByName.Keys)
        {
            AddObjectNameCollisionIfTypeDiffers(objectTypesByName, indexName, "index", result);
        }
        foreach (string triggerName in ExpectedTriggerSqlByName.Keys)
        {
            AddObjectNameCollisionIfTypeDiffers(objectTypesByName, triggerName, "trigger", result);
        }
    }

    private static void AddObjectNameCollisionIfTypeDiffers(
        Dictionary<string, string> objectTypesByName,
        string objectName,
        string expectedType,
        Lr2PlayHistorySchemaCheckResult result)
    {
        if (objectTypesByName.TryGetValue(objectName, out string actualType)
            && !string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            result.ObjectNameCollisions.Add(objectName + ":" + actualType);
        }
    }

    private static bool IsCompatibleColumn(TableColumnInfo existingColumn, RequiredColumn requiredColumn)
    {
        return IsCompatibleType(existingColumn.Type, requiredColumn.Type)
            && (!requiredColumn.NotNull || existingColumn.NotNull)
            && (!requiredColumn.PrimaryKey || existingColumn.PrimaryKey > 0)
            && (requiredColumn.DefaultValue == null || IsSameDefaultValue(existingColumn.DefaultValue, requiredColumn.DefaultValue));
    }

    private static bool IsCompatibleType(string actual, string expected)
    {
        string normalizedActual = (actual ?? string.Empty).Trim().ToUpperInvariant();
        string normalizedExpected = (expected ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedActual == normalizedExpected)
        {
            return true;
        }
        if (normalizedExpected == "TEXT")
        {
            return normalizedActual.Contains("CHAR")
                || normalizedActual.Contains("CLOB")
                || normalizedActual.Contains("TEXT");
        }
        if (normalizedExpected == "INTEGER")
        {
            return normalizedActual.Contains("INT")
                || normalizedActual == "BOOLEAN"
                || normalizedActual == "BOOL";
        }
        return false;
    }

    private static bool IsSameDefaultValue(string actual, string expected)
    {
        return string.Equals(NormalizeDefaultValue(actual), NormalizeDefaultValue(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDefaultValue(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        while (normalized.Length >= 2 && normalized[0] == '(' && normalized[normalized.Length - 1] == ')')
        {
            normalized = normalized.Substring(1, normalized.Length - 2).Trim();
        }
        if (normalized.Length >= 2 && normalized[0] == '\'' && normalized[normalized.Length - 1] == '\'')
        {
            normalized = normalized.Substring(1, normalized.Length - 2);
        }
        return normalized;
    }

    private static string NormalizeSql(string sql)
    {
        string normalized = WhitespaceRegex.Replace((sql ?? string.Empty).Trim(), " ");
        normalized = ReplaceIgnoreCase(normalized, "CREATE TRIGGER IF NOT EXISTS ", "CREATE TRIGGER ");
        return ReplaceIgnoreCase(normalized, "CREATE INDEX IF NOT EXISTS ", "CREATE INDEX ")
            .TrimEnd(';');
    }

    private static string CreateTriggerIfNotExists(string createTriggerSql)
    {
        return ReplaceIgnoreCase(createTriggerSql, "CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ");
    }

    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
    {
        return Regex.Replace(value ?? string.Empty, Regex.Escape(oldValue), _ => newValue ?? string.Empty, RegexOptions.IgnoreCase);
    }

    private static void ExecuteInTransaction(SQLiteConnection db, Action action)
    {
        db.Execute("BEGIN TRANSACTION;");
        try
        {
            action();
            db.Execute("COMMIT;");
        }
        catch
        {
            try
            {
                db.Execute("ROLLBACK;");
            }
            catch
            {
            }
            throw;
        }
    }

    private sealed class RequiredColumn(string name, string type, bool notNull, bool primaryKey = false, string defaultValue = null)
    {
        public string Name { get; } = name;

        public string Type { get; } = type;

        public bool NotNull { get; } = notNull;

        public bool PrimaryKey { get; } = primaryKey;

        public string DefaultValue { get; } = defaultValue;
    }

    private sealed class SqliteMasterRow
    {
        public string type { get; set; }

        public string name { get; set; }

        public string sql { get; set; }
    }

    private sealed class TableColumnInfo
    {
        public string name { get; set; }

        public string type { get; set; }

        public int notnull { get; set; }

        public string dflt_value { get; set; }

        public int pk { get; set; }

        public bool NotNull => notnull != 0;

        public string Type => type ?? string.Empty;

        public string DefaultValue => dflt_value ?? string.Empty;

        public int PrimaryKey => pk;
    }

    private const string CreateLastPlayTableSql =
        @"CREATE TABLE IF NOT EXISTS bms_lr2_last_play (
  hash TEXT PRIMARY KEY,
  last_play_at INTEGER NOT NULL
);";

    private const string CreatePlayHistoryTableSql =
        @"CREATE TABLE IF NOT EXISTS bms_lr2_play_history (
  history_id INTEGER PRIMARY KEY AUTOINCREMENT,

  hash TEXT NOT NULL,
  played_at INTEGER NOT NULL,
  finalized INTEGER NOT NULL DEFAULT 0,

  score_write_type TEXT NOT NULL,

  old_playcount INTEGER,
  new_playcount INTEGER NOT NULL,
  playcount_delta INTEGER NOT NULL,

  old_clearcount INTEGER,
  new_clearcount INTEGER,
  clearcount_delta INTEGER,

  old_failcount INTEGER,
  new_failcount INTEGER,
  failcount_delta INTEGER,

  old_clear INTEGER,
  new_clear INTEGER,

  old_clear_db INTEGER,
  new_clear_db INTEGER,
  old_clear_sd INTEGER,
  new_clear_sd INTEGER,
  old_clear_ex INTEGER,
  new_clear_ex INTEGER,

  old_minbp INTEGER,
  new_minbp INTEGER,

  old_exscore INTEGER,
  new_exscore INTEGER,

  old_maxcombo INTEGER,
  new_maxcombo INTEGER,

  old_totalnotes INTEGER,
  new_totalnotes INTEGER,

  old_complete INTEGER,
  new_complete INTEGER,

  old_op_best INTEGER,
  new_op_best INTEGER,
  old_op_history INTEGER,
  new_op_history INTEGER,

  old_rseed INTEGER,
  new_rseed INTEGER,

  old_scorehash TEXT,
  new_scorehash TEXT,

  old_player_playcount INTEGER,
  new_player_playcount INTEGER,
  player_playcount_delta INTEGER,

  old_playtime_total INTEGER,
  new_playtime_total INTEGER,
  playtime_delta INTEGER,

  old_judge_total INTEGER,
  new_judge_total INTEGER,
  judge_delta INTEGER,

  old_player_perfect INTEGER,
  new_player_perfect INTEGER,
  perfect_delta INTEGER,

  old_player_great INTEGER,
  new_player_great INTEGER,
  great_delta INTEGER,

  old_player_good INTEGER,
  new_player_good INTEGER,
  good_delta INTEGER,

  old_player_bad INTEGER,
  new_player_bad INTEGER,
  bad_delta INTEGER,

  old_player_poor INTEGER,
  new_player_poor INTEGER,
  poor_delta INTEGER,

  old_player_maxcombo INTEGER,
  new_player_maxcombo INTEGER
);";

    private const string CreatePlayHistoryHashTimeIndexSql =
        @"CREATE INDEX IF NOT EXISTS idx_bms_lr2_play_history_hash_time
  ON bms_lr2_play_history(hash, played_at DESC, history_id DESC);";

    private const string CreatePlayHistoryTimeIndexSql =
        @"CREATE INDEX IF NOT EXISTS idx_bms_lr2_play_history_time
  ON bms_lr2_play_history(played_at DESC, history_id DESC);";

    private const string CreatePlayPendingTableSql =
        @"CREATE TABLE IF NOT EXISTS bms_lr2_play_pending (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  history_id INTEGER NOT NULL,
  hash TEXT NOT NULL,
  created_at INTEGER NOT NULL
);";

    private const string CreateScoreInsertTriggerSql =
        @"CREATE TRIGGER bms_lr2_score_history_after_insert
AFTER INSERT ON score
FOR EACH ROW
WHEN NEW.playcount > 0
BEGIN
  INSERT INTO bms_lr2_play_history (
    hash,
    played_at,
    score_write_type,
    old_playcount,
    new_playcount,
    playcount_delta,
    old_clearcount,
    new_clearcount,
    clearcount_delta,
    old_failcount,
    new_failcount,
    failcount_delta,
    old_clear,
    new_clear,
    old_clear_db,
    new_clear_db,
    old_clear_sd,
    new_clear_sd,
    old_clear_ex,
    new_clear_ex,
    old_minbp,
    new_minbp,
    old_exscore,
    new_exscore,
    old_maxcombo,
    new_maxcombo,
    old_totalnotes,
    new_totalnotes,
    old_complete,
    new_complete,
    old_op_best,
    new_op_best,
    old_op_history,
    new_op_history,
    old_rseed,
    new_rseed,
    old_scorehash,
    new_scorehash
  )
  VALUES (
    NEW.hash,
    CAST(strftime('%s', 'now') AS INTEGER),
    'insert',
    NULL,
    NEW.playcount,
    NEW.playcount,
    NULL,
    NEW.clearcount,
    NEW.clearcount,
    NULL,
    NEW.failcount,
    NEW.failcount,
    NULL,
    NEW.clear,
    NULL,
    NEW.clear_db,
    NULL,
    NEW.clear_sd,
    NULL,
    NEW.clear_ex,
    NULL,
    NEW.minbp,
    NULL,
    NEW.perfect * 2 + NEW.great,
    NULL,
    NEW.maxcombo,
    NULL,
    NEW.totalnotes,
    NULL,
    NEW.complete,
    NULL,
    NEW.op_best,
    NULL,
    NEW.op_history,
    NULL,
    NEW.rseed,
    NULL,
    NEW.scorehash
  );

  DELETE FROM bms_lr2_play_pending WHERE id = 1;

  INSERT INTO bms_lr2_play_pending(id, history_id, hash, created_at)
  VALUES (1, last_insert_rowid(), NEW.hash, CAST(strftime('%s', 'now') AS INTEGER));

  INSERT OR REPLACE INTO bms_lr2_last_play(hash, last_play_at)
  VALUES (
    NEW.hash,
    (SELECT played_at FROM bms_lr2_play_history WHERE history_id = (SELECT history_id FROM bms_lr2_play_pending WHERE id = 1))
  );
END;";

    private const string CreateScoreUpdateTriggerSql =
        @"CREATE TRIGGER bms_lr2_score_history_after_update_playcount
AFTER UPDATE OF playcount ON score
FOR EACH ROW
WHEN NEW.playcount > OLD.playcount
BEGIN
  INSERT INTO bms_lr2_play_history (
    hash,
    played_at,
    score_write_type,
    old_playcount,
    new_playcount,
    playcount_delta,
    old_clearcount,
    new_clearcount,
    clearcount_delta,
    old_failcount,
    new_failcount,
    failcount_delta,
    old_clear,
    new_clear,
    old_clear_db,
    new_clear_db,
    old_clear_sd,
    new_clear_sd,
    old_clear_ex,
    new_clear_ex,
    old_minbp,
    new_minbp,
    old_exscore,
    new_exscore,
    old_maxcombo,
    new_maxcombo,
    old_totalnotes,
    new_totalnotes,
    old_complete,
    new_complete,
    old_op_best,
    new_op_best,
    old_op_history,
    new_op_history,
    old_rseed,
    new_rseed,
    old_scorehash,
    new_scorehash
  )
  VALUES (
    NEW.hash,
    CAST(strftime('%s', 'now') AS INTEGER),
    'update',
    OLD.playcount,
    NEW.playcount,
    NEW.playcount - OLD.playcount,
    OLD.clearcount,
    NEW.clearcount,
    NEW.clearcount - OLD.clearcount,
    OLD.failcount,
    NEW.failcount,
    NEW.failcount - OLD.failcount,
    OLD.clear,
    NEW.clear,
    OLD.clear_db,
    NEW.clear_db,
    OLD.clear_sd,
    NEW.clear_sd,
    OLD.clear_ex,
    NEW.clear_ex,
    OLD.minbp,
    NEW.minbp,
    OLD.perfect * 2 + OLD.great,
    NEW.perfect * 2 + NEW.great,
    OLD.maxcombo,
    NEW.maxcombo,
    OLD.totalnotes,
    NEW.totalnotes,
    OLD.complete,
    NEW.complete,
    OLD.op_best,
    NEW.op_best,
    OLD.op_history,
    NEW.op_history,
    OLD.rseed,
    NEW.rseed,
    OLD.scorehash,
    NEW.scorehash
  );

  DELETE FROM bms_lr2_play_pending WHERE id = 1;

  INSERT INTO bms_lr2_play_pending(id, history_id, hash, created_at)
  VALUES (1, last_insert_rowid(), NEW.hash, CAST(strftime('%s', 'now') AS INTEGER));

  INSERT OR REPLACE INTO bms_lr2_last_play(hash, last_play_at)
  VALUES (
    NEW.hash,
    (SELECT played_at FROM bms_lr2_play_history WHERE history_id = (SELECT history_id FROM bms_lr2_play_pending WHERE id = 1))
  );
END;";

    private const string CreatePlayerUpdateTriggerSql =
        @"CREATE TRIGGER bms_lr2_player_history_after_update
AFTER UPDATE OF playcount, clear, fail, perfect, great, good, bad, poor, playtime, maxcombo ON player
FOR EACH ROW
WHEN
  NEW.playcount > OLD.playcount
  AND EXISTS (
    SELECT 1
    FROM bms_lr2_play_pending
    WHERE id = 1
      AND created_at >= CAST(strftime('%s', 'now') AS INTEGER)
        - 10
  )
BEGIN
  UPDATE bms_lr2_play_history
  SET
    finalized = 1,
    old_player_playcount = OLD.playcount,
    new_player_playcount = NEW.playcount,
    player_playcount_delta = NEW.playcount - OLD.playcount,
    old_playtime_total = OLD.playtime,
    new_playtime_total = NEW.playtime,
    playtime_delta = NEW.playtime - OLD.playtime,
    old_judge_total = OLD.perfect + OLD.great + OLD.good + OLD.bad + OLD.poor,
    new_judge_total = NEW.perfect + NEW.great + NEW.good + NEW.bad + NEW.poor,
    judge_delta = (NEW.perfect + NEW.great + NEW.good + NEW.bad + NEW.poor)
                - (OLD.perfect + OLD.great + OLD.good + OLD.bad + OLD.poor),
    old_player_perfect = OLD.perfect,
    new_player_perfect = NEW.perfect,
    perfect_delta = NEW.perfect - OLD.perfect,
    old_player_great = OLD.great,
    new_player_great = NEW.great,
    great_delta = NEW.great - OLD.great,
    old_player_good = OLD.good,
    new_player_good = NEW.good,
    good_delta = NEW.good - OLD.good,
    old_player_bad = OLD.bad,
    new_player_bad = NEW.bad,
    bad_delta = NEW.bad - OLD.bad,
    old_player_poor = OLD.poor,
    new_player_poor = NEW.poor,
    poor_delta = NEW.poor - OLD.poor,
    old_player_maxcombo = OLD.maxcombo,
    new_player_maxcombo = NEW.maxcombo
  WHERE history_id = (SELECT history_id FROM bms_lr2_play_pending WHERE id = 1);

  DELETE FROM bms_lr2_play_pending WHERE id = 1;
END;";

    private const string CreatePlayerCleanupTriggerSql =
        @"CREATE TRIGGER bms_lr2_player_history_cleanup_stale_pending
AFTER UPDATE OF playcount, clear, fail, perfect, great, good, bad, poor, playtime, maxcombo ON player
FOR EACH ROW
WHEN
  NEW.playcount > OLD.playcount
  AND EXISTS (
    SELECT 1
    FROM bms_lr2_play_pending
    WHERE id = 1
      AND created_at < CAST(strftime('%s', 'now') AS INTEGER)
        - 10
  )
BEGIN
  DELETE FROM bms_lr2_play_pending WHERE id = 1;
END;";
}
